using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using FilmBatch.Mobile.Probes;
using FilmBatch.Mobile.Prototype;
using Foundation;
using UIKit;
using static FilmBatch.Mobile.Prototype.PrototypeApp;

namespace FilmBatch.Mobile.Prototype.iOS;

// Scene-based Avalonia bootstrap (AvaloniaAppDelegate owns configurationForConnectingSceneSession
// and its internal AvaloniaSceneDelegate). Real background cancellation via Deactivated.
[Register("PrototypeAppDelegate")]
public sealed class PrototypeAppDelegate : AvaloniaAppDelegate<PrototypeApp>
{
    public PrototypeAppDelegate() => ((IAvaloniaAppDelegate)this).Deactivated += OnDeactivated;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        RequestedOptions.Current = ReadOptions();
        return base.CustomizeAppBuilder(builder);
    }

    private static void OnDeactivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind != ActivationKind.Background) return;
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime single
            && single.MainView is PrototypeView view)
            view.CancelRun();
    }

    private static ProbeOptions ReadOptions()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var output = Path.Combine(documents, "results");
        try
        {
            var config = Path.Combine(documents, "probe-options.json");
            var options = File.Exists(config)
                ? JsonSerializer.Deserialize(File.ReadAllText(config), PrototypeOptionsJson.Default.ProbeOptions)
                    ?? throw new InvalidDataException("Empty probe-options.json.")
                : new ProbeOptions(output);
            options = options.WithLaunchArguments(NSProcessInfo.ProcessInfo.Arguments);
            // Output stays inside the app sandbox; large real fixtures remain caller-configurable.
            return options with
            {
                OutputDirectory = output,
                RawPath = Resolve(documents, options.RawPath),
                ModelPath = Resolve(documents, options.ModelPath)
            };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException
            or ArgumentException or OverflowException or FormatException)
        {
            RequestedOptions.Error = error.ToString();
            Console.Error.WriteLine($"Rejected probe-options.json: {error.Message}");
            return new ProbeOptions(output);
        }

        static string? Resolve(string documents, string? input) => string.IsNullOrWhiteSpace(input) ? null
            : Path.IsPathRooted(input) ? input : Path.Combine(documents, input);
    }
}

public static class PrototypeMain
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(PrototypeAppDelegate));
}

[JsonSerializable(typeof(ProbeOptions))]
internal sealed partial class PrototypeOptionsJson : JsonSerializerContext;
