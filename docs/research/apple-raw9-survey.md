# Apple RAW 9：主張查證與 FilmBatch 採用評估

## 裁決與範圍

**RAW 9 與神經去馬賽克／降噪是真的；建議 GO 隔離可行性研究，暫不替換 FilmBatch 的 RAW 引擎。** 官方 API 的存在，不能證明「所有系統 App 已切換、69 款、α6700 兩秒、ProRAW 降噪必定無效」等整段敘述都成立。

- 查核日：2026-10-27。來源依下方官方影片／API、相機清單、開發者說明及本機原生收據分級；未取得原文 App 名稱或原始測量頁面。
- 本輪交付研究及[可行性 Plans](../superpowers/plans/2026-10-27-apple-raw9-feasibility.md)，不是產品功能或完整產品整合的批准。
- 未改產品程式、參數、Shader、模型、專案或使用者設定；未升級系統、呼叫資源下載 API、commit、push 或重新封裝。

## 1. 逐項查證

| 主張 | 裁決 | 證據與限制 |
|---|---|---|
| macOS 27 有 RAW 9，以 Core ML 聯合處理去馬賽克與降噪，使用 ANE | **已確認官方設計** | S1 的 3:33；S2 的 `version9` 最低 macOS 27。不是本機實際 ANE trace，也不是整條 RAW／匯出管線都不使用 CPU |
| Apple「悄悄」換掉，Photos／Preview 已全部使用 | **需修正** | Apple 已在 WWDC26 公開介紹。S1 說 ImageIO 提供系統 RAW、Photos 使用 CIRAWFilter，但 RAW 9 並非預設啟用，需逐檔檢查後 opt-in；沒有各 App／版本實際選用 decoder 的證據 |
| 高 ISO 更乾淨，α6700 ISO 6400 保留髮絲與布紋 | **一般改善有官方展示；指定案例未知** | S1 示範 Sony A7 II 低雜訊、Canon 5D III ISO 51200、Fujifilm X-T5 ISO 12800；這是 Apple 展示，不是本輪獨立畫質比較，更不是 α6700 的指定案例 |
| 2600 萬像素約兩秒，後續滑桿即時 | **不能當通用效能保證** | S1 說 RAW 9 比舊版更耗資源，重複算圖可利用快取；須固定晶片、檔案、解析度、冷／暖狀態、滑桿種類及真正產出像素的計時。建立 lazy `CIImage` 不算解完 RAW |
| Luminance 調神經降噪，色彩雜訊自動處理 | **已確認 API 語意，逐檔有條件** | S1 的 `luminanceNoiseReductionAmount` 控制細微明度顆粒；RAW 9 的 `colorNoiseReductionAmount` 無效，`detailAmount`／`moireReductionAmount` 不再支援。S3 要求查詢個別 `is…Supported`，不能只因滑桿可寫就宣稱有效 |
| 系統內建，不用另外花錢 | **公開系統 API；費用／離線敘述需限縮** | 不必為呼叫 CIRAWFilter 另接雲端計費服務，但無法據此證明某款 App 免費。S4 有按需資源下載 API，不能承諾首次使用所有模型／相機資源都已在磁碟上；也不把系統模型抽出再散布 |
| 目前支援 69 款，含 α6700 | **69 未核實；α6700 分層確認** | S1 說 OS 27 發布時預期有 hundreds 款，且清單會 OTA 更新，並未提供現行精確總數。S5 官方一般 RAW 清單有 Sony Alpha 6700；S6 開發者的 RAW 9 清單也列 6700，但不代替實際 ARW 壓縮變體與逐檔版本查詢 |
| iPhone ProRAW 是已去馬賽克的線性 DNG | **已確認，但不是「完全無雜訊」** | S7 明說 low noise、already demosaiced、scene-referred／linearizable RGB，並包含可重新解讀的曝光、gain table、tone 等資訊；不是顯影完成的普通 sRGB JPEG |
| ProRAW 一定走 RAW 9，因此 Luminance 一定沒有作用 | **不能直接確認，來源有適用範圍分歧** | S1 概括原生 DNG 可用 RAW 9；S6 卻明列線性 DNG／ProRAW 不支援 V9。須查實際 `supportedDecoderVersions`（含 DNG 版本）、實際選定版本及 S3 旗標，再比較 0／1 的實體像素。無法從 `.dng` 副檔名推定；也不能說線性影像不能再降噪 |
| Film Lab 有 41 款底片、顆粒、halation、高光擴散與相紙 | **未能核實原文 App 清單；與 RAW 9 分層** | 這是應用程式創意效果／資產的主張，不是 S1 的 RAW decoder 能力或 Apple 隨附 41 款底片的證據。本輪不新增 FilmBatch 的底片需求或資產 |
| 只限 Apple Silicon Mac | **macOS 27 平台限制已確認** | S8 明說 macOS 27 僅支援 Apple Silicon；舊 CIRAWFilter 自 macOS 12 即存在，不能反推所有舊版 Apple RAW 也限 Apple Silicon |

