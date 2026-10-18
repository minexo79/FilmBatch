// 移植自 LightCraft crates/raw/src/color.rs（DNG 色彩模型測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Tests.Color;

public sealed class CameraColorTests
{
    private static readonly Mat3 A = new(0.9, 0.2, -0.15, -0.3, 1.25, 0.08, 0.02, -0.12, 0.85);
    private static readonly Mat3 B = new(0.7, 0.3, -0.1, -0.35, 1.3, 0.1, 0.05, -0.2, 1);
    private static ColorData Single() => new() { Illuminant = [21, 0], ColorMatrix = [A, null] };
    private static ColorData Dual() => new() { Illuminant = [17, 21], ColorMatrix = [A, B] };
    private static RawImage Raw(ColorData color) => new() { Format = RawFormat.Dng, Width = 1, Height = 1, Cpp = 1, Data = new RawData.U16([0]), Color = color };
    private static void Close((double X, double Y, double Z) a, (double X, double Y, double Z) b, double tolerance)
    {
        Assert.True(Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance && Math.Abs(a.Z - b.Z) < tolerance, $"{a} != {b}");
    }

    [Fact]
    public void NeutralMapsToWhiteAndPrimariesAreRecovered()
    {
        var t = CameraColor.CameraTransform(Raw(Single()), ColorSpaces.D65);
        Assert.False(t.MatrixIsFallback);
        var cameraRed = A.Apply(ColorSpaces.Rec2020.ToXyz().Apply(1, 0, 0));
        var red = t.Matrix.Apply(cameraRed.X * t.Wb[0], cameraRed.Y * t.Wb[1], cameraRed.Z * t.Wb[2]);
        Close((red.X / red.X, red.Y / red.X, red.Z / red.X), (1, 0, 0), 1e-6);
        var n = A.Apply(ColorSpaces.D65.ToXyz());
        var white = t.Matrix.Apply(n.X * t.Wb[0], n.Y * t.Wb[1], n.Z * t.Wb[2]);
        Close((white.X / white.Y, 1, white.Z / white.Y), (1, 1, 1), 1e-6);
        Close(t.Matrix.Apply(1, 1, 1), (1, 1, 1), 1e-12);
        Assert.Equal(1f, t.Wb.Min());
    }

    [Fact]
    public void DualIlluminantsUseInverseTemperatureAndClamp()
    {
        var c = Dual();
        Assert.InRange(CameraColor.IlluminantWeight(c, Cct.TempTintToXy(2856, 0)), 0.99, 1);
        Assert.InRange(CameraColor.IlluminantWeight(c, Cct.TempTintToXy(6504, 0)), 0, 0.01);
        Assert.Equal(0, CameraColor.IlluminantWeight(c, Cct.TempTintToXy(10000, 0)));
        var mid = Cct.TempTintToXy(4000, 0);
        var g = CameraColor.IlluminantWeight(c, mid);
        Assert.InRange(g, 0.47, 0.51);
        var m = CameraColor.XyzToCamera(c, mid);
        Assert.True(Math.Abs(m[0, 0] - (g * 0.9 + (1 - g) * 0.7)) < 1e-12);
    }

    [Fact]
    public void NeutralRoundTripsAndAsShotTagsTakePrecedence()
    {
        var c = Dual();
        c.AnalogBalance = [1, 1.05, 0.98];
        foreach (var temp in new[] { 2600.0, 3200, 4500, 5500, 7000, 9000 })
        foreach (var tint in new[] { -10.0, 0, 15 })
        {
            var white = Cct.TempTintToXy(temp, tint);
            var back = CameraColor.NeutralToXy(c, CameraColor.CameraNeutral(c, white));
            Assert.True(Math.Abs(white.X - back.X) < 1e-6 && Math.Abs(white.Y - back.Y) < 1e-6);
        }
        c.AsShotNeutral = CameraColor.CameraNeutral(c, Cct.TempTintToXy(3300, 5));
        var raw = Raw(c);
        var (t, ti) = Cct.XyToTempTint(CameraColor.AsShotWhiteXy(raw));
        Assert.InRange(t, 3295, 3305);
        Assert.InRange(ti, 4.5, 5.5);
        Assert.Equal(CameraColor.AsShotWhiteXy(raw), CameraColor.AsShotWhiteXyOf(raw.Info()));
        c.AsShotWhiteXy = ColorSpaces.D50;
        Assert.Equal(ColorSpaces.D50, CameraColor.AsShotWhiteOf(c, [2, 1, 1.5f]));
    }

