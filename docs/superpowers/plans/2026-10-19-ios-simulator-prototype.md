# iOS Simulator and Avalonia Prototype Implementation Plan

> 執行使用 `reflective-implement`、`writing-plans`、`test-driven-development`、`reflective-minimality`。本文件區分實際 app 執行、UI 自動化、原生閉包與實機證據。

**Goal:** 補齊工具鏈，在 Apple 官方 iOS 27.1 Simulator 實跑獨立 UIKit 診斷 host 與隔離 Avalonia prototype，驗證真實原生結果、Run／Cancel、背景／前景及預覽。使用者要求先通過 Simulator 檢查，才繼續共用 Core／Engine 邏輯的主要介面與功能移植。

**Architecture:** 兩個 app 共用 `tools/apple-mobile/Shared/ProbeRunner.cs`，直接參照既有 Core／RAW／GPU；不引用桌面產品 App／Dock／Magick.NET。Avalonia prototype 有共用 view、iOS host 與供 Mac 觀察的 Desktop host。Mac／managed compile 不代替 Simulator 執行。

**Tech Stack:** 原 SDK 與 `global.json` 不變；另隔離安裝官方 workload set `10.0.401.1`／iOS SDK `27.0.10722`。使用 Xcode 27.1、官方 iOS 27.1 arm64 runtime、Avalonia 12.1.4、ONNX Runtime 1.25.0。官方 .NET iOS SDK 只宣告支援 Xcode 27.0；strict build 已拒絕 27.1，僅明確實驗命令使用 `ValidateXcodeVersion=false`，不修改 SDK／專案預設。這不是官方相容性或實機支援承諾。

**Spec:** 使用者本輪要求與 [ADR 0004](../../adr/0004-apple-mobile-shared-core.md)。正式產品 UI 尚未定案。

## 全域約束與授權

- 不更動桌面 App、`FilmBatch.sln` 或 CI；不自行合併 `dev-ai-denoising`。
- 其他 prototypes 與必要修正可本機 commit，無論 Simulator 結果；Simulator／XCTest 測試實際成功後才分開 commit。不 push。`eaab16f` 提交 prototype/runtime 來源，`50ca9fd` 提交原生推論／bundle／linker／狀態版面修正；均排除 Simulator／XCTest。
- 工具鏈只用 Apple／Microsoft 官方來源；不讀取憑證秘密、不代替本人處理登入／簽章。使用程序局部 `DEVELOPER_DIR`，不改全機 Xcode selection。
- 只建立／移除本輪自己的 Simulator，不清除既有裝置、來源照片、模型或驗證資料。實機驗證由使用者延後。
- 27.1 runtime 只列出 iPhone Duo 為相容 device type；建立 iPad A16 已被拒絕。不能將本輪結果稱為 iPad／實機驗證。
- `blocked`／`skipped` 不是原生成功。Dawn／JPEG XL 的 iOS 閉包缺失保持可見，不複製 macOS dylib，也不用 CPU 偽裝 GPU。
- 本輪命令、退出碼、報告、PNG、xcresult、版本／授權雜湊保留在 ignored `dist/apple-mobile-ios27-evidence/`；先前 Mac／編譯證據在 `dist/apple-mobile-simulator-evidence/`。它們不是產品 runtime 相依。

## 檔案與消費端契約

| 位置 | 責任／介面 |
|---|---|
| `Shared/` | `ProbeOptions`、`ProbeResult`、`ProbeReport`、`ProbeRunner.Run`／`WriteReport`；Core／RAW、RGB16 ImageIO、ONNX 與 GPU 真正呼叫 |
| `iOS/` | UIKit scene-based host、取消 epoch、一次性 pending autorun、原生 report |
| `Prototype/` | 共用 Avalonia App／view、iOS／Desktop 啟動；Run／Cancel、adaptive layout、真正 ImageIO PNG 預覽 |
| `Simulator/`、`iOS/UiTests/` | 官方 simctl 安裝／啟動／收報；Swift XCTest 操作實際 controls、解析報告與驗證生命週期 |
| `Directory.Packages.props`、Raw／Shared／Common csproj | 中央 ONNX pin、保留 app RID、傳遞原生內容與 Apple bundle metadata |
| README／ADR／本計畫 | 命令、授權、觀察、失敗與未測邊界 |

UIKit IDs：`Probe.Run`、`Probe.Cancel`、`Probe.Status`、`Probe.Report`。Avalonia IDs：`Prototype.Run`、`Prototype.Cancel`、`Prototype.Status`、`Prototype.Report`、`Prototype.Preview`。bundle IDs：`org.filmbatch.mobile.probe`、`org.filmbatch.mobile.prototype`。

輸出固定於 app 的 `Documents/results/`；matching `run-tag.txt` 驗證 invocation，不用 mtime 猜測。`OperatingSystem` 由 app OS API 產生；Simulator 身分另由 build RID／simctl receipt 證明。取消／stale epoch 不發布新報告，舊工作結束後可重跑。

