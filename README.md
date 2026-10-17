# FilmBatch

批次底片效果處理工具。可為每張照片套上底片色彩與類比效果，再一次批次匯出。  
以 C# / .NET 10、Avalonia 12 與 WebGPU 開發，支援 Windows 與 macOS。

## 可以用來做什麼

- **像 Lightroom 一樣批次修圖**：開啟整個資料夾，以縮圖列瀏覽，一次選多張照片套用同一組設定，再整批匯出。
- **每張照片各有風格**：建立多個預設（例如「Portra 風 + 漏光」），每張照片可指定不同預設，並在預設上單張微調；只記錄差異，修改預設時其他照片自動同步。
- **底片色彩 & 類比效果**：有77組的底片配方 (來自 [Recipe Lab](https://github.com/voxivoid/recipe-lab-sony-pmca))、顆粒、光暈、漏光、暗角、焦外、動態模糊、鏡頭變形、雙重曝光、多格分鏡、灰塵刮痕等 16 個模組，以 GPU 即時預覽。
- **非破壞性編輯**：原始檔永不修改；匯出 JPEG／16-bit TIFF／16-bit PNG，保留 EXIF 並嵌入 sRGB ICC。
- **直接讀取 RAW**：Sony、Canon（CR2／CR3）、Nikon、Fujifilm、Panasonic、Olympus、Pentax、Samsung、Leica、Hasselblad、Epson、Mamiya 與 DNG，以相機內 JPEG 擬合起始色彩後顯影（解碼移植自 [LightCraft](https://github.com/storytold/lightcraft)）。不支援的壓縮（例如 Nikon HE、JPEG XL DNG）會直接回報，不以內嵌預覽代替。
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

- [docs/architecture.md](docs/architecture.md)：專案架構與算圖流程
- [docs/building.md](docs/building.md)：建置、測試、CI、發佈與 CLI 用法
- [docs/adr/0003-gpu-stack-decision.md](docs/adr/0003-gpu-stack-decision.md)：CPU SIMD、RAW GPU 收尾與 Halide AOT 選型紀錄
- [docs/film-provenance.md](docs/film-provenance.md)、[docs/film-calibration.md](docs/film-calibration.md)：底片配方來源與色彩校正
- [docs/plan.md](docs/plan.md)：完整計畫書
- [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)：第三方元件授權
