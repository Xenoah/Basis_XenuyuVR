using Basis.Network.Core;
using System;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct AvatarLoadDataMessage
        {
            public byte messageIndex;
            public ushort payloadSize;
            public byte[] payload;
            public ushort WhoSentUsThis;
            public void Deserialize(NetDataReader Writer)
            {
                // messageIndex を安全に読む。
                if (!Writer.TryGetByte(out messageIndex))
                {
                    throw new ArgumentException("Failed to read messageIndex.");
                }
                if (Writer.TryGetUShort(out WhoSentUsThis))
                {
                    throw new ArgumentException("Failed to read who sent us this!");
                }
                // recipientsSize を安全に読む。
                if (Writer.TryGetUShort(out payloadSize))
                {
                    // 負値相当や異常な size を防ぐ。
                    if (payloadSize > Writer.AvailableBytes / sizeof(ushort))
                    {
                        throw new ArgumentException($"Invalid recipientsSize: {payloadSize}");
                    }
                    if (payload == null || payload.Length != payloadSize)
                    {
                        payload = new byte[payloadSize];
                    }
                    if (!Writer.TryGetBytesWithLength(out payload))
                    {
                        throw new ArgumentException($"Failed to read payload!.");
                    }
                }
                else
                {
                    payload = null;
                }
            }

            public void Serialize(NetDataWriter Writer)
            {
                // messageIndex を書く。
                Writer.Put(messageIndex);
                Writer.Put(WhoSentUsThis);
                // recipientsSize を決めて書く。
                if (payload == null || payload.Length == 0)
                {
                    payloadSize = 0;
                }
                else
                {
                    payloadSize = (ushort)payload.Length;
                }
                Writer.Put(payloadSize);
                // recipients array があれば書く。
                if (payload != null && payload.Length > 0)
                {
                    Writer.Put(payload);
                }
            }
        }
    }
}
