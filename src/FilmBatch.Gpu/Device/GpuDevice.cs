using System.Text;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;
using WebGpuSharp;

namespace FilmBatch.Gpu.Device;

/// <summary>
/// WebGPU 裝置的薄封裝。整個專案只有本組件直接使用 WebGPU 綁定（WebGPUSharp / Dawn），
/// 日後要更換綁定只需改寫本組件（見 docs/adr/0001-webgpu-binding.md）。
/// <para>執行緒：所有提交應由單一 GPU 執行緒進行（計畫書 9.2）。</para>
/// </summary>
public sealed class GpuDevice : IDisposable
{
    private const ulong RequestTimeoutNs = 10_000_000_000;

    internal readonly Instance Instance;
    internal readonly Adapter Adapter;
    internal readonly WebGpuSharp.Device Device;
    internal readonly Queue Queue;

    private string? _pendingError;
    private bool _disposed;

    public GpuAdapterInfo AdapterInfo { get; }

    /// <summary>裝置遺失（驅動程式重置等）。參數為原因說明。可能在任意執行緒觸發。</summary>
    public event Action<string>? DeviceLost;

    public bool IsLost { get; private set; }

    /// <summary>GPU 驗證錯誤等訊息的記錄輸出（例如寫入 log 檔）。</summary>
    public static Action<string>? Log { get; set; }

    private GpuDevice(Instance instance, Adapter adapter, GpuAdapterInfo info)
    {
        Instance = instance;
        Adapter = adapter;
        AdapterInfo = info;

        var desc = new DeviceDescriptor
        {
            // 使用介面卡支援的上限（大尺寸影像需要較大的紋理與緩衝區）
            RequiredLimits = adapter.GetLimits(),
            UncapturedErrorCallback = (type, message) =>
            {
                var text = $"{type}: {Encoding.UTF8.GetString(message)}";
                Log?.Invoke(text);
                _pendingError ??= text;
            },
            DeviceLostCallback = (reason, message) =>
            {
                if (reason is DeviceLostReason.Destroyed or DeviceLostReason.CallbackCancelled) return;
                IsLost = true;
                DeviceLost?.Invoke($"{reason}: {Encoding.UTF8.GetString(message)}");
            },
        };
        Device = adapter.RequestDeviceSync(in desc, RequestTimeoutNs)
            ?? throw new GpuException("無法建立 GPU 裝置。");
        Queue = Device.GetQueue() ?? throw new GpuException("無法取得 GPU 佇列。");
    }

    /// <summary>綁定與原生函式庫版本（顯示於 log 與 gpu-info）。</summary>
    public static string NativeVersion() =>
        $"Dawn（WebGPUSharp {typeof(WebGPU).Assembly.GetName().Version?.ToString(3)}）";

    /// <summary>建立裝置。硬體介面卡優先；找不到時（或 <see cref="GpuDeviceOptions.ForceSoftware"/>）改用軟體介面卡。</summary>
    public static GpuDevice Create(GpuDeviceOptions? options = null)
    {
        options ??= new GpuDeviceOptions();
        var instance = WebGPU.CreateInstance() ?? throw new GpuException("無法建立 WebGPU instance。");

        Adapter? adapter = null;
        if (!options.ForceSoftware)
            adapter = RequestAdapter(instance, options.Backend, options.LowPower, fallback: false);
        // 沒有硬體介面卡時改用軟體介面卡（需 Dawn 建置包含 SwiftShader / WARP，見 ADR）
        adapter ??= RequestAdapter(instance, options.Backend, options.LowPower, fallback: true);
        if (adapter == null)
            throw new GpuException(options.ForceSoftware
                ? "找不到軟體 GPU 介面卡（目前的 Dawn 建置未包含軟體介面卡）。"
                : "找不到可用的 GPU 介面卡。");

        return new GpuDevice(instance, adapter, QueryInfo(adapter));
    }

    /// <summary>
    /// 列出可用的介面卡。WebGPU 標準 API 沒有列舉功能，因此依後端與電源偏好逐一請求後去除重複。
    /// </summary>
    public static IReadOnlyList<GpuAdapterInfo> EnumerateAdapters(GpuBackend backend = GpuBackend.Auto)
    {
        var instance = WebGPU.CreateInstance() ?? throw new GpuException("無法建立 WebGPU instance。");
        GpuBackend[] backends = backend != GpuBackend.Auto ? [backend]
            : OperatingSystem.IsWindows() ? [GpuBackend.Dx12, GpuBackend.Vulkan]
            : OperatingSystem.IsMacOS() ? [GpuBackend.Metal]
            : [GpuBackend.Vulkan];

        var list = new List<GpuAdapterInfo>();
        foreach (var b in backends)
        foreach (var (lowPower, fallback) in new[] { (false, false), (true, false), (false, true) })
        {
            if (RequestAdapter(instance, b, lowPower, fallback) is not { } a) continue;
            var info = QueryInfo(a);
            if (!list.Any(x => x.Name == info.Name && x.Backend == info.Backend)) list.Add(info);
        }
        return list;
    }

