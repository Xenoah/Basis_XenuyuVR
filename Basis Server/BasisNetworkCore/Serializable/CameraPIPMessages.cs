using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// player の PIP camera が作成/破棄されたとき reliable に送る。
    /// server はこれを保存し、late joiner へ replay する。
    /// </summary>
    public struct CameraPIPStateMessage
    {
        public ushort PlayerID;
        public bool IsActive;
        // position と rotation は IsActive == true (initial spawn) の場合だけ送る。
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
            writer.Put(IsActive);
            if (IsActive)
            {
                writer.Put(PositionX);
                writer.Put(PositionY);
                writer.Put(PositionZ);
                writer.Put(RotationX);
                writer.Put(RotationY);
                writer.Put(RotationZ);
                writer.Put(RotationW);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
            IsActive = reader.GetBool();
            if (IsActive)
            {
                PositionX = reader.GetFloat();
                PositionY = reader.GetFloat();
                PositionZ = reader.GetFloat();
                RotationX = reader.GetFloat();
                RotationY = reader.GetFloat();
                RotationZ = reader.GetFloat();
                RotationW = reader.GetFloat();
            }
        }
    }

    /// <summary>
    /// PIP camera 用の position / rotation update。
    /// </summary>
    public struct CameraPIPPositionMessage
    {
        public ushort PlayerID;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
            writer.Put(PositionX);
            writer.Put(PositionY);
            writer.Put(PositionZ);
            writer.Put(RotationX);
            writer.Put(RotationY);
            writer.Put(RotationZ);
            writer.Put(RotationW);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
            PositionX = reader.GetFloat();
            PositionY = reader.GetFloat();
            PositionZ = reader.GetFloat();
            RotationX = reader.GetFloat();
            RotationY = reader.GetFloat();
            RotationZ = reader.GetFloat();
            RotationW = reader.GetFloat();
        }
    }

    /// <summary>
    /// client -> server: camera state change (PlayerID なし。server が peer から埋める)。
    /// </summary>
    public struct ClientCameraPIPStateMessage
    {
        public bool IsActive;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(IsActive);
            if (IsActive)
            {
                writer.Put(PositionX);
                writer.Put(PositionY);
                writer.Put(PositionZ);
                writer.Put(RotationX);
                writer.Put(RotationY);
                writer.Put(RotationZ);
                writer.Put(RotationW);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            IsActive = reader.GetBool();
            if (IsActive)
            {
                PositionX = reader.GetFloat();
                PositionY = reader.GetFloat();
                PositionZ = reader.GetFloat();
                RotationX = reader.GetFloat();
                RotationY = reader.GetFloat();
                RotationZ = reader.GetFloat();
                RotationW = reader.GetFloat();
            }
        }
    }

    /// <summary>
    /// client -> server: position / rotation update (PlayerID なし。server が peer から埋める)。
    /// </summary>
    public struct ClientCameraPIPPositionMessage
    {
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PositionX);
            writer.Put(PositionY);
            writer.Put(PositionZ);
            writer.Put(RotationX);
            writer.Put(RotationY);
            writer.Put(RotationZ);
            writer.Put(RotationW);
        }

        public void Deserialize(NetDataReader reader)
        {
            PositionX = reader.GetFloat();
            PositionY = reader.GetFloat();
            PositionZ = reader.GetFloat();
            RotationX = reader.GetFloat();
            RotationY = reader.GetFloat();
            RotationZ = reader.GetFloat();
            RotationW = reader.GetFloat();
        }
    }
}
