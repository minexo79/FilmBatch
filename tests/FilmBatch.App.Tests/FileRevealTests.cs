using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;

namespace FilmBatch.App.Tests;

/// <summary>縮圖列「在檔案瀏覽器中查看圖片」：各平台的啟動參數與命令可用性。</summary>
public sealed class FileRevealTests
{
    [Fact]
    public void Windows_SelectsFileWithQuotedNonAsciiPath()
    {
        var info = FileReveal.CreateStartInfo(@"D:\修圖大賞\第 1 輪\IMG 0001.ARW", FileReveal.Platform.Windows);
        Assert.Equal("explorer.exe", info.FileName);
        Assert.Equal("/select,\"D:\\修圖大賞\\第 1 輪\\IMG 0001.ARW\"", info.Arguments);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void MacOS_RevealsInFinder()
    {
        var info = FileReveal.CreateStartInfo("/Users/me/相片 集/a b.jpg", FileReveal.Platform.MacOS);
        Assert.Equal("open", info.FileName);
        Assert.Equal(["-R", "/Users/me/相片 集/a b.jpg"], info.ArgumentList);
    }

    [Fact]
    public void Linux_OpensContainingFolder()
    {
        var info = FileReveal.CreateStartInfo("/home/me/photos/a b.jpg", FileReveal.Platform.Linux);
        Assert.Equal("xdg-open", info.FileName);
        Assert.Equal([Path.GetDirectoryName("/home/me/photos/a b.jpg")!], info.ArgumentList);
    }

    [AvaloniaFact]
    public async Task Command_RevealsFocusedPhoto_DisabledWithoutSelection_AndReportsFailure()
    {
        using var vm = TestEnv.CreateViewModel();
        var revealed = new List<string>();
        vm.RevealFile = revealed.Add;
        Assert.False(vm.RevealInFileManagerCommand.CanExecute(null));

        await TestEnv.OpenSamplesAsync(vm);
        var file = vm.Filmstrip.Items[2].FileName;
        vm.Filmstrip.SelectOnly(file);
        Assert.True(vm.RevealInFileManagerCommand.CanExecute(null));
        vm.RevealInFileManagerCommand.Execute(null);
        Assert.Equal([vm.Service!.Project.PathOf(file)], revealed);

        vm.RevealFile = _ => throw new InvalidOperationException("boom");
        vm.RevealInFileManagerCommand.Execute(null);
        Assert.Contains("boom", vm.Toast.Message);

        vm.Filmstrip.Selection.Clear();
        Assert.False(vm.RevealInFileManagerCommand.CanExecute(null));
    }
}
