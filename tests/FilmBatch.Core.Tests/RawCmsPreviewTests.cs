// 嵌入預覽 CMS 路徑的行為測試（上游 convert.rs rgb_or_gray／cmyk 的 NeedsCms 分支＋icc.rs cms_to_linear_rec2020）。
// 期望皆為獨立量測字面值（PIL ImageCms／magick CLI／Magick.NET HDRI 三重 oracle，見 /tmp/cmsproof），
// 不呼叫受測 helper 計算期望。呼叫面為既有 RED 契約 CameraPreview.TryDecodePreview。
// Fixture ICC 為合成固定位元組（base64 內嵌）：neutral RGB LUT（scnr／RGB→Lab A2B0 mft1 9³，
// 中性軸保持中性；純紅→線性 Rec.2020 ≈ (0.2764, -0.0244, 0.3570)，sRGB 替代則為 (0.6275, 0.0691, 0.0164)）
// 與 gray LUT（GRAY→Lab A2B0，gamma 1.8；128 灰→線性 ≈ 0.0578）。產生器為 /tmp/cmsproof 下的 Python
// 腳本（mft1 手工組裝：identity 輸入／輸出表＋9³／17 點 CLUT，檔頭日期清零；重跑位元組一致）。
// 線性 Rec.2020 目標 ICC 由產品內嵌資源提供（PreviewCms.Rec2020Linear）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#.
using System.Buffers.Binary;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using ImageMagick;
using ImageMagick.Formats;
using Xunit;

namespace FilmBatch.Core.Tests;

public sealed class RawCmsPreviewTests
{
    // 中性軸 LUT（R+G+B 平均亮度、R-B／G-中性色度；灰階保持中性）經 LCMS → 線性 Rec.2020 的獨立 oracle 值
    //（PIL ImageCms RELATIVE_COLORIMETRIC 8-bit：(70,0,91) 等；magick HDRI 浮點：0.2764/-0.0244/0.3570 等）。
    // 純紅 (255,0,0) 在 CMS 下為 (0.2764, -0.0244, 0.3570)；sRGB 替代路徑則為 (0.6275, 0.0691, 0.0164)：
    // 兩者 R 通道差 0.35，容差 0.03 仍能明確區分 CMS 與 sRGB 替代（修前行為）。
    private static readonly float[] LutRedCms = [0.2764f, -0.0244f, 0.3570f];
    private static readonly float[] LutRedSrgbSubstitute = [0.6275f, 0.0691f, 0.0164f];

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

    private static byte[] JpegWithIcc(byte[] jpeg, byte[] icc)
    {
        // APP2 ICC_PROFILE 單段（seq=1/1；測試用小 ICC < 64KB 不分段）。
        var seg = new List<byte>();
        seg.AddRange("ICC_PROFILE\0"u8.ToArray());
        seg.Add(1); seg.Add(1);
        seg.AddRange(icc);
        var segArr = seg.ToArray();
        var segLen = segArr.Length + 2;
        // SOI(2)＋標記 FF E2(2)＋長度欄位(2)＋段本體＋原 JPEG 去 SOI 餘部。
        var output = new byte[2 + 2 + 2 + segArr.Length + (jpeg.Length - 2)];
        output[0] = 0xff; output[1] = 0xd8;
        output[2] = 0xff; output[3] = 0xe2;
        output[4] = (byte)(segLen >> 8); output[5] = (byte)segLen;
        Buffer.BlockCopy(segArr, 0, output, 6, segArr.Length);
        Buffer.BlockCopy(jpeg, 2, output, 6 + segArr.Length, jpeg.Length - 2);
        return output;
    }

