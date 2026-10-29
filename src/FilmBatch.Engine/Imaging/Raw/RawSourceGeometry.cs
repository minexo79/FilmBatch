using FilmBatch.Raw;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>Immutable output-to-active-sensor mapping. Pixel centres use edge-normalized coordinates.</summary>
internal sealed class RawSourceGeometry
{
    private readonly int width, height;
    private readonly PixelRect crop;
    private readonly SourcePlacement placement;
    private readonly Stage[] stages;

    private RawSourceGeometry(RawImage raw, int binFactor, IReadOnlyList<Opcode>? ops = null, bool sensor = false)
    {
        width = Math.Max(1, raw.ActiveArea.Width);
        height = Math.Max(1, raw.ActiveArea.Height);
        crop = sensor ? new PixelRect(0, 0, width, height) : raw.DevelopCrop(width, height);
        if (binFactor > 1)
        {
            if (crop.Width < binFactor || crop.Height < binFactor) crop = new(0, 0, width, height);
            crop = new(crop.X, crop.Y, crop.Width / binFactor * binFactor, crop.Height / binFactor * binFactor);
        }
        placement = new(crop.X / (double)width, crop.Y / (double)height,
            crop.Width / (double)width, crop.Height / (double)height, sensor ? Orientation.Normal : raw.Orientation);
        stages = OpcodeOps.AppliedOps(ops ?? raw.Opcodes.List3).Select(Stage.From).OfType<Stage>().ToArray();
    }

    internal static RawSourceGeometry Create(RawImage raw) => new(raw, 1);
    // Binning discards incomplete blocks at the right/bottom, unlike a subsequent image resize.
    internal static RawSourceGeometry Create(RawImage raw, int binFactor) => new(raw, Math.Max(1, binFactor));

    internal static RawSourceGeometry CreateSensor(RawImage raw, IReadOnlyList<Opcode> ops) => new(raw, 1, ops, true);
    internal (double X, double Y) ActiveIndex(double x, double y) => Map(x, y, 1);

    internal (double X, double Y) Active(double u, double v)
    {
        var (ax, ay) = placement.Active(u, v);
        var (x, y) = Map(ax * width - .5, ay * height - .5, 1);
        return ((x + .5) / width, (y + .5) / height);
    }

