// 移植自 LightCraft crates/tiff/src/makernote.rs（廠商附註標頭方言與偏移基準）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Tiff;

/// Maker-note dialects we know how to locate.
public enum MakerNoteKind
{
    /// Plain IFD at the start of the note, offsets relative to the enclosing TIFF header (Canon, many others).
    Plain,
    /// `Nikon\0` + version + embedded TIFF header at +10; offsets relative to that header.
    NikonV3,
    /// `Nikon\0\x01\0`: IFD at +8, offsets relative to the enclosing TIFF header.
    NikonV1,
    /// `SONY DSC `/`SONY CAM `/`SONY MOBILE` etc.: 12-byte header, offsets relative to the TIFF header.
    Sony,
    /// `FUJIFILM` + LE u32 IFD offset; little-endian, offsets relative to the note start.
    Fujifilm,
    /// `OLYMPUS\0`/`OM SYSTEM\0` + byte order + version: offsets relative to the note start.
    OlympusNew,
    /// `OLYMP\0` / `EPSON\0` etc.: IFD at +8, offsets relative to the TIFF header.
    OlympusOld,
    /// `Panasonic\0\0\0`: IFD at +12, offsets relative to the TIFF header.
    Panasonic,
    /// `AOC\0` + byte order: IFD at +6, offsets relative to the TIFF header.
    PentaxAoc,
    /// `PENTAX \0` + byte order: IFD at +10, offsets relative to the note start.
    Pentax,
}

/// A parsed maker-note IFD.
public sealed class MakerNote
{
    public MakerNoteKind Kind { get; }
    public ByteOrder Order { get; }
    /// Absolute offset that stored value offsets are relative to.
    public long Base { get; }
    public Ifd Ifd { get; }
    private MakerNote(MakerNoteKind kind, ByteOrder order, long baseOffset, Ifd ifd) => (Kind, Order, Base, Ifd) = (kind, order, baseOffset, ifd);

    /// Locate and parse the maker note stored at absolute `offset` (length `length`) inside `data`, the buffer whose
    /// start is the enclosing TIFF header. `order` is the enclosing stream's byte order. `make` is only a hint for
    /// header-less notes. Returns null for unknown or unparsable notes.
    public static MakerNote? ParseMakerNote(ReadOnlyMemory<byte> data, long offset, long length, ByteOrder order, string make)
    {
        if (offset < 0 || offset >= data.Length || length < 0) return null;
        int start = (int)offset;
        int end = (int)Math.Min((long)start + Math.Min(length, data.Length), data.Length);
        var note = data.Span[start..end];
        static ByteOrder? OrderAt(ReadOnlySpan<byte> n, int i) =>
            n.Length >= i + 2 && n[i] == (byte)'I' && n[i + 1] == (byte)'I' ? ByteOrder.Little :
            n.Length >= i + 2 && n[i] == (byte)'M' && n[i + 1] == (byte)'M' ? ByteOrder.Big : null;
        var opts = new ParseOptions { MaxIfds = 64, MaxDepth = 3 };
        MakerNote? TryParse(MakerNoteKind kind, long ifdAt, long baseOffset, ByteOrder ord)
        {
            try
            {
                var (ifd, _) = TiffFile.ParseIfdAt(data, ifdAt, ord, baseOffset, false, opts);
                return ifd.Entries.Count == 0 ? null : new MakerNote(kind, ord, baseOffset, ifd);
            }
            catch (RawException) { return null; }
        }
        if (note.StartsWith("Nikon\0"u8))
        {
            if (note.Length > 6 && note[6] == 1) return TryParse(MakerNoteKind.NikonV1, offset + 8, 0, order);
            if (OrderAt(note, 10) is not { } ord || ord.ReadU32(note, 14) is not { } rel) return null;
            long baseAt = offset + 10;
            return TryParse(MakerNoteKind.NikonV3, baseAt + rel, baseAt, ord);
        }
        if (note.StartsWith("FUJIFILM"u8) || note.StartsWith("GENERALE"u8))
        {
            if (ByteOrder.Little.ReadU32(note, 8) is not { } rel) return null;
            return TryParse(MakerNoteKind.Fujifilm, offset + rel, offset, ByteOrder.Little);
        }
        if (note.StartsWith("OLYMPUS\0"u8) || note.StartsWith("OM SYSTEM\0"u8))
        {
            int orderAt = note.StartsWith("OM SYSTEM\0"u8) ? 12 : 8, ifdAt = note.StartsWith("OM SYSTEM\0"u8) ? 16 : 12;
            return TryParse(MakerNoteKind.OlympusNew, offset + ifdAt, offset, OrderAt(note, orderAt) ?? order);
        }
        foreach (var prefix in new byte[][] { "OLYMP\0"u8.ToArray(), "EPSON\0"u8.ToArray(), "MINOL\0"u8.ToArray(), "CAMER\0"u8.ToArray(), "SANYO\0"u8.ToArray() })
            if (note.StartsWith(prefix)) return TryParse(MakerNoteKind.OlympusOld, offset + 8, 0, order);
        if (note.StartsWith("Panasonic\0"u8)) return TryParse(MakerNoteKind.Panasonic, offset + 12, 0, order);
        if (note.StartsWith("AOC\0"u8)) return TryParse(MakerNoteKind.PentaxAoc, offset + 6, 0, OrderAt(note, 4) ?? order);
        if (note.StartsWith("PENTAX \0"u8)) return TryParse(MakerNoteKind.Pentax, offset + 10, offset, OrderAt(note, 8) ?? order);
        if (note.StartsWith("SONY"u8) || note.StartsWith("VHAB     \0"u8))
            if (TryParse(MakerNoteKind.Sony, offset + 12, 0, order) is { } m) return m;
        _ = make;
        return TryParse(MakerNoteKind.Plain, offset, 0, order);
    }
}
