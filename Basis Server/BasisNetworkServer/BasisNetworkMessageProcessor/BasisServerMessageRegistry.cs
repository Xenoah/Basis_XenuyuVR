using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisPermissions;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;

public delegate void BasisServerMessageHandler(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);

/// <summary>
/// table-driven inbound dispatch。core message は dedicated channel (0-59) に bind し、
/// multiplexed plugin message は 61-63 channel payload から読む ushort id に bind する。
/// hardcoded switch を置き換え、shared constant table を編集せずに handler を追加/削除できるようにする。
/// </summary>
public static class BasisServerMessageRegistry
{
    private static readonly BasisServerMessageHandler[] CoreHandlers = new BasisServerMessageHandler[BasisNetworkCommons.TotalChannels];
    private static readonly ConcurrentDictionary<ushort, BasisServerMessageHandler> PluginHandlers = new();
    private static readonly ConcurrentDictionary<ushort, SerializableBasis.BasisMessageDescriptor> PluginDescriptors = new();
    private static readonly ConcurrentDictionary<int, HashSet<ushort>> Subscriptions = new();

    /// <summary>plugin id は core channel range (0-63) より上から始まるため、flat manifest/subscription space で core id と衝突しない。</summary>
    private const ushort PluginIdBase = 64;
    private static int _nextPluginId = PluginIdBase;
    private static readonly ConcurrentDictionary<string, ushort> PluginIdsByName = new();
    private static readonly object _pluginIdLock = new object();

    // supplied manifest は plugin が (un)register されたときだけ変わる。これは startup 時に想定される。
    // per-connect SendSupplyTo が allocation-free になるよう、atomically-swapped snapshot として cache する。
    private sealed class SupplySnapshot
    {
        public readonly int Version;
        public readonly SerializableBasis.BasisMessageDescriptor[] Descriptors;
        public SupplySnapshot(int version, SerializableBasis.BasisMessageDescriptor[] descriptors)
        {
            Version = version;
            Descriptors = descriptors;
        }
    }
    private static volatile SupplySnapshot _supplySnapshot;
    private static int _supplyVersion;

    static BasisServerMessageRegistry()
    {
        RegisterCoreHandlers();
    }

    /// <summary>static constructor を強制実行する (core handler を register)。繰り返し呼んでも安全。</summary>
    public static void EnsureInitialized() { }

    public static void RegisterCore(byte channel, BasisServerMessageHandler handler) => CoreHandlers[channel] = handler;

    public static BasisServerMessageHandler ResolveCore(byte channel) => CoreHandlers[channel];

    /// <summary>multiplexed plugin message id (channel 61-63 上で運ばれる) を handler に bind する。manifest では advertise されないため、descriptor overload を優先する。</summary>
    public static void RegisterPlugin(ushort id, BasisServerMessageHandler handler) => PluginHandlers[id] = handler;

    /// <summary>plugin message を bind し、client が name で subscribe できるよう supplied manifest で advertise する。</summary>
    public static void RegisterPlugin(SerializableBasis.BasisMessageDescriptor descriptor, BasisServerMessageHandler handler)
    {
        PluginHandlers[descriptor.Id] = handler;
        PluginDescriptors[descriptor.Id] = descriptor;
        InvalidateSupply();
    }

    /// <summary>plugin message handler と manifest descriptor を削除する。handler が bind されていた場合 true を返す。</summary>
    public static bool UnregisterPlugin(ushort id)
    {
        PluginDescriptors.TryRemove(id, out _);
        bool removed = PluginHandlers.TryRemove(id, out _);
        InvalidateSupply();
        return removed;
    }

