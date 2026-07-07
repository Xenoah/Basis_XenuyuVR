using Basis.Network.Core;
using Basis.Scripts.Common;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking.Sakiika
{
    /// <summary>
    /// Serverless NAT hole punching for player-hosted public worlds, using Misskey
    /// as the out-of-band signaling channel. No STUN/relay server is run by anyone.
    ///
    /// Flow: the host's announce note is the rendezvous point. A joining guest posts
    /// a reply carrying its public IP + local UDP port (<see cref="JoinSignalPayload"/>).
    /// The host polls those replies and fires a short burst of unconnected UDP packets
    /// at each guest endpoint, opening its own NAT toward the guest so the guest's
    /// LiteNetLib connect packets are allowed through. The guest likewise punches the
    /// host endpoint before/while connecting. Both sides also learn each other's real
    /// external endpoint from the source address of any punch packet that arrives and
    /// re-punch it precisely.
    ///
    /// Limits (inherent, not bugs): full-cone and address-restricted-cone NATs are
    /// covered; port-restricted and symmetric NATs that do not preserve the source
    /// port cannot be traversed without a relay. Invite (no-note) sessions have no
    /// signaling channel, so only full-cone hosts are reachable there.
    /// </summary>
    public static class SakiikaHolePunch
    {
        private const int PunchBurst = 12;
        private const int PunchIntervalMs = 150;
        private const int HostPollIntervalMs = 3000;
        // Marker byte payload so a punch packet is obviously ours in logs/captures.
        private static readonly byte[] PunchPayload = Encoding.ASCII.GetBytes("MVRPUNCH");

        // ── Host side ────────────────────────────────────────────────────────

        private static CancellationTokenSource _hostCts;
        private static readonly ConcurrentDictionary<string, byte> _seenTokens = new();
        // When set, the host is relaying through TURN: guests are permitted on the
        // relay instead of being punched (punching a relayed session is pointless).
        private static SakiikaTurnClient _turnClient;

        /// <summary>
        /// Starts the host punch loop for a public session: poll the announce note's
        /// replies and, for each guest that signals in, either punch its NAT (direct
        /// mode) or permit it on the TURN relay (<paramref name="turnClient"/> set).
        /// Safe to call once per hosted session; <see cref="StopHost"/> ends it.
        /// </summary>
        public static void StartHost(string noteId, SakiikaTurnClient turnClient = null)
        {
            StopHost();
            if (string.IsNullOrEmpty(noteId)) return;

            _turnClient = turnClient;
            _seenTokens.Clear();
            _hostCts = new CancellationTokenSource();
            CancellationToken token = _hostCts.Token;

            // Direct mode learns real guest endpoints from inbound punches; the relay
            // gets them from the Misskey signal, so the socket hook is direct-only.
            if (_turnClient == null) HookServerUnconnected();

            _ = HostPollLoopAsync(noteId, token);
        }

        public static void StopHost()
        {
            _hostCts?.Cancel();
            _hostCts = null;
            _turnClient = null;
            UnhookServerUnconnected();
        }

        private static async Task HostPollLoopAsync(string noteId, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    MisskeyNote[] replies = await MisskeyService.GetNoteRepliesAsync(noteId, 40);
                    foreach (MisskeyNote reply in replies)
                    {
                        if (reply == null || !TryDecodeJoin(reply.text, out JoinSignalPayload join)) continue;
                        if (!_seenTokens.TryAdd(join.token ?? (join.ip + ":" + join.port), 0)) continue;
                        if (!IPAddress.TryParse(join.ip, out IPAddress addr) || join.port <= 0 || join.port > ushort.MaxValue) continue;

                        IPEndPoint guestEp = new IPEndPoint(addr, join.port);
                        if (_turnClient != null)
                        {
                            BasisDebug.Log($"[HolePunch] Guest signalled {guestEp}; permitting on TURN relay.", BasisDebug.LogTag.Networking);
                            _ = _turnClient.PermitGuestAsync(guestEp);
                        }
                        else
                        {
                            BasisDebug.Log($"[HolePunch] Guest signalled {guestEp}; punching.", BasisDebug.LogTag.Networking);
                            _ = PunchAsync(GetServerManager, guestEp, token);
                        }
                    }

                    await Task.Delay(HostPollIntervalMs, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[HolePunch] Host poll loop ended: {ex.Message}");
            }
        }

        private static NetManager GetServerManager()
        {
            try { return NetworkServer.Server; }
            catch { return null; }
        }

        private static bool _serverHooked;
        private static void HookServerUnconnected()
        {
            try
            {
                EventBasedNetListener listener = NetworkServer.Listener;
                if (listener == null || _serverHooked) return;
                listener.NetworkReceiveUnconnectedEvent += OnServerUnconnected;
                _serverHooked = true;
            }
            catch { }
        }

        private static void UnhookServerUnconnected()
        {
            try
            {
                if (!_serverHooked) return;
                EventBasedNetListener listener = NetworkServer.Listener;
                if (listener != null) listener.NetworkReceiveUnconnectedEvent -= OnServerUnconnected;
            }
            catch { }
            finally { _serverHooked = false; }
        }

        private static void OnServerUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader)
        {
            // Any inbound punch reveals the guest's true external endpoint (port and
            // all) — punch it back precisely to cover port-restricted-cone hosts.
            if (remoteEndPoint == null) return;
            CancellationToken token = _hostCts?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested) return;
            _ = PunchAsync(GetServerManager, remoteEndPoint, token, bursts: 6);
        }

        // ── Guest side ───────────────────────────────────────────────────────

        /// <summary>
        /// Guest half: reply to the announce note with our server-reflexive endpoint
        /// (<paramref name="guestPublic"/>, from STUN) and punch the host. Runs
        /// concurrently with the LiteNetLib connect attempt.
        /// </summary>
        public static async Task GuestSignalAndPunchAsync(string noteId, IPEndPoint hostEndpoint, IPEndPoint guestPublic)
        {
            try
            {
                // Punch first so a mapping toward the host exists before it replies.
                _ = PunchAsync(GetClientManager, hostEndpoint, CancellationToken.None);

                if (!string.IsNullOrEmpty(noteId) && MisskeyService.IsLoggedIn && guestPublic != null)
                {
                    JoinSignalPayload payload = new JoinSignalPayload
                    {
                        v = 1,
                        ip = guestPublic.Address.ToString(),
                        port = guestPublic.Port,
                        token = Guid.NewGuid().ToString("N").Substring(0, 8),
                    };
                    string json = JsonUtility.ToJson(payload);
                    string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                    // "home" visibility keeps the signal off the public timeline.
                    await MisskeyService.CreateNoteAsync("MVRJOIN:" + encoded, noteId, "home");
                }

                // Keep punching for a bit so we cross the host's poll interval.
                await PunchAsync(GetClientManager, hostEndpoint, CancellationToken.None, bursts: PunchBurst * 3);
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[HolePunch] Guest signal/punch failed: {ex.Message}");
            }
        }

        private static NetManager GetClientManager()
        {
            try { return BasisNetworkConnection.NetworkClient?.client; }
            catch { return null; }
        }

        // ── Shared ───────────────────────────────────────────────────────────

        private static async Task PunchAsync(Func<NetManager> managerGetter, IPEndPoint target, CancellationToken token, int bursts = PunchBurst)
        {
            if (target == null) return;
            for (int i = 0; i < bursts && !token.IsCancellationRequested; i++)
            {
                try
                {
                    NetManager manager = managerGetter();
                    if (manager != null)
                    {
                        NetDataWriter writer = new NetDataWriter();
                        writer.Put(PunchPayload);
                        manager.SendUnconnectedMessage(writer, target);
                    }
                }
                catch { /* socket may be mid-teardown */ }

                try { await Task.Delay(PunchIntervalMs, token); }
                catch (OperationCanceledException) { return; }
            }
        }

        public static bool TryDecodeJoin(string noteText, out JoinSignalPayload payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(noteText)) return false;
            foreach (string rawLine in noteText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("MVRJOIN:", StringComparison.Ordinal)) continue;
                try
                {
                    string json = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring("MVRJOIN:".Length)));
                    JoinSignalPayload decoded = JsonUtility.FromJson<JoinSignalPayload>(json);
                    if (decoded == null || string.IsNullOrEmpty(decoded.ip)) return false;
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
    }
}
