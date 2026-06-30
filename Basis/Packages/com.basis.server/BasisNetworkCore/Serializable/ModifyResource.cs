using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// すでに spawn 済みの resource 上の flag を変える client→server request であり、
    /// 全 client に変更を適用する server→client broadcast でもある。static-lock state を運ぶ。
    /// <see cref="Static"/> は item を全員に対して固定し、<see cref="StaticAdminLocked"/> は
    /// admin tier を示す。creator ではなく moderator のみが変更または解除できる。
    /// </summary>
    public struct ModifyResource
    {
        /// <summary>変更対象となる spawned resource の unique network id。</summary>
        public string LoadedNetID;
        /// <summary>0 = GameObject、1 = Scene。<see cref="LocalLoadResource.Mode"/> と一致する。</summary>
        public byte Mode;
        /// <summary>希望する frozen state。prop は pickup disabled + frozen、vehicle は locked out。</summary>
        public bool Static;
        /// <summary>希望する admin tier。true の場合は moderator のみ変更可能で、Static を含意する。</summary>
        public bool StaticAdminLocked;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(LoadedNetID);
            writer.Put(Mode);
            writer.Put(Static);
            writer.Put(StaticAdminLocked);
        }
        public void Deserialize(NetDataReader reader)
        {
            LoadedNetID = reader.GetString();
            Mode = reader.GetByte();
            Static = reader.GetBool();
            StaticAdminLocked = reader.GetBool();
        }
    }
}
