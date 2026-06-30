using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using BasisDidLink;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static BasisPermissions.PermissionManager;

public static class NetworkServer
{
    public static EventBasedNetListener Listener;
    public static NetManager Server;
    public static ConcurrentDictionary<int, NetPeer> AuthenticatedPeers = new();
    public static readonly object AuthenticatedPeerTag = new object();
    public static Configuration Configuration;
    /// <summary>
    /// <see cref="Configuration.BasisUserRestrictionMode"/> が <c>AllowList</c> のとき、
    /// <see cref="BasisServerHandle.BasisServerHandleEvents.OnNetworkAccepted"/> で参照する allow-list。
    /// admin-panel からの変更が restart 後も残るよう、config folder 下の BasisAllowList.txt を backing store にする。
    /// </summary>
    public static BasisNetworkServer.Security.BasisAllowList AllowList;
    public static BasisNetworkServer.Security.BasisBanList BanList;
    // connect/disconnect 時に再構築する cached snapshot。broadcast ごとの ToArray() allocation を避ける。
    private static volatile NetPeer[] _peerSnapshot = Array.Empty<NetPeer>();
    // read-then-publish を保護する。OnNetworkAccepted は並列 DID-auth continuation 上で走るため、
    // 同時 join により _peerSnapshot が古い array へ lost-update し、peer を落とす可能性がある。
    private static readonly object _peerSnapshotLock = new object();
    public static NetPeer[] PeerSnapshot => _peerSnapshot;

    public static void RebuildPeerSnapshot()
    {
        lock (_peerSnapshotLock)
        {
            _peerSnapshot = AuthenticatedPeers.Values.ToArray();
        }
    }

    // 集約された NetDataWriter pool。server code 全体の単一の正とする。
    // player 数の spike 後に writer が無制限に溜まらないよう上限を設ける。
    private static readonly ConcurrentQueue<NetDataWriter> _writerPool = new();
    private const int MaxPooledWriters = 64;
    public static NetDataWriter RentWriter(int initialCapacity = 208)
    {
        if (_writerPool.TryDequeue(out var writer)) return writer;
        return new NetDataWriter(true, initialCapacity);
    }
    public static void ReturnWriter(NetDataWriter writer)
    {
        writer.Reset();
        if (_writerPool.Count < MaxPooledWriters)
        {
            _writerPool.Enqueue(writer);
        }
        // else: 破棄する。GC に回収させ、pool の上限を維持する
    }

    public static IAuth Auth;
    public static IAuthIdentity AuthIdentity;
    public static int HighQualityLength;
    #region Server Entry Point

    public static void StartServer(Configuration configuration)
    {
        StopServer();
        Configuration = configuration;

        // Rejoin-only lockdown は「今ここにいる player」を意味し、restart 後には意味がない。
        // RejoinOnly が永続化されると空 snapshot で起動して全員を締め出すため、Normal へ戻す。
        if (configuration.BasisUserRestrictionMode == BasisNetworkCore.Security.BasisUserRestrictionMode.RejoinOnly)
            configuration.BasisUserRestrictionMode = BasisNetworkCore.Security.BasisUserRestrictionMode.Normal;

        HighQualityLength = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
        InitializePulseSettings();
        InitializeAuth();
        BasisHeadlessConnectionPolicyManager.InitializeFromConfig(configuration.DisallowHeadless);
        BasisNetworkServer.Security.BasisGlobalLockManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisCrashReportStateManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisAudioRangeLimitManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisAvatarScaleLimitManager.InitializeFromConfig(configuration);
        BasisNetworkServer.Security.BasisResourceLimitManager.InitializeFromConfig(configuration);
        SetupServer(configuration);
        SubscribeEvents(Configuration);

        if (configuration.EnableStatistics)
        {
            BasisStatistics.StartWorkerThread(Server);
        }

        BasisNetworkUdpDropMonitor.Start();

        BNL.Log("Server Worker Threads Booted");
    }

    public static void StopServer()
    {
        if (Server == null) return;
        try
        {
            Server.Stop();
        }
        catch (Exception ex)
        {
            BNL.LogWarning($"NetworkServer.StopServer failed: {ex.Message}");
        }
        BasisNetworkUdpDropMonitor.Stop();
        Server = null;
        Listener = null;
        AuthenticatedPeers.Clear();
        _peerSnapshot = Array.Empty<NetPeer>();
    }

    private static void InitializePulseSettings()
    {
        BasisServerReductionSystemEvents.BSRBaseMultiplier = Configuration.BSRBaseMultiplier;
        BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval = Configuration.BSRSMillisecondDefaultInterval;
        BasisServerReductionSystemEvents.BSRSIncreaseRate = Configuration.BSRSIncreaseRate;
        BasisServerReductionSystemEvents.HighDistanceSq = Configuration.HighQualityDistance * Configuration.HighQualityDistance;
        BasisServerReductionSystemEvents.MediumDistanceSq = Configuration.MediumQualityDistance * Configuration.MediumQualityDistance;
        BasisServerReductionSystemEvents.LowDistanceSq = Configuration.LowQualityDistance * Configuration.LowQualityDistance;
        BasisServerReductionSystemEvents.EnableAvatarBundleCompression = Configuration.EnableAvatarBundleCompression;
        BasisServerReductionSystemEvents.AvatarBundleMinMessages = Configuration.AvatarBundleMinMessages;
        BasisServerReductionSystemEvents.AvatarBundleMinBytes = Configuration.AvatarBundleMinBytes;
        BSRProfiler.Enabled = Configuration.EnableBSRProfiling;
        BNL.Log($"[BSR] AvatarBundleCompression={Configuration.EnableAvatarBundleCompression} (minMsgs={Configuration.AvatarBundleMinMessages}, minBytes={Configuration.AvatarBundleMinBytes})");
    }

