// 內嵌鏡頭校正的行為迴歸（手推夾具；CI 無外部 corpus 可跑）。
// 移植對象：lightcraft crates/raw/src/{lens_metadata.rs,vendor/{arw_lens.rs,arw_lens_rm6.rs,nef_lens.rs,raf_lens.rs,rw2.rs}}
// 與 engine files.rs embedded_lens／camera_preview.rs into_lens_geometry 語意。
// Sony 表格取自上游 arw_lens.rs 測試公開常數（WIDE／全零／TELE 形狀）；Fuji X-T4 表取自 raf_lens.rs 測試公開常數；
// Nikon 區塊按 nef_lens.rs 測試版面（WIDE 係數、FX 半對角線 21.6、zoom）；Panasonic 區塊按 rw2.rs 文件語意。
// pinned corpus（ILCE-7RM4A 等）由 VendorCorpusTests／SonyCorpusTests 覆蓋：缺檔略過，非本檔範圍。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Vendors.Fujifilm;
using FilmBatch.Raw.Vendors.Nikon;
using FilmBatch.Raw.Vendors.Panasonic;

namespace FilmBatch.Raw.Tests;

public sealed class LensCorrectionsTests
{
    private static RgbImage Gradient(int w, int h)
    {
        var img = new RgbImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                img.Data[i] = (float)x / w;
                img.Data[i + 1] = (float)y / h;
                img.Data[i + 2] = 0.5f;
            }
        return img;
    }

    private static RgbImage Flat(int w, int h, float v)
    {
        var img = new RgbImage(w, h);
        Array.Fill(img.Data, v);
        return img;
    }

    private static double MeanAbsDiff(RgbImage a, RgbImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0;
        for (var i = 0; i < a.Data.Length; i++) sum += Math.Abs(a.Data[i] - b.Data[i]);
        return sum / a.Data.Length;
    }

    [Fact]
    public void SonyValidatedDistortionAltersPixelsAndRefusedModelsDoNot()
    {
        // ILCE-7RM4A + 可驗證表格：應產生 Applied 記錄與 warp；ILCE-7RM4 不可。
        var validated = SonyLensRecord.BuildRecord("ILCE-7RM4A", SonyLensTests.Wide, new PixelRect(0, 0, 32, 24), new PixelRect(2, 2, 30, 20), Compression: 1, LinearYcbcr: false, Source: SonySource.RawIfd);
        Assert.NotNull(validated);
        Assert.Equal(SonyLensStatus.Applied, validated!.Distortion.Status);
        Assert.Single(validated.Opcodes);

        var refused = SonyLensRecord.BuildRecord("ILCE-7RM4", SonyLensTests.Wide, new PixelRect(0, 0, 32, 24), new PixelRect(2, 2, 30, 20), Compression: 1, LinearYcbcr: false, Source: SonySource.RawIfd);
        Assert.NotNull(refused);
        Assert.Equal(SonyLensStatus.NotValidatedModel, refused!.Distortion.Status);
        Assert.Empty(refused.Opcodes);

        // warp 必須實際改變渲染像素（幾何位移），而非恆等。
        var warp = Assert.IsType<WarpRectilinear>(validated.Opcodes[0]);
        var ramp = Gradient(48, 32);
        var corrected = OpcodeOps.ApplyList3([warp], ramp);
        var moved = MeanAbsDiff(ramp, corrected);
        Assert.True(moved > 1e-4, $"warp should move pixels, moved={moved}");
    }

    [Fact]
    public void SonyAllZeroTableIsNeutralAndOffSettingKeepsTable()
    {
        var neutral = SonyLensRecord.BuildRecord("ILCE-7RM4A", new short[16], new PixelRect(0, 0, 32, 24), new PixelRect(2, 2, 30, 20), Compression: 1, LinearYcbcr: false, Source: SonySource.RawIfd);
        Assert.NotNull(neutral);
        Assert.Equal(SonyLensStatus.Neutral, neutral!.Distortion.Status);
        Assert.Empty(neutral.Opcodes);
        // Off（0）不斷言為缺席：表格仍保留。
        Assert.NotNull(neutral.Distortion.Table.Value);
    }

    [Fact]
    public void SonySr2OnlyTableNeverApplies()
    {
        var sr2Only = SonyLensRecord.BuildRecord("ILCE-7RM4A", SonyLensTests.Wide, new PixelRect(0, 0, 32, 24), new PixelRect(2, 2, 30, 20), Compression: 1, LinearYcbcr: false, Source: SonySource.Sr2SubIfd);
        Assert.NotNull(sr2Only);
        Assert.Equal(SonyLensStatus.NotValidatedMode, sr2Only!.Distortion.Status);
        Assert.Empty(sr2Only.Opcodes);
    }

    [Fact]
    public void SonyLinearYcbcrNeverApplies()
    {
        var linear = SonyLensRecord.BuildRecord("ILCE-7RM4A", SonyLensTests.Wide, new PixelRect(0, 0, 32, 24), new PixelRect(2, 2, 30, 20), Compression: 7, LinearYcbcr: true, Source: SonySource.RawIfd);
        Assert.NotNull(linear);
        Assert.Equal(SonyLensStatus.NotValidatedMode, linear!.Distortion.Status);
        Assert.Empty(linear.Opcodes);
    }

    [Fact]
    public void FujiWarpAndVignetteAlterPixels()
    {
        var ops = FujiLens.Corrections("X-T4", FujiLensTests.Xt4Ifd(), FujiLensTests.Xt4Area);
        Assert.Equal(2, ops.Count);
        var warp = Assert.IsType<WarpRectilinear>(ops[0]);
        var vig = Assert.IsType<FixVignetteRadial>(ops[1]);
        var ramp = Gradient(96, 64);
        var warped = OpcodeOps.ApplyList3([warp], ramp);
        Assert.True(MeanAbsDiff(ramp, warped) > 1e-4, "Fuji warp should move pixels");
        var flat = Flat(64, 64, 0.5f);
        var gained = OpcodeOps.ApplyList3([vig], flat);
        Assert.True(MeanAbsDiff(flat, gained) > 1e-3, "Fuji vignette gain should brighten flat field");
        // 未驗證機身不校正。
        Assert.Empty(FujiLens.Corrections("X-T6", FujiLensTests.Xt4Ifd(), FujiLensTests.Xt4Area));
    }

    [Fact]
    public void NikonValidatedWarpCentredOnImageArea()
    {
        var ops = NikonLens.Corrections("NIKON Z 6_2", NikonLensTests.RawWith(NikonLensTests.WideBlock()), NikonLensTests.Area, NikonLensTests.Crop);
        var warp = Assert.Single(ops);
        var wr = Assert.IsType<WarpRectilinear>(warp);
        // 中心應落在影像區（active 內偏移 8px），而非 active 中心。
        Assert.True(Math.Abs(wr.Center[0] * 6063.0 - (8.0 + 6047.0 / 2.0)) < 0.5, $"cx={wr.Center[0]}");
        var ramp = Gradient(96, 64);
        Assert.True(MeanAbsDiff(ramp, OpcodeOps.ApplyList3([wr], ramp)) > 1e-4, "Nikon warp should move pixels");
        // 未驗證機身拒絕。
        Assert.Empty(NikonLens.Corrections("NIKON D850", NikonLensTests.RawWith(NikonLensTests.WideBlock()), NikonLensTests.Area, NikonLensTests.Crop));
    }

    [Fact]
    public void PanasonicDistortionAltersPixelsWhenOn()
    {
        var ops = PanasonicLens.Distortion(PanasonicLensTests.OnBlock(activeW: 100, activeH: 100, halfDiag: 70), new PixelRect(0, 0, 100, 100));
        Assert.NotNull(ops);
        var ramp = Gradient(96, 96);
        Assert.True(MeanAbsDiff(ramp, OpcodeOps.ApplyList3([ops!], ramp)) > 1e-4, "Panasonic warp should move pixels");
        // correction off（word7 低 4 位 != 1）不產生。
        Assert.Null(PanasonicLens.Distortion(PanasonicLensTests.OffBlock(), new PixelRect(0, 0, 100, 100)));
    }

    [Fact]
    public void EmbeddedLensCorrectionAppliesAtFinalResolutionWithCropAndOrientation()
    {
        // helper 必須在最終顯影解析度（含預設裁切＋方向）套用鏡頭校正。
        var ops = FujiLens.Corrections("X-T4", FujiLensTests.Xt4Ifd(), FujiLensTests.Xt4Area);
        var full = Gradient(96, 64);
        var corrected = EmbeddedLensCorrection.Apply(ops, full, new PixelRect(8, 8, 80, 48), Orientation.Normal);
        Assert.Equal((80, 48), (corrected.Width, corrected.Height));
        Assert.True(MeanAbsDiff(Gradient(80, 48), corrected) > 1e-4, "embedded correction should alter cropped pixels");
        // Rotate90 方向下尺寸對調但仍套用校正。
        var rotated = EmbeddedLensCorrection.Apply(ops, full, new PixelRect(8, 8, 80, 48), Orientation.Rotate90);
        Assert.Equal((48, 80), (rotated.Width, rotated.Height));
        // 空清單回傳裁切後影像（不改像素值域外的幾何）。
        var plain = EmbeddedLensCorrection.Apply([], full, new PixelRect(8, 8, 80, 48), Orientation.Normal);
        Assert.Equal((80, 48), (plain.Width, plain.Height));
        Assert.Equal(0.0, MeanAbsDiff(full.Crop(8, 8, 80, 48), plain), precision: 6);
    }
}
