# 本機效能／RAW／AI／亮暗部實驗（2026-10-26）

**E01–E07 與固定強度補測均已實跑，結果包含明確反例；不是全綠驗收或產品修復。** 先保存[完整四產品 Survey](../research/performance-raw-ai-survey.md)、[預先規格](../superpowers/plans/2026-10-26-performance-local-experiments.md)與導航，才編譯／執行探針。RAW 大離群已限縮至切換 AHD 路徑的邊界；1600 px 暗部＋亮部組合的 3 LSB 反例仍保留。追加結果見第 10 節。

本輪支持保留穩定來源物件、量測 AI 快取存活期，以及進一步研究完整 f32 結果持久化；**不支持立即擴大 RAW GPU 化、放寬容差，或用本輪全域／局部 shoulder 原型替換 Basic Highlights**。研究完成不等於候選獲准採用、產品已修復或所有平台通過。

## 1. 驗收與命令狀態

| 項目 | 交付狀態 | 觀察／判定 |
|---|---|---|
| DOC-01：先記錄完整 Survey／方法／導航 | verified | 文件包含四產品版本、來源、效能／RAW／AI／亮暗部及限制；執行前 73 個相對連結目標存在 |
| E01：RAW／記憶體／來源重用／傳輸控制 | verified | 完成交錯 6 對 Full、真正預覽快取對照、來源與完整輸出比較；RAW 解碼等價性不成立，不是精度通過 |
| E02：真實 AI 首次／縮圖／full／快取 | verified | RawNIND／CoreML＋CPU；1／247 tiles、12 次混合命中、縮圖隔離、兩個 processor 首次，設定還原後像素完全相同 |
| E03：亮暗部／CPU／Metal／控制候選 | verified | 實驗完整執行並保留反例：8 組中 1600 combined 最大 3 LSB、1 個 RGB 分量超過 2；其他 7 組在 2 內；不將失敗改為 pass |
| E04：Full RAW AHD／色彩四組分離 | verified | CPU／both 完整 hash 重現；AHD 切換重現大離群，colour-only 較小但也不完全等價；不宣稱內部分支根因已解 |
| E05：兩模型 provider／資料域／完整 f32 I/O | verified | RawNIND 與 NAFNet 各 CPU／CoreML 初次＋6 筆真推論；247-tile 完整神經結果三次逐 byte 相同 |
| E06：CPU 1／2／4 workers | verified | 各 6 筆交錯 Full，完整場景 hash 相同；成本與程序 CPU-time/wall 分列 |
| E07：局部 guided base／重用／品質 | verified | 四組實拍等亮度皆不可達；合成紋理／噪聲／階梯反例保留；另以既定 k=1.75 補齊套用成本及真正 Metal 收尾 |
| EVID-01：收據／重播／來源不變 | verified | 774 個產品來源檔 digest 不變；隔離輸入／來源副本／模型不變；各 mode 產品組件 digest 相同；逐輪收據、probe source 與裁決另行封存 |

`verified` 表示該研究項目已執行、結果可追溯，不表示所有研究假說成立。

| 實際命令／階段 | Exit | 結果 |
|---|---:|---|
| 隔離 probe 首次 Release build | 0 | 產品編譯成功；probe 有兩項 nullable flow 警告 |
| 補真實 throw assertion 的 flow metadata 後 build-final | 0 | 0 警告／0 錯誤；未改產品 |
| `FilmBatch.Core.Tests.dll raw <work> <repo>` | 0 | RAW 成本、匯出對照、完整場景與效果、64 MiB transfer control 收據完整 |
| `… ai <work> <repo>` | 0 | 真實模型與所有 tile／cache／來源不變條件通過 |
| `… tone <work> <repo>` | **2** | 完成量測／控制組後，因 1600 combined 的 3 LSB 反例失敗；原始 failure／輸出保留 |
| 加入限縮診斷後 build-diagnostic | 0 | 0 警告／0 錯誤；產品組件與前輪相同 |
| `… reuse <work> <repo>` | 0 | 真正 BGRA8 預覽快取對照，6 對完整輸出 bytes 相同 |
| `… rawdiff <work> <repo>` | 0 | 取得 RAW 尾端差分布與可見影響；此 exit 僅表示診斷完成，不表示等價 |
| build-expanded／build-local-fixed | 0／0 | 兩次皆 0 警告／0 錯誤；各自執行的 source／probe digest 分版保存 |
| `… stage-compare <work> <repo>` | 0 | 四組 Full、六對完整場景與同一 CPU 收尾比較；不是等價通過 |
| `… aiproviders <work> <repo>` | 0 | 兩模型×兩 provider；每組初次＋6 次，全部 1 tile／非 cache hit |
| `… aidisk <work> <repo>` | 0 | 真實未混合完整 neural RGB，三次 f32 write／flush／read 逐 byte 相同 |
| `… cpuworkers <work> <repo>` | 0 | 三筆暖機＋18 筆交錯 Full，來源完全相同 |
| `… local <work> <repo>` | 0 | 記錄四次不可達匹配及合成邊界；exit 0 只表示研究程序完成 |
| `… local-fixed <work> <repo>` | 0 | 非等亮度 k=1.75 補測；四組×6 筆 apply／CPU／Metal，沒有 GPU fallback |

所有 probe 執行均由 `caffeinate -is` 包住並循序執行，沒有互相爭用的重型 probe；不代表其他程序、熱狀態或 OS cache 已完全控制。沒有為了改文件重跑無關全套測試，沒有 commit／push／發行。

