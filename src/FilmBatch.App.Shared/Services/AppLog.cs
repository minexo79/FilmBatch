using FilmBatch.Core;

namespace FilmBatch.App.Services;

/// <summary>
/// 簡易檔案記錄（logs/filmbatch-yyyyMMdd.log）。INFO／WARN／ERROR 一律寫入；
/// DEBUG 只在 <see cref="DebugEnabled"/>（偏好設定的除錯模式）開啟時寫入。M1 起改用 Microsoft.Extensions.Logging。
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static volatile bool _debugEnabled;

    /// <summary>是否寫入 DEBUG 等級的詳細記錄（載入、算圖耗時等）；由偏好設定的除錯模式控制，即時生效。</summary>
    public static bool DebugEnabled
    {
        get => _debugEnabled;
        set => _debugEnabled = value;
    }

    /// <summary>目前的記錄檔路徑（依日期分檔）。</summary>
    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"filmbatch-{DateTime.Now:yyyyMMdd}.log");

    public static void Debug(string message)
    {
        if (_debugEnabled) Write("DEBUG", message);
    }

    /// <summary>延遲組字串的版本：除錯模式關閉時不建立訊息（用於高頻呼叫點，例如每次算圖）。</summary>
    public static void Debug(Func<string> message)
    {
        if (_debugEnabled) Write("DEBUG", message());
    }

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
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // 記錄失敗不影響操作
        }
    }
}
