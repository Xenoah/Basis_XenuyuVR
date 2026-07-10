using jp.lilxyzw.basispatcher;
using UnityEditor;

namespace jp.lilxyzw.emock.Editor
{
    public static class EmockPatcher
    {
        [MenuItem("Tools/lilBasisPatcher/Add Emock Components")]
        private static void AddEmockComponents()
        {
            ComponentPatcher.AddAvatarComponents(
                "jp.lilxyzw.emock.EmockAnimator",
                "jp.lilxyzw.emock.EmockController",
                "jp.lilxyzw.emock.EmockMenuItem",
                "jp.lilxyzw.emock.EmockNetwork"
            );
        }
    }
}
