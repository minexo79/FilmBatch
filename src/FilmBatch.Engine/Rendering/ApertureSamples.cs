using System.Numerics;

namespace FilmBatch.Engine.Rendering;

internal static class ApertureSamples
{
    public const int Count = 32;
    private static readonly Vector4[][] Patterns = [Create(0), Create(6), Create(8)];
    public static Vector4[] ForShape(int shape) => Patterns[shape is 1 or 2 ? shape : 0];

    private static Vector4[] Create(int sides)
    {
        var result = new Vector4[Count];
        for (var i = 0; i < Count; i++)
        {
            var angle = i * 2.39996323f;
            var radius = MathF.Sqrt((i + .5f) / Count);
            if (sides != 0)
            {
                var sector = 2 * MathF.PI / sides;
                radius *= MathF.Cos(MathF.PI / sides) / MathF.Cos(angle % sector - MathF.PI / sides);
            }
            result[i] = new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0, 0);
        }
        return result;
    }
}
