using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.InputSystem;

namespace jp.lilxyzw.emock
{
    internal static class EmockInputManager
    {
        private static EmockController controller;
        private static bool isInitialized = false;

        public static void Initialize(EmockController inController)
        {
            controller = inController;
            if (isInitialized) return;
            isInitialized = true;
            EmockSettings.LoadAll();

            var devices = BasisDeviceManagement.Instance.AllInputDevices;
            devices.OnListAdded += AddDeviceAction;

            int count = devices.Count;
            for (int i = 0; i < count; i++)
                AddDeviceAction(devices[i]);
        }

        private static void AddDeviceAction(BasisInput device)
        {
            if (!device || !device.TryGetRole(out var role) || role != BasisBoneTrackedRole.LeftHand && role != BasisBoneTrackedRole.RightHand) return;
            if (role == BasisBoneTrackedRole.LeftHand)
            {
                device.CurrentInputState.OnSecondary2DAxisChanged += () => LeftTrackpad(device.CurrentInputState.Secondary2DAxisRaw, device.CurrentInputState.Secondary2DAxisClick);
                device.CurrentInputState.OnTriggerChanged += () => SetLeft(device.CurrentInputState.Trigger > 0.5f ? 1f : 0f);
                device.CurrentInputState.OnGripButtonChanged += () => SetLeft(device.CurrentInputState.GripButton ? 2f : 0f);
                device.CurrentInputState.OnPrimary2DAxisClickChanged += ResetLeft;
                device.CurrentInputState.OnPrimary2DAxisChanged += ResetLeft;
            }
            else
            {
                device.CurrentInputState.OnSecondary2DAxisChanged += () => RightTrackpad(device.CurrentInputState.Secondary2DAxisRaw, device.CurrentInputState.Secondary2DAxisClick);
                device.CurrentInputState.OnTriggerChanged += () => SetRight(device.CurrentInputState.Trigger > 0.5f ? 1f : 0f);
                device.CurrentInputState.OnGripButtonChanged += () => SetRight(device.CurrentInputState.GripButton ? 2f : 0f);
                device.CurrentInputState.OnPrimary2DAxisClickChanged += ResetRight;
                device.CurrentInputState.OnPrimary2DAxisChanged += ResetRight;
            }
        }

        public static void FromKeyboard()
        {
            if (Keyboard.current.leftShiftKey.isPressed)
            {
                if (Keyboard.current.f1Key.wasPressedThisFrame) SetLeft(0f);
                if (Keyboard.current.f2Key.wasPressedThisFrame) SetLeft(1f);
                if (Keyboard.current.f3Key.wasPressedThisFrame) SetLeft(2f);
                if (Keyboard.current.f4Key.wasPressedThisFrame) SetLeft(3f);
                if (Keyboard.current.f5Key.wasPressedThisFrame) SetLeft(4f);
                if (Keyboard.current.f6Key.wasPressedThisFrame) SetLeft(5f);
                if (Keyboard.current.f7Key.wasPressedThisFrame) SetLeft(6f);
                if (Keyboard.current.f8Key.wasPressedThisFrame) SetLeft(7f);
            }
            if (Keyboard.current.rightShiftKey.isPressed)
            {
                if (Keyboard.current.f1Key.wasPressedThisFrame) SetRight(0f);
                if (Keyboard.current.f2Key.wasPressedThisFrame) SetRight(1f);
                if (Keyboard.current.f3Key.wasPressedThisFrame) SetRight(2f);
                if (Keyboard.current.f4Key.wasPressedThisFrame) SetRight(3f);
                if (Keyboard.current.f5Key.wasPressedThisFrame) SetRight(4f);
                if (Keyboard.current.f6Key.wasPressedThisFrame) SetRight(5f);
                if (Keyboard.current.f7Key.wasPressedThisFrame) SetRight(6f);
                if (Keyboard.current.f8Key.wasPressedThisFrame) SetRight(7f);
            }
        }

        private static void SetLeft(float value)
        {
            if (EmockSettings.ChangeByController.RawValue && controller) controller.SetParameter("LeftHand", value);
        }

        private static void SetRight(float value)
        {
            if (EmockSettings.ChangeByController.RawValue && controller) controller.SetParameter("RightHand", value);
        }

        private static void ResetLeft()
        {
            if (EmockSettings.ResetUponMoving.RawValue) SetLeft(0f);
        }

        private static void ResetRight()
        {
            if (EmockSettings.ResetUponMoving.RawValue) SetRight(0f);
        }

        private static void LeftTrackpad(Vector2 vec, bool isTouch)
        {
            if (!isTouch) return;
            if (vec.sqrMagnitude < 0.25f)
            {
                SetLeft(1f);
            }
            else if (vec.x < 0f)
            {
                if (vec.y > -vec.x * 0.577350f) SetLeft(3f);
                else if (vec.y > vec.x * 0.577350f) SetLeft(4f);
                else SetLeft(5f);
            }
            else
            {
                if (vec.y > 0f) SetLeft(7f);
                else SetLeft(6f);
            }
        }

        private static void RightTrackpad(Vector2 vec, bool isTouch)
        {
            if (!isTouch) return;
            if (vec.sqrMagnitude < 0.25f)
            {
                SetRight(1f);
            }
            else if (vec.x > 0f)
            {
                if (vec.y > vec.x * 0.577350f) SetRight(3f);
                else if (vec.y > -vec.x * 0.577350f) SetRight(4f);
                else SetRight(5f);
            }
            else
            {
                if (vec.y > 0f) SetRight(7f);
                else SetRight(6f);
            }
        }
    }
}
