// 移植自 LightCraft crates/raw/src/tiffraw.rs（TIFF IFD 像素資料讀取：strips/tiles，未壓縮、無失真／失真 JPEG、Deflate、JPEG XL）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Codecs;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw;

/// <summary>未壓縮整數資料的位元打包（非 8/16 位元深度用；lightcraft <c>tiffraw::Packing</c>）。</summary>
internal enum Packing
{
    /// <summary>TIFF 標準：MSB-first，列從位元組邊界開始。</summary>
    Msb,
    /// <summary>LSB-first 小端序位元流，列位元組對齊。</summary>
    Lsb,
    /// <summary>每個樣本以檔案位元組序存成 16 位元（不論位元深度）。</summary>
    Word16,
    /// <summary>以檔案位元組序儲存的 32-bit 字內的 MSB-first 位元流；列從字邊界開始。</summary>
    Words32Msb,
}

/// <summary>
/// TIFF IFD 的像素資料讀取（lightcraft <c>tiffraw</c>）。所有檔頭驅動的配置皆受
/// <see cref="RawLimits.MaxSamples"/> 限制；損毀只丟 <see cref="RawException"/>。
/// </summary>
internal static class TiffRaw
{
    /// <summary>
    /// 解碼前檢查（樣本格式、尺寸上限、資料存在）；回傳樣本總數（lightcraft <c>check_image</c>）。
    /// </summary>
    internal static int CheckImage(ReadOnlyMemory<byte> data, ImageInfo info)
    {
        long w = info.Width, h = info.Height, cpp = info.SamplesPerPixel;
        long total;
        try { total = checked(w * h * cpp); }
        catch (OverflowException) { throw RawException.Limit("image too large"); }
        if (total > RawLimits.MaxSamples || total == 0)
            throw RawException.Limit("image too large");
        uint bits = info.Bits();
        bool isFloat = info.SampleFormat == 3;
        if (isFloat && bits is not (16 or 24 or 32))
            throw RawException.Unsupported($"{bits}-bit floating point samples");
        if (!isFloat && (bits < 1 || bits > 16))
            throw RawException.Unsupported($"{bits}-bit integer samples");
        var chunks = info.Chunks(data.Length);
        if (chunks.Count == 0)
            throw RawException.Corrupt("no image data chunks");
        // 每個可見樣本都要有區塊：偏移表太短會留下零填充（黑色）區域
        var (across, down) = info.Grid();
        long planes = info.Planar == 2 ? cpp : 1;
        long expected;
        try { expected = checked((long)across * down * planes); }
        catch (OverflowException) { throw RawException.Limit("image too large"); }
        if (chunks.Count < expected)
            throw RawException.Corrupt($"{chunks.Count} image data chunks, expected {expected}");
        // 配置前的合理性上限：支援的編碼每位元組存約 2000 個樣本（常數資料的 Deflate 為極端）
        ulong available = 0;
        foreach (var c in chunks)
        {
            var s = TiffImage.ChunkBytes(data, c);
            available += s is { } m ? (ulong)m.Length : 0;
        }
        if (available * 2048 < (ulong)total)
            throw RawException.Corrupt($"{available} bytes of image data cannot hold {total} samples");
        return (int)total;
    }

    /// <summary>
    /// 依 <paramref name="mode"/> 讀取樣本：Header 只檢查（<see cref="CheckImage"/>）並回傳空樣本
    /// （浮點為空 F32；lightcraft <c>read_image_in</c>）。
    /// </summary>
    internal static RawData ReadImageIn(DecodeMode mode, ReadOnlyMemory<byte> data, ImageInfo info, ByteOrder order, Packing packing)
    {
        if (mode == DecodeMode.Full)
            return ReadImage(data, info, order, packing);
        CheckImage(data, info);
        // 解碼器拒絕的無失真 JPEG 版面（子取樣分量）在檔頭探測也要失敗；JPEG XL 區塊檔頭不符亦然
        var chunks = info.Chunks(data.Length);
        if (chunks.Count > 0 && TiffImage.ChunkBytes(data, chunks[0]) is { } src)
        {
            if (info.Compression == TiffCompression.Jpeg)
                _ = Ljpeg.FrameInfo(src.Span);
            else if (info.Compression == TiffCompression.JpegXl)
            {
                bool planar = info.Planar == 2 && info.SamplesPerPixel > 1;
                int ccpp = planar ? 1 : info.SamplesPerPixel;
                var (cw, ch, vw, vh) = ChunkDims(info, chunks[0]);
                JxlCheck(src.Span, cw, ch, vw, vh, ccpp, info.Bits(), info.SampleFormat == 3);
            }
        }
        return info.SampleFormat == 3 ? new RawData.F32([]) : new RawData.U16([]);
    }

