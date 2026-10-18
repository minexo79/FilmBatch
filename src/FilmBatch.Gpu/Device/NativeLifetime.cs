using System.Runtime.CompilerServices;
using WebGpuSharp;
using WebGpuSharp.FFI;
using WebGpuSharp.Internal;

namespace FilmBatch.Gpu.Device;

/// <summary>WebGPUSharp 0.5.7 的 managed wrapper 未公開 Dispose；透過原有 safe handle 釋放，避免與 finalizer 重複 Release。</summary>
internal static class NativeLifetime
{
    internal static void Release<T>(WebGPUManagedHandleBase<T>? resource)
        where T : unmanaged, IWebGpuHandle<T>, IEquatable<T>
    {
        if (resource is not null) Access<T>.SafeHandle(resource).Dispose();
    }

    private static class Access<T> where T : unmanaged, IWebGpuHandle<T>, IEquatable<T>
    {
        // 綁定版本升級時須重驗此私有欄位；不可直接 Release，否則 finalizer 會 double-free。
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_safeHandle")]
        internal static extern ref WebGpuSafeHandle<T> SafeHandle(WebGPUManagedHandleBase<T> resource);
    }
}
