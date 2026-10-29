using FilmBatch.Engine.Denoising;
using System.Diagnostics;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Engine.Rendering;

/// <summary>完整 GPU 預覽與 16-bit 匯出；SpikeParams overload 僅供原有 GPU 基準測試。</summary>
public sealed class GpuSpikeRenderer : IPreviewRenderer
{
    private readonly GpuDevice _gpu;
    private readonly GpuEffectPipeline _pipeline;
    private readonly EffectAssets _assets;
    private RenderBackend? _cpuBackend;
    internal bool UsingCpu => _cpuBackend is not null;

    internal RenderBackend Backend => _cpuBackend ?? new(_gpu.AdapterInfo.DisplayName, null, _gpu.AdapterInfo.IsSoftware);
    public string DeviceName => Backend.DeviceName;
    public bool IsSoftware => UsingCpu || _gpu.AdapterInfo.IsSoftware;
    public GpuAdapterInfo AdapterInfo => _gpu.AdapterInfo;
    public GpuDevice Device => _gpu;

    public GpuSpikeRenderer(GpuDevice gpu, ShaderLibrary? shaders = null, EffectRendererOptions? effectOptions = null)
    {
        _gpu = gpu;
        _assets = new(effectOptions ?? new());
        try { _pipeline = new(gpu, shaders ?? new ShaderLibrary(), effectOptions); }
        catch { gpu.Dispose(); throw; }
    }

    public static GpuSpikeRenderer Create(GpuDeviceOptions? options = null, EffectRendererOptions? effectOptions = null) =>
        new(GpuDevice.Create(options), effectOptions: effectOptions);

