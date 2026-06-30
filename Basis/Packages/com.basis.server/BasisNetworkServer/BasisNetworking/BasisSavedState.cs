using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Basis.Network.Core;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    public static class BasisSavedState
    {
        // data type ごとの thread-safe dictionary。
        private static readonly ConcurrentDictionary<int, ClientAvatarChangeMessage> avatarChangeStates = new();
        private static readonly ConcurrentDictionary<int, ClientMetaDataMessage> playerMetaDataMessages = new();
        private static readonly ConcurrentDictionary<int, List<NetPeer>> resolvedVoicePeers = new();
        private static readonly ConcurrentDictionary<int, bool> shoutModeStates = new();

        /// <summary>
        /// specific player の state data をすべて削除し、
        /// 他 player 全員の cached voice-peer list からも purge する。
        /// </summary>
        public static void RemovePlayer(int id)
        {
            avatarChangeStates.TryRemove(id, out _);
            playerMetaDataMessages.TryRemove(id, out _);
            resolvedVoicePeers.TryRemove(id, out _);
            shoutModeStates.TryRemove(id, out _);

            // disconnected peer を他 player 全員の cached list から purge する。
            // これにより、次の recipient update まで dead peer へ voice packet が送られないようにする。
            foreach (var kvp in resolvedVoicePeers)
            {
                List<NetPeer> peers = kvp.Value;
                if (peers == null) continue;

                lock (peers)
                {
                    for (int i = peers.Count - 1; i >= 0; i--)
                    {
                        NetPeer p = peers[i];
                        if (p != null && p.Id == id)
                        {
                            peers.RemoveAt(i);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// player の ReadyMessage を add / update する。
        /// </summary>
        public static void AddLastData(NetPeer client, ReadyMessage readyMessage)
        {
            int id = client.Id;
            avatarChangeStates[id] = readyMessage.clientAvatarChangeMessage;
            playerMetaDataMessages[id] = readyMessage.playerMetaDataMessage;

          // BNL.Log($"Updated {id} with AvatarID {readyMessage.clientAvatarChangeMessage.byteArray.Length}");
        }

        /// <summary>
        /// VoiceReceiversMessage を cached NetPeer list へ resolve する。
        /// </summary>
        public static void AddLastData(NetPeer client, VoiceReceiversMessage voiceReceiversMessage)
        {
            var peers = GetOrCreateResolvedList(client.Id);

            if (voiceReceiversMessage.Users != null)
            {
                lock (peers)
                {
                    peers.Clear();
                    for (int i = 0; i < voiceReceiversMessage.UsersLength; i++)
                    {
                        if (NetworkServer.AuthenticatedPeers.TryGetValue(voiceReceiversMessage.Users[i], out NetPeer found))
                        {
                            peers.Add(found);
                        }
                    }
                }
                voiceReceiversMessage.ReturnPool();
            }
        }

        /// <summary>
        /// player の ClientAvatarChangeMessage を add / update する。
        /// </summary>
        public static void AddLastData(NetPeer client, ClientAvatarChangeMessage avatarChangeMessage)
        {
            avatarChangeStates[client.Id] = avatarChangeMessage;
        }

        /// <summary>
        /// player の last ClientAvatarChangeMessage を取得する。
        /// </summary>
        public static bool GetLastAvatarChangeState(NetPeer client, out ClientAvatarChangeMessage message)
        {
            return avatarChangeStates.TryGetValue(client.Id, out message);
        }

        /// <summary>
        /// player の last PlayerMetaDataMessage を取得する。
        /// </summary>
        public static bool GetLastPlayerMetaData(NetPeer client, out ClientMetaDataMessage message)
        {
            return playerMetaDataMessages.TryGetValue(client.Id, out message);
        }

        /// <summary>
        /// player の voice receiver に対する cached resolved peer list を取得する。
        /// この list は voice packet ごとではなく、voice receivers message が update されるたびに rebuild される。
        /// </summary>
        public static bool GetResolvedVoicePeers(NetPeer client, out List<NetPeer> peers)
        {
            return resolvedVoicePeers.TryGetValue(client.Id, out peers);
        }

        /// <summary>
        /// player の resolved voice peer list を直接 set する。
        /// ushort[] を先に保存するのではなく deserialize 中に peer を resolve する、
        /// inverted-list mode と bitfield mode で使う。
        /// </summary>
        public static List<NetPeer> GetOrCreateResolvedList(int clientId)
        {
            return resolvedVoicePeers.GetOrAdd(clientId, _ => new List<NetPeer>(64));
        }

        /// <summary>
        /// player の shout mode state を set する。
        /// </summary>
        public static void SetShoutMode(int peerId, bool enabled)
        {
            if (enabled)
            {
                shoutModeStates[peerId] = true;
            }
            else
            {
                shoutModeStates.TryRemove(peerId, out _);
            }
        }

        /// <summary>
        /// player が現在 shout mode の場合 true を返す。
        /// </summary>
        public static bool IsInShoutMode(int peerId)
        {
            return shoutModeStates.TryGetValue(peerId, out _);
        }

        /// <summary>
        /// 現在 shout mode の player ID をすべて返す。
        /// </summary>
        public static int[] GetAllShoutModePlayers()
        {
            return shoutModeStates.Keys.ToArray();
        }
    }
}
