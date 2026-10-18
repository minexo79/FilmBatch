# RAW Pipeline Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. User selected subagents. Parent owns integration and verification. Latest user instruction authorizes grouped commits only after double check passes; no push.

**Goal:** 實作前次 LightCraft 對照中列出的全部 RAW 整合落差，交付可實際編輯／匯出的 Mac 應用程式，而不是新增一組未接線的底層 API。

**Architecture:** RAW 來源保留場景線性 Rec.2020 與相機色彩／曲線／幾何資訊；曝光、相機白平衡、遮罩及線性效果不再從已量化 SDR 還原。CPU 與 GPU 在顯示效果階段前才完成 RAW tone/gamut/display transform；一般 JPEG/PNG/TIFF 的既有路徑不變。鏡頭校正、gain map、semantic masks 共用同一個輸出位置→有效 RAW 位置映射。

**Tech Stack:** .NET 10、Avalonia 12.1.4、CommunityToolkit.Mvvm 8.4.2、WebGPUSharp 0.5.7、WGSL、Magick.NET 14.17.2、xUnit v3；沿用現有 C# RAW／色彩實作，不引入另一個 RAW engine。

**Spec:** 本文件「需求、使用情境、驗收」為 spec v1；使用者要求「實作全部，先 Plan For Subagents，並設計實驗與 Review Criteria」。前次可重現證據在 `dist/raw-lightcraft-comparison.json`，上游固定 `storytold/lightcraft@4f0b110bcbd2e350c6d97b47f1d824c2222295c4`（v0.9.1）。

## Global Constraints

- 實作八項需求，不能把 semantic-mask／Kelvin／Lensfun／JXL 部署默認排除。沒有增加全新 AI 分割模型的需求：此處使用檔案內既有 semantic mattes。
- 保留既有七個 staged RAW UI 修正；不 reset、stash 或變更其他 worktree。相關檔案的新增修改疊在現況上，不覆蓋原修正。最新使用者指示允許 parent 在全部驗收與 double check 通過後分批 commit；仍不 push。
- 不寫使用者原始照片；所有實驗使用自建／下載並核對 SHA-256 的副本及隔離 AppPaths。匯出繼續拒絕覆寫來源與既有目的檔。
- 正式封裝支援目前 Build.cs 的 `osx-arm64`／`win-x64`；Mac 實際執行驗證，Windows 靜態／交叉發佈及 CI smoke 的執行界線分開記錄。不得把未執行的 Windows CI 宣稱通過。
- 不新增 managed 框架／重試層／泛用狀態框架；原生 JXL 相依性必須有固定版本、來源與授權。
- 不夾取場景線性資料到 [0,1]；不得為符合舊錯誤輸出而放寬容差。色彩非有限值在既有合法邊界正規化，錯誤輸入不改拿 JPEG 假裝 RAW。
- 來源資料不可被 render／downscale／白平衡／遮罩編輯原地改寫。每個 render 的工作緩衝區可變；來源與 GPU cache identity 穩定。不可每像素配置物件／陣列。
- 不同域要明確：來源為 Rec.2020；若為保留 FilmBatch 效果而採線性 sRGB 工作域，必須是未夾取的浮點轉換；RAW finish 必須使用正確色域，不能直接把 Rec.2020 當 sRGB。
- 不要求已知失敗再跑一次確認：前次四個重現為既有 red 證據。新不確定行為用消費者可見的測試／實驗，不測原始碼文字或接線回音。
- 所有子代理中途不跑 build/lint/test/formatter；先寫行為迴歸，再實作。Parent 在整合點集中執行，避免共用 bin/obj 的競爭。
- 所有新中文 UI 使用正體中文（臺灣），英文 resx 同步；所有入口保留 undo/redo、save/reopen、切換照片、取消與 stale decode 防護。

## 需求、使用情境、驗收

