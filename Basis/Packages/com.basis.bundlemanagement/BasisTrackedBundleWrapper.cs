using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
[System.Serializable]
public class BasisTrackedBundleWrapper
{
    [SerializeField]
    public BasisLoadableBundle LoadableBundle;
    [SerializeField]
    public AssetBundle AssetBundle;
    #if UNITY_BUNDLEUNLOAD
    [SerializeField]
    public bool IsBundleBackingStoreReleased = false;
    #endif
    private int _requestedTimes = 0;
    public bool IsInUse => Volatile.Read(ref _requestedTimes) > 0;
    public bool DidErrorOccur = false;
    public Task BundleLoadTask;
    public static TimeSpan TimeSpan = TimeSpan.FromSeconds(BasisBeeConstants.TimeUntilMemoryRemoval);
    /// <summary>
    /// 例として scene path を保持する。scene が unload されたかを判定し、
    /// memory を解放するために使える。
    /// </summary>
    public string MetaLink;
    // bundle loading の完了を await する method
    public async Task WaitForBundleLoadAsync()
    {
        // bundle loading process の simulation。実際の loading logic に置き換えられる
        while (!IsBundleCompleteAndLoaded())
        {
            if (DidErrorOccur)
            {
                return;
            }
            await Task.Yield(); // Yield to avoid blocking the main thread
        }
    }
    // bundle が完全に loaded か確認する method
    private bool IsBundleCompleteAndLoaded()
    {
        // bundle が loaded か確認する実際の logic はここに実装できる
        return AssetBundle != null; // Assuming AssetBundle being non-null means it's loaded
    }


    // TODO: ここに bug がある
    // 同じ scene を複数 load して、そのうち 1 つを unload したとき
    // 他の duplicate scene も削除してしまう可能性がある?
    public async Task<bool> UnloadIfReady()
    {
        #if !UNITY_SERVER
        if (AssetBundle == null)
        {
            BasisDebug.LogError("Asset Bundle was null this should never occur");
            return false;
        }
        #endif
        if (Volatile.Read(ref _requestedTimes) <= 0)
        {
            await Task.Delay(TimeSpan);
            if (Volatile.Read(ref _requestedTimes) <= 0)
            {
                if (AssetBundle == null)
                {
                    #if UNITY_BUNDLEUNLOAD
                    if (IsBundleBackingStoreReleased)
                    {
                        return true;
                    }

                    BasisDebug.LogError("Asset Bundle was null this should never occur");
                    #endif
                    return false;
                }
                BasisDebug.Log("Unloading Bundle " + AssetBundle.name);
                AssetBundle.Unload(true);
                #if UNITY_BUNDLEUNLOAD
                AssetBundle = null;
                IsBundleBackingStoreReleased = true;
                #endif
                return true;
            }
            else
            {
                BasisDebug.Log("Stopping Unload Process, Bundle was Incremented again after Requested Time");
                return false;
            }
        }
        else
        {
            return false;
        }
    }
    public bool Increment()
    {
        Interlocked.Increment(ref _requestedTimes);
     //   BasisDebug.Log($"Incremented Asset Load {LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation}");
        return true;
    }
    public bool DeIncrement()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _requestedTimes);
            if (current <= 0)
            {
                BasisDebug.LogError("Trying to DeIncrement more than what was loaded, please check Increment and DeIncrement Logic");
                return false;
            }
        } while (Interlocked.CompareExchange(ref _requestedTimes, current - 1, current) != current);

       // BasisDebug.Log($"DeIncremented Asset Load {LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation}");
        return true;
    }
#if UNITY_BUNDLEUNLOAD
    public void ReleaseBundleBackingStore()
    {

        if (AssetBundle == null)
        {
            return;
        }

        BasisDebug.Log("Releasing bundle backing store " + AssetBundle.name);
        AssetBundle.Unload(false);
        AssetBundle = null;
        IsBundleBackingStoreReleased = true;
        BasisDebug.Log("Bundle backing store released for headless scene bundle.");

    }
    #endif
}
