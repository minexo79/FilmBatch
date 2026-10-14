using Avalonia.Controls;

namespace FilmBatch.App.Gallery;

public partial class GalleryWindow : Window
{
    public GalleryWindow()
    {
        InitializeComponent();
        DataContext = new GalleryViewModel();
        // 多選狀態：預先選取兩張
        Thumbs.Selection.Select(0);
        Thumbs.Selection.Select(1);
        Canvas.ControlPointDragged += (p, _) => Canvas.ControlPoint = p;
    }
}
