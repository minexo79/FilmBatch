# Lightcraft 式預覽（Loupe）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 每完成一個 Task 獨立 commit（不 push）。

**Goal:** 中央預覽在任何縮放倍率下都顯示與原圖同等清晰的畫面，而算圖成本跟著「畫布大小」走、不跟著縮放倍率走。

**Architecture:** 依 Lightcraft v0.13.0（`crates/ui-egui/src/region.rs`、`crates/engine/src/media.rs`、`docs/gpu-pipeline.md`）的做法分兩層渲染。**Main 層**：整張畫面只渲染到畫布實際的物理像素尺寸，以 64px 為一級，來源取自 ≤2560 的 Preview 等級。**Window 層**：放大超過 Main 層 1.25 倍時，只渲染螢幕上可見的範圍，最高到 100%，對齊 256px 網格並加 margin，疊在 Main 層上。輔助機制包括：三級來源（Thumb / Preview / Full）、開圖時先顯示替身、依實測時間調整拖曳草稿的尺寸、在 worker 執行緒用 Lanczos 縮到剛好的顯示尺寸，讓 Avalonia 以 1:1 繪製。

**Tech Stack:** .NET 10、Avalonia 12.1.4、CommunityToolkit.Mvvm、現有 `RenderScheduler` / `GpuSpikeRenderer` / `GpuEffectPipeline`（Dawn WebGPU + WGSL）/ `CpuPipeline`、xUnit v3；不新增相依套件。

**Spec:** 本文件的「現況」與「驗收」兩節。參考原始碼：RapidRAW（`src-tauri/src/lib.rs` 的 `process_preview_job`）、Lightcraft v0.13.0（上述檔案）。設計決策以 Lightcraft 為準。

## 現況（2026-10-23，`research/performance-improve` @ `6fb61dc`）

- `ImageCache.GetPreviewAsync` 以固定長邊 `AppSettings.PreviewLongEdge`（預設 1600，可選 1200/1600/2048/2560）解碼一份代理圖；`RenderScheduler` 以來源尺寸輸出。`PreviewCanvas` 放大時以 `BitmapInterpolationMode.HighQuality` 拉伸這張代理圖。**所以 100% 看到的是 1600px 放大的結果，不是原圖細節。**
- 符合視窗時，畫布若小於 1600 會由 Avalonia 縮小（品質受限於繪製時的插值）；畫布若大於 1600（4K、Retina）則是放大，看起來偏糊。
- 開圖時要等 `GetPreviewAsync` 解碼完才顯示，沒有替身；也沒有預取前後張。
- 拖曳滑桿時每一格都以完整代理圖尺寸算圖（transient 設定、排程器只保留最新請求）；沒有依實測時間降級。
- `GpuEffectPipeline.Acquire` 以 `SourceImage` 的參考相等快取上傳的來源紋理。**縮放後的來源必須是同一個物件**，否則每一格都會重新上傳。
- RAW 已有 binning（`RawSourceLoader.DevelopBinned`），Preview 等級可以不做全尺寸 demosaic。
- 所有空間效果都以「畫框」為座標系，例如 `short_side`、`vec2f(size)`、以正規化座標表示的中心與控制點，共 35 個 WGSL 檔案，`CpuPipeline` 也一樣。`shadow_stats` 是全畫框統計。**要只渲染窗口，必須讓每個 pass 知道窗口在畫框中的位置與畫框尺寸。**

## Global Constraints

- 預覽與匯出的色彩契約、RAW 演算法、`Render16` 匯出路徑不變。匯出仍走 `ImageLoader.LoadFull` 全解析度。
- 窗口渲染與「整張同尺寸渲染後裁切」的結果比較：margin 以內的像素逐通道 **max |Δ| ≤ 1 LSB、mean |Δ| < 0.05 LSB**（8-bit）。GPU 與 CPU 路徑都要測。
- 替身（快取的舊渲染、內嵌 JPEG、縮圖）只能暫時顯示，**不得**用於直方圖、照片工具取樣、遮罩、匯出，也不得當成最終畫面。替身顯示期間 `IsBusy` 維持 true。
- 不改既有持久化格式。`AppSettings.PreviewLongEdge` 的語意改為「Main 層上限」，新增值 `0 = 自動`，新安裝的預設為 0；既有使用者保留原值。
- Full 來源每張照片最多保留一份，Before/After 與所有平移共用；閒置且畫面上沒有窗口時才釋放。
- 世代與 epoch 圍欄（`PreviewService.IsStalePreview`、`PostAtEpoch`）在 Main 與 Window 兩條路徑都要保留。換照片、取消、重建後，舊窗口不得送達。
- 文字一律同時提供 zh-TW 與 en。
- 每個 Task 驗證完成後各自 commit，不 push。

## 驗收

