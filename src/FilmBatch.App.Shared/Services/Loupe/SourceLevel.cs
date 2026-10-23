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
