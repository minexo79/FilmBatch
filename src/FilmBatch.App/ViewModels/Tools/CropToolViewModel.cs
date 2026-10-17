using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.Localization;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels.Tools;

/// <summary>裁切框上的拖曳目標：8 個把手、框內（移動）、框外（旋轉）。</summary>
public enum CropHandle { None, Move, Rotate, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>比例下拉選單的一個選項。</summary>
public sealed record CropAspectOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 裁切工具（cut-and-mask 計畫 Task 1.7）：編輯中的裁切框存在 <see cref="Working"/>，
/// 套用（<see cref="Apply"/>）時才以一筆 <see cref="Settings"/> 一次寫入所有 <c>crop.*</c>（一筆復原紀錄）。
/// 座標：<see cref="CropParams"/> 的中心與寬高是「定向後」影像（90° 旋轉與翻轉之後、拉直之前）的正規化座標；
/// 把手拖曳的位移則以裁切框自己的軸向（拉直後）表示，單位同樣是定向影像寬 / 高的比例。
/// </summary>
public sealed partial class CropToolViewModel : ObservableObject
{
    private const double MinSize = 0.02;
    private static readonly CropParams Identity = new(true, 0.5, 0.5, 1, 1, 0, 0, false, false);

    private readonly Action<Settings> _commit;
    private readonly Func<(int W, int H)> _sourceSize;
    private readonly Func<Settings?>? _current;
    private CropParams _start = Identity;
    private string _startAspect = "free";
    private bool _startPortrait;
    private Settings _loaded = ModuleCatalog.Defaults;
    /// <summary>連續拉直時的基準框：角度每次都從這個框重新限制，角度轉回 0 時框也回到原本大小。</summary>
    private CropParams? _rotateBase;
    private bool _loading;
    private CropTool? _tool;

    /// <param name="commit">一次寫入多個 <c>crop.*</c> 鍵（包成一筆復原紀錄）。</param>
    /// <param name="sourceSize">目前照片（預覽代理圖）的寬高；沒有照片時回傳 (0, 0)。</param>
    /// <param name="current">進入工具時讀取目前照片的最終參數；null 時使用最近一次 <see cref="Load"/> 的值。</param>
    public CropToolViewModel(Action<Settings> commit, Func<(int W, int H)> sourceSize, Func<Settings?>? current = null)
    {
        _commit = commit;
        _sourceSize = sourceSize;
        _current = current;
        var def = ModuleCatalog.Param("crop.aspect")!;
        Aspects = def.Choices.Select(c => new CropAspectOption(c.Key, Loc.ChoiceLabel("crop", "aspect", c))).ToList();
    }

    /// <summary>工具是否啟用（畫布顯示裁切框，預覽暫停裁切顯示全幅）。</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>編輯中的裁切（<see cref="CropParams.On"/> 恆為 true）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Angle))]
    private CropParams _working = Identity;

    /// <summary>比例 key（<c>crop.aspect</c> 的選項）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedAspect), nameof(CanSwapOrientation))]
    private string _aspect = "free";

    /// <summary>固定比例時採用直式。</summary>
    [ObservableProperty]
    private bool _portrait;

    /// <summary>多選時裁切只作用於焦點照片（面板顯示提示）。</summary>
    [ObservableProperty]
    private bool _focusedOnly;

    /// <summary>畫布上正在拖曳裁切框（顯示三分線）。</summary>
    [ObservableProperty]
    private bool _isDragging;

    /// <summary>畫布上正在拖曳旋轉（改顯示細格線）。</summary>
    [ObservableProperty]
    private bool _isStraightening;

    public IReadOnlyList<CropAspectOption> Aspects { get; }

    public CropAspectOption? SelectedAspect
    {
        get => Aspects.FirstOrDefault(a => a.Key == Aspect);
        set { if (value is not null) Aspect = value.Key; }
    }

    /// <summary>直式 / 橫式只對「有方向」的固定比例有意義。</summary>
    public bool CanSwapOrientation => Aspect is not ("free" or "original" or "1x1");

    /// <summary>拉直角度（面板滑桿）。</summary>
    public double Angle
    {
        get => Working.AngleDeg;
        set
        {
            if (Math.Abs(value - Working.AngleDeg) > 1e-9) DragRotate(value - Working.AngleDeg);
        }
    }

    /// <summary>畫布上的裁切框工具。</summary>
    public ICanvasTool CanvasTool => _tool ??= new CropTool(this);

    /// <summary>定向後影像的像素尺寸（90° 旋轉後寬高互換）。</summary>
    public (int W, int H) OrientedSize
    {
        get
        {
            var (w, h) = _sourceSize();
            return Working.Quarter % 2 == 1 ? (h, w) : (w, h);
        }
    }

