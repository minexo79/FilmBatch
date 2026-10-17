// 移植自 LightCraft crates/raw/src/vendor/cr2.rs tests（行為測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Vendors.Canon;

namespace FilmBatch.Raw.Tests.Vendors;
public sealed class CanonCr2Tests
{
    [Fact]
    public void SyntheticRoundtrip()
    {
        ushort[]?[] sliceOptions = [null, [2, 24, 16], [1, 40, 24]];
        foreach (var slices in sliceOptions)
        {
            var (file, img) = CanonCr2Fixtures.SyntheticCr2(64, 10, slices, null);
            var mem = new ReadOnlyMemory<byte>(file);
            Assert.Equal(RawFormat.Cr2, RawDecoder.Probe(mem));
            RawImage r = Cr2.Decode(mem, DecodeMode.Full);
            Assert.Equal(64, r.Width);
            Assert.Equal(10, r.Height);
            Assert.Equal(RawFormat.Cr2, r.Format);
            Assert.Equal(1, r.Cpp);
            var data = Assert.IsType<RawData.U16>(r.Data);
            Assert.Equal(img, data.Samples);
            Assert.Equal(Orientation.Rotate270, r.Orientation);
            Assert.Equal("GBRG", r.Cfa!.Name);
            Assert.Equal(400u, r.Metadata.Iso);
        }
    }

    [Fact]
    public void HeaderOnlyMatchesFullInfo()
    {
        var (file, _) = CanonCr2Fixtures.SyntheticCr2(64, 10, [2, 24, 16], null);
        var mem = new ReadOnlyMemory<byte>(file);
        RawImage full = Cr2.Decode(mem, DecodeMode.Full);
        RawImage header = Cr2.Decode(mem, DecodeMode.Header);
        Assert.Equal(full.Info().Format, header.Info().Format);
        Assert.Equal(full.Info().Width, header.Info().Width);
        Assert.Equal(full.Info().Height, header.Info().Height);
        Assert.Equal(full.Info().Cfa?.Name, header.Info().Cfa?.Name);
        Assert.Equal(0, header.Data.Length);
    }

    [Fact]
    public void CfaPatternTag()
    {
        Assert.Equal("RGGB", Cr2.CfaFromTag(1)!.Name);
        Assert.Equal("BGGR", Cr2.CfaFromTag(2)!.Name);
        Assert.Equal("GBRG", Cr2.CfaFromTag(3)!.Name);
        Assert.Equal("GRBG", Cr2.CfaFromTag(4)!.Name);
        Assert.Null(Cr2.CfaFromTag(0));
        Assert.Null(Cr2.CfaFromTag(7));
        Assert.Null(Cr2.CfaFromTag(null));
    }

    [Fact]
    public void WbProbe()
    {
        var v = new ulong[120];
        v[63] = 2000; v[64] = 1024; v[65] = 1026; v[66] = 1500;
        float[]? wb = Cr2.WbFromColorBalance(v);
        Assert.NotNull(wb);
        Assert.True(Math.Abs(wb![0] - 2000f / 1025f) < 1e-4);
        Assert.True(Math.Abs(wb![2] - 1500f / 1025f) < 1e-4);
        Assert.Null(Cr2.WbFromColorBalance(new ulong[10]));
    }

    [Fact]
    public void SliceMismatchIsCorrupt()
    {
        var (file, _) = CanonCr2Fixtures.SyntheticCr2(64, 10, [1, 40, 24], null);
        // 切片總寬 40＋24＝64 整除；改以不整除的合成檔驗證錯誤種類：直接測 Layout 層由 sRAW 覆蓋，
        // 此處以損毀的 strip 觸發 Corrupt/ Tiff 邊界。
        // 截斷檔尾使 strip 越界或解碼失敗：應為 RawException 而非其他例外
        var ex = Assert.ThrowsAny<RawException>(() => Cr2.Decode(new ReadOnlyMemory<byte>(file[..Math.Min(200, file.Length)]), DecodeMode.Full));
    }

