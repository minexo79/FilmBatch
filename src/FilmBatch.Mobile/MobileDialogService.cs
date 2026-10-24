using System.Linq.Expressions;
using Avalonia.Data;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Dialogs;

namespace FilmBatch.Mobile;

/// <summary>Presents real storage pickers and in-view modal editors without desktop windows.</summary>
internal sealed class MobileDialogService(Func<TopLevel?> topLevel, Border overlay, MobileWorkspace workspace) : IDialogService
{
    private Action? _cancelModal;
    private int _generation;
    private CancellationTokenSource _copies = new();

    private TopLevel TopLevel => topLevel() ?? throw new InvalidOperationException("The application view is not attached.");

    internal void CancelPending()
    {
        ++_generation;
        _copies.Cancel();
        _copies.Dispose();
        _copies = new CancellationTokenSource();
        _cancelModal?.Invoke();
    }

    /// <summary>Copies a picked photo folder into a new owned project listed in the Projects chooser.</summary>
    public async Task<string?> PickPhotoFolderAsync(string? startFolder)
    {
        var picked = await PickNativeFolderAsync(MobileText.T("匯入照片資料夾副本", "Import a photo folder copy"));
        if (picked is null) return null;
        // Never return an external virtual path to the shared file-system project model.
        return await workspace.ImportFolderAsync(picked.Value.Folder, picked.Value.Token);
    }

    /// <summary>Copies a picked directory value (for example raw.lens_database) into owned asset storage.</summary>
    public async Task<string?> PickFolderAsync(string? startFolder)
    {
        var picked = await PickNativeFolderAsync(MobileText.T("選擇資料夾副本", "Choose a folder copy"));
        if (picked is null) return null;
        // Directory parameters must stay usable without creating a bogus zero-photo project under Projects/.
        return await workspace.ImportAssetFolderAsync(picked.Value.Folder, picked.Value.Token);
    }

