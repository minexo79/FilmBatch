using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using FilmBatch.Core;
using FilmBatch.Engine.Exporting;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// Apple ImageIO/CoreGraphics 影像後端（macOS 與 iOS 共用；無 SDK 綁定，直接 LibraryImport C API）。
/// 由 <see cref="ImageLoader"/>／<see cref="BatchExporter"/> 經編譯期後端選擇路由使用
/// （<see cref="ImageBackend"/>），亦可在 macOS 測試中直接呼叫。
/// </summary>
/// <remarks>
/// 解碼一律輸出已轉正（EXIF Orientation 1）的 16-bit 直向（straight）sRGB RGBA；
/// 支援 ImageIO 可辨識格式（PNG／JPEG／TIFF／HEIC/HEIF 等）。RAW 不經此路徑
/// （<see cref="Raw.RawSourceLoader"/> 先行攔截）。ICC 轉換為真正色彩管理：
/// 矩陣/TRC 型設定檔走受控線性化＋D50 色料矩陣（與 <see cref="Raw.PreviewIcc"/> 同一數學），
/// LUT/CMYK 型設定檔與嵌入預覽 CMS 走 CG 位元圖內容的 ColorSync 轉換。
/// 解碼／色彩失敗一律擲出例外（誠實錯誤），不以替代色彩假裝成功。
/// 所有 Create/Copy 參考由 <see cref="OwnedObjects"/> 集中釋放；Get 規則指標不外洩。
/// </remarks>
internal static unsafe partial class AppleImageCodec
{
    private const long MaxPixels = 1L << 30;
    // CGImageAlphaInfo.Last (3) | kCGBitmapByteOrder16Host（1<<12 little／3<<12 big）：16-bit 直向 RGBA 輸入。
    private static readonly uint AlphaLast16 = 0x0003u | (BitConverter.IsLittleEndian ? 0x1000u : 0x3000u);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>確認 Apple 影像函式庫可用（發佈冒煙測試）。</summary>
    internal static string NativeVersion()
    {
        RequireApple();
        var handle = NativeLibrary.Load(Native.ImageIo);
        NativeLibrary.Free(handle);
        return "Apple ImageIO/CoreGraphics";
    }

    /// <summary>只讀檔頭幾何與方向（CopyPropertiesAtIndex，不解碼像素），回傳轉正後寬高。</summary>
    internal static (int Width, int Height) DecodeInfo(string path)
    {
        RequireApple();
        using var scope = new OwnedObjects();
        var source = OpenSource(scope, path);
        var props = scope.Add(Native.CGImageSourceCopyPropertiesAtIndex(source, 0, 0));
        var width = Integer(props, "PixelWidth", scope);
        var height = Integer(props, "PixelHeight", scope);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException(CoreText.T($"無法讀取影像尺寸：{path}", $"Could not read image dimensions: {path}"));
        var orientation = IntegerOptional(props, "Orientation", scope);
        return SwapsAxes(orientation) ? ((int)height, (int)width) : ((int)width, (int)height);
    }

    /// <summary>
    /// 解碼為已轉正的 16-bit sRGB RGBA；<paramref name="maxLongEdge"/> 僅縮小（縮圖代理圖）。
    /// 檔案光柵的轉正後尺寸記錄於 <see cref="SourceImage.ReferenceWidth"/>／
    /// <see cref="SourceImage.ReferenceHeight"/>（即使縮放亦保留原始幾何參考）。
    /// </summary>
    internal static SourceImage Load(string path, int maxLongEdge, RawLoadOptions rawOptions)
    {
        RequireApple();
        using var scope = new OwnedObjects();
        var source = OpenSource(scope, path);
        var (refW, refH, orientation) = OrientedDimensions(source, scope, path);
        // ImageIO's thumbnail decoder may quantize even when no downscale is needed.
        // Read the original raster for full-size previews to retain 16-bit samples.
        if (maxLongEdge >= Math.Max(refW, refH))
        {
            var full = scope.Add(Native.CGImageSourceCreateImageAtIndex(source, 0, 0));
            if (full == 0) throw new InvalidDataException($"Could not decode image: {path}");
            return FinishDecode(full, orientation, path, scope, refW, refH, maxEdge: null, rawOptions);
        }

        // 縮圖路徑：ImageIO 於解碼時套用方向與縮放（JPEG 可用子採樣加速）；
        // CreateThumbnailWithTransform 已轉正光柵，不再經托管重排。
        var decoded = scope.Add(CreateThumbnail(source, maxLongEdge, scope));
        return FinishDecode(decoded, orientation: 1, path, scope, refW, refH, maxLongEdge, rawOptions);
    }

    internal static SourceImage FromEmbeddedPreview(byte[] preview, string path, RawLoadOptions rawOptions,
        int maxLongEdge, RawInfo info)
    {
        RequireApple();
        // ImageIO 只在此同步區段借用位元組；釋放所有原生參考後才解除固定，不複製預覽資料。
        fixed (byte* bytes = preview)
        {
            using var scope = new OwnedObjects();
            var data = scope.Add(Native.CFDataCreateWithBytesNoCopy(0, (nint)bytes, preview.Length,
                NameRef(Frameworks.Foundation, "kCFAllocatorNull")));
            var source = scope.Add(Native.CGImageSourceCreateWithData(data, 0));
            if (source == 0) throw new InvalidDataException($"Could not decode embedded preview: {path}");
            var decoded = scope.Add(CreateThumbnail(source, maxLongEdge, scope, applyOrientation: false));
            var (width, height) = info.DevelopedSize();
            if (info.Orientation.SwapsAxes()) (width, height) = (height, width);
            return FinishDecode(decoded, info.Orientation.ToExif(), path, scope, width, height,
                maxLongEdge, rawOptions, info);
        }
    }

    /// <summary>全解析度解碼（匯出用）：轉正、不縮放、保留 16-bit。</summary>
    internal static SourceImage LoadFull(string path, RawLoadOptions rawOptions = default)
    {
        RequireApple();
        using var scope = new OwnedObjects();
        var source = OpenSource(scope, path);
        var (refW, refH, orientation) = OrientedDimensions(source, scope, path);
        var decoded = scope.Add(Native.CGImageSourceCreateImageAtIndex(source, 0, 0));
        if (decoded == 0)
            throw new InvalidDataException(CoreText.T($"無法解碼影像：{path}", $"Could not decode image: {path}"));
        return FinishDecode(decoded, orientation, path, scope, refW, refH, maxEdge: null, rawOptions);
    }

    private static SourceImage FinishDecode(nint decoded, long orientation, string path, OwnedObjects scope,
        int refW, int refH, int? maxEdge, RawLoadOptions rawOptions, RawInfo? rawInfo = null)
    {
        var oriented = ApplyOrientation(decoded, orientation, scope);
        var raster = ConvertToSrgb(oriented, path, scope);
        var result = new SourceImage(raster.Width, raster.Height, raster.Pixels)
        {
            FilePath = Path.GetFullPath(path),
            RawOptions = rawOptions,
            RawInfo = rawInfo,
            ReferenceWidth = refW,
            ReferenceHeight = refH,
        };
        return maxEdge is { } edge && Math.Max(raster.Width, raster.Height) > edge
            ? result.Downscale(edge)
            : result;
    }

    private static void RequireApple()
    {
        if (!OperatingSystem.IsIOS() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("AppleImageCodec requires iOS, macOS, or Mac Catalyst.");
    }

    private static nint OpenSource(OwnedObjects scope, string path)
    {
        var urlBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        var url = scope.Add(Native.CFURLCreateFromFileSystemRepresentation(0, urlBytes, urlBytes.Length, 0));
        var source = Native.CGImageSourceCreateWithURL(url, 0);
        if (source == 0)
            throw new InvalidDataException(CoreText.T($"無法解碼影像：{path}", $"Could not decode image: {path}"));
        scope.Add(source);
        return source;
    }

    private static (int W, int H, long Orientation) OrientedDimensions(nint source, OwnedObjects scope, string path)
    {
        var props = scope.Add(Native.CGImageSourceCopyPropertiesAtIndex(source, 0, 0));
        var w = checked((int)Integer(props, "PixelWidth", scope));
        var h = checked((int)Integer(props, "PixelHeight", scope));
        if (w <= 0 || h <= 0)
            throw new InvalidDataException(CoreText.T($"無法讀取影像尺寸：{path}", $"Could not read image dimensions: {path}"));
        var orientation = IntegerOptional(props, "Orientation", scope);
        var (refW, refH) = SwapsAxes(orientation) ? (h, w) : (w, h);
        if ((long)refW * refH > MaxPixels)
            throw new InvalidDataException(CoreText.T($"影像過大：{path}", $"Image too large: {path}"));
        return (refW, refH, orientation);
    }

    private static nint CreateThumbnail(nint source, int maxPixelSize, OwnedObjects scope, bool applyOrientation = true)
    {
        var options = Dict(scope,
            ("kCGImageSourceCreateThumbnailWithTransform", applyOrientation
                ? Native.kCFBooleanTrue() : NameRef(Frameworks.Foundation, "kCFBooleanFalse")),
            ("kCGImageSourceCreateThumbnailFromImageAlways", Native.kCFBooleanTrue()),
            ("kCGImageSourceShouldCacheImmediately", Native.kCFBooleanTrue()),
            ("kCGImageSourceThumbnailMaxPixelSize", Num(scope, Math.Max(1, maxPixelSize))));
        var image = Native.CGImageSourceCreateThumbnailAtIndex(source, 0, options);
        if (image == 0)
            throw new InvalidDataException("Could not decode image via thumbnail path.");
        return image;
    }

    private static bool SwapsAxes(long orientation) => orientation is >= 5 and <= 8;

    /// <summary>EXIF 方向 2..8 於托管端做 8 方向重排（縮圖路徑已由 ImageIO 轉正者除外）。</summary>
    private static nint ApplyOrientation(nint image, long orientation, OwnedObjects scope)
    {
        if (orientation <= 1) return image;
        var raster = ReadRaster(image, path: null, scope);
        var w = raster.Width;
        var h = raster.Height;
        var (ow, oh) = SwapsAxes(orientation) ? (h, w) : (w, h);
        var output = new ushort[ow * oh * 4];
        for (var dy = 0; dy < oh; dy++)
            for (var dx = 0; dx < ow; dx++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (w - 1 - dx, dy),
                    3 => (w - 1 - dx, h - 1 - dy),
                    4 => (dx, h - 1 - dy),
                    5 => (dy, dx),
                    6 => (dy, h - 1 - dx),
                    7 => (w - 1 - dy, h - 1 - dx),
                    8 => (w - 1 - dy, dx),
                    _ => (dx, dy),
                };
                var s = (sy * w + sx) * 4;
                var d = (dy * ow + dx) * 4;
                output[d] = raster.Pixels[s];
                output[d + 1] = raster.Pixels[s + 1];
                output[d + 2] = raster.Pixels[s + 2];
                output[d + 3] = raster.Pixels[s + 3];
            }
        return CreateRgba16Image(ow, oh, output, raster.ColorSpace, scope);
    }

