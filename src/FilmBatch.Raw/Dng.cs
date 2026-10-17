// 移植自 LightCraft crates/raw/src/dng.rs（DNG 1.7：raw IFD 選擇、像素資料、線性化、黑白階、有效區、預設裁切、CFA、色彩標籤、運算碼）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw;

/// <summary>DNG 解碼（lightcraft <c>dng</c>；無 DNG 版本標籤但以 DNG 方式描述自己的 plain CFA TIFF 亦由此讀）。</summary>
internal static class Dng
{
    /// <summary>主要 raw IFD：像素最多的全解析度（NewSubfileType 0）CFA 或 LinearRaw 影像。</summary>
    internal static Ifd? RawIfd(TiffFile tiff)
    {
        Ifd? best = null;
        ulong bestArea = 0;
        foreach (var i in tiff.AllIfds())
        {
            if (i.U32(Tags.NewSubfileType) is not 0 and not null) continue;
            if (i.U32(Tags.NewSubfileType) is null)
            {
                // 缺標籤視為 0（上游 unwrap_or(0)）
            }
            if (i.U16(Tags.Photometric) is not (TiffPhotometric.Cfa or TiffPhotometric.LinearRaw))
                continue;
            ulong area = (i.U64(Tags.ImageWidth) ?? 0) * (i.U64(Tags.ImageLength) ?? 0);
            if (best is null || area > bestArea) { best = i; bestArea = area; }
        }
        return best;
    }

    internal static RawImage Decode(ReadOnlyMemory<byte> bytes, DecodeMode mode) =>
        DecodeAs(bytes, mode, RawFormat.Dng);

    /// <summary>
    /// 非 DNG 但以 DNG 方式描述 raw 的 TIFF（Hasselblad 3FR、FFF）是否可讀：全解析度 CFA IFD
    /// 加 DNG 的白平衡聲明（AsShotNeutral），編碼為未壓縮或解碼器接受的無失真 JPEG。
    /// </summary>
    internal static bool IsPlainCfaTiff(TiffFile tiff, ReadOnlyMemory<byte> bytes)
    {
        var raw = RawIfd(tiff);
        if (raw is null) return false;
        if (!raw.Contains(Tags.AsShotNeutral) && tiff.Ifds.Count != 0 && !tiff.Ifds[0].Contains(Tags.AsShotNeutral))
            return false;
        ImageInfo info;
        try { info = raw.Image(); }
        catch (RawException) { return false; }
        if (info.SamplesPerPixel != 1 || info.Bits() is < 1 or > 16 || info.SampleFormat == 3)
            return false;
        switch (info.Compression)
        {
            case TiffCompression.None:
                return true;
            case TiffCompression.Jpeg:
            {
                var chunks = info.Chunks(bytes.Length);
                if (chunks.Count == 0) return false;
                if (TiffImage.ChunkBytes(bytes, chunks[0]) is not { } src) return false;
                try { _ = Codecs.Ljpeg.FrameInfo(src.Span); return true; }
                catch (RawException) { return false; }
            }
            default:
                return false;
        }
    }

