using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// non-admin player が scale できる avatar eye height の server-defined minimum/maximum (metres)。
    /// boot 時に Configuration から seed され、GlobalGetAvatarScaleLimits 経由で client へ push されるため、
    /// client は avatar scale をこの範囲へ clamp する。admin (basis.moderation.globallock) は client-side clamp を bypass する。
    /// admin は range を live 変更でき、新しい値は config.xml へ persist されて broadcast される。
    /// </summary>
    public static class BasisAvatarScaleLimitManager
    {
        private const float DefaultMinMeters = 0.1f;
        private const float DefaultMaxMeters = 100f;
        private const float AbsoluteFloor = 0.01f;
        private const float AbsoluteCeiling = 1000f;

        private static float _minMeters = DefaultMinMeters;
        private static float _maxMeters = DefaultMaxMeters;

        public static float MinMeters => Interlocked.CompareExchange(ref _minMeters, 0f, 0f);
        public static float MaxMeters => Interlocked.CompareExchange(ref _maxMeters, 0f, 0f);

        public static void InitializeFromConfig(Configuration config)
        {
            SetLimits(config.MinAvatarEyeHeightMeters, config.MaxAvatarEyeHeightMeters);
        }

        /// <summary>sanitize し、min &lt;= max になるよう order して set し、どちらかの bound が実際に変わったかを返す。</summary>
        public static bool SetLimits(float minMeters, float maxMeters)
        {
            Sanitize(ref minMeters, ref maxMeters);
            float prevMin = Interlocked.Exchange(ref _minMeters, minMeters);
            float prevMax = Interlocked.Exchange(ref _maxMeters, maxMeters);
            return prevMin != minMeters || prevMax != maxMeters;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetAvatarScaleLimits);
                writer.Put(MinMeters);
                writer.Put(MaxMeters);
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
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetAvatarScaleLimits);
                writer.Put(MinMeters);
                writer.Put(MaxMeters);
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

        private static void Sanitize(ref float minMeters, ref float maxMeters)
        {
            if (float.IsNaN(minMeters) || float.IsInfinity(minMeters) || minMeters <= 0f) minMeters = DefaultMinMeters;
            if (float.IsNaN(maxMeters) || float.IsInfinity(maxMeters) || maxMeters <= 0f) maxMeters = DefaultMaxMeters;
            if (minMeters < AbsoluteFloor) minMeters = AbsoluteFloor;
            if (maxMeters > AbsoluteCeiling) maxMeters = AbsoluteCeiling;
            if (maxMeters < minMeters) maxMeters = minMeters;
        }
    }
}
