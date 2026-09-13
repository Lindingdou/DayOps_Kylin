using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.TaskLib.Simulation;
using PitMine3D.Kylin.Views.GeoDb;
using PitMine3D.Kylin.Views.Road;
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「确定开采程序」窗口（短期组第 4 按钮；移植原 <c>ShortTermSequenceWindow</c>）。三段业务，顺序就是现场的顺序：
/// <b>① 设定工作面</b>（份额 / 备采储量 / 面编号）→ <b>② 制定设备类型</b>（穿·采·运·排四工序的型号约束
/// + 配车数）→ <b>③ 物料与去向</b>（月计划物料流的起点）。
///
/// <para><b>这里钉的全是面级【约束】，不是【解】</b>：去向留空就由排产按「运输功最小 + 库容够 +
/// 物料兼容」自己配；设备型号留空就由排产在全矿在册设备里挑。具体哪台机、干哪几天、几个台班，
/// 是「采掘单元清单 → 按目标排产」之后解出来的 —— 在这个窗口里排机号就是<b>第二套指派实现</b>。</para>
///
/// <para>两个下拉都是<b>硬约束</b>：去向按物料过滤（表土只进表土堆场、煤不进排土场），
/// 设备型号只给 <c>equipment_model</c> 字典里该类别的型号 —— 字典外的型号排产时一台都挑不到。</para>
///
/// <para>右侧是<b>设备工艺树</b>（面 → 工序 → 设备）：每一项都能就地改（型号下拉 / 配车数），
/// 免爆面在树上<b>没有</b>穿孔/爆破分支（结构本身就是信息）；图标走 <see cref="EquipIconLibrary"/>（三维那套形态 + 配色）。</para>
/// </summary>
internal sealed class ShortTermSequenceWindow : Window
{
    private readonly ShortTermBase _base;
    private readonly FaceProcessTree _tree = new();
    private bool _ready;

    private readonly DataGrid faceGrid;
    private readonly TreeView procTree = new() { BorderThickness = new Thickness(1, 0, 1, 0) };
    private readonly ComboBox iconSizeBox;
    private readonly TextBlock sinkSourceText = RoadUi.Hint("", 12), equipSourceText = RoadUi.Hint("", 12),
                               treeStatusText = RoadUi.Hint("", 12), statusText = RoadUi.Hint("", 12);

    private const string DefaultStatus =
        "提示：份额决定各面承担的月产 —— 这是本表唯一进算式的列。\n"
      + "⚠ 所有作业面【每个月都同时按份额作业】（露天矿常态），「标注序」只决定逐月表上「主作业面」那一列显示谁，不改变任何量。\n"
      + "⚠ 推进方位由上游「采区划分 / 开采程序确定」定，这里只显示不生效；单元级的真方位在「采掘单元清单」的台账里（由坡顶/坡底线反算）。\n"
      + "⚠ 设备四列钉的是【型号约束】，具体哪台机、干哪几天由「采掘单元清单 → 按目标排产」解；留空 = 不约束。";

    public ShortTermSequenceWindow(ShortTermBase? baseline = null)
    {
        _base = baseline ?? ShortTermSchemeStore.Base;
        Title = "确定开采程序 — 作业面、设备类型、工艺流程";
        PlanUi.Place(this, 1560, 700);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        // ── 标题栏（橙色渐变，短期组配色）
        var header = PlanUi.Header("确定开采程序",
            "设定工作面 → 制定设备类型 → 物料与去向。这里钉的是面级【约束】，具体机号与工日由「采掘单元清单 → 按目标排产」解出来",
            Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xEA, 0x58, 0x0C));

        // ── 工具条（两行）
        var tools = new StackPanel();
        var row1 = new DockPanel();
        var derive = RoadUi.Btn("按本期单元派生作业面…", () => _ = OnDeriveFacesAsync(), 160, bold: true);
        ToolTip.SetTip(derive, "从【采掘单元清单】本期的单元反推作业面骨架：按（物料 × 台阶）分组，\n台阶标高取该组单元顶板均值 —— 归属口径 FA4 就是按「贴近顶板」排序的，两边必须同口径。\n\n⚠ 只派生【标高 / 物料 / 份额 / 备采储量】。\n推进方位、去向、运距、台阶几何一概不派生 —— 猜出来的那几个数看着完全正常，\n却会一路进运输功、进班表、进达成度。派生完要逐个核。\n\n缺省的两个面（主采面·东 60m / 辅采面·南 48m）是样例：真实单元在 1120~1320m，\n差一千多米，归属永远配不上，工序量就恒为 0。");
        var clearDest = RoadUi.Btn("清空去向(交排产自动配)", OnClearDestinations, 160);
        ToolTip.SetTip(clearDest, "清掉面上钉死的去向 → 月度编制时按「运输功最小 + 库容够 + 物料兼容」自动分配");
        foreach (var b in new[] { derive, RoadUi.Btn("＋ 增加作业面", OnAddFace, 110), RoadUi.Btn("－ 删除选中", OnRemoveFace, 90),
                                  RoadUi.Btn("归一份额(=100%)", OnNormalizeShare, 120), RoadUi.Btn("按份额分摊备采储量", OnAllocate, 150), clearDest })
        { DockPanel.SetDock(b, Avalonia.Controls.Dock.Left); row1.Children.Add(b); }
        sinkSourceText.VerticalAlignment = VerticalAlignment.Center; sinkSourceText.TextTrimming = TextTrimming.CharacterEllipsis;
        row1.Children.Add(sinkSourceText);
        tools.Children.Add(row1);

