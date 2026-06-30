using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Xml;
using static SerializableBasis;
namespace BasisPermissions
{
    // =========================
    // permission node constants
    // =========================
    public static class PermNodes
    {
        public const string All = "*";
        public const string help = "basis.command.help";
        public const string ServerStats = "basis.server.stats";

        public const string ResourceLoadWorld = "basis.resource.load.world";
        public const string ResourceUnloadWorld = "basis.resource.unload.world";

        public const string ResourceLoadProp = "basis.resource.load.prop";
        public const string ResourceUnloadProp = "basis.resource.unload.prop";

        public const string ResourceLoadAvatar = "basis.resource.load.avatar";
        public const string ResourceUnloadAvatar = "basis.resource.unload.avatar";

        // global lockout (BasisGlobalLockManager) を bypass する。
        // matching bypass node を持たない user は、lock 中に loading を block される。
        public const string ResourceLockBypassAvatar = "basis.resource.lockbypass.avatar";
        public const string ResourceLockBypassProp = "basis.resource.lockbypass.prop";
        public const string ResourceLockBypassWorld = "basis.resource.lockbypass.world";
        /// <summary>server share 開始時に <c>ServersLocked</c> を bypass する。</summary>
        public const string ResourceLockBypassServer = "basis.resource.lockbypass.server";

        public const string OwnershipTransfer = "basis.ownership.transfer";
        public const string OwnershipRemove = "basis.ownership.remove";
        public const string OwnershipGet = "basis.ownership.get";

        public const string ContentShareDelete = "basis.contentshare.delete";
        public const string ContentShareCreate = "basis.contentshare.create";

        /// <summary>
        /// この person の action が interference から保護されていることを示すために使う。
        /// </summary>
        public const string protection = "basis.protection";

        public const string ConfigurationEditor = "basis.configuration";

        public const string PlayerModeration = "basis.moderation";

        public const string ModerationBan = "basis.moderation.ban";
        public const string ModerationKick = "basis.moderation.kick";
        public const string ModerationIpBan = "basis.moderation.ipban";
        public const string ModerationUnban = "basis.moderation.unban";
        public const string ModerationUnbanIp = "basis.moderation.unbanip";
        public const string ModerationMessage = "basis.moderation.message";
        public const string ModerationMessageAll = "basis.moderation.messageall";
        public const string ModerationTeleport = "basis.moderation.teleport";
        public const string ModerationShout = "basis.moderation.shout";
        public const string ModerationGlobalLock = "basis.moderation.globallock";
        public const string ModerationHeadlessAudio = "basis.moderation.headlessaudio";
        public const string ModerationOpusBitrate = "basis.moderation.opusbitrate";
        /// <summary>server allow-list 上の UUID を add/remove する (ban management とは別)。</summary>
        public const string ModerationAllowlist = "basis.moderation.whitelist";
        public const string AdminLogs = "basis.admin.logs";

        public const string PermissionsView = "basis.permissions.view";
        public const string PermissionsEdit = "basis.permissions.edit";
    }

