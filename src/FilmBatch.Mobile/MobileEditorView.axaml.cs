using System.Globalization;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.Core;

namespace FilmBatch.Mobile;

/// <summary>Hosts the shared FilmBatch editor with native imports and adaptive mobile layout.</summary>
public sealed partial class MobileEditorView : UserControl, IDisposable
{
    /// <summary>Navigation state observed by the mobile view, separate from persisted image settings.</summary>
    public static readonly StyledProperty<MobileShellViewModel> ShellProperty =
        AvaloniaProperty.Register<MobileEditorView, MobileShellViewModel>(nameof(Shell));

    public MobileShellViewModel Shell { get => GetValue(ShellProperty); private set => SetValue(ShellProperty, value); }

    private readonly MobileWorkspace _workspace;
    private readonly MobileDialogService _dialogs;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _pickPhotos;
    private CancellationTokenSource? _ioCancellation;
    private bool _disposed;
    private bool _suspended;
    private bool? _wideLayout;
    private TopLevel? _keyboardRoot;
    private IInsetsManager? _insetsManager;
    private IInputPane? _inputPane;
    private Rect _keyboardRect;
    private bool _applyingLayout;

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
        Shell = new MobileShellViewModel(Main);
        Shell.PropertyChanged += OnShellPropertyChanged;
        DataContext = Main;
        Main.ClipboardJsonChanged += OnClipboardChanged;
        Main.PropertyChanged += OnMainPropertyChanged;
        Main.Sidebar.PropertyChanged += OnSidebarPropertyChanged;
        WorkArea.SizeChanged += (_, _) => ApplyLayout();
        AddHandler(GotFocusEvent, (_, _) => Dispatcher.UIThread.Post(BringFocusedInputIntoView, DispatcherPriority.Loaded));
        SizeChanged += (_, _) => UpdateInsets();
        AttachedToVisualTree += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            _keyboardRoot = top;
            _keyboardRoot?.AddHandler(KeyDownEvent, OnKeyDown);
            _insetsManager = top?.InsetsManager;
            if (_insetsManager is not null) _insetsManager.SafeAreaChanged += OnSafeAreaChanged;
            _inputPane = top?.InputPane;
            if (_inputPane is not null)
            {
                _inputPane.StateChanged += OnInputPaneChanged;
                _keyboardRect = _inputPane.State == InputPaneState.Open ? _inputPane.OccludedRect : default;
            }
            UpdateInsets();
            Resume();
            if (Main.Service is null && workspace.IsProject(settings.LastFolder))
                await RunIoAsync(async token => { token.ThrowIfCancellationRequested(); await Main.OpenFolderAsync(settings.LastFolder!); });
            ApplyLayout();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            DetachPlatformEvents();
            Suspend();
        };
        Loc.OnCultureChanged(this, static view => view.RefreshLabels());
        RefreshLabels();
    }

    private void OnSafeAreaChanged(object? sender, EventArgs args) => UpdateInsets();

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs args)
    {
        _keyboardRect = args.NewState == InputPaneState.Open ? args.EndRect : default;
        UpdateInsets();
        Dispatcher.UIThread.Post(BringFocusedInputIntoView, DispatcherPriority.Loaded);
    }

    private void UpdateInsets()
    {
        var safe = _insetsManager?.SafeAreaPadding ?? default;
        var top = TopLevel.GetTopLevel(this);
        var origin = top is null ? default : this.TranslatePoint(default, top) ?? default;
        var bottom = origin.Y + Bounds.Height;
        var docked = _keyboardRect.Height > 0
            && _keyboardRect.X <= origin.X + 1 && _keyboardRect.Right >= origin.X + Bounds.Width - 1
            && _keyboardRect.Bottom >= bottom - 1 && _keyboardRect.Y < bottom;
        Shell.IsKeyboardOpen = docked;
        SafeRoot.Margin = new Thickness(safe.Left, safe.Top, safe.Right,
            docked ? Math.Max(safe.Bottom, bottom - _keyboardRect.Y) : safe.Bottom);
        var focused = top?.FocusManager?.GetFocusedElement() as Control;
        DismissKeyboardButton.IsVisible = docked || focused is TextBox || focused?.FindAncestorOfType<TextBox>() is not null;
        ApplyLayout();
    }

    private void BringFocusedInputIntoView()
    {
        UpdateInsets();
        if (Shell.IsKeyboardOpen && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused)
            focused.BringIntoView();
    }

    private void DetachPlatformEvents()
    {
        _keyboardRoot?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _keyboardRoot = null;
        if (_insetsManager is not null) _insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
        if (_inputPane is not null) _inputPane.StateChanged -= OnInputPaneChanged;
        _insetsManager = null;
        _inputPane = null;
        _keyboardRect = default;
    }

    private void OnDismissKeyboard(object? sender, RoutedEventArgs args)
    {
        Control? target = ModalOverlay.IsVisible
            ? ModalOverlay.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.IsEffectivelyEnabled && button.IsEffectivelyVisible && button.Focusable)
            : this;
        target?.Focus();
    }

    /// <summary>Gets the canonical shared editor ViewModel.</summary>
    public MainWindowViewModel Main { get; }
    /// <summary>Gets the application-owned import, project and output storage.</summary>
    public MobileWorkspace Workspace => _workspace;

    private void RefreshLabels()
    {
        ImportPhotosButton.IsVisible = _pickPhotos is not null;
        Shell.RefreshLabels();
    }

    private void ApplyLayout()
    {
        if (_applyingLayout || Shell is null || WorkArea.Bounds.Height <= 0) return;
        _applyingLayout = true;
        try
        {
            var width = SafeRoot.Bounds.Width;
            var height = WorkArea.Bounds.Height;
            var wide = width >= 820 || width >= 700 && width > height * 1.25;
            if (_wideLayout != wide)
            {
                var initial = _wideLayout is null;
                _wideLayout = wide;
                WorkArea.ColumnDefinitions = ColumnDefinitions.Parse(wide ? "*,320,72" : "*,0,0");
                WorkArea.RowDefinitions = RowDefinitions.Parse(wide ? "*,0,Auto,0" : "*,0,Auto,Auto");
                Grid.SetColumn(EditingPanel, wide ? 1 : 0);
                Grid.SetRow(EditingPanel, wide ? 0 : 1);
                Grid.SetColumnSpan(EditingPanel, wide ? 1 : 3);
                Grid.SetRowSpan(EditingPanel, wide ? 3 : 1);
                Grid.SetColumnSpan(PreviewStage, wide ? 1 : 3);
                Grid.SetRowSpan(PreviewStage, wide ? 2 : 1);
                Grid.SetColumnSpan(PhotoRailArea, wide ? 1 : 3);
                Grid.SetColumn(ToolNavigation, wide ? 2 : 0);
                Grid.SetRow(ToolNavigation, wide ? 0 : 3);
                Grid.SetColumnSpan(ToolNavigation, wide ? 1 : 3);
                Grid.SetRowSpan(ToolNavigation, wide ? 3 : 1);
                ToolButtons.Columns = wide ? 1 : 5;
                ToolButtons.Rows = wide ? 5 : 1;
                ToolButtons.VerticalAlignment = wide ? VerticalAlignment.Top : VerticalAlignment.Stretch;
                Shell.IsWide = wide;
                if (initial && Main.HasProject) Shell.IsToolPanelOpen = wide;
            }
            var tools = Main.HasSelection && Shell.IsToolPanelOpen;
            WorkArea.ColumnDefinitions[1].Width = new GridLength(wide && tools ? 320 : 0);
            // When typing on a phone, the bounded form gets the available viewport rather than sitting below the keyboard.
            PreviewStage.IsVisible = !Shell.IsKeyboardOpen || !tools || wide;
            WorkArea.RowDefinitions[1].Height = !wide && tools
                ? new GridLength(Shell.IsKeyboardOpen ? height : Math.Min(360, height * 0.45))
                : new GridLength(0);
        }
        finally { _applyingLayout = false; }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args) => ApplyLayout();

    private void OnSidebarPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SidebarViewModel.Active) && Main.Sidebar.IsExport) SetExportDestination();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.Folder))
        {
            SetExportDestination();
            Shell.IsSelectingPhotos = false;
            Shell.IsToolPanelOpen = Shell.IsWide && Main.HasProject;
            Shell.IsPhotoTrayOpen = true;
        }
        if (args.PropertyName is nameof(MainWindowViewModel.HasProject) or nameof(MainWindowViewModel.HasSelection))
            ApplyLayout();
    }

    private void SetExportDestination()
    {
        var name = Main.Folder is null ? "General" : Path.GetFileName(Main.Folder);
        var destination = Path.Combine(_workspace.Exports, name);
        Directory.CreateDirectory(destination);
        Main.Sidebar.Export.Destination = destination;
        Main.Sidebar.Export.Refresh();
    }

    private void OnImport(object? sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        var files = new MenuItem { Header = Loc.Get("Mobile.ImportFiles"), MinHeight = 44 };
        Avalonia.Automation.AutomationProperties.SetAutomationId(files, "Mobile.ImportMenu.Files");
        files.Click += OnImportFiles;
        menu.Items.Add(files);
        if (_pickPhotos is not null)
        {
            var photos = new MenuItem { Header = Loc.Get("Mobile.ImportPhotos"), MinHeight = 44 };
            Avalonia.Automation.AutomationProperties.SetAutomationId(photos, "Mobile.ImportMenu.Photos");
            photos.Click += OnImportPhotos;
            menu.Items.Add(photos);
        }
        menu.ShowAt(ImportButton);
    }

    private async void OnFilters(object? sender, RoutedEventArgs args) =>
        await _dialogs.ShowPhotoFiltersAsync(Main.Filmstrip);

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (ModalOverlay.IsVisible) return;
        var escape = args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None;
        if (escape && (Main.BackgroundRemoval.IsBusy || Main.BackgroundRemoval.IsEditing))
        {
            Main.BackgroundRemoval.CancelCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (escape && (Main.PhotoTools.IsSampling || Main.PhotoTools.IsPicking))
        {
            Main.PhotoTools.CancelCommand.Execute(null);
            args.Handled = true;
            return;
        }
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        if (focused is TextBox || focused?.FindAncestorOfType<TextBox>() is not null) return;
        if (escape)
        {
            Focus();
            Main.Preview.ShowOriginal = false;
            Main.CropTool.CancelCommand.Execute(null);
            Shell.CloseToolsCommand.Execute(null);
            args.Handled = true;
            return;
        }
        var modifiers = args.KeyModifiers & ~KeyModifiers.Shift;
        if (modifiers is not (KeyModifiers.Meta or KeyModifiers.Control)) return;
        ICommand? command = args.Key switch
        {
            Key.Z => args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Main.RedoCommand : Main.UndoCommand,
            Key.C => Main.CopyCommand,
            Key.V => Main.PasteCommand,
            Key.A => Main.SelectAllCommand,
            Key.D0 => Main.ZoomFitCommand,
            Key.D1 => Main.Zoom100Command,
            _ => null,
        };
        if (args.Key == Key.S)
        {
            Main.SaveProject();
            Main.Settings.Save();
            Main.Toast.Show(Loc.Get("Mobile.ProjectSaved"));
            args.Handled = true;
        }
        else if (command?.CanExecute(null) == true) { command.Execute(null); args.Handled = true; }
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
        Add(Loc.Get("Mobile.SaveProject"), "Mobile.SaveProject", () =>
        {
            Main.SaveProject(); Main.Settings.Save();
            MobileStatus.Text = Loc.Get("Mobile.ProjectSaved");
        });
        AddCommand(Loc.Get("Cmd.SelectAll"), "Mobile.SelectAll", Main.SelectAllCommand);
        AddCommand(Loc.Get("Cmd.Copy"), "Mobile.More.Copy", Main.CopyCommand);
        AddCommand(Loc.Get("Cmd.Paste"), "Mobile.More.Paste", Main.PasteCommand);
        AddCommand(Loc.Get("Cmd.ResetSelected"), "Mobile.Reset", Main.ResetPhotoCommand);
        Add(Loc.Get("Mobile.Filters"), "Mobile.More.Filters", () => OnFilters(this, new RoutedEventArgs()));
        AddCommand(Loc.Get("Cmd.PasteSpecial"), "Mobile.PasteSpecial", Main.PasteSpecialCommand);
        AddCommand(Loc.Get("Cmd.CreatePreset"), "Mobile.CreatePreset", Main.CreatePresetCommand);
        AddCommand(Loc.Get("Presets.Manage"), "Mobile.ManagePresets", Main.ManagePresetsCommand);
        AddCommand(Loc.Get("Cmd.Preferences"), "Mobile.Preferences", Main.PreferencesCommand);
        AddCommand(Loc.Get("Status.CancelLoading"), "Mobile.StopLoading", Main.CancelPhotoLoadingCommand);
        Add(Loc.Get("Panel.Export"), "Mobile.ExportPanel", () => Shell.SelectToolCommand.Execute(SidePanel.Export));
        Add(Loc.Get("Mobile.Export.CopyFiles"), "Mobile.SaveFilesMenu", () => OnSaveOutputs(this, new RoutedEventArgs()));
        Add(Loc.Get("Cmd.About"), "Mobile.About", () => Main.AboutCommand.Execute(null));
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
        Main.Preview.ShowOriginal = false;
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
        DetachPlatformEvents();
        Shell.PropertyChanged -= OnShellPropertyChanged;
        Shell.Dispose();
        Main.Sidebar.PropertyChanged -= OnSidebarPropertyChanged;
        Main.ClipboardJsonChanged -= OnClipboardChanged;
        Main.PropertyChanged -= OnMainPropertyChanged;
        Main.Dispose();
    }
}
