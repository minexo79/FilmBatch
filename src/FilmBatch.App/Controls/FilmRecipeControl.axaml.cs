using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FilmBatch.App.Controls;

/// <summary>底片配方選擇控制（僅 XAML，邏輯在 <see cref="ViewModels.FilmRecipeViewModel"/>）。</summary>
public partial class FilmRecipeControl : UserControl
{
    public FilmRecipeControl() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