| Requirement | 使用者結果與驗收界線 | Owner | 實驗 |
|---|---|---|---|
| REQ-RAW-01 | RAW 亮部在降低曝光後保留不同階調；CPU／GPU／預覽／全尺寸匯出共用浮點來源及晚期 finish；一般影像不變 | ScenePipeline + parent | EXP-01、EXP-09 |
| REQ-RAW-02 | CR2/NEF/ARW 等缺少檔內矩陣者轉 DNG 時補足相機色彩、look／tone，保留感光樣本及幾何／EXIF；DNG 可重新開啟且外觀不再因少 tags 改變 | DngColour | EXP-02 |
| REQ-RAW-03 | 鏡頭 warp＋gain map 使用同一個 RAW 位置；crop／orientation／binning 與 DNG 1.6 鏈不錯位、不重複套用 | LensGeometry | EXP-03 |
| REQ-RAW-04 | 有 ProfileGainTableMap 的 DNG 不再額外擬合全域 JPEG tone；合法原有 tone／look 保留 | DngColour | EXP-04 |
| REQ-RAW-05 | RAW 可選 As Shot／Custom Kelvin＋tint；有相機矩陣時重算相機白平衡，無可用矩陣時使用明確的色適應；舊相對暖色不被誤讀為 Kelvin | WhiteBalance + RawControls | EXP-05、EXP-09 |
| REQ-RAW-06 | App 與 CLI 的 JXL RAW 解碼不依賴開發機 Homebrew／系統 libjxl；固定 native ABI，缺失／不相容時明確報錯 | PortableJxl | EXP-06 |
| REQ-RAW-07 | UI 列出實際內嵌 semantic masks，選取後作局部曝光、色溫／色偏、對比／飽和度與反轉／強度；位置對齊 warp／crop／orientation；CPU/GPU 與匯出可見；保存／復原可用 | SemanticMasks + RawControls | EXP-07、EXP-09 |
| REQ-RAW-08 | 可指定外部 Lensfun-format 目錄；以 make/model/lens/focal/aperture/crop factor 找到並內插適用校正；內嵌校正優先、不重複套用；未知／缺少資料不亂套其他鏡頭 | LensGeometry + RawControls | EXP-08、EXP-09 |

### 使用情境

- 開 RAW，調低曝光：原本白色高光重新出現階調；切 CPU／Metal、匯出 PNG16，階調與畫框一致。
- RAW 面板選「自訂白平衡」，輸入 3200 K／色偏；undo/redo，儲存專案再開啟，結果相同。普通 PNG 不顯示 RAW 專用控制；既有 basic 相對暖色仍是相對效果，不暗中換單位。
- RAW 有 sky/person 等 matte：下拉選實際名稱，調局部曝光與飽和度；遮罩以外保持不變。套用到沒有該 matte 的照片不全圖作用；保留設定並顯示不可用狀態，不自造遮罩。
- 指定 Lensfun 資料夾：已驗證內嵌校正優先，只有缺少它時才匹配外部描述檔。不自動下載或隨程式分發 Lensfun 資料庫。
- CLI `raw convert <input.CR2> <output.dng>`：輸出有可用色彩描述，重新開啟；CLI `export <project-folder> --format png --backend Metal` 使用保存的 RAW／WB／mask 設定。失敗不破壞來源，不把缺失原生庫當成功。

## 共用介面與檔案所有權

### 整合契約

