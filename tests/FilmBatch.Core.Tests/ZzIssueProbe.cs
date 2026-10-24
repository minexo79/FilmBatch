// 拋棄式診斷（不提交）：JPG 暗部藍點。
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;
using FilmBatch.Gpu.Device;

namespace FilmBatch.Core.Tests;

public sealed class ZzIssueProbe
{
    [Fact]
    public void Probe()
    {
        var dir = Environment.GetEnvironmentVariable("FB_PROBE_DIR");
        if (dir is null) { Assert.Skip("no probe dir"); return; }
        var log = new System.Text.StringBuilder();
        using var gpu = new GpuSpikeRenderer(GpuDevice.Create());
        using var cpu = new CpuFakeRenderer();
        var src = ImageLoader.Load(Path.Combine(dir, "IMG_7548.jpg"), 2560);
        var d = ModuleCatalog.Defaults;
        foreach (var (tag, s) in new[] { ("none", d), ("C", d.With("basic.contrast", 53.0)), ("S", d.With("basic.shadows", -99.0)), ("St", d.With("basic.structure", -100.0)),
            ("C+S", d.With("basic.contrast", 53.0).With("basic.shadows", -99.0)), ("C+St", d.With("basic.contrast", 53.0).With("basic.structure", -100.0)), ("S+St", d.With("basic.shadows", -99.0).With("basic.structure", -100.0)) })
        {
            var r = cpu;
            var o = r.Render(src, s, TestContext.Current.CancellationToken);
            int blue = 0;
            for (var i = 0; i < o.Bgra.Length; i += 4)
                if (o.Bgra[i] > 120 && o.Bgra[i + 1] < 40 && o.Bgra[i + 2] < 40) blue++;
            log.AppendLine($"{tag}: pureBlue={blue}");
        }
        File.WriteAllText(Path.Combine(dir, "probe_blue.txt"), log.ToString());
    }
}
