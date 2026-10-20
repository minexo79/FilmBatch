# 文件地圖

從[專案首頁](../README.md)進入；要驗證程式，直接讀 [VERIFY.md](../VERIFY.md)。本頁只負責導航，不另存功能清單或驗收結果。

## 依任務走固定路徑

| 任務 | 先讀 | 接著查 |
|---|---|---|
| 第一次修改程式 | [架構與資料流](architecture.md#2-整體架構) | [一次編輯](architecture.md#3-一次編輯的完整資料流) → [修改食譜](architecture.md#5-修改食譜) |
| 確認改動沒有破壞功能 | [驗證入口](../VERIFY.md) | [建置與測試](building.md) → [本次有日期的稽核記錄](superpowers/plans/2026-10-18-governed-verification-and-knowledge.md#current-observations) |
| 調整參數、存檔或 undo | [參數 schema](architecture.md#41-core參數-schemaparams) | [專案設定](architecture.md#42-core專案與照片設定projects) → [復原](architecture.md#43-core復原堆疊commands) |
| 排查 RAW、白平衡、遮罩或鏡頭 | [讀檔與色彩契約](architecture.md#48-engine影像讀取與批次匯出imaging--exporting) | [RAW 參考輸出與限制](../tools/raw-oracle/README.md) → [八項整合需求](superpowers/plans/2026-10-18-raw-pipeline-parity.md) |
| 排查裁切、切換照片或非同步重載 | [介面生命週期](architecture.md#410-app介面層) | [生命週期回歸](building.md#編輯生命週期回歸) → [RAW 介面回歸](building.md#raw-介面回歸) |
| 排查大量資料夾載入、縮圖進度或 CPU 佔用 | [載入與背景 CPU](architecture.md#大量照片載入與背景-cpu) | [載入／即時預覽回歸](building.md#大量照片載入與即時預覽回歸) → [本次需求與觀察](superpowers/plans/2026-10-18-progressive-photo-loading.md) |
| 調整底片色彩或新增 LUT | [底片風格](film-looks.md) | [LUT 轉換工具](../tools/film-luts/README.md) → [授權聲明](../THIRD-PARTY-NOTICES.txt) |
| 排查 GPU 精度或效能 | [算圖管線](architecture.md#46-engine算圖管線rendering) | [GPU／SIMD 選型與量測](adr/0003-gpu-stack-decision.md) → [Shader 驗證](building.md#shader-驗證) |
| 使用 AI 降噪、安裝模型或排查推論 | [AI 降噪決策與資料域](adr/0002-ai-denoise.md) | [CLI／安裝操作](building.md#本機-ai-降噪) → [驗證計畫與結果](superpowers/plans/2026-10-19-ai-denoising.md)；比較其他方案時查[來源 Survey](ai-denoise-survey.md) |
| 評估物件移除、選取輔助與可重開的修補 | [續驗與採用決策](object-removal-survey.md#10-續驗與採用決策) → [VAE／checkpoint 來源續查](object-removal-survey.md#11-權重來源與後續研究) → [ADR 0005](adr/0005-ai-object-removal.md) | [操作與資料契約](object-removal-design.md) → [分階段實作計畫](superpowers/plans/2026-10-20-ai-object-removal.md)；通用移除仍 NO-GO，官方 VAE 非等價替代，場景微調未測；T1 未過，產品功能及完整平台資格未完成 |
| 評估顆粒、白平衡、膚色輔助或 Zone 測光 | [顆粒、中性點白平衡與 Zone Survey](grain-white-balance-survey.md) | 區分已觀察現況、工程提案與未知；顆粒晚期輸出、中性 patch、膚色獨立、Zone 只調曝光階調，不把候選當作已採用功能 |
| 修改匯出或 EXIF | [匯出安全規則](architecture.md#48-engine影像讀取與批次匯出imaging--exporting) | [CLI](building.md#命令列工具cli)；區分算圖後的 DNG 與保留感光樣本的 `raw convert` |
| 建置 Mac／Windows 發佈檔 | [發佈](building.md#發佈) | [簽章與公證](building.md#簽章與公證)；原生相依庫、目標 RID 與本機執行結果分開驗證 |
| 建置／驗證 iPad／iPhone 的共用編輯器 | [iOS 行動版操作與建置](building.md#ios-行動版) → [共用優先決策 ADR 0004](adr/0004-apple-mobile-shared-core.md) | [診斷命令](../tools/apple-mobile/README.md) → [已通過的 prototype／Simulator gate](superpowers/plans/2026-10-19-ios-simulator-prototype.md)；主要編輯器另行驗收，不把 Mac／compile-only 或 prototype 證據當成完整 iOS 支援 |
| 了解當時為何這樣做 | [決策與提交歷史](history.md) | 相應 ADR／現行契約／回歸測試；commit 標題不等於驗證結果 |
| 盤點後續工作 | 下方「文件狀態」 | 先分辨已實作、核心元件、未落地提案與歷史設計，不把舊 checklist 當目前缺陷 |

## 文件狀態

| 文件 | 定位 |
|---|---|
| [architecture.md](architecture.md)、[building.md](building.md)、[film-looks.md](film-looks.md) | 現行契約與操作方式；修改相關程式時一起更新 |
| [VERIFY.md](../VERIFY.md) | 執行入口與失敗分類，不是主機強制的政策或鎖定 oracle |
| [history.md](history.md) | 精選真實提交、理由與取代關係；Git 保留完整歷史 |
| [ADR 0001](adr/0001-webgpu-binding.md)、[ADR 0003](adr/0003-gpu-stack-decision.md) | GPU 選型、量測與後續修正；依文中的日期、狀態與邊界解讀 |
| [ADR 0002](adr/0002-ai-denoise.md) | 現行 RawNIND／NAFNet 選型、原生推論、取消、快取、模型雜湊與授權；整合驗收狀態以連結的計畫為準 |
| [AI 降噪 Survey](ai-denoise-survey.md) | 2026-10-18 的來源調查與 2026-10-19 的採用註記；歷史調查邊界不代表目前仍無推論 |
| [ADR 0004](adr/0004-apple-mobile-shared-core.md) | 共用 C# 與獨立 Avalonia 行動編輯器的已採納方向、AI 分支整合及證據邊界；實機、原生閉包與記憶體仍須獨立驗證 |
| [顆粒、白平衡與 Zone Survey](grain-white-balance-survey.md) | 2026-10-18／19 的顆粒模型、中性點 WB、膚色輔助與 Zone III／V／VIII 調查；只執行現有 CPU 顆粒觀察，未實作候選、未做盲評或效能校準 |
| [物件移除 Survey](object-removal-survey.md) | 2026-10-20 的模型／授權／UX 調查、前置及續驗證據與採用決策；已跑 macOS 原生模型與完整 Moebius 鏈，但通用移除仍 NO-GO，不是已支援功能或人類畫質盲評通過 |
| [ADR 0005](adr/0005-ai-object-removal.md)、[物件移除設計](object-removal-design.md)、[實作計畫](superpowers/plans/2026-10-20-ai-object-removal.md) | 尚未實作的選取／重建分離、顯示域合成、逐張資產保存與 T1–T5 驗收；不是目前功能或 iOS 支援承諾 |
| [裁切／遮罩／曲線計畫](cut-and-mask/plan.md) | 歷史分階段設計；裁切已整合，曲線核心不等於完整曲線功能，一般形狀遮罩不等於 RAW 語意遮罩 |
| [底片替換計畫](superpowers/plans/2026-10-17-film-color-simulation.md) | 底片 LUT 切換的實作歷史；現況與工具入口在 `film-looks.md`，不復活已刪除的 Recipe Lab 校正工具 |
| [RAW parity 計畫](superpowers/plans/2026-10-18-raw-pipeline-parity.md) | 有日期／來源 pin 的需求、實驗與驗收記錄；不把外部語料略過當成通過 |
| [驗證與知識回收計畫／記錄](superpowers/plans/2026-10-18-governed-verification-and-knowledge.md) | 本次範圍、來源處置、執行結果、治理未知項與導航取捨 |
| [原始產品計畫](plan.md) | 2026-10-15 的歷史規劃稿，不是目前的支援平台或功能承諾 |

## 導航取捨

採用 memory-palace 概念中有用的部分：固定入口、穩定位置、沿工作順序前進。沒有新增「宮殿」目錄、搬動原文件或複製領域事實；不宣稱這個文件地圖已改善人的記憶或學習。採用理由與檢查方式記錄在[本次稽核](superpowers/plans/2026-10-18-governed-verification-and-knowledge.md#navigation-decision)。
