// 嵌入預覽解碼的行為測試（上游 files::decode_raw_preview 經 convert.rs／jpeg.rs／space.rs）。
// 期望皆為獨立手算字面值（sRGB EOTF、Adobe gamma 563/256、D65 白點保白的手算工作空間值），
// 不呼叫受測 helper；僅呼叫 RED 快照已存在的 CameraPreview.TryDecodePreview
//（private→internal 可見性變更，契約允許；其餘皆為 Magick／BCL 公開 API）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Engine.Imaging.Raw;
using ImageMagick;
using ImageMagick.Formats;
using Xunit;

namespace FilmBatch.Core.Tests;

public sealed class RawPreviewDecodeTests
{
    // sRGB EOTF(128/255)：((128/255 + 0.055)/1.055)^2.4 ≈ 0.2158605。
    // 白點同為 D65 的轉換保白，故均勻灰三通道皆此值（P2 修前為雙重 EOTF ≈ 0.0383）。
    private const float Srgb128Linear = 0.2158605f;

    // Adobe RGB gamma 563/256：(128/255)^(563/256) ≈ 0.219638；同理保白（P3 修前輸出全 0）。
    private const float Adobe128Linear = 0.219638f;

    // 均勻灰、品質 100、4:4:4 的 JPEG 只有 DC 係數，解回恰為 128：容差須遠小於 sRGB 與 Adobe 的差（0.0038），
    // 否則色彩詮釋順序錯了（例如 JPEG 自帶 Exif 仍套容器 Adobe 宣告）測試也會通過。
    private const float GreyTolerance = 1e-4f;

    private sealed class LeWriter
    {
        public readonly List<byte> B = new();
        public int Pos => B.Count;
        public void U16(ushort v) { B.Add((byte)v); B.Add((byte)(v >> 8)); }
        public void U32(uint v) { B.Add((byte)v); B.Add((byte)(v >> 8)); B.Add((byte)(v >> 16)); B.Add((byte)(v >> 24)); }
        public void Bytes(byte[] b) => B.AddRange(b);
        public void PatchU32(int at, uint v)
        {
            B[at] = (byte)v; B[at + 1] = (byte)(v >> 8); B[at + 2] = (byte)(v >> 16); B[at + 3] = (byte)(v >> 24);
        }
    }

    private static byte[] SolidJpeg(byte r, byte g, byte b, int w, int h)
    {
        using var image = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        var px = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) { px[i * 3] = r; px[i * 3 + 1] = g; px[i * 3 + 2] = b; }
        image.ReadPixels(px, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
        image.Format = MagickFormat.Jpeg;
        image.Quality = 100;
        image.Settings.SetDefines(new JpegWriteDefines { SamplingFactor = JpegSamplingFactor.Ratio444 });
        return image.ToByteArray();
    }

    /// <summary>EXIF IFD＋Interop IFD（上游 exif::summarize 讀的 0xA001／0xA005→tag 1 結構）。</summary>
    private static void AppendExifIfds(LeWriter w, ushort? colorSpace, bool withInterop)
    {
        var hasCs = colorSpace is { };
        w.U16((ushort)(hasCs && withInterop ? 2 : hasCs || withInterop ? 1 : 0));
        if (hasCs)
        {
            w.U16(0xa001); w.U16(3); w.U32(1); w.U16(colorSpace!.Value); w.U16(0);
        }
        var pInterop = -1;
        if (withInterop)
        {
            w.U16(0xa005); w.U16(4); w.U32(1); pInterop = w.Pos; w.U32(0);
        }
        w.U32(0);
        if (withInterop)
        {
            w.PatchU32(pInterop, (uint)w.Pos);
            w.U16(1);
            w.U16(1); w.U16(2); w.U32(4); w.Bytes("R03\0"u8.ToArray());
            w.U32(0);
        }
    }

