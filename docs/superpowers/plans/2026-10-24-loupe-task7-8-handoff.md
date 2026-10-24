# Loupe Task 7–8 交接說明（給本地模型執行）

> 這份文件是 `docs/superpowers/plans/2026-10-23-lightcraft-style-loupe.md` 的 Task 7、Task 8 拆解版。
> Task 1–6 已完成並推到 `origin/research/performance-improve`（`e238417`）。
> 每一小步都能單獨編譯、單獨測試。**一次只做一小步**，該步測試通過後 commit，再做下一步。

---

## 0. 開工前必讀

### 0.1 分支與目錄

- 分支：`research/performance-improve`。
- 不要在主 checkout（`D:\XO-PersonalWorkspace\Personal_Project\DotNETWorkspace\FilmBatch`）上切分支，那裡有別的工作在用 `dev-ai-denoising`。
- 建議用 worktree：

```bash
git worktree add .claude/worktrees/loupe-t7 research/performance-improve
```

- 每完成一小步 commit 一次，**不要 push**。
- Commit 訊息用英文 conventional commits，例如 `feat(preview): add window slot to render scheduler`。

### 0.2 指令

```bash
dotnet build FilmBatch.sln -c Debug
```

```bash
dotnet test tests/FilmBatch.App.Tests -c Debug --filter "FullyQualifiedName~ZoomWindow"
```

```bash
dotnet test tests/FilmBatch.App.Tests -c Debug
```

```bash
dotnet test tests/FilmBatch.Core.Tests -c Debug
```

目前基準：App 338 通過；Core 799 通過、46 skipped（skipped 是沒有 GPU adapter 時略過的 GPU 測試，正常）。
你的修改之後，這兩個數字只能增加，不能有失敗。

### 0.3 絕對不能違反的規則（違反就是退件）

1. **不能用「把小代理圖拉大」冒充 100% 細節。** 窗口一定要從 Full 來源算。
2. **窗口結果必須等於「整張渲染後裁切」。** 不准放寬 `WindowRenderEquivalenceTests` 的容差（max ≤ 1 LSB、mean < 0.05 LSB）。
3. **`SourceImage.ScaledTo(edge)` 同一個 edge 會回傳同一個物件**，GPU 來源紋理快取靠參考相等。不要自己 new 縮放後的 `SourceImage`，也不要每次平移都產生新的來源物件。
4. **Full 來源每張照片只持有一份**（`ImageCache.GetSourceAsync(path, SourceLevel.Full)`），Before/After、所有平移共用。
5. **舊照片的窗口不能送達新照片。** 換照片、取消、重建（epoch 改變）之後，在途的窗口結果要丟掉。沿用 `PreviewService` 既有的世代（generation）與 `PostAtEpoch` 圍欄。
6. **替身（stand-in）期間不畫窗口**；窗口不進直方圖、不進照片工具取樣、不進匯出。
7. 介面文字一律同時提供 zh-TW 與 en。
8. 程式碼註解、XML doc 用繁體中文，風格比照周圍程式碼。
9. 測試要斷言「可觀察行為」（送進算圖器的請求數、送達的影像尺寸、畫面上是否有窗口），不要只斷言私有欄位或佇列長度。

---

## 1. 已經存在、可以直接用的東西

### 1.1 幾何（純函式）— `src/FilmBatch.App.Shared/Services/Loupe/LoupePlan.cs`

```csharp
public readonly record struct ViewSizes(double DrawnLongPx, double CanvasLongPx, int NativeLongPx, int TextureSide, double? DraftScale, bool WindowsAllowed);
public readonly record struct PixelWindow(int X, int Y, int W, int H);
public readonly record struct LoupePlanResult(int MainEdge, int? WindowFrameEdge);

public static class LoupePlan
{
    public const int Snap = 256, MaxSpan = 6144, PreviewLevelEdge = 2560;
    public const double WindowRatio = 1.25;
    public static LoupePlanResult Plan(int previewLimit, ViewSizes v);   // WindowFrameEdge != null 代表要窗口
    public static int MaxSpanFor(int textureSide);
    public static PixelWindow? WindowFor(int frameW, int frameH, (double X0, double Y0, double X1, double Y1) visible, int maxSpan);
}
```

- `WindowFor` 已經處理 margin、對齊 256 網格、裁到畫框內。**在同一格內小幅平移會回傳相等的 `PixelWindow`**，靠這點達成「不重算」。

