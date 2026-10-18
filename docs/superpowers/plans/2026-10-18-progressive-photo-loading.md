# Progressive Photo Loading Implementation Plan

> **For agentic workers:** Follow this bounded plan task-by-task. The parent owns integration, verification and commits; implementation workers skip builds, tests, lint and formatters mid-flight. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 大量 RAW／JPG 資料夾載入時顯示真實進度及耗時、優先目前照片與可見縮圖，並提供可即時調整的背景 CPU 軟性目標。

**Architecture:** 保留 Project.Open 的檔名列舉及所有 RAW 顯影演算法。Filmstrip 以有界工作泵取代逐檔 fire-and-forget；ImageCache 分開前景預覽與背景縮圖的入場控制，單次建立同鍵解碼並在資料夾換代時取消舊等待。以程序 CPU 時間相對全部邏輯核心的比例，在背景工作之間合作式讓出時間；不暫停 UI 執行緒、不設定全域 ThreadPool 上限、不宣稱硬性 CPU 上限。

**Tech Stack:** .NET 10、Avalonia 12.1.4、CommunityToolkit.Mvvm、現有 ImageLoader／RenderScheduler、xUnit v3；不新增相依套件。

**Spec:** 本文件的需求及驗收表，來源為使用者的大量照片載入回報與追加 CPU 降速選項要求。

## Global Constraints

- 保留舊專案可用性：目標開啟失敗時不得清除目前專案、預覽或編輯；既有 crop/undo/save 語意不變。
- 不寫入原始照片、不讀使用者照片作實驗；使用隔離偏好設定與產生的 JPEG/DNG。
- 不修改 RAW 演算法、預覽/匯出色彩契約或用嵌入 JPEG 靜默降級。
- CPU 設定為 10–100%，預設 70%；100% 不主動節流。70% 是合作式背景目標，以全機邏輯核心為分母；單張不可中斷解碼可能短暫超出。
- 關閉／切換資料夾取消排隊與節流等待，舊成功及失敗均不得污染新狀態。已開始的同步 native/RAW 解碼最多完成當前有界工作，不能硬殺。
- 目前照片不被背景節流或所有舊縮圖阻塞；不為每一張照片建立一個等待 Task。
- 縮圖完成數以成功送達 bitmap 或失敗處理為準，不能把開始排隊當成完成；失敗單獨計數。
- 設定值與說明同步提供 zh-TW/en；沿用現有偏好設定與背景工作 flyout。
- 已提交的文件保留；此功能驗證完成後獨立提交，不 push。

## Acceptance

| ID | Observable behavior | Verification |
|---|---|---|
| LOAD-01 | 掃描資料夾時有可見狀態；之後顯示縮圖完成／總數、失敗數與持續更新的耗時，結束不再忙碌 | MainWindowViewModel progress regression; actual desktop capture during blocked/active/completed load |
| LOAD-02 | 目前照片與捲到的可見縮圖優先；大量載入不累積逐檔解碼 Task | Real cache admission and actual filmstrip scroll scenario; 2,000 mixed generated JPEG/DNG observations |
| LOAD-03 | 切換／取消／關閉停止舊排隊；舊讀檔錯誤不污染新專案；壞檔仍明確顯示 | Queued decode cancellation regression; same-name folder switch and corrupt file scenarios |
| CPU-01 | 背景 CPU 目標含 70%，可在載入中調整、持久化，下次開啟仍保留 | Live settings desktop interaction and settings round-trip; controlled CPU-heavy pacing experiment |
| SAFE-01 | 原有 crop/undo/source-option cache、預覽、匯出仍正確 | Existing lifecycle/preferences/RAW tests, solution suite, packaged macOS smoke |

### Additional RAW white balance requirements

User added these while loading work was in progress; Nikon HE/HE★ remains read-only confirmation, not decoder implementation.

| ID | Observable behavior | Verification |
|---|---|---|
| WB-01 | Custom Kelvin/tint changes update preview at ordinary values; continuous adjustments display completed frames for the same source without accepting frames from another photo or a canceled request | Held real-CPU renderer delivery regression and actual native RAW controls |
| WB-02 | Custom Kelvin is 2500–9900 K in sliders/numeric input, JSON/project and YAML presets, and renderer inputs; legacy custom values normalize; source as-shot metadata remains untouched | Bound input and persistence regressions; native small-step edits, undo/save/reopen and source metadata/hash checks |

Keep the independent relative `basic.temperature` effect and the RAW white-balance/color algorithms unchanged. Only the custom setting boundary and UI delivery policy change.

## Work Slices and Interfaces

### Task 1: Cache admission and background CPU pacing

