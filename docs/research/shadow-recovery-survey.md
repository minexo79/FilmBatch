# RAW 暗部還原研究

查證與實跑：2026-10-22。本文前半記錄隔離原型與選型研究；該階段未改產品。後續授權已實作獨立、預設關閉的 `shadow_recovery`，見[功能落地](#場景線性暗部功能落地2026-10-22)；舊 `basic.shadows`、既有預設外觀及專案格式仍保留。研究原型、批次去背與產品暗部功能的證據不可互相替代。

2026-10-23 補註：使用者另提供 `basic.shadows` 暗部洗白案例，已修正其顯示域曲線；下文「保留舊外觀」描述的是 10 月 22 日獨立模組落地時的範圍，不代表保留後來確認的曲線缺陷。現行區分見[算圖契約](../architecture/engine.md#48-engine影像讀取與批次匯出imaging--exporting)。

## 結論與證據界線

優先研究「場景線性亮度導引的局部增益＋較強色度、較弱亮度降噪」，不要先換 RAW 解碼器，也不要把顯示域加亮當作感光資料還原。現行 RAW 已保留浮點高光；可直接重現的問題是 `BasicDisplay` 在顯示轉換後加常數，會把深黑抬灰、壓低相對對比。

沒有相同 RAW、設定與輸出的 Lightroom 對照，**不能證明 Lightroom 多還原一倍、解碼器優劣、真實感光元件 SNR 改善或產品級細節恢復**。本輪實跑 FilmBatch、Apple 一般檢視輸出及 darktable 5.6.2；RawTherapee 5.13 僅查原始碼，未執行。

## 現行管線與資訊損失

- 感光樣本在 `RawImage.Normalized` 減黑位、除以白位範圍後存 `float`，保留有限的負值與大於 1 的值；不是先變成 8-bit PNG 再調整。
- `RawSourceLoader.ApplyColour` 在相機色彩／白平衡轉換後裁除負值與非有限值，但不把正高光夾到 1。這個局部損失不能以「浮點就完全無損」帶過。
- RAW 場景影像為線性 Rec.2020；`CpuPipeline.DecodeLinear` 轉成工作線性 sRGB，GPU 使用 `rgba32float`。跨色域矩陣可再產生負值。本例 1200×802 工作緩衝區範圍為 −0.03150 至 1.53175，尚有超過顯示白的餘量。
- `RawDisplay.FinishLinear` 才做相機階調、色域映射及 sRGB 顯示轉換；最終輸出再量化到 16-bit。16-bit 是輸出精度，不代表仍保留場景動態範圍；拍攝已裁切的所有通道也不能由 tone mapping 憑空還原。
- `CpuPipeline.BasicDisplay`／`linear/basic_display.wgsl` 的暗部為 `RGB += shadows × lo(Y) × 0.35`，位置在 RAW 顯示轉換之後。以真正的 CPU 運算子輸入灰階 0、0.01、0.02、暗部 +100，得到 0.35、0.36、0.37。後兩階的 Michelson 對比由 0.3333 降為 0.01370；這是該運算子的診斷，不是整張 RAW 的感光量測。
- 現有 Zone 是場景域的一維階調曲線；DNG `CameraLocalTone` 是相機提供的 `ProfileGainTableMap`。兩者都不能直接等同任意照片的空間邊緣保留局部 tone mapping。

對應程式：[RAW 正規化](../../src/FilmBatch.Raw/RawImage.Develop.cs)、[色彩轉換](../../src/FilmBatch.Engine/Imaging/Raw/RawSourceLoader.cs)、[CPU 管線](../../src/FilmBatch.Engine/Rendering/CpuPipeline.cs)、[RAW 顯示](../../src/FilmBatch.Engine/Imaging/Raw/RawDisplay.cs)、[GPU 顯示運算子](../../src/FilmBatch.Gpu/Shaders/v1/linear/basic_display.wgsl)。

## 演算法選擇與降噪順序

候選在場景線性浮點 RGB 計算亮度 `Y`，只為導引使用 `log2(max(Y, ε) / 0.18)`。以導引濾波或雙邊濾波求基底 `B`，由基底決定受限的暗部增益 `g = 2^gainEV(B)`，對 RGB 共同相乘；不要逐通道獨立提亮。純黑保持為零，alpha 不變，細節層不以同一強度壓縮。

| 方法 | 可重用的機制 | 主要風險與成本 |
|---|---|---|
| 雙邊基底／細節分離 | 同時依空間距離及亮度差選鄰居，壓縮基底、保留細節 | 直接實作成本隨半徑增加；強壓縮仍可能暈光、梯度反轉或色偏，不是無暈光保證 |
| Guided filter | 以亮度導引局部線性模型，盒狀平均可做到線性像素成本 | 半徑、正則化與導引雜訊影響邊界；原論文也明載某些邊界仍會暈光 |
| 現行顯示域加法 | 不需鄰域，成本低，保留既有外觀契約 | 黑位上移、相對對比下降；轉換後已丟失的資訊無法補回 |

**降噪先於強增益與銳化**。使用同一個場景亮度／暗部權重引導兩者，但亮度與色度強度分開：亮度少量平滑以保留紋理，色度較強抑制彩斑，之後共同套用曝光增益。`C = RGB − Y` 是本原型的線性殘差分解，不是 CIELAB；`Y' + C'` 重組後才進既有顯示轉換。

現有 `DenoiseProcessor` 在來源準備階段執行；`DenoiseTiling.Mix` 已提供分離亮度／色度混合，RAW 與一般 RGB 的模型資料域不同。這些混合公式、`GaussianWeights`、SIMD 模糊、盒狀平均與 `Oklab` 轉換可重用，但**現有模型混合不是現成的免模型降噪器**，不能假稱新局部原型已接上產品。現有 ONNX 降噪亦不應被不知噪聲輪廓的強平滑取代。

本原型採固定啟發值，不是相機／ISO 校準：最大 +3 EV、基底導引半徑 24 代理像素、ε²=0.0225 EV²；雙邊直徑 31、range σ=1 EV、空間 σ=8 像素；亮度／色度混合為 0.35／0.8。實際鏡頭、ISO、細線與低頻彩斑仍須另外檢驗；不能把畫面更平滑當成細節更多。

## 可重現的高反差 RAW 比較

### 輸入與版本

- 公開 CC0 語料：Sony ILCE-7S [`DSC04125.ARW`](https://raw.pixls.us/data/Sony/ILCE-7S/DSC04125.ARW)，沿用 [sony.tsv](../../tests/FilmBatch.Raw.Tests/Corpus/sony.tsv) 的 `arw-sony-a7s-shade.arw`。
- 檔案 SHA-256：`7cc338a0abc8fdad32d61006f1f8f412297b93e040c7efba572eb2bd8f8e8be2`。登錄表感光陣列 4288×2848、FilmBatch 有效參考尺寸 4240×2832、實跑代理圖 1200×802，三者不要混寫。
- FilmBatch 原型由既有 Release 二進位檔匯出工作線性浮點緩衝區，保留組件 SHA-256；候選再進相同的 `RawDisplay.FinishLinear`，不是用外部任意 gamma 取代收尾。
- darktable 5.6.2 官方 arm64 DMG，SHA-256 `6ff88e58a2a59cb07b0a1502fea7205e68cd783380a33a3ce7bda68ec29def0c`；[release tag](https://github.com/darktable-org/darktable/releases/tag/release-5.6.2) 對應 commit `20891f6c6fa7be995e5bf9dff9d1ee5062051773`。隔離 config/cache、停用 OpenCL、輸出 1200×803 RGB16 sRGB TIFF。
- Python 3.11、NumPy 1.24.4、OpenCV 4.8.0；guided 方程獨立寫成隔離原型，未把 GPL 原始碼移入 FilmBatch。

### 實際比較方法

1. `sips` 產生一般檢視 PNG，只用來呈現「未額外調整的暗樹」；Apple 的解碼／階調不等同 FilmBatch 或 darktable，不能當受控數值基準。
2. FilmBatch 保存預設、現行暗部 +100、場景全域 +3 EV、雙邊局部、guided 局部及 guided＋降噪等分支。所有候選保留同一來源 alpha，工作緩衝區雜湊前後一致。
3. 用真正的現行 `BasicDisplay` 二分搜尋暗部量，讓同一固定樹葉矩形的平均線性輸出亮度匹配 guided＋降噪。結果約 +79.5448，不宣稱滑桿 +100 與 +3 EV 等價。
4. darktable 先實際匯出預設，再讀取該 TIFF 內的 11 個 XMP history 模組，保留其內建 +0.7 EV、白平衡、sigmoid 等設定。未改動的 XMP 重播輸出與預設**每個 RGB16 像素完全相同**；之後僅加入版本 5 的 `shadhi`，shadows=100、highlights=0、bilateral。輸出 TIFF 的 XMP 再核對模組已啟用及參數相符。
5. 另保存人工多邊形樹區與只替換該區的結果，區外像素逐一保持相同。這是 host 後合成的二值研究遮罩，不是 darktable GUI 畫筆或 AI 選取實測；邊界未羽化，不能當成品遮罩品質。
6. 獨立分析讀回已存 RAW 分支／TIFF，計算 256 個線性輸出亮度直方圖 bins、樹區亮度分布、log 亮度高頻 RMS 及輸出白位比例。樹區為各分支畫面正規化座標 x=0.20–0.55、y=0.75–0.95；darktable 與 FilmBatch 裁切／色彩／曲線不同，**不跨軟體排列數值品質名次**。

### 同一 FilmBatch 樹區的實測

亮度為逆 sRGB transfer 後的 0–1 相對線性輸出。log 高頻 RMS 為扣除 σ=2 像素 Gaussian 基底的 EV 變化，混有真實紋理與雜訊，**不是有效細節或 SNR 分數**。

| 分支 | 平均亮度 | P10 | P90 | log 高頻 RMS（EV） | 全圖任一通道到白位 |
|---|---:|---:|---:|---:|---:|
| 預設 | 0.02814 | 0.00373 | 0.07848 | 1.0875 | 0.828% |
| 現行暗部 +100 | 0.15116 | 0.13067 | 0.16689 | 0.1214 | 0.828% |
| 現行暗部亮度匹配（約 +79.54） | 0.111898 | 0.08640 | 0.12848 | 0.1848 | 0.828% |
| Guided＋降噪原型 | 0.111898 | 0.04960 | 0.17585 | 0.5548 | 0.828% |
| 雙邊局部原型 | 0.12605 | 0.04526 | 0.23107 | 0.6331 | 0.829% |
| 場景全域 +3 EV | 0.29745 | 0.06186 | 0.74391 | 0.8322 | 63.381% |

可觀察到：匹配平均亮度後，局部原型的亮暗分布較寬；現行加法把深暗像素集中到較高的底值。直接全域 +3 EV 大量壓到顯示白。這支持改進 tone mapping 的方向，不證明原型的每一條高頻都是可恢復的真實細節。開放檢視亦看見 NR 分支的紋理／色彩外觀改變，仍有過度平滑及色偏風險。

darktable 的 +100 分支確實提亮樹區，亦有強烈色彩變化；其預設與 FilmBatch 不同，沒有把它當成自動正確或 Lightroom 的替身。較新的 tone equalizer 是另一個受推薦的候選，但**本輪沒有執行該模組**。

### 已知雜訊、紋理與邊界控制

另以固定 seed 424242、線性加法雜訊 σ=0.00025 的乾淨合成場景比較有／無 NR。平坦暗區對相同乾淨提亮參考計算誤差，另有 12 像素週期紋理與高反差階梯，不使用實景樹葉變異估算感光雜訊。

| 指標 | 無 NR | 有 NR |
|---|---:|---:|
| 暗區亮度 RMSE | 0.0015011 | 0.0011147（降低 25.7%） |
| 暗區色度 RMSE | 0.0018939 | 0.0004621（降低 75.6%） |
| 相對乾淨參考的紋理振幅 | 99.49% | 88.41% |

此固定 guided 控制中，純黑最大值為 0、中性 RGB 最大通道差為 0，無 NR 理想階梯最小相鄰亮度差為 0，未見梯度反轉。這些是指定輸入／參數的結果，不是所有影像無暈光保證；NR 已犧牲約 11.6% 的指定紋理振幅。

單次 1200×802 原型耗時：雙邊局部約 0.181 s、guided 局部約 0.060 s、guided＋NR 約 0.165 s；只涵蓋 Python 濾波段，不含 RAW 解碼、顯示收尾與輸出。不是 warmed benchmark、全解析度、GPU 或分塊效能。

### 重播與保留資料

原始 RAW、工作緩衝區、XMP、RGB16 TIFF、PNG 對照、完整直方圖與 JSON 收據留在隔離研究目錄，不寫進使用者照片或產品預設。已執行的程式為 `engine-probe/Program.cs`、`prepare-darktable.py`、`prototype.py`、`analyze.py`；它們與收據另行封存，未把一次性驅動加入產品測試套件。

重播順序如下；`work` 是研究資料目錄、`dt` 是上述已驗 digest 的 `darktable-cli`。探針專案的參考路徑須指向當次建置的 Core／Engine／Raw／Gpu 組件，並記錄其 digest，不能以不同二進位檔冒充本輪原始數值。

```bash
# 先以本節 URL 取得並核對 RAW，保存各工具版本與組件 digest。
sips -s format png "$work/sony-a7s-shade.arw" --resampleHeightWidthMax 1200 --out "$work/ordinary-viewer.png"
"$dt" "$work/sony-a7s-shade.arw" "$work/darktable-default.tif" --width 1200 --height 1200 --apply-custom-presets false --hq true --core --configdir "$work/dt-config" --cachedir "$work/dt-cache" --disable-opencl --conf plugins/imageio/format/tiff/bpp=16
python3 "$work/prepare-darktable.py" "$work"
# baseline 和 shadows100 分別執行；不可沿用會自動寫入的使用者資料庫。
"$dt" "$work/sony-a7s-shade.arw" "$work/darktable-baseline.xmp" "$work/darktable-baseline-replay.tif" --width 1200 --height 1200 --apply-custom-presets false --hq true --core --configdir "$work/replay-config" --cachedir "$work/replay-cache" --disable-opencl --conf plugins/imageio/format/tiff/bpp=16
"$dt" "$work/sony-a7s-shade.arw" "$work/darktable-shadows100.xmp" "$work/darktable-shadows100.tif" --width 1200 --height 1200 --apply-custom-presets false --hq true --core --configdir "$work/shadow-config" --cachedir "$work/shadow-cache" --disable-opencl --conf plugins/imageio/format/tiff/bpp=16
dotnet run --project "$work/engine-probe/ShadowProbe.csproj" -c Release -- "$work"
python3 "$work/prototype.py" "$work"
dotnet run --project "$work/engine-probe/ShadowProbe.csproj" -c Release -- "$work" finish
python3 "$work/analyze.py" "$work"
```

## 官方與原始研究來源

截至查證日，版本固定如下；升級工具或改輸出設定就要重驗，不沿用本表作新版結果。

| 來源 | 實際讀到的內容與採用界線 |
|---|---|
| [LibRaw API 資料結構](https://www.libraw.org/docs/API-datastruct-eng.html) | RAW buffer 與 dcraw 後處理分開；`output_bps=16` 不等同線性，gamma、auto-bright 需各自指定。沒有由此推論 LibRaw 解碼優於現有移植 |
| [Durand／Dorsey 2002 原作者頁](https://people.csail.mit.edu/fredo/PUBLI/Siggraph2002/) | 讀摘要與實作 FAQ，非全文；log 亮度雙邊基底／細節分離與共同 RGB 比例重建，作者亦警告不受控輸入的比較限制 |
| [He／Sun／Tang ECCV 2010 全文](https://people.csail.mit.edu/kaiming/publications/eccv10guidedfilter.pdf) | 局部線性導引、盒狀平均與線性像素複雜度；第 5 節仍有暈光限制，不能寫成理論保證無 halo |
| [darktable 5.6 Shadows and Highlights](https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/shadows-and-highlights/) | Lab 顯示域處理；官方警告強設定的暈光與陰影色偏，建議 tone equalizer。不是所有成熟工具都必須用同一色域的證據 |
| [darktable 5.6 tone equalizer](https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/tone-equalizer/) | 線性 RGB、導引遮罩與分區曝光；可作局部方案參考，本輪只查文件 |
| [darktable 5.6 filmic RGB](https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/filmic-rgb/) | 場景至顯示映射與對比取捨；編碼動態範圍不等於感光動態範圍，已裁切資訊不能復原 |
| [darktable 5.6 profiled denoise](https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/denoise-profiled/) | 感光元件／ISO／亮度依賴的雜訊，亮度與色度分開控制；仍須量測紋理犧牲 |
| [darktable 固定版本 shadhi 原始碼](https://github.com/darktable-org/darktable/blob/20891f6c6fa7be995e5bf9dff9d1ee5062051773/src/iop/shadhi.c) | 僅用來核對比較用的版本 5、48-byte 參數 ABI；GPL-3.0-or-later，未移植到產品 |
| [RawTherapee 5.13 shadowsHighlights](https://github.com/RawTherapee/RawTherapee/blob/5.13/rtengine/ipshadowshighlights.cc) | 真正的 `guidedFilter(L, mask, mask, radius, 0.075, multiThread, 4)` 與 Lab／RGB 修正路徑；blob `fd2ab3db4627d55f870e916e81984d8117d05912`、release tree `1924179107995b01b73cdc7f4fe91e44fc8227a7`。GPL-3.0-or-later，只作證據，未複製程式碼 |

RawPedia 的 Shadows/Highlights、Noise Reduction 頁面本機讀取回 404；搜尋摘要沒有當成已讀全文。RawTherapee 相關實作判斷改由上述實際讀到的 5.13 原始碼支持。

## 可實作建議與尚未取得的驗收

[INFERENCE，研究階段的建議] 最小候選是獨立、預設不啟用的場景域局部暗部算子：放在工作線性緩衝區、曝光／白平衡／Zone 之後，顯示映射之前，分開控制亮度／色度降噪。不直接重定義 `basic.shadows`，避免改舊預設與專案外觀。這是落地前的設計理由；後續實作與驗收另列於[功能落地](#場景線性暗部功能落地2026-10-22)。

採用前必須驗證：更多相機／ISO 與夜景、亮暗細線／膚色／飽和色、已知噪聲與乾淨參考、不同 preview 尺寸和全圖的空間半徑一致、CPU／GPU 容差、tile halo／反射邊界、全解析度記憶體及取消、RAW／RGB 與透明 alpha、未啟用時的舊外觀不變。Lightroom／RawTherapee 實跑、人工盲評與高光重建另外取得，不能由本例補成 pass。

## 獨立感光解碼比較（2026-10-22）

使用外部 **LibRaw 0.21.4** 的 `open → unpack` 與當時既有的 `FilmBatch.Raw.dll` 比較；尚未執行 `dcraw_process`、白平衡、色彩矩陣或顯示曲線。三份 CC0 輸入均依既有 corpus SHA-256 核對，不使用內嵌 JPEG 或使用者私人照片作為感光資料。

| 輸入 | 全幅感光樣本 | 不同樣本 | 共同雙線性現像後的差異 |
|---|---:|---:|---|
| Sony A7S ARW2 | 12,212,224 | 1 | 裁切後 36,023,040 個 RGB 樣本中，5 個 G 樣本不同 |
| Olympus E-1 ORF | 5,158,784 | 0 | 14,745,600 個 RGB 樣本完全一致 |
| Canon 40D CR2 | 10,341,168 | 0 | 30,233,088 個 RGB 樣本完全一致 |

比較使用同一個完整有效區、逐點黑位、白位與 CFA，先對完整有效區做雙線性去馬賽克，再裁切；共同 NumPy 現像器在三份輸入均與實際 `Develop(Bilinear)` 輸出逐位元一致。這隔離的是**解包資料及其傳遞**，不是比較兩套軟體各自的去馬賽克、白平衡、profile 或起始外觀。

Sony 唯一不同點為感光座標 `(3622, 2157)`：相同 ARW2 code 與反向 LUT，LibRaw 保留 `16596`，現有 `Arw.CodeCurve` 將其裁至 `16383`，與釘選 LightCraft corpus 的 FNV `39441d1e6576dcbc` 相同。確實捨棄了 213 DN 的數值超出量；共同現像會傳遞至中心及四個 G 鄰點，不能說成完全沒有資訊差，也不能只因宣告白位或 14-bit 就證明物理飽和。是否包含可用高光細節仍未知；本例沒有暗部／中間調差異可支持換解碼器改善暗部。

Canon 感光資料完全相同，但兩邊黑白位估計不同：FilmBatch 量測遮蔽欄位黑位，LibRaw 採另一組遮蔽範圍／取整；白位分別為 `16383` 與相機表值 `16224`。沒有感光元件校準資料，不判定哪個估計較正確。共同現像刻意固定相同黑白位，不能將其一致誤寫成原生 level 估計一致。

**決定：保留現有解碼器及其相容契約，不新增 LibRaw 產品相依、不改裁切或黑白位估計。** 若要改 Sony 高光超出值或相機 level 估計，須另有可用高光／校準證據；這次暗部功能不藉此改舊照片外觀。三份樣本也不能推廣為所有相機／壓縮均相同，或當成 Lightroom 優劣證明。

重播資料包含 `libraw_sensor_dump.cpp`、指向既有 Release Raw 組件的 C# 探針、`common_develop.py`、原始感光 dump、輸入 digest 與逐點報告，保留在工作階段實驗資料，不成為 fresh clone 的產品相依。Canon 第一版 mean-black-only 比較器及 Sony 第一版先裁切再插值的邊界差異均另留舊報告；它們是比較器問題，沒有以修改產品去迎合。

## 場景線性暗部功能落地（2026-10-22）

使用者另行授權後，新增 `shadow_recovery` 模組與獨立 C#／WGSL 實作，**不是把上述 Python 原型或 GPL 上游程式碼移植進產品**。操作與可重跑回歸見[建置說明](../building.md#局部暗部還原)，數學／暫存／取消契約見[算圖架構](../architecture/engine.md#46-engine算圖管線rendering)。

- 預設關閉，暗部提亮 3 EV、局部範圍 2%、亮度降噪 35%、色度降噪 80%；開啟後在工作線性域處理，不改 `basic.shadows`。無有效調整時 CPU／GPU 直接略過。
- 共同增益保留黑點，降噪先於增益；覆蓋率加權的局部統計排除全透明隱藏色，alpha 不變。HDR 保留到原有顯示映射，不把最終 16-bit SDR 誤稱 HDR。
- 真正 Sony A7S RAW 全尺寸 4240×2832 在 Apple M3／Metal 實跑，CPU／GPU 16-bit 最大差 1，來源場景雜湊不變；全圖診斷為了另驗預覽使用 2 GiB 預算，**不是產品預設預覽預算或單次 GPU pass 的記憶體量測**。匯出仍走既有分段／不足時明示 CPU 的契約。
- 共用 Headless 桌面實際操作啟用／滑桿、整段拖曳復原／重做、重開、預設、貼上、取樣排除與 PNG 匯出；獨立 OpenCV 解碼核對 3,849,600 個 RGBA16 樣本零差異。這是共用控制項／引擎證據，不是原生 `NSWindow` 或 iOS 暗部實機驗收。
- 本文早期已知合成噪聲的 RMSE 降幅與紋理響應是**隔離原型**的量測，不可換標成產品演算法的畫質／SNR 數值。新產品案例驗證行為與精度，不推廣到其他相機、所有 ISO、保證無暈光或 Lightroom 優劣。

局部範圍按裁切後短邊縮放；降噪按來源像素足跡縮放，代理預覽可能比全圖弱。更多夜景／相機／ISO、人工盲評、跨平台 GPU 與實機效能仍須各自取得證據。三相機解碼比較的決定仍是上節的「保留現有 decoder」，不因暗部功能落地而改判。

## 整合記錄

自 [history.md](../history.md) 搬入，逐字保留，僅調整相對連結。

同日完成[暗部還原研究](shadow-recovery-survey.md)：以一張公開 Sony RAW 實跑現行管線、隔離浮點 guided／雙邊與亮度／色度降噪原型，並用真正 darktable 5.6.2 的可重播 XMP 比較。現行顯示域加法造成抬灰可重現，但沒有 Lightroom 對照或「解碼器多恢復一倍」的證據；合成雜訊降低與紋理犧牲分開量測，未把原型接進產品或改舊預設。

同日依後續授權把獨立 `shadow_recovery` 模組接入浮點線性 CPU／GPU 管線，預設關閉；保留舊 `basic.shadows` 與既有照片外觀，專案格式不變。採覆蓋率加權 guided 基底、增益前亮度／色度降噪與保持黑點的共同增益，沿用參數／復原／預設／匯出流程。真實 Sony A7S RAW、Metal、共用 Headless 控制項與 PNG 像素核對已實跑，完整結果與未驗證邊界見[操作說明](../building.md#局部暗部還原)。外部 LibRaw 的三感光樣本與共同現像比較不足以授權更換解碼器：Sony 只有一個高光超出值的既有相容裁切差，Canon／Olympus 解包一致；黑白位估計未有校準裁定。保留 decoder 與產品相依，不把研究中的降噪百分比或受控 Bilinear 一致推廣成 Lightroom 優劣。這是工作目錄實作記錄，未新增 commit、push 或發行包。
