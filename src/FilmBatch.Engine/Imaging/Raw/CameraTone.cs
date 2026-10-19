// 移植自 LightCraft crates/pipeline/src/tone.rs（CameraTone：相機外觀曲線）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 檔案區域的相機外觀：場景／顯示線性亮度配對節點（32 組）與顯示亮度色度曲線（8 點）。
/// 不可變類別，值語意相等；驗證規則與 lightcraft 的 <c>CameraTone::new/with_chroma</c> 相同。
/// </summary>
internal sealed class CameraTone : IEquatable<CameraTone>
{
    /// <summary>節點數（<c>tone.rs</c> 的 32）。</summary>
    public const int KnotCount = 32;

    /// <summary>色度節點數（<c>tone.rs</c> 的 <c>CHROMA_N</c>），在顯示亮度 0..=1 均勻分佈。</summary>
    public const int ChromaCount = 8;

    private readonly float[] _knots;
    private readonly float[] _chroma;

    private CameraTone(float[] knots, float[] chroma)
    {
        _knots = knots;
        _chroma = chroma;
    }

    /// <summary>
    /// 由 64 個 float（[x0, y0, x1, y1, …]）建立。x 須嚴格遞增，y 非遞減且 &lt; 1，
    /// 全部有限；不符則回傳 null（對應 Rust 的 <c>None</c>）。
    /// </summary>
    public static CameraTone? Create(float[] knots)
    {
        if (knots.Length != KnotCount * 2)
            return null;
        var copy = (float[])knots.Clone();
        var px = 0f;
        var py = 0f;
        for (var i = 0; i < KnotCount; i++)
        {
            var x = copy[2 * i];
            var y = copy[2 * i + 1];
            if (!float.IsFinite(x) || !float.IsFinite(y))
                return null;
            if (x <= px || y < py || y >= 1f)
                return null;
            px = x;
            py = y;
        }
        var chroma = new float[ChromaCount];
        Array.Fill(chroma, 1f);
        return new CameraTone(copy, chroma);
    }

    /// <summary>
    /// 附上顯示亮度 0、1/7 … 1 的色度縮放（每點有限、0..=4）；不符則回傳 null。
    /// 回傳新物件，原物件不變。
    /// </summary>
    public CameraTone? WithChroma(float[] chroma)
    {
        if (chroma.Length != ChromaCount)
            return null;
        foreach (var k in chroma)
        {
            if (!float.IsFinite(k) || k < 0f || k > 4f)
                return null;
        }
        return new CameraTone((float[])_knots.Clone(), (float[])chroma.Clone());
    }

    /// <summary>64 個 float 的配對節點（防禦性複本）。</summary>
    public float[] Knots => (float[])_knots.Clone();

    /// <summary>8 個色度縮放（防禦性複本；未設定時全為 1）。</summary>
    public float[] Chroma => (float[])_chroma.Clone();

    /// <summary>場景亮度 → 顯示線性亮度：節點間線性內插，超出末節點以連續有界肩部延伸。</summary>
    public float Apply(float y)
    {
        if (!float.IsFinite(y) || y <= 0f)
            return 0f;
        var px = 0f;
        var py = 0f;
        for (var i = 0; i < KnotCount; i++)
        {
            var x = _knots[2 * i];
            var q = _knots[2 * i + 1];
            if (y <= x)
            {
                var t = (y - px) / (x - px);
                return py + t * (q - py);
            }
            px = x;
            py = q;
        }
        var ax = _knots[2 * 30];
        var ay = _knots[2 * 30 + 1];
        var bx = _knots[2 * 31];
        var by = _knots[2 * 31 + 1];
        var slope = Clamp((by - ay) / (bx - ax), 0.1f, 16f);
        return 1f - (1f - by) * MathF.Exp(-(y - bx) * slope / MathF.Max(1f - by, 0.01f));
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    public bool Equals(CameraTone? other) =>
        other is not null && _knots.AsSpan().SequenceEqual(other._knots) && _chroma.AsSpan().SequenceEqual(other._chroma);

    public override bool Equals(object? obj) => obj is CameraTone t && Equals(t);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var v in _knots) h.Add(v);
        foreach (var v in _chroma) h.Add(v);
        return h.ToHashCode();
    }

    public static bool operator ==(CameraTone? a, CameraTone? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(CameraTone? a, CameraTone? b) => !(a == b);
}
