using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「境界圈定设置」窗口（生产计划编制·优化开采设计 按钮①；移植原 <c>PlanLib.BoundaryOptimization.PitSchemeConfigWindow</c>）。
/// 编辑「境界优化方案」PitScheme：① 矿床/原则（块体 PCA 自动识别）② 经济合理剥采比（4 公式实时算）③ 分帮边坡角
/// （SlopeDesign 载入 / 按方位绑定 / 按境界线段分帮）④ 面与界线（地表/煤层顶底板/底周界/地表界 按 handle 引用）
/// ⑤ 底部·底宽·台阶 ⑥ 横剖面布置（走向检测/间距推荐/预览剖面线）。方案集会话级共享（<see cref="BoundarySchemeStore"/>）。
/// </summary>
internal sealed class PitSchemeConfigWindow : Window
{
    public ObservableCollection<PitScheme> Schemes { get; }
    private readonly IPlanEntityHost _host;

    private readonly ListBox _schemeList = new() { MinHeight = 200 };
    private readonly ComboBox _depositCombo, _principleCombo, _methodCombo, _econMethodCombo, _equipCombo;
    private readonly TextBlock _dipText = PlanUi.Value("—"), _strikeText = PlanUi.Value("—"), _seamText = PlanUi.Value("—");
    private readonly TextBlock _econFormulaText = new() { FontWeight = FontWeight.Bold, Foreground = PlanUi.TealBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _econResultText = PlanUi.Value("—", 22);
    private readonly TextBox _priceBox = PlanUi.Box("320"), _mineCostBox = PlanUi.Box("95"), _stripCostBox = PlanUi.Box("28"),
                             _ugCostBox = PlanUi.Box(""), _profitBox = PlanUi.Box(""), _reclaimBox = PlanUi.Box("");
    private readonly TextBox _bottomWidthBox = PlanUi.Box("60", 100), _workWidthBox = PlanUi.Box("40", 100), _contractionBox = PlanUi.Box("0", 100),
                             _benchHeightBox = PlanUi.Box("12", 100), _faceAngleBox = PlanUi.Box("70", 100),
                             _strikeAzBox = PlanUi.Box("75", 100), _spacingBox = PlanUi.Box("50", 100);
    private readonly TextBlock _terrainText = PlanUi.Value("（未指定）"), _bottomSrcText = PlanUi.Value("矿体投影(自动)"),
                               _surfaceBoundaryText = PlanUi.Value("（未指定，用块体足迹）"), _sectionRangeText = PlanUi.Value("（需激活块体模型）");
    private readonly DataGrid _wallGrid, _seamGrid;
    private readonly TextBlock _status = RoadUi.Hint("");

    private static readonly string[] EquipItems = { "WK-10 电铲", "WK-20 电铲", "液压挖掘机 EX-3600" };

    public PitSchemeConfigWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "境界圈定设置 — 方案配置";
        PlanUi.Place(this, 1040, 760);
        Schemes = BoundarySchemeStore.Schemes;

        _depositCombo = PlanUi.Combo(new[] { "水平 / 近水平（微倾斜）", "缓倾斜", "倾斜", "急倾斜", "多煤层 / 复合", "不规则 / 块状（金属）" }, 2);
        _principleCombo = PlanUi.Combo(new[] { "平均剥采比原则", "境界剥采比原则", "生产剥采比原则" }, 1);
        _methodCombo = PlanUi.Combo(new[] { "横剖面法", "移动圆锥法", "图论 L-G 法", "网络流法" }, 0);
        _econMethodCombo = PlanUi.Combo(new[] { "成本比较法（替代原则）", "价格法", "价格法 + 盈利", "价格法 + 盈利 + 复垦" }, 1, 320);
        _econMethodCombo.SelectionChanged += (_, _) => RenderEconFormula();
        _equipCombo = PlanUi.Combo(EquipItems, 0);

        _wallGrid = PlanUi.EditableTable(new (string, string, double, bool)[]
        {
            ("帮别", "SideName", 0, false), ("帮型", "SideType", 90, false), ("最终帮坡角 β(°)", "BetaDeg", 130, false),
            ("安全系数 F", "SafetyF", 100, false), ("来源", "Source", 110, true), ("校核", "OkText", 56, true),
        }, 160);
        _seamGrid = PlanUi.EditableTable(new (string, string, double, bool)[]
        {
            ("煤层", "Name", 0, false), ("顶板 handle", "RoofHandle", 120, false), ("底板 handle", "FloorHandle", 120, false),
        }, 120);

        Content = PlanUi.Shell(
            PlanUi.Header("境界圈定设置", "编辑「境界优化方案」(PitScheme) ·  能自动化的已自动预填，你只需确认价/成本与矿床判型 ·  可存多个方案备比选"),
            BuildBody(), BuildFooter());

        _schemeList.ItemsSource = Schemes;
        _schemeList.SelectionChanged += (_, _) => { if (Current != null) LoadFromScheme(Current); };
        if (Schemes.Count > 0) _schemeList.SelectedIndex = 0;
    }

    private PitScheme? Current => _schemeList.SelectedItem as PitScheme;

