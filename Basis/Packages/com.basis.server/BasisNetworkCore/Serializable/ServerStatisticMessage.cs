using Basis.Network.Core;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        /// <summary>
        /// server/client stats の snapshot。wire format を扱いやすくするため fixed layout。
        /// </summary>
        public struct ServerStatisticMessage
        {
            public byte[] Data;
            public void Serialize(NetDataWriter w)
            {
                w.Put(Data);
            }

            public void Deserialize(NetDataReader r)
            {
                Data = r.GetRemainingBytes();
            }
        }
    }
}
