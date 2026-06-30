using System.Xml.Linq;

namespace Basis.Config
{
    public static class ConfigManager
    {
        public static string Password = "default_password";
        public static string Ip = "localhost";
        public static int Port = 4296;
        public static int ClientCount = 250;

        public static string AvatarPassword = "default_avatar_password";
        public static string AvatarUrl = "http://localhost/avatar";
        public static int AvatarLoadMode = 1;

        private static readonly object _lock = new();
        static XElement? Child(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

        static string ReadString(XElement root, string name, string fallback)
        {
            var el = Child(root, name);
            if (el == null)
            {
                BNL.Log($"Missing <{name}>, using fallback.");
                return fallback;
            }

            var value = el.Value.Trim();
            BNL.Log($"Loaded {name}: [{value}]");
            return value;
        }

        static int ReadInt(XElement root, string name, int fallback)
        {
            var el = Child(root, name);
            if (el == null)
            {
                BNL.Log($"Missing <{name}>, using fallback {fallback}.");
                return fallback;
            }

            if (!int.TryParse(el.Value, out var value))
            {
                BNL.Log($"Invalid <{name}> value '{el.Value}', using fallback {fallback}.");
                return fallback;
            }

            BNL.Log($"Loaded {name}: {value}");
            return value;
        }

        // ---------------- main entry ----------------

        public static void LoadOrCreateConfigXml(string filePath)
        {
            lock (_lock)
            {
                filePath = Path.GetFullPath(filePath);
                BNL.Log($"Config path: {filePath}");

                if (!File.Exists(filePath))
                {
                    BNL.Log("Config file not found. Creating default.");

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                        var doc = new XDocument(
                            new XComment(" BasisNetworkClientConsole load-tester configuration。stress testing のため、server に接続する fake client を ClientCount 件 spawn する。 "),
                            new XElement("Configuration",
                                new XComment(" server connection password。server 側の <Password> と一致している必要がある。string。 "),
                                new XElement("Password", Password),
                                new XComment(" 接続先 server host。hostname または IP (例: localhost / 127.0.0.1)。string。 "),
                                new XElement("Ip", Ip),
                                new XComment(" server UDP port。server 側の <SetPort> と一致している必要がある。int、範囲は 1-65535。 "),
                                new XElement("Port", Port),
                                new XComment(" load testing 用に spawn する simulated client 数。int (>= 1)。大きい値ほど CPU、memory、socket を多く使う。 "),
                                new XElement("ClientCount", ClientCount),
                                new XComment(" avatar と一緒に送る unlock password/key。<AvatarUrl> の (encrypted .BEE) bundle を decrypt するために使う。string。 "),
                                new XElement("AvatarPassword", AvatarPassword),
                                new XComment(" 各 fake client が advertise する avatar source。AvatarLoadMode 0 では (encrypted .BEE) bundle の download URL。string。 "),
                                new XElement("AvatarUrl", AvatarUrl),
                                new XComment(" 受信側 client が avatar を load する方法。0 = AssetBundle (AvatarUrl から download)、1 = Addressables、2 = In-scene。許可値は 0、1、2。 "),
                                new XElement("AvatarLoadMode", AvatarLoadMode)
                            )
                        );

                        // atomic write。
                        var temp = filePath + ".tmp";
                        doc.Save(temp);
                        File.Move(temp, filePath);

                        BNL.Log("Default config created successfully.");
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError("Failed to create config file." + ex.Message);
                    }

                    return;
                }

                XDocument docLoaded;
                try
                {
                    docLoaded = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
                }
                catch (Exception ex)
                {
                    BNL.LogError("Failed to load config XML (corrupt or in use)." + ex.Message);
                    return;
                }

                var root = docLoaded.Root;
                if (root == null)
                {
                    BNL.Log("Config XML has no root element.");
                    return;
                }

                BNL.Log($"Root element: {root.Name} | Namespace: '{root.Name.NamespaceName}'");

                try
                {
                    Password = ReadString(root, "Password", Password);
                    Ip = ReadString(root, "Ip", Ip);
                    Port = ReadInt(root, "Port", Port);
                    ClientCount = ReadInt(root, "ClientCount", ClientCount);

                    AvatarPassword = ReadString(root, "AvatarPassword", AvatarPassword);
                    AvatarUrl = ReadString(root, "AvatarUrl", AvatarUrl);
                    AvatarLoadMode = ReadInt(root, "AvatarLoadMode", AvatarLoadMode);
                }
                catch (Exception ex)
                {
                    BNL.LogError("Unexpected error while parsing config." + ex.Message);
                }
            }
        }
    }
}
