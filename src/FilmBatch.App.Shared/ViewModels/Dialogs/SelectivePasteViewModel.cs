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

/// <summary>選擇性貼上（計畫書 2.8）：以模組為單位勾選；記住上次的勾選。</summary>
public sealed partial class SelectivePasteViewModel : ObservableObject
{
    public ObservableCollection<ModuleCheckGroupViewModel> Groups { get; }
    private IEnumerable<ModuleCheckViewModel> All => Groups.SelectMany(g => g.Modules);

    [ObservableProperty]
    private bool _includeSeeds;

    [ObservableProperty]
    private bool _mirrorPositions;

    public SelectivePasteViewModel(AppSettings settings)
    {
        var last = settings.SelectivePasteModules.ToHashSet();
        var firstTime = last.Count == 0;
        Groups = new(ModuleCatalog.Modules
            .GroupBy(m => m.PasteCategory)
            .OrderBy(g => g.Key)
            .Select(g => new ModuleCheckGroupViewModel(Localization.Loc.PasteCategoryLabel(g.Key),
                // 第一次開啟時全選，但裁切 / 細部調整只屬於單張照片，預設不勾選
                g.Select(m => new ModuleCheckViewModel(m,
                    (firstTime && !PhotoOperations.IsPerPhoto(m.PasteCategory)) || last.Contains(m.Key))))));
        IncludeSeeds = settings.SelectivePasteIncludeSeeds;
        MirrorPositions = settings.SelectivePasteMirror;
        foreach (var m in All) m.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CheckedCount));
    }

    public int CheckedCount => All.Count(m => m.IsChecked);

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

    public PasteOptions ToOptions() => new()
    {
        Mode = PasteMode.Selective,
        Modules = All.Where(m => m.IsChecked).Select(m => m.Schema.Key).ToHashSet(),
        IncludeSeeds = IncludeSeeds,
        MirrorPositions = MirrorPositions,
    };

    public void SaveTo(AppSettings settings)
    {
        settings.SelectivePasteModules = All.Where(m => m.IsChecked).Select(m => m.Schema.Key).ToList();
        settings.SelectivePasteIncludeSeeds = IncludeSeeds;
        settings.SelectivePasteMirror = MirrorPositions;
    }
}
