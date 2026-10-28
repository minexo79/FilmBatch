# 效能、RAW、AI 降噪與亮暗部 Survey

查核日期：2026-10-26。比較 LightCraft、RapidRAW、darktable 與 FilmBatch；正式版和開發主線分開釘選。本文記錄原始碼／官方文件研究與先前單次 smoke，不代表四產品同機畫質或速度排名。下列上游機制仍是研究建議；後續已採用的小修正另依現行契約與有日期的證據辨識。

本機後續工作分開保存：[先寫定的實驗規格](../superpowers/plans/2026-10-26-performance-local-experiments.md)、[當次實測與裁決](../evidence/performance-local-2026-10-26.md)。現行行為以[Engine](../architecture/engine.md)、[App](../architecture/app.md)、[GPU](../architecture/gpu.md)及 [VERIFY](../../VERIFY.md) 為準。

本機 E01–E07 與固定強度補測已完成，見上述實測文件。來源身分重用、AI 保留量與 f32 I/O 已有本機證據；RAW 大離群隨 AHD 路徑切換重現，但 kernel／高光內部分支根因未定。兩模型 CPU／CoreML 與 1／2／4 worker scope 均實跑；局部 guided 原型四組等亮度不可達且階梯有 overshoot，不採用為 Basic 替代。1600 px 亮暗部合併的 3 LSB 反例仍保留，不放寬精度或擴大既有 GPU 適用範圍。下文第 7 節仍保留前輪單次 smoke，不覆寫成這輪樣本。