    // =========================
    // data model
    // =========================
    public sealed class PermissionUser
    {
        public string Uuid = "";
        // user に割り当てられた raw node ("-node" deny entry を含められる)。
        public HashSet<string> Nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // group membership。
        public HashSet<string> Groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PermissionGroup
    {
        public string Name = "";
        // group に割り当てられた raw node ("-node" deny entry を含められる)。
        public HashSet<string> Nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // parent group inheritance。
        public HashSet<string> Parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PermissionStore
    {
        public Dictionary<string, PermissionUser> Users = new Dictionary<string, PermissionUser>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PermissionGroup> Groups = new Dictionary<string, PermissionGroup>(StringComparer.OrdinalIgnoreCase);
    }
    public sealed class EffectivePermissions
    {
        // 判定テーブル: node => allow(true) / deny(false)。
        // inheritance resolution 後の exact node と wildcard node ("a.*", "*") を含む。
        private readonly Dictionary<string, bool> _decisions;

        internal EffectivePermissions(Dictionary<string, bool> decisions)
        {
            _decisions = decisions;
        }

        // O(depth) の確認: a.b.c -> a.b.* -> a.* -> *
        public bool Has(string node)
        {
            if (string.IsNullOrWhiteSpace(node))
            {
                return false;
            }

            node = node.Trim();

            // exact。
            if (_decisions.TryGetValue(node, out bool exact))
            {
                return exact;
            }

            // wildcard を上に辿る。
            int idx = node.Length;
            while (true)
            {
                idx = node.LastIndexOf('.', idx - 1);
                if (idx < 0) break;

                string wildcard = node.Substring(0, idx) + ".*";
                if (_decisions.TryGetValue(wildcard, out bool w))
                {
                    return w;
                }
            }

            // global wildcard。
            return _decisions.TryGetValue("*", out bool star) ? star : false;
        }

        // decision map 内で allow(true) の node をすべて返す。
        // note: これは effective *rule* を返すもので、"expanded" node list ではない
        // (expand には possible node の registry が必要)。
        public IReadOnlyCollection<string> GetAllAllowedRules()
        {
            List<string> allowed = new List<string>(_decisions.Count);
            foreach (var kv in _decisions)
            {
                if (kv.Value)
                {
                    allowed.Add(kv.Key);
                }
            }

            return allowed;
        }

        public IReadOnlyCollection<string> GetAllDeniedRules()
        {
            List<string> denied = new List<string>(_decisions.Count);
            foreach (var kv in _decisions)
            {
                if (!kv.Value)
                {
                    denied.Add(kv.Key);
                }
            }

            return denied;
        }

        // debugging / admin UI 用。
        public IReadOnlyDictionary<string, bool> GetDecisionMap() => _decisions;
    }

    // ============================================
    // permission manager (thread-safe + cached)
    // ============================================
    public sealed class PermissionManager
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private PermissionStore _store = new PermissionStore();

        // cache: uuid -> (version, effective perms)
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private int _version = 0;

        // persistence 用 file path。
        private string _xmlPath = "permissions.xml";

        // 小さな change ごとに write しないよう save を debounce する。
        private readonly object _saveGate = new object();
        private Timer? _saveTimer;
        private volatile bool _dirty = false;

        // 必要に応じて調整する。
        public int SaveDebounceMs { get; set; } = 750;

        /// <summary>
        /// permission mutation 後、write lock の外で fire される。
        /// argument は affected UUID。group change が全 user に影響する場合は null。
        /// </summary>
        public Action<string> OnPermissionsChanged;

        // -------------
        // public API
        // -------------
        public void SetXmlPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path cannot be empty");
            _xmlPath = path;
        }

        public string GetXmlPath() => _xmlPath;
        public void LoadFromXml(string? pathOverride = null)
        {
            string path = pathOverride ?? _xmlPath;
            PermissionStore loaded = PermissionXml.Load(path);

            _lock.EnterWriteLock();
            try
            {
                _store = loaded;
                _version++;
                _cache.Clear();
                _dirty = false;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void SaveToXml(string? pathOverride = null)
        {
            string path = pathOverride ?? _xmlPath;
            PermissionStore snapshot = Snapshot();
            PermissionXml.Save(path, snapshot);

            _dirty = false;
        }

        // debounced save: edit 後に呼ぶ。
        public void SaveToXmlDebounced()
        {
            _dirty = true;
            lock (_saveGate)
            {
                if (_saveTimer == null)
                {
                    _saveTimer = new Timer(_ => DebouncedSaveTick(), null, SaveDebounceMs, Timeout.Infinite);
                }
                else
                {
                    _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
                }
            }
        }

        private void DebouncedSaveTick()
        {
            try
            {
                if (_dirty)
                    SaveToXml();
            }
            catch
            {
                // swallow する。必要ならここで log する。
            }
        }

        public bool Has(string uuid, string node)
        {
            return GetEffective(uuid).Has(node);
        }

        public IReadOnlyCollection<string> GetAllAllowedRules(string uuid)
        {
            return GetEffective(uuid).GetAllAllowedRules();
        }

        public IReadOnlyCollection<string> GetAllDeniedRules(string uuid)
        {
            return GetEffective(uuid).GetAllDeniedRules();
        }

        public bool TryGetUser(string uuid, out PermissionUser user)
        {
            _lock.EnterReadLock();
            try { return _store.Users.TryGetValue(uuid, out user!); }
            finally { _lock.ExitReadLock(); }
        }

        public bool TryGetGroup(string name, out PermissionGroup group)
        {
            _lock.EnterReadLock();
            try { return _store.Groups.TryGetValue(name, out group!); }
            finally { _lock.ExitReadLock(); }
        }

        // user を create / get する。
        public PermissionUser GetOrCreateUser(string uuid)
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_store.Users.TryGetValue(uuid, out var u))
                {
                    return u;
                }

                _lock.EnterWriteLock();
                try
                {
                    if (_store.Users.TryGetValue(uuid, out u))
                    {
                        return u;
                    }

                    u = new PermissionUser { Uuid = uuid };
                    u.Groups.Add("default");
                    _store.Users[uuid] = u;
                    TouchUser(uuid);
                    return u;
                }
                finally { _lock.ExitWriteLock(); }
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }

