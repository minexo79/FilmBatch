using System.Diagnostics;
using System.Security.Cryptography;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw.Raster;
using Microsoft.ML.OnnxRuntime;

namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 來源階段（effect 之前）的實際 ONNX 降噪入口。實作重點：
/// • 模型選擇依來源域（RAW 場景線性→RawNIND linear，一般 sRGB→NAFNet-SIDD）。
/// • 「全解析度先行」：代理來源（有 <see cref="SourceImage.FilePath"/> 且參考尺寸大於
///   目前尺寸）先以相同 <see cref="RawLoadOptions"/> 重新載入全尺寸做神經推論，神經結果
///   以全解析度保留（匯出沿用）。預覽混合依域分路：場景線性先把原始／降噪輸入 box 到
///   預覽尺寸再混合（仿射可交換、有界快取）；sRGB 逐像素「混合→u16 量化→覆蓋率 box」
///   融合直寫預覽尺寸，與舊「全尺寸混合再降採樣」逐位元一致，兩者都不再配置全尺寸暫存。
/// • 單一行程級推論閘門（所有實例共用）：全域一次只有一趟推論。
/// • session 有界（最多兩個已知模型身分）；以 (模型路徑, 檔案戳記) 快取，啟用前驗證
///   固定 SHA-256，檔案被替換會偵測並重建 session。
/// • 取消：每次 run 用新的 <see cref="RunOptions"/>＋<see cref="RunOptions.Terminate"/>
///   註冊；run 回來後立刻檢查 token——不依賴原生丟出 <see cref="OnnxRuntimeException"/>
///   （CoreML 融合圖可成功返回）。取消的結果一律丟棄、不進快取、不毒化 session。
/// • 取消或失敗後 session 仍可重用（ORT kernel 無狀態）；非取消的模型／推論錯誤以
///   <see cref="InvalidDataException"/> 回報（缺模型保留 <see cref="FileNotFoundException"/>），
///   排程器不會把它們當 GPU 遺失重試。加速器初始化失敗在 provider 字串中明示後降級
///   CPU（Auto）；顯式指定的 provider 失敗直接拋出。
/// • 無逐像素配置：輸入／輸出 tensor 緩衝、重建平面、混合緩衝皆重用或走 ArrayPool。
/// • 在建構第一個 session 前呼叫 <see cref="OrtEnv.DisableTelemetryEvents"/>（本機推論不
///   回傳遙測）。
/// </summary>
public sealed class DenoiseProcessor : IDisposable
{
    /// <summary>全域單一飛行的推論閘門（所有實例共用）。</summary>
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);

    private static DenoiseProcessor? _shared;
    private static readonly object SharedLock = new();
    /// <summary>應用程式共用的處理器。</summary>
    public static DenoiseProcessor Shared
    {
        get
        {
            lock (SharedLock)
                return _shared ??= new DenoiseProcessor();
        }
    }

    /// <summary>進度事件；stage 為穩定程式碼（loading／infer／complete／cancelled）。</summary>
    public static event Action<DenoiseProgress>? ProgressChanged;

    private readonly object _sync = new();
    private readonly string? _explicitDirectory;
    private readonly string _provider;
    private DenoiseModelStore? _store;
    private string _storeRoot = "";
    private readonly Dictionary<string, SessionHolder> _sessions = new(StringComparer.Ordinal);
    private NeuralResult? _cached;
    private bool _disposed;

    /// <summary>模型目錄每次存取時重新解析（<see cref="AppPaths.OverrideRoot"/> 變動即生效）。</summary>
    private DenoiseModelStore Store
    {
        get
        {
            var root = _explicitDirectory is { Length: > 0 }
                ? _explicitDirectory
                : Path.Combine(AppPaths.Root, "models", "denoise");
            if (_store is null || !string.Equals(root, _storeRoot, StringComparison.Ordinal))
            {
                _storeRoot = root;
                _store = new DenoiseModelStore(root);
            }
            return _store;
        }
    }

    /// <summary>
    /// <paramref name="provider"/>：Auto／CPU／CoreML／DirectML（大小寫不敏感）。
    /// </summary>
    public DenoiseProcessor(string? modelDirectory = null, string provider = "Auto")
    {
        _explicitDirectory = modelDirectory;
        _provider = string.IsNullOrWhiteSpace(provider) ? "Auto" : provider.Trim();
    }

    /// <summary>
    /// 來源階段降噪：回傳新的 <see cref="SourceImage"/>（保留 FilePath／RawOptions／
    /// RawInfo／SemanticMasks／參考尺寸／場景中繼資料／alpha）。停用、零強度或已含
    /// <see cref="SourceImage.Denoise"/> 的來源原樣回傳（不做模型／緩衝工作）。
    /// </summary>
    public SourceImage Prepare(SourceImage source, Settings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);

        var ds = DenoiseSettings.From(settings);
        if (!ds.IsActive) return source;
        if (source.Denoise is not null) return source; // 已降噪的來源不再跑一次
        var spec = DenoiseModelCatalog.ForSource(source);
        return Prepare(source, spec, ds, cancellationToken);
    }

    /// <summary>
    /// 以指定模型規格執行 <see cref="Prepare"/>；公開版本一律用目錄模型。此多載只供
    /// 測試以「測試自有的微型 ONNX」走完全相同的推論管線（不改變產品行為）。
    /// </summary>
    internal SourceImage Prepare(SourceImage source, DenoiseModelSpec spec, DenoiseSettings ds,
        CancellationToken cancellationToken = default)
    {
        var store = Store;
        var modelPath = store.ModelPath(spec);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(CoreText.T(
                $"尚未安裝 {spec.Name} 降噪模型（{modelPath}）。請先以「denoise-models install {spec.Id}」或「import」明確安裝。",
                $"The {spec.Name} denoise model is not installed ({modelPath}). Install it explicitly with 'denoise-models install {spec.Id}' or 'import'."), modelPath);

        var modelStamp = ModelStamp(modelPath);
        var loadFull = source.FilePath is { Length: > 0 } p && File.Exists(p)
            && (source.ReferenceWidth > source.Width || source.ReferenceHeight > source.Height);
        var inferW = loadFull ? source.ReferenceWidth : source.Width;
        var inferH = loadFull ? source.ReferenceHeight : source.Height;
        var totalTiles = TilesFor(spec, inferW, inferH);
        var key = new CacheKey(SourceIdentity(source), source.ReferenceWidth,
            source.ReferenceHeight, source.RawOptions, spec.Id, store.DirectoryPath, modelStamp);

        NeuralResult? cached;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cached = _cached is { } hit && hit.Key == key ? hit : null;
        }
        if (cached is not null)
        {
            Report(source, spec, "complete", cached.TileCount, cached.TileCount, cached.Provider);
            return Materialize(source, cached, spec, cacheHit: true, ds, cancellationToken);
        }

        Report(source, spec, "loading", 0, totalTiles, null);
        var sw = Stopwatch.StartNew();
        try
        {
            InferenceGate.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Report(source, spec, "cancelled", 0, totalTiles, null);
            throw;
        }
        try
        {
            lock (_sync)
                cached = _cached is { } completed && completed.Key == key ? completed : null;
            if (cached is not null)
                return Materialize(source, cached, spec, cacheHit: true, ds, cancellationToken);
            var full = loadFull ? LoadFullSource(source, cancellationToken) : source;
            var session = GetSession(spec, modelPath, modelStamp);
            var inferredRgb = RunInference(spec, session, full, source, totalTiles, cancellationToken);
            var elapsed = sw.Elapsed;
            var neural = new NeuralResult(key, inferredRgb, full,
                session.ProviderName, elapsed, totalTiles);
            var result = Materialize(source, neural, spec,
                cacheHit: false, ds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync) _cached = neural;
            Report(source, spec, "complete", totalTiles, totalTiles, session.ProviderName);
            return result;
        }
        catch (OperationCanceledException)
        {
            Report(source, spec, "cancelled", 0, totalTiles, null);
            throw;
        }
        finally
        {
            InferenceGate.Release();
        }
    }

    private static int TilesFor(DenoiseModelSpec spec, int w, int h) =>
        checked(((w + spec.CoreSize - 1) / spec.CoreSize) * ((h + spec.CoreSize - 1) / spec.CoreSize));

    /// <summary>來源身分：檔案存在時用檔案戳記（可跨物件共享快取），否則以物件參考。</summary>
    private static object SourceIdentity(SourceImage source)
    {
        if (source.FilePath is { Length: > 0 } path && File.Exists(path))
        {
            var fi = new FileInfo(path);
            return new FileId(path, fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        return source;
    }

    private sealed record FileId(string Path, long Ticks, long Length);

    /// <summary>以相同 RAW 選項重新載入全解析度來源。</summary>
    private static SourceImage LoadFullSource(SourceImage proxy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = ImageLoader.LoadFull(proxy.FilePath!, proxy.RawOptions);
        ct.ThrowIfCancellationRequested();
        return full;
    }

    /// <summary>在閘門內執行 tile 推論；回傳全解析度降噪後 RGB 平面與 alpha 平面（可能為 null＝不透明）。</summary>
    private float[] RunInference(DenoiseModelSpec spec, SessionHolder session,
        SourceImage full, SourceImage reportSource, int totalTiles, CancellationToken ct)
    {
        var w = full.Width;
        var h = full.Height;
        var pixelCount = checked(w * h);
        float[]? alpha = null;
        var rgb = full.Scene?.Image.Data ?? new float[checked(pixelCount * 3)];
        var denoised = new float[checked(pixelCount * 3)];
        try
        {
            if (full.Scene is null)
            {
                alpha = new float[pixelCount];
                DenoiseTiling.Deinterleave16(full.Rgba16, pixelCount, rgb, alpha);
                // 透明像素以最近可見色 inpaint，避免把垃圾色送進神經網路。
                DenoiseTiling.InpaintTransparent(rgb, alpha, w, h);
            }
            Report(reportSource, spec, "infer", 0, totalTiles, session.ProviderName);
            var planes = session.RentPlanes();
            try
            {
                DenoiseTiling.RunTiled(spec, rgb, w, h, denoised, planes.Input, planes.Output,
                    (_, _, ti) => session.RunTile(planes, ct), ct,
                    (done, total) => Report(reportSource, spec, "infer", done, total, session.ProviderName));
            }
            finally { session.ReturnPlanes(planes); }
            if (!DenoiseTiling.AllFinite(denoised))
                throw new InvalidDataException(CoreText.T(
                    $"{spec.Name} 產生了非有限值（NaN／∞）。",
                    $"{spec.Name} produced non-finite values (NaN/∞)."));
            if (spec.MatchGain)
            {
                var gain = DenoiseTiling.MatchGain(rgb, denoised);
                if (gain != 1f)
                    DenoiseTiling.ScaleInPlace(denoised, gain);
            }
            ct.ThrowIfCancellationRequested();
            if (!DenoiseTiling.AllFinite(denoised))
                throw new InvalidDataException(CoreText.T("降噪增益校正超出有限範圍。", "Denoising gain correction exceeded finite range."));
            return denoised;
        }
        catch (OnnxRuntimeException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidDataException(CoreText.T(
                $"{spec.Name} 推論失敗：{ex.Message}",
                $"{spec.Name} inference failed: {ex.Message}"), ex);
        }
    }

    /// <summary>
    /// 把快取的神經結果套上後神經混合並封裝成新的 SourceImage（含 report）。三種路徑：
    /// • 匯出／同尺寸：全解析度混合→量化／寫回場景（與舊版逐位元相同，含透明像素
    ///   保留原始 RGB）。
    /// • 場景線性代理：mix 為逐像素仿射——先把原始／降噪輸入 box 到預覽尺寸（依尺寸
    ///   有界快取於神經結果上）再混合；與「先混合再 box」在浮點精度內一致。
    /// • sRGB 代理：量化與夾取必須在全解析度像素上發生——以
    ///   <see cref="DenoiseTiling.MixDownscale16"/> 逐像素「混合→u16 量化→覆蓋率 box」
    ///   直接寫出預覽尺寸；與舊鏈逐位元相同，但不配置全解析度暫存。
    /// </summary>
    private SourceImage Materialize(SourceImage source, NeuralResult neural,
        DenoiseModelSpec spec, bool cacheHit, DenoiseSettings ds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var report = new DenoiseReport(spec.Id, spec.OnnxSha256, neural.Provider,
            neural.InferenceTime, neural.TileCount, cacheHit);
        var full = neural.FullSource;
        var luma = spec.SceneLinear
            ? FilmBatch.Raw.Color.ColorSpaces.Luma2020
            : FilmBatch.Raw.Color.ColorSpaces.Luma709;
        var needsDownscale = full.Width != source.Width || full.Height != source.Height;
        if (full.Scene is { } sceneData)
        {
            RgbImage image;
            if (needsDownscale)
            {
                var preview = PreviewInputs(neural, source.Width, source.Height, ct);
                var pixels = checked(source.Width * source.Height);
                var mixed = (float[])preview.Denoised.Clone();
                DenoiseTiling.Mix(preview.Original, mixed, pixels, null,
                    ds.Amount / 100f, ds.Luminance / 100f, ds.Color / 100f, luma);
                ct.ThrowIfCancellationRequested();
                image = new RgbImage(source.Width, source.Height, mixed);
            }
            else
            {
                var pixels = checked(full.Width * full.Height);
                var mixed = new float[checked(pixels * 3)];
                Array.Copy(neural.Rgb, mixed, pixels * 3);
                DenoiseTiling.Mix(sceneData.Image.Data, mixed, pixels, null,
                    ds.Amount / 100f, ds.Luminance / 100f, ds.Color / 100f, luma);
                ct.ThrowIfCancellationRequested();
                image = new RgbImage(full.Width, full.Height, mixed);
            }
            return new SourceImage(sceneData with { Image = image })
            {
                FilePath = source.FilePath,
                RawOptions = source.RawOptions,
                RawInfo = source.RawInfo,
                SemanticMasks = source.SemanticMasks,
                ReferenceWidth = source.ReferenceWidth,
                ReferenceHeight = source.ReferenceHeight,
                Denoise = report,
                ForegroundMask = source.ForegroundMask,
            };
        }
        int outW;
        int outH;
        ushort[] rgba16;
        if (needsDownscale)
        {
            outW = source.Width;
            outH = source.Height;
            rgba16 = new ushort[checked(outW * outH * 4)];
            DenoiseTiling.MixDownscale16(full.Rgba16, full.Width, full.Height, neural.Rgb,
                ds.Amount / 100f, ds.Luminance / 100f, ds.Color / 100f, luma,
                rgba16, outW, outH, ct);
        }
        else
        {
            outW = full.Width;
            outH = full.Height;
            var pixels = checked(outW * outH);
            var original = new float[checked(pixels * 3)];
            var alpha = new float[pixels];
            var mixed = new float[checked(pixels * 3)];
            Array.Copy(neural.Rgb, mixed, pixels * 3);
            DenoiseTiling.Deinterleave16(full.Rgba16, pixels, original, alpha);
            DenoiseTiling.Mix(original, mixed, pixels, alpha,
                ds.Amount / 100f, ds.Luminance / 100f, ds.Color / 100f, luma);
            ct.ThrowIfCancellationRequested();
            rgba16 = new ushort[checked(pixels * 4)];
            DenoiseTiling.Interleave16(mixed, alpha, pixels, rgba16);
        }
        return new SourceImage(outW, outH, rgba16)
        {
            FilePath = source.FilePath,
            RawOptions = source.RawOptions,
            RawInfo = source.RawInfo,
            SemanticMasks = source.SemanticMasks,
            ReferenceWidth = source.ReferenceWidth,
            ReferenceHeight = source.ReferenceHeight,
            Denoise = report,
            ForegroundMask = source.ForegroundMask,
        };
    }

    /// <summary>
    /// 場景線性代理的預覽尺寸混合輸入：把全尺寸原始與神經結果各自 box 到
    /// (<paramref name="w"/>, <paramref name="h"/>)（與 <see cref="SourceImage.Resize"/> 相同的
    /// 濾鏡），快取於神經結果上（最近 4 個尺寸，<see cref="_sync"/> 保護；神經結果失效時
    /// 一併拋棄）。box 對混合是仿射可交換的——先降採樣再混合等價於先混合再降採樣。
    /// </summary>
    private ScenePreview PreviewInputs(NeuralResult neural, int w, int h, CancellationToken ct)
    {
        lock (_sync)
        {
            var list = neural.Previews;
            if (list is not null)
                for (var i = 0; i < list.Count; i++)
                    if (list[i].Width == w && list[i].Height == h)
                    {
                        var found = list[i];
                        list.RemoveAt(i);
                        list.Insert(0, found);
                        return found;
                    }
        }
        var image = neural.FullSource.Scene!.Image;
        var original = Resample.Resize(image, w, h, ResampleFilter.Box);
        var denoised = Resample.Resize(new RgbImage(image.Width, image.Height, neural.Rgb),
            w, h, ResampleFilter.Box);
        ct.ThrowIfCancellationRequested();
        var entry = new ScenePreview(w, h, original.Data, denoised.Data);
        lock (_sync)
        {
            var list = neural.Previews ??= new List<ScenePreview>(4);
            list.RemoveAll(e => e.Width == w && e.Height == h);
            list.Insert(0, entry);
            while (list.Count > 4) list.RemoveAt(list.Count - 1);
        }
        return entry;
    }

    /// <summary>預覽尺寸的 box 後原始／降噪輸入。</summary>
    private sealed record ScenePreview(int Width, int Height, float[] Original, float[] Denoised);

    /// <summary>模型檔戳記：最後寫入時間＋長度；供快速鍵比對與檔案替換偵測。</summary>
    private static (long Ticks, long Length) ModelStamp(string path)
    {
        var fi = new FileInfo(path);
        return (fi.LastWriteTimeUtc.Ticks, fi.Length);
    }

    /// <summary>取得（必要時建立並驗證 hash 的）session；檔案被替換時重建。</summary>
    private SessionHolder GetSession(DenoiseModelSpec spec, string modelPath,
        (long Ticks, long Length) stamp)
    {
        lock (_sync)
        {
            var key = spec.Id + "|" + modelPath;
            if (_sessions.TryGetValue(key, out var holder) && holder.Stamp == stamp)
                return holder;
            VerifyModelFile(spec, modelPath);
            var fresh = SessionHolder.Create(spec, modelPath, stamp, _provider, Path.GetDirectoryName(modelPath)!);
            _sessions[key] = fresh;
            holder?.Dispose();
            // 有界：最多兩個已知模型身分；超出的全部丟棄。
            if (_sessions.Count > 2)
            {
                List<string>? drop = null;
                foreach (var kv in _sessions)
                {
                    if (ReferenceEquals(kv.Value, fresh)) continue;
                    (drop ??= []).Add(kv.Key);
                }
                if (drop is not null)
                    foreach (var k in drop)
                    {
                        _sessions[k].Dispose();
                        _sessions.Remove(k);
                    }
            }
            return fresh;
        }
    }

    /// <summary>啟用前驗證 ONNX 檔案的固定大小與 SHA-256。</summary>
    private static void VerifyModelFile(DenoiseModelSpec spec, string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length != spec.OnnxBytes)
            throw new InvalidDataException(CoreText.T(
                $"模型檔大小不符：{fi.Length} != {spec.OnnxBytes}（{path}）。",
                $"Model file size mismatch: {fi.Length} != {spec.OnnxBytes} ({path})."));
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!hash.Equals(spec.OnnxSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(CoreText.T(
                $"模型檔 SHA-256 不符：{hash} != {spec.OnnxSha256}（{path}）。",
                $"Model file SHA-256 mismatch: {hash} != {spec.OnnxSha256} ({path})."));
    }

    private static void Report(SourceImage? source, DenoiseModelSpec spec, string stage,
        int done, int total, string? provider)
    {
        var handler = ProgressChanged;
        if (handler is null) return;
        try
        {
            handler(new DenoiseProgress(source?.FilePath, spec.Id, provider ?? "pending",
                stage, done, total));
        }
        catch { /* 訂閱者例外不影響推論 */ }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _sessions.Values) s.Dispose();
            _cached = null;
        }
    }

    /// <summary>快取鍵：來源身分、原始參考尺寸、RawOptions、模型身分與模型根＋戳記。
    /// 藝術／混合設定不在鍵內；代理與全解析度共用同一鍵（同一來源只留全尺寸神經結果）。</summary>
    private sealed record CacheKey(object Identity, int RefW, int RefH, RawLoadOptions RawOptions,
        string ModelId, string ModelRoot, (long Ticks, long Length) ModelStamp);

    /// <summary>快取的全解析度神經結果（未混合）與其來源；匯出以此做全尺寸混合。</summary>
    private sealed record NeuralResult(CacheKey Key, float[] Rgb,
        SourceImage FullSource, string Provider, TimeSpan InferenceTime, int TileCount)
    {
        /// <summary>場景線性代理的預覽尺寸 box 輸入（最近 4 個尺寸，MRU 在前）。
        /// 依附於本結果：結果失效時一併拋棄，不需額外失效邏輯；由 <see cref="_sync"/> 保護。</summary>
        internal List<ScenePreview>? Previews;
    }

    /// <summary>單一模型的 ORT session 與可重用 tensor 平面。</summary>
    private sealed class SessionHolder : IDisposable
    {
        internal InferenceSession Session { get; }
        internal string ProviderName { get; }
        internal (long Ticks, long Length) Stamp { get; }
        internal DenoiseModelSpec Spec { get; }
        private readonly Stack<Planes> _planes = new();
        private readonly int _planeFloats;
        private readonly long[] _shape;
        private bool _disposed;

        private SessionHolder(InferenceSession session, string providerName,
            (long Ticks, long Length) stamp, DenoiseModelSpec spec)
        {
            Session = session;
            ProviderName = providerName;
            Stamp = stamp;
            Spec = spec;
            _planeFloats = 3 * spec.TileSize * spec.TileSize;
            _shape = [1, 3, spec.TileSize, spec.TileSize];
        }

        internal static SessionHolder Create(DenoiseModelSpec spec, string modelPath,
            (long Ticks, long Length) stamp, string provider, string modelRoot)
        {
            OrtEnv.Instance().DisableTelemetryEvents();
            using var options = NewOptions();
            string providerName;
            try { providerName = ConfigureProvider(options, provider, modelRoot); }
            catch (OnnxRuntimeException error)
            {
                throw new InvalidDataException($"Could not configure {provider} inference: {error.Message}", error);
            }
            try
            {
                return new SessionHolder(new InferenceSession(modelPath, options), providerName, stamp, spec);
            }
            catch (OnnxRuntimeException error)
            {
                if (providerName.StartsWith("CPU", StringComparison.Ordinal)
                    || !provider.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(CoreText.T(
                        $"無法建立 {provider} 的推論 session：{error.Message}",
                        $"Could not create the {provider} inference session: {error.Message}"), error);
                using var cpuOptions = NewOptions();
                cpuOptions.AppendExecutionProvider_CPU(1);
                try
                {
                    return new SessionHolder(new InferenceSession(modelPath, cpuOptions),
                        $"CPU (fell back from {providerName}: {error.Message})", stamp, spec);
                }
                catch (OnnxRuntimeException cpuError)
                {
                    throw new InvalidDataException(CoreText.T(
                        $"無法建立 CPU 推論 session：{cpuError.Message}",
                        $"Could not create the CPU inference session: {cpuError.Message}"), cpuError);
                }
            }
        }

        private static SessionOptions NewOptions() => new()
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        /// <summary>依 provider 字串設定執行提供者；回傳誠實名稱（含降級說明）。</summary>
        private static string ConfigureProvider(SessionOptions options, string provider, string modelRoot)
        {
            var p = provider.Trim();
            if (p.Equals("CPU", StringComparison.OrdinalIgnoreCase))
            {
                options.AppendExecutionProvider_CPU(1);
                return "CPU";
            }
            var auto = p.Equals("Auto", StringComparison.OrdinalIgnoreCase);
            var wantCoreML = p.Equals("CoreML", StringComparison.OrdinalIgnoreCase);
            var wantDml = p.Equals("DirectML", StringComparison.OrdinalIgnoreCase)
                || p.Equals("DML", StringComparison.OrdinalIgnoreCase);
            if (!auto && !wantCoreML && !wantDml)
                throw new InvalidDataException(CoreText.T(
                    $"未知的推論 provider：{provider}（可為 Auto／CPU／CoreML／DirectML）。",
                    $"Unknown inference provider: {provider} (Auto/CPU/CoreML/DirectML)."));

            if (auto || wantCoreML)
            {
                if (OperatingSystem.IsMacOS())
                {
                    try
                    {
                        var cacheDir = Path.Combine(modelRoot, "coreml-cache");
                        Directory.CreateDirectory(cacheDir);
                        options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                        {
                            ["ModelFormat"] = "MLProgram",
                            ["MLComputeUnits"] = "ALL",
                            ["RequireStaticInputShapes"] = "1",
                            ["AllowLowPrecisionAccumulationOnGPU"] = "0",
                            ["ModelCacheDirectory"] = cacheDir,
                        });
                        return "CoreML + CPU";
                    }
                    catch (Exception ex) when (auto)
                    {
                        // 落回 CPU；降級原因保留在 provider 字串中。
                        options.AppendExecutionProvider_CPU(1);
                        return "CPU (fell back from CoreML + CPU: " + ex.GetType().Name + ")";
                    }
                }
                else if (wantCoreML)
                {
                    throw new InvalidDataException(CoreText.T(
                        "CoreML 只在 macOS 可用；此平台不支援。",
                        "CoreML is only available on macOS; it is not supported on this platform."));
                }
            }
            if (auto || wantDml)
            {
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        options.EnableMemoryPattern = false;
                        options.AppendExecutionProvider_DML(0);
                        return "DirectML + CPU";
                    }
                    catch (Exception ex) when (auto)
                    {
                        options.AppendExecutionProvider_CPU(1);
                        return "CPU (fell back from DirectML + CPU: " + ex.GetType().Name + ")";
                    }
                }
                else if (wantDml)
                {
                    throw new InvalidDataException(CoreText.T(
                        "DirectML 只在 Windows 可用；此平台不支援。",
                        "DirectML is only available on Windows; it is not supported on this platform."));
                }
            }
            options.AppendExecutionProvider_CPU(1);
            return "CPU";
        }

        /// <summary>重用固定形狀的輸入／輸出 tensor 緩衝（含已建好的 OrtValue）。</summary>
        internal Planes RentPlanes()
        {
            lock (_planes)
            {
                if (_planes.Count > 0) return _planes.Pop();
            }
            return new Planes(_planeFloats, _shape);
        }

        internal void ReturnPlanes(Planes p)
        {
            if (_disposed) { p.Dispose(); return; }
            lock (_planes) _planes.Push(p);
        }

        /// <summary>
        /// 執行單一固定形狀 tile。取消以新 <see cref="RunOptions"/>＋Terminate 註冊傳遞；
        /// run 回來後立刻檢查 token（CoreML 融合圖可成功返回而不丟原生例外）。
        /// 取消的 ORT 例外一律轉為 <see cref="OperationCanceledException"/>。
        /// </summary>
        internal void RunTile(Planes planes, CancellationToken ct)
        {
            using var run = new RunOptions();
            using var registration = ct.Register(() => run.Terminate = true);
            try
            {
                Session.Run(run, [Spec.InputName], [planes.InputValue],
                    [Spec.OutputName], [planes.OutputValue]);
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            ct.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            _disposed = true;
            Session.Dispose();
            lock (_planes)
                while (_planes.Count > 0) _planes.Pop().Dispose();
        }

        /// <summary>固定形狀的輸入／輸出 tensor 與對應 OrtValue（整個 session 生命期重用）。</summary>
        internal sealed class Planes : IDisposable
        {
            internal float[] Input { get; }
            internal float[] Output { get; }
            internal OrtValue InputValue { get; }
            internal OrtValue OutputValue { get; }

            internal Planes(int floats, long[] shape)
            {
                Input = new float[floats];
                Output = new float[floats];
                InputValue = OrtValue.CreateTensorValueFromMemory(
                    OrtMemoryInfo.DefaultInstance, Input.AsMemory(), shape);
                OutputValue = OrtValue.CreateTensorValueFromMemory(
                    OrtMemoryInfo.DefaultInstance, Output.AsMemory(), shape);
            }

            public void Dispose()
            {
                InputValue.Dispose();
                OutputValue.Dispose();
            }
        }
    }
}
