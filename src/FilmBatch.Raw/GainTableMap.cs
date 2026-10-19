// 移植自 LightCraft crates/raw/src/gaintable.rs（DNG ProfileGainTableMap／ProfileGainTableMap2：
// Apple ProRAW 的局部色調映射）。預設不渲染；相機局部色調選項啟用時才套用（見 CameraLocalTone）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Buffers.Binary;
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw;

/// <summary>
/// 解析後的 <c>ProfileGainTableMap</c>（標籤 52525，DNG 1.6）／<c>ProfileGainTableMap2</c>
/// （標籤 52544，DNG 1.7）：以有效區為基準的粗網格一維增益表。
/// 套用規則（DNG 1.7.1）：位置周圍四表雙線性內插、網格外複製邊緣表；表輸入為線性
/// ProPhoto（基線曝光後）(R, G, B, min, max) 與權重的內積、夾到 0..1 後取 gamma 次方；
/// 以 <c>input × MapPointsN</c> 線性查表（夾到最後一點），增益乘上 R、G、B（可超過 1，不截斷）。
/// </summary>
public sealed class GainTableMap
{
    /// <summary>表格上限（Apple 用 6×8）。</summary>
    internal const int MaxTables = 1 << 12;

    /// <summary>增益總數上限（Apple：6×8×257 = 12336）。</summary>
    internal const int MaxGains = 1 << 22;

    /// <summary>垂直（MapPointsV）與水平（MapPointsH）表格數、每表點數（MapPointsN）。</summary>
    public int PointsV { get; set; }

    public int PointsH { get; set; }

    public int PointsN { get; set; }

    /// <summary>網格間距與原點；以有效區高（_v）／寬（_h）為 1。</summary>
    public double SpacingV { get; set; }

    public double SpacingH { get; set; }

    public double OriginV { get; set; }

    public double OriginH { get; set; }

    /// <summary>(R, G, B, min, max) 的表輸入權重。</summary>
    public float[] Weights { get; set; } = new float[5];

    /// <summary>表輸入的指數（v1 標籤為 1）。</summary>
    public float Gamma { get; set; } = 1f;

    /// <summary>
    /// <c>pointsV × pointsH × pointsN</c> 個增益；列主序、表內點最內層。
    /// 整數儲存已線性映射到 <c>GainMin..=GainMax</c>（線性映射與內插可交換）。
    /// </summary>
    public float[] Gains { get; set; } = [];

    /// <summary>
    /// 由檔案位元組序 <paramref name="order"/> 解析（<paramref name="version2"/> 為
    /// <c>ProfileGainTableMap2</c>）：先依該序讀，失敗時對小端檔回退大端序
    ///（opcode 列表慣例的寫入器）。損毀時回傳 null。
    /// </summary>
    public static GainTableMap? Parse(ReadOnlySpan<byte> data, ByteOrder order, bool version2)
    {
        if (ParseIn(data, order, version2) is { } map) return map;
        return order == ByteOrder.Big ? null : ParseIn(data, ByteOrder.Big, version2);
    }

