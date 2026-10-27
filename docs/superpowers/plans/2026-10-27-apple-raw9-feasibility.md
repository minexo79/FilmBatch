# Apple RAW 9 Feasibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans for the isolated experiment steps. Steps use checkbox (`- [ ]`) syntax for tracking. This is a research plan, not authorization to implement or replace a product backend.

**Goal:** 以實際 macOS 27、固定 RAW 與完整像素輸出，決定 Apple RAW 9 是否值得成為 FilmBatch 的明確選用來源。

**Architecture:** 先在儲存庫外以 Swift／CIRAWFilter 驗證能力，再以拋棄式 .NET 程式引用現有 Engine／Core／Raw 比較資料域、幾何與成本。只更新研究與收據，不改產品 schema、來源契約、快取或 UI；不建立第二套正式 benchmark／plugin framework。

**Tech Stack:** macOS 27+／Apple Silicon、相容 Xcode／Core Image SDK、Swift、CIContext／RGBAf、.NET 10、現有 managed RAW／RawNIND／CPU／Metal renderer。

**Spec:** [主張查證與採用評估](../../research/apple-raw9-survey.md)。本機已完成的 macOS 26／RAW 8 負向控制不等於下列 RAW 9 實驗已執行。

## Global Constraints

- 所有輸入唯讀，輸出在 `mktemp` 建立的獨立目錄；記錄原檔前後 SHA-256、OS build、SDK、控制、實際 decoder 與來源 digest。不得開啟或保存使用者現有 FilmBatch 專案／設定。
- RAW 9 執行資格必須同時通過 OS、逐檔 `supportedDecoderVersions`、明確選定版本與真正像素產出；常數／SDK／一般相機清單不算資格。
- 未就緒、未支援、取消、損毀與解碼錯誤分開報告；不得以 RAW 8、內嵌 JPEG 或零像素成功輸出冒充 RAW 9。
- 不自動下載模型或新增 entitlement；只有操作者明確要求準備資源時才呼叫逐檔下載。不得擷取或再散布 Apple 模型／SDK 內容。
- 保留浮點、場景動態範圍與明確色彩空間；不得先削波／量化成 sRGB，再換標籤當線性 RAW。
- RAW 8／9／managed 的演算法不同，不要求三者逐像素相同；若把同一合格來源交給 FilmBatch CPU／GPU，仍須遵守現行一致性門檻，不放寬 oracle。
- Windows、舊 macOS、iOS、managed 預設、現行 RawNIND、Film Look／HSL／曲線／顆粒與專案格式不變。無產品實作、commit、push 或發佈。
- macOS 27、α6700／ProRAW 等實際輸入缺席時寫 `not-run`／確切前提，不補假結果、不把缺檔當無需求、不要求升級目前工作機來完成研究文件。

## 檔案與介面邊界

| 位置 | 職責／本輪允許變更 |
|---|---|
| `docs/research/apple-raw9-survey.md`、此檔、`docs/README.md` | 研究、計畫、狀態及導航 |
| 儲存庫外 `$work/native.swift` | 原生能力／顯影 probe；可重用本輪封存的 `probe.swift`，擴充下方明確版本模式，不改產品 |
| 儲存庫外 `$work/compare/Compare.csproj`、`Program.cs` | 引用既有 `src/FilmBatch.Engine/FilmBatch.Engine.csproj`；若需讀 internal 場景，沿用既有實驗的 `AssemblyName=FilmBatch.Core.Tests`，不改產品可見性 |
| `$work/results/` | JSONL、manifest 與本機浮點／圖片；封存只留文字收據／driver，不放原圖、模型或執行檔 |

**原生 CLI 契約（計畫，尚未實作）：** `native --decoder default|8|9 --out <獨立目錄> [--prepare-resources] <raw...>`。exit 0 表示所請求模式確實產生並檢查像素；exit 2 是明確缺 OS／版本／資源的前提；exit 1 是實際錯誤。逐檔 JSONL 至少含 `inputSha256`、`osBuild`、`supportedDecoderVersions`、`selectedDecoderVersion`、`controlSupport`、`controls`、`resourceStatus`、`extent`、`colorSpace`、`finite`、`outputSha256`、`sourceUnchanged`；不能只依 exit 判定。

## Task 1：實際 RAW 9 能力、資源與 ProRAW 控制

