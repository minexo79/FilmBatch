using FilmBatch.Raw.Vendors;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class VendorUtilsTests
{
    [Fact]
    public void MaskedColumns_AnchorBlackLevelsAtActiveAreaParity()
    {
        var samples = new ushort[8 * 6];
        ushort[] black = [100, 101, 102, 103];
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 8; x++) samples[y * 8 + x] = x < 4 ? black[(y % 2) * 2 + x % 2] : (ushort)5000;
        var even = VendorUtils.BlackFromColumns(samples, 8, 0..4, 0..6, new PixelRect(4, 0, 4, 6));
        Assert.Equal(new float[] { 100, 101, 102, 103 }, even.Values);
        var shifted = VendorUtils.BlackFromColumns(samples, 8, 0..4, 0..6, new PixelRect(3, 1, 4, 5));
        Assert.Equal(103f, shifted.At(0, 0, 0, 1));
        Assert.Equal(0f, VendorUtils.BlackFromColumns(samples, 8, 0..0, 0..6, new PixelRect(0, 0, 8, 6)).Mean());
    }

    [Fact]
    public void WhitePlateau_IgnoresRareHotPixelsAndOutOfRangePadding()
    {
        var samples = new ushort[105004];
        for (var i = 0; i < 100000; i++) samples[i] = (ushort)(2048 + i % 10000);
        Array.Fill(samples, (ushort)13583, 100000, 5000);
        samples[105000] = 13700;
        samples[105001] = 13900;
        samples[105002] = 14100;
        samples[105003] = 14426;
        Assert.Equal(13530f, VendorUtils.WhiteFromData(samples, 14));
        Array.Fill(samples, ushort.MaxValue, 105000, 4);
        Assert.Equal(13530f, VendorUtils.WhiteFromData(samples, 14));
        Assert.Equal(4095f, VendorUtils.WhiteFromData([], 12));
    }

    [Fact]
    public void ActiveRows_IncludeBothParitiesWhenOnlyBlueClips()
    {
        const int size = 2048;
        var samples = new ushort[size * size];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (ushort)((i / size) % 2 == 1 && i % 2 == 1 ? 15000 : i % 10000);
        var active = VendorUtils.ActiveRows(samples, size, new PixelRect(0, 0, size, size));
        Assert.Equal(14942f, VendorUtils.WhiteFromData(active, 14));
    }

    [Fact]
    public void WhitePlateau_UsesNominalWhiteWhenNoSaturationSpikeExists()
    {
        var samples = new ushort[100000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (ushort)(i % 10000);
        Assert.Equal(16383f, VendorUtils.WhiteFromData(samples, 14));
    }
}