**Lightroom 比較也需更新。** S9 說新版 Detail 面板可直接套用降噪等調整，不必為每項增強建立獨立檔；S10 記錄 Classic 14.4（2025-06）將功能移至標準 Detail 面板。因此「Lightroom 降噪一律產生一個大 DNG」不能泛指新版。S9 同頁仍有舊 Enhance→DNG 步驟，必須注明比較的版本與操作路徑；本輪未實跑 Lightroom，不做速度排名。

### 版本與預設的來源差異

S1 明說 RAW 9 需 opt-in；S11 的一般 `decoderVersion` 文件則說新物件預設最新可用版本。兩者不能用來替某一版 Photos／Preview 保證預設值。實作應檢查並**明確選定**檔案支援的版本，再讀回記錄；不依陣列排序、常數存在或一般相機清單推定。

## 2. 本機實際執行的邊界

環境：macOS **26.6.2（25G83）**、arm64、Swift **6.4.0**、SDK **27.0**。以隔離 Swift CLI 呼叫系統 CIRAWFilter，設定長邊 256、非 draft，真正 `CIContext.render` 到線性 Rec.2020 `RGBAf` 記憶體，不只取得 `outputImage`。探測控制為 `boostAmount=0`、`localToneMapAmount=0`、停用 gamut mapping、EDR amount 1；這些設定**不構成與 FilmBatch 場景資料相容的證明**。

| 原檔／相機 | 逐檔支援版本 | 實際選用 | 實體輸出與原檔 |
|---|---|---|---|
| `IMG_5861.CR3`／Canon EOS R8 | `8` | `8` | 171×256 浮點輸出有限；Luma 0／1 像素有差；原檔 SHA-256 不變 |
| `DSC03376.ARW`／Sony A7 IV、ISO 3200 | `7, 8` | `8` | 256×171 浮點輸出有限；此縮放／設定下 Luma 0／1 像素相同；原檔 SHA-256 不變 |

重要負向控制：本機存在 `CIRAWDecoderVersion9` 常數符號，但**不存在**版本限定的 `supportedCameraModelsWithVersion:` selector，且兩個原檔都沒有 V9 可選。SDK／常數存在不代表執行環境或檔案可用 RAW 9。Sony 在此低解析度輸出相同，也不證明其全解析度降噪無效。

- 原生 probe 編譯與執行 exit 0；未使用 FilmBatch renderer，未執行產品測試或 UI 驗收。
- 沒有 macOS 27／RAW 9 的實跑、α6700 或 ProRAW 原檔比較、全尺寸效能、ANE 分派、峰值記憶體或畫質勝出證據。

### 2.1 CLI 契約實驗（仍非 RAW 9 通過）

同日在儲存庫外編譯 `native --decoder default|8|9`（`swiftc -target arm64-apple-macosx14.0`）。macOS 26.6.2 上 `--decoder 9` 對 6 個檔案 **exit 2**，狀態皆為 `prerequisite-missing`，沒有像素、沒有呼叫版本清單 selector，也沒有呼叫 `downloadResources`。`--prepare-resources` 在這台主機直接 exit 2。`--decoder 8` **exit 0**：六檔都選到 8 或 `8.dng`，長邊至多 256 的延伸線性 Rec.2020 `RGBAf` 有限且非零，原檔 SHA-256 不變。這只證明「拒絕 RAW 9 不是因為解碼器整段壞掉」。

| 檔案 | 支援版本（執行時字串） | decoder 9 | decoder 8 長邊 ≤256 |
|---|---|---|---|
| Canon EOS R8 `IMG_5861.CR3` | `8` | 未選版、無像素 | 171×256；Luma 0／1 像素不同 |
| Sony ILCE-7M4 `DSC03376.ARW` ISO 3200 | `7, 8` | 同上 | 256×171；此縮放下 Luma 0／1 雜湊相同 |
| Sigma BF DNG 四檔 | `6.dng, 7.dng, 7, 8.dng, 8` | 同上 | 選 `8.dng`；有限像素；Luma 0／1 不同 |

四個 DNG 的 Core Image 屬性含 `CFALayout=1`、`BlackLevel`、`WhiteLevel`，沒有暴露 `PhotometricInterpretation`。依 CFA 欄位記為 **mosaic DNG**，不是線性 DNG。α6700、X-Trans、ProRAW、線性 DNG **沒有出現在這次輸入**，維持未覆蓋。執行時 `class_getClassMethod(supportedCameraModelsWithVersion:)` 為 false；SDK 27 標頭仍宣告該方法 `API_AVAILABLE(macos(27.0))`，Swift 名稱註記為 `supportedCameraModels(version:)`，apinotes 另寫 `with:`。沒有用相機數量代替 69。