**Files:** Create 上表的暫存 `native.swift` 與結果；Update 研究 §1–2。產出供 Task 2／3 使用的逐檔 manifest 與已物化 `RGBAf`／色彩／幾何資料；不新增永久產品測試。

- [ ] 固定主機與輸入。保存 `sw_vers`、`xcrun --show-sdk-version`、`xcrun swift --version`、晶片／記憶體與檔案 SHA。先用本輪兩張已知 RAW，再加入實際 α6700（含 ISO 6400）、X-Trans、ProRAW、mosaic DNG 與 linear DNG；逐一記錄 DNG 的影像種類，不能只看副檔名。缺少的類別保留為未覆蓋。
- [x] 實作並執行負向控制：OS 26 上要求 9 必須 exit 2，不能進入新 selector；同檔要求 8 可實際輸出，作為「拒絕不是 decoder 全壞」的正控制。2026-10-27：`--decoder 9` exit 2（6 檔、無像素、未呼叫版本 selector／下載）；`--decoder 8` exit 0。正控制含 Canon R8、Sony A7 IV 與四個 Sigma BF mosaic DNG。α6700／X-Trans／ProRAW／線性 DNG 未覆蓋。
- [ ] 在 OS 27 上記錄版本限定相機清單與逐檔版本；不依清單順序選版。DNG 亦檢查 `.version9DNG`。下列版本選擇是計畫片段，不是本機 RAW 9 執行證據：

```swift
@available(macOS 27.0, *)
func activateRaw9(_ filter: CIRAWFilter) -> Bool {
    let candidates: [CIRAWDecoderVersion] = [.version9, .version9DNG]
    guard let version = candidates.first(where: {
        filter.supportedDecoderVersions.contains($0)
    }) else { return false }
    filter.decoderVersion = version
    return filter.decoderVersion == version
}
```

- [ ] 在 `#available(macOS 27.0, *)` 守衛內呼叫；`false` 即回報此檔不支援，不偷偷算 8。現有 SDK 27 標頭的清單方法是 `CIRAWFilter.supportedCameraModels(version:)`，網頁為 `with:`；以實際 SDK 編譯核對，記錄兩份來源的差異，不用硬編碼「69」。
- [ ] 先探測資源是否可完成顯影。若需要資源，明確記錄；只有 `--prepare-resources` 才呼叫 `downloadResources(timeout:completionHandler:)`，保存 Progress／取消／逾時。不要在 27.0 呼叫最低 27.2 的 `downloadAllResources`。完成後另驗離線重開；未實測不得宣稱首次離線可用。
- [ ] 對每檔記錄 `isLuminanceNoiseReductionSupported`、color／detail／moire／lens／local-tone 等旗標與實際值；Luma 0／1 各真正 render，記錄同尺寸像素差。ProRAW 若不支援 V9，明說仍可由其他 RAW 版本顯影，不稱「V9 已成功但滑桿無效」。若 V9 可用但某控制不支援，也保留該能力差異。

Run（在提供相應環境與實作上述暫存 driver 後）：

```bash
xcrun swiftc -target arm64-apple-macosx14.0 "$work/native.swift" -o "$work/native"
"$work/native" --decoder 9 --out "$work/results/native9" "$RAW"
"$work/native" --decoder 8 --out "$work/results/native8" "$RAW"
```

**R9-CAP 驗收：** 每個 RAW 9 成功列都有支援版本、實際選版、有限且可獨立讀取的像素及原檔不變證據；未支援列不產生假成功。α6700 與 ProRAW 的結論只涵蓋實測機型／壓縮／檔案；缺類別不算通過。來源清單與 OS／資源更新後必須重新查核。

## Task 2：場景、白平衡／tone 與幾何相容性

**Files:** Create 暫存 compare 專案；Read 既有 `Imaging/SourceImage.cs`、`Raw/RawSourceLoader.cs`、`RawDisplay.cs`、`RawWhiteBalance.cs`、`RawSourceGeometry.cs`、`Rendering/RawSemanticAdjustments.cs`；Update 研究 §3。輸入是 Task 1 的真實浮點／控制 manifest，輸出 `color-geometry.json`，每項結果為 compatible／incompatible／unknown 並附樣本。

