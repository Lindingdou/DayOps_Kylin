using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>「创建工程位置」窗口的读图输入（由主窗从场景里提出来，窗口自己分模板/端帮）。</summary>
internal sealed class EpInput
{
    /// <summary>图上全部可见多段线：(句柄=按序编号≥1, 图层, 扁平 xyz, 闭合)。</summary>
    public List<EpPolyline> Lines = new();
    /// <summary>可选的采场范围：(名, 扁平 XY 环)。第一项通常是"选中的闭合线"，其后是库里的作业区域。</summary>
    public List<(string Name, double[] Ring)> Regions = new();
    /// <summary>扫到的图层直方图：层名 → (实体数, 其中多段线数)。</summary>
    public Dictionary<string, (int Ents, int Polys)> ScanHist = new(StringComparer.Ordinal);
    /// <summary>图上的工作线（「工作线」图层基线 + 附属结束线/回转中心反算的投影几何），句柄同多段线编号体系。</summary>
    public List<(ulong Handle, WorkLineSamples Geo)> WorkLines = new();
    /// <summary>视口里当前选中的多段线句柄（用选中的工作线 / 改判用）。</summary>
    public HashSet<ulong> SelectedHandles = new();
    /// <summary>图上的三角网（排土场坡面 / 要并进新面模型的已有坡面）：句柄同一编号体系，顶点扁平世界坐标。</summary>
    public List<(ulong Handle, string Name, string Layer, double[] Verts, int[] Tris)> Meshes = new();
}

/// <summary>
/// 「创建工程位置 · 模板 ⊕ 端帮对接」—— 忠实移植原 <c>MineAssLib.Views.EngineeringPositionWindow</c> 的 ①②③ 三步：
///
/// <para>① 提取节点：范围内是开采模板台阶（台阶_* / 人勾的模板图层白名单），其余多段线是现状采场台阶；
/// <b>所有台阶线的所有端点各成一个节点、一律开放</b>。</para>
/// <para>② 连线：<b>平面拾取图</b>上把一个端点<b>拖到</b>另一个端点上即建立衔接（四条闸：不同节点 / 不是同一条线的两头 / 同角色 / 不成环）；
/// 从已连节点按下 = 抓住这一端改挂；双击节点 = 解除它的连线；点连线选中，Delete 删；滚轮缩放（光标锚定）、空白拖动平移；
/// 节点只在光标附近显示（已连的一律显示），标高直接标在点上。</para>
/// <para>③ 生成衔接并落地：<see cref="EpConnectorBuilder"/> 按纵坡闸 + 拐点做法（折接 / 圆弧倒角 R）出三维衔接段，
/// 超闸的先摊开让人看一眼再落；配对关系按范围名存档、下次提取按标高+位置回填。</para>
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item>④⑤ 替换台阶线见 <c>EngineeringPositionWindow.Replace.cs</c>；⑥ 新建工程位置（采场档/排土档）、⑦ 配面生成台阶面见 <c>EngineeringPositionWindow.NewPosition.cs</c>。</item>
///   <item>补了「就近自动配对」按钮（建议线，虚线）—— 原版最终档只有拖拽 + 存档回填；自检下靠它出连线截图。</item>
///   <item>Kylin 无实体句柄：句柄由主窗按序编号，落地时整层替换「创建工程位置_衔接 / _交点」上一批产物（与原版同）。</item>
/// </list>
/// </summary>
internal sealed partial class EngineeringPositionWindow : Window
{
    internal const string LinkLayer  = "创建工程位置_衔接";
    internal const string CrossLayer = "创建工程位置_交点";
    private const string SettingsKeyPrefix = "kylin.ep.links.v2.";
    private const string TplLayersKey = "kylin.ep.tpllayers";
    private const string NoRegionItem = "（不用范围 · 按模板两端切）";
    /// <summary>节点显示半径(px)：只有光标这个圈内的端点才画出来（已连的一律画）。</summary>
    private const double NodeShowRadius = 150;

    private readonly Func<EpInput> _read;
    private readonly Action<IReadOnlyList<EpConnector>, EpScene, double> _land;
    private readonly Func<EpApplyRequest, int> _apply;
    private readonly Action<EpNewPositionRequest> _newPosition;
    private readonly Action<string, bool> _echo;

    private EpScene? _scene;
    private EpInput _input = new();
    private readonly List<EpPolyline> _tplRaw = new();
    private readonly List<EpPolyline> _pitRaw = new();
    private readonly HashSet<string> _tplLayerPick = new(StringComparer.Ordinal);

