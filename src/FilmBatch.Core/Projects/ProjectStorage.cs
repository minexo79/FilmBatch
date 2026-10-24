using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;

namespace FilmBatch.Core.Projects;

internal sealed class ProjectDocument
{
    public required int PipelineVersion { get; init; }
    public required Guid DefaultPresetId { get; init; }
    public required Dictionary<Guid, PresetDocument> PresetSnapshots { get; init; }
    public required Dictionary<string, PhotoDocument> Photos { get; init; }
    public Dictionary<Guid, DateTimeOffset> KeptPresetUpdates { get; init; } = [];
}

internal sealed class PhotoDocument
{
    public Guid? PresetId { get; init; }
    public required Settings Overrides { get; init; }
    public ForegroundMaskDocument? ForegroundMask { get; init; }

    /// <summary>星級評分；未評分（0）不寫出，舊專案沒有此欄位即讀成 0。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Rating { get; init; }
}

internal sealed class PresetDocument
{
    public required string Name { get; init; }
    public string Category { get; init; } = "Uncategorized";
    public string ColorLabel { get; init; } = "#7A7A7A";
    public int PipelineVersion { get; init; } = 1;
    public bool Pinned { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required Settings Settings { get; init; }

    public Preset ToPreset(Guid id)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || PipelineVersion != 1 || Settings is null)
            throw new InvalidDataException("Invalid preset snapshot.");
        return new Preset { Id = id, Name = Name, Category = Category, ColorLabel = ColorLabel,
            PipelineVersion = PipelineVersion, Pinned = Pinned, UpdatedAt = UpdatedAt, Settings = Settings };
    }

    public static PresetDocument From(Preset preset) => new()
    {
        Name = preset.Name, Category = preset.Category, ColorLabel = preset.ColorLabel,
        PipelineVersion = preset.PipelineVersion, Pinned = preset.Pinned, UpdatedAt = preset.UpdatedAt, Settings = preset.Settings,
    };
}

internal static class ProjectStorage
{
    /// <summary>
    /// 寫入的專案格式版本。3：加入逐張照片的去背覆蓋率；2：加入裁切／遮罩／曲線等新 key。
    /// 讀取時接受 1 ~ 目前版本；舊版不得默默覆寫含去背結果的專案。
    /// </summary>
    public const int CurrentVersion = 3;

    public static string FilePath(string folder) => Path.Combine(AppPaths.ProjectFolder(folder), "project.json");

    public static ProjectDocument Read(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize(stream, ProjectJson.Default.ProjectDocument)
            ?? throw new InvalidDataException("Empty project document.");
        if (document.PipelineVersion > CurrentVersion)
            throw new InvalidDataException(CoreText.T(
                $"此專案由較新版本的 FilmBatch 建立（格式版本 {document.PipelineVersion}），請更新 FilmBatch 後再開啟。",
                $"This project was created by a newer version of FilmBatch (format version {document.PipelineVersion}). Update FilmBatch to open it."));
        if (document.PipelineVersion < 1) throw new InvalidDataException($"Unsupported project pipeline: {document.PipelineVersion}");
        if (document.Photos is null || document.PresetSnapshots is null || document.KeptPresetUpdates is null)
            throw new InvalidDataException("Incomplete project document.");
        foreach (var (name, photo) in document.Photos)
        {
            if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0
                || Path.IsPathRooted(name) || photo?.Overrides is null)
                throw new InvalidDataException($"Invalid project photo: {name}");
        }
        return document;
    }

    public static string Revision(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Write(string path, ProjectDocument document, string expectedRevision)
    {
        var content = JsonSerializer.Serialize(document, ProjectJson.Default.ProjectDocument);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Cooperating app instances cannot both pass the revision check before replacing the document.
        using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        if (Revision(path) != expectedRevision)
            throw new IOException("The project was changed by another process. Reopen it before saving; the existing file was not overwritten.");
        AppPaths.WriteAllTextAtomic(path, content);
        return Revision(path);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ProjectDocument))]
internal sealed partial class ProjectJson : JsonSerializerContext;
