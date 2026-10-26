# Sony A7M5 Creative Looks：功能 Survey、效果實驗與採用裁決

## 裁決

**研究與可達實驗完成；目前 NO-GO，不加入產品。** 預生成的 12 種外觀可執行，並非效果無效；未通過的是第三方資料再散布前提、原版完整顯影驗收，以及與 FilmBatch 現行資料域的直接相容性。未進入使用者所說「若 OK」之後的完整實作計畫與產品整合，不把研究原型標成已支援功能。

- 檢查日：2026-10-26。
- 上游：[751K/sony-a7m5-looks，釘選 `d6e014b65d5103a0f094b1dad90a38bdc8c545d2`](https://github.com/751K/sony-a7m5-looks/tree/d6e014b65d5103a0f094b1dad90a38bdc8c545d2)，該提交日期 2026-10-25；不是未固定的 `main`。
- 對照契約：[底片風格](../film-looks.md)、[RAW／算圖](../architecture/engine.md)、[驗證入口](../../VERIFY.md)。既有 Recipe Lab 移除是[歷史脈絡](../history.md#精選演進)，不是禁止將來在條件滿足後另做相機外觀功能。
- 來源碼、參數 schema、Shader、內附 LUT 與 App 操作未修改；只新增本研究與文件入口。研究不涉及 push、重新封裝或全域安裝 Sony／Adobe 軟體。

## 1. 功能範圍

以下是檢查實作後的分類；「有實作」不等於本機已完成 Sony 原廠一致性驗收。

| 功能 | 上游實作／輸入輸出 | 本輪結果或限制 |
|---|---|---|
| 12 種 Creative Look | ST、VV、NT、PT、FL、VV2、IN、SH、BW、SE、FL2、FL3；`sonylook/sony_render.py` 的 `LOOK_IDS`、`Look` | 網頁逐一切換，預生成表格逐一求值，均有實際畫面 |
| ARW 外觀／顯影設定讀取 | `arw_looks.py`、`sr2.py`、`develop_settings.py`；解密並解析 ARW 的 SR2SubIFD，各外觀參數由相片攜帶 | 隨附 A7M5 合成 ARW 讀到 12 種；檢查的三張本機實拍均為 A7M4，只含 10 種 |
| 逐像素外觀鏈 | `LinearMatrix16` → `MainGamma` → `RGB2YCC` → `AreaComp`／`ChromaSuppress` → `YGamma` → `YCC2RGB` → BW／SE Effect | 白平衡後的相機原生線性 RGB（Sony 尺度）→ 顯示編碼 sRGB／Adobe RGB；不是通用 sRGB 藝術 LUT |
| 調整項 | 對比、高光、陰影、色相、飽和度、Fade、黑／白位準、曝光；另有銳化／Fineness、Clarity | 各參數映射見 `sony_render.Look`、`develop_settings.py`；不是 FilmBatch 同名滑桿可直接沿用的語意 |
| 完整 RAW 顯影 | `sony_decode.py`：RawNR、ITP 去馬賽克、DRO／Vatr、鏡頭幾何／色差、SSCS、外觀、Sharpness／Spica、Marble | 本機實跑於建立 decoder 參數時因缺少指定版 Edit 停止；未取得完整輸出 |
| 後置工具 | `sony_tonecurve.py` 的 R/G/B/Y 曲線、`sony_shade.py` 的周邊照度校正 | 現行 `decode`／`Look` 已接入；不能沿用部分舊檔頭「未移植」註解判斷功能不存在 |
| SSCS | `sony_sscs.py`；壓低近截斷、高飽和色的色度 | 屬逐像素處理，預生成 profile 與網頁 LUT 已包含；NR、DRO、銳化等空間處理不在 profile 中 |
| Capture One | `capture_one/`；65³ ICC input profile，平方根 shaper，校正 C1 Linear Response 輸入編碼，輸出 Lab D50 | 以 LCMS 獨立消費 ICC；沒有啟動 Capture One，不能冒充 C1 實際匯出 |
| ACR／Lightroom | `camera_raw/`；linear base DCP + 64³ XMP RGBTable，ProPhoto gamma 1.8，補償 ACR 的輸入編碼 | 實際解析 12 個 XMP、檢查表格 fingerprint／原色／gamma，並求值；未啟動 Adobe host |
| DxO PhotoLab | `dxo/`；base DCP + 64³ `.cube`，ProPhoto gamma 1.8，另補償 PhotoLab 的高光與工作色域處理 | 實際解析 12 個 cube，經上游 `dxo_model.E` 求值；未啟動 PhotoLab |
| 校正／比較工具 | 264 色塊的 `SonyChart.ARW`、identity／square／luma probes、輸入編碼擬合、相片配準與比較圖 | 本輪使用合成圖的已知相機 RGB；沒有把量測模型自身當 Sony 原廠 oracle |
| Rust 加速 | `rust/sonylook_core`、`sonylook/fused_rust.py`；ctypes 載入 cdylib，融合逐像素／空間處理，ITP、Marble、SSCS、部分建表 | Rust crate 無第三方 crate 相依；未建置／計時。上游效能與 bit-identical 宣稱未在本機重現 |
| 網頁示範 | `site/`；Three.js／WebGL2 的攝影、感光與現像互動解說，12 外觀、逐像素追蹤、按住比較 | 本機 localhost Chromium 實際操作及截圖；固定的預生成照片與表格，不是任意 RAW 編輯器 |
| 匯出 | `sony_export.py`；8／16-bit TIFF、PNG、JPEG，sRGB／Adobe RGB，ICC 取自 Edit resources | 此路徑仍需 Sony 安裝資源；本機未完成上游完整匯出 |

主要來源：[README](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/README.md)、[完整 decoder](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/sonylook/sony_decode.py)、[Look 與預設](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/sonylook/sony_render.py)、[Rust](https://github.com/751K/sony-a7m5-looks/tree/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/rust)。

### 外觀觀察與預設差異

在上游提供的運河夜景實際輸出中：VV／VV2 的彩度較強；FL／FL2／FL3 的藍綠與對比配置不同；IN／SH 抬起暗部，SH 更亮；BW 呈黑白，SE 呈棕褐色。這是單一場景的目視描述，不是人像膚色、高 ISO、天空漸層或所有相機的畫質通過。

`CAMERA_FADE` 為 IN=3、SH=6、FL2=1、FL3=1，其餘 0。上游說明 Edit 的 FL2／FL3 預設為 0，但所發布 profile 選相機預設 1；現有 Edit 對照數字不能直接當目前預設的驗收結果。

## 2. 授權與執行前提

[LICENSE](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/LICENSE) 為 MIT，Copyright (c) 2026 sony-a7m5-looks contributors。但 [README 的 License](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/README.md#license) 明確排除 profile 內 Sony 外觀參數、Adobe ColorMatrix 等第三方資料；不能由根目錄 MIT 推導成全部 LUT／DCP 都取得相同再散布權利。

- DCP 生成器讀取 Adobe Standard 的 ColorMatrix；DxO base2 另讀 ForwardMatrix。見 [acr_profiles.py](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/camera_raw/acr_profiles.py#L39-L49)、[dxo_profiles.py](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/dxo/dxo_profiles.py#L45-L59)。
- 原版 Look、profile 重建及 decoder 需讀使用者自己的 Edit 常數；Spica 與 ICC 匯出另需安裝資源。沒有把 Sony 執行檔放進 Python repo，不等於生成的資料沒有第三方權利前提。
- [edit_binary.py](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/sonylook/edit_binary.py#L15-L51) 固定 Edit **4.0.00.10312** 的 arm64 slice SHA-256 `a03b2449625f01003ee41d1a3f42630a20119059f65a74944c35eca81d5da442`。地址與版本綁定；本輪未設 `SONY_EDIT_SKIP_CHECK=1`。
- 這是「再散布權利尚未釐清」的工程裁決，不作侵權、專利或司法合法性的判定。權利判斷需維護者／權利人另行確認；使用者同意實驗不等於第三方授權。
- DCP 容器、XMP RGBTable 編解碼、ICC 寫入等 MIT 工具可以另行評估，但 FilmBatch 已有 TIFF／RAW 色彩與 LUT 工具。本次沒有已驗證的獨立缺口，不為了採用而再加一套格式框架或 Python／Rust 執行期。

## 3. 與 FilmBatch 的差異與修改影響

| 面向 | FilmBatch 現況 | 上游及採用影響 |
|---|---|---|
| 相機／來源 | 多廠 RAW；去馬賽克後轉場景線性 Rec.2020，保留自訂白平衡與語意調整 | 上游完整鏈只接 RGGB，Look 含 ARW 6.0 的機型相關分支。A7M5 profile 不可當跨相機校準 |
| 底片輸入 | `FilmLut` 契約是 sRGB 編碼 → sRGB 編碼；內附 36 卷 | 上游原始 f 是原生相機 RGB → sRGB；host LUT 是 f ∘ E⁻¹。既有矩陣、色調與 E 均不相同 |
| 處理位置 | `CpuPipeline` 底片階段位於 RAW 顯示收尾之前；GPU 同順序 | 把已含 Sony 現像的結果當藝術 LUT，後面仍會經 FilmBatch 相機色調／色域收尾；只轉檔格式或 gamma 不足以使其等價 |
| 匯出色彩 | sRGB、ProPhoto 的像素／ICC 契約；CPU／GPU 共用設定 | 上游 sRGB／Adobe RGB、DxO ProPhoto 是不同處理鏈，不能靠改標籤取得相同結果 |
| UI 與持久化 | `FilmLookViewModel`、共用縮圖佇列、`film.look`、ProjectService／undo；桌面與 iOS 共用 | 正式新增外觀要涵蓋縮圖／Main／Full window／預設／貼上／undo／重開／CLI／批次匯出；只加清單不算整合 |

具體落點：`src/FilmBatch.Core/Film/FilmLut.cs`、`FilmLookLibrary.cs`、`src/FilmBatch.Engine/Rendering/FilmLookProcessing.cs`、`CpuPipeline.cs`、`GpuEffectPipeline.cs`、`Imaging/Raw/RawSourceLoader.cs`、`RawDisplay.cs`。本輪只呼叫，沒有改寫。

## 4. 本機實驗

### E1：原 repo 入口與相機範圍

- `arw_looks.py chart/SonyChart.ARW` 實際列出 12 種外觀，Model=ILCE-7M5、SonyModelID=407。
- `sony_render.py ... Standard FL2 --scale 8 --out ...` 完成 front-end，再於 `Look._edit_sha` 因缺少指定 Edit 停止（exit 1）。
- `sony_decode.py ... --look ST --out .../full-reference.tif` 於 `decoder_params._tables` 同樣停止（exit 1）。沒有產生完整 Sony reference TIFF，沒有偽造常數補足它。
- 檢查的 `DSC03956`、`DSC02936`、`DSC03376` 均為 ILCE-7M4／SonyModelID=388；不把這些實拍變成 A7M5 的精確驗收材料。

### E2：12 外觀的真實網頁操作與預生成 profile 求值

- localhost 提供上游原樣 `site/`，Chromium 實際點選 ST、PT、NT、VV、VV2、FL、FL2、FL3、IN、SH、BW、SE，取得 12 張畫面及逐像素 trace；另驗證 FL2 按住／放開比較。沒有上傳私人照片。
- Python 實際解析每個 64³ XMP（base85／zlib、表格 MD5、維度、ProPhoto 原色與 gamma 1.8）及 64³ DxO cube，套上游輸入編碼模型，對合成圖 264 色塊求值。
- 另把上游公開 `raw.webp` 的 sqrt8 相機樣本縮到 768×512，分別經網站 33³／8-bit 表與 ACR 64³／16-bit 表求值；產生兩張 12 格比較圖供目視檢查。這個縮小、預量化來源不是全精度 RAW 或 Sony 原廠 reference。
- Capture One ICC 由獨立 LCMS 消費。控制發現 LCMS perceptual intent（0）會把抬黑壓回黑：IN／SH 的零輸入為 0；relative／absolute colorimetric（1／3）則分別為 22／43（8-bit），與 ICC 首節點及 ACR 表中的抬黑相符。故保留首次 intent=0 收據，但不將該次差異歸因成上游 ICC 資料錯誤，更不稱為 Capture One host 的錯誤。
- 網站與 profiles／不同 host 模型的比較只表示表示法與模型差異，**不是對 Sony 的精度成績**。Rust 效能、作者的 ARM64 emulation bit-exact 宣稱沒有重跑。

### E3：拒絕直接塞入現有 sRGB LUT 槽

固定測試向量為三通道皆等於 `0.18 × 8183.6` 的相機原生 RGB，**不是已知反射率的 18% 灰卡**。對 ST，正確套 `dxo_model.E` → DxO LUT → ProPhoto 解碼／轉 sRGB，得到約 182.31；誤把同一表當 sRGB → sRGB 直接取樣，得到約 174.30（兩者皆換算為 0–255 單位），相差約 8.01。

接著以兩張隔離 A7M4 實拍副本執行**現行 FilmBatch CPU 管線**：Neutral、Portra 400、Tri-X，以及只在丟棄式 driver 中把 DxO ST 表替換到現有 FilmLut 槽。實際比較圖中錯置版本明顯更亮、改變色彩，舞台藍光偏紫；沒有宣稱哪一張與 Sony 原廠最接近。這是輸入／輸出資料域不符的反例，不是 Sony 外觀或 FilmBatch 原有 LUT 的 bug。

- 共 2×4 張、768×512 的 PNG16，經產品 `BatchExporter.Encode` 輸出，皆無 metadata warning。
- ImageMagick 獨立解碼後，RGBA16 與各次渲染輸出**逐位元相同**；PNG IHDR 為 16-bit、含 ICC、alpha 皆 65535。檢查的是實際輸出，不只檔案存在。
- 原始 `DSC02936.ARW` SHA-256：`91b8ace4f7be69d8d2f6601b628dc699a5bd73bf91ffebc758ff92bdc385ed50`。
- 原始 `DSC03376.ARW` SHA-256：`2655aca052775ed68f737a07ad27e65653ce12853115c07624c187d23e320638`。
- 隨附合成 `SonyChart.ARW` SHA-256：`659334e942c965b41c23dd494aceb64d86e5309ba656a07f97456e93d942919c`。
- 原始輸入與副本皆未改動；FilmBatch 比較為 CPU，不外推成 Metal／DX12／Vulkan／iOS 或原生 FilmBatch UI 的驗收。

環境：.NET SDK 10.0.402；Python 3.13.12、NumPy 2.5.4、SciPy 1.18.1、rawpy 0.27.1／LibRaw 0.22.1、Pillow 13.0.0、tifffile 2026.10.15。Python 相依僅存在隔離實驗環境，不加入產品。

## 5. 上游宣稱與尚未驗證部分

上游 README 的 Edit 對照只有 DSC03591 的 ST／FL2／FL3；其餘九種沒有 Sony rendering 對照，FL2／FL3 Fade 1 也沒有相機 JPEG 驗證。其 PhotoLab 高光模型的 4.6 平均／26 最大差異是**作者報告的色塊模型誤差**，不是本輪重現，也不是 FilmBatch 的錯誤或全圖 MAE。

本輪沒有 Sony Edit 原廠輸出、未量測人像／高 ISO／漸層／極端滑桿組合的精確性，也沒有驗證 A7M4 與 A7M5 光譜響應可互換。預生成的外觀可看見差異，不足以得出「已完整復刻」、「優於現行底片」或「可安全隨附」。

## 6. 如何繼續：選項、影響與入場條件

| 方案 | 決策 | 會改到的既有部分／受影響功能 |
|---|---|---|
| 直接捆 12 個 DxO cube 或網站 LUT 到 FilmLookLibrary | **拒絕**；資料域／host 補償不符，第三方資料權利未清楚 | 不能用加 `looks.json`、33³ 轉檔就宣稱完成 |
| 使用者自有資料、本機生成／匯入的藝術外觀 | **可另立需求，但本輪未批准實作**；明說不是 Sony 原生顯影，不隨附第三方資料 | LUT 工具／載入與資產身分、`film.look` 清單、共用縮圖／Main／窗口、Project／預設／貼上／undo、匯出與缺檔行為。須先固定標準 sRGB 輸入與 RAW 收尾語意 |
| 機型專屬的 Sony 原生 RAW 現像 | **若目標是精確 Sony 行為，這才是相應方向；目前阻擋** | RAW 元資料／相機域、SourceImage 生命週期、白平衡／曝光／AI domain、CPU＋GPU 收尾、schema／雙語 UI、縮圖／Loupe／CLI／匯出／iOS；不是底片清單的小修改 |

推薦目前不做產品改動，先解決以下條件，通過後再寫完整、可執行的 implementation plan：

1. **權利**：維護者／權利人釐清生成資料、常數及 Sony／Adobe profile 元件的使用與散布範圍；本機個人匯入與產品隨附分開決定。
2. **環境**：合法取得並由操作者提供指定版 Edit／資源；保留 SHA 驗證，不假設新版地址可沿用，不代使用者接受專有軟體條款。
3. **目標**：選擇「具明確 sRGB 輸入的藝術風格」或「限定機型的原生 RAW 現像」。兩者是不同功能，不能默默以較容易者替換。
4. **驗收輸入**：同一 A7M5 原始場景與 Sony 原廠輸出，固定 WB／曝光／Fade／色彩空間／空間效果；涵蓋各外觀、膚色、亮飽和色、暗部與漸層。品質容差需依目標制定，不能把上游單張數字直接當全部驗收線。
5. **正式驗證**：CPU／GPU 一致、窗口與整圖、來源與設定不變、預設／貼上／undo／重開、批次 PNG16／ProPhoto／CLI，以及真實桌面操作；iOS／Windows 各自保留平台驗證邊界。

本輪沒有降低取樣精度、改變 RAW 政策、復活舊 Recipe Lab，或把一個缺少前提的介面／shim 當整合成果。

## 7. 可重播證據

本機忽略檔：`artifacts/verification/2026-10-26-sony-looks-survey.tar.gz`，內含實驗 driver、版本／輸入與來源 digest、命令／exit／stdout／stderr、profile 數值及 PNG16 獨立核對收據。`REPLAY.md` 說明如何取得釘選上游、隔離執行及重現失敗邊界。

封存不包含私人 RAW、照片輸出／截圖、Sony／Adobe 執行檔或資源、上游預生成 LUT／profile、Python／.NET 執行檔。目視圖只保留在操作者本機的實驗目錄，不加入 Git 或上傳。初次瀏覽器等待參數寫錯造成的 driver error 也分開記錄，不當成上游 WebGL 錯誤。
