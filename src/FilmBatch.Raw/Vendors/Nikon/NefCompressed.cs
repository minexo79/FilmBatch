// 移植自 LightCraft crates/raw/src/vendor/nefc.rs（Nikon Huffman 壓縮：無失真／失真曲線／split 後固定碼率段）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Nikon;

/// <summary>Nikon 壓縮值域對映（lightcraft <c>nefc::Encoding</c>）：無失真直接取樣，失真經曲線查表。</summary>
internal abstract record NefEncoding;

/// <summary>解碼值即樣本（<c>0..2^bits</c>）。</summary>
internal sealed record NefLossless : NefEncoding;

/// <summary>解碼值索引此曲線得到樣本。</summary>
internal sealed record NefLossy(ushort[] Curve) : NefEncoding;

/// <summary>maker note <c>0x0096</c>（NEF 線性化表）解析結果（lightcraft <c>nefc::DecodeTable</c>）。</summary>
internal sealed class NefDecodeTable
{
    public required NefEncoding Encoding { get; init; }

    /// <summary>預測器種子［列奇偶］［欄］。</summary>
    public int[,] Seeds { get; init; } = new int[2, 2];

    /// <summary>固定碼率段首列；0 表示整條皆 Huffman 編碼。</summary>
    public int Split { get; init; }
}

/// <summary>Nikon Huffman 壓縮 strip 解碼（TIFF compression 34713）。無失真／失真共用同一預測器與差值編碼。</summary>
internal static class NefCompressed
{
    /// <summary>從未在樣本中出現的碼字所對應符號（解碼器直接拒絕）。</summary>
    private const byte Unseen = byte.MaxValue;

    private sealed record HuffmanTable(byte[] Counts, byte[] Symbols);

    private static readonly HuffmanTable Lossless12 = new([0, 1, 4, 2, 3, 1, 2], [5, 4, 6, 3, 7, 2, 8, 1, 9, 0, 10, 11, 12]);
    private static readonly HuffmanTable Lossless14 = new([0, 1, 4, 2, 2, 3, 1, 2], [7, 6, 8, 5, 9, 4, 10, 3, 11, 12, 2, 0, 1, 13, 14]);
    private static readonly HuffmanTable Lossy12 = new([0, 1, 5, 1, 1, 1, 1, 1, 1], [5, 4, 3, 6, 2, 7, 1, 0, 8, 9, Unseen, 10]);
    private static readonly HuffmanTable Lossy14 = new([0, 1, 4, 3, 1, 1, 1, 1, 1], [5, 6, 4, 7, 8, 3, 9, 2, 1, 0, 10, 11, 12]);

    /// <summary>每符號（靠左對齊 12 位元碼，長度），供 <see cref="PefHuffman"/> 建表（lightcraft <c>Table::codes</c>）。</summary>
    internal static (ushort Code, byte Length)[] HuffmanCodes(NefEncoding encoding, uint bits)
    {
        var t = (encoding, bits) switch
        {
            (NefLossless, 12) => Lossless12,
            (NefLossless, _) => Lossless14,
            (NefLossy, 12) => Lossy12,
            (NefLossy, _) => Lossy14,
            _ => throw RawException.Unsupported("Nikon compressed NEF encoding"),
        };
        var codes = new (ushort Code, byte Length)[17];
        uint code = 0;
        var si = 0;
        for (var i = 0; i < t.Counts.Length; i++)
        {
            var len = i + 1;
            for (var j = 0; j < t.Counts[i]; j++)
            {
                if (si < t.Symbols.Length)
                {
                    var s = t.Symbols[si++];
                    if (s < codes.Length)
                        codes[s] = ((ushort)(code << (12 - len)), (byte)len);
                }
                code++;
            }
            code <<= 1;
        }
        return codes;
    }

