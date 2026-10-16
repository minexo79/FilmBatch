namespace FilmBatch.Raw;

/// <summary>
/// 整數像素矩形（lightcraft-raw 的 <c>Rect</c>）。所有欄位皆非負；來自檔案的數值在建構前必須先以
/// 檢查過的轉換處理（超出 <see cref="int.MaxValue"/> 時回報 <see cref="RawException.Corrupt"/>）。
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>裁切到 <paramref name="w"/> × <paramref name="h"/> 範圍內。</summary>
    public PixelRect Clipped(int w, int h)
    {
        var x = Math.Min(X, w);
        var y = Math.Min(Y, h);
        return new PixelRect(x, y, Math.Min(Width, w - x), Math.Min(Height, h - y));
    }
}

/// <summary>EXIF 方向（lightcraft-geom 的 <c>Orientation</c>）。</summary>
public enum Orientation
{
    Normal,
    FlipH,
    Rotate180,
    FlipV,
    Transpose,
    Rotate90,
    Transverse,
    Rotate270,
}

public static class OrientationExtensions
{
    public static Orientation FromExif(int value) => value switch
    {
        2 => Orientation.FlipH,
        3 => Orientation.Rotate180,
        4 => Orientation.FlipV,
        5 => Orientation.Transpose,
        6 => Orientation.Rotate90,
        7 => Orientation.Transverse,
        8 => Orientation.Rotate270,
        _ => Orientation.Normal,
    };

    public static int ToExif(this Orientation o) => o switch
    {
        Orientation.FlipH => 2,
        Orientation.Rotate180 => 3,
        Orientation.FlipV => 4,
        Orientation.Transpose => 5,
        Orientation.Rotate90 => 6,
        Orientation.Transverse => 7,
        Orientation.Rotate270 => 8,
        _ => 1,
    };

    /// <summary>轉正後寬高是否對調。</summary>
    public static bool SwapsAxes(this Orientation o) =>
        o is Orientation.Transpose or Orientation.Rotate90 or Orientation.Transverse or Orientation.Rotate270;

    /// <summary>先水平翻轉（<c>Flip</c>），再順時針轉 <c>QuarterTurns</c> 個 90°。</summary>
    public static (bool Flip, int QuarterTurns) ToParts(this Orientation o) => o switch
    {
        Orientation.Rotate90 => (false, 1),
        Orientation.Rotate180 => (false, 2),
        Orientation.Rotate270 => (false, 3),
        Orientation.FlipH => (true, 0),
        Orientation.Transverse => (true, 1),
        Orientation.FlipV => (true, 2),
        Orientation.Transpose => (true, 3),
        _ => (false, 0),
    };

    public static Orientation FromParts(bool flip, int quarterTurnsClockwise) => (flip, ((quarterTurnsClockwise % 4) + 4) % 4) switch
    {
        (false, 0) => Orientation.Normal,
        (false, 1) => Orientation.Rotate90,
        (false, 2) => Orientation.Rotate180,
        (false, _) => Orientation.Rotate270,
        (true, 0) => Orientation.FlipH,
        (true, 1) => Orientation.Transverse,
        (true, 2) => Orientation.FlipV,
        (true, _) => Orientation.Transpose,
    };

    /// <summary>抵銷這個方向的方向：翻轉與半圈是自身的反向，四分之一圈互換。</summary>
    public static Orientation Inverse(this Orientation o) => o switch
    {
        Orientation.Rotate90 => Orientation.Rotate270,
        Orientation.Rotate270 => Orientation.Rotate90,
        _ => o,
    };
}
