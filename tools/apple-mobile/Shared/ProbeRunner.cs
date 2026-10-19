using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilmBatch.Mobile.Probes;

public sealed record ProbeOptions(string OutputDirectory, int Width = 768, int Height = 512,
    string? RawPath = null, string? ModelPath = null, string Provider = "CPU", bool IncludeGpu = true,
    string? RunTag = null)
{
    // Genuine workload overrides shared by both iOS hosts and their XCTest consumers.
    public ProbeOptions WithLaunchArguments(IReadOnlyList<string> arguments)
    {
        var options = this;
        for (var i = 1; i < arguments.Count; i++)
        {
            string Value() => ++i < arguments.Count ? arguments[i]
                : throw new ArgumentException($"Missing value for {arguments[i - 1]}.");
            switch (arguments[i])
            {
                case "-probe-width": options = options with { Width = int.Parse(Value(), CultureInfo.InvariantCulture) }; break;
                case "-probe-height": options = options with { Height = int.Parse(Value(), CultureInfo.InvariantCulture) }; break;
                case "-probe-raw": options = options with { RawPath = Value() }; break;
                case "-probe-model": options = options with { ModelPath = Value() }; break;
                case "-probe-provider": options = options with { Provider = Value() }; break;
                case "-probe-no-gpu": options = options with { IncludeGpu = false }; break;
            }
        }
        return options;
    }
}

public sealed record ProbeResult(string Name, string Status, string Detail, Dictionary<string, string> Metrics);

public sealed record ProbeReport(string Runtime, string Platform, string Architecture, List<ProbeResult> Results)
{
    public string OperatingSystem { get; } = System.OperatingSystem.IsIOS() ? "iOS"
        : System.OperatingSystem.IsMacOS() ? "macOS"
        : System.OperatingSystem.IsWindows() ? "Windows"
        : System.OperatingSystem.IsLinux() ? "Linux" : "unknown";
    public string RuntimeIdentifier { get; } = RuntimeInformation.RuntimeIdentifier;

    public int ExitCode => Results.Any(result => result.Status == "fail") ? 1
        : Results.Any(result => result.Status == "blocked") ? 2 : 0;
}

public static class ProbeRunner
{
    public static ProbeReport Run(ProbeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 1);
        _ = checked(options.Width * options.Height * 3);
        // Generated fixtures/reports must never overwrite an explicitly selected source or model.
        var outputRoot = Path.TrimEndingDirectorySeparator(CanonicalPath(options.OutputDirectory));
        var outputPrefix = Path.EndsInDirectorySeparator(outputRoot) ? outputRoot : outputRoot + Path.DirectorySeparatorChar;
        ReadOnlySpan<string?> inputs = [options.RawPath, options.ModelPath];
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input)) continue;
            var inputPath = CanonicalPath(input);
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (inputPath.Equals(outputRoot, comparison) || inputPath.StartsWith(outputPrefix, comparison))
                throw new ArgumentException("Choose an output directory separate from RAW and model input files.");
        }
        Directory.CreateDirectory(options.OutputDirectory);
        var results = new List<ProbeResult>();
        Run("core-raw", () => CoreRawProbe.Run(options, cancellationToken));
        Run("apple-imageio", () => AppleImageIoProbe.Run(options.OutputDirectory, cancellationToken));
        Run("onnx", () => OnnxProbe.Run(options, cancellationToken));
        Run("gpu", () => GpuProbe.Run(options.IncludeGpu, cancellationToken));
        return new(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), results);

        void Run(string name, Func<ProbeResult> action)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { results.Add(action()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // A missing optional native closure is evidence, never a successful CPU substitute.
                var nativeMissing = error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                    || error.InnerException is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
                results.Add(new(name, nativeMissing ? "blocked" : "fail", error.ToString(), []));
            }
        }
    }

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(fullPath)!;
        // Resolve every existing ancestor too: resolving only the final file misses directory aliases.
        foreach (var part in fullPath[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(resolved, part);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            resolved = info.LinkTarget is not null
                ? CanonicalPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"Cannot resolve symbolic link: {candidate}"))
                : candidate;
        }
        return resolved;
    }

    public static void WriteReport(string path, ProbeReport report)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, ProbeReportJson.Default.ProbeReport));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProbeReport))]
internal sealed partial class ProbeReportJson : JsonSerializerContext;