    private static byte[] TiffContainer(byte[] jpeg)
    {
        var w = new LeWriter();
        w.Bytes("II"u8.ToArray());
        w.U16(42);
        w.U32(8);
        w.U16(2);
        w.U16(513); w.U16(4); w.U32(1); var pOff = w.Pos; w.U32(0);
        w.U16(514); w.U16(4); w.U32(1); var pLen = w.Pos; w.U32(0);
        w.U32(0);
        w.PatchU32(pOff, (uint)w.Pos);
        w.Bytes(jpeg);
        w.PatchU32(pLen, (uint)jpeg.Length);
        return w.B.ToArray();
    }

    [Fact]
    public void LutProfile_DecodesThroughCms_NotSrgbSubstitute()
    {
        // LUT 型 RGB 設定檔走真正 CMS：純紅輸出 ≈ (0.2764, -0.0244, 0.3570)，
        // 與 sRGB 替代 (0.6275, 0.0691, 0.0164) 差 0.35（修前為後者）。
        var icc = Convert.FromBase64String(CmsFixtures.NeutralRgbLut);
        var file = TiffContainer(JpegWithIcc(SolidJpeg(255, 0, 0, 8, 8), icc));
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        var d = image.Data;
        Assert.InRange(d[0], LutRedCms[0] - 0.03f, LutRedCms[0] + 0.03f);
        Assert.InRange(d[1], LutRedCms[1] - 0.03f, LutRedCms[1] + 0.03f);
        Assert.InRange(d[2], LutRedCms[2] - 0.03f, LutRedCms[2] + 0.03f);
        Assert.True(Math.Abs(d[0] - LutRedSrgbSubstitute[0]) > 0.2f);
    }

    [Theory]
    [InlineData("A2B0")]
    [InlineData("A2B1")]
    public void MatrixAndLutProfile_UsesIntentTransformForRawPreview(string tag)
    {
        // The LUT maps every RGB value to black; the sRGB matrix would render red.
        var icc = MatrixWithBlackLut(tag);
        var file = TiffContainer(JpegWithIcc(SolidJpeg(255, 0, 0, 8, 8), icc));
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        Assert.InRange(image.Data[0], -0.001f, 0.001f);
        Assert.InRange(image.Data[1], -0.001f, 0.001f);
        Assert.InRange(image.Data[2], -0.001f, 0.001f);
    }

