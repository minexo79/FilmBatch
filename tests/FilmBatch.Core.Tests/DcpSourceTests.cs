// DCP-1 D3：外部相機描述檔的顯影、機型／policy、曝光、白平衡、來源 gain map 與 SDR 明度。
// 期望像素是獨立的 float32 手算（DNG 相機模型、Bradford、Rec.2020／ProPhoto、HSV 第 8 步），
// 不呼叫 CameraColor、ProfileTables、HsvTable 或 RawSourceLoader 來產生期望值。
using System.Globalization;
using System.Text;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Gpu.Device;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Core.Tests;

[CollectionDefinition(nameof(DcpSourceCollection), DisableParallelization = true)]
public sealed class DcpSourceCollection;

[Collection(nameof(DcpSourceCollection))]
public sealed class DcpSourceTests : IDisposable
{
    private const double TolK = 2e-5;
    private readonly string _root;
    private readonly string _previousRoot;

    public DcpSourceTests()
    {
        _previousRoot = AppPaths.OverrideRoot ?? "";
        _root = Path.Combine(Path.GetTempPath(), "filmbatch-dcp-d3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = _previousRoot.Length == 0 ? null : _previousRoot;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // 手算字面量。感光正規化後 (0.25, 0.40, 0.10)，恆等相機（ColorMatrix = I，
    // ForwardMatrix 抵消 Rec.2020←XYZ←D50），曝光 0。
    private static readonly float[] IdentityExposure0 = [0.24999963f, 0.40000018f, 0.10000020f];

    // 同上感光，ColorMatrix = diag(2, 0.5, 1)、無 ForwardMatrix，曝光 +1 EV。
    private static readonly float[] ScaledMatrixExposure1 = [0.71129698f, 0.83552045f, 0.12041114f];

    // ColorMatrix = I、無 ForwardMatrix、曝光 0。匹配校正 diag(2, 1, 1) 與不用校正。
    private static readonly float[] CalibrationMatch = [0.33146328f, 0.43482596f, 0.08431140f];
    private static readonly float[] CalibrationIdentity = [0.22412318f, 0.49965230f, 0.08893367f];

    // (0.62, 0.18, 0.11)，恆等相機，HueSat 1×2×1 的 s=1 為色相 +30°、明度 ×0.5，曝光 0。
    private static readonly float[] HueSatShift = [0.36809996f, 0.21928269f, 0.06217679f];

    // (0.45, 0.45, 0.45)，恆等相機，曝光 +2，Look 明度倍率 1。SDR 把明度收到 ≤1；舊路徑保留超白。
    private static readonly float[] SdrClippedGray = [0.99999827f, 1f, 1f];

    // (0.62, 0.18, 0.11)，曝光 +1，Look 的 s=1 明度 ×2。SDR 與保留超白的結果不同。
    private static readonly float[] SdrPushed = [1.1494968f, 0.33372569f, 0.20394385f];

    // IdentityExposure0 × 來源 gain map 1.5。
    private static readonly float[] IdentityWithGain = [0.37499943f, 0.60000026f, 0.15000030f];

    private static readonly (int Num, int Den)[] IdentityMatrix =
    [
        (1, 1), (0, 1), (0, 1),
        (0, 1), (1, 1), (0, 1),
        (0, 1), (0, 1), (1, 1),
    ];

    private static readonly (int Num, int Den)[] ScaledMatrix =
    [
        (2, 1), (0, 1), (0, 1),
        (0, 1), (1, 2), (0, 1),
        (0, 1), (0, 1), (1, 1),
    ];

    // Bradford(D65→D50) · Rec.2020.ToXyz()，分母 1_000_000 的最接近整數。
    private static readonly (int Num, int Den)[] CancellingForward =
    [
        (673515, 1000000), (165697, 1000000), (125083, 1000000),
        (279059, 1000000), (675318, 1000000), (45623, 1000000),
        (-1932, 1000000), (29978, 1000000), (797059, 1000000),
    ];

    [Fact]
    public void External_profile_replaces_exposure_once_and_differs_across_profiles()
    {
        var keyA = Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (-1, 1), blackRender: 1));
        var keyB = Publish(ExposureProfile("Acme Model A", ScaledMatrix, forward: null, offset: (0, 1)));
        var shotA = Develop(ExposureRaw(), keyA);
        var shotB = Develop(ExposureRaw(), keyB);
        Near(IdentityExposure0, shotA);
        Near(ScaledMatrixExposure1, shotB);
        Assert.NotEqual(shotA[0], shotB[0]);
    }

    [Fact]
    public void Matching_calibration_signature_uses_the_camera_matrix_and_a_mismatch_uses_identity()
    {
        var match = Publish(SignatureProfile("SIG-1"));
        var miss = Publish(SignatureProfile("OTHER"));
        var lower = Publish(SignatureProfile("sig-1"));
        Near(CalibrationMatch, Develop(CalibrationRaw("SIG-1"), match));
        Near(CalibrationIdentity, Develop(CalibrationRaw("SIG-1"), miss));
        Near(CalibrationIdentity, Develop(CalibrationRaw("SIG-1"), lower));
        Near(CalibrationMatch, Develop(CalibrationRaw(null), Publish(SignatureProfile(null))));
    }

