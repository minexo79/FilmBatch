using System.Buffers.Binary;
using ImageMagick;

namespace FilmBatch.Engine.Exporting;

/// <summary>
/// Magick.NET 14 writes TIFF pixels/ICC but not the EXIF/GPS directories. Append those
/// directories to our temporary output; leave compressed strips and their offsets untouched.
/// Normalize bounded source directories without copying image containers or stale color declarations.
/// </summary>
internal static class TiffMetadata
{
    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint Value);
    /// <summary>Magick exposes TIFF EXIF as formatted attributes, not an ExifProfile.
    /// Read only metadata directories, never the (potentially very large) pixel strips.</summary>
    public static ExifProfile Read(string path, uint width, uint height)
    {
        using var file = File.OpenRead(path);
        return Normalize(file, width, height, omitMakerNote: false);
    }

    /// <summary>RAW 容器是 TIFF 結構（ORF <c>IIRO</c>/<c>MMOR</c>、RW2 <c>IIU</c>、DNG/CR2/NEF/…）時直接讀原檔目錄;
    /// CR3 傳入合併後的 TIFF 位元組。走同一條正規化路徑，不經相機 JPEG 預覽。</summary>
    public static ExifProfile ReadRaw(string path, uint width, uint height)
    {
        using var file = FilmBatch.Engine.Imaging.Raw.RawSourceLoader.OpenExifTiff(path)
            ?? throw new InvalidDataException("RAW file carries no TIFF-structured EXIF block.");
        return Normalize(file, width, height, omitMakerNote: false);
    }

    public static ExifProfile Normalize(IExifProfile? source, uint width, uint height, bool omitMakerNote = false)
    {
        var bytes = source?.ToByteArray();
        using var file = bytes is null
            ? new MemoryStream("II\x2a\0\x08\0\0\0\0\0\0\0\0\0"u8.ToArray())
            : bytes.AsSpan().StartsWith("Exif\0\0"u8)
                ? new MemoryStream(bytes, 6, bytes.Length - 6, writable: false)
                : throw new InvalidDataException("Invalid EXIF profile header.");
        return Normalize(file, width, height, omitMakerNote);
    }

    private static ExifProfile Normalize(Stream file, uint width, uint height, bool omitMakerNote)
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
                    if (components != 1 || type is not (4 or 13 or 18))
                        throw new InvalidDataException("Invalid TIFF EXIF pointer.");
                    value = Copy(Number(raw), depth + 1, tag);
                    type = 4;
                }
                else
                {
                    var size = FieldSize(type);
                    var length = checked((int)(components * size));
                    if (length < 0 || length > 16 * 1024 * 1024 - metadata.Length)
                        throw new InvalidDataException("TIFF EXIF exceeds the 16 MiB metadata limit.");
                    var payload = new byte[length];
                    if (length <= raw.Length) raw[..length].CopyTo(payload);
                    else
                    {
                        file.Position = checked((long)Number(raw));
                        file.ReadExactly(payload);
                    }
                    var unit = type is 5 or 10 ? 4 : (int)size;
                    if (bigEndian && unit > 1)
                        for (var j = 0; j < payload.Length; j += unit) payload.AsSpan(j, unit).Reverse();
                    if (length > 4)
                    {
                        value = Align(writer);
                        writer.Write(payload);
                    }
                    else
                    {
                        entryBytes[..4].Clear();
                        payload.CopyTo(entryBytes);
                        value = U32(entryBytes, 0);
                    }
                }
                entries.Add(new Entry(tag, type, checked((uint)components), value));
            }
            if (kind == 34665)
            {
                entries.RemoveAll(entry => entry.Tag is 40961 or 40962 or 40963);
                entries.AddRange(OutputExif(width, height, writer));
            }
            if (depth == 0)
            {
                entries.RemoveAll(entry => entry.Tag == 274);
                entries.Add(new Entry(274, 3, 1, 1));
                if (!entries.Any(entry => entry.Tag == 34665))
                    entries.Add(new Entry(34665, 4, 1, WriteDirectory(OutputExif(width, height, writer), writer)));
            }
            return WriteDirectory(entries, writer);
        }

        var root = Copy(first, 0, 0);
        metadata.Position = 4;
        writer.Write(root);
        var result = new byte[checked((int)metadata.Length + 6)];
        "Exif\0\0"u8.CopyTo(result);
        metadata.GetBuffer().AsSpan(0, (int)metadata.Length).CopyTo(result.AsSpan(6));
        return new ExifProfile(result);
    }
    /// <summary>CR3 合併 EXIF（<c>CMT1</c> IFD0＋<c>CMT2</c> Exif IFD＋<c>CMT4</c> GPS IFD；上游 <c>cr3::merged_exif</c> 的正規化版本）。
    /// 每個區塊以自己的位元組序解析、輸出統一為小端序；懸空指標／位移標籤不合併；IFD0 白名單、互通性重建、方向與尺寸覆寫沿用正規化路徑。</summary>
    internal static ExifProfile NormalizeCr3(IReadOnlyList<ReadOnlyMemory<byte>?> blocks, uint width, uint height)
    {
        if (blocks.Count == 0 || blocks[0] is not { Length: >= 8 })
            throw new InvalidDataException("CR3 file carries no CMT1 metadata block.");
        using var metadata = new MemoryStream();
        using var writer = new BinaryWriter(metadata);
        writer.Write("II\x2a\0\0\0\0\0"u8);
        var state = new Cr3State(blocks, width, height, writer);
        var rootBlock = blocks[0]!.Value.ToArray();
        var root = state.CopyRoot(rootBlock);
        metadata.Position = 4;
        writer.Write(root);
        var result = new byte[checked((int)metadata.Length + 6)];
        "Exif\0\0"u8.CopyTo(result);
        metadata.GetBuffer().AsSpan(0, (int)metadata.Length).CopyTo(result.AsSpan(6));
        return new ExifProfile(result);
    }

    /// <summary>CR3 合併用的正規化狀態：一次只持有一個區塊位元組（<see cref="ReadOnlySpan{T}"/> 不可為欄位）。</summary>
    private sealed class Cr3State(IReadOnlyList<ReadOnlyMemory<byte>?> blocks, uint width, uint height, BinaryWriter writer)
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
                    if (kind == 34665 && tag == 40965) continue;
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
                    if (length > 4)
                    {
                        value = Align(writer);
                        writer.Write(payload);
                    }
                    else
                    {
                        entryBytes[..4].Clear();
                        payload.CopyTo(entryBytes);
                        value = U32(entryBytes, 0);
                    }
                }
                entries.Add(new Entry(tag, type, components, value));
            }
            if (kind == 34665)
            {
                entries.RemoveAll(entry => entry.Tag is 40961 or 40962 or 40963);
                entries.AddRange(OutputExif(width, height, writer));
            }
            if (depth == 0)
            {
                entries.RemoveAll(entry => entry.Tag == 274);
                entries.Add(new Entry(274, 3, 1, 1));
                // 上游 set_child：無論 CMT1 是否帶指標，CMT2／CMT4 的第一個 IFD 都掛為 Exif／GPS 子目錄。
                AttachChild(entries, 34665, 1, depth);
                AttachChild(entries, 34853, 3, depth);
                if (!entries.Any(entry => entry.Tag == 34665))
                    entries.Add(new Entry(34665, 4, 1, WriteDirectory(OutputExif(width, height, writer), writer)));
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


    public static void Append(string path, IExifProfile profile)
    {
        // Normalize emits little-endian metadata; the TIFF encoder is explicitly LSB.
        var bytes = profile.ToByteArray();
        if (bytes is null) return;
        var data = bytes.AsSpan(6);
        if (!bytes.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8) ||
            data.Length < 8 || U16(data, 0) != 0x4949 || U16(data, 2) != 42)
            throw new InvalidDataException("Expected canonical little-endian EXIF.");

        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt16() != 0x4949 || reader.ReadUInt16() != 42)
            throw new InvalidDataException("Expected classic little-endian TIFF output.");
        file.Position = reader.ReadUInt32();
        var count = reader.ReadUInt16();
        var entries = new Dictionary<ushort, Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = new Entry(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32());
            entries.Add(entry.Tag, entry);
        }
        if (reader.ReadUInt32() != 0)
            throw new InvalidDataException("Expected single-frame TIFF output.");

        file.Position = file.Length;
        foreach (var entry in CopyDirectory(data, U32(data, 4), writer, depth: 0))
        {
            // The encoder owns dimensions, orientation, compression, ICC, and pixel offsets.
            // Never introduce an old image/thumbnail pointer from source metadata.
            if (entry.Tag is 273 or 288 or 324 or 330 or 513) continue;
            entries.TryAdd(entry.Tag, entry);
        }
        var root = WriteDirectory(entries.Values, writer);
        file.Position = 4;
        writer.Write(root);
    }

    private static Entry[] CopyDirectory(ReadOnlySpan<byte> data, uint offset, BinaryWriter writer, int depth)
    {
        if (depth > 2) throw new InvalidDataException("Unexpected nested EXIF directory.");
        var start = checked((int)offset);
        var count = U16(data, start);
        var entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var pos = checked(start + 2 + i * 12);
            var tag = U16(data, pos);
            var type = U16(data, pos + 2);
            var components = U32(data, pos + 4);
            var value = U32(data, pos + 8);
            var length = checked(components * FieldSize(type));
            if (tag is 34665 or 34853 or 40965)
            {
                if (type != 4 || components != 1)
                    throw new InvalidDataException("Invalid EXIF directory pointer.");
                value = WriteDirectory(CopyDirectory(data, value, writer, depth + 1), writer);
            }
            else if (length > 4)
            {
                var payload = data.Slice(checked((int)value), checked((int)length));
                value = Align(writer);
                writer.Write(payload);
            }
            entries[i] = new Entry(tag, type, components, value);
        }
        // Normalize omits the source thumbnail chain.
        if (U32(data, checked(start + 2 + count * 12)) != 0)
            throw new InvalidDataException("Unexpected EXIF thumbnail directory.");
        return entries;
    }

    private static uint WriteDirectory(IEnumerable<Entry> entries, BinaryWriter writer)
    {
        var sorted = entries.OrderBy(entry => entry.Tag).ToArray();
        var offset = Align(writer);
        writer.Write(checked((ushort)sorted.Length));
        foreach (var entry in sorted)
        {
            writer.Write(entry.Tag);
            writer.Write(entry.Type);
            writer.Write(entry.Count);
            writer.Write(entry.Value);
        }
        writer.Write(0u);
        return offset;
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
