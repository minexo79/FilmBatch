# 建置、測試與發佈

依任務找文件見[文件地圖](README.md)；完整的執行順序、失敗分類與證據邊界見[驗證入口](../VERIFY.md)。

跨平台編解碼／色彩差異的固定輸入、分層對照、歷次反例與證據保存方式，集中於[跨平台影像實驗手冊](cross-platform-imaging-experiments.md)；以下的歷史通過結果只對當時版本與實際平台有效。

## 需求

- Windows 10 / 11 x64；macOS 封裝的最低版本依實際原生相依檔計算。重建 Dawn 後，Apple Silicon 的相依下限為 14.0（不再提供 Intel Mac 建置）；**目前執行驗證僅涵蓋 macOS 26，較舊系統仍需實機驗證**
- 從原始碼建置需要 .NET 10 SDK（版本鎖定於 `global.json`）；發佈版已內附執行階段，不需另外安裝 .NET
- Windows 使用 DX12 / Vulkan，macOS 使用 Metal；沒有可用 GPU 時自動改用 CPU 參考算圖器
- macOS 封裝需在 Mac 執行，使用系統的 `sips`、`iconutil`、`codesign`、`ditto`
- 桌面 GUI 不啟用 trimming：Avalonia／Dock 的反射路徑需要完整組件；iOS 的裁剪設定與 Avalonia registrar 例外另見下節，不以壓掉警告宣稱相容

## 建置與執行

```bash
dotnet build FilmBatch.sln
dotnet run --project src/FilmBatch.App
dotnet run --project src/FilmBatch.App -- --gallery   # 元件展示視窗
```

## 色彩調整

### 色調曲線

位置：**照片基本參數 → 色彩 → 色調曲線**，與 HSL 集中於同一區塊。預設為恆等曲線，不改變原有照片外觀；不需 AI 模型或額外下載。

- **RGB／R／G／B**：選擇主曲線或個別通道，在圖上點選新增控制點、拖曳調整；雙擊內部點或拖出圖的上／下緣可刪除，端點保留。下方顯示輸入／輸出讀值。
- **線性／中等對比／強烈對比**：套用目前通道的點曲線，套用後仍可編輯。「重設」只重設目前通道。
- **分段**：調整亮部、亮色調、暗色調、暗部；展開「分段界線」可調三個分界。交叉分界會在計算時排序並保持間距；「重設」一次還原四段與分界。
- **曲線飽和度**：100 保留曲線後的色彩，降低時以曲線後亮度為中心，將飽和度往曲線前拉回；不是一般飽和度滑桿。

處理順序為分段 → RGB → 個別通道，發生在 RAW 既有顯示映射之後，不取代相機色調／場景線性暗部還原。拖曳預覽不寫專案，放開一次保存、一次復原；通道切換、預設、模組貼上、專案重開與批次匯出共用同一組參數。白平衡／膚色／Zone 取樣時暫停曲線，避免創意調色影響取樣。

回歸入口：

```bash
dotnet test FilmBatch.sln -c Release --filter 'FullyQualifiedName~ToneCurve|FullyQualifiedName~CurveEditorTests|FullyQualifiedName~SamplingView_ToneCurvesDoNotChangeTheSampledPixels'
```

原生介面、全尺寸 PNG16 與合併後驗證紀錄另列於[本輪 pipeline／曲線驗證](superpowers/plans/2026-10-22-performance-correctness.md#pipeline-revalidation-2026-10-23)。CPU／Metal 檢查不代替 Windows／iOS 實機驗收。

### HSL

位置：**照片基本參數 → 色彩 → HSL**。七個內建色相預設使用文字下拉，上方不再有預覽圖；下方選紅／橙／黃／綠／青／藍／紫／洋紅，調整該帶的色相、飽和與明度。多選照片可一起套用，預設一次復原，滑桿沿用既有手勢／復原／覆寫標記。

這是獨立的 `hsl` 模組，不需要啟用底片，`film.strength = 0` 也不會停用 HSL；底片風格的真實縮圖仍保留在底片效果分頁。HSL 關閉或全零時不改畫面，白平衡／膚色／Zone 取樣會暫停它。舊 `film.hue_*` 讀入轉成 `hsl.hue_*`；因處理位置改在底片混合之後，舊的底片加色相組合可能改變外觀，詳見[相容規則](film-looks.md#7-舊專案相容)。


## 局部暗部還原

這是獨立的 `shadow_recovery` 模組，**預設關閉**；與「陰影」／`basic.shadows` 的顯示域調整分開。後者在 2026-10-23 修正抬黑與暗部洗白，採共同 RGB 增益；不是把本模組移到另一個滑桿。適用於 RAW 與一般 RGB 來源，不需要 AI 模型、下載或 LibRaw 相依。最終 JPEG／PNG／TIFF 仍為既有的顯示輸出，不是 HDR 感光資料。

1. 在基本參數的「暗部還原」展開並啟用；若找不到，先取消「隱藏已停用的模組」。
2. 以「暗部提亮」調亮暗處，再調局部範圍；亮度降噪保守使用，以免抹掉紋理，彩色斑塊可另調色度降噪。
3. 拖曳一次可整段復原／重做；設定沿用預設、複製貼上、逐張保存與批次匯出。專案格式仍為 3，不需遷移或重新解碼成另一種檔案。
4. 比較細節請看 100%／實際匯出，不只看縮小預覽。白平衡／Zone 照片取樣會暫停此局部映射，接受提案後恢復。

| 控制項 | 預設 | 範圍與含義 |
|---|---|---|
| 暗部提亮 | 3 EV | 0–5 EV；局部基底決定實際增益，不是整張固定曝光 |
| 局部範圍 | 2% | 裁切後短邊的 0.25–10%；不是降噪視窗大小 |
| 亮度降噪 | 35% | 0–100%；增益前混合暗部亮度差，可能犧牲細紋 |
| 色度降噪 | 80% | 0–100%；增益前混合彩色差，與亮度強度分開 |

演算法在未夾取的工作線性 sRGB 上，以覆蓋率加權 guided filter 求局部基底，先降噪再乘共同 RGB 增益；不加黑位偏移、不改 alpha，全透明隱藏色不污染鄰近像素。高光仍交原有顯示映射處理，不能恢復拍攝時已完全裁切的資訊。局部半徑按影像比例縮放，降噪按原始來源像素足跡縮放；很小的預覽可能減弱或跳過次像素降噪，並非和全圖逐像素相同。兩種有效降噪與提亮都為零時直接略過 CPU／GPU pass。

## 資料位置

| 內容 | 位置 |
|---|---|
| 設定、版面、預設與記錄檔 | Windows：`%APPDATA%\FilmBatch\`；macOS：`~/Library/Application Support/FilmBatch/` |
| 每個照片資料夾的專案與匯出紀錄 | `<照片資料夾>/.filmbatch/` |
| AI 模型與 CoreML 編譯快取 | 上列使用者資料根目錄的 `models/denoise/`；不寫進 `.app`，不隨應用程式封裝權重 |

macOS 的快捷鍵使用 ⌘（例如 ⌘O、⌘Z、⌘⇧Z），並使用原生選單列：應用程式選單提供「關於」與「偏好設定」（⌘,），切換介面語言時選單列即時更新。

## 文件導覽

- [testing.md](testing.md)：測試分類、回歸與合併前 CI
- [release.md](release.md)：Windows／macOS 發佈、簽章與公證、iOS 行動版
- [cli.md](cli.md)：命令列工具、本機 AI 降噪與 AI 去背
- [evidence/](evidence/)：帶日期的驗證記錄（iOS、去背、暗部還原、效能修正）
