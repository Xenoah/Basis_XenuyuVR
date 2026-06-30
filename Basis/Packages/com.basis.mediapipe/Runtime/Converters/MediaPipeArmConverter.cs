using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// PoseLandmarker のランドマーク (肩/肘/手首) を avatar の腕へ retarget する。
    /// 方向は信頼しやすい 2D 画像位置 (奥行きは減衰) から取り、長さは avatar から取り、
    /// avatar の肩を基準に正面 basis へ写像する。body pose が無い場合は
    /// hand landmarker の手首へ fallback し、avatar の腕が届く範囲へ広げて写像する。
    /// SwapArms/InvertForward は単眼カメラの鏡像曖昧性を吸収する。
    /// </summary>
    public sealed class MediaPipeArmConverter
    {
        public float Smoothing = 0.5f;
        public float DepthGain = 0.5f;
        public float HandReachGain = 1.1f;
        public float HeadReachGain = 1f;
        public bool SwapArms = true;
        public bool InvertForward = false;

        private const int LeftShoulder = 11, RightShoulder = 12;
        private const int LeftElbow = 13, RightElbow = 14;
        private const int LeftWrist = 15, RightWrist = 16;

        private Vector3 _leftElbow, _leftWrist, _rightElbow, _rightWrist;
        private bool _leftWristInit, _leftElbowInit, _rightWristInit, _rightElbowInit;

        /// <summary>player root local 空間の avatar 腕 geometry。manager が毎フレーム再構築する。</summary>
        public struct AvatarArmRig
        {
            public Vector3 LeftAnchor;
            public Vector3 RightAnchor;
            public float LeftUpperLen, LeftForeLen;
            public float RightUpperLen, RightForeLen;
            public Vector3 Right, Up, Forward;
            public Vector3 HeadLocal;
            public float HeadMetric;
            public bool Valid;
        }

        /// <summary>body pose (肩/肘/手首) から elbow pole 付きで腕全体を再構築する。</summary>
        public bool TryGetArm(Vector3[] image, float aspect, in AvatarArmRig rig, bool avatarLeft,
            out Vector3 wristLocal, out Vector3 elbowLocal, out Quaternion wristRotation)
        {
            wristLocal = Vector3.zero;
            elbowLocal = Vector3.zero;
            wristRotation = Quaternion.identity;
            if (!rig.Valid || image == null || image.Length <= RightWrist) return false;

            bool srcLeft = avatarLeft ^ SwapArms;
            Vector3 s = image[srcLeft ? LeftShoulder : RightShoulder];
            Vector3 e = image[srcLeft ? LeftElbow : RightElbow];
            Vector3 w = image[srcLeft ? LeftWrist : RightWrist];

            Vector3 upperDir = MapImageDir(e - s, rig, aspect);
            Vector3 foreDir = MapImageDir(w - e, rig, aspect);
            if (upperDir.sqrMagnitude < 0.5f || foreDir.sqrMagnitude < 0.5f) return false;

            Vector3 anchor = avatarLeft ? rig.LeftAnchor : rig.RightAnchor;
            float upperLen = avatarLeft ? rig.LeftUpperLen : rig.RightUpperLen;
            float foreLen = avatarLeft ? rig.LeftForeLen : rig.RightForeLen;

            Vector3 elbow = SmoothElbow(avatarLeft, anchor + upperDir * upperLen);
            Vector3 wrist = SmoothWrist(avatarLeft, elbow + foreDir * foreLen);

            elbowLocal = elbow;
            wristLocal = wrist;
            wristRotation = LookFrom(wrist - elbow, rig.Up);
            return true;
        }

        /// <summary>
        /// hand landmarker だけを使う手首 fallback。顔がある場合、手首は avatar の頭を基準に置き、
        /// 見かけの顔サイズで scale するため、カメラ距離や framing の影響を受けにくい。
        /// それ以外の場合は肩から avatar の腕が届く範囲へ広げる。
        /// </summary>
        public bool TryGetArmFromHand(Vector3 handWrist, Vector2 headImage, float faceSize, float aspect,
            in AvatarArmRig rig, bool avatarLeft, out Vector3 wristLocal, out Quaternion wristRotation)
        {
            wristLocal = Vector3.zero;
            wristRotation = Quaternion.identity;
            if (!rig.Valid) return false;

            Vector3 anchor = avatarLeft ? rig.LeftAnchor : rig.RightAnchor;
            Vector3 wrist;

            if (faceSize > 1e-4f && rig.HeadMetric > 1e-4f)
            {
                float metric = rig.HeadMetric * HeadReachGain / faceSize;
                float h = (handWrist.x - headImage.x) * aspect * (SwapArms ? -1f : 1f) * metric;
                float v = -(handWrist.y - headImage.y) * metric;
                wrist = rig.HeadLocal + h * rig.Right + v * rig.Up;
            }
            else
            {
                float reach = (avatarLeft ? rig.LeftUpperLen + rig.LeftForeLen : rig.RightUpperLen + rig.RightForeLen) * HandReachGain;
                float h = (0.5f - handWrist.x) * 2f * (SwapArms ? -1f : 1f);
                float v = (0.5f - handWrist.y) * 2f;
                wrist = anchor + (h * rig.Right + v * rig.Up) * reach;
            }

            wrist = SmoothWrist(avatarLeft, wrist);
            wristLocal = wrist;
            wristRotation = LookFrom(wrist - anchor, rig.Up);
            return true;
        }

        public void Reset()
        {
            _leftWristInit = _leftElbowInit = _rightWristInit = _rightElbowInit = false;
        }

        // 画像 landmark は正規化済みで y-down。角度が潰れないよう x は aspect で scale し、
        // z (単眼 depth) は forward 方向へ減衰する。avatar の正面 basis へ写像する。
        private Vector3 MapImageDir(Vector3 d, in AvatarArmRig rig, float aspect)
        {
            float right = d.x * aspect * (SwapArms ? -1f : 1f);
            float up = -d.y;
            float forward = d.z * DepthGain * (InvertForward ? -1f : 1f);
            Vector3 v = right * rig.Right + up * rig.Up + forward * rig.Forward;
            return v.sqrMagnitude > 1e-10f ? v.normalized : Vector3.zero;
        }

        private static Quaternion LookFrom(Vector3 forward, Vector3 up) =>
            forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, up) : Quaternion.identity;

        private Vector3 SmoothWrist(bool left, Vector3 wrist)
        {
            float t = 1f - Mathf.Clamp01(Smoothing);
            if (left)
            {
                _leftWrist = _leftWristInit ? Vector3.Lerp(_leftWrist, wrist, t) : wrist;
                _leftWristInit = true;
                return _leftWrist;
            }
            _rightWrist = _rightWristInit ? Vector3.Lerp(_rightWrist, wrist, t) : wrist;
            _rightWristInit = true;
            return _rightWrist;
        }

        private Vector3 SmoothElbow(bool left, Vector3 elbow)
        {
            float t = 1f - Mathf.Clamp01(Smoothing);
            if (left)
            {
                _leftElbow = _leftElbowInit ? Vector3.Lerp(_leftElbow, elbow, t) : elbow;
                _leftElbowInit = true;
                return _leftElbow;
            }
            _rightElbow = _rightElbowInit ? Vector3.Lerp(_rightElbow, elbow, t) : elbow;
            _rightElbowInit = true;
            return _rightElbow;
        }
    }
}
