// 移植自 LightCraft crates/engine/src/camera_preview.rs（with_starting_colour／starting_colour_tags／
// dng_colour／dng_tone_curve）與 files.rs（WbScaleOf／DngWhite）：DNG 轉存前的起始色彩合成。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Opcodes;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 把 RAW 開啟時的起始色彩（<see cref="CameraPreview.StartingColour"/>：檔內矩陣／自家 JPEG 擬合／
/// 實測光譜矩陣／中性 fallback）合成為 DNG 色彩標籤（camera_preview 的 <c>with_starting_colour</c>；
/// 上游 issue #1745：無矩陣 NEF 轉出的 DNG 缺 ColorMatrix1，其他轉換器拒絕開啟）。
/// 供 CLI <c>raw convert</c> 在 <see cref="DngWriter.WriteDng"/> 前呼叫。
/// 鏡頭校正保留於輸出 DNG：只有擬合像開啟時一樣看不見它們，故擬合期間暫時自 List3 移除再復原。
/// </summary>
public static class RawDngConversion
{
    /// <summary>
    /// 檔案已有自有色彩矩陣時不改寫（DNG 保留自身標籤）；否則以
    /// <see cref="StartingColourTags"/> 合成標籤指派 <see cref="RawImage.Color"/>。
    /// </summary>
    public static void WithStartingColour(RawImage raw, ReadOnlyMemory<byte> bytes)
    {
        if (CameraColor.HasMatrix(raw.Color))
            return;
        var list3 = new List<Opcode>(raw.Opcodes.List3);
        var lens = CameraPreviewLens.LensGeometryOf(list3);
        var vignette = EmbeddedVignette.FromList3(list3, raw.ActiveArea, raw.Crop, raw.Orientation);
        raw.Opcodes.List3.RemoveAll(op => op.IsLensCorrection());
        try
        {
            var tags = StartingColourTags(raw, bytes, lens, vignette);
            if (tags is not null)
                raw.Color = tags;
        }
        finally
        {
            raw.Opcodes.List3.Clear();
            raw.Opcodes.List3.AddRange(list3);
        }
    }

    /// <summary>
    /// RAW 起始色彩的 DNG 標籤（camera_preview 的 <c>starting_colour_tags</c>）；檔案自有矩陣時為 null。
    /// 宣告白點在 <see cref="CameraPreview.StartingColour"/> 可能補入實測矩陣之前先決定（與上游順序一致）。
    /// </summary>
    internal static ColorData? StartingColourTags(RawImage raw, ReadOnlyMemory<byte> bytes,
        CameraPreviewLens? lens = null, EmbeddedVignette? vignette = null)
    {
        if (CameraColor.HasMatrix(raw.Color))
            return null;
        var scale = WbScale.Of(raw.Format, raw.Color, raw.WbMultipliers, raw.Metadata);
        var white = scale.Declared(raw.Color, CameraColor.AsShotWhiteXy(raw));
        var (_, t, look) = CameraPreview.StartingColour(raw, bytes, lens, vignette);
        // 無 JPEG 擬合且無實測模型時，保留缺少描述的狀態，不把中性 fallback 宣稱為相機校準。
        if (t.MatrixIsFallback && look is null)
            return null;
        return DngColour(raw.Color, white, t, look);
    }

    /// <summary>
    /// RAW 檔的白平衡刻度（files.rs 的 <c>WbScaleOf</c>，取 DNG 宣告所需部分）：
    /// 自有矩陣的檔案為絕對刻度；無矩陣但可擬合外觀的格式經實測矩陣或白平衡預設取得 Kelvin 刻度；
    /// 其餘為相對於 as-shot 外觀的刻度（6500 K / 0 = as shot）。
    /// </summary>
    private sealed class WbScale
    {
        private readonly bool _relative;
        private readonly Xy? _scaleWhite;
        private readonly Spectral.Camera? _camera;

        private WbScale(bool relative, Xy? scaleWhite, Spectral.Camera? camera)
        {
            _relative = relative;
            _scaleWhite = scaleWhite;
            _camera = camera;
        }