R9-CAP **未通過**。Task 2／3 因沒有任何 RAW 9 像素而 **not-run**。沒有改產品程式。
- 私有收據：`artifacts/verification/2026-10-27-apple-raw9-research.tar.gz`；保存 probe、命令／版本、完整 JSONL、輸入與來源 digest、重播說明。排除原圖、像素陣列、模型、Apple SDK 標頭及執行檔；不是產品 runtime 相依。

## 3. FilmBatch 現況與真正的整合風險

依[目前 RAW／匯出契約](../architecture/engine.md#48-engine影像讀取與批次匯出imaging--exporting)，AppleImageCodec 的 ImageIO／CoreGraphics 點陣圖輸入不等於 Apple RAW developer；目前 RAW 仍由 managed `FilmBatch.Raw`／`RawSourceLoader` 處理。

1. **資料域不是換一個讀檔函式。** `SourceImage.cs:9–18` 的 `RawSceneData` 是未夾取的場景線性 Rec.2020，附相機 tone、相對白平衡模型與幾何；`RawSourceLoader.cs:207–249` 已做高光、起始 WB、相機色彩及 baseline exposure。它不是尚未套 WB 的感光／相機線性 RGB。
2. **[推論] 可能重複處理。** Apple 的 `boostAmount=0` 僅表示不套全域 tone；`linearSpaceFilter` 僅表示插入點在線性空間。兩者不保證沒有其他 WB／曝光／鏡頭／色域處理。`RawDisplay.cs:50–52,78–80` 即使 `Tone=null` 仍套預設 tone，不能以 null 略過重複顯示映射。
3. **[推論] 可能重複降噪。** `DenoiseModelCatalog.ForSource` 只依 `IsSceneLinear` 選 RawNIND／NAFNet；若 Apple RAW 9 已降噪卻照原鍵重開／推論，可能再降噪、混用來源或抹除細節。必須分開來源級 Apple 控制與現行 `denoise.*`，不能只把 UI 改名。
4. **[推論] 幾何不能只比尺寸。** Apple 與 managed 路徑的 crop／orientation／鏡頭 warp 若不同，現有語意／前景遮罩、Loupe、顆粒參考座標可能錯位。相同寬高不證明幾何相同。
5. **快取、重開與匯出要同時遷移。** `RawLoadOptions` 進入 `ImageCache` 的 preview／full 鍵，也影響 Denoise 全尺寸重開、縮圖與窗口的失效；CLI／`BatchExporter` 依不可變快照重新載入。現有 RAW GPU/CPU 標籤是效果 renderer，不識別 Apple decoder 或 ANE。不能預覽用 Apple、匯出卻默默換回 managed 並稱相同外觀。
6. **可重現性有平台邊界。** S11 不保證不同硬體／compiler／系統版本逐 bit 相同。若將來保存 Apple 選項，須記錄實際 decoder／OS／控制與錯誤；在不支援平台不能靜默改用其他引擎卻仍稱 RAW 9。

[ADR 0004](../adr/0004-apple-mobile-shared-core.md#調查來源與精確限制)與[前輪原生平台評估](object-removal-survey.md#16-原生平台與最低可行成本)已提醒平台 RAW 不等於共用管線。RAW 9 的新神經能力值得重新做窄實驗，不推翻舊有「尚未證明可全面替換」的邊界，也不把舊研究誤讀為 RAW 9 永久不可採用。

## 4. 選項、影響與必要 Plans

| 方案 | 決定 | 影響 |
|---|---|---|
| 保留現行 managed RAW＋可選 RawNIND | **維持產品現況** | 跨平台、舊專案、縮圖／Loupe／匯出與復原契約不變；不是宣稱現行首張 AI 成本已解決 |
| macOS 27 的隔離 RAW 9 可行性研究 | **推薦** | 只增加研究及外部 probe；依序驗證能力／資源、浮點與幾何、同條件畫質與成本，見配套 Plans |
| 現在直接增加 Apple backend／全面替換 | **暫不採用** | 尚未驗證資料域與語意，會牽動來源、RAW／AI 選項、快取、CPU／GPU、UI、保存、CLI／匯出及平台缺席行為，不能當小型讀檔修正 |

通過研究後，才制定並審核「明確 opt-in、保留 managed 預設」的完整產品計畫。實作影響範圍列於配套 Plans，不在此先造插件系統、通用 decoder 工廠或 Metal／WebGPU 零複製架構。Film Look、獨立 HSL、曲線、顆粒等效果不因 RAW 9 自動被取代；Windows、舊 macOS 與 iOS 的現行行為本輪全部保持。

### 研究充分性與下一個可推翻條件

官方設計／API／平台、現行程式邊界與本機 RAW 8 負向控制已足夠支持「值得做隔離研究、不能立即產品切換」。未知的 69、指定速度與畫質、ProRAW V9／降噪、原文 App、場景／幾何相容性均已明列，不以模型共識補證據。缺少實際 macOS 27 與對應原檔時，不把未執行當通過；下一步需要的是配套計畫的原生／資料驗證，而不是更多宣傳轉述。

## 來源與重新查核點

以下皆於查核日實際閱讀；官方演算法展示仍屬 Apple 自述，不是本輪獨立重現。

- **S1**：[WWDC26 305：Enhance RAW image processing with Core Image](https://developer.apple.com/videos/play/wwdc2026/305/)；3:33 架構、5:50 支援／opt-in、編輯控制、8:33 效能與快取。相機／預設行為在 OS／RAW 資源更新後重查。
- **S2**：[CIRAWDecoderVersion.version9](https://developer.apple.com/documentation/coreimage/cirawdecoderversion/version9)、[decoder enum 與 DNG 變體](https://developer.apple.com/documentation/coreimage/cirawdecoderversion)、[依版本查相機](https://developer.apple.com/documentation/coreimage/cirawfilter/supportedcameramodels(with:))。本機 SDK 27 標頭的 Swift 名為 `supportedCameraModels(version:)`，網頁顯示 `with:`；執行時依實際 SDK 編譯，不照抄不一致名稱。
- **S3**：[CIRAWFilter](https://developer.apple.com/documentation/coreimage/cirawfilter)、[逐影像明度降噪旗標](https://developer.apple.com/documentation/coreimage/cirawfilter/isluminancenoisereductionsupported)、[boostAmount](https://developer.apple.com/documentation/coreimage/cirawfilter/boostamount)、[linearSpaceFilter](https://developer.apple.com/documentation/coreimage/cirawfilter/linearspacefilter)、[EDR amount](https://developer.apple.com/documentation/coreimage/cirawfilter/extendeddynamicrangeamount)。
- **S4**：[逐檔資源下載，macOS 27.0+](https://developer.apple.com/documentation/coreimage/cirawfilter/downloadresources(timeout:completionhandler:))、[全部資源下載，macOS 27.2+](https://developer.apple.com/documentation/coreimage/cirawfilter/downloadallresources(timeout:completionhandler:))、[cacheIntermediates](https://developer.apple.com/documentation/coreimage/cicontextoption/cacheintermediates)。網路／離線、取消與資源生命週期需實跑，不自動申請額外 entitlement。
- **S5**：[Apple OS 27 一般相機 RAW 清單](https://support.apple.com/en-us/127493)，頁面發布 2026-10-06、內容稱 October 2026；不是 V9 專屬清單。
- **S6**：[Gentlemen Coders：RAW 9 Camera Support](https://www.gentlemencoders.com/extended-raw/raw9.html)，標示更新 2026-10-15；第三方開發者資料，含 α6700 與線性 DNG／ProRAW 限制，不冒充 Apple 保證。
- **S7**：[WWDC21 10160：Capture and process ProRAW images](https://developer.apple.com/videos/play/wwdc2021/10160/)，格式／前置處理的歷史官方說明；不是 RAW 9 實測。
- **S8**：[Apple Developer：App Store submissions now open for the latest OS releases](https://developer.apple.com/news/?id=k1mtkt1k)，2026-09-09，macOS 27 Apple Silicon 限制。
- **S9**：[Adobe Lightroom：Easily enhance image quality](https://helpx.adobe.com/lightroom/desktop/edit-photos/enhance-details.html)、[Classic 現行 Detail 降噪操作](https://helpx.adobe.com/lightroom-classic/desktop/process-and-develop-photos/enhance-details.html)；新面板與舊 Enhance 步驟不可混同，版本更新後重查。
- **S10**：[Lightroom Classic release notes](https://helpx.adobe.com/lightroom-classic/desktop/introduction-to-lightroom-classic/release-notes.html)，2025-06／14.4 的 Detail 面板改動；不作跨 App 速度證據。
- **S11**：[decoderVersion](https://developer.apple.com/documentation/coreimage/cirawfilter/decoderversion)、[逐檔 supportedDecoderVersions](https://developer.apple.com/documentation/coreimage/cirawfilter/supporteddecoderversions)；選定不支援版本會沒有輸出，跨硬體／compiler 不保證 bit 相同。
