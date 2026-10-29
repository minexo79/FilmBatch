using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Engine.Rendering;

/// <summary>執行已正規化的 CLF 節點序列；色域轉換與混合由呼叫端處理。</summary>
internal static class ColorTransformProcessing
{
    private const int ChunkPixels = 4096;

    /// <summary>原地處理 RGBA；每個有界區塊檢查取消，alpha 的位元不變。</summary>
    public static void Apply(float[] pixels, int count, ColorTransform transform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chunks = count / ChunkPixels + (count % ChunkPixels == 0 ? 0 : 1);
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellationToken }, chunk =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ops = transform.Ops;
            var data = transform.Data;
            var end = Math.Min(count, (chunk + 1) * ChunkPixels);
            for (var pixel = chunk * ChunkPixels; pixel < end; pixel++)
            {
                var i = pixel * 4;
                var result = Execute(new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]), ops, data);
                pixels[i] = result.X; pixels[i + 1] = result.Y; pixels[i + 2] = result.Z;
            }
        });
    }

    /// <summary>執行純 chain；輸入與輸出在 caller 宣告的編碼域，alpha 原樣保留。</summary>
    public static Vector4 Pixel(Vector4 input, ColorTransform transform)
    {
        var rgb = Execute(new Vector3(input.X, input.Y, input.Z), transform.Ops, transform.Data);
        return new Vector4(rgb, input.W);
    }

    private static Vector3 Execute(Vector3 rgb, ReadOnlySpan<ColorTransformOp> ops, ReadOnlySpan<float> data)
    {
        foreach (ref readonly var op in ops)
        {
            var offset = op.DataOffset;
            switch (op.Code)
            {
                case ColorTransformOpCode.Matrix:
                    rgb = new Vector3(
                        data[offset] * rgb.X + data[offset + 1] * rgb.Y + data[offset + 2] * rgb.Z + data[offset + 3],
                        data[offset + 4] * rgb.X + data[offset + 5] * rgb.Y + data[offset + 6] * rgb.Z + data[offset + 7],
                        data[offset + 8] * rgb.X + data[offset + 9] * rgb.Y + data[offset + 10] * rgb.Z + data[offset + 11]);
                    break;
                case ColorTransformOpCode.Range:
                    if (op.RangeHasMin && op.RangeHasMax)
                    {
                        var scale = (data[offset + 3] - data[offset + 2]) / (data[offset + 1] - data[offset]);
                        rgb = (rgb - new Vector3(data[offset])) * scale + new Vector3(data[offset + 2]);
                        if (op.RangeClamp) rgb = Vector3.Clamp(rgb, new Vector3(data[offset + 2]), new Vector3(data[offset + 3]));
                    }
                    else if (op.RangeHasMin) rgb = Vector3.Max(rgb, new Vector3(data[offset]));
                    else rgb = Vector3.Min(rgb, new Vector3(data[offset + 1]));
                    break;
                case ColorTransformOpCode.Lut1D:
                    var channels = op.Lut1DChannels;
                    var size = op.Lut1DSize;
                    rgb = new Vector3(Lut1D(data, offset, size, channels, 0, rgb.X),
                        Lut1D(data, offset, size, channels, channels == 1 ? 0 : 1, rgb.Y),
                        Lut1D(data, offset, size, channels, channels == 1 ? 0 : 2, rgb.Z));
                    break;
                case ColorTransformOpCode.Lut3D:
                    rgb = Lut3D(data, offset, op.Lut3DSize, op.Lut3DTetrahedral, rgb);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported transform operation: {op.Code}");
            }
        }
        return rgb;
    }

    private static float Lut1D(ReadOnlySpan<float> data, int offset, int size, int channels, int channel, float input)
    {
        var coordinate = Math.Clamp(input, 0f, 1f) * (size - 1);
        var lo = Math.Min((int)coordinate, size - 2);
        var amount = coordinate - lo;
        var a = data[offset + lo * channels + channel];
        var b = data[offset + (lo + 1) * channels + channel];
        return a + (b - a) * amount;
    }

    private static Vector3 Lut3D(ReadOnlySpan<float> data, int offset, int size, bool tetrahedral, Vector3 input)
    {
        var coordinate = Vector3.Clamp(input, Vector3.Zero, Vector3.One) * (size - 1);
        var r = Math.Min((int)coordinate.X, size - 2);
        var g = Math.Min((int)coordinate.Y, size - 2);
        var b = Math.Min((int)coordinate.Z, size - 2);
        var fr = coordinate.X - r; var fg = coordinate.Y - g; var fb = coordinate.Z - b;
        var c000 = Node(data, offset, size, r, g, b);
        var c111 = Node(data, offset, size, r + 1, g + 1, b + 1);
        if (tetrahedral)
        {
            Vector3 first, second;
            float f1, f2, f3;
            if (fr >= fg)
            {
                if (fg >= fb) { first = Node(data, offset, size, r + 1, g, b); second = Node(data, offset, size, r + 1, g + 1, b); f1 = fr; f2 = fg; f3 = fb; }
                else if (fr >= fb) { first = Node(data, offset, size, r + 1, g, b); second = Node(data, offset, size, r + 1, g, b + 1); f1 = fr; f2 = fb; f3 = fg; }
                else { first = Node(data, offset, size, r, g, b + 1); second = Node(data, offset, size, r + 1, g, b + 1); f1 = fb; f2 = fr; f3 = fg; }
            }
            else
            {
                if (fr >= fb) { first = Node(data, offset, size, r, g + 1, b); second = Node(data, offset, size, r + 1, g + 1, b); f1 = fg; f2 = fr; f3 = fb; }
                else if (fg >= fb) { first = Node(data, offset, size, r, g + 1, b); second = Node(data, offset, size, r, g + 1, b + 1); f1 = fg; f2 = fb; f3 = fr; }
                else { first = Node(data, offset, size, r, g, b + 1); second = Node(data, offset, size, r, g + 1, b + 1); f1 = fb; f2 = fg; f3 = fr; }
            }
            return c000 + (first - c000) * f1 + (second - first) * f2 + (c111 - second) * f3;
        }
        var c001 = Node(data, offset, size, r, g, b + 1);
        var c010 = Node(data, offset, size, r, g + 1, b);
        var c011 = Node(data, offset, size, r, g + 1, b + 1);
        var c100 = Node(data, offset, size, r + 1, g, b);
        var c101 = Node(data, offset, size, r + 1, g, b + 1);
        var c110 = Node(data, offset, size, r + 1, g + 1, b);
        return Vector3.Lerp(Vector3.Lerp(Vector3.Lerp(c000, c001, fb), Vector3.Lerp(c010, c011, fb), fg),
            Vector3.Lerp(Vector3.Lerp(c100, c101, fb), Vector3.Lerp(c110, c111, fb), fg), fr);
    }

    private static Vector3 Node(ReadOnlySpan<float> data, int offset, int size, int r, int g, int b)
    {
        var i = offset + ((r * size + g) * size + b) * 3;
        return new Vector3(data[i], data[i + 1], data[i + 2]);
    }
}
