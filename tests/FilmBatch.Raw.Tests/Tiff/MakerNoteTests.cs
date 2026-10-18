// 移植自 LightCraft crates/tiff/src/makernote.rs（相機附註的偏移基準）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests.Tiff;

public sealed class MakerNoteTests
{
    private static byte[] Tiny(ByteOrder o, uint rel)
    {
        var b = new List<byte>(); o.PutU16(b, 2); o.PutU16(b, 1); o.PutU16(b, 3); o.PutU32(b, 1); o.PutU16(b, 7); o.PutU16(b, 0);
        o.PutU16(b, 2); o.PutU16(b, 4); o.PutU32(b, 2); o.PutU32(b, rel); o.PutU32(b, 0); return b.ToArray();
    }
    private static (byte[] Data, long Off, int Length) WithNote(ByteOrder o, byte[] note)
    {
        var b = new IfdBuilder().With(Tags.Make, new TiffValue.Ascii("Test"))
            .SetChild(Tags.ExifIfd, new IfdBuilder().With(Tags.MakerNote, new TiffValue.Undefined(note)));
        var data = new TiffWriter(o).Write([b]); var e = TiffFile.Parse(data).Exif()!.Get(Tags.MakerNote)!; return (data, e.Offset, e.Count);
    }

    [Theory, InlineData(ByteOrder.Little), InlineData(ByteOrder.Big)]
    public void NikonV3UsesEmbeddedHeaderBase(ByteOrder o)
    {
        var note = new List<byte>("Nikon\0\x02\x10\0\0"u8.ToArray()); note.AddRange(o == ByteOrder.Little ? "II"u8.ToArray() : "MM"u8.ToArray());
        o.PutU16(note, 42); o.PutU32(note, 8); note.AddRange(Tiny(o, 38)); o.PutU32(note, 111); o.PutU32(note, 222);
        var (data, off, len) = WithNote(o, note.ToArray()); var m = MakerNote.ParseMakerNote(data, off, len, o, "NIKON CORPORATION")!;
        Assert.Equal(MakerNoteKind.NikonV3, m.Kind); Assert.Equal(off + 10, m.Base); Assert.Equal((uint)7, m.Ifd.U32(1)); Assert.Equal([111UL, 222UL], m.Ifd.U64s(2)!);
    }

    [Fact]
    public void FujiIgnoresEnclosingByteOrderAndUsesNoteBase()
    {
        var note = new List<byte>("FUJIFILM"u8.ToArray()); ByteOrder.Little.PutU32(note, 12); note.AddRange(Tiny(ByteOrder.Little, 42));
        ByteOrder.Little.PutU32(note, 5); ByteOrder.Little.PutU32(note, 6); var (data, off, len) = WithNote(ByteOrder.Big, note.ToArray());
        var m = MakerNote.ParseMakerNote(data, off, len, ByteOrder.Big, "FUJIFILM")!;
        Assert.Equal(MakerNoteKind.Fujifilm, m.Kind); Assert.Equal(ByteOrder.Little, m.Order); Assert.Equal(off, m.Base); Assert.Equal([5UL, 6UL], m.Ifd.U64s(2)!);
    }

    [Fact]
    public void PlainCanonUsesFileRelativeOffsets()
    {
        var note = Tiny(ByteOrder.Little, 0).Concat(new byte[8]).ToArray(); var (data, off, len) = WithNote(ByteOrder.Little, note);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)off + 22), (uint)off + 30);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)off + 30), 9);
        var m = MakerNote.ParseMakerNote(data, off, len, ByteOrder.Little, "Canon")!;
        Assert.Equal(MakerNoteKind.Plain, m.Kind); Assert.Equal([9UL, 0UL], m.Ifd.U64s(2)!);
    }

    [Theory]
    [InlineData("SONY DSC ", 12, MakerNoteKind.Sony, false)]
    [InlineData("Panasonic", 12, MakerNoteKind.Panasonic, false)]
    [InlineData("OLYMP", 8, MakerNoteKind.OlympusOld, false)]
    [InlineData("OLYMPUS", 12, MakerNoteKind.OlympusNew, true)]
    [InlineData("OM SYSTEM", 16, MakerNoteKind.OlympusNew, true)]
    [InlineData("AOC", 6, MakerNoteKind.PentaxAoc, false)]
    [InlineData("PENTAX ", 10, MakerNoteKind.Pentax, true)]
    public void HeaderDialectsLocateIfd(string header, int skip, MakerNoteKind kind, bool noteBase)
    {
        var note = new byte[skip].Concat(Tiny(ByteOrder.Little, 0)).ToArray(); System.Text.Encoding.ASCII.GetBytes(header).CopyTo(note, 0);
        var (data, off, len) = WithNote(ByteOrder.Little, note); var m = MakerNote.ParseMakerNote(data, off, len, ByteOrder.Little, "")!;
        Assert.Equal(kind, m.Kind); Assert.Equal(noteBase ? off : 0, m.Base); Assert.Equal((uint)7, m.Ifd.U32(1));
    }

    [Fact]
    public void GarbageAndUnrepresentableRangesAreNotMakerNotes()
    {
        var (data, off, len) = WithNote(ByteOrder.Little, Enumerable.Repeat((byte)255, 40).ToArray());
        Assert.Null(MakerNote.ParseMakerNote(data, off, len, ByteOrder.Little, "X"));
        Assert.Null(MakerNote.ParseMakerNote(data, 1L << 40, 10, ByteOrder.Little, "X"));
        Assert.Null(MakerNote.ParseMakerNote(data, off, long.MaxValue, ByteOrder.Little, "X"));
    }
}
