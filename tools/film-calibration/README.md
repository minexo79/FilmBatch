# 底片校正工具（計畫書 7.6）

工具以樣張 01 為輸入，呼叫正式 `FilmParameters` 與 `CpuPipeline`，量測完整影像的平均 CIEDE2000 色差。只用訓練子集擬合；保留集不參與最後重擬合。Factory Standard 與零檔保持恆等，對比／飽和步進保持單調。

## 取得釘選樣張

需要 .NET 10 SDK。以下指令從儲存庫根目錄執行；樣張目錄必須包含上游的 77 張編號 JPEG，不需要額外 `files.txt`。

```sh
git clone --no-checkout https://github.com/voxivoid/recipe-lab-sony-pmca /tmp/recipe-lab-calibration
git -C /tmp/recipe-lab-calibration checkout fd97eec77caf56bb2740a89b503e1037fdb24877
```

## 重現擬合

`initial-styles.json` 保存校正前的工程初值，不會因產品更新而改變基線。輸出不會自動覆寫產品表。

```sh
dotnet run --project tools/film-calibration -c Release -- \
  --src tools/film-calibration/initial-styles.json \
  --samples /tmp/recipe-lab-calibration/docs/samples \
  --out artifacts/film-calibration
```

輸出：

- `creative_styles.fitted.json`：候選表與實測 train／held-out 平均色差。
- `per-sample.csv`：全部樣張的分類、初始與擬合後誤差；本次數值保存於 [`results.csv`](results.csv)。

初始表、演算法與樣張相同時，係數及逐張數值可重現；輸出日期隨執行日更新。正則化只參與優化，不混入報告的平均 ΔE00。

## 評估目前產品表

```sh
dotnet run --project tools/film-calibration -c Release -- \
  --src src/FilmBatch.Core/Film/creative_styles.json \
  --samples /tmp/recipe-lab-calibration/docs/samples \
  --out artifacts/film-assessment --assess

dotnet run --project tools/film-calibration -c Release -- \
  --samples /tmp/recipe-lab-calibration/docs/samples --selftest
```

`--assess` 評估 `--src` 指定的表，輸出 `baseline.csv`，不重新擬合。`--selftest` 檢查兩組 Sharma CIEDE2000 參照值及目前內嵌表的 Factory Standard 恆等行為。

結果、分類、回歸範圍及誤差限制以 [`docs/film-calibration.md`](../../docs/film-calibration.md) 為準。此工具不聲稱在缺少膚色、植物與天空的單一室內場景下完成相機重建。
