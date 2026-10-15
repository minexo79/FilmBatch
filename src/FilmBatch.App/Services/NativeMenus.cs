using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FilmBatch.App.Localization;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;

namespace FilmBatch.App.Services;

/// <summary>
/// macOS 原生選單列（計畫書 2.4.1「macOS 另提供原生選單列 NativeMenu」）：
/// 應用程式選單掛在 <see cref="Application"/>（About／Preferences 加在 Avalonia 自動附加的
/// Hide／Quit 之前），其餘選單掛在主視窗。其他平台安裝後無效果，行為與視窗內選單一致。
/// 手勢以 <see cref="KeyGesture"/> 直接建立（Meta = ⌘），語言切換時由呼叫端重建整棵選單。
/// </summary>
public static class NativeMenus
{
    /// <summary>把原生選單裝到應用程式與主視窗。macOS 以外平台忽略（屬性存在但不顯示）。</summary>
    public static void Install(Application app, MainWindow window, MainWindowViewModel vm)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Apply();
        // 切換語言時原生選單需整棵重建（原生 NSMenuItem 只讀取建立當下的字串）
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture)) Apply();
        };
        return;

        void Apply()
        {
            NativeMenu.SetMenu(app, CreateAppMenu(window, vm));
            NativeMenu.SetMenu(window, CreateWindowMenu(window, vm));
        }
    }

    /// <summary>應用程式選單（選單列最左、顯示 App 名稱的一欄）：About、偏好設定（⌘,）。</summary>
    internal static NativeMenu CreateAppMenu(MainWindow window, MainWindowViewModel vm)
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item(Loc.Format("Menu.AboutApp", Loc.Get("App.Title")),
            new CommandAdapter(() => ShowAbout(window))));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(Loc.Get("Cmd.Preferences"), vm.PreferencesCommand,
            new KeyGesture(Key.OemComma, KeyModifiers.Meta)));
        return menu;
    }

    /// <summary>視窗選單：檔案／編輯／檢視／視窗／預設，與視窗內選單同一組命令與手勢。</summary>
    internal static NativeMenu CreateWindowMenu(MainWindow window, MainWindowViewModel vm)
    {
        var menu = new NativeMenu();

        var file = new NativeMenu();
        file.Items.Add(Item(Loc.Get("Cmd.OpenFolder"), vm.OpenFolderCommand, Meta(Key.O)));
        file.Items.Add(Item(Loc.Get("Cmd.OpenSamples"), vm.OpenSamplesCommand));
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Item(Loc.Get("Cmd.Export"), vm.ExportCommand, Meta(Key.E)));
        file.Items.Add(new NativeMenuItemSeparator());
        // macOS 慣例：偏好設定在應用程式選單；關閉視窗取代「結束」（Quit 由系統自動附加在 App 選單）
        file.Items.Add(Item(Loc.Get("Cmd.CloseWindow"), new CommandAdapter(window.Close), Meta(Key.W)));
        menu.Items.Add(SubMenu(Loc.Get("Menu.File"), file));

        var edit = new NativeMenu();
        edit.Items.Add(Item(Loc.Get("Cmd.Undo"), vm.UndoCommand, Meta(Key.Z)));
        edit.Items.Add(Item(Loc.Get("Cmd.Redo"), vm.RedoCommand, MetaShift(Key.Z)));
        edit.Items.Add(new NativeMenuItemSeparator());
        edit.Items.Add(Item(Loc.Get("Cmd.Copy"), vm.CopyCommand, Meta(Key.C)));
        edit.Items.Add(Item(Loc.Get("Cmd.Paste"), vm.PasteCommand, Meta(Key.V)));
        edit.Items.Add(Item(Loc.Get("Cmd.PastePresetOnly"), vm.PastePresetOnlyCommand));
        edit.Items.Add(Item(Loc.Get("Cmd.PasteSpecial"), vm.PasteSpecialCommand, MetaShift(Key.V)));
        edit.Items.Add(new NativeMenuItemSeparator());
        edit.Items.Add(Item(Loc.Get("Cmd.SelectAll"), vm.SelectAllCommand, Meta(Key.A)));
        menu.Items.Add(SubMenu(Loc.Get("Menu.Edit"), edit));

        var view = new NativeMenu();
        view.Items.Add(Item(Loc.Get("Cmd.SplitCompare"), vm.ToggleSplitCommand));
        view.Items.Add(Item(Loc.Get("Cmd.ZoomFit"), vm.ZoomFitCommand, Meta(Key.D0)));
        view.Items.Add(Item(Loc.Get("Cmd.Zoom100"), vm.Zoom100Command, Meta(Key.D1)));
        view.Items.Add(new NativeMenuItemSeparator());
        view.Items.Add(Item(Loc.Get("Cmd.TogglePanels"), vm.TogglePanelsCommand));
        view.Items.Add(Item(Loc.Get("Cmd.FullScreen"), vm.ToggleFullScreenCommand));
        view.Items.Add(Item(Loc.Get("Cmd.ResetLayout"), vm.ResetLayoutCommand));
        view.Items.Add(new NativeMenuItemSeparator());
        view.Items.Add(Item(Loc.Get("Cmd.Gallery"), vm.ShowGalleryCommand));
        menu.Items.Add(SubMenu(Loc.Get("Menu.View"), view));

        var presets = new NativeMenu();
        presets.Items.Add(Item(Loc.Get("Cmd.CreatePreset"), vm.CreatePresetCommand));
        presets.Items.Add(Item(Loc.Get("Cmd.ManagePresets"), vm.ManagePresetsCommand));
        menu.Items.Add(SubMenu(Loc.Get("Menu.Presets"), presets));

        var win = new NativeMenu();
        win.Items.Add(Item(Loc.Get("Cmd.Minimize"), new CommandAdapter(() => window.WindowState = WindowState.Minimized), Meta(Key.M)));
        win.Items.Add(Item(Loc.Get("Cmd.FullScreen"), vm.ToggleFullScreenCommand, new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control)));
        menu.Items.Add(SubMenu(Loc.Get("Menu.Window"), win));

        return menu;
    }

    private static KeyGesture Meta(Key key) => new(key, KeyModifiers.Meta);
    private static KeyGesture MetaShift(Key key) => new(key, KeyModifiers.Meta | KeyModifiers.Shift);

    private static NativeMenuItem SubMenu(string header, NativeMenu items) =>
        new() { Header = header, Menu = items };

    private static NativeMenuItem Item(string header, ICommand command, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem
        {
            Header = header,
            Command = command,
            Gesture = gesture,
            // 明確同步 CanExecute：原生 NSMenuItem 的啟用狀態在開啟選單時讀取
            IsEnabled = command.CanExecute(null),
        };
        command.CanExecuteChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => item.IsEnabled = command.CanExecute(null));
        return item;
    }

    /// <summary>簡單的「關於」視窗（原生應用程式選單第一項），避免新增對話框服務介面。</summary>
    private static void ShowAbout(Window owner)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        var w = new Window
        {
            Title = Loc.Format("Menu.AboutApp", Loc.Get("App.Title")),
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.Get("App.Title"),
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = $"{Loc.Get("Menu.Version")} {version}",
                        Classes = { "secondary" },
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = Loc.Get("Menu.AboutText"),
                        Classes = { "secondary" },
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            },
        };
        _ = w.ShowDialog(owner);
    }

    /// <summary>把同步動作包成 <see cref="ICommand"/>，讓原生選單項目可共用啟用狀態邏輯。</summary>
    private sealed class CommandAdapter : ICommand
    {
        private readonly Func<bool>? _canExecute;
        private readonly Action _execute;

        public CommandAdapter(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter)) _execute();
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
