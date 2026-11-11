using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Controls;
using FilmBatch.App.Services;
using FilmBatch.App.Localization;
using FilmBatch.Core;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Film;

namespace FilmBatch.App.ViewModels;

/// <summary>底片縮圖格中的一格。</summary>
public sealed partial class FilmTileViewModel(string key, Func<string> name, Func<string>? detail = null, string? caption = null,
    Func<IBrush?>? ramp = null) : ObservableObject
{
    private readonly Lazy<IBrush?> _ramp = new(() => ramp?.Invoke());
    public string Key { get; } = key;
    public string Name => name();
    /// <summary>縮圖下方的短標籤：內建式為序號 1–9，自訂 LUT 為檔名。</summary>
    public string Caption => caption ?? Name;
    /// <summary>縮圖下方的過渡色帶（此底片套在黑到白灰階上的結果）；第一次顯示時才計算，無法取樣時為 null。</summary>
    public IBrush? Ramp => _ramp.Value;
    public bool HasRamp => Ramp is not null;
    /// <summary>清單上的色彩空間標記。空字串表示不另標（內建底片與既有 sRGB 匯入）。</summary>
    public string Detail => detail?.Invoke() ?? "";
    public bool HasDetail => Detail.Length > 0;
    /// <summary>清單項目的無障礙名稱：來源檔名，ProPhoto 再附工作色彩空間。</summary>
    public string AccessibleName => HasDetail ? $"{Name} · {Detail}" : Name;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isLoading;

    public void RefreshLabel()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(AccessibleName));
    }
}

/// <summary>底片分組下拉的一項（切換語言時更新名稱）。</summary>
public sealed partial class FilmGroupViewModel(string key) : ObservableObject
{
    public string Key { get; } = key;
    public string Label => FilmLookViewModel.GroupLabel(Key);
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>匯入 .cube 時可選的色彩空間。顯示文字隨介面語言更新。</summary>
public sealed partial class LutImportSpaceViewModel(FilmLutColorSpace space, string labelKey) : ObservableObject
{
    public FilmLutColorSpace Space { get; } = space;
    public string Label => Loc.Get(labelKey);
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>film 模組頂部：底片分組、.cube 色彩空間與底片縮圖格（docs/film-looks.md）。</summary>
public sealed partial class FilmLookViewModel : ObservableObject
{
    /// <summary>分組下拉最後一項：使用者匯入的 LUT。</summary>
    public const string CustomGroup = "custom";

    private readonly MainWindowViewModel _main;
    private bool _expanded;
    private string? _lastFocus;
    private Settings? _lastResolved;
    private LutImportSpaceViewModel _importColorSpace;
    private CancellationTokenSource? _importCancellation;

    public FilmLookViewModel(MainWindowViewModel main)
    {
        _main = main;
        Thumbnails = new FilmLookThumbnails(main);
        Groups = [.. FilmLookLibrary.GroupsInOrder.Append(CustomGroup).Select(g => new FilmGroupViewModel(g))];
        _selectedGroup = FilmLookLibrary.GroupsInOrder[0];
        _importColorSpace = ImportSpaces[0];
        Imported = [];
        Tiles = [];
        LoadGroup();
        ReloadImported();
    }

    public FilmLookThumbnails Thumbnails { get; }
    public IReadOnlyList<FilmGroupViewModel> Groups { get; }
    /// <summary>使用者匯入的 LUT 與 CLF 轉換。</summary>
    public ObservableCollection<FilmTileViewModel> Imported { get; }
    /// <summary>縮圖格實際顯示的項目：內建分組為該組的 9 式，自訂分組為 <see cref="Imported"/>。</summary>
    public ObservableCollection<FilmTileViewModel> Tiles { get; }

    public bool IsCustomGroup => SelectedGroup == CustomGroup;
    public bool ShowCustomEmpty => IsCustomGroup && Imported.Count == 0;
    public bool CanOpenLutFolder => FileReveal.IsSupported;
    internal Action<string> OpenFolder { get; set; } = FileReveal.OpenFolder;

