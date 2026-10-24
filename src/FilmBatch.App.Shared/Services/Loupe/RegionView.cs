namespace FilmBatch.App.Services.Loupe;

/// <summary>一個已送達（或在途）的窗口：屬於哪張照片、哪一側、哪個畫框尺寸、哪個窗口、哪組設定。</summary>
public sealed record RegionView(string Photo, bool Before, int FrameW, int FrameH, PixelWindow Window, long SettingsKey)
{
    /// <summary>
    /// 這個窗口能不能畫在目前畫面上：照片相同、畫框尺寸相同；設定相同，或正在拖曳（拖曳中允許沿用舊設定的窗口，
    /// 避免拖曳時窗口閃掉，規則同 Lightcraft）。
    /// </summary>
    public bool IsCurrent(string photo, (int W, int H) frame, long settingsKey, bool drafting) =>
        Photo == photo && FrameW == frame.W && FrameH == frame.H && (drafting || SettingsKey == settingsKey);
}