- [ ] 先建立 managed 對照：`ImageLoader.LoadFull(string path, RawLoadOptions rawOptions = default)`；讀取 `SourceImage.Scene.Image`，不要用其延遲的 `Rgba16` 當 RAW。來源維持不可變，另複製到外部比較緩衝。
- [ ] Apple 路徑從全域／局部 tone=0、固定 WB／曝光、明確 lens／sharpness／gamut／EDR 與線性 Rec.2020 `RGBAf` 開始；保存所有預設與實際值。以有已知線性 ramp 的自有 DNG 及實際亮部原檔檢查黑、階梯、飽和色、曝光後 >1 高光、有限值與 alpha。獨立按相同色彩矩陣核對，不能將「float／boost=0」本身當證明。
- [ ] 固定同一檔案的 As Shot 與自訂 Kelvin／Tint、曝光 −1／0／+1，分別只讓 Apple 或 FilmBatch 負責該步；對未削波 ROI 檢查曝光方向／比例與中性灰，定位 baseline exposure、相機 profile、tone 與 gamut 的差異。若無法定義相同白點／tone 意義，記 incompatible，不用任意補償 LUT 強迫看起來相同。
- [ ] 同一張帶方向／鏡頭校正與可用語意遮罩的 RAW，比較 full／proxy 的 crop、orientation、鏡頭 warp、landmark 及遮罩邊界；核對 `ReferenceWidth/Height`。不以「寬高相同」或縮圖看似正常代替座標證明，不能把 managed 幾何直接貼到未驗證的 Apple 像素。
- [ ] 只有資料域與幾何都成立，才在暫存程式將同一 Apple 浮點來源送進既有 CPU／Metal 效果及 PNG16／ProPhoto 對照，遵守現行 [VERIFY](../../../VERIFY.md)／[測試契約](../../testing.md)。`Tone=null` 仍套預設 tone，必須先處理其所有權；不能以提前 sRGB 削波繞過。

Run：`dotnet run --project "$work/compare/Compare.csproj" -c Release -- color --manifest "$work/results/native9/manifest.json" --out "$work/results/color"`。compare 的 `color` 模式依上述輸入／輸出建立；引用現行 Engine，不複製產品演算法為另一份 oracle。

**R9-DOMAIN 驗收：** 場景 transfer／primaries、曝光／WB／tone 所有權、HDR、full／proxy／mask 幾何均有獨立資料對照；同一合格來源的 CPU／GPU 不降低現行容差。Apple 與 managed 外觀不同本身不是 bug；無法證明相容就停止「直接裝入現有 RawSceneData」路線，另立來源契約的設計決策，不發布半相容功能。

## Task 3：同條件畫質、完整成本與研究裁決

**Files:** Extend 暫存 native／compare driver 的 `benchmark` 模式；Update 研究結果。輸入固定 Task 1 manifest 與 Task 2 控制／幾何結論，輸出全部樣本 JSONL、配對 ROI 表與 GO／HOLD 裁決。

- [ ] 同檔交錯比較 RAW 8、RAW 9、現有 managed，另以已合法安裝的固定 RawNIND／provider／digest 做 managed＋AI 對照。不為此下載新模型，不把 Apple 9＋RawNIND 的二次降噪混入 Apple-only 成果；若需雙重降噪，只列為獨立實驗。
- [ ] α6700 ISO 6400 的髮絲／布紋、低 ISO 細字、彩色細節、暗部與高光做相同 ROI／解析度／曝光與輸出色彩的匿名配對。至少兩位操作者記錄原始偏好、紋理／色斑／偽細節／色偏問題；沒有乾淨參考不聲稱真實細節還原，不能以單一雜訊分數判勝。實際照片／ROI 不放公開證據包。
- [ ] 分開計時：新程序第一張（資源準備另列）、暖 context 同照片、切照片、預覽 scale 與全尺寸匯出；每種解碼模式至少 6 個交錯樣本，記中位數／範圍／樣本數，不以 6 次假裝穩定 p95。從讀檔到同步 materialize／readback 都計入，雜湊／品質掃描排除於計時之外。
- [ ] 分別改 Apple exposure、Apple Luminance 與 FilmBatch 創意參數，不假定三種滑桿會命中同一個模型快取。記錄 cancel／切圖後不接受舊結果的外部 probe 行為；這不等於真實 Avalonia UI 延遲已驗收。
- [ ] 互動 context 設 `cacheIntermediates=true`，單張批次匯出 false；記錄經確認的系統 memory-limit 選項、sampled RSS／原生與託管範圍／採樣間隔及長邊。不得將 sampled RSS 稱硬上限；沒有 Instruments 分派證據，不把一般 CoreML 偏好或低 CPU 說成全 ANE。不得由 Core Image 的直接 Metal view 建議推定 Avalonia／Dawn 已零複製。

