using System.Collections.Concurrent;

namespace FilmBatch.Engine.Rendering;

internal static class GaussianWeights
{
    private static readonly ConcurrentDictionary<(int Radius, float Sigma), float[]> Cache = new();

    public static float[] Get(int radius, float sigma) => Cache.GetOrAdd((radius, sigma), static key =>
    {
        var weights = new float[key.Radius * 2 + 1];
        var factor = -.5f / (key.Sigma * key.Sigma);
        float sum = 0;
        for (var i = -key.Radius; i <= key.Radius; i++)
        {
            weights[i + key.Radius] = MathF.Exp(i * i * factor);
            sum += weights[i + key.Radius];
        }
        for (var i = 0; i < weights.Length; i++) weights[i] /= sum;
        return weights;
    });
}
