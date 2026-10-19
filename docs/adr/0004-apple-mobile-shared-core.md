# ADR 0004：Apple 行動版以共用程式碼與技術驗證優先

- 日期：2026-10-19
- 狀態：已採納開發方向；正式 UI 尚未選定，iPad／iPhone 支援尚未驗證。
- 授權：先記錄共用技術決策並分組提交實驗；後續補齊工具鏈、開發隔離 Avalonia prototype 與 pure iOS／官方 Simulator 測試。Simulator 測試實際成功後才獨立 commit；六案例全通過後，使用者明確授權本次 commit／push 並繼續共用主要邏輯的 iOS 介面與功能移植。實機驗證仍延後。
- 執行記錄：[初始技術實驗](../superpowers/plans/2026-10-19-apple-mobile-technical-entry.md)、[Simulator／prototype 與目前工具鏈 gate](../superpowers/plans/2026-10-19-ios-simulator-prototype.md)。

## 決定

保留 C# 的 `FilmBatch.Core`、`FilmBatch.Raw` 及可驗證的算圖／參數契約，不為行動 UI 先重寫攝影演算法。新增獨立的 .NET for iOS **診斷 host** 與桌面命令列 host，執行同一份共用實驗程式碼；不引用桌面的 Avalonia/Dock 啟動組合，也不先選 Avalonia iOS、MAUI 或 SwiftUI 為正式 UI。

診斷 host 只負責啟動、取消及寫出報告，不是產品介面設計。實驗放在 `tools/apple-mobile/`，不加入桌面 `FilmBatch.sln`，不讓既有桌面 CI 突然需要 iOS SDK。優先直接 `ProjectReference` 既有元件，不複製 RAW、預設集、復原或 GPU 實作。NativeAOT 不作為 C#／Swift 靜態函式庫已可交付的假設。

本分支 `dev-ios-app` 起點為 `858648d`；已完成的 AI 降噪功能在獨立的 `dev-ai-denoising` 分支。本輪不自行合併該分支、不另造一套產品降噪服務。ONNX 實驗驗證原生 runtime／張量／模型載入，不代表已接入完整降噪流程或達到攝影品質標準。

## 為什麼不直接換 UI

| 已觀察的限制 | 對本輪的影響 |
|---|---|
| 桌面 host 僅處理 `IClassicDesktopStyleApplicationLifetime`，主視窗有 Dock、1280×800 最小尺寸及桌面互動假設 | 需要新的行動啟動點；不能把現有主視窗直接當成 iPad UX |
| `WebGPUSharp 0.5.7`、`Magick.NET-Q16-HDRI-AnyCPU 14.17.2` 的固定 NuGet 套件沒有 iOS native assets | UI 能以 Metal 繪製，不代表 FilmBatch 的運算或解碼原生庫已可載入 |
| `Microsoft.ML.OnnxRuntime 1.25.0` 有 iOS xcframework／MSBuild 原生整合；1.24.4 的 iOS managed asset 缺失已實際造成缺庫 | 升級並重新連結後，iOS 27.1 Simulator 的原生數值、取消結果丟棄與 session 重用通過；CoreML placement 及實機張量仍須另驗證 |
| 48MP 的單份 FP32 RGB 為 549.3 MiB，三份約 1.61 GiB，另有 RGBA16 約 366.2 MiB | 這只是緩衝區算式，不是峰值 RSS；換 SwiftUI 不會自動消除整張影像與推論 activation 的成本 |
| 初次只有 CommandLineTools；後續已安裝 Xcode 27.1、官方 runtimes，另隔離安裝 iOS SDK 27.0.10722 | 使用者改選直接測試 27.1；官方 SDK 仍要求 Xcode 27.0，僅明確實驗命令使用版本驗證開關，不修改 SDK 或專案預設；實際成功與限制另記 |

## 本輪的驗證邊界

1. **Core／RAW**：直接執行既有參數、復原、儲存及 RAW 解碼／發展 API；保留來源檔案雜湊與影像尺寸／有限值等不變量。產生的線性 DNG 清楚標示為 fixture，不冒稱相機 Bayer RAW。
2. **Apple image IO**：同一份 C# P/Invoke 在 macOS 與 iOS host 使用 ImageIO/CoreGraphics，實測 16-bit、奇數列寬、ICC 與中繼資料 round trip。它是候選替代路徑，並非已取代 Magick.NET 或證實全格式等價。
3. **ONNX**：固定 `1.25.0`，以有獨立數值預期的測試圖驗證真正 native 執行與取消；可另指定實際 ONNX 權重。只有實際模型執行才能支持模型可用的說法；簡單測試圖不能證明 CoreML 已承接整張網路。升級時須重建原生執行檔，避免 managed／native 混用舊版本。
4. **GPU／原生套件**：透過既有 `FilmBatch.Gpu` 建立裝置、送出運算並讀回數值；缺少 iOS 原生庫必須明確阻擋，而非回退 CPU 後宣稱 GPU 成功。
5. **記憶體與建置**：報告實際輸入、緩衝區大小、耗時與可取得的程序／受控記憶體資訊；分開標示 managed compile、AOT/link、模擬器及實機證據。大圖實驗不等於量測到 iPad 記憶體上限。

