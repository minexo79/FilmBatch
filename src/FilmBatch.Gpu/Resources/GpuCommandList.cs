using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.ShaderSystem;
using WebGpuSharp;

namespace FilmBatch.Gpu.Resources;

/// <summary>錄製一批 compute pass 與複製指令，<see cref="Submit"/> 時一次送出。</summary>
public sealed class GpuCommandList : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly CommandEncoder _encoder;

    internal GpuCommandList(GpuDevice gpu)
    {
        _gpu = gpu;
        // Dawn 的錯誤回呼是非同步的；以 error scope 讓錯誤在 Submit 時同步回報
        gpu.Device.PushErrorScope(ErrorFilter.Validation);
        _encoder = gpu.Device.CreateCommandEncoder();
    }

    /// <summary>派送一個 compute kernel。<paramref name="resources"/> 依 binding 0..n 的順序傳入紋理或緩衝區。</summary>
    public void Dispatch(ComputeKernel kernel, ReadOnlySpan<object> resources, uint groupsX, uint groupsY, uint groupsZ = 1)
    {
        if (resources.Length != kernel.Bindings.Count)
            throw new ArgumentException($"{kernel.Label}: 需要 {kernel.Bindings.Count} 個資源，收到 {resources.Length} 個。");

        var entries = new BindGroupEntry[resources.Length];
        for (var i = 0; i < resources.Length; i++)
        {
            entries[i] = resources[i] switch
            {
                GpuTexture t => new BindGroupEntry { Binding = (uint)i, TextureView = t.View },
                GpuBuffer b => new BindGroupEntry { Binding = (uint)i, Buffer = b.Handle, Size = b.Size },
                var r => throw new ArgumentException($"不支援的資源型別：{r?.GetType().Name}"),
            };
        }
        var desc = new BindGroupDescriptor { Layout = kernel.BindGroupLayout, Entries = entries };
        var group = _gpu.Device.CreateBindGroup(in desc) ?? throw new GpuException($"無法建立 bind group（{kernel.Label}）");
        _gpu.ThrowIfError($"建立 bind group（{kernel.Label}）");

        var pass = _encoder.BeginComputePass();
        pass.SetPipeline(kernel.Pipeline);
        pass.SetBindGroup(0, group);
        pass.DispatchWorkgroups(groupsX, groupsY, groupsZ);
        pass.End();
    }

    /// <summary>以 16×16 workgroup 覆蓋整張影像。</summary>
    public void Dispatch2D(ComputeKernel kernel, ReadOnlySpan<object> resources, uint width, uint height)
        => Dispatch(kernel, resources, (width + 15) / 16, (height + 15) / 16);

    public void CopyBuffer(GpuBuffer source, GpuBuffer destination, ulong size)
        => _encoder.CopyBufferToBuffer(source.Handle, 0, destination.Handle, 0, size);

    public void Submit()
    {
        var cmd = _encoder.Finish();
        _gpu.ThrowIfError("結束指令編碼");
        _gpu.Queue.Submit(cmd);
        var (status, message) = _gpu.Device.PopErrorScopeSync(10_000_000_000);
        if (!string.IsNullOrEmpty(message)) throw new GpuException($"GPU 指令錯誤：{status} {message}");
    }

    public void Dispose() { }
}
