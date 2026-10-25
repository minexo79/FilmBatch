using System.Numerics;
using System.Runtime.InteropServices;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Develop;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 解碼專用的 GPU AHD 去馬賽克與色彩收尾：自己持有一個獨立的 <see cref="GpuDevice"/>（解碼執行緒沒有算圖器的裝置）。
/// 只有在 <see cref="BeginScope"/> 的執行內容（<see cref="AsyncLocal{T}"/>）內才會執行 GPU；直接呼叫
/// <see cref="ImageLoader"/>、CLI 與匯出預設走 CPU，不受 App 背景解碼的範圍影響。
/// AHD 以帶狀分段處理整張馬賽克（每帶 <see cref="BandRows"/> 列加 6 像素邊界）；色彩收尾以
/// <see cref="ColourBandRows"/> 列為一帶。沒有硬體 GPU、影像太小、超出預算或裝置限制、裝置遺失時
/// 回退 CPU；各功能的編譯／執行失敗只停用該功能，不影響另一個。
/// 程序啟動前設定 <c>FILMBATCH_NO_GPU_DEMOSAIC=1</c> 會同時停用 AHD 與色彩收尾。
/// </summary>
internal static class RawGpuDemosaic
{
    private const int BandRows = 256;
    private const int ColourBandRows = 512;
    private const long MinPixels = 4_000_000;
    private static readonly object Gate = new();
    private static Runner? s_runner;
    private static GpuBackend s_backend = GpuBackend.Auto;
    private static bool s_ahdDisabled = EnvDisabled;
    private static bool s_colourDisabled = EnvDisabled;
    private static string? s_ahdError;
    private static string? s_colourError;
    private static readonly AsyncLocal<Policy?> CurrentPolicy = new();

    private static bool EnvDisabled => Environment.GetEnvironmentVariable("FILMBATCH_NO_GPU_DEMOSAIC") == "1";

    internal sealed record Policy(bool Enabled, GpuBackend Backend, long MemoryBudgetBytes)
    {
        public AhdAccelerator? PreviousAccelerator;
        public AhdAccelerator? Installed;
    }

