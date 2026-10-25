# 互動、記憶體與濾波修正（2026-10-22）

帶日期的驗證記錄；結果只對當時版本與實際平台有效。

## 互動、記憶體與濾波修正（2026-10-22）

本輪修正滑桿逐次同步寫專案、排程鎖內的耗時回呼，以及透明影像縮小的隱藏色滲入；縮圖來源／點陣圖改為有界保留，AHD 重用工作緩衝、RAF 預覽借用容器切片，RAW 降噪強度調整重用代理輸入。GPU 高斯核心保留原權重與邊界，並非近似模糊；JPEG 提示只影響符合尺寸條件的桌面預覽，完整匯出不降解析度。PNG16 明確寫入 16-bit RGBA，避免少色影像失去 alpha 精度。

完整 Release 回歸與重新封裝的 Apple Silicon App 已實跑，原生視窗另外操作拖曳／放開／復原、跨專案中斷，以及 400 張照片的縮圖捲回重載；不是以 Headless 取代原生驗證。發行包在 `dist/osx-arm64/FilmBatch.app`，CLI 在 `dist/osx-arm64/cli/filmbatch-cli`，本次為本機 ad-hoc 簽章，未公證。

前後量測、輸入雜湊、精度差異、完整測試數與重跑命令見[本輪驗證紀錄](../superpowers/plans/2026-10-22-performance-correctness.md#observed-results--2026-10-22)。JPEG 預覽不是逐位元相同；Fuji 完整解碼在本次量測反而較慢，記憶體改善不等同所有路徑加速。後續已用使用者提供的 Sigma BF DNG 完成[獨立研究與精確 GainMap 修正](../research/sigma-bf-raw-performance.md)，並正式合併研究分支；Windows DX12 軟體介面卡的原生 CI 結果見下節。Windows 硬體 GPU、iOS 原生執行、未下載的 RAW 語料與人工畫質排名仍不在本輪驗收範圍。

### Windows Gaussian 逾時修正

使用者提供的 `build_105_step_106_container_0.txt` 顯示，`35f4373` 後失敗的是 `GpuGaussianTests.HalationAndClarity_LargeRadius_MatchesCpuOnOddSizeWithTransparentEdges` 的 `OnSubmittedWorkDoneSync` 逾時，不是編譯錯誤。記錄不足以辨識實際 adapter，不能推定為 WARP。修正將累積高斯工作分批提交並等待，完成後才重用 uniform 緩衝區；保留原 2049×1665 測例、半徑、像素精度與 10 秒期限，不以 CPU fallback 或縮小測試掩蓋問題。

```bash
dotnet test tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj -c Release --filter FullyQualifiedName~GpuGaussianTests
dotnet run build/CheckShaders.cs -- --naga /path/to/naga
```

半徑 320／321、水平／垂直與不完整 workgroup 的四項分批回歸先失敗、修正後在 Metal 通過；原大型案例也在 Metal 通過。推送 `4a86715` 後，[Windows CI #110](https://circleci.com/gh/minexo79/FilmBatch/110) 實際通過全部 12 項 Gaussian 測試，原大型案例約 30 秒；這是分批提交的總測試時間，單次等待仍限制 10 秒。Windows 封裝與 24 項原生 smoke 也通過，使用 Microsoft Basic Render Driver／DX12 軟體介面卡；不宣稱已驗證 Windows 硬體 GPU。完整測試數、其他 CI gate 與收據見[遠端驗證紀錄](../superpowers/plans/2026-10-22-performance-correctness.md#remote-windows-ci-evidence-after-push)。