| ID | 可觀察行為 | 驗證方式 |
|---|---|---|
| LOUPE-01 | 符合視窗時，Main 渲染長邊 = 畫布物理長邊向上取 64 的倍數，再受「設定上限、2560、原圖長邊、GPU 貼圖上限」約束；顯示時 bitmap 像素等於繪製的裝置像素 | `LoupePlanTests`；`PreviewDeliveryTests` 驗證送達尺寸 |
| LOUPE-02 | 放大到 100%、200%、800% 時，可見區域的像素與原圖解析度渲染後裁切的結果一致（容差見 Global Constraints）；算圖像素數 ≤ 畫布像素 ×(1+margin) 加上 Main 層 | `WindowRenderEquivalenceTests`（GPU 與 CPU）；桌面實機截圖比對 |
| LOUPE-03 | 在同一個 256 網格格子內平移不觸發重算；跨過格子才算新的窗口 | `LoupePlanTests.WindowFor_*`；`ZoomWindowTests` 計數算圖請求 |
| OPEN-01 | 開圖時立即顯示替身，正式渲染完成後原地替換；切到已預取的前後張時，顯示最終畫面 ≤ 100ms（桌面實測，為觀察值，非保證） | `QuickStandInTests`；桌面計時 |
| DRAG-01 | 拖曳中每格「算圖＋顯示」中位數超過 32ms 時，同一次拖曳改用 0.6 倍草稿；同一個「照片 × 控制項」連續兩次拖曳過慢才記為慢，30 秒後重試，再慢則等待時間加倍，最多 240 秒 | `DragScaleTests`；`SliderGestureTests` 擴充 |
| SAFE-01 | 既有 crop/undo/比較/直方圖/遮罩/降噪/匯出行為不變；完整測試套件通過 | 全部 solution 測試；macOS 打包冒煙測試 |

---

### Task 1: Loupe 幾何（純函式）

只寫數學，不接 UI。後面所有 Task 都依賴這裡的名稱與數值。

**Files:**
- Create: `src/FilmBatch.App.Shared/Services/Loupe/LoupePlan.cs`
- Test: `tests/FilmBatch.App.Tests/LoupePlanTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct ViewSizes(double DrawnLongPx, double CanvasLongPx, int NativeLongPx, int TextureSide, double? DraftScale, bool WindowsAllowed)`
  - `readonly record struct PixelWindow(int X, int Y, int W, int H)`
  - `readonly record struct LoupePlanResult(int MainEdge, int? WindowFrameEdge)`
  - `static class LoupePlan`，包含常數 `EdgeStep = 64`、`Snap = 256`、`MaxMargin = 3072`、`MaxSpan = 6144`、`WindowRatio = 1.25`、`PreviewLevelEdge = 2560`、`MinTextureSide = 2048`，以及方法：
    - `int MainEdge(double wantedPx, int previewLimit, int nativeLong, int textureSide)`：`previewLimit == 0` 時使用 `PreviewLevelEdge`
    - `LoupePlanResult Plan(int previewLimit, ViewSizes v)`
    - `int MarginFor(int frameLong)`：`ceil(min(frameLong*0.045, MaxMargin)/Snap)*Snap`，最小 `Snap`
    - `int MaxSpanFor(int textureSide)`：`max(Snap, min(textureSide, MaxSpan)/Snap*Snap)`
    - `PixelWindow? WindowFor(int frameW, int frameH, (double X0, double Y0, double X1, double Y1) visible, int maxSpan)`

- [ ] **Step 1: 寫失敗的測試**（數值取自 Lightcraft `region.rs` 的測試，改成 C#）

```csharp
public class LoupePlanTests
{
    private static ViewSizes Sizes(double drawn, double canvas, int native, double? draft = null) =>
        new(drawn, canvas, native, 16384, draft, WindowsAllowed: true);

    [Fact]
    public void Retina_fit_view_needs_no_window()
    {
        var p = LoupePlan.Plan(0, Sizes(2800, 2800, 6000));
        Assert.Equal(2560, p.MainEdge);
        Assert.Null(p.WindowFrameEdge); // 2800/2560 < 1.25
    }

    [Fact]
    public void Four_k_fit_view_gets_a_window()
    {
        var p = LoupePlan.Plan(0, Sizes(3840, 3840, 6000));
        Assert.Equal(2560, p.MainEdge);
        Assert.Equal(3840, p.WindowFrameEdge);
    }

    [Fact]
    public void Zoom_past_100_percent_caps_window_frame_at_native()
    {
        var p = LoupePlan.Plan(0, Sizes(12000, 2000, 6000));
        Assert.Equal(2048, p.MainEdge);            // 畫布 2000 → 以 64 為一級 → 2048
        Assert.Equal(6000, p.WindowFrameEdge);     // 超過 100% 由 GPU 放大
    }

    [Fact]
    public void Main_edge_steps_by_64_and_respects_limits()
    {
        Assert.Equal(1984, LoupePlan.MainEdge(1950, 0, 6000, 16384));
        Assert.Equal(1600, LoupePlan.MainEdge(1950, 1600, 6000, 16384)); // 使用者上限
        Assert.Equal(1000, LoupePlan.MainEdge(1950, 0, 1000, 16384));    // 原圖較小
        Assert.Equal(2560, LoupePlan.MainEdge(9000, 0, 6000, 16384));    // Preview 等級
    }

    [Fact]
    public void Draft_scales_main_and_suppresses_window()
    {
        var p = LoupePlan.Plan(0, Sizes(2000, 2000, 6000, draft: 0.6));
        Assert.Equal(1216, p.MainEdge); // 2000*0.6=1200 → 1216
        Assert.Null(p.WindowFrameEdge);
    }

    [Fact]
    public void WindowFor_snaps_and_adds_margin()
    {
        var w = LoupePlan.WindowFor(48_000, 32_000, (20_000, 10_000, 21_400, 10_900), LoupePlan.MaxSpan)!.Value;
        Assert.Equal(0, w.X % LoupePlan.Snap);
        Assert.Equal(0, w.Y % LoupePlan.Snap);
        Assert.True(w.X <= 20_000 - LoupePlan.MarginFor(48_000) + LoupePlan.Snap);
        Assert.True(w.X + w.W >= 21_400);
    }

    [Fact]
    public void WindowFor_small_pan_reuses_window()
    {
        var a = LoupePlan.WindowFor(48_000, 32_000, (20_000, 10_000, 21_400, 10_900), LoupePlan.MaxSpan);
        var b = LoupePlan.WindowFor(48_000, 32_000, (20_040, 10_030, 21_440, 10_930), LoupePlan.MaxSpan);
        Assert.Equal(a, b);
    }

    [Fact]
    public void WindowFor_clamps_to_frame_and_span()
    {
        var w = LoupePlan.WindowFor(3000, 2000, (-500, -500, 900, 700), LoupePlan.MaxSpan)!.Value;
        Assert.Equal(0, w.X); Assert.Equal(0, w.Y);
        var big = LoupePlan.WindowFor(100_000, 100_000, (0, 0, 90_000, 90_000), LoupePlan.MaxSpan)!.Value;
        Assert.True(big.W <= LoupePlan.MaxSpan && big.H <= LoupePlan.MaxSpan);
    }

    [Theory]
    [InlineData(2000, 0, 3000, 100)]  // 完全在畫框外
    [InlineData(10, 10, 10, 90)]      // 寬度為 0
    public void WindowFor_returns_null_when_nothing_visible(double x0, double y0, double x1, double y1) =>
        Assert.Null(LoupePlan.WindowFor(1000, 1000, (x0, y0, x1, y1), LoupePlan.MaxSpan));

    [Fact]
    public void WindowFor_rejects_non_finite() =>
        Assert.Null(LoupePlan.WindowFor(1000, 1000, (double.NaN, 0, 10, 10), LoupePlan.MaxSpan));
}
```

