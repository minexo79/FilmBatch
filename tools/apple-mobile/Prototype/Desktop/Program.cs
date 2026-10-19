using System.Globalization;
using Avalonia;
using FilmBatch.Mobile.Probes;
using FilmBatch.Mobile.Prototype;
using static FilmBatch.Mobile.Prototype.PrototypeApp;

// Local desktop observation of the exact shared view. Only the caller-supplied --out directory
// is used; this host never reads or writes FilmBatch user settings, presets or projects.
const string Usage = """
    Usage: filmbatch-mobile-prototype --out <owned-directory> [--width N --height N] [--raw <path>] [--model <onnx>] [--provider CPU|CoreML] [--no-gpu] [--window WxH]
    """;
var parsed = Parse(args);
if (parsed.Help)
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
RequestedOptions.Current = parsed.Options!;
PrototypeHostWindow.PrototypeHostOptions.WindowWidth = parsed.WindowWidth;
PrototypeHostWindow.PrototypeHostOptions.WindowHeight = parsed.WindowHeight;
return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PrototypeApp>().UsePlatformDetect().LogToTrace();

(ProbeOptions? Options, bool Success, bool Help, string? Message, double WindowWidth, double WindowHeight) Parse(string[] arguments)
{
    string? output = null;
    var width = 768;
    var height = 512;
    string? raw = null;
    string? model = null;
    var provider = "CPU";
    var includeGpu = true;
    var windowWidth = 1024d;
    var windowHeight = 720d;
    for (var i = 0; i < arguments.Length; i++)
    {
        switch (arguments[i])
        {
            case "--out" when i + 1 < arguments.Length:
                output = arguments[++i];
                break;
            case "--width" when i + 1 < arguments.Length:
                if (!TryDimension(arguments[++i], out width)) return (null, false, false, "Width must be a positive integer.", 0, 0);
                break;
            case "--height" when i + 1 < arguments.Length:
                if (!TryDimension(arguments[++i], out height)) return (null, false, false, "Height must be a positive integer.", 0, 0);
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
            case "--window" when i + 1 < arguments.Length:
                var size = arguments[++i].Split('x', StringSplitOptions.RemoveEmptyEntries);
                if (size.Length != 2
                    || !double.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out windowWidth)
                    || !double.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out windowHeight)
                    || windowWidth < 360 || windowHeight < 520)
                    return (null, false, false, "--window needs WxH with W>=360 and H>=520.", 0, 0);
                break;
            case "--help" or "-h":
                return (null, true, true, null, 0, 0);
            case "--out" or "--width" or "--height" or "--raw" or "--model" or "--provider" or "--window":
                return (null, false, false, $"{arguments[i]} needs a value.", 0, 0);
            default:
                return (null, false, false, $"Unknown argument: {arguments[i]}", 0, 0);
        }
    }
    if (string.IsNullOrWhiteSpace(output))
        return (null, false, false, "Missing required --out <owned-directory>.", 0, 0);
    if (!string.Equals(provider, "CPU", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(provider, "CoreML", StringComparison.OrdinalIgnoreCase))
        return (null, false, false, "Provider must be CPU or CoreML.", 0, 0);
    try
    {
        _ = checked(width * height * 3);
    }
    catch (OverflowException)
    {
        return (null, false, false, "Width and height are too large for the RGB fixture.", 0, 0);
    }
    return (new ProbeOptions(output, width, height, raw, model, provider, includeGpu), true, false, null, windowWidth, windowHeight);

    static bool TryDimension(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return value >= 1;
        value = 0;
        return false;
    }
}
