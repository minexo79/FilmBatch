// 移植自 LightCraft crates/raw/src/vendor/rafc.rs（Fujifilm 條帶式預測 RAW 壓縮）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Runtime.ExceptionServices;

namespace FilmBatch.Raw.Vendors.Fujifilm;

/// <summary>
/// Fujifilm 條帶式預測 RAW 壓縮（上游 clean-room 實作；演算法見上游
/// crates/raw/docs/raf-compression.md，初始描述來自 capnfabs 的說明文章）。
/// 每個條帶寬 768（X-Trans 每列實際 512 個樣本），高為 6 的倍數；
/// 三個顏色向量各以列緩衝保存，量化器群組改變時重新播種適應狀態。
/// </summary>
internal static class RafCompressed
{
    private const int Block = 768;

    private static readonly byte[,] XTransPattern =
    {
        { 1, 1, 0, 1, 1, 2 },
        { 1, 1, 2, 1, 1, 0 },
        { 2, 0, 1, 0, 2, 1 },
        { 1, 1, 2, 1, 1, 0 },
        { 1, 1, 0, 1, 1, 2 },
        { 0, 2, 1, 2, 0, 1 },
    };

    private static readonly byte[,] Masks =
    {
        { 10, 15 },
        { 15, 10 },
        { 14, 10 },
        { 15, 11 },
        { 11, 15 },
        { 10, 14 },
    };

    private static RawException Corrupt(string why) => RawException.Corrupt($"Fujifilm compressed RAF: {why}");

    private static int Be16(ReadOnlySpan<byte> src, int at)
    {
        if (at < 0 || (uint)(at + 2) > (uint)src.Length) throw Corrupt("truncated header");
        return (src[at] << 8) | src[at + 1];
    }

    /// <summary>一個條帶的位元組、（lossy 時的）量化器表與實際欄寬。</summary>
    private readonly record struct Stripe(byte[] Bytes, byte[]? Quantizers, int Width);

    /// <summary>上游 <c>rafc::decode</c>：16 位元組檔頭、條帶大小表、（lossless 時）量化器表，然後逐條帶解碼。</summary>
    internal static ushort[] Decode(ReadOnlySpan<byte> src, int width, int height, int bits, bool xtrans, DecodeMode mode)
    {
        if (src.Length < 16) throw Corrupt("truncated header");
        if (src[0] != (byte)'I' || src[1] != (byte)'S')
            throw Corrupt("missing compression signature");
        var flag = src[2];
        if (flag > 1) throw Corrupt("invalid compression flag");
        var sensor = src[3];
        if (sensor != (xtrans ? 16 : 0))
            throw Corrupt("compression sensor type disagrees with CFA");
        if (bits is not (12 or 14 or 16))
            throw RawException.Unsupported($"Fujifilm compressed RAF at {bits} bits");
        var count = src[13];
        if (src[4] != bits
            || Be16(src, 5) != height
            || Be16(src, 9) != width
            || count == 0
            || count != (width + (long)Block - 1) / Block
            || Be16(src, 7) != count * Block
            || Be16(src, 11) != Block
            || height == 0
            || height % 6 != 0
            || Be16(src, 14) != height / 6)
        {
            throw Corrupt("inconsistent compression dimensions");
        }
        if ((long)width * height > RawLimits.MaxSamples)
            throw RawException.Limit("image too large");
        var n = checked(width * height);
        var tableEnd = 16 + count * 4L;
        var offset = (tableEnd + 15) / 16 * 16;
        var quantizerStart = offset;
        var quantizerStride = (height / 6 + 15) / 16L * 16;
        if (flag == 0)
        {
            offset += quantizerStride * count;
            if (offset > src.Length)
                throw Corrupt("truncated quantizer tables");
        }
        var stripes = new Stripe[count];
        for (var i = 0; i < count; i++)
        {
            if (16 + i * 4L + 4 > src.Length)
                throw Corrupt("truncated stripe size table");
            var size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(src.Slice(16 + i * 4, 4));
            var end = offset + size;
            if (size == 0 || end > src.Length)
                throw Corrupt("stripe outside file");
            var bytes = src.Slice((int)offset, (int)size).ToArray();
            byte[]? quantizers = null;
            if (flag == 0)
            {
                var qStart = quantizerStart + i * quantizerStride;
                if (qStart + height / 6 > src.Length)
                    throw Corrupt("truncated quantizer table");
                quantizers = src.Slice((int)qStart, height / 6).ToArray();
            }
            stripes[i] = new Stripe(bytes, quantizers, Math.Min(Block, width - i * Block));
            offset = end;
        }
        if (mode == DecodeMode.Header) return [];

        var decoded = new ushort[count][];
        var errors = new Exception?[count];
        Parallel.For(0, count, i =>
        {
            try
            {
                decoded[i] = DecodeStripe(stripes[i], height, bits, xtrans);
            }
            catch (Exception e)
            {
                errors[i] = e;
            }
        });
        for (var i = 0; i < count; i++)
            if (errors[i] is { } e)
                ExceptionDispatchInfo.Throw(e);

        var output = new ushort[n];
        for (var i = 0; i < count; i++)
        {
            var stripe = stripes[i];
            var data = decoded[i];
            for (var row = 0; row < height; row++)
                data.AsSpan(row * stripe.Width, stripe.Width)
                    .CopyTo(output.AsSpan(row * width + i * Block, stripe.Width));
        }
        return output;
    }

