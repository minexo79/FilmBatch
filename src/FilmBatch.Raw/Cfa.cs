namespace FilmBatch.Raw;

/// <summary>彩色濾鏡陣列（CFA）排列。顏色索引：0 = 紅、1 = 綠、2 = 藍。對應 lightcraft-raw 的 <c>Cfa</c>。</summary>
public sealed class Cfa : IEquatable<Cfa>
{
    public Cfa(int width, int height, byte[] pattern)
    {
        Width = width;
        Height = height;
        Pattern = pattern;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary><c>Width × Height</c> 個顏色索引，逐列排列，錨定在影像像素 (0, 0)。</summary>
    public byte[] Pattern { get; }

    /// <summary>由 <c>"RGGB"</c> 之類的字串建立 2×2 拜耳排列；字串不合法時為 null。</summary>
    public static Cfa? Bayer(string s)
    {
        if (s.Length != 4) return null;
        var p = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            switch (s[i])
            {
                case 'R': p[i] = 0; break;
                case 'G': p[i] = 1; break;
                case 'B': p[i] = 2; break;
                default: return null;
            }
        }
        return new Cfa(2, 2, p);
    }

    /// <summary>程式碼中寫死的拜耳排列名稱（"RGGB"、"BGGR"、"GRBG"、"GBRG"）；其他字串退回 RGGB 而不失敗。</summary>
    internal static Cfa BayerStatic(string s) => Bayer(s) ?? new Cfa(2, 2, [0, 1, 1, 2]);

    /// <summary>Fujifilm X-Trans 6×6 排列，錨定在 (0, 0)。</summary>
    public static Cfa XTrans()
    {
        string[] rows = ["GGRGGB", "GGBGGR", "BRGRBG", "GGBGGR", "GGRGGB", "RBGBRG"];
        var pattern = new byte[36];
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 6; x++)
                pattern[y * 6 + x] = rows[y][x] switch { 'R' => 0, 'G' => 1, _ => 2 };
        return new Cfa(6, 6, pattern);
    }

    public byte ColorAt(int x, int y) => Pattern[(y % Height) * Width + (x % Width)];

    /// <summary>相同排列，錨點移到目前錨點的 (<paramref name="dx"/>, <paramref name="dy"/>)（例如裁切到有效區域之後）。</summary>
    public Cfa Shifted(int dx, int dy)
    {
        var pattern = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                pattern[y * Width + x] = ColorAt(x + dx, y + dy);
        return new Cfa(Width, Height, pattern);
    }

    /// <summary>2×2 且恰好一紅、二綠、一藍。</summary>
    public bool IsBayer
    {
        get
        {
            if (Width != 2 || Height != 2) return false;
            Span<int> c = stackalloc int[3];
            foreach (var p in Pattern)
            {
                if (p > 2) return false;
                c[p]++;
            }
            return c[0] == 1 && c[1] == 2 && c[2] == 1;
        }
    }

    /// <summary>排列名稱，例如 <c>"RGGB"</c>。</summary>
    public string Name => string.Concat(Pattern.Select(c => "RGB?"[Math.Min((int)c, 3)]));

    /// <summary>尺寸 1..16 且資料長度一致。</summary>
    internal bool Valid => Width > 0 && Height > 0 && Width <= 16 && Height <= 16 && Pattern.Length == Width * Height;

    public bool Equals(Cfa? other) =>
        other is not null && Width == other.Width && Height == other.Height && Pattern.AsSpan().SequenceEqual(other.Pattern);

    public override bool Equals(object? obj) => Equals(obj as Cfa);

    public override int GetHashCode() => HashCode.Combine(Width, Height, Name);
}