## 2. 固定版本、輸入與量測單位

- FilmBatch 來源：`890b13f4e610eb1498eb294fc6f7da18b76a7533`；開始時工作目錄乾淨，長期改動僅文件。
- Apple M3／Metal，Mac15,12，8 logical cores，16 GiB RAM，AC power；macOS 26.6.2／25G83，Darwin 25.6.0，arm64。
- .NET SDK 10.0.402、實際 runtime 10.0.13；WebGPUSharp 0.5.7、ONNX Runtime 1.25.0、Magick.NET 14.17.2。
- `DSC03376.ARW` 隔離副本，有效來源 7008×4672；SHA-256 `2655aca052775ed68f737a07ad27e65653ce12853115c07624c187d23e320638`。
- RawNIND Linear 已存在權重的隔離副本，31,053,823 B；SHA-256 `df957efadcc152c007d5d3b0917bdff9e41c0d4a0efe56584ef30b36393cd181`；沒有下載／接受新授權或修改權重。
- NAFNet-SIDD 也使用已安裝權重的隔離副本，117,341,957 B；SHA-256 `8b437280db2f9f0ef5c733fa0fec70fc10012a62d19dac24a25b80ec8c529230`，沒有下載。NAFNet 與 RawNIND 的資料域／tile 不同，只在各模型內比較 provider，不據此排名模型畫質或速度。
- RAW 實驗預算 1024 MiB、Metal policy／CPU policy；RAW 迴圈仍是既有半核心預設，不因明示 scope 改成全核心。AI／效果對照固定 CPU 解碼來源。
- 完整 Settings、RawLoadOptions、尺寸／reference、實際 adapter／UsingCpu／provider／TileCount／CacheHit 及組件 digest 均在逐輪收據。

### 產品組件 SHA-256

以下產品組件在首輪及追加程序皆相同；只有 probe 隨診斷改版，精確 source／digest 分開保存 v1／v2／v3／v4。

| 組件 | SHA-256 |
|---|---|
| FilmBatch.Engine | `3e62ccae8ffabc2e50f71e1646a9a4cffe811ad8d41c4489df24078d7bcfe539` |
| FilmBatch.Core | `65ce268b610d1b2fda5f2ab87213d810f26d8cdc851c0ad5f7d2cf242e7ba400` |
| FilmBatch.Gpu | `11f5d9ff9137b08a6bfcde49619235138b24237140ff12fccef30050e42578c6` |
| FilmBatch.Raw | `9c4fcb2681b780150c0f09ca5a5238f9365fd86c8967d7c1db50441c0043e895` |

### 方法限制

耗時用同步 API 牆鐘，含 CPU／GPU 工作、同步及讀回；hash／全圖差分掃描在計時外。每輪另記程序 CPU、累計 managed allocation、10 ms 採樣 working-set／RSS；採樣器自己的少量成本包含在內。下表 MiB 均為 2²⁰ B。

- allocation 不是峰值／保留量；GC 後 `GetTotalMemory` 是 managed 存活量；sampled RSS 是當次程序最大觀察值，可能漏真峰值，不是 native／GPU 分類或程序硬上限。
- Full CPU／GPU 暖機各一筆後，6 對交錯並反轉次序；系列期間 1600 proxy 仍存活，RAW GPU 共用裝置可持續存在。不能把兩組 RSS 最大值差當成獨立 kernel 額外記憶體。
- 效果各設定按順序跑，設定內 CPU／GPU 交錯；JIT／PGO、GC、記憶體狀態未完全穩態。不能從「加亮部分支反而較快」推導該效果負成本。表格為本輪絕對觀察，不是跨設定因果差額或穩定交互 p95。
- 小樣本只報中位數／範圍／n；AI Full 首次只有兩個 processor，不宣稱穩定 cold／warm 排名。CoreML 可能重用 compiled-model disk cache；這不是神經 RGB 結果的持久快取。
- probe 的明確 Forced GC／等待 finalizer 在案例外，用來識別保留量；不是產品 Aggressive GC 策略的實測，也未觀察原生 UI 暫停。

## 3. E01：RAW 成本、來源身分及資料搬移

### Full：暖機後交錯樣本

| 政策 | n | wall 中位數（範圍）ms | CPU 中位數 ms | managed 配置中位數 MiB | sampled RSS 最大觀察 MiB |
|---|---:|---:|---:|---:|---:|
| CPU RAW | 6 | 2019.07（1913.97–2192.52） | 7465.75 | 1228.41 | 1317.83 |
| App RAW Metal policy | 6 | 665.53（607.18–742.77） | 1207.96 | 1283.05 | 1496.45 |

首次 CPU／Metal policy 分別 2541.67／1656.62 ms，與暖樣本分開。RAW AHD／Colour `LastErrors` 均 null；本輪沒有插入每個 kernel 的計數／timestamp，不把 policy 標籤寫成整條 RAW 都在 GPU。此來源的時間收益明確，但較快沒有同步降低配置／RSS，更不保證數值等價，見 §4。

1600×1067 場景 proxy 單次 CPU 載入 768.34 ms、配置約 216.49 MiB；含初次來源準備，不當暖基準。

### 真正的來源重用

初輪以 `Render16` 比同物件／新物件，兩者都配置約 39.09 MiB。查原始碼確認 [RenderForExport](../../src/FilmBatch.Engine/Rendering/GpuEffectPipeline.cs) 入口會 `ClearSources()`；該輪是有效的匯出控制，但不能回答預覽快取問題。保留原收據，追加實際 BGRA8 `Render()`，不以文件宣稱抹掉測量入口錯誤。