1. **ScenePipeline owns SourceImage and RAW source loading.** `SourceImage` 保留既有 raster constructor；RAW 另有明確的浮點場景資料與 immutable-per-source metadata。`Rgba16` 只能是顯示用表示，不得再作 RAW 編輯輸入。原圖比較若需要顯示副本，可延遲產生／快取，匯出不得預先做無用的整張 SDR encode。
2. **WhiteBalance owns camera-white model.** 新 `Imaging/Raw/RawWhiteBalance.cs` 定義 `RawWhiteBalance`，公開 `AsShotTemperature`／`AsShotTint` 供 UI；以既有 `Cct`／`CameraColor.Rebalance` 計算 Rec.2020 的 `Mat3`。建立時接收 ColorData、developed-for Xy 與是否可重算 camera matrix，不保留感光大陣列。ScenePipeline 在來源中保留它。新設定固定 `raw.wb_mode`=`as-shot|custom`、`raw.temperature`=1000..50000 K（預設6500，僅 custom 生效）、`raw.tint`=-150..150。
3. **LensGeometry owns output→RAW map.** 新 `RawSourceGeometry` 由 RawImage metadata 與實際套用的 List3 lens ops 建立，`Active(double u,double v)` 將已轉正完整來源的正規化位置映回有效 RAW 的正規化位置。支援所有實際套用的 warp 類型／串接／切向項，不能只拿 camera-fit 的第一個綠平面近似代替。ScenePipeline 把 geometry metadata 與來源一起保留；SemanticMasks 消費同一介面。
4. **LensGeometry owns database fallback.** 新 `LensProfiles` 解析外部 XML，重用現有 WarpRectilinear／vignette 路徑。`ApplyFallback(RawImage raw,string? directory)` 在 lens snapshot／decode 前呼叫；目錄取 `raw.lens_database`（相對專案／照片資料夾解析）或 `FILMBATCH_LENSFUN_DIR`。有已驗證內嵌校正時不覆蓋。成功匹配才加校正；不讀取 XML 外部實體。
5. **SemanticMasks owns rendering helper, not shared renderer files.** 新 `RawSemanticAdjustments` 及 `raw_semantic_adjust.wgsl`。使用原始 mask 的 Name/InstanceId，來源 geometry 與 `CropTransform` 取正確位置；遮罩控制保存在 `raw.mask_*` 設定，CPU helper 原地處理 render 工作緩衝，不更動來源。GPU shader 由 ScenePipeline 接到相同階段，不以 CPU 結果冒充 GPU。
6. **RawControls owns Core schema and App controls.** `RawLoadOptions` 擴充資料庫位置與 semantic-mask 讀取；它是來源快取鍵。WB／局部調整是 render 設定，不因改 slider 重解碼。提供可用 masks、單位與 unavailable 狀態；新增 raw keys 沿用 ProjectService／Settings／undo 儲存。`ParamDef.Path` 與目錄／mask id 的語意須明確，不能把 arbitrary mask 名稱塞進會被固定 choice coercion 丟棄的參數。
7. **Parent owns ModuleParams.** 子代理回報需要的欄位／方法；不得各自改這個共用檔。`RawWhiteBalance`、semantic render parameters、raw-enabled flags 在這裡統一解析。Parent 也擁有 BatchExporter 與最終 docs／experiments，確保來源縮放保留場景域，輸出只 finish 一次。
8. **DngColour owns CameraPreview.cs / DNG synthesis and CLI RawCommands.** 新 helper 合成 upstream equivalent starting colour，不改 A 的 loader；以 helper 供 CLI decode→prepare→write。四項原本載入 look-fit／測量 fallback／檔內矩陣分支都保留。擬合失敗不冒出假 ColorMatrix。
9. **PortableJxl owns native dependency files/build/CI/notices.** 固定版本 native 包或可重現建置，發佈兩個 RID 到 App／CLI 的正確目錄；Mac 相依載入路徑及簽章處理在簽章前完成。不可去讀使用者系統庫來掩蓋缺少封裝的相依性。

### Dispatch map

| Subagent | Writable ownership | Dependencies / handoff |
|---|---|---|
| ScenePipeline | SourceImage.cs、ImageLoader.cs、RawSourceLoader.cs；CpuPipeline.cs、GpuEffectPipeline.cs、GpuSpikeRenderer.cs；RawDisplay/RawDisplayGpu／相關 finish shader；RawSceneIntegrationTests、所影響的 source/GPU consumer tests | 消費 WB、geometry、mask helper；先發回 SourceImage metadata 介面供其他 slice 對接 |
| DngColour | CameraPreview.cs、CameraPreview DNG synthesis partial、新 RawDngConversion.cs、Cli/RawCommands.cs、DNG colour/tone 行為 tests | 與 loader 僅共用既有 StartingColour signature；不碰 shared loader |
| LensGeometry | CameraLocalTone.cs、新 RawSourceGeometry.cs、LensProfiles／lensdb parser/model；RawLens／RawLocalTone／LensProfile tests | 提供 geometry 與 ApplyFallback 給 ScenePipeline／mask；不編輯 shared loader |
| WhiteBalance | 新 RawWhiteBalance.cs、必要色彩 helper 與獨立 WB tests | 提供 AsShot 公開 metadata 及 Rec2020 matrix；不碰 SourceImage／ModuleParams／UI |
| SemanticMasks | 新 RawSemanticAdjustments.cs、semantic WGSL、獨立 mask render tests | 消費 geometry，與 ScenePipeline 協商 helper／shader binding；不編輯 CPU/GPU shared pipeline／Core schema |
| PortableJxl | Raw/Codecs/JxlNative.cs、Raw csproj、Directory.Packages.props、build JXL packaging、Build.cs、CI.yml、THIRD-PARTY-NOTICES、JXL tests | 唯一新增 native 相依性 owner；不 publish/build，parent 統一執行 |
| RawControls | ModuleCatalog／必要 ParamDef 支援、RawLoadOptions、App RAW 控制／VM／XAML／resx 與 UI tests | 保留既有 staged 修正；消費 SourceImage WB metadata／masks；向 parent 發回 exact settings keys |
| Parent | 此計畫、ModuleParams.cs、BatchExporter.cs、跨實作實驗與桌面 smoke、文件整合、最終 build/publish | 不搶改 child-owned 檔；最後整合、驗證與獨立 review |

