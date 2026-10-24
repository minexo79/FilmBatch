namespace FilmBatch.App.Services.Loupe;

/// <summary>
/// 規劃 Loupe 所需的畫面尺寸輸入（皆為裝置物理像素）。
/// </summary>
/// <param name="DrawnLongPx">照片以目前縮放倍率繪製時的長邊像素（可遠大於畫布）。</param>
/// <param name="CanvasLongPx">預覽畫布實際的物理長邊像素。</param>
/// <param name="NativeLongPx">原圖（裁切後畫框）的長邊像素。</param>
/// <param name="TextureSide">GPU 單張貼圖的邊長上限。</param>
/// <param name="DraftScale">拖曳中的草稿倍率；非 <see langword="null"/> 時縮小 Main 層並停用 Window 層。</param>
/// <param name="WindowsAllowed">是否允許使用 Window 層（例如 crop 模式時關閉）。</param>
public readonly record struct ViewSizes(double DrawnLongPx, double CanvasLongPx, int NativeLongPx, int TextureSide, double? DraftScale, bool WindowsAllowed);

/// <summary>畫框座標系中的整數像素矩形（左上角與寬高）。</summary>
/// <param name="X">左緣（像素）。</param>
/// <param name="Y">上緣（像素）。</param>
/// <param name="W">寬度（像素）。</param>
/// <param name="H">高度（像素）。</param>
public readonly record struct PixelWindow(int X, int Y, int W, int H);

/// <summary><see cref="LoupePlan.Plan"/> 的結果。</summary>
/// <param name="MainEdge">Main 層（整張畫面）的渲染長邊。</param>
/// <param name="WindowFrameEdge">Window 層所在畫框的長邊；<see langword="null"/> 表示不需要 Window 層。</param>
public readonly record struct LoupePlanResult(int MainEdge, int? WindowFrameEdge);

/// <summary>
/// Loupe 要渲染多大：整張畫面約畫布大小；放大超過它能撐住的程度時，再加上螢幕上可見的窗口，最多到 100%
/// （參考 Lightcraft <c>region.rs</c>）。全部為純函式，不碰 UI 或 GPU。
/// </summary>
public static class LoupePlan
{
    /// <summary>Main 層邊長的級距（像素）。</summary>
    public const int EdgeStep = 64;

    /// <summary>Window 層對齊的網格大小（像素）；在同一格子內平移不觸發重算。</summary>
    public const int Snap = 256;

    /// <summary>Window 層 margin 的上限（像素）。</summary>
    public const int MaxMargin = 3072;

    /// <summary>Window 層單軸跨度上限（像素）。</summary>
    public const int MaxSpan = 6144;

    /// <summary>Preview 等級來源的長邊；<c>previewLimit == 0</c>（自動）時作為 Main 層上限。</summary>
    public const int PreviewLevelEdge = 2560;

    /// <summary>假設 GPU 至少支援的貼圖邊長。</summary>
    public const int MinTextureSide = 2048;

    /// <summary>繪製長邊超過 Main 層的此倍數時才加上 Window 層。</summary>
    public const double WindowRatio = 1.25;

    /// <summary>
    /// 計算 Main 層的渲染長邊：把想要的像素數以 <see cref="EdgeStep"/> 向上取整，
    /// 再受「設定上限（0 = <see cref="PreviewLevelEdge"/>）、原圖長邊、GPU 貼圖上限」約束。
    /// </summary>
    /// <param name="wantedPx">想要的長邊像素（通常為畫布與繪製尺寸的較小者，乘上草稿倍率）。</param>
    /// <param name="previewLimit">使用者設定的 Main 層上限；0 表示自動。</param>
    /// <param name="nativeLong">原圖長邊。</param>
    /// <param name="textureSide">GPU 貼圖邊長上限。</param>
    public static int MainEdge(double wantedPx, int previewLimit, int nativeLong, int textureSide)
    {
        var limit = previewLimit == 0 ? PreviewLevelEdge : Math.Clamp(previewLimit, 512, 8192);
        var ceiling = Math.Min(limit, Math.Max(textureSide, MinTextureSide));
        var wanted = double.IsFinite(wantedPx) ? Math.Clamp(wantedPx, 8, ceiling) : 8;
        var stepped = (int)Math.Ceiling(wanted / EdgeStep) * EdgeStep;
        return Math.Min(Math.Min(stepped, Math.Max(nativeLong, 8)), ceiling);
    }

