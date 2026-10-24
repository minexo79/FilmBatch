# Sigma BF RAW 處理速度調查（2026-10-22）

## 結論與證據邊界

使用者回報 Sigma BF RAW 比 Sony RAW 慢很多。獨立的 `research/sigma-bf-raw-performance` 支線原先以 `b32cd4c` 調查並記錄缺少原檔的限制；使用者補上 `9609219312.dng` 後，已在原研究版本與本輪交付版本 `eb7e201` 完成 BF／Sony 的同條件分段量測。

已確認預設 1600 預覽的差距，並直接量到通用 GainMap 逐樣本序列插值的成本；不是 GPU 量測，也不是所有 BF 檔案的結論。下方原 Sony 成績、下載失敗紀錄與當時的假設保留為歷史；接續實檔證據與修正驗證分開記錄。

## 原研究階段：Sony 輸入與測量方法

- 機器：Apple M3、8 邏輯核心、16 GiB；.NET runtime 10.0.13、Release。
- Sony：實檔探測為 `SONY / ILCE-7M4`，ARW、7040×4688 感光資料、14-bit、單平面 CFA；TIFF compression 32767，OpcodeList1/2/3 皆空。預設裁切 7008×4672，檔案方向轉正後為 4672×7008。
- 檔案長度 36,765,696 bytes；SHA-256：`c13c08d7e357ec355c0c481449e12edd74478c73fed2c134a521f549cd516aff`。原圖與私人目錄不納入儲存庫。
- 每個階段執行 4 次，第一筆單列，表內使用後 3 次中位數。每筆前完成 GC；配置量使用 `GC.GetTotalAllocatedBytes(true)`，**不是保留記憶體、原生配置或 peak RSS**。
- 使用 `RawLoadOptions` 預設值、1600 長邊預覽、200 長邊縮圖；未開 AI 降噪或額外效果。測量來源載入，不含 GPU 算圖、UI 排隊、直方圖或互動時間。
- `DevelopLinear` 前重新解碼，以免其釋放 `raw.Data` 影響下一輪；這個準備步驟不計入該階段。讀檔為熱檔案快取，不宣稱冷磁碟成績。
- 各列是獨立量測；`StartingColour` 包含於 `DevelopLinear`，後者包含於產品載入。JIT／快取／排程不同，**不可相減或相加推導未直接量到的子階段**。

| Sony A7 IV 階段 | 暖態中位數（ms） | managed 配置（MiB） |
|---|---:|---:|
| `File.ReadAllBytes` | 3.806 | 35.063 |
| `RawDecoder.Decode`，不含讀檔 | 45.044 | 133.219 |
| `CameraPreview.StartingColour` | 147.809 | 585.072 |
| `RawSourceLoader.DevelopLinear(..., 1600)` | 207.956 | 809.454 |
| 產品縮圖 `ImageLoader.Load(..., 200)` | 44.426 | 106.480 |
| 產品預覽 `ImageLoader.Load(..., 1600)` | 204.044 | 977.852 |
| 預覽 `RawDisplay.Encode` | 31.014 | 13.064 |
| 全尺寸 `ImageLoader.LoadFull` | 1488.396 | 4972.450 |

此 Sony 的 1600 預覽使用 `BinFactor = 4`；輸出 1067×1600。感光 ushort 樣本 SHA-256 為 `e7ba1da75ea9967ee19a78e12838f4effc8a261b8d3d5058ca9aedb6f7971ba7`；預覽 scene float 資料為 `181c7421beaca9bc4c16afaf5f2842dd203b79dabc1cf6b5071b033119232ec5`，全尺寸 scene 為 `e6b3d0925ebe451bfadf6da40ac501962606f50c3aafe5b99cd9652be1217d56`。雜湊以本機 little-endian 原始陣列位元組計算。

## 原研究階段：公開 BF 原檔取得結果

