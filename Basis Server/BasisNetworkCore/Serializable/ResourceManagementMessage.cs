using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// client から server へ呼び出す resource load request。
    /// </summary>
    public struct LocalLoadResource
    {
        /// <summary>
        /// 0 = Game object、1 = Scene。
        /// </summary>
        public byte Mode;
        /// <summary>
        /// この Object が network 上で紐づく unique string。
        /// </summary>
        public string LoadedNetID;
        public string UnlockPassword;
        public string CombinedURL;

        // server からこの item を削除しない。
        // off の場合、server の player count が 0 になった時に削除される。
        public string UUIDOfCreator;
        /// <summary>
        /// normal user はこれらの item を削除できない。
        /// network には書き込まず、server 側だけで扱う。
        /// </summary>
        public bool IsAdminLocked;

        public float PositionX;
        public float PositionY;
        public float PositionZ;

        public float QuaternionX;
        public float QuaternionY;
        public float QuaternionZ;
        public float QuaternionW;

        public float ScaleX;
        public float ScaleY;
        public float ScaleZ;

        public bool Persist;
        /// <summary>
        /// true の場合、この item は "static" になる。全員の pickup が無効になり、
        /// object はその場で固定される。vehicle は lock out される。
        /// server-authoritative で、ModifyResource 経由で設定される。
        /// </summary>
        public bool Static;
        /// <summary>
        /// true の場合、static lock は "admin tier" になる。item creator ではなく moderator だけが
        /// 変更または解除でき、<see cref="Static"/> も true であることを含意する。
        /// removal lock である <see cref="IsAdminLocked"/> とは別物。
        /// </summary>
        public bool StaticAdminLocked;
        /// <summary>
        /// scale を設定するべきか、推定済みの scale をそのまま使うべきかを示す。
        /// </summary>
        public bool ModifyScale;
        /// <summary>
        /// client 側で resource をどう load するかを決める。
        /// 0 = Immediate (すぐ spawn。既存挙動)。
        /// 2 = Synchronized (download 後に readiness を報告し、全員 ready または 5 分 timeout で spawn)。
        /// 3 = Predownload (全 client で download して disc に cache し、spawn はしない)。
        /// </summary>
        public byte LoadStrategy;
        public void Deserialize(NetDataReader Writer)
        {
            Mode = Writer.GetByte();
            LoadedNetID = Writer.GetString();
            UnlockPassword = Writer.GetString();
            CombinedURL = Writer.GetString();
            UUIDOfCreator = Writer.GetString();
            IsAdminLocked = Writer.GetBool();

            Persist = Writer.GetBool();
            Static = Writer.GetBool();
            StaticAdminLocked = Writer.GetBool();
            ModifyScale = Writer.GetBool();
            LoadStrategy = Writer.GetByte();
            if (Mode == 0)
            {
                PositionX = Writer.GetFloat();
                PositionY = Writer.GetFloat();
                PositionZ = Writer.GetFloat();

                QuaternionX = Writer.GetFloat();
                QuaternionY = Writer.GetFloat();
                QuaternionZ = Writer.GetFloat();
                QuaternionW = Writer.GetFloat();

                ScaleX = Writer.GetFloat();
                ScaleY = Writer.GetFloat();
                ScaleZ = Writer.GetFloat();
            }

        }
        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(Mode);
            Writer.Put(LoadedNetID);
            Writer.Put(UnlockPassword);
            Writer.Put(CombinedURL);
            Writer.Put(UUIDOfCreator);
            Writer.Put(IsAdminLocked);
            Writer.Put(Persist);
            Writer.Put(Static);
            Writer.Put(StaticAdminLocked);
            Writer.Put(ModifyScale);
            Writer.Put(LoadStrategy);
            if (Mode == 0)
            {
                Writer.Put(PositionX);
                Writer.Put(PositionY);
                Writer.Put(PositionZ);

                Writer.Put(QuaternionX);
                Writer.Put(QuaternionY);
                Writer.Put(QuaternionZ);
                Writer.Put(QuaternionW);

                Writer.Put(ScaleX);
                Writer.Put(ScaleY);
                Writer.Put(ScaleZ);
            }
        }
    }

    /// <summary>
    /// synchronized load の preload readiness を報告するため、client から server へ送られる。
    /// </summary>
    public struct PreloadReadyMessage
    {
        /// <summary>
        /// この readiness report が対象とする resource の LoadedNetID。
        /// </summary>
        public string LoadedNetID;
        /// <summary>
        /// client が content の preload に成功した場合 true。失敗または timeout の場合 false。
        /// </summary>
        public bool IsReady;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(LoadedNetID);
            writer.Put(IsReady);
        }
        public void Deserialize(NetDataReader reader)
        {
            LoadedNetID = reader.GetString();
            IsReady = reader.GetBool();
        }
    }

    /// <summary>
    /// preloaded resource を今 spawn するべきことを伝えるため、server から全 client へ送られる。
    /// </summary>
    public struct SpawnPreloadedMessage
    {
        /// <summary>
        /// spawn 対象 resource の LoadedNetID。
        /// </summary>
        public string LoadedNetID;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(LoadedNetID);
        }
        public void Deserialize(NetDataReader reader)
        {
            LoadedNetID = reader.GetString();
        }
    }
}