    /// <summary>解出 <c>width × height × cpp</c> 個樣本（lightcraft <c>read_image</c>）。</summary>
    internal static RawData ReadImage(ReadOnlyMemory<byte> data, ImageInfo info, ByteOrder order, Packing packing)
    {
        long w = info.Width, h = info.Height;
        int total = CheckImage(data, info);
        int cpp = info.SamplesPerPixel;
        uint bits = info.Bits();
        bool isFloat = info.SampleFormat == 3;
        var chunks = info.Chunks(data.Length);
        bool planar = info.Planar == 2 && cpp > 1;
        int ccpp = planar ? 1 : cpp;
        ushort[] outU16 = isFloat ? [] : new ushort[total];
        float[] outF32 = isFloat ? new float[total] : [];
        // 平行解碼各區塊（結果與排程無關），再依名目位置合併
        var decoded = new (TiffChunk Chunk, ushort[]? U16, float[]? F32, bool IsFloat, RawException? Error)[chunks.Count];
        Parallel.For(0, chunks.Count, RawParallel.Options, i =>
        {
            try
            {
                var px = DecodeChunk(data, info, chunks[i], order, packing, ccpp, bits, isFloat);
                decoded[i] = px.IsFloat
                    ? (chunks[i], null, px.F32, true, null)
                    : (chunks[i], px.U16, null, false, null);
            }
            catch (RawException e) { decoded[i] = (chunks[i], null, null, isFloat, e); }
        });
        foreach (var (c, u16, f32, chunkFloat, error) in decoded)
        {
            // 無法解碼的區塊不是部分影像：整個檔案只剩預覽
            if (error is not null) throw error;
            long cw = c.Width, ch = c.Height, x0 = c.X, y0 = c.Y;
            if (x0 >= w || y0 >= h) continue;
            long copyW = Math.Min(cw, w - x0), copyH = Math.Min(ch, h - y0);
            for (long y = 0; y < copyH; y++)
                for (long x = 0; x < copyW; x++)
                    for (var s = 0; s < ccpp; s++)
                    {
                        long src = (y * cw + x) * ccpp + s;
                        int dstS = planar ? c.Plane : s;
                        if (dstS >= cpp) continue;
                        long dst = ((y0 + y) * w + x0 + x) * cpp + dstS;
                        if (chunkFloat)
                        {
                            if ((ulong)dst < (ulong)outF32.Length && (ulong)src < (ulong)f32!.Length)
                                outF32[(int)dst] = f32[(int)src];
                        }
                        else
                        {
                            if ((ulong)dst < (ulong)outU16.Length && (ulong)src < (ulong)u16!.Length)
                                outU16[(int)dst] = u16[(int)src];
                        }
                    }
        }
        return isFloat ? new RawData.F32(outF32) : new RawData.U16(outU16);
    }

    private readonly record struct ChunkPx(ushort[]? U16, float[]? F32, bool IsFloat);
    /// <summary>區塊名目尺寸與圖內可見部分（邊緣磚塊較小）。</summary>
    private static (int Cw, int Ch, int Vw, int Vh) ChunkDims(ImageInfo info, TiffChunk c)
    {
        if (c.Width > int.MaxValue || c.Height > int.MaxValue)
            throw RawException.Limit("chunk too large");
        int cw = (int)c.Width, ch = (int)c.Height;
        long vwRaw = (long)info.Width - c.X, vhRaw = (long)info.Height - c.Y;
        int vw = (int)Math.Min(Math.Max(0L, vwRaw), cw);
        int vh = (int)Math.Min(Math.Max(0L, vhRaw), ch);
        return (cw, ch, vw, vh);
    }

