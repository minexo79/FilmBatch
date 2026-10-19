# Apple 行動版技術實驗

這是 [ADR 0004](../../docs/adr/0004-apple-mobile-shared-core.md) 的共用技術進入點，**不是行動版編輯器或正式 UI 框架選擇**。共用 C# 程式碼由 Mac CLI 與獨立 iOS 診斷 app 呼叫，不複製 Core／RAW／GPU 實作，也不引用桌面 Avalonia/Dock/Magick.NET。

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

專案：`iOS/FilmBatch.Mobile.iOS.csproj`。只提供 Run／Cancel／report 的診斷畫面，正式 UI 留待下一輪。沒有 Photos 匯入、外部目錄 bookmark、產品編輯或背景執行 entitlement。

前置條件：完整 Xcode 與相容的 .NET 10 iOS workload／SDK；裝置執行需要本機合適的簽章與 provisioning。診斷 deployment target `iOS 18.0` **不是產品最低 OS 或裝置支援決定**。本輪不自動安裝 SDK、不動憑證／權限。

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

未提供設定時仍可執行自有 DNG／ONNX fixture。切到背景會取消，不假裝有無限背景運算；再次執行會建立新的取消狀態。

### 原生依賴 gate

- 固定 `Microsoft.ML.OnnxRuntime 1.24.4`：iOS app 直接 PackageReference，使用套件自己的 xcframework targets；共用函式庫的傳遞參照不能代替 app-level native link 檢查。
- 固定 `WebGPUSharp 0.5.7`：現有套件沒有 iOS Dawn native assets；預期該項 blocked，直到真正提供／驗證 iOS 原生閉包。**不把 `osx-arm64` dylib 搬進 iOS app**。
- 固定 RAW JPEG XL 原生庫：現有 repository 僅有 macOS／Windows 閉包；預設線性 DNG 不覆蓋 JXL codec。
- ImageIO/CoreGraphics 直接使用系統 framework；macOS 上相同程式的像素 round trip 仍不能代替 iOS link／裝置驗證。
- ONNX runtime 授權全文、第三方聲明與隱私說明由固定 NuGet package 複製到實驗輸出的 `native-onnxruntime/`；呼叫前明確停用 ORT telemetry。實驗不做自動模型下載。

## 結果、重跑與下一個 gate

有日期的命令／觀察／限制保存在[實驗執行記錄](../../docs/superpowers/plans/2026-10-19-apple-mobile-technical-entry.md#結果-ledger)。本機大圖、模型、報告與 compiler 證據位於 ignored `dist/apple-mobile-evidence/`，不是產品 runtime 相依。

iPad 的正式 Release link、真實 RAW／模型／GPU 消費端、24MP／48MP 端到端峰值記憶體／取消／溫控，以及 Photos／Files 權限與前景／背景生命週期，必須各自取得裝置證據。先完成這些技術 gate，再決定 UI 與裝置／OS 下限；不把目前 Mac 的成功當成 iPad 已支援。
