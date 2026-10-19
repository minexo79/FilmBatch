// 縮圖快路徑行為測試：RAW 縮圖改用稀疏讀取＋內嵌相機 JPEG（不顯影，作一般 sRGB 影像打底），
// 並確認預覽尺寸與語意遮罩仍走完整顯影。預覽 JPEG 以內建 baseline 編碼器產生，無外部相依。
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests;

public sealed class RawEmbeddedThumbnailTests
{
    private static string WriteTemp(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "filmbatch-thumb-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] SolidJpeg(int w, int h, byte r, byte g, byte b)
    {
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) { rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; }
        return DngWriterJpeg.Encode(rgb, w, h, 95);
    }

    /// <summary>合成 DNG：IFD0＋CFA 子 IFD（全黑 strip）＋可選 JPEG 預覽子 IFD。</summary>
    private static byte[] PreviewDng(int rawSize, byte[] preview, int previewWidth, int previewHeight,
        Orientation orientation = Orientation.Normal)
    {
        var raw = new IfdBuilder()
            .Set(Tags.NewSubfileType, new TiffValue.Long([0]))
            .Set(Tags.ImageWidth, new TiffValue.Long([(uint)rawSize]))
            .Set(Tags.ImageLength, new TiffValue.Long([(uint)rawSize]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([8]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.None]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([1]))
            .Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]))
            .Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]))
            .SetImage(new ImageData.Strips((uint)rawSize, [new byte[rawSize * rawSize]]));
        var previewIfd = new IfdBuilder()
            .Set(Tags.ImageWidth, new TiffValue.Long([(uint)previewWidth]))
            .Set(Tags.ImageLength, new TiffValue.Long([(uint)previewHeight]))
            .Set(Tags.BitsPerSample, new TiffValue.Short([8, 8, 8]))
            .Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]))
            .Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Rgb]))
            .Set(Tags.SamplesPerPixel, new TiffValue.Short([3]))
            .SetImage(new ImageData.Strips((uint)previewHeight, [preview]));
        var ifd0 = new IfdBuilder()
            .Set(Tags.DngVersion, new TiffValue.Byte([1, 4, 0, 0]))
            .Set(Tags.Orientation, new TiffValue.Short([(ushort)orientation.ToExif()]))
            .Set(Tags.Make, new TiffValue.Ascii("TESTFUNC"))
            .AddSubIfd(raw)
            .AddSubIfd(previewIfd);
        return new TiffWriter().Write([ifd0]);
    }

    private static RawImage CfaRaw(int w, int h)
    {
        var full = new PixelRect(0, 0, w, h);
        var samples = new ushort[w * h];
        for (var i = 0; i < samples.Length; i++) samples[i] = (ushort)(i * 4000 / Math.Max(1, samples.Length - 1));
        return new RawImage
        {
            Format = RawFormat.Dng,
            Width = w,
            Height = h,
            Cpp = 1,
            Data = new RawData.U16(samples),
            Cfa = Cfa.Bayer("RGGB"),
            Bits = 12,
            Black = new BlackLevel { RepeatRows = 2, RepeatCols = 2, Values = [64f, 65f, 66f, 67f] },
            White = [4095f],
            ActiveArea = full,
            Crop = full,
            Orientation = Orientation.Normal,
            Metadata = new Metadata { Make = "Canon", Model = "Canon EOS R5", Software = "FilmBatch" },
        };
    }

    private static void AssertMeanNear(ushort[] rgba, int r, int g, int b, int tolerance = 2000)
    {
        long sr = 0, sg = 0, sb = 0;
        var n = rgba.Length / 4;
        for (var i = 0; i < rgba.Length; i += 4) { sr += rgba[i]; sg += rgba[i + 1]; sb += rgba[i + 2]; }
        Assert.InRange(sr / n, r * 257L - tolerance, r * 257L + tolerance);
        Assert.InRange(sg / n, g * 257L - tolerance, g * 257L + tolerance);
        Assert.InRange(sb / n, b * 257L - tolerance, b * 257L + tolerance);
    }

    [Fact]
    public void ThumbnailExtractsEmbeddedPreviewWithoutDeveloping()
    {
        var jpeg = SolidJpeg(240, 180, 220, 40, 90);
        var path = WriteTemp(PreviewDng(64, jpeg, 240, 180), ".dng");
        try
        {
            Assert.True(RawSourceLoader.TryExtractThumbnail(path, 200, out var preview, out var info));
            Assert.Equal(jpeg, preview);
            Assert.NotNull(info);
            Assert.Equal(RawFormat.Dng, info!.Format);

            var source = ImageLoader.Load(path, 200);
            Assert.False(source.IsSceneLinear);
            Assert.NotNull(source.RawInfo);
            Assert.Equal((200, 150), (source.Width, source.Height));
            Assert.Equal((64, 64), (source.ReferenceWidth, source.ReferenceHeight));
            AssertMeanNear(source.Rgba16, 220, 40, 90);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ThumbnailAppliesRawOrientationOverJpeg()
    {
        var jpeg = SolidJpeg(120, 60, 220, 40, 90);
        var path = WriteTemp(PreviewDng(64, jpeg, 120, 60, Orientation.Rotate90), ".dng");
        try
        {
            var source = ImageLoader.Load(path, 200);
            Assert.False(source.IsSceneLinear);
            Assert.Equal((60, 120), (source.Width, source.Height));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PreviewSizeAndSemanticMasksBypassEmbeddedThumbnail()
    {
        var jpeg = SolidJpeg(240, 180, 220, 40, 90);
        var path = WriteTemp(PreviewDng(64, jpeg, 240, 180), ".dng");
        try
        {
            foreach (var (edge, options) in new (int Edge, RawLoadOptions Options)[]
            {
                (ImageLoader.PreviewLongEdge, default),
                (ImageLoader.ThumbnailLongEdge, new RawLoadOptions(IncludeSemanticMasks: true)),
            })
            {
                SourceImage? source = null;
                var ex = Record.Exception(() => source = ImageLoader.Load(path, edge, options));
                if (ex is not null) { Assert.IsType<RawException>(ex); continue; }
                Assert.True(source!.IsSceneLinear);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RawWithoutPreviewDevelopsFully()
    {
        var path = WriteTemp(
            DngWriter.WriteDng(CfaRaw(8, 6), new DngWriteOptions { Compression = new DngCompression.Uncompressed() }),
            ".dng");
        try
        {
            Assert.False(RawSourceLoader.TryExtractThumbnail(path, 200, out _, out _));
            var source = ImageLoader.Load(path, 200);
            Assert.True(source.IsSceneLinear);
            Assert.NotNull(source.RawInfo);
            Assert.Equal((8, 6), (source.Width, source.Height));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NonRawImageHasNoEmbeddedThumbnail()
    {
        var path = WriteTemp(SolidJpeg(32, 24, 10, 20, 30), ".jpg");
        try
        {
            Assert.False(RawSourceLoader.TryExtractThumbnail(path, 200, out _, out _));
            var source = ImageLoader.Load(path, 200);
            Assert.False(source.IsSceneLinear);
            Assert.Null(source.RawInfo);
        }
        finally { File.Delete(path); }
    }
}
