using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.Core.Curves;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>
/// 曲線參數（<see cref="ParamKind.Curve"/>）：值是 <see cref="ToneCurve.Serialize"/> 的文字。
/// 由 <c>CurveEditor</c> 編輯；Inspector 與遮罩面板共用。
/// </summary>
public sealed partial class CurveParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ParamViewModel(moduleKey, def, host)
{
    private string? _editSession;

    [ObservableProperty]
    private IReadOnlyList<CurvePoint> _points = ToneCurve.Identity.Points;

    /// <summary>
    /// 編輯器送出的修改。同一次拖曳（直到 <paramref name="isFinal"/>）的所有修改合併為一筆復原紀錄，
    /// 結束時呼叫 <see cref="IParamHost.EndEdit"/>。
    /// </summary>
    public void OnEdited(IReadOnlyList<CurvePoint> points, bool isFinal)
    {
        var curve = ToneCurve.Create(points);
        _editSession ??= $"{FlatKey}#{Guid.NewGuid():N}";
        Points = curve.Points;
        Push(curve.Serialize(), _editSession);
        if (!isFinal) return;
        _editSession = null;
        Host.EndEdit();
    }

    protected override void OnLoad(ParamValue value) =>
        Points = ToneCurve.TryParse(value.AsText, out var curve) ? curve.Points : ToneCurve.Identity.Points;
}
