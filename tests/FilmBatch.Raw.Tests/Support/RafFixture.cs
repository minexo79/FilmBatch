// 測試支援：依 lightcraft-raw 的 raf.rs 測試夾具（raf/raf_flagged/raf_full/pack）與 rafc.rs
// 測試夾具（zero_stream/compressed）建構合成 Fujifilm RAF 檔案。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Buffers.Binary;

namespace FilmBatch.Raw.Tests.Support;

/// <summary>
/// 依上游測試夾具的位元組版面建構合成 RAF：
/// 固定檔頭（JPEG、記錄目錄與 raw 區塊的位元組 84..108 大端序指標）＋
/// 小端序 TIFF raw 區塊（IFD0 以 0xf000 指向 raw IFD；raw IFD 含 0xf001..0xf00e）。
/// </summary>
internal static class RafFixture
{
    /// <summary>raw IFD 的額外 SRATIONAL 表（(tag, 分子/分母序列)），鏡像上游 <c>raf_full</c> 的 <c>extra</c>。</summary>
    public static byte[] Create(
        uint w, uint h, uint bits, byte[] strip, byte[]? layout = null, byte[]? jpeg = null, uint words32 = 0,
        string model = "TEST", (ushort Tag, (int Num, int Den)[] Values)[]? extra = null)
    {
        extra ??= [];
        var block = new List<byte>();
        void BlockU16(ushort v) => block.AddRange(BitConverter.GetBytes(v)); // LE on any host we run on
        void BlockU32(uint v) => block.AddRange(BitConverter.GetBytes(v));
        void BlockI32(int v) => block.AddRange(BitConverter.GetBytes(v));

        // "II*\0" + IFD0 offset 8；IFD0 只有一個條目：tag 0xf000 → 子 IFD。
        block.AddRange("II*\0"u8.ToArray());
        block.AddRange(BitConverter.GetBytes(8u));
        var subOff = (uint)(8 + 2 + 12 + 4);
        BlockU16(1);
        block.AddRange([0x00, 0xf0, 13, 0, 1, 0, 0, 0]); // tag 0xf000, type IFD, count 1
        BlockU32(subOff);
        BlockU32(0); // 無下一個 IFD

        var n = 8u + (uint)extra.Length;
        var wbOff = subOff + 2 + 12 * n + 4;
        var next = wbOff + 12;
        var tableAt = new uint[extra.Length];
        for (var i = 0; i < extra.Length; i++)
        {
            tableAt[i] = next;
            next += (uint)(8 * extra[i].Values.Length);
        }
        var stripOff = next;

        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (0xf001, 4, 1, w),
            (0xf002, 4, 1, h),
            (0xf003, 4, 1, bits),
            (0xf006, 4, 1, words32),
            (0xf007, 4, 1, stripOff),
            (0xf008, 4, 1, (uint)strip.Length),
            (0xf00a, 4, 1, 64),
            (0xf00e, 4, 3, wbOff),
        };
        for (var i = 0; i < extra.Length; i++)
            entries.Add((extra[i].Tag, 10, (uint)extra[i].Values.Length, tableAt[i]));
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        BlockU16((ushort)n);
        foreach (var (tag, type, count, value) in entries)
        {
            BlockU16(tag);
            BlockU16(type);
            BlockU32(count);
            BlockU32(value);
        }
        BlockU32(0); // 無下一個 IFD
        // WB_GRBLevels（0xf00e）：三個 LONG，G=600 → [2.0, 1.0, 1.5]
        foreach (var v in new[] { 300u, 600u, 450u }) BlockU32(v);
        foreach (var (_, values) in extra)
            foreach (var (num, den) in values)
            {
                BlockI32(num);
                BlockI32(den);
            }
        block.AddRange(strip);

