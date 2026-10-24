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

    public override void WillResignActive(UIScene scene)
    {
        // Cancel on interruption, before backgrounding; Cancel leaves completed reports intact.
        (Window?.RootViewController as ProbeController)?.Cancel();
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

// Pending autorun request dropped into Documents by the Simulator runner before launch.
// Fields mirror ProbeOptions plus RunTag, which proves which invocation owns a result.
internal sealed record PendingRun(string? RunTag = null, bool AutoRun = true,
    int? Width = null, int? Height = null, string? RawPath = null, string? ModelPath = null,
    string? Provider = null, bool? IncludeGpu = null);

internal sealed class ProbeController : UIViewController
{
    private readonly UIButton _run = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _cancel = UIButton.FromType(UIButtonType.System);
    private readonly UITextView _report = new() { Editable = false };
    private readonly UILabel _status = new() { Lines = 0 };
    private RunEpoch? _current;
    private int _nextRun;
    private int _acceptedRun;
    private bool _disposed;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var view = View!;
        view.BackgroundColor = UIColor.SystemBackground;
        _run.AccessibilityIdentifier = "Probe.Run";
        _cancel.AccessibilityIdentifier = "Probe.Cancel";
        _report.AccessibilityIdentifier = "Probe.Report";
        _status.AccessibilityIdentifier = "Probe.Status";
        _run.SetTitle("Run technical probes", UIControlState.Normal);
        _cancel.SetTitle("Cancel", UIControlState.Normal);
        _cancel.Enabled = false;
        _run.TouchUpInside += RunClicked;
        _cancel.TouchUpInside += CancelClicked;
        _status.Text = "Ready.";
        _report.Text = "Technical diagnostic host, not the FilmBatch editor.\n\n"
            + "Runs the same Core/RAW, ImageIO, ONNX and GPU probe source as the desktop host.\n"
            + "Expected iOS native-library gaps remain blocked, not successful CPU fallbacks.\n\n"
            + "Optional Documents/probe-options.json accepts ProbeOptions; Documents/pending-run.json triggers an automation run. "
            + "Launch arguments -probe-width/-probe-height/-probe-raw/-probe-model/-probe-provider/-probe-no-gpu override configuration. "
            + "Output is always written to Documents/results/report.json.\n"
            + "Relative RawPath and ModelPath values are resolved inside Documents. No model downloads or Photos permissions.";
        var controls = new UIStackView([_run, _cancel]) { Axis = UILayoutConstraintAxis.Horizontal, Spacing = 16 };
        var stack = new UIStackView([controls, _status, _report])
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
        StartPendingRun();
    }

    internal void Cancel()
    {
        if (_current is null) return;
        _current.Cancel();
        if (!_disposed)
        {
            _cancel.Enabled = false;
            _run.Enabled = _current is null;
            _report.Text = string.Empty;
            _status.Text = _acceptedRun == 0
                ? "Cancelling; result discarded."
                : $"Cancelling; result discarded. Last accepted: Run {_acceptedRun}.";
        }
    }
    private void CancelClicked(object? sender, EventArgs args) => Cancel();
    private void RunClicked(object? sender, EventArgs args) => StartRun(new RunEpoch(++_nextRun), options: null);

    // File-based automation entry: the Simulator runner writes Documents/pending-run.json
    // before launch so headless runs carry an explicit tag and option overrides.
    private void StartPendingRun()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var pendingPath = Path.Combine(documents, "pending-run.json");
        PendingRun? pending;
        try
        {
            pending = File.Exists(pendingPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(pendingPath), PendingRunJson.Default.PendingRun)
                : null;
            if (pending is not null) File.Delete(pendingPath);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            _status.Text = "Pending run rejected: " + error.Message;
            return;
        }
        if (pending is null || !pending.AutoRun) return;
        var options = new ProbeOptions(
            OutputDirectory: Path.Combine(documents, "results"),
            Width: pending.Width ?? 768,
            Height: pending.Height ?? 512,
            RawPath: pending.RawPath,
            ModelPath: pending.ModelPath,
            Provider: pending.Provider ?? "CPU",
            IncludeGpu: pending.IncludeGpu ?? true);
        StartRun(new RunEpoch(++_nextRun), options, pending.RunTag);
    }

    private async void StartRun(RunEpoch epoch, ProbeOptions? options, string? runTag = null)
    {
        if (_disposed || _current is not null)
        {
            epoch.Dispose();
            return;
        }
        _current = epoch;
        _run.Enabled = false;
        _cancel.Enabled = true;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var output = Path.Combine(documents, "results");
        var path = Path.Combine(output, "report.json");
        var tagPath = Path.Combine(output, "run-tag.txt");
        _status.Text = $"Run {epoch.Id} running.";
        _report.Text = "Running. Cancel or backgrounding discards the current work; no source edits are persisted.";
        try
        {
            if (options is null)
            {
                var config = Path.Combine(documents, "probe-options.json");
                options = File.Exists(config)
                    ? JsonSerializer.Deserialize(File.ReadAllText(config), MobileOptionsJson.Default.ProbeOptions)
                        ?? throw new InvalidDataException("Empty probe-options.json.")
                    : new ProbeOptions(output);
            }
            options = options.WithLaunchArguments(NSProcessInfo.ProcessInfo.Arguments);
            options = options with
            {
                OutputDirectory = output,
                RawPath = Resolve(options.RawPath),
                ModelPath = Resolve(options.ModelPath)
            };
            var result = await Task.Run(() => ProbeRunner.Run(options, epoch.Token));
            if (!epoch.TryComplete())
            {
                // Cancelled, superseded or disposed: discard the finished report instead
                // of letting stale results overwrite visible or persisted state.
                epoch.Dispose();
                return;
            }
            ProbeRunner.WriteReport(path, result);
            var tag = runTag ?? $"manual-{epoch.Id}";
            File.WriteAllText(tagPath, tag);
            _acceptedRun = epoch.Id;
            var summary = string.Join(", ", result.Results.Select(r => $"{r.Name}:{r.Status}"));
            _status.Text = $"Run {epoch.Id} complete; tag {tag}; exit {result.ExitCode}; {summary}.";
            _report.Text = File.ReadAllText(path);
            Console.WriteLine($"probe-run-complete tag={tag} exit={result.ExitCode} results={summary}");
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException && epoch.IsCancellationRequested;
            if (!epoch.TryComplete())
            {
                epoch.Dispose();
                return;
            }
            var result = new ProbeReport(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                [new("ios-host", cancelled ? "blocked" : "fail", cancelled
                    ? "Cancelled by user or app background transition; no result accepted."
                    : error.ToString(), [])]);
            try
            {
                ProbeRunner.WriteReport(path, result);
                var tag = runTag ?? $"manual-{epoch.Id}";
                File.WriteAllText(tagPath, tag);
                _acceptedRun = epoch.Id;
                _status.Text = $"Run {epoch.Id} {(cancelled ? "cancelled" : "failed")}; tag {tag}; exit {result.ExitCode}.";
                _report.Text = File.ReadAllText(path);
                Console.WriteLine($"probe-run-complete tag={tag} exit={result.ExitCode} results=ios-host:{(cancelled ? "blocked" : "fail")}");
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(writeError);
                _status.Text = $"Run {epoch.Id} failed; report unwritable.";
                _report.Text = error + "\nCould not write report: " + writeError.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_current, epoch)) _current = null;
            epoch.Dispose();
            if (!_disposed && _current is null)
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
            _disposed = true;
            var epoch = _current;
            _current = null;
            epoch?.Dispose();
            _run.TouchUpInside -= RunClicked;
            _cancel.TouchUpInside -= CancelClicked;
        }
        base.Dispose(disposing);
    }
}

[JsonSerializable(typeof(ProbeOptions))]
internal sealed partial class MobileOptionsJson : JsonSerializerContext;

[JsonSerializable(typeof(PendingRun))]
internal sealed partial class PendingRunJson : JsonSerializerContext;
