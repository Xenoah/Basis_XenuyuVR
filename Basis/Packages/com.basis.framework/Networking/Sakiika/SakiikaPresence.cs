using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking.Sakiika
{
    /// <summary>
    /// Serverless "online in VR" presence for the friends tab, built on the same
    /// Misskey-note pattern as the public world directory.
    ///
    /// While logged in, the client keeps one followers-only note alive carrying
    /// <see cref="PresenceTag"/> (renewed every <see cref="RenewInterval"/>,
    /// deleted on quit). Followers-only visibility keeps the heartbeat off public
    /// timelines; mutual follows can still read it. The friends tab probes each
    /// mutual's recent notes for a fresh presence note (or a fresh public world
    /// announce — hosting also proves the player is online). Stale notes age out
    /// by <see cref="MaxPresenceAge"/>, covering crashed clients that never
    /// deleted their note.
    /// </summary>
    public static class SakiikaPresence
    {
        /// <summary>Hashtag (without '#') marking a note as a SakiikaVR presence heartbeat.</summary>
        public const string PresenceTag = "SakiikaVROnline";

        /// <summary>Presence notes older than this count as offline (crash safety net).</summary>
        public static readonly TimeSpan MaxPresenceAge = TimeSpan.FromMinutes(40);
        private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(25);

        private static string _noteId;
        private static DateTime _postedAtUtc;
        private static bool _quitHookInstalled;

        // ── Heartbeat (own presence) ─────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            _ = HeartbeatLoopAsync();
        }

        private static async Task HeartbeatLoopAsync()
        {
            while (Application.isPlaying)
            {
                try
                {
                    if (!MisskeyService.IsLoggedIn)
                    {
                        // Token was cleared (logout) — the old note can no longer be
                        // deleted with it; the age filter retires it for readers.
                        _noteId = null;
                    }
                    else if (string.IsNullOrEmpty(_noteId) || DateTime.UtcNow - _postedAtUtc > RenewInterval)
                    {
                        await RenewPresenceNoteAsync();
                    }
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"[Presence] heartbeat failed: {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
        }

        private static async Task RenewPresenceNoteAsync()
        {
            string old = _noteId;
            _noteId = null;
            if (!string.IsNullOrEmpty(old))
            {
                await MisskeyService.DeleteNoteAsync(old);
            }

            string text = $"さきいかVRでオンライン中 🎮\n#{PresenceTag}";
            string id = await MisskeyService.CreateNoteAsync(text, null, "followers");
            if (!string.IsNullOrEmpty(id))
            {
                _noteId = id;
                _postedAtUtc = DateTime.UtcNow;
                InstallQuitHook();
            }
        }

        private static void InstallQuitHook()
        {
            if (_quitHookInstalled) return;
            _quitHookInstalled = true;
            Application.quitting += () =>
            {
                // Best-effort: the request may not finish before the process dies;
                // stale presence is also filtered by age on the reader side.
                string note = _noteId;
                _noteId = null;
                if (!string.IsNullOrEmpty(note)) _ = MisskeyService.DeleteNoteAsync(note);
            };
        }

        // ── Probing (friends' presence) ──────────────────────────────────────

        /// <summary>
        /// Returns the ids of the given users who look online in VR right now.
        /// One users/notes query per user, run in small parallel batches; capped
        /// so a huge follow list cannot burst-hit the instance's rate limit.
        /// </summary>
        public static async Task<HashSet<string>> QueryOnlineUserIdsAsync(IReadOnlyList<MisskeyUser> users, int maxUsers = 64)
        {
            HashSet<string> online = new HashSet<string>(StringComparer.Ordinal);
            if (users == null || users.Count == 0) return online;

            int count = Math.Min(users.Count, maxUsers);
            if (users.Count > maxUsers)
            {
                BasisDebug.LogWarning($"[Presence] {users.Count} mutuals; probing only the first {maxUsers}.");
            }

            const int batchSize = 6;
            for (int start = 0; start < count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, count);
                Task<bool>[] batch = new Task<bool>[end - start];
                for (int i = start; i < end; i++)
                {
                    batch[i - start] = IsUserOnlineAsync(users[i].id);
                }
                await Task.WhenAll(batch);
                for (int i = start; i < end; i++)
                {
                    if (batch[i - start].Result) online.Add(users[i].id);
                }
            }
            return online;
        }

        private static async Task<bool> IsUserOnlineAsync(string userId)
        {
            MisskeyNote[] notes = await MisskeyService.GetUserNotesAsync(userId, 5);
            foreach (MisskeyNote note in notes)
            {
                if (note?.text == null) continue;
                bool presence = note.text.Contains("#" + PresenceTag);
                bool hosting = note.text.Contains("#" + MisskeyService.WorldTag);
                if (!presence && !hosting) continue;
                if (DateTime.UtcNow - ParseCreatedAt(note.createdAt) <= MaxPresenceAge) return true;
            }
            return false;
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
