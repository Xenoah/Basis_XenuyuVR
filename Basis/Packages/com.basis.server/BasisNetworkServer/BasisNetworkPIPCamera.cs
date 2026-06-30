using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using static SerializableBasis;

namespace BasisNetworkServer
{
    public class CameraPIPState
    {
        public bool IsActive;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;
        public bool HasNewData;
        public Dictionary<int, long> LastSentTimes = new();
    }

    public static class BasisNetworkPIPCamera
    {
        public static ConcurrentDictionary<int, CameraPIPState> PIPStates = new();
        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;

        /// <summary>
        /// client が PIP camera の作成/破棄を通知する。
        /// </summary>
        public static void HandlePIPStateChange(NetPacketReader reader, NetPeer peer)
        {
            ClientCameraPIPStateMessage clientMsg = new ClientCameraPIPStateMessage();
            clientMsg.Deserialize(reader);
            reader.Recycle();

            ushort peerId = (ushort)peer.Id;

            if (clientMsg.IsActive)
            {
                var state = PIPStates.GetOrAdd(peer.Id, _ => new CameraPIPState());
                state.IsActive = true;
                state.PositionX = clientMsg.PositionX;
                state.PositionY = clientMsg.PositionY;
                state.PositionZ = clientMsg.PositionZ;
                state.RotationX = clientMsg.RotationX;
                state.RotationY = clientMsg.RotationY;
                state.RotationZ = clientMsg.RotationZ;
                state.RotationW = clientMsg.RotationW;
                state.HasNewData = true;

                BNL.Log($"PIP camera created for player {peerId}");
            }
            else
            {
                if (PIPStates.TryGetValue(peer.Id, out var state))
                {
                    state.IsActive = false;
                    state.HasNewData = false;
                    state.LastSentTimes.Clear();
                }

                BNL.Log($"PIP camera destroyed for player {peerId}");
            }

            // state を全 peer へ broadcast する。
            CameraPIPStateMessage outMsg = new CameraPIPStateMessage
            {
                PlayerID = peerId,
                IsActive = clientMsg.IsActive,
                PositionX = clientMsg.PositionX,
                PositionY = clientMsg.PositionY,
                PositionZ = clientMsg.PositionZ,
                RotationX = clientMsg.RotationX,
                RotationY = clientMsg.RotationY,
                RotationZ = clientMsg.RotationZ,
                RotationW = clientMsg.RotationW,
            };

            NetDataWriter writer = NetworkServer.RentWriter();
            outMsg.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.CameraPIPStateChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// client が自身の PIP camera の position update を送る。
        /// </summary>
        public static void HandlePIPPositionUpdate(NetPacketReader reader, NetPeer peer)
        {
            ClientCameraPIPPositionMessage clientMsg = new ClientCameraPIPPositionMessage();
            clientMsg.Deserialize(reader);
            reader.Recycle();

            if (!PIPStates.TryGetValue(peer.Id, out var state) || !state.IsActive)
            {
                return; // ignore position updates for non-existent PIPs
            }

            state.PositionX = clientMsg.PositionX;
            state.PositionY = clientMsg.PositionY;
            state.PositionZ = clientMsg.PositionZ;
            state.RotationX = clientMsg.RotationX;
            state.RotationY = clientMsg.RotationY;
            state.RotationZ = clientMsg.RotationZ;
            state.RotationW = clientMsg.RotationW;
            state.HasNewData = true;
        }

        /// <summary>
        /// reduction system の tick loop から呼ばれる。
        /// avatar movement と同じ distance-based interval を使って、recipient へ PIP position update を送る。
        /// </summary>
        public static void UpdatePIPPositions(long nowTicks)
        {
            NetPeer[] peers = NetworkServer.PeerSnapshot;
            if (peers == null) return;

            foreach (var pipKvp in PIPStates)
            {
                int ownerId = pipKvp.Key;
                CameraPIPState pipState = pipKvp.Value;

                if (!pipState.IsActive || !pipState.HasNewData)
                    continue;

                // distance calc 用に PIP owner の player position を取得する。
                if (!BasisServerReductionSystemEvents.playerStates.TryGetValue(ownerId, out PlayerState ownerPlayerState))
                    continue;

                if (!ownerPlayerState.IsActive)
                    continue;

                // outbound message を一度だけ組み立てる。
                CameraPIPPositionMessage posMsg = new CameraPIPPositionMessage
                {
                    PlayerID = (ushort)ownerId,
                    PositionX = pipState.PositionX,
                    PositionY = pipState.PositionY,
                    PositionZ = pipState.PositionZ,
                    RotationX = pipState.RotationX,
                    RotationY = pipState.RotationY,
                    RotationZ = pipState.RotationZ,
                    RotationW = pipState.RotationW,
                };

                NetDataWriter writer = NetworkServer.RentWriter();
                posMsg.Serialize(writer);

                for (int i = 0; i < peers.Length; i++)
                {
                    NetPeer recipientPeer = peers[i];
                    int recipientId = recipientPeer.Id;

                    if (recipientId == ownerId)
                        continue;

                    if (!BasisServerReductionSystemEvents.playerStates.TryGetValue(recipientId, out PlayerState recipientState))
                        continue;

                    if (!recipientState.IsActive)
                        continue;

                    // recipient と PIP owner の距離。
                    float distSq = DistanceSquared(recipientState.Position, ownerPlayerState.Position);
                    CalculateIntervalFromDistanceSq(distSq, out int actualInterval);

                    if (!pipState.LastSentTimes.TryGetValue(recipientId, out long lastSent))
                        lastSent = 0;

                    long elapsed = Math.Max(0, nowTicks - lastSent);
                    long required = (long)(actualInterval * MsToTick);

                    if (elapsed >= required)
                    {
                        recipientPeer.Send(writer, BasisNetworkCommons.CameraPIPPositionChannel, DeliveryMethod.Sequenced);
                        BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.CameraPIPPositionChannel, writer.Length);
                        pipState.LastSentTimes[recipientId] = nowTicks;
                    }
                }

                NetworkServer.ReturnWriter(writer);
            }
        }