### 1.2 引擎窗口渲染（Task 6）— `src/FilmBatch.Engine/Rendering/`

```csharp
public readonly record struct RenderView(int FrameW, int FrameH, int X, int Y, int W, int H);  // RenderView.cs
public sealed record FrameStats(int FrameWidth, int FrameHeight);                             // RenderView.cs

// IPreviewRenderer.cs（GpuSpikeRenderer、CpuFakeRenderer 都已實作）
RenderResult RenderWindow(SourceImage frameSource, Settings settings, RenderView view, FrameStats stats, CancellationToken ct = default);

// RenderResult.Stats：整張 Render 時填入；RenderWindow 時原樣帶回傳入的那份
public FrameStats? Stats { get; init; }
```

- `frameSource` 是「已縮放到窗口畫框尺寸」的來源。`RenderView.FrameW/FrameH` 必須等於 `CropGeometry.Compute(frameSource.Width, frameSource.Height, crop)` 的 `OutWidth/OutHeight`。
  **不要用 `WindowFrameEdge` 直接當 FrameW**，四捨五入會差 1px，窗口就會錯位。
- `RenderView` 必須完全在畫框內（`IsInsideFrame`），否則 `Validate()` 會丟例外。

### 1.3 來源 — `src/FilmBatch.App.Shared/Services/ImageCache.cs`

```csharp
Task<SourceImage> GetSourceAsync(string path, SourceLevel level, RawLoadOptions rawOptions = default, CancellationToken ct = default, SourceAdmission admission = ...);
bool HoldsFull(string path, RawLoadOptions o);
void ReleaseFullIfIdle();
int PreviewLimit { get; }   // 0 = 自動（2560）
```

- `SourceImage.ScaledTo(int longEdge)`（`src/FilmBatch.Engine/Imaging/SourceImage.cs`）：2 格 LRU，同 edge 回傳同物件。

### 1.4 算圖服務與排程

- `src/FilmBatch.App.Shared/Services/PreviewService.cs`：
  - `long RequestPreview(string fileName, SourceImage source, Settings settings, (int W, int H)? displayFit = null, bool draft = false)` 回傳世代。
  - `IsStalePreview(generation)`、`PostAtEpoch(epoch, action)`：世代與 epoch 圍欄，**窗口要照抄這套**。
  - 事件 `PreviewReady`、`PrefetchReady`；屬性 `LastDelivery`、`IsSoftware`。
- `src/FilmBatch.Engine/Scheduling/RenderScheduler.cs`：
  - 目前有 preview slot（只留最新）、thumbnail 佇列、prefetch lane（Task 4 加的，可當範本）。
  - 送達 record：`PreviewRendered(Key, Generation, Result, Epoch)`、`PrefetchRendered(Key, Result, Epoch)`。

### 1.5 UI

- `src/FilmBatch.App.Shared/Controls/PreviewCanvas.cs`
  - `Render(DrawingContext)`：`dest = ImageRect(LayoutSize(img))` 是影像在畫布上的矩形（DIP）；`RenderScaling` 是 DIP→物理像素倍率。
  - `Mapping().RotationDeg != 0`：裁切工具旋轉顯示中，**這時不要畫窗口**。
  - `IsSplitShown`、`SplitX(dest)`：左右分割對比。
  - 事件 `ViewSizesChanged(drawnLongPx, canvasLongPx)`：只在尺寸變時觸發，**平移不觸發**。
- `src/FilmBatch.App.Shared/ViewModels/PreviewViewModel.cs`
  - `UpdatePlan(nativeLongPx, textureSide, previewLimit, draftScale)` 目前寫死 `WindowsAllowed: false`。
  - `StandIn`（非 null 表示正在顯示替身）、`Source`、`Image`、`ShowOriginal`。
- `src/FilmBatch.App.Shared/ViewModels/MainWindowViewModel.cs`
  - `MainRenderSource(proxy, crop, settings)`（約第 780 行）：Main 層如何縮放來源，**窗口的縮放照這個寫法**。
  - `OnPreviewViewSizesChanged()`（約第 804 行）。
  - `AssumedTextureSide = 8192`、`_draftScale`（拖曳中非 null）、`Images`、`Renderer`、`FocusFile`。

### 1.6 測試範本