    internal static RawImage DecodeAs(ReadOnlyMemory<byte> bytes, DecodeMode mode, RawFormat format)
    {
        var tiff = TiffFile.Parse(bytes);
        if (tiff.Ifds.Count == 0)
            throw RawException.Corrupt("DNG without a raw image IFD");
        var ifd0 = tiff.Ifds[0];
        var raw = RawIfd(tiff) ?? throw RawException.Corrupt("DNG without a raw image IFD");
        // GoPro GPR：VC-5 編碼的 DNG，未解碼；檔頭模式仍描述檔案
        if (mode == DecodeMode.Full && raw.U16(Tags.Compression) == TiffCompression.Vc5)
            throw RawException.Unsupported($"DNG compression {TiffCompression.Vc5} (GoPro VC-5) is not decoded yet");
        var info = raw.Image();
        long w = info.Width, h = info.Height;
        int cpp = info.SamplesPerPixel;
        // 單樣本（CFA／單色）、RGB 或四色；兩個樣本無法顯影
        if (cpp is not (1 or 3 or 4))
            throw RawException.Unsupported($"{cpp} samples per pixel");
        var data = TiffRaw.ReadImageIn(mode, bytes, info, tiff.Order, Packing.Msb);
        uint bits = info.Bits();

        // DNG 1.7 交錯儲存：先還原排列，之後才有像素可讀
        if (mode == DecodeMode.Full)
        {
            int rows = raw.U16(Tags.RowInterleaveFactor) ?? 1;
            int cols = raw.U16(Tags.ColumnInterleaveFactor) ?? 1;
            data = Deinterleave(data, checked((int)w), checked((int)h), cpp, rows, cols);
        }

        // 線性化表（整數資料）
        bool linearized = false;
        if (data is RawData.U16 u16data && raw.U64s(Tags.LinearizationTable) is { Length: > 0 } table)
        {
            var lut = new ushort[table.Length];
            for (var i = 0; i < table.Length; i++) lut[i] = (ushort)Math.Min(table[i], 65535);
            int last = lut.Length - 1;
            var v = u16data.Samples;
            for (var i = 0; i < v.Length; i++)
                v[i] = lut[Math.Min(v[i], last)];
            linearized = true;
        }

        bool isFloat = data is RawData.F32;
        float defaultWhite = isFloat ? 1f : (float)(((1UL << (int)Math.Min(bits, 16)) - 1));
        var white = new List<float>();
        if (raw.F64s(Tags.WhiteLevel) is { } wl)
            foreach (var x in wl)
            {
                float f = (float)x;
                if (f > 0) white.Add(f);
            }
        if (white.Count == 0) white.Add(defaultWhite);
        // 浮點的 NaN／無限大是損傷：NaN 與 −∞ 讀成黑（0），+∞ 讀成白階
        if (data is RawData.F32 f32data)
        {
            var v = f32data.Samples;
            for (var i = 0; i < v.Length; i++)
            {
                if (!float.IsFinite(v[i]))
                    v[i] = v[i] > 0 ? (i % Math.Max(1, cpp) < white.Count ? white[i % Math.Max(1, cpp)] : white[0]) : 0f;
            }
        }

        var dim = raw.U64s(Tags.BlackLevelRepeatDim) ?? [];
        int br = 1, bc = 1;
        if (dim is [ulong r, ulong c] && r is >= 1 and <= 16 && c is >= 1 and <= 16)
        { br = (int)r; bc = (int)c; }
        static float Level(double x) => double.IsFinite(x) ? (float)x : 0f;
        var values = new List<float>();
        if (raw.F64s(Tags.BlackLevel) is { } bl)
            foreach (var x in bl) values.Add(Level(x));
        if (values.Count != br * bc * cpp)
            values = [values.Count > 0 ? values[0] : 0f];
        if (values.Count == 1) { br = 1; bc = 1; }
        List<float> Floats(ushort tag)
        {
            var output = new List<float>();
            if (raw.F64s(tag) is { } vs)
                foreach (var x in vs) output.Add(Level(x));
            return output;
        }
        var black = new BlackLevel { RepeatRows = br, RepeatCols = bc, Values = [.. values], DeltaH = [.. Floats(Tags.BlackLevelDeltaH)], DeltaV = [.. Floats(Tags.BlackLevelDeltaV)] };

        PixelRect activeArea;
        if (raw.U64s(Tags.ActiveArea) is [ulong top, ulong left, ulong bottom, ulong right]
            && bottom > top && right > left && bottom <= (ulong)h && right <= (ulong)w)
        {
            activeArea = new PixelRect(checked((int)left), checked((int)top), checked((int)(right - left)), checked((int)(bottom - top)));
        }
        else
        {
            activeArea = new PixelRect(0, 0, checked((int)w), checked((int)h));
        }
        PixelRect crop;
        if (raw.F64s(Tags.DefaultCropOrigin) is [double ox, double oy] && raw.F64s(Tags.DefaultCropSize) is [double cw, double ch]
            && cw >= 1.0 && ch >= 1.0 && ox >= 0.0 && oy >= 0.0)
        {
            crop = new PixelRect((int)Math.Round(ox), (int)Math.Round(oy), (int)Math.Round(cw), (int)Math.Round(ch))
                .Clipped(activeArea.Width, activeArea.Height);
        }
        else
        {
            crop = new PixelRect(0, 0, activeArea.Width, activeArea.Height);
        }

        Cfa? cfa = null;
        if (info.Photometric == TiffPhotometric.Cfa)
        {
            if ((raw.U16(Tags.CfaLayout) ?? 1) != 1)
                throw RawException.Unsupported("non-rectangular CFA layout");
            var cdim = raw.U64s(Tags.CfaRepeatPatternDim) ?? [2, 2];
            int rows, cols;
            if (cdim is [ulong cr, ulong cc] && cr is >= 1 and <= 16 && cc is >= 1 and <= 16)
            { rows = (int)cr; cols = (int)cc; }
            else throw RawException.Corrupt("bad CFARepeatPatternDim");
            byte[] assumed = [0, 1, 1, 2];
            byte[]? pat = raw.Bytes(Tags.CfaPatternEp)
                ?? (format != RawFormat.Dng && rows == 2 && cols == 2 ? assumed : null);
            if (pat is null)
                throw RawException.Corrupt("missing CFAPattern");
            if (pat.Length != rows * cols)
                throw RawException.Corrupt("CFAPattern size mismatch");
            var planes = raw.Bytes(Tags.CfaPlaneColor) ?? [0, 1, 2];
            var pattern = new byte[rows * cols];
            for (var i = 0; i < pattern.Length; i++)
            {
                int idx = Array.IndexOf(planes, pat[i]);
                if (idx < 0 || idx > 2)
                    throw RawException.Unsupported("CFA with more than three colours");
                pattern[i] = (byte)idx;
            }
            if (cpp != 1)
                throw RawException.Unsupported("CFA data with several samples per pixel");
            // 標籤錨定在 ActiveArea 左上；RawImage.cfa 錨定在感光元件原點，故以外移還原
            //（上游寫入器早期版本錨定在原點，以 Software 恰為 "LightCraft" 標記，沿用原讀法）
            var atOrigin = new Cfa(cols, rows, pattern);
            bool legacyWriter = raw.String(Tags.Software)?.Trim() == "LightCraft"
                || ifd0.String(Tags.Software)?.Trim() == "LightCraft";
            cfa = legacyWriter ? atOrigin : atOrigin.Shifted(cols - activeArea.X % cols, rows - activeArea.Y % rows);
        }

        var opcodes = new OpcodeLists
        {
            List1 = ParseOpcodes(raw.Bytes(Tags.OpcodeList1)),
            List2 = ParseOpcodes(raw.Bytes(Tags.OpcodeList2)),
            List3 = ParseOpcodes(raw.Bytes(Tags.OpcodeList3)),
        };

        var metadata = MetadataReader.FromTiff(tiff);
        metadata.Width = (uint)crop.Width;
        metadata.Height = (uint)crop.Height;
        var img = new RawImage
        {
            Format = format,
            Width = checked((int)w),
            Height = checked((int)h),
            Cpp = cpp,
            Data = data,
            Cfa = cfa,
            Bits = (int)bits,
            Black = black,
            White = [.. white],
            ActiveArea = activeArea,
            Crop = crop,
            Orientation = OrientationExtensions.FromExif(ifd0.U16(Tags.Orientation) ?? 1),
            Color = ColorData(ifd0, raw),
            WbMultipliers = null,
            Linearized = linearized,
            PixelAspect = DefaultScaleAspect(raw.F64s(Tags.DefaultScale)),
            Opcodes = opcodes,
            Metadata = metadata,
        };
        img.ValidateFor(mode);
        return img;
    }

