using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Core.Film;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Services;

/// <summary>對話框（計畫書 2.8）。ViewModel 只透過這個介面開啟對話框，因此可在沒有視窗的測試中替換。</summary>
public interface IDialogService
{
    /// <summary>Selects photographs to open as a project; sandboxed hosts import an owned folder copy.</summary>
    Task<string?> PickPhotoFolderAsync(string? startFolder);

    /// <summary>Selects a directory-valued setting without creating a photo project.</summary>
    Task<string?> PickFolderAsync(string? startFolder);
    Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns);
    Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<string> patterns);

    /// <summary>指定預設時，目標照片已有覆寫：回傳 (保留覆寫?, 記住選擇?)；取消時回傳 null。</summary>
    Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(int photoCount);

    Task<bool> ShowSelectivePasteAsync(SelectivePasteViewModel vm);
    Task<bool> ShowNewPresetAsync(NewPresetViewModel vm);
    Task ShowPresetManagerAsync(PresetManagerViewModel vm);
    Task ShowPreferencesAsync(PreferencesViewModel vm);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<string?> PromptAsync(string title, string label, string initial);
    /// <summary>Shows parsed CLF descriptors and requests explicit stage and encoding declarations; null cancels.</summary>
    Task<ClfImportOptions?> ShowColorTransformImportAsync(ColorTransform transform, CancellationToken cancellationToken);
    /// <summary>Confirms the parsed camera model and usage policy before publishing a DCP.</summary>
    Task<bool> ShowCameraProfileImportAsync(DcpProfile profile, CancellationToken cancellationToken);
    void ShowGallery();
    Task ShowAboutAsync(AboutViewModel vm);

    /// <summary>以系統預設瀏覽器開啟網址（GitHub 專案頁、回報問題）。</summary>
    Task OpenUriAsync(string uri);
}
