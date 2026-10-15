using FilmBatch.Core.Projects;
using YamlDotNet.Core;

namespace FilmBatch.Core.Presets;

/// <summary>Global YAML preset library. Disk mutations complete before publishing in-memory changes.</summary>
public sealed class PresetLibrary : IDisposable
{
    private readonly List<Preset> _presets = [];
    private readonly Dictionary<Guid, (string Path, string Revision)> _files = [];
    private readonly string? _folder;
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private bool _disposed;

    public IReadOnlyList<Preset> Presets => _presets;
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];
    public bool IsExternalChange { get; private set; }
    public event EventHandler? Changed;
    public event Action<string>? Error;

    public PresetLibrary(IEnumerable<Preset>? presets = null)
    {
        if (presets is not null) _presets.AddRange(presets);
    }

    private PresetLibrary(string folder) => _folder = Path.GetFullPath(folder);

    public static PresetLibrary Load(string? folder = null)
    {
        folder ??= AppPaths.Presets;
        var initialize = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        var library = new PresetLibrary(folder);
        if (initialize)
            foreach (var preset in SamplePresets.Create()) library.Save(preset);
        else library.Reload();
        return library;
    }

    public Preset? Get(Guid id) => _presets.FirstOrDefault(preset => preset.Id == id);

    public void Save(Preset preset)
    {
        var current = Get(preset.Id);
        if (current is not null && Equivalent(current, preset)) return;
        preset = preset with { UpdatedAt = DateTimeOffset.UtcNow };
        if (_folder is not null)
        {
            var known = _files.TryGetValue(preset.Id, out var existing);
            var path = known ? existing.Path : Path.Combine(_folder, preset.Id + ".yaml");
            var content = PresetFile.Serialize(preset);
            using var fileLock = Lock();
            if (ProjectStorage.Revision(path) != (known ? existing.Revision : ""))
                throw new IOException($"Preset changed outside FilmBatch; reload it before saving: {path}");
            AppPaths.WriteAllTextAtomic(path, content);
            _files[preset.Id] = (path, ProjectStorage.Revision(path));
        }
        var index = _presets.FindIndex(item => item.Id == preset.Id);
        if (index >= 0) _presets[index] = preset;
        else _presets.Add(preset);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(Guid id)
    {
        if (Get(id) is null) return false;
        if (_folder is not null && _files.TryGetValue(id, out var existing))
        {
            using var fileLock = Lock();
            if (ProjectStorage.Revision(existing.Path) != existing.Revision)
                throw new IOException($"Preset changed outside FilmBatch; reload it before deleting: {existing.Path}");
            File.Delete(existing.Path);
            _files.Remove(id);
        }
        _presets.RemoveAll(preset => preset.Id == id);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Reload()
    {
        if (_folder is null || _disposed) return;
        var loaded = new Dictionary<Guid, Preset>();
        var files = new Dictionary<Guid, (string Path, string Revision)>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_folder).Where(path =>
            Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            try
            {
                var revision = ProjectStorage.Revision(path);
                var preset = PresetFile.Read(path);
                if (ProjectStorage.Revision(path) != revision) throw new IOException("Preset changed while reading.");
                if (Get(preset.Id) is { } old)
                {
                    if (Equivalent(old, preset)) preset = old;
                    else if (preset.UpdatedAt <= old.UpdatedAt) preset = preset with { UpdatedAt = DateTimeOffset.UtcNow };
                }
                if (!loaded.TryAdd(preset.Id, preset)) throw new InvalidDataException($"Duplicate preset id: {preset.Id}");
                files.Add(preset.Id, (path, revision));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                or InvalidDataException or YamlException or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                // A broken external edit must not discard or overwrite the last usable preset.
                foreach (var (id, existing) in _files.Where(pair => pair.Value.Path == path))
                    if (Get(id) is { } previous && loaded.TryAdd(id, previous)) files.Add(id, existing);
            }
        }
        LoadErrors = errors;
        var changed = loaded.Count != _presets.Count || loaded.Values.Any(preset => Get(preset.Id) is not { } old || !Equivalent(old, preset));
        _files.Clear();
        foreach (var (id, file) in files) _files.Add(id, file);
        if (changed)
        {
            _presets.Clear();
            _presets.AddRange(loaded.Values);
            IsExternalChange = true;
            try { Changed?.Invoke(this, EventArgs.Empty); }
            finally { IsExternalChange = false; }
        }
        foreach (var error in errors) Error?.Invoke(error);
    }

    /// <summary>Dispatch reloads onto the same thread that owns the library and UI.</summary>
    public void Watch(Action<Action> post)
    {
        if (_folder is null || _watcher is not null || _disposed) return;
        _reloadTimer = new Timer(_ => post(() =>
        {
            if (_disposed) return;
            try { Reload(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error?.Invoke(ex.Message); }
        }), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(_folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void Queue(object? sender, FileSystemEventArgs args)
        {
            if (_disposed) return;
            static bool IsPreset(string path) => Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase);
            if (IsPreset(args.FullPath) || args is RenamedEventArgs renamed && IsPreset(renamed.OldFullPath))
                _reloadTimer?.Change(150, Timeout.Infinite);
        }
        _watcher.Changed += Queue;
        _watcher.Created += Queue;
        _watcher.Deleted += Queue;
        _watcher.Renamed += Queue;
        _watcher.Error += (_, _) => _reloadTimer?.Change(150, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public IReadOnlyList<Preset> Pinned => _presets.Where(preset => preset.Pinned).Take(9).ToList();

    private FileStream Lock() => new(Path.Combine(_folder!, ".lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
    private static bool Equivalent(Preset first, Preset second) => first.Name == second.Name && first.Category == second.Category
        && first.ColorLabel == second.ColorLabel && first.Pinned == second.Pinned && first.PipelineVersion == second.PipelineVersion
        && first.Settings.Equals(second.Settings);

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
        _reloadTimer = null;
    }
}
