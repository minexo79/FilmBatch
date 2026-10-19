// 移植自 LightCraft crates/raw/src/{spectral,kelvin_presets}.rs（行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Color;

namespace FilmBatch.Raw.Tests.Color;

public sealed class SpectralKelvinTests
{
    public static IEnumerable<object[]> Names()
    {
        foreach (var row in new (string, string, string)[]
        {
            ("Canon", "Canon EOS 5D Mark III", "EOS 5D Mark III"), ("Canon", "Canon EOS 600D", "EOS 600D"),
            ("Canon", "Canon EOS-1Ds Mark III", "EOS-1Ds Mark III"), ("Canon", "Canon EOS DIGITAL REBEL XTi", "Digital Rebel XTi"),
            ("Canon", "Canon EOS R5m2", "EOS R5m2"), ("Canon", "Canon PowerShot S90", "PowerShot S90"),
            ("NIKON CORPORATION", "NIKON D850", "D850"), ("NIKON CORPORATION", "NIKON Z f", "Z f"),
            ("SONY", "ILCE-7M3", "ILCE-7M3"), ("FUJIFILM", "X-T4", "X-T4"), ("Panasonic", "DC-GX9", "DC-GX9"),
            ("Canon", "Canon EOS 400D DIGITAL", "Digital Rebel XTi"), ("Canon", "Canon EOS Kiss Digital X", "Digital Rebel XTi"),
            ("Canon", "Canon EOS REBEL T3i", "EOS 600D"), ("Canon", "Canon EOS Kiss X5", "EOS 600D"),
            ("Canon", "Canon EOS R5 Mark II", "EOS R5m2"), ("NIKON CORPORATION", "NIKON D800", "D800E"),
            ("NIKON CORPORATION", "NIKON Z 6_2", "Z f"), ("SONY", "ILCE-7RM4A", "ILCE-7RM4"), ("FUJIFILM", "GFX100S", "GFX 100"),
        }) yield return [row.Item1, row.Item2, row.Item3];
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ExifAndJoinedNamesResolveAliases(string make, string model, string expected)
    {
        Assert.Equal(expected, Spectral.Find(make, model)?.Model);
        Assert.Equal(expected, Spectral.FindCamera(make + " " + model)?.Model);
    }

    [Fact]
    public void EveryRowAndAliasCanBeFoundByJoinedName()
    {
        Assert.Equal(52, Spectral.Cameras().Count);
        foreach (var c in Spectral.Cameras())
        {
            Assert.Same(c, Spectral.Find(c.Make, c.Model));
            Assert.Same(c, Spectral.FindCamera(c.Make + " " + c.Model));
            foreach (var m in c.ColorMatrix.Concat(c.ForwardMatrix))
            {
                Assert.All(m.ToArray(), v => Assert.True(double.IsFinite(v) && Math.Abs(v) < 4));
                Assert.NotNull(m.Inverse());
            }
            foreach (var fm in c.ForwardMatrix)
            {
                var white = fm.Apply(1, 1, 1);
                var d50 = ColorSpaces.D50.ToXyz();
                Assert.True(Math.Abs(white.X - d50.X) < 1e-4 && Math.Abs(white.Y - d50.Y) < 1e-4 && Math.Abs(white.Z - d50.Z) < 1e-4);
            }
        }
        foreach (var alias in Spectral.Aliases)
        {
            Assert.NotNull(Spectral.Find(alias.Make, alias.Model));
            Assert.Same(Spectral.Find(alias.Make, alias.Model), Spectral.FindCamera(alias.Make + " " + alias.Model));
        }
        Assert.Equal("D850", Spectral.Find(null, "NIKON D850")?.Model);
        Assert.Equal("ILCE-7M3", Spectral.FindCamera("  ILCE-7M3  ")?.Model);
    }

    [Theory]
    [InlineData("Canon", "Canon EOS 7D")]
    [InlineData("SONY", "ILCE-7M2")]
    [InlineData("OLYMPUS IMAGING CORP.", "E-M5")]
    [InlineData("Canon", "D850")]
    [InlineData("SONY", "X-T4")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void OtherModelsAndWrongMakesDoNotResolve(string? make, string model) => Assert.Null(Spectral.Find(make, model));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SONY")]
    [InlineData("SONY ILCE-7M2")]
    [InlineData("Canon EOS 7D")]
    [InlineData("D850 SONY")]
    [InlineData("OLYMPUS IMAGING CORP. E-M5")]
    public void InvalidJoinedNamesDoNotResolve(string camera) => Assert.Null(Spectral.FindCamera(camera));

    [Fact]
    public void FillPreservesExistingMatricesAndWhiteThroughPreservesOriginalTags()
    {
        var camera = Spectral.Find("SONY", "ILCE-7M3")!;
        var c = new ColorData();
        var white = Spectral.AsShotWhiteThrough(camera, c, [2.2f, 1, 1.6f])!.Value;
        var (t, tint) = Cct.XyToTempTint(white);
        Assert.InRange(t, 3500, 7000); Assert.True(Math.Abs(tint) < 30);
        Assert.False(CameraColor.HasMatrix(c));
        Assert.Null(Spectral.AsShotWhiteThrough(camera, c, null));
        Assert.Null(Spectral.AsShotWhiteThrough(camera, c, [0, 1, 1]));
        Assert.Null(Spectral.AsShotWhiteThrough(camera, c, [float.NaN, 1, 1]));
        Assert.NotNull(Spectral.AsShotWhiteThrough(camera, new ColorData { AsShotNeutral = [0.5, 1, 0.6] }, null));
        Assert.True(camera.Fill(c));
        Assert.Equal(new ushort[] { 17, 21 }, c.Illuminant);
        Assert.Equal(camera.ColorMatrix[0], c.ColorMatrix[0]); Assert.Equal(camera.ColorMatrix[1], c.ColorMatrix[1]);
        Assert.Equal(camera.ForwardMatrix[0], c.ForwardMatrix[0]); Assert.Equal(camera.ForwardMatrix[1], c.ForwardMatrix[1]);
        var own = Mat3.Diag(0.5, 1, 0.7);
        c.ColorMatrix = [own, null]; c.Illuminant = [21, 0];
        Assert.False(camera.Fill(c));
        Assert.Equal(own, c.ColorMatrix[0]); Assert.Null(c.ColorMatrix[1]); Assert.Equal(new ushort[] { 21, 0 }, c.Illuminant);
    }

    private static KelvinPresets Presets() => KelvinPresets.Create([
        (2500, 1336 / 1024f, 1, 3672 / 1024f), (3200, 1720 / 1024f, 1, 2620 / 1024f),
        (4500, 2256 / 1024f, 1, 1880 / 1024f), (6000, 2680 / 1024f, 1, 1544 / 1024f),
        (8500, 3136 / 1024f, 1, 1300 / 1024f)])!;

    [Fact]
    public void PresetReadsOwnTemperatureAndScaleDoesNotMatter()
    {
        var p = Presets();
        foreach (var point in p.Points)
        {
            var (t, tint) = p.TempTint([point.R, point.G, point.B])!.Value;
            Assert.True(Math.Abs(t - point.Kelvin) < 0.5 && Math.Abs(tint) < 1e-6);
            var (scaledT, scaledTint) = p.TempTint([point.R * 2.3f, point.G * 2.3f, point.B * 2.3f])!.Value;
            Assert.True(Math.Abs(scaledT - t) < 1e-3 && Math.Abs(scaledTint - tint) < 1e-4);
        }
    }

    [Fact]
    public void PresetCurveInterpolatesExtendsAndClamps()
    {
        var p = Presets(); var a = p.Points[2]; var b = p.Points[3];
        var (t, tint) = p.TempTint([MathF.Sqrt(a.R * b.R), 1, MathF.Sqrt(a.B * b.B)])!.Value;
        Assert.InRange(t, 4500, 6000); Assert.True(Math.Abs(tint) < 1);
        Assert.InRange(p.TempTint([3.5f, 1, 1.15f])!.Value.Temp, 8500, 50000);
        Assert.InRange(p.TempTint([1.15f, 1, 4])!.Value.Temp, 2000, 2500);
        Assert.Equal(2000, p.TempTint([0.3f, 1, 20])!.Value.Temp);
    }

    [Fact]
    public void GreenSourceGivesPositiveTintAndTiltedTemperature()
    {
        var p = Presets(); var r = 2680 / 1024f; var b = 1544 / 1024f;
        var day = p.TempTint([r, 1, b])!.Value;
        var green = p.TempTint([r * 1.08f, 1, b * 1.08f])!.Value;
        Assert.InRange(green.Tint, 15, 40); Assert.True(green.Temp > day.Temp + 100);
        Assert.True(p.TempTint([r / 1.08f, 1, b / 1.08f])!.Value.Tint < -15);
    }

    [Fact]
    public void BadPresetsAndWhiteMultipliersGiveNothing()
    {
        var p = Presets();
        Assert.Null(p.TempTint([0, 1, 1])); Assert.Null(p.TempTint([float.NaN, 1, 1])); Assert.Null(p.AsShotWhite(null));
        var points = p.Points.ToArray(); (points[1], points[2]) = (points[2], points[1]); Assert.Null(KelvinPresets.Create(points));
        points = p.Points.ToArray(); points[4] = points[3]; Assert.Null(KelvinPresets.Create(points));
        points = p.Points.ToArray(); points[0] = (2500, 0, 1, 1); Assert.Null(KelvinPresets.Create(points));
        Assert.Null(KelvinPresets.AsShotWhite(new ColorData(), [2, 1, 1.5f]));
        Assert.NotNull(KelvinPresets.AsShotWhite(new ColorData { KelvinPresets = p }, [2, 1, 1.5f]));
    }

    [Theory]
    [InlineData("SONY ILCA-99M2", true)] [InlineData("SONY DSC-RX10", true)] [InlineData("SONY ILCA-77M2", true)]
    [InlineData("SONY SLT-A33", true)] [InlineData("ILCA-99M2", true)] [InlineData("Sony ilca-99m2", true)]
    [InlineData("SONY ILCE-7M3", false)] [InlineData("SONY DSC-RX10M4", false)] [InlineData("Canon SLT-A33", false)]
    [InlineData("", false)] [InlineData("SONY", false)]
    public void PresetCoverageUsesMakeAndModel(string camera, bool expected) => Assert.Equal(expected, KelvinPresets.CoversCamera(camera));
}