- `tests/FilmBatch.App.Tests/LoupeMainRenderTests.cs`：headless Avalonia 開一張大圖、用記錄型 CPU 算圖器數請求數、`TestEnv.PumpUntil(...)` 等待。**ZoomWindowTests 照這個檔的寫法。**
- `tests/FilmBatch.App.Tests/LoupePlanTests.cs`：純函式測試範本。
- `tests/FilmBatch.App.Tests/QuickStandInTests.cs`：替身相關。

---

## 2. Task 7：縮放窗口整合（拆成 7 小步）

目標：放大超過 Main 層 1.25 倍時，另外算「螢幕上看得到的那一塊」，用 Full 來源、最高 100%，疊在 Main 層上面。

### 7.1 `RegionView`（純資料＋純函式）

**新增** `src/FilmBatch.App.Shared/Services/Loupe/RegionView.cs`：

```csharp
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
```

- `SettingsKey`：用一個能代表「目前設定」的 long。先找專案裡有沒有現成的設定雜湊／版本號（`grep -rn "SettingsKey\|ContentHash\|GetHashCode" src/FilmBatch.Core/Params`）。沒有就在 MainWindowViewModel 維護一個「每次送出 Main 請求就 +1」的計數器。

**測試**：`tests/FilmBatch.App.Tests/RegionViewTests.cs`，至少：照片不同→false、畫框不同→false、設定不同且非拖曳→false、設定不同但拖曳中→true。

Commit：`feat(loupe): add RegionView currency rule`

### 7.2 排程器加窗口 slot（Engine）

**修改** `src/FilmBatch.Engine/Scheduling/RenderScheduler.cs`：

- 新增 record：

```csharp
public sealed record WindowRendered(string Key, long Generation, RenderView View, RenderResult Result, long Epoch);
```

- 新增方法 `public void RequestWindow(string key, SourceImage frameSource, Settings settings, RenderView view, FrameStats stats, long generation)` 與 `public void CancelWindow()`。
- 窗口 slot 和 preview slot **分開**，各自只保留最新一筆（新請求覆蓋舊的、未開始的請求）。
- 取工作的優先序：**preview > window > prefetch > thumbnail**。照 prefetch lane 的寫法加。
- 執行時呼叫 `renderer.RenderWindow(...)`，完成後觸發 `event Action<WindowRendered>? WindowRendered`。
- 失敗時走既有 `RenderFailed`，`PreviewGeneration` 填 null（窗口失敗不影響 Main 的 IsBusy）。
- `ResetRenderer` / epoch 改變時清掉窗口 slot。

**測試**：在 `tests/FilmBatch.Core.Tests/RenderSchedulerTests.cs` 加：
- `Window_request_is_rendered_with_render_window`
- `Newer_window_request_replaces_pending_one`
- `Preview_runs_before_pending_window`

Commit：`feat(engine): add window slot to render scheduler`

### 7.3 `PreviewService` 窗口 API

**修改** `src/FilmBatch.App.Shared/Services/PreviewService.cs`：

- `public long RequestWindow(string fileName, SourceImage frameSource, Settings settings, RenderView view, FrameStats stats)`：
  - 世代：窗口用**目前 Main 的世代**（最近一次 `RequestPreview` 回傳的值）當標籤，不要自己遞增 Main 的世代（否則會把 Main 判成過期）。
- 事件 `public event Action<WindowDelivery>? WindowReady;`，`WindowDelivery` 帶 `RenderView`、`RenderResult`、世代。
- 送達時檢查：
  - `PostAtEpoch(r.Epoch, ...)`（epoch 圍欄）。
  - 世代 < 目前照片的第一個世代（`_firstSourceGeneration`）→ 丟掉（換照片了）。
- 窗口**不**算直方圖、**不**做 `DisplayFit` 縮放（窗口以 GPU 原尺寸交給畫布，由畫布縮放到繪製位置）。
- `CancelPreview` 時一併 `CancelWindow()`。

**測試**：`tests/FilmBatch.App.Tests/PreviewWindowServiceTests.cs`：
- `Window_from_previous_photo_is_dropped`
- `Window_after_epoch_change_is_dropped`

Commit：`feat(preview): window requests in PreviewService`

### 7.4 畫布回報可見範圍＋畫窗口

**修改** `src/FilmBatch.App.Shared/Controls/PreviewCanvas.cs`：

1. 新增 StyledProperty：
   - `Bitmap? WindowImage`
   - `PixelRect? WindowRect`（窗口在「畫框像素」座標中的位置）或直接放 `RegionView? WindowRegion` 再加 `bool WindowVisible`（由 VM 判斷 IsCurrent 後設定）。
   - 兩者都加進 `AffectsRender`。