    private sealed record Raster(int Width, int Height, ushort[] Pixels, nint ColorSpace);

    /// <summary>
    /// 解碼後影像 → sRGB：無設定檔視為 sRGB；ICC 可解析為矩陣/TRC 時走受控線性化＋
    /// D50 色料矩陣；LUT/CMYK 等非矩陣設定檔走 CG 位元圖的 ColorSync 轉換。
    /// </summary>
    private static Raster ConvertToSrgb(nint image, string? path, OwnedObjects scope)
    {
        var colorSpace = Native.CGImageGetColorSpace(image);
        if (colorSpace == 0)
            return ReadRaster(image, path, scope);
        // CoreGraphics recognizes the complete sRGB space, including its transfer
        // curve. An identity transform must not re-quantize the embedded ICC TRC.
        var name = CFToString(Native.CGColorSpaceGetName(colorSpace));
        if (name == "kCGColorSpaceSRGB") return ReadRaster(image, path, scope);
        var iccRef = Native.CGColorSpaceCopyICCData(colorSpace);
        if (iccRef != 0)
        {
            scope.Add(iccRef);
            var info = Raw.PreviewIcc.Parse(DataBytes(iccRef));
            if (info is { Kind: Raw.PreviewIccKind.Gray, Trc.Length: >= 1 } g)
                // 灰階 ICC：上游 ResolveSpace 以 sRGB 原色＋kTRC 當全通道曲線。
                return ConvertMatrix(ReadRaster(image, path, scope),
                    Raw.PreviewIcc.RgbToXyzD50(ColorSpaces.Srgb), [g.Trc[0], g.Trc[0], g.Trc[0]], named: ColorSpaces.Srgb);
            if (info is { Kind: Raw.PreviewIccKind.MatrixTrc } m)
                return ApplyMatrixTrc(ReadRaster(image, path, scope), m);
            return ConvertViaColorSync(image, path, scope);
        }
        return ApplyNamedSpace(ReadRaster(image, path, scope), name);
    }

    /// <summary>
    /// 無 ICC 位元組的命名空間：以已知傳輸曲線（P3＝sRGB 曲線；ITUR 系列＝Rec.709；
    /// ROMM＝gamma 1.8；AdobeRGB＝gamma 563/256）先線性化，再走白點適應矩陣。
    /// Apple 實務上會為命名空間合成 ICC；此分支僅在合成缺席時生效，
    /// 若與矩陣型 ICC 路徑有差別，以 ICC 為準。
    /// </summary>
    private static Raster ApplyNamedSpace(Raster raster, string? name)
    {
        (RgbSpace Space, Raw.PreviewTrc Trc)? pair = name switch
        {
            "kCGColorSpaceSRGB" => null,
            "kCGColorSpaceLinearSRGB" => (ColorSpaces.Srgb, Raw.PreviewTrc.Linear),
            "kCGColorSpaceDisplayP3" => (ColorSpaces.DisplayP3, Raw.PreviewTrc.Srgb),
            "kCGColorSpaceLinearDisplayP3" or "kCGColorSpaceExtendedLinearDisplayP3" =>
                (ColorSpaces.DisplayP3, Raw.PreviewTrc.Linear),
            "kCGColorSpaceITUR_2020" => (ColorSpaces.Rec2020, Raw.PreviewTrc.Rec709),
            "kCGColorSpaceLinearITUR_2020" or "kCGColorSpaceExtendedLinearITUR_2020" =>
                (ColorSpaces.Rec2020, Raw.PreviewTrc.Linear),
            // BT.709＝sRGB 原色＋Rec.709 傳輸曲線；PQ/HLG HDR 曲線無法以此 TRC 表達，走 null（同未知）。
            "kCGColorSpaceITUR_709" => (ColorSpaces.Srgb, Raw.PreviewTrc.Rec709),
            "kCGColorSpaceROMMRGB" => (ColorSpaces.ProPhoto, Raw.PreviewTrc.Gamma(1.8f)),
            "kCGColorSpaceAdobeRGB1998" => (ColorSpaces.AdobeRgb, Raw.PreviewTrc.Gamma(563f / 256f)),
            _ => null,
        };
        if (pair is null) return raster;
        var (space, trc) = pair.Value;
        return ConvertMatrix(raster, Raw.PreviewIcc.RgbToXyzD50(space), [trc, trc, trc], named: space);
    }

    private static Raster ApplyMatrixTrc(Raster raster, Raw.PreviewIccInfo info) =>
        ConvertMatrix(raster, info.ToXyzD50, info.Trc, info.Named);

    private static Raster ConvertMatrix(Raster raster, Mat3 toXyzD50, Raw.PreviewTrc[]? trc, RgbSpace? named = null)
    {
        var toSrgb = named is { } n
            ? n.ToSpace(ColorSpaces.Srgb)
            : ColorSpaces.Srgb.FromXyz()
                .Mul(ColorSpaces.Bradford(ColorSpaces.D50, ColorSpaces.Srgb.White))
                .Mul(toXyzD50);
        var p = raster.Pixels;
        var luts = trc is { Length: >= 3 } t
            ? new[] { Raw.PreviewJpegColour.Lut16(t[0]), Raw.PreviewJpegColour.Lut16(t[1]), Raw.PreviewJpegColour.Lut16(t[2]) }
            : null;
        var output = new ushort[p.Length];
        for (var i = 0; i < p.Length; i += 4)
        {
            var r = p[i] / 65535f;
            var g = p[i + 1] / 65535f;
            var b = p[i + 2] / 65535f;
            if (luts is not null)
            {
                r = luts[0][p[i]];
                g = luts[1][p[i + 1]];
                b = luts[2][p[i + 2]];
            }
            var sr = toSrgb[0, 0] * r + toSrgb[0, 1] * g + toSrgb[0, 2] * b;
            var sg = toSrgb[1, 0] * r + toSrgb[1, 1] * g + toSrgb[1, 2] * b;
            var sb = toSrgb[2, 0] * r + toSrgb[2, 1] * g + toSrgb[2, 2] * b;
            output[i] = Quant16(Transfer.LinearToSrgb((float)Math.Max(sr, 0)));
            output[i + 1] = Quant16(Transfer.LinearToSrgb((float)Math.Max(sg, 0)));
            output[i + 2] = Quant16(Transfer.LinearToSrgb((float)Math.Max(sb, 0)));
            output[i + 3] = p[i + 3];
        }
        return raster with { Pixels = output };
    }

    private static ushort Quant16(float v) => (ushort)Math.Clamp((int)(v * 65535f + 0.5f), 0, 65535);

