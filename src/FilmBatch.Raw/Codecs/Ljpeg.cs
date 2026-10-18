// 移植自 LightCraft crates/raw/src/ljpeg.rs（解碼側：ITU-T T.81 / ISO 10918-1 process 14 lossless JPEG，附錄 H）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
// 對應 Rust 檔案的解碼部分；encoder（測試用）在 tests/FilmBatch.Raw.Tests/Support/LjpegEncoder.cs。

namespace FilmBatch.Raw.Codecs;

/// <summary>
/// Lossless JPEG（「LJ92」）：DNG、Canon CR2、Sony lossless ARW 使用的 T.81 process 14 解碼器。
/// 七個預測器、2–16 bit、1–4 個交錯元件（1×1 取樣）、點變換、restart interval、SSSS=16 差值；
/// selection value 8 為 Hasselblad 3FR/FFF 的編碼（見 <see cref="Ljpeg.DecodeHasselblad"/>）。
/// </summary>
internal static partial class Ljpeg
{
    private const int Lookup = 10;

    /// <summary>解碼後的 lossless-JPEG 框（Rust <c>ljpeg::Frame</c>）。</summary>
    public sealed class Frame
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int Components { get; init; }
        public byte Precision { get; init; }
        public byte Predictor { get; init; }
        public byte PointTransform { get; init; }

