using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.App.Services;
using FilmBatch.Engine.Imaging.Raw;
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
/// 偏好設定（計畫書 2.8）。背景載入 CPU 目標即時生效；其餘運算設定於關閉時套用。
/// 後端、軟體介面卡、材質資料夾與記憶體預算變更會在 GPU 執行緒上重建算圖器，
/// 代理圖尺寸變更會重新載入目前照片。介面卡清單僅供顯示（唯讀），不提供選擇。
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

    /// <summary>預覽上限選項；值 0 為「自動」（以 Loupe 的 Preview 等級解碼），其餘為長邊像素。</summary>
    public IReadOnlyList<Option<int>> ProxySizes { get; } =
    [
        Option<int>.Localized(0, "Pref.ProxySize.Auto"),
        new(1200, "1200"), new(1600, "1600"), new(2048, "2048"), new(2560, "2560"),
    ];

    /// <summary>
    /// 下拉選單目前選取的預覽上限項目（與 <see cref="ProxySize"/> 同步）；
    /// 設定檔中不在清單內的值顯示為未選取，但在未變更前保留原值。
    /// </summary>
    public Option<int>? ProxySizeOption
    {
        get => ProxySizes.FirstOrDefault(o => o.Value == ProxySize);
        set
        {
            if (value is not null) ProxySize = value.Value;
        }
    }

    public IReadOnlyList<Option<string>> Backends { get; } = OperatingSystem.IsIOS()
        ? [new("Auto", "CPU")]
        : OperatingSystem.IsMacOS()
            ? [Option<string>.Localized("Auto", "Pref.Backend.Auto", "Metal"), new("Metal", "Metal")]
            : OperatingSystem.IsWindows()
                ? [Option<string>.Localized("Auto", "Pref.Backend.Auto", "DX12"), new("Dx12", "DX12"), new("Vulkan", "Vulkan")]
                : [Option<string>.Localized("Auto", "Pref.Backend.Auto", "Vulkan"), new("Vulkan", "Vulkan")];

    /// <summary>Whether native GPU enumeration/backends exist on this platform; iOS honestly exposes CPU only.</summary>
    public static bool HasNativeGpu => !OperatingSystem.IsIOS();

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
    private int _backgroundCpuPercent;

    [ObservableProperty]
    private string _texturesFolder;

    /// <summary>外部 Lensfun 資料夾；空字串表示跟隨 FILMBATCH_LENSFUN_DIR，再其次為下載的資料庫（皆無則不使用）。</summary>
    [ObservableProperty]
    private string _lensDatabaseFolder;

    [ObservableProperty]
    private Option<string> _language;

    /// <summary>除錯模式：記錄檔額外寫入 DEBUG 訊息；切換即時生效，關閉對話框時存檔。</summary>
    [ObservableProperty]
    private bool _debugLogging;

    /// <summary>記錄檔所在資料夾（顯示用）。</summary>
    public string LogFolder => Core.AppPaths.Logs;

    /// <summary>是否可在系統檔案管理員中開啟記錄資料夾（iOS 等平台隱藏）。</summary>
    public bool CanOpenLogFolder => FileReveal.IsSupported;

    /// <summary>實際開啟資料夾的動作；測試替換以免真的啟動外部程式。</summary>
    internal Action<string> OpenFolder { get; set; } = FileReveal.OpenFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadLensDatabaseCommand), nameof(CancelLensDownloadCommand))]
    private bool _isDownloadingLensDatabase;

    /// <summary>Lensfun 下載進度（0–100）。</summary>
    [ObservableProperty]
    private double _lensDownloadProgress;

    /// <summary>最近一次 Lensfun 下載的錯誤；null = 無。</summary>
    [ObservableProperty]
    private string? _lensDownloadError;

    private Func<string> _lensStatus = static () => "";
    /// <summary>已下載的 Lensfun 版本或下載狀態；以產生函式保存，切換語言時重算。</summary>
    public string LensDownloadStatus => _lensStatus();
    private void SetLensStatus(Func<string> status)
    {
        _lensStatus = status;
        OnPropertyChanged(nameof(LensDownloadStatus));
    }

    /// <summary>下載前就顯示的來源與授權標示。</summary>
    public string LensDownloadAttribution => Loc.Format("Pref.LensDownload.Attribution",
        LensDatabaseStore.Attribution, LensDatabaseStore.License);

    private readonly LensDatabaseStore _lensStore = new();
    private CancellationTokenSource? _lensCts;

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
        _backgroundCpuPercent = s.BackgroundCpuPercent;
        _texturesFolder = s.TexturesFolder ?? Core.AppPaths.Textures;
        _lensDatabaseFolder = s.LensDatabaseFolder ?? _lensStore.InstalledDirectory ?? "";
        _language = Languages.First(l => l.Value == s.EffectiveLanguage);
        _debugLogging = s.DebugLogging;
        ShowInstalledLensDatabase();
        Loc.OnCultureChanged(this, static vm =>
        {
            vm.OnPropertyChanged(nameof(LensDownloadStatus));
            vm.OnPropertyChanged(nameof(LensDownloadAttribution));        });
        _ = LoadAdaptersAsync();
    }

    private async Task LoadAdaptersAsync()
    {
        if (OperatingSystem.IsIOS())
        {
            Adapters.Add(Loc.Get("Pref.AdaptersCpuOnly"));
            return;
        }
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

    [RelayCommand]
    private async Task BrowseLensDatabase()
    {
        var picked = await _main.Dialogs.PickFolderAsync(Directory.Exists(LensDatabaseFolder) ? LensDatabaseFolder : null);
        if (picked is not null) LensDatabaseFolder = picked;
    }

    [RelayCommand]
    private void ClearLensDatabase() => LensDatabaseFolder = "";

    [RelayCommand(CanExecute = nameof(CanOpenLogFolder))]
    private void OpenLogFolder()
    {
        try
        {
            OpenFolder(LogFolder);
        }
        catch (Exception ex)
        {
            AppLog.Error($"無法開啟記錄資料夾：{LogFolder}", ex);
            _main.Toast.Show(Loc.Format("Toast.RevealFailed", ex.Message));
        }
    }

    partial void OnDebugLoggingChanged(bool value)
    {
        _main.Settings.DebugLogging = value;
        AppLog.DebugEnabled = value;
        AppLog.Info(value ? "除錯模式：開啟" : "除錯模式：關閉");
    }

    private void ShowInstalledLensDatabase()
    {
        var info = _lensStore.Installed;
        SetLensStatus(info is null
            ? static () => Loc.Get("Pref.LensDownload.Status.Missing")
            : () => Loc.Format("Pref.LensDownload.Status.Installed", info.ShortCommit, info.CommitDate.ToLocalTime(), info.FileCount));
    }

    private bool CanDownloadLensDatabase => !IsDownloadingLensDatabase;

    /// <summary>
    /// 明確下載最新的 Lensfun data/db（逐檔驗證後才切換版本）。外部資料夾未設定時下載的版本立即生效：
    /// 版本資料夾路徑改變，RAW 來源以新選項重新解碼。關閉對話框不中斷下載，結果以 toast 告知。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadLensDatabase))]
    private async Task DownloadLensDatabase()
    {
        if (IsDownloadingLensDatabase) return;
        using var cancellation = new CancellationTokenSource();
        _lensCts = cancellation;
        IsDownloadingLensDatabase = true;
        LensDownloadProgress = 0;
        LensDownloadError = null;
        var before = _lensStore.Installed?.Commit;
        var beforeDirectory = _lensStore.InstalledDirectory;
        SetLensStatus(static () => Loc.Format("Pref.LensDownload.Status.Downloading", 0.0));
        var progress = new Progress<double>(p =>
        {
            if (!IsDownloadingLensDatabase) return;
            LensDownloadProgress = p * 100;
            SetLensStatus(() => Loc.Format("Pref.LensDownload.Status.Downloading", p));
        });
        try
        {
            var info = await _lensStore.InstallAsync(progress, cancellation.Token);
            LensDownloadProgress = 100;
            if (info.Commit == before)
            {
                SetLensStatus(() => Loc.Format("Pref.LensDownload.Status.Latest", info.ShortCommit, info.CommitDate.ToLocalTime(), info.FileCount));
                return;
            }
            SetLensStatus(() => Loc.Format("Pref.LensDownload.Status.Installed", info.ShortCommit, info.CommitDate.ToLocalTime(), info.FileCount));
            // 輸入框空白或仍指向舊版本時，改顯示新下載的資料夾（儲存時仍視為「跟隨下載的資料庫」）。
            if (string.IsNullOrWhiteSpace(LensDatabaseFolder) || LensDatabaseFolder == beforeDirectory)
                LensDatabaseFolder = _lensStore.InstalledDirectory ?? "";
            _main.Toast.Show(Loc.Format("Pref.LensDownload.Toast", info.ShortCommit));
            if (string.IsNullOrWhiteSpace(_main.Settings.LensDatabaseFolder)) _main.ApplyLensDatabase();
        }
        catch (OperationCanceledException)
        {
            SetLensStatus(static () => Loc.Get("Pref.LensDownload.Status.Cancelled"));
        }
        catch (Exception ex)
        {
            LensDownloadError = ex.Message;
            SetLensStatus(static () => Loc.Get("Pref.LensDownload.Status.Failed"));
            _main.Toast.Show(Loc.Format("Pref.LensDownload.FailedToast", ex.Message));
        }
        finally
        {
            _lensCts = null;
            IsDownloadingLensDatabase = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDownloadingLensDatabase))]
    private void CancelLensDownload()
    {
        try { _lensCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    partial void OnBackgroundChanged(Option<PreviewBackground> value) => _main.ApplyPreviewBackground(value.Value);

    partial void OnBackgroundCpuPercentChanged(int value) => _main.BackgroundCpuPercent = value;

    partial void OnProxySizeChanged(int value) => OnPropertyChanged(nameof(ProxySizeOption));

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
        var backend = Backend.Value;
        var forceSoftware = ForceSoftware;
        if (!HasNativeGpu)
        {
            // iOS has no native GPU path: keep the explicit CPU selection instead of persisting backend edits.
            forceSoftware = true;
            if (backend != "Auto") backend = s.GpuBackend;
        }
        var renderChanged = s.PreviewLongEdge != ProxySize || s.GpuBackend != backend
            || s.ForceSoftwareGpu != forceSoftware || s.GpuMemoryLimitMb != GpuMemoryLimitMb
            || (s.TexturesFolder ?? Core.AppPaths.Textures) != TexturesFolder
            || _main.Renderer.InitError is not null;
        s.PreviewLongEdge = ProxySize;
        s.GpuBackend = backend;
        s.ForceSoftwareGpu = forceSoftware;
        s.GpuMemoryLimitMb = GpuMemoryLimitMb;
        s.TexturesFolder = TexturesFolder;
        // 顯示的是下載的資料夾時不另存，讓之後下載的新版本自動生效。
        var lensDatabase = string.IsNullOrWhiteSpace(LensDatabaseFolder) || LensDatabaseFolder.Trim() == _lensStore.InstalledDirectory
            ? null : LensDatabaseFolder.Trim();
        var lensChanged = s.LensDatabaseFolder != lensDatabase;
        s.LensDatabaseFolder = lensDatabase;
        ApplyError = null;
        try
        {
            // 算圖器重建本身就會重新載入來源與縮圖，鏡頭資料庫一併生效。
            if (renderChanged) _main.ApplyRenderPreferences();
            else if (lensChanged) _main.ApplyLensDatabase();
            else s.Save();
            return true;
        }
        catch (Exception ex)
        {
            ApplyError = ex.Message;
            return false;
        }
    }
}
