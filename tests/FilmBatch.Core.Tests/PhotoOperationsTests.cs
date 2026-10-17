using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;

namespace FilmBatch.Core.Tests;

/// <summary>三層設定模型與覆寫規則（計畫書 3.1、3.4、3.5）。</summary>
public class PhotoOperationsTests
{
    private static Settings S(params (string Key, ParamValue Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, ParamValue>(v.Key, v.Value)));

    private static readonly Settings PresetA = ModuleCatalog.Defaults.Overlay(S(("grain.enabled", true), ("grain.amount", 40.0), ("film.recipe", "kodak-ektar-100")));
    private static readonly Settings PresetB = ModuleCatalog.Defaults.Overlay(S(("grain.enabled", true), ("grain.amount", 10.0)));
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();

    private static Settings BaseFor(Guid? id) => id == IdA ? PresetA : id == IdB ? PresetB : ModuleCatalog.Defaults;

    [Fact]
    public void Resolve_LayersDefaultsPresetAndOverrides()
    {
        var resolved = PhotoOperations.Resolve(PresetA, S(("grain.amount", 55.0)));
        Assert.Equal(55, resolved.Number("grain.amount"));          // 覆寫
        Assert.Equal("kodak-ektar-100", resolved.Text("film.recipe"));      // 預設
        Assert.Equal(30, resolved.Number("grain.size"));            // 系統預設
    }

    [Fact]
    public void SetValue_EqualToPreset_RemovesOverride()
    {
        var e = PhotoEntry.New("a.tif", IdA);
        e = PhotoOperations.SetValue(e, "grain.amount", 60.0, PresetA);
        Assert.True(e.Overrides.ContainsKey("grain.amount"));
        e = PhotoOperations.SetValue(e, "grain.amount", 40.0, PresetA);
        Assert.False(e.HasOverrides);
    }

    [Fact]
    public void SetValue_ClampsToSchemaRange()
    {
        var e = PhotoOperations.SetValue(PhotoEntry.New("a.tif", null), "basic.exposure", 99.0, ModuleCatalog.Defaults);
        Assert.Equal(3, e.Overrides["basic.exposure"].AsNumber);
    }

    [Fact]
    public void SetValue_UnknownKey_Throws() =>
        Assert.Throws<ArgumentException>(() => PhotoOperations.SetValue(PhotoEntry.New("a.tif", null), "nope.x", 1.0, ModuleCatalog.Defaults));

    [Fact]
    public void ResetModule_RemovesOnlyThatModule()
    {
        var e = new PhotoEntry("a.tif", IdA, S(("grain.amount", 60.0), ("grain.enabled", false), ("basic.exposure", 1.0)));
        e = PhotoOperations.ResetModule(e, "grain");
        Assert.Equal(["basic.exposure"], e.Overrides.Keys);
    }

    [Fact]
    public void AssignPreset_KeepOverrides_CleansOverridesEqualToNewPreset()
    {
        // grain.amount = 10 是覆寫（相對 A），改指定 B 後與 B 相同 → 自動移除
        var e = new PhotoEntry("a.tif", IdA, S(("grain.amount", 10.0), ("basic.exposure", 0.5)));
        var kept = PhotoOperations.AssignPreset(e, IdB, keepOverrides: true, PresetB);
        Assert.Equal(IdB, kept.PresetId);
        Assert.Equal(["basic.exposure"], kept.Overrides.Keys);

        var cleared = PhotoOperations.AssignPreset(e, IdB, keepOverrides: false, PresetB);
        Assert.False(cleared.HasOverrides);
    }

    [Fact]
    public void PasteFull_CopiesPresetAndOverrides_ButNotSeedsByDefault()
    {
        var clip = new SettingsClipboard("src.tif", IdA, S(("grain.amount", 70.0), ("light_leak.seed", 12.0)), PresetA);
        var target = new PhotoEntry("dst.tif", IdB, S(("light_leak.seed", 3.0), ("basic.exposure", 1.0)));

        var pasted = PhotoOperations.Paste(clip, target, new PasteOptions { Mode = PasteMode.Full }, BaseFor);

        Assert.Equal(IdA, pasted.PresetId);
        Assert.Equal(70, pasted.Overrides["grain.amount"].AsNumber);
        Assert.Equal(3, pasted.Overrides["light_leak.seed"].AsNumber); // 種子沿用目標自己的
        Assert.False(pasted.Overrides.ContainsKey("basic.exposure"));    // 完整貼上取代目標的覆寫
    }

