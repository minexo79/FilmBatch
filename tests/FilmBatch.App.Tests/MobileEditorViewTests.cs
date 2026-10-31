using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using FilmBatch.App.ViewModels.Tools;
using Avalonia.Input.Raw;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Params;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;
using FilmBatch.Mobile;
using Avalonia.VisualTree;

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

    [AvaloniaFact]
    public void DismissKeyboard_InModal_LeavesPromptOpenAndFocusInside()
    {
        using var host = new MobileHost();
        host.Show();
        var prompt = host.View.Main.Dialogs.PromptAsync("Name", "Name", "draft");
        var overlay = host.View.FindControl<Border>("ModalOverlay")!;
        TestEnv.PumpUntil(() => overlay.IsVisible && host.Window.FocusManager!.GetFocusedElement() is TextBox);
        var dismiss = host.View.FindControl<Button>("DismissKeyboardButton")!;
        // Headless has no software keyboard; expose its action, then exercise the real modal and focus.
        dismiss.IsVisible = true;
        Assert.True(dismiss.IsEffectivelyEnabled);
        host.View.AddHandler(InputElement.GotFocusEvent, (_, _) =>
        {
            if (host.Window.FocusManager!.GetFocusedElement() is not TextBox)
                dismiss.IsVisible = false;
        });
        host.Window.CaptureRenderedFrame()?.Dispose();
        var point = dismiss.TranslatePoint(new Point(dismiss.Bounds.Width / 2, dismiss.Bounds.Height / 2), host.Window)!.Value;

        using var touch = host.Window.TouchBegin(point);
        host.Window.TouchEnd(touch, point);

        Assert.False(prompt.IsCompleted);
        var focused = Assert.IsAssignableFrom<Control>(host.Window.FocusManager!.GetFocusedElement());
        Assert.IsNotType<TextBox>(focused);
        Assert.True(overlay.IsVisualAncestorOf(focused));
        for (var index = 0; index < 6; index++)
        {
            host.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            focused = Assert.IsAssignableFrom<Control>(host.Window.FocusManager.GetFocusedElement());
            Assert.True(overlay.IsVisualAncestorOf(focused));
        }
        host.View.Suspend();
        TestEnv.PumpUntil(() => prompt.IsCompleted);
        Assert.Null(prompt.GetAwaiter().GetResult());
    }

    [AvaloniaFact]
    public void UndoShortcut_WithNoFocusedChild_RestoresPhotoEdit()
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 1, longEdge: 60);
        host.Show(lastFolder: folder);
        var main = host.View.Main;
        TestEnv.PumpUntil(() => main.Preview.Source is not null && !main.Preview.IsBusy);
        var file = main.FocusFile!;
        var before = main.Service!.Project[file];
        main.Inspector.SetValue("basic.exposure", ParamValue.Of(1.0), null);
        main.Inspector.EndEdit();
        Assert.True(main.Stack.CanUndo);
        host.Window.FocusManager!.Focus(null);

        host.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Meta);

        Assert.Equal(before, main.Service.Project[file]);
        Assert.True(main.Stack.CanRedo);
    }

    [AvaloniaFact]
    public void DismissKeyboard_InExport_PreservesTextAndLeavesInputFocus()
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 1, longEdge: 60);
        host.Show(lastFolder: folder);
        TestEnv.PumpUntil(() => host.View.Main.Preview.Source is not null && !host.View.Main.Preview.IsBusy);
        host.View.Shell.SelectToolCommand.Execute(SidePanel.Export);
        host.Window.CaptureRenderedFrame()?.Dispose();
        var input = host.View.GetVisualDescendants().OfType<TextBox>()
            .Single(control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) == "Mobile.ExportTemplate");
        input.Text = "{name}_edited";
        input.Focus();
        var dismiss = host.View.FindControl<Button>("DismissKeyboardButton")!;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(dismiss.IsVisible);
        // A platform can hide the keyboard as soon as the pressed button takes focus.
        // The keyboard-only action then disappears before pointer release.
        dismiss.GotFocus += (_, _) => dismiss.IsVisible = false;
        host.Window.CaptureRenderedFrame()?.Dispose();
        var point = dismiss.TranslatePoint(new Point(dismiss.Bounds.Width / 2, dismiss.Bounds.Height / 2), host.Window)!.Value;

        using var touch = host.Window.TouchBegin(point);
        host.Window.TouchEnd(touch, point);

        var focused = host.Window.FocusManager!.GetFocusedElement() as Control;
        Assert.False(focused is TextBox || focused?.FindAncestorOfType<TextBox>() is not null);
        Assert.Equal("{name}_edited", host.View.Main.Sidebar.Export.FileNameTemplate);
        Assert.True(host.View.Shell.IsToolPanelOpen);
    }

    [AvaloniaFact]
    public void EscapeInFilename_DoesNotCloseToolsOrStealFocus()
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 1, longEdge: 60);
        host.Show(lastFolder: folder);
        TestEnv.PumpUntil(() => host.View.Main.Preview.Source is not null && !host.View.Main.Preview.IsBusy);
        host.View.Shell.SelectToolCommand.Execute(SidePanel.Export);
        host.Window.CaptureRenderedFrame()?.Dispose();
        var input = host.View.GetVisualDescendants().OfType<TextBox>()
            .Single(control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) == "Mobile.ExportTemplate");
        input.Focus();
        Assert.True(input.IsFocused);

        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.True(host.View.Shell.IsToolPanelOpen);
        Assert.True(input.IsFocused);
    }

    [AvaloniaFact]
    public void ModifiedEscape_DoesNotCancelCropOrCloseTools()
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 1, longEdge: 60);
        host.Show(lastFolder: folder);
        TestEnv.PumpUntil(() => host.View.Main.Preview.Source is not null && !host.View.Main.Preview.IsBusy);
        host.View.Shell.SelectToolCommand.Execute(SidePanel.Crop);
        host.View.Focus();

        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.Meta);

        Assert.True(host.View.Main.CropTool.IsActive);
        Assert.True(host.View.Shell.IsToolPanelOpen);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void LeavingSampling_DiscardsProposalAndReleasesCanvas(bool escape)
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 1, longEdge: 60);
        host.Show(lastFolder: folder);
        var main = host.View.Main;
        TestEnv.PumpUntil(() => main.Preview.Source is not null && !main.Preview.IsBusy);
        var before = main.Service!.Project[main.FocusFile!];
        host.View.Shell.SelectToolCommand.Execute(SidePanel.Inspector);
        main.PhotoTools.PickSkinCommand.Execute(null);
        Assert.True(main.PhotoTools.IsPicking);
        host.View.Focus();

        if (escape) host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        else host.View.Shell.CloseToolsCommand.Execute(null);

        Assert.False(main.PhotoTools.IsSampling);
        Assert.False(main.PhotoTools.IsPicking);
        Assert.Null(main.Preview.ActiveTool);
        Assert.Equal(before, main.Service.Project[main.FocusFile!]);
    }

    [AvaloniaFact]
    public void Escape_DiscardsUnappliedCrop_WithoutCreatingUndo()
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 1, longEdge: 60);
        host.Show(lastFolder: folder);
        TestEnv.PumpUntil(() => host.View.Main.Preview.Source is not null && !host.View.Main.Preview.IsBusy);
        var main = host.View.Main;
        var file = main.FocusFile!;
        var before = main.Service!.Project[file];
        main.Stack.Clear();
        host.View.Shell.SelectToolCommand.Execute(SidePanel.Crop);
        main.CropTool.DragHandle(CropHandle.Left, new Point(0.2, 0.5), true);
        Assert.True(main.CropTool.HasChanges);

        host.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });

        Assert.False(main.CropTool.IsActive);
        Assert.Equal(before, main.Service.Project[file]);
        Assert.False(main.Stack.CanUndo);
    }

    [AvaloniaFact]
    public void TouchMultiSelect_DoneKeepsTargets_ForOneUndoableEdit()
    {
        using var host = new MobileHost();
        var folder = host.Workspace.CreateProject();
        SampleImages.Generate(folder, count: 3, longEdge: 60);
        host.Show(lastFolder: folder);
        var main = host.View.Main;
        TestEnv.PumpUntil(() => main.Preview.Image is not null && !main.Preview.IsBusy
            && main.Filmstrip.Items.Count == 3 && main.Filmstrip.Items.All(item => item.Bitmap is not null));
        var files = main.Service!.Project.FileNames.Order(StringComparer.Ordinal).ToArray();
        main.OnSelectionChanged([files[0]], files[0]);
        var original = files.ToDictionary(file => file, file => main.Service.SettingsFor(file).Number("basic.exposure"));
        var list = host.View.FindControl<FilmBatch.App.Views.FilmstripView>("PhotoFilmstrip")!.FindControl<ListBox>("List")!;
        host.View.Shell.ToggleSelectionCommand.Execute(null);
        host.Window.CaptureRenderedFrame()?.Dispose();

        Tap(1);
        Tap(2);
        Tap(0);
        Assert.Equal(new[] { files[1], files[2] }, main.Selection.Order(StringComparer.Ordinal));
        host.View.Shell.ToggleSelectionCommand.Execute(null);
        Assert.Equal(new[] { files[1], files[2] }, main.Selection.Order(StringComparer.Ordinal));

        main.Stack.Clear();
        main.Inspector.SetValue("basic.exposure", ParamValue.Of(1.25), "mobile-touch");
        main.Inspector.EndEdit();
        Assert.Equal(original[files[0]], main.Service.SettingsFor(files[0]).Number("basic.exposure"));
        Assert.Equal(1.25, main.Service.SettingsFor(files[1]).Number("basic.exposure"));
        Assert.Equal(1.25, main.Service.SettingsFor(files[2]).Number("basic.exposure"));
        main.UndoCommand.Execute(null);
        Assert.All(files, file => Assert.Equal(original[file], main.Service.SettingsFor(file).Number("basic.exposure")));
        Assert.False(main.Stack.CanUndo);
        main.RedoCommand.Execute(null);
        Assert.Equal(1.25, main.Service.SettingsFor(files[1]).Number("basic.exposure"));
        Assert.Equal(1.25, main.Service.SettingsFor(files[2]).Number("basic.exposure"));

        void Tap(int index)
        {
            var item = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(index));
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), host.Window)!.Value;
            using var touch = host.Window.TouchBegin(point);
            host.Window.TouchEnd(touch, point);
        }
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
        public Window Window => _window!;

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
