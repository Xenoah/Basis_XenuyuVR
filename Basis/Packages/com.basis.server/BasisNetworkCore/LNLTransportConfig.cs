using System;

namespace Basis.Network.Core
{
    [Serializable]
    public sealed class LNLTransportConfig
    {
        /// <summary>Bump to force existing files to be rewritten; newly-added fields are healed automatically on load.</summary>
        public const int CurrentConfigVersion = 1;
        /// <summary>Schema version stamped into the file; 0 = pre-versioning, upgraded on load.</summary>
        public int ConfigVersion = 0;

#if UNITY_2017_1_OR_NEWER
        // Managed sockets inside the Unity player: the experimental native-socket
        // path silently drops loopback traffic under IL2CPP, which breaks host
        // mode (in-process server + client connecting to 127.0.0.1).
        public bool UseNativeSockets = false;
#else
        public bool UseNativeSockets = true;
#endif
        public bool NatPunchEnabled = true;
        public int NatPortPredictionRange = 32;
        public int PingInterval = 1500;
        public int DisconnectTimeout = 30000;
        public bool SimulatePacketLoss = false;
        public bool SimulateLatency = false;
        public int SimulationPacketLossChance = 10;
        public int SimulationMinLatency = 50;
        public int SimulationMaxLatency = 150;
        public int ReconnectDelay = 500;
        public int MaxConnectAttempts = 10;
        public bool ReuseAddresss = false;
        public bool DontRoute = false;
        public bool IPv6Enabled = true;
        public int MtuOverride = 0;
        public bool MtuDiscovery = true;
        public bool DisconnectOnUnreachable = false;
        public bool AllowPeerAddressChange = true;
        public int MultiSocketCount = 1;
    }
}
