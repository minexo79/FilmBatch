// 移植自 LightCraft crates/codecs/src/icc.rs（parse／info_of：matrix-shaper 判別、D50 色料矩陣、
// TRC 提取、gray kTRC；LUT／CMYK 須完整 CMS）與 crates/codecs/src/space.rs（NamedSpace::recognize、
// rgb_to_xyz_d50、SourceSpace::to_space／to_working）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>ICC 解析結果（上游 <c>icc::IccInfo</c> 的預覽解碼子集）。</summary>
internal sealed record PreviewIccInfo(
    PreviewIccModel Model,
    PreviewIccKind Kind,
    Mat3 ToXyzD50,
    PreviewTrc[] Trc,
    RgbSpace? Named);

/// <summary>裝置色彩模型（上游 <c>IccColorModel</c>）。</summary>
internal enum PreviewIccModel { Rgb, Gray, Other }

/// <summary>套用方式（上游 <c>IccKind</c> 的預覽解碼子集：矩陣／TRC 整形器、灰階 kTRC、其餘需 CMS）。</summary>
internal enum PreviewIccKind { MatrixTrc, Gray, NeedsCms }

/// <summary>
/// 嵌入預覽 ICC 解析（上游 <c>icc::parse</c>／<c>info_of</c> `icc.rs:41-69`）：
/// RGB + PCS XYZ + 非零色料 + 三條 TRC 為 matrix-shaper（行列式 |det| &gt; 1e-6 且 TRC 有效），
/// 其色料矩陣即線性 RGB → XYZ D50（PCS 白 Y = 1）；灰階 + kTRC 且無 A2B LUT 為 gray；
/// 其餘（LUT、CMYK、Lab 等）為 <c>NeedsCms</c>——FilmBatch 無 CMS，
/// 呼叫端按上游 `rgb_or_gray` 的「存在但不可用 → sRGB」分支處理（見下方）。
/// 具名空間辨識容限 0.004（上游 `NamedSpace::recognize` `space.rs:210`）。
/// </summary>
internal static class PreviewIcc
{
    private const float RecognizeTolerance = 0.004f;

