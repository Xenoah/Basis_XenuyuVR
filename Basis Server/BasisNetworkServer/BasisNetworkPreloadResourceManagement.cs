using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static SerializableBasis;

/// <summary>
/// synchronized resource load 用の server-side tracking。
/// どの client が readiness を報告したかを追跡し、全員が ready になるか
/// timeout が切れたときに spawn signal を trigger する。
/// </summary>
public static class BasisNetworkPreloadResourceManagement
{
    /// <summary>
    /// synchronized load の timeout。この duration 後は、報告済み client 数に関係なく
    /// server が spawn signal を送る。
    /// </summary>
    public static readonly TimeSpan SynchronizedTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// active synchronized load session。LoadedNetID を key にする。
    /// </summary>
    public static readonly ConcurrentDictionary<string, SyncLoadSession> ActiveSessions = new();

    public class SyncLoadSession
    {
        public LocalLoadResource Resource;
        public HashSet<int> ReadyPeers = new();
        public HashSet<int> FailedPeers = new();
        public DateTime StartTimeUtc;
        public CancellationTokenSource TimeoutCts;

        /// <summary>
        /// この session 開始時点の connected peer 総数。
        /// </summary>
        public int TotalPeerCount;
        public bool IsComplete => ReadyPeers.Count + FailedPeers.Count >= TotalPeerCount;
    }

    /// <summary>
    /// server が LoadStrategy = 2 (Synchronized) の LoadResource を受け取ったときに呼ぶ。
    /// preload request を全 client に broadcast し、readiness tracking を開始する。
    /// </summary>
    public static void StartSynchronizedLoad(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;

        if (ActiveSessions.ContainsKey(netId))
        {
            BNL.LogError($"PreloadResourceManagement: Session already exists for {netId}");
            return;
        }

        var peerSnapshot = NetworkServer.PeerSnapshot;
        int peerCount = peerSnapshot.Length;

        var session = new SyncLoadSession
        {
            Resource = resource,
            StartTimeUtc = DateTime.UtcNow,
            TotalPeerCount = peerCount,
            TimeoutCts = new CancellationTokenSource(),
        };

        if (!ActiveSessions.TryAdd(netId, session))
        {
            BNL.LogError($"PreloadResourceManagement: Failed to add session for {netId}");
            return;
        }

        BNL.Log($"PreloadResourceManagement: Starting synchronized load for {netId}, {peerCount} peers");

        // load resource を全 client へ broadcast する。
        // client は LoadStrategy = 2 を見て synchronized preload として扱う。
        NetDataWriter writer = NetworkServer.RentWriter();
        resource.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.LoadResourceChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);

        // main resource database にも保存する。
        BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd(netId, resource);

        // peer がいない場合、5 分 timeout を待たず即完了する。
        if (peerCount == 0)
        {
            BNL.Log($"PreloadResourceManagement: No peers connected, completing {netId} immediately");
            session.TimeoutCts.Cancel();
            BroadcastSpawnSignal(netId);
            return;
        }