    [Fact]
    public void Dual_illuminant_huesat_uses_only_the_selected_table_and_look_is_applied()
    {
        var whiteA = new Xy(0.4470706750966438, 0.40750879834778053);
        var whiteB = new Xy(0.313432036002229, 0.323601871509382);
        float[] Hue(float[] first, float[] second, Xy white) =>
            Develop(DualRaw(white), Publish(DualProfile(first, second, look: null)));
        var atA = Hue(HueA, HueB, whiteA);
        AssertSame(atA, Hue(HueA, HueBChanged, whiteA));
        AssertDifferent(atA, Hue(HueAChanged, HueB, whiteA));
        var atB = Hue(HueA, HueB, whiteB);
        AssertSame(atB, Hue(HueAChanged, HueB, whiteB));
        AssertDifferent(atB, Hue(HueA, HueBChanged, whiteB));

        var hueOnly = Develop(ChromaticRaw(), Publish(HueProfile(look: false)));
        var withLook = Develop(ChromaticRaw(), Publish(HueProfile(look: true)));
        Near(HueSatShift, hueOnly);
        AssertDifferent(hueOnly, withLook);
    }

    [Fact]
    public void External_sdr_clips_value_above_one_for_both_encodings_and_embedded_keeps_headroom()
    {
        var legacy = Develop(SdrRaw(), profileKey: null);
        Assert.True(legacy[0] > 1.5f, $"embedded headroom collapsed: {legacy[0]}");
        foreach (var encoding in new ushort[] { 0, 1 })
        {
            var key = Publish(SdrProfile(encoding));
            var actual = Develop(SdrRaw(), key);
            Near(SdrClippedGray, actual);
            Assert.True(actual[0] < legacy[0], "external SDR did not reduce the superwhite");
        }

        var pushed = Develop(PushRaw(), Publish(PushProfile()));
        Near(SdrPushed, pushed);
        Assert.True(Math.Abs(pushed[0] - 1.2399988f) > 0.02f, "SDR push matched the legacy headroom pixel");
    }