    /// <summary>
    /// LUT/CMYK 等非矩陣設定檔：CGContext 位元圖以 ColorSync 轉換至 sRGB，
    /// 32-bit float 內容保留低有效位元與 HDR 範圍。
    /// </summary>
    private static Raster ConvertViaColorSync(nint image, string? path, OwnedObjects scope)
    {
        var width = checked((int)Native.CGImageGetWidth(image));
        var height = checked((int)Native.CGImageGetHeight(image));
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException(CoreText.T($"影像過大：{path}", $"Image too large: {path}"));
        var srgb = scope.Add(Native.CGColorSpaceCreateWithName(NameRef(Frameworks.Graphics, "kCGColorSpaceSRGB")));
        // 128bpp float RGBA（premultiplied-last；alpha 僅內部承接，輸出端以量化後值讀回）。
        var ctx = Native.CGBitmapContextCreate(0, (nuint)width, (nuint)height, 32, (nuint)width * 16, srgb,
            0x100u | 0x2000u | 0x01u); // FloatComponents | ByteOrder32Little(2<<12) | PremultipliedLast
        if (ctx == 0)
            throw new InvalidDataException(CoreText.T($"色彩轉換失敗：{path}", $"Color conversion failed: {path}"));
        scope.Add(ctx);
        var rect = new Native.CGRect(0, 0, width, height);
        Native.CGContextDrawImage(ctx, rect, image);
        var ctxImage = scope.Add(Native.CGBitmapContextCreateImage(ctx));
        var provider = Native.CGImageGetDataProvider(ctxImage);
        if (provider == 0)
            throw new InvalidDataException(CoreText.T($"無法讀取像素：{path}", $"Could not read pixels: {path}"));
        var data = scope.Add(Native.CGDataProviderCopyData(provider));
        var bytes = Data(data);
        var stride = checked((int)Native.CGImageGetBytesPerRow(ctxImage));
        var output = new ushort[checked(width * height * 4)];
        var little = (Native.CGImageGetBitmapInfo(ctxImage) & 0x7000) == 0x2000;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * stride + x * 16;
                for (var c = 0; c < 4; c++)
                {
                    var f = little
                        ? BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(at + c * 4, 4))
                        : BinaryPrimitives.ReadSingleBigEndian(bytes.Slice(at + c * 4, 4));
                    output[(y * width + x) * 4 + c] = Quant16(float.IsFinite(f) ? f : 0f);
                }
            }
        // 反預乘：RGB = RGB * 65535 / A。
        for (var i = 0; i < output.Length; i += 4)
        {
            var a = output[i + 3];
            if (a is > 0 and < 65535)
            {
                output[i] = (ushort)Math.Min((long)output[i] * 65535 / a, 65535);
                output[i + 1] = (ushort)Math.Min((long)output[i + 1] * 65535 / a, 65535);
                output[i + 2] = (ushort)Math.Min((long)output[i + 2] * 65535 / a, 65535);
            }
            else if (a == 0)
            {
                output[i] = output[i + 1] = output[i + 2] = 0;
            }
        }
        return new Raster(width, height, output, srgb);
    }

    /// <summary>
    /// CGImage 光柵 → 16-bit 直向 RGBA：依原生位元序／行距／alpha 佈局逐像素讀取。
    /// 支援 8/16-bit 整數與 32-bit 浮點分量、Gray/RGB/CMYK 色彩模型、
    /// alpha first/last/none/premultiplied。
    /// </summary>
    private static Raster ReadRaster(nint image, string? path, OwnedObjects scope)
    {
        var width = checked((int)Native.CGImageGetWidth(image));
        var height = checked((int)Native.CGImageGetHeight(image));
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException(CoreText.T($"影像過大：{path}", $"Image too large: {path}"));
        var colorSpace = Native.CGImageGetColorSpace(image);
        var model = colorSpace != 0 ? Native.CGColorSpaceGetModel(colorSpace) : -1; // 0=Gray 1=RGB 2=CMYK 5=Indexed
        var bpc = checked((int)Native.CGImageGetBitsPerComponent(image));
        var bpp = checked((int)Native.CGImageGetBitsPerPixel(image));
        var bitmapInfo = Native.CGImageGetBitmapInfo(image);
        var isFloat = (bitmapInfo & 0x100) != 0;
        var channels = bpc > 0 && bpp > 0 && bpp % bpc == 0 ? bpp / bpc : -1;
        var componentOk = isFloat ? bpc is 16 or 32 or 64 : bpc is 8 or 16;
        if (model == 5 || channels < 1 || !componentOk)
        {
            // 子位元（1/2/4-bit）與索引色 CGImage：ImageIO 常已展開；若原始格式仍
            // 不可直接逐分量讀取，改由 CGBitmapContext 以來源色彩空間正規化為
            // 16-bit Gray/RGBA（避免輸出假黑色或調色盤索引當顏色）。
            image = DrawNormalized(image, model, width, height, path, scope);
            colorSpace = Native.CGImageGetColorSpace(image);
            model = colorSpace != 0 ? Native.CGColorSpaceGetModel(colorSpace) : -1;
            bpc = checked((int)Native.CGImageGetBitsPerComponent(image));
            bpp = checked((int)Native.CGImageGetBitsPerPixel(image));
            bitmapInfo = Native.CGImageGetBitmapInfo(image);
            isFloat = (bitmapInfo & 0x100) != 0;
            channels = bpc > 0 && bpp > 0 && bpp % bpc == 0 ? bpp / bpc : -1;
            if (channels < 1 || !(isFloat ? bpc is 16 or 32 or 64 : bpc is 8 or 16))
                throw new InvalidDataException(CoreText.T($"無法讀取像素：{path}", $"Could not read pixels: {path}"));
        }
        var provider = Native.CGImageGetDataProvider(image);
        if (provider == 0)
            throw new InvalidDataException(CoreText.T($"無法讀取像素：{path}", $"Could not read pixels: {path}"));
        var data = scope.Add(Native.CGDataProviderCopyData(provider));
        var bytes = Data(data);
        var stride = checked((int)Native.CGImageGetBytesPerRow(image));
        var alphaInfo = (int)(bitmapInfo & 0x1F);
        var byteOrder = (int)(bitmapInfo & 0x7000);
        // CGImageAlphaInfo：None=0, PremultLast=1, PremultFirst=2, Last=3, First=4, NoneSkipLast=5, NoneSkipFirst=6, Only=7
        var hasAlpha = alphaInfo is 1 or 2 or 3 or 4;
        var alphaLast = alphaInfo is 1 or 3;
        var alphaFirst = alphaInfo is 2 or 4;
        var premult = alphaInfo is 1 or 2;
        var alphaOnly = alphaInfo == 7;
        // 32bpp little-endian（ByteOrder32Little=2<<12）：記憶體順序為邏輯
        // RGBA/X 順序的 32-bit little-endian 反轉——PremultLast/Last=A,B,G,R；
        // PremultFirst/First=B,G,R,A；None/SkipLast/Only=X,B,G,R；
        // NoneSkipFirst=B,G,R,X。不透明影像同樣如此，padding 欄位不得當成 R/A 讀。
        var reverse32Le = byteOrder == 0x2000 && bpc == 8 && channels == 4;
        var output = new ushort[checked(width * height * 4)];

        for (var y = 0; y < height; y++)
        {
            var row = bytes[(y * stride)..];
            for (var x = 0; x < width; x++)
            {
                var at = x * (bpp / 8);
                int r, g, b, a = 65535;
                if (alphaOnly) // Alpha-only：顏色為 0，alpha 為分量本身
                {
                    r = g = b = 0;
                    a = Component(row, at, bpc, byteOrder, isFloat);
                }
                else if (model == 0 && channels is 1 or 2) // Gray（1ch）或 Gray+Alpha（2ch）
                {
                    var gOff = hasAlpha && channels == 2 && !alphaLast ? bpc / 8 : 0; // alphaFirst：灰階在 alpha 之後
                    r = g = b = Component(row, at + gOff, bpc, byteOrder, isFloat);
                    if (hasAlpha && channels == 2)
                    {
                        a = Component(row, at + (alphaLast ? bpc / 8 : 0), bpc, byteOrder, isFloat);
                        if (premult && a is > 0 and < 65535)
                            r = g = b = (int)Math.Min((long)r * 65535 / a, 65535);
                        else if (premult && a == 0)
                            r = g = b = 0;
                    }
                }
                else if (model == 2 && channels is 4 or 5) // CMYK(+A)：上游 naive 分支
                {
                    var c = Component(row, at, bpc, byteOrder, isFloat);
                    var m = Component(row, at + bpc / 8, bpc, byteOrder, isFloat);
                    var yc = Component(row, at + bpc / 8 * 2, bpc, byteOrder, isFloat);
                    var k = Component(row, at + bpc / 8 * 3, bpc, byteOrder, isFloat);
                    var kk = 65535 - k;
                    r = (int)((long)(65535 - c) * kk / 65535);
                    g = (int)((long)(65535 - m) * kk / 65535);
                    b = (int)((long)(65535 - yc) * kk / 65535);
                    if (hasAlpha && channels == 5)
                        a = Component(row, at + bpc / 8 * 4, bpc, byteOrder, isFloat);
                }
                else // RGB(+A)
                {
                    var stride8 = bpc / 8;
                    if (reverse32Le) // 32bpp little-endian：alphaLast=ABGR，alphaFirst=BGRA，不透明=XBGR／BGRX
                    {
                        if (alphaLast)
                        {
                            a = Component(row, at, bpc, byteOrder, isFloat);
                            b = Component(row, at + stride8, bpc, byteOrder, isFloat);
                            g = Component(row, at + stride8 * 2, bpc, byteOrder, isFloat);
                            r = Component(row, at + stride8 * 3, bpc, byteOrder, isFloat);
                        }
                        else if (alphaFirst)
                        {
                            b = Component(row, at, bpc, byteOrder, isFloat);
                            g = Component(row, at + stride8, bpc, byteOrder, isFloat);
                            r = Component(row, at + stride8 * 2, bpc, byteOrder, isFloat);
                            a = Component(row, at + stride8 * 3, bpc, byteOrder, isFloat);
                        }
                        else if (alphaInfo == 6) // NoneSkipFirst：B,G,R,X（末位 padding）
                        {
                            b = Component(row, at, bpc, byteOrder, isFloat);
                            g = Component(row, at + stride8, bpc, byteOrder, isFloat);
                            r = Component(row, at + stride8 * 2, bpc, byteOrder, isFloat);
                        }
                        else // None／NoneSkipLast：X,B,G,R（首位 padding）
                        {
                            b = Component(row, at + stride8, bpc, byteOrder, isFloat);
                            g = Component(row, at + stride8 * 2, bpc, byteOrder, isFloat);
                            r = Component(row, at + stride8 * 3, bpc, byteOrder, isFloat);
                        }
                    }
                    else // 標準 RGBA / ARGB / XRGB；首位 padding 也必須略過，但不當作 alpha。
                    {
                        var aIdx = hasAlpha ? (alphaLast ? channels - 1 : 0) : -1;
                        var c0 = Component(row, at, bpc, byteOrder, isFloat);
                        var c1 = channels >= 2 ? Component(row, at + stride8, bpc, byteOrder, isFloat) : 0;
                        var c2 = channels >= 3 ? Component(row, at + stride8 * 2, bpc, byteOrder, isFloat) : 0;
                        var c3 = channels >= 4 ? Component(row, at + stride8 * 3, bpc, byteOrder, isFloat) : 0;
                        if ((aIdx == 0 || alphaInfo == 6) && channels >= 4) (c0, c1, c2) = (c1, c2, c3);
                        r = c0; g = c1; b = c2;
                        if (hasAlpha) a = Component(row, at + stride8 * aIdx, bpc, byteOrder, isFloat);
                    }
                    if (premult && a is > 0 and < 65535)
                    {
                        r = (int)Math.Min((long)r * 65535 / a, 65535);
                        g = (int)Math.Min((long)g * 65535 / a, 65535);
                        b = (int)Math.Min((long)b * 65535 / a, 65535);
                    }
                    else if (premult && a == 0)
                    {
                        r = g = b = 0;
                    }
                }
                var o = (y * width + x) * 4;
                output[o] = (ushort)Math.Clamp(r, 0, 65535);
                output[o + 1] = (ushort)Math.Clamp(g, 0, 65535);
                output[o + 2] = (ushort)Math.Clamp(b, 0, 65535);
                output[o + 3] = (ushort)Math.Clamp(a, 0, 65535);
            }
        }
        return new Raster(width, height, output, colorSpace);
    }

    /// <summary>
    /// 無法直接逐分量讀取的 CGImage（子位元、索引色等）經 CGBitmapContext 重繪為
    /// 16-bit Gray（1ch）或 RGBA（4ch，alphaLast）。目標空間沿用來源色彩空間：
    /// 索引色以 base space（調色盤展開）為準，無空間時退回 sRGB；Gray→Gray、
    /// RGB→RGB，其餘（Lab/DeviceN/無基底空間）誠實擲出例外。
    /// </summary>
    private static nint DrawNormalized(nint image, int model, int width, int height, string? path, OwnedObjects scope)
    {
        var sourceSpace = Native.CGImageGetColorSpace(image);
        var target = sourceSpace;
        if (model == 5)
        {
            target = Native.CGColorSpaceGetBaseColorSpace(sourceSpace);
            if (target == 0 || Native.CGColorSpaceGetModel(target) != 1)
                throw new InvalidDataException(CoreText.T($"不支援的影像色彩空間：{path}", $"Unsupported image color space: {path}"));
        }
        if (target == 0)
            target = model == 0
                ? scope.Add(Native.CGColorSpaceCreateDeviceGray())
                : scope.Add(Native.CGColorSpaceCreateDeviceRGB());
        var targetModel = Native.CGColorSpaceGetModel(target);
        nuint channels;
        uint bitmapInfo;
        if (targetModel == 0) { channels = 1; bitmapInfo = 0u; } // 16-bit Gray，AlphaNone
        else if (targetModel == 1) { channels = 4; bitmapInfo = AlphaLast16; }
        else throw new InvalidDataException(CoreText.T($"不支援的影像色彩空間：{path}", $"Unsupported image color space: {path}"));
        var ctx = Native.CGBitmapContextCreate(0, (nuint)width, (nuint)height, 16,
            (nuint)width * channels * 2, target, bitmapInfo);
        if (ctx == 0)
            throw new InvalidDataException(CoreText.T($"無法正規化影像：{path}", $"Could not normalize image: {path}"));
        scope.Add(ctx);
        var rect = new Native.CGRect(0, 0, width, height);
        Native.CGContextDrawImage(ctx, rect, image);
        var normalized = Native.CGBitmapContextCreateImage(ctx);
        if (normalized == 0)
            throw new InvalidDataException(CoreText.T($"無法正規化影像：{path}", $"Could not normalize image: {path}"));
        scope.Add(normalized);
        return normalized;
    }

    private static int Component(ReadOnlySpan<byte> row, int at, int bpc, int byteOrder, bool isFloat)
    {
        if (isFloat)
        {
            var f = bpc switch
            {
                16 => (double)BitConverter.ToHalf(row.Slice(at, 2)),
                32 => byteOrder == 0x2000
                    ? BinaryPrimitives.ReadSingleLittleEndian(row.Slice(at, 4))
                    : BinaryPrimitives.ReadSingleBigEndian(row.Slice(at, 4)),
                64 => byteOrder == 0x2000
                    ? BinaryPrimitives.ReadDoubleLittleEndian(row.Slice(at, 8))
                    : BinaryPrimitives.ReadDoubleBigEndian(row.Slice(at, 8)),
                _ => 0,
            };
            return (int)Math.Clamp(Math.Round(f * 65535), 0, 65535);
        }
        return bpc switch
        {
            8 => (int)Math.Round(row[at] * 65535 / 255.0),
            16 => byteOrder == 0x1000 || (byteOrder == 0 && BitConverter.IsLittleEndian)
                ? BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(at, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(row.Slice(at, 2)),
            _ => 0,
        };
    }

    /// <summary>托管 16-bit RGBA → sRGB CGImage（16-bit 整數，alphaLast）。</summary>
    private static nint CreateRgba16Image(int width, int height, ushort[] rgba, nint colorSpace, OwnedObjects scope)
    {
        fixed (ushort* pointer = rgba)
        {
            var data = scope.Add(Native.CFDataCreate(0, (nint)pointer, rgba.Length * sizeof(ushort)));
            var provider = scope.Add(Native.CGDataProviderCreateWithCFData(data));
            var color = colorSpace != 0 ? colorSpace
                : scope.Add(Native.CGColorSpaceCreateWithName(NameRef(Frameworks.Graphics, "kCGColorSpaceSRGB")));
            var image = Native.CGImageCreate((nuint)width, (nuint)height, 16, 64, (nuint)width * 8, color,
                AlphaLast16, provider, 0, 0, 0);
            if (image == 0) throw new InvalidDataException("Could not create CGImage from buffer.");
            scope.Add(image);
            return image;
        }
    }

    // ---------- 匯出編碼 ----------

    /// <summary>
    /// 以 ImageIO 編碼 JPEG（8-bit sRGB，丟棄 alpha）／PNG16／TIFF16（16-bit RGBA，alphaLast）。
    /// 已編碼樣本直接標籤：CGImage 沿用像素編碼的色彩空間，不經 ColorSync 轉換。
    /// EXIF 以 canonical 位元組在檔案層注入（JPEG APP1／PNG eXIf），TIFF 由呼叫端
    /// <see cref="TiffMetadata.Append"/> 附加。Orientation 一律為 1（像素已轉正）。ProPhoto 僅限 TIFF16／PNG16。
    /// </summary>
    internal static void Encode(SourceImage image, string path, ExportFormat format, int quality, byte[]? exif)
    {
        RequireApple();
        RgbEncodingInfo.Validate(image.Encoding);
        var proPhoto = image.Encoding == RgbEncoding.ProPhoto;
        if (proPhoto && format is not (ExportFormat.Tiff16 or ExportFormat.Png16))
            throw new InvalidDataException(CoreText.T(
                $"ProPhoto RGB 僅支援 16-bit TIFF／PNG，不支援 {format}。",
                $"ProPhoto RGB is only supported for 16-bit TIFF/PNG, not {format}."));
        var w = image.Width;
        var h = image.Height;
        using var scope = new OwnedObjects();
        var urlBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        var url = scope.Add(Native.CFURLCreateFromFileSystemRepresentation(0, urlBytes, urlBytes.Length, 0));
        var uti = format switch
        {
            ExportFormat.Jpeg => "public.jpeg",
            ExportFormat.Png16 => "public.png",
            ExportFormat.Tiff16 => "public.tiff",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var destination = Native.CGImageDestinationCreateWithURL(url, Text(scope, uti), 1, 0);
        if (destination == 0)
            throw new InvalidDataException(CoreText.T($"無法建立輸出：{path}", $"Could not create output: {path}"));
        scope.Add(destination);

        var outputSpace = proPhoto ? ProPhotoColorSpace(scope) : scope.Add(Native.CGColorSpaceCreateWithName(NameRef(Frameworks.Graphics, "kCGColorSpaceSRGB")));
        nint cgImage;
        if (format == ExportFormat.Jpeg)
        {
            var rgba = image.Rgba16;
            var rgb = new byte[checked(w * h * 3)];
            for (int s = 0, d = 0; d < rgb.Length; s += 4, d += 3)
            {
                rgb[d] = (byte)((rgba[s] + 128) / 257);
                rgb[d + 1] = (byte)((rgba[s + 1] + 128) / 257);
                rgb[d + 2] = (byte)((rgba[s + 2] + 128) / 257);
            }
            fixed (byte* p = rgb)
            {
                var data = scope.Add(Native.CFDataCreate(0, (nint)p, rgb.Length));
                var provider = scope.Add(Native.CGDataProviderCreateWithCFData(data));
                cgImage = scope.Add(Native.CGImageCreate((nuint)w, (nuint)h, 8, 24, (nuint)w * 3, outputSpace,
                    0u, provider, 0, 1, 0));
            }
        }
        else
        {
            fixed (ushort* p = image.Rgba16)
            {
                var data = scope.Add(Native.CFDataCreate(0, (nint)p, image.Rgba16.Length * sizeof(ushort)));
                var provider = scope.Add(Native.CGDataProviderCreateWithCFData(data));
                cgImage = scope.Add(Native.CGImageCreate((nuint)w, (nuint)h, 16, 64, (nuint)w * 8, outputSpace,
                    AlphaLast16, provider, 0, 1, 0));
            }
        }

        var props = format switch
        {
            ExportFormat.Jpeg => Dict(scope,
                ("Orientation", Num(scope, 1)),
                ("kCGImageDestinationLossyCompressionQuality", NumFloat(scope, quality / 100f)),
                ("kCGImageDestinationMergeMetadata", Native.kCFBooleanTrue())),
            ExportFormat.Tiff16 => Dict(scope,
                ("Orientation", Num(scope, 1)),
                ("{TIFF}", Dict(scope, ("Compression", Num(scope, 8)))), // 8 = Adobe Deflate（Zip）
                ("kCGImageDestinationMergeMetadata", Native.kCFBooleanTrue())),
            _ => Dict(scope,
                ("Orientation", Num(scope, 1)),
                ("kCGImageDestinationMergeMetadata", Native.kCFBooleanTrue())),
        };
        Native.CGImageDestinationAddImage(destination, cgImage, props);
        if (Native.CGImageDestinationFinalize(destination) == 0)
            throw new InvalidDataException(CoreText.T($"輸出編碼失敗：{path}", $"Output encode failed: {path}"));

        // ImageIO 不為 plain sRGB 嵌入可用 ICC（JPEG 無 APP2、PNG 只有 sRGB 意圖塊），
        // 且自帶方向 stub 會與 canonical EXIF 重複：一次檔案層改寫同時注入 ICC 與 EXIF。
        // ProPhoto：一律注入 canonical ICC 精確位元組（PNG iCCP／TIFF 34675），不採用 ImageIO 重建的等效設定檔；
        // EXIF 重新正規化為 Uncalibrated，避免 sRGB 互通性宣告與 ICC 矛盾。
        byte[]? icc = null;
        if (proPhoto)
        {
            icc = RgbEncodingInfo.ProPhotoProfile;
            if (exif is { Length: > 6 })
                exif = TiffMetadata.Normalize(exif, (uint)w, (uint)h, encoding: RgbEncoding.ProPhoto);
        }
        else if (format is ExportFormat.Jpeg or ExportFormat.Png16)
        {
            var iccRef = Native.CGColorSpaceCopyICCData(outputSpace);
            if (iccRef == 0)
                throw new InvalidDataException("Could not obtain the output sRGB ICC profile.");
            scope.Add(iccRef);
            icc = DataBytes(iccRef);
            if (icc.Length == 0)
                throw new InvalidDataException("The output sRGB ICC profile is empty.");
        }
        if (format == ExportFormat.Tiff16 && icc is { Length: > 0 })
            ReplaceTiffIcc(path, icc);
        else if (icc is { Length: > 0 } || exif is { Length: > 6 })
            InjectMetadata(path, format, exif, icc);
    }

    /// <summary>以 canonical ProPhoto ICC 位元組建立 CGColorSpace（CGImage 像素直接標籤，不轉換）。</summary>
    private static nint ProPhotoColorSpace(OwnedObjects scope)
    {
        var profile = RgbEncodingInfo.ProPhotoProfile;
        fixed (byte* p = profile)
        {
            var data = scope.Add(Native.CFDataCreate(0, (nint)p, profile.Length));
            var space = Native.CGColorSpaceCreateWithICCData(data);
            if (space == 0)
                throw new InvalidDataException("Could not create the ProPhoto output color space.");
            return scope.Add(space);
        }
    }

    /// <summary>
    /// 以 canonical ICC 精確位元組取代 TIFF 34675 設定檔（ImageIO 會寫入等效但非逐位元組的重建設定檔）。
    /// 保留既有目錄結構：只改寫 34675 的計數／位移並在檔尾附加位元組，其餘 entry 不動。
    /// </summary>
    private static void ReplaceTiffIcc(string path, byte[] icc)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
        var byteOrder = reader.ReadUInt16();
        var little = byteOrder == 0x4949;
        ushort Read16() => little ? reader.ReadUInt16() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
        uint Read32() => little ? reader.ReadUInt32() : BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
        void Write32(uint value)
        {
            if (little) writer.Write(value);
            else writer.Write(BinaryPrimitives.ReverseEndianness(value));
        }
        if (byteOrder is not (0x4949 or 0x4D4D) || Read16() != 42)
            throw new InvalidDataException("Expected classic TIFF output.");
        var ifd = Read32();
        if (ifd > file.Length - 2)
            throw new InvalidDataException("Invalid TIFF output directory.");
        file.Position = ifd;
        var count = Read16();
        long? iccEntry = null;
        for (var i = 0; i < count; i++)
        {
            var pos = file.Position;
            var tag = Read16();
            var type = Read16();
            var components = Read32();
            var value = Read32();
            if (tag == 34675)
            {
                if (type != 1 && type != 7)
                    throw new InvalidDataException("Unexpected TIFF ICC entry type.");
                if (components > file.Length)
                    throw new InvalidDataException("Invalid TIFF ICC entry.");
                iccEntry = pos;
            }
        }
        if (iccEntry is null)
            throw new InvalidDataException("TIFF output carries no ICC profile entry to replace.");
        file.Position = file.Length;
        if ((file.Position & 1) != 0) writer.Write((byte)0);
        var offset = checked((uint)file.Position);
        writer.Write(icc);
        file.Position = iccEntry.Value + 4;
        Write32((uint)icc.Length);
        Write32(offset);
    }

    /// <summary>
    /// JPEG：SOI 後寫 canonical APP1（Exif）＋分片 APP2（ICC_PROFILE），丟棄 ImageIO 自帶
    /// 方向 stub APP1 與舊 ICC APP2，只留單一 canonical EXIF；PNG：第一個 IDAT 前寫 iCCP＋eXIf，
    /// 丟棄舊 sRGB／iCCP／eXIf（sRGB 意圖塊不得與 iCCP 共存）。經臨時檔一次流式改寫
    /// （8 KiB 緩衝），再原子覆蓋原輸出，不把整檔讀入記憶體。
    /// </summary>
    private static void InjectMetadata(string path, ExportFormat format, byte[]? exif, byte[]? icc)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".exif.tmp";
        try
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (format == ExportFormat.Jpeg)
                    InjectJpegMetadata(input, output, exif, icc);
                else if (format == ExportFormat.Png16)
                    InjectPngMetadata(input, output, exif, icc);
                else
                    return;
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void InjectJpegMetadata(FileStream input, FileStream output, byte[]? exif, byte[]? icc)
    {
        Span<byte> soi = stackalloc byte[2];
        if (input.Read(soi) != 2 || soi[0] != 0xff || soi[1] != 0xd8)
            throw new InvalidDataException("JPEG output missing SOI.");
        output.Write(soi);
        if (exif is { Length: > 6 })
        {
            var segLen = exif.Length + 2;
            if (segLen > 65535)
                throw new InvalidDataException("JPEG EXIF exceeds the APP1 size limit.");
            Span<byte> head = stackalloc byte[4];
            head[0] = 0xff; head[1] = 0xe1;
            BinaryPrimitives.WriteUInt16BigEndian(head.Slice(2, 2), (ushort)segLen);
            output.Write(head);
            output.Write(exif);
        }
        if (icc is { Length: > 0 })
            WriteJpegIcc(output, icc);
        // 標頭段逐段搬移：APP1 Exif stub 與 APP2 ICC_PROFILE 一律丟棄（已被 canonical 取代），
        // 其餘段（JFIF／Photoshop／DQT／DHT／DRI…）原樣保留；SOS 起為掃描資料，直接串流複製。
        Span<byte> head2 = stackalloc byte[4];
        Span<byte> prefix = stackalloc byte[12];
        while (true)
        {
            int mark;
            do { mark = input.ReadByte(); } while (mark >= 0 && mark != 0xff);
            int code;
            do { code = input.ReadByte(); } while (code == 0xff);
            if (mark < 0 || code < 0)
                throw new InvalidDataException("JPEG output truncated before SOS.");
            if (code == 0xda) // SOS：標記＋段頭＋掃描資料原樣複製
            {
                if (input.Read(head2[..2]) != 2)
                    throw new InvalidDataException("JPEG output truncated at SOS.");
                var body = ((head2[0] << 8) | head2[1]) - 2;
                if (body < 0 || input.Length - input.Position < body)
                    throw new InvalidDataException("JPEG output truncated at SOS.");
                output.Write([(byte)0xff, (byte)code]);
                output.Write(head2[..2]);
                CopyExactly(input, output, body);
                input.CopyTo(output, 8192);
                return;
            }
            if (code is 0xd8 or >= 0xd0 and <= 0xd7 or 0x01)
            {
                output.Write([(byte)0xff, (byte)code]);
                continue;
            }
            if (input.Read(head2[..2]) != 2)
                throw new InvalidDataException("JPEG output has a truncated segment.");
            var len = (head2[0] << 8) | head2[1];
            if (len < 2 || input.Length - input.Position < len - 2)
                throw new InvalidDataException("JPEG output has a truncated segment.");
            if (code == 0xe1 && len - 2 >= 6)
            {
                if (input.Read(prefix[..6]) != 6)
                    throw new InvalidDataException("JPEG output has a truncated segment.");
                if (prefix[..6].SequenceEqual("Exif\0\0"u8))
                {
                    input.Position += len - 2 - 6;
                    continue;
                }
                output.Write([(byte)0xff, (byte)code]);
                output.Write(head2[..2]);
                output.Write(prefix[..6]);
                CopyExactly(input, output, len - 2 - 6);
                continue;
            }
            if (code == 0xe2 && len - 2 >= 12)
            {
                if (input.Read(prefix[..12]) != 12)
                    throw new InvalidDataException("JPEG output has a truncated segment.");
                if (prefix.SequenceEqual("ICC_PROFILE\0"u8))
                {
                    input.Position += len - 2 - 12;
                    continue;
                }
                output.Write([(byte)0xff, (byte)code]);
                output.Write(head2[..2]);
                output.Write(prefix[..12]);
                CopyExactly(input, output, len - 2 - 12);
                continue;
            }
            output.Write([(byte)0xff, (byte)code]);
            output.Write(head2[..2]);
            CopyExactly(input, output, len - 2);
        }
    }

    /// <summary>ICC 以 APP2 ICC_PROFILE 分片寫出（65535 位元組段長扣除 2＋14 位元組標頭）。</summary>
    private static void WriteJpegIcc(FileStream output, byte[] icc)
    {
        const int maxData = 65535 - 2 - 14;
        var count = (icc.Length + maxData - 1) / maxData;
        if (count is < 1 or > 255)
            throw new InvalidDataException("JPEG ICC profile exceeds the APP2 size limit.");
        Span<byte> head = stackalloc byte[4 + 14];
        head[0] = 0xff; head[1] = 0xe2;
        "ICC_PROFILE\0"u8.CopyTo(head.Slice(4, 12));
        for (var i = 0; i < count; i++)
        {
            var chunk = icc.AsSpan(i * maxData, Math.Min(maxData, icc.Length - i * maxData));
            BinaryPrimitives.WriteUInt16BigEndian(head.Slice(2, 2), (ushort)(chunk.Length + 14 + 2));
            head[16] = (byte)(i + 1);
            head[17] = (byte)count;
            output.Write(head);
            output.Write(chunk);
        }
    }

    private static void InjectPngMetadata(FileStream input, FileStream output, byte[]? exif, byte[]? icc)
    {
        Span<byte> sig = stackalloc byte[8];
        if (input.Read(sig) != 8 || !sig.SequenceEqual(PngSignature))
            throw new InvalidDataException("PNG output missing signature.");
        output.Write(sig);
        byte[]? iccp = icc is { Length: > 0 } ? IccpChunk(icc) : null;
        byte[]? exifChunk = exif is { Length: > 6 } ? ExifChunk(exif) : null;
        Span<byte> chunkHead = stackalloc byte[8];
        var inserted = false;
        while (input.Read(chunkHead) == 8)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(chunkHead[..4]);
            if (len > int.MaxValue - 8 || input.Length - input.Position < len + 4)
                throw new InvalidDataException("PNG output has a truncated chunk.");
            var type = chunkHead.Slice(4, 4);
            // 舊 sRGB／iCCP／eXIf 一律丟棄：sRGB 不得與 iCCP 共存，eXIf 只留 canonical 單塊。
            if (type.SequenceEqual("sRGB"u8) || type.SequenceEqual("iCCP"u8) || type.SequenceEqual("eXIf"u8))
            {
                input.Position += len + 4;
                continue;
            }
            if (!inserted && type.SequenceEqual("IDAT"u8))
            {
                if (iccp is not null) output.Write(iccp);
                if (exifChunk is not null) output.Write(exifChunk);
                inserted = true;
            }
            output.Write(chunkHead);
            CopyExactly(input, output, len + 4);
            if (type.SequenceEqual("IEND"u8)) break;
        }
        if (!inserted && (iccp is not null || exifChunk is not null))
            throw new InvalidDataException("PNG output missing IDAT.");
    }

    /// <summary>canonical EXIF（去 6 位元組標頭）封裝為單一 eXIf chunk（含 CRC）。</summary>
    private static byte[] ExifChunk(byte[] exif)
    {
        var chunk = exif.AsSpan(6);
        var header = new byte[12 + chunk.Length];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)chunk.Length);
        "eXIf"u8.CopyTo(header.AsSpan(4));
        chunk.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + chunk.Length),
            Crc32(header.AsSpan(4, 4 + chunk.Length)));
        return header;
    }

    /// <summary>sRGB ICC 封裝為 iCCP chunk（profile name "icc"＋zlib 壓縮）。</summary>
    private static byte[] IccpChunk(byte[] icc)
    {
        using var ms = new MemoryStream();
        ms.Write("icc\0"u8);
        ms.WriteByte(0); // compression method 0 = zlib
        using (var ds = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(icc);
        var payload = ms.ToArray();
        var header = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        "iCCP"u8.CopyTo(header.AsSpan(4));
        payload.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + payload.Length),
            Crc32(header.AsSpan(4, 4 + payload.Length)));
        return header;
    }

    /// <summary>串流複製指定長度（8 KiB 分塊）。</summary>
    private static void CopyExactly(Stream input, Stream output, long count)
    {
        Span<byte> buffer = stackalloc byte[8192];
        while (count > 0)
        {
            var take = (int)Math.Min(buffer.Length, count);
            var read = input.Read(buffer[..take]);
            if (read <= 0) throw new EndOfStreamException("Unexpected end of stream while copying.");
            output.Write(buffer[..read]);
            count -= read;
        }
    }

    /// <summary>PNG chunk CRC-32（ISO 3309；無外部依賴）。</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>非 RAW 檔的拍攝資訊：canonical EXIF 位元組經托管 <see cref="FilmBatch.Raw.Meta.MetadataReader"/> 讀取。</summary>
    internal static (DateTime? Taken, string? Camera, string? Lens, int? Iso, double? ExposureSeconds, double? FNumber)
        ReadExif(string path)
    {
        var exif = ExtractExifBytes(path, 0, 0);
        if (exif is not { Length: > 6 }) return (null, null, null, null, null, null);
        var meta = FilmBatch.Raw.Meta.MetadataReader.FromTiff(FilmBatch.Raw.Tiff.TiffFile.Parse(exif.AsMemory(6)));
        DateTime? taken = null;
        if (meta.CaptureTime is { } t)
        {
            try { taken = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Millis); }
            catch (ArgumentOutOfRangeException) { }
        }
        return (taken,
            PhotoExif.CameraName(meta.Make, meta.Model),
            meta.LensModel,
            meta.Iso is <= int.MaxValue ? (int?)meta.Iso : null,
            meta.ExposureTime,
            meta.FNumber);
    }

    /// <summary>範例照片用的 16-bit RGB TIFF（Zip 壓縮；無 alpha）。</summary>
    internal static void WriteTiff16Rgb(string path, int width, int height, ushort[] rgb)
    {
        RequireApple();
        if (rgb.Length != checked((long)width * height * 3)) throw new ArgumentOutOfRangeException(nameof(rgb));
        using var scope = new OwnedObjects();
        var urlBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        var url = scope.Add(Native.CFURLCreateFromFileSystemRepresentation(0, urlBytes, urlBytes.Length, 0));
        var destination = Native.CGImageDestinationCreateWithURL(url, Text(scope, "public.tiff"), 1, 0);
        if (destination == 0) throw new InvalidDataException($"Could not create output: {path}");
        scope.Add(destination);
        var srgb = scope.Add(Native.CGColorSpaceCreateWithName(NameRef(Frameworks.Graphics, "kCGColorSpaceSRGB")));
        nint cgImage;
        fixed (ushort* p = rgb)
        {
            var data = scope.Add(Native.CFDataCreate(0, (nint)p, rgb.Length * sizeof(ushort)));
            var provider = scope.Add(Native.CGDataProviderCreateWithCFData(data));
            cgImage = scope.Add(Native.CGImageCreate((nuint)width, (nuint)height, 16, 48, (nuint)width * 6, srgb,
                0u | (BitConverter.IsLittleEndian ? 0x1000u : 0x3000u), provider, 0, 0, 0)); // 16Host | AlphaNone
        }
        if (cgImage == 0) throw new InvalidDataException("Could not create CGImage from buffer.");
        var props = Dict(scope,
            ("Orientation", Num(scope, 1)),
            ("{TIFF}", Dict(scope, ("Compression", Num(scope, 8))))); // 8 = Adobe Deflate（Zip）
        Native.CGImageDestinationAddImage(destination, cgImage, props);
        if (Native.CGImageDestinationFinalize(destination) == 0)
            throw new InvalidDataException($"Output encode failed: {path}");
    }

    // ---------- 來源 EXIF 擷取（一般影像） ----------

    /// <summary>
    /// 從 JPEG APP1／PNG eXIf／HEIF（ISO BMFF <c>meta</c> 的 <c>Exif</c> 項目）擷取
    /// canonical EXIF 位元組（含 6 位元組 <c>Exif\0\0</c> 標頭）；TIFF 直接以
    /// <see cref="TiffMetadata.Read"/> 正規化整檔目錄。全程流式掃描，不整檔讀入。
    /// </summary>
    internal static byte[]? ExtractExifBytes(string path, uint width, uint height)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            var got = file.Read(head);
            if (got < 12) return null;
            file.Position = 0;
            if (head[0] == 0xff && head[1] == 0xd8)
                return JpegApp1Exif(file);
            if (head[..8].SequenceEqual(PngSignature))
                return PngExif(file);
            if (head[..4].SequenceEqual("II\x2a\0"u8) || head[..4].SequenceEqual("MM\0\x2a"u8)
                || head[..4].SequenceEqual("II\x2b\0"u8) || head[..4].SequenceEqual("MM\0\x2b"u8))
                return TiffMetadata.Read(path, width, height);
            if (head.Slice(4, 4).SequenceEqual("ftyp"u8))
                return HeifExif(file);
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        // InvalidDataException／OverflowException 向外傳：CopyExifProfile 記錄警告並輸出全新 EXIF。
    }



    /// <summary>JPEG 標記流掃描（與 <see cref="Raw.PreviewJpegColour.ParseMarkers"/> 同規則：
    /// 段間垃圾重同步；SOS/EOI 停）；只取第一個 <c>Exif\0\0</c> APP1。</summary>
    private static byte[]? JpegApp1Exif(FileStream file)
    {
        Span<byte> head = stackalloc byte[4];
        while (true)
        {
            // 找下一個 0xFF 標記字首（容忍段間垃圾）。
            int mark;
            do { mark = file.ReadByte(); } while (mark >= 0 && mark != 0xff);
            int code;
            do { code = file.ReadByte(); } while (code == 0xff);
            if (mark < 0 || code < 0 || code is 0xd9 or 0xda) return null; // EOI / SOS：EXIF 必在掃描資料前
            if (code is 0xd8 or >= 0xd0 and <= 0xd7 or 0x01) continue; // SOI/RST/TEM 等無長度標記
            if (file.Read(head[..2]) != 2) return null;
            var len = (head[0] << 8) | head[1];
            if (len < 2) return null;
            var body = len - 2;
            if (code != 0xe1 || body < 6)
            {
                file.Position += body;
                continue;
            }
            Span<byte> prefix = stackalloc byte[6];
            if (file.Read(prefix) != 6) return null;
            if (!prefix.SequenceEqual("Exif\0\0"u8))
            {
                file.Position += body - 6;
                continue;
            }
            var result = new byte[body];
            "Exif\0\0"u8.CopyTo(result);
            var at = 6;
            while (at < result.Length)
            {
                var read = file.Read(result, at, result.Length - at);
                if (read <= 0) return null;
                at += read;
            }
            return result;
        }
    }

    /// <summary>PNG chunk 標頭掃描到 IEND（eXIf 不要求在 IDAT 前）：只讀標頭與 eXIf 內容，
    /// 其餘資料以位移跳過，記憶體與檔案大小無關。</summary>
    private static byte[]? PngExif(FileStream file)
    {
        Span<byte> sig = stackalloc byte[8];
        if (file.Read(sig) != 8 || !sig.SequenceEqual(PngSignature)) return null;
        Span<byte> chunkHead = stackalloc byte[8];
        while (file.Read(chunkHead) == 8)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(chunkHead[..4]);
            var end = chunkHead.Slice(4, 4).SequenceEqual("IEND"u8);
            var isExif = chunkHead.Slice(4, 4).SequenceEqual("eXIf"u8);
            if (isExif && len is > 0 and <= 4u * 1024 * 1024)
            {
                var result = new byte[checked(len + 6)];
                "Exif\0\0"u8.CopyTo(result);
                var at = 6;
                while (at < result.Length)
                {
                    var read = file.Read(result, at, result.Length - at);
                    if (read <= 0) return null;
                    at += read;
                }
                return result;
            }
            if (len > int.MaxValue - 4 || file.Length - file.Position < len + 4) return null;
            file.Position += len + 4; // 資料 + CRC
            if (end) return null;
        }
        return null;
    }

    // ---------- HEIF/HEIC（ISO BMFF）EXIF 項目擷取 ----------

    private static uint FourCc(string type) =>
        ((uint)type[0] << 24) | ((uint)type[1] << 16) | ((uint)type[2] << 8) | type[3];

    /// <summary>讀取 ISO BMFF box 標頭：回傳 (type, 內容起始, 內容結束)。size=0 延伸到 limit。</summary>
    private static uint ReadBoxHeader(FileStream file, long limit, out long start, out long end)
    {
        start = end = 0;
        Span<byte> head = stackalloc byte[8];
        if (file.Position + 8 > limit || file.Read(head) != 8) return 0;
        var size = (long)BinaryPrimitives.ReadUInt32BigEndian(head[..4]);
        var type = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(4, 4));
        var header = 8;
        if (size == 1)
        {
            Span<byte> ext = stackalloc byte[8];
            if (file.Read(ext) != 8) return 0;
            var large = BinaryPrimitives.ReadUInt64BigEndian(ext);
            if (large > long.MaxValue) throw new InvalidDataException("Invalid HEIF box size.");
            size = (long)large;
            header = 16;
        }
        else if (size == 0)
        {
            size = limit - file.Position + 8;
        }
        if (size < header) throw new InvalidDataException("Invalid HEIF box size.");
        start = file.Position;
        end = Math.Min(limit, checked(start + size - header));
        return type;
    }

    /// <summary>
    /// HEIC/HEIF：於 <c>meta</c> 中找 item_type 為 <c>Exif</c> 的項目（iinf→infe v2+，
    /// v0/v1 以 item_name 辨識），以 iloc extent（construction_method 0）讀出項目位元組。
    /// 項目可能以字面 <c>Exif\0\0</c> 開頭（Apple ImageIO 寫出）或以 4 位元組 TIFF
    /// 位移欄位開頭（ISO 23008-12），兩者皆辨識。
    /// </summary>
    private static byte[]? HeifExif(FileStream file)
    {
        var length = file.Length;
        long iinfStart = -1, iinfEnd = -1, ilocStart = -1;
        while (file.Position < length)
        {
            var type = ReadBoxHeader(file, length, out var start, out var end);
            if (type == 0) break;
            if (type == FourCc("meta"))
            {
                var cursor = start + 4; // FullBox version/flags
                while (cursor < end)
                {
                    file.Position = cursor;
                    var child = ReadBoxHeader(file, end, out var childStart, out var childEnd);
                    if (child == 0) break;
                    if (child == FourCc("iinf") && iinfStart < 0) { iinfStart = childStart; iinfEnd = childEnd; }
                    else if (child == FourCc("iloc") && ilocStart < 0) ilocStart = childStart;
                    cursor = childEnd;
                }
            }
            file.Position = end;
        }
        if (iinfStart < 0 || ilocStart < 0) return null;
        var exifId = FindExifItem(file, iinfStart, iinfEnd);
        if (exifId is null) return null;
        var extents = CollectExtents(file, ilocStart, exifId.Value);
        if (extents.Count == 0) return null;
        var total = extents.Sum(e => e.Length);
        if (total <= 0 || total > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid HEIF Exif item extent.");
        var item = new byte[total];
        var at = 0;
        foreach (var (offset, itemLength) in extents)
        {
            file.Position = offset;
            var remaining = itemLength;
            while (remaining > 0)
            {
                var read = file.Read(item, at, (int)Math.Min(remaining, item.Length - at));
                if (read <= 0) throw new EndOfStreamException("Truncated HEIF Exif item.");
                at += read;
                remaining -= read;
            }
        }
        return HeifItemToExif(item);
    }

    /// <summary>iinf：infe v2+ 的 item_type <c>Exif</c> 回傳 item_ID；v0/v1 以 item_name 辨識。</summary>
    private static uint? FindExifItem(FileStream file, long start, long end)
    {
        Span<byte> buf = stackalloc byte[8];
        file.Position = start;
        if (file.Read(buf[..4]) != 4) return null;
        var version = buf[0];
        var countSize = version == 0 ? 2 : 4;
        if (file.Read(buf[..countSize]) != countSize) return null;
        var count = countSize == 2
            ? (long)BinaryPrimitives.ReadUInt16BigEndian(buf[..2])
            : BinaryPrimitives.ReadUInt32BigEndian(buf[..4]);
        for (var i = 0L; i < count && file.Position < end; i++)
        {
            var type = ReadBoxHeader(file, end, out var infeStart, out var infeEnd);
            if (type == 0) return null;
            if (type != FourCc("infe"))
            {
                file.Position = infeEnd;
                continue;
            }
            file.Position = infeStart;
            if (file.Read(buf[..4]) != 4) return null;
            var infeVersion = buf[0];
            if (infeVersion >= 2)
            {
                var idSize = infeVersion == 2 ? 2 : 4;
                if (file.Read(buf[..idSize]) != idSize) return null;
                var id = idSize == 2
                    ? (uint)BinaryPrimitives.ReadUInt16BigEndian(buf[..2])
                    : BinaryPrimitives.ReadUInt32BigEndian(buf[..4]);
                if (file.Read(buf[..2]) != 2) return null; // item_protection_index
                if (file.Read(buf[..4]) != 4) return null;
                if (BinaryPrimitives.ReadUInt32BigEndian(buf[..4]) == FourCc("Exif"))
                    return id;
            }
            else
            {
                // v0/v1：item_ID＋protection_index 之後為 NUL 結尾字串欄位。
                if (file.Read(buf[..4]) != 4) return null;
                var name = ReadCString(file, infeEnd);
                ReadCString(file, infeEnd); // content_type
                ReadCString(file, infeEnd); // content_encoding
                if (string.Equals(name, "Exif", StringComparison.Ordinal))
                    return BinaryPrimitives.ReadUInt16BigEndian(buf[..2]);
            }
            file.Position = infeEnd;
        }
        return null;
    }

    private static string? ReadCString(FileStream file, long limit)
    {
        var bytes = new List<byte>(16);
        while (file.Position < limit)
        {
            var b = file.ReadByte();
            if (b < 0 || b == 0) break;
            bytes.Add((byte)b);
        }
        return bytes.Count > 0 ? Encoding.ASCII.GetString([.. bytes]) : null;
    }

    /// <summary>iloc：收集 <paramref name="itemId"/> 的所有 extent。construction_method 非 0
    /// （idat 內嵌／項目位移）目前不支援，誠實擲出例外。</summary>
    private static List<(long Offset, long Length)> CollectExtents(FileStream file, long start, uint itemId)
    {
        var extents = new List<(long, long)>(1);
        Span<byte> buf = stackalloc byte[8];
        file.Position = start;
        if (file.Read(buf[..4]) != 4) return extents;
        var version = buf[0];
        if (file.Read(buf[..2]) != 2) return extents;
        var offsetSize = buf[0] >> 4;
        var lengthSize = buf[0] & 0x0f;
        var baseOffsetSize = buf[1] >> 4;
        var indexSize = version is 1 or 2 ? buf[1] & 0x0f : 0;
        var countSize = version < 2 ? 2 : 4;
        if (file.Read(buf[..countSize]) != countSize) return extents;
        var count = countSize == 2
            ? (long)BinaryPrimitives.ReadUInt16BigEndian(buf[..2])
            : BinaryPrimitives.ReadUInt32BigEndian(buf[..4]);
        for (var i = 0L; i < count; i++)
        {
            var idSize = version < 2 ? 2 : 4;
            if (file.Read(buf[..idSize]) != idSize) return extents;
            var id = idSize == 2
                ? (uint)BinaryPrimitives.ReadUInt16BigEndian(buf[..2])
                : BinaryPrimitives.ReadUInt32BigEndian(buf[..4]);
            if (version is 1 or 2)
            {
                if (file.Read(buf[..2]) != 2) return extents;
                var construction = BinaryPrimitives.ReadUInt16BigEndian(buf[..2]) & 0x0f;
                if (id == itemId && construction != 0)
                    throw new InvalidDataException("HEIF Exif item uses an unsupported construction method.");
            }
            if (file.Read(buf[..2]) != 2) return extents; // data_reference_index
            var baseOffset = ReadUint(file, baseOffsetSize);
            if (file.Read(buf[..2]) != 2) return extents;
            var extentCount = BinaryPrimitives.ReadUInt16BigEndian(buf[..2]);
            for (var e = 0; e < extentCount; e++)
            {
                if (indexSize > 0) _ = ReadUint(file, indexSize);
                var offset = baseOffset + ReadUint(file, offsetSize);
                var extentLength = ReadUint(file, lengthSize);
                if (id == itemId && extentLength > 0)
                    extents.Add((checked((long)offset), checked((long)extentLength)));
            }
        }
        return extents;
    }

    /// <summary>iloc 可變欄位寬度（0..8 位元組，big-endian）；0 寬表示欄位缺席＝0，不消耗位元組。</summary>
    private static ulong ReadUint(FileStream file, int size)
    {
        if (size <= 0) return 0;
        if (size > 8) throw new InvalidDataException("Invalid HEIF extent field width.");
        Span<byte> buf = stackalloc byte[8];
        if (file.Read(buf[..size]) != size)
            throw new InvalidDataException("Invalid HEIF extent field.");
        ulong value = 0;
        foreach (var b in buf[..size]) value = (value << 8) | b;
        return value;
    }

    /// <summary>
    /// HEIF Exif 項目 → canonical 位元組：字面 <c>Exif\0\0</c>＋TIFF 時直接回傳；
    /// 否則依 ISO 23008-12 讀首 4 位元組位移（相對欄位之後）定位 TIFF 標頭。
    /// </summary>
    private static byte[]? HeifItemToExif(byte[] item)
    {
        if (item.Length > 10 && item.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8)
            && IsTiffHead(item.AsSpan(6, 4)))
            return item;
        if (item.Length >= 8)
        {
            var tiffAt = BinaryPrimitives.ReadUInt32BigEndian(item);
            // 位移自欄位後算起；容忍實作把位移算自欄位起點。
            foreach (var at in new[] { 4 + (long)tiffAt, (long)tiffAt, 4L })
            {
                if (at >= 0 && at <= item.Length - 4 && IsTiffHead(item.AsSpan((int)at, 4)))
                {
                    var result = new byte[item.Length - at + 6];
                    "Exif\0\0"u8.CopyTo(result);
                    item.AsSpan((int)at).CopyTo(result.AsSpan(6));
                    return result;
                }
            }
            // 最後手段：前 16 位元組內搜尋 TIFF 魔數（非標準寫法）。
            for (var at = 0; at + 4 <= Math.Min(16, item.Length); at++)
                if (IsTiffHead(item.AsSpan(at, 4)))
                {
                    var result = new byte[item.Length - at + 6];
                    "Exif\0\0"u8.CopyTo(result);
                    item.AsSpan(at).CopyTo(result.AsSpan(6));
                    return result;
                }
        }
        throw new InvalidDataException("HEIF Exif item has no recognizable TIFF header.");
    }

    private static bool IsTiffHead(ReadOnlySpan<byte> bytes) =>
        bytes.SequenceEqual("II\x2a\0"u8) || bytes.SequenceEqual("MM\0\x2a"u8);

    // ---------- 嵌入預覽 CMS ----------

    /// <summary>
    /// 裝置樣本 → 線性 Rec.2020 的真正 CMS（LUT／CMYK／Gray 設定檔）：
    /// 以來源 ICC 建立 CGImage，繪入以 <see cref="Raw.PreviewCms.Rec2020Linear"/> ICC
    /// 建立的 32-bit float RGB 內容，讀回線性 Rec.2020 float（HDRI，不削波）。
    /// </summary>
    internal static RgbImage? ToLinearRec2020(byte[] icc, float[] samples, int channels, int width, int height, bool gray)
    {
        var dst = Raw.PreviewCms.Rec2020Linear;
        if (dst is null || samples.Length != (long)width * height * channels)
            return null;
        if (width <= 0 || height <= 0 || channels < 1)
            return null;
        try
        {
            using var scope = new OwnedObjects();
            nint srcSpace, dstSpace;
            fixed (byte* pIcc = icc)
            {
                var iccData = scope.Add(Native.CFDataCreate(0, (nint)pIcc, icc.Length));
                srcSpace = Native.CGColorSpaceCreateWithICCData(iccData);
                if (srcSpace == 0) return null;
                scope.Add(srcSpace);
            }
            fixed (byte* pDst = dst)
            {
                var dstData = scope.Add(Native.CFDataCreate(0, (nint)pDst, dst.Length));
                dstSpace = Native.CGColorSpaceCreateWithICCData(dstData);
                if (dstSpace == 0) return null;
                scope.Add(dstSpace);
            }

            var srcChannels = gray ? 1 : channels;
            var floats = new float[samples.Length];
            Array.Copy(samples, floats, samples.Length);
            nint srcImage;
            fixed (float* p = floats)
            {
                var data = scope.Add(Native.CFDataCreate(0, (nint)p, floats.Length * sizeof(float)));
                var provider = scope.Add(Native.CGDataProviderCreateWithCFData(data));
                var bitmapInfo = 0x100u; // kCGBitmapFloatComponents
                srcImage = Native.CGImageCreate((nuint)width, (nuint)height, 32, (nuint)(32 * srcChannels),
                    (nuint)width * (nuint)srcChannels * 4u, srcSpace, bitmapInfo, provider, 0, 0, 0);
            }
            if (srcImage == 0) return null;
            scope.Add(srcImage);

            var ctx = Native.CGBitmapContextCreate(0, (nuint)width, (nuint)height, 32,
                (nuint)width * 16, dstSpace, 0x100u | 0x2000u | 0x03u); // float | ByteOrder32Little | alphaLast
            if (ctx == 0) return null;
            scope.Add(ctx);
            var rect = new Native.CGRect(0, 0, width, height);
            Native.CGContextDrawImage(ctx, rect, srcImage);
            var outImage = Native.CGBitmapContextCreateImage(ctx);
            if (outImage == 0) return null;
            scope.Add(outImage);
            var provider2 = Native.CGImageGetDataProvider(outImage);
            if (provider2 == 0) return null;
            var outData = scope.Add(Native.CGDataProviderCopyData(provider2));
            var outBytes = Data(outData);
            var stride = checked((int)Native.CGImageGetBytesPerRow(outImage));
            var little = (Native.CGImageGetBitmapInfo(outImage) & 0x7000) == 0x2000;
            var linear = new float[checked((long)width * height * 3)];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var at = y * stride + x * 16;
                    for (var c = 0; c < 3; c++)
                    {
                        var f = little
                            ? BinaryPrimitives.ReadSingleLittleEndian(outBytes.Slice(at + c * 4, 4))
                            : BinaryPrimitives.ReadSingleBigEndian(outBytes.Slice(at + c * 4, 4));
                        linear[(y * width + x) * 3 + c] = float.IsFinite(f) ? f : 0f;
                    }
                }
            return new RgbImage(width, height, linear);
        }
        catch
        {
            return null; // CMS 失敗：按上游分支由呼叫端走 sRGB
        }
    }

    // ---------- CF 輔助 ----------

    /// <summary>每個 Create/Copy 參考集中登記並於 <see cref="Dispose"/> 反向釋放。</summary>
    private sealed class OwnedObjects : IDisposable
    {
        private readonly List<nint> _objects = [];
        public nint Add(nint value)
        {
            if (value == 0) throw new InvalidDataException("Apple native Create/Copy returned NULL.");
            _objects.Add(value);
            return value;
        }
        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--) Native.CFRelease(_objects[i]);
        }
    }

    private static ReadOnlySpan<byte> Data(nint data)
    {
        if (data == 0) throw new InvalidDataException("Expected native CFData.");
        var length = checked((int)Native.CFDataGetLength(data));
        var pointer = Native.CFDataGetBytePtr(data);
        if (length < 0 || (length == 0 && pointer == 0)) throw new InvalidDataException("Invalid CFData.");
        return new ReadOnlySpan<byte>((void*)pointer, length);
    }

    private static byte[] DataBytes(nint data) => Data(data).ToArray();

    private static long Integer(nint dict, string key, OwnedObjects scope)
    {
        var value = DictValue(dict, key, scope);
        if (Native.CFGetTypeID(value) != Native.CFNumberGetTypeID())
            throw new InvalidDataException($"Metadata {key} is not a number.");
        if (Native.CFNumberGetValue(value, 4, out var result) == 0)
            throw new InvalidDataException($"Invalid number for {key}.");
        return result;
    }

    private static long IntegerOptional(nint dict, string key, OwnedObjects scope)
    {
        var value = DictValueOrNull(dict, key, scope);
        if (value == 0 || Native.CFGetTypeID(value) != Native.CFNumberGetTypeID()) return 1;
        return Native.CFNumberGetValue(value, 4, out var result) == 0 ? 1 : result;
    }

    private static nint DictValue(nint dict, string key, OwnedObjects scope)
    {
        var value = DictValueOrNull(dict, key, scope);
        if (value == 0) throw new InvalidDataException($"Missing metadata {key}.");
        return value;
    }

    private static nint DictValueOrNull(nint dict, string key, OwnedObjects scope) =>
        dict == 0 ? 0 : Native.CFDictionaryGetValue(dict, Text(scope, key));

    private static nint Text(OwnedObjects scope, string value) =>
        scope.Add(Native.CFStringCreateWithCString(0, value, 0x08000100));

    private static nint Num(OwnedObjects scope, int value) =>
        scope.Add(Native.CFNumberCreate(0, 3, &value)); // kCFNumberSInt32Type

    private static nint NumFloat(OwnedObjects scope, float value)
    {
        var d = (double)value;
        return scope.Add(Native.CFNumberCreate(0, 13, &d)); // kCFNumberDoubleType
    }

    private static nint Dict(OwnedObjects scope, params (string Key, nint Value)[] pairs)
    {
        var keys = new nint[pairs.Length];
        var values = new nint[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            keys[i] = Text(scope, pairs[i].Key);
            values[i] = pairs[i].Value;
        }
        return scope.Add(Native.CFDictionaryCreate(0, keys, values, keys.Length,
            NativeLibrary.GetExport(Frameworks.Foundation, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(Frameworks.Foundation, "kCFTypeDictionaryValueCallBacks")));
    }

    private static string? CFToString(nint value)
    {
        if (value == 0 || Native.CFGetTypeID(value) != Native.CFStringGetTypeID()) return null;
        var length = checked((int)Native.CFStringGetLength(value));
        var buffer = new byte[length * 4 + 1];
        fixed (byte* p = buffer)
        {
            if (Native.CFStringGetCString(value, (nint)p, buffer.Length, 0x08000100) == 0) return null;
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer.AsSpan(0, end < 0 ? buffer.Length : end));
        }
    }

    /// <summary>Framework 句柄（顯式路徑；不依賴 __Internal）。</summary>
    private static class Frameworks
    {
        private static nint _foundation, _graphics, _imageIo;
        private static readonly object _lock = new();

        internal static nint Foundation => Get(0);
        internal static nint Graphics => Get(1);
        internal static nint ImageIo => Get(2);

        private static nint Get(int which)
        {
            lock (_lock)
            {
                return which switch
                {
                    0 => _foundation != 0 ? _foundation : _foundation = NativeLibrary.Load(Native.Foundation),
                    1 => _graphics != 0 ? _graphics : _graphics = NativeLibrary.Load(Native.Graphics),
                    _ => _imageIo != 0 ? _imageIo : _imageIo = NativeLibrary.Load(Native.ImageIo),
                };
            }
        }
    }

    private static nint NameRef(nint library, string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));

    /// <summary>CoreFoundation／CoreGraphics／ImageIO C API；Create/Copy 由呼叫端管理生命期。</summary>
    private static partial class Native
    {
        internal const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        internal const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        internal const string ImageIo = "/System/Library/Frameworks/ImageIO.framework/ImageIO";

        [LibraryImport(Foundation, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
        [LibraryImport(Foundation)] internal static partial nint CFStringGetLength(nint value);
        [LibraryImport(Foundation)] internal static partial byte CFStringGetCString(nint value, nint buffer, nint size, uint encoding);
        [LibraryImport(Foundation)] internal static partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte[] bytes, nint count, byte directory);
        [LibraryImport(Foundation)] internal static partial nint CFDataCreate(nint allocator, nint bytes, nint count);
        [LibraryImport(Foundation)] internal static partial nint CFDataCreateWithBytesNoCopy(nint allocator, nint bytes, nint count, nint bytesDeallocator);
        [LibraryImport(Foundation)] internal static partial nint CFDataGetLength(nint data);
        [LibraryImport(Foundation)] internal static partial nint CFDataGetBytePtr(nint data);
        [LibraryImport(Foundation)] internal static unsafe partial nint CFNumberCreate(nint allocator, int type, void* value);
        [LibraryImport(Foundation)] internal static partial byte CFNumberGetValue(nint number, int type, out long value);
        [LibraryImport(Foundation)] internal static partial nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);
        [LibraryImport(Foundation)] internal static partial nint CFDictionaryGetValue(nint dictionary, nint key);
        [LibraryImport(Foundation)] internal static partial nint CFGetTypeID(nint value);
        [LibraryImport(Foundation)] internal static partial nint CFNumberGetTypeID();
        [LibraryImport(Foundation)] internal static partial nint CFStringGetTypeID();
        [LibraryImport(Foundation)] internal static partial nint CFBooleanGetTypeID();
        [LibraryImport(Foundation)] internal static partial void CFRelease(nint value);

        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceCreateWithName(nint name);
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceCreateWithICCData(nint data);
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceCopyICCData(nint colorSpace);
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceGetName(nint colorSpace);
        [LibraryImport(Graphics)] internal static partial int CGColorSpaceGetModel(nint colorSpace);
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceGetBaseColorSpace(nint colorSpace);
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceCreateDeviceGray();
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceCreateDeviceRGB();
        [LibraryImport(Graphics)] internal static partial nint CGDataProviderCreateWithCFData(nint data);
        [LibraryImport(Graphics)] internal static partial nint CGImageCreate(nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow, nint colorSpace, uint bitmapInfo, nint provider, nint decode, byte interpolate, int intent);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetWidth(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetHeight(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetBitsPerComponent(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetBitsPerPixel(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetBytesPerRow(nint image);
        [LibraryImport(Graphics)] internal static partial uint CGImageGetBitmapInfo(nint image);
        [LibraryImport(Graphics)] internal static partial nint CGImageGetColorSpace(nint image);
        [LibraryImport(Graphics)] internal static partial nint CGImageGetDataProvider(nint image);
        [LibraryImport(Graphics)] internal static partial nint CGDataProviderCopyData(nint provider);
        [LibraryImport(Graphics)] internal static partial nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint colorSpace, uint bitmapInfo);
        [LibraryImport(Graphics)] internal static partial void CGContextDrawImage(nint context, CGRect rect, nint image);
        [LibraryImport(Graphics)] internal static partial nint CGBitmapContextCreateImage(nint context);

        /// <summary>CGRect（四個 double）：arm64 ABI 以 VFP 暫存器傳遞同質結構，必須依值封送。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct CGRect(double x, double y, double width, double height)
        {
            public readonly double X = x, Y = y, Width = width, Height = height;
        }

        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCreateWithURL(nint url, nint options);
        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCreateWithData(nint data, nint options);
        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCreateImageAtIndex(nint source, nuint index, nint options);
        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCreateThumbnailAtIndex(nint source, nuint index, nint options);
        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCopyPropertiesAtIndex(nint source, nuint index, nint options);
        [LibraryImport(ImageIo)] internal static partial nint CGImageDestinationCreateWithURL(nint url, nint type, nuint count, nint options);
        [LibraryImport(ImageIo)] internal static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);
        [LibraryImport(ImageIo)] internal static partial byte CGImageDestinationFinalize(nint destination);

        internal static nint kCFBooleanTrue() => NameRef(Frameworks.Foundation, "kCFBooleanTrue");
    }
}
