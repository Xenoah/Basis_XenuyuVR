using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading;
namespace BasisNetworkServer.BasisNetworking
{
    /// <summary>
    /// 競合を抑える striped counter を使う、高スループットで thread-safe なネットワーク統計。
    /// 0..255 の message index ごとに inbound/outbound の件数と byte 数を追跡し、
    /// byte 配列への compact encode/decode をサポートする (Brotli 利用も可)。
    /// </summary>
    public static class BasisNetworkStatistics
    {
        private const int Indices = 256;

        // stripes が多いほど競合が減る (core 数の 2 倍を起点に、妥当な範囲へ clamp)。
        private static readonly int StripeCount = Math.Clamp(Environment.ProcessorCount * 2, 16, 128);

        // Interlocked が ref long を受け取れるよう jagged array にする (要素を参照できる)。
        private static readonly long[][] _inCountStripes;
        private static readonly long[][] _inBytesStripes;
        private static readonly long[][] _outCountStripes;
        private static readonly long[][] _outBytesStripes;

        // thread-local の stripe 選択。0 は「未初期化」を表す。
        [ThreadStatic] private static int _stripePlusOne;

        static BasisNetworkStatistics()
        {
            _inCountStripes = new long[StripeCount][];
            _inBytesStripes = new long[StripeCount][];
            _outCountStripes = new long[StripeCount][];
            _outBytesStripes = new long[StripeCount][];

            for (int s = 0; s < StripeCount; s++)
            {
                _inCountStripes[s] = new long[Indices];
                _inBytesStripes[s] = new long[Indices];
                _outCountStripes[s] = new long[Indices];
                _outBytesStripes[s] = new long[Indices];
            }
        }
        public static bool IsRecordingData = false;
        // ===== 記録 API =====

        /// <summary><paramref name="index"/> の inbound message を 1 件記録し、encoded byte length を加算する。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordInbound(byte index, int bytesEncoded)
        {
            if(!IsRecordingData)
            {
                return;
            }

            int s = EnsureStripe();

            Interlocked.Increment(ref _inCountStripes[s][index]);
            Interlocked.Add(ref _inBytesStripes[s][index], bytesEncoded);
        }

        /// <summary><paramref name="index"/> の outbound message を 1 件記録し、encoded byte length を加算する。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordOutbound(byte index, int bytesEncoded)
        {
            if (!IsRecordingData)
            {
                return;
            }

            int s = EnsureStripe();

            Interlocked.Increment(ref _outCountStripes[s][index]);
            Interlocked.Add(ref _outBytesStripes[s][index], bytesEncoded);
        }

        /// <summary>
        /// 同じ <paramref name="index"/> の outbound message N 件をまとめて記録する。
        /// 呼び出し側は 1 つの論理スコープ内 (例: BSR send loop の receiver 1 件の tick) で、
        /// 合計 <paramref name="bytesEncoded"/> bytes の <paramref name="count"/> messages を
        /// 蓄積済みであることを想定する。N x (LOCK XADD + LOCK ADD) を 2 x LOCK ADD に畳み込み、
        /// 1000 人以上の player では BSR hot path の CPU を数パーセント節約する。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordOutboundBatch(byte index, long count, long bytesEncoded)
        {
            if (!IsRecordingData || count <= 0)
            {
                return;
            }

            int s = EnsureStripe();

            Interlocked.Add(ref _outCountStripes[s][index], count);
            Interlocked.Add(ref _outBytesStripes[s][index], bytesEncoded);
        }

        // ===== スナップショット API =====

        /// <summary>
        /// 破壊しない snapshot。読み取り中に値は変化し得るが、各読み取りは atomic。
        /// 後方互換メモ:
        ///   - Snapshot.PerIndex と Snapshot.TotalCalls は inbound。
        ///   - Snapshot.OutPerIndex と Snapshot.OutTotalCalls は outbound。
        /// </summary>
        public static Snapshot GetSnapshot()
        {
            var inPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);
            var outPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);