    /// <summary>.cube 匯入可選的色彩空間。預設第一項是 sRGB。</summary>
    public IReadOnlyList<LutImportSpaceViewModel> ImportSpaces { get; } =
    [
        new(FilmLutColorSpace.Srgb, "Film.LutSpace.Srgb"),
        new(FilmLutColorSpace.ProPhoto, "Film.LutSpace.ProPhoto"),
    ];

    /// <summary>目前套在 .cube 上的色彩空間。匯入後留到使用者改選；空選取忽略。</summary>
    public LutImportSpaceViewModel ImportColorSpace
    {
        get => _importColorSpace;
        set
        {
            if (value is null || ReferenceEquals(value, _importColorSpace)) return;
            _importColorSpace = value;
            OnPropertyChanged();
        }
    }

    /// <summary>目前顯示的分組 key（<see cref="FilmLookLibrary.GroupsInOrder"/>）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGroupItem))]
    [NotifyPropertyChangedFor(nameof(IsCustomGroup))]
    [NotifyPropertyChangedFor(nameof(ShowCustomEmpty))]
    private string _selectedGroup;

    /// <summary>給分組下拉綁定。</summary>
    public FilmGroupViewModel? SelectedGroupItem
    {
        get => Groups.FirstOrDefault(g => g.Key == SelectedGroup);
        set { if (value is not null) SelectedGroup = value.Key; }
    }

    public static string GroupLabel(string group) => Loc.TryGet($"Film.Group.{group}") ?? group;

    /// <summary>切換語言後更新分組名稱。</summary>
    public void RefreshLabels()
    {
        foreach (var g in Groups) g.RefreshLabel();
        foreach (var space in ImportSpaces) space.RefreshLabel();
        foreach (var tile in Tiles.Concat(Imported)) tile.RefreshLabel();
    }

    partial void OnSelectedGroupChanged(string value)
    {
        Thumbnails.Invalidate();
        LoadGroup();
        RefreshSelection();
        RequestThumbnails();
    }

    /// <summary>模組展開／收合：只在展開時算縮圖。</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        if (expanded) RequestThumbnails();
        else Thumbnails.Pause();
    }

    /// <summary>更新底片選取框。</summary>
    public void RefreshSelection()
    {
        var resolved = Resolved();
        var look = resolved?.TryGetValue("film.look", out var v) == true ? v.AsText : FilmLookLibrary.DefaultKey;
        foreach (var tile in Tiles.Concat(Imported)) tile.IsSelected = resolved is not null && tile.Key == look;
    }

    /// <summary>焦點或設定改變後由 Inspector 呼叫：更新選取，焦點或最終參數真的變了才讓縮圖作廢並重算。</summary>
    public void OnSettingsChanged()
    {
        RefreshSelection();
        var focus = _main.FocusFile;
        var resolved = Resolved();
        if (focus == _lastFocus && Equals(resolved, _lastResolved)) return;
        _lastFocus = focus;
        _lastResolved = resolved;
        Thumbnails.Invalidate();
        RequestThumbnails();
    }

    /// <summary>開新資料夾或重建算圖裝置：不論設定是否相同，縮圖都要重算。</summary>
    public void InvalidateThumbnails()
    {
        _lastFocus = null;
        _lastResolved = null;
        Thumbnails.Invalidate();
        RequestThumbnails();
    }

    public void RequestThumbnails()
    {
        if (!_expanded) return;
        Thumbnails.Request(() =>
        {
            var resolved = Resolved();
            if (resolved is null) return [];
            var on = resolved.With("film.enabled", ParamValue.Of(true));
            return [.. Tiles.Select(t => ("look", t, on.With("film.look", ParamValue.Of(t.Key))))];
        });
    }

