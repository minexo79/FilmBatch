// 使用者 DCP 資產儲存（DCP-1 §7.3、計畫 D2）：原始位元組即身分，
// key 為 dcp-<SHA-256 64 小寫 hex>；顯示名稱、機型相容與 embed policy 判斷都不參與 key。
// Stage 把來源複製進明確 root 內的有界 owned 暫存，對同一份位元組嚴格解析與 hash；
// Publish 只發布那些暫存位元組——不重新序列化、不重讀呼叫端 source、不覆寫未驗證的既有目標。
// 取消／失敗只刪本次暫存，已發布的合法內容保留。本類別不決定機型相容或 policy 用途。
using System.Security.Cryptography;
using System.Text;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>使用者 DCP 儲存／驗證失敗的分類；全部是可見失敗，不退回 <c>auto</c>。</summary>
public enum CameraProfileFault
{
    /// <summary>key 不是 <c>dcp-</c>＋64 小寫 hex。</summary>
    InvalidKey,

    /// <summary>key 合法但檔案不存在。</summary>
    Missing,

    /// <summary>檔案位元組與 key 的 SHA-256 不符。</summary>
    HashMismatch,

    /// <summary>發布時目標已存在且內容未驗證；不覆寫。</summary>
    UnverifiedTarget,

    /// <summary>來源或既有檔案超過 <see cref="DcpProfile.MaxSourceBytes"/>。</summary>
    TooLarge,
}

/// <summary><see cref="CameraProfileStore"/> 的可見失敗；<see cref="Fault"/> 供呼叫端分類。</summary>
public sealed class CameraProfileException : Exception
{
    public CameraProfileFault Fault { get; }

    public CameraProfileException(CameraProfileFault fault, string message) : base(message) => Fault = fault;

    public CameraProfileException(CameraProfileFault fault, string message, Exception inner)
        : base(message, inner) => Fault = fault;
}

/// <summary><see cref="CameraProfileStore.List"/> 的一項：key、有界顯示名稱與內容可驗證狀態。</summary>
public sealed record CameraProfileItem(string Key, string Name, bool Valid);

/// <summary>
/// <see cref="CameraProfileStore.Stage"/> 回傳的已驗證草稿：擁有明確 root 內、獨占鎖定的暫存與
/// 同一份位元組。未來 UI 的 descriptor／confirm 流程直接讀 <see cref="Profile"/> 與 <see cref="Name"/>；
/// <see cref="Publish"/> 只發布暫存位元組，呼叫端 source 之後被改動也不影響內容。
/// </summary>
public sealed class CameraProfileDraft : IDisposable
{
    private readonly byte[] _bytes;
    private FileStream? _temp;
    private string? _tempPath;
    private bool _disposed;
    private bool _published;

    internal CameraProfileDraft(string root, byte[] bytes, string key, string name, DcpProfile profile,
        FileStream temp, string tempPath)
    {
        Root = root;
        _bytes = bytes;
        Key = key;
        Name = name;
        Profile = profile;
        _temp = temp;
        _tempPath = tempPath;
    }

    /// <summary>Stage 進入時捕捉的完整路徑；發布不再解析呼叫端的 root 字串。</summary>
    public string Root { get; }

    /// <summary><c>dcp-</c>＋暫存位元組的 SHA-256 小寫 hex。</summary>
    public string Key { get; }

    /// <summary>有界顯示名稱（見 <see cref="CameraProfileStore.MaxDisplayNameLength"/>；不參與 key）。</summary>
    public string Name { get; }

    /// <summary>暫存位元組解析出的不可變 profile；model／policy／copyright 的原樣資訊。</summary>
    public DcpProfile Profile { get; }

    /// <summary>
    /// 原子發布暫存位元組到 <c>{key}.dcp</c>（同目錄寫入後 no-clobber rename）。
    /// 內容相同的既有目標不重寫；未驗證目標丟 <see cref="CameraProfileFault.UnverifiedTarget"/> 且不覆寫。
    /// 取消或失敗只刪本次暫存；成功後再次呼叫回傳同一 key。
    /// </summary>
    public string Publish(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published) return Key;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(Root, Key + ".dcp");
            if (!File.Exists(target) || !CameraProfileStore.ContentMatches(target, Key, cancellationToken))
                WriteNew(target, cancellationToken);
            WriteNameIfAbsent();
            _published = true;
            return Key;
        }
        finally { ReleaseTemp(); }
    }

    private void WriteNew(string target, CancellationToken cancellationToken)
    {
        var publishing = Path.Combine(Root, ".dcp-publishing-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(publishing, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (var offset = 0; offset < _bytes.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(64 * 1024, _bytes.Length - offset);
                    stream.Write(_bytes, offset, count);
                    offset += count;
                }
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(publishing, target); }
            catch (IOException) when (File.Exists(target))
            {
                // 目標被搶先建立：內容與 key 相符視為成功（不重寫），未驗證則不覆寫。
                if (!CameraProfileStore.ContentMatches(target, Key, cancellationToken))
                    throw CameraProfileStore.UnverifiedTarget(Key);
            }
        }
        finally { CameraProfileStore.TryDelete(publishing); }
    }

    /// <summary><c>.name</c> 僅在檔名不存在時寫入；驗證過的重複匯入不覆寫既有名稱。</summary>
    private void WriteNameIfAbsent()
    {
        var path = Path.Combine(Root, Key + ".name");
        if (File.Exists(path)) return;
        var pending = path + ".writing-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(Name);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(pending, path); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { CameraProfileStore.TryDelete(pending); }
    }

    private void ReleaseTemp()
    {
        _temp?.Dispose();
        _temp = null;
        if (_tempPath is { } path)
        {
            _tempPath = null;
            CameraProfileStore.TryDelete(path);
        }
    }

    /// <summary>只刪本暫存；已發布的資產不受影響。可重複呼叫。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseTemp();
    }
}