- [ ] **Step 2: 執行測試確認失敗**

Run: `dotnet test tests/FilmBatch.App.Tests --filter FullyQualifiedName~LoupePlanTests`
Expected: 編譯失敗，`LoupePlan` 不存在。

- [ ] **Step 3: 實作**

```csharp
namespace FilmBatch.App.Services.Loupe;

/// <summary>Loupe 要渲染多大：整張畫面約畫布大小；放大超過它能撐住的程度時，再加上螢幕上可見的窗口，最多到 100%（參考 Lightcraft region.rs）。</summary>
public static class LoupePlan
{
    public const int EdgeStep = 64, Snap = 256, MaxMargin = 3072, MaxSpan = 6144, PreviewLevelEdge = 2560, MinTextureSide = 2048;
    public const double WindowRatio = 1.25;

    public static int MainEdge(double wantedPx, int previewLimit, int nativeLong, int textureSide)
    {
        var limit = previewLimit == 0 ? PreviewLevelEdge : Math.Clamp(previewLimit, 512, 8192);
        var ceiling = Math.Min(limit, Math.Max(textureSide, MinTextureSide));
        var wanted = double.IsFinite(wantedPx) ? Math.Clamp(wantedPx, 8, ceiling) : 8;
        var stepped = (int)Math.Ceiling(wanted / EdgeStep) * EdgeStep;
        return Math.Min(Math.Min(stepped, Math.Max(nativeLong, 8)), ceiling);
    }

    public static LoupePlanResult Plan(int previewLimit, ViewSizes v)
    {
        var canvas = double.IsFinite(v.CanvasLongPx) ? Math.Max(v.CanvasLongPx, 8) : 8;
        var drawn = double.IsFinite(v.DrawnLongPx) ? Math.Max(v.DrawnLongPx, 8) : canvas;
        var scale = v.DraftScale is { } s && double.IsFinite(s) ? Math.Clamp(s, 0.1, 1) : 1;
        if (!v.WindowsAllowed)
        {
            // 沒有 Window 層：依繪製尺寸渲染（拖曳草稿則只算畫布大小）；見驗證輪 1 的回歸
            var wanted = v.DraftScale is not null ? Math.Min(drawn, canvas) : drawn;
            return new(MainEdge(wanted * scale, previewLimit, v.NativeLongPx, v.TextureSide), null);
        }
        var main = MainEdge(Math.Min(drawn, canvas) * scale, previewLimit, v.NativeLongPx, v.TextureSide);
        if (!v.WindowsAllowed || v.DraftScale is not null || drawn < main * WindowRatio) return new(main, null);
        return new(main, Math.Min((int)Math.Round(drawn), v.NativeLongPx));
    }

    public static int MarginFor(int frameLong) =>
        Math.Max(1, (int)Math.Ceiling(Math.Min(frameLong * 0.045, MaxMargin) / Snap)) * Snap;

    public static int MaxSpanFor(int textureSide) => Math.Max(Snap, Math.Min(textureSide, MaxSpan) / Snap * Snap);

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
```

（record struct 宣告放在同一檔案的開頭。）

- [ ] **Step 4: 執行測試確認通過**（同 Step 2 的指令）。Expected: 全部 PASS。
- [ ] **Step 5: Commit** `feat(loupe): add Lightcraft-style loupe plan geometry`

