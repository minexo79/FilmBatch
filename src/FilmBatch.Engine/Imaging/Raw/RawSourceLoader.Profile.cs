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
        var profileModel = NormalizeModel(profile.UniqueCameraModel);
        bool matches;
        if (raw.Format == RawFormat.Dng)
        {
            var model = NormalizeModel(metadata.UniqueCameraModel);
            if (model.Length == 0)
                throw new CameraProfileUseException(CameraProfileUseFault.MissingModel, "RAW camera model is missing.");
            matches = string.Equals(model, profileModel, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var make = NormalizeModel(metadata.Make);
            var model = NormalizeModel(metadata.Model);
            if (model.Length == 0)
                throw new CameraProfileUseException(CameraProfileUseFault.MissingModel, "RAW camera model is missing.");
            // EXIF Make can include a legal suffix absent from Model and the DCP identity.
            var separator = make.IndexOf(' ');
            matches = MatchesCamera(profileModel, make, model)
                || (separator > 0 && MatchesCamera(profileModel, make.AsSpan(0, separator), model));
        }
        if (!matches)
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

    private static bool MatchesCamera(string profile, ReadOnlySpan<char> make, string model)
    {
        if (make.IsEmpty || HasMakePrefix(model, make))
            return string.Equals(profile, model, StringComparison.OrdinalIgnoreCase);
        return profile.Length > make.Length && HasMakePrefix(profile, make)
            && profile.AsSpan(make.Length + 1).Equals(model, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMakePrefix(string name, ReadOnlySpan<char> make) =>
        name.AsSpan().StartsWith(make, StringComparison.OrdinalIgnoreCase)
        && (name.Length == make.Length || name[make.Length] == ' ');

    private static string NormalizeModel(string? value) => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