    // 運算碼清單位元組（OpcodeList1 = 51008、OpcodeList2 = 51009、OpcodeList3 = 51022）依 DNG 規格解析
    //（lightcraft dng::opcode_list：raw.bytes(tag).map(parse_list).unwrap_or_default()；截斷的清單提早結束，損毀的運算碼收容為 Unknown）。
    private static List<Opcode> ParseOpcodes(byte[]? blob) => blob is null ? [] : OpcodeOps.ParseList(blob);

    /// <summary>DNG 1.7 列／欄交錯的還原（儲存光柵是馬賽克的排列；lightcraft <c>deinterleave</c>）。</summary>
    internal static RawData Deinterleave(RawData data, int w, int h, int cpp, int rows, int cols)
    {
        if (rows <= 1 && cols <= 1) return data;
        if (rows is < 1 or > 16 || cols is < 1 or > 16)
            throw RawException.Unsupported($"interleave factors {rows} × {cols}");
        var mapR = FieldMap(h, rows);
        var mapC = FieldMap(w, cols);
        if (data is RawData.U16 u16)
        {
            var v = u16.Samples;
            var output = new ushort[v.Length];
            Permute(v, output, w, h, cpp, (r, c) => mapR[r] * w + mapC[c]);
            return new RawData.U16(output);
        }
        else if (data is RawData.F32 f32)
        {
            var v = f32.Samples;
            var output = new float[v.Length];
            Permute(v, output, w, h, cpp, (r, c) => mapR[r] * w + mapC[c]);
            return new RawData.F32(output);
        }
        return data;
    }

    /// <summary>儲存列→影像列的映射（lightcraft <c>field_map</c>）。</summary>
    internal static int[] FieldMap(int len, int n)
    {
        var offs = new int[n + 1];
        int acc = 0;
        for (var g = 0; g <= n; g++)
        {
            offs[g] = acc;
            if (g < n && g < len) acc += (len - 1 - g) / n + 1;
        }
        var map = new int[len];
        for (var g = 0; g < n; g++)
            for (int k = 0, i = offs[g]; i < offs[g + 1]; k++, i++)
                map[i] = g + n * k;
        return map;
    }

