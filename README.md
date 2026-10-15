# FilmBatch

批次底片效果處理工具（C# / .NET 10、Avalonia 12、WebGPU），提供 Windows 與 macOS GUI／CLI、非破壞式編輯、預設與專案保存，以及 JPEG／16-bit TIFF／PNG 匯出。

## 需求

- Windows 10 / 11 x64；macOS 封裝的最低版本依實際原生相依檔計算。重建 Dawn 後，Apple Silicon 的相依下限為 14.0、Intel 為 15.0；**本次執行驗證僅涵蓋 macOS 26，較舊系統仍需實機驗證**
- 從原始碼建置需要 .NET 10 SDK（版本鎖定於 `global.json`）；發佈版已內附執行階段，不需另外安裝 .NET
- Windows 使用 DX12 / Vulkan，macOS 使用 Metal；沒有可用 GPU 時自動改用 CPU 參考算圖器
- macOS 封裝需在 Mac 執行，使用系統的 `sips`、`iconutil`、`codesign`、`ditto`
- GUI 不啟用 trimming：Avalonia／Dock 的反射路徑需要完整組件；不以壓掉警告的方式宣稱修剪相容

## 建置

```bash
dotnet build FilmBatch.sln
```

```bash
dotnet run --project src/FilmBatch.App
```

## 測試

```bash
dotnet test FilmBatch.sln
```

UI 測試以 Avalonia.Headless 執行，不需要顯示器；沒有 GPU 時 GPU 測試自動略過。

### 合併前 CI

`.github/workflows/ci.yml` 在 pull request、merge queue（`merge_group`）、推送至 `master` 及手動觸發時執行：

- Windows x64、macOS Apple Silicon／Intel：還原相依套件、Release 建置、測試、封裝與 GUI 冒煙測試。
- WGSL：使用固定版本的 naga-cli 與產品共用的 `ShaderLibrary`，驗證編譯後嵌入的所有 shader。
- `pre-merge`：只有上述工作全部成功才通過；失敗、取消或略過均不放行。

