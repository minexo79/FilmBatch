using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

/// <summary>Disabled effects retain their values but do not alter pixels; enabling them changes the image.</summary>
public sealed class ModuleEnableTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectSwitches_ControlActualRenderedPixels(bool useGpu)
    {
        var renderer = CreateRenderer(useGpu);
        Assert.SkipWhen(renderer is null, "No GPU available");
        using (renderer!)
        {
            var token = TestContext.Current.CancellationToken;
            var source = GpuSpikeBenchmark.Synthetic(96, 80);
            var failures = new List<string>();
            foreach (var name in new[] { "crop", "basic", "curves", "hsl", "skin", "shadow_recovery",
                "lens_distortion", "multi_lens", "double_exposure", "bokeh", "zoom_rotate_blur", "motion_blur",
                "vignette", "halation", "light_leak", "film", "photo_plate", "tone", "detail", "dirt_scratches", "grain" })
            {
                var key = name + ".enabled";
                var baselineSettings = ModuleCatalog.Defaults.With(key, false);
                var modified = Configured(name, baselineSettings);
                var baseline = renderer.Render16(source, baselineSettings, token);
                var disabled = renderer.Render16(source, modified.With(key, false), token);
                var enabled = renderer.Render16(source, modified.With(key, true), token);
                if (useGpu && renderer is GpuSpikeRenderer gpu && gpu.UsingCpu)
                    failures.Add(name + ": unexpected CPU fallback");
                if ((baseline.Width, baseline.Height) != (disabled.Width, disabled.Height)
                    || !baseline.Rgba16.AsSpan().SequenceEqual(disabled.Rgba16))
                    failures.Add(name + ": disabled settings affected pixels");
                if ((enabled.Width, enabled.Height) == (disabled.Width, disabled.Height)
                    && enabled.Rgba16.AsSpan().SequenceEqual(disabled.Rgba16))
                    failures.Add(name + ": enabling did not affect pixels");
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneOnlySwitches_ControlActualRenderedPixels(bool useGpu)
    {
        var renderer = CreateRenderer(useGpu);
        Assert.SkipWhen(renderer is null, "No GPU available");
        using (renderer!)
        {
            var token = TestContext.Current.CancellationToken;
            var source = SyntheticRawScene(32, 24);
            var failures = new List<string>();
            foreach (var name in new[] { "zone", "highlight_rolloff", "raw" })
            {
                var key = name + ".enabled";
                var baselineSettings = ModuleCatalog.Defaults.With(key, false);
                var modified = ConfiguredRaw(name, baselineSettings);
                var baseline = renderer.Render16(source, baselineSettings, token);
                var disabled = renderer.Render16(source, modified.With(key, false), token);
                var enabled = renderer.Render16(source, modified.With(key, true), token);
                if (useGpu && renderer is GpuSpikeRenderer gpu && gpu.UsingCpu)
                    failures.Add(name + ": unexpected CPU fallback");
                if ((baseline.Width, baseline.Height) != (disabled.Width, disabled.Height)
                    || !baseline.Rgba16.AsSpan().SequenceEqual(disabled.Rgba16))
                    failures.Add(name + ": disabled settings affected pixels");
                if ((enabled.Width, enabled.Height) == (disabled.Width, disabled.Height)
                    && enabled.Rgba16.AsSpan().SequenceEqual(disabled.Rgba16))
                    failures.Add(name + ": enabling did not affect pixels");
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneOnlySwitches_IgnoredOnSdrSource(bool useGpu)
    {
        var renderer = CreateRenderer(useGpu);
        Assert.SkipWhen(renderer is null, "No GPU available");
        using (renderer!)
        {
            var token = TestContext.Current.CancellationToken;
            var sdrSource = GpuSpikeBenchmark.Synthetic(48, 36);
            foreach (var name in new[] { "zone", "highlight_rolloff", "raw" })
            {
                var key = name + ".enabled";
                var baselineSettings = ModuleCatalog.Defaults.With(key, false);
                var modified = ConfiguredRaw(name, baselineSettings);
                var disabled = renderer.Render16(sdrSource, modified.With(key, false), token);
                var enabled = renderer.Render16(sdrSource, modified.With(key, true), token);
                Assert.Equal(disabled.Rgba16, enabled.Rgba16);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubordinateSwitches_GatedByParentAndSubordinateControls(bool useGpu)
    {
        var renderer = CreateRenderer(useGpu);
        Assert.SkipWhen(renderer is null, "No GPU available");
        using (renderer!)
        {
            var token = TestContext.Current.CancellationToken;

            // 1. RAW semantic mask subordinate to raw.enabled
            var rawScene = SyntheticRawScene(32, 24, includeMask: true);
            var maskBaseline = ModuleCatalog.Defaults.With("raw.enabled", true).With("raw.mask_enabled", false);
            var maskConfigured = maskBaseline
                .With("raw.mask_name", "subject")
                .With("raw.mask_exposure", 2.0);
            var maskDisabled = renderer.Render16(rawScene, maskConfigured.With("raw.mask_enabled", false), token);
            var maskEnabled = renderer.Render16(rawScene, maskConfigured.With("raw.mask_enabled", true), token);
            var baselineRender = renderer.Render16(rawScene, maskBaseline, token);
            Assert.Equal(baselineRender.Rgba16, maskDisabled.Rgba16);
            Assert.NotEqual(maskDisabled.Rgba16, maskEnabled.Rgba16);

            // Parent raw.enabled = false inhibits subordinate raw.mask_enabled even when mask_enabled = true
            var parentDisabledWithMask = maskConfigured.With("raw.enabled", false).With("raw.mask_enabled", true);
            var parentDisabledWithoutMask = maskConfigured.With("raw.enabled", false).With("raw.mask_enabled", false);
            var pOffWithMask = renderer.Render16(rawScene, parentDisabledWithMask, token);
            var pOffWithoutMask = renderer.Render16(rawScene, parentDisabledWithoutMask, token);
            Assert.Equal(pOffWithoutMask.Rgba16, pOffWithMask.Rgba16);

            // 2. Motion blur protect_enabled subordinate to motion_blur.enabled
            var sdrSource = GpuSpikeBenchmark.Synthetic(48, 36);
            var motionConfigured = ModuleCatalog.Defaults
                .With("motion_blur.enabled", true)
                .With("motion_blur.length", 80.0)
                .With("motion_blur.protect_size", 40.0);
            var motionUnprotected = renderer.Render16(sdrSource, motionConfigured.With("motion_blur.protect_enabled", false), token);
            var motionProtected = renderer.Render16(sdrSource, motionConfigured.With("motion_blur.protect_enabled", true), token);
            Assert.NotEqual(motionUnprotected.Rgba16, motionProtected.Rgba16);

            var motionDisabledProtect = ModuleCatalog.Defaults
                .With("motion_blur.enabled", false)
                .With("motion_blur.protect_enabled", true)
                .With("motion_blur.length", 80.0);
            var plainSdr = renderer.Render16(sdrSource, ModuleCatalog.Defaults, token);
            var motionDisabledProtectRender = renderer.Render16(sdrSource, motionDisabledProtect, token);
            Assert.Equal(plainSdr.Rgba16, motionDisabledProtectRender.Rgba16);

            // 3. Double exposure asset bypass when disabled
            var invalidAssetSettings = ModuleCatalog.Defaults
                .With("double_exposure.enabled", false)
                .With("double_exposure.source", "file")
                .With("double_exposure.source_path", "non_existent_fixture_path_12345.png");
            var invalidAssetRender = renderer.Render16(sdrSource, invalidAssetSettings, token);
            Assert.Equal(plainSdr.Rgba16, invalidAssetRender.Rgba16);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroIntensity_EquivalentToDisabledEffect(bool useGpu)
    {
        var renderer = CreateRenderer(useGpu);
        Assert.SkipWhen(renderer is null, "No GPU available");
        using (renderer!)
        {
            var token = TestContext.Current.CancellationToken;
            var source = GpuSpikeBenchmark.Synthetic(48, 36);
            var zeroCases = new (string Name, string Key, double Value)[]
            {
                ("skin", "skin.amount", 0.0),
                ("double_exposure", "double_exposure.opacity", 0.0),
                ("bokeh", "bokeh.blur", 0.0),
                ("vignette", "vignette.amount", 0.0),
                ("halation", "halation.amount", 0.0),
                ("light_leak", "light_leak.amount", 0.0),
                ("film", "film.strength", 0.0),
                ("photo_plate", "photo_plate.strength", 0.0),
                ("detail", "detail.sharpen", 0.0),
                ("dirt_scratches", "dirt_scratches.strength", 0.0),
                ("grain", "grain.amount", 0.0),
            };
            foreach (var (name, key, val) in zeroCases)
            {
                var sDisabled = ModuleCatalog.Defaults.With(name + ".enabled", false).With(key, val);
                var sEnabledZero = ModuleCatalog.Defaults.With(name + ".enabled", true).With(key, val);
                if (name == "grain")
                {
                    sDisabled = sDisabled.With("grain.softness", 0.0);
                    sEnabledZero = sEnabledZero.With("grain.softness", 0.0);
                }
                var disabled = renderer.Render16(source, sDisabled, token);
                var enabledZero = renderer.Render16(source, sEnabledZero, token);
                Assert.Equal(disabled.Rgba16, enabledZero.Rgba16);
            }

            // Highlight rolloff on RAW scene with 0 strength
            var rawScene = SyntheticRawScene(32, 24);
            var rolloffDisabled = ModuleCatalog.Defaults.With("highlight_rolloff.enabled", false).With("highlight_rolloff.strength", 0.0);
            var rolloffZero = ModuleCatalog.Defaults.With("highlight_rolloff.enabled", true).With("highlight_rolloff.strength", 0.0);
            var rolloffDisRender = renderer.Render16(rawScene, rolloffDisabled, token);
            var rolloffZeroRender = renderer.Render16(rawScene, rolloffZero, token);
            Assert.Equal(rolloffDisRender.Rgba16, rolloffZeroRender.Rgba16);
        }
    }


    private static IPreviewRenderer? CreateRenderer(bool useGpu)
    {
        try { return useGpu ? GpuSpikeRenderer.Create() : new CpuFakeRenderer(); }
        catch (GpuException) { return null; }
    }

    private static SourceImage SyntheticRawScene(int width = 32, int height = 24, bool includeMask = false)
    {
        var data = new float[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = (y * width + x) * 3;
                var val = 0.02f + (x + y * width) * (2.5f - 0.02f) / (width * height - 1);
                data[idx] = val;
                data[idx + 1] = val * 0.9f;
                data[idx + 2] = val * 0.8f;
            }
        }
        var tone = CameraPreview.SonyDroOffTone();
        var wb = NeutralWhiteBalance(width, height);
        var geometry = DenoiseTestScene.Geometry(width, height);
        var scene = new RawSceneData(new RgbImage(width, height, data), tone, wb, geometry);
        var full = new PixelRect(0, 0, width, height);
        var raw = new RawImage
        {
            Format = RawFormat.Arw,
            Width = width,
            Height = height,
            Cpp = 3,
            Data = new RawData.F32(new float[width * height * 3]),
            Bits = 16,
            White = [1f, 1f, 1f],
            Black = new BlackLevel(),
            ActiveArea = full,
            Crop = full,
            Orientation = Orientation.Normal,
            Color = new ColorData { AsShotNeutral = [1.0, 1.0, 1.0] },
            Metadata = new Metadata(),
            WbMultipliers = [2.0f, 1.0f, 1.5f],
        };
        var masks = includeMask
            ? (IReadOnlyList<SemanticMask>)[new SemanticMask { Name = "subject", Width = width, Height = height, Data = MakeMaskData(width, height) }]
            : [];
        return new SourceImage(scene)
        {
            RawInfo = raw.Info(),
            SemanticMasks = masks,
        };
    }

    private static ushort[] MakeMaskData(int width, int height)
    {
        var mask = new ushort[width * height];
        for (var y = height / 4; y < height * 3 / 4; y++)
            for (var x = width / 4; x < width * 3 / 4; x++)
                mask[y * width + x] = 65535;
        return mask;
    }

    private static RawWhiteBalance NeutralWhiteBalance(int width, int height)
    {
        var full = new PixelRect(0, 0, width, height);
        var raw = new RawImage
        {
            Format = RawFormat.Arw,
            Width = width,
            Height = height,
            Cpp = 3,
            Data = new RawData.F32(new float[width * height * 3]),
            Bits = 16,
            White = [1f, 1f, 1f],
            Black = new BlackLevel(),
            ActiveArea = full,
            Crop = full,
            Orientation = Orientation.Normal,
            Color = new ColorData { AsShotNeutral = [1.0, 1.0, 1.0] },
            Metadata = new Metadata(),
            WbMultipliers = [2.0f, 1.0f, 1.5f],
        };
        var developedFor = CameraColor.AsShotWhiteXy(raw);
        return RawWhiteBalance.Create(raw, developedFor, CameraColor.CameraTransform(raw, developedFor), null);
    }

    private static Settings Configured(string name, Settings s) => name switch
    {
        "crop" => s.With("crop.rect_w", .6).With("crop.rect_h", .75),
        "basic" => s.With("basic.exposure", 1.0),
        "curves" => s.With("curves.highlights", 80.0),
        "hsl" => s.With("hsl.hue_red_h", 100.0),
        "skin" => s.With("skin.region_cx", .25).With("skin.radius_x", .4).With("skin.radius_y", .4).With("skin.temperature", 80.0),
        "shadow_recovery" => s.With("shadow_recovery.exposure", 4.0),
        "lens_distortion" => s.With("lens_distortion.amount", 75.0),
        "multi_lens" => s.With("multi_lens.offset", 50.0),
        "double_exposure" => s.With("double_exposure.opacity", 80.0),
        "bokeh" => s.With("bokeh.blur", 80.0).With("bokeh.focus_size", 5.0),
        "zoom_rotate_blur" => s.With("zoom_rotate_blur.zoom", 80.0),
        "motion_blur" => s.With("motion_blur.length", 80.0),
        "vignette" => s.With("vignette.amount", 80.0),
        "halation" => s.With("halation.amount", 100.0).With("halation.radius", 80.0),
        "light_leak" => s.With("light_leak.amount", 80.0),
        "film" => s.With("film.strength", 100.0),
        "photo_plate" => s.With("photo_plate.strength", 100.0),
        "tone" => s.With("tone.shadow_amount", 80.0),
        "detail" => s.With("detail.sharpen", 80.0),
        "dirt_scratches" => s.With("dirt_scratches.strength", 100.0),
        "grain" => s.With("grain.amount", 80.0),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static Settings ConfiguredRaw(string name, Settings s) => name switch
    {
        "zone" => s.With("zone.slope", 1.8).With("zone.offset", 1.0),
        "highlight_rolloff" => s.With("highlight_rolloff.strength", 100.0),
        "raw" => s.With("raw.wb_mode", "custom").With("raw.temperature", 3200.0).With("raw.tint", 50.0),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
