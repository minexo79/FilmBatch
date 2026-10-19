// 移植自 LightCraft crates/raw/src/vendor/nef_lens.rs（Nikon 機內畸變校正：raw SubIFD 0xc7d5 NikonNEFInfo → DNG warp）。
// 標籤身份：ExifTool Nikon 文件（NikonNEFInfo 0xc7d5；其 DistortionInfo 0x0005 與 DistortionCorrection、
// RadialDistortionCoefficient1..3）。模型（何存值乘何次方、正規化、fill-frame zoom、0x0008 光學中心偏移）
// 由 raw 對自家校正 JPEG 獨立推得；見 docs/nikon-lens-corrections.md。無鏡頭資料庫、無外部解碼器程式碼。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Nikon;

/// <summary>
/// Nikon 機內畸變校正（上游 <c>nef_lens::distortion(model, raw, active, crop)</c>）：
/// <c>r_src = r_out·(1 + k3ρ² + k2ρ⁴ + k1ρ⁶)/(1 + zoom)</c>，繞光學中心，ρ 為影像區半對角線上的半徑
/// （k1..k3 依 ExifTool 命名、按儲存順序）。sensor-shift 鏡頭的光學中心跟著位移。
/// 未驗證機身、其他影像區、缺值／不合理值、折疊映照皆回傳 null。
/// </summary>
internal static class NikonLens
{
    private const ushort NefInfo = 0xc7d5;
    private const ushort DistortionInfo = 0x0005;
    private const ushort CentreOffset = 0x0008;

    /// <summary>以自家 JPEG 校驗過的機身（docs/nikon-lens-corrections.md）。他身同標籤不代表同像素間距／影像區；
    /// 加機身前須先校驗其檔案。精確比對（ILCE 式前綴比對禁用）。</summary>
    internal static readonly string[] Validated = ["NIKON Z 6_2", "NIKON Z 8", "NIKON Z f"];

    /// <summary>校驗過的檔案所存正規化半徑：FX 半對角線（mm）。</summary>
    private const double FxHalfDiagonalMm = 21.6;

    private const int Samples = 256;

    private readonly record struct Distortion(double K0, double K1, double K2, double Zoom);

    private static double? Rational(byte[] b, int at, ByteOrder order)
    {
        if (at < 0 || at + 8 > b.Length) return null;
        var n = unchecked((int)order.ToU32(b.AsSpan(at, 4)));
        var d = unchecked((int)order.ToU32(b.AsSpan(at + 4, 4)));
        if (d <= 0) return null;
        var v = (double)n / d;
        return double.IsFinite(v) ? v : null;
    }

    /// <summary>NikonNEFInfo 條目：blob 為 "Nikon\0"＋4 位元組版本＋小 TIFF 流。</summary>
    private static (Ifd Ifd, ByteOrder Order)? NefInfoEntries(Ifd raw)
    {
        var blob = raw.Bytes(NefInfo);
        if (blob is null || blob.Length < 10) return null;
        if (blob[0] != (byte)'N' || blob[1] != (byte)'i' || blob[2] != (byte)'k'
            || blob[3] != (byte)'o' || blob[4] != (byte)'n' || blob[5] != 0)
            return null;
        var tiff = new ReadOnlyMemory<byte>(blob, 10, blob.Length - 10);
        if (TiffFile.Sniff(tiff) is not { } sniff) return null;
        var (order, big) = sniff;
        if (big) return null;
        if (order.ReadU32(tiff.Span, 4) is not { } first) return null;
        try
        {
            var (ifd, _) = TiffFile.ParseIfdAt(tiff, first, order, 0, false,
                new ParseOptions { FollowChildren = false });
            return (ifd, order);
        }
        catch (RawException) { return null; }
    }

    /// <summary>
    /// DistortionInfo："0100"、校正模式（0 = 無）、正規化半徑（mm，位元組 8）、項數（4，位元組 16）、
    /// 係數（位元組 20／28／36）與 zoom（位元組 64），皆 signed rational。
    /// </summary>
    private static Distortion? DistortionInfoOf(byte[] b, ByteOrder order)
    {
        if (b.Length < 72 || b[0] != (byte)'0' || b[1] != (byte)'1' || b[2] != (byte)'0' || b[3] != (byte)'0')
            return null;
        if (b[4] == 0) return null;
        if (Rational(b, 8, order) is not { } norm) return null;
        if (order.ToU32(b.AsSpan(16, 4)) != 4u) return null;
        if (Math.Abs(norm - FxHalfDiagonalMm) > 0.05) return null;
        if (Rational(b, 20, order) is not { } k0) return null;
        if (Rational(b, 28, order) is not { } k1) return null;
        if (Rational(b, 36, order) is not { } k2) return null;
        if (Rational(b, 64, order) is not { } zoom) return null;
        // 校驗過的檔案：|k| < 0.13、|zoom| < 0.09；此處留寬（< 1.0／< 0.5）。
        if (!(Math.Abs(k0) < 1.0 && Math.Abs(k1) < 1.0 && Math.Abs(k2) < 1.0 && Math.Abs(zoom) < 0.5))
            return null;
        return new Distortion(k0, k1, k2, zoom);
    }

