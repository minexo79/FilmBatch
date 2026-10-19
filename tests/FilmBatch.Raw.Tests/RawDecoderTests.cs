namespace FilmBatch.Raw.Tests;

public sealed class RawDecoderTests
{
    [Theory]
    [InlineData("000000006674797063727820", RawFormat.Cr3)]
    [InlineData("46554A4946494C4D4343442D524157", RawFormat.Raf)]
    [InlineData("4949524F", RawFormat.Orf)]
    [InlineData("49495253", RawFormat.Orf)]
    [InlineData("4D4D4F52", RawFormat.Orf)]
    [InlineData("49495500", RawFormat.Rw2)]
    [InlineData("004D524D", RawFormat.Mrw)]
    [InlineData("464F5662", RawFormat.X3f)]
    [InlineData("4949000000004845415043434452", RawFormat.Crw)]
    [InlineData("49492A00000000004352", RawFormat.Cr2)]
    public void ContainerMagic_RecognizesFormatWithoutAFileName(string hex, RawFormat expected) =>
        Assert.Equal(expected, RawDecoder.Probe(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("")]
    [InlineData("FFD8FFD9")]
    [InlineData("89504E470D0A1A0A")]
    [InlineData("49492A00FFFFFFFF")]
    public void OrdinaryOrTruncatedInput_IsNotClaimedAsRaw(string hex)
    {
        var data = Convert.FromHexString(hex);
        Assert.Null(RawDecoder.Probe(data));
        Assert.Equal(RawErrorKind.NotRaw, Assert.Throws<RawException>(() => RawDecoder.Decode(data)).Kind);
    }

    [Theory]
    [InlineData("004D524D", RawFormat.Mrw)]
    [InlineData("464F5662", RawFormat.X3f)]
    [InlineData("4949000000004845415043434452", RawFormat.Crw)]
    public void RecognizedUnsupportedContainers_AreRefusedInsteadOfUsingJpeg(string hex, RawFormat format)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.False(RawDecoder.IsSupported(format));
        Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.Decode(bytes)).Kind);
        Assert.Equal(RawErrorKind.Unsupported, Assert.Throws<RawException>(() => RawDecoder.ProbeInfo(bytes)).Kind);
    }
}
