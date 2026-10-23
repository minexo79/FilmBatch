# prophoto-icc

FilmBatch ProPhoto 輸出 ICC（`src/FilmBatch.Engine/Imaging/Raw/Profiles/prophoto-gamma18.icc`）的產生器。設定檔描述算圖管線的 ProPhoto 輸出編碼：ROMM 三原色、D50 白點、三通道皆為純 gamma 1.8（無 ROMM 線性段、無 flare）。

## 用法

```bash
cc -O2 -I/opt/homebrew/include -L/opt/homebrew/lib \
  -o /tmp/prophoto-gen tools/prophoto-icc/gen.c -llcms2
/tmp/prophoto-gen src/FilmBatch.Engine/Imaging/Raw/Profiles/prophoto-gamma18.icc
shasum -a 256 src/FilmBatch.Engine/Imaging/Raw/Profiles/prophoto-gamma18.icc
```

釘選工具鏈：Little CMS 2.17（MIT），Homebrew `little-cms2` 2.17；產生器會檢查標頭及執行期版本。固定描述／版權字串、建立日期 `2026-01-01 00:00:00` 及由固定內容計算的 MD5 profile ID。兩次獨立產生的位元組相同。輸出 784 bytes，SHA-256 `44ef82ec4a2b2539ca31862738f2b211f10ae1438a50e121ff58a1eea9a0253d`。

數值定義：三原色 `(0.734699,0.265301)`、`(0.159597,0.840403)`、`(0.036598,0.000105)`，白點 `(0.3457,0.3585)`，與 `ColorSpaces.ProPhoto` 一致。使用 ICC v4.3 `para` type 0、s15Fixed16 gamma `1.8000030517578125`，不採 v2 單值 `curv` 的低精度 `1.796875`。色料矩陣同樣以 s15Fixed16 儲存，經 Bradford 適應至 ICC PCS 白點 XYZ `(0.9642,1,0.8249)`；數值回歸以獨立純 gamma 1.8／Bradford 轉換檢查真正 CMS 解碼，PCS XYZ 絕對誤差上限 `0.0001`，不混用 xy 白點換算出的另一組 XYZ。這不是帶低亮度線性段或參考 flare 的嚴格 ROMM profile。

## 授權

ICC 位元組是 FilmBatch 以 LCMS 由公開色度數值自行算繪,未複製任何廠商設定檔,亦未使用下載的設定檔作為輸入。LCMS 為 MIT(作者 Marti Maria Saguer),詳見 `THIRD-PARTY-NOTICES.txt`「FilmBatch ProPhoto output profile」。產生器為建置期工具,不隨應用程式散布。
