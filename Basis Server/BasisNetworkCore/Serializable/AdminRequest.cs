using Basis.Network.Core;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct AdminRequest
        {
            private byte messageIndex;
            public AdminRequestMode GetAdminRequestMode()
            {
                return (AdminRequestMode)messageIndex;
            }
            public void Deserialize(NetDataReader reader)
            {
                int bytesAvailable = reader.AvailableBytes;
                if (bytesAvailable > 0)
                {
                    messageIndex = reader.GetByte();
                }
                else
                {
                    BNL.LogError($"Unable to read remaining bytes, available: {bytesAvailable}");
                }
            }

            public void Serialize(NetDataWriter writer, AdminRequestMode AdminRequestMode)
            {
                messageIndex = (byte)AdminRequestMode;
                writer.Put(messageIndex);
            }
        }
        public enum AdminRequestMode : byte
        {
            Ban,//bans a player
            Kick,//kicks a player
            IpAndBan,// bans and ip bans a player
            Message,// sends a message to a user
            MessageAll,// sends a message to all users
            UnBanIP,// unbans a user and unbans a associated ip
            UnBan,// unbans a user
          //  RequestBannedPlayers,// banned player の list を取得する。
           // TeleportTo,// player へ teleport する。
            TeleportAll,// teleports everyone
            TeleportPlayer,

            // permission management (request は任意 user、modify は admin のみ)。
            GetPermissions,     // request full permission snapshot (read-only for non-admins)
            SetUserGroup,       // admin: add/remove user from a group
            SetUserNode,        // admin: add/remove permission node from a user
            SetGroupNode,       // admin: add/remove permission node from a group
            CreateGroup,        // admin: create a new permission group
            DeleteGroup,        // admin: delete a permission group
            SetGroupParent,     // admin: add/remove a parent group from a group

            EnableShoutMode,    // admin: enable shout mode for a player (non-spatialized broadcast voice)
            DisableShoutMode,   // admin: disable shout mode for a player

            GlobalToggleAvatars, // admin: toggle global avatar loading lock
            GlobalToggleProps,   // admin: toggle global prop loading lock
            GlobalToggleWorlds,  // admin: toggle global world loading lock
            GlobalGetLockState,  // server→client: current global lock state
            GlobalGetHeadlessAudioState, // server→client: current global headless audio state
            SetGlobalHeadlessAudio, // admin: explicitly set headless audio clip playback state for headless clients
            GlobalGetHeadlessDisallowState, // server→client: current global headless disallow state
            SetGlobalHeadlessDisallow, // admin: explicitly allow/disallow headless client connections
            SetGlobalOpusPacketLoss, // admin: set Opus FEC packet-loss percent (0..100) applied to every client's encoder
            GlobalGetOpusPacketLossState, // server→client: current Opus FEC packet-loss percent

            SetUserOpusBitrate,           // admin: override a single user's Opus encoder bitrate (bps); 0 = clear override
            UserOpusBitrateOverride,      // server→target user: their current bitrate override (0 = none)
            SetGlobalOpusFrameDuration,   // admin: set the Opus frame duration in milliseconds (20 or 40)
            GlobalGetOpusFrameDurationState, // server→client: current Opus frame duration in milliseconds

            // ── server config / allowlist (disk に persist) ─────────────────
            SetServerName,    // admin: set Configuration.ServerName + persist to config.xml. Payload: [string name]
            SetServerMotd,    // admin: set Configuration.ServerMotd + persist to config.xml. Payload: [string motd]
            SetAllowlistMode, // admin: set Configuration.BasisUserRestrictionMode + persist. Payload: [byte BasisUserRestrictionMode]
            AddAllowlist,     // admin: add UUID to BasisAllowList.txt. Payload: [string uuid]
            RemoveAllowlist,  // admin: remove UUID from BasisAllowList.txt. Payload: [string uuid]

            GlobalToggleServers, // admin: toggle global server-share lock (BasisGlobalLockManager.ServersLocked).

            GlobalToggleThirdPerson, // admin: toggle the global third-person camera disable (BasisGlobalLockManager.ThirdPersonDisabled). State is appended as the 5th bool in GlobalGetLockState.

            // ── default library (server-pushed library items、disk に persist) ──
            // payload: [byte mode (0=Avatar,1=World,2=Prop)][string url][string password]
            // PermNodes.ConfigurationEditor で gate される。server の defaultlibrary/ folder 下に
            // 新しい XML file を書き、updated list を rebroadcast する。
            AddDefaultLibraryItem,

            // payload: [string url]
            // URL が一致する defaultlibrary/ XML をすべて削除し、rebroadcast する。
            RemoveDefaultLibraryItem,

            // admin: inbound avatar sync message 上の AdditionalAvatarDatas
            // (blendshape、custom-behaviour param) global strip を toggle する。
            // muscle/position/rotation は通常どおり propagate される。
            // state は GlobalGetLockState の 6 番目の bool として append される。
            GlobalToggleAdditionalAvatarDataLock,

            // admin: per-category camera photo-metadata disallow mask (1 byte) を set する。
            // set bit は全 client に対して 1 つの embedding category を disallow する。0 = すべて許可。
            // current mask は GlobalGetLockState の trailing byte として append される。
            SetGlobalCameraPolicy,

            GlobalGetCrashReportState, // server→client: whether client error/exception reporting is enabled
            SetGlobalCrashReporting,   // admin: enable/disable client error/exception reporting (persisted). Payload: [bool]

            GlobalGetAudioRangeLimits, // server→client: current max microphone + hearing range in metres. Payload: [float micMeters][float hearingMeters]
            SetGlobalAudioRangeLimits, // admin: set max microphone + hearing range in metres (persisted). Payload: [float micMeters][float hearingMeters]

            // ── server log bundle (admin が logs/ + CrashReports/ を 1 compressed bundle として pull) ──
            // admin が request すると、server は logs/ と CrashReports/ folder を 1 container に pack し、
            // LZ4-compress して、admin channel 上で order どおりに stream して返す。
            // large bundle が 1 datagram に依存しないよう chunk に分割する。
            RequestAllLogs,   // client→server (admin): build and stream the full log bundle. Gated by basis.admin.logs. No payload.
            LogBundleBegin,   // server→client: start of a transfer. Payload: [string serverNameSafe][string fileName][bool isCompressed][int payloadBytes][int rawBytes][int totalChunks]
            LogBundleChunk,   // server→client: one ordered chunk. Payload: [int chunkIndex][lenPrefixed bytes]
            LogBundleEnd,     // server→client: end of transfer. Payload: [bool ok][string message]

            // server->client: netId に関係なく、locally loaded scene をすべて clear する。
            // payload なし。server が知らない orphaned scene を処理する。
            ClearAllScenes,

            DeleteAllLogs,    // client→server (admin): delete every file under logs/ + CrashReports/. Gated by basis.admin.logs. No payload. Server replies with a status Message.

            // ── instance restriction policy (persisted。basis.moderation.globallock で gate) ──
            GlobalTogglePlayspaceMover, // admin: toggle the global playspace-mover lockout. State appended to GlobalGetLockState. Non-admins cannot grab/drag their play space while set.
            GlobalToggleDirectConnect,  // admin: toggle the global direct-connect (P2P) lockout. State appended to GlobalGetLockState. The server also refuses to broker P2P requests from non-admins while set.

            GlobalGetAvatarScaleLimits, // server→client: min/max avatar eye height in metres. Payload: [float minMeters][float maxMeters]
            SetGlobalAvatarScaleLimits, // admin: set min/max avatar eye height in metres (persisted). Non-admins are clamped to this range; admins bypass it. Payload: [float minMeters][float maxMeters]

            GlobalGetResourceLimits, // server→client: persisted DoS caps. Payload: [int maxDatabaseEntries][int maxDatabaseNameLength][int maxDatabasePayloadEntries][int maxContentSpheresPerPlayer]
            SetGlobalResourceLimits, // admin: set the persisted DoS caps. Payload: [int maxDatabaseEntries][int maxDatabaseNameLength][int maxDatabasePayloadEntries][int maxContentSpheresPerPlayer]
        }
    }
}
