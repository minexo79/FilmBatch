# Performance Local Experiments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans for the experiment steps. Steps use checkbox (`- [ ]`) syntax for tracking. This is an isolated research plan, not authorization to implement product candidates.

**Goal:** 先完整保存上游 Survey，再以 FilmBatch 現行來源執行可重播的本機成本／數值實驗，為 P1／P2 候選補證據。

**Architecture:** 儲存庫只保留研究、實驗規格、結果與導航。拋棄式 .NET probe 在儲存庫外引用現行 Engine／Core／Raw／Gpu，使用 friend assembly `FilmBatch.Core.Tests` 存取既有內部階段；不修改產品或建置第二套正式 runner。所有重型量測循序執行，避免互相競爭。

**Tech Stack:** .NET 10、FilmBatch managed RAW、Dawn／WebGPUSharp／Metal、既有 ONNX Runtime／RawNIND、System.Diagnostics／GC／SHA-256。

**Spec:** [完整 Survey](../../research/performance-raw-ai-survey.md)。本輪只執行下列三組實驗；其他上游 GUI／跨產品排名、原生 GPU interop、Bayer 模型替換與產品 cutover 不在本輪。

**Spec version:** 1.1，第一次執行前釐清 test-specific 精度單位與實際入口參數；未變更任何產品 oracle。

## Global Constraints

- 產品來源基準：`890b13f4e610eb1498eb294fc6f7da18b76a7533`；來源基準與 probe 組件各記 digest。版本／輸入改變即視為另一輪，不沿用舊數值。
- 先完成 Survey、此計畫及文件導航，才啟動實驗程式；結果不預填為通過。
- 使用同一 Sony ARW 的隔離副本：7008×4672，SHA-256 `2655aca052775ed68f737a07ad27e65653ce12853115c07624c187d23e320638`。不寫使用者專案、預設或設定；不下載新模型。
- RawNIND Linear：FP32 `[1,3,512,512]`，border 64／core 384；既有權重 SHA-256 `df957efadcc152c007d5d3b0917bdff9e41c0d4a0efe56584ef30b36393cd181`。僅複製權重到隔離 models；實際 provider 寫進收據。
- 不改產品 `.cs`／`.wgsl`／schema，不改 oracle／測試預期／精度門檻，不把 shader／讀回錯誤改成沒有 GPU，不以 fallback 冒充 GPU 實測。
- 不 commit／push／發佈。原始照片、模型、完整解析度點陣與執行檔不放進證據封存；保留 probe 原始碼、逐輪 JSONL、組件 digest、命令及 exit。
- 耗時包含實際同步與 CPU 讀回；未觀測到的 GPU 純 kernel 或 UI 延遲明寫未測，不以推導 byte 數冒充實測傳輸。

## 選項與影響

**採用：先文件化，再隔離量測。** 不改 `ImageCache`／`DecodeBudget`／AI cache／Loupe／Basic，避免未量測即變更記憶體政策、正式 AI 品質或舊照片外觀。另一做法是直接修改產品後跑前後比較，但此時尚未定位主要瓶頸，會混合多個因素，故不採用。

唯一長期變更為 `docs/research/performance-raw-ai-survey.md`、此計畫、`docs/evidence/performance-local-2026-10-26.md`、`docs/README.md` 與 `docs/history.md`。不新增產品相依、偏好或通用基準框架。實驗候選不成為產品契約。

## 方法與驗收矩陣

