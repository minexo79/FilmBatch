// REQ-RAW-07 語意遮罩局部調整的行為測試（EXP-07）：零／滿／軟邊遮罩、反轉、多實例、
// 缺失遮罩、全部來源幾何（裁切、轉正、warp、MaskSubArea）。期望值為手算或獨立公式，
// 不以實作常數回填。
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class RawSemanticRenderingTests
{
    // ---- 測試裝置：與產品共用型別，無私有管線程式碼 --------------------------------

    /// <summary>最小線性 RGB RAW（僅中繼資料；RawSourceGeometry 不讀感光樣本）。</summary>
    private static RawImage LinearRaw(int w, int h, Orientation orientation = Orientation.Normal,
        PixelRect? crop = null, IReadOnlyList<Opcode>? list3 = null, PixelRect? active = null)
    {
        var full = new PixelRect(0, 0, w, h);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = w, Height = h, Cpp = 3,
            Data = new RawData.F32(new float[w * h * 3]),
            Bits = 16, White = [1f, 1f, 1f], Black = new BlackLevel(),
            ActiveArea = active ?? full, Crop = crop ?? full, Orientation = orientation,
            Color = new FilmBatch.Raw.ColorData { AsShotNeutral = [1.0, 1.0, 1.0] },
            Metadata = new Metadata(),
        };
        if (list3 is not null) raw.Opcodes = new OpcodeLists { List3 = [.. list3] };
        return raw;
    }

    private static SemanticMask Mask(string name, int w, int h, ushort[] data,
        MaskSubArea? sub = null, string? instance = null) =>
        new() { Name = name, Width = w, Height = h, SubArea = sub, Data = data, InstanceId = instance };

    /// <summary>常數遮罩值。<paramref name="v"/> 為 0..1。</summary>
    private static ushort[] Const(int w, int h, double v)
    {
        var d = new ushort[w * h];
        Array.Fill(d, (ushort)Math.Round(v * 65535));
        return d;
    }

    /// <summary>左半 1、右半 0 的遮罩。</summary>
    private static ushort[] LeftHalf(int w, int h)
    {
        var d = new ushort[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w / 2; x++) d[y * w + x] = 65535;
        return d;
    }

    /// <summary>由 RAW 中繼資料建構來源：幾何走真實 <see cref="RawSourceGeometry"/>，遮罩為檔案語意遮罩。</summary>
    private static SourceImage Source(RawImage raw, int srcW, int srcH, params SemanticMask[] masks)
    {
        var geometry = RawSourceGeometry.Create(raw);
        var scene = new RgbImage(srcW, srcH, new float[srcW * srcH * 3]);
        var sceneData = new RawSceneData(scene, null, null, geometry);
        var rgba = new ushort[srcW * srcH * 4];
        for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 65535;
        return new SourceImage(srcW, srcH, rgba)
        {
            Scene = sceneData,
            SemanticMasks = masks,
            RawInfo = raw.Info(),
        };
    }

    private static ModuleParams Params(SourceImage source, string name = "m", string instance = "",
        double strength = 1, double exposure = 0, double temp = 0, double tint = 0,
        double contrast = 0, double saturation = 0, bool invert = false,
        CropParams? crop = null)
    {
        var c = crop ?? new CropParams(false, 0.5, 0.5, 1, 1, 0, 0, false, false);
        return new ModuleParams
        {
            Crop = c,
            CropT = CropGeometry.Compute(source.Width, source.Height, c),
            RawProcessingOn = true,
            RawMaskOn = true,
            RawMaskName = name,
            RawMaskInstance = instance,
            RawMaskInvert = invert,
            RawMaskStrength = (float)strength,
            RawMaskExposure = (float)exposure,
            RawMaskTemperature = (float)temp,
            RawMaskTint = (float)tint,
            RawMaskContrast = (float)contrast,
            RawMaskSaturation = (float)saturation,
        };
    }

    private static float[] Buffer(int w, int h, float r, float g, float b, float a = 1f)
    {
        var buf = new float[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            buf[i * 4] = r; buf[i * 4 + 1] = g; buf[i * 4 + 2] = b; buf[i * 4 + 3] = a;
        }
        return buf;
    }

    // ---- 基本套用 ---------------------------------------------------------------

    [Fact]
    public void MaskZero_LeavesEveryPixelBitwise()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, new ushort[16]);
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.25f, 0.5f, 0.75f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 1.5, temp: 50, contrast: 40, saturation: -30), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void MaskOne_ExposureDoublesLinearChannels()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.2f, 0.4f, 0.1f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(0.4f, buf[i * 4], 5);
            Assert.Equal(0.8f, buf[i * 4 + 1], 5);
            Assert.Equal(0.2f, buf[i * 4 + 2], 5);
            Assert.Equal(1f, buf[i * 4 + 3]);
        }
    }

    [Fact]
    public void MaskHalf_InterpolatesTowardFullAdjustment()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 0.5));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.2f, 0.2f, 0.2f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        // 遮罩 .5 → 曝光參數折半 → ×2^0.5
        var expected = 0.2f * MathF.Sqrt(2f);
        for (var i = 0; i < 16; i++) Assert.Equal(expected, buf[i * 4], 4);
    }

    [Fact]
    public void Strength_ScalesAlphaBeyondMaskValue()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.2f, 0.2f, 0.2f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, strength: 0.25, exposure: 2.0), TestContext.Current.CancellationToken);
        var expected = 0.2f * MathF.Pow(2f, 0.5f);
        for (var i = 0; i < 16; i++) Assert.Equal(expected, buf[i * 4], 4);
    }

    [Fact]
    public void SoftEdge_BilinearWeightsMatchIndependentFormula()
    {
        // 4x4 遮罩：值 = x/3（左 0、右 1）；曝光 +1 EV → 每像素增益 = 2^(x/3)。
        var raw = LinearRaw(4, 4);
        var d = new ushort[16];
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++) d[y * 4 + x] = (ushort)Math.Round(x / 3.0 * 65535);
        var m = Mask("m", 4, 4, d);
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        for (var x = 0; x < 4; x++)
        {
            var w = m.ValueAt(x + 0.5, 2.5); // 遮罩取樣的獨立事實
            var expected = 0.25f * MathF.Pow(2f, w);
            for (var y = 0; y < 4; y++) Assert.Equal(expected, buf[(y * 4 + x) * 4], 4);
        }
    }

    [Fact]
    public void Invert_AdjustsOnlyOutsideMask()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, LeftHalf(4, 4));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 1.0, invert: true), TestContext.Current.CancellationToken);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                if (x < 2) Assert.Equal(0.25f, buf[i], 6);      // 遮罩內：反轉後為 0 → 不變
                else Assert.Equal(0.5f, buf[i], 4);              // 遮罩外：曝光 ×2
            }
    }

    [Fact]
    public void Invert_StrengthOne_IsComplementOfNormal()
    {
        var raw = LinearRaw(4, 4);
        var d = new ushort[16];
        for (var i = 0; i < 16; i++) d[i] = (ushort)(i * 4369); // 0,4369,...,65535
        var m = Mask("m", 4, 4, d);
        var src = Source(raw, 4, 4, m);
        var a = Buffer(4, 4, 0.3f, 0.3f, 0.3f);
        var b = Buffer(4, 4, 0.3f, 0.3f, 0.3f);
        RawSemanticAdjustments.Apply(a, 4, 4, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        RawSemanticAdjustments.Apply(b, 4, 4, src, Params(src, exposure: 1.0, invert: true), TestContext.Current.CancellationToken);
        // 逐像素：兩者的對數增益相加應等於完整調整的對數增益（w + (1-w) = 1）。
        for (var i = 0; i < 16; i++)
            Assert.Equal(0.3f * 2f, a[i * 4] * b[i * 4] / 0.3f, 3);
    }

    // ---- 色溫／色偏／對比／飽和度 --------------------------------------------------

    [Fact]
    public void Temperature_PreservesLuminance()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.5f, 0.3f, 0.1f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, temp: 100), TestContext.Current.CancellationToken);
        var yBefore = CpuPipeline.Luminance(0.5f, 0.3f, 0.1f);
        var yAfter = CpuPipeline.Luminance(buf[0], buf[1], buf[2]);
        Assert.Equal(yBefore, yAfter, 4);
        Assert.True(buf[0] > 0.5f, "positive temp warms (rises R)");
        Assert.True(buf[2] < 0.1f, "positive temp cools B down");
    }

    [Fact]
    public void Tint_NegativeAddsGreen_PreservesLuminance()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.3f, 0.5f, 0.3f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, tint: -100), TestContext.Current.CancellationToken);
        var yBefore = CpuPipeline.Luminance(0.3f, 0.5f, 0.3f);
        var yAfter = CpuPipeline.Luminance(buf[0], buf[1], buf[2]);
        Assert.Equal(yBefore, yAfter, 4);
        Assert.True(buf[1] > 0.5f, "negative tint shifts green-magenta via (1 - 0.22*tint)");
    }

    [Fact]
    public void TemperatureAndTint_MatchIndependentChannelGainCalculation()
    {
        var src = Source(LinearRaw(4, 4), 4, 4, Mask("m", 4, 4, Const(4, 4, 1)));
        var buf = Buffer(4, 4, 0.4f, 0.3f, -0.1f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, temp: 100, tint: 100), TestContext.Current.CancellationToken);
        // 手算：通道倍率 (1.3,.78,.7)；Rec.709 亮度由 .2728548 正規化回 .29238。
        const double beforeY = 0.2126 * 0.4 + 0.7152 * 0.3 - 0.0722 * 0.1;
        const double afterY = 0.2126 * 0.52 + 0.7152 * 0.234 - 0.0722 * 0.07;
        Assert.Equal((float)(0.52 * beforeY / afterY), buf[0], 5);
        Assert.Equal((float)(0.234 * beforeY / afterY), buf[1], 5);
        Assert.Equal((float)(-0.07 * beforeY / afterY), buf[2], 5);
        Assert.True(buf[2] < 0); // 場景域負值不可被溫色調整夾掉。
    }

    [Fact]
    public void Contrast_PositivePivotsAtGrey_ExpandsLogLuminance()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        // 亮於 0.18：對比 +100 → Y' = Y * 2^(0.14*log2(Y/0.18))
        var buf = Buffer(4, 4, 0.72f, 0.72f, 0.72f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, contrast: 100), TestContext.Current.CancellationToken);
        var y = 0.72f;
        var l1 = Math.Clamp(MathF.Log2(y / 0.18f), -6f, 4f);
        var expected = y * MathF.Pow(2f, 0.14f * l1);
        Assert.Equal(expected, buf[0], 4);
        Assert.True(buf[0] > y, "bright pixel gets brighter");
    }

    [Fact]
    public void Contrast_Negative_CompressesLogLuminance()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.72f, 0.72f, 0.72f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, contrast: -100), TestContext.Current.CancellationToken);
        Assert.True(buf[0] < 0.72f && buf[0] > 0.18f, "bright pixel pulled toward grey");
    }

    [Fact]
    public void Saturation_MixesAroundLuminance_HouseRule()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.6f, 0.3f, 0.1f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, saturation: -100), TestContext.Current.CancellationToken);
        // 完全去飽和 → 全部通道 = Rec.709 亮度
        var l = CpuPipeline.Luminance(0.6f, 0.3f, 0.1f);
        Assert.Equal(l, buf[0], 5);
        Assert.Equal(l, buf[1], 5);
        Assert.Equal(l, buf[2], 5);
    }

    [Fact]
    public void Alpha_IsNeverChanged()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.5f, 0.5f, 0.5f, a: 0.6f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 2, temp: 60, contrast: 80, saturation: 50), TestContext.Current.CancellationToken);
        for (var i = 0; i < 16; i++) Assert.Equal(0.6f, buf[i * 4 + 3]);
    }

    // ---- 選取與缺席 --------------------------------------------------------------

    [Fact]
    public void UnselectedMask_WholeImageUnchanged()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("sky", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.4f, 0.4f, 0.4f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, name: "person", exposure: 3), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void MaskDisabled_NoOp()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var p = Params(src, exposure: 3);
        p.RawMaskOn = false;
        var buf = Buffer(4, 4, 0.4f, 0.4f, 0.4f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, p, TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void InstanceId_SelectsOnlyMatchingInstance()
    {
        var raw = LinearRaw(4, 4);
        var p1 = Mask("person", 4, 4, Const(4, 4, 0), instance: "1");
        var p2 = Mask("person", 4, 4, Const(4, 4, 1), instance: "2");
        var src = Source(raw, 4, 4, p1, p2);
        var buf = Buffer(4, 4, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, name: "person", instance: "2", exposure: 1.0), TestContext.Current.CancellationToken);
        for (var i = 0; i < 16; i++) Assert.Equal(0.5f, buf[i * 4], 5);
    }

    [Fact]
    public void InstanceId_WrongInstance_NoChange()
    {
        var raw = LinearRaw(4, 4);
        var p2 = Mask("person", 4, 4, Const(4, 4, 1), instance: "2");
        var src = Source(raw, 4, 4, p2);
        var buf = Buffer(4, 4, 0.25f, 0.25f, 0.25f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, name: "person", instance: "3", exposure: 1.0), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Theory]
    [InlineData("Person", "2")]
    [InlineData("person", "TWO")]
    public void Identity_IsOrdinalAndExact(string name, string instance)
    {
        var src = Source(LinearRaw(4, 4), 4, 4,
            Mask("person", 4, 4, Const(4, 4, 1), instance: "2"));
        var buf = Buffer(4, 4, 0.25f, 0.4f, 2f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src,
            Params(src, name: name, instance: instance, exposure: 2, invert: true), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void SameNameNoInstance_Ambiguous_NoChange()
    {
        var raw = LinearRaw(4, 4);
        var p1 = Mask("person", 4, 4, Const(4, 4, 1), instance: "1");
        var p2 = Mask("person", 4, 4, Const(4, 4, 1), instance: "2");
        var src = Source(raw, 4, 4, p1, p2);
        var buf = Buffer(4, 4, 0.25f, 0.25f, 0.25f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, name: "person", instance: "", exposure: 1.0), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void SameNameNoInstance_DoesNotMatchNamedInstance()
    {
        var raw = LinearRaw(4, 4);
        var p2 = Mask("person", 4, 4, Const(4, 4, 1), instance: "2");
        var src = Source(raw, 4, 4, p2);
        var buf = Buffer(4, 4, 0.25f, 0.25f, 0.25f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, name: "person", instance: "", exposure: 1.0), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void SameNameSameInstance_MaxCombine()
    {
        var raw = LinearRaw(4, 4);
        var left = Mask("sky", 4, 4, LeftHalf(4, 4));
        var right = Mask("sky", 4, 4, LeftHalf(4, 4).Select(v => (ushort)(65535 - v)).ToArray());
        var src = Source(raw, 4, 4, left, right);
        var buf = Buffer(4, 4, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, name: "sky", exposure: 1.0), TestContext.Current.CancellationToken);
        for (var i = 0; i < 16; i++) Assert.Equal(0.5f, buf[i * 4], 5);
    }

    // ---- 來源幾何：使用者裁切、轉正、預設裁切、MaskSubArea、warp --------------------

    [Fact]
    public void UserCrop_ResamplesMaskAtSourcePosition()
    {
        var raw = LinearRaw(8, 8);
        var m = Mask("m", 8, 8, LeftHalf(8, 8)); // 來源左半遮罩
        var src = Source(raw, 8, 8, m);
        // 使用者裁切右半（來源 x∈[4,8)）→ 4x8 輸出全部在遮罩外 → 不變
        var right = new CropParams(true, 0.75, 0.5, 0.5, 1, 0, 0, false, false);
        var buf = Buffer(4, 8, 0.4f, 0.4f, 0.4f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 8, src, Params(src, exposure: 2, crop: right), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
        // 裁切左半 → 4x8 全部在遮罩內 → ×4
        var left = new CropParams(true, 0.25, 0.5, 0.5, 1, 0, 0, false, false);
        buf = Buffer(4, 8, 0.4f, 0.4f, 0.4f);
        RawSemanticAdjustments.Apply(buf, 4, 8, src, Params(src, exposure: 2, crop: left), TestContext.Current.CancellationToken);
        for (var i = 0; i < 4 * 8; i++) Assert.Equal(1.6f, buf[i * 4], 4);
    }

    [Fact]
    public void UserQuarterTurn_RotatesMaskWithImage()
    {
        var raw = LinearRaw(8, 4);
        var m = Mask("m", 8, 4, LeftHalf(8, 4)); // 來源左半（x<4）遮罩
        var src = Source(raw, 8, 4, m);
        // 使用者 90° 順轉（quarter=1）：8x4 → 4x8 輸出。順轉後來源左半出現在輸出上半。
        var turn = new CropParams(true, 0.5, 0.5, 1, 1, 0, 1, false, false);
        var buf = Buffer(4, 8, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 4, 8, src, Params(src, exposure: 1.0, crop: turn), TestContext.Current.CancellationToken);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                // CropT 映射：輸出 (x,y) → 來源 (y, 3.5-x)。y<4 → sx<4 → 遮罩內。
                if (y < 4) Assert.Equal(0.5f, buf[i], 5);
                else Assert.Equal(0.25f, buf[i], 6);
            }
    }

    [Fact]
    public void DefaultCrop_MovesMaskWithActiveContent()
    {
        // 感光 8x8、預設裁切只留右半（x∈[4,8)）：顯影影像 4x8；遮罩右半在影像中是左半？
        // 遮罩資料在有效區座標：遮罩 x∈[4,8) 對應影像 x∈[0,4)。
        var raw = LinearRaw(8, 8, crop: new PixelRect(4, 0, 4, 8));
        var d = new ushort[64];
        for (var y = 0; y < 8; y++)
            for (var x = 4; x < 8; x++) d[y * 8 + x] = 65535;
        var m = Mask("m", 8, 8, d);
        var src = Source(raw, 4, 8, m);
        var buf = Buffer(4, 8, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 4, 8, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 4; x++)
                Assert.Equal(0.5f, buf[(y * 4 + x) * 4], 5); // 整張影像都來自遮罩的 1 區
    }

    [Fact]
    public void OrientedSource_RotatesSensorMask()
    {
        // EXIF Rotate90：8x4 感光 → 4x8 影像；感光左半遮罩（x<4）順轉後落在影像上半。
        var raw = LinearRaw(8, 4, orientation: Orientation.Rotate90);
        var m = Mask("m", 8, 4, LeftHalf(8, 4));
        var src = Source(raw, 4, 8, m);
        var buf = Buffer(4, 8, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 4, 8, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        var adjusted = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                // 感光左半（x<4）在 Rotate90 下映到影像上半（y<4）或下半，
                // 依轉向慣例；斷言恰好一半、同一半全部被調整。
                if (buf[i] > 0.4f) { adjusted++; Assert.True(buf[i] > 0.45f); }
                else Assert.Equal(0.25f, buf[i], 6);
            }
        Assert.Equal(16, adjusted);
    }

    [Fact]
    public void FlipH_MirrorsMask()
    {
        var raw = LinearRaw(8, 4, orientation: Orientation.FlipH);
        var m = Mask("m", 8, 4, LeftHalf(8, 4));
        var src = Source(raw, 8, 4, m);
        var buf = Buffer(8, 4, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 8, 4, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        // 感光左半遮罩經水平翻轉後作用在影像右半
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                if (x >= 4) Assert.Equal(0.5f, buf[i], 5);
                else Assert.Equal(0.25f, buf[i], 6);
            }
    }

    [Fact]
    public void MaskSubArea_OffsetsAndScalesStoredData()
    {
        // 有效區 8x8；遮罩只存左上 4x4 區域（SubArea Top=0 Left=0 Full 8x8），資料全 1。
        var raw = LinearRaw(8, 8);
        var m = Mask("m", 4, 4, Const(4, 4, 1), sub: new MaskSubArea(0, 0, 8, 8));
        var src = Source(raw, 8, 8, m);
        var buf = Buffer(8, 8, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 8, 8, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                if (x < 4 && y < 4) Assert.Equal(0.5f, buf[i], 5);
                else Assert.Equal(0.25f, buf[i], 6);
            }
    }

    [Fact]
    public void MaskSubArea_NonZeroOffset_PlacesStoredBlock()
    {
        // SubArea Top=4 Left=4、全幅 8x8：存的 4x4 資料覆蓋右下象限。
        var raw = LinearRaw(8, 8);
        var m = Mask("m", 4, 4, Const(4, 4, 1), sub: new MaskSubArea(4, 4, 8, 8));
        var src = Source(raw, 8, 8, m);
        var buf = Buffer(8, 8, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 8, 8, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                if (x >= 4 && y >= 4) Assert.Equal(0.5f, buf[i], 5);
                else Assert.Equal(0.25f, buf[i], 6);
            }
    }

    [Fact]
    public void WarpedGeometry_MaskFollowsImageNotSourceAxes()
    {
        // WarpRectilinear 半徑倍率 0.5、中心 0.5,0.5 套用在顯影鏈；
        // 遮罩中央圓盤（4x4 中間 2x2=1）應跟隨影像 warp 後的位置，而非軸向矩形。
        var planes = new[]
        {
            new double[] { 0.5, 0, 0, 0, 0, 0 },
            new double[] { 0.5, 0, 0, 0, 0, 0 },
            new double[] { 0.5, 0, 0, 0, 0, 0 },
        };
        var raw = LinearRaw(8, 8, list3: new Opcode[] { new WarpRectilinear(planes, [0.5, 0.5]) });
        var d = new ushort[64];
        for (var y = 3; y <= 4; y++)
            for (var x = 3; x <= 4; x++) d[y * 8 + x] = 65535;
        var m = Mask("m", 8, 8, d);
        var src = Source(raw, 8, 8, m);
        var buf = Buffer(8, 8, 0.25f, 0.25f, 0.25f);
        RawSemanticAdjustments.Apply(buf, 8, 8, src, Params(src, exposure: 1.0), TestContext.Current.CancellationToken);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                // 手算：像素中心縮向 (4,4) 一半；中央 2x2 遮罩的雙線性梯形核可分離。
                // 不呼叫 geometry.Active 或 mask.ValueAt 計算期望。
                var sx = 4 + 0.5 * (x + 0.5 - 4);
                var sy = 4 + 0.5 * (y + 0.5 - 4);
                var w = (float)(Math.Clamp(1.5 - Math.Abs(sx - 4), 0, 1)
                    * Math.Clamp(1.5 - Math.Abs(sy - 4), 0, 1));
                var expected = 0.25f * MathF.Pow(2f, w);
                Assert.Equal(expected, buf[i], 4);
            }
        // 確認 warp 確實改變了遮罩落點（非平凡恆等）：有像素在軸向 2x2 外仍被調整。
        var moved = false;
        for (var y = 0; y < 8 && !moved; y++)
            for (var x = 0; x < 8; x++)
                if (x is >= 2 and <= 5 && y is >= 2 and <= 5 && buf[(y * 8 + x) * 4] > 0.4f)
                    if (x < 3 || x > 4 || y < 3 || y > 4) { moved = true; break; }
        Assert.True(moved, "warp should move masked pixels off the un-warped quad");
    }

    // ---- 來源不變性與健壯性 ---------------------------------------------------------

    [Fact]
    public void TransparentPixels_KeepHiddenRgbUnchanged()
    {
        var src = Source(LinearRaw(4, 4), 4, 4, Mask("m", 4, 4, Const(4, 4, 1)));
        var buf = Buffer(4, 4, -0.2f, 2f, 0.1f, a: 0);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src,
            Params(src, exposure: 2, temp: 40, tint: -50, contrast: 60, saturation: -20), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void MissingInvertedMask_DoesNotBecomeWholeImageEdit()
    {
        var src = Source(LinearRaw(4, 4), 4, 4, Mask("sky", 4, 4, Const(4, 4, 1)));
        var buf = Buffer(4, 4, 0.25f, 0.4f, 2f);
        var before = (float[])buf.Clone();
        var p = Params(src, name: "person", exposure: 2, invert: true);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, p, TestContext.Current.CancellationToken);
        Assert.Null(RawSemanticAdjustments.Prepare(src, p, TestContext.Current.CancellationToken));
        Assert.Equal(before, buf);
    }

    [Fact]
    public void RawProcessingDisabled_SkipsCpuAndGpuPreparation()
    {
        var src = Source(LinearRaw(4, 4), 4, 4, Mask("m", 4, 4, Const(4, 4, 1)));
        var p = Params(src, exposure: 2);
        p.RawProcessingOn = false;
        var buf = Buffer(4, 4, 0.25f, 0.4f, 2f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, p, TestContext.Current.CancellationToken);
        Assert.Null(RawSemanticAdjustments.Prepare(src, p, TestContext.Current.CancellationToken));
        Assert.Equal(before, buf);
    }

    [Fact]
    public void FullResolutionEditPreservesSinglePixelMaskDetail()
    {
        const int width = 8192, height = 2;
        var mask = new ushort[width * height];
        for (var i = 0; i < mask.Length; i++) mask[i] = i % 2 == 0 ? ushort.MaxValue : (ushort)0;
        var src = Source(LinearRaw(width, height), width, height, Mask("m", width, height, mask));
        var pixels = Buffer(width, height, .25f, .25f, .25f);
        RawSemanticAdjustments.Apply(pixels, width, height, src, Params(src, exposure: 1), TestContext.Current.CancellationToken);
        for (var i = 0; i < mask.Length; i++)
            for (var c = 0; c < 3; c++)
                Assert.InRange(Math.Abs(pixels[i * 4 + c] - (i % 2 == 0 ? .5f : .25f)), 0, 1e-6f);
    }

    [Fact]
    public void Prepare_ReusesAlphaAcrossSlidersAndCrop_ButNotIdentity()
    {
        var src = Source(LinearRaw(4, 4), 4, 4,
            Mask("m", 4, 4, Const(4, 4, 1)), Mask("other", 4, 4, Const(4, 4, 0)));
        var first = RawSemanticAdjustments.Prepare(src, Params(src, exposure: 1), TestContext.Current.CancellationToken);
        var edited = Params(src, exposure: -2, temp: 70, invert: true,
            crop: new CropParams(true, 0.25, 0.5, 0.5, 1, 0, 0, false, false));
        Assert.Same(first, RawSemanticAdjustments.Prepare(src, edited, TestContext.Current.CancellationToken));
        var other = RawSemanticAdjustments.Prepare(src, Params(src, name: "other"), TestContext.Current.CancellationToken);
        Assert.NotSame(first, other);
        Assert.All(other!.Data, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Prepare_ConcurrentSelectionsKeepTheirOwnAlpha()
    {
        var src = Source(LinearRaw(4, 4), 4, 4,
            Mask("m", 4, 4, Const(4, 4, 1)), Mask("other", 4, 4, Const(4, 4, 0)));
        Parallel.For(0, 32, i =>
        {
            var selected = i % 2 == 0 ? "m" : "other";
            var alpha = RawSemanticAdjustments.Prepare(src, Params(src, name: selected), TestContext.Current.CancellationToken);
            Assert.NotNull(alpha);
            Assert.Equal(selected, alpha.Name);
            Assert.All(alpha.Data, value => Assert.Equal(selected == "m" ? 1f : 0f, value));
        });
    }

    [Fact]
    public void Apply_DoesNotMutateSourceOrMask()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var rgbaBefore = (ushort[])src.Rgba16.Clone();
        var sceneBefore = (float[])src.Scene!.Image.Data.Clone();
        var sensorBefore = (float[])((RawData.F32)raw.Data).Samples.Clone();
        var maskBefore = (ushort[])m.Data.Clone();
        var buf = Buffer(4, 4, 0.4f, 0.4f, 0.4f);
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 3), TestContext.Current.CancellationToken);
        Assert.Equal(rgbaBefore, src.Rgba16);
        Assert.Equal(maskBefore, m.Data);
        Assert.Equal(sceneBefore, src.Scene.Image.Data);
        Assert.Equal(sensorBefore, ((RawData.F32)raw.Data).Samples);
    }

    [Fact]
    public void NonRawSource_NoOp()
    {
        var src = new SourceImage(4, 4, new ushort[64]) { SemanticMasks = [] };
        var p = Params(src, exposure: 3);
        p.RawMaskOn = true; // 即便旗標誤開：無 Scene → 無操作
        var buf = Buffer(4, 4, 0.4f, 0.4f, 0.4f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, p, TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void RepeatedApply_DeterministicSameResult()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 0.5));
        var src = Source(raw, 4, 4, m);
        var p = Params(src, exposure: 1.0, temp: 30, contrast: 20, saturation: -10);
        var a = Buffer(4, 4, 0.4f, 0.3f, 0.2f);
        var b = Buffer(4, 4, 0.4f, 0.3f, 0.2f);
        RawSemanticAdjustments.Apply(a, 4, 4, src, p, TestContext.Current.CancellationToken);
        RawSemanticAdjustments.Apply(b, 4, 4, src, p, TestContext.Current.CancellationToken);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ZeroAdjustments_OnlyStrength_LeavesBufferUnchanged()
    {
        var raw = LinearRaw(4, 4);
        var m = Mask("m", 4, 4, Const(4, 4, 1));
        var src = Source(raw, 4, 4, m);
        var buf = Buffer(4, 4, 0.4f, 0.4f, 0.4f);
        var before = (float[])buf.Clone();
        RawSemanticAdjustments.Apply(buf, 4, 4, src, Params(src, exposure: 0, temp: 0, contrast: 0, saturation: 0), TestContext.Current.CancellationToken);
        Assert.Equal(before, buf);
    }
}
