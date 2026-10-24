# Loupe 放大問題修正交接（給本地模型）

接續 `2026-10-24-loupe-task7-8-handoff.md`。Task 7–8 已完成並推到 `origin/research/performance-improve`（`413519e`）。
放大使用後發現三個問題，這份文件說明現象、原因分析、修法與驗證方式。

> **重要：「分析」與「已驗證」不同。** 下面每個原因都標了信心程度。**先加診斷日誌確認原因，再動手改**；不要憑推測直接大改。

## 0. 工作規則

- **不要使用 `git worktree`**（使用者明確要求）。在一般 checkout 上工作。
  - 分支 `research/performance-improve` 目前被 `.claude/worktrees/loupe` 這個既有 worktree 佔用，`git switch` 會失敗。請**先告訴使用者**，由使用者決定移除該 worktree（`git worktree remove .claude/worktrees/loupe`）後再切換。不要自行建立新的 worktree。
  - 主 checkout 目前在 `dev-ai-denoising`，有別的工作在用；切換前先 `git status` 確認沒有未提交的修改。
- 每個問題各自 commit，**修好並驗證後**才 commit；不要自行 push，等使用者說可以。
- 沿用 `2026-10-24-loupe-task7-8-handoff.md` 第 0.3 節的規則（尤其：不得放寬 `WindowRenderEquivalenceTests` 容差、`ScaledTo` 必須回傳同一個物件、窗口不得送達新照片）。
- 註解與 XML doc 用繁體中文；新增的使用者文字同時提供 zh-TW 與 en。
- 建置與測試：

```bash
dotnet build FilmBatch.sln -c Debug
```

```bash
dotnet test tests/FilmBatch.App.Tests -c Debug --filter "FullyQualifiedName~Loupe|FullyQualifiedName~ZoomWindow"
```

- 已知的既有失敗：`DragDraftTests.Cpu_drag_renders_0_6_draft_and_returns_to_full_plan_size_on_release` 與 `LoupeMainRenderTests` 同批執行時會失敗（`e238417` 基準上就會，與本次無關）。不要把它當成你的修改造成的。

## 1. 問題 A：放大時窗口層吃 CPU，不是 GPU

**信心：高（讀程式碼推算，尚未在使用者機器上實測）。**

### 原因

`GpuSpikeRenderer.RenderWindow`（`src/FilmBatch.Engine/Rendering/GpuSpikeRenderer.cs`）一開始呼叫 `_pipeline.CanRender(frameSource, parameters)`。沒通過就設 `_cpuReason`，整個窗口改走 `CpuPipeline`。

`GpuEffectPipeline.CanRender`（`src/FilmBatch.Engine/Rendering/GpuEffectPipeline.cs` 約第 131 行）的估算：

```text
bytes = WorkspaceBytes(窗口區域 W×H)        // W×H×64 + band 緩衝
      + CachedSource.BytesFor(來源 寬×高)    // 寬×高×16
bytes ≤ _budget（預設 768 MB；或設定的 GPU 記憶體上限）
```

以 A7M4（7008×4672）為例：

| 項目 | 大小 |
|---|---|
| Full 來源貼圖（32.7 MP × 16 B） | 約 524 MB |
| 預算 768 MB 扣掉來源後剩下 | 約 244 MB |
| 能放的窗口 | 約 380 萬像素（約 1950×1950） |
| 一般 100% 窗口（約 1600×1000 可見範圍 + 每邊 512 margin = 2624×2024） | 約 530 萬像素 ≈ 340 MB → **超過預算** |

所以窗口幾乎一定退回 CPU。Main 層只有約 2560，不受影響，所以只有放大的窗口層慢。

### 其他在 CPU 上、本來就存在的部分（不是這個 bug，但讓放大更吃 CPU）

- Full 解碼：JPEG 解碼、RAW demosaic（AHD）。
- `SourceImage.ScaledTo` → `Resize`：Full 縮到窗口畫框用的 CPU box filter（33 MP 逐像素迴圈）。
- `WindowRegion.CropBgra / CropToBgra8`、BGRA → Bitmap。

### 修法（依序嘗試，做到可用就停）

