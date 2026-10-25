# 縮圖載入與放大檢視的效能修正方案

2026-10-25 量測資料夾：`D:\修圖大賞\Sony261011\10061011`（355 組 ARW+JPG，ARW 約 35 MB／張，Sony ILCE-7M4，7008×4672）。
環境：Windows 11、16 邏輯核心、32 GB RAM、RTX 3060（DX12）。Release 建置。

## 結論

兩個問題在 `e97a972` 之前就存在。用同樣流程建置 `e97a972^` 對照，兩版數據幾乎相同，不是這個 commit 造成。

| 指標 | e97a972 | e97a972^ |
|---|---|---|
| 載入期間平均 CPU（全核心） | 17.6% | 18.0% |
| 載入期間讀檔速率 | 約 105 MB/s | 約 105 MB/s |
| 載入共讀取 | 12.5 GB | 12.5 GB |
| 按 1:1 後私有記憶體 | 約 4.5 GB | 約 4.6 GB |
| 再滾輪放大後私有記憶體 | 約 7.6 GB | 約 7.7 GB |

量測方式：自建取樣腳本每 0.5～1 秒讀取程序 CPU、WorkingSet、PrivateBytes、`Win32_Process.ReadTransferCount`，並以滑鼠操作實際點選縮圖、按 1:1、拖曳平移、滾輪放大。另寫一支小程式直接呼叫 `ImageLoader` 量單張成本。

## 問題 1：載入資料夾時縮圖吃 CPU 與硬碟

### 根因

1. `RawLoadOptions.FromSettings` 在 RAW 啟用時固定設 `IncludeSemanticMasks: true`（`src/FilmBatch.Engine/Imaging/RawLoadOptions.cs`）。
2. `ImageLoader.Load` 只在 `!rawOptions.IncludeSemanticMasks` 時走內嵌預覽快路徑（`src/FilmBatch.Engine/Imaging/ImageLoader.cs:21`）。
3. 所以濾鏡條對幾乎所有 RAW 都跳過快路徑，改走 `RawSourceLoader.TryLoad`：整檔讀入、`DecodeLease` 算 SHA256、場景線性顯影。

單張 200px 縮圖的成本（小程式量測）：

| 路徑 | 讀檔 | CPU 時間 |
|---|---|---|
| 內嵌預覽快路徑 | 約 3 MB | 約 80 ms |
| 目前（帶 IncludeSemanticMasks） | 35 MB | 0.8～2.5 s（暖機階段偶有 7 s） |

355 × 35 MB ≈ 12.4 GB，與實測載入總讀取量一致。縮圖不使用語意遮罩，這個條件來自 `735fdcf`。

### 修法

縮圖路徑不應再依賴 `IncludeSemanticMasks` 決定能不能走內嵌預覽。

1. `ImageLoader.Load`：長邊不超過 `ThumbnailLongEdge` 時，不看 `IncludeSemanticMasks`，直接嘗試 `TryExtractThumbnail`。縮圖用不到遮罩。
2. 保留既有的退路：內嵌預覽取不到或損毀時，仍回到完整顯影。
3. 已去背 RAW 的縮圖維持現有分流（`FilmstripViewModel` 的 `needsDeveloped` 走 `GetPreviewAsync`），不受影響。
4. `ImageCache.GetThumbnailSourceAsync` 以 `rawOptions` 當快取鍵比較，不必改。

已確認的取捨（使用者同意）：縮圖改用內嵌預覽後，白平衡、鏡頭校正等 RAW 模組調整不會反映在濾鏡條縮圖，底片、曲線等後段效果仍會反映。選取後的大圖仍是場景線性顯影，準確度不變。

### 驗證

- 同樣資料夾重跑：載入共讀取應由約 12.5 GB 降到約 1 GB 以內（355 × 3 MB 加 JPG 縮圖），平均 CPU 與載入時間應明顯下降。
- 單元測試：新增「RAW 縮圖不論 IncludeSemanticMasks 皆走內嵌預覽」；沿用 `ThumbnailRetentionTests`、`FilmstripLoadingTests`、`BulkLoadingTests`。
- 目視：濾鏡條縮圖與選取後大圖的色調差異可接受。

## 問題 2：放大看 RAW 細節時 CPU、RAM、硬碟吃很多

### 現象

