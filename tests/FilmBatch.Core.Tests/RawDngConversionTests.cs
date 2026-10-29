// 移植自 LightCraft crates/engine/src/camera_preview.rs tests 的 DNG 色彩／色調行為斷言
//（dng_colour、dng_tone_eligible、with_starting_colour／starting_colour_tags）＋DNG roundtrip 行為測試。
// 期望值獨立於實作導出；合成 raw 不依賴外部樣本。
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using Xunit;
using RawToneCurve = FilmBatch.Raw.Color.ToneCurve;

namespace FilmBatch.Core.Tests;

public sealed class RawDngConversionTests
{
    private static RawImage RawOf(RawFormat format, string? make, string? model, ColorData? color = null, float[]? wb = null)
    {
        var area = new PixelRect(0, 0, 2, 2);
        return new RawImage
        {
            Format = format,
            Width = 2,
            Height = 2,
            Cpp = 1,
            Data = new RawData.U16(new ushort[4]),
            Cfa = null,
            Bits = 14,
            Black = new BlackLevel(),
            White = [16383f],
            ActiveArea = area,
            Crop = area,
            Orientation = Orientation.Normal,
            Color = color ?? new ColorData(),
            WbMultipliers = wb ?? [2.0f, 1.0f, 1.5f],
            Opcodes = new OpcodeLists(),
            Metadata = new Metadata { Make = make, Model = model },
        };
    }

