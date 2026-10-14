using FilmBatch.Core.Params;

namespace FilmBatch.Core.Presets;

/// <summary>預設：一組參數（包含各模組開關）。以 <see cref="Id"/> 識別，改名不影響已指定的照片。</summary>
public sealed record Preset
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string Category { get; init; } = "未分類";
    /// <summary>色彩標籤，例如 <c>#D98E2B</c>。</summary>
    public string ColorLabel { get; init; } = "#7A7A7A";
    public int PipelineVersion { get; init; } = 1;
    /// <summary>釘選的預設可用數字鍵 1–9 快速套用。</summary>
    public bool Pinned { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>此預設的參數（只需包含與系統預設不同的值；合併時以系統預設補齊）。</summary>
    public Settings Settings { get; init; } = Settings.Empty;

    /// <summary>系統預設 ⊕ 本預設：照片覆寫的比較基準。</summary>
    public Settings Resolved => ModuleCatalog.Defaults.Overlay(Settings);
}
