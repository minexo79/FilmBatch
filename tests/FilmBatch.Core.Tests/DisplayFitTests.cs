using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

/// <summary>顯示縮放：在 worker 執行緒把預覽結果以 Lanczos-3 縮到剛好的裝置像素，straight alpha 先預乘再濾波。</summary>
public sealed class DisplayFitTests
{
    private static RenderResult Result(int w, int h, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (b, g, r, a) = pixel(x, y);
            var i = (y * w + x) * 4;
            bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = a;
        }
        return new RenderResult(w, h, bgra, TimeSpan.FromMilliseconds(3));
    }

    [Fact]
    public void DisplayFit_keeps_visible_colour_beside_transparent_pixels()
    {
        // 左半不透明紅、右半 alpha 0 的藍：縮小後可見像素不得帶入藍色（無紫邊），完全透明處 RGB 為 0。
        var r = Result(16, 4, (x, _) => x < 8 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)0));
        var fitted = DisplayFit.Fit(r, 6, 6);

        Assert.Equal(6, fitted.Width);
        Assert.Equal(2, fitted.Height);
        var sawPartial = false;
        for (var i = 0; i < fitted.Bgra.Length; i += 4)
        {
            var (b, g, red, a) = (fitted.Bgra[i], fitted.Bgra[i + 1], fitted.Bgra[i + 2], fitted.Bgra[i + 3]);
            if (a == 0)
            {
                Assert.Equal(0, b + g + red);
                continue;
            }
            if (a < 255) sawPartial = true;
            Assert.True(b <= 1, $"blue {b} leaked into visible pixel (alpha {a})");
            Assert.True(g <= 1, $"green {g} in visible pixel (alpha {a})");
            Assert.True(red >= 254, $"red {red} darkened at the edge (alpha {a})");
        }
        Assert.True(sawPartial, "the edge column should be partially covered");
    }

    [Fact]
    public void DisplayFit_is_identity_when_already_fits()
    {
        var r = Result(40, 30, (x, y) => ((byte)x, (byte)y, 9, 255));
        Assert.Same(r, DisplayFit.Fit(r, 40, 30));
        Assert.Same(r, DisplayFit.Fit(r, 4000, 4000));
        Assert.Same(r, DisplayFit.Fit(r, 0, 0));
    }

    [Fact]
    public void DisplayFit_fits_long_edge_box_and_preserves_result_metadata()
    {
        var prepared = new SourceImage(2, 2, new ushort[16]);
        var r = Result(300, 200, (x, y) => ((byte)(x % 256), (byte)(y % 256), 128, 255)) with { PreparedSource = prepared };
        var fitted = DisplayFit.Fit(r, 150, 150);

        Assert.Equal(150, fitted.Width);
        Assert.Equal(100, fitted.Height);
        Assert.Equal(150 * 100 * 4, fitted.Bgra.Length);
        Assert.Same(prepared, fitted.PreparedSource);
        Assert.Equal(r.Elapsed, fitted.Elapsed);
    }

    [Fact]
    public void DisplayFit_keeps_flat_colour_exact()
    {
        var r = Result(97, 61, (_, _) => (12, 200, 77, 255));
        var fitted = DisplayFit.Fit(r, 50, 50);
        for (var i = 0; i < fitted.Bgra.Length; i += 4)
            Assert.Equal(new byte[] { 12, 200, 77, 255 }, fitted.Bgra[i..(i + 4)]);
    }
}