    internal static PreviewIccInfo? Parse(byte[] profile)
    {
        try
        {
            if (profile.Length < 128)
                return null;
            if (ReadU32(profile, 0) != (uint)profile.Length)
                return null;
            var colorSpace = ReadTag(profile, 16);
            var pcs = ReadTag(profile, 20);
            var tagCount = (int)ReadU32(profile, 128);
            if (tagCount < 0 || tagCount > 256 || 132 + tagCount * 12 > profile.Length)
                return null;
            var tags = new Dictionary<string, (uint Off, uint Len)>(tagCount);
            for (var i = 0; i < tagCount; i++)
            {
                var at = 132 + i * 12;
                var sig = ReadTag(profile, at);
                var off = ReadU32(profile, at + 4);
                var len = ReadU32(profile, at + 8);
                if ((ulong)off + len > (ulong)profile.Length)
                    return null;
                tags[sig] = (off, len);
            }
            var hasLut = tags.ContainsKey("A2B0") || tags.ContainsKey("A2B1");
            if (colorSpace == "RGB " && pcs == "XYZ ")
            {
                if (!tags.TryGetValue("rXYZ", out var r) || !tags.TryGetValue("gXYZ", out var g) ||
                    !tags.TryGetValue("bXYZ", out var b) ||
                    !tags.TryGetValue("rTRC", out var rt) || !tags.TryGetValue("gTRC", out var gt) ||
                    !tags.TryGetValue("bTRC", out var bt))
                    return new PreviewIccInfo(PreviewIccModel.Rgb, PreviewIccKind.NeedsCms, Mat3.Identity, [], null);
                if (!TryColorant(profile, r, out var rc) || !TryColorant(profile, g, out var gc) ||
                    !TryColorant(profile, b, out var bc))
                    return new PreviewIccInfo(PreviewIccModel.Rgb, PreviewIccKind.NeedsCms, Mat3.Identity, [], null);
                var m = new Mat3(rc.X, gc.X, bc.X, rc.Y, gc.Y, bc.Y, rc.Z, gc.Z, bc.Z);
                var trcs = new[] { TrcOf(profile, rt), TrcOf(profile, gt), TrcOf(profile, bt) };
                if (Math.Abs(m.Determinant()) <= 1e-6 || trcs.Any(t => t is null || !ValidTrc(t)))
                    return new PreviewIccInfo(PreviewIccModel.Rgb, PreviewIccKind.NeedsCms, Mat3.Identity, [], null);
                var unwrapped = trcs.Select(t => t!).ToArray();
                return new PreviewIccInfo(PreviewIccModel.Rgb, PreviewIccKind.MatrixTrc, m, unwrapped, Recognize(m));
            }
            if (colorSpace == "GRAY" && tags.TryGetValue("kTRC", out var k) && !hasLut)
            {
                var trc = TrcOf(profile, k);
                if (trc is null || !ValidTrc(trc))
                    return new PreviewIccInfo(PreviewIccModel.Gray, PreviewIccKind.NeedsCms, Mat3.Identity, [], null);
                return new PreviewIccInfo(PreviewIccModel.Gray, PreviewIccKind.Gray, Mat3.Identity, [trc, trc, trc], null);
            }
            var model = colorSpace == "GRAY" ? PreviewIccModel.Gray : PreviewIccModel.Other;
            return new PreviewIccInfo(model, PreviewIccKind.NeedsCms, Mat3.Identity, [], null);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// 具名空間辨識（上游 `NamedSpace::recognize` `space.rs:210`）：`RgbToXyzD50`
    /// 與候選矩陣每個元素差皆 &lt; 0.004。 FilmBatch 的 <c>RgbSpace</c> 常數與上游逐值一致
    ///（sRGB／Display P3／Rec.2020／ProPhoto／Adobe RGB，見 ColorMath.cs）。
    /// </summary>
    internal static RgbSpace? Recognize(Mat3 toXyzD50)
    {
        RgbSpace[] all = [ColorSpaces.Srgb, ColorSpaces.DisplayP3, ColorSpaces.Rec2020, ColorSpaces.ProPhoto, ColorSpaces.AdobeRgb];
        foreach (var s in all)
        {
            var m = RgbToXyzD50(s);
            var ok = true;
            for (var r = 0; r < 3 && ok; r++)
                for (var c = 0; c < 3 && ok; c++)
                    if (Math.Abs(m[r, c] - toXyzD50[r, c]) >= RecognizeTolerance)
                        ok = false;
            if (ok)
                return s;
        }
        return null;
    }

    private static bool ValidTrc(PreviewTrc t) =>
        // 上游 `valid_trc`：Gamma 有限且 > 0；參數式僅 1／3／4／5／7 個有限值；其餘恆有效。
        t.GammaValue is { } g
            ? float.IsFinite(g) && g > 0f
            : t.ParametricParameters is not { } p
                ? true
                : p.Length switch
                {
                    1 or 3 or 4 or 5 or 7 => p.All(float.IsFinite),
                    _ => false,
                };

    private static PreviewTrc? TrcOf(byte[] profile, (uint Off, uint Len) tag)
    {
        var (off, len) = tag;
        if (len < 8 || off > (uint)profile.Length - 8)
            return null;
        var type_ = ReadTag(profile, (int)off);
        if (type_ == "curv")
        {
            var n = ReadU32(profile, (int)off + 8);
            if (n > 65536 || 12 + n * 2 > len)
                return null;
            if (n == 0)
                return PreviewTrc.Linear;
            var start = (int)off + 12;
            if (n == 1)
                return PreviewTrc.Gamma(ReadU16(profile, start) / 256f);
            var table = new ushort[n];
            for (var i = 0; i < n; i++)
                table[i] = ReadU16(profile, start + (int)i * 2);
            return PreviewTrc.Table(table);
        }
        if (type_ == "para")
        {
            var func = ReadU16(profile, (int)off + 8);
            int[] counts = [1, 3, 4, 5, 7];
            if (func > 4)
                return null;
            var n = counts[func];
            if (12 + n * 4 > len)
                return null;
            var p = new float[n];
            for (var i = 0; i < n; i++)
                p[i] = S15Fixed16(profile, (int)off + 12 + i * 4);
            // 有效性（gamma > 0、參數個數／有限值）由 ValidTrc 按上游 valid_trc 判定。
            return PreviewTrc.Parametric(p);
        }
        return null;
    }

    private static bool TryColorant(byte[] profile, (uint Off, uint Len) tag, out (double X, double Y, double Z) colorant)
    {
        colorant = (0, 0, 0);
        var (off, len) = tag;
        if (len < 8 || off > (uint)profile.Length - 8 || ReadTag(profile, (int)off) != "XYZ ")
            return false;
        colorant = (S15Fixed16(profile, (int)off + 8), S15Fixed16(profile, (int)off + 12), S15Fixed16(profile, (int)off + 16));
        return colorant != (0.0, 0.0, 0.0);
    }

    private static float S15Fixed16(byte[] b, int at)
    {
        var raw = ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];
        return (int)raw / 65536f;
    }

    internal static Mat3 RgbToXyzD50(in RgbSpace space) =>
        ColorSpaces.Bradford(space.White, ColorSpaces.D50).Mul(space.ToXyz());

    /// <summary>
    /// 線性來源 RGB → 線性目標 RGB（上游 `SourceSpace::to_space` `space.rs:283-293`）：
    /// 具名來源走 `to_space`；D50 色料矩陣來自 ICC 時改走
    /// `dst.from_xyz * bradford(D50, dst.white) * to_xyz_d50`（FilmBatch 白點皆 D65 者兩者一致）。
    /// </summary>
    internal static Mat3 ToSpace(Mat3 toXyzD50, RgbSpace? named, in RgbSpace dst) =>
        named is { } s
            ? s.ToSpace(dst)
            : dst.FromXyz().Mul(ColorSpaces.Bradford(ColorSpaces.D50, dst.White)).Mul(toXyzD50);

    /// <summary>線性來源 RGB → 線性 Rec.2020 D65（上游 `SourceSpace::to_working`）。</summary>
    internal static Mat3 ToWorking(Mat3 toXyzD50, RgbSpace? named) =>
        ToSpace(toXyzD50, named, ColorSpaces.Working);

    private static ushort ReadU16(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);

    private static uint ReadU32(byte[] b, int at) =>
        ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

    private static string ReadTag(byte[] b, int at) =>
        new string([(char)b[at], (char)b[at + 1], (char)b[at + 2], (char)b[at + 3]]);
}
