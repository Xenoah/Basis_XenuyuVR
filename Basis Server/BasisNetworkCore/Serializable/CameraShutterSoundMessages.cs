using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// server -> clients: player が photo を撮った。position は不要。
    /// receiver はすでに tracking している PIP camera transform を lookup する。
    /// </summary>
    public struct CameraShutterSoundMessage
    {
        public ushort PlayerID;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
        }
    }

    /// <summary>
    /// server -> clients: player が countdown timer を開始した。
    /// receiver は同じ tick/shutter timing を local で replay する。
    /// </summary>
    public struct CameraCountdownMessage
    {
        public ushort PlayerID;
        public byte Seconds;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
            writer.Put(Seconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
            Seconds = reader.GetByte();
        }
    }

    /// <summary>
    /// client -> server: local player が countdown timer を開始した。
    /// </summary>
    public struct ClientCameraCountdownMessage
    {
        public byte Seconds;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Seconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            Seconds = reader.GetByte();
        }
    }
}