    internal static NefDecodeTable ParseTable(ReadOnlySpan<byte> t, ByteOrder order, uint bits)
    {
        if (bits != 12 && bits != 14)
            throw RawException.Unsupported($"Nikon compressed NEF with {bits}-bit samples");
        static ushort? U16At(ReadOnlySpan<byte> buf, ByteOrder bo, int o) => bo.ReadU16(buf, o);
        static RawException Short(ReadOnlySpan<byte> buf) => RawException.Corrupt($"NEF: linearization table too short ({buf.Length} bytes)");
        var v0 = t.Length > 0 ? t[0] : throw Short(t);
        var v1 = t.Length > 1 ? t[1] : throw Short(t);
        var seeds = new int[2, 2];
        for (var i = 0; i < 4; i++)
            seeds[i / 2, i % 2] = U16At(t, order, 2 + 2 * i) ?? throw Short(t);
        var split = 0;
        NefEncoding encoding;
        if (v0 == 0x46)
        {
            encoding = new NefLossless();
        }
        else if (v0 == 0x44 && (v1 == 0x10 || v1 == 0x20 || v1 == 0x40))
        {
            var n = (int)(U16At(t, order, 10) ?? throw Short(t));
            var points = new ushort[n];
            for (var i = 0; i < n; i++)
                points[i] = U16At(t, order, 12 + 2 * i) ?? throw Short(t);
            if (n < 2)
                throw RawException.Corrupt($"NEF: linearization curve of {n} points");
            if (v1 == 0x10)
            {
                encoding = new NefLossy(points);
            }
            else
            {
                split = U16At(t, order, 562) ?? 0;
                // type 2：n 點覆蓋 range 個碼，每 range / (n - 1) 一點；曲線首區間恰為此步距（暗部恆等），
                // 版本決定跨度：0x20 覆蓋全部 2^bits 碼，0x40 只覆蓋前四分之一。
                var range = v1 == 0x40 ? 1 << (int)(bits - 2) : 1 << (int)bits;
                if (n - 1 > range || range % (n - 1) != 0)
                    throw RawException.Unsupported($"NEF: {n}-point lossy curve for {bits}-bit data");
                var step = range / (n - 1);
                var curve = new ushort[range + 1];
                var c = 0;
                for (var i = 0; i + 1 < n; i++)
                {
                    uint a = points[i], b = points[i + 1];
                    var s = (uint)step;
                    for (uint k = 0; k < (uint)step; k++)
                        curve[c++] = (ushort)((a * (s - k) + b * k) / s);
                }
                curve[c] = points[n - 1];
                encoding = new NefLossy(curve);
            }
        }
        else
        {
            throw RawException.Unsupported($"Nikon compressed NEF version 0x{v0:x2} 0x{v1:x2}");
        }
        return new NefDecodeTable { Encoding = encoding, Seeds = seeds, Split = split };
    }

    /// <summary>固定碼率段差值類別前綴碼：（前綴位元，前綴長度，類別 n）。</summary>
    private static readonly (uint Prefix, uint Length, uint Category)[] Fixed12 =
    [
        (0b00, 2, 9), (0b010, 3, 10), (0b011, 3, 8), (0b100, 3, 7), (0b101, 3, 6),
        (0b110, 3, 5), (0b1110, 4, 4), (0b11110, 5, 3), (0b111110, 6, 2),
        (0b1111110, 7, 1), (0b11111110, 8, 0),
    ];

    private static readonly (uint Prefix, uint Length, uint Category)[] Fixed14 =
    [
        (0b00, 2, 8), (0b010, 3, 12), (0b011, 3, 11), (0b100, 3, 10), (0b101, 3, 9),
        (0b110, 3, 7), (0b1110, 4, 6), (0b11110, 5, 5), (0b111110, 6, 4),
        (0b1111110, 7, 3), (0b11111110, 8, 2), (0b111111110, 9, 1), (0b1111111110, 10, 0),
    ];

    /// <summary>字（<c>bits - 4</c> 位元）到位差值的去量化表；null 為未使用的全 1 字。</summary>
    internal static int?[] FixedWordTable(uint bits)
    {
        var (w, codes) = bits == 12 ? (8, Fixed12) : (10, Fixed14);
        if (bits != 12 && bits != 14)
            throw RawException.Unsupported($"Nikon compressed NEF with {bits}-bit samples");
        var lut = new int?[1 << w];
        foreach (var (prefix, len, n) in codes)
        {
            var kept = (int)(w - len);
            for (uint m = 0; m < 1u << kept; m++)
            {
                var e = (int)n - kept;
                if (e < 0) e = 0;
                var ex = (int)(m << e);
                var half = (1 << e) >> 1;
                int d;
                if (n == 0) d = 0;
                else if (ex >= 1 << ((int)n - 1)) d = ex + half;
                else if (e > 0) d = ex + half - (1 << (int)n);
                else d = ex - ((1 << (int)n) - 1);
                lut[((int)prefix << kept) | (int)m] = d;
            }
        }
        return lut;
    }

    /// <summary>解整條 strip（<c>w × h</c>）。</summary>
    internal static ushort[] Decode(ReadOnlySpan<byte> src, int w, int h, uint bits, NefDecodeTable table) =>
        DecodeRows(src, w, h, h, bits, table);

