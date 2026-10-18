// 移植自 LightCraft crates/raw/src/dngwrite.rs（DNG 寫入器：未壓縮／LJ92／Deflate 半浮點／全浮點／有損 JPEG）
// raw 影像存於 IFD0（DNG 1.7 §「File Structure」允許）。解碼器往返測試見 RawDecoder（Dng 切片）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.IO.Compression;
using System.Numerics;
using System.Text;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw;

/// <summary>DNG 寫入器（lightcraft <c>dngwrite</c>；bit-exact 解碼器往返測試亦由此驗證）。</summary>
public static class DngWriter
{
    /// <summary>此寫入器所寫 DNG 的 Software 標籤。裸 "LightCraft" 標記舊檔（CFAPattern 錨定感光元件原點；讀取器保留舊讀法）。</summary>
    public const string Software = "FilmBatch DNG 1";

    /// <summary>有損 DNG 8-bit 碼的 gamma（自選：接近等距，近似 sRGB 步階）。</summary>
    private const double LossyGamma = 2.2;

    // zlib 層級：BCL DeflateStream 取 Fastest（上游實測 level 1 的取捨；BCL 無數字層級可調）。

    /// <summary>IEEE 754 binary32 → binary16（捨入到偶數；溢位 → ±inf；NaN 保留）。</summary>
    public static ushort F32ToF16(float v)
    {
        uint b = BitConverter.SingleToUInt32Bits(v);
        ushort sign = (ushort)((b >> 16) & 0x8000);
        int exp = (int)((b >> 23) & 0xff);
        uint man = b & 0x7fffff;
        if (exp == 0xff) return (ushort)(sign | 0x7c00 | (man != 0 ? 0x200 : 0));
        int e = exp - 127 + 15;
        if (e >= 0x1f) return (ushort)(sign | 0x7c00);
        if (e <= 0)
        {
            if (e < -10) return sign;
            uint m = man | 0x800000;
            int shift = 14 - e;
            uint half = m >> shift;
            uint rem = m & ((1u << shift) - 1u);
            uint mid = 1u << (shift - 1);
            uint r = rem > mid || (rem == mid && (half & 1) == 1) ? half + 1 : half;
            return (ushort)(sign | r);
        }
        uint h = ((uint)e << 10) | (man >> 13);
        uint rem2 = man & 0x1fff;
        uint r2 = rem2 > 0x1000 || (rem2 == 0x1000 && (h & 1) == 1) ? h + 1 : h;
        return (ushort)(sign | r2);
    }

    /// <summary>
    /// 浮點預測器的逆運算（<c>Unpack.UndoFloatPredictor</c> 的反函式）：一列 <paramref name="n"/> 個
    /// big-endian 樣本（每樣本 <paramref name="bytesPer"/> 位元組）拆成位元組平面（高位在前），
    /// 再以 <paramref name="stride"/> 個樣本為步距做位元組差分。
    /// </summary>
    private static byte[] FloatPredict(ReadOnlySpan<byte> row, int n, int bytesPer, int stride)
    {
        var planes = new byte[row.Length];
        for (var i = 0; i < n; i++)
            for (var b = 0; b < bytesPer; b++)
                planes[b * n + i] = row[i * bytesPer + b];
        for (var i = planes.Length - 1; i >= stride; i--)
            planes[i] = (byte)(planes[i] - planes[i - stride]);
        return planes;
    }

    private static TiffValue SratMat(Mat3 m)
    {
        var v = m.ToArray();
        return new TiffValue.SRational([.. v.Select(x => DngWriterTiff.SRational(x))]);
    }

    private static TiffValue RatVec(double[] v) =>
        new TiffValue.Rational([.. v.Select(DngWriterTiff.Rational)]);

