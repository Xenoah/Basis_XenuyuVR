using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
public static class BasisEncryptionToData
{
    public static async Task<AssetBundleCreateRequest> GenerateBundleFromFile(string Password, byte[] Bytes, uint CRC, BasisProgressReport progressCallback)
    {
        // decryption 用の password object を定義する
        var BasisPassword = new BasisEncryptionWrapper.BasisPassword
        {
            VP = Password
        };
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        // file を非同期に decrypt する
        var decrypted = await BasisEncryptionWrapper.DecryptFromBytesAsync(UniqueID, BasisPassword, Bytes, progressCallback);

        if (!decrypted.Success || decrypted.Data == null || decrypted.Data.Length == 0)
        {
            BasisDebug.LogError($"Decrypt failed: {decrypted.Error} | {decrypted.Message}");
            return null; // <-- critical
        }

        BasisDebug.Log("Attempting Asset Bundle Load...", BasisDebug.LogTag.Event);

        AssetBundleCreateRequest assetBundleCreateRequest;
        try
        {
            assetBundleCreateRequest = AssetBundle.LoadFromMemoryAsync(decrypted.Data, CRC);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"LoadFromMemoryAsync threw: {ex}");
            return null;
        }
        // 最後に report した progress を追跡する
        int lastReportedProgress = -1;

        // AssetBundleCreateRequest の progress を定期的に確認し、progress を report する
        while (!assetBundleCreateRequest.isDone)
        {
            // progress を percentage (0-100) に変換する
            int progress = Mathf.RoundToInt(assetBundleCreateRequest.progress * 100);

            // progress が変化した場合だけ report する
            if (progress > lastReportedProgress)
            {
                lastReportedProgress = progress;

                // 現在の progress で progress callback を呼ぶ
                progressCallback.ReportProgress(UniqueID.ToString(), progress, "loading bundle");
            }

            // busy waiting を避けるため、次の確認前に短く待つ
            await Task.Delay(50); // Adjust delay as needed (e.g., 50ms)
        }

        progressCallback?.ReportProgress(UniqueID, 100, "loading bundle");
        await assetBundleCreateRequest;

        // CRC が失敗した場合や bytes が bundle ではない場合、req.assetBundle は null のままになり得る。
        if (assetBundleCreateRequest.assetBundle == null)
        {
            BasisDebug.LogError("AssetBundle load finished but assetBundle is null (CRC mismatch or invalid bundle bytes).");
            return null;
        }

        return assetBundleCreateRequest;
    }
    public static async Task<BasisBundleConnector> GenerateMetaFromBytes(string password, byte[] encryptedBytes, BasisProgressReport progressCallback)
    {
        var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
        string uniqueID = BasisGenerateUniqueID.GenerateUniqueID();

        var decryptedMeta = await BasisEncryptionWrapper.DecryptFromBytesAsync(uniqueID, basisPassword, encryptedBytes, progressCallback).ConfigureAwait(false);


        if (decryptedMeta.Success)
        {
            BasisDebug.Log("Converting decrypted meta file to BasisBundleInformation...", BasisDebug.LogTag.Event);
            return ConvertBytesToJson(decryptedMeta.Data, out var connector) ? connector : null;
        }
        else
        {
            BasisDebug.LogError($"Failed to Decrypt, {decryptedMeta.Error} | {decryptedMeta.Message} | {decryptedMeta.Exception}");
            return null;
        }
    }

    public static bool ConvertBytesToJson(byte[] data, out BasisBundleConnector connector)
    {
        connector = null;

        if (data == null || data.Length == 0)
        {
            BasisDebug.LogError($"Data for {nameof(BasisBundleConnector)} is empty or null.", BasisDebug.LogTag.Event);
            return false;
        }

        BasisDebug.Log("Converting byte array to JSON string...", BasisDebug.LogTag.Event);
        try
        {
            connector = BasisSerialization.DeserializeValue<BasisBundleConnector>(data);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"DeserializeValue<BasisBundleConnector> threw {ex.GetType().Name}: {ex.Message}. PlaintextLength={data.Length}. Head={JsonHeadPreview(data)}");
            return false;
        }

        if (connector == null)
        {
            BasisDebug.LogError($"DeserializeValue<BasisBundleConnector> returned null (decrypt OK but wrapper/Value missing). PlaintextLength={data.Length}. Head={JsonHeadPreview(data)}");
            return false;
        }

        BasisDebug.Log("Converted byte array to JSON string...", BasisDebug.LogTag.Event);
        return true;
    }

    private static string JsonHeadPreview(byte[] data)
    {
        if (data == null || data.Length == 0) return "<empty>";
        int count = Math.Min(data.Length, 256);
        string preview = Encoding.UTF8.GetString(data, 0, count);
        StringBuilder sb = new StringBuilder(preview.Length);
        for (int i = 0; i < preview.Length; i++)
        {
            char c = preview[i];
            sb.Append(c < 0x20 || c == 0x7F ? '.' : c);
        }
        if (data.Length > count) sb.Append("...");
        return sb.ToString();
    }
}
