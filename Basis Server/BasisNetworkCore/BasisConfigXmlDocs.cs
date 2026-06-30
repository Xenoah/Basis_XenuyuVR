using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace Basis.Network.Core
{
    /// <summary>
    /// config object を <see cref="XmlSerializer"/> で serialize した後、
    /// 各 element の前に人間が読める XML comment を差し込む。
    /// これにより、生成/保存された config file が自分自身を説明できる。
    /// comment は毎回の save、default-create と admin-panel save で書かれるため、
    /// restart や save をまたいで保持される。読み込み時は comment を無視する。
    /// 登録済み doc がない config type は、XmlSerializer と同じ形で書かれる。
    /// </summary>
    public static class BasisConfigXmlDocs
    {
        public sealed class FieldDoc
        {
            public readonly string Field;
            public readonly string Comment;
            public readonly string Section;
            public FieldDoc(string field, string comment, string section = null)
            {
                Field = field;
                Comment = comment;
                Section = section;
            }
        }

        private sealed class TypeDoc
        {
            public string Header;
            public readonly List<FieldDoc> Fields = new List<FieldDoc>();
        }

        private static readonly Dictionary<Type, TypeDoc> _docs = new Dictionary<Type, TypeDoc>();

        static BasisConfigXmlDocs()
        {
            RegisterServerConfig();
            RegisterLnlConfig();
        }

        /// <summary><paramref name="type"/> 用の doc comment を注入しながら、<paramref name="value"/> を <paramref name="writer"/> へ serialize する。</summary>
        public static void Serialize(XmlSerializer serializer, Type type, object value, TextWriter writer)
        {
            var doc = new XDocument();
            using (var xw = doc.CreateWriter())
            {
                serializer.Serialize(xw, value);
            }
            Inject(doc, type);
            doc.Declaration = new XDeclaration("1.0", "utf-8", null);
            doc.Save(writer);
        }

        private static void Inject(XDocument doc, Type type)
        {
            if (doc.Root == null || !_docs.TryGetValue(type, out TypeDoc entry)) return;

            var byField = new Dictionary<string, FieldDoc>();
            foreach (FieldDoc f in entry.Fields) byField[f.Field] = f;

            foreach (XElement el in new List<XElement>(doc.Root.Elements()))
            {
                if (!byField.TryGetValue(el.Name.LocalName, out FieldDoc info)) continue;
                if (!string.IsNullOrEmpty(info.Section)) el.AddBeforeSelf(new XComment(info.Section));
                if (!string.IsNullOrEmpty(info.Comment)) el.AddBeforeSelf(new XComment(info.Comment));
            }

            if (!string.IsNullOrEmpty(entry.Header)) doc.Root.AddFirst(new XComment(entry.Header));
        }

        private const string VersionFieldName = "ConfigVersion";
        private const string CurrentVersionFieldName = "CurrentConfigVersion";

        /// <summary>
        /// disk 上の config が現在の schema より古く、再保存すべき場合に true。
        /// stamped <c>ConfigVersion</c> が type の <c>CurrentConfigVersion</c> より小さい場合、
        /// または現在の type が書くはずの element が file に欠けている場合が該当する。
        /// </summary>
        public static bool NeedsUpgrade(string filePath, Type type, object loaded)
        {
            if (ReadVersion(loaded) < ReadCurrentVersion(type)) return true;
            return IsMissingAnyField(filePath, type);
        }

        /// <summary><paramref name="filePath"/> の XML に、現在の <paramref name="type"/> が serialize する element が欠けている場合 true。</summary>
        public static bool IsMissingAnyField(string filePath, Type type)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                XDocument doc = XDocument.Load(filePath);
                if (doc.Root == null) return false;

                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (XElement el in doc.Root.Elements()) present.Add(el.Name.LocalName);

                foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.IsInitOnly || f.IsLiteral) continue;
                    if (Attribute.IsDefined(f, typeof(XmlIgnoreAttribute))) continue;
                    if (!present.Contains(f.Name)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>instance の <c>ConfigVersion</c> field があれば、type の <c>CurrentConfigVersion</c> まで stamp する。</summary>
        public static void StampVersion(object config)
        {
            if (config == null) return;
            Type type = config.GetType();
            FieldInfo f = type.GetField(VersionFieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(int)) f.SetValue(config, ReadCurrentVersion(type));
        }

        private static int ReadVersion(object config)
        {
            if (config == null) return 0;
            FieldInfo f = config.GetType().GetField(VersionFieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(int)) return (int)f.GetValue(config);
            return 0;
        }

        private static int ReadCurrentVersion(Type type)
        {
            FieldInfo f = type.GetField(CurrentVersionFieldName, BindingFlags.Public | BindingFlags.Static);
            if (f != null && f.IsLiteral && f.FieldType == typeof(int)) return (int)f.GetRawConstantValue();
            return 0;
        }

        private static void RegisterServerConfig()
        {
            var t = new TypeDoc
            {
                Header = " Basis dedicated-server configuration。下記 field 名と一致する environment variable は、launch 時に値を override します (例: PeerLimit=256)。これらの comment は code から出力されるため、restart や in-game admin-panel save 後も維持されます。 ",
            };
            t.Fields.Add(new FieldDoc("ConfigVersion", " config schema version。自動管理されます。server に新しい setting が追加されると、この file は default 値付きで書き直され、この番号が上がります。手で編集しないでください。 "));
            t.Fields.Add(new FieldDoc("PeerLimit", " 同時接続 peer (player) 数の最大値。int。default 65535。 ", " ===== Networking / listener ===== "));
            t.Fields.Add(new FieldDoc("SetPort", " server が bind して listen する UDP port。client はここへ接続します。ushort、範囲は 1-65535。 "));
            t.Fields.Add(new FieldDoc("ServerName", " client の server-list UI (server-info query) で row title として表示される display name。string。 "));
            t.Fields.Add(new FieldDoc("ServerMotd", " server name と一緒に返す短い message-of-the-day。string。空なら none。 "));
            t.Fields.Add(new FieldDoc("EnableStatistics", " transport statistics (peer/packet counter) を収集し、stats worker を実行します。health endpoint から確認できます。true|false。 "));
            t.Fields.Add(new FieldDoc("HasFileSupport", " disk への data 書き込みの master switch。server log、disk 上の moderation list、auth-identity persistence、chat file support に影響します。in-memory/ephemeral server にする場合は false。true|false。 "));
            t.Fields.Add(new FieldDoc("HealthCheckHost", " HTTP health endpoint が bind する host/interface。string (hostname または IP)。 ", " ===== Health-check HTTP endpoint ===== "));
            t.Fields.Add(new FieldDoc("HealthCheckPort", " HTTP health endpoint 用 port。ushort、範囲は 1-65535。 "));
            t.Fields.Add(new FieldDoc("HealthPath", " health endpoint が serve する URL path。例: /health。string。 "));
            t.Fields.Add(new FieldDoc("BSRSMillisecondDefaultInterval", " distance 0 での base send interval (milliseconds)。50 ms はおよそ 20 Hz。小さいほど update は頻繁になり bandwidth を多く使います。int (ms)。 ", " ===== Server Reduction System (avatar sync rate) ===== intervalMs = BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + distance^2 * BSRSIncreaseRate)。近い player は高速に、遠い player は段階的に低速に update されます。 "));
            t.Fields.Add(new FieldDoc("BSRBaseMultiplier", " distance scaling 前に base interval へ適用する flat multiplier。number。default 1。 "));
            t.Fields.Add(new FieldDoc("BSRSIncreaseRate", " squared distance によって interval がどれだけ速く増えるか。高いほど遠い player の update 頻度が大きく下がります。float。default 0.005。 "));
            t.Fields.Add(new FieldDoc("BSRSlowestSendRate", " 非常に遠い peer に対して client へ渡す最も遅い send-rate floor。float。0 は未設定として扱われ、2.55 に置き換わります。 "));
            t.Fields.Add(new FieldDoc("HighQualityDistance", " peer を sync-quality tier に振り分ける distance threshold (world unit/metre)。内部では squared distance として扱います。High < Medium < Low を保ってください。float。 "));
            t.Fields.Add(new FieldDoc("MediumQualityDistance", " medium-quality の distance threshold (HighQualityDistance 参照)。float。 "));
            t.Fields.Add(new FieldDoc("LowQualityDistance", " low-quality の distance threshold (HighQualityDistance 参照)。float。 "));
            t.Fields.Add(new FieldDoc("OverrideAutoDiscoveryOfIpv", " true の場合、IP version を auto-discover せず、下記 address に正確に bind します。true|false。 ", " ===== Address binding ===== "));
            t.Fields.Add(new FieldDoc("IPv4Address", " bind する IPv4 address。0.0.0.0 = すべての IPv4 interface。string。 "));
            t.Fields.Add(new FieldDoc("IPv6Address", " bind する IPv6 address。::1 = loopback、:: = すべての IPv6 interface。string。 "));
            t.Fields.Add(new FieldDoc("Password", " client が join 時に提示する必要がある password。local 以外の server では必ず変更してください。string。 ", " ===== Authentication ===== "));
            t.Fields.Add(new FieldDoc("UseAuth", " 上記 join password が正しいことを要求します。true|false。 "));
            t.Fields.Add(new FieldDoc("UseAuthIdentity", " password に加えて cryptographic player-identity (DID) verification を要求します。true|false。headless load-test client console はこれに対応しているため、有効のままにできます。 "));
            t.Fields.Add(new FieldDoc("NetworkStackId", " transport stack id。空なら default ('litenetlib')。登録済みは 'litenetlib' のみで、unknown id はそこへ fallback します。stack ごとの tuning は config/transports/<id>.xml にあります。string。 "));
            t.Fields.Add(new FieldDoc("BasisUserRestrictionMode", " player join restriction mode。許可値: Normal | BanList | AllowList | RejoinOnly。RejoinOnly は有効化時点で接続済みの player に server を lock し (admin は join 可能)、restart で Normal に戻ります。 "));
            t.Fields.Add(new FieldDoc("HowManyDuplicateAuthCanExist", " 同じ auth identity を共有する connection が同時にいくつ存在できるか。int。 "));
            t.Fields.Add(new FieldDoc("AuthValidationTimeOutMiliseconds", " client が auth validation を完了するまでの猶予時間。超過すると drop されます。int (ms)。 "));
            t.Fields.Add(new FieldDoc("EnableConsole", " interactive server console (CLI input) を有効にします。headless/daemon deployment では false にしてください。true|false。 ", " ===== Console / persistence ===== "));
            t.Fields.Add(new FieldDoc("DisableWriteUnlessAdminPersistentFlag", " caller が admin でない場合、persistent key/value store への write を拒否します。true|false。 "));
            t.Fields.Add(new FieldDoc("DisableReadUnlessAdminPersistentFlag", " caller が admin でない場合、persistent key/value store からの read を拒否します。true|false。 "));
            t.Fields.Add(new FieldDoc("EnableAvatarBundleCompression", " receiver ごとの avatar message を bundle し、compressed で送信します (有利でない場合は uncompressed per-message に fallback)。client は対応する decoder を support している必要があります。true|false。 ", " ===== Avatar bundle compression ===== "));
            t.Fields.Add(new FieldDoc("AvatarBundleMinMessages", " bundle を試みる前に、1 receiver 向けに queue されている必要がある avatar message の最小数。int。 "));
            t.Fields.Add(new FieldDoc("AvatarBundleMinBytes", " compression を試みる前に必要な uncompressed bundle size の最小値 (bytes)。int。 "));
            t.Fields.Add(new FieldDoc("EnableBSRProfiling", " Server Reduction System の profiling output を出力します。true|false。 "));
            t.Fields.Add(new FieldDoc("DisallowHeadless", " headless client の接続を拒否します。true|false。 "));
            t.Fields.Add(new FieldDoc("AvatarsLocked", " bypass 権限のない user による avatar loading を block します。true|false。 ", " ===== Content lockouts (boot 時に BasisGlobalLockManager へ seed。各 lock は admin panel から live toggle も可能)。lock 中に load するには、対応する basis.resource.lockbypass.{avatar,prop,world} permission が必要です。 ===== "));
            t.Fields.Add(new FieldDoc("PropsLocked", " bypass 権限のない user による prop loading を block します。true|false。 "));
            t.Fields.Add(new FieldDoc("WorldsLocked", " bypass 権限のない user による world loading を block します。true|false。 "));
            t.Fields.Add(new FieldDoc("ServersLocked", " content-share system 経由の saved-server entry sharing を block します。true|false。 "));
            t.Fields.Add(new FieldDoc("ThirdPersonDisabled", " desktop third-person camera を hard-disable するよう全 client に伝えます。true|false。 "));
            t.Fields.Add(new FieldDoc("AdditionalAvatarDataLock", " inbound avatar sync を relay する前に AdditionalAvatarDatas (blendshape、custom-behaviour param) を strip します。muscle/position/rotation は sync されます。true|false。 "));
            t.Fields.Add(new FieldDoc("CameraMetadataDisallowMask", " 全 client に対して disallow する camera photo-metadata category の bitmask (set bit = disallowed)。0 = すべて許可。byte、範囲は 0-255。category-to-bit mapping は client-side で定義されます。 "));
            t.Fields.Add(new FieldDoc("CrashReportingEnabled", " client が遭遇した各 error/exception について one-shot report を server へ送れるようにします。report は UUID と display name 付きで CrashReports/<uuid>.jsonl に保存されます。true|false、default true。false にすると全体で reporting を無効化し、client には送信停止を通知します。 ", " ===== Diagnostics ===== "));
            t.Fields.Add(new FieldDoc("MaxMicrophoneRangeMeters", " client が設定できる microphone (voice transmit) range の最大値 (metre)。client は Microphone Range slider と effective range をこの上限に clamp します。admin panel から live 変更も可能です。float、default 25。 ", " ===== Audio / voice range ===== "));
            t.Fields.Add(new FieldDoc("MaxHearingRangeMeters", " client が設定できる hearing (audio receive) range の最大値 (metre)。client は Hearing Range slider と effective range をこの上限に clamp します。float、default 25。 "));
            t.Fields.Add(new FieldDoc("MinAvatarEyeHeightMeters", " non-admin player が scale できる avatar eye height の最小値 (metre)。client は avatar scale をこの下限に clamp します。float、default 0.1 (実質的に最小なし)。admin (basis.moderation.globallock) は bypass します。 ", " ===== Avatar scale + movement restrictions (boot 時に seed。admin panel から live toggle 可能)。basis.moderation.globallock を持つ admin は bypass します。 ===== "));
            t.Fields.Add(new FieldDoc("MaxAvatarEyeHeightMeters", " non-admin player が scale できる avatar eye height の最大値 (metre)。client は avatar scale をこの上限に clamp します。float、default 100 (実質的に最大なし)。 "));
            t.Fields.Add(new FieldDoc("PlayspaceMoverLocked", " non-admin player が playspace mover (play space の grabbing/dragging/rotating/scaling) を使うことを停止します。true|false、default false。 "));
            t.Fields.Add(new FieldDoc("DirectConnectLocked", " non-admin player 向けの direct (peer-to-peer) connection broker を拒否します。client 側でも direct-connect control を隠します。true|false、default false。 "));
            _docs[typeof(global::Configuration)] = t;
        }

        private static void RegisterLnlConfig()
        {
            var t = new TypeDoc
            {
                Header = " LiteNetLib transport tuning ('litenetlib' network stack 用 sidecar)。LiteNetLib の NetManager に対応します。[NOT APPLIED] と書かれた field は serialize されますが、現時点では server の NetManager に接続されていません。comment は code から出力されるため、restart や save 後も維持されます。 ",
            };
            t.Fields.Add(new FieldDoc("ConfigVersion", " config schema version。自動管理されます。load 時に新しい setting がこの file へ追加されます。手で編集しないでください。 "));
            t.Fields.Add(new FieldDoc("UseNativeSockets", " managed path ではなく OS-native socket call を使います (overhead が低い)。true|false。 "));
            t.Fields.Add(new FieldDoc("NatPunchEnabled", " peer introduction 用の NAT punch-through module を有効にします。true|false。 "));
            t.Fields.Add(new FieldDoc("NatPortPredictionRange", " Hard-NAT traversal: peer の server-observed external port より上の連続 port を、OTHER peer 側でもいくつ punch するか (port-prediction spray)。peer-to-peer port が server から見える port と異なる sequential symmetric/CGNAT mapping を助けます。0 で spray 無効。妥当な範囲は 0-128。int。 "));
            t.Fields.Add(new FieldDoc("PingInterval", " 各 peer への keep-alive ping 間隔。int (ms)。 "));
            t.Fields.Add(new FieldDoc("DisconnectTimeout", " peer から response がなくなってから disconnect するまでの時間。int (ms)。 "));
            t.Fields.Add(new FieldDoc("SimulatePacketLoss", " outgoing packet を人工的に drop します。true|false。 ", " ===== Debug network simulation (testing only) ===== "));
            t.Fields.Add(new FieldDoc("SimulateLatency", " packet に人工的な delay を加えます。true|false。 "));
            t.Fields.Add(new FieldDoc("SimulationPacketLossChance", " SimulatePacketLoss が true の場合の packet-loss percentage。int、範囲は 0-100。 "));
            t.Fields.Add(new FieldDoc("SimulationMinLatency", " SimulateLatency が true の場合に追加する latency の最小値。int (ms)。Min <= Max を保ってください。 "));
            t.Fields.Add(new FieldDoc("SimulationMaxLatency", " SimulateLatency が true の場合に追加する latency の最大値。int (ms)。 "));
            t.Fields.Add(new FieldDoc("ReconnectDelay", " [NOT APPLIED] reconnect attempt 間の delay (client-side connect option)。int (ms)。 "));
            t.Fields.Add(new FieldDoc("MaxConnectAttempts", " [NOT APPLIED] 諦めるまでの connection attempt 数 (client-side connect option)。int。 "));
            t.Fields.Add(new FieldDoc("ReuseAddresss", " [NOT APPLIED] SO_REUSEADDR socket option を設定します (field name の spelling に注意)。true|false。 "));
            t.Fields.Add(new FieldDoc("DontRoute", " [NOT APPLIED] SO_DONTROUTE socket option を設定します (routing table を bypass)。true|false。 "));
            t.Fields.Add(new FieldDoc("IPv6Enabled", " IPv6 (dual-stack) socket support を有効にします。true|false。 "));
            t.Fields.Add(new FieldDoc("MtuOverride", " negotiate せず fixed MTU を強制します。int (bytes)。0 = auto/disabled。 "));
            t.Fields.Add(new FieldDoc("MtuDiscovery", " path-MTU discovery を有効にします。true|false。 "));
            t.Fields.Add(new FieldDoc("DisconnectOnUnreachable", " [NOT APPLIED] ICMP 'unreachable' を受信したとき peer を disconnect します。true|false。 "));
            t.Fields.Add(new FieldDoc("AllowPeerAddressChange", " session 中に peer の remote endpoint (IP/port) が変わることを許可します。例: mobile network roaming。true|false。 "));
            t.Fields.Add(new FieldDoc("MultiSocketCount", " SO_REUSEPORT を使って listen port に bind する UDP socket 数 (Linux only)。1 = single socket / single receive thread (default)。N>1 では N-1 個の extra socket + receive thread を spawn し、Linux kernel が inbound 4-tuple を RSS hash して分散します。peer ごとの 4-tuple は stable なので packet order は維持されます。Windows/macOS では Start() 時に warning を出して 1 に fallback します。目安: 1k players 付近で 2-4、2k 付近で 4-8。int。 "));
            _docs[typeof(LNLTransportConfig)] = t;
        }
    }
}
