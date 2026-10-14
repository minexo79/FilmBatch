namespace FilmBatch.Gpu.Device;

/// <summary>GPU 後端（與 WebGPU 綁定無關的自有列舉）。</summary>
public enum GpuBackend { Auto, Dx12, Vulkan, Metal, Gl, Unknown }

public enum GpuAdapterKind { Discrete, Integrated, Software, Unknown }

/// <summary>介面卡資訊，顯示於狀態列與 log。</summary>
public sealed record GpuAdapterInfo(
    string Name,
    string Driver,
    GpuBackend Backend,
    GpuAdapterKind Kind,
    uint VendorId,
    uint DeviceId,
    uint MaxTextureDimension2D,
    ulong MaxBufferSize,
    ulong MaxStorageBufferBindingSize)
{
    public bool IsSoftware => Kind == GpuAdapterKind.Software;

    /// <summary>例如「NVIDIA GeForce RTX 4070 · DX12」。</summary>
    public string DisplayName => $"{Name} · {BackendName(Backend)}";

    public static string BackendName(GpuBackend b) => b switch
    {
        GpuBackend.Dx12 => "DX12",
        GpuBackend.Vulkan => "Vulkan",
        GpuBackend.Metal => "Metal",
        GpuBackend.Gl => "OpenGL",
        _ => "?",
    };
}

/// <summary>建立裝置的選項（對應偏好設定中的介面卡與後端）。</summary>
public sealed record GpuDeviceOptions
{
    public GpuBackend Backend { get; init; } = GpuBackend.Auto;
    public bool ForceSoftware { get; init; }
    public bool LowPower { get; init; }
}
