using Basis.Network.Core;
using BasisNetworkCore;
using BasisNetworkCore.Security;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using BasisNetworking.InitialData;
using BasisNetworking.InitialData;
using BasisServerHandle;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;

namespace BasisNetworkServer.Security
{
    public static class BasisPlayerModeration
    {
        private static readonly ConcurrentDictionary<string, BannedPlayer> BannedPlayers = new();
        private static readonly ConcurrentDictionary<string, byte> BannedUUIDs = new();
        private static readonly string BanFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "banned_players.xml");

        public static bool UseFileOnDisc = true;

        public class BannedPlayer
        {
            public string UUID { get; set; }
            public string BannedIp { get; set; }
            public string Reason { get; set; }
            public bool HasBannedIp { get; set; }
            public string TimeOfBan { get; set; }
        }

        // =========================
        // core ban logic
        // =========================

        public static string Ban(string UUID, string reason)
        {
            if (!ValidateTarget(UUID, reason, out var peer, out var error))
                return error;

            if (IsProtected(UUID))
                return "Target is protected";

            peer.Disconnect(Encoding.UTF8.GetBytes(reason));

            BannedPlayer bannedPlayer = new()
            {
                UUID = UUID,
                Reason = reason,
                HasBannedIp = false,
                TimeOfBan = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                BannedIp = string.Empty
            };

            BannedPlayers[UUID] = bannedPlayer;
            BannedUUIDs[UUID] = 0;
            SaveBannedPlayers();

            return $"Player {UUID} banned.";
        }

        public static string IpBan(string UUID, string reason)
        {
            if (!ValidateTarget(UUID, reason, out var peer, out var error))
                return error;

            if (IsProtected(UUID))
                return "Target is protected";

            string ip = peer.Address.ToString();
            peer.Disconnect(Encoding.UTF8.GetBytes(reason));

            BannedPlayer bannedPlayer = new()
            {
                UUID = UUID,
                BannedIp = ip,
                Reason = reason,
                HasBannedIp = true,
                TimeOfBan = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            };

            BannedPlayers[UUID] = bannedPlayer;
            BannedUUIDs[UUID] = 0;
            SaveBannedPlayers();

            return $"Player {UUID} and IP {ip} banned.";
        }

        public static string Kick(string UUID, string reason)
        {
            if (!ValidateTarget(UUID, reason, out var peer, out var error))
                return error;

            if (IsProtected(UUID))
                return "Target is protected";

            peer.Disconnect(Encoding.UTF8.GetBytes(reason));
            return $"Player {UUID} kicked.";
        }

        private static bool ValidateTarget(string UUID, string reason, out NetPeer peer, out string error)
        {
            peer = null;
            error = "";

            if (string.IsNullOrEmpty(UUID))
            {
                error = "UUID invalid";
                return false;
            }

            if (string.IsNullOrEmpty(reason))
            {
                error = "Reason invalid";
                return false;
            }

            if (!NetworkServer.AuthIdentity.UUIDToNetID(UUID, out int id) ||
                !NetworkServer.AuthenticatedPeers.TryGetValue(id, out peer))
            {
                error = "Player not found";
                return false;
            }

            return true;
        }

        private static bool IsProtected(string uuid)
        {
            return PermissionIntegration.Manager.Has(uuid, PermNodes.protection);
        }

        // =========================
        // ban storage
        // =========================

        public static void SaveBannedPlayers()
        {
            if (!UseFileOnDisc) return;

            try
            {
                using FileStream fs = new(BanFilePath, FileMode.Create);
                new XmlSerializer(typeof(List<BannedPlayer>)).Serialize(fs, BannedPlayers.Values.ToList());
            }
            catch (Exception ex)
            {
                BNL.LogError($"Save banned failed: {ex.Message}");
            }
        }

