namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 窗口渲染的視圖：在 <paramref name="FrameW"/>×<paramref name="FrameH"/> 的輸出畫框（裁切後、
/// 已縮放到目標倍率）中，只渲染左上角 (<paramref name="X"/>, <paramref name="Y"/>)、
/// 大小 <paramref name="W"/>×<paramref name="H"/> 的子矩形。
/// 所有以畫框為座標系的計算（短邊、中心、正規化座標、控制點、暗角幾何、顆粒／雜訊／種子座標、
/// 鏡頭中心）都以畫框座標與畫框尺寸進行，不以窗口紋理尺寸計算；
/// 預設值 <see cref="Whole"/>＝整個畫框，結果與既有整張渲染逐位元相同。
/// </summary>
/// <param name="FrameW">畫框寬（像素）。</param>
/// <param name="FrameH">畫框高（像素）。</param>
/// <param name="X">窗口左緣（畫框像素，0 ≤ X）。</param>
/// <param name="Y">窗口上緣（畫框像素，0 ≤ Y）。</param>
/// <param name="W">窗口寬（X + W ≤ FrameW）。</param>
/// <param name="H">窗口高（Y + H ≤ FrameH）。</param>
public readonly record struct RenderView(int FrameW, int FrameH, int X, int Y, int W, int H)
{
    /// <summary>整個畫框（窗口＝畫框）。</summary>
    public static RenderView Whole(int frameW, int frameH) => new(frameW, frameH, 0, 0, frameW, frameH);

    /// <summary>是否為整個畫框。</summary>
    public bool IsWhole => X == 0 && Y == 0 && W == FrameW && H == FrameH;

    /// <summary>窗口完全落在畫框內且非空。</summary>
    public bool IsInsideFrame => FrameW > 0 && FrameH > 0 && W > 0 && H > 0
        && X >= 0 && Y >= 0 && (long)X + W <= FrameW && (long)Y + H <= FrameH;

    internal void Validate()
    {
        if (!IsInsideFrame)
            throw new ArgumentOutOfRangeException(nameof(RenderView), this, "窗口必須是畫框內的非空矩形。");
    }
}

/// <summary>
/// 整張畫框（Main 渲染）時記錄的全畫框統計，窗口渲染時原樣傳入，不在窗口上重算。
/// 目前管線沒有任何需要全畫框統計的 pass：<c>shadow_stats</c> 是區塊代理格的局部統計
/// （窗口渲染時把渲染區域對齊到區塊格線並加上足夠的外圍，就和整張渲染相同），
/// 其餘模組都是逐像素或有界鄰域。此型別保留給日後的全域統計（例如自動曝光、百分位數），
/// 由 <see cref="IPreviewRenderer.Render"/> 填入 <see cref="RenderResult.Stats"/>。
/// </summary>
/// <param name="FrameWidth">記錄統計時的畫框寬（Main 渲染的尺寸，可與窗口渲染的畫框不同比例尺）。</param>
/// <param name="FrameHeight">記錄統計時的畫框高。</param>
public sealed record FrameStats(int FrameWidth, int FrameHeight);
