# 文件地圖

從[專案首頁](../README.md)進入；要驗證程式，直接讀 [VERIFY.md](../VERIFY.md)。本頁只負責導航，不另存功能清單或驗收結果。

## 依任務找文件

| 任務 | 先讀 | 接著查 |
|---|---|---|
| 第一次修改程式 | [架構與資料流](architecture.md#2-整體架構) | [修改食譜](architecture.md#5-修改食譜) |
| 確認改動沒有破壞功能 | [VERIFY.md](../VERIFY.md) | [測試](testing.md) |
| 調整參數、存檔或 undo | [Core 模組](architecture/core.md) | [測試對照](architecture.md#6-測試對照) |
| 排查算圖、RAW、白平衡、鏡頭或匯出 | [Engine 模組](architecture/engine.md) | [RAW 參考輸出](../tools/raw-oracle/README.md)、[RAW parity 計畫](superpowers/plans/2026-10-18-raw-pipeline-parity.md) |
| 排查 GPU 精度或效能 | [GPU 與 Shader](architecture/gpu.md) | [ADR 0003](adr/0003-gpu-stack-decision.md)、[Shader 驗證](testing.md#shader-驗證) |
| 排查介面、裁切、切換照片或大量載入 | [介面層](architecture/app.md) | [生命週期回歸](testing.md#編輯生命週期回歸)、[載入回歸](testing.md#大量照片載入與即時預覽回歸) |
| 調整底片色彩或新增 LUT | [底片風格](film-looks.md) | [LUT 轉換工具](../tools/film-luts/README.md)、[授權聲明](../THIRD-PARTY-NOTICES.txt) |
| 使用局部暗部還原或色調曲線 | [building.md](building.md#局部暗部還原) | [暗部研究](research/shadow-recovery-survey.md) |
| 使用 AI 降噪或去背、CLI | [CLI 與本機 AI](cli.md) | [ADR 0002](adr/0002-ai-denoise.md)、[去背驗證](evidence/cutout-2026-10-22.md) |
| 評估物件移除 | [物件移除結論與 Survey](research/object-removal-survey.md) | [ADR 0005](research/0005-ai-object-removal.md)、[設計](research/object-removal-design.md)；通用移除 T1 仍 NO-GO |
| 評估顆粒、白平衡或 Zone | [Survey](research/grain-white-balance-survey.md) | 候選不等於已採用功能 |
| 評估 RAW／AI／CPU／GPU 效能與亮暗部演算法 | [四產品 Survey](research/performance-raw-ai-survey.md) | [本機實驗規格](superpowers/plans/2026-10-26-performance-local-experiments.md)、[實測與裁決](evidence/performance-local-2026-10-26.md)；候選不是已支援功能 |
| 重播跨平台編解碼或色彩差異 | [實驗手冊](cross-platform-imaging-experiments.md) | |
| 建置 Windows／Mac 發佈檔、iOS | [發佈](release.md) | [ADR 0004](adr/0004-apple-mobile-shared-core.md)、[診斷命令](../tools/apple-mobile/README.md) |
| 了解當時為何這樣做 | [history.md](history.md) | 對應的 ADR 與計畫 |
| 找後續想法與進行中的方案 | [backlog.md](backlog.md) | [perf-fix-plan.md](perf-fix-plan.md)、[thumbnail-async-speedup.md](thumbnail-async-speedup.md) |

## 目錄定位

| 位置 | 定位 |
|---|---|
| `architecture.md`、`architecture/`、`building.md`、`testing.md`、`release.md`、`cli.md`、`film-looks.md` | 現行契約與操作方式；修改相關程式時一起更新 |
| `adr/` | 已採納的決策（GPU、AI 降噪、iOS 共用核心）；依文中日期與邊界解讀 |
| `research/` | 調查與研究，不是已支援的功能 |
| `evidence/` | 有日期的驗證記錄，只對當時版本與平台有效 |
| `superpowers/plans/`、`cut-and-mask/`、`plan.md` | 歷史計畫與設計稿，不是目前的需求 |
| `history.md` | 精選提交、理由與取代關係；Git 保留完整歷史 |