    internal void CancelImport()
    {
        var cancellation = _importCancellation;
        _importCancellation = null;
        cancellation?.Cancel();
    }

    [RelayCommand]
    private async Task ImportLut()
    {
        CancelImport();
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        var token = cancellation.Token;
        var root = Path.GetFullPath(AppPaths.UserLuts);
        var service = _main.Service;
        var selection = _main.Selection.ToArray();
        var focus = _main.FocusFile;
        bool Current() => !token.IsCancellationRequested && ReferenceEquals(_importCancellation, cancellation)
            && ReferenceEquals(service, _main.Service) && focus == _main.FocusFile
            && root == Path.GetFullPath(AppPaths.UserLuts) && selection.SequenceEqual(_main.Selection);
        try
        {
            var path = await _main.Dialogs.PickFileAsync(Loc.Get("Film.ImportTitle"), ["*.cube", "*.fblut", "*.png", "*.clf", "*.fblook"]);
            if (path is null || !Current()) return;
            var extension = Path.GetExtension(path);
            string key;
            if (extension.Equals(".clf", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".fblook", StringComparison.OrdinalIgnoreCase))
            {
                var isFblook = extension.Equals(".fblook", StringComparison.OrdinalIgnoreCase);
                using var staged = await Task.Run(() =>
                {
                    using var source = File.OpenRead(path);
                    return UserTransformLibrary.Stage(root, source, isFblook, token);
                }, token);
                if (!Current()) return;
                var options = isFblook ? staged.EmbeddedOptions
                    : await _main.Dialogs.ShowColorTransformImportAsync(staged.Transform, token);
                if (options is null || !Current()) return;
                key = await Task.Run(() => staged.Publish(options, Path.GetFileNameWithoutExtension(path), token), token);
                if (!Current()) return;
            }
            else
            {
                var lut = LutFileImport.Open(path, ImportColorSpace.Space);
                key = UserLutLibrary.Import(lut, Path.GetFileNameWithoutExtension(path));
            }
            if (!Current()) return;
            ReloadImported();
            SelectedGroup = CustomGroup;
            ApplyLook(Imported.First(t => t.Key == key));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (Current()) await _main.Dialogs.ConfirmAsync(Loc.Get("Film.ImportTitle"), ex.Message, Loc.Get("Dialog.OK"));
        }
        finally
        {
            if (ReferenceEquals(_importCancellation, cancellation)) _importCancellation = null;
        }
    }

    [RelayCommand]
    private void OpenLutFolder()
    {
        try { OpenFolder(AppPaths.UserLuts); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _main.Toast.Show(Loc.Format("Toast.RevealFailed", ex.Message));
        }
    }

    /// <summary>重新讀取 LUT 資料夾（使用者直接放入 user-*.fblut 後使用）。</summary>
    [RelayCommand]
    private void RefreshCustom()
    {
        UserLutLibrary.Reset();
        ReloadImported();
        Thumbnails.Invalidate();
        RequestThumbnails();
    }

    private void ReloadImported()
    {
        foreach (var old in Imported)
        {
            var bitmap = old.Thumbnail;
            old.Thumbnail = null;
            bitmap?.Dispose();
        }
        Imported.Clear();
        foreach (var (key, name) in UserLutLibrary.All)
            Imported.Add(new FilmTileViewModel(key, () => name, () => ImportDetail(key), ramp: () => RampBrush(key)));
        foreach (var item in UserTransformLibrary.List(AppPaths.UserLuts))
            Imported.Add(new FilmTileViewModel(item.Key, () => item.Name, () => TransformDetail(item.Options), ramp: () => RampBrush(item.Key)));
        if (IsCustomGroup) LoadGroup();
        OnPropertyChanged(nameof(ShowCustomEmpty));
        RefreshSelection();
    }

