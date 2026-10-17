// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
// RAW 顯示收尾：沿用 RawDisplay.Encode 的運算順序；同一算圖器快取 kernel，
// 輸入與輸出皆依實際緩衝區上限分帶，所有暫存資源於呼叫結束時釋放。
using System.Runtime.InteropServices;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// RAW finishing 的 GPU 呼叫方式：由 BatchExporter 在匯出執行緒傳入 renderer 擁有的 encoder。
/// 回傳 (null, reason) 表示應走 CPU；此處不建立 GPU 裝置。
/// </summary>
internal delegate (ushort[]? Pixels, string? FallbackReason) RawDisplayFinisher(
    RgbImage image, CameraTone? tone, CancellationToken ct);

/// <summary>
/// <c>io/raw_display_finish.wgsl</c> 的 renderer-owned 包裝：延遲編譯、列帶執行、實際上限內分塊。
/// 超過紋理上限或實際配置上限時，在配置前明確 fallback（不先配置再救）。
/// Shader 編譯／驗證錯誤直接傳播（GpuException），絕不吞成 fallback。
/// </summary>
internal sealed class RawDisplayGpu : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ShaderLibrary _shaders;
    private readonly ulong _bandCap;
    private ComputeKernel? _kernel;
    private bool _disposed;

    internal RawDisplayGpu(GpuDevice gpu, ShaderLibrary shaders, ulong maxBandBytes = 0)
    {
        _gpu = gpu;
        _shaders = shaders;
        _bandCap = maxBandBytes > 0 ? maxBandBytes : 64UL * 1024 * 1024;
    }

    /// <summary>超限／OOM 回傳 CPU fallback 原因；shader、提交與讀回錯誤直接傳播。</summary>
    internal (ushort[]? Pixels, string? FallbackReason) Finish(
        RgbImage image, CameraTone? tone, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        var width = (uint)image.Width;
        var height = (uint)image.Height;
        var info = _gpu.AdapterInfo;
        if (width == 0 || height == 0
            || width > info.MaxTextureDimension2D || height > info.MaxTextureDimension2D)
            return (null, "RAW finishing 超過 GPU 影像尺寸上限");

        var cap = Math.Min(_bandCap, Math.Min(info.MaxStorageBufferBindingSize, info.MaxBufferSize));
        var rows = (uint)Math.Min(height, cap / ((ulong)width * 12));
        if (rows == 0 || Math.Min(info.MaxStorageBufferBindingSize, info.MaxBufferSize) < ToneMap.LutCount * 4UL)
            return (null, "RAW finishing 超過 GPU 緩衝區上限");

        EnsureKernel();
        var map = tone is null ? ToneMap.Create(0.0, 0.0, 0.0) : ToneMap.Camera(tone, 0.0, 0.0, 0.0);
        var lut = map.Lut();
        var chroma = map.ChromaLut();
        var matrix = ColorSpaces.Rec2020.ToSpace(ColorSpaces.Srgb).ToF32();
        var bandPixels = (ulong)width * rows;
        GpuBuffer? src = null, lutBuffer = null, chromaBuffer = null, output = null, readback = null, uniforms = null;
        try
        {
            var error = _gpu.TryAllocate(() =>
            {
                src = _gpu.CreateBuffer(bandPixels * 12, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "raw finish source band");
                lutBuffer = _gpu.CreateBuffer((ulong)lut.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "raw finish lut");
                chromaBuffer = _gpu.CreateBuffer((ulong)chroma.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "raw finish chroma");
                output = _gpu.CreateBuffer(bandPixels * 8, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc, "raw finish output band");
                readback = _gpu.CreateBuffer(bandPixels * 8, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "raw finish readback band");
                uniforms = _gpu.CreateBuffer(64, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "raw finish uniforms");
            });
            if (error is not null)
                return (null, $"GPU 顯示記憶體不足（RAW finishing）：{error}");

            var pixels = new ushort[checked(image.Width * image.Height * 4)];
            _gpu.WriteBuffer<float>(lutBuffer!, lut);
            _gpu.WriteBuffer<float>(chromaBuffer!, chroma);
            for (uint row = 0; row < height; row += rows)
            {
                ct.ThrowIfCancellationRequested();
                var bandRows = Math.Min(rows, height - row);
                var count = checked((int)(bandRows * width));
                _gpu.WriteBuffer<float>(src!, image.Data.AsSpan(checked((int)(row * width) * 3), count * 3));
                var parameters = new FinishUniforms(
                    matrix[0], matrix[1], matrix[2], 0,
                    matrix[3], matrix[4], matrix[5], 0,
                    matrix[6], matrix[7], matrix[8], 0, width, bandRows, 0, 0);
                _gpu.WriteBuffer(uniforms!, parameters);
                using var commands = _gpu.BeginCommands();
                commands.Dispatch(_kernel!, [src!, lutBuffer!, chromaBuffer!, output!, uniforms!],
                    (width + 15) / 16, (bandRows + 15) / 16);
                commands.CopyBuffer(output!, readback!, (ulong)count * 8);
                commands.Submit();
                _gpu.ReadBuffer(readback!, pixels, checked((int)(row * width) * 4), count * 4);
            }
            ct.ThrowIfCancellationRequested();
            return (pixels, null);
        }
        finally
        {
            src?.Dispose();
            lutBuffer?.Dispose();
            chromaBuffer?.Dispose();
            output?.Dispose();
            readback?.Dispose();
            uniforms?.Dispose();
        }
    }

    private void EnsureKernel()
    {
        if (_kernel is not null) return;
        // Shader 編譯／pipeline 錯誤由 ComputeKernel 以 GpuException 傳播，不轉成 fallback。
        _kernel = _gpu.CreateKernel(
            _shaders.Load("io/raw_display_finish.wgsl"), "main",
            [BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.StorageRead,
             BindingSpec.Storage, BindingSpec.Uniform],
            "io/raw_display_finish");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _kernel?.Dispose();
    }

    // 與 WGSL 對齊：三列 vec4f 矩陣與一組 16-byte 列帶參數。
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FinishUniforms(
        float M00, float M01, float M02, float Pad0,
        float M10, float M11, float M12, float Pad1,
        float M20, float M21, float M22, float Pad2,
        uint Width, uint Rows, uint RoundingMask, uint Pad3); // RoundingMask 固定 0，保留 shader 的 f32 捨入點。
}
