# film-luts

把 RawTherapee Film Simulation Collection 的 Hald CLUT 轉成 FilmBatch 內附的 33³ `.fblut`。

- 來源：`http://rawtherapee.com/shared/HaldCLUT.zip`（2015-09-20 版，421,602,289 bytes）。
- 工具以 HTTP Range 只抓 `looks.json` 列出的 PNG（36 個，約 43 MB），不下載整包；伺服器若不支援 Range 或檔案大小不符會直接中止。
- 多數 PNG 為 level 12 Hald（1728×1728，144³），Kodak Ektar 100 為 level 16（4096×4096，256³）；工具依寬度自動判斷 level，以四面體內插重新取樣成 33³，存成 16-bit `.fblut`（每檔 215,634 bytes）。

## 用法

```bash
dotnet run --project tools/film-luts -- src/FilmBatch.Core/Film/Looks/looks.json src/FilmBatch.Core/Film/Looks
```

每轉一卷印出一行 `key ← source（level N）`。

## 授權

原始 LUT 與產生的 `.fblut` 皆為 CC BY-SA 4.0（作者 Pat David、Pavlov Dmitry、Michael Ezra），詳見 `src/FilmBatch.Core/Film/Looks/LICENSE.txt`。

## 更新底片清單

修改 `looks.json`（順序即 UI 順序）後重跑上面的指令並提交新的 `.fblut`，同時更新 `docs/film-looks.md`。
