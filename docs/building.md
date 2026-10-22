# 建置、測試與發佈

依任務找文件見[文件地圖](README.md)；完整的執行順序、失敗分類與證據邊界見[驗證入口](../VERIFY.md)。

## 需求

- Windows 10 / 11 x64；macOS 封裝的最低版本依實際原生相依檔計算。重建 Dawn 後，Apple Silicon 的相依下限為 14.0（不再提供 Intel Mac 建置）；**目前執行驗證僅涵蓋 macOS 26，較舊系統仍需實機驗證**
- 從原始碼建置需要 .NET 10 SDK（版本鎖定於 `global.json`）；發佈版已內附執行階段，不需另外安裝 .NET
- Windows 使用 DX12 / Vulkan，macOS 使用 Metal；沒有可用 GPU 時自動改用 CPU 參考算圖器
- macOS 封裝需在 Mac 執行，使用系統的 `sips`、`iconutil`、`codesign`、`ditto`
- 桌面 GUI 不啟用 trimming：Avalonia／Dock 的反射路徑需要完整組件；iOS 的裁剪設定與 Avalonia registrar 例外另見下節，不以壓掉警告宣稱相容

## 建置與執行

```bash
dotnet build FilmBatch.sln
dotnet run --project src/FilmBatch.App
dotnet run --project src/FilmBatch.App -- --gallery   # 元件展示視窗
```

## iOS 行動版

實際編輯器位於 `src/FilmBatch.Mobile.iOS`，不是 `tools/apple-mobile` 的診斷 app。`FilmBatch.App.Shared` 保留唯一的 ViewModel、編輯控制項、非視窗 View、語系與主題；桌面與行動版共用 Core／RAW／Engine，不另外實作參數、底片 LUT、復原或匯出管線。`FilmBatch.Mobile.Desktop` 可在 Mac 顯示相同的行動版介面，但不是 iOS 驗證。

### 建置與啟動

```bash
# 桌面 solution 包含共用介面及行動版桌面 host，不需要 iOS workload。
dotnet build FilmBatch.sln -c Release
dotnet run --project src/FilmBatch.Mobile.Desktop -c Release -- --workspace /tmp/filmbatch-mobile-workspace

# 必須使用與 Xcode 相容、已安裝 iOS workload 的 .NET SDK。
dotnet build src/FilmBatch.Mobile.iOS -c Release -r iossimulator-arm64
xcrun simctl install <udid> src/FilmBatch.Mobile.iOS/bin/Release/net10.0-ios/iossimulator-arm64/FilmBatch.Mobile.iOS.app
xcrun simctl launch <udid> org.filmbatch.editor
```

iOS 專案刻意不加入桌面 solution／CI。Engine 依 iOS RID 在編譯時選用 ImageIO/CoreGraphics，排除不可用的 Magick.NET 原生閉包；桌面仍走原本後端。相依專案必須保留 RID 及 `PublishFolderType`，否則可能把 Mac 原生庫選進 app，或遺失套件授權檔。**不要同時對同一份工作目錄執行桌面與 iOS restore/build**：條件式 NuGet 相依共用各專案的 `obj/project.assets.json`，交錯 restore 會污染另一個目標的編譯參考；請序列執行或使用隔離工作目錄。

目前 SkiaSharp `3.119.4` 的 .NET 10 iOS 組件以 `net10.0-ios26.2` 為目標；iOS workload 的目標平台版本也必須至少為 `26.2`。使用 `26.0` SDK 時，NuGet 會改選一般 `net10.0` 組件，缺少 iOS Skia 原生資產，即使建置成功仍會在 `SKImageInfo` 初始化時失敗。應修復建置工具鏈並檢查實際解析的組件與原生 framework，不以額外套件、CPU 繪圖替代品或壓掉例外掩蓋問題。

本機的 Xcode 27.1／iOS 27.1 實驗使用隔離的 .NET iOS SDK `27.0.10722`；該 SDK 官方要求 Xcode 27.0。只有明確選擇此實驗組合時，才對單次命令加 `-p:ValidateXcodeVersion=false`。這不是正式相容性宣告，不修改 `global.json`、SDK 內部檔案或全機 Xcode selection。Xcode 27 的可見裝置介面為 Device Hub；啟動 CoreSimulator 並不等於已開啟裝置視窗。完整先前工具鏈與 prototype 證據見 [Simulator 記錄](superpowers/plans/2026-10-19-ios-simulator-prototype.md)。

