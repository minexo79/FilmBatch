# ADR 0001：C# WebGPU 綁定選型

- 狀態：**已採用**（M0 GPU spike，2026-10-14）
- 範圍：`src/FilmBatch.Gpu`（引擎只透過此薄封裝存取 WebGPU，日後可替換）

## 決策

採用 **WebGPUSharp 0.5.7（Dawn）**。Windows 預設後端 D3D12，Vulkan 可由偏好設定強制指定；macOS 預設 Metal，偏好設定提供 Auto / Metal。

## 候選比較（Windows 11，RTX 3060 Laptop + Iris Xe）

| 候選 | 原生實作 | 結果 |
|---|---|---|
| Silk.NET.WebGPU 2.23（計畫書原首選） | 內附 wgpu-native（版本號為 0，非正式版建置） | Vulkan 可運作；**D3D12 在 `wgpuDeviceCreateComputePipeline` 存取違規**（任何 shader、任何介面卡，含 WARP；FXC / DXC 皆同）。Windows 上因此無法使用 DX12，也無法使用 WARP 軟體介面卡。Silk 2.x 已少有更新，無法只升級原生函式庫（新版 wgpu-native 的 C 標頭不相容）。**淘汰** |
| Evergine WebGPU.NET 2025.9 / 2026.3 | wgpu-native | 目前版本只附 `browser-wasm` 原生檔，沒有桌面平台。**淘汰** |
| **WebGPUSharp 0.5.7** | Dawn | D3D12、Vulkan 皆可運作；附 win / osx / linux 的 x64 與 arm64 原生檔；目標 .NET 10、支援 Native AOT；API 為受控包裝（貼近 JS WebGPU），不需 unsafe 程式碼。**採用** |

## Spike 量測（`filmbatch-cli spike`，上傳 16-bit → 曝光／亮度／飽和 → 可分離高斯模糊 → BGRA8 → 讀回）

| 介面卡 · 後端 | 影像 | 首張（含上傳） | 拖曳更新 | 拖曳更新＋模糊 | 與 CPU 參考差異 |
|---|---|---|---|---|---|
| RTX 3060 · DX12 | 1600×1067 | 37 ms | 2.0 ms（≈490 fps） | 2.6 ms | 最大 1/255 |
| RTX 3060 · Vulkan | 1600×1067 | 28 ms | 1.9 ms（≈525 fps） | 2.6 ms | 最大 1/255 |
| Iris Xe · DX12 | 1600×1067 | 60 ms | 2.6 ms（≈380 fps） | 5.4 ms | 最大 1/255 |
| Iris Xe · Vulkan | 1600×1067 | — | 3.0 ms（≈340 fps） | 6.3 ms | 最大 1/255 |
| RTX 3060 · DX12 | 4000×2667 | 186 ms | 12.0 ms | 19.7 ms | 最大 1/255 |
| RTX 3060 · DX12 | 6000×4000（24MP） | 684 ms | 37.3 ms | 73.4 ms | 最大 1/255 |
| Apple M3 · Metal（osx-arm64） | 1600×1067 | 79.2 ms | 2.6 ms（381 fps） | 5.4 ms | 最大 1/255 |

（Silk.NET + Vulkan 的同一測試：1600px 拖曳更新 2.3 ms，與 Dawn 相近。）

Apple M3 數值為 2026-10-15 在 macOS 26.6.2 執行發佈版 `dist/osx-arm64/cli/filmbatch-cli spike --backend Metal` 的單次量測；不是跨機器效能保證。GUI 發佈版也通過影像解碼、預覽與縮圖冒煙測試。

驗收標準「1600px 預覽拖曳時更新 ≥ 30 fps」在硬體介面卡上大幅達成；讀回是完整路徑的一部分，已計入上表。

## 實作上的發現

- Dawn 的錯誤回呼是非同步的：`GpuCommandList` 以 error scope 包住每批指令、`ComputeKernel` 以 error scope 包住 shader 編譯，錯誤才能同步回報。
- `Buffer.MapSync` 在 GPU 工作尚未完成時偶發逾時；讀回前先 `Queue.OnSubmittedWorkSync` 即穩定（M3 改為雙緩衝的非同步讀回時需重新驗證）。
- 讀取 `rgba32float` 必須使用明確的 bind group layout（`UnfilterableFloat`），不能用 auto layout。
- Dawn 物件由 GC 釋放；紋理與緩衝區另以 `Destroy()` 立即釋放顯示記憶體。

## 未解決：軟體介面卡

WebGPUSharp 內附的 Dawn 建置**不提供軟體介面卡**（`forceFallbackAdapter` 在 D3D12 / D3D11 / Vulkan 皆回傳 "No supported adapters"；未包含 SwiftShader，也不暴露 WARP）。因此驗收標準「軟體介面卡可正確算出相同結果」目前無法在 Windows 以 GPU 達成。

目前的緩解：
- `RenderScheduler` 在無法建立 GPU 裝置時自動改用 `CpuFakeRenderer`（與 shader 相同數學的 CPU 參考實作），程式仍可使用並提示使用者。

後續選項（M1 前決定）：
1. 以 [webgpu-dawn-build](https://github.com/EmilSV/webgpu-dawn-build) 自建含 SwiftShader 的 Dawn，替換 `webgpu_dawn.dll`。
2. 在 `FilmBatch.Gpu` 內另做一個以 P/Invoke 綁定新版 wgpu-native 的實作（wgpu 的 DX12 後端可列舉 WARP）。
3. 接受「無 GPU 時以 CPU 參考實作降級」，並把 CI 的 GPU 測試改在具備 GPU 的 runner 或本機執行。

## 尚待驗證

- macOS Apple Silicon 的原生 Metal 路徑已驗證；`osx-x64` 封裝在 Rosetta 下通過冒煙，但實體 Intel Mac 與 Linux（Vulkan / lavapipe）仍未驗證。內附 Dawn 的兩個 Mac 架構都宣告 `minos 26.0`；較舊 macOS 需另外的相容原生建置。封裝與執行邊界詳見 [M0 狀態](../M0-status.md)。
