using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

/// <summary>依 Dock 面板的 Context（ViewModel）建立對應的 View；明確列舉，不以反射掃描（Trimming 友善）。</summary>
public sealed class DockViewLocator : IDataTemplate
{
    public bool Match(object? data) => data is IDockable { Context: PreviewViewModel or SidebarViewModel or FilmstripViewModel };

    public Control? Build(object? param) => (param as IDockable)?.Context switch
    {
        PreviewViewModel vm => new PreviewView { DataContext = vm },
        SidebarViewModel vm => new SidebarView { DataContext = vm },
        FilmstripViewModel vm => new FilmstripView { DataContext = vm },
        _ => null,
    };
}