**1. 修正 `CanRender` 的窗口估算（先做這個，最小改動）。**
- 窗口模式（`parameters.View` 非整張）下，工作空間只算 `WindowRegion.Expand` 後的區域，不是整個畫框。確認 `OutputSize(source, p)` 在窗口模式下回傳的是區域尺寸（Task 6 讓 `OutWidth/OutHeight` 回傳 `W/H`）。
- 來源貼圖已經在 `_sources` 快取裡（同一個 `SourceImage` 參考）時，不要再把它的位元組算進「這次需要配置的量」；改成檢查「快取總量 + 新工作空間」是否 ≤ 預算，並在必要時先逐出別的來源。
- 加單元測試：給定預算與來源尺寸，窗口估算在「來源已快取」時通過、在「來源未快取且過大」時不通過。

**2. 來源貼圖本身就超過預算時，不要整張上傳。**
- 窗口只需要區域內的來源像素（加上各 pass 的外圍，`WindowRegion.Expand` 已算好）。評估只上傳來源的子矩形，並讓來源紋理座標加上區域偏移。這會動到 `GpuEffectPipeline.Acquire` 與取樣座標，風險較高，需要 `WindowRenderEquivalenceTests`（GPU 與 CPU）全部仍然通過。
- 若評估後覺得風險太大，在 ledger 寫「未做，原因…」，不要硬做。

**3. 窗口放不下時，切成較小的幾塊分次渲染，不要整個退回 CPU。**
- 在 `RenderWindow` 內：若 `CanRender` 不通過，把 `view` 沿較長邊對半切（保持 256 網格對齊與 margin），分次呼叫 GPU，再把結果拼回 `view.W × view.H`。每一塊都要重新算 `WindowRegion.Expand`。
- 拼接後的結果必須與整張渲染後裁切一致（容差不變）。
- 只有切到最小塊仍放不下，才退回 CPU，並在日誌印出原因與預估大小。

### 診斷（先做）

在 `RenderWindow` 退回 CPU 時用 `AppLog.Warn` 印出：來源尺寸、區域尺寸、預估位元組、預算、是否已快取。使用者再放大一次就能看到是不是這個原因。

### 驗證

- 單元測試如上。
- 實機：複製 demo 照片（不要在原資料夾開，app 會寫 `.filmbatch`），放大到 100% / 200%，確認日誌沒有再退回 CPU、工作管理員 CPU 使用率明顯下降、算圖時間下降（記錄前後的 `RenderResult.Elapsed`）。

## 2. 問題 B：放大後不會變清晰，要移動才會清晰

**信心：中低。** 已經排除了部分路徑（見下），真正原因需要日誌確認。

### 觀察與已確認的事實

- 縮放與平移走的是同一條路徑：畫布 `Render` 內的 `ReportVisibleFrame` 會 `Post` 出 `VisibleFrameRectChanged`，VM 的 `RequestWindowIfNeeded` 算窗口並送出。
- 縮放時 `ReportVisibleFrame`（可見範圍）**先於** `ViewSizesChanged`（繪製尺寸）送出，所以第一次 `RequestWindowIfNeeded` 看到的是**舊的 plan**（縮放前通常沒有窗口 → `ClearWindow()`），第二次（`OnPreviewViewSizesChanged`）才看到新 plan。兩次之間若有任何一個被 `_windowLoading` 擋成 pending 或被早退，就可能沒送出。

### 候選原因（由可能性高到低）

1. **窗口請求在途時間太長，使用者以為沒有反應（與問題 A 相關）。** 第一次放大要先 `GetSourceAsync(Full)`（ARW 的全尺寸 demosaic 要數秒），之後窗口又退回 CPU 算圖。這段時間畫面只有 Main 層（被拉大），看起來就是「不清晰」。移動之後剛好前一個請求完成，所以顯得「移動才清晰」。**先修問題 A，再看是否還存在。**
2. **窗口送達後被丟棄。** `OnWindowReady`（`MainWindowViewModel.Loupe.cs`）有兩個丟棄條件：`delivery.View != ViewOf(region)`，以及 `region.IsCurrent(...)` 為 false。
   - 每次 `OnMainPreviewRequested` 都 `_settingsKey++`。若縮放或 Refit 之後又觸發了一次 Main 請求，在途窗口的 `SettingsKey` 就過期而被丟棄；丟棄時 `_inFlightRegion = null`，但 `_sentRegion` 仍是舊的。之後如果算出的 `RegionView` 恰好與 `_sentRegion` 相等（`SendWindowAsync` 的 `if (region == _sentRegion) return;`），就**不會重送**。
   - `RefreshWindowVisibility` 與 `OnWindowReady` 呼叫 `IsCurrent` 時，畫框參數傳的是 `(region.FrameW, region.FrameH)`（region 自己的畫框），這是恆真比較，**沒有真正檢查畫框是否與目前相同**。
