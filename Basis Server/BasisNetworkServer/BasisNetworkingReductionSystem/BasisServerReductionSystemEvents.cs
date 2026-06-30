using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworking;
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class QueuedMessage
    {
        public NetPeer FromPeer;
        public byte Sequence;
        public LocalAvatarSyncMessage AvatarMessage;
    }

    /// <summary>
    /// inner loop で (sender, receiver) pair ごとに一度記録される deferred avatar send。
    /// 共有された pre-serialized source への参照と receiver ごとの interval byte を保持し、
    /// flush stage がこれらを 1 つの bundle に compress するか、
    /// 個別の SendUnreliableRawMerge として replay するかを決める。
    /// </summary>
    public struct PendingAvatarSend
    {
        public byte[] Source;
        public int Length;
        public byte Channel;
        public byte Interval;
        public byte IntervalOffset; // 1 for byte-id, 2 for ushort-id
    }

    /// <summary>
    /// per-peer tracking と cached distance data をまとめたもの。32 bytes = cache line あたり 2 つ。
    /// send loop は pair ごとにすべての field を sequential に読み、float math は行わない。
    /// </summary>
    public struct PeerTrackingData
    {
        public long LastSentTime;
        public long LastSeenGeneration;
        // slow distance loop (~2Hz) で cache し、fast send loop (~250Hz) で読む。
        // hot path から per-pair distance math を取り除く。
        public long CachedIntervalTicks;
        public byte CachedQualityIndex;
        public byte CachedIntervalByte;
    }

    public class PlayerState
    {
        public NetPeer Peer;
        public bool IsActive;

        // distance decision に使う。
        public Basis.Scripts.Networking.Compression.Vector3 Position;

        // base message shell (send 前に avatarSerialization を差し替える)。
        public ServerSideSyncPlayerMessage SyncMessage;

        // combined per-peer tracking: last sent tick と last seen generation を 1 struct にまとめ、
        // send loop で cache-friendly な O(1) access を行う。player id で index する。
        public PeerTrackingData[] PeerTracking;

        // generation counter。この player が新しい avatar data を受け取るたびに increment される。
        // receiver は LastSeenGeneration と比較し、新しい data があるかを判断する。
        // 32-bit 環境や cross-core visibility の thread safety のため、Interlocked.Read/Increment 経由で access する。
        public long DataGeneration;

        // inner send loop で dereference chain を避けるため、ProcessMessage 中に cache する。
        public bool HasAdditionalData;

        // quality ごとの cached payload (payload bytes と DataQualityLevel のみ)。
        // AvatarHigh は自身の byte[] を所有し、QueuedMessagePool と共有しない。
        // これにより、pool reuse が muscle-change comparison を静かに壊すことを防ぐ。
        public LocalAvatarSyncMessage AvatarHigh;
        public LocalAvatarSyncMessage AvatarMedium;
        public LocalAvatarSyncMessage AvatarLow;
        public LocalAvatarSyncMessage AvatarVeryLow;

        // unreliable client->server packet 用の inbound sequence tracking。
        public byte LastInboundSequence;
        public bool HasReceivedFirst;

        // pre-serialized data に stamp する outbound sequence (新しい avatar update ごとに increment)。
        public byte OutboundSequence;

        // quality ごとの pre-serialized keyframe bytes。
        // Byte-ID: [PlayerID:1][interval_placeholder:1][sequence:1][array:N][additional...]
        // Ushort-ID: [PlayerID:2][interval_placeholder:1][sequence:1][array:N][additional...]
        // interval byte offset は SmallId に依存する (byte なら 1、ushort なら 2)。
        // quality は channel number から derive され、payload には保存しない。
        public byte[][] SerializedKeyframe = new byte[4][];
        public int[] SerializedKeyframeLength = new int[4];

        // playerID が byte に収まる場合 true (<=255)。creation 時に一度だけ設定する。
        public bool SmallId;

        // lazy pre-serialization: last tick で receiver がいた quality level の bitmask。
        // parallel send loop から atomic に更新し、ProcessMessage で read/reset する。
        // bit 0 = VeryLow、bit 1 = Low、bit 2 = Medium、bit 3 = High。
        public int UsedQualities;

        // AvatarHigh.array に保存された actual payload size (ArrayPool 由来なら大きい場合がある)。
        // pooled array を正しく扱うため、muscle-change comparison では .Length ではなくこれを使う。
        public int HighArrayActualSize;

        // receiver ごとの bundle accumulator。UpdateCommunicationAndDistances で populate し、
        // FlushPendingForReceiver で drain する。初回使用時に lazy allocate される。
        // ここへ write するのはこの player 自身の receive thread (Parallel.For body 1 つ) だけなので、
        // synchronization は不要。
        public PendingAvatarSend[] PendingSends;
        public int PendingCount;

        // この receiver へ compressed bundle を emit するとき tick 間で再利用する scratch buffer。
        // deflate path の per-tick allocation を避ける。size は flush logic が決める。
        public byte[] BundleRawScratch;
        public byte[] BundleCompressedScratch;

        // この receiver の bundle で観測した compressed/raw ratio の EMA。
        // FlushPendingForReceiver が 1 MTU-sized chunk に何 message 入るかを予測するために使い、
        // 初回 compress attempt が retry なしで成功しやすくする。0 = unseeded。
        public float LastBundleRatio;
    }

    public partial class BasisServerReductionSystemEvents
    {
        private static readonly CancellationTokenSource cts = new();
        // PlayerState の PeerTracking array 初期 capacity。
        // player ID がこれを超えたら grow する。
        private const int InitialPlayerArrayCapacity = 2048;

        private static readonly ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        public static ShardedConcurrentDictionary<PlayerState> playerStates = new();
        // double-buffered message dictionary。tick ごとに allocate せず、swap と clear を行う。
        private static ShardedConcurrentDictionary<QueuedMessage> currentMessages = new();
        private static ShardedConcurrentDictionary<QueuedMessage> _backMessages = new();

        public static float BSRBaseMultiplier = 1.0f;
        public static float BSRSIncreaseRate = 0.01f;
        public static int BSRSMillisecondDefaultInterval = 50;

        // compressed avatar bundle settings (NetworkServer.InitializePulseSettings から書かれる)。
        // 有効な場合、receiver ごとの inner loop は send を PendingAvatarSend[] に defer し、
        // CompressedAvatarBundleChannel 上の 1 つの deflated bundle または
        // original quality channel 上の個別 SendUnreliableRawMerge call として flush する。
        public static bool EnableAvatarBundleCompression = true;
        public static int AvatarBundleMinMessages = 4;
        public static int AvatarBundleMinBytes = 300;
        // compressed bundle が single UDP datagram に収まるか確認する前に peer.Mtu から引く conservative headroom。
        // LiteNetLib unreliable header、optional packet-layer header、merge length prefix を見込む。
        private const int BundleMtuHeadroom = 32;
        // bundle wire header: [count:1][rawLen:2-LE]
        private const int BundleHeaderSize = 3;
        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;

        // tick ごとに rebuild せず、ProcessMessage / ProcessPendingRemovals 経由で incremental に維持する。
        private static readonly List<(int id, PlayerState state)> _activePlayers = new();
        private static readonly object _activePlayersLock = new();
        private static (int id, PlayerState state)[] _activePlayersSnapshot = Array.Empty<(int, PlayerState)>();
        private static volatile bool _activePlayersDirty = false;

        private static readonly ConcurrentQueue<int> playersToRemove = new();

        // server が空のとき 250Hz polling ではなく tick loop を park させる (~0% CPU)。
        // 最初の packet が届いた瞬間に Set() するため、join latency は増えない。
        // LiteNetLib の logic thread も同じ approach を使っている。
        private static readonly AutoResetEvent _tickWake = new(false);
        private static int _activePlayerCount;

        // currentMessages を毎 tick drain するための reusable snapshot list。per-tick allocation を避ける。
        private static readonly List<QueuedMessage> _messagesSnapshot = new(1024);

        // Parallel.ForEach 用の static delegate。毎 tick の closure allocation を避ける。
        private static readonly Action<QueuedMessage> s_processMessageAction = msg =>
        {
            try
            {
                ProcessMessage(msg);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[ProcessMessage] Exception: {ex}");
            }
        };

        // distance -> quality threshold (squared meters)
        public static float HighDistanceSq = 100f;      // 10m
        public static float MediumDistanceSq = 900f;    // 30m
        public static float LowDistanceSq = 2500f;      // 50m

        public static long intervalMs = 4;
        // server が空のときの fallback wake。実際の wake は _tickWake.Set() が行う。
        private const int IdleWaitMs = 250;
        // load-adaptive inter-tick wait。未使用 budget がこの値より大きい (light load) 場合は
        // WaitOne で block する (~0% CPU)。小さい (heavy load、budget 近辺) 場合は、
        // 小さな残り時間を busy-spin して rate を正確に合わせる。これは spin cap も兼ねるため、
        // loop は tick ごとにこれ以上 spin しない。0 にすると pure WaitOne
        // (最低 CPU、load 下では rate が緩くなる)。高 load で tighter rate を優先するなら intervalMs に近づける。
        // saturated tick (slack なし) は wait も spin もしない。
        public static double MaxSpinMs = 2.5;
        // tick slicing: O(N^2) work を分散するため、各 tick では receiver の subset だけを処理する。
        // adaptive: tick が長すぎると増やし、budget 内なら減らす。
        private static int _sliceCount = 1;
        private static int _sliceIndex = 0;

        // distance cache: N tick ごとに distance から quality/interval を再計算する。
        // fast send loop は pair ごとに毎 tick distance を計算せず、cached value を使う。
        // 4ms tick interval では 125 ticks = 約 500ms。6m/s の player はその間に 3m 移動するが、
        // quality threshold (3m/10m/20m) ひとつ分に収まるため、許容できる stale さ。
        private static int _distanceTickCounter = 0;
        public static int DistanceUpdateIntervalTicks = 125;

        // position-only fast path (repack skip) 用に muscle+tail byte count を cache する。
        private static readonly int HighMuscleAndTailBytes = MuscleBytes(BitQuality.High) + TailBytes;

        // generation snapshot: O(N^2) send loop の前に tick ごとに一度 populate する。
        // pair ごとの Interlocked.Read をなくす (N^2 memory fences -> N)。
        // early player join 時の reallocation を避けるため InitialPlayerArrayCapacity で pre-allocate する。
        private static long[] _generationSnapshot = new long[InitialPlayerArrayCapacity];

        // position snapshot: inner loop で cache-friendly に読むための contiguous array。
        // pair ごとに散らばった heap 上の PlayerState object を pointer-chasing することを避ける。
        private static float[] _posXSnapshot = new float[InitialPlayerArrayCapacity];
        private static float[] _posYSnapshot = new float[InitialPlayerArrayCapacity];
        private static float[] _posZSnapshot = new float[InitialPlayerArrayCapacity];



        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        static BasisServerReductionSystemEvents()
        {
            // Windows で WaitOne が約 4ms の accuracy を保てるよう、OS timer を 1ms に上げる
            // (default は約 15ms)。Windows-only。Linux/macOS ではこの call を skip するため
            // winmm P/Invoke は resolve されない (それらはすでに約 1ms に resolve される)。
            // minimal Windows container などで winmm がない場合も static ctor を fault させて
            // reduction system 全体を落とさないよう try/catch で degrade する。
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { timeBeginPeriod(1); }
                catch (Exception ex) { BNL.LogError($"[BSR] timeBeginPeriod unavailable, tick timing falls back to OS default: {ex.Message}"); }
            }

            var thread = new Thread(BackgroundTickLoop)
            {
                IsBackground = true,
                Name = "BSR-TickLoop",
                Priority = ThreadPriority.AboveNormal,
            };
            thread.Start();
        }

        public static void HandleAvatarMovement(NetPacketReader reader, NetPeer fromPeer, byte channel)
        {
            // client が先頭に付けた application-level sequence byte を読む。
            if (!reader.TryGetByte(out byte sequence))
            {
                reader.Recycle();
                return;
            }

            // quality と additional-data の有無は channel から derive する。
            byte quality = BasisNetworkCommons.GetQualityFromChannel(channel);
            bool hasAdditional = BasisNetworkCommons.ChannelHasAdditionalData(channel);

            // pooled byte[] を再利用するため、deserialize の前に rent する (player ごとの per-frame allocation を避ける)。
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage.Deserialize(reader, quality, hasAdditional);
            reader.Recycle();

            if (message.AvatarMessage.array == null)
            {
                BNL.LogError($"[HandleAvatarMovement] Deserialized avatar message has null array from peer {fromPeer.Id}");
                QueuedMessagePool.Return(message);
                return;
            }

            // この peer 用の pending message を overwrite する。
            // call ごとの closure allocation を避けるため、AddOrUpdate ではなく indexer を使う。
            // prev は pool に返さない。drain phase が capture 済みの可能性があるため。
            // orphaned prev があれば GC に回収される。同じ peer から同じ tick 内に 2 message 来た場合だけで、
            // cost は無視できる。
            currentMessages[fromPeer.Id] = message;

            // loop が park 中 (empty server) の場合だけ wake する。
            // player が登録された後は loop が動作中なので、この read は syscall なしで short-circuit する。
            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }

        public static void AddMessage(NetPeer fromPeer, LocalAvatarSyncMessage localMessage, byte sequence)
        {
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage = localMessage;

            // HandleAvatarMovement と同じく、indexer により closure allocation を避ける。
            currentMessages[fromPeer.Id] = message;

            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }

        /// <summary>
        /// dedicated thread 上の tick loop。player 接続中は ~250Hz (4ms) を target とし、
        /// server が空なら park する (~0% CPU)。inter-tick wait は AutoResetEvent.WaitOne を使うため、
        /// idle または under-budget の loop が core を燃やし続けない。Windows では OS timer を 1ms に上げ、
        /// wait が ~4ms accuracy を保てるようにする。
        /// </summary>
        private static void BackgroundTickLoop()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                long startTick = Stopwatch.GetTimestamp();

                // 1 回の bad tick で thread を殺してはいけない。
                // ここで unhandled throw が起きると (例: mass connect/recycle 中の edge case)、
                // 以後すべての tick が止まり、server restart まで avatar sync が静かに freeze する。
                try
                {
                    RunTick(startTick);
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[BSR Tick] Unhandled exception: {ex}");
                }

                // empty server: 250Hz で spin せず、work が来るまで park する。
                // _tickWake は最初の inbound packet (および Shutdown) で signal されるため、
                // idle 時は ~0% CPU で、connect latency も増えない。
                if (Volatile.Read(ref _activePlayerCount) == 0)
                {
                    _tickWake.WaitOne(IdleWaitMs);
                    continue;
                }

                // load-adaptive wait。remainMs は未使用 budget で、直接的な load signal になる。
                // 大きい (light load) -> WaitOne で block (~0% CPU)。
                // 小さい (heavy load、budget 近辺) -> 残りを spin して rate を正確に合わせる。
                // load 下では scheduler が yield した thread を遅れて wake しがちで、core もどうせ busy なため。
                // remainMs <= 0 (saturated) ならどちらの branch も通らず、wait も spin もしない。
                long targetTick = startTick + (long)(intervalMs * MsToTick);
                double remainMs = (targetTick - Stopwatch.GetTimestamp()) / MsToTick;
                if (remainMs > MaxSpinMs)
                {
                    _tickWake.WaitOne((int)Math.Round(remainMs));
                }
                else
                {
                    while (Stopwatch.GetTimestamp() < targetTick)
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
        }

        private static void RunTick(long startTick)
        {
            bool profiling = BSRProfiler.Enabled;
            long phaseTick = profiling ? Stopwatch.GetTimestamp() : 0;

            // phase 1: drain。
            // inbound thread が cleared dictionary へ write するよう back-buffer と swap する。
            // tick ごとの allocation はなく、swap と drain だけ。
            _backMessages.Clear();
            var batch = Interlocked.Exchange(ref currentMessages, _backMessages);
            _backMessages = batch;
            _messagesSnapshot.Clear();
            foreach (var kvp in batch)
            {
                _messagesSnapshot.Add(kvp.Value);
            }
            if (profiling) { BSRProfiler.drainTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp(); }

            // phase 2: message を処理する (static delegate により per-tick closure allocation を避ける)。
            Parallel.ForEach(_messagesSnapshot, parallelOptions, s_processMessageAction);
            if (profiling) { BSRProfiler.processTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp(); }

            ProcessPendingRemovals();

            // phase 2.5: distance cache update (毎 tick ではなく ~2Hz で実行)。
            _distanceTickCounter++;
            if (_distanceTickCounter >= DistanceUpdateIntervalTicks)
            {
                _distanceTickCounter = 0;
                long distStart = profiling ? Stopwatch.GetTimestamp() : 0;
                UpdateDistanceCache();
                if (profiling) { BSRProfiler.distanceTicks += Stopwatch.GetTimestamp() - distStart; phaseTick = Stopwatch.GetTimestamp(); }
            }

            // phase 3: send loop。
            long now = Stopwatch.GetTimestamp();
            UpdateCommunicationAndDistances(now);
            if (profiling)
            {
                BSRProfiler.updateTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp();
            }

            // phase 4: network I/O。
            BasisNetworkPIPCamera.UpdatePIPPositions(now);
            if (NetworkServer.Server is LNLNetManager lnlReductionServer && lnlReductionServer.manager != null)
            {
                lnlReductionServer.manager.TriggerUpdate();
            }
            if (profiling)
            {
                BSRProfiler.triggerTicks += Stopwatch.GetTimestamp() - phaseTick;
                BSRProfiler.tickCount++;
                BSRProfiler.messagesProcessed += _messagesSnapshot.Count;
            }

            // tick bookkeeping。
            long elapsedTicks = Stopwatch.GetTimestamp() - startTick;
            double elapsedMs = elapsedTicks / MsToTick;

            BSRProfiler.TryPrint();

            // adaptive slice count: tick が 3ms を超えたら slicing を増やし、1ms 未満なら減らす。
            if (elapsedMs > 3.0 && _sliceCount < 32)
            {
                _sliceCount++;
            }
            else if (elapsedMs < 1.0 && _sliceCount > 1)
            {
                _sliceCount--;
            }
        }

        private static void ProcessPendingRemovals()
        {
            while (playersToRemove.TryDequeue(out int id))
            {
                if (playerStates.TryRemove(id, out var removedState))
                {
                    removedState.IsActive = false;

                    // pooled array を ArrayPool へ返す。
                    if (removedState.AvatarHigh.array != null)
                        ArrayPool<byte>.Shared.Return(removedState.AvatarHigh.array);
                    if (removedState.BundleRawScratch != null)
                    {
                        ArrayPool<byte>.Shared.Return(removedState.BundleRawScratch);
                        removedState.BundleRawScratch = null;
                    }
                    if (removedState.BundleCompressedScratch != null)
                    {
                        ArrayPool<byte>.Shared.Return(removedState.BundleCompressedScratch);
                        removedState.BundleCompressedScratch = null;
                    }

                    // active players list から削除する。
                    lock (_activePlayersLock)
                    {
                        for (int i = _activePlayers.Count - 1; i >= 0; i--)
                        {
                            if (_activePlayers[i].id == id)
                            {
                                _activePlayers.RemoveAt(i);
                                _activePlayersDirty = true;
                                Interlocked.Decrement(ref _activePlayerCount);
                                break;
                            }
                        }
                    }


                    // 残っている全 player から、削除された ID の stale な per-player tracking data を clear する。
                    // これをしないと、新しい player がこの ID を再利用したとき、他 player の LastSeenGeneration が
                    // 古い (高い) generation value を保持したままになり、new-data check
                    // (senderGen > seenGens[jId]) が fail して、新しい player の data が送られなくなる。
                    foreach (var kvp in playerStates)
                    {
                        var otherState = kvp.Value;
                        if (id < otherState.PeerTracking.Length)
                        {
                            otherState.PeerTracking[id] = default;
                        }
                    }
                    BNL.Log($"Player {id} removed and cleaned up.");
                }
                else
                {
                    BNL.LogError("Missing Player From Index, Normally Quick Disconnect after Connect " + id);
                }
            }
        }

        private static void UpdateDistanceCache()
        {
            if (_activePlayersDirty)
            {
                lock (_activePlayersLock)
                {
                    if (_activePlayersDirty)
                    {
                        _activePlayersSnapshot = _activePlayers.ToArray();
                        _activePlayersDirty = false;
                    }
                }
            }
            var activeCopy = _activePlayersSnapshot;
            int playerCount = activeCopy.Length;
            if (playerCount == 0) return;

            // cache-friendly な distance math のため、position を contiguous array へ snapshot する。
            int maxId = 0;
            for (int i = 0; i < playerCount; i++)
            {
                if (activeCopy[i].id > maxId) maxId = activeCopy[i].id;
            }
            int snapshotLen = maxId + 1;
            if (_posXSnapshot.Length < snapshotLen)
            {
                int newLen = Math.Max(snapshotLen, _posXSnapshot.Length * 2);
                _posXSnapshot = new float[newLen];
                _posYSnapshot = new float[newLen];
                _posZSnapshot = new float[newLen];
            }
            for (int i = 0; i < playerCount; i++)
            {
                int id = activeCopy[i].id;
                var state = activeCopy[i].state;
                _posXSnapshot[id] = state.Position.x;
                _posYSnapshot[id] = state.Position.y;
                _posZSnapshot[id] = state.Position.z;
            }

            Parallel.For(0, playerCount, parallelOptions, i =>
            {
                var (id, state) = activeCopy[i];
                var tracking = state.PeerTracking;
                if (tracking == null) return;

                float iX = _posXSnapshot[id];
                float iY = _posYSnapshot[id];
                float iZ = _posZSnapshot[id];

                for (int index = 0; index < playerCount; index++)
                {
                    int jId = activeCopy[index].id;
                    if (id == jId) continue;

                    // 必要なら tracking array を grow する (send loop と同じ logic)。
                    if (jId >= tracking.Length)
                    {
                        lock (state)
                        {
                            if (jId >= state.PeerTracking.Length)
                            {
                                int newLen = Math.Max(state.PeerTracking.Length * 2, jId + 1);
                                Array.Resize(ref state.PeerTracking, newLen);
                            }
                            tracking = state.PeerTracking;
                        }
                    }

                    float dx = iX - _posXSnapshot[jId];
                    float dy = iY - _posYSnapshot[jId];
                    float dz = iZ - _posZSnapshot[jId];
                    float distSq = dx * dx + dy * dy + dz * dz;

                    CalculateIntervalFromDistanceSq(distSq, out byte intervalByte, out int actualInterval);

                    tracking[jId].CachedIntervalTicks = (long)(actualInterval * MsToTick);
                    tracking[jId].CachedQualityIndex = (byte)GetQualityIndex(distSq);
                    tracking[jId].CachedIntervalByte = intervalByte;
                }
            });
        }

        private static void UpdateCommunicationAndDistances(long nowTicks)
        {
            // double-buffered snapshot: dirty のときだけ rebuild する。
            if (_activePlayersDirty)
            {
                lock (_activePlayersLock)
                {
                    if (_activePlayersDirty)
                    {
                        _activePlayersSnapshot = _activePlayers.ToArray();
                        _activePlayersDirty = false;
                    }
                }
            }
            var activeCopy = _activePlayersSnapshot;

            int playerCount = activeCopy.Length;
            if (playerCount == 0)
            {
                return;
            }

            // generation counter だけを snapshot する (position は slow distance cache が扱う)。
            int maxId = 0;
            for (int i = 0; i < playerCount; i++)
            {
                if (activeCopy[i].id > maxId) maxId = activeCopy[i].id;
            }
            int snapshotLen = maxId + 1;
            if (_generationSnapshot.Length < snapshotLen)
            {
                _generationSnapshot = new long[Math.Max(snapshotLen, _generationSnapshot.Length * 2)];
            }
            for (int i = 0; i < playerCount; i++)
            {
                int id = activeCopy[i].id;
                _generationSnapshot[id] = Interlocked.Read(ref activeCopy[i].state.DataGeneration);
            }

            // まだ distance cache にない pair (new player) 用の fallback interval。
            long minIntervalTicks = (long)(BSRSMillisecondDefaultInterval * BSRBaseMultiplier * MsToTick);

            // tick slicing: 各 tick で receiver の slice だけを処理する。
            int sliceSize = (playerCount + _sliceCount - 1) / _sliceCount;
            int start = _sliceIndex * sliceSize;
            int end = Math.Min(start + sliceSize, playerCount);
            _sliceIndex = (_sliceIndex + 1) % _sliceCount;

            if (start >= playerCount)
            {
                return;
            }

            bool bundlingEnabled = EnableAvatarBundleCompression;

            Parallel.For(start, end, parallelOptions, i =>
            {
                var (id, state) = activeCopy[i];
                var stateI = state;
                var peer = stateI.Peer;

                var tracking = stateI.PeerTracking;
                if (tracking == null)
                {
                    return;
                }

                // receiver ごとの pending buffer。この tick で送る予定だったものを集め、
                // 最後に compress するか individual で送るかを決める。lazy に grow する。
                var pending = stateI.PendingSends;
                if (pending == null)
                {
                    pending = new PendingAvatarSend[64];
                    stateI.PendingSends = pending;
                }
                int pendingCount = 0;

                // thread-local send counter。hot loop で Interlocked を使わない。
                long localSends = 0;

                for (int index = 0; index < playerCount; index++)
                {
                    int jId = activeCopy[index].id;
                    if (id == jId)
                    {
                        continue;
                    }

                    if (BasisNetworkServer.BasisServerP2PBroker.IsP2POffloaded(jId, id))
                    {
                        continue;
                    }

                    // bounds check。必要なら array を grow する (ID が capacity を超えたときだけなので稀)。
                    if (jId >= tracking.Length)
                    {
                        lock (stateI)
                        {
                            if (jId >= stateI.PeerTracking.Length)
                            {
                                int newLen = Math.Max(stateI.PeerTracking.Length * 2, jId + 1);
                                Array.Resize(ref stateI.PeerTracking, newLen);
                            }
                            tracking = stateI.PeerTracking;
                        }
                    }

                    // 1. new data check。plain array read で、pointer chase なし。
                    long senderGen = _generationSnapshot[jId];
                    if (senderGen <= tracking[jId].LastSeenGeneration)
                    {
                        continue;
                    }

                    // 2. cached distance result を使った interval check (float math なし)。
                    long elapsed = nowTicks - tracking[jId].LastSentTime;
                    long required = tracking[jId].CachedIntervalTicks;
                    if (required <= 0) required = minIntervalTicks;
                    if (elapsed < required)
                    {
                        continue;
                    }

                    // 3. distance cache から quality と interval byte を取得する。
                    int qi = tracking[jId].CachedQualityIndex;
                    byte startAtZeroInterval = tracking[jId].CachedIntervalByte;

                    PlayerState stateJ = activeCopy[index].state;

                    // lazy pre-serialization: serialize 済みでなければ skip し、next tick に必要と mark する。
                    int srcLen = stateJ.SerializedKeyframeLength[qi];
                    byte[] srcArr = stateJ.SerializedKeyframe[qi];
                    if (srcLen == 0 || srcArr == null)
                    {
                        MarkQualityUsed(ref stateJ.UsedQualities, qi);
                        continue;
                    }

                    byte avatarChannel = stateJ.SmallId
                        ? BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, stateJ.HasAdditionalData)
                        : BasisNetworkCommons.GetPlayerAvatarLargeChannelForQuality(qi, stateJ.HasAdditionalData);

                    // send を defer する。pair ごとに見ると SendUnreliableRawMerge より安い。
                    // single struct write と、pool-rent + BlockCopy + enqueue の差。
                    if (pendingCount == pending.Length)
                    {
                        Array.Resize(ref pending, pending.Length * 2);
                        stateI.PendingSends = pending;
                    }
                    ref PendingAvatarSend p = ref pending[pendingCount++];
                    p.Source = srcArr;
                    p.Length = srcLen;
                    p.Channel = avatarChannel;
                    p.Interval = startAtZeroInterval;
                    p.IntervalOffset = (byte)(stateJ.SmallId ? 1 : 2);

                    MarkQualityUsed(ref stateJ.UsedQualities, qi);

                    tracking[jId].LastSentTime = nowTicks;
                    tracking[jId].LastSeenGeneration = senderGen;

                    localSends++;
                }

                stateI.PendingCount = pendingCount;
                if (pendingCount > 0)
                {
                    FlushPendingForReceiver(stateI, peer, bundlingEnabled);
                }

                // send ごとではなく receiver ごとに Interlocked.Add する。~32K ではなく ~25 atomics/tick。
                if (localSends > 0 && BSRProfiler.Enabled)
                {
                    Interlocked.Add(ref BSRProfiler.SendCount, localSends);
                }
            });
        }

        /// <summary>
        /// receiver ごとの PendingSends buffer を wire へ flush する。
        /// bundling が有効で、receiver に <see cref="AvatarBundleMinMessages"/> 件以上の message が
        /// queue されている場合、greedy に 1 つ以上の MTU-sized deflated bundle へ pack し、
        /// <see cref="BasisNetworkCommons.CompressedAvatarBundleChannel"/> で送る。
        /// bundle するには小さすぎる tail (または compress できない pathological pair) は、
        /// original quality channel 上の individual unreliable send として replay する。
        /// </summary>
        private static void FlushPendingForReceiver(PlayerState stateI, NetPeer peer, bool bundlingEnabled)
        {
            int count = stateI.PendingCount;
            if (count <= 0) return;
            var pending = stateI.PendingSends;

            // receiver-tick ごとの stats accumulator。send ごとの Interlocked を、
            // flush 時の channel ごとの RecordOutboundBatch 1 回に畳む。stack-only で call あたり約 4KB。
            Span<long> tailCounts = stackalloc long[256];
            Span<long> tailBytes = stackalloc long[256];
            long bundleCount = 0;
            long bundleBytes = 0;

            int cursor = 0;
            if (bundlingEnabled && count >= AvatarBundleMinMessages)
            {
                cursor = EmitGreedyBundles(stateI, peer, pending, count, ref bundleCount, ref bundleBytes);
            }

            // bundle に pack されなかったものを送る (tail < min、または bundling disabled / pathological no-fit 時の pending 全体)。
            // pre-bundling path と同等で、LiteNetLib の merge buffer はこれらも UDP packet へ pack する。
            int tailSent = 0;
            for (int i = cursor; i < count; i++)
            {
                ref PendingAvatarSend p = ref pending[i];
                if (p.Length <= p.IntervalOffset) continue;
                peer.SendUnreliableRawMerge(p.Source, 0, p.Length, p.Channel, p.IntervalOffset, p.Interval);
                tailCounts[p.Channel]++;
                tailBytes[p.Channel] += p.Length;
                tailSent++;
            }

            // accumulated stats を (channel, metric) ごとに 1 回の Interlocked.Add で flush する。
            if (BasisNetworkStatistics.IsRecordingData)
            {
                if (bundleCount > 0)
                {
                    BasisNetworkStatistics.RecordOutboundBatch(BasisNetworkCommons.CompressedAvatarBundleChannel, bundleCount, bundleBytes);
                }
                if (tailSent > 0)
                {
                    for (int c = 0; c < 256; c++)
                    {
                        if (tailCounts[c] > 0)
                        {
                            BasisNetworkStatistics.RecordOutboundBatch((byte)c, tailCounts[c], tailBytes[c]);
                        }
                    }
                }
            }
            // profiler attribution: "tail of bundled receiver" (cursor > 0) と
            // "bundling が何も生成しなかったための fallback" (bundling enabled かつ cursor == 0) を区別する。
            if (BSRProfiler.Enabled && tailSent > 0)
            {
                Interlocked.Add(ref BSRProfiler.bundleTailUncompressed, tailSent);
                if (bundlingEnabled && cursor == 0 && count >= AvatarBundleMinMessages)
                {
                    Interlocked.Increment(ref BSRProfiler.bundleFallbacks);
                }
            }
            stateI.PendingCount = 0;

            // tick-scoped scratch buffer を pool へ返す。これをしないと PlayerState ごとに
            // 約 85KB 以上を永久保持する (1k+ players では LOH、gen2 pause amplifier になる)。
            if (stateI.BundleRawScratch != null)
            {
                ArrayPool<byte>.Shared.Return(stateI.BundleRawScratch);
                stateI.BundleRawScratch = null;
            }
            if (stateI.BundleCompressedScratch != null)
            {
                ArrayPool<byte>.Shared.Return(stateI.BundleCompressedScratch);
                stateI.BundleCompressedScratch = null;
            }
        }

        /// <summary>
        /// MTU-sized compressed bundle に収まるだけの pending message を greedy に pack し、
        /// 各 bundle を <see cref="BasisNetworkCommons.CompressedAvatarBundleChannel"/> で emit する。
        /// receiver ごとの compressed/raw ratio EMA を使うため、最初の deflate attempt が成功しやすい。
        /// overshoot 時は実測 ratio を使って shrink し、一度だけ retry する。
        /// まだ emit されていない最初の entry index を返す。caller は [cursor, count) の tail を uncompressed で送る。
        /// </summary>
        private static int EmitGreedyBundles(PlayerState stateI, NetPeer peer, PendingAvatarSend[] pending, int count, ref long bundleCount, ref long bundleBytes)
        {
            int budget = peer.Mtu - BundleMtuHeadroom - BundleHeaderSize;
            if (budget <= 0) return 0;

            // initial ratio guess: bit-packed avatar data に対する deflate Fastest の観測値は約 0.6。
            // [0.05, 0.95] に保ち、prediction が zero や full-budget chunk を選ばないようにする。
            float ratio = stateI.LastBundleRatio;
            if (ratio < 0.05f || ratio > 0.95f) ratio = 0.6f;

            int cursor = 0;
            // AvatarBundleMinMessages は bundle 開始だけを gate する (first chunk については caller が確認済み)。
            // loop 内では、各 chunk を MTU に収まる size にする。large message 1-2 件だけの chunk でも、
            // rawLen >= AvatarBundleMinBytes なら deflate header の元が取れる。
            // outer condition は receiver tail が min 未満の場合だけ uncompressed に残す
            // (tiny remainder は uncompressed send でも十分 merge されるため)。
            while (count - cursor >= AvatarBundleMinMessages)
            {
                // ~budget * 0.95 に compress される raw chunk size を予測する
                // (near-MTU overshoot で retry を無駄にしないための小さな safety margin)。
                // その後、target に達するか message が尽きるまで pending を歩いて size を accumulate する。
                int targetRaw = (int)((budget * 0.95f) / ratio);
                int chunkEnd = PickChunkEnd(pending, cursor, count, targetRaw);
                if (chunkEnd <= cursor) break;

                int rawLen = BuildRawForRange(stateI, pending, cursor, chunkEnd);
                if (rawLen < AvatarBundleMinBytes) break;

                if (TryDeflateAndEmit(stateI, peer, cursor, chunkEnd, rawLen, budget, ref bundleCount, ref bundleBytes, out int compressedLen))
                {
                    UpdateRatioEMA(ref stateI.LastBundleRatio, compressedLen, rawLen, weightOnObserved: 0.3f);
                    cursor = chunkEnd;
                    ratio = stateI.LastBundleRatio;
                    continue;
                }

                // overshoot。直前に観測した actual ratio で target を再計算し、
                // より小さい chunk で retry する。observed value を重めにする。
                // この receiver の payload は予測より compress されにくい可能性が高いため。
                UpdateRatioEMA(ref stateI.LastBundleRatio, compressedLen, rawLen, weightOnObserved: 0.7f);
                float observed = (float)compressedLen / rawLen;
                if (observed < 0.05f) observed = 0.05f;
                if (observed > 0.99f) observed = 0.99f;

                int retryTargetRaw = (int)((budget * 0.92f) / observed);
                int retryEnd = PickChunkEnd(pending, cursor, chunkEnd, retryTargetRaw);
                if (retryEnd >= chunkEnd) retryEnd = cursor + Math.Max(1, (chunkEnd - cursor) * 3 / 4);
                if (retryEnd <= cursor) break;

                int retryRawLen = BuildRawForRange(stateI, pending, cursor, retryEnd);
                if (retryRawLen < AvatarBundleMinBytes) break;

                if (BSRProfiler.Enabled) Interlocked.Increment(ref BSRProfiler.bundleRetries);
                if (!TryDeflateAndEmit(stateI, peer, cursor, retryEnd, retryRawLen, budget, ref bundleCount, ref bundleBytes, out int retryCompressed))
                {
                    // 2 回連続で失敗したため、この tick ではこの receiver の bundling を諦める。
                    // caller が cursor..count を uncompressed で replay する。
                    break;
                }

                UpdateRatioEMA(ref stateI.LastBundleRatio, retryCompressed, retryRawLen, weightOnObserved: 0.5f);
                cursor = retryEnd;
                ratio = stateI.LastBundleRatio;
            }
            return cursor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickChunkEnd(PendingAvatarSend[] pending, int cursor, int hardEnd, int targetRaw)
        {
            int chunkEnd = cursor;
            int rawAccum = 0;
            while (chunkEnd < hardEnd)
            {
                int entrySize = 3 + pending[chunkEnd].Length; // [chan:1][len:2][bytes]
                // chunk が必ず grow するよう、少なくとも 1 entry は含める。
                // 次を足すと predicted budget を超える場合だけ break する。
                if (chunkEnd > cursor && rawAccum + entrySize > targetRaw) break;
                rawAccum += entrySize;
                chunkEnd++;
            }
            return chunkEnd;
        }

        /// <summary>
        /// <c>[start, end)</c> 内の pending entry ごとに
        /// <c>[origChannel:1][len:2-LE][bytes (interval-patched)]</c> を
        /// <c>stateI.BundleRawScratch</c> へ書き込み (必要に応じて grow)、
        /// 書き込んだ total byte 数を返す。
        /// </summary>
        private static int BuildRawForRange(PlayerState stateI, PendingAvatarSend[] pending, int start, int end)
        {
            int upperBound = 0;
            for (int i = start; i < end; i++) upperBound += 3 + pending[i].Length;

            byte[] raw = stateI.BundleRawScratch;
            if (raw == null || raw.Length < upperBound)
            {
                if (raw != null) ArrayPool<byte>.Shared.Return(raw);
                raw = ArrayPool<byte>.Shared.Rent(Math.Max(upperBound, 4096));
                stateI.BundleRawScratch = raw;
            }

            int rawPos = 0;
            for (int i = start; i < end; i++)
            {
                ref PendingAvatarSend p = ref pending[i];
                int len = p.Length;
                if (len <= p.IntervalOffset) continue;

                raw[rawPos++] = p.Channel;
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(rawPos, 2), (ushort)len);
                rawPos += 2;
                Buffer.BlockCopy(p.Source, 0, raw, rawPos, len);
                // copy 内の per-receiver interval byte を patch する (source は shared)。
                raw[rawPos + p.IntervalOffset] = p.Interval;
                rawPos += len;
            }
            return rawPos;
        }

        /// <summary>
        /// <c>stateI.BundleRawScratch[0..rawLen]</c> を LZ4-compress し、
        /// <c>stateI.BundleCompressedScratch</c> の payload region
        /// (予約済み bundle-header prefix の後) へ書く。
        /// peer-MTU budget に収まる場合は CompressedAvatarBundleChannel で 1 UDP datagram を emit し、
        /// compressed payload length を報告する。overshoot では false を返す
        /// (caller がより小さい chunk で retry する)。
        /// LZ4Codec.Encode は allocation も per-call setup もない single static call なので、
        /// Write ごとに internal window + hashtable を allocate する DeflateStream より、
        /// high call rate では約 10 倍安い。
        /// </summary>
        private static bool TryDeflateAndEmit(PlayerState stateI, NetPeer peer, int chunkStart, int chunkEnd, int rawLen, int budget, ref long bundleCount, ref long bundleBytes, out int compressedLen)
        {
            compressedLen = 0;
            byte[] raw = stateI.BundleRawScratch;
            byte[] compressed = stateI.BundleCompressedScratch;
            // LZ4 worst case は rawLen + (rawLen / 255) + 16 (MaximumOutputSize が返す値)。
            int compCapacityNeeded = BundleHeaderSize + LZ4Codec.MaximumOutputSize(rawLen);
            if (compressed == null || compressed.Length < compCapacityNeeded)
            {
                if (compressed != null) ArrayPool<byte>.Shared.Return(compressed);
                compressed = ArrayPool<byte>.Shared.Rent(Math.Max(compCapacityNeeded, 4096));
                stateI.BundleCompressedScratch = compressed;
            }

            bool profiling = BSRProfiler.Enabled;
            long deflateStart = profiling ? Stopwatch.GetTimestamp() : 0;

            // wire packet の payload region に直接 encode する。
            // destination span が十分大きくない場合は -1 を返す。上の sizing を考えると起きないはずだが、
            // 起きた場合は overshoot として扱い、caller に小さめで retry させる。
            compressedLen = LZ4Codec.Encode(
                raw.AsSpan(0, rawLen),
                compressed.AsSpan(BundleHeaderSize, compressed.Length - BundleHeaderSize),
                LZ4Level.L00_FAST);

            if (profiling) Interlocked.Add(ref BSRProfiler.bundleDeflateTicks, Stopwatch.GetTimestamp() - deflateStart);

            if (compressedLen <= 0 || compressedLen > budget)
            {
                return false;
            }

            int wireLen = BundleHeaderSize + compressedLen;
            int chunkCount = chunkEnd - chunkStart;
            compressed[0] = (byte)Math.Min(chunkCount, 255);
            BinaryPrimitives.WriteUInt16LittleEndian(compressed.AsSpan(1, 2), (ushort)Math.Min(rawLen, ushort.MaxValue));

            peer.SendUnreliableRawMerge(compressed, 0, wireLen, BasisNetworkCommons.CompressedAvatarBundleChannel);
            bundleCount++;
            bundleBytes += wireLen;

            if (profiling)
            {
                Interlocked.Increment(ref BSRProfiler.bundlesEmitted);
                Interlocked.Add(ref BSRProfiler.bundleMessages, chunkCount);
                Interlocked.Add(ref BSRProfiler.bundleRawBytes, rawLen);
                Interlocked.Add(ref BSRProfiler.bundleCompressedBytes, compressedLen);
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateRatioEMA(ref float ema, int compressed, int raw, float weightOnObserved)
        {
            if (raw <= 0) return;
            float observed = (float)compressed / raw;
            if (observed < 0.05f) observed = 0.05f;
            if (observed > 0.99f) observed = 0.99f;
            float prev = ema;
            if (prev < 0.05f || prev > 0.95f) prev = observed; // unseeded なら採用する。
            ema = prev * (1f - weightOnObserved) + observed * weightOnObserved;
        }

        /// <summary>
        /// UsedQualities bitmask 内の quality bit を atomic に set する。
        /// parallel send loop thread から CAS で lock-free に呼ばれる。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkQualityUsed(ref int usedQualities, int qi)
        {
            int bit = 1 << qi;
            // bit は sticky (send loop では set だけで clear しない) なので、
            // plain read で "set" が見えた場合は常に正しい。
            // 最初の数 tick 後、4 bit すべてが converge した common case で Volatile.Read barrier を避ける。
            if ((usedQualities & bit) != 0) return;

            int cur = Volatile.Read(ref usedQualities);
            while (true)
            {
                if ((cur & bit) != 0) return;
                int updated = cur | bit;
                int was = Interlocked.CompareExchange(ref usedQualities, updated, cur);
                if (was == cur) return;
                cur = was;
            }
        }

        /// <summary>
        /// squared distance を quality index へ map する (BitQuality enum value と一致)。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetQualityIndex(float distSq)
        {
            if (distSq <= HighDistanceSq) return 3;   // High
            if (distSq <= MediumDistanceSq) return 2;  // Medium
            if (distSq <= LowDistanceSq) return 1;     // Low
            return 0;                                   // VeryLow
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateIntervalFromDistanceSq(float distanceSq, out byte offsetByte, out int actualInterval)
        {
            int rawInterval = (int)(BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + (distanceSq * BSRSIncreaseRate)));
            int encodedInterval = rawInterval - BSRSMillisecondDefaultInterval;

            offsetByte = (byte)Math.Clamp(encodedInterval, 0, byte.MaxValue);
            actualInterval = offsetByte + BSRSMillisecondDefaultInterval;
        }

        public static void Shutdown()
        {
            cts.Cancel();
            _tickWake.Set();
        }

        public static void RemovePlayer(int id)
        {
            playersToRemove.Enqueue(id);
        }

        /// <summary>
        /// high quality message から lower quality variant へ AdditionalAvatarData を propagate する。
        /// BuildAllLowerFromHighInto は muscle/position/rotation payload だけを扱うため、
        /// additional data (blendshape、custom avatar behaviour) は別途 propagate する必要がある。
        /// VeryLow quality では additional data を完全に strip する。20m+ では face/detail data が見えないため。
        /// </summary>
        private static void PropagateAdditionalData(
            in LocalAvatarSyncMessage high,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            medium.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            medium.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            medium.LinkedAvatarIndex = high.LinkedAvatarIndex;

            low.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            low.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            low.LinkedAvatarIndex = high.LinkedAvatarIndex;

            veryLow.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            veryLow.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            veryLow.LinkedAvatarIndex = high.LinkedAvatarIndex;
        }
        /// <summary>
        /// high-quality source からすべての lower quality array へ position byte を copy する。
        /// position encoding は全 quality level で同一。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyPositionToLowerQualities(
            byte[] highArray,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            if (medium.array != null)
                Buffer.BlockCopy(highArray, 0, medium.array, 0, WritePosition);
            if (low.array != null)
                Buffer.BlockCopy(highArray, 0, low.array, 0, WritePosition);
            if (veryLow.array != null)
                Buffer.BlockCopy(highArray, 0, veryLow.array, 0, WritePosition);
        }

        private static void ProcessMessage(QueuedMessage message)
        {
            if (message.FromPeer == null)
            {
                QueuedMessagePool.Return(message);
                return;
            }

            int id = message.FromPeer.Id;
            byte inboundSeq = message.Sequence;

            var poolMsg = message.AvatarMessage;

            if (poolMsg.array == null)
            {
                BNL.LogError($"[ProcessMessage] Avatar array is null for peer {id}");
                QueuedMessagePool.Return(message);
                return;
            }

            if (BasisNetworkServer.Security.BasisGlobalLockManager.AdditionalAvatarDataLock)
            {
                poolMsg.AdditionalAvatarDatas = null;
                poolMsg.AdditionalAvatarDataSize = 0;
            }

            var incomingQuality = (BitQuality)poolMsg.DataQualityLevel;
            bool isHighQuality = incomingQuality == BitQuality.High;

            if (!BasisAvatarBitPacking.IsValidQuality(incomingQuality))
            {
                QueuedMessagePool.Return(message);
                return;
            }

            int expectedPayloadSize = BasisAvatarBitPacking.ConvertToSize(incomingQuality);
            if (poolMsg.array.Length < expectedPayloadSize)
            {
                QueuedMessagePool.Return(message);
                return;
            }

            var pos = BasisNetworkCompressionExtensions.ReadPosition(ref poolMsg.array);

            // state.AvatarHigh が自身の buffer を所有するよう、avatar payload を deep-copy する。
            // この copy がないと QueuedMessagePool.Return() が byte[] を保持し、
            // 他 peer に re-rent して state.AvatarHigh.array を静かに overwrite してしまう。
            // per-message heap allocation (約 208 bytes * 11K/sec) を避けるため ArrayPool を使う。
            byte[] rentedArray = ArrayPool<byte>.Shared.Rent(expectedPayloadSize);
            Buffer.BlockCopy(poolMsg.array, 0, rentedArray, 0, expectedPayloadSize);
            var high = new LocalAvatarSyncMessage
            {
                DataQualityLevel = poolMsg.DataQualityLevel,
                AdditionalAvatarDatas = poolMsg.AdditionalAvatarDatas,
                AdditionalAvatarDataSize = poolMsg.AdditionalAvatarDataSize,
                LinkedAvatarIndex = poolMsg.LinkedAvatarIndex,
                array = rentedArray,
            };

            if (!playerStates.TryGetValue(id, out var state))
            {
                state = new PlayerState
                {
                    Peer = message.FromPeer,
                    IsActive = true,
                    Position = pos,
                    SyncMessage = new ServerSideSyncPlayerMessage
                    {
                        playerIdMessage = new PlayerIdMessage { playerID = (ushort)id },
                        avatarSerialization = high
                    },
                    AvatarHigh = high,
                    HighArrayActualSize = expectedPayloadSize,
                    PeerTracking = new PeerTrackingData[InitialPlayerArrayCapacity],
                    DataGeneration = 1,
                    LastInboundSequence = inboundSeq,
                    HasReceivedFirst = true,
                    OutboundSequence = 0,
                    SmallId = id <= byte.MaxValue,
                };

                if (isHighQuality)
                {
                    try
                    {
                        AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                        // high を lower slot へ alias しない。そうすると High-packed muscle data を
                        // lower-quality channel で送ることになり、bit-width mismatch が起きる。
                        // array を null にして PreSerializeAll に skip させる。
                        // repacker の EnsureBuffer と position-only fast path はどちらも null を安全に扱う。
                        state.AvatarMedium.array = null;
                        state.AvatarLow.array = null;
                        state.AvatarVeryLow.array = null;
                    }
                }
                else
                {
                    // Non-High quality は downward repack できない。
                    // wrong channel で mismatched quality data を送らないよう lower slot を null にする。
                    state.AvatarMedium.array = null;
                    state.AvatarLow.array = null;
                    state.AvatarVeryLow.array = null;
                }

                // additional avatar data (例: blendshape) を quality variant へ propagate する。
                // BuildAllLowerFromHighInto は muscle/position payload だけを扱うため、
                // additional data は別途 copy する必要がある。
                PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;

                // first frame: pre-serialize。
                PreSerializeAll(state);

                playerStates[id] = state;

                // active players list に追加する。
                lock (_activePlayersLock)
                {
                    _activePlayers.Add((id, state));
                    _activePlayersDirty = true;
                    Interlocked.Increment(ref _activePlayerCount);
                }
            }
            else
            {
                // peer-slot reuse: LiteNetLib は disconnect 後に NetPeer id を recycle する。
                // incoming peer が別 instance の場合、保存済み Peer は古い disconnected peer で、
                // そこへ send しても静かに no-op になるため、新しい player は avatar data を受け取れない。
                // Peer ref を refresh し、next frame を first frame として扱うことで、
                // sequence-delta check が前 player の last sequence と比較して drop しないようにする。
                if (!ReferenceEquals(state.Peer, message.FromPeer))
                {
                    state.Peer = message.FromPeer;
                    state.HasReceivedFirst = false;
                    state.SmallId = id <= byte.MaxValue;
                }

                // stale inbound packet を drop する (unreliable は out of order で届く可能性がある)。
                if (state.HasReceivedFirst)
                {
                    byte delta = unchecked((byte)(inboundSeq - state.LastInboundSequence));
                    if (delta == 0 || delta >= 128)
                    {
                        // duplicate または stale なので discard する。直前に rent した array は返す。
                        ArrayPool<byte>.Shared.Return(rentedArray);
                        QueuedMessagePool.Return(message);
                        return;
                    }
                }
                state.LastInboundSequence = inboundSeq;
                state.HasReceivedFirst = true;

                if (!state.IsActive)
                {
                    state.IsActive = true;
                }

                state.Position = pos;

                // この sender の新しい update 用に outbound sequence を increment する。
                unchecked { state.OutboundSequence++; }

                byte[] prevArray = state.AvatarHigh.array;
                int prevActualSize = state.HighArrayActualSize;
                state.AvatarHigh = high;
                state.HighArrayActualSize = expectedPayloadSize;

                if (isHighQuality)
                {
                    // muscles+tail が変わったか確認する (position だけが動いた場合は高価な bit repacking を skip)。
                    // ArrayPool は大きめの array を返すことがあるため、.Length ではなく HighArrayActualSize を使う。
                    int muscleAndTailBytes = HighMuscleAndTailBytes;
                    bool musclesOrTailChanged = prevArray == null
                        || ReferenceEquals(prevArray, high.array)
                        || prevActualSize != expectedPayloadSize
                        || !high.array.AsSpan(WritePosition, muscleAndTailBytes)
                            .SequenceEqual(prevArray.AsSpan(WritePosition, muscleAndTailBytes));

                    // lower quality array のいずれかが null の場合は full repack を強制する
                    // (例: 前回の repack failure 後)。
                    // これをしないと position-only fast path が null array を無期限に skip し、
                    // 遠い receiver がその player を見られなくなる。
                    bool needsRecovery = state.AvatarMedium.array == null
                        || state.AvatarLow.array == null
                        || state.AvatarVeryLow.array == null;

                    if (musclesOrTailChanged || needsRecovery)
                    {
                        try
                        {
                            AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                        }
                        catch (Exception ex)
                        {
                            BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                            state.AvatarMedium.array = null;
                            state.AvatarLow.array = null;
                            state.AvatarVeryLow.array = null;
                        }
                    }
                    else
                    {
                        // position-only fast path: position byte をすべての lower quality へ copy する。
                        // position は全 quality level で同一 (bit-width difference なし)。
                        CopyPositionToLowerQualities(high.array, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                    }
                }
                else
                {
                    // Non-High quality は安全に downward repack できない。
                    state.AvatarMedium.array = null;
                    state.AvatarLow.array = null;
                    state.AvatarVeryLow.array = null;
                }

                // additional avatar data を quality variant へ propagate する。
                PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;

                // SyncMessage (shell) を同期した状態に保つ。
                state.SyncMessage.avatarSerialization = high;

                PreSerializeAll(state);

                // muscle comparison が終わったので、前 tick の array を pool へ返す。
                if (prevArray != null)
                {
                    ArrayPool<byte>.Shared.Return(prevArray);
                }

                // single atomic increment により、全 other player に対する O(N) CAS bit-setting を置き換える。
                // receiver はこの generation と LastSeenGeneration を比較して new data を検出する。
                Interlocked.Increment(ref state.DataGeneration);
            }

            QueuedMessagePool.Return(message);
        }

        #region Pre-serialization

        /// <summary>
        /// receiver がいる quality level だけ keyframe message を pre-serialize する。
        /// UsedQualities bit は send loop から accumulate され、reset されない。
        /// これにより tick slicing が quality oscillation を起こさない。
        /// 各 slice は必要な bit を contribute し、それが tick をまたいで保持される。
        /// 数 tick 以内に正しい set へ converge する。
        /// receiver がいない quality level は SerializedKeyframeLength を 0 にするため、
        /// send loop はそれを skip し、next tick に必要と mark する (1 tick catch-up delay、約 4ms)。
        /// player の first frame では 4 level すべてを serialize する。
        /// </summary>
        private static void PreSerializeAll(PlayerState state)
        {
            ushort playerId = state.SyncMessage.playerIdMessage.playerID;

            // accumulated quality bit を読む。bit は sticky で、send loop の MarkQualityUsed により set され、
            // reset されない。tick slicing (32 slices) では、各 slice の receiver が successive tick で
            // 必要な quality bit を contribute する。reset しないことで、
            // 各 tick が receiver の 1/32 由来の bit だけを持つ oscillation を防ぐ。
            int mask = Volatile.Read(ref state.UsedQualities);
            if (mask == 0) mask = 0xF; // new player or no sends yet: serialize all

            LocalAvatarSyncMessage msg;
            for (int qi = 0; qi < 4; qi++)
            {
                if ((mask & (1 << qi)) != 0)
                {
                    msg = qi switch
                    {
                        0 => state.AvatarVeryLow,
                        1 => state.AvatarLow,
                        2 => state.AvatarMedium,
                        _ => state.AvatarHigh,
                    };
                    PreSerializeKeyframe(state, qi, msg, playerId);
                    BSRProfiler.IncrementPreSerializations();
                }
                else
                {
                    // not available と mark する。send loop は skip し、next tick 用に request する。
                    state.SerializedKeyframeLength[qi] = 0;
                    BSRProfiler.IncrementPreSerializationsSkipped();
                }
            }
        }

        private static void PreSerializeKeyframe(PlayerState state, int qi, LocalAvatarSyncMessage msg, ushort playerId)
        {
            if (msg.array == null)
            {
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            var quality = (BitQuality)msg.DataQualityLevel;

            // guard: message の quality は quality slot index と一致している必要がある。
            // client が lower quality を送った場合、AvatarHigh が non-High quality data を含むことがある。
            // この check がないと payload が wrong channel で送られ、
            // receiver 側で size mismatch が起きる (例: "Need 169, have 99")。
            if ((int)quality != qi)
            {
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            int expectedPayload = BasisAvatarBitPacking.ConvertToSize(quality);

            // array が undersized の場合は skip する (例: client が wrong quality level を送った場合)。
            if (msg.array.Length < expectedPayload)
            {
                BNL.LogError($"[PreSerializeKeyframe] Array undersized for quality {quality}: got {msg.array.Length}, need {expectedPayload}. Skipping.");
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            // Byte-ID:   [PlayerID:1][interval:1][sequence:1][array:N][additional...]
            // Ushort-ID: [PlayerID:2][interval:1][sequence:1][array:N][additional...]
            // quality と additional-data の有無は channel number から derive する。
            bool hasAdditional = state.HasAdditionalData
                && msg.AdditionalAvatarDatas != null
                && msg.AdditionalAvatarDatas.Length > 0
                && msg.AdditionalAvatarDatas.Length <= 255;

            int additionalSize = 0;
            if (hasAdditional)
            {
                additionalSize = 1 + 1; // AdditionalSize + LinkedAvatarIndex。
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    additionalSize += 1 + 1 + (msg.AdditionalAvatarDatas[i].array?.Length ?? 0); // PayloadSize + messageIndex + data。
                }
            }

            int idSize = state.SmallId ? 1 : 2;
            int totalSize = idSize + 1 + 1 + expectedPayload + additionalSize;

            if (state.SerializedKeyframe[qi] == null || state.SerializedKeyframe[qi].Length < totalSize)
            {
                state.SerializedKeyframe[qi] = new byte[totalSize];
            }

            // SerializedKeyframe へ直接 write する。
            // intermediate NetDataWriter buffer と最後の BlockCopy を避ける
            // (200K+ pre-serializations/5s で約 40MB/sec 節約)。
            byte[] dst = state.SerializedKeyframe[qi];
            int offset = 0;

            if (state.SmallId)
            {
                dst[offset++] = (byte)playerId;
            }
            else
            {
                dst[offset++] = (byte)(playerId & 0xFF);
                dst[offset++] = (byte)((playerId >> 8) & 0xFF);
            }

            dst[offset++] = 0; // interval placeholder (send loop で receiver ごとに patch)。
            dst[offset++] = state.OutboundSequence;

            Buffer.BlockCopy(msg.array, 0, dst, offset, expectedPayload);
            offset += expectedPayload;

            if (hasAdditional)
            {
                dst[offset++] = (byte)msg.AdditionalAvatarDatas.Length;
                dst[offset++] = msg.LinkedAvatarIndex;
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    var ad = msg.AdditionalAvatarDatas[i];
                    if (ad.array == null || ad.array.Length > 255)
                    {
                        dst[offset++] = 0;
                    }
                    else
                    {
                        byte payloadSize = (byte)ad.array.Length;
                        dst[offset++] = payloadSize;
                        dst[offset++] = ad.messageIndex;
                        if (payloadSize > 0)
                        {
                            Buffer.BlockCopy(ad.array, 0, dst, offset, payloadSize);
                            offset += payloadSize;
                        }
                    }
                }
            }

            state.SerializedKeyframeLength[qi] = offset;
        }

        #endregion
    }

    /// <summary>
    /// power-of-two-sharded な ConcurrentDictionary&lt;int, TValue&gt; replacement。
    /// scrambled key で index される N=NextPow2(ProcessorCount) 個の inner dict に write を分散し、
    /// ingress storm 時の per-bucket lock contention を約 N 倍減らす
    /// (1k+ players、250Hz での HandleAvatarMovement -> currentMessages と
    /// ProcessMessage -> playerStates upsert)。
    /// reference-swappable で、既存の Interlocked.Exchange double-buffer pattern を維持する。
    /// enumeration は shard を sequential に歩き、ConcurrentDictionary の per-shard snapshot semantics を継承する。
    /// </summary>
    public sealed class ShardedConcurrentDictionary<TValue> : System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, TValue>>
    {
        private readonly ConcurrentDictionary<int, TValue>[] _shards;
        private readonly int _mask;

        public ShardedConcurrentDictionary()
            : this(NextPowerOfTwo(Math.Max(1, Environment.ProcessorCount))) { }

        public ShardedConcurrentDictionary(int shardCount)
        {
            if (shardCount <= 0 || (shardCount & (shardCount - 1)) != 0)
                throw new ArgumentException("shardCount must be a positive power of two", nameof(shardCount));
            _shards = new ConcurrentDictionary<int, TValue>[shardCount];
            _mask = shardCount - 1;
            for (int i = 0; i < shardCount; i++) _shards[i] = new ConcurrentDictionary<int, TValue>();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private ConcurrentDictionary<int, TValue> ShardOf(int key) => _shards[Scramble(key) & _mask];

        // 32-bit integer hash mix (Murmur3-style)。
        // player id は LiteNetLib が割り当てる dense small int で、
        // scrambling なしでは low-bit masking により id 0..N-1 がすべて shard 0 へ hash され、
        // shard split の意味が完全になくなる。
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int Scramble(int key)
        {
            unchecked
            {
                uint x = (uint)key;
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return (int)x;
            }
        }

        public bool TryGetValue(int key, out TValue value) => ShardOf(key).TryGetValue(key, out value);
        public bool TryRemove(int key, out TValue value) => ShardOf(key).TryRemove(key, out value);

        public TValue this[int key]
        {
            get => ShardOf(key)[key];
            set => ShardOf(key)[key] = value;
        }

        public void Clear()
        {
            for (int i = 0; i < _shards.Length; i++) _shards[i].Clear();
        }

        public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<int, TValue>> GetEnumerator()
        {
            for (int i = 0; i < _shards.Length; i++)
            {
                foreach (var kvp in _shards[i]) yield return kvp;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private static int NextPowerOfTwo(int x)
        {
            if (x <= 1) return 1;
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }
    }
}
