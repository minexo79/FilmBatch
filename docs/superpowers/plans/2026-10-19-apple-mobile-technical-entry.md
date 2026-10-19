# Apple Mobile Technical Entry Implementation Plan

> Agent execution：使用 `reflective-implement`、`executing-plans`、`test-driven-development` 與 `reflective-minimality`。本輪是使用者要求的技術實驗，不改寫正式桌面產品；整合者在程式碼切片完成後執行所有檢查。

**Goal：** 記錄共用 C# 程式碼與技術優先、iPad 優先及 UI 延後的決定；建立真正能執行及報告失敗的共用實驗與 iOS 技術進入點。分組 commit，不 push。

**Architecture：** `tools/apple-mobile/Shared` 共用 .NET 10 診斷函式庫，直接參照既有 Core／Raw／Gpu；`Host` 是可在目前 Mac 執行的 CLI；`iOS` 是獨立診斷 app，不選定正式 UI。共用 runner 產生可區分 pass／fail／blocked／skipped 的 JSON；不把缺少 native assets 隱藏成成功。

**Tech Stack：** 現有 .NET 10、FilmBatch Core／Raw／Gpu、Apple ImageIO/CoreGraphics、ONNX Runtime 1.24.4；不新增 Python runtime、第二套產品狀態儲存或 UI framework。

**Spec：** [ADR 0004](../../adr/0004-apple-mobile-shared-core.md)。起點 `dev-ios-app@858648d`，不自行合併 `dev-ai-denoising`。

## 共用契約與檔案責任

| 位置 | 責任 |
|---|---|
| `docs/adr/0004-apple-mobile-shared-core.md`、`docs/README.md` | 決策、來源、未知與導航；先 commit 決策文件 |
| `tools/apple-mobile/Shared/FilmBatch.Mobile.Probes.csproj`、`ProbeRunner.cs` | 專案參照、具體 report/options 型別、順序執行、source-generated JSON；沒有 runtime 動態載入 plugin |
| `tools/apple-mobile/Shared/CoreRawProbe.cs` | 既有 Core 參數／復原／儲存與 Raw 寫入／讀取／發展；選用真實 RAW 的來源不變性 |
| `tools/apple-mobile/Shared/AppleImageIoProbe.cs` | 真正 Apple C API 的奇數尺寸、16-bit 像素、ICC／中繼資料 round trip；釋放 CF／CG 所有權 |
| `tools/apple-mobile/Shared/OnnxProbe.cs`、`GpuProbe.cs` | native 張量數值／取消與實際 GPU dispatch/readback；模型、provider 及不支援的原生閉包各自可見 |
| `tools/apple-mobile/Host` | `--out`、`--width/--height`、`--raw`、`--model`、`--provider`、`--no-gpu` 與 Ctrl+C；失敗／阻擋傳回非零退出碼 |
| `tools/apple-mobile/iOS` | .NET for iOS app 啟動／取消／沙箱報告；不引用桌面 App、不放入 `FilmBatch.sln` |
| `tools/apple-mobile/README.md` | 重跑命令、輸入與結果說明、iOS 實機 gate；本機證據放 ignored `dist/apple-mobile-evidence/` |
| `Directory.Packages.props` | 固定實驗需要的 ONNX Runtime 版本；不變更桌面產品的 package references |

共用 `ProbeOptions` 包含輸出目錄、尺寸、選用 RAW／ONNX 路徑、provider 及 GPU 開關。`ProbeResult` 包含名稱、狀態、說明與具名量測值；`ProbeReport` 包含 runtime／platform／architecture 與結果。任何 fail 退出 1，只有 blocked 而沒有 fail 退出 2，其餘退出 0；skipped 必須有原因。

## 執行順序與驗證

### 1. 決策文件

- [ ] 將調查中可持續使用的決策、套件版本、原生與工具鏈限制內化至 ADR，更新文件地圖。
- [ ] 驗證相對連結後，先 commit 決策與此計畫；不推送。

### 2. 共用程式與 Apple 平台實驗

