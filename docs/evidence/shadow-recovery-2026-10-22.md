# 暗部還原驗證記錄（2026-10-22）

帶日期的驗證記錄；結果只對當時版本與實際平台有效。操作說明見 [building.md](../building.md#局部暗部還原)。

## 暗部功能驗證（2026-10-22）

本輪實際執行：

| 檢查 | 已觀察結果 |
|---|---|
| 完整 Release 來源回歸 | Core 625 passed／27 skipped；RAW 626 passed／293 skipped；App 215 passed／0 skipped；合計 1,466 passed／0 failed／320 skipped |
| `ShadowRecoveryTests` | 11 passed；黑點／暗部細節、亮度與色度抑噪、透明隱藏色隔離、HDR、高光分離、舊外觀、單軸影像與實際 GPU 容差 |
| 真正 Sony A7S RAW 全圖 | 4240×2832；工作浮點含負值及 >1 的高光，來源雜湊不變；Apple M3／Metal 的 CPU／GPU RGBA16 最大差 1，未用 CPU fallback 冒充 GPU |
| 空間／取消 smoke | 部分 alpha、旋轉／翻轉／裁切 RGB 最大差 1、alpha 差 0；零控制量與極小來源足跡完全恆等；進行中取消後同一算圖器可重用 |
| 共用 Headless 桌面 | 11 項操作通過：真實 RAW、啟用／滑桿、整段復原／重做、重開、取樣排除、預設／貼上、英文標籤、PNG 匯出及原檔不變 |
| GUI PNG16 獨立解碼 | OpenCV 讀取實際檔案，3,849,600 個 RGBA16 樣本與全圖 CPU 算圖再縮放的結果零差異 |
| 真正 CLI 重新開啟專案匯出 | `export --format png --long-edge 1200 --software` 讀取已保存的暗部設定；3,849,600 個 RGBA16 樣本與 GUI 匯出完全相同，明示使用 CPU |
| 產品嵌入 WGSL | Naga 30.0.1 驗證 35 個檔案通過，包含暗部統計／盒狀／套用與共用函式；不是只檢查儲存庫外副本 |

取得 SDK、還原相依後，從根目錄重跑永久回歸：

```bash
dotnet test FilmBatch.sln -c Release --no-restore --nologo --filter 'FullyQualifiedName~ShadowRecoveryTests|FullyQualifiedName~AppleImageCodecTests|FullyQualifiedName~RawControlsTests'
```

GPU／平台或外部 RAW 語料略過不算通過。全圖診斷為另驗全尺寸預覽而明確提高為 2 GiB 預算；產品預設預覽預算未變，匯出仍走既有分段／不足時明示 CPU 的流程，這不是單一 pass 的效能或記憶體基準。Headless 圖片不代替原生 `NSWindow`；先前桌面鎖定回傳 `-6661` 的限制未重試。Windows／iOS 暗部實機、人工畫質盲評、其他相機／ISO 與 Lightroom 對照未取得。

獨立 OpenCV/libpng 讀取 PNG 仍回報既有 `iCCP: known incorrect sRGB profile` 警告；像素核對通過，不代表 ICC 無警告，本輪沒有抑制警告或偷偷替換設定檔。一次性驅動、實際結果與可還原來源封存於儲存庫外，臨時驗證程式不成為產品相依；未 commit／push 或重新封裝發行包。[研究與三相機解碼比較](../research/shadow-recovery-survey.md)另外區分感光數值、共同現像與無法判定的高光／黑白位資訊。

## Sony RAW 控制項驗證

使用公開 Sony A7S ARW2 實跑既有自訂 WB 控制：真正介面的 Kelvin／色偏變更會更新場景預覽及縮圖，停用／再啟用、拖曳復原／重做、專案重開與 PNG16 匯出皆通過；獨立解碼的 3,849,600 個 RGBA16 匯出樣本與預期結果相同，原檔雜湊不變。這個樣本未重現「Sony 調整全部無效」，未因此盲改 decoder 或加色偏補償，也不推廣到未提供的 Sony 機型／檔案。

相機選項不是每種 RAW 都有資料：本樣本沒有可用的內嵌鏡頭修正表、DNG `ProfileGainTableMap` 或語意遮罩，因此切換這些選項是無效果；自訂白平衡則有可見差異。縮圖預設可能取相機內嵌 JPEG，不可用未經現像的內嵌預覽判定 RAW 設定是否生效；有自訂白平衡的本次縮圖路徑已另外驗證。
