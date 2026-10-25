using System.Globalization;
using System.Text.RegularExpressions;
using FilmBatch.Raw;
using FilmBatch.Raw.Opcodes;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// Opt-in, bounded Lensfun-format database. No database is distributed; it is read from a user folder
/// or from the copy the user explicitly downloads with <see cref="LensDatabaseStore"/>.
/// </summary>
internal static partial class LensProfiles
{
    internal static void ApplyFallback(RawImage raw, string? directory)
    {
        // Decoders expose only verified manufacturer corrections as executable opcodes.
        if (OpcodeOps.AppliedOps(raw.Opcodes.List3).Any(op => op.IsLensCorrection() && op.IsApplied())) return;
        directory = string.IsNullOrWhiteSpace(directory) ? Environment.GetEnvironmentVariable("FILMBATCH_LENSFUN_DIR") : directory;
        if (string.IsNullOrWhiteSpace(directory)) return;
        var db = Load(directory);
        var m = raw.Metadata;
        if (string.IsNullOrWhiteSpace(m.Make) || string.IsNullOrWhiteSpace(m.Model)
            || string.IsNullOrWhiteSpace(m.LensModel) || m.FocalLength is not { } focal || !Positive(focal)) return;
        var cameras = db.Cameras.Where(c => SameMaker(c.Maker, m.Make) && Name(c.Model) == Name(m.Model)).Distinct().ToArray();
        if (cameras.Length != 1) return;
        var camera = cameras[0];
        var crop = camera.Crop;
        if (m.FocalLength35mm is { } equivalent && Positive(equivalent)) crop = Math.Max(crop, equivalent / focal);
        var candidates = db.Profiles.Where(p => p.Models.Any(n => LensName(n, p.Maker) == LensName(m.LensModel, p.Maker))
            && (string.IsNullOrWhiteSpace(m.LensMake) || SameMaker(p.Maker, m.LensMake))
            && p.Mounts.Any(mount => Name(mount) == Name(camera.Mount)) && p.Crop <= crop * 1.01).ToArray();
        if (candidates.Length == 0) return;
        var distance = candidates.Min(p => Math.Abs(Math.Log(p.Crop / crop)));
        candidates = candidates.Where(p => Math.Abs(Math.Abs(Math.Log(p.Crop / crop)) - distance) < 1e-10).ToArray();
        if (candidates.Length != 1) return; // Ambiguity must never select a different lens by file order.
        if (raw.ActiveArea.Width < 2 || raw.ActiveArea.Height < 2) return;
        var correction = Correction(candidates[0], focal, m.FNumber, crop, raw.ActiveArea.Width, raw.ActiveArea.Height,
            raw.DevelopCrop(raw.ActiveArea.Width, raw.ActiveArea.Height));
        raw.Opcodes.List3.AddRange(correction);
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0 && value < 1e6;
    // Keep numeric tokens separate: 18-55 must not collide with 1-855, nor f/1.4 with f/14.
    private static string Name(string? s) => string.Join(" ", Regex.Matches(s ?? "", @"\p{L}+|\p{N}+(?:\.\p{N}+)?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Cast<Match>().Select(m =>
            double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                ? n.ToString("G17", CultureInfo.InvariantCulture) : m.Value.ToLowerInvariant()));
    private static bool SameMaker(string a, string b)
    {
        var firstA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var firstB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Name(firstA).Length > 0 && Name(firstA) == Name(firstB);
    }
    private static string LensName(string s, string maker)
    {
        var name = Name(s); var prefix = Name(maker);
        return prefix.Length > 0 && name.StartsWith(prefix + " ", StringComparison.Ordinal) ? name[(prefix.Length + 1)..] : name;
    }

    private sealed record Camera(string Maker, string Model, string Mount, double Crop);
    private sealed record Point(double Focal, string Model, double[] K, double Aperture = 0, double Distance = 1000);
    private sealed record Profile(string Maker, string[] Models, string[] Mounts, double Crop, double Aspect,
        bool Rectilinear, Point[] Distortion, Point[] Tca, Point[] Vignette);
    private sealed record Database(List<Camera> Cameras, List<Profile> Profiles);

    private static List<Opcode> Correction(Profile p, double focal, double? aperture, double crop, int width, int height,
        PixelRect frame)
    {
        var result = new List<Opcode>();
        var toVignette = p.Crop / crop;
        var toDistortion = toVignette * Math.Sqrt(1 + p.Aspect * p.Aspect);
        // Lensfun is calibrated in the default-cropped frame. List3 still executes
        // on the full active area: express that centre and polynomial in each opcode's units.
        var cx = frame.X + frame.Width / 2d; var cy = frame.Y + frame.Height / 2d;
        var calibrationRadius = double.Hypot(frame.Width, frame.Height) / 2;
        var warpRadius = double.Hypot(Math.Max(Math.Abs(cx - .5), Math.Abs(width - .5 - cx)),
            Math.Max(Math.Abs(cy - .5), Math.Abs(height - .5 - cy)));
        var radiusScale = warpRadius / calibrationRadius;
        var radiusScale2 = radiusScale * radiusScale;
        if ((p.Rectilinear && p.Distortion.Length > 0) || p.Tca.Length > 0)
        {
            var planes = new double[3][];
            for (var ch = 0; ch < 3; ch++)
            {
                var channel = ch;
                double Ratio(double r)
                {
                    var rl = r * toDistortion;
                    var d = p.Rectilinear ? AtFocal(p.Distortion, focal, d => Distortion(d, rl), 1) : 1;
                    return d * AtFocal(p.Tca, focal, t => Tca(t, channel, rl * d), 1);
                }
                var fit = Fit(Ratio, false);
                if (fit is null) { planes = []; break; }
                var scale = radiusScale2;
                for (var i = 1; i < fit.Length; i++, scale *= radiusScale2) fit[i] *= scale;
                planes[ch] = [fit[0], fit[1], fit[2], fit[3], 0, 0];
            }
            if (planes.Length > 0)
            {
                var scale = FrameScale(planes, frame.Width, frame.Height, warpRadius);
                // Fold the uniform pre-warp scale into r·P(r²): coefficient i gets
                // scale^(2i+1). Keep one spatial sample, no extra full-image pass.
                if (scale < 1)
                    foreach (var plane in planes)
                    {
                        var power = scale;
                        for (var i = 0; i < 4; i++, power *= scale * scale) plane[i] *= power;
                    }
                result.Add(new WarpRectilinear(planes, [(cx - .5) / (width - 1), (cy - .5) / (height - 1)]));
            }
        }
        if (p.Vignette.Length > 0 && aperture is { } a && Positive(a))
        {
            double Gain(double r)
            {
                var rr = r * toVignette;
                var att = AtFocal(p.Vignette.GroupBy(v => v.Focal).Select(g => g.First()).OrderBy(v => v.Focal).ToArray(), focal, v =>
                {
                    var atFocal = p.Vignette.Where(x => x.Focal == v.Focal).ToArray();
                    var distance = atFocal.Max(x => x.Distance); // EXIF has no dependable focus distance: infinity calibration.
                    var points = atFocal.Where(x => x.Distance == distance).OrderBy(x => x.Aperture).ToArray();
                    return Interpolate(points, Math.Log2(a), x => Math.Log2(x.Aperture), x =>
                    {
                        var r2 = rr * rr;
                        return 1 + r2 * (x.K[0] + r2 * (x.K[1] + r2 * x.K[2]));
                    }, 1);
                }, 1);
                return att > .05 && double.IsFinite(att) ? 1 / att : double.NaN;
            }
            // Lensfun attenuation belongs to the sensor: apply it before the warp,
            // so the fused inverse sampler evaluates gain at the warped source position.
            if (Fit(Gain, true) is { } k)
            {
                var radius = double.Hypot(Math.Max(cx, width - cx), Math.Max(cy, height - cy));
                var scale2 = radius * radius / (calibrationRadius * calibrationRadius);
                var scale = scale2;
                for (var i = 0; i < k.Length; i++, scale *= scale2) k[i] *= scale;
                result.Insert(0, new FixVignetteRadial(k, [cx / width, cy / height]));
            }
        }
        return result;
    }

    private static double Distortion(Point p, double r) => p.Model switch
    {
        "poly3" => 1 - p.K[0] + p.K[0] * r * r,
        "poly5" => 1 + r * r * (p.K[0] + r * r * p.K[1]),
        "ptlens" => ((p.K[0] * r + p.K[1]) * r + p.K[2]) * r + 1 - p.K[0] - p.K[1] - p.K[2],
        _ => 1,
    };
    private static double Tca(Point p, int channel, double r)
    {
        if (channel == 1) return 1;
        var i = channel == 0 ? 0 : 1;
        return p.Model == "linear" ? p.K[i] : (p.K[4 + i] * r + p.K[2 + i]) * r + p.K[i];
    }
    private static double AtFocal(Point[] points, double focal, Func<Point, double> eval, double fallback) =>
        Interpolate(points, focal, p => p.Focal, eval, fallback);
    private static double Interpolate(Point[] points, double value, Func<Point, double> key, Func<Point, double> eval, double fallback)
    {
        if (points.Length == 0) return fallback;
        var hi = Array.FindIndex(points, p => key(p) >= value);
        if (hi < 0) return eval(points[^1]);
        if (hi == 0) return eval(points[0]);
        var lo = hi - 1; var a = key(points[lo]); var b = key(points[hi]);
        if (b <= a) return eval(points[hi]);
        var t = (value - a) / (b - a);
        return eval(points[lo]) * (1 - t) + eval(points[hi]) * t;
    }
}
