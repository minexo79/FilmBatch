# Governed Verification and Session Knowledge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to execute this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This is an attended audit, not an autonomous governance runtime.

**Goal:** Verify FilmBatch through its real executable surfaces and preserve useful session-only decisions, procedures and plans in portable repository documentation.

**Architecture:** Reuse the existing solution tests, shader validator, packaged App/CLI, domain docs and ADRs. Add only missing navigation and verification entry points; distill historical notes into domain-owned facts and dated provenance rather than copying transcripts or building a second governance system.

**Tech Stack:** .NET 10 / xUnit v3, Avalonia 12.1.4, WebGPUSharp 0.5.7 / Dawn / Metal, naga-cli 30.0.1, Markdown documentation and existing CI/build tools.

**Spec:** AUDIT-v2, defined by the acceptance criteria below and the user's requests to use flow/governance/reflective methods, recover session-only knowledge, consider memory-palace-style navigation while preserving existing material, and make decisions traceable through real commit history.

AUDIT-v2 supersedes the initial AUDIT-v1 plan following the explicit commit-history request. Revalidate the documentation/acceptance joins against v2; the unchanged product revision, command inputs and observed runtime results remain dated evidence, not new human acceptance.

## Global Constraints

- This audit authorizes attended local verification and repository documentation edits, not a release, push, commit, notarization, credential change or branch-protection change. Historical approvals do not grant new authority.
- Do not change product behavior, test expectations, numeric tolerances, native dependencies, existing plans or policy merely to make verification green. Classify a failure before proposing repair.
- Use generated photos and isolated profiles/output directories. Never mutate original user photos or the existing release archives for this audit.
- Parent owns all shared builds/tests/shader/UI runs and repository edits. Research workers read only and do not run shared checks or mutate git.
- Historical notes and agent summaries are leads, not fresh runtime evidence or authority. Preserve dates, revision boundaries, rejected hypotheses and superseded conclusions.
- Do not commit raw transcripts, hidden reasoning, credentials, personal data or machine-specific scratch paths. Preserve concise decisions, alternatives, invariants, commands, falsifiers and provenance.
- Existing domain docs remain canonical. A navigation analogy must not duplicate factual ledgers, create competing authority or claim a measured cognitive benefit.
- Prose, CI YAML and record hashes do not prove enforcement. Unknown host controls and unexecuted platform checks remain explicit.

## Acceptance Criteria

| ID | Observable outcome | Verification |
|---|---|---|
| AUDIT-01 | Bounded verification flow and proposal/authorization/effect/acceptance boundaries are documented; no false claim of sealed or enforced governance | Gate and authority review against actual tools/CI; explicit host-control dispositions |
| AUDIT-02 | Every supplied session-local Markdown handoff is classified; useful missing decisions/procedures have durable homes and provenance | Complete recovery inventory and sentinel comparison against current source/tests/domain docs |
| AUDIT-03 | Current solution, expanded WGSL and actual packaged App/CLI paths are exercised without user-photo mutations | Actual command exits, test totals/skips, shader results, native UI capture, CLI exports and source hashes |
| AUDIT-04 | A reader without the session can locate architecture, verification, lifecycle, RAW/color, film calibration, decisions/provenance and remaining work | Navigation review using only repo entry points; all destination links/anchors resolve |
| AUDIT-05 | Memory-palace-style navigation is adopted only where useful, preserving existing content and canonical ownership | Compare task-to-document routes before/after; no duplicate taxonomy or unsupported learning claim |
| AUDIT-06 | Commands and acceptance joins resolve without deleted scratch projects or ignored dist files as sole dependencies | Execute recorded drive commands; validate links and ID-to-check map; retain portable dated evidence |
| AUDIT-07 | Selected real commits trace each major decision to rationale, current contracts/tests and later supersession; merges are not misrepresented as a linear rewrite | Resolve all recorded commit IDs/parents locally; distinguish commit intent from runtime proof and record the historical source boundary |

## Baseline and Evidence Classes

- Source baseline: `6e3291a5cbe33d267ec33b9d42054efde9bd82f3`; worktree clean when this audit began. This identifies source inspection and current checks, not every historical package's build provenance.
- Observed SDK: .NET `10.0.402`.
- Existing packaged App/CLI and ZIPs are preserved. Re-exercising an existing package is not a fresh source publish; record its digest separately.
- `current observed`: a command/scenario executed for this audit with an exit and input set.
- `historical observed`: previously executed evidence with the original revision/date and boundary retained.
- `source inspected`: a contract exists in code/tests/config; not proof its runtime or external host configuration executed.
- `proposed / unknown`: a candidate or unverified boundary, never a passing check.

