using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BasisNetworkServer
{
    /// <summary>
    /// Linux-only の background sampler。/proc/net/snmp を poll し、
    /// kernel が inbound UDP datagram を drop したとき warn する。
    /// ここでは 2 つの failure mode が見える:
    ///   1) RcvbufErrors increasing  =>  receive thread が socket buffer を十分速く drain できていない。
    ///      対処: MultiSocketCount を上げる (SO_REUSEPORT 経由で port を共有する recv thread を増やす)、
    ///      または kernel buffer を大きくする (sysctl net.core.rmem_max)。
    ///   2) InErrors > RcvbufErrors  =>  saturation とは無関係の checksum/decode-level corruption。
    ///      link/NIC issue を示す。
    /// non-Linux platform では Start() は no-op。
    /// </summary>
    public static class BasisNetworkUdpDropMonitor
    {
        private const string SnmpPath = "/proc/net/snmp";
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

        private static Thread _samplerThread;
        private static volatile bool _running;
        private static long _lastRcvbufErrors = -1;
        private static long _lastInErrors = -1;
        private static long _lastInCsumErrors = -1;

        public static void Start()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            if (_samplerThread != null) return;
            if (!File.Exists(SnmpPath))
            {
                BNL.LogWarning($"[UdpDropMonitor] {SnmpPath} not readable; monitoring disabled");
                return;
            }
            _running = true;
            _lastRcvbufErrors = -1;
            _lastInErrors = -1;
            _lastInCsumErrors = -1;
            _samplerThread = new Thread(Run)
            {
                Name = "UdpDropMonitor",
                IsBackground = true
            };
            _samplerThread.Start();
            BNL.Log("[UdpDropMonitor] Started; sampling /proc/net/snmp every 10s");
        }

        public static void Stop()
        {
            _running = false;
            _samplerThread = null;
        }

        private static void Run()
        {
            while (_running)
            {
                try { Sample(); }
                catch (Exception ex) { BNL.LogError($"[UdpDropMonitor] {ex.Message}"); }
                Thread.Sleep(SampleInterval);
            }
        }

        private static void Sample()
        {
            if (!TryReadSnmpUdp(out long rcvbufErrors, out long inErrors, out long inCsumErrors)) return;

            // first sample は baseline を確立するだけ。その後の delta だけが意味を持つ。
            if (_lastRcvbufErrors >= 0)
            {
                long droppedBuf = rcvbufErrors - _lastRcvbufErrors;
                long deltaIn = inErrors - _lastInErrors;
                long deltaCsum = inCsumErrors - _lastInCsumErrors;

                if (droppedBuf > 0)
                {
                    BNL.LogWarning(
                        $"[UdpDropMonitor] Kernel dropped {droppedBuf} UDP packets in last {SampleInterval.TotalSeconds:F0}s " +
                        $"(RcvbufErrors). Receive thread is saturated -- raise MultiSocketCount in litenetlib.xml " +
                        $"or grow sysctl net.core.rmem_max.");
                }

                // InErrors - RcvbufErrors により buffer 以外の drop (checksum、length など) を分離する。
                // これで bad NIC/cable と saturated app を明確に区別できる。
                long otherDrops = deltaIn - droppedBuf;
                if (otherDrops > 0)
                {
                    string detail = deltaCsum > 0 ? $" (InCsumErrors +{deltaCsum})" : "";
                    BNL.LogWarning(
                        $"[UdpDropMonitor] {otherDrops} additional UDP InErrors in last {SampleInterval.TotalSeconds:F0}s{detail}" +
                        $" -- not recv-buffer related; check NIC/link health.");
                }
            }

            _lastRcvbufErrors = rcvbufErrors;
            _lastInErrors = inErrors;
            _lastInCsumErrors = inCsumErrors;
        }

        // /proc/net/snmp は "Udp:" で始まる 2 行を連続して出力する。
        // 1 行目は column 名、2 行目は値。column set は kernel version に依存するため、
        // fixed index ではなく名前で lookup する。
        private static bool TryReadSnmpUdp(out long rcvbufErrors, out long inErrors, out long inCsumErrors)
        {
            rcvbufErrors = 0;
            inErrors = 0;
            inCsumErrors = 0;
            string[] lines;
            try { lines = File.ReadAllLines(SnmpPath); }
            catch { return false; }

            string headerLine = null;
            string dataLine = null;
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].StartsWith("Udp:", StringComparison.Ordinal) &&
                    lines[i + 1].StartsWith("Udp:", StringComparison.Ordinal))
                {
                    headerLine = lines[i];
                    dataLine = lines[i + 1];
                    break;
                }
            }
            if (headerLine == null || dataLine == null) return false;

            string[] headers = headerLine.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            string[] values = dataLine.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (headers.Length != values.Length) return false;

            for (int i = 1; i < headers.Length; i++)
            {
                switch (headers[i])
                {
                    case "RcvbufErrors":
                        long.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out rcvbufErrors);
                        break;
                    case "InErrors":
                        long.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out inErrors);
                        break;
                    case "InCsumErrors":
                        long.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out inCsumErrors);
                        break;
                }
            }
            return true;
        }
    }
}
