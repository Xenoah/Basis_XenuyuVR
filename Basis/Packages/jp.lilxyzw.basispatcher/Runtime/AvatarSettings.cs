using Basis.Scripts.BasisSdk.Players;
using HVR.Basis.Comms;
using UnityEngine;

namespace jp.lilxyzw.basispatcher
{
    // Avatar-specific settings are saved in the same file used by Vixxy to avoid creating unnecessary extra files.
    public static class AvatarSettings
    {
        public static float Get(string plugin, string key, float defaultValue)
        {
            if (HVRVixxyPersistentStore.TryGet($"{plugin}.parameter:{BasisLocalPlayer.CurrentAvatarUniqueID}|{key}", out var value)) return value;
            return defaultValue;
        }

        public static void Set(string plugin, string key, float value, float defaultValue)
        {
            HVRVixxyPersistentStore.Set($"{plugin}.parameter:{BasisLocalPlayer.CurrentAvatarUniqueID}|{key}", value, defaultValue);
        }

        public static float GetGlobal(string plugin, string key, float defaultValue)
        {
            if (HVRVixxyPersistentStore.TryGet($"{plugin}.parameter:{key}", out var value)) return value;
            return defaultValue;
        }

        public static void SetGlobal(string plugin, string key, float value, float defaultValue)
        {
            HVRVixxyPersistentStore.Set($"{plugin}.parameter:{key}", value, defaultValue);
        }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            Application.quitting += HVRVixxyPersistentStore.FlushNow;
        }
    }
}