        public static void LoadBannedPlayers()
        {
            if (!File.Exists(BanFilePath))
            {
                SaveBannedPlayers();
                return;
            }

            try
            {
                using FileStream fs = new(BanFilePath, FileMode.Open);
                var list = (List<BannedPlayer>)new XmlSerializer(typeof(List<BannedPlayer>)).Deserialize(fs);

                BannedPlayers.Clear();
                BannedUUIDs.Clear();

                foreach (var p in list)
                {
                    BannedPlayers[p.UUID] = p;
                    BannedUUIDs[p.UUID] = 0;
                }
            }
            catch (Exception ex)
            {
                BNL.LogError($"Load banned failed: {ex.Message}");
            }
        }

        public static bool IsBanned(string UUID) => BannedUUIDs.ContainsKey(UUID);

        public static bool Unban(string UUID)
        {
            if (!BannedUUIDs.ContainsKey(UUID))
                return false;

            BannedPlayers.TryRemove(UUID, out _);
            BannedUUIDs.TryRemove(UUID, out _);
            SaveBannedPlayers();
            return true;
        }

        public static bool UnbanIp(string ip)
        {
            var list = BannedPlayers.Values.Where(p => p.HasBannedIp && p.BannedIp == ip).ToList();
            if (!list.Any()) return false;

            foreach (var p in list)
            {
                BannedPlayers.TryRemove(p.UUID, out _);
                BannedUUIDs.TryRemove(p.UUID, out _);
            }

            SaveBannedPlayers();
            return true;
        }

        // =========================
        // admin entry point
        // =========================

        public static void OnAdminMessage(NetPeer peer, NetPacketReader reader)
        {
            if (!NetworkServer.AuthIdentity.NetIDToUUID(peer, out string UUID))
            {
                SendBackMessage(peer, "UUID not found");
                return;
            }

            AdminRequest req = new();
            req.Deserialize(reader);
            var mode = req.GetAdminRequestMode();

                // ===== 権限表示 =====
            if (mode == AdminRequestMode.GetPermissions)
            {
                if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.PermissionsView))
                {
                    SendBackMessage(peer, "No permission: view");
                    return;
                }

