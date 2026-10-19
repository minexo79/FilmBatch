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

- [x] 將調查中可持續使用的決策、套件版本、原生與工具鏈限制內化至 ADR，更新文件地圖。
- [x] 驗證 55 個相對連結後，先以 `710ef52` commit 決策與此計畫；未推送。

### 2. 共用程式與 Apple 平台實驗

- [x] 建立 Core／RAW 自我檢查及 CLI，產生已知線性 DNG 並透過既有 API 讀回；直接驗證參數復原、專案重開及照片雜湊，不用來源字串或 mock echo 作測試。
- [x] 建立同一份 macOS／iOS ImageIO C API 實驗：奇數列寬、16-bit PNG／TIFF、ICC、方向／中繼資料及像素正確性；Mac 實際執行通過。
- [x] 建立 ONNX native 真實執行實驗：小型自有測試圖使用獨立數值預期，選用實際權重時記錄 SHA-256；檢查取消後可重用，不把測試圖稱為已完成 AI 降噪。
- [x] 建立既有 Gpu wrapper 的 Metal 運算與 readback 實驗，保留 native 錯誤；Mac 實際通過，iOS 原生閉包未提供，不以 CPU 假裝 GPU。
- [x] 建立沒有產品 UI 的 scene-based iOS 診斷 host 與 JSON 沙箱報告，不載入 Avalonia.Desktop／Dock／Magick.NET；官方參考組件編譯通過，實際 iOS build 受 workload 阻擋。

### 3. 難題驗證

```bash
dotnet run --project tools/apple-mobile/Host -c Release -- --out dist/apple-mobile-evidence/default
dotnet run --project tools/apple-mobile/Host -c Release -- --out dist/apple-mobile-evidence/stress-24mp --width 6000 --height 4000 --no-gpu
dotnet build tools/apple-mobile/iOS -c Release -r ios-arm64
dotnet test FilmBatch.sln -c Release --logger trx --results-directory dist/apple-mobile-evidence/desktop-tests
```

- [x] 保留 fail-before／pass-after 的同一案例與原始 log；`VerifyBoundaries.cs` 實際 8 項通過，涵蓋重跑、單像素維度及來源路徑／symlink 不變性。
- [x] 在 Mac 執行完整 CLI、24MP／48MP 記憶體／線性 DNG 路徑、真實 Canon RAW、既有 RAWNIND／NAFNet 權重、Ctrl+C 與錯誤退出路徑。
- [x] 檢查固定 ONNX iOS xcframework 的 device／simulator／Catalyst manifest；實際 iOS build 回報 `NETSDK1147`，未安裝 SDK／變更簽章。reference-assembly compile 僅標成 compile-only。
- [x] 既有桌面完整 suite：App 183、Core 506、Raw 618 passed；合計 1,307 passed／320 skipped／0 failed，未改動既有測試、像素容差或平台 skip 規則。

### 4. 結果與提交

- [x] 將實際命令、觀察、限制及下一個可推翻假設的 gate 寫入本計畫和工具 README；本輪文件相對連結與明確 HTML anchors 已檢查。
- [x] 分組 commit 可執行實驗與結果：決策 `710ef52`、實驗 `a0314c9`，結果隨本文件提交；未 push、未安裝 Xcode／workload、未動憑證／權限。正式 UI 不在本輪實作。

## 結果 ledger

| 驗收項目 | 狀態 | 證據／邊界 |
|---|---|---|
| 決策與範圍 | recorded | ADR 0004；使用者授權共用優先、iPad 優先、UI 延後，文件與實驗 commit、不 push |
| 本輪工作分支 | observed | `dev-ios-app@858648d`；AI 功能分支不在本輪自行合併 |
| Core／RAW、影像輸出、ONNX、GPU 實驗 | verified on Mac | 真實 Canon CR2、51×37 RGB16 PNG／TIFF、已存在模型權重及 Apple M3 Metal 全值讀回；不是 iPad 裝置證據 |
| 來源安全與邊界 | verified | 永久回歸 8 項通過；直接重疊、輸入／輸出 symlink 在寫入前拒絕；`/var` 祖先 alias、重跑與單像素維度均有先失敗後通過證據 |
| 記憶體 | measured on Mac | 實際 24MP／48MP 線性 DNG writer→decoder→develop；最新 48MP Darwin peak footprint 4.18 GiB，不是感光 RAW＋完整 AI／GPU 或 iPad 上限 |
| iOS deployment | blocked, not passed | 新專案實際 build 在 `NETSDK1147` 停止；官方 iOS reference 26.0.11017 的 managed API compile 為 0 warning／0 error，不能代替 AOT/link／模擬器／實機 |
| 桌面產品相容性 | verified | 原有 solution 1,307 passed／320 platform-or-external-corpus skipped／0 failed；未修改產品來源與 CI |
| 正式 UI | intentionally deferred | 下輪根據真正 iPad 可執行性、原生閉包及量測再選框架與互動 |
| commit／push | delivered locally | 決策 `710ef52`、實驗 `a0314c9`、本次結果文件；沒有 push |