---

### Task 2: 三級來源（Thumb / Preview / Full）

**Files:**
- Modify: `src/FilmBatch.App.Shared/Services/ImageCache.cs`（新增 Full 單一槽、`GetSourceAsync`）
- Modify: `src/FilmBatch.Engine/Imaging/SourceImage.cs`（新增縮放結果快取 `ScaledTo`）
- Modify: `src/FilmBatch.App.Shared/Services/AppSettings.cs`、`ViewModels/Dialogs/PreferencesViewModel.cs`（`ProxySizes` 加入 `0` = 自動）、`Localization/Strings{,.en}.resx`
- Test: `tests/FilmBatch.App.Tests/SourceLevelTests.cs`、`tests/FilmBatch.Core.Tests/SourceImageScaleTests.cs`（若 Engine 的測試放在 Core.Tests，就沿用該處）

**Interfaces:**
- Produces:
  - `enum SourceLevel { Thumb, Preview, Full }`（放在 `Services/Loupe/SourceLevel.cs`），Preview 等級的長邊 = `LoupePlan.PreviewLevelEdge`；當 `PreviewLongEdge > 2560` 時改用該值。
  - `Task<SourceImage> ImageCache.GetSourceAsync(string path, SourceLevel level, RawLoadOptions rawOptions = default, CancellationToken ct = default)`：Preview 走既有的 `GetPreviewAsync` 路徑；Full 走前景入場呼叫 `ImageLoader.LoadFull`，只保留一筆（以 path 加 options 為 key；換照片或選項時取代並取消舊的那筆）。
  - `bool ImageCache.HoldsFull(string path, RawLoadOptions o)`
  - `void ImageCache.ReleaseFullIfIdle()`：由 UI 在「沒有窗口、閒置 3 秒」時呼叫。
  - `SourceImage SourceImage.ScaledTo(int longEdge)`：同一個來源物件、同一個 longEdge 回傳**同一個**物件（以 `ConditionalWeakTable` 內的小型字典最多保留 2 筆），讓 GPU 紋理快取命中；`longEdge >= max(W,H)` 時回傳 `this`。
- 既有的 `GetPreviewAsync` 與 `PreviewLongEdge` 保持相容。

- [ ] **Step 1: 寫失敗的測試**
  - `ScaledTo_returns_same_instance_for_same_edge`：`Assert.Same(src.ScaledTo(1024), src.ScaledTo(1024))`；`Assert.Same(src, src.ScaledTo(99999))`。
  - `ScaledTo_preserves_reference_size_and_metadata`：`ReferenceWidth`、`FilePath`、`RawOptions` 不變。
  - `Full_level_is_held_once_and_replaced_on_new_photo`：用產生的 JPEG（沿用 `TestSupport` 建檔方式）；A 的 Full、B 的 Full → `HoldsFull(A)` 為 false。
  - `Full_and_preview_share_no_task`：同一路徑同時請求 Preview 與 Full，兩者尺寸不同、都能完成。
  - `Preview_limit_zero_means_auto`：AppSettings 從 JSON 讀到 `0`，偏好設定清單顯示「自動」。
- [ ] **Step 2: 執行確認失敗** — `dotnet test tests/FilmBatch.App.Tests --filter FullyQualifiedName~SourceLevelTests`
- [ ] **Step 3: 實作。** Full 槽沿用 `_decodeGate`、資料夾世代與取消規則（同 `DecodePreviewAsync`）。`ScaledTo` 呼叫既有的 `Downscale`。`PreviewLongEdge == 0` 時，`ImageCache` 以 `LoupePlan.PreviewLevelEdge` 解碼。
- [ ] **Step 4: 執行確認通過**，並跑 `CacheAdmissionTests`、`BulkLoadingTests`、`ThumbnailRetentionTests` 確認沒有回歸。
- [ ] **Step 5: Commit** `feat(cache): add preview/full source levels and stable scaled sources`

---

### Task 3: 依畫布尺寸決定 Main 渲染尺寸，並以 Lanczos 縮到顯示尺寸

**Files:**
- Modify: `src/FilmBatch.App.Shared/Controls/PreviewCanvas.cs`：新增事件 `ViewSizesChanged(double drawnLongPx, double canvasLongPx)`（物理像素 = DIP × `RenderScaling`）；在 `Bounds`、`Zoom`、影像尺寸改變時觸發。影像 bitmap 尺寸剛好等於目的地的裝置像素時，以 `BitmapInterpolationMode.None` 繪製，避免二次插值。
- Modify: `src/FilmBatch.App.Shared/ViewModels/PreviewViewModel.cs`：保存 `ViewSizes`，並提供 `LoupePlanResult CurrentPlan`。
- Modify: `src/FilmBatch.App.Shared/ViewModels/MainWindowViewModel.cs:496-521`（`RequestPreview`）：來源改為 `SourceForPhoto(...).ScaledTo(plan.MainEdge)`；`plan.MainEdge` 改變時才重新請求（以 64 為一級，因此小幅縮放不會重算）。
- Modify: `src/FilmBatch.App.Shared/Services/PreviewService.cs`：`RequestPreview` 新增 `(int W, int H)? displayFit` 參數；在 render worker 的回呼中（直方圖之前）以 Lanczos 縮到 displayFit。直方圖仍以縮放前的結果計算。
- Create: `src/FilmBatch.Engine/Imaging/DisplayFit.cs`：`static RenderResult DisplayFit.Fit(RenderResult r, int maxW, int maxH)`，straight alpha 要先 premultiply 再濾波，與 `BatchExporter` 的 Lanczos 規則相同。若 `BatchExporter` 已有可重用的 Lanczos 核心，就抽出共用，不要重寫。
- Test: `tests/FilmBatch.App.Tests/LoupeMainRenderTests.cs`、`tests/FilmBatch.Core.Tests/DisplayFitTests.cs`

