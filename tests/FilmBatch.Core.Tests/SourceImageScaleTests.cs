using FilmBatch.Engine.Imaging;

namespace FilmBatch.Core.Tests;

/// <summary>穩定的縮放來源：同一來源、同一長邊回傳同一物件，讓 GPU 來源紋理快取（參考相等）命中。</summary>
public sealed class SourceImageScaleTests
{
    private static SourceImage Flat(int w, int h, RawLoadOptions options = default)
    {
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = (ushort)(i * 7);
            px[i + 1] = (ushort)(i * 13);
            px[i + 2] = (ushort)(i * 3);
            px[i + 3] = ushort.MaxValue;
        }
        return new SourceImage(w, h, px) { FilePath = "C:/photos/a.tif", RawOptions = options };
    }

    [Fact]
    public void ScaledTo_returns_same_instance_for_same_edge()
    {
        var src = Flat(400, 300);
        var a = src.ScaledTo(100);
        Assert.Same(a, src.ScaledTo(100));
        Assert.Equal((100, 75), (a.Width, a.Height));
        Assert.Same(src, src.ScaledTo(99999));
        Assert.Same(src, src.ScaledTo(400));
    }

    [Fact]
    public void ScaledTo_keeps_at_most_two_cached_edges()
    {
        var src = Flat(400, 300);
        var a = src.ScaledTo(100);
        var b = src.ScaledTo(200);
        Assert.Same(a, src.ScaledTo(100));   // 觸及 100，200 變成最舊
        var c = src.ScaledTo(50);            // 逐出 200
        Assert.Same(a, src.ScaledTo(100));
        Assert.Same(c, src.ScaledTo(50));
        Assert.NotSame(b, src.ScaledTo(200));
    }

    [Fact]
    public void ScaledTo_preserves_reference_size_and_metadata()
    {
        var options = new RawLoadOptions(LensDatabasePath: "lens-db");
        var src = Flat(400, 300, options);
        var scaled = src.ScaledTo(120);
        Assert.Equal((400, 300), (scaled.ReferenceWidth, scaled.ReferenceHeight));
        Assert.Equal(src.FilePath, scaled.FilePath);
        Assert.Equal(options, scaled.RawOptions);
        Assert.Equal(src.Downscale(120).Rgba16, scaled.Rgba16);
    }

    [Fact]
    public void ScaledTo_rejects_non_positive_edge() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Flat(8, 8).ScaledTo(0));
}
