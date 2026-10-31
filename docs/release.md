# 發佈與 iOS 行動版

Windows／macOS 發佈、簽章與公證，以及 iOS 行動版的建置與操作。

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

**Simulator 產物路徑限制（2026-10-29）**：在 .NET SDK `10.0.402`、iOS workload `27.0.10722` 與 Xcode `27.1` 的實驗組合（單次停用 Xcode 版本檢查）中，使用絕對 `--artifacts-path` 時，建置可能成功卻把 `.app` 的 metadata／簽章與執行檔／資源分到 `bin` 和巢狀 `publish` 兩處，前者因缺少執行檔而無法安裝。SDK 先以 `MSBuild::MakeRelative` 計算 bundle 路徑；當專案與產物路徑沒有共同的第一個目錄時，MSBuild `18.9.15` 會回傳絕對路徑，後續 target 又把它接在專案目錄後。實測 `/Users/...` 對 `/var/...`、以及解析後的 `/private/var/...` 對 `/var/...` 都會命中；因此不能把問題只歸因於 `/var` 符號連結。

優先在隔離的來源副本執行上方的標準 simulator build，不使用 `--artifacts-path`。若使用產物目錄，應以 `pwd -P`／實體路徑一致化來源及產物路徑，並確保兩者具有共同的第一個目錄；本實驗在來源及產物皆為 `/private/var/...` 時，Release simulator bundle 也能直接安裝及啟動。這不代表所有 workload 或真機 publish 已驗證。檢查 app 內主執行檔，並以實際 simulator 安裝和啟動驗證簽章；勿把手動合併、ad-hoc 重簽的測試 bundle 當作正式產物。

可用已安裝該 workload 的 SDK 在隔離來源副本診斷路徑；這只計算屬性，不是建置或驗證 app。將下列路徑換成要檢查的絕對產物目錄；若 `_AppBundlePath` 仍為絕對路徑，檢查 `_RelativeAppBundlePath` 是否錯誤地包含專案目錄。範例的 Xcode 版本覆寫僅適用上述實驗組合。

```bash
dotnet msbuild src/FilmBatch.Mobile.iOS -t:_ComputeVariables \
  -p:_ComputeVariablesDependsOn=_GenerateBundleName \
  -p:Configuration=Release -p:RuntimeIdentifier=iossimulator-arm64 \
  -p:UseArtifactsOutput=true -p:ArtifactsPath=/var/filmbatch-probe \
  -p:ValidateXcodeVersion=false \
  -getProperty:MSBuildProjectDirectory,_AppBundlePath,_RelativeAppBundlePath
```