- [DPReview Sigma BF 圖庫文章](https://www.dpreview.com/samples/9153220621/sigma-bf-sample-gallery/)可經一般 Chromium 瀏覽器開啟；原本靜態讀取回傳 403，不能據此推定整個圖庫不可用。
- 圖庫 sidebar 實際提供的 RAW 連結：
  - [4981074255.dng](https://www.dpreview.com/wp-content/uploads/sample_galleries/L-8491664854/4981074255.dng)：圖庫標示 Richard Butler、ISO 100。
  - [7021580800.dng](https://www.dpreview.com/wp-content/uploads/sample_galleries/L-8491664854/7021580800.dng)：圖庫標示 Dale Baskin、ISO 5000。
- 原始檔下載：curl 回傳 HTTP 403；使用同一瀏覽器工作階段直接導覽原始檔，仍回傳 HTTP 403、`text/html`、標題 `Just a moment...`。未繞過驗證；沒有取得 RAW 位元組或可供核對的 BF 雜湊。
- [raw.pixls.us Sigma 公開索引](https://raw.pixls.us/data/Sigma/)當次包含 DP／SD／fp／fp L，沒有 BF 目錄。本機常用照片目錄的 DNG 搜尋也未找到 BF 輸入。
- 圖庫聲明版權保留；此處只記錄來源連結，不重分發原圖。不把行銷規格或研究摘要中的 compression／OpcodeList 推測當作實檔 EXIF。

### 使用者補充的樣本來源

2026-10-22 補充的 [Sigma BF vacation photo challenge 圖庫](https://www.dpreview.com/sample-galleries/4538867102)已透過一般 Chromium 開啟。第一張「Day 1: Gloucester」的 RAW 按鈕指向 [9609219312.dng](https://www.dpreview.com/wp-content/uploads/sample_galleries/L-4538867102/9609219312.dng)，與本次固定輸入同檔名；頁面標示 Richard Butler、Sigma BF、Sigma 35mm F2 DG DN、35mm、1/500 秒、f/2、ISO 200、−0.3 EV。頁面展示圖標示「Processed using Adobe Camera Raw」，不能把展示 JPEG 當作感光樣本或未加工的相機外觀基準。

該原檔 URL 的直接下載仍回傳 HTTP 403，因此這次只確認圖庫連結與頁面歸屬，未取得遠端位元組來比對 SHA-256。後續合併驗證沿用使用者提供的本機 DNG，不混換輸入；此圖庫沒有擴張下述單一 BF 檔案的量測結論，也不重分發照片。

## 原研究階段：程式分歧與待檢驗假設

| 路徑 | 來源中的規則 | 取得 BF 後如何判斷 |
|---|---|---|
| 縮圖 | `ImageLoader.Load` 僅在長邊 ≤240 且不需語意遮罩時嘗試內嵌預覽；找不到可用預覽才完整現像 | 核對內嵌 JPEG 尺寸與實際縮圖是否走完整解碼；不能假定所有 Sony／DNG 都有相同預覽 |
| DNG 解碼 | `TiffRaw.ReadImage` 平行解碼各區塊，再逐樣本合併；`RawDecoder.DecodeWith` 對 DNG／CFA TIFF 另執行 `LiftClippedSamples` | 核對 compression、strip／tile 配置，分開量測解碼、合併及飽和直方圖；Sony 對照不執行這個 DNG 飽和掃描 |
| 預覽分箱 | `RawImage.Binned.Bin` 拒絕任何仍存在的 OpcodeList3；1600 預覽會改走完整 AHD，之後才縮圖 | 同時記錄 RAW 選項與「移除停用鏡頭校正後」的 List3，不只看原始 tag |
| 鏡頭選項 | `RawSourceLoader.DevelopScene` 預設移除原有 `WarpRectilinear`／`WarpRectilinear2`／`FixVignetteRadial`；`GainMap` 不屬於這個移除集合 | 純鏡頭 opcode 在預設選項下可能已移除，不能直接聲稱它造成 BF 分箱失敗 |
| 起始外觀 | Sony 缺檔內矩陣時可擬合自家 JPEG；有矩陣的 DNG 走另一組 tone eligibility 條件 | 量測 `StartingColour`，核對色彩矩陣、tone、look／gain table；不能先認定 DNG 一定比較慢 |
| 全尺寸色彩／opcode | DNG 的 profile table 與非鏡頭 List3 opcode 可能增加逐像素工作 | 只在實檔確實帶有對應資料時量測，不刪除必要校正來換速度 |

來源入口：[RawSourceLoader](../src/FilmBatch.Engine/Imaging/Raw/RawSourceLoader.cs)、[CameraPreview](../src/FilmBatch.Engine/Imaging/Raw/CameraPreview.cs)、[TiffRaw](../src/FilmBatch.Raw/TiffRaw.cs)、[分箱](../src/FilmBatch.Raw/RawImage.Binned.cs)、[飽和點處理](../src/FilmBatch.Raw/RawImage.Saturation.cs)、[Opcode 定義](../src/FilmBatch.Raw/Opcodes/Opcodes.cs)。

## 接續驗證的約束

取得 BF 原檔後，先用同一來源版本、選項與預覽尺寸重播上述階段，記錄檔案及感光資料雜湊、機型、感光／裁切尺寸、壓縮、區塊數與 OpcodeList。比較縮圖、1600 預覽和全尺寸載入，不將不同解析度的總時間直接當成 decoder 優劣。

若某階段重現差異，再以獨立的窄量測定位；修正必須保留感光樣本、RAW 幾何、profile／opcode／高光行為及匯出精度。以修正前後的實檔輸出與現有 RAW 回歸驗證，不新增型號名稱判斷，不以相機 JPEG 取代 RAW 編輯。本輪一般效能修正另在 `dev-ai-denoising` 驗證，不構成 BF 實檔通過證據。

## 實檔接續與修正計畫

- BF：27,912,610 bytes；SHA-256 `277235132dce1e228cc9fc1ee0d24033ddddd1dbf021bc44ac85dfa669369c7e`。檔內機型為 Sigma BF，14-bit、6080×4042 單平面 CFA，有效區 `(20,2,6040,4040)`，預設裁切 `(12,14,6016,4012)`、方向 Normal。
- 原始壓縮為無失真 JPEG（TIFF compression 7），51 個 6080×80 tile；內嵌 JPEG 有 160×120、6016×4012、1620×1080。
- OpcodeList1 為空，List2 為 GainMap，List3 為 WarpRectilinear。預設停用嵌入鏡頭校正後仍可做 4 倍分箱，預覽為 1504×1003；不是退回全尺寸 AHD。
- GainMap 覆蓋有效區，21×31、單平面、row/column pitch 都是 1；651 個增益值都等於 1。仍須保留一般非恆等 GainMap 的插值與區域／平面語意。
- `eb7e201`：BF／Sony 預覽中位數 855.411／216.659 ms，全尺寸 1440.644／1613.348 ms。前述四次方法不變；不同解析度的總時間不代表每像素效率相同。
- 窄量測共 8 次，捨棄前 3 次，取後 5 次中位數：BF DNG 解碼（不含飽和點掃描）88.602 ms；飽和點掃描 42.105 ms，沒有提升任何樣本；獨立 List2 套用 209.137 ms。正規化含 List2 為 229.523 ms，診斷用的移除 List2 對照為 21.696 ms；**產品不移除 List2**。

最小修正範圍：只改 `OpcodeApply.cs` 的 GainMap 執行方式，使用互不重疊的列平行運算，保留 `GainAt` 的浮點運算順序及 opcode 清單順序。不新增相機判斷、快取、使用者選項或依賴；解碼器、飽和點門檻、色彩、去馬賽克與 JPEG 預覽選擇不變。

| 驗收 | 狀態 | 檢查 |
|---|---|---|
| BF／Sony 同版本分段量測 | 已量測 | 原研究版本與 `eb7e201` 的 full.jsonl |
| GainMap 區域、pitch、平面、插值與退化邊界 | 通過 | 16 項 opcode 測試在修改前後皆通過；新增 7 個非恆等／邊界案例 |
| GainMap 與產品預覽改善 | 通過 | List2 209.137 → 29.279 ms；BF 產品預覽 855.411 → 458.850 ms |
| 感光樣本、預覽／全尺寸 scene 不變 | 通過 | 研究與整合版本各 12 筆檔案／樣本／scene／顯示雜湊相符；窄量測另核對 5 筆 |
| 原生 macOS 實檔載入／匯出 | 通過 | 原生視窗、Apple M3 Metal，6016×4012 的 16-bit RGBA PNG 成功匯出並重新解碼 |
| 分支交付與紀錄 | 程式已提交 | 研究修正 `e61090f`；整合驗證版本 `d64b6b5`；正式分支合併與遠端狀態以 Git 收據為準 |

## 修正後成對結果

比較 `eb7e201` 與僅加入 GainMap 修正的 `d64b6b5`；輸入、選項、.NET／硬體與上述四次量測方法相同。數值為暖態中位數，單位 ms；各階段包含關係不變，不能相加。

| 階段 | BF 修正前 | BF 修正後 | Sony 修正前 | Sony 修正後 |
|---|---:|---:|---:|---:|
| 讀檔 | 3.356 | 3.065 | 5.217 | 5.968 |
| RAW 解碼，含產品原有飽和點處理 | 228.418 | 214.241 | 51.861 | 41.323 |
| 起始色彩 | 189.885 | 174.459 | 124.025 | 130.250 |
| 1600 現像 | 702.947 | 342.834 | 224.512 | 191.837 |
| 200 縮圖 | 125.486 | 112.172 | 46.269 | 44.268 |
| 1600 產品預覽載入 | 855.411 | 458.850 | 216.659 | 234.226 |
| 預覽顯示編碼 | 24.955 | 30.481 | 21.985 | 16.333 |
| 全尺寸來源載入 | 1440.644 | 1033.826 | 1613.348 | 1997.155 |

BF 產品預覽減少 46.4%，全尺寸來源載入減少 28.2%；**不是匯出總時間或 UI 互動延遲的改善率**。Sony 不含 GainMap，本輪全尺寸反而較慢，不能宣稱所有 RAW 都加速，也不能將未修改階段的單次程序計時差異歸因於此修正。兩張照片解析度、壓縮與起始外觀路徑不同；仍然存在的 BF／Sony 預覽差距不代表解碼器遺失資訊或應改用 JPEG。

根因由獨立窄量測確認，而不是由總時間相減推測：

| BF 窄量測 | 修改前（ms） | 修改後（ms） |
|---|---:|---:|
| DNG 解碼，不含飽和點掃描 | 88.602 | 89.385 |
| 飽和點掃描 | 42.105 | 43.472 |
| 正規化與 List2 | 229.523 | 53.432 |
| 只套用 List2 | 209.137 | 29.279 |
| 4 倍分箱與高光遮罩 | 242.776 | 65.395 |

GainMap 單獨約快 7.14 倍；只是改用不重疊的列平行執行，仍套用全部 651 個增益資料，沒有針對「全為 1」跳過運算。`GainAt`、opcode 順序、區域裁切、pitch、平面偏移與 clamp 語意保持不變；飽和點掃描與解碼器未修改。本次不是記憶體最佳化：BF 預覽 managed 配置約 1229.22 MiB、完整來源約 1674.18 MiB，沒有聲稱 peak RSS 改善。

## 像素與原生驗證

- BF 感光 ushort：`f4bd16ece322acecede406cf53e856dbe4634d02451cac3d87c8afcd40c6ea7b`。
- BF 正規化含 List2：`cc616d17fc4c48708f6e245fec31d5426df4b9c82250794688733be87da0eb2c`；4 倍分箱：`bd7f711c9e0ea2de5631ece578f88d5442e9b7bf0c1969c075940c310d0e487e`。
- BF 預覽 scene：`b8cc34332231b934794ff2492cb41c2679571bc69b4f0f2b66394ffce584d701`；全尺寸 scene：`3a577143a03ab9a751ce3c038136f747ad1ca6672452f562381b292fb48e628f`。
- BF 預覽顯示 RGBA16：`b4aa17259954669c0d1e81c7c01f0ed9819a70b90aa0688638d2f8d1b3fdaab3`。以上各值在修正前後完全相同；Sony 的感光／scene／顯示值也相同。這證明此修正不改變既有輸出，不等於與另一解碼器或相機 JPEG 的畫質相等。
- 實際 `Avalonia.Native.WindowImpl` 載入隔離副本，保留 scene-linear RAW 編輯來源，1504×1003 預覽與 6016×4012 參考尺寸正確；縮圖成功 1、失敗 0。截圖確認照片與 Sigma BF 拍攝資訊顯示，使用 Apple M3 Metal。
- 透過匯出面板的 `StartExportCommand` 輸出原尺寸 PNG，成功 1、失敗 0、進度 100%；檔案 IHDR 為 16-bit／color type 6，重新解碼後仍為 6016×4012 且包含非 8-bit 倍數樣本。匯出紀錄確認 `IsSoftwareRenderer=false`，未退回 CPU；重新解碼的 RGBA16 SHA-256 為 `8d38398287f500c2295debe49f7eccd76fd0a0b8f28af71dbd9e57ff06f1833a`。
- Release 回歸：RAW 636 通過／293 略過，Core 655 通過／27 略過，App 235 通過；合計 1526 通過、0 失敗、320 略過。略過包含未下載 RAW／外部 oracle 與 Windows 專用檢查，不能視為已驗證。
- 重新封裝 Apple Silicon App，25 項內建 smoke 通過，CLI 確認 Metal；本機 ad-hoc 簽章，未公證。第一次封裝命令因含空白的 `PATH` 未引用而未啟動，修正命令引用後才取得此通過結果。

可重跑的產品檢查：

```bash
dotnet test tests/FilmBatch.Raw.Tests/FilmBatch.Raw.Tests.csproj -c Release --filter FullyQualifiedName~OpcodesTests
dotnet test FilmBatch.sln -c Release --nologo --logger 'console;verbosity=minimal' --logger trx
dotnet run build/Build.cs -- --rid osx-arm64 --smoke
```

窄量測、成對載入與原生實檔驅動碼及 JSON／TRX／截圖保存在本機忽略的 `artifacts/verification/2026-10-22-sigma-bf.tar.gz`，不推送。SHA-256 `6b208e2d6a65d70d2c8b01c992622722e2f91aef5ad972a037fc3bce03012c8e`，gzip 完整性通過；不含來源照片、匯出照片或模型權重。研究版本 `f059b78 → e61090f` 的來源／像素對照與整合版本 `eb7e201 → d64b6b5` 分開保存，不混用兩版本配置量。

消費端覆蓋：opcode 公開入口由非恆等 fixture 驗證；一般來源與縮圖／預覽／全圖由實檔 driver 驗證；原生編輯器與匯出由 Metal smoke 驗證；持久化格式、來源快取鍵與預設值未變。Windows／iOS 原生執行、其他 BF 檔案與人工畫質比較不在本次通過範圍。

TWINS: searched `GainAt(` and `AreaIter(gm.Area` - found 0 other sites: none. GainMap 的各清單入口共用同一個修正，沒有保留序列版本或相機特例。
