// 移植自 LightCraft crates/raw/src/wb_estimate.rs（無白平衡記錄的 RAW 以內嵌預覽量測拍攝白平衡）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw;

/// <summary>白平衡估計看的原始圖框（lightcraft <c>wb_estimate::Frame</c>）。</summary>
internal sealed class WbFrame
{
    internal const long MaxPreviewPixels = 1 << 24;
    public required ushort[] Data { get; set; }
    public required int Width { get; set; }
    public required PixelRect Crop { get; set; }
    public required Cfa Cfa { get; set; }
    public required BlackLevel Black { get; set; }
    public required float White { get; set; }
}

/// <summary>
/// 以內嵌預覽的中性像素量測拍攝白平衡（lightcraft <c>wb_estimate</c>）。
/// 相機以拍攝白平衡算繪預覽，故預覽中呈中性灰的像素在該白平衡下為中性；
/// 預覽像素格內按顏色平均馬賽克，中性格的中值綠／紅、綠／藍比即增益。
/// 中性像素不足時退回全圖平均（grey world）。
/// </summary>

internal static class WbEstimate {
    /// <summary>預覽像素最大最小通道差小於此值（8-bit）才算中性。</summary>
    private const int NeutralSpread = 12;
    /// <summary>至少需要這麼多中性預覽像素。</summary>
    private const int MinNeutral = 64;
    /// <summary>增益 [R/G, 1, B/G]：內嵌預覽中性像素，否則 grey world（lightcraft <c>as_shot</c>）。</summary>
    internal static float[]? FromRgb(byte[] px, int pw, int ph, WbFrame frame) =>
        FromRgbPixels(px, pw, ph, frame) ?? GreyWorld(frame);

    internal static float[]? FromPreview(ReadOnlyMemory<byte> fileBytes, WbFrame frame) =>
        FromPreviewPixels(RawPreview.EmbeddedPreview(fileBytes), frame) ?? GreyWorld(frame);

    private static float[]? FromPreviewPixels(byte[]? jpeg, WbFrame frame)
    {
        if (jpeg is null) return null;
        byte[] px;
        int pw, ph, ch;
        try { (px, pw, ph, ch) = TiffRaw.LossyJpeg(jpeg, 3); }
        catch (RawException) { return null; }
        if (ch != 3) return null;
        return FromRgbPixels(px, pw, ph, frame);
    }

    private static float[]? FromRgbPixels(byte[] px, int pw, int ph, WbFrame frame)
    {
        long area;
        try { area = checked((long)pw * ph); }
        catch (OverflowException) { return null; }
        if (area > WbFrame.MaxPreviewPixels || (long)px.Length < area * 3)
            return null;
        // 預覽須呈現裁切區：長寬比誤差 3% 內
        double ra = (double)frame.Crop.Width / Math.Max(1, frame.Crop.Height);
        double pa = (double)pw / ph;
        if (Math.Abs(ra / pa - 1.0) > 0.03)
            return null;
        var cells = CellMeans(frame, pw, ph);
        if (cells is null) return null;
        var gr = new List<double>();
        var gb = new List<double>();
        for (var i = 0; i < cells.Length; i++)
        {
            var (m, clip) = cells[i];
            if ((long)i * 3 + 3 > px.Length) break;
            int p0 = px[i * 3], p1 = px[i * 3 + 1], p2 = px[i * 3 + 2];
            int lo = Math.Min(p0, Math.Min(p1, p2)), hi = Math.Max(p0, Math.Max(p1, p2));
            if (clip || lo < 30 || hi > 240 || hi - lo >= NeutralSpread || m[0] <= 1.0 || m[1] <= 1.0 || m[2] <= 1.0)
                continue;
            gr.Add(m[1] / m[0]);
            gb.Add(m[1] / m[2]);
        }
        if (gr.Count < MinNeutral) return null;
        return Gains(Median(gr), Median(gb));
    }

