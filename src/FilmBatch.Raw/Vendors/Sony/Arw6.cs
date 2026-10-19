// 移植自 LightCraft crates/raw/src/vendor/arw6.rs（Sony ARW6「Compressed RAW 2」/HQ，TIFF Compression 32766：容器、幾何、色彩與解碼）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
// Clean-room：佈局來自 SMPTE RDD 34（word 0 的 Picture_info）與對 ILCE-7RM6 實檔的 Phase 0 黑箱檢視
// （tile table、group size words、stream headers 與 index entries），每條規則都經位元組級重序列化驗證。
// 格式描述見 LightCraft docs/arw6-compression.md；下方以斜體 *Container*/*Frame geometry* 等指稱其節名。

using FilmBatch.Raw.Codecs;

namespace FilmBatch.Raw.Vendors.Sony;

/// <summary>Sony ARW6（Compression 32766）strip 解碼器。</summary>
internal static class Arw6
{
    private static RawException Corrupt(string why) => RawException.Corrupt($"ARW6: {why}");

    private static RawException Unsupported(string why) => RawException.Unsupported($"ARW6 {why}");

    private static int? U16Be(ReadOnlySpan<byte> b, int at)
    {
        if (at < 0 || at + 2 > b.Length)
            return null;
        return (b[at] << 8) | b[at + 1];
    }

    private static int? U24Be(ReadOnlySpan<byte> b, int at)
    {
        if (at < 0 || at + 3 > b.Length)
            return null;
        return (b[at] << 16) | (b[at + 1] << 8) | b[at + 2];
    }

    private static int? U32Le(ReadOnlySpan<byte> b, int at)
    {
        if (at < 0 || at + 4 > b.Length)
            return null;
        return b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | ((int)b[at + 3] << 24);
    }

    /// <summary>strip 中的一個 tile：位元組從 <see cref="Offset"/> 到下個 tile 的 offset（最後一個：strip 末端）。</summary>
    public sealed class TileEntry
    {
        public int Offset { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
    }

    /// <summary>一個 strip 最多允許的 tile 數（實檔為 1 或 4）。</summary>
    private const int MaxTiles = 16;