    private static GainTableMap? ParseIn(ReadOnlySpan<byte> b, ByteOrder order, bool version2)
    {
        if (U32At(b, order, 0) is not { } pv || U32At(b, order, 4) is not { } ph) return null;
        if (F64At(b, order, 8) is not { } spacingV || F64At(b, order, 16) is not { } spacingH
            || F64At(b, order, 24) is not { } originV || F64At(b, order, 32) is not { } originH) return null;
        if (U32At(b, order, 40) is not { } pn) return null;
        var weights = new float[5];
        for (var i = 0; i < 5; i++)
        {
            if (F32At(b, order, 44 + 4 * i) is not { } w) return null;
            weights[i] = w;
        }
        long pointsV = pv, pointsH = ph, pointsN = pn;
        if (pointsV <= 0 || pointsH <= 0 || pointsN <= 0) return null;
        if (pointsV > MaxTables || pointsH > MaxTables || pointsV * pointsH > MaxTables) return null;
        long tables = pointsV * pointsH;
        if (pointsN > MaxGains || tables > MaxGains / Math.Max(1, pointsN)) return null;
        long count = tables * pointsN;
        if (count <= 0 || count > MaxGains) return null;
        static bool SpacingOk(double s, long points) => double.IsFinite(s) && (s > 0.0 || points == 1);
        if (!SpacingOk(spacingV, pointsV) || !SpacingOk(spacingH, pointsH)
            || !double.IsFinite(originV) || !double.IsFinite(originH)
            || weights.Any(w => !float.IsFinite(w)))
            return null;
        int header;
        uint dataType;
        float gamma, gainMin, gainMax;
        if (version2)
        {
            if (U32At(b, order, 64) is not { } dt || F32At(b, order, 68) is not { } gm || F32At(b, order, 72) is not { } lo || F32At(b, order, 76) is not { } hi)
                return null;
            (header, dataType, gamma, gainMin, gainMax) = (80, dt, gm, lo, hi);
        }
        else
        {
            (header, dataType, gamma, gainMin, gainMax) = (64, 3, 1f, 0f, 1f);
        }
        if (!(gamma >= 0.25f && gamma <= 4f)) return null;
        int width = dataType switch { 0 => 1, 1 or 2 => 2, 3 => 4, _ => 0 };
        if (width == 0) return null;
        if (b.Length != count * width + header) return null;
        var data = b.Slice(header);
        var gains = new float[count];
        if (dataType == 0)
        {
            var span = gainMax - gainMin;
            for (var i = 0; i < gains.Length; i++)
                gains[i] = gainMin + data[i] / 255f * span;
        }
        else if (dataType == 1)
        {
            var little = order == ByteOrder.Little;
            var span = gainMax - gainMin;
            for (var i = 0; i < gains.Length; i++)
            {
                var q = little ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2 * i, 2)) : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2 * i, 2));
                gains[i] = gainMin + q / 65535f * span;
            }
        }
        else if (dataType == 2)
        {
            var little = order == ByteOrder.Little;
            for (var i = 0; i < gains.Length; i++)
            {
                var q = little ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2 * i, 2)) : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2 * i, 2));
                gains[i] = Unpack.F16ToF32(q);
            }
        }
        else
        {
            var little = order == ByteOrder.Little;
            for (var i = 0; i < gains.Length; i++)
            {
                var v = little ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4 * i, 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4 * i, 4));
                gains[i] = BitConverter.UInt32BitsToSingle(v);
            }
        }
        if (gains.Any(g => !float.IsFinite(g) || g < 0f)) return null;
        return new GainTableMap
        {
            PointsV = (int)pointsV, PointsH = (int)pointsH, PointsN = (int)pointsN,
            SpacingV = spacingV, SpacingH = spacingH, OriginV = originV, OriginH = originH,
            Weights = weights, Gamma = gamma, Gains = gains,
        };
    }

    private static uint? U32At(ReadOnlySpan<byte> b, ByteOrder order, int o) =>
        o < 0 || o > b.Length - 4 ? null :
            (order == ByteOrder.Little ? BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4)) : BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o, 4)));

    private static float? F32At(ReadOnlySpan<byte> b, ByteOrder order, int o) =>
        U32At(b, order, o) is { } v ? BitConverter.UInt32BitsToSingle(v) : null;

    private static double? F64At(ReadOnlySpan<byte> b, ByteOrder order, int o) =>
        o < 0 || o > b.Length - 8 ? null :
            BitConverter.UInt64BitsToDouble(order == ByteOrder.Little ? BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o, 8)) : BinaryPrimitives.ReadUInt64BigEndian(b.Slice(o, 8)));

    /// <summary>
    /// 以 <paramref name="order"/> 序列化：gamma 為 1 時 v1（DNG 1.6，f32 增益），否則 v2（f32 增益）。
    /// 回傳 (是否 v2, 位元組)。
    /// </summary>
    public (bool Version2, byte[] Bytes) ToBytes(ByteOrder order)
    {
        var version2 = Gamma != 1f;
        var output = new List<byte>(80 + 4 * Gains.Length);
        void U32(uint v)
        {
            if (order == ByteOrder.Little) for (var i = 0; i < 4; i++) output.Add((byte)(v >> (8 * i)));
            else for (var i = 3; i >= 0; i--) output.Add((byte)(v >> (8 * i)));
        }
        void F64(double v)
        {
            var bits = BitConverter.DoubleToUInt64Bits(v);
            if (order == ByteOrder.Little) for (var i = 0; i < 8; i++) output.Add((byte)(bits >> (8 * i)));
            else for (var i = 7; i >= 0; i--) output.Add((byte)(bits >> (8 * i)));
        }
        static uint Dim(int v) => v < 0 ? uint.MaxValue : (uint)v;
        U32(Dim(PointsV));
        U32(Dim(PointsH));
        F64(SpacingV); F64(SpacingH); F64(OriginV); F64(OriginH);
        U32(Dim(PointsN));
        var weights = Weights;
        for (var i = 0; i < 5; i++) U32(BitConverter.SingleToUInt32Bits(i < weights.Length ? weights[i] : 0f));
        if (version2)
        {
            // DataType 3（f32）；GainMin/GainMax 對浮點資料無意義
            U32(3); U32(BitConverter.SingleToUInt32Bits(Gamma)); U32(0); U32(BitConverter.SingleToUInt32Bits(1f));
        }
        foreach (var g in Gains) U32(BitConverter.SingleToUInt32Bits(g));
        return (version2, [.. output]);
    }

    /// <summary>
    /// 有效區相對位置 (<paramref name="x"/>, <paramref name="y"/>)、線性 ProPhoto（基線曝光後）
    /// 色彩 (<paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/>) 的增益；
    /// 無法評估時為 1。大量畫素請先 <see cref="Prepare"/>。
    /// </summary>
    public float Gain(float r, float g, float b, double x, double y) =>
        Prepare() is { } eval ? eval.Gain(r, g, b, x, y) : 1f;

    /// <summary>逐畫素評估的準備；尺寸不一致時為 null。</summary>
    public GainEvaluator? Prepare()
    {
        long n = PointsN;
        // 上游為 usize 飽和乘法；此處以 long 檢查，溢位視為不一致
        long tables;
        try { tables = checked((long)PointsV * PointsH); }
        catch (OverflowException) { return null; }
        if (n <= 0 || tables <= 0) return null;
        long total;
        try { total = checked(tables * n); }
        catch (OverflowException) { return null; }
        if (Gains is null || total != Gains.Length) return null;
        static Axis MakeAxis(long points, double origin, double spacing) => new(
            Last: points - 1,
            Origin: origin,
            Inv: points > 1 && double.IsFinite(spacing) && spacing > 0.0 ? 1.0 / spacing : 0.0);
        return new GainEvaluator(this, MakeAxis(PointsV, OriginV, SpacingV), MakeAxis(PointsH, OriginH, SpacingH));
    }

    /// <summary>單一網格軸的準備：末表索引、原點與反間距（單表為 0）。</summary>
    internal readonly record struct Axis(long Last, double Origin, double Inv)
    {
        /// <summary>相對位置的格子：（首表、次表、次表權重）；網格外複製邊緣表。</summary>
        public (long First, long Second, float SecondWeight) Cell(double pos)
        {
            if (Last <= 0 || Inv == 0.0) return (0, 0, 0f);
            var f = (pos - Origin) * Inv;
            f = double.IsNaN(f) ? 0.0 : Math.Clamp(f, 0.0, (double)Last);
            var i0 = Math.Min((long)f, Last);
            var i1 = Math.Min(i0 + 1, Last);
            return (i0, i1, (float)(f - i0));
        }
    }

    /// <summary>表輸入（線性 ProPhoto，基線曝光後）。</summary>
    internal float Input(float r, float g, float b)
    {
        var w = Weights;
        float w0 = w.Length > 0 ? w[0] : 0f, w1 = w.Length > 1 ? w[1] : 0f, w2 = w.Length > 2 ? w[2] : 0f;
        float w3 = w.Length > 3 ? w[3] : 0f, w4 = w.Length > 4 ? w[4] : 0f;
        var lo = MathF.Min(r, MathF.Min(g, b));
        var hi = MathF.Max(r, MathF.Max(g, b));
        var x = w0 * r + w1 * g + w2 * b + w3 * lo + w4 * hi;
        x = float.IsNaN(x) ? 0f : Math.Clamp(x, 0f, 1f);
        return Gamma == 1f ? x : MathF.Pow(x, Gamma);
    }
}

