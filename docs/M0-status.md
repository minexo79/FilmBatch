# 修復狀態與驗證紀錄

> 更新：2026-10-16。由 Windows M0 原型延伸為可保存編輯、實際算圖及批次匯出的 Windows／macOS 程式。本頁區分已執行的驗證、歷史紀錄與仍需外部環境的項目；不以跨平台編譯代替實機驗證。

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
| 縮圖右鍵選單 | 複製設定、完整／僅預設／選擇性貼上、快速匯出與重設；保留既有多選，以右鍵照片作為複製來源，未選照片則改成單選；快速匯出只處理選取照片，重設可復原 |
| 編輯直方圖 | 目前預覽的 256 格 RGB 與顯示亮度、暗部／亮部裁切比例；僅繪圖高度採五格加權平滑，RGB 邊緣與亮度曲線抗鋸齒，保留原始統計與裁切比例；比較原圖時同步切換，分割比較顯示處理後統計；收合／隱藏不計算，切換照片丟棄過期結果，記住展開狀態 |
| Sony FL／IN 色度 | 飽和 -3 原先凍結在任意初值 0.1，改為從已擬合 -2／-1 外推的 0.329012；配方不變，保留刻意的低飽和，工具可重現，未用保留集調參 |
| 直幅漏光旋轉 | 程序化漏光以置中的投影與對應邊界計算，CPU／WGSL 同步修正；真實紋理路徑另驗證 180° 兩側及 0°／360°，不再因直幅旋轉而消失 |
| 預設懸停 | 對目前照片實際算圖；有 debounce、有界快取及過期結果排除，不再以文字摘要假裝預覽 |
| 即時偏好 | GPU 後端、CPU 模式、代理圖尺寸、記憶體預算與紋理資料夾可立即套用；在算圖執行緒重建，丟棄前一代結果，重建預覽及縮圖 |
| 初始化競態 | scheduler → service → ViewModel 完成事件訂閱後才 `Start()`，避免已算出預覽但裝置名稱仍為 `…` |
| Mac 整合 | 原生選單列、Command 手勢、偏好設定後端選項與標題列配置；NativeMenu 不以 Headless 結構測試冒充原生互動證據 |
| 發佈修剪 | GUI 與 CLI 不啟用 trimming；Avalonia／Dock 反射路徑保留，不以 warning suppression 隱藏相容性問題；兩個 self-contained 程式分開封裝 |
| Shader／合併閘門 | 釘選 naga-cli 驗證 include 展開後的 WGSL；Windows 與兩種 Mac 架構工作加上 `pre-merge` 彙總。儲存庫仍須將此檢查設為必要狀態 |

底片數值的來源、相機效果支援範圍與校正結果分別見 [配方來源](film-provenance.md)及[色彩校正](film-calibration.md)。配方欄位吻合、CPU／GPU 一致，均不代表與實體相機的色彩誤差已達標。

## macOS 原生相依套件與發行

- WebGPUSharp 0.5.7 的 NuGet Dawn 原生檔要求 macOS 26.0。`build/build-dawn.sh` 釘選同源 commit `4178cb7e771b700743bc489037390777b98be39d`，先前已實際重建 arm64 與 x64，兩者 `minos` 均為 **14.0**。此次快取缺檔後，從先前已驗證的封裝恢復這兩份相依檔；重新檢查簽章、架構、部署目標與全部 277 個 `_wgpu*` 匯出符號，再用於新封裝。恢復來源與量測雜湊在 `artifacts/dawn/PROVENANCE.txt`；未修改 Mach-O 載入命令。
- 封裝只整檔取代 Dawn，不修改 Mach-O 載入命令。最低系統版本取所有 Mach-O 的最高需求：重建 Dawn 後，Apple Silicon 為 14.0；Intel 因 Magick.NET 原生相依套件為 15.0。沒有重建產物時仍使用 NuGet 的 26.0 要求。
- 上述數字是量測的部署目標，不是舊系統實機通過紀錄。本次載入與算圖在 macOS 26 執行；macOS 14／15 尚無測試機。
- `build/Build.cs` 支援 Developer ID、hardened runtime、公證 keychain profile、staple 與 Gatekeeper 檢查。缺少身分或 profile 直接失敗，不會改用 ad-hoc 假裝正式發行。
- 本機已知沒有有效 Developer ID 身分，未讀取私人憑證、未提交公證。交付的 ZIP 只有 **ad-hoc 簽章**；適合本機試用，不是可保證通過網路下載 Gatekeeper 的正式發行版。

## 已執行的驗證

