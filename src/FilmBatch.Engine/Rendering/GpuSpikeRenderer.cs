using System.Diagnostics;
using System.Runtime.InteropServices;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// M0 GPU spike：上傳 16-bit 影像 → compute shader（曝光／亮度／飽和、可分離高斯模糊）→ 轉 BGRA8 → 讀回。
/// 來源影像在第一次使用時上傳並轉成線性光，之後只更新 uniform（計畫書 11.3）。
/// </summary>
public sealed class GpuSpikeRenderer : IPreviewRenderer
{
    private const int MaxCachedSources = 6;

    private readonly GpuDevice _gpu;
    private readonly ComputeKernel _decode;
    private readonly ComputeKernel _adjust;
    private readonly ComputeKernel _blur;
    private readonly ComputeKernel _encode;
    private readonly GpuBuffer _adjustParams;
    private readonly GpuBuffer _blurParamsH;
    private readonly GpuBuffer _blurParamsV;
    private readonly LinkedList<SourceState> _sources = new();

    public string DeviceName => _gpu.AdapterInfo.DisplayName;
    public bool IsSoftware => _gpu.AdapterInfo.IsSoftware;
    public GpuAdapterInfo AdapterInfo => _gpu.AdapterInfo;
    public GpuDevice Device => _gpu;

    public GpuSpikeRenderer(GpuDevice gpu, ShaderLibrary? shaders = null)
    {
        _gpu = gpu;
        shaders ??= new ShaderLibrary();
        var tex = BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float);
        _decode = gpu.CreateKernel(shaders.Load("io/decode_rgba16.wgsl"), "main", [BindingSpec.TextureUint, tex], "decode");
        _adjust = gpu.CreateKernel(shaders.Load("spike/adjust.wgsl"), "main", [BindingSpec.TextureFloat, tex, BindingSpec.Uniform], "adjust");
        _blur = gpu.CreateKernel(shaders.Load("spike/blur.wgsl"), "main", [BindingSpec.TextureFloat, tex, BindingSpec.Uniform], "blur");
        _encode = gpu.CreateKernel(shaders.Load("io/encode_bgra8.wgsl"), "main", [BindingSpec.TextureFloat, BindingSpec.Storage], "encode");
        _adjustParams = gpu.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "adjust params");
        _blurParamsH = gpu.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "blur params H");
        _blurParamsV = gpu.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "blur params V");
    }

    public static GpuSpikeRenderer Create(GpuDeviceOptions? options = null) => new(GpuDevice.Create(options));

    [StructLayout(LayoutKind.Sequential)]
    private struct AdjustUniforms
    {
        public float Exposure, Brightness, Saturation, Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurUniforms
    {
        public int DirX, DirY, Radius;
        public float Sigma;
    }

    public RenderResult Render(SourceImage source, Settings settings) => Render(source, SpikeParams.From(settings));

    public RenderResult Render(SourceImage source, SpikeParams p)
    {
        var sw = Stopwatch.StartNew();
        var s = Acquire(source);
        uint w = (uint)source.Width, h = (uint)source.Height;

        _gpu.WriteBuffer(_adjustParams, new AdjustUniforms { Exposure = p.Exposure, Brightness = p.Brightness, Saturation = p.Saturation });
        var radius = p.BlurRadiusPixels(source.ShortSide);
        if (radius > 0)
        {
            var sigma = Math.Max(0.5f, radius / 3f);
            _gpu.WriteBuffer(_blurParamsH, new BlurUniforms { DirX = 1, Radius = radius, Sigma = sigma });
            _gpu.WriteBuffer(_blurParamsV, new BlurUniforms { DirY = 1, Radius = radius, Sigma = sigma });
        }

        using (var cmd = _gpu.BeginCommands())
        {
            cmd.Dispatch2D(_adjust, [s.Linear, s.A, _adjustParams], w, h);
            if (radius > 0)
            {
                cmd.Dispatch2D(_blur, [s.A, s.B, _blurParamsH], w, h);
                cmd.Dispatch2D(_blur, [s.B, s.A, _blurParamsV], w, h);
            }
            cmd.Dispatch2D(_encode, [s.A, s.Output], w, h);
            cmd.CopyBuffer(s.Output, s.Readback, s.Output.Size);
            cmd.Submit();
        }

        var bgra = new byte[w * h * 4];
        _gpu.ReadBuffer(s.Readback, bgra);
        return new RenderResult((int)w, (int)h, bgra, sw.Elapsed);
    }

    /// <summary>取得（必要時上傳）來源影像的 GPU 狀態；以 LRU 保留最近使用的幾張。</summary>
    private SourceState Acquire(SourceImage source)
    {
        for (var n = _sources.First; n != null; n = n.Next)
        {
            if (!ReferenceEquals(n.Value.Source, source)) continue;
            _sources.Remove(n);
            _sources.AddFirst(n);
            return n.Value;
        }

        var state = new SourceState(_gpu, source);
        using (var cmd = _gpu.BeginCommands())
        {
            _gpu.WriteTexture<ushort>(state.Upload, source.Rgba16);
            cmd.Dispatch2D(_decode, [state.Upload, state.Linear], (uint)source.Width, (uint)source.Height);
            cmd.Submit();
        }
        _sources.AddFirst(state);
        while (_sources.Count > MaxCachedSources)
        {
            _sources.Last!.Value.Dispose();
            _sources.RemoveLast();
        }
        return state;
    }

    /// <summary>釋放某張來源影像的 GPU 資源（切換資料夾時）。</summary>
    public void Evict(SourceImage source)
    {
        for (var n = _sources.First; n != null; n = n.Next)
        {
            if (!ReferenceEquals(n.Value.Source, source)) continue;
            n.Value.Dispose();
            _sources.Remove(n);
            return;
        }
    }

    private sealed class SourceState : IDisposable
    {
        public readonly SourceImage Source;
        public readonly GpuTexture Upload, Linear, A, B;
        public readonly GpuBuffer Output, Readback;

        public SourceState(GpuDevice gpu, SourceImage source)
        {
            Source = source;
            uint w = (uint)source.Width, h = (uint)source.Height;
            Upload = gpu.CreateTexture(w, h, GpuTextureFormat.Rgba16Uint, GpuTextureUsage.CopyDst | GpuTextureUsage.Sampled, "upload");
            var work = GpuTextureUsage.Sampled | GpuTextureUsage.Storage;
            Linear = gpu.CreateTexture(w, h, GpuTextureFormat.Rgba32Float, work, "linear");
            A = gpu.CreateTexture(w, h, GpuTextureFormat.Rgba32Float, work, "work A");
            B = gpu.CreateTexture(w, h, GpuTextureFormat.Rgba32Float, work, "work B");
            Output = gpu.CreateBuffer(w * h * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc, "bgra out");
            Readback = gpu.CreateBuffer(w * h * 4, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "readback");
        }

        public void Dispose()
        {
            Upload.Dispose();
            Linear.Dispose();
            A.Dispose();
            B.Dispose();
            Output.Dispose();
            Readback.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
        _decode.Dispose();
        _adjust.Dispose();
        _blur.Dispose();
        _encode.Dispose();
        _adjustParams.Dispose();
        _blurParamsH.Dispose();
        _blurParamsV.Dispose();
        _gpu.Dispose();
    }
}
