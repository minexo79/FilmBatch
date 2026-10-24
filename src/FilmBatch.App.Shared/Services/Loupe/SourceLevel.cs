namespace FilmBatch.App.Services.Loupe;

/// <summary>來源解析度等級（參考 Lightcraft media.rs）。</summary>
public enum SourceLevel
{
    /// <summary>縮圖來源（<see cref="ImageCache.ThumbnailLongEdge"/>），背景入場。</summary>
    Thumb,

    /// <summary>預覽代理圖（偏好上限；0 = 自動時為 <see cref="LoupePlan.PreviewLevelEdge"/>），供 Main 層使用。</summary>
    Preview,

    /// <summary>原圖全解析度（<c>ImageLoader.LoadFull</c>），快取中只保留一張，供 Window 層使用。</summary>
    Full,
}

/// <summary>來源解碼的入場方式。</summary>
public enum SourceAdmission
{
    /// <summary>前景入場（焦點照片）：不節流。</summary>
    Foreground,

    /// <summary>
    /// 背景入場（前後張預取）：與縮圖共用背景閘門並受 CPU 節流。只適用 <see cref="SourceLevel.Preview"/>；
    /// 尚未開始解碼時若同一張被前景請求，改由前景重新入場，不排在背景之後。
    /// </summary>
    Background,
}
