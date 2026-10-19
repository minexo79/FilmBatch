using FilmBatch.Core.Params;

namespace FilmBatch.Core.Presets;

/// <summary>內建範例預設（film 模組以 film.look 引用內附底片 LUT）。</summary>
public static class SamplePresets
{
    public static readonly Guid Neutral = Guid.Parse("00000000-0000-4000-8000-000000000001");
    public static readonly Guid PortraWarm = Guid.Parse("3f2a9c1e-8b4d-4e2a-9f1c-0a7b6d5e4c3b");

    private static Settings S(params (string Key, ParamValue Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, ParamValue>(v.Key, v.Value)));

    private static Settings WithLook(IEnumerable<(string Key, ParamValue Value)> extra, string lookKey, double strength = 100) =>
        S([("film.enabled", true), ("film.look", lookKey), ("film.strength", strength), .. extra]);

    public static IReadOnlyList<Preset> Create() =>
    [
        new()
        {
            Id = Neutral, Name = "Neutral (No Effect)", Category = "Basic", ColorLabel = "#7A7A7A", Pinned = true,
        },
        new()
        {
            Id = PortraWarm, Name = "Portra Warm + Light Leak", Category = "Portrait", ColorLabel = "#D98E2B", Pinned = true,
            Settings = WithLook([
                ("basic.brightness", 5.0), ("basic.highlights", -10.0), ("basic.shadows", 10.0),
                ("vignette.enabled", true), ("vignette.amount", 15.0), ("vignette.size", 60.0), ("vignette.feather", 60.0),
                ("halation.enabled", true),
                ("light_leak.enabled", true),
                ("tone.enabled", true), ("tone.shadow_amount", 10.0), ("tone.highlight_amount", 15.0),
                ("grain.enabled", true),
            ], "kodak-portra-400", 85.0),
        },
        new()
        {
            Id = Guid.Parse("a81b0c4e-2d3f-4b5a-9c6d-7e8f90a1b2c3"), Name = "Ektar Vivid", Category = "Landscape", ColorLabel = "#6BBF59", Pinned = true,
            Settings = WithLook([
                ("basic.saturation", 15.0), ("basic.contrast", 10.0),
                ("grain.enabled", true), ("grain.amount", 12.0),
            ], "kodak-ektar-100"),
        },
        new()
        {
            Id = Guid.Parse("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e"), Name = "Tri-X High Contrast", Category = "B&W", ColorLabel = "#DCDCDC", Pinned = true,
            Settings = WithLook([
                ("basic.saturation", -100.0), ("basic.contrast", 25.0),
                ("grain.enabled", true), ("grain.amount", 40.0), ("grain.size", 40.0),
            ], "kodak-tri-x-400"),
        },
        new()
        {
            Id = Guid.Parse("c3d4e5f6-a7b8-4c9d-8e1f-2a3b4c5d6e7f"), Name = "Portra 800 Night", Category = "Night", ColorLabel = "#5AA9E6",
            Settings = WithLook([
                ("basic.exposure", 0.3),
                ("halation.enabled", true), ("halation.amount", 45.0),
                ("grain.enabled", true),
            ], "kodak-portra-800"),
        },
        new()
        {
            Id = Guid.Parse("d4e5f6a7-b8c9-4d0e-9f2a-3b4c5d6e7f80"), Name = "Dreamy Bokeh", Category = "Portrait", ColorLabel = "#E05555",
            Settings = WithLook([
                ("basic.brightness", 10.0), ("basic.saturation", -10.0),
                ("bokeh.enabled", true), ("bokeh.blur", 30.0),
            ], "fuji-pro-400h"),
        },
    ];
}