        // 記錄目錄（大端序 (u16 tag, u16 size, data)）。
        var recs = new List<byte>();
        var nrec = 0u;
        void AddRecord(ushort tag, byte[] data)
        {
            recs.AddRange(tag.ToBytesBigEndian());
            recs.AddRange(((ushort)data.Length).ToBytesBigEndian());
            recs.AddRange(data);
            nrec++;
        }
        AddRecord(0x0100, [(byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w]);
        AddRecord(0x0110, [0, 2, 0, 4]);
        AddRecord(0x0111, [0, (byte)(h - 2), 0, (byte)(w - 4)]);
        if (layout is not null) AddRecord(0x0131, layout);
        var dir = new List<byte>();
        dir.AddRange(nrec.ToBytesBigEndian());
        dir.AddRange(recs);

        var jpg = jpeg ?? [];
        var output = new List<byte>();
        output.AddRange("FUJIFILMCCD-RAW 0201FF000000"u8.ToArray());
        output.AddRange(System.Text.Encoding.ASCII.GetBytes(model));
        while (output.Count < 112) output.Add(0);
        if (strip.AsSpan().StartsWith("IS"u8))
        {
            var compression = strip.Length >= 3 && strip[2] == 0 ? 3u : 2u;
            var bytes = compression.ToBytesBigEndian();
            for (var i = 0; i < bytes.Length; i++) output[108 + i] = bytes[i];
        }
        var jpegOff = 160u;
        var dirOff = jpegOff + (uint)jpg.Length;
        var rawOff = dirOff + (uint)dir.Count;
        uint[] ptrs = [jpegOff, (uint)jpg.Length, dirOff, (uint)dir.Count, rawOff, (uint)block.Count];
        for (var i = 0; i < ptrs.Length; i++)
        {
            var bytes = ptrs[i].ToBytesBigEndian();
            for (var k = 0; k < 4; k++) output[84 + i * 4 + k] = bytes[k];
        }
        while (output.Count < 160) output.Add(0);
        output.AddRange(jpg);
        output.AddRange(dir);
        output.AddRange(block);
        return output.ToArray();
    }

    /// <summary>依上游測試的 <c>pack</c>：16-bit 小端序、12-bit LSB-first、其他深度為 32-bit LE word 內的 MSB 流。</summary>
    public static byte[] Pack(ushort[] px, uint bits)
    {
        switch (bits)
        {
            case 16:
            {
                var o = new byte[px.Length * 2];
                for (var i = 0; i < px.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(i * 2), px[i]);
                return o;
            }
            case 12:
            {
                var o = new List<byte>();
                for (var i = 0; i < px.Length; i += 2)
                {
                    var v = (uint)px[i] | ((uint)px[i + 1] << 12);
                    o.Add((byte)v);
                    o.Add((byte)(v >> 8));
                    o.Add((byte)(v >> 16));
                }
                return o.ToArray();
            }
            default:
            {
                // MSB-first 位元流，再將每 32-bit word 位元組反轉成小端序。
                var stream = new List<byte>();
                ulong acc = 0;
                var n = 0;
                foreach (var v in px)
                {
                    acc = (acc << (int)bits) | v;
                    n += (int)bits;
                    while (n >= 8)
                    {
                        stream.Add((byte)(acc >> (n - 8)));
                        n -= 8;
                    }
                }
                if (n > 0) stream.Add((byte)(acc << (8 - n)));
                while (stream.Count % 4 != 0) stream.Add(0);
                var le = new List<byte>();
                for (var i = 0; i < stream.Count; i += 4)
                {
                    le.Add(stream[i + 3]);
                    le.Add(stream[i + 2]);
                    le.Add(stream[i + 1]);
                    le.Add(stream[i]);
                }
                return le.ToArray();
            }
        }
    }

    /// <summary>上游 rafc.rs 測試的標準 X-Trans 版面記錄（已反轉儲存的 36 位元組）。</summary>
    public static byte[] XTransLayoutRecord()
    {
        var xtrans = Cfa.XTrans().Pattern;
        var layout = new byte[36];
        for (var i = 0; i < 36; i++) layout[i] = xtrans[35 - i];
        return layout;
    }
}

internal static class RafFixtureBytes
{
    public static byte[] ToBytesBigEndian(this ushort v) => [(byte)(v >> 8), (byte)v];

    public static byte[] ToBytesBigEndian(this uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}
