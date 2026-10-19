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

    public string Version { get; }
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string OperatingSystem { get; } = $"{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）";
    public string Gpu { get; }
    public string Repository => RepositoryUrl;

    public IReadOnlyList<PackageInfo> Packages { get; }

    /// <param name="gpuName">Currently active renderer name.</param>
    /// <param name="hostVersion">Host assembly informational version; mobile and desktop hosts pass their own.</param>
    /// <param name="hostPackages">Host package rows; defaults to the components available on the current platform.</param>
    public AboutViewModel(string gpuName, string? hostVersion = null, IReadOnlyList<PackageInfo>? hostPackages = null)
    {
        Gpu = gpuName;
        Version = hostVersion ?? FormatVersion(typeof(AboutViewModel).Assembly);
        Packages = hostPackages ?? CurrentPackages();
    }

    /// <summary>Gets package rows for the current platform without claiming unavailable native components.</summary>
    public static IReadOnlyList<PackageInfo> CurrentPackages()
    {
        if (System.OperatingSystem.IsIOS())
            return
            [
                new("Avalonia UI", FormatVersion(typeof(Avalonia.AvaloniaObject).Assembly, false), "MIT", "https://github.com/AvaloniaUI/Avalonia"),
                new("CommunityToolkit.Mvvm", FormatVersion(typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).Assembly, false), "MIT", "https://github.com/CommunityToolkit/dotnet"),
                new("Apple ImageIO / CoreGraphics", RuntimeInformation.OSDescription, "Apple system frameworks", "https://developer.apple.com/documentation/imageio"),
                new(".NET Runtime", Environment.Version.ToString(), "MIT", "https://github.com/dotnet/runtime"),
                new("RawTherapee Film Simulation Collection", "2015-09-20", "CC BY-SA 4.0", "http://rawtherapee.com/shared/HaldCLUT.zip"),
            ];
        return
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
        new("RawTherapee Film Simulation Collection (Pat David, Pavlov Dmitry, Michael Ezra)", "2015-09-20", "CC BY-SA 4.0", "http://rawtherapee.com/shared/HaldCLUT.zip"),
    ];
    }

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
