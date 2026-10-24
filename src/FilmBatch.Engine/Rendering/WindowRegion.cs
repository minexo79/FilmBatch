using FilmBatch.Core.Random;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 窗口渲染的實際渲染區域：從要求的窗口出發，沿 pass 順序反向推算每個 pass 需要的輸入範圍
/// （有界鄰域＝外擴半徑；取樣位置會移動的 pass＝逐像素取樣位置的外接矩形），
/// 取所有範圍的聯集並夾在畫框內。渲染區域內每個 pass 讀到的鄰居都與整張渲染相同，
/// 因此裁回窗口後的結果與整張渲染一致（邊界夾取只發生在畫框邊緣，與整張渲染相同）。
/// 暗部還原啟用時，區域邊界對齊代理格區塊，使區塊統計與整張渲染逐格相同。
/// CPU 與 GPU 使用同一份結果。
/// </summary>
public static class WindowRegion
{
    /// <summary>邊界的保險像素：涵蓋雙線性的下一個像素與 CPU／GPU 半徑四捨五入的差異。</summary>
    private const int Slack = 2;

    private readonly record struct Rect(int X0, int Y0, int X1, int Y1)
    {
        public Rect Grow(int pad) => new(X0 - pad, Y0 - pad, X1 + pad, Y1 + pad);
        public Rect Union(Rect o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
        public Rect Clamp(int w, int h) => new(Math.Max(0, X0), Math.Max(0, Y0), Math.Min(w, X1), Math.Min(h, Y1));
    }

    /// <summary>
    /// 為 <paramref name="view"/> 計算要渲染的區域（畫框座標，包含 <paramref name="view"/>）。
    /// <paramref name="p"/> 必須是整個畫框的參數（<see cref="ModuleParams.From"/> 且素材已備妥）。
    /// 回傳的區域可能等於整個畫框（例如強烈鏡頭變形讓窗口取樣到整張畫面）。
    /// </summary>
    public static RenderView Expand(ModuleParams p, RenderView view)
    {
        int fw = p.FrameWidth, fh = p.FrameHeight;
        if (view.FrameW != fw || view.FrameH != fh)
            throw new ArgumentException($"窗口的畫框 {view.FrameW}×{view.FrameH} 與設定算出的畫框 {fw}×{fh} 不符。", nameof(view));
        view.Validate();
        var shortSide = MathF.Min(fw, fh);
        var r = new Rect(view.X, view.Y, view.X + view.W, view.Y + view.H);
        var all = r;
        void Grow(int pad) { r = r.Grow(pad).Clamp(fw, fh); all = all.Union(r); }
        void Need(Rect needed) { r = needed.Clamp(fw, fh); all = all.Union(r); }

        // ⑤ 表面：顆粒柔焦（灰塵刮痕與材質疊加逐像素）
        if (p.GrainOn && (p.GrainAmount > 0 || p.GrainSoftness > 0))
        {
            var d = CpuPipeline.DeriveGrain(p, fw, fh);
            if (CpuPipeline.GrainSoftBlur(d.Geom.RefShort, p.GrainSoftness / 100f, d.Geom.PixelScale) is { } soft)
                Grow(soft.Radius + Slack);
        }
        // ④ 顯示：銳化 → 清晰（反向）→ 細節提取
        if (p.DetailOn && p.Sharpen != 0) Grow(Math.Max(1, (int)MathF.Round(shortSide * 0.004f)) + Slack);
        if (p.DetailOn && p.Clarity != 0) Grow(Math.Max(1, (int)MathF.Round(shortSide * 0.02f)) + Slack);
        if (p.BasicOn && p.Structure != 0) Grow(Math.Max(1, (int)MathF.Round(shortSide * 0.012f)) + Slack);
        // ② 光學（反向）：光暈 → 動態 → 縮放旋轉 → 散景 → 雙重曝光
        if (p.HalationOn && p.HalAmount > 0)
            Grow(Math.Max(1, (int)MathF.Round(p.HalRadius / 100f * 0.08f * shortSide)) + Slack);
        if (p.MotionOn && p.MotionLength > 0)
            Grow((int)MathF.Ceiling(p.MotionLength / 100f * 0.2f * shortSide * 0.5f) + Slack);
        if (p.ZoomRotateOn && (p.Zoom != 0 || p.Rotate != 0))
        {
            var zoomLen = MathF.Abs(p.Zoom) / 100f * 0.15f * shortSide;
            var angle = 0.5f * MathF.Abs(p.Rotate) / 100f * 0.1f;   // 最大旋轉角（弧度）
            var cx = p.ZrCx * fw;
            var cy = p.ZrCy * fh;
            var far = 0f;
            foreach (var (x, y) in new[] { (r.X0, r.Y0), (r.X1, r.Y0), (r.X0, r.Y1), (r.X1, r.Y1) })
                far = MathF.Max(far, MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)));
            // 旋轉的弦長 ≤ 半徑 × 角度，再加放射方向位移
            Grow((int)MathF.Ceiling(0.5f * zoomLen + far * angle) + Slack);
        }
        if (p.BokehOn && p.BokehBlur > 0)
        {
            var radius = p.BokehBlur / 100f * shortSide * .03f;
            var reach = 0f;
            foreach (var point in ApertureSamples.ForShape(p.BokehShape))
                reach = MathF.Max(reach, MathF.Max(MathF.Abs(point.X), MathF.Abs(point.Y)));
            Grow((int)MathF.Ceiling(radius * reach) + Slack);
        }
        if (p.DoubleOn && p.Opacity > 0 && p.DoubleAsset is null) Need(r.Union(DoubleSelfBounds(p, r)));
        // ① 幾何（反向）：多格分鏡 → 鏡頭變形
        if (p.MultiOn) Need(r.Union(MultiBounds(p, r)));
        if (p.LensOn) Need(r.Grow(1 + Slack).Union(LensBounds(p, r).Grow(1)));
        // 暗部還原：代理格盒狀濾波兩輪（各外擴 ⌊半徑⌋+1 格）、雙線性上採樣與區塊本身，加上降噪半視窗
        var factor = 0;
        if (p.ShadowRecoveryOn)
        {
            var layout = ShadowRecovery.Layout(p, fw, fh);
            if (layout.Exposure != 0f || layout.Luma != 0f || layout.Chroma != 0f)
            {
                factor = layout.Factor;
                var cells = 2 * ((int)MathF.Floor(layout.ProxyRadius) + 1) + 3;
                Grow(cells * factor + layout.Support + Slack);
            }
        }

