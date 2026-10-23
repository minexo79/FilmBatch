using System.Diagnostics;

namespace FilmBatch.App.Services;

/// <summary>
/// 在系統檔案管理員中顯示檔案：Windows 檔案總管選取該檔、macOS Finder 標示該檔、Linux 開啟所在資料夾。
/// iOS／Android 等沒有檔案管理員可呼叫的平台不支援（介面隱藏該動作）。
/// </summary>
public static class FileReveal
{
    public enum Platform { Windows, MacOS, Linux }

    /// <summary>目前平台；不支援時為 null。</summary>
    public static Platform? Current =>
        OperatingSystem.IsWindows() ? Platform.Windows
        : OperatingSystem.IsMacOS() ? Platform.MacOS
        : OperatingSystem.IsLinux() ? Platform.Linux
        : null;

    public static bool IsSupported => Current is not null;

    /// <summary>
    /// 建立啟動參數。Windows 的 explorer.exe 自行解析命令列，只接受 <c>/select,"路徑"</c> 這種寫法，
    /// 因此不能用 ArgumentList（會把整段加上引號）；路徑本身不會含有引號，非 ASCII 路徑由 Unicode 命令列傳遞。
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string fullPath, Platform platform)
    {
        switch (platform)
        {
            case Platform.Windows:
                return new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = false };
            case Platform.MacOS:
            {
                var info = new ProcessStartInfo("open") { UseShellExecute = false };
                info.ArgumentList.Add("-R");
                info.ArgumentList.Add(fullPath);
                return info;
            }
            case Platform.Linux:
            {
                // xdg-open 無法選取檔案，只開啟所在資料夾
                var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                info.ArgumentList.Add(Path.GetDirectoryName(fullPath) ?? fullPath);
                return info;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(platform));
        }
    }

    /// <summary>在目前平台的檔案管理員中顯示檔案；不支援的平台丟出 <see cref="PlatformNotSupportedException"/>。</summary>
    public static void Reveal(string path)
    {
        var platform = Current ?? throw new PlatformNotSupportedException();
        using var _ = Process.Start(CreateStartInfo(Path.GetFullPath(path), platform));
    }
}
