// 相機局部色調行為測試：預設關閉、啟用後改變線性色彩；期望為手算。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class RawLocalToneTests
{
    private static GainTableMap FlatMap(float gain, int n = 4) => new()
    {
        PointsV = 1, PointsH = 1, PointsN = n,
        SpacingV = 1.0, SpacingH = 1.0, OriginV = 0.0, OriginH = 0.0,
        Weights = [1f / 3f, 1f / 3f, 1f / 3f, 0f, 0f], Gamma = 1f,
        Gains = Enumerable.Repeat(gain, n).ToArray(),
    };

    private static RawImage LinearRaw(int w, int h, float[] samples, GainTableMap? map = null)
    {
        var full = new PixelRect(0, 0, w, h);
        var raw = new RawImage
        {
            Format = RawFormat.Dng, Width = w, Height = h, Cpp = 3,
            Data = new RawData.F32((float[])samples.Clone()),
            Bits = 16, White = [1f, 1f, 1f], Black = new BlackLevel(),
            ActiveArea = full, Crop = full, Orientation = Orientation.Normal,
            Color = new ColorData
            {
                Illuminant = [21, 0],
                ColorMatrix = [Mat3.Identity, null],
                ForwardMatrix = [Mat3.Identity, null],
                AsShotNeutral = [1.0, 1.0, 1.0],
                Profile = new ProfileLook(),
            },
            Metadata = new FilmBatch.Raw.Meta.Metadata(),
        };
        if (map is not null) raw.Color.Profile.GainTableMap = map;
        return raw;
    }

    [Fact]
    public void Apply_DefaultOffLeavesImageUnchanged()
    {
        const int W = 4, H = 4;
        var samples = new float[W * H * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.05f + 0.01f * (i % 13);
        var raw = LinearRaw(W, H, samples, FlatMap(2f));
        var image = CameraLocalTone.DevelopForTest(raw);
        var before = (float[])image.Data.Clone();
        CameraLocalTone.Apply(image, raw, enabled: false);
        Assert.Equal(before, image.Data);
    }

    [Fact]
    public void Apply_OptInMultipliesUniformGain()
    {
        const int W = 4, H = 4;
        var samples = new float[W * H * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.1f + 0.005f * (i % 7);
        var raw = LinearRaw(W, H, samples, FlatMap(2f));
        var image = CameraLocalTone.DevelopForTest(raw);
        var before = (float[])image.Data.Clone();
        Assert.True(CameraLocalTone.Apply(image, raw, enabled: true));
        for (var i = 0; i < image.Data.Length; i++)
            Assert.True(MathF.Abs(image.Data[i] - before[i] * 2f) < 1e-4f * MathF.Max(1f, MathF.Abs(before[i] * 2f)), $"px[{i}]");
    }

    [Fact]
    public void Apply_WithoutMapOrConsistentReturnsFalse()
    {
        const int W = 2, H = 2;
        var samples = new float[W * H * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.2f;
        var raw = LinearRaw(W, H, samples);
        var image = CameraLocalTone.DevelopForTest(raw);
        Assert.False(CameraLocalTone.Apply(image, raw, enabled: true));
        // 長度不符的手工 map：評估失敗，不改變影像
        raw.Color.Profile.GainTableMap = new GainTableMap
        {
            PointsV = 1, PointsH = 1, PointsN = 4,
            SpacingV = 1.0, SpacingH = 1.0, OriginV = 0.0, OriginH = 0.0,
            Weights = [1f, 0f, 0f, 0f, 0f], Gamma = 1f, Gains = [1f, 2f],
        };
        var before = (float[])image.Data.Clone();
        Assert.False(CameraLocalTone.Apply(image, raw, enabled: true));
        Assert.Equal(before, image.Data);
    }

    [Fact]
    public void Apply_RespectsCropAndOrientationPlacement()
    {
        // 有效區 8×8、預設裁切右半 4×8；定向前後同一儲存畫素應取不同增益。
        const int W = 8, H = 8;
        var samples = new float[W * H * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.2f;
        var map = new GainTableMap
        {
            PointsV = 1, PointsH = 2, PointsN = 1,
            SpacingV = 1.0, SpacingH = 0.5, OriginV = 0.0, OriginH = 0.25,
            Weights = [1f / 3f, 1f / 3f, 1f / 3f, 0f, 0f], Gamma = 1f, Gains = [1f, 3f],
        };
        var full = new PixelRect(0, 0, W, H);
        RawImage CropRaw(Orientation o) => new()
        {
            Format = RawFormat.Dng, Width = W, Height = H, Cpp = 3,
            Data = new RawData.F32((float[])samples.Clone()),
            Bits = 16, White = [1f, 1f, 1f], Black = new BlackLevel(),
            ActiveArea = full, Crop = new PixelRect(4, 0, 4, 8), Orientation = o,
            Color = new ColorData
            {
                Illuminant = [21, 0], ColorMatrix = [Mat3.Identity, null],
                ForwardMatrix = [Mat3.Identity, null], AsShotNeutral = [1.0, 1.0, 1.0],
                Profile = new ProfileLook { GainTableMap = map },
            },
            Metadata = new FilmBatch.Raw.Meta.Metadata(),
        };
        var normalRaw = CropRaw(Orientation.Normal);
        var normal = CameraLocalTone.DevelopForTest(normalRaw);
        var before = (float[])normal.Data.Clone();
        Assert.True(CameraLocalTone.Apply(normal, normalRaw, enabled: true));
        // 單點表：增益與色彩無關，只與水平位置有關。
        // x = 0 行：ax = 0.5625 → fh = 0.625 → 2.25；x = 3 行：ax = 0.9375 → 夾到邊緣表 → 3。
        Assert.Equal(4, normal.Width);
        for (var y = 0; y < normal.Height; y++)
            foreach (var (x, want) in new[] { (0, 2.25f), (3, 3f) })
            {
                var i = (y * normal.Width + x) * 3;
                Assert.True(before[i] > 0f);
                var ratio = normal.Data[i] / before[i];
                Assert.True(MathF.Abs(ratio - want) < 1e-5f, $"x={x} ratio={ratio}");
            }
        // 同一份儲存資料轉 90° 後，同列畫素取到不同行位置，增益不同。
        var rotatedRaw = CropRaw(Orientation.Rotate90);
        var rotated = CameraLocalTone.DevelopForTest(rotatedRaw);
        Assert.True(CameraLocalTone.Apply(rotated, rotatedRaw, enabled: true));
        var differs = false;
        for (var i = 0; i < normal.Data.Length; i++)
            if (MathF.Abs(normal.Data[i] - rotated.Data[i]) > 1e-4f) { differs = true; break; }
        Assert.True(differs, "定向應改變取表位置。");
    }

    [Fact]
    public void DevelopLinear_PreservesLegacyAppearanceByDefault()
    {
        var samples = new float[4 * 4 * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.15f + 0.002f * i;
        var plain = LinearRaw(4, 4, samples);
        var mapped = LinearRaw(4, 4, samples, FlatMap(1.7f));
        var a = CameraLocalTone.DevelopForTest(plain);
        var b = CameraLocalTone.DevelopForTest(mapped);
        CameraLocalTone.Apply(b, mapped, enabled: false);
        Assert.Equal(a.Data, b.Data);
    }
}
