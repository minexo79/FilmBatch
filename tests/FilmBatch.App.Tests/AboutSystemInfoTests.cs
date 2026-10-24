using Avalonia.Headless.XUnit;
using FilmBatch.App.ViewModels.Dialogs;

namespace FilmBatch.App.Tests;

/// <summary>「關於」顯示處理器型號／執行緒數與實體記憶體，並納入複製的版本資訊。</summary>
public sealed class AboutSystemInfoTests
{
    [AvaloniaFact]
    public void About_ShowsCpuAndMemory_InPanelAndSummary()
    {
        var vm = new AboutViewModel("Test GPU");
        Assert.Contains(Environment.ProcessorCount.ToString(), vm.Cpu);
        Assert.EndsWith(" GB", vm.Memory);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() + " (", vm.Cpu);
        Assert.Contains(vm.Cpu, vm.Summary);
        Assert.Contains(vm.Memory, vm.Summary);
    }

    [Theory]
    [InlineData(16L * 1024 * 1024 * 1024, "16 GB")]
    [InlineData(31L * 1024 * 1024 * 1024 + 900L * 1024 * 1024, "31.9 GB")]
    [InlineData(0L, "—")]
    public void FormatMemory_RoundsToTenthsOfAGigabyte(long bytes, string expected) =>
        Assert.Equal(expected, AboutViewModel.FormatMemory(bytes));
}