    /// <summary>每預覽格的 R/G/B 均值（扣黑階）與格內是否有截斷樣本。</summary>
    private static (double[] Mean, bool Clipped)[]? CellMeans(WbFrame frame, int pw, int ph)
    {
        var c = frame.Crop;
        if (pw <= 0 || ph <= 0 || c.Width < pw || c.Height < ph || frame.Width == 0)
            return null;
        long n;
        try { n = checked((long)pw * ph); }
        catch (OverflowException) { return null; }
        if (n > int.MaxValue) return null;
        var sums = new double[n * 3];
        var counts = new uint[n * 3];
        var clipped = new bool[n];
        long xEnd;
        try { xEnd = checked((long)c.X + c.Width); }
        catch (OverflowException) { return null; }
        long yEnd;
        try { yEnd = checked((long)c.Y + c.Height); }
        catch (OverflowException) { return null; }
        for (long y = c.Y; y < yEnd; y++)
        {
            if (y < 0 || y > int.MaxValue) continue;
            long rowOff;
            try { rowOff = checked(y * frame.Width); }
            catch (OverflowException) { continue; }
            if (rowOff < 0 || rowOff + frame.Width > frame.Data.Length) continue;
            long cy = (y - c.Y) * ph / Math.Max(1, c.Height);
            long xMin = Math.Max((long)c.X, 0), xMax = Math.Min(xEnd, frame.Width);
            for (long x = xMin; x < xMax; x++)
            {
                long cx = (x - c.X) * pw / Math.Max(1, c.Width);
                long cell = cy * pw + cx;
                if (cell < 0 || cell >= n) continue;
                float v = frame.Data[rowOff + x];
                int col = Math.Min((int)frame.Cfa.ColorAt((int)x, (int)y), 2);
                if (v >= frame.White * 0.98f) clipped[cell] = true;
                sums[cell * 3 + col] += v - frame.Black.At((int)x, (int)y, 0, 1);
                counts[cell * 3 + col]++;
            }
        }
        var output = new (double[] Mean, bool Clipped)[n];
        for (long i = 0; i < n; i++)
        {
            double r = counts[i * 3] > 0 ? sums[i * 3] / counts[i * 3] : 0.0;
            double g = counts[i * 3 + 1] > 0 ? sums[i * 3 + 1] / counts[i * 3 + 1] : 0.0;
            double b = counts[i * 3 + 2] > 0 ? sums[i * 3 + 2] / counts[i * 3 + 2] : 0.0;
            output[i] = (new double[] { r, g, b }, clipped[i]);
        }
        return output;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    private static float[]? Gains(double gr, double gb)
    {
        static bool Ok(double g) => double.IsFinite(g) && g is >= 0.1 and <= 10.0;
        return Ok(gr) && Ok(gb) ? [(float)gr, 1f, (float)gb] : null;
    }

    private static float[]? GreyWorld(WbFrame frame)
    {
        var c = frame.Crop;
        var s = new double[3];
        var n = new ulong[3];
        long xEnd, yEnd;
        try { xEnd = checked((long)c.X + c.Width); yEnd = checked((long)c.Y + c.Height); }
        catch (OverflowException) { return null; }
        for (long y = c.Y; y < yEnd; y++)
        {
            if (y < 0 || y > int.MaxValue) continue;
            for (long x = Math.Max((long)c.X, 0); x < Math.Min(xEnd, frame.Width); x++)
            {
                long idx;
                try { idx = checked(y * frame.Width + x); }
                catch (OverflowException) { continue; }
                if (idx < 0 || idx >= frame.Data.Length) continue;
                float v = frame.Data[idx];
                if (v >= frame.White * 0.98f) continue;
                int k = Math.Min((int)frame.Cfa.ColorAt((int)x, (int)y), 2);
                s[k] += Math.Max(v - frame.Black.At((int)x, (int)y, 0, 1), 0f);
                n[k]++;
            }
        }
        if (n[0] == 0 || n[1] == 0 || n[2] == 0) return null;
        double m0 = s[0] / n[0], m1 = s[1] / n[1], m2 = s[2] / n[2];
        if (m0 <= 0 || m1 <= 0 || m2 <= 0) return null;
        return Gains(m1 / m0, m1 / m2);
    }
}
