using Basis.Network.Core.Compression;
using System;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    /// <summary>
    /// HIGH quality の bone rotation data を server-side で repack し、
    /// 各 bone の smallest-three component をより低い bits-per-component (BPC) で
    /// re-quantize して medium/low/very-low quality に変換する。
    /// </summary>
    public static class AvatarQualityRepacker
    {
        static readonly int Slots = BasisBoneRotationCompression.SyncBoneCount; // 54

        // quality ごとの BPC table を cache する。
        static readonly byte[] HighBpc  = BasisBoneRotationCompression.BPC_HIGH;
        static readonly byte[] MedBpc   = BasisBoneRotationCompression.BPC_MEDIUM;
        static readonly byte[] LowBpc   = BasisBoneRotationCompression.BPC_LOW;
        static readonly byte[] VLowBpc  = BasisBoneRotationCompression.BPC_VERY_LOW;

        // byte count を cache する (現在は RotationBytes へ route される MuscleBytes 経由)。
        static readonly int HighRotBytes = MuscleBytes(BitQuality.High);
        static readonly int MedRotBytes  = MuscleBytes(BitQuality.Medium);
        static readonly int LowRotBytes  = MuscleBytes(BitQuality.Low);
        static readonly int VLowRotBytes = MuscleBytes(BitQuality.VeryLow);

        // payload size を cache する。
        static readonly int HighPayloadSize = WritePosition + HighRotBytes + TailBytes;
        static readonly int MedPayloadSize  = WritePosition + MedRotBytes  + TailBytes;
        static readonly int LowPayloadSize  = WritePosition + LowRotBytes  + TailBytes;
        static readonly int VLowPayloadSize = WritePosition + VLowRotBytes + TailBytes;

        // quality ごとの per-bone bit offset を cache する。
        static readonly int[] HighOffs = BuildBitOffsets(HighBpc);
        static readonly int[] MedOffs  = BuildBitOffsets(MedBpc);
        static readonly int[] LowOffs  = BuildBitOffsets(LowBpc);
        static readonly int[] VLowOffs = BuildBitOffsets(VLowBpc);

        static int[] BuildBitOffsets(byte[] bpc)
        {
            var offs = new int[Slots];
            int bit = 0;
            for (int i = 0; i < Slots; i++)
            {
                offs[i] = bit;
                bit += 2 + 3 * bpc[i]; // 2 index bits + 3 components
            }
            return offs;
        }

        public static void BuildAllLowerFromHighInto(
            in SerializableBasis.LocalAvatarSyncMessage srcHigh,
            ref SerializableBasis.LocalAvatarSyncMessage medium,
            ref SerializableBasis.LocalAvatarSyncMessage low,
            ref SerializableBasis.LocalAvatarSyncMessage veryLow)
        {
            if (srcHigh.array == null)
                throw new ArgumentNullException(nameof(srcHigh.array));

            if (srcHigh.array.Length < HighPayloadSize)
                throw new ArgumentException($"High payload too small. Need >= {HighPayloadSize}, got {srcHigh.array.Length}");

            EnsureBuffer(ref medium, BitQuality.Medium, MedPayloadSize);
            EnsureBuffer(ref low, BitQuality.Low, LowPayloadSize);
            EnsureBuffer(ref veryLow, BitQuality.VeryLow, VLowPayloadSize);

            // position を copy する (12 bytes、変更なし)。
            Buffer.BlockCopy(srcHigh.array, 0, medium.array, 0, WritePosition);
            Buffer.BlockCopy(srcHigh.array, 0, low.array, 0, WritePosition);
            Buffer.BlockCopy(srcHigh.array, 0, veryLow.array, 0, WritePosition);

            int rotBase = WritePosition;

            // rotation region を clear する (BitWriter は byte に OR する)。
            Array.Clear(medium.array, rotBase, MedRotBytes);
            Array.Clear(low.array, rotBase, LowRotBytes);
            Array.Clear(veryLow.array, rotBase, VLowRotBytes);

            // 各 bone を repack する。HIGH BPC の smallest-three を読み、component を lower BPC へ rescale する。
            for (int slot = 0; slot < Slots; slot++)
            {
                int bpcSrc = HighBpc[slot];
                int totalBitsSrc = 2 + 3 * bpcSrc;

                // packed bone 全体 (index + 3 components) を raw bit として読む。
                ulong raw = BitReader.ReadBitsU64(srcHigh.array, rotBase, HighOffs[slot], totalBitsSrc);

                // 2-bit index (どの component が drop されたか) を取り出す。
                uint idx = (uint)(raw & 3UL);

                // source BPC で 3 component を取り出す。
                uint maskSrc = (uint)((1 << bpcSrc) - 1);
                uint qa = (uint)((raw >> 2) & maskSrc);
                uint qb = (uint)((raw >> (2 + bpcSrc)) & maskSrc);
                uint qc = (uint)((raw >> (2 + 2 * bpcSrc)) & maskSrc);

                // target quality ごとに rescale して書く。
                RepackBone(medium.array, rotBase, MedOffs[slot], MedBpc[slot], idx, qa, qb, qc, bpcSrc);
                RepackBone(low.array, rotBase, LowOffs[slot], LowBpc[slot], idx, qa, qb, qc, bpcSrc);
                RepackBone(veryLow.array, rotBase, VLowOffs[slot], VLowBpc[slot], idx, qa, qb, qc, bpcSrc);
            }

            // tail (scale + body rotation) を copy する。
            int srcTailOffset = WritePosition + HighRotBytes;
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, medium.array, WritePosition + MedRotBytes, TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, low.array, WritePosition + LowRotBytes, TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, veryLow.array, WritePosition + VLowRotBytes, TailBytes);
        }

        static void RepackBone(byte[] dst, int baseByteOffset, int bitOffset, int bpcDst,
            uint idx, uint qa, uint qb, uint qc, int bpcSrc)
        {
            // 各 component を source BPC から destination BPC へ rescale する。
            uint da = RescaleQuant(qa, bpcSrc, bpcDst);
            uint db = RescaleQuant(qb, bpcSrc, bpcDst);
            uint dc = RescaleQuant(qc, bpcSrc, bpcDst);

            // pack: [idx:2][da:bpcDst][db:bpcDst][dc:bpcDst]
            ulong packed = (ulong)idx
                | ((ulong)da << 2)
                | ((ulong)db << (2 + bpcDst))
                | ((ulong)dc << (2 + 2 * bpcDst));

            int totalBits = 2 + 3 * bpcDst;
            BitWriter.WriteBitsU64(dst, baseByteOffset, bitOffset, packed, totalBits);
        }

        static void EnsureBuffer(ref SerializableBasis.LocalAvatarSyncMessage msg, BitQuality q, int size)
        {
            msg.DataQualityLevel = (byte)q;
            if (msg.array != null && msg.array.Length >= size)
                return;
            msg.array = new byte[size];
        }

        public static (SerializableBasis.LocalAvatarSyncMessage medium,
                       SerializableBasis.LocalAvatarSyncMessage low,
                       SerializableBasis.LocalAvatarSyncMessage veryLow)
            BuildAllLowerFromHigh(in SerializableBasis.LocalAvatarSyncMessage srcHigh)
        {
            var med = new SerializableBasis.LocalAvatarSyncMessage();
            var low = new SerializableBasis.LocalAvatarSyncMessage();
            var vlow = new SerializableBasis.LocalAvatarSyncMessage();
            BuildAllLowerFromHighInto(srcHigh, ref med, ref low, ref vlow);
            return (med, low, vlow);
        }

        static uint RescaleQuant(uint qSrc, int bSrc, int bDst)
        {
            if (bSrc == bDst) return qSrc;
            if (bDst <= 0) return 0;
            ulong maxSrc = ((ulong)1 << bSrc) - 1UL;
            ulong maxDst = ((ulong)1 << bDst) - 1UL;
            ulong num = (ulong)qSrc * maxDst + (maxSrc >> 1);
            return (uint)(num / maxSrc);
        }

        static class BitReader
        {
            public static ulong ReadBitsU64(byte[] src, int baseByteOffset, int bitPos, int bitCount)
            {
                int bytePos = baseByteOffset + (bitPos >> 3);
                int bitInByte = bitPos & 7;
                ulong result = 0;
                int outShift = 0;
                int bitsLeft = bitCount;

                while (bitsLeft > 0)
                {
                    int room = 8 - bitInByte;
                    int take = bitsLeft < room ? bitsLeft : room;
                    ulong cur = src[bytePos];
                    cur >>= bitInByte;
                    ulong mask = (1UL << take) - 1UL;
                    ulong chunk = cur & mask;
                    result |= (chunk << outShift);
                    outShift += take;
                    bitsLeft -= take;
                    bytePos++;
                    bitInByte = 0;
                }
                return result;
            }
        }

        static class BitWriter
        {
            public static void WriteBitsU64(byte[] dst, int baseByteOffset, int bitPos, ulong value, int bitCount)
            {
                int bytePos = baseByteOffset + (bitPos >> 3);
                int bitInByte = bitPos & 7;
                ulong v = value;
                int bitsLeft = bitCount;

                while (bitsLeft > 0)
                {
                    int room = 8 - bitInByte;
                    int take = bitsLeft < room ? bitsLeft : room;
                    ulong mask = (1UL << take) - 1UL;
                    byte chunk = (byte)(v & mask);
                    dst[bytePos] = (byte)(dst[bytePos] | (chunk << bitInByte));
                    v >>= take;
                    bitsLeft -= take;
                    bytePos++;
                    bitInByte = 0;
                }
            }
        }
    }
}