        // group を create / get する。
        public PermissionGroup GetOrCreateGroup(string name)
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_store.Groups.TryGetValue(name, out var g))
                    return g;

                _lock.EnterWriteLock();
                try
                {
                    if (_store.Groups.TryGetValue(name, out g))
                        return g;

                    g = new PermissionGroup { Name = name };
                    _store.Groups[name] = g;
                    TouchAll();
                    return g;
                }
                finally { _lock.ExitWriteLock(); }
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }

        // mutator (cache を invalidate する)。
        public void AddUserNode(string uuid, string node)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(node))
            {
                return;
            }

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var u = GetOrCreateUser_NoLock(uuid);
                if (u.Nodes.Add(node.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void RemoveUserNode(string uuid, string node)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(node)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Users.TryGetValue(uuid, out var u) && u.Nodes.Remove(node.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void AddUserToGroup(string uuid, string group)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(group)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var u = GetOrCreateUser_NoLock(uuid);
                if (u.Groups.Add(group.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void RemoveUserFromGroup(string uuid, string group)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(group)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Users.TryGetValue(uuid, out var u) && u.Groups.Remove(group.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void AddGroupNode(string groupName, string node)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(node)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var g = GetOrCreateGroup_NoLock(groupName);
                if (g.Nodes.Add(node.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public void RemoveGroupNode(string groupName, string node)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(node)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Groups.TryGetValue(groupName, out var g) && g.Nodes.Remove(node.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public void AddGroupParent(string groupName, string parentName)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(parentName)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var g = GetOrCreateGroup_NoLock(groupName);
                if (g.Parents.Add(parentName.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public void RemoveGroupParent(string groupName, string parentName)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(parentName)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Groups.TryGetValue(groupName, out var g) && g.Parents.Remove(parentName.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public bool DeleteGroup(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return false;

            _lock.EnterWriteLock();
            try
            {
                if (!_store.Groups.Remove(groupName))
                    return false;

                // この group を参照している全 user から削除する。
                foreach (var u in _store.Users.Values)
                    u.Groups.Remove(groupName);

                // 他 group の parent からこの group を削除する。
                foreach (var g in _store.Groups.Values)
                    g.Parents.Remove(groupName);

                TouchAll();
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            OnPermissionsChanged?.Invoke(null);
            return true;
        }

        // save または admin viewing 用に store を snapshot する。
        public PermissionStore Snapshot()
        {
            _lock.EnterReadLock();
            try
            {
                var copy = new PermissionStore();

                foreach (var u in _store.Users)
                {
                    copy.Users[u.Key] = new PermissionUser
                    {
                        Uuid = u.Value.Uuid,
                        Nodes = new HashSet<string>(u.Value.Nodes, StringComparer.OrdinalIgnoreCase),
                        Groups = new HashSet<string>(u.Value.Groups, StringComparer.OrdinalIgnoreCase)
                    };
                }

                foreach (var g in _store.Groups)
                {
                    copy.Groups[g.Key] = new PermissionGroup
                    {
                        Name = g.Value.Name,
                        Nodes = new HashSet<string>(g.Value.Nodes, StringComparer.OrdinalIgnoreCase),
                        Parents = new HashSet<string>(g.Value.Parents, StringComparer.OrdinalIgnoreCase)
                    };
                }

                return copy;
            }
            finally { _lock.ExitReadLock(); }
        }
        private struct CacheEntry
        {
            public int Version;
            public EffectivePermissions Perms;
        }

        private void TouchUser(string uuid)
        {
            _version++;
            _cache.Remove(uuid);
            _dirty = true;
        }

        private void TouchAll()
        {
            _version++;
            _cache.Clear();
            _dirty = true;
        }

        public void EvictUserCache(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            _lock.EnterWriteLock();
            try
            {
                _cache.Remove(uuid);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private EffectivePermissions GetEffective(string uuid)
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_cache.TryGetValue(uuid, out var entry) && entry.Version == _version)
                    return entry.Perms;

                _lock.EnterWriteLock();
                try
                {
                    if (_cache.TryGetValue(uuid, out entry) && entry.Version == _version)
                        return entry.Perms;

                    var built = BuildEffective_NoLock(uuid);
                    _cache[uuid] = new CacheEntry { Version = _version, Perms = built };
                    return built;
                }
                finally { _lock.ExitWriteLock(); }
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }

        public EffectivePermissions BuildEffective_NoLock(string uuid)
        {
            // deny-wins decision table。
            var decisions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // user が store に存在しない場合、implicit "default" group の一員として扱う。
            // これにより <Group name="default"> が実際の default のように振る舞う。
            PermissionUser user;
            if (!_store.Users.TryGetValue(uuid, out user))
            {
                user = new PermissionUser { Uuid = uuid };
                user.Groups.Add("default"); // ✅ implicit default group
            }

            // 1) inheritance 付きで group を適用する (parents first)。
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in user.Groups)
                ApplyGroupRecursive_NoLock(g, visited, decisions);

            // 2) user node を最後に適用する (user は group を override するが、deny は常に勝つ)。
            ApplyRawNodes(user.Nodes, decisions);

            return new EffectivePermissions(decisions);
        }

        private void ApplyGroupRecursive_NoLock(string groupName, HashSet<string> visited, Dictionary<string, bool> decisions)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                return;

            groupName = groupName.Trim();

            if (!visited.Add(groupName))
                return;

            if (!_store.Groups.TryGetValue(groupName, out var group))
                return;

            // parent が先、その後にこの group。
            foreach (var p in group.Parents)
                ApplyGroupRecursive_NoLock(p, visited, decisions);

            ApplyRawNodes(group.Nodes, decisions);
        }

        // raw node は "-node" deny を含められる。
        // deny は常に allow に勝つ。
        private static void ApplyRawNodes(HashSet<string> rawNodes, Dictionary<string, bool> decisions)
        {
            foreach (var raw in rawNodes)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string node = raw.Trim();
                bool allow = true;

                if (node.Length > 0 && node[0] == '-')
                {
                    allow = false;
                    node = node.Substring(1).Trim();
                    if (node.Length == 0) continue;
                }

                if (decisions.TryGetValue(node, out bool existing))
                {
                    if (!existing)
                    {
                        // すでに denied の場合、絶対に overwrite しない。
                        continue;
                    }

                    // existing allow は deny で override できる。
                    if (!allow)
                        decisions[node] = false;
                    else
                        decisions[node] = true;
                }
                else
                {
                    decisions[node] = allow;
                }
            }
        }

        private PermissionUser GetOrCreateUser_NoLock(string uuid)
        {
            if (_store.Users.TryGetValue(uuid, out var u))
                return u;

            u = new PermissionUser { Uuid = uuid };
            u.Groups.Add("default");
            _store.Users[uuid] = u;
            return u;
        }

        private PermissionGroup GetOrCreateGroup_NoLock(string name)
        {
            if (_store.Groups.TryGetValue(name, out var g))
                return g;

            g = new PermissionGroup { Name = name };
            _store.Groups[name] = g;
            return g;
        }

        // -----------------------
        // convenience: default setup
        // -----------------------
        public void EnsureDefaults()
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_store.Groups.ContainsKey("default"))
                {
                    PermissionGroup def = new PermissionGroup { Name = "default" };
                    // 既存 example。
                    def.Nodes.Add(PermNodes.help);
                    // default user はこれらを持つべき。
                    def.Nodes.Add(PermNodes.ResourceLoadProp);
                    def.Nodes.Add(PermNodes.ResourceUnloadProp);

                    def.Nodes.Add(PermNodes.ResourceLoadAvatar);
                    def.Nodes.Add(PermNodes.ResourceUnloadAvatar);

                    def.Nodes.Add(PermNodes.ResourceLoadWorld);
                    def.Nodes.Add(PermNodes.ResourceUnloadWorld);

                    def.Nodes.Add(PermNodes.OwnershipTransfer);
                    def.Nodes.Add(PermNodes.OwnershipRemove);
                    def.Nodes.Add(PermNodes.OwnershipGet);

                    def.Nodes.Add(PermNodes.ContentShareDelete);
                    def.Nodes.Add(PermNodes.ContentShareCreate);

                    _store.Groups["default"] = def;
                }
                if (!_store.Groups.ContainsKey("moderator"))
                {
                    var adm = new PermissionGroup { Name = "moderator" };
                    adm.Parents.Add("default");
                    adm.Nodes.Add(PermNodes.ModerationBan);
                    adm.Nodes.Add(PermNodes.ModerationKick);
                    adm.Nodes.Add(PermNodes.ModerationIpBan);
                    adm.Nodes.Add(PermNodes.ModerationUnban);
                    adm.Nodes.Add(PermNodes.ModerationUnbanIp);
                    adm.Nodes.Add(PermNodes.ModerationMessage);
                    adm.Nodes.Add(PermNodes.ModerationMessageAll);
                    adm.Nodes.Add(PermNodes.ModerationTeleport);
                    adm.Nodes.Add(PermNodes.ModerationShout);
                    adm.Nodes.Add(PermNodes.ModerationGlobalLock);
                    adm.Nodes.Add(PermNodes.ModerationHeadlessAudio);
                    adm.Nodes.Add(PermNodes.ModerationOpusBitrate);
                    adm.Nodes.Add(PermNodes.PermissionsView);

                    adm.Nodes.Add(PermNodes.ResourceLockBypassAvatar);
                    adm.Nodes.Add(PermNodes.ResourceLockBypassProp);
                    adm.Nodes.Add(PermNodes.ResourceLockBypassWorld);
                    adm.Nodes.Add(PermNodes.ResourceLockBypassServer);

                    _store.Groups["moderator"] = adm;
                }
                if (!_store.Groups.ContainsKey("admin"))
                {
                    var adm = new PermissionGroup { Name = "admin" };
                    adm.Nodes.Add("*");
                    adm.Parents.Add("moderator");
                    _store.Groups["admin"] = adm;
                }

                _version++;
                _cache.Clear();
                _dirty = true;
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
        }

        // =========================================
        // XML 永続化 (高速な XmlReader/XmlWriter)
        // =========================================
        public static class PermissionXml
        {
            // XML 形式:
            // <Permissions>
            //   <Groups>
            //     <Group name="default">
            //       <Parent name="base"/>
            //       <Node value="basis.command.help"/>
            //       <Node value="-basis.command.kick"/>
            //     </Group>
            //   </Groups>
            //   <Users>
            //     <User uuid="abc">
            //       <Group name="admin"/>
            //       <Node value="basis.resource.load"/>
            //     </User>
            //   </Users>
            // </Permissions>

            public static PermissionStore Load(string path)
            {
                var store = new PermissionStore();
                if (!File.Exists(path))
                    return store;

                var settings = new XmlReaderSettings
                {
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    DtdProcessing = DtdProcessing.Prohibit
                };

                using var fs = File.OpenRead(path);
                using var xr = XmlReader.Create(fs, settings);

                PermissionGroup? currentGroupDef = null;
                PermissionUser? currentUser = null;

                // context flag。
                bool inGroups = false;
                bool inUsers = false;

                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        switch (xr.Name)
                        {
                            case "Groups":
                                inGroups = true; inUsers = false;
                                break;

                            case "Users":
                                inUsers = true; inGroups = false;
                                break;

                            case "Group":
                                {
                                    // "Group" は次の意味を持ちうる:
                                    // - <Groups> 内では group definition。
                                    // - <Users> 内の <User> 配下では group membership。
                                    string name = xr.GetAttribute("name") ?? "";

                                    if (inGroups)
                                    {
                                        currentGroupDef = new PermissionGroup { Name = name };
                                        store.Groups[name] = currentGroupDef;
                                    }
                                    else if (inUsers && currentUser != null)
                                    {
                                        if (!string.IsNullOrWhiteSpace(name))
                                            currentUser.Groups.Add(name.Trim());
                                    }
                                    break;
                                }

                            case "User":
                                {
                                    string uuid = xr.GetAttribute("uuid") ?? "";
                                    currentUser = new PermissionUser { Uuid = uuid };
                                    store.Users[uuid] = currentUser;
                                    break;
                                }

                            case "Parent":
                                {
                                    if (currentGroupDef != null)
                                    {
                                        string parent = xr.GetAttribute("name") ?? "";
                                        if (!string.IsNullOrWhiteSpace(parent))
                                            currentGroupDef.Parents.Add(parent.Trim());
                                    }
                                    break;
                                }

                            case "Node":
                                {
                                    string node = xr.GetAttribute("value") ?? "";
                                    if (string.IsNullOrWhiteSpace(node))
                                        break;

                                    node = node.Trim();

                                    if (inGroups && currentGroupDef != null)
                                        currentGroupDef.Nodes.Add(node);
                                    else if (inUsers && currentUser != null)
                                        currentUser.Nodes.Add(node);

                                    break;
                                }
                        }
                    }
                    else if (xr.NodeType == XmlNodeType.EndElement)
                    {
                        switch (xr.Name)
                        {
                            case "Group":
                                // group definition context だけを clear する (user group membership ではない)。
                                if (inGroups)
                                    currentGroupDef = null;
                                break;

                            case "User":
                                currentUser = null;
                                break;

                            case "Groups":
                                inGroups = false;
                                currentGroupDef = null;
                                break;

                            case "Users":
                                inUsers = false;
                                currentUser = null;
                                break;
                        }
                    }
                }

                return store;
            }

            public static void Save(string path, PermissionStore store)
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    NewLineHandling = NewLineHandling.Entitize,
                    CloseOutput = true
                };

                using var fs = File.Create(path);
                using var xw = XmlWriter.Create(fs, settings);

                xw.WriteStartDocument();
                xw.WriteStartElement("Permissions");

                // groups。
                xw.WriteStartElement("Groups");
                foreach (var g in store.Groups.Values)
                {
                    xw.WriteStartElement("Group");
                    xw.WriteAttributeString("name", g.Name);

                    foreach (var p in g.Parents)
                    {
                        xw.WriteStartElement("Parent");
                        xw.WriteAttributeString("name", p);
                        xw.WriteEndElement();
                    }

                    foreach (var n in g.Nodes)
                    {
                        xw.WriteStartElement("Node");
                        xw.WriteAttributeString("value", n);
                        xw.WriteEndElement();
                    }

                    xw.WriteEndElement(); // Group
                }
                xw.WriteEndElement(); // Groups

                // users。
                xw.WriteStartElement("Users");
                foreach (var u in store.Users.Values)
                {
                    xw.WriteStartElement("User");
                    xw.WriteAttributeString("uuid", u.Uuid);

                    foreach (var g in u.Groups)
                    {
                        xw.WriteStartElement("Group");
                        xw.WriteAttributeString("name", g);
                        xw.WriteEndElement();
                    }

                    foreach (var n in u.Nodes)
                    {
                        xw.WriteStartElement("Node");
                        xw.WriteAttributeString("value", n);
                        xw.WriteEndElement();
                    }

                    xw.WriteEndElement(); // User
                }
                xw.WriteEndElement(); // Users

                xw.WriteEndElement(); // Permissions
                xw.WriteEndDocument();
            }
        }
        public static class PermissionIntegration
        {
            // global singleton-style instance。
            public static readonly PermissionManager Manager = new PermissionManager();

            // connect 時に保存する player ごとの metadata。permission change 時に ServerMetaDataMessage を rebuild するために使う。
            private static readonly ConcurrentDictionary<string, ClientMetaDataMessage> _playerMeta =
                new ConcurrentDictionary<string, ClientMetaDataMessage>(StringComparer.OrdinalIgnoreCase);

            // server startup 時に呼ぶ。
            public static void Init(string xmlPath)
            {
                Manager.SetXmlPath(xmlPath);

                // existing を load する。
                Manager.LoadFromXml();

                // file が empty / nonexistent の場合、optional default を適用する。
                Manager.EnsureDefaults();

                // save されていることを保証する。
                Manager.SaveToXmlDebounced();

                Manager.OnPermissionsChanged += HandlePermissionsChanged;
            }
            public static void InitWithoutDisc()
            {
                // file が empty / nonexistent の場合、optional default を適用する。
                Manager.EnsureDefaults();

                Manager.OnPermissionsChanged += HandlePermissionsChanged;
            }

            /// <summary>
            /// 後で ServerMetaDataMessage を rebuild できるよう、player 接続時に metadata を保存する。
            /// </summary>
            public static void StorePlayerMeta(string uuid, ClientMetaDataMessage meta)
            {
                _playerMeta[uuid] = meta;
            }

            /// <summary>
            /// player disconnect 時に保存済み metadata を削除する。
            /// </summary>
            public static void RemovePlayerMeta(string uuid)
            {
                _playerMeta.TryRemove(uuid, out _);
            }

            public static bool TryGetPlayerMeta(string uuid, out ClientMetaDataMessage meta)
            {
                return _playerMeta.TryGetValue(uuid, out meta);
            }

            public static void EvictPermissionCache(string uuid)
            {
                Manager.EvictUserCache(uuid);
            }

            public static bool HasValidRequirement(string uuid, string permNode)
            {
                bool hasPermission = Manager.Has(uuid, permNode);
                bool isAdmin = Manager.Has(uuid, PermNodes.All);

                return hasPermission || isAdmin;
            }
            public static bool HasValidRequirement(NetPeer peer, string permNode)
            {
                if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid))
                {
                    bool hasPermission = Manager.Has(uuid, permNode);
                    bool isAdmin = Manager.Has(uuid, PermNodes.All);
                    if (hasPermission || isAdmin)
                    {
                        return true;
                    }
                    else
                    {
                        BNL.LogError($"Permission not found for UUID: {uuid} for perm node {permNode}");
                        return false;
                    }
                }
                else
                {
                    BNL.LogError($"UUID not found for peer: {peer.Id} ");
                    return false;
                }
            }

            private static void HandlePermissionsChanged(string uuid)
            {
                if (uuid != null)
                {
                    SendPermissionUpdate(uuid);
                }
                else
                {
                    // group-level change: connected player 全員へ resend する。
                    foreach (var kvp in _playerMeta)
                    {
                        SendPermissionUpdate(kvp.Key);
                    }
                }
            }

            /// <summary>
            /// connected player に対し、現在の permission を含む ServerMetaDataMessage を rebuild して resend する。
            /// </summary>
            public static void SendPermissionUpdate(string uuid)
            {
                if (!_playerMeta.TryGetValue(uuid, out ClientMetaDataMessage meta))
                    return;

                if (!NetworkServer.AuthIdentity.UUIDToNetID(uuid, out int netId) ||
                    !NetworkServer.AuthenticatedPeers.TryGetValue(netId, out NetPeer peer))
                    return;

                Configuration config = NetworkServer.Configuration;
                ServerMetaDataMessage msg = new ServerMetaDataMessage
                {
                    ClientMetaDataMessage = meta,
                    SyncInterval = config.BSRSMillisecondDefaultInterval,
                    BaseMultiplier = config.BSRBaseMultiplier,
                    IncreaseRate = config.BSRSIncreaseRate,
                    SlowestSendRate = config.BSRSlowestSendRate,
                    PeerLimit = config.PeerLimit,
                };
                msg.SetPermissions(Manager.GetAllAllowedRules(uuid), Manager.GetAllDeniedRules(uuid));

                NetDataWriter writer = NetworkServer.RentWriter();
                msg.Serialize(writer);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.metaDataChannel, DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