## 實作步驟與檢查點

- [x] P1 固定 spec／介面／所有權，保存原始 staged 狀態與前次 red 證據；公開實驗與 Review Criteria。
- [x] P2 一批 dispatch 七個實作 slice。各 slice 先寫可見行為的迴歸／邊界測試，再實作；不跑共享建置，遇到共用介面需求先傳給 owner。
- [x] P3 Parent 已完成 ModuleParams／匯出與六個續接 slice 整合；獨立 binary TIFF fixtures、固定 Rust oracle 與 C# acceptance runner 已建立並執行。
- [x] P4 完成 targeted tests 與真 CPU／Metal／桌面實驗。修正 DNG EXIF／DefaultScale、Lensfun 半徑／gain 順序／f32 取樣、單像素遮罩與非同步生命週期；彩色高光新增 red=38→green≤1 RGB16 的收尾色域迴歸。
- [x] P5 完成獨立數學／色彩／UI 狀態與原生部署 review。有效問題已修正；最終色彩與 JXL reviewer 無阻擋 finding。OOM 操作例外優先傳播，Windows 跨 RID 發佈缺庫已 red→green；實際 Windows／強制物理 OOM 不宣稱驗證。
- [x] P6 更新既有 architecture/building/RAW docs；補修後完整 solution、29 個 WGSL 與重建 Mac `--smoke --zip` 通過；已檢視最終畫面、驗證解壓後 CLI／簽章／JXL。原 14 組對照另存為補修前證據，本次重跑全幅／偏心裁切及兩種 JXL 封裝情境，不混用舊 ZIP 驗證結果。

## 實驗設計（EXP）

Authoritative reference 是固定上游程式、既有數學與原始照片 SHA；不修改上游來配合本地。新 synthetic fixtures 是 developer oracle，必須保留公式／手算期望；同一 helper 算 expected 與 actual 不算獨立對照。先前測得數值用於定位，不作放寬容差的理由。

