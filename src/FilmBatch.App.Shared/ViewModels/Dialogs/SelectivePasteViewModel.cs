using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Services;
using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;

namespace FilmBatch.App.ViewModels.Dialogs;

public sealed partial class ModuleCheckViewModel(ModuleSchema schema, bool isChecked) : ObservableObject
{
    public ModuleSchema Schema { get; } = schema;
    public string Label => Localization.Loc.ModuleLabel(Schema);

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

public sealed class ModuleCheckGroupViewModel(string label, IEnumerable<ModuleCheckViewModel> modules)
{
    public string Label { get; } = label;
    public ObservableCollection<ModuleCheckViewModel> Modules { get; } = new(modules);
}

/// <summary>依模組分組的勾選清單（選擇性貼上、建立預設共用）；第一次開啟時全選，但單張照片專屬的模組（裁切）不勾。</summary>
public abstract partial class ModuleChecklistViewModel : ObservableObject
{
    public ObservableCollection<ModuleCheckGroupViewModel> Groups { get; }
    protected IEnumerable<ModuleCheckViewModel> All => Groups.SelectMany(g => g.Modules);

    protected ModuleChecklistViewModel(IReadOnlyCollection<string> last)
    {
        var firstTime = last.Count == 0;
        Groups = new(ModuleCatalog.Modules
            .GroupBy(m => m.PasteCategory)
            .OrderBy(g => g.Key)
            .Select(g => new ModuleCheckGroupViewModel(Localization.Loc.PasteCategoryLabel(g.Key),
                g.Select(m => new ModuleCheckViewModel(m,
                    (firstTime && !PhotoOperations.IsPerPhoto(m.PasteCategory)) || last.Contains(m.Key))))));
        foreach (var m in All) m.PropertyChanged += (_, _) => OnChecklistChanged();
    }

    public int CheckedCount => All.Count(m => m.IsChecked);

    protected virtual void OnChecklistChanged() => OnPropertyChanged(nameof(CheckedCount));

    public HashSet<string> CheckedKeys() => All.Where(m => m.IsChecked).Select(m => m.Schema.Key).ToHashSet();

    [RelayCommand]
    private void SelectAll() => Set(_ => true);

    [RelayCommand]
    private void SelectNone() => Set(_ => false);

    [RelayCommand]
    private void ColorOnly() => Set(m => m.Schema.PasteCategory == PasteCategory.Color);

    private void Set(Func<ModuleCheckViewModel, bool> f)
    {
        foreach (var m in All) m.IsChecked = f(m);
    }
}

/// <summary>選擇性貼上（計畫書 2.8）：以模組為單位勾選；記住上次的勾選。</summary>
public sealed partial class SelectivePasteViewModel : ModuleChecklistViewModel
{
    [ObservableProperty]
    private bool _includeSeeds;

    [ObservableProperty]
    private bool _mirrorPositions;

    public SelectivePasteViewModel(AppSettings settings) : base(settings.SelectivePasteModules)
    {
        IncludeSeeds = settings.SelectivePasteIncludeSeeds;
        MirrorPositions = settings.SelectivePasteMirror;
    }

    public PasteOptions ToOptions() => new()
    {
        Mode = PasteMode.Selective,
        Modules = CheckedKeys(),
        IncludeSeeds = IncludeSeeds,
        MirrorPositions = MirrorPositions,
    };

    public void SaveTo(AppSettings settings)
    {
        settings.SelectivePasteModules = [.. CheckedKeys()];
        settings.SelectivePasteIncludeSeeds = IncludeSeeds;
        settings.SelectivePasteMirror = MirrorPositions;
    }
}

/// <summary>以此張建立預設：輸入名稱並勾選要存進預設的模組（像 Lightroom 的「建立預設」）；記住上次的勾選。</summary>
public sealed partial class NewPresetViewModel : ModuleChecklistViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _name;

    public NewPresetViewModel(AppSettings settings, string defaultName) : base(settings.NewPresetModules)
    {
        _name = defaultName;
    }

    public bool CanCreate => !string.IsNullOrWhiteSpace(Name) && CheckedCount > 0;

    protected override void OnChecklistChanged()
    {
        base.OnChecklistChanged();
        OnPropertyChanged(nameof(CanCreate));
    }

    public void SaveTo(AppSettings settings) => settings.NewPresetModules = [.. CheckedKeys()];
}
