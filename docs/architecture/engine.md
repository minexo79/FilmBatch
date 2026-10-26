# 架構：Engine 模組

本文件是[架構說明書](../architecture.md)「4. 模組說明」的一部分，涵蓋 4.6–4.8：算圖管線、算圖排程、影像讀取與批次匯出。

## 4.6 Engine．算圖管線（Rendering）

**職責**：把「來源影像 + 最終參數」算成結果影像。同一套演算法有 **CPU 與 GPU 兩份實作**，順序與數學必須一致。

```mermaid
flowchart LR
    S["Settings<br/>（最終參數）"] --> MP["ModuleParams.From<br/>解析成強型別欄位"]
    MP --> CPU["CpuPipeline.Process<br/>C# 參考實作"]
    MP --> GPU["GpuEffectPipeline.Render<br/>派送 WGSL compute pass"]
    CPU --> R1["CpuFakeRenderer"]
    GPU --> R2["GpuSpikeRenderer"]
    R1 --> I["IPreviewRenderer"]
    R2 --> I
```

| 型別 | 檔案 | 說明 |
|---|---|---|
| `IPreviewRenderer` | `IPreviewRenderer.cs` | 算圖器介面：`Render`（預覽，8-bit BGRA）、`Render16`（匯出，16-bit RGBA）、`DeviceName`、`IsSoftware` |
| `ModuleParams` | `ModuleParams.cs` | 把 `Settings` 解析成一個個欄位（`VignetteOn`、`VigAmount`…），並計算各模組的種子。**CPU 與 GPU 共用同一份解析結果**，保證兩端讀到相同的值 |
| `CpuPipeline` | `CpuPipeline.cs` | CPU 完整管線，`Process` 依序執行模組；RGBA 算術、模糊累積與量化使用 `Vector128`，sRGB 曲線與 gamma 保留純量 `MathF.Pow` |
| `GpuEffectPipeline` | `GpuEffectPipeline.cs` | GPU 管線；高斯模糊沿用相同權重、tap 順序與 clamp 邊界，半徑 ≤320 用 14 KiB workgroup tile 共讀來源，較大半徑走直接核心；兩核心共用展平 workgroup 起點，每累計至多 64 Mi 個輸出取樣運算就提交並等待，跨軸／效果也計入，不延長裝置的 10 秒期限 |
| `GpuSpikeRenderer` | `GpuSpikeRenderer.cs` | **正式的 GPU 算圖器**（名稱沿用自 M0 技術驗證）。超過 GPU 紋理上限或顯示記憶體不足時，自動改走 CPU 並在 `DeviceName` 標示原因 |
| `CpuFakeRenderer` | `CpuFakeRenderer.cs` | **正式的 CPU 算圖器**（名稱同樣是歷史遺留），內部呼叫 `CpuPipeline`；無 GPU 或使用者強制軟體模式時使用 |
| `DenoiseProcessor` | `Denoising/` | CPU／GPU 共用的效果前來源階段。正式預覽／匯出仍先全解析度推論；縮圖只處理提供的像素，不重開原檔，另有一筆神經結果快取。RAW 快取代理輸入後混合；sRGB 串流混合／量化並直接縮小；見 [ADR 0002](adr/0002-ai-denoise.md) |
| `FilmLookProcessing` | `FilmLookProcessing.cs` | 底片風格的 CPU 參考實作：LUT → 八色相帶（OKLCh）→ 暖色 → 褪色 → 依效果比例混合；`BandWeight` 為色相帶權重 |
| `FilmLookUniforms` | `FilmLookUniforms.cs` | 傳給 `film/look.wgsl` 的 uniform（`Mix`、`Flags`、`Band0`…`Band7`，與 WGSL `struct LookParams` 對齊） |
| `PhotoAdjustments` | `PhotoAdjustments.cs` | BasicLinear 後的羽化膚色與單調 Zone 亮度曲線；CPU 逐像素與有界分析共用純量數學，GPU 對應 `linear/photo_adjustments.wgsl` |
| `ShadowRecovery` | `ShadowRecovery.cs` | 場景線性局部暗部增益與增益前亮度／色度降噪；覆蓋率加權 guided 基底、共享 `ShadowLayout` 與 CPU／GPU uniforms；GPU 對應 `linear/shadow_stats.wgsl`、`shadow_box.wgsl`、`shadow_apply.wgsl` 與共用 `shadow_common.wgsl` |
| `Oklab` | `Oklab.cs` | 線性 sRGB ↔ OKLab 轉換（與 `look.wgsl` 的 `to_oklab`／`from_oklab` 同係數） |
| `EffectAssets` | `EffectAssets.cs` | 解析外部素材：雙重曝光的第二張照片、漏光與灰塵的材質檔。材質庫為空時改用程序化效果 |
| `RenderView` / `FrameStats` | `RenderView.cs` | 窗口渲染的視圖（畫框內的子矩形）與 Main 整張渲染記錄的全畫框統計。`IPreviewRenderer.RenderWindow` 只算窗口，結果等於「整張渲染後裁切」（逐通道誤差 ≤ 1 LSB、平均 < 0.05 LSB） |