    /// <summary>
    /// 解析 strip 開頭的 tile table（檔案結構，*Container*）：<c>u32</c> 數量、<c>u32</c> 0，再來 24-byte 項目
    /// <c>u64</c> offset（相對 strip）、<c>u32</c> x, y, width, height，皆小端序。
    /// </summary>
    public static TileEntry[] ParseTileTable(ReadOnlySpan<byte> strip, int width, int height)
    {
        var count = U32Le(strip, 0) ?? throw Corrupt("truncated tile table");
        if (count == 0 || count > MaxTiles)
            throw Unsupported("tile count outside 1..=16");
        var tiles = new List<TileEntry>(count);
        var area = 0L;
        for (var i = 0; i < count; i++)
        {
            var at = 8 + 24 * i;
            if (at + 24 > strip.Length)
                throw Corrupt("truncated tile table");
            var e = strip.Slice(at, 24);
            var offsetLong = (long)e[0] | ((long)e[1] << 8) | ((long)e[2] << 16) | ((long)e[3] << 24)
                | ((long)e[4] << 32) | ((long)e[5] << 40) | ((long)e[6] << 48) | ((long)e[7] << 56);
            if (offsetLong < 0 || offsetLong >= strip.Length || offsetLong > int.MaxValue)
                throw Corrupt("tile offset outside the strip");
            var offset = (int)offsetLong;
            if (tiles.Count > 0 && offset <= tiles[^1].Offset)
                throw Corrupt("tile offsets do not increase");
            var x = U32Le(e, 8);
            var y = U32Le(e, 12);
            var w = U32Le(e, 16);
            var h = U32Le(e, 20);
            if (x is null || y is null || w is null || h is null)
                throw Corrupt("truncated tile table");
            // 偶數幾何；level-3 頻帶寬 w/16 必須是整數（w/2 為 8 的倍數）
            if (w == 0 || h == 0 || x % 2 != 0 || y % 2 != 0 || w % 2 != 0 || h % 2 != 0 || (w / 2) % 8 != 0)
                throw Unsupported("tile geometry");
            var fits = (long)x.Value + w <= width && (long)y.Value + h <= height;
            if (!fits)
                throw Corrupt("tile outside the image");
            var a = (long)w.Value * h.Value;
            if (a > RawLimits.MaxSamples)
                throw RawException.Limit("ARW6 tile too large");
            area = checked(area + a) > RawLimits.MaxSamples ? throw RawException.Limit("ARW6 tiles too large") : area + a;
            tiles.Add(new TileEntry { Offset = offset, X = x.Value, Y = y.Value, Width = w.Value, Height = h.Value });
        }
        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var (a2, b2) = (tiles[i], tiles[j]);
                if (a2.X < b2.X + b2.Width && b2.X < a2.X + a2.Width && a2.Y < b2.Y + b2.Height && b2.Y < a2.Y + a2.Height)
                    throw Corrupt("tiles overlap");
            }
        }
        // 在影像內且不重疊的 tiles 覆蓋整張影像 ⇔ 面積和等於影像面積
        if ((long)width * height != area)
            throw Corrupt("tiles do not cover the image");
        return [.. tiles];
    }

    /// <summary>tile <paramref name="i"/> 的位元組：從其 offset 到下個 tile 的 offset（最後一個：strip 末端）。</summary>
    public static ReadOnlySpan<byte> TileBytes(ReadOnlySpan<byte> strip, TileEntry[] tiles, int i)
    {
        if ((uint)i >= (uint)tiles.Length)
            return [];
        var start = tiles[i].Offset;
        var end = i + 1 < tiles.Length ? tiles[i + 1].Offset : strip.Length;
        return start <= end && end <= strip.Length ? strip[start..end] : [];
    }

    /// <summary>tile 的 13 個 stream 之一（位置以 tile 起頭算起的 16-byte word 計）。</summary>
    public sealed class StreamRef
    {
        public byte Group { get; init; }
        public byte Comp { get; init; }
        public int Word { get; init; }
        public int SizeWords { get; init; }
        public int IndexWords { get; init; }
        public int DataWords { get; init; }
    }

    public sealed class TileHeader
    {
        public int Hs { get; init; }
        public int Vs { get; init; }

        /// <summary>檔案順序：g0 c0..2, g1 c0..2, g2 c0..2, g3 c0..2, g4 c0。</summary>
        public List<StreamRef> Streams { get; init; } = [];
    }

    /// <summary>tile word 0 中位元位置 <paramref name="pos"/>（MSB-first）起的 <paramref name="n"/> 個位元。</summary>
    private static uint? Field(ReadOnlySpan<byte> w, int pos, int n)
    {
        var v = 0u;
        for (var i = pos; i < pos + n; i++)
        {
            if (i < 0 || i / 8 >= w.Length)
                return null;
            v = (v << 1) | (uint)((w[i / 8] >> (7 - i % 8)) & 1);
        }
        return v;
    }

    /// <summary>每個 group 的 stream 數：g0..g3 每個色彩元件一個，g4 為綠色殘差。</summary>
    private static readonly int[] GroupStreams = [3, 3, 3, 3, 1];

    /// <summary>
    /// 解析 word 0（RDD 34 <c>Picture_info</c>；實檔皆為 HS = tile 寬、VS = tile 高/2、BBD 16、NC 3、NW 3、NP 1000000000b）、
    /// group size words 3..=7 與各 stream 的 header word（*Container*，Phase 0）。
    /// </summary>
    public static TileHeader ParseTileHeader(ReadOnlySpan<byte> tile, int tw, int th)
    {
        if (tile.Length < 16)
            throw Corrupt("tile shorter than its picture header");
        var w0 = tile[..16];
        static int? F(ReadOnlySpan<byte> w, int pos, int n) => Field(w, pos, n) is { } v ? (int)v : null;
        var (hs, vs, bbd, nc, nw, np) = (F(w0, 64, 16), F(w0, 80, 16), F(w0, 102, 6), F(w0, 112, 3), F(w0, 115, 3), F(w0, 118, 10));
        if (hs is null || vs is null || bbd is null || nc is null || nw is null || np is null)
            throw Corrupt("picture header");
        if (hs != tw || vs * 2 != th || bbd != 16 || nc != 3 || nw != 3 || np != 0b10_0000_0000)
            throw Unsupported($"picture header variant (HS {hs}, VS {vs}, BBD {bbd}, NC {nc}, NW {nw}, NP {Convert.ToString(np.Value, 2)})");
        var streams = new List<StreamRef>(13);
        var word = 8;
        for (var group = 0; group < GroupStreams.Length; group++)
        {
            var n = GroupStreams[group];
            if ((4 + group) * 16 > tile.Length)
                throw Corrupt("truncated group table");
            var table = tile.Slice((3 + group) * 16, 16);
            if (table[0] != n)
                throw Unsupported("group stream count");
            for (var comp = 0; comp < n; comp++)
            {
                var sizeWords = U24Be(table, 1 + 3 * comp) ?? throw Corrupt("group table");
                var end = word + sizeWords;
                if ((long)end * 16 > tile.Length || end < word)
                    throw Corrupt("stream outside the tile");
                if (word * 16 + 16 > tile.Length)
                    throw Corrupt("stream outside the tile");
                var hdr = tile.Slice(word * 16, 16);
                var indexWords = U16Be(hdr, 0) ?? throw Corrupt("stream header");
                var dataWords = U24Be(hdr, 2) ?? throw Corrupt("stream header");
                if ((long)indexWords + dataWords + 1 > sizeWords)
                    throw Corrupt("stream header sizes exceed the stream");
                streams.Add(new StreamRef
                {
                    Group = (byte)group,
                    Comp = (byte)comp,
                    Word = word,
                    SizeWords = sizeWords,
                    IndexWords = indexWords,
                    DataWords = dataWords,
                });
                word = end;
            }
        }
        return new TileHeader { Hs = hs.Value, Vs = vs.Value, Streams = streams };
    }

    /// <summary>一個 stream 的半個 TU：tile 中位元組 <see cref="Start"/> 起 <see cref="Len"/> 個位元組，各頻帶的量化指數（g0/g4：單一 nibble 重複）。</summary>
    public sealed class Half
    {
        public int Start { get; init; }
        public int Len { get; init; }
        public uint[] Qi { get; init; } = new uint[3];
    }

    private static uint? Nibbles(ReadOnlySpan<byte> b, int from, int n)
    {
        var a = 0u;
        for (var i = from; i < from + n; i++)
        {
            if (i / 2 < 0 || i / 2 >= b.Length)
                return null;
            a = (a << 4) | (uint)((b[i / 2] >> (4 * (1 - i % 2))) & 15);
        }
        return a;
    }

    /// <summary>
    /// 一個 stream 的 <c>2 * ntu</c> 個 half。每 TU 一個 index entry（檔案結構）：<c>A:16 X B:16 X</c>，
    /// A / B 為 half 0 / 1 的位元組長度，X 為 QI nibble（g0, g4：1 nibble）或三個 nibble HL, LH, HH（g1..g3）；
    /// 資料區依序放各 TU 的 A 位元組再 B 位元組，連續 TU 相接。
    /// </summary>
    public static Half[] StreamHalves(ReadOnlySpan<byte> tile, StreamRef s, int ntu)
    {
        var nx = s.Group == 0 || s.Group == 4 ? 1 : 3;
        var esz = 4 + nx; // bytes：(16 + 4nx + 16 + 4nx) / 8
        var idxStart = checked((s.Word + 1) * 16);
        var idxLen = s.IndexWords * 16;
        if ((long)ntu * esz > idxLen)
            throw Corrupt("index table too small for the tile's TUs");
        var dataStart = idxStart + idxLen;
        if (dataStart > tile.Length)
            throw Corrupt("index table outside the tile");
        var index = tile.Slice(idxStart, idxLen);
        var dataEnd = (long)s.DataWords * 16 + dataStart;
        if (dataEnd > tile.Length)
            throw Corrupt("stream data outside the tile");
        var halves = new List<Half>(2 * Math.Max(0, ntu));
        var pos = dataStart;
        for (var k = 0; k < ntu; k++)
        {
            var e = index.Slice(k * esz, esz);
            RawException Bad() => Corrupt("index entry");
            var a = (int)(Nibbles(e, 0, 4) ?? throw Bad());
            var x1 = Nibbles(e, 4, nx) ?? throw Bad();
            var b = (int)(Nibbles(e, 4 + nx, 4) ?? throw Bad());
            var x2 = Nibbles(e, 8 + nx, nx) ?? throw Bad();
            foreach (var (len, x) in new[] { (a, x1), (b, x2) })
            {
                var end = pos + len;
                if (end < pos || end > dataEnd)
                    throw Corrupt("half outside its stream's data");
                var qi = nx == 1 ? new[] { x, x, x } : new[] { (x >> 8) & 15, (x >> 4) & 15, x & 15 };
                halves.Add(new Half { Start = pos, Len = len, Qi = qi });
                pos = end;
            }
        }
        return [.. halves];
    }

    // ---- 以感光元件為錨的框幾何與 tile 重建（*Frame geometry*）----

    /// <summary><paramref name="vs"/> 個平面列的 half 以感光元件為錨：half <c>n</c> 涵蓋平面列 <c>8n − 6 + s .. 8n + 1 + s</c>，故需 <c>ceil((vs − 2 − s) / 8) + 1</c> 個 half，每 TU 兩個（*Container*）。</summary>
    public static int TuCount(int vs, int s)
    {
        var halves = (Math.Max(0, vs - (2 + s)) + 7) / 8 + 1;
        return (halves + 1) / 2;
    }

    /// <summary>錨偏移 <paramref name="s"/> 對應的 level 1..=3 lifting 相位（Phase 0 參考模型）：FF（s = 0）給 [0, 1, 1]，APS-C 裁切（s = 3）給 [1, 0, 0]。</summary>
    public static int[] Phases(int s)
    {
        var p1 = s % 2;
        var p2 = ((2 + s - p1) / 2) % 2;
        var k3 = DivEuclid((6 + s) % 8 - p1, 2);
        var p3 = RemEuclid(DivEuclid(k3 - p2, 2), 2);
        return [p1, p2, p3];
    }

    private static int DivEuclid(int a, int b)
    {
        var q = a / b;
        return a % b != 0 && (a < 0) != (b < 0) ? q - 1 : q;
    }

    private static int RemEuclid(int a, int b)
    {
        var r = a % b;
        return r < 0 ? r + Math.Abs(b) : r;
    }

    /// <summary>half <paramref name="n"/> 的平面列範圍：<c>8n − 6 + s .. 8n + 2 + s</c>，夾到 <c>0..rows</c>（不相交時為空）。</summary>
    public static (int Start, int End) HalfRows(int n, int s, int rows)
    {
        var b = 8L * n + s;
        var start = (int)Math.Min(Math.Max(b - 6, 0), Math.Max(0, rows));
        var end = (int)Math.Min(Math.Max(b + 2, 0), Math.Max(0, rows));
        return (start, Math.Max(start, end));
    }

    /// <summary>
    /// 第一個 half 的框列數 <c>lines − 2</c>，在 TU 數已知前僅由 g4 的第一個 index entry 讀出
    /// （index table 在 word <c>g4.word + 1</c>，entry 0 的 A 為 big-endian u16；其位元組在 index table 之後開始）。
    /// 殘差線在至少剩 8 位元且讀取未滿 8 條時持續解碼（*Frame geometry*）。
    /// </summary>
    public static int FirstHalfShift(ReadOnlySpan<byte> tile, StreamRef g4, int width2)
    {
        var idx = checked((g4.Word + 1) * 16);
        var data = checked(g4.IndexWords * 16 + idx);
        var a = U16Be(tile, idx) ?? throw Corrupt("index table outside the tile");
        if (data < 0 || data + a > tile.Length)
            throw Corrupt("half outside the tile");
        var bytes = tile.Slice(data, a);
        var total = bytes.Length * 8;
        var bits = new MsbBits(bytes);
        var lines = 0;
        while (lines < 8 && Math.Max(0, total - bits.ConsumedBits) >= 8)
        {
            Llvc.VldDecodeLine(ref bits, total, width2);
            lines += 1;
        }
        // 停止規則之所以成立：一條殘差線至少耗 8 位元——全零線寬 w 耗 1 + ceil(log2(ceil(w/4) + 1)) 位元，w ≥ 253 時 ≥ 8，
        // 實際平面寬度 ≥ 2496。
        return lines - 2 is >= 0 and <= 5 ? lines - 2 : throw Unsupported("first-half row count outside 2..=7");
    }

    /// <summary>一個 half 的頻帶列：平面列 <see cref="T"/> 與其在各層落入的列（<c>split</c>）。</summary>
    private sealed class HalfRowsLayout
    {
        public (int Start, int End) T;
        public int[] L1 = [];
        public int[] H1 = [];
        public int[] L2 = [];
        public int[] H2 = [];
        public int[] L3 = [];
        public int[] H3 = [];
    }

    /// <summary><c>t % 2 == p</c> 的列進 low band 的 <c>(t − p)/2</c>，其餘進 high band 的 <c>(t − (1 − p))/2</c>。</summary>
    private static (int[] Lo, int[] Hi) Split(IReadOnlyList<int> rows, int p)
    {
        var lo = new List<int>();
        var hi = new List<int>();
        foreach (var t in rows)
        {
            if (t % 2 == p)
                lo.Add(Math.Max(0, t - p) / 2);
            else
                hi.Add(Math.Max(0, t - (1 - p)) / 2);
        }
        return ([.. lo], [.. hi]);
    }

    private static HalfRowsLayout HalfLayout(int n, int s, int vs, int[] ph)
    {
        var (ts, te) = HalfRows(n, s, vs);
        var rows = new List<int>(Math.Max(0, te - ts));
        for (var t = ts; t < te; t++)
            rows.Add(t);
        var (l1, h1) = Split(rows, ph[0]);
        var (l2, h2) = Split(l1, ph[1]);
        var (l3, h3) = Split(l2, ph[2]);
        return new HalfRowsLayout { T = (ts, te), L1 = l1, H1 = h1, L2 = l2, H2 = h2, L3 = l3, H3 = h3 };
    }

    /// <summary>一個 half 在 group <paramref name="g"/> 持有的線數：g0 = LL3 列；g1 = HL3+LH3+HH3；g2 = HL2+LH2+HH2；g3 = HL1+LH1+HH1；g4 = 殘差。</summary>
    private static int LineCount(int g, HalfRowsLayout r) => g switch
    {
        0 => r.L3.Length,
        1 => r.L3.Length + 2 * r.H3.Length,
        2 => r.L2.Length + 2 * r.H2.Length,
        3 => r.L1.Length + 2 * r.H1.Length,
        _ => r.T.End - r.T.Start,
    };

    /// <summary>group <paramref name="g"/> 的線寬：level-<c>l</c> 頻帶寬為 <c>(tw/2) &gt;&gt; l</c>（g0, g1：level 3；g2：2；g3：1；g4：整個平面）。</summary>
    private static int LineWidth(int g, int w2) => g switch
    {
        0 or 1 => w2 >> 3,
        2 => w2 >> 2,
        3 => w2 >> 1,
        _ => w2,
    };

    /// <summary>一個 half 與其解出的（仍量化的）線。</summary>
    private readonly struct HalfLines(Half half, int[][] lines)
    {
        public readonly Half Half = half;
        public readonly int[][] Lines = lines;
    }

    /// <summary>列出 stream 的各 half 並 VLD 解碼其應含的線數；線讀過其 half 末端即 <c>Corrupt</c>。各 half 為獨立位元流：平行解碼。</summary>
    private static HalfLines[] DecodeStream(ReadOnlySpan<byte> tile, StreamRef st, int ntu, int w2, HalfRowsLayout[] layouts)
    {
        var halves = StreamHalves(tile, st, ntu);
        var width = LineWidth(st.Group, w2);
        var results = new HalfLines[halves.Length];
        var errs = new Exception?[halves.Length];
        var tileArr = tile.ToArray(); // Parallel.For 不能捕獲 ref struct 的 ReadOnlySpan；複製一次後平行
        Parallel.For(0, halves.Length, n =>
        {
            try
            {
                var half = halves[n];
                var end = half.Start + half.Len;
                if (end < half.Start || end > tileArr.Length)
                    throw Corrupt("half outside the tile");
                var bytes = tileArr.AsSpan(half.Start, half.Len);
                var total = bytes.Length * 8;
                var bits = new MsbBits(bytes);
                var count = n < layouts.Length ? LineCount(st.Group, layouts[n]) : 0;
                var lines = new int[count][];
                for (var i = 0; i < count; i++)
                {
                    lines[i] = Llvc.VldDecodeLine(ref bits, total, width);
                    if (bits.ConsumedBits > total)
                        throw Corrupt("line runs past its half");
                }
                // Phase 0 量測：8 個實檔共 3562 個 half 中，線都結束在 half 最後一個位元組內（len*8 − 8 &lt; consumed ≤ len*8）；
                // 解出更少位元的垃圾即損毀。
                if (count > 0 && bits.ConsumedBits <= Math.Max(0, total - 8))
                    throw Corrupt("half ends early");
                results[n] = new HalfLines(half, lines);
            }
            catch (Exception e) when (e is RawException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                errs[n] = e;
            }
        });
        for (var i = 0; i < errs.Length; i++)
            if (errs[i] is { } e)
                throw e is RawException re ? re : Corrupt("half decode failed");
        return results;
    }

    /// <summary>把 <paramref name="line"/> 去量化後寫入頻帶的 <paramref name="row"/> 列。</summary>
    private static void Put(Llvc.Plane plane, int row, int[] line, Func<int, int> f)
    {
        var start = (long)row * plane.Width;
        if (start < 0 || start + plane.Width > plane.Data.Length)
            throw Corrupt("band row outside its band");
        var dst = plane.Data.AsSpan((int)start, plane.Width);
        var n = Math.Min(dst.Length, line.Length);
        for (var i = 0; i < n; i++)
            dst[i] = f(line[i]);
    }

    /// <summary>把一個 half 的線依序鋪進（頻帶, 列, QI）：*Frame geometry* 的自然夾取指派。</summary>
    private static void Fill((Llvc.Plane Plane, int[] Rows, uint Qi)[] set, int[][] lines)
    {
        var it = 0;
        foreach (var (plane, rows, qi) in set)
        {
            foreach (var r in rows)
            {
                if (it >= lines.Length)
                    throw Corrupt("half holds fewer lines than rows");
                Put(plane, r, lines[it++], q => Llvc.Dequant(q, qi));
            }
        }
    }

    /// <summary>一個色彩元件：從 streams g0..g3（component <paramref name="c"/>）取得十個頻帶，再做 3 層逆轉換。</summary>
    private static Llvc.Plane Component(int c, HalfLines[][] streams, HalfRowsLayout[] layouts, int vs, int w2, int[] ph)
    {
        var (n1l, n1h) = Llvc.BandRows(vs, ph[0]);
        var (n2l, n2h) = Llvc.BandRows(n1l, ph[1]);
        var (n3l, n3h) = Llvc.BandRows(n2l, ph[2]);
        var (w1, w2b, w3) = (w2 >> 1, w2 >> 2, w2 >> 3);
        var b = new Llvc.Bands3
        {
            Ll3 = Llvc.Plane.Zeros(w3, n3l),
            Hl3 = Llvc.Plane.Zeros(w3, n3l),
            Lh3 = Llvc.Plane.Zeros(w3, n3h),
            Hh3 = Llvc.Plane.Zeros(w3, n3h),
            Hl2 = Llvc.Plane.Zeros(w2b, n2l),
            Lh2 = Llvc.Plane.Zeros(w2b, n2h),
            Hh2 = Llvc.Plane.Zeros(w2b, n2h),
            Hl1 = Llvc.Plane.Zeros(w1, n1l),
            Lh1 = Llvc.Plane.Zeros(w1, n1h),
            Hh1 = Llvc.Plane.Zeros(w1, n1h),
        };
        HalfLines Grp(int g, int n)
        {
            var idx = 3 * g + c;
            if (idx >= streams.Length || n >= streams[idx].Length)
                throw Corrupt("missing half");
            return streams[idx][n];
        }
        for (var n = 0; n < layouts.Length; n++)
        {
            var r = layouts[n];
            var h0 = Grp(0, n);
            // LL3：nibble 必須為 0（Phase 0：未見其他值）；線為 DPCM——綠均值（c = 0）從 2048 起，
            // 無基準的色度平面從 0 起（*Entropy coding and dequantisation*）。
            if (h0.Half.Qi[0] != 0)
                throw Unsupported("LL3 quantiser index");
            for (var i = 0; i < r.L3.Length && i < h0.Lines.Length; i++)
            {
                var row = r.L3[i];
                var line = h0.Lines[i];
                var acc = c == 0 ? 2048 : 0;
                Put(b.Ll3, row, line, d => acc = SaturatingAdd(acc, d));
            }
            var h1 = Grp(1, n);
            Fill([(b.Hl3, r.L3, h1.Half.Qi[0]), (b.Lh3, r.H3, h1.Half.Qi[1]), (b.Hh3, r.H3, h1.Half.Qi[2])], h1.Lines);
            var h2 = Grp(2, n);
            Fill([(b.Hl2, r.L2, h2.Half.Qi[0]), (b.Lh2, r.H2, h2.Half.Qi[1]), (b.Hh2, r.H2, h2.Half.Qi[2])], h2.Lines);
            var h3 = Grp(3, n);
            Fill([(b.Hl1, r.L1, h3.Half.Qi[0]), (b.Lh1, r.H1, h3.Half.Qi[1]), (b.Hh1, r.H1, h3.Half.Qi[2])], h3.Lines);
        }
        return Llvc.Reconstruct3(b, [(byte)ph[0], (byte)ph[1], (byte)ph[2]]);
    }

    private static int SaturatingAdd(int a, int b)
    {
        var v = (long)a + b;
        return v < int.MinValue ? int.MinValue : v > int.MaxValue ? int.MaxValue : (int)v;
    }

    /// <summary>解碼後的 tile：三個小波平面（綠均值、兩個色度）與綠色殘差，各為 <c>tw/2 × th/2</c>。</summary>
    public sealed class TilePlanes
    {
        public Llvc.Plane M { get; init; } = null!;
        public Llvc.Plane C1 { get; init; } = null!;
        public Llvc.Plane C2 { get; init; } = null!;
        public Llvc.Plane Res { get; init; } = null!;
    }

    /// <summary>
    /// 解一個 tile 到其平面：header、由 g4 首個 half 求 <c>s</c>、TU 數、全部 13 個 stream（平行）、
    /// 頻帶組裝與每個元件的 3 層逆轉換（平行）。
    /// </summary>
    public static TilePlanes DecodeTilePlanes(ReadOnlySpan<byte> tile, int tw, int th)
    {
        var h = ParseTileHeader(tile, tw, th);
        var (w2, vs) = (tw / 2, h.Vs);
        if (vs == 0 || tw % 2 != 0 || w2 % 8 != 0 || (long)w2 * vs > RawLimits.MaxSamples)
            throw Unsupported("tile geometry");
        if (h.Streams.Count <= 12)
            throw Corrupt("missing residual stream");
        var g4 = h.Streams[12];
        var s = FirstHalfShift(tile, g4, w2);
        var (ntu, ph) = (TuCount(vs, s), Phases(s));
        var layouts = new HalfRowsLayout[2 * ntu];
        for (var n = 0; n < layouts.Length; n++)
            layouts[n] = HalfLayout(n, s, vs, ph);
        var tileArr = tile.ToArray();
        var streams = new HalfLines[h.Streams.Count][];
        var errs = new Exception?[h.Streams.Count];
        Parallel.For(0, h.Streams.Count, i =>
        {
            try
            {
                streams[i] = DecodeStream(tileArr, h.Streams[i], ntu, w2, layouts);
            }
            catch (Exception e) when (e is RawException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                errs[i] = e;
            }
        });
        for (var i = 0; i < errs.Length; i++)
            if (errs[i] is { } e)
                throw e is RawException re ? re : Corrupt("stream decode failed");
        var comps = new Llvc.Plane?[3];
        var cerrs = new Exception?[3];
        Parallel.For(0, 3, c =>
        {
            try
            {
                comps[c] = Component(c, streams, layouts, vs, w2, ph);
            }
            catch (Exception e) when (e is RawException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                cerrs[c] = e;
            }
        });
        for (var i = 0; i < 3; i++)
            if (cerrs[i] is { } e)
                throw e is RawException re ? re : Corrupt("component decode failed");
        // 殘差：g4 的線即該 half 的平面列，以該 half 的 nibble 去量化
        var res = Llvc.Plane.Zeros(w2, vs);
        var g4lines = streams[12];
        for (var n = 0; n < g4lines.Length && n < layouts.Length; n++)
        {
            var (half, lines) = (g4lines[n].Half, g4lines[n].Lines);
            var r = layouts[n];
            var ri = 0;
            for (var row = r.T.Start; row < r.T.End && ri < lines.Length; row++, ri++)
                Put(res, row, lines[ri], q => Llvc.Dequant(q, half.Qi[0]));
        }
        var m = comps[0] ?? throw Corrupt("missing component");
        var c1 = comps[1] ?? throw Corrupt("missing component");
        var c2 = comps[2] ?? throw Corrupt("missing component");
        return new TilePlanes { M = m, C1 = c1, C2 = c2, Res = res };
    }

    /// <summary>
    /// 色彩重建（*Colour reconstruction*，經 Phase 0 oracle 輸出驗證）：平面構成 <c>(2·W2) × (2·H2)</c> 馬賽克，
    /// 每個 2×2 cell 的 R 在 (0,0)、G1 (0,1)、G2 (1,0)、B (1,1)，各值經 <see cref="Arw6Curve.Curve"/>。
    /// G1 由綠均值減去殘差平均得到；G2 由（未夾取的）G1 預測再加殘差；R/B 為色度加兩綠均值（夾到 0..=4095）。
    /// </summary>
    public static ushort[] Colour(TilePlanes p)
    {
        var (w2, h2) = (p.M.Width, p.M.Height);
        var len = (long)w2 * h2;
        if (p.C1.Width != w2 || p.C1.Height != h2 || p.C2.Width != w2 || p.C2.Height != h2 || p.Res.Width != w2 || p.Res.Height != h2
            || p.C1.Data.Length != len || p.C2.Data.Length != len || p.Res.Data.Length != len || p.M.Data.Length != len)
            throw Corrupt("plane size mismatch");
        var mw = checked(2 * w2);
        var total = checked((int)(4 * len));
        var g1 = new long[len];
        Parallel.For(0, h2, j =>
        {
            var m = p.M.Row(j);
            var rp = p.Res.Row(Math.Max(0, j - 1));
            var rj = p.Res.Row(j);
            if (m.IsEmpty || rp.IsEmpty || rj.IsEmpty)
                return;
            for (var x = 0; x < w2; x++)
            {
                var xn = Math.Min(x + 1, w2 - 1);
                var r = At2(rp, x) + At2(rp, xn) + At2(rj, x) + At2(rj, xn);
                g1[j * w2 + x] = At2(m, x) - ((r + 4) >> 3);
            }
        });
        static long At2(ReadOnlySpan<int> r, int x) => (uint)x < (uint)r.Length ? r[x] : 0;
        var outArr = new ushort[total];
        // 每個平面列產生兩條馬賽克列
        Parallel.For(0, h2, j =>
        {
            var gj = j * w2 + w2 <= g1.Length ? g1.AsSpan(j * w2, w2) : ReadOnlySpan<long>.Empty;
            var jn = Math.Min(j + 1, h2 - 1);
            var gn = jn * w2 + w2 <= g1.Length ? g1.AsSpan(jn * w2, w2) : ReadOnlySpan<long>.Empty;
            var res = p.Res.Row(j);
            var c1 = p.C1.Row(j);
            var c2 = p.C2.Row(j);
            var top = outArr.AsSpan(2 * j * mw, mw);
            var bottom = outArr.AsSpan((2 * j + 1) * mw, mw);
            for (var x = 0; x < w2; x++)
            {
                var xp = Math.Max(0, x - 1);
                var gg = ((Gr(gj, xp) + Gr(gj, x) + Gr(gn, xp) + Gr(gn, x)) >> 2) + At2(res, x);
                var mean = (Math.Clamp(Gr(gj, x), 0, 4095) + Math.Clamp(gg, 0, 4095)) >> 1;
                var cells = new[] { 2 * At2(c1, x) + mean, Gr(gj, x), gg, 2 * At2(c2, x) + mean };
                top[2 * x] = Arw6Curve.Curve((int)Math.Clamp(cells[0], 0, 4095));
                top[2 * x + 1] = Arw6Curve.Curve((int)Math.Clamp(cells[1], 0, 4095));
                bottom[2 * x] = Arw6Curve.Curve((int)Math.Clamp(cells[2], 0, 4095));
                bottom[2 * x + 1] = Arw6Curve.Curve((int)Math.Clamp(cells[3], 0, 4095));
            }
            static long Gr(ReadOnlySpan<long> r, int x) => (uint)x < (uint)r.Length ? r[x] : 0;
        });
        return outArr;
    }

    /// <summary>一個 tile 到其 <c>tw × th</c> 馬賽克（輸出單位）。</summary>
    public static ushort[] DecodeTile(ReadOnlySpan<byte> tile, int tw, int th) => Colour(DecodeTilePlanes(tile, tw, th));

    /// <summary>
    /// 解整個 ARW6 strip：<see cref="DecodeMode.Header"/> 驗證 tile table、tile headers 與 stream tables 並回傳空陣列；
    /// <see cref="DecodeMode.Full"/> 平行解各 tile 後組成 <c>width × height</c> 馬賽克。
    /// </summary>
    internal static ushort[] Decode(ReadOnlySpan<byte> strip, int width, int height, DecodeMode mode)
    {
        var tiles = ParseTileTable(strip, width, height);
        if (mode == DecodeMode.Header)
        {
            for (var i = 0; i < tiles.Length; i++)
            {
                var tile = TileBytes(strip, tiles, i);
                var t = tiles[i];
                var h = ParseTileHeader(tile, t.Width, t.Height);
                if (h.Streams.Count <= 12)
                    throw Corrupt("missing residual stream");
                var g4 = h.Streams[12];
                var s = FirstHalfShift(tile, g4, t.Width / 2);
                var ntu = TuCount(h.Vs, s);
                foreach (var st in h.Streams)
                    StreamHalves(tile, st, ntu);
            }
            return [];
        }
        // tile 失敗先於馬賽克配置回報（LightCraft issue #1512）
        var stripArr = strip.ToArray();
        var decoded = new ushort[tiles.Length][];
        var errs = new Exception?[tiles.Length];
        Parallel.For(0, tiles.Length, i =>
        {
            try
            {
                var t = tiles[i];
                decoded[i] = DecodeTile(TileBytes(stripArr, tiles, i), t.Width, t.Height);
            }
            catch (Exception e) when (e is RawException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                errs[i] = e;
            }
        });
        for (var i = 0; i < errs.Length; i++)
            if (errs[i] is { } e)
                throw e is RawException re ? re : Corrupt("tile decode failed");
        var outArr = new ushort[checked((long)width * height)];
        for (var i = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            var d = decoded[i];
            for (var r = 0; r < d.Length / Math.Max(1, t.Width); r++)
            {
                var row = d.AsSpan(r * t.Width, t.Width);
                var start = (long)(t.Y + r) * width + t.X;
                if (start < 0 || start + row.Length > outArr.Length)
                    throw Corrupt("tile outside image");
                row.CopyTo(outArr.AsSpan((int)start, row.Length));
            }
        }
        return outArr;
    }
}