    /// <summary>
    /// ProPhoto 匯入在清單另標工作色彩空間。儲存的名稱仍是來源檔名，不表示檔案出自特定軟體或相機。
    /// sRGB 不另標，與既有匯入相同。
    /// </summary>
    private static string ImportDetail(string key) =>
        UserLutLibrary.TryGet(key, out var lut) && lut is { ColorSpace: FilmLutColorSpace.ProPhoto }
            ? Loc.Get("Film.LutSpace.ProPhoto")
            : "";

    private static string TransformDetail(ClfImportOptions? options) => options is { } declared
        ? $"CLF · {ColorTransformImportPanel.StageLabel(declared.Stage)} · {ColorTransformImportPanel.EncodingLabel(declared.Input)} → {ColorTransformImportPanel.EncodingLabel(declared.Output)}"
        : Loc.Get("Film.Transform.Invalid");

    [RelayCommand]
    private void ApplyLook(FilmTileViewModel? tile)
    {
        if (tile is null || _main.Service is null || _main.Selection.Count == 0) return;
        var service = _main.Service;
        // 成功後 Project.EntriesChanged → Inspector.Refresh → OnSettingsChanged 會更新選取與縮圖
        service.SetValuesEach(_main.Selection, file => LookValues(service.SettingsFor(file), tile.Key),
            Loc.Format("Film.LookApplied", tile.Name));
    }

    /// <summary>
    /// 切換底片寫入的值。效果比例依新底片給預設（黑白與自訂 LUT 100，其餘 50），
    /// 但照片目前的比例既不是舊底片的預設、也不是參數預設（100）時，視為使用者自己調過，不覆寫。
    /// </summary>
    private static Settings LookValues(Settings resolved, string key)
    {
        var strength = resolved.Number("film.strength");
        var untouched = strength == FilmLookLibrary.DefaultStrength(resolved.Text("film.look"))
            || strength == ModuleCatalog.Param("film.strength")!.Default.AsNumber;
        var values = new List<KeyValuePair<string, ParamValue>>
        {
            new("film.enabled", ParamValue.Of(true)),
            new("film.look", ParamValue.Of(key)),
        };
        if (untouched) values.Add(new("film.strength", ParamValue.Of(FilmLookLibrary.DefaultStrength(key))));
        return new Settings(values);
    }

    /// <summary>此底片的過渡色帶：把底片套在黑到白灰階上，取 9 個樣本做成水平漸層；資產無法載入時不顯示色帶。</summary>
    private static IBrush? RampBrush(string key)
    {
        try
        {
            if (FilmLookLibrary.Resolve(key) is not { } asset) return null;
            var colors = FilmLookRamp.Sample(asset);
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
            };
            var stops = colors.Length / 3;
            for (var i = 0; i < stops; i++)
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(
                    (byte)MathF.Round(colors[i * 3] * 255), (byte)MathF.Round(colors[i * 3 + 1] * 255),
                    (byte)MathF.Round(colors[i * 3 + 2] * 255)), i / (stops - 1.0)));
            return brush.ToImmutable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ColorTransformAssetException)
        {
            return null;
        }
    }

    private void LoadGroup()
    {
        foreach (var tile in Tiles.ToList())
        {
            var bitmap = tile.Thumbnail;
            tile.Thumbnail = null;
            bitmap?.Dispose();
        }
        Tiles.Clear();
        if (IsCustomGroup)
        {
            foreach (var tile in Imported) Tiles.Add(tile);
            return;
        }
        foreach (var look in FilmLookLibrary.All.Where(l => l.Group == SelectedGroup))
        {
            var tile = new FilmTileViewModel(look.Key, () => $"{GroupLabel(look.Group)} {look.Index}",
                caption: look.Index.ToString(), ramp: () => RampBrush(look.Key));
            Tiles.Add(tile);
        }
    }

    private Settings? Resolved() =>
        _main.Service is { } service && _main.FocusFile is { } focus ? service.SettingsFor(focus) : null;
}
