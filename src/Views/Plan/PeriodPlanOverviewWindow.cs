using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「本期一览」—— 排完产之后，<b>这个月要采哪些条带、每一条的岩排到哪个位置</b>，一屏看完（移植原 <c>PlanLib.Views.PeriodPlanOverviewWindow</c>）。
/// 三件事摆在一起：<b>平面示意图</b>（源、汇、连线）· <b>要采的条带</b>（按作业面归并）· <b>要排到的位置</b>（本期接收量 / 库容 / 期末剩余）。
/// <para><b>它只读，不改任何东西</b>：数据是调用方当下那张表的快照。台账重排之后要看新的，关掉重开。</para>
/// </summary>
internal sealed class PeriodPlanOverviewWindow : Window
{
    private readonly Canvas _plan = new() { Background = Brushes.Transparent, ClipToBounds = true, MinHeight = 200 };
    private readonly ObservableCollection<FaceRow> _faces = new();
    private readonly ObservableCollection<SlotRow> _slots = new();
    private readonly TextBlock _sum = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19 };
    private readonly TextBlock _legend = new() { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

    private readonly List<MiningUnitLedger.Row> _mine = new();     // 本期的采掘单元（煤 + 岩）
    private readonly Dictionary<string, MiningUnitLedger.Row> _slotOf = new(StringComparer.Ordinal);

    /// <summary>作业面（= 层/台阶 + 带）一行。</summary>
    public sealed class FaceRow
    {
        public string Face { get; init; } = "";
        public string Kind { get; init; } = "";
        public int Panels { get; init; }
        public string PanelSpan { get; init; } = "";
        public double CoalWanT { get; init; }
        public double RockWanM3 { get; init; }
        public double DonePctAvg { get; init; }
        public int Partial { get; init; }
        public string Dests { get; init; } = "";
        public double HaulKm { get; init; }
    }

    /// <summary>一个排土位置一行。</summary>
    public sealed class SlotRow
    {
        public string Code { get; init; } = "";
        public string Dump { get; init; } = "";
        public string Level { get; init; } = "";
        public double InSituWanM3 { get; init; }
        public double DumpWanM3 { get; init; }
        public double CapWanM3 { get; init; }
        public double LeftWanM3 { get; init; }
        public double FillPct { get; init; }
        public int Sources { get; init; }
        public double HaulKm { get; init; }
    }

    public PeriodPlanOverviewWindow(string period, IEnumerable<MiningUnitLedger.Row> allRows, double kr = 1.15)
    {
        Title = $"本期一览 · {period}";
        PlanUi.Place(this, 1280, 800);
        MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        var rows = allRows?.Where(r => r != null).ToList() ?? new List<MiningUnitLedger.Row>();
        var mine = rows.Where(r => r.Kind != LedgerKind.Dump && string.Equals(r.Period, period, StringComparison.Ordinal)).ToList();
        _mine.AddRange(mine);

        // 去向码 → 排土位置行。索引拿【全表】建（本期文件里可能还没存位置），与三维/模拟同一个函数。
        var index = DumpSlotCode.BuildIndex(rows, out _, out var collided);
        foreach (var kv in index) _slotOf[kv.Key] = kv.Value;

        BuildFaces();
        BuildSlots(kr);
        Content = BuildUi(period, kr, collided.Count);
        _plan.SizeChanged += (_, _) => DrawPlan();
        Opened += (_, _) => DrawPlan();
    }

    // ══ 归并 ══════════════════════════════════════════════════════

    private static string FaceKey(MiningUnitLedger.Row r) => $"{r.Seam}-B{r.Band}";

    private void BuildFaces()
    {
        foreach (var g in _mine.GroupBy(FaceKey).OrderByDescending(g => g.Sum(r => r.Kind == LedgerKind.Coal ? (r.CoalT ?? 0) : 0))
                                                .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = g.ToList();
            var panels = list.Select(r => r.Panel).Where(p => p > 0).OrderBy(p => p).ToList();
            var dests = list.SelectMany(r => r.Flows).Select(f => f.Destination).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal).ToList();
            var hauls = list.SelectMany(r => r.Flows).Where(f => f.HaulKm.HasValue && f.InSituM3 > 0).ToList();

            _faces.Add(new FaceRow
            {
                Face = g.Key,
                Kind = list.All(r => r.Kind == LedgerKind.Coal) ? "煤" : list.All(r => r.Kind == LedgerKind.Rock) ? "岩" : "煤+岩",
                Panels = list.Count,
                // 幅号连不连续，一眼看得出来：跳号 = 这条带被啃出了缺口
                PanelSpan = panels.Count == 0 ? ""
                          : panels.Count == panels[^1] - panels[0] + 1 ? $"P{panels[0]}–P{panels[^1]}"
                          : $"P{panels[0]}–P{panels[^1]}（跳号，{panels.Count}/{panels[^1] - panels[0] + 1}）",
                CoalWanT = list.Where(r => r.Kind == LedgerKind.Coal).Sum(r => r.CoalT ?? 0) / 1e4,
                RockWanM3 = list.Where(r => r.Kind == LedgerKind.Rock).Sum(r => r.NetRockM3 ?? r.GrossM3 ?? 0) / 1e4,
                DonePctAvg = list.Count == 0 ? 0 : Math.Round(list.Average(r => r.Done) * 100, 1),
                Partial = list.Count(r => r.Done > 1e-9 && r.Done < 1 - 1e-9),
                Dests = dests.Count == 0 ? "（无）" : dests.Count == 1 ? dests[0] : $"{dests.Count} 个位置",
                // 按方量加权 —— 目的是"这个面平均拉多远"，不是算运输功
                HaulKm = hauls.Sum(f => f.InSituM3) > 1e-9 ? Math.Round(hauls.Sum(f => f.InSituM3 * (f.HaulKm ?? 0)) / hauls.Sum(f => f.InSituM3), 2) : 0,
            });
        }
    }

    private void BuildSlots(double kr)
    {
        var byCode = new Dictionary<string, (double InSitu, int Src, double HaulSum)>(StringComparer.Ordinal);
        foreach (var u in _mine)
            foreach (var f in u.Flows)
            {
                if (string.IsNullOrWhiteSpace(f.Destination) || f.InSituM3 <= 1e-9) continue;
                if (!_slotOf.ContainsKey(f.Destination)) continue;    // 煤的出矿点不在这张表里
                byCode.TryGetValue(f.Destination, out var v);
                byCode[f.Destination] = (v.InSitu + f.InSituM3, v.Src + 1, v.HaulSum + f.InSituM3 * (f.HaulKm ?? 0));
            }

        foreach (var kv in byCode.OrderByDescending(k => k.Value.InSitu))
        {
            var row = _slotOf[kv.Key];
            double cap = row.DumpCapM3 ?? 0;
            double occ = kv.Value.InSitu * kr;
            DumpSlotCode.TryLevelFromSeam(row.Seam, out int lv);
            _slots.Add(new SlotRow
            {
                Code = kv.Key, Dump = row.Region, Level = row.Seam.Length > 0 ? row.Seam : $"L{lv}",
                InSituWanM3 = kv.Value.InSitu / 1e4, DumpWanM3 = occ / 1e4, CapWanM3 = cap / 1e4,
                // 库容是占容口径，本期占掉的也折成占容再扣 —— 两侧口径必须一致
                LeftWanM3 = cap > 0 ? Math.Max(0, cap - occ) / 1e4 : 0,
                FillPct = cap > 0 ? Math.Round(Math.Min(1, occ / cap) * 100, 1) : 0,
                Sources = kv.Value.Src,
                HaulKm = kv.Value.InSitu > 1e-9 ? Math.Round(kv.Value.HaulSum / kv.Value.InSitu, 2) : 0,
            });
        }
    }

    // ══ 界面 ══════════════════════════════════════════════════════

    private Control BuildUi(string period, double kr, int collided)
    {
        var banner = PlanUi.Header("本期一览", $"{period} · 要采哪些条带、岩排到哪个位置 —— 只读快照", Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xEA, 0x58, 0x0C));

        var root = new Grid { Margin = new Thickness(12), ColumnDefinitions = new ColumnDefinitions("1.15*,*"), RowDefinitions = new RowDefinitions("Auto,*") };

        // ── 汇总条（跨两列）──
        _sum.Text = Summary(period, kr, collided);
        var sumCard = Card("本期合计", _sum, emphasize: true);
        Grid.SetRow(sumCard, 0); Grid.SetColumnSpan(sumCard, 2); root.Children.Add(sumCard);

        // ── 左：平面示意图 ──
        var planBox = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(_plan, 0); planBox.Children.Add(_plan);
        _legend.Text = "■ 煤单元　■ 岩单元　● 排土位置　细线 = 一笔流（越粗量越大）　—— 坐标按台账质心等比投影，只看相对位置与去向，不当图纸用";
        RoadUi.Theme(_legend, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        Grid.SetRow(_legend, 1); planBox.Children.Add(_legend);
        var planCard = Card("平面示意 —— 源 → 汇", planBox);
        Grid.SetRow(planCard, 1); Grid.SetColumn(planCard, 0); root.Children.Add(planCard);

        // ── 右：两张表 ──
        var right = new Grid { RowDefinitions = new RowDefinitions("*,*") };
        var gFace = MakeGrid(_faces,
            ("作业面", nameof(FaceRow.Face), 96, null), ("类型", nameof(FaceRow.Kind), 52, null), ("幅数", nameof(FaceRow.Panels), 44, "{0:0}"),
            ("幅号", nameof(FaceRow.PanelSpan), 158, null), ("煤万t", nameof(FaceRow.CoalWanT), 62, "{0:0.00}"), ("岩万m³", nameof(FaceRow.RockWanM3), 66, "{0:0.0}"),
            ("完成%", nameof(FaceRow.DonePctAvg), 56, "{0:0.#}"), ("锋面幅", nameof(FaceRow.Partial), 52, "{0:0}"), ("去向", nameof(FaceRow.Dests), 150, null), ("均运距km", nameof(FaceRow.HaulKm), 72, "{0:0.00}"));
        var faceCard = Card($"要采的条带（{_faces.Count} 个作业面 · {_mine.Count} 个单元）", gFace);
        Grid.SetRow(faceCard, 0); right.Children.Add(faceCard);

        var gSlot = MakeGrid(_slots,
            ("排土位置", nameof(SlotRow.Code), 156, null), ("排土场", nameof(SlotRow.Dump), 80, null), ("级", nameof(SlotRow.Level), 48, null),
            ("本期实方万m³", nameof(SlotRow.InSituWanM3), 90, "{0:0.00}"), ("占容万m³", nameof(SlotRow.DumpWanM3), 78, "{0:0.00}"), ("库容万m³", nameof(SlotRow.CapWanM3), 78, "{0:0.00}"),
            ("期末剩余", nameof(SlotRow.LeftWanM3), 72, "{0:0.00}"), ("充填%", nameof(SlotRow.FillPct), 56, "{0:0.#}"), ("来源笔数", nameof(SlotRow.Sources), 64, "{0:0}"), ("均运距km", nameof(SlotRow.HaulKm), 72, "{0:0.00}"));
        var slotCard = Card($"要排到的位置（{_slots.Count} 个）", gSlot);
        Grid.SetRow(slotCard, 1); right.Children.Add(slotCard);

        Grid.SetRow(right, 1); Grid.SetColumn(right, 1); root.Children.Add(right);

        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(banner, 0); Grid.SetRow(root, 1); shell.Children.Add(banner); shell.Children.Add(root);
        return shell;
    }

    private string Summary(string period, double kr, int collided)
    {
        double coalT = _mine.Where(r => r.Kind == LedgerKind.Coal).Sum(r => r.CoalT ?? 0);
        double rockM3 = _mine.Where(r => r.Kind == LedgerKind.Rock).Sum(r => r.NetRockM3 ?? r.GrossM3 ?? 0);
        var flows = _mine.SelectMany(r => r.Flows).Where(f => f.InSituM3 > 1e-9).ToList();
        double flowM3 = flows.Sum(f => f.InSituM3);
        double rockFlow = _mine.Where(r => r.Kind == LedgerKind.Rock).SelectMany(r => r.Flows).Sum(f => f.InSituM3);
        double work = flows.Sum(f => f.InSituM3 * (f.HaulKm ?? 0));      // 方·km，只用来给均运距
        int noDest = _mine.Count(r => r.Flows.Count == 0);
        int partial = _mine.Count(r => r.Done > 1e-9 && r.Done < 1 - 1e-9);

        var sb = new System.Text.StringBuilder();
        sb.Append($"煤 {coalT / 1e4:0.00} 万t　·　岩 {rockM3 / 1e4:0.0} 万m³实方　·　排弃占容 ≈ {rockFlow * kr / 1e4:0.0} 万m³（×Kr {kr:0.00}）");
        if (coalT > 1e-9) sb.Append($"　·　剥采比 {rockM3 / coalT:0.00} m³/t");
        sb.Append($"\n{_mine.Count} 个单元 / {_faces.Count} 个作业面　·　锋面幅（本期只采一部分）{partial} 个　·　排到 {_slots.Count} 个位置　·　平均运距 {(flowM3 > 1e-9 ? work / flowM3 : 0):0.00} km");
        if (noDest > 0) sb.Append($"\n◆ 有 {noDest} 个单元【一笔流都没有】（没排到去向）—— 它们在三维与模拟里没有落点，量也进不了运输功。");
        if (collided > 0) sb.Append($"\n◆ 有 {collided} 个排土位置因去向码相撞被整条剔除，指向它们的流解不出来。");
        if (_mine.Count == 0) sb.Append("\n◆ 这一期一个单元都没有 —— 先在「采掘单元清单」按目标排产，或把期次选对。");
        return sb.ToString();
    }

    // ══ 平面图 ════════════════════════════════════════════════════

    /// <summary>画源、汇、连线。<b>等比投影</b>（不拉伸）。</summary>
    private void DrawPlan()
    {
        _plan.Children.Clear();
        double w = _plan.Bounds.Width, h = _plan.Bounds.Height;
        if (w < 40 || h < 40) return;

        var pts = new List<(double X, double Y)>();
        foreach (var u in _mine) if (Ok(u)) pts.Add((u.Cx, u.Cy));
        foreach (var s in _slots) if (_slotOf.TryGetValue(s.Code, out var r) && Ok(r)) pts.Add((r.Cx, r.Cy));
        if (pts.Count == 0)
        {
            var t0 = RoadUi.Hint("没有坐标可画 —— 台账里这一期的单元没有质心（几何列为空）。", 12); t0.Margin = new Thickness(12);
            _plan.Children.Add(t0);
            return;
        }

        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
        const double pad = 18;
        double k = Math.Min((w - 2 * pad) / spanX, (h - 2 * pad) / spanY);   // 等比
        double offX = (w - spanX * k) / 2, offY = (h - spanY * k) / 2;
        Point To(double x, double y) => new(offX + (x - minX) * k, h - (offY + (y - minY) * k));   // y 翻转：北朝上

        // 连线在最下层
        double maxFlow = Math.Max(1, _mine.SelectMany(r => r.Flows).Select(f => f.InSituM3).DefaultIfEmpty(0).Max());
        foreach (var u in _mine)
        {
            if (!Ok(u)) continue;
            foreach (var f in u.Flows)
            {
                if (string.IsNullOrWhiteSpace(f.Destination) || !_slotOf.TryGetValue(f.Destination, out var s) || !Ok(s)) continue;
                var a = To(u.Cx, u.Cy); var b = To(s.Cx, s.Cy);
                _plan.Children.Add(new Line { StartPoint = a, EndPoint = b, Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x94, 0xA3, 0xB8)), StrokeThickness = 0.6 + 2.4 * (f.InSituM3 / maxFlow) });
            }
        }

        foreach (var s in _slots)
        {
            if (!_slotOf.TryGetValue(s.Code, out var r) || !Ok(r)) continue;
            var p = To(r.Cx, r.Cy);
            // 充填率越高越红：绿(空) → 红(满)。三个通道一起插值。
            double t = Math.Min(1, Math.Max(0, s.FillPct / 100.0));
            var dot = new Ellipse
            {
                Width = 9, Height = 9,
                Fill = new SolidColorBrush(Color.FromRgb((byte)(0x15 + t * (0xB4 - 0x15)), (byte)(0x80 + t * (0x25 - 0x80)), (byte)(0x3D + t * (0x25 - 0x3D)))),
            };
            ToolTip.SetTip(dot, $"{s.Code}\n本期 {s.InSituWanM3:0.00}万m³实方 / 占容 {s.DumpWanM3:0.00}万m³\n库容 {s.CapWanM3:0.00}万m³ · 期末剩余 {s.LeftWanM3:0.00}万m³（充填 {s.FillPct:0.#}%）");
            Canvas.SetLeft(dot, p.X - 4.5); Canvas.SetTop(dot, p.Y - 4.5);
            _plan.Children.Add(dot);
        }

        foreach (var u in _mine)
        {
            if (!Ok(u)) continue;
            var p = To(u.Cx, u.Cy);
            bool coal = u.Kind == LedgerKind.Coal;
            bool part = u.Done > 1e-9 && u.Done < 1 - 1e-9;
            var box = new Rectangle
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(coal ? Color.FromRgb(0x1F, 0x29, 0x37) : Color.FromRgb(0xB4, 0x53, 0x09)),
                // 只采了一部分的（锋面幅）描个边，跟整幅区分开
                Stroke = part ? new SolidColorBrush(Color.FromRgb(0x00, 0x86, 0xD1)) : null,
                StrokeThickness = part ? 1.5 : 0,
            };
            ToolTip.SetTip(box, $"{u.UnitId}（{(coal ? "煤" : "岩")}）\n"
                        + (coal ? $"{(u.CoalT ?? 0) / 1e4:0.00}万t" : $"{(u.NetRockM3 ?? 0) / 1e4:0.00}万m³实方") + $" · 完成 {u.Done * 100:0.#}%\n"
                        + (u.Flows.Count == 0 ? "◆ 没有去向" : string.Join("\n", u.Flows.Select(f => $"→ {f.Destination}　{f.InSituM3 / 1e4:0.00}万m³　{(f.HaulKm.HasValue ? f.HaulKm.Value.ToString("0.00") + "km" : "运距未算")}"))));
            Canvas.SetLeft(box, p.X - 4); Canvas.SetTop(box, p.Y - 4);
            _plan.Children.Add(box);
        }

        var scale = RoadUi.Hint($"范围 X[{minX:0}~{maxX:0}] Y[{minY:0}~{maxY:0}]　1px ≈ {1 / Math.Max(1e-9, k):0.#} m", 10.5);
        scale.Margin = new Thickness(4, 2, 0, 0);
        _plan.Children.Add(scale);
    }

    private static bool Ok(MiningUnitLedger.Row r) => Math.Abs(r.Cx) > 1e-6 || Math.Abs(r.Cy) > 1e-6;

    // ══ 小件 ══════════════════════════════════════════════════════

    private static DataGrid MakeGrid<T>(ObservableCollection<T> src, params (string H, string Path, double W, string? Fmt)[] cols)
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12, FrozenColumnCount = 1, RowHeight = 24,
        };
        foreach (var (hh, path, ww, fmt) in cols)
        {
            var b = new Binding(path) { Mode = BindingMode.OneWay }; if (fmt != null) b.StringFormat = fmt;
            g.Columns.Add(new DataGridTextColumn { Header = hh, Width = new DataGridLength(ww), Binding = b, IsReadOnly = true });
        }
        PlanUi.FitHeaders(g);
        g.ItemsSource = src;
        return g;
    }

    /// <summary>标题 + 内容的白卡。内容一律用 Grid 撑满。</summary>
    private static Border Card(string title, Control body, bool emphasize = false)
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var head = new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        RoadUi.Theme(head, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        Grid.SetRow(head, 0); g.Children.Add(head); Grid.SetRow(body, 1); g.Children.Add(body);
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 8, 8), Child = g };
        RoadUi.Theme(b, Border.BackgroundProperty, emphasize ? "Theme.Panel.Background2" : "Theme.Panel.Background");
        RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        return b;
    }

    internal int SelftestFaces => _faces.Count;
    internal int SelftestSlots => _slots.Count;
    internal string SelftestSummary => _sum.Text ?? "";
}
