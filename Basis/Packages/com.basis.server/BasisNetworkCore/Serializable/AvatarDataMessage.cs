using Basis.Network.Core;

using System;
public static partial class SerializableBasis
{
    public struct AvatarDataMessage
    {
        public PlayerIdMessage PlayerIdMessage;
        public byte messageIndex;
        public byte AvatarLinkIndex;
        public ushort recipientsSize;
        /// <summary>
        /// null の場合は全員宛て。そうでなければ listed entry のみに送る。
        /// </summary>
        public ushort[] recipients;
        public byte[] payload;

        public void Deserialize(NetDataReader Writer)
        {
            PlayerIdMessage.Deserialize(Writer);
            if (!Writer.TryGetByte(out AvatarLinkIndex))
            {
                throw new ArgumentException("Failed to read AvatarLinkIndex.");
            }
            // messageIndex を安全に読む。
            if (!Writer.TryGetByte(out messageIndex))
            {
                throw new ArgumentException("Failed to read messageIndex.");
            }
            // recipientsSize を安全に読む。
            if (Writer.TryGetUShort(out recipientsSize))
            {
                // 負値相当や異常な size を防ぐ。
                if (recipientsSize > Writer.AvailableBytes / sizeof(ushort))
                {
                    throw new ArgumentException($"Invalid recipientsSize: {recipientsSize}");
                }
                if (recipients == null || recipients.Length != recipientsSize)
                {
                    recipients = new ushort[recipientsSize];
                }
               // BNL.Log("Recipients is " + recipientsSize);
                for (int index = 0; index < recipientsSize; index++)
                {
                    if (!Writer.TryGetUShort(out recipients[index]))
                    {
                        throw new ArgumentException($"Failed to read recipient at index {index}.");
                    }
                }

                // 残り byte を payload として読む。
                if (Writer.AvailableBytes > 0)
                {
                    if (payload != null && payload.Length == Writer.AvailableBytes)
                    {
                        Writer.GetBytes(payload, Writer.AvailableBytes);
                    }
                    else
                    {
                        payload = Writer.GetRemainingBytes();
                    }
                }
            }
            else
            {
                recipients = null;
                payload = null;
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            PlayerIdMessage.Serialize(Writer);
            Writer.Put(AvatarLinkIndex);
            // messageIndex を書く。
            Writer.Put(messageIndex);

            // recipientsSize を決めて書く。
            if (recipients == null || recipients.Length == 0)
            {
                recipientsSize = 0;
            }
            else
            {
                recipientsSize = (ushort)recipients.Length;
            }
            Writer.Put(recipientsSize);
           // BNL.Log("Recipients is " + recipientsSize);
            // recipients array があれば書く。
            if (recipients != null && recipients.Length > 0)
            {
                for (int index = 0; index < recipientsSize; index++)
                {
                    Writer.Put(recipients[index]);
                }
            }

            // payload があれば書く。
            if (payload != null && payload.Length > 0)
            {
                Writer.Put(payload);
            }
        }
    }
}
