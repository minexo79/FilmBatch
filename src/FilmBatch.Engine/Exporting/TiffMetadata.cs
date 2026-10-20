using System.Buffers.Binary;
using FilmBatch.Raw.Meta;
namespace FilmBatch.Engine.Exporting;

/// <summary>
/// 影像編碼器寫 TIFF 像素/ICC 但不保證 EXIF/GPS 目錄：Append 把 canonical 目錄併入暫存輸出；
/// 壓縮條帶與其位移保持不動。Normalize 邊界內正規化來源目錄，不複製影像容器或過時色彩宣告。
/// canonical EXIF 位元組一律為 <c>Exif\0\0</c> 標頭＋小端 TIFF 目錄。
/// </summary>
internal static class TiffMetadata
{
    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint Value);
    /// <summary>只讀整檔 TIFF 的 metadata 目錄（絕不讀可能很大的像素條帶），回傳 canonical EXIF 位元組。</summary>
    public static byte[] Read(string path, uint width, uint height)
    {
        using var file = File.OpenRead(path);
        return Normalize(file, width, height, omitMakerNote: false);
    }

    /// <summary>RAW 容器是 TIFF 結構（ORF <c>IIRO</c>/<c>MMOR</c>、RW2 <c>IIU</c>、DNG/CR2/NEF/…）時直接讀原檔目錄;
    /// CR3 傳入合併後的 TIFF 位元組。走同一條正規化路徑，不經相機 JPEG 預覽。
    /// 解碼後的 <paramref name="decoded"/>（通常已合併廠商私有值）用來補齊來源缺少的標準欄位；
    /// 來源既有值與 GPS 一律保留。RAW 的 MakerNote 位移以來源檔為原點、無法搬移，一律省略。</summary>
    public static byte[] ReadRaw(string path, uint width, uint height, Metadata? decoded = null)
    {
        using var file = FilmBatch.Engine.Imaging.Raw.RawSourceLoader.OpenExifTiff(path)
            ?? throw new InvalidDataException("RAW file carries no TIFF-structured EXIF block.");
        return Normalize(file, width, height, omitMakerNote: true, decoded);
    }

    /// <summary><paramref name="source"/> 為 canonical EXIF 位元組（<c>Exif\0\0</c>＋TIFF；null 產生全新輸出目錄）。</summary>
    public static byte[] Normalize(byte[]? source, uint width, uint height, bool omitMakerNote = false, Metadata? decoded = null)
    {
        using var file = source is null
            ? new MemoryStream("II\x2a\0\x08\0\0\0\0\0\0\0\0\0"u8.ToArray())
            : source.AsSpan().StartsWith("Exif\0\0"u8)
                ? new MemoryStream(source, 6, source.Length - 6, writable: false)
                : throw new InvalidDataException("Invalid EXIF profile header.");
        return Normalize(file, width, height, omitMakerNote, decoded);
    }

    private static byte[] Normalize(Stream file, uint width, uint height, bool omitMakerNote, Metadata? decoded = null)
    {
        Span<byte> header = stackalloc byte[16];
        file.ReadExactly(header[..8]);
        var bigEndian = header[0] == 'M' && header[1] == 'M';
        if (!bigEndian && !(header[0] == 'I' && header[1] == 'I'))
            throw new InvalidDataException("Invalid TIFF byte order.");
        ulong Number(ReadOnlySpan<byte> value) => value.Length switch
        {
            2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(value) : BinaryPrimitives.ReadUInt16LittleEndian(value),
            4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value),
            8 => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(value) : BinaryPrimitives.ReadUInt64LittleEndian(value),
            _ => throw new InvalidDataException("Invalid TIFF integer width.")
        };
        var magic = Number(header.Slice(2, 2));
        var large = magic == 43;
        // RW2／ORF 的 magic 不同，但 IFD 仍是 classic TIFF；與 RawSourceLoader／TiffFile 的辨識規則一致。
        if (magic is not (42 or 43 or 0x4f52 or 0x5352 or 0x0055))
            throw new InvalidDataException("Invalid TIFF version.");
        if (large)
        {
            if (Number(header.Slice(4, 2)) != 8 || Number(header.Slice(6, 2)) != 0)
                throw new InvalidDataException("Invalid BigTIFF header.");
            file.ReadExactly(header[8..]);
        }
        var first = large ? Number(header[8..]) : Number(header.Slice(4, 4));
        using var metadata = new MemoryStream();
        using var writer = new BinaryWriter(metadata);
        writer.Write("II\x2a\0\0\0\0\0"u8);

        uint Copy(ulong offset, int depth, ushort kind)
        {
            if (depth > 2 || offset > (ulong)file.Length)
                throw new InvalidDataException("Invalid TIFF metadata directory.");
            file.Position = checked((long)offset);
            Span<byte> entryBytes = stackalloc byte[20];
            var countSize = large ? 8 : 2;
            file.ReadExactly(entryBytes[..countSize]);
            var count = Number(entryBytes[..countSize]);
            if (count > 4096) throw new InvalidDataException("TIFF metadata directory is too large.");
            var entries = new List<Entry>();
            for (ulong i = 0; i < count; i++)
            {
                file.Position = checked((long)(offset + (ulong)countSize + i * (large ? 20ul : 12ul)));
                file.ReadExactly(entryBytes[..(large ? 20 : 12)]);
                var tag = (ushort)Number(entryBytes[..2]);
                // Only descriptive IFD0 fields and EXIF/GPS pointers belong in a rendered image.
                // In particular, Photoshop ImageSourceData (37724) contains obsolete source layers.
                if (depth == 0 && tag is not (269 or 270 or 271 or 272 or 274 or 305 or 306
                    or 315 or 316 or 33432 or 34665 or 34853))
                    continue;
                // Source interoperability may describe Adobe RGB (R03), or have a stale pointer.
                // Our output is sRGB: regenerate this directory instead of following/copying it.
                if (kind == 34665 && tag == 40965) continue;
                if (omitMakerNote && kind == 34665 && tag == 37500) continue;
                var type = (ushort)Number(entryBytes.Slice(2, 2));
                var components = Number(entryBytes.Slice(4, large ? 8 : 4));
                var raw = entryBytes.Slice(large ? 12 : 8, large ? 8 : 4);
                uint value;
                if (tag is 34665 or 34853)
                {
                    // Some writers emit SHORT pointers; accept count-1 SHORT/LONG/IFD and canonicalize.
                    if (components != 1 || type is not (3 or 4 or 13 or 18))
                        throw new InvalidDataException("Invalid TIFF EXIF pointer.");
                    // SHORT offsets occupy the first two value bytes; LONG fills the field.
                    value = Copy(Number(raw[..(type == 3 ? 2 : raw.Length)]), depth + 1, tag);
                    type = 4;
                }
                else if (!TryCopyValue(tag, type, components, raw, file, metadata, writer, entryBytes, bigEndian, large, out value, out var fixedType, out var fixedCount))
                {
                    // A single uncoercible standard value must not discard the whole profile;
                    // damaged-metadata exports still carry pixels and report a metadata warning.
                    continue;
                }
                else
                {
                    type = fixedType;
                    components = fixedCount;
                }
                entries.Add(new Entry(tag, type, checked((uint)components), value));
            }
            if (kind == 34665)
            {
                entries.RemoveAll(entry => entry.Tag is 40961 or 40962 or 40963);
                entries.AddRange(OutputExif(width, height, writer));
                if (decoded is not null) Backfill(entries, decoded, Exif: true, writer);
            }
            if (depth == 0)
            {
                entries.RemoveAll(entry => entry.Tag == 274);
                entries.Add(new Entry(274, 3, 1, 1));
                if (!entries.Any(entry => entry.Tag == 34665))
                {
                    var exif = OutputExif(width, height, writer).ToList();
                    if (decoded is not null) Backfill(exif, decoded, Exif: true, writer);
                    entries.Add(new Entry(34665, 4, 1, WriteDirectory(exif, writer)));
                }
                if (decoded is not null) Backfill(entries, decoded, Exif: false, writer);
            }
            return WriteDirectory(entries, writer);
        }

        var root = Copy(first, 0, 0);
        metadata.Position = 4;
        writer.Write(root);
        var result = new byte[checked((int)metadata.Length + 6)];
        "Exif\0\0"u8.CopyTo(result);
        metadata.GetBuffer().AsSpan(0, (int)metadata.Length).CopyTo(result.AsSpan(6));
        return result;
    }
    /// <summary>CR3 合併 EXIF（<c>CMT1</c> IFD0＋<c>CMT2</c> Exif IFD＋<c>CMT4</c> GPS IFD；上游 <c>cr3::merged_exif</c> 的正規化版本）。
    /// 每個區塊以自己的位元組序解析、輸出統一為小端序；懸空指標／位移標籤不合併；IFD0 白名單、互通性重建、方向與尺寸覆寫沿用正規化路徑。</summary>
    internal static byte[] NormalizeCr3(IReadOnlyList<ReadOnlyMemory<byte>?> blocks, uint width, uint height, Metadata? decoded = null)
    {
        if (blocks.Count == 0 || blocks[0] is not { Length: >= 8 })
            throw new InvalidDataException("CR3 file carries no CMT1 metadata block.");
        using var metadata = new MemoryStream();
        using var writer = new BinaryWriter(metadata);
        writer.Write("II\x2a\0\0\0\0\0"u8);
        var state = new Cr3State(blocks, width, height, writer, decoded);
        var rootBlock = blocks[0]!.Value.ToArray();
        var root = state.CopyRoot(rootBlock);
        metadata.Position = 4;
        writer.Write(root);
        var result = new byte[checked((int)metadata.Length + 6)];
        "Exif\0\0"u8.CopyTo(result);
        metadata.GetBuffer().AsSpan(0, (int)metadata.Length).CopyTo(result.AsSpan(6));
        return result;
    }

    /// <summary>CR3 合併用的正規化狀態：一次只持有一個區塊位元組（<see cref="ReadOnlySpan{T}"/> 不可為欄位）。</summary>
    private sealed class Cr3State(IReadOnlyList<ReadOnlyMemory<byte>?> blocks, uint width, uint height, BinaryWriter writer, Metadata? decoded)
    {
        internal static bool IsBig(byte[] block)
        {
            if (block.Length >= 2 && block[0] == (byte)'M' && block[1] == (byte)'M') return true;
            if (block.Length >= 2 && block[0] == (byte)'I' && block[1] == (byte)'I') return false;
            throw new InvalidDataException("Invalid TIFF byte order.");
        }

        internal static uint FirstOffset(byte[] block)
        {
            if (block.Length < 8)
                throw new InvalidDataException("Invalid TIFF byte order.");
            var big = IsBig(block);
            var magic = big ? BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(2, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2, 2));
            if (magic != 42)
                throw new InvalidDataException("Invalid TIFF version.");
            return big ? BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4, 4));
        }

        public uint CopyRoot(byte[] block) => CopyDirectory(block, IsBig(block), FirstOffset(block), 0, 0);

        private uint CopyDirectory(byte[] block, bool bigEndian, ulong offset, int depth, ushort kind)
        {
            uint Number(ReadOnlySpan<byte> value) => value.Length switch
            {
                2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(value) : BinaryPrimitives.ReadUInt16LittleEndian(value),
                4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value),
                _ => throw new InvalidDataException("Invalid TIFF integer width.")
            };
            if (depth > 2 || offset > (ulong)block.Length)
                throw new InvalidDataException("Invalid TIFF metadata directory.");
            var at = checked((int)offset);
            if (block.Length - at < 2)
                throw new InvalidDataException("Invalid TIFF metadata directory.");
            var count = Number(block.AsSpan(at, 2));
            if (count > 4096) throw new InvalidDataException("TIFF metadata directory is too large.");
            if ((ulong)block.Length - (ulong)at - 2 < (ulong)count * 12)
                throw new InvalidDataException("Invalid TIFF metadata directory.");
            var entries = new List<Entry>();
            Span<byte> entryBytes = stackalloc byte[12];
            for (uint i = 0; i < count; i++)
            {
                block.AsSpan(at + 2 + (int)i * 12, 12).CopyTo(entryBytes);
                var tag = (ushort)Number(entryBytes[..2]);
                var type = (ushort)Number(entryBytes.Slice(2, 2));
                var components = Number(entryBytes.Slice(4, 4));
                var raw = entryBytes.Slice(8, 4);
                uint value;
                if (tag is 34665 or 34853)
                {
                    // 子目錄內的指標（上游 SKIP 的 EXIF_IFD／GPS_IFD）跟隨會懸空，一律跳過；
                    // 根目錄的指標值是懸空的：下方改讀 CMT2／CMT4（上游 first(1)／first(3)）。
                    if (depth > 0)
                        continue;
                    if (components != 1 || type is not (4 or 13 or 18))
                        throw new InvalidDataException("Invalid TIFF EXIF pointer.");
                    var childSlot = tag == 34665 ? 1 : 3;
                    if (childSlot >= blocks.Count || blocks[childSlot] is not { Length: >= 8 } child)
                        continue;
                    var childBytes = child.ToArray();
                    value = CopyDirectory(childBytes, IsBig(childBytes), FirstOffset(childBytes), depth + 1, tag);
                    type = 4;
                    entries.Add(new Entry(tag, type, components, value));
                    continue;
                }
                else
                {
                    // 上游 SKIP：合併後會懸空的位移標籤（INTEROP_IFD、SUB_IFDS、MAKER_NOTE、273、279、513、514）。
                    if (tag is 40965 or 330 or 37500 or 273 or 279 or 513 or 514)
                        continue;
                    // 只有描述性 IFD0 欄位屬於輸出影像（沿用正規化路徑的白名單）。
                    if (depth == 0 && tag is not (269 or 270 or 271 or 272 or 274 or 305 or 306
                        or 315 or 316 or 33432))
                        continue;
                    // 來源互通性可能描述 Adobe RGB（R03）或指標已損毀；輸出為 sRGB，一律重新產生。
                    var size = FieldSize(type);
                    var length = checked((long)components * size);
                    var output = writer.BaseStream;
                    if (length < 0 || length > 16 * 1024 * 1024 - output.Length)
                        throw new InvalidDataException("TIFF EXIF exceeds the 16 MiB metadata limit.");
                    var payload = new byte[length];
                    if (length <= 4) raw[..(int)length].CopyTo(payload);
                    else
                    {
                        var position = Number(raw);
                        if ((ulong)position + (ulong)length > (ulong)block.Length)
                            throw new InvalidDataException("Invalid TIFF metadata directory.");
                        block.AsSpan((int)position, (int)length).CopyTo(payload);
                    }
                    var unit = type is 5 or 10 ? 4 : (int)size;
                    if (bigEndian && unit > 1)
                        for (var j = 0; j < payload.Length; j += unit) payload.AsSpan(j, unit).Reverse();
                    if (!TryFixValue(tag, type, components, payload, out var fixedType, out var fixedCount, out var fixedPayload))
                        continue;
                    value = EmitValue(writer, entryBytes, fixedPayload);
                    type = fixedType;
                    components = checked((uint)fixedCount);
                }
                entries.Add(new Entry(tag, type, components, value));
            }
            if (kind == 34665)
            {
                entries.RemoveAll(entry => entry.Tag is 40961 or 40962 or 40963);
                entries.AddRange(OutputExif(width, height, writer));
                if (decoded is not null) Backfill(entries, decoded, Exif: true, writer);
            }
            if (depth == 0)
            {
                entries.RemoveAll(entry => entry.Tag == 274);
                entries.Add(new Entry(274, 3, 1, 1));
                // 上游 set_child：無論 CMT1 是否帶指標，CMT2／CMT4 的第一個 IFD 都掛為 Exif／GPS 子目錄。
                AttachChild(entries, 34665, 1, depth);
                AttachChild(entries, 34853, 3, depth);
                if (!entries.Any(entry => entry.Tag == 34665))
                {
                    // 同正規化路徑：CMT2 缺席時以 decoded 補齊標準 Exif 欄位。
                    var exif = OutputExif(width, height, writer).ToList();
                    if (decoded is not null) Backfill(exif, decoded, Exif: true, writer);
                    entries.Add(new Entry(34665, 4, 1, WriteDirectory(exif, writer)));
                }
                if (decoded is not null) Backfill(entries, decoded, Exif: false, writer);
            }
            return WriteDirectory(entries, writer);
        }

        private void AttachChild(List<Entry> entries, ushort tag, int childSlot, int depth)
        {
            if (entries.Any(entry => entry.Tag == tag)) return;
            if (childSlot >= blocks.Count || blocks[childSlot] is not { Length: >= 8 } child) return;
            var childBytes = child.ToArray();
            var value = CopyDirectory(childBytes, IsBig(childBytes), FirstOffset(childBytes), depth + 1, tag);
            entries.Add(new Entry(tag, 4, 1, value));
        }
    }


    /// <summary>Appends canonical EXIF/GPS to encoder output.
    /// With <paramref name="replaceExif"/>, the writer's own 34665/34853 pointers are replaced by
    /// the canonical copies (for DNG renders whose writer EXIF lacks source standard fields);
    /// encoder-owned image and DNG tags are otherwise untouched.
    /// <paramref name="profile"/>（canonical <c>Exif\0\0</c> 位元組）在併入前以輸出檔自身尺寸
    /// 再經 <see cref="Normalize(byte[]?, uint, uint, bool, Metadata?)"/> 正規化一次。</summary>
    public static void Append(string path, byte[] profile, bool replaceExif = false)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        var byteOrder = reader.ReadUInt16();
        var little = byteOrder == 0x4949;
        ushort Read16() => little ? reader.ReadUInt16() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
        uint Read32() => little ? reader.ReadUInt32() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
        if (byteOrder is not (0x4949 or 0x4D4D) || Read16() != 42)
            throw new InvalidDataException("Expected classic TIFF output.");
        file.Position = Read32();
        var count = Read16();
        var entries = new Dictionary<ushort, Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = new Entry(Read16(), Read16(), Read32(), Read32());
            entries.Add(entry.Tag, entry);
        }
        if (Read32() != 0)
            throw new InvalidDataException("Expected single-frame TIFF output.");

        // Re-canonicalize against the appended file's own dimensions.
        var width = Scalar(entries, 256, little);
        var height = Scalar(entries, 257, little);
        if (width is null || height is null)
            throw new InvalidDataException("Expected TIFF output with IFD0 dimensions.");

        // Canonical EXIF is little-endian; ImageIO may emit big-endian TIFF pixels.
        var bytes = Normalize(profile, width.Value, height.Value);
        var data = bytes.AsSpan(6);
        if (!bytes.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8) ||
            data.Length < 8 || U16(data, 0) != 0x4949 || U16(data, 2) != 42)
            throw new InvalidDataException("Expected canonical little-endian EXIF.");

        file.Position = file.Length;
        if (replaceExif)
        {
            entries.Remove(34665);
            entries.Remove(34853);
        }
        foreach (var entry in CopyDirectory(data, U32(data, 4), writer, depth: 0, little))
        {
            // The encoder owns dimensions, orientation, compression, ICC, and pixel offsets.
            // Never introduce an old image/thumbnail pointer from source metadata.
            if (entry.Tag is 273 or 288 or 324 or 330 or 513) continue;
            if (replaceExif && entry.Tag is 34665 or 34853)
            {
                entries[entry.Tag] = entry;
                continue;
            }
            entries.TryAdd(entry.Tag, entry);
        }
        var root = WriteDirectory(entries.Values, writer, little);
        file.Position = 4;
        Write32(writer, root, little);
    }

    private static uint? Scalar(IReadOnlyDictionary<ushort, Entry> entries, ushort tag, bool little)
    {
        if (!entries.TryGetValue(tag, out var entry) || entry.Count != 1)
            return null;
        // SHORT values sit in the first two bytes of the inline value field; LONG fills it.
        return entry.Type switch
        {
            3 => little ? entry.Value & 0xFFFF : entry.Value >> 16,
            4 => entry.Value,
            _ => null,
        };
    }

    private static Entry[] CopyDirectory(ReadOnlySpan<byte> data, uint offset, BinaryWriter writer, int depth, bool little = true)
    {
        if (depth > 2) throw new InvalidDataException("Unexpected nested EXIF directory.");
        var start = checked((int)offset);
        var count = U16(data, start);
        var entries = new List<Entry>(count);
        Span<byte> inline = stackalloc byte[4];
        for (var i = 0; i < count; i++)
        {
            var pos = checked(start + 2 + i * 12);
            var tag = U16(data, pos);
            var type = U16(data, pos + 2);
            var components = U32(data, pos + 4);
            var value = U32(data, pos + 8);
            // Re-serialized profiles can carry misaligned entries (e.g. field type 0
            // where a stale offset lands); skip unreadable entries, keep the rest.
            if (type is 0 or > 12) continue;
            var length = (ulong)components * FieldSize(type);
            if (tag is 34665 or 34853 or 40965)
            {
                if (type != 4 || components != 1)
                    throw new InvalidDataException("Invalid EXIF directory pointer.");
                value = WriteDirectory(CopyDirectory(data, value, writer, depth + 1, little), writer, little);
            }
            else if (length > 4)
            {
                // Offsets from a re-serialized profile may dangle; skip, don't throw.
                if (length > (ulong)data.Length || value > (ulong)data.Length - length) continue;
                var payload = data.Slice(checked((int)value), checked((int)length));
                value = Align(writer);
                if (little) writer.Write(payload);
                else
                {
                    var converted = payload.ToArray();
                    ReverseComponents(converted, type);
                    writer.Write(converted);
                }
            }
            else if (!little)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(inline, value);
                ReverseComponents(inline[..checked((int)length)], type);
                value = BinaryPrimitives.ReadUInt32BigEndian(inline);
            }
            entries.Add(new Entry(tag, type, components, value));
        }
        // Normalize omits the source thumbnail chain. A re-serialized profile can
        // carry a dangling (out-of-range) next-pointer where a nested directory was
        // relocated; only a real in-range chain is unexpected.
        var next = U32(data, checked(start + 2 + count * 12));
        if (next != 0 && (ulong)next < (ulong)data.Length)
            throw new InvalidDataException("Unexpected EXIF thumbnail directory.");
        return entries.ToArray();
    }

    private static uint WriteDirectory(IEnumerable<Entry> entries, BinaryWriter writer, bool little = true)
    {
        var sorted = entries.OrderBy(entry => entry.Tag).ToArray();
        var offset = Align(writer);
        Write16(writer, checked((ushort)sorted.Length), little);
        foreach (var entry in sorted)
        {
            Write16(writer, entry.Tag, little);
            Write16(writer, entry.Type, little);
            Write32(writer, entry.Count, little);
            Write32(writer, entry.Value, little);
        }
        Write32(writer, 0u, little);
        return offset;
    }

    private static void Write16(BinaryWriter writer, ushort value, bool little) =>
        writer.Write(little ? value : BinaryPrimitives.ReverseEndianness(value));

    private static void Write32(BinaryWriter writer, uint value, bool little) =>
        writer.Write(little ? value : BinaryPrimitives.ReverseEndianness(value));

    private static void ReverseComponents(Span<byte> payload, ushort type)
    {
        var size = type switch { 3 or 8 => 2, 4 or 5 or 9 or 10 or 11 => 4, 12 => 8, _ => 1 };
        if (size == 1) return;
        for (var offset = 0; offset < payload.Length; offset += size)
            payload.Slice(offset, size).Reverse();
    }

    /// <summary>Canonical (type, fixed count) for standard EXIF/GPS tags. Count -1 keeps source values.</summary>
    private readonly record struct Spec(ushort Type, int Count);

    private static readonly Dictionary<ushort, Spec> Specs = new()
    {
        [271] = new(2, -1), [272] = new(2, -1), [305] = new(2, -1), [306] = new(2, 20),
        [315] = new(2, -1), [316] = new(2, -1), [33432] = new(2, -1),
        [33434] = new(5, 1), [33437] = new(5, 1), [34850] = new(3, 1), [34855] = new(3, -1),
        [34864] = new(3, 1), [34866] = new(4, 1), [36864] = new(7, 4),
        [36867] = new(2, 20), [36868] = new(2, 20),
        [36880] = new(2, -1), [36881] = new(2, -1), [36882] = new(2, -1),
        [37377] = new(10, 1), [37378] = new(5, 1), [37380] = new(10, 1), [37381] = new(5, 1),
        [37383] = new(3, 1), [37384] = new(3, 1), [37385] = new(3, 1), [37386] = new(5, 1),
        [37520] = new(2, -1), [37521] = new(2, -1), [37522] = new(2, -1),
        [37121] = new(7, 4), [37122] = new(5, 1), [40960] = new(7, 4),
        [41495] = new(3, 1), [41986] = new(3, 1), [41987] = new(3, 1), [41989] = new(3, 1),
        [41990] = new(3, 1), [41991] = new(3, 1), [41992] = new(3, 1), [41993] = new(3, 1),
        [41994] = new(3, 1), [42033] = new(2, -1), [42034] = new(5, 4),
        [42035] = new(2, -1), [42036] = new(2, -1), [42037] = new(2, -1),
        [0] = new(1, 4), [1] = new(2, -1), [2] = new(5, 3), [3] = new(2, -1),
        [4] = new(5, 3), [5] = new(1, 1), [6] = new(5, 1), [7] = new(5, 3),
        [16] = new(2, -1), [17] = new(5, 1), [29] = new(2, -1),
    };

    /// <summary>Copies one non-pointer value: bounded read, endian fix, spec coercion, ASCII
    /// sanitization, inline-or-offset emission. Returns false to drop just this entry.</summary>
    private static bool TryCopyValue(ushort tag, ushort type, ulong components, ReadOnlySpan<byte> raw,
        Stream file, MemoryStream metadata, BinaryWriter writer, Span<byte> entryBytes,
        bool bigEndian, bool large, out uint value, out ushort fixedType, out ulong fixedCount)
    {
        value = 0;
        fixedType = type;
        fixedCount = components;
        var size = FieldSize(type);
        int length;
        try { length = checked((int)(components * size)); }
        catch (OverflowException) { throw new InvalidDataException("TIFF EXIF exceeds the 16 MiB metadata limit."); }
        if (length < 0 || length > 16 * 1024 * 1024 - metadata.Length)
            throw new InvalidDataException("TIFF EXIF exceeds the 16 MiB metadata limit.");
        var payload = new byte[length];
        if (length <= raw.Length) raw[..length].CopyTo(payload);
        else
        {
            long position = large
                ? (long)(bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(raw) : BinaryPrimitives.ReadUInt64LittleEndian(raw))
                : Number32(raw, bigEndian);
            if (position < 0 || position > file.Length)
                throw new InvalidDataException("Invalid TIFF metadata directory.");
            file.Position = position;
            file.ReadExactly(payload);
        }
        var unit = type is 5 or 10 ? 4 : (int)size;
        if (bigEndian && unit > 1)
            for (var j = 0; j < payload.Length; j += unit) payload.AsSpan(j, unit).Reverse();
        if (!TryFixValue(tag, type, components, payload, out fixedType, out fixedCount, out var fixedPayload))
            return false;
        value = EmitValue(writer, entryBytes, fixedPayload);
        return true;
    }

    private static uint Number32(ReadOnlySpan<byte> value, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value);

    /// <summary>Emits a little-endian payload inline (≤4 bytes) or at an aligned offset.</summary>
    private static uint EmitValue(BinaryWriter writer, Span<byte> scratch, byte[] payload)
    {
        if (payload.Length > 4)
        {
            var offset = Align(writer);
            writer.Write(payload);
            return offset;
        }
        scratch[..4].Clear();
        payload.CopyTo(scratch);
        return U32(scratch, 0);
    }

    /// <summary>Coerces a value to its spec type/count and sanitizes ASCII at cause.
    /// Unknown tags pass through; unrepresentable standard values return false (drop).</summary>
    private static bool TryFixValue(ushort tag, ushort type, ulong components, byte[] payload,
        out ushort fixedType, out ulong fixedCount, out byte[] fixedPayload)
    {
        fixedType = type;
        fixedCount = components;
        fixedPayload = payload;
        if (components == 0) return false;
        if (type == 2)
        {
            var clean = SanitizeAscii(payload);
            if (clean.Length == 0) return false;
            fixedCount = (ulong)clean.Length + 1;
            fixedPayload = [.. clean, (byte)0];
            return true;
        }
        if (tag == 37500 || !Specs.TryGetValue(tag, out var spec)) return true;
        if (type == spec.Type && (spec.Count < 0 || components == (ulong)spec.Count)) return true;
        return TryConvert(spec, type, components, payload, out fixedType, out fixedCount, out fixedPayload);
    }

    private static byte[] SanitizeAscii(byte[] payload)
    {
        var end = Array.IndexOf(payload, (byte)0);
        return end < 0 ? [.. payload] : payload[..end];
    }

    private static bool TryConvert(Spec spec, ushort type, ulong components, byte[] payload,
        out ushort fixedType, out ulong fixedCount, out byte[] fixedPayload)
    {
        fixedType = spec.Type;
        fixedCount = spec.Count < 0 ? components : (ulong)spec.Count;
        fixedPayload = [];
        double Number(int index)
        {
            var unit = UnitOf(type);
            var at = checked(index * unit);
            if (at < 0 || at + unit > payload.Length) return double.NaN;
            return type switch
            {
                1 => payload[at],
                3 => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at, 2)),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at, 4)),
                6 => (sbyte)payload[at],
                8 => BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(at, 2)),
                9 => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at, 4)),
                11 => BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(at, 4)),
                12 => BinaryPrimitives.ReadDoubleLittleEndian(payload.AsSpan(at, 8)),
                16 => BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(at, 8)) is var v && v <= uint.MaxValue ? v : double.NaN,
                17 => BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(at, 8)),
                5 => payload.Length - at >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at + 4, 4)) is not 0
                    ? (double)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at, 4)) / BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at + 4, 4)) : double.NaN,
                10 => payload.Length - at >= 8 && BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at + 4, 4)) is not 0
                    ? (double)BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at, 4)) / BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at + 4, 4)) : double.NaN,
                _ => double.NaN,
            };
        }
        int UnitOf(ushort t) => t switch { 1 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, _ => 8 };
        if (type == 2)
        {
            var clean = SanitizeAscii(payload);
            if (spec.Type is 1 or 7 && spec.Count >= 0)
            {
                if (clean.Length != spec.Count) return false;
                fixedPayload = clean;
                return true;
            }
            if (spec.Type is 3 or 4 && spec.Count >= 0
                && int.TryParse(System.Text.Encoding.ASCII.GetString(clean), out var parsed))
            {
                fixedPayload = spec.Type == 3
                    ? U16Bytes(checked((ushort)Math.Clamp(parsed, 0, 65535)))
                    : U32Bytes(checked((uint)Math.Clamp((long)parsed, 0, uint.MaxValue)));
                fixedCount = 1;
                return true;
            }
            return false;
        }
        if (spec.Type is 1 or 7)
        {
            if (type is not (1 or 7) || spec.Count < 0 || (long)components != spec.Count) return false;
            return true;
        }
        var want = spec.Count < 0 ? checked((int)components) : spec.Count;
        if (want <= 0 || want > 64) return false;
        if (spec.Type is 3 or 4)
        {
            if (type is not (1 or 3 or 4 or 6 or 8 or 9 or 11 or 12 or 16 or 17 or 5 or 10)) return false;
            var output = new byte[want * (spec.Type == 3 ? 2 : 4)];
            for (var i = 0; i < want; i++)
            {
                var v = Number(i);
                if (double.IsNaN(v)) return false;
                var rounded = Math.Round(v);
                if (rounded < 0 || rounded > (spec.Type == 3 ? ushort.MaxValue : uint.MaxValue)) return false;
                if (spec.Type == 3)
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(i * 2, 2), (ushort)rounded);
                else
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4, 4), (uint)rounded);
            }
            fixedPayload = output;
            return true;
        }
        if (spec.Type is 5 or 10)
        {
            if (type is not (1 or 3 or 4 or 5 or 6 or 8 or 9 or 10 or 11 or 12 or 16 or 17)) return false;
            var output = new byte[want * 8];
            for (var i = 0; i < want; i++)
            {
                var v = Number(i);
                if (spec.Type == 5)
                {
                    if (!TryRational(v, out var num, out var den)) return false;
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 8, 4), num);
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 8 + 4, 4), den);
                }
                else
                {
                    if (!TrySRational(v, out var snum, out var sden)) return false;
                    BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(i * 8, 4), snum);
                    BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(i * 8 + 4, 4), sden);
                }
            }
            fixedPayload = output;
            return true;
        }
        return false;
    }

    private static bool TryRational(double value, out uint num, out uint den)
    {
        num = 0;
        den = 1;
        if (!double.IsFinite(value) || value <= 0) return false;
        den = 100_000;
        var scaled = Math.Round(value * den);
        if (scaled < 1)
        {
            den = 1_000_000;
            scaled = Math.Round(value * den);
        }
        if (scaled is < 1 or > uint.MaxValue) return false;
        num = (uint)scaled;
        var divisor = Gcd(num, den);
        num /= divisor;
        den /= divisor;
        return true;
    }

    private static bool TrySRational(double value, out int num, out int den)
    {
        num = 0;
        den = 1;
        if (!double.IsFinite(value) || Math.Abs(value) > int.MaxValue / 100_000) return false;
        den = 100_000;
        var scaled = Math.Round(value * den);
        if (scaled is < int.MinValue or > int.MaxValue) return false;
        num = (int)scaled;
        if (num != 0)
        {
            var divisor = Gcd((uint)Math.Abs((long)num), (uint)den);
            num /= (int)divisor;
            den /= (int)divisor;
        }
        return true;
    }

    private static uint Gcd(uint a, uint b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }

    private static byte[] U16Bytes(ushort value)
    {
        var output = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(output, value);
        return output;
    }

    private static byte[] U32Bytes(uint value)
    {
        var output = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(output, value);
        return output;
    }

    /// <summary>Backfills absent standard fields from decoded vendor metadata.
    /// Existing source values (including GPS) are never overwritten.</summary>
    private static void Backfill(List<Entry> entries, Metadata decoded, bool Exif, BinaryWriter writer)
    {
        var present = new HashSet<ushort>(entries.Select(entry => entry.Tag));
        byte[] scratch = new byte[4];
        void Add(ushort tag, ushort type, byte[] payload)
        {
            if (!present.Add(tag)) return;
            var unit = type is 5 or 10 ? 8 : type is 3 or 8 ? 2 : type is 4 or 9 or 11 ? 4 : 1;
            entries.Add(new Entry(tag, type, checked((uint)(payload.Length / unit)), EmitValue(writer, scratch, payload)));
        }
        byte[]? Text(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var clean = value.Trim();
            var nul = clean.IndexOf('\0');
            if (nul >= 0) clean = clean[..nul];
            if (clean.Length == 0) return null;
            return [.. System.Text.Encoding.ASCII.GetBytes(clean), (byte)0];
        }
        if (!Exif)
        {
            if (Text(decoded.Make) is { } make) Add(271, 2, make);
            if (Text(decoded.Model) is { } model) Add(272, 2, model);
            if (Stamp(decoded.CaptureTime) is { } stamp) Add(306, 2, stamp);
            if (decoded.CaptureTime?.OffsetMinutes is { } ifdOffset) Add(36880, 2, OffsetBytes(ifdOffset));
            if (decoded.CaptureTime is { } ifdTime && ifdTime.Millis is > 0 and <= 999
                && Text(ifdTime.Millis.ToString("D3")) is { } ifdSubsec) Add(37520, 2, ifdSubsec);
            return;
        }
        if (decoded.Iso is > 0 and <= ushort.MaxValue) Add(34855, 3, U16Bytes((ushort)decoded.Iso));
        if (Positive(decoded.ExposureTime) is { } exposure && TryRational(exposure, out var expNum, out var expDen))
            Add(33434, 5, RationalBytes(expNum, expDen));
        if (Positive(decoded.FNumber) is { } fNumber && TryRational(fNumber, out var fNum, out var fDen))
            Add(33437, 5, RationalBytes(fNum, fDen));
        if (Positive(decoded.FocalLength) is { } focal && TryRational(focal, out var focNum, out var focDen))
            Add(37386, 5, RationalBytes(focNum, focDen));
        if (decoded.FocalLength35mm is { } focal35 && double.IsFinite(focal35))
        {
            var rounded = (int)Math.Round(focal35);
            if (rounded is > 0 and <= ushort.MaxValue) Add(41989, 3, U16Bytes((ushort)rounded));
        }
        if (decoded.ExposureBias is { } bias && double.IsFinite(bias) && TrySRational(bias, out var biasNum, out var biasDen))
            Add(37380, 10, SRationalBytes(biasNum, biasDen));
        if (Stamp(decoded.CaptureTime) is { } capture)
        {
            Add(36867, 2, capture);
            Add(36868, 2, capture);
        }
        if (decoded.CaptureTime?.OffsetMinutes is { } offset)
        {
            Add(36881, 2, OffsetBytes(offset));
            Add(36882, 2, OffsetBytes(offset));
        }
        if (decoded.CaptureTime is { } time && time.Millis is > 0 and <= 999
            && Text(time.Millis.ToString("D3")) is { } subsec)
        {
            Add(37521, 2, subsec);
            Add(37522, 2, subsec);
        }
        if (Text(decoded.SerialNumber) is { } serial) Add(42033, 2, serial);
        if (Text(decoded.LensMake) is { } lensMake) Add(42035, 2, lensMake);
        if (Text(decoded.LensModel) is { } lensModel) Add(42036, 2, lensModel);
        if (decoded.LensSpec is { Length: 4 } spec && spec.All(Positive))
        {
            var rats = new List<byte>();
            foreach (var component in spec)
            {
                if (!TryRational(component, out var num, out var den)) return;
                rats.AddRange(RationalBytes(num, den));
            }
            Add(42034, 5, [.. rats]);
        }
    }

    private static double? Positive(double? value) =>
        value is { } v && double.IsFinite(v) && v > 0 ? v : null;

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static byte[]? Stamp(RawDateTime? time)
    {
        if (time is not { } t) return null;
        return [.. System.Text.Encoding.ASCII.GetBytes(
            $"{t.Year:D4}:{t.Month:D2}:{t.Day:D2} {t.Hour:D2}:{t.Minute:D2}:{t.Second:D2}\0")];
    }

    private static byte[] OffsetBytes(int minutes)
    {
        var sign = minutes < 0 ? "-" : "+";
        var absolute = Math.Abs(minutes);
        return System.Text.Encoding.ASCII.GetBytes($"{sign}{absolute / 60:D2}:{absolute % 60:D2}\0");
    }

    private static byte[] RationalBytes(uint num, uint den)
    {
        var output = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), num);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), den);
        return output;
    }

    private static byte[] SRationalBytes(int num, int den)
    {
        var output = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0, 4), num);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4, 4), den);
        return output;
    }

    private static uint Align(BinaryWriter writer)
    {
        if ((writer.BaseStream.Position & 1) != 0) writer.Write((byte)0);
        return checked((uint)writer.BaseStream.Position);
    }


    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static uint FieldSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1u,
        3 or 8 => 2u,
        4 or 9 or 11 => 4u,
        5 or 10 or 12 => 8u,
        _ => throw new InvalidDataException($"Unsupported EXIF field type: {type}")
    };

    private static Entry[] OutputExif(uint width, uint height, BinaryWriter writer) =>
    [
        new(40961, 3, 1, 1), // sRGB
        new(40962, 4, 1, width),
        new(40963, 4, 1, height),
        new(40965, 4, 1, WriteDirectory(
        [
            new Entry(1, 2, 4, U32("R98\0"u8, 0)), // DCF basic file, matching sRGB
            new Entry(2, 7, 4, U32("0100"u8, 0))
        ], writer))
    ];
}
