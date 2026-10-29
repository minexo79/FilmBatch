// Grain/neutral/Zone survey (W1–W3, Z1–Z2): bounded developed-source sampling,
// forward-fit WB solving against the current RawWhiteBalance model, and Zone arithmetic.
// RGB throughout is developed working-linear sRGB: scene sources convert from linear
// Rec.2020 exactly like CpuPipeline.DecodeLinear; SDR sources decode from 16-bit sRGB.
// No full-image copies: sampling strides at most MaxSamples points per region, applies
// the early linear adjustments (current RAW WB + semantic mask + BasicLinear + accepted
// skin/Zone via PhotoAdjustments.ApplySample) point-wise, and never touches film,
// display, or creative geometry. References are relative to the developed image:
// Scene.Image already includes as-shot WB, camera/look transforms and resampling, so
// the forward fit renders consistency and never claims sensor-domain illumination recovery.
using System.Buffers;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging;

/// <summary>
/// Bounded developed-source analysis for decoded SDR and decoded scene-linear
/// RAW sources. All methods are pure with respect to <paramref name="source"/>:
/// sampling never mutates the source.
/// </summary>
public static class PhotoAnalysis
{
    /// <summary>Maximum sample points per region; deterministic stratification above this.</summary>
    public const int MaxSamples = 4096;

    private const double MinTemperature = 2500;
    private const double MaxTemperature = 9900;
    private const double MinTint = -150;
    private const double MaxTint = 150;

