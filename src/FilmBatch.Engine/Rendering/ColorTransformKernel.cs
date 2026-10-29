using System.Runtime.InteropServices;
using FilmBatch.Core.Film;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 單一 device-local chain cache 與一次 compute dispatch。
/// Bindings：source texture、destination rgba32float、caller uniform、read-only 16-byte ops、read-only float table。
/// 同一 verified look-key 重用 buffers；不同 key 取代。目前 chain 若已 Encode 且尚未 ReleaseCompleted，
/// 會保留到 command list 完成或放棄。同時最多一組 retired；第二個切換丟出 <see cref="InvalidOperationException"/>。
/// </summary>
internal sealed class ColorTransformKernel : IDisposable
{
    private readonly GpuDevice _device;
    private readonly ComputeKernel _kernel;
    private Slot? _current;
    private Slot? _retired;
    private string? _lost;
    private bool _disposed;

    // 驗證 Core 的 4×int32 op record 確實是緊密 16 bytes;不符時拒絕上傳而非寫入錯誤位元。
    private static readonly int OpRecordBytes = Marshal.SizeOf<ColorTransformOp>();

    public ColorTransformKernel(GpuDevice device, ShaderLibrary shaders)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shaders);
        _device = device;
        _kernel = device.CreateKernel(
            shaders.Load("film/transform.wgsl"),
            "main",
            [
                BindingSpec.TextureFloat,
                BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float),
                BindingSpec.Uniform,
                BindingSpec.StorageRead,
                BindingSpec.StorageRead,
            ],
            "color transform");
        _device.DeviceLost += OnDeviceLost;
    }

    /// <summary>ops×16 + data×4。不配置 GPU 資源。</summary>
    public static ulong RequiredBytes(ColorTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return (ulong)transform.Ops.Length * 16UL + (ulong)transform.Data.Length * 4UL;
    }

    /// <summary>
    /// <paramref name="maxStorageBufferBindingSize"/> 為 0 代表不限制。
    /// 否則 ops 與 data 兩個 storage binding 都必須小於等於該上限。
    /// </summary>
    public static bool Fits(ColorTransform transform, ulong maxStorageBufferBindingSize)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (maxStorageBufferBindingSize == 0) return true;
        var opsBytes = (ulong)transform.Ops.Length * 16UL;
        var dataBytes = (ulong)transform.Data.Length * 4UL;
        return opsBytes <= maxStorageBufferBindingSize && dataBytes <= maxStorageBufferBindingSize;
    }

    /// <summary>目前 chain 加上至多一組尚未 ReleaseCompleted 的 retired buffers。</summary>
    public ulong ResidentBytes
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ColorTransformKernel));
            return (_current?.Bytes ?? 0UL) + (_retired?.Bytes ?? 0UL);
        }
    }

    /// <summary>目前 resident chain 的 verified look-key;尚未 Prepare 時為 null。</summary>
    public string? CurrentKey
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ColorTransformKernel));
            return _current?.Key;
        }
    }

    public void Prepare(string lookKey, ColorTransform transform) =>
        Prepare(lookKey, transform, _device.AdapterInfo.MaxStorageBufferBindingSize);

    public void Prepare(string lookKey, ColorTransform transform, ulong maxStorageBufferBindingSize)
    {
        ThrowIfUnusable();
        RequireKey(lookKey);
        ArgumentNullException.ThrowIfNull(transform);
        if (_current is not null && string.Equals(_current.Key, lookKey, StringComparison.Ordinal))
            return;
        if (!Fits(transform, maxStorageBufferBindingSize))
            throw new GpuException("色彩轉換的 ops 或 data 超過 storage binding 上限。");
        if (_retired is not null)
            throw new InvalidOperationException("上一組色彩轉換仍在執行，必須先 ReleaseCompleted。");

        var uploaded = Upload(lookKey, transform);
        if (Volatile.Read(ref _lost) is not null || _device.IsLost)
        {
            uploaded.Dispose();
            ThrowIfUnusable();
        }

        if (_current is { InFlight: true }) _retired = _current;
        else _current?.Dispose();
        _current = uploaded;
    }

    public void Encode(GpuCommandList commands, GpuTexture source, GpuTexture destination, GpuBuffer uniforms,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnusable();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(uniforms);
        if (_current is null) throw new InvalidOperationException("色彩轉換尚未準備。");

        _current.InFlight = true;
        commands.Dispatch2D(_kernel,
            [source, destination, uniforms, _current.Ops, _current.Data],
            source.Width, source.Height);
    }

    public void ReleaseCompleted()
    {
        ThrowIfUnusable();
        _retired?.Dispose();
        _retired = null;
        if (_current is not null) _current.InFlight = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.DeviceLost -= OnDeviceLost;
        _current?.Dispose();
        _retired?.Dispose();
        _current = null;
        _retired = null;
        try { _kernel.Dispose(); }
        catch (Exception error)
        {
            GpuDevice.Log?.Invoke($"color transform kernel release failed: {error.Message}");
        }
    }

    private Slot Upload(string lookKey, ColorTransform transform)
    {
        if (OpRecordBytes != 16)
            throw new GpuException("色彩轉換節點紀錄不是 16 bytes,與 GPU ABI 不符。");
        var ops = transform.Ops;

        GpuBuffer? opsBuffer = null;
        GpuBuffer? dataBuffer = null;
        try
        {
            opsBuffer = _device.CreateBuffer((ulong)ops.Length * 16UL,
                GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "color transform ops");
            _device.WriteBuffer(opsBuffer, ops);
            var data = transform.Data;
            dataBuffer = _device.CreateBuffer((ulong)data.Length * sizeof(float),
                GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "color transform data");
            _device.WriteBuffer(dataBuffer, data);
            var slot = new Slot(lookKey, opsBuffer, dataBuffer, RequiredBytes(transform));
            opsBuffer = null;
            dataBuffer = null;
            return slot;
        }
        catch (Exception)
        {
            Release(opsBuffer);
            Release(dataBuffer);
            throw;
        }
    }

    private void OnDeviceLost(string reason) => Interlocked.CompareExchange(ref _lost, reason, null);

    private void ThrowIfUnusable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ColorTransformKernel));
        if (Volatile.Read(ref _lost) is { } reason) throw new GpuException(reason);
        if (_device.IsLost) throw new GpuException("GPU 裝置已遺失，色彩轉換不可再使用。");
    }

    private static void RequireKey(string? lookKey)
    {
        if (lookKey is not { Length: 69 }
            || !lookKey.StartsWith("look-", StringComparison.Ordinal)
            || !IsLowerHex(lookKey.AsSpan(5)))
            throw new ArgumentException("色彩轉換的 look key 必須是 look- 加上 64 個小寫十六進位字元。", nameof(lookKey));
    }

    private static bool IsLowerHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static void Release(GpuBuffer? buffer)
    {
        if (buffer is null) return;
        try { buffer.Dispose(); }
        catch (Exception error)
        {
            GpuDevice.Log?.Invoke($"color transform buffer release failed: {error.Message}");
        }
    }

    private sealed class Slot : IDisposable
    {
        public Slot(string key, GpuBuffer ops, GpuBuffer data, ulong bytes)
        {
            Key = key;
            Ops = ops;
            Data = data;
            Bytes = bytes;
        }

        public string Key { get; }
        public GpuBuffer Ops { get; }
        public GpuBuffer Data { get; }
        public ulong Bytes { get; }
        public bool InFlight { get; set; }

        public void Dispose()
        {
            Release(Ops);
            Release(Data);
        }
    }
}