        internal static WbScale Of(RawFormat format, ColorData color, float[]? wbMultipliers, Metadata meta)
        {
            var ownMatrix = CameraColor.HasMatrix(color);
            var fitted = CameraPreview.FileLocalLook(format) && !ownMatrix;
            var model = fitted ? meta.Model : null;
            var camera = model is { } m ? Spectral.Find(meta.Make, m) : null;
            var presets = camera is null && model is { } pm && KelvinPresets.Covers(meta.Make, pm);
            var scaleWhite = camera is { } c
                ? Spectral.AsShotWhiteThrough(c, color, wbMultipliers)
                : presets ? KelvinPresets.AsShotWhite(color, wbMultipliers) : null;
            var covered = camera is not null || presets;
            return new WbScale(fitted && !covered, scaleWhite, camera);
        }

        /// <summary>
        /// DNG 宣告的白平衡（files.rs 的 <c>WbScaleOf::dng_white</c>）：實測矩陣給刻度時用其矩陣，
        /// 否則用刻度在 As Shot 顯示的白點（預設讀法；relative 刻度的 6500 K / 0；再否則檔案自身標籤的白點）。
        /// </summary>
        internal DngWhite Declared(ColorData color, Xy own)
        {
            if (_camera is { } camera)
            {
                var c = color.Clone();
                c.Profile = new ProfileLook();
                c.KelvinPresets = null;
                if (camera.Fill(c))
                    return new DngWhite.Matrices(c);
            }
            if (_relative)
                return new DngWhite.At(Cct.TempTintToXy(6500.0, 0.0));
            return new DngWhite.At(_scaleWhite ?? own);
        }
    }

    /// <summary>DNG 宣告的白平衡（files.rs 的 <c>DngWhite</c>）。</summary>
    internal abstract record DngWhite
    {
        /// <summary>含實測矩陣的色彩標籤：DNG 經其讀每個白點，與來源相同。</summary>
        internal sealed record Matrices(ColorData Color) : DngWhite;

        /// <summary>來源刻度在 As Shot 顯示的白點。</summary>
        internal sealed record At(Xy Xy) : DngWhite;
    }

