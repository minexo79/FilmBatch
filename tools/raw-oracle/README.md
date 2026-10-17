# RAW 移植參考輸出

`FilmBatch.Raw` 與 `FilmBatch.Engine/Imaging/Raw` 移植自 LightCraft（釘選 commit `f34c7a8a89e0d1bade0a6889185f40acecc84a9f`）。語料測試把 C# 結果與同一批 CC0 樣本在 LightCraft 上的輸出比對；樣本與參考輸出不進版本庫，CI 沒有這兩個資料夾，相關測試會略過。

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
- 移植範圍不包含 JPEG XL DNG、內嵌鏡頭校正、gain-table 區域色調、語意遮罩、稀疏 byte-range RAW 讀取或 DNG 寫出。不支援的 RAW 直接回報，不以相機 JPEG 偽裝成解碼結果。
- RAW 匯出保留標準 IFD0／EXIF／GPS 目錄，不把相機私有標籤轉譯為標準 EXIF；例如語料中 Panasonic GH5 的 ISO 可由 RAW 解碼器讀出，但匯出檔不會因此重建標準 ISO 欄位。