/// <summary>
/// 使用者 DCP 的儲存與解析，檔案在 caller 提供的明確 root 內：
/// <c>{key}.dcp</c>（原始位元組）與同 stem <c>.name</c>（有界顯示名稱）。
/// 無全域快取；<see cref="Resolve"/> 每次重新讀檔、驗 SHA-256、解析並回傳新的不可變 snapshot，
/// 同尺寸與時間戳的替換位元組無法取得舊結果。
/// </summary>
public static class CameraProfileStore
{
    /// <summary>顯示名稱上限（字元）；與既有使用者資產名稱同級。</summary>
    public const int MaxDisplayNameLength = 48;

    private const int CopyBufferSize = 64 * 1024;
    private const int NameBytesCap = (MaxDisplayNameLength + 1) * 4; // 49 個字元、每字元至多 4 bytes
    private const string FallbackName = "DCP";

    /// <summary>
    /// 非互動匯入：對同一份有界暫存位元組驗證後立即發布，回傳 <c>dcp-&lt;64hex&gt;</c>。
    /// 與 <see cref="Stage"/> 共用實作；取消或失敗只刪本次暫存。
    /// </summary>
    public static string Import(string root, Stream source, string displayName,
        CancellationToken cancellationToken = default)
    {
        using var draft = Stage(root, source, displayName, cancellationToken);
        return draft.Publish(cancellationToken);
    }

