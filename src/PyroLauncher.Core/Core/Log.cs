using System.IO;

namespace PyroLauncher.Core;

public static class Log
{
    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(AppPaths.Logs, "launcher.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash */ }
    }
}
