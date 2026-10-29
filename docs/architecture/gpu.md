# 架構：Gpu 模組

本文件是[架構說明書](../architecture.md)「4. 模組說明」的一部分，涵蓋 4.9：WebGPU 薄封裝與 Shader。

## 4.9 Gpu．WebGPU 薄封裝與 Shader

**職責**：把 WebGPUSharp（Dawn）包成少數幾個簡單型別，Engine 只透過它們使用 GPU。初始選型見 [ADR 0001](../adr/0001-webgpu-binding.md)；CPU SIMD、RAW 顯示收尾與 Halide AOT 取捨見 [ADR 0003](../adr/0003-gpu-stack-decision.md)。

| 型別 | 檔案 | 說明 |
|---|---|---|
| `GpuDevice` | `Device/GpuDevice.cs` | 選擇介面卡（可指定 DX12 / Vulkan / Metal）、建立裝置；建立紋理、緩衝區、kernel；上傳與同步讀回；`TryAllocate` 偵測顯示記憶體不足 |
| `GpuAdapterInfo` | `Device/GpuAdapterInfo.cs` | 介面卡名稱、後端、是否為軟體、紋理尺寸上限 |
| `ComputeKernel`、`BindingSpec` | `ShaderSystem/ComputeKernel.cs` | 一支已編譯的 compute shader。`BindingSpec` 陣列的索引就是 WGSL 的 `@binding(n)` |
| `GpuCommandList` | `Resources/GpuCommandList.cs` | 錄製多個 pass 後一次送出；`Dispatch2D` 以 16×16 workgroup 覆蓋整張影像 |
| `GpuTexture`、`GpuBuffer` | `Resources/` | 紋理與緩衝區的封裝 |
| `ShaderLibrary` | `ShaderSystem/ShaderLibrary.cs` | 讀取嵌入的 WGSL 並展開 `#include "path"`（WGSL 原生不支援 include） |

**Shader 目錄**（`src/FilmBatch.Gpu/Shaders/v1/`）

| 資料夾 | 內容 |
|---|---|
| `common/` | 共用函式：色彩轉換、取樣、高斯模糊 |
| `io/` | 解碼 16-bit 輸入、線性 → 顯示空間、編碼成 BGRA8 / RGBA16 |
| `linear/` | 基本調整（線性段與顯示段） |
| `geometry/`、`optical/`、`film/`、`detail/`、`surface/` | 各階段的效果模組 |
| `spike/` | M0 技術驗證用的 shader，只供 `filmbatch-cli spike` 基準測試 |

`film/transform.wgsl` 用單一 dispatch 依序執行 CLF 的 Matrix／Range／LUT1D／LUT3D；來源與目的紋理之外，另綁定宣告／混合 uniform、16-byte op records 與唯讀 float 表。`ColorTransformKernel` 隨 verified key 管理目前及在途 buffer，renderer 在資源限制與預算檢查時計入它們；不足時沿用 CPU fallback。`io/raw_working_finish.wgsl` 的外部 DCP tone 使用原始曲線節點，而非重採樣 LUT。兩者的處理位置與限制見 [Engine 契約](engine.md#46-engine算圖管線rendering)；shader 驗證或 Metal 執行不代表其他後端已驗收。

每支 shader 的慣例（以 `optical/vignette.wgsl` 為例）：

```wgsl
struct Params { amount: f32, size: f32, /* … */ _pad: f32 }   // 對應 C# 的 VignetteUniforms

@group(0) @binding(0) var src: texture_2d<f32>;                          // 輸入
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;   // 輸出
@group(0) @binding(2) var<uniform> p: Params;                            // 參數

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) { /* 每個像素一次 */ }
```

**陷阱**

- `v1/` 是 pipeline 版本。日後改變算法導致舊專案外觀改變時，應新增 `v2/` 而不是改 `v1/`，讓舊專案仍以舊 shader 算圖。
- 改完 shader 一律執行 `dotnet run build/CheckShaders.cs`（naga 驗證）。
- 開發時可用 `ShaderLibrary` 的 `devFolder` 直接從原始碼讀取 shader，不必重新建置。
