namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 區域形狀的距離（散景清晰區、動態模糊與放射旋轉模糊的保護區共用）：形狀編號依 <c>ModuleCatalog</c> 選項順序，
/// 0 圓形、1 橢圓、2 水平帶、3 垂直帶。須與 <c>common/sampling.wgsl</c> 的 <c>shape_distance</c> 逐式一致。
/// </summary>
internal static class ShapeMask
{
    public const int Circle = 0, Ellipse = 1, HBand = 2, VBand = 3;

    /// <summary>長寬比參數（−100–100）→ 橢圓的軸比；0 為正圓。</summary>
    public static float Aspect(float parameter) => MathF.Max(.01f, 1 + parameter / 100f);

    /// <summary>(u, v) 是相對中心的位移（任意長度單位）；先依 cos／sin 旋轉，再量到形狀中心線的距離。</summary>
    public static float Distance(int shape, float u, float v, float aspect, float cos, float sin)
    {
        var ru = u * cos - v * sin;
        var rv = u * sin + v * cos;
        return shape switch
        {
            Ellipse => MathF.Sqrt(ru * ru / (aspect * aspect) + rv * rv * aspect * aspect),
            HBand => MathF.Abs(rv),
            VBand => MathF.Abs(ru),
            _ => MathF.Sqrt(ru * ru + rv * rv),
        };
    }
}