| 步驟 | CPU（全核心） | 私有記憶體 |
|---|---|---|
| 閒置 | 0% | 約 1.1 GB |
| 按 1:1 | 60～70%，持續 1～2 秒 | 約 4.5 GB |
| 平移 | 低 | 約 4.8 GB 峰值 |
| 滾輪放大 | 18%，之後歸零 | 約 7.6 GB |

我的機器上沒有看到磁碟讀取（1:1 時只多讀 35 MB，剛好是一個 ARW）。舊版滾輪放大時曾量到 pages-input 7198/s 的尖峰，表示記憶體吃緊時會換頁；你的機器可用 RAM 較少時，這很可能就是硬碟活動的來源。這一點是推論，尚未在你的機器上驗證。

### 根因（小程式量測）

`ImageLoader.LoadFull` 對 7008×4672：

- 一次解碼配置約 1.7 GB，保留只有約 415 MB。
- CPU 時間 19～30 秒，實際只花 1.7～2.7 秒，代表 `Parallel.For` 佔滿全部 16 個核心，沒有並行度上限或降優先權。
- `SourceImage.ScaledTo` 最多保留 2 份縮放版（`MaxScaledVariants = 2`），各約 70～210 MB；縮放版對應不同長邊，滾輪放大時會再產生新的。
- 執行階段沒有設任何 GC 組態（`runtimeconfig.json` 只有 MVVM 與序列化相關設定），暫時配置的 1.7 GB 不會很快歸還，加上 GPU 上傳，私有記憶體停在 4～7 GB。

### 修法建議（尚未實作，需逐項量測再決定）

按預期效果由高到低：

1. **降低解碼的暫時配置**：找出 `FilmBatch.Raw` 顯影流程中配置 1.7 GB 但只保留 415 MB 的中間緩衝，改為重用或分塊（`Demosaic`、`Highlight`、`Directional` 等的 `Parallel.For` 區域最可疑）。這是記憶體與換頁的主因。
2. **限制 Full 解碼的並行度**：前景 Full 解碼用 `ParallelOptions.MaxDegreeOfParallelism`（例如核心數的一半），或降低該工作的執行緒優先權，讓 UI 與縮圖不被餓死。代價是放大等待時間略增，需要量測取捨。
3. **歸還記憶體**：Full 釋放（`ScheduleFullRelease`，閒置 3 秒）之後觸發一次 `GC.Collect` 加 LOH 壓縮，或在 `runtimeconfig` 設 `System.GC.ConserveMemory`／`System.GC.RetainVM=false`。先量測是否真的降低尖峰後留存量。
4. **縮放版節流**：滾輪連續放大時，只對停下來的最終長邊產生 `ScaledTo`，避免中間級距各配置一份。可與既有的「最新請求勝」合併。
5. **確認 Full 與 Preview 解碼可共用**：`DecodeLease` 目前只共用進行中的解碼；放大時若 Preview 剛釋放，會整檔重讀重解。可評估短暫保留 Preview 的解碼結果，但要避免增加常駐記憶體，優先級較低。

### 驗證

- 重跑同樣流程（選取 → 1:1 → 平移 → 滾輪放大），記錄 PrivateBytes 峰值、1:1 持續 CPU 時間、`Pages Input/sec`。
- 目標：私有記憶體峰值下降（目標值待第一輪量測後訂）、1:1 期間 UI 不卡頓。
- 沿用 `PreviewInteractionTests`、`RawSourceLifecycleTests`、`OffThreadScalingTests`；解碼輸出必須與目前逐像素一致（`RawSceneIntegrationTests`），不能為了省記憶體改變場景線性像素。

## 建議順序

見文末「已決定的範圍與實作順序」，該節取代本節。

## 量測副作用與重現

- 為避免寫入原始資料夾（App 會在開啟的資料夾內建立 `.filmbatch`），量測使用 `D:\fb-verify-scratch\10061011` 的 hardlink 複本。原始資料夾已確認未被寫入。複本只佔 hardlink 空間，可手動刪除。
- 重現：`dotnet build src/FilmBatch.App -c Release` 後執行 `FilmBatch.exe --open <資料夾>`，等縮圖載完，選一張照片按 1:1，再用滾輪放大。

## 補充：與 LightCraft v0.13.0 的差異（問題 2 的 CPU）

使用者提供的錄影（`20261024-1725-43.7376222.mp4`）顯示 LightCraft 載入快、縮放時工作管理員中 LightCraft 約 1.0～1.7 GB、CPU 峰值約 20%（同時 GPU 約 19%）。FilmBatch 同樣操作 CPU 約 60～70%、私有記憶體 4.5～7.6 GB。

