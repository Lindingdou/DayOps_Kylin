using System.Collections.Generic;
using Avalonia.Input;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// Gizmo —— 选中对象上的三轴变换手柄（同 AutoCAD 3DMOVE gizmo）：
/// 选中对象后在其包围盒中心画 +X 红 / +Y 绿 / +Z 蓝三根箭头，光标压上变黄，按住某根轴拖 = 沿该轴平移整个选择集
/// （幽灵跟随, 松开落地, 一步 Undo）。2D 只画 X/Y。GIZMO 命令 / 视图·Gizmo 键 / 选项·显示 切换显示。
///
/// 原版 PitMine3D 的 GIZMO 内核里只是夹点显示开关（Editor::ToggleGizmo → m_showGizmo 只管 vs.Grips），
/// 用户 2026-09-13 明确要求改成真正的三轴手柄；夹点显示开关另留为「夹点开关」命令（<c>ToggleGrips</c>）。
/// 几何/命中/拖拽量的纯函数在 <see cref="GizmoGlyph"/>；这里只管状态与视口事件接线。
/// </summary>
public partial class MainWindow
{
    private bool _gizmoOn = true;                            // Gizmo 显示开关(GIZMO)
    private (double x, double y, double z)? _gizmoCenter;    // 手柄锚点 = 选择集三维包围盒中心(选择集变了在 GizmoRebuild 重算)
    private int _gizmoHover = -1;                            // 悬停轴 0=X 1=Y 2=Z, -1 无
    private GizmoDragState? _gizmoDrag;                      // 沿轴拖拽中
    private (double sx, double sy, double wpp)? _gizmoStamp; // 上次画手柄时中心的屏幕位置 + 像素尺度: 视图变了要按新尺度重画
    private float[]? _gizmoBase;                             // 上次高亮缓冲(实体线 + 夹点, 不含手柄): 只换手柄时直接复用, 免得重镶嵌整份选择集

    private sealed class GizmoDragState
    {
        public int Axis;                                     // 拖的是哪根轴
        public double S0;                                    // 按下时光标射线在轴上的参数
        public double Delta;                                 // 当前沿轴位移(世界单位)
        public float[] Ghost = System.Array.Empty<float>();  // 幽灵线框(缓冲坐标, 未平移, 已着预览色)
        public Avalonia.Point Start;                         // 按下处(屏幕), 没拖过阈值当没动
        public bool Moved;
    }

    /// <summary>GIZMO：切换三轴手柄显示。</summary>
    private void ToggleGizmo()
    {
        _gizmoOn = !_gizmoOn;
        if (!_gizmoOn) { GizmoCancel(); _gizmoHover = -1; }
        RedrawHighlight();
        EditEcho($"GIZMO {(_gizmoOn ? "ON" : "OFF")}");   // 回显同原版 ribbon("> GIZMO ON/OFF"); 它也会写状态栏, 所以友好提示放后面
        StatusMsg.Text = _gizmoOn ? "Gizmo：开（选中对象后拖 X/Y/Z 轴沿轴移动）" : "Gizmo：关";
    }

    /// <summary>选择集变了：重算手柄锚点(HighlightSelection 里调)。</summary>
    private void GizmoRebuild()
    {
        _gizmoHover = -1;
        _gizmoBase = null;
        _gizmoCenter = GizmoCenterOf(_selected);
    }