    [Fact]
    public void PasteFull_IncludeSeeds_CopiesSeeds()
    {
        var clip = new SettingsClipboard("src.tif", IdA, S(("light_leak.seed", 12.0)), PresetA);
        var target = new PhotoEntry("dst.tif", IdB, S(("light_leak.seed", 3.0)));
        var pasted = PhotoOperations.Paste(clip, target, new PasteOptions { Mode = PasteMode.Full, IncludeSeeds = true }, BaseFor);
        Assert.Equal(12, pasted.Overrides["light_leak.seed"].AsNumber);
    }

    [Fact]
    public void PastePresetOnly_KeepsTargetOverrides()
    {
        var clip = new SettingsClipboard("src.tif", IdA, S(("grain.amount", 70.0)), PresetA);
        var target = new PhotoEntry("dst.tif", IdB, S(("basic.exposure", 1.0)));
        var pasted = PhotoOperations.Paste(clip, target, new PasteOptions { Mode = PasteMode.PresetOnly }, BaseFor);
        Assert.Equal(IdA, pasted.PresetId);
        Assert.Equal(["basic.exposure"], pasted.Overrides.Keys);
    }

    [Fact]
    public void PasteSelective_WritesCheckedModulesIncludingSwitch_AndCleans()
    {
        // 來源：A + grain 關閉、basic.exposure 0.7；只勾選 grain
        var sourceResolved = PresetA.Overlay(S(("grain.enabled", false), ("basic.exposure", 0.7)));
        var clip = new SettingsClipboard("src.tif", IdA, S(("grain.enabled", false), ("basic.exposure", 0.7)), sourceResolved);
        var target = PhotoEntry.New("dst.tif", IdB);

        var pasted = PhotoOperations.Paste(clip, target,
            new PasteOptions { Mode = PasteMode.Selective, Modules = new HashSet<string> { "grain" } }, BaseFor);

        Assert.Equal(IdB, pasted.PresetId);                                  // 預設不變
        Assert.False(pasted.Overrides["grain.enabled"].AsBool);              // 開關也被複製
        Assert.Equal(40, pasted.Overrides["grain.amount"].AsNumber);         // A 的 40 ≠ B 的 10
        Assert.False(pasted.Overrides.ContainsKey("grain.size"));            // 與 B 相同 → 清理
        Assert.False(pasted.Overrides.ContainsKey("basic.exposure"));        // 未勾選的模組不動
    }

    [Fact]
    public void PasteSelective_MirrorPositions()
    {
        var resolved = ModuleCatalog.Defaults.Overlay(S(("vignette.center_x", 0.2), ("double_exposure.offset_x", 0.1)));
        var clip = new SettingsClipboard("src.tif", null, Settings.Empty, resolved);
        var pasted = PhotoOperations.Paste(clip, PhotoEntry.New("dst.tif", null),
            new PasteOptions { Mode = PasteMode.Selective, Modules = new HashSet<string> { "vignette", "double_exposure" }, MirrorPositions = true },
            BaseFor);
        Assert.Equal(0.8, pasted.Overrides["vignette.center_x"].AsNumber, 6);
        Assert.Equal(-0.1, pasted.Overrides["double_exposure.offset_x"].AsNumber, 6);
        Assert.False(pasted.Overrides.ContainsKey("vignette.center_y")); // y 不鏡像，與預設相同
    }

    [Fact]
    public void Schema_Has17ModulesInPipelineOrder()
    {
        Assert.Equal(17, ModuleCatalog.Modules.Count);
        Assert.Equal("raw", ModuleCatalog.Modules[0].Key);
        Assert.Equal("basic", ModuleCatalog.Modules[1].Key);
        Assert.Equal("grain", ModuleCatalog.Modules[^1].Key);
        Assert.All(ModuleCatalog.Modules, m => Assert.Equal(ModuleSchema.EnabledKey, m.Params[0].Key));
        // 群組順序與處理階段一致
        var groups = ModuleCatalog.Modules.Select(m => (int)m.Group).ToList();
        Assert.Equal(groups.Order(), groups);
    }

    [Fact]
    public void Defaults_AllWithinRange()
    {
        foreach (var key in ModuleCatalog.AllFlatKeys)
        {
            var def = ModuleCatalog.Param(key)!;
            Assert.Equal(def.Default, def.Coerce(def.Default));
        }
    }
}
