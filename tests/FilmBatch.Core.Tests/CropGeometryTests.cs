using FilmBatch.Engine.Rendering;

namespace FilmBatch.Core.Tests;

/// <summary>裁切幾何：變換、輸出尺寸、限制在影像內（docs/cut-and-mask/plan.md Task 1.2）。</summary>
public sealed class CropGeometryTests
{
    private static CropParams Full => new(true, 0.5, 0.5, 1, 1, 0, 0, false, false);

    [Fact]
    public void Disabled_IsIdentity()
    {
        var t = CropGeometry.Compute(400, 300, Full with { On = false });
        Assert.Equal((400, 300), (t.OutWidth, t.OutHeight));
        Assert.Equal((10.5, 20.5), t.OutToSource.Apply(10.5, 20.5));
    }

    [Fact]
    public void HalfCrop_TopLeftQuadrant()
    {
        var t = CropGeometry.Compute(400, 300, Full with { Cx = 0.25, Cy = 0.25, W = 0.5, H = 0.5 });
        Assert.Equal((200, 150), (t.OutWidth, t.OutHeight));
        var (x, y) = t.OutToSource.Apply(0, 0);
        Assert.Equal(0, x, 6); Assert.Equal(0, y, 6);
    }

    [Fact]
    public void Quarter1_SwapsDimensions_AndRotatesClockwise()
    {
        var t = CropGeometry.Compute(400, 300, Full with { Quarter = 1 });
        Assert.Equal((300, 400), (t.OutWidth, t.OutHeight));
        // 順時針 90°：輸出左上角來自來源左下角
        var (x, y) = t.OutToSource.Apply(0, 0);
        Assert.Equal(0, x, 6); Assert.Equal(300, y, 6);
    }

    [Fact]
    public void FlipH_MirrorsX()
    {
        var t = CropGeometry.Compute(400, 300, Full with { FlipH = true });
        Assert.Equal(400 - 10.5, t.OutToSource.Apply(10.5, 5).X, 6);
    }

    [Fact]
    public void Affine_InverseAndThen_RoundTrip()
    {
        var t = CropGeometry.Compute(400, 300, Full with { Cx = 0.4, W = 0.5, H = 0.6, AngleDeg = 12, Quarter = 3, FlipV = true });
        var back = t.OutToSource.Then(t.OutToSource.Inverse());
        var (x, y) = back.Apply(17.25, 33.5);
        Assert.Equal(17.25, x, 6); Assert.Equal(33.5, y, 6);
    }

    [Fact]
    public void Straighten_FullFrame_DoesNotFit_ConstrainShrinksUntilItFits()
    {
        var c = Full with { AngleDeg = 10 };
        Assert.False(CropGeometry.FitsInside(400, 300, c));
        var fixedC = CropGeometry.Constrain(400, 300, c);
        Assert.True(CropGeometry.FitsInside(400, 300, fixedC));
        Assert.Equal(c.W / c.H, fixedC.W / fixedC.H, 6);   // 等比縮小，比例不變
        Assert.True(fixedC.W > 0.7);                        // 不會縮過頭
    }

    [Fact]
    public void SmallCropNearEdge_IsMovedInside_NotShrunk()   // LightCraft 的「先移動、再縮小」
    {
        var c = Full with { Cx = 0.95, Cy = 0.5, W = 0.3, H = 0.3, AngleDeg = 5 };
        Assert.False(CropGeometry.FitsInside(400, 300, c));
        var fixedC = CropGeometry.Constrain(400, 300, c);
        Assert.True(CropGeometry.FitsInside(400, 300, fixedC));
        Assert.Equal((0.3, 0.3), (fixedC.W, fixedC.H));     // 大小不變
        Assert.True(fixedC.Cx < 0.95 && fixedC.Cx > 0.7);   // 只往中心移到剛好放得下
    }

    [Theory]
    [InlineData("1x1", false, 400, 300, 1.0)]
    [InlineData("3x2", false, 400, 300, 1.5)]
    [InlineData("3x2", true, 400, 300, 2.0 / 3.0)]
    [InlineData("original", false, 400, 300, 4.0 / 3.0)]
    public void AspectRatio_IsPixelRatio(string key, bool portrait, int w, int h, double expected) =>
        Assert.Equal(expected, CropGeometry.AspectRatio(key, portrait, w, h), 6);

    [Fact]
    public void AspectRatio_Free_IsNaN() => Assert.True(double.IsNaN(CropGeometry.AspectRatio("free", false, 400, 300)));
}