        /// <summary><c>width × height × components</c> 交錯樣本。</summary>
        public ushort[] Data { get; init; } = [];
    }

    private static RawException Err(string s) => RawException.Corrupt($"lossless JPEG: {s}");

    /// <summary>熵編碼位元來源介面（首位元為最高位元）。</summary>
    private interface IBits
    {
        /// <summary>不消耗地看接下來 <paramref name="n"/>（1..32）位元；結尾之後補 0。</summary>
        uint Peek(int n);
        void Consume(int n);
        uint Get(int n);
    }

    /// <summary>Huffman 解碼表（T.81 Annex C「BITS」/「HUFFVAL」）。</summary>
    private sealed class Huffman
    {
        /// <summary><see cref="Lookup"/> 位元 peek 用：（碼長, 符號）；碼長 0 = 走慢路徑。</summary>
        private readonly (byte Len, byte Sym)[] _fast;
        private readonly int[] _maxcode = new int[18];
        private readonly int[] _valptr = new int[17];
        private readonly int[] _mincode = new int[17];
        private readonly byte[] _values;

        private Huffman((byte, byte)[] fast, int[] maxcode, int[] valptr, int[] mincode, byte[] values)
        {
            _fast = fast;
            _maxcode = maxcode;
            _valptr = valptr;
            _mincode = mincode;
            _values = values;
        }

        /// <summary>由 16 個碼長計數與符號清單建立。</summary>
        public static Huffman Create(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
        {
            if (counts.Length != 16)
                throw Err("bad Huffman table");
            var total = 0;
            foreach (var c in counts)
                total += c;
            if (total > values.Length || total > 256 || total == 0)
                throw Err("bad Huffman table");
            var maxcode = new int[18];
            var valptr = new int[17];
            var mincode = new int[17];
            var fast = new (byte, byte)[1 << Lookup];
            var code = 0;
            var k = 0;
            for (var len = 1; len <= 16; len++)
            {
                var n = counts[len - 1];
                valptr[len] = k;
                mincode[len] = code;
                for (var i = 0; i < n; i++)
                {
                    if (len <= Lookup)
                    {
                        var shift = Lookup - len;
                        var b = code << shift;
                        for (var j = 0; j < 1 << shift; j++)
                        {
                            var slot = b + j;
                            if ((uint)slot < (uint)fast.Length)
                                fast[slot] = ((byte)len, values[k]);
                        }
                    }
                    code += 1;
                    k += 1;
                }
                maxcode[len] = n > 0 ? code - 1 : -1;
                if (code > 1 << len)
                    throw Err("over-subscribed Huffman table");
                code <<= 1;
            }
            maxcode[17] = int.MaxValue;
            return new Huffman(fast, maxcode, valptr, mincode, values[..total].ToArray());
        }

        public byte Decode<T>(ref T br) where T : struct, IBits, allows ref struct
        {
            var peek = br.Peek(Lookup);
            var (len, sym) = _fast[peek];
            if (len > 0)
            {
                br.Consume(len);
                return sym;
            }
            var code = (int)br.Peek(Lookup + 1);
            var l = Lookup + 1;
            while (l <= 16 && code > _maxcode[l])
            {
                l += 1;
                code = (int)br.Peek(l);
            }
            if (l > 16)
                throw Err("invalid Huffman code");
            br.Consume(l);
            var idx = _valptr[l] + code - _mincode[l];
            if ((uint)idx >= (uint)_values.Length)
                throw Err("invalid Huffman code");
            return _values[idx];
        }
    }

    /// <summary>MSB-first 位元讀取器，處理 <c>FF 00</c> stuffing；遇到 marker 後餵 0（Rust <c>ljpeg::BitReader</c>）。</summary>
    private ref struct BitReader : IBits
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private ulong _buf;
        private int _bits;

        /// <summary>遇到的 marker（<c>FF xx</c>，xx ≠ 0）。</summary>
        public byte? Marker;
        /// <summary>餵過資料結尾的位元數（損毀偵測用）。</summary>
        public int Overrun;


        /// <summary>緩衝中尚未消耗的位元數（Rust 的 <c>br.bits</c>）。</summary>
        public readonly int BitsLeft => _bits;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
            _buf = 0;
            _bits = 0;
            Marker = null;
            Overrun = 0;
        }

        private void Fill()
        {
            while (_bits <= 56)
            {
                byte b;
                if (Marker.HasValue || _pos >= _data.Length)
                {
                    Overrun += 8;
                    b = 0;
                }
                else
                {
                    b = _data[_pos];
                    if (b == 0xff)
                    {
                        var next = _pos + 1 < _data.Length ? _data[_pos + 1] : (byte)0;
                        if (next == 0)
                        {
                            _pos += 2;
                            b = 0xff;
                        }
                        else
                        {
                            Marker = next;
                            Overrun += 8;
                            b = 0;
                        }
                    }
                    else
                    {
                        _pos += 1;
                    }
                }
                _buf |= (ulong)b << (56 - _bits);
                _bits += 8;
            }
        }

        public uint Peek(int n)
        {
            if (_bits < n)
                Fill();
            return (uint)(_buf >> (64 - n));
        }

        public void Consume(int n)
        {
            _buf <<= n;
            _bits -= n;
        }

        public uint Get(int n)
        {
            if (n == 0)
                return 0;
            var v = Peek(n);
            Consume(n);
            return v;
        }

        /// <summary>捨棄緩衝位元並跳到下一個 <c>RSTn</c> marker 之後；找不到時回傳 false。</summary>
        public bool Restart()
        {
            _buf = 0;
            _bits = 0;
            Marker = null;
            Overrun = 0;
            while (_pos + 1 < _data.Length)
            {
                if (_data[_pos] == 0xff && _data[_pos + 1] is >= 0xd0 and <= 0xd7)
                {
                    _pos += 2;
                    return true;
                }
                _pos += 1;
            }
            return false;
        }

        /// <summary>目前已消耗的位元組數（緩衝有位元時為近似值）。</summary>
        public readonly int Position => _pos;
    }

    /// <summary>Hasselblad 熵資料的位元來源：32-bit 小端序字，各從最高位元讀起，無位元組 stuffing（每個位元組都是資料）。</summary>
    private ref struct WordBits : IBits
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private ulong _buf;
        private int _bits;

        /// <summary>在最後一個完整字之後餵入的 0 位元數（<see cref="Overrun"/> &gt; <see cref="BitsLeft"/> 時表示真正吃到）。</summary>
        public int Overrun;

        public readonly int BitsLeft => _bits;

        public WordBits(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
            _buf = 0;
            _bits = 0;
            Overrun = 0;
        }

        private void Fill()
        {
            while (_bits <= 32)
            {
                uint word;
                if (_pos + 4 <= _data.Length)
                {
                    word = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
                    _pos += 4;
                }
                else
                {
                    Overrun = (int)Math.Min(int.MaxValue, (long)Overrun + 32);
                    word = 0;
                }
                _buf |= (ulong)word << (32 - _bits);
                _bits += 32;
            }
        }

        public uint Peek(int n)
        {
            if (_bits < n)
                Fill();
            return (uint)(_buf >> (64 - n));
        }

        public void Consume(int n)
        {
            _buf <<= n;
            _bits -= n;
        }

        public uint Get(int n)
        {
            if (n == 0)
                return 0;
            var v = Peek(n);
            Consume(n);
            return v;
        }
    }

    /// <summary>類別 <paramref name="ssss"/> 的差值（T.81 Table H.2 / F.12 EXTEND）。</summary>
    private static int DiffValue<T>(ref T br, int ssss) where T : struct, IBits, allows ref struct
    {
        switch (ssss)
        {
            case 0:
                return 0;
            case 16:
                return 32768;
            default:
            {
                var s = Math.Min(ssss, 16);
                var v = (int)br.Get(s);
                return v < 1 << (s - 1) ? v - (1 << s) + 1 : v;
            }
        }
    }

    private sealed class Header
    {
        public byte Precision;
        public int Height;
        public int Width;
        public List<byte> Comps = [];
        public List<byte> Sampling = [];
        public Huffman?[] Tables = new Huffman?[4];
        public List<int> TableFor = [];
        public byte Predictor = 1;
        public byte Pt;
        public int Restart;
        public int ScanStart;
    }

    private static int Be16(ReadOnlySpan<byte> d, int i)
    {
        if (i < 0 || i + 2 > d.Length)
            throw Err("truncated header");
        return (d[i] << 8) | d[i + 1];
    }

    private static Header ParseHeader(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xff || d[1] != 0xd8)
            throw Err("missing SOI");
        var h = new Header();
        var i = 2;
        var haveSof = false;
        while (true)
        {
            // 跳過填充位元組／雜散資料到下一個 marker
            while (i < d.Length && d[i] != 0xff)
                i += 1;
            while (i < d.Length && d[i] == 0xff)
                i += 1;
            if (i >= d.Length)
                throw Err("no scan");
            var m = d[i];
            i += 1;
            if (m == 0xd8 || m is >= 0xd0 and <= 0xd7)
                continue;
            if (m == 0xd9)
                throw Err("EOI before scan");
            var len = Be16(d, i);
            if (len < 2 || i + len > d.Length)
                throw Err("truncated segment");
            var seg = d.Slice(i + 2, len - 2);
            switch (m)
            {
                case 0xc3:
                {
                    if (seg.Length < 6)
                        throw Err("short SOF3");
                    h.Precision = seg[0];
                    h.Height = (seg[1] << 8) | seg[2];
                    h.Width = (seg[3] << 8) | seg[4];
                    var n = seg[5];
                    if (n == 0 || n > 4 || seg.Length < 6 + 3 * n)
                        throw Err("bad component count");
                    for (var c = 0; c < n; c++)
                    {
                        var id = seg[6 + 3 * c];
                        var samp = seg[7 + 3 * c];
                        if (samp >> 4 is < 1 or > 4 || (samp & 15) is < 1 or > 4)
                            throw Err("invalid sampling factors");
                        h.Comps.Add(id);
                        h.Sampling.Add(samp);
                    }
                    if (h.Precision is < 2 or > 16)
                        throw Err("bad precision");
                    haveSof = true;
                    break;
                }
                case >= 0xc0 and <= 0xc2 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf:
                    throw RawException.Unsupported($"JPEG process SOF{m - 0xc0:X} (not lossless Huffman)");
                case 0xc4:
                {
                    var j = 0;
                    while (j + 17 <= seg.Length)
                    {
                        var cls = seg[j] >> 4;
                        var id = seg[j] & 15;
                        var counts = seg.Slice(j + 1, 16);
                        var n2 = 0;
                        foreach (var c in counts)
                            n2 += c;
                        if (j + 17 + n2 > seg.Length)
                            throw Err("truncated DHT");
                        var vals = seg.Slice(j + 17, n2);
                        if (cls == 0 && id < 4)
                            h.Tables[id] = Huffman.Create(counts, vals);
                        j += 17 + n2;
                    }
                    break;
                }
                case 0xdd:
                    h.Restart = Be16(seg, 0);
                    break;
                case 0xda:
                {
                    if (!haveSof)
                        throw Err("SOS before SOF3");
                    if (seg.Length < 1)
                        throw Err("short SOS");
                    var ns = seg[0];
                    if (ns != h.Comps.Count || seg.Length < 1 + 2 * ns + 3)
                        throw RawException.Unsupported("lossless JPEG with multiple scans");
                    for (var c = 0; c < ns; c++)
                    {
                        var cid = seg[1 + 2 * c];
                        var pos = h.Comps.IndexOf(cid);
                        if (pos < 0)
                            throw Err("unknown scan component");
                        if (pos != c)
                            throw Err("scan component order");
                        var td = seg[2 + 2 * c] >> 4;
                        if (td > 3 || h.Tables[td] is null)
                            throw Err("missing Huffman table");
                        h.TableFor.Add(td);
                    }
                    h.Predictor = seg[1 + 2 * ns];
                    h.Pt = (byte)(seg[3 + 2 * ns] & 15);
                    if (h.Predictor > 8)
                        throw RawException.Unsupported(
                            $"lossless JPEG scan with predictor selection value {h.Predictor} (T.81 defines 0 to 7, Hasselblad uses 8)");
                    if (h.Pt >= h.Precision)
                        throw Err("bad point transform");
                    h.ScanStart = i + len;
                    return h;
                }
            }
            i += len;
        }
    }

    /// <summary>只解析框頭：(width, height, components, precision)。</summary>
    public static (int Width, int Height, int Components, byte Precision) FrameInfo(ReadOnlySpan<byte> d)
    {
        var h = ParseHeader(d);
        CheckFullResolution(h);
        if (h.Predictor == 8)
            CheckHasselblad(h);
        return (h.Width, h.Height, h.Comps.Count, h.Precision);
    }

    private static void CheckFullResolution(Header h)
    {
        foreach (var s in h.Sampling)
            if (s != 0x11)
                throw RawException.Unsupported("lossless JPEG with subsampled components");
    }

    /// <summary>實際量到的 selection-value-8 掃描都符合此佈局；其他佈局不假定遵循該規則。</summary>
    private static void CheckHasselblad(Header h)
    {
        if (h.Comps.Count != 1 || h.Precision != 16 || h.Pt != 0 || h.Restart != 0 || h.Width == 0 || h.Width % 2 != 0)
            throw RawException.Unsupported(
                $"lossless JPEG with selection value 8 and {h.Comps.Count} components, {h.Precision} bits, point transform {h.Pt}, restart interval {h.Restart}, width {h.Width}");
    }

    /// <summary>Hasselblad selection value 8 的 lossless JPEG（3FR 與 FFF raw）。規則由 14 個樣本檔以 ljpeg-explorer 量得。</summary>
    public static Frame DecodeHasselblad(ReadOnlySpan<byte> d, int maxSamples)
    {
        var h = ParseHeader(d);
        if (h.Predictor != 8)
            throw Err("not a selection value 8 scan");
        return HasselbladFrame(d, h, maxSamples);
    }

    private static Frame HasselbladFrame(ReadOnlySpan<byte> d, Header h, int maxSamples)
    {
        CheckHasselblad(h);
        var (w, ht) = (h.Width, h.Height);
        var total = (long)w * ht;
        if (total > maxSamples)
            throw RawException.Limit("lossless JPEG frame larger than expected");
        var entropy = d[h.ScanStart..];
        if (((long)entropy.Length + 64) * 8 < total)
            throw Err("entropy data too short for frame");
        var table = h.TableFor.Count > 0 && h.TableFor[0] < 4 ? h.Tables[h.TableFor[0]] : null;
        if (table is null)
            throw Err("scan uses an undefined Huffman table");
        var outArr = new ushort[total];
        var br = new WordBits(entropy);
        var start = 1u << (h.Precision - 1);
        var outSpan = outArr.AsSpan();
        for (var row = 0; row < ht; row++)
        {
            var rowSpan = outSpan.Slice(row * w, w);
            var last0 = start;
            var last1 = start;
            // 寬度為偶數（已檢查），每對樣本：兩個 Huffman 類別先來，再來兩個差值
            for (var pair = 0; pair < w; pair += 2)
            {
                var s0 = table.Decode(ref br);
                var s1 = table.Decode(ref br);
                var k = 0;
                foreach (var s in new[] { s0, s1 })
                {
                    int diff;
                    switch (s)
                    {
                        case <= 15:
                            diff = DiffValue(ref br, s);
                            break;
                        case 16:
                            br.Get(16);
                            diff = -32768;
                            break;
                        default:
                            throw Err("invalid difference category");
                    }
                    var v = ((k == 0 ? last0 : last1) + diff) & 0xffff;
                    if (k == 0)
                        last0 = (uint)v;
                    else
                        last1 = (uint)v;
                    rowSpan[pair + k] = (ushort)v;
                    k++;
                }
            }
            // 超過最後一個完整字之後消耗的位元
            if (br.Overrun > br.BitsLeft)
                throw Err("entropy data exhausted");
        }
        return new Frame { Width = w, Height = ht, Components = 1, Precision = h.Precision, Predictor = 8, PointTransform = 0, Data = outArr };
    }

    /// <summary>解碼一個 lossless JPEG 流。<paramref name="maxSamples"/> 限制配置大小（惡意檔頭）。</summary>
    public static Frame Decode(ReadOnlySpan<byte> d, int maxSamples)
    {
        var h = ParseHeader(d);
        CheckFullResolution(h);
        if (h.Predictor == 8)
            return HasselbladFrame(d, h, maxSamples);
        var (w, ht, nc) = (h.Width, h.Height, h.Comps.Count);
        if (w == 0 || ht == 0)
            throw Err("zero frame size");
        var total = (long)w * ht * nc;
        if (total > maxSamples)
            throw RawException.Limit("lossless JPEG frame larger than expected");
        // 熵資料至少要有 ~每樣本 1 位元；過小的檔頭提早拒絕
        var entropy = d[h.ScanStart..];
        if (((long)entropy.Length + 64) * 8 < total)
            throw Err("entropy data too short for frame");
        var tables = new Huffman[h.TableFor.Count];
        for (var i = 0; i < tables.Length; i++)
            tables[i] = h.TableFor[i] < 4 && h.Tables[h.TableFor[i]] is { } t ? t : throw Err("scan uses an undefined Huffman table");
        var outArr = new ushort[total];
        var br = new BitReader(entropy);
        var init = 1 << (h.Precision - h.Pt - 1);
        const int mask = 0xffff;
        var rowLen = w * nc;
        var pred = h.Predictor;
        var restartLeft = h.Restart;
        var firstLine = true; // 影像首列或 restart interval 首列
        var resetPending = true;
        var outSpan = outArr.AsSpan();
        for (var y = 0; y < ht; y++)
        {
            var cur = outSpan.Slice(y * rowLen, rowLen);
            ReadOnlySpan<ushort> prev = y > 0 ? outSpan.Slice((y - 1) * rowLen, rowLen) : [];
            for (var x = 0; x < w; x++)
            {
                if (h.Restart > 0)
                {
                    if (restartLeft == 0)
                    {
                        if (!br.Restart())
                            throw Err("missing restart marker");
                        restartLeft = h.Restart;
                        firstLine = true;
                        resetPending = true;
                    }
                    restartLeft -= 1;
                }
                for (var c = 0; c < nc; c++)
                {
                    var i = x * nc + c;
                    int p;
                    if (resetPending)
                    {
                        p = init;
                    }
                    else if (firstLine)
                    {
                        p = x == 0 ? (i < prev.Length ? prev[i] : init) : cur[i - nc];
                    }
                    else if (x == 0 || pred == 0)
                    {
                        p = pred == 0 ? 0 : prev[i];
                    }
                    else
                    {
                        var ra = (int)cur[i - nc];
                        var rb = (int)prev[i];
                        var rc = (int)prev[i - nc];
                        p = pred switch
                        {
                            1 => ra,
                            2 => rb,
                            3 => rc,
                            4 => ra + rb - rc,
                            5 => ra + ((rb - rc) >> 1),
                            6 => rb + ((ra - rc) >> 1),
                            _ => (ra + rb) >> 1,
                        };
                    }
                    var ssss = tables[c].Decode(ref br);
                    var diff = DiffValue(ref br, ssss);
                    cur[i] = (ushort)((p + diff) & mask);
                }
                resetPending = false;
            }
            firstLine = false;
            if (br.Overrun > 64 * 8)
                throw Err("entropy data exhausted");
        }
        if (h.Pt > 0)
        {
            for (var i = 0; i < outArr.Length; i++)
                outArr[i] <<= h.Pt;
        }
        return new Frame { Width = w, Height = ht, Components = nc, Precision = h.Precision, Predictor = pred, PointTransform = h.Pt, Data = outArr };
    }
}
