# AI 降噪 Survey

- 調查日期：2026-10-18。
- 狀態：**來源調查完成；2026-10-19 已選用 RawNIND linear、NAFNet-SIDD 與 ONNX Runtime**。現行決策見 [ADR 0002](../adr/0002-ai-denoise.md)，實際整合驗收見[計畫與證據](../superpowers/plans/2026-10-19-ai-denoising.md)；下方保留調查當時的候選與未完成條件，不是品質排名或法律意見。
- 來源：本次 LightCraft、Lightroom、darktable-ai、stefanpenner/ai-denoise 與其他候選的原始碼／官方文件調查。下方保留實際查閱的 commit 與主要證據，不依賴對話、暫存目錄或未提交的產物。
- 範圍：優先尋找 MIT、Apache-2.0、zlib 或類似寬鬆授權的本機攝影降噪方案；分開檢查程式碼、模型權重、訓練資料與推論環境。

## 結論與優先順序

這是**工程適配順序，不是畫質或速度排名**。

| 需求 | 優先評估 | 理由與未完成條件 |
|---|---|---|
| 現有 RAW 場景管線 | RawNIND `model_linear.onnx` | 輸入／輸出是線性 Rec.2020，可保留現有去馬賽克；仍須驗證數值範圍、增益匹配、分塊與實際權重來源 |
| Bayer 聯合降噪／去馬賽克 | RawNIND `model_bayer.onnx` | 在感光資料階段處理，但須介入目前去馬賽克之前；有 CoreML FP16 限制，不是線性版本的直接替換品 |
| 一般 JPEG／TIFF 等 RGB 來源 | NAFNet-SIDD Width32 | 模型文件明列 MIT，SIDD 資料集亦標示 MIT；不等同 RAW／HDR 模型，實際權重檔仍須對應授權及雜湊 |
| 需要雜訊等級輸入的 RGB 比較 | DRUNet | 接受額外的 `σ/255` 通道；實際相機雜訊適配與權重散布條件待確認 |

RawNIND 作者提供權重的 **GPL3 與 CC-BY-4.0 雙授權**。CC-BY-4.0 是可選的署名型授權路徑，**不是 MIT／Apache／zlib**；若交付項連權重也嚴格限於後三類，RawNIND 不符合該窄條件。不能把這個權重授權延伸成整個 GPL 訓練／封裝工具鏈已獲寬鬆授權。

## FilmBatch 現況與舊提案的關係