同一 1600×1067 場景、相同設定、同一 Metal renderer；新物件只包裝同一份 `RgbImage`，不重複解碼／複製場景。6 對交錯，12 筆完整 BGRA bytes hash 都等於基準，沒有 CPU fallback。

| 真正預覽入口 | n | wall 中位數（範圍）ms | CPU 中位數 ms | 配置中位數 MiB |
|---|---:|---:|---:|---:|
| 相同 SourceImage 物件 | 6 | 5.243（4.863–5.986） | 1.198 | 6.516 |
| 等像素、新 SourceImage 物件 | 6 | 20.200（14.792–66.449） | 27.820 | 32.576 |

差額約 26.06 MiB，與該 proxy 一份工作 RGBA32f 的尺寸一致；這是來源建立／轉工作色域／上傳整體路徑，不是純 PCIe 或 GPU kernel 時間。保留穩定身分有收益，但來源／RAW 選項改變時仍必須正確失效，不能為了命中而沿用錯誤來源。

其他來源控制：

- 首次取 `preview.Rgba16` 約 49.97 ms／13.08 MiB；之後 6 次回同一陣列，接近計時器解析度。這是 lazy 顯示編碼，不是 RAW 編輯來源變成 RGBA16。
- `ScaledTo(800)` 首次 3.56 ms；相同 edge 回同一物件。`Downscale(800)` 6 次重新建立，中位數 2.27 ms／14.73 MiB，像素 hash 與快取版本相同。不能把接近零的 getter 時間視為算圖加速比。
- 推導資源：Full RGB float 為 374.695 MiB；Full 效果 GPU 的來源 RGBA32f 約 499.594 MiB、四張工作紋理約 1998.375 MiB，不含 band／輸出／遮罩。這是格式推導，不是實測 GPU 實體用量。
- 同一 CPU Full 來源經 CPU／Metal `Render16`：完整 130,965,504 個 RGBA16 分量最大差 1 LSB、alpha 全同。單次 wall CPU 1256.88、Metal 1072.59 ms；Metal 含首次 Full workspace 重配置，不能視為暖效果排名。此結果不包含 RAW CPU／GPU 解碼差異。
- 獨立 64 MiB buffer write→copy→map/read 控制，完整資料相同；首次 18.33 ms，6 次後續中位數 14.04 ms（11.11–23.04）。它不是影像 readback 的單獨耗時，更不是零拷貝／整體呈現收益證明。

GC 後原始 proxy 存活約 21.23 MiB；保留 Full＋proxy 時 managed 約 409.38 MiB；來源／renderer 離開 NoInlining 範圍後約 15.68 MiB，而 RSS 仍約 474.30 MiB。兩者不可互換，也不能只據此宣稱 native leak 或 GC 已歸還全部實體頁。

## 4. E01 的精度風險：RAW 解碼不是效果 parity

兩個完整場景 hash 可重複取得：CPU `58230b3d1009c15de16d4c72928f3add0349383d2a5ae5f26191c28c43daf837`，Metal policy `a4785b6114ed08f43b87acc1b0652142a7782ad67541c3731708581d7c546d7b`。98,224,128 個線性 Rec.2020 RGB 分量全部有限，平均絕對差約 `1.11045e-7`，但最大差 **1.1265284**，不能只報平均值。

| 絕對場景差大於 | 分量數 |
|---:|---:|
| 0.00001 | 4620 |
| 0.0001 | 2285 |
| 0.001 | 1197 |
| 0.01 | 195 |
| 0.1 | 3 |
| 1.0 | 1 |

最大場景差位於 `(1697,225)`：CPU RGB 約 `[1.8556583,1.1542052,1.7061007]`，Metal policy 約 `[2.2807326,2.2807336,2.2807333]`。該點在顯示後大多壓到白，單靠它還不能判定最差可見影響。

因此把兩份不同 RAW 解碼結果各交**同一 CPU Render16／相同預設／同一收尾**：

| 可見輸出指標（RGB16 codes） | 觀察 |
|---|---:|
| 最大分量差 | **13,650** |
| 平均分量差 | 0.0179853 |
| 大於 2 LSB 的 RGB 分量 | 1,899 |
| 大於 32 LSB | 1,661 |
| 大於 256 LSB | 1,098 |
| alpha 差異 | 0 |

最差可見位置為 `(1045,841)` 的 R，CPU 0／Metal policy 13650。另有 `(1859,563)` 的藍色場景 0.2138019／0.11763444，顯示藍約 30264／19158。可見影響不是只有浮點尾數。

**裁決：未證明 RAW CPU／GPU 每像素等價；哪側更正確尚未判定。** 本輪未分離 AHD、CPU 高光重建與 GPU 色彩來定位第一個分歧，不把原因硬歸給某個 shader。CPU 也不是絕對真值。現有 [AHD 測試](../../tests/FilmBatch.Raw.Tests/Develop/GpuAhdTests.cs) 是特定合成輸入的離群比例門檻，不是全相機／每像素最大差保證；本輪只讀其契約，沒有重跑或更改它。來源未改，不能稱為此次文件造成的回歸，也不能用低比例離群當已證明安全。

後續若處理 RAW 效能，先保留這張輸入／最差座標，逐階段找第一個分歧；本輪不改產品、不關閉 GPU 掩蓋差異、不放寬任何容差。

## 5. E02：真實 AI 與快取保留量

