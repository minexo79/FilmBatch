// 移植自 LightCraft crates/raw/src/saturation.rs（低於白階的感光飽和平台）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw;

public sealed partial class RawImage
{
    /// <summary>平台至少佔該顏色樣本的這個比例（且至少 100 個）。</summary>
    private const double MinShare = 1e-4;
    /// <summary>且至少是其下方鄰域平均值的這個倍數。</summary>
    private const double MinSpike = 16.0;
    /// <summary>離白階在此範圍比例內的平台就是白階（不需提升）。</summary>
    private const float NearWhite = .03f;
    /// <summary>平台上方至多此比例的樣本（實際截斷點是資料的頂端）。</summary>
    private const double MaxAbove = .02;
    private const float AboveMargin = .01f;
    /// <summary>平台下方「鄰域平均值」向下看的距離。</summary>
    private const int Neighbourhood = 64;

    /// <summary>各顏色樣本的實際飽和值（明顯低於宣告白階時）；僅檢查單平面整數 CFA 資料。</summary>
    public ushort?[] SaturationPoints()
    {
        ushort?[] none = [null, null, null];
        if (Data is not RawData.U16 d || Cfa is not { } cfa) return none;
        var white = WhiteAt(0);
        var black = Black.Mean();
        // 敵意檔案可宣告任何階值：NaN、負值或反轉的階值一律不動
        var levelsOk = float.IsFinite(black) && float.IsFinite(white) && black >= 0f && white > black && white <= 65535f;
        if (Cpp != 1 || !cfa.Valid || checked((long)Width * Height) != d.Samples.Length || !levelsOk)
            return none;
        var a = ActiveArea;
        static bool Fits(int start, int len, int max) => start >= 0 && len >= 0 && (long)start + len <= max;
        if (a.Width <= 0 || a.Height <= 0 || !Fits(a.X, a.Width, Width) || !Fits(a.Y, a.Height, Height))
            return none;
        var lo = Math.Max((int)MathF.Ceiling(black + .5f * (white - black)), 0);
        var hi = Math.Min((int)white, 65535);
        if (lo + Neighbourhood >= hi) return none;
        var hist = new int[65536 * 3];
        var total = new long[3];
        for (var y = 0; y < a.Height; y++)
        {
            var row = d.Samples.AsSpan((a.Y + y) * Width + a.X, a.Width);
            for (var x = 0; x < row.Length; x++)
            {
                var c = Math.Min(cfa.ColorAt(a.X + x, a.Y + y), (byte)2);
                total[c]++;
                hist[row[x] * 3 + c]++;
            }
        }
        var outp = (ushort?[])none.Clone();
        for (var c = 0; c < 3; c++)
        {
            // 上半部範圍中最多的值
            var bestV = 0; var bestN = 0; var found = false;
            for (var v = lo; v < hi; v++)
            {
                var n = hist[v * 3 + c];
                // Rust max_by_key(n, v)：平手取較大值 → 遍歷遇更大值才更新
                if (!found || n >= bestN) { found = true; bestV = v; bestN = n; }
            }
            if (!found) continue;
            if (bestN < Math.Max(total[c] * MinShare, 100d) || bestV >= white - NearWhite * (white - black))
                continue;
            var below = 0L;
            for (var u = Math.Max(bestV - Neighbourhood, 0); u < bestV; u++) below += hist[u * 3 + c];
            var from = Math.Min(bestV + Math.Max((int)(AboveMargin * (white - black)), 1) + 1, 65536);
            var above = 0L;
            for (var u = from; u < 65536; u++) above += hist[u * 3 + c];
            if (bestN >= MinSpike * Math.Max(below / (double)Neighbourhood, 1d) && above <= MaxAbove * bestN)
                outp[c] = (ushort)bestV;
        }
        return outp;
    }

    /// <summary>把達到各顏色真實飽和點的樣本提升到宣告白階，讓高光重建認出截斷。回傳改變的樣本數（0 = 未動）。</summary>
    public int LiftClippedSamples()
    {
        var points = SaturationPoints();
        if (points.All(p => p is null)) return 0;
        var a = ActiveArea;
        var w = Width;
        var white = (ushort)MathF.Round(WhiteAt(0), MidpointRounding.AwayFromZero);
        if (Cfa is not { } cfa || Data is not RawData.U16 d) return 0;
        var changed = 0;
        for (var y = 0; y < a.Height; y++)
        {
            var row = d.Samples.AsSpan((a.Y + y) * w + a.X, a.Width);
            for (var x = 0; x < row.Length; x++)
            {
                var c = Math.Min(cfa.ColorAt(a.X + x, a.Y + y), (byte)2);
                if (points[c] is { } p && row[x] >= p && row[x] < white)
                {
                    row[x] = white;
                    changed++;
                }
            }
        }
        return changed;
    }
}
