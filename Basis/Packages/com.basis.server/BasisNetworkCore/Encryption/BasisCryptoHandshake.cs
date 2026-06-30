#nullable enable

using System;
using System.Text;
using Basis.Contrib.Crypto;

namespace Basis.Network.Core
{
	/// 暗号化された peer-to-peer (direct) link のための X25519 + HKDF-SHA256 鍵合意。
	/// 2 つの peer はサーバーの signalling channel 経由で一時公開鍵を交換し、
	/// 同じ 2 本の方向別鍵を導出する。ECDH(myPriv, peerPub) は対称であり、
	/// transcript (両方の公開鍵) は channel binding のため HKDF salt に混ぜ込む。
	public static class BasisCryptoHandshake
	{
		public const int PublicKeySize = BasisX25519.KeySize;
		public const int PrivateKeySize = BasisX25519.KeySize;
		public const int KeySize = BasisAeadCipher.KeySize;

		private static readonly byte[] InfoAB = Encoding.ASCII.GetBytes("basis-crypto-v1-ab");
		private static readonly byte[] InfoBA = Encoding.ASCII.GetBytes("basis-crypto-v1-ba");

		public static void GenerateKeyPair(out byte[] privateKey, out byte[] publicKey)
			=> BasisX25519.GenerateKeyPair(out privateKey, out publicKey);

		/// peer-to-peer link の方向別鍵を導出する。役割は公開鍵の並び順で決めるため、
		/// 追加 signalling なしに両端で同じ結果になる。
		public static bool DerivePeerKeys(
			ReadOnlySpan<byte> myPrivate,
			ReadOnlySpan<byte> myPublic,
			ReadOnlySpan<byte> peerPublic,
			out byte[] sendKey,
			out byte[] recvKey)
		{
			sendKey = Array.Empty<byte>();
			recvKey = Array.Empty<byte>();
			try
			{
				int cmp = Compare(myPublic, peerPublic);
				if (cmp == 0) return false;
				bool iAmA = cmp < 0;

				ReadOnlySpan<byte> aPub = iAmA ? myPublic : peerPublic;
				ReadOnlySpan<byte> bPub = iAmA ? peerPublic : myPublic;

				byte[] shared = BasisX25519.Agree(myPrivate, peerPublic);
				byte[] salt = Concat(aPub, bPub);
				byte[] keyAB = BasisHkdf.DeriveKey(shared, salt, InfoAB, KeySize);
				byte[] keyBA = BasisHkdf.DeriveKey(shared, salt, InfoBA, KeySize);

				if (iAmA)
				{
					sendKey = keyAB;
					recvKey = keyBA;
				}
				else
				{
					sendKey = keyBA;
					recvKey = keyAB;
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
		{
			var result = new byte[a.Length + b.Length];
			a.CopyTo(result);
			b.CopyTo(result.AsSpan(a.Length));
			return result;
		}

		private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
		{
			int n = Math.Min(a.Length, b.Length);
			for (int i = 0; i < n; i++)
			{
				int d = a[i] - b[i];
				if (d != 0) return d;
			}
			return a.Length - b.Length;
		}
	}
}
