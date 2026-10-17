// 移植自 LightCraft crates/raw/src/vendor/sr2.rs（Sony 加密 SR2SubIFD）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Buffers.Binary;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Sony;

/// <summary>
/// Sony 的加密 <c>SR2SubIFD</c>：由 IFD0 的 <c>DNGPrivateData</c> 經 <c>SR2Private</c> IFD 找到
/// （<c>SR2SubIFDOffset</c> 0x7200、<c>SR2SubIFDLength</c> 0x7201、<c>SR2SubIFDKey</c> 0x7221，
/// 標籤名稱取自 ExifTool Sony 文件）。此模組是唯一解密的處所；黑階（0x7310）與拍攝白平衡
/// （WB_RGGBLevels 0x7313，R,G,G,B）由此取得。金鑰派生與密鑰流為 black-box 逆向（上游註解記錄完整證明）。
/// </summary>
internal sealed class Sr2SubIfd
{
    private const ushort SubIfdOffset = 0x7200;
    private const ushort SubIfdLength = 0x7201;
    private const ushort SubIfdKey = 0x7221;

    /// <summary>所有檢查過的 ARW 都存這把金鑰；<see cref="Seed"/> 是它的密鑰流前 127 字。</summary>
    private static readonly byte[] DefaultKey = [0x11, 0x22, 0x33, 0x44];

    /// <summary>解密區塊長度上限（最大實測 62112 byte；DSLR-A700）。</summary>
    internal const int MaxLen = 1 << 20;

    /// <summary>該金鑰的前 127 個密鑰流字（每字與四個區塊位元組 XOR，最高位元組優先）。</summary>
    internal static readonly uint[] Seed =
    [
        0x54c2c4d6, 0x49c478bc, 0x34ae2f8e, 0x25d80504, 0xc0d9d6b0, 0xd838fb71, 0xe8eff27d, 0xfbc1fcea, 0x506c499a, 0x47f20f37, 0x710777cf, 0x7867e7ba,
        0x42d67caa, 0x7f2bd11a, 0x67a216ca, 0x0e986d40, 0x4ae8d4c0, 0xe36778b4, 0x5a958415, 0xdbfe2be8, 0x20faa1aa, 0x7132a6b8, 0xf4de4b7f, 0x55991aa1,
        0xa849d5aa, 0x49577832, 0xb92f3daa, 0x399cc526, 0x22cdd000, 0xe1977a29, 0x37c5db55, 0xb0177e1e, 0x2a1016aa, 0xa300086e, 0x3bab9bfe, 0x262eece0,
        0x23771aa9, 0x0a5dc91c, 0x31b902ae, 0x58e64bf8, 0x259c300e, 0xa57705c8, 0x284a6541, 0xfb229c60, 0x1bacaa9e, 0xbcab3350, 0x67cd9fbe, 0x8f135e60,
        0xf8c26a40, 0x6770da61, 0x3e1febfd, 0xd0c70803, 0x8dbb037b, 0x6f6fa4c5, 0x6749d10d, 0x7f51598d, 0xd5e5a4ec, 0x207dfa91, 0x6558ebc2, 0xbe594639,
        0x617a9e5d, 0x3c497950, 0x0844eb3f, 0x04207ed2, 0xd27ceac4, 0x70d20f05, 0xb47003f6, 0xe9e4e3ae, 0xcc19d265, 0x326dd956, 0xf0d3a327, 0xb71275f0,
        0x7994e285, 0x0aff594d, 0x128e8345, 0x7bda597a, 0xd634c380, 0xe24a006f, 0x8974818b, 0x3320b22a, 0xbe808417, 0xa2d5648a, 0x6fe80b39, 0x23ebad41,
        0xa2d11e5d, 0x027d9397, 0x9a722ac8, 0x432c7dac, 0x7146692a, 0x82a3dc77, 0xd66887c5, 0x831f43b7, 0x4e5dddde, 0x03793f81, 0x306ab437, 0x00ccf86c,
        0xfc6ed3d2, 0x076b8fdb, 0x9808cfca, 0x0f4eef6e, 0xc8cc3830, 0x104ac16a, 0xa189eff4, 0x3e085c08, 0xd28baf88, 0x5c853ac4, 0xe60480f8, 0xc51acd98,
        0x691e5ee1, 0x333feeb9, 0x1e35bc33, 0xec4a4642, 0xee57c5a5, 0xbeeb51f7, 0xe0c4f32c, 0xa5422f6a, 0x1d266d12, 0x3752fd3b, 0xfbc53c7d, 0x2421a4a3,
        0xcdc6a2de, 0x26e6b330, 0x6c073d46, 0x058e2f27, 0x43833f30, 0x46d1382e, 0x5f0804ec,
    ];

    private readonly int _start;
    private readonly byte[] _plain;

