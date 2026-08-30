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
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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

            // 编辑（移动/复制/镜像）：左键取点（与命令行坐标共用 FeedPoint）
            if (_editMode != EditMode.None && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null) FeedPoint(wp.Value.x, wp.Value.y);
                return;
            }

            // 修剪/延伸：点目标线 → 其近端点移到与边界(任意实体)的最近交点
            if (_trimActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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
                        else StatusMsg.Text = "该实体暂不支持打断（记录：仅直线，多段线/圆弧待做）";
                        _breakActive = false; _breakPts.Clear();
                        RefreshScene();
                    }
                }
                return;
            }

            // 分帮扩帮：点方向/步距 → 批量偏移台阶线
            if (_benchActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null)
                {
                    if (_pathP1 == null) { _pathP1 = (wp.Value.x, wp.Value.y); StatusMsg.Text = "点对点寻径：点终点"; }
                    else { ComputePath(_pathP1.Value, (wp.Value.x, wp.Value.y)); _pathActive = false; _pathP1 = null; }
                }
                return;
            }

            // 偏移：点击一侧 → 偏移选中实体（保留原实体颜色/图层）
            if (_offsetActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null) FeedPoint(wp.Value.x, wp.Value.y);
                return;
            }

            // 夹点编辑：空闲态单选，左键按在夹点上 → 开始拖拽该夹点
            if (props.IsLeftButtonPressed && _selected.Count == 1 && _measure == null
                && _editMode == EditMode.None && !_offsetActive && !_trimActive && !_breakActive && !_slideActive)
            {
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
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
            if (w != null && SnapToggle.IsChecked == true && snapSrc.Length > 0)
            {
                double tol = SnapTolWorld(p);
                _snapWorld = SnapPoints.FindNearest(snapSrc, w.Value.x, w.Value.y, tol);
                if (_snapWorld != null)
                {
                    Viewport.SetSnapMarker(SnapCross(_snapWorld.Value.x, _snapWorld.Value.y, tol * 0.6));
                    _snapShown = true;
                }
                else if (_snapShown) { Viewport.SetSnapMarker(null); _snapShown = false; }
            }
            else if (_snapShown) { Viewport.SetSnapMarker(null); _snapShown = false; }

            var shown = _snapWorld ?? w;
            CoordText.Text = shown != null
                ? $"X {shown.Value.x:0.00}  Y {shown.Value.y:0.00}{(_snapWorld != null ? "  [捕捉]" : "")}"
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
                _editMode = EditMode.None;
                _editPts.Clear();
                _offsetActive = false;
                _trimActive = false;
                _breakActive = false; _breakPts.Clear();
                _slideActive = false; _slideDragging = false; _slidePts.Clear();
                _pathActive = false; _pathP1 = null;
                _benchActive = false; _benchEntity = null;
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
    private (double x, double y)? _snapWorld;   // 当前捕捉到的世界点
    private (double x, double y)? _cursorWorld; // 当前光标世界点(橡皮筋预览用)
    private (double x, double y)? _lastInputPoint; // 上一取点(命令行相对坐标 @ 的基点)
    private float[] _snapVerts = System.Array.Empty<float>();   // 场景几何顶点缓存(对象捕捉源)
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
    private Avalonia.Point _pressPos;             // 按下位置（区分点击/拖拽）
    private enum EditMode { None, Move, Copy, Mirror, Rotate, Scale }
    private EditMode _editMode = EditMode.None;
    private readonly List<(double x, double y)> _editPts = new();   // 编辑取的点（基点/目标点/参照…）
    private bool _offsetActive;                    // 偏移：等待点击一侧
    private bool _trimActive;                       // 修剪/延伸：等待点目标线
    private bool _breakActive;                      // 打断：等待取两点
    private readonly List<(double x, double y)> _breakPts = new();   // 打断的两点
    private int _gripIndex = -1;                    // 夹点拖拽中的夹点序号(-1=无)
    private bool _pathActive;                       // 点对点寻径：等待取两点
    private (double x, double y)? _pathP1;
    private bool _benchActive;                      // 分帮扩帮：等待点方向/步距
    private SceneEntity? _benchEntity;
    private int _benchCount = 5;
    private System.Collections.Generic.List<BlockModel.Block>? _lastBlocks;   // 最近导入的块体(资源量用)
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
            if (cmd == "新建") { NewScene(); return; }
            if (cmd == "打开") { await OpenSceneAsync(); return; }
            if (cmd == "保存") { await SaveSceneAsync(); return; }
            if (cmd == "撤销") { DoUndo(); return; }
            if (cmd == "重做") { DoRedo(); return; }
            if (cmd == "导入") { await ImportDxfAsync(); return; }
            if (cmd == "导入点") { await ImportPointsAsync(); return; }
            if (cmd == "展绘钻孔" || cmd == "钻孔柱状图" || cmd == "导入钻孔数据" || cmd == "原始钻孔柱状图") { await ImportBoreholesAsync(); return; }
            if (cmd == "等高线" || cmd == "等高线生产" || cmd == "等值线") { await ContourFromCsvAsync(); return; }
            if (cmd == "创建三角网" || cmd == "三角网") { await CreateTinAsync(); return; }
            if (cmd == "坡度着色") { await ShadeTinAsync("坡度着色", "绿=平 → 红=陡", TerrainAnalysis.BuildSlopeMap); return; }
            if (cmd == "坡向着色") { await ShadeTinAsync("坡向着色", "按朝向 HSV 配色", TerrainAnalysis.BuildAspectMap); return; }
            if (cmd == "高程着色" || cmd == "分色显示" || cmd == "高程分带") { await ShadeTinAsync("高程着色", "低绿→中黄→高棕", TerrainAnalysis.BuildElevationMap); return; }
            if (cmd == "体积计算" || cmd == "算量" || cmd == "土方量") { await VolumeAsync(); return; }
            if (cmd == "两期点云算量" || cmd == "两期算量" || cmd == "两期土方") { await TwoEpochVolumeAsync(); return; }
            if (cmd == "圈范围算量") { await BoundaryVolumeAsync(); return; }
            if (cmd == "提取道路中心线" || cmd == "道路中线" || cmd == "提取道路中线") { ExtractCenterline(); return; }
            if (cmd == "点对点寻径" || cmd == "寻径" || cmd == "点对点寻路") { StartPathfind(); return; }
            if (cmd == "排土条带" || cmd == "条带填充" || cmd == "排土条带划分") { DumpStrips(); return; }
            if (cmd == "分帮扩帮" || cmd == "批量台阶扩帮" || cmd == "批量扩坑") { StartBench(); return; }
            if (cmd == "组合工作线" || cmd == "合并多段线" || cmd == "连接台阶线") { JoinPolylines(); return; }
            if (cmd == "块体模型" || cmd == "导入块体" || cmd == "地质体建模") { await ImportBlockModelAsync(); return; }
            if (cmd == "资源量估算" || cmd == "剥采比" || cmd == "快速估值") { ResourceReport(null); return; }
            if (cmd == "境界圈定" || cmd == "凸包" || cmd == "确定境界" || cmd == "采场圈定") { await BoundaryHullAsync(); return; }
            if (cmd == "剖面分析" || cmd == "剖面" || cmd == "点云剖面") { await SectionProfileAsync(); return; }
            if (cmd == "另存为") { await SaveAsAsync(); return; }
            if (cmd == "工具") { new NodeEditorWindow().Show(); StatusMsg.Text = "打开节点编辑器"; return; }
            if (cmd == "2D") { Viewport.SetViewMode(true); StatusMsg.Text = "视图: 2D 平面（正交俯视）"; return; }
            if (cmd == "3D") { Viewport.SetViewMode(false); StatusMsg.Text = "视图: 3D 轨道"; return; }
            if (cmd == "清空视图") { _selected.Clear(); Viewport.SetHighlight(null); Viewport.SetSnapMarker(null); _snapShown = false; RefreshScene(); StatusMsg.Text = "已清空选择/高亮/捕捉标记"; return; }
            if (cmd == "帮助文档") { ShowHelp(); return; }
            if (cmd == "选项") { ShowOptions(); return; }
            if (cmd == "注册") { StatusMsg.Text = "注册/授权：需接入国产数据库(达梦)授权系统（记录待做）"; return; }
            if (cmd == "删除") { DeleteSelected(); return; }
            if (cmd == "全部选择") { SelectAll(); return; }
            if (cmd == "快速选择" || cmd == "选择类似") { SelectSimilar(); return; }
            if (cmd == "最后") { SelectLast(); return; }
            if (cmd == "上次") { SelectPrevious(); return; }
            if (cmd == "分解") { ExplodeSelected(); return; }
            if (cmd == "新建图层") { var l = _layers.New(); PopulateDrawingLayers(); StatusMsg.Text = $"新建图层「{l.Name}」并置为当前"; return; }
            if (cmd == "图层特性管理器") { var l = _layers.CycleCurrent(); StatusMsg.Text = $"当前图层「{l.Name}」 显示{( l.Shown?"开":"关")}/{(l.Locked?"锁":"解锁")}（再点循环切换）"; return; }
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
            if (cmd == "修剪" || cmd == "延伸") { StartTrim(); return; }
            if (cmd == "圆TTR" || cmd == "圆(切切半径)") { StartTTR(); return; }
            if (cmd == "圆弧SER" || cmd == "圆弧(起点端点半径)") { StartArcSer(); return; }
            if (cmd == "打断") { StartBreak(); return; }
            if (cmd == "滑动多段线") { StartSlide(); return; }
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
            Title = "导入图形（DXF/DWG/OFF）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("支持的格式 (DXF/DWG/OFF)") { Patterns = new[] { "*.dxf", "*.dwg", "*.off" } },
                new FilePickerFileType("CAD 图纸 (DXF/DWG)") { Patterns = new[] { "*.dxf", "*.dwg" } },
                new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } }
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
            if (ext == ".pmx") { File.WriteAllText(path, SceneIO.Save(_scene)); SetDocPath(path); StatusMsg.Text = $"已另存 {Path.GetFileName(path)} · {_scene.Count} 实体"; }
            else { int n = SceneExportService.Export(_scene, path); StatusMsg.Text = $"已导出 {Path.GetFileName(path)} · {n} 实体（.dxf/.dwg 不改当前文档）"; }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"另存失败：{ex.Message}"; }
    }

    // ---------- 文件：新建 / 打开 / 保存（绘制场景内部格式）----------
    private void NewScene()
    {
        // 完整文档重置：绘图 / 导入 / 图层 / 选择 / 撤销 / 进行中的命令
        _scene.Clear();
        _selected.Clear(); _prevSelected = new();
        _tool = null; _measure = null;
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
            File.WriteAllText(path, SceneIO.Save(_scene));
            SetDocPath(path);
            StatusMsg.Text = $"已保存 {Path.GetFileName(path)} · {_scene.Count} 实体";
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
            var loaded = SceneIO.Load(File.ReadAllText(files[0].Path.LocalPath));
            _scene.Clear();
            foreach (var e in loaded.Entities) _scene.Add(e);
            _selected.Clear();
            Viewport.SetHighlight(null);
            RefreshScene();
            SetDocPath(files[0].Path.LocalPath);
            StatusMsg.Text = $"已打开 {Path.GetFileName(files[0].Path.LocalPath)} · {_scene.Count} 实体";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"打开失败：{ex.Message}"; }
    }

    // 共享导入逻辑：CAD(dxf/dwg) → 可编辑实体入场景；OFF 等网格 → 显示态
    private void ImportPath(string path)
    {
        StatusMsg.Text = $"正在导入 {Path.GetFileName(path)} …";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dxf" || ext == ".dwg") { ImportCadEditable(path); return; }

        var r = OffImportService.Load(path);
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
        PopulateObjectTreeCounts(er.TypeCounts, Path.GetFileName(path), er.Entities.Count);
        PopulateDrawingLayers();
        string warn = er.Warnings.Count > 0 ? $" · 跳过 {er.Warnings.Count} 类未支持" : "";
        StatusMsg.Text = $"已导入 {Path.GetFileName(path)} · {er.Entities.Count} 可编辑实体 · {er.LayerOrder.Count} 图层（可选中/编辑/删除）{warn}";
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
        RefreshScene();
        Viewport.FitBounds(new[] { r.Bounds[0], r.Bounds[1] - maxDepth * scale, r.Bounds[2] + width, r.Bounds[3] });
        StatusMsg.Text = $"已展绘 {r.Boreholes.Count} 个钻孔 · {cols.Count} 图元（柱状图，岩性配色）";
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
        BeginChange();
        int segCount = 0;
        for (int k = 1; k <= levels; k++)
        {
            double L = zmin + step * k;
            float t = (float)((L - zmin) / (zmax - zmin));
            foreach (var s in Contour.MarchingSquares(grid, gx0, gy0, gdx, gdy, L))
            {
                _scene.Add(new LineEntity { X0 = s.x0, Y0 = s.y0, X1 = s.x1, Y1 = s.y1, Cr = t, Cg = 0.45f, Cb = 1 - t });
                segCount++;
            }
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"等高线：{r.Points.Count} 点 → {levels} 层 · {segCount} 段（z {zmin:0.#}~{zmax:0.#}）";
    }

    // 创建三角网：散点 CSV → Delaunay → 三角边线框入场景
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
        var cells = BlockModel.BuildCells(r.Blocks, r.GradeMin, r.GradeMax);
        BeginChange();
        foreach (var e in cells) _scene.Add(e);
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

    // 文件管理器：选文件夹 → 列出该目录 .dxf
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
        var items = CadFileBrowser.ListDxf(dir);
        FileList.ItemsSource = items
            .Select(x => new ListBoxItem { Content = x.Name, Tag = x.Path })
            .ToList();
        StatusMsg.Text = items.Count == 0 ? "该文件夹无 .dxf 文件" : $"{items.Count} 个 .dxf（双击打开）";
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
            if (_lastImport != null && _lastImport.TypeGeometry.TryGetValue(type, out var geom))
                Viewport.SetHighlight(geom);
            else
            {
                var o = new List<float>();
                foreach (var en in _scene.Entities) if (CnOf(en) == type) en.Tessellate(o);
                Viewport.SetHighlight(o.Count > 0 ? o.ToArray() : null);
            }
        }
        else Viewport.SetHighlight(null);
    }

    // ---------- 右键上下文菜单 ----------
    private void OnCtxZoomExtents(object? s, RoutedEventArgs e) => Viewport.ZoomExtents();
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
        if (_selected.Count == 0) { Viewport.SetHighlight(null); return; }
        var o = new List<float>();
        foreach (var e in _selected) e.Tessellate(o);
        if (_selected.Count == 1)                       // 单选 → 叠加夹点方块
            foreach (var g in _selected[0].Grips()) AppendGripSquare(o, g.x, g.y, GripSize());
        Viewport.SetHighlight(o.ToArray());
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
        if (_selected.Count != 1) return -1;
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
    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb || string.IsNullOrWhiteSpace(tb.Text)) return;

        string cmd = tb.Text.Trim();
        tb.Text = string.Empty;

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
                new NodeEditorWindow().Show();
                StatusMsg.Text = "打开节点编辑器";
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
            case "DIST":
            case "DI":
                _measure = new MeasureState();
                _tool = null;
                StatusMsg.Text = "测距：点第一点";
                break;
            case "ERASE":
            case "E":
                DeleteSelected();
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
                if (!ActivateDrawTool(cmd)) StatusMsg.Text = $"执行: {cmd}";
                break;
        }
    }
}
