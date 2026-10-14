using FilmBatch.Core.Params;

namespace FilmBatch.Core.Presets;

/// <summary>原型用的範例預設庫。</summary>
public static class SamplePresets
{
    public static readonly Guid Neutral = Guid.Parse("00000000-0000-4000-8000-000000000001");
    public static readonly Guid PortraWarm = Guid.Parse("3f2a9c1e-8b4d-4e2a-9f1c-0a7b6d5e4c3b");

    private static Settings S(params (string Key, ParamValue Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, ParamValue>(v.Key, v.Value)));

    public static IReadOnlyList<Preset> Create() =>
    [
        new()
        {
            Id = Neutral, Name = "中性（無效果）", Category = "基本", ColorLabel = "#7A7A7A", Pinned = true,
        },
        new()
        {
            Id = PortraWarm, Name = "Portra 暖調 + 漏光", Category = "人像", ColorLabel = "#D98E2B", Pinned = true,
            Settings = S(
                ("basic.brightness", 5.0), ("basic.highlights", -10.0), ("basic.shadows", 10.0),
                ("vignette.enabled", true), ("vignette.amount", 15.0), ("vignette.size", 60.0), ("vignette.feather", 60.0),
                ("halation.enabled", true),
                ("light_leak.enabled", true),
                ("film.enabled", true), ("film.stock", "portra400"), ("film.strength", 85.0), ("film.contrast", -10.0), ("film.warmth", 15.0), ("film.fade", 8.0),
                ("tone.enabled", true), ("tone.shadow_amount", 10.0), ("tone.highlight_amount", 15.0),
                ("grain.enabled", true)),
        },
        new()
        {
            Id = Guid.Parse("a81b0c4e-2d3f-4b5a-9c6d-7e8f90a1b2c3"), Name = "Ektar 鮮豔", Category = "風景", ColorLabel = "#6BBF59", Pinned = true,
            Settings = S(
                ("basic.saturation", 15.0), ("basic.contrast", 10.0),
                ("film.enabled", true), ("film.stock", "ektar100"), ("film.saturation", 10.0),
                ("grain.enabled", true), ("grain.amount", 12.0)),
        },
        new()
        {
            Id = Guid.Parse("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e"), Name = "Tri-X 高反差", Category = "黑白", ColorLabel = "#DCDCDC", Pinned = true,
            Settings = S(
                ("basic.saturation", -100.0), ("basic.contrast", 25.0),
                ("film.enabled", true), ("film.stock", "trix400"),
                ("grain.enabled", true), ("grain.amount", 40.0), ("grain.size", 40.0)),
        },
        new()
        {
            Id = Guid.Parse("c3d4e5f6-a7b8-4c9d-8e1f-2a3b4c5d6e7f"), Name = "夜景 CineStill", Category = "夜景", ColorLabel = "#5AA9E6",
            Settings = S(
                ("basic.exposure", 0.3),
                ("halation.enabled", true), ("halation.amount", 45.0),
                ("film.enabled", true), ("film.stock", "cinestill800t"),
                ("grain.enabled", true)),
        },
        new()
        {
            Id = Guid.Parse("d4e5f6a7-b8c9-4d0e-9f2a-3b4c5d6e7f80"), Name = "夢幻散景", Category = "人像", ColorLabel = "#E05555",
            Settings = S(
                ("basic.brightness", 10.0), ("basic.saturation", -10.0),
                ("bokeh.enabled", true), ("bokeh.blur", 30.0),
                ("film.enabled", true), ("film.stock", "pro400h")),
        },
    ];
}
