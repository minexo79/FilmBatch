namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 裁切參數（<c>crop.*</c>）。<see cref="Cx"/>、<see cref="Cy"/>、<see cref="W"/>、<see cref="H"/> 為「定向後」
/// 影像（套用 90° 旋轉與翻轉、拉直前）的正規化座標；<see cref="Quarter"/> 為順時針 90° 的次數。
/// </summary>
public readonly record struct CropParams(bool On, double Cx, double Cy, double W, double H,
    double AngleDeg, int Quarter, bool FlipH, bool FlipV);

/// <summary>2D 仿射：(x, y) → (M00·x + M01·y + M02, M10·x + M11·y + M12)。</summary>
public readonly record struct Affine2(double M00, double M01, double M02, double M10, double M11, double M12)
{
    public static Affine2 Identity => new(1, 0, 0, 0, 1, 0);

    public (double X, double Y) Apply(double x, double y) => (M00 * x + M01 * y + M02, M10 * x + M11 * y + M12);

    /// <summary>先套用 this，再套用 <paramref name="n"/>。</summary>
    public Affine2 Then(Affine2 n) => new(
        n.M00 * M00 + n.M01 * M10, n.M00 * M01 + n.M01 * M11, n.M00 * M02 + n.M01 * M12 + n.M02,
        n.M10 * M00 + n.M11 * M10, n.M10 * M01 + n.M11 * M11, n.M10 * M02 + n.M11 * M12 + n.M12);

    public Affine2 Inverse()
    {
        var det = M00 * M11 - M01 * M10;
        var a = M11 / det; var b = -M01 / det; var d = -M10 / det; var e = M00 / det;
        return new(a, b, -(a * M02 + b * M12), d, e, -(d * M02 + e * M12));
    }
}

/// <summary>
/// 裁切的計算結果：輸出尺寸與「輸出像素座標 → 來源 / 定向像素座標」的仿射。
/// 座標慣例：像素中心為 (x + 0.5, y + 0.5)。
/// </summary>
public readonly record struct CropTransform(int OutWidth, int OutHeight, Affine2 OutToSource,
    Affine2 OutToOriented, int OrientedWidth, int OrientedHeight);

/// <summary>
/// 裁切幾何：來源 →（90° 旋轉、翻轉）→ 定向影像 →（以裁切中心拉直）→ 裁切框。
/// 裁切框以定向影像的正規化座標表示（中心 + 寬高）。CPU 與 crop.wgsl 共用此變換。
/// </summary>
public static class CropGeometry
{
    /// <summary>
    /// 從設定讀出 <c>crop.*</c>（缺少的 key 用 schema 預設）。數值與 <see cref="ModuleParams"/> 一樣先轉成 float，
    /// 讓預覽的「原圖對比」與算圖結果得到完全相同的輸出尺寸。
    /// </summary>
    public static CropParams FromSettings(FilmBatch.Core.Params.Settings s)
    {
        static double N(FilmBatch.Core.Params.Settings s, string k, float def) => s.ContainsKey(k) ? (float)s.Number(k) : def;
        static bool B(FilmBatch.Core.Params.Settings s, string k) => s.ContainsKey(k) && s.Bool(k);
        return new CropParams(B(s, "crop.enabled"),
            N(s, "crop.rect_cx", 0.5f), N(s, "crop.rect_cy", 0.5f), N(s, "crop.rect_w", 1), N(s, "crop.rect_h", 1),
            N(s, "crop.angle", 0), s.ContainsKey("crop.quarter") ? (int)Math.Round(s.Number("crop.quarter")) : 0,
            B(s, "crop.flip_h"), B(s, "crop.flip_v"));
    }

    public static CropTransform Compute(int srcW, int srcH, CropParams c)
    {
        if (!c.On) return new(srcW, srcH, Affine2.Identity, Affine2.Identity, srcW, srcH);
        var q = ((c.Quarter % 4) + 4) % 4;
        var (ow, oh) = q % 2 == 1 ? (srcH, srcW) : (srcW, srcH);
        var outW = Math.Max(1, (int)Math.Round(c.W * ow));
        var outH = Math.Max(1, (int)Math.Round(c.H * oh));
        var a = c.AngleDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        // 輸出 → 定向：以輸出中心為原點旋轉，再平移到裁切中心
        var outToOriented = new Affine2(1, 0, -outW / 2.0, 0, 1, -outH / 2.0)
            .Then(new Affine2(cos, -sin, 0, sin, cos, 0))
            .Then(new Affine2(1, 0, c.Cx * ow, 0, 1, c.Cy * oh));
        return new(outW, outH, outToOriented.Then(OrientedToSource(srcW, srcH, ow, oh, q, c.FlipH, c.FlipV)),
            outToOriented, ow, oh);
    }