    private static ChunkPx DecodeChunk(ReadOnlyMemory<byte> data, ImageInfo info, TiffChunk c, ByteOrder order, Packing packing, int cpp, uint bits, bool isFloat)
    {
        var src = TiffImage.ChunkBytes(data, c) ?? throw RawException.Corrupt("chunk offset past end of file");
        var (cw, ch, vw, vh) = ChunkDims(info, c);
        long n;
        try { n = checked((long)cw * ch * cpp); }
        catch (OverflowException) { throw RawException.Limit("chunk too large"); }
        if (n > RawLimits.MaxSamples)
            throw RawException.Limit("chunk too large");
        switch (info.Compression)
        {
            case TiffCompression.None:
            {
                // 未壓縮：缺列補零，但區塊位元組遠少於樣本需求則在配置前拒絕
                ulong need = (ulong)n * bits / 8;
                if ((ulong)src.Length * 64 < need)
                    throw RawException.Corrupt($"an uncompressed chunk of {n} samples has only {src.Length} bytes");
                return UnpackChunk(src.Span, order, packing, cw, ch, cpp, bits, isFloat, 1);
            }
            case TiffCompression.Jpeg:
            {
                int cap;
                try { cap = checked((int)Math.Min((long)n * 2, int.MaxValue)); }
                catch (OverflowException) { cap = int.MaxValue; }
                var f = Ljpeg.Decode(src.Span, Math.Max(cap, 1 << 16));
                if (f.Data.Length < n)
                {
                    // 邊緣磚塊可只編碼圖內列數，但不可更短
                    if (f.Data.Length == 0 || cw == 0 || f.Data.Length % (cw * cpp) != 0 || f.Data.Length / (cw * cpp) < vh)
                        throw RawException.Corrupt($"lossless JPEG tile has {f.Data.Length} samples, expected {n}");
                }
                var v = new ushort[(int)n];
                Array.Copy(f.Data, v, Math.Min(f.Data.Length, (int)n));
                return new ChunkPx(v, null, false);
            }
            case TiffCompression.LossyJpeg:
            {
                // 失真 DNG：每區塊一個 8-bit baseline（DCT）JPEG
                var (px, jw, jh, jc) = LossyJpeg(src, cpp);
                var v = new ushort[n];
                for (var y = 0; y < Math.Min(ch, jh); y++)
                    for (var x = 0; x < Math.Min(cw, jw); x++)
                        for (var k = 0; k < cpp; k++)
                            v[(y * cw + x) * cpp + k] = px[(y * jw + x) * jc + Math.Min(k, jc - 1)];
                return new ChunkPx(v, null, false);
            }
            case TiffCompression.AdobeDeflate:
            case TiffCompression.Deflate:
            {
                int bytesPer = isFloat ? (int)((bits + 7) / 8) : 0;
                long rowBytes = isFloat ? (long)cw * cpp * bytesPer : ((long)cw * cpp * bits + 7) / 8;
                long limit;
                try { limit = checked(rowBytes * ch); }
                catch (OverflowException) { throw RawException.Limit("chunk too large"); }
                if (limit > int.MaxValue)
                    throw RawException.Limit("chunk too large");
                byte[] raw;
                try
                {
                    raw = Unpack.Inflate(src.Span, (int)limit);
                }
                catch (RawException ex) when (ex.Kind == RawErrorKind.Corrupt)
                {
                    // 預測器本身不支援時報 Unsupported，不因損毀的 deflate 資料誤報 Corrupt
                    //（上游 unpack_chunk 的 predictor match 在 inflate 之前）。
                    _ = UnpackChunk(ReadOnlySpan<byte>.Empty, order, packing, 1, 1, cpp, bits, isFloat, info.Predictor);
                    throw;
                }
                return UnpackChunk(raw, order, packing, cw, ch, cpp, bits, isFloat, info.Predictor);
            }
            case TiffCompression.JpegXl:
                return JxlChunk(src.Span, cw, ch, vw, vh, cpp, bits, isFloat);
            default:
                throw RawException.Unsupported($"TIFF compression {info.Compression}");
        }
    }

