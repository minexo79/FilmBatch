# AI Object Removal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `executing-plans` with `reflective-implement`, `test-driven-development` and matching UI/platform skills to implement task-by-task after implementation is authorized. Steps use checkbox syntax. A task finishes only after its observable result is exercised; scaffolds are not deliverables.

**Goal:** 交付可離線、非破壞、可重開且預覽／匯出一致的物件移除；以實際品質、可行性與操作驗收決定模型，不以論文名次直接選型。

**Architecture:** 選取輔助與填補分開。以 canonical CPU 完整解析度管線在色調分離後、清晰／銳化前合成已接受 patch；不反轉 SDR 為 RAW。每張照片保存有序遮罩／生成資產與來源身分，沿用既有命令、專案存檔、原生 ONNX 與匯出安全規則。

**Tech Stack:** 既有 .NET 10、Avalonia 12、`FilmBatch.App.Shared`、Core／Engine、ONNX Runtime：Apple `1.25.0`、Windows DirectML `1.24.4`。產品不新增 Python、LibTorch、自訂 FFT native runtime 或雲端相依服務。

**Spec:** [操作與資料契約](../../research/object-removal-design.md) 設計版本 1；[ADR 0005](../../research/0005-ai-object-removal.md)；[候選與證據](../../research/object-removal-survey.md)。本計畫採原生可執行的本機路線；高成本生成式方案是另列的比較／採用決策，不是未經批准的雲端 fallback。

## 目前狀態與授權