    private static void InitializeAuth()
    {
        var HasFileSupport = Configuration.HasFileSupport;
        BasisPlayerModeration.UseFileOnDisc = HasFileSupport;
        IAuthIdentity.HasFileSupport = HasFileSupport;

        Auth = new PasswordAuth(Configuration.Password ?? string.Empty);
        AuthIdentity = new BasisDIDAuthIdentity();

        if (HasFileSupport)
        {
            // permissions を他の config file と同じ場所に置く
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string configDir = Path.Combine(baseDir, Configuration.ConfigFolderName);

            Directory.CreateDirectory(configDir);
            PermissionIntegration.Init(Path.Combine(configDir, "permissions.xml"));
            AllowList = new BasisNetworkServer.Security.BasisAllowList(Path.Combine(configDir, "BasisAllowList.txt"));
            BanList = new BasisNetworkServer.Security.BasisBanList(Path.Combine(configDir, "BasisBanList.txt"));
        }
        else
        {
            PermissionIntegration.InitWithoutDisc();
            // host が disk support を無効化している場合の best-effort な in-memory allowlist。
            AllowList = new BasisNetworkServer.Security.BasisAllowList();
            BanList = new BasisNetworkServer.Security.BasisBanList();
        }
    }

    private static void SubscribeEvents(Configuration Configuration)
    {
        BasisServerHandleEvents.SubscribeServerEvents();
        BasisPlayerModeration.LoadBannedPlayers();
        BasisNetworkChat.LoadWordFilter(Configuration);
        BasisNetworkStackRegistry.RegisterIntroducerFactory(
            BasisNetworkStackRegistry.LiteNetLibId,
            _ => new BasisNetworkServer.LNLPeerIntroducer());
        BasisNetworkServer.BasisServerP2PBroker.Initialize();
    }

    #endregion

    #region Server Setup

    public static void SetupServer(Configuration configuration)
    {
        Listener = new EventBasedNetListener();
        Server = BasisNetworkStackRegistry.Create(configuration.NetworkStackId, Listener, configuration);

        NetDebug.Logger = new BasisServerLogger();
        StartListening(configuration);
    }

    public static void StartListening(Configuration configuration)
    {
        IPAddress ipv4, ipv6;
        if (configuration.OverrideAutoDiscoveryOfIpv)
        {
            if (!IPAddress.TryParse(Configuration.IPv4Address, out ipv4))
            {
                BNL.LogWarning($"Failed to parse IPv4 bind address '{Configuration.IPv4Address}', falling back to 0.0.0.0");
                ipv4 = IPAddress.Any;
            }
            if (!IPAddress.TryParse(Configuration.IPv6Address, out ipv6))
            {
                BNL.LogWarning($"Failed to parse IPv6 bind address '{Configuration.IPv6Address}', falling back to [::]");
                ipv6 = IPAddress.IPv6Any;
            }
        }
        else
        {
            ipv4 = IPAddress.Any;
            ipv6 = IPAddress.IPv6Any;
        }

        Server.Start(ipv4, ipv6, configuration.SetPort);
        BNL.Log($"Listening on UDP port {configuration.SetPort}");
        BNL.Log($"  IPv4 bind: {ipv4}");
        BNL.Log($"  IPv6 bind: [{ipv6}]");
    }
    #endregion
    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, NetPeer sender, ReadOnlySpan<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        int senderId = sender.Id;
        int sent = 0;
        foreach (var client in clients)
        {
            if (client.Id != senderId && TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
            {
                sent++;
            }
        }
        BasisNetworkStatistics.RecordOutboundBatch(channel, sent, (long)sent * writer.Length);
    }
    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, ReadOnlySpan<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        int sent = 0;
        foreach (var client in clients)
        {
            if (TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
            {
                sent++;
            }
        }
        BasisNetworkStatistics.RecordOutboundBatch(channel, sent, (long)sent * writer.Length);
    }

    public static void BroadcastMessageToClients(NetDataWriter writer, byte channel, ref List<NetPeer> clients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced, int maxMessages = 70)
    {
        if (!CheckValidated(writer))
        {
            return;
        }

        int count = clients.Count;
        int sent = 0;
        for (int Index = 0; Index < count; Index++)
        {
            NetPeer client = clients[Index];
            if (TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
            {
                sent++;
            }
        }
        BasisNetworkStatistics.RecordOutboundBatch(channel, sent, (long)sent * writer.Length);
    }

    public static void TrySend(NetPeer client, NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod, int maxMessages = 70)
    {
        if (TrySendNoRecord(client, writer, channel, deliveryMethod, maxMessages))
        {
            BasisNetworkStatistics.RecordOutbound(channel, writer.Length);
        }
    }

    // 実際に send された場合に true を返す (channel ごとの queue cap で drop された場合とは区別)。
    // queue/send の判定を stats record から分離し、broadcast loop が N 回の Interlocked を
    // (channel, broadcast) ごとの RecordOutboundBatch 1 回に畳み込めるようにする。
    private static bool TrySendNoRecord(NetPeer client, NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod, int maxMessages)
    {
        if (deliveryMethod == DeliveryMethod.Sequenced || deliveryMethod == DeliveryMethod.Unreliable)
        {
            int queuedMessages = client.GetPacketsCountInQueue(channel, deliveryMethod);
            if (queuedMessages > maxMessages)
            {
                return false;
            }
        }
        client.Send(writer, channel, deliveryMethod);
        return true;
    }
    public static bool CheckValidated(NetDataWriter writer)
    {
        if (writer.Length == 0)
        {
            BNL.LogError("Trying to send a message with zero length!");
            return false;
        }
        return true;
    }
}
