using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public static class BasisIOManagement
{
    public static string PersistentDataPath { get; private set; }
    public static RuntimePlatform CachedPlatform { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void CacheUnityMainThreadValues()
    {
        PersistentDataPath = Application.persistentDataPath;
        CachedPlatform = Application.platform;
    }

    public static string GetCurrentCachePlatform()
    {
        return NormalizeCachePlatformName(CachedPlatform.ToString());
    }

    public static bool CachePlatformMatchesCurrent(string downloadedPlatform)
    {
        return string.Equals(NormalizeCachePlatformName(downloadedPlatform), GetCurrentCachePlatform(), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeCachePlatformName(string platformName)
    {
        if (string.IsNullOrWhiteSpace(platformName))
        {
            return string.Empty;
        }

        string normalized = platformName.Trim();
        return normalized switch
        {
            nameof(RuntimePlatform.WindowsEditor) => "StandaloneWindows64",
            nameof(RuntimePlatform.WindowsPlayer) => "StandaloneWindows64",
            nameof(RuntimePlatform.WindowsServer) => "StandaloneWindows64",
            nameof(RuntimePlatform.LinuxEditor) => "StandaloneLinux64",
            nameof(RuntimePlatform.LinuxPlayer) => "StandaloneLinux64",
            nameof(RuntimePlatform.LinuxServer) => "StandaloneLinux64",
            nameof(RuntimePlatform.OSXEditor) => "StandaloneOSX",
            nameof(RuntimePlatform.OSXPlayer) => "StandaloneOSX",
            nameof(RuntimePlatform.Android) => "Android",
            nameof(RuntimePlatform.IPhonePlayer) => "iOS",
            _ => normalized,
        };
    }

    public static string GetBeeCacheFilePath(string uniqueVersion, string downloadedPlatform = null)
    {
        return GenerateFilePath(BuildPlatformAwareCacheFileName(uniqueVersion, BasisBeeConstants.BasisEncryptedExtension, downloadedPlatform), BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetConnectorCacheFilePath(string uniqueVersion, string downloadedPlatform = null)
    {
        return GenerateFilePath(BuildPlatformAwareCacheFileName(uniqueVersion, BasisBeeConstants.BasisConnectorExtension, downloadedPlatform), BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetMetaCacheFilePath(string uniqueVersion, string downloadedPlatform = null)
    {
        return GenerateFilePath(BuildPlatformAwareCacheFileName(uniqueVersion, BasisBeeConstants.BasisMetaExtension, downloadedPlatform), BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetLegacyBeeCacheFilePath(string uniqueVersion)
    {
        return GenerateFilePath($"{uniqueVersion}{BasisBeeConstants.BasisEncryptedExtension}", BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetLegacyMetaCacheFilePath(string uniqueVersion)
    {
        return GenerateFilePath($"{uniqueVersion}{BasisBeeConstants.BasisMetaExtension}", BasisBeeConstants.AssetBundlesFolder);
    }

    private static string BuildPlatformAwareCacheFileName(string uniqueVersion, string extension, string downloadedPlatform)
    {
        if (string.IsNullOrWhiteSpace(uniqueVersion))
            throw new ArgumentException("Unique version is null or empty.", nameof(uniqueVersion));

        string normalizedPlatform = string.IsNullOrWhiteSpace(downloadedPlatform)
            ? GetCurrentCachePlatform()
            : NormalizeCachePlatformName(downloadedPlatform);

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            normalizedPlatform = normalizedPlatform.Replace(invalidChar, '_');
            uniqueVersion = uniqueVersion.Replace(invalidChar, '_');
        }

        return $"{uniqueVersion}.{normalizedPlatform}{extension}";
    }

    public sealed class BeeDownloadResult
    {
        public BasisBundleConnector Connector { get; }
        public string LocalPath { get; }
        public byte[] SectionData { get; }

        public BeeDownloadResult(BasisBundleConnector connector, string localPath, byte[] sectionData)
        {
            Connector = connector ?? throw new ArgumentNullException(nameof(connector));
            LocalPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
            SectionData = sectionData ?? throw new ArgumentNullException(nameof(sectionData));
        }
    }

    public sealed class BeeReadResult
    {
        public BasisBundleConnector Connector { get; }
        public byte[] SectionData { get; }

        public BeeReadResult(BasisBundleConnector connector, byte[] sectionData)
        {
            Connector = connector;
            SectionData = sectionData;
        }
    }

    private sealed class DownloadPayload
    {
        public byte[] Data; // present when downloaded to memory
        public string Path; // present when downloaded to file
    }

    /// <summary>
    /// remote BEE blob (8-byte Int64 header 付き) を download し、connector を decrypt/parse し、
    /// platform に一致する section を download し、local .bee file (4-byte Int32 header) を書き込み、
    /// すべての artifact を返す。
    /// </summary>
    public static async Task<BeeResult<BeeDownloadResult>> DownloadBEEEx(string url, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        // actionable な message で input を validate する
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: {urlErr}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: VP is null or empty.");

        // 1) 8-byte remote header (Int64) を読む
        var headerRes = await DownloadRangeInternal(url, startByte: 0, endByteInclusive: BasisBeeConstants.RemoteHeaderSize - 1, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!headerRes.IsSuccess || headerRes.Value?.Data == null)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to read remote header. {headerRes.Error ?? "No data"}", headerRes.ResponseCode);

        if (headerRes.Value.Data.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Remote header size mismatch. Expected {BasisBeeConstants.RemoteHeaderSize} bytes, got {headerRes.Value.Data.Length}.", headerRes.ResponseCode);

        long connectorLength = ReadInt64LittleEndian(headerRes.Value.Data);
        if (connectorLength <= 0)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Invalid connector length {connectorLength}. Remote file may be corrupt or not a BEE.");

        if (connectorLength > BasisBeeConstants.MaxConnectorBytes)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Connector length {connectorLength} exceeds max allowed {BasisBeeConstants.MaxConnectorBytes}.");

        // 2) connector bytes を download する (header の直後)
        long connectorStart = BasisBeeConstants.RemoteHeaderSize;
        long connectorEndInclusive = BasisBeeConstants.RemoteHeaderSize + connectorLength - 1;

        var connectorRes = await DownloadRangeInternal(url, connectorStart, connectorEndInclusive, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!connectorRes.IsSuccess || connectorRes.Value.Data == null)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to download connector block. {connectorRes.Error ?? "No data"}", connectorRes.ResponseCode);

        if (connectorRes.Value.Data.LongLength != connectorLength)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Expected {connectorLength} connector bytes, got {connectorRes.Value.Data.LongLength}.", connectorRes.ResponseCode);

        var connectorBytes = connectorRes.Value.Data;
        BasisDebug.Log("Downloaded Connector block size: " + connectorBytes.Length);

        // 3) connector を parse する
        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback);
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);

        if (connector == null)
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Failed to parse connector metadata (null).");

        if (connector.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Connector contains no sections.");

        // 4) section を走査して range を計算し、platform に一致する section だけ download する
        long previousEnd = connectorEndInclusive; // End of connector region in the remote file
        byte[] platformSectionData = null;

        for (int index = 0; index < connector.BasisBundleGenerated.Length; index++)
        {
            var entry = connector.BasisBundleGenerated[index];
            if (entry == null)
            {
                BasisDebug.LogError($"DownloadBEEEx: Null section entry at index {index}.");
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Null section entry at index {index}.");
            }

            long start = previousEnd + 1;

            long sectionLength = entry.EndByte;
            if (sectionLength <= 0)
            {
                BasisDebug.LogError($"DownloadBEEEx: Invalid section length at index {index}: {sectionLength}.");
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Invalid section length at index {index}: {sectionLength}.");
            }

            if (sectionLength > BasisBeeConstants.MaxSectionBytes)
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Section length {sectionLength} at index {index} exceeds max allowed {BasisBeeConstants.MaxSectionBytes}.");

            long end = start + sectionLength - 1;

            bool isPlatform = false;
            try
            {
                isPlatform = BasisBundleConnector.IsPlatform(entry);
            }
            catch (Exception ex)
            {
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Exception while checking platform for section {index}: {ex.Message}");
            }

            if (isPlatform)
            {
                BasisDebug.Log($"Downloading platform section range {start}-{end}");
                var sectRes = await DownloadRangeInternal(url, start, end, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

                if (!sectRes.IsSuccess || sectRes.Value?.Data == null)
                    return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to download platform section at index {index}. {sectRes.Error ?? "No data"}", sectRes.ResponseCode);

                if (sectRes.Value.Data.LongLength != sectionLength)
                    return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Expected section length {sectionLength}, got {sectRes.Value.Data.LongLength}.", sectRes.ResponseCode);

                platformSectionData = sectRes.Value.Data;
                BasisDebug.Log("Platform section length: " + platformSectionData.LongLength);
                // break しない。複数 match の有無に関係なく previousEnd が正しく進むよう最後まで走査する
            }

            previousEnd = end;
        }

        if (platformSectionData == null || platformSectionData.Length == 0)
        {
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: No platform-matching section found in connector. Platform Request was {Application.platform}. {BasisBundleConnector.DebugOfPlatforms(connector)}");
        }

        // 5) local .bee を書く (Int32 header + connector + section)
        string fileName = Path.GetFileName(GetBeeCacheFilePath(connector.UniqueVersion));
        if (string.IsNullOrWhiteSpace(fileName))
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Connector has no UniqueVersion / file extension.");

        string localPath;
        try
        {
            localPath = GetBeeCacheFilePath(connector.UniqueVersion);
        }
        catch (Exception ex)
        {
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to generate local file path: {ex.Message}");
        }

        var writeRes = await WriteBeeFileAsync(localPath, connectorBytes, platformSectionData, false);
        if (!writeRes.IsSuccess)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: {writeRes.Error}");

        return BeeResult<BeeDownloadResult>.Ok(new BeeDownloadResult(connector, localPath, platformSectionData));
    }
    /// <summary>
    /// remote BEE (8-byte Int64 header) から connector bytes だけを download して parse する。
    /// </summary>
    public static async Task<BeeResult<(BasisBundleConnector, string)>> DownloadConnectorOnlyEx(string url, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: {urlErr}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<(BasisBundleConnector, string)>.Fail("DownloadConnectorOnlyEx: VP is null or empty.");

        // header
        var headerRes = await DownloadRangeInternal(url, 0, BasisBeeConstants.RemoteHeaderSize - 1, null, progressCallback, cancellationToken, MaxDownloadSizeInMB);
        if (!headerRes.IsSuccess || headerRes.Value?.Data == null)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Failed to read header. {headerRes.Error ?? "No data"}", headerRes.ResponseCode);

        if (headerRes.Value.Data.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Header size mismatch. Expected {BasisBeeConstants.RemoteHeaderSize}, got {headerRes.Value.Data.Length}.", headerRes.ResponseCode);

        long connectorLength = ReadInt64LittleEndian(headerRes.Value.Data);
        if (connectorLength <= 0)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Invalid connector length {connectorLength}.");

        if (connectorLength > BasisBeeConstants.MaxConnectorBytes)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Connector length {connectorLength} exceeds max allowed {BasisBeeConstants.MaxConnectorBytes}.");

        // connector bytes
        long start = BasisBeeConstants.RemoteHeaderSize;
        long end = BasisBeeConstants.RemoteHeaderSize + connectorLength - 1;

        var connectorRes = await DownloadRangeInternal(url, start, end, null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (connectorRes.IsSuccess == false && connectorRes.Error != string.Empty)
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail(connectorRes.Error, connectorRes.ResponseCode);
        }

        if (!connectorRes.IsSuccess || connectorRes.Value?.Data == null)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Failed to read connector bytes. {connectorRes.Error ?? "No data"}", connectorRes.ResponseCode);

        if (connectorRes.Value.Data.LongLength != connectorLength)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Expected {connectorLength} bytes, got {connectorRes.Value.Data.LongLength}.", connectorRes.ResponseCode);

        var connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorRes.Value.Data, progressCallback);
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);
        if (connector == null)
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail("DownloadConnectorOnlyEx: Failed to parse connector metadata (null).");
        }

        // 5) local .bec を書く (Int32 header + connector のみ、section なし)
        string fileName = Path.GetFileName(GetConnectorCacheFilePath(connector.UniqueVersion));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail("DownloadConnectorOnlyEx: Connector has no UniqueVersion / file extension.");

        }
        string localPath = GetConnectorCacheFilePath(connector.UniqueVersion);
        var connectorBytes = connectorRes.Value.Data;

        var writeRes = await WriteBeeFileAsync(localPath, connectorBytes, null, true);

        if (!writeRes.IsSuccess)
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadBEEEx: {writeRes.Error}");
        }
        (BasisBundleConnector, string) Data = new(connector, localPath);

        return BeeResult<(BasisBundleConnector, string)>.Ok(Data);
    }

    /// <summary>
    /// local .bee file (4-byte Int32 header) を読み、connector を再生成し、残りの section data を返す。
    /// </summary>
    public static async Task<BeeResult<BeeReadResult>> ReadBEEFileEx(string filePath, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: File path is null or empty.");
        }

        if (!File.Exists(filePath))
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File not found: {filePath}");
        }

        if (string.IsNullOrWhiteSpace(vp))
        {
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: VP is null or empty.");
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 96 * 1024, useAsync: true);

        if (fs.Length < BasisBeeConstants.DiskHeaderSize)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File too small to contain header. Size={fs.Length} bytes.");
        }

        // Int32 connector size を読む (little-endian)
        byte[] sizeBytes = await ReadExactAsync(fs, BasisBeeConstants.DiskHeaderSize, cancellationToken).ConfigureAwait(false);
        if (sizeBytes.Length != BasisBeeConstants.DiskHeaderSize)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read connector size (header). Got {sizeBytes.Length} bytes.");
        }

        int connectorSize = ReadInt32LittleEndian(sizeBytes);
        long remainingPossible = fs.Length - fs.Position;
        if (connectorSize <= 0 || connectorSize > remainingPossible)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Invalid connector size {connectorSize}. Remaining file bytes: {remainingPossible}. File may be corrupt.");
        }

        // connector bytes を読む
        byte[] connectorBytes = await ReadExactAsync(fs, connectorSize, cancellationToken).ConfigureAwait(false);
        if (connectorBytes.Length != connectorSize)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full connector block. Expected {connectorSize}, got {connectorBytes.Length}.");
        }

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback).ConfigureAwait(false);
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);

        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: Failed to regenerate connector metadata (null).");

        // 残りは section data
        long remaining = fs.Length - fs.Position;
        if (remaining < 0) remaining = 0;

        byte[] sectionData;
        if (remaining == 0)
        {
            sectionData = Array.Empty<byte>();
        }
        else
        {
            sectionData = await ReadExactAsync(fs, checked((int)remaining), cancellationToken).ConfigureAwait(false);
            if (sectionData == null || sectionData.LongLength != remaining)
            {
                return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full section data. Expected {remaining}, got {sectionData?.LongLength ?? 0}.");
            }
        }

        return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, sectionData));
    }
    /// <summary>
    /// local .bee file (4-byte Int32 header) を読み、connector を再生成し、残りの section data を返す。
    /// </summary>
    public static async Task<BeeResult<BeeReadResult>> ReadBEEConnectorFileEx(string filePath, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: File path is null or empty.");

        if (!File.Exists(filePath))
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File not found: {filePath}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: VP is null or empty.");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 96 * 1024, useAsync: true);

        if (fs.Length < BasisBeeConstants.DiskHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File too small to contain header. Size={fs.Length} bytes.");

        // Int32 connector size を読む (little-endian)
        byte[] sizeBytes = await ReadExactAsync(fs, BasisBeeConstants.DiskHeaderSize, cancellationToken).ConfigureAwait(false);
        if (sizeBytes.Length != BasisBeeConstants.DiskHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read connector size (header). Got {sizeBytes.Length} bytes.");

        int connectorSize = ReadInt32LittleEndian(sizeBytes);
        long remainingPossible = fs.Length - fs.Position;
        if (connectorSize <= 0 || connectorSize > remainingPossible)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Invalid connector size {connectorSize}. Remaining file bytes: {remainingPossible}. File may be corrupt.");

        // connector bytes を読む
        byte[] connectorBytes = await ReadExactAsync(fs, connectorSize, cancellationToken).ConfigureAwait(false);
        if (connectorBytes.Length != connectorSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full connector block. Expected {connectorSize}, got {connectorBytes.Length}.");

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback).ConfigureAwait(false);
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);

        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: Failed to regenerate connector metadata (null).");

        return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, null));
    }

    /// <summary>
    /// local file から REMOTE-format BEE blob (8-byte Int64 header) を読み、connector を parse し、
    /// <paramref name="includeSection"/> が true の場合は platform に一致する section を返す。
    /// これは SDK が HTTP host ではなく disk へ直接 export した BEE に対する、
    /// <see cref="DownloadBEEEx"/> の on-disk 相当。
    /// </summary>
    public static async Task<BeeResult<BeeReadResult>> ReadRemoteBeeFromDiskEx(string filePath, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, bool includeSection = true)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: File path is null or empty.");

        if (!File.Exists(filePath))
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: File not found: {filePath}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: VP is null or empty.");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 96 * 1024, useAsync: true);

        if (fs.Length < BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: File too small to contain remote header. Size={fs.Length} bytes.");

        byte[] headerBytes = await ReadExactAsync(fs, BasisBeeConstants.RemoteHeaderSize, cancellationToken).ConfigureAwait(false);
        if (headerBytes.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Failed to read remote header. Got {headerBytes.Length} bytes.");

        long connectorLength = ReadInt64LittleEndian(headerBytes);
        if (connectorLength <= 0)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Invalid connector length {connectorLength}. File may not be a remote-format BEE.");

        if (connectorLength > BasisBeeConstants.MaxConnectorBytes)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Connector length {connectorLength} exceeds max allowed {BasisBeeConstants.MaxConnectorBytes}.");

        long remainingAfterHeader = fs.Length - fs.Position;
        if (connectorLength > remainingAfterHeader)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Connector length {connectorLength} exceeds file remainder {remainingAfterHeader}.");

        byte[] connectorBytes = await ReadExactAsync(fs, checked((int)connectorLength), cancellationToken).ConfigureAwait(false);
        if (connectorBytes.LongLength != connectorLength)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Failed to read full connector block. Expected {connectorLength}, got {connectorBytes.Length}.");

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback).ConfigureAwait(false);
        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: Failed to parse connector metadata (null).");

        if (!includeSection)
            return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, null));

        if (connector.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: Connector contains no sections.");

        long cursor = fs.Position;
        long matchOffset = -1;
        long matchLength = 0;

        for (int index = 0; index < connector.BasisBundleGenerated.Length; index++)
        {
            BasisBundleGenerated entry = connector.BasisBundleGenerated[index];
            if (entry == null)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Null section entry at index {index}.");

            long sectionLength = entry.EndByte;
            if (sectionLength <= 0)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Invalid section length at index {index}: {sectionLength}.");

            if (sectionLength > BasisBeeConstants.MaxSectionBytes)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Section length {sectionLength} at index {index} exceeds max allowed {BasisBeeConstants.MaxSectionBytes}.");

            long sectionEndExclusive = cursor + sectionLength;
            if (sectionEndExclusive > fs.Length)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Section at index {index} runs past end of file.");

            bool isPlatform;
            try
            {
                isPlatform = BasisBundleConnector.IsPlatform(entry);
            }
            catch (Exception ex)
            {
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Exception while checking platform for section {index}: {ex.Message}");
            }

            if (isPlatform)
            {
                matchOffset = cursor;
                matchLength = sectionLength;
            }

            cursor = sectionEndExclusive;
        }

        if (matchOffset < 0)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: No platform-matching section found. Platform Request was {Application.platform}. {BasisBundleConnector.DebugOfPlatforms(connector)}");

        fs.Seek(matchOffset, SeekOrigin.Begin);
        byte[] platformSectionData = await ReadExactAsync(fs, checked((int)matchLength), cancellationToken).ConfigureAwait(false);
        if (platformSectionData.LongLength != matchLength)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Expected section length {matchLength}, got {platformSectionData.Length}.");

        return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, platformSectionData));
    }

    public static string GenerateFilePath(string fileName, string subFolder)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("GenerateFilePath: fileName is null or empty.", nameof(fileName));

        string folderPath = GenerateFolderPath(subFolder);
        string localPath = Path.Combine(folderPath, fileName);

        string fullFolder = Path.GetFullPath(folderPath);
        if (fullFolder.Length == 0 || fullFolder[fullFolder.Length - 1] != Path.DirectorySeparatorChar)
            fullFolder += Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(localPath).StartsWith(fullFolder, StringComparison.Ordinal))
            throw new ArgumentException($"GenerateFilePath: resolved path escapes cache folder: {fileName}", nameof(fileName));

        BasisDebug.Log($"Generated folder path: {localPath}");
        return localPath;
    }

    public static string GenerateFolderPath(string subFolder)
    {
        if (string.IsNullOrWhiteSpace(subFolder))
            throw new ArgumentException("GenerateFolderPath: subFolder is null or empty.", nameof(subFolder));

        string basePath = PersistentDataPath;
        if (string.IsNullOrWhiteSpace(basePath))
            throw new InvalidOperationException("GenerateFolderPath: PersistentDataPath was not initialized on the main thread.");

        string folderPath = Path.Combine(basePath, subFolder);
        if (!Directory.Exists(folderPath))
        {
            BasisDebug.Log($"Directory {folderPath} does not exist. Creating directory.");
            Directory.CreateDirectory(folderPath);
        }
        return folderPath;
    }
    /// <summary>
    /// byte range を download する。
    /// </summary>
    /// <param name="url"></param>
    /// <param name="startByte"></param>
    /// <param name="endByteInclusive"></param>
    /// <param name="toFilePath"></param>
    /// <param name="progress"></param>
    /// <param name="ct"></param>
    /// <param name="MaxDownloadSizeInMB">default は 4GB。</param>
    /// <returns></returns>
    private static async Task<BeeResult<DownloadPayload>> DownloadRangeInternal(string url, long startByte, long? endByteInclusive, string toFilePath, BasisProgressReport progress, CancellationToken ct, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<DownloadPayload>.Fail(urlErr);

        if (startByte < 0)
            return BeeResult<DownloadPayload>.Fail($"Invalid start byte: {startByte}");

        if (endByteInclusive.HasValue && endByteInclusive.Value < startByte)
            return BeeResult<DownloadPayload>.Fail($"Invalid byte range: {startByte}-{endByteInclusive.Value}");


        long expectedBytes = endByteInclusive.HasValue? (endByteInclusive.Value - startByte + 1): long.MaxValue; // open-ended range

        if (!endByteInclusive.HasValue)
            return BeeResult<DownloadPayload>.Fail("Open-ended ranges are not allowed when a max size is enforced.");

        if (expectedBytes <= 0)
            return BeeResult<DownloadPayload>.Fail($"Invalid expected byte count: {expectedBytes}");


        if (expectedBytes > MaxDownloadSizeInMB)
            return BeeResult<DownloadPayload>.Fail($"Refusing download: requested {expectedBytes} bytes exceeds limit {MaxDownloadSizeInMB}.");

        string requestId = BasisGenerateUniqueID.GenerateUniqueID();

        using var req = UnityWebRequest.Get(url);

        string rangeHeader = endByteInclusive.HasValue ? $"bytes={startByte}-{endByteInclusive.Value}" : $"bytes={startByte}-";

        req.SetRequestHeader("Range", rangeHeader);

        if (string.IsNullOrEmpty(toFilePath) == false)
        {
            // caller が path を渡した場合は parent directory が存在するようにする
            string dir = Path.GetDirectoryName(toFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            req.downloadHandler = new DownloadHandlerFile(toFilePath, true) { removeFileOnAbort = true };
        }
        else
        {
            req.downloadHandler = new DownloadHandlerBuffer();
        }

        var op = req.SendWebRequest();

        float lastProgress = 0f;
        const float threshold = 0.5f; // percentage points

        while (!op.isDone)
        {
            if (ct.IsCancellationRequested)
            {
                BasisDebug.Log("Download cancelled.");
                req.Abort();
                return BeeResult<DownloadPayload>.Fail("Cancelled");
            }

            float p = req.downloadProgress * 100f;
            if (progress != null && MathF.Abs(p - lastProgress) >= threshold)
            {
                progress.ReportProgress(requestId, p, "Downloading data...");
                lastProgress = p;
            }

            await Task.Yield();
        }

        long code = req.responseCode;

        // 先に network error を normalize する
        if (req.result != UnityWebRequest.Result.Success)
        {
            progress?.ReportProgress(requestId, 100, "Downloading Complete");
            var errDetail = BuildNetworkErrorDetail(req);
            return BeeResult<DownloadPayload>.Fail($"Network error: {req.error}. {errDetail}", code);
        }

        // partial content semantics を強制し、actionable な reason を提供する
        switch (code)
        {
            case 206:
                // Content-Range があれば validate し、server が request に従ったことを確認する
                string contentRange = req.GetResponseHeader("Content-Range") ?? string.Empty;
                if (!string.IsNullOrEmpty(contentRange))
                {
                    // 基本的な sanity check。dependency を軽く保つため完全 parse は避ける
                    if (!contentRange.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
                    {
                        progress?.ReportProgress(requestId, 100, $"Error! {code}");
                        return BeeResult<DownloadPayload>.Fail($"Unexpected Content-Range header: {contentRange}", code);
                    }
                }
                break;

            case 200:
                progress?.ReportProgress(requestId, 100, $"Error! {code}");
                return BeeResult<DownloadPayload>.Fail("Server returned 200 (full file). Host must support HTTP range requests (206).", code);

            case 416:
                progress?.ReportProgress(requestId, 100, $"Error! {code}");
                return BeeResult<DownloadPayload>.Fail($"Requested Range {startByte}-{(endByteInclusive?.ToString() ?? "end")} not satisfiable. The requested range may exceed the file size.", code);

            default:
                progress?.ReportProgress(requestId, 100, $"Error! {code}");
                var details = BuildNetworkErrorDetail(req);
                return BeeResult<DownloadPayload>.Fail($"Unexpected response code: {code}. {details}", code);
        }

        var payload = new DownloadPayload();
        if (toFilePath == null)
        {
            var data = req.downloadHandler.data;
            if (data == null)
                return BeeResult<DownloadPayload>.Fail("No payload returned (buffer was null).", code);

            // 任意: Content-Length があれば verify する
            var contentLengthHeader = req.GetResponseHeader("Content-Length");
            if (long.TryParse(contentLengthHeader, out var contentLen) && contentLen >= 0 && data.LongLength != contentLen)
            {
                return BeeResult<DownloadPayload>.Fail($"Content-Length mismatch. Header={contentLen}, Received={data.LongLength}.", code);
            }

            payload.Data = data;
        }
        else
        {
            if (!File.Exists(toFilePath))
                return BeeResult<DownloadPayload>.Fail($"Download handler reported success but file was not created: {toFilePath}", code);

            payload.Path = toFilePath;
        }

        return BeeResult<DownloadPayload>.Ok(payload);
    }

    /// <summary>
    /// 4-byte little-endian Int32 header (connector size) + connector [+ optional section] の local .bee を書く。
    /// <paramref name="IgnoreSectionBytes"/> が true の場合、section が渡されても書き込まない。
    /// </summary>
    private static async Task<BeeResult<bool>> WriteBeeFileAsync(string path, byte[] connectorBytes, byte[] sectionBytes, bool IgnoreSectionBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BeeResult<bool>.Fail("WriteBeeFileAsync: Output path is null or empty.");

        if (connectorBytes == null || connectorBytes.Length == 0)
            return BeeResult<bool>.Fail("WriteBeeFileAsync: Connector bytes are empty.");

        // section を無視しない場合、non-null である必要がある (zero-length は許可)
        if (!IgnoreSectionBytes && sectionBytes == null)
            return BeeResult<bool>.Fail("WriteBeeFileAsync: Section bytes are null.");

        // directory を準備する
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // header: connector size の little-endian Int32
        byte[] sizeLE = GetBytesInt32LE(connectorBytes.Length);

        // 実際に section を書くか決める
        bool writeSection = !IgnoreSectionBytes && (sectionBytes?.Length ?? 0) > 0;

        // 書き込む予定の total size を計算する
        long totalSize = sizeLE.Length + connectorBytes.Length + (writeSection ? sectionBytes.Length : 0);

        // buffer を auto-tune する: min 32KB, max 1MB
        int buffer = Clamp((int)(totalSize / 8), 32 * 1024, 1 * 1024 * 1024);

        // 複数の concurrent download が同じ .BEE path を target にする場合の sharing violation を避けるため、
        // temp file に書いてから atomic rename する。
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer, useAsync: true))
            {
                await fs.WriteAsync(sizeLE, 0, sizeLE.Length).ConfigureAwait(false);
                await fs.WriteAsync(connectorBytes, 0, connectorBytes.Length).ConfigureAwait(false);

                if (writeSection)
                {
                    await fs.WriteAsync(sectionBytes, 0, sectionBytes.Length).ConfigureAwait(false);
                }
            }

            long actual = new FileInfo(tempPath).Length;
            BasisDebug.Log($"Expected File Size: {totalSize} bytes");
            BasisDebug.Log($"Actual File Size on Disk: {actual} bytes");

            if (totalSize != actual)
            {
                BasisDebug.LogError("File size does not match expected size!");
                try { File.Delete(tempPath); } catch { }
                return BeeResult<bool>.Fail($"WriteBeeFileAsync: Size mismatch after write. Expected {totalSize}, actual {actual}.");
            }

            // destination が既に存在する場合は置き換える。Windows では destination があると通常の File.Move が throw する。
            // 先に削除すれば、re-download が古い/corrupt な cached file を古い bytes のまま残さず overwrite できる。
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return BeeResult<bool>.Fail($"WriteBeeFileAsync: {ex.GetType().Name}: {ex.Message}");
        }

        return BeeResult<bool>.Ok(true);
    }

    private static bool ValidateUrl(string url, out string normalizedUrl, out string error)
    {
        normalizedUrl = url;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "The provided URL is null or empty.";
            BasisDebug.LogError(error);
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = $"The provided URL is not a valid absolute URI: '{url}'.";
            BasisDebug.LogError(error);
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported URL scheme '{uri.Scheme}'. Only HTTP/HTTPS are supported.";
            BasisDebug.LogError(error);
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// <paramref name="location"/> が HTTP/HTTPS download ではなく local BEE location
    /// (<c>file://</c> URI) の場合、file の現在の存在有無に関係なく true を返す。
    /// 「この content は local であり networked になり得ない」という invariant に使う。
    /// 実際に disk から file を読む必要がある場合は <see cref="TryResolveLocalBeePath"/> を使う。
    /// </summary>
    public static bool IsLocalBeeUrl(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;

        if (Uri.TryCreate(location, UriKind.Absolute, out Uri uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return false;
            return uri.IsFile;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="location"/> が HTTP/HTTPS download ではなく既存の local BEE file
    /// (<c>file://</c> URI または raw filesystem path) を指している場合に true を返し、
    /// absolute local path へ解決する。drop-in local BEE を network download path ではなく
    /// on-disk reader へ通すために使う。
    /// </summary>
    public static bool TryResolveLocalBeePath(string location, out string localPath)
    {
        localPath = null;
        if (string.IsNullOrWhiteSpace(location))
            return false;

        if (Uri.TryCreate(location, UriKind.Absolute, out Uri uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return false;

            if (uri.IsFile)
            {
                try { localPath = uri.LocalPath; }
                catch { return false; }
                return !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
            }

            return false;
        }

        try
        {
            if (File.Exists(location))
            {
                localPath = location;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int size, CancellationToken ct)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));

        byte[] buf = new byte[size];
        int read = 0;

        while (read < size)
        {
            int n = await s.ReadAsync(buf, read, size - read, ct);
            if (n <= 0) break;
            read += n;
        }

        if (read == size)
            return buf;

        // ある分だけ返す (caller が length を確認する)
        if (read == 0)
            return Array.Empty<byte>();

        if (read < size)
        {
            var partial = new byte[read];
            Buffer.BlockCopy(buf, 0, partial, 0, read);
            return partial;
        }

        return buf;
    }

    private static byte[] GetBytesInt32LE(int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }

    private static int ReadInt32LittleEndian(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < 4) throw new ArgumentException("ReadInt32LittleEndian: buffer too small.", nameof(bytes));

        if (!BitConverter.IsLittleEndian)
        {
            var tmp = (byte[])bytes.Clone();
            Array.Reverse(tmp);
            return BitConverter.ToInt32(tmp, 0);
        }
        return BitConverter.ToInt32(bytes, 0);
    }

    private static long ReadInt64LittleEndian(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < 8) throw new ArgumentException("ReadInt64LittleEndian: buffer too small.", nameof(bytes));

        if (!BitConverter.IsLittleEndian)
        {
            var tmp = (byte[])bytes.Clone();
            Array.Reverse(tmp);
            return BitConverter.ToInt64(tmp, 0);
        }
        return BitConverter.ToInt64(bytes, 0);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (min > max) (min, max) = (max, min);
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// HTTP HEAD request を送り、remote file が reachable か確認する。
    /// server が 2xx status code を返した場合、file が存在し access 可能であるとして success を返す。
    /// </summary>
    public static async Task<BeeResult<bool>> CheckRemoteFileReachable(string url, CancellationToken cancellationToken = default)
    {
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<bool>.Fail($"Invalid URL: {urlErr}");

        using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbHEAD);
        req.downloadHandler = new DownloadHandlerBuffer();

        var op = req.SendWebRequest();

        while (!op.isDone)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                req.Abort();
                return BeeResult<bool>.Fail("Reachability check cancelled.");
            }
            await Task.Yield();
        }

        long code = req.responseCode;

        if (req.result == UnityWebRequest.Result.ConnectionError)
            return BeeResult<bool>.Fail($"Cannot connect to server: {req.error}", code);

        if (req.result == UnityWebRequest.Result.ProtocolError)
        {
            if (code == 404)
                return BeeResult<bool>.Fail("Avatar file not found on the server (404). The file may have been moved or deleted.", code);
            if (code == 403)
                return BeeResult<bool>.Fail("Access denied to avatar file (403). You may not have permission to access this file.", code);

            return BeeResult<bool>.Fail($"Server returned error {code}: {req.error}", code);
        }

        if (req.result != UnityWebRequest.Result.Success)
            return BeeResult<bool>.Fail($"Request failed: {req.error}", code);

        return BeeResult<bool>.Ok(true);
    }

    /// <summary>
    /// UnityWebRequest result から null を漏らさず、簡潔で actionable な detail string を作る。
    /// </summary>
    private static string BuildNetworkErrorDetail(UnityWebRequest req)
    {
        if (req != null)
        {
            string acceptRanges = req.GetResponseHeader("Accept-Ranges") ?? "n/a";
            string contentRange = req.GetResponseHeader("Content-Range") ?? "n/a";
            string contentLen = req.GetResponseHeader("Content-Length") ?? "n/a";

            return $"Accept-Ranges={acceptRanges}, Content-Range={contentRange}, Content-Length={contentLen}";
        }
        return "No response header details available.";
    }
}
