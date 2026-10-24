using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FilmBatch.Engine.Downloads;

/// <summary>
/// 使用者明確觸發之下載的共用元件（AI 降噪模型、Lensfun 鏡頭資料庫）：單一 HttpClient、
/// 有上限的串流下載與進度回報、串流 SHA-256、同目錄暫存檔＋原子搬移。
/// 只負責傳輸與落地；內容驗證（固定雜湊、git blob 雜湊、解析）由各 store 決定。
/// </summary>
internal static class Downloader
{
    private const int BufferSize = 1 << 17;

    /// <summary>共用連線；GitHub API 要求 User-Agent。不自動解壓，位元組數與雜湊才會等於伺服器上的檔案。</summary>
    internal static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None })
        { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FilmBatch", "1.0"));
        return http;
    }

    /// <summary>
    /// 串流下載 <paramref name="uri"/> 寫入 <paramref name="target"/>；超過 <paramref name="maxBytes"/> 立即中止。
    /// 每寫入一段就以累計位元組數呼叫 <paramref name="onBytes"/>。傳回總位元組數與伺服器宣告長度（可能為 null）。
    /// </summary>
    internal static async Task<long> DownloadAsync(Uri uri, Stream target, long maxBytes,
        Action<long, long?>? onBytes, CancellationToken cancellationToken, string? accept = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var declared = response.Content.Headers.ContentLength;
        if (declared > maxBytes)
            throw new InvalidDataException($"Download exceeds the size limit: {declared} > {maxBytes} ({uri}).");
        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long read = 0;
            int n;
            while ((n = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read > maxBytes)
                    throw new InvalidDataException($"Download exceeds the size limit: > {maxBytes} ({uri}).");
                await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                onBytes?.Invoke(read, declared);
            }
            return read;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>下載到新檔（<see cref="FileMode.CreateNew"/>），以 <paramref name="expectedBytes"/> 作為缺少 Content-Length 時的進度分母。</summary>
    internal static async Task<long> DownloadToFileAsync(Uri uri, string path, long expectedBytes, long maxBytes,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using var disk = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var read = await DownloadAsync(uri, disk, maxBytes, (done, declared) =>
        {
            var total = declared ?? expectedBytes;
            if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
        }, cancellationToken).ConfigureAwait(false);
        await disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        return read;
    }

    /// <summary>下載小型文件（API 回應）到記憶體，最多 <paramref name="maxBytes"/>。</summary>
    internal static async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken,
        string? accept = null)
    {
        using var memory = new MemoryStream();
        await DownloadAsync(uri, memory, maxBytes, null, cancellationToken, accept).ConfigureAwait(false);
        return memory.ToArray();
    }

    /// <summary>串流計算檔案 SHA-256（十六進位小寫）。</summary>
    internal static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>寫入同目錄暫存檔，驗證完成後原子搬到 <paramref name="finalPath"/>；失敗或取消時清除暫存。</summary>
    internal static async Task WriteAtomicAsync(string finalPath, Func<FileStream, Task> write, CancellationToken cancellationToken)
    {
        var temp = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, finalPath, overwrite: true);
        }
        finally { TryDelete(temp); }
    }

    /// <summary>盡力刪除檔案或資料夾；清理失敗不蓋過原本的例外。</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }
}
