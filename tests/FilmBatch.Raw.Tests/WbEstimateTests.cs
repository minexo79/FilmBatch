// 白平衡估計行為測試（上游 wb_estimate.rs 灰世界測試之移植；獨立手算期望）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;

namespace FilmBatch.Raw.Tests;
public sealed class WbEstimateTests
{
    private static WbFrame TintedFrame(ushort[] d, int w, int h)
    {
        var cfa = Cfa.Bayer("RGGB")!;
        return new WbFrame
        {
            Data = d,
            Width = w,
            Crop = new PixelRect(0, 0, w, h),
            Cfa = cfa,
            Black = BlackLevel.Uniform(100f),
            White = 4095f,
        };
    }

    [Fact]
    public void GreyWorldBalancesATintedFlatFrame()
    {
        // RGGB：黑上 100 上方 R 400、G 800、B 200
        const int w = 8, h = 8;
        ushort[] d = new ushort[w * h];
        for (var i = 0; i < d.Length; i++)
            d[i] = new ushort[] { 500, 900, 900, 300 }[(i / w % 2) * 2 + i % 2];
        var f = TintedFrame(d, w, h);
        Assert.Equal(new float[] { 2.0f, 1.0f, 4.0f }, WbEstimate.FromPreview(ReadOnlyMemory<byte>.Empty, f)!);
        // 退化：全部截斷
        var clipped = new WbFrame
        {
            Data = Enumerable.Repeat((ushort)4095, w * h).ToArray(),
            Width = w,
            Crop = new PixelRect(0, 0, w, h),
            Cfa = f.Cfa,
            Black = f.Black,
            White = 4095f,
        };
        Assert.Null(WbEstimate.FromPreview(ReadOnlyMemory<byte>.Empty, clipped));
        // 資料外的裁切不拋非 RawException
        var outside = new WbFrame
        {
            Data = clipped.Data,
            Width = w,
            Crop = new PixelRect(4, 4, 100, 100),
            Cfa = f.Cfa,
            Black = f.Black,
            White = 4095f,
        };
        try { _ = WbEstimate.FromPreview(ReadOnlyMemory<byte>.Empty, outside); }
        catch (RawException) { }
    }

    [Fact]
    public void FromRgbRespectsAspectGateAndNeutralThreshold()
    {
        const int w = 8, h = 8;
        ushort[] d = new ushort[w * h];
        for (var i = 0; i < d.Length; i++)
            d[i] = new ushort[] { 500, 900, 900, 300 }[(i / w % 2) * 2 + i % 2];
        var f = TintedFrame(d, w, h);
        // 長寬比不符（8×8 圖框配 8×4 預覽）→ grey world 後備（非 null，有色偏故可估）
        var wide = new byte[8 * 4 * 3];
        Array.Fill(wide, (byte)128);
        Assert.NotNull(WbEstimate.FromRgb(wide, 8, 4, f));
        // 全中性預覽（128 灰）：中性像素充足 → 量測值（非 grey world 的 [2,1,4] 也可接受，只驗範圍）
        var grey = new byte[w * h * 3];
        Array.Fill(grey, (byte)128);
        var gains = WbEstimate.FromRgb(grey, w, h, f);
        Assert.NotNull(gains);
        Assert.Equal(1.0f, gains![1]);
        Assert.InRange(gains[0], 0.1, 10.0);
        Assert.InRange(gains[2], 0.1, 10.0);
        // 全黑預覽（lo < 30）：無中性像素 → grey world 後備 [2,1,4]
        var black = new byte[w * h * 3];
        Assert.Equal(new float[] { 2.0f, 1.0f, 4.0f }, WbEstimate.FromRgb(black, w, h, f)!);
    }
}