        var region = all.Clamp(fw, fh);
        if (factor > 1)
            region = new Rect(region.X0 / factor * factor, region.Y0 / factor * factor,
                Math.Min(fw, (region.X1 + factor - 1) / factor * factor),
                Math.Min(fh, (region.Y1 + factor - 1) / factor * factor));
        return new RenderView(fw, fh, region.X0, region.Y0, region.X1 - region.X0, region.Y1 - region.Y0);
    }

    /// <summary>從渲染區域（<paramref name="region"/> 大小的 BGRA）裁出 <paramref name="view"/>（兩者皆為畫框座標）。</summary>
    internal static byte[] CropBgra(byte[] pixels, RenderView region, RenderView view)
    {
        if (region == view) return pixels;
        var result = new byte[checked(view.W * view.H * 4)];
        for (var y = 0; y < view.H; y++)
            Array.Copy(pixels, ((view.Y - region.Y + y) * region.W + view.X - region.X) * 4, result, y * view.W * 4, view.W * 4);
        return result;
    }

    /// <summary>渲染區域的 16-bit RGBA → 裁出 <paramref name="view"/> 的 8-bit BGRA（與 CPU 預覽相同的捨入）。</summary>
    internal static byte[] CropToBgra8(ushort[] rgba16, RenderView region, RenderView view)
    {
        var result = new byte[checked(view.W * view.H * 4)];
        for (var y = 0; y < view.H; y++)
        {
            var s = ((view.Y - region.Y + y) * region.W + view.X - region.X) * 4;
            var d = y * view.W * 4;
            for (var x = 0; x < view.W * 4; x += 4)
            {
                result[d + x] = (byte)((rgba16[s + x + 2] + 128) / 257);
                result[d + x + 1] = (byte)((rgba16[s + x + 1] + 128) / 257);
                result[d + x + 2] = (byte)((rgba16[s + x] + 128) / 257);
                result[d + x + 3] = (byte)((rgba16[s + x + 3] + 128) / 257);
            }
        }
        return result;
    }

    /// <summary>樣本座標（雙線性左上角）範圍 → 需要的像素矩形（含右下鄰居）。</summary>
    private static Rect FromSamples(float minX, float minY, float maxX, float maxY) =>
        float.IsFinite(minX) && float.IsFinite(minY) && float.IsFinite(maxX) && float.IsFinite(maxY)
            ? new((int)MathF.Floor(minX) - Slack, (int)MathF.Floor(minY) - Slack,
                (int)MathF.Floor(maxX) + 2 + Slack, (int)MathF.Floor(maxY) + 2 + Slack)
            : new(int.MinValue / 2, int.MinValue / 2, int.MaxValue / 2, int.MaxValue / 2);

    /// <summary>逐像素取樣位置外接矩形的平行歸約。</summary>
    private static Rect Bounds(Rect r, Action<int, int, float[]> visit)
    {
        object gate = new();
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        Parallel.For(r.Y0, r.Y1, () => new[] { float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity },
            (y, _, local) =>
            {
                for (var x = r.X0; x < r.X1; x++) visit(x, y, local);
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    minX = MathF.Min(minX, local[0]); minY = MathF.Min(minY, local[1]);
                    maxX = MathF.Max(maxX, local[2]); maxY = MathF.Max(maxY, local[3]);
                }
            });
        return FromSamples(minX, minY, maxX, maxY);
    }

    private static void Add(float[] bounds, float x, float y)
    {
        if (!(x >= bounds[0])) bounds[0] = float.IsNaN(x) ? float.NegativeInfinity : x;
        if (!(y >= bounds[1])) bounds[1] = float.IsNaN(y) ? float.NegativeInfinity : y;
        if (!(x <= bounds[2])) bounds[2] = float.IsNaN(x) ? float.PositiveInfinity : x;
        if (!(y <= bounds[3])) bounds[3] = float.IsNaN(y) ? float.PositiveInfinity : y;
    }

    /// <summary>與 <see cref="CpuPipeline.LensDistortion"/> 同式的三通道取樣位置。</summary>
    private static Rect LensBounds(ModuleParams p, Rect r)
    {
        int w = p.FrameWidth, h = p.FrameHeight;
        var k1 = p.LensAmount / 100f * 0.6f;
        var k2 = p.LensAmount / 100f * 0.35f;
        var chroma = p.Chromatic / 100f * 0.02f;
        var crop = p.AutoCrop ? 1f / MathF.Max(1e-3f, 1f + k1 + k2) : 1f;
        if (k1 + k2 < 0) crop = p.AutoCrop ? 1f / MathF.Max(0.5f, 1f + k1 + k2) : 1f;
        var half = MathF.Min(w, h) * 0.5f;
        var cxn = w * 0.5f / half;
        var cyn = h * 0.5f / half;
        return Bounds(r, (x, y, b) =>
        {
            var u = (x + 0.5f) / half - cxn;
            var v = (y + 0.5f) / half - cyn;
            var r2 = u * u + v * v;
            foreach (var k in (ReadOnlySpan<float>)[k1 + chroma, k1, k1 - chroma])
            {
                var f = 1f + k * r2 + k2 * r2 * r2;
                Add(b, (u * f * crop + cxn) * half - 0.5f, (v * f * crop + cyn) * half - 0.5f);
            }
        });
    }

    /// <summary>與 <see cref="CpuPipeline.MultiLens"/> 同式：本格與（divider 時）相鄰格的取樣位置。</summary>
    private static Rect MultiBounds(ModuleParams p, Rect r)
    {
        int w = p.FrameWidth, h = p.FrameHeight;
        var (cols, rows) = p.MultiLayout switch
        {
            0 => (2, 1), 1 => (3, 1), 2 => (4, 1), 3 => (1, 2), 4 => (2, 2), 5 => (2, 2), _ => (2, 1),
        };
        var count = cols * rows;
        var dx = new float[count];
        var dy = new float[count];
        var zoom = new float[count];
        for (var i = 0; i < count; i++)
        {
            var s = p.SeedMultiLens + (uint)i * 747796405u;
            dx[i] = (SeedHash.ToUnitFloat(SeedHash.Pcg(s)) - 0.5f) * 2f * p.MultiOffset / 100f;
            dy[i] = (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 1)) - 0.5f) * 2f * p.MultiOffset / 100f;
            zoom[i] = 1f + (SeedHash.ToUnitFloat(SeedHash.Pcg(s + 2)) - 0.5f) * 0.3f * p.ZoomVar / 100f;
            if (p.MultiLayout == 5) dy[i] += (i % 2 == 0 ? 0.25f : -0.25f) * 0.3f;
        }
        var divider = p.Divider > 0;
        return Bounds(r, (x, y, b) =>
        {
            var u = (x + 0.5f) / w;
            var v = (y + 0.5f) / h;
            var col = Math.Min(cols - 1, (int)(u * cols));
            var row = Math.Min(rows - 1, (int)(v * rows));
            void Sample(int c, int rw)
            {
                var i = rw * cols + c;
                var cu = u * cols - c - 0.5f;
                var cv = v * rows - rw - 0.5f;
                Add(b, (c + 0.5f + cu / zoom[i] + dx[i]) * w / cols - 0.5f, (rw + 0.5f + cv / zoom[i] + dy[i]) * h / rows - 0.5f);
            }
            Sample(col, row);
            if (!divider) return;
            var ncol = Math.Clamp(col + (u * cols - col - 0.5f > 0 ? 1 : -1), 0, cols - 1);
            var nrow = Math.Clamp(row + (v * rows - row - 0.5f > 0 ? 1 : -1), 0, rows - 1);
            Sample(ncol, nrow);
        });
    }

    /// <summary>雙重曝光（第二張＝本張）：仿射映射，極值在四個角的像素中心。</summary>
    private static Rect DoubleSelfBounds(ModuleParams p, Rect r)
    {
        int w = p.FrameWidth, h = p.FrameHeight;
        var scale = p.DoubleScale / 100f;
        var rot = p.DoubleRotation * MathF.PI / 180f;
        var cos = MathF.Cos(rot);
        var sin = MathF.Sin(rot);
        var b = new[] { float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity };
        foreach (var (x, y) in new[] { (r.X0, r.Y0), (r.X1 - 1, r.Y0), (r.X0, r.Y1 - 1), (r.X1 - 1, r.Y1 - 1) })
        {
            var su = ((x + 0.5f) / w - 0.5f - p.OffsetX) / scale;
            var sv = ((y + 0.5f) / h - 0.5f - p.OffsetY) / scale;
            var ru = su * cos - sv * sin;
            var rv = su * sin + sv * cos;
            if (p.DoubleMirror == 1) ru = -ru;
            else if (p.DoubleMirror == 2) rv = -rv;
            Add(b, (ru + 0.5f) * w - 0.5f, (rv + 0.5f) * h - 0.5f);
        }
        // 仿射的四捨五入誤差：多留一個像素
        return FromSamples(b[0], b[1], b[2], b[3]).Grow(1);
    }
}
