using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;

namespace FilmBatch.Core.Projects;

/// <summary>
/// UI 對專案的所有修改入口：每個操作都包成命令送進 <see cref="CommandStack"/>，因此都能復原。
/// </summary>
public sealed class ProjectService
{
    private readonly Project project;
    private readonly PresetLibrary library;
    private readonly CommandStack stack;

    public ProjectService(Project project, PresetLibrary library, CommandStack stack)
    {
        this.project = project;
        this.library = library;
        this.stack = stack;
        project.AttachLibrary(library);
    }

    public Project Project => project;
    public PresetLibrary Library => library;
    public CommandStack Stack => stack;

    /// <summary>重開專案保留快照；只有明確同步才採用修改後的全域預設。</summary>
    public Preset? PresetFor(Guid? id)
    {
        if (id is not { } g) return null;
        var snapshot = project.PresetSnapshots.GetValueOrDefault(g);
        return project.PreserveSnapshots ? snapshot ?? library.Get(g) : library.Get(g) ?? snapshot;
    }

    /// <summary>系統預設 ⊕ 預設。</summary>
    public Settings BaseFor(Guid? presetId) => PresetFor(presetId)?.Resolved ?? ModuleCatalog.Defaults;

    /// <summary>照片的最終參數（三層合併）。</summary>
    public Settings SettingsFor(string fileName)
    {
        var e = project[fileName];
        return PhotoOperations.Resolve(BaseFor(e.PresetId), e.Overrides);
    }

    private bool Apply(string description, IEnumerable<string> fileNames, Func<PhotoEntry, PhotoEntry> op, string? mergeKey = null)
    {
        var after = fileNames.Distinct(StringComparer.Ordinal).ToDictionary(n => n, n => op(project[n]), StringComparer.Ordinal);
        if (after.Count == 0) return false;
        var cmd = new PhotoEditCommand(project, description, after, mergeKey);
        if (cmd.IsNoOp) return false;
        stack.Execute(cmd);
        return true;
    }

    /// <summary>修改參數（多選時同時套用到所有照片）。<paramref name="mergeKey"/> 相同的連續修改合併為一筆復原紀錄。</summary>
    public bool SetValue(IReadOnlyCollection<string> fileNames, string key, ParamValue value, string? mergeKey = null)
    {
        var label = ModuleCatalog.Param(key)?.Label ?? key;
        if (ModuleCatalog.Param(key)?.Kind == ParamKind.Path && Path.IsPathRooted(value.AsText))
            value = AppPaths.ToProjectRelative(project.Folder, value.AsText);
        return Apply($"調整 {label}", fileNames, e => PhotoOperations.SetValue(e, key, value, BaseFor(e.PresetId)), mergeKey);
    }

    /// <summary>
    /// 一筆可復原的多欄位操作，例如套用一組色相修正預設、裁切工具一次寫入所有 <c>crop.*</c>。
    /// 每個值依 schema 夾限（與 <see cref="SetValue"/> 相同）；與基準相同的值移除覆寫。
    /// </summary>
    public bool SetValues(IReadOnlyCollection<string> fileNames, Settings values, string description, string? mergeKey = null)
    {
        var coerced = new Settings(values.Select(kv => KeyValuePair.Create(kv.Key,
            (ModuleCatalog.Param(kv.Key) ?? throw new ArgumentException($"未知的參數：{kv.Key}")).Coerce(kv.Value))));
        return Apply(description, fileNames, entry => entry with
        {
            Overrides = PhotoOperations.Clean(entry.Overrides.Overlay(coerced), BaseFor(entry.PresetId)),
        }, mergeKey);
    }

    /// <summary>套用或移除焦點照片的去背遮罩；與其他編輯共用原子保存與復原。</summary>
    public bool SetForegroundMask(string fileName, ForegroundMask? mask) =>
        Apply(mask is null ? CoreText.T("還原背景", "Restore background") : CoreText.T("套用去背", "Apply background removal"),
            [fileName], entry => entry with { ForegroundMask = mask });

    /// <summary>一次套用多張的去背遮罩；一筆命令、一次復原紀錄。保存失敗不套用也不佔用復原。</summary>
    public bool SetForegroundMasks(IReadOnlyDictionary<string, ForegroundMask> masks)
    {
        ArgumentNullException.ThrowIfNull(masks);
        if (masks.Count == 0) return false;
        return Apply(CoreText.T("套用批次去背", "Apply batch background removal"),
            masks.Keys, entry => masks.TryGetValue(entry.FileName, out var mask) ? entry with { ForegroundMask = mask } : entry);
    }

    /// <summary>設定星級評分（多選時一併套用）；一筆復原紀錄，不影響參數覆寫。</summary>
    public bool SetRating(IReadOnlyCollection<string> fileNames, int rating)
    {
        rating = Math.Clamp(rating, 0, PhotoEntry.MaxRating);
        return Apply(rating == 0 ? CoreText.T("清除評分", "Clear rating") : CoreText.T($"評分 {rating} 星", $"Rate {rating} stars"),
            fileNames, entry => entry with { Rating = rating });
    }

    public bool ResetParam(IReadOnlyCollection<string> fileNames, string key) =>
        Apply($"重設 {ModuleCatalog.Param(key)?.Label ?? key}", fileNames, e => PhotoOperations.ResetParam(e, key));

