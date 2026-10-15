# 修復狀態與驗證紀錄

> 更新：2026-10-15。由 Windows M0 原型延伸為可保存編輯、實際算圖及批次匯出的 Windows／macOS 程式。本頁區分已執行的驗證、歷史紀錄與仍需外部環境的項目；不以跨平台編譯代替實機驗證。

## 使用方式

```bash
dotnet run --project src/FilmBatch.App
dotnet run --project src/FilmBatch.App -- --gallery
dotnet run --project src/FilmBatch.Cli -- export "/path/to/photos" --format png --software
dotnet test FilmBatch.sln -c Release
dotnet run build/CheckShaders.cs
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
```

Mac 封裝、較低部署目標的 Dawn 重建及正式簽章參數見 [README](../README.md)。元件快照由 `ThemeSnapshotTests` 產生；封裝冒煙流程另外輸出 `dist/smoke-<rid>.txt` 與實際視窗的 Avalonia 內容擷取 `.png`。

## 已修復的功能

| 項目 | 行為與實作位置 |
|---|---|
| 專案與預設保存 | `Core/Projects`、`Core/Presets`：JSON 專案、YAML 預設、覆寫、種子與預設快照往返；原子寫入，不改寫來源照片；損毀檔不以空白資料覆蓋 |
| 預設更新 | 已保存的專案保留自己的快照；只有明確同步才套用外部更新；支援外部預設庫重新載入 |
| GUI／CLI 批次匯出 | 共用 `Engine/Exporting/BatchExporter`，實際解碼、16-bit 算圖、縮放、編碼；支援 JPEG 品質、PNG16、ZIP TIFF16、範圍、命名、進度、取消、逐檔錯誤與 `export_log.json` |
| 匯出資料安全 | 拒絕覆寫來源與既有輸出；暫存寫入完成才搬移；取消或失敗不留下假完成檔 |
| 色彩與中繼資料 | 來源 ICC 先轉換為 sRGB，再進入效果管線；輸出嵌入 sRGB ICC，方向正規化；EXIF 拍攝時間、相機資訊、曝光與 GPS 往返有實際檔案驗證 |
| TIFF EXIF | Magick.NET 的 TIFF 編碼器不寫 EXIF 子目錄，讀取時也只提供格式化屬性。`TiffMetadata` 讀取有界的 metadata 目錄、補寫輸出目錄，不複製或重編碼像素區塊；回歸涵蓋兩種位元組順序及 classic TIFF／BigTIFF 來源 |
| 中繼資料異常 | 可解碼的照片不因來源 EXIF 解析失敗而整張匯出失敗；若因此或為符合 JPEG 上限而捨棄或縮減 EXIF，逐張寫入 `.filmbatch/export_log.json` 的 `MetadataWarning`，影像成功輸出的 `Status` 仍為 `ok` |
| 全部效果模組 | 16 個模組有 CPU 與真正的 WebGPU 算圖路徑；原尺寸輸出保留 16-bit 精度。外部雙重曝光照片、紋理路徑、空紋理庫的程序化效果、每張照片的確定性種子均已串接 |
| 底片配方 | 77 組 Recipe Lab 配方、12 個品牌群組、格式 1 自訂配方、EDITED／重設與匯入／匯出；曝光使用精確的 1/3 EV，不使用畫面顯示的四捨五入值 |
| 預設懸停 | 對目前照片實際算圖；有 debounce、有界快取及過期結果排除，不再以文字摘要假裝預覽 |
| 即時偏好 | GPU 後端、CPU 模式、代理圖尺寸、記憶體預算與紋理資料夾可立即套用；在算圖執行緒重建，丟棄前一代結果，重建預覽及縮圖 |
| 初始化競態 | scheduler → service → ViewModel 完成事件訂閱後才 `Start()`，避免已算出預覽但裝置名稱仍為 `…` |
| Mac 整合 | 原生選單列、Command 手勢、偏好設定後端選項與標題列配置；NativeMenu 不以 Headless 結構測試冒充原生互動證據 |
| 發佈修剪 | GUI 與 CLI 不啟用 trimming；Avalonia／Dock 反射路徑保留，不以 warning suppression 隱藏相容性問題；兩個 self-contained 程式分開封裝 |
| Shader／合併閘門 | 釘選 naga-cli 驗證 include 展開後的 WGSL；Windows 與兩種 Mac 架構工作加上 `pre-merge` 彙總。儲存庫仍須將此檢查設為必要狀態 |

