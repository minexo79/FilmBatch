using System.Buffers.Binary;
using System.Security.Cryptography;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Opcodes;

namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class RawSourceLoader
{
    private readonly record struct DecodeKey(string Path, ulong A, ulong B, ulong C, ulong D);
    private static readonly object DecodeLock = new();
    private static readonly Dictionary<DecodeKey, DecodeEntry> ActiveDecodes = [];

    private sealed class DecodeEntry(byte[] bytes)
    {
        internal readonly byte[] Bytes = bytes;
        internal readonly Lazy<RawImage> Raw = new(() => RawDecoder.Decode(bytes));
        internal readonly List<ColourFit> Fits = [];
        internal int Users;
    }

    private sealed record ColourFit(Opcode[] Ops, Xy White, CameraTransform Transform, CameraLook? Look,
        ushort[] Illuminant, Mat3?[] ColorMatrix, Mat3?[] ForwardMatrix);

    /// <summary>僅共用仍在顯影中的解碼；最後一位使用者離開即釋放，不保留完整 RAW 快取。</summary>
    internal sealed class DecodeLease : IDisposable
    {
        private readonly DecodeKey _key;
        private DecodeEntry? _entry;
        private DecodeEntry Entry => _entry ?? throw new ObjectDisposedException(nameof(DecodeLease));

        internal DecodeLease(string path)
        {
            // 每次仍讀取現有內容：相同長度與時間戳也可能是另一張照片，不能據此沿用樣本或擬合。
            var bytes = File.ReadAllBytes(path);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(bytes, hash);
            _key = new(Path.GetFullPath(path), BinaryPrimitives.ReadUInt64LittleEndian(hash),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]), BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));
            lock (DecodeLock)
            {
                if (!ActiveDecodes.TryGetValue(_key, out _entry)) ActiveDecodes.Add(_key, _entry = new(bytes));
                _entry.Users++;
            }
        }

        internal ReadOnlyMemory<byte> Bytes => Entry.Bytes;

        /// <summary>樣本只讀共用；顯影會改寫的色彩標籤、運算碼清單與 Data 欄位各自獨立。</summary>
        internal RawImage CreateRaw()
        {
            var raw = Entry.Raw.Value;
            return new RawImage
            {
                Format = raw.Format, Width = raw.Width, Height = raw.Height, Cpp = raw.Cpp, Data = raw.Data,
                Cfa = raw.Cfa, Bits = raw.Bits, Black = raw.Black, White = raw.White,
                ActiveArea = raw.ActiveArea, Crop = raw.Crop, Orientation = raw.Orientation,
                Color = raw.Color.Clone(), WbMultipliers = raw.WbMultipliers, Linearized = raw.Linearized,
                PixelAspect = raw.PixelAspect, Opcodes = raw.Opcodes.Clone(),
                LensMetadata = raw.LensMetadata, Metadata = raw.Metadata,
            };
        }

        internal (Xy White, CameraTransform Transform, CameraLook? Look) StartingColour(RawImage raw,
            CameraPreviewLens? lens, EmbeddedVignette? vignette)
        {
            var entry = Entry;
            lock (entry.Fits)
            {
                foreach (var fit in entry.Fits)
                {
                    if (!SameOperations(fit.Ops, raw.Opcodes.List3)) continue;
                    // StartingColour 的光譜補值也是結果的一部分；不能只共用回傳矩陣。
                    raw.Color.Illuminant = [.. fit.Illuminant];
                    raw.Color.ColorMatrix = [.. fit.ColorMatrix];
                    raw.Color.ForwardMatrix = [.. fit.ForwardMatrix];
                    return (fit.White, fit.Transform, fit.Look);
                }
                var result = CameraPreview.StartingColour(raw, entry.Bytes, lens, vignette);
                entry.Fits.Add(new([.. raw.Opcodes.List3], result.White, result.Transform, result.Look,
                    [.. raw.Color.Illuminant], [.. raw.Color.ColorMatrix], [.. raw.Color.ForwardMatrix]));
                return result;
            }
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;
            lock (DecodeLock)
                if (--entry.Users == 0) ActiveDecodes.Remove(_key);
        }
    }

    private static bool SameOperations(Opcode[] first, List<Opcode> second)
    {
        if (first.Length != second.Count) return false;
        for (var i = 0; i < first.Length; i++)
        {
            if (first[i] is FixVignetteRadial a && second[i] is FixVignetteRadial b)
            {
                if (!a.K.AsSpan().SequenceEqual(b.K) || !a.Center.AsSpan().SequenceEqual(b.Center)) return false;
            }
            else if (!first[i].Equals(second[i])) return false;
        }
        return true;
    }
}
