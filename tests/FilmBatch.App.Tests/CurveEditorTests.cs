using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FilmBatch.App.Controls;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Curves;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Tests;

/// <summary>最小曲線編輯器：新增 / 拖曳 / 刪除點，拖曳中與放開時的事件（計畫 Task 2.1）。</summary>
public class CurveEditorTests
{
    private const double Size = 200;

    private static (Window Window, CurveEditor Editor, List<(IReadOnlyList<CurvePoint> Points, bool IsFinal)> Edits) Create(
        IReadOnlyList<CurvePoint>? points = null)
    {
        var editor = new CurveEditor
        {
            Width = Size, Height = Size,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        if (points is not null) editor.Points = points;
        var edits = new List<(IReadOnlyList<CurvePoint>, bool)>();
        editor.PointsEdited += (_, e) => edits.Add((e.Points, e.IsFinal));
        var window = new Window { Width = 300, Height = 300, Content = editor };
        window.Show();
        return (window, editor, edits);
    }

    /// <summary>曲線座標 → 視窗座標（編輯器位於視窗左上角）。</summary>
    private static Point At(double x, double y)
    {
        var plot = Size - 2 * CurveEditor.Inset;
        return new Point(CurveEditor.Inset + x * plot, CurveEditor.Inset + (1 - y) * plot);
    }

    [AvaloniaFact]
    public void ClickEmptyArea_AddsPoint_AndEndsWithFinalEdit()
    {
        var (window, editor, edits) = Create();
        window.MouseDown(At(0.5, 0.8), MouseButton.Left);
        window.MouseUp(At(0.5, 0.8), MouseButton.Left);

        Assert.NotEmpty(edits);
        Assert.True(edits[^1].IsFinal);
        Assert.All(edits.Take(edits.Count - 1), e => Assert.False(e.IsFinal));
        Assert.Equal(3, edits[^1].Points.Count);
        Assert.Equal(0.5, edits[^1].Points[1].X, 2);
        Assert.Equal(0.8, edits[^1].Points[1].Y, 2);
        Assert.Equal(3, editor.Points.Count);
    }

    [AvaloniaFact]
    public void DragPoint_RaisesIntermediateEdits_ThenOneFinal()
    {
        var (window, _, edits) = Create([new(0, 0), new(0.5, 0.5), new(1, 1)]);
        window.MouseDown(At(0.5, 0.5), MouseButton.Left);
        window.MouseMove(At(0.55, 0.6));
        window.MouseMove(At(0.6, 0.7));
        window.MouseUp(At(0.6, 0.7), MouseButton.Left);

        Assert.Equal(1, edits.Count(e => e.IsFinal));
        Assert.True(edits[^1].IsFinal);
        Assert.True(edits.Count >= 3);
        Assert.Equal(3, edits[^1].Points.Count);
        Assert.Equal(0.6, edits[^1].Points[1].X, 2);
        Assert.Equal(0.7, edits[^1].Points[1].Y, 2);
    }

    [AvaloniaFact]
    public void DragX_IsLimitedToNeighbours()
    {
        var (window, _, edits) = Create([new(0, 0), new(0.3, 0.3), new(0.6, 0.6), new(1, 1)]);
        window.MouseDown(At(0.3, 0.3), MouseButton.Left);
        window.MouseMove(At(0.9, 0.3));
        window.MouseUp(At(0.9, 0.3), MouseButton.Left);

        var pts = edits[^1].Points;
        Assert.Equal(4, pts.Count);
        Assert.True(pts[1].X < 0.6);
    }

    [AvaloniaFact]
    public void PointLimit_ClickDoesNotAddBeyondMax()
    {
        var full = Enumerable.Range(0, ToneCurve.MaxPoints).Select(i => new CurvePoint(i / 15.0, i / 15.0)).ToList();
        var (window, editor, edits) = Create(full);
        window.MouseDown(At(0.5, 0.95), MouseButton.Left);
        window.MouseUp(At(0.5, 0.95), MouseButton.Left);

        Assert.All(edits, e => Assert.True(e.Points.Count <= ToneCurve.MaxPoints));
        Assert.Equal(ToneCurve.MaxPoints, editor.Points.Count);
    }

    [AvaloniaFact]
    public void DragInteriorPointOutside_RemovesIt()
    {
        var (window, _, edits) = Create([new(0, 0), new(0.5, 0.5), new(1, 1)]);
        window.MouseDown(At(0.5, 0.5), MouseButton.Left);
        window.MouseMove(new Point(At(0.5, 0).X, Size + 40));
        window.MouseUp(new Point(At(0.5, 0).X, Size + 40), MouseButton.Left);

        Assert.True(edits[^1].IsFinal);
        Assert.Equal(2, edits[^1].Points.Count);
    }

    [AvaloniaFact]
    public void Endpoints_CannotBeRemoved()
    {
        var (window, _, edits) = Create();
        window.MouseDown(At(0, 0), MouseButton.Left);
        window.MouseMove(new Point(At(0, 0).X, Size + 40));
        window.MouseUp(new Point(At(0, 0).X, Size + 40), MouseButton.Left);

        Assert.True(edits[^1].IsFinal);
        Assert.Equal(2, edits[^1].Points.Count);
        Assert.Equal(0, edits[^1].Points[0].Y, 3);
    }

    [AvaloniaFact]
    public void CurveParamViewModel_PushesNormalizedText_AndEndsEditOnFinal()
    {
        var host = new RecordingHost();
        var vm = new CurveParamViewModel("curves", ParamDef.Curve("master", "主曲線"), host);
        vm.Load("0,0;0.5,0.6;1,1", overridden: false);
        Assert.Equal(3, vm.Points.Count);

        vm.OnEdited([new(0, 0), new(0.5, 0.612345), new(1, 1)], isFinal: false);
        vm.OnEdited([new(0, 0), new(0.5, 0.7), new(1, 1)], isFinal: true);

        Assert.Equal(["0,0;0.5,0.6123;1,1", "0,0;0.5,0.7;1,1"], host.Values.Select(v => v.Value.AsText));
        Assert.All(host.Values, v => Assert.Equal("curves.master", v.Key));
        Assert.NotNull(host.Values[0].MergeKey);
        Assert.Equal(host.Values[0].MergeKey, host.Values[1].MergeKey);
        Assert.Equal(1, host.EndEdits);
    }

    private sealed class RecordingHost : IParamHost
    {
        public List<(string Key, ParamValue Value, string? MergeKey)> Values { get; } = [];
        public int EndEdits { get; private set; }
        public void SetValue(string flatKey, ParamValue value, string? mergeKey) => Values.Add((flatKey, value, mergeKey));
        public void ResetParam(string flatKey) { }
        public void EndEdit() => EndEdits++;
    }
}