    /// <summary>未套用的修改（與進入工具時不同）。</summary>
    public bool HasChanges => IsActive && (Working != _start || Aspect != _startAspect || Portrait != _startPortrait);

    // ───────────── 載入 / 進入 / 套用 / 取消 ─────────────

    /// <summary>以照片的最終參數載入裁切狀態（不啟用工具）。</summary>
    public void Load(Settings settings)
    {
        _loaded = settings;
        var c = Read(settings);
        _loading = true;
        try
        {
            Aspect = ModuleCatalog.Param("crop.aspect")!.Coerce(settings.TryGetValue("crop.aspect", out var a) ? a : ParamValue.Of("free")).AsText;
            Portrait = settings.Bool("crop.aspect_portrait");
            Working = c;
        }
        finally { _loading = false; }
        _start = c;
        _startAspect = Aspect;
        _startPortrait = Portrait;
        _rotateBase = null;
    }

    /// <summary>未啟用的裁切視為全幅（不含殘留的旋轉、翻轉）。</summary>
    private static CropParams Read(Settings s)
    {
        if (!s.Bool("crop.enabled")) return Identity;
        double N(string k, double def) => s.ContainsKey(k) ? s.Number(k) : def;
        return new CropParams(true, N("crop.rect_cx", 0.5), N("crop.rect_cy", 0.5), N("crop.rect_w", 1), N("crop.rect_h", 1),
            N("crop.angle", 0), ((int)Math.Round(N("crop.quarter", 0)) % 4 + 4) % 4, s.Bool("crop.flip_h"), s.Bool("crop.flip_v"));
    }

    private bool HasSource => _sourceSize() is { W: > 0, H: > 0 };

    /// <summary>進入裁切工具：重新讀取目前照片的裁切，畫布改顯示全幅。</summary>
    [RelayCommand]
    public void Begin()
    {
        if (IsActive || !HasSource) return;
        if (_current is not null)
        {
            if (_current() is not { } settings) return;
            Load(settings);
        }
        else Load(_loaded);
        IsActive = true;
    }

    /// <summary>套用：有修改時一次寫入所有 <c>crop.*</c>；工具結束。</summary>
    [RelayCommand]
    public void Apply()
    {
        if (!IsActive) return;
        var changed = HasChanges;
        var c = Working;
        IsActive = false;
        IsDragging = IsStraightening = false;
        if (!changed) return;
        _start = c;
        _startAspect = Aspect;
        _startPortrait = Portrait;
        _commit(ToSettings(c, Aspect, Portrait));
    }

    /// <summary>取消：回到進入工具前的值（工具保持啟用，可重新調整）。</summary>
    [RelayCommand]
    public void Cancel()
    {
        if (!IsActive) return;
        Load(_loaded);
    }

    /// <summary>只有外部裁切／比例變更才放棄未套用的修改；RAW 與其他效果變更保留編輯中的裁切。</summary>
    public void Reload(Settings settings)
    {
        if (!IsActive) return;
        if (settings.Bool("crop.enabled") == _loaded.Bool("crop.enabled")
            && Read(settings) == _start
            && settings.Bool("crop.aspect_portrait") == _startPortrait
            && ModuleCatalog.Param("crop.aspect")!.Coerce(
                settings.TryGetValue("crop.aspect", out var aspect) ? aspect : ParamValue.Of("free")).AsText == _startAspect)
        {
            _loaded = settings;
            return;
        }
        Load(settings);
    }

    public static Settings ToSettings(CropParams c, string aspect, bool portrait) => Settings.Empty
        .With("crop.enabled", !IsIdentity(c))
        .With("crop.rect_cx", c.Cx).With("crop.rect_cy", c.Cy)
        .With("crop.rect_w", c.W).With("crop.rect_h", c.H)
        .With("crop.angle", c.AngleDeg).With("crop.quarter", (double)c.Quarter)
        .With("crop.flip_h", c.FlipH).With("crop.flip_v", c.FlipV)
        .With("crop.aspect", aspect).With("crop.aspect_portrait", portrait);

    private static bool IsIdentity(CropParams c) =>
        Math.Abs(c.Cx - 0.5) < 1e-9 && Math.Abs(c.Cy - 0.5) < 1e-9 && Math.Abs(c.W - 1) < 1e-9 && Math.Abs(c.H - 1) < 1e-9
        && Math.Abs(c.AngleDeg) < 1e-9 && c.Quarter == 0 && !c.FlipH && !c.FlipV;

    // ───────────── 旋轉 90°、翻轉、重設、比例 ─────────────

    [RelayCommand]
    private void RotateLeft() => Rotate90(clockwise: false);

    [RelayCommand]
    private void RotateRight() => Rotate90(clockwise: true);

