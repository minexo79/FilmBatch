// 移植自 LightCraft crates/raw/src/semantic.rs（DNG 語意遮罩：NewSubFileType 10004H、Photometric 52527）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw;

/// <summary>DNG 語意遮罩的未裁切遮罩座標（DNG <c>MaskSubArea</c>）。未裁切遮罩
/// （<c>FullWidth × FullHeight</c>）橫跨主影像的有效區域。</summary>
public readonly record struct MaskSubArea(uint Top, uint Left, uint FullWidth, uint FullHeight);

/// <summary>顯影座標的語意遮罩輸出:<c>Width × Height</c>、0–255 alpha 單通道(lightcraft <c>developed</c>)。</summary>
/// <param name="Data">列優先 0–255 alpha。</param>
public sealed record DevelopedMask(int Width, int Height, byte[] Data);

/// <summary>DNG 語意遮罩：<c>Width × Height</c> 個值，0 = 不屬於該屬性、65535 = 完全屬於。</summary>
public sealed class SemanticMask
{
    /// <summary><c>SemanticName</c>，例如 <c>urn:com:apple:photo:2020:aux:semanticskymatte</c>。</summary>
    public required string Name { get; init; }

    public string? InstanceId { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary><c>null</c>：遮罩涵蓋整個有效區域。</summary>
    public MaskSubArea? SubArea { get; init; }

    public required ushort[] Data { get; init; }

    /// <summary>未裁切遮罩連續位置的遮罩值（0..1，像素中心在 +0.5）；已存（已裁切）遮罩之外為 0。</summary>
    public float ValueAt(double x, double y)
    {
        var (left, top) = SubArea is { } area ? ((double)area.Left, (double)area.Top) : (0.0, 0.0);
        x -= left;
        y -= top;
        if (Width == 0 || Height == 0 || !(x >= 0.0 && y >= 0.0 && x <= Width && y <= Height))
            return 0f;
        double fx = Math.Max(x - 0.5, 0.0), fy = Math.Max(y - 0.5, 0.0);
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float At(int px, int py)
        {
            int cx = Math.Min(px, Width - 1), cy = Math.Min(py, Height - 1);
            return Data[cy * Width + cx] / 65535f;
        }
        float topRow = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * tx;
        float bottomRow = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * tx;
        return topRow + (bottomRow - topRow) * ty;
    }

    /// <summary>顯影影像上的遮罩（<c>activeArea</c> 的預設 <c>crop</c>，轉正之前），取自遮罩自身解析度、
    /// 0..255 alpha 單通道影像。<c>crop</c> 為空時回傳 <c>null</c>。</summary>
    public DevelopedMask? Developed(PixelRect activeArea, PixelRect crop)
    {
        if (activeArea.Width == 0 || activeArea.Height == 0)
            return null;
        var c = crop.Clipped(activeArea.Width, activeArea.Height);
        if (c.Width == 0 || c.Height == 0 || Width == 0 || Height == 0)
            return null;
        long fw = SubArea is { } subArea ? subArea.FullWidth : Width;
        long fh = SubArea is { } subAreaH ? subAreaH.FullHeight : Height;
        // 未裁切遮罩像素／有效區域像素
        double sx = (double)fw / activeArea.Width, sy = (double)fh / activeArea.Height;
        // Rust f64::round 是遠離零（非銀行家捨入）；先在 double 域夾取再檢查上限，避免巨大標籤溢位。
        double owD = Math.Clamp(Math.Round(c.Width * sx, MidpointRounding.AwayFromZero), 1.0, (double)Math.Max((long)Width, fw));
        double ohD = Math.Clamp(Math.Round(c.Height * sy, MidpointRounding.AwayFromZero), 1.0, (double)Math.Max((long)Height, fh));
        if (owD * ohD > RawSemanticMasks.MaxSamples)
            return null;
        int ow = (int)owD, oh = (int)ohD;
        double kx = (double)c.Width / ow, ky = (double)c.Height / oh;
        var output = new byte[ow * oh];
        // 每列獨立，可平行化（上游 par_rows）；輸出像素 (x, y) 中心對應 crop 相對座標。
        Parallel.For(0, oh, RawParallel.Options, y =>
        {
            double ay = c.Y + (y + 0.5) * ky;
            int row = y * ow;
            for (int x = 0; x < ow; x++)
            {
                double ax = c.X + (x + 0.5) * kx;
                output[row + x] = (byte)MathF.Round(Math.Clamp(ValueAt(ax * sx, ay * sy), 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
            }
        });
        return new DevelopedMask(ow, oh, output);
    }

}

/// <summary>DNG 語意遮罩讀取（lightcraft <c>semantic_masks</c>）：只在呼叫者需要時才讀取；
/// 格式不符或不支援的遮罩 IFD 會略過。</summary>
public static class RawSemanticMasks
    {
    /// <summary>最多從一個檔案讀取的遮罩 IFD 數。</summary>
    public const int MaxMasks = 16;

    /// <summary>樣本數超過此值的遮罩會略過（低解析度 matte；8192 × 4096）。</summary>
    public const long MaxSamples = 1 << 25;

    /// <summary>檔案的語意遮罩（依檔案順序；其他檔案為空）。</summary>
    public static IReadOnlyList<SemanticMask> Read(ReadOnlyMemory<byte> bytes)
    {
        TiffFile tiff;
        try
        {
            tiff = TiffFile.Parse(bytes);
        }
        catch (RawException)
        {
            return [];
        }
        var output = new List<SemanticMask>();
        int seen = 0;
        foreach (var ifd in tiff.AllIfds())
        {
            if (seen >= MaxMasks)
                break;
            if (ifd.U16(Tags.Photometric) != TiffPhotometric.Mask
                && ifd.U32(Tags.NewSubfileType) != Tags.SubfileSemanticMask)
                continue;
            seen++;
            try
            {
                if (ReadMask(bytes, tiff.Order, ifd) is { } mask)
                    output.Add(mask);
            }
            catch (RawException)
            {
            }
        }
        return output;
    }

    private static SemanticMask? ReadMask(ReadOnlyMemory<byte> bytes, ByteOrder order, Ifd ifd)
    {
        if (ifd.String(Tags.SemanticName) is not { Length: > 0 } name)
            return null;
        ImageInfo info;
        try
        {
            info = ifd.Image();
        }
        catch (RawException)
        {
            return null;
        }
        long w = info.Width, h = info.Height;
        if (info.SamplesPerPixel != 1 || w * h > MaxSamples)
            return null;
        uint bits = info.Bits();
        RawData data;
        try
        {
            data = TiffRaw.ReadImage(bytes, info, order, Packing.Msb);
        }
        catch (RawException ex) when (ex.Kind is RawErrorKind.Unsupported or RawErrorKind.Corrupt or RawErrorKind.Limit or RawErrorKind.Tiff)
        {
            return null;
        }
        ushort[] normalized = data switch
        {
            RawData.U16 u when bits is >= 1 and <= 16 =>
                bits == 16 ? u.Samples : NormalizeInt(u.Samples, bits),
            RawData.F32 f => NormalizeFloat(f.Samples),
            _ => [],
        };
        if (normalized.Length == 0 || normalized.Length != (long)info.Width * info.Height)
            return null;
        // 遮罩放不進未裁切遮罩時忽略此標籤（上游飽和加法語意：加總溢位視為不合）。
        MaskSubArea? subArea = null;
        if (ifd.U64s(Tags.MaskSubArea) is { Length: 4 } sa)
        {
            ulong top = sa[0], left = sa[1], fw = sa[2], fh = sa[3];
            ulong uw = (ulong)w, uh = (ulong)h;
            bool fits = fw > 0 && fh > 0 && uw <= fw && uh <= fh && left <= fw - uw && top <= fh - uh
                && Math.Max(fw, fh) <= uint.MaxValue;
            if (fits)
                subArea = new MaskSubArea((uint)top, (uint)left, (uint)fw, (uint)fh);
        }
        if (ifd.String(Tags.SemanticInstanceId) is { Length: > 0 } instanceId)
            return new SemanticMask { Name = name, InstanceId = instanceId, Width = (int)w, Height = (int)h, SubArea = subArea, Data = normalized };
        return new SemanticMask { Name = name, Width = (int)w, Height = (int)h, SubArea = subArea, Data = normalized };
    }

    private static ushort[] NormalizeInt(ushort[] samples, uint bits)
    {
        // 上游 (s as f32 / max * 65535.0).round()：Rust round 是遠離零；Math.Round 預設銀行家捨入須指定。
        float max = (1u << (int)bits) - 1;
        var output = new ushort[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            output[i] = (ushort)Math.Round(Math.Min(samples[i] / max * 65535f, 65535f), MidpointRounding.AwayFromZero);
        return output;
    }

    private static ushort[] NormalizeFloat(float[] samples)
    {
        var output = new ushort[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            output[i] = float.IsFinite(samples[i])
                ? (ushort)Math.Round(Math.Clamp(samples[i], 0f, 1f) * 65535f, MidpointRounding.AwayFromZero)
                : (ushort)0;
        return output;
    }
    }
