// 測試支援：lightcraft-raw rafc.rs 測具的等值實作（zero_stream/compressed 產生確定性的壓縮條帶流）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Tests.Support;

/// <summary>
/// 依上游測試建構「零殘差」的富士條帶式壓縮流：兩個條帶、全部殘差為 0、
/// 每個區塊的量化器表與熵編碼位元流皆可重現上游 <c>compressed(bits, xtrans, quantizers)</c>。
/// </summary>
internal static class RafCompressedFixture
{
    private static readonly byte[][] Masks =
    [
        [10, 15],
        [15, 10],
        [14, 10],
        [15, 11],
        [11, 15],
        [10, 14],
    ];

    /// <summary>上游 <c>zero_stream</c>：每個 context 寫入代表殘差 0 的最短碼（1 個 1 位元 + k 個 0 位元）。</summary>
    public static byte[] ZeroStream(int bits, bool xtrans, byte[]? quantizers)
    {
        var groups = quantizers?.Length ?? 3;
        // main[pair][odd] = (sum, count)；quantizer != 0 時改用 fine 狀態。
        var main = new (uint Sum, uint Count)[3, 2];
        var fine = new (uint Sum, uint Count)[3, 2];
        uint seed = (uint)(1 << (bits - 6));
        for (var p = 0; p < 3; p++)
        for (var o = 0; o < 2; o++)
            main[p, o] = fine[p, o] = (seed, 1);
        uint? previous = null;
        var stream = new List<bool>();
        for (var group = 0; group < groups; group++)
        {
            var q = quantizers?[group] ?? 0u;
            if (previous != q)
            {
                for (var p = 0; p < 3; p++)
                for (var o = 0; o < 2; o++)
                    main[p, o] = (seed, 1);
                previous = q;
            }
            for (var pair = 0; pair < 6; pair++)
            {
                var width = xtrans ? 512 : 384;
                for (var even = 0; even < width + 8; even += 2)
                {
                    for (var parity = 0; parity < 2; parity++)
                    {
                        var column = parity == 0 ? even : even - 7;
                        if (column < 0 || column >= width) continue;
                        for (var colour = 0; colour < 2; colour++)
                        {
                            if (xtrans && ((Masks[pair][colour] >> (column % 4)) & 1) == 0) continue;
                            ref var state = ref (q == 0 ? ref main[pair % 3, parity] : ref fine[pair % 3, parity]);
                            // 上游：k = ceil(log2(sum/count))，sum - 1 使用飽和減法。
                            var t = (state.Sum == 0 ? 0u : state.Sum - 1) / state.Count;
                            var k = 32 - System.Numerics.BitOperations.LeadingZeroCount(t);
                            stream.Add(true);
                            for (var i = 0; i < k; i++) stream.Add(false);
                            if (state.Count == 64)
                            {
                                state.Sum /= 2;
                                state.Count /= 2;
                            }
                            state.Count += 1;
                        }
                    }
                }
            }
        }
        return PackBitsMsb(stream);
    }

    /// <summary>上游 <c>compressed</c>：一個含兩個條帶的容器（第二個條帶主要是多出的位元組，不可進入輸出）。</summary>
    public static byte[] Compressed(int bits, bool xtrans, byte[]? quantizers)
    {
        var groups = quantizers?.Length ?? 3;
        var stream = ZeroStream(bits, xtrans, quantizers);
        var output = new List<byte>();
        output.AddRange("IS"u8.ToArray());
        output.Add((byte)(quantizers is null ? 1 : 0));
        output.Add((byte)(xtrans ? 16 : 0));
        output.Add((byte)bits);
        foreach (var v in new ushort[] { (ushort)(groups * 6), 1536, 780, 768 })
            output.AddRange(RafFixtureBytes.ToBytesBigEndian(v));
        output.Add(2);
        output.AddRange(RafFixtureBytes.ToBytesBigEndian((ushort)groups));
        for (var i = 0; i < 2; i++)
            output.AddRange(RafFixtureBytes.ToBytesBigEndian((uint)stream.Length));
        while (output.Count < 32) output.Add(0);
        if (quantizers is not null)
        {
            for (var i = 0; i < 2; i++)
            {
                output.AddRange(quantizers);
                var pad = (quantizers.Length + 15) / 16 * 16 - quantizers.Length;
                for (var k = 0; k < pad; k++) output.Add(0);
            }
        }
        output.AddRange(stream);
        output.AddRange(stream);
        return output.ToArray();
    }

    /// <summary>上游 <c>compressed</c> 使用的位元打包：每 8 位元一個位元組，MSB first。</summary>
    internal static byte[] PackBitsMsb(List<bool> bits)
    {
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i])
                bytes[i / 8] |= (byte)(1 << (7 - i % 8));
        return bytes;
    }
}
