# docs 整理計畫

日期：2026-10-25。狀態：**已執行（2026-10-25）**。與原提案的差異見文末「執行結果」。依據是各文件的開頭、狀態欄與最後一次 commit，沒有逐份讀完；動手前請先確認。

## 原則

1. **不建 `archive/`。** 沒用的舊文件不另外收藏。還有參考價值的留在原位或移進 `research/`、`evidence/`，除非必要才刪除。
2. **入口文件要精簡。** `AGENTS.md`、`README.md`、`architecture.md`、`building.md`、`history.md` 只放導覽與摘要；太長的內容拆成獨立文件，入口用一兩句話加連結指過去。
3. **搬動或拆分後同步修連結。** README 與各文件互相引用了很多 `#錨點`，拆檔會讓這些連結失效。

## 1. 各文件處置

### 保持原位（現行契約）

`film-looks.md`、`cross-platform-imaging-experiments.md`、`adr/0001`～`0004`

### 入口文件：保留並精簡，長內容拆出

| 文件 | 現況 | 做法 |
|---|---|---|
| `README.md`（55 行，單格極長） | 文件地圖，物件移除那幾列一格塞十幾個連結；「文件狀態」表複述各文件的狀態欄 | 任務表每列只留一兩個入口；狀態表改成一行定位加連結 |
| `architecture.md`（668 行） | §1–3 是入口，§4 模組說明約 430 行 | 留 §1–3、§5–7；§4 拆成 `architecture/` 下的獨立檔：`core.md`（4.1–4.5）、`engine.md`（4.6–4.8）、`gpu.md`（4.9）、`app.md`（4.10）、`cli.md`（4.11）。原處每個模組留兩三句摘要與連結 |
| `building.md`（652 行） | 建置、測試、發佈、CLI、功能驗證記錄混在一起 | 拆成下表四份，`building.md` 只留「需求」與「建置與執行」 |
| `history.md`（288 行） | 精選演進表加 10-20 到 10-22 的長段落 | 表保留；長段落移進對應的計畫或 ADR；「後續調查想法」移到待辦 |

`building.md` 的拆法：

| 新文件 | 內容 |
|---|---|
| `testing.md` | 測試分類、回歸、CI、Shader 驗證 |
| `release.md` | Windows／macOS 發佈、簽章與公證、iOS 建置 |
| `cli.md` | CLI、本機 AI 降噪、AI 去背 |
| `evidence/` | 帶日期的驗證記錄：本輪實測（10-20）、AI／iOS 整合驗證、去背驗證現況、暗部功能驗證、iOS JPEG 色彩核對、互動／記憶體／濾波修正 |

### 研究類：移進 `research/`

`ai-denoise-survey.md`、`grain-white-balance-survey.md`、`shadow-recovery-survey.md`、`sigma-bf-raw-performance.md`、`object-removal-survey.md`、`object-removal-design.md`、`adr/0005`

- `object-removal-survey.md`（1259 行）是最長的一份。不刪，但在 `research/` 另寫一頁結論（NO-GO 理由與重開條件）當入口，原文留作細節。
- `sigma-bf-raw-performance.md` 的修正若已併入，結論補進 `engine.md` 的 RAW 效能段即可。

### 歷史計畫：留在原位，不歸檔

`plan.md`、`cut-and-mask/plan.md`、`superpowers/plans/*`（約 17 份，合計逾 8000 行）

- 不搬、不壓縮、不另建 archive。它們已被 `AGENTS.md` 與 `docs/README.md` 標成「歷史，不是需求」。
- 只在 `docs/README.md` 的任務表不再逐份列出，改指向 `superpowers/plans/` 目錄一個入口，避免入口變長。

### 進行中：先留

`perf-fix-plan.md`、`thumbnail-async-speedup.md`：兩份都是 10-25 的草稿，且都沒被 README 連到，先補連結。實作完後把量測數據與結論併進 `engine.md`，再依「必要才刪」決定是否保留。

### 才需要刪的

| 文件 | 理由 |
|---|---|
| `superpowers/plans/2026-10-23-loupe-task7-8-handoff.md` | 交接筆記，任務完成後沒有參考價值。確認 task 7、8 完成才刪 |
| 根目錄 `agent-messages.txt` | 疑似交接殘留，確認內容後刪 |

其餘一律不刪。

## 2. 必須留的文件內部的整理

1. **`adr/0001` 與 `0003`**：0003 修正 0001。0001 開頭加一行「部分取代，見 0003」，不併檔。
2. **`cross-platform-imaging-experiments.md` 與 `testing.md`**：測試段重疊。留實驗手冊為準，`testing.md` 放連結。
3. **`history.md` 開頭的工作階段 ID**：已查不到，拿掉。

## 3. 目標結構

```
AGENTS.md                      精簡入口
docs/
  README.md                    精簡文件地圖
  architecture.md              §1–3、§5–7 與各模組摘要
  architecture/                core.md、engine.md、gpu.md、app.md、cli.md
  building.md                  需求、建置與執行
  testing.md、release.md、cli.md
  film-looks.md、history.md
  adr/                         0001–0004
  research/                    各 survey、物件移除、sigma-bf、ADR 0005
  evidence/                    帶日期的驗證記錄
  superpowers/、cut-and-mask/、plan.md   歷史計畫，留在原位
  perf-fix-plan.md、thumbnail-async-speedup.md   進行中
```

`architecture/cli.md` 與 `docs/cli.md` 名稱重複：前者講程式結構，後者講使用方式，執行時改成 `architecture/cli-design.md` 以免混淆。

## 4. 執行順序（每步各自 commit）

1. 拆 `building.md`：建 `testing.md`、`release.md`、`cli.md`、`evidence/`，修連結。
2. 拆 `architecture.md` §4，修連結。
3. 搬研究類進 `research/`，補一頁物件移除結論，修連結。
4. 精簡 `README.md` 與 `history.md`；ADR 0001 加註。
5. 補進行中兩份文件的連結。
6. 確認後刪除兩個可刪項目。

## 執行結果

- 照計畫完成：`building.md` 拆成 `testing.md`、`release.md`、`cli.md`、`evidence/`；`architecture.md` §4 拆進 `architecture/`；研究類文件移入 `research/`；README 精簡；全專案連結與錨點已重寫，連結檢查只剩一個原本就壞的 `prophoto-output.md` 附件連結。
- 與計畫不同：`building.md` 另保留「色調曲線」「局部暗部還原」（各約 20 行的功能操作）；`architecture/cli-design.md` 如計畫改名；ADR 0001 只加註「ADR 0003 維持本決策並補充」，沒有標成取代，因為 0003 並未取代 0001 的決策。
- `history.md` 的長段落依去向搬到 ADR 0004、`research/shadow-recovery-survey.md` 的「整合記錄」與 `evidence/integration-notes-2026-10.md`；「後續調查想法」移到 `backlog.md`。`history.md` 仍有約 260 行（表後尚有 10-22～10-25 的各日期節），未再拆。
- 已刪除：`docs/superpowers/plans/2026-10-24-loupe-task7-8-handoff.md`（Task 7、8 在計畫帳本為 verified）與根目錄空檔 `agent-messages.txt`。
- 未做：`cross-platform-imaging-experiments.md` 與 `testing.md` 的測試段重疊尚未去重；`object-removal-survey.md` 沒有另寫結論頁（文件開頭已有結論段）。
