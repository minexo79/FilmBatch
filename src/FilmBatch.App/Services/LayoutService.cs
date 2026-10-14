using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer.SystemTextJson;
using FilmBatch.App.Views;
using FilmBatch.Core;

namespace FilmBatch.App.Services;

/// <summary>Dock 版面的存取（layout.json）。Dock 版面只透過本服務存取，降低 Dock.Avalonia 升級時的影響（計畫書 15）。</summary>
public sealed class LayoutService(DockFactory factory, string? path = null)
{
    private readonly DockSerializer _serializer = new();
    private readonly string _path = path ?? AppPaths.LayoutFile;

    /// <summary>載入上次的版面；沒有或損毀時回傳預設版面。</summary>
    public IRootDock LoadOrDefault()
    {
        try
        {
            if (File.Exists(_path) && _serializer.Deserialize<IRootDock>(File.ReadAllText(_path)) is { } saved)
            {
                factory.InitLayout(saved);
                factory.UpdateTitles(saved);
                return saved;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"無法還原版面，改用預設版面：{ex.Message}");
        }
        return CreateDefault();
    }

    public IRootDock CreateDefault()
    {
        var layout = factory.CreateLayout();
        factory.InitLayout(layout);
        return layout;
    }

    public void Save(IDock? layout)
    {
        if (layout is null) return;
        try
        {
            AppPaths.WriteAllTextAtomic(_path, _serializer.Serialize(layout));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"無法儲存版面：{ex.Message}");
        }
    }
}