3. **ViewSizes 與 VisibleFrame 的先後順序**造成用舊 plan 判斷，且之後沒有補送（只要兩次判斷都落在 `_windowLoading` 或 `WindowsAllowedFor` 為 false 的瞬間）。
4. **窗口還沒送達前，Main 層被 `DisplayFit` 重新縮放到非常大的尺寸。** `Renderer.Refit((drawn, drawn))` 對放大後的繪製尺寸做 Lanczos（在 CPU 上），也是放大時 CPU 偏高的另一個來源。請確認 `DisplayFit.Fit` 在目標尺寸大於來源時的行為；放大超過 Main 層時，Main 層不應被放大到繪製尺寸（窗口層才負責細節）。

### 修法

**B1. 診斷日誌（先做）。** 在 `MainWindowViewModel.Loupe.cs` 的以下位置加 `AppLog.Debug`（沒有 Debug 就用 Warn，確認後再降級）：
- `RequestWindowIfNeeded`：每個早退分支各一行（原因：draft / loading→pending / 無 plan / 無 VisibleFrame / 不允許 / 其他）。
- `SendWindowAsync`：進入、Full 取得完成（耗時）、算出的 `RegionView`、因 `region == _sentRegion` 或 `key != _settingsKey` 早退的原因、實際送出。
- `OnWindowReady`：送達、因 View 不符被丟、因 `IsCurrent` 為 false 被丟（含兩邊的 key）、成功顯示。
- 請使用者放大一次、再放大一次，把日誌貼回來。

**B2. 修正 `IsCurrent` 的畫框參數。** 在 `RefreshWindowVisibility` 與 `OnWindowReady` 中，畫框要傳**目前** Main 畫框推算出的窗口畫框尺寸，不是 `region` 自己的。做法：另存目前期望的窗口畫框（例如 `_currentWindowFrame`，在 `SendWindowAsync` 算出 `frame.OutWidth/OutHeight` 時更新），比較時用它。

**B3. 被丟棄的窗口要能重送。** `OnWindowReady` 因 `IsCurrent` 為 false 丟棄時，同時把 `_sentRegion = null`，再呼叫 `RequestWindowIfNeeded()`，讓新設定世代下的窗口重新送出。

**B4. 縮放時補送。** 在 `OnPreviewViewSizesChanged` 與 `OnVisibleFrameRectChanged` 之後，若 `Preview.CurrentPlan?.WindowFrameEdge` 有值但 `Preview.WindowImage` 為 null 且沒有在途請求，確保一定送出一次（不要只依賴兩個事件的先後順序）。

**B5. 放大期間的視覺回饋。** 窗口在途時讓 `IsBusy` 或轉圈圖示顯示，使用者才知道在算。若不做，在 ledger 註明。

### 測試

在 `tests/FilmBatch.App.Tests/ZoomWindowTests.cs` 加：
- `Zoom_in_from_fit_requests_window_without_panning`：從符合視窗直接設 `Zoom = 1`，不平移，等待後 `RenderWindow` 被呼叫且 `Preview.WindowImage` 不為 null。
- `Window_dropped_by_stale_settings_key_is_resent`：窗口在途時觸發一次 Main 請求，結果應該重新送出窗口，最終畫面有窗口。
- `IsCurrent_checks_the_current_frame`：目前畫框改變後，舊窗口不得被視為 current。

## 3. 問題 C：ARW 影像邊緣出現馬賽克／色條

**現象：** 放大後，影像左緣與下緣出現有顏色的條紋。左緣是橫向的色條（每一列顏色在水平方向延伸），下緣是直向的色條（每一欄顏色向下延伸），寬度約 10–20 個來源像素。看起來像「邊緣像素被複製拉伸」。

**信心：低。以下是要驗證的假設，不是結論。**

### 先釐清的事

