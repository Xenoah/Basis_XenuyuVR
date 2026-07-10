using System.Linq;
using UnityEditor;
using UnityEngine;

namespace jp.lilxyzw.basispatcher
{
    // Adds plugin component type names to Basis' ContentPolice allow-lists
    // (the ScriptableObjects that decide which components may load on
    // avatars, props and scenes).
    // Please do not call this automatically. Let the user perform the action.
    public static class ComponentPatcher
    {
        public static void AddAvatarComponents(params string[] types) => AddComponents("8393424ebf34934449c2d1309a700837", "AvatarContentPoliceSelector.asset", types);
        public static void AddPropComponents(params string[] types) => AddComponents("85698603c757bf749b527a1278cd01ec", "PropContentPoliceSelector.asset", types);
        public static void AddSceneComponents(params string[] types) => AddComponents("3c5f85e575845554b98b078e8fe97860", "SceneContentPoliceSelector.asset", types);

        public static void AddComponents(string guid, string filename, params string[] types)
        {
            var police = AssetDatabase.LoadAssetByGUID<Object>(new GUID(guid));
            if (!police)
            {
                EditorUtility.DisplayDialog("lilBasisPatcher", $"\"{filename}\" not found.", "OK");
                return;
            }
            using var so = new SerializedObject(police);
            using var selectedTypes = so.FindProperty("selectedTypes");
            var size = selectedTypes.arraySize;
            var needToAdds = types.ToHashSet();
            for (int i = 0; i < size; i++)
            {
                using var element = selectedTypes.GetArrayElementAtIndex(i);
                needToAdds.Remove(element.stringValue);
            }
            var needToAddSize = needToAdds.Count;
            if (needToAddSize == 0)
            {
                EditorUtility.DisplayDialog("lilBasisPatcher", "All components have already been added.", "OK");
                return;
            }
            var fullsize = size + needToAddSize;
            selectedTypes.arraySize = fullsize;
            foreach (var needToAdd in needToAdds)
            {
                using var element = selectedTypes.GetArrayElementAtIndex(size);
                element.stringValue = needToAdd;
                size++;
            }
            EditorUtility.DisplayDialog("lilBasisPatcher", $"The following components have been added to \"{filename}\".\r\n\r\n{string.Join("\r\n", needToAdds)}", "OK");
            so.ApplyModifiedProperties();
        }
    }
}
