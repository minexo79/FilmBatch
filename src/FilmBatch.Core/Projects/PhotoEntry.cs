using FilmBatch.Core.Params;

namespace FilmBatch.Core.Projects;

/// <summary>一張照片的設定：指定的預設 + 只記錄差異的覆寫。不可變，修改即產生新物件（復原堆疊依此保存前後狀態）。</summary>
public sealed record PhotoEntry(string FileName, Guid? PresetId, Settings Overrides)
{
    public static PhotoEntry New(string fileName, Guid? presetId) => new(fileName, presetId, Settings.Empty);

    /// <summary>已套用的逐張去背遮罩；不屬於預設或可複製的參數。</summary>
    public ForegroundMask? ForegroundMask { get; init; }

    /// <summary>星級評分 0 ~ 5（0 = 未評分）；只是整理用的標記，不算覆寫，也不隨複製設定貼上。</summary>
    public int Rating { get; init; }

    public const int MaxRating = 5;

    public bool HasOverrides => Overrides.Count > 0 || ForegroundMask is not null;

    /// <summary>被覆寫的模組 key 集合。</summary>
    public IEnumerable<string> OverriddenModules => Overrides.Keys.Select(ModuleSchema.ModuleOf).Distinct();

    public bool Equals(PhotoEntry? other) =>
        other is not null && FileName == other.FileName && PresetId == other.PresetId
        && Overrides.Equals(other.Overrides) && Equals(ForegroundMask, other.ForegroundMask) && Rating == other.Rating;

    public override int GetHashCode() => HashCode.Combine(FileName, PresetId);
}
