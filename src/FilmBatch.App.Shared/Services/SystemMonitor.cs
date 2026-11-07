using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FilmBatch.Engine.Denoising;

namespace FilmBatch.App.Services;

/// <summary>
/// 底列「顯示使用率」用的本程序資源取樣：CPU（程序 CPU 時間）、GPU（Windows 效能計數器中本程序 pid 的 3D 引擎）、RAM（工作集）與磁碟 I/O 速率。
/// 取不到的項目回傳 null，由介面顯示「—」。取樣由呼叫端以固定間隔（1 秒）在背景執行緒呼叫 <see cref="Sample"/>。
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    internal readonly record struct Snapshot(
        double? CpuPercent, double? GpuPercent,
        long RamUsedBytes, long RamTotalBytes, long AppBytes, double? DiskBytesPerSec,
        DenoiseMemoryUsage AiPixels)
    {
        /// <summary>本程序工作集佔系統 RAM 的比例，僅供「吃緊」變色判斷。</summary>
        internal double RamPercent => RamTotalBytes <= 0 ? 0 : 100.0 * AppBytes / RamTotalBytes;
    }

    private readonly int _pid = Environment.ProcessId;
    private long _lastIoBytes, _lastIoTicks;
    private TimeSpan _lastProcessCpu;
    private long _lastProcessTicks;
    private List<System.Diagnostics.PerformanceCounter>? _gpuCounters;
    private int _gpuRefreshIn;
    private bool _gpuUnavailable;


    internal Snapshot Sample()
    {
        var (used, total) = Memory();
        return new Snapshot(Cpu(), Gpu(), used, total, AppWorkingSet(),
            Disk(), DenoiseProcessor.SharedMemoryUsage);
    }

    // ───────────── CPU ─────────────

    private double? Cpu()
    {
        // 本程序 CPU 時間 ÷ 經過時間 ÷ 邏輯核心數（與工作管理員的程序 CPU 欄位同義）
        var now = Stopwatch.GetTimestamp();
        var cpu = GetProcessCpuTime();
        var elapsed = Stopwatch.GetElapsedTime(_lastProcessTicks, now);
        var used = cpu - _lastProcessCpu;
        var first2 = _lastProcessTicks == 0;
        _lastProcessTicks = now;
        _lastProcessCpu = cpu;
        return first2 || elapsed.TotalSeconds <= 0 ? null
            : Math.Clamp(100.0 * used.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount, 0, 100);
    }

    // ───────────── GPU ─────────────

    private double? Gpu() => OperatingSystem.IsWindows() && !_gpuUnavailable ? GpuWindows() : null;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private double? GpuWindows()
    {

        try
        {
            if (_gpuCounters is null || --_gpuRefreshIn <= 0)
            {
                // 引擎實例會隨程序出入，每 5 秒重列一次
                DisposeCounters();
                var category = new PerformanceCounterCategory("GPU Engine");
                _gpuCounters = category.GetInstanceNames()
                    .Where(n => n.Contains($"pid_{_pid}_", StringComparison.OrdinalIgnoreCase)
                        && n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new System.Diagnostics.PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true))
                    .ToList();
                _gpuRefreshIn = 5;
                foreach (var c in _gpuCounters) _ = SafeNext(c); // 第一次取值恒為 0，先暖機
                return null;
            }
            double sum = 0;
            foreach (var c in _gpuCounters) sum += SafeNext(c);
            return Math.Clamp(sum, 0, 100);
        }
        catch
        {
            _gpuUnavailable = true; // 沒有計數器（精簡版 Windows、權限）：之後不再嘗試
            DisposeCounters();
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static double SafeNext(System.Diagnostics.PerformanceCounter c)
    {
        try { return c.NextValue(); }
        catch { return 0; } // 程序結束時實例消失
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void DisposeCounters()
    {
        if (_gpuCounters is null) return;
        foreach (var c in _gpuCounters) c.Dispose();
        _gpuCounters = null;
    }

    // ───────────── RAM ─────────────

    private static (long Used, long Total) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
        }
        var info = GC.GetGCMemoryInfo();
        return (info.MemoryLoadBytes, info.TotalAvailableMemoryBytes);
    }

    private static long AppWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    // ───────────── 磁碟 ─────────────

    /// <summary>本程序的磁碟讀寫速率（bytes/s，讀＋寫合計，同工作管理員的程序「磁碟」欄位）；不支援的平台為 null。</summary>
    private double? Disk()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var process = Process.GetCurrentProcess();
        if (!GetProcessIoCounters(process.Handle, out var io)) return null;
        var bytes = (long)(io.ReadTransferCount + io.WriteTransferCount);
        var now = Stopwatch.GetTimestamp();
        var first = _lastIoTicks == 0;
        var elapsed = Stopwatch.GetElapsedTime(_lastIoTicks, now).TotalSeconds;
        var delta = bytes - _lastIoBytes;
        _lastIoBytes = bytes;
        _lastIoTicks = now;
        return first || elapsed <= 0 ? null : Math.Max(0, delta / elapsed);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        DisposeCounters();
    }

    /// <summary>讀取本程序的 CPU 累計時間；Apple 平台使用 Darwin clock，供資源取樣與背景配速共用。</summary>
    internal static TimeSpan GetProcessCpuTime()
    {
        // .NET for iOS does not implement Process.TotalProcessorTime.
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacOS())
        {
            if (ReadProcessClock(12, out var time) != 0) // Darwin CLOCK_PROCESS_CPUTIME_ID
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the process CPU clock.");
            return TimeSpan.FromTicks(checked(time.Seconds * TimeSpan.TicksPerSecond + time.Nanoseconds / 100));
        }
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessClock
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ReadProcessClock(int clockId, out ProcessClock time);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