            for (int i = 0; i < Indices; i++)
            {
                long inCount = 0, inBytes = 0;
                long outCount = 0, outBytes = 0;

                for (int s = 0; s < StripeCount; s++)
                {
                    inCount += Volatile.Read(ref _inCountStripes[s][i]);
                    inBytes += Volatile.Read(ref _inBytesStripes[s][i]);
                    outCount += Volatile.Read(ref _outCountStripes[s][i]);
                    outBytes += Volatile.Read(ref _outBytesStripes[s][i]);
                }

                if ((inCount | inBytes) != 0)
                    inPerIndex[(byte)i] = new IndexStats(unchecked((ulong)inCount), unchecked((ulong)inBytes));

                if ((outCount | outBytes) != 0)
                    outPerIndex[(byte)i] = new IndexStats(unchecked((ulong)outCount), unchecked((ulong)outBytes));
            }
            return new Snapshot(inPerIndex, outPerIndex);
        }

        /// <summary>
        /// atomic cut: increment を失わずに全 counter を収集して reset する。
        /// 後方互換メモ:
        ///   - Snapshot.PerIndex/TotalCalls は inbound。OutPerIndex/OutTotalCalls は outbound。
        /// </summary>
        public static Snapshot SnapshotAndReset()
        {
            var inPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);
            var outPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);

            for (int i = 0; i < Indices; i++)
            {
                long inCount = 0, inBytes = 0;
                long outCount = 0, outBytes = 0;

                for (int s = 0; s < StripeCount; s++)
                {
                    inCount += Interlocked.Exchange(ref _inCountStripes[s][i], 0);
                    inBytes += Interlocked.Exchange(ref _inBytesStripes[s][i], 0);
                    outCount += Interlocked.Exchange(ref _outCountStripes[s][i], 0);
                    outBytes += Interlocked.Exchange(ref _outBytesStripes[s][i], 0);
                }

                if ((inCount | inBytes) != 0)
                    inPerIndex[(byte)i] = new IndexStats(unchecked((ulong)inCount), unchecked((ulong)inBytes));

                if ((outCount | outBytes) != 0)
                    outPerIndex[(byte)i] = new IndexStats(unchecked((ulong)outCount), unchecked((ulong)outBytes));
            }
            return new Snapshot(inPerIndex, outPerIndex);
        }

        /// <summary>すべてを 0 にする。</summary>
        public static void Clear()
        {
            for (int s = 0; s < StripeCount; s++)
            {
                for (int i = 0; i < Indices; i++)
                {
                    Interlocked.Exchange(ref _inCountStripes[s][i], 0);
                    Interlocked.Exchange(ref _inBytesStripes[s][i], 0);
                    Interlocked.Exchange(ref _outCountStripes[s][i], 0);
                    Interlocked.Exchange(ref _outBytesStripes[s][i], 0);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EnsureStripe()
        {
            int sPlusOne = _stripePlusOne;
            if (sPlusOne == 0)
            {
                int stripe = PickStripe();
                _stripePlusOne = sPlusOne = stripe + 1;
            }
            return sPlusOne - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickStripe()
        {
            // thread を stripes へ安定かつ低コストに分散する。
            int id = Thread.CurrentThread.ManagedThreadId;
            unchecked
            {
                uint x = (uint)id;
                x ^= x >> 17; x *= 0xED5AD4BBu;
                x ^= x >> 11; x *= 0xAC4C1B51u;
                x ^= x >> 15; x *= 0x31848BABu;
                x ^= x >> 14;
                return (int)(x % (uint)StripeCount);
            }
        }
        public readonly struct IndexStats
        {
            public readonly ulong Count;
            public readonly ulong Bytes;
            public IndexStats(ulong count, ulong bytes) { Count = count; Bytes = bytes; }
        }

        public sealed class Snapshot
        {
            // 後方互換 (inbound):
            public readonly Dictionary<byte, IndexStats> PerIndex;
            // 新規 (outbound):
            public readonly Dictionary<byte, IndexStats> OutPerIndex;

            public Snapshot( Dictionary<byte, IndexStats> inPerIndex, Dictionary<byte, IndexStats> outPerIndex)
            {
                PerIndex = inPerIndex;
                OutPerIndex = outPerIndex;
            }

            /// <summary>
            /// atomic cut を取り、live counter を reset してから encode し、必要なら compress する。
            /// </summary>
            public static byte[] SnapshotResetEncode(bool compress = true, int brotliQuality = 6)
            {
                var snap = BasisNetworkStatistics.SnapshotAndReset();
                var raw = EncodeSnapshot(snap);
                return compress ? BrotliCompress(raw, brotliQuality) : raw;
            }

            /// <summary>
            /// 破壊しない snapshot を encode する (reset なし)。debug に便利。
            /// </summary>
            public static byte[] EncodeCurrent(bool compress = true, int brotliQuality = 6)
            {
                var snap = BasisNetworkStatistics.GetSnapshot();
                var raw = EncodeSnapshot(snap);
                return compress ? BrotliCompress(raw, brotliQuality) : raw;
            }

            /// <summary>
            /// snapshot bytes を decode する (必要なら decompression 後)。
            /// </summary>
            public static Snapshot Decode(ReadOnlySpan<byte> data, bool compressed = true)
            {
                ReadOnlySpan<byte> raw = compressed ? BrotliDecompressToSpan(data) : data;
                return DecodeSnapshot(raw);
            }

            // --- エンコード/デコード中核 ---

            private static byte[] EncodeSnapshot(Snapshot s)
            {
                using var ms = new MemoryStream(512); // small default; grows as needed

                // 受信 map
                WriteMap(ms, s.PerIndex);
                // 送信 map
                WriteMap(ms, s.OutPerIndex);

                return ms.ToArray();
            }

            private static Snapshot DecodeSnapshot(ReadOnlySpan<byte> raw)
            {
                var r = new SpanReader(raw);

                var inPer = ReadMap(ref r);
                var outPer = ReadMap(ref r);

                return new Snapshot(inPer, outPer);
            }

            private static void WriteMap(Stream s, Dictionary<byte, IndexStats> map)
            {
                int n = 0;
                foreach (var kvp in map) if ((kvp.Value.Count | kvp.Value.Bytes) != 0) n++;
                WriteUVar(s, (uint)n);
                foreach (var (index, stats) in map)
                {
                    if ((stats.Count | stats.Bytes) == 0) continue;
                    s.WriteByte(index);
                    WriteUVar(s, stats.Count);
                    WriteUVar(s, stats.Bytes);
                }
            }

            private static Dictionary<byte, IndexStats> ReadMap(ref SpanReader r)
            {
                uint n = r.ReadUVar32();
                var dict = new Dictionary<byte, IndexStats>((int)Math.Min(n, 256));
                for (uint i = 0; i < n; i++)
                {
                    byte index = r.ReadByte();
                    ulong count = r.ReadUVar();
                    ulong bytes = r.ReadUVar();
                    dict[index] = new IndexStats(count, bytes);
                }
                return dict;
            }
            private static void WriteUVar(Stream s, ulong value)
            {
                // ulong は最大 10 bytes
                while (value >= 0x80)
                {
                    s.WriteByte((byte)((value & 0x7Fu) | 0x80u));
                    value >>= 7;
                }
                s.WriteByte((byte)value);
            }

            private static void WriteUVar(Stream s, uint value) => WriteUVar(s, (ulong)value);

            private ref struct SpanReader
            {
                private ReadOnlySpan<byte> _span;
                private int _pos;
                public SpanReader(ReadOnlySpan<byte> span) { _span = span; _pos = 0; }
                public byte ReadByte()
                {
                    if (_pos >= _span.Length) throw new EndOfStreamException();
                    return _span[_pos++];
                }
                public ulong ReadUVar()
                {
                    ulong result = 0;
                    int shift = 0;
                    while (true)
                    {
                        byte b = ReadByte();
                        result |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) return result;
                        shift += 7;
                        if (shift > 63) throw new InvalidDataException("Varint too long");
                    }
                }
                public uint ReadUVar32()
                {
                    ulong v = ReadUVar();
                    if (v > uint.MaxValue) throw new InvalidDataException("uvar32 overflow");
                    return (uint)v;
                }
            }

            private static byte[] BrotliCompress(ReadOnlySpan<byte> raw, int quality)
            {
                using var ms = new MemoryStream(raw.Length / 2);
                using (var bs = new BrotliStream(ms, quality >= 7 ? CompressionLevel.Optimal : CompressionLevel.Fastest, leaveOpen: true))
                {
                    bs.Write(raw);
                }
                return ms.ToArray();
            }

            private static ReadOnlySpan<byte> BrotliDecompressToSpan(ReadOnlySpan<byte> comp)
            {
                using var input = new MemoryStream(comp.ToArray());
                using var bs = new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(512);
                bs.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
