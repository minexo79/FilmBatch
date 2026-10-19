using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Mobile.Probes;

// Existing public FilmBatch.Core and FilmBatch.Raw behavior, referenced and executed as-is.
public static class CoreRawProbe
{
    private const int StressWarnPixels = 24_000_000;
    private const string FixtureMake = "FilmBatch";
    private const string FixtureModel = "Generated linear RGB (diagnostic fixture, not a camera Bayer capture)";

    public static ProbeResult Run(ProbeOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timer = Stopwatch.StartNew();
        var metrics = new Dictionary<string, string>();

        var root = Path.Combine(options.OutputDirectory, "core-raw");
        Directory.CreateDirectory(root);

        // Repeated runs keep their own tiny project state; never reset/delete an earlier evidence folder.
        var workspace = Path.Combine(root, "state-" + Guid.NewGuid().ToString("N"));
        var presetPath = Path.Combine(workspace, "probe-presets");
        var photoFolder = Path.Combine(workspace, "probe-photos");
        Directory.CreateDirectory(presetPath);
        Directory.CreateDirectory(photoFolder);

        CheckCore(photoFolder, presetPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        CheckGeneratedDng(root, options.Width, options.Height, metrics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var detail = "Existing Core params/undo/project-reopen and linear fixture write/decode/develop verified.";
        if (options.RawPath is { Length: > 0 } rawPath)
            detail += " " + CheckCameraRaw(rawPath, metrics, cancellationToken);

        timer.Stop();
        metrics["ProbeElapsedMilliseconds"] = timer.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
        metrics["FixtureBoundary"] = "Generated linear RGB DNG; not Bayer/camera color proof.";
        metrics["MemoryBoundary"] = "Managed allocation/process working set describe this run only, not iPad peak RSS.";
        return new("core-raw", "pass", detail, metrics);
    }

    // Existing parameter schemas, the real undo stack, preset storage, and project reopen.
    private static void CheckCore(string photoFolder, string presetPath, CancellationToken cancellationToken)
    {
        const string photo = "probe-photo.tif";
        // Project.Open only admits supported extensions; empty TIFF content is never read.
        File.WriteAllBytes(Path.Combine(photoFolder, photo), []);
        cancellationToken.ThrowIfCancellationRequested();

        var probePreset = new Preset
        {
            Id = Guid.NewGuid(),
            Name = "CoreRaw mobile probe",
            Category = "Probe",
            Settings = new Settings([KeyValuePair.Create("basic.exposure", ParamValue.Of(0.25))]),
        };
        using var library = PresetLibrary.Load(presetPath);
        library.Save(probePreset);
        library.Reload();
        var resolvedBase = library.Get(probePreset.Id)?.Resolved
            ?? throw new InvalidDataException("Saved probe preset was unreadable after reload.");
        if (Math.Abs(resolvedBase.Number("basic.exposure") - 0.25) > 1e-12)
            throw new InvalidDataException("Preset YAML round trip did not preserve basic.exposure.");
        if (library.LoadErrors.Count != 0)
            throw new InvalidDataException("Probe preset folder reported load errors: " + string.Join("; ", library.LoadErrors));

        var project = Project.Open(photoFolder, probePreset.Id);
        var service = new ProjectService(project, library, new CommandStack());
        if (project.FileNames is not [photo])
            throw new InvalidDataException("Project scan did not find exactly the probe photo.");
        if (project[photo].PresetId != probePreset.Id)
            throw new InvalidDataException("Probe photo did not inherit the probe default preset.");

        // Undoable edits, then undo/redo compared against the real stack state.
        if (!service.SetValue([photo], "basic.exposure", 1.5))
            throw new InvalidDataException("SetValue reported no change for basic.exposure.");
        if (!service.SetValue([photo], "grain.amount", 37.0))
            throw new InvalidDataException("SetValue reported no change for grain.amount.");
        if (Math.Abs(service.SettingsFor(photo).Number("basic.exposure") - 1.5) > 1e-12)
            throw new InvalidDataException("Resolved exposure did not include the probe override.");
        service.Stack.Undo();
        if (Math.Abs(service.SettingsFor(photo).Number("grain.amount") - ModuleCatalog.Defaults.Number("grain.amount")) > 1e-12
            || Math.Abs(service.SettingsFor(photo).Number("basic.exposure") - 1.5) > 1e-12)
            throw new InvalidDataException("Undo did not restore only the last override.");
        service.Stack.Redo();
        if (Math.Abs(service.SettingsFor(photo).Number("grain.amount") - 37.0) > 1e-12)
            throw new InvalidDataException("Redo did not reapply the grain override.");
        // Schema clamping is existing ParamDef behavior: basic.exposure range is -3..3 EV.
        if (!service.SetValue([photo], "basic.exposure", 99.0))
            throw new InvalidDataException("Out-of-range SetValue reported no change.");
        if (Math.Abs(service.SettingsFor(photo).Number("basic.exposure") - 3.0) > 1e-12)
            throw new InvalidDataException("Out-of-range exposure did not clamp to the schema maximum.");
        // Assigning another real preset with keepOverrides:false must drop the photo overrides.
        var neutral = new Preset { Id = Guid.NewGuid(), Name = "Neutral probe" };
        library.Save(neutral);
        var neutralBase = library.Get(neutral.Id)?.Resolved ?? throw new InvalidDataException("Neutral preset was not saved.");
        if (!neutralBase.Equals(ModuleCatalog.Defaults))
            throw new InvalidDataException("Empty neutral preset did not resolve to system defaults.");
        if (!service.AssignPreset([photo], neutral.Id, keepOverrides: false))
            throw new InvalidDataException("AssignPreset reported no change.");
        var entry = project[photo];
        if (entry.PresetId != neutral.Id || entry.Overrides.Count != 0)
            throw new InvalidDataException("AssignPreset without keepOverrides did not clear photo overrides.");
        service.Stack.Undo();
        entry = project[photo];
        if (entry.PresetId != probePreset.Id || entry.Overrides.Count == 0)
            throw new InvalidDataException("Undo of preset assignment did not restore the previous preset and overrides.");

        // Project.Open sets autosave once a command-capable service attaches, so every Replace above
        // persisted through SaveCore. Reopen with the real loader and verify the saved state.
        // Existing Core file/project APIs are synchronous; cooperate between calls instead.
        cancellationToken.ThrowIfCancellationRequested();
        const double beforeExposure = 3.0;
        var before = service.SettingsFor(photo);
        using var reopenedLibrary = PresetLibrary.Load(presetPath);
        var reopened = Project.Open(photoFolder, SamplePresets.Neutral);
        _ = new ProjectService(reopened, reopenedLibrary, new CommandStack());
        var reopenedEntry = reopened[photo];
        if (reopenedEntry.PresetId != probePreset.Id)
            throw new InvalidDataException("Reopened project did not preserve the preset assignment.");
        var reopenedSettings = reopenedLibrary.Get(probePreset.Id)?.Resolved.Overlay(reopenedEntry.Overrides)
            ?? throw new InvalidDataException("Reopened project lost the probe preset snapshot.");
        if (Math.Abs(before.Number("basic.exposure") - beforeExposure) > 1e-12
            || Math.Abs(reopenedSettings.Number("basic.exposure") - beforeExposure) > 1e-12
            || Math.Abs(reopenedSettings.Number("grain.amount") - 37.0) > 1e-12)
            throw new InvalidDataException("Reopened project did not preserve saved overrides.");
    }

    // Generated linear RGB fixture: existing writer -> decoder -> develop path, with known values.
    private static void CheckGeneratedDng(string root, int width, int height,
        Dictionary<string, string> metrics, CancellationToken cancellationToken)
    {
        var fixturePixels = FixturePixels(width, height);
        var metadata = new Metadata { Make = FixtureMake, Model = FixtureModel, Software = DngWriter.Software };
        var fixture = DngWriter.WriteRenderedDng(width, height, fixturePixels, ColorSpaces.Srgb, metadata);
        var fixturePath = Path.Combine(root, "generated-linear.dng");
        File.WriteAllBytes(fixturePath, fixture);
        var fixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(fixture));

        var bytes = File.ReadAllBytes(fixturePath);
        if (RawDecoder.Probe(bytes) is not RawFormat.Dng)
            throw new InvalidDataException("Generated DNG fixture was not recognized as DNG.");
        var info = RawDecoder.ProbeInfo(bytes);
        if (info is not { Cpp: 3, Format: RawFormat.Dng })
            throw new InvalidDataException($"Generated fixture decoded as {info.Format}/{info.Cpp}spp, expected DNG/3spp.");
        if (info.Width != width || info.Height != height)
            throw new InvalidDataException($"Fixture geometry {info.Width}x{info.Height}, expected {width}x{height}.");
        if (info.ActiveArea is not { X: 0, Y: 0 } || info.ActiveArea.Width != width || info.ActiveArea.Height != height)
            throw new InvalidDataException("Fixture active area did not cover the generated frame.");
        if (info.Metadata.Make != FixtureMake || info.Metadata.Model != FixtureModel)
            throw new InvalidDataException("Fixture make/model metadata did not round trip.");
        if (info.Color.AsShotNeutral is not [1.0, 1.0, 1.0])
            throw new InvalidDataException("Generated fixture lost its neutral AsShotNeutral.");
        var developed = info.DevelopedSize();
        if (developed != (width, height))
            throw new InvalidDataException($"Fixture developed size {developed}, expected {width}x{height}.");
        cancellationToken.ThrowIfCancellationRequested();

        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var beforeWorkingSet = Environment.WorkingSet;
        var decodeTimer = Stopwatch.StartNew();
        var decoded = RawDecoder.Decode(bytes);
        decodeTimer.Stop();
        if (decoded.Data is not RawData.F32 floats || floats.Samples.Length != (long)width * height * 3)
            throw new InvalidDataException("Generated fixture did not decode to float RGB samples.");
        cancellationToken.ThrowIfCancellationRequested();

        // IEEE float DNG round trip keeps mid-scale finite values (write clamps negatives/invalid to 0).
        const double tolerance = 2e-3;
        var mismatches = 0;
        for (var i = 0; i < floats.Samples.Length; i++)
        {
            var expected = ExpectedFloat(fixturePixels[i]);
            var actual = floats.Samples[i];
            if (!float.IsFinite(actual)) throw new InvalidDataException($"Fixture sample {i} decoded non-finite.");
            if (MathF.Abs(actual - expected) > tolerance) mismatches++;
        }
        var checkedSamples = floats.Samples.Length;
        if (mismatches != 0)
            throw new InvalidDataException($"{mismatches}/{checkedSamples} fixture samples exceeded tolerance {tolerance}.");

        var developedImage = decoded.Develop(DemosaicMethod.Ahd);
        ValidateRgb(developedImage, width, height, tolerance);
        cancellationToken.ThrowIfCancellationRequested();
        var allocatedBytes = checked((long)(GC.GetAllocatedBytesForCurrentThread() - beforeBytes));
        var workingSetDelta = Environment.WorkingSet - beforeWorkingSet;

        metrics["GeneratedFixture"] = Path.GetFullPath(fixturePath);
        metrics["GeneratedFixtureSha256"] = fixtureSha256;
        metrics["GeneratedFixtureKind"] = "Linear RGB DNG fixture (write/render API), not camera Bayer RAW.";
        metrics["FixturePixels"] = ((long)width * height).ToString(CultureInfo.InvariantCulture);
        metrics["FixtureSamples"] = ((long)width * height * 3).ToString(CultureInfo.InvariantCulture);
        metrics["FixtureBytes"] = ((long)fixture.Length).ToString(CultureInfo.InvariantCulture);
        metrics["FixtureSamplesChecked"] = checkedSamples.ToString(CultureInfo.InvariantCulture);
        metrics["FixtureSampleTolerance"] = tolerance.ToString("G", CultureInfo.InvariantCulture);
        metrics["FixtureDecodeMilliseconds"] = decodeTimer.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
        metrics["FixtureDecodeDevelopCallingThreadAllocatedBytes"] = allocatedBytes.ToString(CultureInfo.InvariantCulture);
        metrics["FixtureDecodeDevelopWorkingSetDeltaBytes"] = workingSetDelta.ToString(CultureInfo.InvariantCulture);
        metrics["FixtureDecodeDevelopWorkingSetBytes"] = Environment.WorkingSet.ToString(CultureInfo.InvariantCulture);
        metrics["FixturePixelBufferBytes"] = ((long)width * height * 3 * sizeof(float)).ToString(CultureInfo.InvariantCulture);
        if ((long)width * height >= StressWarnPixels)
            metrics["StressBoundary"] = $"Large {width}x{height} fixture exercises writer/decoder/develop buffers, not iPad limits.";
    }

    // Optional user-selected camera RAW: existing decoder/develop path, source file stays immutable.
    private static string CheckCameraRaw(string rawPath, Dictionary<string, string> metrics, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(rawPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("RAW input was not found.", fullPath);
        using (var before = File.OpenRead(fullPath))
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(before));
            metrics["CameraRawPath"] = fullPath;
            metrics["CameraRawSha256Before"] = hash;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var fileBytes = File.ReadAllBytes(fullPath);
        cancellationToken.ThrowIfCancellationRequested();
        var actualFormat = RawDecoder.Probe(fileBytes) ?? throw new RawInputException(
            $"Probe did not recognize {Path.GetFileName(fullPath)} as supported RAW.");
        if (!RawDecoder.IsSupported(actualFormat))
            throw new RawInputException($"{actualFormat} support is not in this RawDecoder build.");
        var info = RawDecoder.ProbeInfo(fileBytes);
        if (info.Width <= 0 || info.Height <= 0)
            throw new RawInputException("Camera RAW header reported non-positive geometry.");
        var raw = RawDecoder.Decode(fileBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (raw.Format != actualFormat || raw.Width != info.Width || raw.Height != info.Height)
            throw new RawInputException("Full decode disagreed with header probe on format/geometry.");
        if ((long)raw.Width * raw.Height * raw.Cpp != raw.Data.Length)
            throw new RawInputException("Camera RAW decoded sample count disagreed with geometry.");
        raw.Validate();
        metrics["CameraRawFormat"] = actualFormat.ToString();

        // Development consumes Normalized() and one Bayer/linear path; full-image mechanics stay bounded.
        var timer = Stopwatch.StartNew();
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var developed = raw.Develop(DemosaicMethod.Bilinear);
        var allocatedBytes = checked((long)(GC.GetAllocatedBytesForCurrentThread() - beforeBytes));
        timer.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        var expectedDeveloped = raw.Info().DevelopedSize();
        if (developed.Width != expectedDeveloped.Width || developed.Height != expectedDeveloped.Height)
            throw new RawInputException($"Developed {developed.Width}x{developed.Height}, expected {expectedDeveloped}.");
        var sampled = 0;
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        var sampleStride = Math.Max(1, developed.Data.Length / 1_000_000);
        for (var i = 0; i < developed.Data.Length; i += sampleStride)
        {
            var value = developed.Data[i];
            if (!float.IsFinite(value))
                throw new RawInputException("Camera RAW development produced a non-finite sample.");
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            sampled++;
        }

        using (var after = File.OpenRead(fullPath))
        {
            var afterHash = Convert.ToHexStringLower(SHA256.HashData(after));
            metrics["CameraRawSha256After"] = afterHash;
            if (afterHash != metrics["CameraRawSha256Before"])
                throw new InvalidDataException("Camera RAW file changed during the probe.");
        }

        metrics["CameraRawGeometry"] = $"{info.Width}x{info.Height}x{raw.Cpp}";
        metrics["CameraRawBits"] = info.Bits.ToString(CultureInfo.InvariantCulture);
        metrics["CameraRawCfa"] = raw.Cfa?.Name ?? (raw.Cpp == 3 ? "linear-rgb" : "monochrome");
        metrics["CameraRawDeveloped"] = $"{developed.Width}x{developed.Height}";
        metrics["CameraRawSampleStride"] = sampleStride.ToString(CultureInfo.InvariantCulture);
        metrics["CameraRawFiniteSamplesChecked"] = sampled.ToString(CultureInfo.InvariantCulture);
        metrics["CameraRawDevelopedMin"] = minimum.ToString("R", CultureInfo.InvariantCulture);
        metrics["CameraRawDevelopedMax"] = maximum.ToString("R", CultureInfo.InvariantCulture);
        metrics["CameraRawDevelopMillisecondsBilinear"] = timer.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
        metrics["CameraRawDevelopCallingThreadAllocatedBytes"] = allocatedBytes.ToString(CultureInfo.InvariantCulture);
        metrics["CameraRawDevelopPixelBufferBytes"] = ((long)developed.Width * developed.Height * 3 * sizeof(float)).ToString(CultureInfo.InvariantCulture);
        metrics["CameraRawBoundary"] = "Bilinear development of this input only; not per-camera/quality proof.";
        return $"Camera RAW {actualFormat} ({info.Width}x{info.Height}, {metrics["CameraRawCfa"]}) decoded/developed; source SHA-256 unchanged.";
    }

    private static float[] FixturePixels(int width, int height)
    {
        var pixels = new float[checked((long)width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Independent ramps: red varies with x, green with y, blue with x+y. Mid-scale avoids
                // writer clamp edges except at the explicit black corner, which stays exactly 0.
                var i = (int)(((long)y * width + x) * 3);
                pixels[i] = (float)x / Math.Max(1, width - 1);
                pixels[i + 1] = (float)y / Math.Max(1, height - 1);
                pixels[i + 2] = (float)(x + y) / Math.Max(1, width + height - 2);
            }
        }
        return pixels;
    }

    private static float ExpectedFloat(float written) => float.IsFinite(written) ? Math.Clamp(written, 0f, 65504f) : 0f;

    private static void ValidateRgb(RgbImage image, int width, int height, double tolerance)
    {
        if (image.Width != width || image.Height != height)
            throw new InvalidDataException($"Developed fixture geometry {image.Width}x{image.Height}, expected {width}x{height}.");
        if (image.Data.Length != (long)width * height * 3)
            throw new InvalidDataException("Developed fixture data length disagreed with geometry.");
        for (var i = 0; i < image.Data.Length; i++)
        {
            if (!float.IsFinite(image.Data[i]))
                throw new InvalidDataException($"Developed fixture sample {i} was non-finite.");
        }
        // Linear fixture development clones normalized RGB, so corners must match the written ramps.
        var corners = new (int X, int Y, float R, float G, float B)[]
        {
            (0, 0, 0f, 0f, 0f),
            (width - 1, 0, width > 1 ? 1f : 0f, 0f, (float)(width - 1) / Math.Max(1, width + height - 2)),
            (0, height - 1, 0f, height > 1 ? 1f : 0f, (float)(height - 1) / Math.Max(1, width + height - 2)),
            (width - 1, height - 1, width > 1 ? 1f : 0f, height > 1 ? 1f : 0f, width + height > 2 ? 1f : 0f),
        };
        foreach (var (x, y, r, g, b) in corners)
        {
            var i = (int)(((long)y * width + x) * 3);
            if (MathF.Abs(image.Data[i] - r) > tolerance
                || MathF.Abs(image.Data[i + 1] - g) > tolerance
                || MathF.Abs(image.Data[i + 2] - b) > tolerance)
                throw new InvalidDataException($"Developed fixture corner ({x},{y}) differed from the written ramp.");
        }
    }

    // Unsupported/corrupt user input is honest failure evidence, with type and message preserved.
    private sealed class RawInputException(string message) : IOException(message);
}