    private static CameraLook TestLook()
    {
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var x = 0.005f * MathF.Pow(1.2f, i);
            knots[2 * i] = x;
            knots[2 * i + 1] = 0.95f * x / (x + 0.1f);
        }
        var tone = CameraTone.Create(knots)!.WithChroma([1.3f, 1.2f, 1.1f, 1.0f, 1.0f, 0.9f, 0.6f, 0.3f])!;
        var table = new HsvTable
        {
            HueDivisions = 4,
            SatDivisions = 2,
            ValDivisions = 1,
            Data = Enumerable.Repeat(new[] { 5.0f, 1.1f, 1.0f }, 8).SelectMany(v => v).ToArray(),
            SrgbValue = false,
        };
        return new CameraLook(
            new Mat3(1.6, -0.4, -0.2, -0.2, 1.4, -0.2, 0.0, -0.3, 1.3), tone, table);
    }

    /// <summary>讀取端把 <c>ProfileToneCurve</c> 重新取樣成 32 節點 CameraTone 的對應（files.rs 的 dng_tone_curve 讀法）。</summary>
    private static float[] ResampledKnots(RawToneCurve curve)
    {
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var x = MathF.Pow(2, -12f + 12f * i / 31f);
            knots[2 * i] = x;
            knots[2 * i + 1] = MathF.Min(curve.Eval(x), 0.9995f);
        }
        return knots;
    }

    [Fact]
    public void StartingColourRejectsExcessiveLensChainWithoutChangingRaw()
    {
        var raw = RawOf(RawFormat.Arw, "SONY", "Synthetic camera");
        var lens = new FixVignetteRadial([0, 0, 0, 0, 0], [.5, .5]);
        raw.Opcodes.List3.AddRange(Enumerable.Repeat<Opcode>(lens, 9));
        var originalColor = raw.Color;
        var error = Assert.Throws<RawException>(() => RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty));
        Assert.Equal(RawErrorKind.Limit, error.Kind);
        Assert.Same(originalColor, raw.Color);
        Assert.Equal(9, raw.Opcodes.List3.Count);
    }

    [Fact]
    public void DngToneEligible_AllBranches()
    {
        var own = new ColorData { ColorMatrix = [Mat3.Identity, null], Illuminant = [17, 21] };
        var t = CameraColor.CameraTransformOf(own, new Xy(0.3127, 0.3290));
        Assert.False(t.MatrixIsFallback, "fixture: own matrix transform");

        // DNG + 自有矩陣 + 無曲線 → eligible
        Assert.True(CameraPreview.DngToneEligible(RawOf(RawFormat.Dng, "Apple", "iPhone", own), t));

        // 非 DNG（CR2）→ 不合格
        Assert.False(CameraPreview.DngToneEligible(RawOf(RawFormat.Cr2, "Canon", "Canon EOS 40D", own), t));

        // fallback 矩陣（無自有矩陣）→ 不合格
        var fallback = CameraColor.CameraTransformOf(new ColorData(), new Xy(0.3127, 0.3290));
        Assert.True(fallback.MatrixIsFallback);
        Assert.False(CameraPreview.DngToneEligible(RawOf(RawFormat.Dng, "Apple", "iPhone", new ColorData()), fallback));

        // 非恆等 ProfileToneCurve → 不合格
        var bent = own.Clone();
        bent.Profile = new ProfileLook { ToneCurve = RawToneCurve.FromTag([0, 0, 0.5, 0.7, 1.0, 1.0]) };
        Assert.NotNull(bent.Profile.ToneCurve);
        Assert.False(CameraPreview.DngToneEligible(RawOf(RawFormat.Dng, "Apple", "iPhone", bent), t));

        // LookTable → 不合格
        var withLook = own.Clone();
        withLook.Profile = new ProfileLook { LookTable = new HsvTable { HueDivisions = 4, SatDivisions = 2, ValDivisions = 1, Data = new float[24] } };
        Assert.False(CameraPreview.DngToneEligible(RawOf(RawFormat.Dng, "Apple", "iPhone", withLook), t));

        // ProfileGainTableMap → 不合格（REQ-RAW-04：有 gain map 的 DNG 不再額外擬合全域 JPEG tone）。
        // 邊界情況（上游 EXP-04）：tone 設為恆等的副本仍攜帶 gain table，同樣不合格。
        var gain = own.Clone();
        gain.Profile = new ProfileLook { ToneCurve = RawToneCurve.FromTag([0, 0, 0.5, 0.5, 1.0, 1.0]) };
        gain.Profile.GainTableMap = new GainTableMap
        {
            PointsV = 2, PointsH = 2, PointsN = 4,
            SpacingV = 0.5, SpacingH = 0.5, OriginV = 0.25, OriginH = 0.25,
            Weights = [0, 1, 0, 0, 0], Gamma = 1f,
            Gains = Enumerable.Repeat(1f, 16).ToArray(),
        };
        Assert.False(CameraPreview.DngToneEligible(RawOf(RawFormat.Dng, "Apple", "iPhone", gain), t));

        // 無 gain table 的恆等曲線 → 合格
        var identity = own.Clone();
        identity.Profile = new ProfileLook { ToneCurve = RawToneCurve.FromTag([0, 0, 0.5, 0.5, 1.0, 1.0]) };
        Assert.True(CameraPreview.DngToneEligible(RawOf(RawFormat.Dng, "Apple", "iPhone", identity), t));
    }

    [Fact]
    public void WithStartingColour_LeavesRawWithOwnMatricesAlone()
    {
        var color = new ColorData { ColorMatrix = [Mat3.Identity, null], Illuminant = [17, 21] };
        var raw = RawOf(RawFormat.Dng, "Apple", "iPhone", color);
        RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty);
        Assert.Same(color, raw.Color);
    }

    [Fact]
    public void WithStartingColour_MeasuredCameraCarriesSpectralMatrices()
    {
        var raw = RawOf(RawFormat.Cr2, "Canon", "Canon EOS 600D");
        RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty);
        var camera = Spectral.Find("Canon", "Canon EOS 600D");
        Assert.NotNull(camera);
        Assert.Equal(new ushort[] { 17, 21 }, raw.Color.Illuminant);
        Assert.Equal(camera.ColorMatrix[0], raw.Color.ColorMatrix[0]);
        Assert.Equal(camera.ColorMatrix[1], raw.Color.ColorMatrix[1]);
        Assert.NotNull(raw.Color.ForwardMatrix[0]);
        Assert.NotNull(raw.Color.ForwardMatrix[1]);
        Assert.All(raw.Color.ForwardMatrix[0]!.Value.ToArray(), v => Assert.True(double.IsFinite(v)));
        Assert.NotNull(raw.Color.AsShotNeutral);
        Assert.Null(raw.Color.AsShotWhiteXy);
        // 擬合失敗（無 JPEG）仍給中繼資料，但不冒出假外觀：無 hue/sat 表、無 tone curve。
        Assert.Null(raw.Color.Profile.HueSatMap[0]);
        Assert.Null(raw.Color.Profile.ToneCurve);
    }

    [Fact]
    public void WithStartingColour_NoJpegOrMeasuredModelDoesNotInventMatrices()
    {
        var color = new ColorData();
        var raw = RawOf(RawFormat.Cr2, "Unknown", "Uncalibrated camera", color);
        RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty);
        Assert.Same(color, raw.Color);
        Assert.False(CameraColor.HasMatrix(raw.Color));
        Assert.All(raw.Color.ForwardMatrix, m => Assert.Null(m));
        Assert.True(raw.Color.Profile.IsEmpty());
    }

    [Fact]
    public void WithStartingColour_KeepsSensorLensOpsAndRestoresList3()
    {
        var raw = RawOf(RawFormat.Cr2, "Canon", "Canon EOS 600D");
        var warp = new WarpRectilinear([[1.03, -0.1, 0.02, 0.018, 0.0, 0.0]], [0.5, 0.5]);
        var other = new WarpRectilinear2([new double[OpcodeOps.Wr2Len]], [0.5, 0.5], false, false);
        raw.Opcodes.List3.Add(warp);
        raw.Opcodes.List3.Add(other);
        var before = raw.Color;
        RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty);
        // 鏡頭校正留在 DNG 中：List3 復原且逐項相同。
        Assert.Equal(new Opcode[] { warp, other }, raw.Opcodes.List3);
        // 感光樣本不被觸碰（U16 全零仍全零）。
        var data = (RawData.U16)raw.Data;
        Assert.All(data.Samples, v => Assert.Equal((ushort)0, v));
        Assert.NotSame(before, raw.Color);
    }

    [Fact]
    public void StartingColourTags_NeutralMatchesWbMultipliers()
    {
        // upstream a_merges_white_balance_from_the_multipliers_is_the_one_the_starting_colour_tags_have 的前半：
        // 合成 tags 的 AsShotNeutral 與倍率的倒數只差縮放（上游 1e-3）。
        foreach (var (model, mul) in new[] { ("Canon EOS 600D", new[] { 2.0f, 1.0f, 1.5f }), ("Canon EOS 600D", new[] { 1.9f, 1.0f, 1.7f }) })
        {
            var raw = RawOf(RawFormat.Cr2, "Canon", model, wb: mul);
            var tags = RawDngConversion.StartingColourTags(raw, ReadOnlyMemory<byte>.Empty);
            Assert.NotNull(tags);
            var neutral = tags.AsShotNeutral;
            Assert.NotNull(neutral);
            var scale = neutral[1] * mul[1];
            for (var i = 0; i < 3; i++)
                Assert.True(Math.Abs(neutral[i] * mul[i] - scale) < 1e-3,
                    $"{model}: neutral {string.Join(',', neutral)} vs 1/{string.Join(',', mul)}");
        }
    }

    [Fact]
    public void DngColour_RendersLikeTheRawsStartingColour()
    {
        var t = CameraColor.CameraTransformOf(new ColorData(), new Xy(0.36, 0.37));
        Assert.Contains(t.Wb, w => Math.Abs(w - 1.0f) > 0.1);
        var look = TestLook();
        var samples = new[] { new[] { 0.2, 0.3, 0.1 }, new[] { 0.05, 0.02, 0.4 }, new[] { 0.6, 0.6, 0.6 }, new[] { 0.01, 0.5, 0.03 } };
        double[] Render(Mat3 m, float[] wb, double ev, double[] c)
        {
            var p = m.Apply(c[0] * wb[0], c[1] * wb[1], c[2] * wb[2]);
            var k = Math.Pow(2, ev);
            return [p.X * k, p.Y * k, p.Z * k];
        }
        foreach (var l in new CameraLook?[] { look, null })
        {
            var dng = RawDngConversion.DngColour(new ColorData(), new RawDngConversion.DngWhite.At(t.WhiteXy), t, l);
            Assert.NotNull(dng);
            var back = CameraColor.CameraTransformOf(dng, CameraColor.AsShotWhiteOf(dng, null));
            for (var i = 0; i < 3; i++)
                Assert.True(Math.Abs(back.Wb[i] - t.Wb[i]) < 1e-4 * t.Wb[i], $"{back.Wb[i]} vs {t.Wb[i]}");
            var m = l is null ? t.Matrix : l.Matrix.Mul(t.Matrix);
            foreach (var c in samples)
            {
                var (want, got) = (Render(m, t.Wb, t.BaselineExposure, c), Render(back.Matrix, back.Wb, dng.BaselineExposure, c));
                for (var i = 0; i < 3; i++)
                    Assert.True(Math.Abs(want[i] - got[i]) < 1e-5, $"sample: {want[i]} vs {got[i]}");
            }
            var tone = dng.Profile.ToneCurve;
            if (l is null)
            {
                Assert.Null(tone);
                Assert.Null(dng.Profile.HueSatMap[0]);
            }
            else
            {
                Assert.NotNull(tone);
                Assert.Same(look.HueSat, dng.Profile.HueSatMap[0]);
                // 讀取端 32 節點重新取樣後與擬合曲線最差誤差（上游上界 4e-3）。
                var resampled = CameraTone.Create(ResampledKnots(tone));
                Assert.NotNull(resampled);
                var worst = 0f;
                for (var i = 0; i <= 120; i++)
                {
                    var x = MathF.Pow(2, -10f + i / 12f);
                    worst = Math.Max(worst, Math.Abs(resampled.Apply(x) - l.Tone.Apply(x)));
                }
                Assert.True(worst < 4e-3f, $"worst {worst}");
            }
        }
    }

    [Fact]
    public void DngColour_DeclaresTheWhiteBalanceTheSourceShows()
    {
        var tags = new ColorData();
        var asShot = new[] { 2.5039f, 1.0f, 1.5078f };
        var t = CameraColor.CameraTransformOf(tags, CameraColor.AsShotWhiteOf(tags, asShot));
        var look = TestLook() with
        {
            Matrix = new Mat3(1.75, -0.4, -0.2, -0.2, 1.35, -0.2, 0.05, -0.3, 1.3),
        };
        var tint = look.Matrix.Apply(1.0, 1.0, 1.0);
        Assert.True(Math.Abs(tint.X - tint.Y) > 0.1, "a look that tints white");
        void RendersAsShot(ColorData dng)
        {
            var r = CameraColor.CameraTransformOf(dng, CameraColor.AsShotWhiteOf(dng, null));
            for (var i = 0; i < 3; i++)
                Assert.True(Math.Abs(r.Wb[i] - t.Wb[i]) < 1e-4 * t.Wb[i], $"{r.Wb[i]} vs {t.Wb[i]}");
            var m = look.Matrix.Mul(t.Matrix);
            foreach (var c in new[] { new[] { 0.2, 0.3, 0.1 }, new[] { 0.05, 0.02, 0.4 }, new[] { 0.6, 0.6, 0.6 } })
            {
                var want = m.Apply(c[0] * t.Wb[0], c[1] * t.Wb[1], c[2] * t.Wb[2]);
                var got = r.Matrix.Apply(c[0] * r.Wb[0], c[1] * r.Wb[1], c[2] * r.Wb[2]);
                var ev = Math.Pow(2, t.BaselineExposure);
                var evDng = Math.Pow(2, dng.BaselineExposure);
                Assert.True(Math.Abs(want.X * ev - got.X * evDng) < 1e-4 &&
                            Math.Abs(want.Y * ev - got.Y * evDng) < 1e-4 &&
                            Math.Abs(want.Z * ev - got.Z * evDng) < 1e-4,
                    $"want {want} got {got}");
            }
        }
        // 來源刻度顯示的白點（Sony 預設的讀法、relative 刻度的 6500 K / 0）。
        foreach (var xy in new[] { Cct.TempTintToXy(5600.0, 3.0), Cct.TempTintToXy(6500.0, 0.0) })
        {
            var dng = RawDngConversion.DngColour(tags, new RawDngConversion.DngWhite.At(xy), t, look);
            Assert.NotNull(dng);
            var back = CameraColor.AsShotWhiteOf(dng, null);
            Assert.True(Math.Abs(back.X - xy.X) < 1e-6 && Math.Abs(back.Y - xy.Y) < 1e-6,
                $"as shot {back}, shown {xy}");
            RendersAsShot(dng);
        }
        // 有實測矩陣的相機（ILCE-7M3）：DNG 以同一組矩陣讀每個白點。
        var camera = Spectral.Find("SONY", "ILCE-7M3");
        Assert.NotNull(camera);
        var model = tags.Clone();
        Assert.True(camera.Fill(model));
        var shown = Spectral.AsShotWhiteThrough(camera, tags, asShot);
        Assert.NotNull(shown);
        var dngMat = RawDngConversion.DngColour(tags, new RawDngConversion.DngWhite.Matrices(model), t, look);
        Assert.NotNull(dngMat);
        var backXy = CameraColor.AsShotWhiteOf(dngMat, null);
        Assert.True(Math.Abs(backXy.X - shown.Value.X) < 1e-5 && Math.Abs(backXy.Y - shown.Value.Y) < 1e-5,
            $"as shot {backXy}, shown {shown}");
        foreach (var w in new[] { new Xy(0.3127, 0.329), new Xy(0.4476, 0.4074), new Xy(0.3457, 0.3585) })
        {
            var a = CameraColor.WbMultipliers(model, w);
            var b = CameraColor.WbMultipliers(dngMat, w);
            for (var i = 0; i < 3; i++)
                Assert.True(Math.Abs(a[i] - b[i]) < 1e-6 * a[i], $"{w}: {a[i]} vs {b[i]}");
        }
        RendersAsShot(dngMat);
    }

    [Fact]
    public void WithStartingColour_MeasuredFallbackKeepsExistingProfile()
    {
        var profile = new ProfileLook
        {
            ToneCurve = RawToneCurve.FromTag([0, 0, 0.5, 0.7, 1, 1]),
            LookTable = TestLook().HueSat,
        };
        var raw = RawOf(RawFormat.Cr2, "Canon", "Canon EOS 600D", new ColorData { Profile = profile });
        RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty);
        Assert.True(CameraColor.HasMatrix(raw.Color));
        Assert.Same(profile.ToneCurve, raw.Color.Profile.ToneCurve);
        Assert.Same(profile.LookTable, raw.Color.Profile.LookTable);
    }

    [Fact]
    public void DngColour_IndependentNeutralAndExposureValues()
    {
        // Row sums are 2, so normalization contributes exactly +1 EV.
        var transform = new CameraTransform(Mat3.Diag(2, 2, 2), [4, 2, 1],
            new Xy(0.3127, 0.3290), false, 0.5);
        var tags = RawDngConversion.DngColour(new ColorData(),
            new RawDngConversion.DngWhite.At(transform.WhiteXy), transform, null);
        Assert.NotNull(tags);
        Assert.Equal(new double[] { 0.25, 0.5, 1 }, tags.AsShotNeutral);
        Assert.Equal(1.5, tags.BaselineExposure, 12);
        Assert.All(tags.ForwardMatrix[0]!.Value.ToArray(), v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void DngColour_InvalidTransformDoesNotCreateDescriptors()
    {
        var declared = new RawDngConversion.DngWhite.At(new Xy(0.3127, 0.3290));
        foreach (var matrix in new[] { Mat3.Diag(0, 0, 0), Mat3.Diag(double.NaN, 1, 1),
                     new Mat3(1, double.PositiveInfinity, double.NegativeInfinity, 0, 1, 0, 0, 0, 1) })
        {
            var transform = new CameraTransform(matrix, [2, 1, 1.5f], declared.Xy, true, 0);
            Assert.Null(RawDngConversion.DngColour(new ColorData(), declared, transform, null));
        }
        var badWb = new CameraTransform(Mat3.Identity, [float.NaN, 1, 1], declared.Xy, true, 0);
        Assert.Null(RawDngConversion.DngColour(new ColorData(), declared, badWb, null));
    }

    [Theory]
    [InlineData(0.12257147f)]
    [InlineData(0.18f)]
    public void GainTableDng_StartingColourKeepsFileTone(float at18)
    {
        var profile = new ProfileLook
        {
            ToneCurve = RawToneCurve.FromTag([0, 0, 0.18, at18, 1, 1]),
            GainTableMap = new GainTableMap
            {
                PointsV = 1, PointsH = 1, PointsN = 2,
                Weights = [0, 1, 0, 0, 0], Gains = [1, 2],
            },
        };
        var color = new ColorData { ColorMatrix = [Mat3.Identity, null], Profile = profile };
        var raw = RawOf(RawFormat.Dng, "Apple", "synthetic tone fixture", color);
        var (_, transform, look) = CameraPreview.StartingColour(raw, ReadOnlyMemory<byte>.Empty);
        Assert.False(CameraPreview.DngToneEligible(raw, transform));
        Assert.Null(look);
        Assert.Same(profile, raw.Color.Profile);
        Assert.InRange(Math.Abs(raw.Color.Profile.ToneCurve!.Eval(0.18f) - at18), 0, 1e-6f);
    }

    [Theory]
    [InlineData(200u)]
    [InlineData(80000u)]
    public void ConvertedDng_RoundtripKeepsSamplesGeometryAndColour(uint iso)
    {
        // 較小的 Bayer 來源：convert 後重新解碼，樣本、幾何與 metadata 不變；色彩由合成描述檔補上。
        const int w = 96, h = 64;
        var data = new ushort[w * h];
        for (var i = 0; i < data.Length; i++) data[i] = (ushort)(512 + (i * 37) % 2048);
        var raw = new RawImage
        {
            Format = RawFormat.Cr2,
            Width = w, Height = h, Cpp = 1,
            Data = new RawData.U16(data),
            Cfa = Cfa.Bayer("RGGB")!,
            Bits = 14,
            Black = BlackLevel.Uniform(256f),
            White = [4095f],
            ActiveArea = new PixelRect(0, 0, w, h),
            Crop = new PixelRect(4, 4, w - 8, h - 8),
            Orientation = Orientation.Rotate90,
            PixelAspect = 2f,
            WbMultipliers = [1.64f, 1.0f, 1.91f],
            Opcodes = new OpcodeLists(),
            Metadata = new Metadata
            {
                Make = "Canon", Model = "Canon EOS 600D", SerialNumber = "1234567",
                LensMake = "Canon", LensModel = "EF 50mm", LensSpec = [50, 50, 1.8, 1.8],
                ExposureTime = 0.008, FNumber = 5.6, Iso = iso, FocalLength = 50,
                FocalLength35mm = 80, ExposureBias = -2.0 / 3,
                CaptureTime = new RawDateTime(2026, 10, 18, 14, 35, 42, 125, 480),
            },
        };
        RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty);
        var dng = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Uncompressed() });
        var back = RawDecoder.Decode(dng);
        var backData = Assert.IsType<RawData.U16>(back.Data);
        Assert.Equal(data, backData.Samples);
        Assert.Equal(raw.ActiveArea, back.ActiveArea);
        Assert.Equal(raw.Crop, back.Crop);
        Assert.Equal(raw.Orientation, back.Orientation);
        Assert.Equal(raw.Info().DevelopedSize(), back.Info().DevelopedSize());
        // DNG stores this WB in AsShotNeutral; decoder does not populate maker-note multipliers.
        Assert.Null(back.WbMultipliers);
        Assert.True(CameraColor.HasMatrix(back.Color), "converted DNG carries a usable colour model");
        // AsShotNeutral 為倍率的倒數等價（縮放自由）。
        var neutral = back.Color.AsShotNeutral;
        Assert.NotNull(neutral);
        var scale = neutral[1] * raw.WbMultipliers![1];
        for (var i = 0; i < 3; i++)
            Assert.True(Math.Abs(neutral[i] * raw.WbMultipliers[i] - scale) < 1e-3,
                $"neutral {neutral[i]} vs 1/{raw.WbMultipliers[i]}");
        Assert.InRange(Math.Abs(raw.Color.BaselineExposure - back.Color.BaselineExposure), 0, 1e-6);
        Assert.Equal("Canon", back.Metadata.Make);
        Assert.Equal("Canon EOS 600D", back.Metadata.Model);
        Assert.Equal("EF 50mm", back.Metadata.LensModel);
        Assert.Equal(iso, back.Metadata.Iso);
        Assert.Equal(50.0, back.Metadata.FocalLength);
        Assert.InRange(Math.Abs(back.Metadata.ExposureTime!.Value - 0.008), 0, 1e-6);
        Assert.InRange(Math.Abs(back.Metadata.FNumber!.Value - 5.6), 0, 1e-6);
        Assert.Equal("1234567", back.Metadata.SerialNumber);
        Assert.Equal("Canon", back.Metadata.LensMake);
        Assert.Equal(raw.Metadata.LensSpec, back.Metadata.LensSpec);
        Assert.Equal(80.0, back.Metadata.FocalLength35mm);
        Assert.InRange(Math.Abs(back.Metadata.ExposureBias!.Value + 2.0 / 3), 0, 1e-6);
        Assert.Equal(raw.Metadata.CaptureTime, back.Metadata.CaptureTime);
        Assert.Equal(new float[] { 1.64f, 1, 1.91f }, raw.WbMultipliers);
    }

    [Fact]
    public void ConvertedDng_ExistingProfilePreserved()
    {
        const int w = 16, h = 16;
        var data = new ushort[w * h];
        for (var i = 0; i < data.Length; i++) data[i] = (ushort)(1000 + i);
        var color = new ColorData
        {
            Illuminant = [17, 21],
            ColorMatrix = [new Mat3(1.5, -0.3, -0.1, -0.2, 1.4, -0.1, -0.05, -0.25, 1.6), null],
            ForwardMatrix = [new Mat3(0.5, 0.3, 0.1, 0.2, 0.6, 0.1, 0.0, 0.1, 0.8), null],
            AsShotNeutral = [0.5, 1.0, 0.6],
        };
        color.Profile = new ProfileLook { ToneCurve = RawToneCurve.FromTag([0, 0, 0.5, 0.7, 1.0, 1.0]) };
        var raw = new RawImage
        {
            Format = RawFormat.Dng,
            Width = w, Height = h, Cpp = 1,
            Data = new RawData.U16(data),
            Cfa = Cfa.Bayer("RGGB")!,
            Bits = 14,
            Black = BlackLevel.Uniform(64f),
            White = [16383f],
            ActiveArea = new PixelRect(0, 0, w, h),
            Crop = new PixelRect(0, 0, w, h),
            Orientation = Orientation.Normal,
            Color = color,
            Opcodes = new OpcodeLists(),
            Metadata = new Metadata { Make = "Test", Model = "D" },
        };
        RawDngConversion.WithStartingColour(raw, ReadOnlyMemory<byte>.Empty);
        var dng = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Uncompressed() });
        var back = RawDecoder.Decode(dng);
        Assert.Same(color, raw.Color);
        Assert.Equal(color.ColorMatrix[0], back.Color.ColorMatrix[0]);
        Assert.Equal(color.ForwardMatrix[0], back.Color.ForwardMatrix[0]);
        Assert.Equal(color.AsShotNeutral, back.Color.AsShotNeutral);
        Assert.NotNull(back.Color.Profile.ToneCurve);
        var pts = back.Color.Profile.ToneCurve!.Points;
        Assert.True(Math.Abs(pts[3] - 0.7f) < 1e-6, $"existing tone curve kept: {pts[3]}");
    }
}
