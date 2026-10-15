using System.Buffers.Binary;
using ImageMagick;

namespace FilmBatch.Engine.Exporting;

/// <summary>
/// Magick.NET 14 writes TIFF pixels/ICC but not the EXIF/GPS directories. Append those
/// directories to our temporary output; leave compressed strips and their offsets untouched.
/// Input metadata is reserialized by ExifProfile, never parsed directly from a source file.
/// </summary>
internal static class TiffMetadata
{
    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint Value);
    /// <summary>Magick exposes TIFF EXIF as formatted attributes, not an ExifProfile.
    /// Read only metadata directories, never the (potentially very large) pixel strips.</summary>
    public static ExifProfile Read(string path, uint width, uint height)
    {
        using var file = File.OpenRead(path);
        return Normalize(file, width, height);
    }

    public static ExifProfile Normalize(IExifProfile? source, uint width, uint height)
    {
        var bytes = source?.ToByteArray();
        using var file = bytes is null
            ? new MemoryStream("II\x2a\0\x08\0\0\0\0\0\0\0\0\0"u8.ToArray())
            : bytes.AsSpan().StartsWith("Exif\0\0"u8)
                ? new MemoryStream(bytes, 6, bytes.Length - 6, writable: false)
                : throw new InvalidDataException("Invalid EXIF profile header.");
        return Normalize(file, width, height);
    }

    private static ExifProfile Normalize(Stream file, uint width, uint height)
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
        if (magic != 42 && !large) throw new InvalidDataException("Invalid TIFF version.");
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
                // Image-layout fields and other profile containers are not EXIF.
                if (depth == 0 && tag is 256 or 257 or 258 or 259 or 262 or 266 or 273 or 277 or 278 or 279
                    or 284 or 288 or 289 or 290 or 291 or 292 or 293 or 301 or 317 or 320 or 322 or 323
                    or 324 or 325 or 330 or 338 or 339 or 347 or 512 or 513 or 514 or 515 or 517 or 518
                    or 519 or 520 or 521 or 529 or 530 or 531 or 532 or 700 or 33723 or 34377 or 34675)
                    continue;
                var type = (ushort)Number(entryBytes.Slice(2, 2));
                var components = Number(entryBytes.Slice(4, large ? 8 : 4));
                var raw = entryBytes.Slice(large ? 12 : 8, large ? 8 : 4);
                uint value;
                if (tag is 34665 or 34853 or 40965)
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
                entries.AddRange(OutputDimensions(width, height));
            }
            if (depth == 0)
            {
                entries.RemoveAll(entry => entry.Tag == 274);
                entries.Add(new Entry(274, 3, 1, 1));
                if (!entries.Any(entry => entry.Tag == 34665))
                    entries.Add(new Entry(34665, 4, 1, WriteDirectory(OutputDimensions(width, height), writer)));
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

    private static Entry[] OutputDimensions(uint width, uint height) =>
    [
        new(40961, 3, 1, 1), // sRGB
        new(40962, 4, 1, width),
        new(40963, 4, 1, height)
    ];
}
