using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using ShortTermScheduler = PitMine3D.Kylin.Cad.Plan.ShortTermScheduler;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「采排配对」窗口（移植原 <c>PlanLib.ShortTerm.DumpPairingWindow</c>）—— 直接回答月计划最要紧的一问：**从哪采剥、排弃到哪、排不排得下**。
///
/// 中左：源—汇流向矩阵（行=作业面·物料，列=去向，格=该 O-D 的原位实方量与运距）——露天矿调度最经典的一张表。
/// 中右：各去向库容条（设计 / 期初已填 / 本月入方占容 / 期末剩余），快满标红。库容一律按
///       【占容方 V容 = V实×Kr】扣，拿实方或松方扣都会把排土场算错。
/// 下：  本月汇总（采出万t / 剥离万m³实方 / 排弃占容 / 剥采比 / 内排率 / 运输功 / 吨量加权平均运距）。
///
/// <para><b>本窗口是「单元链对位结果」的视图，不自己做配对</b>。流的唯一来源是 <see cref="UnitPlanStore"/>
/// （「采掘单元清单 → 按目标排产」放进来的），经 <see cref="UnitFlowBridge"/> 归并成作业面·物料粒度。
/// <b>取不到就明说"先去排产"，绝不自建一份</b>。</para>
///
/// <para>手改仍然给：改格子里的分配量或整行改投去向 → 回写 <see cref="MonthPeriod.Flows"/> → 重算汇总与库容条。
/// 但那是<b>人工覆盖</b>，<b>不回写单元台账</b>，重新取一次对位就被覆盖。</para>
/// </summary>
internal sealed class DumpPairingWindow : Window
{
    private readonly ObservableCollection<ShortTermPlan> _schemes;
    private readonly ObservableCollection<PairingRow> _rows = new();
    private List<PlanDestination> _cols = new();     // 矩阵列（去向；必要时末列为「未分配」伪去向）
    private bool _loading;

