using System;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine;

namespace jp.lilxyzw.basispatcher
{
    // A callback is being added to allow multiple plugins to add settings to a single tab, preventing the number of settings tabs from becoming excessive.
    public static class CommonSettings
    {
        public static Action load;
        public static Action<RectTransform> addSettings;

        private static PanelTabPage AddTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetTitle("Plugins");
            addSettings?.Invoke(descriptor.ContentParent);
            return tab;
        }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            SettingsProvider.ExternalTabs.Add(("Plugins", AddTab));
            BasisSettingsSystem.OnSettingsFinishedChanges += () => load?.Invoke();
        }
    }
}