| Experiment | Given / When | Expected / Failure signal | Runnable check / evidence |
|---|---|---|---|
| EXP-01 高光可恢復 | 96x16 own-matrix F32 DNG，灰階 .025/.075/.15/.3/.5/.8，BaselineExposure +2；曝光0與-2，CPU／Metal／預覽／PNG16 | 來源 max3.2；-2後三個高光不同且遞增；與固定 Rust 原始中性／-2色階 max≤2 RGB16 codes（先核對相同曲線／量化）。GPU/CPU RAW中性／曝光 max≤2，複合既有效果沿用原定容差；alpha全滿 | RawSceneIntegrationTests；隔離 Rust/C# 同檔比較，輸出各灰階碼值及 source immutability hash；前次 red=max21306 |
| EXP-02 DNG roundtrip | CC0 Canon40D CR2 SHA775c8063…58067be；另測已有矩陣／look 的 DNG、缺 JPEG 的 RAW | 真 CLI convert 有可用 matrix及look/tone（若來源合成成立）；sample hash、crop/orientation/WB/EXIF保留；轉前後場景／顯示的誤差按同色彩模型比較；已有描述不覆蓋，無資料不填假值；目的已存在拒絕 | 新 DNG colour tests＋packaged CLI convert/reopen；列 tags及hash，不以「檔案存在」當通過 |
| EXP-03 gain座標 | 64x64常數.05、barrel-.35、gain1→3；lens/tone四組；加非中心crop、八方向、下採樣與WarpRectilinear2鏈 | 四組對照一致；同一RAW位置的gain在各解析度／方向一致，線性 max≤1e-5；不重複warp／vignette | RawLocalTone／RawLens tests與Rust plan.frame對照；前次combined max.01052898 |
| EXP-04 tone gate | 原始 Apple ProRAW SHAe91e77a4…dbdfc2、只把257點tone設identity的副本；另有無gain／非identity／LookTable分支 | gain存在時不作額外JPEG全域fit；identity副本tone(.18)=.18±1e-6；原始樣本維持.12257147；無gain合法fit仍可用 | DNG tone eligibility消費者測試、實際load；原始輸入SHA不變 |
| EXP-05 Kelvin WB | 有雙矩陣／單矩陣、測量fallback／無矩陣；AsShot、自訂3200/6500K、正負tint | 與Rust wb_matrix_for相同基準的線性色塊 max≤1e-5；AsShot不改色；灰階亮度遵守上游 normalization；不把legacy ±100讀成Kelvin；多次render不改來源 | RawWhiteBalanceTests；保存／reopen／undo與CPU/Metal matrix對照 |
| EXP-06 portable JXL | 發佈App與CLI，含固定16-bit／float JXL DNG；移到隔離目錄，清除DYLD/loader相關環境，確認不含Homebrew依賴 | 封裝內解碼sample hash等於uncompressed oracle；native依賴閉包全在包內／OS標準庫；庫缺失／ABI不符時明確失敗；不能假通過或退回JPEG | JXL decoder tests；解壓後真CLI inspect/export；otool/dumpbin與manifest；Windows runtime僅在實際CI執行後可標verified |
| EXP-07 semantic local edits | 半幅／軟邊mask、MaskSubArea、sky+person多mask／instance，裁切／翻轉／90°／warp、無mask照片 | mask=0像素不變，mask=1符合手算EV倍率，.5權重內插、invert互補；正確sample位置；非選取mask／缺失mask不全圖改變；CPU/GPU輸出一致；設定保存／undo不串照片 | RawSemanticRenderingTests；實際UI選mask調整、存檔、CLI匯出及PNG16像素取樣 |
| EXP-08 Lensfun | 手工獨立XML涵蓋poly3/poly5/ptlens、TCA／PA vignette、焦距/光圈內插、crop body及35mm crop mode、未知鏡頭／XML外部實體 | 人工彎格線校正後曲率<1px且比未校正少8倍；係數對上游lensdb；匹配make/model/mount/crop，內嵌優先且一次；無設定不變；外部實體不讀取 | LensProfileTests；實際RAW metadata＋資料夾設定load／export；不使用網路資料庫作唯一測試 |
| EXP-09 end-to-end與安全 | 真App：RAW→PNG→RAW、RAW/film頁WB/mask/校正→undo/redo→save/reopen；pending crop時改RAW選項；批次匯出期間改UI設定 | 來源／快取與設定一致；不回傳stale bitmap；既有Cancel/同資料夾reopen保護仍成立；匯出使用不可變snapshot；原檔SHA不變；CPU fallback有真實原因 | focused app tests＋實際Avalonia/Metal桌面probe、畫面檢視、實際CLI與輸出圖；完整solution tests、naga、packaged smoke |

### 實驗執行命令

- `dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release --filter FullyQualifiedName~Raw`
- `dotnet test tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj -c Release --filter FullyQualifiedName~Raw`
- `dotnet test FilmBatch.sln -c Release`
- `dotnet run build/CheckShaders.cs -- --naga <pinned-naga-30.0.1>`
- `dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip`
- 解壓後執行 `cli/filmbatch-cli raw convert`／`export` 真實情境；以 `codesign --verify --deep --strict`／`unzip -tq` 驗證封裝。
- 命令中的執行檔路徑與fixture根由實驗環境提供；檔案與test symbols建立後，parent把精確路徑及結果寫入本文件 State Ledger／ignored dist 證據，不將上述設計命令當已執行。

## Review Criteria（RC）

