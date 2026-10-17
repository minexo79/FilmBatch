// 內嵌鏡頭校正的 Engine 端行為迴歸（手推夾具；CI 無外部 corpus 可跑）。
// 上游：engine camera_preview.rs（fit 前先讀 lens_geometry、proxy 擇優 warp、不雙重校正）、
// files.rs embedded_lens（List3 → 最終影像座標的 warp/vignette 轉換）。
// warp 係數為 X-T4 量測的形狀（角落約 0.967、zoom 約 1.03；見 raf_lens.rs 測試），vignette 增益角落約 1.1。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Core.Tests;

public sealed class RawLensTests
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

    private static double MeanAbsDiff(RgbImage a, RgbImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0;
        for (var i = 0; i < a.Data.Length; i++) sum += Math.Abs(a.Data[i] - b.Data[i]);
        return sum / a.Data.Length;
    }

    /// <summary>
    /// 有限像素上的平均絕對差（嵌入 JPEG 幾何 warp 在界外取樣處為 NaN，上游 pairs 略過；
    /// 見 <c>camera_preview::into_lens_geometry</c>）。至少一個有限對才有意義。
    /// </summary>
    private static double MeanAbsDiffFinite(RgbImage a, RgbImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0;
        var n = 0;
        for (var i = 0; i < a.Data.Length; i++)
        {
            var d = Math.Abs(a.Data[i] - b.Data[i]);
            if (!double.IsFinite(d)) continue;
            sum += d;
            n++;
        }
        Assert.True(n > 0, "expected at least one finite pixel pair");
        return sum / n;
    }

    private static RawImage SensorRaw()
    {
        var w = 96; var h = 64;
        var data = new ushort[w * h];
        for (var i = 0; i < data.Length; i++) data[i] = (ushort)(512 + (i * 37) % 2048);
        return new RawImage
        {
            Format = RawFormat.Raf,
            Width = w, Height = h, Cpp = 1,
            Data = new RawData.U16(data),
            Cfa = Cfa.Bayer("RGGB")!,
            Bits = 14,
            Black = BlackLevel.Uniform(256f),
            White = [4095f],
            ActiveArea = new PixelRect(0, 0, w, h),
            Crop = new PixelRect(0, 0, w, h),
            Orientation = Orientation.Normal,
            Opcodes = new OpcodeLists(),
            Metadata = new FilmBatch.Raw.Meta.Metadata { Make = "FUJIFILM", Model = "X-T4" },
        };
    }

    /// <summary>X-T4 形狀的 warp（桶形，角落約 0.967）＋vignette（角落增益約 1.1）。</summary>
    private static List<Opcode> LensOps() =>
    [
        new WarpRectilinear([[1.03, -0.1, 0.02, 0.018, 0.0, 0.0]], [0.5, 0.5]),
        new FixVignetteRadial([0.09, 0.02, 0.005, 0.001, 0.0], [0.5, 0.5]),
    ];

    [Fact]
    public void LensGeometryFollowsEmbeddedJpegWarp()
    {
        // 檔案內 warp（List3）必須能轉成 camera-fit 用的感光代理幾何。
        var geometry = CameraPreviewLens.LensGeometryOf(LensOps());
        Assert.NotNull(geometry);
        // 幾何 warp 必須實際改變代理（非恆等），否則 fit 擇優無意義。
        var proxy = Gradient(48, 32);
        var raw = SensorRaw();
        var warped = CameraPreviewLens.IntoGeometry(proxy, raw, 2, geometry!);
        Assert.NotNull(warped);
        Assert.True(MeanAbsDiffFinite(proxy, warped!) > 1e-4, "lens geometry should move proxy pixels");
    }

    [Fact]
    public void NoDoubleCorrectionWhenFitting()
    {
        // 同一 warp 在 fit 時只用於代理對齊，最終影像由 EmbeddedLensCorrection 另行套用：
        // helper 兩次套用必須與一次不同（冪等即代表幾何遺失），且 List3 轉最終幾何必須存在。
        var ops = LensOps();
        var full = Gradient(96, 64);
        var once = EmbeddedLensCorrection.Apply(ops, full, new PixelRect(0, 0, 96, 64), Orientation.Normal);
        var twice = EmbeddedLensCorrection.Apply(ops, once, new PixelRect(0, 0, 96, 64), Orientation.Normal);
        Assert.True(MeanAbsDiff(once, twice) > 1e-6, "double application must differ: fit path must not pre-apply");
        // DNG 1.6 鏈（WarpRectilinear2）不走舊幾何路徑。
        var chain = new List<Opcode> { new WarpRectilinear2([new double[OpcodeOps.Wr2Len]], [0.5, 0.5], false, false) };
        Assert.Null(CameraPreviewLens.LensGeometryOf(chain));
    }

    [Fact]
    public void FitSelectsBetterAlignedProxy()
    {
        // warp 候選與未 warp 候選中，擇優者應為對齊度高者（上游 alignment 語意）。
        var a = Gradient(32, 32);
        var shifted = new RgbImage(32, 32);
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var i = (y * 32 + x) * 3;
                var sx = Math.Min(x + 3, 31);
                var j = (y * 32 + sx) * 3;
                shifted.Data[i] = a.Data[j]; shifted.Data[i + 1] = a.Data[j + 1]; shifted.Data[i + 2] = a.Data[j + 2];
            }
        Assert.True(CameraPreview.Alignment(a, a) > CameraPreview.Alignment(a, shifted),
            "identical proxies should align better than shifted ones");
    }

    [Fact]
    public void VignetteGainAppliesAtFinalResolution()
    {
        // vignette 是乘性增益：最終影像套用後角落應變亮，且增益以最終影像座標計算。
        var vig = Assert.IsType<FixVignetteRadial>(LensOps()[1]);
        var flat = new RgbImage(64, 64);
        Array.Fill(flat.Data, 0.5f);
        var corrected = EmbeddedLensCorrection.Apply([vig], flat, new PixelRect(0, 0, 64, 64), Orientation.Normal);
        var corner = (corrected.Data[0] + corrected.Data[1] + corrected.Data[2]) / 3;
        var cx = (corrected.Data[(32 * 64 + 32) * 3] + corrected.Data[(32 * 64 + 32) * 3 + 1] + corrected.Data[(32 * 64 + 32) * 3 + 2]) / 3;
        Assert.True(corner > cx * 1.01, $"vignette should brighten corners: corner={corner}, center={cx}");
    }

    [Fact]
    public void LensStatusMetadataSurvivesInfoPropagation()
    {
        // RawInfo 必須攜帶診斷用狀態（Applied／拒絕原因），供 parent／CLI 顯示。
        static SonyLensCorrections Corrections(SonyLensStatus status) => new()
        {
            Shading = new SonyLensComponent<short[]>
            {
                Setting = new SonyLensField<SonySetting> { RawIfd = SonyCopy.Absent, Sr2 = SonyCopy.Absent },
                Table = new SonyLensField<short[]> { RawIfd = SonyCopy.Absent, Sr2 = SonyCopy.Absent },
                Status = SonyLensStatus.NoTable,
            },
            ChromaticAberration = new SonyLensComponent<short[][]>
            {
                Setting = new SonyLensField<SonySetting> { RawIfd = SonyCopy.Absent, Sr2 = SonyCopy.Absent },
                Table = new SonyLensField<short[][]> { RawIfd = SonyCopy.Absent, Sr2 = SonyCopy.Absent },
                Status = SonyLensStatus.NoTable,
            },
            Distortion = new SonyLensComponent<short[]>
            {
                Setting = new SonyLensField<SonySetting> { RawIfd = SonyCopy.Valid, Sr2 = SonyCopy.Absent },
                Table = new SonyLensField<short[]> { RawIfd = SonyCopy.Valid, Sr2 = SonyCopy.Absent },
                Status = status,
            },
            Sr2Read = false,
            Geometry = new SonyLensGeometry
            {
                ImageCrop = new PixelRect(0, 0, 32, 24),
                Reference = new PixelRect(2, 2, 30, 20),
            },
        };
        var raw = SensorRaw();
        raw.LensMetadata = new LensMetadata { Sony = Corrections(SonyLensStatus.Applied) };
        Assert.Equal(SonyLensStatus.Applied, raw.Info().LensMetadata.Sony!.Distortion.Status);
        raw.LensMetadata = new LensMetadata { Sony = Corrections(SonyLensStatus.NotValidatedModel) };
        Assert.Equal(SonyLensStatus.NotValidatedModel, raw.Info().LensMetadata.Sony!.Distortion.Status);
    }
}