實際 provider `CoreML + CPU`、模型 `rawnind-linear`。200×133 proxy 保留 7008×4672 reference，兩者不能混為推論尺寸。

| 路徑 | n | wall 觀察 ms | TileCount／CacheHit |
|---|---:|---:|---|
| 縮圖首次含 session | 1 | 1303.46 | 1／false |
| processor 0 正式預覽首次 | 1 | 22369.37 | 247／false |
| processor 1 重建後首次 | 1 | 31181.13 | 247／false |
| 改 amount／luma／color | 12 | 中位 0.404；0.342–2.021 | 247／true，沒有新 infer-start |
| 插入 320×213 縮圖 | 1 | 68.55 | 1／false |
| 插入縮圖後重取 full 200×133 | 1 | 0.796 | 247／true，像素 hash 未變 |
| full cache 新顯示尺寸 512×341 | 1 | 134.66 | 247／true，無新推論 |
| 相同 512×341 再 materialize | 6 | 中位 2.810；2.006–9.414 | 247／true |
| full cache 新顯示尺寸 1600×1067 | 1 | 184.19 | 247／true，無新推論 |

正式預覽慢不代表可偷換成 thumbnail：247 tiles 是仍走原圖的證據。新顯示尺寸首次需要 box 建 original／denoised preview inputs，即使命中神經快取仍有 135–184 ms 成本。還原原混合強度，場景像素 hash 完全還原；縮圖沒有逐出 full。

### GC 後快取存活量

| 觀測邊界 | managed 存活 MiB | 當時 RSS MiB | 明確 GC wall ms |
|---|---:|---:|---:|
| 僅 proxy 來源 | 21.119 | 206.188 | 1.74 |
| 縮圖神經快取存活 | 28.258 | 361.938 | 1.34 |
| processor 0 Full 神經結果存活 | 787.986 | 870.125 | 66.16 |
| processor 0 dispose／離開後 | 33.495 | 205.203 | 23.98 |
| processor 1 Full 神經結果存活 | 787.252 | 921.734 | 62.29 |
| processor 1 dispose／離開後 | 30.750 | 170.531 | 26.10 |

Full 原始 RGB 與神經 RGB 各 374.695 MiB，合計 **749.391 MiB**，是實際快取的主要陣列格式推導；其餘含 proxy、tensor／session、物件與預覽，不能把推導合計直接當 RSS。首次 Full 兩個樣本配置中位約 1643.78 MiB，最大 sampled RSS 約 1637.33 MiB；與 GC 後保留量不同。

**裁決：** P1 應先暴露／管理已存在的 full 神經快取及 preview inputs 存活期，不先造通用預算框架。P2 持久化值得另做容量／失效設計，但本輪沒有實作或量持久 neural RGB cache；只存神經結果也不會自動省掉重解碼／混合所需的原始 Full。不得改 f16 當無損，不得為省 RAM 靜默降正式品質。

## 6. E03：現行階段、精度反例與成本

### 階段與 alpha 邊界

實際 `BasicDisplay`，Highlights −50：黑／0.0001 不變；白 1→0.825、HDR 2→1.825；飽和綠 `[0,1,0]`→約 `[-0.09547018,0.9045298,-0.09547018]`，有限但進最終量化前有負通道。它是加法亮部調整，不是剪裁重建；各種 alpha 下 RGB 相同，alpha 未動。

實際 `ShadowRecovery.Apply`，+3 EV／radius 2／luma 35／chroma 80：純黑保持 0、近零灰 0.002→約 0.0146453；白 1、HDR 1.05／1.1 的控制內部逐位元不變；alpha=0 像素原樣通過，來源陣列不變。這些是指定 fixture，不推廣成無光暈／無色偏保證。

複製既有 37×19 分數 alpha fixture，沿用 RGB≤32／alpha≤1 的原門檻，實測最大 RGB 差 1、alpha 差 0、實際 Metal 非 fallback。實拍另外設更嚴格 2 LSB 研究目標，不混稱既有全管線通用契約。

### 相同解碼來源的 CPU／Metal Render16

下表設定為預設、Basic Highlights −50、ShadowRecovery +3 EV／r2／l35／c80，以及兩者合併；每組兩筆暖機後各 6 筆 CPU／Metal 交錯。CPU／Metal 各自 6 次 hash 穩定，記錄的 64 次 GPU warm／sample 均非 fallback。成本含每次 `Render16` 的來源重上傳與讀回，不代表持續命中來源的 BGRA8 原生預覽。

| 尺寸／設定 | CPU wall 中位 ms | Metal wall 中位 ms | 最大 RGB 差 LSB | 超過 2 的分量 | alpha 最大差 |
|---|---:|---:|---:|---:|---:|
| 512×341／預設 | 12.858 | 1.697 | 1 | 0 | 0 |
| 512×341／亮部 | 12.831 | 1.698 | 1 | 0 | 0 |
| 512×341／暗部還原 | 18.476 | 2.416 | 1 | 0 | 0 |
| 512×341／合併 | 18.754 | 2.321 | 1 | 0 | 0 |
| 1600×1067／預設 | 91.538 | 15.191 | 1 | 0 | 0 |
| 1600×1067／亮部 | 47.856 | 18.008 | 1 | 0 | 0 |
| 1600×1067／暗部還原 | 140.910 | 27.161 | 2 | 0 | 0 |
| 1600×1067／合併 | 140.263 | 29.478 | **3** | **1** | 0 |

