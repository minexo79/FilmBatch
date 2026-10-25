<p align="center">
  <img src="res/snapshot.png" width="800" />
</p>

<p align="center">
  <sub>封面模特：腦腐 · Instagram：<a href="https://www.instagram.com/ouo.207/">@ouo.207</a></sub>
</p>

<h1 align="center">FilmBatch</h1>

<p align="center">
  <b>批次底片效果處理工具</b><br>
  繁體中文 · <a href="README.md">English</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/UI-Avalonia%2012-8B44AC" alt="Avalonia 12">
  <img src="https://img.shields.io/badge/GPU-WebGPU-1A8F5A" alt="WebGPU">
  <img src="https://img.shields.io/badge/平台-Windows%20%7C%20macOS-lightgrey" alt="Windows | macOS">
  <img src="https://img.shields.io/badge/授權-MIT-blue" alt="MIT 授權">
</p>

FilmBatch 可為每張照片套上底片色彩與類比效果，再一次批次匯出。以 C# / .NET 10、Avalonia 12 與 WebGPU 開發，支援 Windows 與 macOS。

## 截圖

| 瀏覽與批次 | 底片風格 | 批次匯出 |
| :---: | :---: | :---: |
| *（截圖即將補上）* | *（截圖即將補上）* | *（截圖即將補上）* |

## 功能簡介

| 功能 | 說明 |
| --- | --- |
| **漏光** | 可調整的漏光疊加效果，營造溫暖、意外曝光的氛圍。 |
| **膠片感** | 底片色彩模擬，內附 36 卷底片 LUT，可調效果比例、暖色、褪色與八色相帶修正。 |
| **顆粒** | 可重現的顆粒，提供團塊、多尺度與光學統計三種模型；可依暗部、中間調與高光調整 seed 與響應。 |
| **AI 降噪** | 本機 AI 降噪（RAW 與一般 RGB 模型）。模型需明確下載或匯入，推論過程不連線。 |
| **AI 去背** | macOS 上使用 Apple Vision 在本機選取主體，不上傳照片、不生成背景。 |
| **批次處理** | 開啟整個資料夾、一次選多張照片套用同一組設定，也可為每張照片指定不同預設。 |
| **批次導出** | 匯出 JPEG、16-bit TIFF 或 16-bit PNG，保留 EXIF 並嵌入 sRGB ICC；原始檔永不修改。 |

更多細節（包含支援的 RAW 相機與命令列用法）請見 [docs/building.md](docs/building.md) 與 [docs/film-looks.md](docs/film-looks.md)。

## 如何建置

### 需求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows 10/11 或 macOS（AI 去背需要 Apple Silicon）

### 執行

```bash
dotnet run --project src/FilmBatch.App
```

開啟後以「檔案 → 開啟資料夾」（`Ctrl/⌘+O`）選擇照片資料夾即可。

### 建置與測試

```bash
dotnet build FilmBatch.sln
dotnet test FilmBatch.sln
```

### 打包發佈

```bash
dotnet run build/Build.cs -- --rid win-x64 --zip
```

macOS（Apple Silicon）請改用 `osx-arm64`。

元件展示視窗、shader 驗證、iOS 建置等其他流程請見 [docs/building.md](docs/building.md) 與 [VERIFY.md](VERIFY.md)。

## 如何貢獻

歡迎貢獻！

1. Fork 本專案並建立功能分支。
2. 保持變更聚焦，並遵循既有的 MVVM 與專案結構。
3. 送出 Pull Request 前，請先執行 `dotnet build FilmBatch.sln` 與 `dotnet test FilmBatch.sln`。
4. 若修改效果，請同時更新 CPU 參考實作與 WGSL shader，並確認兩者結果一致（見 [docs/architecture.md](docs/architecture.md)）。
5. 在 Pull Request 中說明變更內容與驗證方式。

發現錯誤或有想法？歡迎開 issue 討論。

## Star 歷史

[![Star History Chart](https://api.star-history.com/svg?repos=minexo79/FilmBatch&type=Date)](https://www.star-history.com/#minexo79/FilmBatch&Date)

## 文件

- [docs/README.md](docs/README.md)：文件地圖（現行文件、歷史決策與尚未落地的提案）
- [docs/architecture.md](docs/architecture.md)：專案架構與算圖流程
- [docs/building.md](docs/building.md)：建置、測試、CI、發佈與 CLI 用法
- [docs/film-looks.md](docs/film-looks.md)：底片 LUT 來源與授權、新增底片流程
- [docs/history.md](docs/history.md)：決策與提交歷史
- [VERIFY.md](VERIFY.md)：驗證入口
- [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)：第三方元件授權

## 協議

FilmBatch 以 [MIT 授權](LICENSE)釋出。

第三方元件與內附素材（包含依 CC BY-SA 4.0 授權的 RawTherapee Film Simulation LUT，以及移植自 [LightCraft](https://github.com/storytold/lightcraft) 的 RAW 解碼）列於 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
