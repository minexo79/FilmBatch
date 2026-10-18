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

UI 測試以 Avalonia.Headless 執行，不需要顯示器；只有沒有可用 WebGPU 介面卡時才略過 GPU 測試。軟體介面卡仍會執行測試，實際算圖、讀回或像素比對失敗不能當成「沒有 GPU」略過。

### Windows CI 測試排程

兩套 CI 的 Windows 工作都以同一個命令限制專案與測試集合併發：

```bash
dotnet test FilmBatch.sln -c Release --no-build -m:1 -- xUnit.ParallelizeTestCollections=false
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

最新 Windows 紀錄仍有三個失敗：全解析度 RAW 匯出已顯示 `scene.dng = [Device] is lost.`；另外兩個改為 `GpuSpikeTests.Gpu_MatchesCpuReference` 的零參數與負參數案例，在 `MapForRead` 失敗，後續介面卡不可用。先前同類錯誤發生於第一次 32×48 裁切讀回及 32×24 單帶讀回；這些尺寸與順序差異不支持「大圖超過緩衝區上限」或固定測試必死的結論，也不能單憑三次失敗判定 TDR、OOM 或行程黑名單。原生參照保留已獨立重現並修正，但是否就是 Windows 裝置遺失的原因仍須該 runner 確認。

WebGPUSharp 會將 [遺失與 uncaptured-error callback 排入 ThreadPool](https://github.com/EmilSV/WebGPUSharp/blob/30d51f3a460c9419a2ae6cc8dee54ed75a3c0008/src/Adapter.cs#L388-L468)，所以 `MapSync` 失敗時不保證 callback 已送達。D3D12 讀回現在即使 `IsLost` 尚未設定，也會保留原始綁定例外、介面卡與已收到的最近原生錯誤；未知原因明確標為尚未回報，不假造移除原因。晚到的遺失 callback 另寫入既有 logger，未設定 logger 時寫入標準錯誤。RAW 匯出測試先檢查 `summary.Errors`，CI 可直接顯示檔名與真正錯誤，不再只有 `Failed` 計數。若仍失敗，請同時保留讀回例外與稍後的 callback 訊息；本機驗證不能替代 Windows／D3D12 結果。

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

`.github/workflows/ci.yml` 與 `.circleci/config.yml` 共用還原相依套件、Release 建置、完整測試、隨附 JPEG XL ABI／樣本解碼、封裝及 GUI 冒煙測試命令；WGSL 均以固定的 naga-cli `30.0.1` 驗證產品的 `ShaderLibrary` 輸出。

#### 平台與 SDK

| 用途 | GitHub Actions | CircleCI |
| --- | --- | --- |
| Windows x64 桌面 | `windows-2022` | `windows-server-2022-gui:current` |
| macOS Apple Silicon 桌面 | `macos-26` | Xcode `26.4.1`／macOS `26.3`、`m4pro.medium` |
| WGSL 驗證 | `ubuntu-24.04` | `ubuntu-2404:current` |
| 手動 Dawn 重建 | `macos-15` | Xcode `16.4.0`／macOS `15.3.2`、`m4pro.medium` |

兩邊安裝 `global.json` 的 `sdk.version`；`rollForward` 僅控制執行時從已安裝 SDK 中選擇版本，不代表安裝最新 `10.0` 通道。CircleCI 的 Windows 工作使用 Git Bash 並明確啟用 `errexit`／`pipefail`。

兩邊的 `dawn-build` 會先用 runner 的 Python 3 建立 `$HOME/.filmbatch-dawn-tools` venv，安裝固定的 `cmake==3.31.6`，並將其 `bin` 加入後續步驟的 PATH；不假設 Xcode 映像已附 CMake，也不透過 Homebrew 安裝或更新工具。這只補足 Dawn 的建置前置需求，仍需 Metal SDK、Python 3 及下載原始碼所需的網路與磁碟空間。

映像對照於 2026-10-18 依 [CircleCI 支援表](https://circleci.com/docs/using-macos/)及官方 manifest（[26.4.1](https://circle-macos-docs.s3.amazonaws.com/image-manifest/v17723/manifest.txt)、[16.4.0](https://circle-macos-docs.s3.amazonaws.com/image-manifest/v15338/manifest.txt)）核對；Windows 依[官方執行環境說明](https://circleci.com/docs/using-windows/)。Xcode 版本不等於宿主 macOS 版本，例如 Xcode 26.0.1 的宿主是 macOS 15.6，不能滿足隨附 Dawn 的 macOS 26.0 最低需求。這些設定統一 OS 基線，不保證兩家託管映像的修補版本或所有預裝工具完全相同。

#### Windows 硬體 GPU 與軟體渲染

截至 2026-10-18，依 [resource class 參考](https://circleci.com/docs/reference/configuration-reference/#resourceclass)與 [GPU 執行環境指南](https://circleci.com/docs/guides/execute/using-gpu/)：

| 資源類別 | vCPU／RAM | NVIDIA GPU |
| --- | --- | --- |
| 目前的 `windows.medium` | 4／16 GB | 非專用 GPU 類別 |
| `windows.gpu.nvidia.medium` | 16／60 GB | Tesla T4、16 GiB GPU 記憶體 |

GPU 指南列為 Scale 方案，且需搭配 Windows GPU 映像；存取資格與映像應在切換前向 CircleCI 確認。不能只把現有一般 Windows 映像的 `resource_class` 改名，也不能將目前 runner 的通過結果當作 NVIDIA 硬體驗證。Windows 測試排程的調整不啟用專用 GPU 類別，也不變更遠端方案或設定。

Microsoft 的 [WARP 說明](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/directx-warp)指出 Basic Render Adapter 提供軟體渲染，Windows 10 起也支援 D3D12。診斷 log 的 `IsSoftware=False` 是綁定回傳的分類，不能據此認定為實體 GPU。可用軟體介面卡仍執行真正 WebGPU 算圖；裝置遺失或像素錯誤照常失敗，不以 CPU 算圖冒充成功。供應商變更方案、資源類別或映像時，須重新核對本節。


#### 合併必要檢查

- **GitHub Actions**：在 pull request、merge queue（`merge_group`）、推送至 `master` 及手動觸發時執行。`pre-merge` 使用 `always()` 檢查 `desktop`、`shaders` 的結果，只有全部成功才通過；失敗、取消或略過均不放行。在 `master` 的分支保護／ruleset 中將 **`pre-merge`** 設為必要檢查。
- **CircleCI**：不設只有 `echo` 的 `pre-merge` 聚合工作。若以 CircleCI 作為合併閘門，須啟用 **Project Settings → Advanced Settings → VCS Status Updates**，並將以下三項 **job 狀態**全部設為必要檢查：
  - `ci/circleci: desktop-win-x64`
  - `ci/circleci: desktop-osx-arm64`
  - `ci/circleci: shaders`

CircleCI 的 workflow-level GitHub Checks 與上述 job 狀態不同，不能在停用 VCS Status Updates 後仍要求不存在的 job 狀態；名稱及回報方式見[官方說明](https://circleci.com/docs/guides/integration/status-updates/)。原本的 `ci/circleci: pre-merge` 必要檢查應移除。三個工作彼此不因另一個失敗而中止，也不會因失敗的上游 `requires` 留下未啟動的假聚合工作。

#### CircleCI 觸發設定

此儲存庫使用 GitHub，因此這裡的 Merge Request 指 **Pull Request（PR）**，不是 GitLab 的 `merge_request` 事件。CircleCI 先由專案 trigger 決定是否建立 pipeline，再由 YAML 決定是否執行 workflow；只改 `.circleci/config.yml` 不會訂閱新的 webhook。

**GitHub App pipeline**：在 **Project Settings → Project Setup**，為同一個 pipeline definition 設定下列 trigger，事件來源選本儲存庫：

1. **PR opened or pushed to, default branch and tag pushes**：涵蓋 PR 建立（含草稿）、推送新 commit、重新開啟及預設分支推送；預設分支須為 `master`。
2. **PR marked ready for review**：草稿轉為可審查時再執行檢查；只有第一項不代表會收到此事件。
3. **Pushes to merge queues**：接收 `gh-readonly-queue/*` 分支推送。

YAML 接受的事件如下；每次執行 `ci` 都包含 Windows、macOS、WGSL 三項實際檢查：

| 事件 | `ci` | `dawn-build` |
| --- | --- | --- |
| PR `opened`、`synchronize`、`reopened`、`ready_for_review` | 執行 | 不執行 |
| 開啟 PR 的分支、預設分支或 merge queue 推送 | 執行 | 不執行 |
| PR `closed`、`labeled`；自動 tag 推送 | 不執行 | 不執行 |
| UI **Trigger Pipeline**／API 手動觸發（含手動 tag） | 執行 | 執行 |

不要以 **All pushes** 代替第一項：單純開啟 PR 並不是 push。開啟 PR 的分支推送及一般分支推送也都可能回報 `push`，不能只靠 YAML 的事件名稱分辨；分支與 PR 成員資格由 trigger 篩選，workflow 再篩選事件／tag。手動觸發不需 `run-dawn-build`；重跑既有 pipeline 保留原來的觸發事件，不等同建立新的手動 pipeline。

**GitHub OAuth pipeline**：在 **Project Setup** 選擇 **Pushes to open pull requests, default branch, and tags**，或在 **Advanced → Only build pull requests** 啟用對應設定。OAuth 的原生 GitHub trigger 主要處理 push，不能把它當成上述 GitHub App 的 PR 生命週期訂閱；需要明確訂閱 PR 建立及轉為可審查事件時，使用 GitHub App pipeline。`gh/` 組織可以同時有 OAuth 與 App pipelines，不能只憑專案 URL 判斷；避免兩者對同一事件重複建置。整合差異見[官方說明](https://circleci.com/docs/guides/integration/using-the-circleci-github-app-in-an-oauth-org/)，OAuth 設定見[開源專案指南](https://circleci.com/docs/guides/integration/oss/#only-build-pull-requests)。

GitHub App **不會觸發 fork PR**；如果需要接受外部 fork，應保留 GitHub Actions 的 `pre-merge` 作為必要閘門，不要把無法回報的 CircleCI job 狀態設為所有 PR 的必要條件。這是供應商整合限制，不表示 fork PR 已在 CircleCI 驗證。trigger 選項及事件值見 [CircleCI 官方說明](https://circleci.com/docs/github-trigger-event-options/)，GitHub merge queue 依[官方事件說明](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#merge_group)設定。

#### 逾時與遠端設定界線

GitHub Actions 的 `timeout-minutes` 是整個 job 上限。依本次選擇，CircleCI **不新增 API token**：`timed-run` 共用剩餘命令預算（桌面 30 分鐘、WGSL 15 分鐘、Dawn 360 分鐘），逾時回傳 124，必要時在 30 秒終止寬限後強制結束；初始化、checkout、產物上傳與 runner 等待不受此包裝器控制，CircleCI 方案的整個 job 上限亦可能較短。不能將兩者宣稱為相同的硬性 job deadline。命令透過環境變數傳入子 Bash，保留單引號、換行、`pipefail` 及原始失敗碼。優先使用現有 GNU timeout；沒有時改以 runner 的 Python 3 執行 `build/ci-timeout.py`，對命令 process group 先送 TERM、寬限後送 KILL，並回收直接子程序。啟動計時器可在 checkout 前執行，Python 腳本只在 checkout 後使用；不再為了 timeout 呼叫 Homebrew。

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
