using FilmBatch.Core;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class RawSourceLoader
{
    // Resolve and validate once for each source job. Never use the embedded look as a fallback.
    private static DcpProfile? SelectProfile(RawImage raw, RawLoadOptions options)
    {
        if (options.CameraProfileKey is not { } key) return null;
        var profile = CameraProfileStore.Resolve(AppPaths.UserCameraProfiles, key, RawProcessingScope.Options.CancellationToken);
        var metadata = raw.Metadata;
        string? model;
        if (raw.Format == RawFormat.Dng) model = metadata.UniqueCameraModel;
        else
        {
            var make = NormalizeModel(metadata.Make);
            var name = NormalizeModel(metadata.Model);
            model = name.Length == 0 ? null : make.Length == 0 || name.StartsWith(make + " ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(make, name, StringComparison.OrdinalIgnoreCase) ? name : make + " " + name;
        }
        var normalized = NormalizeModel(model);
        if (normalized.Length == 0)
            throw new CameraProfileUseException(CameraProfileUseFault.MissingModel, "RAW camera model is missing.");
        if (!string.Equals(normalized, NormalizeModel(profile.UniqueCameraModel), StringComparison.OrdinalIgnoreCase))
            throw new CameraProfileUseException(CameraProfileUseFault.ModelMismatch, "Camera profile model does not match the RAW camera.");
        if (raw.Format != RawFormat.Dng && profile.ProfileEmbedPolicy is 0 or 1)
            throw new CameraProfileUseException(CameraProfileUseFault.Policy, "This camera profile policy does not permit processing a non-DNG RAW.");

        var source = raw.Color;
        var selected = profile.ToColorData(source.ImageBaselineExposure);
        selected.AsShotNeutral = source.AsShotNeutral;
        selected.AsShotWhiteXy = source.AsShotWhiteXy;
        selected.AnalogBalance = source.AnalogBalance;
        selected.CameraCalibrationSignature = source.CameraCalibrationSignature;
        if (string.Equals(source.CameraCalibrationSignature ?? "", profile.ProfileCalibrationSignature ?? "", StringComparison.Ordinal))
            selected.CameraCalibration = [.. source.CameraCalibration];
        selected.BaselineSharpness = source.BaselineSharpness;
        selected.Profile.GainTableMap = source.Profile.GainTableMap;
        raw.Color = selected;
        return profile;
    }

    private static string NormalizeModel(string? value) => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