    /// <summary>
    /// plugin message を name で register し、auto-assigned id を割り当て、manifest で advertise して handler を bind する。
    /// assigned id を返す。id は PluginIdBase から registration order で割り当てられるため、
    /// restart 間で stable id にするには deterministic order で plugin を register する。
    /// </summary>
    public static ushort RegisterServerPlugin(string name, DeliveryMethod delivery, BasisServerMessageHandler handler, byte version = 1, SerializableBasis.BasisMessageFlags extraFlags = SerializableBasis.BasisMessageFlags.None)
    {
        ushort id;
        lock (_pluginIdLock)
        {
            if (!PluginIdsByName.TryGetValue(name, out id))
            {
                id = (ushort)_nextPluginId++;
                PluginIdsByName[name] = id;
            }
        }
        SerializableBasis.BasisMessageDescriptor descriptor = new SerializableBasis.BasisMessageDescriptor
        {
            Id = id,
            Version = version,
            Channel = BasisNetworkCommons.GetPluginChannelForDelivery(delivery),
            Flags = (byte)(SerializableBasis.BasisMessageFlags.Multiplexed | extraFlags),
            Name = name,
        };
        PluginHandlers[id] = handler;
        PluginDescriptors[id] = descriptor;
        InvalidateSupply();
        return id;
    }

    /// <summary>plugin に割り当てられた message id を name で lookup する。</summary>
    public static bool TryGetPluginId(string name, out ushort id) => PluginIdsByName.TryGetValue(name, out id);

    /// <summary>
    /// plugin message を name で peer へ送る。id を prepend し、descriptor の channel を使う。
    /// id に subscribe していない peer は skip する。plugin が unknown または skip された場合 false を返す。
    /// </summary>
    public static bool SendToPeer(NetPeer peer, string name, Action<NetDataWriter> writePayload)
    {
        if (!PluginIdsByName.TryGetValue(name, out ushort id) || !PluginDescriptors.TryGetValue(id, out SerializableBasis.BasisMessageDescriptor descriptor))
        {
            return false;
        }
        if (!IsSubscribed(peer.Id, id))
        {
            return false;
        }
        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(id);
        writePayload?.Invoke(writer);
        BasisNetworkStatistics.RecordOutbound(descriptor.Channel, writer.Length);
        NetworkServer.TrySend(peer, writer, descriptor.Channel, BasisNetworkCommons.GetDeliveryForPluginChannel(descriptor.Channel));
        NetworkServer.ReturnWriter(writer);
        return true;
    }

    /// <summary>core catalog と registered plugin descriptor の集合。各 client に供給される manifest。plugin が (un)register されるまで cache される。</summary>
    public static SerializableBasis.BasisMessageDescriptor[] BuildSupply()
    {
        int version = _supplyVersion;
        SupplySnapshot snapshot = _supplySnapshot;
        if (snapshot != null && snapshot.Version == version)
        {
            return snapshot.Descriptors;
        }

        SerializableBasis.BasisMessageDescriptor[] core = SerializableBasis.BasisMessageCatalog.BuildCore();
        SerializableBasis.BasisMessageDescriptor[] result;
        if (PluginDescriptors.IsEmpty)
        {
            result = core;
        }
        else
        {
            List<SerializableBasis.BasisMessageDescriptor> combined = new List<SerializableBasis.BasisMessageDescriptor>(core.Length + PluginDescriptors.Count);
            combined.AddRange(core);
            foreach (KeyValuePair<ushort, SerializableBasis.BasisMessageDescriptor> kvp in PluginDescriptors)
            {
                combined.Add(kvp.Value);
            }
            result = combined.ToArray();
        }

        _supplySnapshot = new SupplySnapshot(version, result);
        return result;
    }

    /// <summary>plugin の (un)register 後に cached manifest を invalidate する。</summary>
    private static void InvalidateSupply() => System.Threading.Interlocked.Increment(ref _supplyVersion);

