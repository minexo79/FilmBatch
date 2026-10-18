using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Localization;
using FilmBatch.Gpu.ShaderSystem;
using WebGpuSharp;

namespace FilmBatch.Gpu.Resources;

/// <summary>錄製一批 compute pass 與複製指令，<see cref="Submit"/> 時一次送出。</summary>
public sealed class GpuCommandList : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly CommandEncoder _encoder;
    private bool _submitted;

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
            throw new ArgumentException(GpuText.Format("Gpu.ResourceCountMismatch", kernel.Label, kernel.Bindings.Count, resources.Length));

        var entries = new BindGroupEntry[resources.Length];
        for (var i = 0; i < resources.Length; i++)
        {
            entries[i] = resources[i] switch
            {
                GpuTexture t => new BindGroupEntry { Binding = (uint)i, TextureView = t.View },
                GpuBuffer b => new BindGroupEntry { Binding = (uint)i, Buffer = b.Handle, Size = b.Size },
                var r => throw new ArgumentException(GpuText.Format("Gpu.UnsupportedResource", r?.GetType().Name)),
            };
        }
        var desc = new BindGroupDescriptor { Layout = kernel.BindGroupLayout, Entries = entries };
        var group = _gpu.Device.CreateBindGroup(in desc) ?? throw new GpuException(GpuText.Format("Gpu.BindGroupFailed", kernel.Label));
        try
        {
            _gpu.ThrowIfError(GpuText.Format("Gpu.Context.CreateBindGroup", kernel.Label));

            var pass = _encoder.BeginComputePass();
            try
            {
                pass.SetPipeline(kernel.Pipeline);
                pass.SetBindGroup(0, group);
                pass.DispatchWorkgroups(groupsX, groupsY, groupsZ);
            }
            finally
            {
                // End 會讓綁定的 pooled pass handle 歸還並 Release。
                pass.End();
            }
        }
        finally
        {
            // Encoder 會自行持有指令所需的原生參照，不需將 managed group 留到 GC。
            if (_gpu.AdapterInfo.Backend == GpuBackend.Dx12) NativeLifetime.Release(group);
        }
    }

    /// <summary>以 16×16 workgroup 覆蓋整張影像。</summary>
    public void Dispatch2D(ComputeKernel kernel, ReadOnlySpan<object> resources, uint width, uint height)
        => Dispatch(kernel, resources, (width + 15) / 16, (height + 15) / 16);

    public void CopyBuffer(GpuBuffer source, GpuBuffer destination, ulong size)
        => _encoder.CopyBufferToBuffer(source.Handle, 0, destination.Handle, 0, size);

    public void Submit()
    {
        var cmd = _encoder.Finish();
        _gpu.ThrowIfError(GpuText.Get("Gpu.Context.FinishCommands"));
        _gpu.Queue.Submit(cmd);
        _submitted = true;
        var (status, message) = _gpu.Device.PopErrorScopeSync(10_000_000_000);
        if (!string.IsNullOrEmpty(message)) throw new GpuException(GpuText.Format("Gpu.CommandError", status, message));
    }

    /// <summary>平衡建構時的 validation error scope；未 Submit 就丟棄時也要 pop，否則 scope 會殘留在裝置堆疊上。</summary>
    public void Dispose()
    {
        if (_submitted) return;
        _submitted = true;
        try
        {
            var (status, message) = _gpu.Device.PopErrorScopeSync(10_000_000_000);
            if (!string.IsNullOrEmpty(message)) GpuDevice.Log?.Invoke($"abandoned command list: {status} {message}");
        }
        catch (WebGPUException error) when (_gpu.IsLost)
        {
            GpuDevice.Log?.Invoke($"abandoned command scope after device loss: {error.Message}");
        }
    }
}
