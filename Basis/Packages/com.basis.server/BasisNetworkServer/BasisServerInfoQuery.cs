using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace BasisServerHandle
{
    /// <summary>
    /// 未接続の "server info" probe を扱う。LiteNetLib UDP 版の
    /// Minecraft server-list-ping に相当する。client は小さな query packet を
    /// server の listen port に送り、公開 server name、現在/最大 player 数、MOTD、
    /// RTT 計測用の元 nonce を受け取る。
    ///
    /// wire format (little-endian):
    ///   Query:    [u32 ServerInfoQueryMagic][u16 protoVersion][u16 nonce][padding: 合計 ServerInfoMinRequestBytes 以上]
    ///   Response: [u32 ServerInfoResponseMagic][u16 protoVersion][u16 nonce]
    ///             [u16 online][u16 max][string name][string motd]
    ///
    /// DDoS 保護:
    ///   1. <b>minimum request size</b>: 増幅率を最大化したい reflection attacker が
    ///      spoof しがちな小さすぎる packet は、処理前に破棄する。
    ///      client は query を padding し、response が request より大きくならないようにするため、
    ///      amplification factor は &lt; 1 になる。
    ///   2. <b>global token bucket</b>: source に関係なく、server が返す response-per-second の
    ///      合計に上限をかける。上の保護をすべて迂回されても worst-case outbound bandwidth を抑える。
    ///   3. <b>bounded per-IP throttle</b>: IP ごとに <see cref="MinIntervalMs"/> あたり 1 response。
    ///      tracking map に上限を設け、spoofed-IP flood で memory を使い尽くされないようにする。
    /// </summary>
    public static class BasisServerInfoQuery
    {
        // --- IP ごとの throttle ---
        // window ごとに IP あたり 1 response。spoofed source IP には効かない
        // (下の global bucket が担当) が、単一 client に response budget を独占されないようにする。
        private const int MinIntervalMs = 500;
        // spoofed-IP flood 時の memory を抑える。dict がこのサイズを超えたら消去する。
        // 一括 reset は荒いが、上限付きで lock-free。
        private const int MaxTrackedIps = 4096;
        private static readonly ConcurrentDictionary<IPAddress, long> _lastSeen = new();

        // --- global token bucket ---
        // 全 source 合計の responses/sec に上限をかける。384-byte response で 100 rps なら
        // worst-case outbound は約 38 KB/s で、無視できる程度に小さい。
        private const double GlobalRefillTokensPerSecond = 100.0;
        private const double GlobalBucketCapacity = 200.0; // burst budget
        private static readonly object _bucketLock = new();
        private static double _tokens = GlobalBucketCapacity;
        private static long _lastRefillTicks;

        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        public static void Subscribe()
        {
            NetworkServer.Listener.NetworkReceiveUnconnectedEvent += HandleQuery;
        }

        public static void Unsubscribe()
        {
            NetworkServer.Listener.NetworkReceiveUnconnectedEvent -= HandleQuery;
        }

        private static void HandleQuery(IPEndPoint remoteEndPoint, NetPacketReader reader)
        {
            try
            {
                // Layer 1: minimum request size。最初に小さすぎる packet を破棄する。
                // これは最も安い amplification 材料であり、ServerInfoMinRequestBytes より
                // 小さいものを送る正当な理由はない。
                int totalBytes = reader.AvailableBytes;
                if (totalBytes < BasisNetworkCommons.ServerInfoMinRequestBytes)
                {
                    reader.Recycle(true);
                    return;
                }

                if (totalBytes < 8)
                {
                    reader.Recycle(true);
                    return;
                }

                uint magic = reader.GetUInt();
                if (magic != BasisNetworkCommons.ServerInfoQueryMagic)
                {
                    reader.Recycle(true);
                    return;
                }

                ushort _protoVersion = reader.GetUShort();
                ushort nonce = reader.GetUShort();
                reader.Recycle(true);

                // Layer 2: IP ごとの cooldown。
                if (!ShouldRespondPerIp(remoteEndPoint.Address))
                    return;

                // Layer 3: global response-rate cap。
                if (!TryConsumeGlobalToken())
                    return;

                Configuration cfg = NetworkServer.Configuration;
                int online = NetworkServer.AuthenticatedPeers.Count;
                int max = cfg != null ? cfg.PeerLimit : 0;
                string serverName = cfg?.ServerName ?? string.Empty;
                string motd = cfg?.ServerMotd ?? string.Empty;

                NetDataWriter writer = NetworkServer.RentWriter();
                try
                {
                    writer.Put(BasisNetworkCommons.ServerInfoResponseMagic);
                    writer.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
                    writer.Put(nonce);
                    writer.Put((ushort)Math.Min(online, ushort.MaxValue));
                    writer.Put((ushort)Math.Min(Math.Max(max, 0), ushort.MaxValue));
                    writer.Put(serverName, BasisNetworkCommons.ServerInfoNameMaxLength);
                    writer.Put(motd, BasisNetworkCommons.ServerInfoMotdMaxLength);
                    NetworkServer.Server.SendUnconnectedMessage(writer, remoteEndPoint);
                }
                finally
                {
                    NetworkServer.ReturnWriter(writer);
                }
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"ServerInfoQuery failed for {remoteEndPoint}: {ex.Message}");
            }
        }

        private static bool ShouldRespondPerIp(IPAddress address)
        {
            // 固有 source IP の flood で dict が埋まっているなら消去する。荒いが上限付きで、
            // LRU の eviction ごとの allocation cost を避けられる。最悪でも消された entry が
            // cooldown を失うだけで、global bucket は引き続き効いているので許容できる。
            if (_lastSeen.Count > MaxTrackedIps)
            {
                _lastSeen.Clear();
            }

            long nowMs = _clock.ElapsedMilliseconds;
            long previous = _lastSeen.GetOrAdd(address, 0L);
            if (previous != 0 && nowMs - previous < MinIntervalMs) return false;
            _lastSeen[address] = nowMs;
            return true;
        }

        private static bool TryConsumeGlobalToken()
        {
            lock (_bucketLock)
            {
                long nowTicks = _clock.ElapsedTicks;
                if (_lastRefillTicks == 0) _lastRefillTicks = nowTicks;

                double secondsElapsed = (nowTicks - _lastRefillTicks) / (double)Stopwatch.Frequency;
                if (secondsElapsed > 0)
                {
                    _tokens = Math.Min(GlobalBucketCapacity, _tokens + secondsElapsed * GlobalRefillTokensPerSecond);
                    _lastRefillTicks = nowTicks;
                }

                if (_tokens < 1.0) return false;
                _tokens -= 1.0;
                return true;
            }
        }
    }
}
