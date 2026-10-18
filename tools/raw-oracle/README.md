# RAW 移植參考輸出

`FilmBatch.Raw` 與 `FilmBatch.Engine/Imaging/Raw` 移植自 LightCraft。下列既有語料產生器仍釘選 commit `f34c7a8a89e0d1bade0a6889185f40acecc84a9f`，不因新的整合實驗暗中換版；八項 RAW 整合另對照 `4f0b110bcbd2e350c6d97b47f1d824c2222295c4`（v0.9.1），驗收契約見[實作計畫](../../docs/superpowers/plans/2026-10-18-raw-pipeline-parity.md)。語料測試把 C# 與同一批 CC0 樣本的上游輸出比對；樣本與參考輸出不進版本庫，CI 沒有這兩個資料夾時相關測試會略過。

```bash
tools/raw-oracle/fetch-samples.sh ~/filmbatch-raw-samples          # 依 Corpus/*.tsv 下載並核對 SHA-256
git clone https://github.com/storytold/lightcraft ~/lightcraft
git -C ~/lightcraft checkout f34c7a8a89e0d1bade0a6889185f40acecc84a9f
tools/raw-oracle/generate.sh ~/lightcraft ~/filmbatch-raw-samples ~/filmbatch-raw-oracle

export FILMBATCH_RAW_SAMPLES=~/filmbatch-raw-samples FILMBATCH_RAW_ORACLE=~/filmbatch-raw-oracle
dotnet test FilmBatch.sln -c Release
```

| 輸出 | 產生者 | 使用的測試 |
|---|---|---|
| `stages/<stem>/info.json`、`*.bin`、`preview.jpg` | `stagedump.rs`：解碼資訊、色彩轉換，以及 normalized／develop／highlight／binned 各階段的 FNV-1a-64 與取樣視窗 | `VendorCorpusTests`、`SonyCorpusTests`、`OracleStagesTests`、`ColorOracleTests` |
| `load/<stem>/load{200,1600}.{bin,json}` | `loaddump.rs`：LightCraft 的 RAW 來源載入（起始外觀擬合、顯影、色彩、縮放、轉正）後的線性 Rec.2020 影像與擬合色調 | `RawRenderOracleTests` |

`generate.sh` 會把兩個範例複製進 LightCraft checkout（並替 `lightcraft-raw` 加上 `serde_json` 開發相依）後建置。LightCraft 拒解的樣本（`refusals.tsv`）不產生 `stages`，拒解行為由 `VendorCorpusTests` 直接驗證。

## 比較範圍與已知偏差

- 感光資料、白平衡、色彩標籤及 normalized／develop／highlight／binned 階段以逐位元比較；不因渲染容差而放寬。
- `RawRenderOracleTests` 要求尺寸與相機色調是否存在一致，線性 RGB 的 NRMS ≤ 2%；套用各自的相機色調後，顯示 RGB 的平均絕對誤差 ≤ 4/255。分位數節點的 x 座標可能因 JPEG 解碼差異而移動，因此不以相同索引的節點座標誤差代替實際輸出比較。
- 相機內嵌 JPEG 使用 Magick.NET／libjpeg-turbo，而非上游的 jpeg-decoder／zune-jpeg；DCT 與色度內插差異可能讓擬合選擇不同候選，尤其 Sony RX100。因此擬合渲染不宣稱逐位元一致。
- 嵌入預覽支援 RGB matrix/TRC 與灰階 kTRC ICC；需完整 CMS 的 LUT／其他 ICC 目前視為不可用並以 sRGB 解讀，與上游可用 CMS 時不同。
- 現已整合 JPEG XL DNG、內嵌鏡頭校正、gain-table 區域色調、既有語意遮罩、檔頭稀疏讀取與 DNG 寫出；不代表所有廠牌／壓縮均支援。不支援的 RAW 直接回報，不以相機 JPEG 偽裝成解碼結果；全檔 RAW 解碼仍受 managed 陣列大小限制。
- RAW 匯出保留標準 IFD0／EXIF／GPS 目錄，不把相機私有標籤轉譯為標準 EXIF；例如語料中 Panasonic GH5 的 ISO 可由 RAW 解碼器讀出，但匯出檔不會因此重建標準 ISO 欄位。
- RAW 編輯來源保持浮點 Rec.2020；CPU／GPU 使用未夾取線性 sRGB 工作域，晚期收尾先轉回 Rec.2020 才做非線性高光壓縮。獨立灰階／彩色／Kelvin／遮罩／Lensfun fixture 與實際 Metal 比對，不以 CPU／GPU 互相比對代替上游參考。
- DNG 色彩合成補足相機矩陣與 profile；lossless 模式保留感光樣本。標準 profile 與檔案內 JPEG 擬合外觀不是同一模型（逐像素亮度還原與 chroma 曲線無法完整寫入）；轉換驗證須比較兩邊實際轉出的 DNG，另保留轉前／後外觀差異，不能為了讓不同模型通過而放寬容差。
- JPEG XL 固定使用隨附 libjxl 0.11.2；來源／授權與封包雜湊見 `src/FilmBatch.Raw/Native/Jxl/provenance.json`。外部 Lensfun 資料庫不隨程式散布，僅由使用者提供。
