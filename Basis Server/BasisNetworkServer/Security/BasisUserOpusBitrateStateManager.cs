using Basis.Network.Core;
using System.Collections.Concurrent;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// user ごとの Opus encoder bitrate (bits per second) admin override。
    /// server は override を netId で保持する。target user の peer が接続中なら、
    /// UserOpusBitrateOverride message を push し、client が live encoder に OPUS_SET_BITRATE を適用する。
    /// 0 は "override なし。client default bitrate を使う" という意味。
    /// peer disconnect 時に state は clear される。
    /// </summary>
    public static class BasisUserOpusBitrateStateManager
    {
        // Opus は 500..512000 bps を受け付ける。voice 用に少し狭い conservative range へ clamp する。
        // 0 は "clear override" sentinel として予約する。
        public const int MinBitrate = 6000;
        public const int MaxBitrate = 510000;

        private static readonly ConcurrentDictionary<int, int> _overrides = new();

        public static bool TryGetBitrate(int netId, out int bitrate) =>
            _overrides.TryGetValue(netId, out bitrate);

        /// <summary>
        /// user の bitrate override を set / clear する。clear するには 0 を渡す。
        /// clamp 後に実際に保存された値を返す (0 = cleared)。
        /// </summary>
        public static int SetBitrate(int netId, int bitrate)
        {
            if (bitrate <= 0)
            {
                _overrides.TryRemove(netId, out _);
                return 0;
            }

            if (bitrate < MinBitrate) bitrate = MinBitrate;
            else if (bitrate > MaxBitrate) bitrate = MaxBitrate;
            _overrides[netId] = bitrate;
            return bitrate;
        }

        public static void ClearForPeer(int netId) => _overrides.TryRemove(netId, out _);

        public static void SendStateToPeer(NetPeer peer)
        {
            int bitrate = TryGetBitrate(peer.Id, out int v) ? v : 0;
            SendOverrideToPeer(peer, bitrate);
        }

        public static void SendOverrideToPeer(NetPeer peer, int bitrate)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.UserOpusBitrateOverride);
                writer.Put(bitrate);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }
    }
}
