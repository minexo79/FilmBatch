using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FilmBatch.App.Localization;

namespace FilmBatch.App.ViewModels.Dialogs;

/// <summary>第三方套件：版本於執行時從已載入的組件讀取，與實際發佈的檔案一致。</summary>
public sealed record PackageInfo(string Name, string Version, string License, string Url);

/// <summary>「說明 → 關於」：版本、執行環境、第三方套件與專案連結。</summary>
public sealed class AboutViewModel
{
    public const string RepositoryUrl = "https://github.com/minexo79/FilmBatch";
    public const string IssuesUrl = RepositoryUrl + "/issues";

    public string Version { get; } = FormatVersion(typeof(App).Assembly);
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string OperatingSystem { get; } = $"{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）";
    public string Gpu { get; }
    public string Repository => RepositoryUrl;

    public IReadOnlyList<PackageInfo> Packages { get; } =
    [
        Package("Avalonia UI", "Avalonia.Base", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        Package("Dock.Avalonia", "Dock.Avalonia", "MIT", "https://github.com/wieslawsoltes/Dock"),
        Package("CommunityToolkit.Mvvm", "CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        Package("SkiaSharp", "SkiaSharp", "MIT", "https://github.com/mono/SkiaSharp"),
        Package("HarfBuzzSharp", "HarfBuzzSharp", "MIT", "https://github.com/mono/SkiaSharp"),
        Package("WebGPUSharp", "WebGpuSharp", "MIT", "https://github.com/EmilSV/WebGPUSharp"),
        new("Dawn (WebGPU)", Loc.Get("About.Native"), "BSD-3-Clause", "https://dawn.googlesource.com/dawn"),
        Package("Magick.NET / ImageMagick", "Magick.NET-Q16-HDRI-AnyCPU", "Apache-2.0", "https://github.com/dlemstra/Magick.NET"),
        Package("YamlDotNet", "YamlDotNet", "MIT", "https://github.com/aaubry/YamlDotNet"),
        Package("System.IO.Hashing", "System.IO.Hashing", "MIT", "https://github.com/dotnet/runtime"),
        new(".NET Runtime", Environment.Version.ToString(), "MIT", "https://github.com/dotnet/runtime"),
        new("Recipe Lab", "fd97eec", "MIT", "https://github.com/voxivoid/recipe-lab-sony-pmca"),
    ];

    public AboutViewModel(string gpuName) => Gpu = gpuName;

    /// <summary>回報問題用的純文字摘要（「複製版本資訊」）。</summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FilmBatch {Version}");
            sb.AppendLine($"{Loc.Get("About.Runtime")}: {Runtime}");
            sb.AppendLine($"{Loc.Get("About.OS")}: {OperatingSystem}");
            sb.AppendLine($"GPU: {Gpu}");
            sb.AppendLine();
            foreach (var p in Packages) sb.AppendLine($"{p.Name} {p.Version} ({p.License})");
            return sb.ToString();
        }
    }

    private static PackageInfo Package(string name, string assembly, string license, string url)
    {
        string version;
        try { version = FormatVersion(Assembly.Load(new AssemblyName(assembly)), withCommit: false); }
        catch (Exception) { version = "—"; }
        return new PackageInfo(name, version, license, url);
    }

    /// <summary>取 InformationalVersion；SDK 附加的「+commit」縮短為 7 碼（套件則省略）。</summary>
    private static string FormatVersion(Assembly assembly, bool withCommit = true)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3) ?? "—";
        var plus = info.IndexOf('+');
        if (plus < 0) return info;
        var commit = info[(plus + 1)..];
        return withCommit && commit.Length >= 7 ? $"{info[..plus]}（{commit[..7]}）" : info[..plus];
    }
}