## Consumer map

- 共用實驗直接呼叫者：CLI covered；iOS 使用同一 runner／JSON 契約，managed API compile-only，native link／實機為明確未驗證邊界。
- 桌面入口與 CI：intentionally unchanged；實驗未加入 solution，既有完整桌面 suite 通過，不要求 iOS workload。
- 既有照片／預設集／專案：covered；相機／模型前後 SHA-256 一致，直接重疊、symlink 與重跑都有實際消費端回歸；不遷移使用者資料。
- 生成值與持久化：covered on Mac；report JSON 使用 source generation，Core 專案／預設集直接讀回；iOS AOT/link 仍須工具鏈，不冒稱已驗證。
- 文件：covered；ADR 保存決策，README 保存操作，本計畫保存日期化驗證；相對路徑與 anchors 有執行檢查，未知項不提升為支援承諾。

## 2026-10-19 觀察與原始證據

執行平台：Apple M3、16 GiB RAM、macOS 26.6.2、.NET SDK 10.0.402／runtime 10.0.13。命令、輸入、JSON、TRX 與失敗重現位於 ignored `dist/apple-mobile-evidence/`；[工具 README](../../../tools/apple-mobile/README.md) 提供重跑入口。以下是本輪實際結果，不承接其他分支的驗收數字。

### 共用消費端

- 預設 CLI 四項均通過；最後將真實 Canon CR2＋既有 NAFNet／CoreML 權重放在同一次 CLI 執行，四項也均通過。Core 直接操作真實 `ProjectService`／`CommandStack`／YAML 預設集／專案重開，沒有另造狀態儲存。
- CC0 Canon 40D CR2 使用現有 `vendors.tsv` 釘選：SHA-256 `775c806358fedddec7622113a5e399a3330bd5a65e35e37ae1108d8bf58067be`，原始來源為 `https://raw.pixls.us/data/Canon/EOS%2040D/_MG_0153.CR2`。3944×2622 RGGB 真實解碼與 Bilinear develop 通過，前後雜湊一致；不是全相機校準或 iOS 相容性聲明。
- ImageIO：兩個 51×37 RGB16 檔，各逐值比對 5,661 components；row stride 306 bytes、native bitmapInfo `0x1000`。編碼／解碼 ICC SHA-256 均為 `2b3aa1645779a9e634744faf9b01e9102b0c9b88fd6deced7934df86b949af7e`；orientation 3、Software、Description 保留。沒有驗證 HEIC／JPEG／P3／EDR，沒有將此實驗接入產品匯出。
- ONNX Runtime 1.24.4：真實 native `y=-x` 的 48 個 FP32 值、預先 terminate 的取消結果丟棄與 fresh RunOptions/session reuse 通過。RAWNIND 的 `[1,3,512,512]` 權重（`df957efadcc152c007d5d3b0917bdff9e41c0d4a0efe56584ef30b36393cd181`）在 CPU 與 CoreML+CPU 實跑；NAFNet 的 `[1,3,768,768]`（`8b437280db2f9f0ef5c733fa0fec70fc10012a62d19dac24a25b80ec8c529230`）在 CoreML+CPU 實跑。模型檔雜湊不變，輸出形狀／有限值檢查通過。輸入是合成張量；RAWNIND 原始輸出含巨大的負值、尚未套用其必要的全域 gain；**只證明權重／ABI 可執行，不是可用降噪影像或品質驗收**。NAFNet 原生警告保留於 log：部分 node 未分配至偏好的 EP，不宣稱全部在 GPU／ANE。
- GPU：既有 `FilmBatch.Gpu` 於 Apple M3 Metal 執行 257 個整數的 guarded dispatch→copy→map；每個結果符合 `3*x+1`，不是完整攝影 shader 移植。
- 直接輸入／輸出重疊、錯誤尺寸及未知 flag 退出 2；缺模型保留 `FileNotFoundException` 與 fail JSON、退出 1，不回傳假成功。對 48MP 工作送 `SIGINT` 退出 130，之後相同輸出目錄可再次完成。

