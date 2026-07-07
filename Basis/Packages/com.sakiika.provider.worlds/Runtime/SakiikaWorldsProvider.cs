using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Sakiika;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Main-menu Worlds panel for SakiikaVR. Replaces the retired Servers panel:
    /// instead of a hand-maintained server list, it shows public P2P world sessions
    /// announced on Misskey (searched by hashtag — no directory server involved)
    /// and hosts the Misskey login (MiAuth) used to publish your own sessions from
    /// the Library's World tab.
    /// </summary>
    public class SakiikaWorldsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SakiikaWorldsProvider());
        }

        public const string TitleKey = "menu.provider.publicWorlds";
        public override string Title => BasisLocalization.Get(TitleKey);
        public override string IconAddress => AddressableAssets.Sprites.World;
        public override int Order => 30;
        public override bool Hidden => false;

        private BasisMenuPanel _panel;
        private RectTransform _listContainer;
        private PanelElementDescriptor _emptyState;
        private PanelButton _loginHeaderButton;
        private PanelButton _refreshButton;
        private PanelSectionToggle _misskeyToggle;
        private PanelElementDescriptor _accountStatus;
        private PanelTextField _instanceField;
        private PanelButton _loginButton;
        private PanelButton _logoutButton;
        private readonly List<PanelElementDescriptor> _rows = new List<PanelElementDescriptor>();
        private bool _refreshing;
        private bool _loginInProgress;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            _panel = panel;
            panel.OnInstanceReleased += OnPanelClosed;

            RectTransform container = panel.Descriptor.ContentParent;
            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            container = scroll.ContentParent;

            BuildHeader(container);

            _listContainer = container;
            _emptyState = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            _emptyState.SetTitle(BasisLocalization.Get("sakiika.worlds.empty"));
            _emptyState.SetDescription(string.Empty);

            BuildMisskeySection(container);

            _ = RefreshWorldsAsync();
        }

        private void OnPanelClosed()
        {
            _rows.Clear();
            _panel = null;
            _listContainer = null;
            _emptyState = null;
            _accountStatus = null;
            _misskeyToggle = null;
            _loginHeaderButton = null;
        }

        // ── Header: login + refresh ──────────────────────────────────────────
        // The in-game display name is fixed to the Misskey account name, so
        // instead of a username field the header carries the login entry point.

        private void BuildHeader(RectTransform container)
        {
            RectTransform actions = BuildActionRow(container);

            _loginHeaderButton = PanelButton.CreateNew(actions);
            UpdateLoginHeaderButton();
            _loginHeaderButton.OnClicked += () =>
            {
                if (!MisskeyService.IsLoggedIn) _ = LoginAsync();
            };

            _refreshButton = PanelButton.CreateNew(actions);
            _refreshButton.Descriptor.SetTitle(BasisLocalization.Get("sakiika.worlds.refresh"));
            _refreshButton.OnClicked += () => _ = RefreshWorldsAsync();
        }

        private void UpdateLoginHeaderButton()
        {
            if (_loginHeaderButton == null) return;
            _loginHeaderButton.Descriptor.SetTitle(MisskeyService.IsLoggedIn
                ? string.Format(BasisLocalization.Get("sakiika.worlds.loggedInAs"), MisskeyService.Username, MisskeyService.InstanceUrl)
                : BasisLocalization.Get("sakiika.worlds.login"));
        }

        // ── Public world list ────────────────────────────────────────────────

        private async Task RefreshWorldsAsync()
        {
            if (_refreshing || _panel == null) return;
            _refreshing = true;
            try
            {
                ClearRows();
                _emptyState.SetTitle(BasisLocalization.Get("sakiika.worlds.querying"));
                _emptyState.SetActive(true);

                MisskeyNote[] notes = await MisskeyService.SearchWorldNotesAsync(50);
                if (_panel == null) return;

                // One row per connection string, newest announce wins; stale
                // announces (host crashed without deleting the note) age out.
                Dictionary<string, (WorldAnnouncePayload payload, MisskeyNote note, DateTime created)> latest =
                    new Dictionary<string, (WorldAnnouncePayload, MisskeyNote, DateTime)>(StringComparer.OrdinalIgnoreCase);
                foreach (MisskeyNote note in notes)
                {
                    if (note == null || !MisskeyService.TryDecodeWorldPayload(note.text, out WorldAnnouncePayload payload)) continue;
                    DateTime created = ParseCreatedAt(note.createdAt);
                    if (DateTime.UtcNow - created > MisskeyService.MaxAnnounceAge) continue;
                    if (!latest.TryGetValue(payload.conn, out var existing) || created > existing.created)
                    {
                        latest[payload.conn] = (payload, note, created);
                    }
                }

                List<(WorldAnnouncePayload payload, MisskeyNote note, DateTime created)> ordered =
                    latest.Values.OrderByDescending(entry => entry.created).ToList();
                foreach ((WorldAnnouncePayload payload, MisskeyNote note, DateTime created) in ordered)
                {
                    AddWorldRow(payload, note, created);
                }

                _emptyState.SetTitle(BasisLocalization.Get("sakiika.worlds.empty"));
                _emptyState.SetActive(ordered.Count == 0);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex.ToString());
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void AddWorldRow(WorldAnnouncePayload payload, MisskeyNote note, DateTime createdUtc)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, _listContainer);
            group.transform.SetSiblingIndex(_misskeyToggle.transform.GetSiblingIndex());

            group.SetTitle(string.IsNullOrEmpty(payload.name)
                ? BasisLocalization.Get("sakiika.worlds.unnamed")
                : payload.name);

            string hostLabel = !string.IsNullOrEmpty(payload.host) ? payload.host : note.user?.username ?? string.Empty;
            string poster = FormatPoster(note.user);
            group.SetDescription(string.Format(BasisLocalization.Get("sakiika.worlds.row.description"),
                hostLabel, poster, FormatTimeAgo(createdUtc)));

            RectTransform actions = BuildActionRow(group.ContentParent);
            PanelButton joinButton = PanelButton.CreateNew(actions);
            joinButton.Descriptor.SetTitle(BasisLocalization.Get("sakiika.worlds.join"));
            string joinNoteId = note.id;
            joinButton.OnClicked += () => _ = SakiikaWorldSession.JoinPublicWorldAsync(payload, joinNoteId);

            _rows.Add(group);
        }

        private void ClearRows()
        {
            foreach (PanelElementDescriptor row in _rows)
            {
                if (row != null && row.gameObject != null)
                {
                    UnityEngine.Object.Destroy(row.gameObject);
                }
            }
            _rows.Clear();
        }

        private static string FormatPoster(MisskeyUser user)
        {
            if (user == null || string.IsNullOrEmpty(user.username)) return string.Empty;
            return string.IsNullOrEmpty(user.host) ? $"@{user.username}" : $"@{user.username}@{user.host}";
        }

        private static DateTime ParseCreatedAt(string createdAt)
        {
            if (DateTime.TryParse(createdAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            {
                return parsed;
            }
            return DateTime.UtcNow;
        }

        private static string FormatTimeAgo(DateTime createdUtc)
        {
            TimeSpan age = DateTime.UtcNow - createdUtc;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalMinutes < 60)
            {
                return string.Format(BasisLocalization.Get("sakiika.worlds.minutesAgo"), (int)age.TotalMinutes);
            }
            return string.Format(BasisLocalization.Get("sakiika.worlds.hoursAgo"), (int)age.TotalHours);
        }

        // ── Misskey account section ──────────────────────────────────────────

        private void BuildMisskeySection(RectTransform container)
        {
            _misskeyToggle = PanelSectionToggle.CreateNewEntry(container);
            _misskeyToggle.SetTitle(BasisLocalization.Get("sakiika.worlds.misskey"));
            int sectionStart = container.childCount;

            _accountStatus = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            _accountStatus.SetDescription(BasisLocalization.Get("sakiika.worlds.misskey.description"));
            UpdateAccountStatus();

            _instanceField = PanelTextField.CreateNewEntry(container);
            _instanceField.Descriptor.SetTitle(BasisLocalization.Get("sakiika.worlds.instance"));
            _instanceField.SetValueWithoutNotify(MisskeyService.InstanceUrl);
            _instanceField.OnValueChanged = value =>
            {
                if (!string.IsNullOrWhiteSpace(value)) MisskeyService.InstanceUrl = value;
            };

            RectTransform accountActions = BuildActionRow(container);

            _loginButton = PanelButton.CreateNew(accountActions);
            _loginButton.Descriptor.SetTitle(BasisLocalization.Get("sakiika.worlds.login"));
            _loginButton.OnClicked += () => _ = LoginAsync();

            _logoutButton = PanelButton.CreateNew(accountActions);
            _logoutButton.Descriptor.SetTitle(BasisLocalization.Get("sakiika.worlds.logout"));
            _logoutButton.OnClicked += () =>
            {
                MisskeyService.Logout();
                UpdateAccountStatus();
                UpdateLoginHeaderButton();
            };

            PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(_misskeyToggle, container, sectionStart, false, null);
        }

        private async Task LoginAsync()
        {
            if (_loginInProgress) return;
            _loginInProgress = true;
            try
            {
                if (_instanceField != null && !string.IsNullOrWhiteSpace(_instanceField.Value))
                {
                    MisskeyService.InstanceUrl = _instanceField.Value;
                }

                SetAccountStatus(BasisLocalization.Get("sakiika.worlds.loginWaiting"));
                string session = MisskeyService.BeginMiAuth();
                bool ok = await MisskeyService.PollMiAuthAsync(session);
                if (_panel == null) return;

                if (ok)
                {
                    UpdateAccountStatus();
                    UpdateLoginHeaderButton();
                    _ = RefreshWorldsAsync();
                }
                else
                {
                    SetAccountStatus(BasisLocalization.Get("sakiika.worlds.loginFailed"));
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex.ToString());
            }
            finally
            {
                _loginInProgress = false;
            }
        }

        private void UpdateAccountStatus()
        {
            SetAccountStatus(MisskeyService.IsLoggedIn
                ? string.Format(BasisLocalization.Get("sakiika.worlds.loggedInAs"), MisskeyService.Username, MisskeyService.InstanceUrl)
                : BasisLocalization.Get("sakiika.worlds.notLoggedIn"));
        }

        private void SetAccountStatus(string status)
        {
            _accountStatus?.SetTitle(status);
        }

        /// <summary>
        /// Inline horizontal action row — same pattern the retired ServersProvider
        /// used for its per-row button strips.
        /// </summary>
        private static RectTransform BuildActionRow(RectTransform parent)
        {
            GameObject rowGO = new GameObject("WorldRowActions", typeof(RectTransform));
            RectTransform rowRect = (RectTransform)rowGO.transform;
            rowRect.SetParent(parent, false);

            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);

            HorizontalLayoutGroup hlg = rowGO.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.spacing = 8f;
            hlg.padding = new RectOffset(8, 8, 4, 8);

            ContentSizeFitter fitter = rowGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layout = rowGO.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;

            return rowRect;
        }
    }
}
