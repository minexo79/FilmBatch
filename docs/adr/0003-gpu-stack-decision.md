# ADR 0003：GPU 運算堆疊定案與 CPU 向量化

- 狀態：**已採用**（M0 後評估，2026-10-17）
- 範圍：`src/FilmBatch.Gpu`、`src/FilmBatch.Engine/Rendering/CpuPipeline.cs`、`src/FilmBatch.Engine/Imaging/Raw/`、`src/FilmBatch.Engine/Exporting/BatchExporter.cs`

## 背景

M0 之後需要正式回答兩個問題：

1. GPU 效果管線是否維持現行堆疊，或改用 ILGPU／Vortice／ComputeSharp 等 C# 原生方案？
2. CPU 參考路徑（無 GPU、偵錯比對、超大影像降級用）如何在不多寫一套程式碼的前提下加速？

## 決策

1. **GPU 維持 WebGPUSharp 0.5.7（Dawn）。**沿用既有 WGSL、裝置、資源繫結與讀回模型，符合使用者「可行時延用同一技術」的要求。[ADR 0001](0001-webgpu-binding.md) 已記錄 DX12／Vulkan／Metal 的 spike；本次沒有足以支持遷移的已量測收益。
2. **CPU 使用 `Vector128<float>` 處理 RGBA 算術、模糊累積與量化。**不新增相依套件，不改公開 API。非線性的 sRGB 曲線與亮度 gamma 保留 `MathF.Pow`，理由見量測。
3. **RAW 解碼、demosaic 與線性 Rec.2020 顯影保留 CPU；編輯與晚期顯示收尾共用 CPU／Dawn 管線。**GPU 工作使用 `GpuSpikeRenderer` 擁有的裝置，不另設全域 GPU 單例；預覽與全解析度匯出都從浮點場景來源執行。
4. **Halide AOT 暫不採用，記錄為未來選項**，理由與重估觸發條件見下節。

## 候選比較

