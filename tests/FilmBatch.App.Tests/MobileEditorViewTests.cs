using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;
using FilmBatch.Mobile;

namespace FilmBatch.App.Tests;

// MobileEditorView changes the process-wide paths and language.
[Collection(nameof(LocalizationTests))]
public sealed class MobileEditorViewTests
{
    [AvaloniaFact]
    public void ReopeningOwnedProject_ClearsWorkingStatus()
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 1, longEdge: 60);
        host.Show(lastFolder: folder);

        TestEnv.PumpUntil(() => host.View.Main.Folder == folder
            && host.View.Main.Preview.Image is not null && !host.View.Main.Preview.IsBusy);
        Assert.True(string.IsNullOrEmpty(host.Status.Text), $"Reopened project retained a status: {host.Status.Text}");
    }

    [AvaloniaFact]
    public void CancellingPhotosSelection_ClearsWorkingStatus()
    {
        using var host = new MobileHost();
        host.Show(pickPhotos: (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        host.ClickPhotos();

        Assert.True(string.IsNullOrEmpty(host.Status.Text), $"Cancelled picker retained a status: {host.Status.Text}");
        Assert.Empty(host.Workspace.ListProjects());
    }

    [AvaloniaFact]
    public void SuccessfulPhotosImport_PreservesResultStatus()
    {
        using var host = new MobileHost();
        var incoming = Assert.Single(SampleImages.Generate(host.Workspace.Incoming, count: 1, longEdge: 60));
        var source = File.ReadAllBytes(incoming);
        var selection = new TaskCompletionSource<IReadOnlyList<string>>();
        host.Show(pickPhotos: (_, _) => selection.Task);
        host.ClickPhotos();
        var working = host.Status.Text;
        Assert.False(string.IsNullOrEmpty(working));

        selection.SetResult([incoming]);
        TestEnv.PumpUntil(() => host.View.Main.Folder is not null && host.View.Main.Preview.Image is not null
            && !host.View.Main.Preview.IsBusy && host.Status.Text != working);
        Assert.False(string.IsNullOrEmpty(host.Status.Text));
        Assert.Contains("1", host.Status.Text);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(host.View.Main.Folder!, Path.GetFileName(incoming))));
    }

    [AvaloniaFact]
    public void FailedPhotosSelection_PreservesErrorStatus()
    {
        using var host = new MobileHost();
        host.Show(pickPhotos: (_, _) => Task.FromException<IReadOnlyList<string>>(new IOException("synthetic picker failure")));
        host.ClickPhotos();

        Assert.Contains("synthetic picker failure", host.Status.Text);
        Assert.Empty(host.Workspace.ListProjects());
    }

    [AvaloniaFact]
    public void SuspendingPhotoSelection_PreservesCancellationStatus()
    {
        using var host = new MobileHost();
        host.Show(pickPhotos: async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        });
        host.ClickPhotos();
        var working = host.Status.Text;

        host.View.Suspend();
        TestEnv.PumpUntil(() => host.Status.Text != working);
        Assert.Contains("cancel", host.Status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.Workspace.ListProjects());
    }

    private sealed class MobileHost : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "filmbatch-mobile-status-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousRoot = AppPaths.OverrideRoot;
        private readonly CultureInfo _previousCulture = Loc.Instance.Culture;
        private Window? _window;

        public MobileHost()
        {
            AppPaths.OverrideRoot = Path.Combine(_root, "application-data");
            Workspace = new MobileWorkspace(Path.Combine(_root, "workspace"));
        }

        public MobileWorkspace Workspace { get; }
        public MobileEditorView View { get; private set; } = null!;
        public TextBlock Status => View.FindControl<TextBlock>("MobileStatus")!;

        public void Show(string? lastFolder = null,
            Func<string, CancellationToken, Task<IReadOnlyList<string>>>? pickPhotos = null)
        {
            new AppSettings { Language = "en", LastFolder = lastFolder }.Save();
            View = new MobileEditorView(Workspace, pickPhotos);
            _window = new Window { Width = 400, Height = 800, Content = View };
            _window.Show();
        }

        public void ClickPhotos() => View.FindControl<Button>("ImportPhotosButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public void Dispose()
        {
            _window?.Close();
            View?.Dispose();
            AppPaths.OverrideRoot = _previousRoot;
            Loc.Instance.Culture = _previousCulture;
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