## 工作進度

### 1. 工具鏈

- [x] 保留原 SDK，建立隔離工具鏈；使用者選擇直接測試 Xcode 27.1／iOS 27.1。
- [x] 取得並驗證官方 workload／SDK、runtime、Simulator device；strict build 的版本拒絕與實驗開關分開記錄。
- [x] 兩個 iOS Release app 實際完成 native build：0 warnings／0 errors。

### 2. 原生執行

- [x] UIKit app 實際 install／launch；Core／RAW、RGB16／ICC ImageIO 與 ONNX 數值／取消結果丟棄／session 重用通過。
- [x] Avalonia app 實際 install／launch；已寫出 iOS 原生報告，畫面呈現真實結果。UI 自動化的狀態查詢失敗不能當成 app 推論卡住。
- [x] 兩個執行檔確認連結 ONNX 1.25.0；核對隨附 ONNX 授權／聲明／隱私檔與 JXL provenance／授權的實際位元組。
- [ ] UIKit／Avalonia 完整六個 XCTest 全通過：目前 4 passed／2 failed；兩個背景案例仍無法建立真正背景狀態，不視為通過。

### 3. 交付 gate

- [x] 既有桌面完整 suite：1,307 passed／320 skipped／0 failed；共用 Mac 原生 CLI 四項通過，來源安全／重跑／單像素邊界 8 項通過。
- [x] 記錄最終 Simulator UI 結果與精確未完成限制。
- [ ] Simulator／XCTest 暫不提交：背景 gate 未通過，相關來源保留在工作樹／既有 staging。
- [ ] 主要 iOS 介面／功能移植暫不開始：使用者指定的 Simulator 前置 gate 尚未通過。

## 已實測結果（2026-10-19）

| 檢查 | 實際結果 | 證據位置（`dist/apple-mobile-ios27-evidence/` 下） |
|---|---|---|
| UIKit Release native build | clean Rebuild 成功，0 warnings／0 errors | `native-clean-rebuild` logs |
| Avalonia Release native build | linker 修正與狀態版面精簡後均成功，最終 0 warnings／0 errors | `prototype-build-linker-fixed` logs；後續 `--disable-build-servers` build |
| UIKit simctl 收報 | iOS 27.1.0、`iossimulator-arm64`；Core／RAW、ImageIO、ONNX pass；GPU blocked，runner exit 2 | `simulator-native-rebuilt/native/report.json` |
| UIKit 初次修正後 XCTest | 2 passed／1 failed；執行與手動取消／重跑通過，Home／背景案例未觀察到取消 | `native-ui-relinked.xcresult`、attachments、`native-background-activities` |
| Avalonia 初次 XCTest | 完成／預覽案例失敗：以 `StaticText` 查詢不到狀態；app 實際已完成並產生報告 | `prototype-ui-first.xcresult`、attachments、`prototype-after-test.png` |
| 最終六個完整 UI 案例 | 4 passed／2 failed，無 skip／預期失敗，xcodebuild exit 65；兩個 app 的執行／手動取消／重跑通過，Avalonia 真實報告及 51×37 圖片預覽通過；兩個背景案例仍為 foreground（4） | `ui-final.xcresult` |
| 背景狀態雙狀態重跑 | 0 passed／2 failed；同時接受 background 與 background-suspended，兩個 app 仍回報 `state=4`（runningForeground） | `ui-background-states.xcresult` |
| 指定 app 的 Command-H | 0 passed／2 failed；XCTest 實際送出按鍵，兩個 app 仍為 foreground（4）；沒有保留無效的額外驅動分支 | `ui-background-keyboard.xcresult` |
| 報告／epoch 邊界 | 20 passed；拒絕 Mac／過期 invocation，取消／dispose 不接受結果，競爭完成只有一個 winner | `dotnet run --project tools/apple-mobile/Simulator -c Release -- verify` |
| 最終原生版本／授權 | 兩個 app 的執行檔均含 1.25.0；ONNX 三份授權檔與 JXL provenance／授權對照一致 | `final-bundle-proof.json` |
| Mac 共用 native CLI | Canon CR2 3944×2622／來源不變、ImageIO、ONNX、Metal arithmetic 四項 pass | `host-runtime-final/report.json` |
| 來源安全與邊界 | 8 passed；直接重疊、雙向 symlink、重跑、1×1／1×7／7×1 | `host-source-boundaries-final` logs |
| 桌面完整 suite | Core 506／27 skipped；RAW 618／293 skipped；App 183／0 skipped；0 failed | `desktop-tests-runtime-final/*.trx` |

先前的 Mac 共用 view 47 項與 managed compile／Swift build-for-testing，只保留為當時的證據，不提升為此輪 iOS UI 成功。

## 失敗原因與已落地修正

