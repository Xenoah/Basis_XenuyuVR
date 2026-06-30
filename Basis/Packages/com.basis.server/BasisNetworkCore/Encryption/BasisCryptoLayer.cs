#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Basis.Contrib.Crypto;
using LiteNetLib.Layers;

namespace Basis.Network.Core
{
	/// LiteNetLib のソケット境界で適用するエンドポイント単位の AEAD 暗号化。
	/// 各接続は X25519 ハンドシェイクで確立した ChaCha20-Poly1305 鍵ペアを
	/// 方向ごとに 1 本ずつ持つ。<see cref="BasisCryptoHandshake"/> を参照。
	///
	/// ユーザーデータを運ぶ packet property だけを暗号化する (Unreliable,
	/// Channeled, Merged)。接続確立、NAT、MTU、out-of-band probe packet は
	/// 平文のままにし、ハンドシェイク自体が鍵の存在に依存しないようにする。
	///
	/// 暗号化データグラムの wire layout:
	///   [byte 0 : LiteNetLib header (平文、AAD として認証)]
	///   [bytes 1..n : ciphertext]
	///   [16 bytes : Poly1305 tag]
	///   [8 bytes  : little-endian nonce counter]
	public sealed class BasisCryptoLayer : PacketLayerBase
	{
		public const int CounterSize = 8;
		public const int Overhead = BasisAeadCipher.TagSize + CounterSize;

		private const byte PropertyMask = 0x1F;
		// LiteNetLib.PacketProperty と同じ値: Unreliable = 0, Channeled = 1, Merged = 12。
		private const byte PropUnreliable = 0;
		private const byte PropChanneled = 1;
		private const byte PropMerged = 12;

		private sealed class Session
		{
			public BasisAeadCipher Send = null!;
			public BasisAeadCipher Recv = null!;
			public long SendCounter;
		}

		// address+port のみをキーにする。非 native socket では outbound で見える NetPeer、
		// inbound/install 時に見える素の IPEndPoint が異なる hash になり得るため、
		// この comparer で 3 者を同じ session に解決し、packet ごとの割り当てを避ける。
		private readonly ConcurrentDictionary<IPEndPoint, Session> _sessions
			= new ConcurrentDictionary<IPEndPoint, Session>(EndpointComparer.Instance);

		public BasisCryptoLayer() : base(Overhead) { }

		private sealed class EndpointComparer : IEqualityComparer<IPEndPoint>
		{
			public static readonly EndpointComparer Instance = new EndpointComparer();

			public bool Equals(IPEndPoint x, IPEndPoint y)
			{
				if (ReferenceEquals(x, y)) return true;
				if (x is null || y is null) return false;
				return x.Port == y.Port && x.Address.Equals(y.Address);
			}

			public int GetHashCode(IPEndPoint ep)
			{
				if (ep is null) return 0;
				unchecked { return (ep.Address.GetHashCode() * 397) ^ ep.Port; }
			}
		}

		public int SessionCount => _sessions.Count;

		/// <param name="initialSendCounter">
		/// 最初に使う nonce counter。同じ鍵を reconnect 用に再インストールする場合は、
		/// その鍵で過去に使った counter より必ず大きい値を渡し、
		/// (key, nonce) の組を再利用しないようにする。
		/// </param>
		public void SetEndpointKeys(IPEndPoint endpoint, byte[] sendKey, byte[] recvKey, long initialSendCounter = 0)
		{
			if (endpoint == null) return;
			var session = new Session
			{
				Send = new BasisAeadCipher(sendKey),
				Recv = new BasisAeadCipher(recvKey),
				SendCounter = initialSendCounter
			};
			if (_sessions.TryRemove(endpoint, out var old)) DisposeSession(old);
			_sessions[endpoint] = session;
		}

		public bool HasEndpoint(IPEndPoint endpoint) => endpoint != null && _sessions.ContainsKey(endpoint);

