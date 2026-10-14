# M0 狀態：UI 規範、原型與 GPU spike

> 日期：2026-10-14　範圍：依指示只建置 **Windows**；macOS / Linux 的發佈冒煙建置延後。

## 使用方式

```bash
dotnet run --project src/FilmBatch.App                 # 原型（檔案 → 開啟範例資料夾）
dotnet run --project src/FilmBatch.App -- --gallery    # 元件展示視窗
dotnet run --project src/FilmBatch.Cli -- spike        # GPU spike 量測（--backend Vulkan、--low-power、--width/--height）
dotnet test FilmBatch.sln                              # Core 28 項 + UI（Headless）14 項
dotnet run build/Build.cs -- --rid win-x64 --smoke --zip   # 發佈 + 冒煙測試 + 壓縮
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
| 7 | 發佈冒煙建置 | **僅 Windows** | `build/Build.cs`、`.github/workflows/ci.yml` |
| 8 | UI 規範定稿 | **未完成**：需要你實際操作原型後的回饋 | — |

## 驗收標準（計畫書 16.1）

- [x] 介面灰階皆為中性灰：配色 token 與覆寫的 Fluent 系統色皆 R=G=B；展示視窗逐一標示，快照測試檢查工具列像素。
- [x] 元件展示視窗涵蓋一般、停用、被覆寫、多選狀態。停留／按下為互動狀態，需以滑鼠檢查（未做強制狀態的靜態展示）。
- [x] 原型可開啟範例資料夾，各面板可操作；解碼與算圖在背景，UI 執行緒不阻塞。
- [x] 指定預設、單張微調、三種貼上、復原／重做已串接三層模型（有測試）。
- [x] 滑桿只在取得焦點時接受滾輪（有 Headless 測試）。
- [x] 停靠版面重新啟動後還原（`layout.json`；有測試，發佈版冒煙測試也驗證）。
- [~] GPU spike：Windows DX12 / Vulkan 完整路徑可運作，1600px 拖曳更新約 2–3 ms（遠高於 30 fps）。**軟體介面卡未達成**（Dawn 建置不含軟體介面卡，見 ADR）；無 GPU 時以 CPU 參考實作降級。macOS / Linux 未驗證。
- [x] 已定案 C# WebGPU 綁定並記錄理由（WebGPUSharp / Dawn）。
- [~] `dotnet publish`：Windows 完成且冒煙測試通過；macOS / Linux 依指示延後。

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

## 尚未納入 M0 的項目（依計畫書屬於後續里程碑）

- 預設 YAML／專案 JSON 讀寫、預設快照同步對話框（M2）；預設庫目前為記憶體內的範例資料，關閉後不保存。
- 實際效果模組：spike 只實作曝光、亮度、飽和與高斯模糊（散景模糊量驅動），其他模組參數可調但不影響畫面（M1／M5／M6）。
- 預設滑鼠停留的效果縮圖（目前以文字摘要代替，M3）；偏好設定中 GPU 相關選項於下次啟動生效（M3 起即時重建裝置）。
- macOS 原生選單列（NativeMenu）、自繪標題列驗證；naga 驗證 WGSL（CI 尚未安裝 naga）。