    /// <summary>定向座標 → 來源座標：先還原翻轉，再逆旋轉 90° 倍數（順時針為正）。</summary>
    private static Affine2 OrientedToSource(int srcW, int srcH, int ow, int oh, int q, bool flipH, bool flipV)
    {
        var unflip = new Affine2(flipH ? -1 : 1, 0, flipH ? ow : 0, 0, flipV ? -1 : 1, flipV ? oh : 0);
        var unrotate = q switch
        {
            1 => new Affine2(0, 1, 0, -1, 0, srcH),          // 順時針 90° 的逆
            2 => new Affine2(-1, 0, srcW, 0, -1, srcH),
            3 => new Affine2(0, -1, srcW, 1, 0, 0),
            _ => Affine2.Identity,
        };
        return unflip.Then(unrotate);
    }

    /// <summary>裁切框四個角是否都落在來源影像內。</summary>
    public static bool FitsInside(int srcW, int srcH, CropParams c)
    {
        var t = Compute(srcW, srcH, c);
        const double eps = 1e-6;
        foreach (var (x, y) in new[] { (0.0, 0.0), (t.OutWidth, 0.0), (0.0, t.OutHeight), (t.OutWidth, (double)t.OutHeight) })
        {
            var (sx, sy) = t.OutToSource.Apply(x, y);
            if (sx < -eps || sy < -eps || sx > srcW + eps || sy > srcH + eps) return false;
        }
        return true;
    }

    /// <summary>
    /// 讓裁切框留在影像內（作法參考 LightCraft 的 <c>CropGeometry::constrained</c>）：
    /// ① 已在影像內就不動；② 沿「目前中心 → 影像中心」的路徑找第一個放得下的位置（大小不變）；
    /// ③ 移到中心仍放不下（通常是拉直角度太大），才在影像中心以二分搜尋等比縮小寬高（比例不變）。
    /// </summary>
    public static CropParams Constrain(int srcW, int srcH, CropParams c)
    {
        if (FitsInside(srcW, srcH, c)) return c;
        // ② 先移動：二分搜尋「往中心走多少比例」剛好放得下，讓框停在最靠近使用者拖到的位置
        var centered = c with { Cx = 0.5, Cy = 0.5 };
        if (FitsInside(srcW, srcH, centered))
        {
            double lo = 0, hi = 1;   // hi 一定放得下
            for (var i = 0; i < 40; i++)
            {
                var mid = (lo + hi) / 2;
                var cand = c with { Cx = c.Cx + (0.5 - c.Cx) * mid, Cy = c.Cy + (0.5 - c.Cy) * mid };
                if (FitsInside(srcW, srcH, cand)) hi = mid; else lo = mid;
            }
            return c with { Cx = c.Cx + (0.5 - c.Cx) * hi, Cy = c.Cy + (0.5 - c.Cy) * hi };
        }
        // ③ 再縮小
        double s0 = 0, s1 = 1;
        for (var i = 0; i < 40; i++)
        {
            var mid = (s0 + s1) / 2;
            if (FitsInside(srcW, srcH, centered with { W = c.W * mid, H = c.H * mid })) s0 = mid; else s1 = mid;
        }
        return centered with { W = Math.Max(0.02, c.W * s0), H = Math.Max(0.02, c.H * s0) };
    }

    /// <summary>
    /// 比例 key（<c>crop.aspect</c>）換算成像素寬 / 高比；<c>free</c> 回傳 <see cref="double.NaN"/>。
    /// <c>4x5</c>、<c>6x7</c> 是「短:長」的慣用寫法，一律換算成「長 / 短」再依方向取倒數。
    /// </summary>
    public static double AspectRatio(string key, bool portrait, int orientedW, int orientedH)
    {
        double r = key switch
        {
            "original" => (double)Math.Max(orientedW, orientedH) / Math.Min(orientedW, orientedH),
            "1x1" => 1, "4x5" => 5.0 / 4, "3x2" => 1.5, "16x9" => 16.0 / 9, "6x7" => 7.0 / 6, "xpan" => 65.0 / 24,
            _ => double.NaN,
        };
        if (double.IsNaN(r)) return r;
        // original 依影像本身方向；其他比例以 portrait 決定橫直
        var landscape = key == "original" ? orientedW >= orientedH : !portrait;
        return landscape ? r : 1 / r;
    }
}
