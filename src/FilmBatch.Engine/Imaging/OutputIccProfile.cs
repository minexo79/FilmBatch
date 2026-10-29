using System.Buffers.Binary;
using System.Security.Cryptography;
using FilmBatch.Engine.Imaging.Raw;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// Immutable, user-supplied RGB matrix/TRC output ICC. LUT, device-link and non-RGB profiles are rejected;
/// values are transformed from FilmBatch's bounded ProPhoto intermediate before the original ICC is embedded.
/// </summary>
public sealed class OutputIccProfile
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private const int Steps = 65535;
    private const int Buckets = 4096;
    private static readonly float[] ProPhotoLinear = Enumerable.Range(0, Steps + 1)
        .Select(i => MathF.Pow(i / (float)Steps, 1.8f)).ToArray();
    private readonly float[] _matrix;
    private readonly float[][] _forward;
    private readonly int[][] _bounds;
    internal byte[] Bytes { get; }
    public string Sha256 { get; }

    private OutputIccProfile(byte[] bytes, float[] matrix, float[][] forward, int[][] bounds)
    {
        Bytes = bytes;
        _matrix = matrix;
        _forward = forward;
        _bounds = bounds;
        Sha256 = System.Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Reads and validates a bounded ICC snapshot; the source file is never changed.</summary>
    public static OutputIccProfile Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 132 or > MaxBytes) throw new InvalidDataException(FilmBatch.Core.CoreText.T("輸出 ICC 大小無效。", "Output ICC size is invalid."));
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Create(bytes);
    }

    /// <summary>Validates and copies an ICC snapshot; later changes to the caller's buffer do not change the profile.</summary>
    public static OutputIccProfile Create(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length is < 132 or > MaxBytes || !data.AsSpan(36, 4).SequenceEqual("acsp"u8)
            || data[8] is not (2 or 4)
            || !(data.AsSpan(12, 4).SequenceEqual("mntr"u8) || data.AsSpan(12, 4).SequenceEqual("spac"u8)))
            throw new InvalidDataException(FilmBatch.Core.CoreText.T("輸出 ICC 必須是大小受限的 RGB 顯示或色彩空間設定檔。", "Output ICC must be a bounded RGB display or color-space profile."));
        var bytes = (byte[])data.Clone();
        var info = PreviewIcc.Parse(bytes);
        if (info is not { Model: PreviewIccModel.Rgb, Kind: PreviewIccKind.MatrixTrc }
            || info.ToXyzD50.Inverse() is not { } inverse)
            throw new InvalidDataException(FilmBatch.Core.CoreText.T("輸出 ICC 必須使用可反轉的 RGB 矩陣與傳輸曲線，且不能含 LUT。", "Output ICC must use an invertible RGB matrix and transfer curves, without a LUT."));
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(128, 4));
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            var tag = bytes.AsSpan(132 + i * 12, 4);
            if (!seen.Add(BinaryPrimitives.ReadUInt32BigEndian(tag))
                || tag[..3].SequenceEqual("B2A"u8) || tag[..3].SequenceEqual("B2D"u8))
                throw new InvalidDataException(FilmBatch.Core.CoreText.T("輸出 ICC 含重複標籤或不支援的輸出轉換。", "Output ICC contains duplicate or unsupported output transform tags."));
        }
        var source = PreviewIcc.Parse(RgbEncodingInfo.ProPhotoProfile)
            ?? throw new InvalidDataException("The ProPhoto intermediate ICC is invalid.");
        var matrix = inverse.Mul(source.ToXyzD50).ToF32();
        var forward = new float[3][];
        var bounds = new int[3][];
        for (var c = 0; c < 3; c++)
        {
            forward[c] = new float[Steps + 1];
            var last = -1f;
            for (var i = 0; i <= Steps; i++)
            {
                var value = info.Trc[c].ToLinear(i / (float)Steps);
                if (!float.IsFinite(value) || value < 0 || value > 1 || value < last)
                    throw new InvalidDataException(FilmBatch.Core.CoreText.T("輸出 ICC 傳輸曲線必須在 0–1 範圍內保持有限且單調。", "Output ICC transfer curves must be finite and monotonic in the 0–1 range."));
                forward[c][i] = last = value;
            }
            if (forward[c][0] >= forward[c][Steps])
                throw new InvalidDataException(FilmBatch.Core.CoreText.T("輸出 ICC 傳輸曲線沒有可用範圍。", "Output ICC transfer curve has no usable range."));
            bounds[c] = new int[Buckets + 1];
            var index = 0;
            for (var i = 0; i <= Buckets; i++)
            {
                var target = i / (float)Buckets;
                while (index < Steps && forward[c][index] < target) index++;
                bounds[c][i] = index;
            }
        }
        return new OutputIccProfile(bytes, matrix, forward, bounds);
    }

    internal ushort[] Convert(ushort[] rgba, CancellationToken token)
    {
        var output = new ushort[rgba.Length];
        Parallel.For(0, rgba.Length / 4, new ParallelOptions { CancellationToken = token }, i =>
        {
            var s = i * 4;
            var r = ProPhotoLinear[rgba[s]];
            var g = ProPhotoLinear[rgba[s + 1]];
            var b = ProPhotoLinear[rgba[s + 2]];
            output[s] = Encode(0, _matrix[0] * r + _matrix[1] * g + _matrix[2] * b);
            output[s + 1] = Encode(1, _matrix[3] * r + _matrix[4] * g + _matrix[5] * b);
            output[s + 2] = Encode(2, _matrix[6] * r + _matrix[7] * g + _matrix[8] * b);
            output[s + 3] = rgba[s + 3];
        });
        return output;
    }

    private ushort Encode(int channel, float value)
    {
        var table = _forward[channel];
        if (value <= table[0]) return 0;
        if (value >= table[Steps]) return Steps;
        var bucket = Math.Clamp((int)(value * Buckets), 0, Buckets - 1);
        var lo = Math.Max(0, _bounds[channel][bucket] - 1);
        var hi = _bounds[channel][bucket + 1];
        while (lo + 1 < hi)
        {
            var mid = (lo + hi) / 2;
            if (table[mid] < value) lo = mid; else hi = mid;
        }
        return (ushort)(value - table[lo] <= table[hi] - value ? lo : hi);
    }
}