    internal Sr2SubIfd(int start, byte[] plain, ByteOrder order)
    {
        _start = start;
        _plain = plain;
        Order = order;
    }

    /// <summary>該目錄數值位元組序（同檔案）。</summary>
    internal ByteOrder Order { get; }

    /// <summary>解密後的區塊（從 entry count 起的目錄）與其檔案偏移（數值偏移的基址）。</summary>
    internal (byte[] Block, int Start) RawBlock => (_plain, _start);

    /// <summary>
    /// 由 IFD0 的 <c>DNGPrivateData</c> 找到並解密檔案的 <c>SR2SubIFD</c>；沒有、越界、大於 <see cref="MaxLen"/> 時回傳 null。
    /// </summary>
    internal static Sr2SubIfd? Read(ReadOnlyMemory<byte> bytes, Ifd ifd0, ByteOrder order)
    {
        var data = bytes.Span;
        var at = order.ReadU32(ifd0.Bytes(Tags.DngPrivateData) ?? [], 0);
        if (at is null) return null;
        var fields = PrivateFields(bytes, (long)at.Value, order);
        if (fields.Offset is not { } start || fields.Length is not { } len || fields.Key is not { } key) return null;
        if (len > MaxLen) return null;
        var end = checked((long)start + len);
        if (end > data.Length || end < start) return null;
        return DecryptKeyed(data.Slice(start, len), start, order, key);
    }

    /// <summary>解密檔案偏移 <paramref name="start"/> 的加密目錄 <paramref name="block"/>（測試與上游測試俱用預設金鑰）。</summary>
    internal static Sr2SubIfd Decrypt(ReadOnlySpan<byte> block, int start, ByteOrder order) => DecryptKeyed(block, start, order, DefaultKey);

    /// <summary>以檔案存的金鑰（tag 0x7221）解密。</summary>
    internal static Sr2SubIfd DecryptKeyed(ReadOnlySpan<byte> block, int start, ByteOrder order, ReadOnlySpan<byte> key)
    {
        var plain = block.ToArray();
        ApplyKeystreamWith(plain, SeedFor(key));
        return new Sr2SubIfd(start, plain, order);
    }

    /// <summary>某 SHORT 或 SSHORT 項目的欄位類型與數值位元組；缺標籤、型別不符或數值越界時回傳 null。</summary>
    internal (ushort Type, byte[] Values)? ShortBytes(ushort tag)
    {
        var data = _plain.AsSpan();
        var o = Order;
        var entries = o.ReadU16(data, 0);
        if (entries is null) return null;
        for (var i = 0; i < entries.Value; i++)
        {
            var at = 2 + 12 * i;
            if (at + 12 > data.Length) break; // entry count past the block end: scan stops where the block ends
            if (o.ReadU16(data, at) is not { } t || t != tag) continue;
            var kind = o.ReadU16(data, at + 2);
            if (kind is not 3 and not 8) return null;
            var count = o.ReadU32(data, at + 4);
            if (count is not { } c || c > int.MaxValue) return null;
            var size = checked((long)c * 2);
            byte[] values;
            if (size <= 4)
            {
                values = data.Slice(at + 8, (int)size).ToArray();
            }
            else
            {
                var off = o.ReadU32(data, at + 8);
                if (off is not { } v) return null;
                var rel = (long)v - _start;
                if (rel < 0 || rel + size > data.Length) return null;
                values = data.Slice((int)rel, (int)size).ToArray();
            }
            return (kind.Value, values);
        }
        return null;
    }

    /// <summary>SHORT 或 SSHORT 項目的全部數值（SSHORT 有號）。</summary>
    internal double[]? Shorts(ushort tag)
    {
        if (ShortBytes(tag) is not { } e) return null;
        var o = Order;
        var values = e.Values;
        var result = new double[values.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var v = o.ReadU16(values, 2 * i) ?? 0;
            result[i] = e.Type == 8 ? unchecked((short)v) : v;
        }
        return result;
    }

    /// <summary>對 <paramref name="block"/> 施加密鑰流（從 <paramref name="seed"/> 起），加密與解密相同。</summary>
    internal static void ApplyKeystreamWith(byte[] block, uint[] seed)
    {
        var ring = (uint[])seed.Clone();
        for (var n = 0; n + 4 <= block.Length; n += 4)
        {
            var w = n / 4; // 字索引；ring[i] 在更新前是 k[w−127]
            var i = w % 127;
            if (w >= 127) ring[i] ^= ring[(i + 64) % 127];
            var wv = ring[i];
            block[n] ^= (byte)(wv >> 24);
            block[n + 1] ^= (byte)(wv >> 16);
            block[n + 2] ^= (byte)(wv >> 8);
            block[n + 3] ^= (byte)wv;
        }
    }

