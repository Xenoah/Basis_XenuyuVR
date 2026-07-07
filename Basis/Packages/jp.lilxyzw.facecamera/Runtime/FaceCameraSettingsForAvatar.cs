using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using jp.lilxyzw.basispatcher;
using UnityEngine;

namespace jp.lilxyzw.facecamera
{
    public static class FaceCameraSettingsForAvatar
    {
        public static float GetRange() => AvatarSettings.Get("facecamera", "Range", 0.1f);
        public static float GetOffsetY() => AvatarSettings.Get("facecamera", "OffsetY", 0.0f);

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            SettingsProvider.AvatarCustomizationBuilder += FaceCameraAvatarMenu;
        }

        private static void FaceCameraAvatarMenu(RectTransform container)
        {
            if (BasisLocalPlayer.Instance is not BasisLocalPlayer player || player.BasisAvatar is not BasisAvatar avatar) return;

            var menuGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            menuGroup.SetTitle(BasisLocalization.Get("settings.jp.lilxyzw.facecamera"));

            var sliderRange = PanelSlider.CreateNew(menuGroup.ContentParent);
            sliderRange.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.range"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            sliderRange.Descriptor.SetDescription(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.range.tooltip"));
            sliderRange.OnValueChanged += value => ApplyValue("Range", value, 0.1f);
            sliderRange.SetValueWithoutNotify(GetRange());

            var sliderOffsetY = PanelSlider.CreateNew(menuGroup.ContentParent);
            sliderOffsetY.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.offsetyavatar"), -1f, 1f, false, 2, ValueDisplayMode.Raw));
            sliderOffsetY.Descriptor.SetDescription(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.offsetyavatar.tooltip"));
            sliderOffsetY.OnValueChanged += value => ApplyValue("OffsetY", value, 0.0f);
            sliderOffsetY.SetValueWithoutNotify(GetOffsetY());
        }

        private static void ApplyValue(string key, float value, float defaultValue)
        {
            AvatarSettings.Set("facecamera", key, value, defaultValue);
            FaceCamera.InitializeFaceCam();
        }
    }
}
