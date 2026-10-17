using FilmBatch.Core.Params;
using static FilmBatch.Core.Params.ParamDef;

namespace FilmBatch.Core.Film;

/// <summary>
/// 分色相修正：OKLCh 色相環上的八個色相帶，各有色相／飽和／明度三個 −100–100 的參數。
/// 中心角度與 WGSL film/look.wgsl 的 CENTERS 必須相同。
/// </summary>
public static class HueBands
{
    public static IReadOnlyList<string> Keys { get; } = ["red", "orange", "yellow", "green", "aqua", "blue", "purple", "magenta"];
    public static IReadOnlyList<string> Labels { get; } = ["紅", "橙", "黃", "綠", "青", "藍", "紫", "洋紅"];
    public static readonly float[] CentersDegrees = [25, 55, 105, 140, 195, 255, 295, 335];
    public const string Channels = "hsl";

    public static string ParamKey(int band, char channel) => $"hue_{Keys[band]}_{channel}";

    /// <summary>24 個扁平 key（film.hue_red_h …），順序：色相帶 × (h, s, l)。</summary>
    public static IReadOnlyList<string> FlatKeys { get; } =
        [.. Enumerable.Range(0, 8).SelectMany(b => Channels.Select(c => "film." + ParamKey(b, c)))];

    public static IEnumerable<ParamDef> Params() =>
        Enumerable.Range(0, 8).SelectMany(b => new[]
        {
            Float(ParamKey(b, 'h'), Labels[b] + "色相", 0, -100, 100),
            Float(ParamKey(b, 's'), Labels[b] + "飽和", 0, -100, 100),
            Float(ParamKey(b, 'l'), Labels[b] + "明度", 0, -100, 100),
        });
}

/// <summary>內建色相修正（一鍵套用）；Values 只列非零欄位，其餘為 0。</summary>
public sealed record HuePreset(string Key, string Name, IReadOnlyDictionary<string, double> Values)
{
    public Settings ToSettings() => new(HueBands.FlatKeys.Select(key =>
        new KeyValuePair<string, ParamValue>(key, ParamValue.Of(Values.TryGetValue(key, out var v) ? v : 0.0))));
}

public static class HuePresets
{
    private static HuePreset P(string key, string name, params (string Key, double Value)[] values) =>
        new(key, name, values.ToDictionary(v => "film.hue_" + v.Key, v => v.Value, StringComparer.Ordinal));

    public static IReadOnlyList<HuePreset> All { get; } =
    [
        P("none", "無"),
        P("warm-skin", "暖膚色", ("red_h", 10), ("orange_s", 10), ("orange_l", 8), ("yellow_s", -10)),
        P("clear-sky", "清澈藍天", ("blue_s", 20), ("blue_l", -10), ("aqua_h", 10), ("aqua_s", 10)),
        P("lush-green", "青翠綠意", ("green_h", 15), ("green_s", 15), ("yellow_h", 15)),
        P("golden", "金黃秋色", ("orange_s", 20), ("yellow_h", -10), ("yellow_s", 20), ("green_h", -20), ("green_s", -15)),
        P("teal-orange", "冷暖對比", ("orange_s", 20), ("aqua_h", 5), ("aqua_s", 20), ("blue_h", -15), ("green_s", -20)),
        P("muted", "低彩度", ("red_s", -25), ("orange_s", -25), ("yellow_s", -25), ("green_s", -25),
            ("aqua_s", -25), ("blue_s", -25), ("purple_s", -25), ("magenta_s", -25)),
    ];

    /// <summary>目前 24 個值完全符合哪個內建預設；都不符合（使用者自訂）時回傳 null。</summary>
    public static HuePreset? Find(Settings resolved) => All.FirstOrDefault(preset =>
        preset.ToSettings().All(kv => resolved.TryGetValue(kv.Key, out var v) && v == kv.Value));
}
