using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Plan;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「采场/排土场圈定」对视口的三样依赖（原 PitMineApp 宿主 <c>PitDesignCapabilityImpl</c> 里 overlay / 逐点取点 / 选区笔刷 的托管等价）：
///   · 区域 overlay = 图层「可采区域」上的闭合三维多段线（整通道替换：每次 Show 先清）；
///   · 逐点取点 = 复用 <see cref="PickPointOrConfirmAsync"/> 循环（左键加点、右键/回车/Esc 结束），Z 取该处地表三角网采样；
///   · 选区笔刷 = <see cref="RegionBrushSession"/>（PS 式掩膜涂改）+ 指针按下/移动/松开三处钩子 + 预览里画笔刷圆圈。
/// </summary>
public partial class MainWindow
{
    private const string RegionOverlayLayer = "可采区域";
    private readonly List<SceneEntity> _regionOverlay = new();

    // ── overlay ──
    private void ShowRegionOverlay(IReadOnlyList<double[]> rings, IReadOnlyList<uint> colors)
    {
        ClearRegionOverlay(refresh: false);
        _layers.EnsureImported(RegionOverlayLayer, 0.85f, 0.35f, 0.19f);
        for (int i = 0; i < rings.Count; i++)
        {
            var r = rings[i];
            if (r == null || r.Length < 6) continue;
            uint rgb = i < colors.Count ? colors[i] : 0x26D973u;
            var pl = new PolylineEntity
            {
                LayerName = RegionOverlayLayer, Closed = r.Length >= 9,
                Cr = ((rgb >> 16) & 0xFF) / 255f, Cg = ((rgb >> 8) & 0xFF) / 255f, Cb = (rgb & 0xFF) / 255f,
                Elevation = r[2], Zs = new List<double>(r.Length / 3), LineWeight = 2,
            };
            for (int k = 0; k + 2 < r.Length; k += 3) { pl.Points.Add((r[k], r[k + 1])); pl.Zs.Add(r[k + 2] - r[2]); }
            _scene.Add(pl);
            _regionOverlay.Add(pl);
        }
        RefreshScene();
    }

    private void ClearRegionOverlay(bool refresh = true)
    {
        foreach (var e in _regionOverlay) { _scene.Remove(e); _selected.Remove(e); }
        _regionOverlay.Clear();
        if (refresh) RefreshScene();
    }

    // ── 逐点取点（圈画）──
    private int _regionPickSession;   // 递增号：EndScreenPointPick 后旧循环自动失效

    private bool BeginRegionPointPick(Action<double, double, double> onPicked, Action onCancel)
    {
        int session = ++_regionPickSession;
        _ = RunRegionPointPickAsync(session, onPicked, onCancel);
        return true;
    }

    private async Task RunRegionPointPickAsync(int session, Action<double, double, double> onPicked, Action onCancel)
    {
        int n = 0;
        while (session == _regionPickSession)
        {
            var (kind, x, y) = await PickPointOrConfirmAsync($"圈画区域：左键点第 {n + 1} 个顶点 · 右键/回车结束并成区域 · Esc 结束", confirmable: true, quiet: n > 0);
            if (session != _regionPickSession) return;   // 窗口已主动退出
            if (kind != PickKind.Picked) break;
            double z = SampleSurfaceZ(x, y) ?? 0;
            n++;
            onPicked(x, y, z);
        }
        if (session != _regionPickSession) return;
        _regionPickSession++;
        onCancel();
    }

    private void EndRegionPointPick()
    {
        _regionPickSession++;
        CancelOneShotPick();
    }

    /// <summary>该 XY 处地表高程：所有可见三角网里最高的命中值；都没命中返回 null。</summary>
    private double? SampleSurfaceZ(double x, double y)
    {
        double? best = null;
        foreach (var me in _scene.Entities.OfType<MeshEntity>())
        {
            if (!me.Visible || me.Verts.Count < 3) continue;
            var z = TinSampler.SampleZ(me.Verts, me.Tris, x, y);
            if (z.HasValue && (best == null || z.Value > best.Value)) best = z;
        }
        return best;
    }

    // ── 选区笔刷 ──
    private RegionBrushSession? _regionBrush;
    private Action<double[]>? _regionBrushOnCommit;
    private Action? _regionBrushOnCancel;
    private bool _regionBrushPainting;
    private int _regionBrushRadiusPx = 16;
    private double _regionBrushCurX, _regionBrushCurY, _regionBrushCurR;

    private bool BeginRegionBrush(double[] targetRing, uint targetColor, IReadOnlyList<double[]> contextRings, IReadOnlyList<uint> contextColors,
                                  Action<double[]> onCommit, Action onCancel)
    {
        if (targetRing == null || targetRing.Length < 9) return false;
        if (_regionBrush != null) EndRegionBrush(false);
        _regionBrush = new RegionBrushSession(targetRing, targetColor, contextRings, contextColors, 0.0);
        _regionBrushOnCommit = onCommit; _regionBrushOnCancel = onCancel;
        _regionBrushPainting = false; _regionBrushCurR = 0;
        _pickCursor = Controls.CadGlViewport.CursorMode.CrosshairOnly;
        SyncCursorMode();
        RedrawRegionBrush();
        return true;
    }

