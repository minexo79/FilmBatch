using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class LensProfileTests
{
    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "FilmBatch-lens-" + Guid.NewGuid().ToString("N"));
        internal Fixture(string calibration, string mount = "T", double crop = 1.5, string lens = "Test 18-55mm")
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "fixture.xml"), $"""
                <lensdatabase>
                  <camera><maker>Testcam</maker><model>TC-1</model><mount>T</mount><cropfactor>1.5</cropfactor></camera>
                  <lens><maker>Testcam</maker><model>{lens}</model><mount>{mount}</mount><cropfactor>{crop.ToString(System.Globalization.CultureInfo.InvariantCulture)}</cropfactor>
                    <calibration>{calibration}</calibration>
                  </lens>
                </lensdatabase>
                """);
        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
    private static RawImage Raw(double focal = 30, double aperture = 4) => new()
    {
        Format = RawFormat.Dng, Width = 101, Height = 101, Cpp = 3, Data = new RawData.F32([]),
        ActiveArea = new(0, 0, 101, 101), Crop = new(0, 0, 101, 101),
        Metadata = new Metadata { Make = "Testcam", Model = "TC-1", LensMake = "Testcam", LensModel = "Test 18-55mm", FocalLength = focal, FNumber = aperture },
    };
    private static void Near(double expected, double actual, double tolerance = 1e-8) => Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(.1, 0)]
    [InlineData(.1, -.032)]
    public void ExternalWarpKeepsEntirePerimeterInsideDefaultCrop(double k1, double k2)
    {
        using var f = new Fixture(FormattableString.Invariant(
            $"<distortion model=\"poly5\" focal=\"30\" k1=\"{k1}\" k2=\"{k2}\"/>"));
        var raw = Raw();
        raw.Crop = new(11, 23, 80, 40);
        LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        // The second profile has inward corners but an outward maximum between
        // the corner and edge midpoint. Checking only eight points misses it.
        foreach (var vertical in new[] { false, true })
        {
            var image = new RgbImage(101, 101);
            for (var y = 0; y < 101; y++)
            for (var x = 0; x < 101; x++)
            for (var ch = 0; ch < 3; ch++)
                image.Data[(y * 101 + x) * 3 + ch] = (vertical ? y : x) / 100f;
            var corrected = RawSourceGeometry.ApplyList3(raw.Opcodes.List3, image);
            for (var y = 23; y <= 62; y++)
            for (var x = 11; x <= 90; x++)
            {
                if (x is not (11 or 90) && y is not (23 or 62)) continue;
                for (var ch = 0; ch < 3; ch++)
                    Assert.InRange(corrected.Data[(y * 101 + x) * 3 + ch],
                        vertical ? .23f - 1e-6f : .11f - 1e-6f,
                        vertical ? .62f + 1e-6f : .90f + 1e-6f);
            }
        }
    }

    [Theory]
    [InlineData(1, 1.15)]
    [InlineData(1.15, 1)]
    public void TcaFramingUsesOutermostChannelWithoutRepeatingEdgePixels(double red, double blue)
    {
        using var f = new Fixture(FormattableString.Invariant(
            $"<tca model=\"linear\" focal=\"30\" kr=\"{red}\" kb=\"{blue}\"/>"));
        var raw = Raw();
        raw.Crop = new(11, 23, 80, 40);
        var data = new float[101 * 101 * 3];
        for (var y = 0; y < 101; y++)
        for (var x = 0; x < 101; x++)
        for (var ch = 0; ch < 3; ch++)
            data[(y * 101 + x) * 3 + ch] = x / 100f;
        raw.Data = new RawData.F32(data);
        raw.Black = BlackLevel.Uniform(0);
        raw.White = [1];
        LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var corrected = RawSourceGeometry.Develop(raw, DemosaicMethod.Bilinear);
        Assert.Equal((80, 40), (corrected.Width, corrected.Height));
        // Uniform pre-warp scale is 1/1.15 for ALL channels, not independent
        // channel clamps. The outer channel reaches x=11 and x=90 exactly.
        var channelRatios = new[] { red / 1.15, 1 / 1.15, blue / 1.15 };
        for (var x = 0; x < 80; x++)
        for (var ch = 0; ch < 3; ch++)
        {
            var expected = (50.5 + (x - 39.5) * channelRatios[ch]) / 100;
            Near(expected, corrected.Data[x * 3 + ch], 1e-6);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ExternalFramingPreservesBinnedEdgeSamples(int k)
    {
        using var f = new Fixture("<tca model=\"linear\" focal=\"30\" kr=\"1\" kb=\"1.15\"/>");
        var data = new ushort[101 * 101];
        for (var y = 0; y < 101; y++)
        for (var x = 0; x < 101; x++)
            data[y * 101 + x] = (ushort)(1024 + (x - 11) / k * 32 + (y - 23) / k * 16);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = 101, Height = 101, Cpp = 1,
            Data = new RawData.U16(data), Cfa = Cfa.Bayer("RGGB"),
            ActiveArea = new(0, 0, 101, 101), Crop = new(11, 23, 80, 40),
            Black = BlackLevel.Uniform(0), White = [4096], Bits = 12, Metadata = Raw().Metadata,
        };
        LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var (image, _, region, _) = RawSourceLoader.DevelopBinned(raw, k)!.Value;
        var corrected = RawSourceGeometry.ApplyLensChainBinned(raw.Opcodes.List3, image, region, k, 101, 101);
        Assert.Equal((80 / k, 40 / k), (corrected.Width, corrected.Height));
        var midX = (corrected.Width - 1) / 2d;
        var midY = (corrected.Height - 1) / 2d;
        for (var y = 0; y < corrected.Height; y++)
        for (var x = 0; x < corrected.Width; x++)
        for (var ch = 0; ch < 3; ch++)
        {
            var ratio = ch == 2 ? 1 : 1 / 1.15;
            var sx = midX + (x - midX) * ratio;
            var sy = midY + (y - midY) * ratio;
            Near((1024 + sx * 32 + sy * 16) / 4096,
                corrected.Data[(y * corrected.Width + x) * 3 + ch], 1e-6);
        }
    }

    private static double Ratio(WarpRectilinear op, double r, int channel = 1)
    {
        // Compare at a physical half-diagonal radius, not the opcode's pixel-centre units.
        r *= 101.0 / 100;
        var k = op.Planes[channel]; var r2 = r * r;
        return k[0] + r2 * (k[1] + r2 * (k[2] + r2 * k[3]));
    }

    [Fact]
    public void Poly3InterpolatesFocalAndRespectsCalibrationRadius()
    {
        using var f = new Fixture("""
            <distortion model="poly3" focal="20" k1="-0.02"/>
            <distortion model="poly3" focal="40" k1="0.06"/>
            """);
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var op = Assert.IsType<WarpRectilinear>(Assert.Single(raw.Opcodes.List3));
        // Midpoint k=.02. Independently solving s·(.98 + .065·(s·100/101)²)=1
        // gives the largest scale that retains the pixel-centre corners.
        const double scale = .9624427146262926;
        Near(scale * (.98 + .02 * 3.25 * .25 * scale * scale), Ratio(op, .5));
        // Applying twice preserves verified correction; no double warp.
        LensProfiles.ApplyFallback(raw, f.DirectoryPath); Assert.Single(raw.Opcodes.List3);
    }

    [Fact]
    public void Poly5AndTcaAreComposedIntoThreePlanes()
    {
        using var f = new Fixture("""
            <distortion model="poly5" focal="30" k1="-0.02" k2="0.001"/>
            <tca model="linear" focal="30" kr="1.001" kb="0.999"/>
            """);
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var op = Assert.IsType<WarpRectilinear>(Assert.Single(raw.Opcodes.List3));
        var expected = 1 - .02 * 3.25 * .25 + .001 * 3.25 * 3.25 * .0625;
        Near(expected, Ratio(op, .5)); Near(expected * 1.001, Ratio(op, .5, 0)); Near(expected * .999, Ratio(op, .5, 2));
    }

    [Fact]
    public void PtLensAndPoly3TcaFollowIndependentNumericGrid()
    {
        using var f = new Fixture("""
            <distortion model="ptlens" focal="30" a="0.001" b="-0.002" c="0.001"/>
            <tca model="poly3" focal="30" vr="1" vb="1" cr="0.0001" cb="-0.0001" br="0.0001" bb="-0.0001"/>
            """);
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var op = Assert.IsType<WarpRectilinear>(Assert.Single(raw.Opcodes.List3));
        // Solve the unfitted Lensfun model's red corner for unit coverage.
        const double scale = .9984152223787026;
        for (var i = 1; i <= 100; i++)
        {
            var r = i / 100d; var l = scale * r * Math.Sqrt(3.25);
            var distortion = .001 * l * l * l - .002 * l * l + .001 * l + 1;
            var landed = l * distortion;
            var red = scale * distortion * (1 + .0001 * landed + .0001 * landed * landed);
            Assert.True(Math.Abs(r * (Ratio(op, r, 0) - red)) <= .001);
        }
    }

    [Fact]
    public void VignetteInterpolatesApertureInStopsAndInvertsAttenuation()
    {
        using var f = new Fixture("""
            <vignetting model="pa" focal="30" aperture="2" distance="1000" k1="-0.2"/>
            <vignetting model="pa" focal="30" aperture="8" distance="1000" k1="-0.1"/>
            """);
        var raw = Raw(aperture: 4); LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var op = Assert.IsType<FixVignetteRadial>(Assert.Single(raw.Opcodes.List3));
        for (var i = 0; i <= 100; i++)
        {
            var r2 = i / 100d; var polynomial = 0d;
            for (var j = 4; j >= 0; j--) polynomial = polynomial * r2 + op.K[j];
            var expected = 1 / (1 - .15 * r2);
            Assert.True(Math.Abs((1 + r2 * polynomial) / expected - 1) <= .01);
        }
    }

    [Fact]
    public void Recorded35mmCropModeScalesRadius()
    {
        using var f = new Fixture("<distortion model=\"poly5\" focal=\"30\" k1=\"-0.02\"/>");
        var raw = Raw(); raw.Metadata.FocalLength35mm = 90;
        LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var op = Assert.IsType<WarpRectilinear>(Assert.Single(raw.Opcodes.List3));
        Near(1 - .02 * 3.25 * .25 * .25, Ratio(op, .5));
    }

    [Theory]
    [InlineData("Other", "TC-1", "Test 18-55mm")]
    [InlineData("Testcam", "Other", "Test 18-55mm")]
    [InlineData("Testcam", "TC-1", "Test 18-50mm")]
    public void MetadataMismatchDoesNotApply(string make, string model, string lens)
    {
        using var f = new Fixture("<distortion model=\"poly3\" focal=\"30\" k1=\"-0.02\"/>");
        var raw = Raw(); raw.Metadata.Make = make; raw.Metadata.Model = model; raw.Metadata.LensModel = lens;
        LensProfiles.ApplyFallback(raw, f.DirectoryPath); Assert.Empty(raw.Opcodes.List3);
    }

    [Theory]
    [InlineData("Other", 1.5)]
    [InlineData("T", 2.0)]
    public void MountOrInsufficientCalibrationCoverageDoesNotApply(string mount, double crop)
    {
        using var f = new Fixture("<distortion model=\"poly3\" focal=\"30\" k1=\"-0.02\"/>", mount, crop);
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath); Assert.Empty(raw.Opcodes.List3);
    }

    [Fact]
    public void VerifiedEmbeddedCorrectionWinsBeforeDatabaseIsOpened()
    {
        var raw = Raw(); var embedded = new WarpRectilinear([[1, -.1, 0, 0, 0, 0]], [.5, .5]);
        raw.Opcodes.List3.Add(embedded);
        LensProfiles.ApplyFallback(raw, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Same(embedded, Assert.Single(raw.Opcodes.List3));
    }

    [Theory]
    [InlineData("<!DOCTYPE lensdatabase [<!ENTITY x SYSTEM 'file:///this-file-must-never-be-opened'>]><lensdatabase>&x;</lensdatabase>")]
    [InlineData("<!DOCTYPE lensdatabase [<!ENTITY x 'internal'>]><lensdatabase>&x;</lensdatabase>")]
    public void EntitiesAreRejected(string xml)
    {
        using var f = new Fixture(""); File.WriteAllText(Path.Combine(f.DirectoryPath, "fixture.xml"), xml);
        var raw = Raw(); Assert.Throws<System.Xml.XmlException>(() => LensProfiles.ApplyFallback(raw, f.DirectoryPath));
        Assert.Empty(raw.Opcodes.List3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableDatabaseFallsBackToUncorrectedSourceWithWarning(bool missing)
    {
        using var f = new Fixture("");
        var directory = missing ? Path.Combine(f.DirectoryPath, "missing") : f.DirectoryPath;
        if (!missing) File.WriteAllText(Path.Combine(f.DirectoryPath, "fixture.xml"), "<lensdatabase><camera>");
        var path = Path.Combine(f.DirectoryPath, "photo.dng");
        File.WriteAllBytes(path, DngWriter.WriteRenderedDng(16, 12, Enumerable.Repeat(.18f, 16 * 12 * 3).ToArray(),
            FilmBatch.Raw.Color.ColorSpaces.Srgb, new Metadata()));
        var plain = FilmBatch.Engine.Imaging.ImageLoader.Load(path, 200, new(true, false, false, null));
        var fallback = FilmBatch.Engine.Imaging.ImageLoader.Load(path, 200, new(true, false, false, directory));
        Assert.Null(plain.LensDatabaseWarning);
        Assert.False(string.IsNullOrEmpty(fallback.LensDatabaseWarning));
        Assert.Equal(plain.Rgba16, fallback.Rgba16);
        // 縮小後的來源保留警告。
        Assert.Equal(fallback.LensDatabaseWarning, fallback.Downscale(8).LensDatabaseWarning);
    }

    [Fact]
    public void OfficialDoctypeHeaderIsIgnoredWithoutFetching()
    {
        // 官方資料庫每個檔案都以外部 DTD 宣告開頭；必須可讀，且不得嘗試取得該 DTD。
        using var f = new Fixture("<distortion model=\"poly3\" focal=\"30\" k1=\"-0.02\"/>");
        var path = Path.Combine(f.DirectoryPath, "fixture.xml");
        File.WriteAllText(path, "<!DOCTYPE lensdatabase SYSTEM 'https://invalid.example/never'>\n" + File.ReadAllText(path));
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        Assert.IsType<WarpRectilinear>(Assert.Single(raw.Opcodes.List3));
    }

    [Fact]
    public void ExcessiveDepthIsRejectedWithoutChangingOpcodes()
    {
        using var f = new Fixture("");
        File.WriteAllText(Path.Combine(f.DirectoryPath, "fixture.xml"), "<lensdatabase>" + string.Concat(Enumerable.Repeat("<a>", 40)) + string.Concat(Enumerable.Repeat("</a>", 40)) + "</lensdatabase>");
        var raw = Raw(); Assert.Throws<InvalidDataException>(() => LensProfiles.ApplyFallback(raw, f.DirectoryPath)); Assert.Empty(raw.Opcodes.List3);
    }

    [Fact]
    public void FoldingAndNonFiniteProfilesAreRejected()
    {
        using var f = new Fixture("<distortion model=\"poly5\" focal=\"30\" k1=\"-2\"/>");
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath); Assert.Empty(raw.Opcodes.List3);
        File.WriteAllText(Path.Combine(f.DirectoryPath, "fixture.xml"), File.ReadAllText(Path.Combine(f.DirectoryPath, "fixture.xml")).Replace("-2", "NaN"));
        LensProfiles.ApplyFallback(raw, f.DirectoryPath); Assert.Empty(raw.Opcodes.List3);
    }
    [Theory]
    [InlineData("Test 1-855mm")]
    [InlineData("Test 18-55mm f/14")]
    public void NumericLensNamesCannotCollide(string exifLens)
    {
        using var f = new Fixture("<distortion model=\"poly3\" focal=\"30\" k1=\"-0.02\"/>", lens: "Test 18-55mm f/1.4");
        var raw = Raw(); raw.Metadata.LensModel = exifLens;
        LensProfiles.ApplyFallback(raw, f.DirectoryPath); Assert.Empty(raw.Opcodes.List3);
    }

    [Fact]
    public void AmbiguousProfilesDoNotApplyByFileOrder()
    {
        using var f = new Fixture("<distortion model=\"poly3\" focal=\"30\" k1=\"-0.02\"/>");
        File.Copy(Path.Combine(f.DirectoryPath, "fixture.xml"), Path.Combine(f.DirectoryPath, "duplicate.xml"));
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath); Assert.Empty(raw.Opcodes.List3);
    }
    [Theory]
    [InlineData(0, 0, 101, 101)]
    [InlineData(11, 23, 75, 61)]
    public void LensfunWarpUsesPixelEdgeRadius(int cropX, int cropY, int cropWidth, int cropHeight)
    {
        using var f = new Fixture("<distortion model=\"poly3\" focal=\"30\" k1=\"-0.02\"/>");
        var raw = Raw();
        raw.Crop = new(cropX, cropY, cropWidth, cropHeight);
        LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var image = new FilmBatch.Raw.Raster.RgbImage(101, 101);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var i = (y * image.Width + x) * 3;
            image.Data[i] = image.Data[i + 1] = image.Data[i + 2] = x / 100f;
        }
        var corrected = RawSourceGeometry.ApplyList3(raw.Opcodes.List3, image);
        // Lensfun's optical centre and physical radius belong to the default crop,
        // even though the executable List3 chain operates on the full active image.
        var px = cropX + cropWidth / 4; var py = cropY + cropHeight / 2;
        var cx = cropX + cropWidth / 2d; var cy = cropY + cropHeight / 2d;
        var radius = double.Hypot(cropWidth, cropHeight) / 2;
        var dx = px + .5 - cx; var dy = py + .5 - cy;
        var r2 = (dx * dx + dy * dy) / (radius * radius);
        var factor = 1.02 - .02 * 3.25 * r2;
        var expected = (cx - .5 + dx * factor) / 100;
        Assert.InRange(Math.Abs(corrected.Data[(py * 101 + px) * 3] - expected), 0, 1e-6);
    }
    [Theory]
    [InlineData(0, 0, 101, 101)]
    [InlineData(11, 23, 75, 61)]
    public void ExternalVignetteUsesWarpedSensorPosition(int cropX, int cropY, int cropWidth, int cropHeight)
    {
        using var f = new Fixture("""
            <distortion model="poly3" focal="30" k1="-0.02"/>
            <vignetting model="pa" focal="30" aperture="4" distance="1000" k1="-0.2"/>
            """);
        var raw = Raw();
        raw.Crop = new(cropX, cropY, cropWidth, cropHeight);
        LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        var image = new FilmBatch.Raw.Raster.RgbImage(101, 101);
        Array.Fill(image.Data, .25f);
        var corrected = RawSourceGeometry.ApplyList3(raw.Opcodes.List3, image);
        var px = cropX + cropWidth / 4; var py = cropY + cropHeight / 3;
        var cx = cropX + cropWidth / 2d; var cy = cropY + cropHeight / 2d;
        var radius = double.Hypot(cropWidth, cropHeight) / 2;
        var dx = px + .5 - cx; var dy = py + .5 - cy;
        var r2 = (dx * dx + dy * dy) / (radius * radius);
        var warp = 1.02 - .02 * 3.25 * r2;
        var sourceRadius2 = r2 * warp * warp;
        var expected = .25 / (1 - .2 * sourceRadius2);
        Assert.InRange(Math.Abs(corrected.Data[(py * 101 + px) * 3] - expected), 0, 1e-5);
    }



    [Fact]
    public void OversizedXmlIsRejectedBeforeParsing()
    {
        using var f = new Fixture("");
        using (var stream = File.OpenWrite(Path.Combine(f.DirectoryPath, "fixture.xml"))) stream.SetLength((16 << 20) + 1);
        var raw = Raw(); Assert.Throws<InvalidDataException>(() => LensProfiles.ApplyFallback(raw, f.DirectoryPath));
        Assert.Empty(raw.Opcodes.List3);
    }

    [Fact]
    public void ParsedDatabaseIsCachedUntilAFileChanges()
    {
        using var f = new Fixture("""<distortion model="poly3" focal="30" k1="0.02"/>""");
        var path = Path.Combine(f.DirectoryPath, "fixture.xml");
        var first = Raw(); LensProfiles.ApplyFallback(first, f.DirectoryPath);
        var k1 = Ratio(Assert.IsType<WarpRectilinear>(Assert.Single(first.Opcodes.List3)), .5);

        // Same name, size and timestamp: the cached parse is reused (content is not re-read).
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("k1=\"0.02\"", "k1=\"0.04\""));
        File.SetLastWriteTimeUtc(path, stamp);
        var cached = Raw(); LensProfiles.ApplyFallback(cached, f.DirectoryPath);
        Near(k1, Ratio(Assert.IsType<WarpRectilinear>(Assert.Single(cached.Opcodes.List3)), .5));

        // A real edit changes the timestamp and is picked up.
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));
        var edited = Raw(); LensProfiles.ApplyFallback(edited, f.DirectoryPath);
        Assert.NotEqual(k1, Ratio(Assert.IsType<WarpRectilinear>(Assert.Single(edited.Opcodes.List3)), .5));
    }

    [Fact]
    public void BrokenDatabaseIsReportedEveryTimeAfterACachedParse()
    {
        using var f = new Fixture("""<distortion model="poly3" focal="30" k1="0.02"/>""");
        var raw = Raw(); LensProfiles.ApplyFallback(raw, f.DirectoryPath);
        Assert.Single(raw.Opcodes.List3);
        File.WriteAllText(Path.Combine(f.DirectoryPath, "broken.xml"), "<lensdatabase>");
        Assert.ThrowsAny<System.Xml.XmlException>(() => LensProfiles.ApplyFallback(Raw(), f.DirectoryPath));
        Assert.ThrowsAny<System.Xml.XmlException>(() => LensProfiles.ApplyFallback(Raw(), f.DirectoryPath));
    }

}
