using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network
{
    public static class BasisServerSideLogging
    {
        private static string LogDirectory;
        private static string CurrentLogFileName => Path.Combine(LogDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.log");

        private static CancellationTokenSource _cancellationTokenSource;
        private static Task _loggingTask;
        private static readonly BlockingCollection<string> LogQueue = new(new ConcurrentQueue<string>(), 200);
        private static readonly SemaphoreSlim FileWriteSemaphore = new(1, 1);

        static BasisServerSideLogging()
        {
        }
        public static bool UseLogging;
        public static bool WriteToScreen = true;
        /// <summary>
        /// Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
        /// </summary>
        /// <param name="config"></param>
        /// <param name="PathOutput"></param>
        public static void Initialize(Configuration config, string logDirectory)
        {
            UseLogging = config.HasFileSupport;
            LogDirectory = logDirectory;
            BNL.LogOutput += Log;
            BNL.LogWarningOutput += LogWarning;
            BNL.LogErrorOutput += LogError;

            if (UseLogging)
            {
                // logs directory が存在するようにする
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }
                Log("Logs are saved to " + CurrentLogFileName);
                StartLoggingTask();
            }
            else
            {
                Log("no logs will be saved");
            }
        }
        private static void StartLoggingTask()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            _loggingTask = Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested || !LogQueue.IsCompleted)
                    {
                        if (LogQueue.TryTake(out var logEntry, 50))
                        {
                            await WriteToFileAsync(logEntry, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // task が cancel されたので正常に抜ける
                }
            }, cancellationToken);
        }

        private static async Task WriteToFileAsync(string logEntry, CancellationToken cancellationToken)
        {
            try
            {
                await FileWriteSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                using (var stream = new FileStream(CurrentLogFileName, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true))
                {
                    var logData = Encoding.UTF8.GetBytes(logEntry + Environment.NewLine);
                    await stream.WriteAsync(logData, 0, logData.Length, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                FileWriteSemaphore.Release();
            }
        }

        public static async Task ShutdownAsync()
        {
            _cancellationTokenSource?.Cancel();
            LogQueue?.CompleteAdding();

            try
            {
                await _loggingTask.ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // cancellation 由来の exception を抑制する
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
            }
        }
        private static string FormatMessage(string level, string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm");
            return $"[{timestamp}] [{level}] {message}";
        }

        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            StringBuilder sb = new StringBuilder(message.Length);
            foreach (char c in message)
            {
                if (c == '\n' || c == '\r') sb.Append(' ');
                else if (c < 0x20 && c != '\t') sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void Log(string message)
        {
            if (WriteToScreen || UseLogging)
            {
                message = Sanitize(message);
                string formattedMessage = FormatMessage("INFO", message);
                WriteColoredMessage($"[{DateTime.Now:HH:mm}] ", ConsoleColor.DarkCyan);
                WriteColoredMessage("[INFO] ", ConsoleColor.DarkMagenta);
                WriteColoredMessage($"{message}\n", ConsoleColor.Gray);

                if (UseLogging)
                {
                    if (!LogQueue.TryAdd(formattedMessage))
                    {
                        LogQueue.TryTake(out _); // queue が満杯なら最古の log を捨てる
                        LogQueue.TryAdd(formattedMessage); // 新しい message の追加を再試行する
                    }
                }
            }
        }
        public static void LogWarning(string message)
        {
            if (WriteToScreen || UseLogging)
            {
                message = Sanitize(message);
                string formattedMessage = FormatMessage("WARNING", message);
                WriteColoredMessage($"[{DateTime.Now:HH:mm}] ", ConsoleColor.DarkCyan); // timestamp は白
                WriteColoredMessage("[WARNING] ", ConsoleColor.DarkYellow); // level は黄色
                WriteColoredMessage($"{message}\n", ConsoleColor.Gray); // message は灰色

                if (UseLogging)
                {
                    if (!LogQueue.TryAdd(formattedMessage))
                    {
                        LogQueue.TryTake(out _); // queue が満杯なら最古の log を捨てる
                        LogQueue.TryAdd(formattedMessage); // 新しい message の追加を再試行する
                    }
                }
            }
        }

        public static void LogError(string message)
        {
            if (WriteToScreen || UseLogging)
            {
                message = Sanitize(message);
                string formattedMessage = FormatMessage("ERROR", message);
                WriteColoredMessage($"[{DateTime.Now:HH:mm}] ", ConsoleColor.DarkCyan); // timestamp は白
                WriteColoredMessage("[ERROR] ", ConsoleColor.DarkRed); // level は赤
                WriteColoredMessage($"{message}\n", ConsoleColor.Gray); // message は灰色


                if (UseLogging)
                {
                    if (!LogQueue.TryAdd(formattedMessage))
                    {
                        LogQueue.TryTake(out _); // queue が満杯なら最古の log を捨てる
                        LogQueue.TryAdd(formattedMessage); // 新しい message の追加を再試行する
                    }
                }
            }
        }

        private static void WriteColoredMessage(string message, ConsoleColor color)
        {
            var originalColor = Console.ForegroundColor; // 元の色を保存する
            Console.ForegroundColor = color; // 目的の色を設定する
            Console.Write(message); // message を書き込む (改行なし)
            Console.ForegroundColor = originalColor; // 元の色へ戻す
        }
    }
}
