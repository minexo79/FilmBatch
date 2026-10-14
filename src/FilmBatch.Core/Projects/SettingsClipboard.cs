using FilmBatch.Core.Params;

namespace FilmBatch.Core.Projects;

public enum PasteMode
{
    /// <summary>presetId + 所有覆寫（Ctrl/⌘+V）。</summary>
    Full,
    /// <summary>只改 presetId，保留目標原本的覆寫。</summary>
    PresetOnly,
    /// <summary>以模組為單位勾選（Ctrl/⌘+Shift+V）。</summary>
    Selective,
}

/// <summary>複製下來的設定（程式內部剪貼簿；同時以 JSON 寫入系統剪貼簿）。</summary>
public sealed record SettingsClipboard(string SourceFile, Guid? PresetId, Settings Overrides, Settings Resolved);

public sealed record PasteOptions
{
    public PasteMode Mode { get; init; } = PasteMode.Full;
    /// <summary>選擇性貼上時勾選的模組 key。</summary>
    public IReadOnlySet<string> Modules { get; init; } = new HashSet<string>();
    /// <summary>預設不複製隨機種子，否則貼上後每張的形狀會一模一樣。</summary>
    public bool IncludeSeeds { get; init; }
    /// <summary>位置類參數水平鏡像（橫直幅混用時）。</summary>
    public bool MirrorPositions { get; init; }
}