最後一組未達預先目標，tone mode 因此 **exit 2**。本輪不把 2 改 3／32、不靠 CPU fallback 讓它變綠、不重跑只為確認、不把既有較寬 fixture 門檻拿來改判這個實拍目標。這是現行行為的研究反例；未定位單分量誤差的第一階段，亦未授權產品修正。

512×341、預先配置工作區的階段控制：ShadowRecovery.Apply 中位 5.768 ms、BasicDisplay 0.783 ms、獨立全域 shoulder 0.337 ms（各 n=6）。三者工作與資料域不同，不能據此說候選比 guided 更好；shoulder 首樣本含 JIT，0.337 ms 不是完整畫面交付。

## 7. 全域 shoulder 控制：匹配亮度不等於保留高光細節

獨立寫出的非局部控制，線性 Rec.2020 亮度 `Y`、pivot=0.18，`Y>pivot` 時：`mappedY=pivot+(Y-pivot)/(1+k*(Y-pivot))`，RGB 同乘 `mappedY/Y`；其餘不變。沒有移入 GPL／AGPL 實作，不冒稱上游 guided filter。`k=0` 的場景及完整 CPU Render16 輸出 hash 與 baseline 完全相同。

ROI 是 baseline 顯示碼值亮度最高 10% 的 17,459 像素；不是手動挑一個對候選有利區域。以 Basic Highlights −50 的 ROI 平均值為目標，10 個含 identity／bracket／bisect 的 render 得 `k=1792`，平均差 1.413/65535，在預設 2 code 匹配界線內。匹配 render 不算 benchmark。

| 同一 ROI，顯示碼值單位 | 預設 | Basic Highlights −50 | 全域 shoulder 控制 |
|---|---:|---:|---:|
| 平均亮度碼 | 53274.60 | 44916.12 | 44917.53 |
| ROI 亮度標準差 | 8342.44 | 5514.22 | **1232.55** |
| ROI RGB 總和的 R/G | 0.98801 | 0.98579 | 0.94519 |
| ROI RGB 總和的 B/G | 1.01750 | 1.02075 | 1.03588 |
| ROI 像素與左鄰居平均亮度絕對差 | 3815.10 | 2653.08 | 1944.28 |
| ROI 像素與上鄰居平均亮度絕對差 | 2178.75 | 1465.01 | 973.62 |

鄰居不一定在 ROI，差值混有邊緣／紋理／噪聲，不是 SNR／真實細節分數；亮度權重用顯示 codes，不是線性光能或色差。共同 scene RGB gain 也未保證經相機曲線／gamut mapping 後的顯示 RGB 比例相同。

另實際查看 208×171 的預設／Basic／候選 PNG，候選舞台衣物等亮部層次更集中；小圖、未校準顯示、未盲評，不當正式人工畫質勝負。數值已顯示：為匹配亮度所需的強 shoulder 明顯壓窄 ROI 分布，不能只憑 mean match 或共同 gain 宣稱保留細節。

**不採用這個控制作為 Basic 替代。** 首輪 E03 沒有局部候選；追加 E07 實作並測了獨立 guided base，但四組等亮度皆不可達且階梯有 overshoot，見第 10 節。沒有把任一原型升格為產品。

## 8. 採用／不採用與下一步

| 候選 | 本輪證據 | 裁決／下一個能推翻判斷的檢查 |
|---|---|---|
| 穩定來源物件與既有快取 | 真正 preview 的 5.24／20.20 ms、配置 6.52／32.58 MiB，完整 bytes 相同 | 優先保留既有身份／縮放重用；若準備修改呼叫端，須先定位實際哪個互動重造物件，驗來源／RAW 選項失效，不先新增快取框架 |
| AI byte accounting／存活期 | full cache 約 788 MiB managed、兩份大 RGB 為 749.39 MiB；dispose 後降至 31–34 MiB | 優先量／暴露此保留量；不能用累計配置或 native RSS 代替。產品政策另需取消／前景／跨來源驗收 |
| AI 持久神經結果 | 處理器重建仍需 247 tiles；完整 374.70 MiB f32 寫入／flush 與讀回各有三筆實測，逐 byte 相同 | 保留 P2 候選，未實作產品快取；尚需來源／模型 digest、RAW 選項、容量、原子發佈、取消完成邊界與重載後混合／顯示驗收 |
| RAW GPU 常駐／更多卸載 | 大離群隨 AHD 切換重現；colour-only 仍有最高 10 LSB 的輸出差 | **不擴大既有適用範圍、不放寬門檻**；尚未分離 AHD 原始輸出與後續高光重建，也不知道哪側更正確 |
| 亮暗部／GPU 效果改動 | 1600 combined 有 3 LSB 反例；其餘列通過本輪目標 | 保留反例，先定位第一個分歧；不把不同尺寸通過或原 fixture 32 當成替代驗收 |
| 簡單全域 shoulder 取代 Basic | mean match 但標準差與 RGB 比例明顯改變 | **不採用此控制**；局部候選必須另證明邊界／紋理／等亮度及相容性，不能只換公式 |
| 局部 guided-base 取代 Basic | 四組實拍均不可達等亮度；合成紋理較全域保留更多振幅，也保留較多雜訊且有階梯 overshoot | **不採用此原型**；fixed-k 收尾通過不能替代品質／相容性 |
| 為降 CPU 把 RAW 全改 1 worker | 1／2／4 workers 完整像素相同，但 wall 約 5.73／2.98／1.65 s | 不改現行預設；worker scope 是每迴圈並行限制，不是整個程序的 CPU 百分比硬上限 |
| 原生 GPU 呈現／Mip／模組前綴 | 控制確認傳輸與重新上傳有成本，但未測互通／真正窗口場景 | 仍為候選；未實作 Avalonia interop／Mip／前綴快取，未證明整體交互收益，不遷移 backend |

