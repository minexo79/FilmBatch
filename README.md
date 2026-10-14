# FilmBatch

批次底片效果處理工具（C# / .NET 10、Avalonia 12、WebGPU）。目前為 M0 原型，只建置 Windows。

## 需求

- Windows 10 / 11 x64
- .NET 10 SDK（版本鎖定於 `global.json`）
- 支援 DX12 或 Vulkan 的 GPU（沒有時自動改用 CPU 算圖）

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

```bash
dotnet run build/Build.cs -- --rid win-x64 --smoke --zip
```

輸出：

- `dist/win-x64/`：GUI（`FilmBatch.exe`）；命令列工具在 `dist/win-x64/cli/filmbatch-cli.exe`
- `dist/smoke-win-x64.txt`：發佈版冒煙測試結果
- `dist/FilmBatch-win-x64.zip`：壓縮檔

## 文件

- [plan.md](plan.md)：計畫書
- [docs/M0-status.md](docs/M0-status.md)：M0 進度、驗收狀態與待決事項
- [docs/adr/0001-webgpu-binding.md](docs/adr/0001-webgpu-binding.md)：WebGPU 綁定選型
- [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)：第三方元件授權
