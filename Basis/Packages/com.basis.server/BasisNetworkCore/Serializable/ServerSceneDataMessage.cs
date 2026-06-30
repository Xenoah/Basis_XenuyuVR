using Basis.Network.Core;
public static partial class SerializableBasis
{
    public struct ServerSceneDataMessage
    {
        public PlayerIdMessage playerIdMessage;
        public RemoteSceneDataMessage sceneDataMessage;

        public void Deserialize(NetDataReader Writer)
        {
            // playerIdMessage を読む。
            playerIdMessage.Deserialize(Writer);
            sceneDataMessage.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            // playerIdMessage と sceneDataMessage を書く。
            playerIdMessage.Serialize(Writer);
            sceneDataMessage.Serialize(Writer);
        }
    }
}