    /// <summary>
    /// 畫面上的影像（含裁切框）轉 90°。只有一個軸翻轉時，來源的旋轉方向與畫面相反，所以 quarter 反向增減。
    /// 固定比例跟著轉成另一個方向。
    /// </summary>
    private void Rotate90(bool clockwise)
    {
        if (!IsActive) return;
        var c = Working;
        var step = clockwise ^ (c.FlipH ^ c.FlipV) ? 1 : 3;
        var (cx, cy) = clockwise ? (1 - c.Cy, c.Cx) : (c.Cy, 1 - c.Cx);
        SetWorking(c with { Cx = cx, Cy = cy, W = c.H, H = c.W, Quarter = (c.Quarter + step) % 4 });
        if (CanSwapOrientation) SetQuietly(() => Portrait = !Portrait);
    }

    [RelayCommand]
    private void FlipHorizontal()
    {
        if (!IsActive) return;
        var c = Working;
        SetWorking(c with { Cx = 1 - c.Cx, AngleDeg = -c.AngleDeg, FlipH = !c.FlipH });
    }

    [RelayCommand]
    private void FlipVertical()
    {
        if (!IsActive) return;
        var c = Working;
        SetWorking(c with { Cy = 1 - c.Cy, AngleDeg = -c.AngleDeg, FlipV = !c.FlipV });
    }

    /// <summary>重設成全幅、不旋轉、不翻轉、自由比例（仍需套用）。</summary>
    [RelayCommand]
    private void Reset()
    {
        if (!IsActive) return;
        SetQuietly(() =>
        {
            Aspect = "free";
            Portrait = false;
        });
        SetWorking(Identity);
    }

    /// <summary>X：切換直式 / 橫式比例。</summary>
    [RelayCommand]
    private void SwapOrientation()
    {
        if (!IsActive || !CanSwapOrientation) return;
        Portrait = !Portrait;
    }

    partial void OnAspectChanged(string value)
    {
        if (!_loading && IsActive) FitAspect();
    }

    partial void OnPortraitChanged(bool value)
    {
        if (!_loading && IsActive) FitAspect();
    }

    private double Ratio()
    {
        var (ow, oh) = OrientedSize;
        return ow <= 0 || oh <= 0 ? double.NaN : CropGeometry.AspectRatio(Aspect, Portrait, ow, oh);
    }

    /// <summary>換比例：以目前中心放上「影像內最大的該比例框」，再限制在影像內（拉直時會縮小）。</summary>
    private void FitAspect()
    {
        var r = Ratio();
        if (double.IsNaN(r)) return;
        var (ow, oh) = OrientedSize;
        var (wpx, hpx) = (double)ow / oh > r ? (oh * r, (double)oh) : (ow, ow / r);
        SetWorking(Working with { W = wpx / ow, H = hpx / oh });
    }

    // ───────────── 拖曳 ─────────────

    /// <summary>移動裁切框（定向影像的正規化位移）；拖出邊界時框停在邊上。</summary>
    public void DragMove(Point delta)
    {
        if (!IsActive) return;
        SetWorking(Working with { Cx = Working.Cx + delta.X, Cy = Working.Cy + delta.Y });
    }

    /// <summary>拉直：角度限制在 ±45°，框先移回影像內、移不進去才縮小。</summary>
    public void DragRotate(double deltaDeg)
    {
        if (!IsActive) return;
        var baseBox = _rotateBase ??= Working;
        var angle = Math.Clamp(Working.AngleDeg + deltaDeg, -45, 45);
        var (w, h) = _sourceSize();
        Working = CropGeometry.Constrain(w, h, baseBox with { AngleDeg = angle });
    }

    /// <summary>
    /// 拖曳把手改變大小。<paramref name="delta"/> 以裁切框自己的軸向表示（定向影像寬 / 高的比例）。
    /// 被拖曳的那一邊夾在影像內（不推動整個框）；固定比例時鎖比例；<paramref name="fromCenter"/>（Alt）以中心縮放。
    /// </summary>
    public void DragHandle(CropHandle handle, Point delta, bool fromCenter)
    {
        if (!IsActive || handle is CropHandle.None or CropHandle.Move or CropHandle.Rotate) return;
        var (sw, sh) = _sourceSize();
        var start = Working;
        bool Fits(CropParams c) => CropGeometry.FitsInside(sw, sh, c);
        var full = Resize(start, handle, delta.X, delta.Y, fromCenter);
        CropParams result;
        if (Fits(full) || !Fits(start)) result = full;
        else
        {
            // 二分搜尋：位移的最大比例（剛好碰到影像邊界）
            double lo = 0, hi = 1;
            for (var i = 0; i < 32; i++)
            {
                var mid = (lo + hi) / 2;
                if (Fits(Resize(start, handle, delta.X * mid, delta.Y * mid, fromCenter))) lo = mid; else hi = mid;
            }
            result = Resize(start, handle, delta.X * lo, delta.Y * lo, fromCenter);
        }
        SetWorking(result);
    }

