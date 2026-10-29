# 可組合色彩轉換子引擎：研究與設計契約

- 日期／規格版本：2026-10-28，CT-2（審查修訂；取代未實作的 CT-1）。
- 狀態：**研究完成，可供實作決策；尚未加入產品。** 本次只寫研究／計畫與執行隔離實驗，不修改 renderer、schema、匯入 UI 或產品依賴。
- 原始碼基線：`ade108c9295c5aaad32b0094e01fce77c1600d92`。
- [實作計畫](../superpowers/plans/2026-10-28-composable-color-transforms.md)；現行產品契約仍為 [film-looks](../film-looks.md)、[Engine](../architecture/engine.md)、[VERIFY](../../VERIFY.md)。
- **DCP-1 補充**：使用者確認要支援 `.dcp` 相機描述檔；§7 與實作計畫的 D1–D5 是獨立 RAW 路徑，仍未實作。CT-2 的 film／CLF 契約和驗收維持 pending，不因 DCP 提案改寫。

## 1. Research Question / Direct Recommendation

能否不把原始 1D 曲線、矩陣及其順序重採樣成小型 RGB 3D LUT，而在 FilmBatch 現有 CPU／WebGPU Pipeline 內直接執行？

**建議：新增有界、資料驅動的色彩轉換子引擎；不重寫外層 Pipeline，不新增節點編輯器。** 先支援 Matrix、Range、一般 LUT1D／3×1D、LUT3D 的有限序列。以 managed C# 為 CPU 路徑，隨產品附帶的固定 WGSL 為 GPU 路徑；CLF 是此序列的一種輸入格式，不是引擎本身。

| 方案 | 優點 | 代價／限制 | 決策 |
|---|---|---|---|
| 保持現有 FBLUT／烘焙 3D LUT | 無新執行器，既有 LUT 工作流程不變 | 曲線重採樣、16-bit unorm、0–1 限制仍在 | 保留為舊資產路徑，不拿來承載新 chain |
| 有界 C#＋WGSL 子引擎 | 保留原節點、表格節點與順序；沿用目前 renderer、排程及 GPU 裝置 | 要自行維護小範圍解析／數學一致性 | **推薦**；本機數值實驗支持可行性 |
| 產品直接整合 OpenColorIO | 成熟的色彩轉換與檔案支援，標準覆蓋較廣 | C++ bridge、各平台原生產物；2.6.0 shader API 無 WGSL，仍不能直接接本案 GPU 路徑 | 暫緩；先作獨立 CPU oracle |

反方理由：完整標準支援通常比自製 subset 更可靠。這個建議只在 subset 足以涵蓋實際輸入、且能以獨立 oracle 驗證時成立；如果使用者必需的 CLF 多數含 Log／Exponent／其他節點，應擴充已測契約或重新評估 OCIO，而非把不支援的節點悄悄省略。

本計畫**不承諾** Sony Edit 等價、Sony RAW 引擎、任意私有查表演算法、float64 運算逐位一致、完整 CLF／ACES、全域 HDR 色彩管理，或把 NumPy 檔案變成產品匯入格式。

## 2. 現有契約與真正的缺口

已檢查 `Core/Film/{FilmLut,FilmLutFile,CubeLut,UserLutLibrary,FilmLookLibrary}.cs`、`Engine/Film/LutFileImport.cs`、`Engine/Rendering/{ModuleParams,CpuPipeline,GpuEffectPipeline,FilmLookProcessing}.cs`。

- `FilmLut` 是 float32 RGB 3D 表，red-fast：`((b*N+g)*N+r)*3`。`FilmLutFile.Write` 會把每個節點 clamp 至 0–1，再寫成 16-bit unorm；讀回仍已失去原精度。FBLUT1 為 sRGB，FBLUT2 攜帶 ProPhoto gamma 1.8 標記。
- `.cube` 已可直接匯入，但仍經上述持久化；只接受單一 3D 表、單位 domain，超過 65³ 重採樣。更換副檔名不會改變這個契約。
- 現有 film pass 位於**線性效果階段**，在 HSL、photo plate、highlight rolloff 及 RAW finishing 之前。它不是顯示端 look 的天然插入點。
- CPU 以浮點陣列運算；GPU 工作紋理為 `RGBA32Float`。外層 Pipeline、RenderView、分帶、取消、Preview／Loupe／thumbnail／export／CLI 已有共用途徑，可以重用。
- 顯示端的固定插入點：RAW 的 `RawDisplay.FinishLinear`／GPU `_rawDisplay.Dispatch`，或非 RAW 的 display encode **之後**、`BasicDisplay`／`_basicDisplay` **之前**。ProPhoto 輸出時，這裡仍是 extended sRGB 效果座標，不是 ProPhoto 像素。
- `film.look`、project、preset、undo／paste 保存的是資產 key，不內嵌 LUT。符合 `user-<16hex>` 格式但檔案不存在的舊 key，現行行為是保留 key 並跳過 film pass。
- `PhotoAnalysis` 只取早期線性調整。新增 display chain 不應讓取樣分析因缺失 look 資產而失敗。
- iOS `MobileWorkspace.ImportAssetAsync` 會先複製輔助檔；其 `CopyAsync` 目前沒有位元組上限。只限制後續 parser，不能宣稱已限制匯入檔案複製量。

