using BasisPermissions;
using static BasisPermissions.PermissionManager;

namespace BasisNetworkConsole
{
    /// <summary>
    /// 初回起動セットアップ wizard。起動時に config.xml が存在しない新規サーバーで、
    /// network server の開始前に一度だけ実行される。主要な server settings を operator に確認させ、
    /// 少なくとも 1 人の admin を指定させることで、新規サーバーが誤設定のまま、または
    /// moderating できる人がいない状態で残ることを防ぐ。
    /// admin は permissions.xml の "admin" group (full access) の member として保存される。
    /// これは runtime の "/perm user group add &lt;uuid&gt; admin" command と同じで、
    /// その他の settings は config.xml へ直接書き戻される。
    /// </summary>
    public static class BasisSetupWizard
    {
        /// <summary>
        /// interactive console が接続されていない headless / automated first boot
        /// (Docker、CI) で admin を seed するための environment variable。
        /// 1 つの UUID、または comma / space 区切りの list を受け付ける。
        /// </summary>
        public const string AdminEnvVar = "BasisFirstAdmin";
        private const string AdminGroup = "admin";
        private const string DefaultPassword = "default_password";

        public static void Run(Configuration config, string configFilePath)
        {
            string configDir = Path.GetDirectoryName(configFilePath) ?? string.Empty;
            string permissionsPath = Path.Combine(configDir, "permissions.xml");

            // shared permission store を "admin" group が存在する状態まで初期化し、
            // そのまま permissions.xml へ書き込む。直後に server が boot すると、
            // PermissionIntegration.Init が同じ file を再読み込みするため、追加した admin が保持される。
            PermissionManager pm = PermissionIntegration.Manager;
            pm.SetXmlPath(permissionsPath);
            pm.LoadFromXml();
            pm.EnsureDefaults();

            // Headless / automated deploy では、prompt ではなく env var から admin を seed する。
            // これらの deployment では server settings は env var / mounted config.xml から来るため、
            // interactive settings walkthrough はここでは実行しない。
            string? fromEnv = Environment.GetEnvironmentVariable(AdminEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                int seeded = SeedFromList(pm, fromEnv);
                if (seeded > 0)
                {
                    pm.SaveToXml();
                    BNL.Log($"[Setup] First boot: added {seeded} admin(s) from ${AdminEnvVar}.");
                    return;
                }
                BNL.LogWarning($"[Setup] ${AdminEnvVar} was set but contained no usable UUIDs.");
            }

            if (!CanPrompt())
            {
                WarnNoAdmin();
                return;
            }

            RunInteractive(pm, config, configFilePath);
        }

        private static void RunInteractive(PermissionManager pm, Configuration config, string configFilePath)
        {
            PrintIntro();
            RunSettingsWalkthrough(config, configFilePath);
            int admins = PromptAdmins(pm);
            BNL.Log($"[Setup] First-time setup complete - {admins} admin(s) configured. Starting server...");
        }

        // ----------------------------------------------------------------------------
        // core server settings
        // ----------------------------------------------------------------------------

        private static void RunSettingsWalkthrough(Configuration config, string configFilePath)
        {
            BNL.Log("");
            BNL.Log("--- Server settings ---");
            BNL.Log("Press Enter to keep the [current] value, or type a new one.");
            BNL.Log("");

            string name = PromptString("Server name (shown in the server list)", config.ServerName);
            string motd = PromptString("Message of the day / MOTD", config.ServerMotd);
            ushort port = PromptUShort("Game port (UDP)", config.SetPort);
            string password = PromptPassword(config.Password);
            int peerLimit = PromptInt("Max players", config.PeerLimit, 1);

            BNL.Log("");
            BNL.Log("--- Review ---");
            BNL.Log($"  Server name : {name}");
            BNL.Log($"  MOTD        : {(motd.Length == 0 ? "<none>" : motd)}");
            BNL.Log($"  Game port   : {port}");
            BNL.Log($"  Password    : {(password == DefaultPassword ? DefaultPassword : "(set)")}");
            BNL.Log($"  Max players : {peerLimit}");
            BNL.Log("");

            if (!ConfirmDefaultYes("Save these settings and start the server?"))
            {
                BNL.Log("[Setup] Settings discarded - keeping config.xml defaults.");
                return;
            }

            config.ServerName = name;
            config.ServerMotd = motd;
            config.SetPort = port;
            config.Password = password;
            config.PeerLimit = peerLimit;
            config.SaveToXml(configFilePath);
            BNL.Log("[Setup] Settings saved to config.xml.");
        }

        // ----------------------------------------------------------------------------
        // admin (必須)
        // ----------------------------------------------------------------------------

        private static int PromptAdmins(PermissionManager pm)
        {
            BNL.Log("");
            BNL.Log("--- Admin setup (required) ---");
            PrintAdminHelp();

            int adminCount = 0;
            while (true)
            {
                Console.Write(adminCount == 0
                    ? "Admin player UUID: "
                    : "Add another admin UUID (leave blank to finish): ");
                string input = (Console.ReadLine() ?? string.Empty).Trim();

                if (input.Length == 0)
                {
                    if (adminCount == 0)
                    {
                        BNL.LogWarning("At least one admin is required before the server can start. Type 'help' if you're stuck.");
                        continue;
                    }
                    break;
                }

                if (input.Equals("help", StringComparison.OrdinalIgnoreCase) || input == "?")
                {
                    PrintAdminHelp();
                    continue;
                }

                if (!LooksLikeDid(input) && !Confirm($"'{input}' doesn't look like a did:key UUID. Add it anyway?"))
                {
                    continue;
                }

                pm.AddUserToGroup(input, AdminGroup);
                pm.SaveToXml();
                adminCount++;
                BNL.Log($"[Setup] Added admin: {input}");
            }

            return adminCount;
        }

