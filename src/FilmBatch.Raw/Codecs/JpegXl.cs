// JPEG XL DNG（壓縮 52546）的 chunk 解碼：libjxl 原生邊界＋上游 jxl.rs 語意。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Codecs;

/// <summary>
/// JPEG XL 磁磚／條帶解碼（lightcraft <c>jxl</c>；DNG 1.7 壓縮 52546）。
/// 每 chunk 為 bare codestream 或 ISO-BMFF container，承載 8..16-bit 無號整數（1 或 3 平面）
/// 或 16-bit 浮點；解碼語意與上游 <c>jxl.rs</c> 一致：
/// <list type="bullet">
/// <item>無失真（非 XYB）以 codestream 自身色彩編碼渲染（恆等），值在 raw IFD 的樣本空間；</item>
/// <item>整數以 codestream 自身位元深度的最大值還原並四捨五入（上游 <c>value / (2^bits − 1)</c> 的逆運算），
/// 無失真逐位元一致，與 IFD 的 <c>BitsPerSample</c> 無關；</item>
/// <item>有損（XYB）只接受線性 RGB、D65 白點、intensity target ≤ 255 的宣告編碼，
/// 以線性 sRGB 解碼後經 3×3 矩陣還原宣告主色；絕不經 CMS 做色域映射／裁切，
/// 故結果可超過 1.0 或小於 0（上游「不裁切」語意）；</item>
/// <item>每 tile 以寬（32-bit）模組緩衝解碼；記憶體以 tile 自身為界，超界的 codestream
/// 解碼失敗而非按檔頭要求配置（上游 <c>AllocTracker</c> 語意）。</item>
/// </list>
/// 原生依賴見 <see cref="JxlNative"/>；缺原生程式庫時報 <see cref="RawErrorKind.Unsupported"/>（非靜默替代）。
/// </summary>
internal static class JpegXl
{
    /// <summary>每個 tile 樣本允許的解碼器記憶體（位元組；上游 <c>BYTES_PER_SAMPLE</c>）。</summary>
    internal const long BytesPerSample = 16;

    /// <summary>固定解碼開銷（檔頭、熵表、群組中繼資料；上游 <c>MARGIN_BYTES</c>）。</summary>
    internal const long MarginBytes = 64 << 10;

    /// <summary>D65 白點（上游 <c>D65</c>）。</summary>
    internal static readonly double[] D65White = [0.3127, 0.3290];

    /// <summary>線性 sRGB 主色（上游 <c>SRGB</c>）。</summary>
    internal static readonly double[,] SrgbPrimaries = { { 0.640, 0.330 }, { 0.300, 0.600 }, { 0.150, 0.060 } };

    /// <summary>BT.2100 主色（上游 <c>BT2100</c>）。</summary>
    internal static readonly double[,] Bt2100Primaries = { { 0.708, 0.292 }, { 0.170, 0.797 }, { 0.131, 0.046 } };

    /// <summary>Display P3 主色（上游 <c>P3</c>）。</summary>
    internal static readonly double[,] P3Primaries = { { 0.680, 0.320 }, { 0.265, 0.690 }, { 0.150, 0.060 } };

    /// <summary>上游 <c>Output::{AsCoded, FromXyb}</c>：chunk 的渲染方式。</summary>
    internal enum OutputKind : byte
    {
        /// <summary>以 codestream 自身色彩編碼渲染（非 XYB：恆等）。</summary>
        AsCoded,
        /// <summary>XYB 有損：線性 sRGB 主色解碼，再以矩陣還原宣告主色（sRGB 時為 null）。</summary>
        FromXyb,
    }

