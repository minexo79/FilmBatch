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
/// 偏好設定（計畫書 2.8）。所有設定在關閉對話框時寫回並立即生效：
/// 後端、軟體介面卡、材質資料夾與記憶體預算會在 GPU 執行緒上重建算圖器，
/// 代理圖尺寸會重建預覽快取並重新載入目前照片。介面卡清單僅供顯示（唯讀），不提供選擇。
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

    public IReadOnlyList<Option<string>> Backends { get; } = OperatingSystem.IsMacOS()
        ? [new("Auto", "自動（Metal）"), new("Metal", "Metal")]
        : OperatingSystem.IsWindows()
            ? [new("Auto", "自動（DX12）"), new("Dx12", "DX12"), new("Vulkan", "Vulkan")]
            : [new("Auto", "自動（Vulkan）"), new("Vulkan", "Vulkan")];

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
    private Option<string> _language;

    /// <summary>重建失敗時的錯誤訊息；成功時為 null。</summary>
    [ObservableProperty]
    private string? _applyError;

    public bool HasApplyError => ApplyError is not null;

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

    partial void OnApplyErrorChanged(string? value) => OnPropertyChanged(nameof(HasApplyError));

    partial void OnLanguageChanged(Option<string> value)
    {
        _main.Settings.Language = value.Value;
        Loc.Instance.Culture = CultureInfo.GetCultureInfo(value.Value);
    }

    /// <summary>
    /// 關閉時寫回設定並立即生效。傳回 true 表示算圖器重建成功或無需重建；
    /// 重建失敗時傳回 false 並在 <see cref="ApplyError"/> 說明（錯誤同時經 Renderer.Failed 表面化）。
    /// </summary>
    public bool Apply()
    {
        var s = _main.Settings;
        s.PreviewLongEdge = ProxySize;
        s.GpuBackend = Backend.Value;
        s.ForceSoftwareGpu = ForceSoftware;
        s.GpuMemoryLimitMb = GpuMemoryLimitMb;
        s.TexturesFolder = TexturesFolder;
        ApplyError = null;
        try
        {
            _main.ApplyRenderPreferences();
            return true;
        }
        catch (Exception ex)
        {
            ApplyError = ex.Message;
            return false;
        }
    }
}