        var row2 = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        var edit = RoadUi.Btn("编辑工艺流程…", () => _ = OnEditProcessAsync(), 112);
        ToolTip.SetTip(edit, "选中一个面，编辑它的穿爆采运排工艺与参数；对话框底部实时算出月穿孔延米/炸药量/爆破次数");
        var check = RoadUi.Btn("校核设备配置", OnCheckEquip, 110);
        ToolTip.SetTip(check, "逐面查：钉的型号在不在字典里、在册可派几台、面编号能不能在 working_face 里查到");
        var clearEq = RoadUi.Btn("清空设备配置", OnClearEquip, 100);
        ToolTip.SetTip(clearEq, "四个工序的型号约束全清空 → 排产时全矿在册设备里挑");
        foreach (var b in new[] { edit, check, clearEq, RoadUi.Btn("重读型号/面台账", OnReloadCatalog, 120) })
        { DockPanel.SetDock(b, Avalonia.Controls.Dock.Left); row2.Children.Add(b); }
        equipSourceText.VerticalAlignment = VerticalAlignment.Center; equipSourceText.TextTrimming = TextTrimming.CharacterEllipsis;
        row2.Children.Add(equipSourceText);
        tools.Children.Add(row2);
        var toolBar = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = tools };
        RoadUi.Theme(toolBar, Border.BackgroundProperty, "Theme.Panel.Background");
        RoadUi.Theme(toolBar, Border.BorderBrushProperty, "Theme.Panel.Border");

        // ── 左：盘子网格（横向比 + 批量改）｜右：设备工艺树（所属关系 + 就地配）
        faceGrid = BuildFaceGrid();
        faceGrid.Margin = new Thickness(12, 12, 0, 12);
        faceGrid.ItemsSource = _base.Faces;

        var right = new DockPanel { Margin = new Thickness(0, 12, 12, 12) };
        var treeHead = new DockPanel();
        var treeTitle = new TextBlock { Text = "设备与工艺（所属关系）", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(treeTitle, Avalonia.Controls.Dock.Left); treeHead.Children.Add(treeTitle);
        var treeTools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        treeTools.Children.Add(new TextBlock { Text = "图标", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Opacity = 0.7 });
        // 图标边长可配（16 认不出电铲/前装机，所以缺省 20）—— 改这里等于改所有用图标的地方，这正是它是个设置而不是各处常量的理由。
        iconSizeBox = PlanUi.Combo(new[] { 16, 20, 24, 32 }.Select(s => s + " px"), 1, 92);
        ToolTip.SetTip(iconSizeBox, "树上设备图标的边长。16 太小分不出电铲/前装机，缺省 20。改了所有用图标的地方一起变");
        iconSizeBox.SelectionChanged += (_, _) => OnIconSizeChanged();
        treeTools.Children.Add(iconSizeBox);
        var expand = RoadUi.Btn("全部展开", OnExpandAll, 72); expand.Margin = new Thickness(8, 0, 0, 0); treeTools.Children.Add(expand);
        var collapse = RoadUi.Btn("全部折叠", OnCollapseAll, 72); collapse.Margin = new Thickness(6, 0, 0, 0); treeTools.Children.Add(collapse);
        treeHead.Children.Add(treeTools);
        var treeHeadB = new Border { Padding = new Thickness(10, 7), BorderThickness = new Thickness(1), Child = treeHead };
        RoadUi.Theme(treeHeadB, Border.BackgroundProperty, "Theme.Panel.Background");
        RoadUi.Theme(treeHeadB, Border.BorderBrushProperty, "Theme.Panel.Border");
        DockPanel.SetDock(treeHeadB, Avalonia.Controls.Dock.Top); right.Children.Add(treeHeadB);
        treeStatusText.TextWrapping = TextWrapping.Wrap;
        var treeFootB = new Border { Padding = new Thickness(10, 7), BorderThickness = new Thickness(1, 0, 1, 1), Child = treeStatusText };
        RoadUi.Theme(treeFootB, Border.BackgroundProperty, "Theme.Panel.Background");
        RoadUi.Theme(treeFootB, Border.BorderBrushProperty, "Theme.Panel.Border");
        DockPanel.SetDock(treeFootB, Avalonia.Controls.Dock.Bottom); right.Children.Add(treeFootB);
        RoadUi.Theme(procTree, TreeView.BorderBrushProperty, "Theme.Panel.Border");
        ScrollViewer.SetHorizontalScrollBarVisibility(procTree, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);   // 不横向滚：节点里的参数摘要/告警才有宽度可换行（横向无限宽时 TextWrapping 永远不生效）
        right.Children.Add(procTree);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,540") };
        body.ColumnDefinitions[0].MinWidth = 520; body.ColumnDefinitions[2].MinWidth = 380;
        Grid.SetColumn(faceGrid, 0); body.Children.Add(faceGrid);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        RoadUi.Theme(splitter, GridSplitter.BackgroundProperty, "Theme.Panel.Border");
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        Grid.SetColumn(right, 2); body.Children.Add(right);

        // ── 页脚
        var foot = new DockPanel();
        var footBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        footBtns.Children.Add(RoadUi.Btn("保存开采程序", OnSave, 110));
        var close = RoadUi.Btn("关闭", OnClose, 70); close.Margin = new Thickness(0); footBtns.Children.Add(close);
        DockPanel.SetDock(footBtns, Avalonia.Controls.Dock.Right); foot.Children.Add(footBtns);
        statusText.Text = DefaultStatus; statusText.TextWrapping = TextWrapping.Wrap; statusText.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(statusText);

        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Grid.SetRow(header, 0); Grid.SetRow(toolBar, 1); Grid.SetRow(body, 2);
        var footB = PlanUi.Footer(foot); Grid.SetRow(footB, 3);
        shell.Children.Add(header); shell.Children.Add(toolBar); shell.Children.Add(body); shell.Children.Add(footB);
        Content = shell;

        RebuildTree();
        RefreshSinkSource();
        RefreshEquipSource();
        UpdateStatus();
        _ready = true;
    }

    // ══ 左表 ═══════════════════════════════════════════════════════════════════

    private DataGrid BuildFaceGrid()
    {
        var dg = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = false, CanUserReorderColumns = false, CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5,
        };
        dg.Columns.Add(Col("标注序", nameof(WorkingFace.Order), 60));
        dg.Columns.Add(Col("作业面名称", nameof(WorkingFace.Name), 170));   // 原版 * 列；Avalonia DataGrid 有 * 列时其余定宽列会被压到最小宽（见 datagrid-star-column-fit），改定宽 + 横向滚动
        // 面编号：关联库表 working_face.face_code。台阶几何和主电铲都在那张表里，这一列是唯一的接点。不建档也能排产，只是几何取不到。
        dg.Columns.Add(Col("面编号(face_code)", nameof(WorkingFace.FaceCode), 118));
        dg.Columns.Add(Col("台阶标高(m)", nameof(WorkingFace.BenchElevationM), 82, "{0:F0}"));
        dg.Columns.Add(Col("产能份额(%)", nameof(WorkingFace.SharePct), 84, "{0:F0}"));
        dg.Columns.Add(Col("备采储量(万t)", nameof(WorkingFace.AvailableReserveWanT), 94, "{0:F0}"));
        // 只读：这一列全仓库【零处读】，改它改不动任何东西。方位的决策在上游「采区划分 / 开采程序确定」。
        dg.Columns.Add(PlanUi.FmtCol("推进方位(°)", nameof(WorkingFace.AdvanceAzimuthDeg), 80, "{0:F0}"));

        // ══ 设备与工艺已移到右侧的【设备工艺树】：网格留下的是【要横向比、要批量改】的那些：份额 / 物料 / 去向 / 运距。
        // 物料：下拉选物料码，参数(密度/Ks/Kr/允许去向)一律来自 PlanMaterialCatalog
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = "物料", Width = new DataGridLength(80), IsReadOnly = true, SortMemberPath = nameof(WorkingFace.MaterialCode),
            CellTemplate = new FuncDataTemplate<WorkingFace>((_, _) =>
            {
                var cb = new ComboBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0, Padding = new Thickness(6, 2) };
                cb.ItemsSource = PlanMaterialCatalog.All;
                cb.DisplayMemberBinding = new Binding(nameof(PlanMaterialSpec.Name));
                cb.SelectedValueBinding = new Binding(nameof(PlanMaterialSpec.Code));
                void Sync() { if (cb.DataContext is WorkingFace f) cb.SelectedValue = f.MaterialCode; }
                cb.DataContextChanged += (_, _) => Sync();
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.DataContext is not WorkingFace f || cb.SelectedValue is not string code || code == f.MaterialCode) return;
                    f.MaterialCode = code;
                    OnFaceCellEdited(f, "物料");
                };
                Sync();
                return cb;
            }),
        });
        dg.Columns.Add(Col("混采构成", nameof(WorkingFace.MaterialMixText), 88));

        // 去向：下拉**按物料过滤**（合规约束在 UI 上就是硬约束）；留空 = 排产时按运输功最小自动分配
        dg.Columns.Add(new DataGridTemplateColumn
        {
            Header = "去向（按物料过滤）", Width = new DataGridLength(196), SortMemberPath = nameof(WorkingFace.DestinationName),
            CellTemplate = new FuncDataTemplate<WorkingFace>((_, _) =>
            {
                var t = new TextBlock { Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                t.Bind(TextBlock.TextProperty, new Binding(nameof(WorkingFace.DestinationCaption)));
                return t;
            }),
            CellEditingTemplate = new FuncDataTemplate<WorkingFace>((f, _) =>
            {
                var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0 };
                cb.ItemsSource = PlanDestinationCatalog.CandidatesFor(f?.MaterialCode);
                cb.DisplayMemberBinding = new Binding(nameof(PlanDestination.PickerText));
                cb.SelectedValueBinding = new Binding(nameof(PlanDestination.Id));
                if (f != null) cb.SelectedValue = f.DestinationId;
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.DataContext is WorkingFace face && cb.SelectedValue is string id && id != face.DestinationId)
                    { face.DestinationId = id; _destChanged = face; }
                };
                return cb;
            }),
        });
        dg.Columns.Add(Col("运距(km)", nameof(WorkingFace.HaulDistanceKm), 72, "{0:F2}"));
        // 工艺：只读展示，双击行或按工具条按钮进对话框编辑。参数有十几个，全铺成列没法看；但工艺是不是配过必须在主表上一眼看得见。
        dg.Columns.Add(PlanUi.FmtCol("工艺流程", nameof(WorkingFace.ProcessCaption), 230));
        dg.Columns.Add(Col("备注", nameof(WorkingFace.Note), 110));
        PlanUi.FitHeaders(dg);

        dg.CellEditEnded += (_, e) =>
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Row.DataContext is not WorkingFace f) return;
            OnFaceCellEdited(f, HeaderText(e.Column));
        };
        dg.DoubleTapped += (_, _) => { if (HeaderText(dg.CurrentColumn) == "工艺流程") _ = OnEditProcessAsync(); };
        return dg;
    }

    private static DataGridTextColumn Col(string header, string path, double width, string? fmt = null)
    {
        var b = new Binding(path) { Mode = BindingMode.TwoWay };
        if (fmt != null) b.StringFormat = fmt;
        return new DataGridTextColumn { Header = header, Binding = b, Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) };
    }

    private static string HeaderText(DataGridColumn? c) => c?.Header switch { string s => s, TextBlock t => t.Text ?? "", _ => "" };

    private WorkingFace? _destChanged;

    /// <summary>网格刷新（WorkingFace 是纯数据类不发通知，原版 Items.Refresh 的等价）。保住选中行。</summary>
    private void RefreshGrid()
    {
        var sel = faceGrid.SelectedItem;
        faceGrid.ItemsSource = null; faceGrid.ItemsSource = _base.Faces;
        if (sel != null && _base.Faces.Contains((WorkingFace)sel)) faceGrid.SelectedItem = sel;
    }

    private void CommitGrid() { try { faceGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { } }

    /// <summary>
    /// 改完物料/去向后回填去向名/类别/运距（WorkingFace 是纯数据类不发通知，故手动刷表）。
    /// 改物料导致原去向不合规时直接清掉 —— 不合规的搭配不允许残留在表里。
    /// </summary>
    private void OnFaceCellEdited(WorkingFace f, string header)
    {
        bool destEdited = header.Contains("去向") && ReferenceEquals(_destChanged, f);
        _destChanged = null;
        // 编辑事务未结束前不能刷表，推到下一个消息循环再处理。
        Dispatcher.UIThread.Post(() =>
        {
            string msg = "";
            if (header.Contains("物料") && PlanDestinationCatalog.DropIncompatibleDestination(f))
                msg = $"「{f.Name}」改为{f.MaterialName}后原去向不合规（{f.MaterialName}只能进 " +
                      $"{string.Join("/", PlanMaterialCatalog.Resolve(f.MaterialCode).AllowedSinks.Select(k => k.Label()))}），已清空，请重选。";

            if (destEdited) { f.HaulDistanceKm = 0; f.EquivHaulKm = 0; }  // 换去向 → 运距按新去向重取
            PlanDestinationCatalog.ApplyTo(f);

            RefreshGrid();
            _tree.RefreshAll(); RenderTree(); UpdateTreeStatus();
            if (msg.Length > 0) statusText.Text = msg; else UpdateStatus();
        }, DispatcherPriority.Background);
    }

    // ══ 设备工艺树 ═════════════════════════════════════════════════════════════

    /// <summary>
    /// 重建设备工艺树。归属与本月量这一轮取不到（它们在「按目标排产」之后才有）—— 传 null，
    /// 树上就显示「◆ 未归属到任何单元」。<b>显示成 0 也比不显示强</b>。
    /// </summary>
    private void RebuildTree()
    {
        _tree.Rebuild(_base.Faces, attribution: null, monthByFace: null, onChanged: OnTreeEdited);
        RenderTree();
        UpdateTreeStatus();
    }

    /// <summary>树上改了型号/配车 → 刷新左边网格与统计（两边看的是同一批 WorkingFace，没有第二份状态）。</summary>
    private void OnTreeEdited()
    {
        RefreshGrid();
        UpdateTreeStatus();
        UpdateStatus();
    }

    /// <summary>把 <see cref="FaceProcessTree.Roots"/> 铺成 TreeViewItem（原 XAML 的两个 DataTemplate）。节点属性经绑定跟着 Raise 走。</summary>
    private void RenderTree()
    {
        procTree.Items.Clear();
        foreach (var root in _tree.Roots)
        {
            var item = new TreeViewItem { Header = FaceHeader(root) };
            item.Bind(TreeViewItem.IsExpandedProperty, new Binding(nameof(FaceNode.IsExpanded)) { Source = root, Mode = BindingMode.TwoWay });
            foreach (var p in root.Children)
                item.Items.Add(new TreeViewItem { Header = ProcessHeader(p), IsExpanded = true });
            procTree.Items.Add(item);
        }
    }

    private static Control FaceHeader(FaceNode n)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 3) };
        var h = new TextBlock { FontWeight = FontWeight.Bold }; h.Bind(TextBlock.TextProperty, new Binding(nameof(FaceNode.Header)) { Source = n }); sp.Children.Add(h);
        var sub = RoadUi.Hint("", 11); sub.TextWrapping = TextWrapping.Wrap; sub.Bind(TextBlock.TextProperty, new Binding(nameof(FaceNode.Sub)) { Source = n }); sp.Children.Add(sub);
        sp.Children.Add(WarnText(n, nameof(FaceNode.Warn), nameof(FaceNode.HasWarn), 0));
        return sp;
    }

    private Control ProcessHeader(ProcessNode n)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 2) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (n.HasEquipment)
        {
            int px = EquipIconLibrary.DefaultSizePx;
            var img = new Image { Width = px, Height = px, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            img.Bind(Image.SourceProperty, new Binding(nameof(ProcessNode.Icon)) { Source = n });
            row.Children.Add(img);
        }
        row.Children.Add(new TextBlock { Text = n.ProcessName, FontWeight = FontWeight.Bold, Width = 42, VerticalAlignment = VerticalAlignment.Center });
        // 型号：爆破没有设备，整块不生成（不是显示一个禁用的空下拉）
        if (n.HasEquipment)
        {
            var cb = new ComboBox { Width = 188, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, MinHeight = 0 };
            cb.ItemsSource = n.ModelOptions;
            cb.DisplayMemberBinding = new Binding(nameof(PlanEquipModel.PickerText));
            cb.SelectedValueBinding = new Binding(nameof(PlanEquipModel.Model));
            cb.Bind(SelectingItemsControlSelectedValue, new Binding(nameof(ProcessNode.Model)) { Source = n, Mode = BindingMode.TwoWay });
            row.Children.Add(cb);
        }
        if (n.HasTruckCount)
        {
            row.Children.Add(new TextBlock { Text = "配车", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0), Opacity = 0.7 });
            var tb = new TextBox { Width = 38, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2) };
            tb.Bind(TextBox.TextProperty, new Binding(nameof(ProcessNode.TrucksPerLoader)) { Source = n, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            ToolTip.SetTip(tb, "0 = 按现场编组规则 dispatch_rule");
            row.Children.Add(tb);
        }
        sp.Children.Add(row);
        var d = RoadUi.Hint("", 11); d.Margin = new Thickness(26, 1, 0, 0); d.TextWrapping = TextWrapping.Wrap;
        d.Bind(TextBlock.TextProperty, new Binding(nameof(ProcessNode.Detail)) { Source = n }); sp.Children.Add(d);
        sp.Children.Add(WarnText(n, nameof(ProcessNode.Warn), nameof(ProcessNode.HasWarn), 26));
        return sp;
    }

    private static readonly AvaloniaProperty SelectingItemsControlSelectedValue = Avalonia.Controls.Primitives.SelectingItemsControl.SelectedValueProperty;

    private static TextBlock WarnText(object src, string textPath, string visPath, double indent)
    {
        var w = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x53, 0x4F)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 1, 0, 0) };
        w.Bind(TextBlock.TextProperty, new Binding(textPath) { Source = src });
        w.Bind(Visual.IsVisibleProperty, new Binding(visPath) { Source = src });
        return w;
    }

    private void UpdateTreeStatus()
    {
        var (cfg, tot, warn) = _tree.Stats();
        int noUnit = _tree.Roots.Count(r => r.AttributedUnits == 0);
        treeStatusText.Text =
            $"{_tree.Roots.Count} 个面 · 配了型号的工序 {cfg}/{tot}"
          + (warn > 0 ? $" · ◆ {warn} 个工序的型号排产时挑不到设备" : "")
          + (noUnit > 0 ? $"\n◆ {noUnit} 个面还没有单元归属 —— 面上钉的型号与工艺这一轮一条都不生效"
                        + "（先在「采掘单元清单 → 按目标排产」跑一次）" : "");
    }

    private void OnIconSizeChanged()
    {
        if (!_ready || iconSizeBox.SelectedItem is not string s) return;
        if (!int.TryParse(s.Split(' ')[0], out int px)) return;
        EquipIconLibrary.Configure(defaultSizePx: px);
        _tree.RefreshAll();          // 缓存已清，重取一遍图标
        RenderTree();
        statusText.Text = $"设备图标边长改为 {px}px（所有用图标的地方一起变）。";
    }

    private void OnExpandAll() { foreach (var r in _tree.Roots) r.IsExpanded = true; }
    private void OnCollapseAll() { foreach (var r in _tree.Roots) r.IsExpanded = false; }

    private void RefreshSinkSource()
    {
        var all = PlanDestinationCatalog.Current;
        sinkSourceText.Text = $"去向台账：{PlanDestinationCatalog.SourceText} · 可选 {all.Count} 个";
    }

    private void RefreshEquipSource()
    {
        int faces = PlanEquipModelCatalog.FaceCodes.Count;
        equipSourceText.Text = PlanEquipModelCatalog.SourceText
                             + (faces > 0 ? $" · working_face 台账 {faces} 个面编号"
                                          : " · ◆ working_face 台账里一个面都没建档（面编号列可手填，但取不到台阶几何）");
    }

    // ══ 工具条命令 ═════════════════════════════════════════════════════════════

    /// <summary>
    /// 「按本期单元派生作业面」—— 从期次台账反推作业面骨架。
    /// <para><b>派生是替换，不是追加</b>：留着旧的样例面，归属时同物料会出现两个候选
    /// （一个标高对得上、一个差一千米），FA5 判歧义就不配 —— 比只有样例面还糟。</para>
    /// </summary>
    private async Task OnDeriveFacesAsync()
    {
        string period = FaceSeedLoader.LatestPeriod(out string pickNote);
        if (period.Length == 0) { statusText.Text = pickNote; return; }

        var seed = FaceSeedLoader.Load(period);
        if (!seed.Ok)
        {
            statusText.Text = seed.Label
                            + (seed.Notes.Count > 0 ? "\n* " + string.Join("\n* ", seed.Notes) : "");
            return;
        }

        var built = FaceAutoBuilder.Build(seed.Units);
        if (!built.Ok) { statusText.Text = built.Headline; return; }

        // 先把要替换掉的说清楚，再问 —— 点头之后才知道换掉了什么就太晚了
        string old = _base.Faces.Count == 0 ? "(现在一个面都没有)"
                   : string.Join("、", _base.Faces.Take(6).Select(f => $"{f.Name}@{f.BenchElevationM:0}m"))
                     + (_base.Faces.Count > 6 ? $" 等 {_base.Faces.Count} 个" : "");

        bool ok = _selftestAutoConfirm || await CoalMsgBox.ConfirmAsync(this, "按本期单元派生作业面",
                $"要用「{period}」的 {seed.Units.Count} 个采场单元派生 {built.Faces.Count} 个作业面，"
                + "\n\n并【替换】现有的面：" + old
                + "\n\n(替换而不是追加：留着旧面的话，归属时同物料会有两个候选 —— "
                + "一个标高对得上、一个差一千米 —— 判歧义就不配，比只有旧面还糟。)"
                + "\n\n⚠ 派生的只有【标高/物料/份额/备采储量】。"
                + "推进方位、去向、运距、台阶几何一概没派生，要逐个核。");
        if (!ok) { statusText.Text = "没有派生(你取消了)。现有的面一个都没动。"; return; }

        _base.Faces.Clear();
        foreach (var df in built.Faces) _base.Faces.Add(df.Face);

        RebuildTree();
        UpdateStatus();
        statusText.Text = (pickNote.Length > 0 ? pickNote + "　" : "") + built.Headline
                        + "\n" + seed.Label
                        + (built.Notes.Count > 0 ? "\n" + string.Join("\n", built.Notes) : "");
    }

    private void OnAddFace()
    {
        int order = _base.Faces.Count > 0 ? _base.Faces.Max(f => f.Order) + 1 : 1;
        _base.Faces.Add(new WorkingFace
        {
            Name = $"作业面{_base.Faces.Count + 1}", Order = order, SharePct = 0,
            BenchElevationM = 0, AdvanceAzimuthDeg = 90,
            MaterialCode = PlanMaterialCatalog.Coal,   // 默认采煤面；改成剥离物料后去向下拉自动换成排土场
        });
        RebuildTree();
        UpdateStatus();
    }

    private void OnRemoveFace()
    {
        if (faceGrid.SelectedItem is WorkingFace f) { _base.Faces.Remove(f); RebuildTree(); UpdateStatus(); }
        else statusText.Text = "请先在表里选中一个作业面再删除";
    }

    private void OnNormalizeShare()
    {
        CommitGrid();
        double sum = _base.Faces.Sum(f => f.SharePct);
        if (sum <= 0) { statusText.Text = "份额和为 0，无法归一"; return; }
        foreach (var f in _base.Faces) f.SharePct = Math.Round(f.SharePct / sum * 100, 0);
        RefreshGrid(); _tree.RefreshAll(); RenderTree();
        UpdateStatus();
    }

    private void OnAllocate()
    {
        CommitGrid();
        double total = _base.Mineable.PreparedReserveWanT;
        double shareSum = _base.Faces.Sum(f => f.SharePct);
        // ★ 备采储量真正的来源是「量驱动采剥接续」那条几何算的链（MinePlanImporter.ApplyPreparedReserve）；「采场/排土场圈定」不产出三量。
        if (shareSum <= 0 || total <= 0)
        {
            statusText.Text = shareSum <= 0
                ? "◆ 份额合计为 0 —— 先在上表填各作业面的产能份额。"
                : "◆ 还没有备采储量（现在是 0）—— 它由**几何算出**，走「量驱动采剥接续」跑一次并「确定入库」即可；"
                  + "「采场/排土场圈定」不产出三量（它只圈边界与类别）。";
            return;
        }
        foreach (var f in _base.Faces) f.AvailableReserveWanT = Math.Round(total * f.SharePct / shareSum, 0);
        RefreshGrid();
        statusText.Text = $"已按份额把备采储量 {total:N0}万t 分摊到 {_base.Faces.Count} 个作业面";
    }

    /// <summary>清空面上钉死的去向 → 排产时按运输功最小自动分配。</summary>
    private void OnClearDestinations()
    {
        CommitGrid();
        foreach (var f in _base.Faces)
        {
            f.DestinationId = ""; f.DestinationName = ""; f.DestinationKindText = "";
            f.HaulDistanceKm = 0; f.EquivHaulKm = 0;
        }
        RefreshGrid(); _tree.RefreshAll(); RenderTree();
        statusText.Text = $"已清空 {_base.Faces.Count} 个面的去向 → 月度计划编制时按「运输功最小 + 库容够 + 物料兼容」自动分配";
    }

    /// <summary>
    /// 逐面校核设备配置。<b>只报「选了但用不了」，不报「没选」</b> —— 留空是合法的（= 不约束）。
    /// </summary>
    private void OnCheckEquip()
    {
        CommitGrid();
        if (_base.Faces.Count == 0) { statusText.Text = "还没有作业面 —— 先「＋ 增加作业面」。"; return; }

        var bad = new List<string>();
        foreach (var f in _base.Faces) bad.AddRange(PlanEquipModelCatalog.Check(f));

        // 面编号单列一类：它不影响排产能不能跑，只影响取不取得到台阶几何，所以是提示不是错误。
        var codes = PlanEquipModelCatalog.FaceCodes;
        var noCode = _base.Faces.Where(f => string.IsNullOrWhiteSpace(f.FaceCode)).Select(f => f.Name).ToList();
        var badCode = _base.Faces
            .Where(f => !string.IsNullOrWhiteSpace(f.FaceCode)
                     && codes.Count > 0
                     && !codes.Contains(f.FaceCode.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(f => $"{f.Name}→{f.FaceCode.Trim()}").ToList();

        int configured = _base.Faces.Count(f => f.EquipConfiguredCount > 0);
        var sb = new System.Text.StringBuilder();
        sb.Append($"{_base.Faces.Count} 个面，{configured} 个配了设备型号（共 {_base.Faces.Sum(f => f.EquipConfiguredCount)}/{_base.Faces.Count * 4} 个工序）");
        if (bad.Count == 0) sb.Append(" · 型号全部可用 ✓");
        else { sb.Append($" · ◆ {bad.Count} 条型号有问题：\n"); sb.Append(string.Join("\n", bad.Take(6))); }
        if (badCode.Count > 0)
            sb.Append($"\n◆ {badCode.Count} 个面编号在 working_face 台账里查不到（{string.Join("、", badCode.Take(4))}）—— 取不到台阶几何");
        else if (noCode.Count > 0)
            sb.Append($"\n· {noCode.Count} 个面还没填面编号（{string.Join("、", noCode.Take(4))}）—— 排产照跑，只是取不到台阶高/采宽/面长与主电铲");
        statusText.Text = sb.ToString();
    }

    /// <summary>
    /// 编辑选中面的工艺流程。台阶高按 face_code 从 <c>working_face</c> 台账取当兜底 ——
    /// <b>取不到就传 0</b>，让对话框明说"穿孔量算不出来"，而不是拿个常见值顶上。
    /// </summary>
    private async Task OnEditProcessAsync()
    {
        CommitGrid();
        if (faceGrid.SelectedItem is not WorkingFace f)
        { statusText.Text = "请先在表里选中一个作业面，再编辑它的工艺流程。"; return; }

        double bench = PlanEquipModelCatalog.BenchHeightOf(f.FaceCode);
        var dlg = new FaceProcessDialog(f, bench);
        _lastProcessDialog = dlg;
        bool ok = await dlg.ShowDialog<bool>(this);
        if (!ok) { statusText.Text = $"「{f.Name}」的工艺流程未改动。"; return; }

        RefreshGrid(); _tree.RefreshAll(); RenderTree(); UpdateTreeStatus();
        var bad = f.Process.CheckComputable(f.Name, f.MaterialCode, bench);
        statusText.Text = $"已更新「{f.Name}」的工艺流程：{f.ProcessCaption}"
                        + (bench > 0 ? $"（台阶高兜底 {bench:0.##}m 来自 working_face）" : "")
                        + (bad.Count > 0 ? "\n◆ " + string.Join("\n◆ ", bad.Take(2)) : "");
    }

    /// <summary>清空四个工序的型号约束 → 排产时全矿在册设备里挑。</summary>
    private void OnClearEquip()
    {
        CommitGrid();
        foreach (var f in _base.Faces)
        {
            f.DrillModel = ""; f.LoaderModel = ""; f.TruckModel = ""; f.DozerModel = "";
            f.TrucksPerLoader = 0;
        }
        RefreshGrid(); _tree.RefreshAll(); RenderTree(); UpdateTreeStatus();
        statusText.Text = $"已清空 {_base.Faces.Count} 个面的设备型号约束 → 排产时在全矿在册设备里挑，配车数回到现场编组规则";
    }

    /// <summary>重读型号字典与工作面台账（在「数据库」模块里改过之后按这个）。</summary>
    private void OnReloadCatalog()
    {
        PlanEquipModelCatalog.Reload();
        RefreshEquipSource();
        RefreshGrid(); _tree.RefreshAll(); RenderTree(); UpdateTreeStatus();
        statusText.Text = "已重读型号字典与工作面台账 —— 下拉里的在册台数已刷新。";
    }

    private void UpdateStatus()
    {
        double sum = _base.Faces.Sum(f => f.SharePct);
        int pinned = _base.Faces.Count(f => f.HasDestination);
        int equipped = _base.Faces.Count(f => f.EquipConfiguredCount > 0);
        statusText.Text = $"共 {_base.Faces.Count} 个作业面 · 份额合计 {sum:0}%（建议归一到 100%） · "
                        + $"已指定去向 {pinned}/{_base.Faces.Count}（留空的由排产按运输功最小自动配） · "
                        + $"已配设备型号 {equipped}/{_base.Faces.Count}（留空的由排产在全矿在册设备里挑）";
    }

    private void OnSave()
    {
        CommitGrid();
        foreach (var f in _base.Faces) PlanDestinationCatalog.ApplyTo(f);
        RefreshGrid();

        // 保存不拦人（约束填一半也是合法状态），但型号选了却用不了必须当场说 —— 存下去之后再报，人已经离开这个窗口了。
        var bad = new List<string>();
        foreach (var f in _base.Faces) bad.AddRange(PlanEquipModelCatalog.Check(f));

        statusText.Text = $"已保存开采程序（{_base.Faces.Count} 个作业面，份额合计 {_base.Faces.Sum(f => f.SharePct):0}%，"
                        + $"已配去向 {_base.Faces.Count(f => f.HasDestination)} 个，"
                        + $"已配设备型号 {_base.Faces.Count(f => f.EquipConfiguredCount > 0)} 个）"
                        + "→ 月度计划编制时按此切面调度并生成物料流；设备型号作为约束喂给「按目标排产」"
                        + (bad.Count > 0 ? $"\n◆ 但有 {bad.Count} 条型号排产时挑不到设备：{string.Join("；", bad.Take(3))}" : "");
    }

    private void OnClose()
    {
        CommitGrid();
        Close();
    }

    // ══ 自检直通 ═══════════════════════════════════════════════════════════════

    private bool _selftestAutoConfirm;
    private FaceProcessDialog? _lastProcessDialog;
    internal string SelftestStatus => statusText.Text ?? "";
    internal string SelftestTreeStatus => treeStatusText.Text ?? "";
    internal int SelftestFaceCount => _base.Faces.Count;
    internal int SelftestTreeRoots => procTree.Items.Count;
    internal void SelftestAddFace() => OnAddFace();
    internal void SelftestCollapseExcept(int idx) { for (int i = 0; i < _tree.Roots.Count; i++) _tree.Roots[i].IsExpanded = i == idx; }
    /// <summary>自检：把第 idx 面改成硬岩并钉一个字典外的穿孔型号（看免爆/穿爆分支与型号告警）。</summary>
    internal void SelftestMakeRock(int idx, string drillModel) { if (idx < 0 || idx >= _base.Faces.Count) return; var f = _base.Faces[idx]; f.MaterialCode = PlanMaterialCatalog.Rock; f.Name = "岩面·北"; f.SharePct = 20; f.BenchElevationM = 1212; f.DrillModel = drillModel; OnFaceCellEdited(f, "物料"); }
    internal void SelftestSelect(int index) { if (index >= 0 && index < _base.Faces.Count) faceGrid.SelectedItem = _base.Faces[index]; }
    internal void SelftestCheckEquip() => OnCheckEquip();
    internal void SelftestNormalize() => OnNormalizeShare();
    internal void SelftestSave() => OnSave();
    internal Task SelftestDeriveAsync() { _selftestAutoConfirm = true; return OnDeriveFacesAsync(); }
    /// <summary>自检：打开选中面的工艺对话框（非阻塞），供截图；随后可 <see cref="SelftestCloseProcessDialog"/>。</summary>
    internal void SelftestOpenProcessDialog() { _ = OnEditProcessAsync(); if (_lastProcessDialog != null) GeoDbWindows.NoteLast(_lastProcessDialog); }
    internal void SelftestCloseProcessDialog(bool ok) { var d = _lastProcessDialog; _lastProcessDialog = null; if (d == null) return; if (ok) d.SelftestOk(); else d.Close(false); }
    internal FaceProcessDialog? SelftestProcessDialog => _lastProcessDialog;
}
