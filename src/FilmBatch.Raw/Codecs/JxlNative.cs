// JPEG XL 原生邊界：系統 libjxl 0.11 的受管 P/Invoke 宣告（decode-only）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Runtime.InteropServices;

namespace FilmBatch.Raw.Codecs;

/// <summary>
/// libjxl C 解碼 API（<c>jxl/decode.h</c> 0.11）的最小受管邊界：只做單幀全解析度解碼。
/// 色彩策略對應上游 <c>jxl.rs</c>：非 XYB 不設輸出描述檔（按原樣渲染）；
/// XYB 有損在 <c>COLOR_ENCODING</c> 事件後要求線性 sRGB 輸出（不經外部 CMS 做色域映射），
/// 再由 <see cref="JpegXl"/> 以 3×3 矩陣還原宣告主色。原生程式庫缺席時
/// <see cref="IsAvailable"/> 為 false，解碼報 <see cref="RawErrorKind.Unsupported"/>。
/// 打包需求見交接（parent 處理）：macOS 隨附 <c>libjxl.0.11.dylib</c>（BSD-3-Clause，
/// JPEG XL Project；相依 highway／brotli 一併隨附或以系統版取代並驗證）。
/// </summary>
internal static partial class JxlNative
{
    private const string Library = "libjxl";

    /// <summary>是否載入了可用的 libjxl（呼叫時探測；解析器已在靜態建構子註冊）。</summary>
    internal static bool IsAvailable => Probe();

    private static bool Probe()
    {
        try { return Version() > 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (Exception) { return false; }
    }

    static JxlNative()
    {
        // 原生解析：先找隨附（輸出目錄／runtimes／<rid>／native），再找 Homebrew／系統路徑；
        // 找不到時回零、沿用預設解析，最終缺席由 IsAvailable 反映為 Unsupported。
        NativeLibrary.SetDllImportResolver(typeof(JxlNative).Assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Library, StringComparison.Ordinal))
            return nint.Zero;
        var tried = new List<string>();
        try
        {
            var baseDir = AppContext.BaseDirectory;
            tried.Add(Path.Combine(baseDir, "libjxl.0.11.dylib"));
            tried.Add(Path.Combine(baseDir, "libjxl.dylib"));
            try
            {
                var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                tried.Add(Path.Combine(baseDir, "runtimes", rid, "native", "libjxl.0.11.dylib"));
                tried.Add(Path.Combine(baseDir, "runtimes", rid, "native", "libjxl.dylib"));
            }
            catch (Exception) { }
            tried.Add("/opt/homebrew/lib/libjxl.0.11.dylib");
            tried.Add("/opt/homebrew/lib/libjxl.dylib");
            tried.Add("/usr/local/lib/libjxl.0.11.dylib");
            tried.Add("/usr/local/lib/libjxl.dylib");
            foreach (var path in tried)
            {
                if (NativeLibrary.TryLoad(path, out nint handle))
                    return handle;
            }
        }
        catch (Exception) { }
        return nint.Zero;
    }

    /// <summary>驗證用的檔頭快照（<c>JxlBasicInfo</c>＋<c>JxlColorEncoding</c> 的驗證子集）。</summary>
    internal readonly record struct BasicInfo(
        uint XSize, uint YSize, uint BitsPerSample, uint ExponentBits,
        float IntensityTarget, uint Orientation, uint ColorChannels, bool UsesOriginalProfile,
        ColorSpace ColorSpace, WhitePoint WhitePoint, Primaries Primaries,
        TransferFunction TransferFunction, double[]? PrimariesXy);

    internal enum Status : uint
    {
        Success = 0,
        Error = 1,
        NeedMoreInput = 2,
        NeedImageOutBuffer = 5,
        BasicInfo = 0x40,
        ColorEncoding = 0x100,
        Frame = 0x400,
        FullImage = 0x1000,
    }

    internal enum ColorSpace : uint
    {
        Rgb = 0,
        Gray = 1,
        Xyb = 2,
        Unknown = 3,
    }

    internal enum WhitePoint : uint
    {
        D65 = 1,
        Custom = 2,
        E = 10,
        Dci = 11,
    }

    internal enum Primaries : uint
    {
        Srgb = 1,
        Custom = 2,
        P2100 = 9,
        P3 = 11,
    }

    internal enum TransferFunction : uint
    {
        Tf709 = 1,
        Unknown = 2,
        Linear = 8,
        Srgb = 13,
        Pq = 16,
        Dci = 17,
        Hlg = 18,
        Gamma = 65535,
    }

