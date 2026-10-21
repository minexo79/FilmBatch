using FilmBatch.Core.Params;

namespace FilmBatch.Core.Projects;

/// <summary>
/// 照片設定的純函式操作（計畫書 3.4、3.5）。所有操作都遵守「與預設相同的覆寫自動移除」。
/// <paramref name="presetBase"/> 一律為 系統預設 ⊕ 目標照片所用預設。
/// </summary>
public static class PhotoOperations
{
    /// <summary>三層合併：系統預設 ⊕ 預設 ⊕ 覆寫。</summary>
    public static Settings Resolve(Settings presetBase, Settings overrides) => presetBase.Overlay(overrides);

    /// <summary>移除與基準相同的覆寫，並移除 schema 中不存在的 key。</summary>
    public static Settings Clean(Settings overrides, Settings presetBase) =>
        overrides.Where((k, v) => ModuleCatalog.Param(k) is not null && (!presetBase.TryGetValue(k, out var b) || b != v));

    public static PhotoEntry SetValue(PhotoEntry entry, string key, ParamValue value, Settings presetBase)
    {
        var def = ModuleCatalog.Param(key) ?? throw new ArgumentException($"未知的參數：{key}");
        var v = def.Coerce(value);
        var overrides = presetBase.TryGetValue(key, out var b) && b == v
            ? entry.Overrides.Without(key)
            : entry.Overrides.With(key, v);
        return entry with { Overrides = overrides };
    }

    public static PhotoEntry ResetParam(PhotoEntry entry, string key) =>
        entry with { Overrides = entry.Overrides.Without(key) };

    public static PhotoEntry ResetModule(PhotoEntry entry, string moduleKey) =>
        entry with { Overrides = entry.Overrides.WithoutRange(ModuleCatalog.Module(moduleKey).FlatKeys) };

    public static PhotoEntry ResetAll(PhotoEntry entry) => entry with { Overrides = Settings.Empty, ForegroundMask = null };

    public static PhotoEntry AssignPreset(PhotoEntry entry, Guid? presetId, bool keepOverrides, Settings newPresetBase) =>
        entry with
        {
            PresetId = presetId,
            Overrides = keepOverrides ? Clean(entry.Overrides, newPresetBase) : Settings.Empty,
        };

    /// <summary>貼上。<paramref name="baseFor"/> 依 presetId 取得 系統預設 ⊕ 預設。</summary>
    public static PhotoEntry Paste(SettingsClipboard clip, PhotoEntry target, PasteOptions options, Func<Guid?, Settings> baseFor)
    {
        switch (options.Mode)
        {
            case PasteMode.PresetOnly:
                return AssignPreset(target, clip.PresetId, keepOverrides: true, baseFor(clip.PresetId));

            case PasteMode.Full:
            {
                // 來源的所有覆寫；種子沿用目標自己的（除非勾選包含種子）。
                // 裁切與細部調整只屬於單張照片：不貼上，目標自己的保留（只能以選擇性貼上複製）。
                var incoming = clip.Overrides.Where((k, _) => (options.IncludeSeeds || !IsSeed(k)) && !IsPerPhoto(k));
                var kept = target.Overrides.Where((k, _) => (!options.IncludeSeeds && IsSeed(k)) || IsPerPhoto(k));
                var overrides = kept.Overlay(Mirror(incoming, options.MirrorPositions));
                var presetBase = baseFor(clip.PresetId);
                return target with { PresetId = clip.PresetId, Overrides = Clean(overrides, presetBase) };
            }

            case PasteMode.Selective:
            {
                // 勾選的模組（含開關）以來源的最終值寫成目標的覆寫
                var presetBase = baseFor(target.PresetId);
                var overrides = target.Overrides;
                foreach (var moduleKey in options.Modules)
                {
                    var module = ModuleCatalog.Module(moduleKey);
                    foreach (var p in module.Params)
                    {
                        if (p.IsSeed && !options.IncludeSeeds) continue;
                        var key = module.FlatKey(p.Key);
                        if (!clip.Resolved.TryGetValue(key, out var v)) continue;
                        overrides = overrides.With(key, options.MirrorPositions ? MirrorValue(p, v) : v);
                    }
                }
                return target with { Overrides = Clean(overrides, presetBase) };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static bool IsSeed(string key) => ModuleCatalog.Param(key)?.IsSeed == true;

    /// <summary>只屬於單張照片的分類（裁切）：全部貼上預設不含。</summary>
    public static bool IsPerPhoto(PasteCategory category) => category is PasteCategory.Crop;

    private static bool IsPerPhoto(string key) =>
        ModuleCatalog.TryGetModule(ModuleSchema.ModuleOf(key), out var m) && IsPerPhoto(m.PasteCategory);

    private static Settings Mirror(Settings s, bool mirror)
    {
        if (!mirror) return s;
        var result = s;
        foreach (var (k, v) in s)
            if (ModuleCatalog.Param(k) is { } p) result = result.With(k, MirrorValue(p, v));
        return result;
    }

    /// <summary>水平鏡像：X 座標 x → 1 − x；以 0 為中心的位移取負值。</summary>
    public static ParamValue MirrorValue(ParamDef p, ParamValue v) => p.Axis switch
    {
        PositionAxis.X when p.IsCenteredOffset => ParamValue.Of(-v.AsNumber),
        PositionAxis.X => ParamValue.Of(1 - v.AsNumber),
        _ => v,
    };
}
