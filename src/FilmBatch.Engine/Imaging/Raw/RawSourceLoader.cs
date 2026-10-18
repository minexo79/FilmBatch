// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Core.Projects;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Vendors.Fujifilm;

namespace FilmBatch.Engine.Imaging.Raw;

internal static class RawSourceLoader
{
    private const float HighlightClip = 0.99f;

    internal static bool IsRawExtension(string path) =>
        Project.RawExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsCandidate(string path) => IsRawExtension(path)
        || Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    /// <summary>一般影像回傳 null；可辨識但不支援或損毀的 RAW 一律回報錯誤，不改用嵌入 JPEG。</summary>
    internal static SourceImage? TryLoad(string path, int edge, RawLoadOptions rawOptions = default)
    {
        if (!IsCandidate(path)) return null;
        if (!IsRawExtension(path))
        {
            if (!FitsBuffer(path)) return null;
            try { _ = RawDecoder.ProbeInfoFile(path); }
            catch (RawException ex) when (ex.Kind == RawErrorKind.NotRaw) { return null; }
        }
        var bytes = File.ReadAllBytes(path);
        RawImage raw;
        try { raw = RawDecoder.Decode(bytes); }
        catch (RawException ex) when (!IsRawExtension(path) && ex.Kind == RawErrorKind.NotRaw) { return null; }
        var info = raw.Info();
        var masks = rawOptions.IncludeSemanticMasks ? FilmBatch.Raw.RawSemanticMasks.Read(bytes) : [];
        var directory = rawOptions.LensDatabasePath;
        if (!string.IsNullOrWhiteSpace(directory) && !Path.IsPathRooted(directory))
            directory = Path.GetFullPath(directory, Path.GetDirectoryName(Path.GetFullPath(path))!);
        var scene = DevelopScene(raw, bytes, edge, rawOptions with { LensDatabasePath = directory });
        return new SourceImage(scene)
        {
            FilePath = Path.GetFullPath(path), RawOptions = rawOptions, RawInfo = info, SemanticMasks = masks,
        };
    }

    internal static RawInfo? TryReadInfo(string path)
    {
        if (!IsCandidate(path)) return null;
        if (!IsRawExtension(path) && !FitsBuffer(path)) return null;
        try { return RawDecoder.ProbeInfoFile(path); }
        catch (RawException ex) when (!IsRawExtension(path) && ex.Kind == RawErrorKind.NotRaw) { return null; }
    }

