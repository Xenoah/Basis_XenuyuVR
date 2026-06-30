using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Opus frame duration 用の runtime-only server toggle。
    /// admin は 20 または 40 (milliseconds) を push し、server がそれを保存して connected client 全員へ broadcast する。
    /// client はそれに応じて encoder packing を切り替える。
    /// Opus として有効な他の frame size (2.5, 5, 10, 60) は拒否する。
    /// この codebase で有用なのは 20/40 のみ。
    /// </summary>
    public static class BasisOpusFrameDurationStateManager
    {
        public const int DefaultMs = 20;
        private static int _frameDurationMs = DefaultMs;

        public static int FrameDurationMs => Interlocked.CompareExchange(ref _frameDurationMs, 0, 0);

        public static bool IsAcceptedDuration(int ms) => ms == 20 || ms == 40;

        /// <summary>frame duration を set する (20 または 40 ms のみ)。変更された場合 true を返す。</summary>
        public static bool SetFrameDurationMs(int ms)
        {
            if (!IsAcceptedDuration(ms)) ms = DefaultMs;
            int previous = Interlocked.Exchange(ref _frameDurationMs, ms);
            return previous != ms;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusFrameDurationState);
                writer.Put((byte)FrameDurationMs);
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
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusFrameDurationState);
                writer.Put((byte)FrameDurationMs);
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