| Criterion | Reject conditions | Review evidence |
|---|---|---|
| RC-1 完整覆蓋 | 任一需求只在parser/helper成立、沒接至實際使用入口；把fallback或stub稱完成 | 八需求→實驗→direct/alternate/cache/persisted/doc consumer map |
| RC-2 數學與色彩域 | SDR提前夾取／二次tone、Rec2020/sRGB誤認、WB採相對倍率冒充Kelvin、mask/lens空間錯位 | 固定Rust數值＋獨立手算fixtures；neutral／邊界／複合controls |
| RC-3 GPU誠實性 | Metal路徑靜默CPU化；結果仍標GPU；只修CPU或依賴fused fma正確性 | 真Metal裝置與渲染記錄、CPU/GPU差、OOM/尺寸fallback、naga |
| RC-4 資料與非同步安全 | 原始資料被改、同來源多次render不同、stale decode蓋新照片、保存／undo／batch snapshot丟新欄位 | 原檔/場景hash；switch/undo/save/reopen/export snapshot桌面證據 |
| RC-5 部署及供應鏈 | Homebrew依賴被當內附、native ABI未固定、transitive dylib缺席、missing library被吞掉、未列license | archive解壓後實跑JXL；native依賴閉包與manifest、notices／CI |
| RC-6 測試與證據誠實 | 改預期值迎合錯誤；duplicate path測試灌數；mock echo／原始碼文字測試；拿skipped當pass；用不相同曲線比較後擴容差 | red evidence、新增test的consumer-visible break、最終exit及對應輸出 |
| RC-7 六個月可維護性 | 新泛用框架、雙套幾何／finish、obsolete finisher與shim留存、每像素配置、無上限XML／外部實體 | ownership與twin sweep、熱路徑與配置／lifetime檢視、明確外部資料邊界 |
| RC-8 原有功能 | 非RAW輸出改變、底片頁被重新藏掉／預設展開回歸、crop取消與同資料夾reopen回歸、舊相對設定失去單位 | 既有行為測試＋實際桌面與一般影像對照；不保留純實作／文字測試 |

獨立 reviewer 不寫產品程式、不跑共享建置。Parent 先讀當前程式重現有效 finding 再修；舊 finding 必須重新定位，不能盲套歷史行號。Review 的 Approve 綁定被檢視的最終內容；後續行為變更使相關決議 stale。

## Consumer map / review focus

- Direct: ImageLoader／RawSourceLoader、Cpu/Gpu renderer、CLI raw convert／export、App RAW controls。
- Alternate: RAW module disabled、as-shot WB、missing matrix/JPEG/gain/mask/lens db、CPU-only、GPU limits/OOM、rendered PNG/TIFF、crop compare-original。
- Cached/generated: ImageCache RawLoadOptions keys、film/look thumbnails、original histogram／display copy、GPU source textures／uniforms、generated localization／MVVM binding。
- Persisted: photo overrides、presets、copy/paste與selective paste、project snapshot／undo、batch export jobs、native publish assets。
- Docs: architecture/building／raw-oracle scope／notices，保留前次staged內容。
- Non-goals: 新AI segmentation模型、全套LightCraft UI移植、改掉FilmBatch的藝術效果；未修改RAW預覽失敗時明確拒絕而非JPEG替代的產品政策；額外Linux正式封裝；Developer ID公證／push。

## Risk / authority decision

Go：使用者已授權實作，變更限本地原始碼、隔離fixtures與可再生dist。沒有生產部署、帳號／權限、資料庫migration、付費服務或私人影像外傳。撤回方式是逐檔撤回本次差異，保留原先staged修正；不得以reset/clean實作rollback。公開上游／native套件僅作資料與依賴來源，不作額外授權。新外部XML拒絕DTD與外部實體，原生庫只載入預期的套件路徑。

## State Ledger（spec v1）

