using System.Numerics;
using System.Runtime.InteropServices;
using FilmBatch.Core.Film;
using FilmBatch.Core.Random;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Engine.Rendering;

public sealed record EffectRendererOptions
{
    public string? TexturesFolder { get; init; }
    public ulong MemoryBudgetBytes { get; init; }
    /// <summary>每次編碼/讀回的列帶大小上限（位元組）；0 = 預設 64 MB，仍受裝置緩衝區上限約束。</summary>
    public ulong MaxBandBytes { get; init; }
}

/// <summary>共用的完整 GPU 管線；預覽與 16-bit 匯出只有最後的量化方式不同。</summary>
internal sealed class GpuEffectPipeline : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ShaderLibrary _shaders;
    private readonly ulong _budget, _bandCap;
    private readonly List<ComputeKernel> _kernels = [];
    private readonly List<GpuBuffer> _uniforms = [];
    private readonly Dictionary<(int Radius, float Sigma), GpuBuffer> _gaussians = [];
    // 內附 LUT 的紋理（FilmLookLibrary 對同一 key 回傳同一個 FilmLut；FilmLut 未覆寫 Equals，預設比較即參考相等）
    private readonly Dictionary<FilmLut, GpuTexture> _luts = [];
    private readonly LinkedList<CachedSource> _sources = new();
    private readonly GpuTexture[] _work = new GpuTexture[4];
    private GpuBuffer? _output, _readback, _band;
    private uint _width, _height, _bandRows;
    private int _uniformIndex;
    private readonly RawDisplayGpu _rawDisplay;
    private readonly ComputeKernel _rawWhiteBalance, _rawSemantic;
    private GpuTexture? _maskTexture;
    private RawMaskAlpha? _maskAlpha;

    private GpuTexture? PrepareMask(SourceImage source, ModuleParams p, CancellationToken token)
    {
        var alpha = RawSemanticAdjustments.Prepare(source, p, token);
        if (alpha is null) return null;
        if (ReferenceEquals(alpha, _maskAlpha)) return _maskTexture;
        _maskTexture?.Dispose();
        _maskTexture = null; _maskAlpha = null;
        var texture = _gpu.CreateTexture((uint)alpha.Width, (uint)alpha.Height, GpuTextureFormat.R32Float,
            GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst, "raw semantic alpha");
        try { _gpu.WriteTexture<float>(texture, alpha.Data); }
        catch { texture.Dispose(); throw; }
        _maskTexture = texture; _maskAlpha = alpha;
        return texture;
    }
    private readonly ComputeKernel _decode, _encode8, _encode16, _display, _spike, _blur, _box,
        _crop, _basicLinear, _photoAdjust, _lens, _multi, _double, _bokeh, _zoom, _motion, _vignette,
        _halExtract, _halAdd, _leak, _look, _plate, _foreground,
        _basicDisplay, _tone, _detail, _dirt, _grain, _texture;

    public GpuEffectPipeline(GpuDevice gpu, ShaderLibrary shaders, EffectRendererOptions? options = null)
    {
        _gpu = gpu;
        _shaders = shaders;
        _budget = options is { MemoryBudgetBytes: > 0 } ? options.MemoryBudgetBytes : 768UL * 1024 * 1024;
        _bandCap = options is { MaxBandBytes: > 0 } ? options.MaxBandBytes : 64UL * 1024 * 1024;
        var tex = BindingSpec.TextureFloat;
        var storage = BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float);
        var uniform = BindingSpec.Uniform;
        ComputeKernel K(string path, params BindingSpec[] bindings)
        {
            var kernel = gpu.CreateKernel(shaders.Load(path + ".wgsl"), "main", bindings, path);
            _kernels.Add(kernel);
            return kernel;
        }
        _rawDisplay = new RawDisplayGpu(gpu, shaders);
        _rawWhiteBalance = K("io/raw_white_balance", tex, storage, uniform);
        _rawSemantic = K("io/raw_semantic_adjust", tex, tex, storage, uniform);
        _decode = K("io/decode_rgba16", BindingSpec.TextureUint, storage);
        _foreground = K("io/foreground_alpha", tex, tex, storage);
        _encode8 = K("io/encode_bgra8", tex, BindingSpec.Storage, uniform);
        _encode16 = K("io/encode_rgba16", tex, BindingSpec.Storage, uniform);
        _display = K("io/linear_to_display", tex, storage);
        _spike = K("spike/adjust", tex, storage, uniform);
        _blur = K("common/gaussian", tex, storage, uniform, BindingSpec.StorageRead);
        _box = K("geometry/box_blur", tex, storage);
        _crop = K("geometry/crop", tex, storage, uniform);
        _basicLinear = K("linear/basic_linear", tex, storage, uniform);
        _photoAdjust = K("linear/photo_adjustments", tex, storage, uniform);
        _lens = K("geometry/lens_distortion", tex, tex, storage, uniform);
        _multi = K("geometry/multi_lens", tex, storage, uniform);
        _double = K("optical/double_exposure", tex, tex, storage, uniform);
        _bokeh = K("optical/bokeh", tex, storage, uniform, uniform);
        _zoom = K("optical/zoom_rotate_blur", tex, storage, uniform);
        _motion = K("optical/motion_blur", tex, storage, uniform);
        _vignette = K("optical/vignette", tex, storage, uniform);
        _halExtract = K("optical/halation_extract", tex, storage, uniform);
        _halAdd = K("optical/halation_add", tex, tex, storage, uniform);
        _leak = K("optical/light_leak", tex, storage, uniform);
        _look = K("film/look", tex, tex, storage, uniform);
        _plate = K("film/photo_plate", tex, storage, uniform);
        _basicDisplay = K("linear/basic_display", tex, tex, storage, uniform);
        _tone = K("film/tone", tex, storage, uniform);
        _detail = K("detail/detail_apply", tex, tex, storage, uniform);
        _dirt = K("surface/dirt_scratches", tex, storage, uniform);
        _grain = K("surface/grain", tex, tex, storage, uniform);
        _texture = K("surface/texture_overlay", tex, tex, storage, uniform);
    }

    /// <summary>預覽用：裝置上限內且不超過記憶體預算（預算保護預覽快取與其他程式）。</summary>
    public bool CanRender(SourceImage source, ModuleParams? p = null)
    {
        if (!FitsDevice(source, p)) return false;
        // Four work textures (output size) + linear source + banded output/readback; include external assets.
        var (w, h) = OutputSize(source, p);
        var bytes = WorkspaceBytes(w, h) + CachedSource.BytesFor(source.Width, source.Height);
        if (p?.RawMaskOn == true) bytes += (ulong)source.Width * (ulong)source.Height * 4;
        if (source.ForegroundMask is { } mask)
            bytes += (ulong)source.Width * (ulong)source.Height * 16 + (ulong)mask.Width * (ulong)mask.Height * 4;
        if (p?.DoubleAsset is { } doubleAsset) bytes += AssetBytes(doubleAsset);
        if (p?.LeakAsset is { } leakAsset) bytes += AssetBytes(leakAsset);
        if (p?.DirtAsset is { } dirtAsset) bytes += AssetBytes(dirtAsset);
        return bytes <= _budget;
    }

    /// <summary>只檢查裝置硬性上限（紋理尺寸）；匯出用，記憶體是否足夠改由實際配置結果判定。</summary>
    public bool FitsDevice(SourceImage source, ModuleParams? p = null)
    {
        var limit = _gpu.AdapterInfo.MaxTextureDimension2D;
        bool Fits(SourceImage image) => (uint)image.Width <= limit && (uint)image.Height <= limit;
        var (w, h) = OutputSize(source, p);
        return Fits(source) && w <= limit && h <= limit
            && (source.ForegroundMask is not { } mask || (uint)mask.Width <= limit && (uint)mask.Height <= limit)
            && (p?.DoubleAsset is null || Fits(p.DoubleAsset.Image))
            && (p?.LeakAsset is null || Fits(p.LeakAsset.Image))
            && (p?.DirtAsset is null || Fits(p.DirtAsset.Image));
    }

    /// <summary>算圖結果（工作紋理）的尺寸：裁切後的畫框；沒有參數時同來源。</summary>
    private static (uint Width, uint Height) OutputSize(SourceImage source, ModuleParams? p) =>
        p is null ? ((uint)source.Width, (uint)source.Height) : ((uint)p.OutWidth, (uint)p.OutHeight);

    private static ulong AssetBytes(EffectImage image) => CachedSource.BytesFor(image.Image.Width, image.Image.Height);

    private ulong WorkspaceBytes(uint width, uint height) =>
        (ulong)width * height * 64 + 2 * (ulong)BandRows(width, height) * width * 8;

    /// <summary>
    /// 匯出在同一個 OutOfMemory scope 中配置並算圖，包含效果 LUT、Gaussian 與 uniform。
    /// 回傳 null 表示成功；OOM 時釋放資源並回傳原因；其他 GPU 錯誤直接傳播。
    /// </summary>
    public string? RenderForExport<T>(SourceImage source, ModuleParams p, T[] destination, CancellationToken token) where T : unmanaged
    {
        ClearSources();
        var error = _gpu.TryAllocate(() => Render(source, p, destination, token));
        if (error is not null) ReleaseAll();
        return error;
    }

    /// <summary>釋放工作區與所有快取紋理（下次算圖時重新配置）。</summary>
    public void ReleaseAll()
    {
        ClearSources();
        _rawDisplay.ReleaseWorking();
        _maskTexture?.Dispose(); _maskTexture = null; _maskAlpha = null;
        foreach (var texture in _work) texture?.Dispose();
        Array.Clear(_work);
        _output?.Dispose(); _readback?.Dispose();
        _output = _readback = null;
        _width = _height = _bandRows = 0;
        foreach (var buffer in _uniforms) buffer.Dispose();
        _uniforms.Clear(); _uniformIndex = 0;
        foreach (var buffer in _gaussians.Values) buffer.Dispose();
        _gaussians.Clear();
        foreach (var texture in _luts.Values) texture.Dispose();
        _luts.Clear();
        _band?.Dispose(); _band = null;
    }

    public void Render<T>(SourceImage source, ModuleParams p, T[] destination, CancellationToken token) where T : unmanaged
    {
        token.ThrowIfCancellationRequested();
        // 來源上傳 / 解碼紋理維持原尺寸；工作紋理以輸出（裁切後）尺寸配置
        EnsureWorkspace((uint)p.OutWidth, (uint)p.OutHeight);
        var input = Acquire(source);
        var mask = PrepareMask(source, p, token);
        var second = p.DoubleAsset is null ? null : Acquire(p.DoubleAsset.Image);
        var leak = p.LeakAsset is null ? null : Acquire(p.LeakAsset.Image);
        var dirt = p.DirtAsset is null ? null : Acquire(p.DirtAsset.Image);
        _uniformIndex = 0;
        using var commands = _gpu.BeginCommands();
        var current = Process(commands, input, second, leak, dirt, mask, source, p, token);
        Finish(commands, current, destination, token);
        TrimSources();
    }

    public void RenderSpike(SourceImage source, SpikeParams p, byte[] destination)
    {
        EnsureWorkspace((uint)source.Width, (uint)source.Height);
        var input = Acquire(source);
        _uniformIndex = 0;
        using var commands = _gpu.BeginCommands();
        var current = Next(input);
        Dispatch(commands, _spike, [input, current, Uniform(new Vector4(p.Exposure, p.Brightness, p.Saturation, 0))]);
        var radius = p.BlurRadiusPixels(source.ShortSide);
        if (radius > 0) current = Blur(commands, current, radius);
        var display = Next(current);
        Dispatch(commands, _display, [current, display]);
        Finish(commands, display, destination, default);
        TrimSources();
    }

    private GpuTexture Process(GpuCommandList commands, GpuTexture current, GpuTexture? second,
        GpuTexture? leak, GpuTexture? dirt, GpuTexture? mask, SourceImage source, ModuleParams p, CancellationToken token)
    {
        var shortSide = Math.Min(_width, _height);
        GpuTexture Apply<T>(ComputeKernel kernel, T values, GpuTexture? extra = null, GpuTexture? reserve = null) where T : unmanaged
        {
            token.ThrowIfCancellationRequested();
            var target = Next(current, extra, reserve);
            var uniform = Uniform(values);
            if (extra is null) Dispatch(commands, kernel, [current, target, uniform]);
            else Dispatch(commands, kernel, [current, extra, target, uniform]);
            return target;
        }
        GpuTexture Sharpen(float radiusScale, float amount, GpuTexture? reserve = null)
        {
            var blurred = Blur(commands, current, Math.Max(1, (int)MathF.Round(shortSide * radiusScale)), reserve);
            return Apply(_detail, new Vector4(amount, 0, 0, 0), blurred, reserve);
        }
        // 裁切：來源尺寸的線性紋理 → 輸出尺寸的工作紋理（關閉時不多一個 pass）
        if (p.Crop.On)
        {
            var m = p.CropT.OutToSource;
            current = Apply(_crop, new CropUniforms((float)m.M00, (float)m.M01, (float)m.M02, current.Width,
                (float)m.M10, (float)m.M11, (float)m.M12, current.Height));
        }
        if (source.Scene is not null)
        {
            if (p.RawWhiteBalanceMatrix is { } wb)
            {
                var m = RawFinishParams.WorkingWhiteBalance(wb).ToF32();
                current = Apply(_rawWhiteBalance, new MatrixUniforms(
                    new(m[0], m[1], m[2], 0), new(m[3], m[4], m[5], 0), new(m[6], m[7], m[8], 0)));
            }
            if (mask is not null)
                current = Apply(_rawSemantic, RawSemanticAdjustments.Uniforms(source, p), mask);
        }
        if (p.BasicOn && (p.Exposure != 0 || p.Temperature != 0 || p.Tint != 0))
            current = Apply(_basicLinear, new Vector4(p.Exposure, p.Temperature / 100, p.Tint / 100, 0));
        if (p.SkinOn || p.ZoneOn) current = Apply(_photoAdjust, PhotoAdjustments.Uniforms(p));
        if (p.LensOn)
        {
            var soft = current;
            if (p.EdgeSoften > 0)
            {
                soft = Next(current);
                Dispatch(commands, _box, [current, soft]);
            }
            current = Apply(_lens, new LensUniforms(p.LensAmount / 100, p.Chromatic / 100, p.EdgeSoften / 100,
                p.AutoCrop ? 1 : 0, _width / (float)_height, shortSide, 0, 0), soft);
        }
        if (p.MultiOn) current = Apply(_multi, MultiUniforms.From(p));
        if (p.DoubleOn && p.Opacity > 0)
            current = Apply(_double, new DoubleUniforms(p.DoubleSource, p.DoubleBlend, p.DoubleMirror,
                p.Opacity / 100, p.OffsetX, p.OffsetY, MathF.Max(.01f, p.DoubleScale / 100),
                p.DoubleRotation * MathF.PI / 180, p.ExposureBalance / 100, 0, 0, 0), second ?? current);
        if (p.BokehOn && p.BokehBlur > 0)
        {
            token.ThrowIfCancellationRequested();
            var output = Next(current);
            var angle = p.FocusAngle * MathF.PI / 180;
            var uniform = Uniform(new BokehUniforms(p.BokehCx, p.BokehCy, p.FocusSize / 200,
                MathF.Max(.01f, 1 + p.FocusAspect / 100), MathF.Cos(angle), MathF.Sin(angle),
                MathF.Max(.001f, p.BokehTransition / 100), p.BokehBlur / 100 * shortSide * .03f,
                p.HighlightBoost / 100, shortSide, p.FocusShape, p.BokehShape));
            var samples = UniformBuffer();
            _gpu.WriteBuffer<Vector4>(samples, ApertureSamples.ForShape(p.BokehShape));
            Dispatch(commands, _bokeh, [current, output, uniform, samples]);
            current = output;
        }
        if (p.ZoomRotateOn && (p.Zoom != 0 || p.Rotate != 0))
            current = Apply(_zoom, new ZoomUniforms(p.ZrCx, p.ZrCy, p.Zoom / 100, p.Rotate / 100,
                p.ZrProtect / 100, p.ZrTransition / 100, _width / (float)_height, shortSide));
        if (p.MotionOn && p.MotionLength > 0)
            current = Apply(_motion, new MotionUniforms(p.MotionAngle * MathF.PI / 180, p.MotionLength / 100,
                p.MotionProtectOn ? 1 : 0, p.MotionPx, p.MotionPy, p.MotionSize / 100, shortSide, 0));
        if (p.VignetteOn && p.VigAmount != 0)
            current = Apply(_vignette, new VignetteUniforms(p.VigAmount / 100, p.VigSize / 100,
                p.VigRoundness / 100, p.VigFeather / 100, p.VigCx, p.VigCy, _width / (float)_height, 0));
        if (p.HalationOn && p.HalAmount > 0)
        {
            var values = new HalationUniforms(p.HalAmount / 100, p.HalThreshold / 100, p.HalRadius / 100, p.HalHue, shortSide, 0, 0, 0);
            var extracted = Apply(_halExtract, values);
            var glow = Blur(commands, extracted, Math.Max(1, (int)MathF.Round(shortSide * p.HalRadius / 100 * .08f)), current);
            current = Apply(_halAdd, values, glow);
        }
        if (p.LeakOn && p.LeakAmount > 0)
            current = leak is null
                ? Apply(_leak, new LeakUniforms(0, p.LeakAmount / 100, p.LeakAngle * MathF.PI / 180,
                    p.LeakSize / 100, p.LeakHue, p.LeakIrregularity / 100, p.LeakCount, p.SeedLightLeak,
                    _width / (float)_height, shortSide, 0, 0))
                : Apply(_texture, OverlayUniforms(p, leak, true), leak);
        if (p.FilmOn && p.FilmLut is not null && p.FilmStrength > 0)
            current = Apply(_look, p.FilmLook, LutTexture(p.FilmLut));
        if (p.PlateOn && p.PlateStrength > 0)
            current = Apply(_plate, new PlateUniforms(p.PlateType, p.PlateStrength / 100, p.PlateOrtho / 100,
                p.PlateTintHue, p.PlateTintAmount / 100, p.PlateEdgeErosion / 100, p.PlateStains / 100,
                p.PlateUneven / 100, p.SeedPhotoPlate, _width / (float)_height, 0, 0));
        token.ThrowIfCancellationRequested();
        var display = Next(current);
        if (source.Scene is { } scene) _rawDisplay.Dispatch(commands, current, display, scene.Tone);
        else Dispatch(commands, _display, [current, display]);
        current = display;
        if (p.BasicOn)
        {
            var basis = p.Structure == 0 ? current : Blur(commands, current, Math.Max(1, (int)MathF.Round(shortSide * .012f)));
            current = Apply(_basicDisplay, new BasicUniforms(p.Brightness / 100, p.Contrast / 100,
                p.Highlights / 100, p.Shadows / 100, p.Saturation / 100, p.Structure / 100, shortSide, 0), basis);
        }
        if (p.ToneOn)
            current = Apply(_tone, new ToneUniforms(p.ToneShadowHue, p.ToneShadowAmount / 100,
                p.ToneHighlightHue, p.ToneHighlightAmount / 100, p.ToneBalance / 100, 0, 0, 0));
        if (p.DetailOn && p.Clarity != 0) current = Sharpen(.02f, p.Clarity / 100 * .5f);
        if (p.DetailOn && p.Sharpen != 0) current = Sharpen(.004f, p.Sharpen / 100 * .7f);
        if (p.DirtOn && p.DirtStrength > 0)
            current = dirt is null
                ? Apply(_dirt, new DirtUniforms(0, p.DirtDust / 100, p.DirtScratches / 100, p.DirtHairs / 100,
                    p.Polarity, p.DirtStrength / 100, p.DirtScale / 100, p.SeedDirt, shortSide, 0, 0, 0))
                : Apply(_texture, OverlayUniforms(p, dirt, false), dirt);
        if (p.GrainOn && (p.GrainAmount > 0 || p.GrainSoftness > 0))
        {
            var d = CpuPipeline.DeriveGrain(p, (int)_width, (int)_height);
            var soft = CpuPipeline.GrainSoftBlur(d.Geom.RefShort, p.GrainSoftness / 100, d.Geom.PixelScale);
            var blurred = soft is { } b ? Blur(commands, current, b.Radius, sigma: b.Sigma) : current;
            current = Apply(_grain, new GrainUniforms(
                new Vector4(p.GrainAmount / 100, p.GrainColor / 100, soft is null ? 0 : p.GrainSoftness / 100, d.CorrGain),
                new Vector4(d.SigmaOpt, d.CellOpt, Math.Clamp(p.GrainRadiusSpread, 0, 1), d.CorrScale),
                new Vector4(d.Geom.A00, d.Geom.A01, d.Geom.B0, 0),
                new Vector4(d.Geom.A10, d.Geom.A11, d.Geom.B1, d.R0),
                p.SeedGrain, (uint)p.GrainModel, (uint)p.GrainSamples, (uint)d.RoughQ,
                new Vector4(p.GrainShadows / 100, p.GrainMidtones / 100, p.GrainHighlights / 100, d.Geom.PixelScale)), blurred);
        }
        return current;
    }

    private TextureUniforms OverlayUniforms(ModuleParams p, GpuTexture asset, bool leak)
    {
        var angle = (leak ? p.LeakAngle : SeedHash.ToUnitFloat(SeedHash.Pcg(p.SeedDirt)) * 360) * MathF.PI / 180;
        if ((_width > _height) != (asset.Width > asset.Height)) angle += MathF.PI / 2;
        return new(MathF.Max(.05f, (leak ? p.LeakSize : p.DirtScale) / 100),
            (leak ? p.LeakAmount : p.DirtStrength) / 100, MathF.Cos(angle), MathF.Sin(angle),
            p.LeakHue, p.LeakIrregularity / 100, (p.DirtDust + p.DirtScratches + p.DirtHairs) / 300,
            Math.Min(_width, _height), leak ? Math.Clamp(p.LeakCount, 1, 3) : 1, p.Polarity,
            leak ? p.SeedLightLeak : p.SeedDirt, leak ? 1 : 0);
    }

    private GpuTexture Blur(GpuCommandList commands, GpuTexture input, int radius, GpuTexture? reserve = null, float? sigma = null)
    {
        var key = (radius, sigma ?? MathF.Max(.5f, radius / 3f));
        if (!_gaussians.TryGetValue(key, out var weights))
        {
            var values = GaussianWeights.Get(key.Item1, key.Item2);
            weights = _gpu.CreateBuffer((ulong)values.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "gaussian weights");
            _gpu.WriteBuffer<float>(weights, values);
            _gaussians.Add(key, weights);
        }
        var temp = Next(input, reserve);
        var result = Next(input, temp, reserve);
        Dispatch(commands, _blur, [input, temp, Uniform(new BlurUniforms(1, 0, radius, 0)), weights]);
        Dispatch(commands, _blur, [temp, result, Uniform(new BlurUniforms(0, 1, radius, 0)), weights]);
        return result;
    }

    private GpuTexture LutTexture(FilmLut lut)
    {
        if (_luts.TryGetValue(lut, out var texture)) return texture;
        var n = (uint)lut.Size;
        texture = _gpu.CreateTexture(n * n, n, GpuTextureFormat.Rgba32Float, GpuTextureUsage.CopyDst | GpuTextureUsage.Sampled, "film lut");
        _gpu.WriteTexture<float>(texture, lut.ToTextureRgba());
        _luts.Add(lut, texture);
        return texture;
    }

    private GpuTexture Next(GpuTexture first, GpuTexture? second = null, GpuTexture? third = null)
    {
        foreach (var texture in _work)
            if (texture != first && texture != second && texture != third) return texture;
        throw new InvalidOperationException("No non-aliasing GPU work texture.");
    }

    private void Dispatch(GpuCommandList commands, ComputeKernel kernel, ReadOnlySpan<object> bindings) =>
        commands.Dispatch2D(kernel, bindings, _width, _height);

    private GpuBuffer Uniform<T>(in T value) where T : unmanaged
    {
        var result = UniformBuffer();
        _gpu.WriteBuffer(result, value);
        return result;
    }

    private GpuBuffer UniformBuffer()
    {
        // Queue writes occur before command submission: each recorded pass needs its own buffer.
        if (_uniformIndex == _uniforms.Count)
            _uniforms.Add(_gpu.CreateBuffer(512, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "effect parameters"));
        return _uniforms[_uniformIndex++];
    }

    /// <summary>
    /// 以列帶編碼並讀回：第一帶與效果 pass 同一次提交，其餘每帶各自提交。
    /// 輸出緩衝區只需容納一帶，大影像不受 maxStorageBufferBindingSize 限制，也不需整張大小的讀回緩衝區。
    /// </summary>
    private void Finish<T>(GpuCommandList commands, GpuTexture current, T[] destination, CancellationToken token) where T : unmanaged
    {
        var encode = typeof(T) == typeof(ushort) ? _encode16 : _encode8;
        var perPixel = destination.Length / ((int)_width * (int)_height); // RGBA16 與 BGRA8 皆為每像素 4 個元素
        _band ??= _gpu.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "encode band");
        GpuCommandList? pending = commands;
        for (uint row = 0; row < _height; row += _bandRows)
        {
            token.ThrowIfCancellationRequested();
            var rows = Math.Min(_bandRows, _height - row);
            using var list = pending ?? _gpu.BeginCommands();
            pending = null;
            _gpu.WriteBuffer(_band, new BandUniforms(row, rows, 0, 0));
            list.Dispatch2D(encode, [current, _output!, _band], _width, rows);
            var count = (int)(rows * _width) * perPixel;
            list.CopyBuffer(_output!, _readback!, (ulong)count * (ulong)Marshal.SizeOf<T>());
            list.Submit();
            _gpu.ReadBuffer(_readback!, destination, (int)(row * _width) * perPixel, count);
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>每帶列數：輸出緩衝區不超過裝置的儲存/緩衝區上限與列帶上限（預設 64 MB）。</summary>
    private uint BandRows(uint width, uint height)
    {
        var info = _gpu.AdapterInfo;
        var cap = _bandCap;
        if (info.MaxStorageBufferBindingSize > 0) cap = Math.Min(cap, info.MaxStorageBufferBindingSize);
        if (info.MaxBufferSize > 0) cap = Math.Min(cap, info.MaxBufferSize);
        var rows = cap / ((ulong)width * 8);
        return (uint)Math.Clamp(rows, 1UL, height);
    }

    private void EnsureWorkspace(uint width, uint height)
    {
        if (_width == width && _height == height) return;
        foreach (var texture in _work) texture?.Dispose();
        _output?.Dispose(); _readback?.Dispose();
        _width = width; _height = height;
        _bandRows = BandRows(_width, _height);
        for (var i = 0; i < _work.Length; i++)
            _work[i] = _gpu.CreateTexture(_width, _height, GpuTextureFormat.Rgba32Float, GpuTextureUsage.Sampled | GpuTextureUsage.Storage, "effect work");
        var size = (ulong)_width * _bandRows * 8;
        _output = _gpu.CreateBuffer(size, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc, "effect output band");
        _readback = _gpu.CreateBuffer(size, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "effect readback band");
        TrimSources();
    }

    private GpuTexture Acquire(SourceImage source)
    {
        for (var node = _sources.First; node is not null; node = node.Next)
            if (ReferenceEquals(node.Value.Source, source))
            {
                _sources.Remove(node); _sources.AddFirst(node);
                return node.Value.Linear;
            }
        var uploaded = new CachedSource(_gpu, source);
        try
        {
            if (source.ForegroundMask is { } mask)
            {
                // 遮罩維持原尺寸上傳，由 GPU 依來源像素中心取樣；不在 CPU 放大成整張照片。
                // 解碼後只乘 alpha，先於任何幾何 pass，RGB 維持 straight。
                ForegroundAlpha.RequireCompatible(source, mask);
                var coverage = ForegroundAlpha.ToFloatPlane(mask);
                using var maskTex = _gpu.CreateTexture((uint)mask.Width, (uint)mask.Height, GpuTextureFormat.R32Float,
                    GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst, "foreground mask");
                _gpu.WriteTexture<float>(maskTex, coverage);
                using var staged = _gpu.CreateTexture((uint)source.Width, (uint)source.Height, GpuTextureFormat.Rgba32Float,
                    GpuTextureUsage.Sampled | GpuTextureUsage.Storage | GpuTextureUsage.CopyDst, "masked staging");
                DecodeSource(source, staged);
                using var commands = _gpu.BeginCommands();
                commands.Dispatch2D(_foreground, [staged, maskTex, uploaded.Linear],
                    (uint)source.Width, (uint)source.Height);
                commands.Submit();
            }
            else DecodeSource(source, uploaded.Linear);
        }
        catch
        {
            uploaded.Dispose();
            throw;
        }
        _sources.AddFirst(uploaded);
        return uploaded.Linear;
    }

    /// <summary>解碼來源到指定線性紋理並送出（SDR 上傳 16-bit 紋理解碼；RAW 先 CPU 色彩轉換再寫入）。</summary>
    private void DecodeSource(SourceImage source, GpuTexture destination)
    {
        if (source.Scene is not null)
        {
            // Only the source color-space conversion runs on CPU; all editing and finishing stays on GPU.
            var working = CpuPipeline.DecodeLinear(source);
            _gpu.WriteTexture<float>(destination, working);
            return;
        }
        // 16-bit 上傳紋理只用於解碼；同一指令清單送出後立即釋放，快取只保留線性紋理
        using var upload = _gpu.CreateTexture((uint)source.Width, (uint)source.Height, GpuTextureFormat.Rgba16Uint,
            GpuTextureUsage.CopyDst | GpuTextureUsage.Sampled, "source upload");
        _gpu.WriteTexture<ushort>(upload, source.Rgba16);
        using var commands = _gpu.BeginCommands();
        commands.Dispatch2D(_decode, [upload, destination], (uint)source.Width, (uint)source.Height);
        commands.Submit();
    }

    private void TrimSources()
    {
        var bytes = WorkspaceBytes(_width, _height);
        foreach (var source in _sources) bytes += source.Bytes;
        while (_sources.Count > 0 && (_sources.Count > 6 || bytes > _budget))
        {
            var last = _sources.Last!.Value;
            bytes -= last.Bytes;
            last.Dispose(); _sources.RemoveLast();
        }
    }

    private void ClearSources()
    {
        foreach (var source in _sources) source.Dispose();
        _sources.Clear();
    }

    public void Evict(SourceImage source)
    {
        for (var node = _sources.First; node is not null; node = node.Next)
            if (ReferenceEquals(node.Value.Source, source))
            {
                node.Value.Dispose(); _sources.Remove(node); return;
            }
    }

    private sealed class CachedSource : IDisposable
    {
        public SourceImage Source { get; }
        public GpuTexture Linear { get; }
        public ulong Bytes => BytesFor(Source.Width, Source.Height);
        public static ulong BytesFor(int width, int height) => (ulong)width * (ulong)height * 16;
        public CachedSource(GpuDevice gpu, SourceImage source)
        {
            Source = source;
            Linear = gpu.CreateTexture((uint)source.Width, (uint)source.Height, GpuTextureFormat.Rgba32Float, GpuTextureUsage.Sampled | GpuTextureUsage.Storage | GpuTextureUsage.CopyDst, "source linear");
        }
        public void Dispose() => Linear.Dispose();
    }

    public void Dispose()
    {
        _rawDisplay.Dispose();
        _maskTexture?.Dispose();
        foreach (var source in _sources) source.Dispose();
        foreach (var texture in _work) texture?.Dispose();
        foreach (var buffer in _uniforms) buffer.Dispose();
        foreach (var buffer in _gaussians.Values) buffer.Dispose();
        foreach (var texture in _luts.Values) texture.Dispose();
        foreach (var kernel in _kernels) kernel.Dispose();
        _output?.Dispose(); _readback?.Dispose(); _band?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct MatrixUniforms(Vector4 Row0, Vector4 Row1, Vector4 Row2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BandUniforms(uint Row, uint Rows, uint Pad0, uint Pad1);
    /// <summary>geometry/crop.wgsl：OutToSource 仿射（列主序）＋ 來源尺寸，32 位元組。</summary>
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CropUniforms(float M00, float M01, float M02, float SrcW, float M10, float M11, float M12, float SrcH);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BlurUniforms(int X, int Y, int Radius, float Pad);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct LensUniforms(float Amount, float Chromatic, float Soften, float Crop, float Aspect, float ShortSide, float Pad0, float Pad1);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct DoubleUniforms(int Source, int Blend, int Mirror, float Opacity, float X, float Y, float Scale, float Rotation, float Exposure, float Pad0, float Pad1, float Pad2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BokehUniforms(float X, float Y, float Focus, float Aspect, float Cosine, float Sine, float Transition, float Radius, float Boost, float ShortSide, int FocusShape, int Shape);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ZoomUniforms(float X, float Y, float Zoom, float Rotate, float Protect, float Transition, float Aspect, float ShortSide);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MotionUniforms(float Angle, float Length, float Protect, float X, float Y, float Size, float ShortSide, float Pad);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct VignetteUniforms(float Amount, float Size, float Roundness, float Feather, float X, float Y, float Aspect, float Pad);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct HalationUniforms(float Amount, float Threshold, float Radius, float Hue, float ShortSide, float Pad0, float Pad1, float Pad2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct LeakUniforms(int Mode, float Amount, float Angle, float Size, float Hue, float Irregularity, int Count, uint Seed, float Aspect, float ShortSide, uint Pad0, uint Pad1);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PlateUniforms(int Type, float Strength, float Ortho, float Hue, float Tint, float Erosion, float Stains, float Uneven, uint Seed, float Aspect, uint Pad0, uint Pad1);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BasicUniforms(float Brightness, float Contrast, float Highlights, float Shadows, float Saturation, float Structure, float ShortSide, float Pad);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ToneUniforms(float ShadowHue, float ShadowAmount, float HighlightHue, float HighlightAmount, float Balance, float Pad0, float Pad1, float Pad2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct DirtUniforms(int Mode, float Dust, float Scratches, float Hairs, int Polarity, float Strength, float Scale, uint Seed, float ShortSide, uint Pad0, uint Pad1, uint Pad2);

    /// <summary>surface/grain.wgsl：A 為效果參數、B 為光學模型常數、MapX/MapY 為輸出→來源參考仿射（w 欄放 r0）、Tone 為亮度帶權重＋像素足跡；96 位元組。</summary>
    [StructLayout(LayoutKind.Sequential)] private readonly record struct GrainUniforms(Vector4 A, Vector4 B, Vector4 MapX, Vector4 MapY, uint Seed, uint Model, uint Samples, uint RoughQ, Vector4 Tone);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct TextureUniforms(float Scale, float Amount, float Cosine, float Sine, float Hue, float Irregularity, float Density, float ShortSide, int Count, int Polarity, uint Seed, int Leak);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MultiUniforms(int Layout, float Divider, Vector2 Pad, Vector4 Cell0, Vector4 Tint0, Vector4 Cell1, Vector4 Tint1, Vector4 Cell2, Vector4 Tint2, Vector4 Cell3, Vector4 Tint3)
    {
        public static MultiUniforms From(ModuleParams p)
        {
            (Vector4 Cell, Vector4 Tint) Cell(int i)
            {
                var seed = p.SeedMultiLens + (uint)i * 747796405u;
                var dx = (SeedHash.ToUnitFloat(SeedHash.Pcg(seed)) - .5f) * 2 * p.MultiOffset / 100;
                var dy = (SeedHash.ToUnitFloat(SeedHash.Pcg(seed + 1)) - .5f) * 2 * p.MultiOffset / 100;
                if (p.MultiLayout == 5) dy += (i % 2 == 0 ? .25f : -.25f) * .3f;
                var zoom = 1 + (SeedHash.ToUnitFloat(SeedHash.Pcg(seed + 2)) - .5f) * .3f * p.ZoomVar / 100;
                var exposure = (SeedHash.ToUnitFloat(SeedHash.Pcg(seed + 3)) - .5f) * 1.5f * p.ExposureVar / 100;
                var (r, g, b) = CpuPipeline.HsvToRgb(SeedHash.ToUnitFloat(SeedHash.Pcg(seed + 4)) * 360, p.ColorVar / 100 * .35f, 1);
                return (new(dx, dy, zoom, exposure), new(r, g, b, 0));
            }
            var a = Cell(0); var b = Cell(1); var c = Cell(2); var d = Cell(3);
            return new(p.MultiLayout, p.Divider / 100, Vector2.Zero, a.Cell, a.Tint, b.Cell, b.Tint, c.Cell, c.Tint, d.Cell, d.Tint);
        }
    }
}
