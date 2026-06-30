using Basis.Network.Core;

namespace BasisNetworkServer
{
    /// <summary>
    /// 一時的な player chat typing state 用の server-side router。
    /// </summary>
    public static class BasisNetworkHandleChatTyping
    {
        /// <summary>
        /// wire format (in): [byte eventType][bool isTyping]
        /// wire format (out): [byte eventType][ushort senderId][bool isTyping]
        /// </summary>
        public static void HandleEvent(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            bool isTyping;
            try
            {
                isTyping = reader.GetBool();
            }
            finally
            {
                reader.Recycle();
            }

            if (peer.Id < 0 || peer.Id > ushort.MaxValue)
            {
                BNL.LogError($"Cannot broadcast chat typing state for peer id {peer.Id}: outside ushort wire format.");
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                writer.Put(eventType);
                writer.Put((ushort)peer.Id);
                writer.Put(isTyping);

                NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.Sequenced);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }
    }
}