    /// <summary>
    /// 決定 Main 層邊長，以及是否需要 Window 層與其畫框長邊（不超過原圖長邊；超過 100% 由 GPU 放大）。
    /// 草稿模式或 <see cref="ViewSizes.WindowsAllowed"/> 為 <see langword="false"/> 時不使用 Window 層。
    /// </summary>
    /// <param name="previewLimit">使用者設定的 Main 層上限；0 表示自動。</param>
    /// <param name="v">目前的畫面尺寸。</param>
    public static LoupePlanResult Plan(int previewLimit, ViewSizes v)
    {
        var canvas = double.IsFinite(v.CanvasLongPx) ? Math.Max(v.CanvasLongPx, 8) : 8;
        var drawn = double.IsFinite(v.DrawnLongPx) ? Math.Max(v.DrawnLongPx, 8) : canvas;
        var scale = v.DraftScale is { } s && double.IsFinite(s) ? Math.Clamp(s, 0.1, 1) : 1;
        if (!v.WindowsAllowed)
        {
            // 沒有 Window 層：整張畫面依繪製尺寸渲染（仍受 Preview 等級等上限約束），放大時才不會比代理圖更糊；
            // 拖曳中的草稿則只算畫布大小，1:1 時拖曳不能每格都算整張照片（Lightcraft plan 的 !windows 分支）。
            var wanted = v.DraftScale is not null ? Math.Min(drawn, canvas) : drawn;
            return new(MainEdge(wanted * scale, previewLimit, v.NativeLongPx, v.TextureSide), null);
        }
        var main = MainEdge(Math.Min(drawn, canvas) * scale, previewLimit, v.NativeLongPx, v.TextureSide);
        if (v.DraftScale is not null) return new(main, null);
        // 與 Lightcraft 相同：以「窗口畫框長邊（不超過原圖）」對 Main 層比較；使用者自訂了 Main 上限且目前是符合視窗時，
        // 視為選擇速度而非銳利度，不加窗口
        var frameEdge = Math.Min((int)Math.Round(drawn), Math.Max(v.NativeLongPx, 8));
        var fit = drawn <= canvas * 1.05;
        if (frameEdge <= main * WindowRatio || (fit && previewLimit != 0)) return new(main, null);
        return new(main, frameEdge);
    }

    /// <summary>
    /// Window 層四周的 margin：<c>ceil(min(frameLong × 0.045, MaxMargin) / Snap) × Snap</c>，最小為 <see cref="Snap"/>。
    /// </summary>
    /// <param name="frameLong">Window 層畫框的長邊。</param>
    public static int MarginFor(int frameLong) =>
        Math.Max(1, (int)Math.Ceiling(Math.Min(frameLong * 0.045, MaxMargin) / Snap)) * Snap;

    /// <summary>依 GPU 貼圖上限求 Window 層單軸跨度上限：不超過 <see cref="MaxSpan"/>，對齊 <see cref="Snap"/>，最小為 <see cref="Snap"/>。</summary>
    /// <param name="textureSide">GPU 貼圖邊長上限。</param>
    public static int MaxSpanFor(int textureSide) => Math.Max(Snap, Math.Min(textureSide, MaxSpan) / Snap * Snap);

    /// <summary>
    /// 把畫框座標中的可見範圍擴張 margin、對齊 <see cref="Snap"/> 網格並裁到畫框內，得到要渲染的窗口。
    /// 單軸超過 <paramref name="maxSpan"/> 時以可見範圍中心取最大跨度。
    /// </summary>
    /// <param name="frameW">畫框寬度（像素）。</param>
    /// <param name="frameH">畫框高度（像素）。</param>
    /// <param name="visible">可見範圍（畫框像素座標，可超出畫框）。</param>
    /// <param name="maxSpan">單軸跨度上限，通常取 <see cref="MaxSpanFor"/>。</param>
    /// <returns>要渲染的窗口；畫框無效、輸入非有限值或沒有可見範圍時為 <see langword="null"/>。</returns>
    public static PixelWindow? WindowFor(int frameW, int frameH, (double X0, double Y0, double X1, double Y1) visible, int maxSpan)
    {
        if (frameW <= 0 || frameH <= 0) return null;
        if (!double.IsFinite(visible.X0) || !double.IsFinite(visible.Y0) || !double.IsFinite(visible.X1) || !double.IsFinite(visible.Y1)) return null;
        var margin = MarginFor(Math.Max(frameW, frameH));
        (int A, int B)? Axis(double lo, double hi, int full)
        {
            lo = Math.Max(lo, 0); hi = Math.Min(hi, full);
            if (hi <= lo) return null;
            var a = Math.Max(0, (int)lo - margin) / Snap * Snap;
            var b = Math.Min(full, (int)Math.Ceiling((Math.Ceiling(hi) + margin) / Snap) * Snap);
            if (b - a <= maxSpan) return (a, b);
            var mid = (int)((lo + hi) / 2);
            var start = Math.Max(0, mid - maxSpan / 2) / Snap * Snap;
            return (start, Math.Min(start + maxSpan, full));
        }
        if (Axis(visible.X0, visible.X1, frameW) is not { } x || Axis(visible.Y0, visible.Y1, frameH) is not { } y) return null;
        return new PixelWindow(x.A, y.A, x.B - x.A, y.B - y.A);
    }
}