### 大圖記憶體

直接量測已建置的 CLI，避免把編譯器程序混進去：

```bash
/usr/bin/time -l dotnet tools/apple-mobile/Host/bin/Release/net10.0/filmbatch-mobile-probe.dll --out dist/apple-mobile-evidence/stress-24mp --width 6000 --height 4000 --no-gpu
/usr/bin/time -l dotnet tools/apple-mobile/Host/bin/Release/net10.0/filmbatch-mobile-probe.dll --out dist/apple-mobile-evidence/stress-48mp --width 8000 --height 6000 --no-gpu
```

| 輸入 | 最新 max RSS（GiB） | 最新 Darwin peak memory footprint（GiB） | 程序 wall time（秒） |
|---|---:|---:|---:|
| 24MP／6000×4000 | 1.62 | 1.62 | 1.46 |
| 48MP／8000×6000 | 2.74 | 4.18 | 2.85 |

兩次實跑的原始 `time -l` 數字都保留，表格為最後一次；RSS 與 Darwin footprint 是不同量，不能互換或把其差異當 leak。這是整張線性 RGB DNG fixture 的 writer／decode／develop 加上小型 native 檢查，GPU 明確略過、沒有完整大圖 AI。`CallingThreadAllocatedBytes` 不包含其他工作執行緒，也不是峰值 RSS。[INFERENCE] 共用 C# 方向可繼續，但必須先降低／量測大圖工作集；換正式 UI 不會自行解決這項成本。

### iOS gate 與修正

- `dotnet build tools/apple-mobile/iOS -c Release -r ios-arm64` 實際回報 `NETSDK1147`：缺少 `ios` workload；前置調查的完整 Xcode／iphoneos SDK 也未補裝。沒有模擬器、裝置、AOT/link 或簽章成功聲明。
- 官方 `Microsoft.iOS.Ref.net10.0_26.0` `26.0.11017` NuGet SHA-256 為 `ac99eb4e23a263e4f4f09c4d99adbc77c79eebce5eb800fc8be413118d3da59a`。以它的 `Microsoft.iOS.dll` 編譯**實際 iOS Program.cs**，0 warning／0 error；原先的 nullable launch-options／iOS 26 舊 UIWindow overload 診斷已修正為可空參數及 `UIWindowSceneDelegate`／scene manifest，沒有壓掉 warning。這個 gate 不執行 UIKit，不證明 native registrar／linker／實機畫面。
- ONNX 固定套件內的 xcframework manifest 列出 `ios-arm64`、`ios-arm64_x86_64-simulator`、Catalyst；app 直接引用套件的 NativeReference targets。WebGPUSharp、JPEG XL 的既有 iOS 閉包仍未提供；正式 iOS GPU 不能因 Mac 成功而標通過。
- 首次整合暴露的工具缺陷已修正：LibraryImport 外層必須 partial、不得繼承 sealed `InvalidDataException`、只能使用 Raw 的公開 `Validate()`、WGSL 不能以保留字 `target` 命名。ImageIO 也不能將桌面 dynamic library 寫成 `__Internal`，已改為明確 framework paths；移除不需要的另一套 PNG/TIFF parser，保留 native 消費端逐值／ICC／中繼資料驗證。
- 重跑污染與 1-pixel corner oracle 均有失敗→通過記錄；來源 symlink 曾讓自有測試副本被 fixture 覆寫，永久回歸先失敗再修正。CanonicalPath 必須連 symlink **目標的祖先**也解析，才能涵蓋 macOS `/var`→`/private/var`；不能只解析最後一個檔案。真正的相機原檔／權重未受修改。

下一個可推翻假設的 gate：補齊實際 iOS 工具鏈後，執行 signed iPad Release，驗證 RAW／ONNX／ImageIO 與未提供的 Dawn／JXL 閉包，再量測完整影像流程的 memory／取消／溫控。正式 UI、Photos／Files 工作流與產品 device/OS 下限仍留待下一輪決定。

