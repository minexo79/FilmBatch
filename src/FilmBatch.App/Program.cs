using Avalonia;
using Avalonia.Media;
using FilmBatch.App.Services;
using FilmBatch.Core;

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
        var options = StartupOptions.Parse(args);
        // 發佈版冒煙測試絕不寫入真正的使用者資料夾（設定、預設庫、layout、log）：
        // 在 AppSettings.Load / AppLog 之前就切到一次性暫存 profile；外部已指定 OverrideRoot 時尊重原值。
        if (options.Smoke && AppPaths.OverrideRoot is null)
            AppPaths.OverrideRoot = Path.Combine(Path.GetTempPath(), $"filmbatch-smoke-{Environment.ProcessId}");
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
            // macOS：顯示 Dock 圖示並保留 Avalonia 在應用程式選單追加的預設項目（關閉視窗、結束等）
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .With(FontOptions())
            .LogToTrace();


    /// <summary>預設字型與後備字型（計畫書 2.2.3）。</summary>
    internal static FontManagerOptions FontOptions()
    {
        // DefaultFamilyName 以複合字型清單表示：首名缺席時逐一解析，尾端固定為
        // 各平台必然存在的字型（Windows 的 Arial；macOS 的 Helvetica Neue；Linux 的
        // sans）。TextRunProperties.CachedGlyphTypeface 在預設家族整個解析不到時
        // 會直接擲回（例如沒有繁中字型的 Windows 找不到 Microsoft JhengHei UI），
        // 空白 TextBlock 也會踩到。FontFallbacks 仍提供逐字碼的備援。
        var (family, fallbacks) = OperatingSystem.IsWindows()
            ? ("Microsoft JhengHei UI, Microsoft JhengHei, Segoe UI, Arial",
               new[] { "Microsoft JhengHei", "Segoe UI" })
            : OperatingSystem.IsMacOS()
            ? ("PingFang TC, Heiti TC, Helvetica Neue",
               new[] { "Heiti TC", "Helvetica Neue" })
            : ("Noto Sans CJK TC, Noto Sans TC, Noto Sans, sans",
               new[] { "Noto Sans TC", "Noto Sans" });
        return new FontManagerOptions
        {
            DefaultFamilyName = family,
            FontFallbacks = fallbacks.Select(f => new FontFallback { FontFamily = new FontFamily(f) }).ToArray(),
        };
    }
}