2. 新增事件 `public event Action<double, double, double, double>? VisibleFrameRectChanged;`
   - 在 `Render` 中算：`visible = dest ∩ new Rect(Bounds.Size)`；轉成畫框比例 `u0 = (visible.X - dest.X) / dest.Width` 等四個值（0..1）。
   - 以 0.001 為單位去重後 `Dispatcher.UIThread.Post(...)` 發出（**不要在 Render 裡直接改屬性**，比照 `ViewSizesChanged` 的寫法）。
   - VM 再乘上窗口畫框的 FrameW/FrameH 得到畫框像素座標。用比例而不是像素，是因為 Main 與窗口的畫框尺寸不同。
3. 畫窗口：畫完 Main（以及分割對比的原圖側）之後，若
   - `WindowImage` 不為 null、窗口可見、
   - `map.RotationDeg == 0`、
   - Main bitmap 的長寬比與畫框長寬比相差 `|ratio - 1| ≤ 0.02`（替身的長寬比可能不同，這時不畫），

   就把 `WindowImage` 畫到 `dest` 中對應的位置：
   ```
   x = dest.X + WindowRect.X / FrameW * dest.Width
   y = dest.Y + WindowRect.Y / FrameH * dest.Height
   w = WindowRect.W / FrameW * dest.Width
   h = WindowRect.H / FrameH * dest.Height
   ```
   用 `ctx.PushClip(new Rect(Bounds.Size))` clip 到畫布；分割對比時，After 側的窗口 clip 到分割線右邊。
4. 平移、縮放只改變上面的繪製位置，**不觸發新請求**（新請求只在 VM 算出的 `PixelWindow` 改變時才送）。

這一步先不接 VM，只寫畫布。可以用 `LoupeMainRenderTests` 類似的 headless 測試，直接設 `WindowImage` / `WindowRect`，截圖檢查窗口位置的像素顏色。

Commit：`feat(preview): canvas draws zoom window and reports visible rect`

### 7.5 ViewModel 串接（核心）

**修改** `PreviewViewModel.cs`：

- `UpdatePlan` 的 `WindowsAllowed` 改為由參數傳入。以下情況 `WindowsAllowed = false`：
  - 裁切工具開啟（畫面旋轉／顯示整張未裁切的畫框）；
  - 背景移除編輯中（`BackgroundRemoval.IsEditing`）；
  - AI 降噪啟用中（降噪作用在整張來源，Full 來源降噪太慢；這是建議做法，若決定不關，請在 ledger 寫明原因）；
  - 正在顯示替身（`StandIn is not null`）。
- 新增 `[ObservableProperty] Bitmap? windowImage;`、`RegionView? WindowRegion`、`bool WindowVisible`。
- 新增 `ReportVisibleFrameRect(u0, v0, u1, v1)`：存起來後呼叫 `main.OnVisibleFrameRectChanged()`。
- 換照片（`Clear()`、`SetResult` 換來源時）清掉 `WindowImage` / `WindowRegion`。

**修改** `MainWindowViewModel.cs`，新增 `RequestWindowIfNeeded()`，在以下時機呼叫：Main 結果送達後、`OnPreviewViewSizesChanged`、`OnVisibleFrameRectChanged`。流程：

```text
1. plan = Preview.CurrentPlan；plan.WindowFrameEdge 為 null → 清窗口、標記閒置時間，return
2. full = await Images.GetSourceAsync(path, SourceLevel.Full, rawOptions, token)
      （同一張照片只 await 一次；在途時不要再開第二個）
3. fullFrame = CropGeometry.Compute(full.Width, full.Height, crop)
   fullFrameLong = max(fullFrame.OutWidth, fullFrame.OutHeight)
   frameSource = WindowFrameEdge >= fullFrameLong
       ? full
       : full.ScaledTo(ceil(max(full.Width, full.Height) * WindowFrameEdge / fullFrameLong))
4. frame = CropGeometry.Compute(frameSource.Width, frameSource.Height, crop)   ← 用實際尺寸
5. visible = (u0*frame.OutWidth, v0*frame.OutHeight, u1*frame.OutWidth, v1*frame.OutHeight)
   win = LoupePlan.WindowFor(frame.OutWidth, frame.OutHeight, visible, LoupePlan.MaxSpanFor(AssumedTextureSide))
   win 為 null → return
6. region = new RegionView(path, Before: false, frame.OutWidth, frame.OutHeight, win, settingsKey)
   region 等於上一次「已送出」的 region → return（同格平移不重算）
7. 若 !Images.HoldsFull(path, rawOptions) 而且已經有一個窗口在途 → 記下 pending，等在途的送達後再送（defer_window）
8. stats = 最近一次 Main 結果的 RenderResult.Stats ?? new FrameStats(mainW, mainH)
9. Renderer.RequestWindow(path, frameSource, settings, new RenderView(frame.OutWidth, frame.OutHeight, win.X, win.Y, win.W, win.H), stats)
```