    [Theory]
    [InlineData("A2B0")]
    [InlineData("A2B1")]
    public void AppleCms_MatrixAndLutProfile_UsesRelativeIntent(string tag)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Apple ColorSync.");
        var image = AppleImageCodec.ToLinearRec2020(MatrixWithBlackLut(tag), [1f, 0f, 0f], 3, 1, 1, false);
        Assert.NotNull(image);
        Assert.InRange(image.Data[0], -0.001f, 0.001f);
        Assert.InRange(image.Data[1], -0.001f, 0.001f);
        Assert.InRange(image.Data[2], -0.001f, 0.001f);
    }

    [Fact]
    public void AppleCms_PreservesPixelOrderAndConvertsSrgbPrimaries()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Apple ColorSync.");
        float[] samples =
        [
            1f, 0f, 0f,  0f, 1f, 0f,
            0f, 0f, 1f,  1f, 1f, 1f,
        ];
        // sRGB primaries and white independently converted to linear Rec.2020.
        float[] expected =
        [
            .6274f, .0691f, .0164f,  .3293f, .9195f, .0880f,
            .0433f, .0114f, .8956f,  1f, 1f, 1f,
        ];
        var result = AppleImageCodec.ToLinearRec2020(ColorProfiles.SRGB.ToByteArray(), samples, 3, 2, 2, false);
        Assert.NotNull(result);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(result.Data[i], expected[i] - .005f, expected[i] + .005f);
    }

    [Fact]
    public void AppleCms_GrayLutTransformsMidtone()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Apple ColorSync.");
        var profile = Convert.FromBase64String(CmsFixtures.GrayLut);
        var result = AppleImageCodec.ToLinearRec2020(profile, [128f / 255f], 1, 1, 1, true);
        Assert.NotNull(result);
        foreach (var value in result.Data)
            Assert.InRange(value, .055f, .061f);
    }
    [Fact]
    public void AppleCms_PreservesExtendedColorsWithoutClipping()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Apple ColorSync.");
        var icc = Convert.FromBase64String(CmsFixtures.NeutralRgbLut);
        var result = AppleImageCodec.ToLinearRec2020(icc, [1f, 0f, 1f], 3, 1, 1, false);
        Assert.NotNull(result);
        Assert.True(result.Data[2] > 1.5f);
    }

    [Fact]
    public void AppleCms_MalformedIcc_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Apple ColorSync.");
        var truncated = Convert.FromBase64String(CmsFixtures.NeutralRgbLut)[..100];
        var result = AppleImageCodec.ToLinearRec2020(truncated, [1f, 0f, 0f], 3, 1, 1, false);
        Assert.Null(result);
    }

    [Fact]
    public void AppleCms_RejectsGrayProfileForRgbSamples()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Apple ColorSync.");
        var profile = Convert.FromBase64String(CmsFixtures.GrayLut);
        Assert.Null(AppleImageCodec.ToLinearRec2020(profile, [1f, 0f, 0f], 3, 1, 1, false));
    }

    [Fact]
    public void RawPreview_DoesNotPassOtherChannelModelsToRgbCms()
    {
        var profile = Convert.FromBase64String(CmsFixtures.NeutralRgbLut);
        "6CLR"u8.CopyTo(profile.AsSpan(16, 4));
        var space = PreviewDecode.ResolveSpace(profile, null, null, 3);
        Assert.Null(space.Cms);
    }


    private static byte[] CreateLut(bool isBlack)
    {
        var lut = new byte[48 + 3 * 256 + 2 * 2 * 2 * 3 + 3 * 256];
        "mft1"u8.CopyTo(lut);
        lut[8] = 3; lut[9] = 3; lut[10] = 2;
        for (var i = 0; i < 3; i++)
            BinaryPrimitives.WriteInt32BigEndian(lut.AsSpan(12 + (i * 3 + i) * 4, 4), 65536);
        for (var channel = 0; channel < 3; channel++)
            for (var i = 0; i < 256; i++)
            {
                lut[48 + channel * 256 + i] = (byte)i;
                lut[48 + 3 * 256 + 24 + channel * 256 + i] = (byte)i;
            }
        var clutOffset = 48 + 3 * 256;
        for (var r = 0; r < 2; r++)
            for (var g = 0; g < 2; g++)
                for (var b = 0; b < 2; b++)
                {
                    var idx = clutOffset + (r * 4 + g * 2 + b) * 3;
                    if (isBlack)
                    {
                        lut[idx] = 0; lut[idx + 1] = 0; lut[idx + 2] = 0;
                    }
                    else
                    {
                        lut[idx] = (byte)(r != 0 ? 255 : 0);
                        lut[idx + 1] = (byte)(g != 0 ? 255 : 0);
                        lut[idx + 2] = (byte)(b != 0 ? 255 : 0);
                    }
                }
        return lut;
    }

    private static byte[] MatrixWithBlackLut(string tag)
    {
        var matrix = ColorProfiles.SRGB.ToByteArray();
        var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(matrix.AsSpan(128, 4)));
        var oldData = 132 + count * 12;

        var blackLut = CreateLut(isBlack: true);
        var normalLut = CreateLut(isBlack: false);

        var addedTags = new List<(string Tag, byte[] Lut)>();
        if (tag == "A2B1")
        {
            // ICC.1 表 17：輸入設定檔（scnr）必要 AToB0Tag；測試相對比色時額外掛 AToB1Tag。
            addedTags.Add(("A2B0", normalLut));
            addedTags.Add(("A2B1", blackLut));
        }
        else
        {
            addedTags.Add((tag, blackLut));
        }

        var addedDirSize = addedTags.Count * 12;
        var totalLutSize = addedTags.Sum(t => t.Lut.Length);
        var profile = new byte[matrix.Length + addedDirSize + totalLutSize];
        matrix.AsSpan(0, 132).CopyTo(profile);
        // 相機嵌入預覽為輸入色彩裝置（scnr），免除 mntr 要求的 BToA 反向標籤。
        "scnr"u8.CopyTo(profile.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), (uint)(count + addedTags.Count));

        for (var i = 0; i < count; i++)
        {
            var entry = matrix.AsSpan(132 + i * 12, 12);
            var target = profile.AsSpan(132 + i * 12, 12);
            entry.CopyTo(target);
            BinaryPrimitives.WriteUInt32BigEndian(target[4..], BinaryPrimitives.ReadUInt32BigEndian(entry[4..]) + (uint)addedDirSize);
        }

        matrix.AsSpan(oldData).CopyTo(profile.AsSpan(oldData + addedDirSize));

        var lutOffset = matrix.Length + addedDirSize;
        for (var i = 0; i < addedTags.Count; i++)
        {
            var addedDir = profile.AsSpan(132 + (count + i) * 12, 12);
            System.Text.Encoding.ASCII.GetBytes(addedTags[i].Tag, addedDir);
            BinaryPrimitives.WriteUInt32BigEndian(addedDir[4..], (uint)lutOffset);
            BinaryPrimitives.WriteUInt32BigEndian(addedDir[8..], (uint)addedTags[i].Lut.Length);
            addedTags[i].Lut.CopyTo(profile.AsSpan(lutOffset));
            lutOffset += addedTags[i].Lut.Length;
        }

        return profile;
    }


    [Fact]
    public void LutProfile_WideValues_NotClipped()
    {
        // 高彩 LUT（純洋紅 → B 通道 2.0691）不得削波到 1（修前 sRGB 路徑全域 ≤ 1 且色相全錯）。
        var icc = Convert.FromBase64String(CmsFixtures.NeutralRgbLut);
        var file = TiffContainer(JpegWithIcc(SolidJpeg(255, 0, 255, 8, 8), icc));
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        var d = image.Data;
        Assert.True(d[2] > 1.5f);
    }

    [Fact]
    public void MalformedIcc_FallsBackToSrgb()
    {
        // 截斷 ICC（128 位元組以下／標籤表損毀）：PreviewIcc.Parse 為 null → 上游 icc.is_some 分支 → sRGB。
        // 均勻灰 128 經 sRGB EOTF ≈ 0.2158605（與既有 RawPreviewDecodeTests 同字面值）。
        var truncated = Convert.FromBase64String(CmsFixtures.NeutralRgbLut)[..100];
        var file = TiffContainer(JpegWithIcc(SolidJpeg(128, 128, 128, 8, 8), truncated));
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        foreach (var v in image.Data)
            Assert.InRange(v, 0.2158605f - 1e-3f, 0.2158605f + 1e-3f);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    public void HeaderWithoutCompleteTagCount_UsesSrgbPreview(int length)
    {
        var icc = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(icc, length);
        "RGB "u8.CopyTo(icc.AsSpan(16));
        "XYZ "u8.CopyTo(icc.AsSpan(20));
        var file = TiffContainer(JpegWithIcc(SolidJpeg(128, 128, 128, 8, 8), icc));
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        foreach (var value in image.Data)
            Assert.InRange(value, 0.2158605f - 1e-3f, 0.2158605f + 1e-3f);
    }

    [Fact]
    public void GrayLutProfile_DecodesThroughCms()
    {
        // 灰階 LUT（gamma 1.8 亮度）：128 灰 → L*≈28.8 → 線性 ≈ 0.0578 三通道中性。
        var icc = Convert.FromBase64String(CmsFixtures.GrayLut);
        var gray = GrayJpeg(128, 8, 8);
        var file = TiffContainer(JpegWithIcc(gray, icc));
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        var d = image.Data;
        Assert.InRange(d[0], 0.0578f - 0.02f, 0.0578f + 0.02f);
        Assert.InRange(d[1], 0.0578f - 0.02f, 0.0578f + 0.02f);
        Assert.InRange(d[2], 0.0578f - 0.02f, 0.0578f + 0.02f);
    }

    [Fact]
    public void GrayModelMismatch_FallsBackToSrgb()
    {
        // RGB LUT 設定檔配灰階 JPEG：上游 wanted=Gray ≠ Rgb → sRGB 分支（不得送 CMS 導致色偏或失敗）。
        var icc = Convert.FromBase64String(CmsFixtures.NeutralRgbLut);
        var file = TiffContainer(JpegWithIcc(GrayJpeg(128, 8, 8), icc));
        Assert.True(CameraPreview.TryDecodePreview(file, 96, out var image, out _, out _));
        foreach (var v in image.Data)
            Assert.InRange(v, 0.2158605f - 1e-3f, 0.2158605f + 1e-3f);
    }

    private static byte[] GrayJpeg(byte v, int w, int h)
    {
        using var image = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        var px = new byte[w * h];
        Array.Fill(px, v);
        image.ReadPixels(px, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "I"));
        image.Format = MagickFormat.Jpeg;
        image.Quality = 100;
        image.Settings.SetDefines(new JpegWriteDefines { SamplingFactor = JpegSamplingFactor.Ratio444 });
        return image.ToByteArray();
    }
}

