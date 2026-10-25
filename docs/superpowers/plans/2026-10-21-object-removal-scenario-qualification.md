# Object Removal Scenario Qualification Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `executing-plans` with `reflective-research` and `reflective-risk` after the execution envelope is authorized. Steps use checkbox (`- [ ]`) syntax. This is a research qualification plan, not authorization to implement a product backend.

**Goal:** 用最少新增請求判斷既有 Codex 影像入口能否跨過「自然物件＋影響確實消失」的必要條件，再決定是否值得投入完整產品資格比較。

**Architecture:** 保留 FilmBatch 共用引擎；候選來源只提供不可信的外部像素，host 決定正式 mask、座標、合成與保存。先研究來源是否值得採用，不先造 provider framework、修補 UI 或專案格式。正式產品的接受／重播仍依既有設計，尚未實作。

**Tech Stack:** 現有已登入的 Codex CLI、已存在的公開 v2 素材、現有隔離 Python／NumPy／Pillow 與手動 PNG16 decoder；不安裝新模型／SDK／runtime。實際 CLI 版本在執行前重新記錄，不把歷史 0.163.0 當永久相容保證。

**Spec:** [Survey 第 19 節](../../research/object-removal-survey.md#19-情境解法成本效益與決策紀錄)，決策版本 1（2026-10-21）；[操作與資料契約](../../research/object-removal-design.md) 版本 1及[原產品實作計畫](2026-10-20-ai-object-removal.md)。本計畫不修改 OR-01–OR-14，也不以外部服務滿足 OR-02 的本機模型要求。

## 狀態與 Definition of Ready

**原 R1 前檢查 BLOCKED／資料中斷紀錄保留；R2 已依使用者續跑授權完成兩次真實 CLI 影像及四個同案本機對照，兩案自動使用篩選仍 NO-GO。通用產品 T1 仍 NO-GO，T2–T5 未開始。** 詳見 [Survey 第 21 節](../../research/object-removal-survey.md#21-中斷後實際續跑與同案低成本對照)。R2 另凍結新 opaque canonical，不冒稱恢復舊 RGBA16 alpha、三位人類驗收或最高效益；原 Q／OR 門檻不變。

可立即做來源／資料的離線查核；新外部請求前必須同時具備：

- 使用者明確同意本輪固定公開素材與最多兩次 CLI dispatch；舊單次影像實驗的授權不無限延伸。若帳戶不能提供可核實的金額上限，須先由使用者接受「兩個 CLI process 不等於兩個內部影像呼叫或可核實金額上限」的限制，否則不 dispatch。
- 固定 manifest／prompt／工具與權限設定的 SHA-256；現有登入可用、帳戶條款允許用途。缺登入或條款不清楚就記 blocked，不要求新 Key、不自動註冊／升級方案。
- deterministic host 能把 CLI 隔離於空工作目錄與這兩張去 metadata 的 context／mask；repo、其他照片、shell、瀏覽、MCP／apps／plugins／hooks 不在可用能力中。僅在 prompt 寫「不要讀」不算隔離；版本不支援同等約束即停止。
- 三位獨立人類評審與至少一位實際操作人可參與品質／總工作量評估。缺人可保留技術結果，但不得宣告品質或成本效益通過。

## Global Constraints

- 不修改產品、既有證據、模型、全域權限或登入；不 commit／push。不跑 T2–T5、不碰 24 張 T5 留出集，不把這兩案算成完整 T1。
- 不外送私人照片、乾淨洞內參考、EXIF／GPS、原始碼、Key 或其他帳戶資料；也不能把 repo 當 CLI 工作目錄。只保留經遮蔽、定向且重新編碼的公開 context 與 binary mask。
- 只讀重用舊 `v2-wall-large` 結果作歷史可執行性參考，不重生它、也不計成這輪新自然移除成功或同協定配對。
- 最多兩次 CLI dispatch，每案一次；含狀態／校準／修訂的任何額外推論也會消耗上限，禁止用「測試登入」偷偷增加請求。查 help／version／非推論 login-status 不計推論。
- 每個 CLI process 最多 240 秒；這是研究停止線，不是產品延遲承諾。一次只跑一個來源請求，不啟動其他本機模型。新證據目錄最多 1 GiB、檔案系統至少保留 10 GiB；是本輪磁碟 guard，不是模型 RAM 或永續產品需求。
- 明確失敗、拒絕、OOM、權限擴張或第一案品質硬失敗就停止後續 dispatch；不縮小／換 mask、不換模型、不 best-of-N、不改 prompt 重試、不清 cache／resume 補格。第二案未跑照記 not-run，不從結果帳本消失。
- CLI dispatch 成功不等於取得有效候選。遇到 dispatch 後逾時／斷線／crash 且遠端結果不明，記 `OUTCOME_UNKNOWN`、`retry_safe: false`；不得因無檔案就認定沒扣額度。保留 invocation ID、時間與 receipts，由使用者決定查帳／放棄，不自動重送。
- RGB8 候選不冒稱真正 RGB16／RAW；host 在同一 canonical 平面直接保留 mask 外 RGB 與全部目的 alpha。新尺寸／裁切未知時拒絕，不用隨意對齊掩飾座標錯誤。

## 檔案與共享契約

執行時才建立忽略目錄 `dist/object-removal-scenario-qualification-20261021/`；以下都是未來研究產物，不表示本輪已存在。

| 檔案／位置 | 責任與精確輸入／輸出 |
|---|---|
| `manifest.json`、`approval.json` | 計畫版本／SHA、兩案來源與外送檔 SHA、mask polarity／bbox、context 範圍、來源權利、工具能力／版本、兩次 dispatch／時間／磁碟停止線、允許的登入用途；核准針對這組 hash，不是通用 provider 權限 |
| `src/probe.py` | 一個有界 runner；`main(manifest_path: str) -> int`，只讀 manifest 已批准檔案，逐案 dispatch 並保存 receipts／raw candidate；不負責評分，不匯入產品。不先建立共用 agent orchestration library |
| `cases/<case>/` | `context.png`、`mask.png`、`prompt.txt`、`dispatch.json`、原始 stdout／stderr、`provider.png`、`candidate.json`、`accepted-rgba16.png`、`contact.png`；不存在的輸出不得造空檔冒充完成 |
| `src/verify.py` | `main(root: str) -> int`；自行解 PNG16 bytes 與 float64 重算，不 import producer、cv2 或模型；輸出 `pixel-verification.json`，非零退出表示硬契約未過 |
| `observations.json`、`human-review.json`、`work-log.json` | 協調者觀察與獨立人類意見分開；操作時間、選取／修正／等待／重作、拒絕／失敗、未知扣款分開；不把 agent 視角湊成三位人類 |
| `decision.json`、`source-manifest.json`、`verification-harnesses.tar.gz` | 全部嘗試及未跑項、條件式決策；每次 dispatch 前綁當時 script／interpreter／manifest SHA，最後來源封存與離線恢復驗證，不用最後版本冒充所有歷史版本 |

重用來源：`dist/object-removal-prompt-guidance-20261021/inputs.json` 的兩個具名案例；`dist/object-removal-native-followup-20261021/cli/` 的既有權限／帳戶／外送紀錄；同根 `verification-harnesses.tar.gz` 中的 `cli/run_codex_image.py`、`cli/compose_codex_image.py`、`cli/verify_codex_image.py` 及 `verify_experiment.py`。以 archive 查當時 recipe，先讀後改；**不要執行舊 runner**，也不要改寫歷史 receipts。host resize／候選 digest 與 manual decoder 可重用做法，但每次的輸入／輸出邊界要獨立重算。

候選記錄至少含 `case`、`sourceSha256`、`maskSha256`、`contextSha256`、`promptSha256`、`scriptSha256`、`interpreter`、`cliVersion`、`argv`（不含憑證）、`dispatchId`、`startedAt`、`endedAt`、`status`、`retry_safe`、`providerFileSha256`、`dimensions`、`encoding`、`resizeMapping`、`actualImageModelId`、`internalImageCalls`、`billedCost`。未揭露的後三項保持 null／unknown，不從語言 turn 推算。

## Task 1 — 凍結可執行且不外洩的兩案

**依賴：** 上述 Ready；**Human Review：** 外送與額度限制。**產出：** 完整 manifest、可查核的外送 preview 與無網路 dry-run，不是 shell prompt 拼接器。

- [ ] 從現有 manifest 精確取 `v2-droid200-shadow`、`v2-bikes-rack`，保持原 512px、mask／context，不擴洞或偷帶乾淨真值。核對 frozen 來源 digest 與使用權；它們是兩個自然移除場景，不拿原本被移除物件作「正確背景」。
- [ ] 由原既有遮蔽 RGB 製作去 metadata PNG，binary mask 的 1 表示待修補；manifest 列明兩張圖的角色、尺寸、polarity、完整 context 範圍。遮蔽圖不含任何洞內原物件像素；不要給模型原始 RGB16／乾淨參考。
- [ ] 固定每案一個背景修補指令：移除已標記物件及已標記影響，只延續可見周圍材質；不指定隱藏背景真值，不使用 LLM 自動改寫或 critic loop。先決定結果縮回 512 的一次 bilinear mapping，後續不可看結果才調插值。
- [ ] 將過去 archive 的最小 image recipe 限縮到 manifest，記錄可用工具與 sandbox。只做 argv／附件／capabilities 的離線 dry-run；以外送清單與 empty workspace 核對沒有 repo／私照／clean truth／metadata，不發模型請求。

**驗收：** Q-01。任一輸入／授權／隔離缺項 → BLOCKED，有精確原因；不以更強權限試到成功。

## Task 2 — 最多兩案真實影像與可保存候選

**依賴：** Task 1；**Human Review：** 每次已批准範圍以外的動作。**產出：** 真實候選或真實失敗、所有 receipts、host 合成與可視 contact。

- [ ] 先跑 `v2-droid200-shadow` 一次，記整個 process 起訖 wall 與可得用量，保存 provider 新檔 bytes／SHA／時間。模型說「成功」但無新候選或檔案不屬本次 dispatch → 不算成功；新 path 仍需在允許的來源根目錄下驗證，不任意讀取模型回覆的檔案路徑。
- [ ] host 驗證編碼、尺寸與完整輸出畫框。先 canonical resize，再只取正式 mask 內 RGB；目的 alpha 沿原圖。拒絕未知 crop／移位／非有限值；保存全部原始結果與 source／mask／candidate 對照，先看全圖及 100% ROI。
- [ ] 第一案沒有目標殘留、影響殘留、顯眼新物件、受保護內容變形等硬失敗時，才執行第二案 `v2-bikes-rack` 一次；同規則記錄。第一案硬失敗直接 NO-GO 此輪必要條件，不再用第二案補漂亮數字。
- [ ] 另以現有 RGB16 非對稱程序控制與**已保存候選**做零請求合成：binary mask、分數 coverage、部分透明目的 alpha；驗證 `M=0` 直接拷貝、alpha 不跟 provider 走、straight RGB 不重複 premultiply。程序控制只驗 host，不算自然移除成功。

**驗收：** Q-02、Q-03。不得 mock 影像生成、以空檔／原圖當輸出、重跑失敗格或自動用其他 CLI／API。至少保存一個獨立 oracle 能偵測的故障控制，例如整張 provider 覆蓋原圖，證明 mask 外檢查有效；使用 throwaway 記憶體資料，不污染正式結果。

## Task 3 — 判斷有沒有值得投入的實際效益

**依賴：** 真實保存結果；**Human Review：** 三位人類評審／產品擁有者，agent 不自批。**產出：** 品質分歧、使用者總工作量與明確 Plan／No-Go 決策。

- [ ] 各評審依 Survey 第 6 節四項 0／1／2 分，隱去來源名稱與順序，整圖及 100% ROI 都看。硬失敗先判；每案四項均由多數給 2 才記可接受。只有兩案仍是必要條件小篩選，不是 ≥80% 留出品質 gate。
- [ ] 實際操作者在自己已使用、條款合格的人工／外部編輯器上處理同兩案；預先記工具／版本、相同目標與「原圖不能改」條件。記 setup、選取／修正、候選等待、檢查／拒絕、重作與總完成時間；人工不強迫用一次複製完成，也不把不同功能當等價品質。沒有可用人工比較就保留該成本 unknown，不以 agent 猜測補數。
- [ ] 先比較可接受結果與人工負擔，再列付費、等待、安裝／記憶體與維護限制；cash、human time、wall 分開，訂閱 sunk cost 不寫零。不提供「全市場最便宜」分數，也不從歷史本機秒數推定此輪因果速度優勢。
- [ ] 若任一自然案不可接受、必要隔離／帳戶條款不可滿足，或無法證明比現有做法更有用，記 NO-GO／證據不足及確切重開條件。兩案均可接受且總工作有優勢，只可建議擴至原 40 場景／3 位評審資格比較；外部模式要另改 scope 並取得同意，**不自動滿足本機 OR-02、不啟動 T2–T5**。

**驗收：** Q-04、Q-05、Q-06。Human Review 不可用時交付技術報告與 blocked 資格，不偽造接受率或時間節省。

## 研究驗收與 Review Criteria

下列是**未執行**檢查；不新增永久產品測試。執行時建立上述兩支 temporary script 後才使用以下命令，不是目前已存在的產品 CLI：

```bash
ROOT="$PWD/dist/object-removal-scenario-qualification-20261021"
PY="$PWD/dist/object-removal-compact-depth-20261020/venv/bin/python"
# Task 1 manifest／approval 及 deterministic host gate 全部核准後才准跑 probe。
"$PY" "$ROOT/src/probe.py" "$ROOT/manifest.json"
# verify 只讀已保存資料，不會再發請求。
"$PY" "$ROOT/src/verify.py" "$ROOT"
```

| ID | 可觀察結果／檢查 | 拒絕條件 |
|---|---|---|
| Q-01 | Task 1 查外送 PNG／manifest／dry-run／授權 hash；只含兩案的 context／mask | 含 clean truth／私照／repo／metadata、版本或權限缺證據、預算未明確接受 |
| Q-02 | Task 2 看新影像 bytes／dispatch receipts／實際畫面；核對未跑及 unknown 也入帳 | 只有文字、自動重試、換路線、未留 OOM／逾時、內部 call count unknown 卻報精確帳單 |
| Q-03 | `verify.py` 解 saved PNG16／獨立合成，全部 mask 外 RGB／alpha 精確保留；錯誤全圖覆蓋必須被抓到 | 放寬容差、驗 producer 成功旗標、把量化容器當原生 16-bit 生成 |
| Q-04 | Task 3 `human-review.json` 逐案保留三位人類四項分數／硬失敗／分歧；自然移除不算 truth PSNR | 用已知牆面代替自然物件、拿 agent 當人、挑 seed／mask、偷用 T5 |
| Q-05 | Task 3 `work-log.json` 分開現有比較、人工接觸時間／等待／失敗／費用未知；按可接受件數計成本 | 缺人工資料卻稱最高效益、只報成功延遲、不記 setup／重作、RSS＋driver 相加 |
| Q-06 | Task 3 `decision.json` 對兩案必要條件、帳戶／維護與產品 gate 分別給狀態；每個 NO-GO 有重開條件 | 兩案成功就通用產品 GO、以外部模式默改本機 OR-02、把本計畫勾成 T1–T5 已完成 |

Review focus：mask 內殘留陰影／新物件、provider resize 導致保護物幾何變形、opaque／fractional alpha、任意輸出 path、CLI 版本變更後的 tool exposure、逾時後未知扣款、總人工時間缺項。最後只封存**本輪自己建立**的來源、所有版本／收據及恢復驗證；保留既有模型／照片／研究目錄，不作順手清理。

## 其他情境與重開方式

離線／普通硬體沒有合格通用模型、Windows runtime 未跑、原生精度反例、未閉合權重 grant、Depth 硬 gate、完整 amodal／高頻量產等，均依 [Survey 第 19 節](../../research/object-removal-survey.md#19-情境解法成本效益與決策紀錄) 各自記錄 No-Go／缺證據，不建立空殼 implementation task。出現具名新 artifact、可存取的 eligible 裝置、允許的 endpoint／費用上限或獨立人力效益證據，才開對應小輪；不擴充此兩請求計畫。

**Next action:** 兩次真實 dispatch 已完成且預算用完，不重抽。先由人類確認 mask 覆蓋、細線／輪框接合及真實操作總成本；任何新影像請求都須另訂明確假說與範圍。中央玩具圖只保留為人工覆核候選；自行車案硬失敗，不能用像素保存通過替代品質。原 T1 及 Q-04／Q-05、OR gate 不因本輪技術結果解除。

## 2026-10-21 執行紀錄與後續選擇

使用者要求 commit、盤點可清除資料，再只做相當可行的實驗。先以 `7b3c200` 提交既有六份研究文件；未 push。完整空間／依賴／清理裁定見 [Survey 第 20 節](../../research/object-removal-survey.md#20-清理盤點與下一輪前檢查)。本輪沒有刪除既有 `/tmp`／`dist` 研究資料，也沒有為預計不再使用的模型再下載材料。

| 本輪檢查 | 已觀察結果 | 不能宣稱的事 |
|---|---|---|
| 兩案公開輸入封包 | 四張去 metadata 的 512px PNG；binary mask、洞內 context 零值及原始 digest 核對 | 不是新模型候選或自然移除成功 |
| 原登入與環境 | ChatGPT 登入可用；獨立 `CODEX_HOME`，只引用既有登入、不複製 token；結束後移除本輪引用、原 metadata 不變 | 沒有更換登入／Key／付費方案或宣稱已知 quota |
| 原生 CLI 停用前檢查 | `0.163.0`；16 個指定停用項中 15 個為 false，`unified_exec` 在 `--disable` 後仍為 true | 沒有證明實際 shell／一般執行 tool 不可用，不以 `shell_tool=false` 偷代 |
| 推論／品質 | 在 Q-01 停止；0 次 live dispatch，兩案均 not-run | 不把未跑當畫質 fail，不勾選 Q-02–Q-05 或 T1 |

**本輪的可執行研究已止於真實前檢查，不以較容易的像素複製或新的假候選替代所要驗證的自然移除。** 一次 `codex sandbox macos --help` 舊介面拼法在此版本被當成執行 `macos`，失敗在模型請求之前；未拿它作隔離證據。另一次 JSON 診斷讀取把 object 當 array，改用 key projection，沒有更改原檢查資料。這些屬 driver／查核事件，不是模型失敗。

原證據寫入 `dist/object-removal-scenario-qualification-20261021/`；交付前，該目錄連同七個既有研究根目錄內容突發遺失，來源未知，不是本助手執行清理。已恢復十份 kernel 物件、兩位 scout 留存報告及四張與原 manifest SHA-256 相符的公開 `/tmp` PNG；恢復檔明標 `recovered-*`，不得當成原始 receipt bytes。原 preparation source、feature raw stdout、舊權重／凍結輸入／歷史輸出未恢復。見 `workspace-interruption.json`、`recovery-verification.json`；493 份產品檔仍與原 baseline 相符，不能同時宣稱遺失的 80 份輸入及四張原照片通過最終重驗。Manifest 物件固定原提交版本的計畫 SHA，後續狀態註記不更改原接受標準。

R1 中斷後的重開條件如下；R2 的固定版本證據及資料重綁另列於末節，不刪除原判定：

1. 有固定 CLI 版本的零推論實際 tool inventory，或等效的 deterministic host 證據，證明模型不能調用 shell／一般執行／MCP／browser 或讀到未允許的資料；單看 flag／help／文字承諾不足。
2. 先恢復／重新取得具明確來源及權利的必要資料並核對 digest，另凍結實際可讀的新 manifest；仍限定相同兩案、每案一次、240 秒、既有登入與不重試停止線。不能把本輪恢復物件視為可直接重播的完整封包；若必須擴權／新 SDK／新增付費入口，該路線仍 No-Go，不自動換方法。
3. 技術輸出、host 像素驗證、人類接受與工作量各自記錄；缺人類資料不宣稱最高效益，更不改本機 OR-02 或啟動 T2–T5。

## R2：使用者要求後的實際續跑（2026-10-21）

- **範圍：** 只補原本 not-run 的兩個自然案例，最多兩個 CLI process、每案一次／240 秒／不重試；沒有新模型權重、Key、付費方案或產品實作。R2 合同與 SHA 固定在 `local://filmbatch-scenario-resume-20261021/manifest.json`，不以本文件後續註記追認原 R1 已執行。
- **隔離解法：** exact CLI `0.163.0`／release commit `83086188ae0614bd882818ff1aa23f3194962012` 的註冊邏輯＋有效 catalog 回讀：shell disabled、patch null、direct tool mode、MCP／browser／code-mode／plugins／hooks 等停用；空 HOME 與不注入 skills。這是版本綁定的 deterministic 控制，不是原始 tools wire manifest，也不是擴權繞過。
- **資料邊界：** 四張原公開附件、兩張 JPEG、兩個 raw mask 雜湊匹配；舊 RGBA16 alpha 未恢復。新 JPEG-derived opaque canonical 只供 R2 host 合成，RGB8 上轉不增加精度，不勾選原 Q-03／RAW／OR 通過。
- **實跑：** 兩張 1254×1254 RGB8 新候選，process wall 71.83／107.88 秒；固定 bilinear 回 512，正式 mask 內合成。另按輸出前凍結的已知區位移規則，跑兩案 hard donor／normal Poisson 共四圖，並明示 RGB8 Poisson 與非人工最佳來源限制。
- **已觀察：** 中央玩具案開放檢視未見明顯替代物，可人工覆核；車架 raw 另造立柱，host 合成仍有支腳／輪框錯位與 mask fringe，硬失敗。便宜 donor 會複製前景／破壞結構，不能作通用替代。
- **驗證：** 六圖 6,291,456 個 RGBA16 samples，獨立 saved-PNG／bilinear／hard-copy／保存核對通過，洞外 RGB 與 alpha 零差異；不是人類接受或完整 solver／原生平台 fidelity。初版 verifier 和後續新增 local-control 檢查的版本均留存。
- **裁定：** 此兩案自動使用 NO-GO；不推進 T1–T5，不把 1 張有希望圖計為盲評接受率。實際 image model／內部呼叫／費用、人類總工作量仍 unknown。兩次請求後停止；沒有新 seed／prompt／mask 試到成功。

可直接檢視 `comparison.png`、兩案 `contact.png`／`roi-100percent.png`、`decision.json`、`verification-summary.json`、`auth-reference-cleanup.json`；登入臨時引用已移除，原 metadata 不變。來源封存／恢復驗證只執行離線 verifier，**不以恢復測試重發模型請求**；照片／衍生圖不隨文件提交。三份原產品契約（design／ADR／T1–T5 plan）未更改，因為產品資料域、保存與採用門檻沒有變更。