    /// <summary>最小 LE TIFF 容器：IFD0 帶 513/514 指向 JPEG；需要時掛 EXIF IFD（容器宣告）。</summary>
    private static byte[] TiffContainer(byte[] jpeg, bool adobeDeclared)
    {
        var w = new LeWriter();
        w.Bytes("II"u8.ToArray());
        w.U16(42);
        w.U32(8);
        w.U16((ushort)(adobeDeclared ? 3 : 2));
        w.U16(513); w.U16(4); w.U32(1); var pJpegOff = w.Pos; w.U32(0);
        w.U16(514); w.U16(4); w.U32(1); var pJpegLen = w.Pos; w.U32(0);
        var pExifOff = -1;
        if (adobeDeclared) { w.U16(34665); w.U16(4); w.U32(1); pExifOff = w.Pos; w.U32(0); }
        w.U32(0);
        w.PatchU32(pJpegOff, (uint)w.Pos);
        w.Bytes(jpeg);
        w.PatchU32(pJpegLen, (uint)jpeg.Length);
        if (adobeDeclared)
        {
            w.PatchU32(pExifOff, (uint)w.Pos);
            AppendExifIfds(w, 0xffff, withInterop: true);
        }
        return w.B.ToArray();
    }

    /// <summary>JPEG 自帶 Exif（APP1 `Exif\0\0`＋LE TIFF；IFD0 只帶 0x8769→EXIF IFD）。</summary>
    private static byte[] JpegWithExif(byte[] jpeg, ushort? colorSpace, bool withInterop)
    {
        var w = new LeWriter();
        w.Bytes("II"u8.ToArray());
        w.U16(42);
        w.U32(8);
        w.U16(1);
        w.U16(0x8769); w.U16(4); w.U32(1); var pExif = w.Pos; w.U32(0);
        w.U32(0);
        w.PatchU32(pExif, (uint)w.Pos);
        AppendExifIfds(w, colorSpace, withInterop);
        var tiff = w.B.ToArray();
        var payload = new byte[6 + tiff.Length];
        payload[0] = (byte)'E'; payload[1] = (byte)'x'; payload[2] = (byte)'i'; payload[3] = (byte)'f';
        Buffer.BlockCopy(tiff, 0, payload, 6, tiff.Length);
        var output = new byte[2 + 2 + 2 + payload.Length + (jpeg.Length - 2)];
        output[0] = 0xff; output[1] = 0xd8;
        output[2] = 0xff; output[3] = 0xe1;
        var segLen = payload.Length + 2;
        output[4] = (byte)(segLen >> 8); output[5] = (byte)segLen;
        Buffer.BlockCopy(payload, 0, output, 6, payload.Length);
        Buffer.BlockCopy(jpeg, 2, output, 6 + payload.Length, jpeg.Length - 2);
        return output;
    }