- [ ] 建立 Core／RAW 自我檢查及 CLI，產生已知線性 DNG 並透過既有 API 讀回；直接驗證參數復原、專案重開及照片雜湊，不用來源字串或 mock echo 作測試。
- [ ] 建立同一份 macOS／iOS ImageIO C API 實驗：奇數列寬、16-bit PNG／TIFF、ICC、方向／中繼資料及像素正確性。
- [ ] 建立 ONNX native 真實執行實驗：小型自有測試圖使用獨立數值預期，選用實際權重時記錄 SHA-256；檢查取消後可重用，不把測試圖稱為已完成 AI 降噪。
- [ ] 建立既有 Gpu wrapper 的 Metal 運算與 readback 實驗，保留 native 錯誤；缺少 iOS native runtime 明確 blocked，不以 CPU 假裝 GPU。
- [ ] 建立沒有產品 UI 的 iOS 診斷 host 與 JSON 沙箱報告，不載入 Avalonia.Desktop／Dock／Magick.NET。

### 3. 難題驗證

```bash
dotnet run --project tools/apple-mobile/Host -c Release -- --out dist/apple-mobile-evidence/default
dotnet run --project tools/apple-mobile/Host -c Release -- --out dist/apple-mobile-evidence/stress-24mp --width 6000 --height 4000 --no-gpu
dotnet build tools/apple-mobile/iOS -c Release -r ios-arm64
dotnet test FilmBatch.sln -c Release --logger trx --results-directory dist/apple-mobile-evidence/desktop-tests
```

- [ ] 先執行實驗的明確輸入／獨立預期；發現錯誤時保留 fail-before／pass-after 的同一案例。實驗自身就是可重跑的行為檢查，不另造 wiring 測試。
- [ ] 在 Mac 執行完整 CLI、24MP 記憶體／RAW 路徑、真實 RAW／實際模型（有本機輸入時）、取消與錯誤退出路徑。
- [ ] 檢查實際固定套件的 iOS／macOS native asset 與平台識別；嘗試 iOS 建置，準確記錄 SDK／AOT／簽章阻擋。若只做 reference-assembly managed compile，必須標成 compile-only，不能計為 Release link 或裝置成功。
- [ ] 執行現有桌面回歸，不縮小既有測試、不變更像素容差或平台 skip 規則。

### 4. 結果與提交

- [ ] 將實際命令、觀察、限制及下一個可推翻假設的 gate 寫入本計畫和工具 README；檢查本輪相對連結。
- [ ] 分組 commit 可執行實驗與結果；不 push、不安裝 Xcode／workload、不動憑證／權限。正式 UI 不在本輪實作。

## 結果 ledger

| 驗收項目 | 狀態 | 證據／邊界 |
|---|---|---|
| 決策與範圍 | recorded | ADR 0004；使用者授權共用優先、iPad 優先、UI 延後，文件與實驗 commit、不 push |
| 本輪工作分支 | observed | `dev-ios-app@858648d`；AI 功能分支不在本輪自行合併 |
| Core／RAW、影像輸出、ONNX、GPU 實驗 | pending | 必須執行原生及像素消費端，不能只檢查套件或 API 存在 |
| 記憶體 | pending | 24MP／48MP 算式不是實測 RSS；Mac 的程序量測不是 iPad 上限 |
| iOS deployment | blocked prerequisite | 前置調查觀察：無已安裝 workload、無 iphoneos SDK；本輪記錄可到達的 build gate，不冒稱實機成功 |
| 桌面產品相容性 | pending | 不改桌面產品行為；仍須跑既有 suite |
| 正式 UI | intentionally deferred | 下輪根據真正 iPad 可執行性、原生閉包及量測再選框架與互動 |
| commit／push | authorized local commits only | 決策先提交、實驗後提交；禁止 push |

## Consumer map

- 共用實驗直接呼叫者：CLI 與 iOS host，必須共用同一 runner／具體 JSON 契約。
- 桌面入口與 CI：刻意不接入新專案；桌面 build/test 不要求 iOS workload。
- 既有照片／預設集／專案：只操作指定實驗輸出與唯讀輸入；不遷移使用者資料。
- 生成值與持久化：source-generated JSON，避免用 reflection-only 序列化掩蓋 AOT 問題；實際 AOT/link 仍須工具鏈。
- 文件：ADR 保存決策，README 保存操作，本計畫保存日期化驗證；未知項不得提升為支援承諾。
