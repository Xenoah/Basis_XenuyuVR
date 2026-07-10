using Basis.Scripts.Settings;
using Basis;
using Basis.BasisUI;
using UnityEngine;

namespace jp.lilxyzw.emock
{
    internal static class EmockSettings
    {
        public static BasisSettingsBinding<bool> ChangeByController = new("emock: change by controller", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> ResetUponMoving = new("emock: reset upon moving", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> StopDistance = new("emock: stop distance", new BasisPlatformDefault<float>(50));

        public static void LoadAll()
        {
            ChangeByController.LoadBindingValue();
            ResetUponMoving.LoadBindingValue();
            StopDistance.LoadBindingValue();
        }

        private static void EmockTab(RectTransform container)
        {
            PanelSectionToggleHelpers.CreateCollapsibleFlatSection(container,
                BasisLocalization.Get("settings.jp.lilxyzw.emock"), () =>
            {
                PanelToggle toggleChangeByController = PanelToggle.CreateNewEntry(container);
                toggleChangeByController.Descriptor.SetTitle(BasisLocalization.Get("settings.jp.lilxyzw.emock.changebycontroller"));
                toggleChangeByController.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.emock.changebycontroller.tooltips"));
                toggleChangeByController.AssignBinding(ChangeByController);

                PanelToggle toggleResetUponMoving = PanelToggle.CreateNewEntry(container);
                toggleResetUponMoving.Descriptor.SetTitle(BasisLocalization.Get("settings.jp.lilxyzw.emock.resetuponmoving"));
                toggleResetUponMoving.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.emock.resetuponmoving.tooltips"));
                toggleResetUponMoving.AssignBinding(ResetUponMoving);

                PanelSlider sliderStopDistance = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Distance(BasisLocalization.Get("settings.jp.lilxyzw.emock.stopdistance"), 100),
                    StopDistance
                );
                sliderStopDistance.Descriptor.SetTooltip(BasisLocalization.Get("settings.jp.lilxyzw.emock.stopdistance.tooltips"));
            });
        }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            basispatcher.CommonSettings.addSettings += EmockTab;
            basispatcher.CommonSettings.load += LoadAll;
        }
    }
}