    [Fact]
    public void UntaggedUniform128_DecodesWithSingleSrgbEotf()
    {
        // P2：單次傳輸解碼，每通道恰一次 sRGB EOTF ≈ 0.2158605（修前雙重 EOTF ≈ 0.0383）。
        var file = TiffContainer(SolidJpeg(128, 128, 128, 8, 8), adobeDeclared: false);
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out var sw, out var sh));
        Assert.Equal(8, sw);
        Assert.Equal(8, sh);
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);
        foreach (var v in image.Data)
            Assert.InRange(v, Srgb128Linear - GreyTolerance, Srgb128Linear + GreyTolerance);
    }

    [Fact]
    public void ContainerAdobeDeclaration_WithoutJpegMetadata_DecodesAdobe()
    {
        // P3：容器宣告 Adobe（0xFFFF＋Interop R03），JPEG 無自帶詮釋 → gamma 563/256 ≈ 0.219638
        //（修前 AdobeToLinearRec2020 寫入全零故輸出全 0）。
        var file = TiffContainer(SolidJpeg(128, 128, 128, 8, 8), adobeDeclared: true);
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        foreach (var v in image.Data)
            Assert.InRange(v, Adobe128Linear - GreyTolerance, Adobe128Linear + GreyTolerance);
    }

    [Fact]
    public void JpegOwnSrgbExif_WinsOverContainerAdobeDeclaration()
    {
        // JPEG 自帶 Exif（ColorSpace=1）：m.exif.is_some() 故容器宣告被忽略，走 sRGB。
        var jpeg = JpegWithExif(SolidJpeg(128, 128, 128, 8, 8), colorSpace: 1, withInterop: false);
        var file = TiffContainer(jpeg, adobeDeclared: true);
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        foreach (var v in image.Data)
            Assert.InRange(v, Srgb128Linear - GreyTolerance, Srgb128Linear + GreyTolerance);
    }

    [Fact]
    public void JpegOwnExifWithoutColorSpace_IgnoresContainerAdobe()
    {
        // JPEG 自帶 Exif 但無 0xA001：仍因 exif.is_some() 忽略容器宣告，走 sRGB。
        var jpeg = JpegWithExif(SolidJpeg(128, 128, 128, 8, 8), colorSpace: null, withInterop: false);
        var file = TiffContainer(jpeg, adobeDeclared: true);
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        foreach (var v in image.Data)
            Assert.InRange(v, Srgb128Linear - GreyTolerance, Srgb128Linear + GreyTolerance);
    }

    [Fact]
    public void JpegOwnAdobeHint_DecodesAdobe_DespiteNoContainerDeclaration()
    {
        // JPEG 自帶 Exif Adobe 提示（0xFFFF＋R03），容器無宣告 → Adobe ≈ 0.219638。
        var jpeg = JpegWithExif(SolidJpeg(128, 128, 128, 8, 8), colorSpace: 0xffff, withInterop: true);
        var file = TiffContainer(jpeg, adobeDeclared: false);
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        foreach (var v in image.Data)
            Assert.InRange(v, Adobe128Linear - GreyTolerance, Adobe128Linear + GreyTolerance);
    }

    [Fact]
    public void EmbeddedIcc_AdobeVsSrgb_DivergeToRespectiveWorkingValues()
    {
        // P4：同像素掛 AdobeRGB1998 vs sRGB ICC → TRC 與色料矩陣不同，Rec.2020 工作值分歧
        //（修前 ColorSpace=RGB 丟棄 ICC，兩者一致）。
        // 純綠 (0,128,0)：sRGB→工作 ≈ (0.0710792, 0.1984925, 0.018999)；
        // Adobe→工作 ≈ (0.0170206, 0.1958132, 0.0094525)（D65 保白矩陣手算）。
        var green = SolidJpeg(0, 128, 0, 8, 8);
        byte[] Tagged(IImageProfile profile)
        {
            using var m = new MagickImage(green);
            m.SetProfile(profile);
            m.Format = MagickFormat.Jpeg;
            m.Quality = 100;
            m.Settings.SetDefines(new JpegWriteDefines { SamplingFactor = JpegSamplingFactor.Ratio444 });
            return m.ToByteArray();
        }
        var srgbJpeg = Tagged(ColorProfiles.SRGB);
        var adobeJpeg = Tagged(ColorProfiles.AdobeRGB1998);
        Assert.True(CameraPreview.TryDecodePreview(TiffContainer(srgbJpeg, false), 96, out var srgbImg, out _, out _));
        Assert.True(CameraPreview.TryDecodePreview(TiffContainer(adobeJpeg, false), 96, out var adobeImg, out _, out _));
        var s = srgbImg.Data;
        Assert.InRange(s[0], 0.0710792f - 0.02f, 0.0710792f + 0.02f);
        Assert.InRange(s[1], 0.1984925f - 0.02f, 0.1984925f + 0.02f);
        Assert.InRange(s[2], 0.018999f - 0.02f, 0.018999f + 0.02f);
        var a = adobeImg.Data;
        Assert.InRange(a[0], 0.0170206f - 0.02f, 0.0170206f + 0.02f);
        Assert.InRange(a[1], 0.1958132f - 0.02f, 0.1958132f + 0.02f);
        Assert.InRange(a[2], 0.0094525f - 0.02f, 0.0094525f + 0.02f);
        Assert.True(Math.Abs(s[0] - a[0]) > 0.03f);
    }

    [Fact]
    public void LargeJpeg_FitsEdgeBox_SourceSizeKept()
    {
        // 尺寸：任一邊 ≥ 2×edge 即走 DCT 縮放＋線性光 Fit；sourceW/H 仍為 JPEG 檔頭全尺寸。
        var file = TiffContainer(SolidJpeg(128, 128, 128, 64, 32), adobeDeclared: false);
        Assert.True(CameraPreview.TryDecodePreview(file, 16, out var image, out var sw, out var sh));
        Assert.Equal(64, sw);
        Assert.Equal(32, sh);
        Assert.Equal(16, image.Width);
        Assert.Equal(8, image.Height);
        foreach (var v in image.Data)
            Assert.InRange(v, Srgb128Linear - GreyTolerance, Srgb128Linear + GreyTolerance);
    }
}