    /// <summary>從金鑰（tag 0x7221 的小端 u32）派生密鑰流前 127 字（上游 seed_from_key）。</summary>
    internal static uint[] SeedFromKey(uint key)
    {
        var p = new uint[128];
        var k = key;
        for (var i = 0; i < 4; i++)
        {
            k = unchecked(k * 48828125u + 1u);
            p[i] = k;
        }
        p[3] = (p[3] << 1) | ((p[0] ^ p[2]) >> 31);
        for (var i = 4; i < 127; i++)
            p[i] = ((p[i - 4] ^ p[i - 2]) << 1) | ((p[i - 3] ^ p[i - 1]) >> 31);
        var seed = new uint[127];
        for (var j = 0; j < 127; j++)
        {
            var i = 127 + j;
            p[i & 127] = p[(i + 1) & 127] ^ p[(i + 65) & 127];
            seed[j] = p[i & 127];
        }
        return seed;
    }

    /// <summary>檔案存的金鑰對應的密鑰流種子：預設金鑰用量測 <see cref="Seed"/>，其餘派生。</summary>
    internal static uint[] SeedFor(ReadOnlySpan<byte> key) =>
        key.Length == 4 && key.SequenceEqual(DefaultKey) ? Seed :
        SeedFromKey(key.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(key[..4]) : ReadPadded(key));

    /// <summary>短金鑰補零至 4 位元組（上游型別保證 4；此處避免 span 越界）。</summary>
    private static uint ReadPadded(ReadOnlySpan<byte> key)
    {
        Span<byte> b = stackalloc byte[4];
        key.CopyTo(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    internal readonly record struct PrivateFieldsResult(int? Offset, int? Length, byte[]? Key);

    /// <summary>
    /// 自 <c>SR2Private</c> 目錄（<paramref name="at"/> 處）取得區塊的檔案偏移、長度與金鑰，
    /// 與上游 TIFF 讀取器的選取規則一致：目錄佈局與條目上限、可讀條目、值預算、各欄位取第一個可讀者。
    /// </summary>
    internal static PrivateFieldsResult PrivateFields(ReadOnlyMemory<byte> bytes, long at, ByteOrder order)
    {
        var data = bytes.Span;
        var opts = ParseOptions.Default;
        DirectoryEntries dir;
        try { dir = TiffFile.DirectoryEntriesOf(bytes, at, order, false, opts.MaxEntries); }
        catch (RawException) { return default; }

        var budget = opts.BudgetFor(data.Length);
        int? offset = null, length = null;
        byte[]? key = null;
        // 各欄位取第一個可讀者：值解不出仍佔位，後面的同名字段不再補上（上游 Option<Option<..>> flatten）。
        var (haveOffset, haveLength, haveKey) = (false, false, false);
        // 目錄次序（非排序去重）逐條看：各欄位取第一個可讀者；只有條目跨距通過才解碼，
        // 惡意目錄無法在區塊長度檢查前逼出以其 count 為尺寸的分配。
        foreach (var p in dir.Positions())
        {
            var span = TiffFile.EntrySpanAt(bytes, p, order, false, 0);
            if (span is not { } s || s.Length > budget) continue;
            budget -= s.Length;
            switch (s.Tag)
            {
                case SubIfdOffset when !haveOffset:
                    haveOffset = true;
                    offset = FirstNumber(bytes.Span, s, order);
                    break;
                case SubIfdLength when !haveLength:
                    haveLength = true;
                    length = FirstNumber(bytes.Span, s, order);
                    break;
                case SubIfdKey when !haveKey:
                    haveKey = true;
                    key = FourBytes(bytes.Span, s, order);
                    break;
            }
        }
        return new PrivateFieldsResult(offset, length, key);

        /// <summary>條目第一個數值（無號或非負整數）；count 0 或越界回傳 null（上游 <c>first_number</c>）。</summary>
        static int? FirstNumber(ReadOnlySpan<byte> bytes, EntrySpan s, ByteOrder order)
        {
            if (s.Count == 0 || s.Start < 0 || s.Start > bytes.Length - s.Type.Size()) return null;
            var first = bytes.Slice((int)s.Start, s.Type.Size());
            return TiffValue.Decode(order, s.Type, first).GetU64(0) is { } v && v <= int.MaxValue ? (int)v : null;
        }

        /// <summary>條目經讀取器 bytes 查詢恰為 4 位元組時的值：解碼前 5 個值位元組（上游 <c>four_bytes</c>）。</summary>
        static byte[]? FourBytes(ReadOnlySpan<byte> bytes, EntrySpan s, ByteOrder order)
        {
            var n = s.Length < 5 ? (int)s.Length : 5;
            if (s.Start < 0 || s.Start > bytes.Length - n) return null;
            var head = bytes.Slice((int)s.Start, n);
            return TiffValue.Decode(order, s.Type, head).AsBytes() is { Length: 4 } b ? b : null;
        }
    }
}