                HandleGetPermissions(peer);
                return;
            }

            switch (mode)
            {
                case AdminRequestMode.Ban:
                    Require(peer, PermNodes.ModerationBan, () =>
                        SendBackMessage(peer, Ban(reader.GetString(), reader.GetString())));
                    break;

                case AdminRequestMode.Kick:
                    Require(peer, PermNodes.ModerationKick, () =>
                        SendBackMessage(peer, Kick(reader.GetString(), reader.GetString())));
                    break;

                case AdminRequestMode.IpAndBan:
                    Require(peer, PermNodes.ModerationIpBan, () =>
                        SendBackMessage(peer, IpBan(reader.GetString(), reader.GetString())));
                    break;

                case AdminRequestMode.UnBan:
                    Require(peer, PermNodes.ModerationUnban, () =>
                        SendBackMessage(peer, Unban(reader.GetString()) ? "Unbanned" : "Failed"));
                    break;

                case AdminRequestMode.UnBanIP:
                    Require(peer, PermNodes.ModerationUnbanIp, () =>
                        SendBackMessage(peer, UnbanIp(reader.GetString()) ? "Unbanned" : "Failed"));
                    break;

                case AdminRequestMode.Message:
                    Require(peer, PermNodes.ModerationMessage, () =>
                    {
                        ushort id = reader.GetUShort();
                        if (NetworkServer.AuthenticatedPeers.TryGetValue(id, out var target))
                            SendBackMessage(target, reader.GetString());
                    });
                    break;

                case AdminRequestMode.MessageAll:
                    Require(peer, PermNodes.ModerationMessageAll, () =>
                    {
                        var writer = NetworkServer.RentWriter();
                        new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
                        writer.Put(reader.GetString());
                        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                        NetworkServer.ReturnWriter(writer);
                    });
                    break;

                case AdminRequestMode.TeleportAll:
                    Require(peer, PermNodes.ModerationTeleport, () =>
                    {
                        var writer = NetworkServer.RentWriter();
                        new AdminRequest().Serialize(writer, mode);
                        writer.Put(reader.GetUShort());
                        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                        NetworkServer.ReturnWriter(writer);
                    });
                    break;

                case AdminRequestMode.TeleportPlayer:
                    Require(peer, PermNodes.ModerationTeleport, () =>
                    {
                        ushort targetId = reader.GetUShort();
                        if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out var targetPeer))
                            return;

                        var writer = NetworkServer.RentWriter();
                        new AdminRequest().Serialize(writer, mode);
                        writer.Put((ushort)peer.Id);
                        NetworkServer.TrySend(targetPeer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
                        NetworkServer.ReturnWriter(writer);
                    });
                    break;

                case AdminRequestMode.EnableShoutMode:
                case AdminRequestMode.DisableShoutMode:
                    Require(peer, PermNodes.ModerationShout, () =>
                        HandleShoutMode(peer, reader, mode == AdminRequestMode.EnableShoutMode));
                    break;

                // ===== global lock =====
                case AdminRequestMode.GlobalToggleAvatars:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Avatar", BasisGlobalLockManager.ToggleAvatars()));
                    break;

                case AdminRequestMode.GlobalToggleProps:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Prop", BasisGlobalLockManager.ToggleProps()));
                    break;

                case AdminRequestMode.GlobalToggleWorlds:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "World", BasisGlobalLockManager.ToggleWorlds()));
                    break;

                case AdminRequestMode.GlobalToggleServers:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Server share", BasisGlobalLockManager.ToggleServers()));
                    break;

                case AdminRequestMode.GlobalToggleThirdPerson:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Third-person camera", BasisGlobalLockManager.ToggleThirdPerson()));
                    break;

                case AdminRequestMode.GlobalToggleAdditionalAvatarDataLock:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleGlobalToggle(peer, "Additional avatar data lock", BasisGlobalLockManager.ToggleAdditionalAvatarDataLock()));
                    break;

                case AdminRequestMode.SetGlobalCameraPolicy:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleCameraPolicySet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalHeadlessAudio:
                    Require(peer, PermNodes.ModerationHeadlessAudio, () =>
                        HandleHeadlessAudioSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalCrashReporting:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleCrashReportingSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalAudioRangeLimits:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleAudioRangeLimitsSet(peer, reader));
                    break;

                case AdminRequestMode.GlobalTogglePlayspaceMover:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandlePlayspaceMoverToggle(peer));
                    break;

                case AdminRequestMode.GlobalToggleDirectConnect:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleDirectConnectToggle(peer));
                    break;

                case AdminRequestMode.SetGlobalAvatarScaleLimits:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleAvatarScaleLimitsSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalResourceLimits:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleResourceLimitsSet(peer, reader));
                    break;

                case AdminRequestMode.RequestAllLogs:
                    Require(peer, PermNodes.AdminLogs, () =>
                        BasisServerLogBundleService.SendAllLogsToPeer(peer));
                    break;

                case AdminRequestMode.DeleteAllLogs:
                    Require(peer, PermNodes.AdminLogs, () =>
                        BasisServerLogBundleService.DeleteAllLogsForPeer(peer));
                    break;

                case AdminRequestMode.SetGlobalHeadlessDisallow:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleHeadlessDisallowSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalOpusPacketLoss:
                    Require(peer, PermNodes.ModerationGlobalLock, () =>
                        HandleOpusPacketLossSet(peer, reader));
                    break;

                case AdminRequestMode.SetUserOpusBitrate:
                    Require(peer, PermNodes.ModerationOpusBitrate, () =>
                        HandleUserOpusBitrateSet(peer, reader));
                    break;

                case AdminRequestMode.SetGlobalOpusFrameDuration:
                    Require(peer, PermNodes.ModerationOpusBitrate, () =>
                        HandleOpusFrameDurationSet(peer, reader));
                    break;

                // ===== 権限編集 =====
                case AdminRequestMode.SetUserGroup:
                case AdminRequestMode.SetUserNode:
                case AdminRequestMode.SetGroupNode:
                case AdminRequestMode.CreateGroup:
                case AdminRequestMode.DeleteGroup:
                case AdminRequestMode.SetGroupParent:
                    Require(peer, PermNodes.PermissionsEdit, () =>
                        HandlePermissionEdit(mode, peer, reader));
                    break;

                // ===== server config =====
                case AdminRequestMode.SetServerName:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        SendBackMessage(peer, ApplyServerName(reader.GetString())));
                    break;

                case AdminRequestMode.SetServerMotd:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        SendBackMessage(peer, ApplyServerMotd(reader.GetString())));
                    break;

                case AdminRequestMode.SetAllowlistMode:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                        SendBackMessage(peer, ApplyAllowlistMode(reader.GetByte())));
                    break;

                case AdminRequestMode.AddAllowlist:
                    Require(peer, PermNodes.ModerationAllowlist, () =>
                        SendBackMessage(peer, ApplyAllowlistAdd(reader.GetString())));
                    break;

                case AdminRequestMode.RemoveAllowlist:
                    Require(peer, PermNodes.ModerationAllowlist, () =>
                        SendBackMessage(peer, ApplyAllowlistRemove(reader.GetString())));
                    break;

                case AdminRequestMode.AddDefaultLibraryItem:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                    {
                        byte itemMode = reader.GetByte();
                        string itemUrl = reader.GetString();
                        string itemPassword = reader.GetString();
                        SendBackMessage(peer, ApplyAddDefaultLibraryItem(itemMode, itemUrl, itemPassword));
                    });
                    break;

                case AdminRequestMode.RemoveDefaultLibraryItem:
                    Require(peer, PermNodes.ConfigurationEditor, () =>
                    {
                        string removeUrl = reader.GetString();
                        SendBackMessage(peer, ApplyRemoveDefaultLibraryItem(removeUrl));
                    });
                    break;
            }

            reader.Recycle();
        }

        // =========================
        // server-config admin operations
        // =========================
        // 各 mutation は live Configuration field を更新する
        // (次回 info-query response、ServerMetaDataMessage、connection check で読まれる)。
        // その後、Configuration の現在 state を config/config.xml へ persist し、restart 後も change を維持する。
        // XML は小さく admin operation も稀なため、SaveConfig は意図的に calling thread 上で fire-and-forget にしている。

        private static string ApplyServerName(string newName)
        {
            if (newName == null) return "Name was null.";
            if (newName.Length > BasisNetworkCommons.ServerInfoNameMaxLength)
                newName = newName.Substring(0, BasisNetworkCommons.ServerInfoNameMaxLength);
            NetworkServer.Configuration.ServerName = newName;
            SaveConfig();
            return $"Server name set to '{newName}'.";
        }

        private static string ApplyServerMotd(string newMotd)
        {
            if (newMotd == null) newMotd = string.Empty;
            if (newMotd.Length > BasisNetworkCommons.ServerInfoMotdMaxLength)
                newMotd = newMotd.Substring(0, BasisNetworkCommons.ServerInfoMotdMaxLength);
            NetworkServer.Configuration.ServerMotd = newMotd;
            SaveConfig();
            return "Server MOTD updated.";
        }

        private static string ApplyAllowlistMode(byte mode)
        {
            BasisUserRestrictionMode parsed = (BasisUserRestrictionMode)mode;
            if (!Enum.IsDefined(typeof(BasisUserRestrictionMode), parsed))
                return $"Unknown restriction mode value {mode}.";
            NetworkServer.Configuration.BasisUserRestrictionMode = parsed;

            if (parsed == BasisUserRestrictionMode.RejoinOnly)
                BasisRejoinLockManager.CaptureCurrentPopulation();
            else
                BasisRejoinLockManager.Clear();

            SaveConfig();
            // restriction mode は lock-state payload に乗る。connected client が refresh するよう push する。
            BasisGlobalLockManager.BroadcastLockState();
            return $"Restriction mode set to {parsed}.";
        }

        private static string ApplyAllowlistAdd(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return "UUID was empty.";
            if (NetworkServer.AllowList == null) return "AllowList not initialized.";
            // fire-and-forget: BasisAllowList.AddToAllowlistAsync は 1 行 append するだけなので、
            // admin へ operation result を返しながら走らせておいて安全。
            _ = NetworkServer.AllowList.AddToAllowlistAsync(uuid);
            return $"Added {uuid} to allowlist.";
        }

        private static string ApplyAllowlistRemove(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return "UUID was empty.";
            if (NetworkServer.AllowList == null) return "AllowList not initialized.";
            _ = NetworkServer.AllowList.RemoveFromAllowlistAsync(uuid);
            return $"Removed {uuid} from allowlist.";
        }

        private static string ApplyAddDefaultLibraryItem(byte mode, string url, string password)
        {
            if (string.IsNullOrWhiteSpace(url)) return "URL was empty.";
            // Mode は client の BundledContentHolder.Mode: 0=Avatar、1=World、2=Prop。
            if (mode > 2) return $"Unknown library mode {mode} (expected 0=Avatar, 1=World, 2=Prop).";

            // `url#fragment` を defensive に split する。
            // admin が password を URL fragment に baked-in した copy-able share string を paste した場合、
            // password が URL field ではなく Password field に入るよう、ここで剥がす。
            // client UI は通常送信前にこれを split するが、この処理はその path を skip した admin や older client を拾う。
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                string fragment = url.Substring(hashIndex + 1);
                url = url.Substring(0, hashIndex);
                if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(fragment))
                {
                    try
                    {
                        password = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fragment));
                    }
                    catch
                    {
                        // fragment が valid base64 ではなかった。raw fragment bytes を保存せず、password は空にしておく。
                    }
                }
            }

            BasisDefaultLibraryConfiguration config = new BasisDefaultLibraryConfiguration
            {
                Mode = mode,
                Url = url,
                Password = password ?? string.Empty,
            };

            string written = BasisDefaultLibraryLoader.SaveItem(Configuration.DefaultLibraryFolderName, config);
            if (string.IsNullOrEmpty(written))
            {
                return "Failed to persist default library entry — see server log.";
            }

            // updated list を connected client 全員へ push し、新しい entry が次回 connect 時だけでなく
            // library にすぐ表示されるようにする。
            BasisNetworkServerLibrary.BroadcastLibraryToAll();
            return $"Default library entry added ({Path.GetFileName(written)}).";
        }

        private static string ApplyRemoveDefaultLibraryItem(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "URL was empty.";

            int removed = BasisDefaultLibraryLoader.RemoveItem(Configuration.DefaultLibraryFolderName, url);
            if (removed <= 0)
            {
                return $"No default library entry matched URL '{url}'.";
            }

            BasisNetworkServerLibrary.BroadcastLibraryToAll();
            return $"Removed {removed} default library entry(ies) for URL '{url}'.";
        }

        private static void SaveConfig()
        {
            try
            {
                NetworkServer.Configuration.SaveToXml(Configuration.GetDefaultPath());
            }
            catch (Exception e)
            {
                BNL.LogError($"Failed to persist server configuration: {e.Message}");
            }
        }

        // =========================
        // helper
        // =========================

        private static void Require(NetPeer peer, string perm, Action action)
        {
            if (!PermissionIntegration.HasValidRequirement(peer, perm))
            {
                SendBackMessage(peer, $"No permission: {perm}");
                return;
            }

            action();
        }

        private static void HandlePermissionEdit(AdminRequestMode mode, NetPeer peer, NetPacketReader reader)
        {
            switch (mode)
            {
                case AdminRequestMode.SetUserGroup:
                    PermissionIntegration.Manager.AddUserToGroup(reader.GetString(), reader.GetString());
                    break;

                case AdminRequestMode.SetUserNode:
                    PermissionIntegration.Manager.AddUserNode(reader.GetString(), reader.GetString());
                    break;

                case AdminRequestMode.SetGroupNode:
                    PermissionIntegration.Manager.AddGroupNode(reader.GetString(), reader.GetString());
                    break;

                case AdminRequestMode.CreateGroup:
                    PermissionIntegration.Manager.GetOrCreateGroup(reader.GetString());
                    break;

                case AdminRequestMode.DeleteGroup:
                    PermissionIntegration.Manager.DeleteGroup(reader.GetString());
                    break;

                case AdminRequestMode.SetGroupParent:
                    PermissionIntegration.Manager.AddGroupParent(reader.GetString(), reader.GetString());
                    break;
            }

            SendBackMessage(peer, "Permission updated");
        }

        private static void HandleGetPermissions(NetPeer peer)
        {
            var snap = PermissionIntegration.Manager.Snapshot();

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GetPermissions);

            writer.Put(snap.Groups.Count);
            foreach (var g in snap.Groups.Values)
            {
                writer.Put(g.Name);
                writer.Put(g.Nodes.Count);
                foreach (var n in g.Nodes)
                {
                    writer.Put(n);
                }

                writer.Put(g.Parents.Count);
                foreach (var p in g.Parents)
                {
                    writer.Put(p);
                }
            }

            writer.Put(snap.Users.Count);
            foreach (var u in snap.Users.Values)
            {
                writer.Put(u.Uuid);
                writer.Put(u.Groups.Count);
                foreach (var g in u.Groups)
                {
                    writer.Put(g);
                }

                writer.Put(u.Nodes.Count);
                foreach (var n in u.Nodes)
                {
                    writer.Put(n);
                }
            }

            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        private static void HandleShoutMode(NetPeer peer, NetPacketReader reader, bool enable)
        {
            ushort id = reader.GetUShort();
            Basis.Network.Server.Generic.BasisSavedState.SetShoutMode(id, enable);
            BasisServerHandle.BasisServerHandleEvents.BroadcastShoutModeState(id, enable, (ushort)peer.Id);
        }

        private static void HandleCrashReportingSet(NetPeer peer, NetPacketReader reader)
        {
            bool enabled = reader.GetBool();
            NetworkServer.Configuration.CrashReportingEnabled = enabled;
            SaveConfig();
            BasisCrashReportStateManager.SetEnabled(enabled);
            BasisCrashReportStateManager.BroadcastState();
            SendBackMessage(peer, $"Crash reporting {(enabled ? "ENABLED" : "DISABLED")}.");
        }

        private static void HandleAudioRangeLimitsSet(NetPeer peer, NetPacketReader reader)
        {
            float microphoneMeters = reader.GetFloat();
            float hearingMeters = reader.GetFloat();
            BasisAudioRangeLimitManager.SetLimits(microphoneMeters, hearingMeters);
            NetworkServer.Configuration.MaxMicrophoneRangeMeters = BasisAudioRangeLimitManager.MaxMicrophoneRangeMeters;
            NetworkServer.Configuration.MaxHearingRangeMeters = BasisAudioRangeLimitManager.MaxHearingRangeMeters;
            SaveConfig();
            BasisAudioRangeLimitManager.BroadcastState();
            SendBackMessage(peer, $"Audio range limits set: microphone {NetworkServer.Configuration.MaxMicrophoneRangeMeters} m, hearing {NetworkServer.Configuration.MaxHearingRangeMeters} m.");
        }

        private static void HandlePlayspaceMoverToggle(NetPeer peer)
        {
            bool locked = BasisGlobalLockManager.TogglePlayspaceMover();
            string state = locked ? "DISABLED" : "ENABLED";
            BroadcastGlobalLockNotice(peer,
                $"Playspace mover is now {state}.",
                $"The playspace mover has been globally {state} for non-admins by an admin.");
        }

        private static void HandleDirectConnectToggle(NetPeer peer)
        {
            bool locked = BasisGlobalLockManager.ToggleDirectConnect();
            string state = locked ? "DISABLED" : "ENABLED";
            BroadcastGlobalLockNotice(peer,
                $"Direct connections are now {state}.",
                $"Direct (peer-to-peer) connections have been globally {state} for non-admins by an admin.");
        }

        private static void HandleAvatarScaleLimitsSet(NetPeer peer, NetPacketReader reader)
        {
            float minMeters = reader.GetFloat();
            float maxMeters = reader.GetFloat();
            BasisAvatarScaleLimitManager.SetLimits(minMeters, maxMeters);
            NetworkServer.Configuration.MinAvatarEyeHeightMeters = BasisAvatarScaleLimitManager.MinMeters;
            NetworkServer.Configuration.MaxAvatarEyeHeightMeters = BasisAvatarScaleLimitManager.MaxMeters;
            SaveConfig();
            BasisAvatarScaleLimitManager.BroadcastState();
            SendBackMessage(peer, $"Avatar scale limits set: {NetworkServer.Configuration.MinAvatarEyeHeightMeters} m .. {NetworkServer.Configuration.MaxAvatarEyeHeightMeters} m.");
        }

        private static void HandleResourceLimitsSet(NetPeer peer, NetPacketReader reader)
        {
            int maxDatabaseEntries = reader.GetInt();
            int maxDatabaseNameLength = reader.GetInt();
            int maxDatabasePayloadEntries = reader.GetInt();
            int maxContentSpheresPerPlayer = reader.GetInt();
            BasisResourceLimitManager.SetLimits(maxDatabaseEntries, maxDatabaseNameLength, maxDatabasePayloadEntries, maxContentSpheresPerPlayer);
            NetworkServer.Configuration.MaxDatabaseEntries = BasisResourceLimitManager.MaxDatabaseEntries;
            NetworkServer.Configuration.MaxDatabaseNameLength = BasisResourceLimitManager.MaxDatabaseNameLength;
            NetworkServer.Configuration.MaxDatabasePayloadEntries = BasisResourceLimitManager.MaxDatabasePayloadEntries;
            NetworkServer.Configuration.MaxContentSpheresPerPlayer = BasisResourceLimitManager.MaxContentSpheresPerPlayer;
            SaveConfig();
            BasisResourceLimitManager.BroadcastState();
            SendBackMessage(peer, $"Resource limits set: db entries {BasisResourceLimitManager.MaxDatabaseEntries}, name length {BasisResourceLimitManager.MaxDatabaseNameLength}, payload entries {BasisResourceLimitManager.MaxDatabasePayloadEntries}, spheres/player {BasisResourceLimitManager.MaxContentSpheresPerPlayer}.");
        }

        /// <summary>
        /// toggle した admin へ reply し、全員へ 1 行 notice を broadcast してから refreshed lock-state payload を push する。
        /// state が GlobalGetLockState に乗る restriction toggle (playspace mover、direct connect) で使う。
        /// </summary>
        private static void BroadcastGlobalLockNotice(NetPeer peer, string adminReply, string broadcastNotice)
        {
            BNL.Log(broadcastNotice);
            SendBackMessage(peer, adminReply);

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
            writer.Put(broadcastNotice);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);

            BasisGlobalLockManager.BroadcastLockState();
        }

        private static void HandleGlobalToggle(NetPeer peer, string contentType, bool nowLocked)
        {
            string state = nowLocked ? "DISABLED" : "ENABLED";
            string notification = $"{contentType} loading has been globally {state} by an admin.";
            BNL.Log(notification);

            // toggle した admin に通知する。
            SendBackMessage(peer, $"{contentType} loading is now {state}.");

            // change を全 client へ通知する。
            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
            writer.Put(notification);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);

            // client が追跡できるよう updated lock state を broadcast する。
            BasisGlobalLockManager.BroadcastLockState();
        }

        private static void HandleHeadlessAudioSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set headless audio clip playback: missing state value.");
                return;
            }

            bool headlessAudioOff = reader.GetBool();
            bool changed = BasisHeadlessAudioStateManager.SetHeadlessAudio(headlessAudioOff);
            string state = headlessAudioOff ? "OFF" : "ON";
            string notification = changed
                ? $"Headless audio clip playback is now {state}."
                : $"Headless audio clip playback was already {state}.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
            BasisHeadlessAudioStateManager.BroadcastState();
        }

        private static void HandleHeadlessDisallowSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set headless connection policy: missing state value.");
                return;
            }

            bool disallowHeadless = reader.GetBool();
            bool changed = BasisHeadlessConnectionPolicyManager.SetDisallowHeadless(disallowHeadless);
            string state = disallowHeadless ? "DISALLOWED" : "ALLOWED";
            string notification = changed
                ? $"Headless clients are now {state}."
                : $"Headless clients were already {state}.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);

            if (disallowHeadless)
            {
                BasisHeadlessConnectionPolicyManager.DisconnectConnectedHeadlessPeers();
            }

            BasisHeadlessConnectionPolicyManager.BroadcastState();
        }

        private static void HandleOpusPacketLossSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set Opus packet loss: missing value byte.");
                return;
            }

            int percent = reader.GetByte();
            bool changed = BasisOpusPacketLossStateManager.SetPacketLossPercent(percent);
            int applied = BasisOpusPacketLossStateManager.PacketLossPercent;
            string notification = changed
                ? $"Opus FEC packet-loss % is now {applied}."
                : $"Opus FEC packet-loss % was already {applied}.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
            BasisOpusPacketLossStateManager.BroadcastState();
        }

        private static void HandleCameraPolicySet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set camera metadata policy: missing mask byte.");
                return;
            }

            byte mask = reader.GetByte();
            BasisGlobalLockManager.SetCameraMetadataDisallowMask(mask);
            BNL.Log($"Camera photo-metadata disallow mask set to {mask}.");
            SendBackMessage(peer, $"Camera metadata policy updated (mask {mask}).");
            BasisGlobalLockManager.BroadcastLockState();
        }

        private static void HandleUserOpusBitrateSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 6) // ushort + int
            {
                SendBackMessage(peer, "Failed to set user Opus bitrate: missing payload.");
                return;
            }

            ushort targetId = reader.GetUShort();
            int requested = reader.GetInt();

            int applied = BasisUserOpusBitrateStateManager.SetBitrate(targetId, requested);

            if (NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out var targetPeer))
            {
                BasisUserOpusBitrateStateManager.SendOverrideToPeer(targetPeer, applied);
            }

            string notification = applied == 0
                ? $"Cleared Opus bitrate override for player {targetId}."
                : $"Opus bitrate override for player {targetId} is now {applied} bps.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
        }

        private static void HandleOpusFrameDurationSet(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1)
            {
                SendBackMessage(peer, "Failed to set Opus frame duration: missing value byte.");
                return;
            }

            int requested = reader.GetByte();
            if (!BasisOpusFrameDurationStateManager.IsAcceptedDuration(requested))
            {
                SendBackMessage(peer, $"Failed to set Opus frame duration: only 20 or 40 ms are accepted (got {requested}).");
                return;
            }

            bool changed = BasisOpusFrameDurationStateManager.SetFrameDurationMs(requested);
            int applied = BasisOpusFrameDurationStateManager.FrameDurationMs;
            string notification = changed
                ? $"Opus frame duration is now {applied} ms."
                : $"Opus frame duration was already {applied} ms.";

            BNL.Log(notification);
            SendBackMessage(peer, notification);
            BasisOpusFrameDurationStateManager.BroadcastState();
        }

        public static void SendBackMessage(NetPeer peer, string msg)
        {
            if (string.IsNullOrEmpty(msg))
            {
                return;
            }

            var writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.Message);
            writer.Put(msg);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
        public static bool GetBannedReason(string UUID, out string reason)
        {
            if (BannedPlayers.TryGetValue(UUID, out BannedPlayer player))
            {
                reason = player.Reason;
                return true;
            }

            reason = string.Empty;
            return false;
        }
        public static bool IsIpBanned(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                return false;
            }

            return BannedPlayers.Values.Any(p => p.HasBannedIp && p.BannedIp == ip);
        }
    }
}
