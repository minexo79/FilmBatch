# Remaining Performance Correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement the admitted repairs task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 依證據排序 P1 之後的問題；使用者追加 AI 卡頓後先修正已實測的命中來源重建，再續定位 RAW AHD 大型離群與合併效果精度，不放寬門檻或降低品質。

**Architecture:** 沿用 CPU reference、既有 RAW accelerator 與 WGSL passes。唯讀分析可分工，單一整合者負責 throwaway 階段擷取、回歸、原生路徑、文件與逐項提交；不為診斷建立產品 API。

**Tech Stack:** .NET 10、managed RAW、WebGPUSharp 0.5.7／Dawn／WGSL、macOS Metal。

**Spec:** [採用裁決](../../evidence/performance-local-2026-10-26.md#8-採用不採用與下一步)、[RAW 與效果反例](../../evidence/performance-local-2026-10-26.md#10-追加-e04e07完整實跑與負面結果)、[Engine 契約](../../architecture/engine.md)、[VERIFY](../../../VERIFY.md)。起點 `3f81cf3`，工作樹乾淨。

## Global Constraints

- 不改使用者照片／設定；實拍僅用隔離副本，記錄前後 SHA-256，不散布照片、模型或點陣。
- 不重跑已知失敗只為確認；新執行必須提供新增階段、第一分歧或修正後證據。
- 不放寬浮點／RGBA16 容差，不以 CPU fallback、停用 GPU、降解析度／f16 或改模型讓反例通過。
- CPU 不是 RAW 感光／高光的絕對真值；修正必須有算法不變量或獨立計算依據。
- 其他平台驗收不在本批完成條件；不推送、不改發布檔、不新增相依。
- 數學修正同時檢查 CPU／GPU、原生真實路徑、相鄰尺寸／邊界；shader 改後執行 `dotnet run build/CheckShaders.cs`。

## 排序與採用狀態

| 順序 | 問題／候選 | 本批裁決 |
|---|---|---|
| 0（原 3） | AI 命中後的來源身分重用 | 已完成最小弱重用及實拍重播：後七幀同參考、GPU 來源維持 1 筆；首次推論仍同步 |
| 1 | RAW GPU AHD 切換帶來 scene 1.1265284／同 CPU 收尾 13,650 LSB 離群 | 執行階段定位及有證據的最小修正；不擴大卸載 |
| 2 | 1600×1067 暗部還原＋Basic Highlights 的 3 LSB／單一分量反例 | 執行階段定位及修正，保持原 2 LSB 研究目標；不是普遍精度承諾 |
| 4 | 跨來源／重開的 f32 AI 持久結果 | 另批完整生命周期設計及驗收，不混入數學修正 |
| 5 | 昂貴前綴、Avalonia 原生 GPU 呈現／Mip | 須先有完整互動收益與等價證據，仍候選 |

不採用的全域／局部亮部原型、RAW 全限 1 worker、全面 f16、GPU backend 遷移不排入產品；保留原負面結果。統一預算需從 retained/accounting 與實際壓力證據另行裁決。

## Task 0: AI 命中後的穩定混合來源（追加最高優先）

使用者追加 GPU-only 問題及卡頓觀察後，先完成 provider 實測及 CPU 路徑盤點，再交付此已確認缺口。Task 1／2 已增加階段擷取，但不因插單而宣稱修復完成；修復仍依根因證據，不以改門檻收尾。

**Files:** `Denoising/DenoiseProcessor.cs`、既有 `DenoisingOrtTests.cs`、現行 Engine 契約；研究、歷史及本計畫一併更新。

**Interfaces:** `Prepare`／`PrepareThumbnail`／`DenoiseReport` 不變。相同 neural、相同請求來源參考與相同 `DenoiseSettings` 的 cache hit 沿用仍存活的成品；單槽弱參考同時記錄來源與成品，不增加 full 像素強保留。首次 miss 仍回報 false，下一次 hit 產生 true 的 report 後才可重用。

- [x] 以既有 Tiny Neg ONNX 走真實推論，驗相同 mix 身分重用、三個 mix 參數各自失效、遮罩來源更新及預取消；先 RED。
- [x] 僅在 `Materialize` 的 hit 邊界作單槽弱重用，既有數學移到相同 body，不改 infer／tile／mix 公式、計量、容量或 public API。
- [x] 重播真實 RAW／RawNIND 247 tiles＋Metal 交替曝光，驗來源快取不因同 mix 增長、輸出逐 byte 穩定、無 fallback；另驗弱輸出可回收。
- [x] 跑相關 Core 回歸，更新文件與歷史，封存隔離收據後獨立提交；不宣稱消除首次推論或全部 UI 卡頓。

本機額外 placement probe 只在隔離 session 使用 `session.disable_cpu_ep_fallback=1`，不改產品預設。RawNIND 49/49 節點交 CoreML 且 strict 可跑；NAFNet 1245/1246 交 CoreML，`/Pad` 在 ORT CPU，strict 正確拒絕。CoreML 計畫的裝置選擇不是實際硬體每算子 trace。

## Task 1: RAW AHD 第一分歧

**Files:** `src/FilmBatch.Raw/Develop/Ahd.cs`、`src/FilmBatch.Raw/Develop/AhdAccelerator.cs`、`src/FilmBatch.Engine/Imaging/Raw/RawGpuDemosaic.cs` 及其實際 shader／回歸；確定根因後只改必要位置。

**Interfaces:** 消費現有 `Demosaic.AhdGpu`、`FillAhdBand` 和 `RawGpuDemosaic.BeginScope`，產出同幾何／同資料域的 camera RGB；不加診斷產品介面。

- [ ] 擷取原尺寸 CPU／GPU demosaic 原始結果與高光前後，對照最差點與方向／邊界，辨別第一分歧。
- [ ] 由已定位的不變量建立最小消費者可見回歸，在修正前觀察失敗；不將私人實拍加入永久 fixture。
- [ ] 最小修正，執行相關 RAW／GPU 回歸、shader 檢查、同張全尺寸實拍及邊界控制，核對未 fallback 與來源不變。
- [ ] 更新現行契約／歷史、保留失敗與成功收據、獨立提交。

## Task 2: 合併效果精度

**Files:** `src/FilmBatch.Engine/Rendering/ShadowRecovery.cs`、`GpuEffectPipeline.cs`、`src/FilmBatch.Gpu/Shaders/v1/linear/shadow_*.wgsl`／實際第一分歧處，以及現有 `ShadowRecoveryTests`／`RawGpuDisplayTests` 中必要回歸。

**Interfaces:** 沿用 production decode、shadow stats／box／apply、RAW finish、BasicDisplay 與 encoder；throwaway driver 可直接派送現有 kernels 並讀回 float。

- [ ] 在同一 CPU 解碼來源擷取 decode／shadow／RAW finish／BasicDisplay，定位原 1600 combined 第一分歧；不能把全管線差硬歸因 shadow。
- [ ] 用獨立／字面值 fixture 建立數學邊界回歸，先 RED 再作最小修正；不更改原 oracle。
- [ ] 真實 Metal 路徑檢查原八組 512／1600×預設／亮部／暗部／合併，RGB≤2、alpha 完全相同、無 fallback、輸入 digest 不變；相關回歸與 shader 檢查。
- [ ] 更新現行文件與歷史、保存可重播收據，獨立提交。

## Consumer map

| 消費者 | 範圍／要求 |
|---|---|
| RAW App preview／Full／thumbnail | AHD／Bilinear 入場不變，直接 camera RGB 與完整收尾均檢查 |
| CLI／匯出 | RAW 解碼 CPU 隔離不變；效果修正對相同來源 CPU／Metal 收尾驗證 |
| Main／窗口／縮圖 | 不改排程／來源世代；相關 rendering regression 覆蓋共用數學 |
| AI／模型／專案資料 | 不改推論、tile、provider、設定與專案格式 |
| AI 同 mix 的 GPU 來源快取 | covered：場景／SDR 的 Tiny Neg 回歸＋真實 247-tile RAW／Metal 八幀重播；首次 miss／hit 報告與三個混合參數、遮罩、預取消均驗證 |
| 強保留／GC | covered：單槽弱參考 1×1 真實模型控制，原先 full 准入／取消回歸不變；成品已失效時允許重建 |
| 檔案／模型／專案資料 | covered：來源與權重固定 digest；公開 API／schema／儲存格式不變 |
| Windows／iOS 與實體滑桿 p95 | untested：本批 source-built Engine／Metal 重播不代替其他平台或原生 UI 延遲百分位 |
| 舊設定 | 不改控制語意與順序；僅修復已確定數值算法不一致 |
| 文件／原研究收據 | 舊失敗不改寫；新增有日期的修復與限制，原始收據保留 |

## State ledger

| Item | Status | Evidence |
|---|---|---|
| 排序與範圍 | verified | 已讀現行契約與 E03/E04，P1 三段已完成；使用者要求排序並開始 |
| RAW AHD 第一分歧／修正 | in_progress | 純 demosaic 擷取已量到 max 0.039443016，早於 highlight／colour；同圖 scene／display 原反例保留，kernel 子階段續查 |
| 合併效果第一分歧／修正 | in_progress | 實際 kernels 重建結果與 renderer 逐 byte 相同；shadow float 已分歧、同輸入 RAW finish≤1 LSB，合併仍 3 LSB；不以擴容差收尾 |
| AI GPU-only／CPU 盤點／卡頓排序 | verified | 49／49 與 1245／1246 ORT placement、strict 對照及完整 CPU／fallback 分層，見實測 §11 |
| AI 身分重用修正 | verified | 先 RED 3 cases；相關 Core 67／67、完整 Core 864 passed／28 skipped；實拍來源 1 slot、像素逐 byte 保持、弱輸出可回收 |
| App 取消 fixture 的既有時序缺口 | verified | 首輪 394 passed／1 failed；補齊初次掃描完成前提後同類 11／11、最後完整 App 395／395，產品取消邏輯與斷言不變 |