    // ───────────── 布局 ─────────────
    private Control BuildBody()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("232,*") };

        // 左：方案管理
        var btns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        btns.Children.Add(RoadUi.Btn("新建", OnNewScheme, 50));
        btns.Children.Add(RoadUi.Btn("克隆", OnCloneScheme, 50));
        btns.Children.Add(RoadUi.Btn("删除", OnDeleteScheme, 50));
        var dock = new DockPanel();
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Bottom);
        dock.Children.Add(btns);
        _schemeList.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
        dock.Children.Add(_schemeList);
        var left = PlanUi.Group("方案管理", dock, new Thickness(12, 12, 6, 12), 8);
        Grid.SetColumn(left, 0); g.Children.Add(left);

        // 右：6 个分组
        var stack = new StackPanel();
        stack.Children.Add(BuildDepositGroup());
        stack.Children.Add(BuildEconGroup());
        stack.Children.Add(BuildWallGroup());
        stack.Children.Add(BuildGeometryGroup());
        stack.Children.Add(BuildBottomGroup());
        stack.Children.Add(BuildSectionGroup());
        var sv = new ScrollViewer { Content = stack, Margin = new Thickness(6, 12, 12, 12), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(sv, 1); g.Children.Add(sv);
        return g;
    }

    private Control BuildDepositGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("矿床类型决定剥采比控制原则与圈定方法（采矿手册：水平/近水平→平均剥采比；倾斜/急倾斜→境界剥采比；分期→生产剥采比）。"));
        sp.Children.Add(PlanUi.LabeledRow("矿床类型", _depositCombo, RoadUi.Btn("自动识别(块体PCA)", OnAutoDetectDeposit), 120, 220, new Thickness(0, 10, 0, 8)));
        var auto = RoadUi.Row(RoadUi.Hint("自动识别："), RoadUi.Lbl("  平均倾角 "), _dipText, RoadUi.Lbl("   走向方位 "), _strikeText, RoadUi.Lbl("   煤层数 "), _seamText);
        sp.Children.Add(PlanUi.InfoBox(auto));
        sp.Children.Add(PlanUi.LabeledRow("剥采比控制原则", _principleCombo, null, 120, 220, new Thickness(0, 0, 0, 6)));
        sp.Children.Add(PlanUi.LabeledRow("圈定方法", _methodCombo, null, 120, 220, new Thickness(0)));
        return PlanUi.Group("① 矿床与剥采比原则", sp);
    }

    private Control BuildEconGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("体积口径 m³/t（与剥采比均衡 VP 曲线一致）。仅露天可行→价格法；露天/地下都可行→成本比较法。"));
        sp.Children.Add(PlanUi.LabeledRow("计算方法", _econMethodCombo, null, 120, 320, new Thickness(0, 10, 0, 8)));
        sp.Children.Add(PlanUi.LabeledRow("公式", _econFormulaText, null, 120, double.NaN, new Thickness(0, 0, 0, 10)));

        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        grid.Children.Add(Param("d 原煤售价", _priceBox, " 元/t"));
        grid.Children.Add(Param("a 露天采矿成本", _mineCostBox, " 元/t"));
        grid.Children.Add(Param("b 剥离成本", _stripCostBox, " 元/m³"));
        grid.Children.Add(Param("C_D 地下采矿成本", _ugCostBox, " 元/t"));
        grid.Children.Add(Param("e 最低盈利", _profitBox, " 元/t"));
        grid.Children.Add(Param("c 复垦费", _reclaimBox, " 元/t"));
        sp.Children.Add(grid);

        var res = new StackPanel { Orientation = Orientation.Horizontal };
        var lbl = new TextBlock { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        lbl.Inlines!.Add(new Run("经济合理剥采比 n"));
        lbl.Inlines.Add(Sub("经"));
        lbl.Inlines.Add(new Run(" = "));
        res.Children.Add(lbl);
        res.Children.Add(_econResultText);
        var unit = RoadUi.Hint(" m³/t"); unit.VerticalAlignment = VerticalAlignment.Bottom; unit.Margin = new Thickness(2, 0, 16, 3);
        res.Children.Add(unit);
        res.Children.Add(RoadUi.Btn("计算", OnComputeEconRatio, 60));
        sp.Children.Add(PlanUi.InfoBox(res, new Thickness(0, 8, 0, 0)));
        return PlanUi.Group("② 经济合理剥采比 n_经", sp);
    }

    private static StackPanel Param(string label, TextBox box, string unit)
    {
        var l = RoadUi.Lbl(label); l.Width = 110; l.VerticalAlignment = VerticalAlignment.Center;
        var u = RoadUi.Hint(unit); u.VerticalAlignment = VerticalAlignment.Center;
        var sp = RoadUi.Row(l, box, u); sp.Margin = new Thickness(0, 4);
        return sp;
    }

    private Control BuildWallGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("各端帮最终帮坡角 β（来自 SlopeDesign，自动按方位绑定 + 稳定性校核 F=tanφ/tanβ；可覆盖）。"));
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        wrap.Children.Add(RoadUi.Btn("从 SlopeDesign 载入", OnLoadFromSlopeDesign, 140));
        wrap.Children.Add(RoadUi.Btn("按方位自动绑定", OnAutoBindWalls, 120));
        var seg = RoadUi.Btn("按境界线段分帮…", async () => await OnAssignSegmentBetaAsync(), 140);
        ToolTip.SetTip(seg, "读「④面与界线」的地表界多段线，逐直线段交互指定各帮最终帮坡角");
        wrap.Children.Add(seg);
        var addWall = RoadUi.Small("+ 添加帮", () => { if (Current is { } c) c.Walls.Add(new WallAngle { SideName = "新帮", BetaDeg = 40, Source = "手填" }); });
        var delWall = RoadUi.Small("− 删除选中帮", () => { if (Current is { } c && _wallGrid.SelectedItem is WallAngle w) c.Walls.Remove(w); });
        wrap.Children.Add(addWall); wrap.Children.Add(delWall);
        sp.Children.Add(wrap);
        sp.Children.Add(_wallGrid);
        return PlanUi.Group("③ 分帮最终边坡角", sp);
    }

    private Control BuildGeometryGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("境界优化引用的面与界线。几何存于图纸（按 handle 引用，不复制）；地表/煤层顶底板为输入，地表界为求解输出。"));
        var bTerrain = RoadUi.Btn("选择面…", () => OpenSurfacePick("选择地表面", PlanEntityType.TriangleMesh, h =>
        {
            if (Current is not { } cur) return;
            cur.Geometry.TerrainHandle = h; _terrainText.Text = $"handle {h}";
        }));
        ToolTip.SetTip(bTerrain, "打开选择对话框：①视口拾取 ②从图纸面清单选");
        sp.Children.Add(PlanUi.LabeledRow("地表面", _terrainText, bTerrain, 120, double.NaN, new Thickness(0, 8, 0, 8)));
        sp.Children.Add(RoadUi.Hint("煤层顶底板（可多层；handle=0 表示未指定）"));
        sp.Children.Add(_seamGrid);
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        wrap.Children.Add(RoadUi.Btn("添加煤层", () => { if (Current is { } cur) cur.Geometry.Seams.Add(new SeamSurfaceRef { Name = $"煤层{cur.Geometry.Seams.Count + 1}" }); }, 80));
        wrap.Children.Add(RoadUi.Btn("删除煤层", () => { if (Current is { } cur && _seamGrid.SelectedItem is SeamSurfaceRef sr) cur.Geometry.Seams.Remove(sr); }, 80));
        wrap.Children.Add(RoadUi.Btn("选顶板…", () => PickSeam(true), 84));
        wrap.Children.Add(RoadUi.Btn("选底板…", () => PickSeam(false), 84));
        sp.Children.Add(wrap);
        var bBottom = RoadUi.Btn("选择线…", () => OpenSurfacePick("选择底周界（种子线）", PlanEntityType.Polyline, h =>
        {
            if (Current is not { } cur) return;
            cur.Geometry.BottomSeedHandle = h; cur.BottomSource = $"handle {h}"; _bottomSrcText.Text = $"handle {h}";
        }));
        sp.Children.Add(PlanUi.LabeledRow("底周界（种子）", _bottomSrcText, bBottom, 120, double.NaN, new Thickness(0, 10, 0, 8)));
        var bLimit = RoadUi.Btn("选择线…", () => OpenSurfacePick("选择地表界（顶口限制线）", PlanEntityType.Polyline, h =>
        {
            if (Current is not { } cur) return;
            cur.Geometry.SurfaceLimitHandle = h; _surfaceBoundaryText.Text = $"handle {h}";
        }));
        ToolTip.SetTip(bLimit, "选地表界/矿权限制线作顶口；配合最小底宽按 β 圈定（不选则用块体足迹兜底）");
        sp.Children.Add(PlanUi.LabeledRow("地表界（顶口限制线）", _surfaceBoundaryText, bLimit, 120, double.NaN, new Thickness(0)));
        return PlanUi.Group("④ 面与界线（地表 / 煤层顶底板 / 底周界 / 地表界）", sp);
    }

    private Control BuildBottomGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("底宽按采装设备规格自动；最小工作面宽供缝合时约束；台阶高 H / 坡面角 α 决定三维台阶面与台阶线（平盘宽 W=H/tanβ−H/tanα 各帮自动反算）。"));
        sp.Children.Add(PlanUi.LabeledRow("最小底宽 (m)", _bottomWidthBox, RoadUi.Btn("按采装设备规格自动", OnAutoBottomWidth), 120, 100, new Thickness(0, 8, 0, 8)));
        sp.Children.Add(PlanUi.LabeledRow("采装设备", _equipCombo, null, 120, 220));
        sp.Children.Add(PlanUi.LabeledRow("最小工作面宽 (m)", _workWidthBox, null, 120, 100, new Thickness(0)));
        sp.Children.Add(PlanUi.LabeledRow("整体收缩 (m)", _contractionBox, null, 120, 100, new Thickness(0, 8, 0, 0),
            tip: "在境界范围内整体向内收缩边界形态（0=满采到算出境界）；逐段收缩在③「按境界线段分帮…」里设。求解储量、落地台阶面都随之变。"));
        sp.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 8) });
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,100,120,100") };
        var l1 = RoadUi.Lbl("台阶高 H (m)"); Grid.SetColumn(l1, 0); g.Children.Add(l1);
        Grid.SetColumn(_benchHeightBox, 1); g.Children.Add(_benchHeightBox);
        var l2 = RoadUi.Lbl("坡面角 α (°)"); l2.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(l2, 2); g.Children.Add(l2);
        Grid.SetColumn(_faceAngleBox, 3); g.Children.Add(_faceAngleBox);
        foreach (var c in g.Children) c.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(g);
        return PlanUi.Group("⑤ 底部 · 底宽 · 台阶", sp);
    }

    private Control BuildSectionGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("沿走向自动布置横剖面（倾斜/急倾斜长露天矿）。走向由块体几何自动检测，间距按块体尺寸推荐。"));
        sp.Children.Add(PlanUi.LabeledRow("走向方位 (°)", _strikeAzBox, RoadUi.Btn("自动检测走向", OnAutoDetectStrike), 120, 100, new Thickness(0, 8, 0, 8)));
        sp.Children.Add(PlanUi.LabeledRow("剖面间距 (m)", _spacingBox, RoadUi.Btn("按块体尺寸推荐", OnRecommendSpacing), 120, 100));
        sp.Children.Add(PlanUi.LabeledRow("剖面范围 / 数量", _sectionRangeText, null, 120, double.NaN));
        var b = RoadUi.Btn("预览剖面线", OnPreviewSections, 110); b.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(b);
        _spacingBox.LostFocus += (_, _) => UpdateSectionRange();
        return PlanUi.Group("⑥ 横剖面布置", sp, new Thickness(0, 0, 0, 4));
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var bAuto = RoadUi.Btn("全部自动重填", OnAutoRefillAll, 110);
        DockPanel.SetDock(bAuto, Avalonia.Controls.Dock.Left); dock.Children.Add(bAuto);
        var right = RoadUi.Foot(RoadUi.Btn("保存方案", OnSaveScheme, 90), RoadUi.Btn("关闭", Close, 70));
        right.Margin = new Thickness(0);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right); dock.Children.Add(right);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new Thickness(14, 0, 0, 0);
        dock.Children.Add(_status);
        return PlanUi.Footer(dock);
    }

    // ───────────── 方案 ↔ 控件 ─────────────
    private void LoadFromScheme(PitScheme s)
    {
        _depositCombo.SelectedIndex = (int)s.Deposit;
        _principleCombo.SelectedIndex = (int)s.Principle;
        _methodCombo.SelectedIndex = (int)s.Method;
        _dipText.Text = s.AutoDipDeg is { } d ? $"{d:0.#}°" : "—";
        _strikeText.Text = s.AutoStrikeDeg is { } st ? $"{st:0.#}°" : "—";
        _seamText.Text = s.AutoSeamCount?.ToString() ?? "—";

        _econMethodCombo.SelectedIndex = (int)s.Econ.Method;
        _priceBox.Text = PlanUi.Num(s.Econ.Price);
        _mineCostBox.Text = PlanUi.Num(s.Econ.MiningCost);
        _stripCostBox.Text = PlanUi.Num(s.Econ.StripCost);
        _ugCostBox.Text = PlanUi.NumOpt(s.Econ.UndergroundCost);
        _profitBox.Text = PlanUi.NumOpt(s.Econ.MinProfit);
        _reclaimBox.Text = PlanUi.NumOpt(s.Econ.ReclaimCost);
        ShowEcon(s.Econ.ComputeEconRatio());
        RenderEconFormula();

        _bottomWidthBox.Text = PlanUi.Num(s.BottomWidthM);
        _workWidthBox.Text = PlanUi.Num(s.MinWorkingWidthM);
        _contractionBox.Text = PlanUi.Num(s.ContractionM);
        _bottomSrcText.Text = s.BottomSource;
        _terrainText.Text = s.Geometry.TerrainHandle != 0 ? $"handle {s.Geometry.TerrainHandle}" : "（未指定）";
        _seamGrid.ItemsSource = s.Geometry.Seams;
        _surfaceBoundaryText.Text = s.Geometry.SurfaceLimitHandle != 0 ? $"handle {s.Geometry.SurfaceLimitHandle}" : "（未指定，用块体足迹）";
        _strikeAzBox.Text = PlanUi.Num(s.StrikeAzimuthDeg);
        _spacingBox.Text = PlanUi.Num(s.SectionSpacingM);

        _benchHeightBox.Text = PlanUi.Num(s.BenchHeightM);
        _faceAngleBox.Text = PlanUi.Num(s.BenchFaceAngleDeg);
        int ei = Array.IndexOf(EquipItems, s.EquipmentRef);
        _equipCombo.SelectedIndex = ei >= 0 ? ei : 0;

        _wallGrid.ItemsSource = s.Walls;
        UpdateSectionRange();
    }

    private void SaveToScheme(PitScheme s)
    {
        s.Deposit = (DepositType)Math.Max(0, _depositCombo.SelectedIndex);
        s.Principle = (StripRatioPrinciple)Math.Max(0, _principleCombo.SelectedIndex);
        s.Method = (DelineationMethod)Math.Max(0, _methodCombo.SelectedIndex);
        ReadEconInto(s.Econ);
        s.BottomWidthM = PlanUi.D(_bottomWidthBox);
        s.MinWorkingWidthM = PlanUi.D(_workWidthBox);
        s.ContractionM = PlanUi.D(_contractionBox);
        s.StrikeAzimuthDeg = PlanUi.D(_strikeAzBox);
        s.SectionSpacingM = PlanUi.D(_spacingBox);
        s.BenchHeightM = PlanUi.D(_benchHeightBox);
        s.BenchFaceAngleDeg = PlanUi.D(_faceAngleBox);
        s.EquipmentRef = _equipCombo.SelectedItem as string ?? s.EquipmentRef;
        s.Result = null; // 参数已变，旧求解结果作废
    }

    private void ReadEconInto(EconParams ec)
    {
        ec.Method = (EconRatioMethod)Math.Max(0, _econMethodCombo.SelectedIndex);
        ec.Price = PlanUi.D(_priceBox);
        ec.MiningCost = PlanUi.D(_mineCostBox);
        ec.StripCost = PlanUi.D(_stripCostBox);
        ec.UndergroundCost = PlanUi.D(_ugCostBox);
        ec.MinProfit = PlanUi.D(_profitBox);
        ec.ReclaimCost = PlanUi.D(_reclaimBox);
    }

    // ───────────── ① 矿床与原则 ─────────────
    private void OnAutoDetectDeposit()
    {
        var m = _host.ActiveBlockModel;
        if (m == null) { _status.Text = "（自动识别）无激活块体模型，请先在「块体模型」里导入/选择一个模型"; return; }
        (Cad.DepositSignature sig, string attr)? r;
        try { r = BlockModelCoal.DetectAuto(m); }
        catch (Exception ex) { _status.Text = $"（自动识别）失败：{ex.Message}"; return; }
        if (r is not { } rr)
        {
            _status.Text = "（自动识别）未找到煤/岩属性或煤单元过少，请在块体模型里指定煤属性（名称含 coal/煤，或分类列含\"煤\"标签）";
            return;
        }
        var s = rr.sig;
        var dep = ClassifyDeposit(s.DipDeg, s.SeamCount);
        var prin = PrincipleFor(dep);
        var meth = MethodFor(dep);

        _dipText.Text = $"{s.DipDeg:0.#}°";
        _strikeText.Text = $"{s.StrikeAzimuthDeg:0.#}°";
        _seamText.Text = s.SeamCount.ToString();
        _depositCombo.SelectedIndex = (int)dep;
        _principleCombo.SelectedIndex = (int)prin;
        _methodCombo.SelectedIndex = (int)meth;
        _strikeAzBox.Text = s.StrikeAzimuthDeg.ToString("0.#");

        if (Current is { } cur)
        {
            cur.AutoDipDeg = s.DipDeg; cur.AutoStrikeDeg = s.StrikeAzimuthDeg; cur.AutoSeamCount = s.SeamCount;
            cur.Deposit = dep; cur.Principle = prin; cur.Method = meth;
            cur.StrikeAzimuthDeg = s.StrikeAzimuthDeg;
        }
        _status.Text = $"自动识别：{DepositCn(dep)} · 倾角 {s.DipDeg:0.#}° · 走向 {s.StrikeAzimuthDeg:0.#}° · {s.SeamCount} 层 · 煤单元 {s.CoalCellCount:N0}（属性 {rr.attr}）→ 已派{PrincipleCn(prin)}";
    }

    /// <summary>按倾角/煤层数分类矿床（手册阈值：&lt;5 近水平 / &lt;15 缓 / &lt;45 倾斜 / ≥45 急倾斜；多层优先）。</summary>
    public static DepositType ClassifyDeposit(double dipDeg, int seamCount)
    {
        if (seamCount > 1) return DepositType.MultiSeam;
        if (dipDeg < 5) return DepositType.NearHorizontal;
        if (dipDeg < 15) return DepositType.GentleDip;
        if (dipDeg < 45) return DepositType.Inclined;
        return DepositType.SteepDip;
    }
    public static StripRatioPrinciple PrincipleFor(DepositType d)
        => d == DepositType.NearHorizontal ? StripRatioPrinciple.Average : StripRatioPrinciple.Contour;
    public static DelineationMethod MethodFor(DepositType d)
        => d == DepositType.IrregularMassive ? DelineationMethod.FloatingCone : DelineationMethod.CrossSection;
    private static string DepositCn(DepositType d) => d switch
    {
        DepositType.NearHorizontal => "水平/近水平", DepositType.GentleDip => "缓倾斜", DepositType.Inclined => "倾斜",
        DepositType.SteepDip => "急倾斜", DepositType.MultiSeam => "多煤层", DepositType.IrregularMassive => "不规则/块状", _ => "—"
    };
    private static string PrincipleCn(StripRatioPrinciple p) => p switch
    {
        StripRatioPrinciple.Average => "平均剥采比原则", StripRatioPrinciple.Contour => "境界剥采比原则", StripRatioPrinciple.Production => "生产剥采比原则", _ => "—"
    };

    // ───────────── ② 经济合理剥采比 ─────────────
    private void RenderEconFormula()
    {
        var inl = _econFormulaText.Inlines!;
        inl.Clear();
        inl.Add(new Run("n")); inl.Add(Sub("经")); inl.Add(new Run(" = "));
        switch (_econMethodCombo.SelectedIndex)
        {
            case 0: inl.Add(new Run("(")); inl.Add(new Run("C")); inl.Add(Sub("D")); inl.Add(new Run(" − a) ⁄ b")); break;
            case 2: inl.Add(new Run("[d − (a + e)] ⁄ b")); break;
            case 3: inl.Add(new Run("[d − (a + e + c)] ⁄ b")); break;
            default: inl.Add(new Run("(d − a) ⁄ b")); break;
        }
    }
    private static Run Sub(string s) => new(s) { BaselineAlignment = BaselineAlignment.Subscript, FontSize = 9 };

    private void OnComputeEconRatio()
    {
        if (Current == null) return;
        ReadEconInto(Current.Econ);
        var n = Current.Econ.ComputeEconRatio();
        ShowEcon(n);
        _status.Text = n is { } v ? $"经济合理剥采比 n_经 = {v:0.00} m³/t" : "参数不足，无法计算 n_经（剥离成本 b 必须 > 0）";
    }
    private void ShowEcon(double? n) => _econResultText.Text = n is { } v ? v.ToString("0.00") : "—";

    // ───────────── ③ 分帮边坡角 ─────────────
    /// <summary>从边坡设计库读各帮最终帮坡角 β + 稳定性校核（每帮取最新有效设计）。</summary>
    private void OnLoadFromSlopeDesign()
    {
        if (Current is not { } cur) return;
        var conn = _host.Db;
        if (conn == null) { _status.Text = "（SlopeDesign）边坡设计库不可用（需先打开/初始化「数据库」模块）"; return; }
        List<(string side, string type, double beta, double? f, double? phi, string from)> designs;
        try { designs = PlanDb.LoadSlopeDesigns(conn); }
        catch (Exception ex) { _status.Text = $"（SlopeDesign）读取失败：{ex.Message}"; return; }

        var latest = designs
            .GroupBy(d => d.side)
            .Select(g => g.OrderByDescending(d => d.from).First())
            .Where(d => d.beta > 0)
            .ToList();
        if (latest.Count == 0) { _status.Text = "（SlopeDesign）边坡设计库为空或无有效帮坡角，请先在「数据库」录入边坡设计"; return; }

        cur.Walls.Clear();
        foreach (var d in latest)
        {
            double? f = d.f;
            if (!f.HasValue && d.phi is { } phi && d.beta > 0)
                f = Math.Tan(phi * Math.PI / 180.0) / Math.Tan(d.beta * Math.PI / 180.0);
            cur.Walls.Add(new WallAngle
            {
                SideName = d.side, SideType = string.IsNullOrWhiteSpace(d.type) ? "final" : d.type,
                BetaDeg = d.beta, SafetyF = f, Source = "SlopeDesign", Ok = (f ?? 0) >= 1.30,
            });
        }
        _wallGrid.ItemsSource = cur.Walls;
        _status.Text = $"已从 SlopeDesign 载入 {cur.Walls.Count} 个帮的最终帮坡角 + 稳定性校核（F≥1.30 为安全）";
    }

    /// <summary>按地表界各直线段的外法向方位，自动绑定帮别 + 取对应帮 β（填 WallSegments，可再微调）。</summary>
    private void OnAutoBindWalls()
    {
        if (Current is not { } cur) return;
        if (cur.Geometry.SurfaceLimitHandle == 0) { _status.Text = "（按方位绑定）请先在「④面与界线」选地表界"; return; }
        var top = PitSchemeEnvelope.ResolveTopOutline(_host, cur.Geometry.SurfaceLimitHandle, null);
        if (top == null || !top.FromSurfaceLimit || top.Count < 3) { _status.Text = "（按方位绑定）地表界读取失败或非闭合多段线"; return; }

        cur.Geometry.WallSegments.Clear();
        for (int i = 0; i < top.Count; i++)
        {
            double az = PitSchemeEnvelope.EdgeNormalAzimuth(top, i);
            cur.Geometry.WallSegments.Add(new SegmentBeta
            {
                Index = i, SideName = WallSegmentDialog.DirName(az),
                BetaDeg = PitSchemeEnvelope.BetaForAzimuth(cur.Walls, az), InsetExtraM = 0,
            });
        }
        _status.Text = $"已按方位把地表界 {top.Count} 段绑定到帮别 + 取对应 β（在③「按境界线段分帮…」可逐段微调）";
    }

    private async System.Threading.Tasks.Task OnAssignSegmentBetaAsync()
    {
        if (Current is not { } cur) return;
        if (cur.Geometry.SurfaceLimitHandle == 0) { _status.Text = "请先在「④ 面与界线」选地表界(顶口限制线)，再按段分帮"; return; }
        var dlg = new WallSegmentDialog(_host, cur.Geometry.SurfaceLimitHandle, cur.Geometry.WallSegments, cur.Walls);
        await dlg.ShowDialog(this);
        if (dlg.Accepted)
        {
            cur.Geometry.WallSegments.Clear();
            foreach (var sg in dlg.ToSegments()) cur.Geometry.WallSegments.Add(sg);
            _status.Text = $"已设定各段帮坡角 + 逐段额外收缩（{cur.Geometry.WallSegments.Count} 段）→ 整体收缩在主面板「⑤ 底部与底宽」填";
        }
    }

    // ───────────── ④ 面与界线 / ⑤ 底部 ─────────────
    private void OnAutoBottomWidth()
    {
        string eq = _equipCombo.SelectedItem as string ?? Current?.EquipmentRef ?? "";
        double w = RecommendBottomWidth(eq);
        _bottomWidthBox.Text = PlanUi.Num(w);
        if (Current is { } cur) { cur.BottomWidthM = w; cur.EquipmentRef = eq; }
        _status.Text = $"按「{eq}」推荐最小底宽 {w:0} m（双车道运输 + 设备回转 + 安全距）";
    }

    /// <summary>采装设备 → 经验最小底宽 (m)。规格越大底宽越宽；未知回退 60。</summary>
    public static double RecommendBottomWidth(string equip)
    {
        var e = equip ?? "";
        if (e.Contains("WK-20")) return 80;
        if (e.Contains("EX-3600")) return 70;
        if (e.Contains("WK-10")) return 60;
        return 60;
    }

    private void PickSeam(bool roof)
    {
        if (_seamGrid.SelectedItem is not SeamSurfaceRef sr) { _status.Text = "请先在表中选中一个煤层行"; return; }
        OpenSurfacePick(roof ? "选择煤层顶板面" : "选择煤层底板面", PlanEntityType.TriangleMesh, h =>
        {
            if (roof) sr.RoofHandle = h; else sr.FloorHandle = h;
            var src = _seamGrid.ItemsSource; _seamGrid.ItemsSource = null; _seamGrid.ItemsSource = src;
        });
    }

    /// <summary>打开面/线选择对话框（含视口拾取 + 清单两种模式），选定后回调写回。</summary>
    private void OpenSurfacePick(string title, int wantType, Action<long> onChosen)
    {
        var dlg = new SurfaceSelectionDialog(_host, title, wantType, onChosen);
        dlg.Show(this);
    }

    // ───────────── ⑥ 横剖面布置 ─────────────
    private void OnAutoDetectStrike()
    {
        var m = _host.ActiveBlockModel;
        if (m == null) { _status.Text = "（走向检测）无激活块体模型，请先在「块体模型」导入/选择"; return; }
        (Cad.DepositSignature sig, string attr)? r;
        try { r = BlockModelCoal.DetectAuto(m); }
        catch (Exception ex) { _status.Text = $"（走向检测）失败：{ex.Message}"; return; }
        if (r is not { } rr) { _status.Text = "（走向检测）未找到煤属性或煤单元过少"; return; }
        var sg = rr.sig;
        _strikeAzBox.Text = sg.StrikeAzimuthDeg.ToString("0.#");
        _strikeText.Text = $"{sg.StrikeAzimuthDeg:0.#}°";
        if (Current is { } cur) { cur.StrikeAzimuthDeg = sg.StrikeAzimuthDeg; cur.AutoStrikeDeg = sg.StrikeAzimuthDeg; }
        UpdateSectionRange();
        _status.Text = $"自动检测走向方位 {sg.StrikeAzimuthDeg:0.#}°（块体煤单元 PCA）";
    }

    /// <summary>按矿体走向长推荐剖面间距（≈走向长/30，钳 25–100 m，取 5 的倍数）。</summary>
    private void OnRecommendSpacing()
    {
        if (!BlockModelCoal.TryModelExtent(_host.ActiveBlockModel, out double lx, out double ly)) { _status.Text = "（间距推荐）无激活块体模型"; return; }
        double strikeLen = Math.Max(lx, ly);
        double sp = RecommendSpacing(strikeLen);
        _spacingBox.Text = PlanUi.Num(sp);
        if (Current is { } cur) cur.SectionSpacingM = sp;
        UpdateSectionRange();
        _status.Text = $"按矿体走向长 ≈{strikeLen:0} m 推荐剖面间距 {sp:0} m";
    }
    public static double RecommendSpacing(double strikeLen) => Math.Min(100, Math.Max(25, Math.Round(strikeLen / 30.0 / 5.0) * 5.0));

    /// <summary>在视口预览横剖面线（沿走向按间距布置、垂直走向贯穿足迹；落到独立图层，幂等）。</summary>
    private void OnPreviewSections()
    {
        if (Current is not { } cur) return;
        var top = PitSchemeEnvelope.ResolveTopOutline(_host, cur.Geometry.SurfaceLimitHandle, _host.ActiveBlockModel);
        if (top == null || top.Count < 3) { _status.Text = "（预览剖面线）无地表界且无激活块体，无法取范围"; return; }

        double sp = PlanUi.D(_spacingBox); if (sp < 1) sp = 50;
        double az = PlanUi.D(_strikeAzBox) * Math.PI / 180.0;
        var lines = SectionLines(top, az, sp);

        string layer = "境界_剖面线_" + PitMaterializer.Sanitize(cur.Name);
        try { var old = _host.GetHandlesByLayer(layer); if (old.Length > 0) _host.DeleteEntities(old); } catch { }
        var batch = new PlanEntityBatch { Layer = layer, LayerColor = (245, 158, 11) };
        foreach (var (x0, y0, x1, y1) in lines) batch.Lines.Add((x0, y0, top.Zsurface, x1, y1, top.Zsurface, 245, 158, 11));
        long[] hs;
        try { hs = _host.Import(batch); }
        catch (Exception ex) { _status.Text = $"（预览剖面线）落地失败：{ex.Message}"; return; }
        double strike = StrikeExtent(top, az);
        _sectionRangeText.Text = $"走向 {strike:0} m · 共 {lines.Count} 条（间距 {sp:0} m）";
        _status.Text = hs.Length > 0
            ? $"已在图层「{layer}」预览 {lines.Count} 条横剖面线（走向 {PlanUi.D(_strikeAzBox):0}°·间距 {sp:0} m）"
            : "（预览剖面线）引擎导入失败";
    }

    /// <summary>沿走向按间距布置、垂直走向贯穿顶口足迹的剖面线（纯几何，可单测）。</summary>
    public static List<(double x0, double y0, double x1, double y1)> SectionLines(PitTopOutline top, double azRad, double spacing)
    {
        double sux = Math.Cos(azRad), suy = Math.Sin(azRad);
        double dvx = -suy, dvy = sux;
        double smin = double.MaxValue, smax = double.MinValue, dmin = double.MaxValue, dmax = double.MinValue;
        for (int i = 0; i < top.Count; i++)
        {
            double rx = top.X[i] - top.Cx, ry = top.Y[i] - top.Cy;
            double ps = rx * sux + ry * suy, pd = rx * dvx + ry * dvy;
            smin = Math.Min(smin, ps); smax = Math.Max(smax, ps);
            dmin = Math.Min(dmin, pd); dmax = Math.Max(dmax, pd);
        }
        var res = new List<(double, double, double, double)>();
        for (double t = smin; t <= smax + 1e-6; t += spacing)
        {
            double cxs = top.Cx + t * sux, cys = top.Cy + t * suy;
            res.Add((cxs + dmin * dvx, cys + dmin * dvy, cxs + dmax * dvx, cys + dmax * dvy));
        }
        return res;
    }
    private static double StrikeExtent(PitTopOutline top, double azRad)
    {
        double sux = Math.Cos(azRad), suy = Math.Sin(azRad), smin = double.MaxValue, smax = double.MinValue;
        for (int i = 0; i < top.Count; i++) { double p = (top.X[i] - top.Cx) * sux + (top.Y[i] - top.Cy) * suy; smin = Math.Min(smin, p); smax = Math.Max(smax, p); }
        return smax - smin;
    }

    private void UpdateSectionRange()
    {
        if (!BlockModelCoal.TryModelExtent(_host.ActiveBlockModel, out double lx, out double ly)) { _sectionRangeText.Text = "（需激活块体模型）"; return; }
        double strikeLen = Math.Max(lx, ly);
        double sp = PlanUi.D(_spacingBox); if (sp < 1) sp = 50;
        int n = (int)Math.Floor(strikeLen / sp) + 1;
        _sectionRangeText.Text = $"走向 {strikeLen:0} m · 共 {n} 条（间距 {sp:0} m）";
    }

    // ───────────── 方案管理 / 底部操作 ─────────────
    private void OnNewScheme()
    {
        var s = new PitScheme { Name = $"方案{Schemes.Count + 1}" };
        Schemes.Add(s);
        _schemeList.SelectedItem = s;
    }
    private void OnCloneScheme()
    {
        if (Current == null) return;
        var c = Current.Clone();
        Schemes.Add(c);
        _schemeList.SelectedItem = c;
    }
    private void OnDeleteScheme()
    {
        if (Current != null && Schemes.Count > 1) Schemes.Remove(Current);
    }

    /// <summary>全部自动重填：矿床判型 → 走向 → 帮角(SlopeDesign) → 按方位分帮 → 底宽 → 间距 → n_经。逐项失败不阻断后续。</summary>
    private void OnAutoRefillAll()
    {
        OnAutoDetectDeposit();
        OnAutoDetectStrike();
        OnLoadFromSlopeDesign();
        OnAutoBindWalls();
        OnAutoBottomWidth();
        OnRecommendSpacing();
        OnComputeEconRatio();
        _status.Text = "已全部自动重填：矿床判型 · 走向 · 帮角(SlopeDesign) · 按方位分帮 · 底宽 · 剖面间距 · n_经（各区可微调）";
    }
    private void OnSaveScheme()
    {
        if (Current == null) return;
        SaveToScheme(Current);
        _status.Text = $"已保存方案「{Current.Name}」（n_经={Current.Econ.ComputeEconRatio():0.00} m³/t）";
        var sel = Current; _schemeList.ItemsSource = null; _schemeList.ItemsSource = Schemes; _schemeList.SelectedItem = sel;
    }
}