    [Fact]
    public void Source_gain_map_stays_with_the_external_matrix_and_obeys_the_switch()
    {
        var key = Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (0, 1)));
        var on = Develop(GainRaw(), key, localTone: true);
        var off = Develop(GainRaw(), key, localTone: false);
        var absent = Develop(FlatRaw(0, 0.25f, 0.40f, 0.10f), key, localTone: true);
        Near(IdentityWithGain, on);
        Near(IdentityExposure0, off);
        Near(IdentityExposure0, absent);
    }

    [Fact]
    public void DngSourceV2GainMapAndExternalHueSatBothAffectDevelopedPixels()
    {
        // WriteDng stores its sole image IFD as IFD0; gamma 2 selects the v2 map tag.
        var raw = ChromaticRaw();
        var map = GainRaw().Color.Profile.GainTableMap!;
        map.Gamma = 2f;
        raw.Color.Profile.GainTableMap = map;
        var path = WriteScene(raw);
        Assert.Equal(2f, RawDecoder.Decode(File.ReadAllBytes(path)).Color.Profile.GainTableMap!.Gamma);

        var matrix = Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (0, 1)));
        var hue = Publish(HueProfile(look: false));
        float[] Load(string key, bool localTone) => Pixels(ImageLoader.Load(path, 32,
            RawLoadOptions.FromSettings(SettingsFor(key, localTone: localTone))));
        Near([0.62f, 0.18f, 0.11f], Load(matrix, localTone: false));
        Near(HueSatShift, Load(hue, localTone: false));
        // Hand-calculated HueSatShift multiplied by the source map's constant gain 1.5.
        Near([0.55214994f, 0.32892404f, 0.09326519f], Load(hue, localTone: true));
    }

    [Fact]
    public void ExternalDcpHueSatAndLookSurviveGpuBudgetFallback()
    {
        float[] hue = [0f, 1f, 1f, 0f, 1f, 2f];
        float[] look = [0f, 1f, 1f, 0f, 1f, 0.5f];
        var key = Publish(Profile("Acme Model A", IdentityMatrix, CancellingForward, (-1, 1),
            0, null, null, hue, null, look, 0));
        // Four million pixels are GPU-eligible by size; one byte forces budget fallback.
        var raw = Sensor(0.8f, 0.32f, 0.16f, 0, 1, 0, 0, 0, "Acme Model A", size: 2000);
        using var scope = RawGpuDemosaic.BeginScope(true, GpuBackend.Auto, 1);
        var image = RawSourceLoader.DevelopLinear(raw, ReadOnlyMemory<byte>.Empty, 2000,
            RawLoadOptions.FromSettings(SettingsFor(key))).Image;
        float[] expected = [0.35138390f, 0.14055356f, 0.07027678f];
        foreach (var i in new[] { 0, (image.Data.Length / 6) * 3, image.Data.Length - 3 })
            Near(expected, [image.Data[i], image.Data[i + 1], image.Data[i + 2]]);
    }

    [Fact]
    public void Missing_hash_model_and_policy_failures_are_visible_and_do_not_return_the_embedded_image()
    {
        var raw = ExposureRaw();
        var missing = "dcp-" + new string('a', 64);
        var missingError = Record.Exception(() => Develop(raw, missing));
        Assert.NotNull(missingError);
        var missingFault = Assert.IsType<CameraProfileException>(missingError);
        Assert.Equal(CameraProfileFault.Missing, missingFault.Fault);

        var key = Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (0, 1)));
        File.WriteAllBytes(ProfilePath(key), ExposureProfile("Acme Model A", ScaledMatrix, null, (0, 1)));
        var hashError = Record.Exception(() => Develop(ExposureRaw(), key));
        Assert.NotNull(hashError);
        Assert.Equal(CameraProfileFault.HashMismatch, Assert.IsType<CameraProfileException>(hashError).Fault);
        File.Delete(ProfilePath(key)); // Subsequent cases publish this same valid profile independently.

        AssertUseFault(() => Develop(ExposureRaw(), Publish(ExposureProfile("Other Camera", IdentityMatrix, CancellingForward, offset: (0, 1)))), "ModelMismatch");
        AssertUseFault(() => Develop(ExposureRaw(unique: null), Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (0, 1)))), "MissingModel");
        AssertUseFault(() => Develop(NonDngRaw(), Publish(PolicyProfile(0))), "Policy");
        AssertUseFault(() => Develop(NonDngRaw(), Publish(PolicyProfile(1))), "Policy");

        Near(IdentityExposure0, Develop(IdentityRaw(), Publish(PolicyProfile(0))));
        Near(IdentityExposure0, Develop(NonDngRaw(), Publish(PolicyProfile(2))));
        Near(IdentityExposure0, Develop(NonDngRaw(), Publish(PolicyProfile(3))));
        Near(IdentityExposure0, Develop(IdentityRaw(), Publish(ExposureProfile("  acme   model a ", IdentityMatrix, CancellingForward, offset: (0, 1)))));
    }

    [Fact]
    public void Disabled_raw_and_raster_files_ignore_the_profile_without_dropping_the_setting()
    {
        var key = Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (-1, 1)));
        var embedded = Pixels(DevelopLinear(ExposureRaw(), RawLoadOptions.FromSettings(ModuleCatalog.Defaults)).Image);
        var disabled = SettingsFor(key, enabled: false);
        Assert.Equal(embedded, Pixels(DevelopLinear(ExposureRaw(), RawLoadOptions.FromSettings(disabled)).Image));
        Assert.Equal(key, disabled.Text("raw.camera_profile"));
        Assert.Equal(RawLoadOptions.FromSettings(ModuleCatalog.Defaults.With("raw.enabled", ParamValue.Of(false))),
            RawLoadOptions.FromSettings(disabled));

        var png = Path.Combine(_root, "raster.png");
        File.WriteAllBytes(png, Convert.FromHexString(
            "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000d49444154789c63f8cfc0f01f00050001ff89993d1d0000000049454e44ae426082"));
        var loaded = ImageLoader.Load(png, 32, RawLoadOptions.FromSettings(SettingsFor(missingKey(), enabled: true)));
        Assert.False(loaded.IsSceneLinear);
        Assert.Equal(1, loaded.Width);
        Assert.True(loaded.Rgba16[0] > loaded.Rgba16[1]);
        Assert.True(loaded.Rgba16[0] > 200);
    }

    [Fact]
    public void Profile_and_custom_white_balance_enter_the_source_identity_and_auto_does_not()
    {
        var key = Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (0, 1)));
        var other = Publish(ExposureProfile("Acme Model A", ScaledMatrix, null, (0, 1)));
        var asShot = SettingsFor(key);
        var customA = SettingsFor(key, mode: "custom", temperature: 4000, tint: 0);
        var customB = SettingsFor(key, mode: "custom", temperature: 7000, tint: 12);
        var autoTemp = SettingsFor(null, mode: "custom", temperature: 4000);
        var autoOther = SettingsFor(null, mode: "custom", temperature: 8000);
        Assert.NotEqual(RawLoadOptions.FromSettings(asShot), RawLoadOptions.FromSettings(SettingsFor(other)));
        Assert.NotEqual(RawLoadOptions.FromSettings(customA), RawLoadOptions.FromSettings(customB));
        Assert.Equal(RawLoadOptions.FromSettings(asShot), RawLoadOptions.FromSettings(SettingsFor(key, mode: "as-shot", temperature: 4000)));
        Assert.Equal(RawLoadOptions.FromSettings(autoTemp), RawLoadOptions.FromSettings(autoOther));
        Assert.Equal(RawLoadOptions.FromSettings(ModuleCatalog.Defaults), RawLoadOptions.FromSettings(SettingsFor(null)));

        var shot = ImageLoader.Load(WriteScene(IdentityRaw()), 32, RawLoadOptions.FromSettings(asShot));
        var warm = ImageLoader.Load(WriteScene(IdentityRaw()), 32, RawLoadOptions.FromSettings(customA));
        Assert.NotNull(shot.WhiteBalance);
        Assert.Equal(5451, shot.WhiteBalance!.AsShotTemperature);
        Assert.Equal(-13, shot.WhiteBalance.AsShotTint);
        Assert.Null(warm.WhiteBalance!.MatrixFor(4000, 0));
        Assert.NotNull(warm.WhiteBalance.MatrixFor(shot.WhiteBalance.AsShotTemperature, shot.WhiteBalance.AsShotTint));
        Assert.Equal(5451, warm.WhiteBalance.AsShotTemperature);
        AssertDifferent(Pixels(shot.Scene!.Image), Pixels(warm.Scene!.Image));
    }

    [Theory]
    [InlineData(Orientation.Normal, 3000, -40, false, false)]
    [InlineData(Orientation.FlipH, 5000, 25, false, false)]
    [InlineData(Orientation.Rotate180, 7000, 0, false, false)]
    [InlineData(Orientation.FlipV, 5000, 25, false, false)]
    [InlineData(Orientation.Transpose, 5000, 25, false, false)]
    [InlineData(Orientation.Rotate90, 5000, 25, false, false)]
    [InlineData(Orientation.Transverse, 5000, 25, false, false)]
    [InlineData(Orientation.Rotate270, 5000, 25, false, false)]
    [InlineData(Orientation.Rotate90, 5000, 25, true, false)]
    [InlineData(Orientation.Rotate90, 5000, 25, false, true)]
    public void CandidateSamples_MatchRealRedevelopment_WithDualTablesResizingAndOrientation(
        Orientation orientation, double temperature, double tint, bool clipped, bool localTone)
    {
        var raw = Sensor(0.2f, 0.2f, 0.2f, 0, 1, 0, 0, 0, "Acme Model A", size: 12);
        raw.Orientation = orientation;
        var sensor = ((RawData.F32)raw.Data).Samples;
        for (var y = 0; y < 12; y++)
        for (var x = 0; x < 12; x++)
        {
            var i = (y * 12 + x) * 3;
            sensor[i] = .1f + x * .025f;
            sensor[i + 1] = .1f + y * .025f;
            sensor[i + 2] = .15f + (x + y) * .01f;
        }
        if (clipped) sensor[(5 * 12 + 5) * 3] = 1f;
        if (localTone) raw.Color.Profile.GainTableMap = GainRaw().Color.Profile.GainTableMap;
        var path = WriteScene(raw);
        var key = Publish(DualProfile(HueA, HueB, HueLook));
        var settings = SettingsFor(key, localTone: localTone);
        var source = ImageLoader.Load(path, 8, RawLoadOptions.FromSettings(settings)).Downscale(5);
        var candidate = new RawSourceLoader.CandidateSnapshot(source, TestContext.Current.CancellationToken);
        var expected = ImageLoader.Load(path, 8, RawLoadOptions.FromSettings(
            SettingsFor(key, mode: "custom", temperature: temperature, tint: tint, localTone: localTone))).Downscale(5).Scene!.Image;
        if (localTone)
        {
            var withoutTone = ImageLoader.Load(path, 8, RawLoadOptions.FromSettings(
                SettingsFor(key, mode: "custom", temperature: temperature, tint: tint))).Downscale(5).Scene!.Image;
            Assert.Contains(expected.Data.Zip(withoutTone.Data), pair => Math.Abs(pair.First - pair.Second) > 1e-4f);
        }
        candidate.Prepare(temperature, tint);
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var (r, g, b) = candidate.Sample(x, y);
            var i = (y * expected.Width + x) * 3;
            Assert.InRange(Math.Abs(expected.Data[i] - r), 0, 2e-5f);
            Assert.InRange(Math.Abs(expected.Data[i + 1] - g), 0, 2e-5f);
            Assert.InRange(Math.Abs(expected.Data[i + 2] - b), 0, 2e-5f);
        }
    }

    [Fact]
    public async Task White_balance_suggestion_redevelops_nonlinear_tables_and_stale_profile_results_do_not_apply()
    {
        // Generate a physically reachable neutral patch for the selected camera model.
        var tags = new ColorData { ColorMatrix = [Mat3.Identity, null], Illuminant = [21, 0] };
        var neutral = CameraColor.CameraNeutral(tags, Cct.TempTintToXy(4500, 0));
        var path = WriteScene(Sensor((float)neutral[0] * .3f, (float)neutral[1] * .3f, (float)neutral[2] * .3f,
            0, 1, 0, 0, 0, "Acme Model A"));
        var key = Publish(HueProfile(look: false));
        var settings = SettingsFor(key);
        var source = ImageLoader.Load(path, 32, RawLoadOptions.FromSettings(settings));
        var suggestion = PhotoAnalysis.SolveWhiteBalance(source, settings, new PhotoRegion(0, 0, 1, 1), null, TestContext.Current.CancellationToken);
        Assert.NotNull(suggestion);
        Assert.InRange(suggestion!.Temperature, 2500, 9900);
        var custom = settings
            .With("raw.wb_mode", ParamValue.Of("custom"))
            .With("raw.temperature", ParamValue.Of(suggestion.Temperature))
            .With("raw.tint", ParamValue.Of(suggestion.Tint));
        var redone = ImageLoader.Load(path, 32, RawLoadOptions.FromSettings(custom));
        Assert.True(Chroma(redone) < Chroma(source) * 0.5,
            $"suggestion {suggestion.Temperature:0}/{suggestion.Tint:0} did not neutralise the redeveloped sample ({Chroma(redone)} vs {Chroma(source)})");
        Assert.True(PhotoAnalysis.Sample(source, settings, new PhotoRegion(0, 0, 1, 1)).ValidSamples <= PhotoAnalysis.MaxSamples);

        var automatic = PhotoAnalysis.AutomaticWhiteBalance(
            ImageLoader.Load(WriteScene(TwoToneRaw()), 64, RawLoadOptions.FromSettings(settings)), settings, TestContext.Current.CancellationToken);
        Assert.NotEmpty(automatic);

        var optionsA = RawLoadOptions.FromSettings(settings);
        var optionsB = RawLoadOptions.FromSettings(SettingsFor(
            Publish(ExposureProfile("Acme Model A", ScaledMatrix, null, (0, 1)))));
        var first = ImageLoader.Load(path, 32, optionsA);
        var second = ImageLoader.Load(path, 32, optionsB);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallel = Task.WhenAll(
            Task.Run(async () => { await gate.Task; return ImageLoader.Load(path, 32, optionsA); }),
            Task.Run(async () => { await gate.Task; return ImageLoader.Load(path, 32, optionsB); }));
        gate.SetResult();
        var done = await parallel;
        AssertSame(Pixels(first.Scene!.Image), Pixels(done[0].Scene!.Image));
        AssertSame(Pixels(second.Scene!.Image), Pixels(done[1].Scene!.Image));
        AssertDifferent(Pixels(done[0].Scene!.Image), Pixels(done[1].Scene!.Image));

        File.WriteAllBytes(ProfilePath(key), HueProfile(look: true));
        var replaced = Record.Exception(() => ImageLoader.Load(path, 32, optionsA));
        Assert.NotNull(replaced);
        Assert.Equal(CameraProfileFault.HashMismatch, Assert.IsType<CameraProfileException>(replaced).Fault);
        Assert.Null(source.Scene!.Tone);
    }

    [Fact]
    public void External_profile_does_not_resample_embedded_tone_or_mutate_the_shared_look()
    {
        var raw = IdentityRaw();
        raw.Color.Profile = new ProfileLook
        {
            ToneCurve = new ToneCurve { Points = [0f, 0f, 0.2f, 0.05f, 1f, 1f] },
            HueSatMap = [Table(0f, 1f, 1f, 10f, 1f, 0.8f), null],
        };
        var shared = raw.Color.Profile;
        var before = (float[])shared.HueSatMap[0]!.Data.Clone();
        var key = Publish(IdentityToneProfile());
        var scene = LoadScene(raw, key);
        Assert.Null(scene.Scene!.Tone);
        Assert.Equal([0f, 0f, 1f, 1f], scene.RawInfo!.Color.Profile.ToneCurve!.Points);
        Assert.Equal(before, shared.HueSatMap[0]!.Data);
        Assert.NotSame(shared, scene.RawInfo.Color.Profile);
        Assert.Equal(new[] { 1d, 1d, 1d }, scene.RawInfo.Color.AsShotNeutral);
        Assert.Equal(1f, scene.RawInfo.White[0]);
    }

    [Fact]
    public void Thumbnail_edge_redevelops_when_a_profile_is_selected()
    {
        var path = WriteScene(IdentityRaw());
        var key = Publish(ExposureProfile("Acme Model A", IdentityMatrix, CancellingForward, offset: (0, 1)));
        var thumb = ImageLoader.Load(path, ImageLoader.ThumbnailLongEdge, RawLoadOptions.FromSettings(SettingsFor(key)));
        var full = ImageLoader.Load(path, 32, RawLoadOptions.FromSettings(SettingsFor(key)));
        Assert.True(thumb.IsSceneLinear);
        Near(IdentityExposure0, Pixels(thumb.Scene!.Image));
        AssertSame(Pixels(thumb.Scene.Image), Pixels(full.Scene!.Image));
    }

    // ---- fixtures ---------------------------------------------------------

    private static readonly float[] HueA = [0f, 1f, 1f, 30f, 1f, 0.5f];
    private static readonly float[] HueB = [0f, 1f, 1f, -15f, 1f, 1.2f];
    private static readonly float[] HueAChanged = [0f, 1f, 1f, -40f, 1f, 0.7f];
    private static readonly float[] HueBChanged = [0f, 1f, 1f, 25f, 1f, 0.4f];

    private string Publish(byte[] dcp)
    {
        using var source = new MemoryStream(dcp, writable: false);
        using var draft = CameraProfileStore.Stage(AppPaths.UserCameraProfiles, source, "Acme", TestContext.Current.CancellationToken);
        return draft.Publish(TestContext.Current.CancellationToken);
    }

    private static string ProfilePath(string key) => Path.Combine(AppPaths.UserCameraProfiles, key + ".dcp");

    private float[] Develop(RawImage raw, string? profileKey, bool localTone = false) =>
        Pixels(DevelopLinear(raw, RawLoadOptions.FromSettings(SettingsFor(profileKey, localTone: localTone))).Image);

    private static (RgbImage Image, CameraTone? Tone) DevelopLinear(RawImage raw, RawLoadOptions options) =>
        RawSourceLoader.DevelopLinear(raw, ReadOnlyMemory<byte>.Empty, 32, options);

    private SourceImage LoadScene(RawImage raw, string key)
    {
        var path = WriteScene(raw);
        return ImageLoader.Load(path, 32, RawLoadOptions.FromSettings(SettingsFor(key)));
    }

    private string WriteScene(RawImage raw)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".dng");
        File.WriteAllBytes(path, DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Uncompressed() }));
        return path;
    }

    private static Settings SettingsFor(string? key, bool enabled = true, string mode = "as-shot",
        double temperature = 6500, double tint = 0, bool localTone = false)
    {
        var settings = ModuleCatalog.Defaults
            .With("raw.enabled", ParamValue.Of(enabled))
            .With("raw.camera_local_tone", ParamValue.Of(localTone))
            .With("raw.wb_mode", ParamValue.Of(mode))
            .With("raw.temperature", ParamValue.Of(temperature))
            .With("raw.tint", ParamValue.Of(tint));
        return key is null ? settings : settings.With("raw.camera_profile", ParamValue.Of(key));
    }

    private static string missingKey() => "dcp-" + new string('b', 64);

    private static RawImage ExposureRaw(string? unique = "Acme Model A") =>
        Sensor(0.45f, 0.60f, 0.30f, black: 0.2f, white: 1.2f, imageBaseline: 1, profileOffset: 0.5, summed: 1.5, unique);

    private static RawImage CalibrationRaw(string? signature) =>
        Sensor(0.25f, 0.40f, 0.10f, black: 0, white: 1, imageBaseline: 0, profileOffset: 0, summed: 0,
            "Acme Model A", signature, Mat3.Diag(2, 1, 1));

    private static RawImage ChromaticRaw() =>
        Sensor(0.62f, 0.18f, 0.11f, 0, 1, 0, 0, 0, "Acme Model A");

    private static RawImage SdrRaw()
    {
        var raw = Sensor(0.45f, 0.45f, 0.45f, 0, 1, 0, 0, 2, "Acme Model A");
        raw.Color.ColorMatrix = [Mat3.Identity, null];
        raw.Color.ForwardMatrix = [Forward(CancellingForward), null];
        raw.Color.Illuminant = [21, 0];
        raw.Color.Profile = new ProfileLook { LookTable = Table(0, 1, 1, 0, 1, 1) };
        return raw;
    }

    private static RawImage PushRaw()
    {
        var raw = Sensor(0.62f, 0.18f, 0.11f, 0, 1, 0, 0, 1, "Acme Model A");
        raw.Color.ColorMatrix = [Mat3.Identity, null];
        raw.Color.ForwardMatrix = [Forward(CancellingForward), null];
        raw.Color.Illuminant = [21, 0];
        raw.Color.Profile = new ProfileLook { LookTable = Table(0, 1, 1, 0, 1, 2) };
        return raw;
    }

    private static RawImage GainRaw()
    {
        var raw = FlatRaw(0, 0.25f, 0.40f, 0.10f);
        raw.Color.Profile = new ProfileLook
        {
            GainTableMap = new GainTableMap
            {
                PointsV = 1, PointsH = 1, PointsN = 1,
                SpacingV = 1, SpacingH = 1, OriginV = 0, OriginH = 0,
                Weights = [1f, 0f, 0f, 0f, 0f], Gamma = 1f, Gains = [1.5f],
            },
        };
        return raw;
    }

    private static RawImage FlatRaw(double baseline, float r, float g, float b) =>
        Sensor(r, g, b, 0, 1, baseline, 0, baseline, "Acme Model A");

    private static RawImage IdentityRaw() => FlatRaw(0, 0.25f, 0.40f, 0.10f);

    private static RawImage DualRaw(Xy white)
    {
        var raw = IdentityRaw();
        raw.Color.AsShotWhiteXy = white;
        raw.Color.AsShotNeutral = null;
        return raw;
    }

    private static RawImage NonDngRaw()
    {
        var raw = IdentityRaw();
        raw.Format = RawFormat.Arw;
        raw.Metadata.Make = "Acme";
        raw.Metadata.Model = "Model A";
        raw.Metadata.UniqueCameraModel = "NOT USED FOR ARW";
        return raw;
    }

    private static RawImage TwoToneRaw()
    {
        var tags = new ColorData { ColorMatrix = [Mat3.Identity, null], Illuminant = [21, 0] };
        var first = CameraColor.CameraNeutral(tags, Cct.TempTintToXy(3200, 0));
        var second = CameraColor.CameraNeutral(tags, Cct.TempTintToXy(7500, 0));
        var raw = Sensor((float)first[0] * .3f, (float)first[1] * .3f, (float)first[2] * .3f,
            0, 1, 0, 0, 0, "Acme Model A", size: 6);
        var data = ((RawData.F32)raw.Data).Samples;
        for (var y = 0; y < 6; y++)
        for (var x = 3; x < 6; x++)
        {
            var i = (y * 6 + x) * 3;
            data[i] = (float)second[0] * .3f; data[i + 1] = (float)second[1] * .3f; data[i + 2] = (float)second[2] * .3f;
        }
        return raw;
    }

    private static RawImage Sensor(float r, float g, float b, float black, float white, double imageBaseline,
        double profileOffset, double summed, string? unique, string? calibrationSignature = null, Mat3? calibration = null, int size = 2)
    {
        var data = new float[size * size * 3];
        for (var i = 0; i < data.Length; i += 3)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b;
        }
        var full = new PixelRect(0, 0, size, size);
        return new RawImage
        {
            Format = RawFormat.Dng, Width = size, Height = size, Cpp = 3,
            Data = new RawData.F32(data), Bits = 16,
            Black = BlackLevel.Uniform(black), White = [white, white, white],
            ActiveArea = full, Crop = full,
            Color = new ColorData
            {
                Illuminant = [21, 0],
                ColorMatrix = [Mat3.Diag(3, 1, 1), null],
                AsShotNeutral = [1, 1, 1],
                ImageBaselineExposure = imageBaseline,
                ProfileBaselineExposureOffset = profileOffset,
                BaselineExposure = summed,
                CameraCalibration = [calibration, null],
                CameraCalibrationSignature = calibrationSignature,
            },
            Metadata = new Metadata { Model = unique, UniqueCameraModel = unique },
        };
    }

    private static HsvTable Table(float h0, float s0, float v0, float h1, float s1, float v1) => new()
    {
        HueDivisions = 1, SatDivisions = 2, ValDivisions = 1,
        Data = [h0, s0, v0, h1, s1, v1],
    };

    private static Mat3 Forward((int Num, int Den)[] rows)
    {
        var values = rows.Select(pair => (double)pair.Num / pair.Den).ToArray();
        return Mat3.FromRows(values);
    }

    private static byte[] ExposureProfile(string model, (int Num, int Den)[] color, (int Num, int Den)[]? forward,
        (int Num, int Den) offset, int? blackRender = null, int policy = 0) =>
        Profile(model, color, forward, offset, policy, blackRender, signature: null, hue: null, hue2: null, look: null, lookEncoding: 0);

    private static byte[] SignatureProfile(string? signature) =>
        Profile("Acme Model A", IdentityMatrix, null, (0, 1), 0, null, signature, null, null, null, 0);

    private static byte[] HueProfile(bool look) =>
        Profile("Acme Model A", IdentityMatrix, CancellingForward, (0, 1), 0, null, null, HueA, null, look ? HueLook : null, 0);

    private static readonly float[] HueLook = [0f, 1f, 1f, 0f, 1f, 0.25f];

    private static byte[] SdrProfile(ushort encoding) =>
        Profile("Acme Model A", IdentityMatrix, CancellingForward, (2, 1), 0, null, null, null, null,
            encoding == 0 ? [0f, 1f, 1f, 0f, 1f, 1f] : Enumerable.Repeat(new[] { 0f, 1f, 1f }, 4).SelectMany(v => v).ToArray(),
            encoding, lookValDivisions: encoding == 0 ? 1 : 2);

    private static byte[] PushProfile() =>
        Profile("Acme Model A", IdentityMatrix, CancellingForward, (1, 1), 0, null, null, null, null,
            [0f, 1f, 1f, 0f, 1f, 2f], 0);

    private static byte[] DualProfile(float[] first, float[] second, float[]? look) =>
        Profile("Acme Model A", IdentityMatrix, CancellingForward, (0, 1), 0, null, null, first, second, look, 0, dual: true);

    private static byte[] PolicyProfile(int policy) =>
        Profile("Acme Model A", IdentityMatrix, CancellingForward, (0, 1), policy, null, null, null, null, null, 0);

    private static byte[] IdentityToneProfile() =>
        Profile("Acme Model A", IdentityMatrix, CancellingForward, (0, 1), 0, null, null, null, null, null, 0, toneIdentity: true);

    private static byte[] Profile(string model, (int Num, int Den)[] color, (int Num, int Den)[]? forward,
        (int Num, int Den) offset, int policy, int? blackRender, string? signature, float[]? hue, float[]? hue2,
        float[]? look, ushort lookEncoding, bool dual = false, bool toneIdentity = false, int lookValDivisions = 1)
    {
        var builder = new DcpBuilder();
        builder.Add(Tags.UniqueCameraModel, FieldType.Ascii, model);
        builder.Add(Tags.ColorMatrix1, FieldType.SRational, color);
        // 單光源用 21（權重必為 1）。雙光源把 17（2856K）放在光源 1、21（6504K）放在光源 2，
        // 使 Kim(2856) 的權重為 1（只用第一張 HueSat），Kim(6504) 的權重為 0（只用第二張）。
        builder.Add(Tags.CalibrationIlluminant1, FieldType.Short, dual ? 17 : 21);
        if (forward is not null) builder.Add(Tags.ForwardMatrix1, FieldType.SRational, forward);
        if (dual)
        {
            builder.Add(Tags.CalibrationIlluminant2, FieldType.Short, 21);
            builder.Add(Tags.ColorMatrix2, FieldType.SRational, color);
            if (forward is not null) builder.Add(Tags.ForwardMatrix2, FieldType.SRational, forward);
        }
        if (hue is not null)
        {
            builder.Add(Tags.ProfileHueSatMapDims, FieldType.Long, 1u, 2u, 1u);
            builder.Add(Tags.ProfileHueSatMapData1, FieldType.Float, hue);
            if (hue2 is not null) builder.Add(Tags.ProfileHueSatMapData2, FieldType.Float, hue2);
        }
        if (look is not null)
        {
            builder.Add(Tags.ProfileLookTableDims, FieldType.Long, 1u, 2u, (uint)lookValDivisions);
            builder.Add(Tags.ProfileLookTableData, FieldType.Float, look);
            if (lookEncoding != 0) builder.Add(Tags.ProfileLookTableEncoding, FieldType.Long, (uint)lookEncoding);
        }
        if (toneIdentity) builder.Add(Tags.ProfileToneCurve, FieldType.Float, [0f, 0f, 1f, 1f]);
        if (offset != (0, 1)) builder.Add(Tags.BaselineExposureOffset, FieldType.SRational, [offset]);
        if (policy != 0) builder.Add(Tags.ProfileEmbedPolicy, FieldType.Long, (uint)policy);
        if (blackRender is { } black) builder.Add(Tags.DefaultBlackRender, FieldType.Long, (uint)black);
        if (signature is not null) builder.Add(Tags.ProfileCalibrationSignature, FieldType.Ascii, signature);
        return builder.Build();
    }

    private static float[] Pixels(RgbImage image) => [image.Data[0], image.Data[1], image.Data[2]];

    private static float[] Pixels(SourceImage source) => Pixels(source.Scene!.Image);

    private static float Chroma(SourceImage source)
    {
        var pixel = Pixels(source);
        return pixel.Max() - pixel.Min();
    }

    private static void Near(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var tol = TolK + TolK * Math.Abs(expected[i]);
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tol,
                $"channel {i}: expected {expected[i].ToString(CultureInfo.InvariantCulture)} got {actual[i].ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void AssertSame(float[] expected, float[] actual) => Near(expected, actual);

    private static void AssertDifferent(float[] left, float[] right)
    {
        var delta = 0f;
        for (var i = 0; i < left.Length; i++) delta = Math.Max(delta, Math.Abs(left[i] - right[i]));
        Assert.True(delta > 0.02f, $"pixels did not differ ({delta})");
    }

    private static void AssertUseFault(Action action, string fault)
    {
        var ex = Record.Exception(action);
        Assert.NotNull(ex);
        Assert.IsNotType<CameraProfileException>(ex);
        Assert.Equal("CameraProfileUseException", ex.GetType().Name);
        Assert.Equal(fault, ex.GetType().GetProperty("Fault")!.GetValue(ex)!.ToString());
    }

    private sealed class DcpBuilder
    {
        private readonly List<(ushort Tag, FieldType Type, byte[] Data)> _entries = [];

        public void Add(ushort tag, FieldType type, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            _entries.Add((tag, type, bytes));
        }

        public void Add(ushort tag, FieldType type, params (int Num, int Den)[] values)
        {
            var data = new byte[values.Length * 8];
            for (var i = 0; i < values.Length; i++)
            {
                Write32(data, i * 8, unchecked((uint)values[i].Num));
                Write32(data, i * 8 + 4, unchecked((uint)values[i].Den));
            }
            _entries.Add((tag, type, data));
        }

        public void Add(ushort tag, FieldType type, int value)
        {
            var unsigned = (ushort)value;
            _entries.Add((tag, type, [(byte)unsigned, (byte)(unsigned >> 8)]));
        }

        public void Add(ushort tag, FieldType type, params uint[] values)
        {
            var data = new byte[values.Length * 4];
            for (var i = 0; i < values.Length; i++) Write32(data, i * 4, values[i]);
            _entries.Add((tag, type, data));
        }

        public void Add(ushort tag, FieldType type, float[] values)
        {
            var data = new byte[values.Length * 4];
            for (var i = 0; i < values.Length; i++) Write32(data, i * 4, BitConverter.SingleToUInt32Bits(values[i]));
            _entries.Add((tag, type, data));
        }

        public byte[] Build()
        {
            var output = new List<byte> { (byte)'I', (byte)'I', 0x52, 0x43, 8, 0, 0, 0 };
            output.Add((byte)_entries.Count);
            output.Add((byte)(_entries.Count >> 8));
            var payloads = new List<byte>();
            var dataStart = 8 + 2 + _entries.Count * 12 + 4;
            foreach (var entry in _entries)
            {
                void U16(ushort v) { output.Add((byte)v); output.Add((byte)(v >> 8)); }
                void U32(uint v) { output.Add((byte)v); output.Add((byte)(v >> 8)); output.Add((byte)(v >> 16)); output.Add((byte)(v >> 24)); }
                U16(entry.Tag);
                U16((ushort)entry.Type);
                var count = entry.Type switch
                {
                    FieldType.SRational => (uint)(entry.Data.Length / 8),
                    FieldType.Float or FieldType.Long => (uint)(entry.Data.Length / 4),
                    FieldType.Short => (uint)(entry.Data.Length / 2),
                    _ => (uint)entry.Data.Length,
                };
                U32(count);
                if (entry.Data.Length <= 4)
                {
                    output.AddRange(entry.Data);
                    for (var i = entry.Data.Length; i < 4; i++) output.Add(0);
                }
                else
                {
                    U32((uint)(dataStart + payloads.Count));
                    payloads.AddRange(entry.Data);
                }
            }
            output.AddRange([0, 0, 0, 0]);
            output.AddRange(payloads);
            return output.ToArray();
        }

        private static void Write32(byte[] data, int at, uint value)
        {
            data[at] = (byte)value;
            data[at + 1] = (byte)(value >> 8);
            data[at + 2] = (byte)(value >> 16);
            data[at + 3] = (byte)(value >> 24);
        }
    }
}
