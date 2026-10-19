// 移植自 LightCraft crates/raw/src/highlight.rs（單元測試）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Tests.Develop;

public sealed class HighlightTests
{
    /// <summary>Daylight-like multipliers: green clips first, red and blue keep rising.</summary>
    private static readonly float[] Wb = [2.5f, 1.0f, 1.6f];

    private static float[] Balanced(float[] p) => [p[0] * Wb[0], p[1] * Wb[1], p[2] * Wb[2]];

    private static float[] Sensor(float[] q) => [MathF.Min(q[0] / Wb[0], 1f), MathF.Min(q[1] / Wb[1], 1f), MathF.Min(q[2] / Wb[2], 1f)];

    private static float[] Chroma(float[] q)
    {
        var s = q[0] + q[1] + q[2];
        return [q[0] / s, q[1] / s, q[2] / s];
    }

    private static RgbImage FromFn(int w, int h, Func<int, int, float[]> f)
    {
        var d = new float[w * h * 3];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var p = f(x, y); var i = (y * w + x) * 3; d[i] = p[0]; d[i + 1] = p[1]; d[i + 2] = p[2]; }
        return new(w, h, d);
    }

    private static RgbImage Filled(int w, int h, float[] v)
    {
        var d = new float[w * h * 3];
        for (var i = 0; i + 2 < d.Length; i += 3) { d[i] = v[0]; d[i + 1] = v[1]; d[i + 2] = v[2]; }
        return new(w, h, d);
    }

    private static RgbImage Clone(RgbImage img) => new(img.Width, img.Height, (float[])img.Data.Clone());

    private static float[] Get(RgbImage img, int x, int y) => [img.Data[(y * img.Width + x) * 3], img.Data[(y * img.Width + x) * 3 + 1], img.Data[(y * img.Width + x) * 3 + 2]];

    private static void Set(RgbImage img, int x, int y, float[] p) { var i = (y * img.Width + x) * 3; img.Data[i] = p[0]; img.Data[i + 1] = p[1]; img.Data[i + 2] = p[2]; }

    /// <summary>
    /// A pale blue sky whose green channel clipped, an unclipped strip of the same sky along the top, and a dark
    /// branch with purple fringes on both sides standing in the clipped part (white-balanced sky colour returned).
    /// </summary>
    private static (RgbImage, float[]) BranchScene()
    {
        var sky = new[] { 1.10f, 1.25f, 1.45f };
        var img = FromFn(192, 128, (x, y) =>
            Sensor(y < 24
                ? [sky[0] * 0.7f, sky[1] * 0.7f, sky[2] * 0.7f]
                : x is >= 90 and < 100 && y >= 40
                    ? [0.06f, 0.05f, 0.03f]
                    : (x is 89 or 100) && y >= 40
                        ? [0.75f, 0.25f, 0.96f]
                        : sky));
        return (img, sky);
    }

    /// <summary>How far chromaticity <paramref name="got"/> lies off the way from <paramref name="from"/> to neutral.</summary>
    private static float OffTheWayToNeutral(float[] got, float[] from)
    {
        var d = new float[3];
        for (var c = 0; c < 3; c++) d[c] = 1f / 3f - from[c];
        var len = 0f;
        foreach (var v in d) len += v * v;
        var t = 0f;
        if (len > 0f) { var num = 0f; for (var c = 0; c < 3; c++) num += (got[c] - from[c]) * d[c]; t = Math.Clamp(num / len, 0f, 1f); }
        var max = 0f;
        for (var c = 0; c < 3; c++) max = Math.Max(max, Math.Abs(got[c] - (from[c] + d[c] * t)));
        return max;
    }

    /// <summary>
    /// Issue #523: the clipped sky beside a branch took the colour of the branch's fringes and of the branch
    /// itself (magenta, green), which any darkening of the highlights then showed. Beside the branch it is now the
    /// clipped sky away from it: the sky's colour, on its way to neutral.
    /// </summary>
    [Fact]
    public void FringesAndDarkBranchesDoNotTintAClippedSky()
    {
        var (img, sky) = BranchScene();
        Assert.True(Highlight.Reconstruct(img, Wb, 0.99f) > 0);
        var worst = 0f;
        var skyChroma = Chroma(sky);
        for (var y = 40; y < 128; y++)
        {
            var away = Chroma(Balanced(Get(img, 30, y)));
            Assert.True(Chroma(Balanced(Get(img, 170, y))).SequenceEqual(away), $"row {y}");
            var off = OffTheWayToNeutral(away, skyChroma);
            Assert.True(off < 0.005f, $"row {y}: the clipped sky is not the sky's colour on its way to neutral ({off})");
            foreach (var x in new[] { 84, 86, 88, 101, 103, 105 })
            {
                var got = Chroma(Balanced(Get(img, x, y)));
                for (var c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(got[c] - away[c]));
            }
        }
        Assert.True(worst < 0.01f, $"clipped sky beside the branch is off the sky away from it by {worst}");
    }

    /// <summary>
    /// A warm light with red and green clipped: left at their clip levels the pair reads red-magenta (the white
    /// balance multiplies red's clip level by 2.5, green's by 1).
    /// </summary>
    [Fact]
    public void TwoClippedChannelsAreNotLeftAtTheirClipLevels()
    {
        var light = new[] { 3.5f, 2.6f, 0.8f };
        var img = FromFn(64, 64, (x, y) =>
        {
            var d = MathF.Sqrt((x - 32f) * (x - 32f) + (y - 32f) * (y - 32f));
            return d < 20f ? Sensor(light) : [0.02f, 0.02f, 0.02f];
        });
        Highlight.Reconstruct(img, Wb, 0.99f);
        var q = Balanced(Get(img, 32, 32));
        Assert.True(Math.Abs(q[2] - 0.8f) < 0.1f, $"the unclipped channel is about kept: [{q[0]}, {q[1]}, {q[2]}]");
        var ratio = q[1] / q[0];
        Assert.True(ratio is >= 0.6f and <= 1.05f, $"green / red {ratio}: not a warm white");
    }

    [Fact]
    public void FullyClippedPixelsFollowTheColourModelWhite()
    {
        var white = new[] { 1.03f, 0.98f, 1.06f };
        var img = Filled(8, 8, [1f, 1f, 1f]);
        Set(img, 0, 0, [0.3f, 0.6f, 0.4f]);
        Highlight.ReconstructWith(img, Wb, 0.99f, white);
        var q = Balanced(Get(img, 4, 4));
        var k = q[1] / white[1];
        for (var c = 0; c < 3; c++) Assert.True(Math.Abs(q[c] - white[c] * k) < 1e-4f * k, $"[{q[0]}, {q[1]}, {q[2]}] is not along white");
        Assert.True(k >= 2.5f * 0.99f - 1e-4f, $"clipped neutral at least as bright as the highest clip level: {k}");
        Assert.True(new[] { 0.3f, 0.6f, 0.4f }.SequenceEqual(Get(img, 0, 0)));
        // a white that cannot be right falls back to camera neutral, and nothing overflows
        foreach (var bad in new[] { new[] { float.NaN, 1f, 1f }, [2.5e38f, 0.74f, 0.74f], [1f, 0f, 1f], [40f, 1f, 1f] })
        {
            img = Filled(4, 4, [1f, 1f, 1f]);
            Set(img, 0, 0, [1f, 1f, 0.5f]);
            Highlight.ReconstructWith(img, Wb, 0.99f, bad);
            q = Balanced(Get(img, 1, 1));
            Assert.True(Math.Abs(q[0] - q[1]) < 1e-4f && Math.Abs(q[2] - q[1]) < 1e-4f, $"bad white: [{q[0]}, {q[1]}, {q[2]}]");
            Assert.All(img.Data, v => Assert.True(float.IsFinite(v)));
        }
    }

    /// <summary>
    /// Red rises through its clip level across a sky whose green and blue clipped: the rebuilt colour changes
    /// smoothly (no edge where red clips too) and arrives at the clipped neutral.
    /// </summary>
    [Fact]
    public void RebuiltColourIsContinuousAsMoreChannelsClip()
    {
        var blue = new[] { 1.0f, 1.3f, 1.9f };
        var img = FromFn(240, 40, (x, y) =>
        {
            if (y < 8) return Sensor([blue[0] * 0.5f, blue[1] * 0.5f, blue[2] * 0.5f]); // unclipped sky: the source of colour
            var level = 0.6f + 2.2f * x / 239f; // red at 0.24 to 1.12 of its clip level
            return Sensor([level * Wb[0] * 0.4f, 1.6f, 2.0f]);
        });
        Highlight.Reconstruct(img, Wb, 0.99f);
        var row = new float[240][];
        for (var x = 0; x < 240; x++) row[x] = Chroma(Balanced(Get(img, x, 30)));
        var step = 0f;
        for (var x = 1; x < 240; x++) for (var c = 0; c < 3; c++) step = Math.Max(step, Math.Abs(row[x][c] - row[x - 1][c]));
        Assert.True(step < 0.01f, $"largest chromaticity step between neighbours {step}");
        var end = row[239];
        Assert.All(end, v => Assert.True(Math.Abs(v - 1f / 3f) < 1e-3f, $"fully clipped end [{end[0]}, {end[1]}, {end[2]}]"));
    }

    /// <summary>
    /// A smooth pink ramp ([0.9k, k, 0.8k], k from 0.8 to 1.35) whose green clips first, then red, then blue,
    /// in <paramref name="rowStart"/>…<paramref name="rowEnd"/> of an <paramref name="h"/>-row picture that is
    /// otherwise near black: the rebuilt middle row, and where green first clips on it.
    /// </summary>
    private static (float[][] Row, int First) RebuiltRamp(int rowStart, int rowEnd, int h)
    {
        var w = 1600;
        const float k0 = 0.8f, k1 = 1.35f;
        float K(int x) => k0 + (k1 - k0) * x / (w - 1);
        var img0 = FromFn(w, h, (x, y) =>
            y >= rowStart && y < rowEnd
                ? [MathF.Min(0.9f * K(x), 1f), MathF.Min(K(x), 1f), MathF.Min(0.8f * K(x), 1f)]
                : [0.01f, 0.01f, 0.01f]);
        var img = Clone(img0);
        Highlight.Reconstruct(img, Wb, 0.99f);
        var y = (rowStart + rowEnd) / 2;
        var row = new float[w][];
        for (var x = 0; x < w; x++) row[x] = Balanced(Get(img, x, y));
        var first = 0;
        for (var x = 0; x < w; x++) if (Get(img0, x, y)[1] >= 0.99f) { first = x; break; }
        return (row, first);
    }

    /// <summary>No channel changes by more than <paramref name="atClip"/> (relative) where green first clips, nor by 3 % anywhere.</summary>
    private static void AssertContinuous(float[][] row, int first, float atClip, string name)
    {
        // across the first clip the colour carries on as it was (the sensor's own colour, not a fade)
        var (a, b) = (row[first - 1], row[first]);
        for (var c = 0; c < 3; c++) Assert.True(Math.Abs(b[c] / a[c] - 1f) < atClip, $"{name}, first clip: [{a[0]}, {a[1]}, {a[2]}] then [{b[0]}, {b[1]}, {b[2]}]");
        var worst = 0f;
        for (var x = 1; x < row.Length; x++) for (var c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(row[x][c] / row[x - 1][c] - 1f));
        // (a smooth ramp moves 0.04 % per pixel here; the rebuilt colour turns faster where it leaves the colour of
        // the unclipped part for neutral, but never by a step)
        Assert.True(worst < 0.03f, $"{name}: largest relative step between neighbours {worst}");
    }

    /// <summary>
    /// The rebuilt colour has no step anywhere along the ramp: not where green first clips (the neutral fade starts
    /// from nothing there), nor where red does, nor where the last channel clips (the fade ends at the fully
    /// clipped neutral).
    /// </summary>
    [Fact]
    public void RebuiltRampIsContinuousFromTheFirstClipToTheLast()
    {
        var (row, first) = RebuiltRamp(0, 32, 32);
        AssertContinuous(row, first, 0.002f, "full ramp");
    }

    /// <summary>
    /// The same ramp as a thin line on near black has no reliable colour around (every pixel of it is on a strong
    /// edge), so its clipped part falls back to the neutral guess. That guess only applies as far as the line runs
    /// past its own unclipped part: no step where it first clips, however thin the line and however much dark
    /// background surrounds it.
    /// </summary>
    [Fact]
    public void ThinLineWithoutReliableColourIsContinuousWhereItClips()
    {
        foreach (var height in new[] { 32, 512, 1024 })
        {
            foreach (var width in new[] { 1, 4, 16 })
            {
                var top = (height - width) / 2;
                var (row, first) = RebuiltRamp(top, top + width, height);
                AssertContinuous(row, first, 0.002f, $"{width} rows of {height}");
                // and well past the first clip the neutral guess has taken over (green as high as red and blue imply)
                var far = row[1500];
                Assert.True(far[1] > 0.95f * far[2], $"{width} rows of {height}: [{far[0]}, {far[1]}, {far[2]}]");
            }
        }
    }

    /// <summary>
    /// Issue #523 (a river photo darkened by Highlights -75): a pale cyan-blue sky ([1, 1.07, 1.2] white-balanced)
    /// brightening from left to right through the point where green clips (and later blue), above bright
    /// yellow-green foliage, with thin dark branches standing in its lower half. Its rebuilt colour used to lean
    /// green to teal: the foliage lent its colour to the sky (green rebuilt from it), with a step where green first
    /// clips, and the sky kept its own pale colour where Lightroom shows white.
    /// </summary>
    [Fact]
    public void PaleSkyByDarkBranchesAndFoliageIsNotTinted()
    {
        var sky = new[] { 1.0f, 1.07f, 1.2f };
        const int w = 320, h = 96;
        float K(int x) => 0.7f * MathF.Pow(2f, 1.2f * x / (w - 1));
        var branches = new[] { 150, 200, 250 };
        bool OnBranch(int x) => branches.Any(b => x >= b - 1 && x <= b + 2);
        var img0 = FromFn(w, h, (x, y) =>
            Sensor(y >= 72
                ? [0.5f, 0.6f, 0.22f]
                : y >= 40 && branches.Any(b => x == b || x == b + 1)
                    ? [0.1f, 0.09f, 0.04f]
                    : [sky[0] * K(x), sky[1] * K(x), sky[2] * K(x)]));
        var img = Clone(img0);
        Highlight.Reconstruct(img, Wb, 0.99f);
        float[] At(int x, int y) => Chroma(Balanced(Get(img, x, y)));
        var first = 0;
        for (var x = 0; x < w; x++) if (Get(img0, x, 20)[1] >= 0.99f) { first = x; break; }
        Assert.True(first > 0 && first < 150);
        float Diff(float[] a, float[] b) { var m = 0f; for (var c = 0; c < 3; c++) m = Math.Max(m, Math.Abs(a[c] - b[c])); return m; }
        var skyChroma = Chroma(sky);
        foreach (var y in new[] { 20, 50 })
        {
            // no step where green clips
            var step = Diff(At(first - 1, y), At(first, y));
            Assert.True(step < 0.001f, $"row {y}: step of {step} where green clips");
            for (var x = first; x < w; x++)
            {
                // nothing but the sky's colour on its way to neutral (no green from the foliage)...
                var off = OffTheWayToNeutral(At(x, y), skyChroma);
                Assert.True(OnBranch(x) || off < 0.005f, $"row {y}, x {x}: off the sky's way to neutral by {off}");
                // ... the same beside the branches as above them ...
                var beside = Diff(At(x, y), At(x, 20));
                Assert.True(OnBranch(x) || beside < 0.005f, $"row {y}, x {x}: beside a branch vs above");
            }
            // ... and neutral well before the last channel clips (the sky is 0.7 stops past green's clip there)
            var end = At(w - 1, y);
            Assert.All(end, v => Assert.True(Math.Abs(v - 1f / 3f) < 0.002f, $"row {y}: [{end[0]}, {end[1]}, {end[2]}] at the end"));
        }
    }

    [Fact]
    public void UnclippedPixelsAreLeftExactly()
    {
        var seed = 0x2545f4914f6cdd1dUL;
        float Rnd() { seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17; return (seed >> 40) / (float)(1UL << 24); }
        foreach (var (w, h) in new[] { (1, 1), (3, 1), (7, 5), (64, 48), (129, 77) })
        {
            var noise = new float[w * h];
            for (var i = 0; i < noise.Length; i++) noise[i] = Rnd();
            var img = FromFn(w, h, (x, y) =>
            {
                var t = (MathF.Sin(x * 0.21f) + MathF.Cos(y * 0.17f)) * 0.5f;
                var b = new[] { 0.5f + 0.4f * t, 0.45f - 0.2f * t, 0.3f + 0.1f * t };
                var r = noise[y * w + x];
                if (r < 0.1f) return [1f, 1f, 1f];
                if (r < 0.4f) return [1f, b[1] * 1.6f, b[2]];
                if (r > 0.995f) return [0f, 0f, 0.00001f];
                return b;
            });
            var @out = Clone(img);
            var n = Highlight.ReconstructWith(@out, [2.1f, 1.0f, 1.6f], 0.99f, [1.02f, 0.99f, 1.05f]);
            var clipped = 0;
            for (var i = 0; i < img.Data.Length; i += 3) if (img.Data[i] >= 0.99f || img.Data[i + 1] >= 0.99f || img.Data[i + 2] >= 0.99f) clipped++;
            Assert.Equal(clipped, n);
            for (var i = 0; i < img.Data.Length; i += 3)
            {
                if (img.Data[i] < 0.99f && img.Data[i + 1] < 0.99f && img.Data[i + 2] < 0.99f)
                {
                    Assert.True(img.Data[i] == @out.Data[i] && img.Data[i + 1] == @out.Data[i + 1] && img.Data[i + 2] == @out.Data[i + 2], $"{w}x{h}");
                }
                else
                {
                    Assert.True(float.IsFinite(@out.Data[i]) && float.IsFinite(@out.Data[i + 1]) && float.IsFinite(@out.Data[i + 2])
                        && @out.Data[i] >= 0f && @out.Data[i + 1] >= 0f && @out.Data[i + 2] >= 0f, $"{w}x{h}");
                }
            }
        }
    }

    [Fact]
    public void ClipNeutralMakesClippedWhiteNeutral()
    {
        var wb = new[] { 2.0f, 1.0f, 1.5f };
        var img = Filled(2, 1, [1f, 1f, 1f]);
        Set(img, 1, 0, [0.2f, 0.3f, 0.4f]);
        Highlight.ClipNeutral(img, wb, 1.0f);
        var p = Get(img, 0, 0);
        var q = new[] { p[0] * wb[0], p[1] * wb[1], p[2] * wb[2] };
        Assert.True(Math.Abs(q[0] - q[1]) < 1e-6f && Math.Abs(q[1] - q[2]) < 1e-6f);
        Assert.True(new[] { 0.2f, 0.3f, 0.4f }.SequenceEqual(Get(img, 1, 0)));
    }

    [Fact]
    public void ReconstructRestoresClippedChannel()
    {
        // a warm gradient whose red channel clips at its bright end
        var truth = FromFn(256, 16, (x, _) =>
        {
            var v = 0.3f + 1.0f * x / 255f;
            return [v * 1.0f, v * 0.6f, v * 0.35f];
        });
        var img = FromFn(256, 16, (x, y) => { var p = Get(truth, x, y); return [MathF.Min(p[0], 1f), MathF.Min(p[1], 1f), MathF.Min(p[2], 1f)]; });
        var first = 0;
        for (var x = 0; x < 256; x++) if (Get(img, x, 8)[0] >= 0.999f) { first = x; break; }
        // near the unclipped gradient (within the reach of its colour) red is rebuilt from it
        float Err(RgbImage i) { var s = 0f; for (var x = first; x < first + 12; x++) s += Math.Abs(Get(i, x, 8)[0] - Get(truth, x, 8)[0]); return s; }
        var before = Err(img);
        var n = Highlight.Reconstruct(img, [1f, 1f, 1f], 0.999f);
        Assert.True(n > 0);
        var after = Err(img);
        Assert.True(after < before * 0.2f, $"before {before} after {after}");
        // unclipped pixels untouched
        Assert.True(Get(truth, 0, 0).SequenceEqual(Get(img, 0, 0)));
        Assert.True(Get(truth, first - 1, 8).SequenceEqual(Get(img, first - 1, 8)));
    }

    [Fact]
    public void FullyClippedAndNoClipping()
    {
        var img = Filled(4, 4, [1f, 1f, 1f]);
        Assert.Equal(16, Highlight.Reconstruct(img, [2f, 1f, 1.5f], 0.99f));
        var p = Get(img, 0, 0);
        Assert.True(Math.Abs(p[0] * 2f - p[1]) < 1e-5f && Math.Abs(p[2] * 1.5f - p[1]) < 1e-5f);
        img = Filled(4, 4, [0.5f, 0.5f, 0.5f]);
        Assert.Equal(0, Highlight.Reconstruct(img, [2f, 1f, 1.5f], 0.99f));
        var empty = new RgbImage(0, 0);
        Assert.Equal(0, Highlight.Reconstruct(empty, [1f, 1f, 1f], 0.99f));
    }

    /// <summary>
    /// A mask decides which channels are clipped (their values are lower bounds), whatever their values; the
    /// unmarked pixels are left as they were; a mask of another size leaves clipping to the values.
    /// </summary>
    [Fact]
    public void AMaskMarksTheClippedChannels()
    {
        var @base = FromFn(32, 32, (x, _) => x >= 16 ? [0.2f, 0.5f, 0.25f] : [0.5f, 0.5f, 0.5f]);
        var mask = new byte[32 * 32];
        for (var i = 0; i < mask.Length; i++) if (i % 32 >= 16) mask[i] = Highlight.ClippedR;
        var img = Clone(@base);
        Assert.Equal(512, Highlight.ReconstructMasked(img, [1f, 1f, 1f], 0.99f, [1f, 1f, 1f], mask));
        for (var i = 0; i < img.Data.Length; i += 3)
        {
            Assert.True(float.IsFinite(img.Data[i]) && float.IsFinite(img.Data[i + 1]) && float.IsFinite(img.Data[i + 2]));
            if (@base.Data[i] == 0.5f)
            {
                Assert.True(@base.Data[i] == img.Data[i] && @base.Data[i + 1] == img.Data[i + 1] && @base.Data[i + 2] == img.Data[i + 2]);
            }
            else
            {
                // red rebuilt from at least its mean, the others kept
                Assert.True(img.Data[i] >= 0.2f && img.Data[i + 1] == 0.5f && img.Data[i + 2] == 0.25f, $"[{img.Data[i]}, {img.Data[i + 1]}, {img.Data[i + 2]}]");
            }
        }
        // red is rebuilt above its mean, towards the neighbours' neutral (as far as the pixel's own blue agrees)
        Assert.True(Get(img, 24, 16)[0] > 0.3f);
        // the values alone see nothing clipped here, nor does a mask of another size
        var plain = Clone(@base);
        Assert.Equal(0, Highlight.Reconstruct(plain, [1f, 1f, 1f], 0.99f));
        Assert.Equal(0, Highlight.ReconstructMasked(plain, [1f, 1f, 1f], 0.99f, [1f, 1f, 1f], mask[1..]));
        Assert.True(@base.Data.SequenceEqual(plain.Data));
    }
}
