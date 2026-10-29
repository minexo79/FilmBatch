# 測試與 CI

測試分類、回歸與合併前 CI。建置與執行見 [building.md](building.md)；完整驗證順序與失敗分類見 [VERIFY.md](../VERIFY.md)。

## 測試

```bash
dotnet test FilmBatch.sln -c Release
```

UI 測試以 Avalonia.Headless 執行，不需要顯示器。多數 GPU 回歸在可用軟體介面卡上也會執行 WebGPU；但 `ToneCurvePipelineTests` 會略過 `IsSoftware` 的 GPU 案例，另有 Windows-only 條件，須逐項查看 executed／skipped，不能從整套 exit 0 推定所有 GPU 路徑已測。真正進入算圖、讀回後的錯誤或像素比對失敗，不能改判成「沒有 GPU」略過。

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
  --filter "FullyQualifiedName~EditorLifecycleTests|FullyQualifiedName~WindowLifecycleTests|FullyQualifiedName~PreviewInteractionTests|FullyQualifiedName~MobileEditorViewTests"
```

涵蓋裁切取消／延後進入、跨專案與同資料夾重開、部分匯出失敗後重試、拖曳中斷與復原區間、分割線優先權（含拉直旋轉後的命中與拖曳）、面板／全螢幕狀態及共用平移。資料夾切換測試使用隔離的照片副本，並檢查新專案可繼續編輯與儲存。行動 View 另驗證自動重開及未選取時清除暫時狀態，並保留匯入、失敗與背景取消結果。

Headless 不會執行 AppKit 的 Spaces 動畫。Mac 桌面驗證須觀察實際 Cocoa 全螢幕狀態並等待動畫結束後再操作下一步：[Avalonia 的原生實作](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/native/Avalonia.Native/src/OSX/WindowImpl.mm#L458-L546)也會同步回報要求狀態，因此 `WindowStateChanged` 不能當成動畫完成通知。另確認 Cancel 後畫布無裁切工具、解碼前 Esc 不會稍後重新啟用、文字欄位內 Esc 能關閉預設管理，以及切換專案前後原始照片位元組不變。實體綠色按鈕、動畫期間快速連按切換與 Windows／Linux 原生視窗操作不屬於 headless 的驗證證據。

### RAW 介面回歸

```bash
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~RawInspectorTests|FullyQualifiedName~Thumbnails_WaitForCurrentRawSource|FullyQualifiedName~RawControlsTests|FullyQualifiedName~RawSourceLifecycleTests"
```

涵蓋 RAW／一般影像切換、隱藏未啟用模組、保留已保存的 RAW 設定、預設展開基本調整、來源選項切換後縮圖自動更新，以及 Kelvin／遮罩設定的復原、儲存、重開、過期解碼失敗與修復後快取重新解碼。永久測試使用隔離的 DNG／一般影像與實際 CPU 管線，不依賴外部相機語料。自訂 Kelvin 範圍為 2500–9900 K；舊 JSON／YAML 值正規化，但拍攝白平衡中繼資料保持原樣。

Mac 桌面另以含 gain-table 的 ProRAW 及含畸變／暗角資料的 RAF 驗證：在底片分頁復原／重做 RAW 選項，即使重解碼時間超過縮圖防抖間隔，底片縮圖仍自動跟上新來源，結果須等於同來源重新要求算圖的結果；切到 PNG 不顯示 RAW 控制項，再切回 RAW 恢復顯示且設定不變。驗證前後核對原始 RAW 的 SHA-256。
以上原生 RAW 選項量測記錄的是當時的來源契約，含當時仍存在的色相縮圖；獨立 HSL 已移除該縮圖路徑。長邊 ≤240 的 RAW 縮圖在未選外部 DCP 時先用內嵌預覽；選用 DCP 時改走所選 profile 的完整顯影，不能把新選擇貼在舊 JPEG 上。選取後的大圖與匯出不使用內嵌縮圖。

### CLF 與相機 profile 回歸

```bash
dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~ClfFileTests|FullyQualifiedName~ColorTransform|FullyQualifiedName~CameraProfile|FullyQualifiedName~DcpSourceTests|FullyQualifiedName~DcpToneTests|FullyQualifiedName~PhotoAnalysisTests'
dotnet test tests/FilmBatch.Raw.Tests -c Release --filter 'FullyQualifiedName~DcpProfileTests|FullyQualifiedName~GpuColourTests|FullyQualifiedName~GainTableMapTests|FullyQualifiedName~CameraColorTests|FullyQualifiedName~ProfileTests'
dotnet test tests/FilmBatch.App.Tests -c Release --filter 'FullyQualifiedName~FilmLookViewModelTests|FullyQualifiedName~RawControlsTests|FullyQualifiedName~RawSourceLifecycleTests|FullyQualifiedName~PhotoToolsTests|FullyQualifiedName~MobileWorkspaceTests|FullyQualifiedName~MobileDialogServiceTests|FullyQualifiedName~LocalizationTests'
dotnet run build/CheckShaders.cs -- --naga /path/to/naga
```

測試覆蓋 parser／資產限制、相機機型與 policy、來源 gain-map 四層優先序及損壞回退、非線性雙光源表的候選與實際重顯影、CPU／GPU 數值、匯入與 undo／取消。合成 DNG 搭配外部 DCP 驗證來源 v2 gain map 與 HueSat 同時作用；雙表及曝光順序另用線性／sRGB 編碼、四個 GPU 分帶與預算回退核對獨立數值。須記錄各套件實際選取數與 skip；headless、合成 DNG、Metal 或 desktop mobile host 不可冒充 Windows DX12、合法實拍 RAW、iOS 原生操作或發佈驗收。完整 gates 見[實作計畫](superpowers/plans/2026-10-28-composable-color-transforms.md#4-verification-commands--release-gates)。

### 色彩區塊：HSL 與曲線

```bash
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~HslTests|FullyQualifiedName~EffectPipelineTests|FullyQualifiedName~ToneCurve"
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~HslViewModelTests|FullyQualifiedName~CurveEditorTests|FullyQualifiedName~FilmLookViewModelTests|FullyQualifiedName~PhotoToolsTests|FullyQualifiedName~ColorSliderTests"
```

HSL 須在關閉底片、底片強度為零時仍改變指定色相；自身關閉／全零恆等、alpha 保留。JSON／YAML 的舊 `film.hue_*` 只在讀入時遷移，新 key 優先且不依輸入順序，不為稀疏層補開關，型別／範圍／未知 key 仍拒絕。共用色彩編輯器確認文字預設、多選／一次復原、滑桿覆寫、自訂標記、曲線與取樣排除；HSL 不再排縮圖，底片縮圖保留。

原生桌面另須操作實際控制項、切換中英文與側邊分頁、保存重開，並經正式匯出命令產生 PNG16，以獨立解碼器核對位深／尺寸／alpha 與 CPU 參考。Headless 或共用介面編譯不代表 Windows／iOS 原生驗收。

### 顆粒、白平衡／膚色與 Zone

```bash
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~GrainModelTests|FullyQualifiedName~SurveyRenderingTests|FullyQualifiedName~PhotoAnalysisTests|FullyQualifiedName~PhotoAdjustmentTests"
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~PhotoToolsTests"
```

涵蓋原始參考座標、非等比例取整代理圖的旋轉、次像素顆粒衰減、局部膚色／Zone、遮罩後的白平衡正向試算，以及提案、取消、復原與儲存邊界。GPU 數值比對仍須實際 GPU；沒有 GPU 的略過不能當成該路徑通過。

桌面另須展開「照片工具」，以點選／拖曳操作中性區、空間候選與參考色，確認預覽不寫入，接受後一次復原還原全部欄位；再檢查膚色參考重複套用、最近一次 Zone 的 III／V／VIII 放置、雙錨點重複擬合不漂移。旋轉取樣的實際來源外接矩形須與畫布投影相符。顆粒三種模型、seed 與進階品質／明暗響應需在預覽和重開後均可見；操作與限制見[現行功能](research/grain-white-balance-survey.md#目前實作與操作)。

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

資源使用率顯示與背景 CPU 配速共用程序時鐘：macOS／iOS 使用 Darwin `CLOCK_PROCESS_CPUTIME_ID`，其他平台使用 `Process.TotalProcessorTime`。iOS 的 .NET API 不支援後者，不能因為桌面可用就直接沿用。平台檢查以 iOS 目標重新編譯並將 `CA1416` 視為錯誤；通過編譯不代表模擬器或實機已完成資源取樣。實際介面須確認關閉除錯記錄後仍可開啟資源顯示，CPU／RAM／磁碟不因取樣例外全數停留在「—」。

### RAW GPU 與載入隔離回歸

```bash
dotnet test tests/FilmBatch.Raw.Tests/FilmBatch.Raw.Tests.csproj -c Release \
  --filter "FullyQualifiedName~GpuAhdTests|FullyQualifiedName~GpuColourTests|FullyQualifiedName~RawGpuPolicyTests|FullyQualifiedName~RawEmbeddedThumbnailTests"
dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release \
  --filter "FullyQualifiedName~CacheAdmissionTests|FullyQualifiedName~DecodeBudgetTests|FullyQualifiedName~OffThreadScalingTests|FullyQualifiedName~PreviewCanvasWindowTests"
```

GPU 像素測試先檢查硬體；沒有硬體或裝置限制使案例無法執行，明確記為 skipped，不以 `return` 冒充通過。預檢成功後的 shader／讀回失敗必須使測試失敗。AHD、色彩與政策案例共用不平行的集合，執行內容內的 hook 在離開範圍時還原，不改全域測試狀態。

數值門檻沿用既有值，沒有因合併放寬；它們是容差檢查，不是跨 GPU 廠牌逐位元一致的 oracle：

| 案例 | 每個樣本的差異判定 | 可超標樣本 |
|---|---|---|
| AHD | `abs(cpu - gpu) > 1e-4 × max(1e-3, abs(cpu))` | 少於 0.1% |
| 色彩收尾 | `abs(cpu - gpu) > 1e-3 × max(1e-2, abs(cpu))` | 少於 0.1% |

額外的 `GpuAhdTests.Gpu_ahd_near_ties_do_not_amplify_math_roundoff` 使用精確 12-bit 合成馬賽克、RGGB／GBRG 與 2049×2049 尺寸，檢查 Lab 近等距不會把捨入差放大成另一組 RGB 候選；最大絕對差上限為預先固定的 `2e-6`。尺寸同時跨越 CPU 128-pixel tile、GPU 256-row band 與最後一列／行；兩案例先在舊 shader 失敗，再於修正後通過。這是特定回歸的不變量，不替代上表的既有一般容差或跨硬體畫質驗收。全尺寸與獨立立方根控制見[實測 §13](evidence/performance-local-2026-10-26.md#13-raw-ahd-捨入與方向選擇修復)。

`FILMBATCH_NO_GPU_DEMOSAIC=1` 須在啟動前設定，會同時停用兩個 RAW GPU 階段；它不會停用效果算圖器。App 的 RAW GPU 政策遵守 `ForceSoftwareGpu`、指定後端及 `GpuMemoryLimitMb`，且 iOS 不啟用。直接 `ImageLoader.LoadFull`、CLI 與 `BatchExporter` 的 RAW 解碼固定走 CPU；驗證 V4 時仍須分別確認效果算圖真的使用硬體／CPU，不能只看啟動成功。

原生 smoke 另檢查載入途中切換照片、100%／177%／300% 縮放與平移、取消背景後換資料夾，以及軟體模式切換後恢復 Metal。每個縮放步驟須等新畫框級距的窗口送達，不能把上一級仍可見的窗口當成完成。CPU／GPU 匯出比較使用相同 CPU RAW 來源；另以啟用中的預覽範圍呼叫 `BatchExporter`，檢查送入效果算圖的浮點場景 hash 仍與未啟用 GPU 的直接載入一致。

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