**Owner files:** `src/FilmBatch.App/Services/ImageCache.cs`; focused cache regressions in `tests/FilmBatch.App.Tests/BulkLoadingTests.cs` (parent first writes baseline tests); one small pacing implementation in Services only if needed.

**Consumes:** Existing `ImageLoader.Load`, `RawLoadOptions`, `FolderGeneration`, real decoder admission semaphore `_decodeGate` used by the existing deterministic RAW race regression.

**Produces:** Existing `GetPreviewAsync(string, RawLoadOptions = default)` and `GetThumbnailSourceAsync(string, RawLoadOptions = default)` remain source-compatible. Both may accept a final optional `CancellationToken` for waiter cancellation; shared cache tasks must not be poisoned by one canceled waiter. `SetBackgroundCpuPercent(int)` applies to pending waits without resetting image content or renderer. Add `WaitForBackgroundAsync(CancellationToken = default)` and `RecordBackgroundWork(TimeSpan cpuTime, TimeSpan elapsed)` only if the pacing needs to span thumbnail render as well as decode; parent/filmstrip consume the settled interface, not an invented second scheduler.

- [x] Parent ran the baseline regressions before implementation: queued stale decode opened the removed file; decoding status incorrectly reported ready.
- [x] Replaced side-effectful `ConcurrentDictionary.AddOrUpdate` factories with lock-owned single-flight entries and separate foreground/background admission.
- [x] Linked work to folder lifetime and checked cancellation before/after decode. Repaired faulted entries remain retryable; preview identity includes path, size and RAW options.
- [x] Measured process-normalized CPU and cooperatively paced background admission; percentage changes wake pending waits. Background decode uses one slot; no global ThreadPool cap.
- [x] Integrated settled APIs, waiter-only cancellation, background-only cancellation and in-flight-safe shutdown; parent executed all checks.

### Task 2: Bounded filmstrip pipeline, viewport priority and counters

**Owner files:** `src/FilmBatch.App/ViewModels/FilmstripViewModel.cs`, `src/FilmBatch.App/Views/FilmstripView.axaml.cs`; new focused priority/cancellation tests if needed, but do not edit the parent baseline test file while Task 1 owns it.

**Consumes:** Cache methods above; parent still calls `Load(Project)`, `RequestThumbnail(string)`, `SetThumbnail(string, RenderResult)`, `SetError(string,string)`.

**Produces:** `TotalThumbnails`, `CompletedThumbnails`, `FailedThumbnails`, `IsLoadingThumbnails`, `LoadingElapsed` (TimeSpan), `CancelLoading()`, `SetVisibleRange(int first,int last)`; an event `LoadingProgressChanged` (Action) lets parent refresh status. `StopLoading()`/Dispose may be used instead only if parent is told exact interface before integration.

- [x] Replaced per-photo fire-and-forget with a bounded pump: focus, realized range, explicit requests, then folder order; all initial photos eventually reach a terminal result.
- [x] Read `VirtualizingStackPanel.FirstRealizedIndex`/`LastRealizedIndex` after layout, preserving filtering, multi-select and context-menu behavior.
- [x] Deduplicated initial/edit work; requeued current RAW options and waited for render delivery before advancing. Completed thumbnails can be explicitly refreshed without reviving canceled bulk scanning.
- [x] Counted initial terminal display delivery once, excluded foreign keys, canceled old pumps and rejected stale results/errors. Cancellation does not create a failure badge.
- [x] Integrated final APIs and exercised priority, cancel, reset/refresh, same-name folder replacement and corrupt-file behavior.

### Task 3: Main window progress, preferences and integration (parent)

**Files:** `src/FilmBatch.App/ViewModels/MainWindowViewModel.cs`, `src/FilmBatch.App/Views/MainWindow.axaml`, `src/FilmBatch.App/Services/AppSettings.cs`, `src/FilmBatch.App/ViewModels/Dialogs/PreferencesViewModel.cs`, `src/FilmBatch.App/Dialogs/PreferencesDialog.axaml`, `src/FilmBatch.App/Localization/Strings{,.en}.resx`, focused tests.

**Interfaces:** Main owns scan generation and elapsed timer, subscribes Filmstrip.LoadingProgressChanged, calls ImageCache.SetBackgroundCpuPercent and Filmstrip.CancelLoading. New observable `BackgroundCpuPercent` immediately updates Settings and cache; preference VM applies it live without rebuilding GPU. A loading cancel command invalidates pending open and queued thumbnails, retaining installed project/preview.

