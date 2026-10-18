// 移植自 LightCraft crates/raw/src/vendor/arw_lens_rm6.rs（ILCE-7RM6 的 raw IFD 表格 → 等價 DNG OpcodeList3）。
// ILCE-7RM4A 版（arw_lens）以 Sony Imaging Edge 匯出量測（單位 16384、表格按輸出半徑索引）；
// 此處為 ILCE-7RM6 版，以自家 JPEG 量測（見兩份文件：docs/sony-ilce7rm6-lens-corrections.md → Two conventions、
// docs/sony-lens-corrections.md）。各由機身選用；無一單位適用每顆鏡頭。
//
// Sony 在 raw IFD 存三個 count-prefixed signed 16-bit 表格，各有機內開關（ExifTool Sony 標籤名）：
// 畸變 DISTORTION_PARAMS（旗標 DISTORTION_FLAG）、暗角 VIGNETTING_PARAMS（VIGNETTING_FLAG）、
// 橫向色差 CA_PARAMS（CA_FLAG）。首值為 knot 數：ILCE-7RM6 全幅 16／16／32，APS-C 裁切 11／11／22，其餘 padding；
// CA 表存紅色 knots 再藍色。
//
// 量測約定（54 張 ILCE-7RM6、4 顆鏡頭，41 張機內畸變校正開啟；clean room）：
// - knot i 在預設裁切中心起 sensor 半徑 i·s，s = 全幅預設裁切半對角線／15（APS-C 同像素間距：knot 釘在 sensor 上）；
//   knot 間線性，末段向外延伸。
// - 畸變：校正半徑 r（px）讀來源半徑 S(r) = r·(1 + v(r)／15000)（正為枕形），相機再 zoom（輸出半徑 = z·校正半徑）
//   使輸出填滿預設裁切且只讀其內：裁切邊界半徑上 S(ρ/z)／ρ 最大者為 1。
// - WarpRectilinear 4 項奇多項式在每張照片 65 個擬合樣本上擬合該映照至 DNG 半徑 2.35·10⁻⁴（0.220 px）；
//   裁切可見的擬合樣本上差過 1440 px 長邊 0.25 px 的表格拒絕（不扭曲顯示）。
// - 暗角：讀取但永不套用。自家 JPEG 保留未校正渲染的徑向衰減（39 張角落／中心比中位數 0.996），無徑向增益模型合表。
// - 橫向 CA：紅（藍）來源半徑 g·(1 + c(g)／3.36·10⁶)，g 為綠來源半徑，c 為同 knot 的紅（藍）半。單位為暫定：
//   量測臨界（最差 0.399 px，預算 0.3 px；每張最佳單位散在 1.5·10⁶..7.2·10⁶，約 ±30% 場景偏差），
//   但 46 張 raw-CFA CA rms 由 0.079 → 0.052 px。表格解析、warp_opcode 轉 warp 的 R／B 平面，但預設不發射
//   （lens_opcodes 傳 lateralCa: false）：去馬賽克後套用使渲染 CA 更差。CA 值超 CA_MAX_RELATIVE 或色版擬合
//   失敗時兩色版同落，只 G 用於三通道。
//
// 校正不看機內旗標。只給 FULL_FRAME_CROP 所列機身：單位與 knot 間距僅在 ILCE-7RM6 驗證。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Sony;

/// <summary>ILCE-7RM6 的鏡頭表格（上游 <c>arw_lens_rm6</c>）。暗角與旗標只解析供測試／文件：不據以建 opcode
///（相機不對 JPEG 施暗角校正，旗標不決定任何事）。</summary>
internal sealed class Rm6LensTables
{
    public double[]? Distortion { get; set; }
    public double[]? Vignetting { get; set; }
    public (double[] Red, double[] Blue)? Ca { get; set; }

    /// <summary>機內畸變、暗角、CA 校正開關記錄值。</summary>
    public long?[] Flags { get; } = new long?[3];
}

internal static class SonyRm6Lens
{
    internal const ushort DistortionParams = 0x7037;
    internal const ushort VignettingParams = 0x7032;
    internal const ushort CaParams = 0x7035;
    internal const ushort DistortionFlag = 0x7036;
    internal const ushort VignettingFlag = 0x7031;
    internal const ushort CaFlag = 0x7034;

    /// <summary>表格驗證過的機身（Exif Model）與其全幅預設裁切（寬，高，px）。</summary>
    private static readonly (string Model, double W, double H)[] FullFrameCrop = [("ILCE-7RM6", 9984.0, 6656.0)];