**AI 像素保留量**：`DenoiseProcessor.MemoryUsage` 分列未混合神經結果、來源與既有縮放／顯示陣列，以及預覽尺寸混合輸入。full／thumbnail 共用的陣列依參考去重；查詢不建立 RAW 的延遲 `Rgba16`，重用的去重集合在查詢後清空，不成為保留者。`SharedMemoryUsage` 在未使用 AI 時回傳零，不建立處理器或 session。這只是 AI 快取可達的主要 managed 像素陣列，不含遮罩、中繼資料、session tensor、ORT/native 或 GPU 記憶體；不是 heap、RSS 或入場預算，不能與程序 RSS 相加。

**處理順序**（`CpuPipeline.Process` 與 `GpuEffectPipeline.Process` 相同）

RAW 來源保持未夾取的浮點線性 Rec.2020，`Rgba16` 只在原圖比較／直方圖等顯示用途首次取用時編碼。兩套算圖管線先轉到未夾取的線性 sRGB 工作域，在裁切後套用相機白平衡與語意遮罩，再執行既有效果；顯示段前轉回 Rec.2020，依原順序完成相機色調、高光去飽和與壓縮，再轉顯示 sRGB。高光壓縮的通道最大值不能與色域轉換交換順序。一般影像維持原有 sRGB 路徑；藝術 LUT 仍有自身的有界輸入限制。

| 階段 | 色彩空間 | 模組 |
|---|---|---|
| ⓪ 來源（選用） | RAW 線性 Rec.2020／一般顯示 sRGB | 固定模型分塊 AI 降噪 → 亮度／色彩混合 → 需要時縮小；兩個 renderer 共用同一實作，不重複一份神經網路 shader |
| ① 幾何 | 線性光 | 基本調整（曝光、色溫、色調）→ 局部膚色 → Zone 曲線（僅場景來源）→ 暗部還原（選用）→ 鏡頭變形 → 多格分鏡 |
| ② 光學 | 線性光 | 雙重曝光 → 散景 → 縮放旋轉模糊 → 動態模糊 → 暗角 → 光暈 → 漏光 |
| ③ 底片 | 線性光 | 底片風格 → 古典版材 |
| — | 線性 → sRGB | `linear_to_display` |
| ④ 顯示 | sRGB | 基本調整（細節 → 亮度 → 對比 → 亮部陰影 → 飽和）→ 色調曲線 → 色調分離 → 清晰與銳化 |
| ⑤ 表面 | sRGB | 灰塵與刮痕 → 顆粒 |

**規則與陷阱**

