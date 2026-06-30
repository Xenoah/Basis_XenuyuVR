using Basis.Network.Core;
using BasisNetworkCore.Security;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;

[Serializable]
public class Configuration
{
    public const string ConfigFolderName = "config";
    public const string LogsFolderName = "logs";
    public const string InitialResourcesFolderName = "initialresources";
    public const string DefaultLibraryFolderName = "defaultlibrary";

    /// <summary>
    /// config change により既存 file を強制的に rewrite したい場合に上げる
    /// (例: doc comment の refresh)。新しく追加された setting はどちらにせよ自動で heal される。
    /// load 時に current field が欠けている config は、新しい setting を追加した状態で re-save される。
    /// </summary>
    public const int CurrentConfigVersion = 3;
    /// <summary>config.xml に stamp される schema version。0 = versioning 前の file で、load 時に upgrade される。</summary>
    public int ConfigVersion = 0;

    public int PeerLimit = ushort.MaxValue;
    public ushort SetPort = 4296;
    /// <summary>unconnected server-info query が返す display name。client server-list UI の row title に表示される。</summary>
    public string ServerName = "Basis Server";
    /// <summary>info query response で server name と一緒に返す短い MOTD。list UI では短い 2 行がきれいに表示される。</summary>
    public string ServerMotd = "";
    public bool EnableStatistics = true;
    public bool HasFileSupport = true;
    public string HealthCheckHost = "localhost";
    public ushort HealthCheckPort = 10666;
    public string HealthPath = "/health";
    public int BSRSMillisecondDefaultInterval = 50;
    public int BSRBaseMultiplier = 1;
    public float BSRSIncreaseRate = 0.005f;
    public float BSRSlowestSendRate = 2.55f;
    public float HighQualityDistance = 10f;
    public float MediumQualityDistance = 20f;
    public float LowQualityDistance = 40f;
    public bool OverrideAutoDiscoveryOfIpv = false;
    public string IPv4Address = "0.0.0.0";
    public string IPv6Address = "::";
    public string Password = "default_password";
    public bool UseAuth = true;
    public bool UseAuthIdentity = true;
    public string NetworkStackId = "";
    public BasisUserRestrictionMode BasisUserRestrictionMode;
    public int HowManyDuplicateAuthCanExist = 2;
    public int AuthValidationTimeOutMiliseconds = 9000;
    public bool EnableConsole = true;
    public bool DisableWriteUnlessAdminPersistentFlag = true;
    public bool DisableReadUnlessAdminPersistentFlag = false;
    /// <summary>
    /// true の場合、avatar reduction system は receiver ごとの avatar message を bundle し、
    /// CompressedAvatarBundleChannel で deflated として emit する。
    /// receiver の queued message が少なすぎて compression の価値がない場合、
    /// または compressed result が peer MTU を超える場合は、message ごとの uncompressed send に fallback する。
    /// client は対応する decoder を実装している必要がある。
    /// </summary>
    public bool EnableAvatarBundleCompression = true;
    /// <summary>single receiver に対して bundle を試みる前に必要な queued avatar message の最小数。</summary>
    public int AvatarBundleMinMessages = 4;
    /// <summary>LZ4 compression を試みる前に必要な uncompressed bundle bytes の最小値。LZ4 は per-call setup がほぼ 0 のため、128 は LZ4 が redundancy を見つけられない極小 case を guard するだけ。</summary>
    public int AvatarBundleMinBytes = 128;
    public bool EnableBSRProfiling = false;
    public bool DisallowHeadless = false;

    // server boot 時に適用する global lockout default。
    // lock 中に load するには、対応する basis.resource.lockbypass.{avatar,prop,world} permission が必要。
    public bool AvatarsLocked = false;
    public bool PropsLocked = false;
    public bool WorldsLocked = true;
    /// <summary>
    /// true の場合、peer は content share system 経由で saved-server entry を share できない。
    /// admin panel から live toggle でき、他の content lockout と一緒に config.xml へ persist される。
    /// 既存 deployment が従来どおり動くよう、default は off。
    /// </summary>
    public bool ServersLocked = false;
    /// <summary>
    /// true の場合、server はすべての client に desktop third-person camera を hard-disable するよう伝える。
    /// admin panel から live toggle でき、他の content lockout と一緒に config.xml へ persist される。
    /// 既存 deployment が従来どおり動くよう、default は off。
    /// </summary>
    public bool ThirdPersonDisabled = false;
    /// <summary>
    /// true の場合、server は inbound avatar sync message を他 peer へ propagate する前に
    /// AdditionalAvatarDatas (blendshape、custom-behaviour param) を strip する。
    /// muscle/position/rotation は通常どおり sync し、additional-data payload だけが drop される。
    /// admin panel から live toggle でき、他の content lockout と一緒に persist される。default は off。
    /// </summary>
    public bool AdditionalAvatarDataLock = false;
    /// <summary>
    /// 全 client に対して disallow する camera photo-metadata embedding category の per-category bitmask。
    /// 0 = すべて許可 (default)。boot 時に BasisGlobalLockManager へ seed され、
    /// GlobalGetLockState で client へ broadcast される。
    /// </summary>
    public byte CameraMetadataDisallowMask = 0;
    public bool CrashReportingEnabled = true;
    public float MaxMicrophoneRangeMeters = 25f;
    public float MaxHearingRangeMeters = 25f;
    public float MinAvatarEyeHeightMeters = 0.1f;
    public float MaxAvatarEyeHeightMeters = 100f;
    public int MaxDatabaseEntries = 10000;
    public int MaxDatabaseNameLength = 256;
    public int MaxDatabasePayloadEntries = 1000;
    public int MaxContentSpheresPerPlayer = 32;
    public bool PlayspaceMoverLocked = false;
    public bool DirectConnectLocked = false;

