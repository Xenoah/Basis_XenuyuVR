using Basis.BasisUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking.Sakiika
{
    /// <summary>
    /// Friend invites over Misskey DMs. Sending posts a "specified"-visibility
    /// note to the friend carrying the current session's connection string as a
    /// machine-readable payload line; receiving polls the direct-note inbox and
    /// offers a join dialog. No server involved — the DM is the transport.
    ///
    /// Receiving needs the read:account scope; tokens issued before the friends
    /// feature don't have it, so the poller disables itself on the first 403
    /// (re-login fixes it). Sending only needs write:notes and works everywhere.
    /// </summary>
    public static class SakiikaInvites
    {
        /// <summary>Prefix of the machine-readable payload line inside an invite DM.</summary>
        public const string InvitePayloadPrefix = "MVRI1:";

        /// <summary>Invites older than this are ignored by the receiver (stale sessions).</summary>
        private static readonly TimeSpan MaxInviteAge = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(45);

        private static readonly HashSet<string> _seenNoteIds = new HashSet<string>(StringComparer.Ordinal);
        private static DateTime _startupUtc;
        private static bool _pollingUnavailable;

        [Serializable]
        public class InvitePayload
        {
            public int v = 1;
            /// <summary>World display name, may be empty.</summary>
            public string name;
            /// <summary>Connection string, address:port#password.</summary>
            public string conn;
            /// <summary>Inviting player's Misskey username.</summary>
            public string host;
        }

        // ── Sending ──────────────────────────────────────────────────────────

        /// <summary>
        /// Invites a friend to the current session. Returns false when there is no
        /// active session or the DM could not be posted; callers surface the result.
        /// </summary>
        public static async Task<bool> SendInviteAsync(MisskeyUser target)
        {
            string conn = SakiikaWorldSession.CurrentSessionConnectionString;
            if (target == null || string.IsNullOrEmpty(target.id) || string.IsNullOrEmpty(conn)) return false;

            InvitePayload payload = new InvitePayload
            {
                v = 1,
                name = SakiikaWorldSession.CurrentSessionWorldName ?? string.Empty,
                conn = conn,
                host = MisskeyService.Username,
            };

            string worldLine = string.IsNullOrEmpty(payload.name) ? string.Empty : $"「{payload.name}」\n";
            string text = $"🎈 さきいかVRのワールドに招待されました!\n{worldLine}" +
                          MisskeyService.BuildPayloadLine(InvitePayloadPrefix, payload);

            string noteId = await MisskeyService.CreateNoteAsync(text, null, "specified", new[] { target.id });
            return !string.IsNullOrEmpty(noteId);
        }

        // ── Receiving ────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            _startupUtc = DateTime.UtcNow;
            _ = PollLoopAsync();
        }

        private static async Task PollLoopAsync()
        {
            while (Application.isPlaying)
            {
                await Task.Delay(PollInterval);
                if (!MisskeyService.IsLoggedIn || _pollingUnavailable) continue;
                try
                {
                    await PollOnceAsync();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"[Invites] poll failed: {ex.Message}");
                }
            }
        }

        private static async Task PollOnceAsync()
        {
            (bool ok, long code, MisskeyNote[] notes) = await MisskeyService.GetSpecifiedMentionsAsync(10);
            if (!ok)
            {
                if (code == 403 || code == 401)
                {
                    _pollingUnavailable = true;
                    BasisDebug.LogWarning("[Invites] notes/mentions is not permitted by this token; " +
                        "invite receiving is disabled. Log in to Misskey again to enable it.");
                }
                return;
            }

            foreach (MisskeyNote note in notes)
            {
                if (note == null || string.IsNullOrEmpty(note.id) || _seenNoteIds.Contains(note.id)) continue;
                _seenNoteIds.Add(note.id);

                // Only fresh invites addressed to this run of the client.
                DateTime created = ParseCreatedAt(note.createdAt);
                if (created < _startupUtc || DateTime.UtcNow - created > MaxInviteAge) continue;
                if (!MisskeyService.TryDecodePayload(note.text, InvitePayloadPrefix, out InvitePayload payload)) continue;
                if (string.IsNullOrEmpty(payload.conn)) continue;

                // Ignore invites we sent ourselves (echo in the inbox).
                string sender = note.user?.username ?? payload.host ?? "?";
                if (string.Equals(sender, MisskeyService.Username, StringComparison.OrdinalIgnoreCase)) continue;

                PromptInvite(sender, payload);
                break; // one dialog at a time; the rest surface on later polls
            }

            // The seen-set only ever grows during a session; cap it defensively.
            if (_seenNoteIds.Count > 500) _seenNoteIds.Clear();
        }

        private static void PromptInvite(string sender, InvitePayload payload)
        {
            BasisMainMenu.Open();
            if (BasisMainMenu.Instance == null)
            {
                BasisDebug.LogWarning($"[Invites] invite from {sender} could not be shown (no menu instance).");
                return;
            }
            if (BasisMainMenu.Instance.Dialogue != null)
            {
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }
            string worldLabel = string.IsNullOrEmpty(payload.name)
                ? BasisLocalization.Get("sakiika.worlds.unnamed")
                : payload.name;
            BasisMainMenu.Instance.OpenDialogue(
                BasisLocalization.Get("friends.invite.received.title"),
                string.Format(BasisLocalization.Get("friends.invite.received.body"), sender, worldLabel),
                BasisLocalization.Get("friends.invite.received.join"),
                BasisLocalization.Get("ui.cancel"),
                accepted =>
                {
                    if (accepted) _ = SakiikaWorldSession.JoinFromConnectionStringAsync(payload.conn);
                });
        }

        private static DateTime ParseCreatedAt(string createdAt)
        {
            if (DateTime.TryParse(createdAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            {
                return parsed;
            }
            return DateTime.MinValue;
        }
    }
}