比較來源：LightCraft v0.13.0 的 `docs/gpu-pipeline.md`、`crates/engine/src/media.rs`、`crates/ui-egui/src/region.rs`、`crates/raw/src/demosaic/mod.rs`，加上本機量測（取樣檔 DSC02587.ARW，7008×4672，16 核心）。

### 已經一致（不必再改）

- 三級來源 Thumb／Preview（2560）／Full，Preview 由 Bayer 分箱（binned）直接產生。
- Window 層門檻 1.25、對齊 256 網格、margin 4.5%、MaxSpan 6144。
- Full 只保留一份、閒置後釋放（FilmBatch 為 3 秒）。
- 去馬賽克預設都是 AHD，Full 解碼的像素格式同為 `Rgb32f`（12 bytes/px，保留約 415 MB 與量測一致）。

### 實際差異

| # | 項目 | LightCraft v0.13.0 | FilmBatch 現況 | 影響 |
|---|---|---|---|---|
| 1 | 去馬賽克執行方式 | `demosaic/mod.rs` 沒有 rayon，是單執行緒循序迴圈 | `Parallel.For` 吃滿全部核心 | FilmBatch 每個階段 CPU 時間是牆鐘的約 14 倍，工作管理員上顯示成 60～70% 的長峰值 |
| 2 | 全尺寸解碼的 CPU 總量 | 單階段單執行緒 | 量測：RawDecoder.Decode 0.7～2.4 CPU 秒、Normalized 0.5～0.8、AHD 8.2～8.9、高光重建 3.7～5.1，再加 ApplyColour 與鏡頭鏈，一次 Full 合計 19～30 CPU 秒，牆鐘僅 1.7～2.7 秒 | 同一份工作被並行放大成高 CPU 占用；總工作量（CPU 秒）也偏高，PPG 4.9、Bilinear 1.0 作對照 |
| 3 | 記憶體預算與解碼閘門 | `memory.rs`：預算 min(25% RAM, 1.5 GiB)，一半給快取、四分之一給進行中的解碼（縮圖／預取要等，放大載入不等） | 沒有總預算，只有 `_decodeGate`（核心數一半）與背景閘門 | FilmBatch 私有記憶體停在 4.5～7.6 GB |
| 4 | 解碼後釋放 | 「解碼後立刻釋放檔案位元組與 raw 樣本，色彩、裁切、方向原地處理」；macOS 另呼叫 `malloc_zone_pressure_relief` | `DecodeLease` 持有整份檔案位元組到顯影結束；一次 Full 配置 1.7 GB、保留 415 MB | 暫時配置高，加上無 GC 組態，記憶體不歸還 |
| 5 | 放大時的來源 | 視窗由同一份已解碼的原圖切出，原圖只上傳 GPU 一次（64MP 約 800 MB），平移與各級縮放都共用 | `SendWindowAsync` 依畫框長邊對 Full 做 `ScaledTo`（Box 縮放，最多 2 個變體），每換一級就產生新來源並重新上傳 GPU | 滾輪放大一次多約 2.4 GB 與 1 CPU 秒的縮放；與量測的 7.6 GB 吻合（推論，未單獨隔離驗證） |
| 6 | 縮放期間的渲染節流 | `SizeHold`：捏合期間與停止後 0.25 秒內不重新規劃 | 已有最新請求勝／合併，但各級仍會建立 `ScaledTo` | 連續滾輪會建出中間級距的縮放來源 |

### 可選的修正項目（問題 2）

A. **限制 Full 解碼的並行度**（對應差異 1）：前景 Full 的 `Parallel.For` 加 `MaxDegreeOfParallelism` 或降低優先權。最小改動，直接壓低 CPU 峰值，代價是放大等待變長。
B. **視窗直接取自 Full，不再逐級 `ScaledTo`**（對應差異 5、6）：Full 在 GPU 只上傳一次並共用；節省約 2.4 GB 與每級的縮放 CPU。改動較大，涉及 GPU 來源紋理快取與窗口像素對應，需用 `PreviewInteractionTests`、`OffThreadScalingTests` 與像素比對驗證。
C. **降低解碼的暫時配置並提早釋放位元組**（對應差異 3、4）：顯影階段原地處理、`DecodeLease` 在 Decode 後即釋放檔案位元組；可再加 GC 組態（`ConserveMemory`）與 Full 釋放後歸還。
D. **加上簡單的記憶體預算**（對應差異 3）：總預算與解碼閘門，縮圖與預取等待、前景放大不等。範圍最大，建議放最後。

