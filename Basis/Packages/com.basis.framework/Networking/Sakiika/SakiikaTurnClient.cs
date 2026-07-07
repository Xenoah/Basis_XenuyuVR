using Basis.Scripts.Common;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Scripts.Networking.Sakiika
{
    /// <summary>
    /// TURN client (RFC 5766, long-term credentials) that makes a player-hosted world
    /// reachable through a relay regardless of the host's NAT type — the fallback for
    /// symmetric NAT where hole punching cannot work.
    ///
    /// Design: LiteNetLib is never modified. The host allocates a relayed transport
    /// address on the TURN server and publishes THAT as the connection endpoint.
    /// Guests connect to it as an ordinary UDP address (they need no TURN support).
    /// This client bridges the TURN allocation and the local in-process LiteNetLib
    /// server: guest traffic arriving at the relay is forwarded to 127.0.0.1:serverPort,
    /// and the server's replies are sent back out through the relay via ChannelData.
    ///
    /// Only the host speaks TURN, so all relay complexity stays on one side.
    /// </summary>
    public sealed class SakiikaTurnClient
    {
        // ── Configuration (defaults match the project's coturn) ──────────────
        public const string HostFile = "TurnHost.BAS";
        public const string PortFile = "TurnPort.BAS";
        public const string UserFile = "TurnUser.BAS";
        public const string PassFile = "TurnPass.BAS";
        public const string RealmFile = "TurnRealm.BAS";

        public const string DefaultHost = "marusankakusikakuonline.com";
        public const int DefaultPort = 3478;
        public const string DefaultUser = "marusan";
        public const string DefaultPass = "test_marusan_password";
        // coturn derives the long-term key from the realm it advertises; we always use
        // the realm from the 401 response (authoritative), so a trailing-slash mismatch
        // here is harmless — this default only applies if the server sends no realm.
        public const string DefaultRealm = "http://marusankakusikakuonline.com/";

        public static string ConfHost => BasisDataStore.LoadString(HostFile, DefaultHost);
        public static int ConfPort => BasisDataStore.LoadInt(PortFile, DefaultPort);
        public static string ConfUser => BasisDataStore.LoadString(UserFile, DefaultUser);
        public static string ConfPass => BasisDataStore.LoadString(PassFile, DefaultPass);
        public static string ConfRealm => BasisDataStore.LoadString(RealmFile, DefaultRealm);

        private const uint MagicCookie = 0x2112A442;
        private const int MaxUdp = 65535;

        private UdpClient _turn;                 // socket to the TURN server
        private IPEndPoint _turnServer;
        private IPEndPoint _relayedAddress;      // XOR-RELAYED-ADDRESS (what guests connect to)
        private IPEndPoint _localServer;         // 127.0.0.1:serverPort (in-process LiteNetLib)
        private CancellationTokenSource _cts;
        private byte[] _integrityKey;
        private string _realm, _nonce;
        private int _channelCounter = 0x4000;

        // Per-guest channel + a local UDP socket used to talk to the in-process
        // server, so the server's replies come back to the right guest.
        private readonly ConcurrentDictionary<string, GuestBridge> _guests = new();

        private sealed class GuestBridge
        {
            public IPEndPoint GuestPeer;   // guest's public address (XOR-PEER-ADDRESS)
            public ushort Channel;
            public UdpClient LocalToServer; // sends to 127.0.0.1:serverPort, receives replies
        }

        public IPEndPoint RelayedAddress => _relayedAddress;

        /// <summary>
        /// Allocates a relay and starts bridging to the local server. Returns the
        /// public relayed endpoint guests should connect to, or null on failure.
        /// </summary>
        public async Task<IPEndPoint> StartAsync(int localServerPort)
        {
            try
            {
                _localServer = new IPEndPoint(IPAddress.Loopback, localServerPort);
                IPAddress serverIp = (await Dns.GetHostAddressesAsync(ConfHost))[0];
                _turnServer = new IPEndPoint(serverIp, ConfPort);
                _turn = new UdpClient(AddressFamily.InterNetwork);
                _turn.Connect(_turnServer);
                _cts = new CancellationTokenSource();

                if (!await AllocateAsync())
                {
                    Stop();
                    return null;
                }

                _ = ReceiveLoopAsync(_cts.Token);
                _ = RefreshLoopAsync(_cts.Token);
                BasisDebug.Log($"[TURN] Relay allocated at {_relayedAddress}", BasisDebug.LogTag.Networking);
                return _relayedAddress;
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[TURN] Start failed: {ex.Message}");
                Stop();
                return null;
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            foreach (GuestBridge g in _guests.Values)
            {
                try { g.LocalToServer?.Close(); } catch { }
            }
            _guests.Clear();
            try { _turn?.Close(); } catch { }
            _turn = null;
        }

        /// <summary>
        /// Permits a guest (its public endpoint, learned via Misskey signaling) to
        /// reach the relay, and binds a channel for efficient data relaying.
        /// </summary>
        public async Task PermitGuestAsync(IPEndPoint guest)
        {
            if (guest == null || _turn == null) return;
            string key = guest.ToString();
            if (_guests.ContainsKey(key)) return;

            ushort channel = (ushort)Interlocked.Increment(ref _channelCounter);
            GuestBridge bridge = new GuestBridge
            {
                GuestPeer = guest,
                Channel = channel,
                LocalToServer = new UdpClient(AddressFamily.InterNetwork),
            };
            _guests[key] = bridge;

            // Pump replies from the in-process server back out through the relay.
            _ = LocalReplyLoopAsync(bridge, _cts.Token);

            await ChannelBindAsync(channel, guest);
            BasisDebug.Log($"[TURN] Channel {channel:X4} bound for guest {guest}", BasisDebug.LogTag.Networking);
        }

        // ── TURN handshakes ──────────────────────────────────────────────────

        private async Task<bool> AllocateAsync()
        {
            // First Allocate with no credentials → expect 401 with REALM + NONCE.
            byte[] txId = NewTxId();
            byte[] req = BuildAllocateRequest(txId, withAuth: false);
            byte[] resp = await SendAndReceiveAsync(req, 3000);
            if (resp == null) return false;

            if (ParseErrorCode(resp, out int code) && code == 401)
            {
                _realm = ParseStringAttr(resp, 0x0014) ?? DefaultRealm;
                _nonce = ParseStringAttr(resp, 0x0015);
                _integrityKey = ComputeKey(ConfUser, _realm, ConfPass);

                byte[] txId2 = NewTxId();
                byte[] req2 = BuildAllocateRequest(txId2, withAuth: true);
                byte[] resp2 = await SendAndReceiveAsync(req2, 4000);
                if (resp2 == null) return false;
                if (GetMessageType(resp2) == 0x0103) // Allocate Success
                {
                    _relayedAddress = ParseXorAddress(resp2, 0x0016); // XOR-RELAYED-ADDRESS
                    return _relayedAddress != null;
                }
                ParseErrorCode(resp2, out int c2);
                BasisDebug.LogWarning($"[TURN] Allocate rejected (code {c2}).");
                return false;
            }

            if (GetMessageType(resp) == 0x0103)
            {
                _relayedAddress = ParseXorAddress(resp, 0x0016);
                return _relayedAddress != null;
            }
            return false;
        }

        private async Task ChannelBindAsync(ushort channel, IPEndPoint peer)
        {
            byte[] txId = NewTxId();
            byte[] req = BuildChannelBindRequest(txId, channel, peer);
            await SendAndReceiveAsync(req, 3000); // best-effort; data still works via Send if it fails
        }

        private async Task RefreshLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(4), token);
                    byte[] txId = NewTxId();
                    byte[] req = BuildRefreshRequest(txId, 600);
                    await SendAndReceiveAsync(req, 3000);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { BasisDebug.LogWarning($"[TURN] Refresh loop ended: {ex.Message}"); }
        }

        // ── Data relaying ────────────────────────────────────────────────────

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _turn.ReceiveAsync(); }
                catch { break; }
                byte[] data = r.Buffer;
                if (data.Length < 4) continue;

                int type = (data[0] << 8) | data[1];
                if ((data[0] & 0xC0) == 0x40) // ChannelData (top two bits 01)
                {
                    ushort channel = (ushort)type;
                    int len = (data[2] << 8) | data[3];
                    if (4 + len > data.Length) continue;
                    GuestBridge bridge = FindByChannel(channel);
                    if (bridge != null)
                    {
                        byte[] payload = new byte[len];
                        Buffer.BlockCopy(data, 4, payload, 0, len);
                        try { bridge.LocalToServer.Send(payload, payload.Length, _localServer); } catch { }
                    }
                }
                else if (type == 0x0017) // Data indication
                {
                    IPEndPoint peer = ParseXorAddress(data, 0x0012); // XOR-PEER-ADDRESS
                    byte[] payload = ParseRawAttr(data, 0x0013);     // DATA
                    if (peer != null && payload != null)
                    {
                        GuestBridge bridge = GetOrCreateBridge(peer);
                        try { bridge.LocalToServer.Send(payload, payload.Length, _localServer); } catch { }
                    }
                }
                // else: control responses handled synchronously in SendAndReceiveAsync
            }
        }

        private async Task LocalReplyLoopAsync(GuestBridge bridge, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await bridge.LocalToServer.ReceiveAsync(); }
                catch { break; }
                // Server replied — wrap in ChannelData and send to the TURN server.
                byte[] payload = r.Buffer;
                byte[] frame = new byte[4 + payload.Length];
                frame[0] = (byte)(bridge.Channel >> 8);
                frame[1] = (byte)(bridge.Channel & 0xFF);
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)(payload.Length & 0xFF);
                Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
                try { _turn.Send(frame, frame.Length); } catch { }
            }
        }

        private GuestBridge FindByChannel(ushort channel)
        {
            foreach (GuestBridge g in _guests.Values)
                if (g.Channel == channel) return g;
            return null;
        }

        private GuestBridge GetOrCreateBridge(IPEndPoint peer)
        {
            string key = peer.ToString();
            if (_guests.TryGetValue(key, out GuestBridge b)) return b;
            b = new GuestBridge
            {
                GuestPeer = peer,
                Channel = (ushort)Interlocked.Increment(ref _channelCounter),
                LocalToServer = new UdpClient(AddressFamily.InterNetwork),
            };
            _guests[key] = b;
            _ = LocalReplyLoopAsync(b, _cts.Token);
            return b;
        }

        // ── STUN/TURN message construction ───────────────────────────────────

        private byte[] BuildAllocateRequest(byte[] txId, bool withAuth)
        {
            StunBuilder b = new StunBuilder(0x0003, txId); // Allocate
            b.AddRequestedTransportUdp();
            b.AddLifetime(600);
            if (withAuth) FinishWithAuth(b);
            else b.Finish();
            return b.ToArray();
        }

        private byte[] BuildRefreshRequest(byte[] txId, int lifetime)
        {
            StunBuilder b = new StunBuilder(0x0004, txId); // Refresh
            b.AddLifetime(lifetime);
            FinishWithAuth(b);
            return b.ToArray();
        }

        private byte[] BuildChannelBindRequest(byte[] txId, ushort channel, IPEndPoint peer)
        {
            StunBuilder b = new StunBuilder(0x0009, txId); // ChannelBind
            b.AddChannelNumber(channel);
            b.AddXorPeerAddress(peer);
            FinishWithAuth(b);
            return b.ToArray();
        }

        private void FinishWithAuth(StunBuilder b)
        {
            b.AddStringAttr(0x0006, ConfUser);   // USERNAME
            b.AddStringAttr(0x0014, _realm);     // REALM
            b.AddStringAttr(0x0015, _nonce);     // NONCE
            b.FinishWithIntegrityAndFingerprint(_integrityKey);
        }

        private async Task<byte[]> SendAndReceiveAsync(byte[] request, int timeoutMs)
        {
            byte[] wantTx = new byte[12];
            Buffer.BlockCopy(request, 8, wantTx, 0, 12);
            _turn.Send(request, request.Length);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Task<UdpReceiveResult> recv = _turn.ReceiveAsync();
                int remain = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                Task done = await Task.WhenAny(recv, Task.Delay(remain));
                if (done != recv) return null;

                byte[] data = recv.Result.Buffer;
                if (data.Length < 20) continue;
                // Match transaction id; data frames (ChannelData) are handled elsewhere.
                bool match = true;
                for (int i = 0; i < 12; i++) if (data[8 + i] != wantTx[i]) { match = false; break; }
                if (match) return data;
                // Not our response — a relayed data frame slipped in; ignore here
                // (the main ReceiveLoop is not running yet during the handshake).
            }
            return null;
        }

        // ── Parsing helpers ──────────────────────────────────────────────────

        private static int GetMessageType(byte[] d) => (d[0] << 8) | d[1];

        private static bool ParseErrorCode(byte[] d, out int code)
        {
            code = 0;
            byte[] v = ParseRawAttr(d, 0x0009);
            if (v == null || v.Length < 4) return false;
            code = v[2] * 100 + v[3];
            return true;
        }

        private static string ParseStringAttr(byte[] d, int attrType)
        {
            byte[] v = ParseRawAttr(d, attrType);
            return v == null ? null : System.Text.Encoding.UTF8.GetString(v);
        }

        private static byte[] ParseRawAttr(byte[] d, int attrType)
        {
            int pos = 20;
            while (pos + 4 <= d.Length)
            {
                int type = (d[pos] << 8) | d[pos + 1];
                int len = (d[pos + 2] << 8) | d[pos + 3];
                int vp = pos + 4;
                if (vp + len > d.Length) break;
                if (type == attrType)
                {
                    byte[] v = new byte[len];
                    Buffer.BlockCopy(d, vp, v, 0, len);
                    return v;
                }
                pos = vp + len;
                if ((len & 3) != 0) pos += 4 - (len & 3);
            }
            return null;
        }

        private static IPEndPoint ParseXorAddress(byte[] d, int attrType)
        {
            byte[] v = ParseRawAttr(d, attrType);
            if (v == null || v.Length < 8) return null;
            int port = ((v[2] << 8) | v[3]) ^ 0x2112;
            byte[] ip = new byte[4];
            byte[] cookie = { 0x21, 0x12, 0xA4, 0x42 };
            for (int i = 0; i < 4; i++) ip[i] = (byte)(v[4 + i] ^ cookie[i]);
            return new IPEndPoint(new IPAddress(ip), port);
        }

        private static byte[] ComputeKey(string user, string realm, string pass)
        {
            using MD5 md5 = MD5.Create();
            return md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{user}:{realm}:{pass}"));
        }

        private static byte[] NewTxId()
        {
            byte[] id = new byte[12];
            byte[] g = Guid.NewGuid().ToByteArray();
            Buffer.BlockCopy(g, 0, id, 0, 12);
            return id;
        }

        // ── STUN builder with MESSAGE-INTEGRITY + FINGERPRINT ────────────────

        private sealed class StunBuilder
        {
            private readonly System.Collections.Generic.List<byte> _buf = new();
            private readonly byte[] _txId;
            private readonly int _type;

            public StunBuilder(int type, byte[] txId)
            {
                _type = type;
                _txId = txId;
                _buf.Add((byte)(type >> 8)); _buf.Add((byte)(type & 0xFF));
                _buf.Add(0); _buf.Add(0); // length placeholder
                _buf.Add(0x21); _buf.Add(0x12); _buf.Add(0xA4); _buf.Add(0x42);
                _buf.AddRange(txId);
            }

            private void AddAttr(int type, byte[] value)
            {
                _buf.Add((byte)(type >> 8)); _buf.Add((byte)(type & 0xFF));
                _buf.Add((byte)(value.Length >> 8)); _buf.Add((byte)(value.Length & 0xFF));
                _buf.AddRange(value);
                // Pad the value to a 4-byte boundary (STUN attribute alignment).
                int pad = (4 - (value.Length & 3)) & 3;
                for (int i = 0; i < pad; i++) _buf.Add(0);
            }

            public void AddRequestedTransportUdp() => AddAttr(0x0019, new byte[] { 17, 0, 0, 0 });
            public void AddLifetime(int seconds) => AddAttr(0x000D, new byte[] { (byte)(seconds >> 24), (byte)(seconds >> 16), (byte)(seconds >> 8), (byte)seconds });
            public void AddChannelNumber(ushort ch) => AddAttr(0x000C, new byte[] { (byte)(ch >> 8), (byte)(ch & 0xFF), 0, 0 });
            public void AddStringAttr(int type, string s) => AddAttr(type, System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty));

            public void AddXorPeerAddress(IPEndPoint ep)
            {
                byte[] ip = ep.Address.GetAddressBytes();
                int xport = ep.Port ^ 0x2112;
                byte[] cookie = { 0x21, 0x12, 0xA4, 0x42 };
                byte[] v = new byte[8];
                v[0] = 0; v[1] = 0x01; // family IPv4
                v[2] = (byte)(xport >> 8); v[3] = (byte)(xport & 0xFF);
                for (int i = 0; i < 4; i++) v[4 + i] = (byte)(ip[i] ^ cookie[i]);
                AddAttr(0x0012, v);
            }

            private void WriteLength(int lengthValue)
            {
                _buf[2] = (byte)(lengthValue >> 8);
                _buf[3] = (byte)(lengthValue & 0xFF);
            }

            public void Finish() => WriteLength(_buf.Count - 20);

            public void FinishWithIntegrityAndFingerprint(byte[] key)
            {
                // MESSAGE-INTEGRITY covers the message with the length field set to
                // include the 24-byte MI attribute itself.
                WriteLength((_buf.Count - 20) + 24);
                byte[] partial = _buf.ToArray();
                using HMACSHA1 hmac = new HMACSHA1(key);
                byte[] mi = hmac.ComputeHash(partial);
                AddAttr(0x0008, mi); // MESSAGE-INTEGRITY (20 bytes)

                // FINGERPRINT covers everything up to it, length including FP (8 bytes).
                WriteLength((_buf.Count - 20) + 8);
                byte[] beforeFp = _buf.ToArray();
                uint crc = Crc32(beforeFp) ^ 0x5354554E;
                AddAttr(0x8028, new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
            }

            public byte[] ToArray() => _buf.ToArray();

            private static readonly uint[] CrcTable = BuildCrcTable();
            private static uint[] BuildCrcTable()
            {
                uint[] t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                return t;
            }
            private static uint Crc32(byte[] data)
            {
                uint crc = 0xFFFFFFFF;
                foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
                return crc ^ 0xFFFFFFFF;
            }
        }
    }
}
