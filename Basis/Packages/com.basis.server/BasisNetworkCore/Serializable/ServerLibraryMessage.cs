using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// client 接続時に server が push する単一 library entry。
    /// Mode は client 側の BundledContentHolder.Mode に従う。0=Avatar、1=World、2=Prop。
    /// </summary>
    public struct ServerLibraryItem
    {
        public byte Mode;
        public string Url;
        public string Password;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Mode);
            writer.Put(Url ?? string.Empty);
            writer.Put(Password ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            Mode = reader.GetByte();
            Url = reader.GetString();
            Password = reader.GetString();
        }
    }

    /// <summary>
    /// default-library list 全体を包む。client 接続時に BasisNetworkCommons.ServerLibraryChannel 上で
    /// client ごとに一度送られる。empty array は有効で、default がないことを意味する。
    /// </summary>
    public struct ServerLibraryMessage
    {
        public ServerLibraryItem[] Items;

        public void Serialize(NetDataWriter writer)
        {
            int count = Items?.Length ?? 0;
            writer.Put((ushort)count);
            for (int i = 0; i < count; i++)
            {
                Items[i].Serialize(writer);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ushort count = reader.GetUShort();
            Items = new ServerLibraryItem[count];
            for (int i = 0; i < count; i++)
            {
                Items[i].Deserialize(reader);
            }
        }
    }
}
