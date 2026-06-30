using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// admin が切り替えられる server-wide toggle。
    /// non-admin player 全員に対して avatar / prop / world loading を global に disable する。
    /// thread-safe。read/write には interlocked operation を使う。
    /// </summary>
    public static class BasisGlobalLockManager
    {
        // 0 = unlocked (loading allowed)、1 = locked (loading blocked)。
        private static int _avatarsLocked;
        private static int _propsLocked;
        private static int _worldsLocked;
        private static int _serversLocked;
        private static int _thirdPersonDisabled;
        private static int _additionalAvatarDataLock;
        private static int _cameraMetadataDisallowMask;
        private static int _playspaceMoverLocked;
        private static int _directConnectLocked;

        public static bool AvatarsLocked => Interlocked.CompareExchange(ref _avatarsLocked, 0, 0) == 1;
        public static bool PropsLocked => Interlocked.CompareExchange(ref _propsLocked, 0, 0) == 1;
        public static bool WorldsLocked => Interlocked.CompareExchange(ref _worldsLocked, 0, 0) == 1;
        public static bool ServersLocked => Interlocked.CompareExchange(ref _serversLocked, 0, 0) == 1;
        public static bool ThirdPersonDisabled => Interlocked.CompareExchange(ref _thirdPersonDisabled, 0, 0) == 1;
        public static bool AdditionalAvatarDataLock => Interlocked.CompareExchange(ref _additionalAvatarDataLock, 0, 0) == 1;
        public static byte CameraMetadataDisallowMask => (byte)Interlocked.CompareExchange(ref _cameraMetadataDisallowMask, 0, 0);
        public static bool PlayspaceMoverLocked => Interlocked.CompareExchange(ref _playspaceMoverLocked, 0, 0) == 1;
        public static bool DirectConnectLocked => Interlocked.CompareExchange(ref _directConnectLocked, 0, 0) == 1;

        /// <summary>
        /// server configuration から initial lock state を seed する。
        /// client thread が動き出す前、startup 時に一度だけ呼ぶ。
        /// </summary>
        public static void InitializeFromConfig(Configuration config)
        {
            Interlocked.Exchange(ref _avatarsLocked, config.AvatarsLocked ? 1 : 0);
            Interlocked.Exchange(ref _propsLocked, config.PropsLocked ? 1 : 0);
            Interlocked.Exchange(ref _worldsLocked, config.WorldsLocked ? 1 : 0);
            Interlocked.Exchange(ref _serversLocked, config.ServersLocked ? 1 : 0);
            Interlocked.Exchange(ref _thirdPersonDisabled, config.ThirdPersonDisabled ? 1 : 0);
            Interlocked.Exchange(ref _additionalAvatarDataLock, config.AdditionalAvatarDataLock ? 1 : 0);
            Interlocked.Exchange(ref _cameraMetadataDisallowMask, config.CameraMetadataDisallowMask);
            Interlocked.Exchange(ref _playspaceMoverLocked, config.PlayspaceMoverLocked ? 1 : 0);
            Interlocked.Exchange(ref _directConnectLocked, config.DirectConnectLocked ? 1 : 0);
        }

        /// <summary>
        /// avatar loading を toggle する。新しい state を返す (true = locked)。
        /// </summary>
        public static bool ToggleAvatars() => Toggle(ref _avatarsLocked);

        /// <summary>
        /// prop loading を toggle する。新しい state を返す (true = locked)。
        /// </summary>
        public static bool ToggleProps() => Toggle(ref _propsLocked);

        /// <summary>
        /// world loading を toggle する。新しい state を返す (true = locked)。
        /// </summary>
        public static bool ToggleWorlds() => Toggle(ref _worldsLocked);

        /// <summary>
        /// server-share dropping を toggle する。新しい state を返す (true = locked)。
        /// </summary>
        public static bool ToggleServers() => Toggle(ref _serversLocked);

        /// <summary>
        /// third-person camera availability を toggle する。新しい state を返す (true = disabled)。
        /// </summary>
        public static bool ToggleThirdPerson() => Toggle(ref _thirdPersonDisabled);

        /// <summary>
        /// inbound avatar sync message に対する network-side の AdditionalAvatarDatas strip を toggle する。
        /// 新しい state を返す (true = additional data stripped)。
        /// </summary>
        public static bool ToggleAdditionalAvatarDataLock() => Toggle(ref _additionalAvatarDataLock);

        /// <summary>
        /// non-admin playspace-mover lockout を toggle する。新しい state を返す (true = locked)。
        /// </summary>
        public static bool TogglePlayspaceMover() => Toggle(ref _playspaceMoverLocked);

        /// <summary>
        /// non-admin direct-connect (P2P) lockout を toggle する。新しい state を返す (true = locked)。
        /// </summary>
        public static bool ToggleDirectConnect() => Toggle(ref _directConnectLocked);

        /// <summary>
        /// camera photo-metadata の per-category disallow mask を set する (set bit = disallowed)。
        /// </summary>
        public static void SetCameraMetadataDisallowMask(byte mask) => Interlocked.Exchange(ref _cameraMetadataDisallowMask, mask);

        private static bool Toggle(ref int field)
        {
            int prev, next;
            do
            {
                prev = field;
                next = prev == 0 ? 1 : 0;
            }
            while (Interlocked.CompareExchange(ref field, next, prev) != prev);
            return next == 1;
        }

        /// <summary>
        /// 現在の global lock state を specific peer へ送る。
        /// new player 接続時に、何が locked か知らせるために使う。
        /// </summary>
        public static void SendLockStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetLockState);
            writer.Put(AvatarsLocked);
            writer.Put(PropsLocked);
            writer.Put(WorldsLocked);
            writer.Put(ServersLocked);
            // ServersLocked の後に append。4 bool だけ読む older client も clean に parse できる。
            writer.Put(ThirdPersonDisabled);
            // ThirdPersonDisabled の後に append。5 bool を parse する older client も動作する。
            writer.Put(AdditionalAvatarDataLock);
            // AdditionalAvatarDataLock (1 byte) の後に append。6 bool を parse する older client も動作する。
            writer.Put(CameraMetadataDisallowMask);
            // CameraMetadataDisallowMask (1 byte) の後に append。手前で reading を止める older client も parse できる。
            writer.Put((byte)NetworkServer.Configuration.BasisUserRestrictionMode);
            // BasisUserRestrictionMode の後に append。手前で reading を止める older client も parse できる。
            writer.Put(PlayspaceMoverLocked);
            writer.Put(DirectConnectLocked);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// 現在の lock state を connected client 全員へ broadcast する。
        /// </summary>
        public static void BroadcastLockState()
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetLockState);
            writer.Put(AvatarsLocked);
            writer.Put(PropsLocked);
            writer.Put(WorldsLocked);
            writer.Put(ServersLocked);
            // ServersLocked の後に append。4 bool だけ読む older client も clean に parse できる。
            writer.Put(ThirdPersonDisabled);
            // ThirdPersonDisabled の後に append。5 bool を parse する older client も動作する。
            writer.Put(AdditionalAvatarDataLock);
            // AdditionalAvatarDataLock (1 byte) の後に append。6 bool を parse する older client も動作する。
            writer.Put(CameraMetadataDisallowMask);
            // CameraMetadataDisallowMask (1 byte) の後に append。手前で reading を止める older client も parse できる。
            writer.Put((byte)NetworkServer.Configuration.BasisUserRestrictionMode);
            // BasisUserRestrictionMode の後に append。手前で reading を止める older client も parse できる。
            writer.Put(PlayspaceMoverLocked);
            writer.Put(DirectConnectLocked);
            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.AdminChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
        }
    }
}