**Interfaces:**
- Consumes: `LoupePlan.Plan/MainEdge`（Task 1）、`SourceImage.ScaledTo`（Task 2）
- Produces: `PreviewViewModel.CurrentPlan`、`PreviewViewModel.ViewSizes`（Task 5、6 使用）

- [ ] **Step 1: 寫失敗的測試**
  - `Canvas_resize_changes_render_size_in_64_px_steps`：以假 renderer 記錄收到的來源尺寸；畫布 1000→1030 DIP（scaling 1）時請求數不變（同一級），→1100 時請求一次，長邊為 1152。
  - `Same_edge_reuses_same_source_instance`：連續兩次 RequestPreview 送出的 `SourceImage` 參考相同。
  - `DisplayFit_keeps_visible_colour_beside_transparent_pixels`：不透明紅色旁邊是 alpha 0 的藍色，縮小後不出現紫邊（與 2026-10-22 計畫的 alpha 規則相同）。
  - `DisplayFit_is_identity_when_already_fits`：尺寸 ≤ 目標時回傳同一物件。
  - `Delivered_bitmap_matches_device_pixels`：送達 `PreviewViewModel.Image` 的像素寬度等於 drawnLongPx（容差 1px）。
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作。** `RenderScaling` 由 `TopLevel.GetTopLevel(this)?.RenderScaling` 取得。`Zoom > 0` 時 drawn = 影像長邊 × Zoom（Zoom 1 = 1 影像像素 = 1 裝置像素）；Zoom 0 時 drawn = canvas fit。
- [ ] **Step 4: 執行確認通過**，再跑 `PreviewDeliveryTests`、`PreviewInteractionTests`、`EffectFootprintPreviewTests`、`CropToolTests`（控制點與範圍輪廓都以正規化座標表示，影像尺寸改變時不應偏移）。
- [ ] **Step 5: 桌面實機**：以 1×、2× scaling 各截一張符合視窗的畫面，比對 bitmap 尺寸與裝置像素（記錄到本文件的 State Ledger）。
- [ ] **Step 6: Commit** `feat(preview): size main render to the canvas and Lanczos-fit for 1:1 display`

---

### Task 4: 開圖替身與前後張預取

**Files:**
- Create: `src/FilmBatch.App.Shared/Services/Loupe/ViewRenderCache.cs`：以 `(path, settingsHash, rawOptions)` 為 key 的 LRU，保存最近 8 張「完整品質 Main 渲染結果」（`RenderResult`）；記憶體上限 256 MB，超過就逐出最舊的。
- Modify: `MainWindowViewModel.LoadPreviewAsync`（`:430`）：在 `await GetPreviewAsync` 之前，依序嘗試替身：① `ViewRenderCache` 命中；② RAW 以 `RawSourceLoader.TryExtractThumbnail(path, 2560, ...)` 取內嵌 JPEG；③ filmstrip 已有的縮圖 bitmap。以 `PreviewViewModel.ShowStandIn(Bitmap, StandInKind)` 顯示。正式結果送達時清除替身。
- Modify: `PreviewViewModel`：新增 `StandInKind? StandIn` 屬性；`SetResult` 時清除。有替身時，`Histogram` 回傳 null。
- Modify: `MainWindowViewModel`：當前照片第一次完整渲染送達後，對 filmstrip 順序中的前一張與後一張呼叫 `Images.GetSourceAsync(..., SourceLevel.Preview)`（背景入場、受 CPU 節流），並在 render 執行緒空閒時算出結果放入 `ViewRenderCache`。新增 `RenderScheduler.RequestPrefetch(key, source, settings)`，優先序低於預覽、高於縮圖，結果只進快取、不送 UI。
- Test: `tests/FilmBatch.App.Tests/QuickStandInTests.cs`、`RenderScheduler` 的既有測試檔（新增預取優先序的案例）

**Interfaces:**
- Consumes: `ImageCache.GetSourceAsync`（Task 2）、`PreviewViewModel.CurrentPlan`（Task 3，預取以目前的 MainEdge 算圖）
- Produces: `ViewRenderCache.TryGet/Put`、`event Action<string, RenderResult> RenderScheduler.PrefetchRendered`

