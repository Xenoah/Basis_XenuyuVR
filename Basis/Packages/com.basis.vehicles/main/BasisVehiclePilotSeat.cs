using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Main
{
    /// <summary>
    /// occupant が BasisVehicleBody node を操縦するための BasisSeat。
    /// </summary>
    public class BasisVehiclePilotSeat : BasisSdk.Interactions.BasisSeat
    {
        /// <summary>
        /// pilot seat が support する control scheme。各 member の summary では簡単のため
        /// keyboard/mouse を参照するが、各 device の実際の bound control は Basis input mapping を参照。
        /// </summary>
        public enum ControlScheme
        {
            /// <summary>
            /// vehicle の component に基づいて control scheme を自動判定する。
            /// throttle 付き vehicle は Navball、wheel 付き vehicle は Car、
            /// hover thruster vehicle は SixDofHorizontal、それ以外は SixDof を default にする。
            /// </summary>
            Auto,
            /// <summary>
            /// control なし。vehicle は pilot input を無視する。
            /// </summary>
            None,
            /// <summary>
            /// 多くの driving game と同様に、前後移動に WS、steering に AD を使う。
            /// car に能力があれば pitch に RF、roll に QEUO も使う。
            /// </summary>
            Car,
            /// <summary>
            /// Space Engineers のように、linear movement に WASDRF、roll に QE、mouse pitch/yaw、または IJKLUO rotation を使う。
            /// </summary>
            SixDof,
            /// <summary>
            /// SixDof に似るが、Minecraft creative mode のように horizontal WASDRF input を平面化する。
            /// Empyrion 風 hovercraft control に向くが、代わりに Car を使いたい場合もある。
            /// </summary>
            SixDofHorizontal,
            /// <summary>
            /// rotation に WASDQE を使い、W が up、S が down。flight sim と比べて pitch は反転する。
            /// RF は forward/backward または throttle up/down として使う。
            /// </summary>
            Navball,
            /// <summary>
            /// Kerbal Space Program や flight sim のように rotation に WASDQE を使い、W が down、S が up。
            /// RF は forward/backward または throttle up/down として使う。
            /// </summary>
            NavballInverted,
        }

        private const float ThrottleRate = 0.5f;

        [Header("Pilot Seat Settings")]
        /// <summary>
        /// 使用する control scheme。BasisVehiclePilotSeat.cs を編集すれば追加できる。
        /// </summary>
        [Tooltip("Auto considers the vehicle's parts.")]
        public ControlScheme controlScheme = ControlScheme.Auto;

        /// <summary>
        /// <see cref="EnterPilotSeat"/> 呼び出し時に自動設定される。
        /// custom use case では override もできる。
        /// </summary>
        [Tooltip("Set when the local player enters the seat.")]
        public bool UseLocalControls = false;

        private BasisVehicleBody _pilotedVehicleBody = null;
        /// <summary>
        /// 操縦対象の vehicle body。
        /// </summary>
        public BasisVehicleBody PilotedVehicleBody
        {
            get { return _pilotedVehicleBody; }
            set
            {
                _pilotedVehicleBody = value;
                if (value != null && value.PilotSeat != this)
                {
                    value.PilotSeat = this;
                }
            }
        }

        /// <summary>
        /// player が <see cref="EnterPilotSeat"/> 経由で pilot seat に入ったとき、runtime で設定されるべき値。
        /// </summary>
        [Tooltip("Leave blank, this is set at runtime.")]
        public BasisPlayer PilotingPlayer = null;
        public override void Awake()
        {
            ResetPitchOnEntry = true;
            base.Awake();
            if (_pilotedVehicleBody == null)
            {
                Transform parent = transform.parent;
                if (parent != null)
                {
                    _pilotedVehicleBody = parent.GetComponent<BasisVehicleBody>();
                }
            }
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.PilotSeat = this;
            }
            else
            {
                BasisDebug.LogError("BasisVehiclePilotSeat should be a direct child of a GameObject with a BasisVehicleBody component.");
            }
            OnLocalPlayerEnterSeat += EnterPilotSeat;
            OnLocalPlayerExitSeat += ExitPilotSeat;
        }

        private bool _loggedMissingLocalPlayer;
        private void FixedUpdate()
        {
            if (!UseLocalControls || _pilotedVehicleBody == null)
            {
                return;
            }
            if (BasisLocalPlayer.Instance == null || BasisLocalPlayer.Instance.LocalCharacterDriver == null)
            {
                BasisDebug.LogErrorOnce(ref _loggedMissingLocalPlayer, "BasisVehiclePilotSeat cannot read pilot input: local player or character driver is missing.");
                return;
            }
            _loggedMissingLocalPlayer = false;
            ControlScheme actualControlScheme = GetActualControlScheme();
            Vector3 angularInput = GetAngularInput(actualControlScheme);
            Vector3 linearInput = GetLinearInput(actualControlScheme);
            _pilotedVehicleBody.AngularActivation = angularInput;
            bool throttleZero = BasisVehiclePilotSeatInputActions.Instance.ThrottleZero.IsPressed();
            if (throttleZero)
            {
                _pilotedVehicleBody.LinearActivation = Vector3.zero;
            }
            else if (_pilotedVehicleBody.UseThrottle)
            {
                Vector3 change = ThrottleRate * linearInput * Time.fixedDeltaTime;
                _pilotedVehicleBody.LinearActivation = Vector3.ClampMagnitude(_pilotedVehicleBody.LinearActivation + change, 1.0f);
            }
            else
            {
                _pilotedVehicleBody.LinearActivation = linearInput;
            }
        }

        public void EnterPilotSeat(BasisPlayer player)
        {
            Debug.Assert(player != null);
            PilotingPlayer = player;
            UseLocalControls = player.IsLocal;
            if (UseLocalControls)
            {
                var vehicleInput = BasisVehiclePilotSeatInputActions.Instance;
                vehicleInput.EnableAll(DoesPilotSeatNeedMouseInput());

                vehicleInput.ThrottleZero.performed += OnThrottleZero;
                vehicleInput.ToggleAngularDampeners.performed += OnToggleAngular;
                vehicleInput.ToggleLinearDampeners.performed += OnToggleLinear;
            }
        }
        private void OnThrottleZero(UnityEngine.InputSystem.InputAction.CallbackContext ctx) => SetThrottleToZero();
        private void OnToggleAngular(UnityEngine.InputSystem.InputAction.CallbackContext ctx) => ToggleAngularDampeners();
        private void OnToggleLinear(UnityEngine.InputSystem.InputAction.CallbackContext ctx) => TogggleLinearDampeners();

        public void ExitPilotSeat(BasisPlayer player)
        {
            if (PilotingPlayer != player)
            {
                return;
            }
            PilotingPlayer = null;
            if (UseLocalControls)
            {
                var vehicleInput = BasisVehiclePilotSeatInputActions.Instance;

                vehicleInput.ThrottleZero.performed -= OnThrottleZero;
                vehicleInput.ToggleAngularDampeners.performed -= OnToggleAngular;
                vehicleInput.ToggleLinearDampeners.performed -= OnToggleLinear;

                vehicleInput.DisableAll();
            }
            UseLocalControls = false;
            // exit 時に activation を 0 にする (gas pedal が踏まれっぱなしの car を避ける)。
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.AngularActivation = Vector3.zero;
                _pilotedVehicleBody.LinearActivation = Vector3.zero;
            }
        }

        public void SetThrottleToZero()
        {
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.AngularActivation = Vector3.zero;
                _pilotedVehicleBody.LinearActivation = Vector3.zero;
            }
        }

        public void ToggleAngularDampeners()
        {
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.AngularDampeners = !_pilotedVehicleBody.AngularDampeners;
            }
        }

        public void TogggleLinearDampeners()
        {
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.LinearDampeners = !_pilotedVehicleBody.LinearDampeners;
            }
        }

        public bool DoesPilotSeatNeedMouseInput()
        {
            // 6DoF control scheme では pitch/yaw に desktop user の mouse input が必要。
            ControlScheme actualControlScheme = GetActualControlScheme();
            return actualControlScheme == ControlScheme.SixDof || actualControlScheme == ControlScheme.SixDofHorizontal;
        }

        public bool DoesPilotSeatWantToKeepUpright()
        {
            ControlScheme actualControlScheme = GetActualControlScheme();
            return actualControlScheme == ControlScheme.Car || actualControlScheme == ControlScheme.SixDofHorizontal;
        }

        /// <summary>
        /// throttle 付き vehicle は Navball、wheel 付き vehicle は Car、
        /// hover thruster vehicle は SixDofHorizontal、それ以外は SixDof を default にする。
        /// </summary>
        /// <returns>実際に使う control scheme (Auto 以外)。</returns>
        private ControlScheme GetActualControlScheme()
        {
            if (controlScheme != ControlScheme.Auto)
            {
                return controlScheme;
            }
            if (_pilotedVehicleBody.UseThrottle)
            {
                return ControlScheme.Navball;
            }
            if (_pilotedVehicleBody.HasWheels())
            {
                return ControlScheme.Car;
            }
            if (_pilotedVehicleBody.HasHoverThrusters())
            {
                return ControlScheme.SixDofHorizontal;
            }
            return ControlScheme.SixDof;
        }

        /// <summary>
        /// 現在の control scheme に基づいて angular input を取得する。
        /// この function の詳細は Basis 固有で、unstable とみなし、変更され得る。
        /// </summary>
        /// <param name="actualControlScheme">実際に使われている control scheme。</param>
        /// <returns>angular input vector。</returns>
        private Vector3 GetAngularInput(ControlScheme actualControlScheme)
        {
            if (actualControlScheme == ControlScheme.None)
            {
                return Vector3.zero;
            }
            Vector2 rawCharRot = BasisLocalPlayer.Instance.LocalCharacterDriver.Rotation;
            if (DoesPilotSeatNeedMouseInput() && BasisVehiclePilotSeatInputActions.Instance.IsPilotSeatOnlyLockerOfLookRotation())
            {
                rawCharRot += Device_Management.Devices.Desktop.BasisDesktopEye.Instance.LookRotationVector * 2.0f;
            }
            Vector2 charRot = StretchToSquare(Vector2.ClampMagnitude(rawCharRot, 1.0f));
            Vector2 charMove = StretchToSquare(BasisLocalPlayer.Instance.LocalCharacterDriver.MovementVector);
            float roll = BasisVehiclePilotSeatInputActions.Instance.RotateRoll.ReadValue<float>();
            switch (actualControlScheme)
            {
                case ControlScheme.Navball:
                    return new Vector3(-charMove.y, charMove.x, roll);
                case ControlScheme.NavballInverted:
                    return new Vector3(charMove.y, charMove.x, roll);
                default: break;
            }
            return new Vector3(-charRot.y, charRot.x, roll);
        }

        /// <summary>
        /// 現在の control scheme に基づいて linear input を取得する。
        /// この function の詳細は Basis 固有で、unstable とみなし、変更され得る。
        /// </summary>
        /// <param name="actualControlScheme">実際に使われている control scheme。</param>
        /// <returns>linear input vector。</returns>
        private Vector3 GetLinearInput(ControlScheme actualControlScheme)
        {
            if (actualControlScheme == ControlScheme.None)
            {
                return Vector3.zero;
            }
            Vector2 charMove = StretchToSquare(BasisLocalPlayer.Instance.LocalCharacterDriver.MovementVector);
            float vert = BasisLocalPlayer.Instance.LocalCharacterDriver.GetVerticalMovement();
            switch (actualControlScheme)
            {
                case ControlScheme.Navball:
                    return new Vector3(0.0f, 0.0f, vert);
                case ControlScheme.NavballInverted:
                    return new Vector3(0.0f, 0.0f, vert);
                case ControlScheme.SixDofHorizontal:
                    {
                        Quaternion vehicleRotation = _pilotedVehicleBody.transform.rotation;
                        Vector3 euler = vehicleRotation.eulerAngles;
                        Quaternion flatten = Quaternion.Euler(-euler.x, 0.0f, -euler.z);
                        Vector3 input = new Vector3(charMove.x, vert, charMove.y);
                        return flatten * input;
                    }
                default: break;
            }
            return new Vector3(charMove.x, vert, charMove.y);
        }

        /// <summary>
        /// circle に制限された Vector2 を、連続的に square へ引き伸ばす。
        /// </summary>
        /// <param name="vector">circle に制限された vector。</param>
        /// <returns>square に制限された vector。</returns>
        private static Vector2 StretchToSquare(Vector2 vector)
        {
            if (vector == Vector2.zero)
            {
                return vector;
            }
            // scale = 1.0 / max(abs(x), abs(y))
            float scale = 1.0f / Mathf.Max(Mathf.Abs(vector.x), Mathf.Abs(vector.y));
            return vector * (scale * vector.magnitude);
        }
    }
}
