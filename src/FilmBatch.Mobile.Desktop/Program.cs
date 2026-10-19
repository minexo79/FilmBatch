using System.Globalization;
using Avalonia;
using FilmBatch.Mobile;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("Usage: filmbatch-mobile --workspace <owned-directory> [--width 1080] [--height 800]");
    return args.Length == 0 ? 2 : 0;
}
try
{
    string? workspace = null;
    for (var i = 0; i < args.Length; i++)
    {
        var option = args[i];
        if (i + 1 == args.Length) throw new ArgumentException("Missing value for " + option);
        var value = args[++i];
        switch (option)
        {
            case "--workspace": workspace = Path.GetFullPath(value); break;
            case "--width": MobileApp.DesktopWidth = Dimension(value, 360); break;
            case "--height": MobileApp.DesktopHeight = Dimension(value, 600); break;
            default: throw new ArgumentException("Unknown option: " + option);
        }
    }
    if (workspace is null) throw new ArgumentException("An explicit --workspace directory is required.");
    MobileApp.WorkspaceRoot = workspace;
    return AppBuilder.Configure<MobileApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

static double Dimension(string value, int minimum)
{
    if (!double.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimension)
        || dimension < minimum || dimension > 10000) throw new ArgumentException("Invalid window dimension: " + value);
    return dimension;
}