- 模組關閉或參數為零時要**完全跳過**，輸出必須與輸入逐位元相同（identity 測試會檢查）。
- 改了 CPU 版就要改 GPU 版，反之亦然；`Gpu_MatchesCpu_*` 測試會比對兩者。
- C# 的 uniform record（`[StructLayout(LayoutKind.Sequential)]`）欄位順序、型別、對齊必須與 WGSL 的 `struct Params` 完全一致，通常以 `_pad` 補齊到 16 位元組的倍數。
- GPU 只有 4 張工作紋理輪流使用（`Next(...)` 會避開正在讀的紋理）；新增需要多張中間結果的 pass 時要用 `reserve` 參數保護。
- 半徑、大小類參數以**短邊比例**計算，預覽代理圖與原尺寸匯出的外觀才會一致。
- 顆粒例外地以來源參考平面的短邊決定粒徑：`SourceImage.ReferenceWidth/Height` 穿過代理圖，輸出像素經裁切逆仿射回到該平面；三種模型共用座標、seed、明暗響應與像素足跡濾波。光學圓盤數截斷於每格 8，最大 λ=1.6 時截斷尾機率低於 0.0046%；不是精確片種或化學模擬。

**基本調整的細節契約**：`basic.structure` 比較目前像素與 Gaussian 基底的亮度，兩者必須來自相同、尚未套用全域顯示調整的影像。`CpuPipeline.BasicDisplay` 與 `basic_display.wgsl` 都先做細節，再做亮度／對比／亮部陰影／飽和；不能用調整後亮度與調整前基底相減，再除以正負通道相消後接近零的亮度。均勻色塊沒有空間細節，即使極端全域設定下也不應被細節增益改色；回歸入口為 `EffectPipelineTests.BasicStructure_UniformColourRemainsUniformUnderExtremeGlobalAdjustments`。這項順序修正不增加 blur、pass 或暫存，不變更參數格式；細節與全域調整同時非零的舊設定會得到修正後外觀，單獨啟用任一類則維持原行為。

**暗部還原契約**：獨立的 `shadow_recovery` 模組預設關閉，不重定義顯示域的 `basic.shadows`。在未夾取的工作線性 sRGB 上，以 `log2((max(Y,0)+2⁻⁷)/0.18)` 作導引，按 alpha 加權的代理格統計與分數盒狀 guided filter 求局部基底，再把亮度／色度降噪與共同 RGB 增益套回來源。增益曲線不加黑位偏移，基底階調斜率下限 0.3；高於膝點的基底增益為 1，不在此處裁掉 HDR。alpha 不變，完全透明的隱藏 RGB 不進鄰域；純黑仍為黑。一般 RGB 來源先按既有流程線性化。

