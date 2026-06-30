using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    /// <summary>
    /// vehicle 用 hover thruster。hovercraft thrust に使う。現実的ではなく SF 寄りの挙動。
    /// </summary>
    public class BasisVehicleHoverThruster : BasisVehicleGimbalablePart
    {
        /// <summary>
        /// vehicle の angular input が hover ratio にどの程度影響するかを制御する。
        /// 0 = なし、1 以上 = stabilization が強すぎる (overcorrection/bounciness)。
        /// </summary>
        private const float TorqueStabilization = 0.5f;

        [Header("Hover Thrust")]
        /// <summary>
        /// hover thruster が提供できる最大 hover energy (Newton-meters、N*m または kg*m^2/s^2)。負にしてはいけない。
        /// </summary>
        [Tooltip("N\u22C5m (kg\u22C5m\u00B2/s\u00B2)")]
        public float MaxHoverEnergy = 0.0f;

        /// <summary>
        /// hover energy が変化する速度 (Newton-meters/sec)。負なら force は即時に変化する。
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float HoverEnergyChangePerSecond = -1.0f;

        /// <summary>
        /// hover thruster が推進に使っている最大 hover energy に対する比率。
        /// </summary>
        [Range(0.0f, 1.0f)]
        [Tooltip("Set at runtime by the vehicle.")]
        public float TargetHoverRatio = 0.0f;

        /// <summary>
        /// thruster が現在適用している hover energy。CurrentHoverRatio * MaxHoverEnergy に近づく。
        /// HoverEnergyChangePerSecond が負なら target value と等しくなる。
        /// </summary>
        private float _currentHoverEnergy = 0.0f;

        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            if (_parentBody == null || !Active)
            {
                _currentHoverEnergy = 0.0f;
                if (_particles != null)
                {
                    _particleEmission.rateOverTime = 0.0f;
                }
                return;
            }
            // current hover energy を target value へ近づける。
            float targetHoverEnergy = Mathf.Clamp01(TargetHoverRatio) * MaxHoverEnergy;
            if (HoverEnergyChangePerSecond < 0.0f)
            {
                _currentHoverEnergy = targetHoverEnergy;
            }
            else
            {
                float hoverEnergyChange = HoverEnergyChangePerSecond * Time.fixedDeltaTime;
                _currentHoverEnergy = Mathf.MoveTowards(_currentHoverEnergy, targetHoverEnergy, hoverEnergyChange);
            }
            if (_particles != null)
            {
                _particleEmission.rateOverTime = 100.0f * Mathf.Abs(_currentHoverEnergy) / MaxHoverEnergy;
            }
            // raycast で ground までの距離を求める。hover thruster は自然に、
            // ground に近いほど thrust を増やし、遠いほど thrust を減らす。
            Vector3 rayOrigin = transform.position;
            // Unity は forward に +Z を使い、これは glTF の +Z object front に対応する。
            // thruster は +Z 方向へ thrust するため、"nozzle" は -Z を向く。
            Vector3 rayDir = -transform.forward;
            RaycastHit hit;
            if (!Physics.Raycast(rayOrigin, rayDir, out hit, 1000.0f))
            {
                return;
            }
            float hitDistance = Mathf.Max(hit.distance, 0.01f); // Avoid division by zero or near-zero.
            float hoverForce = _currentHoverEnergy / hitDistance; // N = Nm / m
            // 注意: Unity の AddForceAtPosition は両方の parameter に global world coordinates を使う。
            _parentBody.AddForceAtPosition(transform.forward * hoverForce, transform.position);
        }

        public override void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            SetGimbalFromVehicleInput(angularInput, linearInput);
            // angular/linear input に基づいて hover ratio を設定する。
            Vector3 thrustDir = _restQuaternionToBody * GetGimbalRotationQuaternion() * new Vector3(0, 0, 1);
            float thrustHover = Mathf.Max(Vector3.Dot(linearInput, thrustDir), 0.0f);
            Vector3 torque = Vector3.Cross(_parentTransformToBody * transform.localPosition, thrustDir);
            float torqueHover = Mathf.Max(Vector3.Dot(angularInput, torque) * TorqueStabilization, 0.0f);
            TargetHoverRatio = Mathf.Clamp01(thrustHover + torqueHover);
        }

        public BasisVehicleHoverThruster()
        {
            // hover thruster には default である程度の linear gimbal adjustment を持たせる。
            LinearGimbalAdjustRatio = 0.5f;
        }
    }
}
