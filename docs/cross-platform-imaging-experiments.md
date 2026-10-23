# 跨平台影像編解碼與色彩實驗手冊

整理日期：2026-10-23。涵蓋 2026-10-15 macOS 移植至 ProPhoto 匯出的實驗方法；這是既有證據的整理與重播指南，不是所有平台、相機或版本都已通過的宣告。現行產品契約見[影像讀取與匯出](architecture.md#48-engine影像讀取與批次匯出imaging--exporting)，各輪結果依下方有日期的來源判讀。

## 1. 先分清是哪一層不同

「編碼問題」可能指壓縮格式、RGB 傳遞曲線、色域，或畫面呈現。先固定比較邊界，不因 Windows 與 Mac 看起來不同就改 decoder 或 shader。

| 邊界 | 保留／比較的資料 | 能回答的問題 |
|---|---|---|
| 容器與感光解碼 | 原檔 SHA-256、尺寸、CFA、black／white level、解壓後整數樣本 | 是否在解包、位元範圍或幾何階段已經不同 |
| RAW 現像 | normalized、demosaic、highlight、binned、場景線性 Rec.2020；相機曲線與白平衡 | 差異是否來自現像／起始外觀，而不是匯出 |
| CPU／GPU 算圖 | 相同來源緩衝區、完整 Settings、seed、尺寸、色域與逐階段 float32 | 哪一個運算最先偏離；不能只看最後 JPEG |
| 輸出色彩 | 色域轉換、D50／D65 色適應、gamut map、TRC、RGBA16 量化 | 是否真的保留廣色域、gamma 是否與 ICC 相符 |
| 檔案編碼 | 編碼前 RGBA16、獨立解碼後 RGBA16、ICC、EXIF、alpha、壓縮參數 | renderer 已錯，還是 codec／metadata 改變了資料 |
| 顯示 | 匯出像素、螢幕／截圖 profile、實際原生預覽與縮圖 | 是像素本身不同，還是顯示色彩管理不同 |

桌面一般影像使用 Magick.NET，iOS 使用 Apple ImageIO／ColorSync；macOS 可直接測 Apple codec，但不代表桌面已切換後端，更不等於 iOS 裝置驗證。RAW 的 managed 解碼／現像與一般 JPEG／PNG 解碼也不能混為同一條路徑。

## 2. 每次實驗先固定的資料

1. **版本**：FilmBatch commit；未提交變更另留 source／binary digest。記錄 .NET SDK／runtime、OS／架構、Magick／LCMS／Dawn／WebGPUSharp 版本；外部 LightCraft、LibRaw、模型與 native library 各自釘選，不用模糊的「最新版」。
2. **實際執行者**：adapter 名稱、vendor、backend、driver、`IsSoftware`、是否進入應用程式 CPU fallback，以及每筆 `export_log.json` 的 `Renderer`。`IsSoftware` 是綁定分類，曾將 Basic Render Driver 標成 false，不能單獨證明硬體身分。D3D12/WARP 執行 WGSL，不等於 C# CPU 參考算圖器；`--software` 只是要求 fallback adapter，建立失敗才改用 C# CPU，不能只看旗標判定執行路徑。
3. **輸入與來源安全**：原檔 SHA-256、檔案大小、相機／格式、感光及已定向尺寸。以隔離副本開專案，輸出不得覆寫來源；結束後再次核對原檔與副本雜湊。
4. **完整設定快照**：`.filmbatch/project.json`、preset snapshot／對應的 preset library、resolved export log、所有覆寫、seed、RAW 選項、鏡頭／gain table／語意遮罩、crop／旋轉／翻轉、AI 模型 digest 與 provider。只知道滑桿名稱或「8 個覆寫」不足以重播。
5. **同一比較單位**：預覽長邊／實際算圖尺寸、全尺寸、取樣器、ROI 座標與座標系、輸出格式／位深／ICC／alpha。JPEG 另記品質、subsampling 與實際 DQT；品質數字是估計值時必須註明。
6. **先定判準**：每個入口的誤差單位、容差、alpha 要求、有限值／HDR／黑點與來源不變條件。RAW 收尾的 `2/65535`、其他效果的既有容差與外部 renderer 的 NRMS／顯示誤差是不同契約，不能互換。

先驗證兩組資料真的可比，再計算差異。不同 ICC 的 RGB 碼值不能直接相減當色差；不同預覽尺寸、crop 或 JPEG decoder 的結果也不能直接歸因於 shader。

## 3. 實驗順序與判讀規則

### 3.1 使用者回報 → 保存原證據 → 找第一個不同的邊界

- 接受使用者提供的失敗，不為「確認有沒有壞」重跑同一舊失敗；保存原圖、輸出、完整錯誤與當時版本。修正前後的受控實驗另列。
- 若只有截圖，先索取／尋找真正的匯出檔與設定；取得檔案後，用獨立解碼及有色彩管理的檢視確認差異是否已存在於像素。
- 依序比對感光樣本、場景 float、renderer 輸出 RGBA16、codec round-trip、原生畫面。先找最早的偏離，避免在下游補色、削波或加等待掩蓋上游問題。
- 一次只換一個因素：CPU／GPU、codec、ICC、RAW 選項、尺寸或版本。GPU parity 實驗固定解碼後輸入，避免 JPEG 解碼差異混入 shader 比較。
- JPEG 是有損編碼；相同 ICC、DQT 或局部 RGB 對應不是相同算圖輸入的證明。局部非單值映射既不能證明某個全域階調函數是根因，也不能單憑它排除所有全域階調原因。要分辨 renderer 與 encoder，仍需編碼前像素或同輸入的無損輸出。

### 3.2 最小數值反例與完整產品路徑都要有

先用黑、近零、中性灰、白、飽和原色、負值／HDR、奇數尺寸、透明邊緣及分帶尾列找邊界；從實拍最大誤差位置擷取少量像素，保留相機曲線／色度資料，再建立穩定回歸。修正後必須回到同一張全尺寸照片、實際 `Render16`、GUI／CLI 與檔案輸出，不能只交付獨立 finisher 通過。

數值收據至少包括 RGB 最大／平均差、超過門檻的分量數、最差位置／通道、alpha 最大差、輸入與輸出雜湊。全圖檢查與固定網格抽樣分列；抽樣找到廣色域像素不等於量測完整影像的廣色域占比。

### 3.3 診斷不能改寫原來的失敗

GPU 階段診斷保存原始 assertion，再用同裝置、production shader 插入觀察點；記錄 shader SHA-256、float32 值與十六進位位元。先確認 `cpuTraceMatchesOriginal`／`probeMatchesOriginal`，不相符時只能說診斷路徑不同。probe 自己失敗也必須保留原失敗，不能回傳成功或覆寫原輸出。

CPU 也不是絕對真值：Windows `MathF.Log2` 曾是第一個錯誤捨入點。用獨立高精度計算或另一個 CMS 核對，而非永遠讓 GPU 模仿 CPU。

### 3.4 精度修正的停止線

- 不放寬像素容差、不把失敗變成 GPU 不可用略過、不縮小案例，也不靠 CPU fallback 讓 GPU 測試變綠。
- 規格允許的未融合 `fma`／除法 ULP 擾動，可在 Metal 建立可攜性反例；它們是機制證據，不是實際 Windows driver 通過。
- 保留失敗前／修正後的同一回歸與收據；相似公式須依資料域、TRC 與已觀察邊界判斷，不能把 ProPhoto 零附近的修正直接套進不同的 sRGB 路徑。
- 錯誤傳播／callback／生命週期的修正，與真正造成裝置遺失的 shader 修正分開記錄。

## 4. Codec、ICC 與中繼資料的獨立核對

### 4.1 不對稱、含低位元與透明度的 fixture

使用奇數尺寸與不對稱色塊辨識 RGBA／BGRA、端序與方向；樣本包含非 257 倍數的 RGB16、半透明與全透明像素。對 PNG16／TIFF16 檢查真實 sample depth、所有 RGBA16 分量與低位元，不只檢查檔名或 `Depth` 屬性。

straight alpha 是保存契約；縮放內部可先乘 coverage、濾波後再除回。用「透明藍＋可見紅」驗證隱藏 RGB 不污染可見色，另查 alpha 精度。少色 PNG 不可因自動 palette／8-bit 最佳化而丟失 16-bit alpha。

codec 的輸入是**已經完成色彩轉換的像素**。嵌入 ICC 時不能再轉一次色域；保留／替換 metadata 也不得改變像素、方向或原有輸出 ICC。sRGB 與 ProPhoto 的 EXIF ColorSpace／Interop 要各自核對，不能把來源 Adobe RGB、舊尺寸或 Photoshop 圖層直接搬到新檔。

### 4.2 ICC 數值而非名稱

- 解析 profile 原色矩陣、PCS 白點及 TRC；以另一個 CMS 做黑／灰／白與飽和色塊轉換。profile 名稱或 byte-identical round-trip 只證明標籤保存，不證明像素與標籤相符。
- ProPhoto 原色／D50／純 gamma 1.8 的 generator、工具鏈版本、授權與 SHA-256 見 [prophoto-icc](../tools/prophoto-icc/README.md)。以固定工具鏈生成兩次並比較 bytes；不下載來源不明的廠商 profile。
- v2 單值 `curv` 的量化與 v4 parametric TRC 不同；對曲線直接計算 `ToLinear`，不能假設解析器的 scalar `GammaValue` 一定有值。
- CMS 比較使用 **ICC PCS D50 XYZ `(0.9642, 1, 0.8249)`**，而不是把另一個 xy 定義推導的白點混進 oracle。曾因此修正驗證器座標基底，沒有放寬 `0.0001` 誤差門檻或改產品矩陣。
- ImageMagick 讀 PNG 時可能正規化標準 sRGB profile；需要驗證原 iCCP bytes 時，明確使用 `PngReadDefines.PreserveiCCP = true`。另外以獨立 CMS 檢查外觀，不把不同 profile 的 RGB16 當同一編碼。

### 4.3 JPEG decoder 與 CMS 分開

iOS JPEG 偏紅調查先以 Pillow 參考對照 ImageIO，大圖 P3 極端色階出現約 `12.85/255` 差異。後來用另一個 iOS ImageIO 程式只取得 JPEG 解碼 RGB，再交主機 LittleCMS 轉換，隔離了 JPEG decoder 與 ICC 轉換；原容差保留，沒有替產品加入色偏補償。詳見 [iOS JPEG 色彩核對](building.md#ios-jpeg-色彩核對2026-10-22)。這些合成樣本未重現回報，不等於使用者未提供的原檔已修復。

## 5. 效能、原生執行與封裝如何避免誤判

- 同機、同版本組合、同資料與相同演算法，分開冷啟動與暖機。交錯執行 baseline／candidate，保存每輪樣本並報中位數；不要只取最快一次。
- CPU SIMD 曾將原始純量版與候選編入同一拋棄式程式，重用輸出及等量工作區；`Exp(Log(x)*k)` 實測退步就移除，不保留第二條近似路徑。
- `GC.GetTotalAllocatedBytes`、GC 後保留量、native 記憶體、GPU 資源與 peak RSS 各自命名；配置量下降不是峰值記憶體下降，也不是整條管線必然加速。
- GPU 計時明列是否含配置、上傳、compute、同步與讀回；原生 codec、RAW 解碼、demosaic、曲線擬合各自計時。預覽解碼提示的品質差異與全尺寸匯出分開。
- 真正啟動 `.app`／CLI／iOS app，觀察預覽、縮圖、編輯、復原、匯出與來源不變。Headless、編譯成功、啟動 Simulator 或 Mac 上跑共用 View，都不是目標平台完整通過。
- 封裝檢查 App 與 CLI 各自的 native 閉包、RID、Mach-O／PE 架構、最低 OS 與來源／授權；在不靠開發機 Homebrew／PATH 的情況啟動。cross-publish 不等於目標執行；Rosetta 不等於實體 Intel Mac。不能修改 Mach-O 最低版本宣告來假裝向下相容。

參考：[CPU SIMD 配對基準](adr/0003-gpu-stack-decision.md#本機-cpu-基準合併前-03761e1)、[舊獨立 RAW 收尾基準](adr/0003-gpu-stack-decision.md#本機-raw-收尾基準合併前-03761e1)、[效能修正的輸入與方法](superpowers/plans/2026-10-22-performance-correctness.md#method-and-inputs)、[發佈](building.md#發佈)、[Apple 技術實驗邊界](../tools/apple-mobile/README.md#實驗實際檢查什麼)。獨立 finisher 已由 `cfe4cf9` 移除，舊基準不能當現行 `Render16` 的端到端效能。

## 6. 可重跑入口

以下是現行入口，不是宣稱本文件整理時又執行了全部歷史實驗。使用符合 `global.json` 的 .NET；完整操作及平台前置條件見 [VERIFY.md](../VERIFY.md) 與[建置文件](building.md)。

### 6.1 分層回歸與 shader

```bash
# Codec／metadata／透明縮放／輸出色彩；GPU case 是否真的執行須看結果。
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~AppleImageCodecTests|FullyQualifiedName~AppleImagingTests|FullyQualifiedName~TiffMetadataTests|FullyQualifiedName~ForegroundAlphaTests|FullyQualifiedName~BatchExporterTests|FullyQualifiedName~ProPhoto"

# CPU／GPU 第一偏離點、分帶、錯誤歸屬與生命週期。
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~RawGpuDisplayTests|FullyQualifiedName~RawToneTests|FullyQualifiedName~GpuSpikeTests|FullyQualifiedName~GpuResourceLifetimeTests|FullyQualifiedName~GpuErrorScopeTests|FullyQualifiedName~GpuGaussianTests"

# 全部 include 展開後的產品 shader；不是單獨驗證 common 片段。
cargo install --locked --version 30.0.1 naga-cli
dotnet run build/CheckShaders.cs
```

Windows 完整回歸使用[既有序列化命令](building.md#windows-ci-測試排程)，保留 detailed console 與 TRX。實際 compute／readback 失敗不能改判沒有 GPU；外部 RAW／oracle、`rawinfo` 與平台限定略過各自統計。上列 filter 的 Windows-only 案例在 Mac 不會執行；`ToneCurvePipelineTests` 另會略過 `IsSoftware` 的 GPU 案例，不能把 WARP 上的整套 exit 0 當成曲線 GPU parity 已驗證。

感光／現像 oracle 的固定 commit、CC0 樣本 SHA-256、`FILMBATCH_RAW_SAMPLES`／`FILMBATCH_RAW_ORACLE` 與產生命令集中於 [raw-oracle](../tools/raw-oracle/README.md)。更新外部程式不自動更新舊 oracle；位元級與 renderer 級門檻分開。

### 6.2 同專案後端請求、色彩與 TIFF／PNG 對照

在 macOS 或 Windows Git Bash 使用下列配對方式。先將完整專案及對應 preset library 複製到隔離工作區，再設定四個 shell 變數；這些不是產品新增設定。`FILMBATCH_EXPERIMENT_PROJECT` 必須包含照片與 `.filmbatch/project.json`；`FILMBATCH_EXPERIMENT_PRESETS` 指向相符的 preset library；backend 在 Mac 為 `Metal`，Windows 為 `Dx12`。`FILMBATCH_EXPERIMENT_COLOR_SPACE` 在重播兩筆 Windows JPEG 案例時必須為 `srgb`；ProPhoto 功能另用 `prophoto`，不能替代 sRGB 路徑驗證。不可把來源工作專案當隔離副本。

```bash
set -e
: "${FILMBATCH_EXPERIMENT_PROJECT:?Set the isolated project copy}"
: "${FILMBATCH_EXPERIMENT_PRESETS:?Set the matching preset library copy}"
: "${FILMBATCH_EXPERIMENT_BACKEND:?Set Metal or Dx12 for the actual host}"
: "${FILMBATCH_EXPERIMENT_COLOR_SPACE:?Set srgb or prophoto for the path under investigation}"
work="$(mktemp -d)"
dotnet build src/FilmBatch.Cli/FilmBatch.Cli.csproj -c Release
for request in default software-request; do
  renderer_args=(--backend "$FILMBATCH_EXPERIMENT_BACKEND")
  if [ "$request" = software-request ]; then renderer_args+=(--software); fi
  for format in tiff png; do
    dotnet run --no-build --project src/FilmBatch.Cli -c Release -- \
      export "$FILMBATCH_EXPERIMENT_PROJECT" \
      --presets-folder "$FILMBATCH_EXPERIMENT_PRESETS" \
      --out "$work/$request-$format" --format "$format" \
      --color-space "$FILMBATCH_EXPERIMENT_COLOR_SPACE" "${renderer_args[@]}"
    cp "$FILMBATCH_EXPERIMENT_PROJECT/.filmbatch/export_log.json" \
      "$work/$request-$format.export_log.json"
  done
done
printf 'Evidence directory: %s\n' "$work"
```

這只產生兩種 adapter 請求的配對輸出，不會自動證明 CPU／GPU parity。逐份核對 log 是否為**預期的實際 renderer**：本輪 Mac 的軟體請求因無 fallback adapter 而使用 `CPU（參考實作） + RAW CPU`；若另一台主機成功取得軟體 GPU，該組仍執行 WGSL，不是 C# 參考。需要確定的 C# CPU 對照時，使用直接建立 `CpuFakeRenderer`、載入同一份 resolved settings 的隔離 Engine 驅動，不以 WARP 冒充。

獨立解碼全部 RGBA16，檢查同一 renderer 的 TIFF／PNG、確認身分後的 CPU／GPU 差異、ICC、alpha、方向／尺寸與前後來源雜湊。對 JPEG 問題保留同設定的 sRGB JPEG 與無損 PNG16／TIFF16，才可隔離有損編碼。對 ProPhoto 則另匯出 sRGB，經獨立 CMS 轉到相同空間，只在共同色域內比較外觀；JPEG／DNG＋ProPhoto 與未知色彩值應在產檔前回傳非零。輸出檔路徑從實際收據取得，不由 `--format tiff` 猜成 `.tiff`。

## 7. 歷次實驗如何形成目前的方法

以下是有日期來源的索引，不是重新執行舊版本的結果；完整數字、版本與被取代的條件留在連結文件中。

| 日期／範圍 | 比較方法與得到的教訓 | 可核對的來源與限制 |
|---|---|---|
| 2026-10-15：macOS 移植 | 真正發佈包執行上傳、調整、模糊與讀回；Apple M3／Metal 的 BGRA8 與 CPU 最大差 1/255。另啟動原生 GUI 檢查解碼、預覽及縮圖。 | [ADR 0001](adr/0001-webgpu-binding.md#spike-量測filmbatch-cli-spike上傳-16-bit--曝光亮度飽和--可分離高斯模糊--bgra8--讀回)。Rosetta 只證明翻譯執行；Intel 發佈後來停止，原生相依下限也另經重建更新。 |
| 2026-10-15：EXIF 與匯出復原 | 從真正 `BatchExporter` 驗證 Adobe RGB Interop、Photoshop 圖層、損毀 EXIF 與 JPEG APP1 大小；先保持已算出的像素，metadata 減量逐張留下警告。 | [歷史中的 `6b9ca2a`／`90e92da`／`5203033`](history.md#精選演進)、[現行安全規則](architecture.md#48-engine影像讀取與批次匯出imaging--exporting)；不是完整保留所有 MakerNote。 |
| 2026-10-17：RAW 移植與來源色彩 | 固定 CC0 SHA／上游 commit，分開感光與現像階段的位元比較、最終 NRMS／顯示誤差。曾以灰階 128 發現二次線性化，使約 0.216 變成 0.038。 | [RAW oracle 範圍](../tools/raw-oracle/README.md#比較範圍與已知偏差)、[色彩陷阱](architecture.md#48-engine影像讀取與批次匯出imaging--exporting)。現在的 managed JPEG／CMS 路徑不能沿用初版 Magick 解碼的限制描述。 |
| 2026-10-18：RAW 整合與 DNG／JXL | EXP-01–09 分開比較實際編輯、場景資料、Kelvin、遮罩、鏡頭、同模型 DNG 與 native JXL 閉包；相機 JPEG 擬合與標準 DNG profile 的外觀差異另外保留。 | [實驗設計](superpowers/plans/2026-10-18-raw-pipeline-parity.md#實驗設計exp)、[結果 ledger](superpowers/plans/2026-10-18-raw-pipeline-parity.md#state-ledgerspec-v1)。不改上游或容差來讓不同模型通過；回歸後來移到正式 `Render16`，刪除獨立 finisher。 |
| 2026-10-18：SIMD 與 GPU 浮點 | 同程序交錯配對純量／SIMD；以高光反例、十組 HDR 影像及捨入擾動定位 LUT／乘加精度。`Exp(Log(x)*k)` 因實測退步移除。 | [ADR 0003 精度](adr/0003-gpu-stack-decision.md#浮點精度)、[CPU 基準](adr/0003-gpu-stack-decision.md#本機-cpu-基準合併前-03761e1)。未融合 `fma` 是 Metal 模擬的可攜性反例，不是已確診的 Windows 根因。 |
| 2026-10-18–19：Windows 精度與裝置遺失 | 保存原始 assertion／原生錯誤，依序隔離上傳、讀回、sRGB 解碼與 callback。Windows CPU `MathF.Log2` 的第一個錯誤捨入由高精度計算核對；D3D12/WARP 的 scalar sRGB 路徑改為等價向量 `select` 後再跑正式管線。 | [完整診斷與 job 34／35／40／43](building.md#gpu-讀回與-raw-數值回歸)。job 40 是診斷成功，`c7f6072` 的 job 43 才是正式修正／封裝通過；不是硬體 RTX 驗證。 |
| 2026-10-19：Apple 原生技術入口 | 用奇數尺寸、非整齊低位元、方向與 ICC 檢查 ImageIO PNG16／TIFF16；真正產生／讀回大圖，分開配置量與 peak footprint。先 Mac 實跑，再另做 iOS build／Simulator。 | [技術入口紀錄](superpowers/plans/2026-10-19-apple-mobile-technical-entry.md#結果-ledger)、[Simulator 工具鏈邊界](../tools/apple-mobile/README.md#工具鏈與-xcode-271-實驗)。`NETSDK1147`、reference compile、Simulator 與實機各是不同證據。 |
| 2026-10-20：iOS 輸出與 metadata | 真正主要 App 匯出後，由 Pillow／imagecodecs／tifffile 獨立核對位深、低位元、ICC、EXIF、方向與來源雜湊；發現 PNG 讀取器會正規化 profile，故保留 iCCP 再查。 | [30 項獨立輸出檢查與原生操作](building.md#本輪實測2026-10-20)。保留 Apple stock sRGB 的 libpng 警告，不用警告消失冒充色彩正確；只屬當時 Simulator 版本。 |
| 2026-10-22：獨立 RAW／暗部參考 | 固定 LibRaw 版本比較尚未現像的感光樣本；darktable 使用隔離設定與保存的 XMP 重播，先核對基線，再比較暗部。合成場景的已知雜訊另計，實景紋理不充當 SNR。 | [感光解碼比較](shadow-recovery-survey.md#獨立感光解碼比較2026-10-22)、[高反差 RAW 方法](shadow-recovery-survey.md#實際比較方法)。一般檢視 PNG 只作呈現，不能當相同 renderer 的數值 oracle。 |
| 2026-10-22：iOS JPEG 偏紅 | 不對稱 ICC 色塊、P3／sRGB 大圖及灰卡；先分離 JPEG decoder，再以 LittleCMS 作色彩參考，保留最初約 12.85/255 的跨 decoder 差異與原容差。 | [iOS JPEG 色彩核對](building.md#ios-jpeg-色彩核對2026-10-22)。合成樣本未重現偏紅，未修改產品，也未宣稱使用者原檔已修復。 |
| 2026-10-22：透明縮放、解碼提示與效能 | 透明藍鄰接可見紅檢查 coverage；少色 PNG 核對真實 RGBA16；JPEG 預覽提示與全尺寸匯出分開。Sigma GainMap 逐列平行化保留相同插值及輸出雜湊。 | [效能／正確性結果](superpowers/plans/2026-10-22-performance-correctness.md#observed-results--2026-10-22)、[Sigma BF](sigma-bf-raw-performance.md)。配置量改善不當作全部解碼加速，預覽提示也不宣稱逐位元相同。 |
| 2026-10-22：Windows Gaussian 逾時 | 保留原大型／透明邊界案例、半徑及 10 秒單次期限，改為有界提交並在 uniform 重用前等待；再由真實 Windows CI 驗證。 | [`4a86715`／CI #110](superpowers/plans/2026-10-22-performance-correctness.md#remote-windows-ci-evidence-after-push)。12 項 Gaussian 與 WARP 發佈 smoke 通過，不延伸為 RTX 或後續版本通過。 |
| 2026-10-23：pipeline／LightCraft 再驗證 | 固定未修改基線、53 項影像／尺寸不變條件，保留縮圖卡在 4/18 與暗部階梯反例；只採用能重現的上游 decoder 差異。曲線再走保存／重開與原生匯出。 | [再驗證紀錄](superpowers/plans/2026-10-22-performance-correctness.md#pipeline-revalidation-2026-10-23)。v0.13.0 調查不自動更新舊 oracle；非零 Basic Shadows 外觀已變，重播必須固定版本。 |
| 2026-10-23：ProPhoto 與合併版 | Rec.2020 原色檢查先抓到 sRGB 色域損失；獨立 CMS、精確 gamma ICC、實拍最差像素與六面漸層抓出零附近相消。修正後完整 4672×7008 CPU／Metal 最大差 1/65535，同 renderer TIFF／PNG 及 GUI／CLI Metal 相同。 | [失敗與修正](superpowers/plans/2026-10-23-prophoto-output.md#precision-failures-caught-before-acceptance)、[合併後重跑](superpowers/plans/2026-10-23-prophoto-output.md#post-merge-verification--2026-10-23)。保留 2/65535 判準；Windows／iOS ProPhoto 未實跑，兩筆 Windows 原案仍未解。 |

## 8. 證據保存與未解問題

每輪保存版本／輸入／設定 manifest、實際命令與退出碼、原始失敗、修正後 TRX、數值收據、最差像素、原生截圖、profile／shader／binary digest 及未覆蓋平台。使用 JSON／TRX 解析實際 executed／passed／failed／skipped，不只看 exit 0，也不把沒有匹配測試的執行當通過。

本輪 xUnit TRX 的 `Counters.notExecuted` 為 0，但個別 `UnitTestResult` 有 320 筆 `outcome="NotExecuted"`；略過數須以個別結果及原因核對，不能只讀該摘要欄位。

`dist/`、`artifacts/` 與暫存工作區不進版本庫；本機證據封存不是 fresh clone 的必備檔案。版本化文件必須保留方法、判準、來源與限制，不能只留暫存路徑或對話連結。封存只收必要的驅動原始碼、設定、摘要與診斷；私人原始照片、完整匯出圖、模型權重與憑證不隨提交／推送公開。拋棄式 driver 在收據封存後移除，不讓產品依賴它。

截至 2026-10-23，Windows RAW 偏暗與 JPEG 黑塊／藍色斷階兩案都已確認差異在匯出像素中，但仍缺當次 resolved settings／export log、Windows build 與 matched-input Windows CPU／GPU 執行。相同 ICC／DQT 及 Mac CPU／Metal 通過只能縮小假設，不能判定 encoder 或 renderer 根因。ProPhoto 精度修正不算這兩案的修復；細節見[獨立 Windows 調查](superpowers/plans/2026-10-23-prophoto-output.md#separate-windows-investigations)。

目前文件中最後有完整正式 Windows CI 收據的是 `4a86715` 的 [#110](superpowers/plans/2026-10-22-performance-correctness.md#remote-windows-ci-evidence-after-push)，不能延伸到後續 Shadows／曲線、ProPhoto 或合併版 `5f93b59`。macOS ImageIO 的 ProPhoto 測試也不代替 iOS 裝置／Simulator；本輪兩者皆未執行。早期文件若只剩 dated 紀錄與公開 CI 連結、原本暫存檔已不在，應標為歷史證據，不能宣稱今天已重新解碼／重播。
