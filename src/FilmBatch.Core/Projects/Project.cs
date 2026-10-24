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
    /// <summary>可由 RAW 解碼器辨識的副檔名；各機身的壓縮變體仍由檔頭判斷。</summary>
    public static readonly string[] RawExtensions =
    [
        ".arw", ".sr2", ".srf", ".dng", ".nef", ".nrw", ".cr2", ".cr3", ".raf",
        ".rw2", ".rwl", ".raw", ".orf", ".pef", ".srw", ".erf", ".mef", ".3fr", ".fff",
    ];

    public static readonly string[] SupportedExtensions = OperatingSystem.IsIOS()
        ? [".tif", ".tiff", ".jpg", ".jpeg", ".png", ".heic", ".heif", .. RawExtensions]
        : [".tif", ".tiff", ".jpg", ".jpeg", ".png", .. RawExtensions];

    /// <summary>依副檔名判斷是否為 RAW 檔（不讀檔頭）。</summary>
    public static bool IsRawFile(string path) =>
        RawExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PhotoEntry> _photos = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<Guid, DateTimeOffset> _keptPresetUpdates = [];
    private PresetLibrary? _library;
    private bool _autoSave;
    private string _diskRevision = "";

    /// <summary>已開啟的專案以快照保留原結果，直到使用者明確同步預設。</summary>
    public bool PreserveSnapshots { get; private set; }

    public string Folder { get; }
    public int PipelineVersion { get; init; } = ProjectStorage.CurrentVersion;
    public Guid DefaultPresetId { get; set; }

    /// <summary>所用預設的快照（預設庫中被刪除或修改時，舊專案的結果不會在不知情時改變）。</summary>
    public Dictionary<Guid, Preset> PresetSnapshots { get; } = [];

    /// <summary>照片設定改變（參數為受影響的檔名）。</summary>
    public event EventHandler<IReadOnlyCollection<string>>? EntriesChanged;

    public Project(string folder, Guid defaultPresetId)
    {
        Folder = Path.GetFullPath(folder);
        DefaultPresetId = defaultPresetId;
    }

    /// <summary>載入既有編輯並掃描照片；損毀或新版專案會報錯，絕不當成新專案覆寫。</summary>
    public static Project Open(string folder, Guid defaultPresetId)
    {
        folder = Path.GetFullPath(folder);
        var path = ProjectStorage.FilePath(folder);
        var revision = ProjectStorage.Revision(path);
        var saved = revision.Length == 0 ? null : ProjectStorage.Read(path);
        if (ProjectStorage.Revision(path) != revision)
            throw new IOException("The project changed while opening. Please reopen it.");
        var project = new Project(folder, saved?.DefaultPresetId ?? defaultPresetId)
        {
            _autoSave = true,
            _diskRevision = revision,
            PreserveSnapshots = saved is not null,
        };
        if (saved is not null)
        {
            foreach (var (id, preset) in saved.PresetSnapshots) project.PresetSnapshots.Add(id, preset.ToPreset(id));
            foreach (var (id, updatedAt) in saved.KeptPresetUpdates) project._keptPresetUpdates.Add(id, updatedAt);
            foreach (var (name, photo) in saved.Photos)
                project._photos.Add(name, new PhotoEntry(name, photo.PresetId, photo.Overrides)
                {
                    ForegroundMask = photo.ForegroundMask is { } mask ? ForegroundMask.FromDocument(mask) : null,
                    Rating = Math.Clamp(photo.Rating, 0, PhotoEntry.MaxRating),
                });
        }
        foreach (var file in Directory.EnumerateFiles(folder)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
        {
            project._photos.TryAdd(file, PhotoEntry.New(file, project.DefaultPresetId));
            project._order.Add(file);
        }
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

    internal void AttachLibrary(PresetLibrary library)
    {
        _library = library;
        CaptureMissingSnapshots(library);
    }

    private void CaptureMissingSnapshots(PresetLibrary library)
    {
        foreach (var id in _photos.Values.Select(photo => photo.PresetId).Append(DefaultPresetId).OfType<Guid>().Distinct())
            if (!PresetSnapshots.ContainsKey(id) && library.Get(id) is { } preset) PresetSnapshots.Add(id, preset);
    }

    public void Save(PresetLibrary library) => SaveCore(library, null);

    private void SaveCore(PresetLibrary library, IReadOnlyDictionary<string, PhotoEntry>? changes)
    {
        CaptureMissingSnapshots(library);
        if (changes is not null)
            foreach (var id in changes.Values.Select(photo => photo.PresetId).OfType<Guid>().Distinct())
                if (!PresetSnapshots.ContainsKey(id) && library.Get(id) is { } preset) PresetSnapshots.Add(id, preset);
        var snapshots = new Dictionary<Guid, Preset>(PresetSnapshots);
        if (!PreserveSnapshots)
            foreach (var id in snapshots.Keys)
                if (library.Get(id) is { } live) snapshots[id] = live;
        var photos = _photos.ToDictionary(pair => pair.Key, pair =>
        {
            var photo = changes?.GetValueOrDefault(pair.Key) ?? pair.Value;
            return new PhotoDocument
            {
                PresetId = photo.PresetId, Overrides = photo.Overrides, ForegroundMask = photo.ForegroundMask?.ToDocument(),
                Rating = photo.Rating,
            };
        }, StringComparer.Ordinal);
        var document = new ProjectDocument
        {
            PipelineVersion = PipelineVersion, DefaultPresetId = DefaultPresetId,
            Photos = photos, PresetSnapshots = snapshots.ToDictionary(pair => pair.Key, pair => PresetDocument.From(pair.Value)),
            KeptPresetUpdates = new(_keptPresetUpdates),
        };
        _diskRevision = ProjectStorage.Write(ProjectStorage.FilePath(Folder), document, _diskRevision);
        foreach (var (id, preset) in snapshots) PresetSnapshots[id] = preset;
    }

    public void KeepPresetSnapshots() => PreserveSnapshots = true;

    public IReadOnlyList<Preset> PendingPresetUpdates(PresetLibrary library) =>
        PresetSnapshots.Where(pair => library.Get(pair.Key) is { } live
                && (!live.Settings.Equals(pair.Value.Settings) || live.Name != pair.Value.Name || live.Category != pair.Value.Category
                    || live.ColorLabel != pair.Value.ColorLabel || live.Pinned != pair.Value.Pinned)
                && (!_keptPresetUpdates.TryGetValue(pair.Key, out var kept) || kept != live.UpdatedAt))
            .Select(pair => library.Get(pair.Key)!).ToArray();

    public void ResolvePresetUpdate(Preset preset, bool synchronize)
    {
        var previousPreserve = PreserveSnapshots;
        var previousSnapshot = PresetSnapshots.GetValueOrDefault(preset.Id);
        var hadKept = _keptPresetUpdates.TryGetValue(preset.Id, out var previousKept);
        PreserveSnapshots = true;
        if (synchronize)
        {
            PresetSnapshots[preset.Id] = preset;
            _keptPresetUpdates.Remove(preset.Id);
        }
        else _keptPresetUpdates[preset.Id] = preset.UpdatedAt;
        try
        {
            if (_autoSave && _library is not null) Save(_library);
        }
        catch
        {
            PreserveSnapshots = previousPreserve;
            if (previousSnapshot is null) PresetSnapshots.Remove(preset.Id);
            else PresetSnapshots[preset.Id] = previousSnapshot;
            if (hadKept) _keptPresetUpdates[preset.Id] = previousKept;
            else _keptPresetUpdates.Remove(preset.Id);
            throw;
        }
        RaiseChanged(_order.Where(name => _photos[name].PresetId == preset.Id).ToArray());
    }

    internal void Replace(IReadOnlyDictionary<string, PhotoEntry> entries)
    {
        if (_autoSave && _library is not null) SaveCore(_library, entries);
        foreach (var (k, v) in entries) _photos[k] = v;
        EntriesChanged?.Invoke(this, entries.Keys.ToList());
    }

    internal void RaiseChanged(IReadOnlyCollection<string> names) => EntriesChanged?.Invoke(this, names);
}
