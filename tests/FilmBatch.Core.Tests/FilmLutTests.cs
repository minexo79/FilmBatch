using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

public class FilmLutTests
{
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(.2f, .7f, .45f)]
    [InlineData(.91f, .13f, .5f)]
    public void Identity_SamplesBackInput(float r, float g, float b)
    {
        var got = FilmLut.Identity(33).Sample(new Vector3(r, g, b));
        Assert.Equal(r, got.X, 5);
        Assert.Equal(g, got.Y, 5);
        Assert.Equal(b, got.Z, 5);
    }

    [Fact]
    public void Sample_ClampsOutOfRangeInput()
    {
        var got = FilmLut.Identity(17).Sample(new Vector3(-.5f, 1.5f, .5f));
        Assert.Equal(new Vector3(0, 1, .5f), got);
    }

    [Fact]
    public void Sample_HitsNodesExactly_OnArbitraryLut()
    {
        var random = new System.Random(3);
        var data = new float[5 * 5 * 5 * 3];
        for (var i = 0; i < data.Length; i++) data[i] = (float)random.NextDouble();
        var lut = new FilmLut(5, data);
        // 節點 (r=1, g=3, b=2) → index ((2*5+3)*5+1)*3
        var got = lut.Sample(new Vector3(1 / 4f, 3 / 4f, 2 / 4f));
        var i0 = ((2 * 5 + 3) * 5 + 1) * 3;
        Assert.Equal(data[i0], got.X, 5);
        Assert.Equal(data[i0 + 1], got.Y, 5);
        Assert.Equal(data[i0 + 2], got.Z, 5);
    }

    [Fact]
    public void FromHald_UsesHaldPixelOrder()
    {
        // level 2 → 邊長 4，影像 8×8；Hald 像素 k = r + g*4 + b*16
        var level = 2; var n = level * level;
        var rgb = new float[n * n * n * 3];
        for (var k = 0; k < n * n * n; k++)
        {
            rgb[k * 3] = k % n / (n - 1f);
            rgb[k * 3 + 1] = k / n % n / (n - 1f);
            rgb[k * 3 + 2] = k / (n * n) / (n - 1f);
        }
        var lut = FilmLut.FromHald(rgb, level);
        Assert.Equal(4, lut.Size);
        Assert.Equal(new Vector3(1 / 3f, 2 / 3f, 1), lut.Sample(new Vector3(1 / 3f, 2 / 3f, 1)));
    }

    [Fact]
    public void Resample_IdentityStaysIdentity()
    {
        var lut = FilmLut.Identity(144).Resample(33);
        Assert.Equal(33, lut.Size);
        var got = lut.Sample(new Vector3(.3f, .6f, .9f));
        Assert.Equal(.3f, got.X, 4);
        Assert.Equal(.6f, got.Y, 4);
        Assert.Equal(.9f, got.Z, 4);
    }

    [Fact]
    public void ToTextureRgba_LaysOutBlueSlicesHorizontally()
    {
        var lut = FilmLut.Identity(3);
        var texture = lut.ToTextureRgba(); // 寬 9（r + b*3），高 3（g）
        Assert.Equal(9 * 3 * 4, texture.Length);
        // (r=2, g=1, b=2) → x = 2 + 2*3 = 8, y = 1
        var t = (1 * 9 + 8) * 4;
        Assert.Equal(new[] { 1f, .5f, 1f, 1f }, texture[t..(t + 4)]);
    }

    [Fact]
    public void File_RoundTripsWithin16BitPrecision()
    {
        var lut = FilmLut.Identity(9);
        using var stream = new MemoryStream();
        FilmLutFile.Write(stream, lut);
        Assert.Equal(8 + 4 + 9 * 9 * 9 * 3 * 2, stream.Length);
        stream.Position = 0;
        var read = FilmLutFile.Read(stream);
        Assert.Equal(9, read.Size);
        for (var i = 0; i < lut.Data.Length; i++) Assert.Equal(lut.Data[i], read.Data[i], 4);
    }

    [Fact]
    public void File_RejectsWrongMagic()
    {
        using var stream = new MemoryStream(new byte[64]);
        Assert.Throws<InvalidDataException>(() => FilmLutFile.Read(stream));
    }
}
