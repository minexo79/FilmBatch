// 移植自 LightCraft crates/raw/src/vendor/sr2.rs tests（密鑰流已知答案、種子派生、目錄選取、惡意目錄）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors.Sony;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class SonySr2Tests
{
    private const ushort Short = 3;
    private const ushort SShort = 8;
    private const ushort Long = 4;
    private const ushort Undefined = 7;
    private const ushort IfdType = 13;
    private static readonly byte[] Key = [0x11, 0x22, 0x33, 0x44];

    /// <summary>上游 sr2::tests::block：檔案偏移 start 的加密 SR2SubIFD；>2 shorts 的值放在表後。</summary>
    private static byte[] Block(int start, (ushort Tag, ushort Kind, ushort[] Values)[] entries, ByteOrder order)
    {
        var plain = new List<byte>();
        order.PutU16(plain, (ushort)entries.Length);
        var data = new List<byte>();
        var dataAt = start + 2 + 12 * entries.Length + 4;
        foreach (var (tag, kind, values) in entries)
        {
            order.PutU16(plain, tag);
            order.PutU16(plain, kind);
            order.PutU32(plain, (uint)values.Length);
            var field = new List<byte>();
            foreach (var v in values) order.PutU16(field, v);
            if (field.Count <= 4)
            {
                while (field.Count < 4) field.Add(0);
                plain.AddRange(field);
            }
            else
            {
                order.PutU32(plain, (uint)(dataAt + data.Count));
                data.AddRange(field);
            }
        }
        plain.AddRange(new byte[4]);
        plain.AddRange(data);
        plain.AddRange([0xa5, 0xa5]);
        var b = plain.ToArray();
        Sr2SubIfd.ApplyKeystreamWith(b, Sr2SubIfd.Seed);
        return b;
    }

    /// <summary>上游 sr2::tests::block_keyed。</summary>
    private static byte[] BlockKeyed(int start, (ushort Tag, ushort Kind, ushort[] Values)[] entries, ByteOrder order, byte[] key)
    {
        var b = Block(start, entries, order);
        Sr2SubIfd.ApplyKeystreamWith(b, Sr2SubIfd.Seed); // undo default key
        Sr2SubIfd.ApplyKeystreamWith(b, Sr2SubIfd.SeedFor(key));
        return b;
    }

    [Fact]
    public void KeystreamMatchesBytesRecoveredAtSinglePositions()
    {
        var ks = new byte[62114];
        Sr2SubIfd.ApplyKeystreamWith(ks, Sr2SubIfd.Seed);
        (int At, byte[] Bytes)[] recovered =
        [
            (0, [0x54, 0xc2, 0xc4, 0xd6, 0x49, 0xc4, 0x78, 0xbc, 0x34, 0xae, 0x2f, 0x8e, 0x25, 0xd8]),
            (1638, [0x18, 0x95, 0x65, 0xf0, 0x18, 0x8f, 0x61, 0xc6]),
            (2166, [0xc6, 0x17, 0x02, 0xcb, 0x89, 0x21, 0xbc, 0x43]),
            (2418, [0xd1, 0xef, 0xda, 0xfe, 0x50, 0xf3, 0xd0, 0xbe]),
            (2510, [0x43, 0xcb, 0x86, 0xb6, 0x11, 0xd2, 0x1a, 0x73]),
        ];
        foreach (var (at, bytes) in recovered)
            Assert.Equal(bytes, ks.AsSpan(at, bytes.Length).ToArray());
    }

    [Fact]
    public void KeystreamMatchesRecordedKnownAnswers()
    {
        // 整段密鑰流的 SHA-256（上游以 CC0 DSLR-A700 區塊量測釘選）。
        var ks = new byte[62112];
        Sr2SubIfd.ApplyKeystreamWith(ks, Sr2SubIfd.Seed);
        Assert.Equal("d55e96d7bb69cb823d9dfa038dd9f3cfb6b080be4df8c8aa2bc56f71a4a3f183",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ks)).ToLowerInvariant());
        (string File, int At, byte[] Stored, short[] Levels)[] cases =
        [
            ("DSLR-A700 DSC07249", 1654, [0xc9, 0x2a, 0xb6, 0xa4, 0x1f, 0xc1, 0x72, 0x30], [2128, 1024, 1024, 1564]),
            ("DSLR-A500 DSC02421", 2182, [0xb9, 0x6a, 0xed, 0xd3, 0x0d, 0xa8, 0xbb, 0xda], [2212, 1024, 1024, 1416]),
            ("SLT-A33 DSC01867", 2434, [0x3c, 0x43, 0x05, 0x3f, 0x0d, 0xe2, 0x05, 0xcc], [2344, 1024, 1024, 1508]),
            ("ILCE-3500 DSC06923", 2526, [0x37, 0x2e, 0x71, 0x5b, 0x88, 0xb7, 0x21, 0x73], [2932, 1024, 1024, 1576]),
        ];
        foreach (var (file, at, stored, levels) in cases)
        {
            var block = new byte[at + 8 + 3 & ~3];
            stored.CopyTo(block.AsSpan(at));
            Sr2SubIfd.ApplyKeystreamWith(block, Sr2SubIfd.Seed);
            var got = new short[4];
            for (var i = 0; i < 4; i++) got[i] = BitConverter.ToInt16(block, at + 2 * i);
            Assert.Equal(levels, got);
        }
    }

    [Fact]
    public void SeedDerivationReproducesTheMeasuredSeed()
    {
        var key = BitConverter.ToUInt32(Key);
        Assert.Equal(Sr2SubIfd.Seed, Sr2SubIfd.SeedFromKey(key));
        Assert.NotEqual(Sr2SubIfd.SeedFromKey(key ^ 1), Sr2SubIfd.SeedFromKey(key));
        Assert.Equal(Sr2SubIfd.Seed, Sr2SubIfd.SeedFor(Key));
    }

    [Fact]
    public void AnotherKeyDecryptsABlock()
    {
        byte[] key = [0x63, 0xbe, 0x01, 0x3e];
        (ushort Tag, ushort Kind, ushort[] Values)[] entries =
            [(0x7300, Short, [514, 511, 512, 513]), (0x7303, Short, [256, 500, 385, 256]), (0x74c3, Short, [16, 8, 3904, 2600])];
        foreach (var order in new[] { ByteOrder.Little, ByteOrder.Big })
        {
            var enc = BlockKeyed(230812, entries, order, key);
            var sub = Sr2SubIfd.DecryptKeyed(enc, 230812, order, key);
            Assert.Equal(new double[] { 514.0, 511.0, 512.0, 513.0 }, sub.Shorts(0x7300)!);
            Assert.Equal(new double[] { 256.0, 500.0, 385.0, 256.0 }, sub.Shorts(0x7303)!);
            Assert.Equal(new double[] { 16.0, 8.0, 3904.0, 2600.0 }, sub.Shorts(0x74c3)!);
            // 錯誤（或預設）金鑰得到雜訊。
            Assert.Null(Sr2SubIfd.Decrypt(enc, 230812, order).Shorts(0x7300));
        }
    }

    [Fact]
    public void KeystreamFollowsItsRecurrence()
    {
        var zeros = new byte[62114];
        Sr2SubIfd.ApplyKeystreamWith(zeros, Sr2SubIfd.Seed);
        uint Word(int n) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(zeros.AsSpan(4 * n, 4));
        Assert.Equal(Sr2SubIfd.Seed[64] ^ Sr2SubIfd.Seed[0], Word(127));
        for (var n = 200; n < 15528; n++)
            Assert.Equal(Word(n - 63) ^ Word(n - 127), Word(n));
        Assert.Equal(new byte[] { 0, 0 }, zeros[62112..]);
        var again = (byte[])zeros.Clone();
        Sr2SubIfd.ApplyKeystreamWith(again, Sr2SubIfd.Seed);
        Assert.All(again[..62112], b => Assert.Equal(0, (int)b));
    }

    [Fact]
    public void ReadsEntriesInlineAndAtFileOffsets()
    {
        foreach (var order in new[] { ByteOrder.Little, ByteOrder.Big })
        {
            var start = 75964;
            var enc = Block(start, [(0x7310, Short, [512, 512, 512, 512]), (0x7311, Short, [256]), (0x7313, SShort, [2212, 1024, 1024, 1416])], order);
            var sub = Sr2SubIfd.Decrypt(enc, start, order);
            Assert.Equal(new double[] { 2212.0, 1024.0, 1024.0, 1416.0 }, sub.Shorts(0x7313)!);
            Assert.Equal(new double[] { 512.0, 512.0, 512.0, 512.0 }, sub.Shorts(0x7310)!);
            Assert.Equal(new double[] { 256.0 }, sub.Shorts(0x7311)!);
            Assert.Null(sub.Shorts(0x7312));
            var enc2 = Block(start, [(0x7313, SShort, [0xffff, 1024])], order);
            Assert.Equal(new double[] { -1.0, 1024.0 }, Sr2SubIfd.Decrypt(enc2, start, order).Shorts(0x7313)!);
        }
    }

    /// <summary>上游 sr2::tests::private：檔案偏移 0 的 SR2Private 目錄（tag,type,count,4-byte value field）。</summary>
    private static byte[] Private((ushort Tag, ushort Kind, uint Count, byte[] Field)[] entries, ByteOrder order)
    {
        var outb = new List<byte>();
        order.PutU16(outb, (ushort)entries.Length);
        foreach (var (tag, kind, count, field) in entries)
        {
            order.PutU16(outb, tag);
            order.PutU16(outb, kind);
            order.PutU32(outb, count);
            outb.AddRange(field);
        }
        order.PutU32(outb, 0);
        return outb.ToArray();
    }

    /// <summary>上游 reader_selects：main 的 TIFF 讀取器解析（同選項），再以 u64/bytes 查詢。</summary>
    private static (int? Offset, int? Length, byte[]? Key) ReaderSelects(byte[] bytes, ByteOrder order)
    {
        var opts = new ParseOptions { MaxIfds = 1, MaxDepth = 0, FollowChildren = false };
        try
        {
            var (dir, _) = TiffFile.ParseIfdAt(bytes, 0, order, 0, false, opts);
            int? N(ushort tag) => dir.U64(tag) is { } v && v <= int.MaxValue ? (int)v : null;
            return (N(0x7200), N(0x7201), dir.Bytes(0x7221) is { Length: 4 } b ? b : null);
        }
        catch (RawException) { return (null, null, null); }
    }

    private static (string Name, byte[] Data)[] DifferentialCases(ByteOrder order)
    {
        byte[] W(uint v) { var l = new List<byte>(); order.PutU32(l, v); return l.ToArray(); }
        byte[] H(ushort a, ushort b) { var l = new List<byte>(); order.PutU16(l, a); order.PutU16(l, b); return l.ToArray(); }
        (ushort, ushort, uint, byte[])[] valid = [(0x7200, Long, 1, W(4096)), (0x7201, Long, 1, W(64)), (0x7221, Undefined, 4, Key)];
        var cases = new List<(string Name, byte[] Data)>();
        foreach (var fillers in new[] { 0, 1, 255, 256, 257, 8189, 8190, 8191, 8192, 8193 })
        {
            var entries = Enumerable.Repeat(((ushort)0x7300, Long, 1u, W(7)), fillers).ToList();
            entries.AddRange(valid);
            cases.Add(($"{fillers} fillers", Private([.. entries], order)));
        }
        var tailAt = 2 + 12 * 4 + 4;
        var tail = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        Key.CopyTo(tail.AsSpan(4));
        tail[8] = 0;
        (string What, ushort Kind, uint Count, byte[] Field)[] numeric =
        [
            ("unknown type 0", 0, 1, W(8)),
            ("unknown type 14", 14, 1, W(8)),
            ("unknown type 0xffff", 0xffff, 1, W(8)),
            ("LONG[2] past the file", Long, 2, W(uint.MaxValue)),
            ("LONG[2] straddling the end", Long, 2, W((uint)tailAt + 28)),
            ("LONG[u32::MAX]", Long, uint.MaxValue, W((uint)tailAt)),
            ("DOUBLE[u32::MAX]", 12, uint.MaxValue, W((uint)tailAt)),
            ("LONG[0]", Long, 0, W(0)),
            ("LONG[2] in the file", Long, 2, W((uint)tailAt)),
            ("SHORT[1]", Short, 1, H(9, 0)),
            ("SHORT[2] inline", Short, 2, H(9, 10)),
            ("BYTE[3]", 1, 3, [5, 6, 7, 0]),
            ("SSHORT -1", SShort, 1, H(0xffff, 0)),
            ("SSHORT 5", SShort, 1, H(5, 0)),
            ("SLONG -1", 9, 1, W(uint.MaxValue)),
            ("LONG8 at a pointer", 16, 1, W((uint)tailAt)),
            ("RATIONAL", 5, 1, W((uint)tailAt)),
            ("ASCII", 2, 4, [(byte)'a', (byte)'b', (byte)'c', 0]),
            ("IFD", IfdType, 1, W(4096)),
        ];
        foreach (var (tag, other) in new (ushort, (ushort, ushort, uint, byte[])[])[] { (0x7200, [valid[1], valid[2]]), (0x7201, [valid[0], valid[2]]) })
        {
            foreach (var (what, kind, count, field) in numeric)
            {
                var validOne = tag == 0x7200 ? valid[0] : valid[1];
                var d = Private([(tag, kind, count, field), validOne, other[0], other[1]], order).ToList();
                d.AddRange(tail);
                cases.Add(($"0x{tag:x}: first {what}", [.. d]));
            }
        }
        (string What, ushort Kind, uint Count, byte[] Field)[] keys =
        [
            ("unknown type", 0, 4, Key),
            ("UNDEFINED[8] past the file", Undefined, 8, W(uint.MaxValue)),
            ("UNDEFINED[5] in the file", Undefined, 5, W((uint)tailAt)),
            ("UNDEFINED[3]", Undefined, 3, Key),
            ("UNDEFINED[0]", Undefined, 0, Key),
            ("BYTE[4]", 1, 4, [1, 2, 3, 4]),
            ("SBYTE[4]", 6, 4, Key),
            ("ASCII[4]", 2, 4, Key),
            ("ASCII[4] with NUL", 2, 4, [0x41, 0x42, 0, 0x43]),
            ("ASCII[4] invalid UTF-8", 2, 4, [0xff, 0x41, 0, 0]),
            ("ASCII[4] two invalid bytes", 2, 4, [0xc3, 0x28, 0x41, 0]),
            ("ASCII[9] at a pointer, NUL at 4", 2, 9, W((uint)tailAt + 4)),
            ("ASCII[8] at a pointer, no NUL", 2, 8, W((uint)tailAt + 12)),
            ("LONG[1]", Long, 1, Key),
        ];
        foreach (var (what, kind, count, field) in keys)
        {
            var d = Private([(0x7221, kind, count, field), valid[0], valid[1], valid[2]], order).ToList();
            d.AddRange(tail);
            cases.Add(($"key: first {what}", [.. d]));
        }
        foreach (var aliases in new[] { 23, 24 })
        {
            var entries = Enumerable.Repeat(((ushort)0x7300, Undefined, 1u << 16, W(0)), aliases).ToList();
            entries.AddRange(valid);
            var d = Private([.. entries], order).ToList();
            while (d.Count < (1 << 16)) d.Add(0);
            cases.Add(($"{aliases} entries using the value budget", [.. d]));
        }
        cases.Add(("empty directory", Private([], order)));
        var longDir = Private(valid, order).ToList();
        var ten = new List<byte>(); order.PutU16(ten, 10);
        longDir[0] = ten[0];
        longDir[1] = ten[1];
        cases.Add(("10 entries declared, 3 present", [.. longDir.Take(2 + 12 * 3)]));
        return [.. cases];
    }

    [Fact]
    public void PrivateFieldsMatchTheTiffReadersSelection()
    {
        var differ = new List<string>();
        foreach (var order in new[] { ByteOrder.Little, ByteOrder.Big })
        {
            foreach (var (name, d) in DifferentialCases(order))
            {
                var f = Sr2SubIfd.PrivateFields(d, 0, order);
                var reader = ReaderSelects(d, order);
                var sameKey = f.Key is null ? reader.Key is null : reader.Key is not null && f.Key.SequenceEqual(reader.Key);
                if (f.Offset != reader.Offset || f.Length != reader.Length || !sameKey) differ.Add($"{order} {name}: ({f.Offset}, {f.Length}, {f.Key?.Length}), reader ({reader.Offset}, {reader.Length}, {reader.Key?.Length})");
            }
            var e0 = Sr2SubIfd.PrivateFields(Array.Empty<byte>(), 0, order);
            Assert.Equal(((int?)null, (int?)null, (byte[]?)null), (e0.Offset, e0.Length, e0.Key));
            var e9 = Sr2SubIfd.PrivateFields(Private([], order), long.MaxValue, order);
            Assert.Equal(((int?)null, (int?)null, (byte[]?)null), (e9.Offset, e9.Length, e9.Key));
        }
        Assert.Empty(differ);
        static bool Found(byte[] d)
        {
            var f = Sr2SubIfd.PrivateFields(d, 0, ByteOrder.Little);
            return f.Offset == 4096 && f.Length == 64 && f.Key is { } k && k.SequenceEqual(Key);
        }
        var cases = DifferentialCases(ByteOrder.Little).ToDictionary(c => c.Name, c => c.Data);
        foreach (var name in new[] { "0 fillers", "256 fillers", "8189 fillers", "0x7200: first LONG[2] past the file",
            "0x7201: first unknown type 14", "key: first UNDEFINED[8] past the file", "23 entries using the value budget" })
            Assert.True(Found(cases[name]), name);
        foreach (var name in new[] { "8190 fillers", "8192 fillers", "24 entries using the value budget", "0x7200: first ASCII", "key: first UNDEFINED[3]" })
            Assert.False(Found(cases[name]), name);
    }

    [Fact]
    public void TheBlockIsFoundBehindFillerAndUnreadableEntries()
    {
        foreach (var order in new[] { ByteOrder.Little, ByteOrder.Big })
        foreach (var (fillers, unreadableFirst) in new[] { (256, false), (300, false), (0, true), (256, true) })
        {
            byte[] W(uint v) { var l = new List<byte>(); order.PutU32(l, v); return l.ToArray(); }
            var n = fillers + 3 + (unreadableFirst ? 1 : 0);
            var start = 2 + 12 * n + 4;
            var block = Block(start, [(0x7310, Short, [800, 800, 800, 800])], order);
            var entries = Enumerable.Repeat(((ushort)0x7300, Long, 1u, W(7)), fillers).ToList();
            if (unreadableFirst) entries.Add((0x7200, Long, 2, W(uint.MaxValue)));
            entries.Add((0x7200, Long, 1, W((uint)start)));
            entries.Add((0x7201, Long, 1, W((uint)block.Length)));
            entries.Add((0x7221, Undefined, 4, Key));
            var file = Private([.. entries], order).ToList();
            Assert.Equal(start, file.Count);
            file.AddRange(block);
            var ifd0 = new Ifd(0)
            {
                Entries = [new TiffEntry(Tags.DngPrivateData, new TiffValue.Byte(W(0)), 0)],
            };
            var black = Sr2SubIfd.Read(file.ToArray(), ifd0, order)?.Shorts(0x7310);
            Assert.Equal(new double[] { 800.0, 800.0, 800.0, 800.0 }, black!);
        }
    }

    [Fact]
    public void HostileDirectoriesGiveNone()
    {
        var start = 4096;
        var good = Block(start, [(0x7313, SShort, [2212, 1024, 1024, 1416])], ByteOrder.Little);
        double[]? Read(byte[] b, int s) => Sr2SubIfd.Decrypt(b, s, ByteOrder.Little).Shorts(0x7313);
        Assert.NotNull(Read(good, start));
        Assert.Null(Read(good, start + 64));
        Assert.Null(Read(good, start - 64));
        for (var n = 0; n < good.Length - 2; n++)
            Assert.Null(Read(good[..n], start));
        // 仍加密、65535 個 0xff 項目、超大 count、另一型別、亂指偏移：皆為 null。
        Assert.Null(Sr2SubIfd.Decrypt(Enumerable.Repeat((byte)0xff, 1000).ToArray(), start, ByteOrder.Little).Shorts(0x7313));
        var plain = (byte[])good.Clone();
        Sr2SubIfd.ApplyKeystreamWith(plain, Sr2SubIfd.Seed);
        double[]? Patched(int at, byte[] bytes)
        {
            var p = (byte[])plain.Clone();
            bytes.CopyTo(p.AsSpan(at));
            Sr2SubIfd.ApplyKeystreamWith(p, Sr2SubIfd.Seed);
            return Read(p, start);
        }
        Assert.Null(Patched(6, BitConverter.GetBytes(uint.MaxValue)));
        Assert.Null(Patched(4, BitConverter.GetBytes((ushort)4)));
        Assert.Null(Patched(10, BitConverter.GetBytes(uint.MaxValue)));
        Assert.Equal(new double[] { 2212.0, 1024.0, 1024.0, 1416.0 }, Patched(0, BitConverter.GetBytes(ushort.MaxValue))!);
    }
}
