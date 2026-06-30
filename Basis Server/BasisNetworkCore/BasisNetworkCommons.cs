namespace Basis.Network.Core
{
    public static class BasisNetworkCommons
    {
        /// <summary>
        /// 内部で発生し得る最大 connection 数。
        /// </summary>
        public const int MaxConnections = ushort.MaxValue;

        public const int NetworkIntervalPoll = 2;
        public const int PingInterval = 1500;
        public const int ReceivePollingTime = 50000;
        /// <summary>
        /// LiteNetLib packet pool size。high-throughput send loop 中に新しい NetPacket object を
        /// allocate しないよう、十分に大きい必要がある。1000 players で約 4M sends/sec の場合、
        /// packet は pool 内を高速に循環する。65536 は pool を warm に保ち、GC pressure を避ける。
        /// </summary>
        public const int PacketPoolSize = 65536;
        /// <summary>
        /// 新しい message を追加する場合はこの値を増やす必要がある。
        /// 64 まで機能する。
        /// </summary>
        public const byte TotalChannels = 64;

        // ── connection lifecycle ─────────────────────────────────────────────
        /// <summary>auth identity message。</summary>
        public const byte AuthIdentityChannel = 0;
        /// <summary>player metadata (UUID、display name、permissions)。</summary>
        public const byte metaDataChannel = 1;
        /// <summary>player entity を削除する。</summary>
        public const byte DisconnectionChannel = 2;

        // ── voice ────────────────────────────────────────────────────────────
        /// <summary>spatialized voice data。</summary>
        public const byte VoiceChannel = 3;
        /// <summary>shout mode voice。non-spatialized audio を全 client へ broadcast する。</summary>
        public const byte ShoutVoiceChannel = 4;
        /// <summary>voice recipient list (byte count、255 recipients 以下)。</summary>
        public const byte AudioRecipientsChannel = 5;
        /// <summary>voice recipient list (ushort count、255 recipients 超)。</summary>
        public const byte AudioRecipientsLargeChannel = 39;
        /// <summary>spatialized voice data (ushort playerID、ID が 255 超の場合)。</summary>
        public const byte VoiceLargeChannel = 40;
        /// <summary>voice excluded list (byte count、255 excluded 以下)。server は listed ID 以外の全員に送る。</summary>
        public const byte AudioRecipientsInvertedChannel = 49;
        /// <summary>voice excluded list (ushort count、255 excluded 超)。server は listed ID 以外の全員に送る。</summary>
        public const byte AudioRecipientsInvertedLargeChannel = 50;
        /// <summary>bitfield としての voice recipients。playerID 位置の bit = recipient。</summary>
        public const byte AudioRecipientsBitfieldChannel = 51;

        // ── quality 別 avatar channel ──────────────────────────────────────
        // layout: PlayerAvatarVeryLowChannel + quality * 2 + hasAdditional
        //   6  = VeryLow               7  = VeryLow + Additional
        //   8  = Low                   9  = Low + Additional
        //   10 = Medium               11 = Medium + Additional
        //   12 = High                 13 = High + Additional
        public const byte PlayerAvatarVeryLowChannel = 6;
        public const byte PlayerAvatarVeryLowAdditionalChannel = 7;
        public const byte PlayerAvatarLowChannel = 8;
        public const byte PlayerAvatarLowAdditionalChannel = 9;
        public const byte PlayerAvatarMediumChannel = 10;
        public const byte PlayerAvatarMediumAdditionalChannel = 11;
        public const byte PlayerAvatarHighChannel = 12;
        public const byte PlayerAvatarHighAdditionalChannel = 13;

        // ── avatar management ────────────────────────────────────────────────
        /// <summary>別 avatar へ切り替える。</summary>
        public const byte AvatarChangeMessageChannel = 14;
        /// <summary>generic avatar script data。</summary>
        public const byte AvatarChannel = 15;

        // ── player management ────────────────────────────────────────────────
        /// <summary>remote player entity を作成する。</summary>
        public const byte CreateRemotePlayerChannel = 16;
        /// <summary>新規 join peer 用に remote player entity を作成する。</summary>
        public const byte CreateRemotePlayersForNewPeerChannel = 17;
        /// <summary>player nameplate の上に表示される chat text message。</summary>
        public const byte ChatChannel = 18;

        // ── ownership ────────────────────────────────────────────────────────
        /// <summary>network object の現在 owner を取得する。</summary>
        public const byte GetCurrentOwnerRequestChannel = 19;
        /// <summary>network object の ownership を移譲する。</summary>
        public const byte ChangeCurrentOwnerRequestChannel = 20;
        /// <summary>現在の ownership を削除する。</summary>
        public const byte RemoveCurrentOwnerRequestChannel = 21;

        // ── net IDs ──────────────────────────────────────────────────────────
        /// <summary>net id を割り当てる (string から ushort)。</summary>
        public const byte netIDAssignChannel = 22;
        /// <summary>net id の array を割り当てる (string から ushort)。</summary>
        public const byte NetIDAssignsChannel = 23;

        // ── scene & resources ────────────────────────────────────────────────
        /// <summary>scene script data。</summary>
        public const byte SceneChannel = 24;
        /// <summary>resource を load する (scene、gameobject、script、asset)。</summary>
        public const byte LoadResourceChannel = 25;
        /// <summary>resource を unload する。</summary>
        public const byte UnloadResourceChannel = 26;
        /// <summary>client が resource preload 完了を server へ伝える。ready または failed。</summary>
        public const byte PreloadReadyChannel = 27;
        /// <summary>server が全 client に、preload 済み resource を spawn するよう伝える。</summary>
        public const byte SpawnPreloadedChannel = 28;
        /// <summary>
        /// すでに spawn 済み resource の flag、たとえば Static を変更する。client→server request。
        /// server が item creator または moderator かを authorize し、その後全 client へ rebroadcast する。
        /// 追加時点で 29-54 がすでに使われていたため、Id は他の resource channel と連続しない 55。
        /// </summary>
        public const byte ModifyResourceChannel = 55;

        // ── content sharing ──────────────────────────────────────────────────
        /// <summary>content sphere を drop する。</summary>
        public const byte ContentShareChannel = 29;
        /// <summary>content sphere を削除する。</summary>
        public const byte ContentShareCleanupChannel = 30;

        // ── server-bound ─────────────────────────────────────────────────────
        /// <summary>developer hook。data は server のみに届けられる。</summary>
        public const byte ServerBoundChannel = 31;

        // ── database & admin ─────────────────────────────────────────────────
        /// <summary>server-side database に data を保存する。</summary>
        public const byte StoreDatabaseChannel = 32;
        /// <summary>id で server-side database の data を request する。</summary>
        public const byte RequestStoreDatabaseChannel = 33;
        /// <summary>client からの admin message。</summary>
        public const byte AdminChannel = 34;

        // ── stats, camera & events ───────────────────────────────────────────
        /// <summary>server statistics。</summary>
        public const byte ServerStatisticsChannel = 35;
        /// <summary>PIP camera の created / destroyed state (reliable、player ごと)。</summary>
        public const byte CameraPIPStateChannel = 36;
        /// <summary>PIP camera position update (sequenced、position のみ)。</summary>
        public const byte CameraPIPPositionChannel = 37;
        /// <summary>
        /// generic low-priority events channel。payload の first byte が
        /// event type を識別する。下の EventType constant を参照。
        /// </summary>
        public const byte EventsChannel = 38;

        // ── EventsChannel 用 event type sub-byte ──
        /// <summary>player が photo を撮った時に発火する camera shutter sound。</summary>
        public const byte EventType_CameraShutterSound = 0;
        /// <summary>camera countdown 開始。remote client は tick / shutter timing を replay する。</summary>
        public const byte EventType_CameraCountdown = 1;
        /// <summary>
        /// session-scoped な "temp block" notification。sender は特定の target peer に、
        /// その相手を local に block した、または解除したことを伝える。
        /// target は block を mirror し、自分の client 上で sender の avatar / audio / nameplate を隠せる。
        /// 永続化はしない。
        /// </summary>
        public const byte EventType_PlayerTempBlock = 2;
        // wire (client→server): [eventType:1][intervalMs:2]
        // wire (server→client): [eventType:1][senderId:2][intervalMs:2]
        public const byte EventType_AvatarRateChange = 3;
        /// <summary>nameplate coloring 用の player ごとの talk mode (Normal/Private/Direct/ThisPerson/Shout)。</summary>
        // wire (client→server): [eventType:1][modeByte:1]
        // wire (server→client): [eventType:1][senderId:2][modeByte:1]
        public const byte EventType_TalkModeChanged = 4;
        /// <summary>nameplate coloring 用の player ごとの self-mute state。</summary>
        // wire (client→server): [eventType:1][muted:1]
        // wire (server→client): [eventType:1][senderId:2][muted:1]
        public const byte EventType_MuteStateChanged = 5;
        /// <summary>remote player の一時的な chat typing state。</summary>
        public const byte EventType_PlayerChatTyping = 6;
        /// <summary>
        /// client→server の one-shot error / exception report (初回検出のみ)。
        /// server は connect metadata から identity を付与して disk に保存し、rebroadcast はしない。
        /// wire: [eventType:1][severity:1][lenPrefixed PermissionCompression blob of (system, message, stack)]
        /// </summary>
        public const byte EventType_ErrorReport = 7;

        // ── quality 別 avatar channel (ushort playerID、ID が 255 超の場合) ──
        // byte-ID channel と同じ layout: base + quality * 2 + hasAdditional
        //   41 = VeryLow              42 = VeryLow + Additional
        //   43 = Low                  44 = Low + Additional
        //   45 = Medium              46 = Medium + Additional
        //   47 = High                48 = High + Additional
        public const byte PlayerAvatarVeryLowLargeChannel = 41;
        public const byte PlayerAvatarVeryLowAdditionalLargeChannel = 42;
        public const byte PlayerAvatarLowLargeChannel = 43;
        public const byte PlayerAvatarLowAdditionalLargeChannel = 44;
        public const byte PlayerAvatarMediumLargeChannel = 45;
        public const byte PlayerAvatarMediumAdditionalLargeChannel = 46;
        public const byte PlayerAvatarHighLargeChannel = 47;
        public const byte PlayerAvatarHighAdditionalLargeChannel = 48;

        // ── compressed avatar bundle (server → client only) ──────────────────
        /// <summary>
        /// 複数の avatar quality message を単一 receiver へ運ぶ server-only outbound channel。
        /// peer MTU に収まる 1 つの UDP datagram へ LZ4-compressed される。
        /// wire format:
        ///   [count:1][rawLen:2-LE][LZ4 block( [origChannel:1][msgLen:2-LE][bytes]* )]
        /// 各 inner [origChannel] は、その message が個別送信される場合に使う
        /// byte-id または ushort-id avatar quality channel (channels 6-13 / 41-48)。
        /// compression: LZ4Codec.Encode、LZ4Level.L00_FAST (K4os.Compression.LZ4 1.3.x)。
        /// </summary>
        public const byte CompressedAvatarBundleChannel = 52;

        // ── server-provided default library ──────────────────────────────────
        /// <summary>
        /// server は connect 時に default library entry (avatars / props / worlds) の list を
        /// 各 client へ push する。item はその server に接続中だけ client library に存在し、
        /// disconnect 時に clear される。
        /// </summary>
        public const byte ServerLibraryChannel = 53;

        // ── peer-to-peer direct connection ───────────────────────────────────
        // payload の first byte が P2PSub_* sub-type を選択する。残り byte は
        // BasisP2PSignalMessage body。reliable-ordered。
        public const byte P2PChannel = 54;

        public const byte P2PSub_Request = 0;
        public const byte P2PSub_Accept = 1;
        public const byte P2PSub_Decline = 2;
        public const byte P2PSub_Cancel = 3;
        public const byte P2PSub_LinkLost = 4;
        public const byte P2PSub_ServerArmed = 5;
        public const byte P2PSub_LinkUp = 6;

        // ── direct-connect custom data (P2P-first, server fallback) ──────────
        /// <summary>P2P world / prop direct custom data。frame: [messageIndex:2][payload]。</summary>
        public const byte DirectSceneChannel = 56;
        /// <summary>direct-origin scene message の server relay。direct link がない recipient 向け。</summary>
        public const byte DirectSceneServerChannel = 57;
        /// <summary>P2P avatar direct custom data。frame: [messageIndex:1][avatarLinkIndex:1][payload]。</summary>
        public const byte DirectAvatarChannel = 58;
        /// <summary>direct-origin avatar message の server relay。direct link がない recipient 向け。</summary>
        public const byte DirectAvatarServerChannel = 59;

        // ── dynamic message registry (subscribe & supply) ────────────────────
        // channel 60-63 は connect 時に negotiated される dynamic message layer を運ぶ。
        // 60 は registry を negotiate する。61-63 は ushort message id を key にした plugin payload を multiplex し、
        // delivery semantic ごとに 1 channel を使う。core channel 0-59 は専用 channel と ordering stream を維持する。
        /// <summary>registry handshake。first payload byte は RegistrySub_* sub-type。</summary>
        public const byte RegistryControlChannel = 60;
        /// <summary>reliable-ordered plugin payload。frame: [messageId:2][payload]。</summary>
        public const byte PluginReliableChannel = 61;
        /// <summary>sequenced plugin payload。frame: [messageId:2][payload]。</summary>
        public const byte PluginSequencedChannel = 62;
        /// <summary>unreliable plugin payload。frame: [messageId:2][payload]。</summary>
        public const byte PluginUnreliableChannel = 63;

        /// <summary>RegistryControlChannel sub-type: server から client への full descriptor manifest。</summary>
        public const byte RegistrySub_Supply = 0;
        /// <summary>RegistryControlChannel sub-type: client から server への、処理可能な message id list。</summary>
        public const byte RegistrySub_Subscribe = 1;

        /// <summary>plugin DeliveryMethod を multiplexed channel (61-63) へ mapping する。未 mapping value では RegistryControlChannel を返す。</summary>
        public static byte GetPluginChannelForDelivery(DeliveryMethod delivery)
        {
            switch (delivery)
            {
                case DeliveryMethod.ReliableOrdered:
                case DeliveryMethod.ReliableUnordered:
                case DeliveryMethod.ReliableSequenced:
                    return PluginReliableChannel;
                case DeliveryMethod.Sequenced:
                    return PluginSequencedChannel;
                case DeliveryMethod.Unreliable:
                    return PluginUnreliableChannel;
                default:
                    return RegistryControlChannel;
            }
        }

        /// <summary>multiplexed plugin channel 用の canonical DeliveryMethod。GetPluginChannelForDelivery の reverse。</summary>
        public static DeliveryMethod GetDeliveryForPluginChannel(byte channel)
        {
            switch (channel)
            {
                case PluginSequencedChannel:
                    return DeliveryMethod.Sequenced;
                case PluginUnreliableChannel:
                    return DeliveryMethod.Unreliable;
                default:
                    return DeliveryMethod.ReliableOrdered;
            }
        }

        /// <summary>channel が [messageId:2] prefix を持つ multiplexed plugin channel (61-63) のいずれかなら true。</summary>
        public static bool IsPluginChannel(byte channel)
        {
            return channel >= PluginReliableChannel && channel <= PluginUnreliableChannel;
        }

        // ── server info unconnected query ────────────────────────────────────
        // out-of-band UDP probe。client は auth なしで server port へ問い合わせ、
        // name / online / max / MOTD payload を受け取れる。Minecraft server-list-ping と同じ形。
        // LiteNetLib の SendUnconnectedMessage 経由で流れるため、channel / peer pipeline には入らない。
        /// <summary>client からの unconnected info query packet 用 magic header。</summary>
        public const uint ServerInfoQueryMagic = 0xBA515101u;
        /// <summary>server からの unconnected info response packet 用 magic header。</summary>
        public const uint ServerInfoResponseMagic = 0xBA515102u;
        /// <summary>info query payload の wire-format version。layout 変更時に上げる。</summary>
        public const ushort ServerInfoProtocolVersion = 1;
        /// <summary>client / server が read / write する server name length の hard cap。</summary>
        public const int ServerInfoNameMaxLength = 64;
        /// <summary>client / server が read / write する MOTD length の hard cap。</summary>
        public const int ServerInfoMotdMaxLength = 256;
        /// <summary>
        /// server が unconnected info query として受け付ける最小 request size (bytes)。
        /// client は query を zero padding でこの size まで伸ばすため、response が request より大きくならない。
        /// これにより、UDP discovery protocol が DDoS reflector として悪用されやすくなる
        /// bandwidth-amplification factor を取り除く。worst-case response は約 340 bytes
        /// (full-length name + MOTD) なので、384 なら amp ratio を &lt; 1 に保てる。
        /// </summary>
        public const int ServerInfoMinRequestBytes = 384;

        /// <summary>
        /// quality index (0-3) + additional data presence を byte-ID channel へ mapping する。
        /// </summary>
        public static byte GetPlayerAvatarChannelForQuality(int qualityIndex, bool hasAdditionalData)
        {
            return (byte)(PlayerAvatarVeryLowChannel + qualityIndex * 2 + (hasAdditionalData ? 1 : 0));
        }

        /// <summary>
        /// quality index (0-3) + additional data presence を ushort-ID channel へ mapping する。playerID が 255 超の場合。
        /// </summary>
        public static byte GetPlayerAvatarLargeChannelForQuality(int qualityIndex, bool hasAdditionalData)
        {
            return (byte)(PlayerAvatarVeryLowLargeChannel + qualityIndex * 2 + (hasAdditionalData ? 1 : 0));
        }

        /// <summary>
        /// この channel が ushort playerID、つまり large variant を使う場合 true を返す。
        /// </summary>
        public static bool IsLargePlayerIdChannel(byte channel)
        {
            return channel == VoiceLargeChannel
                || (channel >= PlayerAvatarVeryLowLargeChannel && channel <= PlayerAvatarHighAdditionalLargeChannel);
        }

        /// <summary>
        /// reverse mapping: channel → quality index (0-3)。
        /// byte-ID / ushort-ID avatar channel の両方で動作する。
        /// </summary>
        public static byte GetQualityFromChannel(byte channel)
        {
            if (channel >= PlayerAvatarVeryLowLargeChannel)
                return (byte)((channel - PlayerAvatarVeryLowLargeChannel) / 2);
            return (byte)((channel - PlayerAvatarVeryLowChannel) / 2);
        }

        /// <summary>
        /// reverse mapping: channel → additional data の有無。
        /// odd offset channel は additional data を運び、even は運ばない。
        /// byte-ID / ushort-ID avatar channel の両方で動作する。
        /// </summary>
        public static bool ChannelHasAdditionalData(byte channel)
        {
            if (channel >= PlayerAvatarVeryLowLargeChannel)
                return ((channel - PlayerAvatarVeryLowLargeChannel) & 1) == 1;
            return ((channel - PlayerAvatarVeryLowChannel) & 1) == 1;
        }

        /// <summary>
        /// aggregate congestion check 用の quality 別 avatar channel 全 16 個 (byte-ID + ushort-ID)。
        /// </summary>
        public static readonly byte[] PlayerAvatarQualityChannels = new byte[]
        {
            PlayerAvatarVeryLowChannel,
            PlayerAvatarVeryLowAdditionalChannel,
            PlayerAvatarLowChannel,
            PlayerAvatarLowAdditionalChannel,
            PlayerAvatarMediumChannel,
            PlayerAvatarMediumAdditionalChannel,
            PlayerAvatarHighChannel,
            PlayerAvatarHighAdditionalChannel,
            PlayerAvatarVeryLowLargeChannel,
            PlayerAvatarVeryLowAdditionalLargeChannel,
            PlayerAvatarLowLargeChannel,
            PlayerAvatarLowAdditionalLargeChannel,
            PlayerAvatarMediumLargeChannel,
            PlayerAvatarMediumAdditionalLargeChannel,
            PlayerAvatarHighLargeChannel,
            PlayerAvatarHighAdditionalLargeChannel,
        };
    }
}
