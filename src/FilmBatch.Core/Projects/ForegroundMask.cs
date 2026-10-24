using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace FilmBatch.Core.Projects;

/// <summary>逐張照片的不可變前景覆蓋率；座標為轉正、尚未裁切的整張來源，0 透明、65535 保留。</summary>
public sealed class ForegroundMask : IEquatable<ForegroundMask>
{
    public const int MaxPixels = 16_777_216;
    private readonly ushort[] _values;
    private readonly byte[] _compressed;

    public int Width { get; }
    public int Height { get; }
    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public ReadOnlySpan<ushort> Values => _values;

    public ForegroundMask(int width, int height, int referenceWidth, int referenceHeight, ReadOnlySpan<ushort> values)
    {
        ValidateDimensions(width, height, referenceWidth, referenceHeight);
        if (values.Length != (long)width * height)
            throw new ArgumentException("Foreground coverage length must be width × height.", nameof(values));
        Width = width;
        Height = height;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        _values = values.ToArray();
        using var encoded = new MemoryStream();
        using (var zlib = new ZLibStream(encoded, CompressionLevel.Fastest, leaveOpen: true))
        {
            if (BitConverter.IsLittleEndian) zlib.Write(MemoryMarshal.AsBytes(_values.AsSpan()));
            else
            {
                Span<byte> row = new byte[width * 2];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                        BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], _values[y * width + x]);
                    zlib.Write(row);
                }
            }
        }
        _compressed = encoded.ToArray();
    }

    private ForegroundMask(ForegroundMaskDocument document, ushort[] values)
    {
        Width = document.Width;
        Height = document.Height;
        ReferenceWidth = document.ReferenceWidth;
        ReferenceHeight = document.ReferenceHeight;
        _values = values;
        _compressed = document.Data;
    }

    private static void ValidateDimensions(int width, int height, int referenceWidth, int referenceHeight)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels || referenceWidth <= 0 || referenceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid foreground coverage dimensions.");
    }

    internal ForegroundMaskDocument ToDocument() => new()
    {
        Width = Width, Height = Height, ReferenceWidth = ReferenceWidth, ReferenceHeight = ReferenceHeight, Data = _compressed,
    };

    internal static ForegroundMask FromDocument(ForegroundMaskDocument document)
    {
        try
        {
            ValidateDimensions(document.Width, document.Height, document.ReferenceWidth, document.ReferenceHeight);
            if (document.Data is null || document.Data.Length > MaxPixels * 2 + 65536)
                throw new InvalidDataException("Invalid foreground coverage payload.");
            using var encoded = new MemoryStream(document.Data, writable: false);
            using var zlib = new ZLibStream(encoded, CompressionMode.Decompress);
            var values = new ushort[checked(document.Width * document.Height)];
            zlib.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
            if (!BitConverter.IsLittleEndian)
                for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReverseEndianness(values[i]);
            if (zlib.ReadByte() != -1) throw new InvalidDataException("Foreground coverage payload is longer than its dimensions.");
            return new ForegroundMask(document, values);
        }
        catch (Exception error) when (error is ArgumentException or EndOfStreamException or OverflowException)
        {
            throw new InvalidDataException("Invalid foreground coverage in project.", error);
        }
    }

    public bool Equals(ForegroundMask? other) => ReferenceEquals(this, other)
        || other is not null && Width == other.Width && Height == other.Height
        && ReferenceWidth == other.ReferenceWidth && ReferenceHeight == other.ReferenceHeight
        && Values.SequenceEqual(other.Values);

    public override bool Equals(object? obj) => obj is ForegroundMask other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Width, Height, ReferenceWidth, ReferenceHeight);
}

internal sealed class ForegroundMaskDocument
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int ReferenceWidth { get; init; }
    public required int ReferenceHeight { get; init; }
    public required byte[] Data { get; init; }
}