2026-10-22 的 JPEG 偏紅調查已在目前來源的主要編輯器補跑原生預覽、編輯／復原及實際匯出；[輸入、獨立色彩參考與未重現範圍](evidence/ios-2026-10-20.md#ios-jpeg-色彩核對2026-10-22)另列，不沿用較早 binary 的結果。

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
- **Projects／導覽**：重開 app 內的專案與已存編輯。頂部保留 Projects、匯入、Undo／Redo、More；More 提供複製／貼上、重設、選擇性貼上、建立／管理預設、偏好設定與停止背景載入。
- **操作狀態**：自動重開專案或選取器未選取而完成後，清除暫時的「處理中」；匯入結果、取消、失敗或操作期間更新的其他狀態保留。預覽與縮圖的背景進度另行顯示。
- **資料夾用途**：開啟照片資料夾使用獨立的專案匯入流程；Lensfun 等目錄參數複製至 app 自有的 `Assets`，不在 Projects 中建立空白相片專案。
- **編輯面板**：五個有文字的入口是預設、調整、裁切、效果與匯出，重用完整共用編輯 View。手機初始收合工具，點工具開啟底部可捲動參數區；再點同工具或關閉回到大預覽。寬版／短橫向將同一參數區移到右側，關閉後釋放側欄空間；轉向不重建專案或復原堆疊。關閉套用有效裁切；未套用裁切可用裁切取消或非文字輸入時的 Escape 放棄。取樣／去背的取消沿用既有命令，不把隱藏的畫布工具留在背景。
- **照片列／批次**：100 點高的縮圖列可獨立收合；「選取」模式中點縮圖增減目標，完成選取不清掉集合。複製、貼上、選擇性貼上與全選使用真實選取集合及原有一次手勢一筆復原契約；篩選在獨立對話框內操作。
- **觸控／鍵盤**：預覽保留平移、雙指縮放，原圖與分割比較有明確按鈕。行動版主要操作與參數控制至少 44 點，桌面預設尺寸不變。safe-area 由 host 套用一次，只有覆蓋視窗底邊且橫跨全寬的鍵盤遮擋才縮小工作區與對話框；浮動鍵盤不加入整面留白。文字欄位取得焦點時也提供收鍵盤動作，不依賴平台回報為停駐鍵盤。對話框固定標題／動作、內容可捲動、Tab 循環及關閉後還原焦點；背景取消不當作確認。文字欄位保留自己的編輯快捷鍵；非文字輸入時支援 Cmd／Ctrl Z、Shift Z、C、V、A、S 與 0／1 縮放。
- **匯出**：共用批次工作建立 JPEG、PNG16、TIFF16 或算圖後的線性 DNG，保留逐張錯誤／取消結果。「建立輸出」先存於 app 的 `Documents/FilmBatch/Exports`；「複製所有輸出至 Files」複製各專案已完成檔案至新批次資料夾，不只限最近一批。保留相對子目錄，同名檔不互相覆寫。DNG 不是未經處理的感光元件原檔。
- **生命週期**：背景時取消 I/O、分析、待處理預覽／縮圖及匯出，保存有效編輯；回前景不清掉專案、選取或復原。同步解碼只能在安全檢查點停止，不承諾背景持續完成工作。
- **預設與背景恢復**：桌面保留檔案即時監看；iOS 不呼叫不受支援的 `FileSystemWatcher`，app 內修改即時更新，外部修改於回前景時重新載入。背景取消的同步詢問不視為「保留舊預設」，環境取消的偏好設定／管理預設視窗不會自動套用未確認的修改。
- **CPU 目標**：iOS 目前明確採用 CPU 攝影算圖；70% 為合作式背景軟目標，不是硬限制。Darwin process clock 提供實際程序 CPU 時間，避免呼叫 iOS 不支援的 `Process.TotalProcessorTime`。

### 驗證邊界

macOS 的相同行動 View、Apple codec 回歸及桌面 solution 測試，均不代替實際 iOS app 的 Files／Photos、編輯、輸出與背景／前景操作。iOS Dawn GPU、JPEG XL 原生解碼閉包、實機簽章／部署、iPad 效能／記憶體上限、HDR／EDR 及完整 AI 降噪的裝置驗收不在既有行動版證據涵蓋範圍；合併 AI 分支不會自動擴大這些主張。

2026-10-31 的[Mobile UI 原生實測](superpowers/plans/2026-10-30-mobile-editor-redesign.md#7-2026-10-31-執行結果與驗證邊界)已完成使用者選定的隔離 Simulator 功能驗證：Files／Photos、批次編輯與復原、PNG16／JPEG 及 Files 副本、窄視窗裁切／resize、軟鍵盤／modal Escape、背景／重開與選取取消均有實際通過證據，輸出與來源另做獨立讀回。完整批次與修正 AX driver 後的單項通過分列，不冒稱一次全平台零失敗。`ValidateXcodeVersion=false` 僅施於隔離來源副本的命令列；Xcode 27.2／.NET iOS 27.1.10018 仍不受支援，不能據此宣稱正式發佈驗收、真機測試或更新既有 IPA 的驗收狀態。

`AppleImageCodecTests`、`AppleImagingTests`、`TiffMetadataTests` 涵蓋 Apple 後端與中繼資料回歸；Apple 原生 cases 只在 macOS 執行。檢查 PNG 實際內嵌的 ICC 時，Magick.NET 的讀取設定須使用 `PngReadDefines.PreserveiCCP=true`：預設解碼器會把辨識出的 sRGB ICC 最佳化掉，`GetColorProfile()` 回傳空值不等於檔案沒有 `iCCP`。仍須以另一個解碼器核對 profile 內容，不修改產品去迎合讀取器的預設行為。

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

`--smoke`／GUI 啟動需要一個作用中的顯示器：螢幕休眠或無頭工作階段（`CGGetActiveDisplayList` 回 0）時 Avalonia 的 RenderTimer 會以原生錯誤 -6661 失敗。遠端操作時先 `caffeinate -u -t <秒>` 喚醒顯示器再執行；若仍失敗，檢查工作階段是否鎖定並解鎖後重試。實測曾在顯示器在線時遇到鎖定工作階段下的 -6661，解鎖後通過。

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
