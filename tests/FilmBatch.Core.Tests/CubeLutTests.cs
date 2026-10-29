using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.Core.Tests;

public class CubeLutTests
{
    private const string Identity2 = """
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """;

    [Fact]
    public void IdentityCube_SamplesNodes()
    {
        var lut = CubeLut.Parse(Identity2);
        Assert.Equal(2, lut.Size);
        Assert.Equal(FilmLutColorSpace.Srgb, lut.ColorSpace);
        Assert.Equal(new Vector3(1, 0, 0), lut.Sample(new Vector3(1, 0, 0)));
        Assert.Equal(new Vector3(0, 1, 1), lut.Sample(new Vector3(0, 1, 1)));
    }

    [Fact]
    public void RejectsNonUnitDomain()
    {
        var text = Identity2.Replace("LUT_3D_SIZE 2", "DOMAIN_MIN 0.1 0 0\nLUT_3D_SIZE 2");
        Assert.Throws<InvalidDataException>(() => CubeLut.Parse(text));
    }

    [Fact]
    public void RejectsOneDShaper() =>
        Assert.Throws<InvalidDataException>(() => CubeLut.Parse("LUT_1D_SIZE 2\n0\n1\n"));

    [Fact]
    public void OversizedCube_ResamplesToMax()
    {
        var size = FilmLutFile.MaxSize + 1;
        var text = new StringBuilder($"LUT_3D_SIZE {size}\n");
        for (var i = 0; i < size * size * size; i++) text.Append("0 0 0\n");
        var lut = CubeLut.Parse(text.ToString());
        Assert.Equal(FilmLutFile.MaxSize, lut.Size);
        Assert.Equal(FilmLutColorSpace.Srgb, lut.ColorSpace);
    }