| ID | Given／When | 必須保存的觀察 | 判定／反證 |
|---|---|---|---|
| DOC-01 | 上游研究已完成 | 正式版／主線 SHA、CPU／GPU、RAW、記憶體、AI、亮暗部、授權、優先序、來源與未測邊界 | 不混用發布版／main；不把既有功能當缺口，不宣稱 f16 無損或跨產品勝出 |
| E01 | 同一 RAW，CPU／App Metal 政策交錯載入；同來源物件及等像素新物件算圖 | 每輪 wall／CPU／managed allocation／sampled RSS／GC 後存活；完整來源 hash、尺寸、RAW GPU errors、實際 renderer、來源重用輸出 hash | CPU／GPU full 各至少 1 次暖機＋6 次交錯樣本；不同 source identity 的相同算圖輸出必須逐樣本相同。場景差依既有容差另分布，不以 hash 不同判等價 |
| E02 | 200 px 線性 RAW 縮圖、正式 `Prepare`、強度改變、縮圖穿插及重建 processor | 模型 digest、provider、tile 數、CacheHit、輸出尺寸／reference、每次 wall／CPU／配置與記憶體快照 | 縮圖 1 tile；正式預覽仍是原圖 247 tiles；amount／luma／color 變更命中 full；縮圖不可逐出 full；全品質不可被小圖快取代替。重建 processor 成本另列，非持久化快取實作 |
| E03 | 黑／近零／白／HDR／飽和色／透明階梯及同一實拍；現行 Basic／ShadowRecovery 與隔離亮部控制候選 | 全分量有限值／黑點／alpha；等亮度 ROI 匹配；RGB 比例／紋理／邊界數值；實際 CPU／Metal Render16 差；每輪成本 | 分數 alpha fixture 沿用既有 RGB 32 LSB／alpha 1 LSB；512 px 實拍另設較嚴格 2 LSB 實驗目標，不宣稱為全管線通用契約，亦記 over2／over32。候選僅共同 RGB 有理 shoulder 非局部對照；未證明 halo／人工畫質就不採用 |
| EVID-01 | 所有程序結束 | 每個命令／exit、未測與失敗分類、輸入前後 SHA、逐輪收據及可重播 probe | 研究結果可從收據重算；不只留下暫存 URI，不把 Engine microbench 當原生 GUI 驗收 |

### 共用量測規則

1. 固定輸入、完整 settings、seed、解析度、參考尺寸、相機曲線與 RAW 選項；CPU／Metal 效果比較使用同一已解碼來源。
2. 首次裝置／JIT／session 成本獨立記錄；暖機不混入 warm 統計。CPU／GPU 交錯順序每輪反轉；其餘重複至少 6 次。AI 全尺寸至少兩個 processor 的首次樣本，不用少量長樣本宣稱穩定 p95。
3. 保存所有樣本，報中位數、範圍及樣本數。若報 nearest-rank p95，明示小樣本時等於最大值，不能當交互 p95。
4. `GC.GetTotalAllocatedBytes` 為累計 managed allocation；`GC.GetTotalMemory` 在明確 GC 邊界記存活量。用背景採樣器記程序 working-set/RSS 最大觀察值及採樣間隔；不是 OS 真正峰值／native 分類／GPU 實體量。GC 放在個別案例外，另記 GC wall。
5. 計時與數值驗證分開；不要把雜湊／全圖誤差掃描時間算進 renderer／decoder。進行中程序不得靠高頻 polling 觀察進度；讀一次已完成的收據。
6. 每個 GPU 操作後保存 `UsingCpu`／DeviceName 或 RAW GPU 正向控制／錯誤；失敗保留原因，停止該路徑，不放寬門檻。
7. 前後 SHA-256 核對原檔／副本／模型。所有輸出留隔離目錄；不開使用者照片專案。

## Task 1：先交付研究與預先規格

**Files:** Create 上述 Survey／此計畫／有狀態標記的 evidence；Modify `docs/README.md`。

- [x] 保存完整四產品研究，明確區分來源研究、前輪 smoke 與本輪待量測。
- [x] 在執行之前寫定 E01／E02／E03 的比較單位、接受界線與不採用條件。
- [x] 文件導航可找到 Survey、計畫及結果；結果初始狀態為尚未執行。執行前 73 個相對連結目標全部存在。

## Task 2：準備並循序執行隔離 probe

**Files:** 儲存庫外 `probe/LocalExperiments.csproj`、`Program.cs`、`AiExperiments.cs`、`RawExperiments.cs`、`ToneExperiments.cs`；不加進 solution。入口 `Probe.RunRaw()`、`Probe.RunAi()`、`Probe.RunTone()`，由 `raw|ai|tone` mode 選擇。

