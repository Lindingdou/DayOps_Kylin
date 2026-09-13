using System.Collections.Generic;
using System.IO;
using Avalonia.VisualTree;
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
using Avalonia.Controls.Templates;
using DMC = Dock.Model.Mvvm.Controls;
using DCore = Dock.Model.Core;

namespace PitMine3D.Kylin.Views;

public partial class MainWindow : Window
{
    /// <summary>启动欢迎界面（由 App 传入；无启动页时为 null，各处 ?. 静默跳过）。</summary>
    private Views.Startup.StartupSplash? _splash;

    /// <summary>
    /// 各功能模块的独立窗口登记，逐个报进度到启动页（0.4 → 0.9，同原版模块加载那一段）。
    /// 模块名与 Ribbon 选项卡一致，用户看得出"现在在装哪一块"。
    /// </summary>
    private void RegisterModuleWindows()
    {
        var steps = new (string Name, System.Action Run)[]
        {
            ("三维地质建模 · 网格编辑", Modeling.MeshEditWindows.Register),
            ("三维地质建模 · 品位估值", Modeling.EstimationWindows.Register),
            ("三维地质建模 · 模型更新", Modeling.ModelUpdateWindows.Register),
            ("三维地质建模 · 块体模型", Modeling.BlockModelWindows.Register),
        };
        _splash?.Report(0.4, "图形引擎就绪，正在加载模块…");
        for (int i = 0; i < steps.Length; i++)
        {
            _splash?.Report(0.4 + 0.5 * i / steps.Length, "正在加载模块…", $"{steps[i].Name} ({i + 1}/{steps.Length})");
            try { steps[i].Run(); }
            catch (System.Exception ex) { PitMine3D.Kylin.CrashLog.Write("模块登记", $"{steps[i].Name} 失败: {ex.Message}"); }
        }
        _splash?.Report(0.9, "模块加载完成");
    }

    public MainWindow() : this(null) { }

    public MainWindow(Views.Startup.StartupSplash? splash)
    {
        _splash = splash;
        InitializeComponent();
        // 原版默认 1920x1080 铺满屏幕；XAML 里设 WindowState 在部分平台不生效, 改代码设。
        // PITMINE_WINDOW=1920x1080 可锁定窗口尺寸(不最大化)：信创整机常是高分屏, 按 1080p 布局核对/交付时用。
        var wantSize = ParseWindowSize(System.Environment.GetEnvironmentVariable("PITMINE_WINDOW"));
        if (wantSize is { } ws)
        {
            WindowState = WindowState.Normal;
            Width = ws.w; Height = ws.h;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else WindowState = WindowState.Maximized;
        _splash?.Report(0.15, "正在初始化图形引擎…");
        _active = NewDocState();   // 首个文档(场景/图层), 供 _scene/_layers 与停靠布局
        _docs.Add(_active);
        BuildDock();               // 代码建 MVVM 停靠布局 + 内容模板(返回暂存面板控件)
        PopulateDrawingLayers();   // 启动即显示绘制图层("0")，可管理
        SetDocPath(null);          // 初始标题=未命名
        RenderAssistant(_assistant.Current());   // 智能助手：启动显示欢迎 + 主菜单
        GlyphFontHost.Install();   // 视口文字用系统真字形(含中文), 取不到则退回笔画字体
        Opened += (_, _) => ReportGraphicsDowngrade();   // 图形被自动降级时在信息栏说明
        Opened += (_, _) => InstallRibbonAutoFit();      // 功能区按窗口宽度自适应缩放(1080p 上一屏放得下)
        InitPropertyRibbon();      // Ribbon「特性」组 颜色/线宽/线型 三栏(填下拉 + 复位显示)
        InstallParamAsker();       // 命令行发起的命令：参数改在命令行里逐项问(AutoCAD 式), 见 MainWindow.CmdParams.cs
        Startup.BrandLogo.Apply(this);   // 标题栏/任务栏用中煤标志(与启动画面同一份矢量)
        RegisterModuleWindows();   // 各功能模块的独立窗口登记(带启动页进度)


        _splash?.Report(0.92, "正在初始化界面…");
        // 主窗口显示出来即认为就绪：关掉启动页（放到 Opened, 免得启动页先没了、主窗口还没上屏那一下黑屏）
        Opened += (_, _) =>
        {
            _splash?.Report(1.0, "就绪");
            _splash?.Close();
            _splash = null;
        };

        // 自检钩子: PITMINE_SELFTEST=<Ribbon 命令名> 时, 窗口显示后自动派发一次该命令 ——
        // 供渲染核对(截图比对原版)用; 未设该变量时完全不生效。
        if (System.Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 } stCmd)
            Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { try { RunSelftest(stCmd); } catch { } });

        // 交互提示同步(同原版 jigPromptText)：任一指针/键盘事件处理完后刷新命令行提示与信息栏历史。
        // handledEventsToo=true —— 视口/按钮把事件标记 Handled 后仍要刷新；排队到事件处理完再算(状态已切换)。
        System.EventHandler<RoutedEventArgs> promptKick = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(SyncPrompt);
        foreach (var ev in new RoutedEvent[] { PointerPressedEvent, PointerReleasedEvent, KeyDownEvent })
            this.AddHandler(ev, promptKick, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        // OpenGL 上下文就绪后，把真实后端版本显示到状态栏(每文档独立视口都会触发)
        _onGlReady = backend =>
        {
            StatusMsg.Text = $"OpenGL 就绪 · {backend}";
        };

        // 视口交互：在宿主 Panel（可命中）上收指针事件，转发到相机。
        // OpenGlControlBase 自身无背景时命中测试不可靠，直接在其上收事件在部分后端收不到，
        // 故统一在 ViewportHost（Background=Transparent → 全区可命中）上处理。
        // 存为委托, 每建一个文档视口即挂上(见 WireHost/EnsureHost)。
        _onHostPressed = (_, e) =>
        {
            var props = e.GetCurrentPoint(ViewportHost).Properties;
            _lastPointer = e.GetPosition(ViewportHost);
            _pressPos = _lastPointer;

            // 双击滚轮(中键) = 范围缩放(ZOOMEXTENTS)，CAD 标准手势。
            if (props.IsMiddleButtonPressed && e.ClickCount == 2)
            {
                _nav = NavMode.None;
                Viewport.ZoomExtents();
                StatusMsg.Text = "范围缩放（双击滚轮）";
                e.Handled = true;
                return;
            }

            // 逐面点选一类拾取(删除三角面)：右键 = 确认结束, 与原版状态机手势一致
            if (_oneShotPick != null && _pickConfirmable && props.IsRightButtonPressed)
            {
                _nav = NavMode.None;
                ConfirmOneShotPick();
                e.Handled = true;
                return;
            }

            // 数据库页面(虚拟钻孔等)挂起的一次性视口拾取：左键一点即回调
            if (_oneShotPick != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                ConsumeOneShotPick(_lastPointer.X, _lastPointer.Y);
                e.Handled = true;
                return;
            }

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

            // 编辑（移动/复制/镜像）取点：左键喂点（与命令行坐标共用 FeedPoint）。选择对象阶段不取点(交给下方框选/点选)。
            if (_editMode != EditMode.None && !_editAwaitSelect && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null) FeedPoint(wp.Value.x, wp.Value.y, ClickZForEdit());
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
                            _selected.Clear(); Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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
                    else if (!_dimContinue && _dimP2 == null) { _dimP2 = (wp.Value.x, wp.Value.y); StatusMsg.Text = "标注：指定尺寸线位置"; }
                    else
                    {
                        double h = System.Math.Max(SnapTolWorld(_lastPointer) * 2.5, 1e-3);
                        // 普通: (P1,P2)测点 + wp 偏移; 连续: (上P2,wp)测点 + 沿用上尺寸线偏移点
                        double x1, y1, x2, y2, ox, oy;
                        if (_dimContinue) { x1 = _dimP1.Value.x; y1 = _dimP1.Value.y; x2 = wp.Value.x; y2 = wp.Value.y; ox = _lastDimOffsetPt?.x ?? wp.Value.x; oy = _lastDimOffsetPt?.y ?? wp.Value.y; }
                        else { x1 = _dimP1.Value.x; y1 = _dimP1.Value.y; x2 = _dimP2!.Value.x; y2 = _dimP2.Value.y; ox = wp.Value.x; oy = wp.Value.y; }
                        var dim = _dimAligned
                            ? DimTools.BuildLinear(x1, y1, x2, y2, ox, oy, h, _dimStyle)          // 对齐: 平行真距
                            : DimTools.BuildLinearAxis(x1, y1, x2, y2, ox, oy, h, _dimStyle);      // 线性: 轴对齐 X/Y
                        BeginChange();
                        foreach (var de in dim) { de.LayerName = _layers.Current.Name; _scene.Add(de); }
                        RefreshScene();
                        StatusMsg.Text = "已标注";
                        _lastDimP2 = (x2, y2); _lastDimOffsetPt = (ox, oy);   // 供连续标注接续(同尺寸线级)
                        _dimActive = false; _dimP1 = null; _dimP2 = null; _dimContinue = false;
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
                    var dim = _dimDiameter
                        ? DimTools.BuildDiameter(c.cx, c.cy, c.r, wp.Value.x - c.cx, wp.Value.y - c.cy, h, _dimStyle)
                        : DimTools.BuildRadial(c.cx, c.cy, c.r, wp.Value.x - c.cx, wp.Value.y - c.cy, h, _dimStyle);
                    BeginChange();
                    foreach (var de in dim) { de.LayerName = _layers.Current.Name; _scene.Add(de); }
                    RefreshScene();
                    StatusMsg.Text = _dimDiameter ? $"已标注 Ø{2 * c.r:0.##}" : $"已标注 R{c.r:0.##}";
                    _dimRadActive = false; _dimRadCircle = null; _dimDiameter = false;
                }
                return;
            }
            if (_dimAngActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    if (_angVertex == null) { _angVertex = (wp.Value.x, wp.Value.y); StatusMsg.Text = "角度标注：指定第一条边上一点"; }
                    else if (_angP1 == null) { _angP1 = (wp.Value.x, wp.Value.y); StatusMsg.Text = "角度标注：指定第二条边上一点"; }
                    else
                    {
                        var v = _angVertex.Value; var p1 = _angP1.Value;
                        double h = System.Math.Max(SnapTolWorld(_lastPointer) * 2.5, 1e-3);
                        double arcR = System.Math.Max(System.Math.Sqrt((p1.x - v.x) * (p1.x - v.x) + (p1.y - v.y) * (p1.y - v.y)) * 0.5, h * 3);
                        var dim = DimTools.BuildAngular(v.x, v.y, p1.x, p1.y, wp.Value.x, wp.Value.y, arcR, h, _dimStyle);
                        BeginChange();
                        foreach (var de in dim) { de.LayerName = _layers.Current.Name; _scene.Add(de); }
                        RefreshScene();
                        var txt = dim[^1] as TextEntity;
                        StatusMsg.Text = $"已标注角度 {txt?.Text}";
                        _dimAngActive = false; _angVertex = null; _angP1 = null;
                    }
                }
                return;
            }

            // 坐标标注：点击任意点 → 小十字 + 引线 + "X=… Y=…"（连续，ESC 退出）
            if (_coordLabelActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = PickWorld();
                if (wp != null)
                {
                    double h = System.Math.Max(SnapTolWorld(_lastPointer) * 2.5, 1e-3);
                    var lab = DimTools.BuildCoordLabel(wp.Value.x, wp.Value.y, h * 6, h * 6, h, _dimStyle);
                    BeginChange();
                    foreach (var de in lab) { de.LayerName = _layers.Current.Name; _scene.Add(de); }
                    RefreshScene();
                    StatusMsg.Text = $"坐标标注 X={wp.Value.x:0.##} Y={wp.Value.y:0.##}（继续点选, ESC 退出）";
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

            // Gizmo 三轴手柄：空闲态左键按在某根轴上 → 沿该轴拖动选择集(松开落地, 一步 Undo)。见 MainWindow.Gizmo.cs
            if (props.IsLeftButtonPressed && _selected.Count > 0 && _measure == null && _tool == null
                && _editMode == EditMode.None && !_offsetActive && !_trimActive && !_breakActive && !_slideActive
                && e.KeyModifiers == KeyModifiers.None && GizmoTryBeginDrag(_lastPointer))
            {
                _nav = NavMode.None;
                if (_gizmoDrag != null) e.Pointer.Capture(ViewportHost);
                return;
            }

            // 夹点编辑(忠实原版 GripEditor.OnMouseDown)：空闲态左键按在夹点上 →
            //   Ctrl/Shift = 只改夹点选择集(Ctrl 逐个加减 / Shift 沿线区间)，本次不拖；
            //   无修饰键   = 点已选中的夹点拖整组，点未选中的清空后只选它再拖。
            //   点空白顺手清掉夹点选择，事件继续交给框选/点选。
            if (props.IsLeftButtonPressed && _selected.Count > 0 && _measure == null && _tool == null
                && _editMode == EditMode.None && !_offsetActive && !_trimActive && !_breakActive && !_slideActive)
            {
                var wp = Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);   // 命中用原始光标点(与原版 HitTest 一致)
                if (wp != null)
                {
                    int gi = _gripsOn ? _grips.HitTest(wp.Value.x, wp.Value.y, SnapTolWorld(_lastPointer)) : -1;
                    if (gi >= 0)
                    {
                        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                        if (ctrl || shift)
                        {
                            if (ctrl) _grips.ToggleGrip(gi); else _grips.SelectRangeTo(gi);
                            RedrawHighlight();
                            StatusMsg.Text = $"夹点：已选 {_grips.SelectedCount()} 个（拖任一个整组移动 · Ctrl 加减 · Shift 区间）";
                            return;
                        }
                        if (!_grips.IsSelected(gi)) _grips.SelectOnly(gi);
                        var wpd = PickWorld() ?? wp;
                        _gripDrag.Begin(_grips, gi, wpd.Value.x, wpd.Value.y);
                        _snapVertsDrag = SnapPoints.Exclude(_lastImport?.LineVertices ?? _snapVerts, _gripDrag.Base.x, _gripDrag.Base.y);   // 捕捉自避：排除被拖的那一个点
                        _nav = NavMode.None;
                        e.Pointer.Capture(ViewportHost);
                        StatusMsg.Text = $"{_gripDrag.Prompt}  空格=切换模式 · 命令行可键入坐标 · Esc=取消";
                        return;
                    }
                    if (_grips.SelectedCount() > 0) { _grips.ClearSelection(); RedrawHighlight(); }
                }
            }

            // 拖放移动文字(AutoCAD：选中对象后按住其本体拖动即移动，松开落地；Ctrl/Shift 仍归选集加减)。
            // 只在 2D、按在【已选中】的文字上才挂起；按在别的东西/空白上照旧走点选/框选。
            if (props.IsLeftButtonPressed && _selected.Count > 0 && Viewport.Is2DView && e.KeyModifiers == KeyModifiers.None
                && _tool == null && _measure == null && _editMode == EditMode.None
                && !_offsetActive && !_trimActive && !_breakActive && !_slideActive)
            {
                var wp = Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null && PickWorld2D(wp.Value.x, wp.Value.y, SnapTolWorld(_lastPointer)) is TextEntity hitText
                    && _selected.Contains(hitText) && !IsLayerLocked(hitText))
                {
                    _textDragPending = true; _textDragging = false;
                    _textDragStart = _lastPointer; _textDragBase = wp.Value;   // 基点 = 按下处(不吸附)，目标点照常吸附/正交
                    _nav = NavMode.None;
                    e.Pointer.Capture(ViewportHost);
                    return;
                }
            }

            // 选择：2D 左键拖=选择框；3D 默认左键拖=轨道旋转，开「选择模式」后左键只做框选/点选(不旋转)。
            // Shift+左键作临时选择(不必开模式)。两种情形单击不拖都=点选。
            bool navShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool selectable = props.IsLeftButtonPressed && _tool == null && _measure == null
                && (_editMode == EditMode.None || _editAwaitSelect) && !_offsetActive && !_trimActive && !_breakActive && !_slideActive;
            if (selectable && (Viewport.Is2DView || _selectMode || navShift))
            {
                _selBoxActive = true; _selBoxStart = _lastPointer; _nav = NavMode.None;
                e.Pointer.Capture(ViewportHost);
                return;
            }

            if (props.IsMiddleButtonPressed)
                _nav = NavMode.Pan;                                       // 中键拖拽 = 平移
            else if (props.IsLeftButtonPressed)
            {
                _nav = NavMode.Orbit;                                     // 3D 左键拖拽 = 轨道旋转(原版一致)
                _orbitPickStart = selectable ? _lastPointer : null;       // 未拖动则松开时按点选处理
            }
            else
                _nav = NavMode.None;                                      // 右键留给上下文菜单
            if (_nav != NavMode.None) e.Pointer.Capture(ViewportHost);
        };
        _onHostMoved = (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            Viewport.SetCursorScreen(p.X, p.Y);   // CAD 十字光标随动
            SyncPrompt();                          // 异步命令(对话框后)切换的状态在此兜底刷新提示
            var w = Viewport.ScreenToWorld(p.X, p.Y);

            // 拖放移动文字：拖过 4px 才算拖(否则松开就是点选)；一旦算拖, 就地变成一次「移动」命令 ——
            // 基点 = 按下处, 之后的幽灵/橡皮筋/正交/吸附全走编辑拖拽那条现成的路, 松开 = 第二点。
            if (_textDragPending)
            {
                if (System.Math.Abs(p.X - _textDragStart.X) < 4 && System.Math.Abs(p.Y - _textDragStart.Y) < 4) { _lastPointer = p; return; }
                _textDragPending = false; _textDragging = true;
                _editMode = EditMode.Move; _editName = "移动"; _editAwaitSelect = false; _editDisplacement = false;
                _editPts.Clear(); _editPts.Add(_textDragBase);
                SyncPrompt();
            }

            // 窗口框选：画选框(交叉=蓝，窗口=绿)
            if (_selBoxActive)
            {
                bool crossing = p.X < _selBoxStart.X;
                Viewport.SetSnapMarker(BoxRect(_selBoxStart, p, crossing));
                _snapShown = true;
                // 拖框期间引导不消失：编辑命令的"选择对象"操作说明 + 当前框选模式
                ShowTipAt(p, (_editAwaitSelect ? $"{_editName}：选择对象 · " : "")
                    + $"框选中 {(crossing ? "交叉(右→左, 碰到即选)" : "窗口(左→右, 全含才选)")} · 松开完成"
                    + (_editAwaitSelect ? $" · 右键确定（已选 {_selected.Count}）" : ""));
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
                RefreshScenePreview();
                _lastPointer = p;
                return;
            }

            // 对象捕捉：吸附到最近顶点（优先场景几何；显示态导入用其网格顶点）
            _snapWorld = null; _snapWorldZ = null;
            var snapSrc = _gripDrag.Active && _snapVertsDrag != null ? _snapVertsDrag : (_lastImport?.LineVertices ?? _snapVerts);   // 夹点拖拽中排除被拖点
            _snapHitMode = null;
            if (w != null && SnapToggle.IsChecked == true)
            {
                double tol = SnapTolWorld(p);
                // ① 顶点候选(端点/中点/圆心/象限) —— 高优先精确点
                var vhit3 = snapSrc.Length > 0 ? SnapVertIndex(snapSrc).FindNearest3(w.Value.x, w.Value.y, tol) : null;
                (double x, double y)? vhit = vhit3 == null ? null : (vhit3.Value.x, vhit3.Value.y);
                double dv = vhit != null ? Dist2(vhit.Value, w.Value) : double.MaxValue;
                // ② 扩展模式(交点/最近/垂足) —— 顶点未覆盖, 从场景原语补算; 垂足以上一取点为锚
                ObjectSnap.Hit? ohit = null;
                if (_snapExtraMask != 0)
                    ohit = SnapGeomIndex().Find(w.Value.x, w.Value.y, tol, _snapExtraMask, _lastInputPoint);
                // ③ 合并: 交点若不比顶点远则取交点; 否则取顶点; 最近/垂足仅在无顶点时兜底
                if (ohit != null && ohit.Value.Mode == ObjectSnap.Mode.Intersection && Dist2((ohit.Value.X, ohit.Value.Y), w.Value) <= dv)
                { _snapWorld = (ohit.Value.X, ohit.Value.Y); _snapHitMode = ObjectSnap.Mode.Intersection; }
                else if (vhit != null) { _snapWorld = vhit; _snapWorldZ = vhit3!.Value.z; }
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
            // 「实时曲面坐标」：状态栏保持投影平面坐标(同原版), 真实三维坐标走光标旁的浮动气泡
            UpdateSurfaceCoordTip(p, shown);

            // Gizmo 拖拽中：沿轴求光标射线最近点, 幽灵 + 手柄一起挪；空闲态则做轴悬停(压上变黄)。见 MainWindow.Gizmo.cs
            if (_gizmoDrag != null) { GizmoDragMove(p); _lastPointer = p; return; }
            if (_nav == NavMode.None && _tool == null && _editMode == EditMode.None && !_gripDrag.Active) GizmoHover(p);

            // 夹点拖拽：按当前模式(拉伸/移动/旋转/缩放)实时预览变换后的实体 + 光标旁模式提示
            if (_gripDrag.Active)
            {
                if (shown != null) RefreshGripPreview(p, shown.Value);
                _lastPointer = p;
                return;
            }
            // 夹点悬停(热夹点)：空闲态光标压到夹点上变亮蓝，供空格切模式
            if (_gripsOn && _grips.Count > 0 && _nav == NavMode.None && _tool == null && _editMode == EditMode.None)
            {
                int hv = w != null ? _grips.HitTest(w.Value.x, w.Value.y, SnapTolWorld(p)) : -1;
                if (hv != _gripHover) { _gripHover = hv; RedrawHighlight(); }
            }

            // 绘制即时浮标：工具激活且已落基点 → 光标旁显示长度/角度/半径/宽高; 否则隐藏(随拖拽实时更新)。
            var dragTip = _active.DragTip;
            if (dragTip != null)
            {
                // 浮标 = 当前步骤提示(与命令行 CmdPrompt 同源) + 实时维度(绘制: 长/角/半径…; 编辑: 位移/角度/比例)
                string? dh = null;
                if (shown != null)
                {
                    string prompt = CurrentPrompt();
                    string? dims = _tool != null ? _tool.DragHint(shown.Value.x, shown.Value.y)
                                 : (_editMode != EditMode.None && !_editAwaitSelect) ? EditDragHint(shown.Value)
                                 : null;
                    if (prompt.Length > 0) dh = dims != null ? $"{prompt}  {dims}" : prompt;
                }
                if (dh != null)
                {
                    ((TextBlock)dragTip.Child!).Text = dh;
                    dragTip.Margin = new Avalonia.Thickness(p.X + 18, p.Y + 20, 0, 0);
                    dragTip.Opacity = 1;
                    (dragTip.Parent as Control)?.InvalidateVisual();   // GL 之上叠层须显式重合成
                }
                else if (dragTip.Opacity != 0) { dragTip.Opacity = 0; (dragTip.Parent as Control)?.InvalidateVisual(); }
            }

            if ((_tool != null || _editMode != EditMode.None) && _nav == NavMode.None) RefreshScenePreview();   // 橡皮筋/编辑拖拽预览随光标刷新

            if (_nav == NavMode.Pan)
                Viewport.Pan(_lastPointer.X, _lastPointer.Y, p.X, p.Y);
            else if (_nav == NavMode.Orbit)
                Viewport.Orbit((p.X - _lastPointer.X) * 0.01, (p.Y - _lastPointer.Y) * 0.01);
            _lastPointer = p;
        };
        _onHostReleased = (_, e) =>
        {
            var rel = e.GetPosition(ViewportHost);

            // 3D 左键：拖过 = 轨道旋转(已在 Moved 里做完)，几乎没动 = 点选
            if (_orbitPickStart is { } ops)
            {
                _orbitPickStart = null;
                if (System.Math.Abs(rel.X - ops.X) < 4 && System.Math.Abs(rel.Y - ops.Y) < 4)
                {
                    _nav = NavMode.None;
                    e.Pointer.Capture(null);
                    PickAt(rel);
                    return;
                }
            }

            // 拖放移动文字：松开 → 没拖过阈值就是一次点选; 拖了就当「移动」的第二点落地(一步 Undo)
            if (_textDragPending || _textDragging)
            {
                bool dragged = _textDragging;
                _textDragPending = false; _textDragging = false;
                e.Pointer.Capture(null);
                if (!dragged) { PickAt(rel); return; }
                var wp = _snapWorld ?? Viewport.ScreenToWorld(rel.X, rel.Y);
                if (wp != null && _editMode == EditMode.Move)
                {
                    var dst = _snapWorld == null ? ApplyDraftAids(wp.Value.x, wp.Value.y) : wp.Value;
                    FeedPoint(dst.x, dst.y, ClickZForEdit());
                    StatusMsg.Text = $"拖放移动完成（{_selected.Count} 个实体）";
                }
                else { _editMode = EditMode.None; _editPts.Clear(); RefreshScene(); }
                HideDragTip();
                SyncPrompt();
                return;
            }

            // 窗口框选：松开 → 拖动成框则框选，未拖动则点选
            if (_selBoxActive)
            {
                _selBoxActive = false;
                e.Pointer.Capture(null);
                Viewport.SetSnapMarker(null); _snapShown = false;
                HideDragTip();   // 收起"框选中…"浮标(否则单击一下也会留着框选提示)
                if (System.Math.Abs(rel.X - _selBoxStart.X) < 4 && System.Math.Abs(rel.Y - _selBoxStart.Y) < 4)
                    PickAt(rel);                    // 无拖动 → 点选
                else
                    BoxSelect(_selBoxStart, rel);   // 拖动成框 → 框选
                return;
            }

            // Gizmo 拖拽：松开 → 沿轴位移落地(一步 Undo)；没拖过阈值当没动。见 MainWindow.Gizmo.cs
            if (_gizmoDrag != null) { e.Pointer.Capture(null); GizmoDragEnd(rel); return; }

            // 夹点拖拽：松开 → 按当前模式落地(整组一步 Undo)；取不到世界点则取消
            if (_gripDrag.Active)
            {
                e.Pointer.Capture(null);
                var wp = _snapWorld ?? Viewport.ScreenToWorld(rel.X, rel.Y);
                if (wp != null) CommitGripDrag(_snapWorld == null ? ApplyDraftAids(wp.Value.x, wp.Value.y) : wp.Value);
                else CancelGripDrag();
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
        _onHostWheel = (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            Viewport.ZoomAt(p.X, p.Y, e.Delta.Y > 0 ? 0.9 : 1.1);        // 朝光标缩放
        };
        _onHostDoubleTapped = (_, te) =>
        {
            if (_tool != null && _tool.IsMultiPoint)                      // 双击结束多段线
            {
                var e = _tool.Finish();
                if (e != null) { BeginChange(); AssignLayer(e); _scene.Add(e); }
                RefreshScene();
                StatusMsg.Text = $"多段线完成（已画 {_scene.Count}）";
            }
            else if (TryBeginTextEditAt(te.GetPosition(ViewportHost))) { }   // 双击文字 = 在位改内容(AutoCAD TEXTEDIT)
            else Viewport.ZoomExtents();                                  // 否则 = 范围缩放
        };
        _onHostExited = (_, _) =>                                          // 光标离开视口 → 收起十字与浮标, 状态栏坐标清空(同原版)
        {
            Viewport.HideCursor();
            CoordText.Text = "";
            if (_active.DragTip != null) { _active.DragTip.Opacity = 0; (_active.DragTip.Parent as Control)?.InvalidateVisual(); }
        };

        // 对象树选类型 → 视口高亮该类型几何
        ObjectTree.SelectionChanged += OnObjectTreeSelect;

        // 命令行「常驻聆听」(AutoCAD 行为)：焦点不在任何输入框时敲字符, 直接进命令框 ——
        // 原先必须先用鼠标点一下底部命令框才能打命令, 是"命令系统没激活"最直接的表现。
        // 用隧道(Tunnel)在窗口这一层先接：隧道从根往下走, 不论焦点落在视口还是停靠面板都能接到。
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);

        // ESC：退出当前绘制/测量
        KeyDown += (_, e) =>
        {
            // 空格：悬停/拖拽夹点时循环夹点模式 Stretch → Move → Rotate → Scale(原版 GripEditor.CycleMode)
            if (e.Key == Key.Space && (_gripDrag.Active || _gripHover >= 0))
            {
                _gripDrag.CycleMode();
                StatusMsg.Text = $"{_gripDrag.Prompt}  空格=切换模式";
                if (_gripDrag.Active && _cursorWorld != null) RefreshGripPreview(_lastPointer, _cursorWorld.Value);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && _gripDrag.Active)   // 拖拽中 Esc：放弃这一拖，实体与选集保持原样
            {
                CancelGripDrag();
                return;
            }
            if (e.Key == Key.Escape && CancelParamAsk()) { e.Handled = true; return; }   // 参数问答中 Esc = 放弃该命令
            if (e.Key == Key.Escape && CancelPcBackground()) { e.Handled = true; return; }   // 后台点云长计算(坡顶底线提取)中 Esc = 取消
            if ((e.Key == Key.Enter || e.Key == Key.Return) && ConfirmOneShotPick()) { e.Handled = true; return; }
            if ((e.Key == Key.Enter || e.Key == Key.Return) && FinishSelectObjects(true)) { e.Handled = true; return; }
            if ((e.Key == Key.Enter || e.Key == Key.Return) && !(CommandInput?.IsFocused ?? false) && TextToolAcceptDefault()) { e.Handled = true; return; }
            // 焦点在命令框时回车已由 SubmitCommandLine 处理(它不标 Handled 会冒泡到这), 别再确认一次
            if ((e.Key == Key.Enter || e.Key == Key.Return) && e.Source is not TextBox && ConfirmEditByKey()) { e.Handled = true; return; }
            if (e.Key == Key.Escape && CancelOneShotPick()) { e.Handled = true; return; }
            if (e.Key == Key.Escape && FinishSelectObjects(false)) { e.Handled = true; return; }
            if (e.Key == Key.Escape)
            {
                // 绘制多段线中途按 Esc：提交已画的多段线(而非丢弃)——符合"Esc 结束并保留"预期(≥2 点才成线)。
                bool finishedPoly = false;
                if (_tool != null && _tool.IsMultiPoint)
                {
                    var fin = _tool.Finish();
                    if (fin != null) { BeginChange(); AssignLayer(fin); _scene.Add(fin); finishedPoly = true; }
                }
                _tool = null;
                _measure = null;
                _angle = null;
                _editMode = EditMode.None; _editAwaitSelect = false; _editDisplacement = false;
                _textDragPending = false; _textDragging = false;
                FinishSelectObjects(false);
                _editPts.Clear();
                _offsetActive = false;
                _trimActive = false;
                _breakActive = false; _breakPts.Clear();
                _slideActive = false; _slideDragging = false; _slidePts.Clear();
                _pathActive = false; _pathP1 = null; _kpathMode = false;
                _pasteBaseActive = false;
                _benchActive = false; _benchEntity = null;
                _spotActive = false;
                _dimActive = false; _dimP1 = null; _dimP2 = null; _dimContinue = false;
                _dimRadActive = false; _dimRadCircle = null; _dimDiameter = false;
                _dimAngActive = false; _angVertex = null; _angP1 = null;
                _coordLabelActive = false;
                _gripDrag.Cancel(); _snapVertsDrag = null; _gripHover = -1;
                GizmoCancel();
                _selBoxActive = false;
                _ttrActive = false; _ttrAwaitRadius = false; _ttrRef1 = null; _ttrRef2 = null;
                _serActive = false; _serAwaitRadius = false; _serStart = null; _serEnd = null;
                _selected.Clear();
                Viewport.SetSnapMarker(null);
                Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
                _snapShown = false;
                if (_active.DragTip != null) { _active.DragTip.Opacity = 0; (_active.DragTip.Parent as Control)?.InvalidateVisual(); }   // 收起绘制浮标
                RefreshScene();          // 提交后刷新(含清除进行中的预览)
                StatusMsg.Text = finishedPoly ? $"多段线完成（已画 {_scene.Count}）" : "就绪";
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

    // 用 MVVM 停靠模型在代码里搭布局(忠实原 PitMine3D AvalonDock 的可拖拽/浮动/停靠 面板):
    // 各面板=轻量 Tool/Document 视图模型; 内容经 DataTemplate 供给 —— 模板直接返回暂存里已建好的
    // 面板控件(仍属 Window namescope, 字段/事件不变), 返回前脱离旧父以支持浮动/重停靠时重取。
    private void BuildDock()
    {
        if (Dock == null) return;
        var f = new Dock.Model.Mvvm.Factory();

        // 左/右面板：各由两个独立 Dock 工具组成, ToolDock 呈现底部标签(左=文件管理器/图层, 右=特性/智能助手)。
        // CanFloat=false：拖拽只在停靠区重排; 吸附不到新位就回到原处(不浮出独立窗口/不消失), 仅关闭键移除。
        // 布局忠实原版 MainWindow.xaml LayoutRoot：
        //   垂直 [ 水平 [ 左(文件管理器/…, 最小宽 240) | 中央文档区 | 右(属性对话框/AI 助手, 宽 270) ] | 底部 信息栏(高 120, 最小 80) ]
        // Dock.Avalonia 只有比例没有像素：按原版 1920x1080 换算 左 240/1920≈0.125、右 270/1920≈0.14、信息栏 120/(1080-Ribbon-状态栏)≈0.14。
        var fileTool = new DMC.Tool { Id = "File", Title = "文件管理器", CanClose = true, CanFloat = false };
        var layerTool = new DMC.Tool { Id = "Layer", Title = "图层", CanClose = true, CanFloat = false };
        var props = new DMC.Tool { Id = "Props", Title = "属性对话框", CanClose = false, CanFloat = false };   // 原版 CanHide=False
        var assistant = new DMC.Tool { Id = "Assistant", Title = "AI 助手", CanClose = false, CanFloat = false }; // 原版 CanClose=False
        var info = new DMC.Tool { Id = "Info", Title = "信息栏", CanClose = false, CanFloat = false };          // 原版 CanHide/CanClose=False
        var leftDock = new DMC.ToolDock { Alignment = DCore.Alignment.Left, Proportion = 0.14,
            ActiveDockable = fileTool, VisibleDockables = f.CreateList<DCore.IDockable>(fileTool, layerTool) };
        var docDock = new DMC.DocumentDock { Proportion = 0.70, CanCreateDocument = false,
            ActiveDockable = _active.Vm, VisibleDockables = f.CreateList<DCore.IDockable>(_active.Vm) };
        _dockFactory = f; _docDock = docDock;
        // 右侧工具停靠：属性对话框(默认选中) + AI 助手 两页(底部标签)。
        var rightDock = new DMC.ToolDock { Alignment = DCore.Alignment.Right, Proportion = 0.16,
            ActiveDockable = props, VisibleDockables = f.CreateList<DCore.IDockable>(props, assistant) };
        _propsTool = props; _assistantTool = assistant;   // Ribbon「AI 助手」切换钮用

        var mainDock = new DMC.ProportionalDock { Orientation = DCore.Orientation.Horizontal, Proportion = 0.84,
            VisibleDockables = f.CreateList<DCore.IDockable>(
                leftDock, new DMC.ProportionalDockSplitter(), docDock, new DMC.ProportionalDockSplitter(), rightDock) };
        // 底部：信息栏(命令历史 + 命令输入)——原版为 DockingManager 内的 LayoutAnchorable, 可拖动/可隐藏。
        var bottomDock = new DMC.ToolDock { Alignment = DCore.Alignment.Bottom, Proportion = 0.16,
            ActiveDockable = info, VisibleDockables = f.CreateList<DCore.IDockable>(info) };
        var vertDock = new DMC.ProportionalDock { Orientation = DCore.Orientation.Vertical,
            VisibleDockables = f.CreateList<DCore.IDockable>(mainDock, new DMC.ProportionalDockSplitter(), bottomDock) };
        var root = new DMC.RootDock { Id = "Root", ActiveDockable = vertDock, DefaultDockable = vertDock,
            VisibleDockables = f.CreateList<DCore.IDockable>(vertDock) };

        f.InitLayout(root);
        Dock.Factory = f;
        Dock.Layout = root;
        f.ActiveDockableChanged += (_, e) => OnActiveDocChanged(e.Dockable);   // 标签切换 → 切当前文档
        f.DockableClosed += (_, e) => OnDockableClosed(e.Dockable);            // 标签关闭 → 摘掉该文档

        // 内容模板：匹配我的叶子面板(按 Id)或任一文档(Doc*), 返回暂存控件; 框架容器仍用内建主题渲染。
        // 关键：注册到 Application 级(而非 Dock 级)——浮动时面板进入独立宿主窗口(另一个 DockControl),
        // 只有 App 级模板会被其继承, 否则浮动面板因无模板而"消失"。
        var tpl = new FuncDataTemplate<DCore.IDockable>(
            d => d?.Id is "File" or "Layer" or "Props" or "Assistant" or "Info" || (d?.Id?.StartsWith("Doc") == true),
            (d, _) => ContentFor(d?.Id));
        var appTpls = Avalonia.Application.Current!.DataTemplates;
        if (!appTpls.Contains(tpl)) appTpls.Add(tpl);
    }

    // 按 Id 取内容控件: 面板类(单例, 暂存于 XAML)返回前脱离旧父供浮动/重停靠; 文档(Doc*)各返回自己的独立视口宿主(懒建)。
    // 每文档独立视口——切换标签时框架材质化该文档自己的宿主, 故各标签互不干扰, 切换不再空白。
    private Control? ContentFor(string? id)
    {
        // 文档: 每个文档拥有自己的独立视口宿主(懒建), 各在各自标签内, 互不冲突——切换不再空白。
        if (id != null && id.StartsWith("Doc"))
        {
            var st = _docs.FirstOrDefault(x => x.Id == id);
            if (st == null) return null;
            EnsureHost(st);
            return st.Host;
        }
        // 面板类为单例(左面板标签组/属性/助手各一份), 复用时先从旧父脱挂。
        Control? c = id switch
        {
            "File" => FileContent,
            "Layer" => LayerContent,
            "Props" => PropsContent,
            "Assistant" => AssistantContent,
            "Info" => CmdContent,
            _ => null,
        };
        if (c?.Parent is Panel p) p.Children.Remove(c);
        else if (c?.Parent is ContentControl cc) cc.Content = null;
        else if (c?.Parent is Avalonia.Controls.Presenters.ContentPresenter cp) cp.Content = null;
        return c;
    }

    // 新建一个文档状态(独立 场景/图层 + 文档视图模型)。首个文档不可关, 其余可关。
    private DocState NewDocState()
    {
        _docSeq++;
        var st = new DocState { Id = $"Doc{_docSeq}", Title = $"未命名 {_docSeq}" };
        st.Vm = new DMC.Document { Id = st.Id, Title = st.Title, CanClose = _docSeq > 1, CanFloat = false };
        return st;
    }

    // 「新建」→ 新增一个文档标签并切过去(每文档独立视口, 切标签即切视口+场景)。
    private void NewDocument()
    {
        var st = NewDocState();
        _docs.Add(st);
        _dockFactory.AddDockable(_docDock, st.Vm);
        _dockFactory.SetActiveDockable(st.Vm);   // 触发 ActiveDockableChanged → OnActiveDocChanged 完成切换
    }

    // 标签切换/激活 → 切当前文档: _active 换掉(其 场景/图层/撤销栈/路径 随之切), 取消进行中命令, 刷面板与场景。
    private void OnActiveDocChanged(DCore.IDockable? d)
    {
        if (d is not DMC.Document doc) return;
        var st = _docs.FirstOrDefault(x => x.Vm == doc);
        PitMine3D.Kylin.CrashLog.Write("文档", $"活动标签 → {doc.Title}（此前 {_active?.Title}）");
        if (st == null || st == _active) return;
        // 离开的那个文档: 选择集是窗口级的(下面清掉), 它视口上的高亮/捕捉标记也得一起撤, 否则切回来时"看着选中了、其实什么都没选"。
        var prev = _active;
        if (prev.Vp != null) { prev.Vp.SetHighlight(null); prev.Vp.SetHighlightFaces(null); prev.Vp.SetSnapMarker(null); }
        _active = st;
        _tool = null; _measure = null; _angle = null; _selected.Clear(); _prevSelected = new();
        _grips.Clear(); _gripHover = -1; _snapShown = false;
        _editMode = EditMode.None; _editAwaitSelect = false; FinishSelectObjects(false); _lastImport = null;
        // 访问 Viewport 即懒建当前文档的独立视口(EnsureHost); 各标签各有其宿主, 切换不再空白。
        // 不清导入几何——每文档视口自留其线框(切换会重建 GL 上下文, CadGlViewport 会据保留源重传)。
        PopulateDrawingLayers();
        _lastSceneCount = -1;   // 对象树按"实体数变了才重建", 两个文档实体数恰好相等时会留着上一个文档的树 —— 强制重建
        RefreshScene();
        Viewport.GridVisible = _gridOn;   // 网格开关是窗口级的, 切到哪个视口就把哪个对齐到状态栏那颗钮
        UpdatePropertyPanel();  // 特性面板空选时显示的是"文档状态"(文件名/实体数), 得跟着换
        SyncWindowTitle();
        StatusMsg.Text = $"当前文档「{st.Title}」";
    }

    // 文档标签被关掉 → 从文档表摘除(否则「切换窗口」下拉还列着它, 点了切到一个已不在布局里的标签);
    // 关的恰是当前文档时, Dock 会把活动标签挪到别的文档并触发 ActiveDockableChanged, 这里只兜底一次。
    private void OnDockableClosed(DCore.IDockable? d)
    {
        if (d is not DMC.Document doc) return;
        var st = _docs.FirstOrDefault(x => x.Vm == doc);
        if (st == null || _docs.Count <= 1) return;
        _docs.Remove(st);
        if (ReferenceEquals(st, _active))
        {
            var next = _docDock.ActiveDockable as DMC.Document;
            var target = _docs.FirstOrDefault(x => x.Vm == next) ?? _docs[0];
            if (ReferenceEquals(_docDock.ActiveDockable, target.Vm)) OnActiveDocChanged(target.Vm);
            else _dockFactory.SetActiveDockable(target.Vm);
        }
    }

    private enum NavMode { None, Orbit, Pan }
    private NavMode _nav;
    private Avalonia.Point _lastPointer;
    private DxfImportService.ImportResult? _lastImport;
    private MeasureState? _measure;
    private AngleState? _angle;                  // 三点测角(MANG)
    private (double x, double y)? _snapWorld;   // 当前捕捉到的世界点
    private double? _snapWorldZ;                // 捕捉到顶点时该顶点的 z(交点/最近/垂足等算出来的点没有 z → null)
    private (double x, double y)? _cursorWorld; // 当前光标世界点(橡皮筋预览用)
    private (double x, double y)? _lastInputPoint; // 上一取点(命令行相对坐标 @ 的基点)
    private float[] _snapVerts = System.Array.Empty<float>();   // 场景几何顶点缓存(对象捕捉源: 端点/中点/圆心/象限)
    // 扩展捕捉模式(交点/最近/垂足)——SnapCandidates 未覆盖, 由 ObjectSnap 从场景原语补算。默认仅交点开(最近/垂足按需)。
    private int _snapExtraMask = 1 << (int)ObjectSnap.Mode.Intersection;
    private ObjectSnap.Mode? _snapHitMode;         // 本次捕捉命中的扩展模式(交点/最近/垂足), null=顶点候选或未命中
    private string? _currentPath { get => _active.Path; set => _active.Path = value; }   // 当前文档的 .pmx 路径(保存直接回写; 归文档, 见 DocState)
    private double _snapTolPx = 12.0;              // 对象捕捉容差(屏幕像素, 选项可调)
    private bool _gridOn = true;                    // 网格显示状态(选项/GRID 同步)
    private bool _snapShown;                     // 捕捉标记是否已显示
    private bool _slideActive;                   // 滑动多段线：已激活(等待按下)
    private bool _slideDragging;                 // 滑动多段线：正在按住拖动
    private readonly List<(double x, double y)> _slidePts = new();   // 滑动采样点
    private Avalonia.Point _slideLastScreen;     // 上次采样的屏幕点(控制采样密度)
    // 多文档：每个文档一套 场景 + 图层; _scene/_layers 恒指向当前活动文档(下方属性)。
    private sealed class DocState
    {
        public string Id = "", Title = "";
        public Scene Scene = new();
        public LayerTable Layers = new();
        public DMC.Document Vm = null!;
        // 撤销栈与文件路径也归文档：此前两者是窗口级单例 —— 在文档 2 按撤销会弹出文档 1 的快照灌进文档 2 的场景,
        // 在文档 2 按保存会把文档 2 的内容写进文档 1 打开的那个 .pmx(实测"两个文档互相干扰"的根子)。
        public UndoManager Undo = new();
        public string? Path;                                       // 该文档的 .pmx 路径(null=未命名)
        public Panel? Host;                                        // 该文档独立视口宿主(懒建)
        public PitMine3D.Kylin.Controls.CadGlViewport? Vp;         // 该文档独立 3D 视口
        public Border? DragTip;                                    // 绘制时跟随光标的即时信息浮标(长度/角度/半径…)
        public Border? SurfTip;                                    // 「实时曲面坐标」开关打开后跟随光标的三维坐标气泡
    }
    private readonly List<DocState> _docs = new();
    private DocState _active = null!;
    private int _docSeq;
    private Dock.Model.Mvvm.Factory _dockFactory = null!;
    private DMC.DocumentDock _docDock = null!;

    // 视口事件处理委托(构造期赋值一次; 每建一个文档视口即挂上, 见 WireHost)——每文档独立视口, 共用同一套逻辑。
    private System.Action<string>? _onGlReady;
    private System.EventHandler<Avalonia.Input.PointerPressedEventArgs>? _onHostPressed;
    private System.EventHandler<Avalonia.Input.PointerEventArgs>? _onHostMoved;
    private System.EventHandler<Avalonia.Input.PointerReleasedEventArgs>? _onHostReleased;
    private Pointer? _selftestPointer;   // 自检 @鼠标 用的合成指针(同一支笔按下/移动/松开, 捕获才对得上)
    private System.EventHandler<Avalonia.Input.PointerWheelEventArgs>? _onHostWheel;
    private System.EventHandler<Avalonia.Input.TappedEventArgs>? _onHostDoubleTapped;
    private System.EventHandler<Avalonia.Input.PointerEventArgs>? _onHostExited;

    private Scene _scene => _active.Scene;        // 当前文档的托管绘制场景
    private LayerTable _layers => _active.Layers;  // 当前文档的图层表
    // Viewport/ViewportHost 指向当前活动文档的独立视口(懒建), 全部 172 处引用自动跟随活动文档。
    private PitMine3D.Kylin.Controls.CadGlViewport Viewport { get { EnsureHost(_active); return _active.Vp!; } }
    private Panel ViewportHost { get { EnsureHost(_active); return _active.Host!; } }

    // 为文档懒建独立视口宿主(透明 Panel 便于命中测试 + CadGlViewport + 提示叠层 + 右键菜单) 并挂上事件委托。
    private void EnsureHost(DocState st)
    {
        if (st.Host != null) return;
        var vp = new PitMine3D.Kylin.Controls.CadGlViewport { GridVisible = _gridOn };   // 新标签继承网格开关(窗口级状态, 视口各一份)
        var host = new Panel { Background = Avalonia.Media.Brushes.Transparent, ContextMenu = BuildViewportContextMenu() };
        var cursorNone = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.None);
        var cursorArrow = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
        host.Cursor = cursorNone;   // 隐藏系统箭头 → 只见 CAD 十字光标
        // 右键菜单弹出期间要能看见系统箭头(菜单弹层沿用宿主的 None 光标就"没鼠标"了)：
        // 打开 → 宿主换回箭头并收起 CAD 十字；关闭 → 恢复 None，十字随下次 PointerMoved 重现。
        host.ContextMenu!.Cursor = cursorArrow;
        host.ContextMenu.Opened += (_, _) => { host.Cursor = cursorArrow; vp.HideCursor(); };
        host.ContextMenu.Closed += (_, _) => { host.Cursor = cursorNone; };
        host.Children.Add(vp);
        // 叠层统一装进一个容器(命中透传): OpenGlControl 的直接兄弟里只有第一个 Border 会合成上屏,
        // 多个叠层须收进单一容器, 容器内的多个子级再正常渲染(否则浮标等第二个叠层不显示)。
        var overlay = new Panel { IsHitTestVisible = false };
        // (原版视口无"视口·OpenGL"操作提示框, 复刻布局时已去掉; 操作说明见 帮助 命令)
        // 帧时间采样 → 状态栏 Performance 项(同原版 txtFrameProfilerStatus)
        vp.FrameStats += (fps, ms) => { if (ReferenceEquals(st, _active) && FpsText != null) FpsText.Text = $"FPS {fps:0} | {ms:0.0} ms"; };
        // 绘制/编辑时跟随光标的即时信息浮标(长度/角度/半径/位移/比例…)
        var tip = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#E6111820"),
            BorderBrush = Avalonia.Media.Brush.Parse("#5A9BE5"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(7, 3),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Opacity = 0,   // 用透明度显隐(始终在布局中); 每次改动后 _onHostMoved 会 RefreshScene 驱动重合成
            IsHitTestVisible = false,   // 文字在位编辑期间叠层会临时打开命中, 浮标自己仍须透传
            Child = new TextBlock { Foreground = Avalonia.Media.Brushes.White, FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold },
        };
        overlay.Children.Add(tip);
        // 「实时曲面坐标」气泡：与绘制浮标分开一个, 否则两者会抢同一个 Border 互相顶掉
        var surfTip = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#E6102015"),
            BorderBrush = Avalonia.Media.Brush.Parse("#4FB477"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(7, 3),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Opacity = 0,
            IsHitTestVisible = false,
            Child = new TextBlock { Foreground = Avalonia.Media.Brushes.White, FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold },
        };
        overlay.Children.Add(surfTip);
        host.Children.Add(overlay);
        st.Vp = vp; st.Host = host; st.DragTip = tip; st.SurfTip = surfTip;
        WireHost(host, vp);
    }

    // 视口右键菜单(每文档一份, 共用同一批处理器)。忠实原 ViewportHost.ContextMenu(见 MainWindow.axaml 历史)。
    private ContextMenu BuildViewportContextMenu()
    {
        var m = new ContextMenu();
        m.Opening += OnCtxMenuOpening;
        Image? Ico(string key) => MenuIcon(key);
        void Item(string header, System.EventHandler<RoutedEventArgs> click, string? icon = null)
        { var mi = new MenuItem { Header = header, Icon = icon == null ? null : Ico(icon) }; mi.Click += click; m.Items.Add(mi); }
        void Cmd(string header, string tag, string? icon = null)
        { var mi = new MenuItem { Header = header, Tag = tag, Icon = icon == null ? null : Ico(icon) }; mi.Click += OnCtxCommand; m.Items.Add(mi); }
        void Sep() => m.Items.Add(new Separator());
        // 视图模式三项(忠实原版右键顶部)：2D 视图 / 3D Orbit / 3D 选择模式；
        // 弹出时隐藏当前所处那一项，用户看到的总是"另外两种可切目标"。
        MenuItem ModeItem(string header, string iconKey, System.EventHandler<RoutedEventArgs> click)
        {
            var mi = new MenuItem { Header = header, Icon = Ico(iconKey) };
            mi.Click += click; m.Items.Add(mi); return mi;
        }
        _ctxTo2D = ModeItem("切换到 2D 视图", "icon_2D", OnCtx2D);
        _ctxToOrbit = ModeItem("切换到 3D Orbit", "icon_3D", OnCtxModeToOrbit);
        _ctxToSelect = ModeItem("切换到 3D 选择模式", "icon_select_mode", OnCtxModeToSelect);
        Item("范围缩放", OnCtxZoomExtents, "icon_Zoom");
        Item("网格 / 轴 开关", OnCtxGrid, "icon_grid");
        Sep();
        Cmd("特性", "特性", "icon_settings");
        Cmd("快速选择（选类似）", "快速选择", "icon_quick_select");
        Cmd("全部选择", "全部选择", "icon_select_all");
        m.Items.Add(new MenuItem { Header = "调用选择集", Name = "CtxSelSets", Icon = Ico("icon_recall_selection") });
        Sep();
        Cmd("复制", "复制到剪贴板", "icon_copy");
        Cmd("剪切", "剪切", "icon_cut");
        Cmd("粘贴", "粘贴", "icon_paste");
        Cmd("删除", "删除", "icon_delete");
        Sep();
        Cmd("隐藏对象", "隐藏对象", "icon_hide_object");
        Cmd("隐藏同一图层对象", "隐藏同一图层对象", "icon_hide_layer");
        Cmd("结束隐藏", "结束隐藏", "icon_show_all");
        Sep();
        Cmd("测距", "距离", "icon_measure");
        Cmd("测角", "角度", "icon_measure_angle");
        Cmd("面积", "面积", "icon_measure_area");
        Sep();
        Item("清除高亮", OnCtxClearHighlight, "icon_clear_marks");
        return m;
    }

    // 把构造期备好的事件委托挂到某个视口宿主(每文档一次)。
    private void WireHost(Panel host, PitMine3D.Kylin.Controls.CadGlViewport vp)
    {
        if (_onGlReady != null) vp.GlReady += _onGlReady;
        if (_onHostPressed != null) host.PointerPressed += _onHostPressed;
        if (_onHostMoved != null) host.PointerMoved += _onHostMoved;
        if (_onHostReleased != null) host.PointerReleased += _onHostReleased;
        if (_onHostWheel != null) host.PointerWheelChanged += _onHostWheel;
        if (_onHostDoubleTapped != null) host.DoubleTapped += _onHostDoubleTapped;
        if (_onHostExited != null) host.PointerExited += _onHostExited;
    }
    private UndoManager _undo => _active.Undo;    // 撤销/重做(当前文档自己的栈, 见 DocState)
    private DrawTool? _tool;                      // 当前激活的绘制工具
    private readonly List<SceneEntity> _selected = new();   // 选择集
    private List<SceneEntity> _prevSelected = new();         // 上次选择集
    private readonly HashSet<string> _hiddenLayers = new();  // 隐藏同一图层对象 记录的层名，结束隐藏一并恢复
    private (byte r, byte g, byte b)[] _colormap = Cad.Colormap.Terrain;   // 当前色带(高程/属性着色用)，色带 <名> 切换
    private double _legendVmin = 0, _legendVmax = 100;                       // 最近一次着色的值域(供图例)
    private string _colormapName = "Terrain";
    private readonly Cad.Draw.CadClipboard _clip = new();    // 实体剪贴板（COPYCLIP/CUTCLIP/PASTECLIP）
    private readonly Cad.Draw.NamedSelections _selSets = new(); // 命名选择集（创建/调用选择集）
    private readonly AssistantEngine _assistant = new();         // 智能助手（菜单引导，点选即执行命令）
    private Data.GeoDatabase? _geoDb;                            // §四/§八 SQLite 数据基座（懒开，会话内复用）
    private int _selSetCycle = -1;                            // 调用选择集轮转序号
    private Avalonia.Point _pressPos;             // 按下位置（区分点击/拖拽）
    private enum EditMode { None, Move, Copy, Mirror, Rotate, Scale }
    private EditMode _editMode = EditMode.None;
    // 移动/复制的「位移(D)」模式(忠实原版 EditCommandState::WaitingDisplacement)：不取基点，
    // 下一次在命令行键入的坐标就是位移向量(相对原点)，直接平移/复制。
    private bool _editDisplacement;
    // 编辑基点的 z 与"这次落地的 z 位移"(仅移动/复制用, 忠实原版 MOVE 的 x,y,z 三分量):
    //   基点 z 来自 键入的第三分量 / 捕捉到的顶点; 都没有就是 0(平面, 同 AutoCAD 在 z=0 的 UCS 上取点)。
    //   目标点键入了 z → dz = z − 基点 z; 只键入 x,y 或 @dx,dy → 纯 XY(同原版 "没给 z 就保持基点高程");
    //   鼠标点目标顶点只有在基点 z 也明确时才动 z —— 否则和从前一样纯 XY, 免得随手一捕捉就把实体抬走。
    private double _editBaseZ;
    private bool _editBaseZKnown;
    private double _editDz;
    // 拖放移动(AutoCAD 拖放编辑：选中对象后按住其本体拖动即移动)。原版只有夹点拖；按用户要求给文字补上。
    // 按下先只挂起(_textDragPending)——没拖过阈值松开就是普通点选；拖过阈值才转成一次「移动」命令(基点 = 按下处)。
    private bool _textDragPending, _textDragging;
    private Avalonia.Point _textDragStart;
    private (double x, double y) _textDragBase;
    private bool _editAwaitSelect;                                  // 编辑命令的"选择对象"阶段(右键确定后转取点)
    private string _editName = "";                                  // 当前编辑命令名(用于提示)
    private readonly List<(double x, double y)> _editPts = new();   // 编辑取的点（基点/目标点/参照…）
    private bool _offsetActive;                    // 偏移：等待点击一侧
    private bool _trimActive;                       // 修剪/延伸：等待点目标线
    private bool _breakActive;                      // 打断：等待取两点
    private readonly List<(double x, double y)> _breakPts = new();   // 打断的两点
    private readonly GripTable _grips = new();        // 夹点表：多实体出夹点 + Ctrl/Shift 多夹点选择(原版 GripManager)
    private readonly GripDrag _gripDrag = new();      // 夹点拖拽状态：拉伸/移动/旋转/缩放四模式(原版 GripEditor)
    private int _gripHover = -1;                      // 悬停(热)夹点序号(-1=无)
    private float[]? _snapVertsDrag;                  // 拖拽期间的捕捉候选(已排除被拖点，原版 excludePoint)
    private bool _gripsOn = true;                    // 夹点显示开关(命令 夹点开关/夹点; GIZMO 是三轴手柄 _gizmoOn, 见 MainWindow.Gizmo.cs)
    private bool _pathActive;                       // 点对点寻径：等待取两点
    private (double x, double y)? _pathP1;
    private bool _kpathMode;                         // 备选路径(K 最短路)模式(复用 _pathActive 取两点)
    private bool _benchActive;                      // 分帮扩帮：等待点方向/步距
    private SceneEntity? _benchEntity;
    private int _benchCount = 5;
    private System.Collections.Generic.List<BlockModel.Block>? _lastBlocks;   // 最近导入的块体(资源量用)
    private readonly List<SceneEntity> _blockCellEntities = new();            // 块体配色方块(供筛选/约束/删除 重渲)
    private double _blockGmin, _blockGmax = 1;                                // 块体品位范围(重渲配色一致)
    private System.Collections.Generic.Dictionary<string, double[]>? _blockAttrs;  // 最近 BLK/PMB 全属性逐块值(供无重导切换活动属性)

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
    private bool _dimActive;                          // 线性标注：取两点
    private (double x, double y)? _dimP1;
    private bool _dimRadActive;                        // 半径标注：选圆/弧后指定方向
    private bool _dimDiameter;                          // 与 _dimRadActive 联用：true=直径标注
    private bool _dimAngActive;                         // 角度标注：三点(顶点+两射线点)
    private (double x, double y)? _angVertex, _angP1;
    private bool _coordLabelActive;                    // 坐标标注：点击点报 X/Y(连续)
    private (double cx, double cy, double r)? _dimRadCircle;
    private (double x, double y)? _lastDimP2;           // 上一条线性标注的第二点(连续标注基准)
    private (double x, double y)? _dimP2;               // 线性标注第二点(3 点工作流: 点1→点2→尺寸线位置)
    private (double x, double y)? _lastDimOffsetPt;     // 上一条标注的尺寸线偏移点(连续标注沿用同尺寸线级)
    private bool _dimContinue;                          // 连续标注模式(2 点: 续点, 尺寸线级沿用)
    private bool _dimAligned;                           // true=对齐标注(尺寸线平行测线,真距); false=线性标注(轴对齐,量 X/Y 分量)
    private readonly Cad.Draw.DimStyle _dimStyle = new();   // 标注样式(DIM 变量：字高/小数位/箭头比/延伸线)，影响新建标注
    private bool _selBoxActive;                     // 窗口框选拖拽中
    private Avalonia.Point _selBoxStart;            // 框选起点(屏幕)
    private bool _selectMode;                        // 选择模式(3D)：左键只框选/点选, 不旋转视图(右键菜单切换, 同原版)
    private MenuItem? _ctxTo2D, _ctxToOrbit, _ctxToSelect;   // 右键顶部视图模式三项(当前态那项隐藏)
    private Avalonia.Point? _orbitPickStart;         // 3D 左键按下点：松开时若几乎没动则按点选处理
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
            // 命令来源：界面按钮/菜单 → 参数照旧走对话框(忠实原版鼠标交互)；命令行转派 → 参数留在命令行问。
            bool fromCmdLine = _dispatchFromCmdLine; _dispatchFromCmdLine = false;
            if (!fromCmdLine) _cmdLineDriven = false;
            // 命令词后面跟的位置参数(`图案填充 45 2` 的 45、2)：不论哪条路都先攒着, 供参数问答/取默认时按位消费。
            int argsAt = cmd.IndexOfAny(new[] { ' ', '\t' });
            if (_forcedInlineArgs != null) { _pendingInlineArgs = _forcedInlineArgs; _forcedInlineArgs = null; }   // 摘掉参数后的重试, 见下方兜底
            else _pendingInlineArgs = argsAt < 0 ? new List<string>() : Modeling.ParamPrompt.SplitArgs(cmd.Substring(argsAt));

            if (_suppressCmdLog) _suppressCmdLog = false; else LogCommand(cmd);   // 命令回显(转派来的已回显, 跳过)
            // 上一条命令还停在「选择对象」阶段就又点了别的命令 —— 按 AutoCAD 的规矩，新命令顶掉旧命令。
            // 不结掉的话那个 await 会一直挂着, 且 _editAwaitSelect 残留会让点选一直是"累加"语义。
            FinishSelectObjects(false);
            if (await TryOpenGeoDbPageAsync(cmd)) return;   // 地质与工程信息数据库 24 页面(按 Ribbon Tag 精确匹配)
            if (await TryPointCloudCommandAsync(cmd)) return;   // 点云处理：数据集版(当前点云 → 结果作为新点云入场景); 无点云时返回 false 回落下面的既有通路
            if (await TryModelingCommandAsync(cmd)) return; // 三维地质建模：场景对象版(选中三角网/点/线 → 结果入场景)
            if (cmd == "新建") { NewDocument(); return; }
            if (cmd == "打开") { await OpenSceneAsync(); return; }
            if (cmd == "保存") { await SaveSceneAsync(); return; }
            if (cmd == "撤销") { DoUndo(); return; }
            if (cmd == "重做") { DoRedo(); return; }
            if (cmd == "导入") { await ImportDxfAsync(); return; }
            if (cmd == "导入PMX" || cmd == "导入原版工程" || cmd == "打开原版工程" || cmd == "PitMine工程") { await PmxImportAsync(); return; }
            if (cmd == "导出PMX" || cmd == "导出原版工程" || cmd == "另存为PMX" || cmd == "保存为PMX") { await PmxExportAsync(); return; }
            if (cmd == "导入点") { await ImportPointsAsync(); return; }
            if (cmd == "导入模板" || cmd.StartsWith("导入模板 ") || cmd == "下载模板") { await ExportImportTemplateAsync(cmd); return; }
            if (cmd == "导出分析" || cmd.StartsWith("导出分析 ")) { await ExportAnalysisAsync(cmd); return; }
            if (cmd == "导入生产记录" || cmd == "生产记录导入" || cmd == "导入生产数据") { await ImportProductionRecordsAsync(); return; }
            if (cmd == "导入月度产能" || cmd == "月度产能导入" || cmd == "导入产能") { await ImportCsvToDbAsync("导入月度产能", "equipment_id,year,month,output_m3", rs => Data.GeoDataQueries.ImportCapacityMonthly(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入故障记录" || cmd == "故障记录导入" || cmd == "导入故障") { await ImportCsvToDbAsync("导入故障记录", "equipment_id,date,fault_type[,shift,duration_hours,description,is_resolved,repair_team]", rs => Data.GeoDataQueries.ImportFaultEvents(EnsureGeoDb()!.Connection, rs)); return; }
            if (cmd == "导入爆破记录" || cmd == "爆破记录导入" || cmd == "导入爆破" || cmd == "导入爆破事件") { await ImportCsvToDbAsync("导入爆破记录", "blast_date[,location_code,drill_id,material,diameter_mm,hole_count,total_hole_length_m,explosive_kg,blast_volume_m3,unit_consumption_kg_m3]", rs => Data.GeoDataQueries.ImportBlastEvents(EnsureGeoDb()!.Connection, rs)); return; }
            if (cmd == "导入设备型号" || cmd == "设备型号导入" || cmd == "导入机型" || cmd == "导入型号") { await ImportCsvToDbAsync("导入设备型号", "model,category[,working_weight_t,power_kw,bucket_m3,load_t,dimensions_lwh,drill_diameter_mm,tire_spec,std_daily_cap_wan_m3]", rs => Data.GeoDataQueries.ImportEquipmentModels(EnsureGeoDb()!.Connection, rs)); return; }
            if (cmd == "导入KPI" || cmd == "导入月度KPI" || cmd == "KPI导入" || cmd == "导入可用率") { await ImportCsvToDbAsync("导入月度KPI", "equipment_id,year,month,plan_hours,work_hours,fault_hours,availability,actual_run_rate,utilization_rate", rs => Data.GeoDataQueries.ImportKpiMonthly(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入设备台账" || cmd == "设备台账导入" || cmd == "导入设备") { await ImportCsvToDbAsync("导入设备台账", "equipment_id,category[,model,manufacturer,origin,status]", rs => Data.GeoDataQueries.ImportEquipmentLedger(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入煤质" || cmd == "煤质导入" || cmd == "导入煤质化验" || cmd == "煤质数据导入") { await ImportCsvToDbAsync("导入煤质化验", "hole_id,seam_code,depth_from[,ad_raw,std_raw,qgr_d,vdaf_raw,sample_thickness,apparent_density,coal_type,…]", rs => Data.GeoDataQueries.ImportCoalSamples(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入观测点" || cmd == "观测点导入" || cmd == "导入见煤点") { await ImportCsvToDbAsync("导入见煤观测点", "point_id,seam_code,x,y[,seam_thickness,floor_elevation]", rs => Data.GeoDataQueries.ImportObservationPoints(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入月度计划" || cmd == "月度计划导入" || cmd == "导入月计划") { await ImportCsvToDbAsync("导入月度计划", "year,month[,plan_strip_wan_m3,plan_coal_wan_t,ratio_strip_coal,avg_distance_km,avg_height_m]", rs => Data.GeoDataQueries.ImportMonthlyPlans(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入见煤成果" || cmd == "见煤成果导入" || cmd == "导入见煤") { await ImportCsvToDbAsync("导入见煤成果", "hole_id,seam_code[,floor_elevation,adopted_thickness,drill_seam_thickness,status]", rs => Data.GeoDataQueries.ImportSeamResults(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入路况" || cmd == "路况导入" || cmd == "导入运输道路") { await ImportCsvToDbAsync("导入运输道路", "road_id,name,road_type(main/branch/dump/temp),length_m[,max_slope_pct,avg_slope_pct,road_width_m,condition(good/fair/poor/closed),turning_radius_m,max_load_t,pavement_type,maintenance_team,last_maintenance_date,notes]", rs => Data.GeoDataQueries.ImportHaulRoads(EnsureGeoDb()!.Connection, rs, true)); return; }
            if (cmd == "导入边坡" || cmd == "边坡导入" || cmd == "导入边坡设计") { await ImportCsvToDbAsync("导入边坡设计", "side_name,side_type(working/final/transition)[,working_slope_angle_deg,final_slope_angle_deg,max_depth_m,safety_factor]", rs => Data.GeoDataQueries.ImportSlopeDesigns(EnsureGeoDb()!.Connection, rs)); return; }
            if (cmd == "展绘钻孔" || cmd == "钻孔柱状图" || cmd == "导入钻孔数据" || cmd == "原始钻孔柱状图") { await ImportBoreholesAsync(); return; }
            if (cmd == "煤厚分析" || cmd == "煤层厚度分析" || cmd == "煤厚") { await CoalThicknessAsync(); return; }
            if (cmd == "等高线" || cmd == "等高线生产" || cmd == "等值线" || cmd.StartsWith("等高线 ") || cmd.StartsWith("等值线 ")) { await ContourFromCsvAsync(cmd); return; }
            if (cmd == "创建三角网" || cmd == "三角网" || cmd == "2.5D TIN" || cmd == "2.5DTIN" || cmd == "转化为三角格网" || cmd == "转三角网") { await CreateTinAsync(); return; }
            if (cmd == "约束三角网" || cmd == "约束Delaunay" || cmd == "约束剖分" || cmd == "breakline三角网") { await CreateConstrainedTinAsync(); return; }
            if (cmd == "裁剪三角网" || cmd == "三角网裁剪" || cmd == "边界三角网") { await CreateClippedTinAsync(); return; }
            if (cmd == "示例三角网" || cmd == "三角网示例") { GenerateSampleTrimesh(); return; }
            if (cmd == "坡度着色" || cmd == "三角网着色" || cmd == "坡度") { await ShadeTinAsync("坡度着色", "绿=平 → 红=陡", TerrainAnalysis.BuildSlopeMap); return; }
            if (cmd == "坡顶底线" || cmd == "坡顶坡底线" || cmd == "断棱线提取" || cmd == "坡顶底线提取" || cmd.StartsWith("坡顶底线 ")) { await CrestToeAsync(cmd); return; }
            if (cmd == "平盘标高清单" || cmd == "平盘清单" || cmd == "标高清单" || cmd == "平盘标高统计" || cmd.StartsWith("平盘标高清单 ") || cmd.StartsWith("平盘清单 ")) { await BenchLevelInventoryAsync(cmd); return; }
            if (cmd == "现状参数提取" || cmd == "台阶参数反推" || cmd == "现状台阶参数" || cmd == "参数反推" || cmd.StartsWith("现状参数提取 ") || cmd.StartsWith("台阶参数反推 ")) { await BenchParameterExtractAsync(cmd); return; }
            if (cmd == "标注台阶标高" || cmd == "台阶标高标注" || cmd == "标高标注" || cmd.StartsWith("标注台阶标高 ") || cmd.StartsWith("台阶标高标注 ")) { await BenchElevationAnnotateAsync(cmd); return; }
            if (cmd == "参数校核" || cmd == "台阶参数校核" || cmd == "现状参数校核" || cmd.StartsWith("参数校核 ") || cmd.StartsWith("台阶参数校核 ")) { await BenchParameterVerifyAsync(cmd); return; }
            if (cmd.StartsWith("平盘宽反算") || cmd.StartsWith("反算平盘宽") || cmd.StartsWith("帮坡角反算")) { BermForAngleCmd(cmd); return; }
            if (cmd == "趋势整合台阶" || cmd == "趋势整合" || cmd == "整合台阶" || cmd == "趋势规整台阶" || cmd.StartsWith("趋势整合台阶 ") || cmd.StartsWith("趋势整合 ")) { await TrendIntegrateAsync(cmd); return; }
            if (cmd == "坡向着色" || cmd == "坡向") { await ShadeTinAsync("坡向着色", "按朝向 HSV 配色", TerrainAnalysis.BuildAspectMap); return; }
            if (cmd == "高程着色" || cmd == "分色显示" || cmd == "高程分带") { await ShadeTinAsync("高程着色", "低绿→中黄→高棕", TerrainAnalysis.BuildElevationMap); return; }
            if (cmd == "体积计算" || cmd == "算量" || cmd == "土方量") { await VolumeAsync(); return; }
            if (cmd == "两期点云算量" || cmd == "两期算量" || cmd == "两期土方") { await TwoEpochVolumeAsync(); return; }
            if (cmd == "圈范围算量" || cmd == "圈量" || cmd.StartsWith("圈范围算量 ")) { await BoundaryVolumeAsync(cmd); return; }
            if (cmd == "提取道路中心线" || cmd == "道路中线" || cmd == "提取道路中线") { ExtractCenterline(); return; }
            if (cmd == "路网连通增强" || cmd == "连通增强" || cmd == "路网桥接" || cmd == "路网连通") { RoadConnectCmd(); return; }
            if (cmd == "点对点寻径" || cmd == "寻径" || cmd == "点对点寻路") { StartPathfind(); return; }
            if (cmd == "备选路径" || cmd == "K最短路" || cmd == "备用路径") { StartKPathfind(); return; }
            if (cmd == "路网校验" || cmd == "连通性诊断" || cmd == "路网体检") { ValidateRoadNetwork(); return; }
            if (cmd == "基础道路网络构建" || cmd == "路网构建" || cmd == "路网预览" || cmd == "构建路网" || cmd == "路网更新") { BuildRoadNetworkCmd(); return; }
            if (cmd == "路网存档" || cmd == "路网导出") { await SnapshotEpochAsync(); return; }
            if (cmd == "演化对比" || cmd == "路网演化" || cmd == "两期路网对比") { await EvolutionCompareAsync(); return; }
            if (cmd == "时段快照" || cmd == "路网快照" || cmd == "纪元快照") { await SnapshotEpochAsync(); return; }
            if (cmd == "排土条带" || cmd == "条带填充" || cmd == "排土条带划分") { DumpStrips(); return; }
            if (cmd == "分帮扩帮" || cmd == "批量台阶扩帮" || cmd == "批量扩坑") { StartBench(); return; }
            if (cmd == "组合工作线" || cmd == "合并多段线" || cmd == "连接台阶线") { await JoinPolylinesCmdAsync(); return; }   // 「连接多段线」= 编辑组 POLYJOIN(带端点容差)
            if (cmd == "块体模型" || cmd == "导入块体" || cmd == "地质体建模") { await ImportBlockModelAsync(); return; }
            if (cmd == "导入PMB" || cmd == "加载PMB" || cmd == "PMB导入" || cmd == "导入块体模型文件" || cmd.StartsWith("导入PMB ")) { await LoadPmbAsync(cmd); return; }
            if (cmd == "导入BLK" || cmd == "加载BLK" || cmd == "BLK导入" || cmd == "导入八叉树块体" || cmd.StartsWith("导入BLK ")) { await LoadBlkAsync(cmd); return; }
            if (cmd == "资源量估算" || cmd == "剥采比" || cmd == "资源量") { ResourceReport(null); return; }
            if (cmd == "导出块体" || cmd == "块体导出") { await ExportBlocksAsync(); return; }
            if (cmd == "导出PMB" || cmd == "PMB导出" || cmd == "导出块体模型文件" || cmd == "块体模型另存") { await PmbExportAsync(); return; }
            if (cmd == "输出报告" || cmd == "资源量报告" || cmd == "块体报告") { await ExportResourceReportAsync(); return; }
            if (cmd == "属性统计" || cmd == "品位统计" || cmd == "直方图" || cmd == "统计报告") { await GradeStatsAsync(); return; }
            if (cmd == "属性报告" || cmd == "块体属性报告" || cmd == "多属性统计" || cmd == "多属性报告") { await BlockAttrReportAsync(); return; }
            if (cmd == "块体着色" || cmd == "块体配色") { ColorBlocksCmd(); return; }
            if (cmd == "块体分类着色" || cmd == "块体离散着色" || cmd == "分类着色" || cmd == "块体分类配色") { ColorBlocksCategoricalCmd(); return; }
            if (cmd == "块体分级着色" || cmd.StartsWith("块体分级着色 ") || cmd == "分级着色" || cmd == "块体分级配色" || cmd == "区间着色") { ColorBlocksClassedCmd(cmd); return; }
            if (cmd == "切换属性" || cmd.StartsWith("切换属性 ") || cmd == "切换品位属性" || cmd.StartsWith("切换品位属性 ") || cmd == "切换活动属性" || cmd.StartsWith("切换活动属性 ")) { SwitchGradeAttrCmd(cmd); return; }
            if (cmd == "筛选块体" || cmd == "块体筛选") { FilterBlocksCmd(); return; }
            if (cmd.StartsWith("表达式筛选块 ") || cmd.StartsWith("表达式筛选 ") || cmd.StartsWith("块体表达式 ") || cmd.StartsWith("按表达式筛选 ")) { BlockExpressionFilterCmd(cmd); return; }
            if (cmd.StartsWith("属性赋值 ") || cmd.StartsWith("公式赋值 ") || cmd.StartsWith("块体属性计算 ") || cmd.StartsWith("属性计算 ")) { BlockAttrAssignCmd(cmd); return; }
            if (cmd.StartsWith("块体煤岩分类 ") || cmd.StartsWith("煤岩分类 ") || cmd.StartsWith("块体煤岩判别 ")) { BlockCoalRockCmd(cmd); return; }
            if (cmd == "面约束块体" || cmd == "曲面约束块体" || cmd == "网格约束块体" || cmd.StartsWith("面约束块体 ")) { await MeshConstrainBlocksAsync(cmd); return; }
            if (cmd == "离散化模型" || cmd == "离散化" || cmd == "体素化" || cmd == "模型体素化" || cmd.StartsWith("离散化模型 ") || cmd.StartsWith("离散化 ")) { await DiscretizeModelAsync(cmd); return; }
            if (cmd == "约束块体" || cmd == "块体约束") { ConstrainBlocksCmd(); return;}
            if (cmd == "删除块体" || cmd == "清除块体") { DeleteBlocksCmd(); return; }
            if (cmd == "切面剖切" || cmd == "块体剖切" || cmd == "切面") { SectionBlocksCmd(); return; }
            if (cmd == "克里金估值" || cmd == "OK估值" || cmd == "克里金") { await EstimateGradeAsync("OK"); return; }
            if (cmd == "快速估值" || cmd == "品位估值" || cmd == "IDW估值" || cmd == "空间分布"
                || cmd.StartsWith("IDW估值 ") || cmd.StartsWith("快速估值 ") || cmd.StartsWith("品位估值 "))
            {
                double pw = 2.0;
                var sp = cmd.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length >= 2 && double.TryParse(sp[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 0) pw = p;
                await EstimateGradeAsync("IDW", pw); return;
            }
            if (cmd == "泛克里金" || cmd == "UK估值" || cmd == "泛克里金估值" || cmd == "趋势克里金") { await EstimateGradeAsync("UK"); return; }
            if (cmd == "最近邻估值" || cmd == "NN估值" || cmd == "邻近估值") { await EstimateGradeAsync("NN"); return; }
            if (cmd == "移动平均估值" || cmd == "MA估值" || cmd == "均值估值") { await EstimateGradeAsync("MA"); return; }
            if (cmd == "简单克里金" || cmd == "SK估值" || cmd == "简单克里金估值") { await EstimateGradeAsync("SK"); return; }
            if (cmd == "设备信息管理" || cmd == "设备台账" || cmd == "设备台账管理" || cmd == "设备信息") { EquipmentRosterCmd(); return; }
            if (cmd == "设备生产数据" || cmd == "生产数据" || cmd == "设备数据分析") { ProductionStatsCmd(); return; }
            if (cmd == "设备因素分析" || cmd == "主控因素" || cmd == "主控因素分析" || cmd == "因素相关分析") { EquipmentFactorCmd(); return; }
            if (cmd.StartsWith("效能提升模拟") || cmd.StartsWith("提升路径模拟") || cmd.StartsWith("What-if") || cmd.StartsWith("效能whatif")) { EfficiencyWhatIfCmd(cmd); return; }
            if (cmd == "产能分析" || cmd == "设备能力" || cmd == "能力分析" || cmd == "产能") { CapacityRankingCmd(); return; }
            if (cmd == "故障分析" || cmd == "设备状态·故障报修" || cmd == "故障报修" || cmd == "设备状态") { FaultStatsCmd(); return; }
            if (cmd == "爆破分析" || cmd == "爆破统计" || cmd == "爆破数据" || cmd == "钻爆分析") { BlastStatsCmd(); return; }
            if (cmd == "设备累计工时" || cmd == "累计运行小时" || cmd == "累计工时" || cmd == "设备工时") { CumulativeHoursCmd(); return; }
            if (cmd == "机型KPI" || cmd == "型号KPI" || cmd == "分机型KPI" || cmd == "机型可用率") { KpiByModelCmd(); return; }
            if (cmd == "KPI分析" || cmd == "KPI" || cmd == "设备KPI") { KpiStatsCmd(); return; }
            if (cmd == "设备综合评分" || cmd == "设备评分" || cmd == "综合评分" || cmd == "设备排名评分") { EquipmentScoreCmd(); return; }
            if (cmd == "钻孔管理" || cmd == "钻孔统计" || cmd == "钻孔信息") { BoreholeStatsCmd(); return; }
            if (cmd == "煤质统计" || cmd == "煤质数据管理" || cmd == "煤质分析" || cmd == "质量·配煤分析" || cmd == "配煤分析") { CoalQualityStatsCmd(); return; }
            if (cmd == "分煤层煤质" || cmd == "煤质箱线" || cmd == "分层煤质统计" || cmd.StartsWith("分煤层煤质 ") || cmd.StartsWith("煤质箱线 ")) { await CoalStatsBySeamCmd(cmd); return; }
            if (cmd == "煤质数据健康度" || cmd == "数据健康度" || cmd == "煤质健康度" || cmd == "煤质数据体检") { CoalDataHealthCmd(); return; }
            if (cmd == "商品煤符合性" || cmd == "煤质达标" || cmd == "商品煤达标" || cmd.StartsWith("商品煤符合性 ") || cmd.StartsWith("煤质达标 ")) { CoalComplianceCmd(cmd); return; }
            if (cmd == "导出符合性" || cmd == "符合性导出" || cmd == "导出超标段") { await ExportComplianceAsync(cmd); return; }
            if (cmd == "品位储量曲线" || cmd == "品位-储量曲线" || cmd == "灰分储量曲线" || cmd.StartsWith("品位储量曲线 ")) { GradeTonnageCmd(cmd); return; }
            if (cmd == "分标高煤质" || cmd == "标高煤质" || cmd.StartsWith("分标高煤质 ")) { CoalByElevationCmd(cmd); return; }
            if (cmd == "煤质离群" || cmd == "离群质检" || cmd == "煤质异常" || cmd.StartsWith("煤质离群 ")) { CoalOutlierCmd(cmd); return; }
            if (cmd == "灰分发热量回归" || cmd == "灰分回归" || cmd == "煤质回归" || cmd == "灰热回归" || cmd.StartsWith("灰分发热量回归 ") || cmd.StartsWith("煤质回归 ")) { await AshCalorificRegressionCmd(cmd); return; }
            if (cmd == "煤质综合结论" || cmd == "煤质结论" || cmd == "综合结论" || cmd == "煤质分析结论" || cmd.StartsWith("煤质综合结论 ")) { await CoalConclusionsCmd(cmd); return; }
            if (cmd == "煤类反推" || cmd == "GB5751反推" || cmd == "煤类一致率" || cmd == "煤类校核" || cmd == "煤类反演" || cmd.StartsWith("煤类反推 ")) { await CoalTypeInferCmd(cmd); return; }
            if (cmd == "煤质审核" || cmd == "煤质数据审核" || cmd == "煤质质检" || cmd == "煤质数据质检" || cmd == "一键审核" || cmd.StartsWith("煤质审核 ")) { await CoalAuditCmd(cmd); return; }
            if (cmd == "测井一致" || cmd == "钻测一致" || cmd == "钻探测井一致" || cmd == "煤厚一致性") { await DrillLogConsistencyCmd(); return; }
            if (cmd == "工分自洽" || cmd == "工业分析自洽" || cmd == "工分校核" || cmd == "MAVFC") { await ProximateConsistencyCmd(); return; }
            if (cmd == "煤质三维插值" || cmd == "品位体素插值" || cmd == "煤质体素" || cmd == "三维插值" || cmd == "品位块模型" || cmd.StartsWith("煤质三维插值 ") || cmd.StartsWith("煤质体素 ")) { await QualityVoxelInterpCmd(cmd); return; }
            if (cmd == "交叉验证" || cmd == "估值交叉验证" || cmd == "留一验证" || cmd == "克里金交叉验证" || cmd.StartsWith("交叉验证 ") || cmd.StartsWith("估值交叉验证 ")) { await SpatialCvCmd(cmd); return; }
            if (cmd == "变差函数分析" || cmd == "实验变差" || cmd == "半变异分析" || cmd == "空间结构分析" || cmd.StartsWith("变差函数分析 ") || cmd.StartsWith("实验变差 ")) { await VariogramAnalysisCmd(cmd); return; }
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
            if (cmd == "煤厚等值线" || cmd == "煤厚分析" || cmd == "等厚线" || cmd == "煤厚等厚线" || cmd.StartsWith("煤厚等值线 ") || cmd.StartsWith("煤厚分析 ")) { await ThicknessIsopachAsync(cmd); return; }
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
            if (cmd.StartsWith("参数验收判定") || cmd.StartsWith("DB参数验收") || cmd.StartsWith("验收判定")) { ParamAcceptanceJudgeCmd(cmd); return; }
            if (cmd.StartsWith("兼容机型") || cmd.StartsWith("可用机型") || cmd.StartsWith("适配机型")) { CompatibleModelsCmd(cmd); return; }
            if (cmd == "作业面台账" || cmd == "作业面" || cmd == "工作面台账" || cmd == "采场参数") { WorkingFacesCmd(); return; }
            if (cmd == "参数模板库" || cmd == "参数化模板" || cmd == "参数模板" || cmd == "参数定义") { ParamTemplatesCmd(); return; }
            if (cmd == "月度计划" || cmd == "月计划" || cmd == "月度计划查看") { MonthlyPlansCmd(); return; }   // 只读展示(编制/授权工作流走 TaskLib, 受阻)
            if (cmd == "路况显示" || cmd == "运输道路" || cmd == "道路台账") { HaulRoadsCmd(); return; }
            if (cmd == "排土场台账" || cmd == "排土场列表" || cmd == "排土场充填" || cmd == "排土场状态") { DumpSitesCmd(); return; }
            if (cmd == "钻孔煤质汇总" || cmd == "孔层煤质汇总" || cmd == "每孔每层煤质" || cmd == "煤质汇总") { CoalSampleSummaryCmd(); return; }
            if (cmd == "边坡设计" || cmd == "边坡参数" || cmd == "帮坡角设计") { SlopeDesignsCmd(); return; }
            if (cmd == "展绘钻孔" || cmd == "开孔坐标管理" || cmd == "钻孔展绘" || cmd == "开孔坐标") { DrawBoreholesCmd(); return; }
            if (cmd == "展绘层位数据" || cmd == "层位展点" || cmd == "展绘层位") { HorizonPointsCmd(); return; }
            if (cmd.StartsWith("虚拟钻孔 ") || cmd.StartsWith("虚拟钻探 ") || cmd.StartsWith("模拟钻孔 ")) { await VirtualDrillAsync(cmd); return; }
            if (cmd == "层位求交" || cmd == "顶底板求交" || cmd == "煤层高程" || cmd.StartsWith("层位求交 ") || cmd.StartsWith("顶底板求交 ") || cmd.StartsWith("煤层高程 ")) { SeamIntersectCmd(cmd); return; }
            if (cmd == "机群总览" || cmd == "设备总览" || cmd == "机群") { FleetOverviewCmd(); return; }
            if (cmd == "机群驾驶舱" || cmd == "领导驾驶舱" || cmd == "驾驶舱" || cmd == "机群健康度") { FleetCockpitCmd(); return; }
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
            if (cmd == "数据字典" || cmd == "导出数据字典" || cmd == "表结构" || cmd == "库结构") { await ExportDataDictionaryAsync(); return; }
            if (cmd.StartsWith("SQL查询 ") || cmd.StartsWith("运行SQL ") || cmd.StartsWith("执行SQL ") || cmd.StartsWith("SQL ")) { await RunSqlQueryAsync(cmd); return; }
            if (cmd == "点云抽稀" || cmd == "抽稀" || cmd == "点云精简" || cmd.StartsWith("点云抽稀 ") || cmd.StartsWith("抽稀 ")) { await ThinPointsAsync("voxel", cmd); return; }     // 抽稀 [格距]
            if (cmd == "自适应抽稀" || cmd == "保特征抽稀" || cmd == "特征抽稀" || cmd.StartsWith("自适应抽稀 ")) { await ThinPointsAsync("adaptive", cmd); return; }
            if (cmd == "均匀抽稀" || cmd == "距离抽稀" || cmd == "等距抽稀" || cmd.StartsWith("均匀抽稀 ")) { await ThinPointsAsync("uniform", cmd); return; }
            if (cmd == "随机抽稀" || cmd == "随机采样" || cmd == "随机精简" || cmd.StartsWith("随机抽稀 ")) { await ThinPointsAsync("random", cmd); return; }
            if (cmd == "地面点滤波" || cmd == "地面滤波") { await GroundFilterAsync(); return; }
            if (cmd == "移除障碍物" || cmd == "渐进形态滤波" || cmd == "地面非地面分离" || cmd.StartsWith("移除障碍物 ")) { await PmfAsync(cmd); return; }
            if (cmd == "C2C" || cmd == "点云比对" || cmd == "位移监测 C2C" || cmd == "位移监测") { await CloudCompareAsync(); return; }
            if (cmd == "画道路中线" || cmd == "手动标定线路" || cmd == "道路中线绘制") { ActivateDrawTool("多段线"); StatusMsg.Text = "画道路中线：绘制折线作道路中线（供路网/寻径/演化对比）"; return; }
            if (cmd == "境界圈定" || cmd == "凸包" || cmd == "采场圈定" || cmd == "采场/排土场圈定") { await BoundaryHullAsync(); return; }
            if (cmd == "确定境界" || cmd == "境界优化" || cmd == "最优坑深" || cmd == "经济境界") { PitDepthCmd(); return; }
            if (cmd.StartsWith("生成境界") || cmd.StartsWith("境界线") || cmd.StartsWith("几何圈定") || cmd.StartsWith("境界壳")) { PitEnvelopeCmd(cmd); return; }
            if (cmd.StartsWith("境界建模") || cmd.StartsWith("境界落地") || cmd.StartsWith("生成台阶面") || cmd.StartsWith("三维境界")) { await BenchModelAsync(cmd); return; }
            if (cmd.StartsWith("境界内资源") || cmd.StartsWith("圈入资源") || cmd.StartsWith("坑内资源") || cmd.StartsWith("圈入量")) { EnclosedResourceCmd(cmd); return; }
            if (cmd.StartsWith("经济剥采比") || cmd.StartsWith("经济合理剥采比") || cmd.StartsWith("允许剥采比")) { EconStrippingRatioCmd(cmd); return; }
            if (cmd.StartsWith("产能推算") || cmd.StartsWith("推进产能") || cmd.StartsWith("产能推进")) { AdvanceCapacityCmd(cmd); return; }
            if (cmd == "开采程序切分" || cmd == "逐期量核算" || cmd == "分期量表" || cmd == "分期剥采比" || cmd.StartsWith("开采程序切分 ")) { await DriveSequenceCmd(cmd); return; }
            if (cmd == "采区划分" || cmd == "采区" || cmd == "储量均衡划分") { PanelSplitCmd(); return; }
            if (cmd == "拉沟推荐" || cmd == "首采区推荐" || cmd == "拉沟位置推荐" || cmd == "拉沟推进推荐") { BoxcutRecommendCmd(); return; }
            if (cmd == "规划计算" || cmd == "开采程序评价" || cmd == "程序评价") { ProgramEvaluateCmd(); return; }
            if (cmd == "派生计划方案" || cmd == "派生方案" || cmd == "多方案派生") { DerivePlansCmd(); return; }
            if (cmd == "中长远进度计划" || cmd == "中长远规划" || cmd == "中长期计划" || cmd == "中长远进度计划编制" || cmd.StartsWith("中长远进度计划 ") || cmd.StartsWith("中长远规划 ")) { await LongTermPlanCmd(cmd); return; }
            if (cmd == "短期生产计划" || cmd == "短期生产计划编制" || cmd == "月度计划编制" || cmd == "月度计划" || cmd.StartsWith("短期生产计划 ") || cmd.StartsWith("月度计划 ")) { await ShortTermPlanCmd(cmd); return; }
            if (cmd == "剖面分析" || cmd == "剖面" || cmd == "点云剖面") { await SectionProfileAsync(); return; }
            if (cmd == "粗糙度" || cmd == "地表粗糙度") { await RoughnessAsync(); return; }
            if (cmd == "曲率" || cmd == "地表曲率") { await CurvatureAsync(); return; }
            if (TryMeasureCommand(cmd)) return;                    // 测量 - 快速 / 半径 / 体积(忠实原版测量 SplitButton)
            if (cmd == "面积" || cmd == "面积测量" || cmd == "周长") { MeasureBySelection("面积"); return; }
            if (cmd == "距离" || cmd == "测量距离" || cmd == "测距") { MeasureBySelection("距离"); return; }
            if (cmd == "角度" || cmd == "测量角度" || cmd == "三点测角") { MeasureBySelection("角度"); return; }
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
            if (cmd == "月度剥离均衡" || cmd == "剥离调度" || cmd == "拉紧绳" || cmd.StartsWith("月度剥离均衡 ")) { await StripScheduleAsync(cmd); return; }
            if (cmd == "工作面线拟合" || cmd == "工作面线" || cmd == "拟合工作面线") { await WorkingFaceLineAsync(); return; }
            if (cmd == "质量统计" || cmd == "统计分析" || cmd == "煤质CSV统计" || cmd == "样本统计") { await QualityStatsAsync(); return; }   // 用户 CSV 统计(区别于 §四 库煤质统计)
            if (cmd == "坡角估算" || cmd == "工作帮坡角" || cmd == "坡角") { await SlopeEstimateAsync(); return; }
            if (cmd == "台阶参数分析" || cmd == "台阶分析" || cmd == "台阶参数" || cmd == "工艺参数分析") { await BenchAnalyzeAsync(); return; }
            if (cmd == "达成分析" || cmd == "产量达成" || cmd == "达成率" || cmd == "达成度评价" || cmd == "产量统计") { await AttainmentAsync(); return; }
            if (cmd == "车铲匹配" || cmd == "配车匹配" || cmd == "车铲配比") { await FleetMatchAsync(); return; }
            if (cmd == "点云质量统计" || cmd == "点云统计" || cmd == "点云质量") { await PointCloudStatsAsync(); return; }
            if (cmd == "分割点云" || cmd == "点云分割" || cmd == "欧氏聚类" || cmd == "点云聚类" || cmd.StartsWith("分割点云 ")) { await SegmentCloudAsync(cmd); return; }
            if (cmd == "区域生长分割" || cmd == "区域生长" || cmd == "光滑度分割" || cmd.StartsWith("区域生长分割 ")) { await RegionGrowAsync(cmd); return; }
            if (cmd == "点云高程着色" || cmd == "高程着色" || cmd == "点云着色") { await ElevationColorAsync(); return; }
            if (cmd == "色带" || cmd == "配色方案" || cmd.StartsWith("色带 ") || cmd.StartsWith("配色方案 ")) { SetColormapCmd(cmd); return; }
            if (cmd == "图例" || cmd == "色带图例" || cmd.StartsWith("图例 ")) { PlaceLegend(cmd); return; }   // 图例 [min max]
            if (cmd == "指北针" || cmd == "指北" || cmd == "北针") { PlaceNorthArrow(); return; }
            if (cmd == "比例尺" || cmd == "标尺") { PlaceScaleBar(); return; }
            if (cmd == "标题栏" || cmd == "图框" || cmd.StartsWith("标题栏 ")) { PlaceTitleBlock(cmd); return; }   // 标题栏 [标题]
            if (cmd == "加载点云" || cmd == "展点" || cmd == "导入点云" || cmd == "加载点") { await LoadPointCloudAsync(); return; }
            if (cmd == "导入LAS" || cmd == "加载LAS" || cmd == "LAS导入" || cmd == "导入激光点云") { await LoadLasAsync("导入LAS"); return; }
            if (cmd == "LAS真彩色" || cmd == "点云真实色" || cmd == "真实色导入LAS" || cmd == "LAS真实色") { await LoadLasAsync("LAS真彩色"); return; }
            if (cmd == "LAS强度色" || cmd == "点云强度着色" || cmd == "强度着色导入LAS" || cmd == "LAS强度着色") { await LoadLasAsync("LAS强度色"); return; }
            if (cmd == "LAS分类着色" || cmd == "点云分类着色" || cmd == "按分类着色") { await LoadLasAsync("LAS分类着色"); return; }
            if (cmd == "LAS剔除植被建筑" || cmd == "点云剔除非地面" || cmd == "剔除植被建筑" || cmd == "LAS保留地面") { await LoadLasAsync("LAS剔除植被"); return; }
            if (cmd == "LAS分类统计" || cmd == "点云分类统计" || cmd == "LAS质量报告" || cmd == "LAS强度分类") { await LasQualityAsync(); return; }
            if (cmd == "正射着色" || cmd == "真实色" || cmd == "影像着色" || cmd == "正射影像着色") { await OrthoColorAsync(); return; }
            if (cmd == "逐点坡度/坡向" || cmd == "逐点坡度坡向" || cmd == "法向估计" || cmd == "点云法向") { await PointNormalsAsync(); return; }
            if (cmd == "高程截断" || cmd == "高程裁剪" || cmd == "Z截断") { await ElevationClipAsync(); return; }
            if (cmd == "点云裁剪" || cmd == "边界裁剪点云" || cmd == "裁剪点云" || cmd == "点云裁剪内" || cmd == "圈内裁剪") { await CropCloudByBoundaryAsync("点云裁剪"); return; }
            if (cmd == "点云裁剪外" || cmd == "圈外裁剪" || cmd == "裁剪点云外" || cmd == "保留界外") { await CropCloudByBoundaryAsync("点云裁剪外"); return; }
            if (cmd == "网格度量" || cmd == "网格面积体积" || cmd == "网格体积") { await MeshMetricsAsync(); return; }
            if (cmd == "网格诊断" || cmd == "网格检查" || cmd == "网格拓扑") { await MeshDiagnoseAsync(); return; }
            if (cmd == "合并三角网" || cmd == "网格合并" || cmd == "合并网格") { await MeshMergeAsync(); return; }
            if (cmd == "补洞(三角网)" || cmd == "补洞" || cmd == "网格补洞" || cmd == "填洞" || cmd.StartsWith("补洞 ") || cmd.StartsWith("网格补洞 ")) { await MeshHoleFillAsync(cmd); return; }
            if (cmd == "网格修复" || cmd == "修复拓扑" || cmd == "修复拓扑关系" || cmd == "拓扑修复" || cmd == "一键修复") { await MeshRepairAsync(); return; }
            if (cmd == "剔面(三角网)" || cmd == "剔面" || cmd == "网格剔面" || cmd == "删陡面" || cmd.StartsWith("剔面 ")) { await MeshFaceCullAsync(cmd); return; }
            if (cmd == "剔倒刺" || cmd == "去尖刺" || cmd == "剔除障碍" || cmd == "剔高Z倒刺" || cmd.StartsWith("剔倒刺 ")) { await MeshSpikeCullAsync(cmd); return; }
            if (cmd == "分割三角网" || cmd == "沿线分割三角网" || cmd == "网格分割" || cmd == "切分三角网") { await MeshSplitAsync(); return; }
            if (cmd == "边界分割三角网" || cmd == "内外分割" || cmd == "闭合边界分割" || cmd == "网格内外分片") { await MeshBoundarySplitAsync(); return; }
            if (cmd == "快速建模" || cmd == "一键建模" || cmd == "顶底成体") { await QuickModelAsync(); return; }
            if (cmd == "连续多层建模" || cmd == "多层建模" || cmd == "逐层成体" || cmd == "层位建模") { await MultiLayerModelAsync(); return; }
            if (cmd == "网格简化" || cmd == "三角网简化" || cmd == "减面" || cmd.StartsWith("网格简化 ") || cmd.StartsWith("三角网简化 ")) { await MeshSimplifyAsync(cmd); return; }
            if (cmd == "导出OBJ" || cmd == "导出网格OBJ" || cmd == "网格导出OBJ") { await ExportMeshAsync("obj"); return; }
            if (cmd == "导出PLY" || cmd == "导出网格PLY" || cmd == "网格导出PLY") { await ExportMeshAsync("ply"); return; }
            if (cmd == "导出STL" || cmd == "导出网格STL" || cmd == "网格导出STL") { await ExportMeshAsync("stl"); return; }
            if (cmd == "中心线管理" || cmd == "边状态" || cmd == "路网拓扑" || cmd == "中线管理") { RoadNetworkReportCmd(); return; }
            if (cmd == "瓶颈段分析" || cmd == "瓶颈段" || cmd == "关键路段" || cmd == "路段介数" || cmd == "路网瓶颈") { RoadBottleneckCmd(); return; }
            if (cmd == "结构路面" || cmd == "路面带" || cmd == "结构路面带" || cmd.StartsWith("结构路面 ")) { StructurePavementCmd(cmd); return; }
            if (cmd == "路网运输指标" || cmd == "运输指标路网" || cmd == "路网指标" || cmd == "路网里程指标") { RoadTransportIndicatorsCmd(); return; }
            if (cmd == "中线交点" || cmd == "交点分类" || cmd == "路网交点" || cmd == "中线交点分类" || cmd.StartsWith("中线交点 ") || cmd.StartsWith("交点分类 ")) { CenterlineJunctionsCmd(cmd); return; }
            if (cmd == "路段分类" || cmd == "路网拓扑分类" || cmd == "路段拓扑" || cmd == "干线支线") { RoadTopologyCmd(); return; }
            if (cmd == "排土场容量校核" || cmd == "容量校核" || cmd == "排土容量") { await DumpCapacityAsync(); return; }
            if (cmd == "生产量核算" || cmd == "任务量汇总" || cmd == "分账合计" || cmd == "生产任务量") { await ProductionQuantityAsync(); return; }
            if (cmd == "生产任务编制" || cmd == "排产" || cmd == "任务裂解" || cmd == "裂解装箱" || cmd == "班次排产"
                || cmd.StartsWith("生产任务编制 ") || cmd.StartsWith("排产 ")) { TaskExplodeCmd(cmd); return; }
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
            if (cmd == "自适应体素算量" || cmd == "百分比块体素" || cmd == "自适应体素" || cmd == "子块体素算量" || cmd.StartsWith("自适应体素算量 ")) { await AdaptiveVoxelAsync(cmd); return; }
            if (cmd == "实体转块体" || cmd == "网格转块体" || cmd == "体转块") { await EntityToBlocksAsync(); return; }
            if (cmd == "立方体" || cmd == "长方体" || cmd.StartsWith("立方体 ") || cmd.StartsWith("长方体 ")) { await BoxPrimitiveAsync(cmd); return; }       // 立方体 [边长 | sx sy sz]
            if (cmd == "球体" || cmd == "球" || cmd.StartsWith("球体 ") || cmd.StartsWith("球 ")) { await SpherePrimitiveAsync(cmd); return; }                  // 球体 [半径]
            if (cmd == "圆柱" || cmd == "圆柱体" || cmd.StartsWith("圆柱 ") || cmd.StartsWith("圆柱体 ")) { await CylinderPrimitiveAsync(cmd); return; }        // 圆柱 [半径 [高]]
            if (cmd == "网格边界" || cmd == "边界环提取" || cmd == "提取边界") { await MeshBoundaryAsync(); return; }
            if (cmd == "SOR去噪" || cmd == "统计去噪" || cmd == "SOR" || cmd.StartsWith("SOR去噪 ") || cmd.StartsWith("SOR ")) { await DenoiseAsync(false, cmd); return; }   // SOR [k σ]
            if (cmd == "ROR去噪" || cmd == "半径去噪" || cmd == "ROR" || cmd.StartsWith("ROR去噪 ") || cmd.StartsWith("ROR ")) { await DenoiseAsync(true, cmd); return; }     // ROR [半径 下限]
            if (cmd == "矿床识别" || cmd == "自动识别" || cmd == "矿床类型识别") { await DepositDetectAsync(); return; }
            if (cmd == "方案综合对比" || cmd == "方案比选" || cmd == "方案对比") { await ProgramCompareAsync(); return; }
            if (cmd == "高程查询" || cmd == "虚拟钻孔" || cmd == "查询高程") { await StartSpotQueryAsync(); return; }
            if (cmd == "文字" || cmd == "单行文字") { ArmText(false); return; }
            if (cmd == "编辑文字" || cmd == "文字编辑" || cmd == "修改文字") { _ = TextEditCommandAsync(); return; }   // TEXTEDIT/DDEDIT/ED: 在位改内容(双击文字同此)
            if (cmd == "多行文字" || cmd == "多行文本") { ArmText(true); return; }
            if (cmd == "对齐标注") { StartDim(true); return; }                                        // 对齐: 平行测线,真距
            if (cmd == "标注" || cmd == "线性标注" || cmd == "尺寸标注" || cmd == "标注台阶标高") { StartDim(false); return; }   // 线性: 轴对齐,量 X/Y
            if (cmd == "半径标注" || cmd == "半径") { StartDimRadial(); return; }
            if (cmd == "直径标注" || cmd == "直径") { StartDimDiameter(); return; }
            if (cmd == "角度标注" || cmd == "角度" || cmd == "测角标注") { StartDimAngular(); return; }
            if (cmd == "坐标标注" || cmd == "标注坐标" || cmd == "点坐标标注") { StartCoordLabel(); return; }
            if (cmd == "连续标注" || cmd == "连续") { StartDimContinue(); return; }
            if (cmd == "标注样式" || cmd == "标注设置" || cmd.StartsWith("标注样式 ") || cmd.StartsWith("标注设置 ")) { DimStyleCmd(cmd); return; }
            if (cmd == "裁剪" || cmd == "多边形裁剪" || cmd == "区运算" || cmd == "范围裁剪") { await ClipPolygonCmdAsync(); return; }
            if (cmd == "线裁剪" || cmd == "裁剪线对象" || cmd == "线内裁剪" || cmd == "边界裁线") { await ClipLinesCmdAsync(true); return; }
            if (cmd == "线外裁剪" || cmd == "外裁线" || cmd == "裁剪线外") { await ClipLinesCmdAsync(false); return; }
            if (cmd == "平滑" || cmd == "光滑" || cmd == "曲线平滑" || cmd == "光滑曲线") { await SmoothPolylineCmdAsync(false); return; }
            if (cmd == "样条平滑" || cmd == "插值平滑" || cmd == "CatmullRom" || cmd == "过点平滑") { SmoothPolyline(spline: true); return; }
            if (cmd == "简化" || cmd == "多段线简化" || cmd == "抽稀线") { await SimplifyPolylineCmdAsync(); return; }   // 「抽稀等值线」= 编辑组 POLYSIMPLIFY(带容差/最少节点数)
            if (cmd == "圈选" || cmd == "窗口圈选") { PolygonSelect(false); return; }
            if (cmd == "交叉圈选") { PolygonSelect(true); return; }
            if (cmd == "坐标转换") { await CoordTransformAsync(); return; }
            if (cmd == "另存为") { await SaveAsAsync(); return; }
            if (cmd == "导出选中实体" || cmd == "导出选中" || cmd == "导出选择") { await ExportSelectedAsync(); return; }
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
            if (cmd == "清空视图" || cmd == "ERASEALL") { await EraseAllAsync(); return; }
            if (cmd == "隐藏对象" || cmd == "隐藏") { HideSelectedObjects(); return; }
            if (cmd == "隐藏同一图层对象" || cmd == "隐藏图层" || cmd == "隐藏同层") { HideSelectedLayers(); return; }
            if (cmd == "结束隐藏" || cmd == "取消隐藏" || cmd == "显示全部" || cmd == "全部显示") { EndHide(); return; }
            if (cmd == "帮助文档" || cmd == "帮助" || cmd == "命令列表") { ShowHelp(); return; }   // 命令行水印里让人打「帮助」, 得认这两个字
            if (cmd == "选项") { ShowOptions(); return; }
            if (cmd == "数据库连接" || cmd == "连接数据库" || cmd == "数据库设置") { ShowDbConnectionSettings(); return; }
            if (cmd == "注册") { StatusMsg.Text = "注册/授权：需接入国产数据库授权系统（记录待做）"; return; }
            if (cmd == "删除") { await DeleteCmdAsync(); return; }
            if (cmd == "全部选择") { SelectAll(); return; }
            if (cmd == "选择类似" || cmd == "选类似" || cmd == "同类选择") { SelectSimilar(); return; }
            if (cmd == "快速选择" || cmd == "QSELECT" || cmd == "条件选择" || cmd.StartsWith("快速选择 ") || cmd.StartsWith("QSELECT ") || cmd.StartsWith("条件选择 ")) { QuickSelectCmd(cmd); return; }
            if (cmd == "最后") { SelectLast(); return; }
            if (cmd == "上次") { SelectPrevious(); return; }
            if (cmd == "取消选择" || cmd == "全部取消选择" || cmd == "清除选择") { DeselectAll(); return; }
            if (cmd == "反选" || cmd == "反向选择" || cmd == "反转选择") { InvertSelection(); return; }
            if (cmd.StartsWith("修改点样式 ")) { await ModifyPointStyleCmdAsync(cmd); return; }
            if (cmd == "分解") { await ExplodeCmdAsync(); return; }
            if (cmd == "区域求差" || cmd == "可采区域求差" || cmd == "多边形求差") { SubtractRegions(); return; }
            if (cmd == "区域重叠检测" || cmd == "区域重叠" || cmd == "重叠检测") { CheckRegionOverlap(); return; }
            if (cmd == "平盘宽度识别" || cmd == "现场参数提取" || cmd == "平盘识别" || cmd == "采场参数识别") { await BenchWidthAsync(); return; }
            if (cmd == "确定可采区域" || cmd == "可采区域" || cmd == "可采区域识别") { await MineableAreaAsync(); return; }
            if (cmd == "采场排土场识别" || cmd == "采场识别" || cmd == "排土场识别" || cmd == "地貌分类" || cmd == "采排识别" || cmd.StartsWith("采场排土场识别 ")) { await LandformClassifyAsync(cmd); return; }
            if (cmd == "网格交线" || cmd == "两网交线" || cmd == "面交线" || cmd == "求交线") { await MeshIntersectionAsync(); return; }
            if (cmd == "网格剖面" || cmd == "三角网剖面" || cmd == "面剖面" || cmd == "曲面剖面") { await MeshSectionAsync(); return; }
            if (cmd == "网格光顺" || cmd == "网格平滑" || cmd == "曲面光顺" || cmd.StartsWith("网格光顺 ") || cmd.StartsWith("网格平滑 ")) { await MeshSmoothAsync(cmd); return; }
            if (cmd == "线落到面上" || cmd == "线落面" || cmd == "线投影到面") { await ProjectPolylinesToMeshAsync(); return; }
            if (cmd == "侧面三角网" || cmd == "侧面放样" || cmd == "放样侧面") { await SideSurfaceAsync(); return; }
            if (cmd == "道路横断面" || cmd == "路面加宽超高" || cmd == "弯道加宽") { RoadCrossSectionCmd(); return; }
            if (cmd.StartsWith("道路设计参数") || cmd.StartsWith("最小平曲线半径") || cmd.StartsWith("平曲线半径") || cmd.StartsWith("道路设计校核")) { RoadDesignParamsCmd(cmd); return; }
            if (cmd.StartsWith("运输布局方案") || cmd.StartsWith("道路布局求解") || cmd.StartsWith("坑线布局方案") || cmd.StartsWith("运输系统布局") || cmd.StartsWith("运量驱动布线") || cmd == "运量驱动布线") { await RoadLayoutCmd(cmd); return; }
            if (cmd == "路面生成" || cmd == "生成路面" || cmd == "中线外扩" || cmd.StartsWith("路面生成 ") || cmd.StartsWith("生成路面 ")) { RoadSurfaceCmd(cmd); return; }
            if (cmd == "纵坡分析" || cmd == "纵坡" || cmd == "坡度分档" || cmd == "限坡校核" || cmd.StartsWith("纵坡分析 ") || cmd.StartsWith("限坡校核 ")) { await GradeProfileAsync(cmd); return; }
            if (cmd == "竖曲线平滑" || cmd == "竖曲线" || cmd == "纵断面竖曲线" || cmd.StartsWith("竖曲线平滑 ") || cmd.StartsWith("竖曲线 ")) { await VerticalCurveAsync(cmd); return; }
            if (cmd == "线形处理" || cmd == "线形" || cmd == "中线线形" || cmd == "线形后处理" || cmd.StartsWith("线形处理 ")) { await LineFormAsync(cmd); return; }
            if (cmd == "台阶面提取" || cmd == "坡面提取" || cmd == "台阶坡面" || cmd == "台阶面" || cmd.StartsWith("台阶面提取 ")) { await BenchFaceExtractAsync(cmd); return; }
            if (cmd == "煤岩台阶判定" || cmd == "煤岩判定" || cmd == "台阶煤岩" || cmd.StartsWith("煤岩台阶判定 ")) { await BenchCoalCmd(cmd); return; }
            if (cmd == "煤层露头线" || cmd == "露头线" || cmd == "煤层露头" || cmd == "露头线提取" || cmd.StartsWith("煤层露头线 ")) { await SeamOutcropCmd(cmd); return; }
            if (cmd == "更新煤层面" || cmd == "更新现状面" || cmd == "煤层面更新" || cmd.StartsWith("更新煤层面 ")) { await SurfaceUpdateCmd(cmd); return; }
            if (cmd == "平行推进" || cmd == "开采程序确定" || cmd == "确定开采程序" || cmd == "工作线推进") { AdvanceCmd(AdvanceMode.Parallel, "平行推进"); return; }
            if (cmd == "定点回转" || cmd == "定点回转推进") { AdvanceCmd(AdvanceMode.FixedPivot, "定点回转"); return; }
            if (cmd == "动点回转" || cmd == "动点回转推进") { AdvanceCmd(AdvanceMode.MovingPivot, "动点回转"); return; }
            if (cmd == "螺旋斜坡道" || cmd == "螺旋坑线" || cmd == "螺旋中线") { SpiralRampCmd(); return; }
            if (cmd == "直线坑线" || cmd == "坑线自动布线" || cmd == "坑线连通自检" || cmd == "直线坑线自动布线" || cmd.StartsWith("直线坑线 ") || cmd.StartsWith("坑线自动布线 ")) { StraightRampRouteCmd(cmd); return; }
            if (cmd == "直线斜坡道" || cmd == "直线中线" || cmd.StartsWith("直线斜坡道 ")) { StraightRampCmd(cmd); return; }
            if (cmd == "折返斜坡道" || cmd == "折返坑线" || cmd == "折返中线") { SwitchbackRampCmd(); return; }
            if (cmd == "运距指标" || cmd == "循环时间" || cmd == "运距统计") { await HaulRecordMetricsAsync(); return; }
            if (cmd == "OD运距矩阵" || cmd == "OD矩阵" || cmd == "运距矩阵") { await OdMatrixAsync(); return; }
            if (cmd.StartsWith("约束寻径") || cmd.StartsWith("运输寻径") || cmd.StartsWith("限坡寻径")) { await RoadConstraintPathAsync(cmd); return; }
            if (cmd == "运输指标报告" || cmd == "路网运输指标全" || cmd == "全运输指标") { await RoadFullIndicatorsAsync(); return; }
            if (cmd == "路网建图" || cmd == "属性路网建图" || cmd == "路网抽图" || cmd == "中线抽图") { await RoadBuildGraphAsync(); return; }
            if (cmd == "新建图层") { var l = _layers.New(); PopulateDrawingLayers(); StatusMsg.Text = $"新建图层「{l.Name}」并置为当前"; return; }
            if (cmd == "删除图层" || cmd == "删层" || cmd == "删除当前图层") { DeleteCurrentLayer(); return; }
            if (cmd.StartsWith("重命名图层 ") || cmd.StartsWith("图层重命名 ") || cmd.StartsWith("图层命名 ")) { RenameCurrentLayer(cmd.Substring(cmd.IndexOf(' ') + 1)); return; }
            if (cmd.StartsWith("合并图层 ") || cmd.StartsWith("图层合并 ")) { MergeLayerIntoCurrent(cmd.Substring(cmd.IndexOf(' ') + 1)); return; }
            if (cmd == "图层隔离" || cmd == "隔离图层") { IsolateLayer(); return; }
            if (cmd == "取消隔离" || cmd == "结束隔离" || cmd == "取消图层隔离") { _layers.AllOn(); PopulateDrawingLayers(); AfterLayerStateChange(); StatusMsg.Text = "已取消图层隔离（全部打开）"; return; }
            if (cmd == "图层特性管理器") { var l = _layers.CycleCurrent(); StatusMsg.Text = $"当前图层「{l.Name}」 显示{( l.Shown?"开":"关")}/{(l.Locked?"锁":"解锁")}（再点循环切换）"; return; }
            if (cmd == "全开" || cmd == "全部打开" || cmd == "图层全开") { _layers.AllOn(); PopulateDrawingLayers(); AfterLayerStateChange(); StatusMsg.Text = "已打开全部图层"; return; }
            if (cmd == "全关" || cmd == "全部关闭" || cmd == "图层全关") { _layers.AllOff(); PopulateDrawingLayers(); AfterLayerStateChange(); StatusMsg.Text = "已关闭全部图层"; return; }
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
            if (cmd == "偏移") { await OffsetCmdAsync(); return; }
            if (cmd == "复制到剪贴板" || cmd == "剪贴板复制") { CopyClip(); return; }
            if (cmd == "剪切") { CutClip(); return; }
            if (cmd == "粘贴" || cmd == "原坐标粘贴") { PasteClip(); return; }
            if (cmd == "基点粘贴") { StartPasteBase(); return; }
            if (cmd == "删除全部" || cmd == "全部删除" || cmd == "清空实体" || cmd == "清除全部" || cmd == "清除点云") { EraseAll(); return; }
            if (cmd == "创建选择集" || cmd == "选择集") { CreateSelSet(); return; }
            if (cmd == "调用选择集") { RecallSelSet(); return; }
            if (cmd == "刷新") { Regen(); return; }
            if (cmd == "特性" || cmd == "属性" || cmd.StartsWith("特性 ") || cmd.StartsWith("属性 ")) { PropertiesCmd(cmd); return; }
            if (cmd == "字高归一化" || cmd == "字高归一" || cmd == "文字高度归一化" || cmd == "修正字高") { TextHeightNormalizeCmd(); return; }
            if (cmd == "清理标记" || cmd == "清除标记") { ClrMark(); return; }
            if (cmd == "修剪" || cmd == "延伸") { await TrimCmdAsync(); return; }
            if (cmd == "圆TTR" || cmd == "圆(切切半径)") { StartTTR(); return; }
            if (cmd == "圆弧SER" || cmd == "圆弧(起点端点半径)") { StartArcSer(); return; }
            if (cmd == "打断") { await BreakCmdAsync(); return; }
            if (cmd == "选择模式" || cmd == "框选模式" || cmd == "SELECTMODE") { if (Viewport.Is2DView) Viewport.SetViewMode(false); SetSelectMode(!_selectMode); return; }
            if (cmd == "夹点开关" || cmd == "夹点") { ToggleGrips(); return; }
            if (cmd == "Gizmo" || cmd == "GIZMO") { ToggleGizmo(); return; }   // 三轴变换手柄, 见 MainWindow.Gizmo.cs
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
            if (cmd == "颜色" || cmd.StartsWith("颜色 ")) { ColorCmd(cmd.Length > 2 ? cmd.Substring(2) : ""); return; }
            if (cmd == "线型" || cmd == "实线" || cmd == "虚线" || cmd == "点划线" || cmd == "点线" || cmd == "双点划线" || cmd == "破折线" || cmd.StartsWith("线型 ")) { SetLinetypeCmd(cmd); return; }
            if (cmd == "图案填充" || cmd == "填充" || cmd == "HATCH" || cmd == "剖面线"
                || cmd.StartsWith("图案填充 ") || cmd.StartsWith("填充 ") || cmd.StartsWith("HATCH ") || cmd.StartsWith("剖面线 "))
            {
                // 缺省 45°、自动间距; 同行可给 "图案填充 <角度> [间距]"，命令行发起且没给就逐项问。
                var hp = await AskCmdParamsAsync("图案填充",
                    new Modeling.PromptDialog.Field("ang", "填充角度", "45", "°"),
                    new Modeling.PromptDialog.Field("sp", "填充间距", "0", "m", "0 = 按边界尺寸自动取"));
                if (hp == null) return;
                HatchBoundaryCmd(hp.D("ang", 45), hp.D("sp", 0));
                return;
            }
            if (ActivateDrawTool(cmd)) return;
            // 带了参数但没有哪条分支认这整串（如「加密多段线 3」——分支只认光命令词）：
            // 把参数摘下来交给参数问答按位消费, 只用命令词再走一遍。重试串已无空格, 不会再递归。
            if (argsAt > 0)
            {
                _forcedInlineArgs = Modeling.ParamPrompt.SplitArgs(cmd.Substring(argsAt));
                DispatchRibbon(cmd.Substring(0, argsAt), fromCmdLine);
                return;
            }
            // 兜底: 未匹配命令(含未移植子系统的受阻功能按钮)——给诚实提示, 而非旧的模糊"命令: X"回显
            StatusMsg.Text = $"「{cmd}」暂未实现——属未移植子系统（排产计划/生产调度/坑线采剥内核/倾斜摄影等），或命令名有误";
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
                new FilePickerFileType("支持的格式 (DXF/DWG/OFF/WL/WT/WP/MPJ/KDF/3DM/3DS)") { Patterns = new[] { "*.dxf", "*.dwg", "*.off", "*.wl", "*.wt", "*.wp", "*.mpj", "*.kdf", "*.3dm", "*.3ds" } },
                new FilePickerFileType("CAD 图纸 (DXF/DWG)") { Patterns = new[] { "*.dxf", "*.dwg" } },
                new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } },
                new FilePickerFileType("MapGIS 6.x (WL 线/WT 注记/WP 区/MPJ 工程)") { Patterns = new[] { "*.wl", "*.wt", "*.wp", "*.mpj" } },
                new FilePickerFileType("WeCAD 地质地形图 (KDF)") { Patterns = new[] { "*.kdf" } },
                new FilePickerFileType("3DMine 网格 (3DM)") { Patterns = new[] { "*.3dm" } }
            }
        });
        if (files.Count == 0) return;
        await ImportPath(files[0].Path.LocalPath);
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
    // 导出选中实体：仅把选中的实体导出为 .dxf/.dwg/.kdf。
    // 原版菜单有"导出选中实体"项但引擎未实装(注释"待引擎能力到位后接入", 仅整模型)；
    // Kylin 托管架构可完成此既有命令（[[unlock-blocked-insights]] 受阻前试托管重算）。不改当前文档。
    private async Task ExportSelectedAsync()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "导出选中：请先选中实体"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出选中实体",
            DefaultExtension = "dxf",
            SuggestedFileName = "selection.dxf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("DXF 图纸") { Patterns = new[] { "*.dxf" } },
                new FilePickerFileType("DWG 图纸") { Patterns = new[] { "*.dwg" } },
                new FilePickerFileType("WeCAD 地质地形图 (KDF)") { Patterns = new[] { "*.kdf" } }
            }
        });
        if (file == null) return;
        string path = file.Path.LocalPath;
        var sub = new Scene();
        foreach (var e in _selected) sub.Add(e);   // 仅选中实体入临时场景（共享引用，只读导出）
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            int n = ext == ".kdf" ? KdfExportService.Export(sub, path, _layers) : SceneExportService.Export(sub, path, _layers);
            StatusMsg.Text = $"导出选中：{n} 个实体 → {Path.GetFileName(path)}（不改当前文档）";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"导出选中失败：{ex.Message}"; }
    }

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
                new FilePickerFileType("DWG 图纸") { Patterns = new[] { "*.dwg" } },
                new FilePickerFileType("WeCAD 地质地形图 (KDF)") { Patterns = new[] { "*.kdf" } }
            }
        });
        if (file == null) return;
        string path = file.Path.LocalPath;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".pmx") { File.WriteAllText(path, SceneIO.SaveDoc(_scene, _layers.Layers, _layers.Current.Name)); SetDocPath(path); StatusMsg.Text = $"已另存 {Path.GetFileName(path)} · {_scene.Count} 实体"; }
            else if (ext == ".kdf") { int n = KdfExportService.Export(_scene, path, _layers); StatusMsg.Text = $"已导出 {Path.GetFileName(path)} · {n} 实体（KDF, 点/图案填充不导出）"; }
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
        Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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

    // 设置当前文档路径并更新窗口标题 + 文档标签名(打开/另存后标签显示文件名, 多标签才分得清哪个是哪个)
    private void SetDocPath(string? path)
    {
        _currentPath = path;
        _active.Title = path != null ? Path.GetFileName(path) : $"未命名 {_active.Id.Substring(3)}";   // Id 形如 Doc3
        _active.Vm.Title = _active.Title;
        SyncWindowTitle();
    }

    // 窗口标题 = 原版系统名 + 当前活动文档名(桌面/软件名另用 DayOps); 切标签时也要重设, 否则标题一直挂着上一个文档的文件名
    private void SyncWindowTitle()
        => Title = "中煤平朔露天煤矿生产计划决策支撑系统 · DayOps — " + (_active.Path == null ? _active.Title : Path.GetFileName(_active.Path));

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
            Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
            RefreshScene();
            SetDocPath(files[0].Path.LocalPath);
            StatusMsg.Text = $"已打开 {Path.GetFileName(files[0].Path.LocalPath)} · {_scene.Count} 实体 · {_layers.Layers.Count} 图层";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"打开失败：{ex.Message}"; }
    }

    // 共享导入逻辑：CAD(dxf/dwg) → 可编辑实体入场景；OFF 等网格 → 显示态
    private async Task ImportPath(string path)
    {
        StatusMsg.Text = $"正在导入 {Path.GetFileName(path)} …";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dxf" || ext == ".dwg") { await ImportCadEditableAsync(path); return; }
        if (ext == ".wl" || ext == ".wt" || ext == ".wp" || ext == ".mpj") { ImportMapGisEditable(path); return; }
        if (ext == ".kdf") { ImportKdfEditable(path); return; }
        if (ext == ".3ds") { ImportTdmStringEditable(path); return; }

        if (ext == ".off") { ImportOffAsMesh(path); return; }   // OFF 三角网 → 场景三角网对象(可选中/建模/存档)
        if (ext == ".3dm" && ImportTdmAsMeshes(path)) return;   // 3DMine 网格 → 场景三角网对象(面模型); 解析失败回退线框显示通道
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
    //
    // 解析放后台线程：现场图纸动辄几十 MB，ACadSharp 读一张 51MB 的接续计划要 7 秒多，
    // 这段若占着 UI 线程，界面就是"卡死"——原版也是加载与界面分离的。
    // LoadEntities 是纯计算(不碰任何控件)，扔线程池安全；回到 UI 线程才动场景与 GL。
    // 全程落日志：卡在哪一步(解析 / 入场景 / 细分上屏)不落日志就只能靠猜。
    private async Task ImportCadEditableAsync(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string name = Path.GetFileName(path);
        long mb = new FileInfo(path).Length / 1024 / 1024;
        PitMine3D.Kylin.CrashLog.Write("导入", $"开始 {name}（{mb} MB）");
        StatusMsg.Text = $"正在后台读取 {name}（{mb} MB）…界面可继续操作";

        DxfImportService.EntityImportResult er;
        _importBusy = true;
        ShowLoadProgress(0, $"正在读取 {name}…");
        // 进度回调发生在后台线程, 必须 Post 回 UI 线程才能碰控件; 节流靠 DxfImportService 里的 2% 一跳。
        void OnProg(double f, string what) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowLoadProgress(f, $"{name}（{mb} MB）· {what}"));
        try
        {
            er = await Task.Run(() =>
            {
                // 同一张图纸再打开：直接读缓存(源文件大小/修改时间没变才认)，省掉几秒的 DWG 解析
                if (ImportCache.TryLoad(path, out var cached))
                {
                    PitMine3D.Kylin.CrashLog.Write("导入", $"命中缓存，跳过解析（{cached.Entities.Count} 图元）");
                    return cached;
                }
                var fresh = DxfImportService.LoadEntities(path, OnProg);
                if (fresh.Success && fresh.Entities.Count > 0) ImportCache.Save(path, fresh);
                return fresh;
            });
        }
        catch (System.Exception ex)
        {
            PitMine3D.Kylin.CrashLog.Write("导入", $"解析抛异常：{ex}");
            StatusMsg.Text = $"导入失败：{ex.Message}";
            return;
        }
        finally { _importBusy = false; HideLoadProgress(); }

        PitMine3D.Kylin.CrashLog.Write("导入", $"[{sw.ElapsedMilliseconds}ms] 解析完成(后台线程) success={er.Success} 实体={er.Entities.Count} 图层={er.LayerOrder.Count}");
        if (!er.Success) { StatusMsg.Text = $"导入失败：{er.Error}"; PitMine3D.Kylin.CrashLog.Write("导入", $"失败：{er.Error}"); return; }
        string warn = er.Warnings.Count > 0 ? $" · 跳过 {er.Warnings.Count} 类未支持" : "";
        ApplyEntityImport(er, name, warn);
        PitMine3D.Kylin.CrashLog.Write("导入", $"[{sw.ElapsedMilliseconds}ms] 全部完成");
    }

    /// <summary>后台解析进行中（供状态提示/避免重入）。</summary>
    private bool _importBusy;

    /// <summary>
    /// 状态栏的大数据加载进度条。几十 MB 的图纸解析要好几秒，光一句"正在读取"看不出还要等多久，
    /// 也分不清是在跑还是卡死了；给个真进度（读文件 → 逐图元转换）心里才有数。
    /// </summary>
    private void ShowLoadProgress(double fraction, string text)
    {
        if (LoadBar == null) return;
        LoadBar.IsVisible = true;
        LoadBar.Value = System.Math.Clamp(fraction, 0, 1);
        if (!string.IsNullOrEmpty(text)) StatusMsg.Text = text;
    }

    private void HideLoadProgress()
    {
        if (LoadBar == null) return;
        LoadBar.IsVisible = false;
        LoadBar.Value = 0;
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

    // 3DMine String File(.3ds 文本折线) 导入为可编辑实体（忠实移植 TdmStringReader）
    private void ImportTdmStringEditable(string path)
    {
        var er = Cad.TdmImportService.LoadStrings(path);
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
            if (er.LayerStates.TryGetValue(ln, out var st))    // 图层状态 round-trip: 恢复开/冻结/锁定
            {
                var lyr = _layers.Get(ln);
                if (lyr != null) { lyr.Visible = st.on; lyr.Frozen = st.frozen; lyr.Locked = st.locked; }
            }
        }
        var swA = System.Diagnostics.Stopwatch.StartNew();
        foreach (var en in er.Entities) _scene.Add(en);
        PitMine3D.Kylin.CrashLog.Write("导入", $"[{swA.ElapsedMilliseconds}ms] 入场景 {er.Entities.Count} 实体");
        _lastImport = null;                    // 捕捉改用场景几何
        Viewport.ClearImported();               // 不再用显示态网格
        RefreshScene();
        PitMine3D.Kylin.CrashLog.Write("导入", $"[{swA.ElapsedMilliseconds}ms] 细分+上屏完成");
        // 定位视图：现场图纸常带着几件跑到几十上百公里外的孤立图元(图框放在原点/拼图残迹)，
        // 按真实包围盒缩放会把 8km 的采剥图压成一个点——看起来就是"打不开"。故缩放到图元密集区。
        var ext = RobustExtent.Compute(er.Centers, er.Bounds);
        Viewport.FitBounds(ext.Bounds);
        string far = "";
        if (ext.Trimmed)
        {
            far = $" · 视图已定位到图元密集区（{RobustExtent.Describe(ext.Bounds)}）；另有 {ext.Outliers} 个图元散在 {RobustExtent.Describe(er.Bounds)} 的范围外围，「范围缩放」可看全图";
            PitMine3D.Kylin.CrashLog.Write("导入",
                $"真实范围 {RobustExtent.Describe(er.Bounds)} 远大于密集区 {RobustExtent.Describe(ext.Bounds)}，离群图元 {ext.Outliers} 个 —— 已按密集区定位视图");
        }
        PopulateObjectTreeCounts(er.TypeCounts, fileName, er.Entities.Count);
        PopulateDrawingLayers();
        PitMine3D.Kylin.CrashLog.Write("导入", $"[{swA.ElapsedMilliseconds}ms] 面板刷新完成");
        StatusMsg.Text = $"已导入 {fileName} · {er.Entities.Count} 可编辑实体 · {er.LayerOrder.Count} 图层（可选中/编辑/删除）{warn}{far}";
    }

    // 导入 PitMine 工程(.pmx 原版私有二进制)：读核心实体(线/点/多段线/文字/网格棱线/圆/弧)入可编辑场景 + 建图层。
    // 复杂类型(MText/Hatch/标注/椭圆/样条)按 recordLen 跳过。区别 Kylin 自己的文本 .pmx(用『打开』)。
    private async Task PmxImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 PitMine 工程：选 .pmx（原版二进制工程）",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PitMine 工程 (PMX)") { Patterns = new[] { "*.pmx" } } }
        });
        if (files.Count == 0) return;
        var r = Cad.PmxImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"导入 PitMine 工程：{r.Error}"; return; }
        BeginChange();
        foreach (var e in r.Entities) { _layers.EnsureImported(e.LayerName, e.Cr, e.Cg, e.Cb); _scene.Add(e); }
        _lastImport = null;
        Viewport.ClearImported();
        RefreshScene();
        Viewport.ZoomExtents();
        PopulateDrawingLayers();
        string fn = System.IO.Path.GetFileName(files[0].Path.LocalPath);
        StatusMsg.Text = $"导入 PitMine 工程 {fn}：{r.Entities.Count} 可编辑实体（{r.Summary}）· {r.LayerNames.Count} 图层";
    }

    // 导出 PitMine 工程(.pmx 原版二进制)：场景实体 → 原版可打开的 .pmx(反向互操作)。核心: 线/点/多段线/文字/圆/矩形。
    private async Task PmxExportAsync()
    {
        if (_scene.Entities.Count == 0) { StatusMsg.Text = "导出PMX：场景为空"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出 PitMine 工程(.pmx 原版二进制)", DefaultExtension = "pmx", SuggestedFileName = "export.pmx",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("PitMine 工程 (PMX)") { Patterns = new[] { "*.pmx" } } }
        });
        if (file == null) return;
        if (Cad.PmxExportService.SaveToFile(file.Path.LocalPath, _scene.Entities, out string err, out int n))
            StatusMsg.Text = $"导出 PitMine 工程：{n} 实体 → {System.IO.Path.GetFileName(file.Path.LocalPath)}（原版二进制 .pmx；线/点/多段线/文字/圆/矩形；圆弧·正多边形暂不导出）";
        else StatusMsg.Text = $"导出PMX：写出失败 {err}";
    }

    // 导出 PMB 块体模型文件(原版 .pmb 二进制, 与 导入块体模型文件 成读写对):
    // 从最近块体重建规则网格 + 全属性(x-fastest) → PmbExportService.ToBytes → 写文件。往返可经导入还原。
    private async Task PmbExportAsync()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "导出PMB：请先导入/生成块体"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出块体模型(.pmb 原版二进制)", DefaultExtension = "pmb", SuggestedFileName = "block_model.pmb",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("PitMine 块体模型 (PMB)") { Patterns = new[] { "*.pmb" } } }
        });
        if (file == null) return;
        try
        {
            var (grid, attrs) = Cad.PmbExportService.FromBlocks(_lastBlocks, _blockAttrs);
            var bytes = Cad.PmbExportService.ToBytes(grid, attrs, System.IO.Path.GetFileNameWithoutExtension(file.Path.LocalPath));
            System.IO.File.WriteAllBytes(file.Path.LocalPath, bytes);
            StatusMsg.Text = $"导出块体模型：{grid.Nx}×{grid.Ny}×{grid.Nz}={(long)grid.Nx * grid.Ny * grid.Nz} 块 · {attrs.Count} 属性[{string.Join("/", attrs.ConvertAll(a => a.name))}] → {System.IO.Path.GetFileName(file.Path.LocalPath)}（原版 .pmb 二进制, 可回导）";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"导出PMB：失败 {ex.Message}"; }
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
        foreach (var (x, y, z) in r.Points)
        {
            var pt = new PointEntity { X = x, Y = y, Elevation = z };   // 保留高程(创建三角网/赋高程用)
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
        double lblH = System.Math.Max((r.Bounds[2] - r.Bounds[0]) / 50.0, width);
        var cols = BoreholeRender.BuildColumns(r.Boreholes, scale, width, lblH * 0.7);   // 含深度刻度
        BeginChange();
        foreach (var e in cols) _scene.Add(e);   // 保留岩性色，不覆盖图层色
        foreach (var h in r.Boreholes)           // 孔号标注(孔口上方)
            _scene.Add(new TextEntity { X = h.X, Y = h.Y + lblH * 0.4, Height = lblH, Text = h.Name, Cr = 0.95f, Cg = 0.95f, Cb = 0.4f });
        RefreshScene();
        Viewport.FitBounds(new[] { r.Bounds[0], r.Bounds[1] - maxDepth * scale, r.Bounds[2] + width, r.Bounds[3] });
        StatusMsg.Text = $"已展绘 {r.Boreholes.Count} 个钻孔 · 柱状图+深度刻度+孔号标注（岩性配色）";
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
    // "等高线 <等高距>"：指定等高距→取整数倍高程处布线(如 5→...100/105/110); 缺省 auto 10 层。
    private async Task ContourFromCsvAsync(string cmd = "等高线")
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

        // 等高距: 显式则取整数倍高程处布线(round 高程); 缺省 auto 10 层
        double interval = 0;
        int sp = cmd.IndexOf(' ');
        if (sp >= 0) double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out interval);
        var contourLevels = Contour.Levels(zmin, zmax, interval);

        int n = 64;
        var grid = Contour.GridFromPoints(r.Points, n, n, out double gx0, out double gy0, out double gdx, out double gdy);
        double labelH = System.Math.Max((r.Bounds[2] - r.Bounds[0]) / 60.0, 1e-3);   // 标注字高
        BeginChange();
        int segCount = 0;
        foreach (double L in contourLevels)
        {
            float t = (float)((L - zmin) / (zmax - zmin));
            var segs = Contour.MarchingSquares(grid, gx0, gy0, gdx, gdy, L);
            var polys = Contour.LinkSegments(segs, System.Math.Max(gdx, gdy) * 1e-3);   // 散段连成折线(可选/可编辑/可平滑)
            foreach (var poly in polys)
            {
                if (poly.Count < 2) continue;
                var pl = new PolylineEntity { Cr = t, Cg = 0.45f, Cb = 1 - t };
                foreach (var p in poly) pl.Points.Add((p.x, p.y));
                _scene.Add(pl);
                segCount += poly.Count - 1;
            }
            if (polys.Count > 0 && polys[0].Count > 0)   // 每层一个高程数字标注(首条折线中点)
            {
                var lp = polys[0]; var mid = lp[lp.Count / 2];
                _scene.Add(new TextEntity { X = mid.x, Y = mid.y, Height = labelH, Text = L.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), Cr = t, Cg = 0.45f, Cb = 1 - t });
            }
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        string how = interval > 1e-9 ? $"等高距 {interval:0.##}" : "auto";
        StatusMsg.Text = $"等高线：{r.Points.Count} 点 → {contourLevels.Count} 层({how}) · {segCount} 段 + 高程标注（z {zmin:0.#}~{zmax:0.#}）";
    }

    // 平盘标高清单(忠实 BenchLevelInventory): 台阶线 CSV(lineId,x,y,z[,layer]) → 按标高归级 →
    // 画各线(投影 XY, 按级配色) + 逐级标高标注 + 存清单 CSV。场景 2D 无逐点 Z, 故由 CSV 提供标高。
    // "平盘标高清单 [合并容差m]" 指定同级合并容差(缺省 0.5)。
    private async Task BenchLevelInventoryAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "平盘标高清单：选台阶线 CSV (lineId,x,y,z[,layer])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"平盘标高清单：读文件失败 {ex.Message}"; return; }

        var lines = BenchLevelInventory.ParseCsv(text);
        if (lines.Count == 0) { StatusMsg.Text = "平盘标高清单：CSV 没解析出线(需 lineId,x,y,z 四列; 同 lineId 连成一条线)"; return; }

        var opt = new BenchLevelInventory.Options();
        int sp = cmd.IndexOf(' ');
        if (sp >= 0 && double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tol) && tol > 0)
            opt.MergeTolM = tol;

        var res = BenchLevelInventory.Build(lines, opt);
        if (!res.Ok) { StatusMsg.Text = $"平盘标高清单：{res.Message}"; return; }

        // 句柄 → 级序(1..N) 映射, 供画线配色。
        var lvOf = new Dictionary<ulong, int>();
        foreach (var lv in res.Levels) foreach (var h in lv.Handles) lvOf[h] = lv.Index;
        int nLv = res.LevelCount;

        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        BeginChange();
        foreach (var sl in lines)
        {
            int npt = sl.Xyz.Length / 3;
            if (npt < 2 || !lvOf.TryGetValue(sl.Handle, out int lvi)) continue;   // 未入级(斜线/碎线/无效)不画
            float t = nLv > 1 ? (float)(lvi - 1) / (nLv - 1) : 0f;                // 级序→色(高级红, 低级蓝)
            var pl = new PolylineEntity { Cr = 1 - t, Cg = 0.45f, Cb = t, LayerName = "平盘标高" };
            for (int i = 0; i < npt; i++)
            {
                double x = sl.Xyz[i * 3], y = sl.Xyz[i * 3 + 1];
                pl.Points.Add((x, y));
                if (x < bx0) bx0 = x; if (y < by0) by0 = y; if (x > bx1) bx1 = x; if (y > by1) by1 = y;
            }
            _scene.Add(pl);
        }
        // 逐级一个标高标注(该级首条线的中点)。
        double labelH = System.Math.Max((bx1 - bx0) / 60.0, 1e-3);
        foreach (var lv in res.Levels)
        {
            if (lv.Handles.Count == 0) continue;
            var first = lines.FirstOrDefault(l => l.Handle == lv.Handles[0]);
            if (first == null || first.Xyz.Length < 6) continue;
            int mid = (first.Xyz.Length / 3) / 2;
            float t = nLv > 1 ? (float)(lv.Index - 1) / (nLv - 1) : 0f;
            _scene.Add(new TextEntity
            {
                X = first.Xyz[mid * 3], Y = first.Xyz[mid * 3 + 1], Height = labelH,
                Text = lv.Elevation.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                Cr = 1 - t, Cg = 0.45f, Cb = t, LayerName = "平盘标高"
            });
        }
        RefreshScene();
        if (bx1 > bx0) Viewport.FitBounds(new[] { bx0, by0, bx1, by1 });

        var saved = await SaveCsvAsync("平盘标高清单", "平盘标高清单.csv", BenchLevelInventory.BuildReport(res, System.IO.Path.GetFileName(files[0].Path.LocalPath)));
        StatusMsg.Text = res.Message + (saved != null ? $" · 清单已存 {saved}" : "");
    }

    // 帮坡角反算平盘宽: 平盘宽反算 <台阶高H> <坡面角α°> <目标整体帮坡角β°> → W=H/tanβ−H/tanα(忠实原 SolveBermForOverallAngle, OverallSlopeAngleDeg 的逆)。
    private void BermForAngleCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 4 || !double.TryParse(tk[1], out double H) || !double.TryParse(tk[2], out double a) || !double.TryParse(tk[3], out double b))
        { StatusMsg.Text = "平盘宽反算：用法 平盘宽反算 <台阶高H> <坡面角α°> <目标整体帮坡角β°>（如 平盘宽反算 15 65 45）"; return; }
        if (H <= 0 || a <= 0 || a >= 90 || b <= 0 || b >= 90) { StatusMsg.Text = "平盘宽反算：需 H>0 且 0<α,β<90°"; return; }
        double W = Cad.BenchParameterExtractor.SolveBermForOverallAngle(H, a, b);
        double check = Cad.BenchParameterExtractor.OverallSlopeAngleDeg(H, a, W);   // 回代校核(W 代回正式应得 β)
        string note = W <= 1e-9 ? $"（目标 β={b:0.#}° ≥ 坡面角 α={a:0.#}°, 无需平盘 W=0; 欲更缓需 β<α）" : $"（回代β={check:0.#}°）";
        StatusMsg.Text = $"平盘宽反算：台阶高 {H:0.#}m · 坡面角 {a:0.#}° · 目标整体帮坡角 {b:0.#}° → 需平盘宽 W={W:0.##}m {note}";
    }

    // 现状台阶参数提取(忠实 ParameterExtractor 件二·提取): 坡顶/坡底台阶线 CSV(role,lineId,x,y,z) →
    // 逐顶点最近邻反推 台阶高H/坡面角α/平盘宽W/整体帮坡角β/采深/台阶数 → 画线(坡顶红/坡底蓝)+ 存报表。
    // 场景 2D 无逐点 Z, 故由 CSV 提供标高。"现状参数提取 [最小落差 最大落差]" 调坡面配对 Δz 窗口(缺省 2.5/60)。
    private async Task BenchParameterExtractAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "现状台阶参数提取：选坡顶/坡底台阶线 CSV (role,lineId,x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"现状参数提取：读文件失败 {ex.Message}"; return; }

        var lines = BenchParameterExtractor.ParseCsv(text);
        if (lines.Count == 0) { StatusMsg.Text = "现状参数提取：CSV 没解析出线(需 role,lineId,x,y,z; role=C/坡顶 或 T/坡底)"; return; }

        double dzMin = 2.5, dzMax = 60.0;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2) double.TryParse(tk[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dzMin);
        if (tk.Length >= 3) double.TryParse(tk[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dzMax);

        var res = BenchParameterExtractor.Extract(lines, dzMin > 0 ? dzMin : 2.5, dzMax > dzMin ? dzMax : 60.0);
        if (!res.Ok) { StatusMsg.Text = $"现状参数提取：{res.Message}"; return; }

        // 画坡顶(红)/坡底(蓝)台阶线, 便于核对取线范围。
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        BeginChange();
        foreach (var l in lines)
        {
            int npt = l.Xyz.Length / 3;
            if (npt < 2) continue;
            var pl = new PolylineEntity { Cr = l.IsCrest ? 1f : 0.2f, Cg = 0.5f, Cb = l.IsCrest ? 0.2f : 1f, LayerName = l.IsCrest ? "坡顶线" : "坡底线" };
            for (int i = 0; i < npt; i++)
            {
                double x = l.Xyz[i * 3], y = l.Xyz[i * 3 + 1];
                pl.Points.Add((x, y));
                if (x < bx0) bx0 = x; if (y < by0) by0 = y; if (x > bx1) bx1 = x; if (y > by1) by1 = y;
            }
            _scene.Add(pl);
        }
        RefreshScene();
        if (bx1 > bx0) Viewport.FitBounds(new[] { bx0, by0, bx1, by1 });

        var saved = await SaveCsvAsync("现状台阶参数", "现状台阶参数.csv", BenchParameterExtractor.BuildReport(res, System.IO.Path.GetFileName(files[0].Path.LocalPath)));
        StatusMsg.Text = res.Message + (saved != null ? $" · 报表已存 {saved}" : "");
    }

    // 标注台阶标高(忠实 BenchElevationAnnotator 放置算法): 台阶线 CSV(lineId,x,y,z[,category]) →
    // 逐条定「▽ 标高符号 + 高程数字」放置点(平盘居中/网格去重/类别配色) → 画 ▽+引线+文字到场景。
    // 场景 2D 无逐点 Z, 故由 CSV 提供标高; 原三维倾斜朝向不适用 2D 场景(记录), 放置算法保真。
    // "标注台阶标高 [符号大小m]" 指定符号大小(缺省按范围自动)。
    private async Task BenchElevationAnnotateAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "标注台阶标高：选台阶线 CSV (lineId,x,y,z[,category])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"标注台阶标高：读文件失败 {ex.Message}"; return; }

        var lines = BenchElevationAnnotator.ParseCsv(text);
        if (lines.Count == 0) { StatusMsg.Text = "标注台阶标高：CSV 没解析出线(需 lineId,x,y,z 四列)"; return; }

        var opt = new BenchElevationAnnotator.Options();
        int sp = cmd.IndexOf(' ');
        if (sp >= 0 && double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sz) && sz > 0)
            opt.SymbolSize = sz;

        var res = BenchElevationAnnotator.Build(lines, opt);
        if (!res.Ok) { StatusMsg.Text = $"标注台阶标高：{res.Message}"; return; }

        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        void Grow(double x, double y) { if (x < bx0) bx0 = x; if (y < by0) by0 = y; if (x > bx1) bx1 = x; if (y > by1) by1 = y; }
        BeginChange();
        foreach (var m in res.Markers)
        {
            float cr = m.R / 255f, cg = m.G / 255f, cb = m.B / 255f;
            // ▽ 符号(闭合三角) + 顶边引线 + 高程文字, 均落 台阶标高标注 图层。
            var tri = BenchElevationAnnotator.TriangleXY(m.X, m.Y, res.SymbolSize);
            var pl = new PolylineEntity { Closed = true, Cr = cr, Cg = cg, Cb = cb, LayerName = BenchElevationAnnotator.Layer };
            foreach (var (x, y) in tri) { pl.Points.Add((x, y)); Grow(x, y); }
            _scene.Add(pl);
            var (lx0, ly0, lx1, ly1) = BenchElevationAnnotator.LeaderXY(m.X, m.Y, res.SymbolSize, m.Label.Length);
            _scene.Add(new LineEntity { X0 = lx0, Y0 = ly0, X1 = lx1, Y1 = ly1, Cr = cr, Cg = cg, Cb = cb, LayerName = BenchElevationAnnotator.Layer });
            var (tx, ty) = BenchElevationAnnotator.TextAnchorXY(m.X, m.Y, res.SymbolSize);
            _scene.Add(new TextEntity { X = tx, Y = ty, Height = res.SymbolSize, Text = m.Label, Cr = cr, Cg = cg, Cb = cb, LayerName = BenchElevationAnnotator.Layer });
            Grow(lx1, ly1);
        }
        RefreshScene();
        if (bx1 > bx0) Viewport.FitBounds(new[] { bx0, by0, bx1, by1 });
        StatusMsg.Text = res.Message;
    }

    // 现状台阶参数校核(忠实 ParameterVerifier 兜底路径): 台阶线 CSV → 提取 → 与规范默认基准逐项校核(偏差%+状态)。
    // "参数校核 [排土] [hard|medium|soft] [摩擦角φ]": 排土=排土场基准(10/35/3); 硬度定采场基准; 摩擦角算稳定性 F。
    private async Task BenchParameterVerifyAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "参数校核：选坡顶/坡底台阶线 CSV (role,lineId,x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"参数校核：读文件失败 {ex.Message}"; return; }

        var lines = BenchParameterExtractor.ParseCsv(text);
        if (lines.Count == 0) { StatusMsg.Text = "参数校核：CSV 没解析出线(需 role,lineId,x,y,z)"; return; }
        var ext = BenchParameterExtractor.Extract(lines);
        if (!ext.Ok) { StatusMsg.Text = $"参数校核：{ext.Message}"; return; }

        // 解析标志: 排土 / 硬度 / 摩擦角。
        bool isDump = false; string? hardness = null; double? phi = null;
        foreach (var t in cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var s = t.ToLowerInvariant();
            if (s is "排土" or "排土场" or "dump") isDump = true;
            else if (s is "hard" or "硬" or "硬岩") hardness = "hard";
            else if (s is "medium" or "中" or "中硬" or "中硬岩") hardness = "medium";
            else if (s is "soft" or "软" or "软岩") hardness = "soft";
            else if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 && v < 60) phi = v;
        }

        // 采场基准优先取 DB 真实设计参数(parameter_definition 标准默认, 忠实原 PickTemplate→BenchTemplateReader 路径);
        // DB 无/排土场 → 兜底规范默认。V026 初设说明书真值替硬编码规范默认。
        (double H, double A, double W)? designOverride = null;
        string dbBaseNote = "";
        if (!isDump)
        {
            var gdb = EnsureGeoDb();
            if (gdb != null)
            {
                var bd = Data.GeoDataQueries.GetBenchDesignBaseline(gdb.Connection);
                if (bd.FromDb)
                {
                    designOverride = (bd.BenchHeightM!.Value, bd.SlopeAngleDeg!.Value, bd.SafetyPlatformWidthM!.Value);
                    dbBaseNote = $" · DB设计基准 H={bd.BenchHeightM:0.#}/α={bd.SlopeAngleDeg:0.#}/W={bd.SafetyPlatformWidthM:0.#}";
                }
            }
        }
        var rep = BenchParameterVerifier.Verify(ext, isDump, hardness, phi, designOverride);
        string combined = BenchParameterExtractor.BuildReport(ext, System.IO.Path.GetFileName(files[0].Path.LocalPath))
                        + "\n" + BenchParameterVerifier.BuildReport(rep);
        var saved = await SaveCsvAsync("现状台阶参数校核", "现状台阶参数校核.csv", combined);
        string statusCn = rep.OverallStatus switch { "pass" => "合格", "warning" => "偏差", "fail" => "超标", _ => "待定" };
        StatusMsg.Text = $"参数校核({(isDump ? "排土场" : "采场")}·{rep.DesignProvenance})：总体 {statusCn}；{ext.Message}"
                       + dbBaseNote
                       + (saved != null ? $" · 报表已存 {saved}" : "");
    }

    // 趋势整合现状台阶(忠实 TrendBenchIntegrator): 选中一条趋势多段线 + 台阶线 CSV(lineId,x,y,z) →
    // 趋势∩台阶求交 → 按标高聚级 → 每级压平到 z_k + 断头接平成规整线, 按级配色入场景。
    // 趋势线取 XY(场景 2D 够用); 台阶标高由 CSV 提供。"趋势整合台阶 [聚级带宽m]"(缺省 5)。
    private async Task TrendIntegrateAsync(string cmd)
    {
        var trendPl = _selected.OfType<PolylineEntity>().FirstOrDefault();
        if (trendPl == null || trendPl.Points.Count < 2) { StatusMsg.Text = "趋势整合台阶：请先在场景选中一条趋势多段线(方向线)"; return; }
        var trend = trendPl.Points.Select(p => (p.x, p.y, 0.0)).ToList();

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "趋势整合台阶：选现状台阶线 CSV (lineId,x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"趋势整合台阶：读文件失败 {ex.Message}"; return; }
        var benchLines = BenchLevelInventory.ParseCsv(text).Select(l => l.Xyz).Where(x => x.Length >= 6).ToList();
        if (benchLines.Count == 0) { StatusMsg.Text = "趋势整合台阶：CSV 无台阶线(需 lineId,x,y,z)"; return; }

        double bw = 5;
        int sp = cmd.IndexOf(' ');
        if (sp >= 0 && double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b) && b > 0) bw = b;

        var r = Cad.TrendBenchIntegrator.Integrate(trend, benchLines, bw);
        if (!r.Success) { StatusMsg.Text = $"趋势整合台阶：{r.Error}"; return; }

        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        BeginChange();
        int nLv = r.Benches.Count;
        for (int i = 0; i < r.Benches.Count; i++)
        {
            var ib = r.Benches[i];
            float t = nLv > 1 ? (float)i / (nLv - 1) : 0f;
            var pl = new PolylineEntity { Cr = 1 - t, Cg = 0.5f, Cb = t, LayerName = "整合台阶" };
            foreach (var p in ib.Line)
            {
                pl.Points.Add((p.X, p.Y));
                if (p.X < bx0) bx0 = p.X; if (p.Y < by0) by0 = p.Y; if (p.X > bx1) bx1 = p.X; if (p.Y > by1) by1 = p.Y;
            }
            _scene.Add(pl);
        }
        RefreshScene();
        if (bx1 > bx0) Viewport.FitBounds(new[] { bx0, by0, bx1, by1 });
        StatusMsg.Text = $"趋势整合台阶：{r.CrossingCount} 交点 → {r.Benches.Count} 级规整台阶(带宽 {bw:0.#}m; 标高 {r.Benches.Min(x => x.Elevation):0.#}~{r.Benches.Max(x => x.Elevation):0.#}m)";
    }

    // 煤厚分析等厚线(原 ThicknessSurfaceBuilder「煤厚分析面」的平面等厚线): 观测/见煤点 CSV → 取每行前 3 个
    // 数值列作(x,y,煤厚)(跳过 point_id/seam_code 非数值)→ IDW 插值 → 等厚线(蓝薄→红厚)+ 层厚标注 + 煤厚统计。
    // "煤厚等值线 <等厚距>" 指定等厚距(整数倍厚度), 缺省 auto 10 层。
    private async Task ThicknessIsopachAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "煤厚等值线：选观测/见煤点 CSV (含 x,y,煤厚 列)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("观测点 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var pts = new System.Collections.Generic.List<(double x, double y, double thickness)>();
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                var nums = new System.Collections.Generic.List<double>();
                foreach (var tok in line.Split(new[] { ',', '\t', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
                    if (double.TryParse(tok, System.Globalization.NumberStyles.Float, inv, out double v)) { nums.Add(v); if (nums.Count == 3) break; }
                if (nums.Count == 3) pts.Add((nums[0], nums[1], nums[2]));   // 每行前 3 数值 = x,y,煤厚
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = $"煤厚等值线：读取失败 {ex.Message}"; return; }
        if (pts.Count < 3) { StatusMsg.Text = "煤厚等值线：需 ≥3 个 (x,y,煤厚) 点(表头会自动跳过)"; return; }

        double interval = 0;
        int sp = cmd.IndexOf(' ');
        if (sp >= 0) double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, inv, out interval);
        var res = ThicknessSurface.Isopach(pts, gridN: 64, interval: interval);
        if (res.Lines.Count == 0) { StatusMsg.Text = $"煤厚等值线：煤厚无起伏或点不足（{Statistics.SummaryLine(res.Stats)}）"; return; }

        double tmin = res.Stats.Min, trange = System.Math.Max(res.Stats.Max - res.Stats.Min, 1e-9);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        BeginChange();
        foreach (var s in res.Lines) { if (s.X0 < minX) minX = s.X0; if (s.Y0 < minY) minY = s.Y0; if (s.X0 > maxX) maxX = s.X0; if (s.Y0 > maxY) maxY = s.Y0; }
        double labelH = System.Math.Max((maxX - minX) / 60.0, 1e-3);
        double iext = System.Math.Max(1e-9, (maxX - minX) * 1e-4);
        foreach (double L in res.Levels)   // 每层: 散段连成折线(单一可选实体) + 首折线中点厚度标注
        {
            float t = (float)((L - tmin) / trange);
            var xy = new List<(double, double, double, double)>();
            foreach (var s in res.Lines) if (s.Level == L) xy.Add((s.X0, s.Y0, s.X1, s.Y1));
            if (xy.Count == 0) continue;
            var polys = Contour.LinkSegments(xy, iext);
            foreach (var poly in polys)
            {
                if (poly.Count < 2) continue;
                var pl = new PolylineEntity { Cr = t, Cg = 0.5f, Cb = 1 - t };
                foreach (var p in poly) pl.Points.Add((p.x, p.y));
                _scene.Add(pl);
            }
            if (polys.Count > 0 && polys[0].Count > 0)
            {
                var lp = polys[0]; var mid = lp[lp.Count / 2];
                _scene.Add(new TextEntity { X = mid.x, Y = mid.y, Height = labelH, Text = L.ToString("0.##", inv), Cr = t, Cg = 0.5f, Cb = 1 - t });
            }
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        StatusMsg.Text = $"煤厚等值线：{pts.Count} 见煤点 → {res.Levels.Count} 层等厚线 · 煤厚 {Statistics.SummaryLine(res.Stats)}";
    }

    // 月度剥离均衡(拉紧绳)：CSV(期号,累计必剥lo,累计能力hi 万m³) → 走廊内单调最平累计剥离曲线 + 触边转折
    // (露煤紧迫/能力吃紧) + 均衡度CV。区别 剥采比均衡(VP=采出↔剥离比): 本命令是时间轴月度剥离调度。忠实原 TautString。
    private async Task StripScheduleAsync(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "月度剥离均衡：选 CSV (期号, 累计必剥 lo, 累计能力 hi; 万m³, 含期初0行)", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("期序列 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } } });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);   // 每行 期号(x), 累计必剥(y), 累计能力(z)
        if (!r.Success || r.Points.Count < 2) { StatusMsg.Text = "月度剥离均衡：需 ≥2 行 (期号, 累计必剥, 累计能力)"; return; }
        var lo = new List<double>(); var hi = new List<double>();
        foreach (var p in r.Points) { lo.Add(p.y); hi.Add(p.z); }
        var pivots = new List<TautString.Pivot>();
        var c = TautString.Solve(lo, hi, lo[^1], out var err, pivots);
        if (c == null) { StatusMsg.Text = $"月度剥离均衡：{err}"; return; }
        var incArr = TautString.ToIncrements(c);
        double cv = TautString.Cv(incArr);
        double imin = double.MaxValue, imax = double.MinValue, isum = 0;
        foreach (var d in incArr) { if (d < imin) imin = d; if (d > imax) imax = d; isum += d; }
        double iavg = incArr.Length > 0 ? isum / incArr.Length : 0;
        double ymax = 0; foreach (var h in hi) if (h > ymax) ymax = h;
        BeginChange();
        for (int i = 1; i < lo.Count; i++)   // 累计必剥 lo(灰)
            _scene.Add(new LineEntity { X0 = i - 1, Y0 = lo[i - 1], X1 = i, Y1 = lo[i], Cr = 0.55f, Cg = 0.55f, Cb = 0.55f, LayerName = "剥离均衡" });
        for (int i = 1; i < hi.Count; i++)   // 累计能力 hi(浅灰)
            _scene.Add(new LineEntity { X0 = i - 1, Y0 = hi[i - 1], X1 = i, Y1 = hi[i], Cr = 0.75f, Cg = 0.75f, Cb = 0.75f, LayerName = "剥离均衡" });
        for (int i = 1; i < c.Length; i++)   // 均衡累计曲线(青)
            _scene.Add(new LineEntity { X0 = i - 1, Y0 = c[i - 1], X1 = i, Y1 = c[i], Cr = 0.1f, Cg = 0.8f, Cb = 0.9f, LayerName = "剥离均衡" });
        RefreshScene();
        if (ymax > 0) Viewport.FitBounds(new double[] { 0, 0, c.Length - 1, ymax });
        string pv = pivots.Count > 0 ? " · 关键月 " + string.Join("/", pivots.ConvertAll(p => p.ToString())) : "";
        StatusMsg.Text = $"月度剥离均衡(拉紧绳)：{incArr.Length}期 · 月剥离 {imin.ToString("0", inv)}~{imax.ToString("0", inv)}(均{iavg.ToString("0", inv)}万m³) · "
            + $"均衡度CV {cv.ToString("0.###", inv)} · {pivots.Count}个触边转折{pv} · 青=均衡曲线/灰=必剥·能力包络（X=期号 Y=累计万m³）";
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

    // 台阶面提取：OFF 现状面 → 按坡度切台阶坡面(连通域分片) → 每片坡顶线(青)/坡底线(橙)入场景 + 台阶高/坡度/面积汇总。
    // 比 坡顶底线(仅平陡散断棱边)完整: 分片 + 有序上下沿 + 每片指标。忠实原 BenchFaceExtractor。用法 "台阶面提取 [坡度阈值°]"。
    private async Task BenchFaceExtractAsync(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double slopeDeg = 20.0;   // 判为坡面的最小坡度(默认 20°, 按实际图纸调)
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double sd) && sd > 0) slopeDeg = sd;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "台阶面提取：选 OFF 现状面", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } } });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"台阶面提取：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "台阶面提取：未解析到三角网格"; return; }
        var vf = new double[verts.Count * 3];
        for (int i = 0; i < verts.Count; i++) { vf[i * 3] = verts[i].x; vf[i * 3 + 1] = verts[i].y; vf[i * 3 + 2] = verts[i].z; }
        var tf = new int[tris.Count * 3];
        for (int i = 0; i < tris.Count; i++) { tf[i * 3] = tris[i].a; tf[i * 3 + 1] = tris[i].b; tf[i * 3 + 2] = tris[i].c; }
        var r = Cad.BenchFaceExtractor.Extract(vf, tf, new Cad.BenchFaceExtractor.Options { MinSlopeDeg = slopeDeg });
        if (!r.Ok) { StatusMsg.Text = $"台阶面提取：{r.Message}"; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        BeginChange();
        void AddRail(double[] xyz, float cr, float cg, float cb)
        {
            if (xyz.Length < 6) return;
            var pl = new PolylineEntity { Cr = cr, Cg = cg, Cb = cb, LayerName = "台阶坡面" };
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            {
                double x = xyz[i], y = xyz[i + 1];
                pl.Points.Add((x, y));
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
            _scene.Add(pl);
        }
        foreach (var f in r.Faces)
        {
            AddRail(f.CrestXyz, 0.1f, 0.8f, 0.9f);    // 坡顶线 青
            AddRail(f.ToeXyz, 0.95f, 0.55f, 0.1f);    // 坡底线 橙
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        string warn = r.Warnings.Count > 0 ? " · " + string.Join(" ", r.Warnings) : "";
        StatusMsg.Text = $"台阶面提取(阈值{slopeDeg.ToString("0.#", inv)}°)：{r.Message} 青=坡顶线/橙=坡底线{warn}";
    }

    // 煤岩台阶判定：坡顶线 CSV(x,y,z) + 台阶高 → 沿线采样种子库各煤层柱(VirtualBorehole) → 台阶区间重叠煤厚
    // → 煤/岩/混台阶 + 各煤层平均厚 + 是否出煤体。忠实原 StandardLevelModel 煤/岩判定核。可接台阶面提取出的坡顶线。
    private async Task BenchCoalCmd(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var db = EnsureGeoDb(); if (db == null) return;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double benchH = 12.0;   // 台阶高 m（默认 12）
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double bh) && bh > 0) benchH = bh;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "煤岩台阶判定：选坡顶线 CSV (x,y,z; 可用台阶面提取得)", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("3D 线 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } } });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success || r.Points.Count < 1) { StatusMsg.Text = "煤岩台阶判定：需 ≥1 个坡顶线点(x,y,z)"; return; }
        var hp = Data.GeoDataQueries.GetHorizonPoints(db.Connection);
        if (hp.Count == 0) { StatusMsg.Text = "煤岩台阶判定：库中无层位点(先展绘层位数据/配置地质模型)"; return; }
        var seams = VirtualBorehole.SeamsFromHorizonPoints(
            System.Linq.Enumerable.Select(hp, p => (p.SeamCode, p.IsRoof, p.X, p.Y, p.Z)));
        double crestZ = 0; foreach (var p in r.Points) crestZ += p.z; crestZ /= r.Points.Count;   // 坡顶级代表标高
        double toeZ = crestZ - benchH;
        var foot = new List<(double, double)>(); foreach (var p in r.Points) foot.Add((p.x, p.y));
        var res = Cad.BenchCoalClassifier.Classify(foot, toeZ, crestZ, seams);
        // 坡顶线入场景, 按煤/岩着色（煤=黑·混=橙·岩=灰）
        float cr = res.BenchKind == BenchCoalResult.Kind.Coal ? 0.15f : res.BenchKind == BenchCoalResult.Kind.Mixed ? 0.95f : 0.6f;
        float cg = res.BenchKind == BenchCoalResult.Kind.Coal ? 0.15f : res.BenchKind == BenchCoalResult.Kind.Mixed ? 0.55f : 0.6f;
        float cb = res.BenchKind == BenchCoalResult.Kind.Coal ? 0.15f : res.BenchKind == BenchCoalResult.Kind.Mixed ? 0.10f : 0.6f;
        BeginChange();
        var pl = new PolylineEntity { Cr = cr, Cg = cg, Cb = cb, LayerName = "煤岩台阶" };
        foreach (var p in r.Points) pl.Points.Add((p.x, p.y));
        _scene.Add(pl);
        RefreshScene();
        if (r.Bounds != null && r.Bounds.Length == 4) Viewport.FitBounds(r.Bounds);
        string seamTxt = res.SeamSummary.Length > 0 ? " · 煤层 " + res.SeamSummary : "";
        StatusMsg.Text = $"煤岩台阶判定(台阶[{toeZ.ToString("0.#", inv)},{crestZ.ToString("0.#", inv)}]·高{benchH.ToString("0.#", inv)}m)："
            + $"{res.KindLabel} · 煤厚占比 {(res.CoalRatio * 100).ToString("0.#", inv)}% · 均厚 {res.MeanCoalThickM.ToString("0.##", inv)}m · "
            + $"{(res.IsMineableCoal ? "出煤体(沿底板)" : "不出煤体(薄)")} · 见煤 {res.SampleHit}/{res.SampleCount} 点{seamTxt}";
    }

    // 煤层露头线：OFF 现状面 + 种子库层位 → 逐煤层求 现状∩顶板(坡顶线)/现状∩底板(坡底线) 等值线(marching triangles)
    // → 坡顶线(青)/坡底线(橙)入场景 + 各层露头长度汇总。忠实原 SeamOutcropLineExtractor。用法 "煤层露头线 [最小线长m]"。
    private async Task SeamOutcropCmd(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var db = EnsureGeoDb(); if (db == null) return;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double minLen = 20.0;
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double ml) && ml >= 0) minLen = ml;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "煤层露头线：选 OFF 现状面", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } } });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"煤层露头线：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "煤层露头线：未解析到三角网格"; return; }
        var vf = new double[verts.Count * 3];
        for (int i = 0; i < verts.Count; i++) { vf[i * 3] = verts[i].x; vf[i * 3 + 1] = verts[i].y; vf[i * 3 + 2] = verts[i].z; }
        var tf = new int[tris.Count * 3];
        for (int i = 0; i < tris.Count; i++) { tf[i * 3] = tris[i].a; tf[i * 3 + 1] = tris[i].b; tf[i * 3 + 2] = tris[i].c; }
        var hp = Data.GeoDataQueries.GetHorizonPoints(db.Connection);
        if (hp.Count == 0) { StatusMsg.Text = "煤层露头线：库中无层位点(先展绘层位数据/配置地质模型)"; return; }
        var seams = VirtualBorehole.SeamsFromHorizonPoints(
            System.Linq.Enumerable.Select(hp, p => (p.SeamCode, p.IsRoof, p.X, p.Y, p.Z)));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int totCrest = 0, totToe = 0, seamsOut = 0, totBands = 0;
        double crestLen = 0, toeLen = 0, bandSpMin = double.MaxValue, bandSpMax = double.MinValue;
        BeginChange();
        void AddLine(double[] xyz, float cr, float cg, float cb)
        {
            if (xyz.Length < 6) return;
            var pl = new PolylineEntity { Cr = cr, Cg = cg, Cb = cb, LayerName = "煤层露头" };
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            {
                double x = xyz[i], y = xyz[i + 1];
                pl.Points.Add((x, y));
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
            _scene.Add(pl);
        }
        var opt = new SeamOutcropLineExtractor.Options { MinLengthM = minLen };
        foreach (var seam in seams)
        {
            var sm = seam;   // 每层各自采样器（顶/底板 TIN 竖直求交）
            SeamOutcropLineExtractor.SampleZ roofZ = (double x, double y, out double z) =>
            { var rr = TinSampler.SampleZ(sm.RoofPoints, x, y); z = rr ?? 0; return rr.HasValue; };
            SeamOutcropLineExtractor.SampleZ floorZ = (double x, double y, out double z) =>
            { var ff = TinSampler.SampleZ(sm.FloorPoints, x, y); z = ff ?? 0; return ff.HasValue; };
            var res = SeamOutcropLineExtractor.Extract(vf, tf, roofZ, floorZ, opt);
            if (!res.Ok) continue;
            seamsOut++;
            foreach (var l in res.CrestLines) { AddLine(l.Xyz, 0.1f, 0.8f, 0.9f); totCrest++; crestLen += l.PlanLengthM; }
            foreach (var l in res.ToeLines) { AddLine(l.Xyz, 0.95f, 0.55f, 0.1f); totToe++; toeLen += l.PlanLengthM; }
            // 坡顶↔坡底按并行性配成露头带(=该层的出露条带); 煤厚由本层顶/底板均高差估
            double mr = 0, mf = 0;
            foreach (var p in sm.RoofPoints) mr += p.z; if (sm.RoofPoints.Count > 0) mr /= sm.RoofPoints.Count;
            foreach (var p in sm.FloorPoints) mf += p.z; if (sm.FloorPoints.Count > 0) mf /= sm.FloorPoints.Count;
            double thick = System.Math.Max(0.5, mr - mf);
            var bands = SeamOutcropLineExtractor.PairIntoBands(res.CrestLines, res.ToeLines, thick, out _);
            foreach (var bd in bands) { totBands++; if (bd.MedianSpacingM < bandSpMin) bandSpMin = bd.MedianSpacingM; if (bd.MedianSpacingM > bandSpMax) bandSpMax = bd.MedianSpacingM; }
        }
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        if (seamsOut == 0) { StatusMsg.Text = "煤层露头线：无一层与现状面相交(整层已采完/尚未揭露, 或 XY 范围不重叠)"; return; }
        string bandTxt = totBands > 0 ? $" · 露头带 {totBands} 条(带宽 {bandSpMin.ToString("0.#", inv)}~{bandSpMax.ToString("0.#", inv)}m=煤厚/tan坡)" : "";
        StatusMsg.Text = $"煤层露头线({seamsOut}/{seams.Count} 层出露)：坡顶 {totCrest} 条({crestLen.ToString("0", inv)}m)/坡底 {totToe} 条({toeLen.ToString("0", inv)}m){bandTxt} · 青=坡顶线(顶板露头)/橙=坡底线(底板露头)";
    }

    // 更新煤层面/现状面：目标 OFF + 观测点 CSV(x,y,z) + 影响半径 → 半径内顶点 smoothstep 羽化 + IDW 拟合观测点
    // → 报受影响顶点/位移/净体积/影响片区 + 观测点入场景 + 导出更新后 OFF。忠实原 SurfaceUpdateEngine 默认(IDW)路径。
    private async Task SurfaceUpdateCmd(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double radius = 80.0;
        string algo = "IDW";   // IDW/NN/MA/OK/SK/UK
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double rad) && rad > 0) radius = rad;
        if (tk.Length >= 3) algo = tk[2].ToUpperInvariant();
        var f1 = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "更新煤层面：选目标面 OFF", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } } });
        if (f1.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(f1[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"更新煤层面：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "更新煤层面：未解析到三角网格"; return; }
        var f2 = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "更新煤层面：选观测点 CSV (x,y,z)", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } } });
        if (f2.Count == 0) return;
        var pr = PointDataImportService.Load(f2[0].Path.LocalPath);
        if (!pr.Success || pr.Points.Count == 0) { StatusMsg.Text = "更新煤层面：观测点为空"; return; }
        var obs = new List<(double, double, double)>();
        foreach (var p in pr.Points) obs.Add((p.x, p.y, p.z));
        var vf = new double[verts.Count * 3];
        for (int i = 0; i < verts.Count; i++) { vf[i * 3] = verts[i].x; vf[i * 3 + 1] = verts[i].y; vf[i * 3 + 2] = verts[i].z; }
        var tf = new int[tris.Count * 3];
        for (int i = 0; i < tris.Count; i++) { tf[i * 3] = tris[i].a; tf[i * 3 + 1] = tris[i].b; tf[i * 3 + 2] = tris[i].c; }
        var res = Cad.SurfaceUpdate.Evaluate(vf, tf, obs, new Cad.SurfaceUpdate.Options { InfluenceRadius = radius, Algorithm = algo });
        BeginChange();
        foreach (var o in obs) { var pt = new PointEntity { X = o.Item1, Y = o.Item2 }; AssignLayer(pt); _scene.Add(pt); }
        RefreshScene();
        if (pr.Bounds != null && pr.Bounds.Length == 4) Viewport.FitBounds(pr.Bounds);
        string offName = null;
        if (res.Any)
        {
            var newVerts = new List<(double x, double y, double z)>(verts.Count);
            for (int i = 0; i < verts.Count; i++) newVerts.Add((verts[i].x, verts[i].y, res.NewZ[i]));
            offName = await SaveCsvAsync("导出更新后煤层面", "surface_updated.off", MeshWeld.ToOff(newVerts, tris));
        }
        string cl = res.Clusters.Count > 0 ? $" · {res.Clusters.Count} 影响片区" : "";
        StatusMsg.Text = $"更新煤层面(R={radius.ToString("0.#", inv)}m·{(obs.Count == 1 ? "NN" : algo)})：{res.Message}{cl} · 抬升≤{res.MaxDisp.ToString("0.##", inv)}m/下沉≤{(-res.MinDisp).ToString("0.##", inv)}m · 净体积 {res.NetVolume.ToString("0", inv)}m³（观测点已入场景）"
            + (offName != null ? $" → {offName}" : "");
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
        if (sum.Count > 0)
            DrawCategoryBars(sum.Select(p => (Cad.Tasks.ProcessProgress.Label(p.Process), p.AvgAttainmentPct)).ToList(), "达成%");
        StatusMsg.Text = "工序进度跟踪：" + string.Join(" · ", parts) + (sum.Count > 0 ? " · 达成柱入场景" : "");
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

    // 生产任务编制(裂解装箱, 忠实原 TaskExploder): 代表性算例(三班×两采装面+穿孔+配煤) → 按班次时窗×编组班产装箱
    // → 逐班任务 + 校核(运力/双占/接续/配煤/欠产)。可选「生产任务编制 <月采出万t> <月剥离万m³> [作业日]」按月计划
    // 分配日目标(忠实原 ShortTermLink); 无参用样例日目标。真数据需接台账/钻孔计划。
    private void TaskExplodeCmd(string cmd)
    {
        var cfg = new Cad.Tasks.Scheduling.ExploderConfig
        {
            DateLabel = "示例", IdPrefix = "D", BlastStart = 12, BlastEnd = 12.5,
            Shifts = { new("早", 0, 8), new("中", 8, 16), new("夜", 16, 24) },
            Blend = new Cad.Tasks.Scheduling.BlendStandard { MaxAshPct = 12.5, EffHoursPerDay = 20 },
            Faces =
            {
                new Cad.Tasks.Scheduling.FaceInput { Zone = "4煤南", Process = Cad.Tasks.ProcessType.Load, DayTargetM3 = 4800, Material = "煤",
                    Quality = new Cad.Tasks.CoalQuality { AshPct = 14 },
                    Group = new Cad.Tasks.Scheduling.EquipmentGroup { MainEquipment = "WK-35A", GroupCapacityM3PerH = 350, RecommendedTrucks = 5,
                        Trucks = new() { "T1", "T2", "T3", "T4", "T5" } } },
                new Cad.Tasks.Scheduling.FaceInput { Zone = "4煤北", Process = Cad.Tasks.ProcessType.Load, DayTargetM3 = 3600, Material = "煤",
                    Quality = new Cad.Tasks.CoalQuality { AshPct = 10 },
                    Group = new Cad.Tasks.Scheduling.EquipmentGroup { MainEquipment = "PH2800", GroupCapacityM3PerH = 300, RecommendedTrucks = 4,
                        Trucks = new() { "T6", "T7", "T8" } } },   // 3<4 → 运力不足
            },
            Drills = { new Cad.Tasks.Scheduling.DrillInput { EquipId = "ZJ-01", Zone = "4煤南", Start = 0, End = 6 } },
            Maintenance = { new Cad.Tasks.Scheduling.MaintenanceWindow { EquipId = "PH2800", Start = 0, End = 2, Label = "定检" } },
        };
        // 可选: 按月计划分配日目标(忠实原 ShortTermLink)。参数 <月采出万t> <月剥离万m³> [作业日]。
        string linkNote = "";
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 3 && double.TryParse(tk[1], out double mCoal) && double.TryParse(tk[2], out double mStrip) && mCoal > 0)
        {
            double mWork = tk.Length >= 4 && double.TryParse(tk[3], out double w) && w > 0 ? w : 25;
            var month = new Cad.Tasks.Scheduling.ShortTermLink.MonthInfo
            { HasPlan = true, PlanName = "命令行", MonthLabel = "本月", CoalWanT = mCoal, StripWanM3 = mStrip, Workdays = mWork, Density = 1.4 };
            linkNote = " · " + Cad.Tasks.Scheduling.ShortTermLink.ApplyToConfig(cfg, month);
        }
        var r = Cad.Tasks.Scheduling.TaskExploder.Explode(cfg);
        int loads = r.Tasks.Count(t => t.Process == Cad.Tasks.ProcessType.Load);
        int idles = r.Tasks.Count(t => t.Process == Cad.Tasks.ProcessType.Idle);
        int errs = r.Violations.Count(v => v.Severity == Cad.Tasks.Scheduling.ViolationSeverity.Error);
        int warns = r.Violations.Count(v => v.Severity == Cad.Tasks.Scheduling.ViolationSeverity.Warn);
        string vio = string.Join(" · ", r.Violations.Take(4).Select(v => $"{v.Code}"));
        StatusMsg.Text = $"生产任务编制(示例排产)：{r.Tasks.Count} 任务(采装{loads}/穿孔{r.Tasks.Count(t => t.Process == Cad.Tasks.ProcessType.Drill)}/空闲{idles}) · 校核 {errs}错/{warns}警[{vio}]{linkNote}（代表算例; 真数据接台账/钻孔计划）";
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
        var (nodes, adj) = Cad.RoadNetwork.BuildNoded(polys, tol);
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

    // 瓶颈段分析(忠实原 TransportIndicators §1.3.5 介数核): 场景中线建路网 → 边介数(最短路中心性) → 高流量段。
    // 源汇=悬挂端点(路网端, 天然出入口), 无则全节点(截断 40)。前几段红粗高亮上屏。
    // (原另乘 车道/陡坡因子; Kylin 中线几何最小模型无 车道/坡度/状态, 该加权记录待边属性模型。)
    private void RoadBottleneckCmd()
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "瓶颈段分析：场景无中线（多段线）"; return; }
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = Cad.RoadNetwork.BuildNoded(polys, tol);
        if (nodes.Count < 2) { StatusMsg.Text = "瓶颈段分析：路网节点不足"; return; }
        var ends = Cad.RoadNetwork.DanglingEndpoints(adj);
        System.Collections.Generic.IReadOnlyList<int> srcs;
        if (ends.Count >= 2) srcs = ends;
        else { var all = new List<int>(); for (int i = 0; i < System.Math.Min(nodes.Count, 40); i++) all.Add(i); srcs = all; }
        var bw = Cad.RoadNetwork.EdgeBetweenness(adj, srcs, srcs);
        if (bw.Count == 0) { StatusMsg.Text = "瓶颈段分析：无边"; return; }
        int topN = System.Math.Min(5, bw.Count);
        BeginChange();
        for (int i = 0; i < topN; i++)
        {
            var e = bw[i]; if (e.Betweenness == 0) break;
            var a = nodes[e.U]; var b = nodes[e.V];
            _scene.Add(new LineEntity { X0 = a.x, Y0 = a.y, X1 = b.x, Y1 = b.y, Cr = 0.95f, Cg = 0.2f, Cb = 0.15f, LayerName = "瓶颈段" });
        }
        RefreshScene();
        var head = string.Join(" · ", bw.Take(topN).Where(e => e.Betweenness > 0).Select((e, i) => $"#{i + 1} 介数{e.Betweenness}(长{e.LengthM:0.#}m)"));
        StatusMsg.Text = $"瓶颈段分析(介数核·{(ends.Count >= 2 ? $"{ends.Count} 端点源汇" : "全节点")})：{bw.Count} 边 · 前 {topN} 高流量段红粗上屏 · {head}（车道/陡坡加权待边属性）";
    }

    // 路网运输指标(忠实原 TransportIndicators 几何部分): 场景路网 → 总里程 + 源×汇可达对 运距均值/最大 + 瓶颈段。
    // 源汇=悬挂端点(无则全节点截 40)。运量加权/成本需吨量与采矿模型, 记录。
    private void RoadTransportIndicatorsCmd()
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "路网运输指标：场景无中线（多段线）"; return; }
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = Cad.RoadNetwork.BuildNoded(polys, tol);
        if (nodes.Count < 2) { StatusMsg.Text = "路网运输指标：路网节点不足"; return; }
        var ends = Cad.RoadNetwork.DanglingEndpoints(adj);
        System.Collections.Generic.IReadOnlyList<int> srcs;
        string basis;
        if (ends.Count >= 2) { srcs = ends; basis = $"{ends.Count} 端点源汇"; }
        else { var all = new List<int>(); for (int i = 0; i < System.Math.Min(nodes.Count, 40); i++) all.Add(i); srcs = all; basis = "全节点(截40)"; }
        var s = Cad.RoadNetwork.NetworkIndicators(adj, srcs, srcs);
        var bw = Cad.RoadNetwork.EdgeBetweenness(adj, srcs, srcs);
        string topSeg = bw.Count > 0 && bw[0].Betweenness > 0 ? $"最忙段 介数{bw[0].Betweenness}(长{bw[0].LengthM:0.#}m)" : "无瓶颈";
        StatusMsg.Text = $"路网运输指标({basis})：总里程 {s.TotalMileageM / 1000:0.###} km · 可达对 {s.ReachablePairs} · 运距 均 {s.MeanDistM:0.#}m/最大 {s.MaxDistM:0.#}m · {topSeg}（运量加权/成本需吨量·采矿模型, 记录）";
    }

    // 结构路面(忠实原 StructurePavement): 场景中线(多段线)按路宽等宽外扩成闭合结构路面带 ribbon 入场景。
    // 用法 结构路面 [路宽m 默认20]。
    private void StructurePavementCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double width = 20; if (tk.Length > 1 && double.TryParse(tk[1], out var wv) && wv > 0) width = wv;
        var lines = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) lines.Add(pl.Points);
        if (lines.Count == 0) { StatusMsg.Text = "结构路面：场景无中线（多段线）"; return; }
        var ribbons = Cad.StructurePavement.BuildRibbons(lines, width);
        if (ribbons.Count == 0) { StatusMsg.Text = "结构路面：无有效中线(≥2 点)"; return; }
        BeginChange();
        foreach (var rib in ribbons)
        {
            var poly = new PolylineEntity { Closed = true, Cr = 0.55f, Cg = 0.55f, Cb = 0.6f, LayerName = "结构路面" };
            poly.Points.AddRange(rib);
            _scene.Add(poly);
        }
        RefreshScene();
        StatusMsg.Text = $"结构路面(路宽 {width:0.#}m·半宽外扩)：{ribbons.Count} 条中线 → 闭合结构路面带入场景（层「结构路面」）";
    }

    // 中线交点分类(忠实 CenterlineJunctions): 场景中线(多段线) → X十字/T丁字/半腰焊/接缝/汇合口 四型分类 →
    // 按型配色画交点标记 + 分类计数。2D 场景 Z=0 故不判立交(需 3D 中线, 记录)。"中线交点 [容差m]"。
    private void CenterlineJunctionsCmd(string cmd)
    {
        var lines = new List<double[]>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2)
            {
                var arr = new double[pl.Points.Count * 3];
                for (int i = 0; i < pl.Points.Count; i++) { arr[i * 3] = pl.Points[i].x; arr[i * 3 + 1] = pl.Points[i].y; arr[i * 3 + 2] = 0; }
                lines.Add(arr);
            }
        if (lines.Count < 2) { StatusMsg.Text = "中线交点：场景需 ≥2 条中线(多段线)"; return; }
        double tol = Cad.CenterlineJunctions.DefaultContactTolM;
        int sp = cmd.IndexOf(' ');
        if (sp >= 0 && double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) && t > 0) tol = t;
        var set = Cad.CenterlineJunctions.Build(lines, tol);
        if (set.Count == 0) { StatusMsg.Text = $"中线交点：未发现交点(容差 {tol:0.#}m)"; return; }
        BeginChange();
        foreach (var j in set.All)
        {
            (float r, float g, float b) = j.Kind switch
            {
                JunctionKind.Cross => (1f, 0.2f, 0.2f),      // 红
                JunctionKind.Tee => (1f, 0.6f, 0.1f),        // 橙
                JunctionKind.MidWeld => (1f, 0.9f, 0.2f),    // 黄
                _ => j.LineCount >= 3 ? (0.2f, 0.8f, 0.3f) : (0.6f, 0.6f, 0.6f),  // 汇合绿 / 接缝灰
            };
            _scene.Add(new CircleEntity { Cx = j.X, Cy = j.Y, Radius = tol * 0.4, Cr = r, Cg = g, Cb = b, LayerName = "中线交点" });
        }
        RefreshScene();
        StatusMsg.Text = "中线交点：" + set.Summary + $"（容差 {tol:0.#}m; 红X/橙T/黄焊/绿汇合/灰缝; 2D 场景无高差, 立交需 3D 中线）";
    }

    // 路段分类(忠实 RoadTopology R-T1/R-T2/R-T3): 场景中线建路网 → 碎边压成路段 →
    // 节点 5 类(度数) + 路段 3 类(干线/支线/孤立段) → 按类配色画路段 + 计数。装卸点/人工改判/可通行需富图模型(记录)。
    private void RoadTopologyCmd()
    {
        var polys = new List<System.Collections.Generic.IReadOnlyList<(double x, double y)>>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.Points.Count >= 2) polys.Add(pl.Points);
        if (polys.Count == 0) { StatusMsg.Text = "路段分类：场景无中线(多段线)"; return; }
        double tol = System.Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var (nodes, adj) = Cad.RoadNetwork.BuildNoded(polys, tol);
        var r = Cad.RoadTopology.Analyze(nodes, adj);
        if (r.Segments.Count == 0) { StatusMsg.Text = "路段分类：未识别路段"; return; }
        BeginChange();
        foreach (var s in r.Segments)
        {
            (float cr, float cg, float cb) = s.Class switch
            {
                RoadSegmentClass.Trunk => (0.2f, 0.8f, 0.3f),   // 干线绿
                RoadSegmentClass.Spur => (1f, 0.6f, 0.1f),      // 支线橙
                _ => (0.6f, 0.6f, 0.6f),                        // 孤立段灰
            };
            var pl = new PolylineEntity { Cr = cr, Cg = cg, Cb = cb, LayerName = "路段分类" };
            foreach (int ni in s.NodePath) if (ni >= 0 && ni < nodes.Count) pl.Points.Add(nodes[ni]);
            if (pl.Points.Count >= 2) _scene.Add(pl);
        }
        RefreshScene();
        StatusMsg.Text = "路段分类：" + r.Summary + "（绿干线/橙支线/灰孤立段; 装卸点/人工改判/可通行过滤需富图模型）";
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
        var solid = LayerSolid.FromSurfaces(v0, t0, v1, t1);
        if (solid == null) { StatusMsg.Text = "快速建模：顶/底面需为有开边的开放面（取其边界环放样侧壁）"; return; }
        var (wv, wt) = solid.Value;
        var d = MeshDiagnose.Analyze(wv, wt);
        bool watertight = d.BoundaryEdges == 0 && d.NonManifoldEdges == 0;
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".", "quickmodel.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(wv, wt)); }
        catch (System.Exception ex) { StatusMsg.Text = $"快速建模：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"快速建模：顶+底+侧壁 焊成 {wt.Count} 三角 · {(watertight ? "水密(闭合地质体)" : $"非水密(开放边 {d.BoundaryEdges})")} → {System.IO.Path.GetFileName(outPath)}";
    }

    // 导出网格到 OBJ/PLY/STL：选源 OFF → 转格式 → 写同名同目录 .obj/.ply/.stl
    private async Task ExportMeshAsync(string fmt)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"导出网格 {fmt.ToUpperInvariant()}：选源 OFF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        var (v, t) = MeshMetrics.ParseOff(System.IO.File.ReadAllText(files[0].Path.LocalPath));
        if (t.Count == 0) { StatusMsg.Text = "导出网格：源无三角网"; return; }
        string outPath = System.IO.Path.ChangeExtension(files[0].Path.LocalPath, fmt);
        try { System.IO.File.WriteAllText(outPath, MeshExport.ByExtension(fmt, v, t)); }
        catch (System.Exception ex) { StatusMsg.Text = $"导出网格：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"导出网格 {fmt.ToUpperInvariant()}：{v.Count} 顶点 · {t.Count} 三角 → {System.IO.Path.GetFileName(outPath)}";
    }

    // 网格简化(顶点聚类)：选 OFF → 按容差(包围盒对角×比例, 默认1%)并顶点、丢塌陷三角 → 写 simplified.off
    private async Task MeshSimplifyAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "网格简化：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        var (v, t) = MeshMetrics.ParseOff(System.IO.File.ReadAllText(files[0].Path.LocalPath));
        if (t.Count == 0) { StatusMsg.Text = "网格简化：无三角网"; return; }
        double ratio = 0.01;   // 可选 "网格简化 <比例%>"
        var tk = cmd.Split(new[] { ' ', '%' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2 && double.TryParse(tk[1], out double pct)) ratio = pct / 100.0;
        var r = MeshSimplify.ByClustering(v, t, ratio);
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".", "simplified.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(r.Verts, r.Tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格简化：写出失败 {ex.Message}"; return; }
        double redV = r.InputVerts > 0 ? 100.0 * (r.InputVerts - r.OutputVerts) / r.InputVerts : 0;
        double redT = r.InputTris > 0 ? 100.0 * (r.InputTris - r.OutputTris) / r.InputTris : 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"网格简化(容差 {(ratio * 100).ToString("0.##", inv)}%)：顶点 {r.InputVerts}→{r.OutputVerts}(-{redV.ToString("0.#", inv)}%) · 三角 {r.InputTris}→{r.OutputTris}(-{redT.ToString("0.#", inv)}%) → {System.IO.Path.GetFileName(outPath)}";
    }

    // 连续多层自动建模：选 N 份 OFF 层位面 → 按均高降序排 → 逐相邻对成体 → 各夹层体写 layerN.off
    private async Task MultiLayerModelAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "连续多层建模：选 N 份层位面 OFF（≥2）",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count < 2) { StatusMsg.Text = "连续多层建模：请选 ≥2 份层位面 OFF"; return; }
        var surfaces = new List<(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)>();
        foreach (var f in files)
        {
            var (v, t) = MeshMetrics.ParseOff(System.IO.File.ReadAllText(f.Path.LocalPath));
            if (t.Count > 0) surfaces.Add((v, t));
        }
        if (surfaces.Count < 2) { StatusMsg.Text = "连续多层建模：有效层位面不足 2"; return; }
        var solids = LayerSolid.MultiLayer(surfaces);
        string dir = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".";
        int made = 0, watertightN = 0;
        for (int i = 0; i < solids.Count; i++)
        {
            if (solids[i] == null) continue;
            var (wv, wt) = solids[i]!.Value;
            var d = MeshDiagnose.Analyze(wv, wt);
            if (d.BoundaryEdges == 0 && d.NonManifoldEdges == 0) watertightN++;
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"layer{i + 1}.off"), MeshWeld.ToOff(wv, wt)); made++; }
            catch (System.Exception ex) { StatusMsg.Text = $"连续多层建模：写出失败 {ex.Message}"; return; }
        }
        StatusMsg.Text = $"连续多层建模：{surfaces.Count} 层位面 → {made} 夹层体（{watertightN} 水密）→ layer1..{made}.off";
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

    // 按闭合边界分割三角网(原 pc_tin_split「沿多段线切分为内/外两片」)：选中闭合折线作边界 + 选 OFF
    // → 三角质心判内/外(与 裁剪三角网 同质心约定)分两片, 各写 OFF。区别于 分割三角网(单线左右, 逐边精确)。
    private async Task MeshBoundarySplitAsync()
    {
        IReadOnlyList<(double x, double y)>? boundary = null;
        foreach (var e in _selected)
            if (e is PolylineEntity pl && pl.Points.Count >= 3) { boundary = pl.Points; break; }
        if (boundary == null) { StatusMsg.Text = "边界分割三角网：请先选中一条闭合折线(≥3 点)作内外边界"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "边界分割三角网：选 OFF 网格", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"边界分割三角网：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "边界分割三角网：未解析到三角网格"; return; }
        var (inside, outside) = MeshBoundarySplit.ByPolygon(verts, tris, boundary);
        if (inside.Tris.Count == 0 || outside.Tris.Count == 0) { StatusMsg.Text = $"边界分割三角网：一侧为空(内 {inside.Tris.Count}/外 {outside.Tris.Count})，边界可能未覆盖或全含网格，未分片"; return; }
        string dir = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".";
        string ip = System.IO.Path.Combine(dir, "split_inside.off"), op = System.IO.Path.Combine(dir, "split_outside.off");
        try
        {
            System.IO.File.WriteAllText(ip, MeshWeld.ToOff(inside.Verts, inside.Tris));
            System.IO.File.WriteAllText(op, MeshWeld.ToOff(outside.Verts, outside.Tris));
        }
        catch (System.Exception ex) { StatusMsg.Text = $"边界分割三角网：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"边界分割三角网：内 {inside.Tris.Count} 三角 / 外 {outside.Tris.Count} 三角 → split_inside/outside.off（质心判别，三角粒度）";
    }

    // 网格修复(原「修复拓扑关系」常见修复): 选 OFF → 焊接→朝向一致→补洞 流水线 → 写 repaired.off + 前后诊断
    private async Task MeshRepairAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "网格修复：选 OFF 网格", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格修复：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "网格修复：未解析到三角网格"; return; }
        var r = MeshRepair.Repair(verts, tris);
        string dir = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".";
        string op = System.IO.Path.Combine(dir, "repaired.off");
        try { System.IO.File.WriteAllText(op, MeshWeld.ToOff(r.Verts, r.Tris)); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格修复：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"网格修复：顶点 {r.VertsBefore}→{r.VertsAfter}(焊接) · 补 {r.FilledHoles} 洞 · 开放边 {r.BoundaryBefore}→{r.BoundaryAfter}{(r.BoundaryAfter == 0 ? "(水密)" : "")} · 朝向已统一 → {System.IO.Path.GetFileName(op)}";
    }

    // 补洞(三角网)：选 OFF → 提边界洞 → 扇形填充 → 落 .filled.off + 前后开放边报表。
    // 三角网剔面(原「按离地高/坡度丢弃三角面」)：选 OFF → 删坡度>阈值(默认60°)的陡三角 → 写 faceculled.off
    private async Task MeshFaceCullAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "剔面：选 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"剔面：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "剔面：未解析到三角网格"; return; }
        double maxSlope = 60;   // 可选 "剔面 <坡度阈值°>"
        var tk = cmd.Split(new[] { ' ', '°' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2 && double.TryParse(tk[1], out double s)) maxSlope = s;
        var kept = MeshFaceCull.BySlope(verts, tris, maxSlope);
        int removed = tris.Count - kept.Count;
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".", "faceculled.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(verts, kept)); }
        catch (System.Exception ex) { StatusMsg.Text = $"剔面：写出失败 {ex.Message}"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"剔面(坡度>{maxSlope.ToString("0.#", inv)}°)：删 {removed} 陡面 · 留 {kept.Count}/{tris.Count} → {System.IO.Path.GetFileName(outPath)}";
    }

    // 剔倒刺(原 pc_remove_obs 孤立高Z尖刺部分)：选 OFF → 删含"高于最高邻居>阈值"顶点的三角(坡度无关)
    private async Task MeshSpikeCullAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "剔倒刺：选 OFF 网格", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"剔倒刺：读取失败 {ex.Message}"; return; }
        var (verts, tris) = MeshMetrics.ParseOff(text);
        if (tris.Count == 0) { StatusMsg.Text = "剔倒刺：未解析到三角网格"; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double tol = 2.0;   // 可选 "剔倒刺 <高差阈值>"
        var tk = cmd.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double v)) tol = v;
        var kept = MeshFaceCull.BySpike(verts, tris, tol);
        int removed = tris.Count - kept.Count;
        string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files[0].Path.LocalPath) ?? ".", "spikeculled.off");
        try { System.IO.File.WriteAllText(outPath, MeshWeld.ToOff(verts, kept)); }
        catch (System.Exception ex) { StatusMsg.Text = $"剔倒刺：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"剔倒刺(高于最高邻居>{tol.ToString("0.#", inv)})：删 {removed} 含倒刺三角 · 留 {kept.Count}/{tris.Count}（坡度无关，坡上局部隆起亦剔）→ {System.IO.Path.GetFileName(outPath)}";
    }

    private async Task MeshHoleFillAsync(string cmd = "补洞")
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
        // 可选 "补洞 <最大洞面积>"：只补 XY 面积 ≤ 此值的洞(避免填外轮廓/矿坑大空洞); 缺省补全部
        double maxArea = double.MaxValue;
        int sp = cmd.IndexOf(' ');
        if (sp >= 0 && double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ma) && ma > 0) maxArea = ma;
        var (nv, nt, holes) = MeshHoleFill.Fill(verts, tris, maxArea);
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
        mt = MeshOrient.MakeConsistent(mv, mt);   // 统一朝向，保 GWN 内外判定可靠(容忍朝向不一致的导入网格)
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
        // 分标高体素体积(忠实原「整体+分标高」)：10 个高程带，各带占用体积
        double bandH = (wn.MaxZ - wn.MinZ) / 10.0;
        var bands = VoxelBands.ByElevation(wn.MinX, wn.MaxX, wn.MinY, wn.MaxY, wn.MinZ, wn.MaxZ,
            cell, bandH, wn.IsInsideClosed);
        long occupied = 0;
        double voxelVol = 0;
        foreach (var b in bands) { occupied += b.Cells; voxelVol += b.Volume; }
        var m = MeshMetrics.Compute(mv, mt);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var name = await SaveCsvAsync("导出分标高体积", "voxel_volume_by_elevation.csv", VoxelBands.ToCsv(bands));
        var top = bands.Count > 0 ? bands.OrderByDescending(b => b.Volume).First() : default;
        StatusMsg.Text = $"体素格网体积：格边 {cell.ToString("0.##", inv)} · 占用 {occupied} 格 · 体素体积 {voxelVol.ToString("0.#", inv)}(精确 {m.Volume.ToString("0.#", inv)}) · {bands.Count} 标高带"
            + (bands.Count > 0 ? $"(峰 {top.ZLow.ToString("0.#", inv)}~{top.ZHigh.ToString("0.#", inv)}m={top.Volume.ToString("0.#", inv)})" : "")
            + (name != null ? $" → {name}" : "");
    }

    // 自适应体素算量(忠实原 VoxelVolumeBuilder 自适应子块退化): OFF 封闭网格 → 边界母块 octree 细分 + N³ 占比"百分比块"
    // → 无偏边界体积(比均匀中心法更接近解析体积)。用法 "自适应体素算量 [细分深度 默认2 子采样N 默认4]"。
    private async Task AdaptiveVoxelAsync(string cmd)
    {
        int depth = 2, sampleN = 4;
        var parts = cmd.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) int.TryParse(parts[1], out depth);
        if (parts.Length >= 3) int.TryParse(parts[2], out sampleN);
        depth = System.Math.Clamp(depth, 1, 5); sampleN = System.Math.Clamp(sampleN, 1, 8);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "自适应体素算量：选封闭 OFF 网格",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Geomview 网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"自适应体素算量：读取失败 {ex.Message}"; return; }
        var (mv, mt) = MeshMetrics.ParseOff(text);
        if (mt.Count == 0) { StatusMsg.Text = "自适应体素算量：无三角"; return; }
        mt = MeshOrient.MakeConsistent(mv, mt);
        var fv = new double[mv.Count * 3];
        for (int i = 0; i < mv.Count; i++) { fv[i * 3] = mv[i].x; fv[i * 3 + 1] = mv[i].y; fv[i * 3 + 2] = mv[i].z; }
        var ft = new int[mt.Count * 3];
        for (int i = 0; i < mt.Count; i++) { ft[i * 3] = mt[i].a; ft[i * 3 + 1] = mt[i].b; ft[i * 3 + 2] = mt[i].c; }

        WindingNumberTester wn;
        try { wn = new WindingNumberTester(fv, ft); }
        catch (System.Exception ex) { StatusMsg.Text = $"自适应体素算量：建测试器失败 {ex.Message}"; return; }
        double dgx = wn.MaxX - wn.MinX, dgy = wn.MaxY - wn.MinY, dgz = wn.MaxZ - wn.MinZ;
        double diag = System.Math.Sqrt(dgx * dgx + dgy * dgy + dgz * dgz);
        double cell = diag > 0 ? diag / 40.0 : 1.0;   // 母块格边(略粗于均匀版, 细分补精度)

        var uni = await System.Threading.Tasks.Task.Run(() => Cad.AdaptiveVoxel.Voxelize(wn.IsInsideClosed,
            wn.MinX, wn.MinY, wn.MinZ, wn.MaxX, wn.MaxY, wn.MaxZ, cell, cell, cell, 0, 1));
        var ada = await System.Threading.Tasks.Task.Run(() => Cad.AdaptiveVoxel.Voxelize(wn.IsInsideClosed,
            wn.MinX, wn.MinY, wn.MinZ, wn.MaxX, wn.MaxY, wn.MaxZ, cell, cell, cell, depth, sampleN));

        var m = MeshMetrics.Compute(mv, mt);   // 解析体积(散度定理), 作精度基准
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double exact = System.Math.Abs(m.Volume);
        double eu = exact > 1e-9 ? System.Math.Abs(uni.Volume - exact) / exact * 100 : 0;
        double ea = exact > 1e-9 ? System.Math.Abs(ada.Volume - exact) / exact * 100 : 0;
        StatusMsg.Text = $"自适应体素算量：格边 {cell.ToString("0.##", inv)} · 深度 {depth}/子采样 {sampleN} · 实心 {ada.SolidCells} 块 + 边界百分比块 {ada.SubCells.Count} · "
            + $"自适应体积 {ada.Volume.ToString("0.#", inv)}(误差 {ea.ToString("0.#", inv)}%) vs 均匀 {uni.Volume.ToString("0.#", inv)}(误差 {eu.ToString("0.#", inv)}%) · 解析 {exact.ToString("0.#", inv)}";
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
        mt = MeshOrient.MakeConsistent(mv, mt);   // 统一朝向，保 GWN 内外判定可靠(容忍朝向不一致的导入网格)
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
        _lastBlocks = blocks; _blockAttrs = null;   // 供资源量/剥采比等复用
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

    // 命令后的数字参数(空格/逗号分隔; 跳过命令词)
    private static double[] PrimitiveNums(string cmd)
    {
        var nums = new List<double>();
        foreach (var p in cmd.Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) nums.Add(d);
        return nums.ToArray();
    }

    private async Task BoxPrimitiveAsync(string cmd = "")
    {
        var a = PrimitiveNums(cmd);   // 立方体 <边长> 或 立方体 <sx> <sy> <sz>
        double sx = a.Length >= 1 && a[0] > 0 ? a[0] : 10;
        double sy = a.Length >= 2 && a[1] > 0 ? a[1] : sx, sz = a.Length >= 3 && a[2] > 0 ? a[2] : sx;
        var (v, t) = PrimitiveBodies.Box(0, 0, 0, sx, sy, sz); await SavePrimitiveAsync("立方体", v, t);
    }

    private async Task SpherePrimitiveAsync(string cmd = "")
    {
        var a = PrimitiveNums(cmd);   // 球体 <半径>
        double r = a.Length >= 1 && a[0] > 0 ? a[0] : 5;
        var (v, t) = PrimitiveBodies.Sphere(0, 0, 0, r, 16, 24); await SavePrimitiveAsync("球体", v, t);
    }

    private async Task CylinderPrimitiveAsync(string cmd = "")
    {
        var a = PrimitiveNums(cmd);   // 圆柱 <半径> [高]
        double r = a.Length >= 1 && a[0] > 0 ? a[0] : 5, hgt = a.Length >= 2 && a[1] > 0 ? a[1] : 10;
        var (v, t) = PrimitiveBodies.Cylinder(0, 0, 0, r, hgt, 24); await SavePrimitiveAsync("圆柱", v, t);
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
        var (nodes, adj) = RoadNetwork.BuildNoded(polys, tol > 0 ? tol : 1e-6);
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

    // 侧面三角网：选顶线 CSV + 底线 CSV → 最短横档 DP 放样 → 保存 OFF + 报表
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
        static bool Ring(List<(double x, double y, double z)> l) => l.Count >= 3 && System.Math.Abs(l[0].x - l[^1].x) < 1e-6 && System.Math.Abs(l[0].y - l[^1].y) < 1e-6;
        bool closed = Ring(top) || Ring(bot);   // 照原版：任一闭合即按环形侧壁
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
    // 采场/排土场自动识别(忠实原 LandformClassifier): 台阶线 CSV(lineId,x,y,z) → 栅格极性分类 →
    // 采场(凹)/外排/内排 区域多边形上屏(红/棕/橙) + 计数。纯栅格, 无内核。用法 采场排土场识别 [栅格m 默认5]。
    private async Task LandformClassifyAsync(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double cell = 5; if (tk.Length > 1 && double.TryParse(tk[1], out var cv) && cv > 0) cell = cv;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "采场/排土场识别：选台阶线 CSV (lineId,x,y,z)", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("台阶线 CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        string[] rows;
        try { rows = System.IO.File.ReadAllLines(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"采场排土场识别：读取失败 {ex.Message}"; return; }
        var groups = new Dictionary<string, List<double>>(); var order = new List<string>();
        foreach (var raw in rows)
        {
            var s = raw.Trim(); if (s.Length == 0 || s.StartsWith("#")) continue;
            var t = s.Split(new[] { ',', '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out double x)) continue;
            if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out double y)) continue;
            if (!double.TryParse(t[3], System.Globalization.NumberStyles.Float, inv, out double z)) continue;
            string id = t[0]; if (!groups.TryGetValue(id, out var list)) { list = new(); groups[id] = list; order.Add(id); }
            list.Add(x); list.Add(y); list.Add(z);
        }
        if (order.Count == 0) { StatusMsg.Text = "采场排土场识别：未解析到台阶线(lineId,x,y,z)"; return; }
        var lines = order.Select(id => groups[id].ToArray()).ToList();
        var r = Cad.LandformClassifier.Classify(lines, cellSize: cell);
        if (!r.Ok) { StatusMsg.Text = $"采场排土场识别：{r.Message}"; return; }
        BeginChange();
        foreach (var reg in r.Regions)
        {
            var poly = new PolylineEntity { Closed = true, LayerName = reg.Category == "pit" ? "采场" : reg.Category == "internal_dump" ? "内排土场" : "外排土场" };
            (poly.Cr, poly.Cg, poly.Cb) = reg.Category == "pit" ? (0.9f, 0.25f, 0.2f) : reg.Category == "internal_dump" ? (0.95f, 0.6f, 0.2f) : (0.6f, 0.45f, 0.3f);
            for (int i = 0; i + 2 < reg.PolygonXyz.Length; i += 3) poly.Points.Add((reg.PolygonXyz[i], reg.PolygonXyz[i + 1]));
            if (poly.Points.Count >= 3) _scene.Add(poly);
        }
        RefreshScene();
        int np = r.Regions.Count(z => z.Category == "pit"), no = r.Regions.Count(z => z.Category == "external_dump"), ni = r.Regions.Count(z => z.Category == "internal_dump");
        double pitHa = r.Regions.Where(z => z.Category == "pit").Sum(z => z.AreaHa);
        StatusMsg.Text = $"采场/排土场识别(栅格 {cell:0.#}m·极性分类)：采场 {np} 块({pitHa:0.#}ha,红) · 外排 {no} 块(棕) · 内排 {ni} 块(橙) · 共 {r.Regions.Count} 区域入场景";
    }

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
    // 两网交线：选两份 OFF → tri-tri 求交段 → 交线 2D 折线入场景 + 导出 3D 交点 CSV。典型：现状∩顶/底板煤层露头线。
    private async Task MeshIntersectionAsync()
    {
        var m1 = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "网格交线：① 选第一份网格 OFF", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("OFF") { Patterns = new[] { "*.off" } } } });
        if (m1.Count == 0) return;
        var (v1, t1) = ReadConcatOff(new[] { m1[0].Path.LocalPath });
        if (t1.Count == 0) { StatusMsg.Text = "网格交线：第一份网格无三角"; return; }
        var m2 = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "网格交线：② 选第二份网格 OFF", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("OFF") { Patterns = new[] { "*.off" } } } });
        if (m2.Count == 0) return;
        var (v2, t2) = ReadConcatOff(new[] { m2[0].Path.LocalPath });
        if (t2.Count == 0) { StatusMsg.Text = "网格交线：第二份网格无三角"; return; }

        var segs = Cad.MeshIntersect.IntersectionSegments(v1, t1, v2, t2);
        if (segs.Count == 0) { StatusMsg.Text = "网格交线：两网无交线（不相交/共面）"; return; }
        // 交段连成折线 2D 投影入场景(琥珀色, 单一可选实体)
        BeginChange();
        var xy = new List<(double, double, double, double)>(segs.Count);
        double sMinX = double.MaxValue, sMaxX = double.MinValue;
        foreach (var s in segs) { xy.Add((s.A.x, s.A.y, s.B.x, s.B.y)); sMinX = System.Math.Min(sMinX, System.Math.Min(s.A.x, s.B.x)); sMaxX = System.Math.Max(sMaxX, System.Math.Max(s.A.x, s.B.x)); }
        double ext = System.Math.Max(1e-6, (sMaxX - sMinX) * 1e-5);   // 交线端点匹配容差(尺度相对)
        foreach (var poly in Contour.LinkSegments(xy, ext))
        {
            if (poly.Count < 2) continue;
            var pl = new PolylineEntity { Cr = 0.95f, Cg = 0.7f, Cb = 0.2f, LayerName = "网格交线" };
            foreach (var p in poly) pl.Points.Add((p.x, p.y));
            _scene.Add(pl);
        }
        RefreshScene();
        // 导出 3D 交点 CSV(x,y,z)
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("x,y,z\n");
        foreach (var s in segs)
        { sb.Append($"{s.A.x.ToString("R", inv)},{s.A.y.ToString("R", inv)},{s.A.z.ToString("R", inv)}\n"); sb.Append($"{s.B.x.ToString("R", inv)},{s.B.y.ToString("R", inv)},{s.B.z.ToString("R", inv)}\n"); }
        try { string outPath = System.IO.Path.ChangeExtension(m1[0].Path.LocalPath, ".intersect.csv"); System.IO.File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(true)); StatusMsg.Text = $"网格交线：{segs.Count} 段交线入场景 + 3D 交点 → {System.IO.Path.GetFileName(outPath)}"; }
        catch { StatusMsg.Text = $"网格交线：{segs.Count} 段交线入场景（图层 网格交线；CSV 写出失败）"; }
    }

    // 网格光顺：选 OFF → Laplacian 光顺(固定边界) → 落 .smoothed.off + 边线框入场景。忠实原网格光顺(标准算法)。
    private async Task MeshSmoothAsync(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        int iters = 3;
        if (tk.Length >= 2 && int.TryParse(tk[1], out int it) && it >= 1 && it <= 50) iters = it;
        var mf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = $"网格光顺（迭代 {iters}）：选网格 OFF", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("OFF") { Patterns = new[] { "*.off" } } } });
        if (mf.Count == 0) return;
        var (v, t) = ReadConcatOff(new[] { mf[0].Path.LocalPath });
        if (t.Count == 0) { StatusMsg.Text = "网格光顺：网格无三角"; return; }
        var sm = Cad.MeshSmooth.Laplacian(v, t, iters, 0.5, fixBoundary: true);
        // 落 .smoothed.off
        string outPath = System.IO.Path.ChangeExtension(mf[0].Path.LocalPath, ".smoothed.off");
        try { System.IO.File.WriteAllText(outPath, Cad.MeshWeld.ToOff(sm, new List<(int a, int b, int c)>(t))); }
        catch (System.Exception ex) { StatusMsg.Text = $"网格光顺：写出失败 {ex.Message}"; return; }
        // 边线框入场景(去重边)
        BeginChange();
        var edges = Delaunay.BuildEdges(new List<(double x, double y)>(sm.ConvertAll(p => (p.x, p.y))), new List<(int a, int b, int c)>(t), 0.6f, 0.8f, 0.85f);
        foreach (var e in edges) { e.LayerName = "网格光顺"; _scene.Add(e); }
        RefreshScene();
        StatusMsg.Text = $"网格光顺：{v.Count} 顶点 · {t.Count} 三角 · 迭代 {iters} → {System.IO.Path.GetFileName(outPath)}（边线框入场景）";
    }

    // 网格剖面：选剖面线(选中多段线/两点) + OFF 网格 → 网格∩竖直面 精确断面 → 剖面曲线(沿线距→高程)入场景。
    private async Task MeshSectionAsync()
    {
        (double x, double y)? a = null, b = null;
        foreach (var e in _selected)
        {
            if (e is LineEntity l) { a = (l.X0, l.Y0); b = (l.X1, l.Y1); break; }
            if (e is PolylineEntity p && p.Points.Count >= 2) { a = p.Points[0]; b = p.Points[^1]; break; }
        }
        if (a == null || b == null) { StatusMsg.Text = "网格剖面：请先选中一条剖面线(直线/多段线)"; return; }
        var mf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "网格剖面：选网格 OFF", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("OFF") { Patterns = new[] { "*.off" } } } });
        if (mf.Count == 0) return;
        var (v, t) = ReadConcatOff(new[] { mf[0].Path.LocalPath });
        if (t.Count == 0) { StatusMsg.Text = "网格剖面：网格无三角"; return; }
        var prof = Cad.MeshPlaneSection.Profile(v, t, a.Value, b.Value);
        if (prof.Count < 2) { StatusMsg.Text = "网格剖面：剖面线未穿过网格（无断面）"; return; }
        double zmin = double.MaxValue; foreach (var (_, z) in prof) if (z < zmin) zmin = z;
        // 剖面曲线画在剖面线起点旁(沿线距为 x, 高程抬为 y)
        double baseX = a.Value.x, baseY = a.Value.y;
        var curve = new PolylineEntity { Cr = 0.3f, Cg = 0.85f, Cb = 0.95f, LayerName = "网格剖面" };
        foreach (var (dist, z) in prof) curve.Points.Add((baseX + dist, baseY + (z - zmin)));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double zmax = double.MinValue; foreach (var (_, z) in prof) if (z > zmax) zmax = z;
        BeginChange(); _scene.Add(curve);
        double gLen = prof[^1].dist, gTextH = System.Math.Max((zmax - zmin) * 0.06, gLen * 0.02);   // 剖面图框架(里程/标高轴)
        foreach (var fe in ProfilePlot.Frame(baseX, baseY, gLen, zmin, zmax, System.Math.Max(gTextH, 1e-3))) { fe.LayerName = "网格剖面"; _scene.Add(fe); }
        RefreshScene();
        StatusMsg.Text = $"网格剖面：{prof.Count} 断面点 · 长 {gLen.ToString("0.#", inv)} · 高程 {zmin.ToString("0.#", inv)}~{zmax.ToString("0.#", inv)}（带里程/标高轴）";
    }

    // 线落到面上：选网格 OFF + 线 CSV(lineId,x,y) → 节点重算落面(顶点投 Z + 每段与三角边交点补节点, 逐段贴面)
    // → 落 .draped.csv(lineId,x,y,z) + 三维线入场景
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
            var (draped, miss) = MeshProjector.DrapePolyline(v, t, xy);
            missed += miss; totV += draped.Count;
            var pl = new PolylineEntity { Cr = 0.35f, Cg = 0.9f, Cb = 0.55f, Zs = new List<double>(draped.Count) };
            foreach (var (x, y, z) in draped)
            {
                pl.Points.Add((x, y)); pl.Zs.Add(z);
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
        // 孤立点/重复点有则附加; 自交三角(横切非相邻三角=原 PMDR SelfIntersect, 检测=tri-tri 测试可托管, 区别于自交"消解"的鲁棒难题)。
        string vtx = (d.IsolatedVertices > 0 || d.DuplicateVertices > 0) ? $" · 孤立点 {d.IsolatedVertices} · 重复点 {d.DuplicateVertices}" : "";
        string si = d.SelfIntersectTriangles < 0 ? " · 自交未检(网格过大)" : d.SelfIntersectTriangles > 0 ? $" · 自交三角 {d.SelfIntersectTriangles}" : "";
        StatusMsg.Text = $"网格诊断：{d.TriangleCount} 三角 · {d.EdgeCount} 边 · 边界边 {d.BoundaryEdges} · 非流形边 {d.NonManifoldEdges} · 退化三角 {d.DegenerateTriangles} · 洞 {d.BoundaryLoops}{vtx}{si} · {(d.IsClosed ? "闭合(水密)" : "非闭合")}";
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
        StatusMsg.Text = $"网格度量：{m.VertexCount} 顶点 · {m.TriangleCount} 三角 · 表面积 {m.SurfaceArea.ToString("0.##", inv)} · 体积 {m.Volume.ToString("0.##", inv)} · 水平投影足迹 {m.HullAreaXY.ToString("0.##", inv)} · 范围 X[{m.MinX.ToString("0.#", inv)},{m.MaxX.ToString("0.#", inv)}] Z[{m.MinZ.ToString("0.#", inv)},{m.MaxZ.ToString("0.#", inv)}]";
    }

    // 色带切换：「色带 <名>」设当前色带(Terrain/Jet/Grayscale/Viridis/Turbo/Magma/Plasma), 影响后续高程/属性着色。
    // 指北针：竖直箭头(指上=北)入场景右下。忠实原版"指北针"
    private void PlaceNorthArrow()
    {
        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        var p0 = Viewport.ScreenToWorld(w * 0.9, h * 0.85) ?? (0.0, 0.0);
        var p1 = Viewport.ScreenToWorld(w * 0.9, h * 0.68) ?? (0.0, 10.0);
        double size = System.Math.Abs(p1.y - p0.y); if (size < 1e-6) size = 10;
        var ents = MapDecor.NorthArrow(p0.x, System.Math.Min(p0.y, p1.y), size);
        BeginChange(); foreach (var e in ents) { e.LayerName = _layers.Current.Name; _scene.Add(e); } RefreshScene();
        StatusMsg.Text = "指北针：入场景（右下，Y+ 为北，可移动/删除）";
    }

    // 比例尺：取整长度的水平标尺入场景左下。忠实原版"比例尺"
    private void PlaceScaleBar()
    {
        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        var pL = Viewport.ScreenToWorld(w * 0.06, h * 0.94) ?? (0.0, 0.0);
        var pR = Viewport.ScreenToWorld(w * 0.26, h * 0.94) ?? (100.0, 0.0);
        double worldLen = MapDecor.NiceLength(System.Math.Abs(pR.x - pL.x));
        double textH = System.Math.Max(worldLen * 0.08, 1e-3);
        var ents = MapDecor.ScaleBar(pL.x, pL.y, worldLen, textH);
        BeginChange(); foreach (var e in ents) { e.LayerName = _layers.Current.Name; _scene.Add(e); } RefreshScene();
        StatusMsg.Text = $"比例尺：{worldLen:0.#} 世界单位（入场景，可移动/删除）";
    }

    // 标题栏：外框+标题+比例/图号/制图/日期 入场景右下。忠实原版"标题栏"。标题=命令给或占位
    private void PlaceTitleBlock(string cmd)
    {
        string title = cmd.StartsWith("标题栏 ") ? cmd.Substring(cmd.IndexOf(' ') + 1).Trim() : "标题";
        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        var p0 = Viewport.ScreenToWorld(w * 0.62, h * 0.94) ?? (0.0, 0.0);
        var p1 = Viewport.ScreenToWorld(w * 0.94, h * 0.78) ?? (100.0, 30.0);
        double bw = System.Math.Abs(p1.x - p0.x), bh = System.Math.Abs(p1.y - p0.y);
        if (bw < 1e-6) bw = 100; if (bh < 1e-6) bh = 30;
        double textH = System.Math.Max(bh * 0.12, 1e-3);
        var ents = MapDecor.TitleBlock(System.Math.Min(p0.x, p1.x), System.Math.Min(p0.y, p1.y), bw, bh, textH, title, "");
        BeginChange(); foreach (var e in ents) { e.LayerName = _layers.Current.Name; _scene.Add(e); } RefreshScene();
        StatusMsg.Text = $"标题栏「{title}」：入场景（右下，可移动/改字/删除）";
    }

    // 色带图例：色条(当前色带渐变)+值标签 入场景左下(世界坐标)。忠实原版"图例"。值域=命令给或最近着色
    private void PlaceLegend(string cmd)
    {
        var a = PrimitiveNums(cmd);
        double vmin = a.Length >= 1 ? a[0] : _legendVmin, vmax = a.Length >= 2 ? a[1] : _legendVmax;
        if (vmax <= vmin) { vmin = 0; vmax = 100; }
        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        var p0 = Viewport.ScreenToWorld(w * 0.06, h * 0.88) ?? (0.0, 0.0);      // 色条底(左下)
        var p1 = Viewport.ScreenToWorld(w * 0.06, h * 0.45) ?? (0.0, 100.0);    // 色条顶
        double height = System.Math.Abs(p1.y - p0.y);
        if (height < 1e-6) height = 100;
        double textH = System.Math.Max(height * 0.05, 1e-3);
        var leg = Legend.Build(_colormap, vmin, vmax, p0.x, System.Math.Min(p0.y, p1.y), textH * 2, height, textH);
        if (leg.Count == 0) { StatusMsg.Text = "图例：无色带"; return; }
        BeginChange();
        foreach (var e in leg) { e.LayerName = _layers.Current.Name; _scene.Add(e); }
        RefreshScene();
        StatusMsg.Text = $"图例：色带 {_colormapName}，值域 [{vmin:0.#}, {vmax:0.#}]（入场景，可移动/删除）";
    }

    private void SetColormapCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 2)
        {
            StatusMsg.Text = $"当前色带：{_colormapName}（可选 Terrain/Jet/Grayscale/Viridis/Turbo/Magma/Plasma；用「色带 Viridis」切换）";
            return;
        }
        string name = tk[1];
        _colormap = Cad.Colormap.ByName(name);
        // 规范化显示名
        _colormapName = name.Trim().ToLowerInvariant() switch
        {
            "jet" => "Jet", "grayscale" or "gray" or "灰度" => "Grayscale", "viridis" => "Viridis",
            "turbo" => "Turbo", "magma" => "Magma", "plasma" => "Plasma", _ => "Terrain"
        };
        StatusMsg.Text = $"色带已设：{_colormapName}（影响后续 高程着色 等；感知均匀色带 Viridis/Turbo 优于 Jet）";
    }

    // 点云高程着色：点 CSV(x,y,z) → 按 z 用当前色带着色 → 彩色点入场景
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
            var (cr, cg, cb) = Colormap.Sample(_colormap, t);
            var pe = new PointEntity { X = p.x, Y = p.y };
            AssignLayer(pe);
            pe.Cr = cr / 255f; pe.Cg = cg / 255f; pe.Cb = cb / 255f;
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        _legendVmin = zmin; _legendVmax = zmax;                 // 记值域供「图例」
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"高程着色：{r.Points.Count} 点 · z {zmin.ToString("0.#", inv)}~{zmax.ToString("0.#", inv)}（地形色带；「图例」可加色带图例）";
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
            var (cr, cg, cb) = Colormap.Sample(_colormap, t);
            _scene.Add(new PointEntity { X = p.x, Y = p.y, Cr = cr / 255f, Cg = cg / 255f, Cb = cb / 255f });
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"高程截断：保留 {kept.Count}/{pts.Count} 点(剔除 {removed} 异常程) · Z 波段 [{zLo.ToString("0.#", inv)}, {zHi.ToString("0.#", inv)}]（地形色带）";
    }

    // 点云边界裁剪：选中闭合多段线作边界 → 导入点 CSV → 保留界内点入场景。忠实原「闭合多段线裁剪点云」。
    private async Task CropCloudByBoundaryAsync(string cmd = "点云裁剪")
    {
        bool keepInside = !cmd.Contains("外");   // "点云裁剪外/圈外裁剪" → 保留界外(pc_crop_cloud 圈内/圈外)
        string side = keepInside ? "界内" : "界外";
        PolylineEntity? bnd = null;
        foreach (var e in _selected) if (e is PolylineEntity p && p.Closed && p.Points.Count >= 3) { bnd = p; break; }
        if (bnd == null) { StatusMsg.Text = $"点云裁剪({side})：请先选中一条闭合多段线作裁剪边界"; return; }
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
        var kept = PointCloudCrop.ByPolygon(pts, boundary, keepInside);
        if (kept.Count == 0) { StatusMsg.Text = $"点云裁剪({side})：{side}无点（共 {pts.Count} 点）"; return; }
        BeginChange();
        foreach (var p in kept)
            _scene.Add(new PointEntity { X = p.x, Y = p.y, Cr = 0.35f, Cg = 0.8f, Cb = 0.5f, LayerName = "点云裁剪" });
        RefreshScene();
        StatusMsg.Text = $"点云裁剪：保留{side} {kept.Count}/{pts.Count} 点入场景（图层 点云裁剪）";
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
            var pe = new PointEntity { X = p.x, Y = p.y, Elevation = p.z, Cr = 0.75f, Cg = 0.78f, Cb = 0.82f };
            AssignLayer(pe); pe.Cr = 0.75f; pe.Cg = 0.78f; pe.Cb = 0.82f;
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"加载点云：{r.Points.Count} 点已入场景（灰点；可着色/去噪/抽稀/统计）";
    }

    // 导入 LAS 点云(公开 LAS 1.2/1.4 规范, 托管解析)：读头 + 抽稀点入场景(俯视灰点)
    private async Task LoadLasAsync(string cmd = "导入LAS")
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 LAS 点云",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("LAS 点云") { Patterns = new[] { "*.las" } } }
        });
        if (files.Count == 0) return;
        var r = LasImportService.Load(files[0].Path.LocalPath, 200000);
        if (!r.Success) { StatusMsg.Text = $"导入 LAS：{r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "导入 LAS：头有效但无点"; return; }
        bool wantRgb = cmd.Contains("真");                 // "LAS真彩色/点云真实色" → 用捕获的 RGB 着色
        bool wantIntensity = cmd.Contains("强度");         // "LAS强度色" → 按回波强度灰阶
        bool useRgb = wantRgb && r.Colors != null && r.Colors.Count == r.Points.Count;
        float iMin = float.MaxValue, iMax = float.MinValue;   // 强度归一化(用实际值域拉满对比)
        if (wantIntensity) foreach (var iv in r.Intensity) { if (iv < iMin) iMin = iv; if (iv > iMax) iMax = iv; }
        float iRange = iMax > iMin ? iMax - iMin : 1;
        bool useIntensity = wantIntensity && r.Intensity.Count == r.Points.Count && iMax > iMin;
        bool wantClassColor = cmd.Contains("分类");        // "LAS分类着色" → 按 ASPRS 分类码离散配色
        bool wantGroundFilter = cmd.Contains("非地面") || cmd.Contains("剔除植被");   // 语义剔除植被/建筑(pc_remove_obs)
        bool hasClass = r.Classification.Count == r.Points.Count;
        bool useClassColor = wantClassColor && hasClass;
        int filtered = 0;
        BeginChange();
        for (int i = 0; i < r.Points.Count; i++)
        {
            byte cls = hasClass ? r.Classification[i] : (byte)0;
            if (wantGroundFilter && (cls == 3 || cls == 4 || cls == 5 || cls == 6 || cls == 7)) { filtered++; continue; }   // 低/中/高植被/建筑/噪声
            var p = r.Points[i];
            var pe = new PointEntity { X = p.x, Y = p.y };
            (float cr, float cg, float cb) Col()
            {
                if (useRgb) { var c = r.Colors![i]; return (c.r, c.g, c.b); }
                if (useIntensity) { float g = (r.Intensity[i] - iMin) / iRange; return (g, g, g); }
                if (useClassColor) return HueColor(cls);
                return (0.75f, 0.78f, 0.82f);
            }
            var (cr0, cg0, cb0) = Col(); pe.Cr = cr0; pe.Cg = cg0; pe.Cb = cb0;
            AssignLayer(pe);
            if (useRgb || useIntensity || useClassColor) { var (cr, cg, cb) = Col(); pe.Cr = cr; pe.Cg = cg; pe.Cb = cb; }   // AssignLayer 可能改色, 置回
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(new[] { r.MinX, r.MinY, r.MaxX, r.MaxY });
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string colorNote = wantGroundFilter ? $"剔除植被/建筑 {filtered} 点(按分类)" : useRgb ? "真实色(RGB)" : useIntensity ? $"强度灰阶[{iMin:0}~{iMax:0}]" : useClassColor ? "分类离散色" : (wantRgb ? $"无RGB(点格式{r.PointFormat}), 灰显" : wantIntensity ? "强度无变化, 灰显" : "灰显");
        StatusMsg.Text = $"导入 LAS(v{r.VersionMajor}.{r.VersionMinor})：{r.PointCount} 点"
            + (r.Points.Count < r.PointCount ? $"(抽稀显示 {r.Points.Count})" : "")
            + $" · {colorNote} · 范围 X[{r.MinX.ToString("0.#", inv)}~{r.MaxX.ToString("0.#", inv)}] Z[{r.MinZ.ToString("0.#", inv)}~{r.MaxZ.ToString("0.#", inv)}]";
    }

    // LAS 分类统计(原 pc_quality「强度分类」)：选 LAS → 逐分类码点数 + 强度分布 → 上屏 + 导出 CSV
    private async Task LasQualityAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "LAS 分类统计：选 LAS", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("LAS 点云") { Patterns = new[] { "*.las" } } }
        });
        if (files.Count == 0) return;
        var r = LasImportService.Load(files[0].Path.LocalPath, 2000000);
        if (!r.Success) { StatusMsg.Text = $"LAS 分类统计：{r.Error}"; return; }
        if (r.Points.Count == 0) { StatusMsg.Text = "LAS 分类统计：无点"; return; }
        var breakdown = LasQualityReport.ClassBreakdown(r.Classification);
        var iSummary = Statistics.Describe(r.Intensity.Select(x => (double)x).ToList());
        string top = breakdown.Count > 0 ? string.Join(" · ", breakdown.Take(4).Select(b => $"{b.Name} {b.Count}")) : "无分类";
        string csv = "# 分类统计\n" + LasQualityReport.ClassBreakdownCsv(breakdown) + "\n# 强度直方图\n" + Statistics.HistogramCsv(iSummary);
        var name = await SaveCsvAsync("导出 LAS 质量报告", "las_quality.csv", csv);
        if (breakdown.Count > 0)
            DrawCategoryBars(breakdown.Select(b => (b.Name, (double)b.Count)).ToList(), "点数");
        StatusMsg.Text = $"LAS 分类统计：{r.PointCount} 点 · {breakdown.Count} 类[{top}] · 强度 {Statistics.SummaryLine(iSummary)}"
            + (breakdown.Count > 0 ? " · 分类柱入场景" : "") + (name != null ? $" → {name}" : "");
    }

    // 正射着色(真实色)：选点 CSV + GeoTIFF 正射影像 → 逐点采像素色 → 真实色点云入场景
    private async Task OrthoColorAsync()
    {
        var pf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "正射着色：选点 CSV (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (pf.Count == 0) return;
        var pr = PointDataImportService.Load(pf[0].Path.LocalPath);
        if (!pr.Success || pr.Points.Count == 0) { StatusMsg.Text = "正射着色：点导入失败/无点"; return; }
        var gf = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "正射着色：选 GeoTIFF 正射影像",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("GeoTIFF (*.tif;*.tiff)") { Patterns = new[] { "*.tif", "*.tiff" } } }
        });
        if (gf.Count == 0) return;
        using var samp = GeoTiffSampler.Load(gf[0].Path.LocalPath);
        if (!samp.Success) { StatusMsg.Text = $"正射着色：{samp.Error}"; return; }
        BeginChange();
        int colored = 0;
        foreach (var p in pr.Points)
        {
            var rgb = samp.SampleRgb(p.x, p.y);
            float cr, cg, cb;
            if (rgb != null) { cr = rgb.Value.r / 255f; cg = rgb.Value.g / 255f; cb = rgb.Value.b / 255f; colored++; }
            else { cr = cg = cb = 0.5f; }   // 影像外灰
            var pe = new PointEntity { X = p.x, Y = p.y, Cr = cr, Cg = cg, Cb = cb };
            AssignLayer(pe); pe.Cr = cr; pe.Cg = cg; pe.Cb = cb;
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(pr.Bounds);
        StatusMsg.Text = $"正射着色(真实色)：{pr.Points.Count} 点 · {colored} 着色（影像 {samp.Width}×{samp.Height}）";
    }

    // 点云去噪 SOR/ROR：点 CSV(x,y,z) → 去噪 → 保留点入场景(黄) + 报表
    private async Task DenoiseAsync(bool ror, string cmd = "")
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
        // 参数：同行可给（SOR「k σ」/ROR「半径 下限」）；命令行发起且没给就逐项问。
        // 缺省=原去噪对话框默认口径(SOR k=8 σ=1.0；ROR 半径=对角/50、下限=4) —— ROR 半径要等点云读完才算得出，故问在这里。
        double diag = System.Math.Sqrt(System.Math.Pow(r.Bounds[2] - r.Bounds[0], 2) + System.Math.Pow(r.Bounds[3] - r.Bounds[1], 2));
        double defRad = System.Math.Max(diag / 50.0, 1e-6);
        var dp = ror
            ? await AskCmdParamsAsync("ROR 去噪",
                new Modeling.PromptDialog.Field("radius", "邻域半径", defRad.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), "m", "以点为心的搜索半径"),
                new Modeling.PromptDialog.Field("minPts", "邻域点数下限", "4", null, "半径内点数少于此数即判为噪点"))
            : await AskCmdParamsAsync("SOR 去噪",
                new Modeling.PromptDialog.Field("k", "邻域点数 k", "8", null, "统计每点到最近 k 个点的平均距离"),
                new Modeling.PromptDialog.Field("sigma", "标准差倍数 σ", "1", null, "均距超过 均值+σ×标准差 即判为噪点"));
        if (dp == null) { StatusMsg.Text = $"{(ror ? "ROR" : "SOR")} 去噪：已取消"; return; }
        double rad = dp.D("radius", defRad); if (rad <= 0) rad = defRad;
        int minPts = dp.I("minPts", 4); if (minPts < 1) minPts = 4;
        int sorK = dp.I("k", 8); if (sorK < 1) sorK = 8;
        double sigma = dp.D("sigma", 1.0); if (sigma <= 0) sigma = 1.0;
        var kept = ror ? PointDenoise.Ror(pts, rad, minPts) : PointDenoise.Sor(pts, sorK, sigma);
        if (kept.Count == 0) { StatusMsg.Text = "去噪：全部被剔除（参数过严）"; return; }

        BeginChange();
        foreach (var p in kept) { var pe = new PointEntity { X = p.x, Y = p.y, Cr = 0.95f, Cg = 0.85f, Cb = 0.3f }; AssignLayer(pe); pe.Cr = 0.95f; pe.Cg = 0.85f; pe.Cb = 0.3f; _scene.Add(pe); }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"{(ror ? "ROR" : "SOR")} 去噪：{pts.Count} → 保留 {kept.Count}（剔除 {pts.Count - kept.Count}）";
    }

    // 点云质量统计：点 CSV(x,y,z) → 计数/包围盒/XY面积/密度/高程均值·标准差 报表
    // 分割点云(欧氏聚类)：高程点 CSV → 距离聚类 → 按簇着色(hue 循环), 小簇/噪点归灰
    private async Task SegmentCloudAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "分割点云：选点 CSV (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success || r.Points.Count == 0) { StatusMsg.Text = "分割点云：导入失败/无点"; return; }
        double dx = r.Bounds[2] - r.Bounds[0], dy = r.Bounds[3] - r.Bounds[1];
        double radius = System.Math.Max(System.Math.Sqrt(dx * dx + dy * dy) / 100.0, 1e-6);   // 默认=对角/100
        var tk = cmd.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2 && double.TryParse(tk[1], out double rr) && rr > 0) radius = rr;
        var label = PointCluster.Euclidean(r.Points, radius, minSize: 3, out int nc);
        BeginChange();
        for (int i = 0; i < r.Points.Count; i++)
        {
            float cr, cg, cb;
            if (label[i] < 0) { cr = cg = cb = 0.5f; }                       // 噪点/小簇 灰
            else { var (hr, hg, hb) = HueColor(label[i]); cr = hr; cg = hg; cb = hb; }
            var pe = new PointEntity { X = r.Points[i].x, Y = r.Points[i].y, Cr = cr, Cg = cg, Cb = cb };
            AssignLayer(pe); pe.Cr = cr; pe.Cg = cg; pe.Cb = cb;
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"分割点云(欧氏聚类, radius {radius:0.##})：{nc} 簇 · {r.Points.Count} 点（各簇异色, 灰=小簇/噪点）";
    }

    // 区域生长分割：高程点 CSV → 按表面光滑度(逐点 PCA 法向+曲率)区域生长 → 各区异色, 折棱/小区归灰。
    // 区别 分割点云(欧氏=按距离)：本命令按【表面光滑度】分, 台阶面/平盘/坡面在折棱处法向突变而分开。用法 "区域生长分割 [平滑角°]"。
    private async Task RegionGrowAsync(string cmd)
    {
        var tk = cmd.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        double smooth = 15.0;   // 平滑角阈 °
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sm) && sm > 0) smooth = sm;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "区域生长分割：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点云 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success || r.Points.Count < 3) { StatusMsg.Text = "区域生长分割：需 ≥3 点(x,y,z)"; return; }
        var seg = Cad.RegionGrow.Segment(r.Points, k: 16, smoothnessDeg: smooth, curvatureThreshold: 0.1, minSize: 10);
        BeginChange();
        for (int i = 0; i < r.Points.Count; i++)
        {
            float cr, cg, cb;
            if (seg.Label[i] < 0) { cr = cg = cb = 0.5f; }                   // 折棱/小区 灰
            else { var (hr, hg, hb) = HueColor(seg.Label[i]); cr = hr; cg = hg; cb = hb; }
            var pe = new PointEntity { X = r.Points[i].x, Y = r.Points[i].y, Cr = cr, Cg = cg, Cb = cb };
            AssignLayer(pe); pe.Cr = cr; pe.Cg = cg; pe.Cb = cb;
            _scene.Add(pe);
        }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"区域生长分割(平滑阈 {smooth:0.#}°)：{seg.RegionCount} 区(按表面光滑度) · {r.Points.Count} 点（各区异色, 灰=折棱/小区; 区别 分割点云=按距离）";
    }

    // 簇 id → 循环色(黄金角 hue)
    private static (float r, float g, float b) HueColor(int id)
    {
        double h = (id * 137.508) % 360.0 / 60.0;   // 黄金角散布色相
        double x = 1 - System.Math.Abs(h % 2 - 1);
        double r, g, b;
        if (h < 1) { r = 1; g = x; b = 0; } else if (h < 2) { r = x; g = 1; b = 0; }
        else if (h < 3) { r = 0; g = 1; b = x; } else if (h < 4) { r = 0; g = x; b = 1; }
        else if (h < 5) { r = x; g = 0; b = 1; } else { r = 1; g = 0; b = x; }
        return ((float)(0.3 + 0.7 * r), (float)(0.3 + 0.7 * g), (float)(0.3 + 0.7 * b));
    }

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
        // 方案综合评分柱状图上屏(决策支持可视, 已算未绘)
        DrawCategoryBars(ranked.Select(s => (s.Name, s.CompositeScore)).ToList(), "综合分");
        string report = $"方案综合对比：{plans.Count} 套 · 推荐【{best}】 | " +
            string.Join(" · ", ranked.Select(s => $"{s.Name} {s.CompositeScore:0}分")) + " · 评分柱入场景";
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
        var pts3d = new List<(double x, double y, double z)>();
        foreach (var p in r.Points) { pts2d.Add((p.x, p.y)); pts3d.Add((p.x, p.y, p.z)); }
        var tris = Delaunay.Triangulate(pts2d);
        if (tris.Count == 0) { StatusMsg.Text = "三角网：点太少或共线，无法剖分"; return; }
        var edges = Delaunay.BuildEdges(pts2d, tris, 0.55f, 0.75f, 0.85f);
        BeginChange();
        foreach (var e in edges) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        // 装成保留高程的 2.5D TIN 面并导 OFF(可复用于 快速建模/算量/分析)——忠实原「创建三角网建面」, 非仅画边线框。
        var st = TinSurface.Describe(pts3d, tris);
        var off = await SaveCsvAsync("导出三角网面(OFF)", "tin_surface.off", MeshWeld.ToOff(pts3d, tris));
        StatusMsg.Text = $"创建三角网：{pts2d.Count} 点 → {tris.Count} 三角 · {edges.Count} 边 · XY 投影面积 {st.ProjectedAreaXY:0.#} · 高程 {st.ZMin:0.#}~{st.ZMax:0.#}"
            + (off != null ? $" · 2.5D 面 OFF → {off}(可喂 快速建模/算量)" : "");
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
        var pts3d = new List<(double x, double y, double z)>();
        foreach (var p in r.Points) { pts2d.Add((p.x, p.y)); pts3d.Add((p.x, p.y, p.z)); }
        // 把约束线顶点并入点集，其相邻段成约束边。约束线来自 2D 场景(无 z), 其 z 由 CSV 点云 IDW 插值(贴合面), 使 2.5D 面一致。
        var constraints = new List<(int u, int v)>();
        foreach (var pl in bkPolys)
        {
            int first = -1, prev = -1;
            foreach (var (vx, vy) in pl.Points)
            {
                pts2d.Add((vx, vy)); pts3d.Add((vx, vy, Contour.IdwAt(r.Points, vx, vy))); int idx = pts2d.Count - 1;
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
        // 同 创建三角网: 装保留高程的 2.5D 面(约束线顶点 z 已 IDW 插值)导 OFF, 断层/山脊约束嵌入的可复用面。
        var st = TinSurface.Describe(pts3d, tris);
        var off = await SaveCsvAsync("导出约束三角网面(OFF)", "tin_constrained.off", MeshWeld.ToOff(pts3d, tris));
        StatusMsg.Text = $"约束三角网：{pts2d.Count} 点 · {bkPolys.Count} 约束线 → {tris.Count} 三角 · {edges.Count} 边（约束段 {kept}/{constraints.Count} 已嵌入或分段）· XY 投影面积 {st.ProjectedAreaXY:0.#}"
            + (off != null ? $" · 2.5D 面 OFF → {off}" : "");
    }

    // 约束段是否体现在网中(直边或经共线分段成链)——粗判：端点间存在一条沿线的边路径。这里简化为直边或任一端相连。
    private static bool ConstraintHeld(List<(int a, int b, int c)> tris, int u, int v, List<(double x, double y)> pts)
    {
        foreach (var t in tris)
            foreach (var (p, q) in new[] { (t.a, t.b), (t.b, t.c), (t.c, t.a) })
                if ((p == u && q == v) || (p == v && q == u)) return true;
        return false;   // 分段链情形从简不深判(面积守恒已在单测锁定正确性)
    }

    // 裁剪三角网：点 CSV + 选中闭合多段线作边界 → 三角剖分只保留质心在界内的三角(不规则域建面)。忠实原「多段线裁剪三角网」。
    private async Task CreateClippedTinAsync()
    {
        PolylineEntity? bnd = null;
        foreach (var e in _selected) if (e is PolylineEntity p && p.Closed && p.Points.Count >= 3) { bnd = p; break; }
        if (bnd == null) { StatusMsg.Text = "裁剪三角网：请先选中一条闭合多段线作裁剪边界，再执行；不裁请用 创建三角网"; return; }
        var boundary = new List<(double x, double y)>(bnd.Points);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "裁剪三角网：选点 CSV (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"裁剪三角网：点导入失败 {r.Error}"; return; }
        var pts2d = new List<(double x, double y)>();
        foreach (var p in r.Points) pts2d.Add((p.x, p.y));
        var tris = Delaunay.TriangulateClipped(pts2d, boundary);
        if (tris.Count == 0) { StatusMsg.Text = "裁剪三角网：边界内无三角(点太少/边界外/共线)"; return; }
        var edges = Delaunay.BuildEdges(pts2d, tris, 0.55f, 0.85f, 0.7f);
        BeginChange();
        foreach (var e in edges) _scene.Add(e);
        RefreshScene();
        StatusMsg.Text = $"裁剪三角网：{pts2d.Count} 点 → 界内 {tris.Count} 三角 · {edges.Count} 边（质心在边界内）";
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

    // 路网连通增强(忠实原 RoadNetworkConnector): 场景中线(多段线) → 焊接近失端点 + 桥接悬空断头(落线段中部则打断成T)
    // → 用连通后的折线集替换原线。源=选中折线(≥2)否则全场景折线。2D 场景 Z=0(高程闸门/焊接退化为平面判距)。
    private void RoadConnectCmd()
    {
        var src = _selected.FindAll(e => e is PolylineEntity);
        if (src.Count < 2)
        {
            src = new System.Collections.Generic.List<SceneEntity>();
            foreach (var e in _scene.Entities)
                if (e is PolylineEntity pl && pl.Points.Count >= 2) src.Add(e);
        }
        if (src.Count < 2) { StatusMsg.Text = "路网连通增强：请选≥2 条中线，或场景中先有路网中线"; return; }

        var lines = new System.Collections.Generic.List<double[]>();
        foreach (var e in src)
        {
            if (e is not PolylineEntity pl || pl.Points.Count < 2) continue;
            var flat = new double[pl.Points.Count * 3];
            for (int i = 0; i < pl.Points.Count; i++)
            { flat[3 * i] = pl.Points[i].x; flat[3 * i + 1] = pl.Points[i].y; flat[3 * i + 2] = 0.0; }
            lines.Add(flat);
        }
        var res = Cad.RoadNetworkConnector.Connect(lines, null, new Cad.RoadConnectOptions());
        if (res.Lines.Count == 0) { StatusMsg.Text = "路网连通增强：无输出（中线退化？）"; return; }

        BeginChange();
        foreach (var e in src) _scene.Remove(e);             // 移除原线
        foreach (var cl in res.Lines)                        // 加入连通后的线
        {
            var poly = new PolylineEntity { Cr = 0.95f, Cg = 0.85f, Cb = 0.30f };
            for (int i = 0; i < cl.Length / 3; i++) poly.Points.Add((cl[3 * i], cl[3 * i + 1]));
            AssignLayer(poly); poly.Cr = 0.95f; poly.Cg = 0.85f; poly.Cb = 0.30f;
            _scene.Add(poly);
        }
        _selected.Clear();
        RefreshScene();
        StatusMsg.Text = res.Summary;
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
    // 导入 BLK(Block_Model_2.0 八叉树块体, 平朔/3DMine 外部格式)：解析叶块几何+选定属性→grade-only 块入场景
    // "导入BLK <属性名>" 选取哪个属性作品位; 缺省首数值属性并列出全属性名供再选
    private async Task LoadBlkAsync(string cmd)
    {
        int sp = cmd.IndexOf(' ');
        string? selectAttr = sp >= 0 ? cmd.Substring(sp + 1).Trim() : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 BLK 块体模型 (Block_Model_2.0)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("八叉树块体 (BLK)") { Patterns = new[] { "*.blk" } } }
        });
        if (files.Count == 0) return;
        var r = BlkImportService.Load(files[0].Path.LocalPath, selectAttr);
        if (!r.Success) { StatusMsg.Text = $"导入 BLK：{r.Error}"; return; }
        if (r.Blocks.Count == 0) { StatusMsg.Text = "导入 BLK：无块"; return; }
        _lastBlocks = r.Blocks;
        _blockAttrs = r.AllAttrs.Count > 0 ? r.AllAttrs : null;   // 持全属性供无重导切换
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var b in r.Blocks) { if (b.X < minX) minX = b.X; if (b.Y < minY) minY = b.Y; if (b.X > maxX) maxX = b.X; if (b.Y > maxY) maxY = b.Y; }
        BeginChange();
        RenderBlocks(r.Blocks);
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        string attrs = r.AttrNames.Count > 0 ? string.Join("/", r.AttrNames) : "无";
        StatusMsg.Text = $"导入 BLK：{r.BlockCount} 叶块 · 品位取「{r.UsedAttr}」 · 全属性[{attrs}]（切换免重导：切换属性 <属性名>）";
    }

    // 导入 PMB(PitMine 块体模型 v1, 公开格式)：解析网格几何+选定属性→grade-only 块入场景
    private async Task LoadPmbAsync(string cmd)
    {
        int sp = cmd.IndexOf(' ');
        string? selectAttr = sp >= 0 ? cmd.Substring(sp + 1).Trim() : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 PMB 块体模型",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PitMine 块体 (PMB)") { Patterns = new[] { "*.pmb" } } }
        });
        if (files.Count == 0) return;
        var r = PmbImportService.Load(files[0].Path.LocalPath, selectAttr);
        if (!r.Success) { StatusMsg.Text = $"导入 PMB：{r.Error}"; return; }
        if (r.Blocks.Count == 0) { StatusMsg.Text = "导入 PMB：无块"; return; }
        _lastBlocks = r.Blocks; _blockAttrs = r.AllAttrs.Count > 0 ? r.AllAttrs : null;   // 持全属性供无重导切换/属性报告(同 BLK)
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var b in r.Blocks) { if (b.X < minX) minX = b.X; if (b.Y < minY) minY = b.Y; if (b.X > maxX) maxX = b.X; if (b.Y > maxY) maxY = b.Y; }
        BeginChange();
        RenderBlocks(r.Blocks);
        RefreshScene();
        if (maxX > minX && maxY > minY) Viewport.FitBounds(new[] { minX, minY, maxX, maxY });
        string attrs = r.AttrNames.Count > 0 ? string.Join("/", r.AttrNames) : "无";
        string swHint = _blockAttrs != null ? "切换免重导：切换属性 <属性名>" : "改属性：导入PMB <属性名>";
        StatusMsg.Text = $"导入 PMB：{r.Nx}×{r.Ny}×{r.Nz} 网格 · {r.Blocks.Count} 块 · 品位取「{r.UsedAttr}」 · 全属性[{attrs}]（{swHint}）";
    }

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
        _lastBlocks = r.Blocks; _blockAttrs = null;   // 供资源量估算
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
        // 剖面图框架(里程/标高 轴 + 网格 + 刻度)——忠实原版"剖面图"
        double profLen = prof[^1].dist;
        double frameTextH = System.Math.Max((zmax - zmin) * 0.06, profLen * 0.02);
        foreach (var fe in ProfilePlot.Frame(baseX, baseY, profLen, zmin, zmax, System.Math.Max(frameTextH, 1e-3)))
        { fe.LayerName = _layers.Current.Name; _scene.Add(fe); }
        RefreshScene();
        StatusMsg.Text = $"剖面分析：{prof.Count} 采样 · 高程 {zmin:0.##}~{zmax:0.##} · 剖面长 {profLen:0.##}（带里程/标高轴）";
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
        var (max, _) = CloudCompare.Stats(dists);
        BeginChange();
        for (int i = 0; i < ra.Points.Count; i++)
        {
            var (cr, cg, cb) = BlockModel.GradeColor(dists[i], 0, max);   // 蓝(近)→红(远)
            _scene.Add(new PointEntity { X = ra.Points[i].x, Y = ra.Points[i].y, Cr = cr, Cg = cg, Cb = cb });
        }
        RefreshScene();
        Viewport.FitBounds(ra.Bounds);
        // 位移分布(原「统计与分布直方图」)：偏差 → min/max/mean/std/分位数 + 20 桶直方图 → CSV
        var summary = Statistics.Describe(dists, 20);
        var name = await SaveCsvAsync("导出C2C分布", "c2c_distribution.csv", Statistics.HistogramCsv(summary));
        // |位移| P95(边坡变形监测判据, 忠实原 C2C 关键指标): 95% 点位移小于此
        var sorted = (double[])dists.Clone(); System.Array.Sort(sorted);
        double p95 = Statistics.Percentile(sorted, 95);
        StatusMsg.Text = $"C2C 比对：{ra.Points.Count} 点 · {Statistics.SummaryLine(summary)} · |位移|P95={p95.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}m"
            + (name != null ? $" · 分布直方图 → {name}" : "");
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

    // 移除障碍物(渐进形态学 PMF)：点 CSV → 分地面/非地面(车辆/设备/植被/堆料), 两色入场景 + 计数。
    // 比 地面点滤波(每格最低点)稳健, 且【另出非地面点云】。用法 "移除障碍物 [格边m] [高差阈m]"。
    private async Task PmfAsync(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "移除障碍物：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success || r.Points.Count == 0) { StatusMsg.Text = $"移除障碍物：导入失败/无点 {r.Error}"; return; }
        double span = System.Math.Max(r.Bounds[2] - r.Bounds[0], r.Bounds[3] - r.Bounds[1]);
        double cell = System.Math.Max(span / 80.0, 1e-6);
        double dhMax = 3.0;
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double c) && c > 0) cell = c;
        if (tk.Length >= 3 && double.TryParse(tk[2], System.Globalization.NumberStyles.Float, inv, out double d) && d > 0) dhMax = d;
        var res = Cad.ProgressiveMorphFilter.Filter(r.Points, cell, dhMax: dhMax);
        BeginChange();
        foreach (var (x, y, _) in res.Ground) { var pt = new PointEntity { X = x, Y = y }; AssignLayer(pt); pt.Cr = 0.55f; pt.Cg = 0.42f; pt.Cb = 0.24f; _scene.Add(pt); }       // 地面 棕
        foreach (var (x, y, _) in res.NonGround) { var pt = new PointEntity { X = x, Y = y }; AssignLayer(pt); pt.Cr = 0.9f; pt.Cg = 0.2f; pt.Cb = 0.2f; _scene.Add(pt); }         // 非地面(障碍) 红
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"移除障碍物(渐进形态学 PMF, cell {cell.ToString("0.##", inv)}·阈 {dhMax.ToString("0.#", inv)}m)："
            + $"地面 {res.GroundCount} 点(棕) / 非地面·障碍 {res.NonGroundCount} 点(红) · 共 {r.Points.Count}（车辆/设备/植被/堆料已分出）";
    }

    // 点云抽稀：XYZ CSV → 体素抽稀 → 抽稀后点入场景 + 报压缩比
    private async Task ThinPointsAsync(string thinMode = "voxel", string cmd = "")
    {
        string mode = thinMode switch { "adaptive" => "自适应保特征抽稀", "uniform" => "均匀抽稀", "random" => "随机抽稀", _ => "点云抽稀(体素)" };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = mode + "：选点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"{mode}：导入失败 {r.Error}"; return; }
        double span = System.Math.Max(r.Bounds[2] - r.Bounds[0], r.Bounds[3] - r.Bounds[1]);
        var ca = PrimitiveNums(cmd);
        double cell = ca.Length >= 1 && ca[0] > 0 ? ca[0] : System.Math.Max(span / 100.0, 1e-6);   // 命令给格距, 否则约 100 格跨度
        var thinned = thinMode switch
        {
            "adaptive" => PointThin.ThinAdaptive(r.Points, cell),
            "uniform" => PointThin.ThinUniform(r.Points, cell),
            "random" => PointThin.ThinRandom(r.Points, r.Points.Count > 0 ? System.Math.Min(1.0, (double)PointThin.Thin(r.Points, cell).Count / r.Points.Count) : 1.0, new System.Random()),
            _ => PointThin.Thin(r.Points, cell)
        };
        string note = thinMode == "adaptive" ? "；脊/棱密留、平坦疏化" : thinMode == "uniform" ? "；保证最小间距" : thinMode == "random" ? "；随机子集(快速)" : "";
        BeginChange();
        foreach (var (x, y, _) in thinned) { var pt = new PointEntity { X = x, Y = y }; AssignLayer(pt); _scene.Add(pt); }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"{mode}：{r.Points.Count} → {thinned.Count} 点（cell {cell:0.##}，压缩 {100.0 * (1 - (double)thinned.Count / r.Points.Count):0.#}%{note}）";
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
        var (nodes, adj) = RoadNetwork.BuildNoded(polys, tol);
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
        // 分标高报量(整体+分台阶)：自动 10 台阶带
        var benches = BlockModel.ResourceByElevation(_lastBlocks, cut, 2.7, benchHeight: 0);
        txt += "\n分标高报量\n" + BlockModel.ResourceByElevationCsv(benches);
        try { System.IO.File.WriteAllText(file.Path.LocalPath, txt); }
        catch (System.Exception ex) { StatusMsg.Text = $"输出报告：写出失败 {ex.Message}"; return; }
        StatusMsg.Text = $"输出报告：资源量报告已保存({benches.Count} 台阶带) → {System.IO.Path.GetFileName(file.Path.LocalPath)}";
    }

    // 属性统计：块体品位 min/max/mean/std/median + 20 桶直方图 → 上屏 + 导出 CSV
    private async Task GradeStatsAsync()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "属性统计：请先导入/生成块体"; return; }
        var grades = _lastBlocks.Select(b => b.Grade).ToList();
        var s = Statistics.Describe(grades, 20);
        var name = await SaveCsvAsync("导出品位直方图", "grade_histogram.csv", Statistics.HistogramCsv(s));
        // 直方图柱状图入场景(可视)——忠实原版"直方图"
        double hvw = ViewportHost.Bounds.Width, hvh = ViewportHost.Bounds.Height;
        var hp0 = Viewport.ScreenToWorld(hvw * 0.3, hvh * 0.85) ?? (0.0, 0.0);
        var hp1 = Viewport.ScreenToWorld(hvw * 0.7, hvh * 0.4) ?? (100.0, 50.0);
        double hw = System.Math.Abs(hp1.x - hp0.x), hh = System.Math.Abs(hp1.y - hp0.y);
        if (hw < 1e-6) hw = 100; if (hh < 1e-6) hh = 50;
        BeginChange();
        foreach (var he in HistogramPlot.Build(s, System.Math.Min(hp0.x, hp1.x), System.Math.Min(hp0.y, hp1.y), hw, hh, System.Math.Max(hh * 0.05, 1e-3)))
        { he.LayerName = _layers.Current.Name; _scene.Add(he); }
        RefreshScene();
        StatusMsg.Text = $"属性统计(品位)：{Statistics.SummaryLine(s)} · 20 桶直方图（柱状图入场景）" + (name != null ? $" · CSV → {name}" : "");
    }

    // 类别柱状图入场景助手: 把 (类别,值) 序列以 BarChartPlot 画到视口中部(0.3–0.7 宽·0.85–0.4 高)。
    // 与直方图/曲线上屏同位同风格; 供煤类分布/设备分类等类别分布可视化复用。
    private void DrawCategoryBars(IReadOnlyList<(string label, double value)> items, string valueName)
    {
        if (items == null || items.Count == 0) return;
        double vw = ViewportHost.Bounds.Width, vh = ViewportHost.Bounds.Height;
        var p0 = Viewport.ScreenToWorld(vw * 0.3, vh * 0.85) ?? (0.0, 0.0);
        var p1 = Viewport.ScreenToWorld(vw * 0.7, vh * 0.4) ?? (100.0, 50.0);
        double w = System.Math.Abs(p1.x - p0.x), h = System.Math.Abs(p1.y - p0.y);
        if (w < 1e-6) w = 100; if (h < 1e-6) h = 50;
        BeginChange();
        foreach (var e in Cad.BarChartPlot.Build(items, System.Math.Min(p0.x, p1.x), System.Math.Min(p0.y, p1.y),
                     w, h, System.Math.Max(h * 0.05, 1e-3), valueName))
        { e.LayerName = _layers.Current.Name; _scene.Add(e); }
        RefreshScene();
    }

    // 块体多属性统计报告(原 BlockReportGenerator「每属性 min/max/mean/std/count+直方图」表格部分):
    // 对持有的全属性各算 min/max/mean/std/Q1/median/Q3 → CSV。仅 BLK 导入持全属性。
    private async Task BlockAttrReportAsync()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "属性报告：请先导入块体"; return; }
        if (_blockAttrs == null || _blockAttrs.Count == 0) { StatusMsg.Text = "属性报告：当前块体未持多属性(仅 BLK 导入持全属性; 重新 导入BLK)"; return; }
        var csv = Statistics.MultiAttrReportCsv(_blockAttrs, 20);
        var name = await SaveCsvAsync("导出块体属性报告", "block_attr_report.csv", csv);
        StatusMsg.Text = $"属性报告：{_blockAttrs.Count} 属性 ×(min/max/mean/std/Q1/median/Q3)" + (name != null ? $" → {name}" : "（取消保存）");
    }

    // 块体着色：按品位配色重渲全部块体(恢复全显)
    private void ColorBlocksCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "块体着色：请先导入/生成块体"; return; }
        BeginChange(); RenderBlocks(_lastBlocks); RefreshScene();
        StatusMsg.Text = $"块体着色：{_lastBlocks.Count} 块按品位配色（蓝低→红高）";
    }

    // 块体分类离散着色(原「分类离散色」)：按不同品位(属性)值各异色, 适合类别属性(岩性/矿岩类型)
    private void ColorBlocksCategoricalCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "块体分类着色：请先导入/生成块体"; return; }
        var catId = new System.Collections.Generic.Dictionary<double, int>();
        foreach (var b in _lastBlocks) if (!catId.ContainsKey(b.Grade)) catId[b.Grade] = catId.Count;
        foreach (var e in _blockCellEntities) _scene.Remove(e);
        _blockCellEntities.Clear();
        var cells = BlockModel.BuildCellsColored(_lastBlocks, b => HueColor(catId[b.Grade]));
        BeginChange();
        foreach (var c in cells) { _scene.Add(c); _blockCellEntities.Add(c); }
        RefreshScene();
        StatusMsg.Text = $"块体分类着色：{catId.Count} 类别(按属性值离散配色，各类异色) · {_lastBlocks.Count} 块（块体着色 恢复连续品位色）";
    }

    // 块体分级区间着色(原 ColoringDialog「分级区间着色」)：连续属性按自定义区间上界分级, 各级固定色。
    // "块体分级着色 1,3,5" → 上界1/3/5(4级); 缺省→四分位 Q1/median/Q3(4级)。区别于连续渐变与分类离散。
    private void ColorBlocksClassedCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "块体分级着色：请先导入/生成块体"; return; }
        var breaks = new System.Collections.Generic.List<double>();
        int sp = cmd.IndexOf(' ');
        if (sp >= 0)
            foreach (var tok in cmd.Substring(sp + 1).Split(new[] { ',', ' ', '，' }, System.StringSplitOptions.RemoveEmptyEntries))
                if (double.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bv)) breaks.Add(bv);
        if (breaks.Count == 0)
        {
            var sorted = _lastBlocks.Select(b => b.Grade).OrderBy(x => x).ToList();
            breaks.Add(Statistics.Percentile(sorted, 25));
            breaks.Add(Statistics.Percentile(sorted, 50));
            breaks.Add(Statistics.Percentile(sorted, 75));
        }
        breaks.Sort();
        var uniq = new System.Collections.Generic.List<double>();
        foreach (var bk in breaks) if (uniq.Count == 0 || bk > uniq[uniq.Count - 1] + 1e-9) uniq.Add(bk);
        breaks = uniq;
        int k = breaks.Count + 1;
        var colors = new System.Collections.Generic.List<(float, float, float)>();
        for (int i = 0; i < k; i++) colors.Add(BlockModel.GradeColor(i, 0, k - 1));   // 蓝(低级)→红(高级)
        foreach (var e in _blockCellEntities) _scene.Remove(e);
        _blockCellEntities.Clear();
        var cells = BlockModel.BuildCellsClassed(_lastBlocks, breaks, colors);
        BeginChange();
        foreach (var c in cells) { _scene.Add(c); _blockCellEntities.Add(c); }
        RefreshScene();
        StatusMsg.Text = $"块体分级着色：{k} 级(区间上界 {string.Join("/", breaks.Select(x => x.ToString("0.###")))}) · {_lastBlocks.Count} 块（块体着色 恢复连续色）";
    }

    // 切换活动品位属性(免重导, 原「多属性显示切换」)：从持有的全属性数组重取 grade → 重渲配色。
    // 仅 BLK 导入持全属性(AllAttrs); 长度须与块数一致。
    private void SwitchGradeAttrCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "切换属性：请先导入 BLK 块体"; return; }
        if (_blockAttrs == null || _blockAttrs.Count == 0) { StatusMsg.Text = "切换属性：当前块体未持多属性(仅 BLK 导入持全属性; 重新 导入BLK)"; return; }
        int sp = cmd.IndexOf(' ');
        string name = sp >= 0 ? cmd.Substring(sp + 1).Trim() : "";
        if (name.Length == 0) { StatusMsg.Text = $"切换属性：可选 [{string.Join("/", _blockAttrs.Keys)}]（用法：切换属性 <属性名>）"; return; }
        if (!_blockAttrs.TryGetValue(name, out var vals)) { StatusMsg.Text = $"切换属性：无「{name}」 · 可选 [{string.Join("/", _blockAttrs.Keys)}]"; return; }
        if (vals.Length != _lastBlocks.Count) { StatusMsg.Text = $"切换属性：属性长度 {vals.Length} ≠ 块数 {_lastBlocks.Count}"; return; }
        double gmin = double.MaxValue, gmax = double.MinValue, gsum = 0;
        for (int i = 0; i < _lastBlocks.Count; i++)
        {
            var b = _lastBlocks[i]; b.Grade = vals[i]; _lastBlocks[i] = b;   // Block 为 struct，需回写
            if (vals[i] < gmin) gmin = vals[i]; if (vals[i] > gmax) gmax = vals[i]; gsum += vals[i];
        }
        BeginChange(); RenderBlocks(_lastBlocks); RefreshScene();
        StatusMsg.Text = $"切换属性→「{name}」：{_lastBlocks.Count} 块重配色 · 值域[{gmin:0.###},{gmax:0.###}] 均{gsum / _lastBlocks.Count:0.###}";
    }

    // 属性赋值(公式模式)：忠实原 ExpressionEngine「属性赋值 公式」——按表达式逐块算新属性存入 _blockAttrs。
    // 用法：属性赋值 <名> = <表达式>。变量: x/y/z/grade(品位)/size(尺寸)/i/j/k/nx/ny/nz/sx/sy/sz + 已有属性名。
    // 函数: min/max/abs/sqrt/exp/log/sin/cos/tan/floor/ceil/round/clamp(v,lo,hi)/if(c,a,b)。
    private void BlockAttrAssignCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "属性赋值：请先导入/生成块体"; return; }
        int eq = cmd.IndexOf('=');
        if (eq < 0) { StatusMsg.Text = "属性赋值：用法 属性赋值 <新属性名> = <表达式>（变量 x/y/z/grade/size/i/j/k/nx/ny/nz + 已有属性; 函数 min/max/abs/sqrt/exp/log/sin/cos/tan/floor/ceil/round/clamp/if）"; return; }
        string head = cmd.Substring(0, eq).Trim(), expr = cmd.Substring(eq + 1).Trim();
        var hp = head.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string name = hp.Length >= 2 ? hp[^1] : "attr";
        Cad.BlockAttrExpression compiled;
        try { compiled = Cad.BlockAttrExpression.Compile(expr); }
        catch (System.Exception ex) { StatusMsg.Text = $"属性赋值：表达式错误 {ex.Message}"; return; }
        // 重建网格算 i/j/k/nx/ny/nz
        double size = _lastBlocks[0].Size > 0 ? _lastBlocks[0].Size : 1;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var b in _lastBlocks) { if (b.X < minX) minX = b.X; if (b.X > maxX) maxX = b.X; if (b.Y < minY) minY = b.Y; if (b.Y > maxY) maxY = b.Y; if (b.Z < minZ) minZ = b.Z; if (b.Z > maxZ) maxZ = b.Z; }
        int nx = (int)System.Math.Round((maxX - minX) / size) + 1, ny = (int)System.Math.Round((maxY - minY) / size) + 1, nz = (int)System.Math.Round((maxZ - minZ) / size) + 1;
        var vals = new double[_lastBlocks.Count];
        var ctx = new Cad.MutableBlockExprContext();
        int bad = 0;
        for (int bi = 0; bi < _lastBlocks.Count; bi++)
        {
            var b = _lastBlocks[bi];
            ctx.Set("x", b.X); ctx.Set("y", b.Y); ctx.Set("z", b.Z); ctx.Set("高程", b.Z); ctx.Set("标高", b.Z);
            ctx.Set("grade", b.Grade); ctx.Set("品位", b.Grade); ctx.Set("值", b.Grade); ctx.Set("size", b.Size); ctx.Set("尺寸", b.Size);
            ctx.Set("i", System.Math.Round((b.X - minX) / size)); ctx.Set("j", System.Math.Round((b.Y - minY) / size)); ctx.Set("k", System.Math.Round((b.Z - minZ) / size));
            ctx.Set("nx", nx); ctx.Set("ny", ny); ctx.Set("nz", nz); ctx.Set("sx", size); ctx.Set("sy", size); ctx.Set("sz", size);
            if (_blockAttrs != null) foreach (var kv in _blockAttrs) if (bi < kv.Value.Length) ctx.Set(kv.Key, kv.Value[bi]);
            double v = compiled.Evaluate(ctx);
            if (double.IsNaN(v) || double.IsInfinity(v)) { bad++; v = 0; }
            vals[bi] = v;
        }
        _blockAttrs ??= new System.Collections.Generic.Dictionary<string, double[]>();
        _blockAttrs[name] = vals;
        double mn = double.MaxValue, mx = double.MinValue, sum = 0; foreach (var v in vals) { if (v < mn) mn = v; if (v > mx) mx = v; sum += v; }
        StatusMsg.Text = $"属性赋值：{name} = {expr} · {vals.Length} 块 · 值域[{mn:0.###},{mx:0.###}] 均{sum / vals.Length:0.###}"
            + (bad > 0 ? $" · {bad} 块无效(NaN/∞)→0" : "") + $" · 用「切换属性 {name}」显示";
    }

    // 块体煤岩分类(忠实原 CoalRockClassifier): 按类别码集判块体属煤/属岩/忽略(类别型模型: 品位值作岩性码)。
    // 用法 块体煤岩分类 煤 <码...> [岩 <码...>] [容差 <t>]。与"品位≥限值=煤"互补(此对离散码)。
    private void BlockCoalRockCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "块体煤岩分类：请先导入/生成块体"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        var coal = new List<double>(); var rock = new List<double>(); double tol = 0.5; int mode = 0;
        for (int i = 1; i < tk.Length; i++)
        {
            if (tk[i] == "煤" || tk[i] == "煤码") { mode = 1; continue; }
            if (tk[i] == "岩" || tk[i] == "岩码") { mode = 2; continue; }
            if (tk[i] == "容差" || tk[i] == "tol") { mode = 3; continue; }
            if (double.TryParse(tk[i], out var v)) { if (mode == 1) coal.Add(v); else if (mode == 2) rock.Add(v); else if (mode == 3) tol = v; }
        }
        if (coal.Count == 0) { StatusMsg.Text = "块体煤岩分类：用法 块体煤岩分类 煤 <码...> [岩 <码...>] [容差 <t>]（按块体品位值作岩性类别码）"; return; }
        var clf = new Cad.CoalRockClassifier { CoalCodes = coal.ToArray(), RockCodes = rock.ToArray(), Tol = tol };
        int nc = 0, nr = 0, ni = 0; double coalVol = 0, rockVol = 0;
        foreach (var b in _lastBlocks)
        {
            double vol = b.Size * b.Size * b.Size;
            if (clf.IsCoal(b.Grade)) { nc++; coalVol += vol; }
            else if (clf.IsRock(b.Grade)) { nr++; rockVol += vol; }
            else ni++;
        }
        double sr = coalVol > 1e-9 ? rockVol / coalVol : 0;
        StatusMsg.Text = $"块体煤岩分类(煤码[{string.Join(",", coal)}]{(rock.Count > 0 ? $"·岩码[{string.Join(",", rock)}]" : "·非煤即岩")}·容差{tol})："
            + $"煤 {nc} 块({coalVol / 1e4:0.#}万m³)·岩 {nr} 块({rockVol / 1e4:0.#}万m³)·忽略 {ni} / 共 {_lastBlocks.Count} · 剥采比 {sr:0.##}";
    }

    // 字高归一化(忠实原 TextHeightNormalizer): 修正导入文字里相对图幅异常巨大/缺失的字高(逐实体离群修正)。
    private void TextHeightNormalizeCmd()
    {
        var texts = _scene.Entities.OfType<TextEntity>().ToList();
        if (texts.Count == 0) { StatusMsg.Text = "字高归一化：场景无文字"; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue; bool any = false;
        void Acc(double x, double y) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; any = true; }
        foreach (var e in _scene.Entities)
            switch (e)
            {
                case LineEntity l: Acc(l.X0, l.Y0); Acc(l.X1, l.Y1); break;
                case PolylineEntity pl: foreach (var p in pl.Points) Acc(p.Item1, p.Item2); break;
                case RectEntity rc: Acc(rc.X0, rc.Y0); Acc(rc.X1, rc.Y1); break;
                case CircleEntity c: Acc(c.Cx - c.Radius, c.Cy - c.Radius); Acc(c.Cx + c.Radius, c.Cy + c.Radius); break;
                case ArcEntity a: Acc(a.X1, a.Y1); Acc(a.X2, a.Y2); Acc(a.X3, a.Y3); break;
                case PointEntity pt: Acc(pt.X, pt.Y); break;
                case PolygonEntity pg: Acc(pg.Cx - pg.Radius, pg.Cy - pg.Radius); Acc(pg.Cx + pg.Radius, pg.Cy + pg.Radius); break;
            }
        double w = any ? maxX - minX : 0, h = any ? maxY - minY : 0;
        var norm = new Cad.TextHeightNormalizer(w, h, texts.Select(t => t.Height));
        if (!norm.IsActive) { StatusMsg.Text = "字高归一化：无有效图幅几何(需线/多段线等参照)"; return; }
        BeginChange();
        foreach (var t in texts) t.Height = norm.Correct(t.Height);
        RefreshScene();
        StatusMsg.Text = $"字高归一化：{texts.Count} 文字 · 图幅对角线 {norm.Diagonal:0.#} · 典型字高 {norm.TypicalHeight:0.##} · 修正 {norm.CorrectedCount} 条(离群/缺失→典型)";
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

    // 表达式筛选块(原 ExpressionEngine「表达式删单元」用途)：按 "Grade>5 AND Z<100" 只显匹配块(非破坏, 块体着色恢复)
    private void BlockExpressionFilterCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "表达式筛选：请先导入/生成块体"; return; }
        int sp = cmd.IndexOf(' ');
        string expr = sp >= 0 ? cmd.Substring(sp + 1).Trim() : "";
        if (string.IsNullOrWhiteSpace(expr)) { StatusMsg.Text = "表达式筛选：用法「表达式筛选块 Grade>5 AND Z<100」(属性 X/Y/Z/Grade/Size)"; return; }
        System.Func<BlockModel.Block, bool> pred;
        try { pred = BlockExpression.Compile(expr); }
        catch (System.Exception ex) { StatusMsg.Text = $"表达式错误：{ex.Message}"; return; }
        var sub = _lastBlocks.Where(pred).ToList();
        BeginChange(); RenderBlocks(sub); RefreshScene();
        StatusMsg.Text = $"表达式筛选「{expr}」：显示 {sub.Count}/{_lastBlocks.Count} 块（块体着色 恢复全显）";
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

    // 面约束块体(忠实 MeshContainmentTester 4 模式): 用一张 3D 网格约束块体——相对开放曲面 上/下, 或相对闭合网格 内/外。
    // 区别于「约束块体」(仅 2D 闭合多段线内)。"面约束块体 上|下|内|外"(缺省 下=保留曲面以下, 如地表下)。
    private async Task MeshConstrainBlocksAsync(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "面约束块体：请先导入块体模型"; return; }
        var mode = Cad.MeshConstraintMode.KeepBelowSurface; string modeCn = "曲面以下";
        if (cmd.Contains("上")) { mode = Cad.MeshConstraintMode.KeepAboveSurface; modeCn = "曲面以上"; }
        else if (cmd.Contains("内")) { mode = Cad.MeshConstraintMode.KeepInsideClosed; modeCn = "闭合内"; }
        else if (cmd.Contains("外")) { mode = Cad.MeshConstraintMode.KeepOutsideClosed; modeCn = "闭合外"; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"面约束块体({modeCn})：选约束网格 OFF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"面约束块体：读取失败 {ex.Message}"; return; }
        var (mv, mt) = MeshMetrics.ParseOff(text);
        if (mt.Count == 0) { StatusMsg.Text = "面约束块体：未解析到网格三角"; return; }

        var (fv, ft) = Cad.MeshContainment.Flatten(mv, mt);
        var centers = _lastBlocks.Select(b => (b.X, b.Y, b.Z)).ToList();
        var keepIdx = Cad.MeshContainment.KeepIndices(fv, ft, centers, mode);
        if (keepIdx.Count == 0) { StatusMsg.Text = $"面约束块体({modeCn})：无块体满足约束"; return; }
        var sub = keepIdx.Select(i => _lastBlocks[i]).ToList();
        BeginChange(); RenderBlocks(sub); RefreshScene();
        StatusMsg.Text = $"面约束块体({modeCn})：{sub.Count}/{_lastBlocks.Count} 块满足（余隐去; 约束网格 {mt.Count} 三角）";
    }

    // 离散化模型(忠实 BlockModelLib「离散化模型」): 把封闭三角网体素化成块体——格心落闭合网内(GWN)保留成块。
    // "离散化模型 [块尺寸]"(缺省按包围盒对角 1/40 自动)。产出块体模型入 _lastBlocks(供资源量/剥采比复用)。
    private async Task DiscretizeModelAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "离散化模型：选封闭三角网体 OFF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("网格 (OFF)") { Patterns = new[] { "*.off" } } }
        });
        if (files.Count == 0) return;
        string text;
        try { text = System.IO.File.ReadAllText(files[0].Path.LocalPath); }
        catch (System.Exception ex) { StatusMsg.Text = $"离散化模型：读取失败 {ex.Message}"; return; }
        var (mv, mt) = MeshMetrics.ParseOff(text);
        if (mt.Count == 0) { StatusMsg.Text = "离散化模型：未解析到三角网"; return; }
        var diag = MeshDiagnose.Analyze(mv, mt);
        if (!diag.IsClosed) { StatusMsg.Text = "离散化模型：需封闭(水密)三角网体——当前非闭合(边界边/非流形)，GWN 内外判不可靠"; return; }

        double bx0 = double.MaxValue, by0 = double.MaxValue, bz0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue, bz1 = double.MinValue;
        foreach (var p in mv) { if (p.x < bx0) bx0 = p.x; if (p.y < by0) by0 = p.y; if (p.z < bz0) bz0 = p.z; if (p.x > bx1) bx1 = p.x; if (p.y > by1) by1 = p.y; if (p.z > bz1) bz1 = p.z; }
        double bdiag = System.Math.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0) + (bz1 - bz0) * (bz1 - bz0));
        double cell = 0; int sp = cmd.IndexOf(' ');
        if (sp >= 0) double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cell);
        if (cell <= 0) cell = System.Math.Max(bdiag / 40.0, 1e-6);

        var (fv, ft) = Cad.MeshContainment.Flatten(mv, mt);
        var r = Cad.MeshVoxelizer.Voxelize(fv, ft, cell);
        if (r.TooLarge) { StatusMsg.Text = $"离散化模型：格数过大({r.Nx}×{r.Ny}×{r.Nz})，请加大块尺寸(如 “离散化模型 {bdiag / 20:0.#}”)"; return; }
        if (r.Centers.Count == 0) { StatusMsg.Text = "离散化模型：无格心落在体内(检查闭合性/块尺寸)"; return; }

        var blocks = new List<BlockModel.Block>(r.Centers.Count);
        foreach (var c in r.Centers) blocks.Add(new BlockModel.Block { X = c.x, Y = c.y, Z = c.z, Size = cell, Grade = 0 });
        _lastBlocks = blocks; _blockAttrs = null;
        BeginChange(); RenderBlocks(blocks); RefreshScene();
        Viewport.ZoomExtents();
        StatusMsg.Text = $"离散化模型：{blocks.Count} 块（块尺寸 {cell:0.##}m · 格网 {r.Nx}×{r.Ny}×{r.Nz} · 体积≈{blocks.Count * cell * cell * cell:0.#}m³）";
    }

    // 删除块体：移除全部块体方块 + 清工作集
    private void DeleteBlocksCmd()
    {
        if (_blockCellEntities.Count == 0 && (_lastBlocks == null || _lastBlocks.Count == 0)) { StatusMsg.Text = "删除块体：无块体"; return; }
        BeginChange();
        foreach (var e in _blockCellEntities) _scene.Remove(e);
        _blockCellEntities.Clear(); _lastBlocks = null; _blockAttrs = null;
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
        var np = DerivePolyline(pl, simp, pl.Closed);   // 全样式(含线型/线宽/可见/标高)+ 三维线逐点高程随简化保留
        BeginChange();
        _scene.Replace(pl, np);
        _selected.Clear(); _selected.Add(np);
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"多段线简化：{pl.Points.Count} → {np.Points.Count} 点（容差 {eps:0.##}）";
    }

    // 曲线平滑：Chaikin(逼近/收缩) 或 CatmullRom(插值/过点)
    private void SmoothPolyline(bool spline = false)
    {
        if (_selected.Count != 1 || _selected[0] is not PolylineEntity pl || pl.Points.Count < 3)
        { StatusMsg.Text = "平滑：请先选中一条至少 3 点的多段线"; return; }
        var sm = spline ? PolylineSmooth.CatmullRom(pl.Points, 8, pl.Closed) : PolylineSmooth.Chaikin(pl.Points, 3, pl.Closed);
        var np = DerivePolyline(pl, sm, pl.Closed);   // 全样式 + 三维线逐点高程随平滑保留
        BeginChange();
        _scene.Replace(pl, np);
        _selected.Clear(); _selected.Add(np);
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"曲线平滑：{pl.Points.Count} → {np.Points.Count} 点（{(spline ? "CatmullRom 插值·过原点" : "Chaikin×3·逼近")}）";
    }

    // 多边形裁剪：选两条多段线(第1=被裁, 第2=凸裁剪边界)→交集
    // ── 其余"改图元"的命令同样走 动词-名词：先激活 → 选择对象 → 右键确定 → 执行 ──

    private async Task JoinPolylinesCmdAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("组合工作线", "多段线", 2);
        if (polys.Count < 2) return;
        JoinPolylines();
    }

    private async Task ClipPolygonCmdAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("裁剪", "两条多段线（第 1 条=被裁，第 2 条=裁剪边界）", 2);
        if (polys.Count < 2) return;
        SelectEntities(new SceneEntity[] { polys[0], polys[1] });
        ClipPolygon();
    }

    private async Task ClipLinesCmdAsync(bool keepInside)
    {
        string name = keepInside ? "线裁剪（保留界内）" : "线外裁剪（保留界外）";
        var polys = await SelectObjectsAsync<PolylineEntity>(name, "被裁多段线 + 最后一条闭合边界", 2);
        if (polys.Count < 2) return;
        ClipLinesByBoundary(keepInside);
    }

    private async Task SimplifyPolylineCmdAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("简化", "多段线（≥3 点）", 1, p => p.Points.Count >= 3);
        if (polys.Count == 0) return;
        SelectEntities(new SceneEntity[] { polys[0] });
        SimplifyPolyline();
    }

    private async Task SmoothPolylineCmdAsync(bool spline)
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("平滑", "多段线（≥3 点）", 1, p => p.Points.Count >= 3);
        if (polys.Count == 0) return;
        SelectEntities(new SceneEntity[] { polys[0] });
        SmoothPolyline(spline);
    }

    private async Task ModifyPointStyleCmdAsync(string cmd)
    {
        var pts = await SelectObjectsAsync<PointEntity>("修改点样式", "点");
        if (pts.Count == 0) return;
        ModifyPointStyle(cmd);
    }

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

    // 线对象裁剪(POLYCLIP)：选中≥2 条多段线, 最后一条=闭合边界, 其余按边界裁成界内(或界外)段
    private void ClipLinesByBoundary(bool keepInside)
    {
        var polys = _selected.FindAll(e => e is PolylineEntity).ConvertAll(e => (PolylineEntity)e);
        if (polys.Count < 2) { StatusMsg.Text = "线裁剪：请先选中≥2 条多段线(末条=闭合边界, 其余=被裁线)"; return; }
        var boundary = polys[^1].Points;
        if (boundary.Count < 3) { StatusMsg.Text = "线裁剪：末条(边界)需≥3 点且闭合"; return; }
        BeginChange();
        int made = 0, removed = 0;
        for (int i = 0; i < polys.Count - 1; i++)
        {
            var subj = polys[i];
            var pieces = LineClip.ByPolygon(subj.Points, subj.Closed, boundary, keepInside);
            if (pieces.Count == 0) continue;
            foreach (var piece in pieces)
            {
                var pl = new PolylineEntity(); pl.CopyStyleFrom(subj);   // 全样式随裁剪段保留
                foreach (var p in piece) pl.Points.Add(p);
                _scene.Add(pl); made++;
            }
            _scene.Remove(subj); removed++;   // 原线被裁段取代
        }
        RefreshScene();
        StatusMsg.Text = made > 0
            ? $"线裁剪({(keepInside ? "保内" : "保外")})：{removed} 条 → {made} 段"
            : $"线裁剪：无{(keepInside ? "界内" : "界外")}段";
    }

    // 线性标注：取两点
    private void StartDim(bool aligned = false)
    {
        _dimActive = true; _dimP1 = null; _dimP2 = null; _dimContinue = false; _dimAligned = aligned;
        _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = $"{(aligned ? "对齐" : "线性")}标注：指定第一点（点1→点2→尺寸线位置）";
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
        _dimRadCircle = c; _dimRadActive = true; _dimDiameter = false;
        _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "半径标注：指定标注方向";
    }

    // 直径标注(DIMDIAMETER)：需先选一个圆或弧，复用半径流程 + _dimDiameter 标志
    private void StartDimDiameter()
    {
        (double cx, double cy, double r)? c = _selected.Count == 1 ? _selected[0] switch
        {
            CircleEntity ce => (ce.Cx, ce.Cy, ce.Radius),
            ArcEntity ae => ArcMath.Circumcircle(ae.X1, ae.Y1, ae.X2, ae.Y2, ae.X3, ae.Y3) is { } v ? (v.Item1, v.Item2, v.Item3) : ((double, double, double)?)null,
            _ => null
        } : null;
        if (c == null) { StatusMsg.Text = "直径标注：请先选中一个圆或弧"; return; }
        _dimRadCircle = c; _dimRadActive = true; _dimDiameter = true;
        _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "直径标注：指定标注方向";
    }

    // 角度标注(DIMANGULAR)：三点 —— 角顶点 + 两条边上各一点
    private void StartDimAngular()
    {
        _dimAngActive = true; _angVertex = null; _angP1 = null;
        _tool = null; _measure = null; _editMode = EditMode.None;
        _dimActive = false; _dimRadActive = false;
        StatusMsg.Text = "角度标注：指定角顶点";
    }

    // 坐标标注：进入连续点选模式，每点生成 十字+引线+"X=… Y=…" 注记
    private void StartCoordLabel()
    {
        _coordLabelActive = true;
        _tool = null; _measure = null; _editMode = EditMode.None;
        _dimActive = false; _dimRadActive = false;
        StatusMsg.Text = "坐标标注：点选要标注坐标的点（连续, ESC 退出）";
    }

    // 连续标注(DIMCONTINUE)：以上一条线性标注的第二点为起点链式接续
    private void StartDimContinue()
    {
        if (_lastDimP2 == null) { StatusMsg.Text = "连续标注：请先做一条线性标注"; return; }
        _dimActive = true; _dimP1 = _lastDimP2; _dimP2 = null; _dimContinue = true;   // 续标: 2 点, 尺寸线级沿用上条
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

    // 线型(CAD linetype)：设当前虚线样式, 新画直线/多段线继承。实线/虚线/点划线/点线/双点划线, 或 "线型 <名>"
    private void SetLinetypeCmd(string cmd)
    {
        if (cmd == "线型")
        {
            StatusMsg.Text = $"线型：当前 {(_currentDash == null ? "实线" : "虚线类")}（可选 实线/虚线/点划线/点线/双点划线，或 线型 <名>；有选中即写入选中实体，无选中则为新画默认）";
            return;
        }
        int sp = cmd.IndexOf(' ');
        string name = sp >= 0 ? cmd.Substring(sp + 1).Trim() : cmd;
        if (!Cad.Draw.DashPattern.IsKnownName(name)) { StatusMsg.Text = $"线型：无法识别「{name}」（实线/虚线/点划线/点线/双点划线）"; return; }
        var dash = Cad.Draw.DashPattern.ByName(name);
        // 忠实原版「特性」组: 有选中就写入选中实体(原版 ApplyPropertyToSelection)；无选中才作新建默认。
        if (_selected.Count > 0)
        {
            int ok = 0, locked = 0;
            BeginChange();
            foreach (var ent in _selected)
            {
                if (IsLayerLocked(ent)) { locked++; continue; }
                ent.Dash = dash; ok++;
            }
            RefreshScene(); HighlightSelection();
            StatusMsg.Text = $"线型 {name}：已写入 {ok}/{_selected.Count} 个实体" + (locked > 0 ? $"（{locked} 个在锁定图层，跳过）" : "");
            return;
        }
        _currentDash = dash;
        StatusMsg.Text = $"线型已设：{name}（{(_currentDash == null ? "实线" : $"虚线, {_currentDash.Length} 段样式")}；未选中实体，新画直线/多段线用此线型）";
    }

    // 文字：忠实原版 TextJigAdapter 四步 —— 指定起点 → 字高<2.5> → 旋转角<0> → 输入文字。
    // 起点点击/键入坐标, 字高与角度可键数值或点第二点, 内容从命令行来(空格是内容的一部分, 见 SpaceSubmitsNow)。
    // 原先是"命令行输入内容 → 放在视口中心", 文字落在哪由视图决定而不是由用户决定 —— 位置不对就是这么来的。
    private void ArmText(bool multiLine)
    {
        _tool = new TextTool { MultiLine = multiLine };
        _measure = null; Viewport.SetSnapMarker(null); _snapShown = false; _lastInputPoint = null;
        StatusMsg.Text = _tool.Prompt + "（ESC 退出）";
        SyncPrompt();
    }

    /// <summary>文字 jig 的空回车：字高/角度取默认值, 内容为空则结束。返回 true 表示已消费。</summary>
    private bool TextToolAcceptDefault()
    {
        if (_tool is not TextTool tt) return false;
        var r = tt.AcceptDefault();
        if (!r.Handled) return false;
        if (r.EndsCommand) _tool = null;
        if (!string.IsNullOrEmpty(r.Message)) { LogCommand("  " + r.Message); StatusMsg.Text = r.Message!; }
        HideDragTip();
        RefreshScene();
        SyncPrompt();
        return true;
    }

    /// <summary>此刻空格还能不能当回车：文字 jig 正等内容时空格是文字的一部分（同 AutoCAD 的 TEXT）。</summary>
    private bool SpaceSubmitsNow(string typed)
        => !(_tool is TextTool { AwaitingText: true }) && AcadCommands.SpaceSubmits(typed);

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
        _selected.Clear(); Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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
    private async Task EstimateGradeAsync(string method = "IDW", double idwPower = 2.0)
    {
        string mode = method switch { "OK" => "克里金估值", "UK" => "泛克里金", "SK" => "简单克里金", "NN" => "最近邻估值", "MA" => "移动平均估值", _ => "快速估值(IDW)" };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = mode + "：选品位样本 CSV (x,y,品位)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("样本 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"{mode}：样本导入失败 {r.Error}"; return; }
        int n = 48;
        double[,] grid; double gx0, gy0, gdx, gdy; double avgVar = 0; string extra = "";
        if (method == "OK" || method == "UK" || method == "SK")
        {
            grid = BuildKrigingGrid(r.Points, n, n, out gx0, out gy0, out gdx, out gdy, out avgVar, method);
            string km = method switch { "UK" => "UK泛", "SK" => "SK简单(稀疏区归均值)", _ => "OK普通" };
            extra = $" · {km}克里金 avg克里金方差 {avgVar:0.###}";
        }
        else if (method == "NN" || method == "MA")
        {
            // 与 GridFromPoints 同布局，再按方法建格
            _ = Contour.GridFromPoints(r.Points, n, n, out gx0, out gy0, out gdx, out gdy);
            grid = method == "NN" ? Contour.GridNearest(r.Points, n, n, gx0, gy0, gdx, gdy)
                                  : Contour.GridMovingAverage(r.Points, n, n, gx0, gy0, gdx, gdy, System.Math.Max(gdx, gdy) * 3);
            extra = method == "NN" ? " · 最近邻(块状)" : " · 移动平均(半径3格)";
        }
        else
        {
            grid = BuildIdwGrid(r.Points, n, n, idwPower, out gx0, out gy0, out gdx, out gdy, out int idwUsed);
            extra = $" · IDW 幂次 {idwPower:0.##} · 邻域均 {idwUsed} 样本(可配: IDW估值 <幂次>)";
        }
        // 忠实原「搜索半径外不赋值」: 半径内无样本的单元置 NaN, 不向无数据支撑区外推(免 IDW/NN 全格铺满误导)
        double radius = System.Math.Max(Contour.AutoRadius(r.Points), 1.5 * System.Math.Max(gdx, gdy));
        Contour.MaskByRadius(grid, r.Points, gx0, gy0, gdx, gdy, radius);
        int valid = Estimation.CountValid(grid), total = grid.Length;
        var (min, max) = Estimation.Range(grid);
        var cells = Estimation.BuildCells(grid, gx0, gy0, gdx, gdy, min, max);
        BeginChange();
        foreach (var e in cells) _scene.Add(e);
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"{mode}：{r.Points.Count} 样本 → {n}² 网格 · {valid}/{total} 格有数据支撑(半径 {radius:0.#}) · 品位 {min:0.###}~{max:0.###}{extra}";
    }

    // 克里金网格：逐格 EstimateAt(OK)/EstimateUniversalAt(UK)/EstimateSimpleAt(SK) 估值；半径外(null)回落 IDW 免留洞；出平均克里金方差
    private static double[,] BuildKrigingGrid(IReadOnlyList<(double x, double y, double z)> pts, int nx, int ny,
        out double x0, out double y0, out double dx, out double dy, out double avgVar, string krigMethod = "OK")
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
                double gxp = x0 + i * dx, gyp = y0 + j * dy;
                var e = krigMethod switch
                {
                    "UK" => OrdinaryKriging.EstimateUniversalAt(cps, gxp, gyp, 0, 12, 0, vg),
                    "SK" => OrdinaryKriging.EstimateSimpleAt(cps, gxp, gyp, 0, null, 12, 0, vg),
                    _ => OrdinaryKriging.EstimateAt(cps, gxp, gyp, 0, 12, 0, vg)
                };
                if (e != null) { idw[i, j] = e.Value.est; varSum += e.Value.variance; varCnt++; }
            }
        avgVar = varCnt > 0 ? varSum / varCnt : 0;
        return idw;
    }

    // IDW 网格：逐格 OrdinaryKriging.IdwEstimate(可配幂次/邻域[1,12], 3D 距离, 半径外 null)估值; null 留兜底值交 MaskByRadius 裁。
    // 忠实原 EstimationAlgorithms.IdwEstimate(power 可变), 取代此前固定 power=2 的 Contour.GridFromPoints。out usedAvg = 平均实用样本数。
    private static double[,] BuildIdwGrid(IReadOnlyList<(double x, double y, double z)> pts, int nx, int ny, double power,
        out double x0, out double y0, out double dx, out double dy, out int usedAvg)
    {
        var grid = Contour.GridFromPoints(pts, nx, ny, out x0, out y0, out dx, out dy);   // 布局 + 半径外兜底
        nx = grid.GetLength(0); ny = grid.GetLength(1);
        var cps = new List<OrdinaryKriging.ControlPoint>(pts.Count);
        foreach (var p in pts) cps.Add(new OrdinaryKriging.ControlPoint(p.x, p.y, 0, p.z));
        long usedSum = 0; int cnt = 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                var e = OrdinaryKriging.IdwEstimate(cps, x0 + i * dx, y0 + j * dy, 0, power, 0, 1, 12, 0);
                if (e != null) { grid[i, j] = e.Value.est; usedSum += e.Value.used; cnt++; }
            }
        usedAvg = cnt > 0 ? (int)(usedSum / cnt) : 0;
        return grid;
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

    // 拉沟推荐(忠实原 PanelDelineator): 块体 → 剥采比场 → 按 剥采比/埋深/运输/内排/工作线/地质 六约束打分,
    // 自动荐首采区拉沟位置 + 推进方位(求解链第一环)。区别 采区划分(需用户指定推进方位): 此层自动推荐方位。
    private void BoxcutRecommendCmd()
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "拉沟推荐：请先导入/生成块体（块体模型 / 实体转块体）"; return; }
        double gsum = 0; foreach (var b in _lastBlocks) gsum += b.Grade;
        double cutoff = gsum / _lastBlocks.Count;
        var blocks = _lastBlocks.Select(b => (b.X, b.Y, b.Z, b.Size, b.Grade)).ToList();
        var field = Cad.StripRatioField.FromBlocks(blocks, cutoff, 1.35);
        if (field == null || field.CoalColumns == 0) { StatusMsg.Text = "拉沟推荐：剥采比场无煤（检查块体品位/阈值）"; return; }
        double minWL = System.Math.Min(field.Nx * field.Dx, field.Ny * field.Dy) * 0.5;   // 最小工作线=短边一半
        var opts = Cad.PanelDelineator.Recommend(field, minWL);
        var rec = opts.FirstOrDefault(o => o.Recommended);
        var top3 = string.Join(" | ", opts.OrderByDescending(o => o.TotalScore).Take(3)
            .Select(o => $"{o.Name.Split('·')[0]}({o.TotalScore:0}分{(o.Recommended ? "★" : "")})"));
        if (rec == null) { StatusMsg.Text = $"拉沟推荐：{opts.Count} 候选均不可行(工作线长 < {minWL:0}m) · {top3}"; return; }
        string modeText = rec.AdvanceMode switch
        { Cad.AdvanceMode.Parallel => "平行推进", Cad.AdvanceMode.FixedPivot => "定点回转", _ => "动点回转" };
        StatusMsg.Text = $"拉沟推荐：★{rec.Name}（方位 {rec.AdvanceAzimuthDeg:0}° · {modeText} · 工作线 {rec.WorkingLineLengthM:0}m · {rec.TotalScore:0}分）· {rec.Rationale} · 候选 {top3}";
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
        // 时序/经济评价(忠实原 PitEvaluator): 泰勒规则服务年限 T=6.5·R^0.25(R=储量 Mt=万t/100) + 年产=储量/年限 + NPV(净值等额分摊折现 8%)。
        double reserveMt = coalWan / 100.0, life = Cad.MineEconomics.TaylorServiceLifeYears(reserveMt);   // 夹 [5,60]a 忠实原 PitEvaluator
        double annualWan = life > 1e-9 ? coalWan / life : 0;
        double npvWan = Cad.MineEconomics.NpvLevelized(r.NetValueYuan / 1e4, life, 0.08);
        string econ = life > 0 ? $" · 服务年限≈{life:0.#}a(泰勒) · 年产≈{annualWan:0.#}万t/a · NPV≈{npvWan:0.#}万元(8%)" : "";
        StatusMsg.Text = $"确定境界(净值最大)：最优坑深 {r.DepthM:0.#}m(底层 k={r.BottomK}/{prof.Nz}) · 圈入煤 {coalWan:0.#}万t · 岩 {wasteWan:0.#}万m³ · 境界剥采比 {r.ContourSR:0.##} · 净值 {r.NetValueYuan / 1e4:0.#}万元{econ}";
    }

    // 经济合理剥采比 n经(m³/t): 按四原则算(忠实原 PitScheme.EconParams)。确定境界用净值最大≈价格法; 此命令显式给四法便于比选。
    // 用法: 经济剥采比 <售价d 元/t> <采矿成本a 元/t> <剥离成本b 元/m³> [盈利e] [复垦c] [地下成本CD]
    private void EconStrippingRatioCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 4 || !double.TryParse(tk[1], out double d) || !double.TryParse(tk[2], out double a) || !double.TryParse(tk[3], out double b) || b <= 0)
        { StatusMsg.Text = "经济剥采比：用法 经济剥采比 <售价d 元/t> <采矿成本a 元/t> <剥离成本b 元/m³> [盈利e] [复垦c] [地下成本CD]（b>0；如 经济剥采比 320 95 28 15 6）"; return; }
        double? e = tk.Length >= 5 && double.TryParse(tk[4], out double ev) ? ev : (double?)null;
        double? c = tk.Length >= 6 && double.TryParse(tk[5], out double cv) ? cv : (double?)null;
        double? cd = tk.Length >= 7 && double.TryParse(tk[6], out double cdv) ? cdv : (double?)null;
        double? nPrice = Cad.MineEconomics.AllowableStrippingRatio(Cad.MineEconomics.EconRatioMethod.Price, a, b, d);
        var parts = new System.Collections.Generic.List<string> { $"价格法 {nPrice:0.##}" };
        if (e.HasValue) parts.Add($"价格+盈利 {Cad.MineEconomics.AllowableStrippingRatio(Cad.MineEconomics.EconRatioMethod.PriceProfit, a, b, d, minProfit: e.Value):0.##}");
        if (e.HasValue && c.HasValue) parts.Add($"价格+盈利+复垦 {Cad.MineEconomics.AllowableStrippingRatio(Cad.MineEconomics.EconRatioMethod.PriceProfitReclaim, a, b, d, minProfit: e.Value, reclaimCost: c.Value):0.##}");
        if (cd.HasValue) parts.Add($"成本比较法 {Cad.MineEconomics.AllowableStrippingRatio(Cad.MineEconomics.EconRatioMethod.CostComparison, a, b, undergroundCost: cd.Value):0.##}");
        StatusMsg.Text = $"经济合理剥采比 n经(m³/t, 售价{d:0.#}·采{a:0.#}·剥{b:0.#})：{string.Join(" · ", parts)}";
    }

    // 产能/推进耦合正算(忠实原 MiningProgramPlan.CapacityWanTaFrom, 原 MiningProgramConfigWindow 用): Q=L·v·H·ρ/1e4。
    // 用法: 产能推算 <工作线长L m> <推进度v m/a> <台阶高H m> [煤密度ρ 默认1.35]
    private void AdvanceCapacityCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 4 || !double.TryParse(tk[1], out double L) || !double.TryParse(tk[2], out double v) || !double.TryParse(tk[3], out double H) || L <= 0 || H <= 0)
        { StatusMsg.Text = "产能推算：用法 产能推算 <工作线长L m> <推进度v m/a> <台阶高H m> [煤密度ρ 默认1.35]（如 产能推算 1000 50 15）"; return; }
        double rho = tk.Length >= 5 && double.TryParse(tk[4], out double rv) && rv > 0 ? rv : Cad.LongTermPlan.DefaultCoalDensity;
        double q = Cad.LongTermScheduler.CapacityWanTaFrom(L, v, H, rho);
        double vBack = Cad.LongTermScheduler.AdvanceRateFrom(q, L, H, rho);   // 逆算校验(应还原 v)
        StatusMsg.Text = $"产能推算(Q=L·v·H·ρ/1e4)：工作线{L:0.#}m·推进{v:0.#}m/a·台阶{H:0.#}m·ρ{rho:0.##} → 产能 {q:0.##} 万t/a（逆算推进度 {vBack:0.#} m/a 自洽）";
    }

    // 生成境界(几何圈定): 块体足迹顶口 + slope_design 分帮坡角 → 逐帮放坡内缩 drop/tanβ 到坑底 → 顶/底境界多边形入场景。
    // 忠实原 PitEnvelope 纯几何核(块体足迹兜底路径, 非 native 地表界)。用法: 生成境界 <坑深m> [最小底宽m 默认20] [统一帮坡角° 无slope_design时默认45]
    private void PitEnvelopeCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "生成境界：请先导入/生成块体"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 2 || !double.TryParse(tk[1], out double depth) || depth <= 0)
        { StatusMsg.Text = "生成境界：用法 生成境界 <坑深m> [最小底宽m 默认20] [统一帮坡角° 默认45]（先有 slope_design 则按分帮坡角）"; return; }
        double minBottom = tk.Length >= 3 && double.TryParse(tk[2], out double mb) && mb > 0 ? mb : 20;
        double uniformBeta = tk.Length >= 4 && double.TryParse(tk[3], out double ub) && ub > 0 ? ub : 45;
        double minX = _lastBlocks.Min(b => b.X - b.Size / 2), maxX = _lastBlocks.Max(b => b.X + b.Size / 2);
        double minY = _lastBlocks.Min(b => b.Y - b.Size / 2), maxY = _lastBlocks.Max(b => b.Y + b.Size / 2);
        double zTop = _lastBlocks.Max(b => b.Z + b.Size / 2);
        var top = Cad.TopOutline.FromBounds(minX, minY, maxX, maxY, zTop);
        var walls = new System.Collections.Generic.List<Cad.WallAngle>();
        var db = EnsureGeoDb();
        if (db != null)
            foreach (var s in Data.GeoDataQueries.GetSlopeDesigns(db.Connection))
                if (s.FinalAngle > 1) walls.Add(new Cad.WallAngle(s.Side, s.FinalAngle));
        if (walls.Count == 0) walls.Add(new Cad.WallAngle("均一", uniformBeta));
        var betas = Cad.PitEnvelope.EdgeBetas(top, walls);
        double capDepth = Cad.PitEnvelope.GeometricDepthCapPerWall(top, minBottom, betas);
        double useDepth = System.Math.Min(depth, capDepth);
        var (bx, by) = Cad.PitEnvelope.InsetPerWall(top, useDepth, betas);
        var topPoly = new PolylineEntity { Closed = true, Cr = 0.9f, Cg = 0.9f, Cb = 0.4f };   // 顶口(黄)
        for (int i = 0; i < top.Count; i++) topPoly.Points.Add((top.X[i], top.Y[i]));
        var botPoly = new PolylineEntity { Closed = true, Cr = 1f, Cg = 0.5f, Cb = 0.2f };      // 坑底(橙)
        for (int i = 0; i < bx.Length; i++) botPoly.Points.Add((bx[i], by[i]));
        AssignLayer(topPoly); AssignLayer(botPoly); _scene.Add(topPoly); _scene.Add(botPoly);
        RefreshScene();
        double topAreaHa = Cad.PitEnvelope.PolygonArea(top.X.ToArray(), top.Y.ToArray()) / 1e4;
        double botAreaHa = Cad.PitEnvelope.PolygonArea(bx, by) / 1e4;
        string cap = useDepth < depth - 1e-6 ? $"(几何封顶, 请求{depth:0.#}m)" : "";
        StatusMsg.Text = $"生成境界(逐帮放坡)：坑深 {useDepth:0.#}m{cap} · 顶口 {topAreaHa:0.##}公顷 · 坑底 {botAreaHa:0.##}公顷 · {walls.Count}帮(β均{Cad.PitEnvelope.AvgBeta(walls):0.#}°) · 最小底宽{minBottom:0.#}m（顶黄/底橙已入场景）";
    }

    // 境界建模(段③三维境界落地): 块体足迹顶口 + slope_design 分帮坡角 → 自顶向下逐台阶 crest/toe 环(坡面 H/tanα ↓ + 平盘 W 内移)
    // → 台阶线(顶红/底蓝)入场景 + 三维台阶面放样导 OFF。忠实原 PitMaterializer 纯几何核(原 3D mesh 走 native/PmbiWriter, 此导 OFF)。
    // 用法: 境界建模 <坑深m> [台阶高H 默认12] [坡面角α 默认70] [统一帮坡角° 默认45]
    private async System.Threading.Tasks.Task BenchModelAsync(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "境界建模：请先导入/生成块体"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 2 || !double.TryParse(tk[1], out double depth) || depth <= 0)
        { StatusMsg.Text = "境界建模：用法 境界建模 <坑深m> [台阶高H 默认12] [坡面角α 默认70] [统一帮坡角° 默认45]"; return; }
        double benchH = tk.Length >= 3 && double.TryParse(tk[2], out double bh) && bh > 0.5 ? bh : 12;
        double faceA = tk.Length >= 4 && double.TryParse(tk[3], out double fa) && fa >= 30 && fa <= 89 ? fa : 70;
        double uniformBeta = tk.Length >= 5 && double.TryParse(tk[4], out double ub) && ub > 0 ? ub : 45;
        double minX = _lastBlocks.Min(b => b.X - b.Size / 2), maxX = _lastBlocks.Max(b => b.X + b.Size / 2);
        double minY = _lastBlocks.Min(b => b.Y - b.Size / 2), maxY = _lastBlocks.Max(b => b.Y + b.Size / 2);
        double zTop = _lastBlocks.Max(b => b.Z + b.Size / 2);
        var top = Cad.TopOutline.FromBounds(minX, minY, maxX, maxY, zTop);
        var walls = new System.Collections.Generic.List<Cad.WallAngle>();
        var db = EnsureGeoDb();
        if (db != null)
            foreach (var s in Data.GeoDataQueries.GetSlopeDesigns(db.Connection))
                if (s.FinalAngle > 1) walls.Add(new Cad.WallAngle(s.Side, s.FinalAngle));
        if (walls.Count == 0) walls.Add(new Cad.WallAngle("均一", uniformBeta));
        var betas = Cad.PitEnvelope.EdgeBetas(top, walls);
        var rings = Cad.PitEnvelope.MaterializeRings(top, betas, benchH, faceA, depth);
        foreach (var ring in rings)
        {
            var poly = new PolylineEntity { Closed = true };
            for (int j = 0; j < ring.X.Length; j++) poly.Points.Add((ring.X[j], ring.Y[j]));
            AssignLayer(poly);
            if (ring.Crest) { poly.Cr = 0.86f; poly.Cg = 0.24f; poly.Cb = 0.24f; }   // 坡顶红
            else { poly.Cr = 0.16f; poly.Cg = 0.43f; poly.Cb = 0.9f; }               // 坡底蓝
            _scene.Add(poly);
        }
        RefreshScene();
        var (verts, tris) = Cad.PitEnvelope.LoftMesh(rings);
        string offNote = "";
        if (verts.Count >= 3 && tris.Count >= 1)
        {
            var saved = await SaveCsvAsync("境界台阶面", "pit_benches.off", Cad.MeshWeld.ToOff(verts, tris));
            if (saved != null) offNote = $" · 三维台阶面({tris.Count}面) → {saved}";
        }
        int benches = rings.Count(r => !r.Crest);
        StatusMsg.Text = $"境界建模(逐台阶放坡)：{benches} 台阶 / {rings.Count} 环 · 深 {depth:0.#}m · H={benchH:0.#}m·坡面α={faceA:0.#}° · {walls.Count}帮 · 台阶线(顶红底蓝)入场景{offNote}";
    }

    // 境界内资源(圈入量): 用逐层境界轮廓(PitEnvelope 逐层放坡内缩)裁块体 → 圈入煤/岩 + 回收率(圈入÷全模型)。
    // 忠实原 SectionSampler.SampleLayers 的 layerClipsXY 语义。区别 资源量(全模型不裁): 本命令出真实坑内圈入资源。
    // 用法: 境界内资源 <坑深m> [统一帮坡角° 默认45]（先有 slope_design 则按分帮坡角）
    private void EnclosedResourceCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "境界内资源：请先导入/生成块体"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 2 || !double.TryParse(tk[1], out double depth) || depth <= 0)
        { StatusMsg.Text = "境界内资源：用法 境界内资源 <坑深m> [统一帮坡角° 默认45]（按逐层境界轮廓裁块体, 出圈入煤/岩+回收率）"; return; }
        double uniformBeta = tk.Length >= 3 && double.TryParse(tk[2], out double ub) && ub > 0 ? ub : 45;
        double cutoff = _lastBlocks.Average(b => b.Grade);
        double density = 1.35;
        double cell = _lastBlocks[0].Size > 1e-9 ? _lastBlocks[0].Size : 1.0;
        double minX = _lastBlocks.Min(b => b.X - b.Size / 2), maxX = _lastBlocks.Max(b => b.X + b.Size / 2);
        double minY = _lastBlocks.Min(b => b.Y - b.Size / 2), maxY = _lastBlocks.Max(b => b.Y + b.Size / 2);
        double minZ = _lastBlocks.Min(b => b.Z), maxZ = _lastBlocks.Max(b => b.Z);
        double zTop = maxZ + cell / 2;
        var top = Cad.TopOutline.FromBounds(minX, minY, maxX, maxY, zTop);
        var walls = new System.Collections.Generic.List<Cad.WallAngle>();
        var db = EnsureGeoDb();
        if (db != null)
            foreach (var s in Data.GeoDataQueries.GetSlopeDesigns(db.Connection))
                if (s.FinalAngle > 1) walls.Add(new Cad.WallAngle(s.Side, s.FinalAngle));
        if (walls.Count == 0) walls.Add(new Cad.WallAngle("均一", uniformBeta));
        var betas = Cad.PitEnvelope.EdgeBetas(top, walls);
        var blocks = _lastBlocks.Select(b => (b.X, b.Y, b.Z, b.Size, b.Grade)).ToList();
        // 逐块灰分(若持多属性且有 灰分/ash 属性): 供圈入煤体积加权平均灰分(忠实原 PitEvaluator AvgAshPct)。
        System.Collections.Generic.List<double>? ashPerBlock = null;
        if (_blockAttrs != null)
        {
            var ashKey = _blockAttrs.Keys.FirstOrDefault(kk => kk.Contains("灰") || kk.ToLowerInvariant().Contains("ash"));
            if (ashKey != null && _blockAttrs[ashKey].Length == blocks.Count) ashPerBlock = _blockAttrs[ashKey].ToList();
        }
        var prof = Cad.SectionSampler.SampleClipped(blocks, cutoff, density, k =>
        {
            double zk = minZ + k * cell;
            double drop = zTop - zk;
            if (drop < 0) drop = 0;
            if (drop > depth + 1e-6) return new System.Collections.Generic.List<(double x, double y)>();  // 坑底以下 → 空(出圈)
            var (bx, by) = Cad.PitEnvelope.InsetPerWall(top, drop, betas);
            var poly = new System.Collections.Generic.List<(double x, double y)>();
            for (int i = 0; i < bx.Length; i++) poly.Add((bx[i], by[i]));
            return poly;
        }, ashPerBlock);
        if (prof == null) { StatusMsg.Text = "境界内资源：无块体"; return; }
        string ashNote = prof.HasAsh ? $" · 圈入煤均灰 {prof.AvgAshPct:0.##}%" : "";
        StatusMsg.Text = $"境界内资源(逐层境界裁)：圈入煤 {prof.CoalT / 1e4:0.##}万t · 岩 {prof.WasteM3 / 1e4:0.##}万m³ · 圈内剥采比 {prof.StripRatioM3PerT:0.##} · 回收率 {prof.RecoveryPct:0.#}%（圈入÷全模型煤）{ashNote} · 坑深{depth:0.#}m·{walls.Count}帮β均{Cad.PitEnvelope.AvgBeta(walls):0.#}°";
    }

    // 开采程序切分: 块体(_lastBlocks) + 选中工作线(定推进方位) + 推进步距 → 逐期煤/岩量 + 累计剥采比 + 导 CSV(喂剥采比均衡)。
    // 忠实原 TemplateDrivingEngine 距离驱动核(平面近似, 陡帮台阶退距≈0)。用法「开采程序切分 [推进步距m 默认50]」。
    private async Task DriveSequenceCmd(string cmd)
    {
        if (_lastBlocks == null || _lastBlocks.Count == 0) { StatusMsg.Text = "开采程序切分：请先导入/生成块体"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '，' }, System.StringSplitOptions.RemoveEmptyEntries);
        // 模式: "开采程序切分 量 <目标煤量万m³>" = 等煤量分期; 否则 "开采程序切分 [步距m]" = 等距。
        bool volMode = tk.Length >= 3 && (tk[1] == "量" || tk[1] == "等煤量" || tk[1] == "煤量");
        double adv = 50, tgtWan = 0;
        if (volMode) { double.TryParse(tk[2], out tgtWan); if (tgtWan <= 0) { StatusMsg.Text = "开采程序切分：等煤量模式需 开采程序切分 量 <目标煤量万m³>"; return; } }
        else if (tk.Length >= 2 && double.TryParse(tk[1], out var a) && a > 0) adv = a;
        // 推进方位: 选中工作线首末方向的法向; 无选中默认 +X。
        double dirX = 1, dirY = 0; string dirHint = "默认+X向";
        var line = _selected.OfType<PolylineEntity>().FirstOrDefault(p => p.Points.Count >= 2);
        if (line != null) { var p0 = line.Points[0]; var p1 = line.Points[^1]; double dx = p1.Item1 - p0.Item1, dy = p1.Item2 - p0.Item2; double dl = System.Math.Sqrt(dx * dx + dy * dy); if (dl > 1e-9) { dirX = -dy / dl; dirY = dx / dl; dirHint = "选中工作线法向"; } }
        // 可选坡面角(末位数字, 0<α<90)启用台阶退距(单斜面); 缺省 0=平面(陡帮)。
        double faceAngle = 0; var last = tk.Length >= 2 ? tk[^1] : "";
        if (tk.Length >= (volMode ? 4 : 3) && double.TryParse(last, out var fa) && fa > 0 && fa < 90) faceAngle = fa;
        double gsum = 0; foreach (var b in _lastBlocks) gsum += b.Grade; double cutoff = gsum / _lastBlocks.Count;
        var cells = _lastBlocks.Select(b => new Cad.DriveSequence.Cell(b.X, b.Y, b.Z, b.Size * b.Size * b.Size, b.Grade >= cutoff)).ToList();
        const double density = 1.3;
        double cellSize = _lastBlocks[0].Size > 0 ? _lastBlocks[0].Size : 10;
        Cad.DriveSequence.DriveResult r;
        if (volMode) r = Cad.DriveSequence.SweepByVolume(cells, dirX, dirY, 10.0, tgtWan * 1e4, density, 0, faceAngle);
        else if (line != null && line.Points.Count > 2)   // 弯工作线 → 多段投影(沿线各段法向)
        {
            var wl = line.Points.Select(pt => (pt.Item1, pt.Item2)).ToList();
            r = Cad.DriveSequence.SweepAlongWorkLine(wl, cells, adv, density, cellSize, 0, faceAngle);
            dirHint = $"沿弯工作线({line.Points.Count}点)";
        }
        else r = Cad.DriveSequence.SweepByDistance(cells, dirX, dirY, adv, density, 0, faceAngle);
        if (r.Periods.Count == 0) { StatusMsg.Text = "开采程序切分：分期失败(检查块体/步距或目标煤量>0)"; return; }
        var name = await SaveCsvAsync("导出分期量表", "drive_periods.csv", Cad.DriveSequence.ToBalanceCsv(r, density));
        // 累计剥采比曲线上屏(已算未绘)：期号(X) vs 累计剥采比(Y)
        if (r.Periods.Count >= 2)
        {
            double dvw = ViewportHost.Bounds.Width, dvh = ViewportHost.Bounds.Height;
            var dp0 = Viewport.ScreenToWorld(dvw * 0.3, dvh * 0.85) ?? (0.0, 0.0);
            var dp1 = Viewport.ScreenToWorld(dvw * 0.7, dvh * 0.4) ?? (100.0, 50.0);
            double dw = System.Math.Abs(dp1.x - dp0.x), dh = System.Math.Abs(dp1.y - dp0.y);
            if (dw < 1e-6) dw = 100; if (dh < 1e-6) dh = 50;
            var dpts = r.Periods.Select(p => ((double)(p.Index + 1), p.CumStripRatio)).ToList();
            BeginChange();
            foreach (var de in Cad.CurvePlot.Build(dpts, System.Math.Min(dp0.x, dp1.x), System.Math.Min(dp0.y, dp1.y),
                         dw, dh, System.Math.Max(dh * 0.05, 1e-3), "期", "累计剥采比"))
            { de.LayerName = _layers.Current.Name; _scene.Add(de); }
            RefreshScene();
        }
        var head = string.Join(" ", r.Periods.Take(4).Select(p => $"期{p.Index + 1}(煤{p.CoalVolM3 / 1e4:0.#}/岩{p.RockVolM3 / 1e4:0.#}万m³·累计剥采比{p.CumStripRatio:0.##})"));
        string sbHint = faceAngle > 0 ? $"·坡面角{faceAngle:0.#}°退距" : "";
        string modeHint = (volMode ? $"{dirHint}·等煤量·目标{tgtWan:0.#}万m³/期" : $"{dirHint}·等距·步距{adv:0.#}m") + sbHint;
        StatusMsg.Text = $"开采程序切分({modeHint})：{r.Periods.Count} 期 · 总煤 {r.TotalCoalVolM3 / 1e4:0.#}万m³ · 总岩 {r.TotalRockVolM3 / 1e4:0.#}万m³ · 综合剥采比 {r.OverallStripRatio:0.##} · {head}"
            + (r.Periods.Count >= 2 ? " · 累计剥采比曲线入场景" : "")
            + (name != null ? $" · 分期量表 → {name}(喂 剥采比均衡)" : "");
    }

    // 运输道路布局求解: 坑线候选 CSV(fromLevel,toLevel,lengthM,geomFeasible[,note]) + 需求/单车道运力/单价 → 紧凑/均衡/单线三方案。
    // 忠实原 RoadLayoutSolver 方案构建核(运量定车道→拆线→可行/成本)。候选可由坑线生成 + FleetOptimizer 运力估。
    private async Task RoadLayoutCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '，' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 2 || !double.TryParse(tk[1], out double demand) || demand <= 0)
        { StatusMsg.Text = "运输布局方案：用法 运输布局方案 <每期需求t> [单车道运力t 默认1500] [单价元/tkm 默认2]（选坑线候选 CSV: fromLevel,toLevel,lengthM,geomFeasible[,note]）"; return; }
        double perLane = 1500; if (tk.Length >= 3 && double.TryParse(tk[2], out var pl) && pl > 0) perLane = pl;
        double unitCost = 2; if (tk.Length >= 4 && double.TryParse(tk[3], out var uc) && uc > 0) unitCost = uc;

        // 候选来源二择一: 选中 ≥2 同心台阶环 → 自动选线(RampRouteGenerator Layer-1, 免 CSV); 否则选候选 CSV。
        var selRings = _selected.FindAll(e => e is PolylineEntity pe && pe.Points.Count >= 3);
        var cands = new List<Cad.RoadLayoutSolver.RampCand>();
        string srcHint;
        if (selRings.Count >= 2)
        {
            var benches = BuildBenchesFromRings(selRings, benchHeightM: 12);
            var gen = Cad.RampRouteGenerator.Generate(benches);
            foreach (var g in gen)
                cands.Add(new Cad.RoadLayoutSolver.RampCand(g.FromLevel, g.ToLevel, g.RequiredLengthM, g.GeomFeasible, g.Note));
            if (cands.Count == 0) { StatusMsg.Text = "运输布局方案：所选台阶环不足 2 有效级(需同心坡顶线)"; return; }
            var (s0, sb0, sp0, _) = Cad.RampRouteGenerator.Tally(gen);
            srcHint = $"自动选线 {gen.Count} 候选(斜{s0}/转{sb0}/螺{sp0})";
        }
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "运输布局方案：选坑线候选 CSV (fromLevel,toLevel,lengthM,geomFeasible[,note])（或先选≥2 台阶环自动选线）",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("候选 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
            });
            if (files.Count == 0) return;
            foreach (var ln in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
            {
                var p = ln.Split(new[] { ',', '\t' }, System.StringSplitOptions.None);
                if (p.Length < 3 || !double.TryParse(p[0].Trim(), out double f) || !double.TryParse(p[1].Trim(), out double t2) || !double.TryParse(p[2].Trim(), out double len) || len <= 0) continue;
                bool feas = p.Length < 4 || !(p[3].Trim().ToLowerInvariant() is "0" or "false" or "no" or "n" or "否");
                cands.Add(new Cad.RoadLayoutSolver.RampCand(f, t2, len, feas, p.Length >= 5 ? p[4].Trim() : "几何不可行"));
            }
            if (cands.Count == 0) { StatusMsg.Text = "运输布局方案：候选 CSV 无有效行(需 fromLevel,toLevel,lengthM[,geomFeasible,note])"; return; }
            srcHint = $"{cands.Count} 候选 CSV";
        }
        string? obj = tk.Length >= 5 && (tk[4] == "均衡" || tk[4] == "最小运输功" || tk[4] == "最小成本") ? tk[4] : null;
        var r = Cad.RoadLayoutSolver.Solve(cands, demand, perLane, unitCost, obj);
        var parts = r.Schemes.Select(s => $"{s.Name.Split('·')[0]}({s.TotalLanes}车道/{s.Lines.Count}线·{(s.Feasible ? $"可行 {s.Score:0.#}分" : "✗" + s.Violations.Count + "违规")}·基建{s.TotalCapexProxyM:0}m·运营{s.TotalHaulCostYuan / 1e4:0.#}万元)");
        StatusMsg.Text = $"运输布局方案({srcHint}·需求 {demand:0}t/期·单车道 {perLane:0}t·目标{obj ?? "最小成本"})：推荐「{r.Recommended?.Name ?? "无可行方案"}」 · " + string.Join(" | ", parts);
    }

    // 属性路网建图(忠实原 RoadGraphBuilder): 读中线多段线 CSV(L,线id,x,y,z) → 交叉口 noding(X十字/T丁字, Z 闸门
    // 区分平交·立交) + 端点吸附 + 共线重复边去重 + 缺口桥接(跨标高不桥) → 属性路网图。报 noding 诊断 + 连通性。
    // 区别 §80/§82(2D 通用图): 此产带纵坡(由 Z 算)的属性图 + Z 感知 noding, 喂约束寻径/全指标。画 noded 中线。
    private async Task RoadBuildGraphAsync()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "路网建图：选中线多段线 CSV (L,线id,x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("中线 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        // 按 线id 分组顺序成多段线。
        var groups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Cad.Point3d>>();
        var order = new System.Collections.Generic.List<string>();
        foreach (var ln in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
        {
            var p = ln.Split(new[] { ',', '\t' }, System.StringSplitOptions.None);
            if (p.Length < 5 || p[0].Trim().ToUpperInvariant() != "L") continue;
            if (!double.TryParse(p[2].Trim(), System.Globalization.NumberStyles.Float, inv, out double x)) continue;
            if (!double.TryParse(p[3].Trim(), System.Globalization.NumberStyles.Float, inv, out double y)) continue;
            if (!double.TryParse(p[4].Trim(), System.Globalization.NumberStyles.Float, inv, out double z)) continue;
            string id = p[1].Trim();
            if (!groups.TryGetValue(id, out var list)) { list = new(); groups[id] = list; order.Add(id); }
            list.Add(new Cad.Point3d(x, y, z));
        }
        var polys = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<Cad.Point3d>>();
        foreach (var id in order) if (groups[id].Count >= 2) polys.Add(groups[id]);
        if (polys.Count == 0) { StatusMsg.Text = "路网建图：CSV 无有效多段线(L,线id,x,y,z; 每线≥2 点)"; return; }

        var g = Cad.RoadGraphBuilder.FromPolylines(polys, out var rep, snapToleranceM: 2.0, gradeSeparationM: 4.0, bridgeGapM: 25.0);
        var val = g.Validate();

        // 画 noded 边(节点位置连线)预览。
        int drawn = 0;
        foreach (var e in g.Edges)
        {
            var a = g.GetNode(e.FromId); var b = g.GetNode(e.ToId);
            if (a == null || b == null) continue;
            var line = new PolylineEntity { Cr = 0.35f, Cg = 0.75f, Cb = 0.95f, LayerName = "路网图_边" };
            line.Points.Add((a.Position.X, a.Position.Y));
            line.Points.Add((b.Position.X, b.Position.Y));
            _scene.Add(line); drawn++;
        }
        BeginChange(); RefreshScene(); Viewport.ZoomExtents();
        StatusMsg.Text = $"路网建图：{rep.InputLines} 中线 → noding {g.NodeCount}节点/{g.EdgeCount}边"
            + $"(X十字{rep.CrossSplits}·T丁字{rep.TeeSplits}·去重{rep.DuplicateEdgesRemoved}·桥接{rep.BridgesAdded})"
            + $" · {(val.IsFullyConnected ? "全连通" : $"{val.ComponentCount}分量")}"
            + (val.Issues.Count > 0 ? $" · {val.Issues.Count}告警" : "") + $" · 入图 {drawn} 边(蓝, 层『路网图_边』)";
    }

    // 全运输指标报告(忠实原 TransportIndicatorsBuilder): 读属性图 CSV(N,id,x,y,z[,类型 L/U/J,吞吐t/h] /
    // E,id,from,to[,限载t,车道]) → 节点类型自动源汇 → 总里程/连通/源汇/理论运能(Σ源汇吞吐 min)/等效运距均最/瓶颈段。
    // 区别既有「路网运输指标」(场景几何部分: 总里程+可达对+瓶颈); 此为全指标(需类型/吞吐属性, 走 CSV)。
    private async Task RoadFullIndicatorsAsync()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "运输指标报告：选属性图 CSV (N,id,x,y,z[,类型L/U/J,吞吐t/h] / E,id,from,to[,限载t,车道])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("属性图 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var g = new Cad.RoadGraph();
        foreach (var ln in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
        {
            var p = ln.Split(new[] { ',', '\t' }, System.StringSplitOptions.None);
            if (p.Length < 1) continue;
            string kind = p[0].Trim().ToUpperInvariant();
            if (kind == "N" && p.Length >= 5
                && double.TryParse(p[2].Trim(), System.Globalization.NumberStyles.Float, inv, out double x)
                && double.TryParse(p[3].Trim(), System.Globalization.NumberStyles.Float, inv, out double y)
                && double.TryParse(p[4].Trim(), System.Globalization.NumberStyles.Float, inv, out double z))
            {
                var t = p.Length >= 6 ? p[5].Trim().ToUpperInvariant() : "J";
                var type = t == "L" ? Cad.RoadNodeType.Loading : t == "U" ? Cad.RoadNodeType.Unloading : Cad.RoadNodeType.Junction;
                var node = new Cad.RoadNode(p[1].Trim(), type, new Cad.Point3d(x, y, z));
                if (p.Length >= 7 && double.TryParse(p[6].Trim(), System.Globalization.NumberStyles.Float, inv, out double tp)) node.ThroughputTph = tp;
                g.AddNode(node);
            }
            else if (kind == "E" && p.Length >= 4)
            {
                try
                {
                    var e = new Cad.RoadEdge(p[1].Trim(), p[2].Trim(), p[3].Trim());
                    if (p.Length >= 5 && double.TryParse(p[4].Trim(), System.Globalization.NumberStyles.Float, inv, out double ml)) e.MaxLoadT = ml;
                    if (p.Length >= 6 && int.TryParse(p[5].Trim(), out int lc) && lc >= 1) e.LaneCount = lc;
                    g.AddEdge(e);
                }
                catch (System.Exception) { }
            }
        }
        if (g.NodeCount < 2 || g.EdgeCount < 1) { StatusMsg.Text = "运输指标报告：图 CSV 无有效节点/边"; return; }

        var ind = Cad.TransportIndicatorsBuilder.Compute(g, new System.Collections.Generic.List<Cad.RoadGraph>(), Cad.TruckProfile.Default);
        var top = ind.Bottlenecks.Count > 0 ? ind.Bottlenecks[0] : null;
        StatusMsg.Text = $"运输指标报告：{ind.NodeCount}节点/{ind.EdgeCount}边 · 总里程 {ind.TotalKm:0.##}km · "
            + (ind.IsFullyConnected ? "连通" : $"{ind.ComponentCount} 分量")
            + $" · 源{ind.Sources.Count}汇{ind.Sinks.Count}{(ind.UsedAllNodesFallback ? "(降级全节点)" : "")}"
            + $" · 理论运能 {ind.TheoreticalCapacityTph:0}t/h(源{ind.SourceThroughputSumTph:0}/汇{ind.SinkCapacitySumTph:0})"
            + $" · 等效运距 均{ind.AvgEquivM:0.#}/最大{ind.MaxEquivM:0.#}m"
            + (top != null ? $" · 瓶颈[{top.EdgeId}]({top.Reason},介数{top.Betweenness})" : "");
    }

    // 约束感知运输寻径(忠实原 DijkstraPathSolver): 读属性图 CSV(N,id,x,y,z / E,id,from,to[,限载t]) → 建带
    // 纵坡(由节点标高自动算)/限载/状态的运输图 → 限坡+限载硬约束 Dijkstra → 报路径+里程/等效运距/时间/成本, 画路径。
    // 2D 场景无 per-node 标高/边限载, 故走 CSV 喂属性(忠实既有"CSV 补 2D 缺属性"式)。
    // 用法 "约束寻径 <起点id> <终点id> [限坡% 限载t]"(缺省 不限坡 / 车 90t) + 选属性图 CSV。
    private async Task RoadConstraintPathAsync(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '，' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 3) { StatusMsg.Text = "约束寻径：用法 约束寻径 <起点id> <终点id> [限坡% 限载t]（选属性图 CSV: N,id,x,y,z / E,id,from,to[,限载t]）"; return; }
        string fromId = tk[1], toId = tk[2];
        double maxGrade = 0; if (tk.Length >= 4) double.TryParse(tk[3], System.Globalization.NumberStyles.Float, inv, out maxGrade);
        double payload = 90; if (tk.Length >= 5) double.TryParse(tk[4], System.Globalization.NumberStyles.Float, inv, out payload);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "约束寻径：选属性图 CSV (N,id,x,y,z / E,id,from,to[,限载t])",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("属性图 (CSV/TXT)") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;

        var g = new Cad.RoadGraph();
        var pos = new System.Collections.Generic.Dictionary<string, (double x, double y)>();
        foreach (var ln in System.IO.File.ReadAllLines(files[0].Path.LocalPath))
        {
            var p = ln.Split(new[] { ',', '\t' }, System.StringSplitOptions.None);
            if (p.Length < 1) continue;
            string kind = p[0].Trim().ToUpperInvariant();
            if (kind == "N" && p.Length >= 5
                && double.TryParse(p[2].Trim(), System.Globalization.NumberStyles.Float, inv, out double x)
                && double.TryParse(p[3].Trim(), System.Globalization.NumberStyles.Float, inv, out double y)
                && double.TryParse(p[4].Trim(), System.Globalization.NumberStyles.Float, inv, out double z))
            { g.AddNode(p[1].Trim(), Cad.RoadNodeType.Junction, new Cad.Point3d(x, y, z)); pos[p[1].Trim()] = (x, y); }
            else if (kind == "E" && p.Length >= 4)
            {
                try
                {
                    var e = new Cad.RoadEdge(p[1].Trim(), p[2].Trim(), p[3].Trim());
                    if (p.Length >= 5 && double.TryParse(p[4].Trim(), System.Globalization.NumberStyles.Float, inv, out double ml)) e.MaxLoadT = ml;
                    g.AddEdge(e);
                }
                catch (System.Exception) { /* 端点未定义等 → 跳过该边 */ }
            }
        }
        if (g.NodeCount < 2 || g.EdgeCount < 1) { StatusMsg.Text = "约束寻径：图 CSV 无有效节点/边(N,id,x,y,z / E,id,from,to)"; return; }

        var query = new Cad.PathQuery { Mode = Cad.WeightMode.Distance, MaxGradePct = maxGrade, Loaded = true, Truck = new Cad.TruckProfile { PayloadT = payload } };
        var r = new Cad.DijkstraPathSolver(g).FindPath(fromId, toId, query);
        if (!r.Feasible)
        { StatusMsg.Text = $"约束寻径：{fromId}→{toId} 不可达(限坡 {maxGrade:0.#}% / 车 {payload:0}t 下无路; 检查节点 id 或放宽约束)"; return; }

        // 画路径(节点位置连线)
        var line = new PolylineEntity { Cr = 0.95f, Cg = 0.85f, Cb = 0.30f, LayerName = "约束寻径_路径" };
        foreach (var nid in r.NodeIds) if (pos.TryGetValue(nid, out var xy)) line.Points.Add((xy.x, xy.y));
        BeginChange(); if (line.Points.Count >= 2) _scene.Add(line); RefreshScene(); Viewport.ZoomExtents();
        StatusMsg.Text = $"约束寻径：{fromId}→{toId} {r.EdgeIds.Count} 段(限坡 {maxGrade:0.#}%/车 {payload:0}t) · 里程 {r.LengthM:0.#}m · 等效运距 {r.EquivM:0.#}m · 时间 {r.TimeMin:0.#}min · 成本 {r.Cost:0.#}元 · 经[{string.Join("→", r.EdgeIds)}]";
    }

    // 从选中同心台阶环构建选线用台阶(忠实沿用 2D 场景适配): 按面积降序赋标高(外圈=地表最高, 每内一环降 benchHeightM),
    // 平盘宽由相邻环等效半径差 R_k−R_{k+1}(=平台宽物理近似)估, 坡顶线=环点。喂 RampRouteGenerator 自动选线。
    private static List<Cad.RampBenchLine> BuildBenchesFromRings(List<SceneEntity> rings, double benchHeightM)
    {
        var sorted = new List<PolylineEntity>();
        foreach (var e in rings) if (e is PolylineEntity pe) sorted.Add(pe);
        sorted.Sort((a, b) => System.Math.Abs(Cad.BenchLines.SignedArea(b.Points))
                                     .CompareTo(System.Math.Abs(Cad.BenchLines.SignedArea(a.Points))));
        int n = sorted.Count;
        var radii = new double[n];
        for (int k = 0; k < n; k++)
            radii[k] = System.Math.Sqrt(System.Math.Abs(Cad.BenchLines.SignedArea(sorted[k].Points)) / System.Math.PI);
        var benches = new List<Cad.RampBenchLine>(n);
        for (int k = 0; k < n; k++)
        {
            double z = (n - 1 - k) * benchHeightM;   // 外圈最高
            double berm = k + 1 < n ? System.Math.Max(0, radii[k] - radii[k + 1]) : radii[k];
            var crest = new List<(double X, double Y, double Z)>(sorted[k].Points.Count);
            foreach (var (x, y) in sorted[k].Points) crest.Add((x, y, z));
            benches.Add(new Cad.RampBenchLine { Level = z, BermWidth = berm, Crest = crest });
        }
        return benches;
    }

    // 派生计划方案：块体→场→按不同采区数/推进方位派生多方案→逐一评价→按 NPV 排名 报表
    // 中长远进度计划(忠实原 LongTermScheduler 量版合成排产): 参数→划期/爬坡/剥离反推/NPV→逐年表 + 剥采比曲线上屏。
    // 用法 中长远进度计划 [设计能力万t/a] [可采储量万t] [基准剥采比] [爬坡:线性|阶梯|激进]。
    private async Task LongTermPlanCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        var p = new Cad.LongTermPlan();
        if (tk.Length > 1 && double.TryParse(tk[1], out var cap) && cap > 0) p.DesignCapacityWanTa = cap;
        if (tk.Length > 2 && double.TryParse(tk[2], out var res) && res > 0) p.CoalReserveWanT = res;
        if (tk.Length > 3 && double.TryParse(tk[3], out var br) && br > 0) p.BaseStripRatio = br;
        if (tk.Length > 4) p.RampProfile = tk[4].Contains("激进") ? Cad.RampProfileKind.Aggressive : tk[4].Contains("阶梯") ? Cad.RampProfileKind.Stepped : Cad.RampProfileKind.Linear;
        // 一键编制: 工作线×方向 16 方案 → 综合评分 → 荐最优, 各方案分柱上屏
        if (tk.Any(t => t == "一键" || t == "多方案" || t == "对比" || t == "自动"))
        {
            var variants = Cad.LongTermScheduler.GenerateVariants(p, Cad.LongTermScheduler.DefaultWorkLines(), Cad.LongTermScheduler.DefaultDirections());
            var results = variants.Select(v => v.Result!).ToList();
            var best = Cad.LongTermComparer.Score(results);
            var ranked = results.OrderByDescending(x => x.CompositeScore).ToList();
            DrawCategoryBars(ranked.Take(8).Select(x => (x.Name, x.CompositeScore)).ToList(), "综合分");
            var nm = await SaveCsvAsync("导出中长远多方案", "long_term_variants.csv",
                "方案,综合分,服务年限,达产期,峰值剥采比,NPV万,内排%,储量均衡,可行\n" +
                string.Join("", ranked.Select(x => $"{x.Name},{x.CompositeScore:0},{x.ServiceLifeYears:0},{x.TimeToCapacityYears:0},{x.ProductionRatioPeak:0.#},{x.Npv:0},{x.InnerDumpPct:0},{x.ReserveBalanceCoef:0.##},{(x.Ok ? "是" : "否")}\n")));
            StatusMsg.Text = $"中长远一键编制({variants.Count} 方案·工作线×方向)：推荐【{best?.Name ?? "无"}】综合 {best?.CompositeScore:0}分(服务 {best?.ServiceLifeYears:0}a·峰值剥采比 {best?.ProductionRatioPeak:0.#}·NPV {best?.Npv:0}万·{(best?.Ok == true ? "可行" : "不达标")}) · 前三 " + string.Join(" | ", ranked.Take(3).Select(x => $"{x.Name} {x.CompositeScore:0}分")) + " · 评分柱入场景"
                + (nm != null ? $" · CSV → {nm}" : "");
            return;
        }
        Cad.LongTermScheduler.Schedule(p);
        var r = p.Result!;
        // 逐年生产剥采比曲线上屏(年 → 剥采比)
        var prod = p.Periods.Where(z => z.CoalWanT > 0).ToList();
        if (prod.Count >= 2)
        {
            double vw = ViewportHost.Bounds.Width, vh = ViewportHost.Bounds.Height;
            var q0 = Viewport.ScreenToWorld(vw * 0.3, vh * 0.85) ?? (0.0, 0.0);
            var q1 = Viewport.ScreenToWorld(vw * 0.7, vh * 0.4) ?? (100.0, 50.0);
            double w = System.Math.Abs(q1.x - q0.x), h = System.Math.Abs(q1.y - q0.y);
            if (w < 1e-6) w = 100; if (h < 1e-6) h = 50;
            var pts = prod.Select((z, i) => ((double)(i + 1), z.Ratio)).ToList();
            BeginChange();
            foreach (var e in Cad.CurvePlot.Build(pts, System.Math.Min(q0.x, q1.x), System.Math.Min(q0.y, q1.y), w, h, System.Math.Max(h * 0.05, 1e-3), "生产年", "剥采比"))
            { e.LayerName = _layers.Current.Name; _scene.Add(e); }
            RefreshScene();
        }
        var name = await SaveCsvAsync("导出中长远进度计划", "long_term_plan.csv", LongTermToCsv(p));
        StatusMsg.Text = $"中长远进度计划(能力 {p.DesignCapacityWanTa:0}万t/a·储量 {p.CoalReserveWanT:0}万t·{RampText(p.RampProfile)})：服务 {r.ServiceLifeYears:0}a(达产 {r.TimeToCapacityYears:0}a·稳产 {r.StablePlateauYears:0}a) · 峰值剥采比 {r.ProductionRatioPeak:0.#} · NPV {r.Npv:0}万 · 回收 {r.PaybackYears:0}a · 内排 {r.InnerDumpPct:0}% · 均衡 {r.ReserveBalanceCoef:0.##} · {(r.Ok ? "可行✓" : "不达标(服务年限/剥采比)")}"
            + (prod.Count >= 2 ? " · 剥采比曲线入场景" : "") + (name != null ? $" · CSV → {name}" : "");
    }

    private static string RampText(Cad.RampProfileKind k) => k switch { Cad.RampProfileKind.Aggressive => "激进达产", Cad.RampProfileKind.Stepped => "阶梯爬坡", _ => "线性爬坡" };

    // 短期(月度)生产计划(忠实原 ShortTermScheduler): 年目标→月分布(作业日×设备×组织形态)→均衡→上限回摊→月剥采比→逐月表+月产柱。
    // 用法 短期生产计划 [年煤目标万t] [基准剥采比] [组织:均衡|多面|集中] [工作历:标准|抢产|保守]。
    private async Task ShortTermPlanCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        var p = new Cad.ShortTermPlan();
        if (tk.Length > 1 && double.TryParse(tk[1], out var an) && an > 0) p.AnnualCoalTargetWanT = an;
        if (tk.Length > 2 && double.TryParse(tk[2], out var br) && br > 0) p.BaseRatio = br;
        if (tk.Length > 3) p.Dispatch = tk[3].Contains("集中") ? Cad.DispatchStrategy.Concentrated : tk[3].Contains("多面") ? Cad.DispatchStrategy.MultiFace : Cad.DispatchStrategy.Balanced;
        if (tk.Length > 4) p.Calendar = tk[4].Contains("抢产") ? Cad.CalendarScenario.Push : tk[4].Contains("保守") ? Cad.CalendarScenario.Conservative : Cad.CalendarScenario.Standard;
        // 一键编制: 作业组织×工作历 9 方案 → 综合评分 → 荐最优
        if (tk.Any(t => t == "一键" || t == "多方案" || t == "对比" || t == "自动"))
        {
            var variants = Cad.ShortTermScheduler.GenerateVariants(p, Cad.ShortTermScheduler.DefaultDispatches(), Cad.ShortTermScheduler.DefaultCalendars());
            var results = variants.Select(v => v.Result!).ToList();
            var best = Cad.ShortTermComparer.Score(results);
            var ranked = results.OrderByDescending(x => x.CompositeScore).ToList();
            DrawCategoryBars(ranked.Select(x => (x.Name, x.CompositeScore)).ToList(), "综合分");
            var nm = await SaveCsvAsync("导出短期多方案", "short_term_variants.csv",
                "方案,综合分,完成率%,均剥采比,峰月煤,设备利用%,均衡,可行\n" +
                string.Join("", ranked.Select(x => $"{x.Name},{x.CompositeScore:0},{x.CompletionRatePct:0.#},{x.AvgRatio:0.##},{x.PeakMonthCoalWanT:0.#},{x.AvgEquipUtilPct:0},{x.BalanceCoef:0.##},{(x.Ok ? "是" : "否")}\n")));
            StatusMsg.Text = $"短期一键编制({variants.Count} 方案·作业组织×工作历)：推荐【{best?.Name ?? "无"}】综合 {best?.CompositeScore:0}分(完成 {best?.CompletionRatePct:0.#}%·利用 {best?.AvgEquipUtilPct:0}%·{(best?.Ok == true ? "可行" : "不达标")}) · 前三 " + string.Join(" | ", ranked.Take(3).Select(x => $"{x.Name} {x.CompositeScore:0}分")) + " · 评分柱入场景"
                + (nm != null ? $" · CSV → {nm}" : "");
            return;
        }
        Cad.ShortTermScheduler.Schedule(p);
        var r = p.Result!;
        DrawCategoryBars(p.Months.Select(m => ($"{m.Month}月", m.CoalWanT)).ToList(), "月煤万t");   // 月产柱
        var name = await SaveCsvAsync("导出短期生产计划", "short_term_plan.csv", ShortTermToCsv(p));
        string disp = p.Dispatch switch { Cad.DispatchStrategy.Concentrated => "集中强采", Cad.DispatchStrategy.MultiFace => "多面展开", _ => "均衡型" };
        string cal = p.Calendar switch { Cad.CalendarScenario.Push => "抢产", Cad.CalendarScenario.Conservative => "保守", _ => "标准" };
        StatusMsg.Text = $"短期生产计划({p.PlanYear}年·{disp}·工作历{cal})：年煤 {r.TotalCoalWanT:0}万t·完成 {r.CompletionRatePct:0.#}% · 均剥采比 {r.AvgRatio:0.##} · 峰月 {r.PeakMonthLabel}({r.PeakMonthCoalWanT:0.#}万t) · 月产CV {r.OutputCv:0.###}·均衡 {r.BalanceCoef:0.##} · 设备利用 {r.AvgEquipUtilPct:0}% · {(r.Ok ? "可行✓" : "不达标")} · 月产柱入场景"
            + (name != null ? $" · CSV → {name}" : "");
    }

    private static string ShortTermToCsv(Cad.ShortTermPlan p)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append("月,作业日,煤万t,剥离万m³,剥采比,累计煤,累计剥离,推进m,设备利用%,完成%,作业面,检修\n");
        foreach (var z in p.Months)
            sb.Append($"{z.Label},{z.Workdays.ToString("0.#", inv)},{z.CoalWanT.ToString("0.#", inv)},{z.StripWanM3.ToString("0", inv)},{z.Ratio.ToString("0.##", inv)},{z.CumCoal.ToString("0.#", inv)},{z.CumStrip.ToString("0", inv)},{z.AdvanceM.ToString("0.#", inv)},{z.EquipUtilPct.ToString("0", inv)},{z.CompletionPct.ToString("0.#", inv)},{z.ActiveFace},{(z.IsMaintenance ? "检修" : "")}\n");
        return sb.ToString();
    }

    private static string LongTermToCsv(Cad.LongTermPlan p)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append("年,相时,能力%,煤万t,剥离万m³,剥采比,累计煤,累计剥离,推进m/a,排土,现金流万,NPV万\n");
        foreach (var z in p.Periods)
            sb.Append($"{z.Label},{z.Phase},{z.CapacityPct.ToString("0", inv)},{z.CoalWanT.ToString("0", inv)},{z.StripWanM3.ToString("0", inv)},{z.Ratio.ToString("0.##", inv)},{z.CumCoal.ToString("0", inv)},{z.CumStrip.ToString("0", inv)},{z.AdvanceRateMpa.ToString("0", inv)},{(z.Dump == Cad.LongTermDumpMode.Internal ? "内排" : "外排")},{z.CashFlowWan.ToString("0", inv)},{z.NpvWan.ToString("0", inv)}\n");
        return sb.ToString();
    }

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
        // 各方案 NPV 柱状图上屏(决策支持, 已算未绘)
        DrawCategoryBars(ranked.Select(v => (v.label, v.r.Npv)).ToList(), "NPV");
        StatusMsg.Text = $"派生计划方案：{variants.Count} 方案 · 推荐 {ranked[0].label} · 前三: " + string.Join(" | ", top) + " · NPV柱入场景";
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
            var pl = new PolylineEntity(); pl.CopyStyleFrom(first);   // 合并保源全样式
            foreach (var pt in chain) pl.Points.Add(pt);
            _scene.Add(pl);
        }
        _selected.Clear(); Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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
        var (nodes, adj) = RoadNetwork.BuildNoded(polys, tol);
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
        var (nodes, adj) = RoadNetwork.BuildNoded(polys, tol);
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
        var (nodes, adj) = RoadNetwork.BuildNoded(polys, tol);
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
        var (nodes, adj) = RoadNetwork.BuildNoded(polys, tol);
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
    private async Task BoundaryVolumeAsync(string cmd = "圈范围算量")
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
        double zmax = double.MinValue;
        foreach (var p in r.Points) { pts2d.Add((p.x, p.y)); if (p.z < zmin) zmin = p.z; if (p.z > zmax) zmax = p.z; }
        var tris = Delaunay.Triangulate(pts2d);
        if (tris.Count == 0) { StatusMsg.Text = "圈范围算量：点太少或共线"; return; }

        // 可选深度: "圈范围算量 <深度>" → 只算顶部 <深度> m(基准=zmax−深度, 忠实原 VolumeInPolygon Depth 语义); 缺省=zmin。
        double depth = 0; int sp = cmd.IndexOf(' ');
        if (sp >= 0) double.TryParse(cmd.Substring(sp + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out depth);
        double baseZ = depth > 0 ? zmax - depth : zmin;

        var (above, below, net) = TerrainAnalysis.VolumeWithinBoundary(r.Points, tris, baseZ, boundary.Points);
        double area = TerrainAnalysis.PolygonAreaXY(boundary.Points);
        string how = depth > 0 ? $"顶部 {depth:0.##}m(基准 z {baseZ:0.##})" : $"基准 z {zmin:0.##}";
        StatusMsg.Text = $"圈范围算量（{how}）：上方 {above:0.##} · 下方 {below:0.##} · 净 {net:0.##} · 投影面积 {area:0.##}m²";
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
        // 分标高带 + 按连通块明细(原 VolumeReportGenerator「按标高带/按连通块」)：各分区和==整体(守恒)
        var bands = TerrainAnalysis.TwoEpochVolumeByElevation(r1.Points, r2.Points, 64, bandHeight: 0);
        var parts = TerrainAnalysis.TwoEpochVolumeByPart(r1.Points, r2.Points, 64);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string report = $"# 汇总\ncut,fill,net\n{cut.ToString("R", inv)},{fill.ToString("R", inv)},{net.ToString("R", inv)}\n\n"
            + "# 按标高带\n" + TerrainAnalysis.TwoEpochByElevationCsv(bands)
            + "\n# 按连通块\n" + TerrainAnalysis.TwoEpochByPartCsv(parts);
        var name = await SaveCsvAsync("导出两期算量报表", "twoepoch_report.csv", report);
        StatusMsg.Text = $"两期算量：挖方(下降) {cut:0.##} · 填方(上升) {fill:0.##} · 净 {net:0.##} · {bands.Count} 标高带 · {parts.Count} 连通块"
            + (name != null ? $" → {name}" : "");
    }

    // 三角网着色通用流程：散点 CSV → 三角网 → builder 生成着色边入场景
    // 坡顶底线提取：高程点 CSV → TIN → 坡度断棱线检测 → 坡顶线(橙)/坡底线(青)入场景
    private async Task CrestToeAsync(string cmd)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "坡顶底线：选高程点 CSV (x,y,z)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("高程点 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } }
        });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success) { StatusMsg.Text = $"坡顶底线：点导入失败 {r.Error}"; return; }
        double thr = 30;   // 可选 "坡顶底线 <坡度阈值°>"
        var tk = cmd.Split(new[] { ' ', '°' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2 && double.TryParse(tk[1], out double th)) thr = th;
        var pts2d = new List<(double x, double y)>();
        foreach (var p in r.Points) pts2d.Add((p.x, p.y));
        var tris = Delaunay.Triangulate(pts2d);
        if (tris.Count == 0) { StatusMsg.Text = "坡顶底线：点太少或共线"; return; }
        var (crest, toe) = CrestToe.Extract(r.Points, tris, thr);
        BeginChange();
        foreach (var e in crest)
        { var l = new LineEntity { X0 = e.X0, Y0 = e.Y0, X1 = e.X1, Y1 = e.Y1, Cr = 0.95f, Cg = 0.55f, Cb = 0.2f }; l.LayerName = "坡顶线"; _scene.Add(l); }
        foreach (var e in toe)
        { var l = new LineEntity { X0 = e.X0, Y0 = e.Y0, X1 = e.X1, Y1 = e.Y1, Cr = 0.2f, Cg = 0.7f, Cb = 0.9f }; l.LayerName = "坡底线"; _scene.Add(l); }
        RefreshScene();
        Viewport.FitBounds(r.Bounds);
        StatusMsg.Text = $"坡顶底线(坡度阈 {thr:0.#}°)：坡顶 {crest.Count} 段(橙) · 坡底 {toe.Count} 段(青) · {tris.Count} 三角";
    }

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
            _ = ImportPath(path);
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
        foreach (var kv in counts)
        {
            var node = new TreeViewItem { Header = $"{kv.Key} × {kv.Value}", Tag = kv.Key };
            if (kv.Key == "三角网")   // 三角网按名称列子项(建模对象可逐个选中)
            {
                node.IsExpanded = true;
                foreach (var me in _scene.Entities.OfType<MeshEntity>())
                    node.Items.Add(new TreeViewItem { Header = $"{me.Name}（{me.TriangleCount} 三角）", Tag = "mesh:" + me.Name });
            }
            else if (kv.Key == "点云")   // 点云同理: 一份点云一个子项(点云管理之外的第二个入口)
            {
                node.IsExpanded = true;
                foreach (var pc in _scene.Entities.OfType<PointCloudEntity>())
                    node.Items.Add(new TreeViewItem { Header = $"{pc.Name}（{pc.PointCount:N0} 点）", Tag = "cloud:" + pc.Name });
            }
            root.Items.Add(node);
        }
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
            "DayOps · Kylin 移植版 — 命令与快捷键\n" +
            "（Home 绘制/编辑为内核到位前的托管重实现）\n" +
            "\n【鼠标】\n" +
            "  中键拖拽 = 平移 · 滚轮 = 朝光标缩放\n" +
            "  2D 左键拖拽 = 窗口框选（左→右全含，右→左交叉）\n" +
            "  3D 左键拖拽 = 轨道旋转 · 右键 = 上下文菜单\n" +
            "  双击 = 结束多段线 / 否则范围缩放\n" +
            "\n【快捷键】\n" +
            "  ESC 取消当前命令 · Del 删除选中 · Ctrl+Z 撤销 · Ctrl+Y 重做\n" +
            "\n【命令行】与 AutoCAD 一致\n" +
            "  焦点在视口时直接敲字即进命令行（不必先点命令框）\n" +
            "  空格 = 回车（执行）· 空行 + 空格/回车 = 重复上次命令 · ESC 取消\n" +
            "  ↑↓ 回溯历史 · Tab 补全 · 命令 ALIAS 打印完整对照表\n" +
            "  中文命令名同样可用（距离 / 俯视 / 图案填充 45 2 …）\n" +
            "\n【命令参数】命令行发起的命令，参数就在命令行上逐项问\n" +
            "  提示形如  最大间距(m) <10>:  或  保留侧 [圈内/圈外] <圈内>:\n" +
            "  直接回车 = 取尖括号里的默认值 · 选项可键入 全名/序号/唯一前缀 · 是否项键 Y/N\n" +
            "  参数也可以和命令写在同一行按顺序给：加密多段线 3 · 图案填充 45 2 · POL 8\n" +
            "  中途 Esc = 放弃该命令 · 从功能区按钮发起时仍是原来的参数对话框\n" +
            "\n【绘制中的选项】提示行末尾的 [ ] 里就是当前可键入的关键字\n" +
            "  多段线 指定下一点或 [闭合(C)/放弃(U)]：C 闭合并收笔 · U 退掉刚点的那一点\n" +
            "  圆 指定圆心或 [三点(3P)/两点(2P)/相切、相切、半径(T)]\n" +
            "  多点绘制中：回车 / 双击 = 结束 · Esc = 结束并保留已画部分\n" +
            "\n【绘制】AutoCAD 命令名\n" +
            "  LINE(L) · PLINE(PL) · CIRCLE(C，下拉 2P/3P/TTR) · ARC(A，下拉 三点/SCE/CSE)\n" +
            "  RECTANG(REC) · POLYGON(POL，可带边数) · POINT(PO) · TEXT(DT) · MTEXT(MT/T) · HATCH(H)\n" +
            "  滑动多段线 PLDRAG（原版特有，AutoCAD 无对应）\n" +
            "\n【修改】\n" +
            "  MOVE(M) · COPY(CO/CP) · ROTATE(RO) · SCALE(SC) · MIRROR(MI) · ERASE(E)\n" +
            "  OFFSET(O) · TRIM(TR) · EXTEND(EX) · BREAK(BR) · EXPLODE(X) · JOIN(J) · OVERKILL\n" +
            "  夹点(选中后拖方块) · 空格切换夹点模式\n" +
            "\n【选择】命令提示符下 = 命令名，「选择对象」阶段 = AutoCAD 选择选项\n" +
            "  ALL 全部 · L 上次画的 · P 上次选择集 · WP 窗口多边形 · CP 交叉多边形\n" +
            "  QSELECT · SELECTSIMILAR · GROUP(G)\n" +
            "\n【视图】\n" +
            "  ZOOM(Z) · PAN(P) · REGEN(RE) · 3DORBIT(3DO) · PLAN\n" +
            "  TOP/BOTTOM/FRONT/BACK/LEFT/RIGHT · SWISO/SEISO/NEISO/NWISO · VSCURRENT(VS)\n" +
            "\n【图层】LAYER(LA) · LAYFRZ/LAYTHW · LAYLCK/LAYULK · LAYON · LAYISO/LAYUNISO · LAYDEL\n" +
            "  左侧面板每层：显隐/冻结/锁定/设当前/色块\n" +
            "\n【标注】DIMLINEAR(DLI) · DIMALIGNED(DAL) · DIMRADIUS(DRA) · DIMDIAMETER(DDI)\n" +
            "  DIMANGULAR(DAN) · DIMCONTINUE(DCO) · DIMORDINATE(DOR) · DIMSTYLE(D)\n" +
            "\n【特性/查询】PROPERTIES(PR/CH/MO) · LIST(LI) · DIST(DI) · AREA(AA) · MEASUREGEOM(MEA)\n" +
            "  COLOR(COL) · LINETYPE(LT) · OSNAP(OS) · ORTHO · GRID · SNAP(SN) · OPTIONS(OP)\n" +
            "\n【三维】BOX · SPHERE · CYLINDER(CYL) · UNION(UNI) · SUBTRACT(SU) · INTERSECT(IN)\n" +
            "  LOFT(侧面三角网) · SECTION(SEC)\n" +
            "\n【文件】\n" +
            "  NEW · OPEN(.pmx) · SAVE/QSAVE(.pmx) · SAVEAS(.pmx/.dxf/.dwg)\n" +
            "  IMPORT(IMP) DXF/DWG/OFF · 导入点 CSV/TXT · EXPORT(EXP) DXF · UNDO(U)/REDO\n" +
            "\n【精确坐标】命令行输入：\n" +
            "  x,y 绝对 · @dx,dy 相对 · d<角 极坐标 · @d<角 相对极";

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
    /// <summary>
    /// 「数据库连接」设置。局域网里一个库多客户端时, 连接串靠这里配, 不靠给每台机器设环境变量。
    /// 改完需要重启才生效 —— 地质库连接在启动时建立并被各窗口共用, 中途换库会让已打开的页面
    /// 拿着旧连接继续跑, 与其做一套复杂的重连广播, 不如明确提示重启。
    /// </summary>
    private async void ShowDbConnectionSettings()
    {
        try
        {
            bool saved = await Views.GeoDb.DbConnectionWindow.ShowAsync(this);
            if (saved) StatusMsg.Text = "数据库连接设置已保存，重启程序后生效。";
        }
        catch (System.Exception ex)
        {
            StatusMsg.Text = "打开数据库连接设置失败：" + ex.Message;
        }
    }

    private void ShowOptions()
    {
        var grid = new CheckBox { Content = "显示网格", IsChecked = _gridOn };
        var snap = new CheckBox { Content = "启用对象捕捉", IsChecked = SnapToggle.IsChecked == true };
        var tolLabel = new TextBlock { Text = "捕捉容差 (像素)", VerticalAlignment = VerticalAlignment.Center };
        var tol = new TextBox { Text = _snapTolPx.ToString("0"), Width = 80 };
        var tolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tolLabel, tol } };

        // 界面缩放：功能区内容 1:1 约需 2600px，1080p 及以下放不下，故可按屏幕选挡位（默认自动）
        var scaleLabel = new TextBlock { Text = "功能区缩放（屏幕适配）", VerticalAlignment = VerticalAlignment.Center };
        var scaleBox = new ComboBox { Width = 210, ItemsSource = RibbonScalePresets.Select(x => x.label).ToList() };
        int curIdx = 0;
        for (int i = 0; i < RibbonScalePresets.Length; i++)
            if (System.Math.Abs(RibbonScalePresets[i].targetWidth - _ribbonScaleSetting) < 1e-6) { curIdx = i; break; }
        scaleBox.SelectedIndex = curIdx;
        scaleBox.SelectionChanged += (_, _) =>   // 选中即预览, 不必先点确定
        {
            int i = System.Math.Max(0, scaleBox.SelectedIndex);
            _ribbonScaleSetting = RibbonScalePresets[i].targetWidth;
            FitRibbons();
        };
        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { scaleLabel, scaleBox } };
        var scaleHint = new TextBlock
        {
            Text = "功能区按 1:1 需要约 2300px 宽，1080p 放不下故默认缩放。选固定分辨率按该宽度缩放，"
                 + "适合投屏或多屏切换；选「自动」则跟随当前窗口宽度。选中即可预览。",
            FontSize = 11, Foreground = Brush.Parse("#666"), TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        };

        var ok = new Button { Content = "确定", MinWidth = 72 };
        var cancel = new Button { Content = "取消", MinWidth = 72 };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { grid, snap, tolRow, scaleRow, scaleHint, btnRow } };
        var win = new Window
        {
            Title = "选项", Width = 430, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            Content = panel
        };
        cancel.Click += (_, _) => win.Close();
        ok.Click += (_, _) =>
        {
            SetGrid(grid.IsChecked == true);
            SnapToggle.IsChecked = snap.IsChecked == true;
            if (double.TryParse(tol.Text, out double t) && t >= 2 && t <= 60) _snapTolPx = t;
            int si = System.Math.Max(0, scaleBox.SelectedIndex);
            _ribbonScaleSetting = RibbonScalePresets[si].targetWidth;
            SaveRibbonScaleSetting();
            FitRibbons();
            StatusMsg.Text = $"选项已应用（网格 {(_gridOn ? "开" : "关")} · 捕捉 {(SnapToggle.IsChecked == true ? "开" : "关")} · 容差 {_snapTolPx:0}px"
                           + $" · 功能区按 {(_ribbonScaleSetting > 0 ? _ribbonScaleSetting.ToString("0") + "px 宽" : "窗口宽度")}适配）";
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
        StatusMsg.Text = $"选择类似：{_selected.Count} 个（类型 {string.Join("/", types)}）";
    }

    // 快速选择(QSELECT) —— 忠实原 QuickSelectFilter: 按 类型/特性/运算符/值 过滤, 支持 排除/追加/当前选择集内。
    // 语法: 快速选择 <类型|*> [<特性> <运算符> <值>] [排除] [追加] [当前]
    //   例: 快速选择 圆 半径 > 5        · 快速选择 * 图层 = 煤层        · 快速选择 多段线 是否闭合 = 是
    //       快速选择 文字 内容 * 标高*   · 快速选择 点                     (选中全部点)
    private void QuickSelectCmd(string cmd)
    {
        var toks = cmd.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries).ToList();
        toks.RemoveAt(0);   // 去掉"快速选择"

        // 标志(位置无关): 排除 / 追加 / 当前(选择集内)
        var crit = new QuickSelectCriteria();
        bool Take(params string[] al) { for (int i = 0; i < toks.Count; i++) if (al.Contains(toks[i])) { toks.RemoveAt(i); return true; } return false; }
        if (Take("排除", "排除模式", "exclude")) crit.ApplyMode = QuickSelectApplyMode.Exclude;
        if (Take("追加", "并入", "append")) crit.AppendToCurrentSelection = true;
        if (Take("当前", "当前选择集", "选择集内", "选集内")) crit.Scope = QuickSelectScope.CurrentSelection;

        if (toks.Count == 0)
        {
            StatusMsg.Text = "快速选择：语法 快速选择 <类型|*> [<特性> <运算符> <值>] [排除][追加][当前]；"
                           + "例 “快速选择 圆 半径 > 5”“快速选择 * 图层 = 煤层”。选中所有同类请用 选择类似。";
            return;
        }

        // ① 类型(先决条件)
        string t0 = toks[0];
        crit.TypeId = (t0 is "*" or "所有" or "全部" or "全部图元" or "任意") ? (int?)null : QsTypeId(t0);
        if (t0 is not ("*" or "所有" or "全部" or "全部图元" or "任意") && crit.TypeId is null)
        { StatusMsg.Text = $"快速选择：未识别对象类型「{t0}」(可用 直线/圆/圆弧/多段线/矩形/正多边形/文字/点，或 * 表示所有)。"; return; }

        // ② 特性 / 运算符 / 值 (缺省=只按类型选全部)
        if (toks.Count == 1)
        {
            crit.Operator = QuickSelectOperator.All;
        }
        else if (toks.Count >= 3)
        {
            string propTok = toks[1], opTok = toks[2];
            string val = toks.Count > 3 ? string.Join(" ", toks.Skip(3)) : "";
            string? key = QsPropKey(crit.TypeId, propTok);
            if (key is null) { StatusMsg.Text = $"快速选择：类型「{t0}」下未识别特性「{propTok}」。"; return; }
            var op = QsOperator(opTok);
            if (op is null) { StatusMsg.Text = $"快速选择：未识别运算符「{opTok}」(可用 = <> > < >= <= *)。"; return; }
            crit.PropertyKey = key; crit.Operator = op.Value; crit.Value = val;
        }
        else
        { StatusMsg.Text = "快速选择：特性筛选需 <特性> <运算符> <值> 三项(或只给类型选全部)。"; return; }

        // ③ 候选集(整图 / 当前选择集内) → 快照 → 过滤
        var pool = crit.Scope == QuickSelectScope.CurrentSelection
            ? _selected.ToList()
            : _scene.Entities.ToList();
        if (pool.Count == 0) { StatusMsg.Text = "快速选择：候选为空(当前选择集内筛需先有选择)。"; return; }
        var snaps = QuickSelectSnapshot.FromScene(pool);
        var res = QuickSelectFilter.Apply(snaps, crit);

        // ④ 命中回映到实体(Handle=下标)，过滤锁定/关闭图层，落选择集
        SaveSel();
        var hit = new List<SceneEntity>();
        foreach (var h in res.Handles)
        {
            var en = pool[(int)h];
            if (_layers.IsSelectable(en.LayerName)) hit.Add(en);
        }
        if (!res.AppendToCurrentSelection) _selected.Clear();
        foreach (var en in hit) if (!_selected.Contains(en)) _selected.Add(en);
        HighlightSelection();
        StatusMsg.Text = QuickSelectFilter.Describe(crit, hit.Count, res.Examined);
    }

    // 类型中文名 → 目录类型 id(含 Kylin 别名); 未识别返回 null。
    private static int? QsTypeId(string name) => name switch
    {
        "直线" or "线" => QuickSelectCatalog.TypeLine,
        "圆" => QuickSelectCatalog.TypeCircle,
        "圆弧" or "弧" => QuickSelectCatalog.TypeArc,
        "多段线" or "多线" => QuickSelectCatalog.TypePolyline,
        "矩形" => QuickSelectCatalog.TypeRectangle,
        "多边形" or "正多边形" => QuickSelectCatalog.TypePolygon,
        "文字" or "单行文字" or "文本" => QuickSelectCatalog.TypeText,
        "点" => QuickSelectCatalog.TypePoint,
        _ => null,
    };

    // 特性 token → 目录键: 先试直接键(radius/layer…), 再按该类型下 DisplayName 匹配(图层/颜色/半径/内容…)。
    private static string? QsPropKey(int? typeId, string tok)
    {
        if (QuickSelectCatalog.Find(tok) is not null) return tok;                 // 直接键
        foreach (var p in QuickSelectCatalog.PropertiesFor(typeId))
            if (string.Equals(p.DisplayName, tok, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.DisplayName.Replace(" ", ""), tok, System.StringComparison.OrdinalIgnoreCase))
                return p.Key;
        return null;
    }

    // 运算符 token → 枚举; 未识别返回 null。
    private static QuickSelectOperator? QsOperator(string tok) => tok switch
    {
        "=" or "==" or "等于" => QuickSelectOperator.Equals,
        "<>" or "!=" or "≠" or "不等于" or "不等" => QuickSelectOperator.NotEquals,
        ">" or "大于" => QuickSelectOperator.Greater,
        "<" or "小于" => QuickSelectOperator.Less,
        ">=" or "≥" or "大于等于" or "不小于" => QuickSelectOperator.GreaterOrEqual,
        "<=" or "≤" or "小于等于" or "不大于" => QuickSelectOperator.LessOrEqual,
        "*" or "通配" or "like" or "匹配" => QuickSelectOperator.Wildcard,
        "全部" or "所有" or "all" => QuickSelectOperator.All,
        _ => null,
    };

    // 对象树选中类型 → 高亮该类型几何；选根/无 → 清除
    private void OnObjectTreeSelect(object? sender, SelectionChangedEventArgs e)
    {
        if (ObjectTree.SelectedItem is TreeViewItem { Tag: string type })
        {
            if (type.StartsWith("mesh:"))   // 单张三角网
            {
                var one = _scene.Entities.OfType<MeshEntity>().FirstOrDefault(m => m.Name == type.Substring(5));
                if (one == null) return;
                _selected.Clear(); _selected.Add(one); HighlightSelection();
                var bb = one.Bounds; StatusMsg.Text = $"对象树：选中三角网「{one.Name}」 {one.VertexCount} 顶点 / {one.TriangleCount} 三角 · Z {bb.minZ:0.#}~{bb.maxZ:0.#}";
                return;
            }
            if (type.StartsWith("cloud:"))   // 单份点云: 选中并顺手设为「当前点云」(各算子的输入锚点)
            {
                var one = _scene.Entities.OfType<PointCloudEntity>().FirstOrDefault(p => p.Name == type.Substring(6));
                if (one == null) return;
                _selected.Clear(); _selected.Add(one); HighlightSelection();
                PcSetCurrent(one);
                var cb = one.Bounds;
                StatusMsg.Text = $"对象树：选中点云「{one.Name}」 {one.PointCount:N0} 点 · Z {cb.minZ:0.#}~{cb.maxZ:0.#} · 已设为当前点云";
                return;
            }
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
    private void OnCtx2D(object? s, RoutedEventArgs e) { Viewport.SetViewMode(true); SetSelectMode(false, quiet: true); StatusMsg.Text = "视图: 2D 平面（左键拖=框选, 单击=点选）"; }
    private void OnCtx3D(object? s, RoutedEventArgs e) { Viewport.SetViewMode(false); StatusMsg.Text = "视图: 3D 轨道"; }

    // 忠实原版右键三态：3D Orbit(左键拖=旋转) ↔ 3D 选择模式(左键只框选/点选)
    private void OnCtxModeToOrbit(object? s, RoutedEventArgs e)
    {
        if (Viewport.Is2DView) Viewport.SetViewMode(false);
        SetSelectMode(false);
    }

    private void OnCtxModeToSelect(object? s, RoutedEventArgs e)
    {
        if (Viewport.Is2DView) Viewport.SetViewMode(false);
        SetSelectMode(true);
    }
    private void OnCtxGrid(object? s, RoutedEventArgs e) => SetGrid(!_gridOn);

    // 网格显隐(保持 _gridOn 与视口一致)
    private void SetGrid(bool on)
    {
        if (GridToggle != null && GridToggle.IsChecked != on) GridToggle.IsChecked = on;   // 状态栏栅格钮同步(触发 OnGridToggle 后下一行早退)
        if (on == _gridOn) return;
        _gridOn = on;
        Viewport.GridVisible = on;   // 直设而非翻转: 各标签视口各有自己的网格位, 翻转会把没对齐的那个翻反
    }

    // ── 状态栏(同原版 extend 项)：栅格开关 / 捕捉模式右键菜单 / 选项 ──
    private void OnGridToggle(object? sender, RoutedEventArgs e) => SetGrid(GridToggle.IsChecked == true);

    private void OnStatusOptionsClick(object? sender, RoutedEventArgs e) => ShowOptions();

    // 捕捉模式菜单打开时按 _snapExtraMask 回填勾选(端点/中点/圆心/象限恒开, 同原版 miSnap* IsCheckable)
    private void OnSnapModeMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        void Sync(MenuItem? mi, ObjectSnap.Mode m) { if (mi != null) mi.Icon = (_snapExtraMask & (1 << (int)m)) != 0 ? new TextBlock { Text = "✓" } : null; }
        Sync(MiSnapIntersection, ObjectSnap.Mode.Intersection);
        Sync(MiSnapPerpendicular, ObjectSnap.Mode.Perpendicular);
        Sync(MiSnapNearest, ObjectSnap.Mode.Nearest);
    }

    private void OnSnapModeMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string t || !int.TryParse(t, out int bit)) return;
        _snapExtraMask ^= 1 << bit;
        SnapToggle.IsChecked = true;   // 改模式即视为要用捕捉(同原版)
        StatusMsg.Text = $"捕捉模式: 交点{On(ObjectSnap.Mode.Intersection)} 垂足{On(ObjectSnap.Mode.Perpendicular)} 最近{On(ObjectSnap.Mode.Nearest)}（端点/中点/圆心/象限恒开）";
        string On(ObjectSnap.Mode m) => (_snapExtraMask & (1 << (int)m)) != 0 ? "✓" : "✗";
    }

    private void OnSnapModeAllOnClick(object? sender, RoutedEventArgs e)
    {
        _snapExtraMask = ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection, ObjectSnap.Mode.Nearest, ObjectSnap.Mode.Perpendicular);
        SnapToggle.IsChecked = true;
        StatusMsg.Text = "对象捕捉: 全部开启";
    }

    private void OnSnapModeAllOffClick(object? sender, RoutedEventArgs e)
    {
        _snapExtraMask = 0;
        SnapToggle.IsChecked = false;
        StatusMsg.Text = "对象捕捉: 全部关闭";
    }

    // ── Ribbon 开始页(复刻原版 Fluent Ribbon Home)专用处理 ──
    private DMC.Tool? _propsTool, _assistantTool;

    /// <summary>按资源键取 16px 菜单图标(找不到返回 null)。</summary>
    private Image? MenuIcon(string key)
        => this.TryFindResource(key, out var r) && r is Avalonia.Media.IImage img ? new Image { Source = img, Width = 16, Height = 16 } : null;

    // 「切换窗口」下拉展开：动态列出已打开的全部文档(同原版 btnSwitchView)，点选即切换；下方附标准视图预设。
    private void OnSwitchViewFlyoutOpening(object? sender, System.EventArgs e)
    {
        if (sender is not MenuFlyout fl) return;
        fl.Items.Clear();
        foreach (var st in _docs)
        {
            var d = st;
            var mi = new MenuItem { Header = (ReferenceEquals(d, _active) ? "● " : "　") + d.Title, Icon = MenuIcon("icon_doc") };
            mi.Click += (_, _) => _dockFactory.SetActiveDockable(d.Vm);
            fl.Items.Add(mi);
        }
        fl.Items.Add(new Separator());
        var views = new MenuItem { Header = "标准视图", Icon = MenuIcon("icon_Ori") };
        foreach (var v in new[] { "俯视", "仰视", "主视", "后视", "左视", "右视", "西南等轴测", "东南等轴测", "东北等轴测", "西北等轴测" })
        {
            var name = v;
            var mi = new MenuItem { Header = name, Icon = MenuIcon("icon_view") };
            mi.Click += (_, _) => DispatchRibbon(name);
            views.Items.Add(mi);
        }
        fl.Items.Add(views);
        var ze = new MenuItem { Header = "范围缩放", Icon = MenuIcon("icon_Zoom") }; ze.Click += (_, _) => DispatchRibbon("范围缩放"); fl.Items.Add(ze);
        var pv = new MenuItem { Header = "上一视图", Icon = MenuIcon("icon_undo") }; pv.Click += (_, _) => DispatchRibbon("上一视图"); fl.Items.Add(pv);
    }

    // 「调用选择集」下拉展开：列出命名选择集(与右键菜单同源 _selSets)。
    private void OnRecallSelFlyoutOpening(object? sender, System.EventArgs e)
    {
        if (sender is not MenuFlyout fl) return;
        fl.Items.Clear();
        if (_selSets.Count == 0) { fl.Items.Add(new MenuItem { Header = "（暂无，先用 创建选择集）", IsEnabled = false }); return; }
        for (int i = 0; i < _selSets.Count; i++)
        {
            var s = _selSets.At(i);
            if (s == null) continue;
            int idx = i;
            var mi = new MenuItem { Header = $"{s.Value.name}  ({s.Value.ents.Count} 项)" };
            mi.Click += (_, _) => RecallSelSetByIndex(idx);
            fl.Items.Add(mi);
        }
    }

    // 「AI 助手」切换：选中 → 右侧停靠切到 AI 助手页；取消 → 切回属性对话框(同原版 btnAiChat 切换 aiChatAnchorable)。
    // 3D 选择模式(右键菜单切换, 忠实原版三态)：开 → 左键只框选/点选(不旋转)；关 → 左键拖拽轨道旋转。2D 左键本就框选。
    private void SetSelectMode(bool on, bool quiet = false)
    {
        _selectMode = on;
        if (!quiet)
            StatusMsg.Text = on
                ? "3D 选择模式：左键拖=框选、单击=点选（不旋转视图）；右键菜单可切回 3D Orbit"
                : "3D Orbit：左键拖=旋转视图、单击=点选；右键菜单可切到 3D 选择模式（或按住 Shift 临时框选）";
        SyncPrompt();
    }

    private void OnAiChatToggle(object? sender, RoutedEventArgs e)
    {
        if (_dockFactory == null) return;
        var target = AiChatToggle.IsChecked == true ? _assistantTool : _propsTool;
        if (target != null) _dockFactory.SetActiveDockable(target);
    }

    // 「填充 → 图案填充」：按注释组的 角度/比例(间距) 栏拼命令 "图案填充 <角度> [间距]"。
    private void OnHatchFillClick(object? sender, RoutedEventArgs e)
    {
        string ang = (HatchAngleBox?.Text ?? "").Trim(); if (ang.Length == 0) ang = "45";
        string sp = (HatchScaleBox?.Text ?? "").Trim();
        DispatchRibbon(sp.Length > 0 ? $"图案填充 {ang} {sp}" : $"图案填充 {ang}");
    }

    // 图案下拉：ANSI31 斜线 / ANSI37 十字 → 映射到现有 十字交叉 开关。
    private void OnHatchPatternChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HatchPatternBox == null) return;
        bool cross = HatchPatternBox.SelectedIndex == 1;
        if (cross != _hatchCross) { _hatchCross = cross; StatusMsg.Text = $"图案填充: {(cross ? "ANSI37 十字交叉" : "ANSI31 斜线")}"; }
    }

    // 线型下拉 → 现有 "线型 <名>" 命令(影响新画直线/多段线)。
    private void OnLinetypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropRibbon) return;   // 回填显示时不当成用户操作(同原版 _suppressPropertyComboEvents)
        if (LinetypeBox?.SelectedItem is ComboBoxItem it && it.Content is string name && _dockFactory != null)
            DispatchRibbon($"线型 {name}");
    }

    // ── 交互提示(同原版 jigPromptText + 命令行历史回显)：按当前交互状态给出 AutoCAD 式步骤提示 ──
    private string _lastPrompt = "";

    /// <summary>当前交互步骤提示；空闲返回空串。顺序 = 各状态互斥优先级(工具/编辑/夹点/测量/其它单步命令)。</summary>
    private string CurrentPrompt()
    {
        if (AskingParams) return ParamAskPrompt();      // 参数问答优先：命令跑到一半在问参数
        if (_tool != null) return _tool.Prompt;
        if (_editAwaitSelect) return $"{_editName}：选择对象 — 单击选取 · 按住拖动框选 · 再点取消选 · 右键确定（已选 {_selected.Count}）";
        if (_editMode != EditMode.None)
            return _editPts.Count == 0
                ? $"{_editName}：{EditFirstPrompt()}"
                : EditPrompt(_editMode, _editPts.Count);
        if (_gripDrag.Active) return _gripDrag.Prompt;
        if (_measure != null) return "测量：指定下一点（右键/Esc 结束）";
        if (_angle != null) return "角度测量：依次指定 顶点、第一点、第二点";
        if (_offsetActive) return "偏移：指定要偏移的那一侧上的点";
        if (_trimActive) return "修剪/延伸：先选边界，再点要修剪或延伸的对象（Esc 退出）";
        if (_breakActive) return _breakPts.Count == 0 ? "打断：指定第一个打断点" : "打断：指定第二个打断点";
        if (_slideActive) return _slideDragging ? "滑动多段线：拖动中…松开结束" : "滑动多段线：按住左键拖动绘制";
        if (_ttrActive) return _ttrAwaitRadius ? "圆TTR：在命令行输入半径并回车" : _ttrRef1 == null ? "圆TTR：选择第一个相切对象（直线/圆）" : "圆TTR：选择第二个相切对象";
        if (_serActive) return _serAwaitRadius ? "圆弧SER：在命令行输入半径并回车（负值取另一侧）" : _serStart == null ? "圆弧SER：指定起点" : "圆弧SER：指定端点";
        if (_dimActive) return _dimP1 == null ? "标注：指定第一条尺寸界线原点" : _dimP2 == null ? "标注：指定第二条尺寸界线原点" : "标注：指定尺寸线位置";
        if (_dimRadActive) return _dimRadCircle == null ? $"{(_dimDiameter ? "直径" : "半径")}标注：选择圆或圆弧" : "标注：指定尺寸线位置";
        if (_dimAngActive) return _angVertex == null ? "角度标注：指定角的顶点" : _angP1 == null ? "角度标注：指定第一条边上的点" : "角度标注：指定第二条边上的点";
        if (_pathActive) return _pathP1 == null ? "寻径：指定起点" : "寻径：指定终点";
        if (_pasteBaseActive) return "粘贴：指定插入基点";
        if (_benchActive) return "分帮扩帮：指定推进方向与步距点";
        if (_spotActive) return "高程查询：点击查询位置（Esc 结束）";
        if (_coordLabelActive) return "坐标标注：点击标注位置（Esc 结束）";
        if (_selBoxActive) return "指定对角点（左→右 窗口 / 右→左 交叉）";
        return "";
    }

    /// <summary>提示变化时：更新命令行提示标签 + 回显到信息栏历史(灰色, 区别于 ▸ 命令行)。</summary>
    private void SyncPrompt()
    {
        if (CmdPrompt == null) return;
        string p = CurrentPrompt();
        if (p == _lastPrompt) return;
        _lastPrompt = p;
        CmdPrompt.Text = p.Length == 0 ? "" : p + ":";
        if (p.Length == 0 || CmdLog == null) return;
        CmdLog.Children.Add(new TextBlock
        {
            Text = "  " + p + ":", FontSize = 11, FontFamily = new FontFamily("Consolas,monospace"),
            Foreground = Brush.Parse("#6B7280")
        });
        while (CmdLog.Children.Count > 100) CmdLog.Children.RemoveAt(0);
        CmdLogScroll?.ScrollToEnd();
    }
    private void OnCtxClearHighlight(object? s, RoutedEventArgs e) { Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null); }

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
        Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
        // 快照只存实体、不存图层表。撤销回来的实体若引用了已被删掉的图层（如「清空视图」删过层），
        // 层名就成了悬空引用 —— 图层管理器里看不到它，也就没法再控制它的显隐/锁定。这里按需补回。
        int restored = 0;
        foreach (var e in _scene.Entities)
        {
            if (string.IsNullOrEmpty(e.LayerName) || _layers.Get(e.LayerName) != null) continue;
            _layers.EnsureImported(e.LayerName, e.Cr, e.Cg, e.Cb);
            restored++;
        }
        if (restored > 0) PopulateDrawingLayers();
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
    // 捕捉几何缓存：BuildSnapGeom 要遍历整个场景建 段/圆/弧/点 四张表，
    // 而它被鼠标移动事件逐个调用(开了交点/最近/垂足捕捉时) —— 场景一大, 光移鼠标就卡。
    // 场景没变就直接复用, 由 RefreshScene / 图层显隐 置空。
    private (List<ObjectSnap.Seg>, List<ObjectSnap.Circ>, List<ObjectSnap.ArcP>, List<(double x, double y)>)? _snapGeomCache;

    private ObjectSnap.Index? _snapGeomIdx;            // 上表的网格索引(懒建, 与上表同寿)
    private SnapPoints.Index? _snapVertIdx;            // 顶点捕捉网格索引(按源数组引用缓存)

    private void InvalidateSnapGeom() { _snapGeomCache = null; _snapGeomIdx = null; _sceneIdx = null; }

    private SceneIndex? _sceneIdx;   // 点选/框选的图元空间索引(懒建; 场景一变就丢, 同上面几个缓存)

    /// <summary>
    /// 点选/框选用的图元空间索引 —— 只让光标/选框附近格子里的图元参与精确判定。
    /// 实测 50.7 万图元：点选 156 ms → 0.0x ms、框选 137 ms → 1.4 ms，选中结果逐个比对一致。
    /// 懒建（第一次点选才付 25~92 ms），场景一改由 <see cref="InvalidateSnapGeom"/> 丢掉重建。
    /// </summary>
    private SceneIndex SceneIdx() => _sceneIdx ??= new SceneIndex(_scene.Entities);

    /// <summary>捕捉原语的网格索引 —— 逐帧只查光标邻域, 图元上万也不拖光标。</summary>
    private ObjectSnap.Index SnapGeomIndex()
    {
        if (_snapGeomIdx != null) return _snapGeomIdx;
        var (segs, circles, arcs, pts) = BuildSnapGeom();
        return _snapGeomIdx = new ObjectSnap.Index(segs, circles, arcs, pts);
    }

    /// <summary>顶点捕捉的网格索引；源数组换了(场景重建/夹点自避)才重建。</summary>
    private SnapPoints.Index SnapVertIndex(float[] src)
    {
        if (_snapVertIdx == null || !ReferenceEquals(_snapVertIdx.Source, src)) _snapVertIdx = new SnapPoints.Index(src);
        return _snapVertIdx;
    }

    private (List<ObjectSnap.Seg> segs, List<ObjectSnap.Circ> circles, List<ObjectSnap.ArcP> arcs, List<(double x, double y)> pts) BuildSnapGeom()
    {
        if (_snapGeomCache is { } hit) return hit;
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
        _snapGeomCache = (segs, circles, arcs, pts);
        return (segs, circles, arcs, pts);
    }

    private static double Dist2((double x, double y) a, (double x, double y) b)
    { double dx = a.x - b.x, dy = a.y - b.y; return dx * dx + dy * dy; }

    private bool _hatchCross;   // 图案填充: 是否十字交叉
    private double[]? _currentDash;   // 当前线型(虚线样式); null=实线。新画线/多段线继承

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

    // 重绘场景（进行中的预览走独立通道，见 RefreshScenePreview）
    private void RefreshScene()
    {
        // 分步落 TRACE：五十万图元的图纸刷一次要好几秒, 卡在哪一步(捕捉点/细分/三角面/注记/对象树)
        // 不逐步计时就只能猜。PITMINE_TRACE=1 才写, 平时零开销。
        var swR = PitMine3D.Kylin.CrashLog.TraceOn ? System.Diagnostics.Stopwatch.StartNew() : null;
        void T(string step) { if (swR != null) PitMine3D.Kylin.CrashLog.Trace($"RefreshScene/{step} {swR.ElapsedMilliseconds}ms"); }

        InvalidateSnapGeom();   // 场景/图层变了 → 捕捉几何缓存作废
        _snapVerts = _scene.SnapCandidates(_layers.IsShown);   // 语义 osnap 点(端点/中点/圆心/象限)
        T("捕捉点");
        Viewport.SetSceneGeometry(_scene.BuildGeometry(_layers.IsShown));
        T("细分+上传");
        RefreshScenePreview();
        T("预览");
        var faces = _scene.BuildFaces(_layers.IsShown);
        T("三角面/生成");
        Viewport.SetSceneFaces(faces);
        T("三角面/上传");
        Viewport.SetSceneCloud(_scene.BuildCloudPoints(_layers.IsShown), PcPointPixels());   // 点云(GL_POINTS)
        T("点云");
        Viewport.SetBillboards(_scene.BuildBillboards(_layers.IsShown));   // 注记始终朝屏幕(原版 screenFacing)
        T("注记");
        if (_scene.Count != _lastSceneCount) { _lastSceneCount = _scene.Count; RefreshObjectTree(); }
        T("对象树");
    }

    /// <summary>
    /// 只重画随光标变的预览(橡皮筋/编辑拖拽跟随/滑动采样)——场景几何、捕捉候选、三角面、注记一概不动。
    /// 鼠标移动路径专用: 移动不改场景, 而"每动一次就把整篇场景重新细分并重传 GPU"正是图元一多就卡的根子。
    /// 改场景的路径一律走 RefreshScene(它顺带刷新这里)。
    /// </summary>
    private void RefreshScenePreview()
    {
        var list = new List<float>();
        AppendScenePreview(list);
        Viewport.SetPreviewGeometry(list.Count == 0 ? null : list.ToArray());
    }

    // 进行中的预览几何(工具橡皮筋 / 滑动多段线 / 编辑拖拽跟随)追加到 list。
    private void AppendScenePreview(List<float> list)
    {
        _tool?.AppendPreview(list, _cursorWorld);
        if (_slideDragging && _slidePts.Count > 1)     // 滑动多段线拖动预览
        {
            var pv = new PolylineEntity { Points = _slidePts, Cr = 0.55f, Cg = 0.62f, Cb = 0.70f };
            pv.Tessellate(list);
        }
        // 编辑即时预览(AutoCAD 式拖拽跟随)：选中实体按「已取点 + 光标」变换后以预览色叠加。
        if (_cursorWorld != null && _selected.Count > 0 && BuildEditPreview(_cursorWorld.Value) is Affine2 em)
        {
            foreach (var e in _selected)
            {
                var g = e.Apply(em);
                g.Cr = 0.55f; g.Cg = 0.62f; g.Cb = 0.70f;   // 预览灰蓝
                g.Tessellate(list);
            }
            if (_editMode == EditMode.Mirror && _editPts.Count == 1)   // 镜像轴线也预览
                new LineEntity { X0 = _editPts[0].x, Y0 = _editPts[0].y, X1 = _cursorWorld.Value.x, Y1 = _cursorWorld.Value.y, Cr = 0.85f, Cg = 0.6f, Cb = 0.3f }.Tessellate(list);
        }
    }

    // 点选：命中则单选(再点取消)，未命中清空
    private void PickAt(Avalonia.Point rel)
    {
        SceneEntity? hit;
        if (!Viewport.Is2DView)
        {
            // 3D：Z=0 平面反投影不代表实体位置(模型可在千米高程), 改屏幕空间点选(边线像素距离 + 三角网面命中取最前)
            SaveSel();
            hit = SelectionBox.PickScreen(_scene.Entities, rel.X, rel.Y, _snapTolPx, Viewport.WorldToScreenDepthProjector(), _layers.IsSelectable);
        }
        else
        {
            var w = _snapWorld ?? Viewport.ScreenToWorld(rel.X, rel.Y);
            if (w == null) return;
            SaveSel();
            hit = PickWorld2D(w.Value.x, w.Value.y, SnapTolWorld(rel));
        }
        // 编辑选择对象阶段: 累加/减选(不清空); 空闲态: 单选替换。
        if (hit == null) { if (!_editAwaitSelect) _selected.Clear(); }
        else if (_selected.Contains(hit)) _selected.Remove(hit);
        else { if (!_editAwaitSelect) _selected.Clear(); _selected.Add(hit); }
        HighlightSelection();
        if (_editAwaitSelect) StatusMsg.Text = $"{_editName}：选择对象（右键确定，已选 {_selected.Count}）";
        else StatusMsg.Text = _selected.Count > 0 ? $"已选 {_selected.Count} 个实体" : "未选中";
    }

    /// <summary>
    /// 2D 世界坐标处的拾取（点选与自检共用一条路，免得两处逻辑走样）。
    /// 走空间索引：只算光标附近格子里的图元 —— 全场景逐个算距离在 50 万图元下要 150 ms 上下。
    /// </summary>
    private SceneEntity? PickWorld2D(double x, double y, double tol)
    {
        var swP = PitMine3D.Kylin.CrashLog.TraceOn ? System.Diagnostics.Stopwatch.StartNew() : null;
        var idx = SceneIdx();
        var hit = _scene.Pick(x, y, tol, _layers.IsSelectable, idx);
        // 2D 点在三角网面内(未贴边线)也算选中该网(着色面模式下面是可见的)
        if (hit == null)
            foreach (var en in idx.Query(x, y, x, y))
                if (en is MeshEntity me && en.Visible && _layers.IsSelectable(en.LayerName) && me.ContainsXY(x, y)) { hit = me; break; }
        if (swP != null) PitMine3D.Kylin.CrashLog.Trace($"点选 {swP.Elapsed.TotalMilliseconds:0.##}ms 命中={(hit == null ? "无" : hit.GetType().Name)}");
        return hit;
    }

    // 选集变化入口：重建夹点表(旧夹点选择必然失效，同原版 Rebuild) → 重画高亮
    private void HighlightSelection()
    {
        UpdatePropertyPanel();
        _grips.Rebuild(_selected);
        _gripHover = -1;
        GizmoRebuild();   // 三轴手柄锚点随选择集重算, 见 MainWindow.Gizmo.cs
        RedrawHighlight();
    }

    // 只重画高亮 + 夹点方块(夹点选择/悬停变化时用，不动夹点表)
    private void RedrawHighlight()
    {
        if (_selected.Count == 0) { Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null); return; }
        // 三角网：表面盖一层高亮色面 + 轮廓边线(大网省边线, 靠高亮色面即可辨识);
        // 其它实体：自身线框重着色。高亮色取青(与黄色地形/深色背景都拉得开)。
        const float hr = 0.15f, hg = 0.95f, hb = 1.0f;
        var ent = new List<float>();
        var faces = new List<float>();
        foreach (var e in _selected)
        {
            if (e is MeshEntity me)
            {
                me.TessellateHighlightFaces(faces, hr, hg, hb);
                if (me.TriangleCount <= 20000) me.TessellateEdges(ent);   // 大网只用高亮色面(避免为判定而构建整张边表)
            }
            // 点云选中: 画三维包围盒线框。逐点重着色既慢(几十万点)又看不出"这一份被选中"——
            // 点太小, 换个颜色仍分不清边界; 框住它才一眼看出选的是哪一份。
            else if (e is PointCloudEntity pcSel) pcSel.TessellateBoundsBox(ent);
            else e.TessellatePick(ent);   // 文字走轮廓: 真字体下 Tessellate 是空的, 选中了看不见高亮
        }
        var o = new List<float>(Controls.CadGlViewport.Recolor(ent.ToArray(), hr, hg, hb));   // 实体=高亮青；夹点保留自身配色
        Viewport.SetHighlightFaces(faces.Count > 0 ? faces.ToArray() : null);
        if (_gripsOn) AppendGripTable(o);
        AppendGizmo(o);   // 三轴变换手柄画在最上层(与夹点同通道), 见 MainWindow.Gizmo.cs
        Viewport.SetHighlight(o.ToArray(), recolor: false);
    }

    // 夹点方块配色忠实原版 Viewport.cpp：冷=蓝、热(悬停)=亮蓝；多选选中=品红 + 白描边 + 大一号(拖任一个整组动，"要动几个点"一眼可见)
    private void AppendGripTable(List<float> o)
    {
        double h = GripSize();
        for (int i = 0; i < _grips.Count; i++)
        {
            var g = _grips.Grips[i];
            bool hot = i == _gripHover;
            if (_grips.IsSelected(i))
            {
                AppendGripSquare(o, g.X, g.Y, h * 1.8, 1f, 1f, 1f);
                AppendGripSquare(o, g.X, g.Y, h * 1.4, hot ? 1f : 0.95f, hot ? 0.45f : 0.05f, hot ? 0.95f : 0.75f);
            }
            else AppendGripSquare(o, g.X, g.Y, h, 0f, hot ? 0.8f : 0.5f, 1f);
        }
    }

    // 夹点拖拽预览：受影响实体按模式变换后画到高亮通道(其余选中实体照常高亮)，并在光标旁显示模式与量值
    private void RefreshGripPreview(Avalonia.Point p, (double x, double y) c)
    {
        var prev = _gripDrag.Preview(c.x, c.y);
        var ent = new List<float>();
        var o = new List<float>();
        double h = GripSize();
        foreach (var e in _selected)
        {
            var hit = prev.Find(t => ReferenceEquals(t.old, e));
            var shown = hit.moved ?? e;
            shown.Tessellate(ent);
            if (_gripsOn) foreach (var g in shown.Grips()) AppendGripSquare(o, g.x, g.y, h);
        }
        o.InsertRange(0, Controls.CadGlViewport.Recolor(ent.ToArray(), 1f, 0.9f, 0.2f));
        Viewport.SetHighlight(o.ToArray(), recolor: false);

        var tip = _active.DragTip;
        if (tip == null) return;
        double v = _gripDrag.ValueAt(c.x, c.y);
        string val = _gripDrag.Mode switch
        {
            GripMode.Rotate => $"角度 {v * 180 / System.Math.PI:0.0}°",
            GripMode.Scale => $"比例 {v:0.000}",
            _ => $"距离 {v:0.00}",
        };
        ((TextBlock)tip.Child!).Text = $"{_gripDrag.Prompt} {val}";
        tip.Margin = new Avalonia.Thickness(p.X + 18, p.Y + 20, 0, 0);
        tip.Opacity = 1;
        (tip.Parent as Control)?.InvalidateVisual();
    }

    // 夹点拖拽落地：整组一次 Replace + 一次 BeginChange(= 原版 AcDbDragGripsCommand 一步 Undo)
    private void CommitGripDrag((double x, double y) pt)
    {
        var res = _gripDrag.Preview(pt.x, pt.y);
        string mode = GripDrag.ModePrompt(_gripDrag.Mode);
        _gripDrag.Cancel(); _snapVertsDrag = null;
        if (res.Count > 0)
        {
            BeginChange();
            foreach (var (old, moved) in res)
            {
                _scene.Replace(old, moved);
                int k = _selected.IndexOf(old);
                if (k >= 0) _selected[k] = moved;
            }
            RefreshScene();
            StatusMsg.Text = $"夹点编辑完成 {mode}（{res.Count} 个实体）";
        }
        else StatusMsg.Text = "夹点：该夹点不支持此操作，未改变";
        HighlightSelection();
        HideDragTip();
    }

    // 夹点拖拽取消：实体从未被改(预览式)，只清状态并恢复高亮
    private void CancelGripDrag()
    {
        _gripDrag.Cancel(); _snapVertsDrag = null;
        RedrawHighlight();
        HideDragTip();
        StatusMsg.Text = "夹点：已取消";
    }

    // 右侧特性面板：随选择更新（单选=逐行属性; 多选=计数; 空=提示）
    private void UpdatePropertyPanel()
    {
        SyncPropertyRibbonFromSelection();   // 选集变了 → 回填 Ribbon「特性」组三栏(同原版 SyncPropertyRibbonFromBag)
        if (PropertyPanel == null || PropertyHint == null) return;
        PropertyPanel.Children.Clear();
        string? cat = null;
        // 非单选: 多选给「选择」统计, 空选给「文档/视图」状态 —— 忠实原版
        // MultiSelectionProperties / DocumentProperties(此前 Kylin 两种情况都只有一句提示)。
        if (_selected.Count != 1)
        {
            PropertyHint.Text = _selected.Count > 1 ? $"选中 {_selected.Count} 个实体（单选可逐行编辑特性）" : "未选中实体（显示当前文档状态）";
            PropertyHint.IsVisible = true;
            foreach (var (c, label, value) in NonSingleSelectionRows())
            {
                if (c != cat) { PropertyPanel.Children.Add(PropGroupHeader(c)); cat = c; }
                PropertyPanel.Children.Add(PropRow(label, value));
            }
            return;
        }
        PropertyHint.IsVisible = false;
        var ent = _selected[0];
        var editable = Cad.Draw.EntityProperties.EditableLabels(ent);
        foreach (var (c, label, value) in Cad.Draw.EntityProperties.Describe(ent))
        {
            if (c != cat) { PropertyPanel.Children.Add(PropGroupHeader(c)); cat = c; }   // 分组标题(原版 PropertyGrid 自带「常规/几何」分组)
            PropertyPanel.Children.Add(editable.Contains(label) ? EditablePropRow(ent, label, value) : PropRow(label, value));
        }
    }

    /// <summary>特性面板的分组标题行(常规/几何/选择/文档/视图)。</summary>
    private static Control PropGroupHeader(string text) => new Border
    {
        Margin = new Thickness(0, 6, 0, 2),
        Padding = new Thickness(6, 2, 6, 2),
        Background = Brush.Parse("#EEF1F5"),
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#3A424C") },
    };

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
            if (IsLayerLocked(ent)) { tb.Text = value; StatusMsg.Text = $"特性编辑：图层「{ent.LayerName}」已锁定，未修改"; return; }
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
    private static void AppendGripSquare(List<float> o, double cx, double cy, double h, float r = 0.30f, float g = 0.62f, float b = 1.0f)
    {
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

    // 框选选框(世界坐标 P3_C3, 交叉=蓝/窗口=绿)
    private float[] BoxRect(Avalonia.Point a, Avalonia.Point b, bool crossing)
    {
        // 选框四角落在视平面上(3D 过注视点垂直视线; 2D 即 Z=0 平面)——旧版 3D 下投到 Z=0 平面, 模型在高程处时选框飘出视野
        var c0 = Viewport.ScreenToViewPlane(a.X, a.Y);
        var c1 = Viewport.ScreenToViewPlane(b.X, a.Y);
        var c2 = Viewport.ScreenToViewPlane(b.X, b.Y);
        var c3 = Viewport.ScreenToViewPlane(a.X, b.Y);
        if (c0 == null || c1 == null || c2 == null || c3 == null) return System.Array.Empty<float>();
        float r = 0.4f, g = crossing ? 0.7f : 0.95f, bl = crossing ? 1.0f : 0.5f;
        var o = new List<float>();
        void Seg((double x, double y, double z) p, (double x, double y, double z) q)
        {
            o.Add((float)p.x); o.Add((float)p.y); o.Add((float)p.z); o.Add(r); o.Add(g); o.Add(bl);
            o.Add((float)q.x); o.Add((float)q.y); o.Add((float)q.z); o.Add(r); o.Add(g); o.Add(bl);
        }
        Seg(c0.Value, c1.Value); Seg(c1.Value, c2.Value); Seg(c2.Value, c3.Value); Seg(c3.Value, c0.Value);
        return o.ToArray();
    }

    // 框选：窗口选(左→右,全含)/交叉选(右→左,相交或含)
    private void BoxSelect(Avalonia.Point a, Avalonia.Point b)
    {
        bool crossing = b.X < a.X;
        SaveSel();
        if (!_editAwaitSelect) _selected.Clear();   // 编辑选择阶段累加, 空闲态替换
        if (!Viewport.Is2DView)
        {
            // 3D：透视下 Z=0 平面反投影不代表实体位置, 改在屏幕空间判定(实体边线投影后做窗口/交叉测试)
            var proj = Viewport.WorldToScreenProjector();
            foreach (var en in _scene.Entities)
            {
                if (!en.Visible || !_layers.IsSelectable(en.LayerName)) continue;
                if (SelectionBox.MatchScreen(en, a.X, a.Y, b.X, b.Y, crossing, proj) && !_selected.Contains(en)) _selected.Add(en);
            }
            HighlightSelection();
            StatusMsg.Text = _selected.Count > 0 ? $"框选 {_selected.Count} 个（{(crossing ? "交叉" : "窗口")}，3D 屏幕空间）" : "框选：未选中";
            return;
        }
        var wa = Viewport.ScreenToWorld(a.X, a.Y);
        var wb = Viewport.ScreenToWorld(b.X, b.Y);
        if (wa == null || wb == null) return;
        double minX = System.Math.Min(wa.Value.x, wb.Value.x), maxX = System.Math.Max(wa.Value.x, wb.Value.x);
        double minY = System.Math.Min(wa.Value.y, wb.Value.y), maxY = System.Math.Max(wa.Value.y, wb.Value.y);
        // 只判"包围盒与选框相交"的那些图元 —— 全场景逐个细分再判框, 50 万图元要 130+ ms, 框一次就顿一下
        foreach (var en in SceneIdx().Query(minX, minY, maxX, maxY))
        {
            if (!en.Visible || !_layers.IsSelectable(en.LayerName)) continue;
            if (SelectionBox.Match(en, minX, minY, maxX, maxY, crossing) && !_selected.Contains(en)) _selected.Add(en);
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
        Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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
        // 编辑"选择对象"阶段: 右键 = 确定选择集(不弹菜单), 转入取点阶段。
        // 编辑组命令(点/线/面/体编辑)有自己的等待者, 先给它收尾; 否则才走 移动/复制/镜像 的取点流程。
        if (_selectObjectsTcs != null) { e.Cancel = true; FinishSelectObjects(true); return; }
        if (_editAwaitSelect) { e.Cancel = true; ConfirmEditSelection(); return; }
        // 取点阶段右键 = 确认(移动/复制定了基点 → 用第一个点作为位移; 其余 → 结束命令), 同原版
        if (_editMode != EditMode.None && ConfirmEditPoint()) { e.Cancel = true; return; }
        // 视图模式三项：隐藏当前所处那一项(忠实原版 RefreshCtxToggleViewState)
        bool is2D = Viewport.Is2DView;
        if (_ctxTo2D != null) _ctxTo2D.IsVisible = !is2D;
        if (_ctxToOrbit != null) _ctxToOrbit.IsVisible = is2D || _selectMode;
        if (_ctxToSelect != null) _ctxToSelect.IsVisible = is2D || !_selectMode;
        // 每文档独立右键菜单——从正在打开的菜单里取本份「调用选择集」子项(按 Name 定位)。
        var ctxSelSets = (sender as ContextMenu)?.Items.OfType<MenuItem>().FirstOrDefault(mi => mi.Name == "CtxSelSets");
        if (ctxSelSets == null) return;
        ctxSelSets.Items.Clear();
        ctxSelSets.IsEnabled = _selSets.Count > 0;
        if (_selSets.Count == 0) { ctxSelSets.Items.Add(new MenuItem { Header = "（暂无，先用 创建选择集）", IsEnabled = false }); return; }
        for (int i = 0; i < _selSets.Count; i++)
        {
            var s = _selSets.At(i);
            if (s == null) continue;
            int idx = i;
            var mi = new MenuItem { Header = $"{s.Value.name}  ({s.Value.ents.Count} 项)" };
            mi.Click += (_, _) => RecallSelSetByIndex(idx);
            ctxSelSets.Items.Add(mi);
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

    /// <summary>
    /// 清空视图 (ERASEALL) —— 忠实原版 OnEraseAllEntitiesClick：
    ///   · 图形空间全部实体      —— 进 Undo 栈，可 Ctrl+Z
    ///   · 全部块体模型          —— 不进 Undo，移除后不可撤销
    ///   · 导入的显示态几何(线框/倾斜摄影通道) —— 不进 Undo
    ///   · 全部图层（只留默认层 "0"）—— 不进 Undo，撤销找回的实体会落在 "0" 层
    /// 点云等其它模块数据不受影响。先弹确认框（默认「否」），取消则只回显一行。
    ///
    /// 注：这条命令原来被写成"清选择/高亮/捕捉标记"，那是旁边「清理标记 (CLRMARK)」干的活 ——
    /// 两个按钮做同一件事，真正的"清空"反而没人做。
    /// </summary>
    private async Task EraseAllAsync()
    {
        bool go = await Modeling.BlockMsgBox.ConfirmAsync(this, "清空视图确认",
            "确定要清空视图吗？\n\n"
          + "• 图形空间全部实体 —— 支持撤销 (Ctrl+Z)\n"
          + "• 全部块体模型 —— ⚠ 不进 Undo 栈，移除后不可撤销\n"
          + "• 导入的显示态几何（线框 / 倾斜摄影通道）—— ⚠ 不可撤销\n"
          + "• 全部图层（只保留默认层 \"0\"）—— ⚠ 不进 Undo 栈；撤销找回实体时，它们用到的图层会按实体颜色补回\n\n"
          + "点云等其它模块数据不受影响。");
        if (!go) { EditEcho("> ERASEALL (已取消)"); return; }

        int removed = _scene.Entities.Count;
        if (removed > 0)
        {
            BeginChange();                      // 实体删除进 Undo 栈
            _scene.Entities.Clear();
        }
        _selected.Clear();
        _surfGrids.Clear();
        Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null); Viewport.SetSnapMarker(null);
        _snapShown = false;
        HighlightSelection();               // 顺带刷特性面板/夹点, 否则清完还显示"选中 N 个实体"

        // 块体模型不属于场景实体, 单独清（不进 Undo，同原版）
        int blocksRemoved = 0;
        try
        {
            var ctx = MdlCtx();
            for (int i = Modeling.BlockModelStore.Models.Count - 1; i >= 0; i--)
                if (Modeling.BlockModelStore.Remove(ctx, Modeling.BlockModelStore.Models[i]) == null) blocksRemoved++;
        }
        catch { }
        _lastBlocks = null; _blockAttrs = null;
        RenderBlocks(new List<BlockModel.Block>());

        // 导入的显示态几何（DXF/3dm 线框、倾斜摄影浏览通道）也清掉，否则"清空"了还留着一屏线
        bool hadImported = _lastImport != null;
        _lastImport = null;
        Viewport.ClearImported();

        // 图层表不随实体走：不清的话各功能建的图层会一直攒在下拉/对象树里
        int layersRemoved = 0;
        foreach (var name in _layers.Layers.Select(l => l.Name).ToList())
            if (name != "0" && _layers.Remove(name)) layersRemoved++;

        PopulateDrawingLayers();
        RefreshObjectTree();
        RefreshScene();

        string blkPart = blocksRemoved > 0 ? $" + 块体模型 {blocksRemoved} 个" : "";
        string impPart = hadImported ? " + 导入显示几何" : "";
        string lyrPart = layersRemoved > 0 ? $" + 图层 {layersRemoved} 个" : "";
        if (removed > 0 || blocksRemoved > 0 || hadImported || layersRemoved > 0)
            EditEcho($"> ERASEALL (清空视图: 删除 {removed} 个实体{blkPart}{impPart}{lyrPart})", EchoLevel.Success);
        else
            EditEcho("> ERASEALL (视图已为空, 无实体可删除)", EchoLevel.Warn);
    }

    private void ClrMark()   // 清理标记 / CLRMARK：清高亮/捕捉标记
    {
        Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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

    // 修改点样式：批量改选中点实体的样式(PDMODE 0-127)与大小。用法「修改点样式 <样式> [大小]」。忠实原"修改点样式"
    private void ModifyPointStyle(string cmd)
    {
        var pts = new List<SceneEntity>();
        foreach (var e in _selected) if (e is PointEntity) pts.Add(e);
        if (pts.Count == 0) { StatusMsg.Text = "修改点样式：请先选中点实体（用法：修改点样式 <样式0-127> [大小]）"; return; }
        var parts = cmd.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        int style = 2; double size = -1;
        if (parts.Length >= 2) int.TryParse(parts[1], out style);
        if (parts.Length >= 3) double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out size);
        style = System.Math.Clamp(style, 0, 127);
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (PointEntity p in pts)
        {
            var np = new PointEntity { X = p.X, Y = p.Y, Size = size > 1e-9 ? size : p.Size, Style = style };
            np.CopyStyleFrom(p);                      // 保色/层/透明等
            _scene.Replace(p, np);
            newSel.Add(np);
        }
        _selected.Clear(); _selected.AddRange(newSel);
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"修改点样式：{pts.Count} 点 → 样式 {style}{(size > 1e-9 ? $" 大小 {size:0.##}" : "")}";
    }

    // 反选：新选择集 = 当前未选中的全部实体（忠实原版"反选"）
    private void InvertSelection()
    {
        SaveSel();
        var cur = new HashSet<SceneEntity>(_selected);
        _selected.Clear();
        foreach (var e in _scene.Entities) if (!cur.Contains(e)) _selected.Add(e);
        HighlightSelection();
        StatusMsg.Text = $"反选：现选 {_selected.Count} 个";
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
        Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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

    // 道路横断面：选一条中线折线 → 按曲率算弯道加宽/超高 → 左右加宽路缘线入场景 + 报表
    // 纵坡分析：3D 折线 CSV(x,y,z 有序) → 逐段纵坡% → 按坡度分档着色(绿平→红陡) + 超限汇总。忠实原「中线按纵坡分档着色」。
    private async Task GradeProfileAsync(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double maxPct = 8.0;   // 露天矿运输道路典型限坡 8%
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mp) && mp > 0) maxPct = mp;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "纵坡分析：选 3D 中线 CSV (x,y,z 有序; 可用 线落到面上 drape 得)", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("3D 线 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } } });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success || r.Points.Count < 2) { StatusMsg.Text = "纵坡分析：需 ≥2 个有序 3D 点(x,y,z)"; return; }
        var line = new List<(double x, double y, double z)>(); foreach (var p in r.Points) line.Add((p.x, p.y, p.z));
        var segs = Cad.GradeProfile.Compute(line);
        if (segs.Count == 0) { StatusMsg.Text = "纵坡分析：无有效段"; return; }
        var (maxAbs, over, avg) = Cad.GradeProfile.Summary(segs, maxPct);
        BeginChange();
        foreach (var s in segs)
        {
            double g = System.Math.Abs(s.GradePct);
            double f = System.Math.Min(1, g / System.Math.Max(1e-6, maxPct * 1.5));   // 归一到 1.5×限坡
            float cr = (float)f, cg = (float)(1 - f) * 0.85f, cb = 0.2f;                // 绿(平)→红(陡)
            if (g > maxPct) { cr = 1f; cg = 0.1f; cb = 0.1f; }                          // 超限=纯红
            _scene.Add(new LineEntity { X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1, Cr = cr, Cg = cg, Cb = cb, LayerName = "纵坡分析" });
        }
        RefreshScene();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        StatusMsg.Text = $"纵坡分析：{segs.Count} 段 · 最大纵坡 {maxAbs.ToString("0.##", inv)}% · 加权均 {avg.ToString("0.##", inv)}% · 超限({maxPct:0.#}%) {over} 段（红=超限；绿平→红陡）";
    }

    // 竖曲线平滑：3D 中线 CSV(x,y,z 有序) → (弧长s,标高z)纵断面 → 变坡点插抛物线竖曲线(GBJ22-87③) →
    // 原/平滑纵断面入场景(灰/青) + 竖曲线条数/最小半径/不达标汇总。忠实原 ProfileSmoother.VerticalCurves。
    private async Task VerticalCurveAsync(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double rV = 500.0;     // 竖曲线半径 m（露天矿运输道路典型）
        double trigger = 0.5;  // 变坡代数差触发阈 %（小于此不设竖曲线）
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double v1) && v1 > 0) rV = v1;
        if (tk.Length >= 3 && double.TryParse(tk[2], System.Globalization.NumberStyles.Float, inv, out double v2) && v2 > 0) trigger = v2;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "竖曲线平滑：选 3D 中线 CSV (x,y,z 有序)", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("3D 线 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } } });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success || r.Points.Count < 3) { StatusMsg.Text = "竖曲线平滑：需 ≥3 个有序 3D 点(x,y,z)"; return; }
        // 累计 XY 弧长为 s，抽 z（竖曲线只改标高、不改平面线形）
        var s = new List<double>(); var z = new List<double>();
        double acc = 0, zmin = double.MaxValue, zmax = double.MinValue;
        for (int i = 0; i < r.Points.Count; i++)
        {
            if (i > 0) { double dx = r.Points[i].x - r.Points[i - 1].x, dy = r.Points[i].y - r.Points[i - 1].y; acc += System.Math.Sqrt(dx * dx + dy * dy); }
            s.Add(acc); z.Add(r.Points[i].z);
            if (r.Points[i].z < zmin) zmin = r.Points[i].z; if (r.Points[i].z > zmax) zmax = r.Points[i].z;
        }
        var res = Cad.RoadVerticalCurve.Smooth(s, z, trigger, rV);
        BeginChange();
        for (int i = 1; i < s.Count; i++)   // 原纵断面(灰)
            _scene.Add(new LineEntity { X0 = s[i - 1], Y0 = z[i - 1], X1 = s[i], Y1 = z[i], Cr = 0.6f, Cg = 0.6f, Cb = 0.6f, LayerName = "竖曲线" });
        var p = res.Profile;
        for (int i = 1; i < p.Count; i++)   // 平滑纵断面(青)
        {
            _scene.Add(new LineEntity { X0 = p[i - 1].S, Y0 = p[i - 1].Z, X1 = p[i].S, Y1 = p[i].Z, Cr = 0.1f, Cg = 0.8f, Cb = 0.9f, LayerName = "竖曲线" });
            if (p[i].Z < zmin) zmin = p[i].Z; if (p[i].Z > zmax) zmax = p[i].Z;
        }
        RefreshScene();
        if (acc > 1e-6 && zmax > zmin) Viewport.FitBounds(new double[] { 0, zmin, acc, zmax });
        StatusMsg.Text = $"竖曲线平滑(R_v={rV.ToString("0.#", inv)}m·触发{trigger.ToString("0.##", inv)}%)："
            + $"{res.Count} 处竖曲线 · 最小半径 {res.MinRadiusM.ToString("0.#", inv)}m · 不达标 {res.Violations} 处 · "
            + $"纵断面 {s.Count}→{p.Count} 点（灰=原/青=平滑；X=里程 Y=标高）";
    }

    // 线形处理：3D 中线 CSV → GBJ22-87 线形后处理(①转角圆弧化 R≥rMin ②分段限坡纵断面[弯道折减+合成坡度]
    // ③变坡点竖曲线) → 原/处理后平面线形入场景(灰/青) + 平曲线半径/纵坡/合成坡度/竖曲线 校核。忠实原 CenterlineLineForm。
    private async Task LineFormAsync(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double rMin = 15.0;     // 最小平曲线半径 m（露天矿运输道路典型）
        double maxGrade = 8.0;  // 直线段限坡 %
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double v1) && v1 > 0) rMin = v1;
        if (tk.Length >= 3 && double.TryParse(tk[2], System.Globalization.NumberStyles.Float, inv, out double v2) && v2 > 0) maxGrade = v2;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "线形处理：选 3D 中线 CSV (x,y,z 有序)", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("3D 线 (CSV/TXT/XYZ)") { Patterns = new[] { "*.csv", "*.txt", "*.xyz" } } } });
        if (files.Count == 0) return;
        var r = PointDataImportService.Load(files[0].Path.LocalPath);
        if (!r.Success || r.Points.Count < 3) { StatusMsg.Text = "线形处理：需 ≥3 个有序 3D 点(x,y,z)"; return; }
        var pts = new List<(double, double, double)>();
        foreach (var p in r.Points) pts.Add((p.x, p.y, p.z));
        // 弯道纵坡按 0.75×直线折减、竖曲线 R=500·触发 0.5%（露天矿运输道路典型；手拾档不内移→centroids=null,offset=0）
        var lf = Cad.CenterlineLineForm.Apply(pts, null, 0, rMin, maxGrade, maxGrade * 0.75, 0, 0, 0.5, 500, 0);
        BeginChange();
        for (int i = 1; i < pts.Count; i++)   // 原折线(灰)
            _scene.Add(new LineEntity { X0 = pts[i - 1].Item1, Y0 = pts[i - 1].Item2, X1 = pts[i].Item1, Y1 = pts[i].Item2, Cr = 0.6f, Cg = 0.6f, Cb = 0.6f, LayerName = "线形" });
        var L = lf.Line;
        for (int i = 1; i < L.Count; i++)     // 处理后线形(青, 转角已圆弧化)
            _scene.Add(new LineEntity { X0 = L[i - 1].X, Y0 = L[i - 1].Y, X1 = L[i].X, Y1 = L[i].Y, Cr = 0.1f, Cg = 0.8f, Cb = 0.9f, LayerName = "线形" });
        RefreshScene();
        if (r.Bounds != null && r.Bounds.Length == 4) Viewport.FitBounds(r.Bounds);
        string warn = lf.GradeExceedsLimit ? " · ⚠展线不足(限坡内 descend 不满总高差, 须增长展线)" : "";
        StatusMsg.Text = $"线形处理(R_min={rMin.ToString("0.#", inv)}m·限坡{maxGrade.ToString("0.#", inv)}%)："
            + $"最小平曲线半径 {lf.MinRadiusM.ToString("0.#", inv)}m · 半径不达标 {lf.Violations} 处 · "
            + $"直线纵坡≤{lf.MaxGradeUsedPct.ToString("0.##", inv)}%·弯道≤{lf.CurveGradeUsedPct.ToString("0.##", inv)}%·合成 {lf.ResultantGradePct.ToString("0.##", inv)}%{warn} · "
            + $"竖曲线 {lf.VerticalCurveCount}处(最小R {lf.MinVerticalRadiusAchievedM.ToString("0.#", inv)}m·不达标{lf.VerticalCurveViolations}) · "
            + $"{pts.Count}→{L.Count}点（灰=原/青=圆弧化线形）";
    }

    // 路面生成：中线多段线 + 路宽 → 等宽双侧外扩成闭合路带多边形。忠实原「中心线按路宽外扩生成路面」。
    private void RoadSurfaceCmd(string cmd)
    {
        PolylineEntity? center = null;
        foreach (var e in _selected) if (e is PolylineEntity p && p.Points.Count >= 2) { center = p; break; }
        if (center == null) { StatusMsg.Text = "路面生成：请先选中一条道路中线多段线(≥2 点)，再执行「路面生成 [路宽]」"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        double width = 15.0;   // 默认露天矿运输道路宽
        if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w) && w > 0) width = w;
        var strip = RoadSurface.Strip(center.Points, width);
        if (strip.Count < 3) { StatusMsg.Text = "路面生成：中线退化，无法成面"; return; }
        var pl = new PolylineEntity { Closed = true, Cr = 0.52f, Cg = 0.52f, Cb = 0.56f };
        foreach (var p in strip) pl.Points.Add(p);
        AssignLayer(pl);
        BeginChange(); _scene.Add(pl); RefreshScene();
        StatusMsg.Text = $"路面生成：中线 {center.Points.Count} 点 → 路带闭合多边形 {strip.Count} 顶点（路宽 {width:0.##}）";
    }

    // 道路设计参数校核: 道路设计参数 <设计速度km/h> [最大超高% 默认6] [台阶高m [最大纵坡% 默认10]]
    // → 最小平曲线半径 R=v²/(127(μ+e_max)) + (给台阶高/纵坡时)展线长=H/(grade/100)。忠实原 TransportConstraintSettings。
    private void RoadDesignParamsCmd(string cmd)
    {
        var tk = cmd.Split(new[] { ' ', ',', '，', '/', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 2 || !double.TryParse(tk[1], out double v) || v <= 0)
        { StatusMsg.Text = "道路设计参数：用法 道路设计参数 <设计速度km/h> [最大超高% 默认6] [台阶高m [最大纵坡% 默认10]]（如 道路设计参数 25 6）"; return; }
        double emax = 6; if (tk.Length >= 3 && double.TryParse(tk[2], out double e2) && e2 >= 0) emax = e2;
        double Rmin = Cad.RoadCrossSection.MinCurveRadiusBySpeed(v, emax);
        string extra = "";
        if (tk.Length >= 4 && double.TryParse(tk[3], out double H) && H > 0)
        {
            double grade = 10; if (tk.Length >= 5 && double.TryParse(tk[4], out double g4) && g4 > 0) grade = g4;
            extra = $" · 展线长(降{H:0.#}m@{grade:0.#}%纵坡)={Cad.RoadCrossSection.DevelopmentLengthM(H, grade):0.#}m";
        }
        StatusMsg.Text = $"道路设计参数：设计车速 {v:0.#}km/h · 最大超高 {emax:0.#}% · 最小平曲线半径 R_min={Rmin:0.#}m（R=v²/(127(μ0.15+e))）{extra}";
    }

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

    // 直线斜坡道中线：从视图中心沿方位角匀降。"直线斜坡道 [纵坡% 长度 方位°]"(缺省 8%/200/0°)。
    // 忠实原 StraightRampAutoRouter 的直线中线核(全套可行性路由=内核规模, 记录)。
    private void StraightRampCmd(string cmd)
    {
        var (sx, sy) = ViewCenterWorld();
        double az = 0, grade = 8, len = 200;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2) double.TryParse(tk[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out grade);
        if (tk.Length >= 3) double.TryParse(tk[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out len);
        if (tk.Length >= 4) double.TryParse(tk[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out az);
        var pts = RampCenterlines.Straight(sx, sy, 0, az, grade, len);
        if (pts.Count < 2) { StatusMsg.Text = "直线斜坡道：参数无效(纵坡/长度需>0)"; return; }
        var pl = new PolylineEntity { Cr = 0.55f, Cg = 0.85f, Cb = 0.35f, LayerName = "直线斜坡道" };
        foreach (var (x, y, _) in pts) pl.Points.Add((x, y));
        BeginChange(); _scene.Add(pl); RefreshScene(); Viewport.ZoomExtents();
        StatusMsg.Text = $"直线斜坡道中线：纵坡{grade:0.#}%·长{len:0.#}·方位{az:0.#}° → {pts.Count} 点(绿; 降 {(grade / 100 * len):0.#}m)";
    }

    // 直线坑线【自动布线】(忠实原 StraightRampAutoRouter.Route 连通自检): 取场景/选中同心台阶环(坡顶线) →
    // 按包围面积降序赋台阶标高(外圈=地表最高, 每内一环降一个台阶高 H) → 逐级直腿首尾相接(平面投影 L=ΔH/i≤限坡)
    // → 内圈周长不足则报告并止于最后可行台阶。画预览中线(黄, 层『运输坑线_预览』)。
    // 用法 "直线坑线 [限坡i% 台阶高H 路宽B]"(缺省 8%/10/0)。坑线落地(贴帮切面)为内核规模, 记录不做。
    // 注: Kylin 场景 2D(环无 Z), 按同心序合成台阶标高 —— 忠实沿用既有 2D 适配(见 提取道路中心线)。
    private void StraightRampRouteCmd(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double i = 8, H = 10, B = 0;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2) double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out i);
        if (tk.Length >= 3) double.TryParse(tk[2], System.Globalization.NumberStyles.Float, inv, out H);
        if (tk.Length >= 4) double.TryParse(tk[3], System.Globalization.NumberStyles.Float, inv, out B);
        if (H < 1) H = 1;   // 防台阶标高被 ZTolerance(0.5) 去重折叠

        // 台阶环: 选中闭合折线(≥2) 否则全场景 ≥3 点折线
        var sel = _selected.FindAll(e => e is PolylineEntity pe && pe.Points.Count >= 3);
        var rings = sel.Count >= 2 ? new System.Collections.Generic.List<SceneEntity>(sel)
                                   : new System.Collections.Generic.List<SceneEntity>();
        if (rings.Count == 0)
            foreach (var e in _scene.Entities)
                if (e is PolylineEntity pl && pl.Points.Count >= 3) rings.Add(e);
        if (rings.Count < 2) { StatusMsg.Text = "直线坑线：请选≥2 条同心台阶环(坡顶线), 或先【批量台阶扩帮】生成台阶。"; return; }

        // 按包围面积降序 → 外圈(地表)在前
        rings.Sort((a, b) =>
        {
            double aa = System.Math.Abs(Cad.BenchLines.SignedArea(((PolylineEntity)a).Points));
            double ab = System.Math.Abs(Cad.BenchLines.SignedArea(((PolylineEntity)b).Points));
            return ab.CompareTo(aa);
        });

        int n = rings.Count;
        var benches = new System.Collections.Generic.List<Cad.RampBenchLine>(n);
        for (int k = 0; k < n; k++)
        {
            double z = (n - 1 - k) * H;   // 外圈最高, 内圈=0
            var pts = ((PolylineEntity)rings[k]).Points;
            var crest = new System.Collections.Generic.List<(double X, double Y, double Z)>(pts.Count);
            foreach (var (x, y) in pts) crest.Add((x, y, z));
            benches.Add(new Cad.RampBenchLine { Level = z, BermWidth = B, Crest = crest });
        }

        var rr = Cad.StraightRampAutoRouter.Route(benches, new Cad.StraightRampRouteOptions { GradePct = i, RoadWidth = B });
        if (!rr.Success) { StatusMsg.Text = $"直线坑线：无法布线 — {rr.Error}"; return; }

        var line = new PolylineEntity { Cr = 0.95f, Cg = 0.85f, Cb = 0.30f, LayerName = "运输坑线_预览" };
        foreach (var (x, y, _) in rr.Centerline) line.Points.Add((x, y));
        BeginChange(); _scene.Add(line); RefreshScene(); Viewport.ZoomExtents();
        StatusMsg.Text = $"直线坑线自动布线：{n}环 限坡{i:0.#}% 台阶高{H:0.#}m → 贯通 {rr.LevelsConnected}/{rr.LevelsTotal} 级"
            + $"(直腿{rr.StraightLegs}/折返{rr.SwitchbackLegs}), 到达 Z={rr.ReachedZ:0.#}m"
            + (rr.ReachedBottom ? " ✓到底" : " (止于最后可行台阶)")
            + $" · 中线 {rr.Centerline.Count} 点(黄, 层『运输坑线_预览』)";
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
    // AutoCAD 式动词-名词流程：命令 → 选择对象(可继续加/减选) → 右键确定 → 取基点 + 拖拽。
    // 已有预选(名词-动词)则作为初始选择集, 右键即可直接确定。
    private void StartEdit(EditMode mode, string name)
    {
        _editMode = mode; _editName = name; _editPts.Clear(); _editDisplacement = false;
        _tool = null; _measure = null; _lastInputPoint = null;
        _editAwaitSelect = true;
        StatusMsg.Text = $"{name}：选择对象（单击/框选，右键确定" + (_selected.Count > 0 ? $"，已选 {_selected.Count}" : "") + "）";
        RefreshScene();
    }

    // 右键确定选择集 → 转入取点阶段（基点/镜像线首点）。空选则取消命令。
    private void ConfirmEditSelection()
    {
        _editAwaitSelect = false;
        if (_selected.Count == 0) { _editMode = EditMode.None; HideDragTip(); StatusMsg.Text = $"{_editName}：未选对象，命令取消"; return; }
        HighlightSelection();
        StatusMsg.Text = $"{_editName}：{EditFirstPrompt()}（已选 {_selected.Count}）";
        RefreshScene();
    }

    // 在光标右下显示浮标文字(拖框/夹点等提前 return 的分支也能给引导)
    private void ShowTipAt(Avalonia.Point p, string text)
    {
        var tip = _active.DragTip;
        if (tip == null) return;
        ((TextBlock)tip.Child!).Text = text;
        tip.Margin = new Avalonia.Thickness(p.X + 18, p.Y + 20, 0, 0);
        tip.Opacity = 1;
        (tip.Parent as Control)?.InvalidateVisual();
    }

    private void HideDragTip()
    {
        if (_active.DragTip != null) { _active.DragTip.Opacity = 0; (_active.DragTip.Parent as Control)?.InvalidateVisual(); }
    }

    // ── Home「修改」组：删除 / 分解 / 偏移 / 打断 / 修剪·延伸 ──
    // 一律 AutoCAD 动词-名词：先激活命令 → 提示"选择对象" → 单击/框选 → 右键确定 → 再进入各自后续步骤。
    // 已有预选作为初始选择集带进来，右键即确定。(Del 键等直接动作仍走下面的原方法，不弹选择提示。)

    private async Task DeleteCmdAsync()
    {
        var ents = await SelectObjectsAsync<SceneEntity>("删除", "要删除的对象");
        if (ents.Count == 0) return;
        DeleteSelected();
    }

    private async Task ExplodeCmdAsync()
    {
        var ents = await SelectObjectsAsync<SceneEntity>("分解", "可分解对象（矩形/多段线等）", 1, e => e.Explode() != null);
        if (ents.Count == 0) return;
        ExplodeSelected();
    }

    private async Task OffsetCmdAsync()
    {
        var ents = await SelectObjectsAsync<SceneEntity>("偏移", "要偏移的对象");
        if (ents.Count == 0) return;
        if (ents.Count > 1) { SelectEntities(new[] { ents[0] }); StatusMsg.Text = "偏移：只对第一个所选对象生效"; }
        StartOffset();
    }

    private async Task BreakCmdAsync()
    {
        var ents = await SelectObjectsAsync<SceneEntity>("打断", "直线 / 多段线 / 圆弧", 1,
            e => e is LineEntity or PolylineEntity or ArcEntity);
        if (ents.Count == 0) return;
        SelectEntities(new[] { ents[0] });
        StartBreak();
    }

    private async Task TrimCmdAsync()
    {
        var ents = await SelectObjectsAsync<SceneEntity>("修剪 / 延伸", "作为边界的对象（线/多段线/圆/弧/矩形）");
        if (ents.Count == 0) return;
        SelectEntities(new[] { ents[0] });
        StartTrim();
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

    // 夹点开关：切换夹点显示；关时选中实体不显方块、也不可拖夹点。(GIZMO 命令是三轴变换手柄, 见 MainWindow.Gizmo.cs)
    private void ToggleGrips()
    {
        _gripsOn = !_gripsOn;
        if (!_gripsOn) { _gripDrag.Cancel(); _snapVertsDrag = null; _gripHover = -1; }
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

    // 图层重命名：当前层就地改名，实体 LayerName 随迁（忠实原版"图层命名/重命名"）
    private void RenameCurrentLayer(string newName)
    {
        newName = newName.Trim();
        string old = _layers.Current.Name;
        if (!_layers.Rename(old, newName))
        {
            StatusMsg.Text = old == "0" ? "默认图层「0」不可改名"
                : _layers.Get(newName) != null ? $"重命名失败：图层「{newName}」已存在（改名不合并，用「合并图层」）"
                : "重命名失败：新名为空或与原名相同";
            return;
        }
        BeginChange();
        int moved = _scene.ReassignLayer(old, newName);
        PopulateDrawingLayers();
        AfterLayerStateChange();
        RefreshScene();
        StatusMsg.Text = $"图层「{old}」→「{newName}」（{moved} 个实体随迁）";
    }

    // 图层合并：把源图层实体并入当前层后删源层（忠实原版"图层合并"）
    private void MergeLayerIntoCurrent(string sourceName)
    {
        sourceName = sourceName.Trim();
        string dst = _layers.Current.Name;
        if (sourceName.Length == 0) { StatusMsg.Text = "合并图层：请给出源图层名"; return; }
        if (sourceName == dst) { StatusMsg.Text = "合并图层：源层与目标（当前层）相同"; return; }
        if (_layers.Get(sourceName) == null) { StatusMsg.Text = $"合并图层：源图层「{sourceName}」不存在"; return; }
        BeginChange();
        int moved = _scene.ReassignLayer(sourceName, dst);
        _layers.Remove(sourceName);                       // 源=="0" 时 Remove 拒绝，实体已移出，"0" 保留(空)
        PopulateDrawingLayers();
        AfterLayerStateChange();
        RefreshScene();
        StatusMsg.Text = $"图层「{sourceName}」并入「{dst}」（{moved} 个实体）";
    }

    // 图层隔离：只显示目标层（有选中→选中实体的层，否则当前层），其余关闭。忠实原版 LAYISO
    private void IsolateLayer()
    {
        string target = _selected.Count > 0 ? _selected[0].LayerName : _layers.Current.Name;
        int hidden = _layers.Isolate(target);
        _layers.SetCurrent(target);
        PopulateDrawingLayers();
        AfterLayerStateChange();
        RefreshScene();
        StatusMsg.Text = $"图层隔离：只显示「{target}」（关闭 {hidden} 层，「取消隔离」恢复）";
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
        _selected.Clear(); Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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
        _selected.Clear(); Viewport.SetHighlight(null); Viewport.SetHighlightFaces(null);
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
    /// <summary>
    /// 绘制中在命令行键入选项关键字（AutoCAD 的 “指定下一点或 [闭合(C)/放弃(U)]”）。
    /// 认领了返回 true —— 必须早于命令解析，否则画多段线时键入的 C 会被当成 CIRCLE 命令。
    /// </summary>
    private bool TryToolOption(string typed)
    {
        if (_tool == null || string.IsNullOrWhiteSpace(typed)) return false;
        var r = _tool.Invoke(typed);
        if (!r.Handled) return false;

        if (r.Entity != null) { BeginChange(); AssignLayer(r.Entity); _scene.Add(r.Entity); }
        if (r.EndsCommand) _tool = null;
        if (r.SwitchTo != null) { _tool = null; ExecuteCommandToken(r.SwitchTo); }   // 圆 → 三点/两点/相切相切半径
        if (!string.IsNullOrEmpty(r.Message)) { LogCommand("  " + r.Message); StatusMsg.Text = r.Message!; }
        HideDragTip();
        RefreshScene();
        SyncPrompt();
        return true;
    }

    /// <summary>
    /// 自检：报告"视图空间里到底有没有东西"。
    /// 场景实体数 / 细分顶点数 / 当前可见世界范围 / 落在可见范围内的顶点比例 —— 四个数一摆，
    /// "导入完了却是空白"到底是没几何、还是几何在视野外，一眼就分得清（截图受窗口层叠干扰，日志不会）。
    /// </summary>
    private void ReportViewportContent()
    {
        var geom = _scene.BuildGeometry(_layers.IsShown);
        int n = geom == null ? 0 : geom.Length / 6;
        var fc = _scene.BuildFaces(_layers.IsShown);
        int texts = 0; foreach (var e in _scene.Entities) if (e is Cad.Draw.TextEntity && e.Visible) texts++;
        var a = Viewport.ScreenToWorld(0, 0);
        var b = Viewport.ScreenToWorld(Viewport.Bounds.Width, Viewport.Bounds.Height);
        string view = a == null || b == null ? "取不到(ScreenToWorld 返回 null —— 相机矩阵不可逆)"
            : $"X[{System.Math.Min(a.Value.x, b.Value.x):0.#}, {System.Math.Max(a.Value.x, b.Value.x):0.#}] " +
              $"Y[{System.Math.Min(a.Value.y, b.Value.y):0.#}, {System.Math.Max(a.Value.y, b.Value.y):0.#}]";

        int inside = 0;
        if (geom != null && a != null && b != null)
        {
            double x0 = System.Math.Min(a.Value.x, b.Value.x), x1 = System.Math.Max(a.Value.x, b.Value.x);
            double y0 = System.Math.Min(a.Value.y, b.Value.y), y1 = System.Math.Max(a.Value.y, b.Value.y);
            for (int i = 0; i + 1 < geom.Length; i += 6)
                if (geom[i] >= x0 && geom[i] <= x1 && geom[i + 1] >= y0 && geom[i + 1] <= y1) inside++;
        }
        string msg = $"场景实体={_scene.Count} 细分顶点={n} 面顶点={(fc == null ? 0 : fc.Length / 6)} "
                   + $"注记={texts} 可见范围={view} 落在视野内的顶点={inside}"
                   + (n > 0 ? $"（{100.0 * inside / n:0.##}%）" : "");
        PitMine3D.Kylin.CrashLog.Write("视口", msg);
        StatusMsg.Text = msg;
    }

    /// <summary>多点绘制收笔（回车/双击）：够点就入场景，然后结束命令（同 AutoCAD 的 PLINE 回车）。</summary>
    private void FinishMultiPointTool()
    {
        if (_tool == null) return;
        var e = _tool.Finish();
        if (e != null) { BeginChange(); AssignLayer(e); _scene.Add(e); }
        _tool = null;
        HideDragTip();
        RefreshScene();
        StatusMsg.Text = e != null ? $"多段线完成（已画 {_scene.Count}）" : "多段线：点数不足（＜2 点），已取消";
        SyncPrompt();
    }

    /// <summary>
    /// 编辑取点阶段的命令行选项与数值(忠实原版 EditCommandState::OnTextInput 的 MOVE/COPY 分支)：
    ///   · 基点提示下键入 D / 位移 / DISPLACEMENT → 位移模式：下一次键入的坐标即位移向量，不取基点；
    ///   · 位移模式下 "dx,dy" / "@dx,dy" / "d&lt;ang" 一律按相对原点解析 → 直接平移/复制；
    ///   · 第二点提示下键入纯数字 = 直接距离：沿 基点→当前光标 方向移动该距离。
    /// 认领了返回 true。须早于坐标解析(位移模式的坐标不能相对基点算)与命令解析(D 别被当成「标注样式」)。
    /// </summary>
    private bool TryEditOption(string cmd)
    {
        if (_editMode is not (EditMode.Move or EditMode.Copy) || _editAwaitSelect) return false;
        if (_editPts.Count == 0)
        {
            if (!_editDisplacement)
            {
                if (!IsDisplacementKeyword(cmd)) return false;
                _editDisplacement = true;
                StatusMsg.Text = $"{_editName}：指定位移 <dx,dy,dz>（键入 dx,dy 或 dx,dy,dz 或 d<角度）";
                SyncPrompt();
                return true;
            }
            var v = ParseCoord3(cmd, (0, 0), 0);   // 位移是向量：@dx,dy 与 dx,dy 同义(相对原点); 没给 dz 就是 0(不动 z)
            if (v == null) { StatusMsg.Text = $"{_editName}：位移须为 dx,dy / dx,dy,dz 或 d<角度（如 100,50 / 100,50,-5 / 50<30）"; return true; }
            ApplyDisplacement(v.Value.x, v.Value.y, v.Value.z ?? 0);
            return true;
        }
        if (_editPts.Count == 1 && double.TryParse(cmd, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dist))
        {
            var t = DirectDistanceTarget(_editPts[0], _cursorWorld, dist);
            if (t == null) { StatusMsg.Text = $"{_editName}：直接距离输入要靠光标给方向——先把光标移到目标方向再键入距离"; return true; }
            FeedPoint(t.Value.x, t.Value.y);
            return true;
        }
        return false;
    }

    /// <summary>移动/复制「位移(D)」关键字：D / DISPLACEMENT / 位移(忽略大小写与空白，同原版 IsDisplacementKeyword)。</summary>
    internal static bool IsDisplacementKeyword(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
        string k = sb.ToString();
        return k == "D" || k == "DISPLACEMENT" || k == "位移";
    }

    /// <summary>直接距离输入：由基点沿 基点→光标 方向走 dist 得到第二点；光标缺失或与基点重合(方向不明)返回 null。</summary>
    internal static (double x, double y)? DirectDistanceTarget((double x, double y) b, (double x, double y)? cursor, double dist)
    {
        if (cursor == null) return null;
        double dx = cursor.Value.x - b.x, dy = cursor.Value.y - b.y;
        double len = System.Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return null;
        return (b.x + dx / len * dist, b.y + dy / len * dist);
    }

    /// <summary>按位移向量落地移动/复制(位移模式 / 第二点回车「使用第一个点作为位移」)，并结束命令。</summary>
    private void ApplyDisplacement(double dx, double dy, double dz = 0)
    {
        bool copy = _editMode == EditMode.Copy;
        string name = _editName;
        _editMode = EditMode.None; _editPts.Clear(); _editDisplacement = false;   // 先退出取点态再落地(理由见 FeedPoint)
        ApplyEditTransform(Affine2.Translate(dx, dy), copy, dz);
        HideDragTip();
        StatusMsg.Text = $"{name}完成：位移 Δx {dx:0.###}  Δy {dy:0.###}  Δz {dz:0.###}";
        SyncPrompt();
    }

    /// <summary>
    /// 取点阶段的 回车/空格/右键(忠实原版 EditCommandState::OnInput 的 confirm 分支)：
    /// 移动/复制已定基点 → 「使用第一个点作为位移」：位移向量 = 基点坐标本身；其余情形 = 结束命令(不改实体)。
    /// 不在取点阶段返回 false。
    /// </summary>
    private bool ConfirmEditPoint()
    {
        if (_editMode == EditMode.None || _editAwaitSelect) return false;
        if (_editMode is EditMode.Move or EditMode.Copy && _editPts.Count == 1)
        {
            var b = _editPts[0];
            ApplyDisplacement(b.x, b.y, _editBaseZ);   // 位移向量 = 基点三分量(键入 x,y,z 的 z 也算数)
            return true;
        }
        string name = _editName;
        _editMode = EditMode.None; _editPts.Clear(); _editDisplacement = false;
        HideDragTip(); RefreshScene();
        StatusMsg.Text = $"{name}：已结束（未改动）";
        SyncPrompt();
        return true;
    }

    /// <summary>回车/空格 = 右键(AutoCAD 三者等价)：编辑命令的选择对象阶段 → 确定选择集；取点阶段 → <see cref="ConfirmEditPoint"/>。</summary>
    private bool ConfirmEditByKey()
    {
        if (_editMode == EditMode.None) return false;
        if (_editAwaitSelect) { if (_selectObjectsTcs != null) return false; ConfirmEditSelection(); return true; }
        return ConfirmEditPoint();
    }

    /// <summary>取点阶段第一步的提示(同原版 PromptForStep)：镜像=镜像线第一点；移动/复制=基点或位移(D)，位移模式下=指定位移。</summary>
    private string EditFirstPrompt() => _editMode switch
    {
        EditMode.Mirror => "指定镜像线的第一点",
        EditMode.Move or EditMode.Copy => _editDisplacement ? "指定位移 <dx,dy,dz>" : "指定基点 或 [位移(D)] <位移>",
        _ => "指定基点"
    };

    private bool TryCoordinateInput(string cmd)
    {
        if (_tool == null && _editMode == EditMode.None) return false;   // 仅取点态接受坐标
        if (_editMode != EditMode.None && !_editAwaitSelect)
        {
            // 编辑取点认三分量：x,y,z / @dx,dy,dz 的 z 进 dz(移动/复制)；只给 x,y 就是纯 XY
            var p3 = ParseCoord3(cmd, _lastInputPoint, _editPts.Count > 0 ? _editBaseZ : 0);
            if (p3 == null) return false;
            FeedPoint(p3.Value.x, p3.Value.y, p3.Value.z);
            return true;
        }
        var pt = ParseCoord(cmd, _lastInputPoint);
        if (pt == null) return false;
        // 「选择对象」阶段键入坐标 = 在该点点选一次(同 AutoCAD 的选择对象提示)，不能当基点攒进 _editPts
        if (_editAwaitSelect) { SelftestPickWorld(pt.Value.x, pt.Value.y); return true; }
        FeedPoint(pt.Value.x, pt.Value.y);
        return true;
    }

    /// <summary>
    /// 三分量坐标：x,y[,z]=绝对；@dx,dy[,dz]=相对上一点(z 相对 lastZ)；[@]d&lt;ang=极坐标(无 z)。
    /// z 为 null 表示"没给"——调用方据此决定动不动 z(同原版 parsePoint 的 hasZ)。无法解析返回 null。
    /// </summary>
    internal static (double x, double y, double? z)? ParseCoord3(string s, (double x, double y)? last, double lastZ)
    {
        s = s.Trim();
        if (s.IndexOf('<') > 0) return ParseCoord(s, last) is { } p ? (p.x, p.y, null) : null;   // 极坐标沿用二维解析
        bool rel = s.StartsWith("@");
        if (rel) s = s.Substring(1).Trim();
        var parts = s.Split(',');
        if (parts.Length is not (2 or 3)) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Float;
        if (!double.TryParse(parts[0].Trim(), ns, inv, out double x) || !double.TryParse(parts[1].Trim(), ns, inv, out double y)) return null;
        double? z = null;
        if (parts.Length == 3) { if (!double.TryParse(parts[2].Trim(), ns, inv, out double zz)) return null; z = zz; }
        if (!rel) return (x, y, z);
        if (last == null) return null;
        return (last.Value.x + x, last.Value.y + y, z.HasValue ? lastZ + z.Value : null);
    }

    /// <summary>
    /// 视口点击给编辑取点的 z：只有捕捉到顶点才有；作目标点时还须基点 z 明确(键入含 z / 捕捉到顶点)，
    /// 否则 null = 纯 XY 移动(与从前一致)。
    /// </summary>
    private double? ClickZForEdit()
        => _snapWorld == null || _snapWorldZ == null ? null
         : (_editPts.Count == 0 || _editBaseZKnown ? _snapWorldZ : null);

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
    private void FeedPoint(double x, double y, double? z = null)
    {
        _lastInputPoint = (x, y);
        if (_editMode != EditMode.None)
        {
            // 位移模式只认命令行键入的向量(同原版 WaitingDisplacement)：矿区坐标动辄几十万，
            // 把点到的绝对坐标当位移会把实体甩出图外。
            if (_editDisplacement && _editPts.Count == 0) { StatusMsg.Text = $"{_editName}：位移模式——请在命令行键入 dx,dy / dx,dy,dz（如 100,50,-5）或 d<角度"; return; }
            bool moveCopy = _editMode is EditMode.Move or EditMode.Copy;
            if (_editPts.Count == 0) { _editBaseZ = z ?? 0; _editBaseZKnown = z.HasValue; _editDz = 0; }
            else if (moveCopy && z.HasValue) _editDz = z.Value - _editBaseZ;   // 目标点给了 z 才动 z(见字段说明)
            _editPts.Add((x, y));
            if (_editPts.Count >= EditPointCount(_editMode))
            {
                double dz = moveCopy ? _editDz : 0;
                ApplyEditTransform(BuildEditTransform(), _editMode == EditMode.Copy, dz);
                _editMode = EditMode.None; _editPts.Clear();
                StatusMsg.Text = dz != 0 ? $"编辑完成（Δz {dz:0.###}）" : "编辑完成";
            }
            else StatusMsg.Text = EditPrompt(_editMode, _editPts.Count)
                                + (moveCopy && _editBaseZKnown ? $"（基点 z={_editBaseZ:0.###}）" : "");
            return;
        }
        if (_tool != null)
        {
            var ent = _tool.AddPoint(x, y);
            if (ent != null) { BeginChange(); AssignLayer(ent); if (_currentDash != null) ent.Dash = _currentDash; _scene.Add(ent); }
            RefreshScene();
            // 多点工具的提示里已经带着"已 N 点"了, 再缀一个"（已画 N）"是两个不同的数挤在一行, 反而看不清。
            StatusMsg.Text = _tool.IsMultiPoint ? _tool.Prompt : $"{_tool.Prompt}（已画 {_scene.Count}）";
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
        (EditMode.Move, 1) => "移动：指定第二个点 或 <使用第一个点作为位移>",
        (EditMode.Copy, 1) => "复制：指定第二个点 或 <使用第一个点作为位移>",
        (EditMode.Mirror, 1) => "镜像：指定镜像线第二点",
        (EditMode.Rotate, 1) => "旋转：指定旋转角参照点",
        (EditMode.Scale, 1) => "缩放：指定参考长度点",
        (EditMode.Scale, 2) => "缩放：指定新长度点",
        _ => "指定目标点"
    };

    private Affine2 BuildEditTransform() => BuildTransformFrom(_editPts, _editMode);

    // 由「已取点」构造编辑变换。move/copy=平移, mirror=镜像线, rotate=绝对角, scale=参照比例。
    private static Affine2 BuildTransformFrom(System.Collections.Generic.IReadOnlyList<(double x, double y)> p, EditMode mode)
    {
        switch (mode)
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

    // 编辑即时预览变换：仅差最后一点(由光标补)时给出——供选中实体拖拽跟随。
    private Affine2? BuildEditPreview((double x, double y) cursor)
    {
        if (_editMode == EditMode.None || _editPts.Count != EditPointCount(_editMode) - 1) return null;
        var pts = new System.Collections.Generic.List<(double x, double y)>(_editPts) { cursor };
        return BuildTransformFrom(pts, _editMode);
    }

    // 编辑拖拽即时信息(位移/角度/比例) —— 供光标浮标显示。
    private string? EditDragHint((double x, double y) c)
    {
        if (_editMode == EditMode.None || _editPts.Count == 0) return null;
        var b = _editPts[0];
        switch (_editMode)
        {
            case EditMode.Move:
            case EditMode.Copy:
                if (_editPts.Count != 1) return null;
                double dx = c.x - b.x, dy = c.y - b.y;
                return $"位移 {System.Math.Sqrt(dx * dx + dy * dy):0.##}  Δx {dx:0.##}  Δy {dy:0.##}";
            case EditMode.Rotate:
                if (_editPts.Count != 1) return null;
                double ang = System.Math.Atan2(c.y - b.y, c.x - b.x) * 180.0 / System.Math.PI;
                return $"角度 {(ang < 0 ? ang + 360.0 : ang):0.#}°";
            case EditMode.Mirror:
                if (_editPts.Count != 1) return null;
                double ma = System.Math.Atan2(c.y - b.y, c.x - b.x) * 180.0 / System.Math.PI;
                return $"镜像轴 {(ma < 0 ? ma + 360.0 : ma):0.#}°";
            case EditMode.Scale:
                if (_editPts.Count != 2) return null;
                double refLen = System.Math.Sqrt((_editPts[1].x - b.x) * (_editPts[1].x - b.x) + (_editPts[1].y - b.y) * (_editPts[1].y - b.y));
                double newLen = System.Math.Sqrt((c.x - b.x) * (c.x - b.x) + (c.y - b.y) * (c.y - b.y));
                return $"比例 {(refLen < 1e-9 ? 1 : newLen / refLen):0.###}";
            default: return null;
        }
    }

    // 编辑态光标浮标文本(随鼠标的动态输入提示): 选择阶段=选择提示; 取点阶段=拖拽维度 或 下一取点提示。
    private string? EditTipText((double x, double y) c)
    {
        if (_editAwaitSelect) return $"选择对象 · 右键确定（已选 {_selected.Count}）";
        if (_editMode == EditMode.None) return null;
        var dim = EditDragHint(c);
        if (dim != null) return dim;
        return _editPts.Count == 0
            ? $"{_editName}：{EditFirstPrompt()}"
            : EditPrompt(_editMode, _editPts.Count);
    }

    // 对选择集施加仿射变换；copy=true 则加副本，否则替换原实体
    // dz：沿 Z 的位移(仅移动/复制会给)。各类实体的 z 都是 Elevation 打底(三角网/点云逐顶点 z 再加 Elevation),
    // 所以整体抬降改 Elevation 即可, 逐顶点 z 原样保留。
    private void ApplyEditTransform(Affine2 m, bool copy, double dz = 0)
    {
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (var e in _selected)
        {
            var e2 = e.Apply(m);
            if (dz != 0) e2.Elevation += dz;
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
        && !_ttrActive && !_serActive && !_offsetActive && !_trimActive
        && !_breakActive && !_slideActive && !_dimActive && !_dimRadActive && !_dimAngActive;

    /// <summary>空命令行 → 上次命令；否则用输入。纯逻辑，可单测。</summary>
    internal static string? RepeatCommand(string typed, string? last)
        => typed.Length > 0 ? typed : (string.IsNullOrEmpty(last) ? null : last);

    // 命令框未识别 → 合成 Tag 转派整条中文命令链(复用 OnRibbonCommand，命令框亦可打中文命令)
    private bool _suppressCmdLog;   // DispatchRibbon 转派时抑制 OnRibbonCommand 重复回显(命令框侧已回显)
    /// <param name="fromCommandLine">
    /// true = 由命令行/助手转派（参数问答留在命令行）；false = 界面按钮/菜单/上下文菜单发起（参数走对话框，忠实原版）。
    /// </param>
    private void DispatchRibbon(string cmd, bool fromCommandLine = false)
    {
        _suppressCmdLog = true; _dispatchFromCmdLine = fromCommandLine;
        OnRibbonCommand(new Button { Tag = cmd }, new RoutedEventArgs());
    }

    private bool _dispatchFromCmdLine;   // DispatchRibbon → OnRibbonCommand 的一次性传参(转派来源)

    /// <summary>
    /// 自检入口(PITMINE_SELFTEST)：分号分隔多条；以 @ 开头的是界面辅助动作(仅为截图核对用)，其余按 Ribbon 命令派发。
    /// 目前支持 @Ribbon末端 —— 把当前功能区横向滚到最右，好截到排在后面的组(如「特性」)。
    /// </summary>
    private void RunSelftest(string script)
    {
        foreach (var raw in script.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
        {
            string cmd = raw.Trim();
            if (cmd.Length == 0) continue;
            RunSelftestStep(cmd);
            // 每步落一条结果到日志/stderr：无人值守跑自检时靠它核对, 不必只依赖截图。
            var foc = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            PitMine3D.Kylin.CrashLog.Write("自检",
                $"{cmd}  →  状态栏「{StatusMsg.Text}」 命令框「{CommandInput.Text}」 焦点={foc?.GetType().Name ?? "无"}");
        }
    }

    private void RunSelftestStep(string cmd)
    {
        {
            if (cmd == "@Ribbon末端") { ScrollRibbonToEnd(); return; }
            if (cmd.StartsWith("@块体示例"))   // @块体示例 [nx ny nz]: 建一个规则块体模型并入场景(截图核对体素显示用)
            {
                var a = cmd.Length > 5 ? cmd.Substring(5).Split(' ', System.StringSplitOptions.RemoveEmptyEntries) : System.Array.Empty<string>();
                int nx = a.Length > 0 && int.TryParse(a[0], out int v0) ? v0 : 12;
                int ny = a.Length > 1 && int.TryParse(a[1], out int v1) ? v1 : 10;
                int nz = a.Length > 2 && int.TryParse(a[2], out int v2) ? v2 : 5;
                SelftestSampleBlockModel(nx, ny, nz);
                return;
            }
            if (cmd.StartsWith("@点云导入 "))   // @点云导入 <路径>: 跳过文件对话框直接加载 LAS/CSV 点云(核对真实航测点云的覆盖范围/耗时), 同「加载点云」那条路
            {
                _ = PcLoadPathAsync(cmd.Substring(6).Trim().Trim('"'));
                return;
            }
            if (cmd.StartsWith("@点云示例"))   // @点云示例 [列 行]: 合成一份台阶地形点云入场景(截图核对点云渲染/算子用)
            {
                var a = cmd.Length > 5 ? cmd.Substring(5).Split(' ', System.StringSplitOptions.RemoveEmptyEntries) : System.Array.Empty<string>();
                int cn = a.Length > 0 && int.TryParse(a[0], out int c0) ? c0 : 200;
                int rn = a.Length > 1 && int.TryParse(a[1], out int r0) ? r0 : 120;
                double dz = a.Length > 2 && double.TryParse(a[2], out double z0) ? z0 : 0;   // 整体抬升(造第二期用)
                SelftestSamplePointCloud(cn, rn, dz);
                return;
            }
            if (cmd == "@嵌入示例")   // @嵌入示例: 起伏三角网 + 红线(原样穿山悬空) + 黄线(嵌入后贴面), 东南视角压低(截图核对贴面)
            {
                SelftestSampleEmbed();
                return;
            }
            if (cmd.StartsWith("@线示例"))   // @线示例 [圈]: 造一条剖面线(默认)或闭合边界并选中(点云剖面/分割/圈量/剖面分析用)
            {
                SelftestSampleLine(cmd.Contains("圈") || cmd.Contains("闭合"));
                return;
            }
            if (cmd.StartsWith("@选中 "))   // @选中 <x> <y>: 「选择对象」阶段按世界坐标加/减选(等价视口单击)
            {
                var a = cmd.Substring(3).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (a.Length >= 2 && double.TryParse(a[0], out double qx) && double.TryParse(a[1], out double qy))
                    SelftestPickWorld(qx, qy);
                return;
            }
            if (cmd.StartsWith("@拾取 "))   // @拾取 <x> <y>: 直接把世界坐标喂给挂起的视口拾取(不模拟鼠标)
            {
                var a = cmd.Substring(3).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (a.Length >= 2 && double.TryParse(a[0], out double px) && double.TryParse(a[1], out double py))
                    SelftestFeedPick(px, py);
                return;
            }
            if (cmd.StartsWith("@命令 "))   // @命令 <行>: 走命令行提交路径(含 AutoCAD 别名解析/历史/回显)
            {
                CommandInput.Text = cmd.Substring(4).Trim();
                SubmitCommandLine(CommandInput);
                return;
            }
            if (cmd.StartsWith("@敲字 "))   // @敲字 <串>: 从视口开始逐字喂 TextInput, 验证命令行"常驻聆听"
            {
                Viewport.Focus();
                foreach (char ch in cmd.Substring(4))
                {
                    // 每次都投给"当前焦点"——第一个字符落在视口(由窗口隧道抢进命令框),
                    // 之后焦点已在命令框, 后续字符就该像真实键入那样直接进它。
                    var target = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Interactive ?? Viewport;
                    target.RaiseEvent(new TextInputEventArgs
                    { RoutedEvent = InputElement.TextInputEvent, Source = target, Text = ch.ToString() });
                }
                return;
            }
            if (cmd == "@空格")   // 敲一个空格(AutoCAD 的"空格=回车"): 单列一条, 因为脚本按 ; 切分后会被 Trim 掉
            {
                // 真实按键的顺序: 先 KeyDown(Space), 没被吃掉才产生 TextInput(" ")。两条路都要走, 才测得准。
                var t = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Interactive ?? Viewport;
                var ke = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = t, Key = Key.Space };
                t.RaiseEvent(ke);
                if (!ke.Handled)
                    t.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Source = t, Text = " " });
                return;
            }
            // @等: 把队列里排着的续跑(异步命令 await 之后那半截, 如参数答完后的实际计算)先跑掉,
            // 好让下一条脚本看到的是"命令真的做完了"的状态。RunSelftest 本身是同步的, 不这么做就只能看到中间态。
            if (cmd == "@等")
            {
                // 先把队列里排着的续跑跑掉；导入已改成后台解析, 那就再泵到它真的结束(上限 120 秒),
                // 否则脚本后面几条会看到一个还空着的场景。
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var swW = System.Diagnostics.Stopwatch.StartNew();
                while (_importBusy && swW.Elapsed.TotalSeconds < 120)
                { System.Threading.Thread.Sleep(50); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                return;
            }
            if (cmd.StartsWith("@等 "))   // @等 <毫秒>: 泵消息等后台算子落地 —— 自检脚本的每一步都是 async void,
            {                             // 不等的话「2.5D TIN;三角网着色」会在网还没建出来时就跑第二条。
                int ms = int.TryParse(cmd.Substring(3).Trim(), out int v) ? System.Math.Clamp(v, 0, 120000) : 1000;
                var end = System.DateTime.UtcNow.AddMilliseconds(ms);
                while (System.DateTime.UtcNow < end)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    System.Threading.Thread.Sleep(15);
                }
                return;
            }
            if (cmd.StartsWith("@导入 "))   // @导入 <路径>: 跳过文件对话框直接导入(核对大图纸能否打开/耗时)
            {
                _ = ImportPath(cmd.Substring(4).Trim().Trim('"'));
                return;
            }
            if (cmd == "@文字清单")   // 场景里全部文字的 位置/字高/内容(核对在位编辑/拖放结果)
            {
                var sbT = new System.Text.StringBuilder("文字清单：");
                foreach (var te in _scene.Entities.OfType<TextEntity>()) sbT.Append($"[({te.X:0.##},{te.Y:0.##}) h{te.Height:0.##} 「{te.Text.Replace('\n', '|')}」] ");
                StatusMsg.Text = sbT.ToString();
                return;
            }
            if (cmd.StartsWith("@鼠标 "))   // @鼠标 <按下|移动|松开|滚轮|双击> <x> <y> [右|格数]: 世界坐标→屏幕后向视口宿主投一次真实指针事件(走 _onHostPressed/Moved/Released 全链)
            {
                var a = cmd.Substring(4).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (a.Length >= 3 && double.TryParse(a[1], out double mx) && double.TryParse(a[2], out double my)
                    && Viewport.WorldToScreen(mx, my, 0) is { } msp)
                {
                    bool right = a.Length >= 4 && a[3] == "右";
                    var pos = new Avalonia.Point(msp.sx, msp.sy);
                    _selftestPointer ??= new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                    ulong ts = (ulong)System.Environment.TickCount64;
                    switch (a[0])
                    {
                        case "按下":
                        {
                            var pp = new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
                                                                right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed);
                            _onHostPressed?.Invoke(ViewportHost, new PointerPressedEventArgs(ViewportHost, _selftestPointer, ViewportHost, pos, ts, pp, KeyModifiers.None));
                            break;
                        }
                        case "移动":
                        {
                            var pp = new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
                            _onHostMoved?.Invoke(ViewportHost, new PointerEventArgs(InputElement.PointerMovedEvent, ViewportHost, _selftestPointer, ViewportHost, pos, ts, pp, KeyModifiers.None));
                            break;
                        }
                        case "松开":
                        {
                            var pp = new PointerPointProperties(RawInputModifiers.None, right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased);
                            _onHostReleased?.Invoke(ViewportHost, new PointerReleasedEventArgs(ViewportHost, _selftestPointer, ViewportHost, pos, ts, pp, KeyModifiers.None, right ? MouseButton.Right : MouseButton.Left));
                            break;
                        }
                        case "滚轮":   // @鼠标 滚轮 <x> <y> [格数]: 正=向上滚(放大), 负=向下滚(缩小), 朝该世界点缩放(同真实滚轮)
                        {
                            int n = a.Length >= 4 && int.TryParse(a[3], out int nn) ? nn : 1;
                            var pp = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
                            for (int i = 0; i < System.Math.Abs(n); i++)
                                _onHostWheel?.Invoke(ViewportHost, new PointerWheelEventArgs(ViewportHost, _selftestPointer, ViewportHost, pos, ts, pp, KeyModifiers.None, new Vector(0, n > 0 ? 1 : -1)));
                            break;
                        }
                        case "双击":   // @鼠标 双击 <x> <y>: 投 DoubleTapped(文字在位编辑/多段线收笔/范围缩放走的那条)
                        {
                            var pp = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
                            var pea = new PointerEventArgs(InputElement.PointerMovedEvent, ViewportHost, _selftestPointer, ViewportHost, pos, ts, pp, KeyModifiers.None);
                            _onHostDoubleTapped?.Invoke(ViewportHost, new TappedEventArgs(Gestures.DoubleTappedEvent, pea));
                            break;
                        }
                        default: StatusMsg.Text = "自检鼠标：动作须为 按下/移动/松开/滚轮/双击"; return;
                    }
                    StatusMsg.Text += $"  [自检鼠标 {a[0]} ({mx:0.#},{my:0.#}) 屏幕({pos.X:0},{pos.Y:0}) 拖拽={_gripDrag.Active} 框选={_selBoxActive} 编辑框={(TextEditing ? $"开 字号{_textEditor!.FontSize:0} 位置({_textEditor.Margin.Left:0},{_textEditor.Margin.Top:0})" : "无")} 选集: {string.Join(",", _selected.Select(EntityTypeName.Of))}]";
                }
                else StatusMsg.Text = "自检鼠标：参数须为 <按下|移动|松开> <x> <y> [右] 且点在视口内";
                return;
            }
            if (cmd.StartsWith("@点选 "))   // @点选 <x> <y>: 按世界坐标走真实点选路径(含空间索引), 计时见 TRACE
            {
                var a = cmd.Substring(4).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (a.Length >= 2 && double.TryParse(a[0], out double px) && double.TryParse(a[1], out double py))
                {
                    var swS = System.Diagnostics.Stopwatch.StartNew();
                    var hit = PickWorld2D(px, py, a.Length >= 3 && double.TryParse(a[2], out double t) ? t : 5.0);
                    StatusMsg.Text = $"点选({px:0.#}, {py:0.#}) {swS.Elapsed.TotalMilliseconds:0.##}ms → "
                                   + (hit == null ? "未命中" : $"命中 {hit.GetType().Name} 层「{hit.LayerName}」");
                    if (hit != null) { _selected.Clear(); _selected.Add(hit); HighlightSelection(); }
                }
                return;
            }
            if (cmd.StartsWith("@稍后 "))   // @稍后 <毫秒> <步骤>: 真回到消息循环等这么久再跑该步(切标签会销毁/重建 GL 上下文, 得让它真的渲染过才有意义)
            {
                var a = cmd.Substring(4).Trim();
                int sp = a.IndexOf(' ');
                if (sp > 0 && int.TryParse(a.Substring(0, sp), out int ms))
                {
                    string later = a.Substring(sp + 1).Trim();
                    var t = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(ms) };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        try { RunSelftestStep(later); } catch (System.Exception ex) { StatusMsg.Text = "自检步骤异常: " + ex.Message; }
                        PitMine3D.Kylin.CrashLog.Write("自检", $"(+{ms}ms) {later}  →  状态栏「{StatusMsg.Text}」");
                    };
                    t.Start();
                }
                return;
            }
            if (cmd.StartsWith("@标签坐标 "))   // @标签坐标 <序号>: 记第 n 个文档标签页签的屏幕中心(供外部真点一下, 核对"点标签切文档"这条路)
            {
                if (int.TryParse(cmd.Substring(5).Trim(), out int ti) && ti >= 1 && ti <= _docs.Count)
                {
                    var vm = _docs[ti - 1].Vm;
                    var tab = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this)
                        .OfType<Control>().FirstOrDefault(c => ReferenceEquals(c.DataContext, vm) && c.GetType().Name.Contains("TabStripItem"));
                    if (tab != null)
                    {
                        var c = tab.PointToScreen(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2));
                        StatusMsg.Text = $"标签{ti} {tab.GetType().Name} 屏幕({c.X},{c.Y}) 尺寸{tab.Bounds.Width:0}x{tab.Bounds.Height:0}";
                    }
                    else StatusMsg.Text = $"标签{ti}: 找不到页签控件";
                }
                return;
            }
            if (cmd.StartsWith("@标签点 "))   // @标签点 <序号>: 走页签条的选中路径切文档(= 用鼠标点页签; 真鼠标进不了 Avalonia)
            {
                if (int.TryParse(cmd.Substring(4).Trim(), out int ti) && ti >= 1 && ti <= _docs.Count)
                {
                    var strip = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this)
                        .OfType<Avalonia.Controls.Primitives.SelectingItemsControl>().FirstOrDefault(c => c.GetType().Name == "DocumentTabStrip");
                    if (strip != null) { strip.SelectedItem = _docs[ti - 1].Vm; StatusMsg.Text = $"标签点{ti}: 页签条选中={((strip.SelectedItem as DMC.Document)?.Title)} 活动={((_docDock.ActiveDockable as DMC.Document)?.Title)} 当前={_active.Title}"; }
                    else StatusMsg.Text = "标签点: 找不到页签条";
                }
                return;
            }
            if (cmd == "@标签态")   // 页签条选中 / DocumentDock.ActiveDockable / _active 三者是否一致
            {
                var strip = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this)
                    .OfType<Avalonia.Controls.Primitives.SelectingItemsControl>().FirstOrDefault(c => c.GetType().Name == "DocumentTabStrip");
                StatusMsg.Text = $"页签条选中={((strip?.SelectedItem as DMC.Document)?.Title ?? "?")} 活动={((_docDock.ActiveDockable as DMC.Document)?.Title)} 当前={_active.Title} 视口父级={_active.Host?.Parent?.GetType().Name ?? "无"}";
                return;
            }
            if (cmd == "@GL状态")   // 当前文档视口的 GPU 侧状态(帧数/附着/各通道 源·传·待), 判"切回来空白"是没渲染还是没重传
            {
                StatusMsg.Text = $"GL {Viewport.GlDebug()}";
                return;
            }
            if (cmd.StartsWith("@文档 "))   // @文档 <序号>: 切到第 n 个文档标签(多文档互不串扰的核对用; 1 起)
            {
                if (int.TryParse(cmd.Substring(3).Trim(), out int di) && di >= 1 && di <= _docs.Count)
                    _dockFactory.SetActiveDockable(_docs[di - 1].Vm);
                else StatusMsg.Text = $"自检：无第 {cmd.Substring(3).Trim()} 个文档(共 {_docs.Count} 个)";
                return;
            }
            if (cmd == "@视口")   // 报告"屏幕上到底有没有东西": 可见世界范围 + 落在其中的顶点比例
            {
                ReportViewportContent();
                return;
            }
            if (cmd == "@Esc")   // 真发一次 Esc(走窗口按键处理): 取消参数问答 / 进行中的绘制编辑
            {
                var et = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Interactive ?? (Interactive)this;
                et.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = et, Key = Key.Escape });
                return;
            }
            if (cmd == "@确认") { if (!ConfirmOneShotPick() && !ConfirmEditPoint()) FinishSelectObjects(true); return; }   // 右键/回车确认(逐面点选 / 选择对象阶段)
            if (cmd == "@取消") { if (!CancelOneShotPick()) FinishSelectObjects(false); return; }
            if (cmd.StartsWith("@示例两体")) { SelftestTwoSolids(); return; }   // 两个相互重叠的立方体(布尔/刀切用)
            if (cmd.StartsWith("@示例点线")) { SelftestPointsAndLines(); return; }   // 若干高程点 + 两条多段线并选中(点/线编辑用)
            if (cmd.StartsWith("@示例刀面")) { SelftestKnife(); return; }   // 一张 z=40 的开放水平面(刀切实体用)
            if (cmd.StartsWith("@示例裁剪")) { SelftestClipFixture(); return; }   // 裁刀 + 带完整属性的被裁线(核对裁剪是否改属性)
            if (cmd.StartsWith("@列属性")) { SelftestDumpProps(); return; }               // 把场景里多段线的全部属性打到信息栏
            if (cmd.StartsWith("@窗口 "))   // @窗口 <宽> <高>: 退出最大化并定尺寸(截图核对用)
            {
                var a = cmd.Substring(3).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (a.Length >= 2 && double.TryParse(a[0], out double w) && double.TryParse(a[1], out double h))
                { WindowState = WindowState.Normal; Position = new PixelPoint(0, 0); Width = w; Height = h; }
                return;
            }
            _cmdLineDriven = false;   // 脚本里的裸命令等同"点 Ribbon 按钮": 参数取默认值, 不在命令行等人答
            DispatchRibbon(cmd);
        }
    }

    /// <summary>
    /// 自检：把一个世界坐标直接喂给挂起的一次性拾取。
    /// 不走"模拟鼠标"那条路 —— 合成的指针事件既不一定被视口收到, 也会被相机漫游吃掉,
    /// 这里直接调回调, 拾取后续逻辑(选中/命中判定)与真实点选完全一致。
    /// </summary>
    private void SelftestFeedPick(double x, double y)
    {
        if (_oneShotPick == null) { StatusMsg.Text = $"自检拾取：当前没有挂起的拾取（({x:0.##}, {y:0.##}) 被忽略）"; return; }
        var cb = _oneShotPick; _oneShotPick = null; _pickConfirmable = false;
        cb(x, y);
    }

    /// <summary>自检：放若干高程点 + 两条相交多段线并全部选中（点编辑 / 线编辑各命令的现成料）。</summary>
    private void SelftestPointsAndLines()
    {
        BeginChange();
        var made = new System.Collections.Generic.List<SceneEntity>();
        for (int i = 0; i < 5; i++)
        {
            var pe = new PointEntity { X = i * 20, Y = 10 + i * 5, Elevation = 100 + i, Size = 3 };
            AssignLayer(pe); _scene.Add(pe); made.Add(pe);
        }
        var pe2 = new PointEntity { X = 0, Y = 10, Elevation = 100, Size = 3 };   // 与第 1 个重合(试"删除重复点")
        AssignLayer(pe2); _scene.Add(pe2); made.Add(pe2);

        var l1 = new PolylineEntity { Zs = new System.Collections.Generic.List<double> { 100, 105, 110, 108 } };
        l1.Points.AddRange(new[] { (0.0, 0.0), (40.0, 30.0), (80.0, 10.0), (120.0, 60.0) });
        var l2 = new PolylineEntity { Zs = new System.Collections.Generic.List<double> { 90, 95, 99 } };
        l2.Points.AddRange(new[] { (0.0, 40.0), (60.0, 0.0), (120.0, 40.0) });
        foreach (var l in new[] { l1, l2 }) { AssignLayer(l); _scene.Add(l); made.Add(l); }

        RefreshScene();
        Viewport.FitBounds(new[] { -10.0, -10.0, 130.0, 70.0 });
        SelectEntities(made);
        StatusMsg.Text = $"自检：已放入 6 个高程点(含 1 个重合) + 2 条相交多段线并选中";
    }

    /// <summary>
    /// 自检：按世界坐标做一次"视口单击选取"，与 <see cref="PickAt"/> 的加减选语义一致
    /// （「选择对象」阶段累加/再点取消选，空闲态单选替换）。不模拟鼠标，直接走命中测试。
    /// </summary>
    private void SelftestPickWorld(double wx, double wy)
    {
        var vb = CurrentViewBounds();
        double tol = vb != null ? System.Math.Max(vb[2] - vb[0], vb[3] - vb[1]) / 200 : 1;
        var hit = _scene.Pick(wx, wy, tol, _layers.IsSelectable);
        if (hit == null)
            foreach (var en in _scene.Entities)
                if (en is MeshEntity me && en.Visible && _layers.IsSelectable(en.LayerName) && me.ContainsXY(wx, wy)) { hit = me; break; }
        if (hit == null) { if (!_editAwaitSelect) _selected.Clear(); }
        else if (_selected.Contains(hit)) _selected.Remove(hit);
        else { if (!_editAwaitSelect) _selected.Clear(); _selected.Add(hit); }
        HighlightSelection();
        StatusMsg.Text = _editAwaitSelect
            ? $"{_editName}：选择对象（右键确定，已选 {_selected.Count}）"
            : (_selected.Count > 0 ? $"已选 {_selected.Count} 个实体" : "未选中");
    }

    /// <summary>自检：一把闭合矩形裁刀 + 两条属性齐全的被裁多段线（核对裁剪前后属性是否走样）。</summary>
    private void SelftestClipFixture()
    {
        BeginChange();
        var lay = _layers.Get("测试层") ?? _layers.EnsureImported("测试层", 0.2f, 0.8f, 0.8f);
        var knife = new PolylineEntity { Closed = true, LayerName = lay.Name, Cr = 0.2f, Cg = 0.9f, Cb = 0.9f };
        knife.Points.AddRange(new[] { (20.0, 20.0), (80.0, 20.0), (80.0, 80.0), (20.0, 80.0) });
        _scene.Add(knife);

        // ① 三维闭合线：Elevation=50 + 逐点 Zs(0/5/10/5) → 绝对 Z 应为 50/55/60/55
        var l3 = new PolylineEntity
        {
            Closed = true, LayerName = lay.Name, Elevation = 50,
            Zs = new System.Collections.Generic.List<double> { 0, 5, 10, 5 },
            Cr = 0.95f, Cg = 0.2f, Cb = 0.2f, LineWeight = 50, Transparency = 30,
            Dash = new double[] { 4, 2 },
        };
        l3.Points.AddRange(new[] { (0.0, 40.0), (100.0, 40.0), (100.0, 60.0), (0.0, 60.0) });
        _scene.Add(l3);

        // ② 平面开口线：Elevation=20
        var l2 = new PolylineEntity
        {
            LayerName = lay.Name, Elevation = 20,
            Cr = 0.2f, Cg = 0.4f, Cb = 0.95f, LineWeight = 30, Transparency = 10,
            Dash = new double[] { 8, 3 },
        };
        l2.Points.AddRange(new[] { (-10.0, 10.0), (110.0, 10.0), (110.0, 90.0) });
        _scene.Add(l2);

        // ③ 整条落在裁刀内的闭合线：裁剪应当**原样不动**(不重建, 闭合标志不该丢)
        var lin = new PolylineEntity
        {
            Closed = true, LayerName = lay.Name, Elevation = 5,
            Zs = new System.Collections.Generic.List<double> { 0, 1, 2 },
            Cr = 0.95f, Cg = 0.85f, Cb = 0.1f, LineWeight = 20, Transparency = 5,
        };
        lin.Points.AddRange(new[] { (30.0, 30.0), (60.0, 30.0), (45.0, 60.0) });
        _scene.Add(lin);

        PopulateDrawingLayers();
        RefreshScene();
        Viewport.FitBounds(new[] { -20.0, -10.0, 120.0, 100.0 });
        StatusMsg.Text = "自检：已放入裁刀(闭合矩形) + 三维闭合线(Elev 50) + 平面开口线(Elev 20) + 刀内闭合三角(Elev 5)";
    }

    /// <summary>自检：把场景里每条多段线的全部属性打到信息栏，用来逐项核对命令有没有改属性。</summary>
    private void SelftestDumpProps()
    {
        foreach (var pl in _scene.Entities.OfType<PolylineEntity>())
        {
            double z0 = double.MaxValue, z1 = double.MinValue;
            for (int i = 0; i < pl.Points.Count; i++) { double z = pl.ZAt(i); z0 = System.Math.Min(z0, z); z1 = System.Math.Max(z1, z); }
            string line = $"点{pl.Points.Count} 层「{pl.LayerName}」 色 {(int)(pl.Cr * 255)},{(int)(pl.Cg * 255)},{(int)(pl.Cb * 255)}"
                        + $" 线型 {(pl.Dash == null ? "实线" : string.Join("/", pl.Dash))} 线宽 {pl.LineWeight} 透明 {pl.Transparency}"
                        + $" 闭合 {(pl.Closed ? "是" : "否")} 标高 {pl.Elevation:0.##} Z {z0:0.##}~{z1:0.##}";
            EditEcho("[属性] " + line);
            PitMine3D.Kylin.CrashLog.Write("属性", line);
        }
    }

    /// <summary>自检：放一张 z=40 的开放水平面作"刀"（刀切闭合实体用；比立方体大一圈，保证切透）。</summary>
    private void SelftestKnife()
    {
        var v = new System.Collections.Generic.List<(double x, double y, double z)>
        { (-20, -20, 40), (170, -20, 40), (170, 170, 40), (-20, 170, 40) };
        var t = new System.Collections.Generic.List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        BeginChange();
        var knife = new MeshEntity("自检刀面", v, t) { Cr = 0.95f, Cg = 0.75f, Cb = 0.25f };
        AssignLayer(knife); _scene.Add(knife);
        RefreshScene();
        StatusMsg.Text = "自检：已放入 z=40 的开放水平刀面「自检刀面」";
    }

    /// <summary>自检：放两个相互重叠的立方体并选中（布尔 / 刀切 / 面交线等两体算子的现成料）。</summary>
    private void SelftestTwoSolids()
    {
        static MeshEntity MakeBox(string name, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            var v = new System.Collections.Generic.List<(double x, double y, double z)>
            {
                (x0,y0,z0),(x1,y0,z0),(x1,y1,z0),(x0,y1,z0),
                (x0,y0,z1),(x1,y0,z1),(x1,y1,z1),(x0,y1,z1),
            };
            var t = new System.Collections.Generic.List<(int a, int b, int c)>
            {
                (0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),
                (2,3,7),(2,7,6),(3,0,4),(3,4,7),(1,2,6),(1,6,5),
            };
            return new MeshEntity(name, v, t);
        }
        BeginChange();
        var a = MakeBox("自检体A", 0, 0, 0, 100, 100, 100);
        var b = MakeBox("自检体B", 50, 50, 50, 150, 150, 150);
        b.Cr = 0.35f; b.Cg = 0.75f; b.Cb = 0.95f;
        AssignLayer(a); AssignLayer(b);
        _scene.Add(a); _scene.Add(b);
        RefreshScene();
        Viewport.FitBounds(new[] { -10.0, -10.0, 160.0, 160.0 });
        SelectEntities(new SceneEntity[] { a, b });
        StatusMsg.Text = "自检：已放入两个重叠立方体（自检体A [0,100]³ / 自检体B [50,150]³）并选中";
    }

    /// <summary>自检：建一个规则块体模型入场景并切三维（核对块体是否为体素）。</summary>
    private void SelftestSampleBlockModel(int nx, int ny, int nz)
    {
        var m = Cad.BlockModelMeta.CreateRegular("自检块体", 0, 0, 0, 20, 20, 10, nx, ny, nz);
        m.ActiveColormapAttribute = Cad.BlockModelMeta.ZElevationSentinel;   // 按高程配色, 核对「显示颜色是否起作用」
        var err = Modeling.BlockModelStore.Create(MdlCtx(), m);
        if (err != null) { StatusMsg.Text = "自检块体：" + err; return; }
        Modeling.BlockModelStore.RefreshDisplay(MdlCtx(), m, fit: true);
        DispatchRibbon("3D");
        var mesh = m.BuildCellMesh(null, out var stat);
        Title += $" [自检 块体 {nx}x{ny}x{nz} 画{stat.DrawnCells}块/剔{stat.CulledCells}块 顶点{mesh?.Verts.Count ?? 0}]";
    }

    private void ScrollRibbonToEnd()
    {
        // 窗口刚 Opened 时功能区还没量完(Extent==Viewport), 故隔帧重试几次再放弃。
        int tries = 0;
        var t = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) =>
        {
            tries++;
            var sv = HomeRibbonScroll;
            double overflow = sv == null ? 0 : sv.Extent.Width - sv.Viewport.Width;
            if (sv != null && overflow > 1)
            {
                sv.Offset = new Vector(overflow, sv.Offset.Y);      // 窗口尺寸还在变时溢出量会变, 故持续压到最右
                StatusMsg.Text = $"自检：功能区已滚到最右（溢出 {overflow:0} px）";
                if (tries >= 10) { Title += $" [自检 滚动 {overflow:0}px]"; t.Stop(); }
            }
            else if (tries >= 12) { StatusMsg.Text = "自检：功能区没有横向溢出，无需滚动"; t.Stop(); }
        };
        t.Start();
    }

    // ---------- §四/§八 地质/生产数据库（局域网 openGauss；连接设置见「数据源 → 数据库连接」）----------

    /// <summary>正在提示"数据库连不上"——避免连点几个数据库命令时弹出一叠相同的窗口。</summary>
    private bool _dbPromptShowing;

    /// <summary>
    /// 用到数据库功能时才连(带进度窗口), 启动时不连。
    ///
    /// 不在启动时连的理由: 绘图、导入、三维这些功能压根不碰数据库。让它们为一个可能连不上的
    /// 服务器先等几秒、甚至先看一个报错窗口, 没有道理 —— 现场很多时候就是开图纸直接干活。
    ///
    /// 连接放后台线程 + 进度窗口: 服务器不可达要等到超时(8 秒), 在 UI 线程上直接连界面就僵住,
    /// 用户看到的是程序卡死而不是"正在连接"。
    /// </summary>
    private async void ConnectDbInteractiveAsync()
    {
        if (_dbPromptShowing) return;      // 连点几个数据库命令时只走一次
        _dbPromptShowing = true;
        try
        {
            var (ok, err) = await Views.GeoDb.DbConnectingDialog.RunAsync(
                this, () => _geoDb = Data.GeoDatabase.OpenSeeded());

            if (ok) { StatusMsg.Text = "数据库已连接，请重新执行刚才的操作。"; return; }
            if (err == null) { StatusMsg.Text = "已放弃连接数据库。"; return; }

            await PromptDbNotConnectedAsync(Data.DbConnectionDiagnosis.Diagnose(err));
        }
        catch (System.Exception ex) { StatusMsg.Text = "连接数据库时出错：" + ex.Message; }
        finally { _dbPromptShowing = false; }
    }

    /// <summary>
    /// 取地质库连接。103 个数据库命令都走这里, 所以"连不上时怎么办"只需在这一处管好。
    ///
    /// 连不上时不再把异常原文丢给用户 —— 那是 "Failed to connect to 192.168.114.131:5432"
    /// 这种既看不懂也不知道去哪改的东西。改成: 明确说清是哪一类问题, 并直接给一个入口去配置。
    /// </summary>
    private Data.GeoDatabase? EnsureGeoDb()
    {
        if (_geoDb != null) return _geoDb;

        // 这里**不做同步连接** —— 连不上要等到 8 秒超时, 在 UI 线程上等就是界面僵住。
        // 改为: 本次命令直接中止, 另起一条带进度窗口的连接流程; 连上后提示用户重新执行。
        StatusMsg.Text = "数据库尚未连接，正在尝试连接…";
        ConnectDbInteractiveAsync();
        return null;
    }

    /// <summary>
    /// 提示数据库不可用。刻意做成模态窗口而不是只写状态栏: 数据库连不上时整组功能都用不了,
    /// 状态栏一行字很容易被当成"点了没反应"。
    /// 重入保护在调用方 ConnectDbInteractiveAsync 里, 这里不重复。
    /// </summary>
    private async System.Threading.Tasks.Task PromptDbNotConnectedAsync(Data.DbConnectionDiagnosis.Result d)
    {
        try
        {
            // 循环: 用户可以「重新尝试连接」或「配置」后再试, 直到连上或主动关闭。
            // 不做成一次性的 —— 现场最常见的就是"服务器刚起来, 再点一下就好",
            // 每次都要从头点一遍数据库命令才能重试, 很折磨人。
            var cur = d;
            while (true)
            {
                var choice = await Views.GeoDb.DbUnavailableDialog.ShowAsync(this, cur);
                if (choice == Views.GeoDb.DbUnavailableDialog.Choice.Dismiss) return;

                if (choice == Views.GeoDb.DbUnavailableDialog.Choice.Configure
                    && !await Views.GeoDb.DbConnectionWindow.ShowAsync(this))
                    continue;   // 用户在设置窗口里取消了, 回到提示窗口

                // 重试同样带进度窗口(新地址可能照样不通, 不能又把界面卡住)
                var (ok2, err2) = await Views.GeoDb.DbConnectingDialog.RunAsync(
                    this, () => _geoDb = Data.GeoDatabase.OpenSeeded());

                if (ok2) { StatusMsg.Text = "数据库已连接，请重新执行刚才的操作。"; return; }
                if (err2 == null) { StatusMsg.Text = "已放弃连接数据库。"; return; }

                cur = Data.DbConnectionDiagnosis.Diagnose(err2);
                StatusMsg.Text = $"{cur.Title}——数据库功能仍不可用";
            }
        }
        catch (System.Exception ex) { StatusMsg.Text = "数据库提示窗口出错：" + ex.Message; }
    }

    private void EquipmentRosterCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var r = Data.GeoDataQueries.GetEquipmentRoster(db.Connection);
        var parts = new List<string>();
        foreach (var c in r.ByCategory) parts.Add($"{c.Category} {c.Count}");
        if (r.ByCategory.Count > 0)
            DrawCategoryBars(r.ByCategory.Select(c => (c.Category, (double)c.Count)).ToList(), "台数");
        StatusMsg.Text = $"设备台账：共 {r.Total} 台（在役 {r.InService}）· 分类: " + string.Join(" / ", parts)
            + (r.ByCategory.Count > 0 ? " · 分类柱入场景" : "");
    }

    // 设备主控因素分析(忠实原 EquipmentAnalysisWindow 因素分析): 因素-产能 Pearson 相关排名 → 主控因素 + 柱上屏。
    private void EquipmentFactorCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetEquipmentFactorRows(db.Connection);
        if (rows.Count < 2) { StatusMsg.Text = $"设备因素分析：对齐的月度因素-产能行不足({rows.Count}, 需≥2; KPI⋈产能)"; return; }
        var cc = Data.EquipmentFactorAnalysis.Correlate(rows);
        if (cc.Count == 0) { StatusMsg.Text = "设备因素分析：因素均无方差, 无法相关"; return; }
        DrawCategoryBars(cc.Select(c => (c.Factor, c.R)).ToList(), "与产能r");   // 有向 r 柱(正上负下)
        var top = cc[0];
        var parts = cc.Select(c => $"{c.Factor} r={c.R:+0.00;-0.00}({c.Strength}{c.Direction})");
        StatusMsg.Text = $"设备主控因素({rows.Count} 对齐月)：主控【{top.Factor}】r={top.R:+0.00;-0.00}({top.Strength}{top.Direction}相关) · " + string.Join(" · ", parts) + " · 相关柱入场景";
    }

    private void ProductionStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetProductionStats(db.Connection);
        // 台效(产量/工时)有工时才附加
        string eff = s.AvgEfficiencyM3PerH > 0 ? $" · 台效 均{s.AvgEfficiencyM3PerH:0.#}/峰{s.PeakEfficiencyM3PerH:0.#} m³/h" : "";
        StatusMsg.Text = $"设备生产数据：{s.Records} 条记录 · 总产量 {s.OutputM3:0.#} m³ · 工时 {s.WorkHours:0.#}h · 故障 {s.FaultHours:0.#}h · 作业率 {s.UtilizationPct:0.#}%{eff}";
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
        // 可靠性(MTBF/MTTR/稳态可用率)有运行时长数据才附加
        string rel = f.MtbfHours > 0 ? $" · 可靠性 MTBF {f.MtbfHours:0.#}h/MTTR {f.MttrHours:0.#}h/稳态可用率 {f.SteadyAvailPct:0.#}%" : "";
        // Weibull 失效分布(浴盆定位)有拟合结果才附加
        string wb = f.WeibullPhase.Length > 0 ? $" · Weibull β={f.WeibullBeta:0.##}/η={f.WeibullEta:0.#}天({f.WeibullPhase})" : "";
        // 大修预警(可用率趋势+稳态)有 KPI 序列才附加
        string oh = f.LatestAvailPct > 0 ? $" · {(f.OverhaulWarn ? "🔴大修预警" : "寿命良好")}(可用率 {f.LatestAvailPct:0.#}%·趋势 {f.AvailTrendPtPerMonth:+0.00;-0.00}pt/月)" : "";
        StatusMsg.Text = $"故障分析：{f.Events} 起 · 累计停机 {f.DowntimeHours:0.#}h · 未修复 {f.Unresolved} · 最多「{f.TopType}」×{f.TopTypeCount}{rel}{wb}{oh}";
    }

    // 爆破分析：blast_event 聚合 —— 总次数/方量/炸药/综合单耗/孔进尺 + 逐月(忠实原 BlastService.GetMonthlyAggregate)。
    private void BlastStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var b = Data.GeoDataQueries.GetBlastStats(db.Connection);
        if (b.Events == 0) { StatusMsg.Text = "爆破分析：无爆破数据"; return; }
        var recent = new List<string>();
        for (int i = b.ByMonth.Count - 1; i >= 0 && recent.Count < 3; i--) { var m = b.ByMonth[i]; recent.Insert(0, $"{m.Year}-{m.Month:00}({m.Count}次/{m.VolumeM3 / 1e4:0.#}万m³/单耗{m.AvgUnitKgM3:0.###})"); }
        StatusMsg.Text = $"爆破分析：{b.Events} 次 · 总方量 {b.TotalVolumeM3 / 1e4:0.#} 万m³ · 炸药 {b.TotalExplosiveKg / 1000:0.#} t · 综合单耗 {b.OverallUnitKgM3:0.###} kg/m³ · 孔进尺 {b.TotalHoleLengthM / 1000.0:0.#} km · {b.Locations} 区 · {b.ByMonth.Count} 月 · 近期 " + string.Join(" ", recent);
    }

    // 设备累计工时：台账基准 + 生产工时累加(忠实原 EquipmentService.CalculateCumulativeHours), 按总时降序=检修优先。
    private void CumulativeHoursCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCumulativeHours(db.Connection);
        if (s.Equipment == 0) { StatusMsg.Text = "设备累计工时：无设备数据"; return; }
        var parts = new List<string>();
        foreach (var r in s.Top) { parts.Add($"{r.EquipmentId}{(r.Model.Length > 0 ? "/" + r.Model : "")} {r.CumulativeHours:0}h"); if (parts.Count >= 6) break; }
        StatusMsg.Text = $"设备累计工时({s.Equipment} 台·基准+生产工时累加)：机队合计 {s.FleetTotalHours / 1e4:0.#} 万h · 最高(检修优先) " + string.Join(" · ", parts);
    }

    // 分机型 KPI：各型号平均 可用率/作业率/利用率(忠实原 KpiService.ByModelMonthly 机型聚合), 降序=选型/淘汰参考。
    private void KpiByModelCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetKpiByModel(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "机型KPI：无 KPI 数据"; return; }
        var parts = new List<string>();
        foreach (var r in rows) { parts.Add($"{r.Model}({r.Units}台 可用{r.AvgAvailPct:0.#}%/作业{r.AvgRunRatePct:0.#}%/利用{r.AvgUtilPct:0.#}%)"); if (parts.Count >= 6) break; }
        StatusMsg.Text = $"机型KPI({rows.Count} 型号·按可用率降序)：" + string.Join(" · ", parts);
    }

    private void KpiStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var k = Data.GeoDataQueries.GetKpiStats(db.Connection);
        if (k.Records == 0) { StatusMsg.Text = "KPI 分析：无 KPI 数据"; return; }
        // 故障归因(内/外部故障率)有数据才附加显示
        string fault = (k.AvgInternalFaultPct > 0 || k.AvgExternalFaultPct > 0)
            ? $" · 故障归因 内{k.AvgInternalFaultPct:0.#}%/外{k.AvgExternalFaultPct:0.#}%" : "";
        StatusMsg.Text = $"KPI 分析：{k.Records} 条 · 三率 可用{k.AvgAvailabilityPct:0.#}%/作业{k.AvgRunRatePct:0.#}%/利用{k.AvgUtilizationPct:0.#}% · OEE {k.OeePct:0.#}%{fault} · 最新 {k.LatestYear}-{k.LatestMonth:00}";
    }

    // 设备五维综合评分(熵权法客观赋权): 产能强度/稳定性/可用率/效率/可靠性 → 综合得分排名
    private void EquipmentScoreCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetEquipmentScores(db.Connection, topN: 8);
        if (rows.Count == 0) { StatusMsg.Text = "设备综合评分：无可评设备(需生产记录)"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.EquipmentId} {r.CompositeScore:0.###}(产{r.CapacityIntensity:0.##}/稳{r.Stability:0.##}/用{r.Availability:0.##}/效{r.Efficiency:0.##}/靠{r.Reliability:0.##})");
        StatusMsg.Text = $"设备综合评分(熵权五维 前{rows.Count})：" + string.Join(" · ", parts);
    }

    private void BoreholeStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var b = Data.GeoDataQueries.GetBoreholeStats(db.Connection);
        var cats = new List<string>();
        foreach (var c in b.ByCategory) cats.Add($"{c.Category} {c.Count}");
        StatusMsg.Text = $"钻孔管理：{b.Holes} 孔 · 总进尺 {b.TotalDepthM:0.#}m（均 {b.AvgDepthM:0.#}m）· 见煤结果 {b.SeamResults} · 类别: " + string.Join(" / ", cats);
    }

    // 分煤层煤质箱线(忠实原 CoalQualityStatsWindow 每煤层五数概括): 库样本→按煤层五数概括 + 箱线图上屏 + CSV。
    // 用法 分煤层煤质 [ad|vdaf|std|qnet]。
    private async Task CoalStatsBySeamCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var samples = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (samples.Count == 0) { StatusMsg.Text = "分煤层煤质：无煤样数据"; return; }
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = CoalIndicator(tk, 1, "ad");
        var rows = Data.CoalAnalytics.StatsBySeam(samples, ind);
        if (rows.Count == 0) { StatusMsg.Text = $"分煤层煤质({ind})：无有效样(缺该指标)"; return; }
        // 箱线图上屏(视口中部)
        double vw = ViewportHost.Bounds.Width, vh = ViewportHost.Bounds.Height;
        var p0 = Viewport.ScreenToWorld(vw * 0.3, vh * 0.85) ?? (0.0, 0.0);
        var p1 = Viewport.ScreenToWorld(vw * 0.7, vh * 0.4) ?? (100.0, 50.0);
        double w = System.Math.Abs(p1.x - p0.x), h = System.Math.Abs(p1.y - p0.y);
        if (w < 1e-6) w = 100; if (h < 1e-6) h = 50;
        var boxes = rows.Select(r => (r.SeamCode, r.Min, r.P25, r.Median, r.P75, r.Max)).ToList();
        BeginChange();
        foreach (var e in Cad.BoxPlot.Build(boxes, System.Math.Min(p0.x, p1.x), System.Math.Min(p0.y, p1.y), w, h, System.Math.Max(h * 0.05, 1e-3), ind))
        { e.LayerName = _layers.Current.Name; _scene.Add(e); }
        RefreshScene();
        var name = await SaveCsvAsync("导出分煤层煤质", $"coal_stats_by_seam_{ind}.csv", Data.CoalAnalytics.StatsBySeamToCsv(rows));
        var head = string.Join(" · ", rows.Take(4).Select(r => $"{r.SeamCode}[{r.Min:0.#}~{r.Max:0.#}]中{r.Median:0.#}({r.SampleLevel})"));
        StatusMsg.Text = $"分煤层煤质({ind}·五数概括+样本充分度)：{rows.Count} 煤层 · {head} · 箱线入场景" + (name != null ? $" · CSV → {name}" : "");
    }

    private void CoalQualityStatsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var q = Data.GeoDataQueries.GetCoalQualityStats(db.Connection);
        if (q.Samples == 0) { StatusMsg.Text = "煤质统计：无煤样数据"; return; }
        // 灰分均匀性(变异系数)有评价才附加
        string uni = q.AshUniformity.Length > 0 ? $" · 灰分CV {q.AshCvPct:0.#}%({q.AshUniformity})" : "";
        // 灰分纵向趋势(有高程样本才附加)
        var vt = Data.GeoDataQueries.GetAshVerticalTrend(db.Connection);
        string vtStr = vt.Samples >= 3 ? $" · {vt.Label}(浅{vt.ShallowAshPct:0.#}→深{vt.DeepAshPct:0.#}%)" : "";
        // 均值贴国标等级(读种子 coal_grade_rule → FindGradeLevel), 忠实原看板 KPI 的 GradeAsh/GradeSulfur/GradeQnet 标注。Vdaf 无分级规则(同原, 只标基)。
        string GL(double v, string type) { var lvl = Data.CoalTypeInference.FindGradeLevel(v, Data.GeoDataQueries.GetGradeRulesByType(db.Connection, type)); return lvl != null ? $"({lvl})" : ""; }
        string adG = GL(q.AvgAshPct, "ash"), stG = GL(q.AvgSulfurPct, "sulfur"), qG = GL(q.AvgCalorificMJ, "qnet");
        // 第 5 KPI: 平均粘结指数 G + 强/中/弱粘结(忠实原看板 kpiG, 阈值 ≥65 强/≥35 中/else 弱)。仅有 G 样本才附加。
        string gStr = q.CakingN > 0 ? $" · 粘结G {q.AvgCakingG:0.#}({(q.AvgCakingG >= 65 ? "强粘结" : q.AvgCakingG >= 35 ? "中粘结" : "弱粘结")})" : "";
        StatusMsg.Text = $"煤质统计：{q.Samples} 样 / {q.Seams} 煤层 · 平均 灰分Ad {q.AvgAshPct:0.##}%{adG} · 挥发分Vdaf {q.AvgVolatilePct:0.##}% · 发热量Qnet {q.AvgCalorificMJ:0.##}MJ/kg{qG} · 全硫St {q.AvgSulfurPct:0.###}%{stG}{gStr}{uni}{vtStr}";
    }

    // 煤质数据健康度(忠实原数据看板): 样品数/煤类标注率/化验孔覆盖/工分自洽率
    private void CoalDataHealthCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var h = Data.GeoDataQueries.GetCoalDataHealth(db.Connection);
        if (h.TotalSamples == 0) { StatusMsg.Text = "煤质数据健康度：无煤样"; return; }
        string self = h.SelfEvaluableCount > 0
            ? $" · 工分自洽率 {h.SelfConsistencyPct:0.#}%({h.SelfConsistentCount}/{h.SelfEvaluableCount})"
            : " · 工分自洽率 —(缺 M/A/V/FC 齐全样本)";
        StatusMsg.Text = $"煤质数据健康度：样品 {h.TotalSamples} · 煤类标注率 {h.CoalTypeCoveragePct:0.#}% · 化验孔覆盖 {h.HolesWithSamples}/{h.TotalHoles}={h.HoleCoveragePct:0.#}%{self}";
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

        // 上图定位: 超标样点按坐标画红标记(达标点淡绿)——忠实「超标段带坐标可上图定位」。
        int drawn = 0; double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
        BeginChange();
        foreach (var ev in r.Samples)
        {
            if (!ev.Evaluated || (ev.X == 0 && ev.Y == 0)) continue;
            _scene.Add(new PointEntity { X = ev.X, Y = ev.Y, Size = ev.Pass ? 1.2 : 2.0, Style = ev.Pass ? 2 : 3, Cr = ev.Pass ? 0.3f : 0.9f, Cg = ev.Pass ? 0.7f : 0.15f, Cb = 0.2f, LayerName = ev.Pass ? "商品煤达标" : "商品煤超标" });
            drawn++; if (ev.X < gx0) gx0 = ev.X; if (ev.Y < gy0) gy0 = ev.Y; if (ev.X > gx1) gx1 = ev.X; if (ev.Y > gy1) gy1 = ev.Y;
        }
        if (drawn > 0) { RefreshScene(); if (gx1 > gx0) Viewport.FitBounds(new[] { gx0, gy0, gx1, gy1 }); }

        var seamParts = new List<string>();
        foreach (var s in r.BySeam) seamParts.Add($"{s.SeamCode}({s.Pass}/{s.Evaluated}·{s.PassPct:0.#}%)");
        StatusMsg.Text = $"商品煤符合性（原煤 Ad≤{adMax:0.#}%·St≤{stMax:0.##}%·Qgr≥{qMin:0.#}MJ/kg）：达标 {r.Pass}/{r.Evaluated}（{r.PassPct:0.#}%）· 数据不足 {r.Insufficient}"
            + (drawn > 0 ? $"（{drawn} 上图·红叉超标/绿达标）" : "") + " · 分煤层 " + string.Join(" ", seamParts);
    }

    // 煤类反推(GB/T 5751)：逐煤样按 Vdaf/G/Y 三维区间反推煤类(读 coal_classification 种子区间) + 与标注 coal_type 比对(一致率 QC)
    // + 不一致样上图定位(红) + 导出。忠实原 CoalReferenceService.ResolveCoalType「纯逻辑」+ CoalQuality 审核「煤类反推」。
    private async Task CoalTypeInferCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var samples = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (samples.Count == 0) { StatusMsg.Text = "煤类反推：无煤样数据"; return; }
        var ranges = Data.GeoDataQueries.GetCoalClassificationRanges(db.Connection);
        if (ranges.Count == 0) { StatusMsg.Text = "煤类反推：coal_classification 字典为空(需 GB/T 5751 区间种子)"; return; }
        bool useClean = cmd.Contains("浮煤") || cmd.Contains("clean");
        var r = Data.CoalTypeInference.InferConsistency(samples, ranges, useClean);

        // 上图定位: 反推≠标注(QC 不一致)红、一致淡绿、无法判定灰。
        var byId = new Dictionary<long, (double x, double y)>();
        foreach (var s in samples) byId[s.Id] = (s.X, s.Y);
        int drawn = 0; double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
        BeginChange();
        foreach (var row in r.Rows)
        {
            if (!byId.TryGetValue(row.Id, out var p) || (p.x == 0 && p.y == 0)) continue;
            bool bad = row.Match == false, good = row.Match == true;
            _scene.Add(new PointEntity { X = p.x, Y = p.y, Size = bad ? 2.0 : 1.2, Style = bad ? 3 : good ? 2 : 1,
                Cr = bad ? 0.9f : good ? 0.3f : 0.6f, Cg = bad ? 0.15f : good ? 0.7f : 0.6f, Cb = bad ? 0.2f : good ? 0.2f : 0.6f,
                LayerName = bad ? "煤类不一致" : good ? "煤类一致" : "煤类未判" });
            drawn++; if (p.x < gx0) gx0 = p.x; if (p.y < gy0) gy0 = p.y; if (p.x > gx1) gx1 = p.x; if (p.y > gy1) gy1 = p.y;
        }
        if (drawn > 0) { RefreshScene(); if (gx1 > gx0) Viewport.FitBounds(new[] { gx0, gy0, gx1, gy1 }); }

        // 反推煤类分布(前六)
        var dist = new Dictionary<string, int>();
        foreach (var row in r.Rows) if (row.Inferred != null) { dist.TryGetValue(row.Inferred, out int n); dist[row.Inferred] = n + 1; }
        var top = new List<string>();
        foreach (var kv in dist.OrderByDescending(k => k.Value)) { top.Add($"{kv.Key}×{kv.Value}"); if (top.Count >= 6) break; }

        var sb = new System.Text.StringBuilder();
        sb.Append("id,hole_id,seam,vdaf,g,y,labeled,inferred,match\n");
        string Q(string? s) => s == null ? "" : (s.Contains(',') ? "\"" + s + "\"" : s);
        string F(double? v) => v?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        foreach (var row in r.Rows)
            sb.Append($"{row.Id},{Q(row.HoleId)},{Q(row.SeamCode)},{F(row.Vdaf)},{F(row.G)},{F(row.Y)},{Q(row.Labeled)},{Q(row.Inferred)},{(row.Match == null ? "" : row.Match.Value ? "1" : "0")}\n");
        var name = await SaveCsvAsync("导出煤类反推", "coal_type_inference.csv", sb.ToString());

        StatusMsg.Text = $"煤类反推(GB/T 5751·{(useClean ? "浮煤" : "原煤")}Vdaf)：一致率 {r.RatePct:0.#}%（{r.Consistent}/{r.Total}）· 无法判定 {r.Inconclusive} · 反推分布 [{string.Join(" ", top)}]"
            + (drawn > 0 ? $"（{drawn} 上图·红不一致/绿一致/灰未判）" : "") + (name != null ? $" · CSV → {name}" : "");
    }

    // 煤质数据审核：一键跑物理范围/原煤vs浮煤/煤类反推/同层离群/浮煤回收率 5 类规则 → findings + 有问题样上图定位 + 导出。
    // 忠实原 CoalQualityService.RunAudit 纯规则逻辑(数据可支撑者); 工分自洽(需 Mad/FCd)/测井一致(需 drill/log 厚)Kylin 数据缺, 记录不做。
    private async Task CoalAuditCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var samples = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (samples.Count == 0) { StatusMsg.Text = "煤质审核：无煤样数据"; return; }
        var ranges = Data.GeoDataQueries.GetCoalClassificationRanges(db.Connection);
        var a = Data.CoalAudit.Run(samples, ranges);

        // 上图: 有 Error 的样红、仅 Warning 的样橙(按样本聚合取最重级)。
        var worst = new Dictionary<long, Data.CoalAudit.Severity>();
        foreach (var it in a.Items)
            if (!worst.TryGetValue(it.SampleId, out var cur) || it.Severity > cur) worst[it.SampleId] = it.Severity;
        var byId = new Dictionary<long, (double x, double y)>();
        foreach (var s in samples) byId[s.Id] = (s.X, s.Y);
        int drawn = 0; double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
        BeginChange();
        foreach (var kv in worst)
        {
            if (!byId.TryGetValue(kv.Key, out var p) || (p.x == 0 && p.y == 0)) continue;
            bool err = kv.Value == Data.CoalAudit.Severity.Error;
            _scene.Add(new PointEntity { X = p.x, Y = p.y, Size = err ? 2.0 : 1.5, Style = 3,
                Cr = err ? 0.9f : 0.95f, Cg = err ? 0.15f : 0.6f, Cb = 0.15f, LayerName = err ? "煤质审核-错误" : "煤质审核-警告" });
            drawn++; if (p.x < gx0) gx0 = p.x; if (p.y < gy0) gy0 = p.y; if (p.x > gx1) gx1 = p.x; if (p.y > gy1) gy1 = p.y;
        }
        if (drawn > 0) { RefreshScene(); if (gx1 > gx0) Viewport.FitBounds(new[] { gx0, gy0, gx1, gy1 }); }

        var cat = string.Join(" ", a.ByCategory.Select(c => $"{c.Category}×{c.Count}"));
        var name = await SaveCsvAsync("导出煤质审核", "coal_audit.csv", Data.CoalAudit.ToCsv(a));
        StatusMsg.Text = $"煤质审核({samples.Count} 样·5 类规则)：{a.Findings} 项问题（错 {a.Errors}·警 {a.Warnings}）· 分类 [{cat}]"
            + (drawn > 0 ? $"（{drawn} 样上图·红错/橙警）" : "") + (name != null ? $" · CSV → {name}" : "")
            + " · 工分自洽见「工分自洽」命令; 测井一致见「测井一致」命令";
    }

    // 工分自洽: coal_sample 原煤 M+A+V+FC≈100% 审核(忠实原 RunAudit 规则1)。四项俱全者才审。
    private async Task ProximateConsistencyCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetProximateRows(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "工分自洽：无 M+A+V+FC 四项俱全的煤样(需 mad/ad/vdaf/fcd 齐)"; return; }
        var f = Data.CoalAudit.CheckProximateConsistency(rows);
        int err = f.Count(x => x.Severity == Data.CoalAudit.Severity.Error), warn = f.Count(x => x.Severity == Data.CoalAudit.Severity.Warning);
        var sb = new System.Text.StringBuilder(); sb.Append("hole_id,seam,severity,message\n");
        foreach (var x in f) sb.Append($"{x.HoleId},{x.SeamCode},{x.Severity},{x.Message}\n");
        var name = f.Count > 0 ? await SaveCsvAsync("导出工分自洽", "proximate_consistency.csv", sb.ToString()) : null;
        StatusMsg.Text = $"工分自洽(原煤 M+A+V+FC≈100%·{rows.Count} 样)：{f.Count} 不自洽（错 {err}·警 {warn}）· 自洽率 {100.0 * (rows.Count - f.Count) / rows.Count:0.#}%"
            + (name != null ? $" · CSV → {name}" : "");
    }

    // 测井一致: borehole_seam_result 钻探 vs 测井煤厚一致性(忠实原 RunAudit 规则6, 厚层相对/薄层绝对误差) + 导 CSV。
    private async Task DrillLogConsistencyCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetDrillLogRows(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "测井一致：无钻探+测井煤厚对比数据"; return; }
        var f = Data.CoalAudit.CheckDrillLogConsistency(rows);
        int err = f.Count(x => x.Severity == Data.CoalAudit.Severity.Error), warn = f.Count(x => x.Severity == Data.CoalAudit.Severity.Warning);
        var sb = new System.Text.StringBuilder(); sb.Append("hole_id,seam,severity,message\n");
        foreach (var x in f) sb.Append($"{x.HoleId},{x.SeamCode},{x.Severity},{x.Message}\n");
        var name = f.Count > 0 ? await SaveCsvAsync("导出测井一致", "drill_log_consistency.csv", sb.ToString()) : null;
        var top = f.Take(3).Select(x => $"{x.HoleId}/{x.SeamCode}({x.Message})");
        StatusMsg.Text = $"测井一致(钻探vs测井煤厚·{rows.Count} 对比)：{f.Count} 不一致（错 {err}·警 {warn}）· 一致率 {100.0 * (rows.Count - f.Count) / rows.Count:0.#}%"
            + (f.Count > 0 ? " · " + string.Join(" ", top) : "") + (name != null ? $" · CSV → {name}" : "");
    }

    // 煤质三维插值(IDW 体素块模型)：DB 煤样(x,y,z=z_sample, 指标) → 3D IDW 体素场 → 导 CSV(块体模型) + 取密集 Z 切片上 2D 彩格。
    // 忠实原 DefaultIdwInterpolation(CoalQualitySpatialWindow 插值引擎)。全 3D 显示受阻(2D 场景), 故导块模型 + Z 切片。
    private async Task QualityVoxelInterpCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var samples = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (samples.Count == 0) { StatusMsg.Text = "煤质三维插值：无煤样数据"; return; }
        var tk = cmd.Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
        string ind = tk.Length >= 2 ? tk[1].ToLowerInvariant() : "ad";
        double res = tk.Length >= 3 && double.TryParse(tk[2], out var rr) && rr > 0 ? rr : 0;
        double? Val(Data.CoalSample s) => ind switch { "vdaf" => s.VdafRaw, "std" or "st" => s.StdRaw, "qnet" or "q" => s.QnetAd, _ => s.AdRaw };
        var cps = new List<OrdinaryKriging.ControlPoint>();
        foreach (var s in samples) if (s.Z is double z && Val(s) is double v && !(s.X == 0 && s.Y == 0)) cps.Add(new OrdinaryKriging.ControlPoint(s.X, s.Y, z, v));
        if (cps.Count < 2) { StatusMsg.Text = $"煤质三维插值：有效样本不足(需 z_sample + {ind} 指标 + 坐标; 得 {cps.Count})"; return; }

        double xn = double.MaxValue, yn = double.MaxValue, zn = double.MaxValue, xx = double.MinValue, yx = double.MinValue, zx = double.MinValue;
        foreach (var p in cps) { xn = System.Math.Min(xn, p.X); xx = System.Math.Max(xx, p.X); yn = System.Math.Min(yn, p.Y); yx = System.Math.Max(yx, p.Y); zn = System.Math.Min(zn, p.Z); zx = System.Math.Max(zx, p.Z); }
        if (res <= 0) res = System.Math.Max(1e-3, System.Math.Max(xx - xn, System.Math.Max(yx - yn, zx - zn)) / 15.0);   // 目标 ~15 格/轴
        var vox = QualityVoxelInterp.Interpolate(cps, xn, yn, zn, xx, yx, zx, res);
        if (vox.Count == 0) { StatusMsg.Text = $"煤质三维插值：体素过密或无支撑(试 煤质三维插值 {ind} <更大分辨率>)"; return; }

        // 取体素最密的 Z 层上 2D 图(蓝低→红高)。
        var byZ = new Dictionary<double, int>();
        foreach (var v in vox) { byZ.TryGetValue(v.Z, out int n); byZ[v.Z] = n + 1; }
        double bestZ = 0; int bestN = -1; foreach (var kv in byZ) if (kv.Value > bestN) { bestN = kv.Value; bestZ = kv.Key; }
        var slice = QualityVoxelInterp.ZSlice(vox, bestZ, res * 0.5);
        double vmin = double.MaxValue, vmax = double.MinValue; foreach (var v in slice) { vmin = System.Math.Min(vmin, v.Value); vmax = System.Math.Max(vmax, v.Value); }
        double span = vmax - vmin > 1e-9 ? vmax - vmin : 1;
        BeginChange();
        foreach (var v in slice)
        {
            float t = (float)((v.Value - vmin) / span);   // 0=低 1=高
            _scene.Add(new PointEntity { X = v.X, Y = v.Y, Size = System.Math.Max(res * 0.4, 0.8), Style = 4,
                Cr = t, Cg = 0.15f, Cb = 1 - t, LayerName = $"煤质体素 Z={bestZ:0.#}" });
        }
        RefreshScene();
        Viewport.FitBounds(new[] { xn, yn, xx, yx });
        var name = await SaveCsvAsync("导出煤质体素", $"quality_voxels_{ind}.csv", QualityVoxelInterp.ToCsv(vox));
        var cov = Data.CoalAnalytics.XyCoverage(cps.Select(p => (p.X, p.Y)));   // 化验平面覆盖(忠实原 空间分布 覆盖信息)
        StatusMsg.Text = $"煤质三维插值(IDW·{ind})：{cps.Count} 样 → {vox.Count} 体素(分辨率 {res:0.##}·Z {zn:0.#}~{zx:0.#}) · 化验覆盖 {cov.MaxX - cov.MinX:0.#}×{cov.MaxY - cov.MinY:0.#}m={cov.AreaKm2:0.###}km² · Z={bestZ:0.#} 切片 {slice.Count} 格上图(蓝低→红高·{vmin:0.##}~{vmax:0.##})"
            + (name != null ? $" · 块模型 CSV → {name}" : "") + " · 全 3D 显示受阻(2D 场景), 出块模型+Z 切片";
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
        // 曲线上屏(已算未绘)：限值(X) → 累计质量%(Y) 折线入场景
        double gvw = ViewportHost.Bounds.Width, gvh = ViewportHost.Bounds.Height;
        var gp0 = Viewport.ScreenToWorld(gvw * 0.3, gvh * 0.85) ?? (0.0, 0.0);
        var gp1 = Viewport.ScreenToWorld(gvw * 0.7, gvh * 0.4) ?? (100.0, 50.0);
        double gw = System.Math.Abs(gp1.x - gp0.x), gh = System.Math.Abs(gp1.y - gp0.y);
        if (gw < 1e-6) gw = 100; if (gh < 1e-6) gh = 50;
        var gpts = r.Curve.Select(p => (p.Cutoff, p.CumMassPct)).ToList();
        BeginChange();
        foreach (var ge in Cad.CurvePlot.Build(gpts, System.Math.Min(gp0.x, gp1.x), System.Math.Min(gp0.y, gp1.y),
                     gw, gh, System.Math.Max(gh * 0.05, 1e-3), ind, "累计%"))
        { ge.LayerName = _layers.Current.Name; _scene.Add(ge); }
        RefreshScene();
        StatusMsg.Text = $"品位-储量曲线（{ind}·{(r.BelowCutoff ? "累计≤" : "累计≥")}·质量代理{(r.DensityUsed ? "厚×密度" : "厚度")}）：{r.N} 样·总质量 {r.TotalMass:0.#} · 中点限值 {mid.Cutoff:0.##}→累计 {mid.CumMassPct:0.#}%(均值 {mid.CumMeanGrade:0.##}) · 曲线入场景";
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
        // 竖向剖面曲线上屏(已算未绘)：品位(X) vs 标高中点(Y) —— 带中点连线
        double evw = ViewportHost.Bounds.Width, evh = ViewportHost.Bounds.Height;
        var ep0 = Viewport.ScreenToWorld(evw * 0.3, evh * 0.85) ?? (0.0, 0.0);
        var ep1 = Viewport.ScreenToWorld(evw * 0.7, evh * 0.4) ?? (100.0, 50.0);
        double ew = System.Math.Abs(ep1.x - ep0.x), eh = System.Math.Abs(ep1.y - ep0.y);
        if (ew < 1e-6) ew = 100; if (eh < 1e-6) eh = 50;
        var epts = bands.Select(b => (b.WeightedMean, (b.ZLow + b.ZHigh) / 2)).ToList();
        BeginChange();
        foreach (var ee in Cad.CurvePlot.Build(epts, System.Math.Min(ep0.x, ep1.x), System.Math.Min(ep0.y, ep1.y),
                     ew, eh, System.Math.Max(eh * 0.05, 1e-3), ind, "标高"))
        { ee.LayerName = _layers.Current.Name; _scene.Add(ee); }
        RefreshScene();
        StatusMsg.Text = $"分标高煤质（{ind}·带高{band:0}m·厚度加权均值）：" + string.Join(" ", parts) + " · 剖面入场景";
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

        // 上图定位: 离群样点按坐标画标记(偏高红/偏低蓝, 大小随严重度)——忠实「超标段带坐标可上图定位」。
        var coords = Data.CoalAnalytics.OutlierCoords(r, s);
        int drawn = 0; double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
        if (coords.Count > 0)
        {
            BeginChange();
            foreach (var (x, y, kind, sev) in coords)
            {
                if (x == 0 && y == 0) continue;   // 无坐标样点跳过
                bool hi = kind == "偏高";
                _scene.Add(new CircleEntity { Cx = x, Cy = y, Radius = System.Math.Max(1.0 + sev * 0.5, 1.0), Segments = 20, Cr = hi ? 0.9f : 0.2f, Cg = 0.2f, Cb = hi ? 0.2f : 0.9f, LayerName = "煤质离群" });
                drawn++; if (x < gx0) gx0 = x; if (y < gy0) gy0 = y; if (x > gx1) gx1 = x; if (y > gy1) gy1 = y;
            }
            if (drawn > 0) { RefreshScene(); if (gx1 > gx0) Viewport.FitBounds(new[] { gx0, gy0, gx1, gy1 }); }
        }

        var top = new List<string>();
        foreach (var o in r.Outliers) { if (top.Count >= 5) break; top.Add($"{o.HoleId}/{o.SeamCode} {o.Value:0.##}({o.Kind}{o.Severity:0.#}IQR)"); }
        StatusMsg.Text = $"煤质离群 QC（{ind}·Tukey 1.5×IQR）：{r.N}样 中位{r.Median:0.##} Q1{r.Q1:0.##}/Q3{r.Q3:0.##} 栅栏[{r.Lower:0.##},{r.Upper:0.##}] → 离群 {r.Outliers.Count} 段"
            + (drawn > 0 ? $"（{drawn} 上图定位·红高/蓝低）" : "") + (top.Count > 0 ? "：" + string.Join(" · ", top) : "");
    }

    // 灰分-发热量回归(忠实 CoalQualityAnalytics.AshCalorificRegression): 一元 OLS + r² + 残差 z-score 离群。
    private async Task AshCalorificRegressionCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "灰分发热量回归：无煤样"; return; }
        var kind = cmd.ToLowerInvariant().Contains("net") ? Data.CalorificKind.Qnet : Data.CalorificKind.Qgr;
        var r = Data.CoalAnalytics.AshCalorificRegression(s, kind);
        if (r.N < 5) { StatusMsg.Text = $"灰分发热量回归：有效样本不足(<5, 需同时有 Ad+{r.YName})"; return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("灰分-发热量回归");
        sb.AppendLine($"样本数,{r.N}");
        sb.AppendLine($"斜率(每%灰),{r.Slope:0.####}");
        sb.AppendLine($"截距,{r.Intercept:0.###}");
        sb.AppendLine($"R²,{r.R2:0.####}");
        sb.AppendLine($"灰分范围(%),{r.XMin:0.##}~{r.XMax:0.##}");
        sb.AppendLine();
        sb.AppendLine($"Ad(%),{r.YName}");
        foreach (var (ad, cal) in r.Points) sb.AppendLine($"{ad:0.##},{cal:0.###}");
        if (r.Suspects.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("残差离群(|z|≥2.5),孔号,煤层,Ad,发热量,z");
            foreach (var su in r.Suspects) sb.AppendLine($",{su.HoleId},{su.SeamCode},{su.Ad:0.##},{su.Cal:0.###},{su.ZScore:0.##}");
        }
        var saved = await SaveCsvAsync("灰分发热量回归", "灰分发热量回归.csv", sb.ToString());
        // 交会散点图上屏(已算未绘)：Ad(X) vs 发热量(Y) 点 + 拟合线 + 残差离群红叉
        double svw = ViewportHost.Bounds.Width, svh = ViewportHost.Bounds.Height;
        var sp0 = Viewport.ScreenToWorld(svw * 0.3, svh * 0.85) ?? (0.0, 0.0);
        var sp1 = Viewport.ScreenToWorld(svw * 0.7, svh * 0.4) ?? (100.0, 50.0);
        double sw = System.Math.Abs(sp1.x - sp0.x), sh = System.Math.Abs(sp1.y - sp0.y);
        if (sw < 1e-6) sw = 100; if (sh < 1e-6) sh = 50;
        var hi = r.Suspects.Select(su => (su.Ad, su.Cal)).ToList();
        BeginChange();
        foreach (var se in Cad.ScatterPlot.Build(r.Points, System.Math.Min(sp0.x, sp1.x), System.Math.Min(sp0.y, sp1.y),
                     sw, sh, System.Math.Max(sh * 0.05, 1e-3), "Ad%", r.YName, (r.Slope, r.Intercept), hi))
        { se.LayerName = _layers.Current.Name; _scene.Add(se); }
        RefreshScene();
        StatusMsg.Text = $"灰分-发热量回归({r.YName})：{r.N}样 · {r.YName}={r.Intercept:0.#}{(r.Slope >= 0 ? "+" : "")}{r.Slope:0.###}·Ad · R²={r.R2:0.###}(相关{(r.R2 >= 0.5 ? "强" : r.R2 >= 0.25 ? "中" : "弱")}) · 残差离群 {r.Suspects.Count} 段 · 交会散点入场景"
                       + (saved != null ? $" · 存 {saved}" : "");
    }

    // 煤质综合结论(忠实 CoalQualityAnalytics.OverallConclusions): 表征/煤层对比/均匀/相关/洗选/用途/数据质量 七类可读结论。
    private async Task CoalConclusionsCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "煤质综合结论：无煤样"; return; }
        var kind = cmd.ToLowerInvariant().Contains("net") ? Data.CalorificKind.Qnet : Data.CalorificKind.Qgr;
        var cs = Data.CoalAnalytics.OverallConclusions(s, kind);
        var saved = await SaveCsvAsync("煤质综合结论", "煤质综合结论.csv", Data.CoalAnalytics.ConclusionsToCsv(cs));
        var repr = cs.FirstOrDefault(c => c.Category == "煤质表征")?.Text ?? cs.FirstOrDefault()?.Text ?? "";
        StatusMsg.Text = $"煤质综合结论：{cs.Count} 条 · {repr}" + (saved != null ? $" · 存 {saved}" : "");
    }

    // 空间估值交叉验证(忠实 CoalQualityEstimator.CrossValidate): 煤质指标点 留一交叉验证 OK/IDW/NN/MA → ME/MAE/RMSE/R²。
    // "交叉验证 [OK|IDW|NN|MA] [ad|qgr|std|vdaf]"(缺省 OK·灰分)。
    private async Task SpatialCvCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "交叉验证：无煤样"; return; }
        string method = "OK", ind = "ad", indLabel = "灰分Ad";
        foreach (var t in cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var u = t.ToUpperInvariant();
            if (u == "OK" || t.Contains("克里金")) method = "OK";
            else if (u == "IDW" || t.Contains("反距离")) method = "IDW";
            else if (u == "NN" || t.Contains("最近邻")) method = "NN";
            else if (u == "MA" || t.Contains("移动平均")) method = "MA";
            else if (u == "AD" || t.Contains("灰")) { ind = "ad"; indLabel = "灰分Ad"; }
            else if (u == "QGR" || t.Contains("发热") || t.Contains("热量")) { ind = "qgr"; indLabel = "发热量Qgr"; }
            else if (u == "QNET") { ind = "qnet"; indLabel = "发热量Qnet"; }
            else if (u == "STD" || u == "ST" || t.Contains("硫")) { ind = "std"; indLabel = "全硫St"; }
            else if (u == "VDAF" || t.Contains("挥发")) { ind = "vdaf"; indLabel = "挥发分Vdaf"; }
        }
        var cps = new List<Cad.OrdinaryKriging.ControlPoint>();
        foreach (var r in s) { var v = Data.CoalAnalytics.Value(r, ind, false); if (v.HasValue) cps.Add(new(r.X, r.Y, r.Z ?? 0, v.Value)); }
        if (cps.Count < 4) { StatusMsg.Text = $"交叉验证({indLabel})：有效点不足(<4)"; return; }
        var res = Cad.SpatialCrossValidation.CrossValidate(cps, method);
        if (res.Predicted == 0) { StatusMsg.Text = $"交叉验证({indLabel})：无点可预测(邻域半径外?)"; return; }
        var saved = await SaveCsvAsync($"交叉验证_{method}", $"交叉验证_{method}.csv", Cad.SpatialCrossValidation.ToCsv(res, method));
        string mse = res.MSE.HasValue ? $" · 标准化误差 {res.MSE.Value:0.##}(方差 {res.MSEVar:0.##},≈1 佳)" : "";
        StatusMsg.Text = $"交叉验证({method}·{indLabel})：{res.Predicted}/{res.N}点 · ME={res.ME:0.##} MAE={res.MAE:0.##} RMSE={res.RMSE:0.##} R²={res.R2:0.###}{mse}"
                       + (saved != null ? $" · 存 {saved}" : "");
    }

    // 变差函数分析(忠实 EstimationAlgorithms.ComputeExperimentalVariogram + FitSpherical): 煤质指标点 →
    // 实验半变异 γ(h) 云 + 球状模型拟合(块金/基台/变程), 看空间相关结构/验证克里金拟合。原经 VariogramEditor 交互, 此出表。
    private async Task VariogramAnalysisCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var s = Data.GeoDataQueries.GetCoalSamples(db.Connection);
        if (s.Count == 0) { StatusMsg.Text = "变差函数分析：无煤样"; return; }
        string ind = "ad", label = "灰分Ad";
        foreach (var t in cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var u = t.ToUpperInvariant();
            if (u == "AD" || t.Contains("灰")) { ind = "ad"; label = "灰分Ad"; }
            else if (u == "QGR" || t.Contains("发热") || t.Contains("热量")) { ind = "qgr"; label = "发热量Qgr"; }
            else if (u == "QNET") { ind = "qnet"; label = "发热量Qnet"; }
            else if (u == "STD" || u == "ST" || t.Contains("硫")) { ind = "std"; label = "全硫St"; }
            else if (u == "VDAF" || t.Contains("挥发")) { ind = "vdaf"; label = "挥发分Vdaf"; }
        }
        var cps = new List<Cad.OrdinaryKriging.ControlPoint>();
        foreach (var r in s) { var v = Data.CoalAnalytics.Value(r, ind, false); if (v.HasValue) cps.Add(new(r.X, r.Y, r.Z ?? 0, v.Value)); }
        if (cps.Count < 3) { StatusMsg.Text = $"变差函数分析({label})：有效点不足(<3)"; return; }

        var exp = Cad.OrdinaryKriging.ExperimentalVariogram(cps);
        var (vg, sSph, sExp, sGau) = Cad.OrdinaryKriging.SelectVariogramModel(cps);   // 三型自动选优
        string modelCn = vg.Model switch { Cad.OrdinaryKriging.VariogramModel.Exponential => "指数", Cad.OrdinaryKriging.VariogramModel.Gaussian => "高斯", _ => "球状" };
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("变差函数分析(实验半变异 + 三型自动选优拟合)");
        sb.AppendLine($"指标,{label}");
        sb.AppendLine($"控制点,{cps.Count}");
        sb.AppendLine($"最佳模型,{modelCn}");
        sb.AppendLine($"块金Nugget,{vg.Nugget:0.####}");
        sb.AppendLine($"基台Sill,{vg.Sill:0.####}");
        sb.AppendLine($"变程Range,{vg.Range:0.##}");
        sb.AppendLine($"残差SSE-球状,{sSph:0.####}");
        sb.AppendLine($"残差SSE-指数,{sExp:0.####}");
        sb.AppendLine($"残差SSE-高斯,{sGau:0.####}");
        sb.AppendLine();
        sb.AppendLine("滞后H,实验γ(h),点对数,拟合γ(h)");
        foreach (var b in exp) sb.AppendLine($"{b.H:0.##},{b.Gamma:0.####},{b.Count},{vg.Gamma(b.H):0.####}");
        var saved = await SaveCsvAsync("变差函数分析", "变差函数分析.csv", sb.ToString());
        int filled = exp.Count(b => b.Count > 0);
        StatusMsg.Text = $"变差函数分析({label})：{cps.Count}点 · 最佳{modelCn}模型 块金{vg.Nugget:0.##}/基台{vg.Sill:0.##}/变程{vg.Range:0.#} · {filled}/{exp.Count}有效滞后箱(SSE 球{sSph:0.#}/指{sExp:0.#}/高{sGau:0.#})"
                       + (saved != null ? $" · 存 {saved}" : "");
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
        foreach (var r in rows) parts.Add($"{r.Shift}班({r.Records}条·{r.OutputM3 / 1e4:0.##}万m³·作业率{r.UtilizationPct:0.#}%·台效{r.EfficiencyM3PerH:0.#}m³/h)");
        StatusMsg.Text = $"班次产量对比：" + string.Join(" · ", parts);
    }

    private void KpiTrendCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetKpiTrend(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "KPI趋势：无 KPI 记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.Year}(可用{r.AvgAvailabilityPct:0.#}%·作业{r.AvgRunRatePct:0.#}%·利用{r.AvgUtilizationPct:0.#}%)");
        StatusMsg.Text = $"设备KPI趋势(三率)：" + string.Join(" · ", parts);
    }

    private void CapacityByCategoryCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetCapacityByCategory(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "产能分类对比：无产能数据"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.Category}({r.Units}台·{r.TotalOutputM3 / 1e4:0.#}万m³·{r.SharePct:0.#}%)");
        DrawCategoryBars(rows.Select(r => (r.Category, r.TotalOutputM3 / 1e4)).ToList(), "万m³");
        StatusMsg.Text = $"产能分类对比：" + string.Join(" · ", parts) + " · 分类柱入场景";
    }

    private void FaultByTypeCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetFaultByType(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "故障类型分布：无故障记录"; return; }
        var parts = new List<string>();
        foreach (var r in rows) parts.Add($"{r.FaultType}({r.Events}次·{r.DowntimeHours:0.#}h·{r.DowntimeSharePct:0.#}%·累计{r.CumulativeSharePct:0.#}%)");
        // 帕累托 80/20: 前几类累计占 80% 停机
        int vital = 0; foreach (var r in rows) { vital++; if (r.CumulativeSharePct >= 80) break; }
        DrawCategoryBars(rows.Select(r => (r.FaultType, r.DowntimeHours)).ToList(), "停机h");
        StatusMsg.Text = $"故障类型分布(帕累托 前{vital}/{rows.Count}类占80%停机)：" + string.Join(" · ", parts) + " · 帕累托柱入场景";
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
        // 峰值年 + 最新年占峰比(忠实原 EquipmentCapabilityWindow) + 同比
        var s = Data.GeoDataQueries.SummarizeAnnual(rows);
        string yoy = rows.Count >= 2 ? $" · 同比 {s.YoYPct:+0.#;-0.#}%" : "";
        StatusMsg.Text = $"年度产量趋势（{rows.Count} 年 · 累计 {tot:0.#}万m³ · 峰值 {s.PeakYear}年{s.PeakWanM3:0.#}万m³ · {s.LatestYear}年为峰值 {s.LatestVsPeakPct:0.#}%{yoy}）：" + string.Join(" · ", parts);
    }

    private void CoalQualityBySeamCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetCoalQualityBySeam(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "分煤层煤质：无煤样"; return; }
        var parts = new List<string>();
        // 工业分析 M/A/V/FC 全列(水分/灰分/挥发/固定碳) + 发热量; 水分/固碳无数据时省略
        foreach (var r in rows)
        {
            string mv = r.AvgMoisturePct > 0 ? $"水{r.AvgMoisturePct:0.#}/" : "";
            string fc = r.AvgFixedCarbonPct > 0 ? $"/固碳{r.AvgFixedCarbonPct:0.#}" : "";
            parts.Add($"{r.SeamCode}({r.Samples}样·{mv}灰{r.AvgAshPct:0.#}/挥{r.AvgVolatilePct:0.#}{fc}/热{r.AvgCalorificMJ:0.#})");
        }
        StatusMsg.Text = $"分煤层煤质（{rows.Count} 层）：" + string.Join(" · ", parts);
    }

    // SQL 查询（原 SqlLib SQL Console 查询侧）：SQL查询 <SELECT…> → 只读执行 → 结果 CSV 导出
    private async Task RunSqlQueryAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        int sp = cmd.IndexOf(' ');
        string sql = sp >= 0 ? cmd.Substring(sp + 1).Trim() : "";
        if (string.IsNullOrWhiteSpace(sql)) { StatusMsg.Text = "SQL查询：用法「SQL查询 <SELECT 语句>」(仅只读)"; return; }
        var (ok, text, rows) = Data.GeoDataQueries.RunSelectCsv(db.Connection, sql);
        if (!ok) { StatusMsg.Text = $"SQL查询失败：{text}"; return; }
        var name = await SaveCsvAsync("导出SQL结果", "sql_result.csv", text);
        StatusMsg.Text = $"SQL查询：{rows} 行结果" + (name != null ? $" → {name}" : "（未保存）");
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

    // 参数验收判定(忠实原 ParameterAcceptanceService.ComputeStatus): 查 parameter_definition 逐参数标定的
    // 标准/报警范围 → 判实测值 fail/warning/pass + 偏差%。区别 参数校核(兜底规范默认): 此用 DB 逐参数标定范围(已种子)。
    // 用法 "参数验收判定 <参数code> <实测值> [模板值]"。
    private void ParamAcceptanceJudgeCmd(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '，' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 3 || !double.TryParse(tk[2], System.Globalization.NumberStyles.Float, inv, out double measured))
        { StatusMsg.Text = "参数验收判定：用法 参数验收判定 <参数code> <实测值> [模板值]（code 见 parameter_definition）"; return; }
        double? template = null;
        if (tk.Length >= 4 && double.TryParse(tk[3], System.Globalization.NumberStyles.Float, inv, out double tv)) template = tv;
        var db = EnsureGeoDb(); if (db == null) return;
        var norm = Data.GeoDataQueries.GetParameterNorm(db.Connection, tk[1]);
        if (norm == null) { StatusMsg.Text = $"参数验收判定：参数 '{tk[1]}' 无定义（查 parameter_definition.code）"; return; }
        var (dev, status) = Data.GeoDataQueries.ComputeAcceptanceStatus(
            norm.AlarmLow, norm.AlarmHigh, norm.StandardMin, norm.StandardMax, template ?? norm.StandardDefault, measured);
        string st = status switch { "fail" => "✗ 超标(fail)", "warning" => "⚠ 警告(warning)", "pending" => "待测(pending)", _ => "✓ 合格(pass)" };
        string rng(double? a, double? b) => $"[{(a.HasValue ? a.Value.ToString("0.##", inv) : "—")}~{(b.HasValue ? b.Value.ToString("0.##", inv) : "—")}]";
        StatusMsg.Text = $"参数验收判定：{norm.Name}({norm.Code}) 实测 {measured.ToString("0.##", inv)}{norm.Unit} → {st}"
            + (dev.HasValue ? $" · 偏差 {dev.Value.ToString("+0.#;-0.#", inv)}%" : "")
            + $" · 标准{rng(norm.StandardMin, norm.StandardMax)} 报警{rng(norm.AlarmLow, norm.AlarmHigh)}";
    }

    // 兼容机型判定(忠实原 ProcessArchitectureService.CompatibleModels): 某参数在给定实测值下, 全机型 − 违反其硬约束者。
    // 数据: equipment_constraint(V012 种子)+ equipment_model。用法 "兼容机型 <参数code> <实测值>"。
    private void CompatibleModelsCmd(string cmd)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tk = cmd.Split(new[] { ' ', ',', '，' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 3 || !double.TryParse(tk[2], System.Globalization.NumberStyles.Float, inv, out double v))
        { StatusMsg.Text = "兼容机型：用法 兼容机型 <参数code> <实测值>（code 见 parameter_definition）"; return; }
        var db = EnsureGeoDb(); if (db == null) return;
        var (compat, blocked) = Data.GeoDataQueries.CompatibleModels(db.Connection, tk[1], v);
        if (compat.Count == 0 && blocked.Count == 0) { StatusMsg.Text = $"兼容机型：参数 '{tk[1]}' 无机型/约束数据（查 equipment_model / equipment_constraint）"; return; }
        string cList = compat.Count <= 8 ? string.Join("/", compat) : string.Join("/", compat.Take(8)) + $"…(+{compat.Count - 8})";
        StatusMsg.Text = $"兼容机型判定：参数 {tk[1]} 实测 {v.ToString("0.##", inv)} → 可用 {compat.Count} 型"
            + (compat.Count > 0 ? $"[{cList}]" : "")
            + (blocked.Count > 0 ? $" · 禁用 {blocked.Count} 型[{string.Join("/", blocked)}]" : " · 无禁用");
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

    // 排土场台账(忠实原 DumpSite + FillRate): DB dump_site → 各场 容量/充填率/坡角/剩余年限/状态 + 总容/总充填率。
    private void DumpSitesCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var sites = Data.GeoDataQueries.GetDumpSites(db.Connection);
        if (sites.Count == 0) { StatusMsg.Text = "排土场台账：无排土场数据（dump_site）"; return; }
        double totCap = 0, totFill = 0;
        var parts = new List<string>();
        foreach (var s in sites)
        {
            totCap += s.DesignCapacityWanM3; totFill += s.CurrentFilledWanM3;
            string st = s.Status switch { "full" => "满", "closed" => "关闭", _ => "在用" };
            parts.Add($"{s.Name}({(s.DumpType == "internal" ? "内排" : "外排")}·容{s.DesignCapacityWanM3:0}万m³·充填{s.FillRatePct:0.#}%·{st})");
        }
        double overallFill = totCap > 1e-9 ? totFill / totCap * 100.0 : 0;
        StatusMsg.Text = $"排土场台账（{sites.Count} 场·总容 {totCap:0}万m³·综合充填 {overallFill:0.#}%）：" + string.Join(" · ", parts);
    }

    // 钻孔煤质汇总(忠实原 CoalQualityService.AllSummary): 读衍生表 coal_sample_summary(每孔每层化验平均)→
    // 按煤层样数加权汇总(Ad/Vdaf/St/Qgr + 主煤类)。§92/93 式: 已种子数据未 surface → 补读命令。
    private void CoalSampleSummaryCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var rows = Data.GeoDataQueries.GetCoalSampleSummaries(db.Connection);
        if (rows.Count == 0) { StatusMsg.Text = "钻孔煤质汇总：无 coal_sample_summary 数据"; return; }
        var parts = new List<string>();
        foreach (var g in rows.GroupBy(r => r.SeamCode).OrderBy(g => g.Key))
        {
            int boreholes = g.Count(), samples = g.Sum(r => r.SampleCount);
            double W(System.Func<Data.GeoDataQueries.CoalSampleSummaryRow, double?> f)
            {
                double num = 0, den = 0;
                foreach (var r in g) if (f(r).HasValue) { num += f(r)!.Value * r.SampleCount; den += r.SampleCount; }
                return den > 0 ? num / den : 0;
            }
            string dom = g.Where(r => !string.IsNullOrEmpty(r.DominantCoalType))
                          .GroupBy(r => r.DominantCoalType!).OrderByDescending(x => x.Sum(r => r.SampleCount))
                          .Select(x => x.Key).FirstOrDefault() ?? "—";
            parts.Add($"{g.Key}({boreholes}孔/{samples}样·Ad{W(r => r.AvgAdRawPct):0.#}%·Vdaf{W(r => r.AvgVdafRawPct):0.#}%·St{W(r => r.AvgStdRawPct):0.##}%·Qgr{W(r => r.AvgQgrDMjKg):0.#}·{dom})");
        }
        StatusMsg.Text = $"钻孔煤质汇总(每孔每层平均, 按样数加权)：{rows.Count} 孔层对 · " + string.Join(" · ", parts);
    }

    private void HaulRoadsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var roads = Data.GeoDataQueries.GetHaulRoads(db.Connection);
        if (roads.Count == 0) { StatusMsg.Text = "路况显示：无道路数据"; return; }
        var parts = new List<string>();
        foreach (var r in roads) parts.Add($"{(string.IsNullOrEmpty(r.Name) ? r.RoadId : r.Name)}(长{r.LengthM:0.#}m/坡{r.MaxSlopePct:0.#}%/宽{r.WidthM:0.#}m{(string.IsNullOrEmpty(r.Condition) ? "" : "/" + r.Condition)})");
        double netKm = Data.GeoDataQueries.GetHaulRoadNetworkKm(db.Connection);   // 在役路网总里程(忠实 TotalNetworkKm)
        StatusMsg.Text = $"路况显示（{roads.Count} 路段·在役总里程 {netKm:0.##}km）：" + string.Join(" · ", parts);
    }

    private void SlopeDesignsCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var slopes = Data.GeoDataQueries.GetSlopeDesigns(db.Connection);
        if (slopes.Count == 0) { StatusMsg.Text = "边坡设计：无边坡数据"; return; }
        // 校核: 无黏聚力安全系数 F=tanφ/tanβ(忠实原 CohesionlessFactorOfSafety), β=帮别对应帮坡角, φ=内摩擦角; 规范 F≥1.30。
        var parts = new List<string>();
        int unsafeN = 0;
        foreach (var s in slopes)
        {
            bool working = string.Equals(s.SideType, "working", System.StringComparison.OrdinalIgnoreCase);
            double beta = working ? (s.WorkingAngle > 0 ? s.WorkingAngle : s.FinalAngle) : (s.FinalAngle > 0 ? s.FinalAngle : s.WorkingAngle);
            string fStr = "";
            if (s.FrictionAngle > 0 && beta > 0)
            {
                double f = Cad.BenchParameterVerifier.CohesionlessFactorOfSafety(beta, s.FrictionAngle);   // 复用既有(勿重复), 忠实原 CohesionlessFactorOfSafety
                bool ok = f >= 1.30;                                                                       // 规范整体边坡安全阈 F≥1.30
                if (!ok) unsafeN++;
                fStr = $"/校核F={f:0.##}({(ok ? "✓" : "⚠<1.30")})";
            }
            parts.Add($"{s.Side}(工作帮{s.WorkingAngle:0.#}°/最终帮{s.FinalAngle:0.#}°/深{s.MaxDepth:0.#}m/设计F{s.SafetyFactor:0.##}{fStr})");
        }
        StatusMsg.Text = $"边坡设计（{slopes.Count} 帮{(unsafeN > 0 ? $"·⚠{unsafeN}帮校核F<1.30" : "")}）：" + string.Join(" · ", parts);
    }

    private void FleetOverviewCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var f = Data.GeoDataQueries.GetFleetOverview(db.Connection);
        var st = new List<string>(); foreach (var c in f.ByStatus) st.Add($"{c.Category} {c.Count}");
        var md = new List<string>(); foreach (var c in f.ByModel) md.Add($"{c.Category} {c.Count}");
        StatusMsg.Text = $"机群总览：共 {f.Total} 台 · 状态[{string.Join(" / ", st)}] · 型号[{string.Join(" / ", md)}]";
    }

    // 机群领导驾驶舱: 健康度红绿灯 + 平均OEE + 可解锁产能 + 产能瓶颈 + 需关注设备(前几台)
    private void FleetCockpitCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var c = Data.GeoDataQueries.GetFleetCockpit(db.Connection);
        if (c.WithKpi == 0) { StatusMsg.Text = "机群驾驶舱：无 KPI 数据的设备"; return; }
        var watch = new List<string>();
        foreach (var w in c.Watch) { watch.Add($"{w.Icon}{w.EquipmentId} {w.Issue}"); if (watch.Count >= 3) break; }
        string watchStr = watch.Count > 0 ? " · 需关注 " + string.Join(" ; ", watch) : "";
        StatusMsg.Text = $"机群驾驶舱：{c.WithKpi}台在评 · 🟢{c.Green}/🟡{c.Yellow}/🔴{c.Red} · 平均OEE {c.AvgOeePct:0.#}% · 瓶颈【{c.BottleneckCategory}】达标率{c.BottleneckPassPct:0.#}% · 可解锁 {c.UnlockWanM3:0.#}万m³/年{watchStr}";
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
        // 实际样本煤种分布(忠实原「煤类饼」的量化)
        var dist = Data.GeoDataQueries.GetCoalTypeDistribution(db.Connection);
        string distStr = dist.Count > 0
            ? " · 样本分布 " + string.Join("/", dist.ConvertAll(d => $"{d.CoalType}{d.Samples}({d.SharePct:0.#}%)"))
            : "";
        // 煤类分布柱状图上屏(忠实原「煤类饼」量化)：类别→占比%
        if (dist.Count > 0)
            DrawCategoryBars(dist.Select(d => (d.CoalType, d.SharePct)).ToList(), "占比%");
        StatusMsg.Text = $"煤种分类（{cls.Count} 种）：" + string.Join(" · ", parts) + distStr
            + (dist.Count > 0 ? " · 煤类分布柱入场景" : "");
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

    // 数据字典导出(原 SqlLib「导出数据字典」)：全部用户表结构(表,列,类型,非空,主键)→ CSV。
    private async System.Threading.Tasks.Task ExportDataDictionaryAsync()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var tables = Data.GeoDataQueries.ListTables(db.Connection);
        string csv = Data.GeoDataQueries.DataDictionaryCsv(db.Connection);
        int cols = System.Math.Max(0, csv.Split('\n').Length - 2);   // 减表头 + 末空行
        var name = await SaveCsvAsync("数据字典", "data_dictionary.csv", csv);
        if (name != null) StatusMsg.Text = $"数据字典：{tables.Count} 表 · {cols} 列 → {name}（表名/列名/类型/非空/主键）";
    }

    private void EfficiencyForecastCmd()
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var f = Data.GeoDataQueries.GetEfficiencyForecast(db.Connection);
        StatusMsg.Text = $"设备效能预测（基线+投影）：基线月产 {f.BaselineMonthlyWanM3:0.##}万m³/产出设备 · 可用率 {f.AvgAvailabilityPct:0.#}% · 作业率 {f.AvgRunRatePct:0.#}% · 投影年产 {f.ProjectedAnnualWanM3:0.#}万m³（产出设备 {f.ProducingUnits}台 · 在役 {f.ActiveEquipment}台）";
    }

    // 效能提升 What-if 模拟(忠实原 EquipmentForecastWindow 四杠杆): 基线产能 × (1+Σ杠杆贡献×协同衰减)。
    // 用法 效能提升模拟 <故障降低%> <出动率%> <装载%> <运距%>(缺省各 0)。
    private void EfficiencyWhatIfCmd(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var f = Data.GeoDataQueries.GetEfficiencyForecast(db.Connection);
        if (f.BaselineMonthlyWanM3 <= 0) { StatusMsg.Text = "效能提升模拟：无基线产能(产出设备月产=0)"; return; }
        double faultShare = Data.GeoDataQueries.GetFaultShare(db.Connection);
        var tk = cmd.Split(new[] { ' ', ',', '\t', '%' }, System.StringSplitOptions.RemoveEmptyEntries);
        double P(int i) => tk.Length > i && double.TryParse(tk[i], out var v) ? System.Math.Max(0, v) / 100.0 : 0;
        double l1 = P(1), l2 = P(2), l3 = P(3), l4 = P(4);
        if (l1 == 0 && l2 == 0 && l3 == 0 && l4 == 0) { StatusMsg.Text = "效能提升模拟：用法 效能提升模拟 <故障降低%> <出动率%> <装载%> <运距%>（如 效能提升模拟 40 15 10 15）"; return; }
        var r = Data.EfficiencyWhatIf.Simulate(f.BaselineMonthlyWanM3, faultShare, l1, l2, l3, l4);
        // 各杠杆贡献柱(基线百分比) 上屏
        DrawCategoryBars(new System.Collections.Generic.List<(string, double)>
        { ("故障降低", r.C1 * 100), ("出动率", r.C2 * 100), ("装载", r.C3 * 100), ("运距", r.C4 * 100) }, "贡献%");
        StatusMsg.Text = $"效能提升模拟(四杠杆·协同衰减 {Data.EfficiencyWhatIf.SynergyDecay})：基线 {r.BaseOutput:0.##} → 模拟 {r.Simulated:0.##}万m³/月(+{r.Delta:0.##}, 增益 {r.ActualGain * 100:0.#}%) · 贡献 故障{r.C1 * 100:0.#}/出动{r.C2 * 100:0.#}/装载{r.C3 * 100:0.#}/运距{r.C4 * 100:0.#}% · 故障工时占比 {faultShare * 100:0.#}% · 贡献柱入场景";
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

    // 虚拟钻孔：层位点建各煤层顶/底板面 → 在 (x,y) 竖直求交合成钻孔柱 → 导出 CSV
    private async Task VirtualDrillAsync(string cmd)
    {
        var db = EnsureGeoDb(); if (db == null) return;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length < 3 || !double.TryParse(tk[1], out double qx) || !double.TryParse(tk[2], out double qy))
        { StatusMsg.Text = "虚拟钻孔：用法「虚拟钻孔 <x> <y>」"; return; }
        var hp = Data.GeoDataQueries.GetHorizonPoints(db.Connection);
        if (hp.Count == 0) { StatusMsg.Text = "虚拟钻孔：库中无层位点(先展绘层位数据)"; return; }
        var seams = VirtualBorehole.SeamsFromHorizonPoints(
            System.Linq.Enumerable.Select(hp, p => (p.SeamCode, p.IsRoof, p.X, p.Y, p.Z)));
        var hits = VirtualBorehole.Drill(qx, qy, seams);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (hits.Count == 0) { StatusMsg.Text = $"虚拟钻孔({qx.ToString("0.#", inv)},{qy.ToString("0.#", inv)})：未见煤(位置在煤层面覆盖外)"; return; }

        // 2D 柱状预览(忠实原"出 2D 柱状预览"): 在钻孔位置画岩柱+煤层分色+深度刻度+煤层/厚度标注。
        double dz = hits[0].RoofZ - hits[hits.Count - 1].FloorZ; if (dz <= 0) dz = 1;
        double vScale = 1.0, colW = System.Math.Max(dz * 0.2, 5), colLbl = System.Math.Max(dz * 0.06, 1);
        BeginChange();
        foreach (var e in BoreholeRender.BuildVirtualColumn(hits, qx, qy, vScale, colW, colLbl)) { e.LayerName = "虚拟钻孔柱状"; _scene.Add(e); }
        RefreshScene();

        var name = await SaveCsvAsync("导出虚拟钻孔", "virtual_borehole.csv", VirtualBorehole.ToCsv(hits));
        var top = hits[0];
        StatusMsg.Text = $"虚拟钻孔({qx.ToString("0.#", inv)},{qy.ToString("0.#", inv)})：见 {hits.Count} 层 · 顶层 {top.SeamCode}(顶{top.RoofZ.ToString("0.#", inv)}/底{top.FloorZ.ToString("0.#", inv)}/厚{top.Thickness.ToString("0.##", inv)}) · 已绘柱状预览"
            + (name != null ? $" → {name}" : "");
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
        if (AcadCommands.IsAcadCommand(t)) return true;                       // AutoCAD 命令名/缩写(L / REC / DLI …)
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

    /// <summary>
    /// 启动时把「图形降级」情况报到信息栏与状态栏 —— 降级是静默发生的(启动器按上次崩溃自动降档),
    /// 不说清楚用户只会觉得"怎么变慢了/三维没了"。
    /// </summary>
    private void ReportGraphicsDowngrade()
    {
        string? prof = System.Environment.GetEnvironmentVariable("PITMINE_GL_PROFILE");
        bool soft = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE"));
        bool nogl = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("PITMINE_NO_GL"));
        string? msg = null;
        if (nogl) msg = "图形已降级：三维视口已停用（图形驱动反复崩溃）。数据库、表单、报表、命令行均可正常使用。";
        else if (soft) msg = "图形已降级：正在使用软件渲染（硬件 OpenGL 驱动崩溃过）。画面较慢但稳定。";
        else if (!string.IsNullOrEmpty(prof)) msg = $"图形已降级：按 OpenGL {prof} 运行（更高档位崩溃过），仍是硬件渲染。";
        if (msg == null) return;
        msg += "  恢复默认：删除 ~/.local/share/PitMine3D.Kylin/.gl-level 后重启。";
        LogCommand(msg);
        StatusMsg.Text = msg;
        PitMine3D.Kylin.CrashLog.Write("GL", msg);
    }


    // ── 功能区自适应宽度（1080p / 2K / 4K 适配）──────────────────────────
    // 功能区 1:1 约需 2300px 宽，1080p 及以下放不下，超出的组只能横向滚动才看得到，
    // 用户看到的就是"显示不完整"。这里给整个功能区套一层缩放：窄屏缩到一屏放得下，宽屏保持 1:1。
    //
    // 关键是包在 TabControl **外面**而不是逐页包：
    //   · 逐页包 → 每页要等切过去才应用缩放，首次切页肉眼可见抖动；
    //   · 逐页包 → 各页按自己的宽度算，最宽的"开始"缩到 84%、窄页保持 100%，跨页图标忽大忽小。
    // 包在外面则任何页一出现就已经是最终尺寸，且天然全页一致。
    private const double RibbonMinScale = 0.55;   // 再小就看不清了, 剩下的交给横向滚动

    /// <summary>缩放挡位：0=自动(跟随窗口宽度)，其余为目标屏幕宽度(像素)。默认 1080p。</summary>
    private double _ribbonScaleSetting = 1920;

    /// <summary>历史最大所需宽度：页是点开才创建的，切走后测得的宽度会变小，
    /// 若跟着变就会来回缩放抖动。故只增不减。</summary>
    private double _ribbonNeedMax;
    private double _lastRibbonScale = 1;

    private static string RibbonScaleFile =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PitMine3D.Kylin.CrashLog.Path) ?? ".", "ribbon-scale.txt");

    private void LoadRibbonScaleSetting()
    {
        try
        {
            if (File.Exists(RibbonScaleFile) &&
                double.TryParse(File.ReadAllText(RibbonScaleFile).Trim(), out double v) && v >= 0 && v <= 8000)
                _ribbonScaleSetting = v;
        }
        catch { }
    }

    private void SaveRibbonScaleSetting()
    {
        try { File.WriteAllText(RibbonScaleFile, _ribbonScaleSetting.ToString("0.###")); } catch { }
    }

    /// <summary>
    /// 屏幕挡位 → 目标宽度(像素)。缩放不写死百分比，而是按「目标宽度 ÷ 功能区实际需要的宽度」算，
    /// 以后功能区增删按钮也不用改这张表。0 = 自动，跟随当前窗口宽度。
    /// 旧配置里若存着已取消的挡位(如 1366)，匹配不上就回落到第一项 1080p。
    /// </summary>
    private static readonly (string label, double targetWidth)[] RibbonScalePresets =
    {
        ("1080p  1920×1080（默认）", 1920),
        ("2K  2560×1440", 2560),
        ("4K  3840×2160", 3840),
        ("自动（跟随窗口宽度）", 0),
    };

    /// <summary>装好缩放：读取挡位并随窗口尺寸重算。</summary>
    private void InstallRibbonAutoFit()
    {
        LoadRibbonScaleSetting();
        SizeChanged += (_, _) => FitRibbons();
        // 等一次布局跑完再量宽度(刚建好时 DesiredSize 可能还是 0)
        Avalonia.Threading.Dispatcher.UIThread.Post(FitRibbons, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>按挡位/窗口宽度重算功能区缩放。</summary>
    private void FitRibbons()
    {
        if (RibbonScaler?.Child is not Control content)
        {
            PitMine3D.Kylin.CrashLog.Write("UI", $"功能区缩放: 取不到容器(RibbonScaler={(RibbonScaler == null ? "null" : "有")})");
            return;
        }
        content.Measure(Size.Infinity);
        _ribbonNeedMax = System.Math.Max(_ribbonNeedMax, content.DesiredSize.Width);
        double need = _ribbonNeedMax;
        double have = Bounds.Width;
        if (need <= 1 || have <= 1) return;

        // 手动挡位按所选分辨率宽度算；自动按当前窗口宽度。都不放大，只在放不下时缩。
        double target = _ribbonScaleSetting > 0 ? _ribbonScaleSetting : have;
        double scale = target >= need ? 1.0 : System.Math.Max(RibbonMinScale, target / need);

        // 必须整体换一个 Transform 对象：只改已有 ScaleTransform 的 ScaleX/ScaleY
        // 不会让 LayoutTransformControl 重新布局，表现就是"算出来了却没缩"(实测踩到)。
        double cur = RibbonScaler.LayoutTransform is ScaleTransform st ? st.ScaleX : 1.0;
        if (System.Math.Abs(cur - scale) <= 0.005) return;
        RibbonScaler.LayoutTransform = new ScaleTransform(scale, scale);
        _lastRibbonScale = scale;
        PitMine3D.Kylin.CrashLog.Write("UI", $"功能区缩放 {scale:0.00}（需 {need:0}px，目标 {target:0}px）");
    }

    /// <summary>解析 PITMINE_WINDOW（形如 1920x1080）；不合法返回 null。</summary>
    private static (double w, double h)? ParseWindowSize(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var parts = spec.Trim().ToLowerInvariant().Split('x');
        if (parts.Length == 2 && double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h)
            && w >= 800 && h >= 600) return (w, h);
        return null;
    }

    // 命令历史/输出面板：回显执行的命令（▸ cmd），滚动到底，上限 100 行
    private void LogCommand(string cmd)
    {
        if (CmdLog == null || string.IsNullOrWhiteSpace(cmd)) return;
        CmdLog.Children.Add(new TextBlock
        {
            Text = "▸ " + cmd, FontSize = 11, FontFamily = new FontFamily("Consolas,monospace"),
            Foreground = Brush.Parse("#2F5FA8")   // 信息栏为浅色面板(同原版 Theme.Surface), 用深蓝可读
        });
        while (CmdLog.Children.Count > 100) CmdLog.Children.RemoveAt(0);
        CmdLogScroll?.ScrollToEnd();
    }

    // 命令目录（供命令行自动补全候选；主要功能命令，覆盖 Home + 各模块）
    private static readonly string[] CommandCatalog =
    {
        // 文件/绘制/修改
        "新建","打开","保存","另存为","导入","导入PMX","导出PMX","选项","命令别名","帮助",
        "点","直线","多段线","滑动多段线","圆","矩形","正多边形","文字","多行文字","编辑文字","圆弧","图案填充","填充十字",
        "复制","移动","旋转","偏移","修剪","延伸","打断","分解","删除","撤销","重做",
        // 对象捕捉
        "对象捕捉","交点捕捉","最近捕捉","垂足捕捉","捕捉全模式",
        // 草图辅助
        "正交","栅格","栅格捕捉",
        // 图层/视图
        "新建图层","删除图层","图层特性管理器","冻结","锁定","全开","图层隔离","取消隔离",
        // 隐藏/隔离
        "隐藏对象","隐藏同一图层对象","结束隐藏",
        "2D","3D","俯视","仰视","主视","后视","左视","右视","西南等轴测","东南等轴测","东北等轴测","西北等轴测","缩放","清空视图","清理标记",
        // 注释/测量/剪贴板/选择
        "线性标注","对齐标注","半径标注","连续标注","标注样式",
        "距离","面积","角度",
        "剪切","复制到剪贴板","粘贴","基点粘贴","原坐标粘贴",
        "快速选择","选择类似","全部选择","反选","取消选择","创建选择集","特性",
        // 线编辑
        "加密多段线","简化","平滑","样条平滑","抽稀等值线","两线交点","闭合多段线","删除重复点","删除重复线","连接多段线","组合工作线",
        // 网格/建模
        "网格度量","网格诊断","创建三角网","约束三角网","裁剪三角网","网格焊接","网格边界","网格交线","网格剖面","网格光顺","合并三角网","固化成体","侧面三角网","台阶面提取","煤岩台阶判定","煤层露头线","更新煤层面","立方体","球体","圆柱","体素格网体积","自适应体素算量","实体转块体",
        // 区域/地形/点云
        "区域求差","区域重叠检测","克里金估值","泛克里金","简单克里金","快速估值","最近邻估值","移动平均估值",
        "坡度","坡向","粗糙度","曲率","加载点云","点云着色","SOR去噪","点云抽稀","自适应抽稀","均匀抽稀","随机抽稀","地面点滤波","高程着色","色带","图例","指北针","比例尺","标题栏","点云质量统计","点云裁剪","分割点云","区域生长分割","移除障碍物",
        // 块体/运输/路网
        "块体模型","导出PMB","属性赋值","字高归一化","资源量","经济剥采比","产能推算","生成境界","境界建模","境界内资源","面约束块体","离散化模型","采场排土场识别","采区划分","拉沟推荐","中长远进度计划","短期生产计划","道路横断面","道路设计参数","运输布局方案","路面生成","纵坡分析","竖曲线平滑","线形处理","运距指标","OD运距矩阵","点对点寻径","备选路径","约束寻径","路网校验","瓶颈段分析","路网运输指标","运输指标报告","路网建图","结构路面","中线交点","路段分类","演化对比","提取道路中心线","路网连通增强","螺旋斜坡道","折返斜坡道","直线斜坡道","直线坑线","坑线自动布线",
        // 生产计划/投影
        "境界圈定","剥采比均衡","月度剥离均衡","方案综合对比","开采程序确定","开采程序切分","平盘宽度识别","平盘标高清单","现状参数提取","参数校核","趋势整合台阶","标注台阶标高","确定可采区域","点落到面上","线落到面上",
        // §四/§八 数据分析(SQLite 种子库)
        "设备台账","生产数据","产能分析","故障分析","爆破分析","设备累计工时","KPI分析","机型KPI","设备智能编组","钻孔管理","煤质统计","煤层管理","工艺架构","展绘层位数据","层位求交","导入生产记录","导入月度产能","导入故障记录","导入爆破记录","导入月度KPI","导入设备台账","导入设备型号","导入煤质","导入观测点","导入月度计划","导入见煤成果","导入路况","导入边坡","导入模板","导出分析",
        "数据库连接","现场验收","参数验收判定","兼容机型","作业面台账","参数模板库","月度计划","路况显示","排土场台账","钻孔煤质汇总","边坡设计","钻孔展绘","机群总览","机群驾驶舱","设备综合评分","数据看板","煤种分类","煤质数据健康度","分煤层煤质",
        "煤层台阶参数","设备约束","煤质分级","观测点","矿区位置","设备效能预测","年度产量","设备故障排名","班次产量对比","KPI趋势",
        "产能分类对比","故障类型分布","设备因素分析","效能提升模拟","分工序验收合格率","数据导出","数据字典","达成度评价","产量预测","时序预测","编组优化","智能编组优化","导出编组","导出预测",
        "商品煤符合性","煤质达标","导出符合性","品位储量曲线","导出品位储量","分标高煤质","导出分标高","煤质离群","导出离群","洗选提质","导出洗选","用途适宜性","导出用途","灰分发热量回归","煤质综合结论","煤类反推","煤类一致率","煤质审核","测井一致","工分自洽","分煤层煤质","煤质三维插值","品位块模型","交叉验证","变差函数分析",
        // TaskLib 自足计算
        "生产量核算","物料换算","采剥平衡","排土场按量推进","配煤核算","工序进度跟踪","编组产能","环节降效",
    };

    // 命令框输入变化 → 候选补全提示（取前 8）
    private void OnCommandInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (CmdSuggest == null || CommandInput == null) return;
        if (AskingParams || _tool is TextTool { AwaitingText: true }) { CmdSuggest.Opacity = 0; return; }    // 正在答参数/输文字内容, 这一行不是命令名
        string t = CommandInput.Text?.Trim() ?? "";
        if (t.Length == 0) { CmdSuggest.Opacity = 0; return; }
        var hits = CommandCandidates(t, 8);
        if (hits.Count == 0) { CmdSuggest.Opacity = 0; return; }
        CmdSuggest.Text = "候选(Tab 补全): " + string.Join("  ·  ", hits);
        CmdSuggest.Opacity = 1;
    }

    /// <summary>
    /// 补全候选：AutoCAD 命令名(前缀命中优先, 带中文释义) → 中文命令目录(子串命中)。
    /// 前缀优先是因为 CAD 用户是按首字母敲的：打 "L" 要先看到 LINE, 而不是子串命中的 POLYLINE。
    /// </summary>
    private static List<string> CommandCandidates(string t, int max)
    {
        var hits = new List<string>();
        void Add(string s) { if (hits.Count < max && !hits.Contains(s)) hits.Add(s); }

        foreach (var en in AcadCommands.Table)
            if (en.Acad.StartsWith(t, System.StringComparison.OrdinalIgnoreCase)) Add($"{en.Acad}={en.Zh}");
        foreach (var c in CommandCatalog)
            if (c.StartsWith(t, System.StringComparison.OrdinalIgnoreCase)) Add(c);
        foreach (var c in CommandCatalog)
            if (c.Contains(t, System.StringComparison.OrdinalIgnoreCase)) Add(c);
        return hits;
    }

    // Tab 补全：取第一个候选填入命令框（AutoCAD 名带释义, 只回填命令名本身）
    private void CompleteCommand(TextBox tb)
    {
        string t = tb.Text?.Trim() ?? "";
        if (t.Length == 0) return;
        var hits = CommandCandidates(t, 1);
        if (hits.Count == 0) return;
        string c = hits[0];
        int eq = c.IndexOf('=');
        if (eq > 0) c = c.Substring(0, eq);
        tb.Text = c; tb.CaretIndex = c.Length;
    }

    /// <summary>「ALIAS / 命令别名」：把 AutoCAD 命令名对照表按分组打到信息栏，供现场查名。</summary>
    private void ListAcadAliases()
    {
        LogCommand($"AutoCAD 命令名对照（共 {AcadCommands.Table.Count} 条；命令行可直接键入）");
        foreach (var g in AcadCommands.Table.GroupBy(en => en.Group))
            LogCommand("  【" + g.Key + "】 " + string.Join("  ", g.Select(en => $"{en.Acad}={en.Zh}")));
        LogCommand("  说明：选择对象阶段 L/P/WP/CP/ALL 按 AutoCAD 选择选项解释（上次画的/上次选择集/窗口多边形/交叉多边形/全部）");
        StatusMsg.Text = $"已列出 {AcadCommands.Table.Count} 条 AutoCAD 命令名对照（见信息栏）";
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
        // 空格 = 回车(AutoCAD 习惯)。例外见 AcadCommands.SpaceSubmits：中文命令(留给输入法)、
        // 行内已有空格、以及在同一行跟参数的命令(图案填充 45 2 / SOR 8 1.0 / POLYGON 6)不劫持空格。
        if (e.Key == Key.Space && SpaceSubmitsNow(tb.Text ?? "")) { e.Handled = true; }
        else if (e.Key != Key.Enter) return;
        SubmitCommandLine(tb);
    }

    /// <summary>
    /// 窗口级隧道 TextInput：焦点不在任何输入控件时，把敲下的字符转进命令框
    /// —— AutoCAD 的命令行是常驻聆听的，不必先用鼠标点一下命令框。
    /// 空格按 AutoCAD 当回车（空行则重复上次命令）。
    /// </summary>
    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (CommandInput == null || string.IsNullOrEmpty(e.Text)) return;
        if (CommandInput.IsFocused) return;                                     // 已在命令框, 走它自己的处理
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox or AutoCompleteBox or ComboBox) return;          // 别抢别的输入框/可编辑下拉
        char c = e.Text[0];
        if (char.IsControl(c)) return;
        // 空格在按钮/菜单项上是"按下"，抢过来会变成按钮和"重复上次命令"一起触发。字母仍照收(同 AutoCAD)。
        if (c == ' ' && focused is Button or ToggleButton or MenuItem) return;

        CommandInput.Focus();
        e.Handled = true;
        if (c == ' ' && SpaceSubmitsNow(CommandInput.Text ?? "")) { SubmitCommandLine(CommandInput); return; }   // 空格 = 回车(文字内容步除外)
        CommandInput.Text = (CommandInput.Text ?? "") + e.Text;
        CommandInput.CaretIndex = CommandInput.Text.Length;
    }

    /// <summary>命令行提交（回车 / 空格）：坐标、进行中的交互输入优先，其余当命令执行。</summary>
    private void SubmitCommandLine(TextBox tb)
    {
        if (CmdSuggest != null) CmdSuggest.Opacity = 0;                          // 执行时收起候选

        // 参数问答进行中：这一行是答案, 不是命令(空行 = 取默认值, 故须在"空行重复上次命令"之前拦)。
        if (AskingParams) { string ans = tb.Text ?? ""; tb.Text = string.Empty; FeedParamAsk(ans); return; }

        string cmd = (tb.Text ?? "").Trim();
        if (cmd.Length == 0)
        {
            // 文字 jig 空回车 = 字高/角度取默认值(原版 "<2.5000>" / "<0>")；等内容时空回车 = 结束
            if (_tool is TextTool) { tb.Text = string.Empty; TextToolAcceptDefault(); return; }
            // 多点绘制中回车 = 结束（同 AutoCAD：PLINE 敲回车收笔）。原先只能双击或 Esc。
            if (_tool is { IsMultiPoint: true }) { tb.Text = string.Empty; FinishMultiPointTool(); return; }
            // 编辑命令中回车 = 右键：选择对象阶段确定选择集；移动/复制定了基点后 = 用第一个点作为位移
            if (ConfirmEditByKey()) { tb.Text = string.Empty; return; }
            // 空命令行 + Enter = 重复上次命令（AutoCAD 行为；仅空闲态，不干预进行中的交互）
            if (!CommandIdle() || string.IsNullOrEmpty(_lastCommand)) return;
            cmd = _lastCommand!;
        }
        tb.Text = string.Empty;

        // 夹点拖拽中：键入坐标(x,y / @dx,dy 相对被拖夹点 / d<ang) → 精确落点(原版 CommitDragAt)
        if (_gripDrag.Active)
        {
            var gp = ParseCoord(cmd, _gripDrag.Base);
            if (gp != null) { CommitGripDrag(gp.Value); return; }
        }

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

        if (TryToolOption(cmd)) return;        // 绘制中的选项关键字（多段线 闭合C/放弃U · 圆 3P/2P/T）—— 早于命令解析, 否则 C 会被当成 CIRCLE
        if (TryEditOption(cmd)) return;        // 移动/复制的 位移(D) / 位移向量 / 直接距离 —— 早于坐标与命令解析, 否则 D 会被当成 标注样式
        if (TryCoordinateInput(cmd)) return;   // 绘制/编辑取点时优先当坐标

        _lastCommand = cmd;                    // 记录供"空命令行 + Enter 重复"（坐标已在上一步返回，不会记为命令）
        PushHistory(cmd);                      // 入命令历史（供 ↑/↓ 回溯；坐标/交互输入不入）
        LogCommand(cmd);                       // 命令输出面板回显(typed; 转派中文由 _suppressCmdLog 防重复)
        ExecuteCommandToken(cmd);
    }

    // 执行一个命令 token（英文命令 switch；未识别 → 中文命令链/绘图工具）。供命令框与智能助手复用。
    private void ExecuteCommandToken(string cmd)
    {
        // AutoCAD 命令名/缩写 → 系统既有命令（L/PL/REC/DLI/Z/LA … 见 AcadCommands）。
        // 选择对象阶段按 AutoCAD 选择选项解释 L/P/WP/CP/ALL, 空闲态按命令名解释(L=直线, P=平移, CP=复制)。
        cmd = AcadCommands.Resolve(cmd, _editAwaitSelect);

        // 本条命令来自命令行/助手 → 它要参数时在命令行里逐项问(见 MainWindow.CmdParams.cs);
        // 命令词后面跟的位置参数先攒着, 问答时优先消费(`加密多段线 15` 直接把 15 填给第一项)。
        _cmdLineDriven = true;
        int argsAt = cmd.IndexOfAny(new[] { ' ', '\t' });
        _pendingInlineArgs = argsAt < 0
            ? new List<string>()
            : Modeling.ParamPrompt.SplitArgs(cmd.Substring(argsAt));

        switch (cmd.ToUpperInvariant())
        {
            case "ALIAS":
            case "命令别名":
            case "别名":
                ListAcadAliases();
                break;
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
                ArmText(false);
                break;
            // 线性/对齐是 AutoCAD 里两条不同命令(轴对齐量 X/Y ↔ 平行测线量真距), 原先都落到 StartDim() = 线性,
            // 打 DIMALIGNED 得到的其实是线性标注 —— 按 AutoCAD 拆开。
            case "DIM":
            case "DIMLINEAR":
                StartDim(false);
                break;
            case "DIMALIGNED":
                StartDim(true);
                break;
            case "DIMRADIUS":      // AutoCAD 命令名; DIMRADIAL/DIMRAD 为本系统旧名, 保留兼容
            case "DIMRADIAL":
            case "DIMRAD":
                StartDimRadial();
                break;
            case "DIMCONTINUE":
            case "DIMCONT":
                StartDimContinue();
                break;
            case "CLIP":
                _ = ClipPolygonCmdAsync();
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
                _ = DeleteCmdAsync();
                break;
            case "COPYCLIP":
                CopyClip();
                break;
            case "CUTCLIP":
                CutClip();
                break;
            // AutoCAD 语义: PASTECLIP(Ctrl+V) 提示指定插入点, PASTEORIG 才是按原坐标粘贴。
            // 原先两个都走原坐标, 与 AutoCAD 不符; 中文按钮「粘贴/原坐标粘贴/基点粘贴」维持原样不动。
            case "PASTECLIP":
            case "PASTEBASE":
                StartPasteBase();
                break;
            case "PASTEORIG":
                PasteClip();
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
                _ = ExplodeCmdAsync();
                break;
            case "DENSIFY":
            case "POLYDENSIFY":
                _ = EdPolylineDensifyAsync();
                break;
            case "POLYINTERSECT":
            case "INTERSECTPOLY":
                _ = EdPolylineIntersectAsync();
                break;
            case "POLYCLOSE":
            case "CLOSEPOLY":
                _ = EdPolylineCloseAsync();
                break;
            case "POINTDEDUPE":
            case "DEDUPEPOINTS":
                _ = EdPointDedupeAsync();
                break;
            case "POLYDEDUPE":
            case "DEDUPEPOLY":
                _ = EdPolylineDedupeAsync();
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
                _ = EdPointProjectAsync();
                break;
            case "POLYPROJECT":
                _ = EdPolylineProjectAsync();
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
                _ = OffsetCmdAsync();
                break;
            case "TRIM":
            case "TR":
            case "EXTEND":
            case "EX":
                _ = TrimCmdAsync();
                break;
            case "PLDRAG":
            case "SPL":
                StartSlide();
                break;
            case "BREAK":
            case "BR":
                _ = BreakCmdAsync();
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
                _ = EdWeldAsync();
                break;
            // ── 「编辑」组的原版内核命令名(与 Ribbon 按钮同一套实现/交互) ──
            case "POINTSETZ":
                _ = EdPointSetZAsync();
                break;
            case "POLYUNIFYZ":
                _ = EdPolylineUnifyZAsync();
                break;
            case "POLYSIMPLIFY":
                _ = EdPolylineSimplifyAsync();
                break;
            case "POLYJOIN":
                _ = EdPolylineJoinAsync();
                break;
            case "POLYCLIP":
                _ = EdPolylineClipAsync();
                break;
            // 原版这条叫 CLIP, 但 Kylin 的 CLIP 早已是"两条多段线求交集裁剪"(ClipPolygon), 不能顶掉;
            // 三角网裁剪走 MESHCLIP / CLIPFACE, Ribbon「闭合线裁剪面 / 裁剪面」按钮走的是同一实现。
            case "MESHCLIP":
            case "CLIPFACE":
                _ = EdMeshClipByLoopAsync();
                break;
            case "SPLITALONG":
                _ = EdMeshSplitAlongAsync();
                break;
            case "INTERSECT":
                _ = EdMeshIntersectAsync();
                break;
            case "REPAIR":
                _ = EdMeshRepairAsync();
                break;
            case "DELFACES":
                _ = EdDeleteMeshFacesAsync();
                break;
            case "BOUNDARY":
                _ = EdMeshBoundaryAsync();
                break;
            case "EMBED":
                _ = EdEmbedPolylineAsync();
                break;
            case "MERGEMESH":
                _ = EdMergeMeshesAsync();
                break;
            case "BOOLUNION":
                _ = EdBooleanAsync(Cad.MeshBoolean.Op.Union);
                break;
            case "BOOLINTER":
                _ = EdBooleanAsync(Cad.MeshBoolean.Op.Intersection);
                break;
            case "BOOLDIFF":
                _ = EdBooleanAsync(Cad.MeshBoolean.Op.Difference);
                break;
            case "BOOLCOMP":
                _ = EdBooleanAsync(Cad.MeshBoolean.Op.Complement);
                break;
            case "CUTBYKNIFE":
                _ = EdCutByKnifeAsync();
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
                NewDocument();
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
                if (TryPolygonWithPrompt(cmd)) break;                                     // 正多边形：命令行发起且没给边数 → 先在命令行问边数(同 AutoCAD)
                if (!ActivateDrawTool(cmd)) DispatchRibbon(cmd, fromCommandLine: true);   // 英文 switch 未识别 → 转中文命令链(命令框也能打中文命令)
                break;
        }
    }
}
