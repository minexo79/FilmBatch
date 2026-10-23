using FilmBatch.Engine.Denoising;
using FilmBatch.Core;
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
    private string? _cpuReason;
    internal bool UsingCpu => _cpuReason is not null;

    public string DeviceName => _cpuReason is { } reason ? CoreText.T($"CPU（{reason}）", $"CPU ({reason})") : _gpu.AdapterInfo.DisplayName;
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
        var parameters = ModuleParams.From(source, settings, encoding);
        _assets.Prepare(source, parameters);
        // 匯出不受預覽記憶體預算限制：只要不超過裝置紋理上限就用 GPU，
        // 實際配置顯示記憶體失敗時才改用 CPU 等效路徑（不靜默截斷）。
        _cpuReason = null;
        if (!_pipeline.FitsDevice(source, parameters))
        {
            _cpuReason = CoreText.T("影像超過 GPU 紋理尺寸上限", "image exceeds the GPU texture size limit");
            return CpuPipeline.Process(source, parameters, null, cancellationToken);
        }
        var output = new ushort[parameters.OutWidth * parameters.OutHeight * 4];
        if (_pipeline.RenderForExport(source, parameters, output, cancellationToken) is { } error)
        {
            _cpuReason = CoreText.T($"GPU 顯示記憶體不足：{error}", $"not enough GPU memory: {error}");
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
        var parameters = Prepare(source, settings);
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
        };
    }

    private ModuleParams Prepare(SourceImage source, Settings settings)
    {
        var parameters = ModuleParams.From(source, settings);
        _assets.Prepare(source, parameters);
        _cpuReason = _pipeline.CanRender(source, parameters) ? null : CoreText.T("影像超過 GPU 尺寸或記憶體預算", "image exceeds the GPU size or memory budget");
        return parameters;
    }

    public RenderResult Render(SourceImage source, SpikeParams parameters)
    {
        if (!_pipeline.CanRender(source)) throw new GpuException("GPU benchmark image exceeds device limits or memory budget.");
        _cpuReason = null;
        var timer = Stopwatch.StartNew();
        var pixels = new byte[checked(source.Width * source.Height * 4)];
        _pipeline.RenderSpike(source, parameters, pixels);
        return new(source.Width, source.Height, pixels, timer.Elapsed);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _gpu.Dispose();
    }
}
