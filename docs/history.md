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

**本輪當時未結案：ARW 周邊破圖。** 完整解碼、四角／中央窗口像素比較與原生切圖排除了已修正的窗口交付問題，但當時尚未納入使用者補充的外部 Lensfun 校正條件，不能據此宣稱周邊破圖已修復。同日後續重現與修正見下一節。

原生驗證使用真正 Avalonia macOS 視窗與 Apple M3 Metal；桌面鎖定，驅動程式明確以 inactive 模式操作並擷取視窗渲染，不當成已解鎖桌面的滑鼠／鍵盤驗收。Windows／DX12、iOS 未重跑；設定與專案格式未變，沒有重新封裝、commit 或 push。契約見[預覽排程](architecture.md#47-engine算圖排程scheduling)與[Loupe 預覽](architecture.md#loupe-預覽)。

## Lensfun 校正邊緣重複條紋（2026-10-25）

使用相同 `DSC01282.ARW` 與使用者提供的 Lensfun `data/db`，匹配 Sony ILCE-7M4／Sigma `24-70mm F2.8 DG DN II | Art 024`、34 mm。這張 ARW 沒有可用內嵌校正；啟用外部校正後，完整現像與分箱預覽都出現截圖所示的邊緣重複條紋，不是只有縮圖或窗口交付出錯。

根因是外部徑向／橫向色差校正缺少自動構圖：逆映射落到有效來源外，取樣器的邊界 clamp 把最外列／行延伸成條紋。完整周界的 70,068 個 RGB 通道樣本中，修改前有 40,726 個超出 ActiveArea，最大越界約 36.02 px；修改後皆在預設裁切內。修正依所有通道與連續周界極值求統一前置倍率，折入原有 `WarpRectilinear`；這張照片需要約 1.01267 倍構圖放大，仍輸出 4672×7008，不遮邊、不事後裁切、不停用分箱，也不增加第二次全圖取樣。

`LensProfileTests` 新增的兩個周界案例、兩個紅／藍色差像素案例與三個分箱倍率案例均先失敗、修正後通過；相關幾何、內嵌校正優先順序、暗角與語意遮罩等 101 項 focused 回歸通過。實拍長邊 1600／2560 預覽與四個 512×512 全尺寸角落的 CPU／Apple M3 Metal 比較，RGB 最大差 1、平均差低於 0.000007 個 8-bit LSB；沒有放寬既有精度門檻。

原生 macOS 視窗以程式驅動完成 270% 放大、四角平移、300% 原始解析度與兩次放大切圖；實際算圖器都是 Metal，來源雜湊不變，四角畫面未再看到重複條紋。270% 依代理畫框長邊 2560 換算為 6912；另在 300% 明確確認 4672×7008 的原始畫框，不能把兩個驗收條件混為一談。初版驅動程式誤要求 270% 必須已達原始尺寸而逾時，修正的是驗證程式，不是產品或精度門檻。

最終以 `dotnet test FilmBatch.sln -c Release --no-restore --no-build -m:1` 完成 **1,856 passed／321 skipped／0 failed**。首次完整測試與原生操作並行時，既有非 RAW 的 `BulkLoadingTests.SuspendedEditor_DefersEditsAndPreferenceRendersUntilResume` 在 Resume 等待逾時；沒有修改產品、測試斷言或逾時，結束原生操作後循序完整重跑通過，該次逾時原因未另行隔離。略過項仍限平台專用、未提供的外部語料與既有診斷；本次新增案例全數執行。Windows／DX12、iOS 原生與 CLI／匯出介面未另跑，完整來源與共用算圖路徑已驗證。

修改前後影像、TRX、座標／像素比較、輸入與來源版本雜湊、原生驅動程式保留在本機忽略檔 `artifacts/verification/2026-10-25-lensfun-edge-framing.tar.gz`；不含原始 ARW，不上傳私人畫面。此次沒有 commit、push 或重新封裝。

只修改外部校正的構圖及對應回歸；沒有更動 RAW decoder、內嵌校正、照片、資料庫、GPU 預算或設定格式。先前未重現邊緣條紋的無外部校正測量仍保留為歷史證據，不把它當成此次修正的基線。

## CPU 狀態、背景縮放與 RAW 重疊載入（2026-10-25）

本輪依前次 CPU 調查實作三項有界修正，不增加 GPU 預算、不改用相機 JPEG 作為最終縮圖、不停用語意遮罩，也不改 RAW 顯影品質。先前 Lensfun 修正已獨立提交為 `9f18eb0`；本輪沒有 commit、push 或重新封裝。

- **真實後端狀態**：Main／Window 完成當下在算圖執行緒擷取後端快照，通過世代／Epoch 驗證才更新 UI。縮圖、預取與過期交付不覆寫可見影格的狀態。原始 DLL 上五個狀態回歸皆因仍回報 GPU 而失敗；修正後通過，原生受限預算情境也觀察到狀態列隨 Metal → CPU（含預算原因）→ Metal 改變，不逐格跳提示。
- **UI 來源縮放**：Main 代理圖與 Full 窗口來源的昂貴縮放移到背景，保留快取身分、最新要求合併、取消與來源釋放。修改前的 Main／Full 來源鎖案例都會阻塞 UI；修改後與換照片、改尺寸／設定、Suspend、窗口更新等共 14 項 UI／狀態回歸通過。
- **RAW 重複工作**：僅重用同檔案、同內容且時間重疊的感光解碼與相同有效校正鏈的色彩擬合。每位使用者保有獨立可變中繼資料；最後租約結束即移除完整資料。五項內容替換、校正隔離、錯誤恢復、共用安全與生命週期回歸通過。相同實拍 ARW 的 240／2560 長邊場景浮點像素雜湊與原版完全一致。

Apple M3 Metal 原生視窗、相同 ARW／Lensfun 的 UI 量測如下。指標是背景執行緒投遞至 UI 的最大延遲，不是 GPU 算圖時間。保留一次修改前與兩次修改後的結果，不擇優；首次修正後量測早於後端狀態的語言切換修補，最終量測包含該修補。

| 情境：UI 最大延遲 | 修改前 | 首次修正後 | 最終修正後 |
|---|---:|---:|---:|
| 開啟資料夾並載入背景縮圖 | 52.09 ms | 29.05 ms | 1,054.97 ms |
| 背景載入中切換 121% | 83.87 ms | 45.00 ms | 129.54 ms |
| 背景載入中切換 177% | 247.21 ms | 5.57 ms | 1.82 ms |
| 背景載入中平移 | 9.42 ms | 38.04 ms | 20.32 ms |
| 停止背景載入後閒置 | 3.83 ms | 68.73 ms | 69.03 ms |
| 停止背景載入後切換 177% | 54.94 ms | 5.54 ms | 8.91 ms |
| 切換 300% | 4.92 ms | 23.94 ms | 2.05 ms |
| 回到 Fit 後閒置 | 0.24 ms | 7.80 ms | 2.66 ms |

RAW 工作量另以相同實拍 ARW 的三組重疊 Thumb／Preview 請求量測；後續語言修補未改動 RAW 路徑，也未重測這組效能數字。

| RAW 重疊請求指標 | 修改前 | 修改後 |
|---|---:|---:|
| 平均 managed 配置量 | 1,942,018,939 bytes | 1,306,378,467 bytes |
| 平均 CPU 時間 | 3,677.10 ms | 3,647.01 ms |

配置量降低約 32.7%，CPU 時間沒有可宣稱的明顯改善。來源鎖回歸已證明 Main／Full 縮放不再阻塞 UI，但整體 UI 延遲沒有一致改善；最終開啟資料夾仍觀察到超過一秒的最大延遲，原因未隔離，不能宣稱所有操作都更快。仍須讀檔與雜湊，不同照片的首次解碼也仍需執行；不能推論整批縮圖吞吐量提升或 CPU 不再忙碌。

原生驗證在可見、active 的 macOS 視窗以程式驅動，於 96 張照片的專案載入中取消背景工作（首次 48 張、最終 31 張），完成 121%／177%／300% 放大與平移，以及 12 次放大切圖。最終共觀察 102 次算圖，其中受限預算控制情境以外的 94 次全部使用 Metal，縮圖保留場景線性來源。300% 明確取得 4672×7008 原始畫框；所有舊 Full 弱參考在切圖後可回收，縮放期間取得的 Full 與最終 Fit 閒置後的 Full 也可回收。受限預算回退另用單張照片隔離驗證，來源檔案雜湊不變；畫面已檢視，不當成手動滑鼠／鍵盤驗收。

完整回歸首次抓到本輪引入的語言切換問題：快照若只保存已翻譯字串，切換英文後仍顯示中文 CPU 名稱。已改為不可變的雙語後端描述，讀取時依目前語言選擇，不重讀 live renderer；初始化也使用同一表示。受影響的 17 項在地化／狀態測試通過，最終原生視窗也驗證 CPU 回退原因即時切換中英文，回到 Fit 後恢復 Metal。

最終以 `dotnet test FilmBatch.sln -c Release --no-restore -m:1` 完成完整回歸：App 382、Core 850、RAW 643 項通過，合計 **1,875 passed／321 skipped／0 failed**。略過項目保留 Windows D3D12、未設定的外部 RAW corpus／oracle fixture 等實際前置條件，不把略過當成已驗證。

本機證據封存於忽略檔 `artifacts/verification/2026-10-25-cpu-workload-repair.tar.gz`，包含前後回歸、原生驅動程式、量測、來源版本與畫面，不含原始 RAW；私人畫面不上傳。Windows／DX12、iOS 原生與 CLI／匯出介面未另跑；共用來源與渲染路徑已實際執行，設定與專案格式未變。原生驅動初次編譯缺少 `Loupe` namespace，修正的是驗證程式，沒有更動產品來迎合檢查。

## 縮圖載入、放大 CPU／記憶體與 RAW GPU 卸載（2026-10-25）

以 `e97a972` 為未修改基線，針對兩個回報：載入資料夾時縮圖吃 CPU 與硬碟，以及放大看 RAW 細節時 CPU、記憶體偏高。量測先確認兩者在基線就存在，不是 `e97a972` 引入。以下八筆為逐項提交，皆未推送：

| 提交 | 主旨 | 內容與理由 |
|---|---|---|
| `d7a40bd` | `perf(thumbnail): use embedded RAW preview regardless of semantic masks` | RAW 啟用時 `IncludeSemanticMasks` 恆為真，使 `ImageLoader.Load` 的縮圖快路徑幾乎永不啟用，每張縮圖都整檔讀入並場景線性顯影。縮圖用不到語意遮罩，改為縮圖長邊一律先試內嵌預覽，取不到才回完整顯影。**契約改變**：RAW 模組（白平衡、鏡頭）調整不反映在濾鏡條縮圖，選取後的大圖不變；使用者已同意此取捨。 |
| `229ed42`、`682b7b5` | `perf(raw): cap Parallel.For degree…`、`…apply the shared parallel cap to ApplyColour` | 新增 `RawParallel.Options`（邏輯核心數的一半），`FilmBatch.Raw` 的 62 處 `Parallel.For` 與 `ApplyColour` 改用它。上限是每個迴圈各自的，不是整個程序的總量，且 `Engine` 其他平行迴圈未套用。 |
| `ce1064f` | `perf(preview): decommit GC memory after the idle Full release` | 一般 GC 與 `GCConserveMemory`、`RetainVM`、非並行 GC、`GCHeapHardLimit` 都無法讓私有記憶體下降；只有 `GCCollectionMode.Aggressive` 會把 GC 已空出的區段還給系統。接在 Full 閒置 3 秒釋放之後，於背景執行緒執行一次。 |
| `2edc0c6` | `perf(loupe): snap window scale to 1024px steps…` | 原計畫 B（窗口直接取自 Full、GPU 端下採樣）評估後放棄：畫框尺寸綁定來源尺寸，涉及降噪、遮罩、鏡頭中心等，且與現有 `RenderWindow` 逐通道 ≤1 LSB 契約衝突。改為窗口畫框長邊向上吸附到 `LoupePlan.FrameStep`（1024）的倍數，跨級後等 250 ms 才縮放。`OffThreadScalingTests` 的預期畫框由 1800×1200 改為 2048×1365，這是刻意的契約變更，**尚待維護者確認**。 |
| `f33d571` | `perf(cache): add in-flight decode memory budget` | 新增 `DecodeBudget`：預算 min(25% RAM, 1.5 GiB)，其中四分之一給進行中解碼；縮圖與預取等待，前景只登記用量不等。64／400／1700 MB 三個估計值來自單張量測，預算比例沿用 LightCraft，未由使用者確認。 |
| `be21bb9` | `perf(raw): run Bayer AHD demosaic on a dedicated GPU device` | 四個 WGSL pass（綠色內插、R/B 補齊加 Lab、同質性、選擇），以 256 列為一帶並含 6 像素反射邊界，與 CPU 磚塊演算法對應。解碼端自建獨立 `GpuDevice`；沒有硬體 GPU、小於 400 萬像素、非 Bayer 或任何 GPU 錯誤時退回 CPU，失敗一次後不再嘗試。使用者核可的容差為「殘差極小」。 |
| `6a8447f` | `perf(raw): run the scene colour finish on the GPU` | `ApplyColour`（白平衡、相機矩陣、描述檔 HueSatMap／LookTable、HueSat 修正）改為 512 列一帶的 compute pass，沿用同一個解碼用 GPU 裝置。 |

**未做項目。** 高光重建的 GPU 化：階段計時顯示它約 1.0–1.2 CPU 秒、牆鐘 0.15–0.2 秒，卻要上傳下載整張影像，判斷不值得。計畫 C 項的「解碼後提早釋放檔案位元組、顯影階段原地處理」：檔案位元組僅 35 MB 且後續仍使用，未找出 1.7 GB 暫時配置的來源。refit 迴圈（見下）依使用者指示不處理。

### 量測

環境：Windows 11、16 邏輯核心、RTX 3060（DX12）、Release 建置。輸入為使用者提供的 `demo` 資料夾（Sony ILCE-7M4，4 張 ARW 加 4 張 JPG，7008×4672）；App 流程只用 `DSC01858.ARW`，以腳本送出滑鼠／鍵盤輸入，取樣程序 CPU 時間與私有記憶體。每個設定只跑一次，不擇優；未用原本 355 張的大資料夾重測。基線資料夾用 `e97a972` 建置的 App。

函式層級（直接呼叫 `ImageLoader`／`RawImage`）：

| 項目 | 修改前 | 修改後 |
|---|---:|---:|
| 4 張縮圖，牆鐘 | 1.36–1.63 s | 0.22–0.23 s |
| 4 張縮圖，CPU 時間 | 8.1–11.3 s | 0.2 s |
| 4 張縮圖，讀檔量 | 137 MB | 11 MB |
| AHD 顯影，CPU 時間（單張 7008×4672） | 7.4 s | 1.2 s |
| AHD 顯影，牆鐘 | 1.08 s | 0.54 s |
| 完整 `LoadFull`，CPU 時間（AHD 與色彩收尾皆 GPU） | 10.0–10.8 s | 3.0–5.2 s |
| 完整 `LoadFull`，牆鐘 | 1.5–1.7 s | 1.15–1.45 s |
| Full 解碼後強制一般 GC，私有記憶體 | 約 1330 MB | 約 1230 MB |
| Full 解碼後 Aggressive GC，私有記憶體 | 約 485 MB | 約 420 MB |

單獨量 Full 解碼階段（GPU 之前）：`ApplyColour` 牆鐘 0.35 s、約 2.8–3.0 CPU 秒，為最大單項；高光重建約 1.0–1.2 CPU 秒；去馬賽克（CPU）約 7 CPU 秒。僅把並行度減半，Full 解碼 CPU 總量幾乎不變（約 17 s 對 15–18 s）、牆鐘由 1.45 s 增至 2.4–2.6 s，只是攤到較少核心。

GPU 與 CPU 結果比對：AHD 在 7008×4672 上平均相對誤差約 2e-6，0.0037% 的樣本相對誤差超過 1e-4，最大絕對誤差 0.021。完整 `LoadFull` 的 16-bit 顯示輸出共 1.31 億個樣本，差異超過 256（滿值 65535）的有 1,595 個（約 0.0012%），最大差 9248，推測為 AHD 同質性平手時選到另一個方向；未逐點確認。

App 流程（選取、按 1:1 按鈕、平移、滾輪放大 6 格、`Ctrl+0` 回到符合視窗並閒置）。表內為各階段的 CPU 時間（秒）與私有記憶體（MB）：

| 階段 | 基線 | 前幾項（至 `f33d571`） | 加 AHD GPU | 加色彩收尾 GPU |
|---|---:|---:|---:|---:|
| 選取，CPU 時間 | 13.4 | 9.9 | 9.8 | 9.5 |
| 1:1，CPU 時間 | 24.6 | 22.0 | 16.5 | 12.9 |
| 1:1，平均 CPU（全核心） | 19% | 17% | 12.5% | 10% |
| 滾輪放大，CPU 時間 | 6.6 | 1.6 | 1.7 | 1.8 |
| 整個流程私有記憶體峰值 | 9,799 | 7,615 | 7,644 | 7,786 |
| 1:1 階段私有記憶體峰值 | 4,470 | 4,840 | 5,061 | 5,377 |
| 回到符合視窗閒置後 | 7,424 | 2,102 | 2,331 | 2,210 |

1:1 階段的記憶體峰值反而上升，是 GPU 緩衝區與獨立裝置的代價；放大後的整體峰值仍低於基線。基線整個流程沒有記憶體歸還。

### 測試結果

- 新增或改寫：`RawEmbeddedThumbnailTests`（遮罩不再繞過內嵌預覽，並新增縮圖遮罩案例）、`LoupePlanTests` 的 `QuantizeFrameEdge`、`DecodeBudgetTests`（4 項）、`GpuAhdTests`、`GpuColourTests`（2 組參數）。`OffThreadScalingTests` 預期值修改如上。
- 完整來源回歸：`dotnet test FilmBatch.sln -c Release`（未加 `-m:1`），**Raw 645 passed／295 skipped、Core 831 passed／47 skipped、App 386 passed／0 skipped，合計 1,862 passed／342 skipped／0 failed**（總數 2,204）。Raw 與 Core 的略過原因未逐項檢視，依 VERIFY.md 推定多為未設定的外部相機語料。
- V2（WGSL 以 Naga 驗證）**未執行**：本機沒有 `cargo` 與 `naga`。五個新 shader（`raw/ahd_*.wgsl`、`raw/apply_colour.wgsl`）只通過 Dawn 的實際編譯，不能記為通過。第一版 `apply_colour.wgsl` 曾有 WGSL 語法錯誤（向量比較不能直接用 `&&`），導致共用的解碼 GPU 裝置建立失敗、AHD 也退回 CPU；已修正。
- `GpuAhdTests`、`GpuColourTests` 在沒有硬體 GPU 時直接 `return`，會顯示成通過而不是略過；這台機器有 GPU，所以實際執行了比對。容差（相對 1e-4／1e-3、差異樣本比例 <0.1%）是看過實測差異後訂的，不是事先固定的契約。

### 使用者的決定

| 議題 | 選項 | 使用者選擇 |
|---|---|---|
| 縮圖來源 | 保持場景線性顯影／改用內嵌預覽 | 改用內嵌預覽，接受 RAW 模組（白平衡、鏡頭）調整不反映在濾鏡條縮圖 |
| 問題 2 範圍與提交方式 | A、B、C、D 是否全做 | 四項全做，盡量交給 GPU；每項各自提交，不合併成一個大提交 |
| A 項並行度上限 | 逐處傳入共用 `ParallelOptions`／`AsyncLocal` 環境式上限／只改最耗 CPU 的幾處 | 逐處傳入共用 `ParallelOptions` |
| B 項做法（兩次決定） | 原案（GPU 端下採樣 Full）與級距量化＋停下才縮放 | 先選 GPU 端下採樣；我讀完程式說明範圍與風險後，改選級距量化＋停下才縮放，並接著做 D 項 |
| GPU 卸載先後 | 先做／先不做 | 一度要求先不做、先用 demo 實機量測；量測後要求做 GPU 卸載 |
| GPU 像素容差 | 殘差極小（相對誤差約 1e-4，平手像素 <0.1%）／必須逐像素一致 | 殘差極小 |
| GPU 裝置 | 解碼專用獨立裝置／共用算圖器的裝置 | 解碼專用獨立裝置 |
| 實機量測 | 由使用者手動操作／由我自動操作 | 由我自動操作取樣（選取、1:1、平移、滾輪） |
| 高光重建 GPU 化 | — | 使用者要求「做高光重建的 GPU 化以及後續步驟」；我依階段計時（約 1.0–1.2 CPU 秒）判斷不值得，改做 `ApplyColour`。這是我的取捨，沒有再詢問使用者 |
| refit 回授迴圈 | 修／不修 | 不修，由其他貢獻者處理 |

### 遇到的問題與處理

- **並行度沒有共用入口：** `FilmBatch.Raw` 有 63 處 `Parallel.For`，沒有共用的 `ParallelOptions`。先停下詢問，才決定逐處傳入；機械式替換實際改了 62 處，另一處是註解文字。
- **GC 設定無效：** 原計畫以 `GCConserveMemory` 等設定歸還記憶體。實測 Full 解碼後託管堆只剩約 397 MB，私有記憶體仍約 1.6–1.7 GB。`GCConserveMemory` 只把堆峰值由約 1.75 GB 降到約 1.08 GB，其他設定無效。以 `VirtualQuery` 確認多出的記憶體在 GC 的保留範圍內，最後只有 `Aggressive` 模式會歸還。
- **B 項原案行不通：** 窗口畫框尺寸與來源尺寸綁定（`ModuleParams`、`CropGeometry`、顆粒、鏡頭中心、前景／語意遮罩、降噪 `Prepare` 都由來源尺寸算起），GPU 來源紋理為 `Rgba32Float`（Full 約 524 MB），且 Box 縮小與雙線性取樣的像素會不同，並與 `RenderWindow` 的 ≤1 LSB 契約衝突。停下說明後由使用者改選級距量化。
- **級距量化引起的測試失敗：** `OffThreadScalingTests.Window_scale_runs_off_ui_and_covers_the_latest_pan` 因畫框由 1800×1200 變 2048×1365 而失敗，屬預期，更新預期值（見上，待維護者確認）。
- **看似回歸的 1×1 灰色預覽：** 自動化第一次啟動時預覽停在灰色 1×1 畫面。對照基線版本正常後，發現我啟動的是舊的 `win-x64` 輸出，另一個目錄才是剛建置的新版；改用新版後正常。這不是產品問題。
- **自動化腳本問題：** PowerShell 的 `[math]::Max` 用 `Int32` 在私有記憶體超過 2 GB 時溢位，改用 `[long]`；`1` 鍵沒有觸發 1:1，改點按鈕；回到符合視窗誤點到分割對比按鈕（按鈕位置會隨縮放文字位移），改用 `Ctrl+0`。這些都是驗證腳本問題，沒有改產品。
- **WGSL 語法錯誤：** 第一版色彩收尾 shader 使用向量比較搭配 `&&`，Dawn 拒絕編譯，連同 AHD 一起退回 CPU，這次實際驗證了退回機制，但也暴露兩個功能共用裝置、一個 kernel 失敗就全部停用的設計缺陷（見下）。
- **xUnit 分析器錯誤：** 新增的 `DecodeBudgetTests` 因 `xUnit1051`（呼叫需傳入 `TestContext.Current.CancellationToken`）無法編譯，已修正。
- **完整測試首次逾時：** 一次完整的 App 測試在 10 分鐘上限被移到背景後沒有輸出，相關背景任務在工作階段結束時被標為停止；之後改跑相關子集，最後以 Release 完整循序重跑通過。
- **暫時性檢測碼：** 為量測階段耗時，曾暫時在 `Highlight.cs`、`RawSourceLoader.cs` 加入計時輸出，量完以備份還原，沒有提交。

### 已知問題與未結案

- **程式審查發現（未修）：** (1) `ImageCache.DecodePreviewBackgroundAsync` 先設 `Started = true` 才等記憶體預算，前景請求會共用這個可能被預算擋住的背景解碼，造成優先權反轉。(2) 解碼端 GPU 裝置不遵守 `ForceSoftwareGpu`（含 `--software`）、後端選擇與 `GpuMemoryLimitMb`；批次匯出也走同一條解碼，使結果在有無 GPU 的機器上有微小差異，並會污染 VERIFY V4 的 CPU／GPU 比對。(3) 並行度上限不是程序級總量。(4) 每次 Full 釋放都做封鎖式 Aggressive GC，且沒有 Full 時也會呼叫。(5) 兩個 GPU 測試靜默通過。(6) 新 shader 重寫了 `common/color.wgsl` 已有的部分函式，也沒採用 `raw_finish_math.wgsl` 保留 f32 捨入點的做法，目前靠容差過關，跨廠牌 GPU 未驗證。
- **文件偏差：** `architecture.md` 仍寫「RAW 縮圖（長邊 ≤240，且不需語意遮罩）」，與 `d7a40bd` 不符；來源縮放一節也未提級距量化；GPU 去馬賽克與色彩收尾尚無文件。
- **refit 回授迴圈（不屬本輪改動，未修）：** 符合視窗且畫布受高度限制時，`PreviewCanvas` 以已取整的 bitmap 長寬比算 `drawn`，`OnPreviewViewSizesChanged` 以精確相等比較，造成 1708 ↔ 1706 像素的兩點震盪，每次都觸發一次 Lanczos 縮放，CPU 持續偏高直到放大。程式碼位於 `e74cb72`／`bd2405b`（2026-10-23），本輪沒有修改這些檔案。使用者指示由其他貢獻者處理；我的測試視窗長寬比剛好吻合，沒有重現，是由程式碼推導。
- **未驗證：** Windows 以外平台、沒有 GPU 的機器、其他相機的 RAW、355 張的大資料夾、多張照片同時解碼時的 GPU 記憶體；完整 App 測試首次嘗試（含 Debug 子集）曾逾時，後以 Release 完整重跑通過。沒有重新封裝或推送。

## 如何新增歷史列

只記對未來修改有用的決定：真實 commit／日期、可核對的理由或重現、被拒絕方案、目前契約／檢查、後續取代。沒有當時證據時明寫「只確認提交意圖」；數值必須保留平台、輸入與來源版本，不把舊測試 totals 複製成新版本的驗收結果。

不保存原始對話、推理逐字稿、機器絕對路徑或憑證；不把某次「可 commit／push」的批准延續為今天的權限。
