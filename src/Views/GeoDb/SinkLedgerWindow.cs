using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 去向台账（移植原 <c>TaskLib.Features.SinkLedgerWindow</c>）：排土场 / 破碎站 / 煤仓 / 堆场的统一台账 ——
/// 现场最关心的两句话「这料能排到哪」「那个场还能排多少」。
///
/// ── 口径（露天矿铁律，全窗口只此一套）──
///  · 库容一律按【排弃占容方 V容 = V实 × Kr】记与扣，不是实方也不是松方；
///  · 破碎站 / 煤仓是**通过型**去向，卸多少走多少、不占库容，占容列一律显示「—」；
///  · 吨量是实方/松方/占容三个口径间唯一的守恒中间量，故入方汇总同时给出吨量。
///
/// ── 可编辑范围（本窗口是台账，不是只读视图）──
///  可改：名称 / 类型 / 状态 / 设计容量 / 通过能力 / 台阶高 / 工作线长 / 当前排弃层 /
///        兜底运距 / 坐标 X·Y·Z / 开放时窗 / 启用期次，另可新增与删除去向。
///  <b>不可改：已填 / 剩余 / 充填率</b> —— 那是实绩逐日累计出来的账，在普通编辑里随手一改，
///  当日入方、内排率、库容预警就全部对不上。确需修正走「盘点修正」：显式改账 + 必填原因，
///  往 <c>sink_stocktake</c> 留一条流水。
///
/// 数据来源与落点：<see cref="SinkRegistryLoader"/>；当日入方：<see cref="SinkInbound"/>。
/// 中文类型名一律走 <c>SinkKind.Label()</c>，本窗口不另建一套映射。
/// </summary>
internal sealed class SinkLedgerWindow : Window
{
    // 充填率阈值：<70% 正常 · 70~90% 警示 · >90% 危险。露天矿现场排产第一眼看的就是这个。
    private const double WarnFillPct = 70;
    private const double DangerFillPct = 90;