    private const double BarPx = 300;                // 库容条总宽（px）
    private static readonly IBrush BrushFilled = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));  // 期初已填
    private static readonly IBrush BrushMonth = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));   // 本月入方
    private static readonly IBrush BrushOver = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));    // 超容
    private static readonly IBrush BrushRemain = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));  // 期末剩余
    private static readonly IBrush BrushOk = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
    private static readonly IBrush BrushWarn = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));
    private static readonly IBrush BrushBad = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly IBrush BrushDenied = new SolidColorBrush(Color.FromArgb(0x22, 0xDC, 0x26, 0x26));

    private readonly ComboBox planBox, monthBox, rowDestBox;
    private readonly DataGrid matrixGrid;
    private readonly ItemsControl capList = new();
    private readonly TextBlock sourceText = RoadUi.Hint("", 12), matrixHint = RoadUi.Hint("下拉只列出该行物料合规可去的去向（表土只进表土堆场、煤不进排土场）。", 12),
                               statusText = RoadUi.Hint("改矩阵里的分配量或整行改投去向，下方汇总与右侧库容条立即重算；库容按占容方逐月累扣，后续月份自动跟随。", 12),
                               warnText = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = BrushBad, IsVisible = false };
    private readonly TextBlock sCoal = Big(), sStrip = Big(), sDump = Big(), sRatio = Big(), sInner = Big(), sWork = Big(), sHaul = Big();
    private static TextBlock Big() => new() { Text = "—", FontWeight = FontWeight.Bold, FontSize = 15 };

    public DumpPairingWindow(ObservableCollection<ShortTermPlan>? schemes = null)
    {
        _schemes = schemes is { Count: > 0 } ? schemes : ShortTermSchemeStore.Schemes;
        Title = "采排配对 — 从哪采剥、排弃到哪";
        PlanUi.Place(this, 1320, 760);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        // ① 标题
        var header = PlanUi.Header("采排配对",
            "源—汇流向矩阵：行=作业面·物料，列=去向，格=该 O-D 的实方量与运距 · 库容按【占容方 V容=V实×Kr】扣，不是按实方",
            Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xEA, 0x58, 0x0C));

        // ② 期次选择 + 数据来源
        var bar = new DockPanel();
        planBox = new ComboBox { Width = 190, Margin = new Thickness(0, 0, 14, 0), MinHeight = 0, DisplayMemberBinding = new Binding(nameof(ShortTermPlan.Name)) };
        monthBox = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 14, 0), MinHeight = 0, DisplayMemberBinding = new Binding(nameof(MonthPeriod.Label)) };
        var take = RoadUi.Btn("⇩ 取单元链的对位结果", OnTakeFromUnitPlan, 170, bold: true);
        ToolTip.SetTip(take, "把「采掘单元清单 → 按目标排产」解出来的对位关系取过来（单元→排土位置，按作业面·物料归并）。\n配对只在那一处解：要换策略请回去改轴重排，本窗口不自己分配");
        foreach (var c in new Control[] { L("方案"), planBox, L("期次(月)"), monthBox, take, RoadUi.Btn("↻ 重读去向台账", OnReloadSinks, 120), RoadUi.Btn("导出本月配对表", () => _ = OnExportAsync(), 120) })
        { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); bar.Children.Add(c); }
        sourceText.VerticalAlignment = VerticalAlignment.Center; sourceText.TextTrimming = TextTrimming.CharacterEllipsis; bar.Children.Add(sourceText);
        var barB = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        RoadUi.Theme(barB, Border.BackgroundProperty, "Theme.Panel.Background"); RoadUi.Theme(barB, Border.BorderBrushProperty, "Theme.Panel.Border");

        // ③ 中：左=源汇流向矩阵，右=去向库容条
        matrixGrid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = false, CanUserReorderColumns = false, CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.All, FontSize = 12.5, ColumnHeaderHeight = 44,
        };
        matrixGrid.ItemsSource = _rows;
        matrixGrid.SelectionChanged += (_, _) => OnRowSelected();
        matrixGrid.CellEditEnded += (_, e) => { if (e.EditAction == DataGridEditAction.Commit) OnMatrixCellEdited(); };
        var rowDock = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        rowDestBox = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 8, 0), MinHeight = 0, DisplayMemberBinding = new Binding(nameof(PlanDestination.PickerText)) };
        foreach (var c in new Control[] { L("选中行整行改投去向："), rowDestBox, RoadUi.Btn("应用", OnApplyRowDest, 70) })
        { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); rowDock.Children.Add(c); }
        matrixHint.VerticalAlignment = VerticalAlignment.Center; matrixHint.TextTrimming = TextTrimming.CharacterEllipsis; rowDock.Children.Add(matrixHint);
        var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(matrixGrid, 0); Grid.SetRow(rowDock, 1); left.Children.Add(matrixGrid); left.Children.Add(rowDock);
        var leftG = PlanUi.Group("源—汇流向矩阵（格内=原位实方 万m³，可直接改；列头括号内为该去向兜底运距）", left, new Thickness(12, 10, 6, 6), 6);

        capList.ItemTemplate = new FuncDataTemplate<CapacityBarVm>((vm, _) => BuildBar(vm));
        var rightG = PlanUi.Group("各去向库容（设计 / 已填 / 本月入方占容 / 期末剩余 · 快满标红）",
            new ScrollViewer { Content = capList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, new Thickness(6, 10, 12, 6), 6);
        var mid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,372") };
        Grid.SetColumn(leftG, 0); Grid.SetColumn(rightG, 1); mid.Children.Add(leftG); mid.Children.Add(rightG);

        // ④ 本月汇总
        var ug = new UniformGrid { Columns = 7 };
        sInner.Foreground = BrushOk;
        foreach (var (cap, v) in new[] { ("采出", sCoal), ("剥离(实方)", sStrip), ("排弃占容(Kr)", sDump), ("剥采比", sRatio), ("内排率", sInner), ("运输功", sWork), ("吨量加权平均运距", sHaul) })
        {
            var sp = new StackPanel();
            sp.Children.Add(RoadUi.Hint(cap, 11)); sp.Children.Add(v);
            ug.Children.Add(sp);
        }
        var sum = new StackPanel(); sum.Children.Add(ug); sum.Children.Add(warnText);
        var sumB = new Border { Margin = new Thickness(12, 0, 12, 6), Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Child = sum };
        RoadUi.Theme(sumB, Border.BackgroundProperty, "Theme.Panel.Background"); RoadUi.Theme(sumB, Border.BorderBrushProperty, "Theme.Panel.Border");

        // ⑤ 状态栏
        var foot = new DockPanel();
        var close = RoadUi.Btn("关闭", Close, 70); close.Margin = new Thickness(0); DockPanel.SetDock(close, Avalonia.Controls.Dock.Right); foot.Children.Add(close);
        statusText.VerticalAlignment = VerticalAlignment.Center; statusText.TextWrapping = TextWrapping.Wrap; foot.Children.Add(statusText);

        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        var footB = PlanUi.Footer(foot);
        Grid.SetRow(header, 0); Grid.SetRow(barB, 1); Grid.SetRow(mid, 2); Grid.SetRow(sumB, 3); Grid.SetRow(footB, 4);
        shell.Children.Add(header); shell.Children.Add(barB); shell.Children.Add(mid); shell.Children.Add(sumB); shell.Children.Add(footB);
        Content = shell;

        _loading = true;
        planBox.ItemsSource = _schemes;
        var confirmed = ShortTermSchemeStore.Confirmed;
        planBox.SelectedItem = confirmed != null && _schemes.Contains(confirmed) ? confirmed : _schemes.FirstOrDefault();
        planBox.SelectionChanged += (_, _) => { if (!_loading) BindPlan(); };
        monthBox.SelectionChanged += (_, _) => { if (!_loading) BuildMatrix(); };
        _loading = false;

        RefreshSourceText();
        BindPlan();
    }

    private static TextBlock L(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    private ShortTermPlan? Plan => planBox?.SelectedItem as ShortTermPlan;
    private MonthPeriod? Month => monthBox?.SelectedItem as MonthPeriod;

    // ───────────────────────── 装载 ─────────────────────────

    private void RefreshSourceText()
    {
        var all = PlanDestinationCatalog.Current;
        sourceText.Text = $"数据来源：{PlanDestinationCatalog.SourceText} · 去向 {all.Count} 个"
                        + $"（排弃类 {all.Count(d => d.IsDumping)} · 通过型 {all.Count(d => !d.IsDumping)}）";
    }

    private void BindPlan()
    {
        var p = Plan;
        if (p == null) { _rows.Clear(); monthBox.ItemsSource = null; SetStatus("没有可用的月度计划方案。"); return; }

        // 没排过产的方案先排一次，否则无期次可配对。
        if (p.Months.Count == 0) ShortTermScheduler.Schedule(p);

        _loading = true;
        monthBox.ItemsSource = p.Months;
        monthBox.SelectedIndex = 0;
        _loading = false;
        BuildMatrix();
    }

    /// <summary>建（或重建）源—汇矩阵：行=作业面·物料，列=去向。</summary>
    private void BuildMatrix()
    {
        _rows.Clear();
        matrixGrid.Columns.Clear();
        var p = Plan; var mp = Month;
        if (p == null || mp == null) { Recompute(); return; }

        // ★ 本月的流【只能来自单元链】（配对只有一处产出）。没排过就明说"先去排产"，**绝不自建**。
        if (mp.Flows.Count == 0) TakeFromUnitPlan(mp, silent: true);

        // 列 = 台账全部去向（矩阵就是分配空间，不只列已用到的）；有未分配量时末列补「未分配」
        _cols = PlanDestinationCatalog.Current.Select(d => d).ToList();
        if (mp.Flows.Any(f => !f.HasDestination))
            _cols.Add(new PlanDestination { Id = "", Name = "（未分配）", Kind = PlanSinkKind.ExternalDump });

        // 行 = 作业面(按接续次序) × 物料(按物料表顺序)。面名允许重名，取先出现的次序，不炸字典。
        var faceOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < p.Faces.Count; i++)
            if (!faceOrder.ContainsKey(p.Faces[i].Name)) faceOrder[p.Faces[i].Name] = p.Faces[i].Order * 1000 + i;
        var matOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < PlanMaterialCatalog.All.Count; i++) matOrder[PlanMaterialCatalog.All[i].Code] = i;

        var groups = mp.Flows
            .GroupBy(f => (f.SourceName, f.MaterialCode))
            .OrderBy(g => faceOrder.TryGetValue(g.Key.SourceName, out int k) ? k : int.MaxValue)
            .ThenBy(g => matOrder.TryGetValue(g.Key.MaterialCode, out int k2) ? k2 : int.MaxValue);

        foreach (var g in groups)
        {
            var face = p.Faces.FirstOrDefault(f => string.Equals(f.Name, g.Key.SourceName, StringComparison.OrdinalIgnoreCase));
            var row = new PairingRow
            {
                FaceName = g.Key.SourceName,
                MaterialCode = g.Key.MaterialCode,
                SourceRefId = g.FirstOrDefault()?.SourceRefId ?? face?.SourceRefId ?? "",
            };
            var spec = PlanMaterialCatalog.Resolve(row.MaterialCode);
            foreach (var d in _cols)
            {
                bool pseudo = d.Id.Length == 0;
                var hit = g.Where(f => string.Equals(f.DestinationId, d.Id, StringComparison.OrdinalIgnoreCase)
                                    && (pseudo ? !f.HasDestination : f.HasDestination)).ToList();
                row.Cells.Add(new PairingCell
                {
                    InSituWanM3 = hit.Sum(f => f.InSituWanM3),
                    HaulKm = hit.Count > 0 ? hit[0].HaulKm : (pseudo ? 0 : Math.Round(d.FallbackHaulKm, 2)),
                    Allowed = pseudo || d.Accepts(spec),
                    IsDumpingDest = !pseudo && d.IsDumping,
                    Tip = pseudo
                        ? "未分配到任何去向（台账里没有可接纳该物料的去向）"
                        : $"{row.FaceName} → {d.Name}（{d.KindText}）\n实际运距 {d.FallbackHaulKm:0.##} km · 等效 {d.EquivHaulKm:0.##} km\n"
                          + (d.Accepts(spec) ? $"{spec.Name}：Kr={spec.ResidualSwellFactor:0.00}，占容 = 实方×Kr"
                                             : $"× {spec.Name} 不能进{d.KindText}"),
                });
            }
            _rows.Add(row);
        }

        BuildColumns();
        Recompute();
    }

    /// <summary>动态建列：作业面 / 物料 / 各去向 / 合计 / 吨量。</summary>
    private void BuildColumns()
    {
        matrixGrid.Columns.Add(new DataGridTextColumn { Header = "作业面", Binding = new Binding(nameof(PairingRow.FaceName)), Width = new DataGridLength(110), IsReadOnly = true });
        matrixGrid.Columns.Add(new DataGridTextColumn { Header = "物料", Binding = new Binding(nameof(PairingRow.MaterialName)), Width = new DataGridLength(66), IsReadOnly = true });

        for (int j = 0; j < _cols.Count; j++)
        {
            var d = _cols[j];
            string head = d.Id.Length == 0 ? "（未分配）" : $"{d.Name}\n{d.KindText} · {d.FallbackHaulKm:0.#}km";
            int jj = j;
            matrixGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = new TextBlock { Text = head, TextWrapping = TextWrapping.Wrap, FontSize = 11 }, Width = new DataGridLength(112),   // 原版 92：Avalonia 表头字距更宽，"内排土场 / 内排土 · 1.6km" 两行要 112 才不截
                CellTemplate = new FuncDataTemplate<PairingRow>((_, _) =>
                {
                    var t = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0) };
                    t.Bind(TextBlock.TextProperty, new Binding($"Cells[{jj}].Text"));
                    t.Bind(TextBlock.BackgroundProperty, new Binding($"Cells[{jj}].CellBrush"));
                    t.Bind(ToolTip.TipProperty, new Binding($"Cells[{jj}].Tip"));
                    var b = new Border { Child = t }; b.Bind(Border.BackgroundProperty, new Binding($"Cells[{jj}].CellBrush"));
                    return b;
                }),
                CellEditingTemplate = new FuncDataTemplate<PairingRow>((_, _) =>
                {
                    var tb = new TextBox { TextAlignment = TextAlignment.Right, MinHeight = 0, Padding = new Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center };
                    tb.Bind(TextBox.TextProperty, new Binding($"Cells[{jj}].Text") { Mode = BindingMode.TwoWay });
                    return tb;
                }),
            });
        }

        matrixGrid.Columns.Add(Right("合计实方\n万m³", nameof(PairingRow.TotalWanM3), 80));
        matrixGrid.Columns.Add(Right("吨量\n万t", nameof(PairingRow.TonnageWanT), 70));
        matrixGrid.Columns.Add(Right("占容方\n万m³", nameof(PairingRow.DumpWanM3), 74));
    }

    private static DataGridTemplateColumn Right(string header, string path, double width) => new()
    {
        Header = new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, Width = new DataGridLength(width), IsReadOnly = true,
        CellTemplate = new FuncDataTemplate<PairingRow>((_, _) =>
        {
            var t = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0) };
            t.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = "{0:N1}" });
            return t;
        }),
    };

    /// <summary>WPF Items.Refresh 的等价：行是纯数据类，改格子之后整表重绑（保住选中行）。</summary>
    private void RefreshMatrix()
    {
        var sel = matrixGrid.SelectedItem;
        matrixGrid.ItemsSource = null; matrixGrid.ItemsSource = _rows;
        if (sel != null) matrixGrid.SelectedItem = sel;
    }

    private void CommitGrid() { try { matrixGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { } }

    // ───────────────────────── 计算 ─────────────────────────

    /// <summary>把矩阵回写成本月物料流（分配量为 0 的格不生成流；不兼容的格直接归零并提示）。</summary>
    private void ApplyMatrixToFlows()
    {
        var mp = Month; if (mp == null) return;
        var list = new List<PlanFlow>();
        int denied = 0;

        foreach (var r in _rows)
        {
            for (int j = 0; j < _cols.Count && j < r.Cells.Count; j++)
            {
                var c = r.Cells[j];
                if (c.InSituWanM3 <= 1e-9) continue;
                if (!c.Allowed) { denied++; c.InSituWanM3 = 0; continue; }   // 合规硬约束：不兼容不许分配

                var d = _cols[j];
                var f = new PlanFlow
                {
                    SourceName = r.FaceName, SourceRefId = r.SourceRefId,
                    MaterialCode = r.MaterialCode, InSituWanM3 = Math.Round(c.InSituWanM3, 2),
                };
                if (d.Id.Length > 0)
                {
                    f.DestinationId = d.Id; f.DestinationName = d.Name; f.DestinationKind = d.Kind;
                    f.HaulKm = c.HaulKm > 1e-6 ? c.HaulKm : Math.Round(d.FallbackHaulKm, 2);
                    f.EquivHaulKm = Math.Round(f.HaulKm * d.Kind.EquivFactor(), 2);
                }
                list.Add(f);
            }
        }
        mp.Flows = list;
        if (denied > 0)
            SetStatus($"已拒绝 {denied} 个不合规分配（该物料不能进该类去向）——合规约束在本表上是硬约束。");
    }

    /// <summary>重算：库容条（含逐月累扣的期初状态）+ 本月汇总 + 警示。</summary>
    private void Recompute()
    {
        var p = Plan; var mp = Month;
        if (p == null || mp == null)
        {
            capList.ItemsSource = null;
            sCoal.Text = sStrip.Text = sDump.Text = sRatio.Text = sInner.Text = sWork.Text = sHaul.Text = "—";
            warnText.IsVisible = false;
            return;
        }

        // ① 期初台账 = 原始台账 + 本月之前各月已排的占容方（"排土场是逐月填起来的"）
        int idx = Math.Max(0, monthBox.SelectedIndex);
        var startLedger = LedgerAtMonthStart(p, idx);

        // ② 本月各去向入方（占容方）
        var monthDump = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var monthInSitu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var monthTon = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in mp.Flows.Where(z => z.HasDestination))
        {
            monthDump[f.DestinationId] = monthDump.GetValueOrDefault(f.DestinationId) + (f.IsDumping ? f.DumpWanM3 : 0);
            monthInSitu[f.DestinationId] = monthInSitu.GetValueOrDefault(f.DestinationId) + f.InSituWanM3;
            monthTon[f.DestinationId] = monthTon.GetValueOrDefault(f.DestinationId) + f.TonnageWanT;
        }

        // ③ 库容条
        var bars = new List<CapacityBarVm>();
        var warns = new List<string>();
        foreach (var d in startLedger.Destinations)
        {
            double filled0 = d.FilledWanM3;
            double inMonth = monthDump.GetValueOrDefault(d.Id);
            bars.Add(CapacityBarVm.Build(d, filled0, inMonth, monthInSitu.GetValueOrDefault(d.Id), monthTon.GetValueOrDefault(d.Id)));

            if (d.IsDumping && d.IsCapacityLimited && filled0 + inMonth > d.DesignCapacityWanM3 + 1e-6)
                warns.Add($"◆{d.Name} 库容不足：期初已填 {filled0:N0} + 本月入方 {inMonth:N0} 超设计容量 {d.DesignCapacityWanM3:N0} 万m³（占容方），" +
                          $"超出 {filled0 + inMonth - d.DesignCapacityWanM3:N0} 万m³。");
            else if (d.IsDumping && d.IsCapacityLimited && (filled0 + inMonth) / d.DesignCapacityWanM3 >= 0.9)
                warns.Add($"{d.Name} 期末填充率 {(filled0 + inMonth) / d.DesignCapacityWanM3 * 100:0.#}%，接近排满，须提前安排扩容/接续排土场。");
        }
        capList.ItemsSource = bars;

        double noDest = mp.Flows.Where(f => !f.HasDestination).Sum(f => f.InSituWanM3);
        if (noDest > 1e-6) warns.Add($"◆本月有 {noDest:N1} 万m³实方未落到任何去向（台账缺可接纳该物料的去向）。");

        // ④ 汇总（全部走 MonthPeriod 的派生属性，口径与排产一致）
        sCoal.Text = $"{mp.CoalWanT:N1} 万t";
        sStrip.Text = $"{mp.StripWanM3:N0} 万m³";
        sDump.Text = $"{mp.DumpedWanM3:N0} 万m³";
        sRatio.Text = $"{mp.Ratio:0.00} m³/t";
        sInner.Text = $"{mp.InternalDumpPct:0.#} %";
        sWork.Text = $"{mp.TransportWorkWanTKm:N0} 万t·km";
        sHaul.Text = $"{mp.WeightedAvgHaulKm:0.##} km";

        mp.Warning = warns.Count > 0 ? string.Join("\n", warns) : "";
        warnText.Text = mp.Warning;
        warnText.IsVisible = warns.Count > 0;

        SyncPlanResult(p);
    }

    /// <summary>本月之前各月已排占容累加出的库容台账（排土场逐月填起来，第 N 月排满就是这么露出来的）。</summary>
    private static PlanDumpLedger LedgerAtMonthStart(ShortTermPlan p, int monthIndex)
    {
        var ledger = PlanDumpLedger.FromCatalog();
        for (int k = 0; k < monthIndex && k < p.Months.Count; k++)
            foreach (var f in p.Months[k].Flows.Where(z => z.IsDumping))
                ledger.AddDump(f.DestinationId, f.DumpWanM3);
        return ledger;
    }

    /// <summary>手改分配后同步方案级的物料/去向指标（其余指标仍由排产负责）。</summary>
    private static void SyncPlanResult(ShortTermPlan p)
    {
        if (p.Result == null) return;
        var flows = p.Months.SelectMany(z => z.Flows).ToList();
        double dumped = flows.Where(f => f.IsDumping).Sum(f => f.DumpWanM3);
        double inner = flows.Where(f => f.IsInternalDump).Sum(f => f.DumpWanM3);
        double work = flows.Sum(f => f.TransportWorkWanTKm);
        double ton = flows.Sum(f => f.TonnageWanT);
        p.Result.TotalDumpedWanM3 = Math.Round(dumped, 0);
        p.Result.InternalDumpPct = dumped > 1e-9 ? Math.Round(inner / dumped * 100, 1) : 0;
        p.Result.TransportWorkWanTKm = Math.Round(work, 0);
        p.Result.WeightedAvgHaulKm = ton > 1e-9 ? Math.Round(work / ton, 2) : 0;
    }

    // ───────────────────────── 交互 ─────────────────────────

    private void OnMatrixCellEdited()
    {
        // 编辑事务未结束前不能刷表，推到下一个消息循环再回写重算。
        Dispatcher.UIThread.Post(() =>
        {
            ApplyMatrixToFlows();
            RefreshMatrix();
            Recompute();
        }, DispatcherPriority.Background);
    }

    private void OnRowSelected()
    {
        if (matrixGrid.SelectedItem is not PairingRow r) { rowDestBox.ItemsSource = null; return; }
        var spec = PlanMaterialCatalog.Resolve(r.MaterialCode);
        rowDestBox.ItemsSource = PlanDestinationCatalog.Current.Where(d => d.Accepts(spec)).ToList();
        rowDestBox.SelectedIndex = 0;
        matrixHint.Text = $"「{r.FaceName}·{r.MaterialName}」可去：{string.Join(" / ", spec.AllowedSinks.Select(k => k.Label()))}";
    }

    /// <summary>整行改投：把该行全部实方量搬到选定去向（合规由下拉过滤保证）。</summary>
    private void OnApplyRowDest()
    {
        CommitGrid();
        if (matrixGrid.SelectedItem is not PairingRow r) { SetStatus("请先在矩阵里选中一行。"); return; }
        if (rowDestBox.SelectedItem is not PlanDestination d) { SetStatus("请先选一个去向。"); return; }

        int j = _cols.FindIndex(x => string.Equals(x.Id, d.Id, StringComparison.OrdinalIgnoreCase));
        if (j < 0) { SetStatus($"「{d.Name}」不在当前矩阵列中，请点「重读去向台账」。"); return; }

        double total = r.TotalWanM3;
        for (int k = 0; k < r.Cells.Count; k++) r.Cells[k].InSituWanM3 = 0;
        r.Cells[j].InSituWanM3 = total;
        r.Cells[j].HaulKm = Math.Round(d.FallbackHaulKm, 2);

        ApplyMatrixToFlows();
        RefreshMatrix();
        Recompute();
        SetStatus($"已把「{r.FaceName}·{r.MaterialName}」{total:N1}万m³实方整行改投「{d.Name}（{d.KindText}）」，运距 {d.FallbackHaulKm:0.##}km。");
    }

    /// <summary>【取单元链的对位结果】—— 本窗口<b>唯一</b>的流来源。要换配对策略请回「采掘单元清单」改轴重排。</summary>
    private void OnTakeFromUnitPlan()
    {
        CommitGrid();
        var mp = Month;
        if (mp == null) { SetStatus("没有可配对的期次。"); return; }
        TakeFromUnitPlan(mp, silent: false);
        BuildMatrix();
    }

    /// <summary>把 <see cref="UnitPlanStore"/> 里那份对位归并成本月物料流。取不到就如实说，不自建。</summary>
    private void TakeFromUnitPlan(MonthPeriod mp, bool silent)
    {
        var res = UnitFlowBridge.ToPlanFlows(UnitPlanStore.Last, PlanDestinationCatalog.Current);
        if (res.Flows.Count == 0)
        {
            // 空就是空 —— 不拿旧流冒充，也不自己拆一份
            if (!silent)
                SetStatus("◆ 取不到单元链的对位结果：" + UnitPlanStore.Caption
                        + "\n　" + string.Join("\n　", res.Notes));
            return;
        }

        // ⚠ 期次对不上要说：单元排产排的是 A 月，这里正看着 B 月，量会张冠李戴。
        string want = mp.Label ?? "";
        string got = UnitPlanStore.Period;
        string cross = got.Length > 0 && !string.Equals(got, want, StringComparison.Ordinal)
            ? $"\n◆ 期次对不上：单元排产排的是 {got}，当前看的是 {want} —— 这份对位不是这个月的。"
            : "";

        mp.Flows = res.Flows;
        mp.Warning = "";
        if (!silent || cross.Length > 0)
            SetStatus($"已取单元链对位：{UnitPlanStore.Caption}\n　{res.Caption}" + cross
                    + (res.Notes.Count > 0 ? "\n　" + string.Join("\n　", res.Notes) : ""));
    }

    private void OnReloadSinks()
    {
        PlanDestinationCatalog.Reload();
        RefreshSourceText();
        BuildMatrix();
        SetStatus($"已重读去向台账：{PlanDestinationCatalog.SourceText}。");
    }

    private async Task OnExportAsync()
    {
        var p = Plan; var mp = Month;
        if (p == null || mp == null) { SetStatus("没有可导出的期次。"); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出采排配对表", SuggestedFileName = $"采排配对_{p.Name}_{mp.Label}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;
        try
        {
            System.IO.File.WriteAllText(file.Path.LocalPath, BuildCsv(p, mp), System.Text.Encoding.UTF8);
            SetStatus($"已导出：{file.Path.LocalPath}");
        }
        catch (Exception ex) { SetStatus($"导出失败：{ex.Message}"); }
    }

    internal string BuildCsv(ShortTermPlan p, MonthPeriod mp)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"采排配对表,{p.Name},{mp.Label}");
        sb.AppendLine($"去向来源,{PlanDestinationCatalog.SourceText}");
        sb.AppendLine();
        sb.AppendLine("=== 源—汇流向矩阵（原位实方 万m³）===");
        sb.AppendLine("作业面,物料," + string.Join(",", _cols.Select(d => d.Name)) + ",合计实方,吨量万t,占容方万m³");
        foreach (var r in _rows)
            sb.AppendLine($"{r.FaceName},{r.MaterialName}," +
                          string.Join(",", r.Cells.Select(c => c.InSituWanM3 <= 1e-9 ? "" : c.InSituWanM3.ToString("0.##"))) +
                          $",{r.TotalWanM3:0.##},{r.TonnageWanT:0.##},{r.DumpWanM3:0.##}");
        sb.AppendLine();
        sb.AppendLine("=== 本月汇总 ===");
        sb.AppendLine($"采出(万t),{mp.CoalWanT:0.#}");
        sb.AppendLine($"剥离(万m³实方),{mp.StripWanM3:0}");
        sb.AppendLine($"排弃占容(万m³),{mp.DumpedWanM3:0}");
        sb.AppendLine($"生产剥采比(m³/t),{mp.Ratio:0.00}");
        sb.AppendLine($"内排率(%),{mp.InternalDumpPct:0.#}");
        sb.AppendLine($"运输功(万t·km),{mp.TransportWorkWanTKm:0}");
        sb.AppendLine($"吨量加权平均运距(km),{mp.WeightedAvgHaulKm:0.##}");
        if (mp.HasWarning)
        {
            sb.AppendLine();
            sb.AppendLine("=== 警示 ===");
            foreach (var w in mp.Warning.Split('\n')) sb.AppendLine(w.Replace(",", "，"));
        }
        return sb.ToString();
    }

    private void SetStatus(string msg) => statusText.Text = msg;

    /// <summary>库容条模板（原 CapacityBarTemplate）：标题 + 百分比 / 三段色条 / 明细。</summary>
    private static Control BuildBar(CapacityBarVm vm)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var pct = new TextBlock { Text = vm.PctText, FontWeight = FontWeight.Bold, Foreground = vm.PctBrush, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(pct, Avalonia.Controls.Dock.Right); head.Children.Add(pct);
        head.Children.Add(new TextBlock { Text = vm.Title, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(head);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 5) };
        bar.Children.Add(new Rectangle { Width = vm.FilledPx, Height = 13, Fill = vm.FilledBrush });
        bar.Children.Add(new Rectangle { Width = vm.MonthPx, Height = 13, Fill = vm.MonthBrush });
        bar.Children.Add(new Rectangle { Width = vm.RemainPx, Height = 13, Fill = vm.RemainBrush });
        sp.Children.Add(bar);
        var detail = RoadUi.Hint(vm.DetailText, 11); detail.TextWrapping = TextWrapping.Wrap; sp.Children.Add(detail);
        var b = new Border { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(9, 7), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Child = sp };
        RoadUi.Theme(b, Border.BackgroundProperty, "Theme.Panel.Background2"); RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        return b;
    }

    // ───────────────────────── 自检直通 ─────────────────────────
    internal int SelftestRowCount => _rows.Count;
    internal int SelftestColCount => _cols.Count;
    internal int SelftestBarCount => capList.ItemsSource is IList<CapacityBarVm> l ? l.Count : 0;
    internal string SelftestStatus => statusText.Text ?? "";
    internal string SelftestSummary => $"采出 {sCoal.Text} · 剥离 {sStrip.Text} · 占容 {sDump.Text} · 剥采比 {sRatio.Text} · 内排 {sInner.Text} · 运输功 {sWork.Text} · 均距 {sHaul.Text}";
    internal void SelftestSelectRow(int i) { if (i >= 0 && i < _rows.Count) matrixGrid.SelectedItem = _rows[i]; }
    internal void SelftestApplyRowDest(int destIdx) { if (rowDestBox.ItemsSource is IList<PlanDestination> l && destIdx < l.Count) { rowDestBox.SelectedIndex = destIdx; OnApplyRowDest(); } }
    internal void SelftestTake() => OnTakeFromUnitPlan();

    // ───────────────────────── 视图模型 ─────────────────────────

    /// <summary>矩阵一行 = 一个作业面的一种物料（一个面常同时出煤和岩，必须拆开才能分别定去向）。</summary>
    public sealed class PairingRow
    {
        public string FaceName { get; set; } = "";
        public string MaterialCode { get; set; } = "";
        public string SourceRefId { get; set; } = "";
        public List<PairingCell> Cells { get; } = new();

        public string MaterialName => PlanMaterialCatalog.NameOf(MaterialCode);
        public double TotalWanM3 => Cells.Sum(c => c.InSituWanM3);
        public double TonnageWanT => PlanMaterialCatalog.Resolve(MaterialCode).ToTonnage(TotalWanM3);
        /// <summary>本行落到排弃类去向的占容方（通过型去向如破碎站/煤仓不占库容，不计入）。</summary>
        public double DumpWanM3 => PlanMaterialCatalog.Resolve(MaterialCode)
                                       .ToDump(Cells.Where(c => c.IsDumpingDest).Sum(c => c.InSituWanM3));
    }

    /// <summary>矩阵一格 = 一条 O-D 的分配量与运距。</summary>
    public sealed class PairingCell
    {
        public double InSituWanM3 { get; set; }
        public double HaulKm { get; set; }
        /// <summary>物料是否允许进该去向（false 时禁止分配，格底淡红）。</summary>
        public bool Allowed { get; set; } = true;
        /// <summary>该列去向是否为排弃类（占排土库容）。</summary>
        public bool IsDumpingDest { get; set; }
        public string Tip { get; set; } = "";

        /// <summary>格内文本：0 显示空白（满屏 0.0 没法看），编辑时按数字解析。</summary>
        public string Text
        {
            get => InSituWanM3 <= 1e-9 ? "" : InSituWanM3.ToString("0.##");
            set => InSituWanM3 = double.TryParse((value ?? "").Trim(), out double v) && v > 0 ? v : 0;
        }

        public IBrush CellBrush => Allowed ? Brushes.Transparent : BrushDenied;
    }

    /// <summary>一根库容条：期初已填 / 本月入方占容 / 期末剩余。</summary>
    public sealed class CapacityBarVm
    {
        public string Title { get; set; } = "";
        public string PctText { get; set; } = "";
        public string DetailText { get; set; } = "";
        public IBrush PctBrush { get; set; } = BrushOk;
        public double FilledPx { get; set; }
        public double MonthPx { get; set; }
        public double RemainPx { get; set; }
        public IBrush FilledBrush { get; set; } = BrushFilled;
        public IBrush MonthBrush { get; set; } = BrushMonth;
        public IBrush RemainBrush { get; set; } = BrushRemain;

        public static CapacityBarVm Build(PlanDestination d, double filled0, double inMonthDump,
                                          double inMonthInSitu, double inMonthTon)
        {
            var vm = new CapacityBarVm { Title = $"{d.Name}（{d.KindText}）" };

            if (!d.IsDumping || !d.IsCapacityLimited)
            {
                // 通过型去向（破碎站/煤仓/堆场）不占排土库容，只看本月接收量。
                vm.PctText = "不限容";
                vm.FilledPx = 0; vm.MonthPx = 0; vm.RemainPx = BarPx;
                vm.PctBrush = BrushOk;
                vm.DetailText = $"通过型去向（不占排土库容）· 本月接收 {inMonthInSitu:N1} 万m³实方 / {inMonthTon:N1} 万t · 运距 {d.FallbackHaulKm:0.##} km";
                return vm;
            }

            double design = d.DesignCapacityWanM3;
            double end = filled0 + inMonthDump;
            double remain = Math.Max(0, design - end);
            double pct = design > 1e-9 ? end / design * 100 : 0;
            double scale = design > 1e-9 ? BarPx / design : 0;

            vm.FilledPx = Math.Max(0, Math.Min(BarPx, filled0 * scale));
            vm.MonthPx = Math.Max(0, Math.Min(BarPx - vm.FilledPx, inMonthDump * scale));
            vm.RemainPx = Math.Max(0, BarPx - vm.FilledPx - vm.MonthPx);
            vm.PctText = pct >= 100 ? $"排满 {pct:0.#}%" : $"{pct:0.#}%";
            vm.PctBrush = pct >= 100 ? BrushBad : (pct >= 85 ? BrushWarn : BrushOk);
            if (pct >= 100) vm.MonthBrush = BrushOver;
            vm.DetailText = $"设计 {design:N0} / 期初已填 {filled0:N0} / 本月入方 +{inMonthDump:N0} / 期末剩余 {remain:N0} 万m³（占容方 Kr 口径）"
                          + $" · 运距 {d.FallbackHaulKm:0.##} km";
            return vm;
        }
    }
}