局部半徑按裁切後短邊比例；降噪 σ 按 `ReferenceWidth/Height` 的來源像素足跡換算，因此縮小預覽可能減弱／跳過次像素降噪，不宣稱和全圖逐像素相同。CPU 重用既有兩張暫存，GPU 重用工作紋理；有效曝光與兩種降噪均為零時直接跳過，不讀取未寫入的暫存。取消在各 pass／列間檢查。`PhotoSamplingSettings` 暫停此局部算子，避免白平衡／Zone 取樣納入創意映射；參數沿用 Settings、預設、貼上、undo 與匯出快照，專案格式仍為 3。實際操作與測試入口見[建置說明](../building.md#局部暗部還原)，研究／解碼比較的證據與產品驗收分列於[暗部研究](../research/shadow-recovery-survey.md)。

**色調曲線契約**：`curves` 位於「照片基本參數 → 基本」，緊接「基本調整」，不另設分頁。分段曲線 → RGB 主曲線 → 個別 R／G／B 通道依序作用於顯示編碼 RGB；CPU 與 GPU 共用 `ToneCurveSet` 合成的 3×1024 查表，再選用曲線飽和度修正，保留 alpha。預設啟用但恆等，直接略過算圖階段；GPU 緩衝區首次需要才配置，隨管線重用及釋放。

四段調整與分界參考 LightCraft v0.13.0；分界排序、保持最小間距，累積最大值讓分段表維持單調，畫布使用同一份表。控制點沿用 `ToneCurve` 的單調三次樣條；通道與預設選單只屬 UI 狀態，實際點值沿用 Settings、預設快照、模組貼上與專案格式 3。拖曳只更新暫存預覽，放開／失去捕捉提交一筆復原；切換照片不會把舊手勢寫入新照片。調整曲線不重新解碼 RAW。展開曲線時，即使主直方圖已收合也收集實際影像分布；白平衡／膚色／Zone 取樣暫停曲線效果。操作見[色調曲線](../building.md#色調曲線)。


## 4.7 Engine．算圖排程（Scheduling）

**職責**：`RenderScheduler` 擁有唯一的 GPU 執行緒，把預覽、縮圖、裝置重建序列化。

| 行為 | 實作 |
|---|---|
| 預覽只算最新的一張 | `_preview` 只有一格，新請求直接覆蓋舊的 |
| 預覽優先於縮圖 | 迴圈每輪先看預覽，沒有才取一張縮圖 |
| 被前景預覽中斷的縮圖 | 若 Epoch／來源仍有效且沒有較新同 key 要求，取消回收後重新排入；即使取代它的預覽也已取消，縮圖仍須交付。明確取消縮圖、換資料夾與 dispose 不恢復舊要求 |
| 同一張縮圖只算最新設定 | `RequestThumbnail` 會先移除同 key 的舊請求 |
| Loupe 窗口 slot | `RequestWindow` 只留最新一筆；優先序為預覽 > 窗口 > 預取 > 縮圖。窗口有獨立要求世代，不因 Main 未換代而重用；`CancelWindow`、重建與清空縮圖時作廢，成功與失敗在 UI 排入前及送達時都再核對世代 |
| 偏好設定切換 GPU 後端 | `ResetRenderer` 推進 `Epoch`、清空佇列，在 GPU 執行緒上重建算圖器 |
| 算圖失敗 | 先重建裝置重試一次，仍失敗以 `RenderFailure` 回報原始例外與要求來源；無法建立算圖器也須回報逐筆要求的失敗終態。窗口走獨立 `WindowFailed`，只解除該要求的去重狀態，不污染主預覽／縮圖錯誤，也不無限自動重送 |
| GPU 建立失敗 | 改用 fallback（CPU 算圖器）並發 `Notice` |
| 閒置來源釋放 | 每筆工作在不可內聯的 `RunNext` 中完成後才回到等待迴圈，避免上一筆要求／結果留在常駐執行緒的堆疊上；不靠產品端強制 GC |

App 端由 `FilmBatch.App.Shared/Services/PreviewService.cs` 包裝：核對來源世代、遞增的交付順序與算圖器 `Epoch`，把事件 post 回 UI 執行緒，並計算直方圖。同一來源的參數調整允許先顯示已完成影格，再接續最新結果；取消、來源更換與裝置重建會封鎖先前交付。

排程器只在鎖內判斷資格並擷取事件委派，解鎖後才呼叫訂閱者／計算直方圖。預覽、縮圖、失敗、通知與裝置就緒的 payload 都帶「原要求的 Epoch」，`PreviewService` 在計算後及 UI 投遞時再次驗證；不可在回呼入口重新讀取目前 Epoch，否則舊結果可能被誤標為新世代。

Main／Window 完成時，算圖執行緒立即擷取不可變的 `RenderBackend`（裝置名稱與是否使用 CPU，名稱包含回退原因），隨影格送達；不可延後在 UI 讀取 live renderer，因為下一筆縮圖可能已改變後端狀態。`PreviewService` 只在影格通過世代／Epoch 檢查後更新 `DeviceName`／`IsSoftware`，以 `BackendChanged` 更新狀態列；縮圖、預取與單純顯示縮放不改寫此狀態。`DeviceReady` 仍只表示初始化／重建，不拿來逐格發提示。

CPU 名稱與回退原因在快照中保留雙語文字，讀取快照時才依目前語言選擇；裝置初始化也使用同一表示方式。切換語言不需重算影格、不讀取 live renderer，也不把已翻譯的字串當成固定的後端身分。

失敗事件的 `PreviewGeneration` 只有預覽要求有值；縮圖及 `$init` 初始化診斷為空。預覽失敗只結束自己的世代，不消耗較新的預覽，也不替照片縮圖記失敗；背景失敗不得清掉等待中的前景影格。縮圖重算失敗時可以保留舊點陣圖，但仍須完成該筆交付等待，讓下一張繼續；初始化錯誤不能讓已取出的縮圖要求無聲消失。

**陷阱**：`RenderScheduler` 的事件**在 GPU 執行緒上觸發**。在 Engine 以外訂閱時，不要直接更新 UI。

## 4.8 Engine．影像讀取與批次匯出（Imaging / Exporting）

| 型別 | 說明 |
|---|---|
| `Imaging/ImageLoader` | RAW 縮圖（長邊 ≤240，不論是否要求語意遮罩）先稀疏讀取相機內嵌預覽；取不到才回完整顯影。內嵌縮圖不反映 RAW 白平衡／鏡頭調整、不帶語意遮罩；選取後的預覽與匯出仍走 `RawSourceLoader`。一般影像與內嵌縮圖經 `ImageBackend` 在桌面使用 Magick.NET、iOS 使用 Apple ImageIO，轉成已定向的 16-bit straight sRGB RGBA |
| `Imaging/AppleImageCodec` | iOS RID 的原生影像 I/O：ImageIO／CoreGraphics／ColorSync 解碼與 ICC 轉換、JPEG／PNG16／TIFF16 編碼、串流 EXIF 擷取；macOS 回歸可直接呼叫此後端，不代表桌面已切換 codec |
| `Imaging/Raw/RawSourceLoader` | RAW 解碼、起始外觀、分箱／去馬賽克、高光重建與色彩處理，產生浮點 Rec.2020 場景來源；有 gain table 的 DNG 不額外擬合全域 JPEG tone。CPU／GPU 共用晚期 RAW 收尾，來源載入不提前量化成 SDR |
| `Imaging/Raw/RawGpuDemosaic` | 僅 App 的快取解碼工作進入 GPU 政策範圍；遵守軟體模式、後端與 GPU 記憶體預算，iOS 停用。AHD／色彩收尾共用獨立裝置，但 kernel 延後建立、失敗分別停用；直接 `ImageLoader`、CLI 與批次匯出使用 CPU RAW |
| `Imaging/SourceImage` | 一般影像保存 sRGB `Rgba16`；已算圖輸出以 `Encoding` 明確標記 sRGB／ProPhoto，複製與縮放保留編碼。RAW 保存場景像素、相機色調／白平衡、共用幾何與語意遮罩；`Downscale` 在場景域縮放，保留原始已定向的 `ReferenceWidth/Height`，算圖不改寫來源 |
| `Imaging/MagickImageCodec` | 桌面 JPEG 預覽先讀尺寸；兩軸都大於兩倍預覽長邊才使用既有 decoder-size hint，避免小圖／窄圖反而被 decoder 放大。保留方向、原始參考尺寸與 ICC；完整載入／匯出不使用提示 |
| `Imaging/ImageHistogram` | 直方圖與裁切比例 |
| `Imaging/SampleImages` | 產生「開啟範例照片」用的測試圖 |
| `Imaging/PhotoAnalysis`／`ZonePlacement` | 至多 4096 點的工作線性 RGB 穩健取樣、相對參考色、候選白平衡正向擬合與 Zone 算術；重用有界取樣緩衝區，不建立全圖遮罩／顯示副本。WB 候選先於 semantic／Basic／局部調整套用，再聚合與計算殘差 |
| `Exporting/BatchExporter` | 匯出主流程：`BuildJobs` 為每張照片建立不可變快照 → `RunAsync` 在背景依序讀檔、`Render16`、縮放、編碼、寫入 EXIF / ICC → 寫 `export_log.json`。工作入口明確停用 RAW GPU，即使呼叫端帶入預覽政策也不受影響；效果算圖仍依匯出選擇使用 CPU／GPU |
| `Exporting/ExportModels` | `ExportOptions`（格式、色彩空間、品質、尺寸、目的地、檔名範本）、`ExportJob`；色彩空間屬於匯出快照，不寫入照片設定或專案 schema |
| `Exporting/ExportFileNames` | 檔名範本（例如 `{name}_{preset}`、`{index}`）展開，並移除各平台的非法字元 |
| `Exporting/TiffMetadata` | 使用正規化 EXIF 位元組，不依賴 Magick profile 型別；保留 TIFF 原有大小端與像素 strips，附加 EXIF／GPS 目錄；RAW 來源（CR3 為 CMT1／CMT2／CMT4 區塊）亦由此正規化 |

內嵌縮圖的方向以 RAW 檔頭為準，覆寫預覽自帶的 EXIF，並保留 RAW 的原始已定向參考尺寸；找不到或無法解碼預覽時，回到完整 RAW 顯影，原始 RAW 的解碼錯誤仍會回報。Apple 記憶體來源只在同步解碼期間借用已固定的預覽位元組，原生參考全部釋放後才解除固定。ImageIO 縮圖可能使用首位 padding 的 XRGB，讀取時須略過 padding，不能將它當作紅色或 alpha。

Apple 後端以串流讀取 JPEG／PNG／HEIF 的來源 EXIF，不因影像超過標頭探測範圍而丟失拍攝資訊；PNG 使用標準 `eXIf` chunk。輸出 JPEG／PNG 時，ImageIO 對 named sRGB 可能省略 ICC，因此編碼後明確寫入 JPEG APP2 `ICC_PROFILE`／PNG `iCCP`，並保留單一正規化 EXIF；TIFF 則保留 ICC tag 34675，再附加來源 EXIF／GPS。中繼資料重寫使用有界串流，不為大型影像額外複製整個檔案，也不加入舊式 `zTXt` ICC 副本來迎合解碼器。

**ProPhoto RGB 輸出：** `Render16` 的輸出編碼預設仍為 sRGB；TIFF16／PNG16 可要求 ProPhoto RGB（D50、純 gamma 1.8）。RAW 的線性 Rec.2020 來源保留到晚期收尾：此路徑略過 sRGB 色域壓縮、以有正負值的延伸 sRGB 傳遞既有效果，最後轉至 ProPhoto、在目標色域壓縮並編碼；不是對已削波的 sRGB 像素換 ICC 標籤。CPU 融入最後量化，GPU 使用既有浮點工作紋理再加一個輸出轉換 pass，沒有新增中途 CPU readback。

ProPhoto 色域壓縮以實際限制通道的 0／1 邊界為投影基準，與朝中性軸縮放的幾何相同；不要改回在零附近相消的 `y + (channel - y) * t`。該式的浮點殘差會被純 gamma 1.8 放大，實拍曾造成 CPU／Metal 相差 4/65535；六面飽和漸層回歸維持 2/65535 上限，不以 epsilon 削波補救。既有 sRGB 色域壓縮與線性 toe 不變。

預覽、縮圖、直方圖與取樣仍為 sRGB；一般 raster 匯入仍由 codec 轉為 sRGB，不宣稱全廣色域匯入或螢幕 soft-proof。既有曲線、有限 LUT 與創意效果保留 sRGB 座標語意，它們主動裁切的色彩不會因選擇 ProPhoto 而恢復。JPEG／DNG 不接受 ProPhoto，UI 切換時重設 sRGB，API／CLI 則在產檔前拒絕不合法組合。

Magick 與 Apple 編碼器依 `SourceImage.Encoding` 寫入匹配的 ICC，不再轉換已編碼像素；16-bit samples 與 straight alpha 保持不變。ProPhoto 的 EXIF ColorSpace 為 `0xFFFF`，不保留聲稱 sRGB 的 Interop 目錄；Apple TIFF／PNG 均回填相同的標準 ICC 位元組。`export_log.json` 每筆記錄 `ColorSpace`。自建 profile 為 ICC v4.3、parametric gamma 1.8，沒有 ROMM 線性 toe 或 flare；可重建來源及授權見 [ICC 產生器](../../tools/prophoto-icc/README.md)，驗收見 [ProPhoto 計畫](../superpowers/plans/2026-10-23-prophoto-output.md)。

RAW 預覽擬合會先依 JPEG 的 ICC／EXIF／容器宣告決定色彩，只線性化一次，在線性光下縮放後轉到 Rec.2020。截斷代理像素以 NaN 排除；鄰域對比採 Rust `f32::min/max` 的略過 NaN 語意，避免連帶排除截斷像素周圍的有效樣本。X-Trans 穩健平均則沿用 Rust `total_cmp` 的負數與正負零排序。

同一檔案需要完整顯影的 Thumb／Preview／Full 重疊載入時，`RawSourceLoader.DecodeLease` 依完整路徑與當次內容 SHA-256 共用感光解碼；色彩擬合另核對實際套用的 List3 校正鏈。各呼叫端各自持有可變的色彩標籤與運算碼清單，最後一份租約結束即移除完整 RAW／檔案位元組，不建立常駐感光快取。各等級仍按自己的分箱倍率顯影、保留場景線性與語意遮罩；上表的內嵌縮圖快路徑不進入此租約。每次完整顯影仍讀取並雜湊檔案；不同照片的首次解碼不會因此消失。

外部 Lensfun 的徑向／橫向色差校正先計算統一的前置構圖倍率，使 R／G／B 三個通道的完整周界都落在預設裁切的有效像素中心內；除了角落與邊中點，也檢查徑向多項式在邊上的極值。倍率直接折入同一個 `WarpRectilinear` 的係數，完整現像、分箱預覽、相機外觀擬合與語意遮罩沿用同一條逆映射，不增加全圖取樣階段。必要時縮小視野，但保留輸出尺寸與原始解析度；不以重複邊緣像素、遮邊或事後裁切補洞。此構圖只套用在外部校正，不重縮放已驗證的內嵌鏡頭校正。

RAW 中繼資料的標頭辨識與感光解碼一致：RW2 的 `0x0055`、ORF 的 `0x4f52`／`0x5352` 都使用 classic TIFF IFD 佈局，保留其 Make／Model 與 EXIF 子目錄，不誤報為損毀的 TIFF。

一般 TIFF 大於 RAW 可緩衝大小時，略過 RAW 探測，仍由已選定的平台影像後端載入；RAW 中繼資料損毀不阻止像素匯出，但必須在 `export_log.json` 記錄 `MetadataWarning`。RAW 移植的明確排除項目與 JPEG／ICC 偏差見 [參考輸出說明](../../tools/raw-oracle/README.md#比較範圍與已知偏差)。

**讀檔與色彩陷阱**

- 不要在判斷一般 TIFF 之前整檔 `File.ReadAllBytes`；大型一般 TIFF 可由平台影像後端／串流中繼資料路徑處理，不代表 managed 全檔 RAW 解碼也支援任意大小。檔頭決定的尺寸／樣本數須先以 checked／寬整數驗證，再套用 `RawLimits.MaxSamples`（`1 << 30`）及各分支上限；語意遮罩另限 16 個 IFD、每張 `1 << 25` 個樣本。上限不是保證有足夠記憶體。
- 損毀 RAW 的內容錯誤依 `RawException.Kind` 分類，不應洩漏索引／整數溢位例外；檔案存取錯誤與資源不足仍需保留原本原因。二維樣本以 `[height, width]`／`[y, x]` 對應，不能用正方形 fixture 掩蓋寬高顛倒。
- CR3 的 CMT1／CMT2／CMT4 可各自使用不同 byte order；正規化各目錄，不把根目錄的大小端直接套到 EXIF／GPS 子區塊。
- 嵌入預覽的色彩宣告與像素轉換是兩件事。不要先令 Magick.NET `ColorSpace = RGB`，再套一次 sRGB EOTF；歷史重現中，8-bit 灰階 128 的線性值由約 0.216 變成約 0.038。ICC／EXIF／容器的來源判定與「只線性化一次」須一起驗證，不能只檢查色彩空間標籤。

上述規則由早期 RAW 修復筆記回收；現行支援範圍以程式碼及[語料比較限制](../../tools/raw-oracle/README.md#比較範圍與已知偏差)為準，舊筆記的 exclusions 不是目前功能清單。

**匯出的安全規則**（改匯出程式碼前務必知道）

- 匯出開始時先建立參數快照，匯出期間使用者繼續編輯不會影響已排入的照片。
- 拒絕覆寫來源檔與既有輸出；先寫暫存檔，完成才搬到正式檔名，取消或失敗不留下半成品。
- EXIF 解析失敗不會讓整張匯出失敗；JPEG EXIF 超過 APP1 上限（65,533 位元組）時先移除 MakerNote，仍超限才只保留方向、尺寸與 sRGB 宣告。這些縮減逐張記錄在 `export_log.json` 的 `MetadataWarning`。
- 輸出皆已轉正：重建尺寸、方向與色彩宣告；僅 sRGB 寫入 Interop `R98`／`0100`，ProPhoto 不寫此目錄。不要沿用來源 Adobe RGB 的 `R03` 或失效指標。Photoshop `ImageSourceData`（37724）等原圖圖層不屬於新算出的影像，不能一併搬入。
- iOS 的 JPEG／PNG16／TIFF16 由 ImageIO 編碼；`ImageBackend` 於編譯時依 iOS RID 選用，桌面仍使用 Magick.NET。TIFF metadata append 支援大小端序，不重寫或重壓縮影像條帶；來源 EXIF／GPS 重新正規化後取代編碼器自行產生的 EXIF 指標。DNG 仍走共用 managed 寫入器。
- 完整尺寸的 Apple 影像不經縮圖解碼器；已辨識為完整 sRGB 空間時不再透過量化 ICC TRC 做一次 identity 轉換。方向、straight alpha、低有效位元及 Adobe RGB 轉換由 `AppleImageCodecTests`／`AppleImagingTests` 覆蓋。
- Box／Lanczos 縮小以 alpha 加權 RGB，濾波後再轉回 straight RGBA；全透明輸出清空 RGB，隱藏色不能污染可見邊緣。桌面 PNG16 明確寫成 16-bit RGBA（PNG64），避免編碼器把少色影像改成調色盤而將 alpha 量化為 8-bit。

去背遮罩以 `SourceImage.WithForegroundMask` 附加，共享原始像素與 RAW 的延遲顯示副本；原圖比較仍使用未遮罩來源。降噪重新建立來源時也必須傳遞遮罩。`ForegroundAlpha` 在解碼後、所有幾何之前以來源像素中心雙線性取樣遮罩，只乘 alpha 一次，RGB 保持 straight；CPU／GPU、預覽 BGRA8 與 PNG16／TIFF16 使用同一透明度語意。已去背或啟用 AI 降噪的 RAW 縮圖略過可能有不同裁切與資料域的相機 JPEG，從現像預覽產生（降噪需要場景線性來源才能選對 RawNIND）。`ExportJob` 捕捉當時遮罩，批次中繼續編輯不改已排入工作；有透明像素時拒絕 JPEG／DNG。

批次去背採兩階段：`ForegroundBatch.BuildJobs` 捕捉去重選取、來源路徑、照片與解析後設定；`RunAsync` 依序載入長邊至多 2048 的現像代理圖，略過既有遮罩，無主體／損毀／取消逐張記錄，不修改 live project。`Apply` 在呼叫端驗證路徑、照片與設定仍相符，拒絕覆寫期間新增的遮罩，經 `SetForegroundMasks` 一起保存。使用者取消仍可保存已完成項目；換專案／暫停／dispose 由 `BackgroundRemovalViewModel` 的 generation＋service 身分丟棄整批過期投遞。CLI 使用同一組 API，不另建推論流程。

現行 `basic.shadows` 仍在顯示轉換後處理，不是場景域邊緣保留局部 tone mapping。2026-10-23 修正原本會抬黑、洗淡暗部的加法：改為由亮度決定的共同 RGB 增益，保留黑點、暗色比例與 alpha，亮度到 0.45 時平滑回到增益 1；零值完全略過。非零的既存陰影設定會採用修正後外觀，參數範圍、專案格式及 CPU／GPU 算圖順序不變。[暗部還原研究](../research/shadow-recovery-survey.md)所述空間局部映射仍屬獨立的 `shadow_recovery` 模組，不把本次曲線修正宣稱為 LightCraft 暗部演算法的完整移植。
