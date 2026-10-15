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
}

internal sealed class PresetDocument
{
    public required string Name { get; init; }
    public string Category { get; init; } = "未分類";
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
    public static string FilePath(string folder) => Path.Combine(AppPaths.ProjectFolder(folder), "project.json");

    public static ProjectDocument Read(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize(stream, ProjectJson.Default.ProjectDocument)
            ?? throw new InvalidDataException("Empty project document.");
        if (document.PipelineVersion != 1) throw new InvalidDataException($"Unsupported project pipeline: {document.PipelineVersion}");
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
