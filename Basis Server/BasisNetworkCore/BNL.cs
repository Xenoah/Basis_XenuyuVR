using System;

/// <summary>
/// console color 対応の Basis network logger。
/// </summary>
public static class BNL
{
    public static Action<string> LogOutput;
    public static Action<string> LogWarningOutput;
    public static Action<string> LogErrorOutput;
    public static void Log(string message)
    {
        string formattedMessage = message;

        if (LogOutput != null)
        {
            LogOutput.Invoke(formattedMessage);
        }
        else
        {
            WriteWithColor(formattedMessage, ConsoleColor.White); // info は白
        }
    }

    public static void LogWarning(string message)
    {
        if (LogWarningOutput != null)
        {
            LogWarningOutput.Invoke(message);
        }
        else
        {
            WriteWithColor(message, ConsoleColor.Yellow); // warning は黄色
        }
    }

    public static void LogError(string message)
    {
        if (LogErrorOutput != null)
        {
            LogErrorOutput.Invoke(message);
        }
        else
        {
            WriteWithColor(message, ConsoleColor.Red); // error は赤
        }
    }
    private static void WriteWithColor(string message, ConsoleColor color)
    {
        ConsoleColor originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }
    public static void ClearConsole()
    {
        Console.Clear();
    }
}
