using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.Core;

namespace FilmBatch.Mobile;

/// <summary>Hosts the shared FilmBatch editor with native imports and adaptive mobile layout.</summary>
public sealed partial class MobileEditorView : UserControl, IDisposable
{
    private readonly MobileWorkspace _workspace;
    private readonly MobileDialogService _dialogs;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _pickPhotos;
    private CancellationTokenSource? _ioCancellation;
    private bool _disposed;
    private bool _suspended;
    private bool? _wideLayout;
    private Avalonia.Controls.Platform.IInsetsManager? _insetsManager;

    /// <summary>Initializes a new instance of the <see cref="MobileEditorView"/> class.</summary>
    public MobileEditorView() : this(new MobileWorkspace(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FilmBatch"))) { }

    /// <summary>Initializes a new instance with an owned workspace and the platform's selected-Photos picker.</summary>
    public MobileEditorView(MobileWorkspace workspace,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? pickPhotos = null)
    {
        InitializeComponent();
        _workspace = workspace;
        _pickPhotos = pickPhotos;
        _dialogs = new MobileDialogService(() => TopLevel.GetTopLevel(this), ModalOverlay, workspace);
        var settings = AppSettings.Load();
        settings.ForceSoftwareGpu = true;
        Loc.Instance.Culture = CultureInfo.GetCultureInfo(settings.EffectiveLanguage);
        Main = new MainWindowViewModel(settings, _dialogs, () => PreviewService.CreateCpuRenderer(settings),
            action => Dispatcher.UIThread.Post(action));
        DataContext = Main;
        Main.ClipboardJsonChanged += OnClipboardChanged;
        Main.PropertyChanged += OnMainPropertyChanged;
        SizeChanged += (_, _) => ApplyLayout();
        AttachedToVisualTree += async (_, _) =>
        {
            _insetsManager = TopLevel.GetTopLevel(this)?.InsetsManager;
            if (_insetsManager is not null)
            {
                SafeRoot.Margin = _insetsManager.SafeAreaPadding;
                _insetsManager.SafeAreaChanged += OnSafeAreaChanged;
            }
            Resume();
            if (Main.Service is null && workspace.IsProject(settings.LastFolder))
                await RunIoAsync(async token => { token.ThrowIfCancellationRequested(); await Main.OpenFolderAsync(settings.LastFolder!); });
            ApplyLayout();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_insetsManager is not null) _insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
            _insetsManager = null;
            Suspend();
        };
        Loc.OnCultureChanged(this, static view => view.RefreshLabels());
        RefreshLabels();
    }

    private void OnSafeAreaChanged(object? sender, EventArgs args)
    {
        if (_insetsManager is not null) SafeRoot.Margin = _insetsManager.SafeAreaPadding;
        ApplyLayout();
    }

    /// <summary>Gets the canonical shared editor ViewModel.</summary>
    public MainWindowViewModel Main { get; }
    /// <summary>Gets the application-owned import, project and output storage.</summary>
    public MobileWorkspace Workspace => _workspace;

    private void RefreshLabels()
    {
        ImportFilesButton.Content = MobileText.T("Files 匯入", "Files");
        ImportPhotosButton.Content = MobileText.T("照片", "Photos");
        ImportPhotosButton.IsVisible = _pickPhotos is not null;
        ProjectsButton.Content = MobileText.T("專案", "Projects");
        MoreButton.Content = MobileText.T("更多", "More");
        SaveOutputsButton.Content = MobileText.T("另存輸出至 Files", "Save outputs to Files");
        ExportLocationNote.Text = MobileText.T("先輸出至此 App 的 Exports；再另存至 Files。原始照片不會被修改。",
            "Outputs stay in this app's Exports until copied to Files. Original photographs are never modified.");
    }

    private void ApplyLayout()
    {
        var wide = Bounds.Width - SafeRoot.Margin.Left - SafeRoot.Margin.Right >= 820;
        if (_wideLayout == wide) return;
        _wideLayout = wide;
        WorkArea.ColumnDefinitions = ColumnDefinitions.Parse(wide ? "*,360" : "*");
        WorkArea.RowDefinitions = RowDefinitions.Parse(wide ? "*" : "2*,3*");
        Grid.SetColumn(EditingPanel, wide ? 1 : 0);
        Grid.SetRow(EditingPanel, wide ? 0 : 1);
        PhotoFilmstrip.Height = wide ? 166 : 194;
    }

    private void OnPanelChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel main || PanelSelector.SelectedIndex < 0) return;
        main.Sidebar.Active = (SidePanel)PanelSelector.SelectedIndex;
        if (main.Sidebar.IsExport) SetExportDestination();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.Folder)) SetExportDestination();
    }

    private void SetExportDestination()
    {
        var name = Main.Folder is null ? "General" : Path.GetFileName(Main.Folder);
        var destination = Path.Combine(_workspace.Exports, name);
        Directory.CreateDirectory(destination);
        Main.Sidebar.Export.Destination = destination;
        Main.Sidebar.Export.Refresh();
    }

    private async void OnImportFiles(object? sender, RoutedEventArgs args) => await RunIoAsync(async token =>
    {
        var level = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("No active application surface.");
        var files = await level.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = MobileText.T("匯入照片至新專案（保留原始檔）", "Import photographs into a new project (keep originals)"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Photographs / RAW")
            {
                Patterns = FilmBatch.Core.Projects.Project.SupportedExtensions.Select(extension => "*" + extension).ToArray(),
                AppleUniformTypeIdentifiers = ["public.image", "public.camera-raw-image"],
            }],
        });
        token.ThrowIfCancellationRequested();
        if (files.Count == 0) return;
        Main.SaveProject();
        var folder = _workspace.CreateProject();
        var imported = await _workspace.ImportAsync(files, folder, token);
        token.ThrowIfCancellationRequested();
        await Main.OpenFolderAsync(folder);
        MobileStatus.Text = MobileText.T("已匯入 ", "Imported ") + imported.Count + MobileText.T(" 張副本。", " copies.");
    });

    private async void OnImportPhotos(object? sender, RoutedEventArgs args) => await RunIoAsync(async token =>
    {
        if (_pickPhotos is null) return;
        var paths = await _pickPhotos(_workspace.Incoming, token);
        token.ThrowIfCancellationRequested();
        if (paths.Count == 0) return;
        Main.SaveProject();
        var folder = _workspace.CreateProject();
        var imported = await _workspace.ImportIncomingAsync(paths, folder, token);
        token.ThrowIfCancellationRequested();
        await Main.OpenFolderAsync(folder);
        MobileStatus.Text = MobileText.T("已匯入 ", "Imported ") + imported.Count + MobileText.T(" 張 Photos 副本。", " Photos copies.");
    });

    private async void OnProjects(object? sender, RoutedEventArgs args) => await RunIoAsync(async token =>
    {
        var project = await _dialogs.ChooseProjectAsync();
        token.ThrowIfCancellationRequested();
        if (project is null) return;
        Main.SaveProject();
        await Main.OpenFolderAsync(project.Folder);
        MobileStatus.Text = MobileText.T("已開啟專案：", "Opened project: ") + project.Name;
    });

    private async void OnApplyPreset(object? sender, RoutedEventArgs args)
    {
        if (Main.Presets.SelectedPreset is { } preset)
            await Main.AssignPresetAsync(Main.Selection, preset.Id);
    }

    private void OnMore(object? sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        Add(MobileText.T("儲存專案", "Save project"), "Mobile.SaveProject", () =>
        {
            Main.SaveProject(); Main.Settings.Save();
            MobileStatus.Text = MobileText.T("專案已儲存。", "Project saved.");
        });
        AddCommand(Loc.Get("Cmd.SelectAll"), "Mobile.SelectAll", Main.SelectAllCommand);
        AddCommand(Loc.Get("Cmd.PasteSpecial"), "Mobile.PasteSpecial", Main.PasteSpecialCommand);
        AddCommand(Loc.Get("Cmd.CreatePreset"), "Mobile.CreatePreset", Main.CreatePresetCommand);
        AddCommand(Loc.Get("Presets.Manage"), "Mobile.ManagePresets", Main.ManagePresetsCommand);
        AddCommand(Loc.Get("Cmd.Preferences"), "Mobile.Preferences", Main.PreferencesCommand);
        AddCommand(MobileText.T("停止背景載入", "Stop background loading"), "Mobile.StopLoading", Main.CancelPhotoLoadingCommand);
        Add(Loc.Get("Panel.Export"), "Mobile.ExportPanel", () => PanelSelector.SelectedIndex = (int)SidePanel.Export);
        Add(MobileText.T("另存輸出至 Files", "Save outputs to Files"), "Mobile.SaveFilesMenu", () => OnSaveOutputs(this, new RoutedEventArgs()));
        Add(MobileText.T("關於 FilmBatch", "About FilmBatch"), "Mobile.About", () => Main.AboutCommand.Execute(null));
        menu.ShowAt(MoreButton);
        void Add(string label, string id, Action action)
        {
            var item = new MenuItem { Header = label, MinHeight = 44 };
            Avalonia.Automation.AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        void AddCommand(string label, string id, ICommand command)
        {
            var item = new MenuItem { Header = label, Command = command, MinHeight = 44 };
            Avalonia.Automation.AutomationProperties.SetAutomationId(item, id);
            menu.Items.Add(item);
        }
    }

    private async void OnSaveOutputs(object? sender, RoutedEventArgs args) => await RunIoAsync(async token =>
    {
        if (Main.Sidebar.Export.IsExporting) throw new InvalidOperationException(MobileText.T("請先等候匯出完成。", "Wait for the export to finish first."));
        var files = Directory.EnumerateFiles(_workspace.Exports, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith('.') && Path.GetExtension(path) is not ".tmp" and not ".json").ToArray();
        if (files.Length == 0) throw new InvalidOperationException(MobileText.T("尚無輸出檔案。", "There are no completed output files."));
        var level = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("No active application surface.");
        var folders = await level.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = MobileText.T("將輸出副本儲存至 Files", "Save output copies to Files"), AllowMultiple = false,
        });
        token.ThrowIfCancellationRequested();
        if (folders.Count == 0) return;
        var progress = new Progress<int>(done =>
        {
            if (_ioCancellation?.Token == token && !token.IsCancellationRequested)
                MobileStatus.Text = $"{MobileText.T("另存輸出", "Saving outputs")} {done}/{files.Length}";
        });
        await _workspace.SaveOutputsAsync(folders[0], files, progress, token);
        MobileStatus.Text = MobileText.T("已另存輸出副本：", "Saved output copies: ") + files.Length;
    });

    private async Task RunIoAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || _suspended || _ioCancellation is not null) return;
        if (Main.Sidebar.Export.IsExporting)
        {
            MobileStatus.Text = MobileText.T("請先等候匯出完成，或取消匯出。", "Wait for the export to finish, or cancel it first.");
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _ioCancellation = cancellation;
        try
        {
            var working = MobileText.T("處理中…", "Working…");
            MobileStatus.Text = working;
            await action(cancellation.Token);
            if (MobileStatus.Text == working) MobileStatus.Text = null;
        }
        catch (OperationCanceledException) { MobileStatus.Text = MobileText.T("已取消；已完成的副本仍保留在此 App。", "Cancelled; completed copies remain in this app."); }
        catch (Exception error)
        {
            AppLog.Error("Mobile file operation failed", error);
            MobileStatus.Text = MobileText.T("操作失敗：", "Operation failed: ") + error.Message;
        }
        finally { if (ReferenceEquals(_ioCancellation, cancellation)) _ioCancellation = null; }
    }

    private async void OnClipboardChanged(object? sender, string json)
    {
        try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(json); }
        catch (Exception error) { Main.Toast.Show(error.Message); }
    }

    /// <summary>Cancels outstanding work, drops stale results and saves the current valid project.</summary>
    public void Suspend()
    {
        if (_disposed || _suspended) return;
        _suspended = true;
        _ioCancellation?.Cancel();
        Main.Suspend();
        _dialogs.CancelPending();
        PhotoPreview.CancelInteraction();
        Main.Settings.Save();
    }

    /// <summary>Restarts pending work without reopening the project or clearing edit history.</summary>
    public void Resume()
    {
        if (_disposed || !_suspended) return;
        _suspended = false;
        Main.Resume();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        Suspend();
        _disposed = true;
        if (_insetsManager is not null) _insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
        _insetsManager = null;
        Main.ClipboardJsonChanged -= OnClipboardChanged;
        Main.PropertyChanged -= OnMainPropertyChanged;
        Main.Dispose();
    }
}
