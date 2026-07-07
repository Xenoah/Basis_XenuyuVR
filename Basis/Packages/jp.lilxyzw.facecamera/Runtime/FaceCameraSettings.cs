using System;
using System.Linq;
using Basis;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine;

namespace jp.lilxyzw.facecamera
{
    public static class FaceCameraSettings
    {
        public static BasisSettingsBinding<bool> EnableFaceCamera = new("facecamera: enable face camera", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> Anchor = new("facecamera: anchor", new BasisPlatformDefault<string>(nameof(AnchorType.LeftBottom)));
        public static BasisSettingsBinding<float> Opacity = new("facecamera: opacity", new BasisPlatformDefault<float>(0.5f));
        public static BasisSettingsBinding<float> SizeDesktop = new("facecamera: desktop size", new BasisPlatformDefault<float>(0.1f));
        public static BasisSettingsBinding<float> OffsetXDesktop = new("facecamera: desktop x offset", new BasisPlatformDefault<float>(0.05f));
        public static BasisSettingsBinding<float> OffsetYDesktop = new("facecamera: desktop y offset", new BasisPlatformDefault<float>(0.05f));
        public static BasisSettingsBinding<float> SizeVR = new("facecamera: vr size", new BasisPlatformDefault<float>(0.1f));
        public static BasisSettingsBinding<float> OffsetXVR = new("facecamera: vr x offset", new BasisPlatformDefault<float>(0.05f));
        public static BasisSettingsBinding<float> OffsetYVR = new("facecamera: vr y offset", new BasisPlatformDefault<float>(0.05f));

        public static void LoadAll()
        {
            EnableFaceCamera.LoadBindingValue();
            Anchor.LoadBindingValue();
            Opacity.LoadBindingValue();
            SizeDesktop.LoadBindingValue();
            OffsetXDesktop.LoadBindingValue();
            OffsetYDesktop.LoadBindingValue();
            SizeVR.LoadBindingValue();
            OffsetXVR.LoadBindingValue();
            OffsetYVR.LoadBindingValue();
        }

        private static void FaceCameraTab(RectTransform container)
        {
            PanelSectionToggleHelpers.CreateCollapsibleFlatSection(container,
                BasisLocalization.Get("settings.jp.lilxyzw.facecamera"), () =>
            {
                PanelToggle toggleEnableFaceCamera = PanelToggle.CreateNewEntry(container);
                toggleEnableFaceCamera.Descriptor.SetTitle(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.enabled"));
                toggleEnableFaceCamera.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.enabled.tooltip"));
                toggleEnableFaceCamera.AssignBinding(EnableFaceCamera);
                toggleEnableFaceCamera.OnValueChanged += _ => FaceCamera.UpdateUI();

                PanelDropdown dropdownAnchor = PanelDropdown.CreateNewEntry(container);
                dropdownAnchor.Descriptor.SetTitle(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.anchor"));
                dropdownAnchor.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.anchor.tooltip"));
                dropdownAnchor.AssignBinding(Anchor);
                dropdownAnchor.AssignEntries(Enum.GetNames(typeof(AnchorType)).ToList());
                dropdownAnchor.OnValueChanged += _ => FaceCamera.UpdateUI();

                PanelSlider sliderOpacity = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.opacity"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    Opacity
                );
                sliderOpacity.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.opacity.tooltip"));
                sliderOpacity.OnValueChanged += _ => FaceCamera.UpdateUI();

                AddModeSettings(container, BasisLocalization.Get("settings.jp.lilxyzw.facecamera.desktop"), SizeDesktop, OffsetXDesktop, OffsetYDesktop);
                AddModeSettings(container, BasisLocalization.Get("settings.jp.lilxyzw.facecamera.vr"), SizeVR, OffsetXVR, OffsetYVR);
            });
        }

        private static void AddModeSettings(RectTransform container, string label, BasisSettingsBinding<float> Size, BasisSettingsBinding<float> OffsetX, BasisSettingsBinding<float> OffsetY)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            group.SetTitle(label);

            PanelSlider sliderSize = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.size"), 0f, 0.5f, false, 2, ValueDisplayMode.Raw),
                Size
            );
            sliderSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.size.tooltip"));
            sliderSize.OnValueChanged += _ => FaceCamera.UpdateUI();

            PanelSlider sliderOffsetX = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.offsetx"), 0f, 0.5f, false, 2, ValueDisplayMode.Raw),
                OffsetX
            );
            sliderOffsetX.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.offsetx.tooltip"));
            sliderOffsetX.OnValueChanged += _ => FaceCamera.UpdateUI();

            PanelSlider sliderOffsetY = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.offsety"), 0f, 0.5f, false, 2, ValueDisplayMode.Raw),
                OffsetY
            );
            sliderOffsetY.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.facecamera.offsety.tooltip"));
            sliderOffsetY.OnValueChanged += _ => FaceCamera.UpdateUI();
        }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            basispatcher.CommonSettings.addSettings += FaceCameraTab;
            basispatcher.CommonSettings.load += LoadAll;
        }
    }

    public enum AnchorType
    {
        LeftTop = 0,
        RightTop = 1,
        LeftBottom = 2,
        RightBottom = 3
    }
}
