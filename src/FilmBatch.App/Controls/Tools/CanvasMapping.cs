using Avalonia;

namespace FilmBatch.App.Controls.Tools;

/// <summary>
/// 影像在畫布上的矩形 ↔ 正規化座標（0–1，相對於目前顯示的影像）。
/// <paramref name="RotationDeg"/> 非 0 時，影像在畫布上以中心旋轉該角度（畫布 y 向下 → 順時針為正），換算一併套用。
/// </summary>
public readonly record struct CanvasMapping(Rect ImageRect, double RotationDeg = 0)
{
    public Point Pivot => ImageRect.Center;

    public Point ToImage(Point canvas)
    {
        var p = Rotate(canvas, -RotationDeg);
        return new((p.X - ImageRect.X) / ImageRect.Width, (p.Y - ImageRect.Y) / ImageRect.Height);
    }

    public Point ToCanvas(Point image) =>
        Rotate(new(ImageRect.X + image.X * ImageRect.Width, ImageRect.Y + image.Y * ImageRect.Height), RotationDeg);

    /// <summary>畫影像用的變換（以影像中心旋轉）。</summary>
    public Matrix Transform =>
        Matrix.CreateTranslation(-Pivot.X, -Pivot.Y) * Matrix.CreateRotation(RotationDeg * Math.PI / 180)
        * Matrix.CreateTranslation(Pivot.X, Pivot.Y);

    private Point Rotate(Point p, double deg)
    {
        if (deg == 0) return p;
        var a = deg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        var (x, y) = (p.X - Pivot.X, p.Y - Pivot.Y);
        return new(Pivot.X + cos * x - sin * y, Pivot.Y + sin * x + cos * y);
    }
}