產品 `ImageCache`、`DecodeBudget`、DenoiseProcessor、Loupe、Basic、shader、schema、oracle 均未改。只有研究／計畫／證據／導航／歷史文件更新。未在本輪執行原生 Avalonia GUI、實體鍵鼠、Windows／DX12／DirectML、iOS、多相機／355 張資料夾、上游 GUI 或四產品同機畫質／速度比較；PNG 檢視不代替色彩管理／正式畫質驗收。

## 9. 收據、封存與重播

本機證據包位置：`artifacts/verification/2026-10-26-performance-local.tar.gz`（gitignored，不上傳／推送）。收據按來源版本保存：

- `probe-v1/`：原 raw／ai／tone 使用的精確 source；原 probe SHA-256 `c2754f90db3ac4d5d87194447000b69c9e0c67e3343bd99fbd602c37a8dfce04`，不是產品組件。
- `probe-v2/`：追加 reuse／rawdiff 的精確 source；`probe-v3/`：E04–E07；`probe/`（v4）：只再追加 `local-fixed` 入口／補測。各版 digest 獨立，不把舊失敗重編成新結果。
- 所有 mode 的 `.stdout`／`.stderr`／`.exit`、各次 build logs、`commands.json`（實際命令／時間／exit），包括非零的原 tone 收據。
- 各組 `*-summary.json`、首輪及追加 `analysis-summary`、環境／來源／輸入與兩個模型的 digest、`preservation.json`、`REPLAY.md` 及逐檔封存 manifest。

不含 RAW、模型、完整解析度影像、PNG／PPM、bin／obj 或執行檔；工作／repo／home 前綴在收據換成符號值。研究渲染與推論只讀隔離照片，沒有寫入原始照片／使用者專案；所用隔離副本及其既有來源副本、權重前後 digest 相同。要逐位元重播需自行提供有權使用、相同 digest 的照片／權重，不宣稱私有輸入已公開。

重播：從對應 source 版本建立新的隔離 `probe/`，以 `dotnet build <work>/probe/LocalExperiments.csproj -c Release -p:FilmBatchRepo=<repo>` 建置；循序執行 `dotnet <work>/probe/bin/Release/net10.0/FilmBatch.Core.Tests.dll <mode> <work> <repo>`。首輪 mode 為 raw／ai／tone，v2 追加 reuse／rawdiff，v3 追加 stage-compare／aiproviders／aidisk／cpuworkers／local，v4 再追加 local-fixed。tone 的非零 exit、local 的不可達與品質反例都不能為了全綠而修改。完整輸入、方法與副作用界線見[計畫](../superpowers/plans/2026-10-26-performance-local-experiments.md)。

工具修正另記：語意搜尋服務／Python eval backend 不可用時，改已知符號定位及 JavaScript／Bun 收據檢查；這些不是產品失敗。probe 的 nullable flow、來源釋放的 NoInlining 範圍及錯用匯出入口的量測方法，均只在儲存庫外修正／追加，不以產品修改掩蓋。

## 10. 追加 E04–E07：完整實跑與負面結果

### 10.1 E04：大型 RAW 離群與 AHD 路徑切換相關

同一 7008×4672 輸入，單一拋棄式程序依序執行 CPU、GPU AHD-only、GPU colour-only、both；GPU policy 先選 Metal，再以反射設 private 停用 flags，CPU 組停用 GPU。這些不是產品設定；尊重 `FILMBATCH_NO_GPU_DEMOSAIC`，不改使用者環境。四份場景同時保留供全分量比較，所以這個 mode 的 RSS 不能拿來比較單一路徑。

CPU SHA 重現 `58230b3d…daf837`，both 重現 `a4785b61…546d7b`。AHD-only／both 的 GPU AHD delegate 各真正回傳一次非 null，colour-only 的 delegate 回傳 null；colour-only／both 的 colour preflight 各完成 2048 rows，正式 decode 無 GPU error。preflight 是 2048² 合成影像的正向控制，不是完整 decode 每次 dispatch 的直接量測；實際色彩卸載另由 colour 組相對 CPU 的非零場景差佐證，不是只憑 adapter 名稱推定。

| 比較；未切換的路徑固定 | 場景最大絕對差 | 場景差 >0.01 分量數 | 同一 CPU 收尾最大差 LSB | RGB >2 分量數 |
|---|---:|---:|---:|---:|
| CPU → AHD-only；CPU colour | 1.1265284 | 195 | 13650 | 1866 |
| CPU → colour-only；CPU AHD | 0.0000568852 | 0 | 10 | 31 |
| AHD-only → both；GPU AHD | 0.0000543743 | 0 | 9 | 30 |
| colour-only → both；GPU colour | 1.1265285 | 195 | 13650 | 1867 |
| CPU → both；兩階段均改 | 1.1265284 | 195 | 13650 | 1899 |

每份場景比較 98,224,128 個 RGB float，無非有限值；輸出 alpha 完全相同。完整六對分布、RMS、每通道差、最差座標與 hash 在 `stage-compare.stdout`。場景最大點 `(1697,225,G)` 的 CPU／GPU 值約 1.1542052／2.2807336；顯示最大點 `(1045,841,R)` 為 0／13650，不是同一個像素。

