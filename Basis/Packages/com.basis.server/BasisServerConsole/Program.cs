using Basis.Network;
using Basis.Network.Server;
using BasisNetworkConsole;
using BasisNetworking.InitialData;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
namespace Basis
{
    class Program
    {
        public static BasisNetworkHealthCheck Check;
#if !UNITY_2017_1_OR_NEWER
        public static BasisRestApiHandler Api;
#endif
        public static bool isRunning = true;
        private static ManualResetEventSlim shutdownEvent = new ManualResetEventSlim(false);
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string configDir = Path.Combine(baseDir, Configuration.ConfigFolderName);
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            string configFilePath = Path.Combine(configDir, "config.xml");
            // LoadFromXml は config.xml がない場合に作成するため、その前に初回起動かどうかを記録する。
            bool isFirstBoot = !File.Exists(configFilePath);
            Configuration config = Configuration.LoadFromXml(configFilePath);
            config.ProcessEnvironmentalOverrides();

            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.LogsFolderName);
            BasisServerSideLogging.Initialize(config, folderPath);

            // 新規サーバーでは、起動前に主要設定を確認し、管理者を必ず指定させる。
            if (isFirstBoot)
            {
                BasisSetupWizard.Run(config, configFilePath);
            }

            BNL.Log("Server Booting");
            Check = new BasisNetworkHealthCheck(config);
#if !UNITY_2017_1_OR_NEWER
            if (config.ApiEnabled && !string.IsNullOrEmpty(config.ApiKey))
                Api = new BasisRestApiHandler(config);
#endif

            NetworkServer.StartServer(config);
            
            // 旧 resource directory 名の移行などを処理する。
            // 数回の version bump 後には削除する想定。
            string[] legacyPaths = [
                "initalresources",    // dooly 表記
                "initialressources",  // フランス語圏っぽい綴り
                "intialresources",   // よくある別 typo
            ];
            
            string correctPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.InitialResourcesFolderName);

            foreach (string legacyName in legacyPaths)
            {
                string legacyFullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, legacyName);
                
                if (Directory.Exists(legacyFullPath) && !Directory.Exists(correctPath))
                {
                    try
                    {
                        BNL.Log($"Found legacy '{legacyName}' directory, migrating to '{Configuration.InitialResourcesFolderName}'...");
                        Directory.Move(legacyFullPath, correctPath);
                        BNL.Log("Directory migration completed successfully");
                        break; // 最初に成功した移行で抜ける。
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"Failed to migrate legacy directory '{legacyName}': {ex.Message}");
                    }
                }
            }
            BasisLoadableLoader.LoadXML(Configuration.InitialResourcesFolderName);
            BasisDefaultLibraryLoader.LoadXML(Configuration.DefaultLibraryFolderName);

            AppDomain.CurrentDomain.ProcessExit += async (sender, eventArgs) =>
            {
                BNL.Log("Shutting down server...");
                isRunning = false;
                shutdownEvent.Set(); // main thread に終了を通知する。
#if !UNITY_2017_1_OR_NEWER
                Api?.Dispose();
#endif
                BasisPersistentDatabase.Shutdown();
                BasisServerReductionSystemEvents.Shutdown();
                if (config.EnableStatistics) BasisStatistics.StopWorkerThread();
                await BasisServerSideLogging.ShutdownAsync();
                BNL.Log("Server shut down successfully.");
            };
            if (config.EnableConsole)
            {
                BasisConsoleCommands.RegisterCommand("/players", "Lists all connected players.", BasisConsoleCommands.HandleShowPlayers);
                BasisConsoleCommands.RegisterCommand("/status", "Shows the current server status.", BasisConsoleCommands.HandleStatus);
                BasisConsoleCommands.RegisterCommand("/shutdown", "Shuts down the server.", BasisConsoleCommands.HandleShutdown);
                BasisConsoleCommands.RegisterCommand("/help", "Displays all available commands.", BasisConsoleCommands.HandleHelp);
                BasisConsoleCommands.RegisterCommand("/clear", "Clears the console", BasisConsoleCommands.HandleClear);
                BasisConsoleCommands.RegisterPermissionCommands();
                BasisConsoleCommands.RegisterConfigurationCommands(config);
                BasisConsoleCommands.StartConsoleListener();
            }
            // shutdown signal を待つ。
            shutdownEvent.Wait();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            BNL.LogError($"Unhandled Exception: {e.ExceptionObject}");
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            BNL.LogError($"Unobserved Task Exception: {e.Exception.Message}");
            e.SetObserved();
        }
    }
}
