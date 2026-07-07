using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Common;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking.Sakiika
{
    public enum WorldSessionVisibility
    {
        /// <summary>Reachable only by people the host hands the connection string to.</summary>
        Invite,
        /// <summary>Additionally announced on Misskey so it shows up in everyone's world list.</summary>
        Public,
    }

    /// <summary>
    /// P2P (player-hosted) world sessions for SakiikaVR. Selecting a world in the
    /// Library and choosing Invite or Public spins up the in-process server
    /// (host mode), connects to it, loads the chosen world networked+persistent so
    /// late joiners receive it, and then either hands the host a connection string
    /// to share (Invite) or announces the session on Misskey (Public). There is no
    /// dedicated directory server; the public list lives in Misskey notes.
    /// </summary>
    public static class SakiikaWorldSession
    {
        /// <summary>Same store key the retired Servers panel used, so an already-configured port carries over.</summary>
        public const string HostPortFile = "HostPort.BAS";
        /// <summary>URL of the world hosted automatically (invite) on startup, empty = none.</summary>
        public const string HomeWorldUrlFile = "SakiikaHomeWorld.BAS";
        public const int DefaultPeerLimit = 32;

        private const int ConnectTimeoutSeconds = 30;

        /// <summary>Misskey note id of the live public announce, if any.</summary>
        public static string ActiveNoteId { get; private set; }
        public static string ActiveConnectionString { get; private set; }

        /// <summary>Live TURN relay for the current hosted session (symmetric NAT), if any.</summary>
        private static SakiikaTurnClient _activeTurn;

        private static bool _quitHookInstalled;
        private static bool _sessionStartInProgress;
        // True while we host our own in-process session. When set, opening another
        // world reuses the running server (swap the world + change visibility) instead
        // of restarting it — restarting raced the DID handshake and failed auth.
        private static bool _isHostingOwnSession;

        // ── Hosting ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a player-hosted session for the given world. Shows error dialogs
        /// itself; callers only need to close their UI beforehand.
        /// </summary>
        public static async Task StartWorldSessionAsync(BasisDataStoreItemKeys.ItemKey item, string worldDisplayName, WorldSessionVisibility visibility, bool showResultDialog = true)
        {
            if (_sessionStartInProgress)
            {
                BasisDebug.LogWarning("World session start already in progress; ignoring.");
                return;
            }

            // The display name is fixed to the Misskey account name, so every
            // session (invite or public) requires a login.
            if (!MisskeyService.IsLoggedIn || string.IsNullOrWhiteSpace(MisskeyService.Username))
            {
                ShowDialog(BasisLocalization.Get("sakiika.session.misskeyRequired.title"),
                    BasisLocalization.Get("sakiika.session.misskeyRequired.body"));
                return;
            }
            string userName = MisskeyService.Username;

            _sessionStartInProgress = true;
            try
            {
                // Already hosting our own session (e.g. the home world): reuse the
                // running server — swap the world and change visibility — rather than
                // tearing down and re-hosting (which raced the DID handshake).
                if (_isHostingOwnSession && BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    await ReuseSessionSwapWorldAsync(item, worldDisplayName, visibility, showResultDialog);
                    return;
                }

                // A previous public session's announce is stale the moment we host anew.
                await DeleteActiveNoteAsync();

                // If we're already in a session (e.g. the home world auto-hosted on
                // startup), fully tear it down and wait for the in-process server to
                // release its UDP port BEFORE hosting again. Re-hosting live races the
                // old server's socket teardown, which made the fresh DID handshake fail
                // with "was unable to authenticate!".
                await EnsureFullyDisconnectedAsync();

                // Resolve the public address up front so a Public session can fail fast
                // (and an Invite session can still fall back to a placeholder).
                Task<string> publicIpTask = GetPublicIpAsync();

                if (BasisRuntimeSpawnRegistry.CountWorldsAndProps() > 0)
                {
                    BasisDebug.Log("Clearing locally loaded worlds/props before hosting.", BasisDebug.LogTag.Networking);
                    await BasisRuntimeSpawnRegistry.RemoveAllWorldsAndProps();
                }

                ushort port = LoadHostPort();
                string password = GeneratePassword();

                BasisNetworkManagement.HostServerName = worldDisplayName ?? "さきいかVR World";
                BasisNetworkManagement.HostServerMotd = string.Empty;
                BasisNetworkManagement.HostPeerLimit = DefaultPeerLimit;
                BasisNetworkManagement.HostUseAuth = true;
                BasisNetworkManagement.HostEnableConsole = false;
                BasisNetworkManagement.HostAvatarsLocked = false;
                BasisNetworkManagement.HostPropsLocked = false;
                BasisNetworkManagement.HostWorldsLocked = true;
                BasisNetworkManagement.HostThirdPersonDisabled = false;

                ServerDirectoryEntry entry = CreateHostEntry(port, password);

                BasisMainMenu.Close();

                // Single host attempt. A retry here previously double-started the
                // in-process server on the same port, which split the DID handshake
                // across two servers and failed authentication. EnsureFullyDisconnected
                // above guarantees a clean single-server start.
                await BasisConnectionService.ConnectAsync(entry, userName, isHostMode: true);
                bool hostConnected = await WaitForLocalConnectionAsync(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
                if (!hostConnected)
                {
                    BasisConnectionService.ReportConnectionError(BasisLocalization.Get("sakiika.session.hostFailed"));
                    return;
                }

                // Persistent + networked so the in-process server replays the scene
                // load to everyone who joins later.
                await ContentLoader.LoadWorld(item, BundledContentHolder.NetworkType.Networked, persistent: true, admin: true);

                // ICE-lite: probe our NAT via STUN. A cone NAT gives a stable public
                // endpoint (srflx) that guests target directly — no port forwarding.
                // A symmetric NAT cannot be hole-punched, so fall back to a TURN relay:
                // allocate a public relayed address and publish THAT; guests connect to
                // it as a normal address while this side bridges the relay to the
                // in-process server.
                (bool symmetric, IPEndPoint srflx) = await SakiikaStun.DetectNatAsync(NetworkServer.Server);
                string publicIp = await publicIpTask;

                string hostForShare;
                ushort sharePort;
                bool usingRelay = false;
                if (symmetric)
                {
                    _activeTurn = new SakiikaTurnClient();
                    IPEndPoint relayed = await _activeTurn.StartAsync(port);
                    if (relayed != null)
                    {
                        hostForShare = relayed.Address.ToString();
                        sharePort = (ushort)relayed.Port;
                        usingRelay = true;
                    }
                    else
                    {
                        // TURN unavailable — degrade to a direct publish and hope the
                        // guest's NAT is permissive enough.
                        _activeTurn = null;
                        hostForShare = srflx != null ? srflx.Address.ToString() : (string.IsNullOrEmpty(publicIp) ? "<your-ip>" : publicIp);
                        sharePort = srflx != null ? (ushort)srflx.Port : port;
                    }
                }
                else if (srflx != null)
                {
                    hostForShare = srflx.Address.ToString();
                    sharePort = (ushort)srflx.Port;
                }
                else
                {
                    hostForShare = string.IsNullOrEmpty(publicIp) ? "<your-ip>" : publicIp;
                    sharePort = port;
                }
                string connectionString = $"{hostForShare}:{sharePort.ToString(CultureInfo.InvariantCulture)}#{password}";
                ActiveConnectionString = connectionString;
                // From here the in-process server is live; a subsequent world open
                // reuses it (swap the world) instead of restarting.
                _isHostingOwnSession = true;

                string noteFailureLine = string.Empty;
                if (visibility == WorldSessionVisibility.Public)
                {
                    if (srflx == null && string.IsNullOrEmpty(publicIp))
                    {
                        noteFailureLine = "\n\n" + BasisLocalization.Get("sakiika.session.noPublicIp");
                    }
                    else
                    {
                        WorldAnnouncePayload payload = new WorldAnnouncePayload
                        {
                            v = 1,
                            name = worldDisplayName,
                            conn = connectionString,
                            host = userName,
                        };
                        ActiveNoteId = await MisskeyService.CreateNoteAsync(MisskeyService.BuildAnnounceNoteText(payload));
                        if (string.IsNullOrEmpty(ActiveNoteId))
                        {
                            noteFailureLine = "\n\n" + BasisLocalization.Get("sakiika.session.announceFailed");
                        }
                        else
                        {
                            // Poll the note's replies for guests. In relay mode we permit
                            // each guest on the TURN allocation; otherwise we punch its NAT.
                            SakiikaHolePunch.StartHost(ActiveNoteId, usingRelay ? _activeTurn : null);
                        }
                        InstallQuitHook();
                    }
                }

                if (showResultDialog)
                {
                    GUIUtility.systemCopyBuffer = connectionString;

                    string title = visibility == WorldSessionVisibility.Public
                        ? BasisLocalization.Get("sakiika.session.startedPublic.title")
                        : BasisLocalization.Get("sakiika.session.startedInvite.title");
                    string body = string.Format(BasisLocalization.Get("sakiika.session.started.body"),
                            worldDisplayName, connectionString)
                        + noteFailureLine;
                    ShowDialog(title, body);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
            finally
            {
                _sessionStartInProgress = false;
            }
        }

        /// <summary>
        /// Reuse path: we already host our own session, so swap the loaded world and
        /// change visibility WITHOUT restarting the server. Players stay connected and
        /// are moved to the new world together (the persistent networked load replays
        /// to everyone). Avoids the DID re-auth race of a server restart.
        /// </summary>
        private static async Task ReuseSessionSwapWorldAsync(BasisDataStoreItemKeys.ItemKey item, string worldDisplayName, WorldSessionVisibility visibility, bool showResultDialog)
        {
            BasisDebug.Log($"Reusing host session; swapping world to '{worldDisplayName}' visibility={visibility}", BasisDebug.LogTag.Networking);

            if (visibility == WorldSessionVisibility.Public && !MisskeyService.IsLoggedIn)
            {
                ShowDialog(BasisLocalization.Get("sakiika.session.misskeyRequired.title"),
                    BasisLocalization.Get("sakiika.session.misskeyRequired.body"));
                return;
            }

            BasisMainMenu.Close();

            // Swap the world for everyone. Networked + persistent so the server replays
            // the scene change to all connected clients (nobody is disconnected).
            if (BasisRuntimeSpawnRegistry.CountWorldsAndProps() > 0)
            {
                await BasisRuntimeSpawnRegistry.RemoveAllWorldsAndProps();
            }
            await ContentLoader.LoadWorld(item, BundledContentHolder.NetworkType.Networked, persistent: true, admin: true);

            string conn = ActiveConnectionString ?? string.Empty;

            if (visibility == WorldSessionVisibility.Public)
            {
                // Publish an announce if we aren't already public. If already public,
                // the note stays and the world simply changed underneath it.
                if (string.IsNullOrEmpty(ActiveNoteId) && !string.IsNullOrEmpty(conn))
                {
                    WorldAnnouncePayload payload = new WorldAnnouncePayload
                    {
                        v = 1,
                        name = worldDisplayName,
                        conn = conn,
                        host = MisskeyService.Username,
                    };
                    ActiveNoteId = await MisskeyService.CreateNoteAsync(MisskeyService.BuildAnnounceNoteText(payload));
                    if (!string.IsNullOrEmpty(ActiveNoteId))
                    {
                        SakiikaHolePunch.StartHost(ActiveNoteId, _activeTurn);
                        InstallQuitHook();
                    }
                }
            }
            else
            {
                // Going back to invite-only: remove the public announce and stop the
                // signal poll, but keep the session (and any TURN relay) running.
                string note = ActiveNoteId;
                ActiveNoteId = null;
                SakiikaHolePunch.StopHost();
                if (!string.IsNullOrEmpty(note)) await MisskeyService.DeleteNoteAsync(note);
            }

            if (showResultDialog)
            {
                if (!string.IsNullOrEmpty(conn)) GUIUtility.systemCopyBuffer = conn;
                string title = visibility == WorldSessionVisibility.Public
                    ? BasisLocalization.Get("sakiika.session.startedPublic.title")
                    : BasisLocalization.Get("sakiika.session.startedInvite.title");
                string body = string.Format(BasisLocalization.Get("sakiika.session.started.body"),
                    worldDisplayName, conn);
                ShowDialog(title, body);
            }
        }

        // ── Home world ───────────────────────────────────────────────────────

        public static bool IsHomeWorld(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null || string.IsNullOrEmpty(item.Url)) return false;
            return string.Equals(BasisDataStore.LoadString(HomeWorldUrlFile, string.Empty), item.Url, StringComparison.OrdinalIgnoreCase);
        }

        public static void ToggleHomeWorld(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null) return;
            BasisDataStore.SaveString(IsHomeWorld(item) ? string.Empty : item.Url ?? string.Empty, HomeWorldUrlFile);
        }

        private static bool _homeWorldAutoHostAttempted;

        /// <summary>
        /// If a home world is configured, host it as an invite session once the
        /// network layer is up. A --connection/deep-link bootstrap wins over this.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterHomeWorldAutoHost()
        {
            void Trigger()
            {
                if (_homeWorldAutoHostAttempted) return;
                _homeWorldAutoHostAttempted = true;
                _ = AutoHostHomeWorldAsync();
            }
            if (BasisNetworkManagement.IsInitialized) Trigger();
            else BasisNetworkManagement.OnIstanceCreated += Trigger;
        }

        private static async Task AutoHostHomeWorldAsync()
        {
            string homeUrl = BasisDataStore.LoadString(HomeWorldUrlFile, string.Empty);
            if (string.IsNullOrEmpty(homeUrl)) return;

            // Give the CLI/deep-link bootstrap a moment to claim the session first.
            await Task.Delay(2000);
            if (BasisConnectionService.AutoConnectAttempted || BasisNetworkConnection.LocalPlayerIsConnected) return;

            if (!MisskeyService.IsLoggedIn)
            {
                BasisDebug.Log("Home world is set but no Misskey login is present; skipping auto-host.", BasisDebug.LogTag.Networking);
                return;
            }

            await BasisDataStoreItemKeys.LoadKeys();
            BasisDataStoreItemKeys.ItemKey item = null;
            foreach (BasisDataStoreItemKeys.ItemKey key in BasisDataStoreItemKeys.DisplayKeys())
            {
                if (key != null && key.Mode == BundledContentHolder.Mode.World
                    && string.Equals(key.Url, homeUrl, StringComparison.OrdinalIgnoreCase))
                {
                    item = key;
                    break;
                }
            }
            if (item == null)
            {
                BasisDebug.LogWarning($"Home world '{homeUrl}' is no longer in the library; skipping auto-host.");
                return;
            }

            // Metadata preloading runs during boot — wait for this world's meta.
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (!CachedMetaData.TryGetMeta(item.Url, out _) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
            if (!CachedMetaData.TryGetMeta(item.Url, out CachedMetaData.CachedContent meta))
            {
                BasisDebug.LogWarning($"Home world '{homeUrl}' metadata never became available; skipping auto-host.");
                return;
            }

            string name = meta.BasisBundleConnector?.BasisBundleDescription?.AssetBundleName;
            if (string.IsNullOrEmpty(name)) name = homeUrl;

            BasisDebug.Log($"Auto-hosting home world '{name}' as an invite session.", BasisDebug.LogTag.Networking);
            await StartWorldSessionAsync(item, name, WorldSessionVisibility.Invite, showResultDialog: false);
        }

        // ── Joining ──────────────────────────────────────────────────────────

        /// <summary>
        /// Joins a session from an address:port#password connection string (as found
        /// in Misskey announces or shared invites). Shows error dialogs itself.
        /// </summary>
        public static async Task JoinFromConnectionStringAsync(string connectionString)
        {
            // Display name comes from the Misskey account — no login, no join.
            if (!MisskeyService.IsLoggedIn || string.IsNullOrWhiteSpace(MisskeyService.Username))
            {
                ShowDialog(BasisLocalization.Get("sakiika.session.misskeyRequired.title"),
                    BasisLocalization.Get("sakiika.session.misskeyRequired.body"));
                return;
            }
            string userName = MisskeyService.Username;

            if (!TryBuildEntry(connectionString, out ServerDirectoryEntry entry))
            {
                ShowDialog(BasisLocalization.Get("sakiika.session.joinFailed.title"),
                    string.Format(BasisLocalization.Get("sakiika.session.badConnection.body"), connectionString ?? string.Empty));
                return;
            }

            // Leaving a hosted public session — its announce is no longer valid.
            await DeleteActiveNoteAsync();
            _isHostingOwnSession = false;
            ActiveConnectionString = null;

            BasisMainMenu.Close();
            await BasisConnectionService.ConnectAsync(entry, userName);
        }

        /// <summary>
        /// Joins a public world discovered on Misskey. Runs the ICE-lite guest half in
        /// parallel with the connect: discover our own public endpoint via STUN, reply
        /// to the announce note with it, and punch the host so cone NATs open both ways.
        /// </summary>
        public static async Task JoinPublicWorldAsync(WorldAnnouncePayload payload, string noteId)
        {
            if (payload == null) return;
            if (!MisskeyService.IsLoggedIn || string.IsNullOrWhiteSpace(MisskeyService.Username))
            {
                ShowDialog(BasisLocalization.Get("sakiika.session.misskeyRequired.title"),
                    BasisLocalization.Get("sakiika.session.misskeyRequired.body"));
                return;
            }
            string userName = MisskeyService.Username;

            if (!TryBuildEntry(payload.conn, out ServerDirectoryEntry entry))
            {
                ShowDialog(BasisLocalization.Get("sakiika.session.joinFailed.title"),
                    string.Format(BasisLocalization.Get("sakiika.session.badConnection.body"), payload.conn ?? string.Empty));
                return;
            }

            await DeleteActiveNoteAsync();
            _isHostingOwnSession = false;
            ActiveConnectionString = null;

            IPEndPoint hostEndpoint = ParseEndpoint(payload.conn);

            BasisMainMenu.Close();
            // ConnectAsync starts the client socket and returns; the hole-punch runs
            // alongside LiteNetLib's connect retries.
            await BasisConnectionService.ConnectAsync(entry, userName);

            _ = RunGuestHolePunchAsync(noteId, hostEndpoint);
        }

        private static async Task RunGuestHolePunchAsync(string noteId, IPEndPoint hostEndpoint)
        {
            try
            {
                if (hostEndpoint == null) return;

                // Wait for the client socket to exist before STUN can use it.
                DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                while (BasisNetworkConnection.NetworkClient?.client == null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }
                NetManager client = BasisNetworkConnection.NetworkClient?.client;
                if (client == null) return;

                IPEndPoint guestPublic = await SakiikaStun.DiscoverAsync(client);
                await SakiikaHolePunch.GuestSignalAndPunchAsync(noteId, hostEndpoint, guestPublic);
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Guest hole-punch failed: {ex.Message}");
            }
        }

        private static IPEndPoint ParseEndpoint(string connectionString)
        {
            if (!LNLConnectionTargetParser.TryParseConnectionString((connectionString ?? string.Empty).Trim(),
                    out string address, out ushort port, out _, out _))
                return null;
            if (IPAddress.TryParse(address, out IPAddress ip)) return new IPEndPoint(ip, port);
            try
            {
                IPAddress[] addrs = System.Net.Dns.GetHostAddresses(address);
                foreach (IPAddress a in addrs)
                {
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return new IPEndPoint(a, port);
                }
            }
            catch { }
            return null;
        }

        public static bool TryBuildEntry(string connectionString, out ServerDirectoryEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(connectionString)) return false;
            if (!LNLConnectionTargetParser.TryParseConnectionString(connectionString.Trim(), out string address, out ushort port, out _, out string password))
                return false;
            if (string.IsNullOrEmpty(address)) return false;

            ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, $"{address}:{port}");
            target.Set(ConnectionTarget.Keys.Address, address);
            target.Set(ConnectionTarget.Keys.Port, port.ToString(CultureInfo.InvariantCulture));
            target.Set(ConnectionTarget.Keys.Password, password ?? string.Empty);

            entry = new ServerDirectoryEntry
            {
                Id = "__sakiika__",
                SourceId = SavedServersDirectorySource.Id,
                DisplayName = string.Empty,
                Target = target,
                Password = password ?? string.Empty,
                HasPassword = !string.IsNullOrEmpty(password),
                CanEdit = false,
                CanRemove = false,
            };
            return true;
        }

        // ── Internals ────────────────────────────────────────────────────────

        private static ServerDirectoryEntry CreateHostEntry(ushort port, string password)
        {
            ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, $"localhost:{port}");
            target.Set(ConnectionTarget.Keys.Address, "localhost");
            target.Set(ConnectionTarget.Keys.Port, port.ToString(CultureInfo.InvariantCulture));
            target.Set(ConnectionTarget.Keys.Password, password);
            return new ServerDirectoryEntry
            {
                Id = "__sakiika_host__",
                SourceId = SavedServersDirectorySource.Id,
                DisplayName = string.Empty,
                Target = target,
                Password = password,
                HasPassword = true,
                CanEdit = false,
                CanRemove = false,
            };
        }

        private static ushort LoadHostPort()
        {
            int stored = BasisDataStore.LoadInt(HostPortFile, SavedServersDirectorySource.DefaultServerPort);
            return (stored > 0 && stored <= ushort.MaxValue) ? (ushort)stored : SavedServersDirectorySource.DefaultServerPort;
        }

        private static string GeneratePassword()
        {
            // Connection-string friendly: no ':' or '#', URL-safe.
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>
        /// Fully leaves any current session and waits until the in-process host server
        /// has stopped listening and released its socket, so a subsequent host starts
        /// from a clean single-server state (avoids the DID re-auth race).
        /// </summary>
        private static async Task EnsureFullyDisconnectedAsync()
        {
            // We are leaving/replacing the current session, so we no longer host it.
            _isHostingOwnSession = false;
            if (!BasisNetworkConnection.LocalPlayerIsConnected
                && !NetworkServer.IsListening
                && BasisNetworkConnection.BasisNetworkServerRunner == null)
            {
                return;
            }

            BasisDebug.Log("Leaving current session before re-hosting.", BasisDebug.LogTag.Networking);
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                Task rebootWait = BasisNetworkConnection.WaitForRebootCompleteAsync(cts.Token);
                await BasisNetworkLifeCycle.Destroy();
                try { await rebootWait; } catch (OperationCanceledException) { }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Teardown before re-host hit an error: {ex.Message}");
            }

            // Disconnecting the old client queues a HandleDisconnection → RebootManagement
            // on the main thread; if it fires AFTER we start the new server it stops that
            // server (splitting the DID handshake). So drain those handlers here: wait for
            // a STABLE disconnected state (no server runner, not listening, not connected)
            // that holds continuously before we re-initialize and re-host.
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            int stableChecks = 0;
            while (DateTime.UtcNow < deadline)
            {
                bool quiet = !NetworkServer.IsListening
                    && BasisNetworkConnection.BasisNetworkServerRunner == null
                    && !BasisNetworkConnection.LocalPlayerIsConnected;
                stableChecks = quiet ? stableChecks + 1 : 0;
                if (stableChecks >= 6) break; // ~900ms of continuous quiet
                await Task.Delay(150);
            }

            // Re-create the network management instance for the fresh host.
            if (!BasisNetworkManagement.IsInitialized)
            {
                BasisNetworkLifeCycle.Initialize();
            }
            await Task.Delay(400);
        }

        private static async Task<bool> WaitForLocalConnectionAsync(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (BasisNetworkConnection.LocalPlayerIsConnected) return true;
                await Task.Delay(250);
            }
            return BasisNetworkConnection.LocalPlayerIsConnected;
        }

        /// <summary>
        /// Resolves this machine's public IPv4 via well-known echo services. Returns
        /// null when none are reachable (offline, or all services blocked).
        /// </summary>
        public static async Task<string> GetPublicIpAsync()
        {
            string[] services =
            {
                "https://api.ipify.org",
                "https://checkip.amazonaws.com",
                "https://ifconfig.me/ip",
            };
            foreach (string service in services)
            {
                string body = await MisskeyService.GetTextAsync(service);
                if (!string.IsNullOrEmpty(body) && IPAddress.TryParse(body, out IPAddress parsed))
                {
                    return parsed.ToString();
                }
            }
            return null;
        }

        /// <summary>Deletes the live public announce note, if one exists. Best-effort.</summary>
        public static async Task DeleteActiveNoteAsync()
        {
            SakiikaHolePunch.StopHost();
            if (_activeTurn != null)
            {
                _activeTurn.Stop();
                _activeTurn = null;
            }
            string noteId = ActiveNoteId;
            ActiveNoteId = null;
            if (string.IsNullOrEmpty(noteId)) return;
            await MisskeyService.DeleteNoteAsync(noteId);
        }

        private static void InstallQuitHook()
        {
            if (_quitHookInstalled) return;
            _quitHookInstalled = true;
            Application.quitting += () =>
            {
                // Best-effort: the request may not finish before the process dies;
                // stale announces are also filtered by age on the reader side.
                _ = DeleteActiveNoteAsync();
            };
        }

        private static void ShowDialog(string title, string body)
        {
            BasisMainMenu.Open();
            if (BasisMainMenu.Instance == null)
            {
                BasisDebug.LogWarning($"{title}: {body}");
                return;
            }
            if (BasisMainMenu.Instance.Dialogue != null)
            {
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }
            BasisMainMenu.Instance.OpenDialogue(title, body, BasisLocalization.Get("ui.ok"), _ => { });
        }
    }
}