// 合成 CMS 回歸 fixture（位元組固定、可重現；產生器見本檔檔頭）：
// neutral RGB LUT（scnr/RGB→Lab A2B0 mft1 9³，中性軸保持中性）與
// gray LUT（scnr/GRAY→Lab A2B0，gamma 1.8；含 ICC.1 表 16 必要 kTRC 與 cprt 標籤）。

internal static class CmsFixtures
{
    internal const string NeutralRgbLut =
        "AAAPoAAAAAACIAAAc2NuclJHQiBMYWIgAAAAAAAAAAAAAAAAYWNzcG1sYWMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPbWAAEA" +
        "AAAA0y1tbGFjAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADZGVzYwAAAKgAAAAod3RwdAAA" +
        "ANAAAAAUQTJCMAAAAOQAAA68bWxhYwAAAAAAAAABAAAADGVuVVMADW5ldXRyYWxMVVQAAAAAAAAAAFhZWiAAAAAAAAD21gABAAAA" +
        "ANMtbWZ0MQAAAAADAwkAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAECAwQFBgcICQoLDA0ODxAREhMUFRYX" +
        "GBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5fYGFi" +
        "Y2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanqKmqq6yt" +
        "rq+wsbKztLW2t7i5uru8vb6/wMHCw8TFxsfIycrLzM3Oz9DR0tPU1dbX2Nna29zd3t/g4eLj5OXm5+jp6uvs7e7v8PHy8/T19vf4" +
        "+fr7/P3+/wABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj9AQUJD" +
        "REVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2BhYmNkZWZnaGlqa2xtbm9wcXJzdHV2d3h5ent8fX5/gIGCg4SFhoeIiYqLjI2O" +
        "j5CRkpOUlZaXmJmam5ydnp+goaKjpKWmp6ipqqusra6vsLGys7S1tre4ubq7vL2+v8DBwsPExcbHyMnKy8zNzs/Q0dLT1NXW19jZ" +
        "2tvc3d7f4OHi4+Tl5ufo6err7O3u7/Dx8vP09fb3+Pn6+/z9/v8AAQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMk" +
        "JSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4/QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl9gYWJjZGVmZ2hpamtsbW5v" +
        "cHFyc3R1dnd4eXp7fH1+f4CBgoOEhYaHiImKi4yNjo+QkZKTlJWWl5iZmpucnZ6foKGio6SlpqeoqaqrrK2ur7CxsrO0tba3uLm6" +
        "u7y9vr/AwcLDxMXGx8jJysvMzc7P0NHS09TV1tfY2drb3N3e3+Dh4uPk5ebn6Onq6+zt7u/w8fLz9PX29/j5+vv8/f7/AICAC3R5" +
        "FWhyIFxrKlBkNUReQDhXSixQVSBJC4COFXSHIGiAKlx5NVByQERrSjhkVSxeYCBXFYCcIHSVKmiONVyHQFCASkR5VThyYCxraiBk" +
        "IICpKnSiNWicQFyVSlCOVUSHYDiAaix5dSByKoC3NXSwQGipSlyiVVCcYESVajiOdSyHgCCANYDFQHS+Smi3VVywYFCpakSidTic" +
        "gCyViiCOQIDSSnTMVWjFYFy+alC3dUSwgDipiiyilSCcSoDgVXTZYGjSalzMdVDFgES+iji3lSywnyCpVYDuYHTnamjgdVzZgFDS" +
        "ikTMlTjFnyy+qiC3C4x5FYByIHRrKmhkNVxeQFBXSkRQVThJYCxCFYyHIICAKnR5NWhyQFxrSlBkVUReYDhXaixQIIyVKoCONXSH" +
        "QGiASlx5VVByYERrajhkdSxeKoyiNYCcQHSVSmiOVVyHYFCAakR5dThygCxrNYywQICpSnSiVWicYFyValCOdUSHgDiAiix5QIy+" +
        "SoC3VXSwYGipalyidVCcgESVijiOlSyHSozMVYDFYHS+ami3dVywgFCpikSilTicnyyVVYzZYIDSanTMdWjFgFy+ilC3lUSwnzip" +
        "qiyiYIznaoDgdXTZgGjSilzMlVDFn0S+qji3tSywFZhyIIxrKoBkNXReQGhXSlxQVVBJYERCajg7IJiAKox5NYByQHRrSmhkVVxe" +
        "YFBXakRQdThJKpiONYyHQICASnR5VWhyYFxralBkdURegDhXNZicQIyVSoCOVXSHYGiAalx5dVBygERrijhkQJipSoyiVYCcYHSV" +
        "amiOdVyHgFCAikR5lThySpi3VYywYICpanSidWicgFyVilCOlUSHnziAVZjFYIy+aoC3dXSwgGipilyilVCcn0SVqjiOYJjSaozM" +
        "dYDFgHS+imi3lVywn1CpqkSitTicapjgdYzZgIDSinTMlWjFn1y+qlC3tUSwvzipIKRrKphkNYxeQIBXSnRQVWhJYFxCalA7dUQ0" +
        "KqR5NZhyQIxrSoBkVXReYGhXalxQdVBJgERCNaSHQJiASox5VYByYHRramhkdVxegFBXikRQQKSVSpiOVYyHYICAanR5dWhygFxr" +
        "ilBklUReSqSiVZicYIyVaoCOdXSHgGiAilx5lVByn0RrVaSwYJipaoyidYCcgHSVimiOlVyHn1CAqkR5YKS+api3dYywgICpinSi" +
        "lWicn1yVqlCOtUSHaqTMdZjFgIy+ioC3lXSwn2ipqlyitVCcv0SVdaTZgJjSiozMlYDFn3S+qmi3tVywv1CpykSiKrBkNaReQJhX" +
        "SoxQVYBJYHRCamg7dVw0gFAuNbByQKRrSphkVYxeYIBXanRQdWhJgFxCilA7QLCASqR5VZhyYIxraoBkdXRegGhXilxQlVBJSrCO" +
        "VaSHYJiAaox5dYBygHRrimhklVxen1BXVbCcYKSVapiOdYyHgICAinR5lWhyn1xrqlBkYLCpaqSidZicgIyVioCOlXSHn2iAqlx5" +
        "tVByarC3daSwgJipioyilYCcn3SVqmiOtVyHv1CAdbDFgKS+ipi3lYywn4CpqnSitWicv1yVylCOgLDSiqTMlZjFn4y+qoC3tXSw" +
        "v2ipylyi1VCcNbxeQLBXSqRQVZhJYIxCaoA7dXQ0gGguilwnQLxrSrBkVaReYJhXaoxQdYBJgHRCimg7lVw0Srx5VbByYKRraphk" +
        "dYxegIBXinRQlWhJn1xCVbyHYLCAaqR5dZhygIxrioBklXRen2hXqlxQYLyVarCOdaSHgJiAiox5lYByn3RrqmhktVxearyidbCc" +
        "gKSVipiOlYyHn4CAqnR5tWhyv1xrdbywgLCpiqSilZicn4yVqoCOtXSHv2iAylx5gLy+irC3laSwn5ipqoyitYCcv3SVymiO1VyH" +
        "irzMlbDFn6S+qpi3tYywv4CpynSi1Wic31yVQMhXSrxQVbBJYKRCapg7dYw0gIAuinQnlWggSshkVbxeYLBXaqRQdZhJgIxCioA7" +
        "lXQ0n2guVchyYLxrarBkdaRegJhXioxQlYBJn3RCqmg7YMiAarx5dbBygKRriphklYxen4BXqnRQtWhJasiOdbyHgLCAiqR5lZhy" +
        "n4xrqoBktXRev2hXdcicgLyVirCOlaSHn5iAqox5tYByv3RrymhkgMipiryilbCcn6SVqpiOtYyHv4CAynR51Whyisi3lbywn7Cp" +
        "qqSitZicv4yVyoCO1XSH32iAlcjFn7y+qrC3taSwv5ipyoyi1YCc33SV6miOStRQVchJYLxCarA7daQ0gJguiownlYAgn3QZVdRe" +
        "YMhXarxQdbBJgKRCipg7lYw0n4AuqnQnYNRrashkdbxegLBXiqRQlZhJn4xCqoA7tXQ0atR5dchygLxrirBklaRen5hXqoxQtYBJ" +
        "v3RCddSHgMiAirx5lbByn6RrqphktYxev4BXynRQgNSVisiOlbyHn7CAqqR5tZhyv4xryoBk1XReitSilcicn7yVqrCOtaSHv5iA" +
        "yox51YBy33RrldSwn8ipqryitbCcv6SVypiO1YyH34CA6nR5n9S+qsi3tbywv7CpyqSi1Zic34yV6oCO9HSHVeBJYNRCasg7dbw0" +
        "gLAuiqQnlZggn4wZqoASYOBXatRQdchJgLxCirA7laQ0n5guqowntYAgauBkddRegMhXirxQlbBJn6RCqpg7tYw0v4AudeBygNRr" +
        "ishklbxen7BXqqRQtZhJv4xCyoA7gOCAitR5lchyn7xrqrBktaRev5hXyoxQ1YBJiuCOldSHn8iAqrx5tbByv6Rryphk1Yxe34BX" +
        "leCcn9SVqsiOtbyHv7CAyqR51Zhy34xr6oBkn+CpqtSitcicv7yVyrCO1aSH35iA6ox59IByquC3tdSwv8ipyryi1bCc36SV6piO" +
        "9IyH/4CAAAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNE" +
        "RUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5fYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6P" +
        "kJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanqKmqq6ytrq+wsbKztLW2t7i5uru8vb6/wMHCw8TFxsfIycrLzM3Oz9DR0tPU1dbX2Nna" +
        "29zd3t/g4eLj5OXm5+jp6uvs7e7v8PHy8/T19vf4+fr7/P3+/wABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fICEiIyQl" +
        "JicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj9AQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2BhYmNkZWZnaGlqa2xtbm9w" +
        "cXJzdHV2d3h5ent8fX5/gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp+goaKjpKWmp6ipqqusra6vsLGys7S1tre4ubq7" +
        "vL2+v8DBwsPExcbHyMnKy8zNzs/Q0dLT1NXW19jZ2tvc3d7f4OHi4+Tl5ufo6err7O3u7/Dx8vP09fb3+Pn6+/z9/v8AAQIDBAUG" +
        "BwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4/QEFCQ0RFRkdISUpLTE1OT1BR" +
        "UlNUVVZXWFlaW1xdXl9gYWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXp7fH1+f4CBgoOEhYaHiImKi4yNjo+QkZKTlJWWl5iZmpuc" +
        "nZ6foKGio6SlpqeoqaqrrK2ur7CxsrO0tba3uLm6u7y9vr/AwcLDxMXGx8jJysvMzc7P0NHS09TV1tfY2drb3N3e3+Dh4uPk5ebn" +
        "6Onq6+zt7u/w8fLz9PX29/j5+vv8/f7/AA==";

