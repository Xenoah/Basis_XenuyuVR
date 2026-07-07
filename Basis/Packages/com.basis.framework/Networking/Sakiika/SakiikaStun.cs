using Basis.Network.Core;
using Basis.Scripts.Common;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using LiteNetManagerConcrete = LiteNetLib.NetManager;

namespace Basis.Scripts.Networking.Sakiika
{
    /// <summary>
    /// Discovers the server-reflexive (public) endpoint of a live LiteNetLib socket
    /// by sending an RFC 5389 STUN Binding Request out that exact socket and parsing
    /// the XOR-MAPPED-ADDRESS from the response. This is the srflx candidate for the
    /// serverless ICE-lite flow — because the query goes out the game socket, the
    /// mapping it reports is the one guests must target.
    ///
    /// Uses public STUN servers (no server run by anyone). LiteNetLib passes STUN
    /// responses to us via <see cref="LiteNetManagerConcrete.OnStunResponse"/>.
    /// </summary>
    public static class SakiikaStun
    {
        // Public STUN servers (host:port). Tried in order until one answers.
        private static readonly (string host, int port)[] Servers =
        {
            ("stun.l.google.com", 19302),
            ("stun1.l.google.com", 19302),
            ("stun.cloudflare.com", 3478),
        };

        private static readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> _pending = new();
        private static bool _hooked;

        private static void EnsureHook()
        {
            if (_hooked) return;
            _hooked = true;
            LiteNetManagerConcrete.OnStunResponse += OnStunResponse;
        }

        /// <summary>
        /// Resolves the public endpoint of <paramref name="manager"/>'s socket, or null
        /// if no STUN server answered within the timeout. Must run on the main thread.
        /// </summary>
        public static async Task<IPEndPoint> DiscoverAsync(NetManager manager, int perServerTimeoutMs = 1200)
        {
            LiteNetManagerConcrete lnl = ExtractLiteNetManager(manager);
            if (lnl == null)
            {
                BasisDebug.LogWarning("[STUN] No LiteNetLib manager available for discovery.");
                return null;
            }

            EnsureHook();

            foreach ((string host, int port) in Servers)
            {
                IPEndPoint serverEp = await ResolveAsync(host, port);
                if (serverEp == null) continue;

                byte[] txId = NewTransactionId(out string key);
                TaskCompletionSource<IPEndPoint> tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[key] = tcs;
                try
                {
                    byte[] request = BuildBindingRequest(txId);
                    // A couple of sends absorbs the odd dropped datagram.
                    lnl.SendStunBinding(request, serverEp);
                    lnl.SendStunBinding(request, serverEp);

                    Task finished = await Task.WhenAny(tcs.Task, Task.Delay(perServerTimeoutMs));
                    if (finished == tcs.Task && tcs.Task.Result != null)
                    {
                        BasisDebug.Log($"[STUN] Public endpoint via {host}: {tcs.Task.Result}", BasisDebug.LogTag.Networking);
                        return tcs.Task.Result;
                    }
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"[STUN] Query to {host} failed: {ex.Message}");
                }
                finally
                {
                    _pending.TryRemove(key, out _);
                }
            }

            BasisDebug.LogWarning("[STUN] No STUN server answered; falling back to the discovered public IP only.");
            return null;
        }

        /// <summary>
        /// Detects a symmetric NAT by querying two different STUN servers and comparing
        /// the mapped port. Symmetric NAT assigns a different external port per
        /// destination, so hole punching cannot work and TURN relay is required.
        /// Returns (isSymmetric, primaryEndpoint). If STUN is unreachable, isSymmetric
        /// is false and the endpoint is null (caller falls back to a direct publish).
        /// </summary>
        public static async Task<(bool isSymmetric, IPEndPoint endpoint)> DetectNatAsync(NetManager manager)
        {
            LiteNetManagerConcrete lnl = ExtractLiteNetManager(manager);
            if (lnl == null) return (false, null);
            EnsureHook();

            IPEndPoint first = await QueryServerAsync(lnl, 0);
            if (first == null) return (false, null);
            IPEndPoint second = await QueryServerAsync(lnl, 1);
            if (second == null) return (false, first); // only one server answered — assume cone

            bool symmetric = first.Port != second.Port;
            if (symmetric)
                BasisDebug.Log($"[STUN] Symmetric NAT detected ({first.Port} vs {second.Port}); TURN relay required.", BasisDebug.LogTag.Networking);
            return (symmetric, first);
        }

