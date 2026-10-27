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

    /// <inheritdoc/>
    public Task<string?> PickPhotoFolderAsync(string? startFolder) => PickFolderAsync(startFolder);

    public async Task<string?> PickFolderAsync(string? startFolder)
    {
        var options = new FolderPickerOpenOptions { Title = Localization.Loc.Get("Dialog.ChoosePhotoFolder"), AllowMultiple = false };
        if (startFolder is not null && Directory.Exists(startFolder))
            options.SuggestedStartLocation = await Owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var result = await Owner.StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns)
    {
        var result = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<string> patterns)
    {
        var result = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggestedName, ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(title) { Patterns = patterns }],
        });
        return result?.TryGetLocalPath();
    }

    public Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(int photoCount) =>
        SimpleDialogs.AskKeepOverridesAsync(Owner, photoCount);

    public async Task<bool> ShowSelectivePasteAsync(SelectivePasteViewModel vm) =>
        await new SelectivePasteDialog { DataContext = vm }.ShowDialog<bool>(Owner);

    public async Task<bool> ShowNewPresetAsync(NewPresetViewModel vm) =>
        await new NewPresetDialog { DataContext = vm }.ShowDialog<bool>(Owner);

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

    public Task ShowAboutAsync(AboutViewModel vm) => new AboutDialog { DataContext = vm }.ShowDialog(Owner);

    public async Task OpenUriAsync(string uri)
    {
        if (!await Owner.Launcher.LaunchUriAsync(new Uri(uri))) AppLog.Warn($"無法開啟網址：{uri}");
    }
}
