using System.Runtime.InteropServices;
using System.Text;

namespace FilmBatch.Engine.Imaging;

/// <summary>Vision 前景實例遮罩的原生互操作（macOS 14+；此 partial 專屬，不碰解碼／匯出路徑）。</summary>
internal static unsafe partial class AppleImageCodec
{
    /// <summary>
    /// 對已轉正的 <paramref name="source"/> 執行 <c>VNGenerateForegroundInstanceMaskRequest</c>（revision 1），
    /// 以 <c>generateScaledMaskForImageForInstances:fromRequestHandler:error:</c> 取出全實例軟覆蓋率。
    /// 來源樣本不被改動；只讀取遮罩 alpha，不做 RGB 生成或重上色。
    /// </summary>
    /// <returns>與 <paramref name="source"/> 同尺寸的覆蓋率（0 透明、65535 保留）；無觀測結果或無實例時為 <see langword="null"/>。</returns>
    internal static ushort[]? GenerateForegroundCoverage(SourceImage source, CancellationToken cancellationToken)
    {
        if (!ForegroundSelection.IsSupported)
            throw new PlatformNotSupportedException("Foreground selection requires macOS 14 or later.");
        cancellationToken.ThrowIfCancellationRequested();
        var vision = VisionFrameworks.Vision;
        var coreVideo = VisionFrameworks.CoreVideo;
        var objc = VisionFrameworks.ObjC;
        ObjC.EnsureLoaded(objc, vision);

        var pool = ObjC.PushPool(objc);
        try
        {
            using var scope = new OwnedObjects();
            nint srgb = scope.Add(Native.CGColorSpaceCreateWithName(
                NameRef(Frameworks.Graphics, "kCGColorSpaceSRGB")));
            var image = CreateRgba16Image(source.Width, source.Height, source.Rgba16, srgb, scope);
            var empty = scope.Add(Native.CFDictionaryCreate(0, [], [], 0,
                NativeLibrary.GetExport(Frameworks.Foundation, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(Frameworks.Foundation, "kCFTypeDictionaryValueCallBacks")));
            var handlerClass = ObjC.GetClass(objc, "VNImageRequestHandler");
            var requestClass = ObjC.GetClass(objc, "VNGenerateForegroundInstanceMaskRequest");
            nint handler = 0, request = 0, array = 0;
            try
            {
                handler = ObjC.AllocInit(objc, handlerClass, "initWithCGImage:options:", image, empty);
                request = ObjC.AllocInit(objc, requestClass, "init");
                ObjC.SetRevision(objc, request, 1);
                array = ObjC.SingleObjectArray(objc, request);
                using var cancel = cancellationToken.Register(static state =>
                {
                    var (handle, req) = ((nint Handle, nint Request))state!;
                    ObjC.Send(handle, req, "cancel");
                }, (objc, request));
                cancellationToken.ThrowIfCancellationRequested();
                nint error = 0;
                var ok = ObjC.PerformRequests(objc, handler, array, &error);
                cancellationToken.ThrowIfCancellationRequested();
                if (ok == 0)
                    throw new InvalidOperationException(VisionErrorMessage(objc, error, "Vision foreground request failed."));
                var observation = ObjC.FirstResult(objc, request);
                if (observation == 0)
                    return null; // 無觀測結果：明確的無主體，不偽造遮罩。
                var instances = ObjC.AllInstances(objc, observation);
                if (instances == 0 || ObjC.IndexSetCount(objc, instances) == 0)
                    return null; // 無前景實例：明確的無主體。
                nint maskError = 0;
                var pixelBuffer = ObjC.GenerateScaledMask(objc, observation, instances, handler, &maskError);
                if (pixelBuffer == 0)
                    throw new InvalidOperationException(VisionErrorMessage(objc, maskError, "Vision foreground mask failed."));
                try
                {
                    var coverage = ReadCoverage(coreVideo, pixelBuffer, source.Width, source.Height);
                    return HasForeground(coverage) ? coverage : null; // 全背景觀測：視為無主體。
                }
                finally
                {
                    Native.CFRelease(pixelBuffer);
                }
            }
            finally
            {
                // NSArray/請求/處理器皆為手動 alloc +1 參考，與 OwnedObjects 的 CF 參考分開釋放。
                if (array != 0) Native.CFRelease(array);
                if (request != 0) Native.CFRelease(request);
                if (handler != 0) Native.CFRelease(handler);
            }
        }
        finally
        {
            ObjC.PopPool(objc, pool);
        }
    }

    private static ushort[] ReadCoverage(nint coreVideo, nint pixelBuffer, int width, int height)
    {
        var format = VisionNative.CVPixelBufferGetPixelFormatType(coreVideo, pixelBuffer);
        var bufferWidth = checked((int)VisionNative.CVPixelBufferGetWidth(coreVideo, pixelBuffer));
        var bufferHeight = checked((int)VisionNative.CVPixelBufferGetHeight(coreVideo, pixelBuffer));
        if (bufferWidth != width || bufferHeight != height)
            throw new InvalidDataException(
                $"Vision foreground mask is {bufferWidth}×{bufferHeight}, expected {width}×{height}.");
        if (format != VisionNative.OneComponent8 && format != VisionNative.OneComponent32Float)
            throw new InvalidDataException($"Unsupported Vision foreground mask format 0x{format:X8}.");
        if (VisionNative.CVPixelBufferLockBaseAddress(coreVideo, pixelBuffer, VisionNative.ReadOnly) != 0)
            throw new InvalidOperationException("Could not lock the Vision foreground mask.");
        try
        {
            var baseAddress = VisionNative.CVPixelBufferGetBaseAddress(coreVideo, pixelBuffer);
            var bytesPerRow = checked((int)VisionNative.CVPixelBufferGetBytesPerRow(coreVideo, pixelBuffer));
            if (baseAddress == 0)
                throw new InvalidDataException("Vision foreground mask has no pixels.");
            var coverage = new ushort[checked(width * height)];
            if (format == VisionNative.OneComponent8)
            {
                var minimumStride = width;
                if (bytesPerRow < minimumStride)
                    throw new InvalidDataException("Vision foreground mask stride is too small.");
                for (var y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<byte>((void*)(baseAddress + (nint)y * bytesPerRow), width);
                    var target = coverage.AsSpan(y * width, width);
                    for (var x = 0; x < width; x++)
                    {
                        var sample = row[x];
                        target[x] = (ushort)(sample * 257); // 8-bit → 16-bit 滿刻度展開。
                    }
                }
            }
            else
            {
                var minimumStride = checked(width * sizeof(float));
                if (bytesPerRow < minimumStride)
                    throw new InvalidDataException("Vision foreground mask stride is too small.");
                for (var y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<float>((void*)(baseAddress + (nint)y * bytesPerRow), width);
                    var target = coverage.AsSpan(y * width, width);
                    for (var x = 0; x < width; x++)
                    {
                        var sample = row[x];
                        if (!float.IsFinite(sample))
                            throw new InvalidDataException("Vision foreground mask contains non-finite coverage.");
                        sample = Math.Clamp(sample, 0f, 1f);
                        target[x] = (ushort)(sample * 65535f + 0.5f);
                    }
                }
            }
            return coverage;
        }
        finally
        {
            VisionNative.CVPixelBufferUnlockBaseAddress(coreVideo, pixelBuffer, VisionNative.ReadOnly);
        }
    }
    private static bool HasForeground(ushort[] coverage)
    {
        for (var i = 0; i < coverage.Length; i++)
            if (coverage[i] != 0)
                return true;
        return false;
    }

    private static string VisionErrorMessage(nint objc, nint error, string fallback)
    {
        if (error == 0)
            return fallback;
        var description = ObjC.ErrorDescription(objc, error);
        return string.IsNullOrEmpty(description) ? fallback : $"{fallback} {description}";
    }

    /// <summary>Vision／CoreVideo／libobjc 句柄：只在 <see cref="ForegroundSelection.IsSupported"/> 之後載入。</summary>
    private static class VisionFrameworks
    {
        private static readonly object _sync = new();
        private static nint _vision, _coreVideo, _objc;

        internal static nint Vision => Get(ref _vision, "/System/Library/Frameworks/Vision.framework/Vision");
        internal static nint CoreVideo => Get(ref _coreVideo, "/System/Library/Frameworks/CoreVideo.framework/CoreVideo");
        internal static nint ObjC => Get(ref _objc, "/usr/lib/libobjc.A.dylib");

        private static nint Get(ref nint slot, string path)
        {
            lock (_sync)
            {
                return slot != 0 ? slot : slot = NativeLibrary.Load(path);
            }
        }
    }

    /// <summary>libobjc 直接訊息傳遞：呼叫端已做平台守衛，此處不做第二次作業系統判斷。</summary>
    private static class ObjC
    {
        private static nint _msgSend, _msgSendBool, _msgSendUlong, _msgSendVoid;
        private static nint _getClass, _selRegister, _pushPool, _popPool;
        private static readonly object _lock = new();
        private static readonly Dictionary<string, nint> _selectors = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, nint> _classes = new(StringComparer.Ordinal);

        internal static void EnsureLoaded(nint objc, nint vision)
        {
            // vision 參數確保呼叫端已載入 Vision（觸發類別註冊）；此處只解析 objc 符號。
            _ = vision;
            lock (_lock)
            {
                _msgSend = Resolve(objc, _msgSend, "objc_msgSend");
                _msgSendBool = Resolve(objc, _msgSendBool, "objc_msgSend");
                _msgSendUlong = Resolve(objc, _msgSendUlong, "objc_msgSend");
                _msgSendVoid = Resolve(objc, _msgSendVoid, "objc_msgSend");
                _getClass = Resolve(objc, _getClass, "objc_lookUpClass");
                _selRegister = Resolve(objc, _selRegister, "sel_registerName");
                _pushPool = Resolve(objc, _pushPool, "objc_autoreleasePoolPush");
                _popPool = Resolve(objc, _popPool, "objc_autoreleasePoolPop");
            }
        }

        private static nint Resolve(nint library, nint cached, string symbol) =>
            cached != 0 ? cached : NativeLibrary.GetExport(library, symbol);

        internal static nint PushPool(nint objc)
        {
            lock (_lock) { _pushPool = Resolve(objc, _pushPool, "objc_autoreleasePoolPush"); }
            return ((delegate* unmanaged<nint>)_pushPool)();
        }

        internal static void PopPool(nint objc, nint token)
        {
            lock (_lock) { _popPool = Resolve(objc, _popPool, "objc_autoreleasePoolPop"); }
            ((delegate* unmanaged<nint, void>)_popPool)(token);
        }

        internal static nint GetClass(nint objc, string name)
        {
            lock (_lock)
            {
                _getClass = Resolve(objc, _getClass, "objc_lookUpClass");
                if (_classes.TryGetValue(name, out var cached) && cached != 0)
                    return cached;
                var utf8 = Encoding.UTF8.GetBytes(name + "\0");
                fixed (byte* p = utf8)
                {
                    var cls = ((delegate* unmanaged<byte*, nint>)_getClass)(p);
                    if (cls == 0)
                        throw new InvalidOperationException($"Vision class {name} is unavailable.");
                    _classes[name] = cls;
                    return cls;
                }
            }
        }

        private static nint Selector(nint objc, string name)
        {
            lock (_lock)
            {
                _selRegister = Resolve(objc, _selRegister, "sel_registerName");
                if (_selectors.TryGetValue(name, out var cached) && cached != 0)
                    return cached;
                var utf8 = Encoding.UTF8.GetBytes(name + "\0");
                fixed (byte* p = utf8)
                {
                    var sel = ((delegate* unmanaged<byte*, nint>)_selRegister)(p);
                    if (sel == 0)
                        throw new InvalidOperationException($"Objective-C selector {name} is unavailable.");
                    _selectors[name] = sel;
                    return sel;
                }
            }
        }

        internal static nint AllocInit(nint objc, nint cls, string selector, nint arg0 = 0, nint arg1 = 0)
        {
            var allocated = ((delegate* unmanaged<nint, nint, nint>)_msgSend)(cls, Selector(objc, "alloc"));
            if (allocated == 0)
                throw new InvalidOperationException("Vision object allocation failed.");
            var count = arg1 != 0 ? 2 : arg0 != 0 ? 1 : 0;
            var sel = Selector(objc, selector);
            nint initialized = count switch
            {
                0 => ((delegate* unmanaged<nint, nint, nint>)_msgSend)(allocated, sel),
                1 => ((delegate* unmanaged<nint, nint, nint, nint>)_msgSend)(allocated, sel, arg0),
                _ => ((delegate* unmanaged<nint, nint, nint, nint, nint>)_msgSend)(allocated, sel, arg0, arg1),
            };
            if (initialized == 0)
            {
                throw new InvalidOperationException($"Vision initializer {selector} failed.");
            }
            return initialized;
        }

        internal static void SetRevision(nint objc, nint request, nuint revision)
        {
            ((delegate* unmanaged<nint, nint, nuint, void>)_msgSendVoid)(
                request, Selector(objc, "setRevision:"), revision);
        }

        internal static nint SingleObjectArray(nint objc, nint value)
        {
            // initWithObjects:count: 取物件指標陣列：呼叫端在 stack 上提供單元素陣列。
            var cls = GetClass(objc, "NSArray");
            var allocated = ((delegate* unmanaged<nint, nint, nint>)_msgSend)(cls, Selector(objc, "alloc"));
            if (allocated == 0)
                throw new InvalidOperationException("Vision request array allocation failed.");
            nint* objects = stackalloc nint[1];
            objects[0] = value;
            var initialized = ((delegate* unmanaged<nint, nint, nint, nuint, nint>)_msgSend)(
                allocated, Selector(objc, "initWithObjects:count:"), (nint)objects, 1);
            if (initialized == 0)
            {
                throw new InvalidOperationException("Vision request array initialization failed.");
            }
            return initialized;
        }

        internal static byte PerformRequests(nint objc, nint handler, nint requests, nint* error)
        {
            // NSError** 以指標寬度傳遞。
            return ((delegate* unmanaged<nint, nint, nint, nint, byte>)_msgSendBool)(
                handler, Selector(objc, "performRequests:error:"), requests, (nint)error);
        }

        internal static nint FirstResult(nint objc, nint request)
        {
            var results = ((delegate* unmanaged<nint, nint, nint>)_msgSend)(
                request, Selector(objc, "results"));
            if (results == 0)
                return 0;
            var count = (int)((delegate* unmanaged<nint, nint, nuint>)_msgSendUlong)(
                results, Selector(objc, "count"));
            if (count <= 0)
                return 0;
            // results 為 autoreleased：呼叫端持有外層 pool 或 request，不額外 retain。
            return ((delegate* unmanaged<nint, nint, nuint, nint>)_msgSend)(
                results, Selector(objc, "objectAtIndex:"), 0);
        }

        internal static nint AllInstances(nint objc, nint observation) =>
            ((delegate* unmanaged<nint, nint, nint>)_msgSend)(observation, Selector(objc, "allInstances"));

        internal static nuint IndexSetCount(nint objc, nint indexSet) =>
            ((delegate* unmanaged<nint, nint, nuint>)_msgSendUlong)(indexSet, Selector(objc, "count"));

        internal static nint GenerateScaledMask(nint objc, nint observation, nint instances,
            nint handler, nint* error)
        {
            return ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)_msgSend)(
                observation, Selector(objc, "generateScaledMaskForImageForInstances:fromRequestHandler:error:"),
                instances, handler, (nint)error);
        }