        /// <summary>
        /// newly joined peer へ、active な PIP camera state をすべて送る。
        /// </summary>
        public static void SendPIPStateToPeer(NetPeer newPeer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();

            foreach (var kvp in PIPStates)
            {
                CameraPIPState state = kvp.Value;
                if (!state.IsActive)
                    continue;

                writer.Reset();
                CameraPIPStateMessage msg = new CameraPIPStateMessage
                {
                    PlayerID = (ushort)kvp.Key,
                    IsActive = true,
                    PositionX = state.PositionX,
                    PositionY = state.PositionY,
                    PositionZ = state.PositionZ,
                    RotationX = state.RotationX,
                    RotationY = state.RotationY,
                    RotationZ = state.RotationZ,
                    RotationW = state.RotationW,
                };
                msg.Serialize(writer);
                NetworkServer.TrySend(newPeer, writer, BasisNetworkCommons.CameraPIPStateChannel, DeliveryMethod.ReliableOrdered);
            }

            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// disconnect 時、この player が active PIP を持っていれば destroy を全員へ broadcast する。
        /// </summary>
        public static void RemovePlayer(int peerId)
        {
            if (PIPStates.TryRemove(peerId, out var state) && state.IsActive)
            {
                CameraPIPStateMessage destroyMsg = new CameraPIPStateMessage
                {
                    PlayerID = (ushort)peerId,
                    IsActive = false,
                };

                NetDataWriter writer = NetworkServer.RentWriter();
                destroyMsg.Serialize(writer);
                NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.CameraPIPStateChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                NetworkServer.ReturnWriter(writer);

                BNL.Log($"PIP camera auto-destroyed for disconnected player {peerId}");
            }

            foreach (var kvp in PIPStates)
            {
                kvp.Value.LastSentTimes.Remove(peerId);
            }
        }

        public static void Reset()
        {
            PIPStates.Clear();
        }

        private static float DistanceSquared(Basis.Scripts.Networking.Compression.Vector3 a, Basis.Scripts.Networking.Compression.Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static void CalculateIntervalFromDistanceSq(float distanceSq, out int actualInterval)
        {
            int rawInterval = (int)(BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval *
                (BasisServerReductionSystemEvents.BSRBaseMultiplier + (distanceSq * BasisServerReductionSystemEvents.BSRSIncreaseRate)));
            int encodedInterval = rawInterval - BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval;
            byte offsetByte = (byte)Math.Clamp(encodedInterval, 0, byte.MaxValue);
            actualInterval = offsetByte + BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval;
        }
    }
}
