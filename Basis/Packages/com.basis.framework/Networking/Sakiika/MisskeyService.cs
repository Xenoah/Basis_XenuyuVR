using Basis.Scripts.Common;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Basis.Scripts.Networking.Sakiika
{
    /// <summary>
    /// Minimal Misskey REST client backing the serverless public-world directory.
    ///
    /// A public world is announced as a public note that carries a hashtag plus a
    /// machine-readable payload line (<see cref="PayloadPrefix"/> + Base64 JSON).
    /// The in-client world list is recovered by searching that hashtag, so no
    /// dedicated directory server is required. Login uses MiAuth — the user
    /// approves the app in their browser and the client polls for the access
    /// token — which also needs no client secret or backend.
    /// </summary>
    public static class MisskeyService
    {
        public const string InstanceFile = "MisskeyInstance.BAS";
        public const string TokenFile = "MisskeyToken.BAS";
        public const string UserFile = "MisskeyUser.BAS";

        public const string DefaultInstance = "https://misskey.io";

        /// <summary>Hashtag (without '#') that marks a note as a SakiikaVR world announce.</summary>
        public const string WorldTag = "SakiikaVRWorld";

        /// <summary>Prefix of the machine-readable payload line inside an announce note.</summary>
        public const string PayloadPrefix = "MVRP1:";

        public const string MiAuthAppName = "SakiikaVR";
        // read:account is needed for notes/mentions (receiving friend invites),
        // read:following for mutual-follow lists when the user hides them.
        // Tokens issued before these scopes were added keep working for
        // everything except invite receiving; the poller detects the 403 and
        // disables itself until the user re-logs in.
        public const string MiAuthPermission = "write:notes,read:account,read:following";

        /// <summary>Announce notes older than this are treated as stale and hidden from the list.</summary>
        public static readonly TimeSpan MaxAnnounceAge = TimeSpan.FromHours(12);

        public static string InstanceUrl
        {
            get => NormalizeInstanceUrl(BasisDataStore.LoadString(InstanceFile, DefaultInstance));
            set => BasisDataStore.SaveString(NormalizeInstanceUrl(value), InstanceFile);
        }

        public static string Token
        {
            get => BasisDataStore.LoadString(TokenFile, string.Empty);
            set => BasisDataStore.SaveString(value ?? string.Empty, TokenFile);
        }

        /// <summary>Username on the instance, e.g. "alice". Display-only.</summary>
        public static string Username
        {
            get => BasisDataStore.LoadString(UserFile, string.Empty);
            set => BasisDataStore.SaveString(value ?? string.Empty, UserFile);
        }

        public static bool IsLoggedIn => !string.IsNullOrEmpty(Token);

        public static void Logout()
        {
            Token = string.Empty;
            Username = string.Empty;
        }

        /// <summary>Trims, prepends https:// when missing, drops trailing slashes.</summary>
        public static string NormalizeInstanceUrl(string raw)
        {
            string value = (raw ?? string.Empty).Trim();
            if (value.Length == 0) return DefaultInstance;
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }
            return value.TrimEnd('/');
        }

        // ── MiAuth ───────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a MiAuth flow: opens the approval page in the system browser and
        /// returns the session id to poll with <see cref="PollMiAuthAsync"/>.
        /// </summary>
        public static string BeginMiAuth()
        {
            string session = Guid.NewGuid().ToString();
            string url = $"{InstanceUrl}/miauth/{session}" +
                         $"?name={Uri.EscapeDataString(MiAuthAppName)}" +
                         $"&permission={Uri.EscapeDataString(MiAuthPermission)}";
            Application.OpenURL(url);
            return session;
        }

        [Serializable]
        private class MiAuthCheckResponse
        {
            public bool ok;
            public string token;
            public MisskeyUser user;
        }

        /// <summary>
        /// Polls <c>/api/miauth/{session}/check</c> until the user approves in the
        /// browser or the timeout elapses. On success the token and username are
        /// persisted and true is returned.
        /// </summary>
        public static async Task<bool> PollMiAuthAsync(string session, int timeoutSeconds = 180)
        {
            string url = $"{InstanceUrl}/api/miauth/{Uri.EscapeDataString(session)}/check";
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                (bool ok, string body, long _) = await PostJsonAsync(url, "{}");
                if (ok && !string.IsNullOrEmpty(body))
                {
                    MiAuthCheckResponse response = null;
                    try { response = JsonUtility.FromJson<MiAuthCheckResponse>(body); }
                    catch (Exception ex) { BasisDebug.LogWarning($"[Misskey] MiAuth check parse failed: {ex.Message}"); }

                    if (response != null && response.ok && !string.IsNullOrEmpty(response.token))
                    {
                        Token = response.token;
                        Username = response.user?.username ?? string.Empty;
                        // The in-game display name is fixed to the Misskey account
                        // name; keep the stored username in sync for every flow
                        // that reads it (deep links, connection service).
                        if (!string.IsNullOrEmpty(Username))
                        {
                            BasisDataStore.SaveString(Username, BasisConnectionService.UsernameFileName);
                        }
                        BasisDebug.Log($"[Misskey] Logged in as @{Username} on {InstanceUrl}");
                        return true;
                    }
                }
                await Task.Delay(2000);
            }
            return false;
        }

        // ── Notes ────────────────────────────────────────────────────────────

        /// <summary>Creates a public note; returns the note id, or null on failure.</summary>
        public static async Task<string> CreateNoteAsync(string text)
        {
            return await CreateNoteAsync(text, null, "public");
        }

        /// <summary>
        /// Creates a note, optionally as a reply to <paramref name="replyId"/> and with a
        /// given visibility ("public", "home", "specified"). Returns the note id or null.
        /// </summary>
        public static Task<string> CreateNoteAsync(string text, string replyId, string visibility)
            => CreateNoteAsync(text, replyId, visibility, null);

        /// <summary>
        /// Creates a note; when <paramref name="visibility"/> is "specified",
        /// <paramref name="visibleUserIds"/> selects the recipients (a Misskey DM).
        /// </summary>
        public static async Task<string> CreateNoteAsync(string text, string replyId, string visibility, string[] visibleUserIds)
        {
            if (!IsLoggedIn)
            {
                BasisDebug.LogWarning("[Misskey] CreateNote requested without a login token.");
                return null;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"i\":\"").Append(JsonEscape(Token)).Append('"');
            sb.Append(",\"visibility\":\"").Append(JsonEscape(visibility)).Append('"');
            if (!string.IsNullOrEmpty(replyId)) sb.Append(",\"replyId\":\"").Append(JsonEscape(replyId)).Append('"');
            if (visibleUserIds != null && visibleUserIds.Length > 0)
            {
                sb.Append(",\"visibleUserIds\":[");
                for (int i = 0; i < visibleUserIds.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(JsonEscape(visibleUserIds[i])).Append('"');
                }
                sb.Append(']');
            }
            sb.Append(",\"text\":\"").Append(JsonEscape(text)).Append("\"}");
            string body = sb.ToString();
            (bool ok, string response, long code) = await PostJsonAsync($"{InstanceUrl}/api/notes/create", body);
            if (!ok)
            {
                BasisDebug.LogError($"[Misskey] notes/create failed (HTTP {code}): {response}");
                return null;
            }

            try
            {
                CreatedNoteResponse created = JsonUtility.FromJson<CreatedNoteResponse>(response);
                return created?.createdNote?.id;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[Misskey] notes/create parse failed: {ex.Message}");
                return null;
            }
        }

        [Serializable]
        private class CreatedNoteResponse
        {
            public MisskeyNote createdNote;
        }

        /// <summary>Deletes a note previously created with this account. Best-effort.</summary>
        public static async Task<bool> DeleteNoteAsync(string noteId)
        {
            if (!IsLoggedIn || string.IsNullOrEmpty(noteId)) return false;
            string body = "{\"i\":\"" + JsonEscape(Token) + "\",\"noteId\":\"" + JsonEscape(noteId) + "\"}";
            (bool ok, string response, long code) = await PostJsonAsync($"{InstanceUrl}/api/notes/delete", body);
            if (!ok) BasisDebug.LogWarning($"[Misskey] notes/delete failed (HTTP {code}): {response}");
            return ok;
        }

        [Serializable]
        private class NoteListWrapper
        {
            public MisskeyNote[] Items;
        }

        /// <summary>
        /// Fetches recent public notes carrying <see cref="WorldTag"/>. Works
        /// without a login on instances that allow anonymous tag search; the token
        /// is attached when available.
        /// </summary>
        public static async Task<MisskeyNote[]> SearchWorldNotesAsync(int limit = 30)
        {
            StringBuilder body = new StringBuilder();
            body.Append("{\"tag\":\"").Append(JsonEscape(WorldTag)).Append("\",\"limit\":").Append(limit);
            if (IsLoggedIn) body.Append(",\"i\":\"").Append(JsonEscape(Token)).Append('"');
            body.Append('}');

            (bool ok, string response, long code) = await PostJsonAsync($"{InstanceUrl}/api/notes/search-by-tag", body.ToString());
            if (!ok)
            {
                BasisDebug.LogWarning($"[Misskey] notes/search-by-tag failed (HTTP {code}): {response}");
                return Array.Empty<MisskeyNote>();
            }

            try
            {
                // JsonUtility cannot parse a top-level array, so wrap it in an object.
                NoteListWrapper wrapper = JsonUtility.FromJson<NoteListWrapper>("{\"Items\":" + response + "}");
                return wrapper?.Items ?? Array.Empty<MisskeyNote>();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[Misskey] notes/search-by-tag parse failed: {ex.Message}");
                return Array.Empty<MisskeyNote>();
            }
        }

        // ── Users (friends tab) ──────────────────────────────────────────────

        /// <summary>
        /// Resolves a username on the configured instance to its full user object
        /// (id, avatarUrl, …) via <c>users/show</c>. Public endpoint — works with
        /// tokens that predate the friends feature. Null on failure.
        /// </summary>
        public static async Task<MisskeyUser> GetUserByUsernameAsync(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            StringBuilder body = new StringBuilder();
            body.Append("{\"username\":\"").Append(JsonEscape(username)).Append('"');
            if (IsLoggedIn) body.Append(",\"i\":\"").Append(JsonEscape(Token)).Append('"');
            body.Append('}');

            (bool ok, string response, long code) = await PostJsonAsync($"{InstanceUrl}/api/users/show", body.ToString());
            if (!ok)
            {
                BasisDebug.LogWarning($"[Misskey] users/show failed (HTTP {code}): {response}");
                return null;
            }
            try
            {
                MisskeyUser user = JsonUtility.FromJson<MisskeyUser>(response);
                return string.IsNullOrEmpty(user?.id) ? null : user;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[Misskey] users/show parse failed: {ex.Message}");
                return null;
            }
        }

        [Serializable]
        private class FollowEntry
        {
            public string id;
            public MisskeyUser followee;
            public MisskeyUser follower;
        }

        [Serializable]
        private class FollowListWrapper
        {
            public FollowEntry[] Items;
        }

        /// <summary>Users the given account follows (paginated, capped).</summary>
        public static Task<List<MisskeyUser>> GetFollowingUsersAsync(string userId, int maxCount = 300)
            => GetFollowListAsync(userId, "following", maxCount);

        /// <summary>Users following the given account (paginated, capped).</summary>
        public static Task<List<MisskeyUser>> GetFollowerUsersAsync(string userId, int maxCount = 300)
            => GetFollowListAsync(userId, "followers", maxCount);

        private static async Task<List<MisskeyUser>> GetFollowListAsync(string userId, string direction, int maxCount)
        {
            List<MisskeyUser> users = new List<MisskeyUser>();
            if (string.IsNullOrEmpty(userId)) return users;

            string untilId = null;
            while (users.Count < maxCount)
            {
                int pageSize = Math.Min(100, maxCount - users.Count);
                StringBuilder body = new StringBuilder();
                body.Append("{\"userId\":\"").Append(JsonEscape(userId)).Append("\",\"limit\":").Append(pageSize);
                if (!string.IsNullOrEmpty(untilId)) body.Append(",\"untilId\":\"").Append(JsonEscape(untilId)).Append('"');
                if (IsLoggedIn) body.Append(",\"i\":\"").Append(JsonEscape(Token)).Append('"');
                body.Append('}');

                (bool ok, string response, long code) = await PostJsonAsync($"{InstanceUrl}/api/users/{direction}", body.ToString());
                if (!ok)
                {
                    BasisDebug.LogWarning($"[Misskey] users/{direction} failed (HTTP {code}): {response}");
                    break;
                }

                FollowEntry[] entries;
                try
                {
                    FollowListWrapper wrapper = JsonUtility.FromJson<FollowListWrapper>("{\"Items\":" + response + "}");
                    entries = wrapper?.Items ?? Array.Empty<FollowEntry>();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"[Misskey] users/{direction} parse failed: {ex.Message}");
                    break;
                }

                if (entries.Length == 0) break;
                foreach (FollowEntry entry in entries)
                {
                    MisskeyUser user = direction == "following" ? entry?.followee : entry?.follower;
                    if (user != null && !string.IsNullOrEmpty(user.id)) users.Add(user);
                    untilId = entry?.id;
                }
                if (entries.Length < pageSize) break;
            }
            return users;
        }

        /// <summary>
        /// Recent notes of one user (presence probing). The token is attached so
        /// followers-only presence notes are visible to mutuals.
        /// </summary>
        public static async Task<MisskeyNote[]> GetUserNotesAsync(string userId, int limit = 5)
        {
            if (string.IsNullOrEmpty(userId)) return Array.Empty<MisskeyNote>();
            StringBuilder body = new StringBuilder();
            body.Append("{\"userId\":\"").Append(JsonEscape(userId)).Append("\",\"limit\":").Append(limit)
                .Append(",\"withReplies\":false,\"withRenotes\":false");
            if (IsLoggedIn) body.Append(",\"i\":\"").Append(JsonEscape(Token)).Append('"');
            body.Append('}');

            (bool ok, string response, long _) = await PostJsonAsync($"{InstanceUrl}/api/users/notes", body.ToString());
            if (!ok) return Array.Empty<MisskeyNote>();
            try
            {
                NoteListWrapper wrapper = JsonUtility.FromJson<NoteListWrapper>("{\"Items\":" + response + "}");
                return wrapper?.Items ?? Array.Empty<MisskeyNote>();
            }
            catch
            {
                return Array.Empty<MisskeyNote>();
            }
        }

        /// <summary>
        /// Direct ("specified") notes addressed to the logged-in account — the
        /// friend-invite inbox. Returns ok=false with the HTTP code on failure so
        /// the caller can distinguish a permission problem (old token without
        /// read:account) from a transient error.
        /// </summary>
        public static async Task<(bool ok, long code, MisskeyNote[] notes)> GetSpecifiedMentionsAsync(int limit = 10)
        {
            if (!IsLoggedIn) return (false, 0, Array.Empty<MisskeyNote>());
            StringBuilder body = new StringBuilder();
            body.Append("{\"i\":\"").Append(JsonEscape(Token)).Append("\",\"limit\":").Append(limit)
                .Append(",\"visibility\":\"specified\"}");

            (bool ok, string response, long code) = await PostJsonAsync($"{InstanceUrl}/api/notes/mentions", body.ToString());
            if (!ok) return (false, code, Array.Empty<MisskeyNote>());
            try
            {
                NoteListWrapper wrapper = JsonUtility.FromJson<NoteListWrapper>("{\"Items\":" + response + "}");
                return (true, code, wrapper?.Items ?? Array.Empty<MisskeyNote>());
            }
            catch
            {
                return (false, code, Array.Empty<MisskeyNote>());
            }
        }

        /// <summary>
        /// Fetches replies to a note (used as the hole-punch signaling channel):
        /// guests reply to the host's announce note with their endpoint.
        /// </summary>
        public static async Task<MisskeyNote[]> GetNoteRepliesAsync(string noteId, int limit = 30)
        {
            if (string.IsNullOrEmpty(noteId)) return Array.Empty<MisskeyNote>();
            StringBuilder body = new StringBuilder();
            body.Append("{\"noteId\":\"").Append(JsonEscape(noteId)).Append("\",\"limit\":").Append(limit);
            if (IsLoggedIn) body.Append(",\"i\":\"").Append(JsonEscape(Token)).Append('"');
            body.Append('}');

            (bool ok, string response, long code) = await PostJsonAsync($"{InstanceUrl}/api/notes/children", body.ToString());
            if (!ok) return Array.Empty<MisskeyNote>();
            try
            {
                NoteListWrapper wrapper = JsonUtility.FromJson<NoteListWrapper>("{\"Items\":" + response + "}");
                return wrapper?.Items ?? Array.Empty<MisskeyNote>();
            }
            catch
            {
                return Array.Empty<MisskeyNote>();
            }
        }

        // ── World announce payload ───────────────────────────────────────────

        public static string BuildAnnounceNoteText(WorldAnnouncePayload payload)
        {
            string json = JsonUtility.ToJson(payload);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            return $"🌐 {payload.name}\n" +
                   $"さきいかVR パブリックワールド公開中!\n" +
                   $"#{WorldTag}\n" +
                   PayloadPrefix + encoded;
        }

        public static bool TryDecodeWorldPayload(string noteText, out WorldAnnouncePayload payload)
        {
            bool ok = TryDecodePayload(noteText, PayloadPrefix, out payload);
            if (ok && string.IsNullOrEmpty(payload.conn))
            {
                payload = null;
                return false;
            }
            return ok;
        }

        /// <summary>Base64-JSON payload line with an arbitrary prefix (world announce, invite, …).</summary>
        public static string BuildPayloadLine<T>(string prefix, T payload)
        {
            string json = JsonUtility.ToJson(payload);
            return prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static bool TryDecodePayload<T>(string noteText, string prefix, out T payload) where T : class
        {
            payload = null;
            if (string.IsNullOrEmpty(noteText)) return false;

            foreach (string rawLine in noteText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                try
                {
                    string json = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(prefix.Length)));
                    T decoded = JsonUtility.FromJson<T>(json);
                    if (decoded == null) return false;
                    payload = decoded;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        // ── HTTP ─────────────────────────────────────────────────────────────

        /// <summary>
        /// POSTs a JSON body and returns (success, responseBody, httpCode). Must be
        /// awaited from the main thread (UnityWebRequest requirement).
        /// </summary>
        public static async Task<(bool ok, string body, long code)> PostJsonAsync(string url, string json, int timeoutSeconds = 15)
        {
            using UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = timeoutSeconds;

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();

            string body = request.downloadHandler?.text ?? string.Empty;
            bool ok = request.result == UnityWebRequest.Result.Success;
            return (ok, body, request.responseCode);
        }

        /// <summary>GETs a URL and returns the trimmed body, or null on failure.</summary>
        public static async Task<string> GetTextAsync(string url, int timeoutSeconds = 10)
        {
            using UnityWebRequest request = UnityWebRequest.Get(url);
            request.timeout = timeoutSeconds;

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success) return null;
            return request.downloadHandler?.text?.Trim();
        }

        public static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            StringBuilder sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    [Serializable]
    public class MisskeyUser
    {
        public string id;
        public string username;
        public string name;
        public string host;
        public string avatarUrl;
    }

    [Serializable]
    public class MisskeyNote
    {
        public string id;
        public string createdAt;
        public string text;
        public MisskeyUser user;
    }

    /// <summary>Machine-readable body of a world announce note.</summary>
    [Serializable]
    public class WorldAnnouncePayload
    {
        public int v = 1;
        /// <summary>World display name.</summary>
        public string name;
        /// <summary>Connection string, address:port#password.</summary>
        public string conn;
        /// <summary>Host player display name.</summary>
        public string host;
    }

    /// <summary>
    /// Hole-punch signal a joining guest posts as a reply to the announce note so
    /// the host can open its NAT toward the guest. Line prefix: MVRJOIN:&lt;base64&gt;.
    /// </summary>
    [Serializable]
    public class JoinSignalPayload
    {
        public int v = 1;
        /// <summary>Guest public IPv4.</summary>
        public string ip;
        /// <summary>Guest local UDP port (best-effort; NATs that preserve the port map it 1:1).</summary>
        public int port;
        /// <summary>Random token so the host can dedupe repeated signals.</summary>
        public string token;
    }
}