- 收到 `WindowReady`：
  - 若 region 的 `IsCurrent(目前照片, 目前畫框, 目前 settingsKey, drafting: _draftScale is not null)` 為 false → 丟掉。
  - 否則把 `RenderResult` 轉成 Bitmap 設給 `Preview.WindowImage`，設 `WindowRegion`。
  - 若有 pending 的窗口請求，現在送出。
- 拖曳中（`_draftScale` 非 null）：**不送新窗口**，但保留舊窗口顯示（`IsCurrent` 的 drafting 規則）。放手後的完整 Main 送達時再補送窗口。
- 釋放 Full：沒有窗口（`WindowFrameEdge` 為 null 或窗口已清掉）持續 3 秒後呼叫 `Images.ReleaseFullIfIdle()`。用 `DispatcherTimer`，換照片或又需要窗口時取消計時。

Commit：`feat(preview): render the visible zoom window at full detail`

### 7.6 Before 側（原圖對比）

- `ShowOriginal`（按住空白鍵）與 `IsSplit` 的 Before 側：用同一份 Full 來源、同樣的 `frameSource`，`Settings` 換成「原始設定」，`RegionView.Before = true`，另存一份 `OriginalWindowImage`。
- **時間不夠可以跳過**：Before 側先只顯示 Main 層，並在計畫 ledger 寫「已知限制：原圖對比側沒有窗口」。

Commit（若有做）：`feat(preview): zoom window for the original comparison`

### 7.7 `ZoomWindowTests`

**新增** `tests/FilmBatch.App.Tests/ZoomWindowTests.cs`，照 `LoupeMainRenderTests` 的方式開一張大圖（≥ 6000px 長邊的合成圖），用記錄型 CPU 算圖器（記錄 `Render` 與 `RenderWindow` 的呼叫次數與 `RenderView`）：

| 測試 | 做法 | 斷言 |
|---|---|---|
| `Zoom_100_requests_one_window_covering_viewport` | 設 `Zoom = 1`，等待 | `RenderWindow` 被呼叫 1 次；`RenderView` 涵蓋畫布可見範圍 |
| `Pan_within_snap_cell_does_not_rerender` | 100% 後把 `Pan` 移 20px | `RenderWindow` 次數不變 |
| `Pan_across_cell_requests_new_window` | 100% 後把 `Pan` 移 > margin + 256px | 次數 +1 |
| `Window_is_dropped_after_photo_switch` | 送出窗口後立刻切下一張（讓算圖器卡住再放行） | 新照片畫面上沒有舊窗口（`WindowImage` 為 null 或屬於新照片） |
| `Second_window_waits_until_full_source_is_held` | Full 載入卡住時連續平移兩次 | 送進算圖器的窗口請求只有最後一個 |
| `Full_source_released_when_idle_without_window` | 100% → 回到符合視窗，等 > 3 秒 | `Images.HoldsFull(...)` 變 false |
| `Drafting_keeps_previous_window_visible` | 有窗口時開始拖曳滑桿 | 拖曳中 `WindowVisible` 仍為 true、沒有新的 `RenderWindow` |
| `Window_not_drawn_over_mismatched_aspect_stand_in` | 替身顯示中且長寬比不同 | 畫布不畫窗口（截圖該區域等於 Main 的像素） |

最後跑完整 App 測試與 Core 測試。

---

## 3. Task 8：文件、整體驗證與證據

### 8.1 架構文件