    [Fact]
    public void Import_RoundTripsThroughFilmSlotKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-luts-" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideRoot = root;
        UserLutLibrary.Reset();
        try
        {
            var key = UserLutLibrary.Import(CubeLut.Parse(Identity2), "mine");
            Assert.True(UserLutLibrary.IsUserKey(key));
            UserLutLibrary.Reset();
            var again = FilmLookLibrary.Resolve(key, TestContext.Current.CancellationToken)?.Lut;
            Assert.NotNull(again);
            Assert.Equal(FilmLutColorSpace.Srgb, again!.ColorSpace);
            Assert.Equal(new Vector3(1, 0, 0), again.Sample(new Vector3(1, 0, 0)));
            Assert.Equal(key, ModuleCatalog.Param("film.look")!.Coerce(ParamValue.Of(key)).AsText);
        }
        finally
        {
            AppPaths.OverrideRoot = null;
            UserLutLibrary.Reset();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ProPhotoCube_ParseAndResample_KeepColorSpace()
    {
        var parsed = CubeLut.Parse(Identity2, FilmLutColorSpace.ProPhoto);
        Assert.Equal(FilmLutColorSpace.ProPhoto, parsed.ColorSpace);
        Assert.Equal(new Vector3(0, 1, 0), parsed.Sample(new Vector3(0, 1, 0)));

        var resampled = parsed.Resample(5);
        Assert.Equal(5, resampled.Size);
        Assert.Equal(FilmLutColorSpace.ProPhoto, resampled.ColorSpace);
        Assert.Equal(new Vector3(1, 0, 1), resampled.Sample(new Vector3(1, 0, 1)));

        var size = FilmLutFile.MaxSize + 1;
        var text = new StringBuilder($"LUT_3D_SIZE {size}\n");
        for (var i = 0; i < size * size * size; i++) text.Append("0.25 0.5 0.75\n");
        var oversized = CubeLut.Parse(text.ToString(), FilmLutColorSpace.ProPhoto);
        Assert.Equal(FilmLutFile.MaxSize, oversized.Size);
        Assert.Equal(FilmLutColorSpace.ProPhoto, oversized.ColorSpace);
        var sample = oversized.Sample(new Vector3(.2f, .4f, .6f));
        Assert.Equal(.25f, sample.X, 4);
        Assert.Equal(.5f, sample.Y, 4);
        Assert.Equal(.75f, sample.Z, 4);
    }

    [Fact]
    public void Import_ProPhoto_ReloadsColorSpace()
    {
        WithUserLuts(() =>
        {
            var source = CubeLut.Parse(Identity2, FilmLutColorSpace.ProPhoto);
            var key = UserLutLibrary.Import(source, "prophoto");
            var stored = File.ReadAllBytes(Path.Combine(AppPaths.UserLuts, key + ".fblut"));
            Assert.Equal("FBLUT2\0\0"u8.ToArray(), stored[..8]);
            Assert.Equal(2, BitConverter.ToInt32(stored, 8));
            Assert.Equal((byte)FilmLutColorSpace.ProPhoto, stored[12]);
            Assert.Equal(8 + 4 + 1 + 2 * 2 * 2 * 3 * 2, stored.Length);

            UserLutLibrary.Reset();
            var again = FilmLookLibrary.Resolve(key, TestContext.Current.CancellationToken)?.Lut;
            Assert.NotNull(again);
            Assert.Equal(FilmLutColorSpace.ProPhoto, again!.ColorSpace);
            Assert.Equal(source.Data, again.Data);
            Assert.Equal(new Vector3(1, 0, 0), again.Sample(new Vector3(1, 0, 0)));
            Assert.Equal(new Vector3(0, 1, 1), again.Sample(new Vector3(0, 1, 1)));
        });
    }

    [Fact]
    public void Import_SameSamples_DistinctKeysPerColorSpace()
    {
        WithUserLuts(() =>
        {
            var srgb = CubeLut.Parse(Identity2);
            var pro = CubeLut.Parse(Identity2, FilmLutColorSpace.ProPhoto);
            Assert.Equal(srgb.Data, pro.Data);
            var keyS = UserLutLibrary.Import(srgb, "same");
            var keyP = UserLutLibrary.Import(pro, "same");
            Assert.NotEqual(keyS, keyP);

            var srgbBytes = File.ReadAllBytes(Path.Combine(AppPaths.UserLuts, keyS + ".fblut"));
            var proBytes = File.ReadAllBytes(Path.Combine(AppPaths.UserLuts, keyP + ".fblut"));
            Assert.Equal(HistoricalIdentity2Fblut1(), srgbBytes);
            Assert.Equal((byte)'2', proBytes[5]);
            Assert.Equal((byte)FilmLutColorSpace.ProPhoto, proBytes[12]);
            Assert.Equal(srgbBytes.AsSpan(12).ToArray(), proBytes.AsSpan(13).ToArray());

            UserLutLibrary.Reset();
            Assert.Equal(FilmLutColorSpace.Srgb, FilmLookLibrary.Resolve(keyS, TestContext.Current.CancellationToken)!.Lut!.ColorSpace);
            var loadedPro = FilmLookLibrary.Resolve(keyP, TestContext.Current.CancellationToken)?.Lut;
            Assert.Equal(FilmLutColorSpace.ProPhoto, loadedPro!.ColorSpace);
            Assert.Equal(new Vector3(1, 0, 0), loadedPro.Sample(new Vector3(1, 0, 0)));
        });
    }

    [Fact]
    public void SrgbPersistence_KeepsFblut1BytesAndUserKey()
    {
        var source = CubeLut.Parse(Identity2);
        using var stream = new MemoryStream();
        FilmLutFile.Write(stream, source);
        var written = stream.ToArray();
        var historical = HistoricalIdentity2Fblut1();
        Assert.Equal(historical, written);

        stream.Position = 0;
        var read = FilmLutFile.Read(stream);
        Assert.Equal(FilmLutColorSpace.Srgb, read.ColorSpace);
        Assert.Equal(source.Data, read.Data);

        WithUserLuts(() =>
        {
            var key = UserLutLibrary.Import(source, "legacy");
            var hash = Convert.ToHexString(SHA256.HashData(historical)).ToLowerInvariant()[..16];
            Assert.Equal("user-" + hash, key);
            UserLutLibrary.Reset();
            Assert.Equal(FilmLutColorSpace.Srgb, FilmLookLibrary.Resolve(key, TestContext.Current.CancellationToken)!.Lut!.ColorSpace);
        });
    }

    [Fact]
    public void BuiltInFblut1_LoadsAsSrgb()
    {
        var lut = FilmLookLibrary.Resolve(FilmLookLibrary.DefaultKey, TestContext.Current.CancellationToken)?.Lut;
        Assert.NotNull(lut);
        Assert.Equal(33, lut!.Size);
        Assert.Equal(FilmLutColorSpace.Srgb, lut.ColorSpace);
    }

    [Fact]
    public void Parse_RejectsUnknownColorSpace()
    {
        var unknown = (FilmLutColorSpace)7;
        Assert.Throws<ArgumentOutOfRangeException>(() => new FilmLut(2, new float[24], unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CubeLut.Parse(Identity2, unknown));
    }

    [Fact]
    public void Fblut2_RejectsUnknownSpaceAndTruncation()
    {
        using var stream = new MemoryStream();
        FilmLutFile.Write(stream, CubeLut.Parse(Identity2, FilmLutColorSpace.ProPhoto));
        var bytes = stream.ToArray();

        var unknown = (byte[])bytes.Clone();
        unknown[12] = 2;
        Assert.Throws<InvalidDataException>(() => FilmLutFile.Read(new MemoryStream(unknown)));

        var otherVersion = (byte[])bytes.Clone();
        otherVersion[5] = (byte)'3';
        Assert.Throws<InvalidDataException>(() => FilmLutFile.Read(new MemoryStream(otherVersion)));

        Assert.Throws<InvalidDataException>(() => FilmLutFile.Read(new MemoryStream(bytes, 0, 12)));

        var definedSrgbFlag = (byte[])bytes.Clone();
        definedSrgbFlag[12] = (byte)FilmLutColorSpace.Srgb;
        var asSrgb = FilmLutFile.Read(new MemoryStream(definedSrgbFlag));
        Assert.Equal(FilmLutColorSpace.Srgb, asSrgb.ColorSpace);
        Assert.Equal(CubeLut.Parse(Identity2).Data, asSrgb.Data);

        Assert.Throws<EndOfStreamException>(() => FilmLutFile.Read(new MemoryStream(bytes, 0, 20)));
    }

    private static void WithUserLuts(Action check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-luts-" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideRoot = root;
        UserLutLibrary.Reset();
        try
        {
            check();
        }
        finally
        {
            AppPaths.OverrideRoot = null;
            UserLutLibrary.Reset();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Identity 2³ 的歷史 FBLUT1：magic、int32 邊長 2、8 個頂點的 16-bit unorm，沒有色彩空間位元組。</summary>
    private static byte[] HistoricalIdentity2Fblut1()
    {
        var bytes = new byte[8 + 4 + 2 * 2 * 2 * 3 * 2];
        "FBLUT1\0\0"u8.CopyTo(bytes);
        bytes[8] = 2;
        byte[] nodes =
        [
            0, 0, 0,
            1, 0, 0,
            0, 1, 0,
            1, 1, 0,
            0, 0, 1,
            1, 0, 1,
            0, 1, 1,
            1, 1, 1,
        ];
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] == 0) continue;
            bytes[12 + i * 2] = 0xFF;
            bytes[12 + i * 2 + 1] = 0xFF;
        }
        return bytes;
    }
}
