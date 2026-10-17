using FilmBatch.Core.Masks;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

public sealed class MaskMathTests
{
    [Fact]
    public void Linear_FullAtStart_ZeroAtEnd_HalfInMiddle()
    {
        var s = new LinearShape(0.5, 0, 0.5, 1);
        Assert.Equal(1f, MaskMath.Linear(s, 0.5, 0, 1), 3);
        Assert.Equal(0f, MaskMath.Linear(s, 0.5, 1, 1), 3);
        Assert.Equal(0.5f, MaskMath.Linear(s, 0.2, 0.5, 1), 3);
    }

    [Fact]
    public void Radial_HardEdge_InsideOutside()
    {
        var s = new RadialShape(0.5, 0.5, 0.2, 0.2, 0, 0);
        Assert.Equal(1f, MaskMath.Radial(s, 0.5, 0.5, 1));
        Assert.Equal(0f, MaskMath.Radial(s, 0.75, 0.5, 1));
    }

    [Fact]
    public void Radial_UsesShortSideUnits_IsRoundOnWideImage()
    {
        var s = new RadialShape(0.5, 0.5, 0.25, 0.25, 0, 0);
        // 寬 2：高 1 的畫面，x 方向 0.125（正規化）＝ 短邊的 0.25
        Assert.True(MaskMath.Radial(s, 0.5 + 0.12, 0.5, 2) > 0.5f);
        Assert.Equal(0f, MaskMath.Radial(s, 0.5 + 0.13, 0.5, 2));
    }

    [Fact]
    public void Radial_Feather_FadesToEdge_AndAngleRotatesEllipse()
    {
        var soft = new RadialShape(0.5, 0.5, 0.2, 0.2, 0, 1);
        Assert.Equal(1f, MaskMath.Radial(soft, 0.5, 0.5, 1), 4);
        Assert.Equal(0.5f, MaskMath.Radial(soft, 0.6, 0.5, 1), 3);
        var wide = new RadialShape(0.5, 0.5, 0.3, 0.1, 0, 0);
        Assert.Equal(1f, MaskMath.Radial(wide, 0.75, 0.5, 1));
        Assert.Equal(0f, MaskMath.Radial(wide, 0.5, 0.75, 1));
        var turned = wide with { AngleDeg = 90 };
        Assert.Equal(0f, MaskMath.Radial(turned, 0.75, 0.5, 1));
        Assert.Equal(1f, MaskMath.Radial(turned, 0.5, 0.75, 1));
    }

    [Fact]
    public void Polygon_InsideOutside_AndFeatherCenteredOnEdge()
    {
        var s = new PolygonShape([new(0.2, 0.2), new(0.8, 0.2), new(0.8, 0.8), new(0.2, 0.8)], 0.1);
        Assert.Equal(1f, MaskMath.Polygon(s, 0.5, 0.5, 1));
        Assert.Equal(0f, MaskMath.Polygon(s, 0.05, 0.5, 1));
        Assert.Equal(0.5f, MaskMath.Polygon(s, 0.2, 0.5, 1), 2);
    }

    [Fact]
    public void Polygon_NoFeather_IsHardEdge()
    {
        var s = new PolygonShape([new(0.2, 0.2), new(0.8, 0.2), new(0.5, 0.8)], 0);
        Assert.Equal(1f, MaskMath.Polygon(s, 0.5, 0.4, 1));
        Assert.Equal(0f, MaskMath.Polygon(s, 0.25, 0.7, 1));
    }

    private static float Lin(int srgb8) => CpuPipeline.SrgbToLinear(srgb8 / 255f);

    private static ColorShape Sampled(int r, int g, int b, double refine) =>
        new([Oklab.FromLinearSrgb(Lin(r), Lin(g), Lin(b))], refine);

    private static float Pick(ColorShape s, int r, int g, int b) => MaskMath.Color(s, Lin(r), Lin(g), Lin(b));

    [Fact]
    public void Color_SelectsSampledColor_RejectsOtherHue()
    {
        var s = Sampled(60, 110, 190, 50);                 // 藍
        Assert.True(Pick(s, 60, 110, 190) > 0.99f);
        Assert.True(Pick(s, 63, 112, 187) > 0.9f);         // 很接近的藍
        Assert.Equal(0f, Pick(s, 200, 120, 50), 3);        // 橘
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(50.0)]
    [InlineData(85.0)]
    public void Color_TealGreyDoesNotSelectNeutralLightGrey(double refine)   // LightCraft issue #157 的情境
    {
        var s = Sampled(120, 150, 155, refine);
        Assert.True(Pick(s, 120, 150, 155) > 0.95f);
        Assert.True(Pick(s, 200, 198, 195) < 0.05f);
    }

