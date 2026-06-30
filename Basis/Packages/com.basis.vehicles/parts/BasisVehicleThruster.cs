using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    /// <summary>
    /// vehicle 用の汎用 thruster。gimbal を持ち、force を出す。
    /// BasisVehicleThruster は rocket engine、jet engine、control thruster など任意の thruster 作成に使える。
    /// </summary>
    public class BasisVehicleThruster : BasisVehicleGimbalablePart
    {
        [Header("Thrust")]
        /// <summary>
        /// thruster が提供できる最大 thrust force (Newtons、kg*m/s^2)。負にしてはいけない。
        /// </summary>
        [Tooltip("N (kg\u22C5m/s\u00B2)")]
        public float MaxForce = 0.0f;

        /// <summary>
        /// thruster force が変化する速度 (Newtons/sec)。負なら force は即時に変化する。
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float ForceChangePerSecond = -1.0f;

        /// <summary>
        /// thruster が現在推進に使っている最大 thrust force に対する比率。
        /// </summary>
        [Range(0.0f, 1.0f)]
        [Tooltip("Set at runtime by the vehicle.")]
        public float TargetForceRatio = 0.0f;

        /// <summary>
        /// thruster が現在適用している thrust force。CurrentForceRatio * MaxForce に近づく。
        /// ForceChangePerSecond が負なら target value と等しくなる。
        /// </summary>
        private float _currentForce = 0.0f;

        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            if (_parentBody == null || MaxForce == 0.0f || !Active)
            {
                _currentForce = 0.0f;
                if (_particles != null)
                {
                    _particleEmission.rateOverTime = 0.0f;
                }
                return;
            }
            // current thrust force を target value へ近づける。
            float targetForce = TargetForceRatio * MaxForce;
            if (ForceChangePerSecond < 0.0f)
            {
                _currentForce = targetForce;
            }
            else
            {
                float forceChange = ForceChangePerSecond * Time.fixedDeltaTime;
                _currentForce = Mathf.MoveTowards(_currentForce, targetForce, forceChange);
            }
            if (_particles != null)
            {
                _particleEmission.rateOverTime = 100.0f * Mathf.Abs(_currentForce / MaxForce);
            }
            if (_parentBody == null)
            {
                return;
            }
            // 注意: Unity の AddForceAtPosition は両方の parameter に global world coordinates を使う。
            _parentBody.AddForceAtPosition(transform.forward * _currentForce, transform.position);
        }

        /// <summary>
        /// vehicle input に基づいて steering と thrust を設定する。
        /// </summary>
        /// <param name="angularInput">vehicle の angular input。範囲は -1.0 から 1.0。</param>
        /// <param name="linearInput">vehicle の linear input。範囲は -1.0 から 1.0。</param>
        public override void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            // vehicle angular input から gimbal を設定する。
            SetGimbalFromVehicleInput(angularInput, linearInput);
            // vehicle linear input から thrust を設定する。
            Quaternion rotationToBody = _restQuaternionToBody * GetGimbalRotationQuaternion();
            Vector3 thrustDirection = rotationToBody * new Vector3(0.0f, 0.0f, 1.0f);
            TargetForceRatio = Mathf.Clamp01(Vector3.Dot(linearInput, thrustDirection));
        }
    }
}
