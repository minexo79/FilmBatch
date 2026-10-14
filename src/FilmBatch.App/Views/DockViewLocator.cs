using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

/// <summary>依 Dock 面板的 Context（ViewModel）建立對應的 View；明確列舉，不以反射掃描（Trimming 友善）。</summary>
public sealed class DockViewLocator : IDataTemplate
{
    public bool Match(object? data) => data is IDockable { Context: PresetLibraryViewModel or PreviewViewModel or InspectorViewModel or FilmstripViewModel };

    public Control? Build(object? param) => (param as IDockable)?.Context switch
    {
        PresetLibraryViewModel vm => new PresetLibraryView { DataContext = vm },
        PreviewViewModel vm => new PreviewView { DataContext = vm },
        InspectorViewModel vm => new InspectorView { DataContext = vm },
        FilmstripViewModel vm => new FilmstripView { DataContext = vm },
        _ => null,
    };
}
