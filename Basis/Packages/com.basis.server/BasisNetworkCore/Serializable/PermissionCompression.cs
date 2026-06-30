using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Basis.Network.Core
{
    /// <summary>
    /// wire 上で送られる extra permission string 用の Deflate compression。
    /// wire format: [byte flag][payload...]
    ///   flag 0 = raw UTF8, flag 1 = Deflate compressed。
    /// string は compression 前に NUL で join される。
    /// </summary>
    public static class PermissionCompression
    {
        private const int MaxDecompressedBytes = 1 * 1024 * 1024;

        /// <summary>
        /// permission string array を単一の byte payload に compress する。
        /// size を節約できる場合は Deflate を使い、それ以外は raw UTF8 を送る。
        /// </summary>
        public static byte[] CompressExtras(string[] strings)
        {
            if (strings == null || strings.Length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] raw = Encoding.UTF8.GetBytes(string.Join("\0", strings));

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    ds.Write(raw, 0, raw.Length);
                }
                deflated = ms.ToArray();
            }

            // 1 byte の flag overhead を含め、小さい方を選ぶ。
            if (deflated.Length < raw.Length)
            {
                byte[] result = new byte[1 + deflated.Length];
                result[0] = 1; // compressed。
                Buffer.BlockCopy(deflated, 0, result, 1, deflated.Length);
                return result;
            }
            else
            {
                byte[] result = new byte[1 + raw.Length];
                result[0] = 0; // raw。
                Buffer.BlockCopy(raw, 0, result, 1, raw.Length);
                return result;
            }
        }

        /// <summary>
        /// byte payload を元の string array に decompress する。
        /// </summary>
        public static string[] DecompressExtras(byte[] data, int expectedCount)
        {
            if (data == null || data.Length < 1 || expectedCount == 0)
            {
                return Array.Empty<string>();
            }

            byte flag = data[0];
            byte[] payload;

            if (flag == 1)
            {
                using (var ms = new MemoryStream(data, 1, data.Length - 1))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
                    try
                    {
                        int read;
                        while ((read = ds.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (output.Length + read > MaxDecompressedBytes)
                            {
                                return Array.Empty<string>();
                            }
                            output.Write(buffer, 0, read);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                    payload = output.ToArray();
                }
            }
            else
            {
                payload = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, payload, 0, payload.Length);
            }

            string joined = Encoding.UTF8.GetString(payload);
            string[] result = joined.Split('\0');

            return result;
        }
    }
}
