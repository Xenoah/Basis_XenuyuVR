using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// server-to-client chat message。chat payload を sender の player ID で包む。
    /// </summary>
    public struct ServerChatMessage
    {
        public PlayerIdMessage playerIdMessage;
        public ChatMessage chatMessage;

        public void Deserialize(NetDataReader reader)
        {
            playerIdMessage.Deserialize(reader);
            chatMessage.Deserialize(reader);
        }

        public void Serialize(NetDataWriter writer)
        {
            playerIdMessage.Serialize(writer);
            chatMessage.Serialize(writer);
        }
    }
}