**能歸因到切換 AHD 路徑，不能宣稱已定位 AHD kernel 的錯誤分支。** 這裡比較的是後續高光／色彩／方向處理完成的場景，不是直接擷取 demosaic 輸出；非線性高光重建可能放大前面的微差。colour-only 也不是逐位元等價。仍不知道 CPU 或 GPU 哪側較正確；不改產品、不放寬容差。

### 10.2 E05：真正的兩模型 CPU／CoreML 推論

從同一 RAW 產生 200×133 線性 Rec.2020 RawNIND 輸入，以及同一來源經既有延遲顯示編碼 `RawDisplay.Encode` 的 sRGB NAFNet 輸入（不是另跑 Render16）。每個模型同時保留一個 CPU 與一個 CoreML processor，session 初次後每 provider 交錯 6 次；每次用等像素的新來源 identity，**全部 CacheHit=false、TileCount=1**。RawNIND 的 tile／border／core 是 512／64／384，NAFNet 是 768／64／640。

| 模型／provider | session 初次 wall ms，n=1 | 後續 wall 中位 ms，n=6 | 後續 CPU 中位 ms |
|---|---:|---:|---:|
| RawNIND／CPU | 439.891 | 287.661 | 1146.084 |
| RawNIND／CoreML＋CPU | 237.715 | 41.055 | 9.650 |
| NAFNet／CPU | 1711.916 | 1728.337 | 6416.283 |
| NAFNet／CoreML＋CPU | 2054.063 | 329.377 | 26.664 |

「初次」只指新 session，沒有清除 OS／CoreML 編譯快取，不是全機冷啟動。CPU time 是程序累計，多執行緒可大於 wall。CoreML 設 `AllowLowPrecisionAccumulationOnGPU=0`，但 provider 是 CoreML＋CPU；`aiproviders.stderr` 確實有 ORT 節點未指派 preferred EP 的警告，原樣保留。沒有逐 operator 裝置證據，不能等同所有工作都在 Metal GPU。

| 同模型第 0 筆完整未混合 neural RGB 比較，79,800 分量 | 最大絕對差 | 相對 L2 | 各 provider 六筆混合輸出 hash 穩定 | 最終顯示最大 RGB 差 LSB |
|---|---:|---:|---|---:|
| RawNIND CPU／CoreML | 0.00000190735 | 0.00000025495 | 是 | 1 |
| NAFNet CPU／CoreML | 0.000000417233 | 0.000000128249 | 是 | 1 |

顯示 alpha 完全相同；沒有已知乾淨實拍真值，不能因此判定任一模型／provider 的降噪品質較好。RawNIND 與 NAFNet 的資料域、模型、填補及 tile 都不同，不能直接比較其時間來排名模型。兩個 processor／模型域的存活期交疊，程序採樣 RSS 也不是各 provider 單獨的記憶體需求。

#### 完整 f32 磁碟控制

另一次真實 RawNIND／CoreML＋CPU full 推論完成 247 tiles、CacheHit=false；透過唯讀反射取得實際 `_cached.Rgb`（gain match 後、強度混合前），不是以來源或混合圖冒充神經結果。7008×4672×3 float 為 **392,896,512 B（374.70 MiB）**，SHA-256 `0f7570ddffdc830648a5777a914b8db2f172daf1773c33da8a65d3646f9d090f`。

| 完整檔案控制，各 n=3 | wall 中位 ms | 範圍 ms | 逐 byte roundtrip |
|---|---:|---|---|
| 開啟／f32 write／Flush(true)／關閉 | 428.813 | 263.111–507.370 | 三次完全相同 |
| 開啟／ReadExactly／關閉 | 49.891 | 39.537–51.341 | 三次完全相同 |

讀回 buffer 在計時外預先配置；OS page cache 未清除。格式為本機 little-endian IEEE754 f32 RGB，無壓縮／header；只是完整 I/O 控制，未實作來源／模型 manifest、原子發佈、容量淘汰、取消圍欄或重開後的混合／顯示流程。不能把「約 50 ms 讀回」寫成產品重開／切圖延遲或持久快取已完成。

### 10.3 E06：1／2／4 workers 改變 wall，不等於少做工作

CPU RAW 固定相同輸入／選項，三個 worker scope 各暖機一次，再按輪旋轉順序各 6 筆 Full；每次 source 離開作用域後做 full GC，沒有 GPU RAW。21 份含暖機的完整場景 hash 均等於上述 CPU 基準。

| RAW worker scope | wall 中位 s | 程序 CPU 中位 s | CPU 中位／wall 中位，平均核數 |
|---|---:|---:|---:|
| 1 | 5.728 | 5.732 | 1.001 |
| 2 | 2.984 | 5.852 | 1.961 |
| 4 | 1.654 | 6.235 | 3.771 |

這是單張、warm filesystem、受控 GC 的程序 CPU-time，不是互動時 OS 百分比取樣或硬上限。各組累計 managed 配置約 1.20 GiB／次、sampled RSS 約 1.26 GiB，並未因減少 workers 而少留一份完整圖。現行半核心預設上限為 4，本輪不調整預設，也不將更小 scope 當成通用效能優化。

### 10.4 E07：真實局部基底、失敗匹配與品質取捨

獨立原型用 alpha 加權的 log-luminance guided base：`u=log2((max(Y,0)+1/128)/0.18)`，Rec.2020 Y；weighted box 的 `a=var/(var+0.04)`、`b=mean-a*mean`，再加權平均係數求基底。邊界是 period-2n mirror；不是降噪的 reflect101。重建 baseY 後，僅對 baseY>0.18 使用 rational shoulder，RGB 同乘增益；其餘原樣複製。沒有硬夾取或移入上游 GPL／AGPL 原始碼。