目前已具備 [RAW 場景線性管線](../architecture/engine.md#48-engine影像讀取與批次匯出imaging--exporting)，不是只有 sRGB JPEG／TIFF 的程式。`RawSourceLoader` 先解碼、分箱／去馬賽克、處理高光與色彩，產生浮點 Rec.2020 來源；後續 CPU／GPU 算圖保留晚期 RAW 顯示收尾。

[ADR 0002](../adr/0002-ai-denoise.md) 已取代早期「沒有 RAW 管線」的 sRGB／FP16 提案，分別選用場景線性 RawNIND 與顯示 RGB NAFNet，維持 FP32 輸入與各自的模型契約。此處候選比較保留為 2026-10-18 的調查歷史，不是目前執行入口。

## 指定專案調查

| 專案／查閱版本 | 實際提供的東西 | 授權與可重用範圍 |
|---|---|---|
| [LightCraft v0.9.1，`4f0b110`](https://github.com/storytold/lightcraft/tree/4f0b110bcbd2e350c6d97b47f1d824c2222295c4) | RAW 降噪整合、Bayer 打包、分塊、混合與快取；自有 Rust CPU／WGSL 推論 | 程式碼 MIT OR Apache-2.0。適合參考資料契約與演算法，不是可直接呼叫的 .NET 函式庫，也不是 ONNX Runtime 包裝 |
| [darktable-ai，`f1a46e2`](https://github.com/darktable-org/darktable-ai/tree/f1a46e2d3feebc917a94f14705d128316fcf8114) | Python 模型轉換、驗證及 ONNX／`.dtmodel` 封裝工具 | 工具鏈 GPL-3.0；各模型與權重須另查。不要把整個工具鏈作為 FilmBatch 的執行依賴 |
| [stefanpenner/ai-denoise，`18f9a82`](https://github.com/stefanpenner/ai-denoise/tree/18f9a8260544e2bdcbda389e44d4572d644ca88b) | Go 協調器，透過 shell 呼叫各自的 Python 環境與後端，輸出影像及比較結果 | 包裝程式 MIT，不代表各後端／權重都是 MIT。不建議當作可嵌入的產品推論引擎 |
| [Lightroom Classic Enhance 官方文件](https://helpx.adobe.com/lightroom-classic/desktop/process-and-develop-photos/enhance-details.html) | 商業產品的操作與支援範圍基準 | 未找到可直接嵌入的開源模型或推論函式庫；可參考非破壞式調整與背景處理，不能宣稱開源候選已有同等品質 |

### LightCraft：可參考實作，不直接搬入整套推論引擎

查閱版本的 [manifest](https://github.com/storytold/lightcraft/blob/4f0b110bcbd2e350c6d97b47f1d824c2222295c4/crates/denoise-core/src/manifest.rs) 只定義 `BayerToRgb`：扣黑位、以白位正規化、尚未套白平衡的 Bayer，打包成 RGGB 四平面，輸出原感光尺寸的相機 RGB。它已同時去馬賽克，後段不可再做一次。

[GPU runner](https://github.com/storytold/lightcraft/blob/4f0b110bcbd2e350c6d97b47f1d824c2222295c4/crates/gpu/src/nn.rs) 以獨立 Rust `wgpu` 裝置執行自有 WGSL 網路算子。這不代表能直接共用 FilmBatch 的 Dawn 裝置或紋理；整套移植會增加另一組 CPU／GPU 算子的維護責任。

[模型目錄](https://github.com/storytold/lightcraft/blob/4f0b110bcbd2e350c6d97b47f1d824c2222295c4/crates/denoise-core/src/known.rs) 將下載的 darktable RawNIND 封裝標為 GPL-3.0；後來的權重替代授權聲明見[作者 issue #1760](https://github.com/storytold/lightcraft/issues/1760)。授權目錄更新與 X-Trans re-mosaic 的 PR 不屬於本次查閱的 v0.9.1，不能列為該版本已發佈能力。

### ai-denoise：正常退出不保證真的推論

[`03_eld/run.py`](https://github.com/stefanpenner/ai-denoise/blob/18f9a8260544e2bdcbda389e44d4572d644ca88b/backends/03_eld/run.py) 與 [`04_pmn/run.py`](https://github.com/stefanpenner/ai-denoise/blob/18f9a8260544e2bdcbda389e44d4572d644ca88b/backends/04_pmn/run.py) 會捕捉推論錯誤、寫入 stderr，然後仍將未降噪的 Bayer 轉成 TIFF。若程序正常退出，[Go 協調器](https://github.com/stefanpenner/ai-denoise/blob/18f9a8260544e2bdcbda389e44d4572d644ca88b/main.go) 不會據此辨識「其實沒有降噪」。

因此，產生影像、退出碼為零或顯示成功，不足以作為品質／效能評測的推論證據。這是原始碼檢查結果，本次未執行該失敗路徑。

### Lightroom：以現行文件為準

Classic 文件更新日期為 2026-05-01，列出的 Denoise 支援包含 Bayer／X-Trans、線性 DNG、DNG proxies、單色 RAW、ProRAW 等，並描述 Detail 面板與背景批次工作流程。[另一份 Lightroom 文件](https://helpx.adobe.com/lightroom/desktop/edit-photos/enhance-details.html) 同時保留新版面板說明與舊 `Enhance-NR.dng` 操作文字。

不宜概括成「只有 Bayer」或「所有版本一定建立新 DNG」；也不能從操作文件推定 Adobe 未公開的網路結構、權重或可嵌入 API。

## 模型資料契約與授權證據

### RawNIND UtNet2

主要來源：[darktable 模型文件](https://github.com/darktable-org/darktable-ai/blob/f1a46e2d3feebc917a94f14705d128316fcf8114/models/rawdenoise-nind/README.md)、[原作者 README，`e80fb7b`](https://github.com/trougnouf/rawnind_jddc/blob/e80fb7b14dc440bba38c01f312fd3cc02deb566c/README.md)。

| 模型 | 輸入 | 輸出 | 額外契約 |
|---|---|---|---|
| `model_bayer.onnx` | FP32 `[1,4,512,512]`；四平面 Bayer | FP32 `[1,3,1024,1024]`；相機原生 RGB | 聯合降噪／去馬賽克、Bayer 相位處理、增益匹配、後續相機色彩轉換 |
| `model_linear.onnx` | FP32 `[1,3,512,512]`；線性 Rec.2020 | FP32 `[1,3,512,512]`；線性 Rec.2020 | 先由現有管線去馬賽克，仍需增益匹配；不能省略輸入範圍與白平衡契約的核對 |

兩者的 ONNX 匯出規格為固定分塊、opset 20。模型輸出帶有任意學得的增益，呼叫端必須按規格校正，不是直接把輸出當作原尺度的影像。

**Apple Silicon 限制：**上游模型文件記載 Bayer 版本在 CoreML 的 ANE／GPU 路徑發生 FP16 中間值溢位，產生 NaN／Inf，因此以 `cpu_only: model_bayer: [coreml]` 指定 CPU compute units。線性版本未列相同限制；這是上游記錄，**不是 FilmBatch 本機推論驗證**。不能因模型支援 ONNX／CoreML 就預設可安全使用 GPU／FP16。

授權分層：

- 訓練／轉換程式：GPL-3.0。
- 原作者提供的預訓練權重：README 明列 GPL3 與 CC-BY-4.0 雙授權。
- 訓練資料：模型文件記載 RawNIND 真實攝影配對資料，CC BY 4.0／CC0、逐張適用。
- 實際交付：仍需將選定 checkpoint、ONNX 轉檔、雜湊、署名、授權連結與變更說明對應起來；本次未下載檔案驗證，不能把整個 `.dtmodel` 封裝宣稱為寬鬆授權。

### NAFNet-SIDD Width32 與舊 NIND

[NAFNet 模型文件](https://github.com/darktable-org/darktable-ai/blob/f1a46e2d3feebc917a94f14705d128316fcf8114/models/denoise-nafnet/README.md) 明列 **Model license: MIT**、訓練資料 MIT，輸入／輸出為 FP32 `[1,3,768,768]`、正規化 RGB。這是 RGB 降噪，不是 Bayer 或任意 HDR 線性場景的替代品。

[上游 LICENSE，`2b4af71`](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/LICENSE) 包含 NAFNet 的 MIT 與 BasicSR 的 Apache-2.0 聲明；[SIDD 官方頁面](https://abdokamel.github.io/sidd/#license) 亦明列資料集及相關程式碼 MIT。**模型文件的 MIT 聲明不是對 Google Drive 實際權重檔已完成授權對應／雜湊核對的證明。**

不要混淆 NIND 與 RawNIND：[舊 `denoise-nind` 模型文件](https://github.com/darktable-org/darktable-ai/blob/f1a46e2d3feebc917a94f14705d128316fcf8114/models/denoise-nind/README.md) 列為 GPL-3.0，本次沒有確認其權重具有 RawNIND 的同一替代授權。

## 其他候選

| 候選／查閱版本 | 已確認的內容 | 判斷與未確認項目 |
|---|---|---|
| [RawForge，`79f1770`](https://github.com/rymuelle/RawForge/tree/79f17700c29765b28a4cf5bd1b6d90bc7e3eb7f2) | MIT Python CLI、alpha 狀態，文件描述 ONNX／DirectML／CoreML 路線 | RAW 備選；不能只憑根目錄 MIT 就宣稱二進位權重可任意散布 |
| [DRUNet／DPIR，`15bca3f`](https://github.com/cszn/DPIR/tree/15bca3fcc1f3cc51a1f99ccf027691e278c19354) | 程式 MIT；RGB 影像另串接 `σ/255` 通道 | 可比較可調雜訊等級；合成高斯雜訊不等同實際 RAW 雜訊，外部 checkpoint 散布條件未確認 |
| [SCUNet，`52e440a`](https://github.com/cszn/SCUNet/tree/52e440a80a655b01e0b41e9dd9bfe599bc11625e) | 程式 Apache-2.0；三通道 RGB；real-PSNR／GAN 版本 | RGB 比較候選；權重散布條件另查，不把程式碼授權當作權重授權證明 |
| [Restormer，`68dc6ac`](https://github.com/swz30/Restormer/tree/68dc6ac472db26f16361150cb7a96a1bc87da93f) | 本次查閱的 `LICENSE.md` 為 MIT | RGB 比較基準；論文文字的 CC-BY-NC-SA 不等同程式碼授權，外部權重條件仍需確認 |
| [Open Image Denoise，`6602ee2`](https://github.com/RenderKit/oidn/tree/6602ee2ca38a1a2a02135beed8f6e68eed630180) | Apache-2.0；官方 RT 模型針對光線追蹤渲染雜訊 | [RT 文件](https://www.openimagedenoise.org/documentation.html#rt) 明確排除非光線追蹤影像，不列為相機照片降噪首選 |

RawForge 發行資訊另查閱：[`onnx_v1.0.0`](https://github.com/rymuelle/RawForge/releases/tag/onnx_v1.0.0)（commit `5a7c8c145d134be30e3441e709c9790d631670d6`）與 [`xtrans_v1.0.0`](https://github.com/rymuelle/RawForge/releases/tag/xtrans_v1.0.0)（`827094cfdcdb73a57c197e4f0add6119e682bfff`）。後者明列初版 X-Trans 模型不接受 conditioning。兩份說明未提供獨立權重授權；本次不把其他模型的 ISO conditioning 宣稱當成全部模型的能力。

Sony NMiOH、PMN、ELD 亦列入來源調查，但權重散布條件與感光元件／校正依賴未完成確認，不列為可直接交付的候選。ai-denoise 的 MIT 不會替這些後端解決授權或資料域問題。

## 推論函式庫

- **優先評估 ONNX Runtime**：[程式碼 MIT，`c1ea5bf`](https://github.com/microsoft/onnxruntime/blob/c1ea5bffb8f78d6d6b7239eccf66ebfc3e5833b0/LICENSE)，有 C# API。相較維護 Python 環境或自有網路算子，這是較小整合路徑的工程判斷，不是速度保證。
- [CoreML provider 文件](https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html) 描述平台與執行選項；實際 .NET 原生套件、算子涵蓋、精度及裝置分派仍須核對。
- [DirectML provider 文件](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html) 已標為維護階段，新 Windows 功能方向轉至 WinML。仍須按支援的 Windows 版本選型，不只照搬舊 ADR。
- [ncnn，`21eb6ca`](https://github.com/Tencent/ncnn/blob/21eb6ca76fc0fccbaf2513c1b76e50f374851de5/LICENSE.txt)：原生函式庫 BSD-3-Clause，另列 zlib／BSD 等元件；CPU／Vulkan 路線會增加 C++ 包裝與轉換工作，暫不優先。

模型、推論函式庫、訓練程式、封裝工具與商業產品是不同角色。沒有單一「MIT 降噪函式庫」會自動解決模型授權、攝影資料域、平台封裝及品質。

## 後續驗證條件

後續若授權實作，最小驗證範圍建議先限 RawNIND linear 與 NAFNet-SIDD，並分別對照正確的 RAW／RGB 來源。

1. **來源與授權**：固定 checkpoint／ONNX 雜湊、轉換版本、模型授權、署名與原生相依庫；不能只核對 repository LICENSE。
2. **數值與資料域**：驗證白平衡、曝光尺度、增益匹配、高光／HDR、負數或超出 1 的場景值、非有限值與 FP16 差異，不能提前夾成 SDR 來滿足模型。
3. **影像品質**：真實高 ISO 照片的細節、色偏、飽和高光、分塊接縫與不同相機；Bayer 聯合模型另驗四種相位，非 Bayer 路線另驗 X-Trans。
4. **互動與快取**：來源階段快取不應隨每次底片／對比／顆粒調整重跑；強度混合須在一致的幾何與色彩域，並驗證取消、切換照片及既有即時 Kelvin 編輯。
5. **部署與量測**：實際 Mac／Windows 的執行 provider、CPU 後備、全解析度匯出、延遲、尖峰記憶體與封裝；不能引用上游速度當作 FilmBatch 成績。

## 本次證據邊界

2026-10-18 的調查只查閱原始碼、官方產品文件、模型文件、授權文字及發行資訊，沒有下載權重或執行推論。2026-10-19 的實作階段已另外下載並驗證兩個固定 ONNX 套件，完成 CPU／CoreML 實拍裁切、HDR／alpha、取消後重新推論、完整預覽／16-bit 匯出、原生介面與 macOS 封裝執行驗證；結果及未覆蓋平台集中記錄於[實作計畫](../superpowers/plans/2026-10-19-ai-denoising.md#observed-acceptance-evidence--2026-10-19)，不將這份歷史 Survey 本身充當 runtime 證據。NIND 的線性測試輸入來自 inverse-sRGB 轉換，不是原始感光樣本或獨立 RAW benchmark；Windows 仍需 DirectML 實機驗證。