    [Fact]
    public void Color_MultipleSamples_UseNearest()
    {
        var blue = Oklab.FromLinearSrgb(Lin(60), Lin(110), Lin(190));
        var orange = Oklab.FromLinearSrgb(Lin(200), Lin(120), Lin(50));
        var s = new ColorShape([blue, orange], 50);
        Assert.True(Pick(s, 200, 120, 50) > 0.99f);
        Assert.True(Pick(s, 60, 110, 190) > 0.99f);
        Assert.Equal(0f, Pick(s, 60, 170, 70), 3);         // 綠：兩個都不像
    }

    [Fact]
    public void ColorTolerance_GrowsWithRefine()
    {
        Assert.Equal(0.015f, MaskMath.ColorTolerance(0), 5);
        Assert.Equal(0.075f, MaskMath.ColorTolerance(100), 5);
        Assert.True(MaskMath.ColorTolerance(60) > MaskMath.ColorTolerance(40));
    }

    [Fact]
    public void OkLab_WhiteIsL1_NeutralHasNoChroma_AndInputIsClamped()
    {
        var white = Oklab.FromLinearSrgb(1, 1, 1);
        Assert.Equal(1, white.L, 3);
        Assert.Equal(0, white.A, 3);
        Assert.Equal(0, white.B, 3);
        Assert.Equal(white, Oklab.FromLinearSrgb(2, 3, 4));
        Assert.Equal(0, Oklab.FromLinearSrgb(-1, -1, -1).L, 6);
    }

    [Fact]
    public void Brush_EraseRemovesPaint()
    {
        // Size 為直徑（短邊比例）：50 px 影像上繪製半徑 10 px、擦除半徑 2.5 px
        var paint = new BrushStroke(0.4, 1, 1, false, [new(0.5, 0.5)]);
        var erase = paint with { Erase = true, Size = 0.1 };
        var list = BrushDabs.Expand(new BrushShape([paint, erase]), 50, 50);
        var alpha = new float[50 * 50];
        BrushDabs.Rasterize(list, Affine2.Identity, 50, 50, alpha);
        Assert.Equal(0f, alpha[25 * 50 + 25], 3);          // 中心被擦掉
        Assert.True(alpha[25 * 50 + 25 + 7] > 0.9f);       // 外圈仍在
    }

    [Fact]
    public void Brush_DabSpacing_AndRasterizeMatchesPerPixelEvaluate()   // CPU 蓋印 ＝ GPU 逐像素算法
    {
        var stroke = new BrushStroke(0.12, 0.4, 0.6, false, [new(0.2, 0.3), new(0.5, 0.5), new(0.8, 0.4)]);
        var list = BrushDabs.Expand(new BrushShape([stroke]), 64, 48);
        foreach (var (a, b) in list.Dabs.Zip(list.Dabs.Skip(1)))
            Assert.True(MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) <= list.Strokes[0].Radius / 4 + 1e-3f);
        var alpha = new float[64 * 48];
        BrushDabs.Rasterize(list, Affine2.Identity, 64, 48, alpha);
        for (var y = 0; y < 48; y += 3)
            for (var x = 0; x < 64; x += 3)
                Assert.Equal(BrushDabs.Evaluate(list, x + 0.5f, y + 0.5f), alpha[y * 64 + x], 4);
    }

    [Fact]
    public void Brush_RasterizeThroughTransform_MatchesEvaluate()
    {
        var paint = new BrushStroke(0.2, 0.3, 0.8, false, [new(0.3, 0.3), new(0.7, 0.6)]);
        var erase = new BrushStroke(0.08, 0.8, 1, true, [new(0.4, 0.5), new(0.6, 0.4)]);
        var list = BrushDabs.Expand(new BrushShape([paint, erase]), 80, 60);
        // 輸出 40×30：放大 2 倍並平移（模擬裁切後的畫框）
        var outToOriented = new Affine2(2, 0, 5, 0, 2, 3);
        var alpha = new float[40 * 30];
        BrushDabs.Rasterize(list, outToOriented, 40, 30, alpha);
        for (var y = 0; y < 30; y++)
            for (var x = 0; x < 40; x++)
            {
                var (ox, oy) = outToOriented.Apply(x + 0.5, y + 0.5);
                Assert.Equal(BrushDabs.Evaluate(list, (float)ox, (float)oy), alpha[y * 40 + x], 4);
            }
        Assert.Contains(alpha, v => v > 0.5f);
    }

    [Theory]
    [InlineData(MaskCombine.Add, 0.3f, 0.6f, 0.6f)]
    [InlineData(MaskCombine.Subtract, 0.8f, 0.5f, 0.4f)]
    [InlineData(MaskCombine.Intersect, 0.8f, 0.5f, 0.4f)]
    public void Combine_Modes(MaskCombine mode, float acc, float s, float expected) =>
        Assert.Equal(expected, MaskMath.Combine(acc, s, mode), 4);
}