半徑為短邊 2%／8%；512×341 對應 7／27 px，1600×1067 為 21／85 px。基底 identity 包含完整來源 SHA、尺寸、stride、半徑、epsilon、pivot、offset 及邊界；產品並沒有新增這種快取。

#### 預定實拍目標四組都不可達

固定 baseline 最高 10% 顯示亮度 ROI，以 Basic Highlights −50 的平均碼值為目標，容差仍為 2／65535。探針記錄 bracket 上界的結果為 `unreachable-floor-above-target`，不調整目標／pivot、換 ROI 或把固定強度冒充匹配。

| 尺寸／半徑 | 目標平均碼值 | bracket 上界平均碼值 | 高於目標 |
|---|---:|---:|---:|
| 512／2% | 44916.118 | 45116.209 | 200.091 |
| 512／8% | 44916.118 | 45171.348 | 255.230 |
| 1600／2% | 44840.201 | 45069.697 | 229.496 |
| 1600／8% | 44840.201 | 45125.103 | 284.902 |

這是該公式／搜尋上界的觀察，不是所有局部演算法都做不到的證明。512 的全域控制仍匹配到差 1.413 code；1600 的全域控制亦未達本輪目標。

#### 已知紋理／噪聲／邊界控制

96×64 fixture 含純黑、0.02 暗部、0.85±0.06 checker、HDR 3.0／0.3 階梯、0.1–2.0 ramp、飽和色、alpha 1／0.5／0.25／0 與 alpha=0 隱藏 RGB。固定 seed 20261026、RGB Gaussian σ=0.02；RMSE 只算 alpha>0 的 RGB，不混入 alpha 或隱藏色。local k=1 對 global 做線性 bright-region 均值匹配（容差 0.0001，與實拍顯示碼值目標不同），不是感光元件 SNR 或自然照片品質評分。

| 合成半徑／演算法 | 紋理振幅／輸入振幅 | noisy-vs-clean RGB RMSE | 亮側 overshoot，線性 Y |
|---|---:|---:|---:|
| 2%／local | 0.616 | 0.015635 | 0.001557 |
| 2%／global 等亮度控制 | 0.361 | 0.015357 | 0 |
| 8%／local | 0.579 | 0.015516 | 0.003845 |
| 8%／global 等亮度控制 | 0.361 | 0.015354 | 0 |

輸入 RGB RMSE 為 0.020097；local 保留較多 checker 振幅，此 fixture 整張可見 RGB 的 noisy-vs-clean RMSE 也較大，且階梯 profile 有反轉。**不能聲稱無 halo、保留全部細節或較佳降噪。** 兩個半徑的純黑、alpha／隱藏 RGB 不影響可見區、飽和色零分量、HDR／全部有限值與 apply 錯尺寸輸出緩衝區拒絕控制均通過；不代表完整色差或所有極端輸入皆安全。

#### 固定強度補測：重用與實際收尾可行，但不是品質通過

原 local 只會在匹配成功時量 apply／Metal，四組皆不可達所以該分支沒有執行。保留 v3／原收據，另按預先記錄的補測，以重用控制原已指定的 **k=1.75** 量四組，各 n=6；不重跑或改判匹配失敗。

| 尺寸／半徑 | 基底 build 中位 ms，原 local n=6 | fixed apply 中位 ms，n=6 | fixed CPU 收尾中位 ms | fixed Metal 收尾中位 ms |
|---|---:|---:|---:|---:|
| 512／2% | 9.405 | 0.571 | 12.436 | 1.794 |
| 512／8% | 9.254 | 0.546 | 12.492 | 1.778 |
| 1600／2% | 21.033 | 7.629 | 32.144 | 13.058 |
| 1600／8% | 26.804 | 2.705 | 36.150 | 10.800 |

基底 build 六筆包含該尺寸／半徑首次呼叫，不是 warm-only 統計；四組首次分別為 10.978／9.307／72.696／26.568 ms，逐筆獨立保存在收據。apply 則先在計時外執行 k=0／k=1.75，表列其後六筆；不混稱兩者為相同暖機狀態。

工作區／輸出配置在 apply 計時外；1600 的十個 float 工作平面另需 68,288,000 B、輸出 20,486,400 B，不是零成本快取。不同 mode／次序的小樣本不能解讀為半徑越大越快，亦未量全尺寸局部原型或 UI p95。GPU 列只是真實 Metal 的**既有 finisher**，局部基底／壓縮仍在 CPU；Render16 包含匯出入口的重新上傳與讀回。

四組 k=0 全 float identity；k=1.75 fresh／reused base 的完整 float SHA 與 CPU 收尾 SHA 完全相同，並與 v3 的 changed-k 控制重現。每組六筆 CPU／Metal 各自 hash 穩定、最大 RGB 差 1 LSB、alpha 相同、無 fallback。實際查看三張由 512 輸出生成的 256×171 PNG；不等亮度、小圖、未校準／未盲評，不用來宣稱細節或色彩勝出。

**裁決：不採用此局部原型替代 Basic。** 基底重用的數值可行與小量 apply 成本不會抵銷不可達匹配、階梯 overshoot、噪聲與舊設定外觀相容等問題。研究目標已實跑，負面結果就是結論；沒有改產品或調寬既有精度契約。

