# 底片風格（Film Look）

`film` 模組（中文「底片風格」、英文「Film Look」）沿用一個底片選擇與效果比例、暖色、褪色。內建外觀與既有使用者 LUT 使用 3D LUT；本工作區另實作有界 CLF 3.0 轉換序列匯入，格式與限制見文末。八色相帶修正獨立於底片。本次變更的跨平台、實拍與發佈 gate 尚未全部完成，不能把本文件視為發佈驗收。

本模組於 2026-10 取代舊的 Recipe Lab 底片配方模組（Sony 創意風格模擬、自訂配方 `.YML`、樣張校正工具已全部移除）。

## 目錄

1. [概念](#1-概念)
2. [來源與授權](#2-來源與授權)
3. [演算法](#3-演算法)
4. [色相帶](#4-色相帶)
5. [新增或更換底片](#5-新增或更換底片)
6. [RAW 與輸入色彩範圍](#6-raw-與輸入色彩範圍)
7. [舊專案相容](#7-舊專案相容)

---

## 1. 概念

| 概念 | FilmBatch 參數 | 範圍（預設） | 說明 |
|---|---|---|---|
| 底片（Film） | `film.look` | 36 卷之一（`kodak-portra-400`） | 一個 33³ 3D LUT |
| 效果比例（Strength） | `film.strength` | 0–100（100） | 結果與原圖在線性光中混合的比例 |
| 暖色（Warmth） | `film.warmth` | −100–100（0） | 正值偏暖（R↑ B↓），負值偏冷 |
| 褪色（Fade） | `film.fade` | 0–100（0） | 抬起黑點、壓低白點，像褪色的相紙 |
| HSL 色相修正 | `hsl.hue_{色相帶}_{h\|s\|l}` | 每個 −100–100（0） | 獨立模組；八個色相帶 × 色相／飽和／明度，共 24 個參數 |

`film.enabled` 控制底片；`hsl.enabled` 獨立控制 HSL，預設啟用但全零恆等。兩者都屬於選擇性貼上的 `PasteCategory.Color`；介面分組分別是 `ModuleGroup.Film` 與 `ModuleGroup.Color`，不改曲線本身的設定或渲染順序。

底片效果分頁（`FilmLookControl`／`FilmLookViewModel`）：

- **底片**：分組下拉（柯達／富士／愛克發與 Lomo／拍立得／黑白／創意）與 3 欄縮圖格。點縮圖即對所有選取中的照片設定 `film.enabled = true`、`film.look = key`（一筆復原紀錄）。
- **效果比例、暖色、褪色**：一般參數列。
- 縮圖以**目前焦點照片**的代理圖縮小到長邊 128 px 算圖，只在 film 模組展開時計算，走共用的低優先縮圖佇列（`FilmLookThumbnails`，鍵前綴 `film-look/look/`）。

基本參數 → **色彩**（曲線與 `HslControl`／`HslViewModel`）：

- **色相修正**：七個內建預設改為純文字下拉，不產生預覽圖或縮圖工作；目前值不符合任何預設時顯示「自訂」。
- **自訂色相**：選一個色相帶，以三條滑桿調整其色相／飽和／明度。預設一次寫入全部 24 個 HSL 值，多選照片一起套用，一次復原；不開啟底片、不改底片強度。
- 模組開關、覆寫標記、重設、複製貼上與逐張保存仍走既有 Inspector／ProjectService。白平衡／膚色／Zone 取樣暫停 HSL，避免創意調色影響取樣。

---

## 2. 來源與授權

| 項目 | 內容 |
|---|---|
| 來源 | RawTherapee Film Simulation Collection，`http://rawtherapee.com/shared/HaldCLUT.zip` |
| 版本 | 2015-09-20 版，421,602,289 bytes（工具以此大小驗證來源未變） |
| 原始格式 | 8-bit sRGB PNG Hald CLUT。內附的 36 卷中 35 卷為 level 12（1728×1728，144³），Kodak Ektar 100 為 level 16（4096×4096，256³） |
| 授權 | Creative Commons Attribution-ShareAlike 4.0 International（CC BY-SA 4.0，<https://creativecommons.org/licenses/by-sa/4.0/>） |
| 原作者 | Pat David、Pavlov Dmitry、Michael Ezra |
| 修改 | 以四面體內插重新取樣成 33³，存成 16-bit 的 `.fblut`（`tools/film-luts`） |
| 散布 | 內附的 `.fblut` 依 CC BY-SA 4.0 散布；FilmBatch 程式本身的授權不受影響 |

授權與修改說明的正式文字在 `src/FilmBatch.Core/Film/Looks/LICENSE.txt`，並列於 `THIRD-PARTY-NOTICES.txt`。

**商標聲明**：底片名稱（Kodak、Fuji、Agfa、Ilford、Lomography、Polaroid 等）僅用於說明 LUT 意圖模擬的底片，FilmBatch 與這些商標所有者無任何關係，亦未獲其背書。

### 內附底片

順序即介面順序（`src/FilmBatch.Core/Film/Looks/looks.json`），第一卷為預設值（`FilmLookLibrary.DefaultKey`）。

| 分組 | key | 名稱 | Hald 來源（zip 內 `HaldCLUT/` 之下） |
|---|---|---|---|
| kodak | `kodak-portra-400` | Kodak Portra 400 | `Color/Kodak/Kodak Portra 400 2.png` |
| kodak | `kodak-portra-160` | Kodak Portra 160 | `Color/Kodak/Kodak Portra 160 2.png` |
| kodak | `kodak-portra-400-vc` | Kodak Portra 400 VC | `Color/Kodak/Kodak Portra 400 VC 2.png` |
| kodak | `kodak-portra-800` | Kodak Portra 800 | `Color/Kodak/Kodak Portra 800 2.png` |
| kodak | `kodak-ektar-100` | Kodak Ektar 100 | `Color/Kodak/Kodak Ektar 100.png`（level 16） |
| kodak | `kodak-kodachrome-64` | Kodak Kodachrome 64 | `Color/Kodak/Kodak Kodachrome 64.png` |
| kodak | `kodak-ektachrome-100vs` | Kodak Ektachrome 100 VS | `Color/Kodak/Kodak Ektachrome 100 VS.png` |
| kodak | `kodak-elite-chrome-200` | Kodak Elite Chrome 200 | `Color/Kodak/Kodak Elite Chrome 200.png` |
| kodak | `kodak-elite-color-400` | Kodak Elite Color 400 | `Color/Kodak/Kodak Elite Color 400.png` |
| fuji | `fuji-velvia-50` | Fuji Velvia 50 | `Color/Fuji/Fuji Velvia 50.png` |
| fuji | `fuji-provia-100f` | Fuji Provia 100F | `Color/Fuji/Fuji Provia 100F.png` |
| fuji | `fuji-astia-100f` | Fuji Astia 100F | `Color/Fuji/Fuji Astia 100F.png` |
| fuji | `fuji-superia-400` | Fuji Superia 400 | `Color/Fuji/Fuji Superia 400 2.png` |
| fuji | `fuji-pro-400h` | Fuji Pro 400H | `Color/Fuji/Fuji 400H 2.png` |
| fuji | `fuji-pro-160c` | Fuji Pro 160C | `Color/Fuji/Fuji 160C 2.png` |
| fuji | `fuji-superia-reala-100` | Fuji Superia Reala 100 | `Color/Fuji/Fuji Superia Reala 100.png` |
| fuji | `fuji-superia-xtra-800` | Fuji Superia X-Tra 800 | `Color/Fuji/Fuji Superia X-Tra 800.png` |
| agfa-lomo | `agfa-vista-200` | Agfa Vista 200 | `Color/Agfa/Agfa Vista 200.png` |
| agfa-lomo | `agfa-ultra-100` | Agfa Ultra Color 100 | `Color/Agfa/Agfa Ultra Color 100.png` |
| agfa-lomo | `lomo-xpro-200` | Lomography X-Pro Slide 200 | `Color/Lomography/Lomography X-Pro Slide 200.png` |
| agfa-lomo | `lomo-redscale-100` | Lomography Redscale 100 | `Color/Lomography/Lomography Redscale 100.png` |
| polaroid | `polaroid-690` | Polaroid 690 | `Color/Polaroid/Polaroid 690 3.png` |
| polaroid | `polaroid-px70` | Polaroid PX-70 | `Color/Polaroid/Polaroid PX-70 3.png` |
| polaroid | `polaroid-polachrome` | Polaroid Polachrome | `Color/Polaroid/Polaroid Polachrome.png` |
| bw | `kodak-tri-x-400` | Kodak Tri-X 400 | `Black-and-White/Kodak/Kodak TRI-X 400 2.png` |
| bw | `kodak-tmax-100` | Kodak T-Max 100 | `Black-and-White/Kodak/Kodak T-Max 100.png` |
| bw | `ilford-hp5-400` | Ilford HP5 Plus 400 | `Black-and-White/Ilford/Ilford HP5 Plus 400.png` |
| bw | `ilford-delta-100` | Ilford Delta 100 | `Black-and-White/Ilford/Ilford Delta 100.png` |
| bw | `fuji-acros-100` | Fuji Neopan Acros 100 | `Black-and-White/Fuji/Fuji Neopan Acros 100.png` |
| bw | `agfa-apx-100` | Agfa APX 100 | `Black-and-White/Agfa/Agfa APX 100.png` |
| creative | `creative-teal-orange` | Teal & Orange | `Color/CreativePack-1/TealOrange.png` |
| creative | `creative-bleach-bypass` | Bleach Bypass | `Color/CreativePack-1/BleachBypass1.png` |
| creative | `creative-fall-colors` | Fall Colors | `Color/CreativePack-1/FallColors.png` |
| creative | `creative-soft-warming` | Soft Warming | `Color/CreativePack-1/SoftWarming.png` |
| creative | `creative-crisp-winter` | Crisp Winter | `Color/CreativePack-1/CrispWinter.png` |
| creative | `creative-moonlight` | Moonlight | `Color/CreativePack-1/Moonlight.png` |

---

## 3. 演算法

既有 LUT 在處理順序 ③ 底片階段（線性光）的第一步執行，之後依序執行獨立 HSL、古典版材。CPU 參考實作為 `src/FilmBatch.Engine/Rendering/FilmLookProcessing.cs`，GPU 實作為 `src/FilmBatch.Gpu/Shaders/v1/film/look.wgsl`；HSL 對應 `HslProcessing.cs` 與 `linear/hsl.wgsl`。CLF 序列則依匯入時宣告選擇工作線性或顯示編碼位置，只執行一次，見下方「CLF 色彩轉換」。

### 3.1 處理順序

對每個像素（輸入為線性光 `linear`）：

1. **編碼**：sRGB LUT（內建 36 卷與以 sRGB 匯入的表）：`display = linear_to_srgb(clamp(linear, 0, 1))`，LUT 的輸入是 sRGB 顯示編碼。
   ProPhoto LUT（以 ProPhoto 匯入的 `.cube`）：`p = clamp(srgb_to_prophoto(linear), 0, 1)`，`encoded = p^(1/1.8)`（D50、純 gamma 1.8，非 ROMM 分段曲線）。
2. **LUT**：sRGB 為 `look = srgb_to_linear(lut.Sample(display))`；ProPhoto 為 `look = prophoto_to_srgb(lut.Sample(encoded)^1.8)`，取樣值先 decode gamma 再乘回線性 sRGB 矩陣。皆為四面體內插（見 3.3）。
3. **暖色**：`w = warmth / 100`，在線性光中 `R × (1 + 0.12w)`、`B × (1 − 0.12w)`，G 不變。
4. **褪色**：`f = fade / 100`，在 sRGB 中 `x × (1 − 0.2f) + 0.1f`（黑點抬到 0.1f、白點壓到 1 − 0.1f），再轉回線性。
5. **混合**：`out = lerp(linear, look, strength / 100)`，在線性光中與**未截斷的原值**混合。
6. **獨立 HSL**：底片混合完成後，若 `hsl.enabled` 且任一色相帶非零，在 OKLCh 中修正色相、色度與明度（見第 4 節）。底片不執行時仍可執行這一步；alpha 不動。

| 常數 | 值 |
|---|---|
| 色相 ±100 | 色相角 ±30° |
| 飽和 ±100 | 色度 ×(1 ± 1)（−100 為完全去色） |
| 明度 ±100 | OKLab L ±0.12 |
| 色度閘門（色相與明度） | `smoothstep(0.02, 0.08, C)` |
| 暖色 ±100 | R ×(1 ± 0.12)、B ×(1 ∓ 0.12)（線性光） |
| 褪色 0–100 | sRGB `x × (1 − 0.2f) + 0.1f` |

**跳過規則**：模組關閉或 `film.strength == 0` 時不解析資產且完全略過底片 pass；未知 legacy key 由 `FilmLookLibrary.Resolve` 回傳 null 並略過。啟用的 `look-` 資產若缺檔、損毀或雜湊不符則回報失敗，不以原圖冒充成功。獨立 HSL 不受影響，關閉或 24 個值全零時完全略過。

**參數傳遞**：`ModuleParams` 的底片欄位以 `FilmLookUniforms.From` 組成 `Mix`（效果比例 0–1、暖色 −1–1、褪色 0–1、LUT 邊長）、`To0`…`To2`（線性 sRGB → ProPhoto 矩陣，`To0.w` 為 ProPhoto 旗標，sRGB LUT 時為 0）與 `From0`…`From2`（ProPhoto → sRGB），共 7 × `vec4f` = 112 bytes，對齊 WGSL `LookParams`。獨立 HSL 的 24 個值由 `HslUniforms` 組成 `Band0`…`Band7`（色相、飽和、明度 −1–1、0），共 8 × `vec4f` = 128 bytes，對齊 WGSL `HslParams`。OKLab 的 CPU 轉換集中於 `Oklab.cs`，HSL shader 使用相同係數。

### 3.2 `.fblut` 格式

| 位移 | 大小 | 內容 |
|---|---|---|
| 0 | 8 | magic `FBLUT1\0\0`（sRGB）或 `FBLUT2\0\0` |
| 8 | 4 | int32（little-endian）邊長 N（2–65；內附檔固定 33） |
| 12 | 0 或 1 | 僅 FBLUT2：`FilmLutColorSpace` 位元組（目前只有 1 = ProPhoto） |
| 12 或 13 | N³ × 3 × 2 | `ushort`（little-endian），0–65535 對應 0–1 |

資料順序為 `((b*N + g)*N + r)*3 + 通道`，也就是 r 變化最快，與 Hald CLUT 的像素順序相同（level L 的 Hald 影像為 L³ × L³ 像素，第 k 個像素 `k = r + g·L² + b·L⁴`）。33³ 檔案大小為 215,634 bytes（FBLUT2 多 1 位元組）。讀寫實作在 `FilmLutFile`：sRGB 永遠寫 FBLUT1，位元組與既有檔案完全相同，使用者 key（內容雜湊）不變；同一張表以 ProPhoto 匯入會得到 FBLUT2 與另一個 key。FBLUT2 的色彩空間位元組只接受 0／1，未知值或缺漏視為損毀而拒絕。記憶體表示為 `FilmLut`（`float[]`，0–1，`ColorSpace` 記錄節點編碼）。

### 3.3 四面體內插與 GPU 紋理排列

- **四面體內插**：把所在的格子依 `fr`、`fg`、`fb` 的大小關係切成 6 個四面體，只用 4 個頂點內插。中性灰沿對角線不會被相鄰的有色節點拉偏，與 DaVinci Resolve、darktable 的做法相同。輸入先夾到 0–1。
- **GPU 紋理**：`FilmLut.ToTextureRgba()` 產生寬 N²、高 N 的 RGBA32F 2D 紋理，`x = r + b·N`、`y = g`（每個藍色切片橫向排開）。shader 以 `textureLoad` 讀取節點、自行做四面體內插（不用硬體三線性）。`GpuEffectPipeline` 以 `FilmLut` 物件為 key 快取紋理（`FilmLookLibrary` 對同一 key 回傳同一個物件），`Dispose` 時一併釋放。

---

## 4. 色相帶

定義在 `src/FilmBatch.Core/Film/HueBands.cs`，正規設定屬於獨立的 `hsl` 模組；留在 `Film` namespace 不代表仍依附底片。

### 4.1 中心角與權重

八個色相帶位於 OKLCh 色相環（`atan2(b, a)`，度）：

| 色相帶 | 紅 red | 橙 orange | 黃 yellow | 綠 green | 青 aqua | 藍 blue | 紫 purple | 洋紅 magenta |
|---|---|---|---|---|---|---|---|---|
| 中心角 | 25° | 55° | 105° | 140° | 195° | 255° | 295° | 335° |

`HueBands.CentersDegrees` 與 `linear/hsl.wgsl` 的 `centers` 陣列必須同值。

- **權重**：每個色相帶的權重從自己的中心（1）線性降到相鄰中心（0），因此任一色相恰好落在兩個相鄰色相帶之間，**權重總和恆為 1**（`HueBandWeights_ArePartitionOfUnity` 測試）。實作為 `HslProcessing.BandWeight(hueDegrees, band)`，WGSL 為 `band_weight`。
- **修正量**：`dh、ds、dl = Σ 權重 × 該色相帶的（h, s, l）/ 100`。
  - 新色相 = 色相 + `dh × 30° × gate`
  - 新色度 = 色度 × `max(0, 1 + ds)`
  - 新 L = L + `dl × 0.12 × gate`
- **色度閘門**：`gate = smoothstep(0.02, 0.08, C)`。接近中性的像素（灰階、黑白底片的輸出）色相角不穩定，閘門讓色相與明度修正在低色度時漸弱為 0；飽和修正是乘法，本來就不影響中性色。色度 < 1e-5 時直接跳過。

參數 key：`hsl.hue_{red|orange|yellow|green|aqua|blue|purple|magenta}_{h|s|l}`（`HueBands.FlatKeys`，順序為色相帶 × (h, s, l)）。

### 4.2 內建預設

`HuePresets.All`，只列非零值，其餘為 0。套用預設會一次寫入全部 24 個值；`HuePresets.Find` 在目前 24 個值完全符合某預設時回傳該預設，否則介面顯示「自訂」。

| key | 名稱 | 非零值 |
|---|---|---|
| `none` | 無 | （全部 0） |
| `warm-skin` | 暖膚色 | red_h +10、orange_s +10、orange_l +8、yellow_s −10 |
| `clear-sky` | 清澈藍天 | blue_s +20、blue_l −10、aqua_h +10、aqua_s +10 |
| `lush-green` | 青翠綠意 | green_h +15、green_s +15、yellow_h +15 |
| `golden` | 金黃秋色 | orange_s +20、yellow_h −10、yellow_s +20、green_h −20、green_s −15 |
| `teal-orange` | 冷暖對比 | orange_s +20、aqua_h +5、aqua_s +20、blue_h −15、green_s −20 |
| `muted` | 低彩度 | 八個色相帶的 s 皆 −25 |

---

## 5. 新增或更換底片

1. **改清單**：編輯 `src/FilmBatch.Core/Film/Looks/looks.json`，加入 `key`（存進設定，發佈後不可再改）、`name`、`group`（必須是 `FilmLookLibrary.GroupsInOrder` 之一）、`source`（HaldCLUT.zip 內的路徑）。陣列順序即介面順序，第一筆必須是 `FilmLookLibrary.DefaultKey`。
2. **產生 LUT**：

   ```bash
   dotnet run --project tools/film-luts -- src/FilmBatch.Core/Film/Looks/looks.json src/FilmBatch.Core/Film/Looks
   ```

   工具以 HTTP Range 只抓清單列出的 PNG（不下載整包 402 MB）：先讀 zip 尾端的中央目錄，再逐檔抓取並解壓。伺服器必須回應 `206 Partial Content`，回 200（忽略 Range）或檔案大小與 421,602,289 bytes 不符都會中止。Hald level 由影像寬度判斷（寬 = L³），重新取樣成 `looks.json` 的 `size`（33）並寫成 `<key>.fblut`。
3. **測試**：`dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~FilmLookTests"`（`FilmLookTests` 檢查卷數、key 不重複、分組合法、每卷都有 33³ LUT、黑白底片輸出中性）。卷數改變時要更新測試中的 36。
4. **更新文件**：本文件「內附底片」表格；若改了分組，連同介面的分組名稱字串（`Strings.resx`／`Strings.en.resx`）。

`.fblut` 由 `EmbeddedResource`（`FilmBatch.Looks/*`）嵌入 Core；`FilmLookLibrary` 以 `JsonDocument` 手動解析 `looks.json`（Core 設為可修剪，不用反射式反序列化），LUT 在第一次使用時載入並快取。

---

## 6. RAW 與輸入色彩範圍

- FilmBatch 可直接開啟支援的 RAW，場景線性 Rec.2020 在效果管線中轉為未夾取的線性 sRGB 工作值；一般 TIFF／JPEG／PNG 仍由 Magick.NET 轉成 sRGB。兩條路徑共用藝術 LUT，但 RAW 曝光與相機白平衡發生在最終顯示收尾之前，不從已量化 SDR 還原。
- LUT 的輸入是 **sRGB 顯示編碼（0–1）**，因此線性光中大於 1 的亮部（例如基本調整提高曝光後）會在進入 LUT 前截斷；寬色域的負值同樣被截到 0。
- 效果比例混合時使用**未截斷的原值**，所以 `strength < 100` 時仍保留部分超出範圍的亮部資訊；`strength = 100` 時亮部以 LUT 的白點為上限。

---

## 7. 舊專案相容

原底片色相帶 `film.hue_*` 不丟棄：專案／設定剪貼簿 JSON 與預設 YAML 讀入時，`HslMigration` 將它們換成正規的 `hsl.hue_*`，並沿用原本的型別／範圍驗證；寫出只存新 key。若新舊 key 同時存在，新 key 優先，與來源順序無關。

出現舊色相欄位且沒有明確的 `hsl.enabled` 時，只沿用同一設定層明確提供的 `film.enabled`；稀疏覆寫不憑空補開關。原底片強度／暖色／褪色不再控制 HSL，且 HSL 改在底片混合之後執行，因此舊的「底片＋色相」組合不保證逐位元同外觀；沒有色相調整的底片不受這項拆分影響。

舊 Recipe Lab 模組的參數已移除。讀取舊專案（`.filmbatch/project.json`）與舊預設（YAML）時，下列 key 會被**靜默丟棄**，不報錯（`src/FilmBatch.Core/Params/RetiredParams.cs`，於 `SettingsJsonConverter` 與 `PresetFile` 兩個讀取入口處理）：

`film.recipe`、`film.style`、`film.saturation`、`film.contrast`、`film.sharpness`、`film.white_balance`、`film.amber_blue`、`film.green_magenta`、`film.effect`、`film.effect_option`、`film.exposure`、`film.dro`、`film.wb_reference`

- 保留的 `film.enabled` 與 `film.strength` 沿用原值。
- 舊檔沒有 `film.look`，因此原本 `film.enabled = true` 的照片會改套用預設底片 **Kodak Portra 400**（外觀會改變，需要時請重新挑選底片）。
- 其他未知的 key 仍會被拒絕（`RetiredParamsTests` 會檢查），避免打錯字的 key 被默默忽略。
- 內建範例預設已改用新 key：Portra Warm + Light Leak → `kodak-portra-400`、Ektar Vivid → `kodak-ektar-100`、Tri-X High Contrast → `kodak-tri-x-400`、Portra 800 Night（原 CineStill Night）→ `kodak-portra-800`、Dreamy Bokeh → `fuji-pro-400h`。已存在於使用者預設庫的範例預設不會被覆寫，其中舊的 film 欄位一樣在讀取時丟棄。


## 使用者匯入的 LUT

底片面板的「匯入 LUT」旁邊可選 `.cube` 的色彩空間（預設 sRGB）：

- `.cube`：必須是 3D 表，`DOMAIN_MIN/MAX` 與 `LUT_3D_INPUT_RANGE`（若有）為 0–1。含 `LUT_1D` 的檔會拒絕，避免丟掉 shaper。邊長大於 65 時縮成 65³。選 **ProPhoto RGB（gamma 1.8）** 時把節點值當成 ProPhoto D50／gamma 1.8 編碼（DxO、ACR 風格表的慣用編碼），檔案本身不標示色彩空間，用錯選項會得到錯誤顏色。
- Hald CLUT PNG：寬高都是 L³。iOS 上不讀 PNG。
- `.fblut`：直接用 `FilmLutFile` 讀取，色彩空間與邊長取決於檔案本身；放進使用者庫時 key 由內容雜湊決定，不沿用原檔名。

匯入的表存在 `AppPaths.UserLuts`（`user-` 加內容雜湊），`film.look` 可存這個 key。它們不是內建 36 卷，檔名只當顯示名稱，不代表廠商授權。

ProPhoto 匯入的表在清單格中多一行色彩空間標記（`ProPhoto RGB (gamma 1.8)`），sRGB 匯入與內建底片不標。顯示名稱一律是來源檔名，色彩空間不寫進名稱檔。

## CLF 色彩轉換

底片面板的同一匯入按鈕接受 `.clf` 與 `.fblook`。匯入 `.clf` 時顯示檔案描述，必須由使用者宣告執行位置（工作線性或顯示編碼）與輸入／輸出編碼（線性 sRGB、sRGB 或 ProPhoto RGB D50／gamma 1.8）；程式不從 descriptor 猜測色彩空間。`.fblook` 沿用封存的宣告，不再詢問；旁邊的 `.cube` 色彩空間選項不影響這兩種檔案。

這是 CLF 3.0 的受限集合，不宣稱完整 CLF／CTF 相容：只接受無 namespace 的 `ProcessList`、非空 id、嚴格 UTF-8 與 Matrix、Range、LUT1D、LUT3D；不支援 `.ctf`、外部 Reference、Log、Exponent、ASC_CDL、inverse LUT、IndexMap、half-domain 或未知節點／屬性。CLF 上限 128 MiB、64 個節點、XML 深度 16，正規化表格與參數合計 32 MiB；LUT1D 最多 65,536 點、LUT3D 最多 128³。LUT3D 預設 trilinear，另支援明示 tetrahedral。完整格式邊界見[研究契約](research/composable-color-transforms.md#32-clf-接受集合與數學)。

`.fblook` 是 16-byte 宣告 header 加原樣 CLF bytes；key 為 `look-` 加完整 envelope 的 SHA-256。檔名只作顯示名稱，使用者必須確認來源授權；不同宣告產生不同 key。匯入前先封存來源，確認後原子發布相同內容；切換照片／專案、取消或暫停不能把舊結果套用至新選取。已發布的共用資產不因之後取消而刪除。

兩種位置均以宣告編碼包裝序列，再在線性 sRGB 套用暖色／褪色與效果比例；alpha 保持不變。這不改寫既有 `.cube`、`.fblut`、Hald 或內建 LUT。選到缺失或損壞的 `look-` 時，啟用的預覽、縮圖及匯出應明確失敗；關閉或強度為零時不載入該資產。固定合成數值、實際 GPU 與尚未執行的跨平台／實拍驗收須分開記錄，見[實作計畫](superpowers/plans/2026-10-28-composable-color-transforms.md#4-verification-commands--release-gates)。
