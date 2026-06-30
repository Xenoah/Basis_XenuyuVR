using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    public abstract class BasisVehicleGimbalablePart : BasisVehiclePart
    {
        [Header("Gimbal")]
        /// <summary>
        /// part が gimbal または rotate できる最大角度 (degrees)。技術的には任意の数値にできるが、
        /// 大きい値では奇妙な挙動になり、90 度付近やそれ以上では特に顕著。
        /// 注意: initial gimbal は object を hierarchy に追加する前に設定する必要がある。
        /// </summary>
        [Range(0.0f, 90.0f)]
        [Tooltip("Recommended values are close to 0.0, usually not more than 30.0.")]
        public float MaxGimbalDegrees = 0.0f;

        /// <summary>
        /// 必要に応じて、linear input に基づく gimbal 調整も許可できる。
        /// たとえば user が前進したくて thruster が下向きの場合、
        /// thruster を少し後ろへ gimbal して前方 thrust を補助できる。
        /// default は thruster で 0.0、hover thruster で 0.5。
        /// </summary>
        [Range(0.0f, 1.0f)]
        [Tooltip("Recommended values are between 0.0 and 0.5.")]
        public float LinearGimbalAdjustRatio = 0.0f;

        /// <summary>
        /// gimbal angle が変化する速度 (degrees/sec)。負なら angle は即時に変化する。
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float GimbalDegreesPerSecond = 60.0f;

        /// <summary>
        /// part を最大 gimbal angle のどの比率まで回すか。
        /// vector の length は 1.0 を超えてはならない。
        /// 注意: initial gimbal は object を hierarchy に追加する前に設定する必要がある。
        /// </summary>
        [Tooltip("Length must not exceed 1.0.")]
        public Vector2 TargetGimbalRatio = Vector2.zero;

        /// <summary>
        /// 現在の gimbal angle (radians)。TargetGimbalRatio * MaxGimbalDegrees * Mathf.Deg2Rad に近づく。
        /// GimbalDegreesPerSecond が負なら target value と等しくなる。
        /// </summary>
        private Vector2 _currentGimbalRadians = Vector2.zero;

        protected BasisCalibratedCoords _parentTransformToBody = BasisCalibratedCoords.Identity;
        protected Quaternion _restQuaternion = Quaternion.identity;
        protected Quaternion _restQuaternionToBody = Quaternion.identity;
        protected Quaternion _bodyToRestQuaternion = Quaternion.identity;
        protected bool _negateGimbal = true;

        protected override void OnEnable()
        {
            base.OnEnable();
            RecalculateTransforms();
            //MakeDebugMesh();
        }

        protected virtual void FixedUpdate()
        {
            // current gimbal radians を target value へ近づける。
            Vector2 targetGimbalRadians = Vector2.ClampMagnitude(TargetGimbalRatio, 1.0f) * (Mathf.Deg2Rad * MaxGimbalDegrees);
            if (GimbalDegreesPerSecond < 0.0f)
            {
                _currentGimbalRadians = targetGimbalRadians;
            }
            else
            {
                float gimbalChange = GimbalDegreesPerSecond * Mathf.Deg2Rad * Time.fixedDeltaTime;
                _currentGimbalRadians = Vector2.MoveTowards(_currentGimbalRadians, targetGimbalRadians, gimbalChange);
            }
            transform.localRotation = _restQuaternion * GetGimbalRotationQuaternion();
        }

        private void RecalculateTransforms()
        {
            if (_parentBody == null)
            {
                return;
            }
            // parent から body への transform を取得する。
            _parentTransformToBody = BasisCalibratedCoords.Identity;
            Transform t = transform.parent;
            while (t != null && t.gameObject != _parentBody.gameObject)
            {
                _parentTransformToBody.rotation = t.localRotation * _parentTransformToBody.rotation;
                _parentTransformToBody.position = t.localPosition + (t.localRotation * _parentTransformToBody.position);
                t = t.parent;
            }
            // part の gimbal の rest orientation rotation を取得する。
            Quaternion gimbalInv = Quaternion.Inverse(GetGimbalRotationQuaternion());
            _restQuaternion = transform.localRotation * gimbalInv;
            // 両方を使って body への rest quaternion とその inverse を求める。
            _restQuaternionToBody = _parentTransformToBody.rotation * _restQuaternion;
            _bodyToRestQuaternion = Quaternion.Inverse(_restQuaternionToBody);
            // この part は center of mass に対してどこにあるか。gimbal の反転が必要な場合がある。
            Quaternion restRot = _parentTransformToBody.rotation * _restQuaternion;
            Vector3 restPos = _parentTransformToBody.position + (_parentTransformToBody.rotation * transform.localPosition);
            Vector3 offset = restPos - _parentBody.centerOfMass;
            _negateGimbal = Vector3.Dot(offset, restRot * Vector3.forward) < 0.0f;
        }

        /// <summary>
        /// 派生 class は linear input を linear force に使う前に、これを呼んで gimbal を設定できる。
        /// </summary>
        protected void SetGimbalFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            if (MaxGimbalDegrees == 0.0f)
            {
                TargetGimbalRatio = Vector2.zero;
                return;
            }
            // local angular input に基づいて gimbal を設定する。
            Vector3 localAngularInput = _bodyToRestQuaternion * angularInput;
            TargetGimbalRatio = Vector2.ClampMagnitude(new Vector2(-localAngularInput.x, -localAngularInput.y), 1.0f);
            // linear input に基づいて gimbal を調整する (任意だが handling を大きく改善する)。
            if (linearInput == Vector3.zero || LinearGimbalAdjustRatio == 0.0f)
            {
                return;
            }
            Quaternion currentRot = _restQuaternionToBody * GetGimbalRotationQuaternion();
            Vector3 localLinearInput = Quaternion.Inverse(currentRot) * linearInput;
            Vector2 linearGimbalAdjust = Vector2.ClampMagnitude(new Vector2(-localLinearInput.y, localLinearInput.x), 1.0f) * LinearGimbalAdjustRatio;
            TargetGimbalRatio = Vector2.ClampMagnitude(TargetGimbalRatio + linearGimbalAdjust, 1.0f);
        }

        protected Quaternion GetGimbalRotationQuaternion()
        {
            if (_currentGimbalRadians == Vector2.zero)
            {
                return Quaternion.identity;
            }
            float angleMag = _currentGimbalRadians.magnitude;
            float sinNorm = Mathf.Sin(angleMag * 0.5f) / angleMag;
            float cosHalf = Mathf.Cos(angleMag * 0.5f);
            return new Quaternion(
                _currentGimbalRadians.x * sinNorm,
                _currentGimbalRadians.y * sinNorm,
                0.0f,
                cosHalf
            );
        }

        /// <summary>
        /// Sqrt(1/2)、つまり sqrt(0.5)、1/sqrt(2)、sqrt(2)/2、sin(45 degrees)、cos(45 degrees)。
        /// </summary>
        private const float SQRT12 = 0.707106781186547524400844362104849f;
        private void MakeDebugMesh()
        {
            // gimbal を可視化する debug mesh を作る。
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(capsule.GetComponent<CapsuleCollider>());
            capsule.transform.SetParent(transform, false);
            capsule.transform.SetLocalPositionAndRotation(new Vector3(0.0f, 0.0f, -1.0f), new Quaternion(SQRT12, 0.0f, 0.0f, SQRT12));
            capsule.transform.localScale = new Vector3(0.1f, 1.0f, 0.1f);
        }
    }
}