修改 `docs/architecture.md`：
- **4.6 Engine．算圖管線**：補 `RenderView` / `FrameStats` / `RenderWindow` 與「窗口結果等於整張渲染後裁切」的契約（容差 max ≤ 1、mean < 0.05 LSB；測試在 `WindowRenderEquivalenceTests`）。
- **4.7 Engine．算圖排程**：slot 與優先序（preview > window > prefetch > thumbnail）、世代與 epoch 圍欄。
- **4.10 App．介面層**：新增「Loupe 預覽」小節，內容：
  - Main 層：畫布物理長邊、64px 一級、上限 Preview 等級 2560（`AppSettings.PreviewLongEdge`，0 = 自動）；Lanczos `DisplayFit` 縮到繪製尺寸，畫布以 1:1 繪製。
  - Window 層：繪製尺寸 ≥ Main × 1.25 時出現；256 網格、margin、MaxSpan 6144；Full 來源一張照片一份、閒置 3 秒釋放。
  - 三級來源 Thumb / Preview / Full。
  - 替身順序：ViewRenderCache → 內嵌 JPEG → 縮圖；替身不進直方圖、工具、匯出。
  - DragScale：16ms 預算、0.6 草稿、Suspect/Slow、30 秒重試倍增到 240 秒。
- **6. 測試對照**：加上 LoupePlanTests、LoupeMainRenderTests、QuickStandInTests、DragScaleTests、DragDraftTests、WindowRenderEquivalenceTests、ZoomWindowTests、RegionViewTests。

文字比照文件現有風格（繁體中文、短句）。

### 8.2 整體測試

```bash
dotnet test FilmBatch.sln -c Release
```

```bash
dotnet run build/Build.cs -- --rid win-x64 --smoke
```

- `--rid` 用目前平台（Windows 是 `win-x64`）。檢查輸出的 smoke receipt 是否成功。
- 有安裝 naga 的話再跑 `dotnet run build/CheckShaders.cs`；沒有就在 ledger 註明「未跑 CheckShaders」。

### 8.3 量測（觀察值，不是保證）

用 demo 照片的**複本**（不要直接開原資料夾，app 會在照片資料夾寫 `.filmbatch`）：

```bash
cp -r /c/Users/minex/Downloads/demo /c/Users/minex/AppData/Local/Temp/filmbatch-demo-copy
```

也不要動 `%APPDATA%\FilmBatch`。

記錄（同一組輸入、同一組設定）：
- 符合視窗時 Main 算圖時間
- 100% 窗口算圖時間
- 平移 10 次時實際算圖次數
- 開圖到替身、開圖到最終畫面的時間
- 峰值記憶體（`GC.GetTotalMemory(true)`）

### 8.4 更新 ledger 並 commit

在 `docs/superpowers/plans/2026-10-23-lightcraft-style-loupe.md` 最後的 State Ledger：
- `Task 7` 一列：commit hash、測試數字、已知限制（例如 Before 側沒有窗口）。
- `Task 8` 一列：Release 測試數字、smoke 結果、量測數字。

Commit：`docs: record Lightcraft-style loupe architecture and evidence`

---

## 4. 常見陷阱

- **座標系搞混**：畫布是 DIP；`RenderScaling` 轉物理像素；窗口是「畫框像素」（裁切後、縮放到 `WindowFrameEdge`）。畫布和 VM 之間用 0..1 比例傳遞最安全。
- **畫框尺寸**：永遠用 `CropGeometry.Compute(source.Width, source.Height, crop)` 的實際結果，不要自己用比例算。
- **在 `Render` 裡改屬性**：會無限重繪。一律 `Dispatcher.UIThread.Post`。
- **每次平移都 `ScaledTo` 不同 edge**：edge 只取決於 `WindowFrameEdge` 與裁切，平移時不會變；若發現 edge 在跳，代表算錯了。
- **把窗口的世代當成新的 Main 世代**：會讓正常的 Main 結果被判成過期，畫面卡住。
- **測試卡住**：headless 測試等待一律用 `TestEnv.PumpUntil(條件, 逾時毫秒)`，不要 `Thread.Sleep`。
- **GPU 測試**：沒有 adapter 時用既有慣例 skip 並註明原因，不要讓它靜默通過。

## 5. 遇到不確定時

- 設計決策以 Lightcraft v0.13.0 為準：<https://github.com/storytold/lightcraft/tree/v0.13.0>（`crates/ui-egui/src/region.rs`、`crates/engine/src/media.rs`、`docs/gpu-pipeline.md`）。
- 原計畫全文：`docs/superpowers/plans/2026-10-23-lightcraft-style-loupe.md`（Task 7、Task 8、Review Rejection Criteria 三節）。
- 做不到的就停下來，在 ledger 寫清楚卡在哪裡，不要用放寬容差或跳過測試的方式交差。
