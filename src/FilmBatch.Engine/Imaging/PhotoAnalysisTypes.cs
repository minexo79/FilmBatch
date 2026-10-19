// Grain/neutral/Zone survey (W1–W3, Z1–Z2): public analysis-domain types.
// Sampling reports developed working-linear sRGB, never sensor data or display output.
namespace FilmBatch.Engine.Imaging;

/// <summary>
/// Normalized source rectangle (0..1 in developed-source coordinates, before any
/// creative geometry). Degenerate or non-finite areas are rejected by sampling;
/// the App click tool expands a click into a small positive patch.
/// </summary>
public readonly record struct PhotoRegion(double X, double Y, double Width, double Height);

/// <summary>
/// Robust sample of a source region in developed working-linear sRGB. Scene HDR
/// values above 1 stay valid (Zone metering); black and non-finite pixels are
/// rejected. <see cref="HasProcessedClipping"/> is a processed-image proxy only:
/// no sensor saturation mask exists at this boundary, so unknown sensor clipping
/// stays unknown rather than being inferred.
/// </summary>
public sealed record PhotoSample(
    PhotoRegion Region,
    int ValidSamples,
    int RejectedSamples,
    double Red,
    double Green,
    double Blue,
    double Luminance,
    double Dispersion,
    bool IsSceneLinear,
    bool HasProcessedClipping)
{
    /// <summary>
    /// Usable for neutral/reference/Zone decisions: at least one valid sample and
    /// strictly positive finite channels and luminance (black rejected).
    /// </summary>
    public bool CanUse =>
        ValidSamples > 0
        && double.IsFinite(Red) && double.IsFinite(Green) && double.IsFinite(Blue)
        && double.IsFinite(Luminance)
        && Red > 0 && Green > 0 && Blue > 0 && Luminance > 0;
}

/// <summary>
/// Relative developed-working-linear chromaticity captured from a reference patch.
/// Gains derived from it are relative to the developed image, not a sensor-domain
/// or spectrally calibrated illumination recovery.
/// </summary>
public readonly record struct PhotoReference(double Red, double Green, double Blue);

/// <summary>
/// Forward-fit white-balance proposal in the current WB model's Kelvin/tint domain.
/// Residual is the post-fit relative chromaticity error; confidence degrades near
/// search bounds and on ambiguous (monochrome, high-variance, mixed-light) data.
/// </summary>
public sealed record WhiteBalanceSuggestion(
    PhotoRegion Region,
    double Temperature,
    double Tint,
    double Residual,
    double Confidence,
    bool AtBoundary);

/// <summary>
/// Unconstrained monotonic log-luminance fit: log2(Y'/0.18) = Slope·log2(Y/0.18) + Offset.
/// Slope is always positive; the UI warns (never silently clamps) when the fit
/// falls outside the bounded schema range.
/// </summary>
public readonly record struct ZoneCurve(double Slope, double Offset);
