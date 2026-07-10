using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using jp.lilxyzw.basispatcher;
using UnityEngine;

namespace jp.lilxyzw.emock
{
    [AddComponentMenu("Emock/Emock Menu Item")]
    public class EmockMenuItem : MonoBehaviour, IEmockAvatarLoad
    {
        public string title;
        public string description;
        public string parameter;
        public List<string> selections;
        public EmockController controller;
        public SaveType saveType;
        public float defaultValue;
        public Sprite icon;

        private float value;

        public void OnAvatarReady(BasisAvatar avatar, bool IsOwner)
        {
            if (IsOwner)
            {
                if (!controller)
                {
                    Debug.LogError("EmockMenuItem: EmockController not found.");
                    Destroy(this);
                }
                value = defaultValue;
                if (saveType == SaveType.None) return;
                if (saveType == SaveType.Avatar)
                {
                    value = AvatarSettings.Get("emock", parameter, defaultValue);
                }
                else if (saveType == SaveType.Global)
                {
                    value = AvatarSettings.GetGlobal("emock", parameter, defaultValue);
                }

                if (controller) controller.SetParameter(parameter, value);
            }
            else Destroy(this);
        }

        public void Reset() => ApplyValue(defaultValue);

        public void ApplyValue(float value)
        {
            this.value = value;
            if (controller) controller.SetParameter(parameter, value);
            if (saveType == SaveType.Avatar)
            {
                AvatarSettings.Set("emock", parameter, value, defaultValue);
            }
            else if (saveType == SaveType.Global)
            {
                AvatarSettings.SetGlobal("emock", parameter, value, defaultValue);
            }
        }

        public void ApplyValue(string choice) => ApplyValue(selections.IndexOf(choice));

        public void ApplyValue(bool value) => ApplyValue(value ? 1f : 0f);

        public float GetValue() => value;
    }

    public enum SaveType
    {
        None,
        Avatar,
        Global
    }
}
