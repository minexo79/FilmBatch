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
    private static bool s_colourDisabled = s_disabled;

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

    /// <summary>RAW 場景顯影的色彩收尾放到 GPU（就地改寫 <paramref name="image"/>）；條件不符或失敗時回傳 false，由 CPU 實作接手。</summary>
    internal static bool TryApplyColour(RgbImage image, float[] wb, float[] matrix, float gain, ProfileTables? tables, HueSat? hueSat)
    {
        if (s_colourDisabled || (long)image.Width * image.Height < MinPixels) return false;
        lock (Gate)
        {
            if (s_colourDisabled) return false;
            try
            {
                s_runner ??= Runner.Create();
                if (s_runner is null) { s_colourDisabled = true; return false; }
                s_runner.ApplyColour(image, wb, matrix, gain, tables, hueSat);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                s_colourDisabled = true;
                GpuDevice.Log?.Invoke($"GPU 色彩收尾停用，改用 CPU：{ex.Message}");
                return false;
            }
        }
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
        private readonly ComputeKernel _green, _complete, _homogeneity, _select, _colour;

        private Runner(GpuDevice gpu)
        {
            _gpu = gpu;
            var shaders = new ShaderLibrary();
            var ro = BindingSpec.StorageRead; var rw = BindingSpec.Storage; var u = BindingSpec.Uniform;
            _green = gpu.CreateKernel(shaders.Load("raw/ahd_green.wgsl"), "main", [ro, ro, rw, rw, u], "ahd green");
            _complete = gpu.CreateKernel(shaders.Load("raw/ahd_complete.wgsl"), "main", [ro, ro, ro, rw, rw, u], "ahd complete");
            _homogeneity = gpu.CreateKernel(shaders.Load("raw/ahd_homogeneity.wgsl"), "main", [ro, ro, rw, rw, u], "ahd homogeneity");
            _select = gpu.CreateKernel(shaders.Load("raw/ahd_select.wgsl"), "main", [ro, ro, ro, ro, rw, u], "ahd select");
            _colour = gpu.CreateKernel(shaders.Load("raw/apply_colour.wgsl"), "main", [rw, ro, u], "raw apply colour");
        }

        internal static Runner? Create()
        {
            var gpu = GpuDevice.Create();
            if (gpu.AdapterInfo.IsSoftware) { gpu.Dispose(); return null; }
            try { return new Runner(gpu); }
            catch { gpu.Dispose(); throw; }
        }


        private static Vector4 Row(float[] m, int r) => new(m[r * 3], m[r * 3 + 1], m[r * 3 + 2], 0);

        internal void ApplyColour(RgbImage image, float[] wb, float[] matrix, float gain, ProfileTables? tables, HueSat? hueSat)
        {
            var packed = new List<float>();
            TableInfo Pack(HsvTable? t)
            {
                if (t is null) return default;
                var offset = packed.Count;
                packed.AddRange(t.Data);
                return new TableInfo(new Int4(t.HueDivisions, t.SatDivisions, t.ValDivisions, t.SrgbValue ? 1 : 0), new Int4(offset, 0, 0, 0));
            }
            var t0 = Pack(tables?.HueSatTable);
            var t1 = Pack(tables?.LookTable);
            var t2 = Pack(hueSat?.Table);
            if (packed.Count == 0) packed.Add(0f);
            var flags = (tables is not null ? 1 : 0) | (tables?.HueSatTable is not null ? 2 : 0)
                | (tables?.LookTable is not null ? 4 : 0) | (hueSat is not null ? 8 : 0);
            var to = tables?.ToProPhoto ?? hueSat?.To ?? new float[9];
            var from = tables?.FromProPhoto ?? hueSat?.From ?? new float[9];
            var w = image.Width; var h = image.Height;
            const int rowsPerBand = 512;
            var maxFloats = (long)w * Math.Min(rowsPerBand, h) * 3;
            using var pixels = _gpu.CreateBuffer((ulong)maxFloats * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst | GpuBufferUsage.CopySrc, "colour pixels");
            using var readback = _gpu.CreateBuffer((ulong)maxFloats * 4, GpuBufferUsage.MapRead | GpuBufferUsage.CopyDst, "colour readback");
            using var table = _gpu.CreateBuffer((ulong)packed.Count * 4, GpuBufferUsage.Storage | GpuBufferUsage.CopyDst, "colour tables");
            using var uniform = _gpu.CreateBuffer((ulong)Marshal.SizeOf<ColourParams>(), GpuBufferUsage.Uniform | GpuBufferUsage.CopyDst, "colour params");
            _gpu.WriteBuffer<float>(table, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packed));
            for (var y0 = 0; y0 < h; y0 += rowsPerBand)
            {
                var rows = Math.Min(rowsPerBand, h - y0);
                var count = w * rows * 3;
                _gpu.WriteBuffer<float>(pixels, image.Data.AsSpan(y0 * w * 3, count));
                _gpu.WriteBuffer(uniform, new ColourParams(new Vector4(wb[0], wb[1], wb[2], gain),
                    Row(matrix, 0), Row(matrix, 1), Row(matrix, 2), Row(to, 0), Row(to, 1), Row(to, 2),
                    Row(from, 0), Row(from, 1), Row(from, 2), new Int4(w, rows, flags, 0), t0, t1, t2));
                using (var commands = _gpu.BeginCommands())
                {
                    commands.Dispatch2D(_colour, [pixels, table, uniform], (uint)w, (uint)rows);
                    commands.CopyBuffer(pixels, readback, (ulong)count * 4);
                    commands.Submit();
                }
                _gpu.ReadBuffer(readback, image.Data, y0 * w * 3, count);
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
            _green.Dispose(); _complete.Dispose(); _homogeneity.Dispose(); _select.Dispose(); _colour.Dispose();
            _gpu.Dispose();
        }
    }
}