    /// <summary>registry manifest を peer へ送る (RegistryControlChannel, RegistrySub_Supply)。connect ごとに一度呼ぶ。</summary>
    public static void SendSupplyTo(NetPeer peer)
    {
        SerializableBasis.BasisMessageSupply supply = new SerializableBasis.BasisMessageSupply { Descriptors = BuildSupply() };
        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(BasisNetworkCommons.RegistrySub_Supply);
        supply.Serialize(writer);
        BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.RegistryControlChannel, writer.Length);
        NetworkServer.TrySend(peer, writer, BasisNetworkCommons.RegistryControlChannel, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>peer が handle できると報告した message id を記録する (RegistrySub_Subscribe 由来)。</summary>
    public static void SetSubscription(int peerId, ushort[] ids)
    {
        Subscriptions[peerId] = (ids == null || ids.Length == 0) ? new HashSet<ushort>() : new HashSet<ushort>(ids);
    }

    /// <summary>peer がこの message id に subscribe している場合 true。peer が subscription を一度も送っていない場合も true (送るまでは filtering しない)。</summary>
    public static bool IsSubscribed(int peerId, ushort id)
    {
        return !Subscriptions.TryGetValue(peerId, out HashSet<ushort> set) || set.Contains(id);
    }

    /// <summary>disconnect 時に peer の subscription record を drop する。</summary>
    public static void ClearSubscription(int peerId) => Subscriptions.TryRemove(peerId, out _);

    /// <summary>
    /// plugin channel payload の先頭 ushort message id を読み、dispatch する。
    /// id が unknown、または payload が短すぎて id を含められない場合は false を返し、
    /// recycle と error-count を caller に任せる。
    /// </summary>
    public static bool DispatchPlugin(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (!reader.TryGetUShort(out ushort id))
        {
            return false;
        }
        if (PluginHandlers.TryGetValue(id, out BasisServerMessageHandler handler))
        {
            handler(peer, reader, channel, deliveryMethod);
            return true;
        }
        return false;
    }

    private static void RegisterCoreHandlers()
    {
        RegisterCore(BasisNetworkCommons.ShoutVoiceChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleShoutVoiceMessage(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.AuthIdentityChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleAuth(reader, peer)); // recycles inside

        BasisServerMessageHandler avatarMovement = (peer, reader, channel, dm) =>
            BasisServerReductionSystemEvents.HandleAvatarMovement(reader, peer, channel); // recycles inside
        RegisterCore(BasisNetworkCommons.PlayerAvatarHighChannel, avatarMovement);
        RegisterCore(BasisNetworkCommons.PlayerAvatarHighAdditionalChannel, avatarMovement);

        RegisterCore(BasisNetworkCommons.VoiceChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleVoiceMessage(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.AvatarChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleAvatar(reader, dm, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.SceneChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleScene(reader, dm, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.DirectAvatarServerChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleAvatar(reader, dm, peer, BasisNetworkCommons.DirectAvatarServerChannel)); // recycles inside

        RegisterCore(BasisNetworkCommons.DirectSceneServerChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleScene(reader, dm, peer, BasisNetworkCommons.DirectSceneServerChannel)); // recycles inside

        RegisterCore(BasisNetworkCommons.AvatarChangeMessageChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.SendAvatarMessageToClients(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, (peer, reader, channel, dm) =>
            HandlePermitted(peer, reader, PermNodes.OwnershipTransfer, () =>
                BasisNetworkOwnership.OwnershipTransfer(reader, peer))); // recycles inside

        RegisterCore(BasisNetworkCommons.GetCurrentOwnerRequestChannel, (peer, reader, channel, dm) =>
            HandlePermitted(peer, reader, PermNodes.OwnershipGet, () =>
                BasisNetworkOwnership.OwnershipResponse(reader, peer))); // recycles inside

        RegisterCore(BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, (peer, reader, channel, dm) =>
            HandlePermitted(peer, reader, PermNodes.OwnershipRemove, () =>
                BasisNetworkOwnership.RemoveOwnership(reader, peer))); // recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer, false)); // byte count, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsLargeChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer, true)); // ushort count, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsInvertedChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceiversInverted(reader, peer, false)); // byte count excluded, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsInvertedLargeChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceiversInverted(reader, peer, true)); // ushort count excluded, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsBitfieldChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceiversBitfield(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.netIDAssignChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.NetIDAssign(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.LoadResourceChannel, (peer, reader, channel, dm) =>
        {
            if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string LRuuid))
            {
                BasisServerHandleEvents.LoadResource(reader, peer, LRuuid);
                return;
            }
            BNL.LogError($"User UUID not found for peer: {peer}");
            reader.Recycle();
        });

        RegisterCore(BasisNetworkCommons.UnloadResourceChannel, (peer, reader, channel, dm) =>
        {
            BasisServerHandleEvents.UnloadResource(reader, peer);
            reader.Recycle();
        });

        RegisterCore(BasisNetworkCommons.ModifyResourceChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleModifyResource(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.AdminChannel, (peer, reader, channel, dm) =>
            BasisPlayerModeration.OnAdminMessage(peer, reader)); // recycles inside

        RegisterCore(BasisNetworkCommons.ContentShareChannel, (peer, reader, channel, dm) =>
            BasisNetworkContentShare.HandleContentShareDrop(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.ContentShareCleanupChannel, (peer, reader, channel, dm) =>
            BasisNetworkContentShare.HandleContentShareCleanup(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.ServerBoundChannel, (peer, reader, channel, dm) =>
        {
            BasisServerHandleEvents.OnServerReceived?.Invoke(peer, reader, dm);
            reader.Recycle(); // recycles here
        });

        RegisterCore(BasisNetworkCommons.StoreDatabaseChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleStoreDatabase(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.RequestStoreDatabaseChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleRequestStoreDatabase(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.ServerStatisticsChannel, (peer, reader, channel, dm) =>
        {
            // permission-gated stats。
            if (!TryWithPermission(peer, reader, PermNodes.ServerStats, out _))
            {
                return;
            }

            if (reader.GetBool())
            {
                BNL.Log("requested Server StatisticsChannel");
                BasisNetworkStatistics.IsRecordingData = true;

                ServerStatisticMessage serverStatistic = new ServerStatisticMessage
                {
                    Data = BasisNetworkStatistics.Snapshot.SnapshotResetEncode(true, 6)
                };

                reader.Recycle();

                NetDataWriter writer = NetworkServer.RentWriter();
                serverStatistic.Serialize(writer);
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.ServerStatisticsChannel, writer.Length);
                peer.Send(writer, BasisNetworkCommons.ServerStatisticsChannel, DeliveryMethod.ReliableOrdered);
                NetworkServer.ReturnWriter(writer);
            }
            else
            {
                BasisNetworkStatistics.IsRecordingData = false;
                reader.Recycle();
            }
        });

        RegisterCore(BasisNetworkCommons.ChatChannel, (peer, reader, channel, dm) =>
            BasisNetworkChat.HandleChatMessage(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.CameraPIPStateChannel, (peer, reader, channel, dm) =>
            BasisNetworkPIPCamera.HandlePIPStateChange(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.CameraPIPPositionChannel, (peer, reader, channel, dm) =>
            BasisNetworkPIPCamera.HandlePIPPositionUpdate(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.PreloadReadyChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandlePreloadReady(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.EventsChannel, (peer, reader, channel, dm) =>
            BasisServerEventsRouter.HandleEvent(reader, peer)); // reads event type byte, routes, recycles inside

        RegisterCore(BasisNetworkCommons.P2PChannel, (peer, reader, channel, dm) =>
            BasisServerP2PBroker.HandleP2PMessage(reader, peer)); // reads sub-type byte, routes, recycles inside

        RegisterCore(BasisNetworkCommons.RegistryControlChannel, (peer, reader, channel, dm) =>
        {
            if (reader.TryGetByte(out byte sub) && sub == BasisNetworkCommons.RegistrySub_Subscribe)
            {
                SerializableBasis.BasisMessageSubscribe subscribe = new SerializableBasis.BasisMessageSubscribe();
                subscribe.Deserialize(reader);
                SetSubscription(peer.Id, subscribe.Ids);
            }
            reader.Recycle();
        });
    }

    private static bool TryWithPermission(NetPeer peer, NetPacketReader reader, string permNode, out string uuid)
    {
        if (!NetworkServer.AuthIdentity.NetIDToUUID(peer, out uuid))
        {
            BNL.LogError($"User UUID not found for peer: {peer}");
            reader.Recycle();
            return false;
        }

        // specific node、admin、global wildcard のいずれかを持つ場合は許可する。
        if (PermissionIntegration.HasValidRequirement(uuid, permNode))
        {
            return true;
        }

        BNL.LogError($"Unauthorized access attempt by UUID: {uuid} for {permNode}");
        reader.Recycle();
        return false;
    }

    private static void HandlePermitted(NetPeer peer, NetPacketReader reader, string permNode, Action action)
    {
        if (TryWithPermission(peer, reader, permNode, out _))
        {
            action();
        }
    }
}