        // timeout task を開始する。
        _ = RunTimeoutAsync(netId, session);
    }

    /// <summary>
    /// server が client から PreloadReady message を受け取ったときに呼ぶ。
    /// </summary>
    public static void HandleClientReady(string loadedNetId, int peerId, bool isReady)
    {
        if (!ActiveSessions.TryGetValue(loadedNetId, out SyncLoadSession session))
        {
            BNL.LogError($"PreloadResourceManagement: Received ready from peer {peerId} for unknown session {loadedNetId}");
            return;
        }

        if (isReady)
        {
            session.ReadyPeers.Add(peerId);
            BNL.Log($"PreloadResourceManagement: Peer {peerId} ready for {loadedNetId} ({session.ReadyPeers.Count + session.FailedPeers.Count}/{session.TotalPeerCount})");
        }
        else
        {
            session.FailedPeers.Add(peerId);
            BNL.Log($"PreloadResourceManagement: Peer {peerId} FAILED for {loadedNetId} ({session.ReadyPeers.Count + session.FailedPeers.Count}/{session.TotalPeerCount})");
        }

        if (session.IsComplete)
        {
            BNL.Log($"PreloadResourceManagement: All peers reported for {loadedNetId}, sending spawn signal");
            session.TimeoutCts.Cancel();
            BroadcastSpawnSignal(loadedNetId);
        }
    }

    /// <summary>
    /// synchronized load session の timeout を実行する。
    /// timeout までに全 client が報告していない場合も、spawn signal を送る。
    /// </summary>
    private static async Task RunTimeoutAsync(string netId, SyncLoadSession session)
    {
        try
        {
            await Task.Delay(SynchronizedTimeout, session.TimeoutCts.Token);

            // timeout 到達。状況に関係なく spawn signal を送る。
            BNL.Log($"PreloadResourceManagement: Timeout reached for {netId}. Ready: {session.ReadyPeers.Count}, Failed: {session.FailedPeers.Count}, Total: {session.TotalPeerCount}");
            BroadcastSpawnSignal(netId);
        }
        catch (TaskCanceledException)
        {
            // session は timeout 前に正常完了済み。
        }
    }

    /// <summary>
    /// synchronized load 用の spawn signal を connected client 全員へ broadcast する。
    /// server tracking の整合性を保つため、既存 scene の unload message も通常の unload path 経由で broadcast する。
    /// client-side の HandleSpawnPreloaded も、message ordering race への safety net として local scene unload を行う。
    /// </summary>
    private static void BroadcastSpawnSignal(string loadedNetId)
    {
        if (!ActiveSessions.TryRemove(loadedNetId, out SyncLoadSession session))
        {
            return;
        }

        session.TimeoutCts.Dispose();

        var peerSnapshot = NetworkServer.PeerSnapshot;

        // synchronized resource 自体が scene の場合だけ、既存 scene を unload する。
        // prop (Mode == 0) が scene unload を引き起こしてはいけない。
        // loadedNetId は除外する。それは切り替え先 scene であり、削除すると
        // client 側の SpawnPreloaded signal と race する。
        if (session.Resource.Mode == 1)
        {
            UnloadAllSceneResources(peerSnapshot, loadedNetId);
        }

        SpawnPreloadedMessage spawnMsg = new SpawnPreloadedMessage
        {
            LoadedNetID = loadedNetId,
        };

        NetDataWriter writer = NetworkServer.RentWriter();
        spawnMsg.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.SpawnPreloadedChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);

        BNL.Log($"PreloadResourceManagement: Spawn signal sent for {loadedNetId}");
    }

    /// <summary>
    /// server database からすべての scene-type resource (Mode == 1) を unload し、
    /// 通常の unload channel 経由で全 client へ unload message を broadcast する。
    /// </summary>
    private static void UnloadAllSceneResources(NetPeer[] peerSnapshot, string excludeNetId = null)
    {
        var sceneResources = BasisNetworkResourceManagement.UshortNetworkDatabase.Values
            .Where(r => r.Mode == 1 && r.LoadedNetID != excludeNetId)
            .ToArray();

        if (sceneResources.Length == 0) return;

        BNL.Log($"PreloadResourceManagement: Unloading {sceneResources.Length} existing scene(s) before synchronized spawn");

        NetDataWriter writer = NetworkServer.RentWriter();
        foreach (var scene in sceneResources)
        {
            BasisNetworkResourceManagement.UshortNetworkDatabase.TryRemove(scene.LoadedNetID, out _);

            UnLoadResource unload = new UnLoadResource
            {
                LoadedNetID = scene.LoadedNetID,
                Mode = 1,
            };
            writer.Reset();
            unload.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.UnloadResourceChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);

            BNL.Log($"PreloadResourceManagement: Unloaded scene {scene.LoadedNetID}");
        }
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// disconnected peer をすべての active synchronized load session から削除する。
    /// expected peer count を decrement し、残りの peer がすでに全員報告済みなら spawn signal を trigger する。
    /// </summary>
    public static void RemovePeer(int peerId)
    {
        List<string> completedSessions = null;

        foreach (var kvp in ActiveSessions)
        {
            var session = kvp.Value;
            session.ReadyPeers.Remove(peerId);
            session.FailedPeers.Remove(peerId);

            if (session.TotalPeerCount > 0)
            {
                session.TotalPeerCount--;
            }

            if (session.TotalPeerCount <= 0)
            {
                // peer が残っていないため cleanup だけ行う。
                session.TimeoutCts?.Cancel();
                session.TimeoutCts?.Dispose();
                ActiveSessions.TryRemove(kvp.Key, out _);
            }
            else if (session.IsComplete)
            {
                completedSessions ??= new List<string>();
                completedSessions.Add(kvp.Key);
            }
        }

        if (completedSessions != null)
        {
            foreach (var netId in completedSessions)
            {
                if (ActiveSessions.TryGetValue(netId, out var session))
                {
                    BNL.Log($"PreloadResourceManagement: All remaining peers reported for {netId} after peer {peerId} disconnected, sending spawn signal");
                    session.TimeoutCts.Cancel();
                    BroadcastSpawnSignal(netId);
                }
            }
        }
    }

    /// <summary>
    /// active session をすべて cleanup する。server reset 時に呼ぶ。
    /// </summary>
    public static void Reset()
    {
        foreach (var kvp in ActiveSessions)
        {
            kvp.Value.TimeoutCts?.Cancel();
            kvp.Value.TimeoutCts?.Dispose();
        }
        ActiveSessions.Clear();
    }
}
