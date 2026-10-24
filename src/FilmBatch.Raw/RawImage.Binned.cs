// 移植自 LightCraft crates/raw/src/binned.rs（超像素分箱顯影）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw;

public sealed partial class RawImage
{
    /// <summary>k×k 區塊是否能分箱（單平面 CFA，每個 k×k 視窗皆含 R、G、B；Bayer 需 k 為偶數）。</summary>
    public bool CanBin(int k)
    {
        if (Cfa is not { } cfa) return false;
        if (k < 2 || Cpp != 1 || MathF.Abs(PixelAspect - 1f) >= .01f || !cfa.Valid || (cfa.IsBayer && k % 2 != 0))
            return false;
        for (var j = 0; j < cfa.Height; j++)
            for (var i = 0; i < cfa.Width; i++)
            {
                var n = BlockLayout(cfa, i, j, k).Counts;
                if (n[0] == 0 || n[1] == 0 || n[2] == 0) return false;
            }
        return true;
    }

    /// <summary>與 <see cref="Develop"/> 相同，但尺寸為 1/k（相機 RGB，白 = 1.0，套用預設裁切，未轉正）。
    /// 無法分箱或帶有 OpcodeList3 時為 null（其運算元定義在完整解析度）。</summary>
    public RgbImage? DevelopBinned(int k, float clip) => Bin(k, clip, keepMax: true)?.Image;

    /// <summary>
    /// 與 <see cref="DevelopBinned(int, float)"/> 相同；<paramref name="deferList3"/> 為 true 時帶有 OpcodeList3
    /// 也分箱，輸出尚未套用任何 List3 運算碼（呼叫端只在 List3 僅含鏡頭鏈時使用）。
    /// </summary>
    public RgbImage? DevelopBinned(int k, float clip, bool deferList3) => Bin(k, clip, keepMax: true, deferList3)?.Image;

    /// <summary>
    /// 與 <see cref="DevelopBinned"/> 相同，但每個通道皆為區塊平均值；遮罩每輸出像素一個位元組，
    /// <see cref="Highlight.ClippedR"/>/G/B 位元標示該顏色區塊內有大於等於 clip 的樣本。
    /// 與影像一起交給 <see cref="Highlight.ReconstructMasked"/>。
    /// </summary>
    public (RgbImage Image, byte[] Mask)? DevelopBinnedMasked(int k, float clip) => Bin(k, clip, keepMax: false, deferList3: false);

    /// <summary>
    /// 與 <see cref="DevelopBinnedMasked(int, float)"/> 相同；<paramref name="deferList3"/> 為 true 時帶有 OpcodeList3
    /// 也分箱，由呼叫端在 <see cref="BinnedRegion"/> 座標下自行套用 List3（輸出尚未套用任何 List3 運算碼）。
    /// </summary>
    public (RgbImage Image, byte[] Mask)? DevelopBinnedMasked(int k, float clip, bool deferList3) =>
        Bin(k, clip, keepMax: false, deferList3);

    /// <summary>分箱涵蓋的有效區域範圍（預設裁切，寬高截為 k 的倍數）；輸出像素 (bx, by) 即此範圍內的第 (bx, by) 個 k×k 區塊。</summary>
    public PixelRect BinnedRegion(int k)
    {
        var a = ActiveArea;
        var c = Crop.Clipped(a.Width, a.Height);
        c = c.Width < k || c.Height < k ? new PixelRect(0, 0, a.Width, a.Height) : c;
        return new PixelRect(c.X, c.Y, c.Width / k * k, c.Height / k * k);
    }

