using System.IO.Compression;

namespace Basis.Scripts.BasisSdk.Players
{
    [Serializable]
    public struct BasisAvatarNetworkLoad
    {
        public string URL;
        public string UnlockPassword;

        /// <summary>
        /// custom string serialization と DeflateStream compression を使い、structure を compressed byte data に encode する。
        /// </summary>
        public byte[] EncodeToBytes()
        {
            using var memoryStream = new MemoryStream();
            using (var writer = new BinaryWriter(memoryStream))
            {
                WriteString(writer, URL);
                WriteString(writer, UnlockPassword);
            }

            byte[] rawData = memoryStream.ToArray();

            using var compressedStream = new MemoryStream();
            using (var deflateStream = new DeflateStream(compressedStream, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                deflateStream.Write(rawData, 0, rawData.Length);
            }

            return compressedStream.ToArray();
        }

        /// <summary>
        /// custom string deserialization と DeflateStream decompression を使い、compressed byte data から structure に decode する。
        /// </summary>
        public static BasisAvatarNetworkLoad DecodeFromBytes(byte[] compressedData)
        {
            using var compressedStream = new MemoryStream(compressedData);
            using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            deflateStream.CopyTo(decompressedStream);

            byte[] rawData = decompressedStream.ToArray();

            using var memoryStream = new MemoryStream(rawData);
            using var reader = new BinaryReader(memoryStream);

            return new BasisAvatarNetworkLoad
            {
                URL = ReadString(reader),
                UnlockPassword = ReadString(reader)
            };
        }

        /// <summary>
        /// string を ushort の length 付きで BinaryWriter に書き込む。
        /// </summary>
        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write((ushort)bytes.Length); // length を ushort として書き込む。
            writer.Write(bytes); // string byte を書き込む。
        }

        /// <summary>
        /// ushort として保存された length に基づいて BinaryReader から string を読む。
        /// </summary> 
        private static string ReadString(BinaryReader reader)
        {
            ushort length = reader.ReadUInt16(); // length を読む。
            byte[] bytes = reader.ReadBytes(length); // string byte を読む。
            return System.Text.Encoding.UTF8.GetString(bytes); // string に戻す。
        }
    }
}
