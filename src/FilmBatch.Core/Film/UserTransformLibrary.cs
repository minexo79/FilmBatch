using System.Buffers;
using System.Security.Cryptography;

namespace FilmBatch.Core.Film;

/// <summary>
/// 使用者匯入的 CLF／FBLOOK 資產（CT-2 §3.3）。檔案為呼叫者明示 <c>root</c> 下的
/// <c>look-&lt;64hex&gt;.fblook</c> 與同 stem 的 <c>.name</c>；root 於每次操作開始時捕捉，
/// 不讀全域 <see cref="AppPaths"/>，可與既有 user-*.fblut 共存。
/// </summary>
public static class UserTransformLibrary
{
    /// <summary>內容定址 key 前置詞；副檔名固定 <c>.fblook</c>。</summary>
    public const string KeyPrefix = "look-";
    private const string Extension = ".fblook";
    private const int MaxNameChars = 48;

    // 至多一個 (root,key) strong entry；entry 只持有已驗證的不可變 snapshot，
    // 不保留第二份原始 bytes。
    private static readonly object CacheLock = new();
    private static string? _cacheRoot;
    private static string? _cacheKey;
    private static ColorTransformAsset? _cacheAsset;

    /// <summary>key 格式：<c>look-</c>＋64 個小寫 hex；不檢查檔案是否存在。</summary>
    public static bool IsLookKey(string key)
    {
        if (key is null || key.Length != KeyPrefix.Length + 64 ||
            !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return false;
        for (var i = KeyPrefix.Length; i < key.Length; i++)
            if (key[i] is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>
    /// 把呼叫者提供的來源複製到本次操作獨有、有界、唯讀的暫存，封存其 SHA-256，
    /// 並就地解析一次（CLF 驗 chain；FBLOOK 驗 header＋chain 並取出內含宣告）。
    /// 之後 UI 可檢視 <see cref="StagedColorImport.Transform"/> 不需重開來源；
    /// 發布失敗或取消只刪本次暫存。caller 保有來源 stream 所有權。
    /// </summary>
    public static StagedColorImport Stage(
        string root, Stream source, bool isFblook, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        root = Path.GetFullPath(root);
        var maxBytes = isFblook ? ColorTransformFile.MaxFileBytes : ClfFile.MaxInputBytes;
        if (source.CanSeek)
        {
            long declared = -1;
            try { declared = source.Length - source.Position; }
            catch (NotSupportedException) { }
            if (declared > maxBytes)
                throw new InvalidDataException($"來源超過 {maxBytes} bytes 上限。");
        }

        Directory.CreateDirectory(root);
        var tempPath = Path.Combine(root, ".ct-stage-" + Guid.NewGuid().ToString("N") + ".tmp");
        byte[] digest;
        try
        {
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var write = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    long total = 0;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        total += read;
                        if (total > maxBytes)
                            throw new InvalidDataException($"來源超過 {maxBytes} bytes 上限。");
                        hasher.AppendData(buffer, 0, read);
                        write.Write(buffer, 0, read);
                    }
                    write.Flush(flushToDisk: true);
                }
                digest = hasher.GetHashAndReset();
            }
            try { File.SetAttributes(tempPath, FileAttributes.ReadOnly); }
            catch (IOException) { }          // 唯讀旗標是強化措施，不取代封存 digest
            catch (UnauthorizedAccessException) { }

            // 解析封存好的同一份 bytes：來源之後再變動也不影響結果。
            ClfImportOptions? embedded;
            ColorTransform transform;
            using (var read = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var snapshot = ColorTransformFile.ReadBounded(read, maxBytes, cancellationToken))
            {
                var parsedDigest = SHA256.HashData(snapshot.GetBuffer().AsSpan(0, (int)snapshot.Length));
                if (!digest.AsSpan().SequenceEqual(parsedDigest))
                    throw new InvalidDataException("封存內容在解析前被竄改。");
                if (isFblook)
                {
                    var asset = ColorTransformFile.ReadSnapshot(snapshot, cancellationToken);
                    embedded = asset.Options;
                    transform = asset.Transform;
                }
                else
                {
                    embedded = null;
                    transform = ClfFile.Read(snapshot, cancellationToken);
                }
            }
            return new StagedColorImport(root, tempPath, digest, isFblook, embedded, transform);
        }
        catch
        {
            DeleteTemp(tempPath);
            throw;
        }
    }