    /// <summary>
    /// 把來源複製進明確 root 內的有界 owned 暫存（上限 <see cref="DcpProfile.MaxSourceBytes"/>；
    /// 不支援 seek 的串流也在讀到上限＋1 時停止），對同一份位元組呼叫
    /// <see cref="DcpProfile.Read"/> 並計算 SHA-256；不發布任何檔案。
    /// 取消或失敗只刪本次暫存。
    /// </summary>
    public static CameraProfileDraft Stage(string root, Stream source, string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(displayName);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        var tempPath = Path.Combine(fullRoot, ".dcp-staging-" + Guid.NewGuid().ToString("N"));
        FileStream? temp = null;
        try
        {
            temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                CopyBufferSize, FileOptions.DeleteOnClose);
            CopyBounded(source, temp, cancellationToken);
            var bytes = new byte[(int)temp.Length];
            temp.Position = 0;
            temp.ReadExactly(bytes);
            var profile = DcpProfile.Read(bytes, cancellationToken);
            var key = CameraProfileKey.Prefix + Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new CameraProfileDraft(fullRoot, bytes, key, BoundDisplayName(displayName), profile, temp, tempPath);
        }
        catch
        {
            temp?.Dispose();
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// 讀檔、驗 <paramref name="key"/> 的 SHA-256 後解析成新的不可變 snapshot。
    /// 非法 key、缺檔與雜湊不符都是 <see cref="CameraProfileException"/>；解析失敗維持
    /// <see cref="RawException"/> 的原樣訊息。
    /// </summary>
    public static DcpProfile Resolve(string root, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!CameraProfileKey.IsUserKey(key))
            throw new CameraProfileException(CameraProfileFault.InvalidKey, CoreText.T(
                $"相機描述檔 key 無效：{key}。", $"Invalid camera profile key: {key}."));
        var path = Path.Combine(Path.GetFullPath(root), key + ".dcp");
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CameraProfileException(CameraProfileFault.Missing, CoreText.T(
                $"找不到相機描述檔 {key}。", $"Camera profile {key} is missing."), ex);
        }
        using (stream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length > DcpProfile.MaxSourceBytes) throw TooLarge();
            var bytes = new byte[(int)stream.Length];
            var position = 0;
            while (position < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = stream.Read(bytes, position, Math.Min(CopyBufferSize, bytes.Length - position));
                if (n == 0) throw new EndOfStreamException("Camera profile changed while reading.");
                position += n;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1) throw TooLarge();
            var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!key.AsSpan(CameraProfileKey.Prefix.Length).SequenceEqual(hex))
                throw new CameraProfileException(CameraProfileFault.HashMismatch, CoreText.T(
                    $"相機描述檔 {key} 的內容與雜湊不符。", $"Camera profile {key} content does not match its hash."));
            return DcpProfile.Read(bytes, cancellationToken);
        }
    }

    /// <summary>
    /// 列出 <c>dcp-*.dcp</c>：key、有界名稱與可驗證狀態（大小上限、DCP 檔頭、SHA-256 相符）。
    /// 不做 profile 解析或整庫數學；單一損毀項只影響自己，目錄不存在時回傳空清單。
    /// </summary>
    public static IReadOnlyList<CameraProfileItem> List(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) return [];
        var items = new List<CameraProfileItem>();
        foreach (var path in Directory.EnumerateFiles(fullRoot))
        {
            var file = Path.GetFileName(path);
            if (!file.EndsWith(".dcp", StringComparison.Ordinal)) continue;
            var key = file[..^4];
            if (!CameraProfileKey.IsUserKey(key)) continue;
            items.Add(new CameraProfileItem(key,
                ReadBoundedName(Path.Combine(fullRoot, key + ".name")), IsListedValid(path, key)));
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return items;
    }

    /// <summary>檔案位元組的 SHA-256 與 <paramref name="key"/> 相符（大小上限先驗；IO 失敗視為不符）。</summary>
    internal static bool ContentMatches(string path, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > DcpProfile.MaxSourceBytes) return false;
            return key.AsSpan(CameraProfileKey.Prefix.Length).SequenceEqual(HashHex(path, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CameraProfileException)
        {
            return false;
        }
    }

    internal static CameraProfileException UnverifiedTarget(string key) => new(CameraProfileFault.UnverifiedTarget,
        CoreText.T($"相機描述檔 {key} 的既有檔案內容未驗證，不覆寫。",
            $"The existing file for camera profile {key} is unverified and was not replaced."));

    internal static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static CameraProfileException TooLarge() => new(CameraProfileFault.TooLarge, CoreText.T(
        $"相機描述檔超過 {DcpProfile.MaxSourceBytes} 位元組上限。",
        $"Camera profile exceeds the {DcpProfile.MaxSourceBytes} byte limit."));

    /// <summary>讀到上限即停；第 64 MiB＋1 位元組立刻失敗，不寫滿暫存也不讀完整來源。</summary>
    private static void CopyBounded(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        long declared = -1;
        if (source.CanSeek)
        {
            try { declared = source.Length - source.Position; }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException) { }
        }
        if (declared > DcpProfile.MaxSourceBytes) throw TooLarge();
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = DcpProfile.MaxSourceBytes - total;
            if (limit == 0)
            {
                // 已在邊界：再多一個位元組即超限。
                if (source.Read(buffer, 0, 1) > 0) throw TooLarge();
                return;
            }
            var chunk = (int)Math.Min(CopyBufferSize, limit);
            var read = source.Read(buffer, 0, chunk);
            if (read <= 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            total += read;
        }
    }

    /// <summary>清單用的廉價驗證：大小上限內、DCP 檔頭、SHA-256 與 key 相符；不解析 profile。</summary>
    private static bool IsListedValid(string path, string key)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > DcpProfile.MaxSourceBytes) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[8];
            stream.ReadExactly(header);
            if (!HeaderOk(header)) return false;
            stream.Position = 0;
            return key.AsSpan(CameraProfileKey.Prefix.Length).SequenceEqual(HashHex(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CameraProfileException)
        {
            return false;
        }
    }

    /// <summary>II/MM ＋ DCP magic 0x4352 的 4 位元組檔頭。</summary>
    private static bool HeaderOk(ReadOnlySpan<byte> header) =>
        (header[0] == 'I' && header[1] == 'I' && header[2] == 0x52 && header[3] == 0x43) ||
        (header[0] == 'M' && header[1] == 'M' && header[2] == 0x43 && header[3] == 0x52);

    private static string HashHex(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashHex(stream, cancellationToken);
    }

    private static string HashHex(Stream stream, CancellationToken cancellationToken = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, DcpProfile.MaxSourceBytes - total + 1));
            if (read == 0) return Convert.ToHexStringLower(sha.GetHashAndReset());
            total += read;
            if (total > DcpProfile.MaxSourceBytes) throw TooLarge();
            sha.AppendData(buffer, 0, read);
        }
    }

    /// <summary>有界讀取顯示名稱：前 49 個字元寬度的位元組，之後一律截斷，不載入整個檔案。</summary>
    private static string ReadBoundedName(string path)
    {
        try
        {
            if (!File.Exists(path)) return FallbackName;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[NameBytesCap];
            var read = stream.Read(buffer, 0, buffer.Length);
            return BoundDisplayName(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FallbackName;
        }
    }

    /// <summary>顯示名稱邊界：換行變空白、去首尾、截到 <see cref="MaxDisplayNameLength"/>；空名回預設字。</summary>
    private static string BoundDisplayName(string name)
    {
        var flat = name.ReplaceLineEndings(" ").Trim();
        if (flat.Length == 0) return FallbackName;
        return flat.Length <= MaxDisplayNameLength ? flat : flat[..MaxDisplayNameLength];
    }
}
