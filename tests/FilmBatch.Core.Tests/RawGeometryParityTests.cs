using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class RawGeometryParityTests
{
    private static RawImage Raw(int w, int h, List<Opcode>? ops = null) => new()
    {
        Format = RawFormat.Dng, Width = w, Height = h, Cpp = 3, Data = new RawData.F32([]),
        ActiveArea = new(0, 0, w, h), Crop = new(0, 0, w, h), Opcodes = new() { List3 = ops ?? [] },
    };
    private static void Near(double expected, double actual, double tolerance = 1e-6) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void ReciprocalScaleChainSamplesCheckerboardOnlyOnce()
    {
        var image = new RgbImage(17, 13);
        for (var i = 0; i < image.Data.Length; i++) image.Data[i] = (i / 3 + i / (17 * 3)) % 2;
        var original = (float[])image.Data.Clone();
        var result = RawSourceGeometry.ApplyList3([
            new WarpRectilinear([[1.25, 0, 0, 0, 0, 0]], [.5, .5]),
            new WarpRectilinear([[.8, 0, 0, 0, 0, 0]], [.5, .5]),
        ], image);
        for (var i = 0; i < original.Length; i++) Near(original[i], result.Data[i]);
        Assert.Equal(original, image.Data);
    }

    [Fact]
    public void InterleavedVignetteIsEvaluatedAtItsOwnStageExactlyOnce()
    {
        var image = new RgbImage(11, 11, Enumerable.Repeat(.05f, 11 * 11 * 3).ToArray());
        var result = RawSourceGeometry.ApplyList3([
            new WarpRectilinear([[1.25, 0, 0, 0, 0, 0]], [.5, .5]),
            new FixVignetteRadial([.2, 0, 0, 0, 0], [.5, .5]),
            new WarpRectilinear([[.8, 0, 0, 0, 0, 0]], [.5, .5]),
        ], image);
        for (var y = 0; y < 11; y++)
            for (var x = 0; x < 11; x++)
            {
                // B maps output index about (5,5) by .8. Vignette normalizes by edge half-diagonal.
                var sx = 5 + .8 * (x - 5); var sy = 5 + .8 * (y - 5);
                var r2 = ((sx - 5) * (sx - 5) + (sy - 5) * (sy - 5)) / (2 * 5.5 * 5.5);
                Near(.05 * (1 + .2 * r2), result.Data[(y * 11 + x) * 3], 1e-7);
            }
    }

    [Theory]
    [InlineData(Orientation.Normal)]
    [InlineData(Orientation.FlipH)]
    [InlineData(Orientation.Rotate180)]
    [InlineData(Orientation.FlipV)]
    [InlineData(Orientation.Transpose)]
    [InlineData(Orientation.Rotate90)]
    [InlineData(Orientation.Transverse)]
    [InlineData(Orientation.Rotate270)]
    public void GainAndGeometryAgreeOnIndependentGridForEveryOrientation(Orientation orientation)
    {
        var raw = Raw(20, 16, [
            new WarpRectilinear([[1.1, 0, 0, 0, 0, 0]], [.25, .5]),
            new WarpRectilinear([[.9, 0, 0, 0, 0, 0]], [.75, .5]),
        ]);
        raw.Crop = new(3, 2, 12, 8); raw.Orientation = orientation;
        raw.Color.Profile.GainTableMap = new GainTableMap
        {
            PointsV = 1, PointsH = 2, PointsN = 1, SpacingV = 1, SpacingH = 1,
            OriginV = 0, OriginH = 0, Weights = [1f, 0, 0, 0, 0], Gamma = 1, Gains = [1, 3],
        };
        var geometry = RawSourceGeometry.Create(raw);
        var image = new RgbImage(5, 4, Enumerable.Repeat(.05f, 5 * 4 * 3).ToArray());
        Assert.True(CameraLocalTone.Apply(image, raw, true, geometry));
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var u = (x + .5) / image.Width; var v = (y + .5) / image.Height;
                // Independent EXIF inverse table, followed by hand-expanded affine warp composition.
                var (mu, mv) = orientation switch
                {
                    Orientation.FlipH => (1 - u, v), Orientation.Rotate180 => (1 - u, 1 - v),
                    Orientation.FlipV => (u, 1 - v), Orientation.Transpose => (v, u),
                    Orientation.Rotate90 => (v, 1 - u), Orientation.Transverse => (1 - v, 1 - u),
                    Orientation.Rotate270 => (1 - v, u), _ => (u, v),
                };
                var ix = 3 + mu * 12 - .5; var iy = 2 + mv * 8 - .5;
                var bx = 14.25 + .9 * (ix - 14.25); var by = 7.5 + .9 * (iy - 7.5);
                var sx = 4.75 + 1.1 * (bx - 4.75); var sy = 7.5 + 1.1 * (by - 7.5);
                var ax = (sx + .5) / 20; var ay = (sy + .5) / 16;
                var mapped = geometry.Active(u, v);
                Near(ax, mapped.X, 1e-12); Near(ay, mapped.Y, 1e-12);
                Near(.05 * (1 + 2 * Math.Clamp(ax, 0, 1)), image.Data[(y * image.Width + x) * 3], 1e-7);
            }
    }

    [Fact]
    public void BinnedGeometryDiscardsIncompleteCropBlocks()
    {
        var raw = Raw(20, 16); raw.Crop = new(3, 2, 13, 9);
        var geometry = RawSourceGeometry.Create(raw, 4);
        var centre = geometry.Active(.5, .5);
        Near((3 + 6d) / 20, centre.X, 1e-12); Near((2 + 4d) / 16, centre.Y, 1e-12);
        var finalCentre = geometry.Active(2.5 / 3, 1.5 / 2);
        Near((3 + 10d) / 20, finalCentre.X, 1e-12); Near((2 + 6d) / 16, finalCentre.Y, 1e-12);
    }

    [Fact]
    public void SnapshotDoesNotRetainMutableRawMetadataOrCoefficients()
    {
        double[] coefficients = [1.2, 0, 0, 0, 0, 0]; double[] center = [.5, .5];
        var raw = Raw(10, 10, [new WarpRectilinear([coefficients], center)]);
        var geometry = RawSourceGeometry.Create(raw); var expected = geometry.Active(.7, .6);
        coefficients[0] = 20; center[0] = 0; raw.Crop = new(0, 0, 2, 2); raw.Opcodes.List3.Clear(); raw.ActiveArea = new(0, 0, 2, 2);
        Assert.Equal(expected, geometry.Active(.7, .6));
    }

    [Fact]
    public void NonSpatialOpcodesRetainOrderBetweenLensChains()
    {
        var input = new RgbImage(3, 3, Enumerable.Repeat(.2f, 27).ToArray());
        var result = RawSourceGeometry.ApplyList3([
            new WarpRectilinear([[1, 0, 0, 0, 0, 0]], [.5, .5]),
            new MapPolynomial(new Area(0, 0, 3, 3, 0, 3, 1, 1), [0, 2]),
            new WarpRectilinear([[1, 0, 0, 0, 0, 0]], [.5, .5]),
        ], input);
        foreach (var pixel in result.Data) Near(.4, pixel);
        foreach (var pixel in input.Data) Near(.2, pixel);
    }

    [Fact]
    public void ExcessiveList3LensChainIsRejectedOnFullAndBinnedPaths()
    {
        var image = new RgbImage(2, 2, Enumerable.Repeat(.2f, 12).ToArray());
        var lens = new FixVignetteRadial([0, 0, 0, 0, 0], [.5, .5]);
        var ops = Enumerable.Repeat<Opcode>(lens, 9).ToArray();
        Assert.Equal(RawErrorKind.Limit, Assert.Throws<RawException>(() => RawSourceGeometry.ApplyList3(ops, image)).Kind);
        Assert.Equal(RawErrorKind.Limit, Assert.Throws<RawException>(() =>
            RawSourceGeometry.ApplyLensChainBinned(ops, image, new PixelRect(0, 0, 4, 4), 2, 4, 4)).Kind);
    }

    [Fact]
    public void CameraFitUsesFullWr2ChainWithoutRetainingMutableCoefficients()
    {
        double[] coefficients = new double[19]; coefficients[0] = .9; coefficients[18] = 1e6;
        var raw = Raw(20, 20, [
            new WarpRectilinear([[1.1, 0, 0, 0, 0, 0]], [.25, .5]),
            new WarpRectilinear2([coefficients], [.75, .5], false, false),
        ]);
        var lens = CameraPreviewLens.LensGeometryOf(raw.Opcodes.List3);
        Assert.NotNull(lens);
        coefficients[0] = 10;
        var proxy = new RgbImage(10, 10);
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                for (var c = 0; c < 3; c++) proxy.Data[(y * 10 + x) * 3 + c] = x;
        var result = CameraPreviewLens.IntoGeometry(proxy, raw, 2, lens!);
        Assert.NotNull(result);
        // Output x=5 -> sensor index 10.5 -> B 10.875 -> A 11.4875 -> proxy index 5.49375.
        Near(5.49375, result!.Data[(5 * 10 + 5) * 3]);
    }

}