        /// <summary>区切り付き list 内の全 UUID を admin group に追加し、追加数を返す。</summary>
        private static int SeedFromList(PermissionManager pm, string raw)
        {
            int added = 0;
            foreach (string part in raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string uuid = part.Trim();
                if (uuid.Length == 0) continue;
                pm.AddUserToGroup(uuid, AdminGroup);
                BNL.Log($"[Setup] Added admin: {uuid}");
                added++;
            }
            return added;
        }

        // ----------------------------------------------------------------------------
        // prompt helper
        // ----------------------------------------------------------------------------

        private static string PromptString(string label, string current)
        {
            string shown = current.Length == 0 ? "<none>" : current;
            Console.Write($"{label} [{shown}]: ");
            string input = (Console.ReadLine() ?? string.Empty).Trim();
            return input.Length == 0 ? current : input;
        }

        private static ushort PromptUShort(string label, ushort current)
        {
            while (true)
            {
                Console.Write($"{label} [{current}]: ");
                string input = (Console.ReadLine() ?? string.Empty).Trim();
                if (input.Length == 0) return current;
                if (ushort.TryParse(input, out ushort value)) return value;
                BNL.LogWarning($"Please enter a whole number between 0 and {ushort.MaxValue}.");
            }
        }

        private static int PromptInt(string label, int current, int min)
        {
            while (true)
            {
                Console.Write($"{label} [{current}]: ");
                string input = (Console.ReadLine() ?? string.Empty).Trim();
                if (input.Length == 0) return current;
                if (int.TryParse(input, out int value) && value >= min) return value;
                BNL.LogWarning($"Please enter a whole number of {min} or more.");
            }
        }

        private static string PromptPassword(string current)
        {
            Console.Write("Server password [keep current]: ");
            string input = (Console.ReadLine() ?? string.Empty).Trim();
            return input.Length == 0 ? current : input;
        }

        // ----------------------------------------------------------------------------
        // text / confirmation
        // ----------------------------------------------------------------------------

        private static void PrintIntro()
        {
            BNL.Log("============================================================");
            BNL.Log(" Basis Server - First-Time Setup");
            BNL.Log("============================================================");
            BNL.Log("No config.xml was found, so this is a brand-new server.");
            BNL.Log("This quick wizard sets up the core server settings and has");
            BNL.Log("you designate at least one admin before the server starts.");
        }

        private static void PrintAdminHelp()
        {
            BNL.Log("An admin is identified by their Basis player UUID (a DID), which");
            BNL.Log("looks like:  did:key:z6Mk...");
            BNL.Log("Where to find it:");
            BNL.Log("  - in the Basis client: open Settings > Developer tab, find the");
            BNL.Log("    \"Identity Key\" section, and tap the eye icon on the \"UUID\"");
            BNL.Log("    field to reveal it; or");
            BNL.Log("  - in this server's log: every time a player connects it prints");
            BNL.Log("    their UUID as \"(UUID did:key:...)\".");
            BNL.Log("The UUID you enter is added to the \"admin\" group (full access).");
            BNL.Log("You can add more than one; leave the prompt blank once done.");
            BNL.Log("");
        }

        private static void WarnNoAdmin()
        {
            BNL.LogWarning("============================================================");
            BNL.LogWarning(" Basis Server first-time setup: NO ADMIN CONFIGURED");
            BNL.LogWarning("------------------------------------------------------------");
            BNL.LogWarning(" No interactive console is attached and the environment");
            BNL.LogWarning($" variable {AdminEnvVar} is not set, so the server is starting");
            BNL.LogWarning(" WITHOUT an admin - nobody can moderate or configure it.");
            BNL.LogWarning(" To set one up, provide the admin's player UUID, e.g.:");
            BNL.LogWarning($"   {AdminEnvVar}=did:key:z6Mk...   (comma-separate for several)");
            BNL.LogWarning(" then delete config/config.xml and restart, or run the server");
            BNL.LogWarning(" once in an interactive terminal to use the setup wizard.");
            BNL.LogWarning("============================================================");
        }

        private static bool LooksLikeDid(string value)
        {
            return value.StartsWith("did:", StringComparison.OrdinalIgnoreCase)
                && value.Length > "did:".Length
                && !value.Contains(' ');
        }

        /// <summary>空行では NO を default とする yes/no prompt。</summary>
        private static bool Confirm(string question)
        {
            Console.Write($"{question} (y/N): ");
            string answer = (Console.ReadLine() ?? string.Empty).Trim();
            return answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>空行では YES を default とする yes/no prompt。</summary>
        private static bool ConfirmDefaultYes(string question)
        {
            Console.Write($"{question} (Y/n): ");
            string answer = (Console.ReadLine() ?? string.Empty).Trim();
            return answer.Length == 0
                || answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>実際の interactive terminal が接続されている場合のみ true。prompt で daemon を hang させないため。</summary>
        private static bool CanPrompt()
        {
            try
            {
                return !Console.IsInputRedirected;
            }
            catch
            {
                return false;
            }
        }
    }
}
