# FilmBatch

批次底片效果處理工具（C# / .NET 10、Avalonia 12、WebGPU）。目前為 M0 原型，可建置 Windows 與 macOS。

## 需求

- Windows 10 / 11 x64，或 **macOS 26 以上**（Apple Silicon / Intel；目前內附的 Dawn 原生函式庫要求 26.0）
- 從原始碼建置需要 .NET 10 SDK（版本鎖定於 `global.json`）；發佈版已內附執行階段，不需另外安裝 .NET
- Windows 使用 DX12 / Vulkan，macOS 使用 Metal；沒有可用 GPU 時自動改用 CPU 參考算圖器
- macOS 封裝需在 Mac 執行，使用系統的 `sips`、`iconutil`、`codesign`、`ditto`

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

## 發佈

### macOS

在 Mac 執行（`--smoke` 需要可顯示視窗的桌面工作階段）：

```bash
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
open dist/osx-arm64/FilmBatch.app
```

Intel Mac 將 `osx-arm64` 改為 `osx-x64`；Apple Silicon 使用原生 `osx-arm64`，不需要 Rosetta。省略 `--rid` 時，Mac 會依目前作業系統回報的架構選擇目標。

輸出：

- `dist/osx-arm64/FilmBatch.app`：可在 Finder 開啟，也可複製到「應用程式」資料夾
- `dist/osx-arm64/cli/filmbatch-cli`：獨立命令列工具
- `dist/smoke-osx-arm64.txt`：原生函式庫、Metal／CPU 預覽、縮圖、復原與 Dock 版面的冒煙結果
- `dist/FilmBatch-osx-arm64.zip`：含 `.app`、CLI 與授權聲明的封裝；使用 Finder 或 `ditto` 解壓縮以保留簽章延伸屬性

```bash
dist/osx-arm64/cli/filmbatch-cli gpu-info --backend Metal
dist/osx-arm64/cli/filmbatch-cli spike --backend Metal
open dist/osx-arm64/FilmBatch.app --args --open "/path/to/photos"
```

快捷鍵使用 ⌘（例如 ⌘O、⌘Z、⌘⇧Z）；設定、版面與記錄檔位於 `~/Library/Application Support/FilmBatch/`。選單目前仍放在視窗內。

封裝只有供本機測試的 **ad-hoc 簽章，尚未公證**，不等於可直接對外散佈的 Developer ID 發行版。網路下載的封裝可能被 Gatekeeper 阻擋；正式發行仍需 Apple 開發者憑證與公證。簽章流程依 [Avalonia macOS 部署說明](https://docs.avaloniaui.net/docs/deployment/macos)。目前不支援 macOS 14／15：雖然 .NET 可在較舊系統執行，實際封裝的 `webgpu_dawn.dylib`（arm64 / x64）均宣告最低版本 26.0。

### Windows

```bash
dotnet run build/Build.cs -- --rid win-x64 --smoke --zip
```

輸出：

- `dist/win-x64/`：GUI（`FilmBatch.exe`）；命令列工具在 `dist/win-x64/cli/filmbatch-cli.exe`
- `dist/smoke-win-x64.txt`：發佈版冒煙測試結果
- `dist/FilmBatch-win-x64.zip`：壓縮檔

## 原型限制

本次 macOS 移植保留 M0 功能範圍：目前只有曝光、亮度、飽和與高斯模糊會影響影像；實際批次匯出及預設／專案檔保存尚未完成。請勿將原型當成正式照片處理交付工具；已驗證項目與未驗證邊界見 [M0 狀態](docs/M0-status.md)。

## 文件

- [plan.md](plan.md)：計畫書
- [docs/M0-status.md](docs/M0-status.md)：M0 進度、驗收狀態與待決事項
- [docs/adr/0001-webgpu-binding.md](docs/adr/0001-webgpu-binding.md)：WebGPU 綁定選型
- [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)：第三方元件授權
