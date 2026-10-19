# Apple 行動版技術實驗

這是 [ADR 0004](../../docs/adr/0004-apple-mobile-shared-core.md) 的共用技術進入點，**不是行動版編輯器或正式 UI 框架選擇**。Mac CLI、獨立 UIKit iOS 診斷 app 與隔離的 Avalonia prototype 呼叫相同 C# 實驗程式碼；不複製 Core／RAW／GPU 實作，不引用桌面產品 App／Dock／Magick.NET。

主要編輯器已獨立放在 `src/FilmBatch.Mobile.iOS`／`FilmBatch.Mobile`；建置及 Files／Photos、專案、編輯與輸出方式見 [iOS 行動版](../../docs/building.md#ios-行動版)。下列診斷與六案例 prototype 驗證不替代主要編輯器的端到端驗收。

分支起點是 `dev-ios-app@858648d`。既有完整 AI 降噪功能在 `dev-ai-denoising`；本輪不自行合併、不另造降噪服務。此處的 ONNX 檢查是 native runtime／張量及選用權重的執行實驗，不能當成降噪品質或完整產品管線的驗收。

## 在目前的 .NET 主機執行

從儲存庫根目錄執行；`dotnet` 必須符合 `global.json`。

```bash
dotnet run --project tools/apple-mobile/Host -c Release -- \
  --out dist/apple-mobile-evidence/default

# 大圖：真正產生／讀回線性 DNG，非只計算緩衝區大小。
dotnet run --project tools/apple-mobile/Host -c Release -- \
  --out dist/apple-mobile-evidence/stress-24mp \
  --width 6000 --height 4000 --no-gpu

# 真實相機 RAW：輸入唯讀；輸出目錄必須與 RAW／模型輸入分開。
dotnet run --project tools/apple-mobile/Host -c Release -- \
  --out dist/apple-mobile-evidence/camera \
  --raw /absolute/path/to/photo.CR3

# 使用已存在的真實 ONNX 權重；不下載、不寫入使用者模型目錄。
dotnet run --project tools/apple-mobile/Host -c Release -- \
  --out dist/apple-mobile-evidence/model \
  --provider CoreML --model /absolute/path/to/rawnind-linear.onnx
```

- `--provider CPU` 為預設值；`CoreML` 是明確要求，不在非 Apple 平台偷偷改用 CPU。
- `--no-gpu` 明確略過 GPU；報告標成 skipped，不宣稱 GPU 相容。
- `Ctrl+C` 取消目前工作；原生呼叫能否即時中斷取決於對應 API，呼叫完成後仍須檢查取消，不能接受過期結果。
- 未指定 RAW 時產生的 DNG 是線性 RGB fixture，**不是 Bayer 相機資料**。
- 未指定模型時使用自有 129-byte ONNX 圖：FP32 `[1,3,4,4]`、`y=-x`，涵蓋負值、零及大於 1 的值；不是假降噪模型，也不是已完成降噪的替代證據。
- 選用模型的目前契約是固定形狀 FP32 `[1,3,H,W]`、單一 input／output、相同輸出尺寸。SHA-256、張量尺寸、有限值範圍與耗時會記錄；不作 tile 重建、RAW 全域 gain 修正或品質評分。

每次輸出 `<out>/report.json`。`fail` 退出 1；只有 `blocked` 而沒有 fail 退出 2；其餘退出 0。`blocked`／`skipped` 都不是該項通過。JSON 使用 source generation，避免為診斷報告另增 reflection-only AOT 相依。

可重跑的來源安全／邊界回歸（先建置 Host）：

```bash
dotnet build tools/apple-mobile/Host -c Release
dotnet run tools/apple-mobile/VerifyBoundaries.cs
```

在自己的暫存目錄檢查直接輸入／輸出重疊、輸入 symlink、輸出目錄 symlink、同目錄重跑及 `1×1`／`1×7`／`7×1`；不刪除使用者選的輸出目錄。Unix 執行 symlink 案例，其他平台明確略過這兩項，不要求 Windows 額外權限。路徑檢查解析連結目標的祖先（包含 macOS `/var`→`/private/var`），在產生 fixture 前拒絕來源重疊。

## 實驗實際檢查什麼

| 實驗 | 有效證據 | 不代表什麼 |
|---|---|---|
| Core／RAW | 直接使用現有參數、復原／專案儲存、RAW 寫入／解碼／發展；選用輸入的前後 SHA-256 | 不代表每台相機與 JXL 變體都已在 iOS 可用 |
| ImageIO | 同一份 Apple C API C# 程式實際編碼／解碼 16-bit PNG／TIFF，檢查奇數尺寸、像素、ICC、方向／中繼資料 | 不代表已取代 Magick.NET，或已驗證 HEIC／JPEG／P3／EDR |
| ONNX | native 數值、取消結果丟棄及 session 重用；選用真實權重時執行實際模型 | `CoreML + CPU` 不宣稱每個 node 都在 GPU／ANE；簡單圖不能證明整張網路 placement |
| GPU | 既有 `FilmBatch.Gpu` wrapper，257 個整數的 dispatch／copy／map readback，全值比對與超額 dispatch 邊界 | 不代表完整攝影 shader／預覽／匯出管線，也不代表 Mac native library 可用於 iOS |
| 記憶體 | `CallingThreadAllocatedBytes` 是呼叫執行緒的配置量、不含其他執行緒；working set 是程序當下值；`time -l` 的 max RSS／peak footprint 另記於執行記錄 | 不是 iPad 峰值 RSS、Jetsam 上限、溫控或完整 RAW＋AI＋GPU 工作集 |

## iOS 診斷 app

專案：`iOS/FilmBatch.Mobile.iOS.csproj`。提供 Run／Cancel／report 的診斷畫面；另有下方的 Avalonia prototype，但兩者都不是正式編輯器。沒有 Photos 匯入、外部目錄 bookmark、產品編輯或背景執行 entitlement。

前置條件：完整 Xcode 與**版本相容**的 .NET 10 iOS workload／SDK；裝置執行另需本機簽章與 provisioning。診斷 deployment target `iOS 18.0` **不是產品最低 OS 或裝置支援決定**。工具鏈安裝依本輪授權執行，不處理憑證秘密或變更全機 Xcode selection。

```bash
# 編譯／連結 gate；無 workload／SDK 時應明確失敗，不視為通過。
dotnet build tools/apple-mobile/iOS -c Release -r ios-arm64

# 有模擬器工具鏈時，透過 .NET iOS Run target 啟動；不是裝置 Release 證據。
dotnet build tools/apple-mobile/iOS -t:Run -c Debug -r iossimulator-arm64
```

診斷 app 從自己的 `Documents/probe-options.json` 讀取選用參數；可透過 Files／檔案共享放入 fixture 或 ONNX。相對輸入路徑以 Documents 為基準，輸出強制為 `Documents/results/`，避免設定覆寫其他路徑。例：

```json
{
  "OutputDirectory": "results",
  "Width": 768,
  "Height": 512,
  "RawPath": "photo.dng",
  "ModelPath": "rawnind-linear.onnx",
  "Provider": "CoreML",
  "IncludeGpu": true
}
```

未提供設定時可執行自有 DNG／ONNX fixture；錯誤設定不能靜默換成預設工作。切到背景會取消，等舊工作結束後才能重跑；取消與 scene disconnect 不接受舊報告。`pending-run.json` 是一次性 autorun 請求，完成讀取後移除，避免下次啟動誤重播。

### 原生依賴 gate

- 固定 `Microsoft.ML.OnnxRuntime 1.25.0`：修正 [1.24.4 的 iOS managed asset 缺失](https://github.com/microsoft/onnxruntime/issues/27868)。iOS app 直接 PackageReference，使用套件自己的 xcframework targets；共用函式庫的傳遞參照不能代替 app-level native link 檢查。升級後必須重新連結原生執行檔；只更新 managed DLL 或解開新 archive 仍可能混用舊 native runtime。
- 固定 `WebGPUSharp 0.5.7`：現有套件沒有 iOS Dawn native assets；預期該項 blocked，直到真正提供／驗證 iOS 原生閉包。**不把 `osx-arm64` dylib 搬進 iOS app**。
- 固定 RAW JPEG XL 原生庫：現有 repository 僅有 macOS／Windows 閉包；預設線性 DNG 不覆蓋 JXL codec。
- ImageIO/CoreGraphics 直接使用系統 framework；macOS 上相同程式的像素 round trip 仍不能代替 iOS link／裝置驗證。
- ONNX runtime 授權全文、第三方聲明與隱私說明由固定 NuGet package 複製到實驗輸出的 `native-onnxruntime/`；呼叫前明確停用 ORT telemetry。實驗不做自動模型下載。

## 結果、重跑與下一個 gate

有日期的命令／觀察／限制保存在[實驗執行記錄](../../docs/superpowers/plans/2026-10-19-apple-mobile-technical-entry.md#結果-ledger)。本機大圖、模型、報告與 compiler 證據位於 ignored `dist/apple-mobile-evidence/`，不是產品 runtime 相依。

iPad 的正式 Release link、真實 RAW／模型／GPU 消費端、24MP／48MP 端到端峰值記憶體／取消／溫控，以及 Photos／Files 權限與前景／背景生命週期，必須各自取得裝置證據。先完成這些技術 gate，再決定 UI 與裝置／OS 下限；不把目前 Mac 的成功當成 iPad 已支援。

## Avalonia prototype 與官方 Simulator

`Prototype/Common/` 提供相同 App／view，`Prototype/Desktop/` 供本機觀察，`Prototype/iOS/` 是 scene-based iOS 入口。固定 Avalonia 12.1.4。畫面有 adaptive 版面、Run／Cancel、報告及真正 ImageIO PNG 預覽，不是完整照片編輯器。

```bash
# Mac 上觀察相同 view；不算 iOS／Simulator 證據。
dotnet run --project tools/apple-mobile/Prototype/Desktop -c Release -- \
  --out dist/apple-mobile-simulator-evidence/interactive-mac \
  --window 390x844

# 不需 iOS runtime：報告來源、run tag、退出碼及取消 epoch 的行為回歸。
dotnet run --project tools/apple-mobile/Simulator -c Release -- verify
```

UIKit identifiers：`Probe.Run`、`Probe.Cancel`、`Probe.Status`、`Probe.Report`。Prototype identifiers：`Prototype.Run`、`Prototype.Cancel`、`Prototype.Status`、`Prototype.Report`、`Prototype.Preview`。兩個 iOS host 都接受真正的 workload launch arguments：`-probe-width`、`-probe-height`、`-probe-raw`、`-probe-model`、`-probe-provider`、`-probe-no-gpu`；XCTest 使用 48MP 工作建立取消視窗，產品來源沒有人工延遲。背景案例必須確認 app 真正進入 background，不能只送出 Home 事件就宣稱生命週期已驗證。

### 可見的 Simulator 視窗

本輪 Xcode 27 的 GUI 入口是 [Device Hub](https://developer.apple.com/documentation/xcode/device-hub)，不是舊的 `Simulator.app`。`simctl bootstatus` 成功只證明模擬裝置已啟動，不代表螢幕視窗已開啟：

```bash
open "/Applications/Xcode.app/Contents/Applications/DeviceHub.app"
```

在 Device Hub 選取這次建立的裝置，再點選 **View Screen**；可用 **Switch to compact window** 將裝置畫面獨立顯示。XCTest 會自行啟動測試 app；手動觀察時可從裝置主畫面開啟 FilmBatch Mobile Prototype。這條路不使用 System Events，也不更改隱私權限。操作說明見 [Apple 官方文件](https://developer.apple.com/documentation/xcode/interacting-with-your-app-in-device-hub)。

Duo 有多個顯示面，`simctl io <udid> screenshot` 須指定 `--display=primary`，避免取得未使用的 integrated display。背景測試直接啟用 `com.apple.springboard` 後，仍須等待被測 app 進入 `runningBackground` 或 `runningBackgroundSuspended`，才驗證取消、空報告及前景重跑；送出 Home 按鍵／手勢本身不算通過。


### 工具鏈與 Xcode 27.1 實驗

原本固定的 .NET iOS SDK `26.0.11017` 要求 Xcode 26.0；後續使用者明確改選 **Xcode 27.1／iOS 27.1**。原 SDK 與 repository `global.json` 保持不變，另建隔離 SDK，安裝官方 workload set `10.0.401.1`，取得 iOS SDK `27.0.10722`。

這仍是**非官方支援組合**：[官方 release](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode27.0-10722) 要求 Xcode 27.0，strict build 實際拒絕了 27.1，尚無已發佈的 `Microsoft.iOS.Sdk.net10.0_27.1` 套件。僅本次明確的實驗 build 命令使用官方記載的 [`ValidateXcodeVersion=false`](https://learn.microsoft.com/en-us/dotnet/ios/building-apps/build-properties#validatexcodeversion)，不寫進專案、不修改 SDK、不關閉編譯或裁剪錯誤。成功也不等於正式 SDK 相容性或實機支援。

已安裝的官方 27.1 runtime 只列出 `iPhone Duo` 為相容 device type；嘗試建立 iPad A16 實際被拒絕。此輪以自己的 Duo Simulator 測試，不能宣稱已驗證 iPad。實體裝置測試仍由使用者延後。

以下為 macOS 上的重跑方式；`$work` 是本次新建目錄，`$out` 應為尚未使用的證據目錄。先確認原 SDK 位置與官方 runtime 已存在：

```bash
work="$(mktemp -d "${TMPDIR:-/tmp}/filmbatch-ios27.XXXXXX")"
cp -cR "$HOME/.dotnet" "$work/dotnet"
export DOTNET_ROOT="$work/dotnet"
export DOTNET_HOST_PATH="$DOTNET_ROOT/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DEVELOPER_DIR="/Applications/Xcode.app/Contents/Developer"
dotnet workload install ios --version 10.0.401.1 --disable-parallel
xcodebuild -version
xcrun simctl list runtimes
out="dist/apple-mobile-ios27-evidence/rerun-$(date +%Y%m%d-%H%M%S)"
udid="$(xcrun simctl create "filmbatch-ios27-$(date +%s)" \
  com.apple.CoreSimulator.SimDeviceType.iPhone-Duo \
  com.apple.CoreSimulator.SimRuntime.iOS-27-1)"
xcrun simctl boot "$udid"
xcrun simctl bootstatus "$udid" -b

# 僅這兩個命令略過已知的版本拒絕；其餘 build／link 錯誤必須處理。
dotnet build tools/apple-mobile/iOS/FilmBatch.Mobile.iOS.csproj \
  -t:Rebuild -c Release -r iossimulator-arm64 -p:ValidateXcodeVersion=false
dotnet build tools/apple-mobile/Prototype/iOS/FilmBatch.Mobile.Prototype.iOS.csproj \
  -t:Rebuild -c Release -r iossimulator-arm64 -p:ValidateXcodeVersion=false

# 指定剛才實際建置的 app，避免 runner 隱含使用另一套 SDK 重新建置。
dotnet run --project tools/apple-mobile/Simulator -c Release -- \
  run --target native --udid "$udid" --timeout 900 \
  --app "$PWD/tools/apple-mobile/iOS/bin/Release/net10.0-ios/iossimulator-arm64/FilmBatch.Mobile.iOS.app" \
  --out "$out/native"
dotnet run --project tools/apple-mobile/Simulator -c Release -- \
  run --target prototype --udid "$udid" --timeout 900 \
  --app "$PWD/tools/apple-mobile/Prototype/iOS/bin/Release/net10.0-ios/iossimulator-arm64/FilmBatch.Mobile.Prototype.iOS.app" \
  --out "$out/prototype"
dotnet run --project tools/apple-mobile/Simulator -c Release -- \
  uitest --udid "$udid" --timeout 900 \
  --class FilmBatchProbeUiTests/ProbeUiTests --out "$out/native-ui"
dotnet run --project tools/apple-mobile/Simulator -c Release -- \
  uitest --udid "$udid" --timeout 900 \
  --class FilmBatchProbeUiTests/PrototypeUiTests --out "$out/prototype-ui"
```

使用後只 shutdown／delete 自己剛建立的 `$udid`，不清除其他 Simulator；隔離 SDK 可在不再重跑時移除，不刪除原 SDK。已存在的 Simulator 可用 `--udid` 指定，runner 不會刪除。`run --target prototype` 只執行完成／報告／預覽案例；完整取消與背景／前景案例由 `uitest` 執行。實際成敗以[執行計畫的最新結果](../../docs/superpowers/plans/2026-10-19-ios-simulator-prototype.md)為準，不由這些重跑命令推定。

Simulator runner 退出碼：`0`＝四項皆 pass；`1`＝probe fail；`2`＝有 blocked／skipped；`3`＝工具、建置、啟動、逾時或證據失效。缺少 iOS Dawn 預期會保留 blocked，不能宣稱全通過。

`report.json` 的 `OperatingSystem` 由 app 的 OS API 產生；`Platform` 保留原始 OS description，`RuntimeIdentifier` 另記 runtime。Darwin 不是 iOS／macOS 判別依據。`validate-report <report.json> <tag>` 必須同時有同目錄的 matching `run-tag.txt`；Simulator 來源仍須由實際 simctl install／launch、build RID 與 receipt 證明，JSON 自身不是防偽證明。

本輪驗收與未完成 gate 見 [Simulator／prototype 計畫](../../docs/superpowers/plans/2026-10-19-ios-simulator-prototype.md#已實測結果2026-10-19)。Xcode 27.1／iOS 27.1 的命令、原生報告、真正畫面、xcresult 與版本／授權雜湊保留在 ignored `dist/apple-mobile-ios27-evidence/`；先前的 Mac 畫面、managed compile 與原始失敗保留在 `dist/apple-mobile-simulator-evidence/`。兩者都不是產品 runtime 相依；Mac 證據不取代 iOS 驗證。
