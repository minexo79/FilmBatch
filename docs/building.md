# 建置、測試與發佈

依任務找文件見[文件地圖](README.md)；完整的執行順序、失敗分類與證據邊界見[驗證入口](../VERIFY.md)。

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

`.github/workflows/ci.yml` 與 `.circleci/config.yml` 共用還原相依套件、Release 建置、完整測試、隨附 JPEG XL ABI／樣本解碼、封裝及 GUI 冒煙測試命令；WGSL 均以固定的 naga-cli `30.0.1` 驗證產品的 `ShaderLibrary` 輸出。

#### 平台與 SDK

| 用途 | GitHub Actions | CircleCI |
| --- | --- | --- |
| Windows x64 桌面 | `windows-2022` | `windows-server-2022-gui:current` |
| macOS Apple Silicon 桌面 | `macos-26` | Xcode `26.4.1`／macOS `26.3`、`m4pro.medium` |
| WGSL 驗證 | `ubuntu-24.04` | `ubuntu-2404:current` |
| 手動 Dawn 重建 | `macos-15` | Xcode `16.4.0`／macOS `15.3.2`、`m4pro.medium` |

兩邊安裝 `global.json` 的 `sdk.version`；`rollForward` 僅控制執行時從已安裝 SDK 中選擇版本，不代表安裝最新 `10.0` 通道。CircleCI 的 Windows 工作使用 Git Bash 並明確啟用 `errexit`／`pipefail`。

映像對照於 2026-10-18 依 [CircleCI 支援表](https://circleci.com/docs/using-macos/)及官方 manifest（[26.4.1](https://circle-macos-docs.s3.amazonaws.com/image-manifest/v17723/manifest.txt)、[16.4.0](https://circle-macos-docs.s3.amazonaws.com/image-manifest/v15338/manifest.txt)）核對；Windows 依[官方執行環境說明](https://circleci.com/docs/using-windows/)。Xcode 版本不等於宿主 macOS 版本，例如 Xcode 26.0.1 的宿主是 macOS 15.6，不能滿足隨附 Dawn 的 macOS 26.0 最低需求。這些設定統一 OS 基線，不保證兩家託管映像的修補版本或所有預裝工具完全相同。

#### 合併必要檢查

- **GitHub Actions**：在 pull request、merge queue（`merge_group`）、推送至 `master` 及手動觸發時執行。`pre-merge` 使用 `always()` 檢查 `desktop`、`shaders` 的結果，只有全部成功才通過；失敗、取消或略過均不放行。在 `master` 的分支保護／ruleset 中將 **`pre-merge`** 設為必要檢查。
- **CircleCI**：不設只有 `echo` 的 `pre-merge` 聚合工作。若以 CircleCI 作為合併閘門，須啟用 **Project Settings → Advanced Settings → VCS Status Updates**，並將以下三項 **job 狀態**全部設為必要檢查：
  - `ci/circleci: desktop-win-x64`
  - `ci/circleci: desktop-osx-arm64`
  - `ci/circleci: shaders`

CircleCI 的 workflow-level GitHub Checks 與上述 job 狀態不同，不能在停用 VCS Status Updates 後仍要求不存在的 job 狀態；名稱及回報方式見[官方說明](https://circleci.com/docs/guides/integration/status-updates/)。原本的 `ci/circleci: pre-merge` 必要檢查應移除。三個工作彼此不因另一個失敗而中止，也不會因失敗的上游 `requires` 留下未啟動的假聚合工作。

#### CircleCI 觸發設定

GitHub App 專案在 **Project Settings → Project Setup**，為同一個 pipeline definition 設定兩個 trigger：

1. **PR opened or pushed to, default branch and tag pushes**；此儲存庫的預設分支須為 `master`。YAML 會排除自動 tag 及 PR 關閉／標籤等非建置事件。
2. **Pushes to merge queues**，接收 `gh-readonly-queue/*` 分支推送。

不要以 **All pushes** 代替第一項：開啟 PR 的分支推送及一般分支推送都可能回報 `push`，不能只靠 YAML 的事件名稱分辨。分支與 PR 成員資格由 trigger 篩選，workflow 再篩選事件／tag。UI **Trigger Pipeline** 或 API 手動觸發會同時執行 `ci` 與 `dawn-build`，不再傳入 `run-dawn-build`；手動指定 tag 亦可執行。重跑既有 pipeline 保留原來的觸發事件，不等同建立新的手動 pipeline。

GitHub App **不會觸發 fork PR**；如果需要接受外部 fork，應保留 GitHub Actions 的 `pre-merge` 作為必要閘門，不要把無法回報的 CircleCI job 狀態設為所有 PR 的必要條件。這是供應商整合限制，不表示 fork PR 已在 CircleCI 驗證。trigger 選項及限制見 [CircleCI 官方說明](https://circleci.com/docs/github-trigger-event-options/)，GitHub merge queue 依[官方事件說明](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#merge_group)設定。

#### 逾時與遠端設定界線

GitHub Actions 的 `timeout-minutes` 是整個 job 上限。依本次選擇，CircleCI **不新增 API token**：`timed-run` 共用剩餘命令預算（桌面 30 分鐘、WGSL 15 分鐘、Dawn 360 分鐘），逾時回傳 124，必要時在 30 秒終止寬限後強制結束；初始化、checkout、產物上傳與 runner 等待不受此包裝器控制，CircleCI 方案的整個 job 上限亦可能較短。不能將兩者宣稱為相同的硬性 job deadline。命令透過環境變數傳入子 Bash，保留單引號、換行、`pipefail` 及原始失敗碼。Mac 初始化若需安裝 coreutils，會禁止 Homebrew 自動升級既有相依使用者及清理舊版。

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

`artifacts/dawn/` 同樣不進版本庫。CI 的 `dawn-build` 只在手動觸發時產生重建檔，`desktop` 工作不會自動下載；fresh clone 沒有該產物時，不能以 `Info.plist` 的宣告代替原生相依庫的實際部署下限。

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
- RAW 面板提供 As Shot／自訂 2500–9900 K＋色偏與實際內嵌語意遮罩；沒有對應遮罩的照片保留設定但不全圖套用。外部 Lensfun-format 目錄可從面板瀏覽指定，儲存為專案相對路徑；也可使用 `FILMBATCH_LENSFUN_DIR`。不會下載資料庫。
- Lensfun 校正以 RAW 的預設裁切區（`DefaultCrop`）建立光學中心與半徑，再換算至完整感光區（`ActiveArea`）座標；非置中裁切的變形與暗角共用相同映射。
- `raw convert` 補上可用的相機色彩矩陣、hue/saturation table 與 tone，保留感光樣本、幾何及標準拍攝中繼資料；沒有可用色彩描述或目的檔已存在時拒絕寫入。標準 DNG profile 無法完整表示檔案內 JPEG 擬合外觀的逐像素亮度還原／chroma 曲線，不能宣稱轉前與轉後逐像素相同；相同色彩模型的對照與此限制分開記錄。

## 資料位置

| 內容 | 位置 |
|---|---|
| 設定、版面、預設與記錄檔 | Windows：`%APPDATA%\FilmBatch\`；macOS：`~/Library/Application Support/FilmBatch/` |
| 每個照片資料夾的專案與匯出紀錄 | `<照片資料夾>/.filmbatch/` |

macOS 的快捷鍵使用 ⌘（例如 ⌘O、⌘Z、⌘⇧Z），並使用原生選單列：應用程式選單提供「關於」與「偏好設定」（⌘,），切換介面語言時選單列即時更新。
