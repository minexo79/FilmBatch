using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Localization;
using WebGpuSharp;
using WgpuBuffer = WebGpuSharp.Buffer;

namespace FilmBatch.Gpu.Resources;

[Flags]
public enum GpuBufferUsage
{
    None = 0, MapRead = 1, MapWrite = 2, CopySrc = 4, CopyDst = 8, Uniform = 64, Storage = 128,
}

public sealed class GpuBuffer : IDisposable
{
    internal readonly WgpuBuffer Handle;

    public ulong Size { get; }

    internal GpuBuffer(GpuDevice gpu, ulong size, GpuBufferUsage usage, string? label)
    {
        // WebGPU 要求緩衝區大小為 4 的倍數
        Size = (size + 3) & ~3UL;
        var desc = new BufferDescriptor { Label = label ?? "", Usage = (BufferUsage)(int)usage, Size = Size };
        Handle = gpu.Device.CreateBuffer(ref desc) ?? throw new GpuException(GpuText.Format("Gpu.BufferFailed", label));
        gpu.ThrowIfError(GpuText.Format("Gpu.Context.CreateBuffer", label));
    }

    /// <summary>立即釋放顯示記憶體（不等 GC）。</summary>
    public void Dispose() => Handle.Destroy();
}
