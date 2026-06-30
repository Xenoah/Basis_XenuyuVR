using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static SerializableBasis;

namespace BasisNetworkServer.BasisNetworking
{
    /// <summary>
    /// chat message 用の server-side handler。
    /// 受信した chat を deserialize し、word filter を適用してから、認証済みの他 peer 全員へ broadcast する。
    /// </summary>
    public static class BasisNetworkChat
    {
        private static readonly HashSet<string> BlockedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string[] _blockedWordsArray = Array.Empty<string>();
        private static readonly string WordFilterFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "chat_word_filter.txt");

        /// <summary>
        /// word filter list を disk から読み込む。file の各行は blocked word / phrase として扱う。
        /// 存在しない場合は空の file を作成する。
        /// </summary>
        public static void LoadWordFilter(Configuration Configuration)
        {
            if (Configuration.HasFileSupport)
            {
                try
                {
                    if (!File.Exists(WordFilterFilePath))
                    {
                        // 説明付きの空 filter file を作成する。
                        string configDir = Path.GetDirectoryName(WordFilterFilePath);
                        if (!Directory.Exists(configDir))
                        {
                            Directory.CreateDirectory(configDir);
                        }
                        File.WriteAllText(WordFilterFilePath,
                            "# Chat word filter - one word or phrase per line\n" +
                            "# Lines starting with # are comments\n" +
                            "# Words are case-insensitive\n" +
                            "fuck\n" +
                            "fucking\n" +
                            "fucker\n" +
                            "fucked\n" +
                            "motherfucker\n" +
                            "shit\n" +
                            "shitting\n" +
                            "bullshit\n" +
                            "bitch\n" +
                            "bitches\n" +
                            "ass\n" +
                            "asshole\n" +
                            "bastard\n" +
                            "damn\n" +
                            "damned\n" +
                            "cunt\n" +
                            "dick\n" +
                            "dickhead\n" +
                            "cock\n" +
                            "cocksucker\n" +
                            "pussy\n" +
                            "whore\n" +
                            "slut\n" +
                            "piss\n" +
                            "pissed\n" +
                            "crap\n" +
                            "wanker\n" +
                            "twat\n" +
                            "prick\n" +
                            "douche\n" +
                            "douchebag\n" +
                            "# Slurs\n" +
                            "nigger\n" +
                            "nigga\n" +
                            "faggot\n" +
                            "fag\n" +
                            "retard\n" +
                            "retarded\n" +
                            "tranny\n" +
                            "kike\n" +
                            "spic\n" +
                            "chink\n" +
                            "gook\n" +
                            "wetback\n" +
                            "beaner\n" +
                            "coon\n" +
                            "dyke\n" +
                            "# Threats / harassment\n" +
                            "kill yourself\n" +
                            "kys\n" +
                            "neck yourself\n" +
                            "go die\n" +
                            "rape\n" +
                            "raping\n" +
                            "rapist\n");
                        BNL.Log("Created default chat word filter file: " + WordFilterFilePath);
                        // default をすぐ有効にするため、読み込み直す。
                        LoadWordFilter(Configuration);
                        return;
                    }

                    BlockedWords.Clear();
                    string[] lines = File.ReadAllLines(WordFilterFilePath);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                        {
                            BlockedWords.Add(trimmed);
                        }
                    }
                    _blockedWordsArray = new string[BlockedWords.Count];
                    BlockedWords.CopyTo(_blockedWordsArray);
                    BNL.Log($"Loaded {BlockedWords.Count} words into chat filter (using homoglyph + trigram detection)");
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Failed to load chat word filter: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// message に word filter を適用し、blocked word を asterisk に置き換える。
        /// homoglyph detection (Unicode の見た目が似た文字) と trigram-based な false positive 防止を使い、
        /// 誤検出 (例: "assignment" 内の "ass" は一致させない) を避けながら回避を検出する。
        /// </summary>
        public static string FilterMessage(string message)
        {
            if (_blockedWordsArray.Length == 0 || string.IsNullOrEmpty(message))
            {
                return message;
            }

            return BasisWordFilter.Filter(message, _blockedWordsArray);
        }

        /// <summary>
        /// client peer から届いた chat message を処理する。
        /// deserialize、filter、再 serialize して、他の peer 全員へ broadcast する。
        /// </summary>
        public static void HandleChatMessage(NetPacketReader reader, NetPeer sender)
        {
            ChatMessage chatMessage = new ChatMessage();
            chatMessage.Deserialize(reader);
            reader.Recycle();

            // decode、filter、再 encode する。
            if (chatMessage.payload != null && chatMessage.payloadSize > 0)
            {
                string text = Encoding.UTF8.GetString(chatMessage.payload, 0, chatMessage.payloadSize);

                // word filter を適用する。
                text = FilterMessage(text);
                text = BasisChatSanitizer.Sanitize(text);

                byte[] filtered = Encoding.UTF8.GetBytes(text);
                chatMessage.payload = filtered;
                chatMessage.payloadSize = (ushort)filtered.Length;
            }

            // sender ID で包む。
            ServerChatMessage serverChatMessage = new ServerChatMessage
            {
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)sender.Id
                },
                chatMessage = chatMessage
            };

            // serialize し、sender 以外の全員へ broadcast する。
            NetDataWriter writer = NetworkServer.RentWriter();
            serverChatMessage.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.ChatChannel, sender, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
