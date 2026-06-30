using Basis.Scripts.Device_Management;
using UnityEngine;

namespace Basis.Integration.AudioLink
{
    /// <summary>
    /// Basis mode switch 後に AudioLink globals を再 publish し、global lookup で <c>_AudioTexture</c> を解決する consumer が Desktop/VR 遷移をまたいでも動き続けるようにする。
    /// </summary>
    /// <remarks><see href="https://github.com/llealloo/audiolink/issues/365"/> の workaround。</remarks>
    internal static class BasisAudioLinkRepublisher
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Subscribe()
        {
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        }

        private static void OnBootModeChanged(string mode)
        {
            global::AudioLink.AudioLink audioLink = Object.FindFirstObjectByType<global::AudioLink.AudioLink>();
            if (audioLink == null || !audioLink.AudioLinkEnabled)
            {
                return;
            }

            audioLink.AudioLinkEnabled = false;
            audioLink.AudioLinkEnabled = true;
        }
    }
}
