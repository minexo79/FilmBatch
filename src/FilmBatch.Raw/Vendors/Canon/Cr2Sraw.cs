// 移植自 LightCraft crates/raw/src/vendor/cr2/sraw.rs（Canon sRAW／mRAW：YCbCr 減採樣）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Canon;

/// <summary>Canon sRAW／mRAW 解碼（raw IFD 的 SRAW_TYPE＝4）。對應 <c>vendor::cr2::sraw</c>。</summary>
internal static class Cr2Sraw
{
    private const double ChromaCentre = 16384.0;
    private const double KR = 0.299, KG = 0.587, KB = 0.114;
    private const float LumaGain = 0.875f;
    private const float SensorFull = 16383f;
    private const double FitLimit = 4096.0;

    /// <summary>解碼影格到影像的對應（對應 sraw::Layout）。</summary>
    internal sealed class Layout
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Vertical { get; private set; }
        public List<int> Widths { get; } = [];
        public int Rows { get; private set; }
        public int Mcu { get; private set; }
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }

        private Layout() { }

        internal static Layout Create(int frameWidth, int frameHeight, int vertical, ulong[] slices)
        {
            if (slices.Length != 3) throw RawException.Corrupt("sRAW without slice layout");
            ulong n = slices[0], w = slices[1], last = slices[2];
            if (n > 64 || w == 0 || last == 0) throw RawException.Corrupt("sRAW slice layout");
            long mcu = 2L * vertical + 2;
            if (w > int.MaxValue || last > int.MaxValue) throw RawException.Corrupt("sRAW slice layout");
            var widths = new List<int>();
            for (ulong k = 0; k < n; k++) widths.Add((int)w);
            widths.Add((int)last);
            foreach (int sw in widths)
                if (sw % mcu != 0) throw RawException.Corrupt("sRAW slice is not a whole number of MCUs");
            long pixels;
            try
            {
                pixels = checked((long)frameWidth * frameHeight);
            }
            catch (OverflowException)
            {
                throw RawException.Limit("sRAW frame too large");
            }
            long samples;
            try
            {
                samples = checked(pixels + pixels / vertical);
            }
            catch (OverflowException)
            {
                throw RawException.Limit("sRAW frame too large");
            }
            long row = 0;
            foreach (int sw in widths)
            {
                row += sw;
                if (row < 0) throw RawException.Limit("sRAW slice widths");
            }
            if (samples == 0 || row == 0 || samples % row != 0)
                throw RawException.Corrupt($"sRAW slices ({row} samples per row) do not divide the frame ({samples} samples)");
            long rows = samples / row;
            long width = row / mcu * 2;
            long height = rows * vertical;
            long total;
            try
            {
                total = checked(checked(width * height) * 3);
            }
            catch (OverflowException)
            {
                throw RawException.Limit("sRAW image too large");
            }
            if (total > RawLimits.MaxSamples) throw RawException.Limit("sRAW image too large");
            if (width > int.MaxValue || height > int.MaxValue || rows > int.MaxValue)
                throw RawException.Limit("sRAW image too large");
            return new Layout
            {
                Width = (int)width,
                Height = (int)height,
                Vertical = vertical,
                Rows = (int)rows,
                Mcu = (int)mcu,
                FrameWidth = frameWidth,
                FrameHeight = frameHeight,
            }.WithWidths(widths);
        }

        public Layout WithWidths(IEnumerable<int> widths)
        {
            Widths.AddRange(widths);
            return this;
        }
    }

    /// <summary>影像幾何的平面 YCbCr（對應 sraw::Planes）。</summary>
    internal sealed class Planes
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Vertical { get; private set; }
        public ushort[] Y = [];
        public ushort[] Cb = [];
        public ushort[] Cr = [];

        private Planes() { }

        internal static Planes FromParts(int width, int height, int vertical, ushort[] y, ushort[] cb, ushort[] cr)
            => new() { Width = width, Height = height, Vertical = vertical, Y = y, Cb = cb, Cr = cr };

        internal int ChromaWidth => Width / 2;

        internal static Planes Arrange(ushort[][] frame, Layout layout)
        {
            int w = layout.Width, h = layout.Height, v = layout.Vertical, m = layout.Mcu;
            int cw = w / 2;
            if (frame.Length < 3 || frame[0].Length < layout.FrameWidth * layout.FrameHeight
                || frame[1].Length * 2 * v < layout.FrameWidth * layout.FrameHeight
                || frame[2].Length * 2 * v < layout.FrameWidth * layout.FrameHeight)
                throw RawException.Corrupt("sRAW frame planes do not match layout");
            var p = new Planes
            {
                Width = w,
                Height = h,
                Vertical = v,
                Y = new ushort[w * h],
                Cb = new ushort[cw * (h / v)],
                Cr = new ushort[cw * (h / v)],
            };
            int mcusPerFrameRow = layout.FrameWidth / 2;
            int x0 = 0;
            foreach (int sw in layout.Widths)
            {
                int perRow = sw / m;
                int first = layout.Rows * (x0 / m);
                for (int r = 0; r < layout.Rows; r++)
                    for (int k = 0; k < perRow; k++)
                    {
                        int q = first + r * perRow + k;
                        int qy = q / mcusPerFrameRow, qx = q % mcusPerFrameRow;
                        int px = (x0 / m + k) * 2;
                        for (int dy = 0; dy < v; dy++)
                        {
                            int src = (qy * v + dy) * layout.FrameWidth + qx * 2;
                            int dst = (r * v + dy) * w + px;
                            p.Y[dst] = frame[0][src];
                            p.Y[dst + 1] = frame[0][src + 1];
                        }
                        p.Cb[r * cw + px / 2] = frame[1][qy * mcusPerFrameRow + qx];
                        p.Cr[r * cw + px / 2] = frame[2][qy * mcusPerFrameRow + qx];
                    }
                x0 += sw;
            }
            return p;
        }

        internal (double Y, double Cb, double Cr) WindowMean(int x0, int x1, int y0, int y1)
        {
            double sy = 0;
            long ny = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++) sy += Y[y * Width + x];
                ny += x1 - x0;
            }
            int cx0 = x0 / 2;
            int cx1 = Math.Max((x1 + 1) / 2, x0 / 2 + 1);
            int cy0 = y0 / Vertical;
            int cy1 = Math.Max((y1 + Vertical - 1) / Vertical, y0 / Vertical + 1);
            int chw = ChromaWidth;
            int chh = Height / Vertical;
            double sb = 0, sr = 0;
            double nc = 0;
            for (int y = cy0; y < Math.Min(cy1, chh); y++)
                for (int x = cx0; x < Math.Min(cx1, chw); x++)
                {
                    sb += Cb[y * chw + x];
                    sr += Cr[y * chw + x];
                    nc += 1.0;
                }
            nc = Math.Max(nc, 1.0);
            return (sy / Math.Max(ny, 1), sb / nc - ChromaCentre, sr / nc - ChromaCentre);
        }

        internal ushort[] ToRgb(double a, double b)
        {
            float gr = (float)(KR / KG * a), gb = (float)(KB / KG * b);
            float fa = (float)a, fb = (float)b;
            int cw = ChromaWidth;
            float centre = (float)ChromaCentre;
            var output = new ushort[Width * Height * 3];
            Parallel.For(0, Height, y =>
            {
                int chroma = (y / Vertical) * cw;
                for (int x = 0; x < Width; x++)
                {
                    float cb = Cb[chroma + x / 2] - centre;
                    float cr = Cr[chroma + x / 2] - centre;
                    float l = Y[y * Width + x];
                    int px = (y * Width + x) * 3;
                    output[px] = (ushort)Math.Clamp(MathF.Round(l + fa * cr, MidpointRounding.AwayFromZero), 0f, 65535f);
                    output[px + 1] = (ushort)Math.Clamp(MathF.Round(l - gr * cr - gb * cb, MidpointRounding.AwayFromZero), 0f, 65535f);
                    output[px + 2] = (ushort)Math.Clamp(MathF.Round(l + fb * cb, MidpointRounding.AwayFromZero), 0f, 65535f);
                }
            });
            return output;
        }
    }

    internal sealed class Preview
    {
        public int Width;
        public int Height;
        public ushort[] Rgb = [];
    }

    private static Preview? ReadPreview(ReadOnlyMemory<byte> bytes, TiffFile tiff)
    {
        if (tiff.Ifds.Count <= 2) return null;
        ImageInfo info;
        try
        {
            info = tiff.Ifds[2].Image();
        }
        catch (RawException ex) when (ex.Kind == RawErrorKind.Tiff)
        {
            return null;
        }
        if (info.SamplesPerPixel != 3 || info.BitsPerSample is not [16, 16, 16]
            || info.Compression != 1 || info.Planar != 1 || info.SampleFormat != 1)
            return null;
        var w = (int)info.Width;
        var h = (int)info.Height;
        long n;
        try
        {
            n = checked(checked(w * h) * 3);
        }
        catch (OverflowException)
        {
            return null;
        }
        if (n == 0 || n > 1 << 24) return null;
        var raw = new List<byte>((int)Math.Min(n * 2, int.MaxValue));
        foreach (TiffChunk c in info.Chunks(bytes.Length))
        {
            ReadOnlyMemory<byte>? part = TiffImage.ChunkBytes(bytes, c);
            if (part is null) return null;
            raw.AddRange(part.Value.Span.ToArray());
            if (raw.Count >= n * 2) break;
        }
        if (raw.Count < n * 2) return null;
        var rgb = new ushort[(int)n];
        var span = raw.ToArray().AsSpan(0, (int)n * 2);
        for (int i = 0; i < (int)n; i++)
            rgb[i] = tiff.Order == ByteOrder.Little
                ? (ushort)(span[2 * i] | (span[2 * i + 1] << 8))
                : (ushort)((span[2 * i] << 8) | span[2 * i + 1]);
        return new Preview { Width = (int)w, Height = (int)h, Rgb = rgb };
    }

    private sealed class Regression
    {
        public double N, Sx, St, Sxx, Sxt, Stt;
        public void Add(double x, double t)
        {
            N += 1; Sx += x; St += t; Sxx += x * x; Sxt += x * t; Stt += t * t;
        }
        public (double Slope, double Rss)? Slope()
        {
            double var = N * Sxx - Sx * Sx;
            if (N < 500.0 || var <= 1e-9 * N * N) return null;
            double slope = (N * Sxt - Sx * St) / var;
            double icpt = (St - slope * Sx) / N;
            double rss = Stt - 2 * slope * Sxt - 2 * icpt * St
                + slope * slope * Sxx + 2 * slope * icpt * Sx + N * icpt * icpt;
            return (slope, Math.Max(rss, 0.0));
        }
    }

    private readonly record struct Grid(double X0, double Y0, double Sx, double Sy);

    internal static (double A, double B)? ChromaGains(Planes planes, Preview preview, PixelRect active)
    {
        double pw = preview.Width, ph = preview.Height;
        double w = planes.Width, h = planes.Height;
        var grids = new Grid[]
        {
            new(0, 0, w / pw, h / ph),
            new(active.X, active.Y, active.Width / pw, active.Height / ph),
            new(0, 0, Math.Round(2 * w / pw) / 2, Math.Round(2 * h / ph) / 2),
        };
        (double A, double B, double Cost)? best = null;
        foreach (var g in grids)
        {
            if (!(g.Sx >= 1.0 && g.Sy >= 1.0)) continue;
            var fr = new Regression();
            var fb = new Regression();
            for (int j = 0; j < preview.Height; j++)
            {
                int y0 = (int)(g.Y0 + j * g.Sy);
                int y1 = Math.Min((int)(g.Y0 + (j + 1) * g.Sy), planes.Height);
                for (int i = 0; i < preview.Width; i++)
                {
                    ushort r = preview.Rgb[(j * preview.Width + i) * 3];
                    ushort gr = preview.Rgb[(j * preview.Width + i) * 3 + 1];
                    ushort b = preview.Rgb[(j * preview.Width + i) * 3 + 2];
                    double rd = r, gd = gr, bd = b;
                    if (Math.Max(rd, Math.Max(gd, bd)) >= FitLimit || Math.Min(rd, Math.Min(gd, bd)) <= 0.0) continue;
                    int x0 = (int)(g.X0 + i * g.Sx);
                    int x1 = Math.Min((int)(g.X0 + (i + 1) * g.Sx), planes.Width);
                    if (x1 <= x0 || y1 <= y0) continue;
                    var (y, cb, cr) = planes.WindowMean(x0, x1, y0, y1);
                    if (y < 150.0) continue;
                    fr.Add(cr, rd - y);
                    fb.Add(cb, bd - y);
                }
            }
            var sa = fr.Slope();
            var sb = fb.Slope();
            if (sa.HasValue && sb.HasValue)
            {
                double cost = (sa.Value.Rss + sb.Value.Rss) / (fr.N + fb.N);
                if (double.IsFinite(sa.Value.Slope) && double.IsFinite(sb.Value.Slope)
                    && (!best.HasValue || cost < best.Value.Cost))
                    best = (sa.Value.Slope, sb.Value.Slope, cost);
            }
        }
        if (best.HasValue && best.Value.A is >= 0.25 and <= 16.0 && best.Value.B is >= 0.25 and <= 16.0)
            return (best.Value.A, best.Value.B);
        return null;
    }

    private static int? AverageBlackAt(short version) => version switch
    {
        6 or 7 or 9 => 231,
        10 or 11 => 276,
        12 or 13 or 15 => 326,
        _ => null,
    };

    internal static float? ColourDataBlack(ulong[] words)
    {
        if (words.Length == 0) return null;
        short version = (short)(ushort)words[0];
        int? at = AverageBlackAt(version);
        if (at is null || at.Value < 0 || (long)at.Value + 4 > words.Length) return null;
        ulong lo = words[at.Value], hi = words[at.Value];
        ulong sum = 0;
        for (int k = 0; k < 4; k++)
        {
            ulong v = words[at.Value + k];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
            sum += v;
        }
        return lo is >= 256 and <= 4096 && hi - lo <= 8 ? sum / 4f : null;
    }

    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, TiffFile tiff, Ifd raw, DecodeMode mode)
    {
        Ifd ifd0 = tiff.Ifds[0];
        ulong? off = raw.U64(Tags.StripOffsets);
        if (off is null) throw RawException.Tiff($"missing required tag {Tags.StripOffsets}");
        ulong len = raw.U64(Tags.StripByteCounts) ?? (ulong)bytes.Length - Math.Min(off.Value, (ulong)bytes.Length);
        long o = off.Value > long.MaxValue ? long.MaxValue : (long)off.Value;
        long l = len > long.MaxValue ? long.MaxValue : (long)len;
        var chunk = new TiffChunk(0, 0, 0, 0, 0, 0, o, l);
        ReadOnlyMemory<byte>? src = TiffImage.ChunkBytes(bytes, chunk);
        if (src is null) throw RawException.Corrupt("raw strip outside file");

        var fi = Ljpeg.FrameInfoSubsampled(src.Value.Span);
        int fw = fi.Width, fh = fi.Height, vertical = fi.Vertical;
        ulong[] slices = raw.U64s(Cr2.Cr2SliceTag) ?? [];
        Layout layout = Layout.Create(fw, fh, vertical, slices);

        MakerNote? mn = Cr2.ParseMakerNote(bytes, tiff, ifd0);
        int w = layout.Width, h = layout.Height;
        PixelRect active = Cr2.ActiveFromSensorInfo(mn, w, h);
        TiffValue? cb = mn?.Ifd.Value(Cr2.ColorBalanceTag);
        ulong[] colourWords = cb is not null && mn is not null ? Cr2.Words(cb, mn.Order) : [];
        float? blackCounts = ColourDataBlack(colourWords);
        float white = LumaGain * (SensorFull - blackCounts.GetValueOrDefault(0f));

        ushort[] data = mode == DecodeMode.Full ? DecodePlanes(src.Value, fw, fh, vertical, layout, w, h, active, bytes, tiff) : [];

        Metadata metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)active.Width;
        metadata.Height = (uint)active.Height;
        var img = new RawImage
        {
            Format = RawFormat.Cr2,
            Width = w,
            Height = h,
            Cpp = 3,
            Data = new RawData.U16(data),
            Cfa = null,
            Bits = 14,
            Black = BlackLevel.Uniform(0f),
            White = [white],
            ActiveArea = active,
            Crop = new PixelRect(0, 0, active.Width, active.Height),
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = new ColorData(),
            WbMultipliers = [1f, 1f, 1f],
            Linearized = false,
            PixelAspect = 1f,
            Opcodes = new OpcodeLists(),
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }

    private static ushort[] DecodePlanes(ReadOnlyMemory<byte> src, int fw, int fh, int vertical,
        Layout layout, int w, int h, PixelRect active, ReadOnlyMemory<byte> bytes, TiffFile tiff)
    {
        long samples;
        try
        {
            samples = checked(checked((long)fw * fh) + (long)fw * fh / vertical);
        }
        catch (OverflowException)
        {
            throw RawException.Limit("sRAW frame too large");
        }
        var frame = Ljpeg.DecodeSubsampled(src.Span, (int)Math.Min(samples, RawLimits.MaxSamples), Ljpeg.Prediction.Sequential);
        Planes planes = Planes.Arrange(frame.Planes, layout);
        Preview? preview = ReadPreview(bytes, tiff);
        (double a, double b) = (1.0, 1.0);
        if (preview is not null && ChromaGains(planes, preview, active) is { } gains)
            (a, b) = gains;
        return planes.ToRgb(a, b);
    }
}
