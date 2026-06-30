using Basis.Network.Core;
using System;

public static partial class SerializableBasis
{
    [Flags]
    public enum BasisMessageFlags : byte
    {
        None = 0,
        /// <summary>leading [messageId:2] prefix 付きで shared plugin channel (61-63) に乗る。core message はこれを clear し、dedicated channel を持つ。</summary>
        Multiplexed = 1 << 0,
        /// <summary>client はこの id の handler を bind する必要があり、できない場合 server は disconnect する。</summary>
        Required = 1 << 1,
        /// <summary>server はこの message を client へ送信できる。</summary>
        ServerToClient = 1 << 2,
        /// <summary>client はこの message を server へ送信できる。</summary>
        ClientToServer = 1 << 3,
    }

    /// <summary>
    /// connect 時に server が client へ供給する message registry の 1 row。
    /// stable Name を wire Id/Channel に bind し、shared constant table を recompile せずに
    /// handler を追加/削除できるようにする。
    /// </summary>
    [System.Serializable]
    public struct BasisMessageDescriptor
    {
        /// <summary>flat message id。core message では dedicated Channel (0-59) と同じ。multiplexed plugin message では [messageId:2] payload prefix として使う dense ushort。</summary>
        public ushort Id;
        /// <summary>payload schema version。client は local handler と比較し、不一致なら id を ignore または refuse する。</summary>
        public byte Version;
        /// <summary>この message が通る LiteNetLib channel (core は専用、multiplexed plugin は 61-63 のいずれか)。</summary>
        public byte Channel;
        /// <summary>BasisMessageFlags bitfield。</summary>
        public byte Flags;
        /// <summary>stable string identity。例: "basis.core.voice" または "com.acme.plugin.foo"。</summary>
        public string Name;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Id);
            writer.Put(Version);
            writer.Put(Channel);
            writer.Put(Flags);
            writer.Put(Name);
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetUShort(out Id)) { BNL.LogError("BasisMessageDescriptor: missing Id"); return false; }
            if (!reader.TryGetByte(out Version)) { BNL.LogError("BasisMessageDescriptor: missing Version"); return false; }
            if (!reader.TryGetByte(out Channel)) { BNL.LogError("BasisMessageDescriptor: missing Channel"); return false; }
            if (!reader.TryGetByte(out Flags)) { BNL.LogError("BasisMessageDescriptor: missing Flags"); return false; }
            if (!reader.TryGetString(out Name)) { BNL.LogError("BasisMessageDescriptor: missing Name"); return false; }
            return true;
        }
    }

    /// <summary>
    /// RegistryControlChannel 上で server から client へ送る (sub-type RegistrySub_Supply)。
    /// この session で server が理解する message type の full set。
    /// client は各 descriptor を Name によって local handler へ bind し、
    /// bind できないものは decode しない。
    /// </summary>
    [System.Serializable]
    public struct BasisMessageSupply
    {
        public BasisMessageDescriptor[] Descriptors;

        public readonly void Serialize(NetDataWriter writer)
        {
            ushort count = (ushort)(Descriptors?.Length ?? 0);
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                Descriptors[i].Serialize(writer);
            }
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetUShort(out ushort count))
            {
                BNL.LogError("BasisMessageSupply: missing count");
                Descriptors = Array.Empty<BasisMessageDescriptor>();
                return false;
            }

            Descriptors = new BasisMessageDescriptor[count];
            for (int i = 0; i < count; i++)
            {
                if (!Descriptors[i].Deserialize(reader))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// RegistryControlChannel 上で client から server へ送る (sub-type RegistrySub_Subscribe)。
    /// client が handler を持つ message id の list。
    /// server はこれを peer ごとに記録し、client が decode できない payload を skip し、
    /// Required id を enforce できるようにする。
    /// </summary>
    [System.Serializable]
    public struct BasisMessageSubscribe
    {
        public ushort[] Ids;

        public readonly void Serialize(NetDataWriter writer)
        {
            ushort count = (ushort)(Ids?.Length ?? 0);
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                writer.Put(Ids[i]);
            }
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetUShort(out ushort count))
            {
                BNL.LogError("BasisMessageSubscribe: missing count");
                Ids = Array.Empty<ushort>();
                return false;
            }

            Ids = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryGetUShort(out Ids[i]))
                {
                    BNL.LogError("BasisMessageSubscribe: truncated id list");
                    return false;
                }
            }
            return true;
        }
    }
}
