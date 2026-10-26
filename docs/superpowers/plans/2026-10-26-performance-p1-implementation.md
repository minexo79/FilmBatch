# Performance P1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 依已完成的本機實驗，分三個可獨立驗收／提交的 P1 項目，顯示真實 AI 像素保留量並降低換來源時舊結果與新推論的重疊。

**Architecture:** 沿用 `DenoiseProcessor` 的 full／thumbnail／四尺寸預覽輸入與 `SourceImage` 的既有所有權。查詢回傳值型別快照，重用去重集合且查詢後清空；既有 RAM 區塊顯示明細。全品質 cache miss 僅在序列推論閘門內、確認取消狀態後釋放舊 full；不建立新的快取框架。

**Tech Stack:** .NET 10、ONNX Runtime、Avalonia 12.1.4、CommunityToolkit.Mvvm 8.4.2；不增加相依。

**Spec:** [Survey 採用邊界](../../research/performance-raw-ai-survey.md#8-候選反證與採用次序)、[本機證據與裁決](../../evidence/performance-local-2026-10-26.md#8-採用不採用與下一步)。研究已先提交為 `6c1afa1`。

## Global Constraints

- 不改像素數學、模型／provider 選擇、tile／全品質、RAW／shader／Basic／任何精度門檻。
- 不把 retained pixel bytes 當程序 RSS、managed heap、ORT／GPU native 記憶體或硬預算；不把它與 RSS 相加。
- 不下載模型，不修改使用者設定／專案，不把照片、模型或執行檔提交；真實模型 smoke 使用隔離副本。
- 已確認主預覽、預取與 Loupe 使用 `ScaledTo`，遮罩包裝使用 `SourceForPhoto` 快取；沒有新證據不重寫這些正確路徑。
- 本批不做 P2 磁碟持久化、統一 RAM 預算、原生 GPU 呈現、Bayer 模型或被拒絕的亮部原型。
- 每段先有失敗檢查，再實作；相關測試及實際路徑 smoke 後更新契約／歷史並獨立 commit。不 push。

## 選項、影響與反證

採用沿用所有權／既有狀態列的小改；不選擇直接加入持久快取或全域預算，因其來源／模型失效、私密照片落盤與取消發佈尚無產品契約。

影響：AI 記憶體明細新增精確的主要陣列位元組計數；RAM 百分比仍是系統數值。新 full 推論開始後不保留舊 full 結果作失敗時的快取退路，回到舊照片可能重推論，但設定與既有畫面不變。相同來源調強度仍命中，縮圖不得逐出 full。查詢若產生 RAW 顯示陣列、重複計入共享來源、保留已逐出陣列，或任何像素／取消／縮圖隔離改變，都屬失敗。

## Task 1：AI retained pixel accounting

**Files:** `src/FilmBatch.Engine/Denoising/DenoiseProcessor.cs`、`DenoiseReport.cs`、`src/FilmBatch.Engine/Imaging/SourceImage.cs`、`tests/FilmBatch.Core.Tests/DenoisingOrtTests.cs`；契約更新於 `docs/architecture/engine.md`、`docs/history.md`。

**Interfaces:** 新增 `public readonly record struct DenoiseMemoryUsage(long NeuralBytes, long SourceBytes, long PreviewBytes)`，衍生 `TotalBytes`；`DenoiseProcessor.MemoryUsage` 及不建立 shared processor 的 `SharedMemoryUsage`。`SourceImage` 以 internal 方法把已存在的主要像素陣列加入呼叫者的參考去重集合；包括既有縮放變體及已建立的延遲顯示陣列，不觸發延遲求值。統計不包含物件／中繼資料、遮罩、session tensor、ORT/native 或 GPU 記憶體。

- [x] 先於既有微型真實 ORT 測試加入行為案例。1×1 RGBA16 同來源的 full＋thumbnail：神經結果各 12 B、來源 8 B 只算一次，總數 32 B；錯誤重複計數會得到 40 B。
- [x] 場景 3×2：source 72 B、neural 72 B；查詢不建立 48 B 的顯示編碼。`ScaledTo(2)` 加 24 B；明確取用 `Rgba16` 後才再加 48 B。
- [x] 驗證既有四尺寸 preview 輸入 LRU：16×8 full 的原始／神經各 1536 B；依序建立 4、6、8、10、12 長邊代理後，最近四筆 preview 為 `(18+32+50+72)*24 = 4128 B`；Dispose 後快照為零。
- [x] 執行 `dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release --filter FullyQualifiedName~DenoisingOrtTests.MemoryUsage`，記錄新增 API 缺席的失敗，再實作最小計數與去重。RED 為 CS1061；GREEN 3/3。
- [x] 執行 DenoisingTests／DenoisingOrtTests，共 39/39。真實 RawNIND 隔離副本：CoreML＋CPU／247 tiles；neural、source 各 392,896,512 B，preview 638,400 B；強度混合命中、還原像素相同；100 次暖查詢配置 0 B；Dispose 為零且 neural 弱參考已回收。來源／模型 SHA 不變。原 smoke driver 有一則 nullable flow 警告，產品測試未警告。
- [ ] 更新契約／實測，提交 `feat(denoise): expose retained pixel memory usage`。

## Task 2：沿用 RAM 區塊顯示明細

**Files:** `src/FilmBatch.App.Shared/Services/SystemMonitor.cs`、`ViewModels/MainWindowViewModel.Status.cs`、`Localization/Strings.resx`、`Strings.en.resx`、`src/FilmBatch.App/Views/MainWindow.axaml`；契約更新於 `docs/architecture/app.md`、`docs/history.md`。

**Interfaces:** 背景取樣擴充 `SystemMonitor.Snapshot` 的 AI pixel 快照。沿用已取樣的 AppBytes，既有 RAM StackPanel 綁定新增明細提示；系統 RAM、程序 RSS 與 AI retained pixels 分列，不相加。不得因只開監控就建立 ORT processor／session。語系資源兩份同時更新。

- [ ] 用儲存庫外的實際 native acceptance driver，先確認既有 RAM 區塊沒有本段的 AI 明細，再實作；不新增文字／wiring 型永久測試。
- [ ] 背景取得快照，沿用 `_post` 到 UI；RAM 百分比／hot 狀態／偏好開關語意不變。
- [ ] 真實模型建立快取後，啟動隔離設定的實際 MainWindow，顯示／檢查 RAM 提示與影像；讀取實際視覺輸出。原生 macOS driver 全程維持顯示器作用，不把 Headless 當 native proof。
- [ ] 執行受影響 App 回歸及建置；更新介面契約／歷史，提交 `feat(ui): show retained AI pixels in RAM details`。

## Task 3：新 full 推論入場時釋放舊 full cache

**Files:** `src/FilmBatch.Engine/Denoising/DenoiseProcessor.cs`、`tests/FilmBatch.Core.Tests/DenoisingOrtTests.cs`；契約更新於 `docs/architecture/engine.md`、`docs/history.md`。

**Interfaces:** 不新增設定／公共清除命令。延用 `InferenceGate`，第二次 cache lookup 未命中且 `fullResolution=true` 才釋放 `_cached`；thumbnail 路徑不釋放 full。等待中或預先取消的呼叫不先逐出；匹配結果不逐出。保留正在 Materialize 的呼叫者已取得的本地結果。

- [ ] 先加入真實 ORT 回歸：prime full＋thumbnail；在另一來源的新 full 的 `infer` 事件觀察，只剩原 thumbnail 的 cache bytes；事件回呼只記觀察，不在被攔截例外的回呼內 assert。
- [ ] 同來源不同強度仍 CacheHit；thumbnail 不逐出 full；進入閘門前取消仍可命中舊 full。新 full 取消／失敗後不發布半成品，重試可完成。
- [ ] 觀察失敗後在閘門內做最小釋放；如 JIT 本地參考仍延長舊大型陣列存活，先以受控弱參考／GC smoke 確認，再隔離 cache lookup 的存活範圍，不靠無條件 GC 掩蓋保留。
- [ ] 執行 DenoisingTests／DenoisingOrtTests 與相關排程回歸；真實 RAW 換來源 smoke 分列 retained pixels 與採樣 RSS，驗輸出／模型／tile／取消及來源不變。
- [ ] 更新契約／實測，提交 `perf(denoise): release superseded full results before inference`。

## Acceptance / consumer map

| Consumer | 覆蓋要求 |
|---|---|
| Engine 直接呼叫、CPU／GPU renderer、CLI 匯出 | 既有像素、模型、tile、CacheHit 及縮圖隔離不變；主要陣列計數與生命周期回歸 |
| Shared processor 與資源取樣 | 沒有使用 AI 時不建立處理器；監控不製造顯示陣列，dispose 不保留像素 |
| MainWindow RAM 區塊與語系 | 實際 native 提示；兩語系資源；既有百分比與顯示偏好不變 |
| 原始照片、專案、既有預設 | 不寫入，真實 smoke 隔離且前後 hash 相同 |
| 持久磁碟／GPU 純 kernel／Windows／iOS native | 本批不建立磁碟快取、不改 shader；本機驗證不推廣為其他平台已驗收 |

## State ledger

| Item | Status | Evidence |
|---|---|---|
| 研究先提交 | verified | `6c1afa1`，五份研究／實驗／導航文件 |
| Task 1 | verified | 三個 accounting 回歸 RED／GREEN；降噪 39/39；真實 247-tile 推論、快照、零配置查詢、弱參考釋放與隔離 digest 通過；待提交 |
| Task 2 | pending | 按 native RAM 提示及相關回歸判定 |
| Task 3 | pending | 按 cache miss 入場、取消／隔離及真實換來源判定 |
