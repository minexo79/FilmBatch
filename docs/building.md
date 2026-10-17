# 建置、測試與發佈

## 需求

- Windows 10 / 11 x64；macOS 封裝的最低版本依實際原生相依檔計算。重建 Dawn 後，Apple Silicon 的相依下限為 14.0（不再提供 Intel Mac 建置）；**目前執行驗證僅涵蓋 macOS 26，較舊系統仍需實機驗證**
- 從原始碼建置需要 .NET 10 SDK（版本鎖定於 `global.json`）；發佈版已內附執行階段，不需另外安裝 .NET
- Windows 使用 DX12 / Vulkan，macOS 使用 Metal；沒有可用 GPU 時自動改用 CPU 參考算圖器
- macOS 封裝需在 Mac 執行，使用系統的 `sips`、`iconutil`、`codesign`、`ditto`
- GUI 不啟用 trimming：Avalonia／Dock 的反射路徑需要完整組件；不以壓掉警告的方式宣稱修剪相容

## 建置與執行

```bash
dotnet build FilmBatch.sln
dotnet run --project src/FilmBatch.App
dotnet run --project src/FilmBatch.App -- --gallery   # 元件展示視窗
```

## 測試

```bash
dotnet test FilmBatch.sln -c Release
```

UI 測試以 Avalonia.Headless 執行，不需要顯示器；沒有 GPU 時 GPU 測試自動略過。

### RAW 語料測試

RAW 解碼與顯影另有對照 LightCraft 參考輸出的語料測試（逐位元的感光資料、色彩標籤與顯影階段，以及起始外觀擬合後的線性／顯示 RGB）。樣本與參考輸出不進版本庫；沒有設定 `FILMBATCH_RAW_SAMPLES`／`FILMBATCH_RAW_ORACLE` 時（例如 CI）這些測試會略過。取得方式、比較門檻與已知偏差見 [tools/raw-oracle/README.md](../tools/raw-oracle/README.md)。

### Shader 驗證

所有 WGSL 經 `ShaderLibrary` 展開 include 後，以固定版本的 naga 驗證：

```bash
cargo install --locked --version 30.0.1 naga-cli
dotnet run build/CheckShaders.cs
```

### 合併前 CI

`.github/workflows/ci.yml` 在 pull request、merge queue（`merge_group`）、推送至 `master` 及手動觸發時執行：

- Windows x64、macOS Apple Silicon：還原相依套件、Release 建置、測試、封裝與 GUI 冒煙測試。
- WGSL：使用固定版本的 naga-cli 與產品共用的 `ShaderLibrary`，驗證編譯後嵌入的所有 shader。
- `pre-merge`：只有上述工作全部成功才通過；失敗、取消或略過均不放行。

