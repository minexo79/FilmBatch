// Unpack.F24ToF32 的字面量測試：期望位元以手算／上游 unpack.rs 測試為準，
// 絕非由受測程式算出。次正規化值 = man × 2^(1-63-16) = man × 2^-78。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;

namespace FilmBatch.Raw.Tests.Codecs;

public sealed class UnpackTests
{
    [Theory]
    [InlineData(0x000001u, 0x18800000u)] // 1 × 2^-78
    [InlineData(0x008000u, 0x20000000u)] // 2^-63
    [InlineData(0x00FFFFu, 0x207FFF00u)] // 最大次正規 mantissa
    [InlineData(0x800001u, 0x98800000u)] // -2^-78
    public void F24SubnormalScaleIs2ToTheMinus78(uint input, uint expectedBits) =>
        Assert.Equal(expectedBits, BitConverter.SingleToUInt32Bits(Unpack.F24ToF32(input)));

    [Theory]
    [InlineData(63u << 16, 0x3F800000u)] // 1.0（上游 unpack.rs 測試）
    [InlineData((1u << 23) | (64u << 16) | 0x8000u, 0xC0400000u)] // -3.0（上游測試）
    public void F24NormalValues(uint input, uint expectedBits) =>
        Assert.Equal(expectedBits, BitConverter.SingleToUInt32Bits(Unpack.F24ToF32(input)));

    [Fact]
    public void F24Zero()
    {
        Assert.Equal(0x00000000u, BitConverter.SingleToUInt32Bits(Unpack.F24ToF32(0u)));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(Unpack.F24ToF32(0x800000u)));
    }
}
