# M0 狀態：UI 規範、原型與 GPU spike

> 更新：2026-10-15。原 Windows M0 原型已加入 macOS `.app`／CLI 封裝；Apple Silicon 原生 Metal 路徑已驗證，Linux 仍未驗證。

## 使用方式

```bash
dotnet run --project src/FilmBatch.App                 # 原型（檔案 → 開啟範例資料夾）
dotnet run --project src/FilmBatch.App -- --gallery    # 元件展示視窗
dotnet run --project src/FilmBatch.Cli -- spike        # GPU spike 量測（--backend Vulkan、--low-power、--width/--height）
dotnet test FilmBatch.sln                              # Core 28 項 + UI（Headless）17 項
dotnet run build/Build.cs -- --rid win-x64 --smoke --zip   # 發佈 + 冒煙測試 + 壓縮
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip # Mac：.app + CLI；Intel 使用 osx-x64
```

UI 截圖由 `ThemeSnapshotTests` 產生於 `tests/FilmBatch.App.Tests/bin/Debug/net10.0/snapshots/`。

## 交付物（計畫書 2.11）

| # | 交付物 | 狀態 | 位置 |
|---|---|---|---|
| 1 | Colors.axaml + Controls.axaml | 完成 | `src/FilmBatch.App/Themes/` |
| 2 | 元件展示視窗（`--gallery`） | 完成 | `src/FilmBatch.App/Gallery/` |
| 3 | 可操作原型（Dock 版面、縮圖列、預設庫、模組面板、預覽畫布） | 完成 | `Views/`、`ViewModels/`、`Controls/` |
| 4 | 對話框原型（選擇性貼上、匯出、預設管理、偏好設定） | 完成；選擇性貼上可實際使用，其餘為原型 | `Dialogs/` |
| 5 | 資料流：三層設定模型 + 復原堆疊 | 完成 | `src/FilmBatch.Core/` |
| 6 | GPU spike | 完成（綁定已改選，見 ADR） | `src/FilmBatch.Gpu/`、`docs/adr/0001-webgpu-binding.md` |
| 7 | 發佈冒煙建置 | Windows（原紀錄）＋macOS（見下方驗證紀錄） | `build/Build.cs`、`.github/workflows/ci.yml` |
| 8 | UI 規範定稿 | **未完成**：需要你實際操作原型後的回饋 | — |

## 驗收標準（計畫書 16.1）

- [x] 介面灰階皆為中性灰：配色 token 與覆寫的 Fluent 系統色皆 R=G=B；展示視窗逐一標示，快照測試檢查工具列像素。
- [x] 元件展示視窗涵蓋一般、停用、被覆寫、多選狀態。停留／按下為互動狀態，需以滑鼠檢查（未做強制狀態的靜態展示）。
- [x] 原型可開啟範例資料夾，各面板可操作；解碼與算圖在背景，UI 執行緒不阻塞。
- [x] 指定預設、單張微調、三種貼上、復原／重做已串接三層模型（有測試）。
- [x] 滑桿只在取得焦點時接受滾輪（有 Headless 測試）。
- [x] 停靠版面重新啟動後還原（`layout.json`；有測試，發佈版冒煙測試也驗證）。
- [~] GPU spike：Windows DX12 / Vulkan 完整路徑可運作，1600px 拖曳更新約 2–3 ms（原紀錄）；macOS Apple M3 / Metal 已驗證，見 ADR。**軟體介面卡未達成**（Dawn 建置不含軟體介面卡）；無 GPU 時以 CPU 參考實作降級。Linux 未驗證。
- [x] 已定案 C# WebGPU 綁定並記錄理由（WebGPUSharp / Dawn）。
- [~] `dotnet publish`：Windows 原紀錄完成；macOS self-contained `.app` 與 CLI 已完成本機封裝和冒煙測試，只有 ad-hoc 簽章、尚未公證。Linux 延後。

## M0 發現、已採取的處理與待決事項