    // Fixed search grids: 2500..9500 step 500 plus the 9900 bound; tint -150..150 step 50.
    private static readonly double[] CoarseTempGrid = BuildCoarseTemps();
    private static readonly double[] CoarseTintGrid = BuildCoarseTints();
    private static readonly Mat3 ToWorking = ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb);
    private static readonly Mat3 ToRec2020 = ColorSpaces.Srgb.ToSpace(ColorSpaces.Rec2020);

    /// <summary>
    /// Samples a normalized source rectangle into developed working-linear sRGB.
    /// <paramref name="applyAdjustments"/> <c>false</c> reports pre-edit working RGB
    /// (scene→working conversion or sRGB decode only); <c>true</c> additionally applies
    /// the current RAW WB matrix, the accepted semantic-mask adjustment (evaluated
    /// directly at sample points, no mask-plane allocation), BasicLinear
    /// exposure/temperature/tint, and accepted skin/Zone via
    /// <c>PhotoAdjustments.ApplySample</c>. Degenerate, non-finite, or fully
    /// out-of-bounds regions yield an unusable zero-valid sample.
    /// </summary>
    public static PhotoSample Sample(SourceImage source, Settings settings, PhotoRegion region, bool applyAdjustments = true)
    {
        var p = ModuleParams.From(source, settings, resolveFilmAsset: false);
        return SampleCore(source, p, region, applyAdjustments);
    }

    /// <summary>
    /// Captures the current adjusted sample as a relative reference chromaticity,
    /// or <c>null</c> when the sample cannot be used.
    /// </summary>
    public static PhotoReference? CaptureReference(SourceImage source, Settings settings, PhotoRegion region)
    {
        var sample = Sample(source, settings, region, applyAdjustments: true);
        return sample.CanUse ? new PhotoReference(sample.Red, sample.Green, sample.Blue) : null;
    }

    /// <summary>
    /// Forward-fits the current <see cref="RawWhiteBalance"/> model on bounded
    /// pre-WB samples. Each candidate runs before semantic, BasicLinear and local
    /// adjustments, and robust statistics run after that complete forward chain.
    /// Manual neutral patches remain valid; automatic scene ambiguity is handled
    /// separately. Kelvin is relative to the current model, not sensor calibration.
    /// </summary>
    public static WhiteBalanceSuggestion? SolveWhiteBalance(
        SourceImage source, Settings settings, PhotoRegion region, PhotoReference? reference = null) =>
        SolveWhiteBalance(source, settings, region, reference, CancellationToken.None);

    internal static WhiteBalanceSuggestion? SolveWhiteBalance(
        SourceImage source, Settings settings, PhotoRegion region, PhotoReference? reference, CancellationToken cancellationToken)
    {
        var wb = source.WhiteBalance;
        var ratios = TargetRatios(reference);
        if (wb is null || ratios is null) return null;
        var p = ModuleParams.From(source, settings, resolveFilmAsset: false);
        var candidate = source.RawOptions.CameraProfileKey is null ? null : new RawSourceLoader.CandidateSnapshot(source, cancellationToken);
        var fit = FitForward(source, p, wb, region, ratios.Value, candidate, cancellationToken);
        if (!double.IsFinite(fit.Residual)) return null;
        var atBoundary = AtBounds(fit.Temp, fit.Tint);
        var confidence = ConfidenceFor(fit.Residual, fit.Dispersion, atBoundary);
        return confidence <= 0 ? null
            : new WhiteBalanceSuggestion(region, fit.Temp, fit.Tint, fit.Residual, confidence, atBoundary);
    }

    /// <summary>
    /// Spatial automatic candidates over the developed source: tiles the uncropped
    /// source into thirds, forward-fits each usable tile, and returns the distinct
    /// confident proposals. Monochromatic, high-variance, or mixed-light sources
    /// may yield zero candidates; confidence is never forced. Empty by design when
    /// no WB model exists.
    /// </summary>
    public static IReadOnlyList<WhiteBalanceSuggestion> AutomaticWhiteBalance(SourceImage source, Settings settings) =>
        AutomaticWhiteBalance(source, settings, CancellationToken.None);

    internal static IReadOnlyList<WhiteBalanceSuggestion> AutomaticWhiteBalance(SourceImage source, Settings settings, CancellationToken cancellationToken)
    {
        var wb = source.WhiteBalance;
        if (wb is null) return [];
        var p = ModuleParams.From(source, settings, resolveFilmAsset: false);
        // A single chromaticity, even at many brightness levels, cannot distinguish
        // colored reflectance from illumination. This restriction is automatic-only.
        var scene = SampleCore(source, p, new(0, 0, 1, 1), applyAdjustments: false);
        if (!scene.CanUse || scene.Dispersion < .03) return [];
        var rawCandidate = source.RawOptions.CameraProfileKey is null ? null : new RawSourceLoader.CandidateSnapshot(source, cancellationToken);
        var results = new List<WhiteBalanceSuggestion>(3);
        foreach (var tile in AutoTiles())
        {
            var fit = FitForward(source, p, wb, tile, (1, 1, 1), rawCandidate, cancellationToken);
            if (!double.IsFinite(fit.Residual)) continue;
            var atBoundary = AtBounds(fit.Temp, fit.Tint);
            // Unlabelled scene content cannot justify calibrated-illuminant confidence.
            var confidence = Math.Min(.45, ConfidenceFor(fit.Residual, fit.Dispersion, atBoundary));
            if (confidence <= 0) continue;
            var candidate = new WhiteBalanceSuggestion(tile, fit.Temp, fit.Tint, fit.Residual, confidence, atBoundary);
            var duplicate = -1;
            for (var i = 0; i < results.Count; i++)
                if (Math.Abs(results[i].Temperature - candidate.Temperature) < 150
                    && Math.Abs(results[i].Tint - candidate.Tint) < 15) { duplicate = i; break; }
            if (duplicate < 0) results.Add(candidate);
            else if (candidate.Confidence > results[duplicate].Confidence) results[duplicate] = candidate;
        }
        return results;
    }

    /// <summary>
    /// Relative gain triplet mapping <paramref name="sample"/> chromaticity onto
    /// <paramref name="reference"/> chromaticity. A common scale fits all channels
    /// into 0.25..4 without changing their ratios; impossible ratios return null.
    /// This says nothing about what the patch depicts.
    /// </summary>
    public static PhotoReference? ReferenceGains(PhotoSample sample, PhotoReference reference)
    {
        if (!sample.CanUse) return null;
        if (!double.IsFinite(reference.Red) || !double.IsFinite(reference.Green) || !double.IsFinite(reference.Blue)) return null;
        if (reference.Red <= 0 || reference.Green <= 0 || reference.Blue <= 0) return null;
        var sampleLuma = Luminance(sample.Red, sample.Green, sample.Blue);
        var refLuma = Luminance(reference.Red, reference.Green, reference.Blue);
        if (!(sampleLuma > 0) || !(refLuma > 0)) return null;
        var red = (reference.Red / refLuma) / (sample.Red / sampleLuma);
        var green = (reference.Green / refLuma) / (sample.Green / sampleLuma);
        var blue = (reference.Blue / refLuma) / (sample.Blue / sampleLuma);
        var minimum = Math.Min(red, Math.Min(green, blue));
        var maximum = Math.Max(red, Math.Max(green, blue));
        if (!(minimum > 0) || !double.IsFinite(maximum)) return null;
        var lower = .25 / minimum;
        var upper = 4 / maximum;
        if (lower > upper) return null;
        var scale = Math.Clamp(1, lower, upper);
        return new PhotoReference(red * scale, green * scale, blue * scale);
    }

    // ---- sampling core ----------------------------------------------------

    private readonly record struct SamplePoint(double Red, double Green, double Blue, int X, int Y);
    private readonly record struct SampleStatistics(
        int Valid, int Rejected, double Red, double Green, double Blue, double Luminance, double Dispersion)
    {
        internal bool CanUse => Valid > 0 && Red > 0 && Green > 0 && Blue > 0 && Luminance > 0;
    }

    private static PhotoSample SampleCore(SourceImage source, ModuleParams p, PhotoRegion region, bool applyAdjustments)
    {
        var points = ArrayPool<SamplePoint>.Shared.Rent(MaxSamples);
        var scratch = ArrayPool<double>.Shared.Rent(MaxSamples * 6);
        try
        {
            var count = ReadSamples(source, region, points, out var rejected, out var clipped);
            Mat3? workingWb = p.RawWhiteBalanceMatrix is { } wb ? RawFinishParams.WorkingWhiteBalance(wb) : null;
            var masks = applyAdjustments ? MaskSelection.For(source, p) : MaskSelection.None;
            var sample = EvaluateSamples(source, p, points, count, rejected, workingWb, masks,
                BasicGains(p), applyAdjustments, scratch);
            return new PhotoSample(region, sample.Valid, sample.Rejected, sample.Red, sample.Green,
                sample.Blue, sample.Luminance, sample.Dispersion, source.Scene is not null, clipped);
        }
        finally
        {
            ArrayPool<SamplePoint>.Shared.Return(points);
            ArrayPool<double>.Shared.Return(scratch);
        }
    }

    private static int ReadSamples(SourceImage source, PhotoRegion region, SamplePoint[] points,
        out int rejected, out bool clipped)
    {
        rejected = 0;
        clipped = false;
        if (!ValidRegion(source, region, out var x0, out var y0, out var x1, out var y1)) return 0;
        var spanW = x1 - x0;
        var spanH = y1 - y0;
        var cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(MaxSamples * (double)spanW / spanH)), 1, Math.Min(spanW, MaxSamples));
        var rows = Math.Min(spanH, Math.Max(1, MaxSamples / cols));
        var data = source.Scene?.Image.Data;
        var rgba = data is null ? source.Rgba16 : null;
        var count = 0;
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < cols; col++)
        {
            var sx = Math.Min(x1 - 1, x0 + (int)((col + .5) * spanW / cols));
            var sy = Math.Min(y1 - 1, y0 + (int)((row + .5) * spanH / rows));
            double r, g, b;
            if (data is not null)
            {
                var i = (sy * source.Width + sx) * 3;
                (r, g, b) = ToWorking.Apply(data[i], data[i + 1], data[i + 2]);
            }
            else
            {
                var i = (sy * source.Width + sx) * 4;
                if (rgba![i + 3] == 0) { rejected++; continue; }
                (r, g, b, _) = SrgbPixelToLinear(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
                clipped |= rgba[i] == 65535 || rgba[i + 1] == 65535 || rgba[i + 2] == 65535;
            }
            if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b)
                || !(Luminance(r, g, b) > 0)) { rejected++; continue; }
            points[count++] = new(r, g, b, sx, sy);
        }
        return count;
    }

    private static (double R, double G, double B) BasicGains(ModuleParams p)
    {
        if (!p.BasicOn) return (1, 1, 1);
        var gain = Math.Pow(2, p.Exposure);
        return (gain * (1 + p.Temperature / 100.0 * .18),
            gain * (1 + p.Tint / 100.0 * .12), gain * (1 - p.Temperature / 100.0 * .18));
    }

    private static SampleStatistics EvaluateSamples(SourceImage source, ModuleParams p, SamplePoint[] points,
        int count, int rejected, Mat3? workingWb, MaskSelection masks,
        (double R, double G, double B) gains, bool applyAdjustments, double[] scratch)
    {
        if (count == 0) return new(0, rejected, 0, 0, 0, 0, 0);
        var reds = scratch.AsSpan(0, count);
        var greens = scratch.AsSpan(count, count);
        var blues = scratch.AsSpan(count * 2, count);
        var chromaR = scratch.AsSpan(count * 3, count);
        var chromaG = scratch.AsSpan(count * 4, count);
        var chromaB = scratch.AsSpan(count * 5, count);
        var valid = 0;
        for (var i = 0; i < count; i++)
        {
            var point = points[i];
            var (r, g, b) = applyAdjustments
                ? ApplyEarlyAdjustments(point.Red, point.Green, point.Blue, point.X, point.Y,
                    source, p, workingWb, masks, gains)
                : (point.Red, point.Green, point.Blue);
            var y = Luminance(r, g, b);
            if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b)
                || !double.IsFinite(y) || !(y > 0)) { rejected++; continue; }
            reds[valid] = r; greens[valid] = g; blues[valid] = b;
            chromaR[valid] = r / y; chromaG[valid] = g / y; chromaB[valid] = b / y;
            valid++;
        }
        if (valid == 0) return new(0, rejected, 0, 0, 0, 0, 0);
        var medR = Median(chromaR[..valid]);
        var medG = Median(chromaG[..valid]);
        var medB = Median(chromaB[..valid]);
        double sumR = 0, sumG = 0, sumB = 0, deviation = 0;
        var kept = 0;
        for (var i = 0; i < valid; i++)
        {
            var y = Luminance(reds[i], greens[i], blues[i]);
            var dev = Math.Abs(reds[i] / y - medR) + Math.Abs(greens[i] / y - medG)
                + Math.Abs(blues[i] / y - medB);
            if (dev > 1.5) { rejected++; continue; }
            sumR += reds[i]; sumG += greens[i]; sumB += blues[i];
            deviation += dev;
            kept++;
        }
        if (kept == 0) return new(0, rejected, 0, 0, 0, 0, 0);
        var red = sumR / kept; var green = sumG / kept; var blue = sumB / kept;
        return new(kept, rejected, red, green, blue, Luminance(red, green, blue), deviation / kept);
    }

    private static bool ValidRegion(SourceImage source, PhotoRegion region, out int x0, out int y0, out int x1, out int y1)
    {
        // Reject degenerate/non-finite areas; clip partially out-of-bounds regions to
        // the source. Fully outside yields an unusable sample rather than an error.
        x0 = y0 = x1 = y1 = 0;
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
            || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)) return false;
        if (region.Width <= 0 || region.Height <= 0) return false;
        x0 = Math.Clamp((int)Math.Floor(region.X * source.Width), 0, source.Width);
        y0 = Math.Clamp((int)Math.Floor(region.Y * source.Height), 0, source.Height);
        x1 = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * source.Width), 0, source.Width);
        y1 = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * source.Height), 0, source.Height);
        return x1 > x0 && y1 > y0;
    }

    private static (double R, double G, double B) ApplyEarlyAdjustments(
        double r, double g, double b, int sx, int sy, SourceImage source, ModuleParams p,
        Mat3? workingWb, MaskSelection masks, (double R, double G, double B) gains)
    {
        // Pipeline order at the early linear stage: current RAW WB matrix, semantic
        // mask, BasicLinear, then accepted skin/Zone. Crop maps output→source, so a
        // normalized source point needs no crop transform here.
        if (workingWb is { } matrix) (r, g, b) = matrix.Apply(r, g, b);
        if (masks.Active)
        {
            var nx = (sx + 0.5) / source.Width;
            var ny = (sy + 0.5) / source.Height;
            (r, g, b) = masks.Adjust(r, g, b, nx, ny);
        }
        if (p.BasicOn) { r *= gains.R; g *= gains.G; b *= gains.B; }
        if (p.SkinOn || p.ZoneOn)
        {
            var nx = (sx + 0.5) / source.Width;
            var ny = (sy + 0.5) / source.Height;
            (r, g, b) = PhotoAdjustments.ApplySample(r, g, b, nx, ny, p);
        }
        return (r, g, b);
    }

    // ---- forward WB fit ---------------------------------------------------

    private static (double R, double G, double B)? TargetRatios(PhotoReference? reference)
    {
        if (reference is null) return (1.0, 1.0, 1.0);
        var r = reference.Value;
        if (!double.IsFinite(r.Red) || !double.IsFinite(r.Green) || !double.IsFinite(r.Blue)) return null;
        if (r.Red <= 0 || r.Green <= 0 || r.Blue <= 0) return null;
        var luma = Luminance(r.Red, r.Green, r.Blue);
        if (!(luma > 0)) return null;
        return (r.Red / luma, r.Green / luma, r.Blue / luma);
    }

    private static (double Temp, double Tint, double Residual, double Dispersion) FitForward(
        SourceImage source, ModuleParams p, RawWhiteBalance wb, PhotoRegion region,
        (double R, double G, double B) ratios, RawSourceLoader.CandidateSnapshot? candidate, CancellationToken cancellationToken)
    {
        var points = ArrayPool<SamplePoint>.Shared.Rent(MaxSamples);
        var scratch = ArrayPool<double>.Shared.Rent(MaxSamples * 6);
        var redeveloped = candidate is null ? null : ArrayPool<SamplePoint>.Shared.Rent(MaxSamples);
        try
        {
            var count = ReadSamples(source, region, points, out var rejected, out _);
            var unedited = EvaluateSamples(source, p, points, count, rejected, null,
                MaskSelection.None, (1, 1, 1), false, scratch);
            if (!unedited.CanUse) return (0, 0, double.PositiveInfinity, 0);
            var masks = MaskSelection.For(source, p);
            var gains = BasicGains(p);
            var bestTemp = 6500.0; var bestTint = 0.0;
            var best = double.PositiveInfinity; var dispersion = 0.0;
            void Consider(double temp, double tint)
            {
                // ModuleParams stores Kelvin/tint as f32; score the actual rendered candidate.
                temp = (float)temp;
                tint = (float)tint;
                cancellationToken.ThrowIfCancellationRequested();
                SampleStatistics sample;
                if (candidate is not null)
                {
                    candidate.Prepare(temp, tint);
                    for (var i = 0; i < count; i++)
                    {
                        var point = points[i];
                        var raw = candidate.Sample(point.X, point.Y);
                        var (r, g, b) = ToWorking.Apply(raw.R, raw.G, raw.B);
                        redeveloped![i] = new(r, g, b, point.X, point.Y);
                    }
                    sample = EvaluateSamples(source, p, redeveloped!, count, rejected, null, masks, gains, true, scratch);
                }
                else
                {
                    var working = ToWorking.Mul(wb.MatrixFor(temp, tint) ?? Mat3.Identity).Mul(ToRec2020);
                    sample = EvaluateSamples(source, p, points, count, rejected, working, masks, gains, true, scratch);
                }
                if (!sample.CanUse) return;
                var dr = sample.Red / sample.Luminance - ratios.R;
                var dg = sample.Green / sample.Luminance - ratios.G;
                var db = sample.Blue / sample.Luminance - ratios.B;
                var residual = Math.Sqrt(dr * dr + dg * dg + db * db);
                if (!(residual < best)) return;
                best = residual; bestTemp = temp; bestTint = tint; dispersion = sample.Dispersion;
            }
            foreach (var temp in CoarseTempGrid)
                foreach (var tint in CoarseTintGrid) Consider(temp, tint);
            var stepT = 250.0; var stepI = 25.0;
            for (var pass = 0; pass < 6; pass++)
            {
                for (var iteration = 0; iteration < 16; iteration++)
                {
                    var centerT = bestTemp; var centerI = bestTint;
                    for (var dt = -1; dt <= 1; dt++)
                        for (var di = -1; di <= 1; di++)
                            Consider(Math.Clamp(centerT + dt * stepT, MinTemperature, MaxTemperature),
                                Math.Clamp(centerI + di * stepI, MinTint, MaxTint));
                    if (centerT == bestTemp && centerI == bestTint) break;
                }
                stepT /= 2.5; stepI /= 2.5;
            }
            return (bestTemp, bestTint, best, dispersion);
        }
        finally
        {
            ArrayPool<SamplePoint>.Shared.Return(points);
            if (redeveloped is not null) ArrayPool<SamplePoint>.Shared.Return(redeveloped);
            ArrayPool<double>.Shared.Return(scratch);
        }
    }

    private static double[] BuildCoarseTemps()
    {
        // Fixed at class load: 2500..9500 step 500 plus the 9900 bound.
        var temps = new List<double>();
        for (var t = MinTemperature; t < MaxTemperature; t += 500) temps.Add(t);
        temps.Add(MaxTemperature);
        return [.. temps];
    }

    private static double[] BuildCoarseTints()
    {
        // Fixed at class load: -150..150 step 50.
        var tints = new List<double>();
        for (var t = MinTint; t <= MaxTint; t += 50) tints.Add(t);
        return [.. tints];
    }

    private static bool AtBounds(double temp, double tint) =>
        temp <= MinTemperature + 1 || temp >= MaxTemperature - 1 || tint <= MinTint + 1 || tint >= MaxTint - 1;

    private static double ConfidenceFor(double residual, double dispersion, bool atBoundary)
    {
        if (!double.IsFinite(residual)) return 0;
        var confidence = 1.0 / (1.0 + residual * 8.0);
        confidence *= 1.0 / (1.0 + dispersion * 2.0);
        if (atBoundary) confidence *= 0.5;
        if (confidence < 0.05) return 0;
        return Math.Clamp(confidence, 0, 1);
    }

    private static IEnumerable<PhotoRegion> AutoTiles()
    {
        // Thirds across the uncropped source: distinct spatial candidates when the
        // scene justifies them; per-tile fits below confidence simply drop out.
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                yield return new PhotoRegion(col / 3.0, row / 3.0, 1 / 3.0, 1 / 3.0);
    }

    // ---- scalar helpers ---------------------------------------------------

    private static double Luminance(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    private static double Median(Span<double> values)
    {
        values.Sort();
        var n = values.Length;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }

    private static (double R, double G, double B, double A) SrgbPixelToLinear(ushort r, ushort g, ushort b, ushort a)
    {
        static double Decode(ushort v)
        {
            var c = v / 65535.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return (Decode(r), Decode(g), Decode(b), a / 65535.0);
    }

    /// <summary>
    /// Bounded per-point semantic-mask evaluation: reuses the file's mask
    /// selection and the geometry Active/Covers/ValueAt chain at sample points,
    /// with the same scalar exposure/temperature/tint/contrast/saturation math as
    /// RawSemanticAdjustments.Apply. Never builds the source-resolution plane.
    /// </summary>
    private sealed class MaskSelection
    {
        internal static readonly MaskSelection None = new();

        private readonly List<SemanticMask>? _masks;
        private readonly RawSourceGeometry? _geometry;
        private readonly float _strength;
        private readonly float _exposure;
        private readonly float _temperature;
        private readonly float _tint;
        private readonly float _contrast;
        private readonly float _saturation;
        private readonly bool _invert;

        private MaskSelection() { }

        private MaskSelection(
            List<SemanticMask> masks, RawSourceGeometry geometry,
            float strength, float exposure, float temperature, float tint,
            float contrast, float saturation, bool invert)
        {
            _masks = masks;
            _geometry = geometry;
            _strength = strength;
            _exposure = exposure;
            _temperature = temperature;
            _tint = tint;
            _contrast = contrast;
            _saturation = saturation;
            _invert = invert;
        }

        internal bool Active => _masks is { Count: > 0 } && _geometry is not null;

        internal static MaskSelection For(SourceImage source, ModuleParams p)
        {
            if (!p.RawProcessingOn || !p.RawMaskOn || source.Scene is null || source.SemanticMasks.Count == 0)
                return None;
            var strength = Math.Clamp(p.RawMaskStrength, 0f, 1f);
            var temp = p.RawMaskTemperature / 100f;
            var tint = p.RawMaskTint / 100f;
            var con = p.RawMaskContrast / 100f;
            var sat = p.RawMaskSaturation / 100f;
            var exp = p.RawMaskExposure;
            // Same identity fast path as RawSemanticAdjustments.Apply: no adjustment,
            // no mask evaluation (and no false "unsupported mask" signal).
            if (strength <= 0f || (exp == 0f && temp == 0f && tint == 0f && con == 0f && sat == 0f))
                return None;
            var selected = RawSemanticAdjustments.SelectMasks(source.SemanticMasks, p.RawMaskName, p.RawMaskInstance);
            if (selected.Count == 0) return None;
            return new MaskSelection(selected, source.Scene.Geometry, strength, exp, temp, tint, con, sat, p.RawMaskInvert);
        }

        internal (double R, double G, double B) Adjust(double r, double g, double b, double nx, double ny)
        {
            var geometry = _geometry!;
            var masks = _masks!;
            // Evaluate path mirrors RawSemanticAdjustments.Evaluate at one point:
            // normalized source → geometry Active (oriented/cropped/warped active
            // coordinates) → per-mask ValueAt, max-combined. Outside coverage → 0.
            double mask = 0;
            if (geometry.Covers(nx, ny))
            {
                var (ax, ay) = geometry.Active(nx, ny);
                foreach (var m in masks)
                {
                    var (fw, fh) = m.SubArea is { } area ? ((double)area.FullWidth, (double)area.FullHeight)
                        : ((double)m.Width, (double)m.Height);
                    var v = m.ValueAt(ax * fw, ay * fh);
                    if (v > mask) mask = v;
                }
            }
            var w = (_invert ? 1.0 - mask : mask) * _strength;
            if (w <= 0) return (r, g, b);
            // Scalar math mirrors RawSemanticAdjustments.AdjustPixel/WGSL:
            // exposure → luminance-preserving temp/tint → log-luminance contrast →
            // luminance-mix saturation. Alpha is handled by the sampler (transparent
            // points never reach adjustment).
            var e = _exposure * w;
            if (e != 0)
            {
                var gain = Math.Pow(2.0, e);
                r *= gain; g *= gain; b *= gain;
            }
            var t = (double)_temperature * w;
            var ti = (double)_tint * w;
            if (t != 0 || ti != 0)
            {
                var y0 = Luminance(r, g, b);
                r *= 1 + 0.3 * t;
                g *= 1 - 0.22 * ti;
                b *= Math.Max(1 - 0.3 * t, 0);
                var y1 = Math.Max(Luminance(r, g, b), 1e-9);
                // Match the renderer even when an extreme WB maps luminance below zero.
                var k = y0 / y1;
                r *= k; g *= k; b *= k;
            }
            var c = _contrast * w;
            if (c != 0)
            {
                var y = Luminance(r, g, b);
                var l1 = Math.Clamp(Math.Log2(Math.Max(y, 1e-9) / 0.18), -6.0, 4.0);
                var gain = Math.Pow(2.0, 0.14 * c * l1);
                r *= gain; g *= gain; b *= gain;
            }
            var s = _saturation * w;
            if (s != 0)
            {
                var y = Luminance(r, g, b);
                var k = Math.Max(1 + s, 0);
                r = y + (r - y) * k;
                g = y + (g - y) * k;
                b = y + (b - y) * k;
            }
            return (r, g, b);
        }
    }
}

/// <summary>
/// Zone metering and shadow/highlight span fitting (Z1/Z2) on developed
/// working-linear luminance. Anchors are sampled scene values, not display percentages.
/// </summary>
public static class ZonePlacement
{
    /// <summary>
    /// Zone value 5 + log2(Y / 0.18); <c>null</c> for non-positive or non-finite luminance.
    /// </summary>
    public static double? ZoneOf(double luminance) =>
        double.IsFinite(luminance) && luminance > 0 ? 5 + Math.Log2(luminance / 0.18) : null;

    /// <summary>
    /// Exposure delta in EV from the current zone to <paramref name="targetZone"/>;
    /// <c>null</c> when either endpoint is invalid.
    /// </summary>
    public static double? ExposureDelta(double luminance, double targetZone)
    {
        var current = ZoneOf(luminance);
        if (current is null || !double.IsFinite(targetZone)) return null;
        return targetZone - current.Value;
    }

    /// <summary>
    /// Fits the monotonic log-luminance curve through two zone anchors:
    /// log2(Y'/0.18) = Slope·log2(Y/0.18) + Offset, where Offset shifts the anchor
    /// luminances to their target zones. Rejects non-finite, non-positive,
    /// degenerate (equal anchors), and reversed anchors; returns the unconstrained
    /// positive-slope fit so the UI can warn about out-of-range parameters instead
    /// of receiving a silently clamped curve.
    /// </summary>
    public static ZoneCurve? Fit(double shadowLuminance, double highlightLuminance, double shadowZone = 3, double highlightZone = 8)
    {
        if (!double.IsFinite(shadowLuminance) || !double.IsFinite(highlightLuminance)) return null;
        if (!double.IsFinite(shadowZone) || !double.IsFinite(highlightZone)) return null;
        if (shadowLuminance <= 0 || highlightLuminance <= 0) return null;
        if (highlightLuminance <= shadowLuminance) return null;
        if (highlightZone <= shadowZone) return null;
        var logS = Math.Log2(shadowLuminance / 0.18);
        var logH = Math.Log2(highlightLuminance / 0.18);
        if (!double.IsFinite(logS) || !double.IsFinite(logH)) return null;
        var span = logH - logS;
        if (!(span > 0)) return null;
        // Target log values: zone Z means log2(Y'/0.18) = Z - 5.
        var slope = (highlightZone - shadowZone) / span;
        if (!(slope > 0) || !double.IsFinite(slope)) return null;
        var offset = (shadowZone - 5) - slope * logS;
        if (!double.IsFinite(offset)) return null;
        return new ZoneCurve(slope, offset);
    }
}
