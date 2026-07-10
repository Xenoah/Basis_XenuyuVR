using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using jp.lilxyzw.basispatcher;
using UnityEngine;

namespace jp.lilxyzw.emock
{
    // This component functions only for the avatar owner.
    // It determines the animation index based on player input and transmits that value over the network.
    [AddComponentMenu("Emock/Emock Controller")]
    public class EmockController : MonoBehaviour, IEmockAvatarLoad, IManagedUpdate
    {
        public EmockNetwork emockNetwork;
        public EmockStateGroup[] groups = {};

        private readonly Dictionary<string, float> parameters = new();
        public bool isInitialized = false;
        public bool needToUpdate = false;

        // Call this from external components
        public void SetParameter(string name, float value)
        {
            parameters[name] = value;
            needToUpdate = true;
        }

        public float GetParameter(string name)
        {
            if (parameters.ContainsKey(name)) return parameters[name];
            return parameters[name] = 0;
        }

        private void Awake()
        {
            #if UNITY_EDITOR
            isInitialized = true;
            IManagedUpdate.Add(this);
            EmockInputManager.Initialize(this);
            if (Application.platform == RuntimePlatform.WindowsEditor ||
                Application.platform == RuntimePlatform.LinuxEditor ||
                Application.platform == RuntimePlatform.OSXEditor) return;
            #endif

            if (!emockNetwork)
            {
                Debug.LogError("EmockNetwork: EmockNetwork not found.");
                Destroy(this);
                return;
            }
        }

        #if UNITY_EDITOR
        public void Update() => EmockInputManager.Initialize(this);
        #endif

        public void ManagedUpdate()
        {
            if (!isInitialized) return;

            EmockInputManager.FromKeyboard();

            if (needToUpdate)
            {
                needToUpdate = false;
                bool isUpdated = false;
                foreach (var group in groups)
                {
                    bool isMatchGroup = true;
                    foreach (var condition in group.conditions)
                    {
                        if (!condition.IsMatch(GetParameter(condition.name)))
                        {
                            isMatchGroup = false;
                            break;
                        }
                    }
                    if (!isMatchGroup) continue;

                    foreach (var state in group.states)
                    {
                        bool isMatchState = true;
                        foreach (var condition in state.conditions)
                        {
                            if (!condition.IsMatch(GetParameter(condition.name)))
                            {
                                isMatchState = false;
                                break;
                            }
                        }
                        if (!isMatchState) continue;

                        emockNetwork.SetIndex(state.index);
                        isUpdated = true;
                        break;
                    }
                    break;
                }

                if (!isUpdated) emockNetwork.SetIndex(0);
            }
        }

        public void OnAvatarReady(BasisAvatar avatar, bool IsOwner)
        {
            if (IsOwner)
            {
                isInitialized = true;
                IManagedUpdate.Add(this);
                EmockInputManager.Initialize(this);
            }
            else Destroy(this);
        }

        private void OnDestroy()
        {
            IManagedUpdate.Remove(this);
        }
    }

    [Serializable]
    public class EmockStateGroup
    {
        public EmockConditions[] conditions = {};
        public EmockState[] states = {};
    }

    [Serializable]
    public class EmockState
    {
        public EmockConditions[] conditions = {};
        public ushort index;
    }

    [Serializable]
    public class EmockConditions
    {
        public string name;
        public float value;
        public EmockConditionMode mode = EmockConditionMode.Equals;

        public bool IsMatch(float parameterValue)
        {
            return mode switch
            {
                EmockConditionMode.If => parameterValue > 0,
                EmockConditionMode.IfNot => parameterValue <= 0,
                EmockConditionMode.Greater => parameterValue > value,
                EmockConditionMode.Less => parameterValue < value,
                EmockConditionMode.Equals => parameterValue == value,
                EmockConditionMode.NotEqual => parameterValue != value,
                _ => false
            };
        }
    }

    public enum EmockConditionMode
    {
        If = 1,
        IfNot = 2,
        Greater = 3,
        Less = 4,
        Equals = 6,
        NotEqual = 7
    }
}
