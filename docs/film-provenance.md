# 底片配方資料來源（Recipe Lab）

FilmBatch 的底片色彩配方搬移自 [Recipe Lab](https://github.com/voxivoid/recipe-lab-sony-pmca)
（MIT，Copyright (c) 2026 André Domingues），授權聲明見 `THIRD-PARTY-NOTICES.txt`。

## 釘選來源

| 項目 | 內容 |
|---|---|
| 檔案 | `src/com/voxivoid/recipelab/Recipes.java`（配方表 `Recipes.ALL`）、`docs/DEVELOPMENT.md`（格式 1）、`docs/SAMPLES.md`（樣張說明） |
| Commit | `fd97eec77caf56bb2740a89b503e1037fdb24877`（2026-10-14，HEAD） |
| 轉出 | `src/FilmBatch.Core/Film/recipelab.json`（77 筆，key 與計畫書 7.4 表格一致；數值一字不改） |
| 自訂格式讀寫 | `src/FilmBatch.Core/Film/CustomRecipeFile.cs`（對應上游 `RecipeFormats` / `RecipeFormatV1` / `CustomRecipes`） |

跟進上游時：以同一 commit 重新轉出 `recipelab.json` 並更新此處與檔頭的 commit；
`key` 不變的配方維持相容（專案檔已記錄實際欄位值）。

## 數值核對方式

- 由 `Recipes.java` 直接解析 77 行 `new Recipe(...)`（群組、名稱、style、sat/con/sharp、wb、ab/gm、pe/ev/dro/sub），
  逐字對照計畫書 7.4 表格（key 推導規則見下）與抽查 22 組完整欄位；品牌計數 8/16/5/14/4/8/4/1/5/4/3/5。
- `key` 推導：英文名稱小寫，`./()+&` 轉空白，去除其他符號，空白/連字號合併為 `-`
 （例如 `Acros +R (red filter)` → `acros-r-red-filter`），結果與 7.4 表格 77 個 key 完全一致。
- 上游 `PE_KEYS[7]` 為 `rough-mono`（內建配方 pe=7 用它）；計畫書 7.2 的 `richtone-mono` 視為別名，
  讀入時相容、寫出統一為 `rough-mono`。

## 樣張校正狀態：單一場景擬合

- 上游 `docs/samples/` 有 77 張同場景相機直出樣張（900×600 JPEG；第 01 張為 `FACTORY (ST)`）。
  已使用正式算圖管線完成 `creative_styles.json` 的受約束擬合（計畫書 7.6）。
- `calibrated: true` 僅表示這次單一室內場景擬合已實際執行，不代表完整重建相機或真實底片。
  30 張訓練與 24 張保留驗證分開；Factory Standard 保持恆等，保留集不參與擬合。
- 9 張飽和超出 ±3 與 5 張舊 PP3 色彩矩陣樣張的聯集共 10 張，排除於擬合之外；
  指定色溫及照片效果樣張另列測量，不混入訓練或保留驗證集。
- 倉庫內含原始 JPEG 01、15、17 作為實際像素回歸，來源與 MIT 聲明見 `THIRD-PARTY-NOTICES.txt`。
  完整 77 張樣張需另行取得；來源 commit、座標下降方法、逐張結果、變差樣本與重現指令見
  [色彩校正紀錄](film-calibration.md)。