    /// <summary>有損 DNG 的 <c>LinearizationTable</c>（256 項：碼 → 線性 16 位元值）。</summary>
    private static ushort[] LossyLinearization()
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
            t[i] = (ushort)Math.Round(Math.Pow(i / 255.0, LossyGamma) * 65535.0);
        return t;
    }

    /// <summary>
    /// 有損 DNG 存放的影像：<paramref name="raw"/> 顯影到線性相機 RGB（白 = 1），
    /// 長邊縮到 <paramref name="longEdge"/> 以內，以 <see cref="LossyGamma"/> 曲線存成
    /// 8-bit 碼（<c>u16</c> 樣本），黑 0、線性化後白 65535。
    /// </summary>
    private static RawImage LossySource(RawImage raw, uint? longEdge)
    {
        var rgb = raw.Develop(DemosaicMethod.Ahd);
        if (longEdge is { } edge)
        {
            int e = (int)Math.Max(edge, 1);
            int longSide = Math.Max(rgb.Width, rgb.Height);
            if (longSide > e)
            {
                double s = (double)e / longSide;
                int w = Math.Max((int)Math.Round(rgb.Width * s), 1);
                int h = Math.Max((int)Math.Round(rgb.Height * s), 1);
                rgb = Resample.Resize(rgb, w, h, ResampleFilter.Lanczos3);
            }
        }
        if (rgb.Width == 0 || rgb.Height == 0)
            throw RawException.Corrupt("empty image");
        float inv = (float)(1.0 / LossyGamma);
        int n = checked(rgb.Width * rgb.Height * 3);
        var data = new ushort[n];
        Parallel.For(0, rgb.Height, y =>
        {
            for (var x = 0; x < rgb.Width; x++)
                for (var s = 0; s < 3; s++)
                {
                    float v = rgb.Data[(y * rgb.Width + x) * 3 + s];
                    data[(y * rgb.Width + x) * 3 + s] = float.IsFinite(v)
                        ? (ushort)Math.Round(Math.Pow(Math.Clamp(v, 0f, 1f), inv) * 255.0)
                        : (ushort)0;
                }
        });
        var full = new PixelRect(0, 0, rgb.Width, rgb.Height);
        // ..raw.clone()：色彩、中繼資料、方向與白平衡倍率隨資料走；
        // 運算碼清單 1/2 作用於去馬賽克前、清單 3 已由 Develop 套用，故清空
        return new RawImage
        {
            Format = raw.Format,
            Width = rgb.Width,
            Height = rgb.Height,
            Cpp = 3,
            Data = new RawData.U16(data),
            Cfa = null,
            Bits = 8,
            Black = new BlackLevel(),
            White = [65535f],
            ActiveArea = full,
            Crop = full,
            Orientation = raw.Orientation,
            Color = raw.Color.Clone(),
            WbMultipliers = raw.WbMultipliers,
            Metadata = raw.Metadata,
            Linearized = true,
            PixelAspect = raw.PixelAspect,
        };
    }

    /// <summary>把 <paramref name="raw"/> 序列化為 DNG。</summary>
    public static byte[] WriteDng(RawImage raw, DngWriteOptions? options = null)
    {
        options ??= new DngWriteOptions();
        raw.Validate();
        RawImage? lossy = null;
        if (options.Compression is DngCompression.LossyJpeg lj)
            lossy = LossySource(raw, lj.LongEdge);
        raw = lossy ?? raw;
        int w = raw.Width, h = raw.Height, cpp = raw.Cpp;
        var ifd = new DngWriterIfd();
        ifd.Set(Tags.NewSubfileType, new TiffValue.Long([0]));
        ifd.Set(Tags.ImageWidth, new TiffValue.Long([(uint)w]));
        ifd.Set(Tags.ImageLength, new TiffValue.Long([(uint)h]));
        ifd.Set(Tags.SamplesPerPixel, new TiffValue.Short([(ushort)cpp]));
        ifd.Set(Tags.PlanarConfiguration, new TiffValue.Short([1]));
        ifd.Set(Tags.Photometric, new TiffValue.Short([raw.Cfa is not null ? TiffPhotometric.Cfa : TiffPhotometric.LinearRaw]));
        ifd.Set(Tags.DngVersion, new TiffValue.Byte([1, 4, 0, 0]));
        ifd.Set(Tags.DngBackwardVersion, new TiffValue.Byte([1, 3, 0, 0]));
        var make = raw.Metadata.Make ?? "";
        var model = raw.Metadata.Model ?? "";
        if (make.Length > 0) ifd.Set(Tags.Make, new TiffValue.Ascii(make));
        if (model.Length > 0) ifd.Set(Tags.Model, new TiffValue.Ascii(model));
        bool modelIncludesMake = make.Length > 0
            && model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
            && (model.Length == make.Length || (!char.IsLetterOrDigit(model[make.Length]) && model[make.Length] != '_'));
        string unique = modelIncludesMake || make.Length == 0 ? model : model.Length == 0 ? make : $"{make} {model}";
        ifd.Set(Tags.UniqueCameraModel, new TiffValue.Ascii(unique.Length > 0 ? unique : "FilmBatch"));
        ifd.Set(Tags.Orientation, new TiffValue.Short([(ushort)raw.Orientation.ToExif()]));
        ifd.Set(Tags.Software, new TiffValue.Ascii(Software));
        ifd.Set(Tags.Xmp, new TiffValue.Byte(Encoding.UTF8.GetBytes(options.Xmp ?? DngWriterXmp.Build(raw.Metadata))));
        WriteExif(ifd, raw.Metadata);

        if (raw.Cfa is { } cfa)
        {
            ifd.Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([(ushort)cfa.Height, (ushort)cfa.Width]));
            var a = raw.ActiveArea;
            ifd.Set(Tags.CfaPatternEp, new TiffValue.Byte(cfa.Shifted(a.X, a.Y).Pattern));
            ifd.Set(Tags.CfaPlaneColor, new TiffValue.Byte([0, 1, 2]));
            ifd.Set(Tags.CfaLayout, new TiffValue.Short([1]));
        }
        var b = raw.Black;
        ifd.Set(Tags.BlackLevelRepeatDim, new TiffValue.Short([(ushort)b.RepeatRows, (ushort)b.RepeatCols]));
        var bl = b.Values.Select(v => (double)v).ToArray();
        if (bl.Length == 1 && cpp > 1) bl = Enumerable.Repeat(bl[0], cpp).ToArray();
        ifd.Set(Tags.BlackLevel, RatVec(bl));
        if (b.DeltaH.Length > 0)
            ifd.Set(Tags.BlackLevelDeltaH, new TiffValue.SRational([.. b.DeltaH.Select(v => DngWriterTiff.SRational(v))])); ;
        if (b.DeltaV.Length > 0)
            ifd.Set(Tags.BlackLevelDeltaV, new TiffValue.SRational([.. b.DeltaV.Select(v => DngWriterTiff.SRational(v))])); ;
        if (raw.Data is RawData.F32)
        {
            ifd.Set(Tags.WhiteLevel, RatVec(Enumerable.Range(0, cpp).Select(s => (double)raw.WhiteAt(s)).ToArray()));
        }
        else
        {
            var white = new uint[cpp];
            for (var s = 0; s < cpp; s++)
                white[s] = (uint)Math.Clamp(Math.Round(raw.WhiteAt(s)), 0, (double)uint.MaxValue);
            ifd.Set(Tags.WhiteLevel, new TiffValue.Long(white));
        }
        var aa = raw.ActiveArea;
        ifd.Set(Tags.ActiveArea, new TiffValue.Long([(uint)aa.Y, (uint)aa.X, (uint)(aa.Y + aa.Height), (uint)(aa.X + aa.Width)]));
        ifd.Set(Tags.DefaultCropOrigin, new TiffValue.Long([(uint)raw.Crop.X, (uint)raw.Crop.Y]));
        ifd.Set(Tags.DefaultCropSize, new TiffValue.Long([(uint)raw.Crop.Width, (uint)raw.Crop.Height]));
        if (float.IsFinite(raw.PixelAspect) && raw.PixelAspect > 0 && raw.PixelAspect != 1f)
            ifd.Set(Tags.DefaultScale, RatVec([1, raw.PixelAspect]));

        var c = raw.Color;
        var illumTags = new[] { Tags.CalibrationIlluminant1, Tags.CalibrationIlluminant2 };
        for (var i = 0; i < 2; i++)
            if (c.ColorMatrix[i] is not null)
                ifd.Set(illumTags[i], new TiffValue.Short([c.Illuminant[i]]));
        var pairs = new[]
        {
            (c.ColorMatrix, new[] { Tags.ColorMatrix1, Tags.ColorMatrix2 }),
            (c.ForwardMatrix, new[] { Tags.ForwardMatrix1, Tags.ForwardMatrix2 }),
            (c.CameraCalibration, new[] { Tags.CameraCalibration1, Tags.CameraCalibration2 }),
        };
        foreach (var (pair, tags) in pairs)
            for (var i = 0; i < 2; i++)
                if (pair[i] is { } m)
                    ifd.Set(tags[i], SratMat(m));
        if (c.AnalogBalance is { } ab) ifd.Set(Tags.AnalogBalance, RatVec(ab.Select(v => (double)v).ToArray()));
        if (c.AsShotNeutral is { } asn) ifd.Set(Tags.AsShotNeutral, RatVec(asn.Select(v => (double)v).ToArray()));
        else if (c.AsShotWhiteXy is { } xy) ifd.Set(Tags.AsShotWhiteXy, RatVec([xy.X, xy.Y]));
        else if (raw.WbMultipliers is { } wb && wb.All(v => v > 0f && float.IsFinite(v)))
            ifd.Set(Tags.AsShotNeutral, RatVec(wb.Select(v => 1.0 / v).ToArray()));
        ifd.Set(Tags.BaselineExposure, new TiffValue.SRational([DngWriterTiff.SRational(c.BaselineExposure)]));
        if (c.BaselineSharpness is { } sharp) ifd.Set(Tags.BaselineSharpness, RatVec([sharp]));

        var p = c.Profile;
        void TableTags(DngWriterIfd target, HsvTable table, ushort dims, ushort enc)
        {
            target.Set(dims, new TiffValue.Long([(uint)table.HueDivisions, (uint)table.SatDivisions, (uint)table.ValDivisions]));
            if (table.SrgbValue) target.Set(enc, new TiffValue.Long([1]));
        }
        static float[] Flat(HsvTable table) => [.. table.Data];
        if (p.HueSatMap is [var ha, var hb])
        {
            if (ha is not null)
            {
                TableTags(ifd, ha, Tags.ProfileHueSatMapDims, Tags.ProfileHueSatMapEncoding);
                ifd.Set(Tags.ProfileHueSatMapData1, new TiffValue.Float(Flat(ha)));
                if (hb is not null && hb.HueDivisions == ha.HueDivisions && hb.SatDivisions == ha.SatDivisions && hb.ValDivisions == ha.ValDivisions)
                    ifd.Set(Tags.ProfileHueSatMapData2, new TiffValue.Float(Flat(hb)));
            }
            else if (hb is not null)
            {
                TableTags(ifd, hb, Tags.ProfileHueSatMapDims, Tags.ProfileHueSatMapEncoding);
                ifd.Set(Tags.ProfileHueSatMapData1, new TiffValue.Float(Flat(hb)));
            }
        }
        if (p.LookTable is { } look)
        {
            TableTags(ifd, look, Tags.ProfileLookTableDims, Tags.ProfileLookTableEncoding);
            ifd.Set(Tags.ProfileLookTableData, new TiffValue.Float(Flat(look)));
        }
        if (p.ToneCurve is { } curve)
            ifd.Set(Tags.ProfileToneCurve, new TiffValue.Float([.. curve.Points]));
        if (p.GainTableMap is { } gtm)
        {
            var (version2, bytes) = gtm.ToBytes(options.Order);
            ifd.Set(version2 ? Tags.ProfileGainTableMap2 : Tags.ProfileGainTableMap, new TiffValue.Undefined(bytes));
        }
        var lists = new[] { (raw.Opcodes.List1, Tags.OpcodeList1), (raw.Opcodes.List2, Tags.OpcodeList2), (raw.Opcodes.List3, Tags.OpcodeList3) };
        foreach (var (list, tag) in lists)
            if (list.Count > 0)
                ifd.Set(tag, new TiffValue.Undefined(OpcodeOps.WriteList(list)));

        var order = options.Order;
        switch (raw.Data, options.Compression)
        {
            case (RawData.F32 f32, DngCompression.Deflate def):
            {
                long tileL = Math.Max((long)def.Tile, 16) & ~15L;
                int tile = (int)tileL;
                int bp = def.Half ? 2 : 4;
                ifd.Set(Tags.BitsPerSample, new TiffValue.Short(Enumerable.Repeat((ushort)(bp * 8), cpp).ToArray()));
                ifd.Set(Tags.SampleFormat, new TiffValue.Short(Enumerable.Repeat((ushort)3, cpp).ToArray()));
                ifd.Set(Tags.Compression, new TiffValue.Short([TiffCompression.AdobeDeflate]));
                ifd.Set(Tags.Predictor, new TiffValue.Short([3]));
                long ta = ((long)w + tile - 1) / tile, td = ((long)h + tile - 1) / tile;
                var tiles = new byte[ta * td][];
                Parallel.For(0, tiles.Length, i =>
                {
                    int tx = (int)((i % ta) * tile), ty = (int)((i / ta) * tile);
                    using var ms = new MemoryStream(tile * tile * cpp * bp);
                    var row = new byte[tile * cpp * bp];
                    for (var y = 0; y < tile; y++)
                    {
                        int sy = Math.Min(ty + y, h - 1);
                        row.AsSpan().Clear();
                        for (var x = 0; x < tile; x++)
                        {
                            int sx = Math.Min(tx + x, w - 1);
                            for (var s = 0; s < cpp; s++)
                            {
                                // 上游以 big-endian 位元組寫預測器平面（讀取側一律 big-endian 還原）
                                float f = f32.Samples[(sy * w + sx) * cpp + s];
                                int o = (x * cpp + s) * bp;
                                if (def.Half)
                                {
                                    ushort hh = F32ToF16(f);
                                    row[o] = (byte)(hh >> 8);
                                    row[o + 1] = (byte)hh;
                                }
                                else
                                {
                                    uint bits32 = BitConverter.SingleToUInt32Bits(f);
                                    row[o] = (byte)(bits32 >> 24);
                                    row[o + 1] = (byte)(bits32 >> 16);
                                    row[o + 2] = (byte)(bits32 >> 8);
                                    row[o + 3] = (byte)bits32;
                                }
                            }
                        }
                        var predicted = FloatPredict(row, tile * cpp, bp, cpp);
                        ms.Write(predicted);
                    }
                    tiles[i] = Deflate(ms.ToArray());
                });
                ifd.SetImage(new DngWriterImageData.Tiles((uint)tile, (uint)tile, tiles));
                break;
            }
            case (RawData.F32 f32, _):
            {
                ifd.Set(Tags.BitsPerSample, new TiffValue.Short(Enumerable.Repeat((ushort)32, cpp).ToArray()));
                ifd.Set(Tags.SampleFormat, new TiffValue.Short(Enumerable.Repeat((ushort)3, cpp).ToArray()));
                ifd.Set(Tags.Compression, new TiffValue.Short([TiffCompression.None]));
                var bytes = new List<byte>(checked(f32.Samples.Length * 4));
                foreach (var x in f32.Samples)
                {
                    uint bits = BitConverter.SingleToUInt32Bits(x);
                    if (order == ByteOrder.Little)
                        for (var k = 0; k < 4; k++) bytes.Add((byte)(bits >> (8 * k)));
                    else
                        for (var k = 3; k >= 0; k--) bytes.Add((byte)(bits >> (8 * k)));
                }
                ifd.SetImage(new DngWriterImageData.Strips((uint)h, [bytes.ToArray()]));
                break;
            }
            case (RawData.U16 u16, DngCompression.LossyJpeg lossyJpeg):
            {
                long tileL = Math.Clamp((long)lossyJpeg.Tile, 16, 4096) & ~15L;
                int tile = (int)tileL;
                ifd.Set(Tags.BitsPerSample, new TiffValue.Short(Enumerable.Repeat((ushort)8, cpp).ToArray()));
                ifd.Set(Tags.Compression, new TiffValue.Short([TiffCompression.LossyJpeg]));
                ifd.Set(Tags.LinearizationTable, new TiffValue.Short(LossyLinearization()));
                byte quality = Math.Clamp(lossyJpeg.Quality, (byte)1, (byte)100);
                long ta = ((long)w + tile - 1) / tile, td = ((long)h + tile - 1) / tile;
                var tiles = new byte[ta * td][];
                var errors = new RawException[tiles.Length];
                Parallel.For(0, tiles.Length, i =>
                {
                    try
                    {
                        int tx = (int)((i % ta) * tile), ty = (int)((i / ta) * tile);
                        var buf = new byte[tile * tile * 3];
                        var src = u16.Samples;
                        for (var y = 0; y < tile; y++)
                        {
                            int sy = Math.Min(ty + y, h - 1);
                            for (var x = 0; x < tile; x++)
                            {
                                int sx = Math.Min(tx + x, w - 1);
                                for (var s = 0; s < 3; s++)
                                {
                                    int v = src[(sy * w + sx) * cpp + Math.Min(s, cpp - 1)];
                                    buf[(y * tile + x) * 3 + s] = (byte)Math.Min(v, 255);
                                }
                            }
                        }
                        tiles[i] = DngWriterJpeg.Encode(buf, tile, tile, quality);
                    }
                    catch (RawException e) { errors[i] = e; }
                });
                for (var i = 0; i < errors.Length; i++)
                    if (errors[i] is { } e) throw e;
                ifd.SetImage(new DngWriterImageData.Tiles((uint)tile, (uint)tile, tiles));
                break;
            }
            case (RawData.U16 u16, _):
            {
                ushort maxv = 0;
                foreach (var v in u16.Samples) if (v > maxv) maxv = v;
                int used = maxv == 0 ? 0 : 16 - (BitOperations.LeadingZeroCount(maxv) - 16);
                int bits = Math.Max(Math.Max(used, Math.Min(raw.Bits, 16)), 2);
                bool uncompressed = options.Compression is DngCompression.Uncompressed;
                ifd.Set(Tags.BitsPerSample, new TiffValue.Short(Enumerable.Repeat((ushort)(uncompressed ? 16 : bits), cpp).ToArray()));
                switch (options.Compression)
                {
                    case DngCompression.Uncompressed:
                    {
                        ifd.Set(Tags.Compression, new TiffValue.Short([TiffCompression.None]));
                        long rowsL = Math.Clamp(65536L / (w * cpp * 2L), 1, h);
                        int rows = (int)rowsL;
                        var src = u16.Samples;
                        int strips = (int)(((long)h + rows - 1) / rows);
                        var chunks = new byte[strips][];
                        Parallel.For(0, strips, i =>
                        {
                            int y0 = i * rows;
                            int count = Math.Min(rows, h - y0) * w * cpp;
                            var b = new byte[count * 2];
                            for (var k = 0; k < count; k++)
                            {
                                ushort v = src[(y0 * w * cpp) + k];
                                if (order == ByteOrder.Little) { b[k * 2] = (byte)v; b[k * 2 + 1] = (byte)(v >> 8); }
                                else { b[k * 2] = (byte)(v >> 8); b[k * 2 + 1] = (byte)v; }
                            }
                            chunks[i] = b;
                        });
                        ifd.SetImage(new DngWriterImageData.Strips((uint)rows, chunks));
                        break;
                    }
                    case DngCompression.Deflate def:
                    {
                        long tileL = Math.Max((long)def.Tile, 16) & ~15L;
                        int tile = (int)tileL;
                        ifd.Set(Tags.BitsPerSample, new TiffValue.Short(Enumerable.Repeat((ushort)16, cpp).ToArray()));
                        ifd.Set(Tags.Compression, new TiffValue.Short([TiffCompression.AdobeDeflate]));
                        ifd.Set(Tags.Predictor, new TiffValue.Short([2]));
                        long ta = ((long)w + tile - 1) / tile, td = ((long)h + tile - 1) / tile;
                        var tiles = new byte[ta * td][];
                        var src = u16.Samples;
                        Parallel.For(0, tiles.Length, i =>
                        {
                            int tx = (int)((i % ta) * tile), ty = (int)((i / ta) * tile);
                            using var ms = new MemoryStream(tile * tile * cpp * 2);
                            var row = new ushort[tile * cpp];
                            var outRow = new byte[tile * cpp * 2];
                            for (var y = 0; y < tile; y++)
                            {
                                int sy = Math.Min(ty + y, h - 1);
                                for (var x = 0; x < tile; x++)
                                {
                                    int sx = Math.Min(tx + x, w - 1);
                                    for (var s = 0; s < cpp; s++)
                                        row[x * cpp + s] = src[(sy * w + sx) * cpp + s];
                                }
                                // In-place delta must run right→left: row[k-cpp] still holds
                                // the original sample only before it is itself differenced.
                                for (var k = row.Length - 1; k >= cpp; k--)
                                    row[k] = (ushort)(row[k] - row[k - cpp]);
                                for (var k = 0; k < row.Length; k++)
                                {
                                    if (order == ByteOrder.Little) { outRow[k * 2] = (byte)row[k]; outRow[k * 2 + 1] = (byte)(row[k] >> 8); }
                                    else { outRow[k * 2] = (byte)(row[k] >> 8); outRow[k * 2 + 1] = (byte)row[k]; }
                                }
                                ms.Write(outRow);
                            }
                            tiles[i] = Deflate(ms.ToArray());
                        });
                        ifd.SetImage(new DngWriterImageData.Tiles((uint)tile, (uint)tile, tiles));
                        break;
                    }
                    case DngCompression.LossyJpeg:
                        throw RawException.Unsupported("lossy DNG of this data");
                    case DngCompression.Lj92 ljpeg:
                    {
                        long tileL = Math.Max((long)ljpeg.Tile, 2) & ~1L;
                        if (tileL > 65534) throw RawException.Limit("tile too large");
                        int tile = (int)tileL;
                        ifd.Set(Tags.Compression, new TiffValue.Short([TiffCompression.Jpeg]));
                        long ta = ((long)w + tile - 1) / tile, td = ((long)h + tile - 1) / tile;
                        var tiles = new byte[ta * td][];
                        var src = u16.Samples;
                        var errors = new RawException[tiles.Length];
                        Parallel.For(0, tiles.Length, i =>
                        {
                            try
                            {
                                int tx = (int)((i % ta) * tile), ty = (int)((i / ta) * tile);
                                var buf = new ushort[tile * tile * cpp];
                                for (var y = 0; y < tile; y++)
                                {
                                    int sy = Math.Min(ty + y, h - 1);
                                    for (var x = 0; x < tile; x++)
                                    {
                                        int sx = Math.Min(tx + x, w - 1);
                                        for (var s = 0; s < cpp; s++)
                                            buf[(y * tile + x) * cpp + s] = src[(sy * w + sx) * cpp + s];
                                    }
                                }
                                tiles[i] = cpp == 1
                                    ? DngWriterLjpeg.Encode(buf, tile / 2, tile, 2, (byte)bits, 1, 0)
                                    : DngWriterLjpeg.Encode(buf, tile, tile, cpp, (byte)bits, 1, 0);
                            }
                            catch (RawException e) { errors[i] = e; }
                        });
                        for (var i = 0; i < errors.Length; i++)
                            if (errors[i] is { } e) throw e;
                        ifd.SetImage(new DngWriterImageData.Tiles((uint)tile, (uint)tile, tiles));
                        break;
                    }
                    default:
                        throw RawException.Unsupported($"DNG compression {options.Compression}");
                }
                break;
            }
        }
        return new DngWriterTiff(order).Write([ifd]);
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream(raw.Length + 16);
        ms.WriteByte(0x78);
        ms.WriteByte(0x01); // zlib 包頭：32K window、最低壓縮層級標記
        uint a = 1, b = 0;
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            ds.Write(raw);
        foreach (var v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static void WriteExif(DngWriterIfd root, Metadata metadata)
    {
        DngWriterIfd? exif = null;
        void Set(ushort tag, TiffValue value) => (exif ??= new DngWriterIfd()).Set(tag, value);
        void Text(ushort tag, string? value)
        {
            if (!string.IsNullOrEmpty(value)) Set(tag, new TiffValue.Ascii(value));
        }
        void Positive(ushort tag, double? value)
        {
            if (value is > 0 && double.IsFinite(value.Value))
                Set(tag, new TiffValue.Rational([DngWriterTiff.Rational(value.Value)]));
        }
        Text(Tags.BodySerialNumber, metadata.SerialNumber);
        Text(Tags.LensMake, metadata.LensMake);
        Text(Tags.LensModel, metadata.LensModel);
        Positive(Tags.ExposureTime, metadata.ExposureTime);
        Positive(Tags.FNumber, metadata.FNumber);
        Positive(Tags.FocalLength, metadata.FocalLength);
        if (metadata.LensSpec is { Length: 4 } spec && spec.All(v => double.IsFinite(v) && v >= 0))
            Set(Tags.LensSpecification, new TiffValue.Rational(spec.Select(DngWriterTiff.Rational).ToArray()));
        if (metadata.Iso is { } iso && iso > 0)
        {
            Set(Tags.IsoSpeed, new TiffValue.Short([(ushort)Math.Min(iso, ushort.MaxValue)]));
            if (iso >= ushort.MaxValue)
            {
                Set(Tags.SensitivityType, new TiffValue.Short([3]));
                Set(Tags.IsoSpeedRatingsExt, new TiffValue.Long([iso]));
            }
        }
        if (metadata.FocalLength35mm is { } equivalent && equivalent > 0 && equivalent <= ushort.MaxValue)
            Set(Tags.FocalLength35mm, new TiffValue.Short([(ushort)Math.Round(equivalent)]));
        if (metadata.ExposureBias is { } bias && double.IsFinite(bias))
            Set(Tags.ExposureBias, new TiffValue.SRational([DngWriterTiff.SRational(bias)]));
        if (metadata.CaptureTime is { } time)
        {
            Text(Tags.DateTimeOriginal, FormattableString.Invariant(
                $"{time.Year:0000}:{time.Month:00}:{time.Day:00} {time.Hour:00}:{time.Minute:00}:{time.Second:00}"));
            if (time.Millis > 0)
                Text(Tags.SubsecTimeOriginal, time.Millis.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
            if (time.OffsetMinutes is { } offset)
                Text(Tags.OffsetTimeOriginal, FormattableString.Invariant(
                    $"{(offset < 0 ? "-" : "+")}{Math.Abs(offset) / 60:00}:{Math.Abs(offset) % 60:00}"));
        }
        if (exif is not null)
        {
            Set(Tags.ExifVersion, new TiffValue.Undefined("0232"u8.ToArray()));
            root.SetChild(Tags.ExifIfd, exif);
        }
    }

    private static ushort IlluminantOf(Xy white) =>
        Math.Abs(white.X - ColorSpaces.D50.X) < 1e-3 && Math.Abs(white.Y - ColorSpaces.D50.Y) < 1e-3 ? (ushort)23 : (ushort)21;

    /// <summary>
    /// 渲染輸出 DNG：已渲染、已轉正影像的線性 DNG（<c>LinearRaw</c>、16-bit float Deflate 磚塊）。
    /// <paramref name="rgb"/> 為 <paramref name="width"/> × <paramref name="height"/> 交錯線性 RGB（<paramref name="space"/>，
    /// 白 = 1；超過 1 保留到半浮點上限，負值與非有限值歸 0）。色彩標籤精確描述該空間——
    /// <c>ColorMatrix1</c> = XYZ → 空間、<c>ForwardMatrix1</c> = 空間 → XYZ D50、中性 <c>AsShotNeutral</c>——
    /// 恆等 <c>ProfileToneCurve</c> 避免 raw 轉換器再疊預設色調曲線。
    /// </summary>
    public static byte[] WriteRenderedDng(int width, int height, ReadOnlySpan<float> rgb, RgbSpace space, Metadata metadata, string? xmp = null)
    {
        long pixels;
        try { pixels = checked((long)width * height * 3); }
        catch (OverflowException) { throw RawException.Limit("image too large"); }
        if (width <= 0 || height <= 0 || (long)width > uint.MaxValue || (long)height > uint.MaxValue)
            throw RawException.Corrupt($"cannot write a {width}×{height} DNG");
        if (rgb.Length < pixels)
            throw RawException.Corrupt($"{rgb.Length} samples for a {width}×{height} RGB image");
        var data = new float[pixels];
        for (var i = 0; i < pixels; i++)
        {
            float v = rgb[i];
            data[i] = float.IsFinite(v) ? Math.Clamp(v, 0f, 65504f) : 0f;
        }
        var full = new PixelRect(0, 0, width, height);
        var forward = ColorSpaces.Bradford(space.White, ColorSpaces.D50).Mul(space.ToXyz());
        var color = new ColorData
        {
            Illuminant = [IlluminantOf(space.White), 0],
            ColorMatrix = [space.FromXyz(), null],
            ForwardMatrix = [forward, null],
            AsShotNeutral = [1.0, 1.0, 1.0],
            Profile = new ProfileLook { ToneCurve = new ToneCurve { Points = [0f, 0f, 1f, 1f] } },
        };
        var raw = new RawImage
        {
            Format = RawFormat.Dng,
            Width = width,
            Height = height,
            Cpp = 3,
            Data = new RawData.F32(data),
            Cfa = null,
            Bits = 16,
            Black = BlackLevel.Uniform(0f),
            White = [1f],
            ActiveArea = full,
            Crop = full,
            Orientation = Orientation.Normal,
            Color = color,
            WbMultipliers = null,
            Linearized = false,
            PixelAspect = 1f,
            Opcodes = new OpcodeLists(),
            Metadata = metadata,
        };
        return WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Deflate(256, true), Order = ByteOrder.Little, Xmp = xmp });
    }
}
