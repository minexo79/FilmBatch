using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

/// <summary>鏡頭校正下的預覽分箱：先分箱再套鏡頭鏈，須與完整解析度顯影後區塊平均一致。</summary>
public sealed class BinnedLensPreviewTests
{
    private const int W = 160, H = 120, K = 4;

    private static (float R, float G, float B) Scene(int x, int y) =>
        (.2f + .3f * x / W + .05f * MathF.Sin(y / 9f), .4f - .2f * y / H, .25f + .1f * MathF.Sin(x / 11f));

    /// <summary>RGGB 馬賽克、偏移的預設裁切，與非置中的 warp＋徑向暗角。</summary>
    private static RawImage Raw(bool lens)
    {
        const float white = 16383;
        var data = new ushort[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var (r, g, b) = Scene(x, y);
                var v = (x % 2, y % 2) switch { (0, 0) => r, (1, 1) => b, _ => g };
                data[y * W + x] = (ushort)MathF.Round(v * white);
            }
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = W, Height = H, Cpp = 1, Data = new RawData.U16(data), Cfa = Cfa.Bayer("RGGB"),
            Black = BlackLevel.Uniform(0), White = [white], Bits = 14,
            ActiveArea = new(0, 0, W, H), Crop = new(6, 10, 144, 100),
        };
        if (lens)
        {
            raw.Opcodes.List3.Add(new WarpRectilinear([[.9, .08, 0, 0, 0, 0]], [.45, .55]));
            raw.Opcodes.List3.Add(new FixVignetteRadial([.3, 0, 0, 0, 0], [.5, .5]));
        }
        return raw;
    }

    private static RgbImage BinnedWithLens(RawImage raw)
    {
        var (image, _, region, deferred) = RawSourceLoader.DevelopBinned(raw, K)!.Value;
        Assert.True(deferred);
        return RawSourceGeometry.ApplyLensChainBinned(raw.Opcodes.List3, image, region, K, raw.ActiveArea.Width, raw.ActiveArea.Height);
    }

    [Fact]
    public void LensChainOnBinnedMatchesFullDevelopBlockMeans()
    {
        var raw = Raw(lens: true);
        Assert.Null(raw.DevelopBinnedMasked(K, .99f)); // 預設語意不變：帶 List3 不分箱
        var binned = BinnedWithLens(raw);
        var full = RawSourceGeometry.Develop(raw, DemosaicMethod.Bilinear);
        Assert.Equal((36, 25), (binned.Width, binned.Height));

        var (plain, _, _, plainDeferred) = RawSourceLoader.DevelopBinned(Raw(lens: false), K)!.Value;
        Assert.False(plainDeferred);
        double worst = 0, moved = 0;
        for (var by = 2; by < binned.Height - 2; by++)
            for (var bx = 2; bx < binned.Width - 2; bx++)
                for (var c = 0; c < 3; c++)
                {
                    var mean = 0f;
                    for (var dy = 0; dy < K; dy++)
                        for (var dx = 0; dx < K; dx++)
                            mean += full.Data[((by * K + dy) * full.Width + bx * K + dx) * 3 + c];
                    mean /= K * K;
                    var i = (by * binned.Width + bx) * 3 + c;
                    worst = Math.Max(worst, Math.Abs(binned.Data[i] - mean));
                    moved = Math.Max(moved, Math.Abs(binned.Data[i] - plain.Data[i]));
                }
        Assert.True(worst < .01, $"binned lens preview differs from full develop by {worst}");
        Assert.True(moved > .05, $"lens chain should visibly change the preview (max change {moved})");
    }

    [Fact]
    public void ClippedMaskStaysInBinnedCoordinatesForReconstruction()
    {
        var raw = Raw(lens: true);
        var data = ((RawData.U16)raw.Data).Samples;
        // 一個 4×4 區塊全部裁切：有效區域 x 70..73、y 50..53 = 分箱區域 (6, 10) 起的第 (16, 10) 個區塊
        for (var y = 50; y < 54; y++) for (var x = 70; x < 74; x++) data[y * W + x] = 16383;
        var (image, mask, region, _) = RawSourceLoader.DevelopBinned(raw, K)!.Value;
        Assert.Equal(new PixelRect(6, 10, 144, 100), region);
        var clipped = Assert.Single(Enumerable.Range(0, mask.Length), i => mask[i] != 0);
        Assert.Equal(16 + 10 * image.Width, clipped);
        Assert.Equal(Highlight.ClippedR | Highlight.ClippedG | Highlight.ClippedB, mask[clipped]);
    }

    [Fact]
    public void NonLensList3StillUsesFullDevelop()
    {
        var raw = Raw(lens: true);
        raw.Opcodes.List3.Add(new FixBadPixelsConstant(0, 0));
        Assert.Null(RawSourceLoader.DevelopBinned(raw, K));
    }
}
