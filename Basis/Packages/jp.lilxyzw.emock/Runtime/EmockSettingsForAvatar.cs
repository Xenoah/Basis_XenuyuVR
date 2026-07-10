using System.Linq;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace jp.lilxyzw.emock
{
    public static class EmockSettingsForAvatar
    {
        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            SettingsProvider.AvatarCustomizationBuilder += EmockAvatarMenu;
        }

        private static void EmockAvatarMenu(RectTransform container)
        {
            if (BasisLocalPlayer.Instance is not BasisLocalPlayer player || player.BasisAvatar is not BasisAvatar avatar) return;

            var menuItems = avatar.GetComponentsInChildren<EmockMenuItem>(true);
            if (menuItems.Length <= 0) return;

            var menuGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            menuGroup.SetTitle("settings.jp.lilxyzw.emock");

            foreach (var menuItem in menuItems)
            {
                var title = string.IsNullOrEmpty(menuItem.title) ? menuItem.name : menuItem.title;
                var description = string.IsNullOrEmpty(menuItem.description) ? title : menuItem.description;
                if (menuItem.selections == null || menuItem.selections.Count == 0)
                {
                    var button = PanelButton.CreateNew(menuGroup.ContentParent);
                    button.Descriptor.SetDescription(description);
                    if (menuItem.icon)
                    {
                        button.Descriptor.SetTitle("");
                        button.SetIcon(menuItem.icon, false);
                        button.SetHeight(128);
                        button.SetWidth(128);
                    }
                    else button.Descriptor.SetTitle(title);
                    button.OnClicked += menuItem.Reset;
                }
                else if (menuItem.selections.Count == 2)
                {
                    var toggle = PanelToggle.CreateNewEntry(menuGroup.ContentParent);
                    toggle.Descriptor.SetTitle(title);
                    toggle.Descriptor.SetDescription(description);
                    toggle.OnValueChanged += menuItem.ApplyValue;
                    toggle.SetValueWithoutNotify(menuItem.GetValue() == 1);
                }
                else if (menuItem.selections.Count > 2)
                {
                    var dropdown = PanelDropdown.CreateNewEntry(menuGroup.ContentParent);
                    dropdown.Descriptor.SetTitle(title);
                    dropdown.Descriptor.SetDescription(description);
                    dropdown.AssignEntries(menuItem.selections);
                    dropdown.OnValueChanged += menuItem.ApplyValue;
                    var value = (int)menuItem.GetValue();
                    if (value >= 0 && value < menuItem.selections.Count)
                        dropdown.SetValueWithoutNotify(menuItem.selections[value]);
                }
            }

            if (menuItems.Any(m => m.selections != null && m.selections.Count > 1))
            {
                var buttonReset = PanelButton.CreateNew(menuGroup.ContentParent);
                buttonReset.Descriptor.SetTitle(BasisLocalization.Get("settings.jp.lilxyzw.emock.resettodefault"));
                buttonReset.Descriptor.SetDescription(BasisLocalization.Get("settings.jp.lilxyzw.emock.resettodefault.tooltips"));
                buttonReset.OnClicked += () =>
                {
                    foreach (var menuItem in menuItems)
                        if(menuItem.selections.Count > 0) menuItem.Reset();
                };
            }
        }
    }
}
