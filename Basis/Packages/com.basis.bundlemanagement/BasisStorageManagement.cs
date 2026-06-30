using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// disk 上の BEE file storage を管理する。total size を追跡し、設定可能な上限 (default 128 GB) を適用し、
/// 最も古い file (file write time による LRU) を evict し、listing/deletion API を提供する。
/// </summary>
public static class BasisStorageManagement
{
    /// <summary>
    /// default の最大 cache size (bytes、128 GB)。
    /// </summary>
    public const long DefaultMaxCacheSizeBytes = 128L * 1024 * 1024 * 1024;

    /// <summary>
    /// 現在の最大 cache size (bytes)。settings で更新される。
    /// </summary>
    public static long MaxCacheSizeBytes = DefaultMaxCacheSizeBytes;

    /// <summary>
    /// 保存済み BEE file 1 件と metadata を表す。
    /// </summary>
    public class StoredBeeFileInfo
    {
        public string DiscKey;
        public string RemoteUrl;
        public string LocalPath;
        public string MetaPath;
        public string UniqueVersion;
        public string DownloadedPlatform;
        public long FileSizeBytes;
        public DateTime LastWriteTimeUtc;
        public bool IsLoadedInMemory;
    }

    /// <summary>
    /// BEEData folder 内の全 file の合計 size を bytes で返す。
    /// </summary>
    public static long GetTotalCacheSizeBytes()
    {
        string folderPath = GetCacheFolderPath();
        if (!Directory.Exists(folderPath))
            return 0;

        long total = 0;
        foreach (string file in Directory.GetFiles(folderPath))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception)
            {
                // enumeration と access の間に file が削除された可能性がある
            }
        }
        return total;
    }

    /// <summary>
    /// 保存済み BEE file と metadata の list を返す。
    /// </summary>
    public static List<StoredBeeFileInfo> GetAllStoredFiles()
    {
        var result = new List<StoredBeeFileInfo>();
        string folderPath = GetCacheFolderPath();

        if (!Directory.Exists(folderPath))
            return result;

        foreach (var kvp in BasisLoadHandler.OnDiscData)
        {
            string discKey = kvp.Key;
            string remoteUrl = kvp.Key;
            BasisBEEExtensionMeta meta = kvp.Value;
            remoteUrl = meta.StoredRemote.RemoteBeeFileLocation;

            string beePath = meta.StoredLocal.DownloadedBeeFileLocation;
            if (string.IsNullOrEmpty(beePath))
            {
                beePath = BasisIOManagement.GetBeeCacheFilePath(meta.UniqueVersion, meta.DownloadedPlatform);
            }

            string metaFilePath = BasisIOManagement.GetMetaCacheFilePath(meta.UniqueVersion, meta.DownloadedPlatform);

            long fileSize = 0;
            DateTime lastWrite = DateTime.MinValue;

            if (File.Exists(beePath))
            {
                try
                {
                    var fi = new FileInfo(beePath);
                    fileSize = fi.Length;
                    lastWrite = fi.LastWriteTimeUtc;
                }
                catch (Exception)
                {
                    // access error は無視する
                }
            }

            bool isLoaded = BasisLoadHandler.IsUrlLoadedInMemory(remoteUrl);

            result.Add(new StoredBeeFileInfo
            {
                DiscKey = discKey,
                RemoteUrl = remoteUrl,
                LocalPath = beePath,
                MetaPath = metaFilePath,
                UniqueVersion = meta.UniqueVersion,
                DownloadedPlatform = meta.DownloadedPlatform,
                FileSizeBytes = fileSize,
                LastWriteTimeUtc = lastWrite,
                IsLoadedInMemory = isLoaded,
            });
        }

        // last write time で sort する (古い順)
        result.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        return result;
    }

    /// <summary>
    /// remote URL key で保存済み BEE file 1 件を削除する。
    /// .BEE file、.BME meta file、すべての in-memory reference を片付ける。
    /// entry が見つかって clean up できた場合 true を返す。
    /// </summary>
    public static bool DeleteStoredFile(string remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl))
            return false;

        bool removedAny = false;
        foreach (var kvp in BasisLoadHandler.OnDiscData.ToList())
        {
            if (!string.Equals(kvp.Value.StoredRemote.RemoteBeeFileLocation, remoteUrl, StringComparison.Ordinal))
            {
                continue;
            }

            if (DeleteStoredEntry(kvp.Key, kvp.Value))
            {
                removedAny = true;
            }
        }

        if (!removedAny)
        {
            BasisDebug.LogWarning($"No OnDiscData entry found for URL: {remoteUrl}", BasisDebug.LogTag.Event);
            return false;
        }

        // loaded なら memory から unload する
        BasisLoadHandler.UnloadAllForUrl(remoteUrl);

        return true;
    }

    /// <summary>
    /// 保存済み BEE file をすべて削除し、cache 全体を clear する。
    /// </summary>
    public static void ClearAllCache()
    {
        // concurrent modification を避けるため、先に全 key を取得する
        var entries = BasisLoadHandler.OnDiscData.ToList();

        foreach (var entry in entries)
        {
            DeleteStoredEntry(entry.Key, entry.Value);
        }

        foreach (var loadedEntry in BasisLoadHandler.LoadedBundles.ToList())
        {
            if (BasisLoadHandler.LoadedBundles.TryRemove(loadedEntry.Key, out BasisTrackedBundleWrapper wrapper) &&
                wrapper?.AssetBundle != null)
            {
                try
                {
                    wrapper.AssetBundle.Unload(true);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Error unloading AssetBundle during cache clear: {ex.Message}");
                }
            }
        }

        // OnDiscData で tracking されていない orphan file も片付ける
        string folderPath = GetCacheFolderPath();
        if (Directory.Exists(folderPath))
        {
            foreach (string file in Directory.GetFiles(folderPath))
            {
                TryDeleteFile(file);
            }
        }

        BasisDebug.Log("All BEE cache cleared.", BasisDebug.LogTag.Event);
    }

    /// <summary>
    /// 現在 memory に loaded されていない最古 (LRU) file を evict し、
    /// total size が MaxCacheSizeBytes 未満になるまで cache size limit を適用する。
    /// 新しい file を download したあとに呼ぶ。
    /// </summary>
    public static void EnforceCacheSizeLimit()
    {
        long currentSize = GetTotalCacheSizeBytes();
        if (currentSize <= MaxCacheSizeBytes)
            return;

        BasisDebug.Log($"Cache size {FormatBytes(currentSize)} exceeds limit {FormatBytes(MaxCacheSizeBytes)}. Evicting oldest files...", BasisDebug.LogTag.Event);

        // 全 file を古い順に取得し、現在 loaded のものは skip する
        var allFiles = GetAllStoredFiles();
        var evictable = allFiles.Where(f => !f.IsLoadedInMemory && f.FileSizeBytes > 0).ToList();

        foreach (var file in evictable)
        {
            if (currentSize <= MaxCacheSizeBytes)
                break;

            long freed = file.FileSizeBytes;
            if (BasisLoadHandler.OnDiscData.TryGetValue(file.DiscKey, out BasisBEEExtensionMeta meta) &&
                DeleteStoredEntry(file.DiscKey, meta))
            {
                currentSize -= freed;
                BasisDebug.Log($"Evicted: {file.UniqueVersion} ({FormatBytes(freed)}). Remaining: {FormatBytes(currentSize)}", BasisDebug.LogTag.Event);
            }
        }

        if (currentSize > MaxCacheSizeBytes)
        {
            BasisDebug.LogWarning($"Cache still exceeds limit after eviction ({FormatBytes(currentSize)} / {FormatBytes(MaxCacheSizeBytes)}). Some files may be in use.", BasisDebug.LogTag.Event);
        }
    }

    /// <summary>
    /// byte count を human-readable string (B, KB, MB, GB) に format する。
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private static string GetCacheFolderPath()
    {
        return BasisIOManagement.GenerateFolderPath(BasisBeeConstants.AssetBundlesFolder);
    }

    private static bool DeleteStoredEntry(string discKey, BasisBEEExtensionMeta meta)
    {
        if (!BasisLoadHandler.OnDiscData.TryRemove(discKey, out _))
        {
            return false;
        }

        string beePath = meta.StoredLocal.DownloadedBeeFileLocation;
        if (string.IsNullOrEmpty(beePath))
        {
            beePath = BasisIOManagement.GetBeeCacheFilePath(meta.UniqueVersion, meta.DownloadedPlatform);
        }
        TryDeleteFile(beePath);

        string connectorPath = meta.StoredLocal.DownloadedConnectorFileLocation;
        if (string.IsNullOrEmpty(connectorPath))
        {
            connectorPath = BasisIOManagement.GetConnectorCacheFilePath(meta.UniqueVersion, meta.DownloadedPlatform);
        }
        TryDeleteFile(connectorPath);

        string metaPath = BasisIOManagement.GetMetaCacheFilePath(meta.UniqueVersion, meta.DownloadedPlatform);
        TryDeleteFile(metaPath);

        BasisDebug.Log($"Deleted stored BEE file: {meta.UniqueVersion} [{meta.DownloadedPlatform}] (source: {meta.StoredRemote.RemoteBeeFileLocation})", BasisDebug.LogTag.Event);
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Failed to delete file {path}: {ex.Message}");
        }
    }
}