## Flow and Authority

| Gate | Owner / release basis | Failure disposition |
|---|---|---|
| Intent | Current user request permits this local audit and documentation scope | Stop a newly irreversible/external effect for user decision |
| Spec | AUDIT-v2 criteria above; do not reinterpret historical instructions as current scope | Record a scope change explicitly before dependent work |
| Plan | This task packet and ownership map; deterministic file/command locations | Resolve missing inputs before execution |
| Execution | Parent runs existing commands; workers return read-only research leads | Preserve raw failed output and classify the owning surface |
| Verification | Deterministic results plus actual runtime observations; model review is advisory | Product regression / doc drift / spec-oracle error / harness failure remain distinct |
| Acceptance | Technical audit disposition is evidence-bound; project/release acceptance belongs to the user/maintainer | Do not self-issue a human approval or infer remote CI/branch protection |
| Retro | Dated recovery disposition, lessons and rejected additions | Keep process proposals separate from activated operating rules |

Proposal: assistant/research workers. Authorization: current user and existing host tool permissions. Effect: actual local tools; no newly implemented broker or capability issuer. Technical verification: command outputs and observations. Human acceptance: user/maintainer, not the executor's success statement.

Host preconditions are assessed, not invented: oracle sealing, sink isolation, budget enforcement, durable ledger storage and human decision channel. The documentation's status may be `artifact-complete`; it must not be called `enforcement-proven` without separate observed enforcement evidence.

## Execution and Durable Homes

1. Recover the session-local Markdown inventory and compare it with existing docs. Keep a disposition for every source, including already-durable/superseded/excluded material.
2. Inspect current test/CLI/UI/build/CI surfaces and run the existing checks once at the integration point. Install the pinned shader validator only in isolated scratch space if absent.
3. Integrate missing domain knowledge into `docs/architecture.md`, `docs/building.md`, `docs/film-looks.md`, relevant ADRs or `tools/raw-oracle/README.md` as appropriate. Preserve historical plans and source pins.
4. Add a concise docs index and verification control document only for the demonstrated entry-point gap. Reuse existing domain pages as feature destinations rather than adding an empty features/governance hierarchy.
5. Add a curated `docs/history.md` decision index from actual reachable commits, linking historical intent, canonical domain documentation, verification and superseded paths. Git remains the full log; do not copy author identities or every patch into prose.
6. Review claims and portable navigation, check all touched links/anchors and acceptance-to-command joins, and preserve a dated audit record. Record any checks that cannot run instead of substituting source review.

## State Ledger

| Item | Status | Evidence / next step |
|---|---|---|
| Scope, authority and baseline | verified | Current user requests; clean baseline and SDK observed |
| Session-only recovery inventory | verified | 33 historical Markdown handoffs classified below; current task brief excluded |
| Current executable verification | verified, bounded | V1–V5 observations below; external corpus, Windows runtime and remote GitHub controls not claimed |
| Documentation ownership / navigation decision | verified | Fresh-reader review plus task-route comparison; existing domain docs remain canonical |
| Durable integration and provenance | verified | 33 handoffs classified; promoted source sentinels reviewed against current code; historical proposals preserved |
| Independent review / link and acceptance checks | verified, bounded | Two source/doc advisory reviews; 161 local links, seven AUDIT-v2 joins, 27 test names and 36 commit IDs checked; no host-enforcement claim |

## Deliberately Not Added

No unattended agent runner, retry loop, policy broker, capability-token system, new core skill, duplicated feature-document tree, raw transcript dump or scientific claim that a memory-palace analogy improves learning. Reconsider a runtime only when a concrete persistence/replay/cancellation/enforced-authority requirement cannot be met by the existing tools and documented gates.

<a id="recovery-inventory"></a>
## Session Recovery Disposition

來源：同一 FilmBatch 工作階段 `01a13e01-f2e0-70e8-b6ac-ab2f176cd24c` 中，2026-10-15–18 的 33 份歷史 Markdown 交接筆記。當次稽核 brief 不計入歷史來源；不複製逐字對話、推理、機器路徑或舊執行權限。分類以現行來源／文件核對，不把筆記的「已完成」當成本次執行證據。