- [ ] **Step 1: 寫失敗的測試**
  - `Opening_a_photo_shows_cached_view_render_before_decode`：以 `CacheAdmissionTests` 既有的「持住解碼」手法卡住 `GetPreviewAsync`；第二次開同一張照片時，`Preview.Image` 立刻有值，`StandIn == ViewCache`，`IsBusy == true`，`Histogram == null`。
  - `Stand_in_is_replaced_and_never_reaches_tools`：正式結果送達後 `StandIn == null`；`PhotoTools.UpdateContext` 收到的是正式來源。
  - `Unedited_raw_uses_embedded_jpeg_stand_in`：用產生的 DNG（含內嵌預覽的 fixture）。
  - `Prefetch_never_delays_focused_preview`：預取排隊時送出預覽，預覽先完成。
  - `Stale_prefetch_after_folder_change_is_dropped`
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作。** settingsHash 使用現有 `Settings` 的穩定序列化（找現有的 hash/Equals 實作重用；若沒有，就以 `Settings` 的 JSON 字串做 SHA-256 前 8 bytes）。
- [ ] **Step 4: 執行確認通過**，再跑 `RawSourceLifecycleTests`、`FilmstripLoadingTests`、`BulkLoadingTests`。
- [ ] **Step 5: 桌面計時**：開啟一張照片、切到下一張（已預取）、切到第五張（未預取），三者從點擊到「最終畫面」的時間記入 ledger。
- [ ] **Step 6: Commit** `feat(preview): show stand-ins on open and prefetch neighbours`

---

### Task 5: 依實測時間調整拖曳草稿尺寸（DragScale）

**Files:**
- Create: `src/FilmBatch.App.Shared/Services/Loupe/DragScale.cs`：從 Lightcraft `region.rs` 的 `DragScale` 移植。
- Modify: `MainWindowViewModel`：手勢開始時呼叫 `DragScale.Begin(photo, controlKey, gpu: !Renderer.IsSoftware)`；每次結果送達時呼叫 `Observe(key, renderMs, lastFrameMs)`；`EndEdit` 時呼叫 `Finish(now)`。拖曳中 `ViewSizes.DraftScale = DragScale.Current`，放手後恢復為 null 並送出完整請求。
- Modify: `PreviewCanvas`：以 `TopLevel.RequestAnimationFrame` 量測上一幀的時間，並提供給 VM。
- Test: `tests/FilmBatch.App.Tests/DragScaleTests.cs`；擴充 `SliderGestureTests.cs`

**Interfaces:**
- Produces:
  - `const double DraftBudgetMs = 16, CpuDraftScale = 0.6, GiveUpFactor = 2, RetryAfterS = 30, RetryMaxS = 240; const int GiveUpAfter = 3, VerdictsKept = 256;`
  - `double? Scale((string Photo, string Control)? drag, bool gpu, long? shownKey, double nowS)`
  - `void Observe(long key, double renderMs, double? lastFrameMs)`

- [ ] **Step 1: 寫失敗的測試**（移植 Lightcraft 的行為）
  - `Gpu_drag_starts_full_size` / `Cpu_drag_starts_at_0_6`
  - `Slow_full_size_drafts_give_up_mid_drag`：輸入 5 個樣本，每個 40ms（> 32ms），第 4 個之後 `Scale` 變成 0.6。
  - `One_slow_drag_is_only_suspect` → 第二次拖曳仍是 1.0；`Two_slow_drags_make_pair_slow_for_30s` → 第三次是 0.6；到 `now+31` 時回到 1.0；再慢一次則等待時間變成 60 秒，最多 240 秒。
  - `Fast_drag_forgets_verdict`
  - `Verdict_is_per_photo_and_control`
  - `First_draft_is_excluded_from_median`
  - `Frame_time_over_1s_counts_as_unknown`
  - `Draft_scale_never_changes_within_drag_unless_giving_up`
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作**（整個類別約 150 行，結構照 Lightcraft；時間由呼叫端傳入，方便測試）。
- [ ] **Step 4: 執行確認通過**，再跑 `SliderGestureTests`、`PreviewDragFrameTests`、`ParamSliderTests`。
- [ ] **Step 5: 桌面實測**：在一台慢機器或強制軟體模式下拖曳曝光，記錄草稿比例與中位數時間。
- [ ] **Step 6: Commit** `feat(preview): adapt slider draft size to measured frame time`

---

### Task 6: 引擎支援窗口渲染（frame-aware passes）

本計畫最大、風險最高的一項。先寫等價測試，再改 shader。

**Files:**
- Modify: `src/FilmBatch.Engine/Rendering/ModuleParams.cs`：新增 `public RenderView View;`。`RenderView(int FrameW, int FrameH, int X, int Y, int W, int H)`，預設值 = 整個畫框（`X=Y=0, W=FrameW, H=FrameH`）。`OutWidth/OutHeight` 在窗口模式下回傳 `W/H`；新增 `FrameWidth/FrameHeight`。
- Modify: `src/FilmBatch.Engine/Rendering/GpuEffectPipeline.cs`：
  - `geometry/crop` 一律執行（窗口模式下即使 `Crop.On == false` 也要）。uniform 加上 `view_origin`、`view_scale`：取樣座標 = `OutToSource(view_origin + (id + .5) * view_scale)`，其中 `view_scale = 1`，因為來源已縮放到 frame 尺寸，見 Task 7。
  - 新增共用 uniform 區塊 `ViewUniforms { frame_size: vec2f, view_origin: vec2f }` 綁在每個 frame-relative pass 的固定 binding；在 `common/sampling.wgsl` 新增 `fn frame_px(id: vec2u) -> vec2f { return vec2f(id) + 0.5 + view.view_origin; }`。
  - `short_side` 一律以 `min(frame_size)` 計算；凡是以 `vec2f(size)` 或 `textureDimensions` 推算中心、正規化座標的地方，都改用 `frame_size` 與 `frame_px`。
  - `shadow_stats`（以及任何全畫框統計）：窗口模式下**不得**在窗口上重算。改為由呼叫端傳入 Main 渲染時的統計（`ModuleParams.FrameStats`），Main 渲染時記錄下來。
  - 每個 pass 的 seed / grain 以 frame 座標取樣，確保窗口內的雜訊與整張渲染時一致。