    /// <summary>
    /// strip 中 <c>w × h</c> 的前 <c>rows</c> 列。串流連續，檔頭探測只解遮光欄檢測需要的列；
    /// 尺寸與截斷檢查仍覆蓋整圖，「lossy after split」尾段規則（與下游損毀）只在解整圖時可見。
    /// </summary>
    internal static ushort[] DecodeRows(ReadOnlySpan<byte> src, int w, int h, int rows, uint bits, NefDecodeTable table)
    {
        if (bits != 12 && bits != 14)
            throw RawException.Unsupported($"Nikon compressed NEF with {bits}-bit samples");
        if (w <= 0 || h <= 0)
            throw RawException.Limit("NEF image size");
        var whole = (long)w * h;
        if (whole > RawLimits.MaxSamples)
            throw RawException.Limit("NEF image size");
        // 每個碼字至少 2 位元：裝不下整圖的 strip 必被截斷（同時以檔大小約束配置）。
        if (whole / 4 > src.Length)
            throw RawException.Corrupt($"NEF: {src.Length} bytes of compressed data for {w}x{h} pixels");
        rows = Math.Min(rows, h);
        var n = w * rows;
        var huff = PefHuffman.Create(HuffmanCodes(table.Encoding, bits));
        var lut = table.Encoding is NefLossy lossy ? lossy.Curve : [];
        var max = lut.Length == 0 ? (1 << (int)bits) - 1 : lut.Length - 1;
        if (table.Split != 0 && table.Split >= h)
            throw RawException.Corrupt($"NEF: split row {table.Split} outside the {h} rows");
        var fixedLut = table.Split != 0 ? FixedWordTable(bits) : [];
        var word = (int)bits - 4;
        var output = new ushort[n];
        var stream = new MsbBits(src);
        var vpred = (int[,])table.Seeds.Clone();
        var clipped = 0;
        for (var y = 0; y < rows; y++)
        {
            var hpred0 = 0;
            var hpred1 = 0;
            for (var x = 0; x < w; x++)
            {
                int d;
                if (table.Split != 0 && y >= table.Split)
                {
                    var code = (int)stream.Get(word);
                    var f = code < fixedLut.Length ? fixedLut[code] : null;
                    if (f is null)
                        throw RawException.Corrupt($"NEF: invalid fixed-rate word at row {y}");
                    d = f.Value;
                }
                else
                {
                    var diff = huff.Diff(ref stream);
                    if (diff is null)
                        throw RawException.Corrupt($"NEF: invalid Huffman code at row {y}");
                    d = diff.Value;
                }
                // 損毀串流的連續大差值會把預測器推過 i32（issue #1515）：改為錯誤而非 panic／繞回。
                int p;
                if (x < 2)
                {
                    var s = (long)vpred[y & 1, x] + d;
                    if (s < int.MinValue || s > int.MaxValue)
                        throw RawException.Corrupt($"NEF: predictor out of range at row {y}");
                    vpred[y & 1, x] = (int)s;
                    if ((x & 1) == 0) hpred0 = (int)s; else hpred1 = (int)s;
                    p = (int)s;
                }
                else
                {
                    var hp = (x & 1) == 0 ? hpred0 : hpred1;
                    var s = (long)hp + d;
                    if (s < int.MinValue || s > int.MaxValue)
                        throw RawException.Corrupt($"NEF: predictor out of range at row {y}");
                    if ((x & 1) == 0) hpred0 = (int)s; else hpred1 = (int)s;
                    p = (int)s;
                }
                var v = Math.Clamp(p, 0, max);
                if (v != p && (table.Split == 0 || y < table.Split))
                    clipped++;
                // 曲線外索引（損毀串流）取 0，如上游 get().unwrap_or(0)，絕不拋出索引例外
                output[y * w + x] = lut.Length == 0 ? (ushort)v : (v >= 0 && v < lut.Length ? lut[v] : (ushort)0);
            }
            // 相機以補位結束 strip：讀過尾端 64 位元以上表示截斷或損毀。
            if (stream.ConsumedBits > (long)src.Length * 8 + 64)
                throw RawException.Corrupt($"NEF: compressed data ends at row {y} of {h}");
        }
        if (table.Split != 0 && rows == h)
        {
            // 固定碼率段必須填滿 strip 剩餘部分：整列的 w 字加上 0..7 位元補位。
            // 多存幾列的 strip 至多容許 4 列；否則拒絕而非解錯。
            long total = (long)src.Length * 8;
            long? left = stream.ConsumedBits <= total ? total - stream.ConsumedBits : null;
            var rowBits = (long)w * word;
            if (left is null || rowBits <= 0 || !(left % rowBits < 8 && left / rowBits <= 4))
                throw RawException.Unsupported($"Nikon \"lossy after split\" NEF whose fixed-rate section (from row {table.Split}) does not end within the last byte");
        }
        // 錯表／損毀串流很快漂出範圍；正常檔案只在少數邊緣像素裁剪。
        if (clipped > n / 1000 + 16)
            throw RawException.Corrupt($"NEF: {clipped} decoded values out of range");
        return output;
    }
}
