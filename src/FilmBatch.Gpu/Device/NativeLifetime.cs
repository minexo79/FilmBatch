using System.Runtime.CompilerServices;
using WebGpuSharp;
using WebGpuSharp.FFI;
using WebGpuSharp.Internal;

namespace FilmBatch.Gpu.Device;

/// <summary>WebGPUSharp 0.5.7 的原生生命週期與 callback 簽章修正；升級綁定時須重驗。</summary>
internal static class NativeLifetime
{
    internal static void Release<T>(WebGPUManagedHandleBase<T>? resource)
        where T : unmanaged, IWebGpuHandle<T>, IEquatable<T>
    {
        if (resource is not null) Access<T>.SafeHandle(resource).Dispose();
    }

    // 0.5.7 的 descriptor 宣告兩參數，native trampoline 卻轉型成三參數並吞掉轉型錯誤。
    // 保留實際三參數 delegate；此兩參數檢視只能交給 descriptor，不可直接 Invoke。
    internal static Action<T, ReadOnlySpan<byte>> DeviceCallback<T>(
        Action<DeviceHandle, T, ReadOnlySpan<byte>> callback) where T : struct, Enum
        => Unsafe.As<Action<T, ReadOnlySpan<byte>>>(callback);

    private static class Access<T> where T : unmanaged, IWebGpuHandle<T>, IEquatable<T>
    {
        // 綁定版本升級時須重驗此私有欄位；不可直接 Release，否則 finalizer 會 double-free。
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_safeHandle")]
        internal static extern ref WebGpuSafeHandle<T> SafeHandle(WebGPUManagedHandleBase<T> resource);
    }
}
