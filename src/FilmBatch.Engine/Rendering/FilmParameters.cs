using System.Numerics;
using System.Runtime.InteropServices;
using FilmBatch.Core.Film;

namespace FilmBatch.Engine.Rendering;

/// <summary>與 film/common.wgsl 相同的 16-byte 對齊資料；所有可校正係數僅從內嵌表載入一次。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FilmParameters
{
    public Vector4 Wb0, Wb1, Wb2;
    public Vector4 Row0, Row1, Row2;
    public Vector4 Curve;   // 曲線斜率、位移、OKLab 色度倍率、曝光增益
    public Vector4 Effect;  // 效果編號、降飽和、暖色偏、黑位
    public Vector4 Tint;    // 染色 RGB、提亮
    public Vector4 Weights; // 單色 RGB 權重、效果對比
    public Vector4 Spatial; // DRO 暗部、亮部、銳利度
    public Vector4 Mix;     // 比例、風格模式（彩色/單色/棕褐）、粗粒、短邊
    public uint Seed, Pad0, Pad1, Pad2;

    private static readonly CreativeStyleTable Table = CreativeStyleTable.LoadEmbedded();
    private static readonly Matrix4x4 RgbToXyz = new(
        .4124564f, .2126729f, .0193339f, 0,
        .3575761f, .7151522f, .1191920f, 0,
        .1804375f, .0721750f, .9503041f, 0,
        0, 0, 0, 1);
    private static readonly Matrix4x4 Bradford = new(
        .8951f, -.7502f, .0389f, 0,
        .2664f, 1.7135f, -.0685f, 0,
        -.1614f, .0367f, 1.0296f, 0,
        0, 0, 0, 1);
    private static readonly Matrix4x4 XyzToRgb = Inverse(RgbToXyz);
    private static readonly Matrix4x4 InverseBradford = Inverse(Bradford);

    public static FilmParameters From(ModuleParams p, int shortSide)
        => From(p, shortSide, Table);

    // Calibration supplies candidate coefficients per render; production keeps its immutable table.
    internal static FilmParameters From(ModuleParams p, int shortSide, CreativeStyleTable table)
    {
        var style = table.StyleBaseOf(p.FilmStyle);
        var matrix = style.Matrix;
        var mode = style.MonoWeights is not null ? 1 : style.Sepia ? 2 : 0;
        var weights = style.MonoWeights;
        var result = new FilmParameters
        {
            Row0 = new((float)matrix[0], (float)matrix[1], (float)matrix[2], 0),
            Row1 = new((float)matrix[3], (float)matrix[4], (float)matrix[5], 0),
            Row2 = new((float)matrix[6], (float)matrix[7], (float)matrix[8], 0),
            Curve = new((float)style.K * (float)table.ContrastFactor((int)MathF.Round(p.FilmContrast)),
                (float)style.Shift, (float)table.SaturationFactor((int)MathF.Round(p.FilmSaturation)), MathF.Pow(2, p.FilmExposure)),
            Weights = weights is null ? new(.299f, .587f, .114f, 1) : new((float)weights[0], (float)weights[1], (float)weights[2], 1),
            Mix = new(p.FilmStrength / 100f, mode, 0, shortSide),
            Seed = p.SeedGrain,
        };
        var dro = table.DroLevels(p.FilmDro);
        result.Spatial = new((float)dro.Shadow, (float)dro.Highlight, p.FilmSharpness / 3f * .8f, 0);
        var effect = ModuleParams.FilmEffectIndex(p.FilmEffect);
        if (effect != 0 && table.TryGetEffect(p.FilmEffect, out var parameters))
        {
            result.Effect = new(effect, (float)parameters.Desaturate, (float)parameters.WarmShift, (float)parameters.FadeBlack);
            result.Weights.W = (float)parameters.ContrastScale;
            if (parameters.Weights is { } mono)
                result.Weights = new((float)mono[0], (float)mono[1], (float)mono[2], result.Weights.W);
            var option = p.FilmEffectOption.Length == 0 ? "blue" : p.FilmEffectOption;
            result.Tint = parameters.Tints.TryGetValue(option, out var tint)
                ? new((float)tint[0], (float)tint[1], (float)tint[2], (float)parameters.Lift)
                : new(1, 1, 1, (float)parameters.Lift);
            result.Mix.Z = (float)parameters.GrainBoost;
            // 相機照片效果優先：忽略創意風格、對比、飽和、DRO 與銳利度。
            result.Spatial = Vector4.Zero;
        }
        var balance = WhiteBalance(p, table);
        result.Wb0 = new(balance.M11, balance.M21, balance.M31, 0);
        result.Wb1 = new(balance.M12, balance.M22, balance.M32, 0);
        result.Wb2 = new(balance.M13, balance.M23, balance.M33, 0);
        return result;
    }

    private static Matrix4x4 WhiteBalance(ModuleParams p, CreativeStyleTable table)
    {
        var reference = Math.Clamp(p.FilmWbReference, 2500, 9900);
        var target = (float)(ModuleParams.WhiteBalanceKelvin(p.FilmWhiteBalance) ?? reference);
        var shifts = table.WhiteBalanceShift((int)p.FilmAmberBlue, (int)p.FilmGreenMagenta);
        target = Math.Clamp(1_000_000f / (1_000_000f / target - (float)shifts.Mired), 1800, 15000);
        if (target == reference && shifts.Cc == 0) return Matrix4x4.Identity;
        var from = Vector3.Transform(WhitePoint(target), Bradford);
        var to = Vector3.Transform(WhitePoint(reference), Bradford);
        var adaptation = RgbToXyz * Bradford * Matrix4x4.CreateScale(to / from) * InverseBradford * XyzToRgb;
        return adaptation * Matrix4x4.CreateScale(1, MathF.Exp((float)shifts.Cc * .005f), 1);
    }

    // CIE 黑體軌跡 xy → XYZ（Y=1），與 Bradford 的錐體矩陣搭配；不是逐通道 RGB 色溫近似。
    private static Vector3 WhitePoint(float temperature)
    {
        var t = (double)temperature;
        var x = t <= 4000
            ? -.2661239e9 / (t * t * t) - .2343580e6 / (t * t) + .8776956e3 / t + .179910
            : -3.0258469e9 / (t * t * t) + 2.1070379e6 / (t * t) + .2226347e3 / t + .240390;
        var y = t <= 2222 ? -1.1063814 * x * x * x - 1.34811020 * x * x + 2.18555832 * x - .20219683
            : t <= 4000 ? -.9549476 * x * x * x - 1.37418593 * x * x + 2.09137015 * x - .16748867
            : 3.0817580 * x * x * x - 5.87338670 * x * x + 3.75112997 * x - .37001483;
        return new((float)(x / y), 1, (float)((1 - x - y) / y));
    }

    private static Matrix4x4 Inverse(Matrix4x4 matrix) =>
        Matrix4x4.Invert(matrix, out var inverse) ? inverse : throw new InvalidOperationException("Invalid color matrix.");
}