    // ── 控件 ──
    private readonly TextBlock _txtDiag = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#5B6B80") };
    private readonly TextBlock _txtStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Brush.Parse("#3B4A5C"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _txtLinkCount = new() { Text = "0", FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#1E8E5A") };
    private readonly ComboBox _cmbRegion = new() { Width = 220 };
    private readonly TextBox _txtGroupTol = new() { Text = "0.5", Width = 48 };
    private readonly CheckBox _chkEndOnly = new() { Content = "只取两端断口", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _cmbJoinStyle = new() { Width = 120 };
    private readonly TextBox _txtFilletR = new() { Text = "30", Width = 48, IsEnabled = false };
    private readonly TextBox _txtGrade = new() { Text = EpConnectorBuilder.DefaultGradePct.ToString("0.#", CultureInfo.InvariantCulture), Width = 48 };
    private readonly ListBox _lstLinks = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Canvas _canvas = new() { Background = Brush.Parse("#1B2230"), Focusable = true, ClipToBounds = true };
    private readonly Button _btnTplLayers = new() { Content = "模板图层 ▾", Padding = new Thickness(10, 3) };
    private readonly TextBlock _txtPvHint = new() { FontSize = 11, Foreground = Brush.Parse("#8A96A8"), VerticalAlignment = VerticalAlignment.Center, Text = "滚轮缩放 · 空白拖动平移 · 节点在光标附近显示" };

    // ── 平面拾取视图 ──
    private double _pvScale = 1, _pvOx, _pvOy;
    private bool _pvFit;
    private Canvas? _nodeLayer;

    private sealed class NodeVisual { public EpNode Node = null!; public Point Center; public Ellipse Dot = null!; public Ellipse Glow = null!; }
    private readonly List<NodeVisual> _nodeVisuals = new();

    private EpNode? _dragFrom;
    /// <summary>正在改挂的那条连线：从已连节点按下 = 抓住这一端；落空/接不上就原样放回去。</summary>
    private EpLink? _reattaching;
    private Avalonia.Controls.Shapes.Path? _rubber;
    private EpLink? _selected;
    private bool _panning;
    private Point _panStart;
    private double _panPvOx, _panPvOy;
    private Point _cursor = new(-1e6, -1e6);
    private bool _suppressLedgerEvent;

    // ── 配色（与原版一致：模板琥珀 / 端帮青蓝 / 衔接亮绿 / 建议灰蓝）──
    private static readonly Color CTplA  = Color.FromRgb(0xF0, 0xC0, 0x76);
    private static readonly Color CTplB  = Color.FromRgb(0xD3, 0x82, 0x2B);
    private static readonly Color CWallA = Color.FromRgb(0x8A, 0xD1, 0xFA);
    private static readonly Color CWallB = Color.FromRgb(0x2E, 0x86, 0xC8);
    private static readonly Color CLink  = Color.FromRgb(0x7B, 0xE0, 0xA8);
    private static readonly Color CSug   = Color.FromRgb(0x8F, 0xA4, 0xC0);

    internal EngineeringPositionWindow(Func<EpInput> read, Action<IReadOnlyList<EpConnector>, EpScene, double> land, Func<EpApplyRequest, int> apply,
                                       Action<EpNewPositionRequest> newPosition, Action<string, bool> echo)
    {
        _read = read; _land = land; _apply = apply; _newPosition = newPosition; _echo = echo;
        Title = "创建工程位置 · 模板 ⊕ 端帮对接";
        Width = 1280; Height = 780; MinWidth = 980; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowFit.ClampToScreen(this);
        RestoreTplLayerPick();
        Content = BuildLayout();
        _canvas.SizeChanged += (_, _) => Render();
        _canvas.PointerPressed += OnCanvasPressed;
        _canvas.PointerMoved += OnCanvasMoved;
        _canvas.PointerReleased += OnCanvasReleased;
        _canvas.PointerWheelChanged += OnCanvasWheel;
        _canvas.KeyDown += (_, e) => { if (e.Key == Key.Delete && _selected != null) { RemoveLink(_selected); e.Handled = true; } };
        _lstLinks.SelectionChanged += (_, _) => { if (_suppressLedgerEvent) return; if (_lstLinks.SelectedItem is ListBoxItem it && it.Tag is EpLink l) { _selected = l; Render(); } };
        _lstLinks.KeyDown += (_, e) => { if (e.Key == Key.Delete && _lstLinks.SelectedItem is ListBoxItem it && it.Tag is EpLink l) { RemoveLink(l); e.Handled = true; } };
        _cmbJoinStyle.SelectionChanged += (_, _) => OnJoinStyleChanged();
        Opened += (_, _) => { try { TryAdoptHandoff(quiet: true); } catch { } };   // 原版 Loaded 时静默读交接单
    }

    // ═══════════════════ 布局 ═══════════════════

    private static Button B(string t, Action a, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = t, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        if (primary) { b.FontWeight = FontWeight.SemiBold; b.Background = Brush.Parse("#1E8E5A"); b.Foreground = Brushes.White; }
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => a();
        return b;
    }
    private static TextBlock T(string t, double size = 12) => new() { Text = t, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };

    private Control BuildLayout()
    {
        var head = new DockPanel { Margin = new Thickness(14, 10, 14, 4) };
        var count = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        count.Children.Add(T("已连 ", 11)); count.Children.Add(_txtLinkCount); count.Children.Add(T(" 处", 11));
        DockPanel.SetDock(count, Avalonia.Controls.Dock.Right);
        head.Children.Add(count);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "创建工程位置 · 模板 ⊕ 端帮对接", FontSize = 16, FontWeight = FontWeight.Bold });
        _txtDiag.Text = "①②③ 选采场范围 → 提取：范围内是开采模板台阶，范围外被分割出的采场台阶就是两侧端帮；在平面图里把一个端点拖到另一个端点上建立衔接（高差按纵坡闸渐进过渡，两端与台阶线相切）。";
        titles.Children.Add(_txtDiag);
        head.Children.Add(titles);

        var row1 = new WrapPanel { Margin = new Thickness(14, 4, 14, 2) };
        row1.Children.Add(T("采场范围"));
        _cmbRegion.Margin = new Thickness(0, 0, 6, 0);
        row1.Children.Add(_cmbRegion);
        row1.Children.Add(B("刷新", RefreshInput));
        row1.Children.Add(T("标高分组 ±", 11.5)); _txtGroupTol.Margin = new Thickness(0, 0, 2, 0); row1.Children.Add(_txtGroupTol); row1.Children.Add(T("m　", 11.5));
        _chkEndOnly.Margin = new Thickness(0, 0, 12, 0); row1.Children.Add(_chkEndOnly);
        row1.Children.Add(B("① 提取节点", Extract, tip: "范围内 = 开采模板台阶（台阶_* 或勾选的模板图层）；其余多段线 = 现状采场台阶；所有端点各成一个节点"));
        _btnTplLayers.Margin = new Thickness(0, 0, 6, 0);
        _btnTplLayers.Click += (_, _) => ShowTplLayerFlyout();
        row1.Children.Add(_btnTplLayers);

        var row2 = new WrapPanel { Margin = new Thickness(14, 2, 14, 6) };
        row2.Children.Add(T("② 连线：在图上把一个端点拖到另一个端点", 11.5));
        row2.Children.Add(B("就近自动配对", AutoPair, tip: "Kylin 补：每个模板端点接同侧、同标高、平距最近且过四条闸的端帮端点（建议线，虚线）；拖拽即可改"));
        row2.Children.Add(B("清空连线", ClearLinks));
        row2.Children.Add(B("适应视图", () => { if (_scene == null) return; _pvFit = false; Render(); }));
        AddNewPositionControls(row1, row2);

        var top = new StackPanel { Children = { head, row1, row2, BuildReplaceRow() } };

        // 中部：左台账 / 右平面图
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("320,8,*"), Margin = new Thickness(14, 0, 14, 0) };
        var ledger = new DockPanel();
        var lh = BuildLedgerHeader();
        DockPanel.SetDock(lh, Avalonia.Controls.Dock.Top); ledger.Children.Add(lh);
        ledger.Children.Add(BuildLedgerBody());
        Grid.SetColumn(ledger, 0); body.Children.Add(ledger);
        ShowLedger(false);

        var pv = new DockPanel();
        var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
        legend.Children.Add(new TextBlock { Text = "平面 · 台阶线与端点节点 · 把一个端点拖到另一个端点上即建立衔接", FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        void Leg(Color c, string t) { legend.Children.Add(new Rectangle { Width = 14, Height = 3, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center }); legend.Children.Add(new TextBlock { Text = t, FontSize = 11, Margin = new Thickness(5, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }); }
        Leg(CTplB, "模板台阶线"); Leg(Color.FromRgb(0x6E, 0x93, 0xB4), "现状采场线"); Leg(CLink, "已衔接");
        legend.Children.Add(_txtPvHint);
        DockPanel.SetDock(legend, Avalonia.Controls.Dock.Top); pv.Children.Add(legend);
        pv.Children.Add(new Border { BorderBrush = Brush.Parse("#D5DBE3"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = _canvas });
        Grid.SetColumn(pv, 2); body.Children.Add(pv);

        // 页脚：③ + 参数（Dock 到底，别靠定高）
        _cmbJoinStyle.ItemsSource = new[] { "多段线折接", "圆弧倒角" };
        _cmbJoinStyle.SelectedIndex = 0;
        var foot = new Border
        {
            Background = Brush.Parse("#F3F4F6"), Padding = new Thickness(14, 7, 14, 9), Margin = new Thickness(0, 8, 0, 0),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
        };
        // 状态一行独占（信息框，原版 Theme.InfoBox），参数 + ③ 另起一行靠右 —— 状态文案长时不再把输入框撑高
        var fp = new StackPanel();
        var statusBox = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5), Margin = new Thickness(0, 0, 0, 7),
            Background = Brush.Parse("#EEF2F7"), BorderBrush = Brush.Parse("#D5DBE3"), BorderThickness = new Thickness(1),
            Child = _txtStatus,
        };
        _txtStatus.Margin = new Thickness(0);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _cmbJoinStyle.VerticalAlignment = VerticalAlignment.Center; _txtFilletR.VerticalAlignment = VerticalAlignment.Center; _txtGrade.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(T("拐点")); _cmbJoinStyle.Margin = new Thickness(0, 0, 6, 0); right.Children.Add(_cmbJoinStyle);
        ToolTip.SetTip(_cmbJoinStyle, "同一级两段接在一起时，拐点怎么处理：\n· 多段线折接 = 两端沿各自走势延伸，在交点处直接折过去（尖角，与原线严格共线）\n· 圆弧倒角 = 在交点处用一段与两边相切的圆弧倒圆（车辆能走，但两端各要让出一小截）\n有高差的那种衔接不受这里影响 —— 它走的是限坡缓和曲线。");
        right.Children.Add(T("R")); _txtFilletR.Margin = new Thickness(0, 0, 2, 0); right.Children.Add(_txtFilletR); right.Children.Add(T("m　"));
        ToolTip.SetTip(_txtFilletR, "圆弧倒角半径(m)。两边让出的切线长 = R·tan(转角/2)；让不出那么多时按可用长度自动压小 R（不会把原线吃穿），台账里报实际用的 R。");
        right.Children.Add(T("纵坡闸 ≤")); _txtGrade.Margin = new Thickness(0, 0, 2, 0); right.Children.Add(_txtGrade); right.Children.Add(T("%　"));
        ToolTip.SetTip(_txtGrade, "衔接段的最大纵坡(%)。高差不许压在一根直弦上——按这个坡把它缓缓爬完：最小水平长 = 1.5 × |Δz| ÷ 纵坡（1.5 是两端与台阶线相切的形状系数，中点最陡）。\n平距不够就沿端帮台阶线往外多吃一段；吃到尽头仍缓不下来的会先弹出来让人判，不静默拉平。");
        right.Children.Add(B("③ 生成衔接并落地", Materialize, primary: true, tip: "按连线生成三维【渐进过渡】衔接段入图（图层「创建工程位置_衔接」，重复生成会替换上一批），并把配对关系按采场范围存档。"));
        var close = B("关闭", Close); close.MinWidth = 76; close.Margin = new Thickness(0); right.Children.Add(close);
        fp.Children.Add(statusBox);
        fp.Children.Add(right);
        foot.Child = fp;

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(foot, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top); root.Children.Add(foot); root.Children.Add(body);
        _txtStatus.Text = "就绪。先选采场范围 → ①提取节点。";
        return root;
    }

    private void Status(string s) => _txtStatus.Text = s;
    private async void Warn(string msg) { Status(msg); await BlockMsgBox.WarnAsync(this, "创建工程位置", msg); }

    private bool ArcJoin => _cmbJoinStyle.SelectedIndex == 1;
    private void OnJoinStyleChanged()
    {
        bool arc = ArcJoin;
        _txtFilletR.IsEnabled = arc;
        if (_scene == null) return;
        Status(arc ? "拐点改为【圆弧倒角】：两端各让出切线长 T = R·tan(转角/2)，让不出就自动压小 R。重跑③即按新做法出线。"
                   : "拐点改为【多段线折接】：两端沿走势延伸、在交点处直接折过去，接头与原线严格共线。重跑③即按新做法出线。");
    }
    private static bool TryD(TextBox tb, out double v) => double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    // ═══════════════════ ① 提取 ═══════════════════

    /// <summary>重读图（范围下拉 + 线）。</summary>
    internal void RefreshInput()
    {
        _input = _read();
        string? keep = _cmbRegion.SelectedItem as string;
        var names = new List<string> { NoRegionItem };
        foreach (var (name, ring) in _input.Regions) if (ring.Length >= 6 && !names.Contains(name)) names.Add(name);
        _cmbRegion.ItemsSource = names;
        _cmbRegion.SelectedIndex = keep != null && names.Contains(keep) ? names.IndexOf(keep) : (names.Count > 1 ? 1 : 0);
    }

    private double[] SelectedRing()
    {
        string sel = _cmbRegion.SelectedItem as string ?? "";
        if (sel.Length == 0 || sel == NoRegionItem) return Array.Empty<double>();
        foreach (var (name, ring) in _input.Regions) if (name == sel) return ring;
        return Array.Empty<double>();
    }

    private bool IsTemplateLayerPick(string layer) => _tplLayerPick.Count > 0 ? _tplLayerPick.Contains(layer) : EngineeringPositionBuilder.IsBenchLine(layer);

    internal void Extract()
    {
        if (_input.Lines.Count == 0) RefreshInput();
        double gtol = TryD(_txtGroupTol, out double g) && g > 0 ? g : 0.5;
        string regionName = _cmbRegion.SelectedItem as string ?? "";
        if (regionName == NoRegionItem) regionName = "";
        var ring = SelectedRing();

        var tpl = new List<EpPolyline>(); var pit = new List<EpPolyline>();
        if (DumpMode)
        {
            if (!SplitLinesDump(tpl, pit)) return;       // 排土档必须先有坡面，否则裁不了旧线
        }
        else
        foreach (var pl in _input.Lines)
        {
            if (pl.Xyz.Length < 6) continue;
            if (pl.Layer == LinkLayer || pl.Layer == CrossLayer) continue;           // 本功能自己的上一批产物
            if (pl.Layer.StartsWith("驱动量_", StringComparison.Ordinal)) continue;   // 斜面/Stage2/控制线不是台阶
            (IsTemplateLayerPick(pl.Layer) ? tpl : pit).Add(pl);
        }

        if (tpl.Count == 0 && DumpMode) { Warn(DumpNoTemplateMessage()); return; }
        if (tpl.Count == 0)
        {
            Warn((_tplLayerPick.Count > 0
                    ? $"按你在「模板图层 ▾」里勾的 {_tplLayerPick.Count} 个图层，一条多段线都没扫到。\n\n"
                    : "图上没有按默认规则认得出的开采模板台阶线（图层 台阶_* 或「创建工程位置_台阶」）。\n\n"
                      + "默认规则只认「驱动量」→「生成采区台阶面」落的那批。模板若是【批量台阶扩帮 / 局部台阶】做的，它落的是 采场_坡顶线 / 采场_坡底线 ——\n"
                      + "与现状采场台阶同一族层名，哪批是模板层名分不出，只能由你指。\n\n")
               + ScanReport()
               + "\n→ 点①提取右边的「模板图层 ▾」，对着上面这张表勾出模板所在的那几层，再重跑①提取。（勾了就只认勾上的；全不选 = 回默认规则。选择会记住。）");
            return;
        }

        _dumpKept.Clear();
        if (DumpMode) _dumpKept.AddRange(pit);           // 裁剩下的旧线 —— ⑥要原样写进新图层

        var scene = EngineeringPositionBuilder.Build(tpl, pit, ring, regionName, gtol, _chkEndOnly.IsChecked == true,
                                                     pitEndpointsAreCuts: DumpMode,                 // 排土档：旧线已按交线裁过，它们自己的两端就是断口
                                                     trustTemplateList: !DumpMode && _tplLayerPick.Count > 0,
                                                     allEndpointNodes: true);
        if (!scene.Success) { Warn("提取失败：" + scene.Error); return; }

        _scene = scene; _selected = null;
        RestoreSavedLinks(scene);
        _tplRaw.Clear(); _tplRaw.AddRange(tpl);
        _pitRaw.Clear(); _pitRaw.AddRange(pit);
        _plan = null; _coverSel = null;
        _txtDiag.Text = scene.Diag;
        _pvFit = false;
        Render(); RebuildLedger();

        int wallNodes = 0, tplNodes = 0;
        foreach (var n in _scene.Nodes) if (n.Kind == EpNodeKind.EndWall) wallNodes++; else tplNodes++;
        if (wallNodes == 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"端帮侧一个节点都没建出来（模板侧 {tplNodes} 个）—— 现在怎么拖都只有模板端点可接，③也接不到端帮。");
            sb.AppendLine($"这一次读到：模板侧 {tpl.Count} 条线 / 采场侧 {pit.Count} 条线。");
            if (pit.Count == 0)
            {
                sb.AppendLine("采场侧一条线都没有。端帮 = 【现状采场台阶线】，图上没有它就无从对接：");
                if (_tplLayerPick.Count > 0) sb.AppendLine("  · 你勾了模板图层 —— 勾了就只认勾上的，没勾的才算端帮；把现状台阶线所在的层也勾进去，端帮侧当然是空的。");
                sb.AppendLine("  · 也可能图上本来就只有模板这一批线（新做的扩帮，还没有现状采场）→ 先把现状台阶线导入/提取出来。");
            }
            Warn(sb.ToString());
            _echo("创建工程位置 · 端帮节点 0 个 —— " + scene.Diag, true);
        }
        Status($"提取完成：模板 {tpl.Count} 条 / 采场 {pit.Count} 条 → 节点 {_scene.Nodes.Count} 个（所有端点开放）。" +
               "在平面图上把一个端点拖到另一个端点上即建立衔接；双击节点解除它的连线。");
        _echo("创建工程位置 · " + scene.Diag, false);
    }

    private string ScanReport(int maxRows = 20)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("本次逐图层扫描（层名 → 实体数 / 其中多段线数）：");
        int shown = 0;
        foreach (var kv in _input.ScanHist.OrderByDescending(k => k.Value.Polys).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            if (shown++ >= maxRows) { sb.AppendLine($"  …另有 {_input.ScanHist.Count - maxRows} 个图层"); break; }
            sb.AppendLine($"  {kv.Key,-24} {kv.Value.Ents,5} / {kv.Value.Polys,5}{(IsTemplateLayerPick(kv.Key) ? "  ← 模板" : "")}");
        }
        if (shown == 0) sb.AppendLine("  （图上一条多段线都没扫到）");
        return sb.ToString();
    }

