using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Dialogs;

namespace FilmBatch.App.Services;

/// <summary>對話框（計畫書 2.8）。ViewModel 只透過這個介面開啟對話框，因此可在沒有視窗的測試中替換。</summary>
public interface IDialogService
{
    Task<string?> PickFolderAsync(string? startFolder);

    /// <summary>指定預設時，目標照片已有覆寫：回傳 (保留覆寫?, 記住選擇?)；取消時回傳 null。</summary>
    Task<(bool Keep, bool Remember)?> AskKeepOverridesAsync(int photoCount);

    Task<bool> ShowSelectivePasteAsync(SelectivePasteViewModel vm);
    Task ShowPresetManagerAsync(PresetManagerViewModel vm);
    Task ShowPreferencesAsync(PreferencesViewModel vm);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<string?> PromptAsync(string title, string label, string initial);
    void ShowGallery();
}
