using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using static BundledContentHolder;
namespace Basis.Scripts.Addressable_Driver.Resource
{
    public static class AddressableResourceProcess
    {
        public static async Task<GameObject> LoadAsGameObjectsAsync(GameObject TempSpawnDisableGameobject,string loadstring,InstantiationParameters instantiationParameters, ChecksRequired Required, Selector Selector, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
        {
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> data = Addressables.LoadAssetAsync<GameObject>(loadstring);

            object result = await data.Task;

            if (result is GameObject resource)
            {
                GameObject spawned = ContentPoliceControl.ContentControl(TempSpawnDisableGameobject, resource, Required, instantiationParameters.Position, instantiationParameters.Rotation, false, Vector3.zero, Selector, instantiationParameters.Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop);
                return spawned;
            }

            // load 失敗 (key に対応する addressable location がない、または asset type が違う) は
            // null result で完了する。そのため以前の "Unexpected result type: " + result.GetType() は
            // null を dereference し、本当の原因ではなく誤解を招く NullReferenceException をここで投げていた。
            // handle を release し、明確な message を投げる。BasisAvatarFactory.LoadAvatarRemote などの caller は
            // これを catch して fallback-avatar path を走らせる。
            string reason = result == null
                ? (data.OperationException?.Message ?? "no addressable location for key")
                : "unexpected result type " + result.GetType();
            if (data.IsValid())
            {
                Addressables.Release(data);
            }
            throw new System.Exception($"Failed to load '{loadstring}' as a GameObject: {reason}");
        }
        /// <summary>
        /// system based gameobject を load する。
        /// required check 付きで load する処理を回避するために使う。
        /// </summary>
        /// <param name="loadstring"></param>
        /// <param name="InstantiationParameters"></param>
        /// <returns></returns>
        public static async Task<GameObject> LoadSystemGameobject(GameObject TempSpawnDisableGameobject, string loadstring, InstantiationParameters InstantiationParameters)
        {
            ChecksRequired Required = new ChecksRequired(false, false, false,false);
            GameObject data = await AddressableResourceProcess.LoadAsGameObjectsAsync(TempSpawnDisableGameobject, loadstring, InstantiationParameters, Required, BundledContentHolder.Selector.System);
            return data;
        }
        public static void ReleaseGameobject(GameObject Reference)
        {
            if (Reference != null)
            {
                Addressables.ReleaseInstance(Reference);
                if (Reference != null)
                {
                    GameObject.Destroy(Reference);
                }
            }
        }
    }
}
