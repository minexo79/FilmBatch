using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FilmBatch.Mobile.Probes;
using Foundation;
using UIKit;

namespace FilmBatch.Mobile.iOS;

public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

// Diagnostic bootstrap only: these run/cancel/report controls do not choose the product UI.
[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;
}

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) throw new InvalidOperationException("Expected a UIWindowScene.");
        Window = new UIWindow(windowScene) { RootViewController = new ProbeController() };
        Window.MakeKeyAndVisible();
    }

    public override void DidEnterBackground(UIScene scene)
    {
        // No entitlement or promise of unlimited background GPU/AI execution in this experiment.
        (Window?.RootViewController as ProbeController)?.Cancel();
    }

    public override void DidDisconnect(UIScene scene)
    {
        (Window?.RootViewController as ProbeController)?.Cancel();
        Window?.RootViewController?.Dispose();
        Window?.Dispose();
        Window = null;
    }
}

internal sealed class ProbeController : UIViewController
{
    private readonly UIButton _run = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _cancel = UIButton.FromType(UIButtonType.System);
    private readonly UITextView _report = new() { Editable = false };
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var view = View!;
        view.BackgroundColor = UIColor.SystemBackground;
        _run.SetTitle("Run technical probes", UIControlState.Normal);
        _cancel.SetTitle("Cancel", UIControlState.Normal);
        _cancel.Enabled = false;
        _run.TouchUpInside += Run;
        _cancel.TouchUpInside += CancelClicked;
        _report.Text = "Technical diagnostic host, not the FilmBatch editor.\n\n"
            + "Runs the same Core/RAW, ImageIO, ONNX and GPU probe source as the desktop host.\n"
            + "Expected iOS native-library gaps remain blocked, not successful CPU fallbacks.\n\n"
            + "Optional Documents/probe-options.json accepts ProbeOptions. Output is always written to Documents/results/report.json.\n"
            + "Relative RawPath and ModelPath values are resolved inside Documents. No model downloads or Photos permissions.";
        var controls = new UIStackView([_run, _cancel]) { Axis = UILayoutConstraintAxis.Horizontal, Spacing = 16 };
        var stack = new UIStackView([controls, _report])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        view.AddSubview(stack);
        var safe = view.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints([
            stack.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor, 16),
            stack.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor, -16),
            stack.TopAnchor.ConstraintEqualTo(safe.TopAnchor, 12),
            stack.BottomAnchor.ConstraintEqualTo(safe.BottomAnchor, -12)
        ]);
    }

    internal void Cancel() => _cancellation?.Cancel();
    private void CancelClicked(object? sender, EventArgs args) => Cancel();

    private async void Run(object? sender, EventArgs args)
    {
        if (_disposed || _cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _run.Enabled = false;
        _cancel.Enabled = true;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var output = Path.Combine(documents, "results");
        var path = Path.Combine(output, "report.json");
        _report.Text = "Running. Cancel or backgrounding discards the current work; no source edits are persisted.";
        try
        {
            var config = Path.Combine(documents, "probe-options.json");
            var options = File.Exists(config)
                ? JsonSerializer.Deserialize(File.ReadAllText(config), MobileOptionsJson.Default.ProbeOptions)
                    ?? throw new InvalidDataException("Empty probe-options.json.")
                : new ProbeOptions(output);
            options = options with
            {
                OutputDirectory = output,
                RawPath = Resolve(options.RawPath),
                ModelPath = Resolve(options.ModelPath)
            };
            var result = await Task.Run(() => ProbeRunner.Run(options, cancellation.Token), cancellation.Token);
            ProbeRunner.WriteReport(path, result);
            if (!_disposed) _report.Text = File.ReadAllText(path);
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException && cancellation.IsCancellationRequested;
            var result = new ProbeReport(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                [new("ios-host", cancelled ? "blocked" : "fail", cancelled ? "Cancelled by user or app background transition; no result accepted." : error.ToString(), [])]);
            try
            {
                ProbeRunner.WriteReport(path, result);
                if (!_disposed) _report.Text = File.ReadAllText(path);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(writeError);
                if (!_disposed) _report.Text = error + "\nCould not write report: " + writeError.Message;
            }
        }
        finally
        {
            _cancellation = null;
            if (!_disposed)
            {
                _run.Enabled = true;
                _cancel.Enabled = false;
            }
        }
        return;

        string? Resolve(string? input) => string.IsNullOrWhiteSpace(input) ? null
            : Path.IsPathRooted(input) ? input : Path.Combine(documents, input);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cancel();
            _disposed = true;
            _run.TouchUpInside -= Run;
            _cancel.TouchUpInside -= CancelClicked;
        }
        base.Dispose(disposing);
    }
}

[JsonSerializable(typeof(ProbeOptions))]
internal sealed partial class MobileOptionsJson : JsonSerializerContext;