**影響範圍：**新的 chain 模型與儲存、film 資產解析、CPU／GPU 的兩個固定 hook、匯入對話框及兩份語系資源、縮圖／分析解析條件、GPU 記憶體與生命週期，以及 scheduler 對新資產錯誤的重試分類。**不改：**RAW decode／demosaic／tone map、輸出 ICC、既有 built-in LUT 內容與 key、legacy FBLUT bytes、既有 LUT 的演算法與插入順序、排程優先順序、其他效果順序。

## 3. 設計契約 CT-2

以下是**待實作的決策**，不是現有功能清單。

### 3.1 同一 film 選擇，一個有界序列

沿用 `film.enabled`、`film.look`、`film.strength`、`film.warmth`、`film.fade`，不新增第二套獨立 look 模組。新的 `FilmLookAsset` 僅可包含 legacy `FilmLut` 或 `ColorTransform` 其中一種；不可兩者同時存在。

- Legacy 選擇走原 film hook，保持原演算法與 rounding。
- 新 transform 在資產 metadata 中固定 `WorkingLinear` 或 `DisplayEncoded` stage；只在對應 hook 執行一次。前者是現有 film 位置，後者是 §2 的顯示端位置。
- 輸入與輸出各明確宣告 `LinearSrgb`（D65）、`Srgb`（D65、sign-preserving extended transfer）或 `ProPhotoGamma18`（D50、純 gamma 1.8）。不提供任意 ICC、任意白點、camera log 或動態插入位置。
- wrapper 使用既有色彩矩陣／Bradford 來源。新 ProPhoto 數學 wrapper 用 `sign(x)*abs(x)^gamma`，不呼叫含 gamut reduction／clamp 的輸出 `EncodeProPhoto`。這是新 chain 的 extended 數學約定，不改既有 bounded ProPhoto LUT 或輸出 profile。
- stage 座標先轉到宣告的輸入；按順序執行節點；再從宣告輸出回到線性 sRGB 效果座標。沿用 warmth／fade 的線性後處理與線性 strength 混合。Display stage 最後重新 encode extended sRGB，包含 sRGB 最終輸出的情況；負 channel 可進入後續 basic／curves／tone，須有專門的 downstream fixture。這個混合域固定，不隨 CLF descriptor 猜測。
- `ModuleParams.From` 只有在 `resolveFilmAsset && FilmOn && FilmStrength > 0` 時解析資產；disabled／strength=0 必須真正 bypass，連資產解析與色彩 round-trip 都不做，輸入 RGB／alpha 原樣保留。啟用的 identity chain 仍可能因 f32 wrapper 有捨入；以獨立參考和既定容差驗收 RGB，alpha 仍逐位不變。合法 legacy render 結果不變。
- 原 alpha 原樣傳遞，不做 premultiply／unpremultiply；負值與 >1 值只在明確的 LUT 座標查找、Range clamp 或既有使用者開啟的 fade／輸出處理處受限制。不能把「無新增隱性 clamp」寫成「整條產品流程無損」。

### 3.2 CLF 接受集合與數學

