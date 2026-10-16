using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Localization;
using WebGpuSharp;

namespace FilmBatch.Gpu.Resources;

public enum GpuTextureFormat { Rgba8Unorm, Rgba16Uint, Rgba16Float, Rgba32Float, R32Float }

[Flags]
public enum GpuTextureUsage { None = 0, CopySrc = 1, CopyDst = 2, Sampled = 4, Storage = 8 }

public static class GpuTextureFormatExtensions
{
    public static uint BytesPerPixel(this GpuTextureFormat f) => f switch
    {
        GpuTextureFormat.Rgba8Unorm => 4,
        GpuTextureFormat.Rgba16Uint or GpuTextureFormat.Rgba16Float => 8,
        GpuTextureFormat.Rgba32Float => 16,
        GpuTextureFormat.R32Float => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    internal static TextureFormat ToNative(this GpuTextureFormat f) => f switch
    {
        GpuTextureFormat.Rgba8Unorm => TextureFormat.RGBA8Unorm,
        GpuTextureFormat.Rgba16Uint => TextureFormat.RGBA16Uint,
        GpuTextureFormat.Rgba16Float => TextureFormat.RGBA16Float,
        GpuTextureFormat.Rgba32Float => TextureFormat.RGBA32Float,
        GpuTextureFormat.R32Float => TextureFormat.R32Float,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

}

/// <summary>2D 紋理與其預設檢視。</summary>
public sealed class GpuTexture : IDisposable
{
    internal readonly Texture Handle;
    internal readonly TextureView View;

    public uint Width { get; }
    public uint Height { get; }
    public GpuTextureFormat Format { get; }

    internal GpuTexture(GpuDevice gpu, uint width, uint height, GpuTextureFormat format, GpuTextureUsage usage, string? label)
    {
        Width = width;
        Height = height;
        Format = format;
        var desc = new TextureDescriptor
        {
            Label = label ?? "",
            Usage = (TextureUsage)(int)usage,
            Dimension = TextureDimension.D2,
            Size = new Extent3D { Width = width, Height = height, DepthOrArrayLayers = 1 },
            Format = format.ToNative(),
            MipLevelCount = 1,
            SampleCount = 1,
        };
        Handle = gpu.Device.CreateTexture(in desc) ?? throw new GpuException(GpuText.Format("Gpu.TextureFailed", label));
        gpu.ThrowIfError(GpuText.Format("Gpu.Context.CreateTexture", label));
        View = Handle.CreateView() ?? throw new GpuException(GpuText.Format("Gpu.TextureViewFailed", label));
    }

    /// <summary>立即釋放顯示記憶體（不等 GC）。</summary>
    public void Dispose() => Handle.Destroy();
}