    // ── REST API ──────────────────────────────────────────────────────────────
    /// <summary>REST management API を有効にするには true にする。</summary>
    public bool ApiEnabled = false;
    public string ApiHost = "localhost";
    public ushort ApiPort = 10667;
    /// <summary>すべての API request で必要な bearer token。空文字列なら ApiEnabled が true でも API は無効。</summary>
    public string ApiKey = "";
    /// <summary>
    /// file から config を読む。file が見つからない場合は filePath に default config file を作成する。
    /// <c>{configDir}/transports/{stackId}.xml</c> から transport ごとの config sidecar も load する。
    /// </summary>
    public static Configuration LoadFromXml(string filePath)
    {
        RuntimeHelpers.RunClassConstructor(typeof(BasisNetworkStackRegistry).TypeHandle);

        Configuration result;
        var serializer = new XmlSerializer(typeof(Configuration));
        if (File.Exists(filePath))
        {
            using (var fileReader = new StreamReader(filePath))
            {
                result = (Configuration)serializer.Deserialize(fileReader);
            }

            // 古い config を heal する。current schema version より古いか、現在書き出す setting が欠けている場合、
            // 既存値を崩さず、新しい setting (default と doc comment 付き) を追加するため re-save する。
            if (BasisConfigXmlDocs.NeedsUpgrade(filePath, typeof(Configuration), result))
            {
                BNL.Log($"{filePath} is from an older version; adding missing settings.");
                result.WriteXml(filePath);
            }
        }
        else
        {
            BNL.Log($"{filePath} not found, creating with default values");
            result = new Configuration();
            result.WriteXml(filePath);
        }

        string configDir = Path.GetDirectoryName(filePath);
        BasisTransportConfigStore.LoadAll(configDir);
        return result;
    }

    /// <summary>
    /// この configuration を <paramref name="filePath"/> へ persist する。
    /// admin panel による in-game change (server name、MOTD、allowlist mode) を restart 後も残すために使う。
    /// sibling temp file + atomic move で書き込み、write 中の crash で live config が壊れないようにする。
    /// </summary>
    public void SaveToXml(string filePath)
    {
        WriteXml(filePath);
        BasisTransportConfigStore.SaveAll(Path.GetDirectoryName(filePath));
    }

    /// <summary>
    /// この config.xml だけを atomic に write する (temp file + replace)。
    /// current schema version を stamp し、doc comment を inject する。transport sidecar には触れない。
    /// </summary>
    private void WriteXml(string filePath)
    {
        ConfigVersion = CurrentConfigVersion;
        var serializer = new XmlSerializer(typeof(Configuration));
        string dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = filePath + ".tmp";
        using (var writer = new StreamWriter(tempPath))
        {
            BasisConfigXmlDocs.Serialize(serializer, typeof(Configuration), this, writer);
        }
        if (File.Exists(filePath)) File.Replace(tempPath, filePath, null);
        else File.Move(tempPath, filePath);
    }

    /// <summary>
    /// <c>{BaseDirectory}/{ConfigFolderName}/config.xml</c> 配下の canonical config.xml path を解決する。
    /// startup 時に bootstrapper (BasisServerConsole.Program / Unity host runner) が読む path と同じ。
    /// </summary>
    public static string GetDefaultPath()
    {
        return Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, ConfigFolderName, "config.xml");
    }

    /// <summary>
    /// public config field と同じ名前の environment variable が見つかった場合、
    /// config.xml に書かれている値をこの code が override する。
    ///
    /// Windows では console で次のように test できる:
    ///    $env:PeerLimit = "256"
    ///   .\BasisNetworkConsole.exe
    /// ただし本来は、Linux admin が launch 時に default を override できるようにするためのもの。
    /// </summary>
    public void ProcessEnvironmentalOverrides()
    {
        ApplyEnvironmentalOverridesTo(this);
    }

    private static void ApplyEnvironmentalOverridesTo(object target)
    {
        if (target == null) return;
        Type type = target.GetType();
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields)
        {
            if (!field.FieldType.IsPrimitive && field.FieldType != typeof(string) && field.FieldType.IsClass)
            {
                object nested = field.GetValue(target);
                if (nested != null) ApplyEnvironmentalOverridesTo(nested);
                continue;
            }

            string value = Environment.GetEnvironmentVariable(field.Name);
            if (value == null) continue;

            BNL.Log($"Applying Environmental Override with Field:{field.Name} Value:{value}");

            if (field.FieldType == typeof(int))
            {
                if (int.TryParse(value, out int number)) field.SetValue(target, number);
                else BNL.LogWarning("Could not cast to int. Failed Override");
            }
            else if (field.FieldType == typeof(ushort))
            {
                if (ushort.TryParse(value, out ushort number)) field.SetValue(target, number);
                else BNL.LogWarning("Could not cast to ushort. Failed Override.");
            }
            else if (field.FieldType == typeof(float))
            {
                if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number)) field.SetValue(target, number);
                else BNL.LogWarning("Could not cast to float. Failed Override.");
            }
            else if (field.FieldType == typeof(string))
            {
                field.SetValue(target, value);
            }
            else if (field.FieldType == typeof(bool))
            {
                if (bool.TryParse(value, out bool boolResult)) field.SetValue(target, boolResult);
                else BNL.LogWarning($"Could not parse '{value}' as bool for field {field.Name}. Failed Override");
            }
            else
            {
                BNL.LogWarning($"Environmental variable type could not be processed for Config Field:{field.Name} Value:{value}");
            }
        }
    }
}