    private static bool FitsBuffer(string path)
    {
        // 檢查的是「已知過大」：檔案不存在或無法存取時不代為決定，沿用原本的讀取＋例外路徑。
        try { return new FileInfo(path).Length <= Array.MaxLength; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { return true; }
        catch { return false; }
    }
    internal static Stream? OpenExifTiff(string path)
    {
        if (!IsCandidate(path)) return null;
        var head = new byte[16];
        try
        {
            using var probe = File.OpenRead(path);
            if (probe.Read(head, 0, 16) < 16) return null;
        }
        catch { return null; }
        if (head.AsSpan(4, 8).SequenceEqual("ftypcrx "u8))
            return Cr3ExifTiff(path);
        if (head.AsSpan().StartsWith("FUJIFILMCCD-RAW"u8))
            return RafExifTiff(path);
        var iiMm = (head[0] == (byte)'I' && head[1] == (byte)'I') || (head[0] == (byte)'M' && head[1] == (byte)'M');
        if (!iiMm) return null;
        var magic = head[0] == (byte)'I' ? (head[2] | (head[3] << 8)) : (head[3] | (head[2] << 8));
        if (magic is not (42 or 43 or 0x4f52 or 0x5352 or 0x0055)) return null;
        try { return File.OpenRead(path); } catch { return null; }
    }

    /// <summary>CR3 的合併 EXIF 目錄（<c>CMT1</c> IFD0 + <c>CMT2</c> Exif IFD + <c>CMT4</c> GPS IFD；上游 <c>cr3::merged_exif</c> 語意）。
    /// 各區塊以自己的位元組序讀取、正規化為小端序；失敗時回傳 null，由呼叫端改用無 EXIF 輸出。</summary>
    private static Stream? Cr3ExifTiff(string path)
    {
        // M3：CR3 的 CMT 盒只在 moov 附近，先讀檔頭判斷大小；超過緩衝上限不載入，由一般影像路徑處理。
        long length;
        try { length = new FileInfo(path).Length; }
        catch { return null; }
        if (length > Array.MaxLength) return null;
        byte[] file;
        try { file = RawSparse.ReadFile(path); }
        catch { return null; }
        var container = Cr3ContainerParser.Parse(new ReadOnlyMemory<byte>(file));
        if (container is null) return null;
        try
        {
            var profile = FilmBatch.Engine.Exporting.TiffMetadata.NormalizeCr3(container.Cmt, 0, 0);
            var bytes = profile.ToByteArray();
            if (bytes is null || bytes.Length <= 6) return null;
            var tiff = new byte[bytes.Length - 6];
            Buffer.BlockCopy(bytes, 6, tiff, 0, tiff.Length);
            return new MemoryStream(tiff, writable: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException or ImageMagick.MagickException)
        {
            return null;
        }
    }

    /// <summary>RAF 內嵌預覽 JPEG 的 APP1 EXIF（<c>Exif\0\0</c> 後的 TIFF 串流；無時回傳 null）。</summary>
    private static Stream? RafExifTiff(string path)
    {
        byte[] file;
        try { file = RawSparse.ReadFile(path, includePreview: true); }
        catch { return null; }
        var jpeg = Raf.PreviewJpeg(new ReadOnlyMemory<byte>(file));
        if (jpeg is null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return null;
        var i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
                return null;
            var j = i + 1;
            while (j < jpeg.Length && jpeg[j] == 0xFF) j++;
            if (j >= jpeg.Length) return null;
            var marker = jpeg[j];
            if (marker == 0x00 || marker == 0xD9) return null;
            i = j + 1;
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7)) continue;
            if (i + 1 >= jpeg.Length) return null;
            var len = (jpeg[i] << 8) | jpeg[i + 1];
            if (len < 2 || i + len > jpeg.Length) return null;
            if (marker == 0xDA) return null;
            if (marker == 0xE1 && len >= 16
                && jpeg[i + 2] == (byte)'E' && jpeg[i + 3] == (byte)'x'
                && jpeg[i + 4] == (byte)'i' && jpeg[i + 5] == (byte)'f'
                && jpeg[i + 6] == 0 && jpeg[i + 7] == 0)
            {
                return new MemoryStream(jpeg, i + 8, len - 8, writable: false);
            }
            i += len;
        }
        return null;
    }
    /// <summary>保留線性 Rec.2020,直到相機曲線與 sRGB 編碼;嵌入 JPEG 只用來估計相機外觀。</summary>
    internal static (RgbImage Image, CameraTone? Tone) DevelopLinear(RawImage raw, ReadOnlyMemory<byte> bytes, int edge, RawLoadOptions rawOptions = default)
    {
        var scene = DevelopScene(raw, bytes, edge, rawOptions);
        return (scene.Image, scene.Tone);
    }

    private static RawSceneData DevelopScene(RawImage raw, ReadOnlyMemory<byte> bytes, int edge, RawLoadOptions rawOptions)
    {
        var list3 = raw.Opcodes.List3;
        // Match against the original embedded corrections first: disabling them must not
        // cause an external profile to replace an embedded match or remove an external fallback.
        var disabled = !rawOptions.EmbeddedLensCorrections
            ? list3.Where(op => op.IsLensCorrection()).ToArray() : [];
        LensProfiles.ApplyFallback(raw, rawOptions.LensDatabasePath);
        if (disabled.Length > 0) list3.RemoveAll(op => disabled.Any(original => ReferenceEquals(original, op)));
        var lens = CameraPreviewLens.LensGeometryOf(list3);
        var vignette = EmbeddedVignette.FromList3(list3, raw.ActiveArea, raw.Crop, raw.Orientation);
        var (xy, transform, look) = CameraPreview.StartingColour(raw, bytes, lens, vignette);
        var whiteBalance = RawWhiteBalance.Create(raw, xy, transform, look);
        var factor = BinFactor(raw, edge);
        var binned = factor is { } k ? raw.DevelopBinnedMasked(k, HighlightClip) : null;
        var image = binned?.Image ?? RawSourceGeometry.Develop(raw, edge <= 600 ? DemosaicMethod.Bilinear : DemosaicMethod.Ahd);
        var geometry = RawSourceGeometry.Create(raw, binned is not null ? factor ?? 1 : 1);
        raw.Data = new RawData.U16([]);
        var matrix = look is null ? transform.Matrix : look.Matrix.Mul(transform.Matrix);
        var white = ClippedWhite(matrix);
        if (binned is { } block)
            Highlight.ReconstructMasked(image, transform.Wb, HighlightClip, white, block.Mask);
        else
            Highlight.ReconstructWith(image, transform.Wb, HighlightClip, white);
        var tables = ProfileTables.Create(raw.Color.Profile, CameraColor.IlluminantWeight(raw.Color, xy));
        var hueSat = look?.HueSat is { } hsv ? HueSat.Create(hsv) : null;
        var gain = MathF.Pow(2, (float)transform.BaselineExposure);
        ApplyColour(image, transform.Wb, matrix, gain, tables, hueSat);
        if (Math.Max(image.Width, image.Height) > edge)
            image = Resample.Fit(image, edge, edge, ResampleFilter.Box);
        image = image.Oriented(raw.Orientation);
        CameraLocalTone.Apply(image, raw, rawOptions.CameraLocalTone, geometry);
        var tone = look?.Tone ?? DngTone(raw.Color.Profile.ToneCurve);
        return new RawSceneData(image, tone, whiteBalance, geometry);
    }