報告區分 `pass`、`fail`、`blocked`、`skipped`；缺 SDK、缺原生庫或未執行實機，不得計為通過。所有實驗只寫指定輸出目錄，不修改原始照片、既有使用者設定或模型。

隔離 Avalonia prototype 延伸本決定，但不是正式 UI 定案：重用同一 `ProbeRunner`，不引用桌面產品 App／Dock。兩個 iOS Release app 已在 Xcode 27.1 實驗工具鏈建置成功，UIKit 與 Avalonia 均已於官方 iOS 27.1 Simulator 執行，產生 Core／RAW、RGB16 ImageIO、ONNX 的真實成功報告；GPU 保留缺少 iOS Dawn 的阻擋。後續開啟 Xcode 27 的 Device Hub，排除只有 CoreSimulator 而沒有可見視窗的問題；將背景驅動改成直接啟用 SpringBoard 後，完整六個 UI 案例 6 pass／0 fail／0 skip／0 expected failures，包含兩個 app 的真正背景取消與前景重跑、手動取消／重跑及 Avalonia 真實預覽。Simulator 測試依授權獨立提交 `d3a21b9`；使用者隨後授權推送並接續主要功能移植。[本輪結果](../superpowers/plans/2026-10-19-ios-simulator-prototype.md#已實測結果2026-10-19)保留先前失敗與修正後證據；Simulator UI 成功不推定正式 SDK 相容性、GPU 或實機支援。

## 下一輪才決定的事項

- 正式 UI 框架、操作流程、觸控／Apple Pencil、iPhone 是否具備同等批次管理功能。
- 支援的 iPad／iPhone 與最低 OS；診斷 host 的 deployment target 不是產品支援承諾。
- Photos 匯入、security-scoped Files／bookmark／File Provider、P3／EDR、背景工作與強制結束的產品契約。
- Dawn iOS 自建閉包或獨立 Metal backend 的選擇；必須先有實機運算／精度／記憶體證據。
- 合併既有 AI 功能分支後的完整來源級降噪、HDR／alpha／tile seam、取消／重用與真實相機品質驗收。

## 可推翻本決定的證據

若正式 iPad Release 建置無法保留必要 C#／原生套件，或實機大圖 RAW／AI／GPU 操作無法符合記憶體、取消及正確性要求，重新比較原生 backend 或演算法改寫。不得因診斷 host 能啟動，就提前排除這些失敗條件。

## 調查來源與精確限制

前置調查日期為 2026-10-19；來源是當時的 FilmBatch 原始碼、固定套件內容、`vtool`／CLI／工具鏈檢查及以下官方文件。這份 ADR 內化決策與邊界，不依賴暫存對話或本機 survey 檔案。

- [Xamarin 支援政策](https://dotnet.microsoft.com/en-us/platform/support/policy/xamarin)：支援已於 2024-05-01 結束；現代 C# iOS 路徑是 .NET for iOS，不強制使用 MAUI。
- [Avalonia 支援平台](https://docs.avaloniaui.net/docs/supported-platforms)、[iOS host](https://docs.avaloniaui.net/docs/platform-specific-guides/ios)：UI 平台支援不等於 FilmBatch 原生運算套件支援。
- [.NET Apple 平台 NativeAOT](https://learn.microsoft.com/en-us/dotnet/maui/deployment/nativeaot?view=net-maui-10.0)、[NativeAOT 函式庫](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)：預設 Mono 與可選 NativeAOT 不同；不能將 iOS app AOT 推論為已支援 Swift 可連結的 C# 靜態函式庫。
- [ONNX Runtime CoreML EP](https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html)、[Apple Objective-C／Swift 介面](https://onnxruntime.ai/docs/get-started/with-obj-c.html)：模型格式、provider placement 與平台建置都須個別驗證。
- [Apple ImageIO](https://developer.apple.com/documentation/imageio)、[CIRAWFilter](https://developer.apple.com/documentation/coreimage/cirawfilter)：ImageIO 是輸出／輸入候選；Apple RAW developer 不保證與現有 RAW 輸出相同。
- [Files 目錄存取](https://developer.apple.com/documentation/uikit/providing-access-to-directories)、[持續背景工作](https://developer.apple.com/documentation/backgroundtasks/performing-long-running-tasks-on-ios-and-ipados)：路徑不是持久授權，背景工作也不是永不終止的桌面程序。