底片數值的來源、相機效果支援範圍與校正結果分別見 [配方來源](film-provenance.md)及[色彩校正](film-calibration.md)。配方欄位吻合、CPU／GPU 一致，均不代表與實體相機的色彩誤差已達標。

## macOS 原生相依套件與發行

- WebGPUSharp 0.5.7 的 NuGet Dawn 原生檔要求 macOS 26.0。`build/build-dawn.sh` 釘選同源 commit `4178cb7e771b700743bc489037390777b98be39d`，實際重建 arm64 與 x64，兩者 `vtool -show-build` 的 `minos` 均為 **14.0**，各有 277 個 `_wgpu*` 匯出符號；來源、雜湊與建置參數在 `artifacts/dawn/PROVENANCE.txt`。
- 封裝只整檔取代 Dawn，不修改 Mach-O 載入命令。最低系統版本取所有 Mach-O 的最高需求：重建 Dawn 後，Apple Silicon 為 14.0；Intel 因 Magick.NET 原生相依套件為 15.0。沒有重建產物時仍使用 NuGet 的 26.0 要求。
- 上述數字是量測的部署目標，不是舊系統實機通過紀錄。本次載入與算圖在 macOS 26 執行；macOS 14／15 尚無測試機。
- `build/Build.cs` 支援 Developer ID、hardened runtime、公證 keychain profile、staple 與 Gatekeeper 檢查。缺少身分或 profile 直接失敗，不會改用 ad-hoc 假裝正式發行。
- 本機已知沒有有效 Developer ID 身分，未讀取私人憑證、未提交公證。交付的 ZIP 只有 **ad-hoc 簽章**；適合本機試用，不是可保證通過網路下載 Gatekeeper 的正式發行版。

## 已執行的驗證

- Release 整合測試：Core 99 項、Avalonia Headless 40 項全數通過，無略過；包含保存／重開、匯出安全、真 16-bit、metadata、懸停、即時偏好、scheduler 狀態轉換及快捷鍵復原／重做。13 項 TIFF metadata 回歸涵蓋大小端序、classic TIFF／BigTIFF、sRGB interoperability、來源圖層排除、損毀目錄與 JPEG 大型 EXIF。
- 真正的 CPU 與 Apple M3／Metal 算圖：16 個模組在 128×85 測試影像的最大通道差為 0–1／65535。此為受測場景結果，不是對所有照片的誤差上界。曾發現 halation 中間紋理被重用覆寫，修正後通過。
- 最終 arm64 與 x64 封裝均通過 GUI 冒煙；x64 在 Apple Silicon 上以 **Rosetta** 執行。視窗、原生函式庫、三張範例照片、預覽、縮圖、編輯／復原、重開保留、懸停、即時代理圖、PNG16 匯出、ICC、來源雜湊、匯出紀錄、Dock 版面與元件展示均通過；結果與內容擷取在 `dist/smoke-osx-{arm64,x64}.{txt,png}`。
- 實際 metadata 匯出程式：PNG 來源 → TIFF16／PNG16／JPEG，以及 TIFF → PNG 再匯出；拍攝時間、曝光分數、GPS 與 sRGB ICC 保留。
- 本次 metadata 修正後，兩種 Mac 架構的正式封裝 CLI 各以 9 種來源情境匯出 JPEG／PNG16／TIFF16：有效來源、R03 色域宣告、失效 Interop 位址、Photoshop 圖層、欄位位址越界、不支援的型別、計數溢位、大型 MakerNote、大型描述。停用外部 .NET 查找後全部通過；檢查 R98／0100、ICC、方向與尺寸、拍攝時間、來源未修改、逐張警告，以及 PNG／TIFF 像素與 TIFF ZIP 壓縮。
- 最終兩份 Mac ZIP 解壓至含空白路徑後，GUI 與 CLI 均通過 `codesign --verify --strict`（GUI 另加 `--deep`），兩份 `.app` 均以 LaunchServices 啟動並完成冒煙。arm64 CPU GUI 與兩種架構的 CLI 也在 `DOTNET_ROOT` 指向不存在的位置、PATH 不含 SDK 時成功執行。
- 此次 metadata 修正前，解壓後的 CLI 另已驗證保存的專案與 YAML 自訂設定，分別走 arm64 CPU 備援與 x64／Rosetta Metal，完成 PNG16、ZIP TIFF16、JPEG 匯出。PNG／TIFF 與預期保存設定的 CPU 像素差分別為 0 與最多 1／65535；尺寸、方向、ICC、拍攝時間、GPS、來源雜湊及無效格式退出碼均通過。本次重新驗證上述 metadata 情境與 GUI 工作流程；GUI 結果在 `dist/smoke-osx-arm64-cpu.txt`、`dist/smoke-osx-{arm64,x64}-extracted.txt`。
- CI 競態修正獨立提交 `3ce9280`；合併前工作流程獨立提交 `371ad52`。隔離快照的 47 項測試、100 次同步 CPU 初始化／實際像素輸出及失敗閘門情境已驗證；這不代表遠端 GitHub Actions 已執行。

