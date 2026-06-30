using Basis.Network.Core;
namespace Basis.Network.Server.Auth
{
    /// <summary>
    /// 認証できるかどうかを判定するための interface。
    /// (password が正しいかどうか)
    /// </summary>
    public interface IAuth
    {
        public bool IsAuthenticated(byte[] BytesMsg);
    }
    public interface IAuthIdentity
    {
        /// <summary>
        /// user の identity を取得するために使う interface。
        /// player の UUID はここで扱う identity になる。
        /// </summary>
        public void ProcessConnection(Configuration Configuration, ConnectionRequest ConnectionRequest, NetPeer NetPeer);
        public void DeInitialize();
        public void RemoveConnection(int NetPeer);
        public bool NetIDToUUID(NetPeer Peer, out string UUID);
        public bool UUIDToNetID(string UUID, out int Peer);

        public static bool HasFileSupport = false;
    }
}
