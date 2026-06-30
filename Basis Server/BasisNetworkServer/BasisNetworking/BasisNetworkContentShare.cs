using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisPermissions;
using System.Collections.Concurrent;
using System.Linq;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

/// <summary>
/// content share sphere の server-side management。
/// active sphere をすべて追跡し、client への broadcasting を処理する。
/// </summary>
public static class BasisNetworkContentShare
{
    /// <summary>
    /// SphereNetID を key にした active content share sphere すべて。
    /// value は creator player ID を含む full message。
    /// </summary>
    public static ConcurrentDictionary<string, ServerContentShareMessage> ActiveSpheres =
        new ConcurrentDictionary<string, ServerContentShareMessage>();

    /// <summary>
    /// client からの content share drop を処理する。
    /// sphere を保存し、全 client へ broadcast する。
    /// </summary>
    public static void HandleContentShareDrop(NetPacketReader reader, NetPeer peer)
    {
        ContentShareMessage msg = new ContentShareMessage();
        msg.Deserialize(reader);
        reader.Recycle();

        if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.ContentShareCreate))
        {
            return;
        }

        // content type に基づく global lock check。
        // content の lock が on で、かつ peer が matching lockbypass permission を持たない場合 block する。
        bool blocked = false;
        string contentName = "";
        switch (msg.ContentType)
        {
            case ContentShareType.Avatar:
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.AvatarsLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassAvatar);
                contentName = "Avatar";
                break;
            case ContentShareType.Prop:
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.PropsLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassProp);
                contentName = "Prop";
                break;
            case ContentShareType.World:
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.WorldsLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassWorld);
                contentName = "World";
                break;
            case ContentShareType.Server:
                // ContentURL は connection string (address[:port][#password]) を運ぶ。
                // receiver は URL を直接 parse するため、UnlockPassword は意図的に未使用。
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.ServersLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassServer);
                contentName = "Server share";
                break;
            default:
                BNL.LogError($"Unknown content share type {(byte)msg.ContentType} from peer {peer.Id}");
                return;
        }
        if (blocked)
        {
            BNL.Log($"{contentName} content sharing is globally disabled. Rejected from peer {peer.Id}");
            BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(peer, $"{contentName} loading is currently disabled by an admin.");
            return;
        }

        if (ActiveSpheres.Count(kvp => kvp.Value.playerIdMessage.playerID == (ushort)peer.Id) >= BasisNetworkServer.Security.BasisResourceLimitManager.MaxContentSpheresPerPlayer)
        {
            BNL.LogError($"Peer {peer.Id} reached content sphere limit.");
            return;
        }

        string sharerUUID = string.Empty;
        string sharerDisplayName = string.Empty;
        if (BasisSavedState.GetLastPlayerMetaData(peer, out ClientMetaDataMessage sharerMeta))
        {
            sharerUUID = sharerMeta.playerUUID ?? string.Empty;
            sharerDisplayName = sharerMeta.playerDisplayName ?? string.Empty;
        }

        ServerContentShareMessage serverMsg = new ServerContentShareMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)peer.Id
            },
            SharerUUID = sharerUUID,
            SharerDisplayName = sharerDisplayName,
            contentShareMessage = msg
        };

        if (ActiveSpheres.TryAdd(msg.SphereNetID, serverMsg))
        {
            BNL.Log($"Content sphere dropped: {msg.SphereNetID} type={msg.ContentType}");

            NetDataWriter writer = NetworkServer.RentWriter();
            serverMsg.Serialize(writer);

            // sender を含む全 client へ broadcast する。
            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.ContentShareChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
        }
        else
        {
            BNL.LogError($"Content sphere already exists: {msg.SphereNetID}");
        }
    }

    /// <summary>
    /// client からの content share cleanup を処理する。
    /// sphere を削除し、削除を全 client へ broadcast する。
    /// </summary>
    public static void HandleContentShareCleanup(NetPacketReader reader, NetPeer peer)
    {
        ContentShareCleanupMessage msg = new ContentShareCleanupMessage();
        msg.Deserialize(reader);
        reader.Recycle();

        if (!ActiveSpheres.TryGetValue(msg.SphereNetID, out ServerContentShareMessage existing))
        {
            BNL.LogError($"Trying to remove content sphere that does not exist: {msg.SphereNetID}");
            return;
        }
        if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.ContentShareDelete))
        {
            return;
        }
        if (ActiveSpheres.TryRemove(msg.SphereNetID, out _))
        {
            BNL.Log($"Content sphere removed: {msg.SphereNetID}");

            ServerContentShareCleanupMessage serverMsg = new ServerContentShareCleanupMessage
            {
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)peer.Id
                },
                contentShareCleanupMessage = msg
            };

            NetDataWriter writer = NetworkServer.RentWriter();
            serverMsg.Serialize(writer);

            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.ContentShareCleanupChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
        }
    }

    /// <summary>
    /// newly connected peer へ active content share sphere をすべて送る。
    /// </summary>
    public static void SendAllSpheresToPeer(NetPeer newConnection)
    {
        ServerContentShareMessage[] spheres = ActiveSpheres.Values.ToArray();
        if (spheres.Length == 0) return;

        NetDataWriter writer = NetworkServer.RentWriter();
        for (int i = 0; i < spheres.Length; i++)
        {
            writer.Reset();
            spheres[i].Serialize(writer);
            NetworkServer.TrySend(
                newConnection,
                writer,
                BasisNetworkCommons.ContentShareChannel,
                DeliveryMethod.ReliableOrdered
            );
        }
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// disconnecting player が作成した sphere をすべて削除する。
    /// </summary>
    public static void RemovePlayerSpheres(int peerId)
    {
        ushort playerId = (ushort)peerId;
        var toRemove = ActiveSpheres.Where(kvp => kvp.Value.playerIdMessage.playerID == playerId)
                                    .Select(kvp => kvp.Key)
                                    .ToArray();

        foreach (string sphereId in toRemove)
        {
            if (ActiveSpheres.TryRemove(sphereId, out _))
            {
                ContentShareCleanupMessage cleanup = new ContentShareCleanupMessage
                {
                    SphereNetID = sphereId
                };

                ServerContentShareCleanupMessage serverMsg = new ServerContentShareCleanupMessage
                {
                    playerIdMessage = new PlayerIdMessage { playerID = playerId },
                    contentShareCleanupMessage = cleanup
                };

                NetDataWriter writer = NetworkServer.RentWriter();
                serverMsg.Serialize(writer);

                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.ContentShareCleanupChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered
                );
                NetworkServer.ReturnWriter(writer);
            }
        }
    }

    /// <summary>
    /// non-persistent sphere をすべて clear する (server が空になったときに呼ぶ)。
    /// </summary>
    public static void Reset()
    {
        string[] keys = ActiveSpheres.Keys.ToArray();
        foreach (string key in keys)
        {
            ActiveSpheres.TryRemove(key, out _);
        }
    }
}