    /// <summary>一筆可復原的多欄位重設（例如 RAW 白平衡的模式＋色溫＋色調一起回到拍攝值）。</summary>
    public bool ResetParams(IReadOnlyCollection<string> fileNames, IReadOnlyCollection<string> keys, string description) =>
        Apply(description, fileNames, e => e with { Overrides = e.Overrides.WithoutRange(keys) });

    public bool ResetModule(IReadOnlyCollection<string> fileNames, string moduleKey) =>
        Apply($"重設 {ModuleCatalog.Module(moduleKey).Label}", fileNames, e => PhotoOperations.ResetModule(e, moduleKey));

    public bool ResetAll(IReadOnlyCollection<string> fileNames) =>
        Apply("重設此張", fileNames, PhotoOperations.ResetAll);

    /// <summary>
    /// 回到原圖：清除覆寫與遮罩，並把預設改回「Neutral (No Effect)」（預設庫中已刪除時改為未指定）。
    /// 評分保留；一筆復原紀錄。
    /// </summary>
    public bool ResetToOriginal(IReadOnlyCollection<string> fileNames)
    {
        Guid? neutral = library.Get(SamplePresets.Neutral) is null ? null : SamplePresets.Neutral;
        if (neutral is { } id) SnapshotPreset(id);
        return Apply("重設此張", fileNames, e => PhotoOperations.ResetAll(e) with { PresetId = neutral });
    }

    public bool AssignPreset(IReadOnlyCollection<string> fileNames, Guid presetId, bool keepOverrides)
    {
        SnapshotPreset(presetId);
        var name = PresetFor(presetId)?.Name ?? "?";
        return Apply($"指定預設「{name}」", fileNames,
            e => PhotoOperations.AssignPreset(e, presetId, keepOverrides, BaseFor(presetId)));
    }

    public SettingsClipboard Copy(string fileName)
    {
        var e = project[fileName];
        return new SettingsClipboard(fileName, e.PresetId, e.Overrides, SettingsFor(fileName));
    }

    public bool Paste(SettingsClipboard clip, IReadOnlyCollection<string> fileNames, PasteOptions options)
    {
        if (options.Mode != PasteMode.Selective && clip.PresetId is { } id) SnapshotPreset(id);
        var label = options.Mode switch
        {
            PasteMode.Full => "貼上設定",
            PasteMode.PresetOnly => "只貼預設",
            _ => "選擇性貼上",
        };
        return Apply(label, fileNames, e => PhotoOperations.Paste(clip, e, options, BaseFor));
    }

    /// <summary>
    /// 以此張建立新預設：目前最終參數存成新預設，並讓這張改指定為新預設。
    /// <paramref name="modules"/> 只存勾選的模組（null = 全部）；沒存進預設的模組留在這張的覆寫，最終參數不變。
    /// </summary>
    public Preset CreatePresetFromPhoto(string fileName, string name, string category, string colorLabel,
        IReadOnlySet<string>? modules = null)
    {
        var resolved = SettingsFor(fileName);
        var saved = modules is null ? resolved : resolved.Where((k, _) => modules.Contains(ModuleSchema.ModuleOf(k)));
        var preset = new Preset
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = category,
            ColorLabel = colorLabel,
            Settings = PhotoOperations.Clean(saved, ModuleCatalog.Defaults),
        };
        var after = project[fileName] with
        {
            PresetId = preset.Id,
            Overrides = modules is null ? Settings.Empty : PhotoOperations.Clean(resolved, preset.Resolved),
        };
        stack.Execute(new CompositeCommand($"建立預設「{name}」",
        [
            new DelegateCommand("新增預設", () => library.Save(preset), () => library.Delete(preset.Id)),
            new PhotoEditCommand(project, "改用新預設", new Dictionary<string, PhotoEntry> { [fileName] = after }),
        ]));
        return preset;
    }

    /// <summary>
    /// 把此張的調整更新回預設（會影響所有使用該預設的照片；呼叫端需先確認）。
    /// 其他照片若有與新值相同的覆寫，會一併清理。此操作無法復原，因此清空復原堆疊。
    /// </summary>
    public void UpdatePresetFromPhoto(string fileName)
    {
        var e = project[fileName];
        if (PresetFor(e.PresetId) is not { } preset) return;
        var updated = preset with
        {
            Settings = PhotoOperations.Clean(SettingsFor(fileName), ModuleCatalog.Defaults),
            UpdatedAt = DateTimeOffset.Now,
        };
        library.Save(updated);
        project.PresetSnapshots[updated.Id] = updated;

        var cleaned = project.FileNames
            .Where(n => project[n].PresetId == updated.Id)
            .ToDictionary(n => n,
                n => project[n] with { Overrides = PhotoOperations.Clean(project[n].Overrides, updated.Resolved) },
                StringComparer.Ordinal);
        cleaned[fileName] = e with { Overrides = Settings.Empty };
        project.Replace(cleaned);
        stack.Clear();
    }

    /// <summary>使用某預設的照片數量（刪除預設前提示用）。</summary>
    public int CountUsing(Guid presetId) => project.FileNames.Count(n => project[n].PresetId == presetId);

    private void SnapshotPreset(Guid id)
    {
        if (library.Get(id) is { } p) project.PresetSnapshots[id] = p;
    }
}