    private void EndRegionBrush(bool commit)
    {
        var b = _regionBrush; var onCommit = _regionBrushOnCommit; var onCancel = _regionBrushOnCancel;
        _regionBrush = null; _regionBrushOnCommit = null; _regionBrushOnCancel = null; _regionBrushPainting = false; _regionBrushCurR = 0;
        SyncCursorMode();
        HideDragTip();
        if (b == null) return;
        if (commit) onCommit?.Invoke(b.HasArea() ? b.Ring : Array.Empty<double>());
        else onCancel?.Invoke();
        RefreshScene();
    }

    private int SetRegionBrushRadius(int px)
    {
        _regionBrushRadiusPx = Math.Clamp(px, 4, 80);
        if (_regionBrush != null) RedrawRegionBrush();
        return _regionBrushRadiusPx;
    }

    /// <summary>重绘：背景其它区域(类别色) + 选中区域选区(目标色，实时重描)；笔刷圆圈在预览里画（随光标）。</summary>
    private void RedrawRegionBrush()
    {
        if (_regionBrush == null) return;
        var rings = new List<double[]>(); var colors = new List<uint>();
        var ctx = _regionBrush.Context; var ctxC = _regionBrush.ContextColors;
        for (int i = 0; i < ctx.Count; i++)
        {
            if (ctx[i] == null || ctx[i].Length < 9) continue;
            rings.Add(ctx[i]); colors.Add(i < ctxC.Count ? ctxC[i] : 0x26D973u);
        }
        if (_regionBrush.Ring.Length >= 9) { rings.Add(_regionBrush.Ring); colors.Add(_regionBrush.TargetColor); }
        ShowRegionOverlay(rings, colors);
    }

    /// <summary>笔刷半径(屏幕 px)换算成世界距（随缩放自适应）。</summary>
    private double RegionBrushRadiusWorld(Avalonia.Point p)
    {
        var c = Viewport.ScreenToWorld(p.X, p.Y);
        var e = Viewport.ScreenToWorld(p.X + _regionBrushRadiusPx, p.Y);
        if (c == null || e == null) return 5.0;
        double r = Math.Sqrt((e.Value.x - c.Value.x) * (e.Value.x - c.Value.x) + (e.Value.y - c.Value.y) * (e.Value.y - c.Value.y));
        return r > 0 ? r : 5.0;
    }

    /// <summary>指针按下：笔刷态下左键落笔（Alt=并入/扩，否则移出/缩）；吞掉。中/右键照常导航。</summary>
    private bool RegionBrushOnPressed(PointerPressedEventArgs e)
    {
        if (_regionBrush == null) return false;
        var props = e.GetCurrentPoint(ViewportHost).Properties;
        if (!props.IsLeftButtonPressed) return false;
        bool add = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _regionBrush.StrokeBegin(add);
        _regionBrushPainting = true;
        _nav = NavMode.None;
        e.Pointer.Capture(ViewportHost);
        var w = Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
        if (w != null) { _regionBrush.StrokeMove(w.Value.x, w.Value.y, RegionBrushRadiusWorld(_lastPointer)); RedrawRegionBrush(); }
        return true;
    }

    /// <summary>指针移动：圆圈跟随；按住左键时涂改 + 实时重描。返回 true=已消费（不再走捕捉/框选）。</summary>
    private bool RegionBrushOnMoved(Avalonia.Point p)
    {
        if (_regionBrush == null) return false;
        var w = Viewport.ScreenToWorld(p.X, p.Y);
        if (w == null) return true;
        double rad = RegionBrushRadiusWorld(p);
        _regionBrushCurX = w.Value.x; _regionBrushCurY = w.Value.y; _regionBrushCurR = rad;
        if (_regionBrushPainting)
        {
            _regionBrush.StrokeMove(w.Value.x, w.Value.y, rad);
            RedrawRegionBrush();
        }
        else RefreshScenePreview();
        ShowTipAt(p, $"笔刷 {_regionBrushRadiusPx}px · Alt+拖=并入(扩) · 拖=移出(缩) · 窗口「完成」/「取消」· Esc 取消");
        _lastPointer = p;
        return true;
    }

    /// <summary>指针松开：抬笔结束本笔。</summary>
    private bool RegionBrushOnReleased(PointerReleasedEventArgs e)
    {
        if (_regionBrush == null || !_regionBrushPainting) return false;
        _regionBrush.StrokeEnd();
        _regionBrushPainting = false;
        e.Pointer.Capture(null);
        RedrawRegionBrush();
        return true;
    }

    /// <summary>Esc：笔刷态下取消编辑（弃改）。</summary>
    private bool RegionBrushOnEscape()
    {
        if (_regionBrush == null) return false;
        EndRegionBrush(false);
        return true;
    }

    /// <summary>预览：笔刷圆圈（白）跟随光标。</summary>
    private void RegionBrushAppendPreview(List<float> list)
    {
        if (_regionBrush == null || _regionBrushCurR <= 0) return;
        const int seg = 32;
        var pv = new PolylineEntity { Closed = true, Cr = 1f, Cg = 1f, Cb = 1f, Elevation = _regionBrush.RepZ };
        for (int i = 0; i < seg; i++)
        {
            double t = 2 * Math.PI * i / seg;
            pv.Points.Add((_regionBrushCurX + _regionBrushCurR * Math.Cos(t), _regionBrushCurY + _regionBrushCurR * Math.Sin(t)));
        }
        pv.Tessellate(list);
    }
}