    /// <summary>
    /// libjxl 檔頭資訊（<c>JxlBasicInfo</c>＋<c>JxlColorEncoding</c> 中驗證所需子集）；
    /// 測試可直接構造以覆蓋編碼器無法產生的分支（如非單位 orientation）。
    /// </summary>
    /// <param name="Width">xsize。</param>
    /// <param name="Height">ysize。</param>
    /// <param name="Orientation">EXIF orientation（1..8）。</param>
    /// <param name="ColorChannels">num_color_channels（1 灰階／3 彩色）。</param>
    /// <param name="Planes">IFD 每像素樣本數（比對用）。</param>
    /// <param name="BitsPerSample">codestream 整數位元深度（浮點時為 exponent 標記以外的位元數）。</param>
    /// <param name="ExponentBits">codestream 浮點指數位元（整數為 0）。</param>
    /// <param name="IsXyb">是否為 XYB 編碼（libjxl <c>uses_original_profile == false</c>；有損 VarDCT 用）。</param>
    /// <param name="ColorSpace">色彩空間（0 = RGB、1 = 灰階；其他為 XYB／未知／ICC）。</param>
    /// <param name="WhitePoint">白點（1 = D65）。</param>
    /// <param name="IntensityTarget">intensity target（nits）。</param>
    /// <param name="Primaries">宣告主色（列主序 3×2；sRGB/BT.2100/P3 以 null＋<paramref name="PrimariesKind"/> 表示）。</param>
    internal readonly record struct HeaderInfo(
        int Width, int Height, int Orientation, int ColorChannels, int Planes,
        uint BitsPerSample, uint ExponentBits, bool IsXyb, int ColorSpace, int WhitePoint,
        float IntensityTarget, double[]? Primaries)
    {
        /// <summary>具名主色種類：0 = 自訂（<see cref="HeaderInfo.Primaries"/>）、1 = sRGB、9 = BT.2100、11 = P3。</summary>
        public int PrimariesKind { get; init; }
        /// <summary>轉換函數（8 = 線性；上游只接受線性）。</summary>
        public int TransferFunction { get; init; }
    }

    /// <summary>
    /// 檢查 chunk 檔頭（上游 <c>open</c> 的驗證部分，不解像素）：
    /// 名目 <c>cw × ch</c> 或圖內可見 <c>vw × vh</c>（邊緣磚塊）、平面數、樣本型別、
    /// orientation、XYB 線性 D65 限制。回傳整數最大值（浮點為 0）與渲染方式。
    /// </summary>
    internal static (float Max, OutputKind Output, float[]? Matrix) CheckHeader(
        HeaderInfo header, int cw, int ch, int vw, int vh, int cpp, uint bits, bool isFloat)
    {
        if (cpp is not (1 or 3))
            throw RawException.Unsupported($"JPEG XL with {cpp} samples per pixel");
        if ((isFloat && bits != 16) || (!isFloat && (bits < 8 || bits > 16)))
            throw RawException.Unsupported($"JPEG XL with {bits}-bit {(isFloat ? "float" : "integer")} samples");
        if ((header.Width != cw || header.Height != ch) && (header.Width != vw || header.Height != vh))
            throw RawException.Corrupt($"JPEG XL tile is {header.Width}×{header.Height}, expected {cw}×{ch} (or {vw}×{vh} at the image edge)");
        if (header.Orientation != 1)
            throw RawException.Corrupt("JPEG XL tile with a non-identity orientation");
        if (header.ColorChannels != cpp)
            throw RawException.Corrupt($"JPEG XL tile has {header.ColorChannels} colour planes, the IFD {cpp} samples per pixel");
        float max;
        if (isFloat)
        {
            // 上游 BitDepth::FloatSample：任何浮點 codestream 皆可（float16／float32），指數位元 0 表整數即不符。
            if (header.ExponentBits == 0)
                throw RawException.Corrupt("JPEG XL tile sample type does not match the IFD");
            max = 0f;
        }
        else
        {
            if (header.ExponentBits != 0 || header.BitsPerSample is < 1 or > 16)
                throw RawException.Corrupt("JPEG XL tile sample type does not match the IFD");
            max = (float)((1u << (int)header.BitsPerSample) - 1);
        }
        var (kind, matrix) = OutputFor(header, cpp);
        return (max, kind, matrix);
    }