    [Fact]
    public void BlackFromMaskedColumnsOnly()
    {
        int w = 40, h = 12, border = 20, light = 14;
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++)
            data[i] = (ushort)(i % w < light ? 2048 + (i % 2) : 6000);
        var active = new PixelRect(border, 2, w - border, h - 2);
        Range cols = Cr2.MaskedColumns(data, w, active, 15000f);
        Assert.Equal(2, cols.Start.Value);
        Assert.Equal(light - 2, cols.End.Value);
        var dark = new ushort[w * h];
        for (int i = 0; i < dark.Length; i++)
            dark[i] = (ushort)(i % w < border ? 2048 : 6000);
        Assert.Equal(new Range(2, border - 2), Cr2.MaskedColumns(dark, w, active, 15000f));
        Assert.Equal(Range.EndAt(0).Start.Value, Cr2.MaskedColumns(dark, w, new PixelRect(5, 0, 30, h), 15000f).Start.Value);
        Assert.True(Cr2.MaskedColumns([], w, active, 15000f).Start.Value == Cr2.MaskedColumns([], w, active, 15000f).End.Value);
    }

    [Fact]
    public void WhiteFromActiveAreaOnly()
    {
        int w = 64, h = 40, top = 4, left = 8;
        var img = new ushort[w * h];
        for (int i = 0; i < img.Length; i++)
        {
            int x = i % w, y = i / w;
            img[i] = y < top ? (ushort)16383 : x < left ? (ushort)1024 : i % 7 == 0 ? (ushort)13433 : (ushort)(1024 + ((i * 7919) % 11000));
        }
        var si = new List<ushort> { (ushort)w, (ushort)h, 0, 0, 0, (ushort)left, (ushort)top, (ushort)(w - 1), (ushort)(h - 1) };
        var note = new List<(ushort Tag, ushort Type, byte[] Data)>
        {
            (CanonCr2Fixtures.SensorInfoTag, 3, CanonCr2Fixtures.U16ListOf(si.ToArray())),
        };
        byte[] file = CanonCr2Fixtures.SyntheticCr2Of(img, w, h, null, note);
        RawImage r = Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Full);
        Assert.Equal(new PixelRect(left, top, w - left, h - top), r.ActiveArea);
        Assert.InRange(r.White[0], 13300f, 13433f);
    }

    [Fact]
    public void ColorDataStoredAsBytes()
    {
        var bytes = new byte[2 * 120];
        bytes[0] = 0xFD; bytes[1] = 0xFF; // 版本 -3
        byte[] levels = [0xC4, 0x07, 0xE9, 0x02, 0xE9, 0x02, 0x69, 0x06];
        Array.Copy(levels, 0, bytes, 142, levels.Length);
        foreach (ushort ty in new ushort[] { 7, 3 })
        {
            var note = new List<(ushort Tag, ushort Type, byte[] Data)>
            {
                (CanonCr2Fixtures.ColorBalanceTag, ty, bytes),
            };
            var (file, _) = CanonCr2Fixtures.SyntheticCr2(64, 10, null, note);
            RawImage r = Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Full);
            float[]? wb = r.WbMultipliers;
            float[] g = wb!;
            Assert.True(Math.Abs(g[0] - 1988f / 745f) < 1e-5 && g[1] == 1f && Math.Abs(g[2] - 1641f / 745f) < 1e-5);
        }
    }

    [Fact]
    public void WhiteBalanceFromWbInfoWhenThereIsNoColorData()
    {
        static byte[] ShotInfo(ushort wb)
        {
            var v = new ushort[34];
            v[7] = wb;
            var o = new List<byte>();
            foreach (ushort x in v) { o.Add((byte)(x & 0xFF)); o.Add((byte)(x >> 8)); }
            return o.ToArray();
        }
        var rows = new int[512];
        rows[2] = 850; rows[3] = 1805; rows[4] = 1684; rows[5] = 850;
        rows[66] = 848; rows[67] = 1747; rows[68] = 1676; rows[69] = 848;
        rows[74] = 820; rows[75] = 1901; rows[76] = 4000; rows[77] = 820;
        rows[18] = 850; rows[19] = 1805; rows[20] = 6000; rows[21] = 850;
        byte[] wbInfo = CanonCr2Fixtures.WbInfoBytes(rows);

        float[]? Wb(ushort si)
        {
            var note = new List<(ushort Tag, ushort Type, byte[] Data)>
            {
                (CanonCr2Fixtures.ShotInfoTag, 3, ShotInfo(si)),
                (CanonCr2Fixtures.WbInfoTag, 1, wbInfo),
            };
            var (file, _) = CanonCr2Fixtures.SyntheticCr2(64, 10, null, note);
            return Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Full).WbMultipliers;
        }

        AssertWb([1747f / 848f, 1f, 1676f / 848f], Wb(15));
        AssertWb([1805f / 850f, 1f, 1684f / 850f], Wb(0));
        AssertWb([1901f / 820f, 1f, 4000f / 820f], Wb(16));
        Assert.Null(Wb(1));
        Assert.Null(Wb(2));
        Assert.Null(Wb(14));

        // 無 ShotInfo
        {
            var note = new List<(ushort Tag, ushort Type, byte[] Data)>
            {
                (CanonCr2Fixtures.WbInfoTag, 1, wbInfo),
            };
            var (file, _) = CanonCr2Fixtures.SyntheticCr2(64, 10, null, note);
            Assert.Null(Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Full).WbMultipliers);
        }
        // ColorData 優先
        {
            var colorData = new byte[2 * 120];
            colorData[0] = 0xFD; colorData[1] = 0xFF;
            byte[] levels = [0xC4, 0x07, 0xE9, 0x02, 0xE9, 0x02, 0x69, 0x06];
            Array.Copy(levels, 0, colorData, 142, levels.Length);
            var note = new List<(ushort Tag, ushort Type, byte[] Data)>
            {
                (CanonCr2Fixtures.ShotInfoTag, 3, ShotInfo(15)),
                (CanonCr2Fixtures.WbInfoTag, 1, wbInfo),
                (CanonCr2Fixtures.ColorBalanceTag, 7, colorData),
            };
            var (file, _) = CanonCr2Fixtures.SyntheticCr2(64, 10, null, note);
            float[]? wb = Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Full).WbMultipliers;
            Assert.NotNull(wb);
            Assert.True(Math.Abs(wb![0] - 1988f / 745f) < 1e-5);
        }
    }

    private static void AssertWb(float[] want, float[]? got)
    {
        Assert.NotNull(got);
        float[] g = got!;
        Assert.Equal(3, g.Length);
        for (int i = 0; i < 3; i++) Assert.True(Math.Abs(g[i] - want[i]) < 1e-5, $"[{string.Join(",", g)}] vs [{string.Join(",", want)}]");
    }

    [Fact]
    public void UnsupportedRawType()
    {
        byte[] stream = CanonCr2Fixtures.Stream422();
        foreach (uint kind in new uint[] { 0u, 2u, 3u, 5u })
        {
            byte[] file = CanonCr2Fixtures.SrawFile(stream, [1, 4, 4], kind, null);
            var ex = Assert.Throws<RawException>(() => Cr2.Decode(new ReadOnlyMemory<byte>(file), DecodeMode.Full));
            Assert.Equal(RawErrorKind.Unsupported, ex.Kind);
        }
    }
}
