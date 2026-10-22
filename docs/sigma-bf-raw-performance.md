# Sigma BF RAW 處理速度調查（2026-10-22）

## 結論與證據邊界

使用者回報 Sigma BF RAW 比 Sony RAW 慢很多。本次建立獨立的 `research/sigma-bf-raw-performance` 支線，以 `b32cd4c` 為來源基準；已完成 Sony 實檔的分段量測與 DNG 路徑調查，**尚未取得可讀取的 BF 原始 DNG，因此沒有確認 BF 根因，也沒有加入 Sigma 型號特例或降低現像品質**。

目前缺少的輸入是一份可在本機讀取的 Sigma BF 原始 DNG，以及造成慢速的操作／RAW 選項。不能用 Sigma fp、Foveon X3F、轉檔 DNG 或圖庫 JPEG 代替 BF 原檔。下列程式差異只是假設的檢驗入口，不是已量到的 BF 瓶頸。

## 實際輸入與測量方法

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

## BF 原檔取得結果

- [DPReview Sigma BF 圖庫文章](https://www.dpreview.com/samples/9153220621/sigma-bf-sample-gallery/)可經一般 Chromium 瀏覽器開啟；原本靜態讀取回傳 403，不能據此推定整個圖庫不可用。
- 圖庫 sidebar 實際提供的 RAW 連結：
  - [4981074255.dng](https://www.dpreview.com/wp-content/uploads/sample_galleries/L-8491664854/4981074255.dng)：圖庫標示 Richard Butler、ISO 100。
  - [7021580800.dng](https://www.dpreview.com/wp-content/uploads/sample_galleries/L-8491664854/7021580800.dng)：圖庫標示 Dale Baskin、ISO 5000。
- 原始檔下載：curl 回傳 HTTP 403；使用同一瀏覽器工作階段直接導覽原始檔，仍回傳 HTTP 403、`text/html`、標題 `Just a moment...`。未繞過驗證；沒有取得 RAW 位元組或可供核對的 BF 雜湊。
- [raw.pixls.us Sigma 公開索引](https://raw.pixls.us/data/Sigma/)當次包含 DP／SD／fp／fp L，沒有 BF 目錄。本機常用照片目錄的 DNG 搜尋也未找到 BF 輸入。
- 圖庫聲明版權保留；此處只記錄來源連結，不重分發原圖。不把行銷規格或研究摘要中的 compression／OpcodeList 推測當作實檔 EXIF。

## 已確認的程式分歧與待檢驗假設

| 路徑 | 來源中的規則 | 取得 BF 後如何判斷 |
|---|---|---|
| 縮圖 | `ImageLoader.Load` 僅在長邊 ≤240 且不需語意遮罩時嘗試內嵌預覽；找不到可用預覽才完整現像 | 核對內嵌 JPEG 尺寸與實際縮圖是否走完整解碼；不能假定所有 Sony／DNG 都有相同預覽 |
| DNG 解碼 | `TiffRaw.ReadImage` 平行解碼各區塊，再逐樣本合併；`RawDecoder.DecodeWith` 對 DNG／CFA TIFF 另執行 `LiftClippedSamples` | 核對 compression、strip／tile 配置，分開量測解碼、合併及飽和直方圖；Sony 對照不執行這個 DNG 飽和掃描 |
| 預覽分箱 | `RawImage.Binned.Bin` 拒絕任何仍存在的 OpcodeList3；1600 預覽會改走完整 AHD，之後才縮圖 | 同時記錄 RAW 選項與「移除停用鏡頭校正後」的 List3，不只看原始 tag |
| 鏡頭選項 | `RawSourceLoader.DevelopScene` 預設移除原有 `WarpRectilinear`／`WarpRectilinear2`／`FixVignetteRadial`；`GainMap` 不屬於這個移除集合 | 純鏡頭 opcode 在預設選項下可能已移除，不能直接聲稱它造成 BF 分箱失敗 |
| 起始外觀 | Sony 缺檔內矩陣時可擬合自家 JPEG；有矩陣的 DNG 走另一組 tone eligibility 條件 | 量測 `StartingColour`，核對色彩矩陣、tone、look／gain table；不能先認定 DNG 一定比較慢 |
| 全尺寸色彩／opcode | DNG 的 profile table 與非鏡頭 List3 opcode 可能增加逐像素工作 | 只在實檔確實帶有對應資料時量測，不刪除必要校正來換速度 |

來源入口：[RawSourceLoader](../src/FilmBatch.Engine/Imaging/Raw/RawSourceLoader.cs)、[CameraPreview](../src/FilmBatch.Engine/Imaging/Raw/CameraPreview.cs)、[TiffRaw](../src/FilmBatch.Raw/TiffRaw.cs)、[分箱](../src/FilmBatch.Raw/RawImage.Binned.cs)、[飽和點處理](../src/FilmBatch.Raw/RawImage.Saturation.cs)、[Opcode 定義](../src/FilmBatch.Raw/Opcodes/Opcodes.cs)。

## 接續驗證的必要條件

取得 BF 原檔後，先用同一來源版本、選項與預覽尺寸重播上述階段，記錄檔案及感光資料雜湊、機型、感光／裁切尺寸、壓縮、區塊數與 OpcodeList。比較縮圖、1600 預覽和全尺寸載入，不將不同解析度的總時間直接當成 decoder 優劣。

若某階段重現差異，再以獨立的窄量測定位；修正必須保留感光樣本、RAW 幾何、profile／opcode／高光行為及匯出精度。以修正前後的實檔輸出與現有 RAW 回歸驗證，不新增型號名稱判斷，不以相機 JPEG 取代 RAW 編輯。本輪一般效能修正另在 `dev-ai-denoising` 驗證，不構成 BF 實檔通過證據。
