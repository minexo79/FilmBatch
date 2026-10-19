# FilmBatch

批次底片效果處理工具。可為每張照片套上底片色彩與類比效果，再一次批次匯出。  
以 C# / .NET 10、Avalonia 12 與 WebGPU 開發，支援 Windows 與 macOS。

## 可以用來做什麼

- **像 Lightroom 一樣批次修圖**：開啟整個資料夾，以縮圖列瀏覽，一次選多張照片套用同一組設定，再整批匯出。大量照片載入時顯示掃描、完成張數／總數、失敗數與耗時，優先目前照片及可見縮圖；可取消背景載入，並在背景工作或偏好設定中即時調整 CPU 目標（10–100%，預設 70%；不是硬性上限）。
- **每張照片各有風格**：建立多個預設（例如「Portra 風 + 漏光」），每張照片可指定不同預設，並在預設上單張微調；只記錄差異，修改預設時其他照片自動同步。
- **底片色彩 & 類比效果**：底片顏色模擬，內附 36 卷底片 LUT（來自 [RawTherapee Film Simulation Collection](http://rawtherapee.com/shared/HaldCLUT.zip)，CC BY-SA 4.0），可調效果比例、暖色、褪色與八色相帶修正，並以目前照片的縮圖挑選；搭配顆粒、光暈、漏光、暗角、焦外、動態模糊、鏡頭變形、雙重曝光、多格分鏡、灰塵刮痕等，以 GPU 即時預覽。
- **非破壞性編輯**：原始檔永不修改；匯出 JPEG／16-bit TIFF／16-bit PNG，保留 EXIF 並嵌入 sRGB ICC。
- **直接編輯 RAW**：支援 Sony、Canon（CR2／CR3）、Nikon、Fujifilm、Panasonic、Olympus、Pentax、Samsung、Leica、Hasselblad、Epson、Mamiya 與 DNG（解碼移植自 [LightCraft](https://github.com/storytold/lightcraft)）。保留場景線性高光供曝光調整，提供相機白平衡／自訂 2500–9900 K＋色偏、內嵌語意遮罩局部調整與鏡頭校正；一般幅度的白平衡調整即更新預覽。含 JPEG XL 的 DNG 使用隨附解碼器，不需安裝系統函式庫。不支援的 RAW 直接回報，不以內嵌預覽代替。
- **本機 AI 降噪**：RAW 使用線性 Rec.2020 RawNIND，一般 RGB 照片使用 NAFNet-SIDD；先對全解析度推論，再產生預覽。強度、亮度與色彩可分開混合，支援取消、復原與 16-bit 匯出；模型須明確下載／匯入，推論不連線。署名、模型資料域與驗證邊界見 [AI 降噪決策](docs/adr/0002-ai-denoise.md)。
- **可重現的顆粒**：團塊、多尺度相關場、2D 光學統計三種模型；可調 seed、暗部／中間調／高光響應與光學取樣品質。裁切與旋轉不重新隨機化，縮小預覽會濾除次像素顆粒；不是特定片種／ISO 的量測重建。
- **照片取樣工具**：在基本參數頂端展開「照片工具」，圈選中性區求白平衡、擷取跨照片的相對參考色，或只調整羽化區域的膚色。RAW 可取樣 Zone 並放置到 III／V／VIII，或以暗部／高光雙錨點擬合階調。提案預覽不存檔，接受後可一次復原；取樣時暫停創意效果，取消後恢復。限制與操作見[顆粒、白平衡與 Zone](docs/grain-white-balance-survey.md#目前實作與操作)。
- **命令列批次輸出**：`filmbatch-cli` 可在腳本中直接套用已保存的設定匯出。

## 快速開始

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project src/FilmBatch.App
```

開啟後以「檔案 → 開啟資料夾」（`Ctrl/⌘+O`）選擇照片資料夾即可。

## 開發教學

**1. 建置與測試**

```bash
dotnet build FilmBatch.sln
dotnet test FilmBatch.sln
```

UI 測試在無顯示器環境也能執行；沒有 GPU 時 GPU 測試自動略過。

**2. 調整介面**

畫面以 MVVM 撰寫：版面在 `src/FilmBatch.App/Views/*.axaml`，邏輯在對應的 `ViewModels/`。色彩與控制項樣式集中在 `Themes/Colors.axaml`、`Themes/Controls.axaml`。以元件展示視窗檢查各種狀態：

```bash
dotnet run --project src/FilmBatch.App -- --gallery
```

**3. 新增或修改效果參數**

模組參數只定義一次，介面面板、存檔與貼上都由它自動產生：

1. 在 `src/FilmBatch.Core/Params/ModuleCatalog.cs` 新增或修改參數。
2. 在 `src/FilmBatch.Engine/Rendering/CpuPipeline.cs` 實作 CPU 參考版本。
3. 在 `src/FilmBatch.Gpu/Shaders/v1/` 修改對應的 WGSL shader，並於 `GpuEffectPipeline.cs` 串接。
4. 執行 `dotnet test` 確認 CPU／GPU 結果一致，並以 `dotnet run build/CheckShaders.cs` 驗證 shader。

**4. 新增介面文字**

字串放在 `src/FilmBatch.App/Localization/Strings.resx`（繁中）與 `Strings.en.resx`（英文），XAML 以 `{l:Tr Key}` 引用。

**5. 打包發佈**

```bash
dotnet run build/Build.cs -- --rid win-x64 --zip
```

macOS（Apple Silicon）改用 `osx-arm64`；不再提供 Intel Mac 建置。

## 文件

- [文件地圖](docs/README.md)：依任務導向現行文件、歷史決策與尚未落地的提案
- [驗證入口](VERIFY.md)：從隔離測試、WGSL、App／CLI 到證據與失敗分類
- [決策與提交歷史](docs/history.md)：以真實 commit 追溯理由、替代方案與後續取代關係

- [docs/architecture.md](docs/architecture.md)：專案架構與算圖流程
- [docs/building.md](docs/building.md)：建置、測試、CI、發佈與 CLI 用法
- [docs/adr/0003-gpu-stack-decision.md](docs/adr/0003-gpu-stack-decision.md)：CPU SIMD、RAW GPU 收尾與 Halide AOT 選型紀錄
- [docs/film-looks.md](docs/film-looks.md)：底片風格的 LUT 來源與授權、演算法與新增底片流程
- [docs/plan.md](docs/plan.md)：歷史產品設計稿；現行行為以架構與驗證文件為準
- [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)：第三方元件授權
