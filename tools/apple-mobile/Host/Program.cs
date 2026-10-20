using System.Globalization;
using FilmBatch.Mobile.Probes;

// Desktop diagnostic entry: explicit flags only, no product UI. Writes <out>/report.json.
const string Usage = """
    Usage: filmbatch-mobile-probe --out <directory> [--width N --height N] [--raw <path>] [--model <onnx>] [--provider CPU|CoreML] [--no-gpu]
    """;
var parsed = Parse(args);
if (parsed.Success && parsed.Help)
{
    Console.WriteLine(Usage);
    return 0;
}
if (!parsed.Success)
{
    Console.Error.WriteLine(parsed.Message ?? "Invalid arguments.");
    Console.Error.WriteLine(Usage);
    return 2;
}
var options = parsed.Options!;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, pressed) =>
{
    pressed.Cancel = true;
    cancellation.Cancel();
};
try
{
    var report = ProbeRunner.Run(options, cancellation.Token);
    var reportPath = Path.Combine(options.OutputDirectory, "report.json");
    ProbeRunner.WriteReport(reportPath, report);
    foreach (var result in report.Results)
        Console.WriteLine($"{result.Name}: {result.Status} — {result.Detail}");
    Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");
    return report.ExitCode;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
{
    // Runner guards (bad dimensions, input inside output, unwritable directory) stay actionable.
    Console.Error.WriteLine(error.Message);
    return 2;
}

(ProbeOptions? Options, bool Success, bool Help, string? Message) Parse(string[] arguments)
{
    string? output = null;
    var width = 768;
    var height = 512;
    string? raw = null;
    string? model = null;
    var provider = "CPU";
    var includeGpu = true;
    for (var i = 0; i < arguments.Length; i++)
    {
        switch (arguments[i])
        {
            case "--out" when i + 1 < arguments.Length:
                output = arguments[++i];
                break;
            case "--width" when i + 1 < arguments.Length:
                if (!TryDimension(arguments[++i], out width)) return (null, false, false, "Width must be a positive integer.");
                break;
            case "--height" when i + 1 < arguments.Length:
                if (!TryDimension(arguments[++i], out height)) return (null, false, false, "Height must be a positive integer.");
                break;
            case "--raw" when i + 1 < arguments.Length:
                raw = arguments[++i];
                break;
            case "--model" when i + 1 < arguments.Length:
                model = arguments[++i];
                break;
            case "--provider" when i + 1 < arguments.Length:
                provider = arguments[++i];
                break;
            case "--no-gpu":
                includeGpu = false;
                break;
            case "--help" or "-h":
                return (null, true, true, null);
            case "--out" or "--width" or "--height" or "--raw" or "--model" or "--provider":
                return (null, false, false, $"{arguments[i]} needs a value.");
            default:
                return (null, false, false, $"Unknown argument: {arguments[i]}");
        }
    }
    if (string.IsNullOrWhiteSpace(output))
        return (null, false, false, "Missing required --out <directory>.");
    if (!string.Equals(provider, "CPU", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(provider, "CoreML", StringComparison.OrdinalIgnoreCase))
        return (null, false, false, "Provider must be CPU or CoreML.");
    try
    {
        _ = checked(width * height * 3);
    }
    catch (OverflowException)
    {
        return (null, false, false, "Width and height are too large for the RGB fixture.");
    }
    return (new ProbeOptions(output, width, height, raw, model, provider, includeGpu), true, false, null);

    static bool TryDimension(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return value >= 1;
        value = 0;
        return false;
    }
}