- Release 整合測試：Core 119 項、Avalonia Headless 43 項全數通過，無略過；包含保存／重開、匯出安全、真 16-bit、metadata、懸停、即時偏好、scheduler 狀態轉換、快捷鍵復原／重做，以及此次右鍵選取／複製／貼上／重設／快速匯出、直方圖、Sony FL／IN 色度與漏光角度回歸。既有 13 項 TIFF metadata 回歸維持通過。
- 直方圖平滑追加驗證：原生視窗以一般／Retina（1×／2×）繪製照片、高低交錯分布、純黑／純白、中間調、均勻分布及空資料；平滑曲線、黑白端點、清空後無殘影與原始統計／裁切比例不變均通過。視覺紀錄為 `dist/histogram-smoothing.png`。兩種 Mac 架構已重新封裝，ZIP 解壓後通過簽章檢查、LaunchServices 啟動、原圖比較與編輯／匯出冒煙；x64 以 Rosetta 執行。此輪只更動繪圖控制項，未重跑整套 Core／Headless 測試；下列 CLI 與 shader 證據為先前修復時的驗證。
- 真正的 CPU 與 Apple M3／Metal 算圖：16 個模組在 128×85 測試影像的最大通道差為 0–1／65535。此為受測場景結果，不是對所有照片的誤差上界。曾發現 halation 中間紋理被重用覆寫，修正後通過。
- 此次 31×97 直幅場景以真正的 CPU 與 Apple M3／Metal 分別算圖；程序化與外部紋理漏光在 0、90、180、181、200、225、270、315、359、360° 均可見，最大通道差 0–1／65535。Sony FL／IN 彩色測試圖的 CPU／Metal 最大差亦為 1／65535；此為受測場景證據，不是相機色彩準確度保證。
- 32 個內嵌 WGSL 檔案經正式 include 展開流程後，全部通過 naga 驗證。
- 最終 arm64 與 x64 封裝均通過 GUI 冒煙；x64 在 Apple Silicon 上以 **Rosetta** 執行。視窗、原生函式庫、三張範例照片、預覽、縮圖、編輯／復原、重開保留、直方圖／原圖比較、右鍵貼上／重設／復原、懸停、即時代理圖、快速 PNG16 匯出、ICC、來源雜湊、匯出紀錄、Dock 版面與元件展示均通過；結果與內容擷取在 `dist/smoke-osx-{arm64,x64}.{txt,png}`，選單擷取為 `.menu.png`。
- 既有 metadata 匯出驗證：PNG 來源 → TIFF16／PNG16／JPEG，以及 TIFF → PNG 再匯出；拍攝時間、曝光分數、GPS 與 sRGB ICC 保留。
- 上次中繼資料修正後，兩種 Mac 架構的封裝 CLI 各以 9 種來源情境匯出 JPEG／PNG16／TIFF16：有效來源、R03 色域宣告、失效 Interop 位址、Photoshop 圖層、欄位位址越界、不支援的型別、計數溢位、大型 MakerNote、大型描述。停用外部 .NET 查找後全部通過；檢查 R98／0100、ICC、方向與尺寸、拍攝時間、來源未修改、逐張警告，以及 PNG／TIFF 像素與 TIFF ZIP 壓縮。
- 最終兩份 Mac ZIP 解壓至含空白路徑後，GUI 與 CLI 均通過 `codesign --verify --strict`（GUI 另加 `--deep`），兩份 `.app` 均以 LaunchServices 啟動並完成冒煙。arm64 CPU GUI 與兩種架構的 CLI 也在 `DOTNET_ROOT` 指向不存在的位置、PATH 不含 SDK 時成功執行。
- 此次最終解壓版 CLI 驗證 Sony FL／IN、直幅 181°／270° 漏光四種已保存設定，分別走 arm64 Metal、x64／Rosetta Metal、arm64 CPU，共 12 次 PNG16 匯出。全部在停用外部 .NET 查找後通過；輸出 31×97、16-bit，與保存設定的 CPU 參照差 0–1／65535，來源雜湊不變。GUI 的解壓版與 CPU 紀錄在 `dist/smoke-osx-{arm64,x64}-extracted.txt`、`dist/smoke-osx-arm64-cpu.txt`。
- 較早另已驗證保存的專案與 YAML 自訂設定，分別走 arm64 CPU 備援與 x64／Rosetta Metal，完成 PNG16、ZIP TIFF16、JPEG 匯出。PNG／TIFF 與預期保存設定的 CPU 像素差分別為 0 與最多 1／65535；尺寸、方向、ICC、拍攝時間、GPS、來源雜湊及無效格式退出碼均通過。此次未重跑完整中繼資料 CLI 情境；相關永久回歸仍在上述整合測試內。
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
