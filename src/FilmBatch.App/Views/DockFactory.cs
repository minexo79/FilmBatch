using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using FilmBatch.App.Localization;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Views;

/// <summary>
/// 停靠版面（計畫書 2.3）：左欄為預覽＋下方縮圖列，右側邊欄（預設庫｜照片基本參數｜照片匯出，以分頁工具列切換）
/// 佔滿整個高度、與縮圖列並排。
/// 面板只用 Dock 內建的 Tool / Document 型別（以 Id 識別），ViewModel 經 <see cref="Factory.ContextLocator"/> 指派，
/// 因此 layout.json 只包含版面本身，可安全地序列化與還原。
/// </summary>
public sealed class DockFactory(MainWindowViewModel main) : Factory
{
    public const string PreviewId = "preview";
    public const string SidebarId = "sidebar";
    public const string FilmstripId = "filmstrip";

    /// <summary>目前版面應有的面板；舊版 layout.json（左側預設庫＋右側模組）缺少側邊欄，載入時改用預設版面。</summary>
    private static readonly string[] ExpectedIds = [PreviewId, SidebarId, FilmstripId];

    public override IRootDock CreateLayout()
    {
        var sidebar = new Tool { Id = SidebarId, Title = Loc.Get("Panel.Inspector"), CanClose = false };
        var filmstrip = new Tool { Id = FilmstripId, Title = Loc.Get("Panel.Filmstrip"), CanClose = false };
        var preview = new Document { Id = PreviewId, Title = Loc.Get("Panel.Preview"), CanClose = false, CanFloat = false };

        var center = new DocumentDock
        {
            Id = "CenterDock", IsCollapsable = false, CanCreateDocument = false,
            ActiveDockable = preview, VisibleDockables = CreateList<IDockable>(preview),
        };
        var right = new ToolDock
        {
            Id = "RightDock", Proportion = 0.27, Alignment = Alignment.Right,
            ActiveDockable = sidebar, VisibleDockables = CreateList<IDockable>(sidebar),
        };
        var bottom = new ToolDock
        {
            Id = "BottomDock", Proportion = 0.2, Alignment = Alignment.Bottom,
            ActiveDockable = filmstrip, VisibleDockables = CreateList<IDockable>(filmstrip),
        };
        var left = new ProportionalDock
        {
            Id = "LeftColumn", Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(center, new ProportionalDockSplitter(), bottom),
        };
        var layout = new ProportionalDock
        {
            Id = "MainLayout", Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), right),
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
            [PreviewId] = () => main.Preview,
            [SidebarId] = () => main.Sidebar,
            [FilmstripId] = () => main.Filmstrip,
        };
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };
        DockableLocator = new Dictionary<string, Func<IDockable?>>();
        base.InitLayout(layout);

        // 不顯示 IDE 式的工具標題列（含滑鼠移入時自動浮現）；面板看起來像固定欄位。
        // 於 InitLayout 設定，舊的 layout.json 還原後也一併套用。
        foreach (var dock in Enumerate(layout).OfType<IToolDock>())
            dock.GripMode = GripMode.Hidden;
    }

    /// <summary>切換語言後更新面板標題（標題會存進 layout.json，載入後以目前語言覆寫）。</summary>
    public void UpdateTitles(IDockable root)
    {
        foreach (var d in Enumerate(root))
        {
            d.Title = d.Id switch
            {
                PreviewId => Loc.Get("Panel.Preview"),
                SidebarId => Loc.Get("Panel.Inspector"),
                FilmstripId => Loc.Get("Panel.Filmstrip"),
                _ => d.Title,
            };
        }
    }

    /// <summary>
    /// 版面是否恰好包含目前版本的面板（不多不少），且側邊欄位於最外層的水平分割（佔滿整個高度）；
    /// 舊版的側邊欄與預覽同列、縮圖列橫跨整個寬度，載入時改用預設版面。
    /// </summary>
    public static bool IsCurrentLayout(IDockable root)
    {
        var ids = Enumerate(root).Where(d => d is ITool or IDocument).Select(d => d.Id).ToHashSet();
        if (!ids.SetEquals(ExpectedIds)) return false;
        return root is IRootDock { VisibleDockables: [IProportionalDock { Orientation: Orientation.Horizontal } main] }
            && main.VisibleDockables is { } columns
            && columns.Any(c => c is IToolDock { VisibleDockables: { } tools } && tools.Any(t => t.Id == SidebarId));
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
