using FilmBatch.Core.Params;

namespace FilmBatch.Core.Projects;

/// <summary>一張照片的設定：指定的預設 + 只記錄差異的覆寫。不可變，修改即產生新物件（復原堆疊依此保存前後狀態）。</summary>
public sealed record PhotoEntry(string FileName, Guid? PresetId, Settings Overrides)
{
    public static PhotoEntry New(string fileName, Guid? presetId) => new(fileName, presetId, Settings.Empty);

    public bool HasOverrides => Overrides.Count > 0;

    /// <summary>被覆寫的模組 key 集合。</summary>
    public IEnumerable<string> OverriddenModules => Overrides.Keys.Select(ModuleSchema.ModuleOf).Distinct();

    public bool Equals(PhotoEntry? other) =>
        other is not null && FileName == other.FileName && PresetId == other.PresetId && Overrides.Equals(other.Overrides);

    public override int GetHashCode() => HashCode.Combine(FileName, PresetId);
}