    /// <summary>选择集三维包围盒中心：三角网/点云取 Bounds，三维多段线逐点 z，其余按夹点 XY + 标高。</summary>
    private static (double x, double y, double z)? GizmoCenterOf(List<SceneEntity> sel)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool any = false;
        void Acc(double x, double y, double z)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
            any = true;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
        foreach (var e in sel)
        {
            if (!e.Visible) continue;
            if (e is MeshEntity me)
            {
                if (me.TriangleCount == 0) continue;
                var b = me.Bounds; Acc(b.minX, b.minY, b.minZ); Acc(b.maxX, b.maxY, b.maxZ);
            }
            else if (e is PointCloudEntity pc)
            {
                var b = pc.Bounds; Acc(b.minX, b.minY, b.minZ); Acc(b.maxX, b.maxY, b.maxZ);
            }
            else if (e is PolylineEntity pl && pl.Has3D)
            {
                for (int i = 0; i < pl.Points.Count; i++) Acc(pl.Points[i].x, pl.Points[i].y, pl.ZAt(i));
            }
            else
            {
                foreach (var g in e.Grips()) Acc(g.x, g.y, e.Elevation);
            }
        }
        return any ? ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2) : null;
    }

    /// <summary>手柄当前锚点(拖拽中随位移走)。</summary>
    private (double x, double y, double z)? GizmoAnchor()
    {
        if (_gizmoCenter is not { } c) return null;
        if (_gizmoDrag is { } d)
        {
            var a = GizmoGlyph.AxisDirs[d.Axis];
            return (c.x + a.x * d.Delta, c.y + a.y * d.Delta, c.z + a.z * d.Delta);
        }
        return c;
    }

    /// <summary>
    /// RedrawHighlight 出口：把手柄追加到高亮缓冲(深度关, 画最上层)。传进来的 o 是"实体线 + 夹点"的整份缓冲，
    /// 顺手存一份, 之后只换手柄(悬停/缩放)时不必再镶嵌选择集。
    /// </summary>
    private void AppendGizmo(List<float> o)
    {
        if (!_gizmoOn || _selected.Count == 0) { _gizmoBase = null; _gizmoStamp = null; return; }
        _gizmoBase = o.ToArray();
        AppendGizmoGeom(o);
    }

    private void AppendGizmoGeom(List<float> o)
    {
        _gizmoStamp = null;
        if (!_gizmoOn || GizmoAnchor() is not { } c) return;
        var proj = Viewport.WorldToScreenProjector();
        double wpp = GizmoGlyph.WorldPerPixel(proj, c.x, c.y, c.z);
        var sc = proj(c.x, c.y, c.z);
        if (wpp <= 0 || sc == null) return;
        _gizmoStamp = (sc.Value.sx, sc.Value.sy, wpp);
        var ray = Viewport.ScreenRay(sc.Value.sx, sc.Value.sy);
        (double x, double y, double z)? vd = ray == null ? null : (ray.Value.dx, ray.Value.dy, ray.Value.dz);
        foreach (var pl in GizmoGlyph.Build(c, wpp, vd, Viewport.Is2DView, _gizmoHover, _gizmoDrag?.Axis ?? -1))
            pl.Tessellate(o);
    }

    /// <summary>只重画手柄(悬停变色 / 视图变了按新尺度)，实体高亮与夹点复用上次缓冲。</summary>
    private void GizmoRedraw()
    {
        if (_gizmoBase == null) { RedrawHighlight(); return; }
        var o = new List<float>(_gizmoBase);
        AppendGizmoGeom(o);
        Viewport.SetHighlight(o.ToArray(), recolor: false);
    }

    /// <summary>光标压在哪根轴上(屏幕空间判)。</summary>
    private int GizmoHitAxis(Avalonia.Point p)
    {
        if (!_gizmoOn || _selected.Count == 0 || GizmoAnchor() is not { } c) return -1;
        var proj = Viewport.WorldToScreenProjector();
        double wpp = GizmoGlyph.WorldPerPixel(proj, c.x, c.y, c.z);
        return GizmoGlyph.HitAxis(proj, c, wpp, Viewport.Is2DView, p.X, p.Y);
    }

    /// <summary>视图(缩放/旋转/平移)变了没：中心屏幕位置或像素尺度与上次画时不同。</summary>
    private bool GizmoViewChanged()
    {
        if (_gizmoStamp is not { } s || GizmoAnchor() is not { } c) return true;
        var proj = Viewport.WorldToScreenProjector();
        var sc = proj(c.x, c.y, c.z);
        if (sc == null) return true;
        double wpp = GizmoGlyph.WorldPerPixel(proj, c.x, c.y, c.z);
        return System.Math.Abs(sc.Value.sx - s.sx) > 0.5 || System.Math.Abs(sc.Value.sy - s.sy) > 0.5
            || System.Math.Abs(wpp - s.wpp) > s.wpp * 1e-3;
    }

    /// <summary>空闲态光标移动：轴悬停变黄；视图变了(滚轮缩放/旋转后)也顺手按新尺度重画。</summary>
    private void GizmoHover(Avalonia.Point p)
    {
        if (!_gizmoOn || _selected.Count == 0 || _gizmoCenter == null) return;
        int hv = GizmoHitAxis(p);
        if (hv != _gizmoHover || GizmoViewChanged()) { _gizmoHover = hv; GizmoRedraw(); }
    }

    /// <summary>左键按下：压在某根轴上 → 开始沿轴拖拽(返回 true 表示接管了这次按下)。</summary>
    private bool GizmoTryBeginDrag(Avalonia.Point p)
    {
        if (!_gizmoOn || _selected.Count == 0 || GizmoAnchor() is not { } c) return false;
        int axis = GizmoHitAxis(p);
        if (axis < 0) return false;
        var ray = Viewport.ScreenRay(p.X, p.Y);
        if (ray == null) return false;
        double? s0 = GizmoGlyph.AxisParam(ray.Value, c, axis);
        if (s0 == null) { StatusMsg.Text = $"Gizmo：正对着 {GizmoGlyph.AxisNames[axis]} 轴看，拖不动，请转个视角"; return false; }
        foreach (var e in _selected)
            if (IsLayerLocked(e)) { StatusMsg.Text = $"Gizmo：图层「{e.LayerName}」已锁定，不可移动"; return true; }

        var ghost = new List<float>();
        foreach (var e in _selected) GizmoGhostOf(e, ghost);
        _gizmoDrag = new GizmoDragState
        {
            Axis = axis, S0 = s0.Value, Delta = 0, Start = p,
            Ghost = Controls.CadGlViewport.Recolor(ghost.ToArray(), 1f, 0.9f, 0.2f),
        };
        _gizmoHover = axis;
        GizmoRedraw();
        StatusMsg.Text = $"Gizmo：沿 {GizmoGlyph.AxisNames[axis]} 轴拖动（松开落地 · Esc 取消）";
        return true;
    }

    /// <summary>拖拽幽灵：选中实体的线框(大三角网/点云只画包围盒棱, 免得逐帧搬几百万顶点)。</summary>
    private static void GizmoGhostOf(SceneEntity e, List<float> o)
    {
        const int maxTris = 20000;
        var col = (1f, 0.9f, 0.2f);
        if (e is MeshEntity me)
        {
            if (me.TriangleCount == 0) return;
            if (me.TriangleCount <= maxTris) { me.TessellateEdges(o); return; }
            var b = me.Bounds;
            foreach (var pl in GizmoGlyph.BoxEdges(b.minX, b.minY, b.minZ, b.maxX, b.maxY, b.maxZ, col)) pl.Tessellate(o);
        }
        else if (e is PointCloudEntity pc) pc.TessellateBoundsBox(o);
        else e.TessellatePick(o);
    }

    /// <summary>拖拽中光标移动：沿轴求光标射线的最近点 → 位移；幽灵 + 手柄一起挪，光标旁报读数。</summary>
    private void GizmoDragMove(Avalonia.Point p)
    {
        if (_gizmoDrag is not { } d || _gizmoCenter is not { } c) return;
        if (!d.Moved && System.Math.Abs(p.X - d.Start.X) < 4 && System.Math.Abs(p.Y - d.Start.Y) < 4) return;
        d.Moved = true;
        var ray = Viewport.ScreenRay(p.X, p.Y);
        if (ray == null) return;
        double? s = GizmoGlyph.AxisParam(ray.Value, c, d.Axis);
        if (s == null) return;
        d.Delta = s.Value - d.S0;

        // 幽灵 = 起拖时的线框整体平移(缓冲坐标下平移与原点口径无关)
        var a = GizmoGlyph.AxisDirs[d.Axis];
        float dx = (float)(a.x * d.Delta), dy = (float)(a.y * d.Delta), dz = (float)(a.z * d.Delta);
        var g = (float[])d.Ghost.Clone();
        for (int i = 0; i + 2 < g.Length; i += 6) { g[i] += dx; g[i + 1] += dy; g[i + 2] += dz; }
        var o = new List<float>(g);
        AppendGizmoGeom(o);
        Viewport.SetHighlight(o.ToArray(), recolor: false);

        string name = GizmoGlyph.AxisNames[d.Axis];
        ShowTipAt(p, $"沿 {name} 轴  Δ{name} {d.Delta:0.##}");
        StatusMsg.Text = $"Gizmo：沿 {name} 轴移动 {d.Delta:0.###}（松开落地 · Esc 取消）";
    }

    /// <summary>松开：拖过阈值就按位移落地(整组一步 Undo)，没拖就当没动。</summary>
    private void GizmoDragEnd(Avalonia.Point p)
    {
        if (_gizmoDrag is not { } d) return;
        if (d.Moved) GizmoDragMove(p);   // 用松开点算准最终位移
        _gizmoDrag = null;
        HideDragTip();
        if (!d.Moved || System.Math.Abs(d.Delta) < 1e-9) { GizmoRedraw(); return; }
        var a = GizmoGlyph.AxisDirs[d.Axis];
        int n = _selected.Count;
        ApplyEditTransform(Affine2.Translate(a.x * d.Delta, a.y * d.Delta), copy: false, dz: a.z * d.Delta);
        StatusMsg.Text = $"Gizmo：沿 {GizmoGlyph.AxisNames[d.Axis]} 轴移动 {d.Delta:0.###}（{n} 个实体）";
    }

    /// <summary>取消拖拽(Esc / 关 Gizmo)：丢掉拖拽态，不动实体。</summary>
    private void GizmoCancel()
    {
        if (_gizmoDrag == null) return;
        _gizmoDrag = null;
        HideDragTip();
    }
}