每項各自 commit；每項改完用同樣流程（選取 → 1:1 → 平移 → 滾輪）量測 PrivateBytes 峰值與 1:1 CPU，並確認像素與現況一致。

## 已決定的範圍與實作順序

使用者的選擇（2026-10-25）：

- 問題 1：縮圖改用內嵌預覽，接受 RAW 模組（白平衡、鏡頭）調整不反映在濾鏡條縮圖。
- 問題 2：A、B、C、D 四項全做，並且「盡量能用 GPU 處理就交給 GPU」。
- 每項各自 commit，不合併成一個大 commit。

### 實作順序

| 順序 | 項目 | 內容 | 驗證 |
|---|---|---|---|
| 0 | 問題 1 | `ImageLoader.Load` 縮圖快路徑不再看 `IncludeSemanticMasks`；補單元測試 | 重跑載入：讀檔量由約 12.5 GB 降到約 1 GB 以內 |
| 1 | A | 前景 Full 解碼加並行度上限／降優先權 | 1:1 持續 CPU 下降；解碼結果逐像素一致 |
| 2 | C | `DecodeLease` 解碼後即釋放檔案位元組；顯影階段原地處理；GC 組態與 Full 釋放後歸還 | 私有記憶體峰值、1:1 後留存量 |
| 3 | B | 視窗直接取自 Full，不再逐級 `ScaledTo`；GPU 只上傳 Full 一次並共用 | 滾輪放大不再多 2.4 GB；`PreviewInteractionTests`、`OffThreadScalingTests` 與像素比對 |
| 4 | D | 記憶體預算（對應 LightCraft `memory.rs`：min(25% RAM, 1.5 GiB)，一半快取、四分之一進行中解碼）與解碼閘門，縮圖／預取等待、前景放大不等 | 載入與放大同時進行時的記憶體上限 |
| 5 | GPU 卸載 | 見下節；需先評估可行性 | 與 CPU 路徑逐像素（或容差）比對 |

先做 A、C、B 的原因：A 最小且立即壓低 CPU 峰值；C 與 B 直接對應量測到的記憶體尖峰。D 範圍最大，放在已能量測效果之後。GPU 卸載放最後，因為它要在 A～C 的基準上才看得出額外收益。

### GPU 卸載（待評估）

使用者要求盡量交給 GPU。目前量測到的 CPU 大宗（一次 Full 合計 19～30 CPU 秒）依序為：

| 階段 | 量測 CPU 秒 | GPU 化難度與備註 |
|---|---|---|
| AHD 去馬賽克 | 8.2～8.9 | 中高。AHD 為雙方向候選加 CIELab 同質性選擇，可拆成 compute pass；需與 CPU 結果比對容差，因 RAW 場景線性像素是現有契約 |
| 高光重建 | 3.7～5.1 | 高。`Highlight.ReconstructWith` 有多階段（含分塊與反覆）演算法 |
| `RawDecoder.Decode`＋`Normalized` | 1.2～3.2 | 低收益。檔案解壓縮本質循序，保留 CPU |
| `ApplyColour`、鏡頭鏈 | 未單獨量測 | 低～中。逐像素矩陣與查表，最容易搬上 GPU |
| 視窗縮放（`ScaledTo`） | 約 1 CPU 秒／級 | 由 B 項直接消除，不必另外 GPU 化 |

評估前要先確認：現有 `FilmBatch.Gpu` 的 WGSL 管線能否接收未去馬賽克的 Bayer 資料、GPU 紋理上限（`AssumedTextureSide = 8192`，7008×4672 的 Full 可放入）、無 GPU 時必須維持現有 CPU 路徑作為備援，且 CPU／GPU 結果必須能比對。此節在實作 A、C、B 之後，依實測剩餘的 CPU 占比再決定先 GPU 化哪一段。

### 待確認

- GPU 卸載的結果允許的像素容差（現有 RAW 契約是否容許與 CPU 參考略有差異）。
- D 項的預算預設值是否沿用 LightCraft（min(25% RAM, 1.5 GiB)），還是另訂較大的預設，因為 FilmBatch 的 Full 保留約 415 MB 加 GPU 紋理。
