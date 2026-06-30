using System;
using System.Collections.Generic;
using static SerializableBasis;
namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class QueuedMessagePool
    {
        private const int ThreadLocalCapacity = 64;

        [ThreadStatic]
        private static List<QueuedMessage> t_pool;

        public static QueuedMessage Rent()
        {
            var local = t_pool;
            if (local != null && local.Count > 0)
            {
                int last = local.Count - 1;
                var msg = local[last];
                local.RemoveAt(last);
                return msg;
            }
            return new QueuedMessage();
        }

        public static void Return(QueuedMessage msg)
        {
            msg.FromPeer = null;
            msg.Sequence = 0;
            // msg.AvatarMessage.array を保持し、次回 Rent 時に再利用できるようにする。
            // deserialize ごとに新しい byte[] を allocate しないため。
            var saved = msg.AvatarMessage;
            msg.AvatarMessage = new LocalAvatarSyncMessage { array = saved.array };

            var local = t_pool;
            if (local == null)
            {
                local = new List<QueuedMessage>(ThreadLocalCapacity);
                t_pool = local;
            }
            if (local.Count < ThreadLocalCapacity)
            {
                local.Add(msg);
            }
            // それ以外は drop する。GC に回収させ、pool を bounded に保つ。
        }
    }
}
