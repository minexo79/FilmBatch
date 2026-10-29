using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FilmBatch.Core.Film;

/// <summary>
/// 匯入時由使用者明確宣告的三個 metadata（CT-2 §3.1）：transform 的固定執行 stage，
/// 以及 chain 的輸入／輸出編碼宣告。不是自動辨識結果；只有 enum 已定義值合法。
/// </summary>
public readonly record struct ClfImportOptions(
    ColorTransformStage Stage,
    ColorTransformEncoding Input,
    ColorTransformEncoding Output)
{
    /// <summary>三個欄位都必須是已定義的 enum 值；未知／超界位元一律不合法。</summary>
    internal bool IsValid =>
        Stage is ColorTransformStage.WorkingLinear or ColorTransformStage.DisplayEncoded &&
        Input is ColorTransformEncoding.LinearSrgb or ColorTransformEncoding.Srgb
            or ColorTransformEncoding.ProPhotoGamma18 &&
        Output is ColorTransformEncoding.LinearSrgb or ColorTransformEncoding.Srgb
            or ColorTransformEncoding.ProPhotoGamma18;
}

/// <summary>
/// 已驗證的 <c>.fblook</c> 資產不可變 snapshot：內容定址 key、匯入宣告與解析過的 chain。
/// 原始 CLF bytes 留在檔案裡（內容定址已固定），不在每個 snapshot 複製 128 MiB。
/// </summary>
public sealed class ColorTransformAsset
{
    internal ColorTransformAsset(string key, ClfImportOptions options, ColorTransform transform)
    {
        Key = key;
        Options = options;
        Transform = transform;
    }

    /// <summary><c>look-</c>＋完整 envelope SHA-256 的 64 個小寫 hex；名稱不參與 hash。</summary>
    public string Key { get; }

    /// <summary>檔案內含的執行 stage 與輸入／輸出編碼宣告。</summary>
    public ClfImportOptions Options { get; }

    /// <summary>payload 解析出的不可變 chain（含 ProcessList metadata）。</summary>
    public ColorTransform Transform { get; }
}

/// <summary>
/// <c>.fblook</c> envelope（CT-2 §3.3）：16-byte header（ASCII <c>FBLOOK1\0</c>、stage、
/// input、output、reserved 0、little-endian uint32 CLF 長度）＋原樣 UTF-8 CLF bytes，
/// 不重新序列化、不量化、無 trailing bytes。讀寫都不依賴可 seek 的 stream。
/// </summary>
public static class ColorTransformFile
{
    /// <summary>envelope header 固定 16 bytes。</summary>
    public const int HeaderBytes = 16;

    /// <summary>完整檔案上限：16 bytes header＋128 MiB 原始 CLF。</summary>
    public const long MaxFileBytes = HeaderBytes + ClfFile.MaxInputBytes;

    private static ReadOnlySpan<byte> Magic => "FBLOOK1\0"u8;

    /// <summary>
    /// 寫出完整 envelope：先驗宣告與 CLF 內容，合法才把 header＋原始 bytes 寫到
    /// <paramref name="destination"/>；驗證失敗不寫任何 byte。caller 保有兩個 stream 所有權。
    /// </summary>
    public static void Write(Stream destination, ClfImportOptions options, Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        if (!options.IsValid)
            throw new InvalidDataException("transform 宣告不合法。");
        using var payload = ReadBounded(source, ClfFile.MaxInputBytes, cancellationToken);
        // 只發布已驗證內容：payload 必須本身就是合法 CLF。
        ClfFile.Read(payload, cancellationToken);
        payload.Position = 0;
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, options, (uint)payload.Length);
        destination.Write(header);
        payload.CopyTo(destination);
    }

    /// <summary>
    /// 讀取完整 envelope：header magic／enum／reserved／宣告長度嚴格，payload 長度必須
    /// 與宣告完全一致（短缺或 trailing bytes 都拒絕），CLF 由 <see cref="ClfFile"/> 驗證。
    /// 內容錯誤都是 <see cref="InvalidDataException"/>，取消不包裝。
    /// </summary>
    public static ColorTransformAsset Read(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var envelope = ReadBounded(source, MaxFileBytes, cancellationToken);
        return ReadSnapshot(envelope, cancellationToken);
    }

    internal static ColorTransformAsset ReadSnapshot(MemoryStream envelope, CancellationToken cancellationToken)
    {
        var segment = Buffer(envelope);
        var span = segment.AsSpan();
        if (span.Length < HeaderBytes ||
            !TryParseHeader(span[..HeaderBytes], out var options, out var declared) ||
            span.Length != HeaderBytes + declared)
            throw new InvalidDataException("FBLOOK header 或長度宣告不合法。");
        ColorTransform transform;
        using (var payload = new MemoryStream(segment.Array!, segment.Offset + HeaderBytes, (int)declared, writable: false))
            transform = ClfFile.Read(payload, cancellationToken);
        var key = "look-" + Convert.ToHexString(SHA256.HashData(span)).ToLowerInvariant();
        return new ColorTransformAsset(key, options, transform);
    }

    /// <summary>寫 16-byte header；caller 已驗 <paramref name="options"/> 與長度上限。</summary>
    internal static void WriteHeader(Span<byte> destination, in ClfImportOptions options, uint clfLength)
    {
        Magic.CopyTo(destination);
        destination[8] = (byte)options.Stage;
        destination[9] = (byte)options.Input;
        destination[10] = (byte)options.Output;
        destination[11] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], clfLength);
    }

    /// <summary>
    /// 只驗 16-byte header（List 使用）：magic、stage／input／output 已知值、reserved 0、
    /// 宣告長度不超過 CLF 上限；不解析 payload、不驗內容雜湊。
    /// </summary>
    internal static bool TryParseHeader(
        ReadOnlySpan<byte> header, out ClfImportOptions options, out uint declaredBytes)
    {
        options = default;
        declaredBytes = 0;
        if (header.Length < HeaderBytes || !header.StartsWith(Magic) || header[11] != 0)
            return false;
        options = new ClfImportOptions(
            (ColorTransformStage)header[8], (ColorTransformEncoding)header[9], (ColorTransformEncoding)header[10]);
        if (!options.IsValid)
        {
            options = default;
            return false;
        }
        declaredBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        return declaredBytes <= ClfFile.MaxInputBytes;
    }

    /// <summary>有界串流複製到記憶體（位置歸零）；宣告或實際超過 <paramref name="maxBytes"/> 都拒絕。</summary>
    internal static MemoryStream ReadBounded(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        long declared = -1;
        if (source.CanSeek)
        {
            try { declared = source.Length - source.Position; }
            catch (NotSupportedException) { }
        }
        if (declared > maxBytes)
            throw new InvalidDataException($"來源超過 {maxBytes} bytes 上限。");
        var output = declared >= 0
            ? new MemoryStream((int)Math.Min(declared, maxBytes))
            : new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > maxBytes)
                    throw new InvalidDataException($"來源超過 {maxBytes} bytes 上限。");
                output.Write(buffer, 0, read);
            }
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static ArraySegment<byte> Buffer(MemoryStream stream) =>
        stream.TryGetBuffer(out var segment)
            ? segment
            : new ArraySegment<byte>(stream.ToArray());
}