1. **RID／裁剪：** 共用 graph 保留 app RID，不把主機 macOS native assets 選進 iOS。full trim 移除未用到的 YamlDotNet reflection serializer，沒有關閉 trim errors。
2. **Apple bundle metadata：** Raw／Shared／Prototype Common 保留傳遞的 `TargetPath`／`PublishFolderType=Resource`，避免內容蒐集最佳化丟失 metadata 而漏包；最後核對真正 app 內容。
3. **Avalonia iOS linker：** iOS 27 的 registrar／trimmer 對 explicit-interface 成員產生無法回查的 DynamicDependency（IL2037）。保留全域 full trim，只對 `Avalonia.iOS` 使用 per-assembly copy，保留註冊成員；不壓掉 IL2037、不改 SDK／NuGet 內容。
4. **ONNX iOS managed asset：** 1.24.4 缺少正確的 iOS `__Internal` managed asset；中央 pin 升至 1.25.0，不留下 app-level version override。
5. **原生連結快取：** 更新 managed DLL 與重新解開新 archive 後，舊執行檔仍連結 1.24.4，造成 CompileApi delegate 空指標。比對 archive SHA、執行檔版本後，以 Rebuild 真正重新連結；1.25.0 原生數值與 session 檢查通過。
6. **真實取消：** UIKit 在 scene resign-active／background 取消未完成 epoch，完成的報告不被取消動作改寫。Avalonia await 外層 finally 負責恢復 controls，不取消 Task 排程本身，避免尚未啟動工作時取消造成永久 busy。
7. **Duo／自動化：** screenshot 指定 `--display=primary`，避免取得未使用的 integrated display。狀態依 accessibility ID 查詢，不限定 UIKit 的 StaticText 類型。先後實測 Home、切換系統 app、測試 app display 上的底緣手勢，以及指定 app 的 Command-H；同時接受 XCTest 的 background／background-suspended 狀態，仍取得 foreground（4）。背景前置條件不放寬為「送出事件」或「非 busy」，也不以 terminate 偽裝背景取消。失敗停留在實際切換狀態，不宣稱已驗證 app 的背景取消。
8. **可用預覽：** 完成狀態不再重複整份詳細報告與缺庫堆疊；完整內容留在 report，手機畫面可直接看見真正圖片預覽。最終原生畫面與 XCTest 已確認。

## 驗收 Ledger

| Requirement | 狀態 | 證據／限制 |
|---|---|---|
| REQ-TOOLCHAIN | verified experimental execution | 官方 SDK／runtime；Xcode 27.1 與 .NET iOS 27.0 非官方支援組合，版本開關僅限明確命令 |
| REQ-PURE-IOS | runnable；background blocked | 原生三項 pass；執行／手動取消／重跑通過，背景切換前置條件失敗；GPU blocked |
| REQ-AVALONIA | runnable；background blocked | 真正報告、預覽、手動取消／重跑通過；背景切換仍停留 foreground |
| REQ-SIMULATOR | partial／gate failed | 六個案例 4 pass／2 fail；背景重跑仍 2 fail，不提交測試、不進入主要功能移植 |
| REQ-DEVICE | deferred by user | 只有 Duo Simulator；沒有 iPad／實機量測或簽章證據 |
| REQ-DESKTOP | verified | 完整 suite 1,307 passed／320 skipped／0 failed；未改桌面產品、solution 或 CI |
| REQ-AUTHORITY | confirmed | prototype/runtime 已獨立 commit；測試成功才獨立 commit；不 push |

## 未完成邊界與下一步

未完成的是可重現的真正背景／前景切換：目前唯一相容的 27.1 Duo Simulator，在上述公開操作後仍回報 runningForeground，兩個背景案例不能驗收。須先取得能建立該狀態的 Simulator／驅動組合，再重跑取消、空報告與新世代結果；不是再跑一次已通過的編譯。額外桌面診斷亦受限：System Events 拒絕 Apple Events（-1743），`open -a Simulator` 找不到 GUI，慣用的 `Developer/Applications` 目錄不存在；未修改系統隱私權限，也不把這些現象當作已證明的根因。官方 .NET iOS SDK 尚未宣告支援 Xcode 27.1，目前證據不足以把切換失敗單獨歸因於 SDK 或 app。重跑命令見 [README](../../../tools/apple-mobile/README.md#avalonia-prototype-與官方-simulator)。保留失敗，不提交 Simulator 測試，也不啟動以全通過為前提的主要功能移植。

無論 UI 測試結果為何，本輪尚未證明 iOS Dawn／JPEG XL、完整攝影算圖／AI 品質、CoreML GPU／ANE placement、HEIC／P3／EDR／Photos／Files 權限、iPad／實機記憶體／溫控或裝置 Release 簽章。這些不得由 Mac 成功、簡單 ONNX 圖或 Simulator 可啟動推定。