    internal const string GrayLut =
        "AAAFpAAAAAACIAAAc2NuckdSQVlMYWIgAAAAAAAAAAAAAAAAYWNzcG1sYWMAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAPbWAAEAAAAA0y1tbGFjAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAF" +
        "ZGVzYwAAAMAAAAAod3RwdAAAAOgAAAAUQTJCMAAAAPwAAARka1RSQwAABWAAAAAOY3BydAAABXAAAAAybWxhYwAA" +
        "AAAAAAABAAAADGVuVVMAC2dyYXlMVVQAAAAAAAAAAAAAAFhZWiAAAAAAAAD21gABAAAAANMtbWZ0MQAAAAABAxEA" +
        "AAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwd" +
        "Hh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5f" +
        "YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6Ch" +
        "oqOkpaanqKmqq6ytrq+wsbKztLW2t7i5uru8vb6/wMHCw8TFxsfIycrLzM3Oz9DR0tPU1dbX2Nna29zd3t/g4eLj" +
        "5OXm5+jp6uvs7e7v8PHy8/T19vf4+fr7/P3+/wCAgAKAgAaAgA2AgBWAgB+AgCyAgDqAgEmAgFuAgG2AgIKAgJiA" +
        "gK+AgMmAgOOAgP+AgAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicoKSorLC0uLzAxMjM0" +
        "NTY3ODk6Ozw9Pj9AQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2BhYmNkZWZnaGlqa2xtbm9wcXJzdHV2" +
        "d3h5ent8fX5/gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp+goaKjpKWmp6ipqqusra6vsLGys7S1tre4" +
        "ubq7vL2+v8DBwsPExcbHyMnKy8zNzs/Q0dLT1NXW19jZ2tvc3d7f4OHi4+Tl5ufo6err7O3u7/Dx8vP09fb3+Pn6" +
        "+/z9/v8AAQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8" +
        "PT4/QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl9gYWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXp7fH1+" +
        "f4CBgoOEhYaHiImKi4yNjo+QkZKTlJWWl5iZmpucnZ6foKGio6SlpqeoqaqrrK2ur7CxsrO0tba3uLm6u7y9vr/A" +
        "wcLDxMXGx8jJysvMzc7P0NHS09TV1tfY2drb3N3e3+Dh4uPk5ebn6Onq6+zt7u/w8fLz9PX29/j5+vv8/f7/AAEC" +
        "AwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNE" +
        "RUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5fYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWG" +
        "h4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanqKmqq6ytrq+wsbKztLW2t7i5uru8vb6/wMHCw8TFxsfI" +
        "ycrLzM3Oz9DR0tPU1dbX2Nna29zd3t/g4eLj5OXm5+jp6uvs7e7v8PHy8/T19vf4+fr7/P3+/wBjdXJ2AAAAAAAA" +
        "AAEBzQAAdGV4dAAAAABDb3B5cmlnaHQgKGMpIDIwMjYgRmlsbUJhdGNoIGNvbnRyaWJ1dG9ycwAAAA==";
}
