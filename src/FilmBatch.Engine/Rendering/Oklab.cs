using System.Numerics;

namespace FilmBatch.Engine.Rendering;

/// <summary>線性 sRGB ↔ OKLab（與 linear/hsl.wgsl 的 to_oklab / from_oklab 同係數）。</summary>
internal static class Oklab
{
    public static Vector3 FromLinear(Vector3 rgb)
    {
        var l = MathF.Cbrt(.41222147f * rgb.X + .53633254f * rgb.Y + .05144599f * rgb.Z);
        var m = MathF.Cbrt(.21190350f * rgb.X + .68069955f * rgb.Y + .10739696f * rgb.Z);
        var s = MathF.Cbrt(.08830246f * rgb.X + .28171884f * rgb.Y + .62997870f * rgb.Z);
        return new(.21045426f * l + .79361779f * m - .00407205f * s,
            1.97799850f * l - 2.42859221f * m + .45059371f * s,
            .02590404f * l + .78277177f * m - .80867577f * s);
    }

    public static Vector3 ToLinear(Vector3 lab)
    {
        var l = lab.X + .39633778f * lab.Y + .21580376f * lab.Z;
        var m = lab.X - .10556135f * lab.Y - .06385417f * lab.Z;
        var s = lab.X - .08948418f * lab.Y - 1.29148555f * lab.Z;
        l *= l * l; m *= m * m; s *= s * s;
        return new(4.07674166f * l - 3.30771159f * m + .23096993f * s,
            -1.26843800f * l + 2.60975740f * m - .34131940f * s,
            -.00419609f * l - .70341861f * m + 1.70761470f * s);
    }
}