**通用移除／T1 維持 NO-GO，T2–T5 未開始。** 最近研究文件提交為 `9a4b4d9`；本輪接續使用者的小型模型與 Depth／拆解圖層研究，只更新五份研究文件及忽略目錄 probe，**不 commit／push、不新增產品程式或永久測試、不外送私人照片或呼叫付費 API**。[第 10 節](../../research/object-removal-survey.md#10-續驗與採用決策)、[第 11 節](../../research/object-removal-survey.md#11-權重來源與後續研究)、[第 12 節](../../research/object-removal-survey.md#12-場景微調vae-身分與原生續驗) 保留既有結果；[第 13 節](../../research/object-removal-survey.md#13-多角色替代方案與實驗) 記錄候選、古典／donor 對照、舊 `dist/` 消失與新 v2 身分；[第 14 節](../../research/object-removal-survey.md#14-小型模型與-depth-分層實驗) 補齊 16 案例 MI-GAN 對照、拒絕 FP16 儲存衍生檔，並實跑 Depth Small 原生與可見圖層／unknown 覆蓋。Depth band 不當成物件 mask 或 amodal 背景；Qwen 多 RGBA 只作來源／材料界線調查，未執行。研究對照不取代 T1 開發／T5 留出集，不新增產品圖層編輯器，也不列成 T2–T5 完成；不宣稱已消失的舊收據仍可原地重現。

本次接續的是使用者的**提示詞／LLM 引導 Survey、實際來回對照及平行 Socratic 研究**，結果集中於 [Survey 第 15 節](../../research/object-removal-survey.md#15-提示詞與-llm-引導實驗)。實際本機文字 LLM 不等於 VLM；同模型空提示基準、no-op critic 與規則組分開。只更新既有五份研究文件及忽略目錄實驗，不新增產品 prompt、改驗收門檻或勾選下列實作任務；不 commit／push、不外送私人照片。

最新原生平台／最低成本延伸見 [Survey 第 16 節](../../research/object-removal-survey.md#16-原生平台與最低可行成本)：以使用者現有系統可負擔的整合、算力與操作為優先，不要求換硬體，也不將公開 API 規格當 runtime 或品質證據。Mac 公開 API 實驗與影像 REST endpoint 的只讀調查已分界；接受候選後本機保存的契約不因來源而改變。這不是新增 cloud provider、第二套平台渲染器或放寬 T1；新影像 endpoint 支出、私人照片上傳與產品功能仍須另行授權；後續已授權 CLI 探針另列於下方。

使用者後續指定 `codex agy agent(cursor) opencode` 作外部入口；[第 17 節](../../research/object-removal-survey.md#17-既有-cli-作為外部模型入口) 記錄現有登入的真實文字呼叫與 Codex 單張公開遮罩圖的真實影像輸出，不使用新的 API Key／直接付費影像 endpoint 或私人照片。OpenCode 超出一次呼叫上限的偏差不隱藏，不補跑共同題；影像／文字／內部 tool trace 與訂閱帳單各自區分。這不勾選後續產品實作任務，不授權全權限 CLI、雲端 fallback 或改動 T1–T5。

訓練空間條件續驗見 [第 18 節](../../research/object-removal-survey.md#18-訓練空間條件續驗與最小可行路線)：48 份完整結果與全部額外失敗工作分開保存；同模型數值增益未解決自然移除，也未證明既有裝置上的低負擔產品路徑。PowerPaint v1 grant blocker、full-resident／offload OOM、harness 失敗及未授權 resume 均不隱藏；只補研究／成本與來源證據，不新增產品任務、模型 runtime 或重跑來美化紀錄。

[Survey 第 19 節](../../research/object-removal-survey.md#19-情境解法成本效益與決策紀錄) 集中全部既有方向的成本／效益與情境決策；新[兩案資格驗證計畫](2026-10-21-object-removal-scenario-qualification.md) 是獨立研究 GO-PLAN，不是本產品計畫的 T1 通過或 T2 前置 scaffold。新外部 dispatch 需自己的公開輸入／帳戶額度／隔離核准；原本本機 OR-02、40 場景／3 位人類／各平台與保存 gate 不變，未開始下列實作。

### Global Constraints

- 既有 ONNX pin 保持 Apple `1.25.0`、Windows DirectML `1.24.4`；是否支援候選 graph 必須實測。
- 一般影像與 RAW 均可在**現像後的顯示域**修補；不修改原始檔、RAW 感光樣本或 `RawSceneData`。
- 一般 copy／paste、套用預設、儲存／更新預設都不攜帶修補筆劃、遮罩或生成像素。
- 有修補走 canonical CPU 完整解析度結果；無修補維持現有快速路徑，既有 CPU／GPU 容差不變。效果 renderer 與 AI provider 分別報告。
- 預覽、縮圖、重開、CLI 與匯出消費同一接受結果；匯出不重新生成，缺資產或過期逐張失敗，缺模型不阻擋已接受資產重播。
- 原子提交先資產後 JSON；存檔／模型／取消失敗不寫成功狀態。每次 native run 結束再驗 token 與來源／設定世代。
- 初次提供一個通過資格的本機填補模型；選取輔助通過獨立 gate 才啟用，手動選取仍須能完成整條路徑。不先蓋通用 provider plugin 系統，不以 Clone／Telea 或原圖冒充 AI。
- T1 資格通過才實作模型路徑；未通過不得以虛構 tensor 契約或未驗證的鏡像權重繼續。可完成其他可執行研究，但不得交付假的功能。

## 檔案與介面地圖

以下 `Create` 都是**未來計畫**，不表示目前檔案已存在。路徑依本輪讀到的唯一共用編輯器位置，不能重新在桌面 App 複製 ViewModel。

| 所有者／位置 | Create／Modify 與責任 |
|---|---|
| Core 照片資料 | Create `src/FilmBatch.Core/Projects/RetouchState.cs`：不可變操作／筆劃／資產參照；Modify `PhotoEntry.cs`、`Project.cs`、`ProjectStorage.cs`、`ProjectService.cs`、`PhotoOperations.cs`：相等、格式 3、命令與 reset |
| Engine 修補 | Create `src/FilmBatch.Engine/Retouching/RetouchAssetStore.cs`、`RetouchRenderer.cs`、`RemovalProcessor.cs`、`RemovalModelCatalog.cs`、`SelectionProcessor.cs`：資產 I/O、canonical 合成、單一填補與選取模型；只有需要實際不同責任時才拆檔 |
| 算圖消費端 | Modify `Rendering/IPreviewRenderer.cs`、`CpuPipeline.cs`、`CpuFakeRenderer.cs`、`GpuSpikeRenderer.cs`、`Scheduling/RenderScheduler.cs`、`Exporting/BatchExporter.cs`、`Exporting/ExportModels.cs`：逐照片 context、full-size-before-preview、patch provenance |
| 共用 UX | Create `src/FilmBatch.App.Shared/Controls/Tools/RemovalTool.cs`、`ViewModels/Tools/RemovalViewModel.cs`、`Views/RemovalView.axaml` 與必要 code-behind；Modify `PreviewCanvas.cs`、`ICanvasTool.cs`、`MainWindowViewModel.cs`、`SidebarViewModel.cs`、`PreviewService.cs`、相關 Views／兩份 resx |
| 桌面／行動 host | Modify `src/FilmBatch.App/Views/DockFactory.cs` 與 `src/FilmBatch.Mobile/MobileEditorView.axaml(.cs)`：接入同一移除面板；MobileWorkspace 的專案複製含資產；不是重寫兩套移除邏輯 |
| CLI | Create `src/FilmBatch.Cli/RemovalCommands.cs`；Modify `Program.cs` 與既有 export：模型狀態／匯入／產生、已有專案修補重播與機器可讀錯誤 |
| 有意義的永久測試 | Create Core.Tests 的 `RetouchPersistenceTests.cs`、`RetouchRenderingTests.cs`、`RemovalProcessorTests.cs`；App.Tests 的 `RemovalViewModelTests.cs`、`RemovalToolTests.cs`；擴充既有 PhotoOperations／BatchExport／生命週期回歸 |

**核心介面約定**（供相依切片共同使用，不是本輪提供實作）：

```csharp
// Core；RetouchStack 內含不可變、有序 RetouchOperation；無資產時為 Empty。
public RetouchStack Retouch { get; init; } // PhotoEntry 的新屬性
public bool SetRetouch(string fileName, RetouchStack state, string description); // ProjectService

// Engine.Retouching；context 只含目前照片的已驗證資產根目錄／操作快照。
public sealed record RetouchRenderContext(string ProjectFolder, RetouchStack Stack);
public SourceImage Render16(SourceImage source, Settings settings,
    RetouchRenderContext context, CancellationToken cancellationToken); // RetouchRenderer
public RemovalProposal Generate(RetouchFrame frame, RemovalMask mask,
    CancellationToken cancellationToken); // RemovalProcessor
```

`RetouchFrame` 是 canonical 修補平面的尺寸／浮點顯示像素與 base／geometry keys；`RemovalMask` 是同尺寸、確認後的遮罩與 bbox；`RemovalProposal` 綁定輸入 keys、patch／mask、推論來源與 generation，**不是已提交命令**。所有權明確：frame／proposal 由單次工作持有並釋放；Core 只保存資產參照，不持有大像素陣列。

`IPreviewRenderer.Render`／`Render16` 在 `Settings` 後新增必填 nullable `RetouchRenderContext? retouch`，沒有修補傳 `null`；兩個 renderer 在 retouch 非空且有啟用操作時共用 `RetouchRenderer`，其餘走原路。同步遷移所有產品呼叫、測試 fake renderer、排程與匯出；不留舊 overload／別名。獨立 `SpikeParams` benchmark 不帶照片修補。修改公開符號前先用可用的 LSP references；本輪 C# LSP 未設定，執行時若仍不可用才用已知符號搜尋補齊。

## T1 — 模型、品質比較與平台資格

**依賴：** 實作授權；符合授權的權重／測試照片。**Human Review：** 權重或基底模型條款含糊、NC／自訂授權、照片外送、付費、安裝第二套 runtime 時停下。

**輸入／輸出：** Survey 的候選 pin → 實際取得的 `model-manifest.json`、來源／權重 SHA-256、張量與前後處理契約、平台矩陣及比較結果。這是可重現的實驗交付，不是產品 UI scaffold。

**2026-10-20 前置實驗紀錄：** [Survey 第 9 節](../../research/object-removal-survey.md#9-本機可行性實驗) 保存原始 6 張圖、20 組主 native（18 pass／2 fail）、12 組記憶體 smoke、8 組選取與 4 組 RGB16 合成；其失敗不追改。本輪[續驗](../../research/object-removal-survey.md#10-續驗與採用決策) 新增 34 組照片／選取 native strict pass、48 組合成與 3 組完整 Moebius 模型鏈。Moebius 顯式噪聲可重現，但固定噪聲後仍 strict fail；真實照片 pre-paste 有 33 個元素超界，近似貼回也未合格。沒有再嘗試巨型單圖匯出。

**採用裁決：** MI-GAN 只留快速工程對照，不作通用移除預設；真實照片與大洞出現不相關／新形狀，數值通過不能代替品質。EfficientSAM 只留人工可修正的提案研究；Moebius 已測 `pretrained` 因數值／後處理、整鏈資源與 VAE 來源暫緩；LaMa 權重資格維持 blocked。沒有選出產品模型，不開始 T2–T5。

**來源續查與重新開啟條件：** 官方原始 SDXL VAE 與 PixelHacker VAE 的 8 組元件比較 fail；FP16-Fix 轉 FP16 後內容相同不證明歷史來源／散布權利。`pretrained`／`ft_places2` 都已有前測，後者仍有畫質與完整鏈數值反例，不再列為「未下載／未測」。下一個學習式候選先閉合精確權重用途、完整 base／材料及 canonical 接受區域契約；AOT／OSOR 等的具體缺口與不同處置見第 13 節。原 T1 的固定資料、三位評審、精確原生後處理及整鏈／平台 gate 不變，24 張 T5 留出集不供調參。不要以 posterior mean、放寬誤差、近似 blur 或小圖結果取代 gate；以下方框仍表示完整產品交付條件。

- [ ] 同場比較 **MI-GAN-512 浮點核心**、**LaMa／big-lama 的明確轉換版本**及 **Moebius 具名 student＋PixelHacker VAE＋採樣迴圈**。依可行性先跑 MI-GAN，不把工程基準的完成順序當畫質名次；Moebius 已測 `pretrained`／`ft_places2` 的前測與本項完整資格分開記錄，不能由架構／檔案大小或跨資料版本比較推論資格。LaMa 權重權利、Moebius repo／模型卡標籤差異與 VAE 來源／條款先釐清；取得相應使用資格後才下載至指定模型／忽略證據目錄。保留位置、bytes、SHA-256、程式碼／權重／base／轉換器／依賴授權與日期。選取優先試 **EfficientSAM-Ti encoder＋decoder**，SAM 2.1 作比較候選；其他研究候選不因列入 Survey 自動加入產品相依。
- [ ] 以原作者參考輸出建立非對稱已知小圖＋真實照片檢查；先暴露 mask polarity、RGB/BGR、range、resize／padding、crop-back 或 exporter 不一致的失敗，再修前後處理。不要用模型輸出自己算出「預期」值。
- [ ] MI-GAN 官方便利 pipeline 為 UINT8 進出；產品候選採 FP32 核心，保留 FilmBatch 的高精度合成。不直接把完整 16-bit 圖經 UINT8 wrapper。core 的固定尺寸／noise/filter 與 wrapper 的任意尺寸不能混為一談。
- [ ] manifest 固定 `modelId`、checkpoint／ONNX SHA-256、exporter revision、opset、dtype／shape／輸入輸出名稱、色彩域、mask polarity、normalization、resize／padding、context margin、output mapping；**數值從實際 artifact 讀取並驗證**，不可由同家族名稱推測。
- [ ] 在現有 .NET ORT 先強制 CPU 實跑，檢查有限值、與參考輸出差異、mask 外原像素、模型缺失、取消後再次推論。CoreML／DirectML 另外紀錄實際圖分區與 fallback；CPU 是合法的真實推論，不要求也不宣稱 100% ANE／GPU。
- [ ] 每個目標／裝置記錄 cold load／warm encode／mask decode／完整 ROI fill／full-frame render 的 p50／p95、樣本數與冷暖定義、峰值程序記憶體、尺寸／精度／provider、取消回到可互動所需時間。Moebius 計入 VAE／採樣及全部活躍推論元件，不以 requirements 中的 CUDA 名稱直接裁定 runtime。參數量／檔案大小不是 activation 峰值或 iOS jetsam 證據。
- [ ] 執行 Survey 第 6 節固定方案：40 場景、8 類各 5 張，T1 開發集每類 2 張共 16 張，T5 留出每類 3 張共 24 張；至少 3 位人類評審、先相同人工 mask，再評人工＋協助選取。候選拒絕／OOM／逾時留在分母；依 Survey 的逐例與整體／分層 gate 決定。未達畫質／資源／授權資格時拒絕 artifact，保留原因，不改名成已成功候選。
- [ ] Gate 輸出：指定一個通過的本機模型、可支援的 ROI／解析度與平台／資源限制；若選取模型失敗保留手動選取完成整條填補路徑，協助選取不得顯示成功。若所有填補候選都失敗，明確回報模型／runtime 缺口，不開始假 AI 功能。

**驗收檢查 T1-模型資格：** 真實 artifact 的 native 成功、取消與參考輸出，完整 manifest 與各平台 `pass/fail/blocked/not-run`；無本機模型執行證據不得勾選。

## T2 — 可保存、可復原的修補與 canonical 合成

**依賴：** 設計契約；不依賴模型下載即可用獨立、固定的像素 fixture 驗證合成與儲存，但不把此結果算成 AI 推論成功。**Human Review：** 專案格式變更的安全失敗與舊版讀取行為。

**產出：** `RetouchStack`／`SetRetouch`／`RetouchAssetStore` 與 `RetouchRenderer`；可在 throwaway CLI driver 生成真正資產、套用、undo／redo、重開並解碼輸出，沒有 UI 也有可執行交付。

- [ ] 先寫並看見以下真實消費端失敗，再加入最小實作：`ApplyUndoRedo_ReplaysAcceptedPixelsWithoutModel`、`SaveFailure_LeavesPhotoAndUndoUnchanged`、`ProjectMove_PreservesPatchReferences`、`CorruptOrEscapingAsset_IsRejected`。每個預期使用手算的非對稱影像或獨立編碼器輸出。
- [ ] Core 加入不可變逐張資料；更新手寫 `PhotoEntry.Equals`，避免有 patch 但被 `PhotoEditCommand.IsNoOp` 略過；更新 `Project.Open/SaveCore`、JSON source generation、格式 3 與 v1／v2 無修補讀取。
- [ ] bbox RGB16 PNG／灰階16 mask 寫暫存→解碼核對與雜湊→原子啟用→revision-checked JSON 提交；候選在顯示／接受前已規範為這組 RGB16／mask16 值，新增 `AcceptedPreview_ReopenPreservesQuantizedPatchPixels`，以非整數 code 值的模型輸出暴露「先接受 float、存檔才量化」的變色。注入 disk-full、JSON revision conflict 與重開缺檔失敗，不更新記憶體成功狀態。驗證相對路徑、symlink、溢位及資產尺寸上限。
- [ ] 原有 `CpuPipeline` 抽出內部 `RenderToRetouchPlane`／`FinishRetouchPlane` 邊界，重用原步驟而非複製一套效果。插入點為 `ToneSplit` 後、`Detail` 前；無修補不建立 retouch frame／載入模型／做額外整圖轉換。
- [ ] 共用 `RetouchRenderer` 先 full-res 合成與完成後段，才縮小；更新 renderer context 與全部消費端。新增 `Composite_PreservesOutsideMaskAndAlpha`、`RetouchDisabled_PreservesExistingPixels`、`ProxyAndExport_UseAcceptedFullFrame`，不放寬既有容差。
- [ ] 實作 keys／過期分類與可復原重套用；新增 `UpstreamToneChange_RequiresExplicitRebase`、`GeometryOrSourceChange_RejectsRebase`、`UndoBaseChange_RevalidatesRetainedPatch`、`EarlierPatchChange_InvalidatesNonOverlappingSuffix`。最後一例後序的 context 包含前序 patch，mask 不重疊，防止錯用交集失效。
- [ ] `ResetAll` 清除並可復原修補，普通 paste／preset 完全不帶修補；新增 `PresetAndPaste_DoNotTransferRetouchAssets`，覆蓋 full／selective／preset-only、更新預設與移除單個模組。
- [ ] 有界 session 資料與資產 GC 保護保存狀態／undo／redo／提交 pin；以兩個持鎖程序與 revision 變更演練，不能刪除另一個已提交操作引用的 blob。保留有效資產不以「快取清理」名義刪除。

**驗收檢查 T2：** 合成／遮罩、命令／資產、安全與依賴測試；另執行 throwaway driver 的 apply→save→程序重啟→無模型 export，獨立解碼 pixel fixture，觀察缺／壞資產會逐張失敗。測試不是唯一證據。

## T3 — 真實本機 AI、選取輔助與完整消費端

**依賴：** T1 實際 manifest、T2 型別與資產合成；**產出：** 可由 CLI 操作的真正本機移除與正式專案重播，UI 接入同一 API。

- [ ] 先保留模型／consumer-visible 邊界失敗：`MissingModel_DoesNotCommitOrReportSuccess`、`MaskPolarityAndCropBack_PreserveMarkedRegion`、`CancelledNativeReturn_CannotPublishOrCache`、`AcceptedPatch_ExportsAfterModelRemoval`。用真實 ORT 圖驗前後處理；真實候選權重另行 smoke。
- [ ] `RemovalModelCatalog` 只接受 T1 的已知 artifact；可借用現有模型 store 的檔案／雜湊工具，不借用降噪的 tensor 或整圖分塊假設。顯式安裝／匯入才連網，固定雜湊驗證後原子啟用。
- [ ] `RemovalProcessor.Generate(frame, mask, ct)` 執行唯一選定模型、固定 ROI／context／padding／range、返回綁定 frame keys 的 `RemovalProposal`。空／壞 mask、錯尺寸、非有限輸出與超出資源上限明確報錯；不能回原圖或換傳統方法當成功。
- [ ] 使用 T1 通過的選取模型（優先 EfficientSAM split encoder／decoder）快取目前 frame 的 embedding；正負點／框改變只重算 decoder，frame 改變即清除。mask 解析回 canonical 平面且由使用者確認；未通過模型 gate 時禁用協助選取，手動選取仍能使用真正填補。
- [ ] 推論與降噪共用有界排程入口；native `RunOptions.Terminate` 加 post-run token／generation check。取消不重建 GPU、不寫 successful cache，下一次明確產生可以正常使用 session。
- [ ] Preview／thumbnail request、`BatchExporter` jobs 與 CLI export 帶逐張 context；更新 `ExportModels`／`export_log.json` 的 patch／base／model／provider／rebase／renderer 欄位。無修補記錄為無修補，不捏造 model report。
- [ ] CLI 提供明確子命令 `removal-models status`、`removal-models import <model-id> <path>`、`remove <photo> --mask <mask.png> --model <model-id> --out <new-file>`；一次命令列操作就是明確接受，輸出新檔與 provenance，不覆寫原檔。既有專案 export 讀已接受資產，不要求 model-id。
- [ ] 本機 HTTP(S) 不可用的 throwaway 環境執行實際 CPU 模型 remove、同專案兩張輸出、刪除模型後重播、取消後重跑；獨立解碼 RGB16／alpha／ICC／EXIF，核對原檔 SHA-256 未變。不得用代理連線失敗代替完整「無外送」檢查，需同時檢視產品 networking 入口與觀察出站請求。

**驗收檢查 T3：** 上述測試加實際 CLI 權重推論／無模型重播／離線／取消 smoke；Fourier export、provider 分區或品質不符退回 T1，不加假 fallback。

## T4 — 桌面／行動完整操作與批次恢復

**依賴：** T2／T3 API；同一 `RemovalViewModel` 驅動兩個 host，Core／Engine 不重複。**Human Review：** 真實照片、原生裝置權限仍須使用者授權。

- [ ] 先驗證狀態機失敗：`Apply_AcceptsOnlyCurrentFrameAndCandidate`、`PhotoSwitchOrSuspend_DiscardsLateResult`、`CancelStroke_AddsNoUndoEntry`、`TwoFingerPan_DoesNotCommitConnectingStroke`。測試 pending native work 再換照片／Suspend，不以手動發一則假 cancel progress 代替。
- [ ] 在 `ICanvasTool`／`PreviewCanvas` 的既有互斥路徑補足筆刷 hover／鍵盤；新 `RemovalTool` 保存正規化向量筆劃與 final mask，螢幕邊界裁切、不把平移當繪圖。四角、90°、翻轉、拉直／裁切、鏡頭／多格與代理尺寸驗證同一平面。
- [ ] 面板實作可見的模式／加減選／大小／模型狀態／產生／取消／比較／套用／停用／刪除。候選不修改照片；Apply 再查 service、photo、source、settings、前序 ops、generation。
- [ ] 實作硬／內容過期 badge、filmstrip 計數與明確重套用預覽；修改早期 op 時顯示後續受影響操作。批次重新產生可取消、逐張檢視接受，匯出對仍過期照片逐張報錯，沒有自動接受或偷偷重生成。
- [ ] 桌面 Dock／行動 PanelSelector 接入同一 View；補兩份 resx、accessible names、鍵盤動作、窄螢幕固定可到達的套用／取消。iOS `Suspend/Resume` 取消草稿；owned project copy 帶完整資產。
- [ ] 啟動實際桌面 App：一般照片與 RAW 各完成筆刷→修正 mask→真實推論→比較→Apply→undo／redo→關閉重開→改 WB／改裁切→過期處置→批次匯出；檢視實際畫面與輸出，不只 headless tests。
- [ ] 行動 View 先驗窄／寬、低高度及兩指互動；若要宣稱 iOS 產生支援，另以同一正式 iOS app、實際模型、原生 link、Simulator 與真機分別驗證。Mac 行動視窗不代替 iOS，iOS 無資源資格時顯示原因而非可執行按鈕。

**驗收檢查 T4：** 真實 UI／模態／生命週期／過期批次與 JPEG／PNG16／TIFF16／已渲染 DNG 解碼檢查；retouch-before compare 與「原圖」互不混淆。DNG 不宣稱保留原始感光樣本。

## T5 — 留出品質、平台與交付閉合

**依賴：** T4 完整可用流程。**Human Review：** 最佳品質的取捨、模型條款與正式支援裝置由產品擁有者依報告決定，不由模型評審自己批准。

- [ ] 使用未拿來調參的照片／mask，執行 Survey 固定場景與盲評；分開填補畫質、選取錯誤、總操作時間、重試次數、拒絕／崩潰／逾時，不只報最好看的樣本或成功案例平均。
- [ ] 紋理、直線／文字、陰影／倒影、人物邊緣、大洞、細線、畫框邊緣、高 ISO、RAW 高光與強 clarity 必須列出最差例。生成更合理不等於還原真實遮蔽背景；不宣稱 Lightroom 同等品質。
- [ ] 計算遮罩合成外精確不變、剩餘空間濾波的影響範圍、alpha／顏色／ICC、尺寸／crop、保存／無模型重播。代理候選不能取代正式尺寸品質；比較 core FP32 與低精度／8-bit 版本的 banding／色偏才考慮最佳化。
- [ ] 實機資源超標時縮小支援 ROI／停用該模型能力必須反映在 UI 與文件，不把 OOM 當隨機失敗或靜默降低品質。筆刷互動 p95 目標 ≤33 ms、取消動作 UI 回應目標 ≤100 ms；推論實際停止時間另外量測，不承諾 kernel 即時終止。模型延遲／記憶體公布實測，不移用論文速度。
- [ ] 各支援平台執行專用回歸、完整既有 suite、正式 publish／原生依賴／授權閉包與可執行檔 smoke。編譯、封裝、native 執行、Simulator 與實機分欄；未跑的平台保留 not-run，不寫 pass。
- [ ] 經 smoke 後更新本 ADR／Survey 的採用註記、architecture／building、`VERIFY.md`、第三方 notices 與 docs 導覽；清除這次實作自己產生的暫時 driver，保留輸入身分與精簡證據。只有新的使用者授權才 commit／push／發佈。

## 驗證命令與證據契約

以下是**實作時**的命令，不是本輪已執行結果；新測試／命令必須先實作後才能執行。

```sh
dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~Retouch|FullyQualifiedName~Removal'
dotnet test tests/FilmBatch.App.Tests -c Release --filter 'FullyQualifiedName~Removal'
dotnet test FilmBatch.sln -c Release

dotnet run --project src/FilmBatch.App -c Release -- --smoke dist/object-removal-implementation-evidence/app-smoke.txt
dotnet run --project src/FilmBatch.Cli -c Release -- removal-models status
```

另外使用 T3 新 CLI 的 `remove` 對**自有／已核准**來源與 mask 執行，`--out` 指向新證據目錄；T2 的 throwaway driver 和原生 GUI／iOS 操作記錄各自保存，不能用命令 exit 0 取代畫面／像素檢查。模型／照片 manifest、輸出 SHA-256、runtime／provider／實際尺寸、時間／記憶體、失敗與原檔未變檢查是可重現報告的最小欄位。

## 需求對照與停止條件

| 需求 | 負責切片與具體檢查 |
|---|---|
| OR-01／OR-13 | T3 選取模型／取消；T4 `RemovalToolTests` 的畫框座標、鍵盤／兩指／窄寬畫面 smoke |
| OR-02／OR-12 | T1-模型資格；T3 `MissingModel_DoesNotCommitOrReportSuccess`／實際 native；T5 平台閉包 |
| OR-03／OR-04 | T2 `ApplyUndoRedo_ReplaysAcceptedPixelsWithoutModel`；T4 `Apply_AcceptsOnlyCurrentFrameAndCandidate`／重開 smoke |
| OR-05 | T2 `Composite_PreservesOutsideMaskAndAlpha`、`ProxyAndExport_UseAcceptedFullFrame`；T5-剩餘濾波支撐／品質 |
| OR-06 | T2 上游／幾何／undo／non-overlapping suffix 四個測試；T4 過期批次 |
| OR-07 | T3 `CancelledNativeReturn_CannotPublishOrCache`；T4 `PhotoSwitchOrSuspend_DiscardsLateResult` |
| OR-08 | T3 `AcceptedPatch_ExportsAfterModelRemoval`／CLI；T4 四格式與縮圖 smoke |
| OR-09／OR-10 | T2 `PresetAndPaste_DoNotTransferRetouchAssets`、`SaveFailure_LeavesPhotoAndUndoUnchanged`、`CorruptOrEscapingAsset_IsRejected` |
| OR-11 | T1 比較設計；T5 留出集、盲評與最差案例報告，由產品擁有者接受 |
| OR-14 | T3-離線與 networking 入口／實際出站觀察；雲端未接入，接入前另行授權 |

**Definition of Done：** 支援範圍內 T1–T5 的 end-to-end 行為與對應需求完成、真實模型／實際 UI／保存與四格式匯出有證據，原有路徑無回歸，授權與平台限制如實記錄。外部資格失敗是拒絕候選或明確 blocker，不是把 scaffold 改稱完成。

**仍未解的資格項：** 最終採用的合法模型、固定品質比較／盲評、Moebius 完整鏈的數值與精確後處理、產品完整尺寸成本、可支援 ROI、Windows／iOS 裝置資源與生命週期。Moebius 顯式噪聲與完整 .NET 鏈已執行，但不是數值合格；候選 checkpoint／ONNX bytes、digest 與本次 macOS 數值／provider 證據各自保留。其餘由 T1／T5 解決，不新增空白 provider、假資料或未測能力，不在本機失敗時偷偷使用網路。