        internal static void Send(nint objc, nint target, string selector)
        {
            ((delegate* unmanaged<nint, nint, void>)_msgSendVoid)(target, Selector(objc, selector));
        }

        internal static string? ErrorDescription(nint objc, nint error)
        {
            var description = ((delegate* unmanaged<nint, nint, nint>)_msgSend)(
                error, Selector(objc, "localizedDescription"));
            return CFToString(description);
        }
    }

    /// <summary>CoreVideo 遮罩讀取：以句柄動態解析，避免非 Apple 平台在載入期綁定符號。</summary>
    private static class VisionNative
    {
        internal const uint OneComponent8 = 0x4C303038; // 'L008'
        internal const uint OneComponent32Float = 0x4C303066; // 'L00f'
        internal const uint ReadOnly = 0x00000001; // kCVPixelBufferLock_ReadOnly

        private static nint _lock, _unlock, _base, _bpr, _width, _height, _format;
        private static readonly object _lockObj = new();

        private static nint Resolve(nint library, nint cached, string symbol)
        {
            lock (_lockObj)
            {
                return cached != 0 ? cached : NativeLibrary.GetExport(library, symbol);
            }
        }

        internal static int CVPixelBufferLockBaseAddress(nint library, nint buffer, nuint flags)
        {
            _lock = Resolve(library, _lock, "CVPixelBufferLockBaseAddress");
            return ((delegate* unmanaged<nint, nuint, int>)_lock)(buffer, flags);
        }

        internal static int CVPixelBufferUnlockBaseAddress(nint library, nint buffer, nuint flags)
        {
            _unlock = Resolve(library, _unlock, "CVPixelBufferUnlockBaseAddress");
            return ((delegate* unmanaged<nint, nuint, int>)_unlock)(buffer, flags);
        }

        internal static nint CVPixelBufferGetBaseAddress(nint library, nint buffer)
        {
            _base = Resolve(library, _base, "CVPixelBufferGetBaseAddress");
            return ((delegate* unmanaged<nint, nint>)_base)(buffer);
        }

        internal static nuint CVPixelBufferGetBytesPerRow(nint library, nint buffer)
        {
            _bpr = Resolve(library, _bpr, "CVPixelBufferGetBytesPerRow");
            return ((delegate* unmanaged<nint, nuint>)_bpr)(buffer);
        }

        internal static nuint CVPixelBufferGetWidth(nint library, nint buffer)
        {
            _width = Resolve(library, _width, "CVPixelBufferGetWidth");
            return ((delegate* unmanaged<nint, nuint>)_width)(buffer);
        }

        internal static nuint CVPixelBufferGetHeight(nint library, nint buffer)
        {
            _height = Resolve(library, _height, "CVPixelBufferGetHeight");
            return ((delegate* unmanaged<nint, nuint>)_height)(buffer);
        }

        internal static uint CVPixelBufferGetPixelFormatType(nint library, nint buffer)
        {
            _format = Resolve(library, _format, "CVPixelBufferGetPixelFormatType");
            return ((delegate* unmanaged<nint, uint>)_format)(buffer);
        }
    }
}
