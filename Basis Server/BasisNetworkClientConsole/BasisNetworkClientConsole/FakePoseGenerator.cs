using System;
using Basis.Network.Core.Compression;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkClientConsole
{
    /// <summary>
    /// fake client 用に、人間らしい avatar pose data を生成する。
    /// 腕を横に下ろし、指を軽く緩め、自然な spine を持つ standing pose に、
    /// 呼吸、揺れ、head の微細な動きといった subtle idle animation を加える。
    ///
    /// T-pose avatar を生んでいた古い zeroed-byte-array 方式の置き換え。
    /// real client と同じ smallest-three quaternion compression を使う。
    /// </summary>
    public static class FakePoseGenerator
    {
        private const float Deg2Rad = MathF.PI / 180f;
        private const float TwoPi = MathF.PI * 2f;
        private const float InvSqrt2 = 0.70710678118f;
        private const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 51

        // base の自然な standing pose。51 個の quaternion を flat float array として保持する。
        // layout: [slot * 4 + 0] = x, [slot * 4 + 1] = y, [slot * 4 + 2] = z, [slot * 4 + 3] = w
        // T-pose relative の delta quaternion。identity は T-pose、non-identity はそこからの偏差を意味する。
        private static readonly float[] BasePose;

        // GetIdleDelta が animate する slot。それ以外の slot は quality ごとの定数として encode される。
        private static readonly bool[] IsAnimated;
        private static readonly ulong[][] BasePackedByQuality;

        static FakePoseGenerator()
        {
            BasePose = new float[BoneCount * 4];
            BuildNaturalStandingPose();

            IsAnimated = new bool[BoneCount];
            MarkAnimatedSlots();

            BasePackedByQuality = new ulong[4][];
            PrecomputeBasePacked();
        }

        private static void MarkAnimatedSlots()
        {
            IsAnimated[0] = true; // Spine
            IsAnimated[1] = true; // Chest
            IsAnimated[3] = true; // Neck
            IsAnimated[4] = true; // Head
            IsAnimated[5] = true; // Left upper arm
            IsAnimated[6] = true; // Right upper arm
            IsAnimated[7] = true; // Left upper leg
            IsAnimated[8] = true; // Right upper leg
            for (int slot = 21; slot <= 30; slot++)
                IsAnimated[slot] = true; // finger proximal
        }

        private static void PrecomputeBasePacked()
        {
            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;

            for (int q = 0; q < BasePackedByQuality.Length; q++)
            {
                byte[] bpc = BasisBoneRotationCompression.GetBpcTable((BitQuality)q);
                ulong[] packed = new ulong[BoneCount];

                for (int slot = 0; slot < BoneCount; slot++)
                {
                    int idx = slot * 4;
                    float bx = BasePose[idx], by = BasePose[idx + 1], bz = BasePose[idx + 2], bw = BasePose[idx + 3];
                    Normalize(ref bx, ref by, ref bz, ref bw);
                    packed[slot] = BasisBoneRotationCompression.EncodeSmallestThree(bx, by, bz, bw, bpc[slot], ranges[slot]);
                }

                BasePackedByQuality[q] = packed;
            }
        }

        // ────────────────────────────────────────────────────────────
        //  natural standing pose の定義
        //
        //  BONE_WRITE_ORDER の slot 割り当て:
        //   0:Spine  1:Chest  2:UpperChest  3:Neck  4:Head
        //   5:LUpperArm  6:RUpperArm  7:LUpperLeg  8:RUpperLeg
        //   9:LLowerArm  10:RLowerArm  11:LLowerLeg  12:RLowerLeg
        //  13:LShoulder  14:RShoulder  15:LHand  16:RHand  17:LFoot  18:RFoot
        //  19:LToes  20:RToes
        //  21-30: finger proximal (L-Thumb,L-Index,L-Mid,L-Ring,L-Little, R-same)
        //  31-40: finger intermediate
        //  41-50: finger distal
        // ────────────────────────────────────────────────────────────

        private static void BuildNaturalStandingPose()
        {
            // 51 bone すべてを identity (T-pose) で初期化する。
            for (int i = 0; i < BoneCount; i++)
                SetQuat(i, 0f, 0f, 0f, 1f);

            // ── Spine chain: 自然な S curve ──
            SetAxisAngle(0, 1, 0, 0, 5f);     // Spine: わずかに前傾
            SetAxisAngle(1, 1, 0, 0, -3f);    // Chest: 補正用のわずかな伸展
            SetAxisAngle(2, 1, 0, 0, 2f);     // UpperChest: わずかに前へ
            SetAxisAngle(3, 1, 0, 0, 8f);     // Neck: 前方 tilt
            SetAxisAngle(4, 1, 0, 0, -3f);    // Head: 目線を水平にするため少し後ろへ

            // ── Upper arms: T-pose から下げる ──
            // bone の local T-pose frame では、-Z rotation で腕が下方向へ swing する。
            // 両腕は構造的に mirror しているため、同じ local delta を使う。
            SetAxisAngle(5, 0, 0, 1, -72f);   // left upper arm: 約 72 degree 下げる
            SetAxisAngle(6, 0, 0, 1, -72f);   // right upper arm: 約 72 degree 下げる

            // ── Upper legs: わずかな前傾を持つ直立姿勢 ──
            SetAxisAngle(7, 1, 0, 0, 2f);
            SetAxisAngle(8, 1, 0, 0, 2f);

            // ── Lower arms: elbow を少し曲げる ──
            SetAxisAngle(9, 0, 1, 0, 20f);    // left elbow
            SetAxisAngle(10, 0, 1, 0, -20f);  // right elbow (mirrored)

            // ── Lower legs: knee をごくわずかに曲げる ──
            SetAxisAngle(11, 1, 0, 0, 5f);
            SetAxisAngle(12, 1, 0, 0, 5f);

            // ── Shoulders: わずかに下げる ──
            SetAxisAngle(13, 0, 0, 1, -3f);
            SetAxisAngle(14, 0, 0, 1, 3f);

            // ── Hands: 自然な wrist angle を少し付ける ──
            SetAxisAngle(15, 0, 0, 1, 5f);
            SetAxisAngle(16, 0, 0, 1, -5f);

            // ── Feet: standing 用にわずかに dorsiflexion させる ──
            SetAxisAngle(17, 1, 0, 0, -8f);
            SetAxisAngle(18, 1, 0, 0, -8f);

            // ── Toes: ground に平らに置く (identity) ──
            // slot 19-20 はすでに identity。

            // ── Fingers: relaxed / slightly curled ──
            // proximal bone: 約 20 degree curl (axis が異なる thumb も含む)
            for (int i = 21; i <= 30; i++)
                SetAxisAngle(i, 1, 0, 0, 20f);

            // intermediate bone: 約 30 degree curl
            for (int i = 31; i <= 40; i++)
                SetAxisAngle(i, 1, 0, 0, 30f);

            // distal bone: 約 15 degree curl
            for (int i = 41; i <= 50; i++)
                SetAxisAngle(i, 1, 0, 0, 15f);
        }

        // ────────────────────────────────────────────────────────────
        //  bone rotation encoding (packet byte buffer へ書く)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 51 個すべての bone rotation (base pose + idle animation) を、
        /// smallest-three compression を使って packet buffer へ書き込む。
        /// 書き込み前に rotation region を clear する。
        /// </summary>
        /// <param name="dst">packet byte array。</param>
        /// <param name="byteOffset">bone rotation region の開始位置 (position bytes の後)。</param>
        /// <param name="quality">compression quality level。</param>
        /// <param name="timeSec">animation 用の経過秒数。</param>
        /// <param name="phase">player ごとの phase offset (animation の同期を避ける)。</param>
        public static void WriteBoneRotations(byte[] dst, int byteOffset, BitQuality quality, double timeSec, float phase)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;
            ulong[] basePacked = BasePackedByQuality[(int)quality];

            // rotation region を clear する。WriteBits は byte に OR するため、clean な状態で始める必要がある。
            int rotBytes = BasisBoneRotationCompression.RotationBytes(quality);
            Array.Clear(dst, byteOffset, rotBytes);

            int bitPos = byteOffset << 3;

            for (int slot = 0; slot < BoneCount; slot++)
            {
                int bitsPerComp = bpc[slot];
                int totalBits = 2 + 3 * bitsPerComp;

                ulong packed;
                if (IsAnimated[slot])
                {
                    // base pose quaternion
                    int idx = slot * 4;
                    float bx = BasePose[idx], by = BasePose[idx + 1], bz = BasePose[idx + 2], bw = BasePose[idx + 3];

                    // idle animation delta
                    GetIdleDelta(slot, timeSec, phase, out float dx, out float dy, out float dz, out float dw);

                    // combined = base * delta
                    QuatMul(bx, by, bz, bw, dx, dy, dz, dw, out float rx, out float ry, out float rz, out float rw);
                    Normalize(ref rx, ref ry, ref rz, ref rw);

                    packed = BasisBoneRotationCompression.EncodeSmallestThree(rx, ry, rz, rw, bitsPerComp, ranges[slot]);
                }
                else
                {
                    packed = basePacked[slot];
                }

                BasisBoneRotationCompression.WriteBits(dst, bitPos, packed, totalBits);
                bitPos += totalBits;
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Hips (body) rotation - 7-byte compressed quaternion tail
        //
        //  Unity 側の WriteCompressedQuaternionToBytes と同じ format:
        //   [1 byte: 最大 component index]
        //   [2 bytes: ushort comp a]
        //   [2 bytes: ushort comp b]
        //   [2 bytes: ushort comp c]
        //  各 component は [-InvSqrt2, +InvSqrt2] から [0, 65535] へ quantize される。
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// animated hips rotation を packet の 7-byte tail へ書き込む。
        /// </summary>
        public static void WriteCompressedHipsRotation(byte[] dst, int offset, double timeSec, float phase)
        {
            // subtle な body yaw sway と、わずかな lateral tilt。
            float yaw = 3f * MathF.Sin((float)(timeSec * 0.06 * TwoPi + phase * 1.7));
            float tilt = 1f * MathF.Sin((float)(timeSec * 0.04 * TwoPi + phase * 2.3));

            AxisAngleToQuat(0, 1, 0, yaw, out float yx, out float yy, out float yz, out float yw);
            AxisAngleToQuat(0, 0, 1, tilt, out float tx, out float ty, out float tz, out float tw);
            QuatMul(yx, yy, yz, yw, tx, ty, tz, tw, out float qx, out float qy, out float qz, out float qw);
            Normalize(ref qx, ref qy, ref qz, ref qw);

            WriteCompressedQuat(dst, offset, qx, qy, qz, qw);
        }

        private static void WriteCompressedQuat(byte[] dst, int offset, float qx, float qy, float qz, float qw)
        {
            // 絶対値が最大の component を探す。
            float ax = MathF.Abs(qx), ay = MathF.Abs(qy), az = MathF.Abs(qz), aw = MathF.Abs(qw);
            int largest = 0;
            float max = ax;
            if (ay > max) { largest = 1; max = ay; }
            if (az > max) { largest = 2; max = az; }
            if (aw > max) { largest = 3; }

            // 最大 component が正になるようにする (double-cover equivalence)。
            float sign = largest switch { 0 => qx, 1 => qy, 2 => qz, _ => qw };
            if (sign < 0f) { qx = -qx; qy = -qy; qz = -qz; qw = -qw; }

            // 3 つの smallest component を取り出す。
            float a, b, c;
            switch (largest)
            {
                case 0: a = qy; b = qz; c = qw; break;
                case 1: a = qx; b = qz; c = qw; break;
                case 2: a = qx; b = qy; c = qw; break;
                default: a = qx; b = qy; c = qz; break;
            }

            ushort qa = QuantizeSmall(a);
            ushort qb = QuantizeSmall(b);
            ushort qc = QuantizeSmall(c);

            dst[offset] = (byte)largest;
            dst[offset + 1] = (byte)qa;
            dst[offset + 2] = (byte)(qa >> 8);
            dst[offset + 3] = (byte)qb;
            dst[offset + 4] = (byte)(qb >> 8);
            dst[offset + 5] = (byte)qc;
            dst[offset + 6] = (byte)(qc >> 8);
        }

        // ────────────────────────────────────────────────────────────
        //  idle animation
        //
        //  animate される各 bone には、base pose の上に重ねる小さな time-varying delta quaternion を与える。
        //  frequency は 1 Hz 未満で、11 Hz の send rate でもゆっくり自然に見える motion にする。
        //
        //  breathing: ~0.25 Hz (15 breaths/min) on spine/chest
        //  head look: ~0.08-0.15 Hz slow gaze drift
        //  arm sway: ~0.1 Hz subtle pendulum, L/R out of phase
        //  weight shift: ~0.05 Hz leg loading alternation
        //  grip: ~0.07 Hz subtle finger tightening/relaxing
        // ────────────────────────────────────────────────────────────

        private static void GetIdleDelta(int slot, double t, float phase, out float dx, out float dy, out float dz, out float dw)
        {
            // default: identity (この bone には animation なし)
            dx = 0f; dy = 0f; dz = 0f; dw = 1f;

            float p = phase;

            switch (slot)
            {
                case 0: // spine - breathing
                    AxisAngleToQuat(1, 0, 0, 1.5f * MathF.Sin((float)(t * 0.25 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 1: // chest - breathing
                    AxisAngleToQuat(1, 0, 0, 1.0f * MathF.Sin((float)(t * 0.25 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 3: // neck - slow gaze drift (yaw + pitch combined)
                {
                    float yaw = 3f * MathF.Sin((float)(t * 0.08 * TwoPi + p * 1.3));
                    float pitch = 1.5f * MathF.Sin((float)(t * 0.12 * TwoPi + p * 0.7));
                    AxisAngleToQuat(0, 1, 0, yaw, out float yx, out float yy, out float yz, out float yw);
                    AxisAngleToQuat(1, 0, 0, pitch, out float px, out float py, out float pz, out float pw);
                    QuatMul(yx, yy, yz, yw, px, py, pz, pw, out dx, out dy, out dz, out dw);
                    break;
                }

                case 4: // head - micro-nod
                    AxisAngleToQuat(1, 0, 0, 1f * MathF.Sin((float)(t * 0.15 * TwoPi + p * 2.1)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 5: // left upper arm - sway
                    AxisAngleToQuat(1, 0, 0, 2f * MathF.Sin((float)(t * 0.1 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 6: // right upper arm - sway (left と逆 phase)
                    AxisAngleToQuat(1, 0, 0, 2f * MathF.Sin((float)(t * 0.1 * TwoPi + p + MathF.PI)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 7: // left upper leg - weight shift
                    AxisAngleToQuat(0, 0, 1, 1f * MathF.Sin((float)(t * 0.05 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                case 8: // right upper leg - weight shift (opposite)
                    AxisAngleToQuat(0, 0, 1, -1f * MathF.Sin((float)(t * 0.05 * TwoPi + p)),
                        out dx, out dy, out dz, out dw);
                    break;

                default:
                    // finger proximal (slot 21-30): subtle grip change
                    if (slot >= 21 && slot <= 30)
                    {
                        float grip = 5f * MathF.Sin((float)(t * 0.07 * TwoPi + p * 1.1 + slot * 0.3));
                        AxisAngleToQuat(1, 0, 0, grip, out dx, out dy, out dz, out dw);
                    }
                    break;
            }
        }

        // ────────────────────────────────────────────────────────────
        //  quaternion math helper (pure float、Unity dependency なし)
        // ────────────────────────────────────────────────────────────

        private static void SetQuat(int slot, float x, float y, float z, float w)
        {
            int idx = slot * 4;
            BasePose[idx] = x;
            BasePose[idx + 1] = y;
            BasePose[idx + 2] = z;
            BasePose[idx + 3] = w;
        }

        private static void SetAxisAngle(int slot, float ax, float ay, float az, float degrees)
        {
            AxisAngleToQuat(ax, ay, az, degrees, out float qx, out float qy, out float qz, out float qw);
            SetQuat(slot, qx, qy, qz, qw);
        }

        private static void AxisAngleToQuat(float ax, float ay, float az, float degrees, out float qx, out float qy, out float qz, out float qw)
        {
            float half = degrees * Deg2Rad * 0.5f;
            float s = MathF.Sin(half);
            float c = MathF.Cos(half);
            float len = MathF.Sqrt(ax * ax + ay * ay + az * az);
            if (len > 0.0001f)
            {
                float inv = 1f / len;
                ax *= inv; ay *= inv; az *= inv;
            }
            qx = ax * s;
            qy = ay * s;
            qz = az * s;
            qw = c;
        }

        /// <summary>Hamilton product: result = a * b。</summary>
        private static void QuatMul(float ax, float ay, float az, float aw,
                                     float bx, float by, float bz, float bw,
                                     out float rx, out float ry, out float rz, out float rw)
        {
            rw = aw * bw - ax * bx - ay * by - az * bz;
            rx = aw * bx + ax * bw + ay * bz - az * by;
            ry = aw * by - ax * bz + ay * bw + az * bx;
            rz = aw * bz + ax * by - ay * bx + az * bw;
        }

        private static void Normalize(ref float x, ref float y, ref float z, ref float w)
        {
            float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
            if (len > 1e-8f)
            {
                float inv = 1f / len;
                x *= inv; y *= inv; z *= inv; w *= inv;
            }
            else
            {
                x = 0f; y = 0f; z = 0f; w = 1f;
            }
        }

        private static ushort QuantizeSmall(float v)
        {
            if (v < -InvSqrt2) v = -InvSqrt2;
            if (v > InvSqrt2) v = InvSqrt2;
            float t = (v + InvSqrt2) / (2f * InvSqrt2);
            int qi = (int)MathF.Round(t * 65535f);
            if (qi < 0) qi = 0;
            if (qi > 65535) qi = 65535;
            return (ushort)qi;
        }
    }
}
