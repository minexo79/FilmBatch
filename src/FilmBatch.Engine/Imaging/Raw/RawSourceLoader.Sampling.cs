using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class RawSourceLoader
{
    /// <summary>Sensor and geometry snapshot for evaluating only the requested external-profile WB samples.</summary>
    internal sealed class CandidateSnapshot
    {
        private sealed class BoxWeights(int sourceWidth, int sourceHeight, int width, int height)
        {
            internal readonly (int First, float[] Weights)[] X = Resample.Weights(sourceWidth, width, ResampleFilter.Box);
            internal readonly (int First, float[] Weights)[] Y = Resample.Weights(sourceHeight, height, ResampleFilter.Box);
        }

        private readonly RawImage _raw;
        private readonly RgbImage _base;
        private readonly RgbImage? _working;
        private readonly RgbImage? _unclipped;
        private RgbImage _input;
        private readonly byte[]? _clipMask;
        private readonly PixelRect _region;
        private readonly int? _factor;
        private readonly bool _lensDeferred;
        private readonly RawSourceGeometry _geometry;
        private readonly ProfileTables? _tables;
        private readonly GainEvaluator? _localTone;
        private readonly float[]? _toProPhoto;
        private readonly BoxWeights? _fit;
        private readonly BoxWeights[] _resizes;
        private readonly int _fitWidth, _fitHeight, _orientedWidth, _orientedHeight;
        private readonly CancellationToken _token;
        private float[] _matrix = [];
        private float[] _wb = [];
        private float _gain, _weight;

        internal CandidateSnapshot(SourceImage source, CancellationToken token)
        {
            var identity = source.Scene?.SamplingIdentity ?? throw new InvalidDataException("RAW sampling identity is unavailable.");
            var path = source.FilePath ?? throw new InvalidDataException("RAW sampling source path is unavailable.");
            _token = token;
            token.ThrowIfCancellationRequested();
            using var decoded = new DecodeLease(path);
            if (!decoded.Matches(identity)) throw new InvalidDataException("RAW source changed since it was displayed.");
            _raw = decoded.CreateRaw();
            var options = source.RawOptions;
            var directory = options.LensDatabasePath;
            if (!string.IsNullOrWhiteSpace(directory) && !Path.IsPathRooted(directory))
                directory = Path.GetFullPath(directory, Path.GetDirectoryName(Path.GetFullPath(path))!);
            var list3 = _raw.Opcodes.List3;
            var disabled = !options.EmbeddedLensCorrections ? list3.Where(op => op.IsLensCorrection()).ToArray() : [];
            _ = TryApplyLensFallback(_raw, directory);
            if (disabled.Length > 0) list3.RemoveAll(op => disabled.Any(original => ReferenceEquals(original, op)));
            _ = SelectProfile(_raw, options) ?? throw new InvalidDataException("External RAW profile is unavailable.");
            _factor = BinFactor(_raw, identity.Edge);
            var binned = _factor is { } k ? DevelopBinned(_raw, k) : null;
            _base = binned?.Image ?? RawSourceGeometry.Develop(_raw,
                identity.Edge <= 600 ? DemosaicMethod.Bilinear : DemosaicMethod.Ahd);
            _clipMask = binned?.Mask;
            _region = binned?.Region ?? default;
            _lensDeferred = binned?.LensDeferred ?? false;
            _geometry = RawSourceGeometry.Create(_raw, binned is not null ? _factor ?? 1 : 1);
            _raw.Data = new RawData.U16([]);
            _tables = ProfileTables.CreateForSampling(_raw.Color.Profile);

            // Highlight reconstruction depends on the complete sensor neighbourhood. If no
            // channel is clipped it is a no-op, so reuse the sensor image for every candidate.
            var clipped = _clipMask is not null && Array.Exists(_clipMask, value => value != 0);
            if (!clipped) clipped = Array.Exists(_base.Data, value => value >= HighlightClip);
            if (clipped) _working = new RgbImage(_base.Width, _base.Height);
            else _unclipped = ApplyDeferredLens(_base);
            _input = _unclipped ?? _working!;

            var scale = Math.Min(Math.Min(identity.Edge / (double)_base.Width, identity.Edge / (double)_base.Height), 1d);
            _fitWidth = Math.Max((int)Math.Round(_base.Width * scale), 1);
            _fitHeight = Math.Max((int)Math.Round(_base.Height * scale), 1);
            if (_fitWidth != _base.Width || _fitHeight != _base.Height)
                _fit = new BoxWeights(_base.Width, _base.Height, _fitWidth, _fitHeight);
            (_orientedWidth, _orientedHeight) = _raw.Orientation.SwapsAxes()
                ? (_fitHeight, _fitWidth) : (_fitWidth, _fitHeight);
            if (options.CameraLocalTone && _raw.Color.Profile.GainTableMap?.Prepare() is { HasValue: true } localTone)
            {
                _localTone = localTone;
                _toProPhoto = ColorSpaces.ProPhoto.FromXyz().Mul(ColorSpaces.Bradford(ColorSpaces.D65, ColorSpaces.D50))
                    .Mul(ColorSpaces.Rec2020.ToXyz()).ToF32();
            }
            var resizes = new List<BoxWeights>();
            var width = _orientedWidth; var height = _orientedHeight;
            foreach (var (nextWidth, nextHeight) in identity.Resizes)
            {
                if (nextWidth != width || nextHeight != height)
                    resizes.Add(new BoxWeights(width, height, nextWidth, nextHeight));
                width = nextWidth; height = nextHeight;
            }
            _resizes = [.. resizes];
            if (width != source.Width || height != source.Height)
                throw new InvalidDataException("RAW candidate dimensions differ from the displayed source.");
            token.ThrowIfCancellationRequested();
        }

        internal void Prepare(double temperature, double tint)
        {
            _token.ThrowIfCancellationRequested();
            var xy = Cct.TempTintToXy((float)temperature, (float)tint);
            var transform = CameraColor.CameraTransform(_raw, xy);
            _matrix = transform.Matrix.ToF32();
            _wb = transform.Wb;
            _gain = MathF.Pow(2, (float)transform.BaselineExposure);
            _weight = (float)CameraColor.IlluminantWeight(_raw.Color, xy);
            if (_working is { } working)
            {
                Array.Copy(_base.Data, working.Data, _base.Data.Length);
                var white = ClippedWhite(transform.Matrix);
                if (_clipMask is { } mask)
                    Highlight.ReconstructMasked(working, _wb, HighlightClip, white, mask);
                else Highlight.ReconstructWith(working, _wb, HighlightClip, white);
                _input = ApplyDeferredLens(working);
            }
            else _input = _unclipped!;
            _token.ThrowIfCancellationRequested();
        }

        private RgbImage ApplyDeferredLens(RgbImage image) => _lensDeferred
            ? RawSourceGeometry.ApplyLensChainBinned(_raw.Opcodes.List3, image, _region, _factor!.Value,
                _raw.ActiveArea.Width, _raw.ActiveArea.Height)
            : image;

        internal (float R, float G, float B) Sample(int x, int y)
        {
            _token.ThrowIfCancellationRequested();
            return SampleResize(_resizes.Length - 1, x, y);
        }

        private (float R, float G, float B) SampleResize(int stage, int x, int y)
        {
            if (stage < 0) return SampleOriented(x, y);
            var weights = _resizes[stage];
            var (x0, wx) = weights.X[x]; var (y0, wy) = weights.Y[y];
            float r = 0, g = 0, b = 0;
            for (var j = 0; j < wy.Length; j++)
            {
                float rr = 0, gg = 0, bb = 0;
                for (var i = 0; i < wx.Length; i++)
                {
                    var sample = SampleResize(stage - 1, x0 + i, y0 + j);
                    rr += sample.R * wx[i]; gg += sample.G * wx[i]; bb += sample.B * wx[i];
                }
                r += rr * wy[j]; g += gg * wy[j]; b += bb * wy[j];
            }
            return (r, g, b);
        }

        private (float R, float G, float B) SampleOriented(int x, int y)
        {
            var (flip, turns) = _raw.Orientation.ToParts();
            var (sx, sy) = turns switch
            {
                1 => (y, _fitHeight - 1 - x),
                2 => (_fitWidth - 1 - x, _fitHeight - 1 - y),
                3 => (_fitWidth - 1 - y, x),
                _ => (x, y),
            };
            if (flip) sx = _fitWidth - 1 - sx;
            var (r, g, b) = SampleFit(sx, sy);
            if (_localTone is { } local && _toProPhoto is { } m)
            {
                var q0 = m[0] * r + m[1] * g + m[2] * b;
                var q1 = m[3] * r + m[4] * g + m[5] * b;
                var q2 = m[6] * r + m[7] * g + m[8] * b;
                var (ax, ay) = _geometry.Active((x + .5) / _orientedWidth, (y + .5) / _orientedHeight);
                var gain = local.Gain(q0, q1, q2, ax, ay);
                r *= gain; g *= gain; b *= gain;
            }
            return (r, g, b);
        }

        private (float R, float G, float B) SampleFit(int x, int y)
        {
            if (_fit is not { } weights) return SampleColour(x, y);
            var (x0, wx) = weights.X[x]; var (y0, wy) = weights.Y[y];
            float r = 0, g = 0, b = 0;
            for (var j = 0; j < wy.Length; j++)
            {
                float rr = 0, gg = 0, bb = 0;
                for (var i = 0; i < wx.Length; i++)
                {
                    var sample = SampleColour(x0 + i, y0 + j);
                    rr += sample.R * wx[i]; gg += sample.G * wx[i]; bb += sample.B * wx[i];
                }
                r += rr * wy[j]; g += gg * wy[j]; b += bb * wy[j];
            }
            return (r, g, b);
        }

        private (float R, float G, float B) SampleColour(int x, int y)
        {
            var data = _input.Data; var i = (y * _input.Width + x) * 3;
            var c0 = data[i] * _wb[0]; var c1 = data[i + 1] * _wb[1]; var c2 = data[i + 2] * _wb[2];
            var r = _matrix[0] * c0 + _matrix[1] * c1 + _matrix[2] * c2;
            var g = _matrix[3] * c0 + _matrix[4] * c1 + _matrix[5] * c2;
            var b = _matrix[6] * c0 + _matrix[7] * c1 + _matrix[8] * c2;
            if (_tables is not null) (r, g, b) = _tables.Apply(r, g, b, _gain, _weight);
            else { r *= _gain; g *= _gain; b *= _gain; }
            return (float.IsFinite(r) && r > 0 ? r : 0,
                float.IsFinite(g) && g > 0 ? g : 0,
                float.IsFinite(b) && b > 0 ? b : 0);
        }
    }
}
