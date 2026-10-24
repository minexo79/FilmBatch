using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmBatch.Core;
using FilmBatch.Engine.Downloads;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>已安裝的 Lensfun 資料庫版本：上游 commit、該 commit 的時間、XML 檔數與下載時間。</summary>
public sealed record LensDatabaseInfo(string Commit, DateTimeOffset CommitDate, int FileCount, DateTimeOffset DownloadedAt)
{
    /// <summary>短 commit（顯示用）。</summary>
    public string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;
}

/// <summary>
/// 使用者明確觸發的 Lensfun 資料庫下載者：只取上游儲存庫的 <c>data/db</c> XML，放在
/// <c>AppPaths.Root/lensfun/db-&lt;commit&gt;</c>，再以原子寫入的 <c>installed.json</c> 指向它。
/// 每個版本一個資料夾，路徑隨版本改變——RAW 來源快取鍵因此自然失效，舊版本可安全移除。
/// <para>
/// 授權邊界：Lensfun 的函式庫是 LGPL-3、<c>lensfun-update-data</c> 等應用程式是 GPL-3，資料庫本身是
/// CC BY-SA 3.0。本類別是獨立實作（直接讀 GitHub 公開的檔案清單與原始檔），不連結、不移植 Lensfun
/// 任何程式碼，也不使用其更新工具或封存格式；資料庫不隨程式散布，只由使用者下載到自己的資料夾，
/// 並在介面上標示來源與授權。
/// </para>
/// 完整性：每個檔案以 GitHub 清單提供的 git blob SHA-1 與位元組數驗證；整批再以讀取端相同的
/// 上限與解析規則驗證通過後才切換版本，失敗或取消時保留原本已安裝的版本。
/// </summary>
public sealed partial class LensDatabaseStore
{
    public const string SourceUrl = "https://github.com/lensfun/lensfun/tree/master/data/db";
    public const string License = "CC BY-SA 3.0";
    public const string Attribution = "Lensfun lens database © the Lensfun contributors";

    private const string Api = "https://api.github.com/repos/lensfun/lensfun/";
    private const string RawRoot = "https://raw.githubusercontent.com/lensfun/lensfun/";
    private const string PointerFile = "installed.json";
    private const string VersionPrefix = "db-";
    private const long MaxApiBytes = 4 << 20;
    private const int Parallelism = 4;

    private static readonly Lock CacheLock = new();
    private static (string Pointer, long Ticks, string? Directory) _resolved;

    public LensDatabaseStore(string? directory = null)
    {
        RootPath = directory is { Length: > 0 }
            ? Path.GetFullPath(directory)
            : Path.Combine(AppPaths.Root, "lensfun");
    }

    /// <summary>所有下載版本與指標檔所在的資料夾。</summary>
    public string RootPath { get; }

    private string PointerPath => Path.Combine(RootPath, PointerFile);

    /// <summary>目前安裝的版本；沒有或指標檔損壞時為 null。</summary>
    public LensDatabaseInfo? Installed => ReadPointer()?.Info;

    /// <summary>目前安裝版本的 XML 資料夾；沒有安裝時為 null。</summary>
    public string? InstalledDirectory => ReadPointer()?.Directory;

    /// <summary>
    /// 預設位置已安裝資料庫的資料夾（<see cref="RawLoadOptions.FromSettings"/> 在偏好設定與環境變數
    /// 都未指定時使用）。以指標檔修改時間快取，每次 RAW 讀取只做一次 stat。
    /// </summary>
    public static string? DefaultInstalledDirectory()
    {
        var store = new LensDatabaseStore();
        var pointer = store.PointerPath;
        long ticks;
        try { ticks = File.Exists(pointer) ? File.GetLastWriteTimeUtc(pointer).Ticks : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ticks = 0; }
        lock (CacheLock)
            if (_resolved.Pointer == pointer && _resolved.Ticks == ticks) return _resolved.Directory;
        var directory = ticks == 0 ? null : store.InstalledDirectory;
        lock (CacheLock) _resolved = (pointer, ticks, directory);
        return directory;
    }

