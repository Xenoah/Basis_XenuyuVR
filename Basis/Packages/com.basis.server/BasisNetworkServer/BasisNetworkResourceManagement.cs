using Basis.Network.Core;
using BasisPermissions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

public static class BasisNetworkResourceManagement
{
    public static ConcurrentDictionary<string, LocalLoadResource> UshortNetworkDatabase = new ConcurrentDictionary<string, LocalLoadResource>();
    public static void Reset()
    {
        LocalLoadResource[] resourceArray = UshortNetworkDatabase.Values.ToArray();
        int length = resourceArray.Length;

        for (int index = 0; index < length; index++)
        {
            LocalLoadResource llr = resourceArray[index];

            if (!llr.Persist)
            {
                // unload resource message を準備して送る。
                UnLoadResource unloadResource = new UnLoadResource
                {
                    Mode = llr.Mode,
                    LoadedNetID = llr.LoadedNetID
                };

                NetDataWriter writer = NetworkServer.RentWriter();
                unloadResource.Serialize(writer);
                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.UnloadResourceChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered
                );
                NetworkServer.ReturnWriter(writer);

                // non-persistent resource を database から削除する。
                UshortNetworkDatabase.Remove(llr.LoadedNetID,out LocalLoadResource Resource);
            }
        }
    }
    public static void RemovePeerResources(string uuid)
    {
        if (string.IsNullOrEmpty(uuid)) return;
        LocalLoadResource[] resourceArray = UshortNetworkDatabase.Values.ToArray();
        int length = resourceArray.Length;
        for (int index = 0; index < length; index++)
        {
            LocalLoadResource llr = resourceArray[index];
            if (llr.Persist || llr.UUIDOfCreator != uuid)
            {
                continue;
            }
            UnLoadResource unloadResource = new UnLoadResource
            {
                Mode = llr.Mode,
                LoadedNetID = llr.LoadedNetID
            };
            NetDataWriter writer = NetworkServer.RentWriter();
            unloadResource.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.UnloadResourceChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
            UshortNetworkDatabase.Remove(llr.LoadedNetID, out LocalLoadResource Resource);
        }
    }
    public static void SendOutAllResources(NetPeer NewConnection)
    {
        LocalLoadResource[] Resource = UshortNetworkDatabase.Values.ToArray();
        if (Resource != null)
        {
            int length = Resource.Length;
            NetDataWriter Writer = NetworkServer.RentWriter();
            for (int Index = 0; Index < length; Index++)
            {
                Writer.Reset();
                LocalLoadResource LLR = Resource[Index];

                // synchronized resource (LoadStrategy == 2) では、session がまだ active か確認する。
                // すでに完了済みなら immediate (0) として送信し、late joiner が存在しない spawn signal を
                // 待たずにすぐ spawn できるようにする。まだ active なら late joiner を session に追加し、
                // synchronized load に参加させる。
                if (LLR.LoadStrategy == 2)
                {
                    if (BasisNetworkPreloadResourceManagement.ActiveSessions.TryGetValue(LLR.LoadedNetID, out var session))
                    {
                        // session は進行中。late joiner を peer count に追加する。
                        session.TotalPeerCount++;
                    }
                    else
                    {
                        // session は完了済み。immediate load として送る。
                        LLR.LoadStrategy = 0;
                    }
                }

                LLR.Serialize(Writer);
                NetworkServer.TrySend(NewConnection, Writer, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(Writer);
        }
    }
    // predownload broadcast: connected client 全員に、今 bundle を disc cache するよう伝える。
    // UshortNetworkDatabase には意図的に追加しない。loaded resource ではないため、
    // SendOutAllResources によって late joiner へ replay されず、何も spawn しない。
    public static void PredownloadResource(LocalLoadResource LocalLoadResource)
    {
        NetDataWriter Writer = NetworkServer.RentWriter();
        LocalLoadResource.Serialize(Writer);
        BNL.Log("Broadcasting predownload for " + LocalLoadResource.CombinedURL);
        NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.LoadResourceChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(Writer);
    }
    public static void LoadResource(LocalLoadResource LocalLoadResource)
    {
        if (UshortNetworkDatabase.ContainsKey(LocalLoadResource.LoadedNetID) == false)
        {
            NetDataWriter Writer = NetworkServer.RentWriter();
            LocalLoadResource.Serialize(Writer);
            if (UshortNetworkDatabase.TryAdd(LocalLoadResource.LoadedNetID, LocalLoadResource))
            {
                BNL.Log("Adding Object " + LocalLoadResource.LoadedNetID);
                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.LoadResourceChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                BNL.LogError("Try Add Failed Already have Object Loaded With " + LocalLoadResource.LoadedNetID);
            }
            NetworkServer.ReturnWriter(Writer);
        }
        else
        {
            BNL.LogError("Already have Object Loaded With " + LocalLoadResource.LoadedNetID);
        }
    }
    // server-authoritative path。caller (REST API など) は game peer より高い level で
    // すでに authenticated なので、IsAdminLocked peer check を skip する。
    // resource が見つからない場合 (TryRemove が atomic に fail) は false を返す。
    public static bool UnloadResource(UnLoadResource unLoadResource)
    {
        if (!UshortNetworkDatabase.TryRemove(unLoadResource.LoadedNetID, out _))
        {
            BNL.LogError($"[Server] Trying to unload an object that does not exist: {unLoadResource.LoadedNetID}");
            return false;
        }

        NetDataWriter writer = NetworkServer.RentWriter();
        unLoadResource.Serialize(writer);
        BNL.Log("Removing Object (server) " + unLoadResource.LoadedNetID);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.UnloadResourceChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
        return true;
    }

    public static void UnloadResource(UnLoadResource unLoadResource, NetPeer peer)
    {
        if (!UshortNetworkDatabase.TryGetValue(unLoadResource.LoadedNetID, out LocalLoadResource resource))
        {
            BNL.LogError($"Trying to unload an object that does not exist! ID Provided was [{unLoadResource.LoadedNetID}]");
            return;
        }

        // admin lock validation。
        if (resource.IsAdminLocked && !PermissionIntegration.HasValidRequirement(peer, PermNodes.protection))
        {
            return;
        }

        // validation 後にだけ remove する。
        if (!UshortNetworkDatabase.TryRemove(unLoadResource.LoadedNetID, out _))
        {
            BNL.LogError($"Failed to remove object [{unLoadResource.LoadedNetID}] after validation.");
            return;
        }

        NetDataWriter writer = NetworkServer.RentWriter();
        unLoadResource.Serialize(writer);

        BNL.Log("Removing Object " + unLoadResource.LoadedNetID);

        NetworkServer.BroadcastMessageToClients(
            writer,
            BasisNetworkCommons.UnloadResourceChannel,
            NetworkServer.PeerSnapshot,
            DeliveryMethod.ReliableOrdered
        );
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// already-spawned resource の server-authoritative な "Static" flag を toggle する。
    /// item creator または moderator (protection permission) だけが変更できる。
    /// 成功時は新しい state を保存し、すべての client へ rebroadcast する
    /// (record 全体を serialize する <see cref="SendOutAllResources"/> 経由で late joiner にも replay される)。
    /// </summary>
    public static void SetStatic(ModifyResource modifyResource, NetPeer peer)
    {
        if (!UshortNetworkDatabase.TryGetValue(modifyResource.LoadedNetID, out LocalLoadResource resource))
        {
            BNL.LogError($"Trying to modify an object that does not exist! ID Provided was [{modifyResource.LoadedNetID}]");
            return;
        }

        // admin-lock は frozen を含意する。"admin-locked but movable" は request できない。
        bool targetAdminLocked = modifyResource.StaticAdminLocked;
        bool targetStatic = modifyResource.Static || targetAdminLocked;

        // authorize。admin tier に触れる transition (入る/出る) には moderator が必要。
        // item creator は admin lock を set/clear できない。plain static toggle
        // (non-admin tier) では creator も許可する。
        bool involvesAdminTier = resource.StaticAdminLocked || targetAdminLocked;
        bool isModerator = PermissionIntegration.HasValidRequirement(peer, PermNodes.protection);
        bool isCreator = NetworkServer.AuthIdentity.NetIDToUUID(peer, out string requesterUuid)
            && !string.IsNullOrEmpty(resource.UUIDOfCreator)
            && requesterUuid == resource.UUIDOfCreator;
        bool allowed = involvesAdminTier ? isModerator : (isCreator || isModerator);
        if (!allowed)
        {
            return;
        }

        // 何も変わらない場合は no-op にし、network spam を避ける。
        if (resource.Static == targetStatic && resource.StaticAdminLocked == targetAdminLocked)
        {
            return;
        }

        // LocalLoadResource は value type なので、copy を mutate して書き戻す。
        resource.Static = targetStatic;
        resource.StaticAdminLocked = targetAdminLocked;
        UshortNetworkDatabase[modifyResource.LoadedNetID] = resource;

        // 全 client が resolved state + routing で一致するよう、broadcast を normalize する。
        modifyResource.Static = targetStatic;
        modifyResource.StaticAdminLocked = targetAdminLocked;
        modifyResource.Mode = resource.Mode;

        NetDataWriter writer = NetworkServer.RentWriter();
        modifyResource.Serialize(writer);
        BNL.Log($"Set Static={targetStatic} AdminLocked={targetAdminLocked} on Object {modifyResource.LoadedNetID}");
        NetworkServer.BroadcastMessageToClients(
            writer,
            BasisNetworkCommons.ModifyResourceChannel,
            NetworkServer.PeerSnapshot,
            DeliveryMethod.ReliableOrdered
        );
        NetworkServer.ReturnWriter(writer);
    }
}