    /// <summary>畸變表的值 1（相對半徑）；34 張全域最小平方擬合 15000.7。</summary>
    private const double DistortionUnit = 15000.0;

    /// <summary>CA 表的值 1（相對綠來源半徑）。暫定：20 張乾淨照片最佳單位中位數。</summary>
    private const double CaUnit = 3.36e6;

    /// <summary>可信的最大 CA 相對值（|c|／CA_UNIT）：全幅角約 12 px；54 張量測表最大 1724。</summary>
    private const double CaMaxRelative = 2e-3;

    /// <summary>填框 zoom 掃描的裁切邊界半徑數。</summary>
    private const int ZoomScan = 4096;

    /// <summary>裁切可見的擬合樣本上可接受的最大多項式擬合誤差（1440 px 長邊的 px）。</summary>
    private const double FitErrorPxAt1440 = 0.25;

    /// <summary>count-prefixed 整數表的取值：count 在範圍內且容得下。</summary>
    private static double[]? Table(Ifd raw, ushort tag, int min, int max)
    {
        var v = raw.Value(tag);
        if (v is null) return null;
        if (v.GetI64(0) is not { } first || first < min || first > max) return null;
        var count = (int)first;
        if (count >= v.Count) return null;
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (v.GetI64(i + 1) is not { } x) return null;
            values[i] = x;
        }
        return values;
    }

    /// <summary>raw IFD 的鏡頭表格。</summary>
    internal static Rm6LensTables ReadTables(Ifd raw)
    {
        var t = new Rm6LensTables
        {
            Distortion = Table(raw, DistortionParams, 2, 16),
            Vignetting = Table(raw, VignettingParams, 2, 16),
        };
        if (Table(raw, CaParams, 4, 32) is { } ca && ca.Length % 2 == 0)
            t.Ca = (ca[..(ca.Length / 2)], ca[(ca.Length / 2)..]);
        t.Flags[0] = raw.Value(DistortionFlag)?.GetI64(0);
        t.Flags[1] = raw.Value(VignettingFlag)?.GetI64(0);
        t.Flags[2] = raw.Value(CaFlag)?.GetI64(0);
        return t;
    }

    /// <summary>驗證過機身的 knot 間距（px）：全幅預設裁切半對角線／15。</summary>
    private static double? KnotSpacing(string model)
    {
        var trimmed = model.Trim();
        foreach (var (m, w, h) in FullFrameCrop)
            if (m == trimmed) return LensFit.Hypot(w, h) / 30.0;
        return null;
    }

    /// <summary>
    /// 半徑 r（px）處的 knot 表值：knot i 在 i·spacing，間線性，末段向外延伸。
    /// knot 不足兩點時 NaN。
    /// </summary>
    private static double Interpolate(double[] knots, double spacing, double r)
    {
        if (knots.Length < 2 || !double.IsFinite(spacing) || spacing <= 0) return double.NaN;
        var t = r / spacing;
        int i = t > 0.0 ? Math.Min((int)t, knots.Length - 2) : 0;
        if (i < 0 || i + 1 >= knots.Length) return double.NaN;
        return knots[i] + (knots[i + 1] - knots[i]) * (t - i);
    }

    /// <summary>
    /// 來源半徑映照 <paramref name="source"/>、半尺寸 x×y 裁切的相機填框 zoom z：
    /// 邊界半徑 ρ ∈ [min(x,y), hypot(x,y)] 上 source(ρ/z)／ρ 最大者為 1，使輸出裁切只讀來源裁切內並相切。
    /// [0.5, 2] 內二分；無解或比值非有限時回傳 null。
    /// </summary>
    private static double? FillZoom(Func<double, double> source, double x, double y)
    {
        var (near, far) = (Math.Min(x, y), LensFit.Hypot(x, y));
        bool? Overshoots(double z)
        {
            var worst = double.NegativeInfinity;
            for (var k = 0; k <= ZoomScan; k++)
            {
                var rb = near + k * (far - near) / ZoomScan;
                var ratio = source(rb / z) / rb;
                if (!double.IsFinite(ratio)) return null;
                worst = Math.Max(worst, ratio);
            }
            return worst > 1.0;
        }
        var (lo, hi) = (0.5, 2.0);
        if (Overshoots(lo) is not false || Overshoots(hi) is not true) return null;
        for (var i = 0; i < 100; i++)
        {
            var z = 0.5 * (lo + hi);
            if (Overshoots(z) is true) lo = z; else hi = z;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// 畸變＋CA 表為 active-area 相對 WarpRectilinear（對中預設裁切 <paramref name="crop"/>，如相機校正），
    /// knot 間距 <paramref name="spacing"/> px：lateralCa 時平面 R,G,B，否則 G 單平面（用於三通道），
    /// CA 表缺席／不合理或色版擬合失敗時亦同。畸變表缺席或全零時回傳 null（CA 單獨不套用：相對綠）。
    /// zoom 或 G 擬合失敗亦回傳 null。
    /// </summary>
    internal static Opcode? WarpOpcode(Rm6LensTables t, double spacing, PixelRect active, PixelRect crop, bool lateralCa)
    {
        if (t.Distortion is not { } v || !v.Any(x => x != 0.0)) return null;
        var (aw, ah) = ((double)active.Width, (double)active.Height);
        if (aw < 2.0 || ah < 2.0 || crop.Width == 0 || crop.Height == 0) return null;
        if (!double.IsFinite(spacing) || spacing <= 0) return null;
        var (cw, ch) = ((double)crop.Width, (double)crop.Height);
        double Source(double r) => r * (1.0 + Interpolate(v, spacing, r) / DistortionUnit);
        if (FillZoom(Source, cw / 2.0, ch / 2.0) is not { } z) return null;
        // DNG 正規化：像素索引單位，半徑 1 = 最遠 active-area 角。
        var (cx, cy) = (crop.X + (cw - 1.0) / 2.0, crop.Y + (ch - 1.0) / 2.0);
        var m = Math.Max(OpcodeOps.MaxRadius(cx, cy, aw - 1.0, ah - 1.0), 1e-9);
        var maxFitError = FitErrorPxAt1440 * (Math.Max(cw, ch) / 1440.0) / m;
        var shown = LensFit.Hypot(cw / 2.0, ch / 2.0) / m;
        // 輸出半徑（px）→ 綠來源半徑。
        Func<double, double> Green = r => Source(r / z);
        double[]? Plane(Func<double, double> map)
        {
            var k = LensFit.FitRadialWarp(u => map(u * m) / m, maxFitError, shown);
            if (k is null) return null;
            return [k[0], k[1], k[2], k[3], 0.0, 0.0];
        }
        if (Plane(Green) is not { } g) return null;
        // 紅藍：相對綠來源半徑，在該半徑上取值。
        double[]? Colour(double[] c) => Plane(r =>
        {
            var s = Green(r);
            return s * (1.0 + Interpolate(c, spacing, s) / CaUnit);
        });
        static bool Plausible(double[] c) => c.All(x => double.IsFinite(x) && Math.Abs(x) <= CaMaxRelative * CaUnit);
        List<double[]>? rgb = null;
        if (lateralCa && t.Ca is { } ca && Plausible(ca.Red) && Plausible(ca.Blue))
        {
            if (Colour(ca.Red) is { } red && Colour(ca.Blue) is { } blue)
                rgb = [red, g, blue];
            else
                return null;
        }
        var planes = rgb ?? [g];
        return new WarpRectilinear([.. planes], [cx / (aw - 1.0), cy / (ah - 1.0)]);
    }

    /// <summary>
    /// Sony raw IFD 對機身 <paramref name="model"/>（Exif Model）的 OpcodeList3 鏡頭校正；
    /// 表格未驗證的機身為空。
    ///
    /// 畸變限定：橫向 CA 平面關閉（lateralCa: false）。去馬賽克已藏約七成鏡頭 CA
    /// （渲染顯示 raw-CFA 量測 CA 的 0.29×），而去馬賽克後 warp 按表格 0.84× 搬 R／B；
    /// 43 張驗證照片角落 R/G、B/G CA rms 由 0.032／max 0.125 px（關）→ 0.052／0.219 px（開），
    /// 僅 16–35 mm 改善。後續：去馬賽克前 warp CFA 色版再開啟此處平面。
    /// </summary>
    internal static List<Opcode> LensOpcodes(Ifd raw, string model, PixelRect active, PixelRect crop)
    {
        if (KnotSpacing(model) is not { } spacing) return [];
        return WarpOpcode(ReadTables(raw), spacing, active, crop, false) is { } op ? [op] : [];
    }
}
