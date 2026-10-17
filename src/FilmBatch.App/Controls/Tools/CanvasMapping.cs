using Avalonia;

namespace FilmBatch.App.Controls.Tools;

/// <summary>影像在畫布上的矩形 ↔ 正規化座標（0–1，相對於目前顯示的影像）。</summary>
public readonly record struct CanvasMapping(Rect ImageRect)
{
    public Point ToImage(Point canvas) =>
        new((canvas.X - ImageRect.X) / ImageRect.Width, (canvas.Y - ImageRect.Y) / ImageRect.Height);

    public Point ToCanvas(Point image) =>
        new(ImageRect.X + image.X * ImageRect.Width, ImageRect.Y + image.Y * ImageRect.Height);
}