    private CropParams Resize(CropParams c, CropHandle handle, double dx, double dy, bool fromCenter)
    {
        var (ow, oh) = OrientedSize;
        var left = handle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft;
        var right = handle is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight;
        var top = handle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight;
        var bottom = handle is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight;
        var k = fromCenter ? 2 : 1;
        var w = c.W + (right ? dx * k : 0) - (left ? dx * k : 0);
        var h = c.H + (bottom ? dy * k : 0) - (top ? dy * k : 0);

        var r = Ratio();
        if (!double.IsNaN(r))
        {
            var horizontalOnly = (left || right) && !(top || bottom);
            var verticalOnly = (top || bottom) && !(left || right);
            if (horizontalOnly) h = w * ow / (r * oh);
            else if (verticalOnly) w = h * oh * r / ow;
            else if (w * ow / r >= h * oh) h = w * ow / (r * oh);
            else w = h * oh * r / ow;
            var s = Math.Max(1, Math.Max(MinSize / Math.Max(w, 1e-9), MinSize / Math.Max(h, 1e-9)));
            (w, h) = (w * s, h * s);
        }
        else (w, h) = (Math.Max(MinSize, w), Math.Max(MinSize, h));

        // 對邊固定（以中心縮放或沒拖曳該軸時中心固定）；框自己軸向的中心位移
        var lx = fromCenter || !(left || right) ? 0 : left ? (c.W - w) / 2 : (w - c.W) / 2;
        var ly = fromCenter || !(top || bottom) ? 0 : top ? (c.H - h) / 2 : (h - c.H) / 2;
        var a = c.AngleDeg * Math.PI / 180;
        var (px, py) = (lx * ow, ly * oh);
        var cx = c.Cx + (Math.Cos(a) * px - Math.Sin(a) * py) / ow;
        var cy = c.Cy + (Math.Sin(a) * px + Math.Cos(a) * py) / oh;
        return c with { Cx = cx, Cy = cy, W = w, H = h };
    }

    /// <summary>非拉直的修改：限制在影像內，並結束連續拉直的基準。</summary>
    private void SetWorking(CropParams c)
    {
        _rotateBase = null;
        var (w, h) = _sourceSize();
        Working = w > 0 && h > 0 ? CropGeometry.Constrain(w, h, c with { On = true }) : c;
    }

    private void SetQuietly(Action change)
    {
        _loading = true;
        try { change(); }
        finally { _loading = false; }
    }

    // ───────────── 畫布幾何 ─────────────

    /// <summary>裁切框四角（左上、右上、右下、左下）在定向影像中的正規化座標（已套用拉直）。</summary>
    public Point[] Corners() => BoxPoints(Working, [(-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5)]);

    /// <summary>框內的點（框自己軸向的 -0.5–0.5 座標）換算成定向影像的正規化座標。</summary>
    public Point[] BoxPoints(CropParams c, IEnumerable<(double U, double V)> local)
    {
        var (ow, oh) = OrientedSize;
        if (ow <= 0 || oh <= 0) return local.Select(_ => new Point(c.Cx, c.Cy)).ToArray();
        var a = c.AngleDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        return local.Select(p =>
        {
            var (x, y) = (p.U * c.W * ow, p.V * c.H * oh);
            return new Point(c.Cx + (cos * x - sin * y) / ow, c.Cy + (sin * x + cos * y) / oh);
        }).ToArray();
    }

    /// <summary>定向影像正規化位移 → 裁切框軸向的位移（把手拖曳用）。</summary>
    public Point ToBoxDelta(Point imageDelta)
    {
        var (ow, oh) = OrientedSize;
        var a = Working.AngleDeg * Math.PI / 180;
        var (x, y) = (imageDelta.X * ow, imageDelta.Y * oh);
        return new Point((Math.Cos(a) * x + Math.Sin(a) * y) / ow, (-Math.Sin(a) * x + Math.Cos(a) * y) / oh);
    }

    /// <summary>拖曳開始：記下狀態，之後每次移動都從這個狀態重新計算（拖出邊界再拖回時框會跟著回來）。</summary>
    internal CropParams BeginDrag(bool straighten)
    {
        IsDragging = true;
        IsStraightening = straighten;
        return Working;
    }

    internal void Restore(CropParams c) => Working = c;

    internal void EndDrag() => IsDragging = IsStraightening = false;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(HasChanges));
        if (!value) IsDragging = IsStraightening = false;
    }
}
