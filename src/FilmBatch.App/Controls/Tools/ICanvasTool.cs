using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace FilmBatch.App.Controls.Tools;

/// <summary>
/// 預覽畫布上的互動工具（裁切、之後的遮罩工具）。<see cref="PreviewCanvas.ActiveTool"/> 非 null 時，
/// 指標事件先交給工具；工具不接手（<see cref="OnPressed"/> 回傳 false）才走畫布原本的平移 / 控制點邏輯。
/// </summary>
public interface ICanvasTool
{
    /// <summary>在影像之上繪製工具（座標為畫布座標，以 <paramref name="map"/> 換算）。</summary>
    void Render(DrawingContext ctx, CanvasMapping map);

    /// <returns>true 表示工具接手這次拖曳（不平移畫布）。</returns>
    bool OnPressed(Point canvas, CanvasMapping map, KeyModifiers mods);

    void OnMoved(Point canvas, CanvasMapping map, KeyModifiers mods);

    void OnReleased(Point canvas, CanvasMapping map);

    /// <summary>沒有拖曳時，游標停在 <paramref name="canvas"/> 應顯示的游標；null 表示交給畫布決定。</summary>
    Cursor? CursorAt(Point canvas, CanvasMapping map) => null;

    /// <summary>畫布顯示影像時的旋轉角度（度，順時針為正）；裁切拉直時讓影像轉、框保持水平。</summary>
    double ViewRotationDeg => 0;

    /// <summary>工具狀態改變需重繪。</summary>
    event Action? Invalidated;
}
