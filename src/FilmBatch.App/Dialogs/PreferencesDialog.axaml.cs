using Avalonia.Controls;

namespace FilmBatch.App.Dialogs;

public partial class PreferencesDialog : Window
{
    public PreferencesDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        // 分類切換只改變顯示中的區塊（View 層行為，ViewModel 不需要知道目前分類）
        Control[] sections = [GeneralSection, PreviewSection, GpuSection, FoldersSection];
        SectionList.SelectionChanged += (_, _) =>
        {
            for (var i = 0; i < sections.Length; i++) sections[i].IsVisible = i == SectionList.SelectedIndex;
            ContentScroll.Offset = default;
        };
    }
}
