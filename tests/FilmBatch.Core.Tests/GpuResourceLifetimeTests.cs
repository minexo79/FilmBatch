using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;
using WebGpuSharp;
using WebGpuSharp.FFI;
using WebGpuSharp.Marshalling;

namespace FilmBatch.Core.Tests;

[CollectionDefinition("GPU native lifetime", DisableParallelization = true)]
public sealed class GpuNativeLifetimeCollection;

[Collection("GPU native lifetime")]
public sealed class GpuResourceLifetimeTests
{
    [Fact]
    public void WindowsDx12_DisposedResourcesReleaseNativeReferencesWithoutGC()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "僅適用於 Windows D3D12 原生資源生命週期");
        Assert.SkipWhen(GpuDevice.EnumerateAdapters(GpuBackend.Dx12).Count == 0, "沒有可用的 D3D12 介面卡");
        // 清除其他測試留下、已不可達的 wrappers；以下迴圈與驗收不呼叫 GC。
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var deviceHandles = DeviceHandles();
        using var device = GpuDevice.Create(new GpuDeviceOptions { Backend = GpuBackend.Dx12 });
        var resourceHandles = ResourceHandles();

        for (var iteration = 0; iteration < 12; iteration++)
        {
            using var kernel = device.CreateKernel("""
                @group(0) @binding(0) var<storage, read_write> values: array<u32>;
                @compute @workgroup_size(1) fn main(@builtin(global_invocation_id) p: vec3u) {
                    values[p.x] += 1u;
                }
                """, "main", [BindingSpec.Storage], "native lifetime");
            using var source = device.CreateBuffer(16, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc | GpuBufferUsage.CopyDst);
            using var readback = device.CreateBuffer(16, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst);
            using var texture = device.CreateTexture(2, 2, GpuTextureFormat.Rgba32Float,
                GpuTextureUsage.Sampled | GpuTextureUsage.CopyDst);
            device.WriteBuffer<uint>(source, new uint[] { 1, 2, 3, 4 });
            using (var commands = device.BeginCommands())
            {
                commands.Dispatch(kernel, [source], 4, 1);
                commands.CopyBuffer(source, readback, 16);
                commands.Submit();
            }
            var actual = new uint[4];
            device.ReadBuffer(readback, actual);
            Assert.Equal(new uint[] { 2, 3, 4, 5 }, actual);
            // 讀回後釋放；仍保持 managed wrapper 存活，不能靠偶然 GC 讓原生參照數歸零。
            source.Dispose();
            readback.Dispose();
            texture.Dispose();
            kernel.Dispose();
            Assert.Equal(resourceHandles, ResourceHandles());
            GC.KeepAlive(source);
            GC.KeepAlive(readback);
            GC.KeepAlive(texture);
            GC.KeepAlive(kernel);
        }

        device.Dispose();
        Assert.Equal(deviceHandles, DeviceHandles());
        GC.KeepAlive(device);
    }

    [Fact]
    public void WindowsDx12_ReadbackFailureKeepsOriginalErrorBeforeLostCallback()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "僅適用於 Windows D3D12 讀回錯誤");
        Assert.SkipWhen(GpuDevice.EnumerateAdapters(GpuBackend.Dx12).Count == 0, "沒有可用的 D3D12 介面卡");
        using var device = GpuDevice.Create(new GpuDeviceOptions { Backend = GpuBackend.Dx12 });
        using var readback = device.CreateBuffer(16, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst);
        // 正常 Destroy 的 callback 被忽略，確定 IsLost 尚未設定；MapSync 仍必須保留原始錯誤。
        WebGPUMarshal.GetHandle(device.Device).Destroy();
        Assert.False(device.IsLost);
        var error = Assert.IsType<GpuException>(Record.Exception(() => device.ReadBuffer(readback, new uint[4])));
        var original = Assert.IsType<WebGPUException>(error.InnerException);
        Assert.Contains(original.Message, error.Message);
        Assert.Contains(device.AdapterInfo.DisplayName, error.Message);
    }

    private static (ulong Instance, ulong Adapter, ulong Device, ulong Queue) DeviceHandles() =>
        (WebGpuSafeHandle.GetActiveHandles<InstanceHandle>(), WebGpuSafeHandle.GetActiveHandles<AdapterHandle>(),
         WebGpuSafeHandle.GetActiveHandles<DeviceHandle>(), WebGpuSafeHandle.GetActiveHandles<QueueHandle>());

    private static (ulong Buffer, ulong Texture, ulong View, ulong Group, ulong Shader, ulong Layout, ulong Pipeline, ulong GroupLayout) ResourceHandles() =>
        (WebGpuSafeHandle.GetActiveHandles<BufferHandle>(), WebGpuSafeHandle.GetActiveHandles<TextureHandle>(),
         WebGpuSafeHandle.GetActiveHandles<TextureViewHandle>(), WebGpuSafeHandle.GetActiveHandles<BindGroupHandle>(),
         WebGpuSafeHandle.GetActiveHandles<ShaderModuleHandle>(), WebGpuSafeHandle.GetActiveHandles<PipelineLayoutHandle>(),
         WebGpuSafeHandle.GetActiveHandles<ComputePipelineHandle>(), WebGpuSafeHandle.GetActiveHandles<BindGroupLayoutHandle>());
}