    /// <summary>
    /// 依檔頭決定渲染方式（上游 <c>output_for</c>）：非 XYB 恆為 <see cref="OutputKind.AsCoded"/>；
    /// XYB 有損只接受線性 RGB＋D65＋intensity ≤ 255，sRGB 主色免矩陣，
    /// BT.2100／P3／自訂主色計算還原矩陣（線性 sRGB → 宣告主色，D65）。
    /// </summary>
    internal static (OutputKind Kind, float[]? Matrix) OutputFor(HeaderInfo header, int planes)
    {
        if (!header.IsXyb)
            return (OutputKind.AsCoded, null);
        static RawException Unsupported(string what) => RawException.Unsupported($"lossy JPEG XL tile with {what}");
        if (planes != 3)
            throw Unsupported("a single colour plane");
        // ICC 描述檔（libjxl 取不到 enum 時回 Unknown）與 XYB 中間色皆無法還原。
        if (header.ColorSpace is 2 or 3)
            throw Unsupported("an ICC profile instead of a colour encoding");
        if (header.ColorSpace != 0 || header.TransferFunction != 8)
            throw Unsupported("a declared encoding that is not linear RGB");
        if (header.WhitePoint != 1)
            throw Unsupported("a white point other than D65");
        if (header.IntensityTarget > 255f)
            throw Unsupported("an intensity target above 255 nits");
        double[,] primaries = header.PrimariesKind switch
        {
            1 => SrgbPrimaries,
            9 => Bt2100Primaries,
            11 => P3Primaries,
            _ => header.Primaries is { Length: 6 }
                ? new double[,] { { header.Primaries[0], header.Primaries[1] }, { header.Primaries[2], header.Primaries[3] }, { header.Primaries[4], header.Primaries[5] } }
                : throw Unsupported("an ICC profile instead of a colour encoding"),
        };
        if (header.PrimariesKind == 1)
            return (OutputKind.FromXyb, null);
        if (primaries[0, 1] <= 0.0 || primaries[1, 1] <= 0.0 || primaries[2, 1] <= 0.0)
            throw Unsupported("invalid primaries");
        // 還原矩陣 = inverse(rgb_to_xyz(declared)) * rgb_to_xyz(sRGB)（上游 mul(inverse, srgb)）。
        var toXyz = RgbToXyz(primaries);
        var fromSrgb = RgbToXyz(SrgbPrimaries);
        var matrix = Mul(Inverse(toXyz), fromSrgb);
        var flat = new float[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            {
                var v = matrix[r, c];
                if (!double.IsFinite(v))
                    throw Unsupported("degenerate primaries");
                flat[r * 3 + c] = (float)v;
            }
        return (OutputKind.FromXyb, flat);
    }

    /// <summary>線性 RGB → XYZ（主色 xy＋D65 白點；上游 <c>rgb_to_xyz</c>）。</summary>
    internal static double[,] RgbToXyz(double[,] primaries)
    {
        static double[] Xyz(double x, double y) => [x / y, 1.0, (1.0 - x - y) / y];
        var r = Xyz(primaries[0, 0], primaries[0, 1]);
        var g = Xyz(primaries[1, 0], primaries[1, 1]);
        var b = Xyz(primaries[2, 0], primaries[2, 1]);
        var prim = new double[,] { { r[0], g[0], b[0] }, { 1.0, 1.0, 1.0 }, { r[2], g[2], b[2] } };
        var w = Xyz(D65White[0], D65White[1]);
        var inv = Inverse(prim);
        var s = new double[3];
        for (var i = 0; i < 3; i++)
            s[i] = inv[i, 0] * w[0] + inv[i, 1] * w[1] + inv[i, 2] * w[2];
        var output = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                output[i, j] = prim[i, j] * s[j];
        return output;
    }

    /// <summary>3×3 反矩陣（上游 <c>inverse</c>；餘因子轉置除以行列式）。</summary>
    internal static double[,] Inverse(double[,] m)
    {
        static double Cof(double[,] m, int i, int j) =>
            m[(i + 1) % 3, (j + 1) % 3] * m[(i + 2) % 3, (j + 2) % 3] -
            m[(i + 1) % 3, (j + 2) % 3] * m[(i + 2) % 3, (j + 1) % 3];
        var det = m[0, 0] * Cof(m, 0, 0) + m[0, 1] * Cof(m, 0, 1) + m[0, 2] * Cof(m, 0, 2);
        var output = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                output[i, j] = Cof(m, j, i) / det;
        return output;
    }

    /// <summary>3×3 矩陣乘法（上游 <c>mul</c>）。</summary>
    internal static double[,] Mul(double[,] a, double[,] b)
    {
        var output = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                output[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return output;
    }

    /// <summary>
    /// 解析 chunk 檔頭並驗證（上游 <c>check</c>；不解像素）。成功不回傳值。
    /// </summary>
    internal static void Check(ReadOnlySpan<byte> src, int cw, int ch, int vw, int vh, int cpp, uint bits, bool isFloat)
    {
        if (!JxlNative.IsAvailable)
            throw RawException.Unsupported("JPEG XL DNG (no libjxl runtime available)");
        var header = JxlNative.GetHeader(src);
        CheckHeader(ToHeaderInfo(header, cpp), cw, ch, vw, vh, cpp, bits, isFloat);
    }

    /// <summary>
    /// 解一個 chunk 為 <c>cw × ch × cpp</c> 樣本（列主序交錯；上游 <c>decode</c>）：
    /// 以圖內尺寸 <c>vw × vh</c> 編碼的 chunk 填左上，其餘為零。
    /// 整數以 <c>round(sample * max)</c> 還原（<c>as</c> 飽和語意：NaN → 0，有損過衝裁切到整數範圍）；
    /// 浮點直接回傳解碼值（不裁切）。有損經矩陣還原宣告主色。
    /// </summary>
    internal static (ushort[]? U16, float[]? F32, bool IsFloat) Decode(
        ReadOnlySpan<byte> src, int cw, int ch, int vw, int vh, int cpp, uint bits, bool isFloat)
    {
        if (!JxlNative.IsAvailable)
            throw RawException.Unsupported("JPEG XL DNG (no libjxl runtime available)");
        if (cw <= 0 || ch <= 0 || cpp is not (1 or 3))
            throw RawException.Corrupt("JPEG XL tile: bad chunk geometry");
        long n;
        try { n = checked((long)cw * ch * cpp); }
        catch (OverflowException) { throw RawException.Limit("chunk too large"); }
        if (n > RawLimits.MaxSamples)
            throw RawException.Limit("chunk too large");
        var header = JxlNative.GetHeader(src);
        var (max, output, matrix) = CheckHeader(ToHeaderInfo(header, cpp), cw, ch, vw, vh, cpp, bits, isFloat);
        float[] rendered;
        int rw, rh;
        try
        {
            // 全幅 float 交錯渲染（上游 render_frame().image_all_channels() 的對應）。
            (rendered, rw, rh, int rf) = JxlNative.DecodeFloat(src, cpp, output == OutputKind.FromXyb);
            if (rf < cpp)
                throw RawException.Corrupt($"JPEG XL tile rendered {rw}×{rh}×{rf}, expected {cw}×{ch}×{cpp}");
        }
        catch (RawException) { throw; }
        catch (OutOfMemoryException)
        {
            throw RawException.Limit("JPEG XL tile exceeds the decoder memory budget");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw RawException.Corrupt($"JPEG XL tile: {ex.GetType().Name}");
        }
        if (rw <= 0 || rh <= 0 || rw > cw || rh > ch)
            throw RawException.Corrupt($"JPEG XL tile rendered {rw}×{rh}, expected {cw}×{ch}×{cpp}");
        if (isFloat)
        {
            var output32 = new float[n];
            RestoreInto(rendered, rw, rh, cpp, cw, ch, output32, matrix);
            return (null, output32, true);
        }
        var output16 = new ushort[n];
        RestoreIntoU16(rendered, rw, rh, cpp, cw, ch, output16, matrix, max);
        return (output16, null, false);
    }

    private static HeaderInfo ToHeaderInfo(JxlNative.BasicInfo info, int cpp) => new(
        (int)info.XSize, (int)info.YSize, (int)info.Orientation, (int)info.ColorChannels, cpp,
        info.BitsPerSample, info.ExponentBits, !info.UsesOriginalProfile,
        (int)info.ColorSpace, (int)info.WhitePoint, info.IntensityTarget,
        info.PrimariesXy)
    {
        PrimariesKind = (int)info.Primaries,
        TransferFunction = (int)info.TransferFunction,
    };

    /// <summary>有損還原：線性 sRGB → 宣告主色（上游 <c>decode</c> 的 <c>restore</c> 閉包）。</summary>
    internal static void RestoreInto(float[] rendered, int rw, int rh, int cpp, int cw, int ch, float[] output, float[]? matrix)
    {
        int copyW = Math.Min(rw, cw), copyH = Math.Min(rh, ch);
        int rf = rendered.Length / Math.Max(1, rw * rh);
        if (rf < cpp)
            throw RawException.Corrupt("JPEG XL tile: short render");
        for (var y = 0; y < copyH; y++)
            for (var x = 0; x < copyW; x++)
            {
                int s = (y * rw + x) * rf, d = (y * cw + x) * cpp;
                if (matrix is null)
                {
                    for (var k = 0; k < cpp; k++)
                        output[d + k] = rendered[s + k];
                }
                else
                {
                    var r = rendered[s]; var g = rendered[s + 1]; var b = rendered[s + 2];
                    output[d] = matrix[0] * r + matrix[1] * g + matrix[2] * b;
                    output[d + 1] = matrix[3] * r + matrix[4] * g + matrix[5] * b;
                    output[d + 2] = matrix[6] * r + matrix[7] * g + matrix[8] * b;
                }
            }
    }

    private static void RestoreIntoU16(float[] rendered, int rw, int rh, int cpp, int cw, int ch, ushort[] output, float[]? matrix, float max)
    {
        // 上游 `as` 飽和語意：NaN → 0、超過範圍裁切到 ushort；MathF.Round 外自行邊界檢查。
        int copyW = Math.Min(rw, cw), copyH = Math.Min(rh, ch);
        int rf = rendered.Length / Math.Max(1, rw * rh);
        if (rf < cpp)
            throw RawException.Corrupt("JPEG XL tile: short render");
        Span<float> px = stackalloc float[3];
        for (var y = 0; y < copyH; y++)
            for (var x = 0; x < copyW; x++)
            {
                int s = (y * rw + x) * rf, d = (y * cw + x) * cpp;
                if (matrix is null)
                {
                    for (var k = 0; k < cpp; k++)
                        output[d + k] = SaturateU16(rendered[s + k] * max);
                }
                else
                {
                    var r = rendered[s]; var g = rendered[s + 1]; var b = rendered[s + 2];
                    px[0] = matrix[0] * r + matrix[1] * g + matrix[2] * b;
                    px[1] = matrix[3] * r + matrix[4] * g + matrix[5] * b;
                    px[2] = matrix[6] * r + matrix[7] * g + matrix[8] * b;
                    for (var k = 0; k < cpp; k++)
                        output[d + k] = SaturateU16(px[k] * max);
                }
            }
    }

    private static ushort SaturateU16(float v)
    {
        if (!float.IsFinite(v)) return v > 0 ? ushort.MaxValue : (ushort)0;
        var r = MathF.Round(v);
        return r <= 0f ? (ushort)0 : r >= 65535f ? ushort.MaxValue : (ushort)r;
    }
}
