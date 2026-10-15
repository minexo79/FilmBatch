using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Gpu.Device;

namespace FilmBatch.App.ViewModels.Dialogs;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 偏好設定（計畫書 2.8）。預覽背景、隱藏未啟用模組與語言立即生效；
/// GPU 介面卡與後端於下次啟動生效（M3 起改為即時重建裝置）。
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public IReadOnlyList<Option<PreviewBackground>> Backgrounds { get; } =
    [
        new(PreviewBackground.DarkGray, "深灰 #161616（預設）"),
        new(PreviewBackground.MidGray, "中灰 #808080（判斷亮度用）"),
        new(PreviewBackground.Black, "純黑 #000000"),
    ];

    public IReadOnlyList<int> ProxySizes { get; } = [1200, 1600, 2048, 2560];

    public IReadOnlyList<Option<string>> Backends { get; } =
    [
        new("Auto", "自動（Windows：DX12）"), new("Dx12", "DX12"), new("Vulkan", "Vulkan"),
    ];

    public IReadOnlyList<Option<string>> Languages { get; } =
        Loc.Languages.Select(l => new Option<string>(l.Code, l.Name)).ToList();

    public ObservableCollection<string> Adapters { get; } = [];

    [ObservableProperty]
    private Option<PreviewBackground> _background;

    [ObservableProperty]
    private int _proxySize;

    [ObservableProperty]
    private Option<string> _backend;

    [ObservableProperty]
    private bool _forceSoftware;

    [ObservableProperty]
    private int _gpuMemoryLimitMb;

    [ObservableProperty]
    private string _texturesFolder;

    [ObservableProperty]
    private string _lutsFolder;

    [ObservableProperty]
    private Option<string> _language;

    public string CurrentAdapter => _main.GpuName;

    public PreferencesViewModel(MainWindowViewModel main)
    {
        _main = main;
        var s = main.Settings;
        _background = Backgrounds.First(b => b.Value == s.PreviewBackground);
        _proxySize = s.PreviewLongEdge;
        _backend = Backends.FirstOrDefault(b => b.Value == s.GpuBackend) ?? Backends[0];
        _forceSoftware = s.ForceSoftwareGpu;
        _gpuMemoryLimitMb = s.GpuMemoryLimitMb;
        _texturesFolder = s.TexturesFolder ?? Core.AppPaths.Textures;
        _lutsFolder = s.LutsFolder ?? Core.AppPaths.Luts;
        _language = Languages.FirstOrDefault(l => l.Value == s.Language) ?? Languages[0];
        _ = LoadAdaptersAsync();
    }

    private async Task LoadAdaptersAsync()
    {
        try
        {
            var list = await Task.Run(() => GpuDevice.EnumerateAdapters());
            foreach (var a in list) Adapters.Add($"{a.DisplayName}（{a.Kind}）");
        }
        catch (Exception ex)
        {
            Adapters.Add($"無法列舉：{ex.Message}");
        }
    }

    partial void OnBackgroundChanged(Option<PreviewBackground> value) => _main.ApplyPreviewBackground(value.Value);

    partial void OnLanguageChanged(Option<string> value)
    {
        _main.Settings.Language = value.Value;
        Loc.Instance.Culture = CultureInfo.GetCultureInfo(value.Value);
    }

    /// <summary>關閉時寫回其餘設定。</summary>
    public void Apply()
    {
        var s = _main.Settings;
        s.PreviewLongEdge = ProxySize;
        s.GpuBackend = Backend.Value;
        s.ForceSoftwareGpu = ForceSoftware;
        s.GpuMemoryLimitMb = GpuMemoryLimitMb;
        s.TexturesFolder = TexturesFolder;
        s.LutsFolder = LutsFolder;
    }
}