2026-10-22 的 JPEG 偏紅調查已在目前來源的主要編輯器補跑原生預覽、編輯／復原及實際匯出；[輸入、獨立色彩參考與未重現範圍](#ios-jpeg-色彩核對2026-10-22)另列，不沿用較早 binary 的結果。

### 真機 IPA 與重簽側載

必須使用完整的 iOS workload，包含對應主機的 `ios-arm64` cross-AOT 編譯器及 Mono runtime。以下命令在 repository 根目錄執行，使用獨立產物目錄，避免改寫桌面專案的 NuGet restore 結果：

```bash
dotnet publish src/FilmBatch.Mobile.iOS/FilmBatch.Mobile.iOS.csproj \
  -c Release -r ios-arm64 \
  --artifacts-path "$PWD/dist/ios-device-build" \
  -o "$PWD/dist/ios-arm64" \
  -p:EnableCodeSigning=false \
  -p:CodesignRequireProvisioningProfile=false \
  -p:BuildIpa=true \
  -p:IpaPackagePath="$PWD/dist/FilmBatch-ios-arm64-unsigned.ipa"
```

輸出為 `dist/FilmBatch-ios-arm64-unsigned.ipa`。本輪產物為版本 `0.3`、build `1`，目標是 iPhone／iPad 的 `iPhoneOS`／`arm64`，最低 iOS／iPadOS `18.0`，不是 Simulator app。Release publish 已完成 Mono AOT／native link；封裝檢查確認 ZIP／CRC 正常、176 個 payload 檔案與建置輸出一致，主程式及兩個原生 framework 均為真機 ABI，沒有 Mac／Simulator 原生相依。

**這是未簽章 IPA，不能直接安裝。** 請交由 Sideloadly／AltStore 重簽，或使用適當的 development profile 後再安裝；本輪未使用 Apple 開發者簽章身分或 provisioning profile，也未操作重簽工具。IPA 的 SHA-256 保留在同目錄的 `.ipa.sha256` 檔案，封裝檢查記錄位於忽略的 `dist/ios-device-evidence/`。實機安裝與功能、記憶體及穩定性測試尚未執行，依使用者決定留待後續進行；Simulator 的既有通過結果不代替真機驗證。

### 操作與資料所有權

- **Files／Photos**：選取多張一般影像或 RAW，串流／provider 暫存副本複製至 app 自有的新專案；不原地編輯外部原始檔。Photos 使用 PHPicker 的使用者選取結果，不要求整個照片圖庫權限。HEIC／HEIF 由 Apple 後端讀取。
- **Projects**：重開 app 內的專案與已存編輯。工具列提供 Undo／Redo、複製貼上與重設；More 提供選擇性貼上、建立／管理預設、偏好設定與停止背景載入。
- **資料夾用途**：開啟照片資料夾使用獨立的專案匯入流程；Lensfun 等目錄參數複製至 app 自有的 `Assets`，不在 Projects 中建立空白相片專案。
- **編輯面板**：使用共用的照片基本參數、RAW 控制、底片效果、白平衡／膚色／Zone 工具、裁切與原圖比較。窄版將預覽與面板上下排列；寬版左右排列。行動版的照片資訊與參數一起捲動，避免低高度視窗把參數區壓成零高度；桌面仍固定照片資訊。模組標題使用標準切換按鈕，觸控與輔助工具共用展開狀態。預覽支援觸控平移與雙指縮放；縮圖以檔名提供無障礙名稱，不依賴滑鼠 tooltip。
- **匯出**：共用批次工作建立 JPEG、PNG16、TIFF16 或算圖後的線性 DNG，保留逐張錯誤／取消結果。完成檔先存於 app 的 `Documents/FilmBatch/Exports`，可由 Files 存取，或用「另存輸出至 Files」複製至新批次資料夾；保留各專案的相對子目錄，同名檔不互相覆寫。DNG 不是未經處理的感光元件原檔。
- **生命週期**：背景時取消 I/O、分析、待處理預覽／縮圖及匯出，保存有效編輯；回前景不清掉專案、選取或復原。同步解碼只能在安全檢查點停止，不承諾背景持續完成工作。
- **預設與背景恢復**：桌面保留檔案即時監看；iOS 不呼叫不受支援的 `FileSystemWatcher`，app 內修改即時更新，外部修改於回前景時重新載入。背景取消的同步詢問不視為「保留舊預設」，環境取消的偏好設定／管理預設視窗不會自動套用未確認的修改。
- **CPU 目標**：iOS 目前明確採用 CPU 攝影算圖；70% 為合作式背景軟目標，不是硬限制。Darwin process clock 提供實際程序 CPU 時間，避免呼叫 iOS 不支援的 `Process.TotalProcessorTime`。

### 驗證邊界

macOS 的相同行動 View、Apple codec 回歸及桌面 solution 測試，均不代替實際 iOS app 的 Files／Photos、編輯、輸出與背景／前景操作。iOS Dawn GPU、JPEG XL 原生解碼閉包、實機簽章／部署、iPad 效能／記憶體上限、HDR／EDR 及完整 AI 降噪的裝置驗收不在既有行動版證據涵蓋範圍；合併 AI 分支不會自動擴大這些主張。

`AppleImageCodecTests`、`AppleImagingTests`、`TiffMetadataTests` 涵蓋 Apple 後端與中繼資料回歸；Apple 原生 cases 只在 macOS 執行。檢查 PNG 實際內嵌的 ICC 時，Magick.NET 的讀取設定須使用 `PngReadDefines.PreserveiCCP=true`：預設解碼器會把辨識出的 sRGB ICC 最佳化掉，`GetColorProfile()` 回傳空值不等於檔案沒有 `iCCP`。仍須以另一個解碼器核對 profile 內容，不修改產品去迎合讀取器的預設行為。

### 本輪實測（2026-10-20）

實際執行 `org.filmbatch.editor` 的 Release app，不以診斷 app 或 Mac 介面替代 iOS 驗收。原生案例分開執行，使用同一份最終來源與同一個 Simulator 執行檔；先前合併執行的啟動／選取器失敗保留為失敗記錄，不計入通過結果。

| 驗證面向 | 已觀察結果 | 範圍 |
|---|---|---|
| 桌面 solution 回歸 | 1,327 passed／0 failed／320 skipped | RAW 618、Core 522、App 187；略過項目包含外部 RAW corpus／oracle 及平台條件，不代表已驗證 |
| Mac 的共用行動編輯介面 | 37 checks 通過 | 寬窄版、參數展開、實際控制項編輯／復原、預設、裁切、重開、Canon CR2、匯出與來源不變 |
| Mac 桌面主 App 解鎖補跑 | 25 checks 通過 | 實際主視窗、Apple M3／Metal shader 編譯與讀回、編輯／復原、重開、真正 16-bit 匯出／ICC／來源不變、macOS 選單、Dock 與元件展示 |
| iOS Simulator Release | 4 個案例分別通過 | Files 多選匯入／編輯／復原／貼上／真正軟體鍵盤建立預設／裁切／儲存重開／四格式匯出；Photos 選取匯入；Files 輸出副本；實際 48MP 工作的手動及背景取消 |
| 原生匯出獨立檢查 | 30 checks 通過 | Pillow／imagecodecs／tifffile 核對來源雜湊、所選照片、裁切尺寸、JPEG／PNG16／TIFF16／線性 DNG、16-bit 低位元、ICC、EXIF 與方向 |
| 原生 Files provider 副本 | 11 個檔案逐一雜湊相同 | 保留專案子目錄、既有批次及終態訊息；取消工作無殘留輸出，來源檔雜湊不變 |
| iOS Release 建置與簽章 | 0 warnings／0 errors；Simulator ad-hoc 驗證通過 | 包含 AOT／link；不是實機簽章、App Store 發佈或官方工具鏈相容性承諾 |

本機證據保留於忽略的 `dist/ios-main-evidence/`：`source-manifest-release-final.json` 綁定來源；`native-release-isolated-main.xcresult`、`native-release-photos-button-confirm.xcresult`、`native-release-files-copies-final.xcresult` 與 `native-release-final-cancellation-inspection.json` 記錄原生操作；`native-release-output-inspection.json`、`native-release-provider-copies-inspection.json` 與 `mobile-view-source-final-runtime/result.json` 記錄獨立輸出及 Mac 操作。一次性 XCTest driver 不屬於產品或 CI；重跑須先在自有測試資料中準備檔案與 Photos 選取項目，不得操作使用者原始照片。

Apple 系統輸出的 sRGB ICC 可被獨立 LittleCMS 解析並用於色彩轉換，但 libpng 仍會對此 stock profile 顯示 `known incorrect sRGB profile` 警告；本輪保留此警告，沒有抑制它或新增舊式 `zTXt` ICC 副本。原生輸出保留標準 ICC 與單一 canonical EXIF；這個已觀察的相容性警告不是全平台色彩等價承諾。

桌面主 App 的 `--smoke` 先前曾因主機鎖定（`CGSSessionScreenIsLocked=1`、`activeDisplays=0`）而在 Avalonia.Native 啟動 RenderTimer 時回傳 `-6661`；該次仍保留為環境 `blocked` 的歷史記錄。使用者解鎖後，已用實際桌面主 App 補跑並取得 `SMOKE PASS`，解除這項未完成驗證；沒有修改產品、抑制例外、替換計時器或改用 Headless。補跑證據為 `dist/ios-main-evidence/desktop-source-final-unlocked-smoke.txt` 與同名 `.json`。這項結果不擴張 iOS 實機、GPU、工具鏈或色彩相容性的支援承諾。

### AI／iOS 整合驗證（2026-10-20）

將 iOS／CI 分支整合至 AI 降噪分支後，重新執行下列檢查；不沿用上節較早的 iOS Release 主流程結果作為本次合併的通過數量。

| 檢查 | 本次結果 | 證據範圍 |
|---|---|---|
| 桌面 solution 回歸 | 1,374 passed／0 failed／320 skipped | RAW 626、Core 548、App 200；外部 corpus／oracle 及平台略過仍未驗證 |
| 桌面主 App | 25 checks 通過 | 實際 Metal 算圖、編輯／復原／重開、16-bit 匯出、ICC、macOS 選單與 Dock |
| 桌面降噪介面 | 25 checks 通過 | 兩個來源域、實際模型、混合滑桿、取消／錯誤與匯出；不冒稱 iOS 介面驗收 |
| 實拍 CPU 降噪 | 兩個固定模型通過 | NIND paired crop；保留 HDR／alpha、取消後重用與全解析度先行；線性測試圖不是感光元件 RAW |
| CLI 離線工作流程 | 兩個模型匯入及兩張 16-bit PNG 匯出通過 | RGB／線性 DNG fixture 均使用 CoreML＋CPU，獨立讀取 ICC／位元深度並核對來源雜湊；未自動下載權重 |
| iOS Debug 原生建置 | 0 warnings／0 errors；簽章與原生閉包通過 | 隔離 SDK `27.0.10722`、Xcode／Simulator `27.1`；Skia framework、ONNX 連結與授權檔齊全，未帶入 Windows DLL |
| iOS 原生選取器冒煙 | 2 passed／0 failed／0 skipped | 實際編輯器顯示、Files／Photos 選取器與取消後繼續操作；不把選取器開啟當成照片匯入或 AI 推論成功 |
| CI admission | 11 tests 通過 | 既有 webhook admission／continuation 保留，worker jobs／commands／required gate 不變；本輪未 push 或觸發雲端工作 |

本次來源與執行記錄保留於忽略的 `dist/ios-into-denoising-evidence/ledger.json`，包括曾失敗的 `26.0` SDK／Skia 資產選擇與修正後結果。原生冒煙的早期 AX snapshot／Photos 載入時序失敗也保留；最終改用單一元素查詢、逐螢幕擷取與實際 Photos／Collections 就緒條件後通過，沒有為測試修改產品。這不是新版真機 IPA，也沒有重做 iOS 完整 AI 工作流程、實機或 Windows DirectML 推論驗收。




## 測試

```bash
dotnet test FilmBatch.sln -c Release
```

UI 測試以 Avalonia.Headless 執行，不需要顯示器；只有沒有可用 WebGPU 介面卡時才略過 GPU 測試。軟體介面卡仍會執行測試，實際算圖、讀回或像素比對失敗不能當成「沒有 GPU」略過。

### Windows CI 測試排程

兩套 CI 的 Windows 工作都以同一個命令限制專案與測試集合併發：

```bash
dotnet test FilmBatch.sln -c Release --no-build -m:1 --logger "console;verbosity=detailed" --logger trx --results-directory dist/test-results/win-x64 -- xUnit.ParallelizeTestCollections=false
```

`-m:1` 限制 MSBuild 同時執行的測試專案；`xUnit.ParallelizeTestCollections=false` 關閉同一測試組件內的集合併發。[RunSettings 的大小寫有意義](https://xunit.net/docs/config-runsettings#switches)，不能寫成 `xunit.parallelizeTestCollections`。macOS 保留原本的平行測試命令；產品的渲染排程、所有 GPU 測試與像素容差都不變。

Windows log 曾顯示四個 GPU 測試類別交錯執行，接連出現十次裝置遺失及一次建立介面卡失敗。序列化移除這層測試資源爭用，不代表已證實 TDR／OOM，也不能代替 Windows／D3D12 回歸；裝置遺失仍會使測試失敗。外部 RAW 樣本、LightCraft oracle 或 `rawinfo` 未提供而產生的略過，與 GPU 失敗分開判讀。

### 編輯生命週期回歸

```bash
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~EditorLifecycleTests|FullyQualifiedName~WindowLifecycleTests|FullyQualifiedName~PreviewInteractionTests"
```

涵蓋裁切取消／延後進入、跨專案與同資料夾重開、部分匯出失敗後重試、拖曳中斷與復原區間、分割線優先權（含拉直旋轉後的命中與拖曳）、面板／全螢幕狀態及共用平移。資料夾切換測試使用隔離的照片副本，並檢查新專案可繼續編輯與儲存。

Headless 不會執行 AppKit 的 Spaces 動畫。Mac 桌面驗證須觀察實際 Cocoa 全螢幕狀態並等待動畫結束後再操作下一步：[Avalonia 的原生實作](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/native/Avalonia.Native/src/OSX/WindowImpl.mm#L458-L546)也會同步回報要求狀態，因此 `WindowStateChanged` 不能當成動畫完成通知。另確認 Cancel 後畫布無裁切工具、解碼前 Esc 不會稍後重新啟用、文字欄位內 Esc 能關閉預設管理，以及切換專案前後原始照片位元組不變。實體綠色按鈕、動畫期間快速連按切換與 Windows／Linux 原生視窗操作不屬於 headless 的驗證證據。

### RAW 介面回歸

```bash
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~RawInspectorTests|FullyQualifiedName~Thumbnails_WaitForCurrentRawSource|FullyQualifiedName~RawControlsTests|FullyQualifiedName~RawSourceLifecycleTests"
```

涵蓋 RAW／一般影像切換、隱藏未啟用模組、保留已保存的 RAW 設定、預設展開基本調整、來源選項切換後縮圖自動更新，以及 Kelvin／遮罩設定的復原、儲存、重開、過期解碼失敗與修復後快取重新解碼。永久測試使用隔離的 DNG／一般影像與實際 CPU 管線，不依賴外部相機語料。自訂 Kelvin 範圍為 2500–9900 K；舊 JSON／YAML 值正規化，但拍攝白平衡中繼資料保持原樣。

Mac 桌面另以含 gain-table 的 ProRAW 及含畸變／暗角資料的 RAF 驗證：在底片分頁復原／重做 RAW 選項，即使重解碼時間超過縮圖防抖間隔，底片與色相縮圖仍自動跟上新來源，結果須等於同來源重新要求算圖的結果；切到 PNG 不顯示 RAW 控制項，再切回 RAW 恢復顯示且設定不變。驗證前後核對原始 RAW 的 SHA-256。

### 顆粒、白平衡／膚色與 Zone

```bash
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~GrainModelTests|FullyQualifiedName~SurveyRenderingTests|FullyQualifiedName~PhotoAnalysisTests|FullyQualifiedName~PhotoAdjustmentTests"
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~PhotoToolsTests"
```

涵蓋原始參考座標、非等比例取整代理圖的旋轉、次像素顆粒衰減、局部膚色／Zone、遮罩後的白平衡正向試算，以及提案、取消、復原與儲存邊界。GPU 數值比對仍須實際 GPU；沒有 GPU 的略過不能當成該路徑通過。

桌面另須展開「照片工具」，以點選／拖曳操作中性區、空間候選與參考色，確認預覽不寫入，接受後一次復原還原全部欄位；再檢查膚色參考重複套用、最近一次 Zone 的 III／V／VIII 放置、雙錨點重複擬合不漂移。旋轉取樣的實際來源外接矩形須與畫布投影相符。顆粒三種模型、seed 與進階品質／明暗響應需在預覽和重開後均可見；操作與限制見[現行功能](grain-white-balance-survey.md#目前實作與操作)。

真實 OS 失焦／重新啟用需要未鎖定的桌面。鎖定畫面下，Avalonia 原生視窗與控制項仍可供受控驗收，但不能據此聲稱 OS 焦點切換、實體滑鼠或鍵盤已通過；這些界線須另記，不能靠送出合成失焦事件假裝完成。

未鎖定桌面的補驗已通過：實際 Apple M3／Metal 原生視窗共 149 項檢查通過、0 失敗、0 受阻，交付 120 個預覽影格。原生視窗啟用、另一視窗取得焦點後取消取樣提案，以及主視窗重新啟用均實際執行；失焦不寫入設定或產生復原紀錄，三個來源 DNG 的 SHA-256 不變。控制項仍由 automation peer 與畫布路由事件操作，不將此結果宣稱為實體滑鼠／鍵盤驗證。


### GPU 讀回與 RAW 數值回歸

```bash
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~RawGpuDisplayTests|FullyQualifiedName~RawSceneIntegrationTests|FullyQualifiedName~RawToneTests|FullyQualifiedName~RawCameraLookTests|FullyQualifiedName~GpuSpikeTests|FullyQualifiedName~GpuErrorScopeTests|FullyQualifiedName~GpuResourceLifetimeTests|FullyQualifiedName~EffectPipelineTests"
```

涵蓋預覽／16-bit 匯出的共用 GPU 管線、分帶讀回、裁切後工作區重建及 RAW 飽和高光。兩個 `GpuDevice.ReadBuffer` 多載都使用 10 秒的奈秒預算；WebGPUSharp `0.5.7` 的 `MapSync` 雖將參數命名為 `timeoutMilliseconds`，卻[直接轉交](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/Buffer.cs#L75-L104)給 [以奈秒計的 `WaitAny`](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/Instance.cs#L155-L175)，不能傳入看似代表 10 秒的 `10_000`。真正的裝置錯誤仍會傳播，不因延長等待而改成成功。

Windows 的 `Auto` 明確要求 D3D12，不再把 `BackendType.Undefined` 交給 Dawn 決定。實際失敗 log 曾顯示 `Microsoft Basic Render Driver`／D3D11／`Backend Unknown`，因此不會套用 D3D12 專用生命週期策略。`GpuSpikeTests.WindowsAuto_CreatesDx12Device` 驗證自動建立的裝置確為 D3D12；明確選擇 Vulkan 等後端及非 Windows 自動選擇保持不變。

Windows／D3D12 的裝置生命週期另有回歸檢查：`GpuSpikeTests.WindowsDx12_DisposeReleasesDeviceOwner`。WebGPUSharp `0.5.7` 會[以強 `GCHandle` 保留 callback](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/Adapter.cs#L105-L128)，直接捕捉 `GpuDevice` 會讓原生裝置無法回收。因此僅在 D3D12 使用弱參考 callback，並於 `Dispose()` 等待佇列後明確銷毀原生裝置；不變更渲染數學或 GPU 錯誤傳播。Metal／Vulkan 保留原生命週期，包含同樣的強 callback 保留風險；這是本次僅限 Windows 修正的已知未修範圍。

上述永久測試僅在 Windows 且可列出 D3D12 介面卡時執行，其他環境會明確略過；列出介面卡之後的裝置建立、處置或回收錯誤不會改成略過。本機另以真實 Metal 裝置注入 D3D12 生命週期策略，確認處置後原生操作回報裝置遺失、callback 不再保留裝置；這只能證明策略的原生機制，不能代替 Windows／D3D12 驅動程式驗證。

`Destroy()` 不等於釋放 wrapper 持有的原生參照。WebGPUSharp `0.5.7` 的 [managed wrapper](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/internal/WebGPUManagedHandleBase.cs) 只有私有 `_safeHandle`，未公開 `Dispose()`；原本即使銷毀 buffer／texture，view、bind group、shader 與 pipeline 仍須等 GC。D3D12 路徑現在透過 `NativeLifetime` 取得原有 [safe handle](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/WebGpuSafeHandle.cs) 並呼叫其冪等 `Dispose()`，同時取消該 handle 的 finalizer，避免手動原生 `Release` 後再次釋放。正常與建立失敗路徑都回收擁有的參照；已錄製的指令由 Dawn 自行持有所需參照。裝置及 D3D12 介面卡查詢的 wrapper 參照也會配對釋放。此 bridge 綁定私有欄位，升級 WebGPUSharp 時必須重驗，不能直接搬用到其他版本。

`GpuResourceLifetimeTests` 在 Windows／D3D12 驗證連續 12 次 compute／讀回後的原生參照數、重複處置，以及遺失 callback 尚未設定時的讀回錯誤。資源驗收期間不強制 GC；僅在量測基準前清除其他測試留下的不可達 wrapper。兩個新回歸另在真實 Metal 上注入 D3D12 生命週期策略，舊碼皆失敗、修正後皆通過；正常 Metal 與注入策略的 RAW 裁切／還原／16-bit TIFF 匯出均保持 GPU 處理，與 CPU 最大差異為 `1/65535`。這些是原生機制證據，不是 Windows 驅動程式通過證明。

RAW 收尾的 sRGB 冪次先前已改採固定多項式，降低對後端 `pow` 精度的依賴。本次 Windows 診斷顯示，亮度比 `y/Grey` 同為 `0x409E7365`，第一次偏離卻發生於 CPU `MathF.Log2`：EV 為 `0x4013B47C`，GPU 為 `0x4013B47B`。對精確輸入 `4.951586246490478515625` 的高精度對數驗算確認下鄰才是正確捨入；只替換 EV 的獨立 float32 重播，也將紅色色碼從 1324 還原為 1319。

因此僅在 Windows 對已完成 float32 除法的比值使用 `(float)Math.Log2(ratio)`；其他平台保留 `MathF.Log2`。正式色調映射與診斷共用 `ToneMap.LogExposure`，既有 `2/65535` 容差及 shader 不變。`RawToneTests.RawDisplay_HighlightNearGamutBoundaryPreservesRedDetail` 從 CPU 顯示收尾驗證紅色細節，不要求 GPU。來源連結的強制 Windows 分支冒煙測試得到 `[1319,65535,64083,65535]`；真實 Metal 的四個邊界案例亦與 CPU 完全相同。強制分支不等於 Windows CRT／D3D12 實機執行，六個裝置遺失與完整 Windows 回歸仍須由對應 runner 確認；若 D3D12 專用測試略過，不算該路徑已驗證。

修正前的 Windows 紀錄包含 64×48 RAW 全解析度匯出，以及 `GpuSpikeTests.Gpu_MatchesCpuReference` 的 320×200 零參數與負參數失敗。RAW 讀回錯誤原遭集合格式化器截短；spike 則回報 `Microsoft Basic Render Driver · DX12` 的原生 `[Device] is lost.`，之後多個介面卡請求無法成功。其他執行順序也曾在第一次 32×48 裁切或 32×24 單帶讀回失敗；這不支持「大圖超過緩衝區上限」、固定測試必死，或單憑三次失敗判定 TDR／OOM／行程黑名單。後續原生回呼與 shader 診斷、正式修正結果如下。

WebGPUSharp 會將 [遺失與 uncaptured-error callback 排入 ThreadPool](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/Adapter.cs#L388-L468)，所以 `MapSync` 失敗時不保證 callback 已送達。D3D12 讀回現在即使 `IsLost` 尚未設定，也會保留原始綁定例外、介面卡與已收到的最近原生錯誤；未知原因明確標為尚未回報，不假造移除原因。晚到的遺失 callback 另寫入既有 logger，未設定 logger 時寫入標準錯誤。RAW 匯出測試會直接以檔名及完整 `summary.Errors` 字串宣告失敗，不再讓 `Assert.Empty` 的集合格式化器截短原生訊息。若仍失敗，請同時保留讀回例外與稍後的 callback 訊息；本機驗證不能替代 Windows／D3D12 結果。

WebGPUSharp `0.5.7` 的 [`DeviceDescriptor`](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/DeviceDescriptor.cs#L17-L20) 將兩種裝置 callback 宣告成兩參數 delegate，但 [`Adapter` 的 native trampoline](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/Adapter.cs#L388-L468) 卻轉型成含 `DeviceHandle` 的三參數 delegate，轉型失敗又被吞掉。因此先前的「回呼尚未收到」不只是排程競態：正常註冊根本無法收到這兩種回呼。D3D12 現在以既有 `NativeLifetime` 邊界保留正確三參數 delegate，再以 `Unsafe.As` 存入 descriptor 的兩參數欄位；這個檢視只能交給綁定，不能在 managed code 直接呼叫，升級綁定時必須重驗。弱參考 owner 保持不變；沒有新增等待、重試或變更渲染數學。原生冒煙測試在真實 Metal 上先重現兩種回呼遺失，再驗證修正後收到 `Validation` 與 `Destroyed`；另以 D3D12 策略驗證原生驗證錯誤會到達呼叫端且處置後 owner 可回收。`WindowsDx12_UncapturedValidationErrorReachesCaller` 保留實際 Windows 原生錯誤的回歸檢查。這修復診斷與錯誤傳播，不代表造成 D3D12 移除的渲染問題已排除；Metal／Vulkan 的既有 callback 路徑未變。

後續實際 Windows 診斷已縮小渲染問題：[`job 34`](https://circleci.com/gh/minexo79/FilmBatch/34) 的 float／uint 紋理上傳與儲存緩衝區讀回通過，但原始 sRGB 解碼在保持上傳紋理存活時仍遺失裝置；[`job 35`](https://circleci.com/gh/minexo79/FilmBatch/35) 證明移除色彩轉換即通過，合併提交或增加診斷用等待卻無效。相同 GPU 工作改用等價向量 `select` 後，[`job 40`](https://circleci.com/gh/minexo79/FilmBatch/40) 在 Microsoft Basic Render Driver／D3D12 讀回 12,288 個分量，最大 float 差異 `2.3841858e-7`。正式修正只將 `common/color.wgsl` 的雙向 sRGB 轉換改成向量形式，保留分段門檻、係數、GPU 執行與既有像素容差；未選取的冪次分支以有效底數計算，不改變低段或負值的選取結果。沒有加入渲染等待、重試、CPU 解碼替代或驅動程式設定。暫時的上傳診斷程式與 CI 步驟已移除，仍由原本的完整測試及發佈冒煙測試驗證正式修正；完整 Windows 結果另記於本次修正的驗證紀錄。

正式修正 `c7f6072` 已由 [`Windows job 43`](https://circleci.com/gh/minexo79/FilmBatch/43) 完整驗證：App 183、Core 511、RAW 618 項通過，0 失敗；Core 22、RAW 293 項既有略過。三個 spike 參數案例、裁切後重配、兩種完整 RAW 匯出、旋轉裁切匯出及四個高光邊界案例皆實際通過，沒有原生裝置遺失紀錄或 GPU 不可用略過。發佈的 Windows 執行檔在 D3D12/WARP 完成 GUI 冒煙測試，確認 1600×1066 真正 16-bit 匯出、sRGB ICC 與來源不變；[`macOS job 42`](https://circleci.com/gh/minexo79/FilmBatch/42) 亦通過完整流程與 25 項 Metal GUI 檢查，並產生 Apple Silicon `.app` ZIP。這些結果驗證該修正版本，不將先前 job 40 的診斷模式通過誤認為當時的正式 shader 已修正。

D3D12 介面卡請求失敗另輸出 `[GPU-ADAPTER]`，包含要求的 backend、low-power／fallback 選項及完整綁定例外；裝置初始化失敗輸出 `[GPU-CREATE]`。兩者使用既有 logger，未設定時寫入標準錯誤；請一併保留後續測試略過前的這些訊息。既有可用性判斷未改成全域快取，也未把失敗改成成功或加入重試。WebGPUSharp 的同步介面卡 API 將 `Unavailable`／`Error` 等狀態都轉成 `WebGPUException`，因此不能只看泛用的「沒有介面卡」訊息推斷原因。

兩套 Windows CI 都以 `--logger "console;verbosity=detailed" --logger trx --results-directory dist/test-results/win-x64` 執行，保留原有序列化選項。預設 console logger 不顯示上述 process-level 訊息；實際 xUnit runner 冒煙測試確認 detailed logger 可顯示測試失敗或略過前的原生原因，TRX 亦保留完整 run-level 輸出。CircleCI 於 `FilmBatch-win-x64/test-results` 保存 TRX，GitHub Actions 以 `FilmBatch-win-x64-tests` artifact 保存，即使測試失敗仍會嘗試上傳；macOS 測試命令與產物不變。

目前已先等待佇列完成再以 10 秒預算映射，沒有 `Poll` 忙等；原生映射 callback 的錯誤字串與逾時例外也不同。回呼尚未收到不代表裝置仍健康，縮小讀回列帶也不會自動將 RAW／效果 compute pass 分帶。本機已用真實 Metal 裝置重現「callback 尚未設定但原生操作已拒絕」及 xUnit 訊息截短，確認完整輸出；這是診斷修正，不是三項 Windows 裝置遺失已解決的證據。

另修正命令中止時的 error-scope 生命週期：未提交的命令清單也會 pop 自己的 validation scope，避免後續呼叫拿錯外層錯誤；kernel 建立途中失敗亦以 `finally` 配對。`GpuErrorScopeTests` 使用真正的 Dawn 驗證錯誤檢查歸屬，未以模擬 callback 當成驅動程式驗證。本機 Metal 的失敗前／修正後回歸與正常 RAW 匯出已執行；Windows／D3D12 移除原因及三項失敗是否排除，仍須對應 runner 的新結果。

Windows 的 `Gpu_SaturatedHighlightNearGamutBoundary_MatchesCpu` 若超出既有 `2/65535` 容差，原本的 assertion 訊息會附上 `[RAW-PARITY] BEGIN` 到 `[RAW-PARITY] END` 的診斷區塊。現行 CI 的 `dotnet test` 即可顯示，不需環境變數或調整 logger。內容包含原始 CPU／GPU RGBA16、輸入像素、裝置／驅動程式／.NET 版本、shader SHA-256，以及工作域轉換、tone／LUT 座標與相鄰節點、高光、gamut 投影和 sRGB 各階段的 float32 數值與十六進位位元。

診斷只處理該測試的單像素邊界案例：CPU 中間值由測試內的診斷鏡像計算（EV 重用正式方法）；GPU 中間值由同一裝置執行記憶體內插入讀回點的內嵌 production RAW shader，最後使用 production RGBA16 encoder。原始渲染結果不會被替換。`cpuTraceMatchesOriginal`／`probeMatchesOriginal` 分別確認診斷碼值是否重現原始 CPU／GPU 碼值；若為 `False`，不能直接將中間值視為原失敗路徑的證據。額外診斷若失敗會記錄 `probeError`，原始 assertion 仍維持失敗；通過及非 Windows 案例不啟動 probe。下一輪 Windows CI 請確認實際建立的後端；若仍有差異，保留完整區塊及上述重現標記。

### 大量照片載入與即時預覽回歸

```bash
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~BulkLoadingTests|FullyQualifiedName~CacheAdmissionTests|FullyQualifiedName~FilmstripLoadingTests|FullyQualifiedName~BackgroundLoadingPreferencesTests|FullyQualifiedName~PreviewDeliveryTests"
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~RawWhiteBalanceRangeTests|FullyQualifiedName~RenderSchedulerTests"
```

涵蓋掃描／解碼期間的真實狀態、共享來源等待取消、前景獨立入場、選項復原與淘汰、壞檔不阻斷後續照片、可見範圍優先、取消後的明確重算、即時 CPU 設定及同來源已完成影格的交付。錯誤路徑另驗證缺失素材重算不堵塞後續照片、預覽錯誤不污染可成功的較小縮圖、背景或舊預覽錯誤不丟掉新影格，以及算圖器初始化／重建失敗仍交付逐筆錯誤。永久測試使用隔離產生的照片與實際 CPU 管線，不以私人佇列長度作為驗收結果。

桌面另以 2,000 張混合 JPEG／DNG 驗證：載入開始立即有掃描狀態，捲到後段與選取最後一張不等待前段全數完成；載入中調整 30%／70%／100%，不重建 GPU、不遺失編輯；取消後保留張數與目前預覽；重疊開啟資料夾、同名舊錯誤、壞檔及空資料夾皆有正確終態。已完成的縮圖遇到缺失素材時，其他照片的編輯仍能送達；重設錯誤設定後可直接恢復，不必重開。RAW 自訂 5500→5600 K 應更新畫面，連續一般幅度調整仍送達影格，復原／重做及儲存重開維持結果；前後核對原始檔 SHA-256。

CPU 目標是程序相對全機邏輯核心的合作式背景目標，不是硬性上限，也不保證將輕量工作拉高至 70%。單張不可中斷解碼可短暫超標；計時、CPU 比例與延遲只屬當次機器／輸入的觀察，不當成跨機效能門檻。

### RAW 語料測試

RAW 解碼與顯影另有對照 LightCraft 參考輸出的語料測試（逐位元的感光資料、色彩標籤與顯影階段，以及起始外觀擬合後的線性／顯示 RGB）。樣本與參考輸出不進版本庫；沒有設定 `FILMBATCH_RAW_SAMPLES`／`FILMBATCH_RAW_ORACLE` 時（例如 CI）這些測試會略過。取得方式、比較門檻與已知偏差見 [tools/raw-oracle/README.md](../tools/raw-oracle/README.md)。

場景線性、白平衡、語意遮罩、鏡頭與 DNG 色彩寫出另有不需外部語料的測試：

```bash
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~Raw|FullyQualifiedName~LensProfile"
dotnet test tests/FilmBatch.Raw.Tests/FilmBatch.Raw.Tests.csproj -c Release \
  --filter "FullyQualifiedName~PortableJxl|FullyQualifiedName~JpegXl"
```

支援平台的 JPEG XL 測試要求實際隨附解碼器；庫缺失屬於部署失敗，不當作條件略過。Windows 的原生載入／GUI 行為仍須在 Windows CI 或實機驗證，Mac 上的 PE／相依符號檢查不能代替執行。

八項整合需求與 review 準則見 [RAW pipeline parity 計畫](superpowers/plans/2026-10-18-raw-pipeline-parity.md)，版本化的稽核結果與回收來源見[驗證與知識回收記錄](superpowers/plans/2026-10-18-governed-verification-and-knowledge.md)。`dist/raw-pipeline-parity-acceptance.json`、`dist/raw-parity-native-osx-arm64.txt` 與 smoke 原始記錄是可選的本機證據，`dist/` 不進版本庫，fresh clone 不應依賴它們或已刪除的暫存 driver。數值實驗不取代實際 CLI／原生函式庫驗證，條件略過的語料不計為通過。

### Shader 驗證

所有 WGSL 編譯單元經 `ShaderLibrary` 展開 include 後，以固定版本的 naga 驗證；`common/` 共用片段隨使用它的 shader 一起檢查，不單獨編譯：

```bash
cargo install --locked --version 30.0.1 naga-cli
dotnet run build/CheckShaders.cs
```

### 合併前 CI

`.github/workflows/ci.yml` 與 `.circleci/ci.yml` 共用還原相依套件、Release 建置、完整測試、隨附 JPEG XL ABI／樣本解碼、封裝及 GUI 冒煙測試命令；WGSL 均以固定的 naga-cli `30.0.1` 驗證產品的 `ShaderLibrary` 輸出。`.circleci/config.yml` 僅負責先判斷 webhook 是否符合建置條件，再接續完整 CI。

#### 平台與 SDK

| 用途 | GitHub Actions | CircleCI |
| --- | --- | --- |
| Windows x64 桌面 | `windows-2022` | `windows-server-2022-gui:current` |
| macOS Apple Silicon 桌面 | `macos-26` | Xcode `26.4.1`／macOS `26.3`、`m4pro.medium` |
| WGSL 驗證 | `ubuntu-24.04` | `ubuntu-2404:current` |

兩邊安裝 `global.json` 的 `sdk.version`；`rollForward` 僅控制執行時從已安裝 SDK 中選擇版本，不代表安裝最新 `10.0` 通道。CircleCI 的 Windows 工作使用 Git Bash 並明確啟用 `errexit`／`pipefail`。

映像對照於 2026-10-18 依 [CircleCI 支援表](https://circleci.com/docs/using-macos/)及官方 [26.4.1 manifest](https://circle-macos-docs.s3.amazonaws.com/image-manifest/v17723/manifest.txt)核對；Windows 依[官方執行環境說明](https://circleci.com/docs/using-windows/)。Xcode 版本不等於宿主 macOS 版本，例如 Xcode 26.0.1 的宿主是 macOS 15.6，不能滿足隨附 Dawn 的 macOS 26.0 最低需求。這些設定統一 OS 基線，不保證兩家託管映像的修補版本或所有預裝工具完全相同。

#### Windows 硬體 GPU 與軟體渲染

截至 2026-10-18，依 [resource class 參考](https://circleci.com/docs/reference/configuration-reference/#resourceclass)與 [GPU 執行環境指南](https://circleci.com/docs/guides/execute/using-gpu/)：

| 資源類別 | vCPU／RAM | NVIDIA GPU |
| --- | --- | --- |
| 目前的 `windows.medium` | 4／16 GB | 非專用 GPU 類別 |
| `windows.gpu.nvidia.medium` | 16／60 GB | Tesla T4、16 GiB GPU 記憶體 |

GPU 指南列為 Scale 方案，且需搭配 Windows GPU 映像；存取資格與映像應在切換前向 CircleCI 確認。不能只把現有一般 Windows 映像的 `resource_class` 改名，也不能將目前 runner 的通過結果當作 NVIDIA 硬體驗證。Windows 測試排程的調整不啟用專用 GPU 類別，也不變更遠端方案或設定。

Microsoft 的 [WARP 說明](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/directx-warp)指出 Basic Render Adapter 提供軟體渲染，Windows 10 起也支援 D3D12。診斷 log 的 `IsSoftware=False` 是綁定回傳的分類，不能據此認定為實體 GPU。可用軟體介面卡仍執行真正 WebGPU 算圖；裝置遺失或像素錯誤照常失敗，不以 CPU 算圖冒充成功。供應商變更方案、資源類別或映像時，須重新核對本節。


#### 合併必要檢查

- **GitHub Actions**：只在推送至 `master`／`pre-merge`、目標為這兩個分支的 PR（`opened`、`synchronize`、`reopened`、`ready_for_review`）及兩者的 merge queue（`merge_group.checks_requested`）時執行。沒有 `workflow_dispatch`、排程或 tag 觸發。名為 `pre-merge` 的聚合 job 與同名分支是不同概念：job 使用 `always()` 檢查 `desktop`、`shaders` 的結果，只有全部成功才通過；失敗、取消或略過均不放行。在兩個目標分支的分支保護／ruleset 中將 **`pre-merge`** 設為必要檢查。
- **CircleCI**：保留 `pre-merge` 分支新增的聚合 gate，`requires` 下列三項實際檢查全部成功後才執行。啟用 **Project Settings → Advanced Settings → VCS Status Updates**，可將 **`ci/circleci: pre-merge`** 設為必要檢查；各前置工作的狀態仍可獨立檢視：
  - `ci/circleci: desktop-win-x64`
  - `ci/circleci: desktop-osx-arm64`
  - `ci/circleci: shaders`

CircleCI 的 workflow-level GitHub Checks 與上述 job 狀態不同，不能在停用 VCS Status Updates 後仍要求不存在的 job 狀態；名稱及回報方式見[官方說明](https://circleci.com/docs/guides/integration/status-updates/)。聚合 gate 不取代測試，只回報三個前置工作已成功；上游失敗、取消或略過時，gate 不會排程，必要檢查不會取得成功狀態，可能維持等待。三個實際檢查彼此不因另一個失敗而中止，所有工作仍受同一個 `ci` workflow 的 master／pre-merge 觸發限制。

#### CircleCI 觸發設定

此儲存庫使用 GitHub，因此這裡的 Merge Request 指 **Pull Request（PR）**，不是 GitLab 的 `merge_request` 事件。保留既有 GitHub OAuth／webhook 整合，不要求改裝 GitHub App，也不新增個人 API token。

**實際故障與修正**：[pipeline 23](https://app.circleci.com/pipelines/gh/minexo79/FilmBatch/23/details) 收到了 [PR 15](https://github.com/minexo79/FilmBatch/pull/15) 的 webhook，來源是 `dev-ios-app`、目標是 `pre-merge`，revision 為 `3cb4516`；當時 CircleCI 的 compiled config 為空，沒有任何 workflow。舊條件依賴 GitHub App 專用的 `pipeline.event.github.pull_request.base.ref`，本機合成 App 事件的檢查不足以代表這個 webhook。修正後由 setup job 讀取實際 pipeline API，再向 GitHub 查詢 PR 目標，不再假設整合會注入 App 欄位。

流程與條件：

1. `.circleci/config.yml` 使用 `setup: true`，僅執行小型 `admit-ci` job；它不建置 .NET、不啟動 macOS／Windows runner，也不編譯 shader。
2. `build/ci-admission.py` 讀取本次 pipeline 的實際 trigger、branch、revision 與 tag。只有 `webhook` 可進入建置判斷；API／UI 手動、排程與 tag 均不啟動完整建置。
3. `master`／`pre-merge` 的更新，以及 `gh-readonly-queue/master/`／`gh-readonly-queue/pre-merge/` 更新，直接放行。其餘來源必須有目前仍開啟的 PR，且 head branch、head SHA、目標儲存庫全部符合本次 pipeline，base 必須是 `master` 或 `pre-merge`。過期 revision、已關閉 PR、其他目標分支與無 PR 的一般推送都不放行。
4. 透過 CircleCI 自動注入、每個 pipeline 僅可使用一次的 `CIRCLE_CONTINUATION_KEY`，接續 `.circleci/ci.yml`，並傳入預設為 false 的 `run_ci`。放行後仍執行原有 Windows、macOS、WGSL 三項工作與 `pre-merge` 聚合 gate；不放行時完整 CI workflow 不會排程。

| 事件 | 完整建置 |
| --- | --- |
| `master`／`pre-merge` 的 webhook 更新（包含 PR 合併後更新） | 執行 |
| 其他來源的 webhook，且目前有符合 branch／SHA 的 PR 指向 `master`／`pre-merge` | 執行 |
| 兩個目標分支的 merge queue webhook 更新 | 執行 |
| 一般分支推送且無符合的 PR、其他目標、已關閉或過期 PR | 不執行 |
| tag、排程、API／UI 手動觸發 | 不執行 |

**已接受的取捨**：不符合條件的事件仍可能建立 pipeline 並消耗一次小型 admission job；這是保留既有 webhook 整合、又在執行期取得 PR 目標所需的成本，不能宣稱完全沒有 job 或 pipeline 紀錄。admission 命令沿用 `build/ci-timeout.py`，預算 120 秒；初始化與 checkout 不在此命令預算內。其餘命令預算與必要 gate 不變。

**公開 API 與失敗行為**：目前儲存庫與 pipeline metadata 可公開讀取，因此查詢不帶個人 API token。API 限流、連線失敗或無效回應會讓 admission 失敗，不會假裝已成功略過。continuation key 只送到 CircleCI continuation API，不寫入檔案或 log；POST 不重試，無法確定結果時回報 `OUTCOME_UNKNOWN`。若日後改成私有儲存庫或限制 API 存取，須先配置適當存取權，不能以失敗查詢當成放行依據。

**遠端設定**：在 **Project Settings → Project Setup** 保留現有 webhook trigger，不要再新增重複的 GitHub App trigger。若選「只建置開啟 PR、預設分支及 tag」，非預設的 `pre-merge` 合併後推送未必會觸發；需要涵蓋這類更新時使用 **All pushes**，由 admission 篩選完整建置。YAML 無法自行修改 webhook 訂閱或遠端設定；若要求被拒絕事件連 pipeline 紀錄都不建立，仍須管理員調整訂閱。

**Dynamic config 前提**：須啟用 **Project Settings → Advanced → Enable dynamic config using setup workflows**；2023-12-01 之後建立的專案預設啟用，但若被關閉仍需管理員恢復。setup pipeline 六小時內只能 continuation 一次，不能重跑 setup workflow；應由下一次正常 webhook 建立新 pipeline。兩個目標分支的直接推送也會建置；若只允許 PR 合併更新，須另外設定分支保護。

官方契約見 [dynamic configuration](https://circleci.com/docs/guides/orchestrate/dynamic-config/)、[pipeline values](https://circleci.com/docs/reference/variables/) 與 [OAuth／GitHub App 整合差異](https://circleci.com/docs/guides/integration/using-the-circleci-github-app-in-an-oauth-org/)。fork PR 能否建立 pipeline 仍由既有 webhook 設定決定，不在此宣稱已驗證 fork 執行。

#### 逾時與遠端設定界線

GitHub Actions 的 `timeout-minutes` 是整個 job 上限。依本次選擇，CircleCI **不新增 API token**：`timed-run` 共用剩餘命令預算（桌面 30 分鐘、WGSL 15 分鐘），逾時回傳 124，必要時在 30 秒終止寬限後強制結束；初始化、checkout、產物上傳與 runner 等待不受此包裝器控制，CircleCI 方案的整個 job 上限亦可能較短。不能將兩者宣稱為相同的硬性 job deadline。命令透過環境變數傳入子 Bash，保留單引號、換行、`pipefail` 及原始失敗碼。優先使用現有 GNU timeout；沒有時改以 runner 的 Python 3 執行 `build/ci-timeout.py`，對命令 process group 先送 TERM、寬限後送 KILL，並回收直接子程序。啟動計時器可在 checkout 前執行，Python 腳本只在 checkout 後使用；不再為了 timeout 呼叫 Homebrew。

計時器會在初始化階段確認 GNU timeout 或 `python3` 可用；兩者皆缺失時立即報錯，不把故障延後到還原／建置命令。

CircleCI 官方的[自訂整個 job 時限方案](https://support.circleci.com/hc/en-us/articles/14114124583195-How-to-set-a-custom-maximum-job-duration)需要個人 API token 呼叫取消 API，本設定刻意不採用。YAML 不會修改 trigger、VCS 狀態回報、分支保護或 ruleset；這些遠端設定仍須管理員套用，並以實際 PR／merge queue 的回報確認，不能把本機設定驗證當成已啟用的雲端合併保護。

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

App 與 CLI 各自隨附固定 ABI 的 libjxl 0.11.2 原生閉包與授權，不使用 Homebrew／PATH 搜尋作為缺庫退路。`src/FilmBatch.Raw/Native/Jxl/provenance.json` 記錄封包來源、雜湊與相依關係；`build/repack-jxl.py` 依該紀錄重建封裝，Mac install names 在簽章前改成 `@loader_path`。不得只複製 `libjxl` 而遺漏其相依庫。

跨 RID 發佈時，App → Engine → Raw 的專案參考必須保留目標 `RuntimeIdentifier`。Engine 與 Raw 的 `IsRidAgnostic=false` 防止 SDK 將 RID 移除後，Raw 誤依 Mac 主機選入 Windows 封裝的 dylib。GUI 與 CLI 的發佈根目錄各自都要通過 manifest 閉包檢查；只檢查 Raw 的建置資料夾不足以證明封裝正確。

NuGet 內附的 Dawn 要求 macOS 26.0。若要使用較低部署目標，先重建相同 commit 的原生函式庫：

```bash
dotnet restore FilmBatch.sln
bash build/build-dawn.sh arm64
dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
```

需 Xcode Command Line Tools（含 Metal SDK）、CMake、Python 3。腳本產生 `artifacts/dawn/osx-arm64/webgpu_dawn.dylib` 與量測來源記錄 `PROVENANCE.txt`，核對架構、部署目標及 WebGPU 匯出符號。封裝器只整檔取代，不修改 Mach-O 載入命令；沒有重建檔時，仍使用最低版本 26.0 的 NuGet 版本。

`artifacts/dawn/` 同樣不進版本庫。CI 不再提供手動 `dawn-build` 入口；需要重建時，使用上方本機命令。`desktop` 工作不會自動下載重建檔；fresh clone 沒有該產物時，不能以 `Info.plist` 的宣告代替原生相依庫的實際部署下限。

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
filmbatch-cli gpu-info
filmbatch-cli raw convert "/path/input.CR2" "/path/output.dng" --compression lj92
filmbatch-cli raw masks "/path/input.dng"
```

開發時可直接 `dotnet run --project src/FilmBatch.Cli -- <指令>`。

- 匯出使用已保存的專案設定，CLI 不會自動更新預設快照。
- `--software` 要求軟體 GPU 介面卡；若不可用則以 CPU 算圖並顯示原因。
- `--presets-folder` 指定獨立預設庫。
- RAW 的預覽與匯出均從浮點場景來源執行白平衡、局部遮罩與既有效果，再完成顯示收尾；解碼與 demosaic 由 CPU 執行，Metal 路徑不把 CPU 算好的圖冒充 GPU。超過裝置尺寸／記憶體限制時明確改用 CPU。
- 每筆 RAW `export_log.json` 的 `Renderer` 會附上 `RAW GPU` 或 `RAW CPU`，降級時保留原因；一般影像維持原有裝置標示。精度、量測與 Halide AOT 取捨見 [ADR 0003](adr/0003-gpu-stack-decision.md)。
- AI 降噪另記於每筆匯出紀錄的 `Denoise`：模型身分與 SHA-256、實際推論 provider、推論時間、tile 數及快取命中。效果 renderer 的 GPU 名稱不代表神經網路全部在同一裝置執行。
- RAW 面板提供 As Shot／自訂 2500–9900 K＋色偏與實際內嵌語意遮罩；沒有對應遮罩的照片保留設定但不全圖套用。外部 Lensfun-format 目錄可從面板瀏覽指定，儲存為專案相對路徑；也可使用 `FILMBATCH_LENSFUN_DIR`。不會下載資料庫。
- Lensfun 校正以 RAW 的預設裁切區（`DefaultCrop`）建立光學中心與半徑，再換算至完整感光區（`ActiveArea`）座標；非置中裁切的變形與暗角共用相同映射。
- `raw convert` 補上可用的相機色彩矩陣、hue/saturation table 與 tone，保留感光樣本、幾何及標準拍攝中繼資料；沒有可用色彩描述或目的檔已存在時拒絕寫入。標準 DNG profile 無法完整表示檔案內 JPEG 擬合外觀的逐像素亮度還原／chroma 曲線，不能宣稱轉前與轉後逐像素相同；相同色彩模型的對照與此限制分開記錄。

### 本機 AI 降噪

基本參數「AI 功能」裡的 AI 降噪預設關閉。先在同一區的模型面板閱讀署名／授權，再明確下載，或匯入官方 `.dtmodel` 套件／符合固定雜湊的 ONNX。收合「AI 功能」不會取消模型下載、匯入或進行中的降噪推論；要取消推論請先展開，再按模型面板的取消。RAW 場景來源自動使用 RawNIND Linear，一般 RGB 使用 NAFNet-SIDD；缺少／損毀模型時回報錯誤，不會在算圖時自動下載，也不以傳統濾鏡或原圖冒充成功。

```bash
filmbatch-cli denoise-models status
filmbatch-cli denoise-models install all
filmbatch-cli denoise-models import rawnind-linear "/path/rawdenoise-nind.dtmodel"
filmbatch-cli denoise-models import nafnet-sidd "/path/denoise-nafnet.dtmodel"
filmbatch-cli export "/path/to/photos" --out "/path/to/output" --format tiff --denoise 75
```

`--denoise 0–100` 只覆蓋本次匯出的啟用與強度，省略則沿用專案；不修改專案或預設。介面的總強度、亮度與色彩皆為推論結果與原圖的混合量，不是感光 ISO、模型雜訊 σ 或細節重建保證。相同來源的混合量與藝術效果調整使用神經網路快取；初次仍先全解析度推論。後續 RAW 強度調整使用快取的代理輸入，sRGB 則保留全圖逐像素夾取／量化次序、直接累積預覽，兩者均避免反覆配置全圖暫存；匯出仍用全解析度結果。

下載／匯入與照片設定分開；切換到需要不同模型的來源域時，舊安裝作業會取消，不能把 RGB 模型的完成狀態套到 RAW。明確取消預覽不會關閉照片的降噪設定；匯出中的工作請使用匯出面板自己的取消動作。

macOS Auto 嘗試 CoreML，Windows Auto 嘗試 DirectML；初始化失敗才明確標示並改用 CPU。`--software` 控制既有效果算圖器，不代表強制 AI provider 也改為 CPU。provider 報告含 CPU 混合執行的可能性，不承諾全部節點使用 GPU／ANE。CoreML 的融合圖可能先完成當前 tile 才處理取消，完成後仍丟棄已取消結果；取消／換照片不能發布舊結果。權重、署名及選型見 [ADR 0002](adr/0002-ai-denoise.md)。

本次 Apple Silicon 原生降噪介面及 self-contained GUI smoke 各 25 項通過；已實際執行隨附 CLI 的模型下載／匯入及兩個來源域的 16-bit PNG 匯出，保留 ICC 與來源雜湊。Windows 完成交叉封裝及 DirectML 原生庫／授權閉包檢查，未執行 Windows 推論。實拍畫質、測試數量及證據索引見[AI 降噪驗收紀錄](superpowers/plans/2026-10-19-ai-denoising.md#observed-acceptance-evidence--2026-10-19)；不宣稱 Lightroom 同等品質或所有相機均已驗證。


### 本機 AI 去背

在基本參數「AI 功能」裡的「去背」區，點選「AI 選取主體」。收合「AI 功能」會丟棄未接受的單張草稿與進行中的單張選取；進行中的批次去背不會因此取消，完成後仍一次保存，重新展開可看進度。macOS 透過系統 Apple Vision 產生前景遮罩；API 需求為 macOS 14 以上，不代表整個應用程式已在該版本驗收。封裝的部署下限仍由[實際原生相依庫](#macos)決定，本輪只在 macOS 26 執行。應用程式不另下載權重，也不呼叫雲端或外部 CLI。其他平台的 AI 按鈕停用，仍可用「修正遮罩」手動畫筆；沒有主體或原生失敗時明確回報，不把空白或全圖當成成功提案。

1. 草稿顯示未裁切的來源，以棋盤格表示透明區；切換「保留筆刷／移除筆刷」、調整大小，或反轉遮罩。
2. 「套用」只修改焦點照片，形成一筆可復原的操作；「取消」、換照片／工具或 app 暫停會丟棄未接受草稿。已套用的結果可用「還原背景」移除，也可復原這個操作。
3. 遮罩存於 `.filmbatch/project.json`，不修改原始影像；移動整個照片資料夾、重開、縮圖或匯出不需重新選取主體。預設、複製／貼上不會把遮罩帶到別張照片；重設照片會移除遮罩。
4. 透明結果請匯出 PNG16 或 TIFF16。若遮罩處理後仍有透明像素，JPEG／DNG 明確失敗，不默默丟棄透明度。CLI 沿用已保存的遮罩：

   ```bash
   filmbatch-cli export "/path/to/photos" --out "/path/to/cutouts" --format png
   ```

AI／新草稿的工作影像長邊最多 2048，遮罩以來源參考尺寸對齊完整影像，不先放大保存；已存的較高解析度遮罩不因打開較小預覽而降解析度。CPU／GPU 在裁切與幾何之前，只將覆蓋率乘進原始 alpha 一次；RAW 仍保留場景線性 RGB。Vision 可能把多個物體一起選取或選錯邊緣，必須檢視並修正；這是去背，不是重建被遮擋背景，[生成式物件移除](object-removal-design.md) 的 NO-GO 與 T1–T5 不變。

#### 批次去背與草稿逐筆復原

在縮圖列選取多張，點選「批次去背選取照片」：以開始時的選取與設定快照逐張推論，不跟隨後來的焦點改變。已有遮罩的照片原樣略過；全部處理後一起保存成一筆可復原操作，原始照片、曝光與其他設定不改變。完成摘要分別列出套用、略過、無主體與失敗，錯誤檔名另列出。

- 點選「取消」後，只保存已完成的遮罩，不接受進行中的結果；未開始項目標為取消。換專案、暫停或關閉則丟棄整批過期投遞，不能把結果或復原紀錄寫到新專案。推論期間修改過的照片／設定不被舊快照覆寫，保存衝突也不消耗 undo。
- 修正單張遮罩時，按下／移動／放開是一筆草稿操作；反轉另是一筆，重疊筆畫能還原原本的 16-bit 部分覆蓋率。草稿區提供「復原／重做」，共用快捷鍵為 macOS ⌘Z／⌘⇧Z，Windows 為 Ctrl+Z／Ctrl+Shift+Z；行動按鈕共用路由，但本輪沒有 iOS 原生驗收。
- 草稿歷史即使為空，也不會穿透去復原較早的照片設定。「取消」保留已接受遮罩；「套用」只保存目前筆畫復原後的狀態，形成一筆照片命令。歷史以區塊差異保存，上限為 200 筆／32 MiB 樣本 payload，不逐筆複製整張遮罩。

CLI 使用同一批次引擎，可直接產生遮罩，再沿用既有匯出：

```bash
filmbatch-cli remove-background "/path/to/photos" --presets-folder "/path/to/presets"
filmbatch-cli export "/path/to/photos" --out "/path/to/cutouts" --format png --presets-folder "/path/to/presets"
```

也可從本次來源執行 `dotnet run --project src/FilmBatch.Cli -c Release -- remove-background "/path/to/photos"`；`--presets-folder` 可省略，省略時使用使用者預設庫。舊 `dist/` 二進位檔不會因來源變更自動取得新命令。重跑會略過既有遮罩，不再次推論；`remove-background` 只保存專案，不另外匯出 RGB。

| CLI 結束碼 | 意義 |
|---|---|
| 0 | 可處理項目完成，含全部已有遮罩的 no-op |
| 1 | 資料夾內沒有支援照片 |
| 2 | 資料夾／預設／專案無效，或不支援原生 Vision 平台 |
| 3 | 有無主體、逐張錯誤或保存失敗；其他成功項目仍可能已保存 |
| 130 | 使用者取消，已完成項目仍可保存；未完成項目不接受 |


#### 去背驗證現況（2026-10-22）

目前完成的是來源程式的去背功能與下列驗證，不是新發行包或跨平台驗收：

| 實際執行的檢查 | 結果與範圍 |
|---|---|
| Release solution build | 0 warnings／0 errors |
| 完整 Release 回歸 | Core 611 passed／27 skipped；RAW 626 passed／293 skipped；App 215 passed／0 skipped；合計 1,452 passed／0 failed／320 skipped；外部 RAW 語料與平台略過不算通過 |
| CPU／GPU 遮罩覆蓋率 | 兩條路徑均通過，GPU 案例實際編譯並執行遮罩 shader，未用 CPU fallback 冒充 |
| 真實 Vision＋Headless Skia 共用桌面 | 本輪 17 項通過：三張選取中套用 2、略過既有遮罩 1；批次一次復原／重做、兩次筆畫與逐筆復原、共享快捷鍵、套用／重開、來源不變、PNG 匯出與取消進行中結果 |
| GUI PNG16 獨立解碼 | 615,360 個 alpha 樣本與套用後遮罩完全相同，保留原有 12345 的部分覆蓋率 |
| 真正 CLI 與 Ctrl-C | 9 項工作流程檢查通過；產生 2 張、重跑略過且專案雜湊不變，損毀／無主體為 3，空資料夾為 1，無效資料夾為 2；12 張案例在第一張完成後送 SIGINT，保存 1 張、其餘取消、exit 130 |
| CLI PNG16 獨立解碼 | 兩張合計 1,164,480 個 alpha 樣本與專案 zlib 解壓遮罩零差異；來源照片雜湊不變 |

完成本頁的還原／建置後，可從儲存庫根目錄重跑去背相關回歸；本輪實際執行為 Core 43、App 13 通過，0 failed／0 skipped：

```bash
dotnet test FilmBatch.sln -c Release --no-restore --nologo --filter 'FullyQualifiedName~Foreground|FullyQualifiedName~BackgroundRemoval|FullyQualifiedName~PreparedSource_StillRendersForegroundCoverageAfterDenoising'
```

`ForegroundSelectionTests` 涵蓋原生取消與無主體結果；`ForegroundPersistenceTests` 涵蓋格式 1／2 遷移、格式 3 遮罩、損毀輸入及保存衝突下的 undo／redo；`ForegroundAlphaTests`、`BackgroundRemovalTests` 與降噪案例涵蓋透明度及 RGB／RAW 來源重建。`ForegroundBatchTests` 驗證不變來源／設定、獨立遮罩、已完成與取消分界、快照變更及一次保存；`ForegroundBatchUiTests` 驗證選取改變、專案替換、暫停與取消的所有權；`ForegroundHistoryTests` 驗證逐筆復原、重做分支、反轉、部分覆蓋率及不消耗照片歷史。沒有匹配案例、平台或 GPU 略過均不得算通過。

其後暗部功能驗證已補齊 Naga 30.0.1，對產品實際嵌入的 35 個 WGSL 全部通過，包含去背 shader；詳見[暗部功能驗證](#暗部功能驗證2026-10-22)。此項靜態檢查不代替下列原生視窗／平台驗收。

尚未驗證的項目：

- 原生去背視窗操作：先前已觀察到桌面鎖定（`activeDisplays=0`）且 Avalonia.Native 啟動回傳 `-6661`，本輪未為確認相同限制而重試。Headless 畫面不代替 `NSWindow` 驗收，未替換計時器或抑制例外；須在可用的桌面工作階段補跑。
- Windows／iOS 去背實機、神經網路硬體放置、人類畫質與髮絲級邊緣品質。
- 包含本次去背變更的新 `.app`／Windows／iOS 發行包尚未重新封裝；先前版本的封裝或原生 smoke 結果不得延伸為本次新功能通過。
- 獨立 OpenCV/libpng 讀取本輪 PNG 時仍回報既有 `iCCP: known incorrect sRGB profile` 警告；alpha 核對通過，但不是 ICC 無警告的證據。本輪未修改或忽略該色彩設定檔。

一次性端到端驅動、TRX、畫面與 PNG／CLI 核對收據保留於儲存庫外的工作階段證據。2026-10-21 的四份驗證來源已封存為 `verification-harnesses.tar.gz`（SHA-256：`c914a5def6c6b990b2be2e279b42a1a48f570c142f20a888cd0c4e9a3b2cca99`），是上一輪單張去背的證據，不代替本輪批次／逐筆結果。本輪另保存實際驅動與來源封存，臨時驅動不成為產品依賴；既有照片／研究資料未清除。fresh clone 的永久入口為上方命令及 [VERIFY V1](../VERIFY.md#v1)。暗部研究與後續實作另見[局部暗部還原](#局部暗部還原)，不以去背驗證代替。

## 局部暗部還原

這是獨立的 `shadow_recovery` 模組，**預設關閉**；既有「陰影」／`basic.shadows` 的顯示域行為不變。適用於 RAW 與一般 RGB 來源，不需要 AI 模型、下載或 LibRaw 相依。最終 JPEG／PNG／TIFF 仍為既有的顯示輸出，不是 HDR 感光資料。

1. 在基本參數的「暗部還原」展開並啟用；若找不到，先取消「隱藏已停用的模組」。
2. 以「暗部提亮」調亮暗處，再調局部範圍；亮度降噪保守使用，以免抹掉紋理，彩色斑塊可另調色度降噪。
3. 拖曳一次可整段復原／重做；設定沿用預設、複製貼上、逐張保存與批次匯出。專案格式仍為 3，不需遷移或重新解碼成另一種檔案。
4. 比較細節請看 100%／實際匯出，不只看縮小預覽。白平衡／Zone 照片取樣會暫停此局部映射，接受提案後恢復。

| 控制項 | 預設 | 範圍與含義 |
|---|---|---|
| 暗部提亮 | 3 EV | 0–5 EV；局部基底決定實際增益，不是整張固定曝光 |
| 局部範圍 | 2% | 裁切後短邊的 0.25–10%；不是降噪視窗大小 |
| 亮度降噪 | 35% | 0–100%；增益前混合暗部亮度差，可能犧牲細紋 |
| 色度降噪 | 80% | 0–100%；增益前混合彩色差，與亮度強度分開 |

演算法在未夾取的工作線性 sRGB 上，以覆蓋率加權 guided filter 求局部基底，先降噪再乘共同 RGB 增益；不加黑位偏移、不改 alpha，全透明隱藏色不污染鄰近像素。高光仍交原有顯示映射處理，不能恢復拍攝時已完全裁切的資訊。局部半徑按影像比例縮放，降噪按原始來源像素足跡縮放；很小的預覽可能減弱或跳過次像素降噪，並非和全圖逐像素相同。兩種有效降噪與提亮都為零時直接略過 CPU／GPU pass。

### 暗部功能驗證（2026-10-22）

本輪實際執行：

| 檢查 | 已觀察結果 |
|---|---|
| 完整 Release 來源回歸 | Core 625 passed／27 skipped；RAW 626 passed／293 skipped；App 215 passed／0 skipped；合計 1,466 passed／0 failed／320 skipped |
| `ShadowRecoveryTests` | 11 passed；黑點／暗部細節、亮度與色度抑噪、透明隱藏色隔離、HDR、高光分離、舊外觀、單軸影像與實際 GPU 容差 |
| 真正 Sony A7S RAW 全圖 | 4240×2832；工作浮點含負值及 >1 的高光，來源雜湊不變；Apple M3／Metal 的 CPU／GPU RGBA16 最大差 1，未用 CPU fallback 冒充 GPU |
| 空間／取消 smoke | 部分 alpha、旋轉／翻轉／裁切 RGB 最大差 1、alpha 差 0；零控制量與極小來源足跡完全恆等；進行中取消後同一算圖器可重用 |
| 共用 Headless 桌面 | 11 項操作通過：真實 RAW、啟用／滑桿、整段復原／重做、重開、取樣排除、預設／貼上、英文標籤、PNG 匯出及原檔不變 |
| GUI PNG16 獨立解碼 | OpenCV 讀取實際檔案，3,849,600 個 RGBA16 樣本與全圖 CPU 算圖再縮放的結果零差異 |
| 真正 CLI 重新開啟專案匯出 | `export --format png --long-edge 1200 --software` 讀取已保存的暗部設定；3,849,600 個 RGBA16 樣本與 GUI 匯出完全相同，明示使用 CPU |
| 產品嵌入 WGSL | Naga 30.0.1 驗證 35 個檔案通過，包含暗部統計／盒狀／套用與共用函式；不是只檢查儲存庫外副本 |

取得 SDK、還原相依後，從根目錄重跑永久回歸：

```bash
dotnet test FilmBatch.sln -c Release --no-restore --nologo --filter 'FullyQualifiedName~ShadowRecoveryTests|FullyQualifiedName~AppleImageCodecTests|FullyQualifiedName~RawControlsTests'
```

GPU／平台或外部 RAW 語料略過不算通過。全圖診斷為另驗全尺寸預覽而明確提高為 2 GiB 預算；產品預設預覽預算未變，匯出仍走既有分段／不足時明示 CPU 的流程，這不是單一 pass 的效能或記憶體基準。Headless 圖片不代替原生 `NSWindow`；先前桌面鎖定回傳 `-6661` 的限制未重試。Windows／iOS 暗部實機、人工畫質盲評、其他相機／ISO 與 Lightroom 對照未取得。

獨立 OpenCV/libpng 讀取 PNG 仍回報既有 `iCCP: known incorrect sRGB profile` 警告；像素核對通過，不代表 ICC 無警告，本輪沒有抑制警告或偷偷替換設定檔。一次性驅動、實際結果與可還原來源封存於儲存庫外，臨時驗證程式不成為產品相依；未 commit／push 或重新封裝發行包。[研究與三相機解碼比較](shadow-recovery-survey.md)另外區分感光數值、共同現像與無法判定的高光／黑白位資訊。

### Sony RAW 控制項驗證

使用公開 Sony A7S ARW2 實跑既有自訂 WB 控制：真正介面的 Kelvin／色偏變更會更新場景預覽及縮圖，停用／再啟用、拖曳復原／重做、專案重開與 PNG16 匯出皆通過；獨立解碼的 3,849,600 個 RGBA16 匯出樣本與預期結果相同，原檔雜湊不變。這個樣本未重現「Sony 調整全部無效」，未因此盲改 decoder 或加色偏補償，也不推廣到未提供的 Sony 機型／檔案。

相機選項不是每種 RAW 都有資料：本樣本沒有可用的內嵌鏡頭修正表、DNG `ProfileGainTableMap` 或語意遮罩，因此切換這些選項是無效果；自訂白平衡則有可見差異。縮圖預設可能取相機內嵌 JPEG，不可用未經現像的內嵌預覽判定 RAW 設定是否生效；有自訂白平衡的本次縮圖路徑已另外驗證。

### iOS JPEG 色彩核對（2026-10-22）

使用目前來源的 `org.filmbatch.editor` Release，在 iOS 27.1 Simulator 執行；隔離 .NET SDK `10.0.402`、workload set `10.0.401.1`／iOS SDK `27.0.10722`，沿用上列明示的 Xcode 版本實驗，不修改全機 SDK 或 `global.json`。輸入是自有合成 JPEG：無 ICC、sRGB、Display P3、Adobe RGB 不對稱色塊，以及 4032×3024 的 P3／sRGB 漸層與中性灰卡，不是使用者回報的原檔。

| 實際路徑 | 已觀察結果 |
|---|---|
| 主要編輯器預覽、縮圖、曝光編輯／復原及綁定匯出控制項 | 11 項檢查通過；6 張來源與縮圖完成、0 失敗，原始 JPEG 雜湊不變 |
| PNG16 原尺寸匯出 | 6 張成功；OpenCV 獨立解碼並對 LittleCMS 色彩參考核對全圖，最大差 1 個 8-bit 色階，最大 RMSE 0.291 |
| JPEG 原尺寸匯出 | 6 張成功；不對稱色塊與兩張大圖的灰卡通過，大圖灰卡均為 `(128,128,128)`；未以灰卡結果推論 JPEG 邊緣或全圖逐像素相同 |
| 移除診斷驅動後的主要 App | 另建乾淨版本並安裝啟動，實際畫面有預覽及 7／7 縮圖、0 失敗；恢復原測試工作區設定並重定位安裝後的 container 路徑 |

獨立 CMS 參考以另一個 iOS ImageIO 程式取得來源 JPEG 解碼後的 RGB，再由主機 LittleCMS 依輸入 ICC 轉為 sRGB。這將 JPEG 解碼差異與 ICC 轉換差異分開：最初直接用 Pillow 解碼的大圖 P3 比較在極端色階出現最大約 12.85／255 差異；分離解碼邊界後，原有像素容差未放寬，色彩核對通過。最初失敗與診斷保留，沒有據此替產品加色偏補償。

完整操作由儲存庫外暫時編入的驅動呼叫真正共用編輯器及綁定控制項，並使用實際 iOS Engine 匯出；不是 Mac host 或另寫一套 encode。這不等於原生 AX／手指觸控驗收；此 Simulator 的 XCTest AX 查詢失敗已另留證據。驅動與乾淨版是不同的 linked bundle，不宣稱二進位完全相同；乾淨版另保留啟動畫面。

結論限於這組輸入與 Simulator：**未重現系統性偏紅，未修改 JPEG 解碼或色彩管線，不宣稱已修好使用者原檔。**實機螢幕、True Tone／HDR JPEG、未知 ICC／相機及未提供的原檔仍未驗證。既有 Apple stock sRGB 的 libpng `iCCP` 警告仍可見，沒有抑制。原生輸出、截圖、獨立核對結果及可還原驅動封存在儲存庫外；不新增產品診斷相依或新版 IPA。

## 互動、記憶體與濾波修正（2026-10-22）

本輪修正滑桿逐次同步寫專案、排程鎖內的耗時回呼，以及透明影像縮小的隱藏色滲入；縮圖來源／點陣圖改為有界保留，AHD 重用工作緩衝、RAF 預覽借用容器切片，RAW 降噪強度調整重用代理輸入。GPU 高斯核心保留原權重與邊界，並非近似模糊；JPEG 提示只影響符合尺寸條件的桌面預覽，完整匯出不降解析度。PNG16 明確寫入 16-bit RGBA，避免少色影像失去 alpha 精度。

完整 Release 回歸與重新封裝的 Apple Silicon App 已實跑，原生視窗另外操作拖曳／放開／復原、跨專案中斷，以及 400 張照片的縮圖捲回重載；不是以 Headless 取代原生驗證。發行包在 `dist/osx-arm64/FilmBatch.app`，CLI 在 `dist/osx-arm64/cli/filmbatch-cli`，本次為本機 ad-hoc 簽章，未公證。

前後量測、輸入雜湊、精度差異、完整測試數與重跑命令見[本輪驗證紀錄](superpowers/plans/2026-10-22-performance-correctness.md#observed-results--2026-10-22)。JPEG 預覽不是逐位元相同；Fuji 完整解碼在本次量測反而較慢，記憶體改善不等同所有路徑加速。Windows／iOS、未下載的 RAW 語料、人工畫質排名與 Sigma BF 根因均未由本輪結果驗收；Sigma 的獨立研究分支仍缺可讀取的原始 DNG。

## 資料位置

| 內容 | 位置 |
|---|---|
| 設定、版面、預設與記錄檔 | Windows：`%APPDATA%\FilmBatch\`；macOS：`~/Library/Application Support/FilmBatch/` |
| 每個照片資料夾的專案與匯出紀錄 | `<照片資料夾>/.filmbatch/` |
| AI 模型與 CoreML 編譯快取 | 上列使用者資料根目錄的 `models/denoise/`；不寫進 `.app`，不隨應用程式封裝權重 |

macOS 的快捷鍵使用 ⌘（例如 ⌘O、⌘Z、⌘⇧Z），並使用原生選單列：應用程式選單提供「關於」與「偏好設定」（⌘,），切換介面語言時選單列即時更新。
