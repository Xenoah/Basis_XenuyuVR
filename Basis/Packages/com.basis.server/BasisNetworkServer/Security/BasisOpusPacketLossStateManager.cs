using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Opus encoder の in-band FEC aggressiveness 用 runtime-only server toggle。
    /// admin が 0..100 percentage を push し、server は connected client 全員へ broadcast する。
    /// client は active encoder に OPUS_SET_PACKET_LOSS_PERC で適用する。
    /// </summary>
    public static class BasisOpusPacketLossStateManager
    {
        // 0..100 に clamp する。client と admin handler が race する可能性があるため Interlocked を使う。
        private static int _packetLossPercent = 10;

        public static int PacketLossPercent => Interlocked.CompareExchange(ref _packetLossPercent, 0, 0);

        /// <summary>clamp して set し、値が実際に変わったかを返す。</summary>
        public static bool SetPacketLossPercent(int percent)
        {
            if (percent < 0) percent = 0;
            else if (percent > 100) percent = 100;
            int previous = Interlocked.Exchange(ref _packetLossPercent, percent);
            return previous != percent;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusPacketLossState);
                writer.Put((byte)PacketLossPercent);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        public static void BroadcastState()
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusPacketLossState);
                writer.Put((byte)PacketLossPercent);
                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.AdminChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }
    }
}
