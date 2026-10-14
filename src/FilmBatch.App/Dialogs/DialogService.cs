using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FilmBatch.App.Gallery;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels.Dialogs;

namespace FilmBatch.App.Dialogs;

/// <summary>以 <c>Window.ShowDialog&lt;T&gt;(owner)</c> 實作的對話框服務（計畫書 2.8）。</summary>
public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    private Window Owner => owner() ?? throw new InvalidOperationException("主視窗尚未建立");

    public async Task<string?> PickFolderAsync(string? startFolder)
    {
        var options = new FolderPickerOpenOptions { Title = "選擇照片資料夾", AllowMultiple = false };
        if (startFolder is not null && Directory.Exists(startFolder))
            options.SuggestedStartLocation = await Owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var result = await Owner.StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(int photoCount) =>
        SimpleDialogs.AskKeepOverridesAsync(Owner, photoCount);

    public async Task<bool> ShowSelectivePasteAsync(SelectivePasteViewModel vm) =>
        await new SelectivePasteDialog { DataContext = vm }.ShowDialog<bool>(Owner);

    public Task ShowExportAsync(ExportViewModel vm) =>
        new ExportDialog { DataContext = vm }.ShowDialog(Owner);

    public async Task ShowPresetManagerAsync(PresetManagerViewModel vm)
    {
        await new PresetManagerDialog { DataContext = vm }.ShowDialog(Owner);
        vm.Apply();
    }

    public async Task ShowPreferencesAsync(PreferencesViewModel vm)
    {
        await new PreferencesDialog { DataContext = vm }.ShowDialog(Owner);
        vm.Apply();
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        SimpleDialogs.ConfirmAsync(Owner, title, message, confirmText);

    public Task<string?> PromptAsync(string title, string label, string initial) =>
        SimpleDialogs.PromptAsync(Owner, title, label, initial);

    public void ShowGallery() => new GalleryWindow().Show(Owner);
}