		public void RemoveEndpoint(IPEndPoint endpoint)
		{
			if (endpoint != null && _sessions.TryRemove(endpoint, out var session)) DisposeSession(session);
		}

		public void RemapEndpoint(IPEndPoint oldEndpoint, IPEndPoint newEndpoint)
		{
			if (oldEndpoint == null || newEndpoint == null) return;
			if (_sessions.TryRemove(oldEndpoint, out var session)) _sessions[newEndpoint] = session;
		}

		public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
		{
			if (length < 1) return;
			byte header = data[offset];
			if (!IsEncryptable((byte)(header & PropertyMask))) return;
			if (endPoint == null || !_sessions.TryGetValue(endPoint, out var session)) return;

			long counter = Interlocked.Increment(ref session.SendCounter);
			Span<byte> nonce = stackalloc byte[BasisAeadCipher.NonceSize];
			WriteCounter(nonce, counter);

			int tagOffset = offset + length;
			session.Send.Seal(nonce, header, data, offset + 1, length - 1, data, tagOffset);
			WriteCounterBytes(data, tagOffset + BasisAeadCipher.TagSize, counter);
			length += Overhead;
		}

		public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
		{
			if (length < 1) return;
			byte header = data[0];
			if (!IsEncryptable((byte)(header & PropertyMask))) return;
			if (endPoint == null || !_sessions.TryGetValue(endPoint, out var session)) return;

			if (length < 1 + Overhead)
			{
				length = 0;
				return;
			}

			int tagOffset = length - Overhead;
			int counterOffset = length - CounterSize;
			long counter = ReadCounterBytes(data, counterOffset);
			Span<byte> nonce = stackalloc byte[BasisAeadCipher.NonceSize];
			WriteCounter(nonce, counter);

			int payloadLength = tagOffset - 1;
			if (!session.Recv.Open(nonce, header, data, 1, payloadLength, data, tagOffset))
			{
				length = 0;
				return;
			}
			length -= Overhead;
		}

		private static bool IsEncryptable(byte property)
			=> property == PropUnreliable || property == PropChanneled || property == PropMerged;

		private static void DisposeSession(Session session)
		{
			session.Send.Dispose();
			session.Recv.Dispose();
		}

		private static void WriteCounter(Span<byte> nonce, long counter)
		{
			nonce.Clear();
			ulong c = (ulong)counter;
			nonce[0] = (byte)c;
			nonce[1] = (byte)(c >> 8);
			nonce[2] = (byte)(c >> 16);
			nonce[3] = (byte)(c >> 24);
			nonce[4] = (byte)(c >> 32);
			nonce[5] = (byte)(c >> 40);
			nonce[6] = (byte)(c >> 48);
			nonce[7] = (byte)(c >> 56);
		}

		private static void WriteCounterBytes(byte[] buffer, int offset, long counter)
		{
			ulong c = (ulong)counter;
			buffer[offset] = (byte)c;
			buffer[offset + 1] = (byte)(c >> 8);
			buffer[offset + 2] = (byte)(c >> 16);
			buffer[offset + 3] = (byte)(c >> 24);
			buffer[offset + 4] = (byte)(c >> 32);
			buffer[offset + 5] = (byte)(c >> 40);
			buffer[offset + 6] = (byte)(c >> 48);
			buffer[offset + 7] = (byte)(c >> 56);
		}

		private static long ReadCounterBytes(byte[] buffer, int offset)
		{
			ulong c = buffer[offset]
				| ((ulong)buffer[offset + 1] << 8)
				| ((ulong)buffer[offset + 2] << 16)
				| ((ulong)buffer[offset + 3] << 24)
				| ((ulong)buffer[offset + 4] << 32)
				| ((ulong)buffer[offset + 5] << 40)
				| ((ulong)buffer[offset + 6] << 48)
				| ((ulong)buffer[offset + 7] << 56);
			return (long)c;
		}
	}
}
