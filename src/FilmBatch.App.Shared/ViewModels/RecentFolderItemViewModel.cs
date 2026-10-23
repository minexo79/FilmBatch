using CommunityToolkit.Mvvm.Input;

namespace FilmBatch.App.ViewModels;

/// <summary>「檔案 → 開啟...」子選單中的一個最近資料夾。</summary>
public sealed class RecentFolderItemViewModel
{
    public RecentFolderItemViewModel(string path, Func<string, Task> open)
    {
        Path = path;
        OpenCommand = new AsyncRelayCommand(() => open(path));
    }

    /// <summary>資料夾完整路徑。</summary>
    public string Path { get; }

    /// <summary>選單標題：底線在 Avalonia 選單代表快速鍵，路徑中的底線要重複一次才會原樣顯示。</summary>
    public string Header => Path.Replace("_", "__");

    public IAsyncRelayCommand OpenCommand { get; }
}