    private static readonly IBrush OkBrush = Brush.Parse("#16A34A");
    private static readonly IBrush WarnBrush = Brush.Parse("#D97706");
    private static readonly IBrush DangerBrush = Brush.Parse("#DC2626");
    private static readonly IBrush IdleBrush = Brush.Parse("#94A3B8");

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    /// <summary>编辑副本：改坏了点「刷新」即恢复，未点「保存」不动台账。</summary>
    private SinkRegistry _work = new();
    private List<SinkInboundAcc> _inbound = new();
    private bool _dirty;
    private readonly List<DataGridColumn> _geoCols = new();
    private readonly CheckBox _chkGeo = new() { Content = "显示坐标与时窗列", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    // ── 行视图 ──────────────────────────────────────────────────────────────

    /// <summary>台账一行。可编辑列直接写回 <see cref="Node"/>，保存时整表回灌。</summary>
    internal sealed class SinkRow
    {
        public SinkNode Node { get; }
        private readonly Action _onEdit;

        public SinkRow(SinkNode n, Action onEdit) { Node = n; _onEdit = onEdit; }

        public string Id => Node.Id;
        public string Name
        {
            get => Node.Name;
            set { Node.Name = value ?? ""; _onEdit(); }
        }
        public string KindLabel
        {
            get => Node.Kind.Label();
            set { if (KindOf(value) is { } k) { Node.Kind = k; _onEdit(); } }
        }
        public string StatusLabel
        {
            get => StatusText(Node.Status);
            set { Node.Status = StatusCode(value); _onEdit(); }
        }
        /// <summary>设计容量【万 m³】—— 界面一律用万方，契约里是 m³，只在这一处换算。</summary>
        public double CapWan
        {
            get => Node.DesignCapacityM3 / 1e4;
            set { Node.DesignCapacityM3 = Math.Max(0, value) * 1e4; _onEdit(); }
        }
        public double AcceptTph
        {
            get => Node.AcceptTph;
            set { Node.AcceptTph = Math.Max(0, value); _onEdit(); }
        }
        public double BenchH
        {
            get => Node.BenchHeightM;
            set { Node.BenchHeightM = Math.Max(0, value); _onEdit(); }
        }
        public double WorkLine
        {
            get => Node.WorkLineLengthM;
            set { Node.WorkLineLengthM = Math.Max(0, value); _onEdit(); }
        }
        public int BenchLevel
        {
            get => Node.ActiveBenchLevel;
            set { Node.ActiveBenchLevel = Math.Max(1, value); _onEdit(); }
        }
        public double FallbackKm
        {
            get => Node.FallbackHaulKm;
            set { Node.FallbackHaulKm = Math.Max(0, value); _onEdit(); }
        }
        public double X { get => Node.X; set { Node.X = value; _onEdit(); } }
        public double Y { get => Node.Y; set { Node.Y = value; _onEdit(); } }
        public double Z { get => Node.Z; set { Node.Z = value; _onEdit(); } }
        public double OpenFrom
        {
            get => Node.OpenFromHour;
            set { Node.OpenFromHour = SinkRegistryLoader.Clamp24(value, 0); _onEdit(); }
        }
        public double OpenTo
        {
            get => Node.OpenToHour;
            set { Node.OpenToHour = SinkRegistryLoader.Clamp24(value, 24); _onEdit(); }
        }
        public string Period
        {
            get => Node.OpenFromPeriod ?? "";
            set { Node.OpenFromPeriod = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); _onEdit(); }
        }

        // ── 只读派生（实绩账，编辑不了）──
        /// <summary>已填【万 m³】。通过型去向不占库容 ⇒ 显示「—」。</summary>
        public string FilledWan => Node.IsCapacityLimited ? (Node.FilledM3 / 1e4).ToString("0.##", CultureInfo.InvariantCulture) : "—";
        public string RemainWan => Node.IsCapacityLimited ? (Node.RemainingM3 / 1e4).ToString("0.##", CultureInfo.InvariantCulture) : "不限";
        public string FillPct => Node.IsCapacityLimited ? (Node.FillRate * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
        public string Materials => Node.AcceptedMaterials.Count == 0
            ? "（按物料自身判定）"
            : string.Join("、", Node.AcceptedMaterials.Select(c => MaterialCatalog.Resolve(c).Name));

        public IBrush FillBrush
        {
            get
            {
                if (!Node.IsCapacityLimited) return IdleBrush;
                double p = Node.FillRate * 100;
                return p >= DangerFillPct ? DangerBrush : p >= WarnFillPct ? WarnBrush : OkBrush;
            }
        }
    }

    internal sealed class InboundRow
    {
        public string Sink { get; set; } = "";
        public string Kind { get; set; } = "";
        public string InSitu { get; set; } = "";
        public string Dump { get; set; } = "";
        public string Tonnage { get; set; } = "";
        public string AfterRemaining { get; set; } = "";
        public string Note { get; set; } = "";
        public IBrush Brush { get; set; } = Brushes.Black;
    }

    // ── 控件 ────────────────────────────────────────────────────────────────

    private readonly DataGrid _grid = NewGrid(readOnly: false);
    private readonly DataGrid _inGrid = NewGrid(readOnly: true);
    private readonly ObservableCollection<SinkRow> _rows = new();
    private readonly ObservableCollection<InboundRow> _inRows = new();
    private readonly TextBlock _src = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = WarnBrush };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _totals = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static DataGrid NewGrid(bool readOnly) => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = readOnly,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };

    /// <summary>表头一律包 TextBlock 并关掉裁剪：字符串表头会按首次测量宽度截成「可」「非」。</summary>
    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    public SinkLedgerWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "去向台账";
        Width = 1360; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildSinkColumns();
        BuildInboundColumns();
        _grid.ItemsSource = _rows;
        _inGrid.ItemsSource = _inRows;
        _grid.CellEditEnded += (_, _) => { MarkDirty(); RefreshDerivedOnly(); };

        Content = BuildLayout();
        Reload();
    }

    private void BuildSinkColumns()
    {
        DataGridTextColumn T(string h, string path, double w, bool ro = false) => new()
        {
            Header = Head(h), Width = new DataGridLength(w), IsReadOnly = ro,
            Binding = new Binding(path) { Mode = ro ? BindingMode.OneWay : BindingMode.TwoWay },
        };

        _grid.Columns.Add(T("编号", nameof(SinkRow.Id), 130, ro: true));
        _grid.Columns.Add(T("名称", nameof(SinkRow.Name), 150));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Head("类型"), Width = new DataGridLength(110),
            CellTemplate = TextCell(nameof(SinkRow.KindLabel)),
            CellEditingTemplate = ComboCell(nameof(SinkRow.KindLabel), KindLabels),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Head("状态"), Width = new DataGridLength(90),
            CellTemplate = TextCell(nameof(SinkRow.StatusLabel)),
            CellEditingTemplate = ComboCell(nameof(SinkRow.StatusLabel), StatusLabels),
        });
        _grid.Columns.Add(T("设计容量 万m³", nameof(SinkRow.CapWan), 135));
        // ── 实绩账：只读，改它只能走「盘点修正」──
        _grid.Columns.Add(T("已填 万m³", nameof(SinkRow.FilledWan), 115, ro: true));
        _grid.Columns.Add(T("剩余 万m³", nameof(SinkRow.RemainWan), 115, ro: true));
        // 充填率上色：快满的一眼跳出来 —— 现场排产第一眼看的就是这个。
        // 走模板列而不是 DataGridTextColumn.Foreground：后者是整列一个刷子，逐行判不了。
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Head("充填率"), Width = new DataGridLength(80), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<SinkRow>((_, _) =>
            {
                var tb = new TextBlock { Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
                tb.Bind(TextBlock.TextProperty, new Binding(nameof(SinkRow.FillPct)));
                tb.Bind(TextBlock.ForegroundProperty, new Binding(nameof(SinkRow.FillBrush)));
                return tb;
            }, supportsRecycling: true),
        });
        _grid.Columns.Add(T("通过能力 t/h", nameof(SinkRow.AcceptTph), 120));
        _grid.Columns.Add(T("台阶高 m", nameof(SinkRow.BenchH), 100));
        _grid.Columns.Add(T("工作线长 m", nameof(SinkRow.WorkLine), 115));
        _grid.Columns.Add(T("排弃层", nameof(SinkRow.BenchLevel), 70));
        _grid.Columns.Add(T("兜底运距 km", nameof(SinkRow.FallbackKm), 120));
        // 这六列平时折起来：一屏放不下二十列，而坐标与时窗不是每天要看的
        foreach (var c in new[]
                 {
                     T("X", nameof(SinkRow.X), 95), T("Y", nameof(SinkRow.Y), 95), T("Z", nameof(SinkRow.Z), 80),
                     T("开放起", nameof(SinkRow.OpenFrom), 80), T("开放止", nameof(SinkRow.OpenTo), 80),
                     T("启用期次", nameof(SinkRow.Period), 100),
                 })
        {
            _geoCols.Add(c);
            _grid.Columns.Add(c);
        }
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("可接物料"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true,
            Binding = new Binding(nameof(SinkRow.Materials)),
        });
    }

    private void BuildInboundColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _inGrid.Columns.Add(C("去向", nameof(InboundRow.Sink), 170));
        _inGrid.Columns.Add(C("类型", nameof(InboundRow.Kind), 100));
        _inGrid.Columns.Add(C("计划实方 万m³", nameof(InboundRow.InSitu), 135));
        _inGrid.Columns.Add(C("计划占容 万m³", nameof(InboundRow.Dump), 135));
        _inGrid.Columns.Add(C("吨量 万t", nameof(InboundRow.Tonnage), 100));
        _inGrid.Columns.Add(C("排后剩余 万m³", nameof(InboundRow.AfterRemaining), 140));
        // 排超的行标红 —— 同上，逐行上色只能走模板列
        _inGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Head("说明"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<InboundRow>((_, _) =>
            {
                var tb = new TextBlock { Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
                tb.Bind(TextBlock.TextProperty, new Binding(nameof(InboundRow.Note)));
                tb.Bind(TextBlock.ForegroundProperty, new Binding(nameof(InboundRow.Brush)));
                return tb;
            }, supportsRecycling: true),
        });
    }

    private static IDataTemplate TextCell(string path) => new FuncDataTemplate<SinkRow>((_, _) =>
        new TextBlock { Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center }
            .Bind2(TextBlock.TextProperty, path), supportsRecycling: true);

    private static IDataTemplate ComboCell(string path, string[] items) => new FuncDataTemplate<SinkRow>((_, _) =>
    {
        var cb = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
        cb.Bind(ComboBox.SelectedItemProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        return cb;
    }, supportsRecycling: true);

    private static string[] KindLabels => Enum.GetValues<SinkKind>().Select(k => k.Label()).ToArray();
    private static string[] StatusLabels => new[] { "在用", "已排满", "已关闭" };

    private static SinkKind? KindOf(string? label)
    {
        foreach (var k in Enum.GetValues<SinkKind>())
            if (string.Equals(k.Label(), label, StringComparison.Ordinal)) return k;
        return null;
    }

    private static string StatusText(string? code)
        => string.Equals(code, "full", StringComparison.OrdinalIgnoreCase) ? "已排满"
         : string.Equals(code, "closed", StringComparison.OrdinalIgnoreCase) ? "已关闭"
         : "在用";

    private static string StatusCode(string? label)
        => label == "已排满" ? "full" : label == "已关闭" ? "closed" : "active";

    // ── 布局 ────────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        Button B(string text, Action act, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 3), Margin = new Thickness(0, 0, 8, 0) };
            if (primary) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => act();
            return b;
        }

        var addDump = B("新增排土场", () => Add(SinkKind.ExternalDump));
        var addLup = B("新增卸载点", () => Add(SinkKind.Crusher));
        var del = B("删除", Delete);
        var stock = B("盘点修正…", Stocktake);
        var refresh = B("刷新", () => Reload());
        var save = B("保存", Save, primary: true);

        _chkGeo.IsCheckedChanged += (_, _) => ApplyGeoCols();
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6),
            Children = { addDump, addLup, del, stock, refresh, save, _chkGeo },
        };
        ApplyGeoCols();

        var top = new StackPanel { Children = { bar, _src, _hint } };
        var bottom = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Text = "当日入方（按去向汇总）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
                new Border { Height = 150, Child = _inGrid },
                new Border { Height = 30, Background = Brush.Parse("#F3F3F3"), Margin = new Thickness(0, 6, 0, 0), Child = _totals },
                new Border { Height = 26, Margin = new Thickness(0, 2, 0, 0), Child = _status },
            },
        };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        return root;
    }

    // ── 装载 ────────────────────────────────────────────────────────────────

    internal void Reload()
    {
        var conn = _conn();
        // 编辑副本：登记簿别处也在用，台账上的改动在保存前不许外泄
        _work = SinkRegistryLoader.Load(conn).Clone();
        _dirty = false;

        _src.Text = "数据来源：" + SinkRegistryLoader.LastSourceLabel;
        _inbound = SinkInbound.Today(conn, out string inErr);

        FillSinkGrid();
        FillInboundGrid(inErr);
        FillTotals();
        SetStatus("", null);
    }

    private void FillSinkGrid()
    {
        // 排弃类在前（现场最关心库容），其次通过型；同类按充填率降序 —— 快满的先跳出来
        _rows.Clear();
        foreach (var s in _work.All
                     .OrderByDescending(s => s.IsDumping)
                     .ThenByDescending(s => s.FillRate)
                     .ThenBy(s => s.Name, StringComparer.CurrentCulture))
            _rows.Add(new SinkRow(s, MarkDirty));
    }

    /// <summary>只读派生列（已填/剩余/充填率/可接物料）不是 INotify 的，改完重建一遍行即可。</summary>
    private void RefreshDerivedOnly()
    {
        var keep = _grid.SelectedIndex;
        FillSinkGrid();
        FillTotals();
        if (keep >= 0 && keep < _rows.Count) _grid.SelectedIndex = keep;
    }

    private void FillInboundGrid(string inErr)
    {
        _inRows.Clear();
        int over = 0, noDest = 0;

        foreach (var b in _inbound)
        {
            if (b.SinkId.Length == 0) { noDest++; continue; }

            var node = _work.Find(b.SinkId)
                    ?? _work.All.FirstOrDefault(x => string.Equals(x.Name, b.SinkName, StringComparison.OrdinalIgnoreCase));
            bool dumping = node?.IsDumping ?? b.Kind.IsDumping();
            bool limited = node?.IsCapacityLimited ?? false;

            // 排后剩余 = 当前剩余 − 今日尚未排弃的占容方
            double after = limited ? node!.RemainingM3 - b.PendingDumpM3 : double.PositiveInfinity;
            bool isOver = limited && after < 0;
            if (isOver) over++;

            _inRows.Add(new InboundRow
            {
                Sink = node?.Name ?? b.SinkName,
                Kind = (node?.Kind ?? b.Kind).Label(),
                InSitu = W(b.PlanInSituM3),
                Dump = dumping ? W(b.PlanDumpM3) : "—",
                Tonnage = W(b.PlanTonnageT),
                AfterRemaining = limited ? (after / 1e4).ToString("0.##", CultureInfo.InvariantCulture) : "不限",
                Note = InboundNote(node, b, dumping, limited, after),
                Brush = isOver ? DangerBrush : Brushes.Black,
            });
        }

        var hints = new List<string>();
        if (inErr.Length > 0) hints.Add($"当日入方读不出（{inErr}）—— 台账主表照常显示");
        else if (_inbound.Count == 0) hints.Add("当日入方为空：作业面去向台账（working_face_routing）里还没有采装面的当日目标");
        if (noDest > 0) hints.Add($"{noDest} 条当日目标没有去向，未计入上表 —— 先在「作业面去向」里指定去向");
        if (over > 0) hints.Add($"{over} 个去向按今日计划会排超库容，请调整流向或先盘点核实");
        // 判不了要说出来，别让人把"没数据源"当成"今天一方都没排"
        if (_inbound.Count > 0)
            hints.Add("「已排」无数据源（working_face_routing 只存当日目标），排后剩余按"
                    + "「今日尚未排弃」的保守口径算；今天若已回灌过实绩，那部分会被重复扣一次");

        _hint.Text = hints.Count > 0 ? "◆ " + string.Join("\n◆ ", hints) : "";
        _hint.IsVisible = hints.Count > 0;
    }

    private static string InboundNote(SinkNode? node, SinkInboundAcc b, bool dumping, bool limited, double after)
    {
        if (node == null) return "台账里没有这个去向 —— 请先在上表新增，否则库容校核算不出来";
        if (!node.IsActive) return $"该去向状态为「{StatusText(node.Status)}」，仍被当日计划指为去向";
        if (!dumping) return "通过型去向，不占库容";
        if (!limited) return "未录设计容量 ⇒ 按容量不限处理";
        if (after < 0) return $"排超 {Math.Abs(after) / 1e4:0.##} 万m³";
        double pctAfter = node.DesignCapacityM3 <= 0 ? 0 : (node.FilledM3 + b.PendingDumpM3) / node.DesignCapacityM3 * 100;
        return pctAfter >= DangerFillPct ? $"排后充填率 {pctAfter:0.#}%，接近排满" : "";
    }

    private void FillTotals()
    {
        var dumping = _work.All.Where(s => s.IsDumping && s.IsCapacityLimited).ToList();
        double cap = dumping.Sum(s => s.DesignCapacityM3);
        double remain = dumping.Sum(s => s.RemainingM3);
        double planDump = _inbound.Sum(b => b.PlanDumpM3);
        double planT = _inbound.Sum(b => b.PlanTonnageT);
        int active = _work.Active.Count();

        _totals.Text = $"去向 {_work.All.Count} 个（在用 {active}）　|　"
                     + $"排弃类总库容 {cap / 1e4:N0} 万m³ · 余 {remain / 1e4:N0} 万m³"
                     + (cap > 1e-6 ? $"（已填 {(cap - remain) / cap * 100:0.#}%）" : "")
                     + $"　|　当日计划占容 {planDump / 1e4:0.##} 万m³ · 吨量 {planT / 1e4:0.##} 万t";
    }

    private static string W(double m3) => (m3 / 1e4).ToString("0.####", CultureInfo.InvariantCulture);

    // ── 操作 ────────────────────────────────────────────────────────────────

    private void MarkDirty()
    {
        _dirty = true;
        SetStatus("有未保存的改动 —— 点「保存」写回台账", WarnBrush);
    }

    private void SetStatus(string text, IBrush? brush)
    {
        _status.Text = text;
        _status.Foreground = brush ?? Brushes.Black;
        if (text.Length > 0) _echo(text);
    }

    private void Add(SinkKind kind)
    {
        var n = SinkRegistryLoader.CreateNew(kind);
        _work.Put(n);
        FillSinkGrid();
        FillTotals();
        _grid.SelectedIndex = _rows.ToList().FindIndex(r => r.Id == n.Id);
        MarkDirty();
        SetStatus($"已新增「{n.Name}」，改好名称与容量后点「保存」写回台账", WarnBrush);
    }

    private SinkRow? Selected => _grid.SelectedItem as SinkRow;

    private async void Delete()
    {
        var row = Selected;
        if (row == null) { SetStatus("请先选中要删除的去向", WarnBrush); return; }

        // 当日有入方的去向不许删 —— 那样删掉，当日运量就没有落点了
        var hit = _inbound.FirstOrDefault(b =>
            string.Equals(b.SinkId, row.Id, StringComparison.OrdinalIgnoreCase) && b.PlanDumpM3 > 1e-6);
        if (hit != null)
        {
            SetStatus($"「{row.Name}」当日还有 {hit.PlanDumpM3 / 1e4:0.##} 万m³ 入方，不能删 —— 请先改掉那些作业面的去向", DangerBrush);
            return;
        }

        if (!await Confirm($"删除去向「{row.Name}」？\n本体行与扩展档案会一并删除，此操作不可撤销。")) return;

        var r = SinkRegistryLoader.Delete(_conn(), row.Node);
        if (r.Ok) { SetStatus($"已删除「{row.Name}」", OkBrush); Reload(); }
        else SetStatus(r.Caption, DangerBrush);
    }

    private async void Stocktake()
    {
        var row = Selected;
        if (row == null) { SetStatus("请先选中要盘点的去向", WarnBrush); return; }
        if (!row.Node.IsDumping) { SetStatus("通过型去向（破碎站/煤仓/堆场）不占排土库容，无需盘点", WarnBrush); return; }

        var dlg = new StocktakeDialog(row.Node);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;

        var r = SinkRegistryLoader.Stocktake(_conn(), row.Node, dlg.NewFilledM3, dlg.Reason);
        if (r.Ok)
        {
            SetStatus($"「{row.Name}」已填改为 {dlg.NewFilledM3 / 1e4:0.##} 万m³，已留盘点流水", OkBrush);
            Reload();
        }
        else SetStatus(r.Aborted ? "未盘点：" + r.AbortReason : r.Caption, DangerBrush);
    }

    /// <summary>折叠/展开坐标与时窗六列。</summary>
    private void ApplyGeoCols()
    {
        bool on = _chkGeo.IsChecked == true;
        foreach (var c in _geoCols) c.IsVisible = on;
    }

    private void Save()
    {
        // 正在编辑的单元格先落值，否则最后改的那一格会存不进去
        _grid.CommitEdit();
        if (!_dirty) { SetStatus("没有改动需要保存", null); return; }
        var r = SinkRegistryLoader.Save(_conn(), _work);
        SetStatus(r.Caption, r.Ok ? OkBrush : DangerBrush);
        if (r.Saved > 0 || r.Ok) Reload();
    }

    private async System.Threading.Tasks.Task<bool> Confirm(string text)
    {
        var dlg = new Window
        {
            Title = "确认", Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
        };
        bool ok = false;
        var yes = new Button { Content = "删除", Padding = new Thickness(16, 4), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "取消", Padding = new Thickness(16, 4) };
        yes.Click += (_, _) => { ok = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
        await dlg.ShowDialog(this);
        return ok;
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal void SetGeoCols(bool on) { _chkGeo.IsChecked = on; }
    internal bool GeoColsVisible => _geoCols.Count > 0 && _geoCols[0].IsVisible;
    internal int InboundRowCount => _inRows.Count;
    internal string SourceText => _src.Text ?? "";
    internal string TotalsText => _totals.Text ?? "";
    internal string HintText => _hint.Text ?? "";
    internal string StatusTextValue => _status.Text ?? "";
}

/// <summary>
/// 盘点修正对话框：这是**唯一**允许改「已填」的入口，故原因必填 —— 无原因不许改账。
/// </summary>
internal sealed class StocktakeDialog : Window
{
    private readonly TextBox _val = new() { Width = 160 };
    private readonly TextBox _why = new() { Width = 300, AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _err = new() { Foreground = Brush.Parse("#DC2626"), TextWrapping = TextWrapping.Wrap };

    internal bool Confirmed { get; private set; }
    internal double NewFilledM3 { get; private set; }
    internal string Reason { get; private set; } = "";

    internal StocktakeDialog(SinkNode s)
    {
        Title = "盘点修正";
        Width = 440; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _val.Text = (s.FilledM3 / 1e4).ToString("0.####", CultureInfo.InvariantCulture);

        var ok = new Button { Content = "改账", Padding = new Thickness(16, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 4) };
        ok.Click += (_, _) =>
        {
            if (!double.TryParse(_val.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double wan) || wan < 0)
            { _err.Text = "盘点后的已填量必须是 ≥0 的数"; return; }
            if (string.IsNullOrWhiteSpace(_why.Text))
            { _err.Text = "必须填写修正原因 —— 无原因不许改账"; return; }
            NewFilledM3 = wan * 1e4;      // 界面万 m³ → 契约 m³
            Reason = _why.Text.Trim();
            Confirmed = true;
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"{s.Caption}　设计容量 {s.DesignCapacityM3 / 1e4:0.##} 万m³", FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "「已填」是实绩逐日累计出来的账，普通编辑改不了。确需修正（实测扫描、历史补录、"
                         + "口径纠偏）在此显式改账，会往盘点流水留一条改前/改后/差额/原因。",
                    TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555"),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = "盘点后已填 万m³", VerticalAlignment = VerticalAlignment.Center }, _val },
                },
                new TextBlock { Text = "修正原因（必填）" },
                _why,
                _err,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };
    }
}

internal static class BindHelp
{
    /// <summary>给模板里的 TextBlock 挂单向绑定（省得每处写三行）。</summary>
    internal static T Bind2<T>(this T c, AvaloniaProperty p, string path) where T : Control
    { c.Bind(p, new Binding(path)); return c; }
}