    private static Adapter? RequestAdapter(Instance instance, GpuBackend backend, bool lowPower, bool fallback)
    {
        var opts = new RequestAdapterOptions
        {
            CompatibleSurface = null!,
            FeatureLevel = FeatureLevel.Core,
            PowerPreference = lowPower ? PowerPreference.LowPower : PowerPreference.HighPerformance,
            ForceFallbackAdapter = fallback,
            BackendType = backend switch
            {
                GpuBackend.Dx12 => BackendType.D3D12,
                GpuBackend.Vulkan => BackendType.Vulkan,
                GpuBackend.Metal => BackendType.Metal,
                GpuBackend.Gl => BackendType.OpenGL,
                // Dawn 的預設：Windows → D3D12、macOS → Metal、Linux → Vulkan
                _ => BackendType.Undefined,
            },
        };
        try
        {
            return instance.RequestAdapterSync(in opts, RequestTimeoutNs);
        }
        catch (WebGPUException)
        {
            return null; // No supported adapters
        }
    }

    private static GpuAdapterInfo QueryInfo(Adapter adapter)
    {
        var props = adapter.GetInfo();
        var limits = adapter.GetLimits() ?? default;
        return new GpuAdapterInfo(
            Name: string.IsNullOrEmpty(props.Device) ? props.Description ?? "Unknown" : props.Device,
            Driver: props.Description ?? "",
            Backend: props.BackendType switch
            {
                BackendType.D3D12 => GpuBackend.Dx12,
                BackendType.Vulkan => GpuBackend.Vulkan,
                BackendType.Metal => GpuBackend.Metal,
                BackendType.OpenGL or BackendType.OpenGLES => GpuBackend.Gl,
                _ => GpuBackend.Unknown,
            },
            Kind: props.AdapterType switch
            {
                AdapterType.DiscreteGPU => GpuAdapterKind.Discrete,
                AdapterType.IntegratedGPU => GpuAdapterKind.Integrated,
                AdapterType.CPU => GpuAdapterKind.Software,
                _ => GpuAdapterKind.Unknown,
            },
            VendorId: props.VendorID,
            DeviceId: props.DeviceID,
            MaxTextureDimension2D: limits.MaxTextureDimension2D,
            MaxBufferSize: limits.MaxBufferSize,
            MaxStorageBufferBindingSize: limits.MaxStorageBufferBindingSize);
    }

    /// <summary>若先前的操作產生驗證錯誤，丟出例外。</summary>
    internal void ThrowIfError(string context)
    {
        var err = Interlocked.Exchange(ref _pendingError, null);
        if (err != null) throw new GpuException($"{context}：{err}");
    }

    // ───────────── 資源建立 ─────────────

    public GpuTexture CreateTexture(uint width, uint height, GpuTextureFormat format, GpuTextureUsage usage, string? label = null)
        => new(this, width, height, format, usage, label);

    public GpuBuffer CreateBuffer(ulong size, GpuBufferUsage usage, string? label = null)
        => new(this, size, usage, label);

    public ComputeKernel CreateKernel(string wgsl, string entryPoint, IReadOnlyList<BindingSpec> bindings, string? label = null)
        => new(this, wgsl, entryPoint, bindings, label);

    public GpuCommandList BeginCommands() => new(this);

    // ───────────── 資料傳輸 ─────────────

    /// <summary>上傳整張紋理。WebGPU 的 writeTexture 不要求每列 256 位元組對齊。</summary>
    public void WriteTexture<T>(GpuTexture texture, ReadOnlySpan<T> data) where T : unmanaged
    {
        var dst = new TexelCopyTextureInfo { Texture = texture.Handle, Aspect = TextureAspect.All };
        var layout = new TexelCopyBufferLayout
        {
            BytesPerRow = texture.Width * texture.Format.BytesPerPixel(),
            RowsPerImage = texture.Height,
        };
        var size = new Extent3D { Width = texture.Width, Height = texture.Height, DepthOrArrayLayers = 1 };
        Queue.WriteTexture(in dst, data, in layout, in size);
        ThrowIfError("上傳紋理");
    }

    public void WriteBuffer<T>(GpuBuffer buffer, ReadOnlySpan<T> data, ulong offset = 0) where T : unmanaged
        => Queue.WriteBuffer(buffer.Handle, offset, data);

    public void WriteBuffer<T>(GpuBuffer buffer, in T value) where T : unmanaged
        => Queue.WriteBuffer(buffer.Handle, 0, new ReadOnlySpan<T>(in value));

    /// <summary>同步讀回（MapRead 緩衝區）到 <paramref name="destination"/>。呼叫端應在 GPU 執行緒上呼叫。</summary>
    public void ReadBuffer(GpuBuffer buffer, byte[] destination)
    {
        WaitIdle();
        buffer.Handle.MapSync(MapMode.Read, 0, (nuint)buffer.Size, 10_000);
        try
        {
            buffer.Handle.GetConstMappedRange<byte>(span => span[..destination.Length].CopyTo(destination));
        }
        finally
        {
            buffer.Handle.Unmap();
        }
        ThrowIfError("讀回");
    }

    /// <summary>等待佇列中的工作全部完成。</summary>
    public void WaitIdle() => Queue.OnSubmittedWorkSync(RequestTimeoutNs);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Dawn 物件由 WebGPUSharp 以 GC 釋放；這裡只需確保佇列工作完成
        try { WaitIdle(); } catch (Exception) { /* 已遺失的裝置 */ }
    }
}
