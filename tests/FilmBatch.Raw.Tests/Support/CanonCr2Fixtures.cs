// 移植自 LightCraft crates/raw/src/vendor/cr2.rs tests、cr2/sraw.rs tests 與 crates/raw/src/ljpeg.rs tests（合成夾具）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Numerics;

namespace FilmBatch.Raw.Tests.Support;

/// <summary>Canon CR2 合成檔夾具（測試專用）：最小 LJ92 編碼器＋手寫 LE TIFF。</summary>
internal static class CanonCr2Fixtures
{
    internal const ushort Cr2SliceTag = 0xC640;
    internal const ushort SrawTypeTag = 0xC6C5;
    internal const ushort CfaTag = 0xC5E0;
    internal const ushort SensorInfoTag = 0x00E0;
    internal const ushort ColorBalanceTag = 0x4001;
    internal const ushort ShotInfoTag = 0x0004;
    internal const ushort WbInfoTag = 0x0029;

    private const ushort TAscii = 2;
    private const ushort TShort = 3;
    private const ushort TLong = 4;
    private const ushort TUndefined = 7;

    // ---------- LJ92 編碼（predictor 1、無 restart；固定 5 位元 Huffman 表） ----------

    private sealed class BitWriter
    {
        public readonly List<byte> Out = [];
        private ulong _acc;
        private int _n;

        public void Put(uint v, int bits)
        {
            if (bits == 0) return;
            _acc = (_acc << bits) | (v & (bits >= 32 ? 0xFFFFFFFFu : ((1u << bits) - 1)));
            _n += bits;
            while (_n >= 8)
            {
                byte b = (byte)(_acc >> (_n - 8));
                Out.Add(b);
                if (b == 0xFF) Out.Add(0);
                _n -= 8;
            }
        }

        public void Flush()
        {
            if (_n > 0)
            {
                int pad = 8 - _n;
                Put((1u << pad) - 1u, pad);
            }
        }
    }

    private static byte SsssOf(int diff)
    {
        if (diff == 0) return 0;
        if (diff == 32768 || diff == -32768) return 16;
        return (byte)(32 - BitOperations.LeadingZeroCount((uint)Math.Abs(diff)));
    }