    private async Task<(IStorageFolder Folder, CancellationToken Token)?> PickNativeFolderAsync(string title)
    {
        var generation = _generation;
        var token = _copies.Token;
        var selected = await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title, AllowMultiple = false,
        });
        if (selected.Count == 0) return null;
        if (generation != _generation) { selected[0].Dispose(); return null; }
        return (selected[0], token);
    }

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns)
    {
        var generation = _generation;
        var token = _copies.Token;
        var selected = await TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        });
        if (selected.Count == 0) return null;
        if (generation != _generation) { selected[0].Dispose(); return null; }
        return await workspace.ImportAssetAsync(selected[0], token);
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<string> patterns)
    {
        var name = await PromptAsync(title,
            MobileText.T("檔名（儲存至此 App 的 Exports，之後可另存至 Files）", "Filename (saved in this app's Exports; use Save outputs to Files to copy it out)"), suggestedName);
        return string.IsNullOrWhiteSpace(name) ? null : workspace.NewExportPath(name.Trim());
    }

    public async Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(int photoCount)
    {
        var remember = new CheckBox { Content = Loc.Get("Dialog.Remember") };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(MobileText.Label(Loc.Format("Dialog.AssignMessage", photoCount)));
        body.Children.Add(remember);
        var result = await ShowAsync(Loc.Get("Dialog.AssignTitle"), body,
            [MobileText.T("保留覆寫", "Keep overrides"), MobileText.T("清除覆寫", "Clear overrides"), Loc.Get("Dialog.Cancel")]);
        return result is 0 or 1 ? (result == 0, remember.IsChecked == true) : null;
    }

    public async Task<bool> ShowSelectivePasteAsync(SelectivePasteViewModel vm)
    {
        var body = new StackPanel { Spacing = 10, DataContext = vm };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(CommandButton(Loc.Get("Dialog.SelectAll"), "Mobile.PasteAll", vm.SelectAllCommand));
        actions.Children.Add(CommandButton(MobileText.T("全不選", "Select none"), "Mobile.PasteNone", vm.SelectNoneCommand));
        actions.Children.Add(CommandButton(MobileText.T("僅色彩", "Color only"), "Mobile.PasteColor", vm.ColorOnlyCommand));
        body.Children.Add(actions);
        foreach (var group in vm.Groups)
        {
            body.Children.Add(MobileText.Label(group.Label));
            foreach (var module in group.Modules)
            {
                var check = new CheckBox { Content = module.Label, MinHeight = 44, DataContext = module };
                MobileText.Bind(check, ToggleButton.IsCheckedProperty, (ModuleCheckViewModel m) => m.IsChecked);
                body.Children.Add(check);
            }
        }
        body.Children.Add(Check(MobileText.T("包含隨機種子", "Include random seeds"), (SelectivePasteViewModel m) => m.IncludeSeeds));
        body.Children.Add(Check(MobileText.T("鏡射位置", "Mirror positions"), (SelectivePasteViewModel m) => m.MirrorPositions));
        return await ShowAsync(Loc.Get("Cmd.PasteSpecial"), body, [Loc.Get("Cmd.Paste"), Loc.Get("Dialog.Cancel")]) == 0;
    }

    public async Task ShowPresetManagerAsync(PresetManagerViewModel vm)
    {
        var body = new StackPanel { Spacing = 12, DataContext = vm };
        var rows = new ComboBox { ItemsSource = vm.Rows, HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberBinding = CompiledBinding.Create((PresetRowViewModel row) => row.Name) };
        MobileText.Bind(rows, SelectingItemsControl.SelectedItemProperty, (PresetManagerViewModel m) => m.Selected);
        body.Children.Add(rows);
        var name = new TextBox { PlaceholderText = Loc.Get("Presets.Col.Name") };
        MobileText.Bind(name, TextBox.TextProperty, (PresetManagerViewModel m) => m.Selected!.Name);
        body.Children.Add(name);
        var category = new TextBox { PlaceholderText = Loc.Get("Presets.Col.Category") };
        MobileText.Bind(category, TextBox.TextProperty, (PresetManagerViewModel m) => m.Selected!.Category);
        body.Children.Add(category);
        var colors = new ComboBox { ItemsSource = vm.ColorLabels, HorizontalAlignment = HorizontalAlignment.Stretch };
        MobileText.Bind(colors, SelectingItemsControl.SelectedItemProperty, (PresetManagerViewModel m) => m.Selected!.ColorLabel);
        body.Children.Add(colors);
        var commands = new WrapPanel();
        commands.Children.Add(CommandButton(Loc.Get("Presets.ImportYaml"), "Mobile.PresetImport", vm.ImportCommand));
        commands.Children.Add(CommandButton(Loc.Get("Presets.ExportYaml"), "Mobile.PresetExport", vm.ExportCommand));
        commands.Children.Add(CommandButton(Loc.Get("Presets.Delete"), "Mobile.PresetDelete", vm.DeleteCommand));
        body.Children.Add(commands);
        rows.SelectedItem = vm.Rows.FirstOrDefault();
        var managerResult = await ShowAsync(Loc.Get("Presets.Manage"), body, [Loc.Get("Dialog.Close")]);
        if (managerResult >= 0) vm.Apply();
    }

    public async Task ShowPreferencesAsync(PreferencesViewModel vm)
    {
        var body = new StackPanel { Spacing = 12, DataContext = vm };
        body.Children.Add(MobileText.Label(Loc.Get("Cmd.Language")));
        body.Children.Add(Options(vm.Languages, (PreferencesViewModel m) => m.Language));
        body.Children.Add(MobileText.Label(MobileText.T("預覽長邊", "Preview long edge")));
        body.Children.Add(Options(vm.ProxySizes, (PreferencesViewModel m) => m.ProxySizeOption));
        body.Children.Add(MobileText.Label(MobileText.T("預覽背景", "Preview background")));
        body.Children.Add(Options(vm.Backgrounds, (PreferencesViewModel m) => m.Background));
        body.Children.Add(MobileText.Label(MobileText.T("背景 CPU 目標（%）", "Background CPU target (%)")));
        var cpu = new NumericUpDown { Minimum = 10, Maximum = 100, Increment = 10, MinHeight = 44 };
        MobileText.Bind(cpu, NumericUpDown.ValueProperty, (PreferencesViewModel m) => m.BackgroundCpuPercent);
        body.Children.Add(cpu);
        body.Children.Add(MobileText.Label(MobileText.T("相片處理使用 CPU；此版本未提供 iOS Dawn GPU 後端。", "Photographic processing uses the CPU; an iOS Dawn GPU backend is not included.")));
        var preferencesResult = await ShowAsync(Loc.Get("Cmd.Preferences"), body, [Loc.Get("Dialog.Close")]);
        if (preferencesResult >= 0 && !vm.Apply()) await ConfirmAsync(Loc.Get("Cmd.Preferences"), vm.ApplyError ?? "Apply failed", Loc.Get("Dialog.Close"));
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        await ShowAsync(title, MobileText.Label(message), [confirmText, Loc.Get("Dialog.Cancel")]) == 0;

    public async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var input = new TextBox { Text = initial, MinHeight = 44 };
        Avalonia.Automation.AutomationProperties.SetAutomationId(input, "Mobile.PromptInput");
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(MobileText.Label(label));
        body.Children.Add(input);
        var result = await ShowAsync(title, body, [Loc.Get("Dialog.OK"), Loc.Get("Dialog.Cancel")]);
        return result == 0 ? input.Text : null;
    }

    public void ShowGallery() => _ = ShowAsync("FilmBatch", MobileText.Label(
        MobileText.T("這是行動版照片編輯器；元件展示位於桌面版。", "This is the mobile photo editor; the component gallery belongs to the desktop application.")), [Loc.Get("Dialog.Close")]);

    public async Task ShowAboutAsync(AboutViewModel vm)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(MobileText.Label(vm.Summary));
        var repository = MobileText.Button("GitHub", "Mobile.Repository");
        repository.Click += async (_, _) => await OpenUriAsync(vm.Repository);
        body.Children.Add(repository);
        await ShowAsync("FilmBatch", body, [Loc.Get("Dialog.Close")]);
    }

    public async Task OpenUriAsync(string uri)
    {
        if (!await TopLevel.Launcher.LaunchUriAsync(new Uri(uri))) throw new IOException("Could not open " + uri);
    }

    internal async Task<MobileProject?> ChooseProjectAsync()
    {
        var projects = workspace.ListProjects();
        var list = new ListBox { ItemsSource = projects, MinHeight = 180, MaxHeight = 360 };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(MobileText.Label(MobileText.T("開啟儲存在此 App 的專案", "Open a project stored in this app")));
        body.Children.Add(list);
        var result = await ShowAsync(MobileText.T("專案", "Projects"), body,
            [MobileText.T("開啟", "Open"), Loc.Get("Dialog.Cancel")]);
        return result == 0 ? list.SelectedItem as MobileProject : null;
    }

    private async Task<int> ShowAsync(string title, Control body, IReadOnlyList<string> choices)
    {
        // A confirmation may be opened by a command inside a manager sheet. Preserve that sheet.
        var previousChild = overlay.Child;
        var previousVisible = overlay.IsVisible;
        var previousCancel = _cancelModal;
        var generation = _generation;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var card = new DockPanel { Margin = new Thickness(16), MaxWidth = 620, MaxHeight = 720,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var heading = MobileText.Label(title);
        heading.FontSize = 20;
        heading.Margin = new Thickness(16);
        DockPanel.SetDock(heading, Dock.Top);
        card.Children.Add(heading);
        var actions = new WrapPanel { Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var button = MobileText.Button(choices[i], "Mobile.ModalChoice" + i);
            button.Margin = new Thickness(4);
            button.Click += (_, _) => completion.TrySetResult(index);
            actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Bottom);
        card.Children.Add(actions);
        card.Children.Add(new ScrollViewer { Content = body, Margin = new Thickness(16, 0, 16, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var backdrop = new Border { Child = card, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.Parse("#242428")),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, MaxWidth = 660,
            Margin = new Thickness(12) };
        overlay.Child = backdrop;
        overlay.IsVisible = true;
        _cancelModal = () => completion.TrySetResult(-1);
        try { return await completion.Task; }
        finally
        {
            var cancelled = generation != _generation;
            overlay.Child = cancelled ? null : previousChild;
            overlay.IsVisible = !cancelled && previousVisible;
            _cancelModal = previousCancel;
            if (cancelled) previousCancel?.Invoke();
        }
    }

    private static CheckBox Check<TSource>(string label, Expression<Func<TSource, bool>> expression)
    {
        var control = new CheckBox { Content = label, MinHeight = 44 };
        MobileText.Bind(control, ToggleButton.IsCheckedProperty, expression);
        return control;
    }

    private static ComboBox Options<TSource, TValue>(System.Collections.IEnumerable options, Expression<Func<TSource, TValue>> expression)
    {
        var control = new ComboBox { ItemsSource = options, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        MobileText.Bind(control, SelectingItemsControl.SelectedItemProperty, expression);
        return control;
    }

    private static Button CommandButton(string label, string id, System.Windows.Input.ICommand command)
    {
        var button = MobileText.Button(label, id);
        button.Command = command;
        return button;
    }
}