1. 只有 ARW 嗎？JPEG 同一張的位置有沒有？
2. 符合視窗（只有 Main 層）時，邊緣有沒有？（如果沒有，代表只出現在窗口層或 Full 來源。）
3. 用 `Render16` 匯出全尺寸圖，邊緣有沒有？（如果有，是 RAW Full 載入的問題；如果沒有，是窗口渲染的問題。）

### 假設（依可能性）

1. **Full 路徑與 Preview（binned）路徑對邊緣的處理不一致。**
   - `RawSourceLoader.DevelopScene`（`src/FilmBatch.Engine/Imaging/Raw/RawSourceLoader.cs` 約第 206–260 行）：Preview 走 `DevelopBinned`（含 `raw.BinnedRegion(k)` 與 `ApplyLensChainBinned`），Full 走 `RawSourceGeometry.Develop(raw, DemosaicMethod.Ahd)`。
   - Sony ARW 內嵌失真校正（`EmbeddedVignette`／`list3`）。鏡頭失真校正會從來源外取樣，如果邊緣是**夾取（clamp to edge）**而 Binned 路徑有裁掉無效區域、Full 路徑沒有，就會出現「邊緣像素被拉伸」的條紋。
   - 檢查：兩條路徑在相同畫框下，邊緣 20 個像素的內容是否一致；`raw.Crop` / `raw.ActiveArea` 在兩條路徑是否都套用。
2. **AHD demosaic 的邊緣。** `src/FilmBatch.Raw/Develop/Ahd.cs` 的 tile 以 `Reflect` 鏡射處理邊界，看起來是正確的，可能性較低；但仍要用只有 AHD（關閉鏡頭校正）的 RAW 驗證。
3. **窗口渲染在畫框邊緣的處理。** `WindowRegion.Expand` 夾在畫框內，邊界取樣由 pass 本身夾取；`WindowRenderEquivalenceTests` 目前只用合成圖，沒有真實 RAW 的邊緣。若問題只出現在窗口層，檢查 `view.X == 0`、`view.Y + view.H == FrameH` 時的各個 pass（特別是鏡頭失真、暈影、降噪）。
4. **`ScaledTo` / `Resize` 對 `Scene`（RAW 場景資料）的 box 縮放**在邊緣用了錯的範圍。

### 診斷步驟

1. 把使用者的 ARW 複本（不要在原資料夾開）以 `RawSourceLoader` 載入 Preview 與 Full 兩個等級，輸出各自邊緣 64×64 的 PNG，肉眼比較。
2. 用 `CpuFakeRenderer.Render` 整張渲染 Full，與 `RenderWindow` 在左上角、右下角窗口的結果比較（加進 `WindowRenderEquivalenceTests` 的真實 RAW 案例，沒有 RAW 時略過並註明）。
3. 若 Full 本身邊緣就有色條：檢查假設 1；若只有窗口有：檢查假設 3。

### 修法

依診斷結果決定；**不要在確認原因前改 shader 或 demosaic**。可能的修法：
- 假設 1：讓 Full 路徑與 Binned 路徑使用同一份有效區域（`ActiveArea` ∩ `Crop`），或對失真校正後的無效邊緣做裁切／遮罩，保持 `ReferenceWidth/Height` 與兩條路徑一致。
- 假設 3：修正對應 pass 在畫框邊緣的取樣（以畫框座標 clamp，不是以窗口紋理座標 clamp）。

### 驗證

- 同一張 ARW 在 Preview 與 Full 兩個等級的邊緣一致（容差同 Global Constraints）。
- 新增的真實 RAW 邊緣測試通過。
- 實機：放大到 270% 平移到四個邊緣，沒有色條。

## 4. 建議順序

1. B1 診斷日誌、A 的診斷日誌（一次 build 加完，請使用者各跑一次）。
2. 問題 A 修法 1（`CanRender` 估算）→ 看問題 B 是否消失。
3. 問題 B 的 B2、B3、B4（無論 B 是否消失，`IsCurrent` 恆真是明確的缺陷，應該修）。
4. 問題 C 診斷，確認原因後再修。
5. 修法 2、3（較大的 GPU 改動）只在修法 1 不夠時才做。

## 5. 完成後

在 `docs/superpowers/plans/2026-10-23-lightcraft-style-loupe.md` 的 State Ledger 加一列，記錄：
- 每個問題的確認原因（日誌或測試證據）、修法、commit hash；
- 沒做的項目與原因（例如修法 2、B5）；
- 測試數字（App、Core）。
