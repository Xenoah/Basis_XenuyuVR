using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;

namespace BasisNetworkServer
{
    /// <summary>
    /// <see cref="BasisNetworkCommons.EventsChannel"/> 上で
    /// <see cref="BasisNetworkCommons.EventType_PlayerTempBlock"/> sub-byte として送られる、
    /// session-scoped な "temp block" notification の server-side router。
    ///
    /// user が別 player に対する local block を切り替えると、client は temp-block message を送る。
    /// server は payload を sender の peer id で書き換え、target peer のみに転送する。
    /// これにより、どちらの端にも state を永続化せず、target 側 client で block を mirror できる。
    /// </summary>
    public static class BasisNetworkHandleTempBlock
    {
        /// <summary>
        /// wire format (in): [byte eventType][ushort targetID][bool isBlocked]
        /// wire format (out to target): [byte eventType][ushort senderID][bool isBlocked]
        /// </summary>
        public static void HandleEvent(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            ushort targetId = reader.GetUShort();
            bool isBlocked = reader.GetBool();
            reader.Recycle();

            if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out NetPeer targetPeer))
            {
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);
            writer.Put((ushort)peer.Id);
            writer.Put(isBlocked);

            NetworkServer.TrySend(targetPeer, writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
