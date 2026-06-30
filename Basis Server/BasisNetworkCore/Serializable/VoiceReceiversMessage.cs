using System;
using System.Buffers;
using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct VoiceReceiversMessage
    {
        // data が壊れている場合に巨大 allocation を避けるための hard cap。
        private const int MaxUsers = ushort.MaxValue;

        public ushort[] Users;
        public int UsersLength; // 実際の count。rented array はより大きい場合がある。

        /// <param name="largeCount">
        /// false = byte count (AudioRecipientsChannel, 255 recipients 以下)。
        /// true  = ushort count (AudioRecipientsLargeChannel, 255 recipients 超)。
        /// </param>
        public void Deserialize(NetDataReader reader, bool largeCount)
        {
            int remainingBytes = reader.AvailableBytes;

            if (remainingBytes <= 0)
            {
                Users = Array.Empty<ushort>();
                return;
            }

            int countSize = largeCount ? sizeof(ushort) : sizeof(byte);
            if (remainingBytes < countSize)
            {
                BNL.LogError(
                    $"VoiceReceiversMessage: not enough bytes for length. " +
                    $"Remaining={remainingBytes}");
                SkipRemaining(reader);
                Users = Array.Empty<ushort>();
                return;
            }

            ushort count = largeCount ? reader.GetUShort() : reader.GetByte();

            if (count == 0)
            {
                // 明示的な "no recipients": consumer (BasisSavedState.AddLastData) が
                // 実際の clear として扱えるよう、non-null の empty array を使う。
                // null Users は "parse 不能 / corrupted" を意味し、consumer が意図的に無視して
                // last-known recipients を保持できるようにする。
                ReturnPool();
                Users = Array.Empty<ushort>();
                UsersLength = 0;
                return;
            }

            if (count > MaxUsers)
            {
                BNL.LogError($"VoiceReceiversMessage: reported count={count} exceeds MaxUsers={MaxUsers}. Possible protocol mismatch or corrupted packet.");
                SkipRemaining(reader);
                ReturnPool();
                Users = null;
                UsersLength = 0;
                return;
            }

            int bytesNeeded = count * sizeof(ushort);

            if (reader.AvailableBytes < bytesNeeded)
            {
                BNL.LogError($"VoiceReceiversMessage: count={count} needs {bytesNeeded} bytes, but only {reader.AvailableBytes} available. Protocol mismatch?");
                SkipRemaining(reader);
                ReturnPool();
                Users = null;
                UsersLength = 0;
                return;
            }

            // 新しい array を rent する前に、以前の rented array を返却する。
            ReturnPool();
            Users = ArrayPool<ushort>.Shared.Rent(count);
            UsersLength = count;
            for (int i = 0; i < count; i++)
            {
                Users[i] = reader.GetUShort();
            }
        }

        /// <summary>
        /// <c>Serialize(writer, largeCount: true)</c> と同等で、常に 2-byte (ushort) count を書く。
        /// <see cref="BasisNetworkCommons.AudioRecipientsLargeChannel"/> (channel 39) で送る場合のみ使う。
        /// <see cref="BasisNetworkCommons.AudioRecipientsChannel"/> (channel 5) では、
        /// server が期待する length field width と一致させるため、largeCount overload を <c>false</c> で呼ぶ。
        /// </summary>
        public void Serialize(NetDataWriter writer)
        {
            Serialize(writer, largeCount: true);
        }

        /// <param name="largeCount">
        /// packet を送る channel と一致させる必要がある。
        /// false = byte count (AudioRecipientsChannel, 255 recipients 以下)。
        /// true  = ushort count (AudioRecipientsLargeChannel, 最大 65535 recipients)。
        /// 不一致だと wire format が desync し、server は "Protocol mismatch?" を log する。
        /// </param>
        public void Serialize(NetDataWriter writer, bool largeCount)
        {
            int usersLength = Users?.Length ?? 0;
            int maxCount = largeCount ? ushort.MaxValue : byte.MaxValue;

            if (usersLength == 0)
            {
                // read side との同期を保つため、0-length は必ず書く。
                if (largeCount) writer.Put((ushort)0);
                else writer.Put((byte)0);
                return;
            }

            if (usersLength > maxCount)
            {
                BNL.LogError(
                    $"VoiceReceiversMessage: Users.Length={usersLength} exceeds " +
                    $"{(largeCount ? "ushort.MaxValue" : "byte.MaxValue")} for this channel. Truncating.");
            }

            int count = Math.Min(usersLength, maxCount);
            if (largeCount) writer.Put((ushort)count);
            else writer.Put((byte)count);

            for (int i = 0; i < count; i++)
            {
                writer.Put(Users[i]);
            }
        }

        /// <summary>
        /// rented array を pool へ返却する。peer へ解決した後に呼ぶ。
        /// </summary>
        public void ReturnPool()
        {
            // pool に返すのは pool から rent された array (length > 0) のみ。
            // Array.Empty<ushort>() の "explicit empty" sentinel や null は返さない。
            if (Users != null && Users.Length != 0)
            {
                ArrayPool<ushort>.Shared.Return(Users);
            }
            Users = null;
            UsersLength = 0;
        }

        private static void SkipRemaining(NetDataReader reader)
        {
            if (reader.AvailableBytes > 0)
            {
                reader.SkipBytes(reader.AvailableBytes);
            }
        }
    }
}