    // JPEG XL DNG（上游 jxl feature）：真解碼在 Raw/Codecs/JpegXl.cs（libjxl 原生邊界）。
    private static ChunkPx JxlChunk(ReadOnlySpan<byte> src, int cw, int ch, int vw, int vh, int cpp, uint bits, bool isFloat)
    {
        var (u16, f32, floatOut) = Codecs.JpegXl.Decode(src, cw, ch, vw, vh, cpp, bits, isFloat);
        return floatOut ? new ChunkPx(null, f32, true) : new ChunkPx(u16, null, false);
    }

    private static void JxlCheck(ReadOnlySpan<byte> src, int cw, int ch, int vw, int vh, int cpp, uint bits, bool isFloat) =>
        Codecs.JpegXl.Check(src, cw, ch, vw, vh, cpp, bits, isFloat);

    /// <summary>解一個 baseline JPEG 區塊為 8-bit 樣本（lightcraft <c>lossy_jpeg</c>）。</summary>
    internal static (byte[] Samples, int Width, int Height, int Channels) LossyJpeg(ReadOnlyMemory<byte> src, int cpp)
    {
        var (samples, w, h, c) = Jpeg8.Decode(src.Span, cpp == 1 ? 1 : 3);
        int want = cpp == 1 ? 1 : 3;
        if (w == 0 || h == 0 || samples.Length < (long)w * h * c)
            throw RawException.Corrupt("lossy JPEG tile: short output");
        return (samples, w, h, want);
    }