- Modify: `src/FilmBatch.Engine/Rendering/CpuPipeline.cs`：同樣的規則（`shortSide`、中心、統計、grain 座標）。
- Modify: `src/FilmBatch.Engine/Rendering/IPreviewRenderer.cs`：新增 `RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats, CancellationToken ct = default)`；`GpuSpikeRenderer` 與 `CpuFakeRenderer` 都要實作。`RenderResult` 新增 `FrameStats? Stats { get; init; }`，由 `Render` 填入。
- 受影響的 shader（實作前先 `grep -l "short_side\|vec2f(size)\|textureDimensions"` 確認，目前為 35 個）：至少包括 `geometry/{crop,lens_distortion,multi_lens}`、`optical/*`、`surface/{grain,dirt_scratches,texture_overlay}`、`film/{look,photo_plate}`、`linear/{basic_display,shadow_*}`、`io/foreground_alpha`。
- 執行 `build/CheckShaders.cs`，確認所有 entry point 都能通過 Naga 驗證。
- Test: `tests/FilmBatch.Core.Tests/WindowRenderEquivalenceTests.cs`（GPU 測試沿用既有「沒有 adapter 就略過並註明」的慣例）

**Interfaces:**
- Produces: `RenderView`、`FrameStats`、`IPreviewRenderer.RenderWindow`、`RenderResult.Stats`

- [ ] **Step 1: 寫失敗的等價測試**

```csharp
public static TheoryData<string, Settings> Cases => WindowCases.All; // 每個 frame-relative 模組各一組非零設定，加上 crop+旋轉、鏡頭、遮罩、grain

[Theory, MemberData(nameof(Cases))]
public void Window_matches_full_frame_crop_cpu(string name, Settings settings)
{
    var src = Fixtures.Synthetic(3000, 2000);
    var full = new CpuFakeRenderer().Render(src, settings);
    var view = new RenderView(full.Width, full.Height, X: 1024, Y: 512, W: 768, H: 512);
    var win = new CpuFakeRenderer().RenderWindow(src, settings, view, full.Stats!);
    PixelAssert.WithinLsb(Crop(full, view), win, maxAbs: 1, meanAbs: 0.05, because: name);
}
// 同樣的案例另寫 _gpu 版本；另加窗口在四個角落與邊緣（X=0、X+W=FrameW）的案例
```

- [ ] **Step 2: 執行確認失敗**（`RenderWindow` 不存在）。
- [ ] **Step 3: 先改 CPU 路徑**，直到 CPU 的所有案例通過。CPU 是 oracle。
- [ ] **Step 4: 改 GPU 路徑**，逐一處理 shader。每改完一組就跑 `--filter WindowRenderEquivalence` 加上既有的 GPU/CPU 等價測試，確認**整張渲染的結果 bit 不變**（View 為預設值時）。
- [ ] **Step 5: 執行 `build/CheckShaders.cs` 與完整的 Core 測試。**
- [ ] **Step 6: Commit**（可拆成兩次：`feat(engine): frame-aware window rendering on CPU`、`feat(gpu): frame-aware window rendering shaders`）

---

### Task 7: 縮放窗口整合（UI）

**Files:**
- Create: `src/FilmBatch.App.Shared/Services/Loupe/RegionView.cs`：`record RegionView(string Photo, bool Before, int FrameW, int FrameH, PixelWindow Window, long SettingsKey)`，以及 `bool IsCurrent(string photo, (int,int) frame, long settings, bool drafting)`（規則同 Lightcraft：拖曳中允許沿用舊設定的窗口）。
- Modify: `PreviewViewModel`：新增 `Bitmap? WindowImage`、`RegionView? WindowRegion`；以畫布回報的可見矩形（frame 像素座標）呼叫 `LoupePlan.WindowFor`。結果不同時才送出請求；若 `!Images.HoldsFull(...)` 且已有窗口在途，就延後（`defer_window`）。
- Modify: `MainWindowViewModel`：`plan.WindowFrameEdge` 有值時，取得 `Images.GetSourceAsync(path, SourceLevel.Full)`，以 `.ScaledTo(WindowFrameEdge)`（`WindowFrameEdge == 原圖長邊` 時就是 Full 本身）作為 frame source，再呼叫 `Renderer.RequestWindow(...)`。Stats 取自最近一次 Main 結果的 `RenderResult.Stats`。
- Modify: `PreviewService` / `RenderScheduler`：新增窗口 slot（與預覽 slot 分開，各自只保留最新的請求；優先序：預覽 > 窗口 > 預取 > 縮圖），並沿用世代 / epoch 圍欄。新增事件 `WindowReady(RegionView, RenderResult)`。
- Modify: `PreviewCanvas`：繪製 Main 之後，若 `WindowRegion` 為 current 且長寬比與 Main 相同（`|ratio-1| ≤ 0.02`），把 `WindowImage` 畫在 `frameRect` 中對應的位置，clip 到畫布範圍（分割對比時 clip 到該側）。平移與縮放只改變繪製位置，不重算。
- Modify: `MainWindowViewModel`：沒有窗口且閒置 3 秒時，呼叫 `Images.ReleaseFullIfIdle()`。
- 原圖對比（`ShowOriginal` / `IsSplit`）：Before 側以相同的窗口邏輯，用 `RenderWindow` 搭配「原始設定」算圖，並共用同一份 Full 來源。如果時程不允許，Before 側可以先只顯示 Main 層，並在 ledger 註明這是已知限制。
- Test: `tests/FilmBatch.App.Tests/ZoomWindowTests.cs`

