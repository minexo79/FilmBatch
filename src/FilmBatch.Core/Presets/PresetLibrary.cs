namespace FilmBatch.Core.Presets;

/// <summary>
/// 全域預設庫。M0 為記憶體內實作；YAML 讀寫、資料夾監看於 M2 加入。
/// </summary>
public sealed class PresetLibrary
{
    private readonly List<Preset> _presets = [];

    public IReadOnlyList<Preset> Presets => _presets;

    /// <summary>預設新增、修改、刪除時觸發。</summary>
    public event EventHandler? Changed;

    public PresetLibrary(IEnumerable<Preset>? presets = null)
    {
        if (presets != null) _presets.AddRange(presets);
    }

    public Preset? Get(Guid id) => _presets.FirstOrDefault(p => p.Id == id);

    public void Save(Preset preset)
    {
        var i = _presets.FindIndex(p => p.Id == preset.Id);
        if (i >= 0) _presets[i] = preset;
        else _presets.Add(preset);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(Guid id)
    {
        var removed = _presets.RemoveAll(p => p.Id == id) > 0;
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>前 9 個釘選預設（數字鍵 1–9）。</summary>
    public IReadOnlyList<Preset> Pinned => _presets.Where(p => p.Pinned).Take(9).ToList();
}
