using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FilmBatch.Mobile.Probes;

namespace FilmBatch.Mobile.Prototype;

// Diagnostic view only: real Run/Cancel around ProbeRunner, real preview from the ImageIO PNG.
public sealed partial class PrototypeView : UserControl
{
    private const double WideMinWidth = 720;

    private readonly ProbeOptions _options;
    private CancellationTokenSource? _cancellation;
    private Bitmap? _preview;
    private int _runId;
    private bool _detached;

    public PrototypeView() : this(new ProbeOptions(Directory.CreateTempSubdirectory("filmbatch-prototype-").FullName)) { }

    public PrototypeView(ProbeOptions options, string? startupError = null)
    {
        _options = options;
        InitializeComponent();
        StatusText.Text = startupError is null ? ReadyText() : $"Configuration rejected: {startupError}";
        RunButton.IsEnabled = startupError is null;
        ReportText.Text = startupError;
        RunButton.Click += async (_, _) => await RunAsync();
        CancelButton.Click += (_, _) => CancelRun();
        Layout.SizeChanged += (_, _) => ApplyLayout(Layout.Bounds.Width);
        AttachedToVisualTree += (_, _) => _detached = false;
        DetachedFromVisualTree += (_, _) =>
        {
            _detached = true;
            CancelRun();
            ClearPreview();
        };
        ApplyLayout(double.NaN);
    }

    // Environment-driven cancellation (background/scene detach): no result accepted after it.
    public void CancelRun() => _cancellation?.Cancel();

    private async Task RunAsync()
    {
        if (_detached || _cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var id = ++_runId;
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ReportText.Text = string.Empty;
        StatusText.Text = "Running Core/RAW, ImageIO, ONNX and GPU probes.";
        ClearPreview();
        try
        {
            // Do not cancel scheduling: the awaited body must reach finally even when
            // Cancel is pressed before a thread-pool worker starts the native work.
            var report = await Task.Run(() => ProbeRunner.Run(_options, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (_detached || _runId != id || !ReferenceEquals(_cancellation, cancellation)) return;
            var path = Path.Combine(_options.OutputDirectory, "report.json");
            ProbeRunner.WriteReport(path, report);
            File.WriteAllText(Path.Combine(_options.OutputDirectory, "run-tag.txt"), _options.RunTag ?? Guid.NewGuid().ToString("N"));
            ReportText.Text = File.ReadAllText(path);
            StatusText.Text = $"Run {id} complete; exit {report.ExitCode}.";
            if (report.Results.Any(result => result.Name == "apple-imageio" && result.Status == "pass"))
                LoadPreview();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_detached && _runId == id)
                StatusText.Text = "Cancelled; result discarded. Run again to retry.";
        }
        catch (Exception error)
        {
            if (!_detached && _runId == id)
            {
                ReportText.Text = error.ToString();
                StatusText.Text = $"Run failed: {error.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                if (!_detached)
                {
                    RunButton.IsEnabled = true;
                    CancelButton.IsEnabled = false;
                }
            }
        }
    }

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        _preview?.Dispose();
        _preview = null;
        AutomationProperties.SetName(PreviewImage, "ImageIO output preview");
    }

    private void LoadPreview()
    {
        ClearPreview();
        var path = Path.Combine(_options.OutputDirectory, "apple-imageio-51x37-rgb16.png");
        if (!File.Exists(path))
        {
            StatusText.Text += "\nPreview: apple-imageio-51x37-rgb16.png was not produced by this run.";
            return;
        }
        try
        {
            _preview = new Bitmap(path);
            PreviewImage.Source = _preview;
            AutomationProperties.SetName(PreviewImage, $"ImageIO output preview {_preview.PixelSize.Width}x{_preview.PixelSize.Height}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text += $"\nPreview failed to load {path}: {error.Message}";
        }
    }

    // Fixed three-row AXAML plus one optional row appended once for the narrow layout.
    private void ApplyLayout(double width)
    {
        while (Layout.RowDefinitions.Count < 4)
            Layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (double.IsNaN(width) || width >= WideMinWidth)
        {
            Grid.SetRow(StatusPane, 1); Grid.SetColumn(StatusPane, 0); Grid.SetColumnSpan(StatusPane, 1);
            Grid.SetRow(PreviewPane, 1); Grid.SetColumn(PreviewPane, 1); Grid.SetColumnSpan(PreviewPane, 1);
            Grid.SetRow(ReportText, 2); Grid.SetColumn(ReportText, 0); Grid.SetColumnSpan(ReportText, 2);
            Controls.Orientation = Orientation.Horizontal;
        }
        else
        {
            Grid.SetRow(StatusPane, 1); Grid.SetColumn(StatusPane, 0); Grid.SetColumnSpan(StatusPane, 2);
            Grid.SetRow(PreviewPane, 2); Grid.SetColumn(PreviewPane, 0); Grid.SetColumnSpan(PreviewPane, 2);
            Grid.SetRow(ReportText, 3); Grid.SetColumn(ReportText, 0); Grid.SetColumnSpan(ReportText, 2);
            Controls.Orientation = Orientation.Vertical;
        }
    }


    private string ReadyText() =>
        "Isolated iOS prototype, not the FilmBatch editor.\n\n"
        + "Runs the shared Core/RAW, ImageIO, ONNX and GPU probe source with real native calls.\n"
        + $"Output: {Path.GetFullPath(_options.OutputDirectory)}\n"
        + $"Fixture: {_options.Width}x{_options.Height}; provider {_options.Provider}; GPU {(_options.IncludeGpu ? "included" : "skipped")}.";
}
