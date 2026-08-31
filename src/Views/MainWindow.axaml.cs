using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PopulateDrawingLayers();   // 启动即显示绘制图层("0")，可管理
        SetDocPath(null);          // 初始标题=未命名
        RenderAssistant(_assistant.Current());   // 智能助手：启动显示欢迎 + 主菜单

        // OpenGL 上下文就绪后，把真实后端版本显示到视口与状态栏
        Viewport.GlReady += backend =>
        {
            GlInfo.Text = $"渲染后端: {backend}";
            StatusMsg.Text = $"OpenGL 就绪 · {backend}";
        };

        // 视口交互：在宿主 Panel（可命中）上收指针事件，转发到相机。
        // OpenGlControlBase 自身无背景时命中测试不可靠，直接在其上收事件在部分后端收不到，
        // 故统一在 ViewportHost（Background=Transparent → 全区可命中）上处理。
        ViewportHost.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(ViewportHost).Properties;
            _lastPointer = e.GetPosition(ViewportHost);
            _pressPos = _lastPointer;

            // 测距模式：左键取点（第一/第二点）
            if (_measure != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    var first = _measure.First;
                    var d = _measure.AddPoint(wp.Value.x, wp.Value.y);
                    if (d == null)
                        StatusMsg.Text = "测距：点第二点";
                    else
                    {
                        StatusMsg.Text = $"距离 = {d:0.###}";
                        if (first != null)
                            Viewport.SetHighlight(new float[]
                            {
                                (float)first.Value.x, (float)first.Value.y, 0, 0, 0, 0,
                                (float)wp.Value.x,    (float)wp.Value.y,    0, 0, 0, 0
                            });
                        _measure = null;
                    }
                }
                return;
            }

            // 三点测角模式：顶点 → 第一射线端 → 第二射线端
            if (_angle != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    var v = _angle.Vertex; var a = _angle.FirstRay;
                    var deg = _angle.AddPoint(wp.Value.x, wp.Value.y);
                    if (deg == null)
                        StatusMsg.Text = _angle.HasFirstRay ? "测角：点第二边端点" : "测角：点第一边端点";
                    else
                    {
                        StatusMsg.Text = $"角度 = {deg:0.##}°";
                        if (v != null && a != null)
                            Viewport.SetHighlight(new float[]
                            {
                                (float)a.Value.x, (float)a.Value.y, 0, 0, 0, 0,
                                (float)v.Value.x, (float)v.Value.y, 0, 0, 0, 0,
                                (float)v.Value.x, (float)v.Value.y, 0, 0, 0, 0,
                                (float)wp.Value.x, (float)wp.Value.y, 0, 0, 0, 0
                            });
                        _angle = null;
                    }
                }
                return;
            }

            // 编辑（移动/复制/镜像）：左键取点（与命令行坐标共用 FeedPoint）
            if (_editMode != EditMode.None && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null) FeedPoint(wp.Value.x, wp.Value.y);
                return;
            }

            // 修剪/延伸：点目标线 → 其近端点移到与边界(任意实体)的最近交点
            if (_trimActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null && _selected.Count == 1)
                {
                    var boundary = _selected[0];
                    var hit = _scene.Pick(wp.Value.x, wp.Value.y, SnapTolWorld(_lastPointer) * 3, _layers.IsSelectable);
                    if (hit is LineEntity target && !ReferenceEquals(target, boundary))
                    {
                        var nl = TrimExtend(target, boundary, (wp.Value.x, wp.Value.y));
                        if (nl != null) { BeginChange(); _scene.Replace(target, nl); StatusMsg.Text = "已修剪/延伸"; }
                        else StatusMsg.Text = "与边界无交点，无法修剪/延伸";
                    }
                    else if (hit is PolylineEntity ptarget && !ReferenceEquals(ptarget, boundary))
                    {
                        var np = TrimTools.TrimExtendPolylineEnd(ptarget, boundary, wp.Value.x, wp.Value.y);
                        if (np != null) { BeginChange(); _scene.Replace(ptarget, np); StatusMsg.Text = "已修剪/延伸多段线端"; }
                        else StatusMsg.Text = "与边界无交点，无法修剪/延伸";
                    }
                    else if (hit is ArcEntity atarget && !ReferenceEquals(atarget, boundary))
                    {
                        var na = TrimTools.TrimExtendArc(atarget, boundary, wp.Value.x, wp.Value.y);
                        if (na != null) { BeginChange(); _scene.Replace(atarget, na); StatusMsg.Text = "已修剪/延伸圆弧端"; }
                        else StatusMsg.Text = "与边界无交点，无法修剪/延伸";
                    }
                    else StatusMsg.Text = "未点中目标（目标须为直线/多段线/圆弧）";
                    RefreshScene();
                }
                _trimActive = false;
                return;
            }

            // 打断：取两点 → 移除选中实体两点间的一段
            if (_breakActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null && _selected.Count == 1)
                {
                    _breakPts.Add((wp.Value.x, wp.Value.y));
                    if (_breakPts.Count < 2) StatusMsg.Text = "打断：指定第二点";
                    else
                    {
                        var parts = _selected[0].Break(_breakPts[0].x, _breakPts[0].y, _breakPts[1].x, _breakPts[1].y);
                        if (parts != null)
                        {
                            BeginChange();
                            _scene.Remove(_selected[0]);
                            foreach (var pe in parts) _scene.Add(pe);
                            _selected.Clear(); Viewport.SetHighlight(null);
                            StatusMsg.Text = $"已打断（剩 {parts.Count} 段）";
                        }
                        else StatusMsg.Text = "该实体不支持打断（点/文字；直线/多段线/圆弧/圆/矩形/多边形可打断）";
                        _breakActive = false; _breakPts.Clear();
                        RefreshScene();
                    }
                }
                return;
            }

            // 线性标注：取两点 → 尺寸线 + 距离文字
            if (_dimActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    if (_dimP1 == null) { _dimP1 = (wp.Value.x, wp.Value.y); StatusMsg.Text = "标注：指定第二点"; }
                    else
                    {
                        double h = System.Math.Max(SnapTolWorld(_lastPointer) * 2.5, 1e-3);
                        var dim = DimTools.Build(_dimP1.Value.x, _dimP1.Value.y, wp.Value.x, wp.Value.y, h, _dimStyle);
                        BeginChange();
                        foreach (var de in dim) { de.LayerName = _layers.Current.Name; _scene.Add(de); }
                        RefreshScene();
                        StatusMsg.Text = "已标注";
                        _lastDimP2 = (wp.Value.x, wp.Value.y);   // 供连续标注接续
                        _dimActive = false; _dimP1 = null;
                    }
                }
                return;
            }

            // 半径标注：已选圆/弧，点击给方向 → 径向线 + 箭头 + "R值"
            if (_dimRadActive && _dimRadCircle != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    var c = _dimRadCircle.Value;
                    double h = System.Math.Max(SnapTolWorld(_lastPointer) * 2.5, 1e-3);
                    var dim = DimTools.BuildRadial(c.cx, c.cy, c.r, wp.Value.x - c.cx, wp.Value.y - c.cy, h, _dimStyle);
                    BeginChange();
                    foreach (var de in dim) { de.LayerName = _layers.Current.Name; _scene.Add(de); }
                    RefreshScene();
                    StatusMsg.Text = $"已标注 R{c.r:0.##}";
                    _dimRadActive = false; _dimRadCircle = null;
                }
                return;
            }

            // 高程查询：点击任意点 → IDW 报高程 + 标记（连续，ESC 退出）
            if (_spotActive && props.IsLeftButtonPressed && _spotTerrain != null)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    double z = Contour.IdwAt(_spotTerrain, wp.Value.x, wp.Value.y);
                    var mk = new PointEntity { X = wp.Value.x, Y = wp.Value.y, Size = SnapTolWorld(_lastPointer), Cr = 0.95f, Cg = 0.85f, Cb = 0.30f };
                    BeginChange(); _scene.Add(mk); RefreshScene();
                    StatusMsg.Text = $"高程查询：({wp.Value.x:0.##}, {wp.Value.y:0.##}) → z = {z:0.###}（继续点，ESC 退出）";
                }
                return;
            }

            // 分帮扩帮：点方向/步距 → 批量偏移台阶线
            if (_benchActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null && _benchEntity != null)
                {
                    var lines = BenchTools.BatchOffset(_benchEntity, wp.Value.x, wp.Value.y, _benchCount);
                    if (lines.Count > 0)
                    {
                        BeginChange();
                        foreach (var bl in lines) _scene.Add(bl);
                        RefreshScene();
                        StatusMsg.Text = $"分帮扩帮：生成 {lines.Count} 条平行台阶";
                    }
                    else StatusMsg.Text = "分帮扩帮：无法偏移（仅线/多段线）";
                }
                _benchActive = false; _benchEntity = null;
                return;
            }

            // 点对点寻径：取起点、终点
            if (_pathActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    if (_pathP1 == null) { _pathP1 = (wp.Value.x, wp.Value.y); StatusMsg.Text = _kpathMode ? "备选路径：点终点" : "点对点寻径：点终点"; }
                    else { if (_kpathMode) ComputeKPaths(_pathP1.Value, (wp.Value.x, wp.Value.y)); else ComputePath(_pathP1.Value, (wp.Value.x, wp.Value.y)); _pathActive = false; _pathP1 = null; _kpathMode = false; }
                }
                return;
            }

            // 基点粘贴：拾取插入点
            if (_pasteBaseActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null) { PasteAtPoint(wp.Value); _pasteBaseActive = false; }
                return;
            }

            // 偏移：点击一侧 → 偏移选中实体（保留原实体颜色/图层）
            if (_offsetActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null && _selected.Count == 1)
                {
                    var off = _selected[0].Offset(wp.Value.x, wp.Value.y);
                    if (off != null) { BeginChange(); _scene.Add(off); StatusMsg.Text = "已偏移"; }
                    else StatusMsg.Text = "该实体不支持偏移（如点/退化几何）";
                    RefreshScene();
                }
                _offsetActive = false;
                return;
            }

            // 滑动多段线：按住左键开始，拖动自动采样，松开成线
            if (_slideActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                e.Pointer.Capture(ViewportHost);
                _slideDragging = true;
                _slidePts.Clear();
                var wp = Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null) { _slidePts.Add((wp.Value.x, wp.Value.y)); _slideLastScreen = _lastPointer; }
                StatusMsg.Text = "滑动多段线：按住拖动…松开结束";
                return;
            }

            // 圆 TTR：依次点两个相切参照(直线或圆)（半径走命令行）
            if (_ttrActive && !_ttrAwaitRadius && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    var hit = _scene.Pick(wp.Value.x, wp.Value.y, SnapTolWorld(_lastPointer) * 3, _layers.IsSelectable);
                    if (hit is LineEntity or CircleEntity)
                    {
                        if (_ttrRef1 == null) { _ttrRef1 = hit; _ttrPick1 = (wp.Value.x, wp.Value.y); StatusMsg.Text = "圆TTR：点第二个相切参照(直线/圆)"; }
                        else if (!ReferenceEquals(hit, _ttrRef1)) { _ttrRef2 = hit; _ttrPick2 = (wp.Value.x, wp.Value.y); _ttrAwaitRadius = true; StatusMsg.Text = "圆TTR：命令行输入半径并回车"; }
                    }
                    else StatusMsg.Text = "圆TTR：请点直线或圆";
                }
                return;
            }

            // 圆弧 SER：依次取起点、端点（半径走命令行）
            if (_serActive && !_serAwaitRadius && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    if (_serStart == null) { _serStart = (wp.Value.x, wp.Value.y); StatusMsg.Text = "圆弧SER：指定端点"; }
                    else { _serEnd = (wp.Value.x, wp.Value.y); _serAwaitRadius = true; StatusMsg.Text = "圆弧SER：命令行输入半径并回车（负值取另一侧）"; }
                }
                return;
            }

            // 绘制工具：左键喂点（与命令行坐标共用 FeedPoint）
            if (_tool != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null) FeedPoint(wp.Value.x, wp.Value.y);
                return;
            }

            // 夹点编辑：空闲态单选，左键按在夹点上 → 开始拖拽该夹点
            if (props.IsLeftButtonPressed && _selected.Count == 1 && _measure == null
                && _editMode == EditMode.None && !_offsetActive && !_trimActive && !_breakActive && !_slideActive)
            {
                var wp = PickWorld();
                if (wp != null)
                {
                    int gi = HitGrip(wp.Value.x, wp.Value.y, SnapTolWorld(_lastPointer));
                    if (gi >= 0)
                    {
                        _gripIndex = gi; _nav = NavMode.None;
                        e.Pointer.Capture(ViewportHost);
                        StatusMsg.Text = "夹点：拖到目标点松开";
                        return;
                    }
                }
            }

            // 窗口框选（仅 2D 空闲态左键）：拖=选择框，不拖=点选（平移改中键）
            if (props.IsLeftButtonPressed && Viewport.Is2DView && _tool == null && _measure == null
                && _editMode == EditMode.None && !_offsetActive && !_trimActive && !_breakActive && !_slideActive)
            {
                _selBoxActive = true; _selBoxStart = _lastPointer; _nav = NavMode.None;
                e.Pointer.Capture(ViewportHost);
                return;
            }

            if (props.IsMiddleButtonPressed)
                _nav = NavMode.Pan;                                       // 中键拖拽 = 平移
            else if (props.IsLeftButtonPressed)
                _nav = NavMode.Orbit;                                     // 3D 左键旋转
            else
                _nav = NavMode.None;                                      // 右键留给上下文菜单
            if (_nav != NavMode.None) e.Pointer.Capture(ViewportHost);
        };
        ViewportHost.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            var w = Viewport.ScreenToWorld(p.X, p.Y);

            // 窗口框选：画选框(交叉=蓝，窗口=绿)
            if (_selBoxActive)
            {
                Viewport.SetSnapMarker(BoxRect(_selBoxStart, p, p.X < _selBoxStart.X));
                _snapShown = true;
                _lastPointer = p;
                return;
            }

            // 滑动多段线：拖动中按像素间距采样
            if (_slideDragging)
            {
                double dpx = p.X - _slideLastScreen.X, dpy = p.Y - _slideLastScreen.Y;
                if (dpx * dpx + dpy * dpy >= 36 && w != null)   // 移动 ≥6px 采一点
                { _slidePts.Add((w.Value.x, w.Value.y)); _slideLastScreen = p; }
                CoordText.Text = w != null ? $"X {w.Value.x:0.00}  Y {w.Value.y:0.00}  [滑动 {_slidePts.Count}]" : "";
                RefreshScene();
                _lastPointer = p;
                return;
            }

            // 对象捕捉：吸附到最近顶点（优先场景几何；显示态导入用其网格顶点）
            _snapWorld = null;
            var snapSrc = _lastImport?.LineVertices ?? _snapVerts;
            _snapHitMode = null;
            if (w != null && SnapToggle.IsChecked == true)
            {
                double tol = SnapTolWorld(p);
                // ① 顶点候选(端点/中点/圆心/象限) —— 高优先精确点
                var vhit = snapSrc.Length > 0 ? SnapPoints.FindNearest(snapSrc, w.Value.x, w.Value.y, tol) : null;
                double dv = vhit != null ? Dist2(vhit.Value, w.Value) : double.MaxValue;
                // ② 扩展模式(交点/最近/垂足) —— 顶点未覆盖, 从场景原语补算; 垂足以上一取点为锚
                ObjectSnap.Hit? ohit = null;
                if (_snapExtraMask != 0)
                {
                    var (segs, circles, arcs, spts) = BuildSnapGeom();
                    ohit = ObjectSnap.Find(segs, circles, arcs, spts, w.Value.x, w.Value.y, tol, _snapExtraMask, _lastInputPoint);
                }
                // ③ 合并: 交点若不比顶点远则取交点; 否则取顶点; 最近/垂足仅在无顶点时兜底
                if (ohit != null && ohit.Value.Mode == ObjectSnap.Mode.Intersection && Dist2((ohit.Value.X, ohit.Value.Y), w.Value) <= dv)
                { _snapWorld = (ohit.Value.X, ohit.Value.Y); _snapHitMode = ObjectSnap.Mode.Intersection; }
                else if (vhit != null) _snapWorld = vhit;
                else if (ohit != null) { _snapWorld = (ohit.Value.X, ohit.Value.Y); _snapHitMode = ohit.Value.Mode; }
                else _snapWorld = null;

                if (_snapWorld != null)
                {
                    Viewport.SetSnapMarker(SnapCross(_snapWorld.Value.x, _snapWorld.Value.y, tol * 0.6));
                    _snapShown = true;
                }
                else if (_snapShown) { Viewport.SetSnapMarker(null); _snapShown = false; }
            }
            else if (_snapShown) { Viewport.SetSnapMarker(null); _snapShown = false; }

            var shown = _snapWorld ?? w;
            if (shown != null && _snapWorld == null)   // osnap 未命中 → 预览点也应用 栅格/正交(与落点 PickWorld 一致)
                shown = ApplyDraftAids(shown.Value.x, shown.Value.y);
            CoordText.Text = shown != null
                ? $"X {shown.Value.x:0.00}  Y {shown.Value.y:0.00}{(_snapWorld != null ? $"  [{SnapModeLabel(_snapHitMode)}]" : (_orthoOn || _snapOn ? "  [辅助]" : ""))}"
                : $"视口 px  X {p.X:0}  Y {p.Y:0}";

            _cursorWorld = shown;

            // 夹点拖拽：实时预览移动后的实体（高亮通道）
            if (_gripIndex >= 0 && _selected.Count == 1 && shown != null)
            {
                var moved = _selected[0].MoveGrip(_gripIndex, shown.Value.x, shown.Value.y);
                if (moved != null)
                {
                    var o = new List<float>();
                    moved.Tessellate(o);
                    foreach (var g in moved.Grips()) AppendGripSquare(o, g.x, g.y, GripSize());
                    Viewport.SetHighlight(o.ToArray());
                }
                _lastPointer = p;
                return;
            }

            if (_tool != null && _nav == NavMode.None) RefreshScene();   // 橡皮筋预览随光标刷新

            if (_nav == NavMode.Pan)
                Viewport.Pan(_lastPointer.X, _lastPointer.Y, p.X, p.Y);
            else if (_nav == NavMode.Orbit)
                Viewport.Orbit((p.X - _lastPointer.X) * 0.01, (p.Y - _lastPointer.Y) * 0.01);
            _lastPointer = p;
        };
        ViewportHost.PointerReleased += (_, e) =>
        {
            var rel = e.GetPosition(ViewportHost);

            // 窗口框选：松开 → 拖动成框则框选，未拖动则点选
            if (_selBoxActive)
            {
                _selBoxActive = false;
                e.Pointer.Capture(null);
                Viewport.SetSnapMarker(null); _snapShown = false;
                if (System.Math.Abs(rel.X - _selBoxStart.X) < 4 && System.Math.Abs(rel.Y - _selBoxStart.Y) < 4)
                    PickAt(rel);                    // 无拖动 → 点选
                else
                    BoxSelect(_selBoxStart, rel);   // 拖动成框 → 框选
                return;
            }

            // 夹点拖拽：松开 → 用移动后的实体替换原实体
            if (_gripIndex >= 0)
            {
                int gi = _gripIndex; _gripIndex = -1;
                e.Pointer.Capture(null);
                var wp = _snapWorld ?? Viewport.ScreenToWorld(rel.X, rel.Y);
                if (wp != null && _selected.Count == 1)
                {
                    var moved = _selected[0].MoveGrip(gi, wp.Value.x, wp.Value.y);
                    if (moved != null)
                    {
                        BeginChange();
                        _scene.Replace(_selected[0], moved);
                        _selected.Clear(); _selected.Add(moved);
                        RefreshScene();
                        HighlightSelection();
                        StatusMsg.Text = "夹点编辑完成";
                    }
                }
                return;
            }

            // 滑动多段线：松开 → 采样点成多段线
            if (_slideDragging)
            {
                _slideDragging = false;
                e.Pointer.Capture(null);
                if (_slidePts.Count >= 2)
                {
                    BeginChange();
                    var pl = new PolylineEntity { Points = new List<(double, double)>(_slidePts) };
                    AssignLayer(pl); _scene.Add(pl);
                    StatusMsg.Text = $"滑动多段线完成（{_slidePts.Count} 点，共 {_scene.Count}）";
                }
                else StatusMsg.Text = "滑动多段线：点数不足，已取消";
                _slidePts.Clear();
                RefreshScene();
                return;
            }

            // 无拖动 + 非绘制/测距 → 视为点选
            bool wasClick = _nav != NavMode.None && _tool == null && _measure == null
                && System.Math.Abs(rel.X - _pressPos.X) < 4 && System.Math.Abs(rel.Y - _pressPos.Y) < 4;
            _nav = NavMode.None;
            e.Pointer.Capture(null);
            if (wasClick) PickAt(rel);
        };
        ViewportHost.PointerWheelChanged += (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            Viewport.ZoomAt(p.X, p.Y, e.Delta.Y > 0 ? 0.9 : 1.1);        // 朝光标缩放
        };
        ViewportHost.DoubleTapped += (_, _) =>
        {
            if (_tool != null && _tool.IsMultiPoint)                      // 双击结束多段线
            {
                var e = _tool.Finish();
                if (e != null) { BeginChange(); AssignLayer(e); _scene.Add(e); }
                RefreshScene();
                StatusMsg.Text = $"多段线完成（已画 {_scene.Count}）";
            }
            else Viewport.ZoomExtents();                                  // 否则 = 范围缩放
        };

        // 对象树选类型 → 视口高亮该类型几何
        ObjectTree.SelectionChanged += OnObjectTreeSelect;

        // ESC：退出当前绘制/测量
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _tool = null;
                _measure = null;
                _angle = null;
                _editMode = EditMode.None;
                _editPts.Clear();
                _offsetActive = false;
                _trimActive = false;
                _breakActive = false; _breakPts.Clear();
                _slideActive = false; _slideDragging = false; _slidePts.Clear();
                _pathActive = false; _pathP1 = null; _kpathMode = false;
                _pasteBaseActive = false;
                _benchActive = false; _benchEntity = null;
                _spotActive = false;
                _textActive = false;
                _dimActive = false; _dimP1 = null;
                _dimRadActive = false; _dimRadCircle = null;
                _gripIndex = -1;
                _selBoxActive = false;
                _ttrActive = false; _ttrAwaitRadius = false; _ttrRef1 = null; _ttrRef2 = null;
                _serActive = false; _serAwaitRadius = false; _serStart = null; _serEnd = null;
                _selected.Clear();
                Viewport.SetSnapMarker(null);
                Viewport.SetHighlight(null);
                _snapShown = false;
                RefreshScene();          // 清除进行中的预览
                StatusMsg.Text = "就绪";
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected();
            }
            else if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
            {
                DoUndo();
            }
            else if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
            {
                DoRedo();
            }
        };
    }

    private enum NavMode { None, Orbit, Pan }
    private NavMode _nav;
    private Avalonia.Point _lastPointer;
    private DxfImportService.ImportResult? _lastImport;
    private MeasureState? _measure;
    private AngleState? _angle;                  // 三点测角(MANG)
    private (double x, double y)? _snapWorld;   // 当前捕捉到的世界点
    private (double x, double y)? _cursorWorld; // 当前光标世界点(橡皮筋预览用)
    private (double x, double y)? _lastInputPoint; // 上一取点(命令行相对坐标 @ 的基点)
    private float[] _snapVerts = System.Array.Empty<float>();   // 场景几何顶点缓存(对象捕捉源: 端点/中点/圆心/象限)
    // 扩展捕捉模式(交点/最近/垂足)——SnapCandidates 未覆盖, 由 ObjectSnap 从场景原语补算。默认仅交点开(最近/垂足按需)。
    private int _snapExtraMask = 1 << (int)ObjectSnap.Mode.Intersection;
    private ObjectSnap.Mode? _snapHitMode;         // 本次捕捉命中的扩展模式(交点/最近/垂足), null=顶点候选或未命中
    private string? _currentPath;                  // 当前 .pmx 文档路径(保存直接回写)
    private double _snapTolPx = 12.0;              // 对象捕捉容差(屏幕像素, 选项可调)
    private bool _gridOn = true;                    // 网格显示状态(选项/GRID 同步)
    private bool _snapShown;                     // 捕捉标记是否已显示
    private bool _slideActive;                   // 滑动多段线：已激活(等待按下)
    private bool _slideDragging;                 // 滑动多段线：正在按住拖动
    private readonly List<(double x, double y)> _slidePts = new();   // 滑动采样点
    private Avalonia.Point _slideLastScreen;     // 上次采样的屏幕点(控制采样密度)
    private readonly Scene _scene = new();       // 托管绘制场景
    private readonly LayerTable _layers = new();  // 图层表
    private readonly UndoManager _undo = new();   // 撤销/重做
    private DrawTool? _tool;                      // 当前激活的绘制工具
    private readonly List<SceneEntity> _selected = new();   // 选择集
    private List<SceneEntity> _prevSelected = new();         // 上次选择集
    private readonly HashSet<string> _hiddenLayers = new();  // 隐藏同一图层对象 记录的层名，结束隐藏一并恢复
    private readonly Cad.Draw.CadClipboard _clip = new();    // 实体剪贴板（COPYCLIP/CUTCLIP/PASTECLIP）
    private readonly Cad.Draw.NamedSelections _selSets = new(); // 命名选择集（创建/调用选择集）
    private readonly AssistantEngine _assistant = new();         // 智能助手（菜单引导，点选即执行命令）
    private Data.GeoDatabase? _geoDb;                            // §四/§八 SQLite 数据基座（懒开，会话内复用）
    private int _selSetCycle = -1;                            // 调用选择集轮转序号
    private Avalonia.Point _pressPos;             // 按下位置（区分点击/拖拽）
    private enum EditMode { None, Move, Copy, Mirror, Rotate, Scale }
    private EditMode _editMode = EditMode.None;
    private readonly List<(double x, double y)> _editPts = new();   // 编辑取的点（基点/目标点/参照…）
    private bool _offsetActive;                    // 偏移：等待点击一侧
    private bool _trimActive;                       // 修剪/延伸：等待点目标线
    private bool _breakActive;                      // 打断：等待取两点
    private readonly List<(double x, double y)> _breakPts = new();   // 打断的两点
    private int _gripIndex = -1;                    // 夹点拖拽中的夹点序号(-1=无)
    private bool _gripsOn = true;                    // 夹点显示开关(GIZMO)
    private bool _pathActive;                       // 点对点寻径：等待取两点
    private (double x, double y)? _pathP1;
    private bool _kpathMode;                         // 备选路径(K 最短路)模式(复用 _pathActive 取两点)
    private bool _benchActive;                      // 分帮扩帮：等待点方向/步距
    private SceneEntity? _benchEntity;
    private int _benchCount = 5;
    private System.Collections.Generic.List<BlockModel.Block>? _lastBlocks;   // 最近导入的块体(资源量用)
    private readonly List<SceneEntity> _blockCellEntities = new();            // 块体配色方块(供筛选/约束/删除 重渲)
    private double _blockGmin, _blockGmax = 1;                                // 块体品位范围(重渲配色一致)

    // 渲染一组块体为品位配色方块：清旧块方块 → 按 _lastBlocks 全域品位范围配色 → 入场景并追踪
    private void RenderBlocks(IReadOnlyList<BlockModel.Block> toShow)
    {
        foreach (var e in _blockCellEntities) _scene.Remove(e);
        _blockCellEntities.Clear();
        var cells = BlockModel.BuildCells(toShow, _blockGmin, _blockGmax);
        foreach (var c in cells) { _scene.Add(c); _blockCellEntities.Add(c); }
    }
    private bool _spotActive;                       // 高程查询：点击报高程
    private System.Collections.Generic.List<(double x, double y, double z)>? _spotTerrain;
    private bool _textActive;                        // 文字：等待命令行输入内容
    private bool _dimActive;                          // 线性标注：取两点
    private (double x, double y)? _dimP1;
    private bool _dimRadActive;                        // 半径标注：选圆/弧后指定方向
    private (double cx, double cy, double r)? _dimRadCircle;
    private (double x, double y)? _lastDimP2;           // 上一条线性标注的第二点(连续标注基准)
    private readonly Cad.Draw.DimStyle _dimStyle = new();   // 标注样式(DIM 变量：字高/小数位/箭头比)，影响新建标注
    private bool _selBoxActive;                     // 窗口框选拖拽中
    private Avalonia.Point _selBoxStart;            // 框选起点(屏幕)
    private bool _ttrActive, _ttrAwaitRadius;       // 圆 TTR：选两相切参照(线/圆) → 输半径
    private SceneEntity? _ttrRef1, _ttrRef2;
    private (double x, double y) _ttrPick1, _ttrPick2;
    private bool _serActive, _serAwaitRadius;       // 圆弧 SER：起点端点 → 输半径
    private (double x, double y)? _serStart, _serEnd;

    // Ribbon 按钮 → 「导入」走真实 DXF 导入；其余暂回显命令（证明整条 UI 已接线）
    private async void OnRibbonCommand(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is string cmd)
        {
            if (_suppressCmdLog) _suppressCmdLog = false; else LogCommand(cmd);   // 命令回显(转派来的已回显, 跳过)
            if (cmd == "新建") { NewScene(); return; }
            if (cmd == "打开") { await OpenSceneAsync(); return; }
            if (cmd == "保存") { await SaveSceneAsync(); return; }
            if (cmd == "撤销") { DoUndo(); return; }
            if (cmd == "重做") { DoRedo(); return; }
            if (cmd == "导入") { await ImportDxfAsync(); return; }
            if (cmd == "导入点") { await ImportPointsAsync(); return; }
            if (cmd == "导入模板" || cmd.StartsWith("导入模板 ") || cmd == "下载模板") { await ExportImportTemplateAsync(cmd); return; }
            if (cmd == "导出分析" || cmd.StartsWith("导出分析 ")) { await ExportAnalysisAsync(cmd); return; }
            if (cmd == "导入生产记录" || cmd == "生产记录导入" || cmd == "导入生产数据") { await ImportProductionRecordsAsync(); return; }
            if (cmd == "导入月度产能" || cmd == "月度产能导入" || cmd == "导入产能") { await ImportCsvToDbAsync("导入月度产能", "equipment_id,year,month,output_m3", rs => Data.GeoDataQueries.ImportCapacityMonthly(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入故障记录" || cmd == "故障记录导入" || cmd == "导入故障") { await ImportCsvToDbAsync("导入故障记录", "equipment_id,date,fault_type[,shift,duration_hours,description,is_resolved,repair_team]", rs => Data.GeoDataQueries.ImportFaultEvents(EnsureGeoDb()!.Connection, rs)); return; }
            if (cmd == "导入KPI" || cmd == "导入月度KPI" || cmd == "KPI导入" || cmd == "导入可用率") { await ImportCsvToDbAsync("导入月度KPI", "equipment_id,year,month,plan_hours,work_hours,fault_hours,availability,actual_run_rate,utilization_rate", rs => Data.GeoDataQueries.ImportKpiMonthly(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入设备台账" || cmd == "设备台账导入" || cmd == "导入设备") { await ImportCsvToDbAsync("导入设备台账", "equipment_id,category[,model,manufacturer,origin,status]", rs => Data.GeoDataQueries.ImportEquipmentLedger(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入煤质" || cmd == "煤质导入" || cmd == "导入煤质化验" || cmd == "煤质数据导入") { await ImportCsvToDbAsync("导入煤质化验", "hole_id,seam_code,depth_from[,ad_raw,std_raw,qgr_d,vdaf_raw,sample_thickness,apparent_density,coal_type,…]", rs => Data.GeoDataQueries.ImportCoalSamples(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入观测点" || cmd == "观测点导入" || cmd == "导入见煤点") { await ImportCsvToDbAsync("导入见煤观测点", "point_id,seam_code,x,y[,seam_thickness,floor_elevation]", rs => Data.GeoDataQueries.ImportObservationPoints(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入月度计划" || cmd == "月度计划导入" || cmd == "导入月计划") { await ImportCsvToDbAsync("导入月度计划", "year,month[,plan_strip_wan_m3,plan_coal_wan_t,ratio_strip_coal,avg_distance_km,avg_height_m]", rs => Data.GeoDataQueries.ImportMonthlyPlans(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入见煤成果" || cmd == "见煤成果导入" || cmd == "导入见煤") { await ImportCsvToDbAsync("导入见煤成果", "hole_id,seam_code[,floor_elevation,adopted_thickness,drill_seam_thickness,status]", rs => Data.GeoDataQueries.ImportSeamResults(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入路况" || cmd == "路况导入" || cmd == "导入运输道路") { await ImportCsvToDbAsync("导入运输道路", "road_id,name,road_type(main/branch/dump/temp),length_m[,max_slope_pct,avg_slope_pct,road_width_m]", rs => Data.GeoDataQueries.ImportHaulRoads(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入边坡" || cmd == "边坡导入" || cmd == "导入边坡设计") { await ImportCsvToDbAsync("导入边坡设计", "side_name,side_type(working/final/transition)[,working_slope_angle_deg,final_slope_angle_deg,max_depth_m,safety_factor]", rs => Data.GeoDataQueries.ImportSlopeDesigns(EnsureGeoDb()!.Connection, rs)); return; }
            if (cmd == "展绘钻孔" || cmd == "钻孔柱状图" || cmd == "导入钻孔数据" || cmd == "原始钻孔柱状图") { await ImportBoreholesAsync(); return; }
            if (cmd == "煤厚分析" || cmd == "煤层厚度分析" || cmd == "煤厚") { await CoalThicknessAsync(); return; }
            if (cmd == "等高线" || cmd == "等高线生产" || cmd == "等值线") { await ContourFromCsvAsync(); return; }
            if (cmd == "创建三角网" || cmd == "三角网" || cmd == "2.5D TIN" || cmd == "2.5DTIN") { await CreateTinAsync(); return; }
            if (cmd == "约束三角网" || cmd == "约束Delaunay" || cmd == "约束剖分" || cmd == "breakline三角网") { await CreateConstrainedTinAsync(); return; }
            if (cmd == "示例三角网" || cmd == "三角网示例") { GenerateSampleTrimesh(); return; }
            if (cmd == "坡度着色" || cmd == "三角网着色" || cmd == "坡度") { await ShadeTinAsync("坡度着色", "绿=平 → 红=陡", TerrainAnalysis.BuildSlopeMap); return; }
            if (cmd == "坡向着色" || cmd == "坡向") { await ShadeTinAsync("坡向着色", "按朝向 HSV 配色", TerrainAnalysis.BuildAspectMap); return; }
            if (cmd == "高程着色" || cmd == "分色显示" || cmd == "高程分带") { await ShadeTinAsync("高程着色", "低绿→中黄→高棕", TerrainAnalysis.BuildElevationMap); return; }
            if (cmd == "体积计算" || cmd == "算量" || cmd == "土方量") { await VolumeAsync(); return; }
            if (cmd == "两期点云算量" || cmd == "两期算量" || cmd == "两期土方") { await TwoEpochVolumeAsync(); return; }
            if (cmd == "圈范围算量") { await BoundaryVolumeAsync(); return; }
            if (cmd == "提取道路中心线" || cmd == "道路中线" || cmd == "提取道路中线") { ExtractCenterline(); return; }
            if (cmd == "点对点寻径" || cmd == "寻径" || cmd == "点对点寻路") { StartPathfind(); return; }
            if (cmd == "备选路径" || cmd == "K最短路" || cmd == "备用路径") { StartKPathfind(); return; }
            if (cmd == "路网校验" || cmd == "连通性诊断" || cmd == "路网体检") { ValidateRoadNetwork(); return; }
            if (cmd == "基础道路网络构建" || cmd == "路网构建" || cmd == "路网预览" || cmd == "构建路网" || cmd == "路网更新") { BuildRoadNetworkCmd(); return; }
            if (cmd == "路网存档" || cmd == "路网导出") { await SnapshotEpochAsync(); return; }
            if (cmd == "演化对比" || cmd == "路网演化" || cmd == "两期路网对比") { await EvolutionCompareAsync(); return; }
            if (cmd == "时段快照" || cmd == "路网快照" || cmd == "纪元快照") { await SnapshotEpochAsync(); return; }
            if (cmd == "排土条带" || cmd == "条带填充" || cmd == "排土条带划分") { DumpStrips(); return; }
            if (cmd == "分帮扩帮" || cmd == "批量台阶扩帮" || cmd == "批量扩坑") { StartBench(); return; }
            if (cmd == "组合工作线" || cmd == "合并多段线" || cmd == "连接台阶线" || cmd == "连接多段线") { JoinPolylines(); return; }
            if (cmd == "块体模型" || cmd == "导入块体" || cmd == "地质体建模") { await ImportBlockModelAsync(); return; }
            if (cmd == "资源量估算" || cmd == "剥采比" || cmd == "资源量") { ResourceReport(null); return; }
            if (cmd == "导出块体" || cmd == "块体导出") { await ExportBlocksAsync(); return; }
            if (cmd == "输出报告" || cmd == "资源量报告" || cmd == "块体报告") { await ExportResourceReportAsync(); return; }
            if (cmd == "块体着色" || cmd == "块体配色") { ColorBlocksCmd(); return; }
            if (cmd == "筛选块体" || cmd == "块体筛选") { FilterBlocksCmd(); return; }
            if (cmd == "约束块体" || cmd == "块体约束") { ConstrainBlocksCmd(); return; }
            if (cmd == "删除块体" || cmd == "清除块体") { DeleteBlocksCmd(); return; }
            if (cmd == "切面剖切" || cmd == "块体剖切" || cmd == "切面") { SectionBlocksCmd(); return; }
            if (cmd == "克里金估值" || cmd == "OK估值" || cmd == "克里金") { await EstimateGradeAsync(kriging: true); return; }
            if (cmd == "快速估值" || cmd == "品位估值" || cmd == "IDW估值" || cmd == "空间分布") { await EstimateGradeAsync(kriging: false); return; }
            if (cmd == "设备信息管理" || cmd == "设备台账" || cmd == "设备台账管理" || cmd == "设备信息") { EquipmentRosterCmd(); return; }
            if (cmd == "设备生产数据" || cmd == "生产数据" || cmd == "设备数据分析") { ProductionStatsCmd(); return; }
            if (cmd == "产能分析" || cmd == "设备能力" || cmd == "能力分析" || cmd == "产能") { CapacityRankingCmd(); return; }
            if (cmd == "故障分析" || cmd == "设备状态·故障报修" || cmd == "故障报修" || cmd == "设备状态") { FaultStatsCmd(); return; }
            if (cmd == "KPI分析" || cmd == "KPI" || cmd == "设备KPI") { KpiStatsCmd(); return; }
            if (cmd == "钻孔管理" || cmd == "钻孔统计" || cmd == "钻孔信息") { BoreholeStatsCmd(); return; }
            if (cmd == "煤质统计" || cmd == "煤质数据管理" || cmd == "煤质分析" || cmd == "质量·配煤分析" || cmd == "配煤分析") { CoalQualityStatsCmd(); return; }
            if (cmd == "商品煤符合性" || cmd == "煤质达标" || cmd == "商品煤达标" || cmd.StartsWith("商品煤符合性 ") || cmd.StartsWith("煤质达标 ")) { CoalComplianceCmd(cmd); return; }
            if (cmd == "导出符合性" || cmd == "符合性导出" || cmd == "导出超标段") { await ExportComplianceAsync(cmd); return; }
            if (cmd == "品位储量曲线" || cmd == "品位-储量曲线" || cmd == "灰分储量曲线" || cmd.StartsWith("品位储量曲线 ")) { GradeTonnageCmd(cmd); return; }
            if (cmd == "分标高煤质" || cmd == "标高煤质" || cmd.StartsWith("分标高煤质 ")) { CoalByElevationCmd(cmd); return; }
            if (cmd == "煤质离群" || cmd == "离群质检" || cmd == "煤质异常" || cmd.StartsWith("煤质离群 ")) { CoalOutlierCmd(cmd); return; }
            if (cmd == "导出离群" || cmd == "离群导出" || cmd.StartsWith("导出离群 ")) { await ExportOutliersAsync(cmd); return; }
            if (cmd == "导出品位储量" || cmd == "品位储量导出" || cmd.StartsWith("导出品位储量 ")) { await ExportGradeTonnageAsync(cmd); return; }
            if (cmd == "导出分标高" || cmd == "分标高导出" || cmd.StartsWith("导出分标高 ")) { await ExportElevationAsync(cmd); return; }
            if (cmd == "导出洗选" || cmd == "洗选导出") { await ExportWashingAsync(); return; }
            if (cmd == "导出用途" || cmd == "用途导出") { await ExportUtilizationAsync(); return; }
            if (cmd == "导出编组" || cmd == "编组导出" || cmd.StartsWith("导出编组 ")) { await ExportFleetOptAsync(cmd); return; }
            if (cmd == "导出预测" || cmd == "预测导出") { await ExportForecastAsync(); return; }
            if (cmd == "洗选提质" || cmd == "洗选分析" || cmd == "降灰脱硫") { CoalWashingCmd(); return; }
            if (cmd == "用途适宜性" || cmd == "煤炭用途" || cmd == "动力炼焦评价") { CoalUtilizationCmd(); return; }
            if (cmd == "煤层管理" || cmd == "煤层定义" || cmd == "煤层列表") { CoalSeamsCmd(); return; }
            if (cmd == "见煤统计" || cmd == "煤层对比" || cmd == "见煤对比" || cmd == "钻孔见煤") { SeamIntersectionsCmd(); return; }
            if (cmd == "分煤层煤质" || cmd == "煤层煤质" || cmd == "分层煤质") { CoalQualityBySeamCmd(); return; }
            if (cmd == "年度产量" || cmd == "产量趋势" || cmd == "年度产量趋势" || cmd == "年产量") { AnnualOutputCmd(); return; }
            if (cmd == "设备故障排名" || cmd == "故障排名" || cmd == "检修排名") { FaultRankCmd(); return; }
            if (cmd == "班次产量对比" || cmd == "班次产量" || cmd == "班产对比") { ShiftOutputCmd(); return; }
            if (cmd == "KPI趋势" || cmd == "设备KPI趋势" || cmd == "kpi趋势") { KpiTrendCmd(); return; }
            if (cmd == "产能分类对比" || cmd == "产能分类" || cmd == "分类产能") { CapacityByCategoryCmd(); return; }
            if (cmd == "故障类型分布" || cmd == "故障类型" || cmd == "故障构成") { FaultByTypeCmd(); return; }
            if (cmd == "分工序验收" || cmd == "分工序验收合格率" || cmd == "工序验收") { AcceptanceByPhaseCmd(); return; }
            if (cmd == "设备智能编组" || cmd == "调度规则" || cmd == "配车规则" || cmd == "铲车配比") { DispatchRulesCmd(); return; }
            if (cmd == "编组优化" || cmd == "智能编组优化" || cmd == "设备编组优化" || cmd.StartsWith("编组优化 ")) { FleetOptimizeCmd(cmd); return; }
            if (cmd == "工艺架构定义" || cmd == "工艺架构" || cmd == "平盘工艺地图" || cmd == "工艺系统") { ProcessArchitectureCmd(); return; }
            if (cmd == "现场验收录入" || cmd == "现场验收" || cmd == "参数验收") { AcceptanceStatsCmd(); return; }
            if (cmd == "作业面台账" || cmd == "作业面" || cmd == "工作面台账" || cmd == "采场参数") { WorkingFacesCmd(); return; }
            if (cmd == "参数模板库" || cmd == "参数化模板" || cmd == "参数模板" || cmd == "参数定义") { ParamTemplatesCmd(); return; }
            if (cmd == "月度计划" || cmd == "月计划" || cmd == "月度计划查看") { MonthlyPlansCmd(); return; }   // 只读展示(编制/授权工作流走 TaskLib, 受阻)
            if (cmd == "路况显示" || cmd == "运输道路" || cmd == "道路台账") { HaulRoadsCmd(); return; }
            if (cmd == "边坡设计" || cmd == "边坡参数" || cmd == "帮坡角设计") { SlopeDesignsCmd(); return; }
            if (cmd == "展绘钻孔" || cmd == "开孔坐标管理" || cmd == "钻孔展绘" || cmd == "开孔坐标") { DrawBoreholesCmd(); return; }
            if (cmd == "展绘层位数据" || cmd == "层位展点" || cmd == "展绘层位") { HorizonPointsCmd(); return; }
            if (cmd == "层位求交" || cmd == "顶底板求交" || cmd == "煤层高程" || cmd.StartsWith("层位求交 ") || cmd.StartsWith("顶底板求交 ") || cmd.StartsWith("煤层高程 ")) { SeamIntersectCmd(cmd); return; }
            if (cmd == "机群总览" || cmd == "设备总览" || cmd == "机群") { FleetOverviewCmd(); return; }
            if (cmd == "数据看板" || cmd == "看板" || cmd == "调度态势看板" || cmd == "态势看板") { DataBoardCmd(); return; }
            if (cmd == "煤种分类" || cmd == "煤类分类" || cmd == "煤炭分类") { CoalClassificationCmd(); return; }
            if (cmd == "煤层台阶参数" || cmd == "台阶参数" || cmd == "煤层参数") { SeamBenchParamsCmd(); return; }
            if (cmd == "设备约束条件" || cmd == "设备约束" || cmd == "能力约束") { EquipmentConstraintsCmd(); return; }
            if (cmd == "煤质分级" || cmd == "煤质分级规则" || cmd == "分级规则") { CoalGradeRulesCmd(); return; }
            if (cmd == "展绘观测点" || cmd == "煤层观测点" || cmd == "观测点" || cmd == "露头观测点") { DrawObservationPointsCmd(); return; }
            if (cmd == "采区列表" || cmd == "采区管理" || cmd == "矿区位置" || cmd == "采场位置") { MineLocationsCmd(); return; }
            if (cmd == "设备效能预测" || cmd == "效能预测" || cmd == "班次效能预测" || cmd == "产能预测") { EfficiencyForecastCmd(); return; }
            if (cmd == "产量预测" || cmd == "产量趋势预测" || cmd == "时序预测") { OutputForecastCmd(false); return; }
            if (cmd == "Holt预测" || cmd == "产量预测Holt") { OutputForecastCmd(true); return; }
            if (cmd == "数据导入导出" || cmd == "数据导出" || cmd == "导出数据库" || cmd == "地质数据导出") { await ExportGeoDataAsync(); return; }
            if (cmd == "点云抽稀" || cmd == "抽稀" || cmd == "点云精简") { await ThinPointsAsync(); return; }
            if (cmd == "地面点滤波" || cmd == "地面滤波") { await GroundFilterAsync(); return; }
            if (cmd == "C2C" || cmd == "点云比对" || cmd == "位移监测 C2C" || cmd == "位移监测") { await CloudCompareAsync(); return; }
            if (cmd == "画道路中线" || cmd == "手动标定线路" || cmd == "道路中线绘制") { ActivateDrawTool("多段线"); StatusMsg.Text = "画道路中线：绘制折线作道路中线（供路网/寻径/演化对比）"; return; }
            if (cmd == "境界圈定" || cmd == "凸包" || cmd == "采场圈定" || cmd == "采场/排土场圈定") { await BoundaryHullAsync(); return; }
            if (cmd == "确定境界" || cmd == "境界优化" || cmd == "最优坑深" || cmd == "经济境界") { PitDepthCmd(); return; }
            if (cmd == "采区划分" || cmd == "采区" || cmd == "储量均衡划分") { PanelSplitCmd(); return; }
            if (cmd == "规划计算" || cmd == "开采程序评价" || cmd == "程序评价") { ProgramEvaluateCmd(); return; }
            if (cmd == "派生计划方案" || cmd == "派生方案" || cmd == "多方案派生") { DerivePlansCmd(); return; }
            if (cmd == "剖面分析" || cmd == "剖面" || cmd == "点云剖面") { await SectionProfileAsync(); return; }
            if (cmd == "粗糙度" || cmd == "地表粗糙度") { await RoughnessAsync(); return; }
            if (cmd == "曲率" || cmd == "地表曲率") { await CurvatureAsync(); return; }
            if (cmd == "面积" || cmd == "面积测量" || cmd == "周长") { MeasureArea(); return; }
            if (cmd == "距离" || cmd == "测量距离" || cmd == "测距") { _measure = new MeasureState(); _tool = null; StatusMsg.Text = "测距：点第一点"; return; }
            if (cmd == "角度" || cmd == "测量角度" || cmd == "三点测角") { _angle = new AngleState(); _tool = null; _measure = null; StatusMsg.Text = "测角：点顶点"; return; }
            if (cmd == "等效运距" || cmd == "运输指标" || cmd == "运输指标报表" || cmd == "驱动距离") { await HaulMetricsAsync(); return; }
            if (cmd == "批量台阶扩帮" || cmd == "台阶线生成" || cmd == "台阶扩帮"
                || cmd.StartsWith("批量台阶扩帮 ") || cmd.StartsWith("台阶线生成 ") || cmd.StartsWith("台阶扩帮 "))
            {
                // 可选 "台阶扩帮 <帮宽W> <台阶高H> <坡面角α>" → 真实台阶距 W+H/tanα；缺省用境界短边/10
                double? benchD = null;
                var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length >= 4 && double.TryParse(tk[1], out double bw) && double.TryParse(tk[2], out double bh) && double.TryParse(tk[3], out double ba))
                    benchD = Cad.BenchLines.BenchDistance(bw, bh, ba);
                GenerateBenchLines(benchD);
                return;
            }
            if (cmd == "剥采比均衡" || cmd == "VP曲线" || cmd == "剥采比") { await StrippingBalanceAsync(); return; }
            if (cmd == "工作面线拟合" || cmd == "工作面线" || cmd == "拟合工作面线") { await WorkingFaceLineAsync(); return; }
            if (cmd == "质量统计" || cmd == "统计分析" || cmd == "煤质CSV统计" || cmd == "样本统计") { await QualityStatsAsync(); return; }   // 用户 CSV 统计(区别于 §四 库煤质统计)
            if (cmd == "坡角估算" || cmd == "工作帮坡角" || cmd == "坡角") { await SlopeEstimateAsync(); return; }
            if (cmd == "台阶参数分析" || cmd == "台阶分析" || cmd == "台阶参数" || cmd == "工艺参数分析") { await BenchAnalyzeAsync(); return; }
            if (cmd == "达成分析" || cmd == "产量达成" || cmd == "达成率" || cmd == "达成度评价" || cmd == "产量统计") { await AttainmentAsync(); return; }
            if (cmd == "车铲匹配" || cmd == "配车匹配" || cmd == "车铲配比") { await FleetMatchAsync(); return; }
            if (cmd == "点云质量统计" || cmd == "点云统计" || cmd == "点云质量") { await PointCloudStatsAsync(); return; }
            if (cmd == "点云高程着色" || cmd == "高程着色" || cmd == "点云着色") { await ElevationColorAsync(); return; }
            if (cmd == "加载点云" || cmd == "展点" || cmd == "导入点云" || cmd == "加载点") { await LoadPointCloudAsync(); return; }
            if (cmd == "逐点坡度/坡向" || cmd == "逐点坡度坡向" || cmd == "法向估计" || cmd == "点云法向") { await PointNormalsAsync(); return; }
            if (cmd == "高程截断" || cmd == "高程裁剪" || cmd == "Z截断") { await ElevationClipAsync(); return; }
            if (cmd == "点云裁剪" || cmd == "边界裁剪点云" || cmd == "裁剪点云") { await CropCloudByBoundaryAsync(); return; }
            if (cmd == "网格度量" || cmd == "网格面积体积" || cmd == "网格体积") { await MeshMetricsAsync(); return; }
            if (cmd == "网格诊断" || cmd == "网格检查" || cmd == "网格拓扑") { await MeshDiagnoseAsync(); return; }
            if (cmd == "网格焊接" || cmd == "合并顶点" || cmd == "顶点焊接") { await MeshWeldAsync(); return; }
            if (cmd == "合并三角网" || cmd == "网格合并" || cmd == "合并网格") { await MeshMergeAsync(); return; }
            if (cmd == "补洞(三角网)" || cmd == "补洞" || cmd == "网格补洞" || cmd == "填洞") { await MeshHoleFillAsync(); return; }
            if (cmd == "分割三角网" || cmd == "沿线分割三角网" || cmd == "网格分割" || cmd == "切分三角网") { await MeshSplitAsync(); return; }
            if (cmd == "快速建模" || cmd == "一键建模" || cmd == "顶底成体") { await QuickModelAsync(); return; }
            if (cmd == "中心线管理" || cmd == "边状态" || cmd == "路网拓扑" || cmd == "中线管理") { RoadNetworkReportCmd(); return; }
            if (cmd == "排土场容量校核" || cmd == "容量校核" || cmd == "排土容量") { await DumpCapacityAsync(); return; }
            if (cmd == "生产量核算" || cmd == "任务量汇总" || cmd == "分账合计" || cmd == "生产任务量") { await ProductionQuantityAsync(); return; }
            if (cmd == "采剥平衡" || cmd == "采剥平衡分析" || cmd == "剥采平衡" || cmd == "物料平衡") { await StripBalanceAsync(); return; }
            if (cmd == "配煤核算" || cmd == "配煤" || cmd == "煤质混合" || cmd == "配煤计算") { await CoalBlendAsync(); return; }
            if (cmd == "工序进度跟踪" || cmd == "工序进度" || cmd == "进度跟踪") { await ProcessProgressAsync(); return; }
            if (cmd == "环节降效" || cmd == "天气降效" || cmd.StartsWith("环节降效 ") || cmd.StartsWith("天气降效 "))
            {
                var t = cmd.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                double dl = 10, dh = 20, dd = 15;
                if (t.Length >= 2) double.TryParse(t[1], out dl);
                if (t.Length >= 3) double.TryParse(t[2], out dh);
                if (t.Length >= 4) double.TryParse(t[3], out dd);
                LinkDerateCmd(dl, dh, dd);
                return;
            }
            if (cmd == "编组产能" || cmd == "车铲循环" || cmd.StartsWith("编组产能 ") || cmd.StartsWith("车铲循环 "))
            {
                var t = cmd.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                double bm = 12, pl = 100, rho = 2.5, ks = 1.5, km = 3; int nt = 0;
                if (t.Length >= 2) double.TryParse(t[1], out bm);
                if (t.Length >= 3) double.TryParse(t[2], out pl);
                if (t.Length >= 4) double.TryParse(t[3], out rho);
                if (t.Length >= 5) double.TryParse(t[4], out ks);
                if (t.Length >= 6) double.TryParse(t[5], out km);
                if (t.Length >= 7) int.TryParse(t[6], out nt);
                FleetCycleCmd(bm, pl, rho, ks, km, nt);
                return;
            }
            if (cmd == "排土场按量推进" || cmd == "排土按量推进" || cmd.StartsWith("排土场按量推进 ") || cmd.StartsWith("排土按量推进 "))
            {
                var tok = cmd.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                double vol = 60000, wl = 300, bh = 20;
                if (tok.Length >= 2) double.TryParse(tok[1], out vol);
                if (tok.Length >= 3) double.TryParse(tok[2], out wl);
                if (tok.Length >= 4) double.TryParse(tok[3], out bh);
                DumpAdvanceByVolumeCmd(vol, wl, bh);
                return;
            }
            if (cmd == "物料换算" || cmd == "煤岩换算" || cmd.StartsWith("物料换算 ") || cmd.StartsWith("煤岩换算 "))
            {
                var tok = cmd.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                string mixText = tok.Length >= 2 ? tok[1] : "煤7:岩3";
                double vol = 1000; if (tok.Length >= 3) double.TryParse(tok[2], out vol);
                MaterialConvertCmd(mixText, vol);
                return;
            }
            if (cmd == "固化成体" || cmd == "固化实体") { await SolidifyAsync(); return; }
            if (cmd == "体素格网体积" || cmd == "体素体积" || cmd == "体素算量") { await VoxelVolumeAsync(); return; }
            if (cmd == "实体转块体" || cmd == "网格转块体" || cmd == "体转块") { await EntityToBlocksAsync(); return; }
            if (cmd == "立方体" || cmd == "长方体") { await BoxPrimitiveAsync(); return; }
            if (cmd == "球体" || cmd == "球") { await SpherePrimitiveAsync(); return; }
            if (cmd == "圆柱" || cmd == "圆柱体") { await CylinderPrimitiveAsync(); return; }
            if (cmd == "网格边界" || cmd == "边界环提取" || cmd == "提取边界") { await MeshBoundaryAsync(); return; }
            if (cmd == "SOR去噪" || cmd == "统计去噪" || cmd == "SOR") { await DenoiseAsync(false); return; }
            if (cmd == "ROR去噪" || cmd == "半径去噪" || cmd == "ROR") { await DenoiseAsync(true); return; }
            if (cmd == "矿床识别" || cmd == "自动识别" || cmd == "矿床类型识别") { await DepositDetectAsync(); return; }
            if (cmd == "方案综合对比" || cmd == "方案比选" || cmd == "方案对比") { await ProgramCompareAsync(); return; }
            if (cmd == "高程查询" || cmd == "虚拟钻孔" || cmd == "查询高程") { await StartSpotQueryAsync(); return; }
            if (cmd == "文字" || cmd == "单行文字") { ArmText(); return; }
            if (cmd == "标注" || cmd == "线性标注" || cmd == "对齐标注" || cmd == "尺寸标注" || cmd == "标注台阶标高") { StartDim(); return; }
            if (cmd == "半径标注" || cmd == "半径") { StartDimRadial(); return; }
            if (cmd == "连续标注" || cmd == "连续") { StartDimContinue(); return; }
            if (cmd == "标注样式" || cmd == "标注设置" || cmd.StartsWith("标注样式 ") || cmd.StartsWith("标注设置 ")) { DimStyleCmd(cmd); return; }
            if (cmd == "裁剪" || cmd == "多边形裁剪" || cmd == "区运算" || cmd == "范围裁剪") { ClipPolygon(); return; }
            if (cmd == "平滑" || cmd == "光滑" || cmd == "曲线平滑" || cmd == "光滑曲线") { SmoothPolyline(); return; }
            if (cmd == "简化" || cmd == "多段线简化" || cmd == "抽稀线" || cmd == "抽稀等值线") { SimplifyPolyline(); return; }
            if (cmd == "圈选" || cmd == "窗口圈选") { PolygonSelect(false); return; }
            if (cmd == "交叉圈选") { PolygonSelect(true); return; }
            if (cmd == "坐标转换") { await CoordTransformAsync(); return; }
            if (cmd == "另存为") { await SaveAsAsync(); return; }
            if (cmd == "工具") { OpenNodeEditor(); return; }
            if (cmd == "2D") { Viewport.SetViewMode(true); StatusMsg.Text = "视图: 2D 平面（正交俯视）"; return; }
            if (cmd == "3D") { Viewport.SetViewMode(false); StatusMsg.Text = "视图: 3D 轨道"; return; }
            if (cmd == "俯视" || cmd == "顶视") { Viewport.SetView("top"); StatusMsg.Text = "视图: 俯视"; return; }
            if (cmd == "仰视") { Viewport.SetView("bottom"); StatusMsg.Text = "视图: 仰视"; return; }
            if (cmd == "主视" || cmd == "前视") { Viewport.SetView("front"); StatusMsg.Text = "视图: 主视"; return; }
            if (cmd == "后视") { Viewport.SetView("back"); StatusMsg.Text = "视图: 后视"; return; }
            if (cmd == "左视") { Viewport.SetView("left"); StatusMsg.Text = "视图: 左视"; return; }
            if (cmd == "右视") { Viewport.SetView("right"); StatusMsg.Text = "视图: 右视"; return; }
            if (cmd == "西南等轴测" || cmd == "西南轴测") { Viewport.SetView("sw"); StatusMsg.Text = "视图: 西南等轴测"; return; }
            if (cmd == "东南等轴测" || cmd == "东南轴测") { Viewport.SetView("se"); StatusMsg.Text = "视图: 东南等轴测"; return; }
            if (cmd == "东北等轴测" || cmd == "东北轴测") { Viewport.SetView("ne"); StatusMsg.Text = "视图: 东北等轴测"; return; }
            if (cmd == "西北等轴测" || cmd == "西北轴测") { Viewport.SetView("nw"); StatusMsg.Text = "视图: 西北等轴测"; return; }
            if (cmd == "范围缩放" || cmd == "全部缩放" || cmd == "范围") { Viewport.ZoomExtents(); StatusMsg.Text = "视图: 范围缩放"; return; }
            if (cmd == "上一视图" || cmd == "返回视图") { StatusMsg.Text = Viewport.PrevView() ? "视图: 已返回上一视图" : "视图: 无更早视图"; return; }
            if (cmd == "清空视图") { _selected.Clear(); Viewport.SetHighlight(null); Viewport.SetSnapMarker(null); _snapShown = false; RefreshScene(); StatusMsg.Text = "已清空选择/高亮/捕捉标记"; return; }
            if (cmd == "隐藏对象" || cmd == "隐藏") { HideSelectedObjects(); return; }
            if (cmd == "隐藏同一图层对象" || cmd == "隐藏图层" || cmd == "隐藏同层") { HideSelectedLayers(); return; }
            if (cmd == "结束隐藏" || cmd == "取消隐藏" || cmd == "显示全部" || cmd == "全部显示") { EndHide(); return; }
            if (cmd == "帮助文档") { ShowHelp(); return; }
            if (cmd == "选项") { ShowOptions(); return; }
            if (cmd == "注册") { StatusMsg.Text = "注册/授权：需接入国产数据库(达梦)授权系统（记录待做）"; return; }
            if (cmd == "删除") { DeleteSelected(); return; }
            if (cmd == "全部选择") { SelectAll(); return; }
            if (cmd == "快速选择" || cmd == "选择类似") { SelectSimilar(); return; }
            if (cmd == "最后") { SelectLast(); return; }
            if (cmd == "上次") { SelectPrevious(); return; }
            if (cmd == "取消选择" || cmd == "全部取消选择" || cmd == "清除选择") { DeselectAll(); return; }
            if (cmd == "分解") { ExplodeSelected(); return; }
            if (cmd == "加密多段线" || cmd == "加密") { DensifySelectedPolylines(); return; }
            if (cmd == "两线交点" || cmd == "求交点" || cmd == "线交点") { IntersectSelectedPolylines(); return; }
            if (cmd == "闭合多段线" || cmd == "闭合线") { CloseSelectedPolylines(); return; }
            if (cmd == "删除重复点" || cmd == "去重复点" || cmd == "点去重") { DedupeSelectedPoints(); return; }
            if (cmd == "删除重复线" || cmd == "去重复线" || cmd == "线去重") { DedupeSelectedPolylines(); return; }
            if (cmd == "区域求差" || cmd == "可采区域求差" || cmd == "多边形求差") { SubtractRegions(); return; }
            if (cmd == "区域重叠检测" || cmd == "区域重叠" || cmd == "重叠检测") { CheckRegionOverlap(); return; }
            if (cmd == "平盘宽度识别" || cmd == "现场参数提取" || cmd == "平盘识别" || cmd == "采场参数识别") { await BenchWidthAsync(); return; }
            if (cmd == "确定可采区域" || cmd == "可采区域" || cmd == "可采区域识别") { await MineableAreaAsync(); return; }
            if (cmd == "点落到面上" || cmd == "点落面" || cmd == "点投影到面") { await ProjectPointsToMeshAsync(); return; }
            if (cmd == "线落到面上" || cmd == "线落面" || cmd == "线投影到面") { await ProjectPolylinesToMeshAsync(); return; }
            if (cmd == "侧面三角网" || cmd == "侧面放样" || cmd == "放样侧面") { await SideSurfaceAsync(); return; }
            if (cmd == "道路横断面" || cmd == "路面加宽超高" || cmd == "弯道加宽") { RoadCrossSectionCmd(); return; }
            if (cmd == "平行推进" || cmd == "开采程序确定" || cmd == "工作线推进") { AdvanceCmd(AdvanceMode.Parallel, "平行推进"); return; }
            if (cmd == "定点回转" || cmd == "定点回转推进") { AdvanceCmd(AdvanceMode.FixedPivot, "定点回转"); return; }
            if (cmd == "动点回转" || cmd == "动点回转推进") { AdvanceCmd(AdvanceMode.MovingPivot, "动点回转"); return; }
            if (cmd == "螺旋斜坡道" || cmd == "螺旋坑线" || cmd == "螺旋中线") { SpiralRampCmd(); return; }
            if (cmd == "折返斜坡道" || cmd == "折返坑线" || cmd == "折返中线") { SwitchbackRampCmd(); return; }
            if (cmd == "运距指标" || cmd == "循环时间" || cmd == "运距统计") { await HaulRecordMetricsAsync(); return; }
            if (cmd == "OD运距矩阵" || cmd == "OD矩阵" || cmd == "运距矩阵") { await OdMatrixAsync(); return; }
            if (cmd == "新建图层") { var l = _layers.New(); PopulateDrawingLayers(); StatusMsg.Text = $"新建图层「{l.Name}」并置为当前"; return; }
            if (cmd == "删除图层" || cmd == "删层" || cmd == "删除当前图层") { DeleteCurrentLayer(); return; }
            if (cmd == "图层特性管理器") { var l = _layers.CycleCurrent(); StatusMsg.Text = $"当前图层「{l.Name}」 显示{( l.Shown?"开":"关")}/{(l.Locked?"锁":"解锁")}（再点循环切换）"; return; }
            if (cmd == "全开" || cmd == "全部打开" || cmd == "图层全开") { _layers.AllOn(); PopulateDrawingLayers(); AfterLayerStateChange(); StatusMsg.Text = "已打开全部图层"; return; }
            if (cmd == "冻结") { FreezeCurrentLayer(true); return; }
            if (cmd == "解冻") { FreezeCurrentLayer(false); return; }
            if (cmd == "锁定") { LockCurrentLayer(true); return; }
            if (cmd == "解锁") { LockCurrentLayer(false); return; }
            if (cmd == "图层全开") { LayersAllOn(); return; }
            if (cmd == "移动") { StartEdit(EditMode.Move, "移动"); return; }
            if (cmd == "复制") { StartEdit(EditMode.Copy, "复制"); return; }
            if (cmd == "镜像") { StartEdit(EditMode.Mirror, "镜像"); return; }
            if (cmd == "旋转") { StartEdit(EditMode.Rotate, "旋转"); return; }
            if (cmd == "缩放") { StartEdit(EditMode.Scale, "缩放"); return; }
            if (cmd == "偏移") { StartOffset(); return; }
            if (cmd == "复制到剪贴板" || cmd == "剪贴板复制") { CopyClip(); return; }
            if (cmd == "剪切") { CutClip(); return; }
            if (cmd == "粘贴" || cmd == "原坐标粘贴") { PasteClip(); return; }
            if (cmd == "基点粘贴") { StartPasteBase(); return; }
            if (cmd == "删除全部" || cmd == "全部删除" || cmd == "清空实体" || cmd == "清除全部" || cmd == "清除点云") { EraseAll(); return; }
            if (cmd == "创建选择集" || cmd == "选择集") { CreateSelSet(); return; }
            if (cmd == "调用选择集") { RecallSelSet(); return; }
            if (cmd == "刷新") { Regen(); return; }
            if (cmd == "特性" || cmd == "属性" || cmd.StartsWith("特性 ") || cmd.StartsWith("属性 ")) { PropertiesCmd(cmd); return; }
            if (cmd == "清理标记" || cmd == "清除标记") { ClrMark(); return; }
            if (cmd == "修剪" || cmd == "延伸") { StartTrim(); return; }
            if (cmd == "圆TTR" || cmd == "圆(切切半径)") { StartTTR(); return; }
            if (cmd == "圆弧SER" || cmd == "圆弧(起点端点半径)") { StartArcSer(); return; }
            if (cmd == "打断") { StartBreak(); return; }
            if (cmd == "夹点开关" || cmd == "夹点") { ToggleGizmo(); return; }
            if (cmd == "正交" || cmd == "正交开关") { _orthoOn = !_orthoOn; SyncDraftToggles(); StatusMsg.Text = _orthoOn ? "正交: 开" : "正交: 关"; return; }
            if (cmd == "栅格" || cmd == "栅格显示" || cmd == "显示栅格" || cmd == "GRID") { SetGrid(!_gridOn); StatusMsg.Text = _gridOn ? "栅格: 开" : "栅格: 关"; return; }
            if (cmd == "栅格捕捉" || cmd == "捕捉开关") { _snapOn = !_snapOn; SyncDraftToggles(); StatusMsg.Text = _snapOn ? $"栅格捕捉: 开（步长 {_snapStep:0.##}）" : "栅格捕捉: 关"; return; }
            if (cmd == "对象捕捉" || cmd == "对象捕捉开关" || cmd == "OSNAP") { SnapToggle.IsChecked = !(SnapToggle.IsChecked == true); StatusMsg.Text = $"对象捕捉: {(SnapToggle.IsChecked == true ? "开" : "关")}"; return; }
            if (cmd == "交点捕捉") { ToggleSnapExtra(ObjectSnap.Mode.Intersection, "交点"); return; }
            if (cmd == "最近捕捉" || cmd == "最近点捕捉") { ToggleSnapExtra(ObjectSnap.Mode.Nearest, "最近"); return; }
            if (cmd == "垂足捕捉" || cmd == "垂直捕捉") { ToggleSnapExtra(ObjectSnap.Mode.Perpendicular, "垂足"); return; }
            if (cmd == "捕捉全模式" || cmd == "全部对象捕捉") { _snapExtraMask = ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection, ObjectSnap.Mode.Nearest, ObjectSnap.Mode.Perpendicular); SnapToggle.IsChecked = true; StatusMsg.Text = "对象捕捉: 交点+最近+垂足 全开(端点/中点/圆心/象限恒开)"; return; }
            if (cmd == "滑动多段线") { StartSlide(); return; }
            if (cmd == "平移" || cmd == "PAN") { StatusMsg.Text = "平移：按住鼠标中键拖拽视图（滚轮朝光标缩放）"; return; }
            if (cmd == "填充十字" || cmd == "交叉填充" || cmd == "十字填充") { _hatchCross = !_hatchCross; StatusMsg.Text = $"图案填充: 十字交叉 {(_hatchCross ? "开" : "关")}（再执行 图案填充）"; return; }
            if (cmd == "图案填充" || cmd == "填充" || cmd == "HATCH" || cmd == "剖面线"
                || cmd.StartsWith("图案填充 ") || cmd.StartsWith("填充 ") || cmd.StartsWith("HATCH ") || cmd.StartsWith("剖面线 "))
            {
                double ang = 45, sp = 0;   // 缺省 45°、自动间距; 可 "图案填充 <角度> [间距]"
                var tok = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length >= 2) double.TryParse(tok[1], out ang);
                if (tok.Length >= 3) double.TryParse(tok[2], out sp);
                HatchBoundaryCmd(ang, sp);
                return;
            }
            if (ActivateDrawTool(cmd)) return;
            StatusMsg.Text = $"命令: {cmd}";
            CommandInput.Text = cmd;
            CommandInput.CaretIndex = cmd.Length;
        }
    }

    // DXF 导入：文件对话框 → DxfImportService → 视口显示 + 范围缩放
    private async Task ImportDxfAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入图形（DXF/DWG/OFF/MapGIS/KDF/3DMine）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("支持的格式 (DXF/DWG/OFF/WL/WT/WP/MPJ/KDF/3DM)") { Patterns = new[] { "*.dxf", "*.dwg", "*.off", "*.wl", "*.wt", "*.wp", "*.mpj", "*.kdf", "*.3dm" } },
                new FilePickerFileType("CAD 图纸 (DXF/DWG)") { Patterns = new[] { "*.dxf", "*.dwg" } },
                new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } },
                new FilePickerFileType("MapGIS 6.x (WL 线/WT 注记/WP 区/MPJ 工程)") { Patterns = new[] { "*.wl", "*.wt", "*.wp", "*.mpj" } },
                new FilePickerFileType("WeCAD 地质地形图 (KDF)") { Patterns = new[] { "*.kdf" } },
                new FilePickerFileType("3DMine 网格 (3DM)") { Patterns = new[] { "*.3dm" } }
            }
        });
        if (files.Count == 0) return;
        ImportPath(files[0].Path.LocalPath);
    }

    // 导出 DXF：把场景实体写为原生 DXF 实体（直线/圆/弧/点/多段线，保留图层）
    private async Task ExportDxfAsync()
    {
        if (_scene.Count == 0) { StatusMsg.Text = "场景为空，无可导出"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 DXF",
            DefaultExtension = "dxf",
            SuggestedFileName = "export.dxf",
            FileTypeChoices = new[] { new FilePickerFileType("DXF 图纸") { Patterns = new[] { "*.dxf" } } }
        });
        if (file == null) return;
        try
        {
            int n = SceneExportService.Export(_scene, file.Path.LocalPath);
            StatusMsg.Text = $"已导出 {Path.GetFileName(file.Path.LocalPath)} · {n} 实体";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"导出失败：{ex.Message}"; }
    }

    // 另存为：场景存 .pmx / 导出 .dxf / .dwg（按所选扩展名）
    private async Task SaveAsAsync()
    {
        if (_scene.Count == 0) { StatusMsg.Text = "场景为空，无可另存"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为",
            DefaultExtension = "pmx",
            SuggestedFileName = "drawing.pmx",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PitMine 图形 (PMX)") { Patterns = new[] { "*.pmx" } },
                new FilePickerFileType("DXF 图纸") { Patterns = new[] { "*.dxf" } },
                new FilePickerFileType("DWG 图纸") { Patterns = new[] { "*.dwg" } }
            }
        });
        if (file == null) return;
        string path = file.Path.LocalPath;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".pmx") { File.WriteAllText(path, SceneIO.SaveDoc(_scene, _layers.Layers, _layers.Current.Name)); SetDocPath(path); StatusMsg.Text = $"已另存 {Path.GetFileName(path)} · {_scene.Count} 实体"; }
            else { int n = SceneExportService.Export(_scene, path, _layers); StatusMsg.Text = $"已导出 {Path.GetFileName(path)} · {n} 实体（.dxf/.dwg 不改当前文档）"; }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"另存失败：{ex.Message}"; }
    }

    // ---------- 文件：新建 / 打开 / 保存（绘制场景内部格式）----------
    private void NewScene()
    {
        // 完整文档重置：绘图 / 导入 / 图层 / 选择 / 撤销 / 进行中的命令
        _scene.Clear();
        _selected.Clear(); _prevSelected = new();
        _tool = null; _measure = null; _angle = null;
        _editMode = EditMode.None; _editPts.Clear();
        _offsetActive = false; _trimActive = false;
        _breakActive = false; _breakPts.Clear();
        _slideActive = false; _slideDragging = false; _slidePts.Clear();
        _lastInputPoint = null;

        _lastImport = null;
        Viewport.ClearImported();
        Viewport.SetHighlight(null);
        Viewport.SetSnapMarker(null); _snapShown = false;
        ObjectTree.ItemsSource = null;
        ObjectTreeHint.IsVisible = true;

        _layers.Reset();
        PopulateDrawingLayers();
        _undo.Clear();
        SetDocPath(null);
        RefreshScene();
        StatusMsg.Text = "新建图形（已重置：绘图/导入/图层/选择/撤销）";
    }

    // 设置当前文档路径并更新窗口标题
    private void SetDocPath(string? path)
    {
        _currentPath = path;
        Title = "PitMine3D · Kylin — " + (path == null ? "未命名" : Path.GetFileName(path));
    }

    private async Task SaveSceneAsync()
    {
        string? path = _currentPath;
        if (path == null || Path.GetExtension(path).ToLowerInvariant() != ".pmx")   // 无当前 .pmx → 弹框
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存图形",
                DefaultExtension = "pmx",
                SuggestedFileName = "drawing.pmx",
                FileTypeChoices = new[] { new FilePickerFileType("PitMine 图形") { Patterns = new[] { "*.pmx" } } }
            });
            if (file == null) return;
            path = file.Path.LocalPath;
        }
        try
        {
            File.WriteAllText(path, SceneIO.SaveDoc(_scene, _layers.Layers, _layers.Current.Name));
            SetDocPath(path);
            StatusMsg.Text = $"已保存 {Path.GetFileName(path)} · {_scene.Count} 实体 · {_layers.Layers.Count} 图层";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"保存失败：{ex.Message}"; }
    }

    private async Task OpenSceneAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开图形",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PitMine 图形") { Patterns = new[] { "*.pmx" } } }
        });
        if (files.Count == 0) return;
        try
        {
            var doc = SceneIO.LoadDoc(File.ReadAllText(files[0].Path.LocalPath));
            _scene.Clear();
            foreach (var e in doc.Scene.Entities) _scene.Add(e);
            _layers.Reset();
            if (doc.Layers.Count > 0)
                _layers.Restore(doc.Layers, doc.Current);        // 新格式：整表恢复(含冻结/锁定/显隐/空层)
            else
                foreach (var e in _scene.Entities)               // 旧格式：按实体名+色回退重建
                    _layers.EnsureImported(e.LayerName, e.Cr, e.Cg, e.Cb);
            PopulateDrawingLayers();
            _selected.Clear();
            Viewport.SetHighlight(null);
            RefreshScene();
            SetDocPath(files[0].Path.LocalPath);
            StatusMsg.Text = $"已打开 {Path.GetFileName(files[0].Path.LocalPath)} · {_scene.Count} 实体 · {_layers.Layers.Count} 图层";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"打开失败：{ex.Message}"; }
    }

    // 共享导入逻辑：CAD(dxf/dwg) → 可编辑实体入场景；OFF 等网格 → 显示态
    private void ImportPath(string path)
    {
        StatusMsg.Text = $"正在导入 {Path.GetFileName(path)} …";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dxf" || ext == ".dwg") { ImportCadEditable(path); return; }
        if (ext == ".wl" || ext == ".wt" || ext == ".wp" || ext == ".mpj") { ImportMapGisEditable(path); return; }
        if (ext == ".kdf") { ImportKdfEditable(path); return; }

        // OFF 网格 / 3DMine .3dm 三角网 → 显示态线框
        var r = ext == ".3dm" ? Cad.TdmImportService.Load(path) : OffImportService.Load(path);
        if (!r.Success) { StatusMsg.Text = $"导入失败：{r.Error}"; return; }
        _lastImport = r;
        Viewport.ShowImportedLayers(r.LayerGeometry, r.Bounds);
        PopulateObjectTree(r, Path.GetFileName(path));
        PopulateLayers(r);
        StatusMsg.Text = $"已导入 {Path.GetFileName(path)} · {r.EntityCount} 实体 · {r.SegmentCount} 线段 · {r.LayerOrder.Count} 图层";
    }

    // CAD 导入为可编辑实体：入绘制场景 + 图层并入绘制图层表（可选中/编辑/删除/按层管理）
    private void ImportCadEditable(string path)
    {
        var er = DxfImportService.LoadEntities(path);
        if (!er.Success) { StatusMsg.Text = $"导入失败：{er.Error}"; return; }
        string warn = er.Warnings.Count > 0 ? $" · 跳过 {er.Warnings.Count} 类未支持" : "";
        ApplyEntityImport(er, Path.GetFileName(path), warn);
    }

    // MapGIS 6.x .WL(线/等高线) / .WT(点注记) 导入为可编辑实体（忠实移植 MapGisWlReader/MapGisWtReader）
    private void ImportMapGisEditable(string path)
    {
        var er = Cad.MapGisImportService.Load(path);
        if (!er.Success) { StatusMsg.Text = $"导入失败：{er.Error}"; return; }
        string warn = er.Warnings.Count > 0 ? $" · {string.Join("；", er.Warnings)}" : "";
        ApplyEntityImport(er, Path.GetFileName(path), warn);
    }

    // 打开节点编辑器：求值"烘焙"节点产出的几何，加入主绘图场景（可选中/编辑/删除）
    private void OpenNodeEditor()
    {
        var win = new NodeEditorWindow(geoms =>
        {
            if (geoms.Count == 0) return;
            BeginChange();
            foreach (var g in geoms) { AssignLayer(g); _scene.Add(g); }
            RefreshScene();
            StatusMsg.Text = $"节点编辑器：烘焙 {geoms.Count} 个几何入场景（当前图层「{_layers.Current.Name}」）";
        });
        win.Show();
        StatusMsg.Text = "打开节点编辑器（参数→几何→烘焙；点「求值到场景」入图）";
    }

    // WeCAD .KDF 地质地形图 导入为可编辑实体（忠实移植 KdfReader 二进制解析）
    private void ImportKdfEditable(string path)
    {
        var er = Cad.KdfImportService.Load(path);
        if (!er.Success) { StatusMsg.Text = $"导入失败：{er.Error}"; return; }
        string warn = er.Warnings.Count > 0 ? $" · {string.Join("；", er.Warnings)}" : "";
        ApplyEntityImport(er, Path.GetFileName(path), warn);
    }

    // 共享：把可编辑导入结果并入场景 + 图层表 + 对象树（DXF/DWG/MapGIS 通用）
    private void ApplyEntityImport(DxfImportService.EntityImportResult er, string fileName, string warn)
    {
        BeginChange();
        foreach (var ln in er.LayerOrder)
        {
            var c = er.LayerColors[ln];
            _layers.EnsureImported(ln, c.r, c.g, c.b);
        }
        foreach (var en in er.Entities) _scene.Add(en);
        _lastImport = null;                    // 捕捉改用场景几何
        Viewport.ClearImported();               // 不再用显示态网格
        RefreshScene();
        Viewport.FitBounds(er.Bounds);
        PopulateObjectTreeCounts(er.TypeCounts, fileName, er.Entities.Count);
        PopulateDrawingLayers();
        StatusMsg.Text = $"已导入 {fileName} · {er.Entities.Count} 可编辑实体 · {er.LayerOrder.Count} 图层（可选中/编辑/删除）{warn}";
    }

    // 点数据导入：CSV/TXT/XYZ/PTS → 可编辑的点实体（进入绘制场景，可选中/编辑/删除）
    private async Task ImportPointsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入点数据（CSV/TXT/XYZ）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("点数据 (CSV/TXT/XYZ/PTS)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz", "*.pts" } }
            }
        });
        if (files.Count == 0) return;
        ImportPointsPath(files[0].Path.LocalPath);
    }

    private void ImportPointsPath(string path)
    {
        var r = PointDataImportService.Load(path);
        if (!r.Success) { StatusMsg.Text = $"点导入失败：{r.Error}"; return; }
        BeginChange();
        foreach (var (x, y, _) in r.Points)
        {
            var pt = new PointEntity { X = x, Y = y };
            AssignLayer(pt);
            _scene.Add(pt);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"已导入 {r.Points.Count} 个点（{Path.GetFileName(path)}）· 跳过 {r.SkippedLines} 行 · 可选中/编辑";
    }

    // 钻孔导入 + 柱状图展绘（按岩性配色的分层矩形柱）
    private async Task ImportBoreholesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入钻孔数据（CSV：孔号,X,Y,高程,自,至,岩性）",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("钻孔 CSV/TXT") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        var r = BoreholeImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"钻孔导入失败：{r.Error}"; return; }

        double maxDepth = 0; foreach (var h in r.Boreholes) if (h.TotalDepth > maxDepth) maxDepth = h.TotalDepth;
        double scale = 1.0, width = 2.0;
        var cols = BoreholeRender.BuildColumns(r.Boreholes, scale, width);
        BeginChange();
        foreach (var e in cols) _scene.Add(e);   // 保留岩性色，不覆盖图层色
        double lblH = System.Math.Max((r.Bounds[2] - r.Bounds[0]) / 50.0, width);
        foreach (var h in r.Boreholes)           // 孔号标注(孔口上方)
            _scene.Add(new TextEntity { X = h.X, Y = h.Y + lblH * 0.4, Height = lblH, Text = h.Name, Cr = 0.95f, Cg = 0.95f, Cb = 0.4f });
        RefreshScene();
        Viewport.FitBounds(new[] { r.Bounds[0], r.Bounds[1] - maxDepth * scale, r.Bounds[2] + width, r.Bounds[3] });
        StatusMsg.Text = $"已展绘 {r.Boreholes.Count} 个钻孔 · 柱状图+孔号标注（岩性配色）";
    }

    // 煤厚分析：导入钻孔 CSV → 逐孔累计煤层(岩性含「煤」)厚度 → 按厚配色标记(点)入场景 + 统计报表
    private async Task CoalThicknessAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "煤厚分析：选钻孔 CSV（孔号,X,Y,高程,自,至,岩性）",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("钻孔 CSV/TXT") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        var r = BoreholeImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"煤厚分析：钻孔导入失败 {r.Error}"; return; }
        if (r.Boreholes.Count == 0) { StatusMsg.Text = "煤厚分析：无钻孔"; return; }
        var thicks = new List<(double x, double y, double t, string name)>();
        double tmin = double.MaxValue, tmax = double.MinValue, tsum = 0;
        int coalHoles = 0;
        foreach (var h in r.Boreholes)
        {
            var iv = h.Intervals.Select(i => (i.From, i.To, i.Rock));
            double t = CoalThicknessAnalyzer.CoalThickness(iv);
            thicks.Add((h.X, h.Y, t, h.Name));
            if (t < tmin) tmin = t; if (t > tmax) tmax = t; tsum += t; if (t > 1e-9) coalHoles++;
        }
        double range = tmax - tmin;
        double markSize = System.Math.Max((r.Bounds[2] - r.Bounds[0]) / 40.0, 1.0);
        BeginChange();
        foreach (var (x, y, t, _) in thicks)
        {
            double f = range > 1e-9 ? (t - tmin) / range : 0.5;   // 薄蓝→厚红
            _scene.Add(new PointEntity { X = x, Y = y, Size = markSize, Cr = (float)f, Cg = 0.35f, Cb = (float)(1 - f) });
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        double mean = tsum / r.Boreholes.Count;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"煤厚分析：{r.Boreholes.Count} 孔(含煤 {coalHoles}) · 煤厚 {tmin.ToString("0.##", inv)}~{tmax.ToString("0.##", inv)}m · 均 {mean.ToString("0.##", inv)}m（薄蓝→厚红标记）";
    }

    // 等高线：高程点 CSV(x,y,z) → IDW 网格 → 多层 Marching Squares → 彩色等值折线
    private async Task ContourFromCsvAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "等高线：选高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"等高线：点导入失败 {r.Error}"; return; }

        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var p in r.Points) { if (p.z < zmin) zmin = p.z; if (p.z > zmax) zmax = p.z; }
        if (zmax - zmin < 1e-6) { StatusMsg.Text = "等高线：z 无起伏（CSV 需带高程列）"; return; }

        int n = 64, levels = 10;
        var grid = Contour.GridFromPoints(r.Points, n, n, out double gx0, out double gy0, out double gdx, out double gdy);
        double step = (zmax - zmin) / (levels + 1);
        double labelH = System.Math.Max((r.Bounds[2] - r.Bounds[0]) / 60.0, 1e-3);   // 标注字高
        BeginChange();
        int segCount = 0;
        for (int k = 1; k <= levels; k++)
        {
            double L = zmin + step * k;
            float t = (float)((L - zmin) / (zmax - zmin));
            var segs = Contour.MarchingSquares(grid, gx0, gy0, gdx, gdy, L);
            foreach (var s in segs)
            {
                _scene.Add(new LineEntity { X0 = s.x0, Y0 = s.y0, X1 = s.x1, Y1 = s.y1, Cr = t, Cg = 0.45f, Cb = 1 - t });
                segCount++;
            }
            if (segs.Count > 0)   // 每层一个高程数字标注(取中间那段)
            {
                var mid = segs[segs.Count / 2];
                _scene.Add(new TextEntity { X = mid.x0, Y = mid.y0, Height = labelH, Text = L.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), Cr = t, Cg = 0.45f, Cb = 1 - t });
            }
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"等高线：{r.Points.Count} 点 → {levels} 层 · {segCount} 段 + 高程标注（z {zmin:0.#}~{zmax:0.#}）";
    }

    // 剥采比均衡(VP曲线)：分期物料量 CSV → 累计 V-P 曲线 → DP 分段均衡 → 曲线/折线/比值上屏 + 报表
    private async Task StrippingBalanceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "剥采比均衡：选分期物料量 CSV (每行 采出量万t, 剥离量万m³)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("分期物料量 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var coal = new List<double>(); var strip = new List<double>();
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ',', '\t', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                var nums = new List<double>();
                foreach (var p in parts)
                    if (double.TryParse(p, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)) nums.Add(v);
                if (nums.Count >= 2) { coal.Add(nums[^2]); strip.Add(nums[^1]); }   // 末两数 = 采出量,剥离量
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"剥采比均衡：读取失败 {ex.Message}"; return; }
        if (coal.Count < 2) { StatusMsg.Text = "剥采比均衡：需至少 2 期（每行 采出量,剥离量）"; return; }

        var xs = new List<double> { 0 }; var ys = new List<double> { 0 };
        double cx = 0, cy = 0;
        for (int i = 0; i < coal.Count; i++) { cx += coal[i]; cy += strip[i]; xs.Add(cx); ys.Add(cy); }

        var res = VpBalanceSolver.Solve(xs, ys, null);
        if (!res.Ok) { StatusMsg.Text = "剥采比均衡：求解失败（累计曲线需单调不减）"; return; }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        BeginChange();
        var curve = new PolylineEntity { Cr = 0.3f, Cg = 0.85f, Cb = 0.95f };   // 实际累计 V-P 曲线(青)
        for (int i = 0; i < xs.Count; i++) curve.Points.Add((xs[i], ys[i]));
        AssignLayer(curve); curve.Cr = 0.3f; curve.Cg = 0.85f; curve.Cb = 0.95f; _scene.Add(curve);

        var bal = new PolylineEntity { Cr = 0.95f, Cg = 0.85f, Cb = 0.3f };     // 均衡折线(黄, 过断点)
        foreach (var bp in res.Breakpoints) bal.Points.Add((xs[bp], ys[bp]));
        AssignLayer(bal); bal.Cr = 0.95f; bal.Cg = 0.85f; bal.Cb = 0.3f; _scene.Add(bal);

        double h = System.Math.Max(cx / 40.0, 1e-3);
        foreach (var seg in res.Segments)
        {
            double mx = (xs[seg.A] + xs[seg.B]) / 2, my = (ys[seg.A] + ys[seg.B]) / 2;
            _scene.Add(new TextEntity { X = mx, Y = my + h, Height = h, Text = seg.RatioM3PerT.ToString("0.##", inv), Cr = 0.95f, Cg = 0.85f, Cb = 0.3f });
        }
        RefreshScene();
        Viewport.FitBounds(new double[] { 0, 0, cx, cy });

        string report = $"剥采比均衡：{coal.Count} 期 · K={res.UsedK} 段 · 总超前剥离面积 {res.TotalLeadArea:0.#}；";
        for (int i = 0; i < res.Segments.Count; i++)
        {
            var s = res.Segments[i];
            report += $" 段{i + 1} 均衡比{s.RatioM3PerT.ToString("0.##", inv)}({s.B - s.A}期,峰值超前{s.PeakLeadWanM3:0.#})";
        }
        StatusMsg.Text = report;
    }

    // 网格边界：OFF 网格 → 提取开放边边界环 → 各环作闭合折线(投影 XY)入场景
    private async Task MeshBoundaryAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "网格边界：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格边界：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "网格边界：未解析到三角网格"; return; }
        var loops = MeshBoundaryLoops.Extract(verts, tris);
        if (loops.Count == 0) { StatusMsg.Text = "网格边界：无开放边(网格闭合/水密), 无边界环"; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int totPts = 0;
        BeginChange();
        foreach (var loop in loops)
        {
            var pl = new PolylineEntity { Closed = true, Cr = 0.35f, Cg = 0.9f, Cb = 0.55f };   // 绿色边界环
            foreach (var (x, y, _) in loop)
            {
                pl.Points.Add((x, y));
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
            totPts += loop.Count;
            _scene.Add(pl);
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        StatusMsg.Text = $"网格边界：{loops.Count} 环 · {totPts} 点(投影 XY 作闭合折线入场景)";
    }

    // 环节降效产能（TaskLib 降效切片）：给采/运/排降效% → 用默认编组解 τ_L/T_c/MF → 采装面/排土面能力系数 + 降后产能。
    // 用法 "环节降效 <采%> <运%> <排%>"，缺省 (10/20/15)。
    private void LinkDerateCmd(double dLoad, double dHaul, double dDump)
    {
        var fc = Cad.Tasks.FleetCycle.Solve(12, 100, 2.5, 1.5, 3, trucks: 0);   // 默认编组解出 τ_L/T_c/MF
        double fLoad = Cad.Tasks.LinkDerate.Factor(Cad.Tasks.ProcessType.Load, fc.LoadTaktMin, fc.CycleTimeMin, fc.MatchFactor, dLoad, dHaul, dDump);
        double fDump = Cad.Tasks.LinkDerate.Factor(Cad.Tasks.ProcessType.Dump, 0, 0, 0, dLoad, dHaul, dDump);
        StatusMsg.Text = $"环节降效：采装{dLoad:0.#}%/运输{dHaul:0.#}%/排土{dDump:0.#}% · 编组(MF {fc.MatchFactor:0.##}) → 采装面系数 {fLoad:0.###}(降后产能 {fc.GroupCapM3PerH * fLoad:0.#}m³/h) · 排土面系数 {fDump:0.###}（运输降效对铲瓶颈面不生效=物理）";
    }

    // 编组产能（TaskLib 车铲循环切片）：铲斗/载重/密度/运距/车数 → 斗数/节拍/循环/匹配系数/编组产能。
    // 用法 "编组产能 <铲斗m³> <载重t> <ρ实> <Ks> <运距km> [车数]"，缺省 12/100/2.5/1.5/3/最优。
    private void FleetCycleCmd(double bucketM3, double payloadT, double rho, double ks, double haulKm, int trucks)
    {
        var r = Cad.Tasks.FleetCycle.Solve(bucketM3, payloadT, rho, ks, haulKm, trucks);
        StatusMsg.Text = $"编组产能：{r.BucketsPerTruck:0.#}斗/车 · 节拍 {r.LoadTaktMin:0.##}min · 循环 {r.CycleTimeMin:0.#}min · 最优 {r.OptimalTrucks}车(实 {r.Trucks}) · MF {r.MatchFactor:0.##}({r.Bottleneck}) · 铲{r.ShovelCapTph:0}t/h·队{r.FleetCapTph:0}t/h → 编组产能 {r.GroupCapM3PerH:0.#}m³实方/h";
    }

    // 工序进度跟踪（TaskLib 进度切片）：读任务 计划/实绩 CSV(工序,计划量,实绩量[,计划延米,实绩延米]) → 按工序聚合达成率。
    private async Task ProcessProgressAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "工序进度跟踪：选任务 CSV(工序,计划量,实绩量[,计划延米,实绩延米])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("任务进度 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"工序进度：读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var recs = new List<Cad.Tasks.ProcessProgress.Row>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var c = s.Split(new[] { ',', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (c.Length < 3) continue;
            var proc = ParseProcess(c[0].Trim());
            if (proc == null) continue;
            double D(int i) => c.Length > i && double.TryParse(c[i].Trim(), System.Globalization.NumberStyles.Float, inv, out var v) ? v : 0;
            Cad.Tasks.DrillQuantity? drill = null;
            if (proc == Cad.Tasks.ProcessType.Drill && c.Length >= 5)
                drill = new Cad.Tasks.DrillQuantity { PlanMeters = D(3), ActualMeters = D(4) };
            recs.Add(new Cad.Tasks.ProcessProgress.Row(proc.Value, D(1), D(2), drill));
        }
        if (recs.Count == 0) { StatusMsg.Text = "工序进度：未解析到任务(需 工序,计划量,实绩量)"; return; }
        var sum = Cad.Tasks.ProcessProgress.Summarize(recs);
        var parts = sum.Select(p => $"{Cad.Tasks.ProcessProgress.Label(p.Process)} {p.Count}项(达成{p.AvgAttainmentPct:0.#}%·达标{p.DoneCount})");
        StatusMsg.Text = "工序进度跟踪：" + string.Join(" · ", parts);
    }

    // 配煤核算（TaskLib 煤质切片）：读配煤 CSV(吨,灰%,热MJ,硫%) → 按吨量加权混合煤质。
    private async Task CoalBlendAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "配煤核算：选配煤 CSV(吨,灰分%,热值MJ/kg,硫分%)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("配煤 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"配煤核算：读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var src = new List<(double, Cad.Tasks.CoalQuality)>();
        double totT = 0;
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var c = s.Split(new[] { ',', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (c.Length < 2) continue;
            if (!double.TryParse(c[0].Trim(), System.Globalization.NumberStyles.Float, inv, out double t)) continue;
            double D(int i) => c.Length > i && double.TryParse(c[i].Trim(), System.Globalization.NumberStyles.Float, inv, out var v) ? v : 0;
            src.Add((t, new Cad.Tasks.CoalQuality { AshPct = D(1), CalorificMJkg = D(2), SulfurPct = D(3) }));
            totT += t;
        }
        if (src.Count == 0) { StatusMsg.Text = "配煤核算：未解析到配煤记录(需 吨,灰%,热MJ,硫%)"; return; }
        var b = Cad.Tasks.CoalQuality.Blend(src);
        var std = Cad.Tasks.CoalQuality.Standard;
        bool ok = b.MeetsTarget(std);
        StatusMsg.Text = $"配煤核算：{src.Count} 路 · 总 {totT / 1e4:0.##}万t → 混合煤质 {b.Caption} · 对标(灰≤{std.AshPct}/热≥{std.CalorificMJkg}/硫≤{std.SulfurPct}) {(ok ? "达标 ✓" : "不达标 ✗")}";
    }

    // 排土场按量推进（TaskLib 汇切片）：按排弃占容方反算推进距离 d = V容 / (工作线长 × 台阶高)。
    // 用法 "排土场按量推进 <占容方m³> <工作线长m> <台阶高m>"，缺省 (占容/工作线/台阶)=(60000/300/20)。
    private void DumpAdvanceByVolumeCmd(double dumpM3, double workLineM, double benchH)
    {
        var sink = new Cad.Tasks.SinkNode { WorkLineLengthM = workLineM, BenchHeightM = benchH };
        double d = sink.AdvanceMetersFor(dumpM3);
        if (d <= 0) { StatusMsg.Text = "排土场按量推进：工作线长/台阶高需 > 0"; return; }
        StatusMsg.Text = $"排土场按量推进：排弃占容 {dumpM3 / 1e4:0.##}万m³ · 工作线 {workLineM:0.#}m · 台阶 {benchH:0.#}m → 推进距离 {d:0.##} m（坡顶线沿推进方向偏移此距生成堆填面；三维形态需内核）";
    }

    // 采剥平衡分析（TaskLib 物料流切片）：读物料流 CSV(物料,实方m³,去向,运距km) → 采出/剥离/剥采比/内排率/运输功/加权运距。
    private async Task StripBalanceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "采剥平衡：选物料流 CSV(物料,实方m³,去向[内排/外排/破碎站],运距km)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("物料流 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"采剥平衡：读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var b = new Cad.Tasks.PeriodBalance();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var c = s.Split(new[] { ',', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (c.Length < 2) continue;
            string code = Cad.Tasks.MaterialCatalog.CodeFromText(c[0].Trim());
            if (code.Length == 0) continue;   // 跳表头/未知
            if (!double.TryParse(c[1].Trim(), System.Globalization.NumberStyles.Float, inv, out double vol)) continue;
            var sink = c.Length > 2 ? ParseSink(c[2].Trim()) : Cad.Tasks.SinkKind.ExternalDump;
            double km = c.Length > 3 && double.TryParse(c[3].Trim(), System.Globalization.NumberStyles.Float, inv, out var k) ? k : 0;
            b.Flows.Add(new Cad.Tasks.MaterialFlow { MaterialCode = code, InSituM3 = vol, SinkKind = sink, HaulKm = km });
        }
        if (b.Flows.Count == 0) { StatusMsg.Text = "采剥平衡：未解析到物料流(需 物料,实方m³[,去向,运距])"; return; }
        StatusMsg.Text = $"采剥平衡：采出 {b.OreWanT:0.00}万t · 剥离 {b.StripWanM3:0.00}万m³ · 剥采比 {b.StripRatio:0.00} · 排弃 {b.DumpedWanM3:0.00}万m³(内排率 {b.InternalDumpPct:0.#}%) · 运输功 {b.TransportWorkWanTKm:0.00}万t·km · 加权运距 {b.WeightedAvgHaulKm:0.00}km";
    }

    private static Cad.Tasks.SinkKind ParseSink(string s) => s switch
    {
        "内排" or "内排土场" => Cad.Tasks.SinkKind.InternalDump,
        "外排" or "外排土场" => Cad.Tasks.SinkKind.ExternalDump,
        "破碎站" or "破碎" => Cad.Tasks.SinkKind.Crusher,
        "仓" or "原煤仓" => Cad.Tasks.SinkKind.Silo,
        "堆场" or "储煤场" or "储矿场" => Cad.Tasks.SinkKind.Stockpile,
        "表土堆场" => Cad.Tasks.SinkKind.TopsoilYard,
        _ => Cad.Tasks.SinkKind.ExternalDump,
    };

    // 物料换算（TaskLib 物料切片）：混采文本 + 实方体积 → 吨量/松散方/占容方/煤占比。用法 "物料换算 煤7:岩3 1000"。
    private void MaterialConvertCmd(string mixText, double inSituM3)
    {
        var mix = Cad.Tasks.MaterialMix.Parse(mixText);
        if (mix.IsEmpty) { StatusMsg.Text = "物料换算：未解析到物料（例 煤7:岩3 或 岩）"; return; }
        double ton = mix.ToTonnage(inSituM3), loose = mix.ToLooseM3(inSituM3), dump = mix.ToDumpM3(inSituM3);
        double oreM3 = inSituM3 * mix.OreFraction, wasteM3 = inSituM3 * (1 - mix.OreFraction);
        StatusMsg.Text = $"物料换算：{mix.Caption} · 实方 {inSituM3:0.#}m³ → 吨 {ton:0.#}t · 松散 {loose:0.#}m³ · 占容 {dump:0.#}m³ · 采出(煤) {oreM3:0.#}m³/剥离(岩) {wasteM3:0.#}m³（煤占比 {mix.OreFraction * 100:0.#}%）";
    }

    // 生产量核算（TaskLib 量核算切片）：读任务记录 CSV(工序,方量,吨,车次,运距) → 按工序取账分账合计。
    private async Task ProductionQuantityAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "生产量核算：选任务记录 CSV(工序,方量m³,吨,车次,运距km[,孔数,延米])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("任务记录 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"生产量核算：读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tasks = new List<Cad.Tasks.ProductionTask>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var c = s.Split(new[] { ',', '\t', ';' }, System.StringSplitOptions.None);
            if (c.Length < 1) continue;
            var proc = ParseProcess(c[0].Trim());
            if (proc == null) continue;   // 跳表头/未知工序
            double D(int i) => c.Length > i && double.TryParse(c[i].Trim(), System.Globalization.NumberStyles.Float, inv, out var v) ? v : 0;
            int I(int i) => c.Length > i && int.TryParse(c[i].Trim(), out var v) ? v : 0;
            double vol = D(1), ton = D(2), km = D(4);
            tasks.Add(new Cad.Tasks.ProductionTask
            {
                Process = proc.Value,
                ControlVolumeM3 = proc == Cad.Tasks.ProcessType.Drill ? vol : 0,
                TargetVolumeM3 = proc == Cad.Tasks.ProcessType.Load ? vol : 0,
                TargetTonnageT = proc == Cad.Tasks.ProcessType.Load ? ton : 0,
                HaulTonnageT = proc == Cad.Tasks.ProcessType.Haul ? ton : 0,
                TripCount = I(3),
                DumpVolumeM3 = proc == Cad.Tasks.ProcessType.Dump ? vol : 0,
                EffectiveHaulKm = km,
                Drill = proc == Cad.Tasks.ProcessType.Drill ? new Cad.Tasks.DrillInfo { PlanHoles = I(5), PlanMeters = D(6) } : null,
            });
        }
        if (tasks.Count == 0) { StatusMsg.Text = "生产量核算：未解析到任务记录(工序需 穿孔/爆破/采装/运输/排土)"; return; }
        StatusMsg.Text = "生产量核算（分账，不合总）：" + Cad.Tasks.TaskQuantity.Sum(tasks).Caption;
    }

    private static Cad.Tasks.ProcessType? ParseProcess(string s) => s switch
    {
        "穿孔" or "钻孔" or "drill" or "Drill" => Cad.Tasks.ProcessType.Drill,
        "爆破" or "blast" or "Blast" => Cad.Tasks.ProcessType.Blast,
        "采装" or "采掘" or "装载" or "load" or "Load" => Cad.Tasks.ProcessType.Load,
        "运输" or "haul" or "Haul" => Cad.Tasks.ProcessType.Haul,
        "排土" or "排弃" or "dump" or "Dump" => Cad.Tasks.ProcessType.Dump,
        _ => null,
    };

    // 排土场容量校核：排土设计面 vs 现状面 的填方体积 = 设计形态总容积(原义)。复用 TerrainAnalysis.TwoEpochVolume。
    private async Task DumpCapacityAsync()
    {
        var opt = new System.Func<string, FilePickerOpenOptions>(t => new FilePickerOpenOptions
        {
            Title = t, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        var f1 = await StorageProvider.OpenFilePickerAsync(opt("容量校核：选【现状面】高程点 CSV"));
        if (f1.Count == 0) return;
        var f2 = await StorageProvider.OpenFilePickerAsync(opt("容量校核：选【排土设计面】高程点 CSV"));
        if (f2.Count == 0) return;
        var r1 = PointDataImportService.Load(f1[0].Path.LocalPath);
        var r2 = PointDataImportService.Load(f2[0].Path.LocalPath);
        if (!r1.Success || !r2.Success) { StatusMsg.Text = "容量校核：点导入失败"; return; }
        var (cut, fill, net) = TerrainAnalysis.TwoEpochVolume(r1.Points, r2.Points, 64);
        StatusMsg.Text = $"排土场容量校核：设计容积(填方) {fill:0.##} m³{(cut > 1e-6 ? $" · 设计面低于现状处(挖) {cut:0.##}" : "")} · 净 {net:0.##}（对比需排量判够不够）";
    }

    // 中心线管理 / 边状态：从场景折线(道路中线)建路网 → 拓扑报表(中线/节点/边/总长/断头/交叉)。
    // 原为管理·状态对话框; 此出只读拓扑视图(增删边/改状态需交互 UI, 记录)。
    private void RoadNetworkReportCmd()
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "中心线管理：场景无中线（多段线）"; return; }
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = Cad.RoadNetwork.Build(polys, tol);
        int edges = 0, deadEnds = 0, junctions = 0, isolated = 0;
        double totLen = 0;
        for (int u = 0; u < adj.Count; u++)
        {
            int deg = adj[u].Count;
            if (deg == 0) isolated++; else if (deg == 1) deadEnds++; else if (deg >= 3) junctions++;
            foreach (var (v, w) in adj[u]) if (v > u) { edges++; totLen += w; }
        }
        StatusMsg.Text = $"中心线管理/边状态：{polys.Count} 中线 · 节点 {nodes.Count} · 边 {edges}(总长 {totLen:0.#}) · 断头 {deadEnds} · 交叉 {junctions} · 孤立 {isolated}（增删边/改状态需交互 UI）";
    }

    // 快速建模：选顶面 + 底面 OFF → 各提最大边界环 → 侧壁放样(SideSurface.Loft) → 顶+底+侧 焊成闭合体。
    // 复用已验证 primitives(MeshBoundaryLoops + SideSurface.Loft + MeshWeld), 免移原 1000 行 QuickModelBuilder。
    private async Task QuickModelAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "快速建模：选顶面 + 底面 OFF（2 份）",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count < 2) { StatusMsg.Text = "快速建模：请选 2 份 OFF（顶面、底面）"; return; }
        var (v0, t0) = MeshMetrics.ParseOff(System.IO.File.ReadAllText(files[0].Path.LocalPath));
        var (v1, t1) = MeshMetrics.ParseOff(System.IO.File.ReadAllText(files[1].Path.LocalPath));
        if (t0.Count == 0 || t1.Count == 0) { StatusMsg.Text = "快速建模：某面未解析到三角网"; return; }
        var loop0 = LargestLoop(MeshBoundaryLoops.Extract(v0, t0));
        var loop1 = LargestLoop(MeshBoundaryLoops.Extract(v1, t1));
        if (loop0 == null || loop1 == null) { StatusMsg.Text = "快速建模：顶/底面需为有开边的开放面（取其边界环放样侧壁）"; return; }
        var (sv, st) = SideSurface.Loft(loop0, loop1, closed: true, flip: false);
        if (st.Count == 0) { StatusMsg.Text = "快速建模：侧壁放样失败（边界环过短）"; return; }
        var (verts, tris) = MeshWeld.Concat(new List<(IReadOnlyList<(double x, double y, double z)>, IReadOnlyList<(int a, int b, int c)>)>
            { (v0, t0), (v1, t1), (sv, st) });
        var m = MeshMetrics.Compute(verts, tris);
        double diag = System.Math.Sqrt((m.MaxX - m.MinX) * (m.MaxX - m.MinX) + (m.MaxY - m.MinY) * (m.MaxY - m.MinY) + (m.MaxZ - m.MinZ) * (m.MaxZ - m.MinZ));
        var w = MeshWeld.Weld(verts, tris, diag > 0 ? diag * 1e-4 : 1e-6, dropDuplicateTris: true);
        var d = MeshDiagnose.Analyze(w.Verts, w.Tris);
        bool watertight = d.BoundaryEdges == 0 && d.NonManifoldEdges == 0;
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".", "quickmodel.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(w.Verts, w.Tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"快速建模：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"快速建模：顶+底+侧壁 焊成 {w.OutputTris} 三角 · {(watertight ? "水密(闭合地质体)" : $"非水密(开放边 {d.BoundaryEdges})")} → {System.IO.Path.GetFileName(outPath)}";
    }

    private static List<(double x, double y, double z)>? LargestLoop(List<List<(double x, double y, double z)>> loops)
    {
        List<(double x, double y, double z)>? best = null;
        foreach (var l in loops) if (best == null || l.Count > best.Count) best = l;
        return best;
    }

    // 分割三角网：选中折线定切割线(首→末点所在竖直面) + 选 OFF → 三角形-平面裁剪切两片 → 落 .left/.right.off。
    private async Task MeshSplitAsync()
    {
        (double x, double y)? p0 = null, p1 = null;
        foreach (var e in _selected)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) { p0 = pl.Points[0]; p1 = pl.Points[pl.Points.Count - 1]; break; }
        if (p0 == null || p1 == null) { StatusMsg.Text = "分割三角网：请先选中一条折线作切割线（用其首→末点定竖直切面）"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "分割三角网：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"分割三角网：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "分割三角网：未解析到三角网格"; return; }
        var (l, r) = MeshPlaneSplit.Split(verts, tris, p0.Value.x, p0.Value.y, p1.Value.x, p1.Value.y);
        if (l.t.Count == 0 || r.t.Count == 0) { StatusMsg.Text = "分割三角网：切面未穿过网格（一侧为空），未切分"; return; }
        string dir = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".";
        string lp = System.IO.Path.Combine(dir, "split_left.off"), rp = System.IO.Path.Combine(dir, "split_right.off");
        try
        {
            System.IO.File.WriteAllText(lp, MeshWeld.ToOff(l.v, l.t));
            System.IO.File.WriteAllText(rp, MeshWeld.ToOff(r.v, r.t));
        }
        catch (System.Exception ex) { StatusMsg.Text = $"分割三角网：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"分割三角网：切两片 · 左 {l.t.Count} 三角 / 右 {r.t.Count} 三角 → split_left/right.off（切线取折线首末点弦，曲折线为弦近似）";
    }

    // 补洞(三角网)：选 OFF → 提边界洞 → 扇形填充 → 落 .filled.off + 前后开放边报表。
    private async Task MeshHoleFillAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "补洞：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"补洞：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "补洞：未解析到三角网格"; return; }
        int beforeB = MeshDiagnose.Analyze(verts, tris).BoundaryEdges;
        if (beforeB == 0) { StatusMsg.Text = "补洞：网格已水密(无开放边), 无洞可补"; return; }
        var (nv, nt, holes) = MeshHoleFill.Fill(verts, tris);
        int afterB = MeshDiagnose.Analyze(nv, nt).BoundaryEdges;
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".", "filled.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(nv, nt)); }
        catch (System.Exception ex) { StatusMsg.Text = $"补洞：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"补洞：补 {holes} 洞 · 三角 {tris.Count}→{nt.Count} · 开放边 {beforeB}→{afterB}{(afterB == 0 ? "(已水密)" : "")} → {System.IO.Path.GetFileName(outPath)}";
    }

    // 读多份 OFF 并拼接为一份 (verts, tris)(索引偏移)；失败的文件跳过
    private static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) ReadConcatOff(IReadOnlyList<string> paths)
    {
        var meshes = new List<(IReadOnlyList<(double x, double y, double z)>, IReadOnlyList<(int a, int b, int c)>)>();
        foreach (var p in paths)
        {
            string text;
            try { text = System.IO.File.ReadAllText(p); } catch { continue; }
            var (v, t) = MeshMetrics.ParseOff(text);
            meshes.Add((v, t));
        }
        return MeshWeld.Concat(meshes);
    }

    // 合并三角网：选多份 OFF → 拼接 → 跨网焊接(去重复三角) → 落 .merged.off + 开放/非流形边 报表
    private async Task MeshMergeAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "合并三角网：选多份 OFF 网格",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        var paths = new List<string>(); foreach (var f in files) paths.Add(f.Path.LocalPath);
        var (verts, tris) = ReadConcatOff(paths);
        if (tris.Count == 0) { StatusMsg.Text = "合并三角网：未解析到三角网格"; return; }
        var m = MeshMetrics.Compute(verts, tris);
        double diag = System.Math.Sqrt((m.MaxX - m.MinX) * (m.MaxX - m.MinX) + (m.MaxY - m.MinY) * (m.MaxY - m.MinY) + (m.MaxZ - m.MinZ) * (m.MaxZ - m.MinZ));
        double tol = diag > 0 ? diag * 1e-4 : 1e-6;
        var w = MeshWeld.Weld(verts, tris, tol, dropDuplicateTris: true);
        var d = MeshDiagnose.Analyze(w.Verts, w.Tris);
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(paths[0]) ?? ".", "merged.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(w.Verts, w.Tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"合并三角网：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"合并三角网：{paths.Count} 网 · 顶点 {w.InputVerts}→{w.OutputVerts} · 三角 {w.OutputTris}(去重复 {w.DuplicateTris}) · 开放边 {d.BoundaryEdges}·非流形 {d.NonManifoldEdges} → {System.IO.Path.GetFileName(outPath)}";
    }

    // 固化成体：选多份 OFF(顶/底/侧) → 拼接 → 跨网焊接(保缠绕) → 水密自检 → 落 .solid.off + 报表
    private async Task SolidifyAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "固化成体：选多份 OFF(顶/底/侧面)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        var paths = new List<string>(); foreach (var f in files) paths.Add(f.Path.LocalPath);
        var (verts, tris) = ReadConcatOff(paths);
        if (tris.Count == 0) { StatusMsg.Text = "固化成体：未解析到三角网格"; return; }
        var m = MeshMetrics.Compute(verts, tris);
        double diag = System.Math.Sqrt((m.MaxX - m.MinX) * (m.MaxX - m.MinX) + (m.MaxY - m.MinY) * (m.MaxY - m.MinY) + (m.MaxZ - m.MinZ) * (m.MaxZ - m.MinZ));
        double tol = diag > 0 ? diag * 1e-4 : 1e-6;
        var w = MeshWeld.Weld(verts, tris, tol, dropDuplicateTris: false);   // 闭合体上同顶点不同缠绕合法, 不去重复
        var d = MeshDiagnose.Analyze(w.Verts, w.Tris);
        bool watertight = d.BoundaryEdges == 0 && d.NonManifoldEdges == 0 && w.OutputTris > 0;
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(paths[0]) ?? ".", "solid.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(w.Verts, w.Tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"固化成体：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"固化成体：{paths.Count} 网焊成 {w.OutputTris} 三角 · {(watertight ? "水密(闭合实体)" : $"非水密(开放边 {d.BoundaryEdges}·非流形 {d.NonManifoldEdges})")} → {System.IO.Path.GetFileName(outPath)}";
    }

    // 体素格网体积：选封闭 OFF → 广义缠绕数逐格判内外 → 占用格数×格体积 = 体素体积(与散度定理精确体积对比)
    private async Task VoxelVolumeAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "体素格网体积：选封闭 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"体素格网体积：读取失败 {ex.Message}"; return; }
        var (mv, mt) = MeshMetrics.ParseOff(text);
        if (mt.Count == 0) { StatusMsg.Text = "体素格网体积：无三角"; return; }
        var fv = new double[mv.Count * 3];
        for (int i = 0; i < mv.Count; i++) { fv[i * 3] = mv[i].x; fv[i * 3 + 1] = mv[i].y; fv[i * 3 + 2] = mv[i].z; }
        var ft = new int[mt.Count * 3];
        for (int i = 0; i < mt.Count; i++) { ft[i * 3] = mt[i].a; ft[i * 3 + 1] = mt[i].b; ft[i * 3 + 2] = mt[i].c; }

        WindingNumberTester wn;
        try { wn = new WindingNumberTester(fv, ft); }
        catch (System.Exception ex) { StatusMsg.Text = $"体素格网体积：建测试器失败 {ex.Message}"; return; }
        double dx = wn.MaxX - wn.MinX, dy = wn.MaxY - wn.MinY, dz = wn.MaxZ - wn.MinZ;
        double diag = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double cell = diag > 0 ? diag / 60.0 : 1.0;   // 格边=包围盒对角/60(约束总格数)
        long occupied = 0, total = 0;
        for (double z = wn.MinZ + cell * 0.5; z <= wn.MaxZ; z += cell)
            for (double y = wn.MinY + cell * 0.5; y <= wn.MaxY; y += cell)
                for (double x = wn.MinX + cell * 0.5; x <= wn.MaxX; x += cell)
                {
                    total++;
                    if (wn.IsInsideClosed(x, y, z)) occupied++;
                }
        double voxelVol = occupied * cell * cell * cell;
        var m = MeshMetrics.Compute(mv, mt);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"体素格网体积：格边 {cell.ToString("0.##", inv)} · 占用 {occupied}/{total} 格 · 体素体积 {voxelVol.ToString("0.#", inv)}(精确 {m.Volume.ToString("0.#", inv)})";
    }

    // 实体转块体：选封闭 OFF → GWN 逐格判内外 → 占用格作块体(BlockModel.Block)入场景
    private async Task EntityToBlocksAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "实体转块体：选封闭 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"实体转块体：读取失败 {ex.Message}"; return; }
        var (mv, mt) = MeshMetrics.ParseOff(text);
        if (mt.Count == 0) { StatusMsg.Text = "实体转块体：无三角"; return; }
        var fv = new double[mv.Count * 3];
        for (int i = 0; i < mv.Count; i++) { fv[i * 3] = mv[i].x; fv[i * 3 + 1] = mv[i].y; fv[i * 3 + 2] = mv[i].z; }
        var ft = new int[mt.Count * 3];
        for (int i = 0; i < mt.Count; i++) { ft[i * 3] = mt[i].a; ft[i * 3 + 1] = mt[i].b; ft[i * 3 + 2] = mt[i].c; }

        WindingNumberTester wn;
        try { wn = new WindingNumberTester(fv, ft); }
        catch (System.Exception ex) { StatusMsg.Text = $"实体转块体：建测试器失败 {ex.Message}"; return; }
        double dx = wn.MaxX - wn.MinX, dy = wn.MaxY - wn.MinY, dz = wn.MaxZ - wn.MinZ;
        double diag = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double cell = diag > 0 ? diag / 20.0 : 1.0;   // 较粗(限场景块数)
        // 安全：总格数过大再自动加粗
        while (dx / cell * (dy / cell) * (dz / cell) > 200000) cell *= 1.5;
        var blocks = new List<BlockModel.Block>();
        for (double z = wn.MinZ + cell * 0.5; z <= wn.MaxZ; z += cell)
            for (double y = wn.MinY + cell * 0.5; y <= wn.MaxY; y += cell)
                for (double x = wn.MinX + cell * 0.5; x <= wn.MaxX; x += cell)
                    if (wn.IsInsideClosed(x, y, z)) blocks.Add(new BlockModel.Block { X = x, Y = y, Z = z, Size = cell, Grade = 0 });
        if (blocks.Count == 0) { StatusMsg.Text = "实体转块体：无占用块体(网格可能非闭合/朝向不一致)"; return; }
        _lastBlocks = blocks;   // 供资源量/剥采比等复用
        _blockGmin = 0; _blockGmax = 1;   // 体素化块体品位置 0(几何)
        BeginChange();
        RenderBlocks(blocks);
        RefreshScene();
        Viewport.FitBounds(new[] { wn.MinX, wn.MinY, wn.MaxX, wn.MaxY });
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"实体转块体：格边 {cell.ToString("0.##", inv)} · {blocks.Count} 块 · 体积 {(blocks.Count * cell * cell * cell).ToString("0.#", inv)}(已入场景, 可接资源量/筛选)";
    }

    // 基本几何体：生成拓扑闭合三角网 → 保存 OFF + 度量报表
    private async Task SavePrimitiveAsync(string name, List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"保存{name}", DefaultExtension = "off", SuggestedFileName = $"{name}.off",
            FileTypeChoices = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, MeshWeld.ToOff(verts, tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"{name}：写出失败 {ex.Message}"; return; }
        var m = MeshMetrics.Compute(verts, tris);
        var d = MeshDiagnose.Analyze(verts, tris);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"{name}：{m.VertexCount} 顶点 · {m.TriangleCount} 三角 · 体积 {m.Volume.ToString("0.##", inv)} · {(d.IsClosed ? "闭合" : "非闭合")} → {System.IO.Path.GetFileName(file.Path.LocalPath)}";
    }

    private async Task BoxPrimitiveAsync()
    { var (v, t) = PrimitiveBodies.Box(0, 0, 0, 10, 10, 10); await SavePrimitiveAsync("立方体", v, t); }

    private async Task SpherePrimitiveAsync()
    { var (v, t) = PrimitiveBodies.Sphere(0, 0, 0, 5, 16, 24); await SavePrimitiveAsync("球体", v, t); }

    private async Task CylinderPrimitiveAsync()
    { var (v, t) = PrimitiveBodies.Cylinder(0, 0, 0, 5, 10, 24); await SavePrimitiveAsync("圆柱", v, t); }

    // 网格焊接：OFF 网格 → 按容差合并重合顶点 → 落 .welded.off + 报表
    private async Task MeshWeldAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "网格焊接：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath, text;
        try { text = System.IO.File.ReadAllText(path); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格焊接：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "网格焊接：未解析到三角网格"; return; }
        // 容差取包围盒对角的 1e-4（与原「按容差合并重合顶点」同量纲, 缺省保守）
        var m = MeshMetrics.Compute(verts, tris);
        double diag = System.Math.Sqrt((m.MaxX - m.MinX) * (m.MaxX - m.MinX) + (m.MaxY - m.MinY) * (m.MaxY - m.MinY) + (m.MaxZ - m.MinZ) * (m.MaxZ - m.MinZ));
        double tol = diag > 0 ? diag * 1e-4 : 1e-6;
        var w = MeshWeld.Weld(verts, tris, tol, dropDuplicateTris: true);
        string outPath = System.IO.Path.ChangeExtension(path, ".welded.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(w.Verts, w.Tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格焊接：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"网格焊接：顶点 {w.InputVerts}→{w.OutputVerts} · 三角 {w.InputTris}→{w.OutputTris}(退化 {w.DroppedDegenerate}·重复 {w.DuplicateTris}) · 容差 {tol.ToString("0.###e0", System.Globalization.CultureInfo.InvariantCulture)} → {System.IO.Path.GetFileName(outPath)}";
    }

    // OD 运距矩阵：读 OD 点 CSV(x,y[,name]) → 场景多段线路网 → 各 OD 对最短路距离矩阵 → 落 CSV + 报表
    private async Task OdMatrixAsync()
    {
        var polys = new List<IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities) if (e is PolylineEntity p && p.Points.Count >= 2) polys.Add(p.Points);
        if (polys.Count == 0) { StatusMsg.Text = "OD 运距矩阵：场景无路网（多段线）"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "OD 运距矩阵：选 OD 点 CSV (x,y[,name])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("OD 点 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"OD 运距矩阵：读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var pts = new List<(double x, double y)>(); var names = new List<string>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var t = s.Split(new[] { ',', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) continue;
            if (!double.TryParse(t[0], System.Globalization.NumberStyles.Float, inv, out double x)) continue;
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out double y)) continue;
            pts.Add((x, y)); names.Add(t.Length >= 3 ? t[2] : $"P{pts.Count}");
        }
        if (pts.Count < 2) { StatusMsg.Text = "OD 运距矩阵：需 ≥2 个 OD 点(x,y[,name])"; return; }
        double tol = SnapTolWorld(_lastPointer);
        var (nodes, adj) = RoadNetwork.Build(polys, tol > 0 ? tol : 1e-6);
        var idx = new int[pts.Count];
        for (int i = 0; i < pts.Count; i++) idx[i] = RoadNetwork.NearestNode(nodes, pts[i].x, pts[i].y);
        // 逐源单源最短距 → 矩阵
        var mat = new double[pts.Count][];
        int reach = 0; double sum = 0, max = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var dall = RoadNetwork.DijkstraDistances(adj, idx[i]);
            mat[i] = new double[pts.Count];
            for (int j = 0; j < pts.Count; j++)
            {
                double d = (idx[j] >= 0 && idx[j] < dall.Length) ? dall[idx[j]] : double.PositiveInfinity;
                mat[i][j] = d;
                if (i != j && !double.IsInfinity(d)) { reach++; sum += d; if (d > max) max = d; }
            }
        }
        // 落 CSV
        string outPath = System.IO.Path.ChangeExtension(files[0].Path.LocalPath, ".odmatrix.csv");
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("from\\to");
            foreach (var nm in names) sb.Append(',').Append(nm);
            sb.Append('\n');
            for (int i = 0; i < pts.Count; i++)
            {
                sb.Append(names[i]);
                for (int j = 0; j < pts.Count; j++)
                    sb.Append(',').Append(double.IsInfinity(mat[i][j]) ? "INF" : mat[i][j].ToString("0.##", inv));
                sb.Append('\n');
            }
            System.IO.File.WriteAllText(outPath, sb.ToString());
        }
        catch (System.Exception ex) { StatusMsg.Text = $"OD 运距矩阵：写出失败 {ex.Message}"; return; }
        int totalPairs = pts.Count * (pts.Count - 1);
        double avg = reach > 0 ? sum / reach : 0;
        StatusMsg.Text = $"OD 运距矩阵：{pts.Count} 点 · 可达 {reach}/{totalPairs} 对 · 平均 {avg:0.#} · 最大 {max:0.#} → {System.IO.Path.GetFileName(outPath)}";
    }

    // 运距指标：读运输记录 CSV(distanceM,gradePct,tons) → 等效运距/循环时间/加权平均/最大运距 报表
    // (区别于既有 HaulMetricsAsync 的路网寻径版：此为按记录的坡阻折算+循环时间公式)
    private async Task HaulRecordMetricsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "运距指标：选运输记录 CSV (distanceM,gradePct,tons)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("运输记录 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"运距指标：读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var samples = new List<(double d, double grade, double tons)>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var t = s.Split(new[] { ',', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) continue;
            if (!double.TryParse(t[0], System.Globalization.NumberStyles.Float, inv, out double d)) continue;   // 跳表头
            double grade = 0, tons = 1;
            if (t.Length >= 2) double.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out grade);
            if (t.Length >= 3) double.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out tons);
            samples.Add((d, grade, tons));
        }
        if (samples.Count == 0) { StatusMsg.Text = "运距指标：未解析到运输记录(需 distanceM[,gradePct,tons])"; return; }
        var truck = TruckProfile.Default;
        double sumEquiv = 0, sumCycle = 0, sumTons = 0;
        var dists = new List<double>(); var wsamples = new List<(double, double)>();
        foreach (var (d, grade, tons) in samples)
        {
            double loadedMin = HaulMetrics.TravelTimeMin(d, grade, truck, true);
            double emptyMin = HaulMetrics.TravelTimeMin(d, -grade, truck, false);   // 返程坡向相反、空车
            sumCycle += HaulMetrics.CycleTimeMin(loadedMin, emptyMin);
            sumEquiv += HaulMetrics.EquivalentLengthM(d, grade, truck, true);
            sumTons += tons;
            dists.Add(d); wsamples.Add((d, tons));
        }
        double wavg = HaulMetrics.WeightedAverageHaulM(wsamples);
        double maxH = HaulMetrics.MaxHaulM(dists);
        double avgCycle = sumCycle / samples.Count;
        StatusMsg.Text = $"运距指标({samples.Count} 车·{truck.PayloadT:0}t)：加权平均运距 {wavg:0.#}m · 最大 {maxH:0.#}m · 等效总里程 {sumEquiv / 1000.0:0.##}km · 平均循环 {avgCycle:0.#}min · 总量 {sumTons:0.#}t";
    }

    // 读单条 3D 折线 CSV(x,y,z)
    private static List<(double x, double y, double z)>? ReadLineCsv(string path)
    {
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(path); } catch { return null; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var pts = new List<(double x, double y, double z)>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var t = s.Split(new[] { ',', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) continue;
            if (!double.TryParse(t[0], System.Globalization.NumberStyles.Float, inv, out double x)) continue;
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out double y)) continue;
            double z = 0; if (t.Length >= 3) double.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out z);
            pts.Add((x, y, z));
        }
        return pts;
    }

    // 侧面三角网：选顶线 CSV + 底线 CSV → 弧长拉链放样 → 保存 OFF + 报表
    private async Task SideSurfaceAsync()
    {
        var tf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "侧面三角网：① 选顶线 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("折线 CSV") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (tf.Count == 0) return;
        var top = ReadLineCsv(tf[0].Path.LocalPath);
        if (top == null || top.Count < 2) { StatusMsg.Text = "侧面三角网：顶线需 ≥2 点(x,y,z)"; return; }
        var bfp = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "侧面三角网：② 选底线 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("折线 CSV") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (bfp.Count == 0) return;
        var bot = ReadLineCsv(bfp[0].Path.LocalPath);
        if (bot == null || bot.Count < 2) { StatusMsg.Text = "侧面三角网：底线需 ≥2 点(x,y,z)"; return; }
        bool closed = top.Count >= 3 && System.Math.Abs(top[0].x - top[^1].x) < 1e-6 && System.Math.Abs(top[0].y - top[^1].y) < 1e-6;
        var (verts, tris) = SideSurface.Loft(top, bot, closed, flip: false);
        if (tris.Count == 0) { StatusMsg.Text = "侧面三角网：放样失败(点太少/退化)"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存侧面三角网", DefaultExtension = "off", SuggestedFileName = "side.off",
            FileTypeChoices = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, MeshWeld.ToOff(verts, tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"侧面三角网：写出失败 {ex.Message}"; return; }
        var m = MeshMetrics.Compute(verts, tris);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"侧面三角网：顶{top.Count}·底{bot.Count}线{(closed ? "(闭环)" : "")} → {verts.Count} 顶点·{tris.Count} 三角·面积 {m.SurfaceArea.ToString("0.#", inv)} → {System.IO.Path.GetFileName(file.Path.LocalPath)}";
    }

    // 确定可采区域：选煤层底板 OFF + 台阶线 CSV(lineId,x,y,z) → 找采煤台阶+可采面积+上覆揭露量 报表
    private async Task MineableAreaAsync()
    {
        var mf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "确定可采区域：① 选煤层底板 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (mf.Count == 0) return;
        string mtext;
        try { mtext = System.IO.File.ReadAllText(mf[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"确定可采区域：底板读取失败 {ex.Message}"; return; }
        var (mv, mt) = MeshMetrics.ParseOff(mtext);
        if (mt.Count == 0) { StatusMsg.Text = "确定可采区域：底板网格无三角"; return; }
        // tuple → flat
        var fv = new double[mv.Count * 3];
        for (int i = 0; i < mv.Count; i++) { fv[i * 3] = mv[i].x; fv[i * 3 + 1] = mv[i].y; fv[i * 3 + 2] = mv[i].z; }
        var ft = new int[mt.Count * 3];
        for (int i = 0; i < mt.Count; i++) { ft[i * 3] = mt[i].a; ft[i * 3 + 1] = mt[i].b; ft[i * 3 + 2] = mt[i].c; }

        var bf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "确定可采区域：② 选台阶线 CSV (lineId,x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (bf.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(bf[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"确定可采区域：台阶线读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var groups = new Dictionary<string, List<double>>(); var order = new List<string>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var t = s.Split(new[] { ',', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out double x)) continue;
            if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out double y)) continue;
            if (!double.TryParse(t[3], System.Globalization.NumberStyles.Float, inv, out double z)) continue;
            string id = t[0];
            if (!groups.TryGetValue(id, out var list)) { list = new(); groups[id] = list; order.Add(id); }
            list.Add(x); list.Add(y); list.Add(z);
        }
        var benches = new List<MineableAreaIdentifier.BenchLine>();
        foreach (var id in order)
        {
            var arr = groups[id].ToArray();
            if (arr.Length < 6) continue;
            // 首末点近重合 → 判闭合
            bool closed = arr.Length >= 9 &&
                System.Math.Abs(arr[0] - arr[arr.Length - 3]) < 1e-6 && System.Math.Abs(arr[1] - arr[arr.Length - 2]) < 1e-6;
            benches.Add(MineableAreaIdentifier.BenchLine.From(arr, closed));
        }
        if (benches.Count == 0) { StatusMsg.Text = "确定可采区域：未解析到台阶线"; return; }
        var r = MineableAreaIdentifier.Identify(fv, ft, benches, wMin: 20, benchH: 15, faceAngleDeg: 65, bermW: 5);
        if (!r.Ok) { StatusMsg.Text = $"确定可采区域：{r.Message}"; return; }
        // 高亮采煤台阶环
        if (r.CoalBench != null && r.CoalBench.Xyz.Length >= 6)
        {
            var pl = new PolylineEntity { Closed = r.CoalBench.Closed, Cr = 0.95f, Cg = 0.3f, Cb = 0.3f };
            for (int i = 0; i + 2 < r.CoalBench.Xyz.Length; i += 3) pl.Points.Add((r.CoalBench.Xyz[i], r.CoalBench.Xyz[i + 1]));
            BeginChange(); _scene.Add(pl); RefreshScene();
        }
        string strip = r.Overburden.Count > 0
            ? $" · 上覆揭露: " + string.Join(", ", r.Overburden.Select(o => $"Z{o.Z:0}退{o.StripBackM:0.#}m"))
            : "";
        StatusMsg.Text = $"确定可采区域：{r.Message}{strip}";
    }

    // 读网格 OFF → flat (verts, tris)；失败返回 (null,null)
    private (double[]? v, int[]? t) ReadMeshFlat(string path)
    {
        string text;
        try { text = System.IO.File.ReadAllText(path); } catch { return (null, null); }
        var (mv, mt) = MeshMetrics.ParseOff(text);
        if (mt.Count == 0) return (null, null);
        var v = new double[mv.Count * 3];
        for (int i = 0; i < mv.Count; i++) { v[i * 3] = mv[i].x; v[i * 3 + 1] = mv[i].y; v[i * 3 + 2] = mv[i].z; }
        var t = new int[mt.Count * 3];
        for (int i = 0; i < mt.Count; i++) { t[i * 3] = mt[i].a; t[i * 3 + 1] = mt[i].b; t[i * 3 + 2] = mt[i].c; }
        return (v, t);
    }

    // 点落到面上：选网格 OFF + 点 CSV(x,y) → 逐点重心插值取 Z → 落 .draped.csv(x,y,z) + 点入场景
    private async Task ProjectPointsToMeshAsync()
    {
        var mf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "点落到面上：① 选网格 OFF", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (mf.Count == 0) return;
        var (v, t) = ReadMeshFlat(mf[0].Path.LocalPath);
        if (v == null || t == null) { StatusMsg.Text = "点落到面上：网格无三角"; return; }
        var pfp = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "点落到面上：② 选点 CSV (x,y)", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 CSV") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (pfp.Count == 0) return;
        var r = PointDataImportService.Load(pfp[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"点落到面上：点导入失败 {r.Error}"; return; }
        var xy = new List<(double x, double y)>();
        foreach (var p in r.Points) xy.Add((p.x, p.y));
        var (draped, missed) = MeshProjector.Drape(v, t, xy);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("x,y,z\n");
        foreach (var (x, y, z) in draped) sb.Append(x.ToString("R", inv)).Append(',').Append(y.ToString("R", inv)).Append(',').Append(z.ToString("R", inv)).Append('\n');
        string outPath = System.IO.Path.ChangeExtension(pfp[0].Path.LocalPath, ".draped.csv");
        try { System.IO.File.WriteAllText(outPath, sb.ToString()); } catch (System.Exception ex) { StatusMsg.Text = $"点落到面上：写出失败 {ex.Message}"; return; }
        BeginChange();
        foreach (var (x, y, _) in draped) _scene.Add(new PointEntity { X = x, Y = y, Cr = 0.3f, Cg = 0.85f, Cb = 0.95f });
        RefreshScene();
        StatusMsg.Text = $"点落到面上：{draped.Count} 点投影(未命中 {missed}) → {System.IO.Path.GetFileName(outPath)}(x,y,z)";
    }

    // 线落到面上：选网格 OFF + 线 CSV(lineId,x,y) → 逐顶点重心插值取 Z → 落 .draped.csv(lineId,x,y,z) + 线入场景
    private async Task ProjectPolylinesToMeshAsync()
    {
        var mf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "线落到面上：① 选网格 OFF", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (mf.Count == 0) return;
        var (v, t) = ReadMeshFlat(mf[0].Path.LocalPath);
        if (v == null || t == null) { StatusMsg.Text = "线落到面上：网格无三角"; return; }
        var lf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "线落到面上：② 选线 CSV (lineId,x,y)", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("线 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (lf.Count == 0) return;
        var evo = ReadEvoLines(lf[0].Path.LocalPath);   // 复用分组(lineId,x,y[,z]) → EvoLine
        if (evo.Count == 0) { StatusMsg.Text = "线落到面上：未解析到线(需 lineId,x,y, 每线≥2点)"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("lineId,x,y,z\n");
        int totV = 0, missed = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        BeginChange();
        foreach (var ln in evo)
        {
            var xy = new List<(double x, double y)>();
            foreach (var p in ln.Centerline) xy.Add((p.X, p.Y));
            var (draped, miss) = MeshProjector.Drape(v, t, xy);
            missed += miss; totV += draped.Count;
            var pl = new PolylineEntity { Cr = 0.35f, Cg = 0.9f, Cb = 0.55f };
            foreach (var (x, y, z) in draped)
            {
                pl.Points.Add((x, y));
                sb.Append(ln.Id).Append(',').Append(x.ToString("R", inv)).Append(',').Append(y.ToString("R", inv)).Append(',').Append(z.ToString("R", inv)).Append('\n');
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
            _scene.Add(pl);
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        string outPath = System.IO.Path.ChangeExtension(lf[0].Path.LocalPath, ".draped.csv");
        try { System.IO.File.WriteAllText(outPath, sb.ToString()); } catch (System.Exception ex) { StatusMsg.Text = $"线落到面上：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"线落到面上：{evo.Count} 线·{totV} 顶点投影(未命中 {missed}) → {System.IO.Path.GetFileName(outPath)}";
    }

    // 平盘宽度识别(现场参数提取)：读台阶线 CSV(lineId,x,y,z) → 圈出宽度 ≥ 目标 的平盘 → 多边形入场景
    private async Task BenchWidthAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "平盘宽度识别：选台阶线 CSV (lineId,x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"平盘宽度识别：读取失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var groups = new Dictionary<string, List<double>>();
        var order = new List<string>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var t = s.Split(new[] { ',', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out double x)) continue;   // 跳表头
            if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out double y)) continue;
            if (!double.TryParse(t[3], System.Globalization.NumberStyles.Float, inv, out double z)) continue;
            string id = t[0];
            if (!groups.TryGetValue(id, out var list)) { list = new List<double>(); groups[id] = list; order.Add(id); }
            list.Add(x); list.Add(y); list.Add(z);
        }
        var lines = new List<double[]>();
        foreach (var id in order) if (groups[id].Count >= 6) lines.Add(groups[id].ToArray());
        if (lines.Count == 0) { StatusMsg.Text = "平盘宽度识别：未解析到台阶线(需 lineId,x,y,z, 每线 ≥2 点)"; return; }
        const double wTarget = 20.0;   // 目标平盘宽度默认 20m(典型工作平盘); 本环境无参数对话框, 取此默认
        var r = BenchWidthIdentifier.Identify(lines, wTarget);
        if (!r.Ok) { StatusMsg.Text = $"平盘宽度识别：{r.Message}"; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        BeginChange();
        foreach (var reg in r.Regions)
        {
            var pl = new PolylineEntity { Closed = true, Cr = 0.3f, Cg = 0.85f, Cb = 0.95f };   // 青色达标平盘
            foreach (var (x, y) in reg.Polygon)
            {
                pl.Points.Add((x, y));
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
            _scene.Add(pl);
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        StatusMsg.Text = $"平盘宽度识别(≥{wTarget:0.#}m)：{r.Message}";
    }

    // 网格诊断：OFF 网格 → 边界边/非流形边/退化三角/洞数/是否闭合 报表
    private async Task MeshDiagnoseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "网格诊断：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格诊断：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "网格诊断：未解析到三角网格"; return; }
        var d = MeshDiagnose.Analyze(verts, tris);
        StatusMsg.Text = $"网格诊断：{d.TriangleCount} 三角 · {d.EdgeCount} 边 · 边界边 {d.BoundaryEdges} · 非流形边 {d.NonManifoldEdges} · 退化三角 {d.DegenerateTriangles} · 洞 {d.BoundaryLoops} · {(d.IsClosed ? "闭合(水密)" : "非闭合")}";
    }

    // 网格度量：OFF 网格 → 表面积/体积/包围盒 报表
    private async Task MeshMetricsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "网格度量：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格度量：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (verts.Count == 0 || tris.Count == 0) { StatusMsg.Text = "网格度量：未解析到三角网格"; return; }
        var m = MeshMetrics.Compute(verts, tris);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"网格度量：{m.VertexCount} 顶点 · {m.TriangleCount} 三角 · 表面积 {m.SurfaceArea.ToString("0.##", inv)} · 体积 {m.Volume.ToString("0.##", inv)} · 范围 X[{m.MinX.ToString("0.#", inv)},{m.MaxX.ToString("0.#", inv)}] Z[{m.MinZ.ToString("0.#", inv)},{m.MaxZ.ToString("0.#", inv)}]";
    }

    // 点云高程着色：点 CSV(x,y,z) → 按 z 用地形色带着色 → 彩色点入场景
    private async Task ElevationColorAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "点云高程着色：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"高程着色：点导入失败 {r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "高程着色：无点"; return; }

        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var p in r.Points) { if (p.z < zmin) zmin = p.z; if (p.z > zmax) zmax = p.z; }
        double range = zmax - zmin;
        BeginChange();
        foreach (var p in r.Points)
        {
            double t = range > 1e-9 ? (p.z - zmin) / range : 0.5;
            var (cr, cg, cb) = Colormap.Sample(Colormap.Terrain, t);
            var pe = new PointEntity { X = p.x, Y = p.y };
            AssignLayer(pe);
            pe.Cr = cr / 255f; pe.Cg = cg / 255f; pe.Cb = cb / 255f;
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"高程着色：{r.Points.Count} 点 · z {zmin.ToString("0.#", inv)}~{zmax.ToString("0.#", inv)}（地形色带）";
    }

    // 逐点坡度坡向 / 法向估计：点 CSV(x,y,z) → k 近邻 PCA 逐点法向 → 坡度配色点(绿平→红陡)入场景 + 报表
    private async Task PointNormalsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "逐点坡度/坡向：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"逐点坡度坡向：导入失败 {r.Error}"; return; }
        if (r.Points.Count < 3) { StatusMsg.Text = "逐点坡度坡向：点太少(≥3)"; return; }
        var pts = new List<(double x, double y, double z)>(); foreach (var p in r.Points) pts.Add((p.x, p.y, p.z));
        var attrs = PointNormals.Compute(pts, 12);
        if (attrs.Count == 0) { StatusMsg.Text = "逐点坡度坡向：计算失败"; return; }
        double smin = double.MaxValue, smax = double.MinValue, ssum = 0;
        foreach (var a in attrs) { if (a.slope < smin) smin = a.slope; if (a.slope > smax) smax = a.slope; ssum += a.slope; }
        BeginChange();
        for (int i = 0; i < pts.Count; i++)
        {
            double f = System.Math.Min(1.0, attrs[i].slope / 60.0);   // 0..60° 映射满量程(绿→红)
            _scene.Add(new PointEntity { X = pts[i].x, Y = pts[i].y, Cr = (float)f, Cg = (float)(1 - f) * 0.85f, Cb = 0.25f });
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"逐点坡度坡向(k=12 PCA)：{pts.Count} 点 · 坡度 {smin.ToString("0.#", inv)}~{smax.ToString("0.#", inv)}° · 均 {(ssum / attrs.Count).ToString("0.#", inv)}°（绿平→红陡）";
    }

    // 高程截断：点 CSV(x,y,z) → 剔除 Z 异常高/低程点(保留 [p2,p98] 波段) → 保留点入场景 + 报表
    private async Task ElevationClipAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "高程截断：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"高程截断：导入失败 {r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "高程截断：无点"; return; }
        var pts = new List<(double x, double y, double z)>(); foreach (var p in r.Points) pts.Add((p.x, p.y, p.z));
        var (kept, zLo, zHi, removed) = PointZClip.Clip(pts, 0.02, 0.98);   // 剔除极端 2% 高/低程
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var p in kept) { if (p.z < zmin) zmin = p.z; if (p.z > zmax) zmax = p.z; }
        double range = zmax - zmin;
        BeginChange();
        foreach (var p in kept)
        {
            double t = range > 1e-9 ? (p.z - zmin) / range : 0.5;
            var (cr, cg, cb) = Colormap.Sample(Colormap.Terrain, t);
            _scene.Add(new PointEntity { X = p.x, Y = p.y, Cr = cr / 255f, Cg = cg / 255f, Cb = cb / 255f });
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"高程截断：保留 {kept.Count}/{pts.Count} 点(剔除 {removed} 异常程) · Z 波段 [{zLo.ToString("0.#", inv)}, {zHi.ToString("0.#", inv)}]（地形色带）";
    }

    // 点云边界裁剪：选中闭合多段线作边界 → 导入点 CSV → 保留界内点入场景。忠实原「闭合多段线裁剪点云」。
    private async Task CropCloudByBoundaryAsync()
    {
        PolylineEntity? bnd = null;
        foreach (var e in _selected) if (e is PolylineEntity p && p.Closed && p.Points.Count >= 3) { bnd = p; break; }
        if (bnd == null) { StatusMsg.Text = "点云裁剪：请先选中一条闭合多段线作裁剪边界"; return; }
        var boundary = new List<(double x, double y)>(bnd.Points);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "点云裁剪：选点 CSV (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"点云裁剪：导入失败 {r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "点云裁剪：无点"; return; }
        var pts = new List<(double x, double y, double z)>(); foreach (var p in r.Points) pts.Add((p.x, p.y, p.z));
        var kept = PointCloudCrop.ByPolygon(pts, boundary, keepInside: true);
        if (kept.Count == 0) { StatusMsg.Text = $"点云裁剪：边界内无点（共 {pts.Count} 点全在界外）"; return; }
        BeginChange();
        foreach (var p in kept)
            _scene.Add(new PointEntity { X = p.x, Y = p.y, Cr = 0.35f, Cg = 0.8f, Cb = 0.5f, LayerName = "点云裁剪" });
        RefreshScene();
        StatusMsg.Text = $"点云裁剪：保留边界内 {kept.Count}/{pts.Count} 点入场景（图层 点云裁剪）";
    }

    // 加载点云/展点：点 CSV(x,y[,z]) → 灰点入场景 + 范围缩放
    private async Task LoadPointCloudAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "加载点云：选点 CSV (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"加载点云：导入失败 {r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "加载点云：无点"; return; }
        BeginChange();
        foreach (var p in r.Points)
        {
            var pe = new PointEntity { X = p.x, Y = p.y, Cr = 0.75f, Cg = 0.78f, Cb = 0.82f };
            AssignLayer(pe); pe.Cr = 0.75f; pe.Cg = 0.78f; pe.Cb = 0.82f;
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"加载点云：{r.Points.Count} 点已入场景（灰点；可着色/去噪/抽稀/统计）";
    }

    // 点云去噪 SOR/ROR：点 CSV(x,y,z) → 去噪 → 保留点入场景(黄) + 报表
    private async Task DenoiseAsync(bool ror)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = ror ? "ROR 去噪：选点 CSV (x,y,z)" : "SOR 去噪：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"去噪：点导入失败 {r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "去噪：无点"; return; }

        var pts = new List<(double x, double y, double z)>(r.Points.Count);
        foreach (var p in r.Points) pts.Add((p.x, p.y, p.z));
        // 默认参数（原去噪对话框默认口径）：SOR k=8 σ=1.0；ROR 半径=包围盒对角/50、下限=4
        double diag = System.Math.Sqrt(System.Math.Pow(r.Bounds[2] - r.Bounds[0], 2) + System.Math.Pow(r.Bounds[3] - r.Bounds[1], 2));
        var kept = ror
            ? PointDenoise.Ror(pts, System.Math.Max(diag / 50.0, 1e-6), 4)
            : PointDenoise.Sor(pts, 8, 1.0);
        if (kept.Count == 0) { StatusMsg.Text = "去噪：全部被剔除（参数过严）"; return; }

        BeginChange();
        foreach (var p in kept) { var pe = new PointEntity { X = p.x, Y = p.y, Cr = 0.95f, Cg = 0.85f, Cb = 0.3f }; AssignLayer(pe); pe.Cr = 0.95f; pe.Cg = 0.85f; pe.Cb = 0.3f; _scene.Add(pe); }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"{(ror ? "ROR" : "SOR")} 去噪：{pts.Count} → 保留 {kept.Count}（剔除 {pts.Count - kept.Count}）";
    }

    // 点云质量统计：点 CSV(x,y,z) → 计数/包围盒/XY面积/密度/高程均值·标准差 报表
    private async Task PointCloudStatsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "点云质量统计：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"点云统计：点导入失败 {r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "点云统计：无点"; return; }

        var pts = new List<(double x, double y, double z)>(r.Points.Count);
        foreach (var p in r.Points) pts.Add((p.x, p.y, p.z));
        var s = PointCloudStats.Compute(pts);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"点云统计：{s.Count} 点 · 范围 X[{s.MinX.ToString("0.#", inv)},{s.MaxX.ToString("0.#", inv)}] Y[{s.MinY.ToString("0.#", inv)},{s.MaxY.ToString("0.#", inv)}] Z[{s.MinZ.ToString("0.#", inv)},{s.MaxZ.ToString("0.#", inv)}] · 面积 {s.AreaXY.ToString("0", inv)}m² · 密度 {s.DensityXY.ToString("0.###", inv)}点/m² · 高程 均值{s.MeanZ.ToString("0.##", inv)} σ{s.StdZ.ToString("0.##", inv)}";
    }

    // 车铲匹配：CSV(卡车数, 装车节拍min, 循环时间min) → 匹配系数 + Erlang-C 等待概率 + 结论
    private async Task FleetMatchAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "车铲匹配：选 CSV (卡车数, 装车节拍min, 循环时间min)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("车铲参数 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var rows = new List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var f = line.Split(new[] { ',', '\t', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3) continue;
                if (!int.TryParse(f[0].Trim(), out int trucks)) continue;
                if (!double.TryParse(f[1].Trim(), System.Globalization.NumberStyles.Any, inv, out double loadMin)) continue;
                if (!double.TryParse(f[2].Trim(), System.Globalization.NumberStyles.Any, inv, out double cycMin)) continue;
                double mf = FleetMatch.MatchFactor(trucks, loadMin, cycMin);
                double wait = FleetMatch.ErlangC(System.Math.Max(1, trucks), System.Math.Min(0.999, mf));
                rows.Add($"{trucks}车: MF {mf.ToString("0.##", inv)}({FleetMatch.Verdict(mf)}) 排队概率 {(wait * 100).ToString("0", inv)}%");
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"车铲匹配：读取失败 {ex.Message}"; return; }
        if (rows.Count == 0) { StatusMsg.Text = "车铲匹配：需每行 卡车数,装车节拍min,循环时间min"; return; }
        StatusMsg.Text = "车铲匹配  " + string.Join("  |  ", rows);
    }

    // 产量达成分析：生产记录 CSV(计划量,实际量,计划工时,实际工时[,故障h,检修h]) → 逐行分解→合并→报表
    private async Task AttainmentAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "达成分析：选生产记录 CSV (计划量,实际量,计划工时,实际工时[,故障h,检修h])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("生产记录 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var parts = new List<AttainmentBreakdown>();
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var f = line.Split(new[] { ',', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                var nums = new List<double>();
                foreach (var s in f)
                    if (double.TryParse(s.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)) nums.Add(v);
                if (nums.Count < 4) continue;   // 计划量,实际量,计划工时,实际工时[,故障,检修]
                parts.Add(AttainmentAnalyzer.Of(nums[0], nums[1], nums[2], nums[3],
                    nums.Count > 4 ? nums[4] : 0, nums.Count > 5 ? nums[5] : 0));
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"达成分析：读取失败 {ex.Message}"; return; }
        if (parts.Count == 0) { StatusMsg.Text = "达成分析：需每行 计划量,实际量,计划工时,实际工时[,故障h,检修h]"; return; }

        var b = AttainmentAnalyzer.Combine(parts);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string causes = b.Items.Count > 0
            ? " | 归因: " + string.Join(" · ", b.Items.Select(i => $"{i.Cause} {i.VolumeM3.ToString("0", inv)}"))
            : "";
        StatusMsg.Text = $"达成分析：{parts.Count} 条 · 计划 {b.PlanM3.ToString("0", inv)} 实际 {b.ActualM3.ToString("0", inv)} · 达成 {b.AttainPct.ToString("0", inv)}% · 缺口 {b.GapM3.ToString("0", inv)}(已解释 {b.ExplainedPct.ToString("0", inv)}%){causes}";
    }

    // 台阶参数分析：剖面 CSV(里程, 高程) → 分平盘/坡面段 → 台阶高/坡面角/平盘宽/整体帮坡角 报表
    private async Task BenchAnalyzeAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "台阶参数分析：选剖面 CSV (里程, 高程)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("剖面 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var dists = new List<double>(); var zs = new List<double>();
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ',', '\t', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dd)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double zz))
                { dists.Add(dd); zs.Add(zz); }
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"台阶分析：读取失败 {ex.Message}"; return; }
        if (dists.Count < 2) { StatusMsg.Text = "台阶分析：需 ≥2 个剖面点(里程,高程)"; return; }

        var res = BenchAnalyzer.Analyze(dists, zs);
        if (res.Rows.Count == 0) { StatusMsg.Text = "台阶分析：未识别出台阶段"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double maxH = 0, sumFaceAng = 0; int fc = 0;
        foreach (var r in res.Rows) if (r.Kind == "坡面") { if (r.Height > maxH) maxH = r.Height; sumFaceAng += r.FaceAngleDeg; fc++; }
        string avgAng = fc > 0 ? (sumFaceAng / fc).ToString("0.#", inv) : "—";
        StatusMsg.Text = $"台阶分析：{res.FaceCount} 坡面 · {res.BermCount} 平盘 · 最大台阶高 {maxH.ToString("0.##", inv)} · 平均坡面角 {avgAng}° · 整体帮坡角 {res.OverallSlopeDeg.ToString("0.#", inv)}°";
    }

    // 坡角估算：点集 CSV(x,y,z) → 最小二乘拟合平面 → 最陡坡角(工作帮坡角口径) + 报表
    private async Task SlopeEstimateAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "坡角估算：选面上点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("面点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"坡角估算：点导入失败 {r.Error}"; return; }
        if (r.Points.Count < 3) { StatusMsg.Text = "坡角估算：需 ≥3 个不共线点"; return; }

        var pts = new List<(double x, double y, double z)>(r.Points.Count);
        foreach (var p in r.Points) pts.Add((p.x, p.y, p.z));
        var deg = SlopeEstimator.MaxSlopeDeg(pts);
        if (deg == null) { StatusMsg.Text = "坡角估算：点近共线，拟合不出平面"; return; }
        StatusMsg.Text = $"坡角估算：{pts.Count} 点拟合平面 → 最陡坡角 ≈ {deg.Value:0.##}°";
    }

    // 煤质统计：CSV(可选 煤层标签, 指标值) → 按标签分组算 计数/均值/标准差/min/max/P25/50/75 → 报表
    private async Task QualityStatsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "煤质统计：选指标 CSV (可选 煤层, 指标值)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("煤质指标 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var groups = new Dictionary<string, List<double>>();
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ',', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                // 末字段为指标值；若前面还有非数值字段, 取第一个作煤层标签
                if (!double.TryParse(parts[^1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
                string label = parts.Length >= 2 ? parts[0].Trim() : "全部";
                if (!groups.TryGetValue(label, out var lst)) { lst = new List<double>(); groups[label] = lst; }
                lst.Add(v);
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"煤质统计：读取失败 {ex.Message}"; return; }
        if (groups.Count == 0) { StatusMsg.Text = "煤质统计：无有效数值（每行 [煤层,] 指标值）"; return; }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var report = new List<string>();
        foreach (var kv in groups.OrderBy(k => k.Key))
        {
            var s = QualityStatistics.Compute(kv.Value);
            report.Add($"{kv.Key}: n={s.Count} 均值{s.Mean.ToString("0.##", inv)} σ{s.Std.ToString("0.##", inv)} [{s.Min.ToString("0.##", inv)}~{s.Max.ToString("0.##", inv)}] 中位{s.P50.ToString("0.##", inv)}");
        }
        StatusMsg.Text = "煤质统计  " + string.Join("  |  ", report);
    }

    // 工作面线拟合：露煤格中心 CSV(x,y) → PCA走向+趋势修正 → 折线段上屏 + 报表
    private async Task WorkingFaceLineAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "工作面线拟合：选露煤格中心 CSV (x,y)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("露煤点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"工作面线：点导入失败 {r.Error}"; return; }

        var pts = new List<(double X, double Y)>(r.Points.Count);
        foreach (var p in r.Points) pts.Add((p.x, p.y));
        var fit = WorkingFaceLineFitter.Fit(pts);
        if (!fit.Ok) { StatusMsg.Text = $"工作面线：{fit.Message}"; return; }

        BeginChange();
        foreach (var seg in fit.Segments)
        {
            var pl = new PolylineEntity { Cr = 0.95f, Cg = 0.55f, Cb = 0.2f };
            for (int i = 0; i + 1 < seg.Length; i += 2) pl.Points.Add((seg[i], seg[i + 1]));
            AssignLayer(pl); pl.Cr = 0.95f; pl.Cg = 0.55f; pl.Cb = 0.2f; _scene.Add(pl);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        string msg = fit.Message;
        if (fit.DroppedSpeckle > 0 || fit.PulledBack > 0 || fit.DroppedOutlier > 0)
            msg += $"（斑点丢 {fit.DroppedSpeckle} · 拉回 {fit.PulledBack} · 离群丢 {fit.DroppedOutlier}）";
        StatusMsg.Text = msg;
    }

    // 矿床识别：煤单元中心 CSV(x,y,z) → PCA 倾角/走向 + Z 层游程煤层数 → 走向线上屏 + 报告
    private async Task DepositDetectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "矿床识别：选煤单元中心 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("煤单元中心 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"矿床识别：点导入失败 {r.Error}"; return; }
        if (r.Points.Count < 8) { StatusMsg.Text = "矿床识别：煤单元中心需 ≥8 点"; return; }

        // Z 层厚：取相邻唯一 z 的中位间隔，退化用 z 幅度/10
        var zs = new List<double>();
        foreach (var p in r.Points) zs.Add(p.z);
        zs.Sort();
        var gaps = new List<double>();
        for (int i = 1; i < zs.Count; i++) { double g = zs[i] - zs[i - 1]; if (g > 1e-6) gaps.Add(g); }
        double zLayer;
        if (gaps.Count > 0) { gaps.Sort(); zLayer = gaps[gaps.Count / 2]; }
        else zLayer = System.Math.Max((zs[^1] - zs[0]) / 10.0, 1.0);

        var cells = new List<(double X, double Y, double Z)>(r.Points.Count);
        foreach (var p in r.Points) cells.Add((p.x, p.y, p.z));
        var sig = DepositAutoDetector.Detect(cells, zLayer);
        if (sig == null) { StatusMsg.Text = "矿床识别：煤单元过少或退化，识别不出"; return; }

        // 过质心画走向线（水平面内，方位角 az，长度=平面对角 0.6）
        double cx = 0, cy = 0; foreach (var p in r.Points) { cx += p.x; cy += p.y; } cx /= r.Points.Count; cy /= r.Points.Count;
        double diag = System.Math.Sqrt(System.Math.Pow(r.Bounds[2] - r.Bounds[0], 2) + System.Math.Pow(r.Bounds[3] - r.Bounds[1], 2));
        double half = System.Math.Max(diag * 0.3, 1e-3);
        double azr = sig.Value.StrikeAzimuthDeg * System.Math.PI / 180.0;
        double dx = System.Math.Sin(azr), dy = System.Math.Cos(azr);   // 方位角(从 +Y 顺时针)→方向
        BeginChange();
        _scene.Add(new LineEntity { X0 = cx - dx * half, Y0 = cy - dy * half, X1 = cx + dx * half, Y1 = cy + dy * half, Cr = 0.95f, Cg = 0.4f, Cb = 0.85f });
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"矿床识别：倾角 {sig.Value.DipDeg:0.#}° · 走向 {sig.Value.StrikeAzimuthDeg:0.#}° · 煤层 {sig.Value.SeamCount} · 煤单元 {sig.Value.CoalCellCount}（Z 层厚 {zLayer:0.##}）";
    }

    // 方案综合对比：读方案指标 CSV → 多准则加权评分 → 排名 + 推荐 报表
    private async Task ProgramCompareAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "方案综合对比：选方案指标 CSV (名称,峰值剥采比,基建剥离,内排率,达产年,服务年限,储量均衡,NPV)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("方案指标 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var plans = new List<ProgramComparer.Plan>();
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ',', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8) continue;
                var nums = new double[7];
                bool ok = true;
                for (int k = 0; k < 7; k++)
                    if (!double.TryParse(parts[parts.Length - 7 + k].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out nums[k])) { ok = false; break; }
                if (!ok) continue;
                string name = parts.Length > 7 ? parts[0].Trim() : $"方案{plans.Count + 1}";
                plans.Add(new ProgramComparer.Plan(name, nums[0], nums[1], nums[2], nums[3], nums[4], nums[5], nums[6]));
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"方案对比：读取失败 {ex.Message}"; return; }
        if (plans.Count == 0) { StatusMsg.Text = "方案对比：需每行 名称+7 项指标(峰值剥采比,基建剥离,内排率,达产年,服务年限,储量均衡,NPV)"; return; }

        var (scored, best) = ProgramComparer.Score(plans);
        var ranked = scored.OrderByDescending(s => s.CompositeScore).ToList();
        string report = $"方案综合对比：{plans.Count} 套 · 推荐【{best}】 | " +
            string.Join(" · ", ranked.Select(s => $"{s.Name} {s.CompositeScore:0}分"));
        StatusMsg.Text = report;
    }

    // 创建三角网：散点 CSV → Delaunay → 三角边线框入场景
    // TRIMESH：生成示例三角网（确定性 6×6 网格点 → Delaunay → 三角边，复用已测 Delaunay，无需文件）
    private void GenerateSampleTrimesh()
    {
        var pts2d = new List<(double x, double y)>();
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
                pts2d.Add((i * 20.0, j * 20.0));
        var tris = Delaunay.Triangulate(pts2d);
        var edges = Delaunay.BuildEdges(pts2d, tris, 0.55f, 0.75f, 0.85f);
        if (edges.Count == 0) { StatusMsg.Text = "示例三角网：生成失败"; return; }
        BeginChange();
        foreach (var e in edges) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(new double[] { 0, 0, 100, 100 });
        StatusMsg.Text = $"示例三角网：{pts2d.Count} 点 → {tris.Count} 三角 · {edges.Count} 边";
    }

    private async Task CreateTinAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "创建三角网：选点 CSV (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"三角网：点导入失败 {r.Error}"; return; }
        var pts2d = new List<(double x, double y)>();
        foreach (var p in r.Points) pts2d.Add((p.x, p.y));
        var tris = Delaunay.Triangulate(pts2d);
        if (tris.Count == 0) { StatusMsg.Text = "三角网：点太少或共线，无法剖分"; return; }
        var edges = Delaunay.BuildEdges(pts2d, tris, 0.55f, 0.75f, 0.85f);
        BeginChange();
        foreach (var e in edges) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"创建三角网：{pts2d.Count} 点 → {tris.Count} 三角 · {edges.Count} 边";
    }

    // 约束三角网(breakline 嵌入)：点 CSV + 选中的多段线作约束边(断层/山脊等必为三角边)。忠实原「多段线约束嵌入」。
    private async Task CreateConstrainedTinAsync()
    {
        // 先收集选中的多段线作 breakline(取点前捕获选择)
        var bkPolys = new List<PolylineEntity>();
        foreach (var e in _selected) if (e is PolylineEntity p && p.Points.Count >= 2) bkPolys.Add(p);
        if (bkPolys.Count == 0) { StatusMsg.Text = "约束三角网：请先选中 ≥1 条多段线作约束线(断层/山脊 breakline)，再执行；无约束请用 创建三角网"; return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "约束三角网：选点 CSV (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"约束三角网：点导入失败 {r.Error}"; return; }

        var pts2d = new List<(double x, double y)>();
        foreach (var p in r.Points) pts2d.Add((p.x, p.y));
        // 把约束线顶点并入点集，其相邻段成约束边
        var constraints = new List<(int u, int v)>();
        foreach (var pl in bkPolys)
        {
            int first = -1, prev = -1;
            foreach (var (vx, vy) in pl.Points)
            {
                pts2d.Add((vx, vy)); int idx = pts2d.Count - 1;
                if (prev >= 0) constraints.Add((prev, idx));
                if (first < 0) first = idx;
                prev = idx;
            }
            if (pl.Closed && first >= 0 && prev != first) constraints.Add((prev, first));
        }
        if (pts2d.Count < 3) { StatusMsg.Text = "约束三角网：点太少"; return; }

        var tris = Delaunay.TriangulateConstrained(pts2d, constraints);
        if (tris.Count == 0) { StatusMsg.Text = "约束三角网：点太少或共线，无法剖分"; return; }
        int kept = 0; foreach (var (u, v) in constraints) if (ConstraintHeld(tris, u, v, pts2d)) kept++;
        var edges = Delaunay.BuildEdges(pts2d, tris, 0.85f, 0.6f, 0.35f);   // 约束网偏暖色区别
        BeginChange();
        foreach (var e in edges) _scene.Add(e);
        RefreshScene();
        StatusMsg.Text = $"约束三角网：{pts2d.Count} 点 · {bkPolys.Count} 约束线 → {tris.Count} 三角 · {edges.Count} 边（约束段 {kept}/{constraints.Count} 已嵌入或分段）";
    }

    // 约束段是否体现在网中(直边或经共线分段成链)——粗判：端点间存在一条沿线的边路径。这里简化为直边或任一端相连。
    private static bool ConstraintHeld(List<(int a, int b, int c)> tris, int u, int v, List<(double x, double y)> pts)
    {
        foreach (var t in tris)
            foreach (var (p, q) in new[] { (t.a, t.b), (t.b, t.c), (t.c, t.a) })
                if ((p == u && q == v) || (p == v && q == u)) return true;
        return false;   // 分段链情形从简不深判(面积守恒已在单测锁定正确性)
    }

    // 体积/土方量：散点 CSV → 三角网 → 相对最低点体积（挖方/填方/净值），报状态栏
    private async Task VolumeAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "体积计算：选高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"体积计算：点导入失败 {r.Error}"; return; }
        var pts2d = new List<(double x, double y)>();
        double zmin = double.MaxValue;
        foreach (var p in r.Points) { pts2d.Add((p.x, p.y)); if (p.z < zmin) zmin = p.z; }
        var tris = Delaunay.Triangulate(pts2d);
        if (tris.Count == 0) { StatusMsg.Text = "体积计算：点太少或共线"; return; }
        var (above, below, net) = TerrainAnalysis.Volume(r.Points, tris, zmin);
        StatusMsg.Text = $"体积（基准=最低 z {zmin:0.##}）：上方 {above:0.##} · 下方 {below:0.##} · 净 {net:0.##}（{tris.Count} 三角）";
    }

    // 提取道路中心线：选两条路边多段线 → 中点连成中心线
    private void ExtractCenterline()
    {
        var polys = _selected.FindAll(e => e is PolylineEntity);
        if (polys.Count != 2)
        { StatusMsg.Text = "提取道路中心线：请先选中两条路边多段线"; return; }
        var a = (PolylineEntity)polys[0]; var b = (PolylineEntity)polys[1];
        var mid = RoadTools.Centerline(a.Points, b.Points);
        if (mid.Count < 2) { StatusMsg.Text = "提取道路中心线：路边点数不足"; return; }
        var cl = new PolylineEntity { Cr = 0.95f, Cg = 0.85f, Cb = 0.30f };   // 黄色中心线
        foreach (var p in mid) cl.Points.Add(p);
        AssignLayer(cl); cl.Cr = 0.95f; cl.Cg = 0.85f; cl.Cb = 0.30f;         // 保中心线色
        BeginChange();
        _scene.Add(cl);
        RefreshScene();
        StatusMsg.Text = $"已提取道路中心线（{mid.Count} 点）";
    }

    // 排土条带：选中闭合多段线内按间距生成平行线条带
    private void DumpStrips()
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity poly || !poly.Closed || poly.Points.Count < 3)
        { StatusMsg.Text = "排土条带：请先选中一条闭合多段线作范围"; return; }
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in poly.Points) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
        double spacing = System.Math.Max((maxY - minY) / 20.0, 1e-6);   // 自动约 20 条
        var segs = Hatch.ParallelFill(poly.Points, spacing, 0);
        if (segs.Count == 0) { StatusMsg.Text = "排土条带：无填充（范围过小）"; return; }
        BeginChange();
        foreach (var s in segs)
            _scene.Add(new LineEntity { X0 = s.x0, Y0 = s.y0, X1 = s.x1, Y1 = s.y1, Cr = 0.80f, Cg = 0.60f, Cb = 0.35f });
        RefreshScene();
        StatusMsg.Text = $"排土条带：{segs.Count} 条（间距 {spacing:0.##}）";
    }

    // 块体模型：CSV(x,y,z[,尺寸,品位]) → 品位配色方块平面显示 + 统计
    private async Task ImportBlockModelAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "块体模型：选 CSV (x,y,z[,尺寸,品位])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("块体 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt", "*.blk" } } }
        });
        if (files.Count == 0) return;
        var r = BlockModel.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"块体导入失败：{r.Error}"; return; }
        _lastBlocks = r.Blocks;   // 供资源量估算
        _blockGmin = r.GradeMin; _blockGmax = r.GradeMax;
        BeginChange();
        RenderBlocks(r.Blocks);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"块体模型：{r.Blocks.Count} 块 · 品位 {r.GradeMin:0.##}~{r.GradeMax:0.##}(均 {r.GradeMean:0.##})";
    }

    // 剖面分析：选中剖面线(直线/多段线) + 地形高程点 CSV → 距离-高程剖面曲线
    private async Task SectionProfileAsync()
    {
        var section = new List<(double x, double y)>();
        if (_selected.Count == 1 && _selected[0] is PolylineEntity spl) section.AddRange(spl.Points);
        else if (_selected.Count == 1 && _selected[0] is LineEntity sl) { section.Add((sl.X0, sl.Y0)); section.Add((sl.X1, sl.Y1)); }
        else { StatusMsg.Text = "剖面分析：请先选中一条剖面线（直线/多段线）"; return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "剖面分析：选地形高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"剖面分析：点导入失败 {r.Error}"; return; }

        var prof = Profile.Sample(section, r.Points, 100);
        if (prof.Count < 2) { StatusMsg.Text = "剖面分析：采样失败"; return; }
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var p in prof) { if (p.z < zmin) zmin = p.z; if (p.z > zmax) zmax = p.z; }

        // 剖面曲线画在剖面线包围盒下方（X=沿线距离，Y=高程）
        double baseX = section[0].x, baseY = 0;
        foreach (var p in section) if (p.y < baseY || baseY == 0) baseY = p.y;
        baseY -= (zmax - zmin) + 10;
        var curve = new PolylineEntity { Cr = 0.30f, Cg = 0.90f, Cb = 0.50f };
        foreach (var (dist, z) in prof) curve.Points.Add((baseX + dist, baseY + (z - zmin)));
        BeginChange();
        _scene.Add(curve);
        RefreshScene();
        StatusMsg.Text = $"剖面分析：{prof.Count} 采样 · 高程 {zmin:0.##}~{zmax:0.##} · 剖面长 {prof[^1].dist:0.##}";
    }

    // C2C 点云比对：两期 XYZ → A 每点到 B 最近距离 → 按偏差配色点 + 报最大/平均偏差
    private async Task CloudCompareAsync()
    {
        var opt = new System.Func<string, FilePickerOpenOptions>(t => new FilePickerOpenOptions
        {
            Title = t, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        var f1 = await StorageProvider.OpenFilePickerAsync(opt("C2C 比对：选【当前】点云 CSV"));
        if (f1.Count == 0) return;
        var f2 = await StorageProvider.OpenFilePickerAsync(opt("C2C 比对：选【参考】点云 CSV"));
        if (f2.Count == 0) return;
        var ra = PointDataImportService.Load(f1[0].Path.LocalPath);
        var rb = PointDataImportService.Load(f2[0].Path.LocalPath);
        if (!ra.Success || !rb.Success) { StatusMsg.Text = "C2C：点导入失败"; return; }
        var dists = CloudCompare.Distances(ra.Points, rb.Points);
        var (max, mean) = CloudCompare.Stats(dists);
        BeginChange();
        for (int i = 0; i < ra.Points.Count; i++)
        {
            var (cr, cg, cb) = BlockModel.GradeColor(dists[i], 0, max);   // 蓝(近)→红(远)
            _scene.Add(new PointEntity { X = ra.Points[i].x, Y = ra.Points[i].y, Cr = cr, Cg = cg, Cb = cb });
        }
        RefreshScene();
        Viewport.FitBounds(ra.Bounds);
        StatusMsg.Text = $"C2C 比对：{ra.Points.Count} 点 · 最大偏差 {max:0.###} · 平均 {mean:0.###}";
    }

    // 地面点滤波：XYZ CSV → 每 XY 格取最低点(≈地面) → 点入场景
    private async Task GroundFilterAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "地面点滤波：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"地面点滤波：导入失败 {r.Error}"; return; }
        double span = System.Math.Max(r.Bounds[2] - r.Bounds[0], r.Bounds[3] - r.Bounds[1]);
        double cell = System.Math.Max(span / 80.0, 1e-6);
        var ground = GroundFilter.LowestPerCell(r.Points, cell);
        BeginChange();
        foreach (var (x, y, _) in ground) { var pt = new PointEntity { X = x, Y = y }; AssignLayer(pt); _scene.Add(pt); }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"地面点滤波：{r.Points.Count} → {ground.Count} 地面点（cell {cell:0.##}）";
    }

    // 点云抽稀：XYZ CSV → 体素抽稀 → 抽稀后点入场景 + 报压缩比
    private async Task ThinPointsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "点云抽稀：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"点云抽稀：导入失败 {r.Error}"; return; }
        double span = System.Math.Max(r.Bounds[2] - r.Bounds[0], r.Bounds[3] - r.Bounds[1]);
        double cell = System.Math.Max(span / 100.0, 1e-6);   // 约 100 格跨度
        var thinned = PointThin.Thin(r.Points, cell);
        BeginChange();
        foreach (var (x, y, _) in thinned) { var pt = new PointEntity { X = x, Y = y }; AssignLayer(pt); _scene.Add(pt); }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"点云抽稀：{r.Points.Count} → {thinned.Count} 点（cell {cell:0.##}，压缩 {100.0 * (1 - (double)thinned.Count / r.Points.Count):0.#}%）";
    }

    // 曲率：地形 CSV → IDW 网格 → 拉普拉斯曲率 → 配色格(蓝凸/红凹)
    private async Task CurvatureAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "曲率：选高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"曲率：导入失败 {r.Error}"; return; }
        int n = 48;
        var grid = Contour.GridFromPoints(r.Points, n, n, out double gx0, out double gy0, out double gdx, out double gdy);
        var curv = Curvature.Compute(grid, gdx);
        var (min, max) = Estimation.Range(curv);
        var cells = Estimation.BuildCells(curv, gx0, gy0, gdx, gdy, min, max);
        BeginChange();
        foreach (var e in cells) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"曲率：{n}² 网格 · 范围 {min:0.###}~{max:0.###}（蓝=凸脊 红=凹沟）";
    }

    // 等效运距：场景多段线建路网 + 运输任务 CSV(fromX,fromY,toX,toY,吨位) → 吨位加权平均运距
    private async Task HaulMetricsAsync()
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "等效运距：场景无路网（多段线）"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "等效运距：选运输任务 CSV (fromX,fromY,toX,toY,吨位)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("运输任务 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = RoadNetwork.Build(polys, tol);
        double totalTon = 0, totalTonDist = 0; int ok = 0, skip = 0;
        foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
        {
            var t = raw.Split(new[] { ',', '\t', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 5) continue;
            if (!(double.TryParse(t[0], out double fx) && double.TryParse(t[1], out double fy)
                && double.TryParse(t[2], out double tx) && double.TryParse(t[3], out double ty)
                && double.TryParse(t[4], out double ton))) continue;
            var path = RoadNetwork.Dijkstra(adj, RoadNetwork.NearestNode(nodes, fx, fy), RoadNetwork.NearestNode(nodes, tx, ty));
            if (path.Count < 2) { skip++; continue; }
            totalTon += ton; totalTonDist += ton * RoadNetwork.PathLength(nodes, path); ok++;
        }
        if (ok == 0) { StatusMsg.Text = "等效运距：无可达任务（检查路网/任务坐标）"; return; }
        StatusMsg.Text = $"等效运距：{ok} 任务 · 吨公里 {totalTonDist:0.#} · 等效运距 {totalTonDist / totalTon:0.###}（跳过 {skip} 不可达）";
    }

    // 高程查询：载入地形高程点，进入点击查询模式
    private async Task StartSpotQueryAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "高程查询：选地形高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"高程查询：导入失败 {r.Error}"; return; }
        _spotTerrain = r.Points; _spotActive = true;
        _tool = null; _measure = null; _editMode = EditMode.None;
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"高程查询：已载入 {r.Points.Count} 点，点击视口任意位置查询高程（ESC 退出）";
    }

    // 导出块体：最近块体 → CSV(x,y,z,尺寸,品位)
    private async Task ExportBlocksAsync()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "导出块体：请先导入/生成块体"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出块体", DefaultExtension = "csv", SuggestedFileName = "blocks.csv",
            FileTypeChoices = new[] { new FilePickerFileType("块体 CSV") { Patterns = new[] { "*.csv" } } }
        });
        if (file == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("x,y,z,size,grade\n");
        foreach (var b in _lastBlocks)
            sb.Append(b.X.ToString("R", inv)).Append(',').Append(b.Y.ToString("R", inv)).Append(',').Append(b.Z.ToString("R", inv))
              .Append(',').Append(b.Size.ToString("R", inv)).Append(',').Append(b.Grade.ToString("R", inv)).Append('\n');
        try { System.IO.File.WriteAllText(file.Path.LocalPath, sb.ToString()); }
        catch (System.Exception ex) { StatusMsg.Text = $"导出块体：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"导出块体：{_lastBlocks.Count} 块 → {System.IO.Path.GetFileName(file.Path.LocalPath)}";
    }

    // 输出报告：最近块体资源量/剥采比 → 文本报告文件
    private async Task ExportResourceReportAsync()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "输出报告：请先导入/生成块体"; return; }
        double gsum = 0, gmin = double.MaxValue, gmax = double.MinValue;
        foreach (var b in _lastBlocks) { gsum += b.Grade; if (b.Grade < gmin) gmin = b.Grade; if (b.Grade > gmax) gmax = b.Grade; }
        double cut = gsum / _lastBlocks.Count;
        var (ore, waste, strip, avg, metal, tonnage) = BlockModel.Resource(_lastBlocks, cut, 2.7);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "输出资源量报告", DefaultExtension = "txt", SuggestedFileName = "resource_report.txt",
            FileTypeChoices = new[] { new FilePickerFileType("报告 (TXT/CSV)") { Patterns = new[] { "*.txt", "*.csv" } } }
        });
        if (file == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string txt = $"资源量报告\n块体数,{_lastBlocks.Count}\ncutoff(平均品位),{cut.ToString("0.###", inv)}\n" +
                     $"品位范围,{gmin.ToString("0.###", inv)}~{gmax.ToString("0.###", inv)}\n矿量(体积),{ore.ToString("0.#", inv)}\n" +
                     $"吨位,{tonnage.ToString("0.#", inv)}\n废石(体积),{waste.ToString("0.#", inv)}\n剥采比,{strip.ToString("0.##", inv)}\n" +
                     $"平均品位,{avg.ToString("0.###", inv)}\n金属量,{metal.ToString("0.#", inv)}\n";
        try { System.IO.File.WriteAllText(file.Path.LocalPath, txt); }
        catch (System.Exception ex) { StatusMsg.Text = $"输出报告：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"输出报告：资源量报告已保存 → {System.IO.Path.GetFileName(file.Path.LocalPath)}";
    }

    // 块体着色：按品位配色重渲全部块体(恢复全显)
    private void ColorBlocksCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "块体着色：请先导入/生成块体"; return; }
        BeginChange(); RenderBlocks(_lastBlocks); RefreshScene();
        StatusMsg.Text = $"块体着色：{_lastBlocks.Count} 块按品位配色（蓝低→红高）";
    }

    // 筛选块体：只显示品位 ≥ 平均品位 的块(矿块)
    private void FilterBlocksCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "筛选块体：请先导入/生成块体"; return; }
        double gsum = 0; foreach (var b in _lastBlocks) gsum += b.Grade; double cutoff = gsum / _lastBlocks.Count;
        var sub = _lastBlocks.Where(b => b.Grade >= cutoff).ToList();
        BeginChange(); RenderBlocks(sub); RefreshScene();
        StatusMsg.Text = $"筛选块体：品位≥{cutoff:0.###} → 显示 {sub.Count}/{_lastBlocks.Count} 块（块体着色 恢复全显）";
    }

    // 约束块体：只保留(显示)落在选中闭合多段线内的块
    private void ConstrainBlocksCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "约束块体：请先导入/生成块体"; return; }
        PolylineEntity? bnd = null;
        foreach (var e in _selected) if (e is PolylineEntity p && p.Closed && p.Points.Count >= 3) { bnd = p; break; }
        if (bnd == null) { StatusMsg.Text = "约束块体：请先选中一条闭合多段线作约束边界"; return; }
        var sub = _lastBlocks.Where(b => LineMath.PointInPolygon(b.X, b.Y, bnd.Points)).ToList();
        if (sub.Count == 0) { StatusMsg.Text = "约束块体：边界内无块体"; return; }
        BeginChange(); RenderBlocks(sub); RefreshScene();
        StatusMsg.Text = $"约束块体：边界内 {sub.Count}/{_lastBlocks.Count} 块（块体着色 恢复全显）";
    }

    // 删除块体：移除全部块体方块 + 清工作集
    private void DeleteBlocksCmd()
    {
        if (_blockCellEntities.Count == 0 && (_lastBlocks == null || _lastBlocks.Count == 0)) { StatusMsg.Text = "删除块体：无块体"; return; }
        BeginChange();
        foreach (var e in _blockCellEntities) _scene.Remove(e);
        _blockCellEntities.Clear(); _lastBlocks = null;
        RefreshScene();
        StatusMsg.Text = "已删除全部块体";
    }

    // 切面剖切：只显示中心落在 选中直线/多段线 一个块宽带内的块(沿线剖面切片)
    private void SectionBlocksCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "切面剖切：请先导入/生成块体"; return; }
        List<(double x, double y)>? line = null;
        foreach (var e in _selected)
        {
            if (e is LineEntity l) { line = new() { (l.X0, l.Y0), (l.X1, l.Y1) }; break; }
            if (e is PolylineEntity p && p.Points.Count >= 2) { line = new List<(double x, double y)>(p.Points); break; }
        }
        if (line == null) { StatusMsg.Text = "切面剖切：请先选中一条直线/多段线作剖切线"; return; }
        double band = _lastBlocks[0].Size;
        var sub = _lastBlocks.Where(b => PtToPolylineDist(b.X, b.Y, line) <= band).ToList();
        if (sub.Count == 0) { StatusMsg.Text = "切面剖切：剖切线附近无块体"; return; }
        BeginChange(); RenderBlocks(sub); RefreshScene();
        StatusMsg.Text = $"切面剖切：沿线切片 {sub.Count}/{_lastBlocks.Count} 块（带宽 {band:0.##}；块体着色 恢复全显）";
    }

    // 点到折线最近距离(逐段点-段距)
    private static double PtToPolylineDist(double px, double py, IReadOnlyList<(double x, double y)> line)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i]; var b = line[i + 1];
            double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : ((px - a.x) * dx + (py - a.y) * dy) / l2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = a.x + t * dx, qy = a.y + t * dy, ex = px - qx, ey = py - qy;
            double d = System.Math.Sqrt(ex * ex + ey * ey);
            if (d < best) best = d;
        }
        return best;
    }

    // 多边形圈选：以选中的闭合多段线为边界，选中其内实体
    private void PolygonSelect(bool crossing)
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity bnd || !bnd.Closed || bnd.Points.Count < 3)
        { StatusMsg.Text = "圈选：请先选中一条闭合多段线作边界"; return; }
        var poly = bnd.Points;
        SaveSel();
        var picked = new List<SceneEntity>();
        foreach (var en in _scene.Entities)
        {
            if (ReferenceEquals(en, bnd)) continue;
            if (!_layers.IsSelectable(en.LayerName)) continue;
            if (SelectionBox.MatchPolygon(en, poly, crossing)) picked.Add(en);
        }
        _selected.Clear();
        _selected.AddRange(picked);
        HighlightSelection();
        StatusMsg.Text = $"圈选 {_selected.Count} 个（{(crossing ? "交叉" : "窗口")}，边界内）";
    }

    // 多段线简化：Douglas-Peucker 减顶点保形
    private void SimplifyPolyline()
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity pl || pl.Points.Count < 3)
        { StatusMsg.Text = "简化：请先选中一条至少 3 点的多段线"; return; }
        double eps = System.Math.Max(SnapTolWorld(_lastPointer) * 0.5, 1e-6);
        var simp = PolylineSimplify.DouglasPeucker(pl.Points, eps);
        var np = new PolylineEntity { Closed = pl.Closed, Cr = pl.Cr, Cg = pl.Cg, Cb = pl.Cb, LayerName = pl.LayerName };
        foreach (var p in simp) np.Points.Add(p);
        BeginChange();
        _scene.Replace(pl, np);
        _selected.Clear(); _selected.Add(np);
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"多段线简化：{pl.Points.Count} → {np.Points.Count} 点（容差 {eps:0.##}）";
    }

    // 曲线平滑：对选中多段线做 Chaikin 平滑替换
    private void SmoothPolyline()
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity pl || pl.Points.Count < 3)
        { StatusMsg.Text = "平滑：请先选中一条至少 3 点的多段线"; return; }
        var sm = PolylineSmooth.Chaikin(pl.Points, 3, pl.Closed);
        var np = new PolylineEntity { Closed = pl.Closed, Cr = pl.Cr, Cg = pl.Cg, Cb = pl.Cb, LayerName = pl.LayerName };
        foreach (var p in sm) np.Points.Add(p);
        BeginChange();
        _scene.Replace(pl, np);
        _selected.Clear(); _selected.Add(np);
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"曲线平滑：{pl.Points.Count} → {np.Points.Count} 点（Chaikin×3）";
    }

    // 多边形裁剪：选两条多段线(第1=被裁, 第2=凸裁剪边界)→交集
    private void ClipPolygon()
    {
        var polys = _selected.FindAll(e => e is PolylineEntity);
        if (polys.Count != 2) { StatusMsg.Text = "裁剪：请先选中两条多段线(第1=被裁, 第2=裁剪边界)"; return; }
        var subject = ((PolylineEntity)polys[0]).Points;
        var clipHull = GeomHull.ConvexHull(((PolylineEntity)polys[1]).Points);   // 边界取凸包保证凸+CCW
        var result = PolygonClip.Clip(subject, clipHull);
        if (result.Count < 3) { StatusMsg.Text = "裁剪：无交集"; return; }
        var pl = new PolylineEntity { Closed = true, Cr = 0.4f, Cg = 0.95f, Cb = 0.6f };
        foreach (var p in result) pl.Points.Add(p);
        BeginChange();
        _scene.Add(pl);
        RefreshScene();
        StatusMsg.Text = $"裁剪完成：交集 {result.Count} 顶点";
    }

    // 线性标注：取两点
    private void StartDim()
    {
        _dimActive = true; _dimP1 = null;
        _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "线性标注：指定第一点";
    }

    // 半径标注(DIMRADIAL)：需先选一个圆或弧
    private void StartDimRadial()
    {
        (double cx, double cy, double r)? c = _selected.Count == 1 ? _selected[0] switch
        {
            CircleEntity ce => (ce.Cx, ce.Cy, ce.Radius),
            ArcEntity ae => ArcMath.Circumcircle(ae.X1, ae.Y1, ae.X2, ae.Y2, ae.X3, ae.Y3) is { } v ? (v.Item1, v.Item2, v.Item3) : ((double, double, double)?)null,
            _ => null
        } : null;
        if (c == null) { StatusMsg.Text = "半径标注：请先选中一个圆或弧"; return; }
        _dimRadCircle = c; _dimRadActive = true;
        _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "半径标注：指定标注方向";
    }

    // 连续标注(DIMCONTINUE)：以上一条线性标注的第二点为起点链式接续
    private void StartDimContinue()
    {
        if (_lastDimP2 == null) { StatusMsg.Text = "连续标注：请先做一条线性标注"; return; }
        _dimActive = true; _dimP1 = _lastDimP2;
        _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "连续标注：指定下一点";
    }

    // 标注样式(DIM 变量)：无参显示当前值；「标注样式 <文字高> [小数位] [箭头比]」设置。文字高 0=自动(随缩放)。
    private void DimStyleCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length <= 1)
        {
            StatusMsg.Text = $"标注样式：文字高 {(_dimStyle.TextHeight > 0 ? _dimStyle.TextHeight.ToString("0.##") : "自动")} · 小数位 {_dimStyle.DecimalPlaces} · 箭头比 {_dimStyle.ArrowRatio:0.##}（设置：标注样式 <文字高> [小数位] [箭头比]，文字高 0=自动）";
            return;
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture; var fl = System.Globalization.NumberStyles.Float;
        if (double.TryParse(tk[1], fl, inv, out double h) && h >= 0) _dimStyle.TextHeight = h;
        if (tk.Length >= 3 && int.TryParse(tk[2], out int dec) && dec >= 0 && dec <= 8) _dimStyle.DecimalPlaces = dec;
        if (tk.Length >= 4 && double.TryParse(tk[3], fl, inv, out double ar) && ar > 0 && ar < 5) _dimStyle.ArrowRatio = ar;
        StatusMsg.Text = $"标注样式已设：文字高 {(_dimStyle.TextHeight > 0 ? _dimStyle.TextHeight.ToString("0.##") : "自动")} · 小数位 {_dimStyle.DecimalPlaces} · 箭头比 {_dimStyle.ArrowRatio:0.##}（影响新建标注）";
    }

    // 文字：进入模式，下一条命令行输入即文字内容
    private void ArmText()
    {
        _textActive = true; _tool = null; _measure = null;
        StatusMsg.Text = "文字：在命令行输入内容并回车（数字/符号/XYZM 可显，中文暂空）";
    }

    private void PlaceText(string content)
    {
        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        var c = Viewport.ScreenToWorld(w / 2, h / 2) ?? (0, 0);
        double height = System.Math.Max(SnapTolWorld(new Avalonia.Point(w / 2, h / 2)) * 3, 1e-3);
        var tx = new TextEntity { X = c.x, Y = c.y, Height = height, Text = content };
        AssignLayer(tx);
        BeginChange(); _scene.Add(tx); RefreshScene();
        StatusMsg.Text = $"已放置文字「{content}」（视口中心，字高 {height:0.##}）";
    }

    // 面积/周长：对选中的多段线(闭合优先)算面积+周长，报状态栏
    private void MeasureArea()
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity pl || pl.Points.Count < 3)
        { StatusMsg.Text = "面积：请先选中一条至少 3 点的多段线（闭合更准）"; return; }
        double area = GeomMeasure.Area(pl.Points);
        double peri = GeomMeasure.Perimeter(pl.Points, true);
        StatusMsg.Text = $"面积 {area:0.###} · 周长(闭合) {peri:0.###} · {pl.Points.Count} 顶点";
    }

    // 坐标转换：控制点对 CSV(srcX,srcY,dstX,dstY) → Helmert 4参 → 套用全场景
    private async Task CoordTransformAsync()
    {
        if (_scene.Count == 0) { StatusMsg.Text = "坐标转换：场景为空"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "坐标转换：选控制点对 CSV (srcX,srcY,dstX,dstY)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("控制点 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        var pairs = new List<(double sx, double sy, double dx, double dy)>();
        foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
        {
            var t = raw.Split(new[] { ',', '\t', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            if (double.TryParse(t[0], out double a) && double.TryParse(t[1], out double b2)
                && double.TryParse(t[2], out double c) && double.TryParse(t[3], out double d))
                pairs.Add((a, b2, c, d));
        }
        var h = CoordTransform.Solve(pairs);
        if (h == null) { StatusMsg.Text = "坐标转换：需≥2 对有效控制点(srcX,srcY,dstX,dstY)"; return; }
        var m = CoordTransform.ToAffine(h.Value);
        BeginChange();
        for (int i = 0; i < _scene.Entities.Count; i++) _scene.Entities[i] = _scene.Entities[i].Apply(m);
        _selected.Clear(); Viewport.SetHighlight(null);
        RefreshScene();
        double scale = System.Math.Sqrt(h.Value.a * h.Value.a + h.Value.b * h.Value.b);
        double rot = System.Math.Atan2(h.Value.b, h.Value.a) * 180 / System.Math.PI;
        StatusMsg.Text = $"坐标转换完成（{pairs.Count} 控制点）：缩放 {scale:0.####} · 旋转 {rot:0.##}° · 平移({h.Value.tx:0.##},{h.Value.ty:0.##})";
    }

    // 粗糙度：地形 CSV → IDW 网格 → 3×3 邻域极差 → 配色格
    private async Task RoughnessAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "粗糙度：选高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"粗糙度：导入失败 {r.Error}"; return; }
        int n = 48;
        var grid = Contour.GridFromPoints(r.Points, n, n, out double gx0, out double gy0, out double gdx, out double gdy);
        var rough = Roughness.Compute(grid);
        var (min, max) = Estimation.Range(rough);
        var cells = Estimation.BuildCells(rough, gx0, gy0, gdx, gdy, min, max);
        BeginChange();
        foreach (var e in cells) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"粗糙度：{n}² 网格 · 极差 {min:0.###}~{max:0.###}（红=粗糙）";
    }

    // 快速估值：品位样本 CSV(x,y,品位) → IDW/克里金 网格 → 品位配色估值面
    private async Task EstimateGradeAsync(bool kriging = false)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = (kriging ? "克里金估值" : "快速估值") + "：选品位样本 CSV (x,y,品位)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("样本 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"{(kriging ? "克里金估值" : "快速估值")}：样本导入失败 {r.Error}"; return; }
        int n = 48;
        double[,] grid; double gx0, gy0, gdx, gdy; double avgVar = 0; string extra = "";
        if (kriging)
        {
            grid = BuildKrigingGrid(r.Points, n, n, out gx0, out gy0, out gdx, out gdy, out avgVar);
            extra = $" · OK克里金 avg克里金方差 {avgVar:0.###}";
        }
        else
        {
            grid = Contour.GridFromPoints(r.Points, n, n, out gx0, out gy0, out gdx, out gdy);
        }
        var (min, max) = Estimation.Range(grid);
        var cells = Estimation.BuildCells(grid, gx0, gy0, gdx, gdy, min, max);
        BeginChange();
        foreach (var e in cells) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"{(kriging ? "克里金估值" : "快速估值")}：{r.Points.Count} 样本 → {n}² 网格 · 品位 {min:0.###}~{max:0.###}{extra}";
    }

    // OK 克里金网格：逐格用 OrdinaryKriging.EstimateAt 估值；半径外(null)回落 IDW 值免留洞；出平均克里金方差
    private static double[,] BuildKrigingGrid(IReadOnlyList<(double x, double y, double z)> pts, int nx, int ny,
        out double x0, out double y0, out double dx, out double dy, out double avgVar)
    {
        var idw = Contour.GridFromPoints(pts, nx, ny, out x0, out y0, out dx, out dy);   // 布局 + 半径外回落
        nx = idw.GetLength(0); ny = idw.GetLength(1);
        var cps = new List<OrdinaryKriging.ControlPoint>(pts.Count);
        foreach (var p in pts) cps.Add(new OrdinaryKriging.ControlPoint(p.x, p.y, 0, p.z));
        var vg = cps.Count > 0 ? OrdinaryKriging.FitVariogram(cps) : null;
        double varSum = 0; int varCnt = 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                var e = OrdinaryKriging.EstimateAt(cps, x0 + i * dx, y0 + j * dy, 0, 12, 0, vg);
                if (e != null) { idw[i, j] = e.Value.est; varSum += e.Value.variance; varCnt++; }
            }
        avgVar = varCnt > 0 ? varSum / varCnt : 0;
        return idw;
    }

    // 境界圈定：散点 CSV → 凸包 → 闭合边界多段线
    private async Task BoundaryHullAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "境界圈定：选点 CSV (x,y)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"境界圈定：点导入失败 {r.Error}"; return; }
        var pts2d = new List<(double x, double y)>();
        foreach (var p in r.Points) pts2d.Add((p.x, p.y));
        var hull = GeomHull.ConvexHull(pts2d);
        if (hull.Count < 3) { StatusMsg.Text = "境界圈定：点太少或共线"; return; }
        var pl = new PolylineEntity { Closed = true, Cr = 0.95f, Cg = 0.55f, Cb = 0.25f };
        foreach (var p in hull) pl.Points.Add(p);
        BeginChange();
        _scene.Add(pl);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"境界圈定：{r.Points.Count} 点 → 凸包 {hull.Count} 顶点";
    }

    // 资源量估算 / 剥采比：对最近导入的块体，按 cutoff 分矿废
    private void ResourceReport(double? cutoff)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "资源量：请先导入块体模型（块体模型命令）"; return; }
        double gsum = 0, gmin = double.MaxValue, gmax = double.MinValue;
        foreach (var b in _lastBlocks) { gsum += b.Grade; if (b.Grade < gmin) gmin = b.Grade; if (b.Grade > gmax) gmax = b.Grade; }
        double cut = cutoff ?? gsum / _lastBlocks.Count;   // 默认=平均品位
        var (ore, waste, strip, avg, metal, tonnage) = BlockModel.Resource(_lastBlocks, cut, 2.7);
        StatusMsg.Text = $"资源量(cutoff {cut:0.##})：矿量 {ore:0.#} 吨位 {tonnage:0.#} · 废 {waste:0.#} · 剥采比 {strip:0.##} · 平均品位 {avg:0.###} · 金属 {metal:0.#}";
    }

    // 采区划分：最近块体 → 剥采比场(品位阈值聚合) → 沿推进轴等煤量切 N 采区 → 采区矩形入场景 + 报表
    private void PanelSplitCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "采区划分：请先导入/生成块体（块体模型 / 实体转块体）"; return; }
        double gsum = 0; foreach (var b in _lastBlocks) gsum += b.Grade;
        double cutoff = gsum / _lastBlocks.Count;   // 默认阈值=平均品位(煤/岩判别)
        var blocks = _lastBlocks.Select(b => (b.X, b.Y, b.Z, b.Size, b.Grade)).ToList();
        var field = StripRatioField.FromBlocks(blocks, cutoff, 1.35);   // 煤密度 1.35 t/m³
        if (field == null) { StatusMsg.Text = "采区划分：剥采比场构建失败"; return; }
        var plan = new MiningPlanParams();   // 默认: ByLife·4采区·400万t/a·30年·内排·台阶12m·方位0
        var panels = PanelSplitter.Split(plan, field);
        if (panels.Count == 0) { StatusMsg.Text = "采区划分：未切出采区（检查块体范围/品位）"; return; }
        BeginChange();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int k = 0;
        foreach (var p in panels)
        {
            float t = panels.Count > 1 ? (float)k / (panels.Count - 1) : 0f;
            var rect = new RectEntity { X0 = p.MinX, Y0 = p.MinY, X1 = p.MaxX, Y1 = p.MaxY, Cr = t, Cg = 0.55f, Cb = 1f - t };
            _scene.Add(rect);
            if (p.MinX < minX) minX = p.MinX; if (p.MinY < minY) minY = p.MinY; if (p.MaxX > maxX) maxX = p.MaxX; if (p.MaxY > maxY) maxY = p.MaxY;
            k++;
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        double totCoal = panels.Sum(p => p.CoalWanT);
        var first = panels.OrderBy(p => p.Order).First();
        StatusMsg.Text = $"采区划分：{panels.Count} 采区(等煤量) · 总煤 {totCoal:0} 万t · 首采区剥采比 {first.StripRatio:0.##} · 服务 {first.ServiceLifeYears:0.#}a(蓝→红=开采序)";
    }

    // 规划计算：块体→剥采比场→采区划分→开采程序系统指标评价 报表
    private void ProgramEvaluateCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "规划计算：请先导入/生成块体"; return; }
        double gsum = 0; foreach (var b in _lastBlocks) gsum += b.Grade;
        double cutoff = gsum / _lastBlocks.Count;
        var blocks = _lastBlocks.Select(b => (b.X, b.Y, b.Z, b.Size, b.Grade)).ToList();
        var field = StripRatioField.FromBlocks(blocks, cutoff, 1.35);
        if (field == null) { StatusMsg.Text = "规划计算：剥采比场构建失败"; return; }
        var plan = new MiningPlanParams();
        var panels = PanelSplitter.Split(plan, field);
        if (panels.Count == 0) { StatusMsg.Text = "规划计算：未切出采区"; return; }
        var r = ProgramEvaluator.Evaluate(plan, panels);
        StatusMsg.Text = $"规划计算：{r.PanelCount} 采区 · 服务 {r.ServiceLifeYears:0.#}a · 峰值剥采比 {r.ProductionRatioPeak:0.##} · 储量均衡 {r.ReserveBalanceCoef:0.##} · 内排率 {r.InnerDumpPct:0}% · 基建剥离 {r.BasicStrippingYiM3:0.##}亿m³ · 平均运距 {r.AvgHaulKm:0.##}km · NPV {r.Npv:0}万元 · 校核{(r.Ok ? "通过" : "待校核")}";
    }

    // 确定境界·经济最优坑深：块体→逐 Z 层煤/岩剖面→净值最大定坑底 报表
    private void PitDepthCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "确定境界：请先导入/生成块体"; return; }
        double gsum = 0; foreach (var b in _lastBlocks) gsum += b.Grade;
        double cutoff = gsum / _lastBlocks.Count;
        var blocks = _lastBlocks.Select(b => (b.X, b.Y, b.Z, b.Size, b.Grade)).ToList();
        var prof = ResourceProfileLite.FromBlocks(blocks, cutoff, 1.35);
        if (prof == null) { StatusMsg.Text = "确定境界：资源剖面构建失败"; return; }
        // 经济口径: 单位煤净收益 (煤价-采煤成本)=300-80=220 元/t; 剥离成本 20 元/m³
        var r = SectionSolver.SolveDepth(prof, revenuePerCoalT: 220, stripCostPerM3: 20);
        double coalWan = r.CoalT / 1e4, wasteWan = r.WasteM3 / 1e4;
        StatusMsg.Text = $"确定境界(净值最大)：最优坑深 {r.DepthM:0.#}m(底层 k={r.BottomK}/{prof.Nz}) · 圈入煤 {coalWan:0.#}万t · 岩 {wasteWan:0.#}万m³ · 境界剥采比 {r.ContourSR:0.##} · 净值 {r.NetValueYuan / 1e4:0.#}万元";
    }

    // 派生计划方案：块体→场→按不同采区数/推进方位派生多方案→逐一评价→按 NPV 排名 报表
    private void DerivePlansCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "派生计划方案：请先导入/生成块体"; return; }
        double gsum = 0; foreach (var b in _lastBlocks) gsum += b.Grade;
        double cutoff = gsum / _lastBlocks.Count;
        var blocks = _lastBlocks.Select(b => (b.X, b.Y, b.Z, b.Size, b.Grade)).ToList();
        var field = StripRatioField.FromBlocks(blocks, cutoff, 1.35);
        if (field == null) { StatusMsg.Text = "派生计划方案：剥采比场构建失败"; return; }
        var variants = new List<(string label, ProgramResult r)>();
        foreach (int pc in new[] { 2, 3, 4, 5, 6 })
            foreach (double az in new[] { 0.0, 90.0 })
            {
                var plan = new MiningPlanParams { Split = SplitObjective.FixedN, PanelCount = pc, AdvanceAzimuthDeg = az };
                var panels = PanelSplitter.Split(plan, field);
                if (panels.Count == 0) continue;
                var r = ProgramEvaluator.Evaluate(plan, panels);
                variants.Add(($"{pc}采区/方位{az:0}", r));
            }
        if (variants.Count == 0) { StatusMsg.Text = "派生计划方案：未派生出可行方案"; return; }
        var ranked = variants.OrderByDescending(v => v.r.Npv).ToList();
        var top = ranked.Take(3).Select(v => $"{v.label}(NPV {v.r.Npv:0}·剥采比{v.r.ProductionRatioPeak:0.##}·均衡{v.r.ReserveBalanceCoef:0.##})");
        StatusMsg.Text = $"派生计划方案：{variants.Count} 方案 · 推荐 {ranked[0].label} · 前三: " + string.Join(" | ", top);
    }

    // 组合工作线：合并选中的多段线（端点相接连成一条）
    private void JoinPolylines()
    {
        var polys = _selected.FindAll(e => e is PolylineEntity);
        if (polys.Count < 2) { StatusMsg.Text = "组合工作线：请先选中至少两条多段线"; return; }
        var inputs = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var p in polys) inputs.Add(((PolylineEntity)p).Points);
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var merged = PolylineJoin.Join(inputs, tol);
        BeginChange();
        foreach (var p in polys) _scene.Remove(p);
        var first = (PolylineEntity)polys[0];
        foreach (var chain in merged)
        {
            var pl = new PolylineEntity { Cr = first.Cr, Cg = first.Cg, Cb = first.Cb, LayerName = first.LayerName };
            foreach (var pt in chain) pl.Points.Add(pt);
            _scene.Add(pl);
        }
        _selected.Clear(); Viewport.SetHighlight(null);
        RefreshScene();
        StatusMsg.Text = $"组合工作线：{polys.Count} 条 → {merged.Count} 条";
    }

    // 分帮扩帮：选中台阶线/多段线，点方向 → 批量平行偏移
    private void StartBench()
    {
        if (_selected.Count != 1 || _selected[0] is not (LineEntity or PolylineEntity))
        { StatusMsg.Text = "分帮扩帮：请先选中一条台阶线（直线/多段线）"; return; }
        _benchEntity = _selected[0]; _benchActive = true;
        _tool = null; _measure = null; _editMode = EditMode.None;
        _offsetActive = false; _trimActive = false; _breakActive = false; _pathActive = false;
        StatusMsg.Text = $"分帮扩帮：点一侧确定方向与步距（生成 {_benchCount} 条）";
    }

    // 点对点寻径：场景所有多段线建路网 → 两点最近节点 Dijkstra → 高亮路径
    private void StartPathfind()
    {
        _pathActive = true; _pathP1 = null;
        _tool = null; _measure = null; _editMode = EditMode.None;
        _offsetActive = false; _trimActive = false; _breakActive = false;
        StatusMsg.Text = "点对点寻径：点起点（路网 = 场景中的多段线）";
    }

    private void ComputePath((double x, double y) a, (double x, double y) b)
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "寻径：场景无路（多段线）"; return; }

        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = RoadNetwork.Build(polys, tol);
        int s = RoadNetwork.NearestNode(nodes, a.x, a.y);
        int g = RoadNetwork.NearestNode(nodes, b.x, b.y);
        var path = RoadNetwork.Dijkstra(adj, s, g);
        if (path.Count < 2) { StatusMsg.Text = "寻径：两点在路网上不连通"; return; }

        var route = new PolylineEntity { Cr = 0.30f, Cg = 0.95f, Cb = 0.95f };   // 青色路径
        double dist = 0;
        for (int i = 0; i < path.Count; i++)
        {
            var p = nodes[path[i]];
            route.Points.Add(p);
            if (i > 0) { var q = nodes[path[i - 1]]; dist += System.Math.Sqrt((p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y)); }
        }
        BeginChange();
        _scene.Add(route);
        RefreshScene();
        StatusMsg.Text = $"寻径完成：{path.Count} 节点 · 路径长度 {dist:0.##}";
    }

    // 备选路径：K 最短路(Yen)。复用点对点两点取点, 计算并高亮 K 条不同路径
    private void StartKPathfind()
    {
        _pathActive = true; _kpathMode = true; _pathP1 = null;
        _tool = null; _measure = null; _editMode = EditMode.None;
        _offsetActive = false; _trimActive = false; _breakActive = false;
        StatusMsg.Text = "备选路径(K最短路)：点起点（路网 = 场景中的多段线）";
    }

    private void ComputeKPaths((double x, double y) a, (double x, double y) b)
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "备选路径：场景无路（多段线）"; return; }
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = RoadNetwork.Build(polys, tol);
        int s = RoadNetwork.NearestNode(nodes, a.x, a.y);
        int g = RoadNetwork.NearestNode(nodes, b.x, b.y);
        const int K = 3;
        var paths = RoadNetwork.KShortestPaths(adj, s, g, K);
        if (paths.Count == 0) { StatusMsg.Text = "备选路径：两点在路网上不连通"; return; }
        // 主路青、备选橙/黄, 依次高亮
        var colors = new (float r, float g, float b)[] { (0.30f, 0.95f, 0.95f), (0.95f, 0.55f, 0.20f), (0.95f, 0.85f, 0.30f) };
        BeginChange();
        var lens = new List<double>();
        for (int i = 0; i < paths.Count; i++)
        {
            var col = colors[i % colors.Length];
            var route = new PolylineEntity { Cr = col.r, Cg = col.g, Cb = col.b };
            foreach (var idx in paths[i]) route.Points.Add(nodes[idx]);
            _scene.Add(route);
            lens.Add(RoadNetwork.PathLength(nodes, paths[i]));
        }
        RefreshScene();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lens.Count; i++) { if (i > 0) sb.Append(" · "); sb.Append($"#{i + 1} {lens[i]:0.#}"); }
        StatusMsg.Text = $"备选路径：{paths.Count} 条(里程升序) {sb}";
    }

    // 基础道路网络构建：场景多段线建无向加权图 → 报节点/边/连通片数 + 交点(度≥3)黄点标注
    private void BuildRoadNetworkCmd()
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities) if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "路网构建：场景无路（多段线）"; return; }
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = RoadNetwork.Build(polys, tol);
        int edges = 0; for (int i = 0; i < adj.Count; i++) edges += adj[i].Count; edges /= 2;   // 无向
        RoadConnectivity.Components(adj, out int comps);
        double markSize = tol > 0 ? tol * 1.5 : 1.0;
        BeginChange();
        int junctions = 0;
        for (int i = 0; i < nodes.Count; i++)
            if (adj[i].Count >= 3) { _scene.Add(new PointEntity { X = nodes[i].x, Y = nodes[i].y, Size = markSize, Cr = 0.95f, Cg = 0.85f, Cb = 0.25f }); junctions++; }
        RefreshScene();
        StatusMsg.Text = $"路网构建：{polys.Count} 中线 → {nodes.Count} 节点·{edges} 边·{comps} 连通片·{junctions} 交点(度≥3, 黄点)";
    }

    // 路网校验：场景多段线建图 → 连通分量数 + 片间最窄缺口(品红线标注) + 报表
    private void ValidateRoadNetwork()
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "路网校验：场景无路（多段线）"; return; }
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = RoadNetwork.Build(polys, tol);
        RoadConnectivity.Components(adj, out int comps);
        if (comps <= 1) { StatusMsg.Text = $"路网校验：连通(1 片, {nodes.Count} 节点)——网络完整"; return; }
        // maxGap 按包围盒对角取, 尺度稳健
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in nodes) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
        double diag = System.Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        double maxGap = diag > 0 ? diag * 0.5 : 300.0;
        var gaps = RoadConnectivity.AllGaps(nodes, adj, maxGap, RoadConnectivity.SampleStepM);
        BeginChange();
        foreach (var g in gaps)
            _scene.Add(new LineEntity { X0 = g.From.x, Y0 = g.From.y, X1 = g.To.x, Y1 = g.To.y, Cr = 0.95f, Cg = 0.2f, Cb = 0.85f });   // 品红缺口线
        RefreshScene();
        string narrow = gaps.Count > 0 ? $" · 最窄缺口 {gaps[0].GapM:0.#}(片{gaps[0].FromComp}↔{gaps[0].ToComp})" : "";
        StatusMsg.Text = $"路网校验：{comps} 个连通片(断开!) · {gaps.Count} 处可接缺口(≤{maxGap:0.#}, 品红标注){narrow}";
    }

    // 读中线 CSV(lineId,x,y[,z]) → 分组为 EvoLine 列表(供演化对比)
    private static List<EvoLine> ReadEvoLines(string path)
    {
        var lines = new List<EvoLine>();
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(path); } catch { return lines; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var groups = new Dictionary<string, EvoLine>(); var order = new List<string>();
        foreach (var raw in rows)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("#")) continue;
            var t = s.Split(new[] { ',', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 3) continue;
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out double x)) continue;
            if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out double y)) continue;
            double z = 0; if (t.Length >= 4) double.TryParse(t[3], System.Globalization.NumberStyles.Float, inv, out z);
            string id = t[0];
            if (!groups.TryGetValue(id, out var ln)) { ln = new EvoLine { Id = id }; groups[id] = ln; order.Add(id); }
            ln.Centerline.Add(new Pt3(x, y, z));
        }
        foreach (var id in order) if (groups[id].Centerline.Count >= 2) lines.Add(groups[id]);
        return lines;
    }

    // 时段快照：把当前场景折线(路网中线)导出为纪元 CSV(lineId,x,y,z)——供演化对比作两期输入
    private async Task SnapshotEpochAsync()
    {
        var polys = new List<PolylineEntity>();
        foreach (var e in _scene.Entities) if (e is PolylineEntity p && p.Points.Count >= 2) polys.Add(p);
        if (polys.Count == 0) { StatusMsg.Text = "时段快照：场景无路网（多段线）"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "时段快照：导出路网纪元 CSV", DefaultExtension = "csv", SuggestedFileName = "epoch.csv",
            FileTypeChoices = new[] { new FilePickerFileType("路网纪元 CSV") { Patterns = new[] { "*.csv" } } }
        });
        if (file == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("lineId,x,y,z\n");
        int id = 0, verts = 0;
        foreach (var p in polys)
        {
            id++;
            foreach (var (x, y) in p.Points) { sb.Append("L").Append(id).Append(',').Append(x.ToString("R", inv)).Append(',').Append(y.ToString("R", inv)).Append(",0\n"); verts++; }
        }
        try { System.IO.File.WriteAllText(file.Path.LocalPath, sb.ToString()); }
        catch (System.Exception ex) { StatusMsg.Text = $"时段快照：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"时段快照：{polys.Count} 条中线·{verts} 顶点 → {System.IO.Path.GetFileName(file.Path.LocalPath)}（可作演化对比的一期输入）";
    }

    // 演化对比：读上期 + 本期中线 CSV(lineId,x,y[,z]) → 逐段分类(保持/移位/延拓/截短/废除) → 配色入场景 + 里程账
    private async Task EvolutionCompareAsync()
    {
        var pf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "演化对比：① 选上期中线 CSV (lineId,x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("中线 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (pf.Count == 0) return;
        var prev = ReadEvoLines(pf[0].Path.LocalPath);
        var cf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "演化对比：② 选本期中线 CSV (lineId,x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("中线 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (cf.Count == 0) return;
        var curr = ReadEvoLines(cf[0].Path.LocalPath);
        if (prev.Count == 0 && curr.Count == 0) { StatusMsg.Text = "演化对比：两期均未解析到中线(需 lineId,x,y[,z], 每线≥2点)"; return; }
        var res = RoadEvolutionAnalyzer.Analyze(prev, curr, new RoadEvolutionOptions());
        // 按类别配色：保持灰/移位橙/延拓·新建绿/截短黄/废除红
        (float r, float g, float b) Col(RoadEvolutionClass c) => c switch
        {
            RoadEvolutionClass.Keep => (0.6f, 0.6f, 0.65f),
            RoadEvolutionClass.Shift => (0.95f, 0.55f, 0.20f),
            RoadEvolutionClass.Extend => (0.35f, 0.9f, 0.45f),
            RoadEvolutionClass.Shorten => (0.95f, 0.85f, 0.30f),
            _ => (0.95f, 0.25f, 0.25f),
        };
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        BeginChange();
        foreach (var r in res.Routes)
        {
            if (r.DisplayCenterline.Count < 2) continue;
            var (cr, cg, cb) = Col(r.Class);
            var pl = new PolylineEntity { Cr = cr, Cg = cg, Cb = cb };
            foreach (var p in r.DisplayCenterline)
            {
                pl.Points.Add((p.X, p.Y));
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y; if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
            _scene.Add(pl);
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        // 落 CSV 明细到本期文件旁
        try { System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(cf[0].Path.LocalPath, ".evolution.csv"), res.ToCsv()); } catch { }
        StatusMsg.Text = $"演化对比：{res.Summary}｜{res.Ledger.Text}";
    }

    // 圈范围算量：选中的闭合多段线作边界 → TIN → 边界内三角体积
    private async Task BoundaryVolumeAsync()
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity boundary || !boundary.Closed || boundary.Points.Count < 3)
        { StatusMsg.Text = "圈范围算量：请先选中一条闭合多段线作边界"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "圈范围算量：选高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"圈范围算量：点导入失败 {r.Error}"; return; }
        var pts2d = new List<(double x, double y)>();
        double zmin = double.MaxValue;
        foreach (var p in r.Points) { pts2d.Add((p.x, p.y)); if (p.z < zmin) zmin = p.z; }
        var tris = Delaunay.Triangulate(pts2d);
        if (tris.Count == 0) { StatusMsg.Text = "圈范围算量：点太少或共线"; return; }
        var (above, below, net) = TerrainAnalysis.VolumeWithinBoundary(r.Points, tris, zmin, boundary.Points);
        StatusMsg.Text = $"圈范围算量（基准 z {zmin:0.##}）：上方 {above:0.##} · 下方 {below:0.##} · 净 {net:0.##}";
    }

    // 两期算量：选两期高程点 CSV → 同网格差值 → 挖方/填方/净值
    private async Task TwoEpochVolumeAsync()
    {
        var opt = new System.Func<string, FilePickerOpenOptions>(t => new FilePickerOpenOptions
        {
            Title = t, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        var f1 = await StorageProvider.OpenFilePickerAsync(opt("两期算量：选【第一期】高程点 CSV"));
        if (f1.Count == 0) return;
        var f2 = await StorageProvider.OpenFilePickerAsync(opt("两期算量：选【第二期】高程点 CSV"));
        if (f2.Count == 0) return;
        var r1 = PointDataImportService.Load(f1[0].Path.LocalPath);
        var r2 = PointDataImportService.Load(f2[0].Path.LocalPath);
        if (!r1.Success || !r2.Success) { StatusMsg.Text = "两期算量：点导入失败"; return; }
        var (cut, fill, net) = TerrainAnalysis.TwoEpochVolume(r1.Points, r2.Points, 64);
        StatusMsg.Text = $"两期算量：挖方(下降) {cut:0.##} · 填方(上升) {fill:0.##} · 净 {net:0.##}";
    }

    // 三角网着色通用流程：散点 CSV → 三角网 → builder 生成着色边入场景
    private async Task ShadeTinAsync(string title, string doneHint,
        System.Func<System.Collections.Generic.IReadOnlyList<(double x, double y, double z)>, List<(int a, int b, int c)>, List<SceneEntity>> builder)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"{title}：选高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"{title}：点导入失败 {r.Error}"; return; }
        var pts2d = new List<(double x, double y)>();
        foreach (var p in r.Points) pts2d.Add((p.x, p.y));
        var tris = Delaunay.Triangulate(pts2d);
        if (tris.Count == 0) { StatusMsg.Text = $"{title}：点太少或共线"; return; }
        var geo = builder(r.Points, tris);
        BeginChange();
        foreach (var e in geo) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"{title}：{tris.Count} 三角（{doneHint}）";
    }

    // 图层管理器：列出图层复选框，勾选控制显隐
    private void PopulateLayers(DxfImportService.ImportResult r)
    {
        var items = new List<CheckBox>();
        foreach (var name in r.LayerOrder)
        {
            var cb = new CheckBox
            {
                Content = $"{name}（{r.LayerCounts.GetValueOrDefault(name)}）",
                IsChecked = true,
                Tag = name,
                FontSize = 12
            };
            cb.IsCheckedChanged += OnLayerToggle;
            items.Add(cb);
        }
        LayerList.ItemsSource = items;
    }

    private void OnLayerToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string layer)
            Viewport.SetLayerVisible(layer, cb.IsChecked == true);
    }

    // 文件管理器：选文件夹 → 列出该目录所有可导入图形（DXF/DWG/OFF/MapGIS/KDF/3DMine）
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择图纸文件夹",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;

        string dir = folders[0].Path.LocalPath;
        FileFolderLabel.Text = dir;
        var items = CadFileBrowser.ListImportable(dir);
        FileList.ItemsSource = items
            .Select(x => new ListBoxItem { Content = x.Name, Tag = x.Path })
            .ToList();
        StatusMsg.Text = items.Count == 0 ? "该文件夹无可导入图形（DXF/DWG/OFF/MapGIS/KDF/3DMine）" : $"{items.Count} 个图形文件（双击打开）";
    }

    // 双击文件列表项 → 导入该图纸
    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is ListBoxItem { Tag: string path })
            ImportPath(path);
    }

    // 对象管理器：按图元类型列出导入的实体
    private void PopulateObjectTree(DxfImportService.ImportResult r, string fileName)
    {
        ObjectTreeHint.IsVisible = false;
        var root = new TreeViewItem { Header = $"{fileName}（{r.EntityCount} 实体）", IsExpanded = true };
        foreach (var kv in r.TypeCounts)
            root.Items.Add(new TreeViewItem { Header = $"{kv.Key} × {kv.Value}", Tag = kv.Key });
        ObjectTree.ItemsSource = new[] { root };
    }

    // 对象管理器：按类型列出（可编辑导入）
    private void PopulateObjectTreeCounts(Dictionary<string, int> typeCounts, string fileName, int total)
    {
        ObjectTreeHint.IsVisible = false;
        var root = new TreeViewItem { Header = $"{fileName}（{total} 实体）", IsExpanded = true };
        foreach (var kv in typeCounts)
            root.Items.Add(new TreeViewItem { Header = $"{kv.Key} × {kv.Value}", Tag = kv.Key });
        ObjectTree.ItemsSource = new[] { root };
    }

    // 对象管理器：实时反映绘制场景（按类型分组计数，随增删刷新；点类型节点→选中该类全部）
    private int _lastSceneCount = -1;
    private void RefreshObjectTree()
    {
        if (_scene.Count == 0)
        {
            if (_lastImport == null) { ObjectTree.ItemsSource = null; ObjectTreeHint.IsVisible = true; }
            return;   // 空场景但有 OFF 显示导入：保留其类型树
        }
        var counts = new Dictionary<string, int>();
        foreach (var en in _scene.Entities) { var t = CnOf(en); counts[t] = counts.GetValueOrDefault(t) + 1; }
        ObjectTreeHint.IsVisible = false;
        var root = new TreeViewItem { Header = $"图形（{_scene.Count} 实体）", IsExpanded = true };
        foreach (var kv in counts) root.Items.Add(new TreeViewItem { Header = $"{kv.Key} × {kv.Value}", Tag = kv.Key });
        ObjectTree.ItemsSource = new[] { root };
    }

    // 图层面板：每层一行 [显隐][冻结][锁定][色块][名称→设当前]，由绘制图层表驱动
    private void PopulateDrawingLayers()
    {
        var rows = new List<Control>();
        foreach (var l in _layers.Layers)
        {
            var layer = l;   // 闭包捕获
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };

            var vis = new CheckBox { IsChecked = layer.Visible, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(vis, "显示/隐藏");
            vis.IsCheckedChanged += (_, _) => { layer.Visible = vis.IsChecked == true; AfterLayerStateChange(); };

            var frz = new ToggleButton { IsChecked = layer.Frozen, Content = "冻", FontSize = 10, Padding = new Thickness(3, 0), MinWidth = 0 };
            ToolTip.SetTip(frz, "冻结（隐藏且不可选）");
            frz.IsCheckedChanged += (_, _) => { layer.Frozen = frz.IsChecked == true; AfterLayerStateChange(); };

            var lck = new ToggleButton { IsChecked = layer.Locked, Content = "锁", FontSize = 10, Padding = new Thickness(3, 0), MinWidth = 0 };
            ToolTip.SetTip(lck, "锁定（可见不可选）");
            lck.IsCheckedChanged += (_, _) => { layer.Locked = lck.IsChecked == true; AfterLayerStateChange(); };

            var swatch = new Button
            {
                Width = 16, Height = 16, Padding = new Thickness(0), MinWidth = 0,
                Background = new SolidColorBrush(Color.FromRgb((byte)(layer.Cr * 255), (byte)(layer.Cg * 255), (byte)(layer.Cb * 255))),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(swatch, "点击换色（该层实体跟随变色）");
            swatch.Click += (_, _) => CycleLayerColor(layer);

            bool cur = ReferenceEquals(layer, _layers.Current);
            var name = new Button
            {
                Content = (cur ? "● " : "") + layer.Name,
                FontWeight = cur ? FontWeight.Bold : FontWeight.Normal,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0), FontSize = 12
            };
            ToolTip.SetTip(name, "点击设为当前图层");
            name.Click += (_, _) => { _layers.SetCurrent(layer.Name); PopulateDrawingLayers(); StatusMsg.Text = $"当前图层「{layer.Name}」"; };

            row.Children.Add(vis); row.Children.Add(frz); row.Children.Add(lck); row.Children.Add(swatch); row.Children.Add(name);
            rows.Add(row);
        }
        LayerList.ItemsSource = rows;
    }

    // 图层显隐/冻结/锁定变更后：失效选择清理 + 重绘
    private void AfterLayerStateChange()
    {
        _selected.RemoveAll(en => !_layers.IsSelectable(en.LayerName));
        HighlightSelection();
        RefreshScene();
    }

    private static readonly (float r, float g, float b)[] LayerPalette =
    {
        (0.86f, 0.90f, 0.60f), (0.90f, 0.50f, 0.40f), (0.50f, 0.80f, 0.95f), (0.70f, 0.85f, 0.50f),
        (0.90f, 0.75f, 0.40f), (0.75f, 0.60f, 0.90f), (0.55f, 0.90f, 0.70f), (0.90f, 0.60f, 0.75f)
    };

    // 图层改色：循环到下一预设色，该层实体跟随变色
    private void CycleLayerColor(Layer l)
    {
        int idx = 0;
        for (int i = 0; i < LayerPalette.Length; i++)
            if (System.Math.Abs(LayerPalette[i].r - l.Cr) < 0.02f && System.Math.Abs(LayerPalette[i].g - l.Cg) < 0.02f && System.Math.Abs(LayerPalette[i].b - l.Cb) < 0.02f)
            { idx = i; break; }
        var c = LayerPalette[(idx + 1) % LayerPalette.Length];
        l.Cr = c.r; l.Cg = c.g; l.Cb = c.b;
        int n = _scene.RecolorLayer(l.Name, c.r, c.g, c.b);
        RefreshScene();
        HighlightSelection();
        PopulateDrawingLayers();
        StatusMsg.Text = $"图层「{l.Name}」改色（{n} 个实体跟随）";
    }

    // 帮助：命令与快捷键参考窗口
    private void ShowHelp()
    {
        const string help =
            "PitMine3D · Kylin 移植版 — 命令与快捷键\n" +
            "（Home 绘制/编辑为内核到位前的托管重实现）\n" +
            "\n【鼠标】\n" +
            "  中键拖拽 = 平移 · 滚轮 = 朝光标缩放\n" +
            "  2D 左键拖拽 = 窗口框选（左→右全含，右→左交叉）\n" +
            "  3D 左键拖拽 = 轨道旋转 · 右键 = 上下文菜单\n" +
            "  双击 = 结束多段线 / 否则范围缩放\n" +
            "\n【快捷键】\n" +
            "  ESC 取消当前命令 · Del 删除选中 · Ctrl+Z 撤销 · Ctrl+Y 重做\n" +
            "\n【绘制】\n" +
            "  直线 LINE · 圆 CIRCLE(下拉:2P/3P/TTR) · 圆弧 ARC(下拉:三点/SCE/CSE)\n" +
            "  矩形 RECT · 多段线 PLINE · 滑动多段线 PLDRAG · 点 POINT · 正多边形 POLYGON(可带边数)\n" +
            "\n【修改】\n" +
            "  移动 M · 复制 CO · 旋转 RO · 缩放 SC · 镜像 MI · 删除 E\n" +
            "  偏移 O · 修剪/延伸 TR/EX · 打断 BR · 分解 X · 夹点(选中后拖方块)\n" +
            "\n【选择】\n" +
            "  全部 ALL · 最后 LAST · 上次 P · 快速选择(选类似) QSELECT\n" +
            "\n【文件】\n" +
            "  新建 NEW · 打开 OPEN(.pmx) · 保存 SAVE(.pmx) · 另存为(.pmx/.dxf/.dwg)\n" +
            "  导入 DXF/DWG/OFF · 导入点 CSV/TXT · 导出 DXF\n" +
            "\n【精确坐标】命令行输入：\n" +
            "  x,y 绝对 · @dx,dy 相对 · d<角 极坐标 · @d<角 相对极\n" +
            "\n【图层】左侧面板每层：显隐/冻结/锁定/设当前/色块\n" +
            "\n【视图】2D · 3D · 网格 GRID · 范围缩放 ZE";

        var win = new Window
        {
            Title = "帮助 — 命令与快捷键",
            Width = 560, Height = 660,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), FontSize = 13 }
            }
        };
        win.Show(this);
        StatusMsg.Text = "已打开帮助";
    }

    // 选项：网格 / 对象捕捉 / 捕捉容差（即时生效）
    private void ShowOptions()
    {
        var grid = new CheckBox { Content = "显示网格", IsChecked = _gridOn };
        var snap = new CheckBox { Content = "启用对象捕捉", IsChecked = SnapToggle.IsChecked == true };
        var tolLabel = new TextBlock { Text = "捕捉容差 (像素)", VerticalAlignment = VerticalAlignment.Center };
        var tol = new TextBox { Text = _snapTolPx.ToString("0"), Width = 80 };
        var tolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tolLabel, tol } };

        var ok = new Button { Content = "确定", MinWidth = 72 };
        var cancel = new Button { Content = "取消", MinWidth = 72 };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { grid, snap, tolRow, btnRow } };
        var win = new Window
        {
            Title = "选项", Width = 320, Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            Content = panel
        };
        cancel.Click += (_, _) => win.Close();
        ok.Click += (_, _) =>
        {
            SetGrid(grid.IsChecked == true);
            SnapToggle.IsChecked = snap.IsChecked == true;
            if (double.TryParse(tol.Text, out double t) && t >= 2 && t <= 60) _snapTolPx = t;
            StatusMsg.Text = $"选项已应用（网格 {(_gridOn ? "开" : "关")} · 捕捉 {(SnapToggle.IsChecked == true ? "开" : "关")} · 容差 {_snapTolPx:0}px）";
            win.Close();
        };
        win.Show(this);
    }

    // 场景实体 → 类型中文名（对象树高亮 / 快速选择匹配用）
    private static string CnOf(SceneEntity e) => EntityTypeName.Of(e);

    // 快速选择（选择类似）：以选中实体的类型为准，选中场景中所有同类型实体
    private void SelectSimilar()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "快速选择：请先选一个参照实体（再执行选中所有同类）"; return; }
        var types = new HashSet<string>(_selected.Select(CnOf));
        SaveSel();
        var matched = _scene.Entities.Where(en => types.Contains(CnOf(en)) && _layers.IsSelectable(en.LayerName)).ToList();
        _selected.Clear();
        _selected.AddRange(matched);
        HighlightSelection();
        StatusMsg.Text = $"快速选择：{_selected.Count} 个（类型 {string.Join("/", types)}）";
    }

    // 对象树选中类型 → 高亮该类型几何；选根/无 → 清除
    private void OnObjectTreeSelect(object? sender, SelectionChangedEventArgs e)
    {
        if (ObjectTree.SelectedItem is TreeViewItem { Tag: string type })
        {
            // OFF 显示态导入(不在场景)：仅高亮其类型几何
            if (_scene.Count == 0 && _lastImport != null && _lastImport.TypeGeometry.TryGetValue(type, out var geom))
            { Viewport.SetHighlight(geom); return; }
            // 实时场景：真选中该类全部实体(可编辑/看特性)
            var sel = new List<SceneEntity>();
            foreach (var en in _scene.Entities) if (CnOf(en) == type) sel.Add(en);
            if (sel.Count == 0) return;
            _selected.Clear(); _selected.AddRange(sel);
            HighlightSelection();
            StatusMsg.Text = $"对象树：选中 {type} × {sel.Count}";
        }
        // 根节点/程序刷新导致的空选择：不动 _selected(避免刷新反噬清选)
    }

    // ---------- 右键上下文菜单 ----------
    private void OnCtxZoomExtents(object? s, RoutedEventArgs e) => Viewport.ZoomExtents();
    // 通用右键菜单项 → 按 Tag 派发命令（复用既有命令处理，忠实原丰富上下文菜单）
    private void OnCtxCommand(object? s, RoutedEventArgs e) { if (s is MenuItem { Tag: string cmd }) DispatchRibbon(cmd); }
    private void OnCtx2D(object? s, RoutedEventArgs e) { Viewport.SetViewMode(true); StatusMsg.Text = "视图: 2D 平面"; }
    private void OnCtx3D(object? s, RoutedEventArgs e) { Viewport.SetViewMode(false); StatusMsg.Text = "视图: 3D 轨道"; }
    private void OnCtxGrid(object? s, RoutedEventArgs e) => SetGrid(!_gridOn);

    // 网格显隐(保持 _gridOn 与视口一致)
    private void SetGrid(bool on)
    {
        if (on == _gridOn) return;
        _gridOn = on;
        Viewport.ToggleGrid();
    }
    private void OnCtxClearHighlight(object? s, RoutedEventArgs e) => Viewport.SetHighlight(null);

    // 对象捕捉容差：约 12px 换算到世界单位
    private double SnapTolWorld(Avalonia.Point p)
    {
        var a = Viewport.ScreenToWorld(p.X, p.Y);
        var b = Viewport.ScreenToWorld(p.X + _snapTolPx, p.Y);
        if (a == null || b == null) return 0;
        double dx = b.Value.x - a.Value.x, dy = b.Value.y - a.Value.y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    // 捕捉标记：绿色十字几何（P3_C3）
    private static float[] SnapCross(double x, double y, double s)
    {
        const float g0 = 0.2f, g1 = 1f, g2 = 0.4f;
        return new float[]
        {
            (float)(x - s), (float)y, 0, g0, g1, g2,  (float)(x + s), (float)y, 0, g0, g1, g2,
            (float)x, (float)(y - s), 0, g0, g1, g2,  (float)x, (float)(y + s), 0, g0, g1, g2
        };
    }

    // 激活绘制工具（Home 绘制命令/按钮；识别中文按钮名与英文命令）。
    private bool ActivateDrawTool(string cmd)
    {
        string u = cmd.Trim().ToUpperInvariant();

        // 正多边形：可带边数，如 "POLYGON 5" / "POL8" / "正多边形6"（须排除 POLYLINE）
        string letters = new string(u.TakeWhile(char.IsLetter).ToArray());
        if (letters == "POLYGON" || letters == "POL" || cmd.Trim().StartsWith("正多边形"))
        {
            int sides = 6;
            string digits = new string(cmd.Where(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out int n) && n >= 3 && n <= 120) sides = n;
            _tool = new PolygonTool { Sides = sides };
            _measure = null; Viewport.SetSnapMarker(null); _snapShown = false; _lastInputPoint = null;
            StatusMsg.Text = _tool.Prompt + "（ESC 退出）";
            return true;
        }

        DrawTool? t = u switch
        {
            "LINE" => new LineTool(),
            "CIRCLE" => new CircleTool(),
            "CIRCLE2P" or "C2P" => new Circle2PTool(),
            "CIRCLE3P" or "C3P" => new Circle3PTool(),
            "ARC" => new ArcTool(),
            "ARCSCE" => new ArcSceTool(),
            "ARCCSE" => new ArcCseTool(),
            "RECTANG" or "RECT" => new RectTool(),
            "PLINE" or "POLYLINE" => new PolylineTool(),
            "POINT" or "PO" => new PointTool(),
            _ => cmd.Trim() switch
            {
                "直线" => new LineTool(),
                "圆" => new CircleTool(),
                "圆2P" or "圆(2点)" => new Circle2PTool(),
                "圆3P" or "圆(3点)" => new Circle3PTool(),
                "圆弧" or "圆弧(三点)" => new ArcTool(),
                "圆弧SCE" or "圆弧(起点圆心端点)" => new ArcSceTool(),
                "圆弧CSE" or "圆弧(圆心起点端点)" => new ArcCseTool(),
                "矩形" => new RectTool(),
                "多段线" => new PolylineTool(),
                "点" => new PointTool(),
                _ => (DrawTool?)null
            }
        };
        if (t == null) return false;
        _tool = t;
        _measure = null;                              // 退出测距
        Viewport.SetSnapMarker(null); _snapShown = false; _lastInputPoint = null;
        StatusMsg.Text = t.Prompt + "（ESC 退出）";
        return true;
    }

    // 撤销/重做：改动前记快照
    private void BeginChange() => _undo.Push(SceneIO.Save(_scene));

    private void LoadSceneFrom(string json)
    {
        var loaded = SceneIO.Load(json);
        _scene.Clear();
        foreach (var e in loaded.Entities) _scene.Add(e);
        _selected.Clear();
        Viewport.SetHighlight(null);
        RefreshScene();
    }

    private void DoUndo()
    {
        var s = _undo.Undo(SceneIO.Save(_scene));
        if (s == null) { StatusMsg.Text = "无可撤销"; return; }
        LoadSceneFrom(s); StatusMsg.Text = "已撤销";
    }

    private void DoRedo()
    {
        var s = _undo.Redo(SceneIO.Save(_scene));
        if (s == null) { StatusMsg.Text = "无可重做"; return; }
        LoadSceneFrom(s); StatusMsg.Text = "已重做";
    }

    // 新实体归当前图层（名称 + 图层色）
    private void AssignLayer(SceneEntity e)
    {
        e.LayerName = _layers.Current.Name;
        e.Cr = _layers.Current.Cr; e.Cg = _layers.Current.Cg; e.Cb = _layers.Current.Cb;
    }

    // 从场景实体抽取捕捉原语(线段/圆/圆弧/点)——供 ObjectSnap 补算交点/最近/垂足(端点/中点/圆心已由 SnapCandidates 覆盖)。
    private (List<ObjectSnap.Seg> segs, List<ObjectSnap.Circ> circles, List<ObjectSnap.ArcP> arcs, List<(double x, double y)> pts) BuildSnapGeom()
    {
        var segs = new List<ObjectSnap.Seg>();
        var circles = new List<ObjectSnap.Circ>();
        var arcs = new List<ObjectSnap.ArcP>();
        var pts = new List<(double x, double y)>();
        foreach (var e in _scene.Entities)
        {
            if (!_layers.IsShown(e.LayerName)) continue;
            switch (e)
            {
                case LineEntity l: segs.Add(new ObjectSnap.Seg(l.X0, l.Y0, l.X1, l.Y1)); break;
                case RectEntity r:
                    segs.Add(new ObjectSnap.Seg(r.X0, r.Y0, r.X1, r.Y0));
                    segs.Add(new ObjectSnap.Seg(r.X1, r.Y0, r.X1, r.Y1));
                    segs.Add(new ObjectSnap.Seg(r.X1, r.Y1, r.X0, r.Y1));
                    segs.Add(new ObjectSnap.Seg(r.X0, r.Y1, r.X0, r.Y0));
                    break;
                case PolylineEntity pl:
                    for (int i = 0; i + 1 < pl.Points.Count; i++)
                        segs.Add(new ObjectSnap.Seg(pl.Points[i].x, pl.Points[i].y, pl.Points[i + 1].x, pl.Points[i + 1].y));
                    break;
                case CircleEntity c: circles.Add(new ObjectSnap.Circ(c.Cx, c.Cy, c.Radius)); break;
                case PointEntity p: pts.Add((p.X, p.Y)); break;
                case ArcEntity a:
                    var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
                    if (cc != null)
                    {
                        double cx = cc.Value.cx, cy = cc.Value.cy, rr = cc.Value.r;
                        double a0 = System.Math.Atan2(a.Y1 - cy, a.X1 - cx);
                        double am = System.Math.Atan2(a.Y2 - cy, a.X2 - cx);
                        double a1 = System.Math.Atan2(a.Y3 - cy, a.X3 - cx);
                        // 定向: 使 CCW A0→A1 经过弧上中间点 am; 否则换向(取互补弧)。
                        double Norm(double x) { while (x < 0) x += 2 * System.Math.PI; while (x >= 2 * System.Math.PI) x -= 2 * System.Math.PI; return x; }
                        double sweep = Norm(a1 - a0), amid = Norm(am - a0);
                        if (sweep < 1e-9 || amid > sweep) arcs.Add(new ObjectSnap.ArcP(cx, cy, rr, a1, a0));
                        else arcs.Add(new ObjectSnap.ArcP(cx, cy, rr, a0, a1));
                    }
                    break;
            }
        }
        return (segs, circles, arcs, pts);
    }

    private static double Dist2((double x, double y) a, (double x, double y) b)
    { double dx = a.x - b.x, dy = a.y - b.y; return dx * dx + dy * dy; }

    private bool _hatchCross;   // 图案填充: 是否十字交叉

    // 图案填充(用户定义线剖面): 选中闭合边界(闭合多段线/矩形/正多边形) → 按角度+间距生成剖面线入场景。
    // 原「填充」走引擎命名图案库(不可见, 记录); 此为标准可见的用户定义线填充, 亦本 2D 线渲染器唯一可行形式。
    private void HatchBoundaryCmd(double angleDeg, double spacing)
    {
        List<(double x, double y)>? bnd = null;
        foreach (var e in _selected)
        {
            if (e is PolylineEntity p && p.Closed && p.Points.Count >= 3) { bnd = new List<(double, double)>(p.Points); break; }
            if (e is RectEntity r) { bnd = new List<(double, double)> { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1) }; break; }
            if (e is PolygonEntity pg && pg.Sides >= 3)
            {
                bnd = new List<(double, double)>();
                for (int i = 0; i < pg.Sides; i++)
                { double a = pg.Rotation + 2 * System.Math.PI * i / pg.Sides; bnd.Add((pg.Cx + pg.Radius * System.Math.Cos(a), pg.Cy + pg.Radius * System.Math.Sin(a))); }
                break;
            }
        }
        if (bnd == null) { StatusMsg.Text = "图案填充：请先选中一条闭合多段线/矩形/正多边形作边界"; return; }

        // 间距缺省 = 边界包围盒对角线的 1/24（约 20~30 条线）
        if (spacing <= 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var v in bnd) { if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x; if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y; }
            double diag = System.Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            spacing = System.Math.Max(diag / 24.0, 1e-6);
        }
        var lines = HatchPattern.Generate(bnd, angleDeg, spacing, _hatchCross);
        if (lines.Count == 0) { StatusMsg.Text = "图案填充：未生成剖面线（边界过小或间距过大）"; return; }
        BeginChange();
        foreach (var (x1, y1, x2, y2) in lines)
        {
            var le = new LineEntity { X0 = x1, Y0 = y1, X1 = x2, Y1 = y2, Cr = 0.40f, Cg = 0.70f, Cb = 0.85f };
            AssignLayer(le); _scene.Add(le);
        }
        RefreshScene();
        StatusMsg.Text = $"图案填充：{lines.Count} 条剖面线（角度 {angleDeg:0.#}° 间距 {spacing:0.##}{(_hatchCross ? " 十字" : "")}）";
    }

    // 切换某扩展捕捉模式(交点/最近/垂足)位; 顺带确保主对象捕捉开。
    private void ToggleSnapExtra(ObjectSnap.Mode m, string name)
    {
        int bit = 1 << (int)m;
        _snapExtraMask ^= bit;
        bool on = (_snapExtraMask & bit) != 0;
        if (on) SnapToggle.IsChecked = true;
        StatusMsg.Text = $"{name}捕捉: {(on ? "开" : "关")}";
    }

    // 捕捉标记文案: 扩展模式显名(交点/最近/垂足), 顶点候选统称"捕捉"。
    private static string SnapModeLabel(ObjectSnap.Mode? m) => m switch
    {
        ObjectSnap.Mode.Intersection => "交点",
        ObjectSnap.Mode.Nearest => "最近",
        ObjectSnap.Mode.Perpendicular => "垂足",
        ObjectSnap.Mode.Center => "圆心",
        ObjectSnap.Mode.Midpoint => "中点",
        ObjectSnap.Mode.Endpoint => "端点",
        _ => "捕捉",
    };

    // 重绘场景（含当前工具进行中的预览：已点的段 + 到光标的橡皮筋）
    private void RefreshScene()
    {
        var baseGeom = _scene.BuildGeometry(_layers.IsShown);
        _snapVerts = _scene.SnapCandidates(_layers.IsShown);   // 语义 osnap 点(端点/中点/圆心/象限)
        var list = new List<float>(baseGeom);
        _tool?.AppendPreview(list, _cursorWorld);
        if (_slideDragging && _slidePts.Count > 1)     // 滑动多段线拖动预览
        {
            var pv = new PolylineEntity { Points = _slidePts, Cr = 0.55f, Cg = 0.62f, Cb = 0.70f };
            pv.Tessellate(list);
        }
        Viewport.SetSceneGeometry(list.ToArray());
        if (_scene.Count != _lastSceneCount) { _lastSceneCount = _scene.Count; RefreshObjectTree(); }
    }

    // 点选：命中则单选(再点取消)，未命中清空
    private void PickAt(Avalonia.Point rel)
    {
        var w = _snapWorld ?? Viewport.ScreenToWorld(rel.X, rel.Y);
        if (w == null) return;
        SaveSel();
        double tol = SnapTolWorld(rel);
        var hit = _scene.Pick(w.Value.x, w.Value.y, tol, _layers.IsSelectable);
        if (hit == null) _selected.Clear();
        else if (_selected.Contains(hit)) _selected.Remove(hit);
        else { _selected.Clear(); _selected.Add(hit); }
        HighlightSelection();
        StatusMsg.Text = _selected.Count > 0 ? $"已选 {_selected.Count} 个实体" : "未选中";
    }

    private void HighlightSelection()
    {
        UpdatePropertyPanel();
        if (_selected.Count == 0) { Viewport.SetHighlight(null); return; }
        var o = new List<float>();
        foreach (var e in _selected) e.Tessellate(o);
        if (_gripsOn && _selected.Count == 1)           // 单选 + 夹点开 → 叠加夹点方块
            foreach (var g in _selected[0].Grips()) AppendGripSquare(o, g.x, g.y, GripSize());
        Viewport.SetHighlight(o.ToArray());
    }

    // 右侧特性面板：随选择更新（单选=逐行属性; 多选=计数; 空=提示）
    private void UpdatePropertyPanel()
    {
        if (PropertyPanel == null || PropertyHint == null) return;
        PropertyPanel.Children.Clear();
        if (_selected.Count == 0) { PropertyHint.Text = "选中单个实体查看特性"; PropertyHint.IsVisible = true; return; }
        if (_selected.Count > 1) { PropertyHint.Text = $"选中 {_selected.Count} 个实体（单选查看特性）"; PropertyHint.IsVisible = true; return; }
        PropertyHint.IsVisible = false;
        var ent = _selected[0];
        var editable = Cad.Draw.EntityProperties.EditableLabels(ent);
        foreach (var (_, label, value) in Cad.Draw.EntityProperties.Describe(ent))
            PropertyPanel.Children.Add(editable.Contains(label) ? EditablePropRow(ent, label, value) : PropRow(label, value));
    }

    // 可编辑特性行: 值为 TextBox, 回车/失焦提交 → WithEdited 重建实体并替换。
    private Control EditablePropRow(SceneEntity ent, string label, string value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*"), Margin = new Thickness(6, 2, 6, 2) };
        var l = new TextBlock { Text = label, FontSize = 11, Foreground = Brush.Parse("#6A727C"), VerticalAlignment = VerticalAlignment.Center };
        var tb = new TextBox { Text = value, FontSize = 11, Padding = new Thickness(3, 1, 3, 1), MinHeight = 0, Background = Brush.Parse("#FBFCFD"), BorderBrush = Brush.Parse("#DCDFE4") };
        Grid.SetColumn(l, 0); Grid.SetColumn(tb, 1);
        g.Children.Add(l); g.Children.Add(tb);

        void Commit()
        {
            if (tb.Text == value) return;                       // 未改
            var edited = Cad.Draw.EntityProperties.WithEdited(ent, label, tb.Text ?? "");
            if (edited == null) { tb.Text = value; StatusMsg.Text = $"特性编辑：「{label}」输入无效，已还原"; return; }
            BeginChange();
            _scene.Replace(ent, edited);
            _selected.Clear(); _selected.Add(edited);
            RefreshScene(); HighlightSelection(); UpdatePropertyPanel();
            StatusMsg.Text = $"特性编辑：{label} 已更新";
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, ev) => { if (ev.Key == Avalonia.Input.Key.Enter) { Commit(); ev.Handled = true; } };
        return g;
    }

    private static Control PropRow(string label, string value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*"), Margin = new Thickness(6, 2, 6, 2) };
        var l = new TextBlock { Text = label, FontSize = 11, Foreground = Brush.Parse("#6A727C") };
        var v = new TextBlock { Text = value, FontSize = 11, Foreground = Brush.Parse("#2A2F36"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(l, 0); Grid.SetColumn(v, 1);
        g.Children.Add(l); g.Children.Add(v);
        return g;
    }

    // 夹点方块（小正方形轮廓，蓝色）
    private static void AppendGripSquare(List<float> o, double cx, double cy, double h)
    {
        const float r = 0.30f, g = 0.62f, b = 1.0f;
        void Seg(double x0, double y0, double x1, double y1)
        {
            o.Add((float)x0); o.Add((float)y0); o.Add(0); o.Add(r); o.Add(g); o.Add(b);
            o.Add((float)x1); o.Add((float)y1); o.Add(0); o.Add(r); o.Add(g); o.Add(b);
        }
        Seg(cx - h, cy - h, cx + h, cy - h); Seg(cx + h, cy - h, cx + h, cy + h);
        Seg(cx + h, cy + h, cx - h, cy + h); Seg(cx - h, cy + h, cx - h, cy - h);
    }

    // 夹点世界半尺寸（约 5px 换算）
    private double GripSize()
    {
        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        return SnapTolWorld(new Avalonia.Point(w / 2, h / 2)) * 0.45;
    }

    // 命中夹点：返回 _selected[0] 上距 (wx,wy) 在容差内的夹点序号，无则 -1
    private int HitGrip(double wx, double wy, double tol)
    {
        if (!_gripsOn || _selected.Count != 1) return -1;
        var grips = _selected[0].Grips();
        int best = -1; double bestD = tol * tol;
        for (int i = 0; i < grips.Count; i++)
        {
            double dx = grips[i].x - wx, dy = grips[i].y - wy, d = dx * dx + dy * dy;
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // 框选选框(世界坐标 P3_C3, 交叉=蓝/窗口=绿)
    private float[] BoxRect(Avalonia.Point a, Avalonia.Point b, bool crossing)
    {
        var c0 = Viewport.ScreenToWorld(a.X, a.Y);
        var c1 = Viewport.ScreenToWorld(b.X, a.Y);
        var c2 = Viewport.ScreenToWorld(b.X, b.Y);
        var c3 = Viewport.ScreenToWorld(a.X, b.Y);
        if (c0 == null || c1 == null || c2 == null || c3 == null) return System.Array.Empty<float>();
        float r = 0.4f, g = crossing ? 0.7f : 0.95f, bl = crossing ? 1.0f : 0.5f;
        var o = new List<float>();
        void Seg((double x, double y) p, (double x, double y) q)
        {
            o.Add((float)p.x); o.Add((float)p.y); o.Add(0); o.Add(r); o.Add(g); o.Add(bl);
            o.Add((float)q.x); o.Add((float)q.y); o.Add(0); o.Add(r); o.Add(g); o.Add(bl);
        }
        Seg(c0.Value, c1.Value); Seg(c1.Value, c2.Value); Seg(c2.Value, c3.Value); Seg(c3.Value, c0.Value);
        return o.ToArray();
    }

    // 框选：窗口选(左→右,全含)/交叉选(右→左,相交或含)
    private void BoxSelect(Avalonia.Point a, Avalonia.Point b)
    {
        var wa = Viewport.ScreenToWorld(a.X, a.Y);
        var wb = Viewport.ScreenToWorld(b.X, b.Y);
        if (wa == null || wb == null) return;
        double minX = System.Math.Min(wa.Value.x, wb.Value.x), maxX = System.Math.Max(wa.Value.x, wb.Value.x);
        double minY = System.Math.Min(wa.Value.y, wb.Value.y), maxY = System.Math.Max(wa.Value.y, wb.Value.y);
        bool crossing = b.X < a.X;
        SaveSel();
        _selected.Clear();
        foreach (var en in _scene.Entities)
        {
            if (!_layers.IsSelectable(en.LayerName)) continue;
            if (SelectionBox.Match(en, minX, minY, maxX, maxY, crossing)) _selected.Add(en);
        }
        HighlightSelection();
        StatusMsg.Text = _selected.Count > 0 ? $"框选 {_selected.Count} 个（{(crossing ? "交叉" : "窗口")}）" : "框选：未选中";
    }

    private void DeleteSelected()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "未选中实体"; return; }
        BeginChange();
        foreach (var e in _selected) _scene.Remove(e);
        int n = _selected.Count;
        _selected.Clear();
        Viewport.SetHighlight(null);
        RefreshScene();
        StatusMsg.Text = $"已删除 {n} 个实体";
    }

    // ---------- 剪贴板（COPYCLIP/CUTCLIP/PASTECLIP/PASTEORIG）+ 删除全部 ----------
    private void CopyClip()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "复制：未选中实体"; return; }
        _clip.Set(_selected);
        StatusMsg.Text = $"已复制 {_clip.Count} 个实体到剪贴板";
    }

    private void CutClip()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "剪切：未选中实体"; return; }
        _clip.Set(_selected);
        int n = _clip.Count;
        BeginChange();
        foreach (var e in _selected) _scene.Remove(e);
        _selected.Clear(); Viewport.SetHighlight(null); RefreshScene();
        StatusMsg.Text = $"已剪切 {n} 个实体到剪贴板";
    }

    private bool _pasteBaseActive;   // 基点粘贴：等待拾取插入点
    private void StartPasteBase()
    {
        if (_clip.IsEmpty) { StatusMsg.Text = "基点粘贴：剪贴板为空"; return; }
        _pasteBaseActive = true; _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "基点粘贴：点插入点（剪贴板内容质心对齐到该点）";
    }

    // 基点粘贴：剪贴板质心平移到 target 后粘入
    private void PasteAtPoint((double x, double y) target)
    {
        var c = _clip.Centroid() ?? (0.0, 0.0);
        var pasted = _clip.Paste(target.x - c.x, target.y - c.y);
        BeginChange();
        foreach (var e in pasted) _scene.Add(e);
        _selected.Clear(); _selected.AddRange(pasted);
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"基点粘贴：{pasted.Count} 个实体已插入（可继续移动）";
    }

    private void PasteClip()
    {
        if (_clip.IsEmpty) { StatusMsg.Text = "粘贴：剪贴板为空"; return; }
        var pasted = _clip.Paste(0, 0);              // 原位粘贴（克隆），选中以便随后移动
        BeginChange();
        foreach (var e in pasted) _scene.Add(e);
        _selected.Clear(); _selected.AddRange(pasted);
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"已粘贴 {pasted.Count} 个实体（已选中，可移动）";
    }

    private void EraseAll()
    {
        if (_scene.Count == 0) { StatusMsg.Text = "场景为空"; return; }
        BeginChange();
        int n = _scene.Count;
        _scene.Clear();
        _selected.Clear(); Viewport.SetHighlight(null); RefreshScene();
        StatusMsg.Text = $"已删除全部 {n} 个实体";
    }

    // ---------- 命名选择集（创建/调用）+ 刷新 + 清理标记 ----------
    private void CreateSelSet()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "创建选择集：未选中实体"; return; }
        string name = $"选择集{_selSets.Count + 1}";
        _selSets.Store(name, _selected);
        StatusMsg.Text = $"已创建「{name}」（{_selected.Count} 实体）";
    }

    private void RecallSelSet()
    {
        if (_selSets.Count == 0) { StatusMsg.Text = "调用选择集：暂无选择集（先用创建选择集）"; return; }
        _selSetCycle++;
        var s = _selSets.At(_selSetCycle);
        if (s == null) return;
        _selected.Clear();
        foreach (var e in s.Value.ents) if (_scene.Entities.Contains(e)) _selected.Add(e);   // 剔除已删
        HighlightSelection();
        StatusMsg.Text = $"调用「{s.Value.name}」（{_selected.Count} 实体，再点循环下一组）";
    }

    // 按序号调用指定命名选择集（右键子菜单用）。
    private void RecallSelSetByIndex(int idx)
    {
        var s = _selSets.At(idx);
        if (s == null) return;
        _selected.Clear();
        foreach (var e in s.Value.ents) if (_scene.Entities.Contains(e)) _selected.Add(e);
        HighlightSelection(); UpdatePropertyPanel();
        StatusMsg.Text = $"调用「{s.Value.name}」（{_selected.Count} 实体）";
    }

    // 右键菜单打开 → 动态重建「调用选择集」子菜单（忠实原上下文菜单的选择集入口）。
    private void OnCtxMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (CtxSelSets == null) return;
        CtxSelSets.Items.Clear();
        CtxSelSets.IsEnabled = _selSets.Count > 0;
        if (_selSets.Count == 0) { CtxSelSets.Items.Add(new MenuItem { Header = "（暂无，先用 创建选择集）", IsEnabled = false }); return; }
        for (int i = 0; i < _selSets.Count; i++)
        {
            var s = _selSets.At(i);
            if (s == null) continue;
            int idx = i;
            var mi = new MenuItem { Header = $"{s.Value.name}  ({s.Value.ents.Count} 项)" };
            mi.Click += (_, _) => RecallSelSetByIndex(idx);
            CtxSelSets.Items.Add(mi);
        }
    }

    // 特性 / PROPERTIES：读出选中实体的属性（常规+几何）到状态栏（完整属性面板为后续 UI 增强）
    private void ShowProperties()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "特性：未选中实体"; return; }
        if (_selected.Count > 1) { StatusMsg.Text = $"特性：选中 {_selected.Count} 个实体（单选查看详细特性）"; return; }
        var rows = Cad.Draw.EntityProperties.Describe(_selected[0]);
        var parts = new List<string>();
        foreach (var (_, label, value) in rows) parts.Add($"{label}={value}");
        string editable = string.Join("/", Cad.Draw.EntityProperties.EditableLabels(_selected[0]));
        StatusMsg.Text = "特性  " + string.Join(" · ", parts) + $"  （编辑：特性 <标签> <值>，可改 {editable}）";
    }

    // 特性编辑：无参→显示；「特性 <标签> <值>」→ 改选中实体属性(图层/颜色/几何)。忠实 EntityProperties.WithEdited(已测)。
    private void PropertiesCmd(string cmd)
    {
        int sp0 = cmd.IndexOf(' ');
        string rest = sp0 < 0 ? "" : cmd.Substring(sp0 + 1).Trim();
        if (rest.Length == 0) { ShowProperties(); return; }
        if (_selected.Count != 1) { StatusMsg.Text = "特性编辑：请单选一个实体（特性 <标签> <值>）"; return; }
        int sp1 = rest.IndexOf(' ');
        if (sp1 < 0) { StatusMsg.Text = "特性编辑：用法 特性 <标签> <值>，如「特性 颜色 #FF0000」「特性 半径 8.5」「特性 图层 煤层」"; return; }
        string label = rest.Substring(0, sp1).Trim(), value = rest.Substring(sp1 + 1).Trim();
        var edited = Cad.Draw.EntityProperties.WithEdited(_selected[0], label, value);
        if (edited == null)
        {
            string editable = string.Join("/", Cad.Draw.EntityProperties.EditableLabels(_selected[0]));
            StatusMsg.Text = $"特性编辑：无法设「{label}={value}」（该实体可改：{editable}）";
            return;
        }
        BeginChange();
        _scene.Replace(_selected[0], edited);
        _selected.Clear(); _selected.Add(edited);
        HighlightSelection();
        RefreshScene();
        StatusMsg.Text = $"特性已改：{label} = {value}";
    }

    private void Regen()   // 刷新 / REGEN：重建显示几何
    {
        RefreshScene();
        HighlightSelection();
        StatusMsg.Text = "已刷新";
    }

    private void ClrMark()   // 清理标记 / CLRMARK：清高亮/捕捉标记
    {
        Viewport.SetHighlight(null);
        Viewport.SetSnapMarker(null);
        _snapShown = false;
        StatusMsg.Text = "已清理标记";
    }

    // ---------- 选择命令（全选/最后/上次）+ 分解 ----------
    private void SaveSel() => _prevSelected = new List<SceneEntity>(_selected);

    private void SelectAll()
    {
        SaveSel();
        _selected.Clear();
        _selected.AddRange(_scene.Entities);
        HighlightSelection();
        StatusMsg.Text = $"全选 {_selected.Count} 个";
    }

    private void SelectLast()
    {
        if (_scene.Count == 0) { StatusMsg.Text = "无实体"; return; }
        SaveSel();
        _selected.Clear();
        _selected.Add(_scene.Entities[_scene.Count - 1]);
        HighlightSelection();
        StatusMsg.Text = "已选最后创建的实体";
    }

    private void SelectPrevious()
    {
        var tmp = new List<SceneEntity>(_selected);
        _selected.Clear();
        _selected.AddRange(_prevSelected);
        _prevSelected = tmp;
        HighlightSelection();
        StatusMsg.Text = $"恢复上次选择 {_selected.Count} 个";
    }

    // 取消选择：清空当前选择集(保留为"上次"以便"上次"恢复)，清高亮；不动视图/捕捉标记（忠实原 SelectNone）
    private void DeselectAll()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "当前无选择"; return; }
        SaveSel();
        int n = _selected.Count;
        _selected.Clear();
        Viewport.SetHighlight(null);
        RefreshScene();
        StatusMsg.Text = $"已取消选择（{n} 个；「上次」可恢复）";
    }

    private void ExplodeSelected()
    {
        var explodable = _selected.FindAll(e => e.Explode() != null);
        if (explodable.Count == 0) { StatusMsg.Text = "无可分解实体（矩形/多段线）"; return; }
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (var e in explodable)
        {
            var parts = e.Explode()!;
            _scene.Remove(e);
            foreach (var p in parts) { _scene.Add(p); newSel.Add(p); }
        }
        _selected.Clear();
        _selected.AddRange(newSel);
        HighlightSelection();
        RefreshScene();
        StatusMsg.Text = $"已分解为 {newSel.Count} 段";
    }

    // 各点表的包围盒对角(供自动取参用)
    private static double PolyDiag(IReadOnlyList<(double x, double y)> pts)
    {
        if (pts == null || pts.Count == 0) return 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in pts) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
        double dx = maxX - minX, dy = maxY - minY;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    // 加密多段线：逐段按最大步长匀分插点(步长=各自包围盒对角/50, 自动取)
    private void DensifySelectedPolylines()
    {
        var polys = new List<PolylineEntity>();
        foreach (var e in _selected) if (e is PolylineEntity p) polys.Add(p);
        if (polys.Count == 0) { StatusMsg.Text = "加密多段线：请先选中多段线"; return; }
        BeginChange();
        int ov = 0, fv = 0; var newSel = new List<SceneEntity>();
        foreach (var p in polys)
        {
            double diag = PolyDiag(p.Points);
            double step = diag > 0 ? diag / 50.0 : 1.0;
            var densified = PolylineEdit.Densify(p.Points, p.Closed, step);
            var np = new PolylineEntity { Closed = p.Closed, Cr = p.Cr, Cg = p.Cg, Cb = p.Cb, LayerName = p.LayerName };
            np.Points.AddRange(densified);
            ov += p.Points.Count; fv += densified.Count;
            _scene.Remove(p); _scene.Add(np); newSel.Add(np);
        }
        _selected.Clear(); _selected.AddRange(newSel);
        HighlightSelection(); RefreshScene();
        StatusMsg.Text = $"加密多段线：{polys.Count} 条 · 顶点 {ov}→{fv}(插入 {fv - ov})";
    }

    // 把选中实体抽成 (点表, 是否闭合) 序列(供交点用)：支持直线/多段线/矩形
    private static bool AsSequence(SceneEntity e, out List<(double x, double y)> pts, out bool closed)
    {
        pts = new List<(double, double)>(); closed = false;
        switch (e)
        {
            case LineEntity l: pts.Add((l.X0, l.Y0)); pts.Add((l.X1, l.Y1)); return true;
            case PolylineEntity p: pts.AddRange(p.Points); closed = p.Closed; return pts.Count >= 2;
            case RectEntity r:
                pts.Add((r.X0, r.Y0)); pts.Add((r.X1, r.Y0)); pts.Add((r.X1, r.Y1)); pts.Add((r.X0, r.Y1));
                closed = true; return true;
            default: return false;
        }
    }

    // 两线交点：选中的线/多段线/矩形两两求段交点 → 各交点作 Point 标注入场景
    private void IntersectSelectedPolylines()
    {
        var seqs = new List<(List<(double x, double y)> pts, bool closed)>();
        foreach (var e in _selected)
            if (AsSequence(e, out var pts, out var closed)) seqs.Add((pts, closed));
        if (seqs.Count < 2) { StatusMsg.Text = "两线交点：请先选中至少两条线/多段线/矩形"; return; }
        // 去重容差按参与点的包围盒对角取
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in seqs) foreach (var (x, y) in s.pts) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
        double diag = System.Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        double tol = diag > 0 ? diag * 1e-6 : 1e-6;
        var all = new List<(double x, double y)>();
        for (int i = 0; i < seqs.Count; i++)
            for (int j = i + 1; j < seqs.Count; j++)
            {
                var hits = PolylineIntersect.Between(seqs[i].pts, seqs[i].closed, seqs[j].pts, seqs[j].closed, tol);
                foreach (var h in hits)
                {
                    bool dup = false; double t2 = tol * tol;
                    foreach (var q in all) { double dx = q.x - h.x, dy = q.y - h.y; if (dx * dx + dy * dy <= t2) { dup = true; break; } }
                    if (!dup) all.Add(h);
                }
            }
        if (all.Count == 0) { StatusMsg.Text = "两线交点：所选线之间无交点"; return; }
        BeginChange();
        foreach (var (x, y) in all)
        {
            var pe = new PointEntity { X = x, Y = y, Cr = 0.95f, Cg = 0.3f, Cb = 0.3f };   // 红点标交点
            _scene.Add(pe);
        }
        RefreshScene();
        StatusMsg.Text = $"两线交点：{seqs.Count} 条线 → {all.Count} 个交点(已作红点标注)";
    }

    // 闭合多段线：把选中未闭合多段线置为闭合
    private void CloseSelectedPolylines()
    {
        var open = new List<PolylineEntity>();
        foreach (var e in _selected) if (e is PolylineEntity p && !p.Closed && p.Points.Count >= 3) open.Add(p);
        if (open.Count == 0) { StatusMsg.Text = "闭合多段线：请先选中未闭合的多段线(≥3 点)"; return; }
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (var p in open)
        {
            var np = new PolylineEntity { Closed = true, Cr = p.Cr, Cg = p.Cg, Cb = p.Cb, LayerName = p.LayerName };
            np.Points.AddRange(p.Points);
            _scene.Replace(p, np); newSel.Add(np);
        }
        _selected.Clear(); _selected.AddRange(newSel);
        HighlightSelection(); RefreshScene();
        StatusMsg.Text = $"闭合多段线：已闭合 {open.Count} 条";
    }

    // 删除重复点：选中的点按容差(1e-3)去重, 移除重复者
    private void DedupeSelectedPoints()
    {
        var pts = new List<PointEntity>();
        foreach (var e in _selected) if (e is PointEntity pe) pts.Add(pe);
        if (pts.Count < 2) { StatusMsg.Text = "删除重复点：请先选中多个点"; return; }
        var coords = new List<(double x, double y)>();
        foreach (var p in pts) coords.Add((p.X, p.Y));
        var keep = new HashSet<int>(GeomDedup.KeepAfterDedup(coords, 1e-3));
        int removed = 0;
        BeginChange();
        var newSel = new List<SceneEntity>();
        for (int i = 0; i < pts.Count; i++)
        {
            if (keep.Contains(i)) newSel.Add(pts[i]);
            else { _scene.Remove(pts[i]); removed++; }
        }
        _selected.Clear(); _selected.AddRange(newSel);
        HighlightSelection(); RefreshScene();
        StatusMsg.Text = $"删除重复点：原 {pts.Count} · 删除 {removed} · 余 {pts.Count - removed}";
    }

    // 删除重复线：选中的多段线按几何(正/反向等长重合)去重, 移除重复者
    private void DedupeSelectedPolylines()
    {
        var polys = new List<PolylineEntity>();
        foreach (var e in _selected) if (e is PolylineEntity p) polys.Add(p);
        if (polys.Count < 2) { StatusMsg.Text = "删除重复线：请先选中多条多段线"; return; }
        var kept = new List<PolylineEntity>();
        var dups = new List<PolylineEntity>();
        foreach (var p in polys)
        {
            bool isDup = false;
            foreach (var q in kept)
                if (GeomDedup.SamePolyline(p.Points, p.Closed, q.Points, q.Closed, 1e-3)) { isDup = true; break; }
            if (isDup) dups.Add(p); else kept.Add(p);
        }
        if (dups.Count == 0) { StatusMsg.Text = "删除重复线：所选无重复"; return; }
        BeginChange();
        foreach (var d in dups) _scene.Remove(d);
        _selected.Clear();
        foreach (var k in kept) _selected.Add(k);
        HighlightSelection(); RefreshScene();
        StatusMsg.Text = $"删除重复线：原 {polys.Count} · 删除 {dups.Count} · 余 {kept.Count}";
    }

    // 道路横断面：选一条中线折线 → 按曲率算弯道加宽/超高 → 左右加宽路缘线入场景 + 报表
    private void RoadCrossSectionCmd()
    {
        PolylineEntity? center = null;
        foreach (var e in _selected) if (e is PolylineEntity p && p.Points.Count >= 2) { center = p; break; }
        if (center == null) { StatusMsg.Text = "道路横断面：请先选中一条中线多段线(≥2 点)"; return; }
        // 典型露天矿运输道路参数(本环境无参数对话框, 取标准默认)
        const double baseWidth = 15.0, widenThreshold = 100.0, wheelbase = 6.0, designSpeed = 30.0, maxSuper = 8.0;
        const int laneCount = 2;
        var pts = new List<(double X, double Y, double Z)>(center.Points.Count);
        foreach (var (x, y) in center.Points) pts.Add((x, y, 0));
        var cs = RoadCrossSection.ComputeAlong(pts, baseWidth, widenThreshold, laneCount, wheelbase, designSpeed, maxSuper);
        // 逐站按半宽沿法向偏移出左右路缘
        var left = new PolylineEntity { Cr = 0.6f, Cg = 0.6f, Cb = 0.65f };
        var right = new PolylineEntity { Cr = 0.6f, Cg = 0.6f, Cb = 0.65f };
        int n = center.Points.Count;
        for (int k = 0; k < n; k++)
        {
            // 法向：相邻段方向均值的垂直
            double dx, dy;
            var cur = center.Points[k];
            if (k == 0) { dx = center.Points[1].Item1 - cur.Item1; dy = center.Points[1].Item2 - cur.Item2; }
            else if (k == n - 1) { dx = cur.Item1 - center.Points[k - 1].Item1; dy = cur.Item2 - center.Points[k - 1].Item2; }
            else { dx = center.Points[k + 1].Item1 - center.Points[k - 1].Item1; dy = center.Points[k + 1].Item2 - center.Points[k - 1].Item2; }
            double len = System.Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { dx = 1; dy = 0; len = 1; }
            double nxp = -dy / len, nyp = dx / len;           // 左法向
            double half = cs.WidthM[k] / 2.0;
            left.Points.Add((cur.Item1 + nxp * half, cur.Item2 + nyp * half));
            right.Points.Add((cur.Item1 - nxp * half, cur.Item2 - nyp * half));
        }
        BeginChange();
        _scene.Add(left); _scene.Add(right);
        RefreshScene();
        StatusMsg.Text = $"道路横断面(基宽{baseWidth:0.#}m·{laneCount}道·V{designSpeed:0}km/h)：最大加宽 {cs.MaxWideningM:0.##}m · 最大超高 {cs.MaxSuperelevationPct:0.#}% · 加宽段长 {cs.WidenedLengthM:0.#}m(左右路缘已入场景)";
    }

    // 开采程序确定·工作线推进：选中折线为拉沟, 按推进方式生成各步工作线(绿→红渐变)入场景
    private void AdvanceCmd(AdvanceMode mode, string label)
    {
        PolylineEntity? boxcut = null;
        foreach (var e in _selected) if (e is PolylineEntity p && p.Points.Count >= 2) { boxcut = p; break; }
        if (boxcut == null) { StatusMsg.Text = $"{label}：请先选中一条工作线(拉沟)多段线"; return; }
        int n = boxcut.Points.Count;
        var flat = new double[n * 2];
        for (int i = 0; i < n; i++) { flat[i * 2] = boxcut.Points[i].Item1; flat[i * 2 + 1] = boxcut.Points[i].Item2; }
        // 推进方位 = 拉沟首末方向的法向(朝远离质心侧)
        double dx = boxcut.Points[^1].Item1 - boxcut.Points[0].Item1, dy = boxcut.Points[^1].Item2 - boxcut.Points[0].Item2;
        double nlen = System.Math.Sqrt(dx * dx + dy * dy); if (nlen < 1e-9) { dx = 1; dy = 0; nlen = 1; }
        double az = System.Math.Atan2(dx / nlen, -dy / nlen) * 180.0 / System.Math.PI;   // 法向方位(度)
        double cx = 0, cy = 0; foreach (var (px, py) in boxcut.Points) { cx += px; cy += py; } cx /= n; cy /= n;
        // 采宽默认 = 拉沟长度/10(尺度稳健), 8 步
        double stepB = nlen > 0 ? System.Math.Max(nlen / 10.0, 1.0) : 30.0;
        // 定点回转瞬心：拉沟质心沿反法向退 3 倍长度(给一个远瞬心 → 缓弯)
        double az2 = az * System.Math.PI / 180.0;
        double pivotX = cx - System.Math.Cos(az2) * nlen * 3, pivotY = cy - System.Math.Sin(az2) * nlen * 3;
        var lines = AdvancePlanner.GenerateWorkingLines(flat, mode, az, pivotX, pivotY, stepB, 8);
        if (lines.Count <= 1) { StatusMsg.Text = $"{label}：生成失败(参数无效)"; return; }
        BeginChange();
        for (int k = 1; k < lines.Count; k++)   // index0=拉沟自身, 跳过
        {
            float t = (float)k / (lines.Count - 1);
            var pl = new PolylineEntity { Closed = boxcut.Closed, Cr = t, Cg = 0.85f - 0.5f * t, Cb = 1f - t };
            var arr = lines[k];
            for (int i = 0; i + 1 < arr.Length; i += 2) pl.Points.Add((arr[i], arr[i + 1]));
            _scene.Add(pl);
        }
        RefreshScene();
        StatusMsg.Text = $"{label}：{lines.Count - 1} 步工作线(采宽 {stepB:0.#}m, 方位 {az:0.#}°) 已入场景";
    }

    // 螺旋斜坡道中线：默认参数(半径50·2圈·纵坡8%)于视图中心生成螺旋中线折线入场景
    private void SpiralRampCmd()
    {
        var (cx, cy) = ViewCenterWorld();
        var pts = RampCenterlines.Spiral(cx, cy, 0, radius: 50, startAngleDeg: 0, turns: 2, ccw: true, gradePct: 8);
        if (pts.Count < 2) { StatusMsg.Text = "螺旋斜坡道：参数无效"; return; }
        var pl = new PolylineEntity { Cr = 0.30f, Cg = 0.95f, Cb = 0.95f };
        foreach (var (x, y, _) in pts) pl.Points.Add((x, y));
        BeginChange();
        _scene.Add(pl);
        RefreshScene();
        StatusMsg.Text = $"螺旋斜坡道中线：半径50·2圈·纵坡8% → {pts.Count} 点(青, 已入场景; Z 待贴面重定)";
    }

    // 折返斜坡道中线：默认参数(3腿·腿长100·纵坡8%·回头弧R20)于视图中心生成折返中线折线入场景
    private void SwitchbackRampCmd()
    {
        var (sx, sy) = ViewCenterWorld();
        var pts = RampCenterlines.Switchback(sx, sy, 0, azimuthDeg: 0, turnSide: +1, legs: 3,
            legLength: 100, gradePct: 8, curveGradePct: 4, radius: 20);
        if (pts.Count < 2) { StatusMsg.Text = "折返斜坡道：参数无效"; return; }
        var pl = new PolylineEntity { Cr = 0.95f, Cg = 0.55f, Cb = 0.20f };
        foreach (var (x, y, _) in pts) pl.Points.Add((x, y));
        BeginChange();
        _scene.Add(pl);
        RefreshScene();
        StatusMsg.Text = $"折返斜坡道中线：3腿·腿长100·纵坡8%·回头R20 → {pts.Count} 点(橙, 已入场景; Z 待贴面重定)";
    }

    // 视图中心的世界坐标(生成体放置点)；取不到时退回原点
    private (double x, double y) ViewCenterWorld()
    {
        try
        {
            var w = Viewport.ScreenToWorld(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2);
            return w.HasValue ? (w.Value.x, w.Value.y) : (0, 0);
        }
        catch { return (0, 0); }
    }

    // 区域求差：选两条闭合多段线(第1=被减 subject, 第2=减去 clip)→ subject∖clip 最大块作新闭合多段线
    private void SubtractRegions()
    {
        var polys = new List<PolylineEntity>();
        foreach (var e in _selected) if (e is PolylineEntity p && p.Points.Count >= 3) polys.Add(p);
        if (polys.Count != 2) { StatusMsg.Text = "区域求差：请按序选中两条闭合多段线(第1=被减, 第2=减去)"; return; }
        var diff = RegionBool.SubtractKeepLargest(polys[0].Points, polys[1].Points);
        if (diff.Count < 3) { StatusMsg.Text = "区域求差：结果为空(被减区域被完全覆盖)"; return; }
        var np = new PolylineEntity { Closed = true, Cr = 0.95f, Cg = 0.75f, Cb = 0.25f };   // 橙色差集
        np.Points.AddRange(diff);
        BeginChange();
        _scene.Add(np);
        RefreshScene();
        StatusMsg.Text = $"区域求差：subject∖clip → {diff.Count} 顶点(橙色, 已入场景)";
    }

    // 区域重叠检测：选两条闭合多段线 → 是否成片重叠(重叠面积占较小者 ≥2%)
    private void CheckRegionOverlap()
    {
        var polys = new List<PolylineEntity>();
        foreach (var e in _selected) if (e is PolylineEntity p && p.Points.Count >= 3) polys.Add(p);
        if (polys.Count != 2) { StatusMsg.Text = "区域重叠检测：请选中两条闭合多段线"; return; }
        bool ov = RegionBool.Overlaps(polys[0].Points, polys[1].Points);
        StatusMsg.Text = ov ? "区域重叠检测：两区域成片重叠(≥2%)——空间不互斥" : "区域重叠检测：两区域不重叠(或仅边界相邻)——互斥";
    }

    // 进入编辑（移动/复制/镜像）：需已有选择
    private void StartEdit(EditMode mode, string name)
    {
        if (_selected.Count == 0) { StatusMsg.Text = $"{name}：请先选实体"; return; }
        _editMode = mode; _editPts.Clear(); _tool = null; _measure = null; _lastInputPoint = null;
        StatusMsg.Text = mode == EditMode.Mirror ? $"{name}：指定镜像线第一点" : $"{name}：指定基点";
    }

    private void StartOffset()
    {
        if (_selected.Count != 1) { StatusMsg.Text = "偏移：请先选中一个实体"; return; }
        _offsetActive = true; _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "偏移：点击偏移到的一侧";
    }

    private void StartTrim()
    {
        if (_selected.Count != 1)
        { StatusMsg.Text = "修剪/延伸：请先选一个作为边界的实体（线/多段线/圆/弧/矩形）"; return; }
        _trimActive = true; _tool = null; _measure = null; _editMode = EditMode.None; _offsetActive = false;
        StatusMsg.Text = "点击要修剪/延伸的直线（近端点移到与边界最近交点）";
    }

    // 圆心轨迹：线→向点击侧偏移 r 的直线；圆→同心圆(外切 rc+r / 内切 |rc-r|，按点击在圆外/内)
    private readonly struct Locus
    {
        public readonly bool IsLine;
        public readonly double A, B, C, D;   // 线:x0,y0,x1,y1 ; 圆:cx,cy,r,(-)
        private Locus(bool line, double a, double b, double c, double d) { IsLine = line; A = a; B = b; C = c; D = d; }
        public static Locus Line(double x0, double y0, double x1, double y1) => new(true, x0, y0, x1, y1);
        public static Locus Circle(double cx, double cy, double r) => new(false, cx, cy, r, 0);
    }

    private static Locus? LocusOf(SceneEntity e, (double x, double y) pick, double r)
    {
        if (e is LineEntity l)
        {
            var o = LineMath.OffsetToward(l.X0, l.Y0, l.X1, l.Y1, pick.x, pick.y, r);
            return o == null ? (Locus?)null : Locus.Line(o.Value.x0, o.Value.y0, o.Value.x1, o.Value.y1);
        }
        if (e is CircleEntity c)
        {
            double dp = System.Math.Sqrt((pick.x - c.Cx) * (pick.x - c.Cx) + (pick.y - c.Cy) * (pick.y - c.Cy));
            double lr = dp > c.Radius ? c.Radius + r : System.Math.Abs(c.Radius - r);   // 点击在圆外→外切
            return lr < 1e-9 ? (Locus?)null : Locus.Circle(c.Cx, c.Cy, lr);
        }
        return null;
    }

    private static List<(double x, double y)> IntersectLoci(Locus a, Locus b)
    {
        if (a.IsLine && b.IsLine)
        {
            var p = LineMath.IntersectInfinite(a.A, a.B, a.C, a.D, b.A, b.B, b.C, b.D);
            return p == null ? new List<(double x, double y)>() : new List<(double x, double y)> { p.Value };
        }
        if (a.IsLine) return LineMath.IntersectLineCircle(a.A, a.B, a.C, a.D, b.A, b.B, b.C);
        if (b.IsLine) return LineMath.IntersectLineCircle(b.A, b.B, b.C, b.D, a.A, a.B, a.C);
        return LineMath.IntersectCircleCircle(a.A, a.B, a.C, b.A, b.B, b.C);
    }

    // TTR：求与两参照(线/圆)相切、半径 r 的圆心，取离两点击中点最近的候选解
    private (double x, double y)? TtrSolveCenter(SceneEntity r1, (double x, double y) p1, SceneEntity r2, (double x, double y) p2, double r)
    {
        var l1 = LocusOf(r1, p1, r); var l2 = LocusOf(r2, p2, r);
        if (l1 == null || l2 == null) return null;
        var cands = IntersectLoci(l1.Value, l2.Value);
        if (cands.Count == 0) return null;
        double mx = (p1.x + p2.x) / 2, my = (p1.y + p2.y) / 2;
        (double x, double y)? best = null; double bestD = double.MaxValue;
        foreach (var c in cands) { double d = (c.x - mx) * (c.x - mx) + (c.y - my) * (c.y - my); if (d < bestD) { bestD = d; best = c; } }
        return best;
    }

    private void StartTTR()
    {
        _ttrActive = true; _ttrAwaitRadius = false; _ttrRef1 = null; _ttrRef2 = null;
        _tool = null; _measure = null; _editMode = EditMode.None;
        _offsetActive = false; _trimActive = false; _breakActive = false;
        StatusMsg.Text = "圆TTR：点第一个相切参照（直线或圆，须先有两个）";
    }

    private void StartArcSer()
    {
        _serActive = true; _serAwaitRadius = false; _serStart = null; _serEnd = null;
        _tool = null; _measure = null; _editMode = EditMode.None;
        _offsetActive = false; _trimActive = false; _breakActive = false; _ttrActive = false;
        StatusMsg.Text = "圆弧SER：指定起点";
    }

    private void StartBreak()
    {
        if (_selected.Count != 1 || _selected[0] is not (LineEntity or PolylineEntity or ArcEntity))
        { StatusMsg.Text = "打断：请先选一条直线/多段线/圆弧"; return; }
        _breakActive = true; _breakPts.Clear();
        _tool = null; _measure = null; _editMode = EditMode.None; _offsetActive = false; _trimActive = false;
        StatusMsg.Text = "打断：指定第一点（两点间的一段将被移除）";
    }

    // 批量台阶扩帮(几何核)：选中闭合多段线 → 逐圈定距内偏移生成台阶顶线。
    // benchD 给定(=真实 W+H/tanα)时用之；否则回落境界短边/10 的几何默认。
    private void GenerateBenchLines(double? benchD = null)
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity pl || !pl.Closed || pl.Points.Count < 3)
        { StatusMsg.Text = "批量台阶扩帮：请先选中一条闭合多段线(境界)"; return; }
        double d;
        string basis;
        if (benchD is > 1e-6) { d = benchD.Value; basis = "帮参数 W+H/tanα"; }
        else
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pl.Points) { minX = System.Math.Min(minX, p.x); minY = System.Math.Min(minY, p.y); maxX = System.Math.Max(maxX, p.x); maxY = System.Math.Max(maxY, p.y); }
            d = System.Math.Max(System.Math.Min(maxX - minX, maxY - minY) / 10.0, 1e-6);
            basis = "境界短边/10(可 台阶扩帮 帮宽 台阶高 坡面角 用真实距)";
        }
        var rings = BenchLines.Generate(pl.Points, d, 20);
        if (rings.Count == 0) { StatusMsg.Text = "批量台阶扩帮：未生成台阶线(境界过小/自交)"; return; }
        BeginChange();
        foreach (var ring in rings)
        {
            var bl = new PolylineEntity { Closed = true };
            bl.Points.AddRange(ring);
            AssignLayer(bl);
            _scene.Add(bl);
        }
        RefreshScene();
        StatusMsg.Text = $"批量台阶扩帮：生成 {rings.Count} 圈台阶线(台阶距 {d:0.##} · {basis})";
    }

    // GIZMO：切换夹点显示；关时选中实体不显方块、也不可拖夹点
    private void ToggleGizmo()
    {
        _gripsOn = !_gripsOn;
        if (!_gripsOn) { _gripIndex = -1; }
        HighlightSelection();
        StatusMsg.Text = _gripsOn ? "夹点：开" : "夹点：关";
    }

    private void StartSlide()
    {
        _tool = null; _measure = null; _editMode = EditMode.None; _editPts.Clear();
        _offsetActive = false; _trimActive = false;
        _slideActive = true; _slideDragging = false; _slidePts.Clear();
        Viewport.SetSnapMarker(null); _snapShown = false;
        StatusMsg.Text = "滑动多段线：在视口按住左键拖动采样，松开成线（ESC 退出）";
    }

    // 图层特性：作用于当前图层（当前层可用 图层特性管理器 循环切换）
    private void FreezeCurrentLayer(bool freeze)
    {
        _layers.Current.Frozen = freeze;
        AfterLayerStateChange();
        PopulateDrawingLayers();
        StatusMsg.Text = $"图层「{_layers.Current.Name}」{(freeze ? "已冻结（隐藏且不可选）" : "已解冻")}";
    }

    // 删除当前图层：默认层「0」不可删；该层实体移到「0」层不丢；当前切至「0」；可撤销（忠实原 DeleteLayer 语义）
    private void DeleteCurrentLayer()
    {
        var target = _layers.Current;
        if (target.Name == "0") { StatusMsg.Text = "默认图层「0」不可删除"; return; }
        BeginChange();
        int moved = _scene.ReassignLayer(target.Name, "0");
        _layers.SetCurrent("0");
        _layers.Remove(target.Name);
        PopulateDrawingLayers();
        AfterLayerStateChange();
        RefreshScene();
        StatusMsg.Text = $"已删除图层「{target.Name}」（{moved} 个实体移至图层 0，当前切至 0）";
    }
    private void LockCurrentLayer(bool locked)
    {
        _layers.Current.Locked = locked;
        AfterLayerStateChange();
        PopulateDrawingLayers();
        StatusMsg.Text = $"图层「{_layers.Current.Name}」{(locked ? "已锁定（可见不可选）" : "已解锁")}";
    }
    private void LayersAllOn()
    {
        _layers.AllOn();
        AfterLayerStateChange();
        PopulateDrawingLayers();
        StatusMsg.Text = "所有图层已打开（解冻）";
    }

    // 隐藏对象：选中实体 Visible=false（忠实 OnCtxHideObjectClick）。不可拾取、不上屏、不参与捕捉。
    private void HideSelectedObjects()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "隐藏对象：没有选中实体"; return; }
        int n = _scene.HideEntities(_selected);
        _selected.Clear(); Viewport.SetHighlight(null);
        RefreshScene();
        StatusMsg.Text = $"隐藏对象：{n} 个（结束隐藏可恢复，共隐藏 {_scene.HiddenCount}）";
    }

    // 隐藏同一图层对象：把选中实体所在图层整体隐藏（忠实 OnCtxHideLayerClick），记录层名待恢复。
    private void HideSelectedLayers()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "隐藏同一图层对象：没有选中实体"; return; }
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var e in _selected) names.Add(e.LayerName);
        int ok = 0;
        foreach (var name in names)
        {
            var ly = _layers.Get(name);
            if (ly != null && ly.Visible) { ly.Visible = false; _hiddenLayers.Add(name); ok++; }
        }
        _selected.Clear(); Viewport.SetHighlight(null);
        AfterLayerStateChange(); PopulateDrawingLayers();
        StatusMsg.Text = $"隐藏图层 {ok} 个：{string.Join(", ", names)}（结束隐藏可恢复）";
    }

    // 结束隐藏：恢复所有被隐藏实体 + 本会话隐藏的图层（忠实 OnCtxShowAllClick）。
    private void EndHide()
    {
        int eOk = _scene.ShowAllHidden();
        int lOk = 0;
        foreach (var name in _hiddenLayers)
        {
            var ly = _layers.Get(name);
            if (ly != null && !ly.Visible) { ly.Visible = true; lOk++; }
        }
        int lTotal = _hiddenLayers.Count;
        _hiddenLayers.Clear();
        AfterLayerStateChange(); PopulateDrawingLayers();
        RefreshScene();
        StatusMsg.Text = $"结束隐藏：恢复实体 {eOk} 个，恢复图层 {lOk}/{lTotal} 个";
    }

    // 命令行精确坐标：绘制/编辑取点时把 "x,y" / "@dx,dy" / "@d<ang" 当作一次点击
    private bool TryCoordinateInput(string cmd)
    {
        if (_tool == null && _editMode == EditMode.None) return false;   // 仅取点态接受坐标
        var pt = ParseCoord(cmd, _lastInputPoint);
        if (pt == null) return false;
        FeedPoint(pt.Value.x, pt.Value.y);
        return true;
    }

    /// <summary>解析坐标：x,y=绝对直角；@dx,dy=相对；d&lt;ang=绝对极(角度°)；@d&lt;ang=相对极。无法解析返回 null。</summary>
    internal static (double x, double y)? ParseCoord(string s, (double x, double y)? last)
    {
        s = s.Trim();
        bool rel = s.StartsWith("@");
        if (rel) s = s.Substring(1).Trim();

        int lt = s.IndexOf('<');
        if (lt > 0)   // 极坐标 距离<角度
        {
            if (double.TryParse(s.Substring(0, lt).Trim(), out double dist) &&
                double.TryParse(s.Substring(lt + 1).Trim(), out double ang))
            {
                double rad = ang * System.Math.PI / 180.0;
                double dx = dist * System.Math.Cos(rad), dy = dist * System.Math.Sin(rad);
                if (!rel) return (dx, dy);
                return last == null ? null : (last.Value.x + dx, last.Value.y + dy);
            }
            return null;
        }

        int comma = s.IndexOf(',');
        if (comma > 0)   // 直角 x,y
        {
            if (double.TryParse(s.Substring(0, comma).Trim(), out double x) &&
                double.TryParse(s.Substring(comma + 1).Trim(), out double y))
            {
                if (!rel) return (x, y);
                return last == null ? null : (last.Value.x + x, last.Value.y + y);
            }
        }
        return null;
    }

    // 把一个世界点喂给当前取点态(编辑/绘制)，等效一次点击
    private void FeedPoint(double x, double y)
    {
        _lastInputPoint = (x, y);
        if (_editMode != EditMode.None)
        {
            _editPts.Add((x, y));
            if (_editPts.Count >= EditPointCount(_editMode))
            {
                ApplyEditTransform(BuildEditTransform(), _editMode == EditMode.Copy);
                _editMode = EditMode.None; _editPts.Clear();
                StatusMsg.Text = "编辑完成";
            }
            else StatusMsg.Text = EditPrompt(_editMode, _editPts.Count);
            return;
        }
        if (_tool != null)
        {
            var ent = _tool.AddPoint(x, y);
            if (ent != null) { BeginChange(); AssignLayer(ent); _scene.Add(ent); }
            RefreshScene();
            StatusMsg.Text = $"{_tool.Prompt}（已画 {_scene.Count}）";
        }
    }

    private static double Dist2((double x, double y) p, double x, double y)
        => (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y);

    // 修剪/延伸：目标直线的近点击端 移到 与边界(任意实体, 镶嵌成段)最近的交点
    private LineEntity? TrimExtend(LineEntity target, SceneEntity boundary, (double x, double y) click)
    {
        var o = new List<float>();
        boundary.Tessellate(o);
        double d0 = Dist2(click, target.X0, target.Y0), d1 = Dist2(click, target.X1, target.Y1);
        bool moveStart = d0 < d1;
        double ex = moveStart ? target.X0 : target.X1, ey = moveStart ? target.Y0 : target.Y1;
        (double x, double y)? best = null; double bestD = double.MaxValue;
        for (int i = 0; i + 11 < o.Count; i += 12)
        {
            var isect = LineMath.IntersectInfiniteWithSegment(
                target.X0, target.Y0, target.X1, target.Y1, o[i], o[i + 1], o[i + 6], o[i + 7]);
            if (isect == null) continue;
            double d = Dist2(isect.Value, ex, ey);
            if (d < bestD) { bestD = d; best = isect; }
        }
        if (best == null) return null;
        var nl = (LineEntity)target.Apply(Affine2.Translate(0, 0));
        if (moveStart) { nl.X0 = best.Value.x; nl.Y0 = best.Value.y; }
        else { nl.X1 = best.Value.x; nl.Y1 = best.Value.y; }
        return nl;
    }

    private static int EditPointCount(EditMode m) => m == EditMode.Scale ? 3 : 2;

    private static string EditPrompt(EditMode m, int have) => (m, have) switch
    {
        (EditMode.Mirror, 1) => "镜像：指定镜像线第二点",
        (EditMode.Rotate, 1) => "旋转：指定旋转角参照点",
        (EditMode.Scale, 1) => "缩放：指定参考长度点",
        (EditMode.Scale, 2) => "缩放：指定新长度点",
        _ => "指定目标点"
    };

    private Affine2 BuildEditTransform()
    {
        var p = _editPts;
        switch (_editMode)
        {
            case EditMode.Move:
            case EditMode.Copy:
                return Affine2.Translate(p[1].x - p[0].x, p[1].y - p[0].y);
            case EditMode.Mirror:
                return Affine2.MirrorLine(p[0].x, p[0].y, p[1].x, p[1].y);
            case EditMode.Rotate:
                return Affine2.Rotate(System.Math.Atan2(p[1].y - p[0].y, p[1].x - p[0].x), p[0].x, p[0].y);
            case EditMode.Scale:
            {
                double refLen = System.Math.Sqrt((p[1].x - p[0].x) * (p[1].x - p[0].x) + (p[1].y - p[0].y) * (p[1].y - p[0].y));
                double newLen = System.Math.Sqrt((p[2].x - p[0].x) * (p[2].x - p[0].x) + (p[2].y - p[0].y) * (p[2].y - p[0].y));
                double f = refLen < 1e-9 ? 1 : newLen / refLen;
                return Affine2.Scale(f, p[0].x, p[0].y);
            }
            default: return Affine2.Translate(0, 0);
        }
    }

    // 对选择集施加仿射变换；copy=true 则加副本，否则替换原实体
    private void ApplyEditTransform(Affine2 m, bool copy)
    {
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (var e in _selected)
        {
            var e2 = e.Apply(m);
            if (copy) _scene.Add(e2); else _scene.Replace(e, e2);
            newSel.Add(e2);
        }
        _selected.Clear();
        _selected.AddRange(newSel);
        RefreshScene();
        HighlightSelection();
    }

    // 命令行回车 → 命令分发（已实装的走功能，其余回显）
    private string? _lastCommand;   // 上次成功派发的命令（空命令行 + Enter 重复用）
    private bool _orthoOn;          // 正交约束（ORTHO）
    private bool _snapOn;           // 栅格捕捉（SNAP）
    private bool _syncToggle;       // 防状态栏开关↔命令/键 同步回环

    // 状态栏「正交」开关 → _orthoOn
    private void OnOrthoToggle(object? sender, RoutedEventArgs e)
    {
        if (_syncToggle) return;
        _orthoOn = OrthoToggle.IsChecked == true;
        StatusMsg.Text = _orthoOn ? "正交: 开（取点锁定水平/垂直）" : "正交: 关";
    }

    // 状态栏「栅格捕捉」开关 → _snapOn
    private void OnGridSnapToggle(object? sender, RoutedEventArgs e)
    {
        if (_syncToggle) return;
        _snapOn = GridSnapToggle.IsChecked == true;
        StatusMsg.Text = _snapOn ? $"栅格捕捉: 开（步长 {_snapStep:0.##}）" : "栅格捕捉: 关";
    }

    // 命令/键切换正交/栅格后：同步状态栏开关按钮视觉态
    private void SyncDraftToggles()
    {
        _syncToggle = true;
        if (OrthoToggle != null) OrthoToggle.IsChecked = _orthoOn;
        if (GridSnapToggle != null) GridSnapToggle.IsChecked = _snapOn;
        _syncToggle = false;
    }
    private double _snapStep = 1.0; // 栅格步长（世界单位）

    /// <summary>按开关对点应用栅格捕捉 / 正交约束（默认关闭 → 原样返回）。osnap 命中的点不应调用此(osnap 优先)。</summary>
    private (double x, double y) ApplyDraftAids(double x, double y)
    {
        if (_snapOn) (x, y) = Cad.Draw.DraftAids.Snap(x, y, _snapStep);
        if (_orthoOn && _lastInputPoint != null) (x, y) = Cad.Draw.DraftAids.Ortho(_lastInputPoint.Value.x, _lastInputPoint.Value.y, x, y);
        return (x, y);
    }

    /// <summary>取当前世界点：对象捕捉优先，其后按开关应用栅格捕捉 / 正交约束（默认关闭 → 等价原逻辑）。</summary>
    private (double x, double y)? PickWorld()
    {
        var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
        if (wp == null) return null;
        return _snapWorld == null ? ApplyDraftAids(wp.Value.x, wp.Value.y) : wp.Value;
    }

    /// <summary>命令行是否空闲（无进行中的绘制/编辑/测量/交互）——空 Enter 仅在此态重复上次命令。</summary>
    private bool CommandIdle() =>
        _tool == null && _measure == null && _angle == null && _editMode == EditMode.None
        && !_textActive && !_ttrActive && !_serActive && !_offsetActive && !_trimActive
        && !_breakActive && !_slideActive && !_dimActive && !_dimRadActive;

    /// <summary>空命令行 → 上次命令；否则用输入。纯逻辑，可单测。</summary>
    internal static string? RepeatCommand(string typed, string? last)
        => typed.Length > 0 ? typed : (string.IsNullOrEmpty(last) ? null : last);

    // 命令框未识别 → 合成 Tag 转派整条中文命令链(复用 OnRibbonCommand，命令框亦可打中文命令)
    private bool _suppressCmdLog;   // DispatchRibbon 转派时抑制 OnRibbonCommand 重复回显(命令框侧已回显)
    private void DispatchRibbon(string cmd) { _suppressCmdLog = true; OnRibbonCommand(new Button { Tag = cmd }, new RoutedEventArgs()); }

    // ---------- §四/§八 地质/生产数据库（SQLite 数据基座，本机自带真实种子数据）----------
    private Data.GeoDatabase? EnsureGeoDb()
    {
        if (_geoDb != null) return _geoDb;
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pmkylin_geo.db");
            _geoDb = Data.GeoDatabase.OpenSeeded(path);   // 文件库：会话间持久, 已应用迁移跳过
            return _geoDb;
        }
        catch (System.Exception ex) { StatusMsg.Text = $"数据库打开失败：{ex.Message}"; return null; }
    }

    private void EquipmentRosterCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var r = Data.GeoDataQueries.GetEquipmentRoster(db.Connection);
        var parts = new List<string>();
        foreach (var c in r.ByCategory) parts.Add($"{c.Category} {c.Count}");
        StatusMsg.Text = $"设备台账：共 {r.Total} 台（在役 {r.InService}）· 分类: " + string.Join(" / ", parts);
    }

    private void ProductionStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetProductionStats(db.Connection);
        StatusMsg.Text = $"设备生产数据：{s.Records} 条记录 · 总产量 {s.OutputM3:0.#} m³ · 工时 {s.WorkHours:0.#}h · 故障 {s.FaultHours:0.#}h · 作业率 {s.UtilizationPct:0.#}%";
    }

    private void CapacityRankingCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetCapacityRanking(db.Connection, 8);
        if (rows.Count == 0) { StatusMsg.Text = "产能分析：无产能数据"; return; }
        var top = new List<string>();
        foreach (var r in rows) top.Add($"{r.EquipmentId}{(string.IsNullOrEmpty(r.Model) ? "" : "(" + r.Model + ")")} {r.TotalOutputM3:0.#}");
        StatusMsg.Text = $"产能分析（累计产量 Top{rows.Count}）：" + string.Join(" · ", top);
    }

    private void FaultStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var f = Data.GeoDataQueries.GetFaultStats(db.Connection);
        StatusMsg.Text = $"故障分析：{f.Events} 起 · 累计停机 {f.DowntimeHours:0.#}h · 未修复 {f.Unresolved} · 最多「{f.TopType}」×{f.TopTypeCount}";
    }

    private void KpiStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var k = Data.GeoDataQueries.GetKpiStats(db.Connection);
        if (k.Records == 0) { StatusMsg.Text = "KPI 分析：无 KPI 数据"; return; }
        StatusMsg.Text = $"KPI 分析：{k.Records} 条 · 平均可用率 {k.AvgAvailabilityPct:0.#}% · 平均利用率 {k.AvgUtilizationPct:0.#}% · 最新 {k.LatestYear}-{k.LatestMonth:00}";
    }

    private void BoreholeStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var b = Data.GeoDataQueries.GetBoreholeStats(db.Connection);
        var cats = new List<string>();
        foreach (var c in b.ByCategory) cats.Add($"{c.Category} {c.Count}");
        StatusMsg.Text = $"钻孔管理：{b.Holes} 孔 · 总进尺 {b.TotalDepthM:0.#}m（均 {b.AvgDepthM:0.#}m）· 见煤结果 {b.SeamResults} · 类别: " + string.Join(" / ", cats);
    }

    private void CoalQualityStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var q = Data.GeoDataQueries.GetCoalQualityStats(db.Connection);
        if (q.Samples == 0) { StatusMsg.Text = "煤质统计：无煤样数据"; return; }
        StatusMsg.Text = $"煤质统计：{q.Samples} 样 / {q.Seams} 煤层 · 平均 灰分Ad {q.AvgAshPct:0.##}% · 挥发分Vdaf {q.AvgVolatilePct:0.##}% · 发热量Qnet {q.AvgCalorificMJ:0.##}MJ/kg · 全硫St {q.AvgSulfurPct:0.###}%";
    }

    // 商品煤符合性(CoalAnalytics)：逐化验段判 Ad≤/St≤/Q≥ → 达标率 + 按煤层 + 超标数。缺省 Ad≤30/St≤1/Qgr≥21
    private void CoalComplianceCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var samples = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (samples.Count == 0) { StatusMsg.Text = "商品煤符合性：无煤样数据"; return; }
        double adMax = 30, stMax = 1.0, qMin = 21;   // 缺省商品煤限值; 可 "商品煤符合性 <灰max> <硫max> <热min>"
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2) double.TryParse(tk[1], out adMax);
        if (tk.Length >= 3) double.TryParse(tk[2], out stMax);
        if (tk.Length >= 4) double.TryParse(tk[3], out qMin);
        var lim = new Data.ComplianceLimits(UseClean: false, AshOn: true, AshMax: adMax, SulfurOn: true, SulfurMax: stMax,
            CalorificOn: true, CalorificMin: qMin, Calorific: Data.CalorificKind.Qgr, VdafOn: false, VdafMin: 0, VdafMax: 0);
        var r = Data.CoalAnalytics.Evaluate(samples, lim);
        var seamParts = new List<string>();
        foreach (var s in r.BySeam) seamParts.Add($"{s.SeamCode}({s.Pass}/{s.Evaluated}·{s.PassPct:0.#}%)");
        StatusMsg.Text = $"商品煤符合性（原煤 Ad≤{adMax:0.#}%·St≤{stMax:0.##}%·Qgr≥{qMin:0.#}MJ/kg）：达标 {r.Pass}/{r.Evaluated}（{r.PassPct:0.#}%）· 数据不足 {r.Insufficient} · 分煤层 " + string.Join(" ", seamParts);
    }

    private static string CoalIndicator(string[] tk, int idx, string def)
        => tk.Length > idx && (tk[idx] is "ad" or "std" or "vdaf" or "qgr" or "qnet") ? tk[idx] : def;

    // 解析 CSV（委托可测的 GeoDataQueries.ParseCsv）
    private static List<System.Collections.Generic.IReadOnlyDictionary<string, string>> ParseCsvRows(string text)
        => Data.GeoDataQueries.ParseCsv(text);

    // 导出 §四/§八 聚合分析结果 → CSV（反射序列化）。可 "导出分析 <类型>"。
    private async Task ExportAnalysisAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var c = db.Connection;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string key = tk.Length >= 2 ? tk[1] : "";
        (string csv, int n, string name)? R = key switch
        {
            "产能排名" => Wrap(Data.GeoDataQueries.GetCapacityRanking(c, 1000), "capacity_ranking"),
            "故障排名" => Wrap(Data.GeoDataQueries.GetFaultByEquipment(c, 1000), "fault_ranking"),
            "见煤统计" => Wrap(Data.GeoDataQueries.GetSeamIntersections(c), "seam_intersections"),
            "分层煤质" => Wrap(Data.GeoDataQueries.GetCoalQualityBySeam(c), "coal_quality_by_seam"),
            "年度产量" => Wrap(Data.GeoDataQueries.GetAnnualOutput(c), "annual_output"),
            "KPI趋势" => Wrap(Data.GeoDataQueries.GetKpiTrend(c), "kpi_trend"),
            "产能分类" => Wrap(Data.GeoDataQueries.GetCapacityByCategory(c), "capacity_by_category"),
            "故障类型" => Wrap(Data.GeoDataQueries.GetFaultByType(c), "fault_by_type"),
            "班次产量" => Wrap(Data.GeoDataQueries.GetProductionByShift(c), "production_by_shift"),
            "月度计划" => Wrap(Data.GeoDataQueries.GetMonthlyPlans(c), "monthly_plans"),
            "作业面" => Wrap(Data.GeoDataQueries.GetWorkingFaces(c), "working_faces"),
            "分工序验收" => Wrap(Data.GeoDataQueries.GetAcceptanceByPhase(c), "acceptance_by_phase"),
            "边坡设计" => Wrap(Data.GeoDataQueries.GetSlopeDesigns(c), "slope_designs"),
            "路况" => Wrap(Data.GeoDataQueries.GetHaulRoads(c), "haul_roads"),
            "煤种分类" => Wrap(Data.GeoDataQueries.GetCoalClassification(c), "coal_classification"),
            "分级规则" => Wrap(Data.GeoDataQueries.GetCoalGradeRules(c), "coal_grade_rules"),
            "台阶参数" => Wrap(Data.GeoDataQueries.GetSeamBenchParams(c), "seam_bench_params"),
            "煤层" => Wrap(Data.GeoDataQueries.GetCoalSeams(c), "coal_seams"),
            "矿区位置" => Wrap(Data.GeoDataQueries.GetMineLocations(c), "mine_locations"),
            "层位点" => Wrap(Data.GeoDataQueries.GetHorizonPoints(c), "horizon_points"),
            _ => null,
        };
        if (R == null)
        {
            StatusMsg.Text = "导出分析：类型须为 产能排名/故障排名/见煤统计/分层煤质/年度产量/KPI趋势/产能分类/故障类型/班次产量/月度计划/作业面/分工序验收/边坡设计/路况/煤种分类/分级规则/台阶参数/煤层/矿区位置/层位点（如「导出分析 产能排名」）";
            return;
        }
        var fname = await SaveCsvAsync($"导出分析 · {key}", R.Value.name + ".csv", R.Value.csv);
        if (fname != null) StatusMsg.Text = $"导出分析（{key}）：{R.Value.n} 行 → {fname}";
    }

    private static (string csv, int n, string name) Wrap<T>(System.Collections.Generic.IReadOnlyList<T> rows, string name)
        => (Data.GeoDataQueries.RecordsToCsv(rows), rows.Count, name);

    // 导出导入模板：生成带表头+示例行的空 CSV, 供用户按格式填写后导入。可 "导入模板 <类型>"。
    private async Task ExportImportTemplateAsync(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string key = tk.Length >= 2 ? tk[1] : "生产记录";
        string? tpl = Data.GeoDataQueries.ImportTemplate(key);
        if (tpl == null)
        {
            StatusMsg.Text = "导入模板：类型须为 生产记录/月度产能/故障记录/月度KPI/设备台账/煤质化验/观测点/月度计划/见煤成果/运输道路/边坡设计（如「导入模板 煤质化验」）";
            return;
        }
        var name = await SaveCsvAsync($"导入模板 · {key}", $"template_{key}.csv", tpl);
        if (name != null) StatusMsg.Text = $"导入模板（{key}）：表头 + 示例行已导出 → {name}（填入数据后用 导入{key} 入库）";
    }

    // 通用 CSV → 库导入：文件选择 → ParseCsvRows → importFn，报 新增/更新/跳过/错误。
    private async Task ImportCsvToDbAsync(string title, string colsHint, System.Func<List<System.Collections.Generic.IReadOnlyDictionary<string, string>>, Data.GeoDataQueries.ImportOutcome> importFn)
    {
        if (EnsureGeoDb() == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"{title}：选 CSV ({colsHint})", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV/TXT") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        List<System.Collections.Generic.IReadOnlyDictionary<string, string>> rows;
        try { rows = ParseCsvRows(System.IO.File.ReadAllText(files[0].Path.LocalPath)); }
        catch (System.Exception ex) { StatusMsg.Text = $"{title}：读取失败 {ex.Message}"; return; }
        if (rows.Count == 0) { StatusMsg.Text = $"{title}：无数据行(需表头 + 数据)"; return; }
        var o = importFn(rows);
        StatusMsg.Text = $"{title}：新增 {o.Inserted} · 更新 {o.Updated} · 跳过 {o.Skipped} · 错误 {o.Errors}（共 {rows.Count} 行）";
    }

    // 导入生产班次记录(CSV → production_record, 按 设备+日期+班次 upsert)
    private async Task ImportProductionRecordsAsync()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入生产记录：选 CSV (equipment_id,date,shift,output_m3,work_hours,fault_hours)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("生产记录 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        List<System.Collections.Generic.IReadOnlyDictionary<string, string>> rows;
        try { rows = ParseCsvRows(System.IO.File.ReadAllText(files[0].Path.LocalPath)); }
        catch (System.Exception ex) { StatusMsg.Text = $"导入生产记录：读取失败 {ex.Message}"; return; }
        if (rows.Count == 0) { StatusMsg.Text = "导入生产记录：无数据行(需表头 + 数据)"; return; }
        var o = Data.GeoDataQueries.ImportProductionRecords(db.Connection, rows, overwrite: true);
        StatusMsg.Text = $"导入生产记录：新增 {o.Inserted} · 更新 {o.Updated} · 跳过 {o.Skipped} · 错误 {o.Errors}（共 {rows.Count} 行）";
    }

    // 通用 CSV 保存：SaveFilePicker → WriteAllText；成功返回文件名，取消/失败返回 null(状态自报)。
    private async Task<string?> SaveCsvAsync(string title, string suggestedName, string content)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title, DefaultExtension = "csv", SuggestedFileName = suggestedName,
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
        });
        if (file == null) return null;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, content); return System.IO.Path.GetFileName(file.Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"{title}：写出失败 {ex.Message}"; return null; }
    }

    // 导出品位-储量曲线
    private async Task ExportGradeTonnageAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "导出品位储量：无煤样"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = CoalIndicator(tk, 1, "ad");
        var r = Data.CoalAnalytics.GradeTonnage(s, ind, useClean: false);
        if (r.Curve.Count == 0) { StatusMsg.Text = $"导出品位储量({ind})：无有效样(缺厚度)"; return; }
        var name = await SaveCsvAsync("导出品位-储量曲线", $"grade_tonnage_{ind}.csv", Data.CoalAnalytics.GradeTonnageToCsv(r));
        if (name != null) StatusMsg.Text = $"导出品位储量({ind})：{r.Curve.Count} 点曲线 → {name}";
    }

    // 导出分标高煤质
    private async Task ExportElevationAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "导出分标高：无煤样"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = CoalIndicator(tk, 1, "ad");
        double band = 20; if (tk.Length > 2) double.TryParse(tk[2], out band);
        var bands = Data.CoalAnalytics.ByElevation(s, ind, useClean: false, band);
        if (bands.Count == 0) { StatusMsg.Text = $"导出分标高({ind})：无有效样"; return; }
        var name = await SaveCsvAsync("导出分标高煤质", $"coal_by_elevation_{ind}.csv", Data.CoalAnalytics.ElevationToCsv(bands));
        if (name != null) StatusMsg.Text = $"导出分标高({ind})：{bands.Count} 标高带 → {name}";
    }

    // 导出洗选提质
    private async Task ExportWashingAsync()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "导出洗选：无煤样"; return; }
        var rows = Data.CoalAnalytics.WashingBySeam(s);
        var name = await SaveCsvAsync("导出洗选提质", "coal_washing.csv", Data.CoalAnalytics.WashingToCsv(rows));
        if (name != null) StatusMsg.Text = $"导出洗选：{rows.Count} 行 → {name}";
    }

    // 导出用途适宜性
    private async Task ExportUtilizationAsync()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "导出用途：无煤样"; return; }
        var rows = Data.CoalAnalytics.UtilizationBySeam(s);
        var name = await SaveCsvAsync("导出用途适宜性", "coal_utilization.csv", Data.CoalAnalytics.UtilizationToCsv(rows));
        if (name != null) StatusMsg.Text = $"导出用途：{rows.Count} 煤层 → {name}";
    }

    // 导出编组优化方案
    private async Task ExportFleetOptAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rules = Data.GeoDataQueries.GetFleetDispatchRules(db.Connection);
        if (rules.Count == 0) { StatusMsg.Text = "导出编组：无编组规则"; return; }
        double targetM3;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2 && double.TryParse(tk[1], out double wan)) targetM3 = wan * 1e4;
        else { double sw = 0; foreach (var p in Data.GeoDataQueries.GetMonthlyPlans(db.Connection)) sw = System.Math.Max(sw, p.PlanStripWanM3); targetM3 = (sw > 0 ? sw : 1000) * 1e4 / 26.0; }
        var r = Data.FleetOptimizer.Optimize(new Data.FleetOptInput { DailyTargetM3 = targetM3, Rules = rules });
        var name = await SaveCsvAsync("导出编组优化方案", "fleet_optimization.csv", Data.FleetOptimizer.ToCsv(r));
        if (name != null) StatusMsg.Text = $"导出编组：{r.Groups.Count} 编组方案(铲{r.TotalShovels}/车{r.TotalTrucks}) → {name}";
    }

    // 导出产量预测(历史+未来+区间)
    private async Task ExportForecastAsync()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var series = Data.GeoDataQueries.GetMonthlyOutputSeries(db.Connection);
        if (series.Count < 3) { StatusMsg.Text = "导出预测：月度序列样本不足(<3)"; return; }
        var r = Data.ForecastModels.Forecast(series, 12);
        var name = await SaveCsvAsync("导出产量预测", "output_forecast.csv", Data.ForecastModels.PathToCsv(series, r));
        if (name != null) StatusMsg.Text = $"导出预测：{series.Count} 历史 + 12 期预测(趋势{r.TrendLabel}) → {name}";
    }

    // 导出商品煤符合性：逐化验段(含超标段坐标/原因)→ CSV，供定位处置
    private async Task ExportComplianceAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var samples = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (samples.Count == 0) { StatusMsg.Text = "导出符合性：无煤样数据"; return; }
        var lim = new Data.ComplianceLimits(false, true, 30, true, 1.0, true, 21, Data.CalorificKind.Qgr, false, 0, 0);
        var r = Data.CoalAnalytics.Evaluate(samples, lim);
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出商品煤符合性", DefaultExtension = "csv", SuggestedFileName = "coal_compliance.csv",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, Data.CoalAnalytics.ComplianceToCsv(r)); }
        catch (System.Exception ex) { StatusMsg.Text = $"导出符合性：写出失败 {ex.Message}"; return; }
        int fails = r.Samples.Count(e => e.Evaluated && !e.Pass);
        StatusMsg.Text = $"导出符合性：{r.Evaluated} 可判段(超标 {fails}) → {System.IO.Path.GetFileName(file.Path.LocalPath)}";
    }

    // 导出煤质离群 QC：离群段(坐标/方向/严重度)→ CSV，供定位复检
    private async Task ExportOutliersAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var samples = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (samples.Count == 0) { StatusMsg.Text = "导出离群：无煤样数据"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = CoalIndicator(tk, 1, "ad");
        var r = Data.CoalAnalytics.DetectOutliers(samples, ind, useClean: false);
        if (r.N < 5) { StatusMsg.Text = $"导出离群({ind})：样本不足(<5)"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出煤质离群 QC", DefaultExtension = "csv", SuggestedFileName = $"coal_outliers_{ind}.csv",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
        });
        if (file == null) return;
        try { System.IO.File.WriteAllText(file.Path.LocalPath, Data.CoalAnalytics.OutliersToCsv(r)); }
        catch (System.Exception ex) { StatusMsg.Text = $"导出离群：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"导出离群({ind})：{r.Outliers.Count} 离群段 → {System.IO.Path.GetFileName(file.Path.LocalPath)}";
    }

    // 品位-储量曲线：厚度×密度加权, 灰/硫累计≤限值、热量累计≥限值
    private void GradeTonnageCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "品位储量曲线：无煤样"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = CoalIndicator(tk, 1, "ad");
        var r = Data.CoalAnalytics.GradeTonnage(s, ind, useClean: false);
        if (r.Curve.Count == 0) { StatusMsg.Text = $"品位储量曲线({ind})：无有效样(缺厚度)"; return; }
        var mid = r.Curve[r.Curve.Count / 2];
        StatusMsg.Text = $"品位-储量曲线（{ind}·{(r.BelowCutoff ? "累计≤" : "累计≥")}·质量代理{(r.DensityUsed ? "厚×密度" : "厚度")}）：{r.N} 样·总质量 {r.TotalMass:0.#} · 中点限值 {mid.Cutoff:0.##}→累计 {mid.CumMassPct:0.#}%(均值 {mid.CumMeanGrade:0.##})";
    }

    // 分标高煤质：按标高带厚度加权均值
    private void CoalByElevationCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "分标高煤质：无煤样"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = CoalIndicator(tk, 1, "ad");
        double band = 20; if (tk.Length > 2) double.TryParse(tk[2], out band);
        var bands = Data.CoalAnalytics.ByElevation(s, ind, useClean: false, band);
        if (bands.Count == 0) { StatusMsg.Text = $"分标高煤质({ind})：无有效样(缺标高/厚度)"; return; }
        var parts = new List<string>();
        foreach (var b in bands) parts.Add($"[{b.ZLow:0}~{b.ZHigh:0}]{b.WeightedMean:0.##}({b.N})");
        StatusMsg.Text = $"分标高煤质（{ind}·带高{band:0}m·厚度加权均值）：" + string.Join(" ", parts);
    }

    // 煤质离群 QC：Tukey IQR 1.5×IQR 栅栏
    private void CoalOutlierCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "煤质离群：无煤样"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = CoalIndicator(tk, 1, "ad");
        var r = Data.CoalAnalytics.DetectOutliers(s, ind, useClean: false);
        if (r.N < 5) { StatusMsg.Text = $"煤质离群({ind})：样本不足(<5)"; return; }
        var top = new List<string>();
        foreach (var o in r.Outliers) { if (top.Count >= 5) break; top.Add($"{o.HoleId}/{o.SeamCode} {o.Value:0.##}({o.Kind}{o.Severity:0.#}IQR)"); }
        StatusMsg.Text = $"煤质离群 QC（{ind}·Tukey 1.5×IQR）：{r.N}样 中位{r.Median:0.##} Q1{r.Q1:0.##}/Q3{r.Q3:0.##} 栅栏[{r.Lower:0.##},{r.Upper:0.##}] → 离群 {r.Outliers.Count} 段" + (top.Count > 0 ? "：" + string.Join(" · ", top) : "");
    }

    // 洗选提质：成对原煤↔浮煤 → 降灰率/脱硫率/回收率
    private void CoalWashingCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "洗选提质：无煤样"; return; }
        var rows = Data.CoalAnalytics.WashingBySeam(s);
        if (rows.Count == 0) { StatusMsg.Text = "洗选提质：无数据"; return; }
        var parts = new List<string>();
        foreach (var w in rows)
            parts.Add($"{w.SeamCode}(降灰{(w.DeAshPct.HasValue ? w.DeAshPct.Value.ToString("0.#") + "%" : "—")}/脱硫{(w.DeSulfurPct.HasValue ? w.DeSulfurPct.Value.ToString("0.#") + "%" : "—")}/回收{(w.YieldMean.HasValue ? w.YieldMean.Value.ToString("0.#") + "%" : "—")})");
        StatusMsg.Text = $"洗选提质（原煤↔浮煤成对）：" + string.Join(" · ", parts);
    }

    // 用途适宜性：动力煤评价(灰/硫/热) + 炼焦评价(G 粘结)
    private void CoalUtilizationCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "用途适宜性：无煤样"; return; }
        var rows = Data.CoalAnalytics.UtilizationBySeam(s);
        if (rows.Count == 0) { StatusMsg.Text = "用途适宜性：无数据"; return; }
        var parts = new List<string>();
        foreach (var u in rows) parts.Add($"{u.SeamCode}(动力{u.SteamGrade}[{u.SteamNote}]·炼焦{u.CokingType})");
        StatusMsg.Text = $"用途适宜性（动力煤+炼焦）：" + string.Join(" · ", parts);
    }

    private void ShiftOutputCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetProductionByShift(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "班次产量对比：无生产记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.Shift}班({r.Records}条·{r.OutputM3 / 1e4:0.##}万m³·作业率{r.UtilizationPct:0.#}%)");
        StatusMsg.Text = $"班次产量对比：" + string.Join(" · ", parts);
    }

    private void KpiTrendCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetKpiTrend(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "KPI趋势：无 KPI 记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.Year}(可用{r.AvgAvailabilityPct:0.#}%·利用{r.AvgUtilizationPct:0.#}%)");
        StatusMsg.Text = $"设备KPI趋势：" + string.Join(" · ", parts);
    }

    private void CapacityByCategoryCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetCapacityByCategory(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "产能分类对比：无产能数据"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.Category}({r.Units}台·{r.TotalOutputM3 / 1e4:0.#}万m³·{r.SharePct:0.#}%)");
        StatusMsg.Text = $"产能分类对比：" + string.Join(" · ", parts);
    }

    private void FaultByTypeCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetFaultByType(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "故障类型分布：无故障记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.FaultType}({r.Events}次·{r.DowntimeHours:0.#}h·{r.DowntimeSharePct:0.#}%)");
        StatusMsg.Text = $"故障类型分布：" + string.Join(" · ", parts);
    }

    private void AcceptanceByPhaseCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetAcceptanceByPhase(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "分工序验收：无验收记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.Phase}({r.Passed}/{r.Records}·{r.PassPct:0.#}%)");
        StatusMsg.Text = $"分工序验收合格率(薄弱在前)：" + string.Join(" · ", parts);
    }

    private void FaultRankCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetFaultByEquipment(db.Connection, 8);
        if (rows.Count == 0) { StatusMsg.Text = "设备故障排名：无故障记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.EquipmentId}({r.Events}起/停{r.DowntimeHours:0.#}h)");
        StatusMsg.Text = $"设备故障排名（按停机时 Top{rows.Count}）：" + string.Join(" · ", parts);
    }

    private void AnnualOutputCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetAnnualOutput(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "年度产量：无产能数据"; return; }
        var parts = new List<string>();
        double tot = 0;
        foreach (var r in rows) { parts.Add($"{r.Year}: {r.OutputWanM3:0.#}万m³"); tot += r.OutputWanM3; }
        StatusMsg.Text = $"年度产量趋势（{rows.Count} 年 · 累计 {tot:0.#}万m³）：" + string.Join(" · ", parts);
    }

    private void CoalQualityBySeamCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetCoalQualityBySeam(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "分煤层煤质：无煤样"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.SeamCode}({r.Samples}样·灰{r.AvgAshPct:0.#}/挥{r.AvgVolatilePct:0.#}/热{r.AvgCalorificMJ:0.#})");
        StatusMsg.Text = $"分煤层煤质（{rows.Count} 层）：" + string.Join(" · ", parts);
    }

    private void SeamIntersectionsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetSeamIntersections(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "见煤统计：无见煤记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.SeamCode}({r.Holes}孔/均厚{r.AvgThicknessM:0.##}m{(r.PinchCount > 0 ? $"/尖灭{r.PinchCount}" : "")})");
        StatusMsg.Text = $"见煤统计（{rows.Count} 煤层）：" + string.Join(" · ", parts);
    }

    private void CoalSeamsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var seams = Data.GeoDataQueries.GetCoalSeams(db.Connection);
        if (seams.Count == 0) { StatusMsg.Text = "煤层管理：无煤层定义"; return; }
        var parts = new List<string>();
        foreach (var s in seams) parts.Add($"{s.SeamCode}({s.SampleCount}样)");
        StatusMsg.Text = $"煤层管理：{seams.Count} 煤层 · " + string.Join(" / ", parts);
    }

    private void DispatchRulesCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var d = Data.GeoDataQueries.GetDispatchRules(db.Connection, 6);
        if (d.Top.Count == 0) { StatusMsg.Text = "设备编组：无在役调度规则"; return; }
        var parts = new List<string>();
        foreach (var r in d.Top) parts.Add($"{r.Shovel}→{r.Truck}×{r.Trucks}(装{r.Loads:0.#}/循环{r.CycleMin:0.#}min/评{r.Score:0.#})");
        StatusMsg.Text = $"设备智能编组（在役 {d.Active} 规则，按评分）：" + string.Join(" · ", parts);
    }

    // 编组优化(FleetOptimizer)：物理产能子模型+M/M/c排队+DP最小卡车数达标 → 达日产目标的最优铲车编组
    private void FleetOptimizeCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rules = Data.GeoDataQueries.GetFleetDispatchRules(db.Connection);
        if (rules.Count == 0) { StatusMsg.Text = "编组优化：无在役编组规则"; return; }
        // 日产目标：可 "编组优化 <日目标万m³>"；缺省取月计划剥离量/26 工作日(万m³→m³)
        double targetM3;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2 && double.TryParse(tk[1], out double wan)) targetM3 = wan * 1e4;
        else
        {
            double stripWan = 0;
            foreach (var p in Data.GeoDataQueries.GetMonthlyPlans(db.Connection)) stripWan = System.Math.Max(stripWan, p.PlanStripWanM3);
            targetM3 = (stripWan > 0 ? stripWan : 1000) * 1e4 / 26.0;   // 月剥离/26 工作日
        }
        var r = Data.FleetOptimizer.Optimize(new Data.FleetOptInput { DailyTargetM3 = targetM3, Rules = rules });
        if (r.Groups.Count == 0) { StatusMsg.Text = $"编组优化：{(r.Notes.Count > 0 ? r.Notes[0] : "无解")}"; return; }
        var parts = new List<string>();
        foreach (var g in r.Groups)
            parts.Add($"{g.Rule.ShovelModel}×{g.ShovelCount}台(配{g.Rule.TruckModel}×{g.TotalTrucks}车/组日产{g.GroupDailyM3 / 1e4:0.##}万m³/匹配{g.MatchFactor:0.##}/{g.Bottleneck})");
        StatusMsg.Text = $"编组优化（目标 {targetM3 / 1e4:0.##}万m³/天 → {(r.TargetMet ? "达标" : "缺口")} {r.TotalDailyM3 / 1e4:0.##}万m³ · 铲{r.TotalShovels}/车{r.TotalTrucks}）：" + string.Join(" · ", parts);
    }

    private void ProcessArchitectureCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var p = Data.GeoDataQueries.GetProcessArchitecture(db.Connection);
        StatusMsg.Text = $"工艺架构：{p.Systems} 系统 / {p.Phases} 工序 / {p.Templates} 模板 · 系统: " + string.Join(" / ", p.SystemNames);
    }

    private void AcceptanceStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var a = Data.GeoDataQueries.GetAcceptanceStats(db.Connection);
        if (a.Records == 0) { StatusMsg.Text = "现场验收：无验收记录"; return; }
        var st = new List<string>();
        foreach (var c in a.ByStatus) st.Add($"{c.Category} {c.Count}");
        StatusMsg.Text = $"现场验收：{a.Records} 条 · 合格率 {a.PassPct:0.#}% · 平均偏差 {a.AvgAbsDeviationPct:0.#}% · " + string.Join(" / ", st);
    }

    private void WorkingFacesCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var faces = Data.GeoDataQueries.GetWorkingFaces(db.Connection);
        if (faces.Count == 0) { StatusMsg.Text = "作业面台账：无工作面"; return; }
        var parts = new List<string>();
        foreach (var f in faces) parts.Add($"{f.FaceCode}(台阶{f.BenchHeight:0.#}m/坡{f.SlopeAngle:0.#}°/采宽{f.MiningWidth:0.#}m/推进{f.AdvanceRate:0.#}m·月)");
        StatusMsg.Text = $"作业面台账：{faces.Count} 面 · " + string.Join(" · ", parts);
    }

    private void ParamTemplatesCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var p = Data.GeoDataQueries.GetParamTemplates(db.Connection);
        StatusMsg.Text = $"参数模板库：{p.Definitions} 参数定义（{p.Required} 必填 · 涉 {p.Phases} 工序）· {p.TemplateValues} 模板取值";
    }

    private void MonthlyPlansCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var plans = Data.GeoDataQueries.GetMonthlyPlans(db.Connection);
        if (plans.Count == 0) { StatusMsg.Text = "月度计划：无计划数据"; return; }
        var parts = new List<string>();
        foreach (var p in plans) parts.Add($"{p.Year}-{p.Month:00}: 剥离 {p.PlanStripWanM3:0.#}万m³/煤 {p.PlanCoalWanT:0.#}万t/剥采比 {p.StripRatio:0.##}/运距 {p.AvgDistanceKm:0.#}km");
        StatusMsg.Text = $"月度计划（{plans.Count} 期）：" + string.Join(" · ", parts);
    }

    private void HaulRoadsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var roads = Data.GeoDataQueries.GetHaulRoads(db.Connection);
        if (roads.Count == 0) { StatusMsg.Text = "路况显示：无道路数据"; return; }
        var parts = new List<string>();
        foreach (var r in roads) parts.Add($"{(string.IsNullOrEmpty(r.Name) ? r.RoadId : r.Name)}(长{r.LengthM:0.#}m/坡{r.MaxSlopePct:0.#}%/宽{r.WidthM:0.#}m{(string.IsNullOrEmpty(r.Condition) ? "" : "/" + r.Condition)})");
        StatusMsg.Text = $"路况显示（{roads.Count} 路段）：" + string.Join(" · ", parts);
    }

    private void SlopeDesignsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var slopes = Data.GeoDataQueries.GetSlopeDesigns(db.Connection);
        if (slopes.Count == 0) { StatusMsg.Text = "边坡设计：无边坡数据"; return; }
        var parts = new List<string>();
        foreach (var s in slopes) parts.Add($"{s.Side}(工作帮{s.WorkingAngle:0.#}°/最终帮{s.FinalAngle:0.#}°/深{s.MaxDepth:0.#}m/安全系数{s.SafetyFactor:0.##})");
        StatusMsg.Text = $"边坡设计（{slopes.Count} 帮）：" + string.Join(" · ", parts);
    }

    private void FleetOverviewCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var f = Data.GeoDataQueries.GetFleetOverview(db.Connection);
        var st = new List<string>(); foreach (var c in f.ByStatus) st.Add($"{c.Category} {c.Count}");
        var md = new List<string>(); foreach (var c in f.ByModel) md.Add($"{c.Category} {c.Count}");
        StatusMsg.Text = $"机群总览：共 {f.Total} 台 · 状态[{string.Join(" / ", st)}] · 型号[{string.Join(" / ", md)}]";
    }

    private void DataBoardCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var eq = Data.GeoDataQueries.GetEquipmentRoster(db.Connection);
        var pr = Data.GeoDataQueries.GetProductionStats(db.Connection);
        var ft = Data.GeoDataQueries.GetFaultStats(db.Connection);
        var kp = Data.GeoDataQueries.GetKpiStats(db.Connection);
        StatusMsg.Text = $"数据看板（文本汇总）：设备 {eq.Total} 台（在役 {eq.InService}）· 产量 {pr.OutputM3:0.#}m³/{pr.Records}记录 · 作业率 {pr.UtilizationPct:0.#}% · 故障 {ft.Events}起停机{ft.DowntimeHours:0.#}h · KPI 可用率 {kp.AvgAvailabilityPct:0.#}%";
    }

    private void CoalClassificationCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var cls = Data.GeoDataQueries.GetCoalClassification(db.Connection);
        if (cls.Count == 0) { StatusMsg.Text = "煤种分类：无分类数据"; return; }
        var parts = new List<string>();
        foreach (var c in cls) parts.Add($"{c.Code} {c.NameCn}(Vdaf {c.VdafMin:0.#}~{c.VdafMax:0.#}%)");
        StatusMsg.Text = $"煤种分类（{cls.Count} 种）：" + string.Join(" · ", parts);
    }

    private void SeamBenchParamsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetSeamBenchParams(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "煤层台阶参数：无数据"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.SeamCode}(台阶{r.BenchHeight:0.#}m/坡{r.SlopeAngle:0.#}°/平台{r.BermWidth:0.#}m/最小采厚{r.MinThick:0.##}m)");
        StatusMsg.Text = $"煤层台阶参数（{rows.Count} 煤层）：" + string.Join(" · ", parts);
    }

    private void EquipmentConstraintsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var c = Data.GeoDataQueries.GetEquipmentConstraints(db.Connection);
        var ty = new List<string>(); foreach (var t in c.ByType) ty.Add($"{t.Category} {t.Count}");
        StatusMsg.Text = $"设备约束条件：{c.Total} 条（在役 {c.Active}）· 类型: " + string.Join(" / ", ty);
    }

    private void CoalGradeRulesCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetCoalGradeRules(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "煤质分级：无规则"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.Type}:{r.LevelName}({r.Min:0.#}~{r.Max:0.#})");
        StatusMsg.Text = $"煤质分级规则（{rows.Count} 级）：" + string.Join(" · ", parts);
    }

    private void DrawObservationPointsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var pts = Data.GeoDataQueries.GetObservationPoints(db.Connection);
        if (pts.Count == 0) { StatusMsg.Text = "展绘观测点：库中无带坐标观测点"; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, sumT = 0; int nT = 0;
        BeginChange();
        foreach (var (pid, x, y, thick, _) in pts)
        {
            _scene.Add(new PointEntity { X = x, Y = y, Size = 2.0, Cr = 0.95f, Cg = 0.55f, Cb = 0.25f, LayerName = "煤层观测点" });
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
            if (thick > 0) { sumT += thick; nT++; }
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new double[] { minX, minY, maxX, maxY });
        StatusMsg.Text = $"展绘观测点：{pts.Count} 点入场景（图层「煤层观测点」）· 平均煤厚 {(nT > 0 ? sumT / nT : 0):0.##}m（{nT} 有效）";
    }

    // 数据导出：§四 关键表整表导出为 CSV(选目标文件夹)。导入半需模板对话框(记录)。
    private async System.Threading.Tasks.Task ExportGeoDataAsync()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        { Title = "数据导出：选导出目标文件夹", AllowMultiple = false });
        if (folders.Count == 0) return;
        string dir = folders[0].Path.LocalPath;
        string[] tables = { "equipment", "equipment_model", "production_record", "capacity_monthly",
                            "equipment_kpi_monthly", "fault_event", "borehole", "coal_sample",
                            "coal_seam_def", "dispatch_rule", "parameter_acceptance", "working_face",
                            "monthly_plan", "haul_road", "slope_design", "mine_location" };
        int ok = 0;
        foreach (var t in tables)
        {
            try
            {
                string csv = Data.GeoDataQueries.ExportTableToCsv(db.Connection, t);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, t + ".csv"), csv, new System.Text.UTF8Encoding(true));
                ok++;
            }
            catch { /* 单表失败不阻整体 */ }
        }
        StatusMsg.Text = $"数据导出：{ok}/{tables.Length} 张 §四 表 → {dir}（导入需模板对话框，受阻记录）";
    }

    private void EfficiencyForecastCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var f = Data.GeoDataQueries.GetEfficiencyForecast(db.Connection);
        StatusMsg.Text = $"设备效能预测（基线+投影）：基线月产 {f.BaselineMonthlyWanM3:0.##}万m³/产出设备 · 可用率 {f.AvgAvailabilityPct:0.#}% · 作业率 {f.AvgRunRatePct:0.#}% · 投影年产 {f.ProjectedAnnualWanM3:0.#}万m³（产出设备 {f.ProducingUnits}台 · 在役 {f.ActiveEquipment}台）";
    }

    // 产量时序预测：ForecastModels(LSQ趋势+EWMA融合/Holt) 对月度产量序列做点预测 + 趋势 + 异常
    private void OutputForecastCmd(bool holt)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var series = Data.GeoDataQueries.GetMonthlyOutputSeries(db.Connection);
        if (series.Count < 3) { StatusMsg.Text = "产量预测：月度序列样本不足(<3)"; return; }
        var r = Data.ForecastModels.Forecast(series, 6, method: holt ? Data.ForecastMethod.Holt : Data.ForecastMethod.Fusion);
        double lo = r.Next - r.HalfWidthAt(0), hi = r.Next + r.HalfWidthAt(0);
        StatusMsg.Text = $"产量时序预测（{r.Method}）：下期 {r.Next:0.#}万m³ [95%区间 {System.Math.Max(0, lo):0.#}~{hi:0.#}] · 趋势{r.TrendLabel}(斜率{r.Slope:+0.0;-0.0}/月, R²{r.R2:0.00}) · 历史异常 {r.AnomalyCount} 期 · 6期路径 {string.Join("/", System.Array.ConvertAll(r.Path, v => v.ToString("0")))}";
    }

    private void MineLocationsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var locs = Data.GeoDataQueries.GetMineLocations(db.Connection);
        if (locs.Count == 0) { StatusMsg.Text = "采区列表：无位置数据"; return; }
        var parts = new List<string>();
        foreach (var l in locs) parts.Add($"{l.Code}{(string.IsNullOrEmpty(l.Name) ? "" : " " + l.Name)}(标高{l.Elevation:0.#}m{(string.IsNullOrEmpty(l.Team) ? "" : "/" + l.Team)}{(l.Active ? "" : "/停用")})");
        StatusMsg.Text = $"采区列表（{locs.Count} 处）：" + string.Join(" · ", parts);
    }

    // 展绘钻孔 / 开孔坐标管理：读库钻孔平面坐标 → 点位入场景(可见几何) + 缩放到范围。
    private void DrawBoreholesCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var pts = Data.GeoDataQueries.GetBoreholeCoords(db.Connection);
        if (pts.Count == 0) { StatusMsg.Text = "展绘钻孔：库中无带坐标的钻孔"; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        BeginChange();
        foreach (var (holeId, x, y, _) in pts)
        {
            var pe = new PointEntity { X = x, Y = y, Size = 2.0, Cr = 0.30f, Cg = 0.75f, Cb = 0.95f, LayerName = "钻孔" };
            _scene.Add(pe);
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        RefreshScene();
        if (maxX > minX && maxY > minY)
            Viewport.FitBounds(new double[] { minX, minY, maxX, maxY });
        StatusMsg.Text = $"展绘钻孔：{pts.Count} 孔位入场景（图层「钻孔」）· 范围 X[{minX:0}~{maxX:0}] Y[{minY:0}~{maxY:0}]";
    }

    // 展绘层位数据(HorizonPointBuilder)：分煤层 底板(floor_elevation)/顶板(底+采用厚度) 高程点入场景, 按 煤层×顶/底 分层
    private void HorizonPointsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var pts = Data.GeoDataQueries.GetHorizonPoints(db.Connection);
        if (pts.Count == 0) { StatusMsg.Text = "展绘层位数据：库中无见煤成果(缺底板高程)"; return; }
        BeginChange();
        int roof = 0, floor = 0;
        foreach (var p in pts)
        {
            // 按煤层 hash 稳定配色, 顶板偏暖/底板偏冷
            int h = System.Math.Abs(p.SeamCode.GetHashCode());
            float baseHue = (h % 7) / 7.0f;
            var pe = new PointEntity
            {
                X = p.X, Y = p.Y, Size = 1.6,
                Cr = p.IsRoof ? 0.5f + 0.5f * baseHue : 0.2f * baseHue,
                Cg = 0.4f + 0.4f * baseHue, Cb = p.IsRoof ? 0.3f : 0.7f,
                LayerName = $"层位_{p.SeamCode}_{(p.IsRoof ? "顶板" : "底板")}",
            };
            _scene.Add(pe);
            if (p.IsRoof) roof++; else floor++;
        }
        RefreshScene();
        int seams = pts.Select(p => p.SeamCode).Distinct().Count();
        StatusMsg.Text = $"展绘层位数据：{seams} 煤层 · 顶板 {roof} + 底板 {floor} = {pts.Count} 点入场景（图层 层位_煤层_顶/底板）";
    }

    // 层位求交(顶底板竖直求交算高程)：对各煤层顶/底板层位点建 TIN，在 (x,y) 竖直采高 → 报各煤层顶/底板高程 + 厚度。
    // 忠实原 GeoDataBase「煤层顶底板三角网竖直求交」核(TinSampler)，层位点替内核存库 TIN。
    private void SeamIntersectCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        var inv = System.Globalization.CultureInfo.InvariantCulture; var fl = System.Globalization.NumberStyles.Float;
        if (tk.Length < 3 || !double.TryParse(tk[1], fl, inv, out double qx) || !double.TryParse(tk[2], fl, inv, out double qy))
        { StatusMsg.Text = "层位求交：用法「层位求交 <x> <y>」——在该点竖直求交各煤层顶/底板 TIN 算高程"; return; }
        var db = EnsureGeoDb(); if (db == null) return;
        var pts = Data.GeoDataQueries.GetHorizonPoints(db.Connection);
        if (pts.Count == 0) { StatusMsg.Text = "层位求交：库中无见煤成果(缺底板高程)"; return; }

        var report = new List<string>();
        foreach (var g in pts.GroupBy(p => p.SeamCode).OrderBy(g => g.Key))
        {
            var roofP = g.Where(p => p.IsRoof).Select(p => (p.X, p.Y, p.Z)).ToList();
            var floorP = g.Where(p => !p.IsRoof).Select(p => (p.X, p.Y, p.Z)).ToList();
            double? rz = TinSampler.SampleZ(roofP, qx, qy);
            double? fz = TinSampler.SampleZ(floorP, qx, qy);
            if (rz == null && fz == null) continue;   // 该点在此煤层层位范围外
            string seg = $"{g.Key}: 顶{(rz.HasValue ? rz.Value.ToString("0.#", inv) : "—")} 底{(fz.HasValue ? fz.Value.ToString("0.#", inv) : "—")}";
            if (rz.HasValue && fz.HasValue) seg += $" 厚{(rz.Value - fz.Value).ToString("0.##", inv)}";
            report.Add(seg);
        }
        if (report.Count == 0) { StatusMsg.Text = $"层位求交 ({qx:0.#},{qy:0.#})：该点落在所有煤层层位 TIN 范围外（无覆盖）"; return; }
        // 在查询点插一个标记点，便于定位
        BeginChange();
        _scene.Add(new PointEntity { X = qx, Y = qy, Size = 2.2, Cr = 0.95f, Cg = 0.3f, Cb = 0.2f, LayerName = "层位求交" });
        RefreshScene();
        StatusMsg.Text = $"层位求交 ({qx:0.#},{qy:0.#})：" + string.Join(" | ", report);
    }

    // ---------- 智能助手面板（菜单引导，点选即执行命令）----------
    private void RenderAssistant(AssistantEngine.Reply r)
    {
        if (AssistantPanel == null) return;
        AssistantPanel.Children.Clear();
        AssistantPanel.Children.Add(new TextBlock
        {
            Text = r.Content, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#3A3F46"), Margin = new Thickness(4, 2, 4, 8)
        });
        foreach (var opt in r.Options)
        {
            var btn = new Button
            {
                Content = opt.Command == null ? opt.Label : $"{opt.Label}  ›",
                FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 3), Padding = new Thickness(8, 4, 8, 4),
                Background = Brush.Parse(opt.Command == null ? "#E8EDF3" : "#DDEBFB"),
                BorderBrush = Brush.Parse("#C7D2DE")
            };
            var captured = opt;
            btn.Click += (_, _) => OnAssistantOption(captured);
            AssistantPanel.Children.Add(btn);
        }
        AssistantScrollToEnd();
    }

    private void AssistantScrollToEnd()
    {
        if (AssistantPanel?.Parent is ScrollViewer sv) sv.ScrollToEnd();
    }

    private void OnAssistantOption(AssistantEngine.Option opt)
    {
        if (!string.IsNullOrEmpty(opt.Command))
        {
            LogCommand(opt.Command!);
            ExecuteCommandToken(opt.Command!);
        }
        RenderAssistant(_assistant.Select(opt));
    }

    private void OnAssistantSend(object? sender, RoutedEventArgs e) => AssistantSubmit();
    private void OnAssistantInputKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { AssistantSubmit(); e.Handled = true; } }

    private void AssistantSubmit()
    {
        if (AssistantInput == null) return;
        string text = (AssistantInput.Text ?? "").Trim();
        if (text.Length == 0) return;
        AssistantInput.Text = string.Empty;
        var reply = _assistant.HandleText(text, IsKnownCommand);
        if (IsKnownCommand(text)) { LogCommand(text); ExecuteCommandToken(text); }
        RenderAssistant(reply);
    }

    // 判定一个 token 是否为可执行命令（命令目录 或 绘图工具 或 中文命令链已知）。
    private bool IsKnownCommand(string token)
    {
        string t = token.Trim();
        if (t.Length == 0) return false;
        string u = t.ToUpperInvariant();
        foreach (var c in CommandCatalog) if (string.Equals(c, t, System.StringComparison.OrdinalIgnoreCase)) return true;
        // 助手菜单用到的英文命令 + 常见别名
        switch (u)
        {
            case "CIRCLE": case "RECTANG": case "LINE": case "PLINE": case "POLYGON": case "POINT":
            case "MOVE": case "COPY": case "ROTATE": case "SCALE": case "MIRROR": case "OFFSET": case "TRIM": case "ERASE":
            case "DIMALIGNED": case "DIMRADIAL": case "DIST": case "MANG":
            case "ZOOMEXTENTS": case "PAN": case "3DORBIT": case "3DVIEW": case "GIZMO":
                return true;
        }
        return false;
    }

    // 命令历史/输出面板：回显执行的命令（▸ cmd），滚动到底，上限 100 行
    private void LogCommand(string cmd)
    {
        if (CmdLog == null || string.IsNullOrWhiteSpace(cmd)) return;
        CmdLog.Children.Add(new TextBlock
        {
            Text = "▸ " + cmd, FontSize = 11, FontFamily = new FontFamily("Consolas,monospace"),
            Foreground = Brush.Parse("#8FB6E8")
        });
        while (CmdLog.Children.Count > 100) CmdLog.Children.RemoveAt(0);
        CmdLogScroll?.ScrollToEnd();
    }

    // 命令目录（供命令行自动补全候选；主要功能命令，覆盖 Home + 各模块）
    private static readonly string[] CommandCatalog =
    {
        // 文件/绘制/修改
        "新建","打开","保存","另存为","导入","选项",
        "点","直线","多段线","滑动多段线","圆","矩形","正多边形","文字","圆弧","图案填充","填充十字",
        "复制","移动","旋转","偏移","修剪","延伸","打断","分解","删除","撤销","重做",
        // 对象捕捉
        "对象捕捉","交点捕捉","最近捕捉","垂足捕捉","捕捉全模式",
        // 草图辅助
        "正交","栅格","栅格捕捉",
        // 图层/视图
        "新建图层","删除图层","图层特性管理器","冻结","锁定","全开",
        // 隐藏/隔离
        "隐藏对象","隐藏同一图层对象","结束隐藏",
        "2D","3D","俯视","仰视","主视","后视","左视","右视","西南等轴测","东南等轴测","东北等轴测","西北等轴测","缩放","清空视图","清理标记",
        // 注释/测量/剪贴板/选择
        "线性标注","对齐标注","半径标注","连续标注","标注样式",
        "距离","面积","角度",
        "剪切","复制到剪贴板","粘贴","基点粘贴","原坐标粘贴",
        "快速选择","全部选择","取消选择","创建选择集","特性",
        // 线编辑
        "加密多段线","简化","抽稀等值线","两线交点","闭合多段线","删除重复点","删除重复线","连接多段线","组合工作线",
        // 网格/建模
        "网格度量","网格诊断","创建三角网","约束三角网","网格焊接","网格边界","合并三角网","固化成体","侧面三角网","立方体","球体","圆柱","体素格网体积","实体转块体",
        // 区域/地形/点云
        "区域求差","区域重叠检测","克里金估值","快速估值",
        "坡度","坡向","粗糙度","曲率","加载点云","点云着色","SOR去噪","点云抽稀","地面点滤波","高程着色","点云质量统计","点云裁剪",
        // 块体/运输/路网
        "块体模型","资源量","道路横断面","运距指标","OD运距矩阵","点对点寻径","备选路径","路网校验","演化对比","螺旋斜坡道","折返斜坡道",
        // 生产计划/投影
        "境界圈定","剥采比均衡","方案综合对比","开采程序确定","平盘宽度识别","确定可采区域","点落到面上","线落到面上",
        // §四/§八 数据分析(SQLite 种子库)
        "设备台账","生产数据","产能分析","故障分析","KPI分析","设备智能编组","钻孔管理","煤质统计","煤层管理","工艺架构","展绘层位数据","层位求交","导入生产记录","导入月度产能","导入故障记录","导入月度KPI","导入设备台账","导入煤质","导入观测点","导入月度计划","导入见煤成果","导入路况","导入边坡","导入模板","导出分析",
        "现场验收","作业面台账","参数模板库","月度计划","路况显示","边坡设计","钻孔展绘","机群总览","数据看板","煤种分类",
        "煤层台阶参数","设备约束","煤质分级","观测点","矿区位置","设备效能预测","年度产量","设备故障排名","班次产量对比","KPI趋势",
        "产能分类对比","故障类型分布","分工序验收合格率","数据导出","达成度评价","产量预测","时序预测","编组优化","智能编组优化","导出编组","导出预测",
        "商品煤符合性","煤质达标","导出符合性","品位储量曲线","导出品位储量","分标高煤质","导出分标高","煤质离群","导出离群","洗选提质","导出洗选","用途适宜性","导出用途",
        // TaskLib 自足计算
        "生产量核算","物料换算","采剥平衡","排土场按量推进","配煤核算","工序进度跟踪","编组产能","环节降效",
    };

    // 命令框输入变化 → 候选补全提示（子串匹配, 取前 8）
    private void OnCommandInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (CmdSuggest == null || CommandInput == null) return;
        string t = CommandInput.Text?.Trim() ?? "";
        if (t.Length == 0) { CmdSuggest.IsVisible = false; return; }
        var hits = new List<string>();
        foreach (var c in CommandCatalog)
        {
            if (c.Contains(t, System.StringComparison.OrdinalIgnoreCase)) hits.Add(c);
            if (hits.Count >= 8) break;
        }
        if (hits.Count == 0) { CmdSuggest.IsVisible = false; return; }
        CmdSuggest.Text = "候选(Tab 补全): " + string.Join("  ·  ", hits);
        CmdSuggest.IsVisible = true;
    }

    // Tab 补全：取第一个候选填入命令框
    private void CompleteCommand(TextBox tb)
    {
        string t = tb.Text?.Trim() ?? "";
        if (t.Length == 0) return;
        foreach (var c in CommandCatalog)
            if (c.Contains(t, System.StringComparison.OrdinalIgnoreCase)) { tb.Text = c; tb.CaretIndex = c.Length; return; }
    }

    // 命令历史（供命令行 ↑/↓ 回溯）
    private readonly List<string> _cmdHistory = new();
    private int _cmdHistoryIdx = -1;   // -1/末尾 = 停在当前输入(空)

    private void PushHistory(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return;
        if (_cmdHistory.Count == 0 || _cmdHistory[^1] != cmd) _cmdHistory.Add(cmd);   // 去连续重复
        if (_cmdHistory.Count > 200) _cmdHistory.RemoveAt(0);
        _cmdHistoryIdx = -1;
    }

    private void RecallHistory(TextBox tb, int dir)   // dir=-1 较早, +1 较新
    {
        if (_cmdHistory.Count == 0) return;
        if (_cmdHistoryIdx < 0) _cmdHistoryIdx = _cmdHistory.Count;   // 从"末尾之后"(当前输入)起
        _cmdHistoryIdx = System.Math.Clamp(_cmdHistoryIdx + dir, 0, _cmdHistory.Count);
        if (_cmdHistoryIdx >= _cmdHistory.Count) { tb.Text = string.Empty; }
        else { tb.Text = _cmdHistory[_cmdHistoryIdx]; tb.CaretIndex = tb.Text.Length; }
    }

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Up) { RecallHistory(tb, -1); e.Handled = true; return; }     // ↑ 回溯较早命令
        if (e.Key == Key.Down) { RecallHistory(tb, +1); e.Handled = true; return; }   // ↓ 回溯较新命令
        if (e.Key == Key.Tab) { CompleteCommand(tb); e.Handled = true; return; }      // Tab 补全首个候选
        if (e.Key != Key.Enter) return;
        if (CmdSuggest != null) CmdSuggest.IsVisible = false;                          // 执行时收起候选

        string cmd = tb.Text.Trim();
        if (cmd.Length == 0)   // 空命令行 + Enter = 重复上次命令（AutoCAD 行为；仅空闲态，不干预进行中的交互）
        {
            if (!CommandIdle() || string.IsNullOrEmpty(_lastCommand)) return;
            cmd = _lastCommand!;
        }
        tb.Text = string.Empty;

        // 文字：下一条命令行输入即内容
        if (_textActive) { _textActive = false; if (cmd.Length > 0) PlaceText(cmd); return; }

        // 圆 TTR：等待半径
        if (_ttrActive && _ttrAwaitRadius && _ttrRef1 != null && _ttrRef2 != null && double.TryParse(cmd, out double ttrR) && ttrR > 0)
        {
            var c = TtrSolveCenter(_ttrRef1, _ttrPick1, _ttrRef2, _ttrPick2, ttrR);
            if (c != null)
            {
                var circ = new CircleEntity { Cx = c.Value.x, Cy = c.Value.y, Radius = ttrR };
                BeginChange(); AssignLayer(circ); _scene.Add(circ); RefreshScene();
                StatusMsg.Text = $"圆TTR完成 r={ttrR}";
            }
            else StatusMsg.Text = "圆TTR：该半径下两参照无相切解";
            _ttrActive = false; _ttrRef1 = null; _ttrRef2 = null; _ttrAwaitRadius = false;
            return;
        }

        // 圆弧 SER：等待半径
        if (_serActive && _serAwaitRadius && _serStart != null && _serEnd != null && double.TryParse(cmd, out double serR) && System.Math.Abs(serR) > 1e-9)
        {
            var t = ArcMath.FromStartEndRadius(_serStart.Value.x, _serStart.Value.y, _serEnd.Value.x, _serEnd.Value.y, serR);
            if (t != null)
            {
                var arc = new ArcEntity { X1 = t.Value.x1, Y1 = t.Value.y1, X2 = t.Value.x2, Y2 = t.Value.y2, X3 = t.Value.x3, Y3 = t.Value.y3 };
                BeginChange(); AssignLayer(arc); _scene.Add(arc); RefreshScene();
                StatusMsg.Text = $"圆弧SER完成 r={serR}";
            }
            else StatusMsg.Text = "圆弧SER：半径太小(＜半弦)，无解";
            _serActive = false; _serAwaitRadius = false; _serStart = null; _serEnd = null;
            return;
        }

        if (TryCoordinateInput(cmd)) return;   // 绘制/编辑取点时优先当坐标

        _lastCommand = cmd;                    // 记录供"空命令行 + Enter 重复"（坐标已在上一步返回，不会记为命令）
        PushHistory(cmd);                      // 入命令历史（供 ↑/↓ 回溯；坐标/交互输入不入）
        LogCommand(cmd);                       // 命令输出面板回显(typed; 转派中文由 _suppressCmdLog 防重复)
        ExecuteCommandToken(cmd);
    }

    // 执行一个命令 token（英文命令 switch；未识别 → 中文命令链/绘图工具）。供命令框与智能助手复用。
    private void ExecuteCommandToken(string cmd)
    {
        switch (cmd.ToUpperInvariant())
        {
            case "2D":
                Viewport.SetViewMode(true);
                StatusMsg.Text = "视图: 2D 平面（正交俯视，拖拽平移）";
                break;
            case "3D":
            case "3DVIEW":
            case "3DORBIT":
                Viewport.SetViewMode(false);
                StatusMsg.Text = "视图: 3D 轨道";
                break;
            case "NODEEDITOR":
            case "节点编辑器":
                OpenNodeEditor();
                break;
            case "ZE":
            case "ZOOM":
            case "ZOOMEXTENTS":
                Viewport.ZoomExtents();
                StatusMsg.Text = "范围缩放";
                break;
            case "GRID":
                SetGrid(!_gridOn);
                StatusMsg.Text = _gridOn ? "网格: 开" : "网格: 关";
                break;
            case "OPTIONS":
            case "OP":
                ShowOptions();
                break;
            case "EXPORTDXF":
            case "导出":
                _ = ExportDxfAsync();
                break;
            case "IMPORTPT":
            case "PTIMPORT":
                _ = ImportPointsAsync();
                break;
            case "BOREHOLE":
            case "ZK":
                _ = ImportBoreholesAsync();
                break;
            case "CONTOUR":
                _ = ContourFromCsvAsync();
                break;
            case "TIN":
                _ = CreateTinAsync();
                break;
            case "TRIMESH":
                GenerateSampleTrimesh();
                break;
            case "SLOPE":
                _ = ShadeTinAsync("坡度着色", "绿=平 → 红=陡", TerrainAnalysis.BuildSlopeMap);
                break;
            case "ASPECT":
                _ = ShadeTinAsync("坡向着色", "按朝向 HSV 配色", TerrainAnalysis.BuildAspectMap);
                break;
            case "ELEV":
                _ = ShadeTinAsync("高程着色", "低绿→中黄→高棕", TerrainAnalysis.BuildElevationMap);
                break;
            case "VOLUME":
                _ = VolumeAsync();
                break;
            case "DIFFVOL":
                _ = TwoEpochVolumeAsync();
                break;
            case "BNDVOL":
                _ = BoundaryVolumeAsync();
                break;
            case "CENTERLINE":
                ExtractCenterline();
                break;
            case "PATH":
                StartPathfind();
                break;
            case "KPATH":
            case "ALTPATH":
                StartKPathfind();
                break;
            case "ROADVALIDATE":
            case "NETCHECK":
                ValidateRoadNetwork();
                break;
            case "ROADEVOLUTION":
            case "EVOLUTION":
                _ = EvolutionCompareAsync();
                break;
            case "STRIPS":
                DumpStrips();
                break;
            case "BENCH":
                StartBench();
                break;
            case "JOINPOLY":
                JoinPolylines();
                break;
            case "BLOCKMODEL":
                _ = ImportBlockModelAsync();
                break;
            case "RESOURCE":
                ResourceReport(null);
                break;
            case "HULL":
                _ = BoundaryHullAsync();
                break;
            case "PROFILE":
                _ = SectionProfileAsync();
                break;
            case "ROUGHNESS":
                _ = RoughnessAsync();
                break;
            case "CURVATURE":
                _ = CurvatureAsync();
                break;
            case "COORDTRANS":
                _ = CoordTransformAsync();
                break;
            case "HAUL":
                _ = HaulMetricsAsync();
                break;
            case "SPOT":
                _ = StartSpotQueryAsync();
                break;
            case "ESTIMATE":
                _ = EstimateGradeAsync();
                break;
            case "THIN":
                _ = ThinPointsAsync();
                break;
            case "GROUND":
                _ = GroundFilterAsync();
                break;
            case "C2C":
                _ = CloudCompareAsync();
                break;
            case "AREA":
            case "AA":
                MeasureArea();
                break;
            case "TEXT":
            case "DT":
                ArmText();
                break;
            case "DIM":
            case "DIMLINEAR":
            case "DIMALIGNED":
                StartDim();
                break;
            case "DIMRADIAL":
            case "DIMRAD":
                StartDimRadial();
                break;
            case "DIMCONTINUE":
            case "DIMCONT":
                StartDimContinue();
                break;
            case "CLIP":
                ClipPolygon();
                break;
            case "SMOOTH":
                SmoothPolyline();
                break;
            case "SIMPLIFY":
            case "DP":
                SimplifyPolyline();
                break;
            case "WP":
                PolygonSelect(false);
                break;
            case "CP":
                PolygonSelect(true);
                break;
            case "DIST":
            case "DI":
                _measure = new MeasureState();
                _tool = null;
                StatusMsg.Text = "测距：点第一点";
                break;
            case "MANG":
            case "ANG":
                _angle = new AngleState();
                _tool = null; _measure = null;
                StatusMsg.Text = "测角：点顶点";
                break;
            case "ERASE":
            case "E":
                DeleteSelected();
                break;
            case "COPYCLIP":
                CopyClip();
                break;
            case "CUTCLIP":
                CutClip();
                break;
            case "PASTECLIP":
            case "PASTEORIG":
                PasteClip();
                break;
            case "PASTEBASE":
                StartPasteBase();
                break;
            case "ERASEALL":
                EraseAll();
                break;
            case "GROUP":
            case "SELSET":
                CreateSelSet();
                break;
            case "SELSETCALL":
            case "GROUPCALL":
                RecallSelSet();
                break;
            case "REGEN":
            case "RE":
                Regen();
                break;
            case "PROPERTIES":
            case "PROPS":
            case "PR":
                ShowProperties();
                break;
            case "CLRMARK":
                ClrMark();
                break;
            case "ALL":
                SelectAll();
                break;
            case "QSELECT":
            case "QSEL":
            case "SELECTSIMILAR":
            case "SI":
                SelectSimilar();
                break;
            case "LAST":
                SelectLast();
                break;
            case "PREVIOUS":
            case "P":
                SelectPrevious();
                break;
            case "EXPLODE":
            case "X":
                ExplodeSelected();
                break;
            case "DENSIFY":
            case "POLYDENSIFY":
                DensifySelectedPolylines();
                break;
            case "POLYINTERSECT":
            case "INTERSECTPOLY":
                IntersectSelectedPolylines();
                break;
            case "POLYCLOSE":
            case "CLOSEPOLY":
                CloseSelectedPolylines();
                break;
            case "POINTDEDUPE":
            case "DEDUPEPOINTS":
                DedupeSelectedPoints();
                break;
            case "POLYDEDUPE":
            case "DEDUPEPOLY":
                DedupeSelectedPolylines();
                break;
            case "REGIONSUBTRACT":
            case "REGIONDIFF":
                SubtractRegions();
                break;
            case "REGIONOVERLAP":
                CheckRegionOverlap();
                break;
            case "BENCHWIDTH":
            case "WIDEBENCH":
                _ = BenchWidthAsync();
                break;
            case "MINEABLEAREA":
                _ = MineableAreaAsync();
                break;
            case "POINTPROJECT":
                _ = ProjectPointsToMeshAsync();
                break;
            case "POLYPROJECT":
                _ = ProjectPolylinesToMeshAsync();
                break;
            case "SIDESURFACE":
            case "LOFT":
                _ = SideSurfaceAsync();
                break;
            case "ROADSECTION":
            case "ROADWIDEN":
                RoadCrossSectionCmd();
                break;
            case "ADVANCE":
            case "PARALLELADVANCE":
                AdvanceCmd(AdvanceMode.Parallel, "平行推进");
                break;
            case "FIXEDPIVOT":
                AdvanceCmd(AdvanceMode.FixedPivot, "定点回转");
                break;
            case "MOVINGPIVOT":
                AdvanceCmd(AdvanceMode.MovingPivot, "动点回转");
                break;
            case "SPIRALRAMP":
                SpiralRampCmd();
                break;
            case "SWITCHBACK":
                SwitchbackRampCmd();
                break;
            case "HAULMETRICS":
            case "CYCLETIME":
                _ = HaulRecordMetricsAsync();
                break;
            case "ODMATRIX":
                _ = OdMatrixAsync();
                break;
            case "MOVE":
            case "M":
                StartEdit(EditMode.Move, "移动");
                break;
            case "COPY":
            case "CO":
                StartEdit(EditMode.Copy, "复制");
                break;
            case "MIRROR":
            case "MI":
                StartEdit(EditMode.Mirror, "镜像");
                break;
            case "ROTATE":
            case "RO":
                StartEdit(EditMode.Rotate, "旋转");
                break;
            case "SCALE":
            case "SC":
                StartEdit(EditMode.Scale, "缩放");
                break;
            case "OFFSET":
            case "O":
                StartOffset();
                break;
            case "TRIM":
            case "TR":
            case "EXTEND":
            case "EX":
                StartTrim();
                break;
            case "PLDRAG":
            case "SPL":
                StartSlide();
                break;
            case "BREAK":
            case "BR":
                StartBreak();
                break;
            case "GIZMO":
                ToggleGizmo();
                break;
            case "ORTHO":
                _orthoOn = !_orthoOn;
                SyncDraftToggles();
                StatusMsg.Text = _orthoOn ? "正交: 开（取点锁定水平/垂直）" : "正交: 关";
                break;
            case "SNAP":
                _snapOn = !_snapOn;
                SyncDraftToggles();
                StatusMsg.Text = _snapOn ? $"栅格捕捉: 开（步长 {_snapStep:0.##}）" : "栅格捕捉: 关";
                break;
            case "BENCHLINES":
            case "BENCHEXPAND":
                GenerateBenchLines();
                break;
            case "VPBALANCE":
            case "STRIPBALANCE":
                _ = StrippingBalanceAsync();
                break;
            case "WORKFACELINE":
            case "WFLINE":
                _ = WorkingFaceLineAsync();
                break;
            case "QUALITYSTATS":
            case "COALSTATS":
                _ = QualityStatsAsync();
                break;
            case "SLOPEEST":
            case "WORKSLOPE":
                _ = SlopeEstimateAsync();
                break;
            case "BENCHANALYZE":
            case "PROCESSPARAM":
                _ = BenchAnalyzeAsync();
                break;
            case "ATTAINMENT":
            case "ATTAIN":
                _ = AttainmentAsync();
                break;
            case "FLEETMATCH":
            case "TRUCKMATCH":
                _ = FleetMatchAsync();
                break;
            case "PCSTATS":
            case "CLOUDSTATS":
                _ = PointCloudStatsAsync();
                break;
            case "ELEVCOLOR":
            case "PCCOLOR":
                _ = ElevationColorAsync();
                break;
            case "MESHMETRICS":
            case "MESHVOLUME":
                _ = MeshMetricsAsync();
                break;
            case "MESHDIAGNOSE":
            case "MESHCHECK":
                _ = MeshDiagnoseAsync();
                break;
            case "MESHWELD":
            case "WELD":
                _ = MeshWeldAsync();
                break;
            case "MESHMERGE":
                _ = MeshMergeAsync();
                break;
            case "SOLIDIFY":
                _ = SolidifyAsync();
                break;
            case "VOXELVOLUME":
            case "VOXEL":
                _ = VoxelVolumeAsync();
                break;
            case "ENTITYTOBLOCKS":
            case "SOLID2BLOCK":
                _ = EntityToBlocksAsync();
                break;
            case "BOX":
                _ = BoxPrimitiveAsync();
                break;
            case "SPHERE":
                _ = SpherePrimitiveAsync();
                break;
            case "CYLINDER":
                _ = CylinderPrimitiveAsync();
                break;
            case "MESHBOUNDARY":
            case "MESHBOUND":
                _ = MeshBoundaryAsync();
                break;
            case "SOR":
                _ = DenoiseAsync(false);
                break;
            case "ROR":
                _ = DenoiseAsync(true);
                break;
            case "DEPOSITDETECT":
            case "DEPOSIT":
                _ = DepositDetectAsync();
                break;
            case "PROGRAMCOMPARE":
            case "PLANCOMPARE":
                _ = ProgramCompareAsync();
                break;
            case "CIRCLETTR":
            case "TTR":
                StartTTR();
                break;
            case "ARCSER":
                StartArcSer();
                break;
            case "LAYFRZ":
                FreezeCurrentLayer(true);
                break;
            case "LAYTHW":
                FreezeCurrentLayer(false);
                break;
            case "LAYLCK":
                LockCurrentLayer(true);
                break;
            case "LAYULK":
                LockCurrentLayer(false);
                break;
            case "LAYON":
                LayersAllOn();
                break;
            case "NEW":
                NewScene();
                break;
            case "UNDO":
            case "U":
                DoUndo();
                break;
            case "REDO":
                DoRedo();
                break;
            case "LAYER":
            case "LA":
                { var l = _layers.CycleCurrent(); StatusMsg.Text = $"当前图层「{l.Name}」"; }
                break;
            case "OPEN":
                _ = OpenSceneAsync();
                break;
            case "SAVE":
                _ = SaveSceneAsync();
                break;
            default:
                if (!ActivateDrawTool(cmd)) DispatchRibbon(cmd);   // 英文 switch 未识别 → 转中文命令链(命令框也能打中文命令)
                break;
        }
    }
}