- [x] Added a real-admission-held baseline status regression; no thumbnail completion is required to report active loading.
- [x] Tracked scan generation and elapsed time; preserved failed-open state and explicit canceled/error/completed counts.
- [x] Added progress bar, counts/elapsed, cancel and live 10–100% CPU controls in existing flyout/preferences. Native captures exposed numeric text clipping; widened both inputs and verified visible 70/100 values.
- [x] Verified focused cases, actual desktop 2,000-photo scenarios, final solution suite and packaged macOS smoke; a clean ZIP extraction also passed signature, GUI smoke and clean-PATH Metal CLI checks.
- [x] Updated existing architecture/building/usage docs and recorded verified feature commit `eac234b`; final evidence and decision history accompany this ledger. No push.

## State Ledger

| Item | Status | Evidence |
|---|---|---|
| Existing documentation commit | verified | `2beb481`; no push |
| Root cause | inspected | Filmstrip.Load starts one async task per photo; cache shares admission; Main status only reads PendingThumbnails |
| LOAD-01 / LOAD-02 / LOAD-03 | verified | Actual desktop: 2,000 generated mixed JPEG/DNG, 21 checks; priority, cancel retaining edits/preview, folder replacement, bad/empty folders, truthful counts, failed refresh continuing later edits and recovery without reopening |
| CPU-01 | verified | Actual 30/70/100 live controls and saved setting; no GPU reset. Real RAW decode experiment on eight logical cores: 20% target observed 20.34%; 70% observed 29.12%; unpaced observed 36.83%; not a hard cap or utilization promise |
| SAFE-01 | verified | Final solution: 1,213 passed, 315 external-corpus/oracle skips, zero failed (Core 426/22 skipped, Raw 618/293, App 169/0). Restart disposal and six render-failure regressions failed before repair and passed afterward. Final packaged and clean-extracted GUI each passed 25 checks |
| WB-01 | verified | Held real-CPU delivery/cancel/source-switch and failure-origin regressions; nine final native RAW checks, ordinary 5500→5600 K update observed in 27.89 ms, sustained edits, exact undo/redo and save/reopen |
| WB-02 | verified | Schema/UI, JSON/project, YAML read/write and direct renderer-input bounds 2500–9900 K; legacy values normalize. Source as-shot metadata and original-file hash unchanged |
| Consumer map | defined | Main/filmstrip/cache; presets/film-look direct thumbnail cache users; settings JSON; both languages; original RAW lifecycle gate regression; docs/package |

### Observed Desktop Run

Generated fixtures only; Apple M3 / Metal, eight logical cores. Final bulk run used a fresh isolated preference profile; filesystem caches were not cleared. These are observations, not performance thresholds or real-camera-corpus coverage.

| Scenario | Observation |
|---|---|
| First foreground preview | 193.2 ms |
| Visible thumbnail near index 1800 | 245.6 ms; only 12 initial thumbnails had completed |
| Select final DNG | 285.7 ms; only 17 initial thumbnails had completed |
| Finish all 2,000 thumbnails | 26.96 s; 2,000/2,000, zero failures |
| Process CPU across all logical cores during full run | 25.63% |
| Source-file preservation | Four representative JPEG/DNG SHA-256 checks plus the independent WB source unchanged |

Prior CPU/cache and RAW source-option tests failed before their fixes. The full solution also exposed repeated ViewModel disposal canceling a disposed waiter; the existing restart test passes unchanged after an idempotence guard. Independent Kelvin review found no code defect. Two final reviews found four related failure-delivery issues: retained-bitmap errors left the pump waiting, missing renderer returned no per-key event, background failures consumed preview delivery, and preview failures poisoned pending thumbnail rows. Parent reproduced all six corresponding cases failing, then passed them with source-aware `RenderFailure` metadata, generation fencing and terminal delivery. Native Metal also proved failed refresh/next-edit/recovery. Nikon HE/HE★ was confirmation only, with no decoder change.

The final Apple Silicon app and ZIP are in `dist/osx-arm64/FilmBatch.app` and `dist/FilmBatch-osx-arm64.zip` (138,171,625 bytes; SHA-256 `466b96151221eff077eef6d152f34e0d55b6d965a7b14a4b5f57684957adeb00`). Final ZIP integrity and deep/strict signature verification passed after fresh extraction; self-contained GUI/CLI ran with a system-only PATH and Metal. Signing remains ad-hoc, without Developer ID/notarization. Windows runtime and external RAW corpus/oracle checks were not performed. Disposable fixtures/runners are not required by the committed regressions; ignored `dist/progressive-loading-acceptance.json` retains detailed observations and capture paths.

## Review Rejection Criteria

Reject a fake hard-70% claim, global ThreadPool cap, per-file unbounded async tasks, a preview waiting behind the entire background queue, progress based only on render queue length, stale folder faults affecting current state, option changes resetting image edits/GPU, silent JPEG substitution for RAW, and tests which only assert implementation text or private queue sizes. CPU timing evidence is observational, not a full-system guarantee.
