using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Random;

namespace FilmBatch.Core.Projects;

/// <summary>
/// 一個照片資料夾的專案。照片 key 保留原始大小寫並以 Ordinal 比對。
/// 修改一律經由 <see cref="ProjectService"/> 的命令，才能復原。
/// </summary>
public sealed class Project
{
    public static readonly string[] SupportedExtensions = [".tif", ".tiff", ".jpg", ".jpeg", ".png"];

    private readonly Dictionary<string, PhotoEntry> _photos = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public string Folder { get; }
    public int PipelineVersion { get; init; } = 1;
    public Guid DefaultPresetId { get; set; }

    /// <summary>所用預設的快照（預設庫中被刪除或修改時，舊專案的結果不會在不知情時改變）。</summary>
    public Dictionary<Guid, Preset> PresetSnapshots { get; } = [];

    /// <summary>照片設定改變（參數為受影響的檔名）。</summary>
    public event EventHandler<IReadOnlyCollection<string>>? EntriesChanged;

    public Project(string folder, Guid defaultPresetId)
    {
        Folder = folder;
        DefaultPresetId = defaultPresetId;
    }

    /// <summary>掃描資料夾中的照片，新照片自動指定為預設預設。</summary>
    public static Project Open(string folder, Guid defaultPresetId)
    {
        var project = new Project(folder, defaultPresetId);
        var files = Directory.EnumerateFiles(folder)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal);
        foreach (var f in files) project.Add(PhotoEntry.New(f, defaultPresetId));
        return project;
    }

    public void Add(PhotoEntry entry)
    {
        if (_photos.TryAdd(entry.FileName, entry)) _order.Add(entry.FileName);
    }

    public IReadOnlyList<string> FileNames => _order;

    public PhotoEntry this[string fileName] => _photos[fileName];

    public string PathOf(string fileName) => Path.Combine(Folder, fileName);

    public uint SeedFor(string fileName) => SeedHash.ForFileName(fileName);

    internal void Replace(IReadOnlyDictionary<string, PhotoEntry> entries)
    {
        foreach (var (k, v) in entries) _photos[k] = v;
        EntriesChanged?.Invoke(this, entries.Keys.ToList());
    }

    internal void RaiseChanged(IReadOnlyCollection<string> names) => EntriesChanged?.Invoke(this, names);
}
