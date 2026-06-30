using Basis.Scripts.Common;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    [RequireComponent(typeof(WheelCollider))]
    public class BasisVehicleWheel : BasisVehiclePart
    {
        [Header("Steering")]
        /// <summary>
        /// wheel が steering できる最大角度 (radians)。
        /// </summary>
        [Range(0.0f, 90.0f)]
        [Tooltip("Realistic values are less than 45.0 degrees.")]
        public float MaxSteeringAngleDegrees = 0.0f;

        /// <summary>
        /// wheel steering angle が変化する速度 (degrees/sec)。負なら angle は即時に変化する。
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float SteeringDegreesPerSecond = 60.0f;

        /// <summary>
        /// wheel を最大 steering angle のどの比率まで回すか。
        /// </summary>
        [Range(-1.0f, 1.0f)]
        [Tooltip("Negative values mean turn left. Set at runtime by the vehicle.")]
        public float TargetSteeringRatio = 0.0f;

        /// <summary>
        /// 現在の steering angle (degrees)。CurrentSteeringRatio * MaxSteeringAngleDegrees に近づく。
        /// SteeringDegreesPerSecond が負なら target value と等しくなる。
        /// </summary>
        private float _currentSteeringAngleDegrees = 0.0f;

        [Header("Force")]
        /// <summary>
        /// wheel が推進に提供できる最大 force (Newtons、kg*m/s^2)。
        /// </summary>
        [Tooltip("N (kg\u22C5m/s\u00B2)")]
        public float MaxPropulsionForce = 0.0f;

        /// <summary>
        /// vehicle が停止しようとしているときに wheel がかける braking force (Newtons、kg*m/s^2)。
        /// 負なら、wheel は propulsion force を braking として使う。
        /// </summary>
        [Tooltip("Negative means use propulsion force as braking.")]
        public float BrakingForce = -1.0f;

        /// <summary>
        /// wheel propulsion force が変化する速度 (Newtons/sec)。負なら force は即時に変化する。
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float PropulsionForceChangePerSecond = -1.0f;

        /// <summary>
        /// wheel が推進に使っている最大 force に対する比率。
        /// </summary>
        [Range(-1.0f, 1.0f)]
        [Tooltip("Negative values mean reverse. Set at runtime by the vehicle.")]
        public float TargetPropulsionForceRatio = 0.0f;

        /// <summary>
        /// wheel が現在適用している force。CurrentForceRatio * MaxForce に近づく。
        /// ForceChangePerSecond が負なら target value と等しくなる。
        /// </summary>
        private float _currentForce = 0.0f;

        private WheelCollider _wheelCollider = null;
        private Dictionary<Transform, BasisCalibratedCoords> _childTransforms = new();
        private bool _negateSteering = false;

        protected override void OnEnable()
        {
            base.OnEnable();
            if (transform.localPosition.z < 0.0f)
            {
                _negateSteering = true;
            }
            _wheelCollider = GetComponent<WheelCollider>();
            // wheel と一緒に回転させる child transform をすべて探す。
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (_childTransforms.ContainsKey(child) == false)
                {
                    BasisCalibratedCoords coords = new BasisCalibratedCoords(child.localPosition, child.localRotation);
                    _childTransforms.Add(child, coords);
                }
            }
        }
        private void FixedUpdate()
        {
            if (!Active)
            {
                _currentForce = 0.0f;
                _wheelCollider.brakeTorque = 0.0f;
                _wheelCollider.motorTorque = 0.0f;
                if (_particles != null)
                {
                    _particleEmission.rateOverTime = 0.0f;
                }
                return;
            }
            // wheel collider の steering angle を target へ近づける。
            float steerTarget = TargetSteeringRatio * MaxSteeringAngleDegrees;
            if (SteeringDegreesPerSecond < 0.0f)
            {
                _currentSteeringAngleDegrees = steerTarget;
            }
            else
            {
                float steerChange = SteeringDegreesPerSecond * Time.fixedDeltaTime;
                _currentSteeringAngleDegrees = Mathf.MoveTowards(_currentSteeringAngleDegrees, steerTarget, steerChange);
            }
            _wheelCollider.steerAngle = _currentSteeringAngleDegrees;
            // wheel が近づく target force を求める。
            float forceTarget = 0.0f;
            bool shouldWheelsBrake = false;
            if (_parentVehicleBody != null)
            {
                forceTarget = TargetPropulsionForceRatio * MaxPropulsionForce;
                shouldWheelsBrake = _parentVehicleBody.AngularDampeners && _parentVehicleBody.LinearDampeners && _parentVehicleBody.AngularActivation == Vector3.zero && _parentVehicleBody.LinearActivation == Vector3.zero;
            }
            if (shouldWheelsBrake)
            {
                float brakeForce = BrakingForce < 0.0f ? forceTarget : BrakingForce;
                // 注意: Unity の brakeTorque は braking direction に関係なく必ず正でなければならない。
                _wheelCollider.brakeTorque = Mathf.Abs(brakeForce * _wheelCollider.radius);
                forceTarget = 0.0f; // Ramp down the motor torque to zero while braking.
            }
            else
            {
                _wheelCollider.brakeTorque = 0.0f;
            }
            // wheel collider の motor torque を target へ近づける。
            if (PropulsionForceChangePerSecond < 0.0f)
            {
                _currentForce = forceTarget;
            }
            else
            {
                float forceChange = PropulsionForceChangePerSecond * Time.fixedDeltaTime;
                _currentForce = Mathf.MoveTowards(_currentForce, forceTarget, forceChange);
            }
            if (_particles != null)
            {
                _particleEmission.rateOverTime = 100.0f * Mathf.Abs(_currentForce) / MaxPropulsionForce;
            }
            // 注意: Unity の motorTorque は Newton-meters (N*m または kg*m^2/s^2) だが、forceAmount は Newtons (N または kg*m/s^2)。
            _wheelCollider.motorTorque = _currentForce * _wheelCollider.radius;
            // child object をすべて wheel collider の rotation と position に合わせる。
            _wheelCollider.GetWorldPose(out Vector3 wheelPosition, out Quaternion wheelRotation);
            foreach (KeyValuePair<Transform, BasisCalibratedCoords> entry in _childTransforms)
            {
                Transform child = entry.Key;
                BasisCalibratedCoords coords = entry.Value;
                child.position = wheelPosition + (wheelRotation * coords.position);
                child.rotation = wheelRotation * coords.rotation;
            }
        }

        /// <summary>
        /// vehicle input に基づいて wheel の steering と thrust を設定する。
        /// </summary>
        /// <param name="angularInput">vehicle の angular input。範囲は -1.0 から 1.0。</param>
        /// <param name="linearInput">vehicle の linear input。範囲は -1.0 から 1.0。</param>
        public override void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            // NOTE: この code は 0 steering が forward を意味する wheel のみを support する。
            // 理想的には他の rotation の wheel も許可したいが、より複雑になる。
            // linear input が強い場合はそれを優先し、それ以外は angular input で steering を設定する。
            float source = (Mathf.Abs(linearInput.x) * 2.0f > Mathf.Abs(angularInput.y)) ? linearInput.x : angularInput.y;
            float steerRatio = source * source;
            if ((source < 0.0f) != _negateSteering)
            {
                steerRatio = -steerRatio;
            }
            TargetSteeringRatio = Mathf.Clamp(steerRatio, -1.0f, 1.0f);
            // vehicle linear input から thrust を設定する。
            float forceRatio = linearInput.z * Mathf.Cos(_currentSteeringAngleDegrees * Mathf.Deg2Rad);
            TargetPropulsionForceRatio = Mathf.Clamp(forceRatio, -1.0f, 1.0f);
        }
    }
}
