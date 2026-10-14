using FilmBatch.Core;

namespace FilmBatch.App.Services;

/// <summary>簡易檔案記錄（logs/filmbatch-yyyyMMdd.log）。M1 起改用 Microsoft.Extensions.Logging。</summary>
public static class AppLog
{
    private static readonly object Lock = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"filmbatch-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // 記錄失敗不影響操作
        }
    }
}
