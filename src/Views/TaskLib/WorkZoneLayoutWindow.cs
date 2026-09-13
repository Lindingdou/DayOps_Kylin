// 忠实移植自原 PitMine3D Modules/TaskLib/Features/WorkZoneLayoutWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：WPF Canvas 鼠标事件 → Avalonia Pointer 事件；MessageBox → CoalMsgBox（async）；OpenFileDialog → StorageProvider；
// 文字白色投影（DropShadowEffect）→ 无（Avalonia 无同类效果）；DataGrid 行样式绑定 → 行属性（Brush）。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Data.Entities;    // ProcessZone（工序码值域 / 工序色）
using PitMine3D.Kylin.TaskLib.Zoning;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 作业区划分 —— 对着正射影像圈作业区，落 <c>mineable_region</c> 台账。
///
/// ── 为什么是这张表 ── 这张表已经是全项目共用的「作业区域」注册表：三维推演读它当推进轮廓、
/// 路网中心线提取读它当裁剪范围、短期计划的「采场/排土场圈定」也写它。
///
/// ── 三件本窗口负责守住的事 ──
///  ① **坐标是真的**：画布上的像素按影像四至换算成绝对世界坐标；没有配准的影像直接拒绝装载。
///  ② **Z 有出处**：落库前逐顶点从现状面三角网采 Z，采不到就要人填基准标高，两条都不成立**拒绝入库**。
///  ③ **关联当场可见**：区域能不能进推演取决于 类别极性 / 名字对不对得上源汇 / 顶点数 / Z，右下角逐条判给人看。
/// </summary>
public sealed class WorkZoneLayoutWindow : Window
{
    // ── 台账与诊断 ──
    private List<ZoneRecord> _zones = new();
    private readonly ObservableCollection<ZoneRow> _rows = new();
    private ZoneLinkReport _report = new();

    // ── 底图 ──
    private ZoneBasemap? _map;
    private bool _pushedTo3D;
    /// <summary>底图的 Image 元素，跨帧复用（见 <see cref="DrawBasemap"/>）。</summary>
    private Image? _imgEl;

    // ── 视图变换（世界 ↔ 画布像素）──
    private readonly ZoneView _view = new();

    // ── 框定 / 平移 ──
    private enum DrawMode { Rect, Polygon }

    private DrawMode _mode = DrawMode.Rect;
    private bool _drawing;
    private readonly List<(double X, double Y)> _drawPts = new();
    private Point? _rectFrom;
    private Point? _cursor;
    private bool _panning;
    private Point _panFrom;
    private double _panViewMinX, _panViewMaxY;

    private const string DefaultHint = "左键拖=平移 · 滚轮=缩放 · 「框定新区域」后拖框即入库";

    private ZoneElevation.TerrainProbe _terrain;

    // ── 本期候选 ──
    private ZonePlanResult? _plan;
    private ProcessZonePlanResult? _procPlan;
    private readonly List<(string Name, string Process, List<ZonePoint> Ring)> _storedProc = new();
    private readonly List<(string UnitId, double Done, double[] MinedXy, double[] LeftXy)> _progress = new();
    private readonly ObservableCollection<PropRow> _propRows = new();

    // ── 手工调整边界（Z12：只改几何）──
    private bool _editing;
    private int _dragVertex = -1;
    private bool _dragWhole;
    private (double X, double Y) _dragFrom;
    private List<ZonePoint> _dragOrigRing = new();
    private int _hoverVertex = -1, _hoverEdge = -1;
    private long _redrawTargetId;
    private long _undoId;
    private List<ZonePoint>? _undoRing;
    private string _undoLabel = "";
    private const double HitPx = 9.0;
    private bool _loaded;

