using System;
using System.Collections.Generic;
using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// player の local return message に添えるために必要な data をすべて含む。
    /// この message はより async で、local player に関する変更に使えることを目的とする。
    /// </summary>
    public struct ServerMetaDataMessage
    {
        public ClientMetaDataMessage ClientMetaDataMessage;

        public int SyncInterval;
        public int BaseMultiplier;
        public float IncreaseRate;
        public float SlowestSendRate;
        public int PeerLimit;
        // この player が持つ permission を client へ含めたい。
        public byte[] PermissionsBitset;     // fast, fixed — known nodes as bits
        public string[] ExtraPermissions;    // dynamic fallback — compressed on the wire

        /// <summary>
        /// allowed permission node string の collection から bitset + extras を populate する。
        /// serialize 前に server 側で呼ぶ。
        /// </summary>
        public void SetPermissions(IReadOnlyCollection<string> allowedNodes, IReadOnlyCollection<string> deniedNodes = null)
        {
            PermissionBitsetMap.Encode(allowedNodes, out PermissionsBitset, out ExtraPermissions, deniedNodes);
        }

        /// <summary>
        /// bitset + extras を full set の permission node string へ decode する。
        /// deserialize 後に client 側で呼ぶ。
        /// </summary>
        public HashSet<string> GetPermissions()
        {
            return PermissionBitsetMap.Decode(PermissionsBitset, ExtraPermissions);
        }

        public void Deserialize(NetDataReader Writer)
        {
            ClientMetaDataMessage.Deserialize(Writer);

            Writer.Get(out SyncInterval);
            Writer.Get(out BaseMultiplier);
            Writer.Get(out IncreaseRate);
            Writer.Get(out SlowestSendRate);
            Writer.Get(out PeerLimit);

            // permissions (backward compatible。data が残っていなければ skip)。
            if (Writer.AvailableBytes > 0)
            {
                PermissionsBitset = Writer.GetBytesWithLength();

                Writer.Get(out ushort extraCount);
                if (extraCount > 0)
                {
                    byte[] compressed = Writer.GetBytesWithLength();
                    ExtraPermissions = PermissionCompression.DecompressExtras(compressed, extraCount);
                }
                else
                {
                    ExtraPermissions = Array.Empty<string>();
                }
            }
            else
            {
                PermissionsBitset = Array.Empty<byte>();
                ExtraPermissions = Array.Empty<string>();
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            ClientMetaDataMessage.Serialize(Writer);

            if (SyncInterval == 0)
            {
                SyncInterval = 50;
                BNL.LogError("SyncInterval was not set! ");
            }
            if (BaseMultiplier == 0)
            {
                BaseMultiplier = 1;
                BNL.LogError("Base Multiplier was not set! ");
            }
            if (IncreaseRate == 0)
            {
                IncreaseRate = 0.005f;
                BNL.LogError("IncreaseRate was not set! ");
            }
            if (SlowestSendRate == 0)
            {
                SlowestSendRate = 2.55f;
                BNL.LogError("Slowest Send Rate was not set!");
            }

            Writer.Put(SyncInterval);
            Writer.Put(BaseMultiplier);
            Writer.Put(IncreaseRate);
            Writer.Put(SlowestSendRate);
            Writer.Put(PeerLimit);

            // permissions bitset (ushort length prefix + PutBytesWithLength 経由の raw bytes)。
            Writer.PutBytesWithLength(PermissionsBitset ?? Array.Empty<byte>());

            // extra permissions (compressed)。
            ushort extraCount = (ushort)(ExtraPermissions != null ? ExtraPermissions.Length : 0);
            Writer.Put(extraCount);
            if (extraCount > 0)
            {
                byte[] compressed = PermissionCompression.CompressExtras(ExtraPermissions);
                Writer.PutBytesWithLength(compressed);
            }
        }
    }
}