    /// <summary>
    /// 下載最新的 <c>data/db</c>：查詢最後一個修改 data/db 的 commit、列出該 commit 的檔案、
    /// 逐檔下載並驗證，整批解析通過後原子切換。已是最新版本時不重新下載。
    /// </summary>
    public async Task<LensDatabaseInfo> InstallAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (commit, commitDate) = await LatestCommitAsync(cancellationToken).ConfigureAwait(false);
        if (ReadPointer() is { } current && current.Info.Commit == commit)
        {
            progress?.Report(1);
            return current.Info;
        }
        var entries = await ListAsync(commit, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(RootPath);
        var staging = Path.Combine(RootPath, ".staging-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(RootPath, VersionPrefix + commit[..12]);
        try
        {
            Directory.CreateDirectory(staging);
            var total = entries.Sum(e => e.Size);
            long done = 0;
            await Parallel.ForEachAsync(entries, new ParallelOptions
            {
                MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken,
            }, async (entry, ct) =>
            {
                var uri = new Uri(RawRoot + commit + "/data/db/" + Uri.EscapeDataString(entry.Name));
                using var memory = new MemoryStream((int)entry.Size);
                long last = 0;
                await Downloader.DownloadAsync(uri, memory, entry.Size, (read, _) =>
                {
                    var delta = read - last;
                    last = read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)Interlocked.Add(ref done, delta) / total));
                }, ct).ConfigureAwait(false);
                var bytes = memory.ToArray();
                if (bytes.Length != entry.Size)
                    throw new InvalidDataException(CoreText.T(
                        $"Lensfun 檔案大小不符：{entry.Name}（{bytes.Length} != {entry.Size}）。",
                        $"Lensfun file size mismatch: {entry.Name} ({bytes.Length} != {entry.Size})."));
                var blob = GitBlobSha1(bytes);
                if (!blob.Equals(entry.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(CoreText.T(
                        $"Lensfun 檔案雜湊不符：{entry.Name}（{blob} != {entry.Sha}）。",
                        $"Lensfun file hash mismatch: {entry.Name} ({blob} != {entry.Sha})."));
                await File.WriteAllBytesAsync(Path.Combine(staging, entry.Name), bytes, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var (cameras, profiles) = LensProfiles.Validate(staging);
            if (cameras == 0 || profiles == 0)
                throw new InvalidDataException(CoreText.T("下載的 Lensfun 資料庫沒有任何相機或鏡頭。",
                    "The downloaded Lensfun database contains no cameras or lenses."));
            cancellationToken.ThrowIfCancellationRequested();
            Downloader.TryDelete(target);
            Directory.Move(staging, target);
            var info = new LensDatabaseInfo(commit, commitDate, entries.Count, DateTimeOffset.UtcNow);
            AppPaths.WriteAllTextAtomic(PointerPath, WritePointer(info, Path.GetFileName(target)));
            RemoveOtherVersions(Path.GetFileName(target));
            progress?.Report(1);
            return info;
        }
        finally { Downloader.TryDelete(staging); }
    }

    private sealed record Entry(string Name, string Sha, long Size);

    private static async Task<(string Commit, DateTimeOffset Date)> LatestCommitAsync(CancellationToken cancellationToken)
    {
        var json = await Downloader.GetBytesAsync(new Uri(Api + "commits?path=data/db&per_page=1"), MaxApiBytes,
            cancellationToken, "application/vnd.github+json").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new InvalidDataException("Lensfun commit listing is empty.");
        var first = doc.RootElement[0];
        var sha = first.GetProperty("sha").GetString() ?? "";
        if (!CommitPattern().IsMatch(sha)) throw new InvalidDataException("Lensfun commit id is invalid.");
        var date = first.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        return (sha.ToLowerInvariant(), date);
    }

    /// <summary>列出該 commit 的 data/db；只接受單層、安全檔名的 .xml，並套用讀取端相同的數量與大小上限。</summary>
    private static async Task<List<Entry>> ListAsync(string commit, CancellationToken cancellationToken)
    {
        var json = await Downloader.GetBytesAsync(new Uri(Api + "contents/data/db?ref=" + commit), MaxApiBytes,
            cancellationToken, "application/vnd.github+json").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Lensfun file listing is invalid.");
        var entries = new List<Entry>();
        long total = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "file") continue;
            var name = item.GetProperty("name").GetString() ?? "";
            if (!XmlNamePattern().IsMatch(name)) continue;
            var sha = item.GetProperty("sha").GetString() ?? "";
            var size = item.GetProperty("size").GetInt64();
            if (!CommitPattern().IsMatch(sha) || size <= 0 || size > LensProfiles.MaxFileBytes)
                throw new InvalidDataException($"Lensfun file entry is invalid: {name}.");
            total += size;
            entries.Add(new(name, sha.ToLowerInvariant(), size));
        }
        if (entries.Count == 0) throw new InvalidDataException("Lensfun data/db has no XML files.");
        if (entries.Count > LensProfiles.MaxFiles || total > LensProfiles.MaxDatabaseBytes)
            throw new InvalidDataException("Lensfun data/db exceeds the input size limit.");
        return entries;
    }

    /// <summary>git 物件雜湊：SHA-1("blob &lt;長度&gt;\0" + 內容)，即 GitHub 清單中的 <c>sha</c>。</summary>
    internal static string GitBlobSha1(ReadOnlySpan<byte> content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.ASCII.GetBytes($"blob {content.Length}\0"));
        sha.AppendData(content);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private (LensDatabaseInfo Info, string Directory)? ReadPointer()
    {
        try
        {
            if (!File.Exists(PointerPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(PointerPath));
            var root = doc.RootElement;
            var folder = root.GetProperty("folder").GetString() ?? "";
            // 指標只能指向本資料夾下的版本資料夾，不能被改成任意路徑。
            if (!VersionFolderPattern().IsMatch(folder)) return null;
            var directory = Path.Combine(RootPath, folder);
            if (!Directory.Exists(directory)) return null;
            var info = new LensDatabaseInfo(root.GetProperty("commit").GetString() ?? "",
                root.GetProperty("commit_date").GetDateTimeOffset(), root.GetProperty("files").GetInt32(),
                root.GetProperty("downloaded_at").GetDateTimeOffset());
            return (info, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string WritePointer(LensDatabaseInfo info, string folder)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("folder", folder);
            w.WriteString("commit", info.Commit);
            w.WriteString("commit_date", info.CommitDate);
            w.WriteNumber("files", info.FileCount);
            w.WriteString("downloaded_at", info.DownloadedAt);
            w.WriteString("source", SourceUrl);
            w.WriteString("license", License);
            w.WriteString("attribution", Attribution);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>切換後移除舊版本與殘留暫存；檔案被占用時留待下次。</summary>
    private void RemoveOtherVersions(string keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            var name = Path.GetFileName(dir);
            if (name != keep && (VersionFolderPattern().IsMatch(name) || name.StartsWith(".staging-", StringComparison.Ordinal)))
                Downloader.TryDelete(dir);
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.xml$")]
    private static partial Regex XmlNamePattern();

    [GeneratedRegex("^db-[0-9a-f]{12}$")]
    private static partial Regex VersionFolderPattern();
}
