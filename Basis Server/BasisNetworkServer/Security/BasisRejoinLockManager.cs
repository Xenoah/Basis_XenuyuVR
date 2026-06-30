using Basis.Network.Core;
using System.Collections.Concurrent;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// runtime-only の "rejoin-only" lockdown。
    /// restriction mode が <see cref="BasisNetworkCore.Security.BasisUserRestrictionMode.RejoinOnly"/> の間は、
    /// mode 有効化時点で capture された UUID だけが (re)connect でき、新規 user は入れない。
    /// set は disconnect をまたいで保持される (capture 済み player は退出して再参加できる) が、増えることはない。
    /// disk には persist されない。restart すると mode は Normal に戻り、set は空から始まる。
    /// </summary>
    public static class BasisRejoinLockManager
    {
        private static readonly ConcurrentDictionary<string, byte> _allowed = new ConcurrentDictionary<string, byte>();

        public static int Count => _allowed.Count;

        /// <summary>
        /// 現在 authenticated 済みの全 peer の UUID を allowed set として snapshot する。
        /// restriction mode が RejoinOnly へ transition したときに呼ぶ。
        /// </summary>
        public static void CaptureCurrentPopulation()
        {
            _allowed.Clear();
            if (NetworkServer.AuthIdentity == null) return;

            foreach (NetPeer peer in NetworkServer.AuthenticatedPeers.Values)
            {
                if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) && !string.IsNullOrEmpty(uuid))
                {
                    _allowed.TryAdd(uuid, 0);
                }
            }

            BNL.Log($"Rejoin-only lockdown enabled — captured {_allowed.Count} current player(s).");
        }

        /// <summary>captured set を drop する (mode が RejoinOnly 以外へ変わった)。</summary>
        public static void Clear() => _allowed.Clear();

        /// <summary>lockdown 有効化時にこの UUID が接続済みだった場合 true。</summary>
        public static bool IsAllowed(string uuid) => !string.IsNullOrEmpty(uuid) && _allowed.ContainsKey(uuid);
    }
}
