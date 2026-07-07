using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using UnityEngine;

public class BasisOpenMenuForcefully : MonoBehaviour
{
    public bool OpenServerMenu = true;
    public string ProviderTitleKey = "menu.provider.publicWorlds";
    public void Start()
    {
        if(BasisDeviceManagement.OnInitializationComplete)
        {
            OpenMenu();
        }
        else
        {
            BasisDeviceManagement.OnInitializationCompleted += OpenMenu;
        }
    }
    public void OnDestroy()
    {
        BasisDeviceManagement.OnInitializationCompleted -= OpenMenu;
    }
    public void OpenMenu()
    {
        BasisMainMenu.Open();
        if (OpenServerMenu)
        {
            // SakiikaVR: the Servers panel is retired — scenes that still carry
            // its serialized key are routed to the Worlds panel instead.
            string key = ProviderTitleKey == "menu.provider.servers"
                ? "menu.provider.publicWorlds"
                : ProviderTitleKey;
            BasisMainMenu.OpenWithProvider(BasisLocalization.Get(key));
        }
        else
        {
            BasisMainMenu.Open();
        }
    }
}
