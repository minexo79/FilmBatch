using System.Diagnostics;
using System.Text;
using FilmBatch.Engine.Imaging;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// GPU spike 的量測與驗證（計畫書 16.1）：完整路徑的延遲、拖曳時的更新率，
/// 以及 GPU 結果與 CPU 參考實作的差異（在 sRGB 8-bit 上量測最大通道差，作為 ΔE 的保守替代）。
/// </summary>
public static class GpuSpikeBenchmark
{
    public sealed record Report(
        GpuAdapterInfo Adapter,
        int Width,
        int Height,
        TimeSpan DeviceCreate,
        TimeSpan FirstFrame,
        TimeSpan AverageFrame,
        TimeSpan AverageFrameWithBlur,
        int MaxChannelDiffVsCpu,
        double MeanChannelDiffVsCpu)
    {
        public double Fps => 1.0 / AverageFrame.TotalSeconds;
        public double FpsWithBlur => 1.0 / AverageFrameWithBlur.TotalSeconds;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"介面卡         : {Adapter.DisplayName}（{Adapter.Kind}）");
            sb.AppendLine($"驅動程式       : {Adapter.Driver}");
            sb.AppendLine($"紋理上限       : {Adapter.MaxTextureDimension2D}px，緩衝區上限 {Adapter.MaxBufferSize / (1024 * 1024)}MB");
            sb.AppendLine($"影像           : {Width}×{Height}");
            sb.AppendLine($"建立裝置       : {DeviceCreate.TotalMilliseconds:F0} ms");
            sb.AppendLine($"首張（含上傳） : {FirstFrame.TotalMilliseconds:F1} ms");
            sb.AppendLine($"拖曳更新       : {AverageFrame.TotalMilliseconds:F1} ms（{Fps:F0} fps）");
            sb.AppendLine($"拖曳更新＋模糊 : {AverageFrameWithBlur.TotalMilliseconds:F1} ms（{FpsWithBlur:F0} fps）");
            sb.Append($"與 CPU 參考差異: 最大 {MaxChannelDiffVsCpu}/255，平均 {MeanChannelDiffVsCpu:F3}/255");
            return sb.ToString();
        }
    }

    /// <summary>以合成影像（預設 1600×1067，即預覽代理圖尺寸）執行完整路徑。</summary>
    public static Report Run(GpuDeviceOptions? options = null, int width = 1600, int height = 1067, int frames = 30)
    {
        var source = Synthetic(width, height);
        var sw = Stopwatch.StartNew();
        using var gpu = GpuSpikeRenderer.Create(options);
        var deviceCreate = sw.Elapsed;

        var first = gpu.Render(source, new SpikeParams(0.2f, 0.1f, 0.2f, 0));

        // 模擬拖曳滑桿：每一幀參數都不同（只更新 uniform）
        sw.Restart();
        for (var i = 0; i < frames; i++)
            gpu.Render(source, new SpikeParams(i * 0.02f, 0.1f, -0.5f + i / (float)frames, 0));
        var avg = sw.Elapsed / frames;

        sw.Restart();
        for (var i = 0; i < frames; i++)
            gpu.Render(source, new SpikeParams(0.1f, 0, 0.2f, 0.004f + i * 0.0003f));
        var avgBlur = sw.Elapsed / frames;

        // 正確性：與 CPU 參考實作比對
        var p = new SpikeParams(0.3f, 0.15f, 0.25f, 0.006f);
        var g = gpu.Render(source, p).Bgra;
        var c = new CpuFakeRenderer().Render(source, p).Bgra;
        int max = 0;
        long sum = 0;
        for (var i = 0; i < g.Length; i++)
        {
            var d = Math.Abs(g[i] - c[i]);
            max = Math.Max(max, d);
            sum += d;
        }

        return new Report(gpu.AdapterInfo, width, height, deviceCreate, first.Elapsed, avg, avgBlur, max, (double)sum / g.Length);
    }

    /// <summary>合成測試影像：漸層＋高光點，不需讀檔。</summary>
    public static SourceImage Synthetic(int w, int h)
    {
        var data = new ushort[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                data[i] = (ushort)(65535L * x / Math.Max(1, w - 1));
                data[i + 1] = (ushort)(65535L * y / Math.Max(1, h - 1));
                data[i + 2] = (ushort)(((x / 64 + y / 64) % 2) * 40000 + 12000);
                data[i + 3] = 65535;
                if ((x - w / 2) * (x - w / 2) + (y - h / 2) * (y - h / 2) < 400)
                    data[i] = data[i + 1] = data[i + 2] = 65535;
            }
        });
        return new SourceImage(w, h, data);
    }
}
