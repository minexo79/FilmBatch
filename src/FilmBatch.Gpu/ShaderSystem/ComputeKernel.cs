using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Localization;
using FilmBatch.Gpu.Resources;
using WebGpuSharp;

namespace FilmBatch.Gpu.ShaderSystem;

public enum BindingKind
{
    /// <summary><c>texture_2d&lt;f32&gt;</c>，以 textureLoad 讀取（不經過濾，可用於 rgba32float）。</summary>
    TextureFloat,
    /// <summary><c>texture_2d&lt;u32&gt;</c>。</summary>
    TextureUint,
    /// <summary><c>texture_storage_2d&lt;format, write&gt;</c>。</summary>
    StorageTextureWrite,
    Uniform,
    StorageBufferRead,
    StorageBuffer,
}

/// <summary>group 0 中的一個 binding；陣列索引即 binding 編號。</summary>
public readonly record struct BindingSpec(BindingKind Kind, GpuTextureFormat Format = GpuTextureFormat.Rgba32Float)
{
    public static readonly BindingSpec TextureFloat = new(BindingKind.TextureFloat);
    public static readonly BindingSpec TextureUint = new(BindingKind.TextureUint);
    public static readonly BindingSpec Uniform = new(BindingKind.Uniform);
    public static readonly BindingSpec StorageRead = new(BindingKind.StorageBufferRead);
    public static readonly BindingSpec Storage = new(BindingKind.StorageBuffer);
    public static BindingSpec StorageTexture(GpuTextureFormat f) => new(BindingKind.StorageTextureWrite, f);
}

/// <summary>已編譯的 compute pipeline，使用明確的 bind group layout（不用 auto layout，才能讀取不可過濾的 rgba32float）。</summary>
public sealed class ComputeKernel : IDisposable
{
    internal readonly ComputePipeline Pipeline;
    internal readonly BindGroupLayout BindGroupLayout;

    public string Label { get; }
    public IReadOnlyList<BindingSpec> Bindings { get; }

    internal ComputeKernel(GpuDevice gpu, string wgsl, string entryPoint, IReadOnlyList<BindingSpec> bindings, string? label)
    {
        Label = label ?? entryPoint;
        Bindings = bindings;
        var device = gpu.Device;

        // 以 error scope 精確取得 shader 編譯與 pipeline 建立的錯誤訊息
        device.PushErrorScope(ErrorFilter.Validation);
        ComputePipeline? pipeline = null;
        ErrorType status = ErrorType.NoError;
        string? message = null;
        try
        {
            var wgslDesc = new ShaderModuleWGSLDescriptor { Code = wgsl };
            var module = device.CreateShaderModuleWGSL(Label, in wgslDesc);

            var entries = new BindGroupLayoutEntry[bindings.Count];
            for (var i = 0; i < bindings.Count; i++)
                entries[i] = ToLayoutEntry((uint)i, bindings[i]);
            var bglDesc = new BindGroupLayoutDescriptor { Label = Label, Entries = entries };
            BindGroupLayout = device.CreateBindGroupLayout(in bglDesc)!;

            var plDesc = new PipelineLayoutDescriptor { Label = Label, BindGroupLayouts = [BindGroupLayout] };
            var pipelineLayout = device.CreatePipelineLayout(in plDesc);

            var pipeDesc = new ComputePipelineDescriptor
            {
                Label = Label,
                Layout = pipelineLayout!,
                Compute = new ComputeState { Module = module!, EntryPoint = entryPoint },
            };
            pipeline = device.CreateComputePipelineSync(in pipeDesc);
        }
        finally
        {
            (status, message) = device.PopErrorScopeSync(10_000_000_000);
        }
        if (pipeline is null || !string.IsNullOrEmpty(message))
            throw new GpuException(GpuText.Format("Gpu.PipelineFailed", Label, status, message));
        Pipeline = pipeline;
    }

    private static BindGroupLayoutEntry ToLayoutEntry(uint binding, BindingSpec spec)
    {
        var e = new BindGroupLayoutEntry { Binding = binding, Visibility = ShaderStage.Compute };
        switch (spec.Kind)
        {
            case BindingKind.TextureFloat:
                e.Texture = new TextureBindingLayout { SampleType = TextureSampleType.UnfilterableFloat, ViewDimension = TextureViewDimension.D2 };
                break;
            case BindingKind.TextureUint:
                e.Texture = new TextureBindingLayout { SampleType = TextureSampleType.Uint, ViewDimension = TextureViewDimension.D2 };
                break;
            case BindingKind.StorageTextureWrite:
                e.StorageTexture = new StorageTextureBindingLayout
                {
                    Access = StorageTextureAccess.WriteOnly,
                    Format = spec.Format.ToNative(),
                    ViewDimension = TextureViewDimension.D2,
                };
                break;
            case BindingKind.Uniform:
                e.Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform };
                break;
            case BindingKind.StorageBufferRead:
                e.Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage };
                break;
            case BindingKind.StorageBuffer:
                e.Buffer = new BufferBindingLayout { Type = BufferBindingType.Storage };
                break;
        }
        return e;
    }

    public void Dispose() { }
}
