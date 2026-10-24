using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using FilmBatch.Core;
using FilmBatch.Engine.Downloads;

namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 本機 AI 降噪模型檔的安裝者。預設目錄為 <c>AppPaths.Root/models/denoise</c>（每次
/// 建立 store 時重新取值，不捕捉靜態快照）。<see cref="IsInstalled"/> 只驗證檔案存在
/// 且大小符合固定值——不在每次刷新時對 100 MB 檔案做 SHA-256；真正的 hash 驗證留給
/// <see cref="DenoiseProcessor"/> 的 session 啟用（會偵測安裝後被替換的檔案）。
/// 下載只發生在明確的 <see cref="InstallAsync"/>；<see cref="ImportAsync"/> 完全不連線。
/// 傳輸、雜湊與原子寫入共用 <see cref="Downloader"/>（與 Lensfun 資料庫下載相同）。
/// </summary>
public sealed class DenoiseModelStore
{
    public DenoiseModelStore(string? directory = null)
    {
        DirectoryPath = directory is { Length: > 0 }
            ? Path.GetFullPath(directory)
            : Path.Combine(AppPaths.Root, "models", "denoise");
    }

    public string DirectoryPath { get; }

    /// <summary>已安裝 ONNX 的完整路徑。</summary>
    public string ModelPath(DenoiseModelSpec model) => Path.Combine(DirectoryPath, model.FileName);

    /// <summary>
    /// 快速安裝檢查：檔案存在且大小等於固定位元組數。故意不對每次 UI 刷新做全檔 SHA-256。
    /// </summary>
    public bool IsInstalled(DenoiseModelSpec model)
    {
        var path = ModelPath(model);
        if (!File.Exists(path)) return false;
        try { return new FileInfo(path).Length == model.OnnxBytes; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// 從固定 <see cref="DenoiseModelSpec.DownloadUri"/> 明確下載封存、串流驗證其
    /// SHA-256／位元組數，再從中只解出 <see cref="DenoiseModelSpec.OnnxEntry"/> 並驗證其
    /// SHA-256／位元組數後原子安放。封存只被串流讀一次；不會整包落地、不會解出其他 entry。
    /// </summary>
    public async Task InstallAsync(DenoiseModelSpec model, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var archiveTemp = Path.Combine(DirectoryPath, ".download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await Downloader.DownloadToFileAsync(model.DownloadUri, archiveTemp, model.ArchiveBytes,
                model.ArchiveBytes, progress, cancellationToken).ConfigureAwait(false);
            var info = new FileInfo(archiveTemp);
            if (info.Length != model.ArchiveBytes)
                throw new InvalidDataException(CoreText.T(
                    $"模型封存大小不符：{info.Length} != {model.ArchiveBytes}（{model.Name}）。",
                    $"Model archive size mismatch: {info.Length} != {model.ArchiveBytes} ({model.Name})."));
            var archiveHash = await Downloader.Sha256FileAsync(archiveTemp, cancellationToken).ConfigureAwait(false);
            if (!archiveHash.Equals(model.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(CoreText.T(
                    $"模型封存 SHA-256 不符：{archiveHash} != {model.ArchiveSha256}（{model.Name}）。",
                    $"Model archive SHA-256 mismatch: {archiveHash} != {model.ArchiveSha256} ({model.Name})."));
            // 只解出固定 entry；不允許任意 ZIP 路徑穿越。
            using (var zip = ZipFile.OpenRead(archiveTemp))
            {
                var entry = zip.GetEntry(model.OnnxEntry)
                    ?? throw new InvalidDataException(CoreText.T(
                        $"模型封存缺少預期 entry：{model.OnnxEntry}。",
                        $"Model archive is missing the expected entry: {model.OnnxEntry}."));
                await ExtractEntryAsync(model, entry, cancellationToken).ConfigureAwait(false);
            }
            File.Delete(archiveTemp);
        }
        finally { Downloader.TryDelete(archiveTemp); }
    }

    /// <summary>
    /// 從本機套件匯入。支援兩種來源：
    /// （1）官方 .dtmodel／ZIP 封存——驗證其固定 SHA-256／位元組數後解出固定 entry；
    /// （2）直接的 .onnx 檔——驗證其固定 SHA-256／位元組數後安放到目標名稱。
    /// 不接受任何其他 entry、不解出任意路徑；全部驗證通過才會落地。
    /// </summary>
    public async Task ImportAsync(DenoiseModelSpec model, string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException(CoreText.T($"找不到模型套件：{packagePath}", $"Model package not found: {packagePath}"), packagePath);
        Directory.CreateDirectory(DirectoryPath);
        var info = new FileInfo(packagePath);
        if (info.Length == model.ArchiveBytes)
        {
            var hash = await Downloader.Sha256FileAsync(packagePath, cancellationToken).ConfigureAwait(false);
            if (!hash.Equals(model.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(CoreText.T(
                    $"模型封存 SHA-256 不符：{hash} != {model.ArchiveSha256}（{model.Name}）。",
                    $"Model archive SHA-256 mismatch: {hash} != {model.ArchiveSha256} ({model.Name})."));
            using var zip = ZipFile.OpenRead(packagePath);
            var entry = zip.GetEntry(model.OnnxEntry)
                ?? throw new InvalidDataException(CoreText.T(
                    $"模型封存缺少預期 entry：{model.OnnxEntry}。",
                    $"Model archive is missing the expected entry: {model.OnnxEntry}."));
            await ExtractEntryAsync(model, entry, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (info.Length == model.OnnxBytes)
        {
            await Downloader.WriteAtomicAsync(ModelPath(model), async target =>
            {
                await using var source = File.OpenRead(packagePath);
                await CopyVerifiedAsync(model, source, target, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new InvalidDataException(CoreText.T(
            $"套件大小 {info.Length} 不符合 {model.Name} 的封存（{model.ArchiveBytes}）或 ONNX（{model.OnnxBytes}）固定大小。",
            $"Package size {info.Length} matches neither the archive ({model.ArchiveBytes}) nor the ONNX ({model.OnnxBytes}) size for {model.Name}."));
    }

    /// <summary>
    /// 從 ZIP entry 串流解出到暫存、驗證 ONNX SHA-256／位元組數，再原子搬到最終路徑。
    /// </summary>
    private Task ExtractEntryAsync(DenoiseModelSpec model, ZipArchiveEntry entry,
        CancellationToken cancellationToken) =>
        Downloader.WriteAtomicAsync(ModelPath(model), async target =>
        {
            await using var source = entry.Open();
            await CopyVerifiedAsync(model, source, target, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task CopyVerifiedAsync(DenoiseModelSpec model, Stream source, Stream target,
        CancellationToken cancellationToken)
    {
        long written = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 17);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                sha.AppendData(buffer, 0, count);
                written += count;
                if (written > model.OnnxBytes)
                    throw new InvalidDataException("ONNX data exceeds the pinned model size.");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (written != model.OnnxBytes)
            throw new InvalidDataException(CoreText.T(
                $"解出的 ONNX 大小不符：{written} != {model.OnnxBytes}。",
                $"Extracted ONNX size mismatch: {written} != {model.OnnxBytes}."));
        var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (!hash.Equals(model.OnnxSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(CoreText.T(
                $"解出的 ONNX SHA-256 不符：{hash} != {model.OnnxSha256}。",
                $"Extracted ONNX SHA-256 mismatch: {hash} != {model.OnnxSha256}."));
    }
}
