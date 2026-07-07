using UnityEngine;
using UnityEngine.SceneManagement;

namespace jp.lilxyzw.basispatcher
{
    public class UpdateInvoker : MonoBehaviour
    {
        private static UpdateInvoker Instance;
        private void Update() => IManagedUpdate.Invoke();
        private void LateUpdate() => IManagedLateUpdate.Invoke();

        private static void AddToScene()
        {
            if (!Instance) Instance = new GameObject("UpdateInvoker").AddComponent<UpdateInvoker>();
        }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            AddToScene();
            SceneManager.sceneLoaded += (_,_) => AddToScene();
        }
    }
}
