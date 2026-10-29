# Composable Color Transforms Implementation Plan

> **For agentic workers:** Use the installed `executing-plans` skill to implement this plan task-by-task, with review checkpoints. Steps use checkbox (`- [ ]`) syntax for tracking. This is a research-backed plan, **not authorization or evidence that product implementation has started**.

**Goal:** 在既有 film 工作流程內直接執行有界的矩陣／曲線／3D 表格序列，避免把新 CLF 一律烘焙、clamp、量化成 FBLUT，同時保留既有 LUT 結果與 key。

**Architecture:** Core 提供不可變 transform、嚴格 CLF subset 及保存原始 CLF bytes 的資產格式；Engine 提供 C#／固定 WGSL 執行器，在既有線性 film 位置或 display encode 後擇一執行。Preview、Loupe、thumbnail、export、CLI 共用現有 renderer／排程器；不引入通用效果 graph 或 OCIO 產品依賴。

**Tech Stack:** .NET 10／C#、Avalonia 12.1.4／MVVM、WebGPUSharp 0.5.7／Dawn／WGSL；OpenColorIO 2.6.0 僅作外部 CPU oracle。

**Spec:** [可組合色彩轉換研究與設計契約 CT-2](../../research/composable-color-transforms.md)；[DCP-1 補充](../../research/composable-color-transforms.md#7-dcp-1raw-相機描述檔擴充)是獨立 RAW profile 契約。CT-1 的審查結論與任何驗收宣告已 stale；CT-2、DCP-1 的實作項目及 AC 均為 pending。再修改任一契約時增加其版本並重新標記受影響項目。

## Global Constraints

- 本文件所有未勾選項目都是**未實作／未驗收**。研究已執行的數值證據僅見 spec §4，不等同下列產品 gate。
- 沿用既有 `film.*` 設定，不新增第二套 look module；同一選擇只走 legacy LUT 或 transform 其中一路。
- 兩個固定 stage：`WorkingLinear`（原 film hook）、`DisplayEncoded`（RAW／raster display encode 後、basic display 前）；metadata 固定位置，不從 descriptor／bit-depth 推論。
- 明確 input/output：`LinearSrgb`、`Srgb`、`ProPhotoGamma18`。wrapper／strength／warmth／fade／alpha 嚴格遵循 CT-2 §3.1。
- 接受 Matrix、Range、普通 LUT1D／3×1D、LUT3D；完整集合及正規化公式見 CT-2 §3.2；未知節點或屬性先拒絕，不跳過、不偷偷烘焙。
- CLF 128 MiB；FBLOOK 128 MiB＋16 bytes；64 nodes；XML depth 16；LUT1D 65,536 點；LUT3D 128³；合計已正規化資料 32 MiB。GPU 還須服從裝置上限及既有 budget。
- Legacy `.cube/.fblut/Hald`、36 個 built-in looks、FBLUT1／2 bytes、`user-<16hex>` key 與缺檔 bypass 不遷移。新資產為 `look-<64hex>.fblook`，缺檔／損壞必須可見失敗。
- No new native runtime dependency、script/shader loading、Sony assets、NPY/NPZ import、RAW decoder replacement、output profile change。
- 每個完成項目獨立 review／commit；需改公開 symbol 時先用可用的 LSP references。若仍無 LSP，記錄限制並完整搜尋既有使用點，不留 obsolete aliases。
- 新測試只涵蓋消費者可見數學、邊界、失敗及相容性；不用 source-text／wiring-only assertions。每個切片另以實際 API／程式 smoke 驗證，不能只看綠色單元測試。

## 1. Usage / 驗收契約

### 正常流程（目標行為，尚未實作）

1. 使用既有「匯入 LUT」選到 `.clf`；先在背景把檔案封存到有界、只讀的 owned temporary，解析其中 descriptor，再顯示 stage／input／output 三個宣告欄位。預選 `DisplayEncoded / Srgb / Srgb`，必須按確認接受；明寫這是使用者宣告，並非自動辨識。
2. 確認後，對**同一份**已封存 bytes 驗證、保存原檔及宣告；產生 `look-<64hex>`。沿用既有 imported tile、Detail／tooltip、選取與 undo；Detail 顯示 `CLF · Display · sRGB → sRGB` 等語系化內容。
3. renderer 在指定 stage 直接執行節點；預覽、放大鏡、縮圖與輸出使用相同 asset snapshot。重新啟動、載入 project／preset、複製貼上後仍是同一個 key／結果。
4. 重匯入 `.fblook` 使用其內含 declarations，不重新猜測；同 bytes 同 key。一般 `.cube/.fblut/Hald` 繼續既有流程與 cube 色域選項。

### 失敗與容易混淆之處

- 含 `Log`、外部 Reference、非法 Range style、DTD、超限陣列：明確告知不支援的節點／原因；沒有部分結果、沒有新選擇或 undo 記錄。
- 取消 picker／宣告 modal／複製或發布前的驗證：沒有新設定、正式資產或本次 owned 暫存。若取消或 context 切換發生在原子發布後，可留下有效共用資產，但不得套用到新 selection／project、建立 undo 或刪除該資產。
- 啟用的新 key 缺檔：preview 顯示失敗、thumbnail 不冒充成功、export／CLI 不輸出成功原圖。停用或強度為 0 可繞過該新資產。
- 舊 `user-` 缺檔：仍保留設定並跳過 film；不把這項既有相容性改成新錯誤。
- 取白平衡／Zone 樣本只分析早期線性來源，不應讀取／執行後段 transform。ProPhoto 最終輸出也不代表 display hook 收到 ProPhoto RGB。

### Acceptance IDs

| ID | 可觀察結果 | 負責 task／確定性檢查 |
|---|---|---|
| AC-CT-01 | subset 正規化、節點順序與拒絕邊界正確，不能只因 OCIO 接受就通過 | T1：`ClfFileTests`；手算 Matrix／Range＋惡意／超限 fixtures |
| AC-CT-02 | 1D 原節點不重採樣；3D B-fast、兩種插值與 tie 正確；RGB extended 值／alpha 合約成立 | T1／T2／T4：`ColorTransformTests`、`ColorTransformGpuTests`＋OCIO fixtures |
| AC-CT-03 | 原 CLF bytes／宣告完整保存；key 穩定且 domain/stage 不同不混淆 | T3：`ColorTransformFileTests`；重啟／重載與 `.fblook` 重匯入 smoke |
| AC-CT-04 | 舊 FBLUT bytes、key、missing-user bypass 及既有渲染數值不變 | T5：既有 `CubeLutTests`、`FilmLookTests`、`FilmLutTests`、`EffectPipelineTests` 與基線輸出 |
| AC-CT-05 | 新 look 只在指定 hook 執行一次；兩個 stage、RAW／raster、sRGB／ProPhoto 輸出都一致 | T5：`ColorTransformPipelineTests`＋實際 renderer／CLI end-to-end smoke |
| AC-CT-06 | 不產生每像素配置；GPU 資產受 budget／binding limit 約束，取消／fallback／disposal 不污染下一張 | T4／T5／T7：`ColorTransformGpuTests`、resource-lifetime cases、實際批次／切換 smoke |
| AC-CT-07 | UI 宣告可見、匯入可取消；mobile copy 在 cap 處停止；壞檔不改設定 | T6：`FilmLookViewModelTests`、`MobileWorkspaceTests`／`MobileDialogServiceTests`＋desktop/mobile native smoke |
| AC-CT-08 | `look-` 設定穿過 project/preset/paste/undo/thumbnail/Loupe/export；缺檔不假成功；取樣不解析後段資產 | T5／T6：settings／persistence／sampling／VM 行為測試與 native UI scenario |
| AC-CT-09 | 指定平台、精度、效能與文件 gates 有實際記錄，不能以 skip 或研究報告替代 | T7：§4 命令與證據 ledger；未執行的平台不得宣稱完成 |

## 2. 變更地圖與共享介面

以下 `Create` 均指實作階段，不是本次建立的產品檔案；名稱／介面是 CT-2 的交接契約。

| 區域 | 檔案與責任 |
|---|---|
| Core 模型／格式（Create） | `src/FilmBatch.Core/Film/ColorTransform.cs`：stage/encoding enums、immutable ops/data 與 caps；`ClfFile.cs`：streaming subset parser；`ColorTransformFile.cs`：16-byte envelope；`UserTransformLibrary.cs`：owned import、key、按需載入；`FilmLookAsset.cs`：兩種資產的互斥容器 |
| Core 相容接點（Modify） | `Film/FilmLookLibrary.cs`、`Params/ParamDef.cs`、`Params/SettingsJsonConverter.cs`；不新增 schema 欄位、不搬動 built-in 資產；T5 負責 resolver／settings cutover |
| CPU（Create/Modify） | 新 `src/FilmBatch.Engine/Rendering/ColorTransformProcessing.cs`；修改 `ModuleParams.cs`、`CpuPipeline.cs`、`FilmLookProcessing.cs`、`Imaging/PhotoAnalysis.cs`；`RgbEncoding.cs` 只重用既有 transfer／matrix，不改輸出 gamut mapper |
| GPU（Create/Modify） | 新 `src/FilmBatch.Gpu/Shaders/v1/film/transform.wgsl`、`src/FilmBatch.Engine/Rendering/ColorTransformKernel.cs`／`ColorTransformUniforms.cs`；T5 修改 `GpuEffectPipeline.cs` 的 dispatch／budget／disposal；沿用 GpuDevice、GpuBuffer、GpuCommandList |
| 匯入／共享 UI（Create/Modify） | 新 `src/FilmBatch.Engine/Film/FilmAssetImport.cs` 取代只回傳 LUT 的入口；新 `App.Shared/ViewModels/Dialogs/ColorTransformImportViewModel.cs`；修改 `FilmLookViewModel.cs`、`MainWindowViewModel.cs`、`FilmLookControl.axaml`、`IDialogService.cs`、兩份 `Strings*.resx` |
| Desktop／mobile（Create/Modify） | 新 `src/FilmBatch.App/Dialogs/ColorTransformImportDialog.axaml`／`.axaml.cs`；修改 `App/Dialogs/DialogService.cs`、`Mobile/MobileDialogService.cs`、`Mobile/MobileWorkspace.cs`、App.Tests project references；測試 fake dialog 同步更新 |
| 文件 | `docs/film-looks.md`、`docs/architecture/{core,engine,gpu,app}.md`、`docs/testing.md`、`docs/cli.md`、`docs/history.md`、`VERIFY.md`：按每個實際完成切片更新，不提前宣稱支援 |

關鍵簽章（可見性限現有 caller 所需，不新增 plugin interface）：

```csharp
ClfFile.Read(Stream source, CancellationToken cancellationToken = default) : ColorTransform
ColorTransformFile.Read(Stream source, CancellationToken cancellationToken = default) : ColorTransformAsset
ColorTransformFile.Write(Stream destination, ClfImportOptions options, Stream validatedSource) : void
UserTransformLibrary.Import(string root, Stream staged, bool isFblook, ClfImportOptions? options, string displayName, CancellationToken cancellationToken) : string
UserTransformLibrary.Resolve(string root, string key) : ColorTransformAsset
UserTransformLibrary.List(string root) : IReadOnlyList<TransformLibraryItem>
FilmLookLibrary.Resolve(string key) : FilmLookAsset?
ColorTransformProcessing.Pixel(Vector3 value, ColorTransform transform) : Vector3
ColorTransformKernel.RequiredBytes(ColorTransform transform) : ulong
ModuleParams.From(SourceImage source, Settings settings, RgbEncoding encoding = RgbEncoding.Srgb, bool resolveFilmAsset = true) : ModuleParams
IDialogService.ShowColorTransformImportAsync(ColorTransformImportViewModel viewModel) : Task<bool>
FilmAssetImport.Stage(string path, string root, CancellationToken cancellationToken) : StagedFilmImport
FilmAssetImport.Publish(StagedFilmImport staged, ClfImportOptions? options, string displayName, CancellationToken cancellationToken) : string
FilmAssetImport.ImportLegacy(string path, FilmLutColorSpace cubeSpace) : string
```

`ClfImportOptions` 是 stage/input/output 三個 enum；`ColorTransformAsset` 包含 options 與 parsed chain；`TransformLibraryItem` 包含 key、bounded name、可讀 header 的 options 或 invalid 狀態。`StagedFilmImport` 擁有封存的唯讀暫存與擷取的 descriptor／檔案種類／捕捉的 root，`Dispose` 關閉並刪除該暫存；只有它的相同 bytes 可提交。`.clf` 必須給 options，`.fblook` 必須使用其內含 options；不相符即拒絕。原 CLF 是 stream／owned file，不在每個 render snapshot 複製 128 MiB bytes。`ColorTransform` 保存已正規化的不可變 float32 ops/data；建構／parse 可配置，pixel loop 不可配置。傳入 stream ownership 保留 caller，library 自己開啟的 stream 由 library 關閉。

`FilmLookLibrary.Resolve` 對非 `look-` 的未知／缺檔 legacy key 可回 null；對 `look-` 必須回非 null 或將該 asset 的 I/O、解析、hash 錯誤包成 `ColorTransformAssetException`，保留 inner exception，不包裝取消。`ColorTransformKernel.RequiredBytes` 不配置 GPU 資源，讓 T5 在分帶／fallback 決策前計入實際 ops＋table 大小。

既有 `FilmLookLibrary.TryLut` callsites 完整改為 `Resolve` 後移除舊公開入口，不留 alias。`ModuleParams.FilmLut` 改為 `FilmAsset`，legacy branch 從 asset 取得原 `FilmLut`；直接注入 LUT 的 parity test 同步包成 legacy asset，**不換樣本、不放寬容差**。`LutFileImport.Open` 的 `.cube/.fblut/Hald` 實作移到 `FilmAssetImport` 的 legacy branch，舊 class 無 caller 後移除；`UserLutLibrary` 仍是實際被使用的 legacy store，不是 shim。

## 3. Tasks

### T1 — 可測的 Core subset 與拒絕邊界

**Dependencies:** CT-2；無其他 task。**Files:** 新 Core `ColorTransform.cs`、`ClfFile.cs`；新 `tests/FilmBatch.Core.Tests/{ClfFileTests,ColorTransformTests}.cs` 與少量 synthetic fixtures。**Human review:** 是，接受集合／尺度規則。**Risk:** 輸入語義錯誤比 parse 成功更危險。

- [ ] 先以手算 fixtures 固定 Matrix／Range 正規化、chain order、普通 1D 節點值／中點；例如 `10i→12i` matrix 用 raw diagonal `4095/1023` 應保持 normalized RGB，raw offset `409.5` 應加 `0.1`。用交換順序會改變結果的 fixture，不能全用 identity。
- [ ] 新 parser 只接受非 namespace 的 `ProcessList`、數值為 3.0 的 `compCLFversion`、非空 id、CT-2 白名單及嚴格 UTF-8；明訂早期版本、namespace、`.ctf` 與 OCIO CTF-only 屬性不支援，不把 permissive OCIO parser 當 acceptance oracle。
- [ ] 依 dimensions 先驗 checked count／32 MiB cap，再逐 token parse，拒絕缺／多 token、NaN/Inf、溢位、非法 in/out depth 接續、未知 style／attribute、外部 entity、超深／超長／超 node 數。測 `Info` 子樹、comments、`xml:lang`、三種 descriptor 與 inert attributes 可存在但不改數學、無外部 I/O；超過 metadata cap 必須拒絕。
- [ ] Range 專測單側 clamp 的 normalized 相等與 bit-depth scale：合法 min/max pair 接受，單側 offset、單側 `noClamp`、不完整 pair、倒置 bounds 拒絕；雙側 `maxOut==minOut` 接受。
- [ ] 程式化建構與 CLF parse 共用相同不可變 ops/data 驗證，不提供繞過 host offset／length 檢查的公共入口。enum／record 只覆蓋四種 opcode。
- [ ] 以 CPU 可呼叫的 isolated smoke 讀合法 fixture 並輸出正規化結果；對不支援節點確認錯誤包含節點與原因，且沒有 partial chain。
- [ ] 執行 `dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~ClfFileTests|FullyQualifiedName~ColorTransformTests'`；review、更新對應契約說明、commit。

**完成判準：**AC-CT-01；解析集合封閉且有反例，不要求這階段已整合 UI／產品 renderer。

### T2 — CPU 原生數學與獨立 oracle

**Dependencies:** T1。**Files:** 新 `ColorTransformProcessing.cs`；`ColorTransformTests.cs`；oracle 僅為臨時工具或測試資料產生器，不加產品 package。**Human review:** 是，oracle／數值容差。**Risk:** 自製參考與實作共享同一錯誤。

- [ ] 先寫會區分 B-fast/R-fast、trilinear/tetrahedral 的非對稱 2³ fixture；加入 tie `r=b>g`、全部六種 tetrahedral 分支、端點、負輸入、超白輸入、負／超白 table output、alpha=0／非整數 alpha。
- [ ] 實作 `Pixel` 四種 opcode switch；1D 用 linear、3D 用規格決策的兩種插值。無每像素 LINQ、delegate／boxing、陣列配置、表格複製。只在 parser 做 bit-depth normalization。
- [ ] CPU image 執行按既有平行策略，取消檢查放在有界 row／chunk，不在 inner RGB component 配置；保留 alpha bits。wrapper 與混合規則留給 T5，先證明純 chain。
- [ ] 以 OCIO 2.6.0 `OPTIMIZATION_NONE + INTERP_DEFAULT` 產生獨立 fixture 結果；先用手算 Matrix／Range 交叉驗證 oracle 使用方式。保留版本、原檔 hash、input/output 與來源授權；一般 test 不依賴每台機器安裝 Python／OCIO。
- [ ] 重播 spec §4 的曲率實驗；驗收 `abs(actual-reference) <= 5e-6 + 2e-5*abs(reference)`（封存 fixture 上），alpha exact。原表節點保留、noClamp extended 值不消失；33³／65³ 烘焙結果只作對照，不作正確 oracle。
- [ ] 執行 T1 的 filter；以實際 CPU evaluator 輸出報告，不以 assertion count 代替數值；review、文件、commit。

**完成判準：**AC-CT-02 的 CPU 部分；不宣稱 GPU、parser 完整 CLF conformance 或 float64 bit-exact。

### T3 — 原始資產儲存與內容 key

**Dependencies:** T1；與 T2／T4 獨立。**Files:** 新 Core `ColorTransformFile.cs`、`UserTransformLibrary.cs`、`FilmLookAsset.cs` 與 `ColorTransformFileTests.cs`。只做 storage/model，不修改既有 resolver、settings 或 pipeline；這些統由 T5 cutover。**Human review:** 是，格式／相容性。**Risk:** bytes/key 或匯入發布邊界不一致。

- [ ] 先固定 CT-2 envelope 的 round-trip bytes、所有 enums／reserved／length／trailing rejection；同 source bytes＋options 同 key，stage 或 encoding 改變則不同 key；`.name` 改變不改 key。
- [ ] `UserTransformLibrary.Import` 對呼叫者提供的封存 stream 做有界 owned-temp import，對同一份 bytes parse／hash／write 後原子發布；發布前取消或非法內容只刪本次暫存；發布後可保留合法共享資產。existing target 不覆寫未驗證內容；`.fblook` 驗 header 與原始 source，沿用內含 declarations。
- [ ] `List(root)` 只列 key、bounded name 及 16-byte header 取得的 options／invalid 狀態，不載入或驗證全部 chain；`Resolve(root,key)` 才完整驗 hash、parse 並提供不可變 snapshot。root＋key 最多一個 strong cache entry，root 改變／測試 Reset 清空；GPU cache 不在 Core。
- [ ] 測封存前後修改原來源、不同 root、重複匯入、發布前／後取消及未選取壞檔；保存的原始 bytes 必須與實際驗證 bytes 相同，發布後取消不得刪除共用資產。
- [ ] 執行 `dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~ColorTransformFileTests'`；另外用兩次獨立程式執行證明 save→process exit→load 的 bytes/key／數學結果一致；review、文件、commit。

**完成判準：**AC-CT-03。AC-CT-04／08 的舊資產及 settings 整合由 T5 驗收。跨專案測試不得並行修改全域 `AppPaths.OverrideRoot`；沿用既有隔離約定。

### T4 — 單 dispatch GPU 執行與資源生命週期

**Dependencies:** T1／T2 的數學與 fixture；不依賴 T3／UI。**Files:** 新 `film/transform.wgsl`、`ColorTransformKernel.cs`、`ColorTransformUniforms.cs`、`ColorTransformGpuTests.cs`；不修改 `GpuEffectPipeline.cs`，由 T5 負責正式管線與 budget cutover。**Human review:** 是，layout／budget／生命週期。**Risk:** buffer offset、floating-point 分歧、反覆換圖時 stale resources。

- [ ] GPU test 直接注入不可變 chain，不依賴全域使用者 library。用 T2 同一組輸入及獨立 expected，不能只拿另一個同錯誤的程式比較。
- [ ] 實作 CT-2 的 16-byte op record／flat f32 table；固定 bindings 為 source texture、destination RGBA32Float、uniform、read-only ops storage、read-only data storage；host offsets／byte counts 和 WGSL stride 以實際 dispatch 驗證。
- [ ] `ColorTransformKernel` 接受既有 `GpuDevice`／`ShaderLibrary`，管理單一 device-local chain buffers，暴露無配置的 `RequiredBytes` 供 T5 在 `CanRender` 前估算；record 單個 dispatch 到呼叫者的 `GpuCommandList`，以 caller 提供的獨立 uniform buffer 使用共同 wrapper／mix contract。不逐 node 上傳整張圖、產生 arbitrary WGSL 或逐 node readback。
- [ ] 單一 device-local cache 以已驗 hash 的 `look-<64hex>` 識別資產，不以可能重建的 chain 物件參考當 key，也不另算一份 table hash；caller 必須只把驗過的 key 與其 immutable snapshot 配對。測同 key 重新解析為不同 snapshot 時重用 buffer、兩個 device 不共用資源，以及 A→B→A 替換後正確重傳且 cache 不增長；T4 isolated test 可使用綁定 fixture 內容的合成識別。
- [ ] 在提交與完成邊界處理 cache 替換、cancel、Dispose／device loss。renderer 的 budget／binding limits、分帶和 CPU fallback 實際接入及測試由 T5 完成。
- [ ] 執行 `dotnet run build/CheckShaders.cs` 與 `dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~ColorTransformGpuTests|FullyQualifiedName~GpuResourceLifetimeTests'`。`naga` 不在 PATH 時依既有 `--naga` 參數指定；GPU skip 不能算平台通過。
- [ ] 在真實 Metal 裝置及 Windows DX12 執行同一小型 driver，保存 adapter/native versions、actual backend、數值、upload bytes／dispatch 次數；review、文件、commit。

**完成判準：**AC-CT-02 的獨立 GPU kernel 部分；AC-CT-06 的正式 renderer／budget 整合仍待 T5／T7。容差同 T2 的固定 fixture；WGSL 允許 fusion，不要求 CPU／GPU 逐位相同。

### T5 — 兩個 hook、所有 renderer consumers 與 bypass

**Dependencies:** T2／T3／T4。**Files:** `FilmLookLibrary.cs`、`ParamDef.cs`、`SettingsJsonConverter.cs`、`ModuleParams.cs`、`CpuPipeline.cs`、`GpuEffectPipeline.cs`、`FilmLookProcessing.cs`、`PhotoAnalysis.cs`、`RenderScheduler.cs`；新 `ColorTransformPipelineTests.cs`；更新全部受影響的 `FilmLookTests.cs`、`EffectPipelineTests.cs`、`FilmLutTests.cs`、settings／persistence 行為測試。**Human review:** 是，stage／output domain／舊輸出。**Risk:** 顏色正確但放錯位置、重複 tone map、identity wrapper 改 rounding。

- [ ] 先封存合法 legacy 設定的 consumer-level numeric baseline，包含 sRGB／ProPhoto LUT、RAW／raster、warmth／fade／strength／alpha。只改必要 API 呼叫，不改 fixtures 或縮小驗收集合。
- [ ] `FilmLookLibrary.Resolve` 依 prefix 分流，遷移全部 `TryLut` callers 並移除舊入口；對有效 `look-` 失敗統一包 `ColorTransformAssetException`，其他未知／缺檔 legacy key 沿用 null bypass。測缺檔、損壞、hash mismatch、未選取壞檔與既有 `user-` 行為。
- [ ] 將 `look-<64hex>` 加入 `film.look` 驗證，並維持既有 `user-`：`ParamDef.Coerce` 的 key 是未限定的 `"look"`，不能據此放寬其他模組；在 `ParamDef` 增加內部 film-look 選項標記，由 `ModuleCatalog` 僅在 `film.look` 設定，兩種 user key 僅在此標記下通過。`SettingsJsonConverter` 使用完整 key `"film.look"`。測其他同名 choice 對兩種 user key 仍回預設、合法 legacy key 不變。
- [ ] `ModuleParams.FilmAsset` 決定只在一個 hook 執行，且只有 `resolveFilmAsset && FilmOn && FilmStrength > 0` 才解析。WorkingLinear 使用原 film 位置；DisplayEncoded 置於完整 display encode 分支之後（RAW 及 raster 皆涵蓋），不得插在單一 RAW branch 裡。
- [ ] 包裝轉換使用既有矩陣來源與 sign-preserving transfer；ProPhoto wrapper 不呼叫輸出 gamut compression。新 look 的原值與結果均以線性 sRGB 做 warmth/fade/strength；Display 回 extended sRGB。保持 legacy 算式／求值順序，不為抽 helper 改結果。
- [ ] `GpuEffectPipeline` 以 T4 `RequiredBytes` 在配置前計入 `ResidentBytes`／`CanRender`／分帶資產與 binding capacity；`TrimSources` 也須計入實際常駐的 transform buffers 及仍在途而尚未釋放的舊 buffers，避免 source cache 加資產超出 budget。不合限制使用既有 CPU fallback。測不同 band 計畫結果、切換資產時的 budget、途中取消後下一張、device-loss／OOM，不新增假成功分支。
- [ ] strength=0／disabled 真正 bypass。`RenderScheduler.TryRender` 對 `ColorTransformAssetException` 跳過第一次 device-reinit retry，直接走既有 failure event；preview、thumbnail、export／CLI 報失敗而非成功原圖。不要把一般 legacy I/O 全部排除重試。
- [ ] `PhotoAnalysis` 的所有 `ModuleParams.From` 明確傳 `resolveFilmAsset:false`；早期 sampling 不執行、不載入 film。Normal preview／Loupe／thumbnail／export 仍預設 resolve。測 unavailable 新 key 不阻擋取樣，但會阻擋啟用的成品 render。
- [ ] 用非交換 Matrix／tone fixture 證明 stage 不可互換且只執行一次；矩陣驗算不能只比較 CPU↔GPU。涵蓋兩 stage × RAW/raster × sRGB/ProPhoto output、strength 0/partial/100、warmth/fade、透明像素、windowed vs full-frame crop，以及負 channel 經 display stage 後續 basic／curves／tone 的數值。
- [ ] 執行 `dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~ColorTransformPipelineTests|FullyQualifiedName~EffectPipelineTests|FullyQualifiedName~FilmLookTests|FullyQualifiedName~FilmLutTests|FullyQualifiedName~CubeLutTests|FullyQualifiedName~PersistenceTests|FullyQualifiedName~Settings|FullyQualifiedName~PhotoAnalysis|FullyQualifiedName~WindowRenderEquivalence'`；確認各命名 suite 實際被選取。
- [ ] throwaway renderer driver 以實際 `SourceImage`（含 synthetic scene-linear RAW-style source）與 resolved settings 跑 CPU／GPU full/windowed/export；另用臨時 project 與真實 CLI export 讀回 16-bit TIFF／ICC。記錄這是 synthetic source 還是真實解碼 RAW，不混寫。
- [ ] review、更新 engine／film-looks 契約、commit。

**完成判準：**AC-CT-04／05，AC-CT-06 的正式 GPU 整合與 AC-CT-08 的 settings／renderer／sampling 部分。沒有第二個 scheduler／graph；原本未因 film 處理而存在的後段分析行為不新增。

### T6 — Desktop／mobile 匯入與可見錯誤

**Dependencies:** T3／T5。**Files:** §2 的 Engine import、Shared VM/dialog service／XAML／resx、Desktop dialog、Mobile dialog/workspace；既有 `tests/FilmBatch.App.Tests/{FilmLookViewModelTests,LocalizationTests}.cs`，新 `MobileWorkspaceTests.cs`／`MobileDialogServiceTests.cs`。App.Tests 加 `FilmBatch.Mobile` project reference；測 internal dialog 時在 Mobile project 開 `InternalsVisibleTo` 給該測試 assembly。**Human review:** 是，input/output 宣告是否會誤導。**Risk:** picker 畫面能選，但儲存／重啟／行動端取消錯誤。

- [ ] 先在既有 VM 測試中使用真實非 identity CLF／FBLOOK；驗最終保存 bytes/key、選擇、undo 與 renderer 像素，不做純 mock echo。取消／非法檔不得改原設定或建立 undo。
- [ ] 同一匯入按鈕增加 `.clf/.fblook`；只有 `.clf` 顯示新的三欄宣告 modal。舊 Cube 空間選項對 `.clf` 不生效並有清楚說明；`.fblook` 自帶 metadata。所有新增字串放兩份 resx。
- [ ] 實作 `ShowColorTransformImportAsync`：desktop 按既有 modal 模式；mobile 用現有 overlay/generation/cancellation 模式。`ReloadImported` 合併 `UserTransformLibrary.List(root)`；`ImportDetail` 對 `look-` 用該項 header 顯示 stage／input／output，無效 header 顯示錯誤而非讓其他項目失敗。
- [ ] `.clf/.fblook` 的 `Stage`／解析／hash／`Publish` 放在 UI 執行緒外並傳取消 token；先封存並解析 descriptor，modal 確認後發布同一份暫存。`ImportLegacy` 保留現有 `.cube/.fblut/Hald` 行為。`StagedFilmImport` 在取消、錯誤及正常完成時均釋放 owned 暫存。
- [ ] 在 shared VM 為本次匯入捕捉 library root、project service、完整 selection 與 import generation；project／selection 改變、`Suspend`／`Dispose` 時取消 token 並使 generation 失效。每個 await 後回 UI 執行緒重新檢查 context；只有發布成功且仍有效，才 `ReloadImported`、選取、`Service.SetValues`。發布後取消允許合法資產保留，但不得修改新 context 或刪除共享檔案。
- [ ] Mobile `PickFileAsync` 在 `ImportAssetAsync` 返回後再次核對 `_generation`／token，失效則不交付舊結果；`ImportAssetAsync` 只對 `.clf/.fblook` 啟用 CT-2 上限。`CopyAsync` 加可選 `long? maxBytes`，對不可 Seek 的 stream 按已複製 bytes 截止並刪本次 `.import-*`；其他 copy 行為不變，parser 仍獨立驗長度。
- [ ] 執行 `dotnet test tests/FilmBatch.App.Tests -c Release --filter 'FullyQualifiedName~FilmLookViewModelTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~MobileWorkspaceTests|FullyQualifiedName~MobileDialogServiceTests'`；測非 Seek over-limit stream、picker/modal／發布前後取消、來源變動、selection／project 切換、suspend／dispose、重新啟動及有效發布後不誤刪；確認所有命名 suite 實際被選取。
- [ ] 真實桌面 App 在隔離 profile 開啟，從 picker 匯入、確認 metadata、看主圖／tile／Loupe、undo、重啟；`dotnet run --project src/FilmBatch.App -- --gallery` 檢查控制項 appearance，但 gallery 不代替真實匯入。mobile host 與 iOS simulator/device 各跑可用的實際匯入／取消／重啟路徑；截圖無私人照片／路徑。
- [ ] review、更新操作與 shared/mobile 文件、commit。

**完成判準：**AC-CT-07／08。若某平台無 runtime，本項平台驗收保持 pending，不因 headless VM test 通過宣稱 UI 已完成。

### T7 — 發佈級整合、效能與契約收斂

**Dependencies:** T1–T6。**Files:** 只補真實 consumer-visible gaps、更新 §2 文件／證據；不新增第二套 benchmark／驗證框架。**Human review:** 是，跨平台數值與回歸證據。**Risk:** 只驗合成小圖，隱藏大圖、output codec、資產生命週期成本。

- [ ] 每個 AC 都對應已執行 case 與 artifact；測試名稱、版本、原檔 digest、輸入／settings digest、實際 backend、passed/failed/skipped 分開記錄。
- [ ] 使用固定 synthetic ramp、非對稱色塊、部分 alpha，加上具使用權且只本地保存的 RAW／JPEG／TIFF，各跑兩 stage、兩 output encoding、CPU／GPU、不同 RenderView／band。對讀回 RGBA16 比較數值與 ICC，不只看 screenshot。
- [ ] 先量基線，再量同機同輸入的新路徑：1920×1080 preview、6000×4000 export；disabled、單 4097×3 shaper＋兩矩陣、三節點含 33³、壓力 64-node／128³ 分開；warm-up 5 次、記錄 30 次 median/p95，涵蓋 asset cold/warm、allocation／peak RSS／GPU bytes。這些是待量條件，不是已測性能。
- [ ] 已停用／legacy 路徑的 median 不超過基線 5%；超過時查新增解析／配置／dispatch，不能接受為「新功能正常成本」。新 chain 同時記與 CPU、現有相同解析度 LUT 的絕對耗時；不預設一個未由使用情境支持的全平台 FPS，若互動明顯退化，先優化／重新評估 adoption，不宣稱更快。
- [ ] 所有下列 gates 完成後，才把 `docs/film-looks.md`／architecture／CLI／VERIFY 從提案改成產品支援；`docs/history.md` 記實際 dates、平台與限制。研究文件仍保留當時的範圍，不能回填成當時已實作。
- [ ] 專家 review：是否新增多餘抽象／cache／state；是否滿足 VERIFY；是否可省掉每 frame 重複 parse/upload／wrapper 計算。未過 gate 不用「foundation」或「MVP」宣稱整體完成。
- [ ] 最後移除臨時 driver／私有輸入／scaffolds，只保留允許的 tests 與本機 redacted evidence，按完成項目 commit；未獲要求不 push。

## 4. Verification Commands / Release Gates

以下是**實作後待執行**；此研究回合未跑產品測試套件，也沒有因文件變更重建 App。桌面與 iOS restore/build 必須依 release 文件序列執行或隔離工作目錄，不能交錯污染共用 `obj`。

```bash
work=$(mktemp -d "${TMPDIR:-/tmp}/filmbatch-ct2.XXXXXX")
dotnet build FilmBatch.sln -c Release
dotnet run build/CheckShaders.cs
dotnet test FilmBatch.sln -c Release --logger trx --results-directory "$work/test-results"
dotnet run --project src/FilmBatch.App -- --gallery
```

Windows suite 依 [testing](../../testing.md#windows-ci-測試排程) 使用既有單 MSBuild node／關閉 xUnit collection parallel 的規則，不把 macOS 的成功當 DX12 證據。Native app smoke／CLI export 遵循 [VERIFY](../../../VERIFY.md) 和 [release](../../release.md)；iOS 必須依 [真正行動編輯器的建置與操作](../../release.md#ios-行動版) 驗證。[Apple mobile 工具](../../../tools/apple-mobile/README.md) 是診斷工具，不能代替 iOS 編輯器。使用隔離 profile／照片副本，不改使用者現有 project。

| Gate | 必須觀察到 | 沒有證據時 |
|---|---|---|
| CPU／parser／storage | 每種接受節點、scale、失敗、舊 bytes/key 與原檔 round-trip | 尚未完成 |
| Metal | 實際 adapter／dispatch、數值、alpha、反覆切換與 fallback／cancel | GPU skip 不算過 |
| Windows DX12 | 同一 fixture 的實際 hardware 或明確標示 WARP 結果；仍需交代硬體覆蓋 | 不得以 Metal 代替 |
| Desktop UI | 真 picker→metadata→render→Loupe→undo→restart→export | gallery/headless 不是代用品 |
| iOS | 既有 CPU backend、import stream cap、overlay cancel、重啟與成品像素；不要求憑空新增 iOS GPU | desktop mobile host 不能代替 iOS |
| 效能／大圖 | 同 host/input 記錄基線／新路徑及 cold/warm；記憶體／band budget 包含 assets | 不宣稱更快／更省記憶體 |
| 文件／相容 | 所有 AC 有結果，current contracts 與實作一致，無 obsolete callers／aliases | 不能用 commit title 結案 |

16-bit consumer 比較沿用既有 CPU/GPU 容差（film parity 最大 64/65535、alpha 至多 1 LSB；純 chain alpha 必須 exact）；若基線的特定路徑有更嚴規則，不降低它。獨立 oracle fixtures 使用 T2 的 float 容差；不拿「兩個 renderer 一致」代替與 reference 的一致。

## 5. Review Focus / 未決驗證

- 有限但極端／病態的 f32 參數可能 overflow 或放大誤差；用固定 fixture 記錄數值域，不能以「保存原檔」宣稱運算 lossless。擴大數值範圍需新 oracle／容差評估。
- XML profile 故意小於完整 CLF；真實外部 corpus 覆蓋率尚未知。接收任何新節點／attribute／namespace 前先固定語義及拒絕測試，不使接受集合靠 parser 寬鬆度漂移。
- Display／WorkingLinear 對 RAW、raster、ProPhoto output 的位置與 transfer 是最高風險；T5 的非交換測試、實際 codec readback 與 T7 的合法 RAW 樣本負責，而非靠 enum 命名。
- cache 只留一個強引用 entry 不等於 process 總記憶體上限；在途 renderer snapshots、UI 工作數與檔案 copy 要納入實測。禁止以 unbounded dictionary 解決 thumbnail thrash。
- OCIO 的平台部署及 WGSL 缺口沒有在本計畫解決；它只當 oracle。若日後改產品依賴，另做 native/license/mobile 發佈決策，不偷偷加在此切片。

**Planning completion:** CT-2 的方案與 gates 已列明；DCP-1 的方案與獨立 gates 見下節。**Product completion:** CLF 須實際完成 T1–T7，DCP 須實際完成 D1–D5 與各自 release gates；沒有預先打勾或用一方的證據代替另一方。

## 6. DCP-1 — RAW 相機描述檔擴充

### 6.1 使用與範圍

**目標（尚未實作）：**在 RAW 原廠處理區匯入 `.dcp`，選取後影響相容 RAW 的相機顯影；使用原相機 profile 的照片、既有 film LUT／CLF 流程及沒有選 DCP 的輸出不改變。DCP 是 camera input，不是 `film.look`；它可與 film look 同時使用。DCP-1 規格是研究文件 §7；不承諾完整 DCP、Adobe／DxO 等價、Sony XMP RGBTable、HDR 或第三光源。

1. 匯入 standalone `.dcp`：背景封存、驗證、顯示名稱／機型／policy，成功才存原始 bytes；選定相容 RAW 後記一次 undo。選擇 `auto` 可回到現行 RAW 起始顯影（可能是內嵌資料、JPEG 擬合或光譜 fallback），重啟／preset／複製貼上後保留選擇。
2. 選定外部 profile 時，白平衡變更重新套其矩陣與光源相關 HSV 表；主圖、開發後縮圖、Loupe、取樣、完整匯出共用相同選擇。後續 film 效果仍可獨立啟用。
3. 壞檔、未知 tag、機型不符或 policy 0／1 配非 DNG：顯示原因，不能默默退回 `auto` 或把原圖當成功輸出。JPEG 等非 RAW 不套 profile；停用 RAW 模組保留設定但不解析它。

**來源與限制：**手工／合成 fixture 只使用自創矩陣、表格與機型。Sony／Adobe profile 或照片不得加入 repo、成品或 evidence archive；若使用者本地提供合法樣本，只記匿名 digest、權利來源、實際版本與數值。先依 [DNG 1.7.1.0](https://helpx.adobe.com/content/dam/help/en/photoshop/pdf/DNG_Spec_1_7_1_0.pdf)／[可讀副本](https://github.com/user-attachments/files/22010027/DNG_Spec_1_7_1_0.pdf) 核對讀取；DCP-1 資源上限、接受集合、tone 計算、來源身分及 policy 均以研究文件 §7.2–7.3 為準。

### 6.2 Acceptance IDs

| ID | 可觀察結果 | 負責項目／檢查 |
|---|---|---|
| AC-DCP-01 | 兩種 endian 正確解析完整受限 profile；截斷、重複、未知／HDR／三光源／超限均明確拒絕 | D1：`DcpProfileTests`，合成 binary 和獨立手算 |
| AC-DCP-02 | 原始 bytes／policy／copyright 不變；相同 bytes 同 key；不同 root、重啟、取消、缺檔與 hash mismatch 行為正確 | D2／D5：`CameraProfileStoreTests`、UI／CLI smoke |
| AC-DCP-03 | 相機 matching、signature、baseline offset、嵌入／外部 profile 替換順序及 policy 對每張 RAW 正確 | D3：`DcpSourceTests`、`CameraColorTests`，獨立數值與相容／不相容 fixture |
| AC-DCP-04 | 非線性 HueSat／Look 與曝光順序及兩種白平衡數學正確；CPU 和實際 GPU 各對獨立 expected 比較 | D3／D4：`DcpSourceTests`、`GpuColourTests`；GPU skip 不算通過 |
| AC-DCP-05 | DCP tone 保留原節點並按指定三次曲線求值；identity 不觸發 JPEG fit；CPU／GPU／輸出與 alpha 正確 | D4：`DcpToneTests`、`RawGpuDisplayTests` 和獨立數值 fixture |
| AC-DCP-06 | A→B→undo A、白平衡切換、嵌入 JPEG 縮圖、併發共享解碼及遲到結果不混 profile；主圖／Loupe／取樣／preset／CLI／輸出一致 | D3／D5：`RawDecodeSharingTests`、`RawSourceLifecycleTests`、`RawControlsTests`＋真實 UI／CLI scenario |
| AC-DCP-07 | Metal、Windows DX12、iOS、記憶體／效能及文件 gate 皆有結果；未測平台不稱完成 | D5：§4 與下列 DCP release gates，記錄 backend、inputs、skips |

數值門檻是待驗收的上限，不是本回合量測：合成 fixture 的 camera matrix／單一 pixel 中間值，CPU 對獨立 double expected 使用 `abs ≤ 2e-5 + 2e-5*abs(expected)`；實際 GPU 使用 `abs ≤ 1e-4 + 5e-4*abs(expected)`，每個取樣值均須通過。DCP tone 的 RGBA16 CPU／GPU 最大差沿用 `RawGpuDisplayTests` 的 2 LSB，alpha 沿用各既有 consumer 的較嚴規則。不同算圖階段分開比較，不能把完整 RAW 顯影後的差異都歸因於 profile。若固定 fixture 未通過，先修正實作／oracle 或另行審查規格，不能事後直接放寬門檻。

### 6.3 檔案與介面

| 層 | 實作階段的責任 |
|---|---|
| Raw | 新 `src/FilmBatch.Raw/Color/DcpProfile.cs`：嚴格 standalone parser 與 immutable profile；`Tiff/Reader.cs`／`Dng.cs` 僅抽取共用 IFD 讀取及來源 metadata，不改影像 TIFF 的既有容錯。`RawImage.cs`、`Meta/Metadata*.cs` 補分離的 baseline、signature 與唯一機型 |
| Core | `Params/ModuleCatalog.cs` 定義 `raw.camera_profile`；新 `Params/CameraProfileKey.cs` 的 `IsUserKey` 驗 `dcp-`＋64 個小寫 hex；`ParamDef.cs` 以僅此選項使用的內部標記及 `SettingsJsonConverter.cs` 只接受 `auto` 或合法 key，與 `film.look` 分開；`AppPaths.cs` 新增 `UserCameraProfiles` |
| Engine | 新 `src/FilmBatch.Engine/Imaging/Raw/CameraProfileStore.cs` 保存與解析使用者 DCP snapshot，與既有提供 bundled JSON 擬合參考的 `CameraProfiles.cs` 不同；修改 `RawLoadOptions.cs`、`RawSourceLoader.cs`／`.Decode.cs`、`RawWhiteBalance.cs`、`CameraPreview.cs`、`ImageLoader.cs`、`PhotoAnalysis.cs`、`RawDisplay.cs`／`RawDisplayGpu.cs` 及 GPU tone shader；`CameraLocalTone.cs` 消費的 source gain map 必須保留；必要時更新 RAW colour shader 而不改舊 profile 的結果 |
| App | 修改 `FilmBatch.App.Shared/ViewModels` 的 RAW 控制／selection、`ModuleSectionViewModel.cs`、`PhotoToolsViewModel.cs`、`Services/ImageCache.cs`、`FilmstripViewModel.cs`、`IDialogService.cs`、桌面／行動 picker、`MobileWorkspace.cs` 與兩份語系資源；複用既有匯入／取消與 ProjectService undo，不新增另一個 film module |
| Tests／文件 | 新 `tests/FilmBatch.Raw.Tests/Color/DcpProfileTests.cs`、`tests/FilmBatch.Core.Tests/{CameraProfileStoreTests,DcpSourceTests,DcpToneTests}.cs`；擴充 `PhotoAnalysisTests.cs` 與既有 GPU／RAW lifecycle／settings／UI 測試；更新 architecture、building、CLI、testing、VERIFY 與 history 的**實際**契約與證據 |

跨層介面：

```csharp
DcpProfile.Read(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default) : DcpProfile
CameraProfileStore.Import(string root, Stream source, string displayName, CancellationToken cancellationToken) : string
CameraProfileStore.Resolve(string root, string key, CancellationToken cancellationToken) : DcpProfile
CameraProfileStore.List(string root) : IReadOnlyList<CameraProfileItem>
```

`DcpProfile` 封裝受驗證的 model／兩組 matrices／tables／tone／policy／copyright，不可讓呼叫端修改共用 arrays；`CameraProfileItem` 只供顯示 key、bounded name 與有效／失敗狀態。`root` 在工作開始時捕捉；store 在 Engine，避免 Core 反向引用 Raw。匯入／解析超限 64 MiB 前停止，原始檔為 `AppPaths.UserCameraProfiles/dcp-<64hex>.dcp`，名稱使用同 stem `.name`；source stream 由 caller 管理。UI、CLI 及 renderer 不各自重新實作 parser。若 CT-2 同時實作，`ParamDef`、`SettingsJsonConverter`、UI 與 GPU 共同檔必須由一個整合者依序合併；DCP 不依賴先完成 T1–T7。

### D1 — Standalone DCP parser 與來源資料分離

**Dependencies:** DCP-1 §7.2–7.3。**Files:** 上表 Raw 檔案與 `DcpProfileTests.cs`。**Human review:** 是，接受／拒絕集合與作者權利。**Risk:** TIFF reader 目前會跳過不完整 entry、裁切 entry count、去重，不能視為嚴格 DCP。

- [ ] 先產生兩端序的合成 profile，使用已知 3×3 SRATIONAL 矩陣、21／17 illuminant、非對稱 HSV 表、非均勻 tone 節點及 model `Acme Model A`；逐一 assert 原始型別、值、表次序與 model／policy／copyright。故意提供一項順序改變即改結果的表格。
- [ ] 先跑 `dotnet test tests/FilmBatch.Raw.Tests -c Release --filter 'FullyQualifiedName~DcpProfileTests'`，記錄 parser 尚不存在的 RED；新增 profile 專用 header／IFD 嚴格 preflight，再重用現有低層 TIFF 值解析，不以改寫原檔 magic 實作產品 parser。
- [ ] 檢查 header、IFD 計數／結尾、tag 唯一性、offset／count／type、零 denominator、有限數、矩陣／光源配對、HSV 零飽和列與維度、curve 端點／順序、SDR dynamic-range 及 §7.2 限額；超限在配置前拒絕。覆蓋有符號曝光 offset（上游生成器實用 SRATIONAL）並拒絕未知的實際型別。
- [ ] 測長度剛好上限及超一、截斷 header／IFD／value、重疊或超界 offset、duplicate tag、unknown tag／encoding、HDR、第三光源、自訂 illuminant、gain map、RGBTables、不完整表／非有限值；另測合法 policy 0–3／black 0–1／SDR Version=1 與非法 policy=4、black=2、dynamic-range Version=2 或 DynamicRange=2、SDR hint>1／非有限值。回傳可辨識錯誤而非 partial profile。
- [ ] 將 DNG 來源 baseline 與 profile offset、兩個 calibration signature、獨立 UniqueCameraModel 分開保存；對未選外部 profile 的既有解析／輸出保持不變。跑 `dotnet test tests/FilmBatch.Raw.Tests -c Release --filter 'FullyQualifiedName~DcpProfileTests|FullyQualifiedName~CameraColorTests|FullyQualifiedName~ProfileTests|FullyQualifiedName~DngWriterTests'`，確認各 suite 非零；以獨立小 driver 讀合成 DCP 並列欄位和拒絕原因；review、文件、commit。

**完成判準：**AC-DCP-01；沒有將 embedded DNG 的容錯悄悄改為 standalone 嚴格政策。

### D2 — 原始資產、選擇與序列化

**Dependencies:** D1。**Files:** Core `AppPaths.cs`／`ModuleCatalog.cs`／`ParamDef.cs`／`SettingsJsonConverter.cs`，新 `CameraProfileKey.cs`；Engine `CameraProfileStore.cs`；新 `CameraProfileStoreTests.cs` 與既有 settings／preset 測試。**Human review:** 是，檔案儲存與 policy。**Risk:** key、已驗 bytes 與真正用的 profile 不一致。

- [ ] 先測 `auto` 預設、有效／無效 `dcp-<64hex>` 只在 `raw.camera_profile` 接受；在 Core 實作不引用 Engine 的 `CameraProfileKey.IsUserKey`，於 `ModuleCatalog` 只為此 choice 設內部標記，`SettingsJsonConverter` 使用完整 key。JSON、preset、undo／paste 保留 key；其他 choice 不被放寬，舊 settings 無此欄位時仍有相同輸出。
- [ ] `Import` 使用受限、唯本次擁有的 temporary；對同一 bytes 呼叫 D1 parser、計算完整 SHA-256，再以同目錄原子發布；`.name` 限長且不影響 key。已存在 target 先驗 hash 與內容，不覆寫未驗證資料；取消／失敗只刪本次 temporary，不刪共用資產。
- [ ] `Resolve` 在新工作邊界驗檔案與 key，回不可變 snapshot；飛行中的工作使用同一份 snapshot，不能在發布後重新依路徑讀另一份。`List` 不執行整庫 profile 數學；損毀項不毒化其他項。限制快取與在途 snapshot 的生命週期，不新增無界全域字典。
- [ ] 測重複匯入、process 重啟後 bytes／key、root 切換、同名不同內容、同尺寸與時間戳但內容遭替換、缺檔／hash mismatch、發布前後取消；保留 policy／copyright 且不得重新序列化來源。
- [ ] 跑 `dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~CameraProfileStoreTests|FullyQualifiedName~Settings|FullyQualifiedName~PersistenceTests'`；另外以兩個獨立程序執行 import→exit→resolve 並比對 bytes／key；review、文件、commit。

**完成判準：**AC-DCP-02 的儲存／設定部分；UI 與真實工作 snapshot 由 D5／D3 驗收。

### D3 — RAW 顯影、模型匹配、白平衡與快取

**Dependencies:** D1／D2。**Files:** `RawLoadOptions.cs`、`RawSourceLoader.cs`／`.Decode.cs`、`RawWhiteBalance.cs`、`CameraPreview.cs`、`ImageLoader.cs`、`PhotoAnalysis.cs`、`PhotoToolsViewModel.cs`、`ImageCache.cs`、`FilmstripViewModel.cs`；新 `DcpSourceTests.cs`，擴充 `PhotoAnalysisTests.cs`、`RawDecodeSharingTests.cs`、`RawSourceLifecycleTests.cs`、`GpuColourTests.cs`。**Human review:** 是，顯影順序／機型／policy。**Risk:** 原 profile／JPEG fit 疊兩次，或非線性表仍用 as-shot。

- [ ] 先用相同合成 sensor、明顯不同的 profile A／B、非 identity 雙光源 HueSat／Look 與 -1 EV offset 建立 RED。固定獨立預期：offset 是影像 baseline 加新 offset 一次；signature 相同用非 identity CC、不同用 identity；unsupported/missing/mismatch 不得回原圖。依 DNG 規格及手算矩陣交叉檢查，再用不依賴產品的 DCP 參考實作檢查可共用的中間值；若 oracle 不可取得，該 gate 保持 pending。
- [ ] 在 `StartingColour`／白平衡、highlight 與 `ProfileTables.Create` 前解析一次 snapshot、核對來源身分與 policy；修改每次 `DecodeLease.CreateRaw` 的獨立 `raw.Color`，另建 `ProfileLook` 並附回來源 gain map，不修改淺層共用的原 profile。所有後續 `ProfileTables.Create(raw.Color.Profile,...)`、DCP tone 與 `CameraLocalTone.Apply(image,raw,...)` 使用此有效資料；外部 profile 不走原 JPEG／DNG tone fit。用同一 DNG 的 IFD0 v2 source map＋不同矩陣／非 identity HSV 的 DCP，證明開啟局部 tone 時兩者均生效。
- [ ] `RawLoadOptions` 以 profile key 和選中 profile 時的 WB mode／temperature／tint 辨識來源；`auto` 一律正規化為無外部 key、無 DCP-WB 值的既有 options 表示。WB 改變重算 sensor→camera transform、光源插值及非線性表，在後續 `ModuleParams` 禁止第二次 WB。無 DCP 時原 WB 快速路徑與 key 不變。`DecodeLease` 可共享 sensor bytes，但外部 profile 不使用依 opcode 快取的舊 `ColourFit`。
- [ ] 在 `ImageLoader.Load` 的 ≤240px 內嵌 preview 快路與 filmstrip `NeedsDeveloped` 都處理選定 DCP：必須使用重顯影來源，不能只把新 `RawOptions` 貼到舊 JPEG。主圖／Loupe／縮圖／PhotoAnalysis／denoise／batch full load 皆採相同 snapshot；過期工作不得覆蓋新的選擇。
- [ ] 保留 as-shot 色溫／色偏與白平衡建議。為選定 DCP 的 `PhotoAnalysis.SolveWhiteBalance`／`AutomaticWhiteBalance` 建立按需的可重用 RAW 感光／幾何快照，按候選白點重算 highlight、相機矩陣與非線性表，然後沿用現有 sample adjustment／統計；最多 `MaxSamples` 個 sample，不以舊 `MatrixFor` 冒充。求解在背景、以 photo／selection 世代與取消防止舊建議送達；釋放求解資源。用非交換 dual-table fixture 比對候選預測與該 WB 實際重顯影 sample，並測 manual／automatic 建議與 cancel／切圖。
- [ ] 實際測同一 RAW 的 A／B 併發、A→B→undo A、as-shot→custom→as-shot、DNG source identity、非 DNG Make／Model、缺機型、policy 0／1 vs DNG／非 DNG、policy 2／3、嵌入 JPEG；測來源 gain map 四個既有候選的優先序（IFD0 v2→raw IFD v2→raw IFD v1→IFD0 v1）、均無 map 與開關，拒絕外部 DCP map；測取消與 source replacement。無 DCP 的既有相機外觀／WB／RAW export regression 不降低容差。
- [ ] 外部 DCP 的 SDR HSV 明度倍率結果依 DNG 1.7.1 §Applying HSV step 8 clip 至 ≤1；CPU／GPU 測明度 >1 與倍率使明度超界的合成手算 fixture，包含兩種 encoding，既有 embedded headroom 行為不得改變。RAWmakase 只作共同有界域的獨立比較，不以它的超白 headroom 覆蓋規格。
- [ ] 擴充 `GpuColourTests`：對**非 null `ProfileTables`** 同時測 HueSat＋Look、兩種 encoding、曝光順序、超白與 band／CPU fallback；CPU 與每個可用 GPU 均對封存的獨立 expected 比較，不能只互比。跑 `dotnet test tests/FilmBatch.Raw.Tests -c Release --filter 'FullyQualifiedName~GpuColourTests|FullyQualifiedName~CameraColorTests|FullyQualifiedName~ProfileTests'` 及 `dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~DcpSourceTests|FullyQualifiedName~RawDecodeSharingTests|FullyQualifiedName~RawWhiteBalanceTests|FullyQualifiedName~PhotoAnalysisTests'`；記錄 adapter、GPU executed／skipped；review、文件、commit。

**完成判準：**AC-DCP-03／04 及 AC-DCP-06 來源／快取部分；不宣稱 Adobe 原廠外觀一致。

### D4 — 原始 tone curve 的 CPU／GPU 顯示收尾

**Dependencies:** D3。**Files:** `RawDisplay.cs`、`RawDisplayGpu.cs`、`SourceImage.cs`、`src/FilmBatch.Gpu/Shaders/v1/io/raw_working_finish.wgsl`；新 `DcpToneTests.cs`，擴充 `RawGpuDisplayTests.cs`。**Human review:** 是，曲線與 SDR 域。**Risk:** 32 點重採樣、不同 spline、重複 tone 或白平衡。

- [ ] 先寫 4 點非均勻 spline、identity 與缺省 tone 的獨立數值 fixture：直接測節點／中點、0、1、負亮度及 >1、RGB ratio 與 alpha；至少一段設計成 32 點重採樣會失敗。使用 `ToneCurve.Eval` 既有 cubic 規則作明確契約，expected 由獨立公式預先計算，不呼叫產品 evaluator。
- [ ] 只對外部 DCP 的 tone 分支保留原始節點與預計算係數，在 RAW 顯示收尾依 §7.3 的 Rec.2020 亮度執行；缺省 tone 使用舊收尾，identity 是明確曲線且不觸發 DNG JPEG fit。不得把節點再烘成 32 點或 4096 點；tone 資源在 profile 或 device 變更時正確釋放。
- [ ] WGSL 與 CPU 使用相同 spline、邊界與亮度約定；檢查 GPU storage/binding 與現有 budget，超限走既有 CPU fallback，不能截斷曲線。16-bit 成品沿用既有 CPU/GPU parity 上限且對獨立 expected 不得放寬；alpha 與原契約一致。
- [ ] 跑 `dotnet run build/CheckShaders.cs`，再跑 `dotnet test tests/FilmBatch.Core.Tests -c Release --filter 'FullyQualifiedName~DcpToneTests|FullyQualifiedName~RawGpuDisplayTests|FullyQualifiedName~RawDngConversionTests'`；實際 Metal 與 Windows DX12 執行非 identity fixture，記 adapter、誤差及 fallback；review、文件、commit。

**完成判準：**AC-DCP-05；未測的 GPU 平台保持 pending。

### D5 — UI、實際消費者與跨平台 release

**Dependencies:** D2–D4。**Files:** §6.3 App／文件、`RawControlsTests.cs`、`RawSourceLifecycleTests.cs`、`LocalizationTests.cs`；新 `MobileWorkspaceTests.cs`／`MobileDialogServiceTests.cs`，`tests/FilmBatch.App.Tests/FilmBatch.App.Tests.csproj` 引用 `FilmBatch.Mobile`，測 internal dialog 時在 `src/FilmBatch.Mobile/FilmBatch.Mobile.csproj` 加 `InternalsVisibleTo`。若 CT-2 T6 已建立這些，只增補行為測試。**Human review:** 是，機型／授權錯誤及使用者操作。**Risk:** 測試通過但 picker、profile selection 或正式輸出繞過新路徑。

- [ ] 把匯入與選擇放在既有 RAW 控制區，顯示 `auto`（RAW 預設）、使用者 profile、model 與 policy；共用 ProjectService 的一次 undo。`ModuleSectionViewModel` 排除 generic `raw.camera_profile` 靜態 choice，使用可更新使用者 library 的專用選擇列，避免兩個不一致控制。桌面及 mobile picker／背景複製／取消／selection generation 按既有模式，mobile 對 `.dcp` 複製時即執行 64 MiB 上限（非 Seek stream 亦然）；中文／英文資源同步。
- [ ] 測 picker／驗證／發布前後取消、切換 selection／project、suspend／dispose、重啟、不同機型批次、缺檔與被換檔、RAW 關閉、貼到 raster；無正式資產或選擇不得誤設，發布後合法共用檔不得因取消被刪。執行 `dotnet test tests/FilmBatch.App.Tests -c Release --filter 'FullyQualifiedName~RawControlsTests|FullyQualifiedName~RawSourceLifecycleTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~MobileWorkspaceTests|FullyQualifiedName~MobileDialogServiceTests'`；確認各 suite 實際被執行。
- [ ] 用獨立 profile／合成 RAW，在隔離使用者目錄走真實 desktop picker→選取 A→變 WB→B→undo A→重啟→Loupe／縮圖→PNG16／TIFF16 和 rendered DNG 匯出；讀回像素與 ICC，與同 settings 的 CPU expected 比較。CLI 讀相同 project／preset 匯出；區分 rendered DNG 與不讀選擇的 `raw convert`，不可宣稱後者嵌入了 DCP。
- [ ] 執行 §4 solution／shader gates；在真實 Metal、Windows DX12（明記 hardware／WARP）、iOS 編輯器分別驗證，任何 GPU skip、僅 desktop mobile host 或 gallery 均不是對應平台 PASS。量同機同輸入的無 DCP 基線與 DCP as-shot／WB 切換及白平衡建議，記錄 preview／export／求解 median、p95、峰值記憶體與 buffer bytes；不得宣稱已測得效能。
- [ ] 測合法非 identity DCP 及可使用的真實 RAW；記錄 profile 機型、policy、來源權利、digest、平台／版本、passed／failed／skipped、獨立 oracle 和誤差。Sony 上游 policy 0 不用於 ARW；若沒有合法匹配樣本或獨立 oracle，標記該 gate pending，不用其他樣本取代。
- [ ] 由維護者／法律審查 [Adobe DNG Specification patent license](https://helpx.adobe.com/camera-raw/desktop/dng-and-file-formats/digital-negative.html#dng) 對既有 DNG 讀寫及 standalone DCP 的適用範圍；依該授權散布的 Compliant Implementation，須在原始碼及隨發行提供的文件顯著顯示 `This product includes DNG technology under license by Adobe.`，並核對 `THIRD-PARTY-NOTICES.txt` 與發行內容。此授權排除其他檔案格式的實作，不取代 profile 來源授權；適用性或聲明未確認前 release gate 保持 pending，不宣稱法律符合。
- [ ] 對照 AC-DCP-01–07 與既有 VERIFY，專家檢查重複 parser／cache、舊路徑相容、CPU/GPU 預算；按實際結果更新現行操作／架構／testing／CLI／VERIFY 及 history，清除暫存、不保存第三方檔案或私人照片；完成項目獨立 commit，未獲要求不 push。

**完成判準：**AC-DCP-02／06／07 及所有 DCP-1 gates，包含可用平台的實際操作與獨立數值證據；研究與檔案匯入成功均不等於完成。