處置合計：11 份已有 durable home、9 份抽取理由／陷阱或補導引、10 份被取代、3 份排除暫存操作資料。下表保留每份來源的名稱，閱讀與重現不需要存取原工作階段。

| 來源 basename | 處置 | 現行位置／理由 |
|---|---|---|
| `rawcontrols-ledger.md` | 抽取 | [參數／復原](../../architecture/core.md#41-core參數-schemaparams)：Text 不做路徑處理，名稱＋實例一次 undo |
| `raw-parity-shared-contracts.md` | 抽取／補導引 | [場景域契約](../../architecture/engine.md#46-engine算圖管線rendering)已存在；`Rgba16` 是顯示快取，不作 RAW 編輯輸入 |
| `filmbatch-lightcraft-latest-review.md` | 已有 | [RAW 參考與限制](../../../tools/raw-oracle/README.md)、RAW parity 計畫 |
| `filmbatch-lightcraft-source-findings.md` | 被取代 | 初步假說由後續實驗／parity 記錄裁決，不獨立宣稱已驗證 |
| `filmbatch-commit-delivery.md` | 被取代 | 當時的兩項 RAW UI 問題由 `cc23364`／`7504087` 與現行測試涵蓋 |
| `filmbatch-raw-ui-repair-plan.md` | 已有 | [RAW UI 回歸](../../testing.md#raw-介面回歸)：面板可見性、matching source、慢重載縮圖 |
| `filmbatch-pull-reconciliation.md` | 已有 | [畫布生命週期](../../architecture/app.md#410-app介面層)：旋轉分割線逆映射；封裝根結構見 building |
| `filmbatch-pull-preservation.md` | 排除 | 當次 stash／pull 保存步驟；不是可沿用的操作授權或穩定介面 |
| `filmbatch-lifecycle-verification.md` | 已有 | architecture／building 的生命週期與 Cocoa 動畫邊界；舊 totals 留在歷史 |
| `filmbatch-lifecycle-implementation.md` | 被取代 | 預先分工由正式實作與驗證記錄取代 |
| `filmbatch-canvas-integration.md` | 抽取 | [獨立畫布 Fit 契約](../../architecture/app.md#410-app介面層)：Pan reset 不能只放 ViewModel |
| `filmbatch-cancel-review.md` | 抽取 | 保留 close-to-apply、裁切套用後停用工具、舊 Service ownership 的理由；不是全部對話框都有 rollback Cancel |
| `filmbatch-mac-build.md` | 排除 | 暫存封裝／雜湊 receipt；本次封裝身分另有實際觀察 |
| `filmbatch-windows-raw-precision.md` | 已有 | [ADR 0003](../../adr/0003-gpu-stack-decision.md)：D3D12 `fma`、補償算術與精度限制 |
| `filmbatch-merge-implementation.md` | 已有 | ADR 0003／[history](../../history.md)：LUT／裁切合併、pending crop 不被被動重載清掉 |
| `filmbatch-merge-review.md` | 被取代 | 合併前衝突方案，由實際 merge／後續修復裁決 |
| `filmbatch-fix-commit.md` | 已有 | ADR 0003：傳遞曲線 sweep、LUT log2 差異與不放寬門檻 |
| `filmbatch-simd-delivery.md` | 已有 | ADR 0003：SIMD 量測與拒用近似運算；不把舊時序當目前效能 |
| `raw-completion-progress.md` | 排除 | 暫存分派與中途狀態，不保留為完成證據 |
| `raw-completion-plan.md` | 抽取 | [讀檔上限](../../architecture/engine.md#48-engine影像讀取與批次匯出imaging--exporting)、[原生閉包](../../release.md#macos)；原分工／API 草案不作現行權威 |
| `raw-fix-delivery.md` | 抽取 | 大型一般 TIFF 與 managed RAW 全檔限制分開、寬高索引、RW2／ORF 標頭；舊 exclusions 不沿用 |
| `raw-review-fixes.md` | 抽取 | 單次線性化、ICC 宣告／轉換分離、CR3 CMT 各自的 byte order |
| `raw-tiff-api.md` | 已有 | `src/FilmBatch.Raw/Tiff/` 的現行型別與 RAW 來源契約，不另複製 API 草稿 |
| `raw-port-ledger.md` | 被取代 | 早期完成狀態由後續 capability／parity 交付取代 |
| `raw-port-interfaces.md` | 被取代 | 草稿介面由實際程式碼取代 |
| `raw-port-plan.md` | 被取代 | 初始鏡頭／gain table／遮罩／JXL／DNG 排除項不是永久功能限制 |
| `review-dev-i18n-en.md` | 已有 | 語系資源與原生選單契約、`LocalizationTests`、history 的實際提交 |
| `histogram-smoothing.md` | 抽取 | [直方圖快取](../../architecture/app.md#410-app介面層)：只濾波顯示高度，資料更新重建 geometry |
| `filmbatch-ui-bugs.md` | 已有 | 漏光旋轉／filmstrip 操作的現行測試；Sony 配方特定校正已由 LUT 模型取代 |
| `filmbatch-metadata-review.md` | 抽取 | [EXIF 降級與輸出規則](../../architecture/engine.md#48-engine影像讀取與批次匯出imaging--exporting)：APP1、R98、圖層過濾及逐張 warning |
| `filmbatch-repair-progress.md` | 被取代 | M0 中間狀態、Intel Mac 封裝不作現行平台承諾 |
| `filmbatch-repair-contract.md` | 被取代 | 舊 M0 任務範圍，由目前建置／驗證入口取代 |
| `filmbatch-macos-ledger.md` | 被取代 | 初始 Mac port receipt，由目前 Dawn 建置、相依下限與 arm64 流程取代 |

回收時拒絕直接搬入的說法：過時的 JPEG description 截斷細節、已移除的獨立 RAW finish／4096 alpha cap、「曲線核心全未實作」、將上游 Dawn pin 當 FilmBatch commit、把 CPU／GPU 一致當獨立色彩正確性。每項以目前程式碼或實際 Git parent／主旨裁決，沒有重新開啟已完成的產品實作。

<a id="current-observations"></a>
## Current Observations — 2026-10-18

來源基準為本頁上方的完整 commit；SDK `10.0.402`，Apple M3／Metal，macOS 26。產品程式、測試、數值 oracle、相依與發佈檔未因本次文件稽核而修改。以下是本次執行，不借用之前的 14 個 RAW 實驗作新證據。

### V1 — Source Tests

執行 `dotnet test FilmBatch.sln -c Release --nologo --logger trx --results-directory <scratch>/test-results`，exit 0。讀取三個 TRX 的結果；略過列的原因包含 `FILMBATCH_RAW_SAMPLES is not set`，不是「也通過」。

| 專案 | Passed | Skipped | Failed |
|---|---:|---:|---:|
| FilmBatch.Raw.Tests | 618 | 293 | 0 |
| FilmBatch.Core.Tests | 415 | 22 | 0 |
| FilmBatch.App.Tests | 141 | 0 | 0 |
| 合計 | 1174 | 315 | 0 |

### V2–V4 — Real Executable Paths

| 檢查 | 實際觀察 | 限制 |
|---|---|---|
| WGSL | 隔離安裝 naga-cli `30.0.1`；`dotnet run build/CheckShaders.cs -- --naga <scratch>/naga/bin/naga` exit 0，29 個展開 include 的編譯單元通過 | 靜態驗證不取代平台 GPU 執行 |
| Mac App | 原封裝 `FilmBatch --smoke`，隔離 TMPDIR／設定，exit 0，26 個 PASS 與 `SMOKE PASS`；實際擷取可見預覽、RGB／明度直方圖、三張縮圖、匯出結果與 Metal 狀態 | 非 Cocoa 動畫／實體按鈕的完整驗證；未重建 App |
| CLI PNG | 以 `samples` 產生照片，選三張於隔離資料夾，分別用 `--backend Metal` 與 `--software` 匯出 PNG、長邊 256；六筆 export entry 全為 `ok`，裝置分別為 Apple M3 Metal 與 CPU 參考實作 | 生成的一般影像，不代表真實相機語料 |
| 解碼後比較 | 儲存庫外的 .NET console 引用 Engine，以 `ImageLoader.LoadFull` 比較三對 PNG；尺寸 256×171、256×171、171×256，RGB16 最大差 0，alpha 全為 65535 | 不把兩條實作互相比對當上游／獨立色彩 oracle |
| CLI DNG | 三張照片經 `export --format dng`，三筆 `ok`；重解碼尺寸與三通道相符 | 這是算圖後的 DNG，不是原始相機 RAW 的無損轉檔證據 |
| `raw convert` | 從上述 DNG 取一張，以 `--compression uncompressed` 寫到新目的檔，exit 0；重解碼樣本位元組與尺寸相同。再次同目的檔 exit 2，目的檔 SHA-256 不變 | 不是所有相機／壓縮／JXL tile 的完整覆蓋 |
| 來源不改寫 | 三張輸入 TIFF 的 SHA-256 前後相符；只有生成照片資料夾的 `.filmbatch` 與隔離輸出被寫入 | 沒有以使用者原始照片做實驗 |

另直接取 `VERIFY.md` V4 的整段 shell 原文，在新的隔離目錄、僅系統 PATH 下執行：12 張新生成照片各跑 Metal PNG、CPU PNG、DNG，共 36 筆匯出全為 `ok`；無損 `raw convert` 成功，12 份來源 SHA-256 全部核對通過。這次重播驗證文件命令本身，與上方三對解碼像素比較是不同輸入集。

生成且受保護的三張輸入：

| 檔名 | SHA-256 |
|---|---|
| DSC_0012.tif | `ab5729212af4c323e960dfba98000c1ed2ebc1a97a28f56a1d06447de178a34d` |
| DSC_0013.tif | `7eecf28e35ed66bd982d33ecbd8bd956e38e044f4a4f37cb5eab93ec2a3751c4` |
| DSC_0014.tif | `2d59f1e6655b2c92d6306446416fde1e8760b095150821bcd894ba35c5ec8ab1` |

driver 曾錯猜 `samples` 的起始編號，另曾傳入不合法長邊 64（CLI 正確 exit 2）。處置為依真實輸出選檔、改用合法 256；分類是 harness 輸入錯誤，沒有改產品、驗收期望或容差。這些失敗不計為成功案例。

### V5 — Existing Package Identity

`codesign --verify --deep --strict`、`unzip -tq` 均 exit 0。這是檢查現有封裝，不是本次新 publish，也不把原始碼 HEAD 當成二進位檔的可重現建置證明。

| 現有產物 | 本次計算的 SHA-256 |
|---|---|
| `dist/FilmBatch-osx-arm64.zip` | `f0fcc10d437a3817298503d2d660e90c530667dffcc87df639ae1fdf383e3796` |
| `dist/osx-arm64/FilmBatch.app/Contents/MacOS/FilmBatch` | `e57e9cf0216849ee75f6875dcb62d159ac069868ae88e440338d0fc93e2f3124` |
| `dist/osx-arm64/cli/filmbatch-cli` | `1f16c4cbb164df101de7f7bcf2b7c2dd40d9c59cec2c4a93036c70a52b27da4e` |

封裝沿用歷史記錄的 ad-hoc 簽章；沒有 Developer ID／公證或 macOS 14／15 實機證據。Windows 之前的 PE／相依符號與 10 DLL 閉包檢查是歷史靜態證據，本次未執行 Windows，也沒有據此宣稱載入成功。當前隨附 JXL 的原生 ABI／樣本路徑由 V1 的現行測試涵蓋，但不是這次對解壓 Windows 發佈檔的執行。

### Host Controls and Unknowns

| 主張／控制 | 本次證據與處置 |
|---|---|
| CI 有平台／Shader／聚合工作 | 已讀 `.github/workflows/ci.yml`；這證明定義存在，不證明當前 revision 的遠端工作成功 |
| `pre-merge` 是否為必要檢查、ruleset 是否啟用 | 三個 `gh api` 查詢（branch protection、rulesets、revision runs）均 exit 4，缺少驗證憑證；另嘗試公開 API 未取得結果。標為 unknown，不推論未啟用；維護者／GitHub 管理員裁決 |
| oracle 封存 | 此次沒有修改產品 oracle，但工作樹可寫，未證明 host 防改、受保護分支或外部可信 seal |
| 隔離外部效果 | 使用生成照片、獨立設定／預設與輸出；未做 push、release、公證、憑證／規則修改。這不代表所有工具都被政策 broker 限制 |
| 預算／ledger／人的接受 | 工具有每次呼叫的 deadline；未證明全工作流預算強制或 append-only 儲存。repo 文件是可攜記錄，不是防竄改 ledger。人的接受由當次使用者／維護者決定 |

<a id="navigation-decision"></a>
## Navigation Decision and Review Basis

**Claim**：需要固定任務入口與可追溯理由，不需要新框架。**Evidence**：原首頁已有架構／建置／底片文件，但沒有集中驗證入口、提交決策索引；詳細驗收資料指向忽略的 `dist/`，數份失敗案例理由只留在工作階段。**Counterargument**：再加一套 taxonomy 或複製功能頁只會增加同步成本。**Decision**：只新增 `docs/README.md`、根目錄 `VERIFY.md` 與 `docs/history.md`；領域事實補到現有 architecture／building，保留原始計畫並標示歷史邊界。**Unknown**：沒有對人的記憶、速度或學習做實驗，不宣稱 memory-palace 效益。

| 任務 | 先前入口／缺口 | 本次穩定路徑 |
|---|---|---|
| 找目前行為 | 首頁有架構與歷史計畫並列，狀態需自行判讀 | 文件地圖 → 現行領域文件；計畫加狀態註記 |
| 從零驗證 | building 有個別命令，沒有集中順序／失敗分類 | VERIFY V1–V5 → 原有測試／產品；結果 → 本頁 |
| 找「為何不能刪」 | 部分理由只在 33 份交接筆記 | history → 現行契約／回歸；附上被取代路徑 |
| 判讀剩餘工作 | 舊 exclusions／phase 草稿容易被讀成現況 | 文件狀態表 → proposal／歷史計畫／已驗證邊界 |

這是導航與來源追溯的改善，不建立新的強制政策、功能樹、鎖定 acceptance manifest 或自主 agent runner。驗收追溯是文件 join，不宣稱通過需要 host immutable oracle 的強制封存驗證。

## AUDIT-v2 Traceability

下表為引用，不重新定義上方七項驗收條件；技術處置仍不等於維護者／發佈接受。

| 證據入口 | 涵蓋條件 | 檢查方式與狀態 |
|---|---|---|
| 本頁 Flow／Host Controls、[VERIFY](../../../VERIFY.md) | AUDIT-01 | 權限、effect 與接受分離；外部控制明列 unknown，無 enforcement-proven 主張 |
| Session Recovery Disposition | AUDIT-02 | 33 份名稱逐一處置；抽取的 sentinel 對照現行來源／領域文件；未漏掉基準清單 |
| Current Observations、VERIFY V1–V5 | AUDIT-03 | 實際 tests／WGSL／App／CLI 與來源 SHA，平台／語料邊界分開 |
| [文件地圖](../../README.md) | AUDIT-04 | Fresh-reader source/doc review完成；161 個本機文件連結／anchor 與 27 個測試類別名稱解析成功 |
| Navigation Decision | AUDIT-05 | 比較前後任務路徑；既有文件保留，不複製功能事實、不宣稱人的學習效果 |
| VERIFY、此版控內記錄 | AUDIT-06 | V4 原文實際重播成功；現有 V1–V5 命令、觀察、ID 與連結已核對，暫存 driver 非相依；不宣稱 immutable oracle join |
| [history](../../history.md) | AUDIT-07 | 36 個本機 commit ID、33 個引用主旨與 `703ff49` 的兩個 parent 解析相符；上游 pin 另列，不把提交意圖當 runtime 證據 |

## Final Review and Disposition

兩份分開範圍的 advisory review 分別檢查導航／命令與回收主張／來源一致性；都不是新的 runtime 驗證。來源一致性未發現阻擋問題。導航 review 的一項 P3 有效：原文把「非零」尺寸概括成正值合法範圍，但 CLI 對非正值選擇原尺寸。已以現行分支條件與實際 `--long-edge -5`（三筆 exit 0／原尺寸）確認並更正文案，沒有改產品。另將跨兩天的裁切分支歷史列改成日期範圍，避免把 merge 誤讀為較早發生。

Deterministic 檢查範圍：10 份新建／修改的 Markdown、161 個本機連結／anchor、33 份唯一回收來源、27 個可解析的測試類別、七項 AUDIT-v2 definition→trace 引用，以及精選提交／主旨／merge parent；不驗證外部 URL 可用性，也不把文件 join 當 sealed acceptance oracle。新入口／歷史／本記錄未含內部工具 URI 或個人機器路徑。

**Technical disposition**：AUDIT-v2 七項條件完成，文件 artifact-complete；產品原始碼、數值門檻、既有封裝不變。保留外部語料略過、Windows／較舊 macOS 執行、公證與遠端 GitHub 控制等明確邊界。這不是 enforcement-proven 或維護者／發行接受；本次不 commit、不 push。
