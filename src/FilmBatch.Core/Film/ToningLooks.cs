using System.Numerics;

namespace FilmBatch.Core.Film;

/// <summary>內建底片的六個基準色帶。</summary>
public enum ToningBand
{
    Aqua,
    Teal,
    Amber,
    AquaAmber,
    TealAmber,
    Mono,
}

/// <summary>
/// 內建底片：以六個基準色帶（湖藍、青綠、暖黃、湖藍→暖黃、青綠→暖黃、黑白）生成每組 3×3 共九式，
/// 載入時烘焙成 33³ 的 sRGB <see cref="FilmLut"/>，其餘流程（四面體內插、暖色、褪色、效果比例）沿用 LUT 管線。
/// 欄為過渡（柔和、中、強烈），列為濃淡（淡、中、濃）。
/// </summary>
public static class ToningLooks
{
    public const int Size = 33;

    private static readonly Vector3 AquaColor = FromHex(0x30DFF3);
    private static readonly Vector3 TealColor = FromHex(0x0187AE);
    private static readonly Vector3 AmberColor = FromHex(0xFFA500);

    /// <summary>過渡：色帶在亮度上的 smoothstep 半寬度（柔和 → 強烈）。</summary>
    private static readonly float[] Transition = [.5f, .28f, .08f];
    /// <summary>濃淡：色度加入量（淡 → 濃）。</summary>
    private static readonly float[] Density = [.3f, .55f, .8f];
    /// <summary>色度包絡指數：越小色度越往暗部與亮部延伸。</summary>
    private static readonly float[] Envelope = [.4f, .6f, .9f];
    /// <summary>黑白：對比 S 曲線量（柔和 → 強烈）。</summary>
    private static readonly float[] MonoContrast = [0f, .45f, .9f];
    /// <summary>黑白：中間調 gamma（亮 → 暗）。</summary>
    private static readonly float[] MonoGamma = [.8f, 1f, 1.25f];

    /// <summary>烘焙一式。<paramref name="row"/> 為濃淡、<paramref name="column"/> 為過渡，皆 0–2。</summary>
    public static FilmLut Bake(ToningBand band, int row, int column)
    {
        if (row is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(row));
        if (column is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(column));
        var data = new float[Size * Size * Size * 3];
        var max = Size - 1f;
        for (var b = 0; b < Size; b++)
        for (var g = 0; g < Size; g++)
        for (var r = 0; r < Size; r++)
        {
            var v = Map(band, row, column, new Vector3(r / max, g / max, b / max));
            var i = ((b * Size + g) * Size + r) * 3;
            data[i] = v.X; data[i + 1] = v.Y; data[i + 2] = v.Z;
        }
        return new FilmLut(Size, data);
    }

    /// <summary>單一 sRGB 顯示編碼值的映射（供烘焙與測試）。</summary>
    internal static Vector3 Map(ToningBand band, int row, int column, Vector3 rgb)
    {
        var l = Math.Clamp(Luma(rgb), 0f, 1f);
        if (band == ToningBand.Mono)
        {
            var x = MathF.Pow(l, MonoGamma[row]);
            var g = x + MonoContrast[column] * (SmoothStep(0f, 1f, x) - x);
            return new Vector3(Math.Clamp(g, 0f, 1f));
        }
        var (shadow, highlight) = Ends(band);
        var w = Transition[column];
        var tint = Vector3.Lerp(shadow, highlight, SmoothStep(.5f - w, .5f + w, l));
        var delta = tint - new Vector3(Luma(tint));
        var envelope = MathF.Pow(4f * l * (1f - l), Envelope[column]);
        // 在原色上加入亮度中性的色度偏移（像疊一層該色的遮罩，把顏色往該色拉），不取代原本的顏色。
        return Vector3.Clamp(Vector3.Clamp(rgb, Vector3.Zero, Vector3.One) + delta * (Density[row] * envelope), Vector3.Zero, Vector3.One);
    }

    /// <summary>暗部端與亮部端顏色；單色帶的亮部端淡化為近白，讓過渡欄也有差異。</summary>
    private static (Vector3 Shadow, Vector3 Highlight) Ends(ToningBand band) => band switch
    {
        ToningBand.Aqua => Single(AquaColor),
        ToningBand.Teal => Single(TealColor),
        ToningBand.Amber => Single(AmberColor),
        ToningBand.AquaAmber => (AquaColor, AmberColor),
        ToningBand.TealAmber => (TealColor, AmberColor),
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    private static (Vector3, Vector3) Single(Vector3 color) => (color, Vector3.Lerp(color, Vector3.One, .75f));

    private static float Luma(Vector3 c) => .2126f * c.X + .7152f * c.Y + .0722f * c.Z;

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector3 FromHex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
}
