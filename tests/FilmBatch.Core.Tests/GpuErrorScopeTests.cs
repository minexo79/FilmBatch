using FilmBatch.Gpu.Device;
using WebGpuSharp;

namespace FilmBatch.Core.Tests;

public sealed class GpuErrorScopeTests
{
    private static GpuDevice? CreateDevice()
    {
        try { return GpuDevice.Create(); }
        catch (GpuException) { return null; }
    }

    [Fact]
    public void AbandonedCommands_DoNotConsumeTheCallersValidationError()
    {
        using var device = CreateDevice();
        Assert.SkipWhen(device is null, "沒有可用的 GPU 介面卡");
        device.Device.PushErrorScope(ErrorFilter.Validation);
        RecordValidationError(device);

        using (device.BeginCommands()) { }

        var (type, _) = device.Device.PopErrorScopeSync(10_000_000_000);
        Assert.Equal(ErrorType.Validation, type);
    }

    [Fact]
    public void FailedKernelCompilation_DoesNotConsumeTheCallersValidationError()
    {
        using var device = CreateDevice();
        Assert.SkipWhen(device is null, "沒有可用的 GPU 介面卡");
        device.Device.PushErrorScope(ErrorFilter.Validation);
        RecordValidationError(device);

        var error = Record.Exception(() => device.CreateKernel(
            "@compute @workgroup_size(1) fn main() { invalid_statement; }", "main", [], "invalid kernel"));
        Assert.True(error is GpuException or WebGPUException, $"Invalid WGSL must fail compilation, got {error}");

        var (type, _) = device.Device.PopErrorScopeSync(10_000_000_000);
        Assert.Equal(ErrorType.Validation, type);
    }

    private static void RecordValidationError(GpuDevice device)
    {
        // MapRead buffers may only add CopyDst usage; this deliberately invalid descriptor
        // produces a real Dawn validation error in the caller's scope, not a mocked callback.
        var descriptor = new BufferDescriptor { Size = 16, Usage = BufferUsage.MapRead | BufferUsage.Storage };
        _ = device.Device.CreateBuffer(ref descriptor);
    }

    [Fact]
    public void DescriptorHeapFailure_IsAnAllocationFailure()
    {
        var error = new GpuException("ID3D12Device::CreateDescriptorHeap at CheckOutOfMemoryHRESULTImpl");
        Assert.True(GpuDevice.IsAllocationFailure(error));
        Assert.False(GpuDevice.IsAllocationFailure(new OperationCanceledException("CreateDescriptorHeap")));
        Assert.False(GpuDevice.IsAllocationFailure(new GpuException("validation: bind group")));
    }
}