**Interfaces:**
- Consumes: Task 1 到 Task 6 的全部介面。

- [ ] **Step 1: 寫失敗的測試**
  - `Zoom_100_requests_one_window_covering_viewport`
  - `Pan_within_snap_cell_does_not_rerender`；`Pan_across_cell_requests_new_window`
  - `Window_is_dropped_after_photo_switch`（世代圍欄）
  - `Second_window_waits_until_full_source_is_held`
  - `Full_source_released_when_idle_without_window`
  - `Drafting_keeps_previous_window_visible`
  - `Window_not_drawn_over_mismatched_aspect_stand_in`
- [ ] **Step 2: 執行確認失敗**
- [ ] **Step 3: 實作**
- [ ] **Step 4: 執行確認通過**，再跑完整的 App 測試。
- [ ] **Step 5: 桌面實機**：以 24MP RAW 與 JPEG 各測一次，在 100%、200%、800% 時截圖，與 `Render16` 全尺寸匯出後裁切的結果比較（記錄 max/mean LSB 差異與算圖時間）；平移時以每秒畫面數觀察是否順暢。
- [ ] **Step 6: Commit** `feat(preview): render the visible zoom window at full detail`

---

### Task 8: 文件、整體驗證與證據

**Files:** `docs/architecture.md`（預覽一節）、`docs/building.md` 或 `VERIFY.md`（若有相關驗證步驟）、本計畫的 State Ledger。

- [ ] 更新架構文件：Main/Window 兩層、三級來源、替身、DragScale、窗口等價契約。
- [ ] 執行完整 Release solution 測試；執行 `dotnet run build/Build.cs -- --rid osx-arm64 --smoke`（或目前平台對應的 rid），檢查冒煙測試的 receipt。
- [ ] 前後對照量測（相同輸入、相同設定）：符合視窗算圖時間、100% 窗口算圖時間、平移時算圖次數、開圖到替身／到最終畫面的時間、峰值記憶體（`GC.GetTotalMemory(true)` 與 GPU 預算）。結果記為觀察值，不作為保證。
- [ ] Commit `docs: record Lightcraft-style loupe architecture and evidence`

## 不在本計畫範圍

- **分段快取（Lightcraft StageCache / GpuStages）**：Task 5 完成後先量測。如果 GPU 拖曳全尺寸草稿的中位數仍超過 16ms，再另開計畫，把幾何、降噪、空間平面與逐像素 `finish` 分開快取。
- **算圖結果直接留在 GPU、由 Dawn 繪到 Avalonia surface**（RapidRAW 的 wgpu display）：需要和 Avalonia 的 compositor 共用裝置，風險高，另行評估。
- Windows / iOS 實機驗證：共用程式碼，但本輪不推定在這兩個平台上的結果。

## Review Rejection Criteria

以下情況直接退回：

- 以「拉大固定代理圖」冒充 100% 細節。
- 窗口的結果與整張渲染後裁切不一致，卻以放寬容差帶過。
- 窗口模式在窗口上重算全畫框統計。
- 每次平移、每一格拖曳都重新上傳來源紋理（`ScaledTo` 沒有回傳同一個物件）。
- 替身進入直方圖、工具或匯出。
- Full 來源每個視角各持一份。
- 舊照片的窗口送達新照片。
- 測試只斷言私有欄位或佇列長度。

## State Ledger

| Item | Status | Evidence |
|---|---|---|
| 現況調查 | inspected | 固定 1600 代理圖由畫布放大；沒有替身與預取；GPU 以參考相等快取來源；RAW 已有 binning；35 個 frame-relative shader |
| Task 1 | verified | `448c68a`；LoupePlanTests 11/11 |
| Task 2 | verified | `54e92b0`；SourceLevelTests 8、SourceImageScaleTests 4；App 284、Core 701/42 skipped |
| Task 3 | verified | `e74cb72` + `bd2405b`（同級 refit、工具分析維持代理圖解析度）；App 291、Core 705/42 skipped |
| 驗證輪 1 | observed | 50 張 Sony A7M4（7008×4672，25 ARW + 25 JPG）、RTX 3060 DX12、headless Avalonia+Skia、Auto。開圖到最終預覽中位數／p90：ARW 519／625 ms，JPG 1333／1860 ms（JPG 解碼約為 ARW 的 3 倍）；符合視窗 1:1 共 55/55；50 張縮圖 12.6 s 完成。**回歸**：放大時 Main 被畫布尺寸封頂（1600），100% 以上比 Task 3 之前更糊（針織紋出現摩爾紋） |
| 回歸修正 | verified | `LoupePlan.Plan`：沒有 Window 層時依繪製尺寸渲染（上限 2560），拖曳草稿仍為畫布尺寸；App 293/293 |
| Task 4 – 8 | pending | — |