    /// <summary>
    /// 使白平衡相機 RGB 在 as-shot 白點經 <paramref name="t"/>（疊加 <paramref name="look"/>）渲染的
    /// DNG 色彩標籤（camera_preview 的 <c>dng_colour</c>）：一個 forward 矩陣（配與之相符的 color
    /// matrix 與 AsShotNeutral）、look 的 hue/sat 表寫為 ProfileHueSatMap、tone 曲線寫為
    /// ProfileToneCurve；look 對白點亮度做的事放進 BaselineExposure。
    /// 色彩矩陣宣告的是來源顯示的白平衡（<c>WbScaleOf::dng_white</c>），DNG 才能以來源的
    /// As Shot 色溫／色偏開啟。白平衡或矩陣無法給標籤時為 null。
    /// </summary>
    internal static ColorData? DngColour(ColorData tags, DngWhite declared, CameraTransform t, CameraLook? look)
    {
        var m = look is null ? t.Matrix : look.Matrix.Mul(t.Matrix);
        // DNG 模型以單位亮度渲染相機白點：look 對白點亮度的改變進入 baseline exposure。
        var white = m.Apply(1.0, 1.0, 1.0);
        var k = (white.X + white.Y + white.Z) / 3.0;
        if (!(double.IsFinite(k) && k > 1e-6) || !t.Wb.All(w => float.IsFinite(w) && w > 0f))
            return null;
        var mk = Scale(m, 1.0 / k);
        var forward = ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50).Mul(ColorSpaces.Rec2020.ToXyz()).Mul(mk);
        if (!forward.ToArray().All(double.IsFinite) || !double.IsFinite(t.BaselineExposure))
            return null;
        var wb = t.Wb.Select(w => (double)w).ToArray();
        var wbMin = wb.Min();
        var neutral = wb.Select(w => wbMin / w).ToArray();
        ushort[] illuminant;
        Mat3?[] colorMatrix;
        switch (declared)
        {
            case DngWhite.Matrices matrices when CameraColor.HasMatrix(matrices.Color):
                illuminant = matrices.Color.Illuminant;
                colorMatrix = matrices.Color.ColorMatrix;
                break;
            case DngWhite.Matrices:
                if (Declaring(t.WhiteXy, neutral) is not { } dm) return null;
                (illuminant, colorMatrix) = ([(ushort)21, (ushort)0], new Mat3?[] { dm, null });
                break;
            case DngWhite.At at:
                if (Declaring(at.Xy, neutral) is not { } da) return null;
                (illuminant, colorMatrix) = ([(ushort)21, (ushort)0], new Mat3?[] { da, null });
                break;
            default:
                return null;
        }
        foreach (var cm in colorMatrix)
            if (cm is { } mat && !mat.ToArray().All(double.IsFinite))
                return null;
        return new ColorData
        {
            Illuminant = illuminant,
            ColorMatrix = colorMatrix,
            // 第二個 color matrix 才配第二個 forward matrix（DNG 規格）：同一個、在單一白點擬合。
            ForwardMatrix = [forward, colorMatrix[1] is null ? null : forward],
            CameraCalibration = [null, null],
            AnalogBalance = null,
            AsShotNeutral = neutral,
            AsShotWhiteXy = null,
            BaselineExposure = t.BaselineExposure + Math.Log2(k),
            BaselineSharpness = tags.BaselineSharpness,
            KelvinPresets = null,
            Profile = new ProfileLook
            {
                HueSatMap = tags.Profile.HueSatMap.Any(table => table is not null)
                    ? tags.Profile.HueSatMap
                    : [look?.HueSat, null],
                LookTable = tags.Profile.LookTable,
                ToneCurve = tags.Profile.ToneCurve ?? (look is null ? null : DngToneCurve(look.Tone)),
                GainTableMap = tags.Profile.GainTableMap,
            },
        };
    }

    private static Mat3 Scale(in Mat3 m, double s)
    {
        var a = m.ToArray();
        for (var i = 0; i < a.Length; i++) a[i] *= s;
        return Mat3.FromRows(a);
    }

    /// <summary>
    /// 使相機 <paramref name="neutral"/> 在 <paramref name="xy"/> 白點成立的 XYZ → 相機矩陣：
    /// 通用相機模型逐通道縮放（camera_preview 的 <c>declaring</c>）；模型在該點無正 neutral 時為 null。
    /// </summary>
    private static Mat3? Declaring(Xy xy, double[] neutral)
    {
        var g = CameraColor.XyzToCamera(new ColorData(), xy);
        var n = g.Apply(xy.ToXyz());
        if (!double.IsFinite(n.X) || !double.IsFinite(n.Y) || !double.IsFinite(n.Z)
            || n.X <= 1e-9 || n.Y <= 1e-9 || n.Z <= 1e-9)
            return null;
        return Mat3.Diag(neutral[0] / n.X, neutral[1] / n.Y, neutral[2] / n.Z).Mul(g);
    }

    /// <summary>
    /// 相機 tone 曲線寫成 DNG <c>ProfileToneCurve</c>（線性入出、1 = 白；camera_preview 的
    /// <c>dng_tone_curve</c>）：在白點以下 12 級範圍密集取樣——讀取端再取樣成 32 節點的範圍。
    /// </summary>
    private static FilmBatch.Raw.Color.ToneCurve? DngToneCurve(CameraTone tone)
    {
        const int n = 256;
        var flat = new double[2 * (n + 1) + 2];
        for (var i = 0; i <= n; i++)
        {
            var x = MathF.Pow(2, -12f + 12f * i / n);
            flat[2 * i + 2] = x;
            flat[2 * i + 3] = MathF.Min(tone.Apply(x), 1.0f);
        }
        return FilmBatch.Raw.Color.ToneCurve.FromTag(flat);
    }
}