    private (RgbImage Image, byte[] Mask)? Bin(int k, float clip, bool keepMax, bool deferList3 = false)
    {
        Validate();
        if (!CanBin(k) || (!deferList3 && Opcodes.List3.Count != 0)) return null;
        if (Cfa is not { } cfa) return null;
        var a = ActiveArea;
        // 只分箱預設裁切（相對於有效區域）
        var c = BinnedRegion(k);
        var (bw, bh) = (c.Width / k, c.Height / k);
        if (bw == 0 || bh == 0) return null;
        // Opcode 清單 1/2 需要正規化複本；純檔案逐列正規化即可
        Normalized? norm = Opcodes.List1.Count == 0 && Opcodes.List2.Count == 0 ? null : Normalized();
        cfa = cfa.Shifted(a.X, a.Y);
        var range = WhiteAt(0) - Black.Mean();
        var scale = range > 0f ? 1f / range : 1f;
        var uniformBlack = Black.DeltaH.Length == 0 && Black.DeltaV.Length == 0
            && Black.Values.Length > 0 && Black.Values.All(v => v == Black.Values[0]);
        var black0 = Black.At(0, 0, 0, 1);
        // 每個排列相位的區塊角對應的區塊樣本顏色與各顏色倒數
        var (pw, ph) = (cfa.Width, cfa.Height);
        var layouts = new (byte[] Layout, float[] Inv)[pw * ph];
        for (var j = 0; j < ph; j++)
            for (var i = 0; i < pw; i++)
            {
                var (l, n) = BlockLayout(cfa, i, j, k);
                layouts[j * pw + i] = (l, new float[] { 1f / Math.Max(n[0], 1), 1f / Math.Max(n[1], 1), 1f / Math.Max(n[2], 1) });
            }
        var wlen = bw * k;
        var outp = new RgbImage(bw, bh);
        var mask = new byte[bw * bh];
        // 每個工作分區重用 k 列暫存；每輪完整覆寫，不跨列保留樣本。
        Parallel.For(0, bh, () => new float[k * wlen], (by, _, rows) =>
        {
            for (var dy = 0; dy < k; dy++)
            {
                var y = c.Y + by * k + dy;
                var dst = rows.AsSpan(dy * wlen, wlen);
                if (norm is { } nm)
                    nm.Data.AsSpan((y * nm.Width + c.X), wlen).CopyTo(dst);
                else if (Data is RawData.U16 u16 && uniformBlack)
                {
                    var src = u16.Samples.AsSpan(((a.Y + y) * Width + a.X + c.X), wlen);
                    for (var i = 0; i < wlen; i++) dst[i] = (src[i] - black0) * scale;
                }
                else
                {
                    var baseIdx = (a.Y + y) * Width + a.X + c.X;
                    for (var x = 0; x < wlen; x++) dst[x] = (Data.Get(baseIdx + x) - Black.At(c.X + x, y, 0, 1)) * scale;
                }
            }
            var py = (c.Y + by * k) % ph;
            Span<float> sum = stackalloc float[3];
            Span<float> max = stackalloc float[3];
            for (var bx = 0; bx < bw; bx++)
            {
                var (layout, inv) = layouts[py * pw + (c.X + bx * k) % pw];
                sum.Clear();
                max[0] = max[1] = max[2] = float.MinValue;

                for (var dy = 0; dy < k; dy++)
                {
                    var rowOff = dy * wlen + bx * k;
                    var layOff = dy * k;
                    for (var x = 0; x < k; x++)
                    {
                        var v = rows[rowOff + x];
                        var col = Math.Min(layout[layOff + x], (byte)2);
                        sum[col] += v;
                        max[col] = MathF.Max(max[col], v);
                    }
                }
                var i = (by * bw + bx) * 3;
                for (var ch = 0; ch < 3; ch++)
                {
                    var clipped = max[ch] >= clip;
                    outp.Data[i + ch] = clipped && keepMax ? max[ch] : sum[ch] * inv[ch];
                    if (clipped) mask[by * bw + bx] |= (byte)(1 << ch);
                }
            }
            return rows;
        }, _ => { });
        return (outp, mask);
    }

    /// <summary>區塊角在排列位置 (i, j)（列主序）的 k×k 樣本顏色，與各顏色個數。</summary>
    private static (byte[] Layout, int[] Counts) BlockLayout(Cfa cfa, int i, int j, int k)
    {
        var counts = new int[3];
        var layout = new byte[k * k];
        for (var t = 0; t < k * k; t++)
        {
            var c = (byte)Math.Min(cfa.ColorAt(i + t % k, j + t / k), (byte)2);
            counts[c]++;
            layout[t] = c;
        }
        return (layout, counts);
    }
}
