using Avalonia;
using Avalonia.Media;
using FilmBatch.App.Services;

namespace FilmBatch.App;

/// <summary>命令列參數：--gallery、--smoke [輸出檔]、--software、--open &lt;資料夾&gt;。</summary>
public sealed record StartupOptions(bool Gallery, bool Smoke, string? SmokeOutput, bool ForceSoftware, string? OpenFolder)
{
    public static StartupOptions Current { get; private set; } = Parse([]);

    public static StartupOptions Parse(string[] args)
    {
        string? Next(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        }
        return Current = new StartupOptions(
            Gallery: args.Contains("--gallery"),
            Smoke: args.Contains("--smoke"),
            SmokeOutput: Next("--smoke"),
            ForceSoftware: args.Contains("--software"),
            OpenFolder: Next("--open"));
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupOptions.Parse(args);
        AppLog.Info($"FilmBatch 啟動：{string.Join(' ', args)}");
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("啟動失敗", ex);
            throw;
        }
    }

    // Avalonia 設計工具也會呼叫此方法
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(FontOptions())
            .LogToTrace();

    /// <summary>預設字型與後備字型（計畫書 2.2.3）。</summary>
    internal static FontManagerOptions FontOptions()
    {
        var (primary, fallbacks) = OperatingSystem.IsWindows() ? ("Microsoft JhengHei UI", new[] { "Microsoft JhengHei", "Segoe UI" })
            : OperatingSystem.IsMacOS() ? ("PingFang TC", new[] { "Heiti TC", "Helvetica Neue" })
            : ("Noto Sans CJK TC", new[] { "Noto Sans TC", "Noto Sans" });
        return new FontManagerOptions
        {
            DefaultFamilyName = primary,
            FontFallbacks = fallbacks.Select(f => new FontFallback { FontFamily = new FontFamily(f) }).ToArray(),
        };
    }
}