基準為 [Academy CLF 3.0](https://docs.acescentral.com/clf/specification/)，加上明示的產品資源上限；不聲稱完整 SMPTE／CLF conformance。只接受非 namespace 的 `ProcessList`、`compCLFversion` 數值為 3.0、非空 id、嚴格 UTF-8（可有 BOM，原樣保存）。CLF 1.x／2.x、其他版本、帶 namespace 的處理節點、`.ctf` 匯入及 OCIO 輸出的 CTF-only 屬性均不在集合內，即使部分節點名稱相同也不自動轉換。

| 節點／項目 | 接受的契約 | 明確拒絕 |
|---|---|---|
| Matrix | row-major `3 3`、`3 4`；相容舊 `3 3 3`／`3 4 3` 尺寸寫法，最後一維須為 3；第四欄為 offset | alpha matrix、其他尺寸 |
| Range | 四界限須 `maxIn>minIn`、`maxOut>=minOut`；單側只接受成對 min 或 max、style 為省略／`Clamp`，且 `outBound=inBound*S(out)/S(in)`（以正規化 f32 相等檢查）；省略 style 等於 `Clamp` | 單側 `noClamp`、單側 offset、不完整 pair、倒置 bounds、未知 style；不採 OCIO 的寬鬆 fallback |
| LUT1D | 2–65,536 點，一欄 broadcast 或三欄 RGB；linear interpolation | `halfDomain`、`rawHalfs`、非 linear interpolation |
| LUT3D | 等邊 2–128；RGB 三分量；**blue-fast** `((r*N+g)*N+b)*3`；預設 trilinear，明示 tetrahedral 亦支援 | 非等邊、其他插值、超過產品上限 |
| Bit depth | `8i/10i/12i/16i/16f/32f`；相鄰節點的宣告深度須接續一致；全程 float32 | 任意節點間量化成整數或 half |
| Metadata | `Description/InputDescriptor/OutputDescriptor`、XML comments、`Info` 的有界 opaque subtree；ProcessList 的 `id/name/inverseOf`、ProcessNode 的 `id/name`、`xml:lang` 可接受但不參與運算 | 計算區域的未知屬性／節點、外部 Reference、非宣告 processing instruction；metadata 不決定 encoding、不啟用 inverse、不觸發 I/O |
| 其他運算 | 本集合之外全部 fail-before-apply | Log、Exponent、ASC_CDL、IndexMap、hueAdjust、inverse LUT、CTF-only nodes、外部程式／shader |

正規化尺度 `S(8i,10i,12i,16i)=(255,1023,4095,65535)`，`S(16f,32f)=1`。Matrix 係數乘 `S(in)/S(out)`、offset 除 `S(out)`；Range 的 in/out bounds 分別除對應尺度；LUT 值除 `S(out)`。解析時正規化一次，保留節點順序，不先 fusion、化簡或重採樣。

單側 Range 正規化後只有 `max(x,minBound)` 或 `min(x,maxBound)`，不能從一對 bounds 推導任意 offset。`Info` 是規格 metadata，不是自動執行的擴充；其內含 XML 即使名為 Matrix／Reference，也不加入節點或開啟檔案。全部 metadata 文字與屬性值合計最多 65,536 個 UTF-16 code units，仍受整體 XML 深度／檔案大小限制。

CLF blue-fast 不可直接 memcpy 到 legacy red-fast `FilmLut`。新 CPU／GPU 一律使用自身的 blue-fast flat buffer。LUT 查找的**輸入座標** clamp 到表格端點；表格的負／超白輸出不 clamp。此端點策略是明訂的 OCIO-compatible profile 政策，不能冒稱 Academy interpolation appendix 已完整規定所有超界情況。

插值用八角權重／常規六分支 tetrahedral，測 `r=b>g` 等 tie，不照抄 appendix 中有歧義的 strict-inequality catch-all。`halfDomain`、`rawHalfs` 不是普通 65,536 點表的別名，故不假裝支援。

### 3.3 保存與相容性

不新增「全部變成 FBLUT3」的遷移。`.cube/.fblut/Hald` 繼續走既有 `UserLutLibrary`，舊 bytes、key 與選擇不重寫。只有新 `.clf` 資產使用 `.fblook` envelope：

| Offset | Bytes | 內容 |
|---|---:|---|
| 0 | 8 | ASCII `FBLOOK1` 加 NUL |
| 8 | 1 | stage：0=`WorkingLinear`，1=`DisplayEncoded` |
| 9 | 1 | input：0=`LinearSrgb`，1=`Srgb`，2=`ProPhotoGamma18` |
| 10 | 1 | output：同上 |
| 11 | 1 | reserved，必須為 0 |
| 12 | 4 | uint32 little-endian 原始 CLF UTF-8 byte 長度 |
| 16 | N | 原始 CLF bytes，不重新序列化、量化或烘焙；不得有 trailing bytes |

- 新 key：`look-` 加 envelope 完整 SHA-256 的 64 個小寫 hex，含 stage／input／output；名稱不參與 hash。保存於既有 `AppPaths.UserLuts`，獨立掃描 `look-*.fblook` 與同 stem 的 `.name`。
- `.fblook` 也能直接匯入；沿用內含 declarations，不再顯示猜色域選單。按相同 bytes 重匯入得到相同 key。`.clf` 在顯示 descriptor／宣告 modal 前先複製到有界、本次匯入擁有且保持唯讀的暫存，背景解析該份資料；使用者確認後仍對同一份封存 bytes 驗證、hash、原子發布，避免預覽、parse、保存各讀到不同版本。取消時關閉 handle 並清理本次暫存。
- 以 `.fblook` 原子發布為匯入完成的界線：此前取消／驗證失敗只清理本次擁有的暫存，不發布資產；此後若 UI context 已失效，已發布的合法內容可保留，但不得套用到新 selection／project、建立 undo 或刪除共用資產。背景匯入使用啟動時捕捉的 library root，不能在發布時重新讀取可能已切換的全域 root。
- 新資產原始 CLF bytes 可還原不代表其 float64 小數在 GPU 逐位無損。計算契約仍為 float32。Sony 私有表格／演算法不能只套 envelope 就變成可執行的 CLF。
- Settings 的 `film.look` choice 僅擴充識別新 `look-<64hex>` 格式；其他任意字串仍遵守既有規則。`FilmLookLibrary.Resolve` 的 null 只表示舊／未知非 `look-` key 的 bypass；啟用的新 key 缺檔／損壞／hash 不合時，統一將檔案、解析、驗證失敗包成 `ColorTransformAssetException`（保留原始原因；取消例外不包裝）。Scheduler 對這個特定錯誤直接報既有 render／thumbnail／export failure，不重建 GPU，也**不能默認原圖成功**；不改其他 legacy I/O 重試分類。
- 新資產只按需解析；不可在任何 built-in resolve 時解析所有新檔。清單只讀 bounded name 與 16-byte header，損壞 header 顯示無效項目而不毒化其他資產；完整 hash／chain 驗證仍在 Resolve。先只留一個 recently-resolved strong cache entry（root＋key），renderer 使用中的不可變 snapshot 自行持有；不另造無界全域 cache。

### 3.4 資源與信任邊界

產品上限：CLF 原檔 **128 MiB**、`.fblook` **128 MiB＋16 bytes**；最多 **64 nodes**；XML 深度 **16**；LUT1D 最多 **65,536** 點；LUT3D 最多 **128³**；正規化後全部參數／表格合計 **32 MiB**。128³ RGB float32 為 24 MiB；三通道 65,536 點表為 768 KiB。這些是待實作的產品限制，不是已測得的效能數字。

使用 streaming XML／數字解析，停用 DTD、entity expansion、外部 resolver；checked dimension multiplication、精確 token 數、有限數值、known enum。超限在大型配置或行動端複製超限前拒絕。不得讓 XML metadata、路徑或未知節點控制檔案、網路或執行程式。未知 version／reserved bits 必須拒絕。

GPU 使用固定 shader、一個 dispatch 執行整條有限序列，不逐節點 CPU readback。op records 以 4×u32／16-byte stride，tables 以 flat f32 storage buffer；不使用 legacy `N²×N` texture trick。Host 驗證所有 offset／length／span，checked byte 計算、裝置 storage binding limit 與既有 GPU budget 必須包括新 ops/data buffers。先只保留一組 device-local chain buffers；完成在途工作後才能替換／釋放，device loss 後不可重用舊 handles。超裝置限制走既有整張 CPU fallback，不分段改變演算法。

非有限來源參數拒絕；有限 f32 運算的 overflow／病態條件數不是可攜逐位一致的保證範圍。數值驗收以封存 fixture 與誤差規則為準；若常見有效輸入造成不穩定，必須修正或縮小並明示接受集合，不能只放寬測試讓它過。

## 4. 已執行的隔離實驗

### 4.1 環境、輸入與 oracle

2026-10-28：Apple M3／Metal，macOS 26.6.2（25G83），SDK 10.0.402、runtime 10.0.13，WebGPUSharp 0.5.7／Dawn。實驗直接呼叫現有 `GpuDevice`／buffer／dispatch API；**不是產品 film hook 或檔案匯入實作**。

- Chain：Matrix → 4097×3 LUT1D → noClamp Range → Matrix；1D table 49,164 bytes，op record 16 bytes，一次 dispatch。
- 樣本：8193 個 neutral ramp＋8192 個 `Random(20261028)` RGB＋8 corners＋3 extended inputs，共 16,396；3D 烘焙比較只用其中 16,393 個單位立方體樣本。
- double reference：對相同 **float32** 參數／表格以 double 求值，不是原始 float64 作者數據的 oracle。
- 獨立 oracle：PyOpenColorIO **2.6.0**、CPython **3.13.0**／arm64，讀取產生的 CLF；`OPTIMIZATION_NONE`、`INTERP_DEFAULT`，使用實際回傳的 Python list，不假定 list 會原地修改。

| 執行路徑 | RGB 最大絕對差 | 比較基準／樣本 |
|---|---:|---|
| C# float32 原生 chain | 1.7303333121887476e-7 | double reference／16,396 |
| WGSL float32 原生 chain | 1.1667272969972942e-7 | double reference／16,396 |
| C# float32 原生 chain | 1.1920928955078125e-7 | OCIO 2.6.0／16,396 |
| WGSL float32 原生 chain | 1.7881393432617188e-7 | OCIO 2.6.0／16,396 |
| 烘焙 33³ float 表 | 0.03180333465335147 | double reference／16,393 |
| 烘焙 33³ FBLUT unorm16 | 0.03181088954207675 | double reference／16,393 |
| 烘焙 65³ float 表 | 0.022495883621259483 | double reference／16,393 |
| 烘焙 65³ FBLUT unorm16 | 0.022501866437478812 | double reference／16,393 |

全部 chain 路徑 alpha exact。獨立 noClamp probe 保留 `[-1.25,-0.25,2.75]` 與 `[0,1.25,3.75]`，最大差 0。`1.00000001` 轉 f32 得 1，直接示範「保存原始資料」不等於「f32 計算逐位無損」。

另以 OCIO 執行非對稱 2³ CLF：輸入 `(0.25,0.5,0.75)`，省略插值得到 `(0.125,0.375,0.1875)`，明示 tetrahedral 得到 `(0.25,0.5,0.25)`；tie fixture `(0.8,0.2,0.8)` 得三通道約 `0.600000024`。這確認本次 oracle 的 default 與 tie；**沒有驗證未實作的 FilmBatch CLF parser／新 3D kernel**。

這是刻意具有曲率的合成例子，證明有一類轉換保留 1D chain 可避免立方體烘焙誤差；不能外推成所有 LUT 的品質提升比例，也不是 Sony 準確度或速度 benchmark。

### 4.2 證據保存與重播

本機、gitignored：`artifacts/verification/2026-10-28-color-transform-probe.tar.gz`。

- SHA-256：`18cac03c8a5344f54e9d3d2ac94fe0e6d972f9f886bda5ab7a43da81bd4ddc04`。
- 11 個純文字 members：driver／project、oracle script、安裝紀錄、兩份 JSON report、四份 synthetic CLF、逐檔 SHA256 manifest。已檢查 member 路徑與 manifest digest；不含照片、Sony tables、套件、binary、private absolute paths 或個人設定。
- driver SHA-256：`24ddb161e578708984ae769804d0714fe725749e443a1b0af9ac13811732a021`。
- 檔案僅在這台工作站保存；git clone 不會自動取得它。需要跨機重播時應交付上述 archive 並核對 digest，不能以文件數字冒充執行證據。

以下為實際使用的命令形狀（`work` 換成解壓目錄；本機 dotnet 使用 `$HOME/.dotnet/dotnet`）：

```bash
dotnet run --project "$work/ColorProbe.csproj" -c Release -p:FilmBatchRoot="$PWD" -- "$work/evidence"
python3.13 -m venv "$work/oracle313"
"$work/oracle313/bin/pip" install --only-binary=:all: opencolorio==2.6.0
"$work/oracle313/bin/python" "$work/oracle.py" "$work/evidence"
```

首次 `python3` 為 3.15.0，pip 改走 sdist build，240 秒逾時；那次沒有 oracle 結果。改用已安裝的 3.13.0、強制 binary wheel 後成功。PyPI 報告的 wheel hash 與來源 pin 保存在 setup-note；未宣稱已本地重建該 wheel 並驗證 source-to-binary 對應。

CT-2 審查時另從封存檔解壓至隔離暫存重播：source HEAD 為上列基線，Core／GPU 與共用 build props 無未提交變更；Metal driver 與 OCIO 命令均成功，四個 CLF digest 及 OCIO JSON 報告與封存內容一致。這仍是同一合成資料集的重播，不是新增產品或跨平台驗收。

## 5. Evidence Used / Version Context

| 主張／項目 | 一手來源／檢查方式 | 狀態與限制 |
|---|---|---|
| 既有 LUT 量化、hook、key 行為 | §2 列出的基線檔案與現行架構文件 | verified：現行程式契約；不是新功能實測 |
| 原生表格可在現有 Metal API 一次 dispatch 執行 | §4 driver、input/data digests、JSON reports | verified：僅上述合成集合與裝置 |
| 不應以自製 double oracle 作唯一正確性證據 | 相同 CLF 由 OCIO 2.6.0 實際讀取及執行 | verified：共同集合；新 parser 尚不存在 |
| CLF 節點、尺度、順序、blue-fast | [Academy 規格](https://docs.acescentral.com/clf/specification/)／[指南](https://docs.acescentral.com/clf/guides/) | verified：來源文字；CT-2 僅 subset |
| OCIO 2.6.0 tag identity | [GitHub ref](https://api.github.com/repos/AcademySoftwareFoundation/OpenColorIO/git/refs/tags/v2.6.0)：`10a43ec1df03d072ca9a9e2fb3f26e454b0900d8` | verified，2026-10-28；升級時重查 release 與 fixture 結果 |
| OCIO 無現成 WGSL shader target | [該 pin 的 GpuLanguage enum](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/10a43ec1df03d072ca9a9e2fb3f26e454b0900d8/include/OpenColorIO/OpenColorTypes.h#L465-L480) | verified：列有 GLSL/HLSL/MSL 等，無 WGSL；不代表未來版本永遠沒有 |
| OCIO 可作產品依賴但需額外工程 | [BSD-3-Clause](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/10a43ec1df03d072ca9a9e2fb3f26e454b0900d8/LICENSE)、[bindings build](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/v2.6.0/src/bindings/CMakeLists.txt) | needs-qualification：授權可用；本案 C#／iOS build、bridge、GPU 整合未驗證 |
| LUT 超界端點及 Range 寬鬆解析差異 | OCIO [LUT1D](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/v2.6.0/src/OpenColorIO/ops/lut1d/Lut1DOpCPU.cpp)、[LUT3D](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/v2.6.0/src/OpenColorIO/ops/lut3d/Lut3DOpCPU.cpp)、[reader](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/v2.6.0/src/OpenColorIO/fileformats/ctf/CTFReaderHelper.cpp) | verified：source inspection；不拿寬鬆 acceptance 代替 parser 規格 |
| 可攜 WGSL 不是 float64 計算平台 | [WGSL draft](https://gpuweb.github.io/gpuweb/wgsl/)，檢查版本 commit `a1a1e79f35f9a3d6db255dc170621ae7411eba0`，§6.2.4／§15.7.7 | verified：concrete f32/f16，允許 reassociation/fusion；draft 版本升級需重查 |
| 新引擎實際效能／Windows／iOS 可用性 | 尚無產品整合或相應裝置量測 | unknown：實作 release gate，不是本次 PASS |

Academy 頁面保留其 2025-09 標準化說明；OCIO 2.6.0 release 則宣告支援 SMPTE ST 2136-1。此處不把公開 committee draft／XSD 當最終標準全文，也不宣稱獲得規格認證。若未來加入 Log，需先解決 Academy prose、public draft XSD 與 OCIO default base 的版本差異，不能直接照其中一份猜測。

## 6. Evidence vs Inference / Risks / Handoff

**已驗證：**原生 1D＋矩陣序列可用目前 C#／WGSL API 執行；指定合成集合的誤差與 alpha；OCIO 的上述 CLF oracle 行為；現行 Pipeline／FBLUT 的限制。

**[INFERENCE] 建議：**有界子引擎是比「整條 Pipeline 泛化」更小的改動，且比現階段直接引入 OCIO 原生依賴貼近現有 C#／Dawn 架構。這不是已量測的維護成本、效能或完整色彩正確性結論。

尚未驗證的產品風險：兩個 hook 與所有輸出色域的一致性、真實 RAW／raster end-to-end、Windows DX12 rounding、iOS CPU／匯入 UI／記憶體、cancel／device-loss／banding、64-node 和大型 LUT 效能，以及真實 CLF corpus 的覆蓋率。

**停止研究的理由：**推薦方案的關鍵可行性已有執行證據；標準／資產／stage 的必要契約已固定；未測項目均是可觀察的實作 gate，沒有拿假定產品能力補齊。下一步按計畫做 CT-2 的垂直切片；所有步驟目前未勾選。若它無法通過獨立 oracle、舊資產 regression 或跨平台 gates，就不能把本研究當成產品完成證明。

審查修訂：CT-1 的計畫／驗收宣告作廢並以 CT-2 全部重新列為 pending；補明單側 Range、惰性 metadata、版本限制、錯誤分類、取消發布界線及切片 ownership。§4 的原始實驗／digest 沒有改寫，仍只證明其明列的合成集合，不被升格為 CT-2 產品驗收。

## 7. DCP-1：RAW 相機描述檔擴充

### 7.1 問題與決策

**可以規劃支援，但目前不能匯入獨立 `.dcp`。** DCP 是相機輸入描述檔，不是 CLF 或 RGB 3D LUT：它決定 RAW 白平衡、相機轉換、光源插值、HSV 描述表及預設色調。推薦在 RAW 顯影前選取它，再讓結果走現有 film 流程；不把相機資料塞進 `film.look`，也不把它烘成 FBLUT。

| 方案 | 決策與原因 |
|---|---|
| 轉成既有 `.cube/.fblut` | 不採用：失去相機白平衡／矩陣插值，也無法保證 HSV 表與色調曲線的原始語義 |
| 在 RAW 路徑增加有界 DCP 讀取與選擇 | **推薦**：重用既有 `CameraColor`、`ProfileTables`、CPU／GPU 顯影；須補嚴格驗證、白平衡重算、曲線與快取 |
| 直接導入外部 RAW／Adobe 引擎 | 不在本計畫：平台、授權及顯影差異均未驗證 |

現行 `Dng.ColorData` 只從影像自身讀取部分 profile tags；`TiffFile.ReadHeader` 不接受相機 profile magic `0x4352`。`Dng.ProfileLook` 對損毀欄位會忽略，不能直接當獨立檔的嚴格驗證器。現行 `RawWhiteBalance` 將已套用的 profile 排除在後續白平衡矩陣之外；非線性表與白平衡一般不可交換。`RawSourceLoader.DngTone` 又把曲線降為 32 點並限制末值，不能宣稱保留原曲線。

2026-10-28 以兩份各 26 bytes 的合成檔呼叫現行 `TiffFile.Parse`：IFD offset＝8、entry count＝1，tag 50778 為 SHORT、count＝1、值＝21，末尾 next offset＝0。little-endian `IIRC` 與 big-endian `MMCR` 都因 header 被拒；僅在**實驗副本**把 magic 換為 TIFF 42 後，兩者均讀到 21。這只證明 header 差異與低層 IFD 可供評估，**不是 DCP parser 或顯影驗收**。本機未找到 `exiftool`、`rawtherapee-cli` 或 `dcpTool`；本回合未執行獨立 DCP 渲染 oracle。

### 7.2 DCP-1 接受與拒絕範圍

此集合是產品提案，不是完整 DNG／DCP 相容宣告。依 [Adobe DNG 1.7.1.0 規格](https://helpx.adobe.com/content/dam/help/en/photoshop/pdf/DNG_Spec_1_7_1_0.pdf)（2023-09；本次另核對[可讀副本](https://github.com/user-attachments/files/22010027/DNG_Spec_1_7_1_0.pdf)）：

| 部分 | DCP-1 接受與處理 | 明確拒絕 |
|---|---|---|
| 容器 | `II`／`MM`、magic `0x4352`、一個完整 classic IFD、offset 相對檔首；32-bit count／offset 先做 checked 邊界檢查 | BigTIFF、截斷、重複 tag、子／後續 IFD、offset 或型別／數量錯誤、未知 tag；不可把 TIFF parser 的寬鬆忽略當成功 |
| 相機與矩陣 | 有 `UniqueCameraModel`；三色 `ColorMatrix1`，可選第二光源／第二矩陣與對應 `ForwardMatrix1/2`；有限、可逆及成對條件須驗證。可辨識的單光源未知碼允許不插值；雙光源須均為現有可換算且不同的標準光源 | 三光源、自訂 illuminant、非三色／ReductionMatrix、不完整 dual 配對與病態／不可逆矩陣 |
| HSV 與曝光 | 0–2 個 `ProfileHueSatMapData` 與可選 `ProfileLookTableData`；原始 float32 表不烘焙；維度／數量、零飽和列、有限值與 encoding 0／1 均先驗證；`BaselineExposureOffset` 加在**影像自身** BaselineExposure 上一次 | 未知 encoding、表格缺件、非法維度／數值、超限及 HDR 變體；不得靜默只套部分 profile |
| 色調 | SDR `ProfileToneCurve` 可缺省；存在時須有 `(0,0)`、`(1,1)`、嚴格遞增 x 與有限 0–1 y。直接保留原節點，以既有 `ToneCurve.Eval` 的三次插值契約及對應 WGSL 計算，不經 32 點 `CameraTone` | 超限、錯誤端點／非有限資料；不宣稱與 Adobe 的未指定 spline 或整個顯影外觀逐位一致 |
| 其他 | `ProfileName`、`ProfileCopyright`、`ProfileCalibrationSignature`、`ProfileGroupName` 按欄位類型驗證；`ProfileEmbedPolicy` 只接受 0–3，`DefaultBlackRender` 只接受 0／1。`ProfileDynamicRange` 可缺省，存在時僅接受 Version=1、DynamicRange=0、有限的 HintMaxOutputValue ≤1（≤0 表示無 hint）；保留原始 bytes | 不合法 enum／hint、HDR `ProfileDynamicRange`、外部 DCP gain-table map、RGBTables、未知語義及其他未列 tag；不可忽略未知 tag 後宣稱成功 |

上限：原始 DCP **64 MiB**；單 IFD 最多 **256** entries；每個 HSV 表至多 **1,048,576** 格、總解析資料至多 **40 MiB**；色調最多 **65,536** 對點。先驗長度／乘積再配置；動態 GPU binding 與現有 budget 仍須驗證。這些是待實作的產品上限，不是既有能力。

### 7.3 套用、保存與錯誤契約

- 新的 `raw.camera_profile` 每張照片預設 `auto`，或是 `dcp-<原始檔 SHA-256 64 個小寫 hex>`。`auto` 表示現行 RAW 起始顯影（可能來自內嵌資料、JPEG 擬合或光譜 fallback），不保證檔內有描述檔。它是 RAW 來源選項，不是 film 選項；停用 RAW 模組時保留設定但不載入外部檔。一般 JPEG／PNG／TIFF 不套相機 profile，也不因跨格式貼上而丟棄設定。
- 從選取檔複製有界 owned temporary，對同一份 bytes 嚴格驗證、hash、原子發布原檔；相同內容同 key。顯示名稱不影響 key。每次新來源／匯出工作使用已驗 key 對應的不可變 snapshot；缺檔、損壞或 hash 不符為可見錯誤，不退回 `auto` 並假稱成功。取消與 project／selection 切換沿用既有匯入世代檢查，不能套用至新選取。
- 比對 profile 的 `UniqueCameraModel` 與來源身分：DNG 優先保留並使用原始 `UniqueCameraModel`；其他 RAW 使用 Make＋Model，若 Model 已含 Make 不重複。僅去首尾／合併空白及 invariant 大小寫比較；無法確認或不符則該 RAW 可見失敗，不猜跨機型相容。需補資料欄位：現有 `MetadataReader` 在 Model 存在時會丟失 UniqueCameraModel。
- 替換 profile 的矩陣、光源、HueSat／Look、tone、曝光 offset；保留影像的 sensor black/white、AsShotNeutral／AsShotWhiteXY、AnalogBalance、鏡頭與原始影像 metadata。來源 `CameraCalibration` 只有相機及新 profile 的 calibration signature 完全相同（缺省均為空字串）才使用，否則使用 identity；現行資料模型須補保留 signature。影像 BaselineExposure 與原 profile offset 須分開，不能把舊 offset 疊到新 offset。
- 局部 gain map 不得一律清空：DNG 1.7.1 的優先序為所選 Camera Profile IFD 的 `ProfileGainTableMap2` → 影像 IFD0 的 `ProfileGainTableMap2` → Raw IFD 的 `ProfileGainTableMap`。DCP-1 拒絕外部 profile 自帶 gain map，因此沿用來源 map 並遵守現有 `raw.camera_local_tone` 開關；現有 reader 對相容位置的實際順序仍是 IFD0 v2 → Raw IFD v2 → Raw IFD v1 → IFD0 v1，不因本功能改變。須測每個候選、同時存在及均無 map，不能混淆來源 map 與外部 profile map。
- 外部 profile 選定時，不套原本的 JPEG 擬合 camera look／DNG tone fit；在相機白平衡、highlight reconstruction 與 profile tables 之前決定 profile。每個 `DecodeLease.CreateRaw` 持有獨立 `ColorData`；建立新的 `ProfileLook` 並附回來源 gain map，將這份有效資料提供給 `StartingColour`、`ProfileTables.Create`、tone 與 `CameraLocalTone`，不得修改 `ColorData.Clone()` 淺層共用的舊 `ProfileLook`。自訂白平衡重新依選定光源重顯影並重建表格；不再對已經套過非線性表的結果只乘 WB 矩陣，也不能第二次套白平衡。profile key 與相關 WB 值進入來源選項／快取與 stale-result 比對；`auto` 正規化成沒有外部 key 的既有來源選項，重用 RAW sample 但不得跨 profile 共用錯誤的 `ColourFit`。
- 外部 DCP 只接受 SDR；依 DNG 1.7.1「Applying the Hue/Saturation/Value Mapping Table」第 8 步，HSV 明度乘倍率後須限制至不大於 1。既有 embedded `HsvTable.Limited` 對超白保留 headroom，不可把其行為直接當作 DCP SDR 參考；新增的 SDR 策略只套用於外部 DCP，CPU／GPU 對相同獨立超白 fixtures 驗證，不能改變 legacy embedded 行為。RAWmakase 0.4.6 在部分超白輸入也保留 headroom，僅以它驗證雙方相同行為域，不作此處 SDR 規格 oracle。
- 白平衡色溫／色偏與手動、automatic 建議仍須可用：保留 as-shot metadata；`PhotoAnalysis` 的一般取樣讀選定 profile 的已顯影來源，白平衡候選則從可重用的 RAW 感光／幾何快照，在候選白點下重算 highlight、相機矩陣及 HSV 表，再接續現有 sample adjustments；不得用已套非線性 profile 的像素只乘 `MatrixFor` 近似。候選 sample 數仍受 `MaxSamples` 上限，資源只在求解期間持有並受取消／世代保護；與實際候選白平衡重新顯影的 sample 對照，單獨記錄延遲與峰值記憶體。不能為了支援 DCP 靜默停用現有建議功能。
- DCP tone 存在時，在現有 RAW 顯示收尾以線性 Rec.2020 亮度直接求原始 spline；SDR 查表輸入夾到 0–1，亮度非正時輸出黑，RGB 以結果／原亮度縮放後沿用既有 highlight／gamut/output 步驟，alpha 不變。identity tone 仍是明確指定，不可觸發自動 JPEG tone fit；tone 不存在時使用既有預設收尾。這是 FilmBatch 的具體呈現契約，非 Adobe 外觀等價承諾。
- `DefaultBlackRender=1` 不做**額外顯影黑點／flare subtraction**，但 RAW sensor 黑階正規化仍必須執行；`0` 沿用 FilmBatch 自己的預設收尾，不推稱 Adobe 的黑點決策。DCP 的 HDR、額外 embedded profile 選擇、Sony XMP RGBTable、`raw convert` profile 嵌入與跨主機外觀一致均不在 DCP-1。
- `ProfileEmbedPolicy` 缺省為 0。依規格，0／1 不用來處理非 DNG；2／3 可在 stored profile 的適用範圍處理，2 禁止新嵌入。保留 policy／copyright，對用途不符明確拒絕；不靠改檔或更名繞過。policy 不是第三方授權證明，亦不代表可再散布。普通 rendered DNG 是已顯影輸出，不宣稱嵌入相機 profile。

### 7.4 證據、限制與交接

| 項目 | 證據／狀態 |
|---|---|
| 相機 profile 容器、signature、policy、HSV／tone 與 HDR | 上列 Adobe 1.7.1.0 §§ExtraCameraProfiles、CameraCalibrationSignature、ProfileEmbedPolicy、ProfileDynamicRange、Mapping Camera Color Space；verified 為規格文字，不是產品符合性測試 |
| 既有可重用與缺口 | `Dng.cs`、`RawImage.cs`、`CameraColor.cs`、`Profile.cs`、`RawSourceLoader*.cs`、`RawWhiteBalance.cs`、`ImageLoader.cs`；verified 為 source inspection |
| Sony A7M5 範例 | 釘選的 [ACR generator](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/camera_raw/acr_profiles.py)／[DxO generator](https://github.com/751K/sony-a7m5-looks/blob/d6e014b65d5103a0f094b1dad90a38bdc8c545d2/dxo/dxo_profiles.py)：DCP 是 base，ACR 外觀另在 XMP RGBTable，DxO 外觀另在 `.cube`；source 寫 policy 0，並使用 Adobe Standard 矩陣 |
| 實際樣本結構 | 本機唯讀檢查上游產生的 ACR base（538 bytes，SHA-256 `33c829b62b60a1a578a36ffb50cf04722423d90a158b2246b94c0adf6c2b0991`）與 DxO v2（540 bytes，`ad6378a426925beddd547cc9c8c02dd52d9b94062d0a45ad48d15cf580ba800a`）：各 12 tags、`IIRC`、policy 0、tone `[0,0,1,1]`、DefaultBlackRender 1；只檢查結構，未散布檔案、未檢查使用權、未渲染驗收 |
| DNG 專利授權 | [Adobe DNG Specification patent license](https://helpx.adobe.com/camera-raw/desktop/dng-and-file-formats/digital-negative.html#dng) 將權利限定於符合 DNG 規格的影像檔讀寫，並排除其他檔案格式的實作；依該授權散布的 Compliant Implementation 須在原始碼和文件顯著顯示 `This product includes DNG technology under license by Adobe.`。是否適用既有 DNG 程式及 standalone DCP 是待維護者／法律審查的問題，不能視為 DCP、第三方 profile 或其他專利的一般授權；發布前依審查結果處理聲明 |
| 精度／覆蓋／平台／授權 | **unknown**：獨立相機 profile oracle、真實 corpus、Sony/Adobe 權利、Metal／DX12／iOS、WB 重顯影效能與最終畫質均須 D1–D5 實測；CT-2 的 OCIO 實驗不能代替 |

Sony 範例的 policy 0 不能拿來直接處理 ARW；只匯入 base DCP 也不等於得到 Sony 12 種外觀。既有 [Sony survey](sony-creative-looks-survey.md) 的 NO-GO 與授權限制不因這份計畫改變。DCP-1 的實作步驟和逐項 AC 見[擴充計畫 §6](../superpowers/plans/2026-10-28-composable-color-transforms.md#6-dcp-1--raw-相機描述檔擴充)；所有步驟 pending。
