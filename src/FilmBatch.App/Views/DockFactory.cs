using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using FilmBatch.App.Localization;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

/// <summary>
/// 停靠版面（計畫書 2.3）：左預設庫、中央預覽、右照片資訊與模組、下縮圖列。
/// 面板只用 Dock 內建的 Tool / Document 型別（以 Id 識別），ViewModel 經 <see cref="Factory.ContextLocator"/> 指派，
/// 因此 layout.json 只包含版面本身，可安全地序列化與還原。
/// </summary>
public sealed class DockFactory(MainWindowViewModel main) : Factory
{
    public const string PresetsId = "presets";
    public const string PreviewId = "preview";
    public const string InspectorId = "inspector";
    public const string FilmstripId = "filmstrip";

    public override IRootDock CreateLayout()
    {
        var presets = new Tool { Id = PresetsId, Title = Loc.Get("Panel.Presets"), CanClose = false };
        var inspector = new Tool { Id = InspectorId, Title = Loc.Get("Panel.Inspector"), CanClose = false };
        var filmstrip = new Tool { Id = FilmstripId, Title = Loc.Get("Panel.Filmstrip"), CanClose = false };
        var preview = new Document { Id = PreviewId, Title = Loc.Get("Panel.Preview"), CanClose = false, CanFloat = false };

        var left = new ToolDock
        {
            Id = "LeftDock", Proportion = 0.16, Alignment = Alignment.Left,
            ActiveDockable = presets, VisibleDockables = CreateList<IDockable>(presets),
        };
        var center = new DocumentDock
        {
            Id = "CenterDock", IsCollapsable = false, CanCreateDocument = false,
            ActiveDockable = preview, VisibleDockables = CreateList<IDockable>(preview),
        };
        var right = new ToolDock
        {
            Id = "RightDock", Proportion = 0.25, Alignment = Alignment.Right,
            ActiveDockable = inspector, VisibleDockables = CreateList<IDockable>(inspector),
        };
        var top = new ProportionalDock
        {
            Id = "TopRow", Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), center, new ProportionalDockSplitter(), right),
        };
        var bottom = new ToolDock
        {
            Id = "BottomDock", Proportion = 0.2, Alignment = Alignment.Bottom,
            ActiveDockable = filmstrip, VisibleDockables = CreateList<IDockable>(filmstrip),
        };
        var layout = new ProportionalDock
        {
            Id = "MainLayout", Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(top, new ProportionalDockSplitter(), bottom),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(layout);
        root.ActiveDockable = layout;
        root.DefaultDockable = layout;
        return root;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            [PresetsId] = () => main.Presets,
            [PreviewId] = () => main.Preview,
            [InspectorId] = () => main.Inspector,
            [FilmstripId] = () => main.Filmstrip,
        };
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };
        DockableLocator = new Dictionary<string, Func<IDockable?>>();
        base.InitLayout(layout);
    }

    /// <summary>切換語言後更新面板標題（標題會存進 layout.json，載入後以目前語言覆寫）。</summary>
    public void UpdateTitles(IDockable root)
    {
        foreach (var d in Enumerate(root))
        {
            d.Title = d.Id switch
            {
                PresetsId => Loc.Get("Panel.Presets"),
                PreviewId => Loc.Get("Panel.Preview"),
                InspectorId => Loc.Get("Panel.Inspector"),
                FilmstripId => Loc.Get("Panel.Filmstrip"),
                _ => d.Title,
            };
        }
    }

    private static IEnumerable<IDockable> Enumerate(IDockable d)
    {
        yield return d;
        if (d is IDock { VisibleDockables: { } children })
            foreach (var c in children.SelectMany(Enumerate)) yield return c;
        if (d is IRootDock root)
        {
            foreach (var list in new[] { root.LeftPinnedDockables, root.RightPinnedDockables, root.TopPinnedDockables, root.BottomPinnedDockables })
                if (list is not null)
                    foreach (var c in list.SelectMany(Enumerate)) yield return c;
            if (root.Windows is { } windows)
                foreach (var w in windows)
                    if (w.Layout is not null)
                        foreach (var c in Enumerate(w.Layout)) yield return c;
        }
    }
}