    // ── 控件（与原 XAML x:Name 一一对应）──
    private readonly Button btnClearBasemap, btnPushTo3D;
    private Button btnDraw = null!, btnFinish = null!, btnCancelDraw = null!, btnPlan = null!, btnApplyPlan = null!, btnClearPlan = null!, btnProcPlan = null!, btnApplyProc = null!;
    private readonly Button btnApplyMeta, btnZoomTo, btnDelete, btnRedraw, btnUndoEdit, btnOffsetOut, btnOffsetIn;
    private readonly TextBlock basemapText = Hint("未载入底图 —— 没有影像也能划区（按已有区域定视野），但对着航拍图划才对得上地物");
    private readonly ComboBox cbCategory = new() { Width = 104, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox cbDrawMode = new() { Width = 96, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox txtBaseZ = new() { Width = 72, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock zHint = new() { Text = "留空=采现状面", FontSize = 11, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock drawHint = Hint("左键拖=平移 · 滚轮=缩放 · 「框定新区域」后拖框即入库");
    private readonly ComboBox cbMonth = new() { Width = 104, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox txtCell = new() { Width = 52, Margin = new Thickness(0, 0, 3, 0) };
    private readonly TextBlock planHint = Hint("按采矿模型 + 月度计划自动圈出本期作业区域 —— 生成后可逐块勾选入库，入库后仍可手工调整边界");
    private readonly TextBox txtLead = new() { Width = 40, Margin = new Thickness(0, 0, 3, 0), Text = "3" };
    private readonly TextBox txtGuard = new() { Width = 46, Margin = new Thickness(0, 0, 3, 0), Text = "300" };
    private readonly TextBox txtTip = new() { Width = 40, Margin = new Thickness(0, 0, 3, 0), Text = "25" };
    private readonly CheckBox chkShowProgress = new() { Content = "显示进度", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox chkShowProc = new() { Content = "显示已入库", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock procHint = Hint("入库后，任务编制的作业面就能从本期工序区派生出来 —— 不必再落到样例盘子");
    private readonly DataGrid zoneGrid = TaskUi.Grid(readOnly: true, single: true);
    private readonly Border planPanel;
    private readonly TextBlock planListHeader = new() { Text = "本期候选", FontWeight = FontWeight.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DataGrid planGrid = TaskUi.Grid(readOnly: true, single: true);
    private readonly AutoCompleteBox cbName = new() { FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0 };
    private readonly ComboBox cbEditCategory = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleButton btnEditGeom = new() { Content = "改顶点", MinWidth = 72, Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
    private readonly TextBox txtOffset = new() { Width = 56, Text = "10", Margin = new Thickness(0, 0, 4, 0) };
    private readonly Canvas mapCanvas = new() { ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)) };
    private readonly TextBlock linkHeader = new() { FontWeight = FontWeight.Bold, FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = "推演关联诊断" };
    private readonly TextBlock linkDetail = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0), Text = "选中左侧一块区域，这里逐条说明它会不会进推演、驱动它的是哪个源/汇。" };
    private readonly TextBlock coordText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 330, Margin = new Thickness(10, 0, 12, 0), TextAlignment = TextAlignment.Right, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI") };
    private readonly TextBlock toolStatus = Hint("");

    private static readonly string[] CategoryNames = { "采场", "外排土场", "内排土场", "未分类", "剥采工作帮", "排土工作帮" };

    public WorkZoneLayoutWindow()
    {
        Title = "作业区划分 — 日常生产组织";
        TaskUi.Place(this, 1320, 900);
        MinHeight = 660; MinWidth = 1120;

        var header = TaskUi.Header("作业区划分", "对着正射影像圈作业区 → 落 mineable_region 台账 → 三维推演/路网裁剪直接读同一张表");

        // ── 工具条 ①：底图 ──
        var t1 = new DockPanel();
        void L1(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); t1.Children.Add(c); }
        void R1(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Right); t1.Children.Add(c); }
        var lb = Btn("载入正射影像(TIFF)", OnLoadBasemap, 132, 6); ToolTip.SetTip(lb, "选一张带地理配准的 GeoTIFF（或同名 .tfw 世界文件）。画布上的点按影像四至换算成世界坐标入库。"); L1(lb);
        btnClearBasemap = Btn("清除底图", OnClearBasemap, 72, 6); btnClearBasemap.IsEnabled = false; L1(btnClearBasemap);
        btnPushTo3D = Btn("同步贴到三维地表", OnPushTo3D, 120, 16); btnPushTo3D.IsEnabled = false; ToolTip.SetTip(btnPushTo3D, "把同一张影像贴到三维地表（正射着色），让推演演示与这里划的区踩在同一张图上"); L1(btnPushTo3D);
        var fi = Btn("影像范围", FitImage, 72, 0); ToolTip.SetTip(fi, "视野对齐到影像四至"); R1(fi);
        var fa = Btn("充满", FitAll, 56, 6); ToolTip.SetTip(fa, "视野装下影像 + 全部区域"); R1(fa);
        R1(Btn("刷新", OnRefresh, 56, 6));
        basemapText.Margin = new Thickness(0, 0, 10, 0);
        t1.Children.Add(basemapText);
        var bar1 = TaskUi.Bar(t1, top: true);

        // ── 工具条 ②：三种产出各占一页 ──
        var tabMake = new TabControl { Margin = new Thickness(10, 6, 10, 0), Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        tabMake.Items.Add(new TabItem { Header = "① 手工圈画", Content = TabPage(BuildDrawTab()) });
        tabMake.Items.Add(new TabItem { Header = "② 作业区域 · 按月计划生成", Content = TabPage(BuildPlanTab()) });
        tabMake.Items.Add(new TabItem { Header = "③ 工序作业区", Content = TabPage(BuildProcTab()) });

        // ── 主区：清单 + 画布 ──
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("464,*"), Margin = new Thickness(10, 8, 10, 6) };

        var leftGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), MinHeight = 524 };
        leftGrid.RowDefinitions[1].MinHeight = 96;
        var lh = new DockPanel { Margin = new Thickness(10, 8, 8, 4) };
        var none = Small("全不选", () => SetAllActive(false), 58); DockPanel.SetDock(none, Avalonia.Controls.Dock.Right); lh.Children.Add(none);
        var all = Small("全选", () => SetAllActive(true), 48); all.Margin = new Thickness(0, 0, 4, 0); DockPanel.SetDock(all, Avalonia.Controls.Dock.Right); lh.Children.Add(all);
        var lt = new TextBlock { Text = "作业区域台账（mineable_region）", FontWeight = FontWeight.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        TaskUi.Theme(lt, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        lh.Children.Add(lt);
        Grid.SetRow(lh, 0); leftGrid.Children.Add(lh);

        zoneGrid.Margin = new Thickness(6, 0, 6, 6);
        // 选定 ≠ 显示：本列管「算不算数」（推演轮廓 + 路网裁剪的范围）
        zoneGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = TaskUi.Head("选定"), Width = new DataGridLength(42), SortMemberPath = nameof(ZoneRow.Active),
            CellTemplate = new FuncDataTemplate<ZoneRow>((_, _) =>
            {
                var cb = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
                cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ZoneRow.Active)) { Mode = BindingMode.OneWay });
                ToolTip.SetTip(cb, "勾上 = 本期纳入作业范围：进三维推演的推进轮廓，也进路网中心线提取的裁剪范围。\n去勾 = 台账里还在，但两处都不参与。");
                cb.IsCheckedChanged += (s, _) => OnActiveToggled(cb);
                return cb;
            }),
        });
        zoneGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "", Width = new DataGridLength(22),
            CellTemplate = new FuncDataTemplate<ZoneRow>((_, _) =>
            {
                var r = new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                r.Bind(Shape.FillProperty, new Binding(nameof(ZoneRow.ColorBrush)));
                return r;
            }),
        });
        // 名称是与推演对接的**键**（采场对源名、排土对汇名），必须给足宽度
        var nameCol = TaskUi.StyledCol<ZoneRow>("名称", nameof(ZoneRow.Name), 140, tipPath: nameof(ZoneRow.BindText), editable: false);
        nameCol.Width = new DataGridLength(1, DataGridLengthUnitType.Star); nameCol.MinWidth = 140;
        zoneGrid.Columns.Add(nameCol);
        zoneGrid.Columns.Add(TaskUi.TextCol("类别", nameof(ZoneRow.CategoryText), 76));
        zoneGrid.Columns.Add(TaskUi.TextCol("顶点", nameof(ZoneRow.PointText), 42));
        zoneGrid.Columns.Add(TaskUi.StyledCol<ZoneRow>("推演", nameof(ZoneRow.Verdict), 82, tipPath: nameof(ZoneRow.BindText), brushPath: nameof(ZoneRow.VerdictBrush), boldPath: nameof(ZoneRow.VerdictWeight), editable: false));
        zoneGrid.SelectionChanged += (_, _) => OnZoneSelected();
        Grid.SetRow(zoneGrid, 1); leftGrid.Children.Add(zoneGrid);

        // 本期候选（自动生成的结果）—— 生成前整块不占地方
        var pp = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        var ph = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var pn = Small("都不勾", () => SetAllProposals(false), 58); DockPanel.SetDock(pn, Avalonia.Controls.Dock.Right); ph.Children.Add(pn);
        var pa = Small("全勾", () => SetAllProposals(true), 48); pa.Margin = new Thickness(0, 0, 4, 0); DockPanel.SetDock(pa, Avalonia.Controls.Dock.Right); ph.Children.Add(pa);
        TaskUi.Theme(planListHeader, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        ph.Children.Add(planListHeader);
        Grid.SetRow(ph, 0); pp.Children.Add(ph);
        planGrid.MaxHeight = 140; planGrid.Margin = new Thickness(0);
        planGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = TaskUi.Head("入库"), Width = new DataGridLength(42),
            CellTemplate = new FuncDataTemplate<PropRow>((_, _) =>
            {
                var cb = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
                cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(PropRow.Selected)) { Mode = BindingMode.OneWay });
                cb.Bind(ToolTip.TipProperty, new Binding(nameof(PropRow.IssueText)));
                cb.IsCheckedChanged += (s, _) => OnProposalToggled(cb);
                return cb;
            }),
        });
        planGrid.Columns.Add(TaskUi.StyledCol<PropRow>("工序", nameof(PropRow.ProcText), 72, tipPath: nameof(PropRow.IssueText), editable: false));
        var pnc = TaskUi.StyledCol<PropRow>("名称", nameof(PropRow.Name), 110, tipPath: nameof(PropRow.IssueText), editable: false);
        pnc.Width = new DataGridLength(1, DataGridLengthUnitType.Star); pnc.MinWidth = 110;
        planGrid.Columns.Add(pnc);
        planGrid.Columns.Add(TaskUi.TextCol("面积万m²", nameof(PropRow.AreaText), 70));
        planGrid.Columns.Add(TaskUi.TextCol("单元", nameof(PropRow.BlockText), 42));
        planGrid.Columns.Add(TaskUi.StyledCol<PropRow>("入库方式", nameof(PropRow.ActionText), 84, tipPath: nameof(PropRow.IssueText), brushPath: nameof(PropRow.ActionBrush), editable: false));
        planGrid.SelectionChanged += (_, _) => OnProposalSelected();
        Grid.SetRow(planGrid, 1); pp.Children.Add(planGrid);
        planPanel = new Border { IsVisible = false, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6), Child = pp };
        TaskUi.Theme(planPanel, Border.BorderBrushProperty, "Theme.Surface.Border");
        Grid.SetRow(planPanel, 2); leftGrid.Children.Add(planPanel);

        // 选中区域
        var sel = new StackPanel();
        var st = new TextBlock { Text = "选中区域", FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(0, 0, 0, 5) };
        TaskUi.Theme(st, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        sel.Children.Add(st);
        var nd = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var nl = ToolLabel("名称"); nl.Width = 42; DockPanel.SetDock(nl, Avalonia.Controls.Dock.Left); nd.Children.Add(nl);
        ToolTip.SetTip(cbName, "下拉是当日盘子里的作业面名 / 去向名 —— 选一个即可与推演直接对上。\n自由输入也行，但名字对不上时该区在推演里整期不动。");
        nd.Children.Add(cbName);
        sel.Children.Add(nd);
        var cd = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var cl = ToolLabel("类别"); cl.Width = 42; DockPanel.SetDock(cl, Avalonia.Controls.Dock.Left); cd.Children.Add(cl);
        foreach (var s in CategoryNames) cbEditCategory.Items.Add(new ComboBoxItem { Content = s });
        cbEditCategory.SelectedIndex = 0;
        cd.Children.Add(cbEditCategory);
        sel.Children.Add(cd);
        var sb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        btnApplyMeta = Btn("应用", OnApplyMeta, 72, 6); btnApplyMeta.IsEnabled = false; sb.Children.Add(btnApplyMeta);
        btnZoomTo = Btn("定位", OnZoomToSelected, 62, 6); btnZoomTo.IsEnabled = false; sb.Children.Add(btnZoomTo);
        btnDelete = Btn("删除区域", OnDeleteZone, 82, 0); btnDelete.IsEnabled = false; sb.Children.Add(btnDelete);
        sel.Children.Add(sb);
        // 手工调整边界：只改几何，名称/类别/选定/id 一律不动。默认收起。
        var eg = new StackPanel();
        var eg1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        ToolTip.SetTip(btnEditGeom, "打开后在图上：拖顶点=移动 · 拖区域内部=整体平移 · Alt+点边=插点 · 右键顶点=删点 · Esc=退出");
        btnEditGeom.IsCheckedChanged += (_, _) => OnEditGeomToggled();
        eg1.Children.Add(btnEditGeom);
        btnRedraw = Btn("重画边界", OnRedrawZone, 82, 6); btnRedraw.IsEnabled = false; ToolTip.SetTip(btnRedraw, "按当前『方式』重新画一条边界替换它 —— 名称/类别/选定状态保留"); eg1.Children.Add(btnRedraw);
        btnUndoEdit = Btn("撤销调整", OnUndoEdit, 82, 0); btnUndoEdit.IsEnabled = false; ToolTip.SetTip(btnUndoEdit, "退回本窗口最近一次几何调整之前的边界（只留一步）"); eg1.Children.Add(btnUndoEdit);
        eg.Children.Add(eg1);
        var eg2 = new StackPanel { Orientation = Orientation.Horizontal };
        eg2.Children.Add(ToolLabel("等距"));
        ToolTip.SetTip(txtOffset, "正数=外扩，负数=内缩（m）。走推演那套等距偏移，缩过头会被拒绝而不是悄悄收成一个点。");
        eg2.Children.Add(txtOffset); eg2.Children.Add(ToolLabel("m"));
        btnOffsetOut = Btn("外扩", () => ApplyOffset(+1), 56, 4); btnOffsetOut.IsEnabled = false; eg2.Children.Add(btnOffsetOut);
        btnOffsetIn = Btn("内缩", () => ApplyOffset(-1), 56, 0); btnOffsetIn.IsEnabled = false; eg2.Children.Add(btnOffsetIn);
        eg.Children.Add(eg2);
        var exp = new Expander { IsExpanded = false, Padding = new Thickness(0, 4, 0, 0), FontSize = 11, Header = "手工调整边界（只改几何，名称/类别/选定不动）", Content = eg, HorizontalAlignment = HorizontalAlignment.Stretch };
        var expBox = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 0), Child = exp };
        TaskUi.Theme(expBox, Border.BorderBrushProperty, "Theme.Surface.Border");
        sel.Children.Add(expBox);
        var selBox = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 8), Child = sel };
        TaskUi.Theme(selBox, Border.BorderBrushProperty, "Theme.Surface.Border");
        Grid.SetRow(selBox, 3); leftGrid.Children.Add(selBox);

        var leftScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = leftGrid };
        var leftBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = leftScroll };
        TaskUi.Theme(leftBox, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(leftBox, Border.BorderBrushProperty, "Theme.Surface.Border");
        Grid.SetColumn(leftBox, 0); main.Children.Add(leftBox);

        mapCanvas.SizeChanged += (_, _) => OnCanvasSize();
        mapCanvas.PointerPressed += OnCanvasPressed;
        mapCanvas.PointerReleased += OnCanvasReleased;
        mapCanvas.PointerMoved += OnCanvasMove;
        mapCanvas.PointerWheelChanged += OnCanvasWheel;
        mapCanvas.PointerExited += (_, _) => OnCanvasLeave();
        var mapBox = new Border { Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = mapCanvas };
        TaskUi.Theme(mapBox, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(mapBox, Border.BorderBrushProperty, "Theme.Surface.Border");
        Grid.SetColumn(mapBox, 1); main.Children.Add(mapBox);

        // 推演关联诊断 —— ★ 定高 96：长文本走里面的滚动条，绝不再偷主区
        TaskUi.Theme(linkHeader, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        TaskUi.Theme(linkDetail, TextBlock.ForegroundProperty, "Theme.Text.Body");
        var diagSp = new StackPanel(); diagSp.Children.Add(linkHeader); diagSp.Children.Add(linkDetail);
        var diag = new Border { Margin = new Thickness(10, 0, 10, 6), Padding = new Thickness(12, 8), Height = 96, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = diagSp } };
        TaskUi.Theme(diag, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(diag, Border.BorderBrushProperty, "Theme.Surface.Border");

        // 状态栏
        var foot = new DockPanel();
        var close = Btn("关闭", Close, 70, 0); close.Height = 30; DockPanel.SetDock(close, Avalonia.Controls.Dock.Right); foot.Children.Add(close);
        TaskUi.Theme(coordText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        DockPanel.SetDock(coordText, Avalonia.Controls.Dock.Right); foot.Children.Add(coordText);
        toolStatus.Margin = new Thickness(0, 0, 10, 0);
        foot.Children.Add(toolStatus);
        var footBar = TaskUi.Bar(foot, top: false, padY: 8);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        Grid.SetRow(header, 0); Grid.SetRow(bar1, 1); Grid.SetRow(tabMake, 2); Grid.SetRow(main, 3); Grid.SetRow(diag, 4); Grid.SetRow(footBar, 5);
        root.Children.Add(header); root.Children.Add(bar1); root.Children.Add(tabMake); root.Children.Add(main); root.Children.Add(diag); root.Children.Add(footBar);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = root;

        zoneGrid.ItemsSource = _rows;
        planGrid.ItemsSource = _propRows;
        KeyDown += OnWindowKeyDown;
        Opened += (_, _) =>
        {
            _loaded = true;
            ProbeTerrain(); LoadMonths(); Reload();
            TryAutoProcessZones();      // 期次有台账就自动出工序区候选（只画不入库）
            // ★ 工程里配过影像就**开窗即装**，不必再点一次「载入正射影像」；没配过不弹文件框
            if (!TryAutoBasemap()) FitAll();
        };
    }

    // ── 三个页签的内容 ──

    private static Border TabPage(Control content)
    {
        var b = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(14, 8), Child = content };
        TaskUi.Theme(b, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(b, Border.BorderBrushProperty, "Theme.Surface.Border");
        return b;
    }

    private DockPanel BuildDrawTab()
    {
        var d = new DockPanel();
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); d.Children.Add(c); }
        L(ToolLabel("类别"));
        foreach (var s in CategoryNames) cbCategory.Items.Add(new ComboBoxItem { Content = s });
        cbCategory.SelectedIndex = 0;
        ToolTip.SetTip(cbCategory, "决定推进极性：采场/未分类=内缩，排土场=外扩。填反了轮廓会朝反方向动，推演不报错。");
        L(cbCategory);
        L(ToolLabel("方式"));
        foreach (var s in new[] { "矩形框选", "多边形" }) cbDrawMode.Items.Add(new ComboBoxItem { Content = s });
        cbDrawMode.SelectedIndex = 0;
        cbDrawMode.SelectionChanged += (_, _) => OnDrawModeChanged();
        ToolTip.SetTip(cbDrawMode, "矩形框选：按住左键拖一个框，松开即闭合入库（快，规整作业区用这个）。\n多边形：左键逐点点出边界，右键闭合（跟着地物边界走）。");
        L(cbDrawMode);
        L(ToolLabel("基准标高"));
        ToolTip.SetTip(txtBaseZ, "留空 = 逐顶点从现状面三角网采 Z（首选）。现状面不在图上时才填这里；\n不填也采不到就拒绝入库——写 0 会被推演当成台账实测高程。");
        L(txtBaseZ);
        TaskUi.Theme(zHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        L(zHint);
        btnDraw = Btn("框定新区域", OnStartDraw, 96, 6); L(btnDraw);
        btnFinish = Btn("完成", CommitDraw, 62, 6); btnFinish.IsEnabled = false; L(btnFinish);
        btnCancelDraw = Btn("取消", () => { EndDraw(); toolStatus.Text = "已取消框定（未入库）"; }, 62, 16); btnCancelDraw.IsEnabled = false; L(btnCancelDraw);
        d.Children.Add(drawHint);
        return d;
    }

    private DockPanel BuildPlanTab()
    {
        var d = new DockPanel();
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); d.Children.Add(c); }
        var ag = ToolLabel("自动生成"); ag.FontWeight = FontWeight.Bold;
        ToolTip.SetTip(ag, "范围来自【月度台账】里排到本期的采掘单元，形状来自【采矿模型】的真轨（没有真轨退台账长宽盒子）。\n一块区域 = 一个作业面本期要开采的那片地。");
        L(ag);
        L(ToolLabel("期次"));
        ToolTip.SetTip(cbMonth, "采掘单元台账目录下的期次（一个月一份 CSV）。只有【期次 = 本期】的行才算本期作业范围。");
        L(cbMonth);
        L(ToolLabel("格距"));
        ToolTip.SetTip(txtCell, "占地并集的栅格格距（m）。留空 = 按范围自适应。\n轮廓精度就是它：调小一点边界更贴合，代价是顶点更多。");
        L(txtCell);
        var m1 = ToolLabel("m"); m1.Margin = new Thickness(0, 0, 12, 0); L(m1);
        btnPlan = Btn("生成候选", OnGeneratePlan, 86, 6); ToolTip.SetTip(btnPlan, "只算不写库：候选画在图上、列在左侧，勾了才入库。"); L(btnPlan);
        btnApplyPlan = Btn("入库选中", OnApplyPlan, 86, 6); btnApplyPlan.IsEnabled = false; ToolTip.SetTip(btnApplyPlan, "同名的既有区域【只换边界】（名称/类别/选定/id 全保住）；没有同名的才新增。"); L(btnApplyPlan);
        btnClearPlan = Btn("清除候选", OnClearPlan, 72, 14); btnClearPlan.IsEnabled = false; L(btnClearPlan);
        d.Children.Add(planHint);
        return d;
    }

    private DockPanel BuildProcTab()
    {
        var d = new DockPanel();
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); d.Children.Add(c); }
        var pz = ToolLabel("工序作业区"); pz.FontWeight = FontWeight.Bold;
        ToolTip.SetTip(pz, "同一个作业面、同一个月，五道工序的区域【不是同一块地】。\n穿孔＝采装队列上前推超前期的那一段（免爆的面没有这一块）\n爆破警戒＝穿孔区外扩警戒半径，按【炮次】分组而不是按面\n采装＝本期单元占地并集\n排土·卸载＝排土幅靠坡顶线的前缘带（卡车）\n排土·推排＝全幅减去卸载带剩下的后方带（推土机）\n运输不出面状区域：它是线状的，真正需要地的是装车点与卸载点两个端点。");
        L(pz);
        L(ToolLabel("穿爆超前"));
        ToolTip.SetTip(txtLead, "工日。穿孔区＝把采装那一段沿排产序整体往前挪这么多天的量。\n硬岩大区爆破与煤层控制爆破的超前期本来就不一样，这里是全局缺省值。");
        L(txtLead);
        var u1 = ToolLabel("工日"); u1.Margin = new Thickness(0, 0, 10, 0); L(u1);
        L(ToolLabel("警戒半径"));
        ToolTip.SetTip(txtGuard, "m。GB6722 对个别飞散物的人员安全距离 ≥200m，露天矿一般取 300m。\n⚠ 飞石 / 振动 / 冲击波三个距离不是一个数 —— 这里要的是清场用的那个（最大的）。");
        L(txtGuard);
        var u2 = ToolLabel("m"); u2.Margin = new Thickness(0, 0, 10, 0); L(u2);
        L(ToolLabel("卸载带宽"));
        ToolTip.SetTip(txtTip, "m（车长 + 车挡 + 安全余量）。排土幅的推进宽不超过它时，\n整幅都是卸载带、【没有推排带】—— 那不是算法少切了一块，是推土机在这些幅上没有独立站位。");
        L(txtTip);
        var u3 = ToolLabel("m"); u3.Margin = new Thickness(0, 0, 12, 0); L(u3);
        btnProcPlan = Btn("生成工序区", OnGenerateProcessZones, 92, 6); ToolTip.SetTip(btnProcPlan, "只算不写库：候选画在图上、列在左侧，勾了才入库。"); L(btnProcPlan);
        btnApplyProc = Btn("入库选中", OnApplyProcessZones, 86, 14); btnApplyProc.IsEnabled = false; ToolTip.SetTip(btnApplyProc, "按【期次 + 工序 + 名字】三键入库：同一块只换几何，不新增。\n落在 process_zone 表，不动可采区域台账。"); L(btnApplyProc);
        ToolTip.SetTip(chkShowProgress, "把本期每个采掘单元按【期初完成度】切成两段画出来：\n　已采 = 实色　·　待采 = 淡色虚线\n切的是推进方向上的一段（按面积反求宽度），不是把占地环拦腰截一刀。\n⚠ 只有带真轨的单元切得出来 —— 盒子近似的单元切出来的带朝向是编的，一律不画。");
        chkShowProgress.IsCheckedChanged += (_, _) => { if (_loaded) ReloadProgress(); };
        L(chkShowProgress);
        ToolTip.SetTip(chkShowProc, "把本期【已入库】的工序作业区按工序色画到影像上（实线，比候选淡）。\n候选是虚线 —— 两者一眼分得开：一个还没进库，一个已经是本期的作业位置。");
        chkShowProc.IsCheckedChanged += (_, _) => { if (_loaded) ReloadStoredProc(); };
        L(chkShowProc);
        d.Children.Add(procHint);
        return d;
    }

    private static TextBlock ToolLabel(string t)
    {
        var l = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        TaskUi.Theme(l, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return l;
    }

    private static TextBlock Hint(string t)
    {
        var l = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        TaskUi.Theme(l, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        l.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) ToolTip.SetTip(l, l.Text); };
        return l;
    }

    private static Button Btn(string text, Action onClick, double minWidth, double right)
    {
        var b = TaskUi.Btn(text, onClick, minWidth); b.Margin = new Thickness(0, 0, right, 0);
        return b;
    }

    private static Button Small(string text, Action onClick, double minWidth)
    {
        var b = TaskUi.Btn(text, onClick, minWidth); b.Padding = new Thickness(6, 1); b.FontSize = 11; b.Margin = new Thickness(0);
        return b;
    }

    /// <summary>期次下拉：采掘单元台账目录里已有的那些期。</summary>
    private void LoadMonths()
    {
        var months = ZonePlanSource.Months();
        cbMonth.ItemsSource = months;
        string def = ZonePlanSource.DefaultMonth(out string note);
        cbMonth.SelectedItem = months.FirstOrDefault(m => string.Equals(m, def, StringComparison.OrdinalIgnoreCase));
        if (cbMonth.SelectedItem == null) cbMonth.PlaceholderText = def;
        if (note.Length > 0) planHint.Text = note;
        else if (months.Count > 0)
            planHint.Text = $"台账里有 {months.Count} 期（{string.Join("、", months.TakeLast(4))}）—— "
                          + "「生成候选」按该期的计划圈出本期作业区域，只算不写库。";
    }

    private string MonthText => (cbMonth.SelectedItem as string) ?? (cbMonth.PlaceholderText ?? "").Trim();

    /// <summary>现状面在不在图上，开窗就问清楚 —— 否则人对着影像框完一个区，才在入库那一刻被告知要填标高，白框一次。</summary>
    private void ProbeTerrain()
    {
        _terrain = ZoneElevation.Probe();
        zHint.Text = _terrain.Available ? "留空=采现状面" : "⚠ 必填（无现状面）";
        if (_terrain.Available) TaskUi.Theme(zHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        else zHint.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        ToolTip.SetTip(zHint, _terrain.Caption);
        ToolTip.SetTip(txtBaseZ, _terrain.Caption);
    }

    // ═══════════════════ 台账装载 ═══════════════════

    private void Reload()
    {
        long keepId = Selected?.Rec.Id ?? 0;

        _zones = ZoneStore.LoadAll();
        _report = ZoneLinkage.Diagnose(_zones);

        // Diagnose 逐块产出，顺序与 _zones 一一对应 —— 按序号配，不按名字（名字允许重复）
        _rows.Clear();
        for (int i = 0; i < _zones.Count; i++)
            _rows.Add(new ZoneRow(_zones[i], i < _report.Links.Count ? _report.Links[i] : null));

        linkHeader.Text = _report.Header
            + (_zones.Count > 0
                ? $"　|　{ScopeLabel()}：会进推演 {_report.OkCount} · 选定了但进不去 {_report.DeadCount}"
                  + (_report.InactiveCount > 0 ? $" · 未选定 {_report.InactiveCount}" : "")
                : "");

        if (keepId != 0)
        {
            var back = _rows.FirstOrDefault(r => r.Rec.Id == keepId);
            if (back != null) zoneGrid.SelectedItem = back;
        }
        if (zoneGrid.SelectedItem == null && _rows.Count > 0) zoneGrid.SelectedIndex = 0;

        RefreshNameCandidates();
        // 台账重读之后 _zones 是一批新对象：候选里挂的「同名既有」必须重新挂
        RebindProposals();
        ShowSelectedDetail();
        Redraw();

        toolStatus.Text = ZoneStore.LastLabel;
    }

    private void RefreshNameCandidates()
    {
        // Avalonia 的 AutoCompleteBox 换 ItemsSource 会把 Text 清掉：以选中行的名字为准回填，而不是信换源前读到的 Text
        string keep = Selected?.Rec.Name ?? cbName.Text ?? "";
        cbName.ItemsSource = _report.BindableNames;
        SetNameText(keep);
    }

    /// <summary>
    /// 往名称框写字。Avalonia 的 AutoCompleteBox 在 Text 被程序改动、且下拉源刚换过时会把值清回空
    /// （它把"非用户输入"的文本改动当成筛选状态重置），所以写完再排一拍补写一次。
    /// </summary>
    private void SetNameText(string text)
    {
        cbName.Text = text;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if ((cbName.Text ?? "") != text) cbName.Text = text; }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private ZoneRow? Selected => zoneGrid.SelectedItem as ZoneRow;

    private void OnZoneSelected()
    {
        var r = Selected;
        bool has = r != null;
        bool ring = has && r!.Rec.RingUsable;
        btnApplyMeta.IsEnabled = has;
        btnDelete.IsEnabled = has;
        btnZoomTo.IsEnabled = ring;
        btnRedraw.IsEnabled = has;
        btnEditGeom.IsEnabled = ring;
        btnOffsetOut.IsEnabled = ring;
        btnOffsetIn.IsEnabled = ring;
        if (!ring && _editing) { _editing = false; btnEditGeom.IsChecked = false; }
        if (r != null)
        {
            SetNameText(r.Rec.Name);
            cbEditCategory.SelectedIndex = CategoryIndex(r.Rec.Category);
        }
        ShowSelectedDetail();
        Redraw();
    }

    private void ShowSelectedDetail()
    {
        var r = Selected;
        if (r?.Link == null)
        {
            linkDetail.Text = _zones.Count == 0
                ? string.Join("\n", _report.Notes.DefaultIfEmpty(
                    "台账里一块区域都没有。载入正射影像后点「圈画新区域」开始划。"))
                : "选中左侧一块区域，这里逐条说明它会不会进推演、驱动它的是哪个源/汇。";
            return;
        }

        var lines = new List<string> { $"【{r.Rec.Name}】{r.Link.Verdict}　绑定：{r.Link.BindText}" };
        lines.AddRange(r.Link.Detail);
        if (_map != null)
        {
            double cov = _map.CoverageOf(r.Rec.Ring);
            lines.Add(cov >= 0.999 ? "◆ 底图：该区完整落在当前影像范围内。"
                    : cov <= 1e-9 ? "◆ 底图：该区**完全落在影像之外** —— 多半是影像与工程不在同一套坐标系。"
                    : $"◆ 底图：该区 {cov * 100:0}% 的顶点在影像范围内，其余出框。");
        }
        if (_report.Notes.Count > 0) lines.Add("— " + string.Join("　", _report.Notes));
        linkDetail.Text = string.Join("\n", lines);
    }

    // ═══════════════════ 底图 ═══════════════════

    private async void OnLoadBasemap()
    {
        // 工程里配过影像就直接用，不再弹框；配了却读不到时如实说一句，再退回选文件
        string path = Shading.OrthophotoConfig.ResolvePath(out string why);
        if (path.Length == 0)
        {
            if (Shading.OrthophotoConfig.HasPath) toolStatus.Text = why;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择正射影像（GeoTIFF）", AllowMultiple = false, FileTypeFilter = FilterOf(ZoneBasemapLoader.FileFilter),
            });
            if (files.Count == 0) return;
            path = files[0].Path.LocalPath;
            why = "本次选择（已记为工程配置）";
        }

        ApplyBasemap(path, why);
    }

    private static List<FilePickerFileType> FilterOf(string wpfFilter)
    {
        var list = new List<FilePickerFileType>();
        var parts = wpfFilter.Split('|');
        for (int i = 0; i + 1 < parts.Length; i += 2)
            list.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries) });
        return list;
    }

    /// <summary>开窗时按<b>工程配置</b>自动装底图。装上返回 true。<b>不弹文件框</b>。</summary>
    private bool TryAutoBasemap()
    {
        try
        {
            string path = Shading.OrthophotoConfig.ResolvePath(out string why);
            if (path.Length == 0)
            {
                if (Shading.OrthophotoConfig.HasPath) basemapText.Text = why;
                return false;
            }
            return ApplyBasemap(path, why);
        }
        catch { return false; }
    }

    /// <summary>装一张底图（手动选与开窗自动共用这一条）。</summary>
    private bool ApplyBasemap(string path, string why)
    {
        var old = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        ZoneBasemapResult res;
        try { res = ZoneBasemapLoader.Load(path); }
        finally { Cursor = old; }

        if (!res.Ok || res.Map == null)
        {
            basemapText.Text = res.Message;
            toolStatus.Text = res.Message + (res.Notes.Count > 0 ? "　|　" + string.Join("　", res.Notes) : "");
            return false;
        }

        _map = res.Map;
        btnClearBasemap.IsEnabled = true;
        btnPushTo3D.IsEnabled = true;

        var notes = new List<string>(res.Notes);
        notes.AddRange(ZoneBasemapLoader.Coverage(_map, _zones));

        notes.Insert(0, why);          // 这张图是从哪来的（配置 / 本次选的），第一条就说清
        basemapText.Text = _map.Message;
        toolStatus.Text = string.Join("　|　", notes);
        FitImage();
        return true;
    }

    private void OnClearBasemap()
    {
        _map = null;
        btnClearBasemap.IsEnabled = false;
        btnPushTo3D.IsEnabled = false;
        basemapText.Text = "未载入底图 —— 没有影像也能划区（按已有区域定视野），但对着航拍图划才对得上地物";
        if (_pushedTo3D)
        {
            try { toolStatus.Text = PitMine3D.Kylin.TaskLib.Adjust.OrthophotoBasemap.Clear(); } catch { }
            _pushedTo3D = false;
        }
        else toolStatus.Text = "底图已清除（平面画布）";
        Redraw();
    }

    /// <summary>把同一张影像贴到三维地表，让推演演示与这里划的区踩在同一张图上。</summary>
    private void OnPushTo3D()
    {
        if (_map == null) return;
        var old = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            var r = PitMine3D.Kylin.TaskLib.Adjust.OrthophotoBasemap.Load(_map.Path);
            _pushedTo3D = r.Ok;
            toolStatus.Text = r.Message + (r.Notes.Count > 0 ? "　|　" + string.Join("　", r.Notes) : "");
        }
        catch (Exception ex) { toolStatus.Text = $"贴三维地表失败：{ex.Message}"; }
        finally { Cursor = old; }
    }

    // ═══════════════════ 框定 ═══════════════════

    private void OnDrawModeChanged()
    {
        if (!_loaded) return;
        _mode = cbDrawMode.SelectedIndex == 1 ? DrawMode.Polygon : DrawMode.Rect;
        if (_drawing) { EndDraw(); toolStatus.Text = "已切换框定方式，本次未入库"; }
        else drawHint.Text = DefaultHint;
    }

    private void OnStartDraw()
    {
        if (!ZoneStore.Available)
        {
            toolStatus.Text = "作业区域台账不可用（GeoDataBase 未就绪）——框出来也存不进去，先不画。";
            return;
        }
        if (!_view.Ready)
        {
            toolStatus.Text = "画布还没有参照：先载入正射影像（或先有区域）再框定，"
                            + "否则框出来的坐标没有意义（画布像素换不出世界坐标）。";
            return;
        }
        // 没有现状面 + 没填标高 = 框完必被拒。与其让人白框一次，不如现在就拦下
        if (!_terrain.Available && (txtBaseZ.Text ?? "").Trim().Length == 0)
        {
            txtBaseZ.Focus();
            toolStatus.Text = _terrain.Caption;
            return;
        }
        _drawing = true;
        _drawPts.Clear();
        _rectFrom = null;
        btnDraw.IsEnabled = false;
        btnFinish.IsEnabled = _mode == DrawMode.Polygon;   // 矩形靠松开鼠标闭合，没有「完成」这一步
        btnCancelDraw.IsEnabled = true;
        drawHint.Text = _mode == DrawMode.Rect
            ? $"框定中（{ZoneStore.CategoryZh(DrawCategory)}）：按住左键拖一个框，松开即闭合入库；Esc 取消。"
            : $"框定中（{ZoneStore.CategoryZh(DrawCategory)}）：左键逐点点出边界，右键或「完成」闭合入库；Esc 取消。";
        Redraw();
    }

    private void EndDraw()
    {
        _drawing = false;
        _drawPts.Clear();
        _rectFrom = null;
        _redrawTargetId = 0;
        btnDraw.IsEnabled = !_editing;
        btnFinish.IsEnabled = false;
        btnCancelDraw.IsEnabled = false;
        drawHint.Text = DefaultHint;
        Redraw();
    }

    private string DrawCategory => CategoryKey(cbCategory.SelectedIndex);

    /// <summary>闭合入库。Z 解不出时**保留已画的点**（填完基准标高再点一次「完成」即可）。</summary>
    private void CommitDraw()
    {
        if (!_drawing) return;

        if (_drawPts.Count < 3)
        {
            toolStatus.Text = _mode == DrawMode.Rect
                ? "框太小（任一边不足 1 m）—— 拖一个真正的框，或 Esc 取消"
                : $"只点了 {_drawPts.Count} 个点，至少 3 个才能成区域（继续点，或 Esc 取消）";
            return;
        }

        double? manualZ = null;
        string t = (txtBaseZ.Text ?? "").Trim();
        if (t.Length > 0)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
            {
                btnFinish.IsEnabled = true;
                toolStatus.Text = $"基准标高「{t}」不是数字 —— 改对再点「完成」（框还在）";
                return;
            }
            manualZ = z;
        }

        var zr = ZoneElevation.Resolve(_drawPts, manualZ);
        if (!zr.Ok)
        {
            btnFinish.IsEnabled = true;
            toolStatus.Text = "未入库：" + zr.Message;
            linkDetail.Text = "未入库：" + zr.Message
                + $"\n框好的 {_drawPts.Count} 个顶点还在画布上 —— 在上方「基准标高」填一个标高后点「完成」即可入库。";
            return;
        }

        // ── 重画：换的是既有区域的边界，名称/类别/选定/id 一律不动（Z12）──
        if (_redrawTargetId != 0)
        {
            var target = _zones.FirstOrDefault(z => z.Id == _redrawTargetId);
            if (target == null)
            {
                EndDraw();
                toolStatus.Text = "要重画的那块区域已经不在台账里了（可能被别处删了），本次未入库。";
                return;
            }
            var rep = ZoneEdit.Replace(target.Ring, zr.Ring);
            if (!rep.Ok) { btnFinish.IsEnabled = true; toolStatus.Text = rep.Message; return; }

            EndDraw();
            CommitGeometry(target, new ZoneEditResult { Ok = true, Ring = rep.Ring, Message = rep.Message + "　|　" + zr.Message },
                           "重画边界", zr.Provenance);
            return;
        }

        string category = DrawCategory;
        string name = ZoneStore.NextName(category, _zones);

        // 重叠只提示不拦：现场确实存在采场压着老排土场的情形
        var cx = _drawPts.Average(p => p.X);
        var cy = _drawPts.Average(p => p.Y);
        var overlap = _zones.FirstOrDefault(z => z.RingUsable && ZoneStore.Contains(z.Ring, cx, cy));

        long id = ZoneStore.Insert(name, category, zr.Ring, zr.Provenance);
        if (id == 0)
        {
            toolStatus.Text = "入库失败：" + ZoneStore.LastLabel;
            return;
        }

        EndDraw();
        Reload();

        var back = _rows.FirstOrDefault(r => r.Rec.Id == id);
        if (back != null) { zoneGrid.SelectedItem = back; zoneGrid.ScrollIntoView(back, null); }

        var msg = new List<string> { $"已入库「{name}」（{zr.Ring.Count} 点，{ZoneStore.CategoryZh(category)}）", zr.Message };
        if (overlap != null) msg.Add($"⚠ 质心落在已有区域「{overlap.Name}」内，两块空间重叠 —— 推演按各自的量各推各的，不做互斥。");
        if (back?.Link is { Bind: ZoneBind.None })
            msg.Add("⚠ 这块区域现在**进不了推演**：名字对不上盘子里的源/汇。在左下「名称」下拉里选一个再「应用」。");
        toolStatus.Text = string.Join("　|　", msg);
    }

    // ═══════════════════ 改名 / 改类别 / 删除 ═══════════════════

    private void OnApplyMeta()
    {
        if (Selected is not { } row) return;
        string name = (cbName.Text ?? "").Trim();
        if (name.Length == 0) { toolStatus.Text = "名称不能为空"; return; }

        string category = CategoryKey(cbEditCategory.SelectedIndex);
        string oldName = row.Rec.Name, oldCat = row.Rec.Category;
        if (name == oldName && category == oldCat) { toolStatus.Text = "没有改动"; return; }

        if (!ZoneStore.UpdateMeta(row.Rec, name, category)) { toolStatus.Text = ZoneStore.LastLabel; return; }

        long id = row.Rec.Id;
        Reload();
        var back = _rows.FirstOrDefault(r => r.Rec.Id == id);
        if (back != null) zoneGrid.SelectedItem = back;

        var bits = new List<string>();
        if (name != oldName) bits.Add($"名称 {oldName} → {name}");
        if (category != oldCat) bits.Add($"类别 {ZoneStore.CategoryZh(oldCat)} → {ZoneStore.CategoryZh(category)}"
                                       + $"（推进极性 {(oldCat is "external_dump" or "internal_dump" ? "外扩" : "内缩")}"
                                       + $" → {(category is "external_dump" or "internal_dump" ? "外扩" : "内缩")}）");
        toolStatus.Text = "已更新：" + string.Join("；", bits)
                        + (back?.Link != null ? $"　|　推演：{back.Link.Verdict}（{back.Link.BindText}）" : "");
    }

    // ═══════════════════ 本期选定 ═══════════════════

    private void OnActiveToggled(CheckBox cb)
    {
        if (cb.DataContext is not ZoneRow row) return;
        bool want = cb.IsChecked == true;
        if (want == row.Rec.Active) return;              // 重绑时的回声，不是人点的

        if (!ZoneStore.SetActive(row.Rec, want))
        {
            cb.IsChecked = row.Rec.Active;               // 写失败就把勾退回去，不留假象
            toolStatus.Text = ZoneStore.LastLabel;
            return;
        }

        long keep = row.Rec.Id;
        Reload();
        var back = _rows.FirstOrDefault(r => r.Rec.Id == keep);
        if (back != null) zoneGrid.SelectedItem = back;
        toolStatus.Text = ZoneStore.LastLabel + "　|　" + ScopeLabel();
    }

    private async void SetAllActive(bool active)
    {
        var todo = _rows.Where(r => r.Rec.Active != active).Select(r => r.Rec).ToList();
        if (todo.Count == 0) { toolStatus.Text = active ? "已经全部选定" : "已经全部未选定"; return; }

        // 全不选 = 推演里一块区域都没有（会退到示意图形），这一步值得确认一次
        if (!active && !await TaskUi.Confirm(this, "作业区划分",
                $"把 {todo.Count} 块区域全部移出本期作业范围？\n\n"
                + "移出后三维推演拿不到任何真实边界，会退回**示意图形**（形状不代表真实位置），"
                + "路网中心线提取也不再受区域限制、改为全图提取。"))
        {
            Reload();   // 把已经被点动的勾恢复
            return;
        }

        int ok = 0;
        foreach (var z in todo) if (ZoneStore.SetActive(z, active)) ok++;
        Reload();
        toolStatus.Text = ok == todo.Count
            ? $"{(active ? "已全部选定" : "已全部移出作业范围")}（{ok} 块）　|　{ScopeLabel()}"
            : $"改了 {ok}/{todo.Count} 块，其余失败：{ZoneStore.LastLabel}";
    }

    /// <summary>"本期在哪几块作业"的一句话，改完选定就报一次。</summary>
    private string ScopeLabel()
    {
        int on = _zones.Count(z => z.Active), all = _zones.Count;
        if (all == 0) return "台账为空";
        if (on == 0) return "⚠ 一块都没选定 —— 推演退到示意图形，路网提取不限范围";
        return on == all
            ? $"本期作业范围：全部 {all} 块"
            : $"本期作业范围：{on}/{all} 块（{all - on} 块不进推演、不参与路网裁剪）";
    }

    private async void OnDeleteZone()
    {
        if (Selected is not { } row) return;
        if (!await TaskUi.Confirm(this, "作业区划分", $"删除区域「{row.Rec.Name}」？\n\n这块区域同时是三维推演的推进轮廓与路网提取的裁剪范围，"
                          + "删掉之后两处都会少一块。")) return;

        string nm = row.Rec.Name;
        if (!ZoneStore.Delete(row.Rec.Id)) { toolStatus.Text = ZoneStore.LastLabel; return; }
        Reload();
        toolStatus.Text = $"已删除「{nm}」";
    }

    // ═══════════════════ 按采矿模型 + 月度计划自动生成（只算不写库）═══════════════════

    private void OnGeneratePlan()
    {
        string month = MonthText;
        if (month.Length == 0)
        {
            planHint.Text = "先选一个期次 —— 没有期次就没有「本期要开采哪几块」这件事。";
            return;
        }

        double cell = 0;
        string ct = (txtCell.Text ?? "").Trim();
        if (ct.Length > 0)
        {
            if (!double.TryParse(ct, NumberStyles.Float, CultureInfo.InvariantCulture, out cell) || cell <= 0)
            {
                planHint.Text = $"格距「{ct}」不是正数 —— 留空即按范围自适应。";
                txtCell.Focus();
                return;
            }
        }

        var old = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        _procPlan = null;                    // 同一张候选表一次只装一种
        try { _plan = ZoneAutoPlanner.Plan(month, _zones, cell); }
        catch (Exception ex)
        {
            _plan = null;
            planHint.Text = $"生成失败（{ex.GetType().Name}：{ex.Message}）";
            return;
        }
        finally { Cursor = old; }

        FillProposals();

        if (!_plan.Ok)
        {
            planHint.Text = _plan.Header;
            linkDetail.Text = _plan.Header + "\n" + string.Join("\n", _plan.Notes);
            return;
        }

        planHint.Text = _plan.Header + "　|　" + _plan.Scope.RailLabel;
        linkDetail.Text = PlanDetailText();
        toolStatus.Text = $"候选已生成（未入库）：{_plan.Proposals.Count} 块，默认勾 {_plan.SelectedCount} 块";
        FitProposals();
    }

    /// <summary>候选表当前装的是哪一期（两种候选共用这张表）。</summary>
    private string ActiveMonth => _procPlan?.Period ?? _plan?.Month ?? "";

    /// <summary>两个「入库选中」按钮的可用性。<b>必须按候选的种类分别开</b>。</summary>
    private void SyncApplyButtons()
    {
        bool onZone = _plan != null && _propRows.Any(r => !r.IsProc && r.Selected);
        bool onProc = _procPlan != null && _propRows.Any(r => r.IsProc && r.Selected);
        btnApplyPlan.IsEnabled = onZone;
        btnApplyProc.IsEnabled = onProc;
    }

    private void FillProposals()
    {
        _propRows.Clear();
        if (_procPlan != null)
            foreach (var p in _procPlan.Proposals) _propRows.Add(new PropRow(p));
        else if (_plan != null)
            foreach (var p in _plan.Proposals) _propRows.Add(new PropRow(p));

        bool any = _propRows.Count > 0;
        planPanel.IsVisible = any;
        btnClearPlan.IsEnabled = any;
        SyncApplyButtons();
        planListHeader.Text = !any ? "本期候选"
            : $"本期候选（{ActiveMonth}{(_procPlan != null ? " · 工序作业区" : "")}）"
              + $"　{_propRows.Count} 块 · 勾选 {_propRows.Count(r => r.Selected)}";
        RefreshPlanGrid();
        Redraw();
    }

    private void RefreshPlanGrid()
    {
        var keep = planGrid.SelectedItem;
        planGrid.ItemsSource = null; planGrid.ItemsSource = _propRows;
        if (keep != null) planGrid.SelectedItem = keep;
    }

    private string PlanDetailText()
    {
        if (_plan == null) return "";
        var lines = new List<string> { _plan.Header };
        if (_plan.Scope.PlanLabel.Length > 0) lines.Add("· " + _plan.Scope.PlanLabel);
        if (_plan.Scope.RailLabel.Length > 0) lines.Add("· " + _plan.Scope.RailLabel);
        lines.AddRange(_plan.Notes);
        lines.Add("— 候选**还没有入库**：勾好之后点上方「入库选中」。同名的既有区域只换边界，"
                + "名称/类别/选定状态/id 全保住；没有同名的才新增。");
        return string.Join("\n", lines);
    }

    private void OnProposalSelected()
    {
        if (planGrid.SelectedItem is not PropRow row) { Redraw(); return; }
        if (row.Proc != null) { ShowProcDetail(row.Proc); Redraw(); return; }
        var p = row.Prop;
        var lines = new List<string>
        {
            $"【候选 {p.Name}】{ZoneStore.CategoryZh(p.Category)}　{p.ActionText}",
            $"① 范围：{p.Blocks.Count} 个本期单元（{string.Join("、", p.Blocks.Select(b => b.UnitId).Take(8))}"
              + (p.Blocks.Count > 8 ? " …" : "") + $"），合计 {p.VolumeM3 / 1e4:0.##} 万m³",
            $"② 分组：{(p.ByFace ? $"按作业面「{p.GroupName}」—— 推演按这个名字配源，天然对得上" : $"按台账的采场/排土场名「{p.GroupName}」兜底（这些单元没有作业面绑着）")}"
              + (p.PieceCount > 1 ? $"；本组共 {p.PieceCount} 块不相连的地，这是第 {p.PieceIndex} 块" : ""),
            $"③ 几何：{p.Ring.Count} 个顶点，占地 {p.AreaM2 / 1e4:0.##} 万m²（格距 {p.CellM:0.##} m，轮廓精度即此值）"
              + $"；真轨 {p.Blocks.Count(b => b.RealRail)} / 盒子 {p.Blocks.Count(b => !b.RealRail)}",
            $"④ 高程：{(p.Provenance.Length > 0 ? p.Provenance : "⚠ 解不出 —— 本块不会入库")}",
        };
        lines.AddRange(p.Issues.Select(s => "◆ " + s));
        linkDetail.Text = string.Join("\n", lines);
        Redraw();
    }

    private void OnProposalToggled(CheckBox cb)
    {
        if (cb.DataContext is not PropRow row) return;
        bool want = cb.IsChecked == true;
        if (want == row.Selected) return;

        if (want && !row.ZOk)
        {
            cb.IsChecked = false;
            toolStatus.Text = $"「{row.Name}」解不出高程，不能入库 —— 写 0 会被下游当成台账实测高程。";
            return;
        }
        row.Selected = want;
        SyncApplyButtons();
        planListHeader.Text = $"本期候选（{ActiveMonth}）　{_propRows.Count} 块 · 勾选 {_propRows.Count(r => r.Selected)}";
        Redraw();
    }

    private void SetAllProposals(bool on)
    {
        int blocked = 0;
        foreach (var r in _propRows)
        {
            if (on && !r.ZOk) { r.Selected = false; blocked++; continue; }
            r.Selected = on;
        }
        RefreshPlanGrid();
        SyncApplyButtons();
        planListHeader.Text = $"本期候选（{ActiveMonth}）　{_propRows.Count} 块 · 勾选 {_propRows.Count(r => r.Selected)}";
        toolStatus.Text = on
            ? $"已勾选 {_propRows.Count(r => r.Selected)} 块"
              + (blocked > 0 ? $"（{blocked} 块解不出高程，不能入库）" : "")
              + "　⚠ 同一组的多块都勾上时，推演会让每一块各吃下该面的全部量。"
            : "已全部取消勾选";
        Redraw();
    }

    private async void OnApplyPlan()
    {
        if (_plan == null) return;
        var chosen = _plan.Proposals.Where(p => p.Selected).ToList();
        if (chosen.Count == 0) { toolStatus.Text = "一块都没勾。"; return; }

        int upd = chosen.Count(p => p.Existing != null);
        var dup = chosen.GroupBy(p => p.GroupName).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        string warn = dup.Count > 0
            ? $"\n\n⚠ {string.Join("、", dup)} 有不止一块被勾上 —— 这些区域名字都能对上同一个源，"
            + "推演会让**每一块各吃下该面的全部量**，总量会重复计。确认要这样？"
            : "";
        if (!await TaskUi.Confirm(this, "按计划自动生成",
                $"把 {chosen.Count} 块候选写进作业区域台账？\n\n"
              + $"· 新增 {chosen.Count - upd} 块\n"
              + $"· 改既有区域的边界 {upd} 块（名称/类别/选定/id 不动）" + warn)) return;

        string msg = ZoneAutoPlanner.Apply(chosen, out int ins, out int updated, out var failures);
        Reload();
        RebindProposals();
        toolStatus.Text = msg;
        linkDetail.Text = msg
            + (failures.Count > 0 ? "\n没入库的：\n" + string.Join("\n", failures) : "")
            + "\n入库后左侧台账里的「推演」列会当场判这些区域能不能对上源/汇 —— 对不上时改名即可。";
    }

    /// <summary>入库后台账对象换了一批，候选的「同名既有」引用要重新挂，否则再点一次入库会重复新增。</summary>
    private void RebindProposals()
    {
        if (_plan == null) return;
        foreach (var p in _plan.Proposals)
            p.Existing = _zones.FirstOrDefault(z => string.Equals(z.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        RefreshPlanGrid();
    }

    private void OnClearPlan()
    {
        _plan = null;
        _procPlan = null;
        FillProposals();
        toolStatus.Text = "候选已清除（台账不受影响）";
        linkDetail.Text = "候选已清除。台账里的区域一块没动。";
    }

    // ═══════════════════ 工序作业区（五道工序 = 五块位置不同的地）═══════════════════

    /// <summary>开窗 / 换期次时<b>自动生成工序作业区候选并画到影像上</b>。只生成、不入库；失败一律安静。</summary>
    private void TryAutoProcessZones()
    {
        try
        {
            string month = MonthText;
            if (month.Length == 0) return;

            var opt = new ProcessZoneOptions();
            if (double.TryParse((txtLead.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lead) && lead >= 0)
                opt.BlastLeadDays = lead;
            if (double.TryParse((txtGuard.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double gr) && gr >= 0)
                opt.GuardRadiusM = gr;
            if (double.TryParse((txtTip.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double tw) && tw > 0)
                opt.TipBandWidthM = tw;

            var existing = ProcessZoneStore.LoadPeriod(month);
            _plan = null;
            _procPlan = ProcessZonePlanner.Plan(month, opt, existing);
            FillProposals();

            procHint.Text = _procPlan.Ok
                ? _procPlan.Header + "　（开窗自动生成的**候选**，还没入库 —— 勾选后点「入库选中」）"
                : _procPlan.Header;
            if (_procPlan.Ok) { linkDetail.Text = ProcDetailText(); FitProposals(); }
            ReloadProgress();       // 进度跟着期次走
        }
        catch (Exception ex)
        {
            procHint.Text = $"自动生成工序区跳过（{ex.GetType().Name}）—— 可手动点「生成工序区」。";
        }
    }

    private void OnGenerateProcessZones()
    {
        string month = MonthText;
        if (month.Length == 0)
        {
            procHint.Text = "先选一个期次 —— 工序作业区是按期的。";
            return;
        }
        if (!ReadNum(txtCell, "格距", 0, allowEmpty: true, out double cell)) return;
        if (!ReadNum(txtLead, "穿爆超前", 3, allowEmpty: false, out double lead)) return;
        if (!ReadNum(txtGuard, "警戒半径", 300, allowEmpty: false, out double guard)) return;
        if (!ReadNum(txtTip, "卸载带宽", 25, allowEmpty: false, out double tip)) return;

        var opt = new ProcessZoneOptions
        {
            CellM = cell, BlastLeadDays = lead, GuardRadiusM = guard, TipBandWidthM = tip,
        };

        var existing = ProcessZoneStore.LoadPeriod(month);

        var old = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            _plan = null;                       // 同一张候选表一次只装一种
            _procPlan = ProcessZonePlanner.Plan(month, opt, existing);
        }
        catch (Exception ex)
        {
            _procPlan = null;
            procHint.Text = $"生成失败（{ex.GetType().Name}：{ex.Message}）";
            return;
        }
        finally { Cursor = old; }

        FillProposals();

        if (!_procPlan.Ok)
        {
            procHint.Text = _procPlan.Header;
            linkDetail.Text = _procPlan.Header + "\n" + string.Join("\n", _procPlan.Notes);
            return;
        }
        procHint.Text = _procPlan.Header;
        linkDetail.Text = ProcDetailText();
        toolStatus.Text = $"工序区候选已生成（未入库）：{_procPlan.Proposals.Count} 块，"
                        + $"默认勾 {_procPlan.SelectedCount} 块";
        FitProposals();
    }

    private string ProcDetailText()
    {
        if (_procPlan == null) return "";
        var lines = new List<string> { _procPlan.Header };
        lines.AddRange(_procPlan.Notes);
        lines.Add("— 候选**还没有入库**：勾好之后点上方「入库选中」（工序作业区那一行的）。"
                + "入库落在 process_zone 表，按【期次 + 工序 + 名字】三键 upsert，"
                + "**不动可采区域台账** —— 那张表是长期存在的地（推演与路网裁剪读它），"
                + "工序区是按期的作业位置，两者不许互相顶替。");
        lines.Add("— 入库之后，任务编制的作业面就能从本期工序区派生出来，不必再落到样例盘子。");
        return string.Join("\n", lines);
    }

    /// <summary>选中一块工序区候选时的详情。</summary>
    private void ShowProcDetail(ProcessZoneProposal p)
    {
        var lines = new List<string>
        {
            $"【候选 {p.Name}】{p.ProcessZh}　{p.ActionText}"
              + (p.EquipRole.Length > 0 ? $"　派 {p.EquipRole}" : "　不派设备（禁入区）"),
            $"① 范围：{p.Blocks.Count} 个本期单元（{string.Join("、", p.Blocks.Select(b => b.UnitId).Take(8))}"
              + (p.Blocks.Count > 8 ? " …" : "") + "）"
              + (p.VolumeM3.HasValue ? $"，合计 {p.VolumeM3.Value / 1e4:0.##} 万m³（{p.Basis}）"
                                     : "，**本区没有量口径**（警戒区不谈量，给个 0 会被下游 SUM 进工程量）"),
            $"② 分组：{p.GroupKey}"
              + (p.PieceCount > 1 ? $"；本组共 {p.PieceCount} 块不相连的地，这是第 {p.PieceIndex} 块" : ""),
            $"③ 几何：{p.Ring.Count} 个顶点，占地 {p.AreaM2 / 1e4:0.##} 万m²"
              + $"（格距 {p.CellM:0.##} m，轮廓精度即此值）",
            $"④ 高程：{(p.ZSource.Length > 0 ? p.ZSource : "⚠ 解不出 —— 本块不会入库")}",
        };
        if (p.LeadDays.HasValue)
            lines.Add($"⑤ 穿爆超前 {p.LeadDays.Value:0.#} 工日 —— 这块地是采装队列上**往前挪了这么多天的量**的那一段。");
        if (p.GuardRadiusM.HasValue)
            lines.Add($"⑤ 警戒半径 {p.GuardRadiusM.Value:0.#} m（栅格膨胀，不是等距偏移）。");
        if (p.BandWidthM.HasValue)
            lines.Add($"⑤ 带宽 {p.BandWidthM.Value:0.#} m。");
        lines.AddRange(p.Issues.Select(x => "◆ " + x));
        linkDetail.Text = string.Join("\n", lines);
    }

    private async void OnApplyProcessZones()
    {
        if (_procPlan == null) return;
        var chosen = _procPlan.Proposals.Where(x => x.Selected).ToList();
        if (chosen.Count == 0) { toolStatus.Text = "一块都没勾。"; return; }

        int upd = chosen.Count(x => x.Existing != null);
        var byProc = chosen.GroupBy(x => x.ProcessZh).Select(g => $"{g.Key} {g.Count()}").ToList();
        if (!await TaskUi.Confirm(this, "工序作业区",
                $"把 {chosen.Count} 块工序作业区写进 {_procPlan.Period} 的工序区台账？\n\n"
              + $"· {string.Join(" · ", byProc)}\n"
              + $"· 新增 {chosen.Count - upd} 块 · 改边界 {upd} 块\n\n"
              + "落在 process_zone 表（V047），**不动可采区域台账** —— "
              + "那张表是长期存在的地，三维推演与路网裁剪读它；工序区是按期的作业位置，两者不互相顶替。")) return;

        string msg = ProcessZoneStore.Apply(_procPlan.Period, chosen, out int ins, out int updated, out var fails);

        // 入库后既有行换了一批，候选的「同名既有」引用要重挂，否则再点一次会当成新增
        var now = ProcessZoneStore.LoadPeriod(_procPlan.Period);
        foreach (var x in _procPlan.Proposals)
            x.Existing = now.FirstOrDefault(z =>
                string.Equals(z.Process, x.Process, StringComparison.OrdinalIgnoreCase)
             && string.Equals(z.Name, x.Name, StringComparison.OrdinalIgnoreCase));
        RefreshPlanGrid();

        ReloadStoredProc();      // 刚写进去的那批立刻画到影像上
        toolStatus.Text = msg;
        linkDetail.Text = msg
            + (fails.Count > 0 ? "\n没入库的：\n" + string.Join("\n", fails) : "")
            + "\n\n接下来：任务编制的作业面会**从本期工序区派生**（采装区 → 采装面、排土卸载区 → 排土面），"
            + "不必再落到样例盘子。已在「作业面台账」建过档的面**原样保留**，派生只补台账里没有的那些。";
    }

    /// <summary>读一个正数参数。空且允许为空时给 0；不合法时把焦点放回去并说清楚。</summary>
    private bool ReadNum(TextBox box, string label, double dflt, bool allowEmpty, out double val)
    {
        val = 0;
        string t = (box.Text ?? "").Trim();
        if (t.Length == 0)
        {
            if (allowEmpty) return true;
            val = dflt;
            box.Text = dflt.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out val) || val < 0)
        {
            procHint.Text = $"{label}「{t}」不是非负数。";
            box.Focus();
            return false;
        }
        return true;
    }

    private void FitProposals()
    {
        if (_propRows.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in _propRows)
            foreach (var v in p.Ring)
            {
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
            }
        if (minX > maxX) return;
        double padX = Math.Max(30, (maxX - minX) * 0.12), padY = Math.Max(30, (maxY - minY) * 0.12);
        Fit(minX - padX, minY - padY, maxX + padX, maxY + padY);
    }

    // ═══════════════════ 手工调整边界（Z12：只改几何）═══════════════════

    private void OnEditGeomToggled()
    {
        bool want = btnEditGeom.IsChecked == true;
        if (want && Selected is null)
        {
            btnEditGeom.IsChecked = false;
            return;
        }
        if (want && !Selected!.Rec.RingUsable)
        {
            btnEditGeom.IsChecked = false;
            toolStatus.Text = "这块区域顶点不足 3 个，先「重画边界」再调整。";
            return;
        }
        if (want && _drawing) { EndDraw(); }

        _editing = want;
        _dragVertex = -1; _dragWhole = false;
        drawHint.Text = _editing
            ? $"改顶点中（{Selected!.Rec.Name}）：拖顶点=移动 · 拖区域内部=整体平移 · Alt+左键点边=插点 · 右键顶点=删点 · Esc 退出。松手即落库。"
            : DefaultHint;
        btnDraw.IsEnabled = !_editing;
        Redraw();
    }

    private void OnRedrawZone()
    {
        if (Selected is not { } row) return;
        if (_editing) { btnEditGeom.IsChecked = false; }
        _redrawTargetId = row.Rec.Id;
        OnStartDraw();
        if (!_drawing) { _redrawTargetId = 0; return; }   // 起不来（台账不可用/无参照）就别留着目标
        drawHint.Text = $"重画「{row.Rec.Name}」的边界（{(_mode == DrawMode.Rect ? "拖框" : "逐点点，右键闭合")}）："
                      + "画完替换它的边界，名称/类别/选定状态保留；Esc 取消。";
    }

    private void ApplyOffset(int sign)
    {
        if (Selected is not { } row) return;
        string t = (txtOffset.Text ?? "").Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d <= 0)
        {
            toolStatus.Text = $"等距距离「{t}」不是正数。";
            txtOffset.Focus();
            return;
        }

        var res = ZoneEdit.OffsetRing(row.Rec.Ring, sign * d);
        if (!res.Ok) { toolStatus.Text = res.Message; return; }
        CommitGeometry(row.Rec, res, $"等距{(sign > 0 ? "外扩" : "内缩")} {d:0.##}m");
    }

    private void OnUndoEdit()
    {
        if (_undoRing == null || _undoId == 0) return;
        var rec = _zones.FirstOrDefault(z => z.Id == _undoId);
        if (rec == null)
        {
            toolStatus.Text = "要撤销的那块区域已经不在台账里了。";
            ClearUndo();
            return;
        }
        var back = _undoRing;
        ClearUndo();     // 先清，免得撤销本身又被记成一步
        if (!ZoneStore.UpdateRing(rec, back)) { toolStatus.Text = ZoneStore.LastLabel; return; }

        long keep = rec.Id;
        Reload();
        SelectById(keep);
        toolStatus.Text = "已撤销上一次几何调整。";
    }

    /// <summary>落库 + 记一步撤销 + 刷新。所有手工调整都走它。</summary>
    private void CommitGeometry(ZoneRecord rec, ZoneEditResult res, string what, string? absoluteProvenance = null)
    {
        var before = rec.Ring.ToList();
        string prov = absoluteProvenance ?? (res.Provenance.Length > 0 ? rec.ZProvenance + res.Provenance : "");
        if (!ZoneStore.UpdateRing(rec, res.Ring, prov, $"手工调整 {DateTime.Now:yyyy-MM-dd HH:mm}｜{what}"))
        {
            toolStatus.Text = ZoneStore.LastLabel;
            return;
        }

        _undoId = rec.Id; _undoRing = before; _undoLabel = what;
        btnUndoEdit.IsEnabled = true;
        ToolTip.SetTip(btnUndoEdit, $"退回「{rec.Name}」{_undoLabel} 之前的边界（{before.Count} 个顶点）—— 只留这一步");

        long keep = rec.Id;
        Reload();
        SelectById(keep);
        toolStatus.Text = res.Message;
    }

    private void ClearUndo()
    {
        _undoId = 0; _undoRing = null; _undoLabel = "";
        btnUndoEdit.IsEnabled = false;
        ToolTip.SetTip(btnUndoEdit, "退回本窗口最近一次几何调整之前的边界（只留一步）");
    }

    private void SelectById(long id)
    {
        var back = _rows.FirstOrDefault(r => r.Rec.Id == id);
        if (back != null) { zoneGrid.SelectedItem = back; zoneGrid.ScrollIntoView(back, null); }
    }

    /// <summary>命中半径：屏幕 <see cref="HitPx"/> 像素换算成米（缩放变了半径跟着变）。</summary>
    private double HitTolM => _view.Scale > 1e-9 ? HitPx / _view.Scale : 1.0;

    // ═══════════════════ 视图 ═══════════════════

    private void OnRefresh()
    {
        Reload();
        toolStatus.Text = "已刷新　|　" + ZoneStore.LastLabel;
    }

    private void OnZoomToSelected()
    {
        if (Selected is not { } row || !row.Rec.RingUsable) return;
        var (a, b, c, d) = row.Rec.Bounds();
        double padX = Math.Max(20, (c - a) * 0.15), padY = Math.Max(20, (d - b) * 0.15);
        Fit(a - padX, b - padY, c + padX, d + padY);
    }

    private void FitAll()
    {
        var bb = ContentBounds();
        if (bb == null) { Redraw(); return; }
        Fit(bb.Value.MinX, bb.Value.MinY, bb.Value.MaxX, bb.Value.MaxY);
    }

    private void FitImage()
    {
        if (_map == null) { FitAll(); return; }
        Fit(_map.MinX, _map.MinY, _map.MaxX, _map.MaxY);
    }

    private (double MinX, double MinY, double MaxX, double MaxY)? ContentBounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Acc(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        if (_map != null) { Acc(_map.MinX, _map.MinY); Acc(_map.MaxX, _map.MaxY); }
        foreach (var z in _zones)
            foreach (var p in z.Ring) Acc(p.X, p.Y);
        foreach (var q in _propRows)
            foreach (var p in q.Ring) Acc(p.X, p.Y);
        if (minX > maxX) return null;
        if (maxX - minX < 1) { minX -= 50; maxX += 50; }
        if (maxY - minY < 1) { minY -= 50; maxY += 50; }
        return (minX, minY, maxX, maxY);
    }

    private void Fit(double minX, double minY, double maxX, double maxY)
    {
        SyncViewport();
        if (_view.Fit(minX, minY, maxX, maxY)) Redraw();
    }

    private void SyncViewport()
    {
        _view.SetViewport(mapCanvas.Bounds.Width, mapCanvas.Bounds.Height);
        _view.ImageSpanM = _map == null ? 0 : Math.Max(_map.MaxX - _map.MinX, _map.MaxY - _map.MinY);
    }

    private double Sx(double wx) => _view.Sx(wx);
    private double Sy(double wy) => _view.Sy(wy);
    private double Wx(double sx) => _view.Wx(sx);
    private double Wy(double sy) => _view.Wy(sy);

    private void OnCanvasSize()
    {
        SyncViewport();
        if (!_view.Ready) FitAll(); else Redraw();
    }

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_view.Ready) return;
        var p = e.GetPosition(mapCanvas);
        if (_view.ZoomAt(p.X, p.Y, e.Delta.Y > 0 ? 1.18 : 1 / 1.18)) Redraw();
        e.Handled = true;
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(mapCanvas).Properties;
        if (props.IsRightButtonPressed) { OnCanvasRightDown(e); return; }
        if (!props.IsLeftButtonPressed) return;
        if (!_view.Ready) return;
        var p = e.GetPosition(mapCanvas);

        if (_drawing && _mode == DrawMode.Polygon)
        {
            _drawPts.Add((Wx(p.X), Wy(p.Y)));
            drawHint.Text = $"框定中（{ZoneStore.CategoryZh(DrawCategory)}）：已点 {_drawPts.Count} 点，"
                          + "右键或「完成」闭合入库，Esc 取消。";
            Redraw();
            return;
        }

        if (_drawing && _mode == DrawMode.Rect)
        {
            // 拖框：按下定起点，抬起闭合。中途已有未入库的框（上次 Z 没解出）就丢掉重来。
            _rectFrom = p;
            _cursor = p;
            _drawPts.Clear();
            btnFinish.IsEnabled = false;
            e.Pointer.Capture(mapCanvas);
            Redraw();
            return;
        }

        // ── 改顶点模式：拖顶点 / Alt+点边插点 / 拖区域内部整体平移 ──
        if (_editing && Selected is { } er && er.Rec.RingUsable)
        {
            double wx = Wx(p.X), wy = Wy(p.Y);
            double tol = HitTolM;

            int vi = ZoneEdit.HitVertex(er.Rec.Ring, wx, wy, tol);
            if (vi >= 0)
            {
                BeginDrag(er.Rec, vi, false, wx, wy, e);
                return;
            }

            // Alt + 点边 = 在这条边上插一个点，并直接拖它
            if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
            {
                int ei = ZoneEdit.HitEdge(er.Rec.Ring, wx, wy, tol);
                if (ei >= 0)
                {
                    var ins = ZoneEdit.InsertVertex(er.Rec.Ring, ei, wx, wy);
                    if (!ins.Ok) { toolStatus.Text = ins.Message; return; }
                    CommitGeometry(er.Rec, ins, "插入顶点");
                    var again = Selected;
                    if (again != null) BeginDrag(again.Rec, ei + 1, false, wx, wy, e);
                    return;
                }
            }

            if (ZoneStore.Contains(er.Rec.Ring, wx, wy))
            {
                BeginDrag(er.Rec, -1, true, wx, wy, e);
                return;
            }
            // 落在区域外：不算编辑，照常平移视图
        }

        _panning = true;
        _panFrom = p;
        _panViewMinX = _view.MinX;
        _panViewMaxY = _view.MaxY;
        e.Pointer.Capture(mapCanvas);
        mapCanvas.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>起拖：记住原环（拖动过程中只改内存里的副本，松手才落库）。</summary>
    private void BeginDrag(ZoneRecord rec, int vertex, bool whole, double wx, double wy, PointerPressedEventArgs e)
    {
        _dragVertex = vertex;
        _dragWhole = whole;
        _dragFrom = (wx, wy);
        _dragOrigRing = rec.Ring.ToList();
        e.Pointer.Capture(mapCanvas);
        mapCanvas.Cursor = new Cursor(whole ? StandardCursorType.SizeAll : StandardCursorType.Cross);
    }

    private bool Dragging => _dragVertex >= 0 || _dragWhole;

    private void OnCanvasMove(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(mapCanvas);

        // ── 拖动中：只改内存里的环，画面跟手；松手才落库 ──
        if (Dragging && Selected is { } dr)
        {
            double wx = Wx(p.X), wy = Wy(p.Y);
            if (_dragWhole)
            {
                double dx = wx - _dragFrom.X, dy = wy - _dragFrom.Y;
                dr.Rec.Ring = _dragOrigRing.Select(q => new ZonePoint(q.X + dx, q.Y + dy, q.Z)).ToList();
                coordText.Text = $"平移 ΔX {dx:+0.##;-0.##;0}　ΔY {dy:+0.##;-0.##;0}　"
                               + $"{Math.Sqrt(dx * dx + dy * dy):0.##} m";
            }
            else if (_dragVertex >= 0 && _dragVertex < dr.Rec.Ring.Count)
            {
                var keepZ = _dragOrigRing[Math.Min(_dragVertex, _dragOrigRing.Count - 1)].Z;
                var ring = dr.Rec.Ring.ToList();
                ring[_dragVertex] = new ZonePoint(wx, wy, keepZ);
                dr.Rec.Ring = ring;
                coordText.Text = $"顶点 {_dragVertex + 1}/{ring.Count}　X {wx:0.##}　Y {wy:0.##}　"
                               + $"面积 {ZoneEdit.Area(ring) / 1e4:0.##} 万m²";
            }
            Redraw();
            return;
        }

        // ── 改顶点模式的悬停高亮 ──
        if (_editing && !_drawing && Selected is { } hr && hr.Rec.RingUsable && _view.Ready)
        {
            double wx = Wx(p.X), wy = Wy(p.Y), tol = HitTolM;
            int hv = ZoneEdit.HitVertex(hr.Rec.Ring, wx, wy, tol);
            int he = hv >= 0 ? -1 : ZoneEdit.HitEdge(hr.Rec.Ring, wx, wy, tol);
            if (hv != _hoverVertex || he != _hoverEdge)
            {
                _hoverVertex = hv; _hoverEdge = he;
                mapCanvas.Cursor = new Cursor(hv >= 0 ? StandardCursorType.SizeAll : StandardCursorType.Arrow);
                Redraw();
            }
        }

        if (_panning)
        {
            _view.PanFrom(_panViewMinX, _panViewMaxY, p.X - _panFrom.X, p.Y - _panFrom.Y);
            Redraw();
            UpdateCoordReadout(p);
            return;
        }

        if (_drawing && (_rectFrom != null || _drawPts.Count > 0)) { _cursor = p; Redraw(); }
        UpdateCoordReadout(p);
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        // 改顶点：松手才落库
        if (Dragging)
        {
            e.Pointer.Capture(null);
            mapCanvas.Cursor = new Cursor(StandardCursorType.Arrow);
            bool whole = _dragWhole;
            int vi = _dragVertex;
            _dragWhole = false; _dragVertex = -1;

            if (Selected is not { } row) { Reload(); return; }
            var moved = row.Rec.Ring.ToList();
            row.Rec.Ring = _dragOrigRing;                    // 先退回原环，由 ZoneEdit 走正规入口

            ZoneEditResult res;
            if (whole)
            {
                var (cx0, cy0) = Center(_dragOrigRing);
                var (cx1, cy1) = Center(moved);
                res = ZoneEdit.Translate(_dragOrigRing, cx1 - cx0, cy1 - cy0);
            }
            else if (vi >= 0 && vi < moved.Count && vi < _dragOrigRing.Count)
            {
                // 只按下没挪 = 不落库
                var o = _dragOrigRing[vi];
                if (Math.Abs(moved[vi].X - o.X) < 1e-6 && Math.Abs(moved[vi].Y - o.Y) < 1e-6)
                { Redraw(); return; }
                res = ZoneEdit.MoveVertex(_dragOrigRing, vi, moved[vi].X, moved[vi].Y);
            }
            else res = ZoneEditResult.Fail("没有拖动。");

            if (!res.Ok) { toolStatus.Text = res.Message; Redraw(); return; }
            CommitGeometry(row.Rec, res, whole ? "整体平移" : $"移动第 {vi + 1} 个顶点");
            return;
        }

        // 拖框松手 = 闭合入库
        if (_drawing && _mode == DrawMode.Rect && _rectFrom is { } from)
        {
            var to = e.GetPosition(mapCanvas);
            e.Pointer.Capture(null);
            _rectFrom = null;

            var rect = _view.RectFromDrag(from.X, from.Y, to.X, to.Y);
            if (rect.Count < 4)
            {
                toolStatus.Text = "框太小（任一边不足 1 m）—— 重新拖一个框，或 Esc 取消";
                Redraw();
                return;
            }
            _drawPts.Clear();
            _drawPts.AddRange(rect);
            CommitDraw();
            return;
        }

        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        mapCanvas.Cursor = new Cursor(StandardCursorType.Arrow);
    }

    private void OnCanvasRightDown(PointerPressedEventArgs e)
    {
        // 改顶点模式：右键顶点 = 删这个点
        if (_editing && !_drawing && Selected is { } row && row.Rec.RingUsable && _view.Ready)
        {
            var p = e.GetPosition(mapCanvas);
            int vi = ZoneEdit.HitVertex(row.Rec.Ring, Wx(p.X), Wy(p.Y), HitTolM);
            if (vi >= 0)
            {
                var res = ZoneEdit.DeleteVertex(row.Rec.Ring, vi);
                if (!res.Ok) toolStatus.Text = res.Message;
                else CommitGeometry(row.Rec, res, $"删除第 {vi + 1} 个顶点");
                e.Handled = true;
                return;
            }
        }

        if (!_drawing || _mode != DrawMode.Polygon) return;
        CommitDraw();
        e.Handled = true;
    }

    private void OnCanvasLeave()
    {
        coordText.Text = "";
        if (_cursor != null && _rectFrom == null) { _cursor = null; Redraw(); }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_drawing)
        {
            bool wasRedraw = _redrawTargetId != 0;
            EndDraw();
            toolStatus.Text = wasRedraw ? "已取消重画（原边界没动）" : "已取消框定（未入库）";
            e.Handled = true;
            return;
        }

        if (_editing)
        {
            if (Dragging && Selected is { } row) { row.Rec.Ring = _dragOrigRing; }   // 拖到一半按 Esc = 放弃这一拖
            _dragVertex = -1; _dragWhole = false;
            btnEditGeom.IsChecked = false;                 // 触发 OnEditGeomToggled 收尾
            toolStatus.Text = "已退出改顶点";
            e.Handled = true;
        }
    }

    /// <summary>光标世界坐标。写 coordText 而不是 toolStatus —— 后者要留住「已入库 / 未入库因为…」那类话。</summary>
    private void UpdateCoordReadout(Point p)
    {
        if (!_view.Ready) { coordText.Text = ""; return; }
        double wx = Wx(p.X), wy = Wy(p.Y);
        string cov = _map == null ? "" : _map.Covers(wx, wy) ? "" : "　⚠影像外";

        // 拖框中：读数换成「框多大」
        if (_drawing && _mode == DrawMode.Rect && _rectFrom is { } from)
        {
            double w = Math.Abs(wx - Wx(from.X)), h = Math.Abs(wy - Wy(from.Y));
            coordText.Text = $"框 {w:0.#} × {h:0.#} m　{w * h / 1e4:0.##} 万m²{cov}";
            return;
        }
        coordText.Text = $"X {wx:0.##}　Y {wy:0.##}　{_view.MetersPerPixel:0.###} m/px{cov}";
    }

    // ═══════════════════ 绘制 ═══════════════════

    private void Redraw()
    {
        var c = mapCanvas;
        c.Children.Clear();
        double W = c.Bounds.Width, H = c.Bounds.Height;
        if (W < 4 || H < 4) return;

        if (!_view.Ready)
        {
            AddText(c, 18, 18,
                _map == null && _zones.Count == 0
                    ? "还没有可显示的内容。\n\n点上方「载入正射影像(TIFF)」选一张带地理配准的航拍图，\n再点「框定新区域」在影像上拖一个框。"
                    : "画布尺寸不足。",
                TaskUi.Muted, 13);
            return;
        }

        DrawBasemap(c);
        DrawPlanBlocks(c);
        DrawZones(c);
        DrawProgress(c);
        DrawStoredProcessZones(c);
        DrawProposals(c);
        DrawInProgress(c);
        DrawEditHandles(c);
        DrawScaleBar(c, W, H);
    }

    /// <summary>本期块段的占地（候选的"原料"）—— 画在最底下当底衬。</summary>
    private void DrawPlanBlocks(Canvas c)
    {
        if (_plan == null || _plan.Scope.Blocks.Count == 0) return;
        foreach (var b in _plan.Scope.Blocks)
        {
            int n = b.FootprintXy.Length / 2;
            if (n < 3) continue;
            var col = FromRgb(PitMine3D.Kylin.TaskLib.Simulation.UnitSolidStage.BaseRgbOfKind(b.Kind));
            var pts = new AvaloniaList<Point>();
            for (int i = 0; i < n; i++)
                pts.Add(new Point(Sx(b.FootprintXy[i * 2]), Sy(b.FootprintXy[i * 2 + 1])));
            c.Children.Add(new Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, col.R, col.G, col.B)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x90, col.R, col.G, col.B)),
                StrokeThickness = b.RealRail ? 0.9 : 0.7,
                // 盒子近似画虚线：一眼看出哪几块是"没有真轨、按长宽摆的"
                StrokeDashArray = b.RealRail ? null : new AvaloniaList<double> { 2, 2 },
            });
        }
    }

    /// <summary>本期【已入库】的工序作业区 —— 实线、按工序色、比候选淡。</summary>
    private void DrawStoredProcessZones(Canvas c)
    {
        foreach (var (name, proc, ring) in _storedProc)
        {
            if (ring.Count < 3) continue;
            var col = FromRgb(ProcessZone.Color(proc));
            var pts = new AvaloniaList<Point>(ring.Select(v => new Point(Sx(v.X), Sy(v.Y))));
            c.Children.Add(new Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(Color.FromArgb(0x1E, col.R, col.G, col.B)),
                Stroke = new SolidColorBrush(col),
                StrokeThickness = 1.8,
            });
        }
    }

    /// <summary>装本期已入库的工序区。期次变了 / 入库之后调它。</summary>
    private void ReloadStoredProc()
    {
        _storedProc.Clear();
        if (chkShowProc.IsChecked != true) { Redraw(); return; }

        string month = MonthText;
        if (month.Length == 0) { Redraw(); return; }
        try
        {
            foreach (var z in ProcessZoneStore.LoadPeriod(month))
            {
                if (z.Active == 0) continue;
                var ring = ProcessZoneStore.ParseRing(z);
                if (ring.Count >= 3) _storedProc.Add((z.Name, z.Process, ring));
            }
            procHint.Text = _storedProc.Count > 0
                ? $"{month} 已入库 {_storedProc.Count} 块工序作业区（实线，按工序色）"
                : $"{month} 还没有已入库的工序作业区 —— 先「生成工序区」再勾选入库";
        }
        catch (Exception ex) { procHint.Text = $"已入库工序区读不到（{ex.GetType().Name}）"; }
        Redraw();
    }

    /// <summary>本期作业进度 —— 每个单元按期初完成度切成「已采 / 待采」两段。<b>只有带真轨的单元切得出来</b>。</summary>
    private void ReloadProgress()
    {
        _progress.Clear();
        if (chkShowProgress.IsChecked != true) { Redraw(); return; }

        string month = MonthText;
        if (month.Length == 0) { Redraw(); return; }

        int noRail = 0, done0 = 0;
        try
        {
            var scope = ZonePlanSource.Load(month);
            foreach (var b in scope.Blocks)
            {
                if (!b.RealRail || b.Crest.Length < 6 || b.Toe.Length < 6 || !(b.WidthM > 1e-6))
                { noRail++; continue; }
                if (b.Done <= 1e-9) { done0++; }

                double v = Math.Max(1e-9, b.VolumeM3);
                double mined = v * Math.Clamp(b.Done, 0, 1);
                var parts = new List<(string, double)>();
                if (mined > 1e-9) parts.Add(("已采", mined));
                if (v - mined > 1e-9) parts.Add(("待采", v - mined));
                if (parts.Count == 0) continue;

                var split = UnitLedger.TaskZoneSplitter.Split(b.Crest, b.Toe, b.WidthM, b.TowardCrest, parts);
                if (!split.Ok) continue;

                var m = split.Slices.FirstOrDefault(x => x.Key == "已采");
                var l = split.Slices.FirstOrDefault(x => x.Key == "待采");
                _progress.Add((b.UnitId, b.Done,
                               m?.RingXy ?? Array.Empty<double>(),
                               l?.RingXy ?? Array.Empty<double>()));
            }

            int shown = _progress.Count;
            procHint.Text = shown > 0
                ? $"{month} 进度：{shown} 个单元画出「已采/待采」"
                  + (noRail > 0 ? $"　◆ {noRail} 个没有真轨，切不出带（盒子近似的朝向是编的，不画）" : "")
                  + (done0 == shown ? "　· 全部单元完成度都是 0 —— 那是**还没开始**，不是画错了" : "")
                : $"{month} 一个单元都画不出进度"
                  + (noRail > 0 ? $"（{noRail} 个没有真轨）" : "（本期没有块段）");
        }
        catch (Exception ex) { procHint.Text = $"进度算不出（{ex.GetType().Name}）"; }
        Redraw();
    }

    /// <summary>画进度：已采实色、待采淡色虚线。</summary>
    private void DrawProgress(Canvas c)
    {
        foreach (var (unitId, done, minedXy, leftXy) in _progress)
        {
            AddRingPoly(c, minedXy, Color.FromRgb(0xC0, 0x39, 0x2B), fillA: 0x66, dash: false);
            AddRingPoly(c, leftXy, Color.FromRgb(0x94, 0x9A, 0xA3), fillA: 0x18, dash: true);

            // 进度标签只给完成度 > 0 的画
            var src = minedXy.Length >= 6 ? minedXy : leftXy;
            if (src.Length < 6 || done <= 1e-9) continue;
            double cx = 0, cy = 0; int n = src.Length / 2;
            for (int i = 0; i < n; i++) { cx += Sx(src[i * 2]); cy += Sy(src[i * 2 + 1]); }
            AddText(c, cx / n + 4, cy / n - 12, $"{unitId} {done * 100:0}%",
                    new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), 11);
        }
    }

    /// <summary>扁平 [x,y,…] → 画一块。</summary>
    private void AddRingPoly(Canvas c, double[] xy, Color col, byte fillA, bool dash)
    {
        if (xy == null || xy.Length < 6) return;
        var pts = new AvaloniaList<Point>();
        for (int i = 0; i + 1 < xy.Length; i += 2) pts.Add(new Point(Sx(xy[i]), Sy(xy[i + 1])));
        var poly = new Polygon
        {
            Points = pts,
            Fill = new SolidColorBrush(Color.FromArgb(fillA, col.R, col.G, col.B)),
            Stroke = new SolidColorBrush(col),
            StrokeThickness = dash ? 1.0 : 1.6,
        };
        if (dash) poly.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        c.Children.Add(poly);
    }

    private void DrawProposals(Canvas c)
    {
        var focus = planGrid.SelectedItem as PropRow;

        foreach (var p in _propRows)
        {
            if (p.Ring.Count < 3) continue;
            bool on = p.Selected;
            bool isFocus = ReferenceEquals(p, focus);
            var col = on ? FromRgb(p.ColorRgb) : Color.FromRgb(0x94, 0x9A, 0xA3);

            var pts = new AvaloniaList<Point>(p.Ring.Select(v => new Point(Sx(v.X), Sy(v.Y))));
            c.Children.Add(new Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(Color.FromArgb(on ? (byte)0x30 : (byte)0x12, col.R, col.G, col.B)),
                Stroke = new SolidColorBrush(col),
                StrokeThickness = isFocus ? 3.2 : on ? 2.2 : 1.4,
                StrokeDashArray = new AvaloniaList<double> { 6, 3 },
            });
            if (isFocus) foreach (var v in p.Ring) AddVertex(c, Sx(v.X), Sy(v.Y), col);

            // ★ 标签**放不下就不画**：按屏幕尺寸判（宽不到一个标签宽约 90px 就不画）；当前选中的那一块永远画
            if (!isFocus)
            {
                if (!on) continue;
                double w = p.Ring.Max(v => Sx(v.X)) - p.Ring.Min(v => Sx(v.X));
                double h = p.Ring.Max(v => Sy(v.Y)) - p.Ring.Min(v => Sy(v.Y));
                if (w < 90 || h < 16) continue;
            }

            var ct = new Point(p.Ring.Average(v => Sx(v.X)), p.Ring.Average(v => Sy(v.Y)));
            AddText(c, ct.X + 6, ct.Y + 6,
                    $"候选 {p.Name}{(p.ProcText.Length > 0 ? "·" + p.ProcText : "")}"
                  + $"　{p.AreaM2 / 1e4:0.##}万m²",
                    new SolidColorBrush(col), isFocus ? 12.5 : 11.5, bold: isFocus);
        }
    }

    /// <summary>改顶点模式的把手：顶点方块 + 悬停高亮 + 边中点的"可插点"提示。</summary>
    private void DrawEditHandles(Canvas c)
    {
        if (!_editing || Selected is not { } row || !row.Rec.RingUsable) return;
        var ring = row.Rec.Ring;
        var col = FromRgb(ZoneStore.CategoryColor(row.Rec.Category));

        if (_hoverEdge >= 0 && _hoverEdge < ring.Count && _dragVertex < 0 && !_dragWhole)
        {
            var a = ring[_hoverEdge];
            var b = ring[(_hoverEdge + 1) % ring.Count];
            AddLine(c, Sx(a.X), Sy(a.Y), Sx(b.X), Sy(b.Y),
                    new SolidColorBrush(Color.FromArgb(0xCC, col.R, col.G, col.B)), 4.5, null);
            AddText(c, (Sx(a.X) + Sx(b.X)) / 2 + 8, (Sy(a.Y) + Sy(b.Y)) / 2 - 18,
                    "Alt+左键 在这条边上插点", new SolidColorBrush(col), 11.5);
        }

        for (int i = 0; i < ring.Count; i++)
        {
            bool hot = i == _hoverVertex || i == _dragVertex;
            double s = hot ? 11 : 8;
            var r = new Rectangle
            {
                Width = s, Height = s,
                Fill = new SolidColorBrush(hot ? Color.FromRgb(0xDC, 0x26, 0x26) : col),
                Stroke = Brushes.White,
                StrokeThickness = 1.4,
            };
            Canvas.SetLeft(r, Sx(ring[i].X) - s / 2);
            Canvas.SetTop(r, Sy(ring[i].Y) - s / 2);
            c.Children.Add(r);
        }
    }

    private static (double X, double Y) Center(IReadOnlyList<ZonePoint> ring)
    {
        if (ring.Count == 0) return (0, 0);
        return (ring.Average(p => p.X), ring.Average(p => p.Y));
    }

    private void DrawBasemap(Canvas c)
    {
        if (_map == null) return;
        double x0 = Sx(_map.MinX), y0 = Sy(_map.MaxY);
        double w = (_map.MaxX - _map.MinX) * _view.Scale, h = (_map.MaxY - _map.MinY) * _view.Scale;
        if (w <= 0 || h <= 0) return;
        // 完全在视野外就不摆这个元素
        if (x0 + w < -4 || y0 + h < -4 || x0 > c.Bounds.Width + 4 || y0 > c.Bounds.Height + 4) return;

        // 复用同一个 Image 元素：圈画时每次鼠标移动都会 Redraw，每帧新建一个挂着 4096² 位图的元素开销不小
        _imgEl ??= new Image { Stretch = Stretch.Fill };
        if (!ReferenceEquals(_imgEl.Source, _map.Image)) _imgEl.Source = _map.Image;
        _imgEl.Width = w;
        _imgEl.Height = h;
        Canvas.SetLeft(_imgEl, x0);
        Canvas.SetTop(_imgEl, y0);
        c.Children.Add(_imgEl);
    }

    private void DrawZones(Canvas c)
    {
        var sel = Selected?.Rec;
        foreach (var z in _zones)
        {
            if (z.Ring.Count < 2) continue;
            bool isSel = ReferenceEquals(z, sel);
            // 未选定的画成**灰的**：图上一眼看出本期在哪几块作业
            var col = z.Active ? FromRgb(ZoneStore.CategoryColor(z.Category))
                               : Color.FromRgb(0x94, 0x9A, 0xA3);

            var pts = new AvaloniaList<Point>(z.Ring.Select(p => new Point(Sx(p.X), Sy(p.Y))));
            byte alpha = !z.Active ? (byte)0x12 : isSel ? (byte)0x50 : (byte)0x2A;
            var poly = new Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)),
                Stroke = new SolidColorBrush(col),
                StrokeThickness = isSel ? 2.6 : 1.5,
            };
            // 虚线 = 进不了推演：未选定，或几何不成立（顶点 < 3）
            if (!z.RingUsable || !z.Active) poly.StrokeDashArray = new AvaloniaList<double> { 3, 3 };
            c.Children.Add(poly);

            if (isSel)
                foreach (var p in z.Ring) AddVertex(c, Sx(p.X), Sy(p.Y), col);

            var ct = new Point(z.Ring.Average(p => Sx(p.X)), z.Ring.Average(p => Sy(p.Y)));
            string tag = z.Name
                       + (z.Active ? "" : "（未选定）")
                       + (z.RingUsable ? "" : "（顶点不足）");
            if (Selected?.Link is { } lk && isSel && z.Active && lk.Bind == ZoneBind.None) tag += " ⚠不进推演";
            AddText(c, ct.X + 6, ct.Y - 8, tag, new SolidColorBrush(col), isSel ? 13 : 12, bold: isSel);
        }
    }

    private void DrawInProgress(Canvas c)
    {
        if (!_drawing) return;
        var col = FromRgb(ZoneStore.CategoryColor(DrawCategory));
        var brush = new SolidColorBrush(col);

        // ── 矩形拖框预览 ──
        if (_rectFrom is { } from && _cursor is { } now)
        {
            double x = Math.Min(from.X, now.X), y = Math.Min(from.Y, now.Y);
            double w = Math.Abs(now.X - from.X), h = Math.Abs(now.Y - from.Y);
            var box = new Rectangle
            {
                Width = w, Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(0x40, col.R, col.G, col.B)),
                Stroke = brush, StrokeThickness = 2,
                StrokeDashArray = new AvaloniaList<double> { 5, 3 },
            };
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            c.Children.Add(box);
            AddVertex(c, from.X, from.Y, col);
            AddVertex(c, now.X, now.Y, col);

            double wm = w / _view.Scale, hm = h / _view.Scale;
            AddText(c, x + 6, y - 20, $"{wm:0.#} × {hm:0.#} m　{wm * hm / 1e4:0.##} 万m²", brush, 12, bold: true);
            return;
        }

        if (_drawPts.Count == 0) return;

        var pts = new AvaloniaList<Point>(_drawPts.Select(p => new Point(Sx(p.X), Sy(p.Y))));

        // 矩形已框好但 Z 没解出、等着填基准标高：画成实心闭合框（不是橡皮筋）
        if (_mode == DrawMode.Rect)
        {
            c.Children.Add(new Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(Color.FromArgb(0x40, col.R, col.G, col.B)),
                Stroke = brush, StrokeThickness = 2.4,
                StrokeDashArray = new AvaloniaList<double> { 5, 3 },
            });
            foreach (var p in pts) AddVertex(c, p.X, p.Y, col);
            AddText(c, pts[0].X + 7, pts[0].Y + 6, "待入库（填基准标高后点「完成」）", brush, 12, bold: true);
            return;
        }

        if (_drawPts.Count >= 2)
            c.Children.Add(new Polyline { Points = pts, Stroke = brush, StrokeThickness = 2 });

        // 橡皮筋：最后一点 → 光标 → 首点（闭合预览）
        if (_cursor is { } cur)
        {
            var last = pts[^1];
            AddLine(c, last.X, last.Y, cur.X, cur.Y, brush, 1.6, new AvaloniaList<double> { 4, 3 });
            if (_drawPts.Count >= 2)
                AddLine(c, cur.X, cur.Y, pts[0].X, pts[0].Y, brush, 1.2, new AvaloniaList<double> { 2, 4 });
        }

        foreach (var p in pts) AddVertex(c, p.X, p.Y, col);
        AddText(c, pts[0].X + 7, pts[0].Y - 20, $"{_drawPts.Count} 点", brush, 12, bold: true);
    }

    /// <summary>比例尺：划区最容易出的错是"影像与工程不同投影"，一眼看得出量级就少一半误会。</summary>
    private void DrawScaleBar(Canvas c, double W, double H)
    {
        double target = W * 0.22 / _view.Scale;                  // 想要的世界长度 m
        double pow = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1e-6, target))));
        double[] steps = { 1, 2, 5, 10 };
        double len = steps.Select(s => s * pow).FirstOrDefault(v => v >= target * 0.6);
        if (len <= 0) len = pow;

        double px = len * _view.Scale;
        if (px < 20 || px > W * 0.6) return;

        double x = 14, y = H - 22;
        var b = TaskUi.BodyBrush;
        AddLine(c, x, y, x + px, y, b, 2, null);
        AddLine(c, x, y - 4, x, y + 4, b, 2, null);
        AddLine(c, x + px, y - 4, x + px, y + 4, b, 2, null);
        AddText(c, x, y - 20, len >= 1000 ? $"{len / 1000:0.##} km" : $"{len:0.#} m", b, 11.5);
    }

    // ═══════════════════ 小工具 ═══════════════════

    private static string CategoryKey(int idx) => idx switch
    {
        0 => "pit",
        1 => "external_dump",
        2 => "internal_dump",
        4 => "pit_working_slope",
        5 => "dump_working_slope",
        _ => "mineable",
    };

    private static int CategoryIndex(string key) => key switch
    {
        "pit" => 0,
        "external_dump" => 1,
        "internal_dump" => 2,
        // ★ 两个工作帮必须在这里认出来：漏了就回落 3（未分类），点「应用」会把它静默改判成未分类
        "pit_working_slope" => 4,
        "dump_working_slope" => 5,
        _ => 3,
    };

    internal static Color FromRgb(uint rgb)
        => Color.FromRgb((byte)(rgb >> 16 & 0xFF), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));

    private static void AddVertex(Canvas c, double x, double y, Color col)
    {
        var r = new Rectangle
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(col),
            Stroke = Brushes.White, StrokeThickness = 1.2,
        };
        Canvas.SetLeft(r, x - 3.5);
        Canvas.SetTop(r, y - 3.5);
        c.Children.Add(r);
    }

    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2,
                                IBrush b, double th, AvaloniaList<double>? dash)
    {
        var l = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th };
        if (dash != null) l.StrokeDashArray = dash;
        c.Children.Add(l);
    }

    private static void AddText(Canvas c, double x, double y, string t, IBrush b, double size, bool bold = false)
    {
        // 原版给文字一圈白色投影（DropShadowEffect）以便在影像上可读；Avalonia 无同类效果，改用半透明白底衬
        var tb = new TextBlock { Text = t, Foreground = b, FontSize = size, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
        var bg = new Border { Child = tb, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(2, 0), CornerRadius = new CornerRadius(2) };
        Canvas.SetLeft(bg, x);
        Canvas.SetTop(bg, y);
        c.Children.Add(bg);
    }

    /// <summary>清单一行。</summary>
    public sealed class ZoneRow
    {
        public ZoneRecord Rec { get; }
        public ZoneLink? Link { get; }

        public ZoneRow(ZoneRecord rec, ZoneLink? link)
        {
            Rec = rec;
            Link = link;
            ColorBrush = new SolidColorBrush(FromRgb(ZoneStore.CategoryColor(rec.Category)));
        }

        public bool Active => Rec.Active;
        public string Name => Rec.Name;
        public string CategoryText => ZoneStore.CategoryZh(Rec.Category);
        public string PointText => Rec.PointCount.ToString();
        public string AreaText => Rec.AreaM2 <= 0 ? "—" : (Rec.AreaM2 / 1e4).ToString("0.##");
        public IBrush ColorBrush { get; }

        /// <summary>未选定的一律显示「未选定」——它的绑定关系再好也进不去推演，先说这一条。</summary>
        public string Verdict => !Rec.Active ? "未选定" : Link?.Verdict ?? "—";

        public string BindText => Link == null ? ""
            : (Rec.Active ? "" : "本期未选定，不进推演、不参与路网裁剪　|　")
            + $"绑定：{Link.BindText}　高程：{Link.ZText}";

        public IBrush VerdictBrush => !Rec.Active ? Frozen(0x94, 0x9A, 0xA3) : Link?.Bind switch
        {
            ZoneBind.Direct when Link.RingOk => Frozen(0x16, 0xA3, 0x4A),
            ZoneBind.Fallback when Link.RingOk => Frozen(0xD9, 0x77, 0x06),
            _ => Frozen(0xDC, 0x26, 0x26),
        };

        public FontWeight VerdictWeight => FontWeight.Bold;

        private static IBrush Frozen(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    /// <summary>候选表的一行 —— <b>装得下两种候选</b>：作业区域（<see cref="ZoneProposal"/>）与工序作业区（<see cref="ProcessZoneProposal"/>）。</summary>
    public sealed class PropRow
    {
        public ZoneProposal? Zone { get; }
        public ProcessZoneProposal? Proc { get; }

        /// <summary>兼容旧访问点：只有作业区域候选时非空。</summary>
        public ZoneProposal Prop => Zone!;

        public PropRow(ZoneProposal p) => Zone = p;
        public PropRow(ProcessZoneProposal p) => Proc = p;

        public bool IsProc => Proc != null;

        /// <summary>勾没勾。<b>可写</b> —— 两种候选各自记在自己身上。</summary>
        public bool Selected
        {
            get => Zone != null ? Zone.Selected : Proc!.Selected;
            set { if (Zone != null) Zone.Selected = value; else Proc!.Selected = value; }
        }

        /// <summary>高程解得出来（<b>解不出一律不许勾</b>）。</summary>
        public bool ZOk => Zone != null ? Zone.Provenance.Length > 0 : Proc!.ZSource.Length > 0;

        public IReadOnlyList<ZonePoint> Ring => Zone != null ? Zone.Ring : Proc!.Ring;

        public string ProcText => Proc != null ? Proc.ProcessZh : "";

        public uint ColorRgb => Proc != null
            ? ProcessZone.Color(Proc.Process)
            : ZoneStore.CategoryColor(Zone!.Category);

        public string Name => Zone != null ? Zone.Name : Proc!.Name;
        public double AreaM2 => Zone != null ? Zone.AreaM2 : Proc!.AreaM2;
        public string AreaText => (AreaM2 / 1e4).ToString("0.##");
        public string BlockText => (Zone != null ? Zone.Blocks.Count : Proc!.Blocks.Count).ToString();

        /// <summary>列里放<b>短</b>的那一版；整句在 ToolTip 与确认框里。</summary>
        public string ActionText => !ZOk ? "解不出高程"
                                  : (Zone != null ? Zone.Existing != null : Proc!.Existing != null)
                                    ? "改边界" : "新增";

        /// <summary>新增=绿 · 改既有=橙 · 不能入库=红。</summary>
        public IBrush ActionBrush => !ZOk ? Red
                                  : (Zone != null ? Zone.Existing != null : Proc!.Existing != null)
                                    ? Amber : Green;

        /// <summary>悬停提示：这一块的口径与要注意的事。</summary>
        public string IssueText
        {
            get
            {
                var bits = new List<string>();
                if (Proc != null)
                {
                    bits.Add($"{Proc.ProcessZh}　{Proc.AreaM2 / 1e4:0.##} 万m²　{Proc.Blocks.Count} 个单元"
                           + (Proc.VolumeM3.HasValue ? $" / {Proc.VolumeM3.Value / 1e4:0.##} 万m³（{Proc.Basis}）"
                                                     : "　（本区没有量口径）"));
                    bits.Add(Proc.EquipRole.Length > 0 ? $"派 {Proc.EquipRole}" : "不派设备（禁入区）");
                    bits.Add($"分组「{Proc.GroupKey}」"
                           + (Proc.PieceCount > 1 ? $"，本组第 {Proc.PieceIndex}/{Proc.PieceCount} 块（不相连）" : ""));
                    bits.Add(ZOk ? "高程：" + Proc.ZSource : "⚠ 解不出高程，不能入库");
                    bits.AddRange(Proc.Issues);
                    return string.Join("\n", bits);
                }
                bits.Add($"{ZoneStore.CategoryZh(Zone!.Category)}　{Zone.AreaM2 / 1e4:0.##} 万m²　"
                       + $"{Zone.Blocks.Count} 个单元 / {Zone.VolumeM3 / 1e4:0.##} 万m³");
                bits.Add(Zone.ByFace ? $"按作业面「{Zone.GroupName}」分组"
                                     : $"按采场/排土场名「{Zone.GroupName}」兜底分组");
                bits.Add(ZOk ? "高程：" + Zone.Provenance : "⚠ 解不出高程，不能入库");
                bits.AddRange(Zone.Issues);
                return string.Join("\n", bits);
            }
        }

        private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
        private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
        private static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    }
}