    /// <summary>條目 0x0008："0100"＋光學中心相對影像區中心的像素偏移（x 右、y 下），兩個 signed rational。
    /// 鏡頭防震時為零（sensor 置中）。</summary>
    private static (double X, double Y)? CentreOffsetOf(byte[] b, ByteOrder order)
    {
        if (b.Length < 20 || b[0] != (byte)'0' || b[1] != (byte)'1' || b[2] != (byte)'0' || b[3] != (byte)'0')
            return null;
        if (Rational(b, 4, order) is not { } x) return null;
        if (Rational(b, 12, order) is not { } y) return null;
        return (x, y);
    }

    /// <summary>
    /// 相機 <paramref name="model"/> 的 NEF、raw IFD 為 <paramref name="raw"/>、影像區 <paramref name="crop"/>
    ///（sensor 像素，位於 <paramref name="active"/> 內）的校正，active-area 座標 OpcodeList3 warp。
    /// </summary>
    internal static Opcode? DistortionOf(string model, Ifd raw, PixelRect active, PixelRect crop)
    {
        if (!Validated.Contains(model.Trim(), StringComparer.Ordinal)) return null;
        crop = crop.Clipped(active.Width, active.Height);
        if (active.Width < 2 || active.Height < 2 || crop.Width < 2 || crop.Height < 2) return null;
        // 存的半徑是 FX 半對角線：只校正完整 3:2 影像區（非 sensor 上的 DX 等裁切）。
        if (Math.Abs((double)crop.Width / crop.Height - 1.5) > 0.005 || crop.Width < active.Width * 0.95)
            return null;
        if (NefInfoEntries(raw) is not { } info) return null;
        var (entries, order) = info;
        if (entries.Bytes(DistortionInfo) is not { } dib) return null;
        if (DistortionInfoOf(dib, order) is not { } d) return null;
        var (ox, oy) = entries.Bytes(CentreOffset) is { } cob && CentreOffsetOf(cob, order) is { } off
            ? off : (0.0, 0.0);
        var (cw, ch) = (crop.Width - 1.0, crop.Height - 1.0);
        if (Math.Abs(ox) > cw * 0.05 || Math.Abs(oy) > ch * 0.05) return null;
        var halfDiag = LensFit.Hypot(cw, ch) / 2.0;
        double Poly(double rho)
        {
            var x = rho * rho;
            return 1.0 + x * (d.K2 + x * (d.K1 + x * d.K0));
        }
        // DNG 以最遠 active-area 角正規化；影像區至多到達該處。
        var (ax, ay) = (active.Width - 1.0, active.Height - 1.0);
        var (cx, cy) = (crop.X + cw / 2.0 + ox, crop.Y + ch / 2.0 + oy);
        var farthest = LensFit.Hypot(Math.Max(cx, ax - cx), Math.Max(cy, ay - cy));
        var a = farthest / halfDiag;
        // 來源半徑必須隨輸出半徑長到最遠角，否則映照折疊。
        var prev = -1.0;
        for (var j = 0; j <= Samples; j++)
        {
            var rho = a * j / Samples;
            var source = rho * Poly(rho) / (1.0 + d.Zoom);
            if (!double.IsFinite(source) || source <= prev) return null;
            prev = source;
        }
        var s = 1.0 / (1.0 + d.Zoom);
        var a2 = a * a;
        var planes = new[] { new[] { s, d.K2 * a2 * s, d.K1 * a2 * a2 * s, d.K0 * a2 * a2 * a2 * s, 0.0, 0.0 } };
        return new WarpRectilinear(planes, [cx / ax, cy / ay]);
    }

    /// <summary>NEF 的鏡頭校正（上游 nef.rs 語意：list3 一個 warp；無／拒絕時空清單）。</summary>
    internal static List<Opcode> Corrections(string model, Ifd raw, PixelRect active, PixelRect crop)
        => DistortionOf(model, raw, active, crop) is { } op ? [op] : [];
}