    internal bool Covers(double u, double v)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0 || u > 1 || v < 0 || v > 1) return false;
        var (x, y) = Active(u, v);
        return double.IsFinite(x) && double.IsFinite(y) && x >= 0 && x <= 1 && y >= 0 && y <= 1;
    }

    private (double X, double Y) Map(double x, double y, int plane)
    {
        for (var i = stages.Length - 1; i >= 0; i--)
            (x, y) = stages[i].Map(x, y, plane, width, height);
        return (x, y);
    }

    /// <summary>
    /// Develop with one spatial sample for a contiguous lens chain. Non-spatial List3 operations retain their
    /// order and form explicit barriers; no opcode is removed from the caller's RawImage.
    /// </summary>
    internal static RgbImage Develop(RawImage raw, DemosaicMethod method)
    {
        if (raw.Opcodes.List3.Count == 0) return raw.Develop(method);
        var image = Demosaic.Run(raw.Normalized(), method);
        image = ApplyList3(raw.Opcodes.List3, image);
        var c = raw.DevelopCrop(image.Width, image.Height);
        if (c.X != 0 || c.Y != 0 || c.Width != image.Width || c.Height != image.Height)
            image = image.Crop(c.X, c.Y, c.Width, c.Height);
        var h = RawImage.StretchedHeight(image.Height, raw.PixelAspect);
        return h == image.Height ? image : Resample.Resize(image, image.Width, h, ResampleFilter.Mitchell);
    }

    internal static RgbImage ApplyList3(IReadOnlyList<Opcode> list, RgbImage image)
    {
        OpcodeOps.ValidateList3(list);
        var pending = new List<Opcode>();
        foreach (var op in OpcodeOps.AppliedOps(list))
        {
            if (op is WarpRectilinear or WarpRectilinear2 or FixVignetteRadial) { pending.Add(op); continue; }
            if (pending.Count > 0) { image = ApplyLensChain(pending, image); pending.Clear(); }
            image = OpcodeOps.ApplyList3([op], image);
        }
        return pending.Count > 0 ? ApplyLensChain(pending, image) : image;
    }

    private static RgbImage ApplyLensChain(List<Opcode> ops, RgbImage image)
    {
        var chain = Chain(ops);
        var w = image.Width; var h = image.Height;
        var result = new RgbImage(w, h);
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            for (var x = 0; x < w; x++)
                for (var ch = 0; ch < 3; ch++)
                {
                    var (sx, sy, gain) = MapChain(chain, x, y, ch, w, h);
                    result.Data[(y * w + x) * 3 + ch] = Sample(image, sx, sy, ch) * (float)gain;
                }
        });
        return result;
    }

    /// <summary>List3 是否只含鏡頭鏈（warp／徑向暗角）：此時可以先分箱，再於分箱座標套用同一條鏈。</summary>
    internal static bool IsLensChainOnly(IReadOnlyList<Opcode> list) =>
        OpcodeOps.AppliedOps(list).All(op => op is WarpRectilinear or WarpRectilinear2 or FixVignetteRadial);

    /// <summary>
    /// 在分箱影像上套用鏡頭鏈（預覽代理用）：每個輸出區塊以其中心的有效區域座標走與 <see cref="ApplyLensChain"/>
    /// 相同的映射，再換回分箱座標雙線性取樣。<paramref name="region"/> 為 <see cref="RawImage.BinnedRegion"/>；
    /// <paramref name="activeWidth"/>／<paramref name="activeHeight"/> 為鏡頭運算碼所定義的完整解析度有效區域。
    /// </summary>
    internal static RgbImage ApplyLensChainBinned(IReadOnlyList<Opcode> list, RgbImage image,
        PixelRect region, int k, int activeWidth, int activeHeight)
    {
        OpcodeOps.ValidateList3(list);
        var chain = Chain(OpcodeOps.AppliedOps(list));
        if (chain.Length == 0) return image;
        var w = image.Width; var h = image.Height;
        var half = (k - 1) / 2d;
        var result = new RgbImage(w, h);
        Parallel.For(0, h, RawProcessingScope.Options, by =>
        {
            for (var bx = 0; bx < w; bx++)
            {
                double x = region.X + bx * k + half, y = region.Y + by * k + half;
                for (var ch = 0; ch < 3; ch++)
                {
                    var (sx, sy, gain) = MapChain(chain, x, y, ch, activeWidth, activeHeight);
                    result.Data[(by * w + bx) * 3 + ch] =
                        Sample(image, (sx - region.X - half) / k, (sy - region.Y - half) / k, ch) * (float)gain;
                }
            }
        });
        return result;
    }

    private static (Opcode Op, Stage? Warp)[] Chain(IEnumerable<Opcode> ops) =>
        ops.Where(op => op is WarpRectilinear or WarpRectilinear2 or FixVignetteRadial)
            .Select(op => (Op: op, Warp: Stage.From(op))).ToArray();

    /// <summary>輸出像素 (x, y)（有效區域像素中心座標）經鏡頭鏈反向映射到來源座標與暗角增益。</summary>
    private static (double X, double Y, double Gain) MapChain((Opcode Op, Stage? Warp)[] chain, double x, double y, int ch, int w, int h)
    {
        double sx = x, sy = y, gain = 1;
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            if (chain[i].Warp is { } warp) (sx, sy) = warp.Map(sx, sy, ch, w, h);
            else if (chain[i].Op is FixVignetteRadial v)
            {
                var cx = v.Center[0] * w; var cy = v.Center[1] * h;
                var radius = double.Hypot(Math.Max(cx, w - cx), Math.Max(cy, h - cy));
                var r2 = ((sx + .5 - cx) * (sx + .5 - cx) + (sy + .5 - cy) * (sy + .5 - cy)) / Math.Max(radius * radius, 1e-18);
                var f = 0d;
                for (var k = 4; k >= 0; k--) f = f * r2 + v.K[k];
                gain *= 1 + r2 * f;
            }
        }
        return (sx, sy, gain);
    }

    // Match the raster boundary: continuous pixel centres become f32 before bilinear interpolation.
    private static float Sample(RgbImage image, double x, double y, int ch)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        var fx = Math.Clamp((float)(x + .5) - .5f, 0, image.Width - 1f);
        var fy = Math.Clamp((float)(y + .5) - .5f, 0, image.Height - 1f);
        var x0 = (int)MathF.Floor(fx); var y0 = (int)MathF.Floor(fy);
        var x1 = Math.Min(x0 + 1, image.Width - 1); var y1 = Math.Min(y0 + 1, image.Height - 1);
        var tx = fx - x0; var ty = fy - y0;
        var a = image.Data[(y0 * image.Width + x0) * 3 + ch];
        var b = image.Data[(y0 * image.Width + x1) * 3 + ch];
        var c = image.Data[(y1 * image.Width + x0) * 3 + ch];
        var d = image.Data[(y1 * image.Width + x1) * 3 + ch];
        var top = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    private sealed record Stage(double[][] Planes, double[] Center, bool Wr2, bool Reciprocal)
    {
        internal static Stage? From(Opcode op)
        {
            var data = op switch
            {
                WarpRectilinear w => new Stage(w.Planes, w.Center, false, false),
                WarpRectilinear2 w => new Stage(w.Planes, w.Center, true, w.Reciprocal),
                _ => null,
            };
            if (data is null || data.Center.Length != 2 || !data.Center.All(double.IsFinite)
                || data.Planes.Length == 0 || data.Planes.Any(p => p.Length < (data.Wr2 ? 19 : 6) || !p.All(double.IsFinite))) return null;
            return data with { Planes = data.Planes.Select(p => (double[])p.Clone()).ToArray(), Center = (double[])data.Center.Clone() };
        }

        internal (double X, double Y) Map(double x, double y, int plane, int w, int h)
        {
            var cx = Center[0] * (w - 1d); var cy = Center[1] * (h - 1d);
            var radius = Math.Max(double.Hypot(Math.Max(Math.Abs(cx), Math.Abs(w - 1 - cx)), Math.Max(Math.Abs(cy), Math.Abs(h - 1 - cy))), 1e-9);
            var dx = (x - cx) / radius; var dy = (y - cy) / radius;
            var r2 = dx * dx + dy * dy;
            var k = Planes[Math.Min(plane, Planes.Length - 1)];
            var f = Wr2 ? OpcodeOps.Wr2Factor(k, Reciprocal, Math.Sqrt(r2)) : k[0] + r2 * (k[1] + r2 * (k[2] + r2 * k[3]));
            if (f is not { } factor) return (x, y);
            var ti = Wr2 ? 15 : 4;
            return (cx + radius * (dx * factor + k[ti] * 2 * dx * dy + k[ti + 1] * (r2 + 2 * dx * dx)),
                cy + radius * (dy * factor + k[ti + 1] * 2 * dx * dy + k[ti] * (r2 + 2 * dy * dy)));
        }
    }
}
