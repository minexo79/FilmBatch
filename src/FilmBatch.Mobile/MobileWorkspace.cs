using Avalonia.Platform.Storage;
using FilmBatch.Core.Film;
using FilmBatch.Raw.Color;
using FilmBatch.Core.Projects;

namespace FilmBatch.Mobile;

/// <summary>Stores imported copies, editable projects and exports inside the application container.</summary>
public sealed class MobileWorkspace
{
    /// <summary>Initializes a new instance of the <see cref="MobileWorkspace"/> class.</summary>
    public MobileWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Exports);
    }

    /// <summary>Gets the owned application document directory.</summary>
    public string Root { get; }
    /// <summary>Gets the directory containing editable photo projects.</summary>
    public string Projects => Path.Combine(Root, "Projects");
    /// <summary>Gets the directory containing completed outputs.</summary>
    public string Exports => Path.Combine(Root, "Exports");
    /// <summary>Gets the private location for selected Photos representations.</summary>
    public string Incoming => Path.Combine(Root, "Incoming");

    /// <summary>Creates an independent editable project without touching original photographs.</summary>
    public string CreateProject()
    {
        var folder = Path.Combine(Projects, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Lists owned projects without decoding their photographs.</summary>
    public IReadOnlyList<MobileProject> ListProjects() => Directory.EnumerateDirectories(Projects)
        .Select(folder => new MobileProject(Path.GetFileName(folder), folder,
            Directory.EnumerateFiles(folder).Count(IsPhoto)))
        .OrderByDescending(project => project.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Checks whether a remembered folder is an existing, direct owned project.</summary>
    public bool IsProject(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var path = Path.GetFullPath(folder);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        return string.Equals(parent, Path.GetFullPath(Projects), StringComparison.Ordinal)
            && Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null;
    }

    /// <summary>Copies selected storage streams into an owned project using collision-free names.</summary>
    public async Task<IReadOnlyList<string>> ImportAsync(IEnumerable<IStorageFile> files, string destination,
        CancellationToken cancellationToken)
    {
        RequireOwned(destination);
        var imported = new List<string>();
        foreach (var file in files)
        {
            using (file)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPhoto(file.Name)) throw new InvalidDataException(MobileText.T("不支援的照片格式：", "Unsupported photograph: ") + file.Name);
                await using var input = await file.OpenReadAsync();
                imported.Add(await CopyAsync(input, file.Name, destination, cancellationToken));
            }
        }
        return imported;
    }

    /// <summary>Copies one selected auxiliary file without retaining an external file-system path.</summary>
    public async Task<string> ImportAssetAsync(IStorageFile file, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Root, "Assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using (file)
        {
            await using var input = await file.OpenReadAsync();
            return await CopyAssetAsync(input, file.Name, folder, cancellationToken);
        }
    }

    /// <summary>Imports a selected folder into an owned project, including its bounded subfolder tree.</summary>
    public async Task<string> ImportFolderAsync(IStorageFolder source, CancellationToken cancellationToken)
    {
        var destination = CreateProject();
        using (source) await CopyFolderAsync(source, destination, depth: 0, cancellationToken);
        return destination;
    }

    /// <summary>Copies a selected auxiliary folder into owned asset storage outside the project list.</summary>
    public async Task<string> ImportAssetFolderAsync(IStorageFolder source, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Root, "Assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using (source) await CopyFolderAsync(source, folder, depth: 0, cancellationToken);
        return folder;
    }

    private static async Task CopyFolderAsync(IStorageFolder source, string destination, int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 16) throw new InvalidDataException("The selected directory tree is too deep.");
        await foreach (var item in source.GetItemsAsync().WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateName(item.Name);
            using (item)
            {
                if (item is IStorageFile file)
                {
                    await using var input = await file.OpenReadAsync();
                    await CopyAsync(input, item.Name, destination, cancellationToken);
                }
                else if (item is IStorageFolder folder)
                {
                    var child = Path.Combine(destination, item.Name);
                    Directory.CreateDirectory(child);
                    await CopyFolderAsync(folder, child, depth + 1, cancellationToken);
                }
            }
        }
    }

    /// <summary>Accepts Photos representations already copied into this application's Incoming directory.</summary>
    public async Task<IReadOnlyList<string>> ImportIncomingAsync(IEnumerable<string> paths, string destination,
        CancellationToken cancellationToken)
    {
        RequireOwned(destination);
        var imported = new List<string>();
        var incomingRoot = Path.GetFullPath(Incoming) + Path.DirectorySeparatorChar;
        foreach (var source in paths)
        {
            var full = Path.GetFullPath(source);
            RequireOwned(full);
            if (!full.StartsWith(incomingRoot, StringComparison.Ordinal))
                throw new InvalidDataException("Photos representation is not an owned incoming file.");
            if (!IsPhoto(full)) throw new InvalidDataException("Unsupported Photos representation: " + Path.GetFileName(full));
            await using (var input = File.OpenRead(full))
                imported.Add(await CopyAsync(input, Path.GetFileName(full), destination, cancellationToken));
            File.Delete(full);
        }
        return imported;
    }

    /// <summary>Copies completed outputs to a new user-selected folder without overwriting existing files.</summary>
    public async Task SaveOutputsAsync(IStorageFolder destination, IReadOnlyList<string> files,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using (destination)
        {
            var name = "FilmBatch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            using var batch = await destination.CreateFolderAsync(name)
                ?? throw new IOException("The selected provider could not create an output folder.");
            var folders = new Dictionary<string, IStorageFolder>(StringComparer.Ordinal);
            var written = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                for (var i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireOwned(files[i]);
                    var folder = await BatchFolderAsync(batch, files[i], folders);
                    ValidateName(Path.GetFileName(files[i]));
                    var fileName = await UniqueBatchNameAsync(folder, Path.GetFileName(files[i]), files[i], written);
                    using var output = await folder.CreateFileAsync(fileName)
                        ?? throw new IOException("The selected provider could not create " + fileName);
                    try
                    {
                        await using var input = File.OpenRead(files[i]);
                        await using var stream = await output.OpenWriteAsync();
                        await input.CopyToAsync(stream, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }
                    catch
                    {
                        await output.DeleteAsync();
                        throw;
                    }
                    progress?.Report(i + 1);
                }
            }
            finally
            {
                foreach (var folder in folders.Values) folder.Dispose();
            }
        }
    }

    private async Task<IStorageFolder> BatchFolderAsync(IStorageFolder batch, string file, Dictionary<string, IStorageFolder> cache)
    {
        var exportsRoot = Path.GetFullPath(Exports) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(exportsRoot, StringComparison.Ordinal))
            throw new InvalidDataException("Only owned export outputs can be saved to Files.");
        var relative = Path.GetDirectoryName(Path.GetRelativePath(Exports, full)) ?? string.Empty;
        if (relative.Length == 0 || relative == "." || Path.IsPathRooted(relative)) return batch;
        var current = batch;
        var prefix = string.Empty;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            ValidateName(part);
            prefix = prefix.Length == 0 ? part : prefix + "/" + part;
            if (!cache.TryGetValue(prefix, out var child))
            {
                child = await current.CreateFolderAsync(part)
                    ?? throw new IOException("The selected provider could not create an output folder.");
                cache[prefix] = child;
            }
            current = child;
        }
        return current;
    }

    private async Task<string> UniqueBatchNameAsync(IStorageFolder folder, string fileName, string source, HashSet<string> written)
    {
        // CreateFileAsync truncates/overwrites provider-side, so probe with provider-native lookups and
        // suffix before creation. Same-folder duplicates therefore stay distinct on every real provider.
        var directory = Path.GetDirectoryName(Path.GetRelativePath(Exports, Path.GetFullPath(source))) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var index = 2; !written.Add(directory + "\0" + candidate) || await ExistsAsync(folder, candidate); index++)
            candidate = stem + "_" + index + extension;
        return candidate;
    }

    private static async Task<bool> ExistsAsync(IStorageFolder folder, string name)
    {
        // Both real providers return null when nothing matches (BclStorageFolder checks File/Directory.Exists;
        // IOSStorageFolder returns null when NSFileManager reports missing). Either probe returning non-null
        // means a same-name file or folder already occupies this batch folder, so suffix before creation.
        var collision = await folder.GetFileAsync(name);
        using (collision)
        {
            if (collision is not null) return true;
        }
        var directory = await folder.GetFolderAsync(name);
        using (directory)
        {
            return directory is not null;
        }
    }

    /// <summary>Resolves a new filename in the owned exports directory without overwriting an earlier result.</summary>
    public string NewExportPath(string fileName)
    {
        ValidateName(fileName);
        return UniquePath(Exports, fileName);
    }

    /// <summary>Gets whether a filename can be scanned by the shared project model.</summary>
    public static bool IsPhoto(string path) => Project.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void RequireOwned(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("The path is outside the mobile workspace.");
        var relative = Path.GetRelativePath(Root, full);
        var current = Root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) throw new InvalidDataException("Symbolic links are not accepted in the mobile workspace.");
        }
    }

    internal static Task<string> CopyAssetAsync(Stream source, string name, string destination, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(name);
        long? maximum = extension.Equals(".clf", StringComparison.OrdinalIgnoreCase) ? ClfFile.MaxInputBytes
            : extension.Equals(".fblook", StringComparison.OrdinalIgnoreCase) ? ColorTransformFile.MaxFileBytes
            : extension.Equals(".dcp", StringComparison.OrdinalIgnoreCase) ? DcpProfile.MaxSourceBytes
            : null;
        return CopyAsync(source, name, destination, cancellationToken, maximum);
    }

    private static async Task<string> CopyAsync(Stream source, string name, string destination, CancellationToken cancellationToken, long? maxBytes = null)
    {
        ValidateName(name);
        Directory.CreateDirectory(destination);
        var temporary = Path.Combine(destination, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (maxBytes is not { } limit)
                    await source.CopyToAsync(output, cancellationToken);
                else
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    while (true)
                    {
                        var count = (int)Math.Min(buffer.Length, limit - total + 1);
                        var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
                        if (read == 0) break;
                        total += read;
                        if (total > limit) throw new InvalidDataException($"Asset exceeds the {limit}-byte limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = UniquePath(destination, name);
            File.Move(temporary, result, overwrite: false);
            return result;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string UniquePath(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = Path.Combine(folder, name);
        for (var index = 2; File.Exists(candidate) || Directory.Exists(candidate); index++)
            candidate = Path.Combine(folder, stem + "_" + index + extension);
        return candidate;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != Path.GetFileName(name)
            || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The selected provider returned an invalid filename.");
    }
}

/// <summary>Describes an editable photo project in the application container.</summary>
public sealed record MobileProject(string Name, string Folder, int PhotoCount)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Name} · {PhotoCount}";
}