後續追加實測已區分 ORT 算子分配與 host CPU 工作：RawNIND 49／49 交 CoreML，NAFNet 另有一個 ORT CPU `/Pad`；strict 控制分別可跑／明確拒絕，不能把 `CoreML + CPU` 標籤當實際 CPU 比例。已確認且修正同來源／同 mix 的 AI 命中成品身分重建：弱單槽重用讓七幀維持一份 GPU 來源、像素逐 byte 不變；未改模型、品質或推論同步。完整 CPU／fallback 盤點、實測邊界與後續排序見[追加實測 §11](../evidence/performance-local-2026-10-26.md#11-追加gpu-only-可行性cpu-路徑與-ai-命中後重用修復)；原始 E01–E07 不改寫成修正後結果。

暗部合併反例的後續修復：同一 CPU scene 的階段擷取確認 guided 二階矩相減會放大暗部捨入差；CPU／WGSL 對統計 guide 平移後再減回，不改理想算法或原容差。原八組與四組相鄰尺寸的真實 Metal `Render16` 皆 RGB≤1 LSB、alpha 相同；獨立 double 回歸先失敗後通過，詳見[數值修復 §12](../evidence/performance-local-2026-10-26.md#12-暗部-guided-矩抵消修復)。RAW AHD 隨後以 f32 捨入屏障、相同運算順序與餘差立方根修正 green／Lab／方向分歧：兩種 Bayer 合成回歸先失敗後通過，單張全尺寸 camera RGB 與同 CPU 收尾均逐位元相同，見[追加 §13](../evidence/performance-local-2026-10-26.md#13-raw-ahd-捨入與方向選擇修復)。不回改原研究反例，也不把本機結果當成全相機畫質證明。

## 1. 結論與方法

FilmBatch 最值得先量測的是重複處理、來源／暫存的存活期、CPU／GPU 往返，以及完整解析度工作的觸發條件；不是看到 CPU 用量就把所有迴圈搬上 GPU。三套上游的參考重點不同：LightCraft 的 RAW／相機處理與跨快取預算，RapidRAW 的原生 GPU 呈現與 shader 特化，darktable 的模組級快取、混合排程與局部階調。

研究依序核對版本、現行本機契約、上游實作、獨立 smoke，再提出候選；發行說明的效能宣稱不當成量測。上游未啟動 GUI，未用相同照片、設定、尺寸、模型、硬體跑四產品成對基準。無法判定速度／畫質冠軍、跨平台穩定性或特定相機的細節恢復率。

## 2. 版本、既有基準與取代關係

| 專案 | 查核時最新正式版 | 正式版程式 commit | 查閱開發主線 commit |
|---|---|---|---|
| LightCraft | [v0.14.0](https://github.com/storytold/lightcraft/releases/tag/v0.14.0)，10/25 發布 | `03c88c166c054c009db2a47187d59991f6232b85` | `bb08a697a41b2561a54f1b740245119503b24758` |
| RapidRAW | [v1.6.7](https://github.com/CyberTimon/RapidRAW/releases/tag/v1.6.7)，10/25 發布 | `a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8` | `d30a65a803856b1e2f452cff998e0de8f13e7d0f` |
| darktable | [5.6.2](https://github.com/darktable-org/darktable/releases/tag/release-5.6.2)，10/04 發布 | `20891f6c6fa7be995e5bf9dff9d1ee5062051773` | `27276e5a4087d531579e591fc80702597617822d`，5.7 開發線 |
| FilmBatch | 本機來源，非重新封裝發行版 | `890b13f4e610eb1498eb294fc6f7da18b76a7533` | 同左；研究開始時工作目錄乾淨 |

- 「最新」只對查核日期成立；下游程式結論依不可變 SHA 判讀。
- darktable 的 annotated tag 物件為 `908607e556359a4167a63515a3ee7a8393ba7284`；上表列 peeled 程式 commit，不混為同一物件。
- FilmBatch 舊 LightCraft 效能比較停在 v0.13.0／`23e181283cb448205d865e8cd288b56afe987986`。舊文的 AHD 單執行緒結論不能沿用；現行 AHD 已使用 Rayon 平行化。
- LightCraft v0.14.0→上述 main 的相關差異包括產品／raster 配置、DNG／opcode／preview／sparse／vendor 及匯出處理；本輪查閱的 AHD、GPU 效果及局部 tone 核心未因這段差異改變。不能把 main 所有變更都視為 v0.14.0 已發布。
- RapidRAW 正式版→上述 main 的 `git diff --name-status` 只有 `README.md`，因此本輪處理實作也適用 v1.6.7。
- darktable 5.6.2 已含可選 neural restore，不是只有 5.7 開發版才有；編譯／設定／模型是否可用仍是不同條件。
- FilmBatch [RAW oracle](../../tools/raw-oracle/README.md) 生成器仍釘 `f34c7a8a89e0d1bade0a6889185f40acecc84a9f`；此次 Survey 不更新 oracle，也不更換既有數值預期。
- 歷史 ADR 中「RAW 只走 CPU」的舊描述，已被目前 App 範圍內 RAW GPU 政策取代；直接載入、CLI 與匯出仍保持 CPU RAW，不能一概改寫。

## 3. RAW、CPU／GPU 與畫面交付

| 主題 | LightCraft | RapidRAW | darktable | FilmBatch |
|---|---|---|---|---|
| RAW 容器／壓縮解碼 | 自有 Rust RAW 引擎，CPU | Rust `rawler`；Apple RAW 另為選用路徑 | RawSpeed，部分格式另由 LibRaw 等處理 | 自有 managed RAW 引擎，源自 LightCraft 移植 |
| 傳統去馬賽克 | CPU AHD 等，Rayon 平行化 | CPU `rawler`；快速模式可選 Speed | 依演算法提供 CPU／OpenCL | App 可用 GPU Bayer AHD；直接載入／匯出為 CPU RAW |
| 小尺寸 RAW | 優先從 mosaic 分箱 | 快速現像／代理圖與快取 | Mipmap、縮放 ROI、分級來源 | Bayer 分箱；普通縮圖可用內嵌 JPEG，RAW＋AI／遮罩要求現像來源 |
| 效果 GPU | wgpu／WGSL | wgpu／WGSL | OpenCL，逐模組選擇 | Dawn／WebGPUSharp／WGSL |
| 放大檢視 | Main＋Region，可見區域算圖 | ROI／tile、原生顯示變換 | ROI 反推各模組輸入，必要時 tiling | Main＋Window、halo、最多四塊窗口 |
| 畫面交付 | GPU 讀回 CPU，再交 egui 紋理 | Windows／macOS 預設可用原生 WGPU surface | Pixelpipe 最後仍需 host／顯示交付 | GPU 讀回 CPU，再複製至 Avalonia bitmap |
| CPU 控制 | Rayon 工作池／資料平行 | Rayon、背景工作、記憶體入場 | OpenMP／SIMD、背景佇列與 CPU／GPU 排程 | RAW 每迴圈預設半數邏輯核心，背景再降低／配速 |
| 昂貴階段重用 | 來源／預覽／處理資料快取 | 來源／處理快取與 shader 特化 | 模組及上游狀態雜湊，可重用未失效前綴 | 已重用來源、LUT、工作區；不是完整模組前綴快取 |
| 匯出 | 受控平行與獨立入場預算 | 估計影像成本限制入場 | 獨立匯出工作及 CPU／OpenCL 政策 | 逐張完整解碼＋完整 Render16，再縮至輸出尺寸 |

### 3.1 FilmBatch 已 GPU 化，但沒有端到端 GPU 常駐

目前 App 路徑可包含 CPU 解碼／正規化→GPU AHD→CPU 讀回與高光重建→GPU 相機色彩→CPU 讀回→CPU 轉工作色域 RGBA→效果 GPU 上傳→效果／讀回→Avalonia bitmap。AHD 與色彩各有獨立停用／錯誤記錄，色彩逐帶成功才提交，失敗後 CPU 僅處理未提交後綴。

`RawGpuDemosaic` 的 GPU 範圍受 App 軟體模式、後端、記憶體偏好控制；Bayer AHD 有至少 4M pixels 等條件。AHD 帶高 256、色彩帶高 512。這不是把整個 RAW decoder 移到 GPU。`--software` 的效果 adapter 政策與 AI provider 也不是同一設定。

效果 GPU 的四張 RGBA32f 工作紋理合計 64 B/pixel，另有線性來源 16 B/pixel、帶狀編碼／讀回、素材與遮罩。RAW 來源上傳前的 `CpuPipeline.DecodeLinear` 仍建立 CPU 工作 RGBA。這些是可由尺寸推導的資源量，不等同實測程序峰值；整合式 GPU 的 host／device 記憶體也不能簡單相加宣稱實體用量。

匯出刻意不使用預覽記憶體預算作為 CPU fallback 判準；由裝置硬上限及實際配置錯誤決定。不能把預覽策略或縮小匯出後的尺寸當成完整工作成本。

### 3.2 直接呈現應參考 RapidRAW，而非誤稱 LightCraft 零拷貝

LightCraft `finish_and_read()` 仍建 staging、等待／map／複製，egui 之後重新上傳。RapidRAW 原生呈現路徑可綁定效果輸出紋理並呈現 surface，省去整張顯示畫面的 image readback；Windows／macOS 的預設值與 Linux／Android 不同。

這不代表整個應用程式零拷貝：RapidRAW 的直方圖／波形分析仍可非同步 `copy_texture_to_buffer`／map 預覽影像，匯出編碼另有 host 需求。FilmBatch 若採用，需證明 Avalonia／Dawn 互通、色彩／alpha、resize、裝置生命週期及整體收益；不能只看一個 kernel 時間。

### 3.3 已有 ROI，不應再造第二套

FilmBatch Window 仍先 `Full.ScaledTo()` 建畫框來源，以 1024 向上級距與 250 ms 等待減少重建；Main 有 64 px 級距。GPU 從同一 Full 紋理取樣／Mip 是不同候選，會碰到效果半徑、顆粒座標／足跡、鏡頭與遮罩映射、參考尺寸、halo 及窗口／全圖裁切等價性。向上量化可能多處理像素，例如 3201→4096 約增 63.74% 畫框像素；不能只移除等待便宣稱更快且外觀不變。

## 4. 記憶體、快取與排程

| 專案 | 可借鏡機制 | 必須保留的限制 |
|---|---|---|
| LightCraft | 同一預算分配：引擎快取約 1/2、進行中解碼 1/4、GPU buffer pool 1/8；及早釋放輸入 | 前景可緊急准入，匯出另可允許到約 2 倍基本預算；不是程序 RAM 硬上限 |
| RapidRAW | 進行中解碼 single-flight、估計成本租約、非使用中 float 影像轉 f16、神經結果磁碟快取 | f32→f16 有損；展回 f32 不會恢復遺失尾數；入場估計不保證不 OOM |
| darktable | Mipmap／磁碟快取、模組前綴快取、RAM／VRAM 配額、模組 tiling 能力與工作量估計 | tile 增加 halo 重算與傳輸；不是越小越快，也非所有模組／模式都有 OpenCL |
| FilmBatch | single-flight、背景單槽幫浦、前景優先、來源 LRU、畫面 byte LRU、解碼入場與 GPU 預算 | 估計／保留量分散；部分快取按張數，並非全部沒有 byte accounting |

FilmBatch 的具體界線：

- `DecodeBudget` 使用縮圖 64 MiB、Preview 400 MiB、Full 1700 MiB 固定估計；以實體可用記憶體的 1/4、最多 1536 MiB 為基礎，再保守分配 1/4 給入場。前景記帳但不等待；單一背景工作可以超過估計上限。這些不是相機尺寸校準，也不是程序硬限制。
- `ImageCache` 保留最多 8 個 Preview、64 個縮圖來源；`ViewRenderCache` 已有精確 BGRA byte accounting、256 MiB／8 entries LRU。效果 GPU 另有來源／工作區預算，不能把「沒有共同總預算」寫成「完全沒有預算」。
- `DecodeLease` 只在仍有使用者的期間共用 RAW bytes／樣本／色彩擬合；最後一位離開即釋放。每次新租約仍讀檔並 SHA-256，避免只憑時間戳／長度誤認內容。
- Full／窗口真正釋放後，仍在背景呼叫 blocking Aggressive GC。背景呼叫不代表不會暫停 UI；本次不先改 GC 策略。
- 背景 CPU 百分比是合作式軟目標；RAW 的半核心／背景比例是每迴圈範圍，不涵蓋所有 Engine／原生迴圈，也不是程序級 CPU 硬上限。
- 已有前景取代等待入場的同鍵背景解碼、取消／世代圍欄、latest-wins、縮圖有界幫浦；不是待新增的能力。

優先延伸既有 byte accounting，量測主要影像、暫存與快取存活期；不先建立泛用資源管理框架。配置量、GC 後存活量、native／GPU 配置估計及 sampled RSS 必須分列。

## 5. AI 降噪：模型、資料域、分塊與快取

| 專案 | 模型／介入階段 | 推論執行 | 神經結果及強度調整 |
|---|---|---|---|
| LightCraft | RawNIND Bayer，四平面 mosaic；聯合降噪／去馬賽克 | 自有 Rust CPU runner／WGSL neural runner | 相機 RGB `.lcdn`，半精度壓縮；再接高光重建／相機色彩 |
| RapidRAW | `nind_denoise_utnet_684.onnx`，已去馬賽克三通道 RGB；RAW 路徑線性 RGB | ONNX Runtime，Windows DirectML；macOS 降噪可嘗試 CoreML | `.rrdn` 半精度結果；amount／detail 混合快取 |
| darktable | Bayer RawNIND 聯合降噪；非 Bayer 線性 Rec.2020；另有後段 RGB 模型 | ONNX Runtime，與效果 OpenCL 分離 | patch 預覽，獨立完整工作產生 DNG／TIFF 再匯入 |
| FilmBatch | RAW 用 RawNIND Linear：已現像線性 Rec.2020；一般 sRGB 用 NAFNet-SIDD | ONNX Runtime，Auto 為 CoreML＋CPU／DirectML＋CPU | RAM 各一筆 full／thumbnail；總量／亮度／色彩在推論後混合 |

### 5.1 分塊和接縫不是同一演算法

- **LightCraft**：Bayer 模型輸入 `[R,G1,G2,B]` 的 512 cell tile，對應 1024 px RGB 輸出；overlap 64 cells。平滑權重融合，逐 tile／通道校正尺度（nominal 1e6、限制約 ±5%），另保護剪裁樣本。packed cell 不能直接當普通 RGB pixel 比較工作量。
- **RapidRAW**：tile 504、每側 margin 12、有效 480、overlap 64、step 416；Smoothstep 權重融合。補回 `src-clamp(src,0,1)` 殘差及低頻空間色彩殘差，不是只做全圖白平衡。
- **darktable**：輸入尺寸取自模型設定；Bayer overlap 32 是 packed pixels，linear 為 32 普通 pixels，RGB 為 64。Bayer 前處理有 daylight D65／as-shot 白平衡選擇，輸出 inverse-WB／remosaic。RGB 低強度可作 DWT 殘差細節混合；RAW 強度不是同一 DWT 流程。
- **FilmBatch**：RawNIND `[1,3,512,512]` FP32、每側 context 64、只寫中央 384；NAFNet `[1,3,768,768]`、context 64、中央 640。Reflect101 padding，丟棄 context 輸出，不做 overlap 加權平均。RawNIND 另作全圖平均 gain matching；alpha／來源中繼資料保持原契約。

tile 數不能直接換算跨模型速度。模型、通道數、packed 尺度、有效核心、融合及 provider 都不同。

### 5.2 FilmBatch 的快取與品質邊界

正式 `Prepare()` 即使收到 200 px 代理，只要檔案存在且參考尺寸較大，仍重開 Full 作完整推論；`PrepareThumbnail()` 才只在縮圖像素上執行。full 與 thumbnail 各自快取，不互相逐出。神經身分包含來源檔案戳記／長度或物件、參考／推論尺寸、RAW 選項、模型與目錄／模型 stamp；強度不在神經身分內，變更混合強度不需重跑模型。

RAW＋AI 的縮圖來源已修正為現像線性來源再縮小，避免內嵌 JPEG 的 sRGB 資料域誤選 NAFNet；不可為了避免缺模型而代換模型、停用 AI 或把正式預覽降成縮圖品質。詳見 [AI ADR](../adr/0002-ai-denoise.md) 與[修正歷史](../history.md#raw-ai-縮圖資料域與取消預覽交付2026-10-26)。

目前推論有程序級 semaphore 1，CPU intra-op 約 1–4；float tensor／ORT／CoreML 的傳輸不是效果 Dawn 的同一裝置或同一套預算。持久化快取應納入模型 digest、來源版本、RAW 選項、容量與原子完成邊界；不能直接把 f32 改 f16 當無損最佳化。

### 5.3 平台與工作流程限制

- RapidRAW 的部分分割／CLIP 等模型受 macOS GPU gate 關閉；降噪從另一入口嘗試 CoreML。不能說全部 AI 都用 GPU，也不能因 gate 就說降噪不可能用 GPU。
- darktable 5.6.2 AI 預設關閉、模型選裝；官方列 Windows DirectML、macOS CoreML、Linux bundled CPU，外部 ONNX Runtime 可另提供 providers。編譯進去不等於當次機器已啟用。
- darktable 的 patch 預覽與獨立全圖工作，有助把昂貴推論和一般交互分開；FilmBatch 研究同類方法時，暫時預覽必須明示，不能冒充正式全品質。
- 本輪沒有驗證四者用同一模型／噪聲分布／相機設定的去噪品質，也沒有宣稱 CoreML 全圖每個 operator 都在 GPU。

## 6. 高光重建、亮暗部與顯示階調

三者要分開：感光剪裁重建估計缺失通道；場景重新打光調整亮暗基底；顯示 tone mapping 把動態範圍映入輸出。所有通道已剪裁時，沒有足夠資料證明恢復成原來真實細節。

| 專案 | RAW 剪裁重建 | 亮部／暗部調整 | 顯示階調 |
|---|---|---|---|
| LightCraft | 去馬賽克相機 RGB；可靠鄰域色度、多尺度金字塔、邊界／中性 fallback | log 亮度基底；V2 先中值再 guided filter，EV 增益共同乘 RGB | 相機擬合曲線或延伸 Reinhard，高光去色／色域處理 |
| RapidRAW | 相機 RGB，自動剪裁位階、opposed-root 重建與空間補償 | guided base/detail；有理亮部壓縮、感知暗部曲線、細節重注／雜訊保護 | 可選 AgX 等 |
| darktable | 早期 RAW 模組，inpaint opposed、guided laplacians、segmentation 等 | tone equalizer 的 guided／EIGF 曝光區增益；另保留舊 Lab shadows/highlights | Filmic RGB、sigmoid、AgX 等 |
| FilmBatch | 已移植 LightCraft 系多尺度高光重建，主要 CPU | 顯示域 Basic；場景線性 `shadow_recovery`；2026-10-28 起另有預設關閉的 `highlight_rolloff`，不取代 Basic | 相機曲線／延伸 Reinhard，晚期 RAW 收尾 |

### 6.1 Basic Highlights 的落差

目前顯示域公式為 `RGB' = RGB + 0.35 h smoothstep(0.45,0.95,Y)`，`h` 是 highlights/100。共同加偏移會改變 RGB 比例；不是局部基底曝光壓縮，也不是剪裁重建。已經平坦的顯示白變暗，不代表重建出高光紋理。

`basic.shadows` 已不是舊加法：現行為顯示域的單調有理 toe、共同 RGB gain，保留黑點並在亮度估計達 0.45 接回恆等。它仍沒有鄰域 base/detail 分離；不能把舊暗部洗白問題繼續描述成現行演算法。

### 6.2 不再新增第二套 guided 暗部模組

既有 `shadow_recovery` 已有帶暗偏移的 log 亮度、alpha 加權 guided base、增益前亮度／色度降噪、共同 RGB gain、黑點／HDR 保護。寫下這段時的下一步是量測基底重用及場景線性亮部壓縮，不是再造同一套暗部。2026-10-28 的五張實拍沒有採用局部亮部基底；採用的是預設關閉、只壓膝點以上的全域肩部，見[亮部實驗](highlight-rolloff.md)。

guided／中值和共同增益不保證無光暈／色偏；半徑、正則化、遮罩邊界、雜訊、極端設定及後續 gamut／tone 都影響結果。darktable 官方亦列 halo、曝光曲線振盪等取捨。segmentation 高光模式沒有 OpenCL，不能把所有高光重建都概括成 GPU。

亮部候選若進實驗，應獨立於產品、明確定義 pivot／強度；以相同來源與顯示收尾比較黑白階、HDR、飽和色、透明邊緣、紋理及高反差邊界。不能用兩邊都設「−100」冒充等亮度或等強度比較，也不能未處理舊設定就直接替換 Basic。

## 7. 前輪已執行的 FilmBatch smoke

以下是本次文件化之前的單次研究收據，不是後續受控實驗重跑。環境：Apple M3／Metal、8 logical cores、macOS 26.6.2、.NET runtime 10.0.13；同一 Sony ARW 有效尺寸 7008×4672，SHA-256 `2655aca052775ed68f737a07ad27e65653ce12853115c07624c187d23e320638`。RAW 與模型使用隔離副本。

| 路徑 | 牆鐘 | 程序 CPU | 累計 managed 配置 |
|---|---:|---:|---:|
| 1600 px 場景代理 CPU | 1.146 s | 2.778 s | 226,683,808 B |
| Full CPU | 2.727 s | 8.920 s | 1,287,508,032 B |
| Full，App RAW GPU 政策 | 0.883 s | 1.553 s | 1,345,320,808 B |

執行順序、JIT、裝置建立及 OS／模型快取未受控，不計穩定加速比；配置量不是峰值／保留量。CPU／GPU 浮點場景 hash 不同，當輪未量全尺寸場景誤差上界，不以不同 hash 直接判錯或判等價。

其他已觀察到的路徑：

- CLI `gpu-info`：Dawn／WebGPUSharp 0.5.7、Apple M3 Metal integrated、max2D 16384。
- CLI `spike --backend Metal --width 1600 --height 1067`：首幀 37.9 ms、uniform 變更平均 2.9 ms、blur 5.6 ms、最大 CPU 分量差 1/255。這只是合成 SpikeParams 路徑，不是完整 RAW／AI 編輯延遲。
- 真實 RawNIND／CoreML＋CPU：200×133 線性縮圖 1 tile，首次含 session 約 1.44 s；相同可見尺寸的正式預覽仍取 Full 作 247 tiles，牆鐘約 14.16 s；amount 80→40 命中完整神經結果，materialize 約 1.47 ms。
- 512×341 實拍來源、shadow recovery＋Basic 亮暗部，實際 Metal（非 CPU fallback）對 CPU 最大差 1/65535，alpha 相同。
- 獨立階段：Basic Highlights −100 將顯示白 1 變 0.65；shadow recovery 的灰階 `[0,0.005,0.02,2]` 成為 `[0,0.03302363,0.08446512,2]`。這是指定 fixture 的黑點／HDR 證據，不是普遍畫質保證。
- 原始 ARW／副本 SHA-256 前後相同。沒有啟動上游 GUI，沒有新增產品測試／修改產品／commit／push。

## 8. 候選、反證與採用次序

| 次序／候選 | 證據與主張 | 反例／未知 | 決策與可推翻方式 |
|---|---|---|---|
| P1：延伸既有 byte accounting、縮短大型暫存存活 | 固定估計與 count cache 無法代表所有來源成本；已有 accounting 可延伸 | f32、AI／GPU／native 保留量不能只靠配置量推算 | 先量 retained／sampled RSS／主要陣列與取消；若主因不在 retained，不能推銷統一預算 |
| P1：互動重播與分段計時 | 首次 AI／來源建立與命中成本差異大 | Engine microbench 不涵蓋 Avalonia／使用者操作 p95 | 固定來源／設定與冷暖，保存逐輪樣本；原生完整互動仍需獨立驗收 |
| P2：少數昂貴前綴快取 | darktable 逐階段 invalidation；FilmBatch 已有來源／工作區重用 | 多留 float 紋理可能更耗 RAM，只有末端參數變更才有利 | 先量同物件／等像素新物件、昂貴基底成本；只在收益支持時另提實作，不重寫通用 graph |
| P2：AI 持久化結果與獨立工作 | 現行 RAM 單筆 full 命中快，跨處理器／重開會失效 | 磁碟容量、模型／來源失效、取消圍欄、寫入完整性 | 先驗證首次／命中／重建成本和可保留量；保持 f32，不靜默降低正式品質 |
| P2：場景線性亮部壓縮 | Basic additive 與局部 gain 資料域不同 | 自動匹配參數、halo、色域、舊照片外觀未定 | 2026-10-26 的 mean-match 原型與局部 guided 仍不採用、不替換 Basic。2026-10-28 另落地預設關閉的全域肩部，見[亮部實驗](highlight-rolloff.md) |
| P3：原生 GPU 呈現／Full 紋理縮放與 Mip | RapidRAW 有可省整幅顯示讀回的實作 | Avalonia／Dawn interop、色彩／alpha／生命週期及窗口等價尚未知 | 先量資料交付與來源重建；沒有端到端收益及等價證據就不 cutover |
| P3：Bayer 聯合降噪 | LightCraft／darktable 模型介入較早 | WB、感光 normalization、高光重建、remosaic、相機品質都變 | 先驗證資料契約；不能只換模型檔或用 RGB tile 數宣稱更快 |

不採用：因上游使用而遷移 GPU backend；把 CPU 平行度全部壓小來當減少工作量；f32 全面改 f16；用 Bayer 模型直接替換 RawNIND Linear；放寬浮點／RGBA16 容差；未驗舊設定就替換 Basic；把研究候選或發行說明當已支援能力。

授權分開看：LightCraft 程式 MIT／Apache-2.0，RapidRAW AGPL，darktable GPL；後兩者僅參考機制，不把原始碼移入 FilmBatch。模型權重另依各自條款；FilmBatch 固定 RawNIND Linear 權重 SHA-256 `df957efadcc152c007d5d3b0917bdff9e41c0d4a0efe56584ef30b36393cd181`，NAFNet-SIDD 為 `8b437280db2f9f0ef5c733fa0fec70fc10012a62d19dac24a25b80ec8c529230`，詳見 [catalog](../../src/FilmBatch.Engine/Denoising/DenoiseModelCatalog.cs)、[ADR](../adr/0002-ai-denoise.md)及[第三方聲明](../../THIRD-PARTY-NOTICES.txt)。本輪不新增模型下載／授權接受。

## 9. 可追溯來源

以下固定 SHA 是程式主張來源；官方 manual 是版本文件，不是本機執行證據。

### LightCraft

- [RAW 載入／分箱／模型接點](https://github.com/storytold/lightcraft/blob/bb08a697a41b2561a54f1b740245119503b24758/crates/engine/src/files.rs#L239-L490)。
- [AHD 平行 bands／tiles](https://github.com/storytold/lightcraft/blob/bb08a697a41b2561a54f1b740245119503b24758/crates/raw/src/demosaic/ahd.rs#L1-L60)。
- [GPU readback／map／copy](https://github.com/storytold/lightcraft/blob/bb08a697a41b2561a54f1b740245119503b24758/crates/gpu/src/ctx.rs#L570-L618)。
- [預算與前景例外](https://github.com/storytold/lightcraft/blob/bb08a697a41b2561a54f1b740245119503b24758/crates/engine/src/memory.rs#L14-L100)。
- [Bayer 模型／tile／gain 契約](https://github.com/storytold/lightcraft/blob/bb08a697a41b2561a54f1b740245119503b24758/crates/denoise-core/src/known.rs#L22-L57)。
- [V2 local base](https://github.com/storytold/lightcraft/blob/bb08a697a41b2561a54f1b740245119503b24758/crates/pipeline/src/local.rs#L326-L360)、[亮暗部 EV gain／tone 收尾](https://github.com/storytold/lightcraft/blob/bb08a697a41b2561a54f1b740245119503b24758/crates/pipeline/src/finish.rs#L469-L538)。

### RapidRAW

- [RAW loader／Apple 選用條件](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/image_loader.rs#L108-L174)、[CPU rawler／色彩與亮部處理](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/raw_processing.rs#L252-L459)。
- [非同步 shader 特化](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/gpu_processing.rs#L2135-L2188)、[原生 surface／跳過 image readback](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/gpu_processing.rs#L3160-L3228)。
- [f16 cache compaction](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/cache_utils.rs#L431-L487)、[估計入場預算](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/memory_budget.rs#L28-L80)。
- [AI 神經快取／殘差補償](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/ai_denoise.rs#L297-L465)、[provider／平台 gate](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/ai_processing.rs#L500-L655)、[降噪專用入口](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/ai_processing.rs#L1237-L1277)、[tile 權重](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/ai_processing.rs#L1407-L1463)。
- [亮暗部 shader](https://github.com/CyberTimon/RapidRAW/blob/a4f2cf5cb867fa1def9f6d5ca2a16654b56fb8a8/src-tauri/src/shaders/shader.wgsl#L476-L657)。

### darktable

- [5.6 neural restore 操作與獨立輸出](https://docs.darktable.org/usermanual/5.6/en/module-reference/utility-modules/shared/neural-restore/)、[AI provider／平台限制](https://docs.darktable.org/usermanual/5.6/en/special-topics/ai/gpu-acceleration/)。
- [正式版 neural restore](https://github.com/darktable-org/darktable/blob/20891f6c6fa7be995e5bf9dff9d1ee5062051773/src/libs/neural_restore.c)、[Bayer 前處理](https://github.com/darktable-org/darktable/blob/20891f6c6fa7be995e5bf9dff9d1ee5062051773/src/common/ai/restore_raw_bayer.c)。
- [正式版 pixelpipe cache 身分](https://github.com/darktable-org/darktable/blob/20891f6c6fa7be995e5bf9dff9d1ee5062051773/src/develop/pixelpipe_cache.c#L30-L165)、[CPU／OpenCL 交接](https://github.com/darktable-org/darktable/blob/20891f6c6fa7be995e5bf9dff9d1ee5062051773/src/develop/pixelpipe_hb.c#L3100-L3235)。
- [記憶體／tiling／排程](https://docs.darktable.org/usermanual/5.6/en/special-topics/mem-performance/)、[高光重建與限制](https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/highlight-reconstruction/)、[tone equalizer 與 halo 取捨](https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/tone-equalizer/)。

### FilmBatch

- [RAW scope／帶狀 GPU](../../src/FilmBatch.Engine/Imaging/Raw/RawGpuDemosaic.cs)、[RAW loader](../../src/FilmBatch.Engine/Imaging/Raw/RawSourceLoader.cs)、[共用解碼租約](../../src/FilmBatch.Engine/Imaging/Raw/RawSourceLoader.Decode.cs)、[每迴圈並行範圍](../../src/FilmBatch.Raw/RawProcessingScope.cs)。
- [GPU 效果資源／讀回](../../src/FilmBatch.Engine/Rendering/GpuEffectPipeline.cs)、[匯出隔離](../../src/FilmBatch.Engine/Exporting/BatchExporter.cs)、[Loupe 來源／GC](../../src/FilmBatch.App.Shared/ViewModels/MainWindowViewModel.Loupe.cs)。
- [入場估計](../../src/FilmBatch.App.Shared/Services/DecodeBudget.cs)、[影像來源快取](../../src/FilmBatch.App.Shared/Services/ImageCache.cs)、[畫面 byte LRU](../../src/FilmBatch.App.Shared/Services/Loupe/ViewRenderCache.cs)。
- [神經快取／provider](../../src/FilmBatch.Engine/Denoising/DenoiseProcessor.cs)、[tile／mix](../../src/FilmBatch.Engine/Denoising/DenoiseTiling.cs)、[RAW 縮圖來源重評估](../../src/FilmBatch.App.Shared/ViewModels/FilmstripViewModel.cs)。
- [Basic display](../../src/FilmBatch.Engine/Rendering/CpuPipeline.cs)、[場景暗部還原](../../src/FilmBatch.Engine/Rendering/ShadowRecovery.cs)、[RAW 剪裁重建](../../src/FilmBatch.Raw/Develop/Highlight.cs)、[RAW 顯示](../../src/FilmBatch.Engine/Imaging/Raw/RawDisplay.cs)、[ToneMap](../../src/FilmBatch.Engine/Imaging/Raw/ToneMap.cs)。

研究工具限制：語意搜尋服務不可用時改用已知名稱／來源定位；外部 partial clone 的 darktable worktree 曾遇 promised-blob 取回失敗，改讀固定 SHA 原始碼。這些是研究工具問題，不是產品缺陷，沒有因此省略 load-bearing 主張或推導速度排名。
