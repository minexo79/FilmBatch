using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.App.ViewModels.Dialogs;
using FilmBatch.Core;

namespace FilmBatch.App.Tests;

/// <summary>改動程序共用 <see cref="AppLog.DebugEnabled"/> 的測試集合：建立主視窗 VM 也會重設它，因此不與其他測試並行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DebugLoggingCollection
{
    public const string Name = "DebugLogging";
}

/// <summary>偏好設定的除錯模式（DEBUG 記錄即時開關、存入 settings.json）與開啟記錄資料夾。</summary>
[Collection(DebugLoggingCollection.Name)]
public sealed class DebugLoggingPreferenceTests
{
    [Fact]
    public void OpenFolder_StartInfo_PassesFolderAsSingleArgument()
    {
        var windows = FileReveal.CreateOpenFolderStartInfo(@"D:\使用者 資料\logs", FileReveal.Platform.Windows);
        Assert.Equal("explorer.exe", windows.FileName);
        Assert.Equal([@"D:\使用者 資料\logs"], windows.ArgumentList);
        Assert.Equal("open", FileReveal.CreateOpenFolderStartInfo("/Users/me/logs", FileReveal.Platform.MacOS).FileName);
        Assert.Equal("xdg-open", FileReveal.CreateOpenFolderStartInfo("/home/me/logs", FileReveal.Platform.Linux).FileName);
    }

    [AvaloniaFact]
    public void Toggle_GatesDebugLines_AppliesImmediately_AndPersists()
    {
        using var main = TestEnv.CreateViewModel();
        try
        {
            Assert.False(AppLog.DebugEnabled);
            var prefs = new PreferencesViewModel(main);
            Assert.False(prefs.DebugLogging);

            var hidden = $"debug-off-{Guid.NewGuid():N}";
            AppLog.Debug(hidden);
            prefs.DebugLogging = true;
            Assert.True(AppLog.DebugEnabled);
            var shown = $"debug-on-{Guid.NewGuid():N}";
            AppLog.Debug(shown);
            var log = File.ReadAllText(AppLog.CurrentFile);
            Assert.DoesNotContain(hidden, log);
            Assert.Contains($"[DEBUG] {shown}", log);

            Assert.True(prefs.Apply());
            Assert.True(AppSettings.Load().DebugLogging);
            Assert.True(new PreferencesViewModel(main).DebugLogging);

            prefs.DebugLogging = false;
            Assert.True(prefs.Apply());
            Assert.False(AppLog.DebugEnabled);
            Assert.False(AppSettings.Load().DebugLogging);
        }
        finally { AppLog.DebugEnabled = false; }
    }

    [AvaloniaFact]
    public void OpenLogFolder_OpensLogsPath_AndReportsFailure()
    {
        using var main = TestEnv.CreateViewModel();
        var opened = new List<string>();
        var prefs = new PreferencesViewModel(main) { OpenFolder = opened.Add };
        Assert.Equal(AppPaths.Logs, prefs.LogFolder);
        Assert.True(prefs.OpenLogFolderCommand.CanExecute(null));
        prefs.OpenLogFolderCommand.Execute(null);
        Assert.Equal([AppPaths.Logs], opened);

        prefs.OpenFolder = _ => throw new InvalidOperationException("boom");
        prefs.OpenLogFolderCommand.Execute(null);
        Assert.Contains("boom", main.Toast.Message);
    }
}
