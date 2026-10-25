using System.Runtime.InteropServices;
using FilmBatch.Gpu.Device;
using FilmBatch.Gpu.Resources;
using FilmBatch.Gpu.ShaderSystem;
using FilmBatch.Raw;
using FilmBatch.Raw.Develop;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 解碼專用的 GPU AHD 去馬賽克：自己持有一個獨立的 <see cref="GpuDevice"/>（解碼執行緒沒有算圖器的裝置），
/// 以帶狀分段處理整張馬賽克（每帶 <see cref="BandRows"/> 列加 6 像素邊界），暫時顯示記憶體約 170 MB。
/// 沒有硬體 GPU、影像太小、裝置遺失或任何錯誤時回傳 null，由 CPU 實作接手；失敗一次後本程序不再嘗試。
/// 環境變數 <c>FILMBATCH_NO_GPU_DEMOSAIC=1</c> 可停用。
/// </summary>
internal static class RawGpuDemosaic
{
    private const int BandRows = 256;
    private const long MinPixels = 4_000_000;
    private static readonly object Gate = new();
    private static Runner? s_runner;
    private static bool s_disabled = Environment.GetEnvironmentVariable("FILMBATCH_NO_GPU_DEMOSAIC") == "1";

    internal static void Install() => Demosaic.AhdGpu ??= TryRun;

    internal static float[]? TryRun(float[] mosaic, int width, int height, Cfa cfa)
    {
        if (s_disabled || (long)width * height < MinPixels || !cfa.IsBayer) return null;
        lock (Gate)
        {
            if (s_disabled) return null;
            try
            {
                s_runner ??= Runner.Create();
                if (s_runner is null) { s_disabled = true; return null; }
                return s_runner.Run(mosaic, width, height, cfa);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                s_disabled = true;
                try { s_runner?.Dispose(); } catch { }
                s_runner = null;
                GpuDevice.Log?.Invoke($"GPU AHD 停用，改用 CPU：{ex.Message}");
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Params(int Bw, int Bh, int W, int Rows);

    private sealed class Runner : IDisposable
    {
        private readonly GpuDevice _gpu;
        private readonly ComputeKernel _green, _complete, _homogeneity, _select;

        private Runner(GpuDevice gpu)
        {
            _gpu = gpu;
            var shaders = new ShaderLibrary();
            var ro = BindingSpec.StorageRead; var rw = BindingSpec.Storage; var u = BindingSpec.Uniform;
            _green = gpu.CreateKernel(shaders.Load("raw/ahd_green.wgsl"), "main", [ro, ro, rw, rw, u], "ahd green");
            _complete = gpu.CreateKernel(shaders.Load("raw/ahd_complete.wgsl"), "main", [ro, ro, ro, rw, rw, u], "ahd complete");
            _homogeneity = gpu.CreateKernel(shaders.Load("raw/ahd_homogeneity.wgsl"), "main", [ro, ro, rw, rw, u], "ahd homogeneity");
            _select = gpu.CreateKernel(shaders.Load("raw/ahd_select.wgsl"), "main", [ro, ro, ro, ro, rw, u], "ahd select");
        }

        internal static Runner? Create()
        {
            var gpu = GpuDevice.Create();
            if (gpu.AdapterInfo.IsSoftware) { gpu.Dispose(); return null; }
            try { return new Runner(gpu); }
            catch { gpu.Dispose(); throw; }
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
                    commands.Dispatch2D(_green, [bMos, bCol, gh, gv, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(_complete, [bMos, bCol, gh, rh, lh, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(_complete, [bMos, bCol, gv, rv, lv, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(_homogeneity, [lh, lv, hh, hv, uniform], (uint)bw, (uint)bh);
                    commands.Dispatch2D(_select, [rh, rv, hh, hv, output, uniform], (uint)w, (uint)rows);
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
            _green.Dispose(); _complete.Dispose(); _homogeneity.Dispose(); _select.Dispose();
            _gpu.Dispose();
        }
    }
}