    // ── 模板图层白名单（勾了就只认勾上的；空 = 默认规则 台阶_*）──
    private void ShowTplLayerFlyout()
    {
        var panel = new StackPanel { Margin = new Thickness(10), MinWidth = 260 };
        panel.Children.Add(new TextBlock { Text = "哪些图层算【开采模板台阶线】", FontWeight = FontWeight.SemiBold, FontSize = 12 });
        panel.Children.Add(new TextBlock { Text = "一个都不勾 = 按默认规则认 台阶_*。勾了就【只认勾上的】，其余多段线一律算端帮。改完请重跑①提取。", FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5B6B80"), Margin = new Thickness(0, 2, 0, 6), MaxWidth = 320 });
        var boxes = new List<CheckBox>();
        var layers = _input.ScanHist.Where(k => k.Value.Polys > 0).Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var s in _tplLayerPick) if (!layers.Contains(s)) layers.Add(s);
        var list = new StackPanel();
        foreach (var lay in layers)
        {
            int polys = _input.ScanHist.TryGetValue(lay, out var v) ? v.Polys : 0;
            var cb = new CheckBox { Content = $"{lay}　({polys} 条)", IsChecked = _tplLayerPick.Contains(lay), Tag = lay, FontSize = 11.5 };
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) _tplLayerPick.Add(lay); else _tplLayerPick.Remove(lay); SaveTplLayerPick(); };
            boxes.Add(cb); list.Children.Add(cb);
        }
        panel.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(B("全不选(回默认)", () => { foreach (var cb in boxes) cb.IsChecked = false; _tplLayerPick.Clear(); SaveTplLayerPick(); }));
        panel.Children.Add(bar);
        var fly = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        fly.ShowAt(_btnTplLayers);
    }
    private void SaveTplLayerPick()
    {
        try { UserSettings.Current.Set(TplLayersKey, _tplLayerPick.ToList()); UserSettings.Current.Flush(); } catch { }
        _btnTplLayers.Content = _tplLayerPick.Count > 0 ? $"模板图层 ▾ ({_tplLayerPick.Count})" : "模板图层 ▾";
    }
    private void RestoreTplLayerPick()
    {
        try { var l = UserSettings.Current.Get<List<string>>(TplLayersKey); if (l != null) foreach (var s in l) _tplLayerPick.Add(s); } catch { }
        _btnTplLayers.Content = _tplLayerPick.Count > 0 ? $"模板图层 ▾ ({_tplLayerPick.Count})" : "模板图层 ▾";
    }

    // ═══════════════════ ② 连线 ═══════════════════

    internal void AutoPair()
    {
        if (_scene == null) { Status("先「①提取节点」。"); return; }
        double gtol = TryD(_txtGroupTol, out double g) && g > 0 ? g : 0.5;
        int n = EngineeringPositionBuilder.AutoPairNearest(_scene, gtol, 400);
        _selected = null;
        Render(); RebuildLedger();
        Status(n > 0 ? $"就近自动配对：新增 {n} 条建议线（虚线）。拖拽可改挂，双击节点可解除；确认无误就「③ 生成衔接并落地」。"
                     : "就近自动配对：没有可配的对（同侧、同标高 ±分组容差、过四条闸的端帮端点一个都没有）—— 请在图上手拖。");
    }

    private void ClearLinks()
    {
        if (_scene == null) return;
        _scene.Links.Clear(); _selected = null;
        Render(); RebuildLedger();
        Status("已清空全部连线。");
    }

    private void Render()
    {
        _canvas.Children.Clear();
        _nodeVisuals.Clear();
        if (_scene == null || _canvas.Bounds.Width < 40 || _canvas.Bounds.Height < 40) return;
        RenderPlanPick();
    }

    private Point PV(double x, double y) => new((x - _pvOx) * _pvScale, (_pvOy - y) * _pvScale);

    private void FitPlanView()
    {
        double xmn = double.MaxValue, xmx = double.MinValue, ymn = double.MaxValue, ymx = double.MinValue;
        void Eat(IReadOnlyList<EpPolyline> src)
        {
            foreach (var pl in src)
                for (int i = 0; i + 2 < pl.Xyz.Length; i += 3)
                {
                    double x = pl.Xyz[i], y = pl.Xyz[i + 1];
                    if (x < xmn) xmn = x; if (x > xmx) xmx = x;
                    if (y < ymn) ymn = y; if (y > ymx) ymx = y;
                }
        }
        Eat(_tplRaw); Eat(_pitRaw);
        if (xmn > xmx || ymn > ymx) { _pvScale = 1; _pvOx = 0; _pvOy = 0; _pvFit = true; return; }
        double w = Math.Max(1e-6, xmx - xmn), h = Math.Max(1e-6, ymx - ymn);
        double cw = Math.Max(40, _canvas.Bounds.Width), ch = Math.Max(40, _canvas.Bounds.Height);
        _pvScale = Math.Min(cw / w, ch / h) * 0.84;
        _pvOx = 0.5 * (xmn + xmx) - cw / (2 * _pvScale);
        _pvOy = 0.5 * (ymn + ymx) + ch / (2 * _pvScale);
        _pvFit = true;
    }

    /// <summary>平面拾取图：现状采场线（淡青蓝）→ 模板台阶线（琥珀）→ 已连衔接（绿）→ 节点层（随光标刷新）。</summary>
    private void RenderPlanPick()
    {
        if (_scene == null) return;
        if (!_pvFit) FitPlanView();

        foreach (var pl in _pitRaw) if (!DrawFaceColored(pl)) AddPvPoly(pl.Xyz, Color.FromArgb(0x88, 0x6E, 0x93, 0xB4), 1.0);
        foreach (var pl in _tplRaw) if (!DrawFaceColored(pl)) AddPvPoly(pl.Xyz, CTplB, 1.8);
        if (_scene.RingXy.Length >= 6)
        {
            var ring = new List<Point>();
            for (int i = 0; i + 1 < _scene.RingXy.Length; i += 2) ring.Add(PV(_scene.RingXy[i], _scene.RingXy[i + 1]));
            ring.Add(ring[0]);
            _canvas.Children.Add(new Polyline { Points = new AvaloniaList<Point>(ring), Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xA9, 0x4D)), StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 6, 4 }, IsHitTestVisible = false });
        }

        RenderPlanOverlay();

        foreach (var l in _scene.Links)
        {
            var a = _scene.ById(l.TemplateId); var b = _scene.ById(l.WallId);
            if (a == null || b == null) continue;
            var pa = PV(a.X, a.Y); var pb = PV(b.X, b.Y);
            var seg = new Line
            {
                StartPoint = pa, EndPoint = pb,
                Stroke = new SolidColorBrush(l.Suggested ? CSug : CLink),
                StrokeThickness = ReferenceEquals(l, _selected) ? 3.6 : 2.4,
                Cursor = new Cursor(StandardCursorType.Hand), Tag = l,
            };
            if (l.Suggested) seg.StrokeDashArray = new AvaloniaList<double> { 5, 4 };
            ToolTip.SetTip(seg, EngineeringPositionBuilder.Describe(_scene, l));
            seg.PointerPressed += (s, e) => { if ((s as Line)?.Tag is EpLink lk) { SelectLink(lk); e.Handled = true; } };
            _canvas.Children.Add(seg);
        }

        _nodeLayer = new Canvas();
        _canvas.Children.Add(_nodeLayer);
        RefreshNodeDots();

        var info = new TextBlock
        {
            Text = $"台阶线 {_tplRaw.Count + _pitRaw.Count} 条（模板 {_tplRaw.Count}）· 端点节点 {_scene.Nodes.Count} 个 · 已衔接 {_scene.Links.Count} 处 —— 节点在光标 {NodeShowRadius:0} px 内显示",
            FontSize = 11, Opacity = 0.85, Foreground = Brush.Parse("#AEBED6"), IsHitTestVisible = false,
        };
        Canvas.SetLeft(info, 10); Canvas.SetTop(info, 6);
        _canvas.Children.Add(info);
    }

    /// <summary>只画光标附近那一圈里的端点；已连的、正在拖的一律画。<see cref="_nodeVisuals"/> 只装画出来的 —— 看得见与点得中是同一件事。</summary>
    private void RefreshNodeDots()
    {
        if (_scene == null || _nodeLayer == null) return;
        _nodeLayer.Children.Clear();
        _nodeVisuals.Clear();
        double r2 = NodeShowRadius * NodeShowRadius;
        double cw = _canvas.Bounds.Width, ch = _canvas.Bounds.Height;

        foreach (var n in _scene.Nodes)
        {
            var c = PV(n.X, n.Y);
            if (c.X < -30 || c.Y < -30 || c.X > cw + 30 || c.Y > ch + 30) continue;
            bool linked = IsLinked(n);
            bool isDragSrc = _dragFrom != null && ReferenceEquals(n, _dragFrom);
            double dx = c.X - _cursor.X, dy = c.Y - _cursor.Y;
            if (!linked && !isDragSrc && dx * dx + dy * dy > r2) continue;

            bool tpl = n.Kind == EpNodeKind.Template;
            var glow = new Ellipse { Width = 20, Height = 20, IsHitTestVisible = false, Fill = new SolidColorBrush(tpl ? CTplA : CWallA), Opacity = linked ? 0.34 : 0.16 };
            Canvas.SetLeft(glow, c.X - 10); Canvas.SetTop(glow, c.Y - 10);
            var dot = new Ellipse
            {
                Width = 9, Height = 9,
                Fill = new SolidColorBrush(tpl ? CTplB : CWallB),
                Stroke = new SolidColorBrush(linked ? CLink : Colors.White),
                StrokeThickness = linked ? 2.0 : 1.3,
                IsHitTestVisible = false,      // 命中一律走 HitNode 按圆心判，别让两套判据分叉
            };
            Canvas.SetLeft(dot, c.X - 4.5); Canvas.SetTop(dot, c.Y - 4.5);
            _nodeLayer.Children.Add(glow); _nodeLayer.Children.Add(dot);

            // 标高直接标在点上：平面图把高程那一维压掉了，而"是不是同一级"正是接线前必须先判的
            var lab = new TextBlock
            {
                Text = n.Z.ToString("0.#", CultureInfo.InvariantCulture), FontSize = 10.5,
                Foreground = new SolidColorBrush(tpl ? CTplA : CWallA), IsHitTestVisible = false,
            };
            Canvas.SetLeft(lab, c.X + 7); Canvas.SetTop(lab, c.Y - 14);
            _nodeLayer.Children.Add(lab);
            _nodeVisuals.Add(new NodeVisual { Node = n, Center = c, Dot = dot, Glow = glow });
        }
        if (_dragFrom != null) HighlightTargets(_dragFrom, true);
    }

    private void AddPvPoly(double[] xyz, Color col, double th)
    {
        if (xyz == null || xyz.Length < 6) return;
        var pts = new List<Point>(xyz.Length / 3);
        for (int i = 0; i + 2 < xyz.Length; i += 3) pts.Add(PV(xyz[i], xyz[i + 1]));
        _canvas.Children.Add(new Polyline { Points = new AvaloniaList<Point>(pts), Stroke = new SolidColorBrush(col), StrokeThickness = th, IsHitTestVisible = false });
    }

    private bool IsLinked(EpNode n)
    {
        if (_scene == null) return false;
        foreach (var l in _scene.Links) if (l.TemplateId == n.Id || l.WallId == n.Id) return true;
        return false;
    }

    private static Geometry LinkGeometry(Point a, Point b)
    {
        double dx = (b.X - a.X) * 0.45;
        var fig = new PathFigure { StartPoint = a, IsClosed = false, IsFilled = false };
        fig.Segments!.Add(new BezierSegment { Point1 = new Point(a.X + dx, a.Y), Point2 = new Point(b.X - dx, b.Y), Point3 = b });
        var g = new PathGeometry(); g.Figures!.Add(fig);
        return g;
    }

    private NodeVisual? HitNode(Point p, double r = 16)
    {
        NodeVisual? best = null; double bd = r * r;
        foreach (var nv in _nodeVisuals)
        {
            double dx = nv.Center.X - p.X, dy = nv.Center.Y - p.Y, d2 = dx * dx + dy * dy;
            if (d2 <= bd) { bd = d2; best = nv; }
        }
        return best;
    }

    private NodeVisual? FindVisual(EpNode n) { foreach (var nv in _nodeVisuals) if (ReferenceEquals(nv.Node, n)) return nv; return null; }

    private bool IsValidPair(EpNode a, EpNode b) => EngineeringPositionBuilder.IsValidPair(_scene, a, b);

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        _canvas.Focus();
        if (_scene == null) return;
        var pp = e.GetCurrentPoint(_canvas);
        if (!pp.Properties.IsLeftButtonPressed) return;
        var p = pp.Position;

        if (HandleFacePick(p)) { e.Handled = true; return; }        // ⑦配面档：点线成对

        var hit = HitNode(p);
        if (hit != null)
        {
            if (e.ClickCount >= 2) { ClearLinksOf(hit.Node); e.Handled = true; return; }

            // 【已连节点：抓住这一端改挂】皮筋从【对端】拉出来，落到别处即把这一端改接过去
            _reattaching = null;
            var held = hit.Node;
            var exist = _scene.Links.Find(l => l.TemplateId == held.Id || l.WallId == held.Id);
            if (exist != null)
            {
                string otherId = exist.TemplateId == held.Id ? exist.WallId : exist.TemplateId;
                var other = _scene.ById(otherId);
                if (other != null)
                {
                    _reattaching = exist;
                    _scene.Links.Remove(exist);
                    held = other;
                    _selected = null;
                    Render(); RebuildLedger();
                }
            }
            _dragFrom = held;
            var from = FindVisual(held);
            _rubber = new Avalonia.Controls.Shapes.Path
            {
                Stroke = new SolidColorBrush(CLink), StrokeThickness = 2.8,
                StrokeDashArray = new AvaloniaList<double> { 5, 4 },      // 手工拉出来的是虚线：还没定
                IsHitTestVisible = false, ZIndex = 99,
                Data = LinkGeometry(from?.Center ?? hit.Center, p),
            };
            _canvas.Children.Add(_rubber);
            HighlightTargets(hit.Node, true);
            e.Pointer.Capture(_canvas);
            Status(_reattaching != null
                ? "正在改挂这一端 —— 落在别的节点上即改接过去；落空或落在接不上的地方，原来那条连线原样恢复。"
                : "松开鼠标落在另一个端点上即建立衔接（双击节点=解除它的连线）。");
            e.Handled = true;
            return;
        }

        // 空白处按下 = 平移
        _panning = true; _panStart = p; _panPvOx = _pvOx; _panPvOy = _pvOy;
        e.Pointer.Capture(_canvas);
        SelectLink(null);
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (_scene == null) return;
        var p = e.GetPosition(_canvas);
        if (_dragFrom != null && _rubber != null)
        {
            // 拖动中也要刷节点：目标端点不画出来 HitNode 就命不中
            _cursor = p;
            RefreshNodeDots();
            var from = FindVisual(_dragFrom);
            var tgt = HitNode(p);
            bool ok = tgt != null && !ReferenceEquals(tgt.Node, _dragFrom) && IsValidPair(_dragFrom, tgt.Node);
            _rubber.Data = LinkGeometry(from?.Center ?? p, ok ? tgt!.Center : p);
            _rubber.Stroke = new SolidColorBrush(ok ? CLink : Color.FromArgb(0xCC, 0x8F, 0xA4, 0xC0));
            return;
        }
        if (_panning)
        {
            _pvOx = _panPvOx - (p.X - _panStart.X) / _pvScale;
            _pvOy = _panPvOy + (p.Y - _panStart.Y) / _pvScale;
            Render();
            return;
        }
        _cursor = p;
        RefreshNodeDots();
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_scene == null) return;
        e.Pointer.Capture(null);
        if (_dragFrom != null)
        {
            var p = e.GetPosition(_canvas);
            var tgt = HitNode(p);
            HighlightTargets(_dragFrom, false);
            if (_rubber != null) { _canvas.Children.Remove(_rubber); _rubber = null; }

            if (tgt != null && IsValidPair(_dragFrom, tgt.Node))
            {
                AddLink(_dragFrom, tgt.Node);
                if (_reattaching != null) Status("已改挂：" + EngineeringPositionBuilder.Describe(_scene, _selected!));
                _reattaching = null;
            }
            else
            {
                // 【原样恢复】改挂没落成就把摘下来的那条放回去 —— 手一滑把好好的连线弄没了是最难受的"看不见的破坏"
                if (_reattaching != null)
                {
                    _scene.Links.Add(_reattaching);
                    _selected = _reattaching; _reattaching = null;
                    Render(); RebuildLedger();
                    Status(tgt == null ? "落在空白处 —— 改挂取消，原来那条连线已恢复。" : "那个节点接不上 —— 改挂取消，原来那条连线已恢复。");
                }
                else if (tgt != null)
                    Status(ReferenceEquals(tgt.Node, _dragFrom) ? "同一个节点，没建连线。" : EngineeringPositionBuilder.PairRefusal(_scene, _dragFrom, tgt.Node));
            }
            _dragFrom = null;
        }
        _panning = false;
    }

    /// <summary>滚轮缩放，以光标为锚。</summary>
    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        double wx = _pvOx + p.X / _pvScale, wy = _pvOy - p.Y / _pvScale;
        double zf = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;
        _pvScale = Math.Max(1e-9, _pvScale * zf);
        _pvOx = wx - p.X / _pvScale; _pvOy = wy + p.Y / _pvScale;
        _cursor = p;
        Render();
        e.Handled = true;
    }

    private void HighlightTargets(EpNode from, bool on)
    {
        foreach (var nv in _nodeVisuals)
        {
            if (!IsValidPair(from, nv.Node)) continue;
            nv.Glow.Opacity = on ? 0.62 : (IsLinked(nv.Node) ? 0.34 : 0.16);
            nv.Dot.StrokeThickness = on ? 2.6 : (IsLinked(nv.Node) ? 2.0 : 1.3);
        }
    }

    private void AddLink(EpNode a, EpNode b)
    {
        if (_scene == null) return;
        var link = EngineeringPositionBuilder.AddLink(_scene, a, b);
        _selected = link;
        Render(); RebuildLedger();
        Status("已衔接：" + EngineeringPositionBuilder.Describe(_scene, link));
    }

    /// <summary>自检/脚本用：按节点 Id 建一条连线（走同一套四条闸）。</summary>
    internal bool LinkByIds(string aId, string bId)
    {
        if (_scene == null) return false;
        var a = _scene.ById(aId); var b = _scene.ById(bId);
        if (a == null || b == null || !IsValidPair(a, b)) return false;
        AddLink(a, b);
        return true;
    }

    private void ClearLinksOf(EpNode n)
    {
        if (_scene == null) return;
        int before = _scene.Links.Count;
        _scene.Links.RemoveAll(l => l.TemplateId == n.Id || l.WallId == n.Id);
        if (_scene.Links.Count == before) { Status("该节点本来就没有连线。"); return; }
        _selected = null;
        Render(); RebuildLedger();
        Status($"已解除该节点的 {before - _scene.Links.Count} 条连线。");
    }

    private void RemoveLink(EpLink l)
    {
        if (_scene == null) return;
        _scene.Links.Remove(l);
        if (ReferenceEquals(_selected, l)) _selected = null;
        Render(); RebuildLedger();
        Status("已删除 1 条连线。");
    }

    private void SelectLink(EpLink? l) { _selected = l; Render(); SyncLedgerSelection(); }

    // ═══════════════════ 台账 ═══════════════════

    private void RebuildLedger()
    {
        _suppressLedgerEvent = true;
        _lstLinks.Items.Clear();
        if (_scene != null)
        {
            foreach (var l in _scene.Links)
            {
                var t = _scene.ById(l.TemplateId); var w = _scene.ById(l.WallId);
                if (t == null || w == null) continue;
                string side = t.Side == 0 ? _scene.SideAName : _scene.SideBName;
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0x33, CWallB.R, CWallB.G, CWallB.B)),
                    Child = new TextBlock { Text = side, FontSize = 10, Foreground = new SolidColorBrush(CWallB), Margin = new Thickness(6, 1, 6, 2) },
                });
                head.Children.Add(new TextBlock { Text = $"{t.Z:0.#} ⇄ {w.Z:0.#} m", FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                if (l.Suggested) head.Children.Add(new TextBlock { Text = " 自动", FontSize = 10, Foreground = new SolidColorBrush(CSug), VerticalAlignment = VerticalAlignment.Center });
                var sub = new TextBlock
                {
                    Text = $"Δz={w.Z - t.Z:+0.##;-0.##;0} m · 平距 {Dist2D(t, w):0.#} m" + (string.IsNullOrEmpty(t.Label) ? "" : $" · {t.Label}"),
                    FontSize = 10.5, Foreground = Brush.Parse("#5B6B80"), Margin = new Thickness(0, 1, 0, 0),
                };
                var sp = new StackPanel { Children = { head, sub } };
                _lstLinks.Items.Add(new ListBoxItem { Content = sp, Tag = l, Padding = new Thickness(6, 3) });
            }
        }
        _suppressLedgerEvent = false;
        SyncLedgerSelection();
        _txtLinkCount.Text = (_scene?.Links.Count ?? 0).ToString();
    }

    private static double Dist2D(EpNode a, EpNode b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private string SideName(int side) => _scene == null ? (side == 0 ? "A 端" : "B 端") : (side == 0 ? _scene.SideAName : _scene.SideBName);

    private void SyncLedgerSelection()
    {
        _suppressLedgerEvent = true;
        _lstLinks.SelectedIndex = -1;
        if (_selected != null)
            for (int i = 0; i < _lstLinks.Items.Count; i++)
                if (_lstLinks.Items[i] is ListBoxItem it && ReferenceEquals(it.Tag, _selected)) { _lstLinks.SelectedIndex = i; break; }
        _suppressLedgerEvent = false;
    }

    // ═══════════════════ ③ 生成衔接并落地 ═══════════════════

    private List<EpConnector> BuildConnectors()
    {
        double gradePct = TryD(_txtGrade, out double gp) && gp > 0 ? gp : EpConnectorBuilder.DefaultGradePct;
        double fillet = ArcJoin && TryD(_txtFilletR, out double fr) && fr > 0 ? fr : 0;
        return EpConnectorBuilder.Build(_scene, gradePct, sameLevelTol: 0.5, filletRadius: fillet);
    }

    internal async void Materialize()
    {
        if (_scene == null) { Warn("请先「①提取节点」。"); return; }
        if (_scene.Links.Count == 0) { Warn("还没有任何连线——在平面图上把一个端点拖到另一个端点上，或先「就近自动配对」。"); return; }
        var cons = BuildConnectors();
        double gradePct = TryD(_txtGrade, out double gp) && gp > 0 ? gp : EpConnectorBuilder.DefaultGradePct;
        if (cons.Count == 0) { Warn("连线没能产出几何（节点成员为空？）。"); return; }

        int overGrade = 0; double eaten = 0;
        foreach (var c in cons) { if (c.OverGrade) overGrade++; eaten += c.WallEaten; }
        if (overGrade > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{overGrade}/{cons.Count} 段衔接超过纵坡闸 {gradePct:0.##}%：沿端帮的退路吃到尽头，仍缓不下来。");
            sb.AppendLine("多半是这一级新旧台阶标高差得太多，或端帮那一侧本来就没多少线可吃。");
            int shown = 0;
            foreach (var c in cons) { if (!c.OverGrade || shown++ >= 6) continue; sb.AppendLine("  · " + c.Describe(SideName(c.Side))); }
            if (overGrade > 6) sb.AppendLine($"  …另有 {overGrade - 6} 段（信息栏有全部明细）");
            sb.AppendLine(); sb.AppendLine("仍按当前坡度落地？（几何照出，台账会标出这几段）");
            if (!await BlockMsgBox.ConfirmAsync(this, "创建工程位置 · 衔接超闸", sb.ToString(), "仍然落地", "取消"))
            { Status($"已取消落地。{overGrade} 段超闸 —— 放宽纵坡闸、或先把这一级的标高对齐再来。"); return; }
        }

        try
        {
            _land(cons, _scene, gradePct);
            SaveLinks(_scene);
            int drawn = cons.Count(c => c.HasGeometry), marks = cons.Count(c => c.Overlapped);
            string msg = $"衔接落地✓：{_scene.Links.Count} 处配对 → {drawn} 段入图（图层「{LinkLayer}」" +
                         (marks > 0 ? $"，另有 {marks} 处两线已交叉只在「{CrossLayer}」落了交点标记" : "") + $"）。{EpConnectorBuilder.Summarize(cons, gradePct)} " +
                         $"配对关系已按范围「{(string.IsNullOrEmpty(_scene.RegionName) ? "(无范围)" : _scene.RegionName)}」存档。" +
                         (eaten > 0 ? $" 注：过渡段沿端帮盖住了 {eaten:0.#}m 老台阶线 —— 本功能不动原实体，那一段要不要裁请对着账目定。" : "");
            Status(msg);
            _echo("创建工程位置 · " + msg, false);
            foreach (var c in cons) _echo("    " + c.Describe(SideName(c.Side)), c.OverGrade || c.NoJoin);
        }
        catch (Exception ex) { Warn("落地异常：" + ex.Message); }
    }

    // ═══════════════════ 配对存档（跨会话）═══════════════════
    // 存的是【侧别 + 标高 + 平面位置】，不是节点 Id —— 重新提取一次 Id 就变了

    private static string SettingsKey(string region) => SettingsKeyPrefix + (string.IsNullOrEmpty(region) ? "_default" : region);

    private void SaveLinks(EpScene s)
    {
        try
        {
            var dto = new EpLinkStoreDto { RegionName = s.RegionName, SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), TplPairMode = true };
            foreach (var l in s.Links)
            {
                var t = s.ById(l.TemplateId); var w = s.ById(l.WallId);
                if (t == null || w == null) continue;
                dto.Links.Add(new EpLinkDto
                {
                    Side = t.Side, TemplateZ = t.Z, WallZ = w.Z, WallIsTemplate = w.Kind == EpNodeKind.Template, WallSide = w.Side,
                    TX = t.X, TY = t.Y, WX = w.X, WY = w.Y,
                    Note = EngineeringPositionBuilder.Describe(s, l),
                });
            }
            UserSettings.Current.Set(SettingsKey(s.RegionName), dto);
            UserSettings.Current.Flush();
        }
        catch { /* 存档失败不影响落地 */ }
    }

    private void RestoreSavedLinks(EpScene s)
    {
        try
        {
            var dto = UserSettings.Current.Get<EpLinkStoreDto>(SettingsKey(s.RegionName));
            if (dto?.Links == null || dto.Links.Count == 0) return;
            double tol = TryD(_txtGroupTol, out double g) && g > 0 ? Math.Max(g, 0.5) : 0.5;
            int hit = 0;
            foreach (var d in dto.Links)
            {
                var t = Nearest(s, EpNodeKind.Template, d.Side, d.TemplateZ, tol, d.TX, d.TY);
                var w = Nearest(s, d.WallIsTemplate ? EpNodeKind.Template : EpNodeKind.EndWall, d.WallSide >= 0 ? d.WallSide : d.Side, d.WallZ, tol, d.WX, d.WY);
                if (t == null || w == null) continue;
                if (s.Links.Exists(x => x.TemplateId == t.Id || x.WallId == w.Id)) continue;
                if (!EngineeringPositionBuilder.IsValidPair(s, t, w)) continue;
                s.Links.Add(new EpLink { TemplateId = t.Id, WallId = w.Id, Suggested = false });
                hit++;
            }
            if (hit > 0) Status($"已按标高+位置回填上次（{dto.SavedAt}）存下的配对 {hit}/{dto.Links.Count} 处。");
        }
        catch { }
    }

    /// <summary>同侧同类、|Δz| ≤ tol 里离存档位置最近的节点（没存位置的老档只按标高）。</summary>
    private static EpNode? Nearest(EpScene s, EpNodeKind kind, int side, double z, double tol, double px, double py)
    {
        EpNode? best = null; double bd = double.MaxValue;
        bool hasPos = !double.IsNaN(px) && !double.IsNaN(py);
        foreach (var n in s.Nodes)
        {
            if (n.Kind != kind || n.Side != side) continue;
            double dz = Math.Abs(n.Z - z);
            if (dz > tol) continue;
            double d = hasPos ? Math.Sqrt((n.X - px) * (n.X - px) + (n.Y - py) * (n.Y - py)) : dz;
            if (hasPos && d > 5.0) continue;      // 位置差 5m 以上就不是同一个端点
            if (d < bd) { bd = d; best = n; }
        }
        return best;
    }

    internal int NodeCount => _scene?.Nodes.Count ?? 0;
    internal int LinkCount => _scene?.Links.Count ?? 0;
    internal string DiagText => _txtDiag.Text ?? "";
    internal string StatusText => _txtStatus.Text ?? "";
}