| Item | Status | Evidence |
|---|---|---|
| Scope / baseline / staged ownership | verified | 初始git status：7 staged files，無其他工作樹變更；既有dist對照證據已讀 |
| Plan for subagents / experiments / review criteria | done | 本文件，尚不是實作或測試通過證據 |
| REQ-RAW-01 scene-linear source / late CPU-GPU finish | verified | EXP-01 來源 max3.2，曝光0/-2、CPU/Metal/Rust RGB16 max0；65,536 彩色樣本 max1；灰階不代替彩色邊界 |
| REQ-RAW-02 DNG starting colour | verified same-model; documented format boundary | 真 CLI Canon40D 轉換保留 sample SHA、幾何與拍攝 EXIF；對上游實際轉出的 DNG：線性 max1.49e-8，CPU/Metal/Rust RGB16 max1。原始 JPEG 擬合模型與標準 DNG 不同，外觀差 max2460／mean105.34；不放寬門檻假裝相同 |
| REQ-RAW-03 shared warp / gain geometry | verified | lens×tone 四組全圖線性對固定 Rust max0；crop／方向／binning／WarpRectilinear2／鏈式逆映射有永久迴歸 |
| REQ-RAW-04 gain-table tone exclusion | verified | 原始 Apple ProRAW tone(.18)=.12257147；僅改 identity tone 副本=.18；與固定上游一致，原始 SHA 不變 |
| REQ-RAW-05 Kelvin / tint | verified | 四組 WB matrix 對 Rust max4.44e-16；CPU/Metal/Rust RGB16 max1；原生 bound 3200 K、不重新解碼、undo/redo/save/reopen 通過 |
| REQ-RAW-06 portable JXL | verified on extracted Mac; Windows static/cross-publish only | 乾淨環境的解壓 CLI 成功轉換／匯出16-bit與float JXL DNG；sample bytes逐位元等於獨立jxl-oxide 0.12.6；隨附0.11.2／ABI11002。Windows GUI/CLI各10 DLL、20份SHA皆等於manifest；實際 Windows runtime 未執行 |
| REQ-RAW-07 semantic local edits | verified | 四組 invert／strength 對 Rust RGB16 max1；8192 寬單像素 mask max0；全尺寸 alpha 不降採樣；實際 bound Name/Instance、局部曝光、undo/redo/save/reopen 通過 |
| REQ-RAW-08 external Lensfun | verified for full and off-centre default crops | 延遲 review 的兩個新案例先失敗（warp .00110384、gain .00538858），修正為 DefaultCrop 光學中心／半徑後，53 個鏡頭／幾何檢查通過。實際載入全幅與偏心裁切的線性公式誤差分別為 3.60e-7／3.30e-7；封裝 CLI 的 CPU／Metal 匯出最大差均為 1 個 RGB16 碼值 |
| EXP-01 through EXP-09 execution | verified within recorded boundaries | 原 14 組數值／部署情境與 26 項 RAW 桌面檢查保留於補修前證據；本次封裝另有 25 項 GUI smoke、4 個來源的 CPU／Metal 匯出、兩種 JXL 無失真轉換及來源 SHA 核對。`dist/raw-pipeline-parity-acceptance.json` 明確分開兩輪證據，未宣稱重跑全部舊實驗 |
| Verification harness corrections | recorded | Apple TIFF 按 II/MM；灰階 fixture 用 F32 Deflate 避免 half 量化；Lensfun oracle 保留自己的 geometry plan；native driver 保持 Avalonia binding 並等待控制項更新。WGSL include 隨實際 shader 展開驗證；CLI log 逐次自專案 `.filmbatch/export_log.json` 保存。此次 JXL float32 codestream 的合成 TIFF 封裝改用既有 DNG 慣例的名目 16-bit 宣告；原錯誤與更正另存，不更改產品支援範圍 |
| Independent code / methodology review | completed; scoped approvals | 兩輪獨立source review＋最終色彩／JXL審查；已修正實際可重現缺陷，不把靜態review當runtime。DNG對照遵守同色彩模型；完整語料／第三方DNG viewer／Windows實機／強制OOM不在已執行證據中 |
| Final solution / shaders / packaged Mac smoke | verified after follow-up | 完整 solution：1174 passed／315 外部語料條件略過／0 failed（Core415／22、Raw618／293、App141／0）；29 個展開後 WGSL 通過。15 個 GPU 迴歸走真正 Render16；25 項封裝 GUI smoke 與實際畫面已檢視 |
| Mac archive / extracted signatures / native JXL | verified after follow-up; ad-hoc only | 新 ZIP 為 138126331 bytes，SHA256 `f0fcc10d437a3817298503d2d660e90c530667dffcc87df639ae1fdf383e3796`；兩平台 ZIP 完整性、解壓 Mac 簽章與乾淨 PATH 的 CLI 通過。全幅／偏心裁切、整數／float JXL 共 4 組 CPU／Metal PNG16 最大差 1；兩種 JXL sample bytes 與獨立參考相同。Windows GUI／CLI 各 10 個 DLL、20 份 SHA 符合 manifest，仍未執行 Windows runtime |
| Final double check / grouped commits | verified; original commits retained and follow-up source commits complete | 原 7 個 staged 檔案原樣提交 `cc23364`；DNG `088a5d2`、線性管線 `8c3c0b4`、RAW 控制項 `7504087`、JXL `c9cd74d`、文件 `5be5745`。本次 Lensfun 補修為 `07eff8b`，獨立 GPU finish 移除為 `cfe4cf9`；驗收紀錄隨文件提交，不 amend 既有內容、不 push |

