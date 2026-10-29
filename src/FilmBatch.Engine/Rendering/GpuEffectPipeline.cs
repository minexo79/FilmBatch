using System.Numerics;
using System.Runtime.InteropServices;
using FilmBatch.Core.Curves;
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
    // 色調曲線查表（3 × ToneCurveSet.Size 個 float）：第一次用到曲線時才配置，之後每次算圖覆寫重用。
    private GpuBuffer? _curveTable;
    private ComputeKernel? _proPhotoOutput;
    private uint _width, _height, _bandRows;
    private int _uniformIndex;
    private ulong _pendingBlurSamples;
    private readonly RawDisplayGpu _rawDisplay;
    private ColorTransformKernel? _colorTransform;
    private readonly ComputeKernel _rawWhiteBalance, _rawSemantic;
    private GpuTexture? _maskTexture;
    private RawMaskAlpha? _maskAlpha;
    // 以畫框為座標系的 kernel：最後一個 binding 是共用的 view uniform（畫框尺寸＋渲染區域原點，見 common/sampling.wgsl）。
    private readonly HashSet<ComputeKernel> _framed = [];
    private GpuBuffer? _view;

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
    private readonly ComputeKernel _decode, _encode8, _encode16, _display, _spike, _blur, _blurTile, _box,
        _crop, _basicLinear, _photoAdjust, _shadowStats, _shadowBox, _shadowApply, _highlightRolloff, _lens, _multi, _double, _bokeh, _zoom, _motion, _vignette,
        _halExtract, _halAdd, _leak, _look, _hsl, _plate, _foreground,
        _basicDisplay, _toneCurves, _tone, _detail, _dirt, _grain, _texture;

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
        // 畫框相對的 pass：多一個 view uniform binding，由 Dispatch 自動附上
        ComputeKernel F(string path, params BindingSpec[] bindings)
        {
            var kernel = K(path, [.. bindings, uniform]);
            _framed.Add(kernel);
            return kernel;
        }
        _rawDisplay = new RawDisplayGpu(gpu, shaders);
        _rawWhiteBalance = K("io/raw_white_balance", tex, storage, uniform);
        _rawSemantic = F("io/raw_semantic_adjust", tex, tex, storage, uniform);
        _decode = K("io/decode_rgba16", BindingSpec.TextureUint, storage);
        _foreground = K("io/foreground_alpha", tex, tex, storage);
        _encode8 = K("io/encode_bgra8", tex, BindingSpec.Storage, uniform);
        _encode16 = K("io/encode_rgba16", tex, BindingSpec.Storage, uniform);
        _display = K("io/linear_to_display", tex, storage, uniform);
        _spike = K("spike/adjust", tex, storage, uniform);
        _blur = K("common/gaussian", tex, storage, uniform, BindingSpec.StorageRead);
        _blurTile = K("common/gaussian_tile", tex, storage, uniform, BindingSpec.StorageRead);
        _box = K("geometry/box_blur", tex, storage);
        _crop = F("geometry/crop", tex, storage, uniform);
        _basicLinear = K("linear/basic_linear", tex, storage, uniform);
        _photoAdjust = F("linear/photo_adjustments", tex, storage, uniform);
        _shadowStats = K("linear/shadow_stats", tex, storage, uniform);
        _shadowBox = K("linear/shadow_box", tex, storage, uniform);
        _shadowApply = K("linear/shadow_apply", tex, tex, storage, uniform);
        _highlightRolloff = K("linear/highlight_rolloff", tex, storage, uniform);
        _lens = F("geometry/lens_distortion", tex, tex, storage, uniform);
        _multi = F("geometry/multi_lens", tex, storage, uniform);
        _double = F("optical/double_exposure", tex, tex, storage, uniform);
        _bokeh = F("optical/bokeh", tex, storage, uniform, uniform);
        _zoom = F("optical/zoom_rotate_blur", tex, storage, uniform);
        _motion = F("optical/motion_blur", tex, storage, uniform);
        _vignette = F("optical/vignette", tex, storage, uniform);
        _halExtract = K("optical/halation_extract", tex, storage, uniform);
        _halAdd = K("optical/halation_add", tex, tex, storage, uniform);
        _leak = F("optical/light_leak", tex, storage, uniform);
        _look = K("film/look", tex, tex, storage, uniform);
        _hsl = K("linear/hsl", tex, storage, uniform);
        _plate = F("film/photo_plate", tex, storage, uniform);
        _basicDisplay = K("linear/basic_display", tex, tex, storage, uniform);
        _toneCurves = K("linear/tone_curves", tex, storage, uniform, BindingSpec.StorageRead);
        _tone = K("film/tone", tex, storage, uniform);
        _detail = K("detail/detail_apply", tex, tex, storage, uniform);
        _dirt = F("surface/dirt_scratches", tex, storage, uniform);
        _grain = F("surface/grain", tex, tex, storage, uniform);
        _texture = F("surface/texture_overlay", tex, tex, storage, uniform);
    }

    /// <summary>預覽用：裝置上限內且不超過記憶體預算（預算保護預覽快取與其他程式）。</summary>
    public bool CanRender(SourceImage source, ModuleParams? p = null)
    {
        if (!FitsDevice(source, p)) return false;
        // Four work textures (output size) + linear source + banded output/readback; include external assets.
        var (w, h) = OutputSize(source, p);
        return WorkspaceBytes(w, h) + ResidentBytes(source, p) <= _budget;
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
            && (p?.DirtAsset is null || Fits(p.DirtAsset.Image))
            && (ActiveTransform(p) is not { } asset || ColorTransformKernel.Fits(asset.Transform, StorageLimit()))
            && FitsProfileTone(source);
    }

    private ulong StorageLimit()
    {
        var binding = _gpu.AdapterInfo.MaxStorageBufferBindingSize;
        var buffer = _gpu.AdapterInfo.MaxBufferSize;
        return binding == 0 ? buffer : buffer == 0 ? binding : Math.Min(binding, buffer);
    }

    private bool FitsProfileTone(SourceImage source)
    {
        var bytes = RawDisplayGpu.ProfileToneBytes(source.Scene?.ProfileTone);
        var limit = StorageLimit();
        return limit == 0 || bytes <= limit;
    }

    private static ColorTransformAsset? ActiveTransform(ModuleParams? p) =>
        p is { FilmOn: true, FilmStrength: > 0 } ? p.FilmAsset?.Transform : null;

    /// <summary>算圖結果（工作紋理）的尺寸：裁切後的畫框；沒有參數時同來源。</summary>
    private static (uint Width, uint Height) OutputSize(SourceImage source, ModuleParams? p) =>
        p is null ? ((uint)source.Width, (uint)source.Height) : ((uint)p.OutWidth, (uint)p.OutHeight);

    private static ulong AssetBytes(EffectImage image) => CachedSource.BytesFor(image.Image.Width, image.Image.Height);

    private ulong WorkspaceBytes(uint width, uint height) =>
        (ulong)width * height * 64 + 2 * (ulong)BandRows(width, height) * width * 8;

    /// <summary>
    /// 來源常駐位元組（整張線性來源＋RAW 語意遮罩＋去背暫存＋外部素材）：分塊只縮小工作區，
    ///    來源一律整張保留，不因分塊而裁切或重算。
    /// </summary>
    private ulong ResidentBytes(SourceImage source, ModuleParams? p)
    {
        var bytes = CachedSource.BytesFor(source.Width, source.Height);
        if (p?.RawMaskOn == true) bytes += (ulong)source.Width * (ulong)source.Height * 4;
        if (source.ForegroundMask is { } mask)
            bytes += (ulong)source.Width * (ulong)source.Height * 16 + (ulong)mask.Width * (ulong)mask.Height * 4;
        if (p?.DoubleAsset is { } doubleAsset) bytes += AssetBytes(doubleAsset);
        if (p?.LeakAsset is { } leakAsset) bytes += AssetBytes(leakAsset);
        if (p?.DirtAsset is { } dirtAsset) bytes += AssetBytes(dirtAsset);
        bytes += _colorTransform?.ResidentBytes ?? 0;
        if (ActiveTransform(p) is { } asset && _colorTransform?.CurrentKey != asset.Key)
            bytes += ColorTransformKernel.RequiredBytes(asset.Transform);
        bytes += Math.Max(_rawDisplay.ProfileResidentBytes, RawDisplayGpu.ProfileToneBytes(source.Scene?.ProfileTone));
        return bytes;
    }

    /// <summary>
    /// 單一區域超過預算時沿長邊對半切分；呼叫端提供最多四塊的暫存。
    /// 每塊各自 Expand，工作區加整張來源與素材皆須符合預算及裝置上限。
    /// 回傳可用塊數；無法容納（包含 nonlocal 效果的完整取樣範圍）時回傳 0。
    /// </summary>
    internal int PlanTiles(SourceImage source, ModuleParams frame, RenderView view,
        Span<(RenderView Tile, RenderView Region)> plan)
    {
        var limit = _gpu.AdapterInfo.MaxTextureDimension2D;
        bool Fits(SourceImage image) => (uint)image.Width <= limit && (uint)image.Height <= limit;
        if (!Fits(source)) return 0;
        if (source.ForegroundMask is { } mask && ((uint)mask.Width > limit || (uint)mask.Height > limit)) return 0;
        if (frame.DoubleAsset is not null && !Fits(frame.DoubleAsset.Image)) return 0;
        if (frame.LeakAsset is not null && !Fits(frame.LeakAsset.Image)) return 0;
        if (frame.DirtAsset is not null && !Fits(frame.DirtAsset.Image)) return 0;
        if (ActiveTransform(frame) is { } asset && !ColorTransformKernel.Fits(asset.Transform, StorageLimit())) return 0;
        if (!FitsProfileTone(source)) return 0;
        var resident = ResidentBytes(source, frame);
        if (resident >= _budget) return 0;
        plan[0] = (view, default);
        for (var count = 1; count * 2 <= plan.Length; count *= 2)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                var tile = plan[i].Tile;
                if (tile.W >= tile.H)
                {
                    if (tile.W <= 1) return 0;
                    var w0 = tile.W / 2;
                    plan[i * 2] = (tile with { W = w0 }, default);
                    plan[i * 2 + 1] = (tile with { X = tile.X + w0, W = tile.W - w0 }, default);
                }
                else
                {
                    if (tile.H <= 1) return 0;
                    var h0 = tile.H / 2;
                    plan[i * 2] = (tile with { H = h0 }, default);
                    plan[i * 2 + 1] = (tile with { Y = tile.Y + h0, H = tile.H - h0 }, default);
                }
            }
            var fits = true;
            for (var i = 0; i < count * 2; i++)
            {
                var region = WindowRegion.Expand(frame, plan[i].Tile);
                plan[i].Region = region;
                if ((uint)region.W > limit || (uint)region.H > limit ||
                    resident + WorkspaceBytes((uint)region.W, (uint)region.H) > _budget)
                    fits = false;
            }
            if (fits) return count * 2;
        }
        return 0;
    }

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
        _colorTransform?.Dispose(); _colorTransform = null;
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
        _curveTable?.Dispose(); _curveTable = null;
        _view?.Dispose(); _view = null;
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
        _pendingBlurSamples = 0;
        // 整個算圖共用一份（模糊分批 Flush 會重用一般 uniform 池，不能放在池裡）
        _view ??= _gpu.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "frame view");
        _gpu.WriteBuffer(_view, new ViewUniforms(p.FrameWidth, p.FrameHeight, p.View.X, p.View.Y));
        var transform = ActiveTransform(p);
        var completed = false;
        try
        {
            using var commands = _gpu.BeginCommands();
            var current = Process(commands, input, second, leak, dirt, mask, source, p, token);
            Finish(commands, current, destination, token);
            completed = true;
        }
        finally
        {
            if (transform is not null && _colorTransform is not null && !_gpu.IsLost)
            {
                // On an abandoned command list a previous Flush may already have submitted work.
                if (!completed) _gpu.WaitIdle();
                _colorTransform.ReleaseCompleted();
            }
        }
        TrimSources();
    }

    /// <summary>
    /// 分塊窗口渲染：<paramref name="plan"/> 來自 <see cref="PlanTiles"/>，每塊各自 Expand 求區域後以全域畫框座標算圖，
    /// 再把 tile 精確拼回 <paramref name="view"/>（無接縫：每塊的輸出只取自己的 tile 範圍，halo 只用於輸入鄰域）。
    /// 工作區逐塊重用，來源紋理整張保留；任一塊取消即丟出。
    /// </summary>
    public void RenderTiled(SourceImage source, ModuleParams frame, RenderView view,
        ReadOnlySpan<(RenderView Tile, RenderView Region)> plan, byte[] destination, CancellationToken token)
    {
        if (destination.Length != checked(view.W * view.H * 4))
            throw new ArgumentException("Destination must be view.W × view.H × 4 bytes.", nameof(destination));
        var originalView = frame.View;
        byte[]? pixels = null;
        try
        {
            foreach (var (tile, region) in plan)
            {
                token.ThrowIfCancellationRequested();
                frame.View = region;
                var length = checked(region.W * region.H * 4);
                if (pixels?.Length != length) pixels = new byte[length];
                Render(source, frame, pixels, token);
                for (var y = 0; y < tile.H; y++)
                    Array.Copy(pixels, ((tile.Y - region.Y + y) * region.W + tile.X - region.X) * 4,
                        destination, ((tile.Y - view.Y + y) * view.W + tile.X - view.X) * 4, tile.W * 4);
            }
        }
        finally { frame.View = originalView; }
    }

    public void RenderSpike(SourceImage source, SpikeParams p, byte[] destination)
    {
        EnsureWorkspace((uint)source.Width, (uint)source.Height);
        var input = Acquire(source);
        _uniformIndex = 0;
        _pendingBlurSamples = 0;
        using var commands = _gpu.BeginCommands();
        var current = Next(input);
        Dispatch(commands, _spike, [input, current, Uniform(new Vector4(p.Exposure, p.Brightness, p.Saturation, 0))]);
        var radius = p.BlurRadiusPixels(source.ShortSide);
        if (radius > 0) current = Blur(commands, current, radius);
        var display = Next(current);
        Dispatch(commands, _display, [current, display, Uniform(new DisplayUniforms(0, 0, 0, 0))]);
        Finish(commands, display, destination, default);
        TrimSources();
    }

    private GpuTexture Process(GpuCommandList commands, GpuTexture current, GpuTexture? second,
        GpuTexture? leak, GpuTexture? dirt, GpuTexture? mask, SourceImage source, ModuleParams p, CancellationToken token)
    {
        // 畫框相對量一律以畫框計算（窗口模式下 _width × _height 只是渲染區域）
        var shortSide = (uint)Math.Min(p.FrameWidth, p.FrameHeight);
        var aspect = p.FrameWidth / (float)p.FrameHeight;
        GpuTexture Apply<T>(ComputeKernel kernel, T values, GpuTexture? extra = null, GpuTexture? reserve = null) where T : unmanaged
        {
            token.ThrowIfCancellationRequested();
            var target = Next(current, extra, reserve);
            var uniform = Uniform(values);
            if (extra is null) Dispatch(commands, kernel, [current, target, uniform]);
            else Dispatch(commands, kernel, [current, extra, target, uniform]);
            return target;
        }
        GpuTexture ApplyTransform(ColorTransformAsset asset)
        {
            token.ThrowIfCancellationRequested();
            var target = Next(current);
            _colorTransform ??= new ColorTransformKernel(_gpu, _shaders);
            _colorTransform.Prepare(asset.Key, asset.Transform, StorageLimit());
            var to = RgbEncodingInfo.SrgbToProPhoto;
            var from = RgbEncodingInfo.ProPhotoToSrgb;
            var values = new ColorTransformUniforms
            {
                Mix = new Vector4(p.FilmStrength / 100, p.FilmWarmth / 100, p.FilmFade / 100, (float)asset.Options.Stage),
                Domain = new Vector4((float)asset.Options.Input, (float)asset.Options.Output, 0, 0),
                To0 = new Vector4(to[0], to[1], to[2], 0), To1 = new Vector4(to[3], to[4], to[5], 0),
                To2 = new Vector4(to[6], to[7], to[8], 0),
                From0 = new Vector4(from[0], from[1], from[2], 0), From1 = new Vector4(from[3], from[4], from[5], 0),
                From2 = new Vector4(from[6], from[7], from[8], 0),
            };
            _colorTransform.Encode(commands, current, target, Uniform(values), token);
            return target;
        }
        GpuTexture Sharpen(float radiusScale, float amount, GpuTexture? reserve = null)
        {
            var blurred = Blur(commands, current, Math.Max(1, (int)MathF.Round(shortSide * radiusScale)), reserve, token: token);
            return Apply(_detail, new Vector4(amount, 0, 0, 0), blurred, reserve);
        }
        // 裁切：來源尺寸的線性紋理 → 輸出尺寸的工作紋理（關閉時不多一個 pass）。
        // 窗口模式下一律執行：取出渲染區域（畫框像素 + 區域原點 → 來源像素）。
        if (p.Crop.On || p.IsWindow)
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
        if (p.ShadowRecoveryOn) current = RecoverShadows(commands, current, p, token);
        if (p.LensOn)
        {
            var soft = current;
            if (p.EdgeSoften > 0)
            {
                soft = Next(current);
                Dispatch(commands, _box, [current, soft]);
            }
            current = Apply(_lens, new LensUniforms(p.LensAmount / 100, p.Chromatic / 100, p.EdgeSoften / 100,
                p.AutoCrop ? 1 : 0, aspect, shortSide, 0, 0), soft);
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
                p.ZrProtect / 100, p.ZrTransition / 100, aspect, shortSide,
                p.ZrProtectShape, ShapeMask.Aspect(p.ZrProtectAspect), MathF.Cos(p.ZrProtectAngle * MathF.PI / 180), MathF.Sin(p.ZrProtectAngle * MathF.PI / 180)));
        if (p.MotionOn && p.MotionLength > 0)
            current = Apply(_motion, new MotionUniforms(p.MotionAngle * MathF.PI / 180, p.MotionLength / 100,
                p.MotionProtectOn ? 1 : 0, p.MotionPx, p.MotionPy, p.MotionSize / 100, shortSide,
                p.MotionProtectShape, ShapeMask.Aspect(p.MotionProtectAspect),
                MathF.Cos(p.MotionProtectAngle * MathF.PI / 180), MathF.Sin(p.MotionProtectAngle * MathF.PI / 180)));
        if (p.VignetteOn && p.VigAmount != 0)
            current = Apply(_vignette, new VignetteUniforms(p.VigAmount / 100, p.VigSize / 100,
                p.VigRoundness / 100, p.VigFeather / 100, p.VigCx, p.VigCy, aspect, 0));
        if (p.HalationOn && p.HalAmount > 0)
        {
            var values = new HalationUniforms(p.HalAmount / 100, p.HalThreshold / 100, p.HalRadius / 100, p.HalHue, shortSide, p.HalSaturation / 100, 0, 0);
            var extracted = Apply(_halExtract, values);
            var glow = Blur(commands, extracted, Math.Max(1, (int)MathF.Round(shortSide * p.HalRadius / 100 * .08f)), current, token: token);
            current = Apply(_halAdd, values, glow);
        }
        if (p.LeakOn && p.LeakAmount > 0)
            current = leak is null
                ? Apply(_leak, new LeakUniforms(0, p.LeakAmount / 100, p.LeakAngle * MathF.PI / 180,
                    p.LeakSize / 100, p.LeakHue, p.LeakIrregularity / 100, p.LeakCount, p.SeedLightLeak,
                    aspect, shortSide, p.LeakBlend, p.LeakRed / 100, p.LeakYellow / 100,
                    p.LeakBlue / 100, p.LeakWhite / 100, 0))
                : Apply(_texture, OverlayUniforms(p, leak, true), leak);
        if (p.FilmOn && p.FilmAsset?.Lut is { } filmLut && p.FilmStrength > 0)
            current = Apply(_look, p.FilmLook, LutTexture(filmLut));
        else if (ActiveTransform(p) is { Options.Stage: ColorTransformStage.WorkingLinear } working)
            current = ApplyTransform(working);
        if (p.HslOn)
            current = Apply(_hsl, p.Hsl);
        if (p.PlateOn && p.PlateStrength > 0)
            current = Apply(_plate, new PlateUniforms(p.PlateType, p.PlateStrength / 100, p.PlateOrtho / 100,
                p.PlateTintHue, p.PlateTintAmount / 100, p.PlateEdgeErosion / 100, p.PlateStains / 100,
                p.PlateUneven / 100, p.SeedPhotoPlate, aspect, 0, 0));
        token.ThrowIfCancellationRequested();
        if (p.HighlightRolloffOn)
            current = Apply(_highlightRolloff, HighlightRolloff.Uniforms(p.HighlightRolloffSlope));
        var display = Next(current);
        var proPhoto = p.OutputEncoding == RgbEncoding.ProPhoto;
        var displayTransform = ActiveTransform(p) is { Options.Stage: ColorTransformStage.DisplayEncoded };
        var extended = proPhoto || displayTransform;
        if (source.Scene is { } scene) _rawDisplay.Dispatch(commands, current, display, scene.Tone, extended, scene.ProfileTone);
        else Dispatch(commands, _display, [current, display, Uniform(new DisplayUniforms(extended ? 1u : 0u, 0, 0, 0))]);
        current = display;
        if (displayTransform) current = ApplyTransform(p.FilmAsset!.Transform!);
        if (p.BasicOn)
        {
            var basis = p.Structure == 0 ? current : Blur(commands, current, Math.Max(1, (int)MathF.Round(shortSide * .012f)), token: token);
            current = Apply(_basicDisplay, new BasicUniforms(p.Brightness / 100, p.Contrast / 100,
                p.Highlights / 100, p.Shadows == 0 ? 0 : MathF.Pow(2f, -2f * p.Shadows / 100) - 1f,
                p.Saturation / 100, p.Structure / 100, shortSide, 0), basis);
        }
        if (p.Curves is { } curves)
        {
            token.ThrowIfCancellationRequested();
            var curved = Next(current);
            Dispatch(commands, _toneCurves, [current, curved,
                Uniform(new CurveUniforms(curves.RefineSaturation, ToneCurveSet.Size, 0, 0)), CurveTable(curves)]);
            current = curved;
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
            var d = CpuPipeline.DeriveGrain(p, p.FrameWidth, p.FrameHeight);
            var soft = CpuPipeline.GrainSoftBlur(d.Geom.RefShort, p.GrainSoftness / 100, d.Geom.PixelScale);
            var blurred = soft is { } b ? Blur(commands, current, b.Radius, sigma: b.Sigma, token: token) : current;
            current = Apply(_grain, new GrainUniforms(
                new Vector4(p.GrainAmount / 100, p.GrainColor / 100, soft is null ? 0 : p.GrainSoftness / 100, d.CorrGain),
                new Vector4(d.SigmaOpt, d.CellOpt, Math.Clamp(p.GrainRadiusSpread, 0, 1), d.CorrScale),
                new Vector4(d.Geom.A00, d.Geom.A01, d.Geom.B0, 0),
                new Vector4(d.Geom.A10, d.Geom.A11, d.Geom.B1, d.R0),
                p.SeedGrain, (uint)p.GrainModel, (uint)p.GrainSamples, (uint)d.RoughQ,
                new Vector4(p.GrainShadows / 100, p.GrainMidtones / 100, p.GrainHighlights / 100, d.Geom.PixelScale)), blurred);
        }
        if (proPhoto)
        {
            _proPhotoOutput ??= CreateProPhotoKernel();
            var m = RgbEncodingInfo.SrgbToProPhoto;
            var luma = RgbEncodingInfo.ProPhotoLuma;
            current = Apply(_proPhotoOutput, new OutputColorUniforms(
                new(m[0], m[1], m[2], 0), new(m[3], m[4], m[5], 0), new(m[6], m[7], m[8], 0),
                new(luma.R, luma.G, luma.B, 0)));
        }
        return current;
    }

    private ComputeKernel CreateProPhotoKernel()
    {
        var kernel = _gpu.CreateKernel(_shaders.Load("io/prophoto_output.wgsl"), "main",
            [BindingSpec.TextureFloat, BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float), BindingSpec.Uniform],
            "ProPhoto output");
        _kernels.Add(kernel);
        return kernel;
    }

    private TextureUniforms OverlayUniforms(ModuleParams p, GpuTexture asset, bool leak)
    {
        var angle = (leak ? p.LeakAngle : SeedHash.ToUnitFloat(SeedHash.Pcg(p.SeedDirt)) * 360) * MathF.PI / 180;
        if ((p.FrameWidth > p.FrameHeight) != (asset.Width > asset.Height)) angle += MathF.PI / 2;
        return new(MathF.Max(.05f, (leak ? p.LeakSize : p.DirtScale) / 100),
            (leak ? p.LeakAmount : p.DirtStrength) / 100, MathF.Cos(angle), MathF.Sin(angle),
            p.LeakHue, p.LeakIrregularity / 100, (p.DirtDust + p.DirtScratches + p.DirtHairs) / 300,
            Math.Min(p.FrameWidth, p.FrameHeight), leak ? Math.Clamp(p.LeakCount, 1, 3) : 1, p.Polarity,
            leak ? p.SeedLightLeak : p.SeedDirt, leak ? 1 : 0, p.LeakBlend, 0, 0, 0);
    }

    /// <summary>
    /// 暗部還原（ShadowRecovery 同式）：區塊統計 → 代理格盒狀平均／係數（H、V+係數、H、V）→ 全解析度套用。
    /// 代理格寫在既有工作紋理左上區域（代理格 ≤ 輸出尺寸），不配置新紋理、不改記憶體預算；
    /// 每個 pass 的讀取與寫入分屬不同紋理，pass 之間檢查取消。
    /// </summary>
    private GpuTexture RecoverShadows(GpuCommandList commands, GpuTexture current, ModuleParams p, CancellationToken token)
    {
        var layout = ShadowRecovery.Layout(p, (int)_width, (int)_height);
        uint pw = (uint)layout.ProxyWidth, ph = (uint)layout.ProxyHeight;
        token.ThrowIfCancellationRequested();
        if (layout.Exposure == 0f && layout.Luma == 0f && layout.Chroma == 0f) return current;
        var stats = Next(current);
        var temp = Next(current, stats);
        commands.Dispatch2D(_shadowStats, [current, stats, Uniform(ShadowRecovery.StatsUniforms(layout))], pw, ph);
        void Box(GpuTexture from, GpuTexture to, bool horizontal, bool coefficients)
        {
            token.ThrowIfCancellationRequested();
            commands.Dispatch2D(_shadowBox,
                [from, to, Uniform(ShadowRecovery.BoxUniforms(layout, horizontal, coefficients))], pw, ph);
        }
        Box(stats, temp, horizontal: true, coefficients: false);
        Box(temp, stats, horizontal: false, coefficients: true);
        Box(stats, temp, horizontal: true, coefficients: false);
        Box(temp, stats, horizontal: false, coefficients: false);
        token.ThrowIfCancellationRequested();
        var output = Next(current, stats);
        Dispatch(commands, _shadowApply, [current, stats, output, Uniform(ShadowRecovery.ApplyUniforms(layout))]);
        return output;
    }

    /// <summary>平鋪式高斯的半徑上限：workgroup 記憶體容納 256＋2×r 個 vec4 時的界線（見 gaussian_tile.wgsl）。</summary>
    private const int TileBlurMaxRadius = 320;
    // Bound cumulative Gaussian work between queue waits, including consecutive axes/effects.
    // Keep the normal 10-second device deadline; do not turn a whole image into one unbounded submission.
    private const ulong BlurSamplesPerSubmission = 64UL * 1024 * 1024;

    private GpuTexture Blur(GpuCommandList commands, GpuTexture input, int radius, GpuTexture? reserve = null,
        float? sigma = null, CancellationToken token = default)
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
        BlurPass(commands, input, temp, weights, horizontal: true, radius, token);
        BlurPass(commands, temp, result, weights, horizontal: false, radius, token);
        return result;
    }

    /// <summary>
    /// 單向高斯 pass：tile 半徑內用平鋪協作載入，超出上限改派直接版；
    /// 兩者都以展平的 workgroup 起點分批，不改權重、clamp 邊界與逐像素 tap 順序。
    /// </summary>
    private void BlurPass(GpuCommandList commands, GpuTexture input, GpuTexture output,
        GpuBuffer weights, bool horizontal, int radius, CancellationToken token)
    {
        var tiled = radius <= TileBlurMaxRadius;
        var major = horizontal ? _width : _height;
        var minor = horizontal ? _height : _width;
        var totalGroups = tiled ? (major + 255) / 256 * minor : (_width + 15) / 16 * ((_height + 15) / 16);
        var samplesPerGroup = 256UL * (ulong)(2 * radius + 1);
        // Both kernels have 256 lanes. Also respect WebGPU's baseline per-dimension dispatch limit.
        var maxGroups = (uint)Math.Clamp(BlurSamplesPerSubmission / samplesPerGroup, 1UL, 65535UL);
        for (uint first = 0; first < totalGroups;)
        {
            token.ThrowIfCancellationRequested();
            var groups = Math.Min(maxGroups, totalGroups - first);
            var samples = groups * samplesPerGroup;
            if (_pendingBlurSamples + samples > BlurSamplesPerSubmission)
            {
                commands.Flush();
                _pendingBlurSamples = 0;
                _uniformIndex = 0; // Every previous user of these buffers has completed.
                token.ThrowIfCancellationRequested();
            }
            var uniform = Uniform(new BlurUniforms(horizontal ? 1 : 0, horizontal ? 0 : 1, radius, first));
            commands.Dispatch(tiled ? _blurTile : _blur, [input, output, uniform, weights], groups, 1);
            _pendingBlurSamples += samples;
            first += groups;
        }
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

    /// <summary>
    /// 色調曲線查表的儲存緩衝區：第一次需要時配置（12 KB），大小不符才重建；每次算圖只有一個曲線 pass，
    /// 錄製前覆寫一次。由 <see cref="ReleaseAll"/> 與 <see cref="Dispose"/> 釋放；停用或恆等時從不配置。
    /// </summary>
    private GpuBuffer CurveTable(ToneCurveSet curves)
    {
        var bytes = (ulong)curves.Table.Length * sizeof(float);
        var buffer = _curveTable;
        if (buffer is null || buffer.Size != bytes)
        {
            buffer?.Dispose();
            _curveTable = null;
            _curveTable = buffer = _gpu.CreateBuffer(bytes, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "tone curve table");
        }
        _gpu.WriteBuffer<float>(buffer, curves.Table);
        return buffer;
    }

    private GpuTexture Next(GpuTexture first, GpuTexture? second = null, GpuTexture? third = null)
    {
        foreach (var texture in _work)
            if (texture != first && texture != second && texture != third) return texture;
        throw new InvalidOperationException("No non-aliasing GPU work texture.");
    }

    private void Dispatch(GpuCommandList commands, ComputeKernel kernel, ReadOnlySpan<object> bindings)
    {
        if (!_framed.Contains(kernel))
        {
            commands.Dispatch2D(kernel, bindings, _width, _height);
            return;
        }
        var all = new object[bindings.Length + 1];
        bindings.CopyTo(all);
        all[^1] = _view ?? throw new InvalidOperationException("Frame view uniform is not initialised.");
        commands.Dispatch2D(kernel, all, _width, _height);
    }

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
        if (source.Scene is not null || source.Encoding == RgbEncoding.ProPhoto)
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
        var bytes = WorkspaceBytes(_width, _height) + (_colorTransform?.ResidentBytes ?? 0) + _rawDisplay.ProfileResidentBytes;
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
        _colorTransform?.Dispose(); _colorTransform = null;
        _maskTexture?.Dispose(); _maskAlpha = null;
        ClearSources();
        foreach (var texture in _work) texture?.Dispose();
        foreach (var buffer in _uniforms) buffer.Dispose();
        foreach (var buffer in _gaussians.Values) buffer.Dispose();
        foreach (var texture in _luts.Values) texture.Dispose();
        foreach (var kernel in _kernels) kernel.Dispose();
        _output?.Dispose(); _readback?.Dispose(); _band?.Dispose(); _curveTable?.Dispose(); _view?.Dispose();
    }

    /// <summary>common/sampling.wgsl 的 FrameView：畫框尺寸（f32）＋渲染區域原點（i32），16 位元組。</summary>
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ViewUniforms(float FrameWidth, float FrameHeight, int OriginX, int OriginY);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MatrixUniforms(Vector4 Row0, Vector4 Row1, Vector4 Row2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BandUniforms(uint Row, uint Rows, uint Pad0, uint Pad1);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct DisplayUniforms(uint ExtendedSrgb, uint Pad0, uint Pad1, uint Pad2);
    // io/prophoto_output.wgsl: four vec4f rows at 0/16/32/48, total 64 bytes.
    [StructLayout(LayoutKind.Sequential)] private readonly record struct OutputColorUniforms(Vector4 Row0, Vector4 Row1, Vector4 Row2, Vector4 Luma);
    /// <summary>geometry/crop.wgsl：OutToSource 仿射（列主序）＋ 來源尺寸，32 位元組。</summary>
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CropUniforms(float M00, float M01, float M02, float SrcW, float M10, float M11, float M12, float SrcH);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BlurUniforms(int X, int Y, int Radius, uint FirstGroup);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct LensUniforms(float Amount, float Chromatic, float Soften, float Crop, float Aspect, float ShortSide, float Pad0, float Pad1);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct DoubleUniforms(int Source, int Blend, int Mirror, float Opacity, float X, float Y, float Scale, float Rotation, float Exposure, float Pad0, float Pad1, float Pad2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BokehUniforms(float X, float Y, float Focus, float Aspect, float Cosine, float Sine, float Transition, float Radius, float Boost, float ShortSide, int FocusShape, int Shape);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ZoomUniforms(float X, float Y, float Zoom, float Rotate, float Protect, float Transition, float Aspect, float ShortSide, int ShapeKind, float ShapeAspect, float ShapeCos, float ShapeSin);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MotionUniforms(float Angle, float Length, float Protect, float X, float Y, float Size, float ShortSide, int ShapeKind, float ShapeAspect, float ShapeCos, float ShapeSin);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct VignetteUniforms(float Amount, float Size, float Roundness, float Feather, float X, float Y, float Aspect, float Pad);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct HalationUniforms(float Amount, float Threshold, float Radius, float Hue, float ShortSide, float Saturation, float Pad1, float Pad2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct LeakUniforms(int Mode, float Amount, float Angle, float Size, float Hue, float Irregularity, int Count, uint Seed, float Aspect, float ShortSide, int Blend, float Red, float Yellow, float Blue, float White, uint Pad0);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PlateUniforms(int Type, float Strength, float Ortho, float Hue, float Tint, float Erosion, float Stains, float Uneven, uint Seed, float Aspect, uint Pad0, uint Pad1);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct BasicUniforms(float Brightness, float Contrast, float Highlights, float ShadowToe, float Saturation, float Structure, float ShortSide, float Pad);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ToneUniforms(float ShadowHue, float ShadowAmount, float HighlightHue, float HighlightAmount, float Balance, float Pad0, float Pad1, float Pad2);
    /// <summary>linear/tone_curves.wgsl：曲線飽和度（0–1，1 略過修正）與每通道查表格數，16 位元組。</summary>
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CurveUniforms(float Refine, uint Size, uint Pad0, uint Pad1);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct DirtUniforms(int Mode, float Dust, float Scratches, float Hairs, int Polarity, float Strength, float Scale, uint Seed, float ShortSide, uint Pad0, uint Pad1, uint Pad2);

    /// <summary>surface/grain.wgsl：A 為效果參數、B 為光學模型常數、MapX/MapY 為輸出→來源參考仿射（w 欄放 r0）、Tone 為亮度帶權重＋像素足跡；96 位元組。</summary>
    [StructLayout(LayoutKind.Sequential)] private readonly record struct GrainUniforms(Vector4 A, Vector4 B, Vector4 MapX, Vector4 MapY, uint Seed, uint Model, uint Samples, uint RoughQ, Vector4 Tone);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct TextureUniforms(float Scale, float Amount, float Cosine, float Sine, float Hue, float Irregularity, float Density, float ShortSide, int Count, int Polarity, uint Seed, int Leak, int Blend, uint Pad0, uint Pad1, uint Pad2);
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
