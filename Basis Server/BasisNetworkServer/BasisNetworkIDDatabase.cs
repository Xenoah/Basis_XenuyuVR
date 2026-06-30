using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkCore
{
    public static class BasisNetworkIDDatabase
    {
        public static ConcurrentDictionary<string, ushort> UshortNetworkDatabase = new ConcurrentDictionary<string, ushort>();
        private static int counter = -1; // 最初の increment が 0 になるよう -1 から始める。
        public static void AddOrFindNetworkID(NetPeer NetPeer, string UniqueStringID)
        {
            if (UshortNetworkDatabase.TryGetValue(UniqueStringID, out ushort Value)) // 基本的には起こらない想定。
            {
                // 既知の ID なので、その player に返すだけでよい。
                ServerNetIDMessage SNIM = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { playerID = UniqueStringID },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = Value }
                };
                NetDataWriter Writer = NetworkServer.RentWriter();
                SNIM.Serialize(Writer);
                NetworkServer.TrySend(NetPeer, Writer, BasisNetworkCommons.netIDAssignChannel, DeliveryMethod.ReliableOrdered);
                NetworkServer.ReturnWriter(Writer);
                BNL.Log($"Sent existing NetID ({Value}) for {UniqueStringID} to peer {NetPeer.Address}");
            }
            else
            {
                // 新しい ID を割り当てることを記録する。
                BNL.Log($"No existing ID found for {UniqueStringID}. Assigning a new ID.");

                // thread-safe increment で新しい unique ushort ID を生成する。
                int newCounter = Interlocked.Increment(ref counter);

                // ushort range を超えていないか確認する。
                if (newCounter > ushort.MaxValue)
                {
                    Interlocked.Decrement(ref counter); // roll back する。
                    string errorMessage = $"Error: Cannot assign a new NetID for {UniqueStringID}. Maximum ID limit of {ushort.MaxValue} reached.";
                    BNL.Log(errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }

                ushort newID = (ushort)newCounter;

                // database に追加する。
                UshortNetworkDatabase[UniqueStringID] = newID;
                BNL.Log($"New ID {newID} assigned to {UniqueStringID}");

                // request 元 peer に通知し、他 peer へ broadcast する。
                ServerNetIDMessage SUIMA = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { playerID = UniqueStringID },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = newID }
                };
                NetDataWriter Writer = NetworkServer.RentWriter();
                SUIMA.Serialize(Writer);

                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.netIDAssignChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                NetworkServer.ReturnWriter(Writer);
                BNL.Log($"Broadcasted new ID ({newID}) for {UniqueStringID} to all connected peers.");
            }
        }

        public static bool GetAllNetworkID(out List<ServerNetIDMessage> ServerUniqueIDMessages)
        {
            ServerUniqueIDMessages = new List<ServerNetIDMessage>();
            foreach (KeyValuePair<string, ushort> pair in UshortNetworkDatabase)
            {
                ServerNetIDMessage SUIM = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { playerID = pair.Key },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = pair.Value }
                };
                ServerUniqueIDMessages.Add(SUIM);
            }
            int Count = ServerUniqueIDMessages.Count;
            return Count != 0;
        }
        public static void RemoveUshortNetworkID(ushort netID)
        {
            BNL.Log($"Attempting to remove NetID: {netID}");
            // value (ushort ID) に基づいて削除する。
            var itemToRemove = UshortNetworkDatabase.FirstOrDefault(kvp => kvp.Value == netID);
            if (!string.IsNullOrEmpty(itemToRemove.Key))
            {
                if (UshortNetworkDatabase.TryRemove(itemToRemove.Key, out _))
                {
                    BNL.Log($"Successfully removed NetID: {netID} associated with UniqueStringID: {itemToRemove.Key}");
                }
                else
                {
                    BNL.Log($"Failed to remove NetID: {netID} (concurrent operation may have interfered)");
                }
            }
            else
            {
                BNL.Log($"NetID {netID} not found in the database.");
            }
        }

        public static void Reset()
        {
            BNL.Log("Resetting BasisNetworkIDDatabase...");
            UshortNetworkDatabase.Clear();
            Interlocked.Exchange(ref counter, -1);
            BNL.Log("Database reset complete. Counter set to -1.");
        }
    }
}
