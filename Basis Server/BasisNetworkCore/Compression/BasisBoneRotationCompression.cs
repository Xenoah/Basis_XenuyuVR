using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// "smallest three" クォータニオンエンコードを使うボーン回転圧縮。
    /// Pure C# で Unity 依存なし。サーバー上で動作できる。
    ///
    /// 各ボーンには自由度 (DOF) に応じた bits-per-component (BPC) を割り当てる:
    ///   3-DOF body joints: 10 BPC (合計 32 bits)
    ///   2-DOF limb joints: 8 BPC (合計 26 bits)
    ///   2-DOF extremities: 7 BPC (合計 23 bits)
    ///   1-2 DOF toes/eyes/jaw: 5 BPC (合計 17 bits)
    ///   2-DOF finger proximal: 6 BPC (合計 20 bits)
    ///   1-DOF finger mid/distal: 4 BPC (合計 14 bits)
    /// </summary>
    public static class BasisBoneRotationCompression
    {
        /// <summary>
        /// 同期するボーン数。以下は除外する:
        ///   Hips (0) はパケット末尾の body rotation として送信する
        ///   LeftEye (21), RightEye (22), Jaw (23) は BasisRemoteFaceManagement がローカル駆動する
        /// </summary>
        public const int SyncBoneCount = 51;

        /// <summary>sqrt(2) の逆数。smallest-three で破棄されなかった成分が取り得る最大の大きさ。</summary>
        public const float InvSqrt2 = 0.70710678118f;

        // 位置/スケール/回転サイズは BasisAvatarBitPacking の定義を再利用する
        public const int WritePosition = BasisAvatarBitPacking.WritePosition;   // 12
        public const int WriteScale    = BasisAvatarBitPacking.WriteScale;      // 2
        public const int WriteRotation = BasisAvatarBitPacking.WriteRotation;   // 7
        public const int WriteHipsDelta = BasisAvatarBitPacking.WriteHipsDelta; // 6
        public const int WriteHipsRotation = BasisAvatarBitPacking.WriteHipsRotation; // 7
        public const int TailBytes     = BasisAvatarBitPacking.TailBytes;       // 22

        // ────────────────────────────────────────────────────────────
        //  ボーン書き込み順: HumanBodyBones enum 値 (Hips=0 は除外)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// slot index (0..50) を HumanBodyBones enum 値へ対応付ける。
        /// Hips(0), LeftEye(21), RightEye(22), Jaw(23) は除外する。
        /// グループ順: 3-DOF body → 2-DOF limbs → 2-DOF extremities → toes → finger proximal → finger mid/distal。
        /// </summary>
        public static readonly int[] BONE_WRITE_ORDER = new int[]
        {
            // 3-DOF body (9 bones): Spine, Chest, UpperChest, Neck, Head, UpperArms, UpperLegs
            7, 8, 54, 9, 10, 13, 14, 1, 2,
            // 2-DOF limbs (4 bones): LowerArms, LowerLegs
            15, 16, 3, 4,
            // 2-DOF extremities (6 bones): Shoulders, Hands, Feet
            11, 12, 17, 18, 5, 6,
            // toes (2 bones): eyes/jaw は除外 (face system が駆動)
            19, 20,
            // 2-DOF finger proximal (10 bones)
            24, 27, 30, 33, 36, 39, 42, 45, 48, 51,
            // 1-DOF finger intermediate (10 bones)
            25, 28, 31, 34, 37, 40, 43, 46, 49, 52,
            // 1-DOF finger distal (10 bones)
            26, 29, 32, 35, 38, 41, 44, 47, 50, 53,
        };

        /// <summary>
        /// 逆引き: HumanBodyBones enum 値 → slot index。
        /// Index 0 (Hips) = -1。Bones 1..54 は slots 0..53 へ対応する。
        /// </summary>
        public static readonly int[] BONE_TO_SLOT;

        static BasisBoneRotationCompression()
        {
            BONE_TO_SLOT = new int[55];
            for (int i = 0; i < 55; i++) BONE_TO_SLOT[i] = -1;
            for (int slot = 0; slot < SyncBoneCount; slot++)
                BONE_TO_SLOT[BONE_WRITE_ORDER[slot]] = slot;
        }

        // ────────────────────────────────────────────────────────────
        //  Bits-per-component テーブル (品質レベルごと)
        //  ボーンごとの合計 bits = 2 (index) + 3 * BPC
        // ────────────────────────────────────────────────────────────

        /// <summary>HIGH 品質。1182 bits = 回転 148 bytes。Packet = 169 bytes。
        /// 指ごとの優先度: thumb/index は表現力が高いため多めに割り当てる。
        /// Proximal は spread motion を担うため intermediate/distal より多めにする。</summary>
        public static readonly byte[] BPC_HIGH = new byte[]
        {
            // 3-DOF body (9): spine, chest, upperchest, neck, head, upper arms, upper legs
            10,10,10,10,10,10,10,10,10,
            // 2-DOF limbs (4): lower arms, lower legs
            10,10,10,10,
            // 2-DOF extremities (6): shoulders(2), hands(2), feet(2)
            10,10, 10,10, 9,9,
            // toes (2)
            5,5,
            // finger proximal (10): L-Thumb,L-Index,L-Mid,L-Ring,L-Little, R-same
            6,6,6,6,5,  6,6,6,6,5,
            // finger intermediate (10): Thumb/Index=6, Mid/Ring/Little=5
            6,6,5,5,5,  6,6,5,5,5,
            // finger distal (10): all 5
            5,5,5,5,5,  5,5,5,5,5,
        };

        /// <summary>MEDIUM 品質。972 bits = 回転 122 bytes。Packet = 143 bytes。</summary>
        public static readonly byte[] BPC_MEDIUM = new byte[]
        {
            8,8,8,8,8,8,8,8,8,
            8,8,8,8,
            8,8, 8,8, 6,6,
            3,3,
            6,6,5,5,4,  6,6,5,5,4,
            5,5,4,4,4,  5,5,4,4,4,
            4,4,4,4,4,  4,4,4,4,4,
        };

        /// <summary>LOW 品質。774 bits = 回転 97 bytes。Packet = 118 bytes。</summary>
        public static readonly byte[] BPC_LOW = new byte[]
        {
            6,6,6,6,6,6,6,6,6,
            6,6,6,6,
            6,6, 6,6, 5,5,
            3,3,
            5,5,4,4,3,  5,5,4,4,3,
            4,4,3,3,3,  4,4,3,3,3,
            3,3,3,3,3,  3,3,3,3,3,
        };

        /// <summary>VERY LOW 品質。621 bits = 回転 78 bytes。Packet = 99 bytes。</summary>
        public static readonly byte[] BPC_VERY_LOW = new byte[]
        {
            5,5,5,5,5,5,5,5,5,
            5,5,5,5,
            5,5, 5,5, 4,4,
            2,2,
            4,4,3,3,2,  4,4,3,3,2,
            3,3,2,2,2,  3,3,2,2,2,
            2,2,2,2,2,  2,2,2,2,2,
        };

        // ────────────────────────────────────────────────────────────
        //  ボーンごとの最大成分範囲 (関節制限)
        //  maxComp = sin(maxAngle/2) に約 15% の安全余裕を加え、InvSqrt2 で上限を切る。
        //  範囲が狭いほど、同じ bit 数で精度が上がる。
        //  精度倍率 = InvSqrt2 / maxComp。
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// ボーンスロットごとのクォータニオン成分の最大値。
        /// 成分は完全な [-0.707, 0.707] ではなく [-maxComp, maxComp] の範囲で量子化する。
        ///
        /// 設計: ダンス、体操、睡眠姿勢、反り、開脚などを含む人間の姿勢全般を支えるため、
        /// ほとんどの関節は InvSqrt2 の全範囲を使う。
        /// 大きく回転できない関節だけ範囲を狭める:
        ///   - Eyes: 最大視線方向は約 35 度 (外眼筋の解剖学的制限)
        ///   - Jaw: 最大開口 + 左右は約 40 度 (TMJ 制限)
        ///   - Toes: 最大 curl は約 55 度 (中足骨の制限)
        ///   - UpperChest: 最大約 50 度 (胸椎は癒合/制限される)
        ///
        /// Hips orientation は別途フル精度の圧縮クォータニオンとして送るため、
        /// upside-down や sideways などはこの制限の影響を受けない。
        /// </summary>
        /// <summary>
        /// ボーンスロットごとのクォータニオン成分の最大値。
        /// smallest-three で最大成分を破棄したあと、残り 3 成分を
        /// [-maxComp, maxComp] の範囲で量子化する。
        /// 範囲が狭いほど、同じ BPC で精度が上がる。
        ///
        /// 値は解剖学的な最大回転から導出し、残り成分の最大候補として
        /// sin(maxAngle/2) を計算したうえで安全余裕を加える。
        /// T-pose から 90 度に近づく、または超える可能性がある関節には
        /// 完全な InvSqrt2 を使う。
        /// </summary>
        public static readonly float[] MAX_COMPONENT = new float[]
        {
            // 3-DOF body (9): Spine, Chest, UpperChest, Neck, Head, UpperArms, UpperLegs
            InvSqrt2,               // Spine         full (深い反り/折り畳みで合計 90 度を超え得る)
            InvSqrt2,               // Chest         full
            0.50f,                  // UpperChest    胸郭の制限は約 58 度 -> 1.41x
            InvSqrt2,               // Neck          full (極端な頭部傾き)
            InvSqrt2,               // Head          full
            InvSqrt2, InvSqrt2,     // UpperArms     full (肩の可動域は約 180 度)
            InvSqrt2, InvSqrt2,     // UpperLegs     full (開脚、深いしゃがみ)

            // 2-DOF limbs (4): LowerArms, LowerLegs
            InvSqrt2, InvSqrt2,     // LowerArms     full (肘 150 度 + 回内 90 度)
            InvSqrt2, InvSqrt2,     // LowerLegs     full (膝 150 度)

            // 2-DOF extremities (6): Shoulders, Hands, Feet
            0.50f, 0.50f,           // Shoulders     鎖骨最大約 58 度 (shrug+protract) -> 1.41x
            InvSqrt2, InvSqrt2,     // Hands         full (手首は約 90 度回せる)
            0.60f, 0.60f,           // Feet          足首は合成で最大約 70 度 -> 1.18x

            // toes (2): eyes/jaw は除外 (face system が駆動)
            0.50f, 0.50f,           // Toes          約 58 度 curl -> 1.41x

            // finger proximal (10): curl 約 90 度 + spread 約 25 度 -> 合成約 95 度
            // 95 度時: axis=0.74, w=0.68。axis 破棄後の残り最大値は 0.68
            0.68f, 0.68f, 0.68f, 0.68f, 0.68f,
            0.68f, 0.68f, 0.68f, 0.68f, 0.68f,

            // finger intermediate (10): curl のみ、最大約 110 度
            // 110 度時: axis=0.82, w=0.57。axis 破棄後の残り最大値は 0.57
            0.58f, 0.58f, 0.58f, 0.58f, 0.58f,
            0.58f, 0.58f, 0.58f, 0.58f, 0.58f,

            // finger distal (10): curl のみ、最大約 80 度
            // 80 度時: w=0.77, axis=0.64。w 破棄後の残り最大値は 0.64
            0.65f, 0.65f, 0.65f, 0.65f, 0.65f,
            0.65f, 0.65f, 0.65f, 0.65f, 0.65f,
        };

        public static byte[] GetBpcTable(BasisAvatarBitPacking.BitQuality q) => q switch
        {
            BasisAvatarBitPacking.BitQuality.High     => BPC_HIGH,
            BasisAvatarBitPacking.BitQuality.Medium   => BPC_MEDIUM,
            BasisAvatarBitPacking.BitQuality.Low      => BPC_LOW,
            BasisAvatarBitPacking.BitQuality.VeryLow  => BPC_VERY_LOW,
            _ => BPC_HIGH
        };

        // ────────────────────────────────────────────────────────────
        //  サイズ計算
        // ────────────────────────────────────────────────────────────

        public static int RotationBytes(BasisAvatarBitPacking.BitQuality q)
        {
            byte[] bpc = GetBpcTable(q);
            int totalBits = 0;
            for (int i = 0; i < bpc.Length; i++)
                totalBits += 2 + 3 * bpc[i];
            return (totalBits + 7) >> 3;
        }

        public static int ConvertToSize(BasisAvatarBitPacking.BitQuality q)
        {
            return WritePosition + RotationBytes(q) + TailBytes;
        }

        public static int ComputeBitOffsets(byte[] bpc, int[] outBitOffsets)
        {
            int pos = 0;
            for (int i = 0; i < bpc.Length; i++)
            {
                outBitOffsets[i] = pos;
                pos += 2 + 3 * bpc[i];
            }
            return pos;
        }

        // ────────────────────────────────────────────────────────────
        //  Smallest-Three Encode / Decode (pure floats、Unity 型なし)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 単位クォータニオン (x,y,z,w) を "smallest three" 圧縮でエンコードする。
        /// 回転範囲が限られる関節では精度を上げるため、成分を
        /// [-maxRange, maxRange] に量子化する。全範囲の関節では InvSqrt2 を使う。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong EncodeSmallestThree(float qx, float qy, float qz, float qw, int bpc, float maxRange = InvSqrt2)
        {
            float ax = Math.Abs(qx), ay = Math.Abs(qy), az = Math.Abs(qz), aw = Math.Abs(qw);

            // 絶対値が最大の成分を探す
            int maxIdx = 0;
            float maxVal = ax;
            if (ay > maxVal) { maxIdx = 1; maxVal = ay; }
            if (az > maxVal) { maxIdx = 2; maxVal = az; }
            if (aw > maxVal) { maxIdx = 3; }

            // 最大成分が負ならクォータニオンを反転する
            float sign = 1f;
            switch (maxIdx)
            {
                case 0: if (qx < 0f) sign = -1f; break;
                case 1: if (qy < 0f) sign = -1f; break;
                case 2: if (qz < 0f) sign = -1f; break;
                case 3: if (qw < 0f) sign = -1f; break;
            }
            qx *= sign; qy *= sign; qz *= sign; qw *= sign;

            // 残り 3 成分を取り出す
            float a, b, c;
            switch (maxIdx)
            {
                case 0:  a = qy; b = qz; c = qw; break;
                case 1:  a = qx; b = qz; c = qw; break;
                case 2:  a = qx; b = qy; c = qw; break;
                default: a = qx; b = qy; c = qz; break;
            }

            // [-maxRange, maxRange] 内で量子化する (端のケースは clamp)
            float invRange = 1f / maxRange;
            uint maxQ = (uint)((1 << bpc) - 1);
            uint qa = Clamp((uint)Math.Round((ClampF(a * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);
            uint qA = Clamp((uint)Math.Round((ClampF(b * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);
            uint qC = Clamp((uint)Math.Round((ClampF(c * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);

            return (ulong)maxIdx | ((ulong)qa << 2) | ((ulong)qA << (2 + bpc)) | ((ulong)qC << (2 + 2 * bpc));
        }

        /// <summary>
        /// "smallest three" 圧縮クォータニオンを (x,y,z,w) にデコードする。
        /// maxRange はエンコード時に使った値と一致している必要がある。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DecodeSmallestThree(ulong packed, int bpc, out float qx, out float qy, out float qz, out float qw, float maxRange = InvSqrt2)
        {
            uint mask = (uint)((1 << bpc) - 1);
            int maxIdx = (int)(packed & 3UL);
            uint qa = (uint)((packed >> 2) & mask);
            uint qb = (uint)((packed >> (2 + bpc)) & mask);
            uint qc = (uint)((packed >> (2 + 2 * bpc)) & mask);

            float fMax = (float)mask;
            float a = (qa / fMax * 2f - 1f) * maxRange;
            float b = (qb / fMax * 2f - 1f) * maxRange;
            float c = (qc / fMax * 2f - 1f) * maxRange;

            float d2 = 1f - a * a - b * b - c * c;
            float d = d2 > 0f ? (float)Math.Sqrt(d2) : 0f;

            switch (maxIdx)
            {
                case 0:  qx = d; qy = a; qz = b; qw = c; break;
                case 1:  qx = a; qy = d; qz = b; qw = c; break;
                case 2:  qx = a; qy = b; qz = d; qw = c; break;
                default: qx = a; qy = b; qz = c; qw = d; break;
            }

            // 正規化
            float len = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len > 1e-8f)
            {
                float inv = 1f / len;
                qx *= inv; qy *= inv; qz *= inv; qw *= inv;
            }
            else
            {
                qx = 0f; qy = 0f; qz = 0f; qw = 1f;
            }
        }

        // ────────────────────────────────────────────────────────────
        //  ビットストリーム読み書き (pure C#)
        // ────────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBits(byte[] dst, int bitPos, ulong value, int bitCount)
        {
            int bytePos = bitPos >> 3;
            int bitInByte = bitPos & 7;
            ulong v = value;
            int bitsLeft = bitCount;

            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;
                ulong maskVal = (1UL << take) - 1UL;
                byte chunk = (byte)(v & maskVal);
                dst[bytePos] = (byte)(dst[bytePos] | (chunk << bitInByte));
                v >>= take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadBits(byte[] src, ref int bitPos, int bitCount)
        {
            int bytePos = bitPos >> 3;
            int bitInByte = bitPos & 7;
            ulong outV = 0;
            int outShift = 0;
            int bitsLeft = bitCount;

            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;
                ulong maskVal = (1UL << take) - 1UL;
                ulong chunk = ((ulong)src[bytePos] >> bitInByte) & maskVal;
                outV |= chunk << outShift;
                outShift += take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }

            bitPos += bitCount;
            return outV;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Clamp(uint v, uint min, uint max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ClampF(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