    internal enum DataType : uint
    {
        Float = 0,
        UInt8 = 2,
        UInt16 = 3,
        Float16 = 5,
    }

    internal enum Endianness : uint
    {
        Native = 0,
        Little = 1,
        Big = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelFormat
    {
        public uint NumChannels;
        public DataType DataType;
        public Endianness Endianness;
        public nuint Align;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BasicInfoRaw
    {
        public int HaveContainer;
        public uint XSize;
        public uint YSize;
        public uint BitsPerSample;
        public uint ExponentBitsPerSample;
        public float IntensityTarget;
        public float MinNits;
        public int RelativeToMaxDisplay;
        public float LinearBelow;
        public int UsesOriginalProfile;
        public int HavePreview;
        public int HaveAnimation;
        public uint Orientation;
        public uint NumColorChannels;
        public uint NumExtraChannels;
        public uint AlphaBits;
        public uint AlphaExponentBits;
        public int AlphaPremultiplied;
        public uint PreviewXSize;
        public uint PreviewYSize;
        public uint AnimationTpsNumerator;
        public uint AnimationTpsDenominator;
        public uint AnimationNumLoops;
        public int AnimationHaveTimecodes;
        public uint IntrinsicXSize;
        public uint IntrinsicYSize;
        public fixed byte Padding[100];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ColorEncodingRaw
    {
        public ColorSpace ColorSpace;
        public WhitePoint WhitePoint;
        public fixed double WhitePointXy[2];
        public Primaries Primaries;
        public fixed double PrimariesRedXy[2];
        public fixed double PrimariesGreenXy[2];
        public fixed double PrimariesBlueXy[2];
        public TransferFunction TransferFunction;
        public double Gamma;
        public uint RenderingIntent;
    }

    internal readonly record struct ColorEncoding(ColorSpace ColorSpace, WhitePoint WhitePoint, (double X, double Y) WhitePointXy,
        Primaries Primaries, (double X, double Y) PrimariesRedXy, (double X, double Y) PrimariesGreenXy, (double X, double Y) PrimariesBlueXy,
        TransferFunction TransferFunction, double Gamma, uint RenderingIntent)
    {
        internal static unsafe ColorEncoding From(in ColorEncodingRaw r) => new(
            r.ColorSpace, r.WhitePoint, (r.WhitePointXy[0], r.WhitePointXy[1]), r.Primaries,
            (r.PrimariesRedXy[0], r.PrimariesRedXy[1]), (r.PrimariesGreenXy[0], r.PrimariesGreenXy[1]),
            (r.PrimariesBlueXy[0], r.PrimariesBlueXy[1]), r.TransferFunction, r.Gamma, r.RenderingIntent);

        internal unsafe ColorEncodingRaw ToRaw()
        {
            var r = new ColorEncodingRaw
            {
                ColorSpace = ColorSpace, WhitePoint = WhitePoint, Primaries = Primaries,
                TransferFunction = TransferFunction, Gamma = Gamma, RenderingIntent = RenderingIntent,
            };
            r.WhitePointXy[0] = WhitePointXy.X; r.WhitePointXy[1] = WhitePointXy.Y;
            r.PrimariesRedXy[0] = PrimariesRedXy.X; r.PrimariesRedXy[1] = PrimariesRedXy.Y;
            r.PrimariesGreenXy[0] = PrimariesGreenXy.X; r.PrimariesGreenXy[1] = PrimariesGreenXy.Y;
            r.PrimariesBlueXy[0] = PrimariesBlueXy.X; r.PrimariesBlueXy[1] = PrimariesBlueXy.Y;
            return r;
        }
    }

    [LibraryImport(Library)]
    private static partial uint JxlDecoderVersion();

    [LibraryImport(Library)]
    private static partial nint JxlDecoderCreate(nint memoryManager);

    [LibraryImport(Library)]
    private static partial void JxlDecoderDestroy(nint dec);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderSubscribeEvents(nint dec, uint events);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderSetInput(nint dec, nint data, nuint len);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderCloseInput(nint dec);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderProcessInput(nint dec);

    [LibraryImport(Library)]
    private static unsafe partial uint JxlDecoderGetBasicInfo(nint dec, BasicInfoRaw* info);

    [LibraryImport(Library)]
    private static unsafe partial uint JxlDecoderGetColorAsEncodedProfile(nint dec, uint target, ColorEncodingRaw* encoding);

    [LibraryImport(Library)]
    private static unsafe partial uint JxlDecoderSetOutputColorProfile(nint dec, ColorEncodingRaw* colorEncoding, nint iccData, nuint iccSize);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderImageOutBufferSize(nint dec, in PixelFormat format, ref nuint size);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderSetImageOutBuffer(nint dec, in PixelFormat format, nint buffer, nuint size);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderSetKeepOrientation(nint dec, int skipReorientation);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderSetUnpremultiplyAlpha(nint dec, int unpremulAlpha);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderSetRenderSpotcolors(nint dec, int renderSpotcolors);

    [LibraryImport(Library)]
    private static partial uint JxlDecoderSetCoalescing(nint dec, int coalescing);

    private static uint Version() => JxlDecoderVersion();

    /// <summary>讀取檔頭（basic info＋原始色彩編碼；不解像素）。</summary>
    internal static BasicInfo GetHeader(ReadOnlySpan<byte> src)
    {
        // 空輸入由簽章檢查直接拒絕，避免 pin 空 Span。
        if (src.IsEmpty)
            throw RawException.Corrupt("JPEG XL tile: empty chunk");
        unsafe
        {
            fixed (byte* p = src)
            {
                nint dec = Create();
                try
                {
                    Subscribe(dec, (uint)(Status.BasicInfo | Status.ColorEncoding));
                    SetInput(dec, (nint)p, (nuint)src.Length);
                    CloseInput(dec);
                    BasicInfoRaw basic = new();
                    ColorEncoding? color = null;
                    bool haveBasic = false, haveColor = false;
                    for (var i = 0; i < 16; i++)
                    {
                        var status = (Status)ProcessInput(dec);
                        if (status == Status.BasicInfo)
                        {
                            if (GetBasicInfo(dec, &basic) != (uint)Status.Success)
                                throw RawException.Corrupt("JPEG XL tile: cannot read basic info");
                            haveBasic = true;
                        }
                        else if (status == Status.ColorEncoding)
                        {
                            var enc = new ColorEncodingRaw();
                            // ICC 描述檔時回 ERROR（上游 ColourEncoding::ICC 分支）：記為 null，
                            // CheckHeader/output_for 階段以「非線性 RGB」為由拒絕有損。
                            color = GetColorAsEncodedProfile(dec, 0, &enc) == (uint)Status.Success
                                ? ColorEncoding.From(in enc)
                                : null;
                            haveColor = true;
                        }
                        else if (status is Status.Error)
                        {
                            throw RawException.Corrupt("JPEG XL tile: bad headers");
                        }
                        else if (status is Status.NeedMoreInput)
                        {
                            throw RawException.Corrupt("JPEG XL tile: truncated headers");
                        }
                        else if (status is Status.FullImage or Status.Success)
                        {
                            break;
                        }
                        if (haveBasic && haveColor)
                            break;
                    }
                    if (!haveBasic)
                        throw RawException.Corrupt("JPEG XL tile: bad headers");
                    return ToBasicInfo(basic, color);
                }
                finally
                {
                    Destroy(dec);
                }
            }
        }
    }

    /// <summary>
    /// 全幅解碼為 float 交錯樣本（<paramref name="cpp"/> 通道；灰階以 1 通道讀取）。
    /// <paramref name="linearOutput"/> 為 true 時先要求線性 sRGB 輸出（XYB 有損用）。
    /// 回傳（樣本、寬、高、實際通道數）。
    /// </summary>
    internal static (float[] Samples, int Width, int Height, int Channels) DecodeFloat(
        ReadOnlySpan<byte> src, int cpp, bool linearOutput)
    {
        if (src.IsEmpty)
            throw RawException.Corrupt("JPEG XL tile: empty chunk");
        if (cpp is not (1 or 3))
            throw RawException.Corrupt("JPEG XL tile: bad channel count");
        unsafe
        {
            fixed (byte* p = src)
            {
                nint dec = Create();
                GCHandle pinned = default;
                bool isPinned = false;
                float[]? output = null;
                uint w = 0, h = 0;
                try
                {
                    Subscribe(dec, (uint)(Status.BasicInfo | Status.ColorEncoding | Status.Frame | Status.FullImage));
                    SetKeepOrientation(dec, 1);
                    SetUnpremultiplyAlpha(dec, 1);
                    SetRenderSpotcolors(dec, 0);
                    SetCoalescing(dec, 1);
                    SetInput(dec, (nint)p, (nuint)src.Length);
                    CloseInput(dec);
                    bool colorRequested = false;
                    for (var i = 0; i < 64; i++)
                    {
                        var status = (Status)ProcessInput(dec);
                        if (status == Status.BasicInfo)
                        {
                            BasicInfoRaw basic = new();
                            if (GetBasicInfo(dec, &basic) != (uint)Status.Success)
                                throw RawException.Corrupt("JPEG XL tile: cannot read basic info");
                            // 記憶體上界：tile 自身樣本數 × 16 位元組＋64 KiB（上游 AllocTracker 語意）；
                            // 超界的 codestream 在此拒絕，不按檔頭要求配置。
                            long pixels;
                            try { pixels = checked((long)basic.XSize * basic.YSize * Math.Max(1, cpp)); }
                            catch (OverflowException) { throw RawException.Limit("JPEG XL tile too large"); }
                            long budget;
                            try { budget = checked(pixels * JpegXl.BytesPerSample + JpegXl.MarginBytes); }
                            catch (OverflowException) { throw RawException.Limit("JPEG XL tile too large"); }
                            if (budget > int.MaxValue)
                                throw RawException.Limit("JPEG XL tile too large");
                            if (basic.XSize == 0 || basic.YSize == 0)
                                throw RawException.Corrupt("JPEG XL tile: bad dimensions");
                            if ((ulong)basic.XSize * basic.YSize > (ulong)RawLimits.MaxSamples)
                                throw RawException.Limit("JPEG XL tile too large");
                            w = basic.XSize; h = basic.YSize;
                        }
                        else if (status == Status.ColorEncoding)
                        {
                            if (linearOutput && !colorRequested)
                            {
                                colorRequested = true;
                                var want = LinearSrgb().ToRaw();
                                if (SetOutputColorProfile(dec, &want, nint.Zero, 0) != (uint)Status.Success)
                                    throw RawException.Corrupt("JPEG XL tile: cannot request linear output");
                            }
                        }
                        else if (status == Status.NeedImageOutBuffer)
                        {
                            if (isPinned)
                                throw RawException.Corrupt("JPEG XL tile: unexpected second frame");
                            var format = new PixelFormat
                            {
                                NumChannels = (uint)cpp,
                                DataType = DataType.Float,
                                Endianness = Endianness.Native,
                                Align = 0,
                            };
                            nuint size = 0;
                            if (ImageOutBufferSize(dec, in format, ref size) != (uint)Status.Success)
                                throw RawException.Corrupt("JPEG XL tile: cannot size output buffer");
                            // 輸出位元組以 libjxl 回報為準（灰階 float 為 w*h*4）；
                            // 實際通道數由位元組反推，呼叫端再與 IFD 平面數核對。
                            if ((long)size <= 0 || (long)size > int.MaxValue || (long)size % 4 != 0)
                                throw RawException.Corrupt("JPEG XL tile: unexpected output size");
                            long expectMax;
                            try { expectMax = checked((long)w * h * cpp * 4); }
                            catch (OverflowException) { throw RawException.Limit("JPEG XL tile too large"); }
                            if ((long)size > expectMax)
                                throw RawException.Corrupt("JPEG XL tile: unexpected output size");
                            if ((long)size < expectMax / 3)
                                throw RawException.Corrupt("JPEG XL tile: unexpected output size");
                            output = new float[(long)size / 4];
                            pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
                            isPinned = true;
                            if (SetImageOutBuffer(dec, in format, pinned.AddrOfPinnedObject(), (nuint)(long)size) != (uint)Status.Success)
                                throw RawException.Corrupt("JPEG XL tile: cannot set output buffer");
                        }
                        else if (status == Status.FullImage)
                        {
                            if (!isPinned || output is null)
                                throw RawException.Corrupt("JPEG XL tile: no complete frame");
                            int actual = output.Length / Math.Max(1, (int)w * (int)h);
                            return (output, (int)w, (int)h, actual);
                        }
                        else if (status == Status.Error)
                        {
                            throw RawException.Corrupt("JPEG XL tile: decode failed");
                        }
                        else if (status == Status.NeedMoreInput)
                        {
                            throw RawException.Corrupt("JPEG XL tile: truncated data");
                        }
                        else if (status == Status.Success)
                        {
                            if (!isPinned || output is null)
                                throw RawException.Corrupt("JPEG XL tile: no complete frame");
                            int actual = output.Length / Math.Max(1, (int)w * (int)h);
                            return (output, (int)w, (int)h, actual);
                        }
                    }
                    throw RawException.Corrupt("JPEG XL tile: no complete frame");
                }
                finally
                {
                    if (isPinned)
                        pinned.Free();
                    Destroy(dec);
                }
            }
        }
    }

    private static ColorEncoding LinearSrgb() => new(
        ColorSpace.Rgb, WhitePoint.D65, (0.3127, 0.3290), Primaries.Srgb,
        (0.64, 0.33), (0.30, 0.60), (0.15, 0.06), TransferFunction.Linear, 0, 1);

    private static BasicInfo ToBasicInfo(BasicInfoRaw basic, ColorEncoding? color)
    {
        double[]? xy = null;
        ColorSpace cs = ColorSpace.Unknown;
        WhitePoint wp = WhitePoint.Custom;
        Primaries prim = Primaries.Custom;
        TransferFunction tf = TransferFunction.Unknown;
        if (color is { } enc)
        {
            cs = enc.ColorSpace;
            wp = enc.WhitePoint;
            prim = enc.Primaries;
            tf = enc.TransferFunction;
            // 灰階時主色欄位無意義（上游忽略）；RGB 自訂主色才攜帶數值。
            if (enc.ColorSpace == ColorSpace.Rgb && enc.Primaries == Primaries.Custom)
                xy = [enc.PrimariesRedXy.X, enc.PrimariesRedXy.Y, enc.PrimariesGreenXy.X, enc.PrimariesGreenXy.Y, enc.PrimariesBlueXy.X, enc.PrimariesBlueXy.Y];
        }
        return new BasicInfo(
            basic.XSize, basic.YSize, basic.BitsPerSample, basic.ExponentBitsPerSample,
            basic.IntensityTarget == 0 ? 255f : basic.IntensityTarget,
            basic.Orientation, basic.NumColorChannels, basic.UsesOriginalProfile != 0,
            cs, wp, prim, tf, xy);
    }

    private static nint Create()
    {
        var dec = JxlDecoderCreate(nint.Zero);
        if (dec == nint.Zero)
            throw RawException.Limit("JPEG XL tile too large");
        return dec;
    }

    private static void Destroy(nint dec)
    {
        if (dec != nint.Zero)
            JxlDecoderDestroy(dec);
    }

    private static void Subscribe(nint dec, uint events)
    {
        if (JxlDecoderSubscribeEvents(dec, events) != (uint)Status.Success)
            throw RawException.Corrupt("JPEG XL tile: cannot subscribe to decoder events");
    }

    private static void SetInput(nint dec, nint data, nuint len)
    {
        if (JxlDecoderSetInput(dec, data, len) != (uint)Status.Success)
            throw RawException.Corrupt("JPEG XL tile: cannot set decoder input");
    }

    private static void CloseInput(nint dec) => JxlDecoderCloseInput(dec);

    private static uint ProcessInput(nint dec) => JxlDecoderProcessInput(dec);

    private static unsafe uint GetBasicInfo(nint dec, BasicInfoRaw* info) => JxlDecoderGetBasicInfo(dec, info);

    private static unsafe uint GetColorAsEncodedProfile(nint dec, uint target, ColorEncodingRaw* enc) =>
        JxlDecoderGetColorAsEncodedProfile(dec, target, enc);

    private static unsafe uint SetOutputColorProfile(nint dec, ColorEncodingRaw* enc, nint icc, nuint size) =>
        JxlDecoderSetOutputColorProfile(dec, enc, icc, size);

    private static uint ImageOutBufferSize(nint dec, in PixelFormat format, ref nuint size) =>
        JxlDecoderImageOutBufferSize(dec, in format, ref size);

    private static uint SetImageOutBuffer(nint dec, in PixelFormat format, nint buffer, nuint size) =>
        JxlDecoderSetImageOutBuffer(dec, in format, buffer, size);

    private static void SetKeepOrientation(nint dec, int v)
    {
        if (JxlDecoderSetKeepOrientation(dec, v) != (uint)Status.Success)
            throw RawException.Corrupt("JPEG XL tile: cannot set orientation handling");
    }

    private static void SetUnpremultiplyAlpha(nint dec, int v)
    {
        if (JxlDecoderSetUnpremultiplyAlpha(dec, v) != (uint)Status.Success)
            throw RawException.Corrupt("JPEG XL tile: cannot set alpha handling");
    }

    private static void SetRenderSpotcolors(nint dec, int v)
    {
        if (JxlDecoderSetRenderSpotcolors(dec, v) != (uint)Status.Success)
            throw RawException.Corrupt("JPEG XL tile: cannot set spot color handling");
    }

    private static void SetCoalescing(nint dec, int v)
    {
        if (JxlDecoderSetCoalescing(dec, v) != (uint)Status.Success)
            throw RawException.Corrupt("JPEG XL tile: cannot set coalescing");
    }
}
