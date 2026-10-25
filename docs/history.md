# 決策與提交歷史

這是精選決策索引，不是複製完整 Git log。歷史來源為本機可達的提交、既有 ADR／計畫，以及 2026-10-15–18 的工作階段交接筆記（工作階段 ID：`01a13e01-f2e0-70e8-b6ac-ab2f176cd24c`）；回收處置見[稽核記錄](superpowers/plans/2026-10-18-governed-verification-and-knowledge.md#recovery-inventory)。不需要該工作階段或其暫存檔才能閱讀本頁。

- 記錄的來源邊界：`6e3291a5cbe33d267ec33b9d42054efde9bd82f3`。
- commit 主旨證明「當時要改什麼」，不證明測試成功、review 獨立性、遠端推送或目前封裝正確。
- 「現行契約／檢查」指向目前可讀的文件與測試；本次實際執行結果只記在[有日期的證據](superpowers/plans/2026-10-18-governed-verification-and-knowledge.md#current-observations)，不從 commit 標題補造結果。
- 短 ID 在本機解析；可用 `git show <ID>` 看原始變更，以 `git log --graph --oneline --all` 看完整分支。後續改動以新列或取代註記補入，不重寫舊決策為「一直如此」。

## 精選演進

| 日期／決策 | 真實提交與原主旨 | 理由、取代關係與現行契約／檢查 |
|---|---|---|
| 2026-10-15：完成桌面工作流 | `cfe7f7c` — `feat: complete film editing and macOS delivery workflows` | 將可用介面、編輯與封裝串起來，而非只交付編譯成功。後續原生部署、RAW、底片與生命週期有各自修正，不能把此里程碑當最終實作。[建置與原生 smoke](building.md)、[VERIFY V3](../VERIFY.md#v3)。 |
| 2026-10-15：中繼資料與匯出復原 | `6b9ca2a` — `fix: normalize sRGB metadata and exclude source image layers`；`90e92da` — `fix: preserve rendered exports when source EXIF is malformed`；`5203033` — `fix: bound JPEG EXIF without silent capture metadata loss` | 不沿用來源圖層／Adobe RGB 的 Interop，也不因 EXIF 壞掉丟失已算出的像素。超過 JPEG APP1 預算先移除 MakerNote，仍過大才使用最小輸出 EXIF，逐張留下警告；不是無條件保留全部私有標籤。[匯出契約](architecture.md#48-engine影像讀取與批次匯出imaging--exporting)、`TiffMetadataTests`／`BatchExporterTests`。 |
| 2026-10-16：直方圖只改呈現 | `af296ce` — `fix: smooth histogram display without changing statistics` | 平滑顯示高度，不改 bins／裁切統計；重開舊 `StreamGeometry` 曾累加歷史 figures，因此資料更新時重建、重繪間才快取。[顯示契約](architecture.md#410-app介面層)、`ImageHistogramTests`／`HistogramTests`；舊跨平台封裝結果不自動延伸至目前版本。 |
| 2026-10-16：語系與原生選單 | `c877c1c` — `Complete English localization and default to English`；`b86d622` — `Fix macOS native menus on language switch` | 資源檔與介面語言一起切換；Mac 原生選單根物件保持穩定，只重建子項。[多國語系與服務](architecture.md#410-app介面層)、`LocalizationTests`、V3 的選單／語言檢查。 |
| 2026-10-16：AI 降噪提案 | `00b2443` — `Add ADR 0002 proposing AI denoise` | 這是當時提案，不是 ONNX／CoreML／DirectML 已交付的證據。早期沒有 RAW 的前提已被後續管線取代；2026-10-19 的[現行 ADR 0002](adr/0002-ai-denoise.md)改採分域的 RawNIND／NAFNet 與固定模型驗證，不再沿用未實測估算；原提案可由該提交查閱。 |
| 2026-10-17：RAW 解碼與能力補齊 | `f4cd262` — `feat(raw): port multi-vendor decoding and camera development`；`3a44538` — `feat(raw): macOS port + lossy-JPEG/deflate/NEF fixes and hardened EXIF append` | 早期「不做鏡頭／gain table／語意遮罩／稀疏檔頭／JXL／DNG 寫出」是當時範圍，不是永久限制。後續已整合，但各格式與比較偏差仍要明列。[raw-oracle](../tools/raw-oracle/README.md)釘選原本的 `f34c7a8a` 語料；整合實驗另釘 `4f0b110b`，不暗中重產既有 oracle。 |
| 2026-10-17：改用底片 LUT | `4e9d232` — `Bundle 36 RawTherapee film LUTs (CC BY-SA 4.0) with conversion tool`；`6c50f3a` — `Remove Recipe Lab film recipes, Sony coefficients and calibration tool`；`031c010` — `Add Nik-style film look thumbnail grid and hue band editor` | 以 LUT／色相帶模型取代 Recipe Lab／Sony 係數；舊校正工具與其建議不應再成為操作入口。`RetiredParams` 處理舊設定，不另維護第二套引擎。[現行底片文件](film-looks.md)、[LUT 工具](../tools/film-luts/README.md)、`FilmLutTests`／`FilmLookTests`／`RetiredParamsTests`。 |
| 2026-10-17–18：裁切與曲線／遮罩分支 | `bea0f91` — `Add tone curve core, curve param kind and basic curve editor`；`0c5e0e5` — `Remove mask code (CPU and GPU) for now`；`703ff49` — `Merge dev-cutandmask into feature/raw-port` | 核心型別曾整合、一般遮罩程式後來移除，裁切保留。不能把完整計畫當全部已交付，也不能聲稱所有曲線核心從未實作。RAW 語意遮罩是後續另一個來源契約。[原計畫與現況註記](cut-and-mask/plan.md)、[生命週期](architecture.md#410-app介面層)。 |
| 2026-10-17：停止 Intel Mac 封裝 | `c1e17fe` — `build: drop osx-x64 (Intel Mac) per dev-cutandmask direction`；`c6852ff` — `docs+build: relocate smoke-display note and drop x64 dawn builds` | 過去 Rosetta／x64 驗證是歷史，不代表仍有 Intel Mac 發行目標。保留 Apple Silicon 與 Windows x64；降低 Mac 部署下限需重建原生庫，不改 Mach-O 宣告來假裝相容。[macOS 發佈](building.md#macos)。Dawn 的 `4178cb7` 是上游 pin，不是這裡的 FilmBatch commit。 |
| 2026-10-18：SIMD 與可攜精度 | `03761e1` — `perf(engine): vectorize CPU effects and add RAW GPU finishing`；`0edba50` — `fix(gpu): make RAW finishing precision independent of fused fma` | 以 SIMD 改善既有 CPU 參考路徑，不以 `Exp(Log(x)*k)` 近似犧牲結果。Metal 模擬未融合 `fma` 曾重現 RAW 超差，顯式補償算術用於可攜性修正；它未被證明是 Windows 當次根因。後續 Windows trace 的第一個錯誤捨入位於 CPU `MathF.Log2`，見[診斷記錄](building.md#gpu-讀回與-raw-數值回歸)。獨立 finisher 再由 `cfe4cf9` 移除；精度理由仍適用。[ADR 0003](adr/0003-gpu-stack-decision.md)、`RawGpuDisplayTests`／V2。 |
| 2026-10-18：編輯生命週期 | `e5788ed` — `fix(preview): end interrupted gestures and preserve shared viewport`；`bc5a3ac` — `fix(crop): preserve project ownership and honor cancellation`；`c3ed472` — `fix(export): allow retries while retaining batch errors`；`b87ce49` — `fix(window): restore fullscreen state and release held modes`；`ace9f8f` — `docs: describe editor lifecycle guarantees and verification limits` | 保留舊專案 ownership 後才切換、取消延後裁切、結束中斷手勢、分開上一批失敗與當前選項有效性。關閉即套用的偏好設定／預設管理不是 rollback Cancel；套用裁切後留在分頁且工具停用也是預期狀態。[現行契約](architecture.md#410-app介面層)、[回歸與 Cocoa 邊界](building.md#編輯生命週期回歸)。 |
| 2026-10-18：RAW UI 與來源一致 | `cc23364` — `fix(raw-ui): synchronize controls and thumbnails with decoded sources`；`7504087` — `feat(raw-ui): expose Kelvin, semantic masks and Lensfun controls` | 依解碼後來源顯示 RAW 面板，等待 matching `RawOptions` 後才生成縮圖；識別字用 `Text`，遮罩名稱＋實例原子寫入。缺失遮罩保留設定但不改成全圖；過期解碼失敗不能清掉 undo 還原的畫面。[參數與復原](architecture.md#41-core參數-schemaparams)、`RawControlsTests`／`RawInspectorTests`／`RawSourceLifecycleTests`。 |
| 2026-10-18：DNG 起始色彩與中繼資料 | `088a5d2` — `feat(raw): preserve starting colour and capture metadata in DNG conversions` | 在來源有可用描述時補足矩陣／profile，保留感光樣本、幾何與標準拍攝欄位；不能把原始 JPEG 擬合的逐像素亮度／chroma 曲線宣稱為標準 DNG 可完整保存。[DNG 比較限制](../tools/raw-oracle/README.md#比較範圍與已知偏差)、`RawDngConversionTests`；同模型對照與轉前／後外觀差異分別記錄。 |
| 2026-10-18：場景線性編輯 | `8c3c0b4` — `feat(raw): integrate scene-linear CPU and GPU edits with shared geometry` | RAW 場景不先壓成 SDR；白平衡、局部調整與幾何作用後才顯示收尾。非線性高光壓縮必須回到 Rec.2020 基底，CPU／GPU 彼此一致不代表色彩正確。語意 alpha 在全尺寸取樣，不用固定 4096 平面抹掉單像素細節。[算圖契約](architecture.md#46-engine算圖管線rendering)、`RawSceneIntegrationTests`／`RawSemanticRenderingTests`。 |
| 2026-10-18：隨附 JPEG XL 閉包 | `c9cd74d` — `build(raw): bundle pinned JPEG XL runtimes for Mac and Windows` | 固定 ABI 與來源／授權，不靠 Homebrew／PATH 掩蓋缺庫；SDK 的 RID negotiation 曾把主機 Mac dylib 選進 Windows 發佈，故 Engine／Raw 明確保留 RID。App 與 CLI 各自檢查實際封裝根目錄。[建置限制](building.md#macos)、[manifest](../src/FilmBatch.Raw/Native/Jxl/provenance.json)、`PortableJxlTests`／`JpegXlTests`。Windows 載入仍須實際執行。 |
| 2026-10-18：修正非置中預設裁切 | `07eff8b` — `fix(raw): map Lensfun calibration to off-centre default crops` | 置中 fixture 會漏掉光學中心錯誤。Lensfun 的中心／半徑屬 `DefaultCrop`，執行時轉成完整 `ActiveArea` opcode 單位；warp 與 vignette 必須共用映射。[鏡頭契約](architecture.md#410-app介面層)、`LensProfileTests` 的非置中裁切案例；保留原本全畫框行為與精度。 |
| 2026-10-18：只保留正式 RAW 收尾 | `cfe4cf9` — `refactor(raw): retire standalone GPU finishing path`；`6e3291a` — `docs(raw): close delayed-review acceptance and package evidence` | 刪除 `FinishRaw`／`raw_display_finish.wgsl` 的平行路徑與專用讀回，回歸改走產品實際的 `Render16`，不放寬原精度。舊 API 與 4096 alpha cap 不應再作修改範本。[RAW parity 記錄](superpowers/plans/2026-10-18-raw-pipeline-parity.md)、`RawGpuDisplayTests`，目前證據見本次稽核。 |
| 2026-10-18：大量照片與即時白平衡 | `eac234b` — `feat(loading): prioritize photos with live CPU pacing and responsive RAW white balance` | 以有界泵及獨立前景入場取代逐照片等待；先送目前照片／可見縮圖，完成以點陣圖或失敗交付計數。CPU 70% 是可即時調整的合作式目標，不是硬性上限。相同來源已完成影格可持續顯示；失敗需攜帶預覽世代，不能消耗其他要求或堵塞後續縮圖。自訂 Kelvin 收斂至 2500–9900 K，不改拍攝白平衡或相對暖色。[載入契約](architecture.md#大量照片載入與背景-cpu)、[回歸](building.md#大量照片載入與即時預覽回歸)、[本次具條件的觀察](superpowers/plans/2026-10-18-progressive-photo-loading.md)。 |
| 2026-10-19：來源階段本機 AI 降噪 | `902dc16` — `feat(denoise): add verified source-stage ONNX processing`；`6ccb98a` — `feat(cli): manage denoise models and export with AI strength`；`af46260` — `feat(app): add non-destructive AI denoise workflow` | RAW 使用 RawNIND Linear，一般 RGB 使用 NAFNet-SIDD；固定雜湊／署名、全解析度推論後縮小、神經結果與混合量分離、真實取消及 UI／CLI／16-bit 匯出。實際 CPU／CoreML、原生介面、完整回歸與 macOS 可執行檔已驗證；Windows 交叉封裝不當成實機通過。實作完成後經明確授權分組提交，未推送。[現行決策](adr/0002-ai-denoise.md)、[具條件驗收與證據](superpowers/plans/2026-10-19-ai-denoising.md#observed-acceptance-evidence--2026-10-19)。 |
| 2026-10-19：Apple 行動版共用技術優先 | `710ef52` — `docs(ios): prioritize shared core and technical probes`；`a0314c9` — `feat(ios): add shared native portability probes` | 保留 C# Core／RAW 與可驗證的 GPU／ONNX 契約，先做共用 CLI 和獨立 iOS 診斷 host，不選正式 UI、不把桌面 Dock 搬到 iPad。Mac 已實跑 Canon CR2、RGB16 ImageIO、真實 ONNX 權重及 Metal 整數讀回；48MP 線性 DNG 的 Darwin peak footprint 為 4.18 GiB，不是 iPad 或完整 AI／GPU 的量測。iOS 實際 build 受 `NETSDK1147` 阻擋，reference compile 不等於 AOT/link／實機。[ADR 0004](adr/0004-apple-mobile-shared-core.md)、[重跑入口](../tools/apple-mobile/README.md)、[本輪證據與限制](superpowers/plans/2026-10-19-apple-mobile-technical-entry.md#結果-ledger)。 |
| 2026-10-19：隔離 prototype 與官方 Simulator 自動化 | `eaab16f` — prototype／runtime；`50ca9fd` — 原生推論與 bundle 修正；`d3a21b9` — 六案例全通過後獨立提交 Simulator／XCTest；使用者後續授權推送 | 新增 Avalonia 共用 view／iOS 與 Desktop host、UIKit scene／取消 epoch、simctl 收報與 XCTest。直接測試使用者選擇的 Xcode 27.1／iOS 27.1，隔離安裝 .NET iOS 27.0 SDK，保留非官方支援組合標示。兩個 Release app native build、iOS Core／RAW、RGB16 ImageIO、ONNX 實際通過；修復 ONNX 1.24.4 包裝／原生快取、Apple bundle metadata 與 Avalonia registrar 裁剪問題。開啟 Device Hub 恢復可見裝置視窗，背景驅動直接啟用 SpringBoard；完整六個 UI 案例 6 pass／0 fail，保留真正背景狀態、取消丟棄、空報告與前景重跑斷言。後續主要功能移植已獲授權，GPU／iPad／實機不得推定。[本輪驗收與限制](superpowers/plans/2026-10-19-ios-simulator-prototype.md)、[命令](../tools/apple-mobile/README.md#avalonia-prototype-與官方-simulator)。 |
| 2026-10-20：共用主要編輯器與原生 iOS 工作流程 | `22d31ec` — `feat(ios): share the editor and add native photo workflows` | 將 ViewModel、控制項、非視窗 View、語系及主題移至唯一的 `FilmBatch.App.Shared`，桌面 Dock 保留在原 host；行動版重用 Core／RAW／Engine，以原生 Files／Photos 副本、app 自有專案及 CPU 算圖完成主要編輯與批次匯出。修復不支援的程序時間／檔案監看、背景入場、觸控與輔助工具標題、低高度參數面板，以及 ImageIO 的標準 ICC／單一 EXIF；不新增舊式 ICC 副本迎合讀取器。最終 Release 原生操作、獨立輸出解碼／雜湊、實際大圖取消與桌面回歸均有分開證據；實機、iOS GPU、工具鏈官方相容性及 AI 不作推定。[架構決策](adr/0004-apple-mobile-shared-core.md)、[操作／結果／限制](building.md#ios-行動版)、[驗證入口](../VERIFY.md#v1)。 |

## 合併不是線性重寫

`703ff49` 有兩個 parent：`03761e1`（RAW／SIMD）與 `de62003`（dev-cutandmask／LUT／裁切）。因此底片模型替換與 RAW 實作來自不同分支，不能把 log 顯示的相鄰日期當成單一分支的先後依賴。更早的曲線／遮罩整合 `9e33f85` 與後續移除 `0c5e0e5` 也都保留在可達歷史中；「曾合併」不等於「目前仍啟用」。

2026-10-20 的另一項整合，以 `cea2d58` 的 AI 降噪與 `754ca96` 的 iOS／CI 為兩端：採用 iOS 分支的唯一 `FilmBatch.App.Shared` 編輯器，保留降噪分支的實際模型、非破壞式參數與來源階段匯出；ONNX 套件依目標平台分開選取，避免舊路徑重複實作或把 Windows 原生 DLL 帶入 iOS。既有 webhook admission／continuation 與 `pre-merge` 所需 gate 保持不變；平台證據仍依[建置與驗證邊界](building.md#ios-行動版)分開記錄。

2026-10-21 的預覽整合，以研究提交 `a93c017` 所在的 `dev-ai-denoising` 為目標，來源為 `dev-preview-enhanced` 的 `bf3d529`：效果輪廓與拖曳功能進入既有 `FilmBatch.App.Shared`，保留原本的雙指縮放、中斷收尾與復原分組；桌面同時載入共用 Controls、host 專用 Dock 與新增的色溫／色偏滑桿樣式，不回復已淘汰的桌面 Controls 路徑。合併後 Release 測試實際為 1,400 通過、0 失敗、320 跳過，跳過案例不算驗收；macOS 原生主 App 的 25 項 smoke 通過，另在真正的 `NSWindow` 驗證輪廓內／外圈拖曳、兩次復原，以及拖曳途中第二指接手縮放且不繼續改參數。這些是 CPU 算圖與 macOS 視窗的證據，不延伸為 Windows、iOS 實機或硬體 GPU 通過；既有 AI 降噪及物件移除研究 gate 不因預覽合併而改變。

2026-10-21 另依使用者要求實作可行的去背流程：採用 Apple Vision 前景選取，不引入生成式模型或雲端；新增逐張遮罩、手動筆刷、透明預覽、復原／重開及 PNG／TIFF 匯出，專案格式升為 3 並可讀取格式 1／2。降噪重建來源須保留遮罩，儲存衝突不得消耗 undo／redo。來源 Release、真實 Vision＋Headless Skia 操作鏈與獨立 PNG／CLI 像素核對通過；文件交付前另重跑 31 項去背相關回歸。原生桌面啟動因鎖定視窗系統的 `-6661` 未通過，不以無頭畫面替代 NSWindow 證據；Windows／iOS 去背實機、人工畫質、獨立 Naga 與新發行包仍未驗收。一次性驗證來源已封存且核對可還原性，未清除既有研究資料。這是已落地的去背，不是改寫既有生成式移除 NO-GO；API 版本需求、實際封裝下限、完整結果與重跑入口見[建置說明](building.md#本機-ai-去背)。

2026-10-22 去背延伸採用「背景計算、呼叫端一次保存」：多選與 `remove-background` CLI 共用引擎，已有遮罩略過；取消只接受已完成項目，換專案／暫停丟棄過期投遞，保存衝突不消耗 undo。草稿逐筆復原採受影響區塊而非整張快照，並將桌面／共用行動 Undo／Redo 隔離於照片歷史。Release build 為 0 warnings／0 errors；完整來源回歸 1,452 passed／0 failed／320 skipped，真實 Vision＋Headless 共用桌面 17 項、CLI 工作流程 9 項及獨立 PNG alpha 核對通過。Headless 不是 NSWindow 證據，未重新封裝或取得 Windows／iOS 去背驗收；[操作與驗證邊界](building.md#本機-ai-去背)另列。此處記錄工作目錄的實作與實跑，並非新增 commit 或 push 收據。

同日完成[暗部還原研究](shadow-recovery-survey.md)：以一張公開 Sony RAW 實跑現行管線、隔離浮點 guided／雙邊與亮度／色度降噪原型，並用真正 darktable 5.6.2 的可重播 XMP 比較。現行顯示域加法造成抬灰可重現，但沒有 Lightroom 對照或「解碼器多恢復一倍」的證據；合成雜訊降低與紋理犧牲分開量測，未把原型接進產品或改舊預設。

同日依後續授權把獨立 `shadow_recovery` 模組接入浮點線性 CPU／GPU 管線，預設關閉；保留舊 `basic.shadows` 與既有照片外觀，專案格式不變。採覆蓋率加權 guided 基底、增益前亮度／色度降噪與保持黑點的共同增益，沿用參數／復原／預設／匯出流程。真實 Sony A7S RAW、Metal、共用 Headless 控制項與 PNG 像素核對已實跑，完整結果與未驗證邊界見[操作說明](building.md#局部暗部還原)。外部 LibRaw 的三感光樣本與共同現像比較不足以授權更換解碼器：Sony 只有一個高光超出值的既有相容裁切差，Canon／Olympus 解包一致；黑白位估計未有校準裁定。保留 decoder 與產品相依，不把研究中的降噪百分比或受控 Bilinear 一致推廣成 Lightroom 優劣。這是工作目錄實作記錄，未新增 commit、push 或發行包。

同日針對 Sony 控制項與 iOS JPEG 偏紅補做實際操作驗證：公開 A7S 的自訂 WB 在預覽、縮圖、復原／重開與匯出可見生效；缺少鏡頭／gain map／語意資料的選項不冒充可用。iOS 目前來源的主要 App 另跑合成色塊與 P3／sRGB 大圖的預覽、曝光／復原及真實 PNG16／JPEG 匯出，再以獨立 CMS 核對；未重現偏紅。JPEG 解碼器間的數值差異與 ICC 轉換分開量測，不以補償色偏、感光樣本一致或 Mac 結果代替 iOS 證據；保留使用者未提供原檔與實機螢幕的未知範圍，見[核對方法與結果](building.md#ios-jpeg-色彩核對2026-10-22)。未更換 decoder、未新增 commit／push 或真機發行包。

## 後續調查想法（2026-10-22）

以下是本輪留下的調查方向，不代表已實作或已取得新一輪開發授權：

- **先取得原始案例，再判定修正位置。** Sony 調整無效與 iOS JPEG 偏紅都需要回報當時的原檔、機型及設定；保留 ICC／EXIF 與檔案雜湊，逐層區分解包、白平衡／profile、預覽、匯出及螢幕顯示。合成圖未重現不能否定回報，也不能拿換 decoder 或固定色偏補償當預設答案；[iOS 核對方法](building.md#ios-jpeg-色彩核對2026-10-22)可作重播起點。
- **讓每張照片的能力更容易辨識。** 可考慮依實際中繼資料提示內嵌鏡頭修正、gain map、語意遮罩及內嵌 JPEG 縮圖的適用範圍；沒有資料與控制項未生效必須分開。這是介面提示構想，尚未新增能力偵測或停用規則。
- **把暗部畫質與執行成本分開驗收。** 下一輪候選是更多相機／ISO／夜景、亮暗細線、膚色及飽和邊緣，分別量測降噪、紋理損失、暈光、代理預覽與全圖差異；GPU 單次處理時間、記憶體及真機取消另測，不沿用多次算圖診斷的耗時當效能結論。既有[研究與未取得的驗收](shadow-recovery-survey.md#可實作建議與尚未取得的驗收)仍適用。
- **解碼器變更維持證據門檻。** 只有受控比較確認影響正確性或可用影像資訊的差異，才另行評估修正；Sony 高光超出值與相機黑白位仍需各自的高光／校準證據，不因暗部功能已落地就重寫既有相容契約。

同日後續獲使用者明確授權先記錄、再 commit／push。本次提交意圖是保存已驗證的批次去背、草稿逐筆復原、暗部功能與調查文件；前文「未提交／未推送」描述的是驗證階段，不是遠端完成收據。推送限目前 `dev-ai-denoising` 分支的一般前進式更新，不包含強制推送、tag、部署或新版發行包；實際提交與遠端狀態以 Git 收據為準。

## 互動與影像效能修正（2026-10-22）

依使用者授權處理效能審查，以 `b32cd4c` 為未修改基準，先重現再整合。滑桿改用綁定原專案／選取集合的暫存值，結束才保存；排程回呼移出提交鎖，原始 epoch 隨結果傳遞至 UI。縮圖像素改為有界保留，捲回重載不重複計數；透明縮小以覆蓋率加權，PNG16 不再自動退成調色盤 alpha。

RAW 只減少不必要的複製與配置，不更換解碼器；降噪仍保留完整神經結果供匯出，RAW 代理混合與 SDR 逐像素夾取／量化採不同路徑以維持原契約。GPU 使用相同高斯權重與 tap 順序的共讀 tile，不採近似核心；JPEG 提示保留完整匯出，並明列預覽差異。成對量測、未加速路徑、最後 Release 回歸、真正 Metal／原生視窗及重建發行包證據集中於[本輪紀錄](superpowers/plans/2026-10-22-performance-correctness.md)，不借用前輪結果。

Sigma BF 另在 `research/sigma-bf-raw-performance` 保存並推送研究提交 `f059b78`：已量測 Sony，尚無可讀取的 BF 原始 DNG；公開原檔下載受到 HTTP 403 阻擋。這不是 BF 修正完成的宣告，DNG 路徑假說不併入產品；後續需以原始 BF 檔案驗證。

程式修正分組提交為 `a5c8266`（透明縮小／PNG16／JPEG）、`7211c44`（編輯手勢／縮圖／排程）、`a7f22c2`（RAW 配置）與 `35f4373`（降噪代理／精確 Gaussian）。Git 已回報 `b32cd4c..35f4373` 一般前進式推送至 `origin/dev-ai-denoising` 成功；本證據文件另行提交，不從提交主旨推論測試或遠端狀態。

## Sigma BF、預覽分支整合與 Windows 修正（2026-10-22）

使用者後續提供可讀取的 Sigma BF DNG，取代前節的缺少輸入限制。研究分支提交 `e61090f`、`91827cb` 量測並修正共用 GainMap 的逐列工作分配，保留插值與完整輸出雜湊；已正式合併至 `dev-ai-denoising`。接著以 `4a86715` 合併 `dev-preview-enhanced` 的 `4996977`，保留共用編輯器的暫存手勢／單次提交、AI／遮罩／匯出契約，納入 RAW 白平衡、色軌、膚色控制點、漏光混合與 Lensfun 偏好。

同一整合提交修正 `35f4373` 引入的大型 Gaussian 佇列逾時：限制累積工作量、分批等待，不延長 10 秒期限、不縮小案例、不改走應用程式 CPU renderer。[Windows CI #110](https://circleci.com/gh/minexo79/FilmBatch/110) 已在 DX12 軟體介面卡上通過原案例、完整回歸、封裝與原生 smoke；macOS、shader、admission 與 pre-merge gate 也通過。程式碼已推送；後續文件提交僅補記遠端結果，不混淆驗證版本。[整合與驗證證據](superpowers/plans/2026-10-22-performance-correctness.md#subsequent-sigma-and-preview-branch-integration)保留各平台測試數與未驗證範圍。

## Pipeline 再驗證與色調曲線（2026-10-23）

以目前分支的 `e84ad2c` 為基準，實際重現縮圖中斷後停在 4/18，以及 Basic Shadows 抬黑／階調反轉；修正排程復原條件與共同 RGB 增益後，原生 18 張載入及 Shadows 全尺寸 Metal 匯出通過。RAW 分箱改用每個 worker 的暫存陣列，三張實拍預覽配置量下降約 15–25%，53 項影像／尺寸欄位不變；不宣稱整條 pipeline 全面加速。

LightCraft v0.13.0 差異只採用經重現的 Canon coded-width 與 Fujifilm plain-radius 修正；未因版本更新任意放寬 DNG 色調擬合、支援新機型或替換外部 oracle。使用者另要求多段曲線，沿用既有點編輯器、Settings 與復原，放在「照片基本參數 → 基本」的基本調整之後。CPU／Metal 共用查表；原生操作、保存／貼上、重開與 PNG16 匯出已有實測，跨平台與外部相機資料的邊界另列於[完整驗證記錄](superpowers/plans/2026-10-22-performance-correctness.md#pipeline-revalidation-2026-10-23)。
本輪以五筆實作／文件提交拆分，`1f92a70` 再合併遠端至 `6fb61dc`，保留評分、面板順序、RAW 預設與其他遠端行為。合併後回歸為 1,620 passed／320 skipped／0 failed，37 個 WGSL、11 段原生曲線流程、18 張中斷恢復載入與 25 項 macOS 封裝 smoke 均通過；封裝 CLI 的 Metal 像素與 GUI 相同，CPU 最多差 1/65535。完整 receipt 與未測範圍見上述驗證記錄。


## ProPhoto RGB 匯出（2026-10-23）

以 `35da12a` 為基準的工作樹加入真正的 RAW 廣色域 ProPhoto TIFF16／PNG16 輸出，保留原本 sRGB 預覽與匯出預設；不是只替換 ICC 標籤。GUI／CLI、來源編碼、縮放、EXIF 與匯出紀錄共用明確的色彩契約。ICC 由 LCMS 2.17（MIT）自行產生，使用 v4.3 parametric gamma 1.8，避免 v2 單值曲線把 gamma 量化成 1.796875；沒有引入 GPL／LGPL 實作或下載廠商 profile。

實拍驗收先抓到 CPU／Metal 最大差 4/65535：色域壓縮在零附近相消的殘差，被純 gamma 1.8 放大。改為等價的邊界投影後，六面飽和漸層回歸與原問題像素通過；4672×7008 實拍最大差降為 1/65535，未放寬 2/65535 門檻。同一 renderer 的 TIFF／PNG，以及原生 GUI／CLI Metal 像素完全相同。完整回歸為 1,698 passed／320 skipped／0 failed，38 個 WGSL 通過；原生匯出不改變預覽與直方圖。

兩筆 Windows RAW／JPEG 偏暗、黑塊與藍色斷階報告仍未修復：已確認差異存在於匯出像素，但缺當次完整設定、建置版本與可執行的 Windows 環境。不能用 Mac CPU／Metal 相符推定 DX12 正確，也不能把 ProPhoto 修正冒充跨平台修復。詳見[完整驗收、來源授權與證據邊界](superpowers/plans/2026-10-23-prophoto-output.md#final-verification-evidence--2026-10-23)。

後續依明確授權拆成 `0786aa0`（色彩／算圖）、`0c496e4`（codec／metadata）、`566de17`（匯出介面與文件），再以 `5f93b59` 合併遠端 `31f23a7`，保留重設照片回到 Neutral 與 seed 數值／隨機按鈕。兩份語系資源由 Git 自動合併；獨立 XML 核對確認為雙方完整聯集，無重複鍵或殘留衝突。合併後重新執行 **1,703 passed／320 skipped／0 failed**、38 個 WGSL、原生匯出及八種 CLI 情境；4672×7008 CPU／Metal 最大差仍為 1/65535，GUI／CLI Metal 與同一 renderer 的 TIFF／PNG 完全相同。[合併後證據](superpowers/plans/2026-10-23-prophoto-output.md#post-merge-verification--2026-10-23)與[長期跨平台實驗手冊](cross-platform-imaging-experiments.md)分別保留本輪收據、各層比較方式及不得跨平台推論的限制；後續只有文件變更，沒有重新封裝。

## 基本調整的黑塊與藍色斷階（2026-10-24）

使用者新增 macOS 的原圖、對比 59／100、陰影 -100、細節 -100 匯出紀錄後，CPU 與 Metal 都能在 JPEG 編碼前重現。根因不是已證實的 Windows 專屬差異，而是細節增益把全域調整後、可能正負相消的亮度，與調整前的 Gaussian 基底比較；代理圖中原為 0.081 的 B 通道曾被放大至 120.989。

最小修正是讓 CPU／WGSL 的細節區塊先於全域顯示調整，不增加 pass、配置或夾取補丁。8 個均勻色塊案例先失敗後通過；16 組 Neutral／單獨全域／單獨細節、sRGB／ProPhoto 相容性雜湊不變。兩組設定的 6000×4000 CPU／Metal 最大差均為 1/65535；原生 App 重播 220 個設定後的 JPEG，解碼像素與直接 Engine 匯出完全一致。完整回歸為 1,711 passed／320 skipped／0 failed，38 個 WGSL 通過。既有細節與全域調整同時非零的設定會採用修正後外觀；來源照片未改動。

方法、收據與限制見[本輪實驗](cross-platform-imaging-experiments.md#71-極端基本調整異常2026-10-24)。Windows／DX12 尚未實跑，原 Windows 匯出的精確設定／版本仍不明，另一筆 RAW 偏暗案也未因此結案；未重新封裝、commit 或 push。

## ARW 預覽排程、記憶體與 GPU 預算（2026-10-25）

使用 `DSC01282.ARW`（Sony ILCE-7M4，轉正後 4672×7008，SHA-256 `9bfbc40308ee7654b40e131dc5455f04d55ba19574456b16649e8db120d946b7`）與未修改基線比對。只修正可重現的生命週期與工作區問題，不更動 RAW 解碼、色彩公式、來源照片或 GPU 預算：

- **放大後不清晰**：窗口成功／失敗改用獨立要求世代，防止排入 UI 後的舊結果誤標成新設定；Suspend／Resume 清除失效的去重狀態。另以原生操作重現「同尺寸照片在放大狀態下切換」：替身暫停送件，但不能把正式影像的窗口計畫停用，否則沒有新尺寸事件便永遠不送窗口。回歸先在窗口交付等待失敗，再於修正後通過。
- **記憶體保留**：明確釋放淘汰的雙緩衝／原圖比較 bitmap，換照片及閒置後釋放 Full 與對應 GPU 來源。即使快取已清空，常駐 render 迴圈仍可由區域變數保留上一筆來源；四條真正 CPU 算圖路徑的弱參照回歸先失敗，再以不可內聯的單筆 `RunNext` 解決。產品不呼叫強制 GC。
- **載入卡頓**：不再於一般載入時，先在 UI 執行緒把 RAW 編碼成看不到的原圖比較 bitmap；比較啟用才建立、隱藏即釋放。這是減少 UI 工作與配置，不是 RAW decoder 加速。
- **意外回退 CPU**：原先以整個窗口工作區一次判斷預算；現在最多切四塊，各自保留效果鄰域並直接拼回需要的像素。實拍 3072×2304 窗口在相同 768 MiB 預算下由 CPU 改為 Metal；中性、細節／暗角、陰影復原與鏡頭校正皆通過整張／窗口比較，最大差 ≤1、平均差 <0.05 個 8-bit LSB。1 MiB 不足預算時仍安全回退 CPU，不縮小 Full 來源。

同一驅動程式、隔離設定、四份相同 ARW 副本的單次配對觀察如下。受控記憶體數值來自驗證程式的 `GC.GetTotalMemory(true)`，不是 RSS、GPU 專用用量或峰值；不把單次開圖差異宣稱為普遍吞吐量改善。

| 觀察 | 修改前 | 修改後 |
|---|---:|---:|
| 隱藏原圖對比的 `SetOriginal` UI 工作（ms） | 175.9 | 1.3 |
| 該步驟配置（MiB） | 50.05 | 0.00265 |
| 開圖期間最大 UI 排程間隔（ms） | 383.6 | 121.7 |
| 首次開圖完成（s） | 2.921 | 2.761 |
| 放大後回到符合視窗、閒置時的受控記憶體（MiB） | 781.2 | 304.9 |
| 12 次選圖後的受控記憶體（MiB） | 1036.1 | 446.5 |

最終完整 Release 回歸為 **1,849 passed／321 skipped／0 failed**，38 個 WGSL 通過 Naga。原生流程完成不平移的 100%／300% 放大、四角平移、12 次一般選圖及 12 次放大選圖；放大切換時逐張以弱參照確認前一張 Full 已回收，最後回到符合視窗後 Full 也已回收。實際算圖器各次皆無 CPU 回退理由，來源雜湊不變。略過項限 Windows 專用、未提供的外部 RAW／oracle 資料與既有診斷 probe；本次新增回歸沒有略過。

重現程式、修改前後 TRX、像素比較、配對量測與原生畫面保留在本機忽略檔 `artifacts/verification/2026-10-25-preview-lifecycle.tar.gz`；不包含原始 ARW，不上傳私人照片與驗證畫面。核心回歸為 `GpuWindowBudgetTests`、`RenderSchedulerTests`、`WindowDeliveryRaceTests` 與 `PreviewBitmapLifetimeTests`。

**未結案：ARW 周邊破圖。** 現有照片的完整解碼、四角／中央窗口像素比較、原生四角平移與目前提供的截圖，仍未隔離出報告的周邊破圖。不能把已修正的過期窗口交付直接當成該畫面的根因；也沒有以邊緣抹除、RAW 特例或放寬精度處理。需要發生當下的倍率、平移／設定與畫面才能閉合因果。

原生驗證使用真正 Avalonia macOS 視窗與 Apple M3 Metal；桌面鎖定，驅動程式明確以 inactive 模式操作並擷取視窗渲染，不當成已解鎖桌面的滑鼠／鍵盤驗收。Windows／DX12、iOS 未重跑；設定與專案格式未變，沒有重新封裝、commit 或 push。契約見[預覽排程](architecture.md#47-engine算圖排程scheduling)與[Loupe 預覽](architecture.md#loupe-預覽)。

## 如何新增歷史列

只記對未來修改有用的決定：真實 commit／日期、可核對的理由或重現、被拒絕方案、目前契約／檢查、後續取代。沒有當時證據時明寫「只確認提交意圖」；數值必須保留平台、輸入與來源版本，不把舊測試 totals 複製成新版本的驗收結果。

不保存原始對話、推理逐字稿、機器絕對路徑或憑證；不把某次「可 commit／push」的批准延續為今天的權限。