請在 `master` 的分支保護或 ruleset 中，將 **`pre-merge`** 設為必要狀態檢查，才能阻擋未通過的合併；工作流程本身不會修改儲存庫規則。`merge_group` 觸發方式依 [GitHub 官方說明](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#merge_group) 設定。

## 發佈

所有封裝都經由 `build/Build.cs`，GUI 與 CLI 各自以 self-contained 方式發佈。

### Windows

```bash
dotnet run build/Build.cs -- --rid win-x64 --smoke --zip
```

輸出：

- `dist/win-x64/`：GUI（`FilmBatch.exe`）；命令列工具在 `dist/win-x64/cli/filmbatch-cli.exe`
- `dist/smoke-win-x64.txt`：發佈版冒煙測試結果
- `dist/FilmBatch-win-x64.zip`：壓縮檔

### macOS

在 Mac 執行（`--smoke` 需要可顯示視窗的桌面工作階段）：

```bash
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
open dist/osx-arm64/FilmBatch.app
```

macOS 僅支援 Apple Silicon（`osx-arm64`），不再提供 Intel Mac（`osx-x64`）建置。省略 `--rid` 時，Mac 預設為 `osx-arm64`。

NuGet 內附的 Dawn 要求 macOS 26.0。若要使用較低部署目標，先重建相同 commit 的原生函式庫：

```bash
dotnet restore FilmBatch.sln
bash build/build-dawn.sh arm64
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
```

需 Xcode Command Line Tools（含 Metal SDK）、CMake、Python 3。腳本產生 `artifacts/dawn/osx-arm64/webgpu_dawn.dylib` 與量測來源記錄 `PROVENANCE.txt`，核對架構、部署目標及 WebGPU 匯出符號。封裝器只整檔取代，不修改 Mach-O 載入命令；沒有重建檔時，仍使用最低版本 26.0 的 NuGet 版本。

輸出：

- `dist/osx-arm64/FilmBatch.app`：可在 Finder 開啟，也可複製到「應用程式」資料夾
- `dist/osx-arm64/cli/filmbatch-cli`：獨立命令列工具
- `dist/smoke-osx-arm64.txt`／`.png`：GUI 工作流程結果與實際視窗的 Avalonia 內容擷取（不含原生系統選單列／標題列）
- `dist/FilmBatch-osx-arm64.zip`：含 `.app`、CLI 與授權聲明的封裝；使用 Finder 或 `ditto` 解壓縮以保留簽章延伸屬性

`--smoke`／GUI 啟動需要一個作用中的顯示器：螢幕休眠或無頭工作階段（`CGGetActiveDisplayList` 回 0）時 Avalonia 的 RenderTimer 會以原生錯誤 -6661 失敗。遠端操作時先 `caffeinate -u -t <秒>` 喚醒顯示器再執行。

```bash
dist/osx-arm64/cli/filmbatch-cli gpu-info --backend Metal
dist/osx-arm64/cli/filmbatch-cli spike --backend Metal
open dist/osx-arm64/FilmBatch.app --args --open "/path/to/photos"
```

#### 簽章與公證

預設封裝只有供本機測試的 **ad-hoc 簽章，尚未公證**，不等於可直接對外散佈的 Developer ID 發行版。網路下載的封裝可能被 Gatekeeper 阻擋。正式發行需自己的 Developer ID Application 憑證與已儲存的公證憑證：

```bash
xcrun notarytool store-credentials "FilmBatch-notary"
dotnet run build/Build.cs -- --rid osx-arm64 \
  --sign "Developer ID Application: Your Name (TEAMID)" \
  --notary-profile "FilmBatch-notary" --zip
```

也可使用 `FILMBATCH_SIGN_IDENTITY`／`FILMBATCH_NOTARY_PROFILE` 環境變數。流程先檢查簽章身分，再由內而外簽署、提交完整 GUI／CLI 套件、公證訂票及 Gatekeeper 檢查；缺少身分或 profile 直接失敗，不會退回 ad-hoc。密碼不放在建置參數或記錄檔。

## 命令列工具（CLI）

```bash
filmbatch-cli export "/path/to/photos" --out "/path/to/output" --format png
filmbatch-cli export "/path/to/photos" --format jpg --quality 95 --long-edge 3000
filmbatch-cli recipes list
filmbatch-cli recipes import "/path/to/recipes"
filmbatch-cli gpu-info
```

開發時可直接 `dotnet run --project src/FilmBatch.Cli -- <指令>`。

- 匯出使用已保存的專案設定，CLI 不會自動更新預設快照。
- `--software` 要求軟體 GPU 介面卡；若不可用則以 CPU 算圖並顯示原因。
- `--presets-folder` 指定獨立預設庫。
- GPU 算圖器匯出 RAW 時，顯示收尾沿用同一 Dawn 裝置，分帶處理並保留 CPU 降級原因；解碼與 demosaic 仍由 CPU 執行，直接預覽載入不變。
- 每筆 `export_log.json` 的 `Renderer` 會附上 `RAW GPU` 或 `RAW CPU（原因）`；整批使用 CPU 算圖器時維持原有 CPU 標示。精度、量測與 Halide AOT 取捨見 [ADR 0003](adr/0003-gpu-stack-decision.md)。

## 資料位置

| 內容 | 位置 |
|---|---|
| 設定、版面、預設、配方與記錄檔 | Windows：`%APPDATA%\FilmBatch\`；macOS：`~/Library/Application Support/FilmBatch/` |
| 每個照片資料夾的專案與匯出紀錄 | `<照片資料夾>/.filmbatch/` |

macOS 的快捷鍵使用 ⌘（例如 ⌘O、⌘Z、⌘⇧Z），並使用原生選單列：應用程式選單提供「關於」與「偏好設定」（⌘,），切換介面語言時選單列即時更新。
