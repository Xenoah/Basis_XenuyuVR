using Basis.Network.Core;
public static partial class SerializableBasis
{
    public struct ServerSideSyncPlayerMessage
    {
        public PlayerIdMessage playerIdMessage;
        public byte interval;
        public byte sequence;
        public LocalAvatarSyncMessage avatarSerialization;
        public void Deserialize(NetDataReader Writer)
        {
            playerIdMessage.Deserialize(Writer);//2bytes
            Writer.Get(out interval);//1 byte
            Writer.Get(out sequence);//1 byte
            avatarSerialization.Deserialize(Writer);
        }
        /// <summary>
        /// quality と additional-data の有無が channel から derive される場合に deserialize する (server->client path)。
        /// </summary>
        public void Deserialize(NetDataReader Writer, byte channelDerivedQuality, bool hasAdditionalData)
        {
            playerIdMessage.Deserialize(Writer);//2bytes
            Writer.Get(out interval);//1 byte
            Writer.Get(out sequence);//1 byte
            avatarSerialization.Deserialize(Writer, channelDerivedQuality, hasAdditionalData);
        }
        /// <summary>
        /// channel variant に基づき、byte / ushort playerID で deserialize する。
        /// </summary>
        public void Deserialize(NetDataReader Writer, byte channelDerivedQuality, bool hasAdditionalData, bool largeId)
        {
            playerIdMessage.Deserialize(Writer, largeId);
            Writer.Get(out interval);
            Writer.Get(out sequence);
            avatarSerialization.Deserialize(Writer, channelDerivedQuality, hasAdditionalData);
        }
        public void Serialize(NetDataWriter Writer)
        {
            playerIdMessage.Serialize(Writer);
            Writer.Put(interval);
            Writer.Put(sequence);
            avatarSerialization.Serialize(Writer, (Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality)avatarSerialization.DataQualityLevel);
        }
    }
}