        private static async Task<IPEndPoint> QueryServerAsync(LiteNetManagerConcrete lnl, int serverIndex)
        {
            if (serverIndex >= Servers.Length) return null;
            (string host, int port) = Servers[serverIndex];
            IPEndPoint serverEp = await ResolveAsync(host, port);
            if (serverEp == null) return null;

            byte[] txId = NewTransactionId(out string key);
            TaskCompletionSource<IPEndPoint> tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[key] = tcs;
            try
            {
                byte[] request = BuildBindingRequest(txId);
                lnl.SendStunBinding(request, serverEp);
                lnl.SendStunBinding(request, serverEp);
                Task finished = await Task.WhenAny(tcs.Task, Task.Delay(1500));
                return finished == tcs.Task ? tcs.Task.Result : null;
            }
            finally { _pending.TryRemove(key, out _); }
        }

        private static void OnStunResponse(IPEndPoint from, byte[] data, int size)
        {
            try
            {
                if (size < 20) return;
                // Bytes 8..19 are the transaction id (matches the key we stored).
                string key = BitConverter.ToString(data, 8, 12);
                if (!_pending.TryGetValue(key, out TaskCompletionSource<IPEndPoint> tcs)) return;

                IPEndPoint mapped = ParseXorMappedAddress(data, size);
                tcs.TrySetResult(mapped);
            }
            catch { /* malformed response — let the timeout handle it */ }
        }

        // ── STUN wire format ─────────────────────────────────────────────────

        private static readonly byte[] MagicCookie = { 0x21, 0x12, 0xA4, 0x42 };

        private static byte[] NewTransactionId(out string key)
        {
            // Time/Random are unavailable in workflow scripts but fine here (runtime).
            byte[] id = new byte[12];
            Guid g = Guid.NewGuid();
            byte[] gb = g.ToByteArray();
            Array.Copy(gb, 0, id, 0, 12);
            key = BitConverter.ToString(id, 0, 12);
            return id;
        }

        private static byte[] BuildBindingRequest(byte[] txId)
        {
            byte[] msg = new byte[20];
            msg[0] = 0x00; msg[1] = 0x01; // Binding Request
            msg[2] = 0x00; msg[3] = 0x00; // length 0
            Array.Copy(MagicCookie, 0, msg, 4, 4);
            Array.Copy(txId, 0, msg, 8, 12);
            return msg;
        }

        private static IPEndPoint ParseXorMappedAddress(byte[] data, int size)
        {
            int pos = 20; // attributes start after the 20-byte header
            while (pos + 4 <= size)
            {
                int type = (data[pos] << 8) | data[pos + 1];
                int len = (data[pos + 2] << 8) | data[pos + 3];
                int valuePos = pos + 4;
                if (valuePos + len > size) break;

                // 0x0020 XOR-MAPPED-ADDRESS (preferred), 0x0001 MAPPED-ADDRESS (legacy).
                if (type == 0x0020 || type == 0x0001)
                {
                    int family = data[valuePos + 1];
                    int rawPort = (data[valuePos + 2] << 8) | data[valuePos + 3];
                    if (type == 0x0020) rawPort ^= 0x2112; // XOR with cookie high 16 bits
                    if (family == 0x01) // IPv4
                    {
                        byte[] ip = new byte[4];
                        Array.Copy(data, valuePos + 4, ip, 0, 4);
                        if (type == 0x0020)
                        {
                            for (int i = 0; i < 4; i++) ip[i] ^= MagicCookie[i];
                        }
                        return new IPEndPoint(new IPAddress(ip), rawPort);
                    }
                }

                pos = valuePos + len;
                if ((len & 3) != 0) pos += 4 - (len & 3); // 32-bit alignment padding
            }
            return null;
        }

        private static async Task<IPEndPoint> ResolveAsync(string host, int port)
        {
            try
            {
                if (IPAddress.TryParse(host, out IPAddress literal))
                    return new IPEndPoint(literal, port);
                IPAddress[] addrs = await Dns.GetHostAddressesAsync(host);
                foreach (IPAddress a in addrs)
                {
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return new IPEndPoint(a, port);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[STUN] DNS resolve of {host} failed: {ex.Message}");
            }
            return null;
        }

        private static LiteNetManagerConcrete ExtractLiteNetManager(NetManager manager)
        {
            // The Basis transport wraps a concrete LiteNetLib.NetManager; reach it so
            // STUN goes out the real game socket.
            if (manager is Basis.Network.Core.LNLNetManager lnlWrapper)
                return lnlWrapper.manager;
            return null;
        }
    }
}