## 驗證邊界與剩餘限制

1. **實體 Intel、Windows、較舊 macOS 與 Linux**：本機無對應環境。Rosetta、跨平台 publish 和 CI YAML 都不等同實機測試。
2. **正式簽章／公證**：缺少使用者的 Developer ID Application 身分與公證 profile；已完成流程及拒絕路徑，未驗證 Apple 端成功路徑。
3. **原生互動外觀**：此工作階段沒有螢幕擷取／Apple Events 權限。實際視窗的 Avalonia 內容可擷取，但不含系統選單列與原生標題列；不能聲稱已人工驗證交通燈按鈕、滑鼠手勢及所有 Command 快捷鍵。
4. **相機模擬與色彩校正**：以校正文件中的實測結果為準；不支援的相機 Picture Effect 會顯示提示，不能解讀為已實作全部 Sony 效果。
5. **私有中繼資料**：不以原封不動的 metadata 封包備份為目的；移除舊縮圖與 Photoshop 來源圖層，更新方向、尺寸與色域。TIFF metadata 上限 16 MiB；影像布局／ICC／XMP 不混入 EXIF。Interoperability 重建為符合 sRGB 的 R98／0100，不沿用來源 R03 宣告或失效位址。JPEG EXIF 的 APP1 上限為 65,533 位元組，超限先移除 MakerNote；仍過大或來源 EXIF 解析失敗時，只保留新的方向、尺寸與 sRGB 宣告，並寫入 `MetadataWarning`。未移除的廠商 MakerNote，其私有相對位址仍不保證可搬移。
6. **UI 規範定稿**：功能驗證不取代實際使用者對操作與外觀的驗收。

## 保留的原型決策

- Silk.NET.WebGPU 在 DX12 建立 compute pipeline 曾發生存取違規，已改為 WebGPUSharp／Dawn，見 [ADR](adr/0001-webgpu-binding.md)。Dawn 沒有可用的軟體介面卡時，以明確標示的 CPU 參考實作處理，不宣稱是 GPU。
- CLI 使用 `filmbatch-cli` 並放在 `cli/`，避免不分大小寫檔案系統的名稱衝突，以及兩份 self-contained 框架互相覆寫。
- 中性灰 token、共用平台字型、滑桿焦點滾輪、復原／重做、三種貼上與 Dock 版面還原保留既有行為測試。
