// Grain/neutral/Zone survey analysis behavior (W1–W3, Z1–Z2): robust sampling,
// forward-fit WB against the current model, reference handling, auto ambiguity,
// and Zone boundaries. Expectations are hand-computed or independently derived,
// never copied from implementation constants.
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class PhotoAnalysisTests
{
    // ---- fixtures: product types only, no pipeline internals ----------------

    private static SourceImage Sdr(int w, int h, ushort r, ushort g, ushort b, ushort a = 65535)
    {
        var px = new ushort[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a;
        }
        return new SourceImage(w, h, px);
    }

    private static RawImage LinearRaw(int w, int h)
    {
        var full = new PixelRect(0, 0, w, h);
        return new RawImage
        {
            Format = RawFormat.Dng, Width = w, Height = h, Cpp = 3,
            Data = new RawData.F32(new float[w * h * 3]),
            Bits = 16, White = [1f, 1f, 1f], Black = new BlackLevel(),
            ActiveArea = full, Crop = full, Orientation = Orientation.Normal,
            Color = new ColorData { AsShotNeutral = [1.0, 1.0, 1.0] },
            Metadata = new Metadata(),
        };
    }

    private static SourceImage SceneSource(int w, int h, float r, float g, float b, RawWhiteBalance? wb = null)
    {
        var raw = LinearRaw(w, h);
        var data = new float[w * h * 3];
        for (var i = 0; i < data.Length; i += 3)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b;
        }
        var scene = new RawSceneData(new RgbImage(w, h, data), null, wb, RawSourceGeometry.Create(raw));
        var rgba = new ushort[w * h * 4];
        for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 65535;
        return new SourceImage(w, h, rgba) { Scene = scene, RawInfo = raw.Info() };
    }

    private static RawWhiteBalance NeutralModel()
    {
        // Relative-scale ARW-style source (upstream relative_wb): as-shot reads as
        // 6500/0 and MatrixFor adapts with luminance-normalized Bradford — a real
        // forward transform, not identity.
        var raw = LinearRaw(4, 4);
        raw.Format = RawFormat.Arw;
        raw.Color = new ColorData();
        raw.WbMultipliers = [2.0f, 1.0f, 1.5f];
        var developedFor = CameraColor.AsShotWhiteXy(raw);
        return RawWhiteBalance.Create(raw, developedFor, CameraColor.CameraTransform(raw, developedFor), null);
    }

    private static void Near(double expected, double actual, double tolerance = 1e-6) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    private static double SrgbToLinear(ushort v)
    {
        var c = v / 65535.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    // ---- sampling: working-linear values, bounded cost, robustness ---------

    [Fact]
    public void SdrSampleReportsDecodedWorkingLinearRgb()
    {
        var source = Sdr(8, 8, 32768, 16384, 49152);
        var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        Assert.True(sample.CanUse);
        Assert.False(sample.IsSceneLinear);
        Assert.Equal(64, sample.ValidSamples);
        Assert.Equal(0, sample.RejectedSamples);
        Near(SrgbToLinear(32768), sample.Red);
        Near(SrgbToLinear(16384), sample.Green);
        Near(SrgbToLinear(49152), sample.Blue);
        Near(0.2126 * sample.Red + 0.7152 * sample.Green + 0.0722 * sample.Blue, sample.Luminance, 1e-12);
    }

    [Fact]
    public void SamplingIsDeterministicAndBounded()
    {
        var source = Sdr(160, 120, 30000, 20000, 10000);
        var region = new PhotoRegion(0, 0, 1, 1);
        var first = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, region);
        var second = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, region);
        Assert.Equal(first, second);
        Assert.True(first.ValidSamples <= PhotoAnalysis.MaxSamples);
        Assert.True(first.ValidSamples > 1000);
    }

    [Fact]
    public void BlackAndTransparentPixelsAreRejected()
    {
        var px = new ushort[4 * 4 * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = 30000;
            px[i + 3] = 65535;
        }
        // Two black pixels and two transparent pixels; the rest is mid grey.
        px[0] = px[1] = px[2] = 0;
        px[4] = px[5] = px[6] = 0;
        px[11] = 0;
        px[15] = 0;
        var source = new SourceImage(4, 4, px);
        var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        Assert.True(sample.CanUse);
        Assert.Equal(12, sample.ValidSamples);
        Assert.Equal(4, sample.RejectedSamples);
        Near(SrgbToLinear(30000), sample.Red);
    }

    [Fact]
    public void InvalidAndOutOfBoundsRegionsAreUnusable()
    {
        var source = Sdr(8, 8, 30000, 30000, 30000);
        foreach (var region in new[]
        {
            new PhotoRegion(0, 0, 0, 0.5),           // degenerate width
            new PhotoRegion(0, 0, 0.5, -1),          // negative height
            new PhotoRegion(double.NaN, 0, 0.5, 0.5),// non-finite origin
            new PhotoRegion(2, 2, 0.5, 0.5),         // fully outside
        })
        {
            var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, region);
            Assert.False(sample.CanUse);
            Assert.Equal(0, sample.ValidSamples);
        }
    }

    [Fact]
    public void PartiallyClippedRegionSamplesTheOverlap()
    {
        var source = Sdr(8, 8, 30000, 30000, 30000);
        var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0.5, 0.5, 1, 1));
        Assert.True(sample.CanUse);
        Assert.Equal(16, sample.ValidSamples);
    }

    [Fact]
    public void OutlierSpecksDoNotDragTheMean()
    {
        var px = new ushort[8 * 8 * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = 28000;
            px[i + 3] = 65535;
        }
        // One hot-red outlier pixel.
        px[0] = 65535; px[1] = 1000; px[2] = 1000;
        var source = new SourceImage(8, 8, px);
        var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        Assert.True(sample.CanUse);
        Assert.Equal(1, sample.RejectedSamples);
        Near(SrgbToLinear(28000), sample.Red, 1e-9);
        Near(SrgbToLinear(28000), sample.Green, 1e-9);
        Near(SrgbToLinear(28000), sample.Blue, 1e-9);
    }

    [Fact]
    public void FullCodeValueRaisesProcessedClippingProxy()
    {
        var source = Sdr(4, 4, 65535, 30000, 30000);
        var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        Assert.True(sample.CanUse);
        Assert.True(sample.HasProcessedClipping);
        var clean = PhotoAnalysis.Sample(Sdr(4, 4, 30000, 30000, 30000), ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        Assert.False(clean.HasProcessedClipping);
    }

    [Fact]
    public void SceneHdrAboveOneStaysValidForZone()
    {
        var source = SceneSource(4, 4, 2.0f, 2.0f, 2.0f);
        var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        Assert.True(sample.CanUse);
        Assert.True(sample.IsSceneLinear);
        Assert.True(sample.Luminance > 1);
        Assert.False(sample.HasProcessedClipping);
        Assert.NotNull(ZonePlacement.ZoneOf(sample.Luminance));
    }

    // ---- RAW white balance replaces relative basic temperature/tint --------

    [Fact]
    public void RawWhiteBalanceSuppressesRelativeBasicTemperatureAndTint()
    {
        var settings = ModuleCatalog.Defaults
            .With("raw.enabled", true).With("basic.temperature", 40.0).With("basic.tint", -20.0);
        var raw = ModuleParams.From(SceneSource(4, 4, .2f, .2f, .2f, NeutralModel()), settings);
        Assert.Equal(0, raw.Temperature);
        Assert.Equal(0, raw.Tint);
        Assert.True(ModuleParams.UsesRawWhiteBalance(SceneSource(4, 4, .2f, .2f, .2f, NeutralModel()), settings));

        // RAW processing off, no WB model, or an SDR source: relative values still apply.
        foreach (var (source, s) in new[]
        {
            (SceneSource(4, 4, .2f, .2f, .2f, NeutralModel()), settings.With("raw.enabled", false)),
            (SceneSource(4, 4, .2f, .2f, .2f), settings),
            (Sdr(4, 4, 30000, 30000, 30000), settings),
        })
        {
            var p = ModuleParams.From(source, s);
            Assert.Equal(40, p.Temperature);
            Assert.Equal(-20, p.Tint);
            Assert.False(ModuleParams.UsesRawWhiteBalance(source, s));
        }
    }

    // ---- RAW processing defaults on, but only scene-linear sources use it ---

    [Fact]
    public void RawProcessing_DefaultsOn_ButNonRawRendersIgnoreIt()
    {
        var defaults = ModuleCatalog.Defaults;
        Assert.True(defaults.ContainsKey("raw.enabled"));
        Assert.True(defaults.Bool("raw.enabled"));
        Assert.NotEqual(new RawLoadOptions(), RawLoadOptions.FromSettings(defaults));
        var scene = SceneSource(4, 4, .2f, .2f, .2f, NeutralModel());
        Assert.True(ModuleParams.From(scene, defaults).RawProcessingOn);
        Assert.True(ModuleParams.UsesRawWhiteBalance(scene, defaults));

        // SDR sources: the default-on value never turns RAW processing or RAW WB on,
        // so the relative basic temperature/tint still applies and pixels match RAW off.
        var sdr = Sdr(8, 8, 30000, 20000, 12000);
        var edited = defaults.With("basic.exposure", 0.5).With("basic.temperature", 30.0);
        var p = ModuleParams.From(sdr, edited);
        Assert.False(p.RawProcessingOn);
        Assert.False(p.RawMaskOn);
        Assert.Equal(30, p.Temperature);
        Assert.False(ModuleParams.UsesRawWhiteBalance(sdr, edited));
        using var cpu = new CpuFakeRenderer();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(cpu.Render16(sdr, edited.With("raw.enabled", false), ct).Rgba16, cpu.Render16(sdr, edited, ct).Rgba16);
    }

    // ---- applyAdjustments: pre-WB vs current edits -------------------------

    [Fact]
    public void PreEditSampleIgnoresExposureButAdjustedSampleAppliesIt()
    {
        var source = Sdr(4, 4, 28000, 28000, 28000);
        var settings = ModuleCatalog.Defaults.With("basic.exposure", 1.0);
        var pre = PhotoAnalysis.Sample(source, settings, new PhotoRegion(0, 0, 1, 1), applyAdjustments: false);
        var adjusted = PhotoAnalysis.Sample(source, settings, new PhotoRegion(0, 0, 1, 1), applyAdjustments: true);
        Near(SrgbToLinear(28000), pre.Red);
        Near(2 * SrgbToLinear(28000), adjusted.Red, 1e-9);
        Near(2 * SrgbToLinear(28000), adjusted.Luminance, 1e-9);
    }

    [Fact]
    public void AdjustedSampleAppliesBasicTemperatureGains()
    {
        var source = Sdr(4, 4, 28000, 28000, 28000);
        var settings = ModuleCatalog.Defaults.With("basic.temperature", 100.0);
        var adjusted = PhotoAnalysis.Sample(source, settings, new PhotoRegion(0, 0, 1, 1), applyAdjustments: true);
        var linear = SrgbToLinear(28000);
        Near(linear * 1.18, adjusted.Red, 1e-9);
        Near(linear, adjusted.Green, 1e-9);
        Near(linear * 0.82, adjusted.Blue, 1e-9);
    }

    [Fact]
    public void CurrentCustomWhiteBalanceAffectsAdjustedSample()
    {
        var wb = NeutralModel();
        var source = SceneSource(4, 4, 0.5f, 0.4f, 0.3f, wb);
        var asShot = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        var custom = PhotoAnalysis.Sample(source,
            ModuleCatalog.Defaults.With("raw.enabled", true).With("raw.wb_mode", "custom").With("raw.temperature", 3200.0),
            new PhotoRegion(0, 0, 1, 1));
        Assert.True(asShot.CanUse && custom.CanUse);
        // Same developed chromaticity in, different WB matrix out: channels must move.
        Assert.NotEqual(asShot.Red / asShot.Green, custom.Red / custom.Green, 1e-6);
    }

    [Fact]
    public void AcceptedSkinRegionChangesOnlyItsNeighbourhood()
    {
        var source = Sdr(41, 41, 28000, 28000, 28000);
        var settings = ModuleCatalog.Defaults
            .With("skin.enabled", true)
            .With("skin.region_cx", 0.5).With("skin.region_cy", 0.5)
            .With("skin.radius_x", 0.1).With("skin.radius_y", 0.1)
            .With("skin.amount", 100.0).With("skin.temperature", 100.0).With("skin.feather", 0.0);
        var center = PhotoAnalysis.Sample(source, settings, new PhotoRegion(0.48, 0.48, 0.04, 0.04));
        var corner = PhotoAnalysis.Sample(source, settings, new PhotoRegion(0.0, 0.0, 0.04, 0.04));
        var plain = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0.48, 0.48, 0.04, 0.04));
        Assert.True(center.CanUse && corner.CanUse);
        Assert.True(center.Red > plain.Red);
        Assert.True(center.Blue < plain.Blue);
        Near(plain.Red, corner.Red, 1e-12);
        Near(plain.Blue, corner.Blue, 1e-12);
        // Luminance-preserving: skin warmth must not move the sampled luminance.
        Near(plain.Luminance, center.Luminance, 1e-9);
    }

    [Fact]
    public void SamplingNeverMutatesTheSource()
    {
        var source = Sdr(8, 8, 28000, 20000, 12000);
        var before = (ushort[])source.Rgba16.Clone();
        var settings = ModuleCatalog.Defaults.With("basic.exposure", 2.0).With("basic.temperature", 80.0);
        _ = PhotoAnalysis.Sample(source, settings, new PhotoRegion(0, 0, 1, 1));
        _ = PhotoAnalysis.CaptureReference(source, settings, new PhotoRegion(0, 0, 1, 1));
        Assert.Equal(before, source.Rgba16);
    }

    // ---- references and gains ----------------------------------------------

    [Fact]
    public void CaptureReferenceReturnsAdjustedChromaticity()
    {
        var source = Sdr(4, 4, 28000, 28000, 28000);
        var settings = ModuleCatalog.Defaults.With("basic.exposure", 1.0);
        var reference = PhotoAnalysis.CaptureReference(source, settings, new PhotoRegion(0, 0, 1, 1));
        Assert.NotNull(reference);
        Near(2 * SrgbToLinear(28000), reference.Value.Red, 1e-9);
        Assert.Null(PhotoAnalysis.CaptureReference(source, settings, new PhotoRegion(0, 0, 0, 0)));
    }

    [Fact]
    public void ReferenceGainsMapChromaticityIntoTheReferenceDomain()
    {
        var sample = new PhotoSample(new PhotoRegion(0, 0, 1, 1), 10, 0, 0.4, 0.2, 0.1,
            0.2126 * 0.4 + 0.7152 * 0.2 + 0.0722 * 0.1, 0, false, false);
        var gains = PhotoAnalysis.ReferenceGains(sample, new PhotoReference(0.3, 0.3, 0.3));
        Assert.NotNull(gains);
        // Neutral target from a warm sample: red gain below one, blue above one.
        Assert.True(gains.Value.Red < 1);
        Assert.True(gains.Value.Blue > 1);
        Assert.InRange(gains.Value.Red, 0.25, 4.0);
        Assert.InRange(gains.Value.Green, 0.25, 4.0);
        Assert.InRange(gains.Value.Blue, 0.25, 4.0);
    }

    [Fact]
    public void ReferenceGainsRejectUnusableInputs()
    {
        var bad = new PhotoSample(new PhotoRegion(0, 0, 1, 1), 0, 4, 0, 0, 0, 0, 0, false, false);
        Assert.Null(PhotoAnalysis.ReferenceGains(bad, new PhotoReference(0.3, 0.3, 0.3)));
        var good = new PhotoSample(new PhotoRegion(0, 0, 1, 1), 4, 0, 0.3, 0.3, 0.3, 0.3, 0, false, false);
        Assert.Null(PhotoAnalysis.ReferenceGains(good, new PhotoReference(0, 0.3, 0.3)));
        Assert.Null(PhotoAnalysis.ReferenceGains(good, new PhotoReference(double.NaN, 0.3, 0.3)));
    }

    // ---- forward WB fit -----------------------------------------------------

    [Fact]
    public void SolveReturnsNullWithoutWhiteBalanceModel()
    {
        var source = Sdr(8, 8, 28000, 20000, 12000);
        Assert.Null(PhotoAnalysis.SolveWhiteBalance(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1)));
        Assert.Empty(PhotoAnalysis.AutomaticWhiteBalance(source, ModuleCatalog.Defaults));
    }

    [Fact]
    public void SolveRecoversKnownForwardTransform()
    {
        // Ground truth built only from the public WB model: take a neutral Rec.2020
        // grey, push it through the 3200 K candidate matrix back to as-shot space,
        // then require the solver to find 3200 K again from the developed sample.
        var wb = NeutralModel();
        var pushed = wb.MatrixFor(3200, 0);
        Assert.NotNull(pushed);
        var inverse = pushed.Value.Inverse();
        Assert.NotNull(inverse);
        var rec2020 = inverse.Value.Apply(0.5, 0.5, 0.5);
        var source = SceneSource(8, 8, (float)rec2020.X, (float)rec2020.Y, (float)rec2020.Z, wb);
        var suggestion = PhotoAnalysis.SolveWhiteBalance(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        Assert.NotNull(suggestion);
        Assert.InRange(suggestion.Temperature, 2900, 3500);
        Assert.InRange(suggestion.Tint, -30, 30);
        Assert.False(suggestion.AtBoundary);
        Assert.True(suggestion.Confidence > 0.3);
        // The proposed matrix must actually neutralize the sample in working space.
        var sample = PhotoAnalysis.Sample(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1), applyAdjustments: false);
        var toRec2020 = ColorSpaces.Srgb.ToSpace(ColorSpaces.Rec2020);
        var toWorking = ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb);
        var fitted = wb.MatrixFor(suggestion.Temperature, suggestion.Tint) ?? Mat3.Identity;
        var working = toWorking.Mul(fitted).Mul(toRec2020).Apply(sample.Red, sample.Green, sample.Blue);
        var luma = 0.2126 * working.X + 0.7152 * working.Y + 0.0722 * working.Z;
        Near(1.0, working.X / luma, 0.05);
        Near(1.0, working.Y / luma, 0.05);
        Near(1.0, working.Z / luma, 0.05);
    }

    [Fact]
    public void SolveToReferenceMatchesCaptureDomain()
    {
        var wb = NeutralModel();
        var reference = new PhotoReference(0.45, 0.3, 0.2);
        // Build a sample that reaches the reference exactly through a known candidate.
        var pushed = wb.MatrixFor(4500, 20) ?? Mat3.Identity;
        var toRec2020 = ColorSpaces.Srgb.ToSpace(ColorSpaces.Rec2020);
        var toWorking = ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb);
        var working = toWorking.Mul(pushed).Mul(toRec2020);
        var workingInverse = working.Inverse();
        Assert.NotNull(workingInverse);
        var pre = workingInverse.Value.Apply(reference.Red, reference.Green, reference.Blue);
        var scene2020 = toRec2020.Apply(pre.X, pre.Y, pre.Z);
        var source = SceneSource(8, 8, (float)scene2020.X, (float)scene2020.Y, (float)scene2020.Z, wb);
        var suggestion = PhotoAnalysis.SolveWhiteBalance(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1), reference);
        Assert.NotNull(suggestion);
        Assert.InRange(suggestion.Temperature, 4100, 4900);
    }

    [Fact]
    public void SolveStaysWithinSearchBoundsAndFlagsThem()
    {
        var wb = NeutralModel();
        // Extremely blue scene content: no in-bounds candidate can neutralize it,
        // so the fit must stop at the search edge and say so.
        var source = SceneSource(8, 8, 0.05f, 0.1f, 1.5f, wb);
        var suggestion = PhotoAnalysis.SolveWhiteBalance(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1));
        if (suggestion is null) return; // Honest low-confidence refusal is also acceptable.
        Assert.InRange(suggestion.Temperature, 2500, 9900);
        Assert.InRange(suggestion.Tint, -150, 150);
        Assert.True(suggestion.AtBoundary);
        Assert.True(suggestion.Confidence < 0.5);
    }

    [Fact]
    public void SolveRefusesMonochromeWithoutForcedConfidence()
    {
        var wb = NeutralModel();
        // Single-channel content carries no chromaticity direction to fit.
        var source = SceneSource(8, 8, 0.6f, 0.001f, 0.001f, wb);
        Assert.Null(PhotoAnalysis.SolveWhiteBalance(source, ModuleCatalog.Defaults, new PhotoRegion(0, 0, 1, 1)));
    }
    [Fact]
    public void ManualNeutralPatchCanKeepTheAsShotWhiteBalance()
    {
        var source = SceneSource(8, 8, .18f, .18f, .18f, NeutralModel());
        var suggestion = PhotoAnalysis.SolveWhiteBalance(source, ModuleCatalog.Defaults, new(0, 0, 1, 1));
        Assert.NotNull(suggestion);
        Assert.InRange(suggestion.Temperature, 6400, 6600);
        Assert.InRange(suggestion.Residual, 0, .01);
    }

    [Fact]
    public void ForwardFitReproducesReferenceAfterExistingSpatialAndBasicEdits()
    {
        var basis = SceneSource(12, 8, .24f, .18f, .12f, NeutralModel());
        var alpha = new ushort[12 * 8];
        for (var row = 0; row < 8; row++)
            for (var x = 0; x < 6; x++) alpha[row * 12 + x] = 65535;
        var source = new SourceImage(12, 8, basis.Rgba16)
        {
            Scene = basis.Scene, RawInfo = basis.RawInfo,
            SemanticMasks = [new SemanticMask { Name = "subject", Width = 12, Height = 8, Data = alpha }],
        };
        var region = new PhotoRegion(0, 0, 1, 1);
        var settings = ModuleCatalog.Defaults.With("raw.enabled", true)
            .With("raw.mask_enabled", true).With("raw.mask_name", "subject")
            .With("raw.mask_exposure", .7).With("raw.mask_temperature", 35.0).With("raw.mask_tint", -20.0)
            .With("basic.temperature", 65.0).With("basic.tint", -45.0)
            .With("skin.enabled", true).With("skin.temperature", -60.0)
            .With("skin.region_cx", .3).With("skin.radius_x", .4).With("skin.radius_y", .7)
            .With("zone.enabled", true).With("zone.slope", 1.4);
        // Target comes from the actual CPU linear-stage consumers, not the sampler's mirror.
        var target = CpuLinearReference(settings.With("raw.wb_mode", "custom")
            .With("raw.temperature", 4500.0).With("raw.tint", 20.0));
        var proposal = PhotoAnalysis.SolveWhiteBalance(source, settings, region, target);
        Assert.NotNull(proposal);
        var actual = PhotoAnalysis.CaptureReference(source, settings.With("raw.wb_mode", "custom")
            .With("raw.temperature", proposal.Temperature).With("raw.tint", proposal.Tint), region)!.Value;
        Assert.True(Math.Abs(target.Red / target.Green - actual.Red / actual.Green) < .01,
            $"proposal={proposal}; actual={actual}; reference={target}");
        Assert.True(Math.Abs(target.Blue / target.Green - actual.Blue / actual.Green) < .01,
            $"proposal={proposal}; actual={actual}; reference={target}");
        var rendered = CpuLinearReference(settings.With("raw.wb_mode", "custom")
            .With("raw.temperature", proposal.Temperature).With("raw.tint", proposal.Tint));
        Assert.InRange(Math.Abs(rendered.Red / rendered.Green - target.Red / target.Green), 0, .01);
        Assert.InRange(Math.Abs(rendered.Blue / rendered.Green - target.Blue / target.Green), 0, .01);
        static double Luma(PhotoReference color) => .2126 * color.Red + .7152 * color.Green + .0722 * color.Blue;
        var y = Luma(actual);
        var ty = Luma(target);
        var dr = actual.Red / y - target.Red / ty;
        var dg = actual.Green / y - target.Green / ty;
        var db = actual.Blue / y - target.Blue / ty;
        Near(Math.Sqrt(dr * dr + dg * dg + db * db), proposal.Residual, 1e-8);

        PhotoReference CpuLinearReference(Settings current)
        {
            var p = ModuleParams.From(source, current);
            var pixels = CpuPipeline.DecodeLinear(source);
            if (p.RawWhiteBalanceMatrix is { } wb)
            {
                var matrix = RawFinishParams.WorkingWhiteBalance(wb);
                for (var i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 1], pixels[i + 2]) = matrix.ApplyF32(pixels[i], pixels[i + 1], pixels[i + 2]);
            }
            RawSemanticAdjustments.Apply(pixels, source.Width, source.Height, source, p, TestContext.Current.CancellationToken);
            // The fixture's +65 warmth / -45 tint stay in settings but are not applied: on a RAW
            // source with a WB model the Kelvin white balance replaces relative basic temperature/tint.
            Assert.Equal((0f, 0f), (p.Temperature, p.Tint));
            PhotoAdjustments.Apply(pixels, source.Width, source.Height, p, TestContext.Current.CancellationToken);
            double red = 0, green = 0, blue = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                red += pixels[i]; green += pixels[i + 1]; blue += pixels[i + 2];
            }
            var count = pixels.Length / 4;
            return new PhotoReference(red / count, green / count, blue / count);
        }
    }

    [Fact]
    public void AdjustedSampleMatchesCpuMaskedLuminanceAfterExtremeWhiteBalance()
    {
        var basis = SceneSource(1, 1, 0, 0, 1, NeutralModel());
        var source = new SourceImage(1, 1, basis.Rgba16)
        {
            Scene = basis.Scene, RawInfo = basis.RawInfo,
            SemanticMasks = [new SemanticMask { Name = "subject", Width = 1, Height = 1, Data = [65535] }],
        };
        var settings = ModuleCatalog.Defaults.With("raw.enabled", true).With("raw.wb_mode", "custom")
            .With("raw.temperature", 2500.0).With("raw.tint", 150.0)
            .With("raw.mask_enabled", true).With("raw.mask_name", "subject").With("raw.mask_temperature", 100.0);
        var p = ModuleParams.From(source, settings);
        var pixels = CpuPipeline.DecodeLinear(source);
        var matrix = RawFinishParams.WorkingWhiteBalance(p.RawWhiteBalanceMatrix!.Value);
        (pixels[0], pixels[1], pixels[2]) = matrix.ApplyF32(pixels[0], pixels[1], pixels[2]);
        RawSemanticAdjustments.Apply(pixels, 1, 1, source, p, TestContext.Current.CancellationToken);
        var renderedY = .2126 * pixels[0] + .7152 * pixels[1] + .0722 * pixels[2];
        var sample = PhotoAnalysis.Sample(source, settings, new PhotoRegion(0, 0, 1, 1));
        Assert.True(renderedY > 0);
        Assert.InRange(Math.Abs(sample.Luminance / renderedY - 1), 0, 1e-4);
    }

    [Fact]
    public void ReferenceGainsPreserveFeasibleRatiosRatherThanClippingChannelsIndependently()
    {
        var sample = new PhotoSample(new(0, 0, 1, 1), 16, 0, .01, 1, 1, .789526, 0, false, false);
        var reference = new PhotoReference(1, 10, 10);
        var gains = PhotoAnalysis.ReferenceGains(sample, reference);
        Assert.NotNull(gains);
        Near(reference.Red / reference.Green, sample.Red * gains.Value.Red / (sample.Green * gains.Value.Green), 1e-9);
        var scaled = PhotoAnalysis.ReferenceGains(sample, new(100, 1000, 1000));
        Assert.NotNull(scaled);
        Near(gains.Value.Red, scaled.Value.Red, 1e-12);
        Near(gains.Value.Green, scaled.Value.Green, 1e-12);
        Near(gains.Value.Blue, scaled.Value.Blue, 1e-12);
        Assert.Null(PhotoAnalysis.ReferenceGains(sample with { Red = .0001 }, new(1, 1, 1)));
    }

    [Fact]
    public void AutomaticUniformCastDoesNotClaimAnIlluminant()
    {
        var source = SceneSource(12, 12, .207f, .18f, .1494f, NeutralModel());
        Assert.All(PhotoAnalysis.AutomaticWhiteBalance(source, ModuleCatalog.Defaults),
            candidate => Assert.InRange(candidate.Confidence, 0, .45));
    }


    [Fact]
    public void AutomaticCandidatesOfferDistinctFitsForDifferentSpatialCasts()
    {
        var wb = NeutralModel();
        // Three equal-luminance neutral cards under distinct model-relative casts.
        // Equal brightness prevents "pick the brightest" from satisfying this case.
        var temperatures = new[] { 3500.0, 5500.0, 8000.0 };
        var cards = temperatures.Select(temperature =>
            wb.MatrixFor(temperature, 0)!.Value.Inverse()!.Value.Apply(.18, .18, .18)).ToArray();
        var data = new float[12 * 6 * 3];
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 12; x++)
            {
                var i = (y * 12 + x) * 3;
                var card = cards[x / 4];
                data[i] = (float)card.Item1;
                data[i + 1] = (float)card.Item2;
                data[i + 2] = (float)card.Item3;
            }
        var raw = LinearRaw(12, 6);
        var scene = new RawSceneData(new RgbImage(12, 6, data), null, wb, RawSourceGeometry.Create(raw));
        var rgba = new ushort[12 * 6 * 4];
        for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 65535;
        var source = new SourceImage(12, 6, rgba) { Scene = scene, RawInfo = raw.Info() };
        var candidates = PhotoAnalysis.AutomaticWhiteBalance(source, ModuleCatalog.Defaults);
        Assert.Equal(3, candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
            for (var j = i + 1; j < candidates.Count; j++)
                Assert.True(Math.Abs(candidates[i].Temperature - candidates[j].Temperature) >= 150
                    || Math.Abs(candidates[i].Tint - candidates[j].Tint) >= 15);
        foreach (var c in candidates)
        {
            Assert.InRange(c.Temperature, 2500, 9900);
            Assert.InRange(c.Tint, -150, 150);
            Assert.InRange(c.Confidence, double.Epsilon, .45);
            Assert.Contains(temperatures, temperature => Math.Abs(temperature - c.Temperature) < 100);
            var settings = ModuleCatalog.Defaults.With("raw.enabled", true).With("raw.wb_mode", "custom")
                .With("raw.temperature", c.Temperature).With("raw.tint", c.Tint);
            var actual = PhotoAnalysis.CaptureReference(source, settings, c.Region)!.Value;
            Near(1, actual.Red / actual.Green, .01);
            Near(1, actual.Blue / actual.Green, .01);
        }
    }

    [Fact]
    public void AutoOnUniformGreyYieldsNoForcedCandidate()
    {
        var wb = NeutralModel();
        var source = SceneSource(12, 12, 0.5f, 0.5f, 0.5f, wb);
        // Perfectly neutral tiles have no chromaticity error to fit; the solver
        // must not invent a confident answer from nothing.
        var candidates = PhotoAnalysis.AutomaticWhiteBalance(source, ModuleCatalog.Defaults);
        foreach (var c in candidates)
            Assert.True(c.Confidence < 0.9);
    }

    // ---- Zone ----------------------------------------------------------------

    [Fact]
    public void ZoneOfMiddleGreyIsFive()
    {
        Assert.Equal(5.0, ZonePlacement.ZoneOf(0.18));
        Near(6.0, ZonePlacement.ZoneOf(0.36)!.Value);
        Near(4.0, ZonePlacement.ZoneOf(0.09)!.Value);
        Assert.Null(ZonePlacement.ZoneOf(0));
        Assert.Null(ZonePlacement.ZoneOf(-1));
        Assert.Null(ZonePlacement.ZoneOf(double.NaN));
        Assert.Null(ZonePlacement.ZoneOf(double.PositiveInfinity));
    }

    [Fact]
    public void ExposureDeltaIsTargetMinusCurrent()
    {
        Near(2.0, ZonePlacement.ExposureDelta(0.18, 7)!.Value);
        Near(-1.0, ZonePlacement.ExposureDelta(0.36, 5)!.Value);
        Assert.Null(ZonePlacement.ExposureDelta(0, 5));
        Assert.Null(ZonePlacement.ExposureDelta(0.18, double.NaN));
    }

    [Fact]
    public void ZoneFitPassesThroughBothAnchorsMonotonically()
    {
        var curve = ZonePlacement.Fit(0.045, 0.72, 3, 8);
        Assert.NotNull(curve);
        Assert.True(curve.Value.Slope > 0);
        double Map(double y) => 0.18 * Math.Pow(y / 0.18, curve.Value.Slope) * Math.Pow(2, curve.Value.Offset);
        Near(3.0, ZonePlacement.ZoneOf(Map(0.045))!.Value, 1e-9);
        Near(8.0, ZonePlacement.ZoneOf(Map(0.72))!.Value, 1e-9);
        // Monotonic: ordered inputs map to ordered outputs.
        Assert.True(Map(0.02) < Map(0.045) && Map(0.045) < Map(0.3) && Map(0.3) < Map(0.72) && Map(0.72) < Map(1.5));
    }

    [Fact]
    public void ZoneFitRejectsDegenerateAndReversedAnchors()
    {
        Assert.Null(ZonePlacement.Fit(0.1, 0.1));          // degenerate span
        Assert.Null(ZonePlacement.Fit(0.5, 0.1));          // reversed luminances
        Assert.Null(ZonePlacement.Fit(0.1, 0.5, 8, 3));    // reversed zones
        Assert.Null(ZonePlacement.Fit(0, 0.5));            // non-positive
        Assert.Null(ZonePlacement.Fit(-1, 0.5));
        Assert.Null(ZonePlacement.Fit(double.NaN, 0.5));
        Assert.Null(ZonePlacement.Fit(0.1, double.PositiveInfinity));
        Assert.Null(ZonePlacement.Fit(0.1, 0.5, double.NaN, 8));
    }

    [Fact]
    public void ZoneFitReturnsUnconstrainedPositiveSlopeForUiWarning()
    {
        // An extreme span the bounded schema cannot express: the fit still returns
        // a positive unconstrained curve so the UI can warn instead of clamping.
        var curve = ZonePlacement.Fit(0.1, 0.1001, 3, 8);
        Assert.NotNull(curve);
        Assert.True(curve.Value.Slope > 0);
        Assert.True(curve.Value.Slope < 0.25 || curve.Value.Offset is < -8 or > 8);
    }
}
