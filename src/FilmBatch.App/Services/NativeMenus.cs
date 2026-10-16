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
/// 「關於」與「偏好設定」放在應用程式選單開頭（Avalonia 附加的 Services／Hide／Quit 之前），其餘選單掛在主視窗。
/// Avalonia.Native 把匯出綁定在第一次匯出的根 <see cref="NativeMenu"/>：主視窗換根會丟例外、應用程式換根不會生效，
/// 所以兩個根都只設定一次，切換語言時只重建根之下的項目（原生子選單的標題只在建立時讀取）。
/// 手勢以 <see cref="KeyGesture"/> 直接建立（Meta = ⌘）。其他平台安裝後無效果，行為與視窗內選單一致。
/// </summary>
public static class NativeMenus
{
    /// <summary>把原生選單裝到應用程式與主視窗。macOS 以外平台忽略（屬性存在但不顯示）。</summary>
    public static void Install(Application app, MainWindow window, MainWindowViewModel vm)
    {
        if (!OperatingSystem.IsMacOS()) return;
        // 應用程式選單的根宣告在 App.axaml：Avalonia 在初始化完成前就匯出它，並在後面附加系統項目
        var appMenu = NativeMenu.GetMenu(app) ?? throw new InvalidOperationException("App.axaml must declare NativeMenu.Menu.");
        var windowMenu = new NativeMenu();
        NativeMenuItemBase[] appItems = [];
        Fill();
        NativeMenu.SetMenu(window, windowMenu);
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture)) Fill();
        };
        return;

        void Fill()
        {
            foreach (var item in appItems) appMenu.Items.Remove(item);
            appItems = CreateAppItems(vm);
            for (var i = 0; i < appItems.Length; i++) appMenu.Items.Insert(i, appItems[i]);

            windowMenu.Items.Clear();
            FillWindowMenu(windowMenu, window, vm);
        }
    }

    /// <summary>應用程式選單（選單列最左、顯示 App 名稱的一欄）開頭的項目：About、偏好設定（⌘,）。</summary>
    private static NativeMenuItemBase[] CreateAppItems(MainWindowViewModel vm) =>
    [
        Item(Loc.Get("Cmd.About"), vm.AboutCommand),
        new NativeMenuItemSeparator(),
        Item(Loc.Get("Cmd.Preferences"), vm.PreferencesCommand, new KeyGesture(Key.OemComma, KeyModifiers.Meta)),
    ];

    /// <summary>視窗選單：檔案／編輯／檢視／預設／視窗／說明，與視窗內選單同一組命令與手勢。</summary>
    private static void FillWindowMenu(NativeMenu menu, MainWindow window, MainWindowViewModel vm)
    {
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

        // macOS 慣例：「關於」在應用程式選單，說明選單只放外部連結
        var help = new NativeMenu();
        help.Items.Add(Item(Loc.Get("Cmd.OpenGitHub"), vm.OpenGitHubCommand));
        help.Items.Add(Item(Loc.Get("Cmd.ReportIssue"), vm.ReportIssueCommand));
        menu.Items.Add(SubMenu(Loc.Get("Menu.Help"), help));
    }

    private static KeyGesture Meta(Key key) => new(key, KeyModifiers.Meta);
    private static KeyGesture MetaShift(Key key) => new(key, KeyModifiers.Meta | KeyModifiers.Shift);

    private static NativeMenuItem SubMenu(string header, NativeMenu items) =>
        new() { Header = header, Menu = items };

    // NativeMenuItem 以弱事件追蹤 CanExecuteChanged 並同步 IsEnabled（原生選單開啟時讀取），重建後舊項目可被回收
    private static NativeMenuItem Item(string header, ICommand command, KeyGesture? gesture = null) =>
        new() { Header = header, Command = command, Gesture = gesture };

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