請在 `master` 的分支保護或 ruleset 中，將 **`pre-merge`** 設為必要狀態檢查，才能阻擋未通過的合併；工作流程本身不會修改儲存庫規則。`merge_group` 觸發方式依 [GitHub 官方說明](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#merge_group) 設定。

本機可執行相同的 shader 驗證：

```bash
cargo install --locked --version 30.0.1 naga-cli
dotnet run build/CheckShaders.cs
```

## 發佈

### macOS

在 Mac 執行（`--smoke` 需要可顯示視窗的桌面工作階段）：

```bash
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
open dist/osx-arm64/FilmBatch.app
```

Intel Mac 將 `osx-arm64` 改為 `osx-x64`；Apple Silicon 使用原生 `osx-arm64`，不需要 Rosetta。省略 `--rid` 時，Mac 會依目前作業系統回報的架構選擇目標。

NuGet 內附的 Dawn 要求 macOS 26.0。若要使用較低部署目標，先重建相同 commit 的原生函式庫：

```bash
dotnet restore FilmBatch.sln
bash build/build-dawn.sh all
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
```

需 Xcode Command Line Tools（含 Metal SDK）、CMake、Python 3；在 Apple Silicon 建置 x64 還需 Rosetta。腳本產生 `artifacts/dawn/{osx-arm64,osx-x64}/webgpu_dawn.dylib` 與量測來源記錄 `PROVENANCE.txt`，核對架構、部署目標及 WebGPU 匯出符號。封裝器只整檔取代，不修改 Mach-O 載入命令；沒有重建檔時，仍使用最低版本 26.0 的 NuGet 版本。

輸出：

- `dist/osx-arm64/FilmBatch.app`：可在 Finder 開啟，也可複製到「應用程式」資料夾
- `dist/osx-arm64/cli/filmbatch-cli`：獨立命令列工具
- `dist/smoke-osx-arm64.txt`／`.png`：GUI 工作流程結果與實際視窗的 Avalonia 內容擷取（不含原生系統選單列／標題列）
- `dist/FilmBatch-osx-arm64.zip`：含 `.app`、CLI 與授權聲明的封裝；使用 Finder 或 `ditto` 解壓縮以保留簽章延伸屬性

```bash
dist/osx-arm64/cli/filmbatch-cli gpu-info --backend Metal
dist/osx-arm64/cli/filmbatch-cli spike --backend Metal
open dist/osx-arm64/FilmBatch.app --args --open "/path/to/photos"
```

快捷鍵使用 ⌘（例如 ⌘O、⌘Z、⌘⇧Z）；macOS 使用原生選單列。設定、版面、預設、配方與記錄檔位於 `~/Library/Application Support/FilmBatch/`；每個照片資料夾的專案與匯出紀錄位於 `.filmbatch/`。

預設封裝只有供本機測試的 **ad-hoc 簽章，尚未公證**，不等於可直接對外散佈的 Developer ID 發行版。網路下載的封裝可能被 Gatekeeper 阻擋。正式發行需自己的 Developer ID Application 憑證與已儲存的公證憑證：

```bash
xcrun notarytool store-credentials "FilmBatch-notary"
dotnet run build/Build.cs -- --rid osx-arm64 \
  --sign "Developer ID Application: Your Name (TEAMID)" \
  --notary-profile "FilmBatch-notary" --zip
```

也可使用 `FILMBATCH_SIGN_IDENTITY`／`FILMBATCH_NOTARY_PROFILE` 環境變數。流程先檢查簽章身分，再由內而外簽署、提交完整 GUI／CLI 套件、公證訂票及 Gatekeeper 檢查；缺少身分或 profile 直接失敗，不會退回 ad-hoc。密碼不放在建置參數或記錄檔。本工作階段沒有 Developer ID 憑證，因此只驗證本機簽章與缺少憑證時的拒絕路徑，未執行正式公證。

### Windows

```bash
dotnet run build/Build.cs -- --rid win-x64 --smoke --zip
```

輸出：

- `dist/win-x64/`：GUI（`FilmBatch.exe`）；命令列工具在 `dist/win-x64/cli/filmbatch-cli.exe`
- `dist/smoke-win-x64.txt`：發佈版冒煙測試結果
- `dist/FilmBatch-win-x64.zip`：壓縮檔

## 編輯與匯出

- 編輯以專案 JSON 與預設 YAML 保存，不改寫來源照片；重新開啟時保留原預設快照，需明確同步才採用更新的預設。
- 16 個效果模組共用 CPU／GPU 管線；預覽與原尺寸 16-bit 匯出共用效果設定。超過 GPU 尺寸或記憶體限制時，明確標示改由 CPU 處理。
- 內建 77 組 Recipe Lab 配方與格式 1 自訂配方匯入／匯出；未支援的相機 Picture Effect 會明確提示，不宣稱完整模擬 Sony 相機。來源與校正狀態見 [配方來源](docs/film-provenance.md)及[色彩校正](docs/film-calibration.md)。
- 預設懸停顯示實際算圖；GPU、代理圖尺寸、紋理資料夾與記憶體偏好可立即套用。
- GUI 的照片匯出面板可選範圍、格式、尺寸與命名；輸出保留可用的 EXIF 拍攝中繼資料、正規化方向、嵌入 sRGB ICC，避免覆寫來源或既有輸出。
- EXIF 解析失敗不會阻止可解碼照片的影像匯出；JPEG EXIF 超過 APP1 的 65,533 位元組上限時，先移除 MakerNote，仍超限才改用新的方向、尺寸與 sRGB 宣告。這些捨棄或縮減會記錄於 `.filmbatch/export_log.json` 的逐張 `MetadataWarning` 欄位，不代表完整保留原始 metadata 封包。

```bash
dist/osx-arm64/cli/filmbatch-cli export "/path/to/photos" --out "/path/to/output" --format png
dist/osx-arm64/cli/filmbatch-cli export "/path/to/photos" --format jpg --quality 95 --long-edge 3000
dist/osx-arm64/cli/filmbatch-cli recipes list
dist/osx-arm64/cli/filmbatch-cli recipes import "/path/to/recipes"
```

CLI 的 `--software` 會要求軟體 GPU 介面卡；若不可用則以 CPU 算圖並顯示原因。匯出使用已保存的專案設定，CLI 不會自動更新預設快照。可用 `--presets-folder` 指定獨立預設庫。驗證紀錄與未驗證環境見[狀態與限制](docs/M0-status.md)。

## 文件

- [plan.md](plan.md)：計畫書
- [docs/M0-status.md](docs/M0-status.md)：原型歷程、修復進度與驗證邊界
- [docs/adr/0001-webgpu-binding.md](docs/adr/0001-webgpu-binding.md)：WebGPU 綁定選型
- [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)：第三方元件授權