    /// <summary>全解析度 LJ92 編碼（對應 ljpeg::encode 的 predictor 1、無 restart 路徑）。</summary>
    internal static byte[] LjpegEncode(ushort[] data, int width, int height, int components, byte precision)
    {
        int rowLen = width * components;
        int init = 1 << (precision - 1);
        var diffs = new int[data.Length];
        bool resetPending = true; // 僅首像素用 init（對應 Rust encode 的 reset_pending）
        for (int y = 0; y < height; y++)
        {
            bool firstLine = y == 0;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < components; c++)
                {
                    int i = y * rowLen + x * components + c;
                    int p = resetPending ? init
                        : firstLine ? (x == 0 ? data[i - rowLen] : data[i - components])
                        : x == 0 ? data[i - rowLen] : data[i - components]; // predictor 1
                    int d = (data[i] - p) & 0xFFFF;
                    if (d > 32768) d -= 65536;
                    diffs[i] = d;
                }
                resetPending = false;
            }
        }
        var o = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC3 };
        int sofLen = 8 + 3 * components;
        o.Add((byte)(sofLen >> 8)); o.Add((byte)sofLen);
        o.Add(precision);
        o.Add((byte)(height >> 8)); o.Add((byte)height);
        o.Add((byte)(width >> 8)); o.Add((byte)width);
        o.Add((byte)components);
        for (int c = 0; c < components; c++) o.AddRange([(byte)(c + 1), 0x11, 0]);
        for (int c = 0; c < components; c++)
        {
            o.AddRange([0xFF, 0xC4, 0x00, 0x24, (byte)c]);
            for (int k = 0; k < 16; k++) o.Add(k == 4 ? (byte)17 : (byte)0);
            for (int k = 0; k <= 16; k++) o.Add((byte)k);
        }
        o.AddRange([0xFF, 0xDA]);
        int sosLen = 6 + 2 * components;
        o.Add((byte)(sosLen >> 8)); o.Add((byte)sosLen);
        o.Add((byte)components);
        for (int c = 0; c < components; c++) o.AddRange([(byte)(c + 1), (byte)(c << 4)]);
        o.AddRange([1, 0, 0]);
        var w = new BitWriter();
        w.Out.AddRange(o);
        foreach (int d in diffs)
        {
            byte s = SsssOf(d);
            w.Put(s, 5);
            if (s > 0 && s < 16)
            {
                int v = d < 0 ? d - 1 : d;
                w.Put((uint)v & ((1u << s) - 1), s);
            }
        }
        w.Flush();
        w.Out.AddRange([0xFF, 0xD9]);
        return w.Out.ToArray();
    }

    /// <summary>手寫 subsampled 夾具（對應 ljpeg::fixture_subsampled）。</summary>
    internal static byte[] FixtureSubsampled(byte width, byte height, byte sampling, int[] differences)
    {
        var o = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC3, 0, 17, 16, 0, height, 0, width, 3, 1, sampling, 0, 2, 0x11, 0, 3, 0x11, 0 };
        o.AddRange([0xFF, 0xC4, 0, 36, 0]);
        for (int i = 0; i < 16; i++) o.Add(i == 4 ? (byte)17 : (byte)0);
        for (int i = 0; i <= 16; i++) o.Add((byte)i);
        o.AddRange([0xFF, 0xDA, 0, 12, 3, 1, 0, 2, 0, 3, 0, 1, 0, 0]);
        var w = new BitWriter();
        w.Out.AddRange(o);
        foreach (int d in differences)
        {
            byte s = SsssOf(d);
            w.Put(s, 5);
            if (s > 0 && s < 16)
            {
                int v = d < 0 ? d - 1 : d;
                w.Put((uint)v & ((1u << s) - 1), s);
            }
        }
        w.Flush();
        w.Out.AddRange([0xFF, 0xD9]);
        return w.Out.ToArray();
    }

    internal static byte[] Stream422()
        => FixtureSubsampled(4, 2, 0x21, [-31768, 1, -16384, -16284, 1, 1, 1, 2, 100, 1, 100, 100, 1, 1, 1, 2]);

    // ---------- 最小 LE TIFF 組裝 ----------

    private sealed class Entry
    {
        public ushort Tag;
        public ushort Type;
        public uint Count;
        public byte[] Inline = new byte[4]; // 外部值時忽略
        public byte[]? External;
    }

    private static byte[] U16(ushort v) => [(byte)(v & 0xFF), (byte)(v >> 8)];
    private static byte[] U32(uint v) => [(byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)(v >> 24)];
    private static byte[] U16List(params ushort[] vs)
    {
        var o = new List<byte>();
        foreach (var v in vs) o.AddRange(U16(v));
        return o.ToArray();
    }
    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s + "\0");

    private static Entry Inl(ushort tag, ushort type, uint count, byte[] inline4)
    {
        var e = new Entry { Tag = tag, Type = type, Count = count };
        Array.Copy(inline4, e.Inline, Math.Min(4, inline4.Length));
        return e;
    }
    private static Entry Ext(ushort tag, ushort type, uint count, byte[] data) => new() { Tag = tag, Type = type, Count = count, External = data };

    /// <summary>Plain maker-note（Canon：值位址為 TIFF 檔頭相對位址）。</summary>
    internal static byte[] BuildPlainNote(List<(ushort Tag, ushort Type, byte[] Data)> entries, int noteOffset)
    {
        var ordered = entries.OrderBy(e => e.Tag).ToList();
        var o = new List<byte>();
        o.AddRange(U16((ushort)ordered.Count));
        int valueAt = noteOffset + 2 + 12 * ordered.Count + 4;
        foreach (var (tag, type, data) in ordered)
        {
            uint count = type == TShort ? (uint)(data.Length / 2) : (uint)data.Length;
            o.AddRange(U16(tag));
            o.AddRange(U16(type));
            o.AddRange(U32(count));
            if (data.Length <= 4)
            {
                var inline4 = new byte[4];
                Array.Copy(data, inline4, data.Length);
                o.AddRange(inline4);
            }
            else
            {
                o.AddRange(U32((uint)valueAt));
                valueAt += data.Length;
            }
        }
        o.AddRange(U32(0));
        foreach (var (_, _, data) in ordered)
            if (data.Length > 4) o.AddRange(data);
        return o.ToArray();
    }

    internal static byte[] U16ListOf(params ushort[] vs) => U16List(vs);

    internal static byte[] ShotInfoBytes(ushort wbSetting)
    {
        var v = new ushort[34];
        v[7] = wbSetting;
        return U16List(v);
    }

    internal static byte[] WbInfoBytes(int[] rows)
    {
        var o = new List<byte>();
        foreach (int r in rows)
        {
            o.Add((byte)(r & 0xFF)); o.Add((byte)((r >> 8) & 0xFF));
            o.Add((byte)((r >> 16) & 0xFF)); o.Add((byte)(r >> 24));
        }
        return o.ToArray();
    }

    // ---------- 對外合成檔 ----------

    /// <summary>合成 CFA CR2（IFD0..IFD3；IFD3 為切片 LJ92）。noteEntries 為 maker-note 內容（可為空）。</summary>
    internal static (byte[] File, ushort[] Img) SyntheticCr2(int w, int h, ushort[]? slices, List<(ushort Tag, ushort Type, byte[] Data)>? noteEntries)
    {
        var img = new ushort[w * h];
        for (int i = 0; i < img.Length; i++) img[i] = (ushort)(1024 + ((i * 7919) % 12000));
        return (SyntheticCr2Of(img, w, h, slices, noteEntries), img);
    }

    internal static byte[] SyntheticCr2Of(ushort[] img, int w, int h, ushort[]? slices, List<(ushort Tag, ushort Type, byte[] Data)>? noteEntries)
    {
        var widths = new List<int>();
        if (slices is null) widths.Add(w);
        else { for (int k = 0; k < slices[0]; k++) widths.Add(slices[1]); widths.Add(slices[2]); }
        var stream = new List<ushort>();
        int x0 = 0;
        foreach (int sw in widths)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < sw; x++) stream.Add(img[y * w + x0 + x]);
            x0 += sw;
        }
        byte[] enc = LjpegEncode(stream.ToArray(), w / 2, h, 2, 14);
        return AssembleCr2(h, enc, slices, cfa: 3, srawType: null, noteEntries, previewRgb: null);
    }

    /// <summary>sRAW 合成檔（raw IFD 持有 stream；IFD2 可帶 2×2 16-bit RGB preview）。</summary>
    internal static byte[] SrawFile(byte[] stream, ushort[] slices, uint srawType, ushort[]? preview)
        => AssembleCr2(2, stream, slices, cfa: null, srawType: srawType, noteEntries: null, previewRgb: preview);

    private sealed class Blob
    {
        public Entry? Owner; // null＝無主（strip 本體）
        public byte[] Data = [];
        public int Offset;
    }

    private static void SetInline(List<Entry> f, ushort tag, uint value)
    {
        foreach (var e in f)
            if (e.Tag == tag && e.External is null)
            {
                var b = U32(value);
                Array.Copy(b, e.Inline, 4);
                return;
            }
        throw new InvalidOperationException($"fixture inline tag {tag:X} not found");
    }

    private static byte[] AssembleCr2(int rowsPerStrip, byte[] strip, ushort[]? slices, uint? cfa, uint? srawType,
        List<(ushort Tag, ushort Type, byte[] Data)>? noteEntries, ushort[]? previewRgb)
    {
        byte[]? previewStrip = null;
        if (previewRgb is not null)
        {
            previewStrip = new byte[previewRgb.Length * 2];
            for (int i = 0; i < previewRgb.Length; i++)
            {
                previewStrip[2 * i] = (byte)(previewRgb[i] & 0xFF);
                previewStrip[2 * i + 1] = (byte)(previewRgb[i] >> 8);
            }
        }
        byte[] makeB = Ascii("Canon");
        byte[] modelB = Ascii("Canon EOS Test");
        byte[]? sliceB = slices is not null ? U16List(slices) : null;
        byte[]? bitsB = previewStrip is not null ? U16List(16, 16, 16) : null;

        var ifd0 = new List<Entry>
        {
            Ext(271, TAscii, (uint)makeB.Length, makeB),
            Ext(272, TAscii, (uint)modelB.Length, modelB),
            Inl(274, TShort, 1, U16(8)),
            Inl(34665, TLong, 1, U32(0)), // Exif 位址回填
        };
        var ifd1 = new List<Entry> { Inl(1, TShort, 1, U16(0)) };
        var ifd2 = new List<Entry>();
        if (previewStrip is null) ifd2.Add(Inl(1, TShort, 1, U16(0)));
        else
        {
            ifd2.Add(Inl(256, TLong, 1, U32(2)));
            ifd2.Add(Inl(257, TLong, 1, U32(2)));
            ifd2.Add(Ext(258, TShort, 3, bitsB!));
            ifd2.Add(Inl(259, TShort, 1, U16(1)));
            ifd2.Add(Inl(262, TShort, 1, U16(2)));
            ifd2.Add(Inl(273, TLong, 1, U32(0))); // preview strip 位址回填
            ifd2.Add(Inl(277, TShort, 1, U16(3)));
            ifd2.Add(Inl(278, TLong, 1, U32(2)));
            ifd2.Add(Inl(279, TLong, 1, U32((uint)previewStrip.Length)));
        }
        var ifd3 = new List<Entry> { Inl(259, TShort, 1, U16(6)) };
        if (cfa.HasValue) ifd3.Add(Inl(CfaTag, TLong, 1, U32(cfa.Value)));
        if (srawType.HasValue) ifd3.Add(Inl(SrawTypeTag, TLong, 1, U32(srawType.Value)));
        if (sliceB is not null) ifd3.Add(Ext(Cr2SliceTag, TShort, (uint)slices!.Length, sliceB));
        ifd3.Add(Inl(273, TLong, 1, U32(0))); // raw strip 位址回填
        ifd3.Add(Inl(279, TLong, 1, U32((uint)strip.Length)));
        ifd3.Add(Inl(278, TLong, 1, U32((uint)rowsPerStrip)));

        var exif = new List<Entry> { Inl(34855, TShort, 1, U16(400)) };
        Entry? mnEntry = null;
        if (noteEntries is not null)
        {
            mnEntry = Ext(37500, TUndefined, 0, []); // Count 與內容稍後定
            exif.Add(mnEntry);
        }

        // 版面：IFD 依序，外部值在後。note 內容依其位址產生，需先定址。
        var ifds = new List<List<Entry>> { ifd0, ifd1, ifd2, ifd3, exif };
        static int Aligned(int n) => n + (n & 1);
        int at = 8;
        var ifdAt = new List<int>();
        foreach (var f in ifds) { ifdAt.Add(at); at += 2 + 12 * f.Count + 4; }
        int exifAt = ifdAt[4];
        SetInline(ifd0, 34665, (uint)exifAt);

        var blobs = new List<Blob>();
        void TakeBlobs(List<Entry> owner)
        {
            foreach (var e in owner)
                if (e.External is not null && e.External.Length > 0)
                    blobs.Add(new Blob { Owner = e, Data = e.External });
        }
        TakeBlobs(ifd0); TakeBlobs(ifd2); TakeBlobs(ifd3);
        if (previewStrip is not null) blobs.Add(new Blob { Data = previewStrip });
        Blob stripBlob = new() { Data = strip };
        blobs.Add(stripBlob);
        // note 定址：先算其他外部值長度，再建 note 內容
        int cursor = at;
        foreach (var b in blobs) { if ((cursor & 1) == 1) cursor += 1; cursor += Aligned(b.Data.Length); }
        if ((cursor & 1) == 1) cursor += 1;
        int noteAt = cursor;
        byte[] noteBytes = noteEntries is not null ? BuildPlainNote(noteEntries, noteAt) : [];
        Blob? noteBlob = null;
        if (mnEntry is not null)
        {
            mnEntry.Count = (uint)noteBytes.Length;
            noteBlob = new Blob { Owner = mnEntry, Data = noteBytes };
        }
        // 定外部位址
        foreach (var b in blobs)
        {
            if ((at & 1) == 1) at += 1;
            b.Offset = at;
            at += Aligned(b.Data.Length);
        }
        if (noteBlob is not null)
        {
            if ((at & 1) == 1) at += 1;
            noteBlob.Offset = at;
            at += Aligned(noteBlob.Data.Length);
            blobs.Add(noteBlob);
        }
        // 回填 strip 位址
        SetInline(ifd3, 273, (uint)stripBlob.Offset);
        if (previewStrip is not null)
        {
            var pb = blobs.First(b => b.Owner is null && b.Data == previewStrip);
            SetInline(ifd2, 273, (uint)pb.Offset);
        }

        var file = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 };
        for (int k = 0; k < 4; k++)
        {
            WriteEntries(file, ifds[k], blobs);
            file.AddRange(U32(k + 1 < 4 ? (uint)ifdAt[k + 1] : 0));
        }
        while (file.Count < exifAt) file.Add(0);
        WriteEntries(file, exif, blobs);
        file.AddRange(U32(0));
        // 外部值（含 note）
        foreach (var (off, d) in blobs.Select(b => (b.Offset, b.Data)).OrderBy(x => x.Offset))
        {
            while (file.Count < off) file.Add(0);
            file.AddRange(d);
            if ((d.Length & 1) == 1) file.Add(0);
        }
        return file.ToArray();
    }

    private static void WriteEntries(List<byte> file, List<Entry> entries, List<Blob> blobs)
    {
        var ordered = entries.OrderBy(e => e.Tag).ToList();
        file.AddRange(U16((ushort)ordered.Count));
        foreach (var e in ordered)
        {
            file.AddRange(U16(e.Tag));
            file.AddRange(U16(e.Type));
            file.AddRange(U32(e.Count));
            if (e.External is not null)
            {
                Blob? b = blobs.FirstOrDefault(x => ReferenceEquals(x.Owner, e));
                file.AddRange(U32((uint)(b?.Offset ?? 0)));
            }
            else file.AddRange(e.Inline);
        }
    }
}
