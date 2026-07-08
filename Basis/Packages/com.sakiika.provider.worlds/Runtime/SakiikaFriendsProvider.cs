using Basis.Scripts.Networking.Sakiika;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;

namespace Basis.BasisUI
{
    /// <summary>
    /// Main-menu Friends panel: Misskey mutual follows shown as a world-tab-style
    /// grid of avatar cards, with the members currently online in VR (presence
    /// notes, see <see cref="SakiikaPresence"/>) in their own section on top.
    /// Online friends can be invited to the current session via a Misskey DM
    /// (<see cref="SakiikaInvites"/>). Sits in the same provider row as the
    /// Personal Mirror / Photo Camera buttons.
    /// </summary>
    public class SakiikaFriendsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SakiikaFriendsProvider());
        }

        public const string TitleKey = "menu.provider.friends";
        public override string Title => BasisLocalization.Get(TitleKey);
        public override string IconAddress => AddressableAssets.Sprites.People;
        public override int Order => 31; // right after the Worlds panel (30)
        public override bool Hidden => false;

        private BasisMenuPanel _panel;
        private RectTransform _container;
        private bool _refreshing;

        private static string _selfUserId;
        private static string _selfUserIdForUsername;
        // Keyed by avatarUrl; session-lifetime cache so panel refreshes don't re-download.
        private static readonly Dictionary<string, Sprite> _avatarCache = new Dictionary<string, Sprite>(StringComparer.Ordinal);

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
            panel.OnInstanceReleased += () => { _panel = null; _container = null; };

            RectTransform container = panel.Descriptor.ContentParent;
            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            _container = scroll.ContentParent;

            _ = RefreshAsync();
        }

        // ── Panel body ───────────────────────────────────────────────────────

        private async Task RefreshAsync()
        {
            if (_refreshing || _panel == null) return;
            _refreshing = true;
            try
            {
                RectTransform container = _container;
                ClearChildren(container);

                BuildHeader(container);

                if (!MisskeyService.IsLoggedIn)
                {
                    PanelElementDescriptor prompt = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                    prompt.SetTitle(BasisLocalization.Get("friends.notLoggedIn.title"));
                    prompt.SetDescription(BasisLocalization.Get("friends.notLoggedIn.body"));
                    return;
                }

                PanelElementDescriptor loading = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                loading.SetTitle(BasisLocalization.Get("friends.loading"));

                List<MisskeyUser> mutuals = await FetchMutualFollowsAsync();
                if (_panel == null) return;

                mutuals = mutuals.OrderBy(u => string.IsNullOrEmpty(u.name) ? u.username : u.name,
                    StringComparer.InvariantCultureIgnoreCase).ToList();

                HashSet<string> onlineIds = await SakiikaPresence.QueryOnlineUserIdsAsync(mutuals);
                if (_panel == null) return;

                container = _container;
                ClearChildren(container);
                BuildHeader(container);

                // ── Online in VR (above the full list) ──
                List<MisskeyUser> online = mutuals.Where(u => onlineIds.Contains(u.id)).ToList();
                PanelElementDescriptor onlineHeader = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                onlineHeader.SetTitle($"{BasisLocalization.Get("friends.section.online")} ({online.Count})");
                if (online.Count == 0)
                {
                    onlineHeader.SetDescription(BasisLocalization.Get("friends.empty.online"));
                }
                else
                {
                    RectTransform onlineGrid = CreateFriendsGrid(container);
                    foreach (MisskeyUser user in online) CreateFriendCard(user, true, onlineGrid);
                }

                // ── All mutual follows ──
                PanelElementDescriptor mutualHeader = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                mutualHeader.SetTitle($"{BasisLocalization.Get("friends.section.mutuals")} ({mutuals.Count})");
                if (mutuals.Count == 0)
                {
                    mutualHeader.SetDescription(BasisLocalization.Get("friends.empty.mutuals"));
                }
                else
                {
                    RectTransform mutualGrid = CreateFriendsGrid(container);
                    foreach (MisskeyUser user in mutuals) CreateFriendCard(user, onlineIds.Contains(user.id), mutualGrid);
                }
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

        private void BuildHeader(RectTransform container)
        {
            PanelButton refresh = PanelButton.CreateNew(ButtonStyles.StandardButton, container);
            refresh.Descriptor.SetTitle(BasisLocalization.Get("sakiika.worlds.refresh"));
            refresh.Descriptor.SetHeight(60);
            refresh.OnClicked += () => _ = RefreshAsync();
        }

        private static void ClearChildren(RectTransform container)
        {
            if (container == null) return;
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                if (child != null && child.gameObject != null)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }

        // ── Data ─────────────────────────────────────────────────────────────

        /// <summary>Mutual follows = intersection of the own following and follower lists.</summary>
        private static async Task<List<MisskeyUser>> FetchMutualFollowsAsync()
        {
            string username = MisskeyService.Username;
            if (_selfUserId == null || _selfUserIdForUsername != username)
            {
                MisskeyUser self = await MisskeyService.GetUserByUsernameAsync(username);
                if (self == null) return new List<MisskeyUser>();
                _selfUserId = self.id;
                _selfUserIdForUsername = username;
            }

            Task<List<MisskeyUser>> followingTask = MisskeyService.GetFollowingUsersAsync(_selfUserId);
            Task<List<MisskeyUser>> followersTask = MisskeyService.GetFollowerUsersAsync(_selfUserId);
            await Task.WhenAll(followingTask, followersTask);

            HashSet<string> followerIds = new HashSet<string>(
                followersTask.Result.Select(u => u.id), StringComparer.Ordinal);
            // Keep the following-list user objects (they carry avatarUrl too);
            // dedupe defensively in case the API pages overlap.
            return followingTask.Result
                .Where(u => followerIds.Contains(u.id))
                .GroupBy(u => u.id, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
        }

        // ── Cards ────────────────────────────────────────────────────────────

        /// <summary>
        /// Plain GridLayoutGroup matching the library grid prefab's cell metrics,
        /// so friend cards line up exactly like the world tab's grid but can sit
        /// as a section inside this vertical page.
        /// </summary>
        private static RectTransform CreateFriendsGrid(RectTransform parent)
        {
            GameObject go = new GameObject("FriendsGrid", typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);

            GridLayoutGroup grid = go.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(200, 250);
            grid.spacing = new Vector2(10, 15);
            grid.padding = new RectOffset(10, 10, 10, 10);
            grid.childAlignment = TextAnchor.UpperLeft;

            ContentSizeFitter fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layout = go.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;

            return rect;
        }

        private void CreateFriendCard(MisskeyUser user, bool online, RectTransform container)
        {
            PanelButton buttonPanel = PanelButton.CreateNew(ButtonStyles.Prop, container);
            buttonPanel.ButtonStyling.ShowIndicator(online);

            var desc = buttonPanel.Descriptor;
            desc.SetTitle(string.IsNullOrEmpty(user.name) ? user.username : user.name);
            string handle = string.IsNullOrEmpty(user.host) ? $"@{user.username}" : $"@{user.username}@{user.host}";
            desc.SetDescription(online ? $"🟢 {handle}" : handle);

            buttonPanel.SetIcon(AddressableAssets.Sprites.Avatars);
            _ = ApplyAvatarAsync(buttonPanel, user.avatarUrl);
            desc.ForceRebuild();

            buttonPanel.OnClicked += () => ShowFriendDialog(user, online);
        }

        private static async Task ApplyAvatarAsync(PanelButton buttonPanel, string avatarUrl)
        {
            if (string.IsNullOrEmpty(avatarUrl)) return;

            if (!_avatarCache.TryGetValue(avatarUrl, out Sprite sprite))
            {
                sprite = await DownloadSpriteAsync(avatarUrl);
                if (sprite == null) return;
                _avatarCache[avatarUrl] = sprite;
            }

            // The card may have been destroyed by a refresh while downloading.
            if (buttonPanel == null || buttonPanel.Descriptor == null) return;
            buttonPanel.SetIcon(sprite, false);
        }

        private static async Task<Sprite> DownloadSpriteAsync(string url)
        {
            try
            {
                using UnityWebRequest request = UnityWebRequestTexture.GetTexture(url);
                request.timeout = 15;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone) await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success) return null;
                Texture2D texture = DownloadHandlerTexture.GetContent(request);
                if (texture == null) return null;
                return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[Friends] avatar download failed for {url}: {ex.Message}");
                return null;
            }
        }

        // ── Invite dialogs ───────────────────────────────────────────────────

        private static void ShowFriendDialog(MisskeyUser user, bool online)
        {
            string display = string.IsNullOrEmpty(user.name) ? user.username : user.name;

            if (!online)
            {
                OpenDialogue(
                    BasisLocalization.Get("friends.dialog.offline.title"),
                    string.Format(BasisLocalization.Get("friends.dialog.offline.body"), display),
                    null, null);
                return;
            }

            if (string.IsNullOrEmpty(SakiikaWorldSession.CurrentSessionConnectionString))
            {
                OpenDialogue(
                    BasisLocalization.Get("friends.invite.needSession.title"),
                    BasisLocalization.Get("friends.invite.needSession.body"),
                    null, null);
                return;
            }

            OpenDialogue(
                BasisLocalization.Get("friends.dialog.invite.title"),
                string.Format(BasisLocalization.Get("friends.dialog.invite.body"), display),
                BasisLocalization.Get("friends.dialog.invite.send"),
                accepted =>
                {
                    if (accepted) _ = SendInviteAsync(user);
                });
        }

        private static async Task SendInviteAsync(MisskeyUser user)
        {
            bool sent = await SakiikaInvites.SendInviteAsync(user);
            string display = string.IsNullOrEmpty(user.name) ? user.username : user.name;
            OpenDialogue(
                sent ? BasisLocalization.Get("friends.invite.sent.title") : BasisLocalization.Get("friends.invite.failed.title"),
                string.Format(sent
                    ? BasisLocalization.Get("friends.invite.sent.body")
                    : BasisLocalization.Get("friends.invite.failed.body"), display),
                null, null);
        }

        /// <summary>
        /// Two-button confirm when <paramref name="accept"/> is given, otherwise a
        /// plain OK box. Releases any dialogue already on screen first (the menu
        /// allows only one), same as SakiikaWorldSession.ShowDialog.
        /// </summary>
        private static void OpenDialogue(string title, string body, string accept, Action<bool> callback)
        {
            if (BasisMainMenu.Instance == null)
            {
                BasisDebug.LogWarning($"{title}: {body}");
                return;
            }
            if (BasisMainMenu.Instance.Dialogue != null)
            {
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }
            if (string.IsNullOrEmpty(accept))
            {
                BasisMainMenu.Instance.OpenDialogue(title, body, BasisLocalization.Get("ui.ok"), _ => { });
            }
            else
            {
                BasisMainMenu.Instance.OpenDialogue(title, body, accept, BasisLocalization.Get("ui.cancel"), callback);
            }
        }
    }
}