    public SourceImage Render16(SourceImage source, Settings settings, CancellationToken cancellationToken = default,
        RgbEncoding encoding = RgbEncoding.Srgb)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DenoiseSettings.From(settings).IsActive)
            source = DenoiseProcessor.Shared.Prepare(source, settings, cancellationToken);
        var parameters = ModuleParams.From(source, settings, encoding, cancellationToken: cancellationToken);
        _assets.Prepare(source, parameters);
        // 匯出不受預覽記憶體預算限制：只要不超過裝置紋理上限就用 GPU，
        // 實際配置顯示記憶體失敗時才改用 CPU 等效路徑（不靜默截斷）。
        _cpuBackend = null;
        if (!_pipeline.FitsDevice(source, parameters))
        {
            _cpuBackend = RenderBackend.GpuSizeExceeded;
            return CpuPipeline.Process(source, parameters, null, cancellationToken);
        }
        var output = new ushort[parameters.OutWidth * parameters.OutHeight * 4];
        if (_pipeline.RenderForExport(source, parameters, output, cancellationToken) is { } error)
        {
            _cpuBackend = RenderBackend.GpuAllocationFailed(error);
            return CpuPipeline.Process(source, parameters, null, cancellationToken);
        }
        return new SourceImage(parameters.OutWidth, parameters.OutHeight, output, encoding)
        {
            FilePath = source.FilePath, RawOptions = source.RawOptions,
            RawInfo = source.RawInfo, SemanticMasks = source.SemanticMasks,
            Denoise = source.Denoise,
        };
    }

    public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        if (DenoiseSettings.From(settings).IsActive)
            source = DenoiseProcessor.Shared.Prepare(source, settings, cancellationToken);
        var parameters = Prepare(source, settings, cancellationToken);
        // 輸出為裁切後的尺寸
        var output = new byte[parameters.OutWidth * parameters.OutHeight * 4];
        if (UsingCpu)
        {
            var rendered = CpuPipeline.Process(source, parameters, null, cancellationToken).Rgba16;
            for (var i = 0; i < rendered.Length; i += 4)
            {
                output[i] = (byte)((rendered[i + 2] + 128) / 257);
                output[i + 1] = (byte)((rendered[i + 1] + 128) / 257);
                output[i + 2] = (byte)((rendered[i] + 128) / 257);
                output[i + 3] = (byte)((rendered[i + 3] + 128) / 257);
            }
        }
        else _pipeline.Render(source, parameters, output, cancellationToken);
        return new(parameters.OutWidth, parameters.OutHeight, output, timer.Elapsed)
        {
            Denoise = source.Denoise, PreparedSource = source,
            Stats = new FrameStats(parameters.FrameWidth, parameters.FrameHeight),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// GPU 只配置並處理 <see cref="WindowRegion.Expand"/> 算出的區域（工作紋理＝區域尺寸），整張來源紋理照常以參考相等快取；
    /// 每個以畫框為座標系的 pass 透過共用的 view uniform（畫框尺寸＋區域原點）換算畫框座標。
    /// 單一區域超過預算時改切分窗口分塊算圖（每塊各自 Expand、整張來源保留、預算不變）；分塊仍超過或不可分塊時改用 CPU 等效路徑。
    /// </remarks>
    public RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats,
        CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        if (DenoiseSettings.From(settings).IsActive)
            frameSource = DenoiseProcessor.Shared.Prepare(frameSource, settings, cancellationToken);
        var parameters = ModuleParams.From(frameSource, settings, cancellationToken: cancellationToken);
        _assets.Prepare(frameSource, parameters);
        var region = WindowRegion.Expand(parameters, view);
        parameters.View = region;
        parameters.FrameStats = stats;
        byte[] output;
        if (_pipeline.CanRender(frameSource, parameters))
        {
            _cpuBackend = null;
            var pixels = new byte[region.W * region.H * 4];
            _pipeline.Render(frameSource, parameters, pixels, cancellationToken);
            output = WindowRegion.CropBgra(pixels, region, view);
        }
        else
        {
            Span<(RenderView Tile, RenderView Region)> plan = stackalloc (RenderView, RenderView)[4];
            var count = _pipeline.PlanTiles(frameSource, parameters, view, plan);
            if (count > 0)
            {
                _cpuBackend = null;
                output = new byte[checked(view.W * view.H * 4)];
                _pipeline.RenderTiled(frameSource, parameters, view, plan[..count], output, cancellationToken);
            }
            else
            {
                _cpuBackend = RenderBackend.GpuBudgetExceeded;
                output = WindowRegion.CropToBgra8(CpuPipeline.Process(frameSource, parameters, null, cancellationToken).Rgba16, region, view);
            }
        }
        return new(view.W, view.H, output, timer.Elapsed)
        {
            Denoise = frameSource.Denoise, PreparedSource = frameSource, Stats = stats,
        };
    }

    private ModuleParams Prepare(SourceImage source, Settings settings, CancellationToken cancellationToken)
    {
        var parameters = ModuleParams.From(source, settings, cancellationToken: cancellationToken);
        _assets.Prepare(source, parameters);
        _cpuBackend = _pipeline.CanRender(source, parameters) ? null : RenderBackend.GpuBudgetExceeded;
        return parameters;
    }

    public RenderResult Render(SourceImage source, SpikeParams parameters)
    {
        if (!_pipeline.CanRender(source)) throw new GpuException("GPU benchmark image exceeds device limits or memory budget.");
        _cpuBackend = null;
        var timer = Stopwatch.StartNew();
        var pixels = new byte[checked(source.Width * source.Height * 4)];
        _pipeline.RenderSpike(source, parameters, pixels);
        return new(source.Width, source.Height, pixels, timer.Elapsed);
    }

    /// <summary>
    /// 釋放指定來源的 GPU 常駐紋理（idle／換照片時由呼叫端在 GPU 執行緒上呼叫）；來源不在快取時無動作。
    /// 只釋放該來源，不動工作區與其他快取。
    /// </summary>
    public void EvictSource(SourceImage source) => _pipeline.Evict(source);

    public void Dispose()
    {
        _pipeline.Dispose();
        _gpu.Dispose();
    }
}