**Interfaces:** Probe 共用 `Root`／`Input`；`Emit(string kind, object data)` 寫單行 JSONL；`Measure<T>(string label, Func<T> action, int iteration = 0)` 回傳結果並記 wall／CPU／allocated／sampled RSS；`Hash(SourceImage)` 在量測外取整份像素 SHA；`Require(bool,string)` 不滿足即非零 exit；`Collect(string label)` 在案例外記明確 GC 快照。

- [x] 只從已存在輸入複製 ARW／RawNIND，核對 digest；記 SDK 10.0.402、機器及 774 個產品來源檔 digest；組件 digest 由每個實際 mode 記錄。
- [x] 完成所有 mode 後建置 Release；首輪 probe 有兩項 nullable flow 警告，補上真實 throw assertion 的 `DoesNotReturnIf(false)` 契約後，最終 0 警告／0 錯誤。未改產品。
- [x] 已循序執行 raw／ai／tone，各保存 stdout／stderr／exit；raw／ai exit 0，tone 因 1600 combined 一個分量為 3 LSB，未達預設 2 LSB 目標而 exit 2。保留失敗，不重釘門檻。
- [x] E01 記 CPU／GPU full、Preview、穩定物件／等像素新物件、延遲顯示編碼及明確釋放後快照；無分段事件時不捏造各 kernel／傳輸的純時間。
- [x] E02 已取得真實 RawNIND／CoreML＋CPU：1／247 tiles、12 次混合命中、縮圖隔離、顯示尺寸重用及兩個 processor 首次收據。
- [x] E03 已執行階段邊界、8 組尺寸／設定的 Render16／成本與全域 shoulder 控制；包含反例而非全綠結論。

## Task 3：回填與裁決

**Files:** Modify evidence、此計畫、`docs/history.md`；必要時只在 Survey 增加結果入口，不覆寫前輪單次值。

- [x] 從逐輪收據計算表格，分開配置／存活／RSS／GPU 推導量，列出失敗／工具修正與未測。
- [x] 針對 P1 accounting／重用、P2 AI persistence／亮部各寫證據、反證及本輪採用／不採用界線；不把候選轉為產品功能。
- [x] 封存原始收據、probe source、source／binary digest、重播步驟及命令；不包含照片、模型、執行檔或私人絕對路徑。
- [x] 核對所有輸入不變、文件相對連結與導航可解析。純文件變更不跑無關全套測試，不宣稱未執行的 GUI／Windows／iOS／完整封裝 gate 通過。

## 初輪結果後的限縮診斷（執行前追加）

保留 `probe-v1` 原始碼、digest 與 raw／ai／tone 原收據；新增診斷不改 spec 1.1 的精度判準，不重新跑已失敗的 tone 來「確認」。

- `reuse <work> <repo>`：初輪 `Render16` 數值本身有效，但它走 `RenderForExport()`，入口會 `ClearSources()`，不能回答預覽來源快取問題。補用實際 BGRA8 `Render()`，暖機後 6 對同物件／等場景新物件，交錯次序、逐輸出 byte hash 完全一致、實際 Metal 非 fallback。原匯出對照不刪除或改名成成功的預覽對照。
- `rawdiff <work> <repo>`：初輪 full scene 最大差 1.1265284、平均約 1.11e-7。追加全分量差異分布、最差座標與鄰近數值，再把兩個不同解碼結果各交同一 CPU Render16，量可見輸出差；與「同 CPU 來源的 CPU／GPU 效果最大差 1 LSB」分開。只診斷，不提高既有 AHD／色彩或效果容差，不修產品，也不把低比例離群當已證明安全。

這兩個 mode 循序執行；每次入口仍保存組件 digest、輸入前後 SHA、命令與 exit。後續結果回填 evidence；研究完成不等於精度候選通過或可發佈。

## 擴充覆蓋 E04–E07（spec 2，依使用者續行要求，執行前記錄）

目標保持完整：**實驗直到 RAW／AI／CPU／GPU 效能與亮暗部演算法都被實驗完成**。首輪既有結果／反例不失效；新增下列實際對照，不把文件核對或單純全域控制冒充剩餘實驗。

