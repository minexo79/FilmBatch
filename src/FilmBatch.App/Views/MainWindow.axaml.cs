using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel? _vm;
    private readonly DockFactory? _factory;
    private readonly LayoutService? _layout;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _panelsBeforeFullScreen;
    private bool _syncingFullScreen;

    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;

        // Dock 版面：還原上次的版面（layout.json），失敗時使用預設版面
        _factory = new DockFactory(vm);
        _layout = new LayoutService(_factory);
        DockHost.Factory = _factory;
        DockHost.Layout = _layout.LoadOrDefault();

        vm.ResetLayoutRequested += (_, _) => DockHost.Layout = _layout.CreateDefault();
        vm.ClipboardJsonChanged += async (_, json) =>
        {
            // 同時以 JSON 寫入系統剪貼簿，方便在兩個 FilmBatch 視窗之間貼上（計畫書 2.7.2）
            if (Clipboard is { } cb) await cb.SetTextAsync(json);
        };
        vm.PropertyChanged += OnViewModelPropertyChanged;
        Deactivated += (_, _) =>
        {
            vm.Preview.ShowOriginal = false;
            vm.PhotoTools.CancelStale();
            vm.BackgroundRemoval.CancelCommand.Execute(null);
        };
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            if (DockHost.Layout is { } l) _factory.UpdateTitles(l);
        };

        vm.RecentFolders.CollectionChanged += (_, _) => UpdateRecentFoldersMenu(vm);
        UpdateRecentFoldersMenu(vm);
        ExitItem.Click += (_, _) => Close();
        ConfigureTitleBar();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        RestoreWindowPlacement(vm.Settings);
    }

    /// <summary>
    /// 「開啟...」子選單：把最近資料夾的項目放在「（無）」佔位項之前，
    /// 分隔線與「清除清單」固定在 XAML 中（Avalonia 選單無法混合 ItemsSource 與固定項目）。
    /// </summary>
    private void UpdateRecentFoldersMenu(MainWindowViewModel vm)
    {
        var items = RecentFoldersMenu.Items;
        while (items.Count > 0 && items[0] is MenuItem { Tag: RecentFolderItemViewModel }) items.RemoveAt(0);
        for (var i = 0; i < vm.RecentFolders.Count; i++)
        {
            var recent = vm.RecentFolders[i];
            items.Insert(i, new MenuItem { Header = recent.Header, Command = recent.OpenCommand, Tag = recent });
        }
    }

    /// <summary>
    /// 標題列與選單列合一：系統按鈕仍由平台繪製，這裡只保留其位置。
    /// Windows 的最小化／最大化／關閉在右側；macOS 的紅綠燈在左側。
    /// </summary>
    private void ConfigureTitleBar()
    {
        if (OperatingSystem.IsMacOS())
        {
            TitleIcon.Margin = new Thickness(78, 0, 4, 0);
            CaptionButtonsSpace.Width = 12;
        }
        else if (OperatingSystem.IsLinux())
        {
            CaptionButtonsSpace.Width = 110;
        }
        // 隱藏平台繪製的標題文字（標題改由本視窗自己的標題列顯示），只保留系統按鈕。
        // 繪製的裝飾不在視窗的樣式樹中，因此於開啟後在視覺樹中尋找。
        Opened += (_, _) => Dispatcher.UIThread.Post(HideDrawnTitleText, DispatcherPriority.Loaded);
    }

    private void HideDrawnTitleText()
    {
        Visual root = this;
        while (root.GetVisualParent() is { } parent) root = parent;
        foreach (var v in root.GetVisualDescendants())
            if (v is Control { Name: "PART_TitleTextPanel" } c)
                c.IsVisible = false;
    }

    /// <summary>冒煙測試：目前版面能否序列化後還原（發佈版的 Trimming 檢查）。</summary>
    internal bool VerifyLayoutRoundTrip()
    {
        if (_vm is null || _factory is null) return false;
        var path = Path.Combine(Path.GetTempPath(), $"filmbatch-smoke-layout-{Environment.ProcessId}.json");
        try
        {
            new LayoutService(_factory, path).Save(DockHost.Layout);
            var restored = new LayoutService(new DockFactory(_vm), path).LoadOrDefault();
            return File.Exists(path) && restored.VisibleDockables?.Count > 0;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private void RestoreWindowPlacement(AppSettings s)
    {
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowX is { } x && s.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || _vm is null || _syncingFullScreen) return;
        var fullScreen = WindowState == WindowState.FullScreen;
        if (_vm.IsFullScreen == fullScreen) return;

        _syncingFullScreen = true;
        try
        {
            if (fullScreen)
            {
                _stateBeforeFullScreen = (WindowState)change.OldValue!;
                _panelsBeforeFullScreen = _vm.PanelsHidden;
            }
            _vm.IsFullScreen = fullScreen;
            _vm.PanelsHidden = fullScreen || _panelsBeforeFullScreen;
        }
        finally { _syncingFullScreen = false; }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsFullScreen) || _vm is null || _syncingFullScreen) return;
        _syncingFullScreen = true;
        try
        {
            if (_vm.IsFullScreen)
            {
                _stateBeforeFullScreen = WindowState;
                _panelsBeforeFullScreen = _vm.PanelsHidden;
                WindowState = WindowState.FullScreen;
                _vm.PanelsHidden = true;
            }
            else
            {
                WindowState = _stateBeforeFullScreen;
                _vm.PanelsHidden = _panelsBeforeFullScreen;
            }
        }
        finally { _syncingFullScreen = false; }
    }

    /// <summary>焦點在文字輸入元件時，單鍵快捷鍵與 Ctrl+C/V/Z/A 交給該元件處理。</summary>
    private bool IsTyping() => FocusManager?.GetFocusedElement() is TextBox;

    private KeyModifiers CommandModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var cmd = CommandModifier;
        var mods = e.KeyModifiers;
        var typing = IsTyping();

        // ── 修飾鍵 + 按鍵（macOS 上 Ctrl 自動對應 ⌘） ──
        if (mods == cmd)
        {
            switch (e.Key)
            {
                case Key.O: Run(_vm.OpenFolderCommand); break;
                case Key.E: Run(_vm.ExportCommand); break;
                case Key.D0 or Key.NumPad0: Run(_vm.ZoomFitCommand); break;
                case Key.D1 or Key.NumPad1: Run(_vm.Zoom100Command); break;
                case Key.Z when !typing: Run(_vm.UndoCommand); break;
                case Key.Y when !typing: Run(_vm.RedoCommand); break;
                case Key.C when !typing: Run(_vm.CopyCommand); break;
                case Key.V when !typing: Run(_vm.PasteCommand); break;
                case Key.A when !typing: Run(_vm.SelectAllCommand); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (mods == (cmd | KeyModifiers.Shift))
        {
            switch (e.Key)
            {
                case Key.Z when !typing: Run(_vm.RedoCommand); break;
                case Key.V when !typing: Run(_vm.PasteSpecialCommand); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        // ── 單鍵（輸入文字時不處理）──
        var crop = _vm.CropTool;
        if (e.Key == Key.Escape && mods == KeyModifiers.None && !_vm.IsFullScreen
            && (_vm.BackgroundRemoval.IsBusy || _vm.BackgroundRemoval.IsEditing))
        {
            _vm.BackgroundRemoval.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Esc 在輸入焦點中也要生效：照片工具提案／取樣不允許被文字輸入吞掉。
        if (e.Key == Key.Escape && mods == KeyModifiers.None
            && (_vm.PhotoTools.IsSampling || _vm.PhotoTools.IsPicking) && !_vm.IsFullScreen)
        {
            _vm.PhotoTools.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Shift + 1–9：套用第 N 個釘選預設（單獨的數字鍵留給星級評分）
        if (!typing && mods == KeyModifiers.Shift && DigitOf(e.Key) is >= 1 and var pin)
        {
            _vm.ApplyPinnedCommand.Execute(pin);
            e.Handled = true;
            return;
        }
        if (typing || mods != KeyModifiers.None) return;
        // 0–5（含數字鍵台）：設定選取照片的星級評分，0 清除
        if (DigitOf(e.Key) is >= 0 and <= FilmBatch.Core.Projects.PhotoEntry.MaxRating and var rating)
        {
            if (_vm.SetRatingCommand.CanExecute(rating)) _vm.SetRatingCommand.Execute(rating);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            // 裁切工具（cut-and-mask Task 1.7）：R 進入 / 套用並返回，其餘只在工具啟用時有效
            case Key.R: Run(_vm.ToggleCropCommand); break;
            case Key.Enter when crop.IsActive: Run(crop.ApplyCommand); break;
            case Key.Escape when (crop.IsActive || crop.IsBeginPending) && !_vm.IsFullScreen: Run(crop.CancelCommand); break;
            case Key.X when crop.IsActive: Run(crop.SwapOrientationCommand); break;
            case Key.OemOpenBrackets when crop.IsActive: Run(crop.RotateLeftCommand); break;
            case Key.OemCloseBrackets when crop.IsActive: Run(crop.RotateRightCommand); break;
            case Key.Space: _vm.Preview.ShowOriginal = true; break;
            case Key.Y: Run(_vm.ToggleSplitCommand); break;
            case Key.F: Run(_vm.ToggleFullScreenCommand); break;
            case Key.Tab: Run(_vm.TogglePanelsCommand); break;
            case Key.Escape when _vm.IsFullScreen: _vm.IsFullScreen = false; break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>主鍵盤或數字鍵台的數字 0–9；其他按鍵為 null。</summary>
    private static int? DigitOf(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.Space) return;
        _vm.Preview.ShowOriginal = false;
        e.Handled = !IsTyping();
    }

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_vm is null) return;

        // 先寫回專案（每張照片的預設指定與覆寫）：失敗時取消關閉，避免靜默遺失資料
        try
        {
            _vm.SaveProject();
        }
        catch (Exception ex)
        {
            AppLog.Error("儲存專案失敗", ex);
            _vm.Toast.Show(Loc.Format("Toast.SaveProjectFailed", ex.Message));
            e.Cancel = true;
            return;
        }

        _layout?.Save(DockHost.Layout);
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
        }
        try
        {
            s.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"無法儲存設定：{ex.Message}");
        }
    }
}