    /// <summary>
    /// 進入 RAW GPU 解碼政策範圍：<paramref name="enabled"/> 來自 <c>!ForceSoftwareGpu</c>（iOS 永遠 false），
    /// <paramref name="backend"/> 來自偏好設定，<paramref name="memoryBudgetBytes"/> 來自偏好設定的 MB 數
    /// （0 表示未設定，只用裝置上限檢查）。巢狀範圍取較嚴格者：任一層停用即走 CPU，
    /// 內層啟用也不會突破外層停用；超出外層預算時以較小者為準。
    /// 回傳值型別 scope，離開即還原前一個加速器與政策。
    /// </summary>
    internal static Scope BeginScope(bool enabled, GpuBackend backend, long memoryBudgetBytes)
    {
        var outer = CurrentPolicy.Value;
        var effective = enabled && outer is not { Enabled: false };
        var budget = memoryBudgetBytes <= 0 ? outer?.MemoryBudgetBytes ?? 0
            : outer is { MemoryBudgetBytes: > 0 } ? Math.Min(outer.MemoryBudgetBytes, memoryBudgetBytes)
            : memoryBudgetBytes;
        var policy = new Policy(effective, backend, budget);
        // 內層停用也要記住外層加速器，離開時才能還原；內層啟用在外層停用下不掛鉤。
        // 儲存實際掛上的委派實例：方法群組每次轉換都是新實例，不能用 ReferenceEquals 與新轉換比較。
        policy.PreviousAccelerator = Demosaic.AhdGpu;
        policy.Installed = effective ? (AhdAccelerator)TryRun : null;
        Demosaic.AhdGpu = policy.Installed;
        CurrentPolicy.Value = policy;
        return new Scope(policy, outer);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Policy? _policy;
        private readonly Policy? _previous;

        internal Scope(Policy? policy, Policy? previous)
        {
            _policy = policy;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_policy is null) return;
            if (!ReferenceEquals(CurrentPolicy.Value, _policy)) return;
            if (ReferenceEquals(Demosaic.AhdGpu, _policy.Installed))
                Demosaic.AhdGpu = _policy.PreviousAccelerator;
            CurrentPolicy.Value = _previous;
        }
    }

    internal static float[]? TryRun(float[] mosaic, int width, int height, Cfa cfa)
    {
        var policy = CurrentPolicy.Value;
        if (policy is not { Enabled: true }) return null;
        if ((long)width * height < MinPixels || !cfa.IsBayer || !FitsAhdMemory(width, height, policy.MemoryBudgetBytes)) return null;
        lock (Gate)
        {
            SelectBackend(policy.Backend);
            if (s_ahdDisabled) return null;
            try
            {
                var runner = GetRunner(policy);
                if (runner is null) { DisableAhd("no hardware GPU"); return null; }
                if (!runner.FitsAhd(width, height, policy.MemoryBudgetBytes)) return null;
                return runner.Run(mosaic, width, height, cfa);
            }
            catch (Exception ex) when (ex is GpuException or WebGpuSharp.WebGPUException or InvalidOperationException or DllNotFoundException or BadImageFormatException)
            {
                DisableAhd(ex.Message);
                if (IsDeviceLoss(ex)) ResetRunner();
                GpuDevice.Log?.Invoke($"GPU AHD 停用，改用 CPU：{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// RAW 場景顯影的色彩收尾放到 GPU。回傳已提交的列數：等於高度表示整張完成；
    /// 小於高度時呼叫端只對未提交的後綴跑 CPU，避免重複轉換已完成的列。
    /// 範圍外、條件不符或整張失敗時回傳 0，由 CPU 處理整張。
    /// </summary>
    internal static int TryApplyColour(RgbImage image, float[] wb, float[] matrix, float gain, ProfileTables? tables, HueSat? hueSat)
    {
        var policy = CurrentPolicy.Value;
        if (policy is not { Enabled: true }) return 0;
        if ((long)image.Width * image.Height < MinPixels
            || !FitsColourMemory(image.Width, image.Height, PackedFloats(tables, hueSat), policy.MemoryBudgetBytes)) return 0;
        lock (Gate)
        {
            SelectBackend(policy.Backend);
            if (s_colourDisabled) return 0;
            var committed = 0;
            try
            {
                var runner = GetRunner(policy);
                if (runner is null) { DisableColour("no hardware GPU"); return 0; }
                runner.ApplyColour(image, wb, matrix, gain, tables, hueSat, policy.MemoryBudgetBytes, ref committed);
                return committed;
            }
            catch (Exception ex) when (ex is GpuException or WebGpuSharp.WebGPUException or InvalidOperationException or DllNotFoundException or BadImageFormatException)
            {
                // 已提交的前綴保留（呼叫端只重跑未提交後綴）；此功能之後停用。
                DisableColour(ex.Message);
                if (IsDeviceLoss(ex)) ResetRunner();
                GpuDevice.Log?.Invoke($"GPU 色彩收尾停用，改用 CPU：{ex.Message}");
                return committed;
            }
        }
    }

    /// <summary>測試與 smoke 用的可用性預檢：只判斷硬體裝置是否存在，不編譯 shader、不改變停用狀態。</summary>
    internal static string? ProbeUnavailableReason(GpuBackend backend)
    {
        try
        {
            using var gpu = GpuDevice.Create(new GpuDeviceOptions { Backend = backend });
            if (gpu.AdapterInfo.IsSoftware) return "software adapter only";
            return null;
        }
        catch (Exception ex) when (ex is GpuException or WebGpuSharp.WebGPUException or InvalidOperationException or DllNotFoundException or BadImageFormatException)
        {
            return ex.Message;
        }
    }

    /// <summary>最近一次各功能的停用原因（無則 null）。供 smoke 觀察實際走了哪條路徑；不做永久遙測。</summary>
    internal static (string? Ahd, string? Colour) LastErrors
    {
        get { lock (Gate) { return (s_ahdError, s_colourError); } }
    }

    private static void SelectBackend(GpuBackend backend)
    {
        if (s_backend == backend) return;
        ResetRunner();
        s_backend = backend;
        s_ahdDisabled = s_colourDisabled = EnvDisabled;
        s_ahdError = s_colourError = null;
    }

    private static Runner? GetRunner(Policy policy)
    {
        // 預算變更不重建裝置；後端切換才清除該裝置的功能失敗狀態。
        return s_runner ??= Runner.Create(policy.Backend);
    }

    private static void ResetRunner()
    {
        try { s_runner?.Dispose(); } catch { }
        s_runner = null;
    }

    private static void DisableAhd(string reason)
    {
        s_ahdDisabled = true;
        s_ahdError ??= reason;
    }

    private static void DisableColour(string reason)
    {
        s_colourDisabled = true;
        s_colourError ??= reason;
    }

    private static bool IsDeviceLoss(Exception ex)
    {
        if (s_runner?.DeviceLost == true) return true;
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is GpuException g && (g.Message.Contains("裝置遺失", StringComparison.Ordinal)
                || g.Message.Contains("device lost", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static long PackedFloats(ProfileTables? tables, HueSat? hueSat) =>
        Math.Max(1L, (long)(tables?.HueSatTable?.Data.Length ?? 0)
            + (tables?.LookTable?.Data.Length ?? 0) + (hueSat?.Table.Data.Length ?? 0));

    private static bool FitsAhdMemory(int w, int h, long budget)
    {
        if (w <= 0 || h <= 0 || (long)w * h > Array.MaxLength / 3) return false;
        var pixels = ((long)w + 2 * Demosaic.AhdBandMargin) * (BandRows + 2 * Demosaic.AhdBandMargin);
        var output = (long)w * BandRows * 3;
        // Device buffers plus host result and mosaic/CFA scratch; no full-result allocation before admission.
        var bytes = pixels * 88 + output * 8 + 16 + (long)w * h * 12 + pixels * 8;
        return budget <= 0 || bytes <= budget;
    }

    private static bool FitsColourMemory(int w, int h, long packedFloats, long budget)
    {
        var band = (long)w * Math.Min(ColourBandRows, h) * 3;
        if (band > Array.MaxLength || packedFloats > Array.MaxLength) return false;
        // Pixel/readback buffers, transactional host scratch, and host/device tables.
        var bytes = band * 12 + packedFloats * 8 + Marshal.SizeOf<ColourParams>();
        return budget <= 0 || bytes <= budget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Params(int Bw, int Bh, int W, int Rows);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Int4(int X, int Y, int Z, int W);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TableInfo(Int4 Div, Int4 Info);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ColourParams(Vector4 Wb, Vector4 M0, Vector4 M1, Vector4 M2,
        Vector4 To0, Vector4 To1, Vector4 To2, Vector4 From0, Vector4 From1, Vector4 From2,
        Int4 Info, TableInfo T0, TableInfo T1, TableInfo T2);

    private sealed class Runner : IDisposable
    {
        private readonly GpuDevice _gpu;
        private readonly ShaderLibrary _shaders = new();
        private ComputeKernel? _green, _complete, _homogeneity, _select, _colour;

        private Runner(GpuDevice gpu)
        {
            _gpu = gpu;
        }

        internal bool DeviceLost => _gpu.IsLost;

        internal static Runner? Create(GpuBackend backend)
        {
            var gpu = GpuDevice.Create(new GpuDeviceOptions { Backend = backend });
            if (gpu.AdapterInfo.IsSoftware) { gpu.Dispose(); return null; }
            return new Runner(gpu);
        }

        private static Vector4 Row(float[]? m, int r) =>
            m is null ? default : new(m[r * 3], m[r * 3 + 1], m[r * 3 + 2], 0);

        private ComputeKernel Green => _green ??= AhdKernel("raw/ahd_green.wgsl", [BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.Storage, BindingSpec.Storage, BindingSpec.Uniform], "ahd green");
        private ComputeKernel Complete => _complete ??= AhdKernel("raw/ahd_complete.wgsl", [BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.Storage, BindingSpec.Storage, BindingSpec.Uniform], "ahd complete");
        private ComputeKernel Homogeneity => _homogeneity ??= AhdKernel("raw/ahd_homogeneity.wgsl", [BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.Storage, BindingSpec.Storage, BindingSpec.Uniform], "ahd homogeneity");
        private ComputeKernel Select => _select ??= AhdKernel("raw/ahd_select.wgsl", [BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.StorageRead, BindingSpec.Storage, BindingSpec.Uniform], "ahd select");
        private ComputeKernel Colour => _colour ??= _gpu.CreateKernel(_shaders.Load("raw/apply_colour.wgsl"), "main", [BindingSpec.Storage, BindingSpec.StorageRead, BindingSpec.Uniform], "raw apply colour");

        private ComputeKernel AhdKernel(string path, BindingSpec[] bindings, string label) =>
            _gpu.CreateKernel(_shaders.Load(path), "main", bindings, label);

        internal bool FitsAhd(int w, int h, long budgetBytes)
        {
            var margin = Demosaic.AhdBandMargin;
            var bw = (long)w + 2 * margin;
            var maxPx = bw * (BandRows + 2 * margin);
            var maxOut = (long)w * BandRows * 3;
            var largest = Math.Max(maxPx * 16, maxOut * 4);
            if (!FitsDevice(largest)) return false;
            return FitsAhdMemory(w, h, budgetBytes);
        }

        internal bool FitsColour(int w, int h, int packedFloats, long budgetBytes)
        {
            var rows = Math.Min(ColourBandRows, h);
            var band = (long)w * rows * 3;
            var largest = Math.Max(band * 4, (long)packedFloats * 4);
            if (!FitsDevice(largest)) return false;
            return FitsColourMemory(w, h, packedFloats, budgetBytes);
        }

        private bool FitsDevice(long largestBytes)
        {
            var info = _gpu.AdapterInfo;
            return (ulong)largestBytes <= info.MaxBufferSize
                && (ulong)largestBytes <= info.MaxStorageBufferBindingSize;
        }


        /// <summary>經 <paramref name="committed"/> 回報已提交的列數；整張完成時等於高度。失敗的帶不寫入 image.Data。</summary>
        internal void ApplyColour(RgbImage image, float[] wb, float[] matrix, float gain, ProfileTables? tables, HueSat? hueSat, long budgetBytes, ref int committed)
        {
            var w = image.Width; var h = image.Height;
            var packedCount = PackedFloats(tables, hueSat);
            if (!FitsColourMemory(w, h, packedCount, budgetBytes) || !FitsColour(w, h, (int)packedCount, budgetBytes)) return;
            var packed = new float[(int)packedCount];
            var next = 0;
            TableInfo Pack(HsvTable? t)
            {
                if (t is null) return default;
                var offset = next;
                t.Data.AsSpan().CopyTo(packed.AsSpan(next));
                next += t.Data.Length;
                return new TableInfo(new Int4(t.HueDivisions, t.SatDivisions, t.ValDivisions, t.SrgbValue ? 1 : 0), new Int4(offset, 0, 0, 0));
            }
            var t0 = Pack(tables?.HueSatTable);
            var t1 = Pack(tables?.LookTable);
            var t2 = Pack(hueSat?.Table);
            var flags = (tables is not null ? 1 : 0) | (tables?.HueSatTable is not null ? 2 : 0)
                | (tables?.LookTable is not null ? 4 : 0) | (hueSat is not null ? 8 : 0);
            var to = tables?.ToProPhoto ?? hueSat?.To;
            var from = tables?.FromProPhoto ?? hueSat?.From;
            var colour = Colour;
            var maxFloats = (long)w * Math.Min(ColourBandRows, h) * 3;
            using var pixels = _gpu.CreateBuffer((ulong)maxFloats * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst | GpuBufferUsage.CopySrc, "colour pixels");
            using var readback = _gpu.CreateBuffer((ulong)maxFloats * 4, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "colour readback");
            using var table = _gpu.CreateBuffer((ulong)packed.Length * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "colour tables");
            using var uniform = _gpu.CreateBuffer((ulong)Marshal.SizeOf<ColourParams>(), GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "colour params");
            _gpu.WriteBuffer<float>(table, packed);
            committed = 0;
            var scratch = new float[maxFloats];
            for (var y0 = 0; y0 < h; y0 += ColourBandRows)
            {
                var rows = Math.Min(ColourBandRows, h - y0);
                var count = w * rows * 3;
                _gpu.WriteBuffer<float>(pixels, image.Data.AsSpan(y0 * w * 3, count));
                _gpu.WriteBuffer(uniform, new ColourParams(new Vector4(wb[0], wb[1], wb[2], gain),
                    Row(matrix, 0), Row(matrix, 1), Row(matrix, 2), Row(to, 0), Row(to, 1), Row(to, 2),
                    Row(from, 0), Row(from, 1), Row(from, 2), new Int4(w, rows, flags, 0), t0, t1, t2));
                using (var commands = _gpu.BeginCommands())
                {
                    commands.Dispatch2D(colour, [pixels, table, uniform], (uint)w, (uint)rows);
                    commands.CopyBuffer(pixels, readback, (ulong)count * 4);
                    commands.Submit();
                }
                // 先讀到暫存再提交：失敗的帶不污染 image.Data，呼叫端只重跑未提交後綴。
                _gpu.ReadBuffer(readback, scratch, 0, count);
                Buffer.BlockCopy(scratch, 0, image.Data, y0 * w * 3 * sizeof(float), count * sizeof(float));
                committed = y0 + rows;
            }
        }

        internal float[] Run(float[] data, int w, int h, Cfa cfa)
        {
            var margin = Demosaic.AhdBandMargin;
            var bw = w + 2 * margin;
            var maxPx = (long)bw * (BandRows + 2 * margin);
            var maxOut = (long)w * BandRows * 3;
            var result = new float[checked((long)w * h * 3)];
            var mos = new float[maxPx]; var col = new uint[maxPx];
            var f32 = (ulong)sizeof(float);
            // 觸發 AHD kernels 編譯：色彩 kernel 失敗不影響此處，反之亦然。
            var green = Green; var complete = Complete; var homogeneity = Homogeneity; var select = Select;
            using var bMos = _gpu.CreateBuffer((ulong)maxPx * f32, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "ahd mos");
            using var bCol = _gpu.CreateBuffer((ulong)maxPx * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "ahd col");
            using var gh = _gpu.CreateBuffer((ulong)maxPx * f32, GpuBufferUsage.Storage, "ahd gh");
            using var gv = _gpu.CreateBuffer((ulong)maxPx * f32, GpuBufferUsage.Storage, "ahd gv");
            using var rh = _gpu.CreateBuffer((ulong)maxPx * 16, GpuBufferUsage.Storage, "ahd rh");
            using var lh = _gpu.CreateBuffer((ulong)maxPx * 16, GpuBufferUsage.Storage, "ahd lh");
            using var rv = _gpu.CreateBuffer((ulong)maxPx * 16, GpuBufferUsage.Storage, "ahd rv");
            using var lv = _gpu.CreateBuffer((ulong)maxPx * 16, GpuBufferUsage.Storage, "ahd lv");
            using var hh = _gpu.CreateBuffer((ulong)maxPx * 4, GpuBufferUsage.Storage, "ahd hh");
            using var hv = _gpu.CreateBuffer((ulong)maxPx * 4, GpuBufferUsage.Storage, "ahd hv");
            using var output = _gpu.CreateBuffer((ulong)maxOut * f32, GpuBufferUsage.Storage | GpuBufferUsage.CopySrc, "ahd out");
            using var readback = _gpu.CreateBuffer((ulong)maxOut * f32, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "ahd readback");
            using var uniform = _gpu.CreateBuffer(16, GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "ahd params");

            for (var y0 = 0; y0 < h; y0 += BandRows)
            {
                var rows = Math.Min(BandRows, h - y0);
                var bh = rows + 2 * margin;
                Demosaic.FillAhdBand(data, w, h, cfa, y0, rows, mos, col);
                var count = checked(bw * bh);
                _gpu.WriteBuffer<float>(bMos, mos.AsSpan(0, count));
                _gpu.WriteBuffer<uint>(bCol, col.AsSpan(0, count));
                _gpu.WriteBuffer(uniform, new Params(bw, bh, w, rows));
                using (var commands = _gpu.BeginCommands())
                {
                    commands.Dispatch2D(green, [bMos, bCol, gh, gv, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(complete, [bMos, bCol, gh, rh, lh, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(complete, [bMos, bCol, gv, rv, lv, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(homogeneity, [lh, lv, hh, hv, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(select, [rh, rv, hh, hv, output, uniform], (uint)w, (uint)rows);
                    var bytes = (ulong)w * (ulong)rows * 3 * f32;
                    commands.CopyBuffer(output, readback, bytes);
                    commands.Submit();
                }
                _gpu.ReadBuffer(readback, result, y0 * w * 3, rows * w * 3);
            }
            return result;
        }

        public void Dispose()
        {
            _green?.Dispose(); _complete?.Dispose(); _homogeneity?.Dispose(); _select?.Dispose(); _colour?.Dispose();
            _gpu.Dispose();
        }
    }
}
