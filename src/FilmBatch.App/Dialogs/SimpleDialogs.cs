using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FilmBatch.App.Localization;

namespace FilmBatch.App.Dialogs;

/// <summary>確認類的小型對話框（計畫書 2.8）：以程式碼建立，樣式沿用主題。</summary>
internal static class SimpleDialogs
{
    private static Window CreateWindow(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText)
    {
        var w = CreateWindow(title, 420);
        var ok = new Button { Content = confirmText, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = Loc.Get("Dialog.Cancel"), IsCancel = true };
        ok.Click += (_, _) => w.Close(true);
        cancel.Click += (_, _) => w.Close(false);
        w.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, Buttons(ok, cancel) },
        };
        return await w.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial)
    {
        var w = CreateWindow(title, 380);
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = Loc.Get("Dialog.OK"), IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = Loc.Get("Dialog.Cancel"), IsCancel = true };
        ok.Click += (_, _) => w.Close(box.Text);
        cancel.Click += (_, _) => w.Close(null);
        w.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children = { new TextBlock { Text = label }, box, Buttons(ok, cancel) },
        };
        w.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await w.ShowDialog<string?>(owner);
    }

    /// <summary>指定預設時目標照片已有覆寫：保留 / 清除，可勾選「記住我的選擇」（計畫書 2.7.1）。</summary>
    public static async Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(Window owner, int count)
    {
        var w = CreateWindow(Loc.Get("Dialog.AssignTitle"), 420);
        var remember = new CheckBox { Content = Loc.Get("Dialog.Remember") };
        var keep = new Button { Content = Loc.Get("Dialog.KeepOverrides"), IsDefault = true, Classes = { "accent" } };
        var clear = new Button { Content = Loc.Get("Dialog.ClearOverrides") };
        var cancel = new Button { Content = Loc.Get("Dialog.Cancel"), IsCancel = true };
        (bool, bool)? result = null;
        keep.Click += (_, _) => { result = (true, remember.IsChecked == true); w.Close(); };
        clear.Click += (_, _) => { result = (false, remember.IsChecked == true); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"有 {count} 張照片已有單張覆寫。\n保留覆寫：只更換預設，手動調整的欄位維持不變。\n清除覆寫：完全改用新預設。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                remember,
                Buttons(keep, clear, cancel),
            },
        };
        await w.ShowDialog(owner);
        return result;
    }
}