### Review rulings / retained boundaries

- 舊 UI finding「未知白平衡選項顯示首項」不構成可保存的 RAW 路徑：`SettingsJsonConverter.Validate` 拒絕未知 `raw.wb_mode`，寫入也經 choice coercion；動態 light-leak asset 是既有獨立例外。未為此新增未知值框架。
- 舊 film-source finding 取決於 `Preview.Clear` 是否保留來源；目前明確設 `Source=null`，焦點切換先 Clear，來源完成才排程，故拒絕未成立的 stale-photo 推測。
- 文字路徑仍沿用既有逐鍵提交慣例；正式 RAW 面板提供資料夾瀏覽，已驗證相對路徑與選取世代保護。改造全站文字編輯提交時機不在八項需求，parent 保持既有相容性。
- >managed陣列上限的完整RAW解碼不支援；`ProbeInfoFile` 的稀疏規劃仍需要受限陣列，不是任意大檔的串流解析器。未為單一大型`.tif`特殊分支新增另一套不完整的檔頭解析器。
- GPU scope-only OOM回報真實降級原因；任何操作例外（含取消／驗證錯誤）一律傳播，不被同時的OOM訊息吞掉。強制物理OOM未執行，尺寸／預算fallback與實際Metal輸出另有證據。
- JXL reviewer 的兩項既有靜態觀察不作新功能缺陷或執行證據：lossy integer tile 在精確半碼處的rounding與Rust可能差1碼；內嵌JXL預覽尚不供JPEG外觀擬合。此次保留原行為，lossless感光樣本另經獨立解碼器逐位元驗證；完整JXL預覽相容性未宣稱驗證。
- 初次TWINS的搜尋未包含另一個名稱`FinishRaw`；延遲review擴大範圍後確認只剩舊測試呼叫，已移除該入口、獨立buffer讀回、kernel／band參數與shader，保留完整Render16的部分尾帶、取消、色域及色調行為測試。`src/tests`不再有舊入口引用。
- 遮罩精度明確保留16-bit來源值；對Rust的已執行fixture使用可由8-bit精確表示的alpha。上游`developed()`量化為u8，因此不宣稱任意16-bit軟邊逐碼相同，不為了對齊低精度參考而丟棄來源資訊或放寬CPU/Metal容差。
- 延遲missing-directory建議不採納為新fallback：已指定但不存在／不可讀的外部資料庫屬於可修正設定錯誤，沿用明確回報政策；未設定／無匹配仍不加校正。既有`ExplicitRequestAfterRepair_DecodesInsteadOfReplayingFault`覆蓋修復後重解碼，不掩蓋失敗。
- 其他延遲finding已核對為過期或前提不適用：原index已先獨立commit；CLI log已逐次自專案目錄保存；架構／oracle文件已更新；快取fault/cancel已不重用；RAW未知choice受既有驗證攔截；原生driver已保留binding；DNG同模型對照已嚴格通過且保留不同模型差異。未重做已正確的程式碼。
- 本次偏心裁切補修未新增 fallback、資料庫或第二套算圖器；已移除的獨立 RAW finish 入口沒有相容別名。TWINS：`RawDisplayFinisher|ForWorking\(|MaxAlphaLongEdge|FinishRaw|raw_display_finish\.wgsl` 在 `src/tests/build` 為 0 處；保留的歷史文字不作現行 API。

