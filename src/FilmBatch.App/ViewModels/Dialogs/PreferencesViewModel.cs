using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Gpu.Device;

namespace FilmBatch.App.ViewModels.Dialogs;

/// <summary>下拉選單的一個選項。以資源 key 建立時，切換語言會即時更新 <see cref="Label"/>。</summary>
public abstract class OptionBase : ObservableObject
{
    public abstract string Label { get; }

    public override string ToString() => Label;
}

public sealed class Option<T> : OptionBase
{
    private readonly Func<string> _label;

    /// <summary>固定文字（名稱、型號等不需翻譯的內容）。</summary>
    public Option(T value, string label) : this(value, () => label, live: false) { }

    private Option(T value, Func<string> label, bool live)
    {
        Value = value;
        _label = label;
        if (live) Loc.OnCultureChanged(this, static o => o.OnPropertyChanged(nameof(Label)));
    }

    /// <summary>以 <see cref="Loc"/> 的 key 取得文字，可帶格式參數。</summary>
    public static Option<T> Localized(T value, string key, params object[] args) =>
        new(value, () => args.Length == 0 ? Loc.Get(key) : Loc.Format(key, args), live: true);

    public T Value { get; }

    public override string Label => _label();
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
        Option<PreviewBackground>.Localized(PreviewBackground.DarkGray, "Pref.Background.DarkGray"),
        Option<PreviewBackground>.Localized(PreviewBackground.MidGray, "Pref.Background.MidGray"),
        Option<PreviewBackground>.Localized(PreviewBackground.Black, "Pref.Background.Black"),
    ];

    public IReadOnlyList<int> ProxySizes { get; } = [1200, 1600, 2048, 2560];

    public IReadOnlyList<Option<string>> Backends { get; } = OperatingSystem.IsMacOS()
        ? [Option<string>.Localized("Auto", "Pref.Backend.Auto", "Metal"), new("Metal", "Metal")]
        : OperatingSystem.IsWindows()
            ? [Option<string>.Localized("Auto", "Pref.Backend.Auto", "DX12"), new("Dx12", "DX12"), new("Vulkan", "Vulkan")]
            : [Option<string>.Localized("Auto", "Pref.Backend.Auto", "Vulkan"), new("Vulkan", "Vulkan")];

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
        _language = Languages.First(l => l.Value == s.EffectiveLanguage);
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
            Adapters.Add(Loc.Format("Pref.AdaptersFailed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task BrowseTextures()
    {
        var picked = await _main.Dialogs.PickFolderAsync(Directory.Exists(TexturesFolder) ? TexturesFolder : null);
        if (picked is not null) TexturesFolder = picked;
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