/// <summary>逐畫素評估的增益表（lightcraft <c>GainEval</c>）；欄位公開可手造，查表一律檢查，永不拋出。</summary>
public readonly struct GainEvaluator
{
    private readonly GainTableMap _map;
    private readonly GainTableMap.Axis _v;
    private readonly GainTableMap.Axis _h;

    internal GainEvaluator(GainTableMap map, GainTableMap.Axis v, GainTableMap.Axis h) => (_map, _v, _h) = (map, v, h);

    /// <summary>是否有可評估的表格（尺寸一致）。</summary>
    public bool HasValue
    {
        get
        {
            long n = _map.PointsN;
            long tables;
            try { tables = checked((long)_map.PointsV * _map.PointsH); }
            catch (OverflowException) { return false; }
            if (n <= 0 || tables <= 0) return false;
            long total;
            try { total = checked(tables * n); }
            catch (OverflowException) { return false; }
            return total == _map.Gains.Length;
        }
    }

    /// <summary><see cref="GainTableMap.Gain"/> 的逐畫素版。</summary>
    public float Gain(float r, float g, float b, double x, double y)
    {
        var m = _map;
        if (m is null || m.Gains is null) return 1f;
        long n = m.PointsN;
        if (n <= 0) return 1f;
        long last = n - 1;
        var (v0, v1, fv) = _v.Cell(y);
        var (h0, h1, fh) = _h.Cell(x);
        // 四表共用一個小數索引（≥ 0，C# 轉整數向零截斷等同 floor）
        var idx = MathF.Min(m.Input(r, g, b) * n, last);
        if (!float.IsFinite(idx)) return 1f;
        var i0 = Math.Min((long)idx, last);
        var i1 = Math.Min(i0 + 1, last);
        var t = idx - i0;
        float At(long v, long h)
        {
            long tableStart;
            try { tableStart = checked((v * m.PointsH + h) * n); }
            catch (OverflowException) { return 1f; }
            var gains = m.Gains;
            float g0 = (uint)(tableStart + i0) < (uint)gains.Length ? gains[tableStart + i0] : 1f;
            float g1 = (uint)(tableStart + i1) < (uint)gains.Length ? gains[tableStart + i1] : 1f;
            return g0 + (g1 - g0) * t;
        }
        var a = At(v0, h0); var bb = At(v0, h1); var c = At(v1, h0); var d = At(v1, h1);
        var top = a + (bb - a) * fh;
        var bottom = c + (d - c) * fh;
        var gain = top + (bottom - top) * fv;
        return float.IsFinite(gain) ? MathF.Max(gain, 0f) : 1f;
    }
}

/// <summary>
/// 顯影後來源在 raw 有效區的位置（lightcraft <c>SourcePlacement</c>）：
/// 來源是有效區的預設裁切，再經 EXIF 方向。與解析度無關。
/// </summary>
public readonly record struct SourcePlacement(double X, double Y, double Width, double Height, Orientation Orientation)
{
    /// <summary>定向後來源正規化 (0..1) 座標的相對有效區位置。</summary>
    public (double X, double Y) Active(double u, double v)
    {
        var (mu, mv) = Orientation.Inverse().Map(u, v, 1.0, 1.0);
        return (X + mu * Width, Y + mv * Height);
    }
}

/// <summary>EXIF 方向的正規化座標映射（lightcraft-geom <c>Orientation::map</c>）。</summary>
internal static class OrientationMap
{
    public static (double X, double Y) Map(this Orientation o, double x, double y, double w, double h)
    {
        var (flip, turns) = o.ToParts();
        var (w2, h2) = (w, h);
        if (flip) x = w2 - x;
        for (var i = 0; i < turns; i++)
        {
            // 順時針 90°：(x, y) → (h − y, x)
            var nx = h2 - y;
            y = x;
            x = nx;
            (w2, h2) = (h2, w2);
        }
        return (x, y);
    }
}