Run：`dotnet run --project "$work/compare/Compare.csproj" -c Release -- benchmark --manifest "$work/results/native9/manifest.json" --out "$work/results/benchmark"`。Apple 的處理仍呼叫 Task 1 driver，避免 .NET 比較器重造另一套 CIRAWFilter 控制。

**R9-VALUE 驗收：** 全部樣本可從文字收據重算、畫質偏好有具名操作者／匿名樣本對應，冷／暖／預覽／full 與資源／讀回分開。沒有「26MP <2 秒」的預設成功門檻；未達到不是 probe 壞掉，不能裁小輸出來過關。若要採用，必須看見兩位操作者在事先固定的高 ISO ROI 上共同偏好 RAW 9 的雜訊／紋理取捨，且未共同指出嚴重細節／色彩退化；同硬體完整路徑的畫質／耗時／記憶體取捨交維護者決定，不預寫 RAW 9 必勝。

## 通過後才審核的產品範圍

以下是影響 map，不是已批准或可略過驗證的 feature 工作清單：

| 邊界 | 必須協調的現有檔案／消費端 |
|---|---|
| 明確選用／能力／保存 | `Core/Params/ModuleCatalog.cs`、Engine `RawLoadOptions.cs`；唯一 schema、舊專案與跨平台不支援行為、值保留與錯誤；不把 Apple 控制重新命名成現有 `denoise.*` |
| 來源與原生生命週期 | `ImageLoader.cs`、`RawSourceLoader.cs`、`SourceImage.cs`；有條件原生載入／CF 與 autorelease 所有權、浮點色彩、metadata／遮罩／幾何；參考 `AppleImageCodec.Foreground.cs` 的守衛，不把點陣 codec 當 RAW bridge |
| 來源失效與重開 | App.Shared `ImageCache.cs`、Loupe／preset hover／film thumbnails／filmstrip；Engine `DenoiseProcessor.cs`／`DenoiseModelCatalog.cs`；所有 source key、全尺寸重開、世代、取消與最新影格交付 |
| 顯示與輸出 | `RawDisplay.cs`、CPU／GPU 對應流程、CLI `Program.cs`、`BatchExporter.cs`／`ExportModels.cs`；同一來源語意、不可變快照、實際 decoder／OS／控制 provenance，不用效果 renderer 標籤冒充 decoder |
| UI 與端到端 | 現有 RAW Inspector、雙語 Strings、一次手勢一次 undo、多選／貼上／重開、原生窗口／PNG16／ProPhoto、封裝舊 OS 負向 smoke；不支援控制不可假裝有效 |

重用回歸候選：`RawSceneIntegrationTests`、`RawWhiteBalanceTests`、`RawGeometryParityTests`、`RawSemanticRenderingTests`、`RawControlsTests`、`RawSourceLifecycleTests`、`DenoisingOrtTests`、`RawExportIntegrationTests`、`ProPhotoOutputTests`。這些目前不是 RAW 9 測試，本輪沒有執行或宣稱它們涵蓋新引擎。完整 feature 計畫必須垂直涵蓋上述消費端，不能只交付 preview 接線或靜默 fallback。

## 完成狀態與交接

| 項目 | 本輪狀態 |
|---|---|
| 官方來源、現行管線／影響、隔離 native RAW 8 基線 | 已完成，見研究與私有文字收據 |
| R9-CAP 的實際 OS 27／各類檔案／資源／ProRAW | 未通過。OS 26 CLI 負向控制已跑：decoder 9 exit 2、decoder 8 exit 0；缺 OS 27、α6700、ProRAW、X-Trans、線性 DNG |
| R9-DOMAIN 的 RAW 9 場景／幾何相容性 | 未執行，依賴實際 RAW 9 輸出 |
| R9-VALUE 的同條件畫質／效能 | 未執行；缺指定 α6700／ProRAW 等比較輸入及 OS 27 |
| 產品整合／預設 cutover | 不在本輪；先維持 managed，待研究 GO 與完整 feature 計畫審核 |

研究執行可採同一主機逐項執行，或分工準備原生與 .NET 比較器後**序列量測**；不得同時跑重型案例污染效能。每一階段可獨立否決後續產品提案。收據保存完整失敗／未測與 input/source manifest；不把「研究完成」寫成「RAW 9 已整合」。