    private static void ApplyColour(RgbImage image, float[] wb, Mat3 matrix, float gain, ProfileTables? tables, HueSat? hueSat)
    {
        var m = matrix.ToF32();
        var data = image.Data;
        Parallel.For(0, image.Height, y =>
        {
            var end = (y + 1) * image.Width * 3;
            for (var i = y * image.Width * 3; i < end; i += 3)
            {
                var c0 = data[i] * wb[0];
                var c1 = data[i + 1] * wb[1];
                var c2 = data[i + 2] * wb[2];
                var r = m[0] * c0 + m[1] * c1 + m[2] * c2;
                var g = m[3] * c0 + m[4] * c1 + m[5] * c2;
                var b = m[6] * c0 + m[7] * c1 + m[8] * c2;
                if (tables is not null) (r, g, b) = tables.Apply(r, g, b, gain);
                else { r *= gain; g *= gain; b *= gain; }
                if (hueSat is not null) (r, g, b) = hueSat.Apply(r, g, b);
                data[i] = float.IsFinite(r) && r > 0 ? r : 0;
                data[i + 1] = float.IsFinite(g) && g > 0 ? g : 0;
                data[i + 2] = float.IsFinite(b) && b > 0 ? b : 0;
            }
        });
    }

    private static CameraTone? DngTone(ToneCurve? curve)
    {
        if (curve is null) return null;
        var knots = new float[64];
        for (var i = 0; i < 32; i++)
        {
            var x = MathF.Pow(2, -12f + 12f * i / 31f);
            knots[2 * i] = x;
            knots[2 * i + 1] = MathF.Min(curve.Eval(x), 0.9995f);
        }
        return CameraTone.Create(knots);
    }

    internal static int? BinFactor(RawImage raw, int edge)
    {
        var crop = raw.Crop.Clipped(raw.ActiveArea.Width, raw.ActiveArea.Height);
        var longest = crop.Width > 1 && crop.Height > 1
            ? Math.Max(crop.Width, crop.Height) : Math.Max(raw.ActiveArea.Width, raw.ActiveArea.Height);
        var need = Math.Max(1, (long)edge * 9 / 10);
        var need3 = Math.Max(1, (long)edge * 3 / 4);
        ReadOnlySpan<int> factors = [8, 6, 4, 3, 2];
        foreach (var k in factors)
            if (raw.CanBin(k) && (longest / k >= need || k == 3 && !raw.CanBin(2) && longest / k >= need3)) return k;
        return null;
    }

    internal static float[] ClippedWhite(Mat3 model)
    {
        float[] camera = [1, 1, 1];
        if (model.Inverse() is not { } inverse) return camera;
        var condition = Norm(model) * Norm(inverse);
        if (!double.IsFinite(condition) || condition > 1e3) return camera;
        var n = inverse.Apply(1, 1, 1);
        var target = Luma(model.Apply(1, 1, 1));
        var own = Luma(model.Apply(n));
        if (!(target > 0 && own > 0 && double.IsFinite(target) && double.IsFinite(own))) return camera;
        var w0 = n.X * target / own;
        var w1 = n.Y * target / own;
        var w2 = n.Z * target / own;
        if (!InRange(w0) || !InRange(w1) || !InRange(w2)) return camera;
        float[] white = [(float)w0, (float)w1, (float)w2];
        var m = model.ToF32();
        var r = m[0] * white[0] + m[1] * white[1] + m[2] * white[2];
        var g = m[3] * white[0] + m[4] * white[1] + m[5] * white[2];
        var b = m[6] * white[0] + m[7] * white[1] + m[8] * white[2];
        var lo = MathF.Min(r, MathF.Min(g, b));
        var hi = MathF.Max(r, MathF.Max(g, b));
        return lo > 0 && float.IsFinite(hi) && hi - lo <= 1e-3f * hi ? white : camera;

        static bool InRange(double value) => double.IsFinite(value) && value >= 0.25 && value <= 4;
        static double Luma((double X, double Y, double Z) v) =>
            v.X * (double)ColorSpaces.Luma2020[0] + v.Y * (double)ColorSpaces.Luma2020[1] + v.Z * (double)ColorSpaces.Luma2020[2];
        static double Norm(Mat3 m)
        {
            var norm = 0.0;
            for (var row = 0; row < 3; row++) norm = Math.Max(norm, Math.Abs(m[row, 0]) + Math.Abs(m[row, 1]) + Math.Abs(m[row, 2]));
            return norm;
        }
    }
}