    /// <summary>
    /// 單次完成 <see cref="Stage"/>＋<see cref="StagedColorImport.Publish"/>：
    /// <paramref name="staged"/> 仍由本函式封存；<c>.clf</c> 必須給宣告，
    /// <c>.fblook</c> 必須給 null 或與內含宣告一致的值。
    /// </summary>
    public static string Import(
        string root, Stream staged, bool isFblook, ClfImportOptions? options, string displayName,
        CancellationToken cancellationToken = default)
    {
        using var stage = Stage(root, staged, isFblook, cancellationToken);
        return stage.Publish(options, displayName, cancellationToken);
    }

    /// <summary>
    /// 完整驗證 <c>root/key.fblook</c> 並回傳不可變 snapshot：每次呼叫都驗目前檔案的
    /// SHA-256 等於 key（缺檔 <see cref="FileNotFoundException"/>；hash 不合、envelope 或
    /// CLF 非法 <see cref="InvalidDataException"/>；key 格式不對 <see cref="ArgumentException"/>）。
    /// 驗證通過後可重用同一 (root,key) 已解析的不可變 chain；至多一個 strong entry。
    /// </summary>
    public static ColorTransformAsset Resolve(string root, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!IsLookKey(key))
            throw new ArgumentException($"不是合法的 look key：{key}", nameof(key));
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.GetFullPath(root);
        var path = Path.Combine(fullRoot, key + Extension);
        ColorTransformAsset? cached;
        lock (CacheLock)
            cached = _cacheRoot == fullRoot && _cacheKey == key ? _cacheAsset : null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (cached is not null)
        {
            if (!HashFile(file, cancellationToken).AsSpan().SequenceEqual(Convert.FromHexString(key[KeyPrefix.Length..])))
                throw new InvalidDataException($"transform 資產內容與 key 不符：{key}");
            return cached;
        }
        var asset = ColorTransformFile.Read(file, cancellationToken);
        if (!string.Equals(asset.Key, key, StringComparison.Ordinal))
            throw new InvalidDataException($"transform 資產內容與 key 不符：{key}");
        lock (CacheLock)
        {
            _cacheRoot = fullRoot;
            _cacheKey = key;
            _cacheAsset = asset;
        }
        return asset;
    }

    /// <summary>
    /// 只讀每個 <c>look-*.fblook</c> 的 16-byte header：key、bounded name（無 .name 時以 key 為名）、
    /// 可解析的宣告；header 無效的項目標記 <see cref="TransformLibraryItem.IsValid"/> = false，
    /// 不解析 chain、不驗 hash，也不讓壞檔影響其他項目。完整驗證只在 <see cref="Resolve"/>。
    /// </summary>
    public static IReadOnlyList<TransformLibraryItem> List(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var dir = Path.GetFullPath(root);
        var items = new List<TransformLibraryItem>();
        if (!Directory.Exists(dir))
            return items;
        foreach (var file in Directory.EnumerateFiles(dir, KeyPrefix + "*" + Extension))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            ClfImportOptions? options = null;
            var nameFile = Path.Combine(dir, key + ".name");
            string name;
            try
            {
                if (!File.Exists(nameFile)) name = key;
                else
                {
                    using var reader = new StreamReader(nameFile);
                    var chars = new char[1024];
                    var count = reader.ReadBlock(chars, 0, chars.Length);
                    name = SanitizeName(new string(chars, 0, count), key);
                }
            }
            catch (IOException) { name = key; }
            catch (UnauthorizedAccessException) { name = key; }
            if (IsLookKey(key))
            {
                try
                {
                    var header = new byte[ColorTransformFile.HeaderBytes];
                    using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (read.Read(header, 0, header.Length) == header.Length &&
                        ColorTransformFile.TryParseHeader(header, out var parsed, out _))
                        options = parsed;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            items.Add(new TransformLibraryItem(key, name, options));
        }
        items.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return items;
    }

    /// <summary>清空唯一 (root,key) entry；測試或 root 切換後可呼叫。</summary>
    public static void Reset()
    {
        lock (CacheLock)
        {
            _cacheRoot = null;
            _cacheKey = null;
            _cacheAsset = null;
        }
    }

    /// <summary>發布暫存內容（由 <see cref="StagedColorImport.Publish"/> 呼叫）。</summary>
    internal static string Publish(
        StagedColorImport staged, ClfImportOptions? options, string displayName,
        CancellationToken cancellationToken)
    {
        var root = staged.Root;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(root);

        // 決定本份 bytes 的宣告；宣告必須合法，fblook 沿用內含值。
        ClfImportOptions declared;
        if (staged.IsFblook)
        {
            declared = staged.EmbeddedOptions!.Value;
            if (options is { } supplied && !supplied.Equals(declared))
                throw new InvalidDataException("宣告與 FBLOOK 內含 metadata 不符。");
        }
        else
        {
            if (options is not { } supplied)
                throw new ArgumentException(".clf 匯入必須提供 stage／input／output 宣告。", nameof(options));
            if (!supplied.IsValid)
                throw new InvalidDataException("transform 宣告不合法。");
            declared = supplied;
        }

        var stagedTemp = staged.TempPath ?? throw new ObjectDisposedException(nameof(StagedColorImport));

        var publishTemp = Path.Combine(root, ".ct-pub-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // 單一讀取封存檔 → 單一寫入發布暫存，對實際寫出的 bytes 計算兩個 hash：
            // sealHash 驗封存未被竄改；envelopeHash 即內容 key。
            using var sealHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var envelopeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = new FileStream(stagedTemp, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(publishTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var length = input.Length;
                var maxBytes = staged.IsFblook ? ColorTransformFile.MaxFileBytes : ClfFile.MaxInputBytes;
                if (length > maxBytes) throw new InvalidDataException("封存內容超過上限。");
                long copied = 0;
                if (!staged.IsFblook)
                {
                    Span<byte> header = stackalloc byte[ColorTransformFile.HeaderBytes];
                    ColorTransformFile.WriteHeader(header, in declared, (uint)length);
                    envelopeHash.AppendData(header);
                    output.Write(header);
                }
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    copied += read;
                    if (copied > maxBytes) throw new InvalidDataException("封存內容超過上限。");
                    sealHash.AppendData(buffer, 0, read);
                    envelopeHash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                if (copied != length) throw new InvalidDataException("封存內容長度已變更。");
                cancellationToken.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }
            if (!staged.Digest.AsSpan().SequenceEqual(sealHash.GetHashAndReset()))
                throw new InvalidDataException("封存內容在發布前被竄改，拒絕發布分歧的 bytes。");
            var key = KeyPrefix + Convert.ToHexString(envelopeHash.GetHashAndReset()).ToLowerInvariant();
            var dest = Path.Combine(root, key + Extension);

            // 原子發布：不覆寫未驗證的既有檔案。目標已存在時只有內容 hash 相同才算成功。
            if (File.Exists(dest))
            {
                using var existingFile = File.OpenRead(dest);
                var existing = HashFile(existingFile, cancellationToken);
                if (!existing.AsSpan().SequenceEqual(Convert.FromHexString(key[KeyPrefix.Length..])))
                    throw new InvalidDataException($"目標已有未驗證內容，不覆寫：{key}");
                // 同內容的既有檔案：發布成功（冪等），仍更新 bounded name。
            }
            else
            {
                try
                {
                    File.Move(publishTemp, dest, overwrite: false);
                    publishTemp = null;
                }
                catch (IOException) when (File.Exists(dest))
                {
                    using var existingFile = File.OpenRead(dest);
                    var existing = HashFile(existingFile, cancellationToken);
                    if (!existing.AsSpan().SequenceEqual(Convert.FromHexString(key[KeyPrefix.Length..])))
                        throw new InvalidDataException($"目標已有未驗證內容，不覆寫：{key}");
                }
            }

            var name = SanitizeName(displayName, staged.Transform.Name);
            AppPaths.WriteAllTextAtomic(Path.Combine(root, key + ".name"), name);
            return key;
        }
        finally
        {
            if (publishTemp is not null) DeleteTemp(publishTemp);
        }
    }

    private static byte[] HashFile(Stream source, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int bufferSize = 81920;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, bufferSize);
                if (read == 0) break;
                total += read;
                if (total > ColorTransformFile.MaxFileBytes)
                    throw new InvalidDataException("資產超過大小上限。");
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string SanitizeName(string name, string? fallback)
    {
        var trimmed = (name ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (trimmed.Length == 0)
        {
            trimmed = (fallback ?? string.Empty).ReplaceLineEndings(" ").Trim();
            if (trimmed.Length == 0) trimmed = "CLF";
        }
        return trimmed.Length <= MaxNameChars ? trimmed : trimmed[..MaxNameChars];
    }

    internal static void DeleteTemp(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// 一次匯入獨有的封存暫存：唯讀的來源複本、其 SHA-256 與解析出的不可變 chain。
/// 宣告尚未提供時即可檢視 <see cref="Transform"/>（含 descriptor）與
/// <see cref="EmbeddedOptions"/>（fblook 的內含宣告；clf 為 null）；只有同一批封存
/// bytes 可經 <see cref="Publish"/> 發布。<see cref="Dispose"/> 刪除本次暫存。
/// </summary>
public sealed class StagedColorImport : IDisposable
{
    internal StagedColorImport(
        string root, string tempPath, byte[] digest, bool isFblook,
        ClfImportOptions? embeddedOptions, ColorTransform transform)
    {
        Root = root;
        TempPath = tempPath;
        Digest = digest;
        IsFblook = isFblook;
        EmbeddedOptions = embeddedOptions;
        Transform = transform;
    }

    /// <summary>封存時捕捉的資產目錄；發布不重新讀取全域 root。</summary>
    public string Root { get; }
    /// <summary>封存暫存路徑；<see cref="Publish"/> 失敗或 <see cref="Dispose"/> 後為 null。</summary>
    internal string? TempPath { get; private set; }
    internal byte[] Digest { get; }

    /// <summary>來源是完整 <c>.fblook</c>（true）還是裸 <c>.clf</c>（false）。</summary>
    public bool IsFblook { get; }

    /// <summary>fblook 內含的 stage／input／output 宣告；clf 為 null，由呼叫者提供。</summary>
    public ClfImportOptions? EmbeddedOptions { get; }

    /// <summary>封存 bytes 解析出的不可變 chain；UI 展示 descriptor 不需重開來源。</summary>
    public ColorTransform Transform { get; }

    /// <summary>
    /// 驗證宣告後原子發布：把封存 bytes（clf 補上 header）寫入本次發布暫存，對實際寫出
    /// 的 bytes 同時驗封存 digest 與計算內容 key，再以不覆寫未驗證檔案的方式完成發布。
    /// 發布後可保留已驗證的共享資產；取消或失敗只刪本次暫存。
    /// </summary>
    public string Publish(ClfImportOptions? options, string displayName,
        CancellationToken cancellationToken = default)
    {
        var tempPath = TempPath;
        ObjectDisposedException.ThrowIf(tempPath is null, this);
        try
        {
            return UserTransformLibrary.Publish(this, options, displayName, cancellationToken);
        }
        catch
        {
            UserTransformLibrary.DeleteTemp(tempPath);
            TempPath = null;
            throw;
        }
    }

    /// <summary>關閉並刪除本次暫存；不影響已發布的資產。</summary>
    public void Dispose()
    {
        var tempPath = TempPath;
        if (tempPath is null) return;
        UserTransformLibrary.DeleteTemp(tempPath);
        TempPath = null;
    }
}

/// <summary>清單項目：key、bounded 顯示名稱與 header 可讀的宣告；header 無效時 <see cref="Options"/> 為 null。</summary>
public sealed record TransformLibraryItem(string Key, string Name, ClfImportOptions? Options)
{
    /// <summary>16-byte header 通過嚴格驗證；chain／hash 是否有效要等 <see cref="UserTransformLibrary.Resolve"/>。</summary>
    public bool IsValid => Options is not null;
}