    private static ChunkPx UnpackChunk(ReadOnlySpan<byte> src, ByteOrder order, Packing packing, int cw, int ch, int cpp, uint bits, bool isFloat, ushort predictor)
    {
        int rowN;
        try { rowN = checked(cw * cpp); }
        catch (OverflowException) { throw RawException.Limit("chunk too large"); }
        int factor;
        bool fp;
        switch (predictor)
        {
            case 1: factor = 0; fp = false; break;
            case 2: factor = 1; fp = false; break;
            case 3: factor = 1; fp = true; break;
            case 34892: factor = 2; fp = false; break;
            case 34893: factor = 4; fp = false; break;
            case 34894: factor = 2; fp = true; break;
            case 34895: factor = 4; fp = true; break;
            default: throw RawException.Unsupported($"predictor {predictor}");
        }
        bool little = order == ByteOrder.Little;
        if (isFloat)
        {
            int bp = (int)((bits + 7) / 8);
            long rowBytes = (long)rowN * bp;
            if (rowBytes > int.MaxValue) throw RawException.Limit("chunk too large");
            long totalF;
            try { totalF = checked((long)rowN * ch); }
            catch (OverflowException) { throw RawException.Limit("chunk too large"); }
            if (totalF > int.MaxValue) throw RawException.Limit("chunk too large");
            var output = new float[(int)totalF];
            var rowbuf = new byte[(int)rowBytes];
            for (var y = 0; y < ch; y++)
            {
                long off = (long)y * rowBytes;
                int len = off >= src.Length ? 0 : Math.Min(src.Length - (int)off, (int)rowBytes);
                if (len > 0) src.Slice((int)off, len).CopyTo(rowbuf);
                if (len < rowbuf.Length) Array.Clear(rowbuf, len, rowbuf.Length - len);
                bool big;
                if (fp)
                {
                    Unpack.UndoFloatPredictor(rowbuf, rowN, bp, cpp * factor);
                    big = true;
                }
                else
                {
                    if (factor > 0)
                        throw RawException.Unsupported("integer predictor on float data");
                    big = !little;
                }
                var row = new Span<float>(output, y * rowN, rowN);
                for (var i = 0; i < rowN; i++)
                {
                    var b = new ReadOnlySpan<byte>(rowbuf, i * bp, bp);
                    row[i] = (bp, big) switch
                    {
                        (2, true) => Unpack.F16ToF32((ushort)((b[0] << 8) | b[1])),
                        (2, false) => Unpack.F16ToF32((ushort)(b[0] | (b[1] << 8))),
                        (3, true) => Unpack.F24ToF32(((uint)b[0] << 16) | ((uint)b[1] << 8) | b[2]),
                        (3, false) => Unpack.F24ToF32((uint)b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16)),
                        (4, true) => BitConverter.IsLittleEndian
                            ? BitConverter.ToSingle([(byte)b[3], (byte)b[2], (byte)b[1], (byte)b[0]])
                            : BitConverter.ToSingle([b[0], b[1], b[2], b[3]]),
                        _ => BitConverter.IsLittleEndian
                            ? BitConverter.ToSingle([b[0], b[1], b[2], b[3]])
                            : BitConverter.ToSingle([(byte)b[3], (byte)b[2], (byte)b[1], (byte)b[0]]),
                    };
                }
            }
            return new ChunkPx(null, output, true);
        }
        if (fp)
            throw RawException.Unsupported("floating-point predictor on integer data");
        long total;
        try { total = checked((long)rowN * ch); }
        catch (OverflowException) { throw RawException.Limit("chunk too large"); }
        if (total > int.MaxValue) throw RawException.Limit("chunk too large");
        var out16 = new ushort[(int)total];
        int rowBytesInt;
        int kind;
        try
        {
            switch (bits, packing)
            {
                case (8, _): rowBytesInt = rowN; kind = 0; break;
                case (16, _): rowBytesInt = checked(rowN * 2); kind = 1; break;
                case (_, Packing.Word16): rowBytesInt = checked(rowN * 2); kind = 1; break;
                case (_, Packing.Msb): rowBytesInt = (int)(((long)rowN * bits + 7) / 8); kind = 2; break;
                case (_, Packing.Lsb): rowBytesInt = (int)(((long)rowN * bits + 7) / 8); kind = 3; break;
                default: rowBytesInt = (int)(((long)rowN * bits + 31) / 32 * 4); kind = 4; break; // Words32Msb
            }
        }
        catch (OverflowException) { throw RawException.Limit("chunk too large"); }
        for (var y = 0; y < ch; y++)
        {
            long off = (long)y * rowBytesInt;
            var s = off >= src.Length ? ReadOnlySpan<byte>.Empty : src.Slice((int)off, Math.Min(src.Length - (int)off, rowBytesInt));
            var row = new Span<ushort>(out16, y * rowN, rowN);
            switch (kind)
            {
                case 0:
                    for (var i = 0; i < row.Length; i++) row[i] = i < s.Length ? s[i] : (ushort)0;
                    break;
                case 1:
                    Unpack.ReadU16s(s, row, little);
                    // Rust zip 語意：來源不足時尾部保持 0（out16 已零初始化）
                    break;
                case 2:
                    Unpack.UnpackMsb(s, (int)bits, row);
                    break;
                case 4:
                    Unpack.UnpackWords32Msb(s, little, (int)bits, row);
                    break;
                default:
                    Unpack.UnpackLsb(s, (int)bits, row);
                    break;
            }
            if (factor > 0)
            {
                if (bits == 8)
                {
                    int stride;
                    try { stride = checked(cpp * factor); }
                    catch (OverflowException) { throw RawException.Limit("chunk too large"); }
                    for (var i = stride; i < row.Length; i++)
                        row[i] = (ushort)(((byte)row[i] + (byte)row[i - stride]) & 0xFF);
                }
                else
                {
                    int stride;
                    try { stride = checked(cpp * factor); }
                    catch (OverflowException) { throw RawException.Limit("chunk too large"); }
                    Unpack.UndoDiffU16(row, stride);
                }
            }
        }
        return new ChunkPx(out16, null, false);
    }
}
