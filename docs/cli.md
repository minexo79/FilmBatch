# 命令列工具與本機 AI 功能

CLI 用法，以及本機 AI 降噪、AI 去背的操作。

## 命令列工具（CLI）

```bash
filmbatch-cli export "/path/to/photos" --out "/path/to/output" --format png
filmbatch-cli export "/path/to/photos" --out "/path/to/prophoto" --format tiff --color-space prophoto
filmbatch-cli export "/path/to/photos" --format jpg --quality 95 --long-edge 3000
filmbatch-cli gpu-info
filmbatch-cli raw convert "/path/input.CR2" "/path/output.dng" --compression lj92
filmbatch-cli raw masks "/path/input.dng"
```

開發時可直接 `dotnet run --project src/FilmBatch.Cli -- <指令>`。

- 匯出使用已保存的專案設定，CLI 不會自動更新預設快照。
- `--software` 要求軟體 GPU 介面卡；若不可用則以 CPU 算圖並顯示原因。
- `--presets-folder` 指定獨立預設庫。
- RAW 的預覽與匯出均從浮點場景來源執行白平衡、局部遮罩與既有效果，再完成顯示收尾；解碼與 demosaic 由 CPU 執行，Metal 路徑不把 CPU 算好的圖冒充 GPU。超過裝置尺寸／記憶體限制時明確改用 CPU。
- 每筆 RAW `export_log.json` 的 `Renderer` 會附上 `RAW GPU` 或 `RAW CPU`，降級時保留原因；一般影像維持原有裝置標示。精度、量測與 Halide AOT 取捨見 [ADR 0003](adr/0003-gpu-stack-decision.md)。
- `--color-space srgb|prophoto` 預設為 `srgb`。ProPhoto 只支援 `--format tiff`／`png`，輸出為 16-bit、D50、純 gamma 1.8，嵌入自建的匹配 ICC；JPEG／DNG 或未知色彩空間回傳結束碼 2，不產生輸出檔。每筆匯出紀錄的 `ColorSpace` 保留實際選擇。
- GUI 在匯出面板選 TIFF／PNG 後可選「ProPhoto RGB」；切回 JPEG／DNG 會重設為 sRGB。此選項不改變預覽與直方圖，也不改寫照片設定。RAW 中性輸出可保留 sRGB 外的色彩；一般 raster 匯入與會裁切色域的既有效果仍有原有限制，詳見[色彩契約](architecture/engine.md#48-engine影像讀取與批次匯出imaging--exporting)與[驗收記錄](superpowers/plans/2026-10-23-prophoto-output.md)。
- AI 降噪另記於每筆匯出紀錄的 `Denoise`：模型身分與 SHA-256、實際推論 provider、推論時間、tile 數及快取命中。效果 renderer 的 GPU 名稱不代表神經網路全部在同一裝置執行。
- RAW 處理啟用時，「基本調整」提供 As Shot／自訂 2500–9900 K＋色偏，取代相對色溫／色偏；RAW 面板仍提供實際內嵌語意遮罩，沒有對應遮罩的照片保留設定但不全圖套用。外部 Lensfun-format 目錄在「偏好設定 → 資料夾」指定（存於 settings.json，全部專案共用）；CLI 匯出用 `--lens-database`，兩者皆未設定時使用 `FILMBATCH_LENSFUN_DIR`，再其次為使用者在同一頁按「下載最新版」（或 CLI `lens-database download`）取得的資料庫。下載只取 Lensfun 儲存庫的 `data/db` XML（CC BY-SA 3.0），存於 `AppPaths.Root/lensfun/db-<commit>`，逐檔以 git blob SHA-1 驗證並整批解析通過後才以 `installed.json` 原子切換；這是獨立實作，不連結也不移植 Lensfun 的 LGPL 函式庫或 GPL 的 `lensfun-update-data`，資料庫也不隨程式散布。RAW 讀取絕不自動下載。
- Lensfun 校正以 RAW 的預設裁切區（`DefaultCrop`）建立光學中心與半徑，再換算至完整感光區（`ActiveArea`）座標；非置中裁切的變形與暗角共用相同映射。
- `raw convert` 補上可用的相機色彩矩陣、hue/saturation table 與 tone，保留感光樣本、幾何及標準拍攝中繼資料；沒有可用色彩描述或目的檔已存在時拒絕寫入。標準 DNG profile 無法完整表示檔案內 JPEG 擬合外觀的逐像素亮度還原／chroma 曲線，不能宣稱轉前與轉後逐像素相同；相同色彩模型的對照與此限制分開記錄。

### 本機 AI 降噪

基本參數「AI 功能」裡的 AI 降噪預設關閉。先在同一區的模型面板閱讀署名／授權，再明確下載，或匯入官方 `.dtmodel` 套件／符合固定雜湊的 ONNX。收合「AI 功能」不會取消模型下載、匯入或進行中的降噪推論；要取消推論請先展開，再按模型面板的取消。RAW 場景來源自動使用 RawNIND Linear，一般 RGB 使用 NAFNet-SIDD；缺少／損毀模型時回報錯誤，不會在算圖時自動下載，也不以傳統濾鏡或原圖冒充成功。

```bash
filmbatch-cli denoise-models status
filmbatch-cli denoise-models install all
filmbatch-cli denoise-models import rawnind-linear "/path/rawdenoise-nind.dtmodel"
filmbatch-cli denoise-models import nafnet-sidd "/path/denoise-nafnet.dtmodel"
filmbatch-cli export "/path/to/photos" --out "/path/to/output" --format tiff --denoise 75
```

`--denoise 0–100` 只覆蓋本次匯出的啟用與強度，省略則沿用專案；不修改專案或預設。介面的總強度、亮度與色彩皆為推論結果與原圖的混合量，不是感光 ISO、模型雜訊 σ 或細節重建保證。相同來源的混合量與藝術效果調整使用神經網路快取；初次仍先全解析度推論。後續 RAW 強度調整使用快取的代理輸入，sRGB 則保留全圖逐像素夾取／量化次序、直接累積預覽，兩者均避免反覆配置全圖暫存；匯出仍用全解析度結果。

下載／匯入與照片設定分開；切換到需要不同模型的來源域時，舊安裝作業會取消，不能把 RGB 模型的完成狀態套到 RAW。明確取消預覽不會關閉照片的降噪設定；匯出中的工作請使用匯出面板自己的取消動作。

macOS Auto 嘗試 CoreML，Windows Auto 嘗試 DirectML；初始化失敗才明確標示並改用 CPU。`--software` 控制既有效果算圖器，不代表強制 AI provider 也改為 CPU。provider 報告含 CPU 混合執行的可能性，不承諾全部節點使用 GPU／ANE。CoreML 的融合圖可能先完成當前 tile 才處理取消，完成後仍丟棄已取消結果；取消／換照片不能發布舊結果。權重、署名及選型見 [ADR 0002](adr/0002-ai-denoise.md)。

本次 Apple Silicon 原生降噪介面及 self-contained GUI smoke 各 25 項通過；已實際執行隨附 CLI 的模型下載／匯入及兩個來源域的 16-bit PNG 匯出，保留 ICC 與來源雜湊。Windows 完成交叉封裝及 DirectML 原生庫／授權閉包檢查，未執行 Windows 推論。實拍畫質、測試數量及證據索引見[AI 降噪驗收紀錄](superpowers/plans/2026-10-19-ai-denoising.md#observed-acceptance-evidence--2026-10-19)；不宣稱 Lightroom 同等品質或所有相機均已驗證。


### 本機 AI 去背

在基本參數「AI 功能」裡的「去背」區，點選「AI 選取主體」。收合「AI 功能」會丟棄未接受的單張草稿與進行中的單張選取；進行中的批次去背不會因此取消，完成後仍一次保存，重新展開可看進度。macOS 透過系統 Apple Vision 產生前景遮罩；API 需求為 macOS 14 以上，不代表整個應用程式已在該版本驗收。封裝的部署下限仍由[實際原生相依庫](release.md#macos)決定，本輪只在 macOS 26 執行。應用程式不另下載權重，也不呼叫雲端或外部 CLI。其他平台的 AI 按鈕停用，仍可用「修正遮罩」手動畫筆；沒有主體或原生失敗時明確回報，不把空白或全圖當成成功提案。

1. 草稿顯示未裁切的來源，以棋盤格表示透明區；切換「保留筆刷／移除筆刷」、調整大小，或反轉遮罩。
2. 「套用」只修改焦點照片，形成一筆可復原的操作；「取消」、換照片／工具或 app 暫停會丟棄未接受草稿。已套用的結果可用「還原背景」移除，也可復原這個操作。
3. 遮罩存於 `.filmbatch/project.json`，不修改原始影像；移動整個照片資料夾、重開、縮圖或匯出不需重新選取主體。預設、複製／貼上不會把遮罩帶到別張照片；重設照片會移除遮罩。
4. 透明結果請匯出 PNG16 或 TIFF16。若遮罩處理後仍有透明像素，JPEG／DNG 明確失敗，不默默丟棄透明度。CLI 沿用已保存的遮罩：

   ```bash
   filmbatch-cli export "/path/to/photos" --out "/path/to/cutouts" --format png
   ```

AI／新草稿的工作影像長邊最多 2048，遮罩以來源參考尺寸對齊完整影像，不先放大保存；已存的較高解析度遮罩不因打開較小預覽而降解析度。CPU／GPU 在裁切與幾何之前，只將覆蓋率乘進原始 alpha 一次；RAW 仍保留場景線性 RGB。Vision 可能把多個物體一起選取或選錯邊緣，必須檢視並修正；這是去背，不是重建被遮擋背景，[生成式物件移除](research/object-removal-design.md) 的 NO-GO 與 T1–T5 不變。

#### 批次去背與草稿逐筆復原

在縮圖列選取多張，點選「批次去背選取照片」：以開始時的選取與設定快照逐張推論，不跟隨後來的焦點改變。已有遮罩的照片原樣略過；全部處理後一起保存成一筆可復原操作，原始照片、曝光與其他設定不改變。完成摘要分別列出套用、略過、無主體與失敗，錯誤檔名另列出。

- 點選「取消」後，只保存已完成的遮罩，不接受進行中的結果；未開始項目標為取消。換專案、暫停或關閉則丟棄整批過期投遞，不能把結果或復原紀錄寫到新專案。推論期間修改過的照片／設定不被舊快照覆寫，保存衝突也不消耗 undo。
- 修正單張遮罩時，按下／移動／放開是一筆草稿操作；反轉另是一筆，重疊筆畫能還原原本的 16-bit 部分覆蓋率。草稿區提供「復原／重做」，共用快捷鍵為 macOS ⌘Z／⌘⇧Z，Windows 為 Ctrl+Z／Ctrl+Shift+Z；行動按鈕共用路由，但本輪沒有 iOS 原生驗收。
- 草稿歷史即使為空，也不會穿透去復原較早的照片設定。「取消」保留已接受遮罩；「套用」只保存目前筆畫復原後的狀態，形成一筆照片命令。歷史以區塊差異保存，上限為 200 筆／32 MiB 樣本 payload，不逐筆複製整張遮罩。

CLI 使用同一批次引擎，可直接產生遮罩，再沿用既有匯出：

```bash
filmbatch-cli remove-background "/path/to/photos" --presets-folder "/path/to/presets"
filmbatch-cli export "/path/to/photos" --out "/path/to/cutouts" --format png --presets-folder "/path/to/presets"
```

也可從本次來源執行 `dotnet run --project src/FilmBatch.Cli -c Release -- remove-background "/path/to/photos"`；`--presets-folder` 可省略，省略時使用使用者預設庫。舊 `dist/` 二進位檔不會因來源變更自動取得新命令。重跑會略過既有遮罩，不再次推論；`remove-background` 只保存專案，不另外匯出 RGB。

| CLI 結束碼 | 意義 |
|---|---|
| 0 | 可處理項目完成，含全部已有遮罩的 no-op |
| 1 | 資料夾內沒有支援照片 |
| 2 | 資料夾／預設／專案無效，或不支援原生 Vision 平台 |
| 3 | 有無主體、逐張錯誤或保存失敗；其他成功項目仍可能已保存 |
| 130 | 使用者取消，已完成項目仍可保存；未完成項目不接受 |
