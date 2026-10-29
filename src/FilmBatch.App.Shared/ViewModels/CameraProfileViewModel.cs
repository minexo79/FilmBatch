using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.ViewModels;

/// <summary>A camera profile choice; a missing asset remains visible rather than silently selecting auto.</summary>
public sealed class CameraProfileOption(string key, string name, bool valid)
{
    public string Key { get; } = key;
    public string Label => Key == "auto" ? Loc.Get("Choice.raw.camera_profile.auto") : name;
    public bool Valid { get; } = valid;
}

/// <summary>Imports and selects camera profiles for the existing RAW module.</summary>
public sealed partial class CameraProfileViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private CancellationTokenSource? _importCancellation;
    private CameraProfileOption? _selected;
    private bool _loading;
    private int _detailGeneration;
    private int _listGeneration;
    private string? _detailFocus;
    private bool _disposed;

    public CameraProfileViewModel(MainWindowViewModel main)
    {
        _main = main;
        Options.Add(new CameraProfileOption("auto", "", true));
        _selected = Options[0];
        _ = ReloadSafeAsync();
    }

    public ObservableCollection<CameraProfileOption> Options { get; } = [];

    public CameraProfileOption? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value)) return;
            if (!_loading) _main.Inspector.SetValue("raw.camera_profile", ParamValue.Of(value.Key), null);
            _ = RefreshDetailAsync(value);
        }
    }

    [ObservableProperty] private string _detail = "";

    public static string Describe(DcpProfile profile) => Loc.Format("Raw.Profile.Description",
        profile.UniqueCameraModel, Loc.Get("Raw.Profile.Policy" + profile.ProfileEmbedPolicy),
        string.IsNullOrEmpty(profile.ProfileCopyright) ? Loc.Get("Raw.Profile.NoCopyright") : profile.ProfileCopyright);

    internal void OnSettingsChanged()
    {
        var focus = _main.FocusFile;
        var key = _main.Service is { } service && focus is { } file
            ? service.SettingsFor(file).Text("raw.camera_profile") : "auto";
        var focusChanged = _detailFocus != focus;
        _detailFocus = focus;
        SelectWithoutWriting(key, refresh: focusChanged);
    }

    internal void RefreshLabels()
    {
        SelectWithoutWriting(_selected?.Key ?? "auto", refresh: true);
    }

    internal void CancelImport()
    {
        var cancellation = _importCancellation;
        _importCancellation = null;
        cancellation?.Cancel();
    }

    internal void Dispose()
    {
        _disposed = true;
        CancelImport();
        ++_listGeneration;
        ++_detailGeneration;
    }

    [RelayCommand]
    private async Task Import()
    {
        CancelImport();
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        var token = cancellation.Token;
        var root = Path.GetFullPath(AppPaths.UserCameraProfiles);
        var service = _main.Service;
        var selection = _main.Selection.ToArray();
        var focus = _main.FocusFile;
        if (service is null || focus is null || selection.Length == 0 || !selection.Contains(focus))
        {
            _importCancellation = null;
            return;
        }
        bool Current() => !_disposed && !token.IsCancellationRequested && ReferenceEquals(_importCancellation, cancellation)
            && ReferenceEquals(service, _main.Service) && focus == _main.FocusFile
            && root == Path.GetFullPath(AppPaths.UserCameraProfiles) && selection.SequenceEqual(_main.Selection);
        try
        {
            var path = await _main.Dialogs.PickFileAsync(Loc.Get("Raw.Profile.ImportTitle"), ["*.dcp"]);
            if (path is null || !Current()) return;
            using var draft = await Task.Run(() =>
            {
                using var source = File.OpenRead(path);
                return CameraProfileStore.Stage(root, source, Path.GetFileNameWithoutExtension(path), token);
            }, token);
            if (!Current() || !await _main.Dialogs.ShowCameraProfileImportAsync(draft.Profile, token) || !Current()) return;
            var key = await Task.Run(() => draft.Publish(token), token);
            if (!Current()) return;
            await ReloadAsync(token);
            if (!Current()) return;
            _main.Inspector.SetValue("raw.camera_profile", ParamValue.Of(key), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is RawException or CameraProfileException or IOException or UnauthorizedAccessException)
        {
            if (Current()) await _main.Dialogs.ConfirmAsync(Loc.Get("Raw.Profile.ImportTitle"), ex.Message, Loc.Get("Dialog.OK"));
        }
        finally
        {
            if (ReferenceEquals(_importCancellation, cancellation)) _importCancellation = null;
        }
    }

    private async Task ReloadSafeAsync()
    {
        try { await ReloadAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!_disposed) Detail = ex.Message; }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var generation = ++_listGeneration;
        var root = Path.GetFullPath(AppPaths.UserCameraProfiles);
        var items = await Task.Run(() => CameraProfileStore.List(root), cancellationToken);
        if (_disposed || cancellationToken.IsCancellationRequested || generation != _listGeneration
            || root != Path.GetFullPath(AppPaths.UserCameraProfiles)) return;
        var key = _selected?.Key ?? "auto";
        _loading = true;
        try
        {
            Options.Clear();
            Options.Add(new CameraProfileOption("auto", "", true));
            foreach (var item in items) Options.Add(new CameraProfileOption(item.Key, item.Name, item.Valid));
            SelectWithoutWriting(key, refresh: true);
        }
        finally { _loading = false; }
    }

    private void SelectWithoutWriting(string key, bool refresh = false)
    {
        var option = Options.FirstOrDefault(item => item.Key == key);
        if (option is null)
        {
            option = new CameraProfileOption(key, key, false);
            Options.Add(option);
        }
        var previous = _loading;
        _loading = true;
        try
        {
            if (refresh && ReferenceEquals(_selected, option))
            {
                OnPropertyChanged(nameof(Selected));
                _ = RefreshDetailAsync(option);
            }
            else Selected = option;
        }
        finally { _loading = previous; }
    }

    private async Task RefreshDetailAsync(CameraProfileOption option)
    {
        var generation = ++_detailGeneration;
        if (option.Key == "auto") { Detail = ""; return; }
        if (!option.Valid) { Detail = Loc.Get("Raw.Profile.Invalid"); return; }
        var root = Path.GetFullPath(AppPaths.UserCameraProfiles);
        string detail;
        try
        {
            var profile = await Task.Run(() => CameraProfileStore.Resolve(root, option.Key));
            detail = Describe(profile);
        }
        catch (Exception ex) when (ex is RawException or CameraProfileException or IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
        }
        if (!_disposed && generation == _detailGeneration && root == Path.GetFullPath(AppPaths.UserCameraProfiles)) Detail = detail;
    }
}