    [Fact]
    public void ForwardMatrixAndInverseColorMatrixAgree()
    {
        var n = A.Apply(ColorSpaces.D65.ToXyz());
        var fm = ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50).Mul(A.Inverse()!.Value).Mul(Mat3.Diag(n.X, n.Y, n.Z));
        var w = fm.Apply(1, 1, 1);
        var s = 1 / w.Y;
        fm = Mat3.FromRows(fm.ToArray().Select(v => v * s).ToArray());
        var cm = CameraColor.CameraTransformOf(Single(), ColorSpaces.D65);
        var c = Single();
        c.ForwardMatrix = [fm, null];
        var forward = CameraColor.CameraTransformOf(c, ColorSpaces.D65);
        for (var r = 0; r < 3; r++) for (var col = 0; col < 3; col++) Assert.True(Math.Abs(cm.Matrix[r, col] - forward.Matrix[r, col]) < 1e-9);
        Assert.Equal(cm.Wb, forward.Wb);
    }

    [Fact]
    public void CalibrationAndAnalogBalanceScaleNeutral()
    {
        var c = Single();
        var before = CameraColor.CameraNeutral(c, ColorSpaces.D65);
        c.CameraCalibration = [Mat3.Diag(1, 0.5, 1), null];
        var after = CameraColor.CameraNeutral(c, ColorSpaces.D65);
        Assert.True(Math.Abs(after[1] / after[0] - 0.5 * before[1] / before[0]) < 1e-9);
    }

    [Fact]
    public void MissingMatrixUsesSrgbFallbackAndVendorWhite()
    {
        var raw = Raw(new ColorData());
        raw.WbMultipliers = [2, 1, 1.5f];
        var t = CameraColor.CameraTransform(raw, CameraColor.AsShotWhiteXy(raw));
        Assert.True(t.MatrixIsFallback);
        Assert.True(MathF.Abs(t.Wb[0] / t.Wb[1] - 2) < 1e-3 && MathF.Abs(t.Wb[2] / t.Wb[1] - 1.5f) < 1e-3);
        var daylight = CameraColor.CameraToRec2020(raw, ColorSpaces.D65);
        var expected = ColorSpaces.Srgb.ToSpace(ColorSpaces.Rec2020);
        for (var r = 0; r < 3; r++) for (var col = 0; col < 3; col++) Assert.True(Math.Abs(daylight.Matrix[r, col] - expected[r, col]) < 1e-6);
        Assert.Equal(new[] { 1f, 1f, 1f }, daylight.Wb);
        Assert.Equal(ColorSpaces.D65, CameraColor.AsShotWhiteOf(new ColorData(), [float.NaN, 1, 1]));
    }

    [Theory]
    [InlineData(21, 6504.0)]
    [InlineData(17, 2856.0)]
    [InlineData(0, null)]
    [InlineData(255, null)]
    public void IlluminantCodes(int code, double? expected) => Assert.Equal(expected, CameraColor.IlluminantTemperature((ushort)code));

    [Fact]
    public void RebalanceAndPickerUseTheCameraModel()
    {
        var c = Dual();
        var from = Cct.TempTintToXy(5500, 0);
        var to = Cct.TempTintToXy(3200, 10);
        var a = CameraColor.CameraTransformOf(c, from);
        var n = CameraColor.CameraNeutral(c, to);
        var developed = a.Matrix.Apply(n[0] * a.Wb[0], n[1] * a.Wb[1], n[2] * a.Wb[2]);
        Assert.True(developed.X > 1.2 * developed.Z);
        var output = CameraColor.Rebalance(c, from, to)!.Value.Apply(developed);
        Close((output.X / output.Y, 1, output.Z / output.Y), (1, 1, 1), 1e-6);
        var same = CameraColor.Rebalance(c, from, from)!.Value;
        for (var r = 0; r < 3; r++) for (var col = 0; col < 3; col++) Assert.True(Math.Abs(same[r, col] - (r == col ? 1 : 0)) < 1e-9);
        var back = CameraColor.NeutralWhite(c, from, [developed.X, developed.Y, developed.Z])!.Value;
        Assert.True(Math.Abs(back.X - to.X) < 1e-6 && Math.Abs(back.Y - to.Y) < 1e-6);
        Assert.Null(CameraColor.Rebalance(new ColorData(), from, to));
        Assert.Null(CameraColor.NeutralWhite(c, from, [-1, 0.5, 0.5]));
        Assert.Null(CameraColor.NeutralWhite(new ColorData(), from, [developed.X, developed.Y, developed.Z]));
    }

    [Fact]
    public void GreyWorldIgnoresClippedAndDarkPixels()
    {
        var image = new RgbImage(20, 20);
        for (var y = 0; y < 20; y++) for (var x = 0; x < 20; x++)
        {
            var v = 0.2f + 0.02f * x;
            var i = (y * 20 + x) * 3;
            image.Data[i] = v * 0.5f; image.Data[i + 1] = v; image.Data[i + 2] = v * 0.8f;
        }
        var m = CameraColor.GreyWorld(image);
        Assert.True(MathF.Abs(m[0] - 2) < 1e-3 && MathF.Abs(m[1] - 1) < 1e-6 && MathF.Abs(m[2] - 1.25f) < 1e-3);
        Assert.Equal(new[] { 1f, 1f, 1f }, CameraColor.GreyWorld(new RgbImage(1, 1, [1, 1, 1])));
    }
}
