using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FilmBatch.App.Services;

/// <summary>
/// 底列「顯示使用率」用的系統資源取樣：CPU、GPU（Windows 效能計數器的 3D 引擎）、RAM 與磁碟作用時間。
/// 取不到的項目回傳 null，由介面顯示「—」。取樣由呼叫端以固定間隔（1 秒）在背景執行緒呼叫 <see cref="Sample"/>。
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    internal readonly record struct Snapshot(
        double? CpuPercent, double? GpuPercent,
        long RamUsedBytes, long RamTotalBytes, long AppBytes, double? DiskPercent)
    {
        internal double RamPercent => RamTotalBytes <= 0 ? 0 : 100.0 * RamUsedBytes / RamTotalBytes;
    }

    private ulong _lastIdle, _lastTotal;
    private TimeSpan _lastProcessCpu;
    private long _lastProcessTicks;
    private List<System.Diagnostics.PerformanceCounter>? _gpuCounters;
    private int _gpuRefreshIn;
    private bool _gpuUnavailable;
    private System.Diagnostics.PerformanceCounter? _diskCounter;
    private bool _diskWarm, _diskUnavailable;


    internal Snapshot Sample()
    {
        var (used, total) = Memory();
        return new Snapshot(Cpu(), Gpu(), used, total, Process.GetCurrentProcess().WorkingSet64, Disk());
    }

    // ───────────── CPU ─────────────

    private double? Cpu()
    {
        if (OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var i = ToUInt64(idle);
            var t = ToUInt64(kernel) + ToUInt64(user); // kernel 時間已含 idle
            var dTotal = t - _lastTotal;
            var dIdle = i - _lastIdle;
            var first = _lastTotal == 0;
            _lastIdle = i;
            _lastTotal = t;
            return first || dTotal == 0 ? null : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
        }
        // 其他平台：以本程序的 CPU 時間近似
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
                    .Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
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

    // ───────────── 磁碟 ─────────────

    /// <summary>磁碟使用率＝工作管理員的「作用時間」（100 − PhysicalDisk 閒置時間），全部實體磁碟合計；非 Windows 或沒有計數器時為 null。</summary>
    private double? Disk()
    {
        if (!OperatingSystem.IsWindows() || _diskUnavailable) return null;
        return DiskWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private double? DiskWindows()
    {
        try
        {
            _diskCounter ??= new System.Diagnostics.PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            var idle = _diskCounter.NextValue();
            if (_diskWarm) return Math.Clamp(100 - idle, 0, 100);
            _diskWarm = true; // 第一次取值沒有差值，先暖機
            return null;
        }
        catch
        {
            _diskUnavailable = true;
            _diskCounter?.Dispose();
            _diskCounter = null;
            return null;
        }
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        DisposeCounters();
        _diskCounter?.Dispose();
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

    private static ulong ToUInt64(FileTime t) => ((ulong)t.High << 32) | t.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