| 發現 | 處理 | 待決 |
|---|---|---|
| Silk.NET.WebGPU 2.23 在 DX12 建立 compute pipeline 時存取違規（含 WARP） | 改用 WebGPUSharp（Dawn） | — |
| Dawn 建置沒有軟體介面卡 | CPU 參考實作降級 + 狀態列警示 | M1 前：自建含 SwiftShader 的 Dawn，或另做 wgpu-native 實作，或接受降級（ADR 選項 1–3） |
| GUI `FilmBatch.exe` 與 CLI `filmbatch.exe` 在不分大小寫的檔案系統上同名衝突 | CLI 改名 `filmbatch-cli` | 是否讓 GUI 執行檔兼任 CLI 子命令 |
| 兩個 self-contained 發佈疊在同一資料夾（GUI 有 trimming）會讓 GUI 啟動失敗 | CLI 暫放 `cli\` 子資料夾 | M7 定案：共用資料夾的發佈方式（例如單一發佈或不修剪） |
| Dock.Avalonia 的序列化與樣板以反射為主 | Dock 組件整個保留不修剪（`TrimmerRootAssembly`） | Native AOT 評估時再確認 |
| `{l:Tr}` 原本用反射繫結，發佈時 trim 分析報錯 | 改為 IObservable 繫結 | — |
| 全域 Button Padding 會擠掉 Dock 標題列按鈕圖示 | 移除全域樣式 | — |
| 標題列與選單列合一（需求變更） | 內容延伸進標題列；空白處標為原生拖曳區（已以 `WM_NCHITTEST` 驗證為 HTCAPTION），隱藏平台繪製的標題文字（該元素不在樣式樹中，改以程式隱藏）；計畫書 2.2.1 已更新 | macOS／Linux 的按鈕位置於跨平台建置時驗證 |
| macOS `codesign` 將 `Contents/MacOS` 內的受控 DLL 也當作巢狀程式碼 | 先逐檔簽署（不含 apphost），最後簽署 `.app`；以 `ditto` 保存延伸屬性 | 正式發行仍需 Developer ID、公證 |
| Headless 測試固定使用 Windows 字型，在 Mac 無法建立 glyphTypeface | 與 GUI 共用 `Program.FontOptions()`，使用 PingFang TC 與相同後備字型 | — |
| Mac 偏好設定缺少 Metal、快捷鍵提示固定為 Ctrl | 依平台顯示後端選項、選單手勢、工具提示與空白畫面快捷鍵 | — |
| WebGPUSharp 0.5.7 的兩個 Mac Dawn 原生檔都宣告 `minos 26.0`（`vtool -show-build`） | `.app` 的 `LSMinimumSystemVersion`、README 與 Mac CI runner 一律使用 macOS 26 | 支援較舊 macOS 需另取得並驗證相容的 Dawn 建置，不能只降低 plist 版本 |
| CI 偶發預覽已完成但狀態列仍為 `…` | `RenderScheduler` 不再於建構函式啟動；先完成 scheduler → service → ViewModel 的事件訂閱，再明確 `Start()`。保留原本 `CPU` 斷言，新增啟動／備援順序回歸 | 隔離的 CI 修正快照重新建置後，`dotnet test FilmBatch.sln -c Release --no-build`：Core 30、UI 17 全數通過、無略過；同步派送的 100 次 CPU 初始化與實際像素輸出冒煙亦通過 |

## macOS 驗證紀錄（2026-10-15）

- `dotnet test FilmBatch.sln -c Release`：Core 28、UI 17 全數通過，無略過；包含 Metal 與 CPU 參考結果比對。
- `osx-arm64` 發佈版冒煙：視窗、Magick.Native、Dawn / Metal、3 張範例照片、預覽、縮圖、參數覆寫／復原、Dock 版面往返、展示視窗均通過。
- `osx-x64` 也完成封裝、簽章驗證、GUI 全部冒煙項目及 CLI `gpu-info`，執行環境為 Apple Silicon 上的 **Rosetta**，不等於實體 Intel Mac 驗證。
- ZIP 以 `ditto` 解壓縮至含空白的路徑後，`codesign --verify --deep --strict` 通過；再以 LaunchServices `open -n -W` 啟動冒煙模式通過。驗證時將 `DOTNET_ROOT` 指向不存在的目錄，確認不依賴外部 .NET。
- GUI 實際開啟含空白路徑的 12 張 TIFF 範例，原生視窗已顯示。此工作階段沒有螢幕擷取／Apple Events 權限，因此不能聲稱已人工驗證原生標題列按鈕或鍵盤互動；Headless 渲染快照不等於原生視窗截圖。
- CI 已加入 macOS 26 Apple Silicon 與 Intel runner；本機執行不代表 GitHub Actions 已執行。Windows 本次未重跑；實體 Intel Mac、正式簽章／公證仍需各自驗證。較舊 macOS 不在目前封裝支援範圍。
- 既有 Avalonia / Dock 第三方修剪警告仍存在；不抑制新增警告，發佈版以實際冒煙確認目前使用路徑。


## 尚未納入 M0 的項目（依計畫書屬於後續里程碑）

- 預設 YAML／專案 JSON 讀寫、預設快照同步對話框（M2）；預設庫目前為記憶體內的範例資料，關閉後不保存。
- 實際效果模組：spike 只實作曝光、亮度、飽和與高斯模糊（散景模糊量驅動），其他模組參數可調但不影響畫面（M1／M5／M6）。
- 預設滑鼠停留的效果縮圖（目前以文字摘要代替，M3）；偏好設定中 GPU 相關選項於下次啟動生效（M3 起即時重建裝置）。
- macOS 原生選單列（NativeMenu）、自繪標題列驗證；naga 驗證 WGSL（CI 尚未安裝 naga）。
