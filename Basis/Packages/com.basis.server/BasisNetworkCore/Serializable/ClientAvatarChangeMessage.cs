using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct ClientAvatarChangeMessage
    {
        // Downloading - URL から download を試みる。hash も存在することを確認する。
        // BuiltIn - Unity 内の addressable として load する。
        public byte loadMode;
        public byte[] byteArray;
        // これを increment し、255 を超えたら wrap する。
        public byte LocalAvatarIndex;
        public void Deserialize(NetDataReader Writer)
        {
            // load mode を読む。
            loadMode = Writer.GetByte();
            // 指定 length で byte array を初期化する。
            ushort Length = Writer.GetUShort();
            if (Length == 0)
            {
                byteArray = null;
            }
            else
            {
                if (Length > Writer.AvailableBytes)
                {
                    byteArray = null;
                    throw new System.ArgumentException($"Avatar change length {Length} exceeds available data ({Writer.AvailableBytes} bytes).");
                }
                if (byteArray == null || byteArray.Length != Length)
                {
                    byteArray = new byte[Length];
                }

                // 各 byte を手動で array へ読む。
                Writer.GetBytes(byteArray, 0, byteArray.Length);
            }
            LocalAvatarIndex = Writer.GetByte();
        }
        public void Serialize(NetDataWriter Writer)
        {
            // load mode を書く。
            Writer.Put(loadMode);
            if (byteArray == null)
            {
                Writer.Put((ushort)0);
            }
            else
            {
                Writer.Put((ushort)byteArray.Length);
                Writer.Put(byteArray);
            }
            Writer.Put(LocalAvatarIndex);
        }
    }
}
