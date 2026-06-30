using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Main
{
    [RequireComponent(typeof(Rigidbody))]
    public class BasisVehicleBody : MonoBehaviour
    {
        private const float InertiaDampenerRateAngular = 4.0f;
        private const float InertiaDampenerRateLinear = 1.0f;

        /// <summary>
        /// pilot seat / driver seat として使う node。この seat に座った player が vehicle を制御する。
        /// </summary>
        [Tooltip("Can be null to set automatically.")]
        public BasisVehiclePilotSeat PilotSeat = null;

        /// <summary>
        /// vehicle の angular force 比率を制御する input value。
        /// 各 axis は -1.0 から 1.0 の範囲で、input 全体の長さは 1.0 を超える場合がある。
        /// </summary>
        [Tooltip("Each axis is on a range of -1.0 to 1.0.")]
        public Vector3 AngularActivation = Vector3.zero;
        /// <summary>
        /// vehicle の linear force 比率を制御する input value。
        /// 各 axis は -1.0 から 1.0 の範囲で、input 全体の長さは 1.0 を超える場合がある。
        /// </summary>
        [Tooltip("Each axis is on a range of -1.0 to 1.0.")]
        public Vector3 LinearActivation = Vector3.zero;

        /// <summary>
        /// part 由来の torque を除いた vehicle 固有の gyroscope torque。単位は Newton-meters/radian (kg*m^2/s^2/rad)。
        /// </summary>
        [Tooltip("N\u22C5m/rad (kg\u22C5m\u00B2/s\u00B2/rad)")]
        public Vector3 GyroscopeTorque = Vector3.zero;

        /// <summary>
        /// 非負の場合、vehicle がそれ以上加速しない目標 speed (meters/sec)。
        /// throttle 使用時、正なら activation はこの speed の比率、負なら thrust power の比率になる。
        /// </summary>
        [Tooltip("Negative means no speed limit.")]
        public float MaxSpeed = -1.0f;

        /// <summary>
        /// true の場合、特定 rotation の angular activation input がないとき vehicle は rotation を減速する。
        /// </summary>
        [Tooltip("Should the vehicle slow its rotation automatically?")]
        public bool AngularDampeners = true;
        /// <summary>
        /// true の場合、特定 direction の linear activation input がないとき vehicle は自身を減速する。
        /// </summary>
        [Tooltip("Should the vehicle slow itself down automatically?")]
        public bool LinearDampeners = true;
        /// <summary>
        /// true の場合、vehicle は linear movement に throttle を使う。pilot seat input は離しても「残る」。
        /// MaxSpeed が非負なら throttle はその speed の比率、それ以外は thrust power の比率になる。
        /// </summary>
        [Tooltip("Persist linear input and use as a ratio of MaxSpeed or thrust power.")]
        public bool UseThrottle = false;

        private List<Parts.BasisVehicleHoverThruster> _hoverThrusters = new List<Parts.BasisVehicleHoverThruster>();
        private List<Parts.BasisVehicleWheel> _wheels = new List<Parts.BasisVehicleWheel>();
        private List<Parts.BasisVehiclePart> _otherParts = new List<Parts.BasisVehiclePart>();
        public Rigidbody rb;
        /// <summary>
        /// server-authoritative な "locked" state。library の Static toggle が on のとき
        /// <see cref="Basis.Network.Vehicles.BasisNetworkedVehicle"/> により設定される。
        /// true の間、FixedUpdate は force を適用しない。
        /// </summary>
        public bool IsLocked = false;
        private void Awake()
        {
            if (PilotSeat != null)
            {
                PilotSeat.PilotedVehicleBody = this;
            }
            rb = GetComponent<Rigidbody>();
            RegisterChildParts();
        }

        private void RegisterChildParts()
        {
            Parts.BasisVehiclePart[] parts = GetComponentsInChildren<Parts.BasisVehiclePart>(true);
            for (int i = 0; i < parts.Length; i++)
            {
                if (NearestBody(parts[i].transform) == this)
                {
                    RegisterPart(parts[i]);
                }
            }
        }

        private static BasisVehicleBody NearestBody(Transform partTransform)
        {
            Transform parent = partTransform.parent;
            while (parent != null)
            {
                if (parent.TryGetComponent(out Rigidbody _))
                {
                    parent.TryGetComponent(out BasisVehicleBody body);
                    return body;
                }
                parent = parent.parent;
            }
            return null;
        }

        private void FixedUpdate()
        {
            if (rb == null)
            {
                BasisDebug.LogError("BasisVehicleBody: No Rigidbody found on the vehicle body.");
                return;
            }
            // locked (static) vehicle は force を適用しない。全員に対して frozen になる。
            if (IsLocked)
            {
                return;
            }
            Vector3 actualAngular = AngularActivation;
            Vector3 actualLinear = LinearActivation;
            Vector3 localLinearVel = transform.InverseTransformDirection(rb.linearVelocity);
            Vector3 localAngularVel = transform.InverseTransformDirection(rb.angularVelocity);
            Vector3 localUpDirection = -GetLocalGravityDirection();
            // activation、throttle、dampener に基づき、実際に使う linear value を決定する。
            if (MaxSpeed >= 0.0f)
            {
                // この場合、throttle は maximum speed の比率であり、
                // vehicle が target speed に合うよう thrust を調整する。
                Vector3 targetVelocity = MaxSpeed * Vector3.ClampMagnitude(LinearActivation, 1.0f);
                actualLinear = (targetVelocity - localLinearVel) / MaxSpeed;
            }
            else if (!UseThrottle && LinearDampeners)
            {
                if (Mathf.Approximately(actualLinear.x, 0.0f))
                {
                    actualLinear.x = localLinearVel.x * -InertiaDampenerRateLinear;
                }
                if (Mathf.Approximately(actualLinear.y, 0.0f))
                {
                    actualLinear.y = localLinearVel.y * -InertiaDampenerRateLinear;
                }
                if (Mathf.Approximately(actualLinear.z, 0.0f))
                {
                    actualLinear.z = localLinearVel.z * -InertiaDampenerRateLinear;
                }
                if (_hoverThrusters.Count > 0)
                {
                    actualLinear += (LinearActivation != Vector3.zero) ? localUpDirection : localUpDirection * 0.75f;
                }
            }
            // vehicle wheel は dampener によって回転させない。wheel にとっては、
            // まっすぐ向くことが vehicle の回転停止に最も近い動きだから。
            for (int i = 0; i < _wheels.Count; i++)
            {
                _wheels[i].SetFromVehicleInput(actualAngular, actualLinear);
            }
            // activation と dampener に基づき、実際に使う angular value を決定する。
            if (AngularDampeners)
            {
                if (Mathf.Approximately(AngularActivation.x, 0.0f))
                {
                    actualAngular.x = localAngularVel.x * -InertiaDampenerRateAngular;
                }
                if (Mathf.Approximately(AngularActivation.y, 0.0f))
                {
                    actualAngular.y = localAngularVel.y * -InertiaDampenerRateAngular;
                }
                if (Mathf.Approximately(AngularActivation.z, 0.0f))
                {
                    actualAngular.z = localAngularVel.z * -InertiaDampenerRateAngular;
                }
                // hovercraft や car などは自身を upright に保とうとする。
                if (PilotSeat != null && PilotSeat.DoesPilotSeatWantToKeepUpright())
                {
                    Quaternion toUp = GetRotationToUpright(localUpDirection);
                    Vector3 v = Vector3.ClampMagnitude(new Vector3(toUp.x, 0.0f, toUp.z), 1.0f);
                    // その axis に input がない場合だけ upright correction を適用する。
                    if (Mathf.Approximately(AngularActivation.x, 0.0f))
                    {
                        actualAngular.x += v.x;
                    }
                    if (Mathf.Approximately(AngularActivation.z, 0.0f))
                    {
                        actualAngular.z += v.z;
                    }
                }
            }
            // 実際の input を axis ごとに -1.0 から 1.0 へ clamp する (全体の長さは 1.0 を超え得る)。
            // 個々の part (thruster など) は必要に応じてさらに clamp できる (例: 長さ 1.0)。
            actualAngular = new Vector3(
                Mathf.Clamp(actualAngular.x, -1.0f, 1.0f),
                Mathf.Clamp(actualAngular.y, -1.0f, 1.0f),
                Mathf.Clamp(actualAngular.z, -1.0f, 1.0f)
            );
            actualLinear = new Vector3(
                Mathf.Clamp(actualLinear.x, -1.0f, 1.0f),
                Mathf.Clamp(actualLinear.y, -1.0f, 1.0f),
                Mathf.Clamp(actualLinear.z, -1.0f, 1.0f)
            );
            // throttle と dampener を含む実際の angular/linear input を計算したので、
            // wheel 以外のすべてへ適用する。
            rb.AddTorque(transform.TransformDirection(Vector3.Scale(GyroscopeTorque, actualAngular)), ForceMode.Force);
            for (int i = 0; i < _hoverThrusters.Count; i++)
            {
                _hoverThrusters[i].SetFromVehicleInput(actualAngular, actualLinear);
            }
            for (int i = 0; i < _otherParts.Count; i++)
            {
                _otherParts[i].SetFromVehicleInput(actualAngular, actualLinear);
            }
        }

        public bool HasHoverThrusters()
        {
            return _hoverThrusters.Count > 0;
        }

        public bool HasWheels()
        {
            return _wheels.Count > 0;
        }

        public void RegisterPart(Parts.BasisVehiclePart part)
        {
            if (part is Parts.BasisVehicleHoverThruster hoverThruster)
            {
                if (!_hoverThrusters.Contains(hoverThruster)) _hoverThrusters.Add(hoverThruster);
            }
            else if (part is Parts.BasisVehicleWheel wheel)
            {
                if (!_wheels.Contains(wheel)) _wheels.Add(wheel);
            }
            else
            {
                if (!_otherParts.Contains(part)) _otherParts.Add(part);
            }
        }

        private Quaternion GetRotationToUpright(Vector3 upDirection)
        {
            if (upDirection == Vector3.zero)
            {
                return Quaternion.identity;
            }
            Vector3 x = Vector3.Cross(upDirection, Vector3.forward).normalized;
            Vector3 z = Vector3.Cross(x, upDirection).normalized;
            return Quaternion.LookRotation(z, upDirection);
        }

        private Vector3 GetLocalGravityDirection()
        {
            // TODO: ここでは gravity が常に global であると仮定しているが、将来の Basis では変わる可能性がある。
            return Quaternion.Inverse(transform.rotation) * Physics.gravity.normalized;
        }
    }
}