| 候選 | 證據與理由 | 結果 |
|---|---|---|
| WebGPUSharp＋Dawn | 使用者既有選擇；ADR 0001 已驗證 DX12／Vulkan／Metal | **維持** |
| ILGPU | 官方提供 [CPU／CUDA／OpenCL accelerator](https://ilgpu.net/docs/02-beginner/01-context-and-accelerators)，無 Metal 後端；macOS GPU 依賴已棄用的 [OpenCL](https://developer.apple.com/opencl)。需另行移植 kernel 與資源模型 | 不遷移 |
| ComputeSharp | [官方](https://github.com/Sergio0694/ComputeSharp)以 DirectX 12／Direct2D 為基礎，不能取代 macOS Metal 路徑 | 不採用 |
| Vortice.Windows | [Windows Direct3D 等繫結](https://github.com/amerkoleci/Vortice.Windows)，不是跨平台 Metal 方案 | 不採用 |
| Veldrid | [官方](https://github.com/mellinoe/veldrid)支援跨平台算圖、Metal 與 compute；問題是 shader／資源／管線遷移成本，不是缺少 compute。維護狀態以其 README 的公告為準 | 不遷移 |
| Silk.NET.WebGPU | ADR 0001 記錄當時版本的 DX12 後端問題，不泛推所有新版 | 不遷移 |
| `Vector128<float>`（本工作） | 僅 CPU 參考路徑加速，不改架構 | **採用** |
| Halide AOT | 見下節 | 未來選項 |

替代方案平台資訊查核日期：2026-10-17。未安裝或基準測試這些方案；本表不是跨函式庫效能排名。Apple 自 macOS 10.14 起棄用 OpenCL。

## CPU 向量化範圍

逐像素算術以四條 lane 處理 RGBA；模糊仍讀取鄰近像素，只把每個 tap 的四通道累積向量化。

| Pass | 實作 |
|---|---|
| `DecodeLinear` | `Vector64<ushort>` 只載入一個像素，再展寬、轉 float、正規化；RGB 曲線保留純量 `MathF.Pow`，alpha 直通 |
| `BasicLinear` | 曝光／色溫／色調常數向量逐像素乘 |
| `BasicDisplay` | 對比、亮暗部、飽和度與 structure 使用 RGB 向量算術；gamma 保留純量，亮度權重只算一次 |
| `ToneSplit` | 純量亮度／smoothstep 權重與向量混色 |
| `Detail` | 向量載入原值與模糊基底，RGB 縮放後還原原始 alpha |
| `Linear→sRGB` | 沿用純量曲線，向量載入與儲存保留 alpha |
| RGBA16 量化 | clamp → 乘 65535 → nearest-even round → 整數轉換 → unsigned narrow；值域先限制為 0–65535，不依賴 narrow 飽和 |
| `SeparableBlur` | 每個 tap 一次累積 RGBA，維持原取樣邊界、權重與加總順序 |

幾何／雙線性取樣、程序化雜訊、底片查表與 RAW demosaic 沒有在本次重寫；不由程式結構推論其效能上限。

## 精度與效能取捨

- 起初以 `Exp(Log(x)·k)` 代替向量 `Pow`，但同程序、相同輸入的配對基準顯示明顯退步，因此移除；沒有保留近似數學的第二套實作。
- M3 上改回 `MathF.Pow` 後，28 組小影像／設定與五組 1600×1067 影像均與原始純量版逐值一致；完整 65536 種 u16 RGBA 解碼與 18 組模糊案例也沒有觀察到浮點差異。這是已測輸入的結果，不是所有平台的逐位元保證。
- RGB-only 效果不得改變 alpha；單像素／奇數尺寸與完整 16-bit 色階列入回歸檢查。

## RAW 邊界（現行實作）

- CPU 保留：RAW 解碼、demosaic、線性 Rec.2020 顯影（`RawSourceLoader` 既有路徑）。
- GPU 使用 `Imaging/Raw/RawDisplayGpu.cs` 與 `Shaders/v1/io/raw_working_finish.wgsl`，在完整效果管線內直接處理工作紋理：完成相機／預設色調、高光、色域及顯示編碼；kernel 與 LUT 由算圖器快取。
- RAW 場景先轉未夾取線性 sRGB 工作域，收尾前轉回 Rec.2020。沒有獨立 RAW 收尾讀回；最後 RGBA16 匯出仍依裝置上限及 `MaxBandBytes` 分帶讀回，包含最後不足一帶的像素。
- 超限／GPU OOM 改用 CPU 並保留原因；shader 編譯、提交及讀回錯誤直接回報，不偽裝成功或靜默退回 CPU。
- `ImageLoader.LoadFull`／預覽載入僅建立場景來源；實際編輯入口為 `GpuSpikeRenderer.Render16`／`Render`（CPU 等效入口為 `CpuPipeline`）。舊的獨立 RAW finish API、資源與 shader 已移除，不保留只供測試使用的第二條管線。
- 每筆 RAW `export_log.json` 的 `Renderer` 分別標示效果算圖與 RAW 收尾，例如 `Apple M3 · Metal + RAW GPU`、`… + RAW CPU（原因）`；純 CPU 算圖器也標示 `RAW CPU`，混合批次不沿用前一張的狀態。

### 裁切與底片管線整合（2026-10-18）

合併提交 `703ff49` 將 `dev-cutandmask`（`de62003`）整合至 `feature/raw-port`，基準為 `03761e1`；後續場景線性 RAW 整合修訂收尾時機，現行邊界如下：

- **來源與效果分開。**來源選項在 RAW 載入／顯影時生效；白平衡與局部調整只重算畫面。裁切先在浮點工作域執行，RAW 收尾位於線性效果與顯示效果之間；不以固定模組數或陣列索引識別功能。
- **尺寸與來源資訊一起保留。**`GpuSpikeRenderer.Render16` 以 `OutWidth`／`OutHeight` 包裝裁切後像素，同時保留 `FilePath`、`RawOptions`、`RawInfo` 與 `SemanticMasks`。後兩者仍描述原始 RAW 座標，不改寫成裁切座標。
- **RAW 重載不取消裁切編輯。**預覽先用照片設定檢查是否需要重新顯影，再套用裁切工具的暫時全幅／方向設定與原圖比較；不把暫時預覽設定寫回照片。`EntriesChanged` 也會由 RAW／其他效果變更觸發，因此 `CropTool.Reload` 只在已保存的裁切幾何、啟用或比例設定改變時重置編輯；真正的裁切復原／貼上仍會重載。離開裁切分頁時才套用尚未提交的裁切。
- **底片整套切換。**採用 dev 的 `FilmLookProcessing`、LUT schema／素材、CPU／WGSL 與介面，維持既有 RAW 收尾與 SIMD 算術；不恢復已移除的 Recipe Lab 命令、工具或 `RecipesFolder`。CLI 保留 `RawCommands`，專案保留 RAW JSON／ICC 嵌入資源及測試組件存取權。
- **授權與文件跟隨實際內容。**保留 RAW 移植元件聲明與 RawTherapee LUT 的 CC BY-SA 歸屬；文件入口改指向 `film-looks.md`，不留下已移除的配方／校正文件連結。

驗證同時涵蓋 RAW＋裁切／旋轉匯出、CPU／Metal 像素對齊、LUT 路徑、裁切後回到全幅的工作區重建，以及裁切編輯中的 RAW 重載。合併前的基準量測不作為合併後整體匯出效能的宣稱。

### 場景線性驗收與鏡頭畫框

現行 GPU 迴歸走真正的 `Render16`，與 CPU 完整管線比較；不以只剩測試呼叫的獨立收尾證明產品行為。下方浮點精度段落保留早期數值調校的量測來源，現行驗收及限制另見 [RAW pipeline parity 計畫](../superpowers/plans/2026-10-18-raw-pipeline-parity.md)。

外部 Lensfun 的校正中心與半徑屬於未轉正的預設裁切畫框。產生 List3 時換算成整個 ActiveArea 的既有 warp／vignette 座標與多項式單位，不改共用取樣器的慣例；偏心裁切與完整畫框均有像素迴歸。


### 浮點精度

大圖 smoke 曾在高光接近色域邊界時觀察到 6／4／3 個 16-bit 色碼的差異；擴大到五個隨機種子後，又找出 5-code 的青色高光輸入。四組輸入均保留於 `RawGpuDisplayTests.Gpu_SaturatedHighlightNearGamutBoundary_MatchesCpu`，未放寬 2-code 容差。

Metal 的乘加重結合與 LUT 座標捨入、原生 `log2` 容許誤差會在色域壓縮後放大。WGSL 保留逐項 f32 捨入，以餘差校正除法，並以 mantissa 範圍縮減、atanh 級數與 high／low 餘差計算 LUT 用的 log2；CPU 曲線不變。固定為 0 的 uniform `RoundingMask` 是阻止編譯器跨捨入點重結合的數值屏障，不是可調參數，也不可直接替換為編譯期常數。此做法不依賴 Dawn 私有 ABI。

補償項本身也必須逐項保留捨入點；僅保護最終 high／low 相加仍不足。5-code 案例的第一個差異位於 LUT 用的 log2，放大後的紅通道走 sRGB 線性分支，因此不是末端 `pow` 的誤差。修正補償餘差的捨入屏障後，五個種子（147、1、42、2026、888）各跑預設與相機曲線，共十組 1600×1067 HDR 影像的最大 RGB 差均為 1／65535，alpha 完全一致。另以 1,048,576 個線性／對數分布輸入獨立檢查 sRGB 編碼，原 `pow` 路徑最大差為 1 code；未將它替換成未證明必要的 log2／exp2 路徑。

Windows CI（`windows-latest`；Dawn 在 Windows 預設 D3D12，提供的日誌未記錄實際介面卡）之後仍在同一組青色高光輸入得到 5／65535。WGSL 規格的 `fma` 精度「繼承自 `x * y + z`」，明文允許實作成乘法捨入後再加法；先前用 `fma` 取得的除法餘差與 log2 high／low 補償在未融合實作上會失效。在 Metal 以同一捨入屏障模擬未融合 `fma`，修正前的 shader 重現同一輸入的 CPU 1319／GPU 1324。現在完全不使用 `fma`：精確乘積餘差改用 Veltkamp 分割與 Dekker 乘積，只依賴規格保證正確捨入的 f32 加、減、乘；執行期除法（規格允許 2.5 ULP）再以餘差修正。LUT／色度座標、sRGB 曲線與 16-bit 量化的乘加也各自加上捨入屏障，不交由後端決定是否合併成 mad／fma。原生 Metal、模擬未融合 `fma`，以及每個執行期除法刻意偏移 +2／−2 ULP 四種條件下，四組邊界、色域、黑白／暗部、非平坦場景與十組 1600×1067 HDR 影像的最大 RGB 差均為 1／65535，alpha 完全一致。

後續 Windows trace 將同一紅通道差異的第一個錯誤捨入定位到 CPU `MathF.Log2`，並以高精度計算核對，見[診斷與正式 CI 結果](../building.md#gpu-讀回與-raw-數值回歸)。上段未融合 `fma` 是 Metal 模擬成立的可攜性風險，不是 Windows 當次根因的確診。

## Halide AOT（未來選項）

- Halide 為內嵌於 C++ 的影像管線語言，演算法與排程分離；目標含 x86／ARM 等 CPU、Linux／Windows／macOS 等系統，GPU 含 CUDA／OpenCL／Metal／DX12（[官網總覽](https://halide-lang.org/)）。
- AOT 流程為：產生器定義管線與排程 → 編出靜態函式庫與標頭（如 `lesson_10_halide.a`）→ 連結進執行檔；最終程式執行期不做 JIT、不相依 libHalide（[官方 AOT 教學](https://halide-lang.org/docs/tutorial/lesson_10_aot_compilation_generate.html)）。
- 代價：引入外部 codegen 工具鏈、C++ 產生器、多目標產物與額外建置步驟；排程需逐目標調校。本次不新增相依。
- 重估觸發：CPU 在 pass fusion 後仍有可證實瓶頸，或需要同一排程同時產出跨平台 SIMD 與 GPU 版本且收益可量測時，再評估。

## 本機 CPU 基準（合併前 `03761e1`）

Apple M3、.NET 10.0.13、Release、`DOTNET_TieredCompilation=0`。基準將原始純量 `CpuPipeline` 與修改版編入同一個拋棄式程式；影像 1600×1067、`Random(42)`，各暖機四次，交錯執行十二組，取中位數；輸出緩衝區重用，兩版工作區配置一致。

| 設定 | 純量中位數 | SIMD 中位數 | 加速比 |
|---|---:|---:|---:|
| 系統預設 | 20.45 ms | 18.70 ms | 1.09× |
| 基本調整 | 59.41 ms | 49.46 ms | 1.20× |
| 色調分離 | 27.26 ms | 24.48 ms | 1.11× |
| 清晰／銳化 | 84.14 ms | 70.22 ms | 1.20× |
| 三者合併 | 129.14 ms | 105.68 ms | 1.22× |

基本調整：曝光 0.7、色溫 30、色調 −20、亮度 10、對比 15、飽和度 20、亮部 −10、暗部 15、structure 20。色調分離：暗部色相 210／量 40、亮部色相 45／量 30、平衡 −15。清晰 30／銳化 25。這不是「全部模組開啟」基準，也沒有證明記憶體頻寬為瓶頸；數值不保證其他硬體的加速幅度。

## 本機 RAW 收尾基準（合併前 `03761e1`）

Apple M3／Metal、Release、`DOTNET_TieredCompilation=0`；1600×1067 線性 RGB，`rng = new Random(147)` 連續抽樣 `(float)rng.NextDouble() * 2.5f` 產生有限 HDR 值，前兩像素設為黑與 `1e-10f`；相機曲線／色度沿用 `RawGpuDisplayTests` 的合成 fixture。各暖機兩次，交錯量測六組，取中位數。以下為補償餘差屏障修正後的重新量測。

| 色調 | CPU 收尾 | GPU 收尾（含配置、上傳、compute、讀回） | 與 CPU 的最大 RGB 差 |
|---|---:|---:|---:|
| 預設 | 15.45 ms | 11.43 ms | 1／65535 |
| 相機曲線＋色度 | 22.14 ms | 8.80 ms | 1／65535 |

兩組 alpha 完全一致。這些時間不包含 RAW 解碼、demosaic 或外觀擬合，不代表整個 RAW 匯出的加速比。

此表量測的獨立 finisher 後由 `cfe4cf9` 移除；現行管線直接在效果紋理中收尾並由 `Render16` 讀回，不能用本表宣稱現在端到端匯出的加速比。


## 驗證界線

- 精度修正後 `dotnet test FilmBatch.sln -c Release`：978 通過、313 略過、0 失敗；其中 Core 281、Raw 613、App 84 通過。外部 `FILMBATCH_RAW_SAMPLES`／`FILMBATCH_RAW_ORACLE` 未設定，相關語料／對照測試未執行。
- RAW GPU 的 15 個案例在真實 GPU 上執行：預設／相機曲線、色度、高光與色域邊界、黑白與暗部 LUT 外插／上限、分帶尾端、取消、CPU 降級、全幅及裁切／旋轉 DNG 匯出的像素／中繼資料／裝置紀錄。
- `dotnet run build/CheckShaders.cs -- --naga <naga-30.0.1>`：30 個嵌入 WGSL 全數通過，包含新裁切、LUT 及既有 RAW 收尾。
- 實際 CLI 將生成的 256×128 DNG，以保存的 RAW／旋轉／裁切設定分別經 `--backend Metal` 與 `--software` 匯出 64×128 PNG，像素最大差 1／65535，alpha 與相機中繼資料一致；Metal 紀錄 `RAW GPU`，無軟體 GPU 時明確顯示原因後以 CPU 成功匯出。
- 實際桌面 DNG 操作曾重現「切換 RAW 選項丟失未套用裁切」。`RawOptionChange_PreservesPendingCropUntilApplyAndUndo` 在修正前失敗、修正後通過；真實 Metal 視窗亦確認 RAW 重載保留編輯框、原圖比較維持旋轉後全幅，離開裁切分頁後才得到 96×256 預覽。
- RAW＋裁切／旋轉、加 LUT，以及裁切後回到全幅的獨立 CPU／Metal smoke，最大差分別為 0／1／0 個 16-bit 色碼，alpha 完全一致；以 `DOTNET_EnableHWIntrinsic=0` 重跑仍得到相同結果。
- 完整測試包含 RAW 分帶預算不足時的 CPU 降級；故意損毀 WGSL 並確認編譯錯誤傳播的證據來自合併前 `03761e1`。
- 當時 Windows CI 重現邊界失敗，該輪只有 Metal 及規格允許的未融合 `fma`／±2 ULP 除法模擬證據。後續另修正 Windows CPU log2 與 D3D12 sRGB shader；`c7f6072` 的 Windows job 43 已完成正式管線及發佈 smoke，見[後續診斷與 CI 記錄](../building.md#gpu-讀回與-raw-數值回歸)。這不延伸為目前所有新功能或硬體 GPU 通過；Windows／Linux 效能未由本表量測，也未把 RAW 解碼、demosaic、外觀擬合說成已 GPU 化。
