using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FilmBatch.Core.Film;

namespace FilmBatch.App.Controls;

/// <summary>
/// 具有色彩語意的滑桿軌道漸層（色相、各色強度、色相帶等）。
/// 色溫／色偏是固定的冷暖、綠洋紅軸，仍由 ColorSliderStyles.axaml 的 class 提供；
/// 這裡處理需依參數計算的漸層，回傳 null 表示使用一般軌道。顏色只是示意，與主題無關。
/// </summary>
public static class ParamTrackBrushes
{
    /// <summary>八個色相帶的示意色（各色相帶中心附近），色塊與色相帶滑桿共用。</summary>
    public static IReadOnlyList<Color> BandColors { get; } =
        [.. new[] { "#E04848", "#F08A3C", "#E8CF3A", "#4CB050", "#3EC1C9", "#3F6FE0", "#8A55D6", "#D64FA8" }.Select(Color.Parse)];

    /// <summary>未上色的起點（與一般軌道同色），讓「0 = 無此顏色」一眼可辨。</summary>
    private static readonly Color Off = Color.Parse("#4A4A4A");

    private static readonly IBrush HueWheel = HueRange(0, 360, 0.6, 0.9);
    private static readonly IBrush Saturation = Gradient(
        (0, Color.Parse("#7A7A7A")), (0.5, Color.Parse("#8A8174")),
        (0.7, Color.Parse("#E05050")), (0.85, Color.Parse("#E8C83A")), (1, Color.Parse("#4C9FE0")));

    private static readonly Dictionary<string, IBrush> ByFlatKey = new(StringComparer.Ordinal)
    {
        ["basic.saturation"] = Saturation,
        ["raw.mask_saturation"] = Saturation,
        // 光暈色相 0–60°：shader 以 hsv(hue, 0.75, 1) 上色，紅 → 黃
        ["halation.hue"] = HueRange(0, 60, 0.75, 0.9),
        // 漏光：紅／黃／藍三色電漿各自的貢獻量，色相為整體旋轉
        ["light_leak.red"] = Gradient((0, Off), (1, Color.Parse("#E04848"))),
        ["light_leak.yellow"] = Gradient((0, Off), (1, Color.Parse("#F0D58A"))),
        ["light_leak.blue"] = Gradient((0, Off), (1, Color.Parse("#3F6FE0"))),
        ["light_leak.hue"] = HueWheel,
        // 膚色參考增益 0.25–4（預設 1 約在 20% 處）：預設點為中性灰，往右該色變強
        ["skin.ref_red"] = Gain(Color.Parse("#E04848")),
        ["skin.ref_green"] = Gain(Color.Parse("#4CB050")),
        ["skin.ref_blue"] = Gain(Color.Parse("#3F6FE0")),
        ["tone.shadow_hue"] = HueWheel,
        ["tone.highlight_hue"] = HueWheel,
        ["photo_plate.tint_hue"] = HueWheel,
    };

    private static readonly Dictionary<string, IBrush> Bands = BuildBands();

    /// <summary>參數（扁平 key，例如 <c>tone.shadow_hue</c>）的軌道漸層；沒有色彩語意時回傳 null。</summary>
    public static IBrush? For(string flatKey) =>
        ByFlatKey.GetValueOrDefault(flatKey) ?? Bands.GetValueOrDefault(flatKey);

    /// <summary>
    /// 底片風格的八個色相帶：色相 −100–100 對應中心 ±30°（film/look.wgsl），
    /// 飽和由灰到鮮豔，明度由暗到亮，三者都以該色相帶的示意色為中心。
    /// </summary>
    private static Dictionary<string, IBrush> BuildBands()
    {
        var bands = new Dictionary<string, IBrush>(StringComparer.Ordinal);
        for (var b = 0; b < HueBands.Keys.Count; b++)
        {
            var hsv = BandColors[b].ToHsv();
            bands["film." + HueBands.ParamKey(b, 'h')] = HueRange(hsv.H - 30, hsv.H + 30, hsv.S, hsv.V);
            bands["film." + HueBands.ParamKey(b, 's')] = Gradient(
                (0, HsvColor.ToRgb(hsv.H, 0.05, hsv.V * 0.7)), (0.5, HsvColor.ToRgb(hsv.H, hsv.S * 0.5, hsv.V * 0.85)), (1, BandColors[b]));
            bands["film." + HueBands.ParamKey(b, 'l')] = Gradient(
                (0, HsvColor.ToRgb(hsv.H, hsv.S, hsv.V * 0.3)), (0.5, BandColors[b]), (1, HsvColor.ToRgb(hsv.H, hsv.S * 0.35, 1)));
        }
        return bands;
    }

    /// <summary>從 <paramref name="from"/>° 到 <paramref name="to"/>° 的色相漸層（每 30° 以內一個色標，避免 sRGB 內插變灰）。</summary>
    private static IBrush HueRange(double from, double to, double saturation, double value)
    {
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(to - from) / 30));
        return Gradient([.. Enumerable.Range(0, steps + 1).Select(i =>
        {
            var t = (double)i / steps;
            var hue = ((from + (to - from) * t) % 360 + 360) % 360;
            return (t, HsvColor.ToRgb(hue, saturation, value));
        })]);
    }

    /// <summary>RGB 增益（0.25–4，1 為中性）：暗灰 → 中性灰（預設值位置）→ 該色。</summary>
    private static IBrush Gain(Color color) =>
        Gradient((0, Color.Parse("#333333")), ((1 - 0.25) / (4 - 0.25), Color.Parse("#6A6A6A")), (1, color));

    private static IBrush Gradient(params (double Offset, Color Color)[] stops) => new ImmutableLinearGradientBrush(
        [.. stops.Select(s => new ImmutableGradientStop(s.Offset, s.Color))],
        startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
        endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
}
