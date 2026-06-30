using Basis.Network.Core;
using BasisNetworking.InitialData;
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using static SerializableBasis;

public static class BasisNetworkServerLibrary
{
    /// <summary>
    /// <see cref="BasisNetworkCommons.ServerLibraryChannel"/> 上の wire format:
    ///   [u16 rawLen][u16 compressedLen][bytes payload]
    /// <c>compressedLen == 0</c> の場合、payload は raw <see cref="ServerLibraryMessage"/> bytes。
    /// それ以外の場合、payload は LZ4-encoded で、decompress すると <c>rawLen</c> bytes になる。
    ///
    /// wire payload は cache され、admin が library を mutate した場合
    /// (<see cref="BroadcastLibraryToAll"/> 経由) だけ rebuild される。
    /// peer ごとの join では cached byte を pooled writer へ memcpy するだけなので、
    /// hot path の新規 allocation は 0。
    /// </summary>
    private static byte[] _cachedWire = Array.Empty<byte>();
    private static int _cachedWireLen;
    private static readonly object _cacheLock = new object();

    public static void SendLibraryToPeer(NetPeer peer)
    {
        if (!TryGetCachedWire(out byte[] wire, out int wireLen)) return;
        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(wire, 0, wireLen);
        NetworkServer.TrySend(peer, writer, BasisNetworkCommons.ServerLibraryChannel, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    public static void BroadcastLibraryToAll()
    {
        // library が mutate されたため、broadcast 前に cache を rebuild する。
        lock (_cacheLock) RebuildCacheLocked();
        if (_cachedWireLen == 0) return;

        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(_cachedWire, 0, _cachedWireLen);
        NetworkServer.BroadcastMessageToClients(
            writer,
            BasisNetworkCommons.ServerLibraryChannel,
            NetworkServer.PeerSnapshot,
            DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    private static bool TryGetCachedWire(out byte[] wire, out int wireLen)
    {
        lock (_cacheLock)
        {
            if (_cachedWireLen == 0)
            {
                RebuildCacheLocked();
            }
            wire = _cachedWire;
            wireLen = _cachedWireLen;
            return wireLen > 0;
        }
    }

    private static void RebuildCacheLocked()
    {
        var loaded = BasisDefaultLibraryLoader.LoadedItems;
        int count = loaded?.Count ?? 0;

        // BasisDefaultLibraryLoader.LoadedItems から rented writer へ直接 serialize する。
        // 以前の version が作っていた ServerLibraryItem[] allocation を避ける。
        // ServerLibraryMessage.Serialize と byte-for-byte で一致させる。
        NetDataWriter raw = NetworkServer.RentWriter();
        try
        {
            raw.Put((ushort)count);
            for (int i = 0; i < count; i++)
            {
                var src = loaded[i];
                raw.Put(src.Mode);
                raw.Put(src.Url ?? string.Empty);
                raw.Put(src.Password ?? string.Empty);
            }
            int rawLen = raw.Length;
            if (rawLen <= 0 || rawLen > ushort.MaxValue)
            {
                _cachedWireLen = 0;
                return;
            }

            int maxOut = LZ4Codec.MaximumOutputSize(rawLen);
            byte[] compressedScratch = ArrayPool<byte>.Shared.Rent(maxOut);
            try
            {
                int compressedLen = LZ4Codec.Encode(
                    raw.Data.AsSpan(0, rawLen),
                    compressedScratch.AsSpan(0, maxOut),
                    LZ4Level.L00_FAST);

                bool useCompressed = compressedLen > 0
                    && compressedLen < rawLen
                    && compressedLen <= ushort.MaxValue;
                int payloadLen = useCompressed ? compressedLen : rawLen;
                int wireLen = 4 + payloadLen; // [u16 rawLen][u16 compressedLen]

                // wire format が実際に大きくなった場合だけ cache buffer を grow する。
                // それ以外は既存 allocation を再利用する。
                if (_cachedWire.Length < wireLen)
                {
                    _cachedWire = new byte[Math.Max(wireLen, 256)];
                }

                // [u16 rawLen] (little-endian。NetDataWriter.Put(ushort) と一致)
                _cachedWire[0] = (byte)rawLen;
                _cachedWire[1] = (byte)(rawLen >> 8);
                // [u16 compressedLen]。0 は payload が raw であることを意味する。
                int compLenWire = useCompressed ? compressedLen : 0;
                _cachedWire[2] = (byte)compLenWire;
                _cachedWire[3] = (byte)(compLenWire >> 8);

                Buffer.BlockCopy(
                    useCompressed ? compressedScratch : raw.Data,
                    0,
                    _cachedWire,
                    4,
                    payloadLen);
                _cachedWireLen = wireLen;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedScratch);
            }
        }
        finally
        {
            NetworkServer.ReturnWriter(raw);
        }
    }
}