    /// <summary>上游 <c>Bits</c>：MSB-first 位元讀取器，超出來源時丟「truncated entropy stream」。</summary>
    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _src;
        private int _pos;
        private ulong _acc;
        private int _available;

        internal Bits(ReadOnlySpan<byte> src)
        {
            _src = src;
            _pos = 0;
            _acc = 0;
            _available = 0;
        }

        internal int Get(int n)
        {
            while (_available < n)
            {
                if ((uint)_pos >= (uint)_src.Length) throw Corrupt("truncated entropy stream");
                _acc = (_acc << 8) | _src[_pos++];
                _available += 8;
            }
            _available -= n;
            return (int)((_acc >> _available) & ((1ul << n) - 1));
        }

        /// <summary>殘差解碼：前導零計數（prefix == limit 時為 escape 讀 bits 位元），接著 Golomb-Rice tail。</summary>
        internal int Residual(int k, int bits, int range, int limit)
        {
            var zeros = 0;
            while (Get(1) == 0)
            {
                zeros++;
                if (zeros > limit) throw Corrupt("invalid residual escape");
            }
            var code = zeros == limit ? Get(bits) + 1 : (zeros << k) | Get(k);
            if (code > range)
                throw Corrupt($"residual {code} outside sample range {range} (k={k}, prefix={zeros}, bits={bits}, position={_pos * 8 - _available})");
            return (code & 1) == 0 ? code / 2 : -(code / 2) - 1;
        }
    }

    private struct State
    {
        internal uint Sum;
        internal uint Count;

        internal int K()
        {
            var k = 0;
            while (Count << k < Sum) k++;
            return k;
        }

        internal void Update(int delta)
        {
            Sum += (uint)Math.Abs(delta);
            if (Count == 64)
            {
                Sum >>= 1;
                Count >>= 1;
            }
            Count += 1;
        }
    }

    private static int Quant(int v, int near)
    {
        var a = Math.Abs(v);
        var bucket = a <= near
            ? 0
            : a < 18 + 3 * near
                ? 1
                : a < 67 + 5 * near
                    ? 2
                    : a < 276 + 7 * near
                        ? 3
                        : 4;
        return v < 0 ? -bucket : bucket;
    }

    private static int EvenPrediction(int nw, int n, int ne, int nn)
    {
        var a = Math.Abs(nw - n);
        var b = Math.Abs(ne - n);
        var c = Math.Abs(nn - n);
        var pair = a > b && a > c
            ? ne + nn
            : b > a && b > c
                ? nw + nn
                : nw + ne;
        return (pair + 2 * n) >> 2;
    }

    /// <summary>顏色向量列 <paramref name="y"/>、欄 <paramref name="x"/> 的樣本（界外丟 invalid predictor position）。</summary>
    private static int Sample(int[][] lines, int y, int x)
    {
        if ((uint)y >= (uint)lines.Length || (uint)x >= (uint)lines[y].Length)
            throw Corrupt("invalid predictor position");
        return lines[y][x];
    }

    private static ushort[] DecodeStripe(Stripe stripe, int height, int bits, bool xtrans)
    {
        var size = xtrans ? Block * 2 / 3 : Block / 2;
        // 每個顏色向量：先含上一群組留下的 2 列尾巴，群組內依序加入新列。
        var rows = new int[3][][];
        for (var c = 0; c < 3; c++)
        {
            rows[c] = new int[2][];
            for (var i = 0; i < 2; i++) rows[c][i] = new int[size + 2];
        }
        var max = (1 << bits) - 1;
        int Range(int near) => (max + 2 * near) / (2 * near + 1) + 1;
        State Seed(int near) => new() { Sum = (uint)Math.Max(2, (Range(near) + 32) / 64), Count = 1 };
        var states = new State[3, 2, 41];
        void ResetStates(int near)
        {
            for (var p = 0; p < 3; p++)
            for (var o = 0; o < 2; o++)
            for (var i = 0; i < 41; i++)
                states[p, o, i] = Seed(near);
        }
        ResetStates(0);
        // 平滑區域保留較高精度、且跨量化器群組維持獨立狀態：[precision][pair%3][odd][|context|]。
        var fine = new State[3, 3, 2, 5];
        for (var p = 0; p < 3; p++)
        for (var t = 0; t < 3; t++)
        for (var o = 0; o < 2; o++)
        for (var i = 0; i < 5; i++)
            fine[p, t, o, i] = Seed(p);
        int? lastNear = null;
        var reader = new Bits(stripe.Bytes);
        var output = new List<ushort>(checked(height * stripe.Width));
        for (var group = 0; group < height / 6; group++)
        {
            var near = stripe.Quantizers is { } q ? q[group] : 0;
            if (lastNear != near)
            {
                ResetStates(near);
                lastNear = near;
            }
            for (var pair = 0; pair < 6; pair++)
            {
                ReadOnlySpan<int> colours = pair % 2 == 0 ? [0, 1] : [1, 2];
                // 為本 pair 的兩個顏色各加入一列，延續上一列的左右邊界。
                foreach (var colour in colours)
                {
                    var lines = rows[colour];
                    var prev = lines.Length - 1;
                    var row = new int[size + 2];
                    row[0] = Sample(lines, prev, 1);
                    row[^1] = Sample(lines, prev, size);
                    var next = new int[lines.Length + 1][];
                    Array.Copy(lines, next, lines.Length);
                    next[lines.Length] = row;
                    rows[colour] = next;
                }
                for (var even = 0; even < size + 8; even += 2)
                {
                    // odd=0 在 even 位置；odd=1 在 even-7（even<7 時不存在）。
                    for (var odd = 0; odd < 2; odd++)
                    {
                        var pos = odd == 0 ? even : even >= 7 ? even - 7 : -1;
                        if (pos < 0 || pos >= size) continue;
                        var x = pos + 1;
                        for (var which = 0; which < colours.Length; which++)
                        {
                            var colour = colours[which];
                            var lines = rows[colour];
                            var y = lines.Length - 1;
                            var nw = Sample(lines, y - 1, x - 1);
                            var north = Sample(lines, y - 1, x);
                            var ne = Sample(lines, y - 1, x + 1);
                            var nn = Sample(lines, y - 2, x);
                            var west = Sample(lines, y, x - 1);
                            var east = Sample(lines, y, x + 1);
                            int predict, d1, d2;
                            if (odd == 0)
                            {
                                predict = EvenPrediction(nw, north, ne, nn);
                                d1 = north - nn;
                                d2 = nw - north;
                            }
                            else
                            {
                                predict = north >= Math.Min(nw, ne) && north <= Math.Max(nw, ne)
                                    ? (west + east) >> 1
                                    : (west + east + 2 * north) >> 2;
                                d1 = north - nw;
                                d2 = nw - west;
                            }
                            var real = !xtrans || ((Masks[pair, which] >> (pos % 4)) & 1) != 0;
                            int value;
                            if (real)
                            {
                                var contrast = Math.Abs(d1) + Math.Abs(d2);
                                var precision = contrast switch
                                {
                                    <= 5 => 0,
                                    6 => Math.Min(near, 1),
                                    7 => Math.Min(near, 2),
                                    _ => near,
                                };
                                int context;
                                int codedRange;
                                int delta;
                                if (precision < near)
                                {
                                    int Sign(int v) => Math.Abs(v) <= precision ? 0 : Math.Sign(v);
                                    context = 3 * Sign(d1) + Sign(d2);
                                    ref var state = ref fine[precision, pair % 3, odd, Math.Abs(context)];
                                    codedRange = Range(precision);
                                    var codedBits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(codedRange - 1));
                                    delta = reader.Residual(state.K(), codedBits, codedRange, 4 * bits - codedBits - 1);
                                    state.Update(delta);
                                }
                                else
                                {
                                    context = 9 * Quant(d1, near) + Quant(d2, near);
                                    ref var state = ref states[pair % 3, odd, Math.Abs(context)];
                                    codedRange = Range(near);
                                    var codedBits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(codedRange - 1));
                                    delta = reader.Residual(state.K(), codedBits, codedRange, 4 * bits - codedBits - 1);
                                    state.Update(delta);
                                }
                                if (context < 0) delta = -delta;
                                var step = 2 * precision + 1;
                                value = predict + delta * step;
                                if (value < -precision) value += codedRange * step;
                                else if (value > max + precision) value -= codedRange * step;
                                value = Math.Clamp(value, 0, max);
                            }
                            else
                            {
                                value = predict;
                            }
                            lines[y][x] = value;
                        }
                    }
                }
            }
            for (var y = 0; y < 6; y++)
            {
                for (var x = 0; x < stripe.Width; x++)
                {
                    var colour = xtrans
                        ? XTransPattern[y, x % 6]
                        : (y % 2, x % 2) switch
                        {
                            (0, 0) => 0,
                            (1, 1) => 2,
                            _ => 1,
                        };
                    var row = 2 + (colour == 1 ? y : y / 2);
                    var col = 1 + (xtrans ? 2 * (x / 3) + Math.Min(x % 3, 1) : x / 2);
                    output.Add((ushort)Sample(rows[colour], row, col));
                }
            }
            // 留下每個顏色向量的最後 2 列作為下一群組的前導。
            for (var c = 0; c < 3; c++)
            {
                var lines = rows[c];
                rows[c] = [lines[^2], lines[^1]];
            }
        }
        return output.ToArray();
    }
}