    private static void Permute<T>(T[] v, T[] output, int w, int h, int cpp, Func<int, int, int> dest)
    {
        long n;
        try { n = checked((long)w * h * cpp); }
        catch (OverflowException) { throw RawException.Corrupt("image too large"); }
        if (v.Length != n)
            throw RawException.Corrupt("mosaic size does not match the image");
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
            {
                int from = (r * w + c) * cpp, to = dest(r, c) * cpp;
                Array.Copy(v, from, output, to, cpp);
            }
    }

    private static Mat3? ToMat3(double[]? v)
    {
        if (v is not { Length: 9 } || v.Any(x => !double.IsFinite(x))) return null;
        var m = Mat3.FromRows(v);
        return Math.Abs(m.Determinant()) > 1e-12 ? m : null;
    }

    private static double[]? Vec3(double[]? v) =>
        v is { Length: 3 } && v.All(x => double.IsFinite(x) && x > 0.0) ? [v[0], v[1], v[2]] : null;

    /// <summary>色彩標籤：DNG 放 IFD0，但有些寫入器放 raw IFD；raw IFD 優先（lightcraft <c>color_data</c>）。</summary>
    internal static ColorData ColorData(Ifd ifd0, Ifd raw)
    {
        double[]? Get(ushort tag) => raw.F64s(tag) ?? ifd0.F64s(tag);
        ushort GetI(ushort tag) => raw.U16(tag) ?? ifd0.U16(tag) ?? 0;
        var asShotXy = Get(Tags.AsShotWhiteXy);
        double baseExp = Get(Tags.BaselineExposure)?.FirstOrDefault() is { } be && double.IsFinite(be) ? be : 0.0;
        double baseOff = Get(Tags.BaselineExposureOffset)?.FirstOrDefault() is { } bo && double.IsFinite(bo) ? bo : 0.0;
        double? sharp = Get(Tags.BaselineSharpness)?.FirstOrDefault();
        return new ColorData
        {
            Illuminant = [GetI(Tags.CalibrationIlluminant1), GetI(Tags.CalibrationIlluminant2)],
            ColorMatrix = [ToMat3(Get(Tags.ColorMatrix1)), ToMat3(Get(Tags.ColorMatrix2))],
            ForwardMatrix = [ToMat3(Get(Tags.ForwardMatrix1)), ToMat3(Get(Tags.ForwardMatrix2))],
            CameraCalibration = [ToMat3(Get(Tags.CameraCalibration1)), ToMat3(Get(Tags.CameraCalibration2))],
            AnalogBalance = Vec3(Get(Tags.AnalogBalance)),
            AsShotNeutral = Vec3(Get(Tags.AsShotNeutral)),
            AsShotWhiteXy = asShotXy is { Length: 2 } && asShotXy[0] > 0.0 && asShotXy[1] > 0.0
                ? new Xy(asShotXy[0], asShotXy[1]) : null,
            BaselineExposure = baseExp + baseOff,
            BaselineSharpness = sharp is { } s && double.IsFinite(s) && s > 0.0 && s <= 16.0 ? s : null,
            Profile = ProfileLook(ifd0, raw),
            KelvinPresets = null,
        };
    }

    /// <summary>描述檔外觀標籤；損毀的忽略（lightcraft <c>profile_look</c>；GainTableMap 依計畫除外）。</summary>
    internal static ProfileLook ProfileLook(Ifd ifd0, Ifd raw)
    {
        Ifd Pick(ushort dims) => raw.Contains(dims) ? raw : ifd0;
        HsvTable? Table(ushort dims, ushort data, ushort enc)
        {
            var ifd = Pick(dims);
            var d = ifd.U64s(dims);
            var v = ifd.F64s(data);
            if (d is null || v is null) return null;
            ulong e = ifd.U64s(enc)?.FirstOrDefault() ?? 0;
            return HsvTable.FromTags(d, v, e);
        }
        return new ProfileLook
        {
            HueSatMap = [Table(Tags.ProfileHueSatMapDims, Tags.ProfileHueSatMapData1, Tags.ProfileHueSatMapEncoding),
                         Table(Tags.ProfileHueSatMapDims, Tags.ProfileHueSatMapData2, Tags.ProfileHueSatMapEncoding)],
            LookTable = Table(Tags.ProfileLookTableDims, Tags.ProfileLookTableData, Tags.ProfileLookTableEncoding),
            ToneCurve = (Pick(Tags.ProfileToneCurve).F64s(Tags.ProfileToneCurve) is { } t)
                ? ToneCurve.FromTag(t) : null,
        };
    }

    /// <summary><c>DefaultScale</c> 的像素高寬比：只有 1.5..4 的明顯比例才算（列拉伸）。</summary>
    internal static float DefaultScaleAspect(double[]? scale)
    {
        if (scale is [double h, double v] && h > 0.0 && v > 0.0 && v / h is >= 1.5 and <= 4.0)
            return (float)(v / h);
        return 1f;
    }
}
