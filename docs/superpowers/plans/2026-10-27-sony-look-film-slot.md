# Sony Look 逐像素模擬：實作計畫

> 這是實作邊界，不是把 Sony 表放進產品的許可。實驗數字見 [主張查證](../../research/sony-creative-looks-survey.md) 第 9.2 節。

**Goal:** 只把已經驗證的契約鎖進測試：33³ 以內的 sRGB LUT 經現有底片槽後，能還原查表結果。不新增 Sony 底片、不嵌入由 Edit 或上游 profile 算出的 `.fblut`。

**Architecture:** 底片槽維持 `FilmLookProcessing.Pixel`（線性 → sRGB → `FilmLut` → 線性，強度 1、暖色 0、褪色 0）。產品清單仍只有 CC BY-SA 的 36 卷。

**Tech Stack:** .NET 10、xUnit、既有 `FilmLut`／`FilmLookProcessing`。

## 決策

| 主張 | 證據 | 未知 | 反證 | 決定 |
|---|---|---|---|---|
| 逐像素色彩能走現有底片槽 | 33³ Standard 表，27 個離節點色，最大通道差 0.022；槽輸出再編碼後與 `Sample` 差 0 | 實拍、Edit 視窗、空間處理 | 輸入是「sRGB 解碼當成相機碼」，不是相機 JPEG | **可以**用現有槽掛一張合法的 33³ 表 |
| 把本次 Standard 表放進 `looks.json` | 表由 Sony Look 參數算出 | 再散布權利未取得 | 根目錄 MIT 不涵蓋那些參數 | **不做** |

Falsifier：若 `FilmLookProcessing.Pixel` 在強度 1、暖色 0、褪色 0 時，不能把 LUT 節點還原成 sRGB，則「不必新寫管線」不成立，本計畫停止。

## 允許的變更

- [x] 新增測試 `FilmSlot_FullStrength_RoundTripsLutSample`，用合成 2³ LUT（不是 Sony 資料）。
- 不改 `looks.json`、不新增 `.fblut`、不改 UI、shader、參數 schema。

## 驗收

```bash
dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~FilmSlot_FullStrength_RoundTripsLutSample"
```

測試須斷言：純洋紅節點 `(1,0,1)` 經底片槽再編成 sRGB 後，等於該節點的 LUT 值；強度 0 時像素維持原線性值。

十二種 Look 的 33³ 離節點誤差見研究第 9.3 節。VV2 最大差 0.065、SH 0.080；升到 65³ 仍有 0.035 與 0.043。不因此放寬產品清單，也不把這些表放進 `Looks/`。