| ID | 實驗與輸入 | 必須觀測／判定 |
|---|---|---|
| E04 | 同一 Full RAW，CPU／GPU AHD-only／GPU colour-only／both 的四種政策組合；另存隔離場景與 Render16，逐分量比較 | 生產程式不改；只在拋棄式程序中明示選擇性停用既有 private GPU flags，先選定 Metal backend 避免重設。CPU／both 的完整 hash 必須等於已保存基準，才能用 mixed 組合歸因。記 AHD 實際非 null 回傳、完整分布／最差座標／可見差；不只取平均，不以範圍外 CPU 路徑冒充 GPU |
| E05 | 同一 200 px 線性來源的 RawNIND，及同一來源經既有 finisher 的 200 px sRGB／NAFNet；各模型 CPU／CoreML session 初次＋至少 6 次實際推論，以新來源 identity 避開 cache hit；既有縮圖／正式契約另驗 | 記模型 digest、provider、tile、CacheHit、CPU／wall、完整未混合 float 與顯示誤差；CPU 不視為絕對真值。兩個已安裝模型只複製到隔離目錄，不下載；sRGB 必須選 NAFNet，不代換。真實完整未混合 neural RGB 做 f32 寫入／flush／讀回至少 3 次，完整 bytes 相同；只量 I/O control，不假稱已實作持久產品快取 |
| E06 | CPU RAW、1／2／4 worker scope，各暖機後 6 個 Full 樣本；每輪旋轉順序 | scope 只能降低現有半核心預設；逐份完整場景 hash 相同。記 wall／CPU／allocation／sampled RSS，計算 CPU-time/wall 的平均使用核心數；不把 per-loop workers 當程序百分比硬上限 |
| E07 | 真正的 alpha 加權 log-luminance guided base＋scene-linear common-RGB highlight compression；獨立原型，不抄上游 GPL／AGPL，不更動現行 Basic | 同一實拍、同一 finisher，匹配固定 ROI 平均；至少兩個半徑。量 base build／apply／fresh versus reused base 的成本與完整像素等價；測黑／HDR 有限值、alpha／hidden RGB、強階梯梯度、已知乾淨紋理與固定 seed 噪聲。保存局部／全域／Basic 對照及實際 CPU／Metal 收尾差；不把 mean match 當細節或無 halo 證明，失敗照錄 |

原 v1／v2 probe 與原 tone exit 2 保留；擴充為新的 probe source 版本，組件 digest、命令、收據／輸入不變重新核對。重型程序仍全部循序，不在子代理編輯中執行 build／test／benchmark。只有資料準備／編碼可分工；parent 整合後統一建置／跑實驗。不得把 root cause 未定、provider fallback、ROI 匹配失敗或較差畫質改判為 pass。

執行前補註：E04 主比較在單一拋棄式程序循序切換四組，每組先選定 backend 再設停用 flags；CPU／both 必須重現既有完整 SHA，並追蹤 delegate 真正回傳與 colour preflight／錯誤。四組場景會同時保留供逐分量歸因，故其 RSS 不用來比較單一路徑。E07 的噪聲 RMSE 只算 alpha>0 的 RGB，不混入 alpha 或隱藏色；fresh／reused base 同時核對完整 float 與實際 finisher 輸出 SHA。

E07 執行後的限縮補測：原 `local` 四組均得到 `unreachable-floor-above-target`，所以 matched-candidate 的 apply／Metal 分支未執行。保留這版 source／全部失敗匹配；另以原重用檢查已指定的 `k=1.75` 執行 `local-fixed`：六筆套用成本、CPU／Metal 真實收尾、完整 fresh／reused float 與輸出 SHA、k=0 identity 及預覽。它不是等亮度替代，不能藉此推翻原失敗或宣稱畫質較好。

- [x] E04 四組 RAW 因子對照與離群定位完成。
- [x] E05 CPU／CoreML、資料域與完整 f32 I/O control 完成。
- [x] E06 CPU 1／2／4 workers 交錯樣本完成。
- [x] E07 真正局部 guided 候選、重用及畫質／成本對照完成。
- [x] 全部新收據回填、封存移除 macOS AppleDouble／xattrs、逐檔 digest 與所有新連結核對；保留反例，不要求全綠才准結束研究。
