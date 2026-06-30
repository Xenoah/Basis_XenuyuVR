using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkCore;
using BasisNetworkCore.Pooling;
using BasisNetworkCore.Security;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisPermissions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerHandle
{
    public static class BasisServerHandleEvents
    {
        [ThreadStatic] private static HashSet<int> _excludedSet;

        #region Server Events Setup
        public static void SubscribeServerEvents()
        {
            NetworkServer.Listener.ConnectionRequestEvent += HandleConnectionRequest;
            NetworkServer.Listener.PeerDisconnectedEvent += HandlePeerDisconnected;
            NetworkServer.Listener.NetworkReceiveEvent += BasisNetworkMessageProcessor.ProcessMessage;
            NetworkServer.Listener.NetworkErrorEvent += OnNetworkError;
            BasisServerInfoQuery.Subscribe();
        }

        public static void UnsubscribeServerEvents()
        {
            NetworkServer.Listener.ConnectionRequestEvent -= HandleConnectionRequest;
            NetworkServer.Listener.PeerDisconnectedEvent -= HandlePeerDisconnected;
            NetworkServer.Listener.NetworkReceiveEvent -= BasisNetworkMessageProcessor.ProcessMessage;
            NetworkServer.Listener.NetworkErrorEvent -= OnNetworkError;
            BasisServerInfoQuery.Unsubscribe();
        }

        public static void StopWorker()
        {
            NetworkServer.Server?.Stop();
            BasisServerHandleEvents.UnsubscribeServerEvents();
        }
        #endregion

        #region Network Event Handlers

        public static void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            BNL.LogError($"Endpoint {endPoint.ToString()} was reported with error {socketError}");
        }
        #endregion

        #region Peer Connection and Disconnection

        /// <summary>
        /// graceful disconnect と reconnect-collision eviction で共有する、
        /// peer ごとの idempotent subsystem cleanup を実行する。
        /// 他 peer へ disconnect を broadcast せず、server-wide state も reset しない。
        /// どちらが適切かは caller が判断する。
        /// </summary>
        private static bool CleanupPeerSubsystems(NetPeer peer, int id)
        {
            if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid))
            {
                PermissionIntegration.RemovePlayerMeta(uuid);
                PermissionIntegration.EvictPermissionCache(uuid);
                BasisNetworkHandleErrorReport.RemoveUser(uuid);
                BasisNetworkResourceManagement.RemovePeerResources(uuid);
            }

            NetworkServer.AuthIdentity.RemoveConnection(id);
            BasisNetworkOwnership.RemovePlayerOwnership(id);
            BasisSavedState.RemovePlayer(id);
            BasisServerReductionSystemEvents.RemovePlayer(id);
            BasisNetworkPIPCamera.RemovePlayer(id);
            BasisNetworkContentShare.RemovePlayerSpheres(id);
            BasisNetworkPreloadResourceManagement.RemovePeer(id);
            BasisNetworkServer.Security.BasisUserOpusBitrateStateManager.ClearForPeer(id);
            BasisServerP2PBroker.RemovePeer(id);
            BasisNetworkMessageProcessor.ClearPeerErrors(id);
            BasisServerMessageRegistry.ClearSubscription(id);

            return NetworkServer.AuthenticatedPeers.TryRemove(id, out _);
        }

        public static void HandlePeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            try
            {
                if(peer == null)
                {
                    BNL.LogError("Missing Peer this is a mistake!");
                    return;
                }
                int id = peer.Id;

                if (CleanupPeerSubsystems(peer, id))
                {
                    NetworkServer.RebuildPeerSnapshot();
                    BNL.Log($"Peer removed: {id}");
                }
                else
                {
                    BNL.Log($"Peer {id} was not in AuthenticatedPeers (likely rejected before auth completed).");
                }

                if (NetworkServer.AuthenticatedPeers.IsEmpty)
                {
                    BasisNetworkIDDatabase.Reset();
                    BasisNetworkResourceManagement.Reset();
                    BasisNetworkContentShare.Reset();
                }

                NetDataWriter writer = NetworkServer.RentWriter();
                writer.Put((ushort)id);
                if (NetworkServer.CheckValidated(writer))
                {
                    NetPeer[] Peers = NetworkServer.PeerSnapshot;
                    foreach (var client in Peers)
                    {
                        if (client.Id != id)
                        {
                            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.DisconnectionChannel, writer.Length);
                            client.Send(writer, BasisNetworkCommons.DisconnectionChannel, DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
                NetworkServer.ReturnWriter(writer);
            }
            catch (Exception e)
            {
                BNL.LogError($"{e.Message} {e.StackTrace}");
            }
        }
        #endregion

        #region Utility Methods
        public static void RejectWithReason(ConnectionRequest request, string reason)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(reason);
            request.Reject(writer);
            NetworkServer.ReturnWriter(writer);
            BNL.LogError($"Rejected for reason: {reason}");
        }
        public static void RejectWithReason(NetPeer request, string reason)
        {
            int id = request.Id;
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(reason ?? string.Empty);
            byte[] reasonBytes = writer.CopyData();
            NetworkServer.ReturnWriter(writer);
            // key-value-matched remove: "Peer already exists" は duplicate を拒否するため、
            // stored NetPeer が実際にこの peer の場合だけ evict する。
            // そうしないと、この slot を所有する alive peer を静かに kick してしまう。
            var kvp = new KeyValuePair<int, NetPeer>(id, request);
            if (((ICollection<KeyValuePair<int, NetPeer>>)NetworkServer.AuthenticatedPeers).Remove(kvp))
            {
                NetworkServer.RebuildPeerSnapshot();
            }
            request.Disconnect(reasonBytes);
            BNL.LogError($"Rejected after accept with reason: {reason}");
        }

        public static bool IsHeadlessDisallowed(ClientMetaDataMessage metaData, out string reason)
        {
            if (!BasisHeadlessConnectionPolicyManager.HeadlessDisallowed ||
                !BasisHeadlessConnectionPolicyManager.IsHeadlessClient(metaData))
            {
                reason = null;
                return false;
            }

            reason = BasisHeadlessConnectionPolicyManager.DisallowedReason;
            return true;
        }
        #endregion

        #region Connection Handling
        public static void HandleConnectionRequest(ConnectionRequest ConReq)
        {
            try
            {
                if (BasisPlayerModeration.IsIpBanned(ConReq.RemoteEndPoint.Address.ToString()))
                {
                    RejectWithReason(ConReq, "Banned IP");
                    return;
                }
              //  BNL.Log("Processing Connection Request");
                int ServerCount = NetworkServer.Server.ConnectedPeersCount;

                if (ServerCount >= NetworkServer.Configuration.PeerLimit)
                {
                    RejectWithReason(ConReq, "Server is full! Rejected.");
                    return;
                }

                if (!ConReq.Data.TryGetUShort(out ushort ClientVersion))
                {
                    RejectWithReason(ConReq, "Invalid client data.");
                    return;
                }

                if (ClientVersion != BasisNetworkVersion.ServerVersion)
                {
                    RejectWithReason(ConReq, "Client version does not match server.");
                    return;
                }
                if (NetworkServer.Configuration.UseAuth)
                {
                    BytesMessage authMessage = new BytesMessage();
                    if (!authMessage.Deserialize(ConReq.Data, out byte[] AuthBytes))
                    {
                        RejectWithReason(ConReq, "Malformed auth payload");
                        return;
                    }
                    if (NetworkServer.Auth.IsAuthenticated(AuthBytes) == false)
                    {
                        RejectWithReason(ConReq, "Authentication failed, Auth rejected");
                        return;
                    }
                }
                else
                {
                    // needle を進めるため、data は読みたい。
                    BytesMessage authMessage = new BytesMessage();
                    authMessage.Deserialize(ConReq.Data, out byte[] UnusedBytes);
                }
                if (NetworkServer.Configuration.UseAuthIdentity)
                {
                    NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on
                    NetworkServer.AuthIdentity.ProcessConnection(NetworkServer.Configuration, ConReq, newPeer);
                }
                else
                {
                    ReadyMessage readyMessage = new ReadyMessage();
                    readyMessage.Deserialize(ConReq.Data);

                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        if (IsHeadlessDisallowed(readyMessage.playerMetaDataMessage, out string reason))
                        {
                            RejectWithReason(ConReq, reason);
                            return;
                        }
                    }

                    NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on

                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        OnNetworkAccepted(newPeer, readyMessage, readyMessage.playerMetaDataMessage.playerUUID);
                    }
                }
            }
            catch (Exception e)
            {
                RejectWithReason(ConReq, "Fatal Connection Issue stacktrace on server " + e.Message);
                BNL.LogError(e.StackTrace);
            }
        }
        public static void OnNetworkAccepted(NetPeer newPeer, ReadyMessage ReadyMessage, string UUID)
        {
            ushort PeerId = (ushort)newPeer.Id;

            // AllowList gate。両方の auth path (DID challenge + plain ReadyMessage) は verified UUID 付きで
            // ここへ流れ込むため、entry 時に BasisUserRestrictionMode.AllowList を enforce する single point になる。
            // banlist は HandleConnectionRequest / BasisDIDAuthIdentity.ProcessConnection で別途 enforce される。
            if (NetworkServer.Configuration.BasisUserRestrictionMode == BasisUserRestrictionMode.AllowList
                && NetworkServer.AllowList != null
                && !NetworkServer.AllowList.IsAllowed(UUID))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — not on allowlist.");
                RejectWithReason(newPeer, "You are not on the allowlist.");
                return;
            }

            if (NetworkServer.Configuration.BasisUserRestrictionMode == BasisUserRestrictionMode.BanList
                && NetworkServer.BanList != null
                && NetworkServer.BanList.IsBanned(UUID))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — on banlist.");
                RejectWithReason(newPeer, "You are not permitted on this server.");
                return;
            }

            // rejoin-only lockdown: mode 有効化時に capture された UUID だけが (re)connect できる。
            // config-editor admin は常に bypass し、admin が自分を lock out できないようにする。
            if (NetworkServer.Configuration.BasisUserRestrictionMode == BasisUserRestrictionMode.RejoinOnly
                && !BasisRejoinLockManager.IsAllowed(UUID)
                && !PermissionIntegration.HasValidRequirement(UUID, PermNodes.ConfigurationEditor))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — server locked to current players (rejoin-only).");
                RejectWithReason(newPeer, "The server is locked — only players already here may rejoin.");
                return;
            }

            string sanitizedDisplayName = BasisDisplayNameSanitizer.Sanitize(ReadyMessage.playerMetaDataMessage.playerDisplayName);
            if (string.IsNullOrEmpty(sanitizedDisplayName))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — empty or invisible display name.");
                RejectWithReason(newPeer, "Choose a non-empty username.");
                return;
            }
            ReadyMessage.playerMetaDataMessage.playerDisplayName = sanitizedDisplayName;

            bool added = NetworkServer.AuthenticatedPeers.TryAdd(PeerId, newPeer);
            if (!added)
            {
                // reconnect collision: 前回 disconnect の subsystem cleanup が完了する前、
                // または元の PeerDisconnectedEvent がまだ dispatch される前に、
                // LiteNetLib がこの peer-id slot を recycle した。
                // LNL が同じ Id の live peer を 2 つ渡すことはないため old entry は stale。
                // 同期的に evict し、insert を retry する。
                if (NetworkServer.AuthenticatedPeers.TryGetValue(PeerId, out NetPeer stale) &&
                    !ReferenceEquals(stale, newPeer))
                {
                    BNL.Log($"Reconnect collision on peer id {PeerId}; evicting stale entry and accepting new connection.");
                    CleanupPeerSubsystems(stale, PeerId);
                    added = NetworkServer.AuthenticatedPeers.TryAdd(PeerId, newPeer);
                }
            }

            if (added)
            {
                newPeer.Tag = NetworkServer.AuthenticatedPeerTag;
                NetworkServer.RebuildPeerSnapshot();
                BNL.Log($"Peer connected: {newPeer.Id}");
                // user が提供した UUID が正しいと決して仮定せず、server 側で必ず再計算する。
                // これにより、auth を通過したが local で bad UUID を持つ場合でも、影響はその user local に限られる。
                // user local に特定 UUID を強制する方法はない。internet はそういう仕組みではない。
                // 代わりに、追加の client 全員が正しい値を持つことを保証できる。
                // これは server が auth check を行っている場合だけ起きる。
                ReadyMessage.playerMetaDataMessage.playerUUID = UUID;
                PermissionIntegration.StorePlayerMeta(UUID, ReadyMessage.playerMetaDataMessage);

               Configuration Config = NetworkServer.Configuration;
                // server 側で処理した後、その data を local client へ送る。
                ServerMetaDataMessage ServerMetaDataMessage = new ServerMetaDataMessage
                {
                    ClientMetaDataMessage = ReadyMessage.playerMetaDataMessage,
                    SyncInterval = Config.BSRSMillisecondDefaultInterval,
                    BaseMultiplier = Config.BSRBaseMultiplier,
                    IncreaseRate = Config.BSRSIncreaseRate,
                    SlowestSendRate = Config.BSRSlowestSendRate,
                    PeerLimit = Config.PeerLimit,

                };
                ServerMetaDataMessage.SetPermissions(PermissionIntegration.Manager.GetAllAllowedRules(UUID), PermissionIntegration.Manager.GetAllDeniedRules(UUID));
                NetDataWriter Writer = NetworkServer.RentWriter();
                ServerMetaDataMessage.Serialize(Writer);
                NetworkServer.TrySend(newPeer, Writer, BasisNetworkCommons.metaDataChannel, DeliveryMethod.ReliableOrdered);

                BasisServerMessageRegistry.SendSupplyTo(newPeer);

                if (BasisNetworkIDDatabase.GetAllNetworkID(out List<ServerNetIDMessage> ServerNetIDMessages))
                {
                    ServerUniqueIDMessages ServerUniqueIDMessageArray = new ServerUniqueIDMessages
                    {
                        Messages = ServerNetIDMessages.ToArray(),
                    };

                    Writer.Reset();
                    ServerUniqueIDMessageArray.Serialize(Writer);
                    //BNL.Log($"Sending out Network Id Count " + ServerUniqueIDMessageArray.Messages.Length);
                    NetworkServer.TrySend(newPeer, Writer, BasisNetworkCommons.NetIDAssignsChannel, DeliveryMethod.ReliableOrdered);
                }

                NetworkServer.ReturnWriter(Writer);

                SendRemoteSpawnMessage(newPeer, ReadyMessage);

                BasisNetworkResourceManagement.SendOutAllResources(newPeer);
                BasisNetworkServerLibrary.SendLibraryToPeer(newPeer);
                BasisNetworkOwnership.SendOutOwnershipInformation(newPeer);
                BasisNetworkPIPCamera.SendPIPStateToPeer(newPeer);
                BasisNetworkContentShare.SendAllSpheresToPeer(newPeer);
                BasisNetworkServer.Security.BasisGlobalLockManager.SendLockStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisHeadlessAudioStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisHeadlessConnectionPolicyManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisOpusPacketLossStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisOpusFrameDurationStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisUserOpusBitrateStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisCrashReportStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisAudioRangeLimitManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisAvatarScaleLimitManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisResourceLimitManager.SendStateToPeer(newPeer);
                SendShoutStateToPeer(newPeer);
            }
            else
            {
                RejectWithReason(newPeer, "Peer already exists.");
            }
        }
        #endregion
        // delegate type を定義する。
        public delegate void AuthEventHandler(NetPacketReader reader, NetPeer peer);

        // delegate type の event を宣言する。
        public static event AuthEventHandler OnAuthReceived;
        public static void HandleAuth(NetPacketReader Reader, NetPeer Peer)
        {
            OnAuthReceived?.Invoke(Reader, Peer);
            Reader.Recycle();
        }
        public static ServerEventHandler OnServerReceived;
        public delegate void ServerEventHandler(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod);
        #region Avatar and Voice Handling
        public static void SendAvatarMessageToClients(NetPacketReader Reader, NetPeer Peer)
        {
            ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage();
            ClientAvatarChangeMessage.Deserialize(Reader);
            Reader.Recycle();

            // global avatar lock: network broadcast は拒否するが、local state は保存する。
            if (BasisNetworkServer.Security.BasisGlobalLockManager.AvatarsLocked)
            {
                bool hasBypass = false;
                if (NetworkServer.AuthIdentity.NetIDToUUID(Peer, out string uuid))
                {
                    hasBypass = PermissionIntegration.HasValidRequirement(uuid, PermNodes.ResourceLockBypassAvatar);
                }

                if (!hasBypass)
                {
                    BNL.Log($"Avatar loading is globally disabled. Rejected avatar change from peer {Peer.Id}");
                    BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "Avatar loading is currently disabled by an admin.");
                    return;
                }
            }

            ServerAvatarChangeMessage serverAvatarChangeMessage = new ServerAvatarChangeMessage
            {
                clientAvatarChangeMessage = ClientAvatarChangeMessage,
                uShortPlayerId = new PlayerIdMessage
                {
                    playerID = (ushort)Peer.Id
                }
            };
            BasisSavedState.AddLastData(Peer, ClientAvatarChangeMessage);
            NetDataWriter Writer = NetworkServer.RentWriter();
            serverAvatarChangeMessage.Serialize(Writer);

            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessageChannel, Peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(Writer);
        }

        public static void HandleVoiceMessage(NetPacketReader reader, NetPeer peer)
        {
            AudioSegmentDataMessage audioSegment = ThreadSafeMessagePool<AudioSegmentDataMessage>.Rent();
            audioSegment.Deserialize(reader);
            reader.Recycle();

            ServerAudioSegmentMessage serverAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment,
            };

            SendVoiceMessageToClients(serverAudio, peer, DeliveryMethod.Unreliable);

            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }

        /// <summary>
        /// client が ShoutVoiceChannel (channel 0) で送った shout voice を処理する。
        /// sender が shout mode を許可されている場合だけ処理する。
        /// connected peer 全員へ broadcast する。
        /// </summary>
        public static void HandleShoutVoiceMessage(NetPacketReader reader, NetPeer peer)
        {
            if (!BasisSavedState.IsInShoutMode(peer.Id))
            {
                BNL.LogError($"Peer {peer.Id} sent shout voice but is not in shout mode. Ignoring.");
                reader.Recycle();
                return;
            }

            AudioSegmentDataMessage audioSegment = ThreadSafeMessagePool<AudioSegmentDataMessage>.Rent();
            audioSegment.Deserialize(reader);
            reader.Recycle();

            ServerAudioSegmentMessage serverAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment,
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)peer.Id,
                },
            };

            // 一度だけ serialize し、各 peer へ raw 送信する。N 個の writer->packet copy を skip する。
            var writer = NetworkServer.RentWriter();
            serverAudio.Serialize(writer);
            int len = writer.Length;
            byte[] data = writer.Data;
            byte channel = BasisNetworkCommons.ShoutVoiceChannel;
            int senderId = peer.Id;

            var clients = NetworkServer.PeerSnapshot;
            for (int i = 0; i < clients.Length; i++)
            {
                NetPeer client = clients[i];
                if (client.Id != senderId)
                {
                    client.SendUnreliableRawMerge(data, 0, len, channel);
                    BasisNetworkStatistics.RecordOutbound(channel, len);
                }
            }

            NetworkServer.ReturnWriter(writer);
            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }

        /// <summary>
        /// shout mode state change を AdminChannel 経由で全 client へ broadcast する。
        /// </summary>
        public static void BroadcastShoutModeState(ushort targetPlayerId, bool enabled, ushort initiatorPlayerId)
        {
            var writer = NetworkServer.RentWriter();
            AdminRequestMode mode = enabled ? AdminRequestMode.EnableShoutMode : AdminRequestMode.DisableShoutMode;
            new AdminRequest().Serialize(writer, mode);
            writer.Put(targetPlayerId);
            writer.Put(initiatorPlayerId);

            NetPeer[] peers = NetworkServer.PeerSnapshot;
            foreach (var client in peers)
            {
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                client.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }

            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// newly connected peer へ現在の shout mode state を送る。
        /// </summary>
        public static void SendShoutStateToPeer(NetPeer newPeer)
        {
            int[] shoutPlayers = BasisSavedState.GetAllShoutModePlayers();
            if (shoutPlayers.Length == 0) return;

            var writer = NetworkServer.RentWriter();
            foreach (int peerId in shoutPlayers)
            {
                writer.Reset();
                new AdminRequest().Serialize(writer, AdminRequestMode.EnableShoutMode);
                writer.Put((ushort)peerId);
                writer.Put((ushort)peerId);
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                newPeer.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(writer);
        }

        public static void SendVoiceMessageToClients(ServerAudioSegmentMessage audioSegment, NetPeer sender, DeliveryMethod method)
        {
            if (!BasisSavedState.GetResolvedVoicePeers(sender, out List<NetPeer> targetPeers) || targetPeers == null)
            {
                return;
            }

            // concurrent rebuild や RemovePlayer が indexer read と race しないよう、list lock 下で snapshot する。
            // lock は短く、ref-array copy だけ。
            NetPeer[] snapshot;
            int snapshotCount;
            lock (targetPeers)
            {
                snapshotCount = targetPeers.Count;
                if (snapshotCount == 0) return;
                snapshot = ArrayPool<NetPeer>.Shared.Rent(snapshotCount);
                targetPeers.CopyTo(0, snapshot, 0, snapshotCount);
            }

            audioSegment.playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)sender.Id,
            };

            bool largeId = sender.Id > byte.MaxValue;
            byte channel = largeId ? BasisNetworkCommons.VoiceLargeChannel : BasisNetworkCommons.VoiceChannel;

            // byte[] へ一度だけ serialize し、各 peer へ raw 送信する。N 個の writer->packet copy を skip する。
            var writer = NetworkServer.RentWriter();
            audioSegment.Serialize(writer, largeId);
            int len = writer.Length;
            byte[] data = writer.Data;

            for (int i = 0; i < snapshotCount; i++)
            {
                NetPeer client = snapshot[i];
                if (client == null) continue;
                if (BasisNetworkServer.BasisServerP2PBroker.IsP2POffloaded(sender.Id, client.Id))
                {
                    continue;
                }
                client.SendUnreliableRawMerge(data, 0, len, channel);
                BasisNetworkStatistics.RecordOutbound(channel, len);
            }

            NetworkServer.ReturnWriter(writer);
            ArrayPool<NetPeer>.Shared.Return(snapshot, clearArray: true);
        }
        public static void UpdateVoiceReceivers(NetPacketReader Reader, NetPeer Peer, bool largeCount)
        {
            VoiceReceiversMessage VoiceReceiversMessage = new VoiceReceiversMessage();
            VoiceReceiversMessage.Deserialize(Reader, largeCount);
            Reader.Recycle();
            BasisSavedState.AddLastData(Peer, VoiceReceiversMessage);
        }

        /// <summary>
        /// inverted mode: message には EXCLUDE する ID が含まれる。それ以外の全員が recipient。
        /// </summary>
        public static void UpdateVoiceReceiversInverted(NetPacketReader Reader, NetPeer Peer, bool largeCount)
        {
            VoiceReceiversMessage excluded = new VoiceReceiversMessage();
            excluded.Deserialize(Reader, largeCount);
            Reader.Recycle();

            int senderId = Peer.Id;
            var peers = BasisSavedState.GetOrCreateResolvedList(senderId);

            lock (peers)
            {
                peers.Clear();

                if (excluded.Users == null || excluded.UsersLength == 0)
                {
                    // exclusion なし。sender 以外の全員が recipient。
                    foreach (var kvp in NetworkServer.AuthenticatedPeers)
                    {
                        if (kvp.Key != senderId)
                            peers.Add(kvp.Value);
                    }
                }
                else
                {
                    // allocation を避けるため thread-local set を再利用する。
                    if (_excludedSet == null)
                        _excludedSet = new HashSet<int>(64);
                    else
                        _excludedSet.Clear();
                    for (int i = 0; i < excluded.UsersLength; i++)
                        _excludedSet.Add(excluded.Users[i]);

                    foreach (var kvp in NetworkServer.AuthenticatedPeers)
                    {
                        if (kvp.Key != senderId && !_excludedSet.Contains(kvp.Key))
                            peers.Add(kvp.Value);
                    }
                }
            }

            excluded.ReturnPool();
        }

        /// <summary>
        /// bitfield mode: position N の set bit は playerID N が recipient であることを意味する。
        /// wire format: [byteCount: ushort][bitfield bytes]
        /// </summary>
        public static void UpdateVoiceReceiversBitfield(NetPacketReader Reader, NetPeer Peer)
        {
            int senderId = Peer.Id;

            if (Reader.AvailableBytes < sizeof(ushort))
            {
                Reader.Recycle();
                return;
            }

            ushort byteCount = Reader.GetUShort();

            if (byteCount == 0 || Reader.AvailableBytes < byteCount)
            {
                Reader.Recycle();
                return;
            }

            var peers = BasisSavedState.GetOrCreateResolvedList(senderId);

            lock (peers)
            {
                peers.Clear();

                for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
                {
                    byte b = Reader.GetByte();
                    if (b == 0) continue;

                    int baseId = byteIdx * 8;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((b & (1 << bit)) != 0)
                        {
                            int playerId = baseId + bit;
                            if (playerId != senderId && NetworkServer.AuthenticatedPeers.TryGetValue(playerId, out NetPeer found))
                            {
                                peers.Add(found);
                            }
                        }
                    }
                }
            }

            Reader.Recycle();
        }
        #endregion

        #region Spawn and Client List Handling
        public static void SendRemoteSpawnMessage(NetPeer authClient, ReadyMessage readyMessage)
        {
            ServerReadyMessage serverReadyMessage = LoadInitialState(authClient, readyMessage);
            NotifyExistingClients(serverReadyMessage, authClient);
            SendClientListToNewClient(authClient);
        }

        public static ServerReadyMessage LoadInitialState(NetPeer authClient, ReadyMessage readyMessage)
        {
            ServerReadyMessage serverReadyMessage = new ServerReadyMessage
            {
                localReadyMessage = readyMessage,
                playerIdMessage = new PlayerIdMessage()
                {
                    playerID = (ushort)authClient.Id
                }
            };
            BasisServerReductionSystemEvents.AddMessage(authClient, readyMessage.localAvatarSyncMessage, 0);
            BasisSavedState.AddLastData(authClient, readyMessage);
            return serverReadyMessage;
        }
        /// <summary>
        /// 既存 client に new player を通知する。
        /// </summary>
        /// <param name="serverSideSyncPlayerMessage"></param>
        /// <param name="authClient"></param>
        public static void NotifyExistingClients(ServerReadyMessage serverSideSyncPlayerMessage, NetPeer authClient)
        {
            NetDataWriter Writer = NetworkServer.RentWriter();
            try
            {
                serverSideSyncPlayerMessage.Serialize(Writer);
                if (!NetworkServer.CheckValidated(Writer))
                {
                    return;
                }
                NetPeer[] peers = NetworkServer.PeerSnapshot;
                foreach (NetPeer client in peers)
                {
                    if (client == authClient)
                    {
                        continue;
                    }
                    try
                    {
                        client.Send(Writer, BasisNetworkCommons.CreateRemotePlayerChannel, DeliveryMethod.ReliableOrdered);
                        BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.CreateRemotePlayerChannel, Writer.Length);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"Failed to notify peer {client?.Id} of new player {authClient.Id}: {ex.Message}");
                    }
                }
            }
            finally
            {
                NetworkServer.ReturnWriter(Writer);
            }
        }
        /// <summary>
        /// new client へ全員を送る。
        /// </summary>
        /// <param name="authClient"></param>
        public static void SendClientListToNewClient(NetPeer authClient)
        {
            try
            {
                NetPeer[] peers = NetworkServer.PeerSnapshot;
                NetDataWriter writer = NetworkServer.RentWriter();
                foreach (var peer in peers)
                {
                    if (peer == authClient)
                    {
                        continue;
                    }
                    writer.Reset();
                    if (CreateServerReadyMessageForPeer(peer, out ServerReadyMessage Message))
                    {
                        Message.Serialize(writer);
                        //  BNL.Log($"Writing Data with size {writer.Length}");
                        NetworkServer.TrySend(authClient, writer, BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, DeliveryMethod.ReliableOrdered);
                    }
                }
                NetworkServer.ReturnWriter(writer);
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to send client list: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private static bool CreateServerReadyMessageForPeer(NetPeer peer, out ServerReadyMessage ServerReadyMessage)
        {
            try
            {
                ClientAvatarChangeMessage changeState;
                bool haveAvatar = BasisSavedState.GetLastAvatarChangeState(peer, out changeState) && changeState.byteArray != null;
                if (!haveAvatar)
                {
                    BNL.Log($"No avatar state yet for peer {peer.Id}; sending placeholder spawn so the remote player is created on the joining client.");
                    changeState = new ClientAvatarChangeMessage
                    {
                        loadMode = 0,
                        byteArray = null,
                        LocalAvatarIndex = 0
                    };
                }

                int id = peer.Id;
                LocalAvatarSyncMessage syncState;
                if (BasisServerReductionSystemEvents.playerStates.TryGetValue(id, out PlayerState state))
                {
                    syncState = state.SyncMessage.avatarSerialization;
                }
                else
                {
                    syncState = new LocalAvatarSyncMessage
                    {
                        DataQualityLevel = (byte)Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality.High,
                        array = new byte[NetworkServer.HighQualityLength],
                        AdditionalAvatarDatas = null,
                        AdditionalAvatarDataSize = 0,
                        LinkedAvatarIndex = 0
                    };
                    // 必要なら fallback を log する。
                    // BNL.LogError("Unable to get Last Player Avatar Data! Using Error Fallback");
                }
                // metadata。
                if (!BasisSavedState.GetLastPlayerMetaData(peer, out var metaData))
                {
                    metaData = new ClientMetaDataMessage
                    {
                        playerDisplayName = "Error",
                        playerUUID = string.Empty,
                        playerPlatform = string.Empty
                    };
                    BNL.LogError("Unable to get Last Player Meta Data! Using Error Fallback");
                }

                // ServerReadyMessage を組み立てる。
                ServerReadyMessage = new ServerReadyMessage
                {
                    localReadyMessage = new ReadyMessage
                    {
                        localAvatarSyncMessage = syncState,
                        clientAvatarChangeMessage = changeState,
                        playerMetaDataMessage = metaData
                    },
                    playerIdMessage = new PlayerIdMessage
                    {
                        playerID = (ushort)peer.Id
                    }
                };

                return true;
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to create ServerReadyMessage for peer {peer.Id}: {ex.Message}");
                ServerReadyMessage = new ServerReadyMessage();
                return false;
            }
        }
        #endregion
        #region Network ID Generation
        public static void NetIDAssign(NetPacketReader Reader, NetPeer Peer)
        {
            NetIDMessage ServerUniqueIDMessage = new NetIDMessage();
            ServerUniqueIDMessage.Deserialize(Reader);
            Reader.Recycle();
            // ushort を含む message を client に返す。new の場合は全員へ送る。
            BasisNetworkIDDatabase.AddOrFindNetworkID(Peer, ServerUniqueIDMessage.playerID);
            // string を ushort に変換する必要がある。
        }
        public static void LoadResource(NetPacketReader Reader, NetPeer Peer,string UUID)
        {
            LocalLoadResource LocalLoadResource = new LocalLoadResource();

            if (NetworkServer.AuthIdentity.NetIDToUUID(Peer, out string uuid) == false)
            {
                BNL.LogError($"User UUID not found for peer: {Peer}");
                return;
            }
            LocalLoadResource.Deserialize(Reader);
            bool isPrivileged = PermissionIntegration.HasValidRequirement(Peer, PermNodes.protection);
            LocalLoadResource.IsAdminLocked = isPrivileged;
            LocalLoadResource.UUIDOfCreator = UUID;
            if (!isPrivileged)
            {
                LocalLoadResource.Persist = false;
                LocalLoadResource.Static = false;
                LocalLoadResource.StaticAdminLocked = false;
            }
            Reader.Recycle();

            switch (LocalLoadResource.Mode)
            {
                case 0:
                    if (BasisNetworkServer.Security.BasisGlobalLockManager.PropsLocked &&
                        !PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLockBypassProp))
                    {
                        BNL.Log($"Prop loading is globally disabled. Rejected request from {UUID}");
                        BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "Prop loading is currently disabled by an admin.");
                        return;
                    }
                    if (PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLoadProp) == false)
                    {
                        BNL.LogError($"Invalid Request To Load Gameobject From {UUID}");
                        return;
                    }
                    break;
                case 1:
                    if (BasisNetworkServer.Security.BasisGlobalLockManager.WorldsLocked &&
                        !PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLockBypassWorld))
                    {
                        BNL.Log($"World loading is globally disabled. Rejected request from {UUID}");
                        BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "World loading is currently disabled by an admin.");
                        return;
                    }
                    if (PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLoadWorld) == false)
                    {
                        BNL.LogError($"Invalid Request To Load Scene From {UUID}");
                        return;
                    }
                    break;
                default:
                    BNL.LogError($"Missing Mode {LocalLoadResource.Mode}");
                    return;
            }
            // load strategy に基づいて route する。
            switch (LocalLoadResource.LoadStrategy)
            {
                case 0:
                    BasisNetworkResourceManagement.LoadResource(LocalLoadResource);
                    break;
                case 2: // Synchronized
                    BasisNetworkPreloadResourceManagement.StartSynchronizedLoad(LocalLoadResource);
                    break;
                case 3: // Predownload only - tell everyone to cache it; do not register or spawn
                    BasisNetworkResourceManagement.PredownloadResource(LocalLoadResource);
                    break;
                default:
                    BNL.LogError("Falling Back to Resource Load, Unsupported Load Strategy");
                    BasisNetworkResourceManagement.LoadResource(LocalLoadResource);
                    break;
            }
        }
        public static void HandlePreloadReady(NetPacketReader Reader, NetPeer Peer)
        {
            PreloadReadyMessage readyMsg = new PreloadReadyMessage();
            readyMsg.Deserialize(Reader);
            Reader.Recycle();
            BasisNetworkPreloadResourceManagement.HandleClientReady(readyMsg.LoadedNetID, Peer.Id, readyMsg.IsReady);
        }
        public static void UnloadResource(NetPacketReader Reader, NetPeer Peer)
        {
            UnLoadResource UnLoadResource = new UnLoadResource();
            UnLoadResource.Deserialize(Reader);
            Reader.Recycle();

            switch (UnLoadResource.Mode)
            {
                case 0:
                    if (PermissionIntegration.HasValidRequirement(Peer, PermNodes.ResourceUnloadProp) == false)
                    {
                        return;
                    }
                    break;
                case 1:
                    if (PermissionIntegration.HasValidRequirement(Peer, PermNodes.ResourceUnloadWorld) == false)
                    {
                        return;
                    }
                    break;
                default:
                    BNL.LogError($"Missing Mode {UnLoadResource.Mode}");
                    return;
            }

            // ushort を含む message を client に返す。new の場合は全員へ送る。
            BasisNetworkResourceManagement.UnloadResource(UnLoadResource, Peer);
            // string を ushort に変換する必要がある。
        }
        public static void HandleModifyResource(NetPacketReader Reader, NetPeer Peer)
        {
            ModifyResource modifyResource = new ModifyResource();
            modifyResource.Deserialize(Reader);
            Reader.Recycle();
            // authorization (creator または moderator) は SetStatic 内で enforce される。
            BasisNetworkResourceManagement.SetStatic(modifyResource, Peer);
        }
        #endregion
        public static void HandleStoreDatabase(NetPacketReader reader, NetPeer peer)
        {
            if (NetworkServer.Configuration.DisableWriteUnlessAdminPersistentFlag)
            {
                if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.ConfigurationEditor))
                {
                    return;
                }
            }
            var dataMessage = new DatabasePrimativeMessage();
            dataMessage.Deserialize(reader);
            reader.Recycle();

            var basisData = new BasisData(dataMessage.Name, dataMessage.jsonPayload);
            BasisPersistentDatabase.AddOrUpdate(basisData);
        }

        public static void HandleRequestStoreDatabase(NetPacketReader reader, NetPeer peer)
        {
            if(NetworkServer.Configuration.DisableReadUnlessAdminPersistentFlag)
            {
                if(!PermissionIntegration.HasValidRequirement(peer, PermNodes.ConfigurationEditor))
                {
                    return;
                }
            }
            var dataRequest = new DataBaseRequest();
            dataRequest.Deserialize(reader);
            reader.Recycle();
            if (!BasisPersistentDatabase.GetByName(dataRequest.DatabaseID, out var db))
            {
                db = new BasisData(dataRequest.DatabaseID, new System.Collections.Concurrent.ConcurrentDictionary<string, object>());
            }

            var msg = new DatabasePrimativeMessage
            {
                Name = db.Name,
                jsonPayload = db.JsonPayload
            };

            var writer = NetworkServer.RentWriter();
            msg.Serialize(writer);
            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.StoreDatabaseChannel, writer.Length);
            peer.Send(writer, BasisNetworkCommons.StoreDatabaseChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
