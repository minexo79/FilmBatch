// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
// RAW 顯示收尾：在線性／顯示效果之間直接處理工作紋理，沿用 renderer 的裝置與命令。
// 相機 LUT 與 kernel 由算圖器快取；不另建讀回管線。
using System.Numerics;
using System.Runtime.InteropServices;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// <c>io/raw_working_finish.wgsl</c> 的 renderer-owned 包裝：未夾取工作紋理直接完成 RAW 收尾。
/// 資源上限與 OOM 由完整效果管線處理；此處不攔截 shader、提交或讀回錯誤。
/// </summary>
internal sealed class RawDisplayGpu : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ShaderLibrary _shaders;
    private bool _disposed;
    private ComputeKernel? _textureKernel;
    private GpuBuffer? _toneLut, _chromaLut, _textureUniform;
    private CameraTone? _cachedTone;
    private bool _toneReady;

    // Records the finish directly between linear effects and display effects. No CPU readback.
    internal void Dispatch(GpuCommandList commands, GpuTexture input, GpuTexture output, CameraTone? tone)
    {
        Prepare(tone);
        var f = RawFinishParams.Working;
        var m = f.Output; var conversion = f.Input;
        var values = new WorkingUniforms(new FinishUniforms(m[0], m[1], m[2], 0,
            m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, output.Width, output.Height, 0, 0),
            new Vector4(conversion[0], conversion[1], conversion[2], 0),
            new Vector4(conversion[3], conversion[4], conversion[5], 0),
            new Vector4(conversion[6], conversion[7], conversion[8], 0));
        _gpu.WriteBuffer(_textureUniform!, values);
        commands.Dispatch2D(_textureKernel!, [input, _toneLut!, _chromaLut!, output, _textureUniform!], output.Width, output.Height);
    }

    internal void Prepare(CameraTone? tone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _textureKernel ??= _gpu.CreateKernel(_shaders.Load("io/raw_working_finish.wgsl"), "main",
            [BindingSpec.TextureFloat, BindingSpec.StorageRead, BindingSpec.StorageRead,
             BindingSpec.StorageTexture(GpuTextureFormat.Rgba32Float), BindingSpec.Uniform], "raw working finish");
        if (!_toneReady || !ReferenceEquals(_cachedTone, tone))
        {
            var map = tone is null ? ToneMap.Create(0, 0, 0) : ToneMap.Camera(tone, 0, 0, 0);
            var lut = map.Lut(); var chroma = map.ChromaLut();
            _toneLut ??= _gpu.CreateBuffer((ulong)lut.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "raw tone");
            _chromaLut ??= _gpu.CreateBuffer((ulong)chroma.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "raw chroma");
            _gpu.WriteBuffer<float>(_toneLut, lut);
            _gpu.WriteBuffer<float>(_chromaLut, chroma);
            _cachedTone = tone; _toneReady = true;
        }
        _textureUniform ??= _gpu.CreateBuffer(112, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "raw working finish parameters");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WorkingUniforms(FinishUniforms Finish, Vector4 Scene0, Vector4 Scene1, Vector4 Scene2);

    internal RawDisplayGpu(GpuDevice gpu, ShaderLibrary shaders)
    {
        _gpu = gpu;
        _shaders = shaders;
    }


    internal void ReleaseWorking()
    {
        _toneLut?.Dispose(); _chromaLut?.Dispose(); _textureUniform?.Dispose();
        _toneLut = _chromaLut = _textureUniform = null;
        _cachedTone = null; _toneReady = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textureKernel?.Dispose();
        ReleaseWorking();
    }

    // 與 WGSL 對齊：三列 vec4f 矩陣與一組 16-byte 列帶參數。
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FinishUniforms(
        float M00, float M01, float M02, float Pad0,
        float M10, float M11, float M12, float Pad1,
        float M20, float M21, float M22, float Pad2,
        uint Width, uint Rows, uint RoundingMask, uint Pad3); // RoundingMask 固定 0，保留 shader 的 f32 捨入點。
}
