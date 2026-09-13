// 忠实移植自原 PitMine3D Modules/TaskLib/Features/SinkLedgerWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：WPF CellStyle 触发器（告警红粗 / 未录坐标琥珀斜体 / 启用门槛琥珀粗 / 新行加粗）→ 行属性绑定 + LoadingRow；
// MessageBox → CoalMsgBox（async）；就地小窗（可接物料多选 / 盘点修正）→ Avalonia Window.ShowDialog<bool>。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;
using SinkRegistryLoader = PitMine3D.Kylin.TaskLib.Engine.SinkRegistryLoader;   // 与 Kylin 旧切片 Data.SinkRegistryLoader 消歧
using EquipmentDataContext = PitMine3D.Kylin.Data.EquipmentDataContext;

/// <summary>
/// 去向台账：排土场 / 破碎站 / 煤仓 / 堆场 的统一台账 —— 现场最关心的两句话
/// 「这料能排到哪」「那个场还能排多少」。
///
/// ── 口径（露天矿铁律，全窗口只此一套）──
///  · 库容一律按【排弃占容方 V容 = V实 × Kr】记与扣，不是实方也不是松方；
///  · 破碎站 / 煤仓是**通过型**去向，卸多少走多少、不占库容，占容列一律显示「—」；
///  · 吨量是实方/松方/占容三个口径间唯一的守恒中间量，故入方汇总同时给出吨量。
///
/// ── 可编辑范围（本窗口是台账，不是只读视图）──
///  可改：名称 / 类型 / 状态 / 设计容量 / 通过能力 / 台阶高 / 工作线长 / 当前排弃层 /
///        兜底运距 / 坐标 X·Y·Z / 开放时窗 / 启用期次 / 可接物料，另可新增与删除去向。
///  **不可改：已填 / 剩余 / 充填率** —— 那是实绩逐日累计出来的账，在普通编辑里随手一改，
///  当日入方、内排率、库容预警就全部对不上。确需修正走「盘点修正」：显式改账 + 必填原因，
///  往 sink_stocktake 留一条流水（改前/改后/差额/原因/人）。
///
/// 数据来源与落点：<see cref="SinkRegistryLoader"/>
/// （读 dump_site + load_unload_point + sink_profile；未接通自动回落样例，写回逐条报结果）。
/// 中文类型名一律走 <c>SinkKind.Label()</c> 扩展方法，本窗口不另建一套映射。
/// </summary>
public sealed class SinkLedgerWindow : Window
{
    // 充填率阈值：<70% 正常 · 70~90% 警示 · >90% 危险。露天矿现场排产第一眼看的就是这个。
    private const double WarnFillPct = 70;
    private const double DangerFillPct = 90;

    private static readonly IBrush OkBrush = TaskUi.Green;
    private static readonly IBrush WarnBrush = TaskUi.Amber;
    private static readonly IBrush DangerBrush = TaskUi.Red;
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    /// <summary>编辑副本：改坏了点「刷新」即恢复，未点「保存」不动台账。</summary>
    private SinkRegistry _work = new();
    private List<SinkRow> _rows = new();
    private List<Inbound> _inbound = new();
    private bool _dirty;

    // ── 行视图 ───────────────────────────────────────────────────────────────

    /// <summary>类型下拉项（中文名唯一出处 = SinkKind.Label()）。</summary>
    public sealed class KindOption
    {
        public SinkKind Kind { get; init; }
        public string Label { get; init; } = "";
    }

    /// <summary>状态下拉项。dump_site.status 上有 CHECK 约束，只能是这三个值。</summary>
    public sealed class StatusOption
    {
        public string Code { get; init; } = "";
        public string Label { get; init; } = "";
    }

    /// <summary>可接物料复选项（选项源 = MaterialCatalog.All）。</summary>
    public sealed class MaterialPick : INotifyPropertyChanged
    {
        private readonly Action _onToggle;
        private bool _checked;

        public MaterialPick(MaterialSpec spec, bool isChecked, Action onToggle)
        {
            Code = spec.Code;
            Name = spec.Name;
            _checked = isChecked;
            _onToggle = onToggle;
        }

        public string Code { get; }
        public string Name { get; }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                _onToggle();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 去向主表一行 = 一个 <see cref="SinkNode"/> 的可编辑视图。
    /// 所有 setter 直接写内存节点，「保存」时整批交给 <see cref="SinkRegistryLoader.Save(SinkRegistry?)"/>。
    /// 派生列（已填/剩余/充填率）只有 getter —— 改不了是刻意的，不是没写完。
    /// </summary>
    public sealed class SinkRow : INotifyPropertyChanged
    {
        private readonly Action _onEdit;

        public SinkRow(SinkNode node, bool isNew, Action onEdit)
        {
            Node = node;
            IsNew = isNew;
            _onEdit = onEdit;
            MaterialPicks = MaterialCatalog.All
                .OrderBy(m => m.Code, StringComparer.Ordinal)
                .Select(m => new MaterialPick(m, node.AcceptedMaterials.Contains(m.Code), OnMaterialToggled))
                .ToList();
        }

        /// <summary>被编辑的内存节点（保存时整批写回台账）。</summary>
        public SinkNode Node { get; }

        /// <summary>还没入库的新行（整行加粗提示"这条还没落库"）。</summary>
        public bool IsNew { get; internal set; }

        public string Id => Node.Id;

        public string Name
        {
            get => Node.Name;
            set { Node.Name = (value ?? "").Trim(); Edited(); }
        }

        public SinkKind Kind
        {
            get => Node.Kind;
            set
            {
                if (Node.Kind == value) return;
                Node.Kind = value;
                Raise(nameof(Kind)); Raise(nameof(KindLabel));
                // 排弃类↔通过型互换会影响"占不占库容"，派生三列跟着变
                RaiseDerived();
                Edited();
            }
        }

        public string KindLabel => Node.Kind.Label();

        public string StatusCode
        {
            get => NormalizeStatusCode(Node.Status);
            set
            {
                string v = NormalizeStatusCode(value);
                if (string.Equals(Node.Status, v, StringComparison.OrdinalIgnoreCase)) return;
                Node.Status = v;
                Raise(nameof(StatusCode)); Raise(nameof(StatusLabel));
                Edited();
            }
        }

        public string StatusLabel => StatusText(Node);

        /// <summary>设计容量【万 m³】。契约里是 m³，这里 ÷1e4 只为界面好读，写回时由装载器再折回。</summary>
        public double CapacityWan
        {
            get => Node.DesignCapacityM3 / 1e4;
            set
            {
                double v = Sane(value, Node.DesignCapacityM3 / 1e4);
                Node.DesignCapacityM3 = Math.Max(0, v) * 1e4;
                Raise(nameof(CapacityWan)); RaiseDerived();
                Edited();
            }
        }

        // ── 派生只读列：实绩累计出来的账，改它得走「盘点修正」 ──
        public string Filled => Node.IsCapacityLimited ? $"{Node.FilledM3 / 1e4:0.##}" : "—";
        public string Remaining => Node.IsCapacityLimited ? $"{Node.RemainingM3 / 1e4:0.##}" : "不限";
        public double FillPct => Node.IsCapacityLimited ? Node.FillRate * 100 : 0;
        public string FillText => Node.IsCapacityLimited ? $"{Node.FillRate * 100:0.#}%" : "通过型";
        public IBrush FillBrush => Node.IsCapacityLimited ? FillColor(Node.FillRate * 100) : IdleBrush;
        public string FillTip => Node.IsCapacityLimited
            ? $"{Node.CapacityCaption}（口径：排弃占容方 V容 = V实×Kr）\n"
              + "已填/剩余/充填率由实绩累计得出，此处不可直接改；确需修正请用工具条的「盘点修正…」。"
            : "通过型去向：卸多少走多少，不占排土库容";

        public double Tph
        {
            get => Node.AcceptTph;
            set { Node.AcceptTph = Math.Max(0, Sane(value, Node.AcceptTph)); Raise(nameof(Tph)); Edited(); }
        }

        public double BenchH
        {
            get => Node.BenchHeightM;
            set { Node.BenchHeightM = Math.Max(0, Sane(value, Node.BenchHeightM)); Raise(nameof(BenchH)); Edited(); }
        }

        public int Level
        {
            get => Node.ActiveBenchLevel;
            set { Node.ActiveBenchLevel = Math.Max(1, value); Raise(nameof(Level)); Edited(); }
        }

        public double LineLen
        {
            get => Node.WorkLineLengthM;
            set { Node.WorkLineLengthM = Math.Max(0, Sane(value, Node.WorkLineLengthM)); Raise(nameof(LineLen)); Edited(); }
        }

        public double FallbackKm
        {
            get => Node.FallbackHaulKm;
            set { Node.FallbackHaulKm = Math.Max(0, Sane(value, Node.FallbackHaulKm)); Raise(nameof(FallbackKm)); Edited(); }
        }

        public double OpenFrom
        {
            get => Node.OpenFromHour;
            set { Node.OpenFromHour = Clamp24(Sane(value, Node.OpenFromHour)); Raise(nameof(OpenFrom)); Edited(); }
        }

        public double OpenTo
        {
            get => Node.OpenToHour;
            set { Node.OpenToHour = Clamp24(Sane(value, Node.OpenToHour)); Raise(nameof(OpenTo)); Edited(); }
        }

        // ── 坐标（V036）────────────────────────────────────────────────────────
        //  排土场落 sink_profile.x/y/z（dump_site 没有坐标列），卸载点落 load_unload_point.x/y/z，
        //  由 SinkRegistryLoader 按去向类型自动选表——本窗口只管改内存节点。

        public double X
        {
            get => Node.X;
            set { Node.X = Sane(value, Node.X); RaiseCoord(nameof(X)); }
        }

        public double Y
        {
            get => Node.Y;
            set { Node.Y = Sane(value, Node.Y); RaiseCoord(nameof(Y)); }
        }

        public double Z
        {
            get => Node.Z;
            set { Node.Z = Sane(value, Node.Z); RaiseCoord(nameof(Z)); }
        }

        /// <summary>
        /// 没录坐标（X、Y 均为 0）。判据与 <c>HaulResolver.HasPosition</c> 一致：
        /// Z 不参与——标高 0 是合法值，拿它判"有没有坐标"会把坑底的点误判成未录。
        /// </summary>
        public bool NoCoord => Math.Abs(Node.X) < 1e-6 && Math.Abs(Node.Y) < 1e-6;

        // 原 CoordCell 样式：未录坐标 ⇒ 琥珀斜体（Avalonia 端由列模板绑这两个属性）
        public IBrush CoordBrush => NoCoord ? WarnBrush : TaskUi.BodyBrush;
        public FontStyle CoordStyle => NoCoord ? FontStyle.Italic : FontStyle.Normal;

        /// <summary>
        /// 无坐标的后果要说清楚：这不是"少填一格"，而是运距直接降级到兜底值。
        /// 三层兜底里，路网层需要「编号能匹配上路网节点」或「有坐标可吸附」，两条都不通就落兜底。
        /// </summary>
        public string CoordTip => NoCoord
            ? $"⚠ 未录坐标（X、Y 均为 0 视为未录，不是原点）。\n\n"
              + $"后果：路网求运距时，先拿编号「{Node.RefEntityId}」/「{Node.Id}」去精确匹配路网节点，"
              + "匹配不上又没有坐标可吸附最近节点 ⇒ 本去向的运距只能用兜底值 "
              + $"{Node.FallbackHaulKm:0.##} km —— 那是按去向类型给的量级缺省，不是这个场的真实运距，"
              + "循环时间、配车数、编组班产会跟着一起偏。\n\n"
              + (Node.IsDumping
                  ? "排土场默认就是这个状态：dump_site 表没有坐标列，坐标只能在本列录入（存 sink_profile）。"
                  : "卸载点的坐标存 load_unload_point，可在本列录入；破碎站也可用「破碎站位置设置」在视口上拾取。")
            : $"坐标 ({Node.X:0.##}, {Node.Y:0.##}, {Node.Z:0.##})　"
              + (Node.IsDumping ? "（存 sink_profile.x/y/z）" : "（存 load_unload_point.x/y/z）")
              + "\n路网可按最近节点吸附求运距；吸附不到仍会退回兜底值。";

        /// <summary>
        /// 启用期次（V035 就有列，V036 这一轮才给了 UI 入口）。空 = 已启用。
        /// 内排土场必须等采空区形成才能接收——这是露天矿的硬约束，不是备注。
        /// </summary>
        public string OpenPeriod
        {
            get => Node.OpenFromPeriod ?? "";
            set
            {
                string v = (value ?? "").Trim();
                Node.OpenFromPeriod = v.Length == 0 ? null : v;
                Raise(nameof(OpenPeriod)); Raise(nameof(HasOpenGate)); Raise(nameof(PeriodTip));
                Raise(nameof(PeriodBrush)); Raise(nameof(PeriodWeight));
                Edited();
            }
        }

        /// <summary>本去向带启用门槛（填了期次）——不是无条件可用，值得在表里标出来。</summary>
        public bool HasOpenGate => !string.IsNullOrWhiteSpace(Node.OpenFromPeriod);

        // 原 PeriodCell 样式：带门槛 ⇒ 琥珀粗体
        public IBrush PeriodBrush => HasOpenGate ? WarnBrush : TaskUi.BodyBrush;
        public FontWeight PeriodWeight => HasOpenGate ? FontWeight.Bold : FontWeight.Normal;

        public string PeriodTip => HasOpenGate
            ? $"本去向须到期次「{Node.OpenFromPeriod}」才启用，在此之前不应往这里排料。\n"
              + "内排土场的典型用法：等采空区形成、底部承载层做好，才轮得到它接收。\n"
              + "清空本格 = 已启用（无期次门槛）。"
            : "留空 = 已启用，无期次门槛。\n\n"
              + "内排土场须等采空区形成后才能启用，此时填期次标签（如 2026-09）：\n"
              + "在到达该期次之前，本去向不应被排料——内排能降运距、能省征地，"
              + "但排早了就是往还没采完的地方回填。";

        /// <summary>改一个坐标分量 ⇒ 该格连同"有没有坐标"的标色与提示一起刷新。</summary>
        private void RaiseCoord(string name)
        {
            Raise(name); Raise(nameof(NoCoord)); Raise(nameof(CoordTip)); Raise(nameof(CoordBrush)); Raise(nameof(CoordStyle));
            Edited();
        }

        public List<MaterialPick> MaterialPicks { get; }

        /// <summary>可接物料摘要：白名单优先；全不选 = 不限定，按物料自身的允许去向类型判定。</summary>
        public string MaterialsCaption
        {
            get
            {
                var picked = MaterialPicks.Where(p => p.Checked).Select(p => p.Name).ToList();
                if (picked.Count > 0) return string.Join(" / ", picked);
                try
                {
                    var implied = MaterialCatalog.All.Where(Node.Accepts).Select(m => m.Name).ToList();
                    return implied.Count == 0 ? "（无可接物料）" : $"不限定（默认可接：{string.Join(" / ", implied)}）";
                }
                catch { return "不限定"; }
            }
        }

        public string MaterialsTip =>
            "勾选 = 本点白名单（只收勾中的物料）；全不勾 = 不限定，按物料自身的允许去向类型判定。\n"
            + "表土只能进表土堆场、煤不进排土场这类合规约束由物料目录保证，勾选只能更严、不能放宽。";

        public List<KindOption> KindOptions { get; } = Enum.GetValues<SinkKind>()
            .Select(k => new KindOption { Kind = k, Label = k.Label() }).ToList();

        public List<StatusOption> StatusOptions { get; } = new()
        {
            new StatusOption { Code = "active", Label = "在用" },
            new StatusOption { Code = "full",   Label = "已排满" },
            new StatusOption { Code = "closed", Label = "已关闭" },
        };

        /// <summary>盘点后刷新派生列（FilledM3 被显式改过）。</summary>
        internal void RefreshDerived() => RaiseDerived();

        private void OnMaterialToggled()
        {
            Node.AcceptedMaterials = new HashSet<string>(
                MaterialPicks.Where(p => p.Checked).Select(p => p.Code), StringComparer.OrdinalIgnoreCase);
            Raise(nameof(MaterialsCaption));
            Edited();
        }

        private void RaiseDerived()
        {
            Raise(nameof(Filled)); Raise(nameof(Remaining)); Raise(nameof(FillPct));
            Raise(nameof(FillText)); Raise(nameof(FillBrush)); Raise(nameof(FillTip));
            Raise(nameof(MaterialsCaption));
        }

        private void Edited() => _onEdit();

        /// <summary>非数/无穷一律退回原值：绑定层能挡住"abc"，挡不住 1e999。</summary>
        private static double Sane(double v, double fallback)
            => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;

        private static double Clamp24(double v) => v < 0 ? 0 : v > 24 ? 24 : v;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>本日入方一行（库容预警：<see cref="Over"/> 为 true ⇒ 整行标红）。</summary>
    public sealed class InboundRow
    {
        public string Sink { get; set; } = "";
        public string Kind { get; set; } = "";
        public string InSitu { get; set; } = "";
        public string Dump { get; set; } = "";
        public string Tonnage { get; set; } = "";
        public string Done { get; set; } = "";
        public string AfterRemaining { get; set; } = "";
        public string Note { get; set; } = "";
        public bool Over { get; set; }
    }

    /// <summary>一个去向的当日入方账（实方 / 占容 / 吨量 · 计划与已排分开记）。</summary>
    private sealed class Inbound
    {
        public string SinkId = "";
        public string SinkName = "";
        public SinkKind Kind;
        public double PlanInSituM3, PlanDumpM3, PlanTonnageT;
        public double DoneDumpM3;
        /// <summary>尚未排弃的占容方（已排部分已计入去向的 FilledM3，不能再扣一次）。</summary>
        public double PendingDumpM3 => Math.Max(0, PlanDumpM3 - DoneDumpM3);
    }

    // ── 控件（与原 XAML x:Name 一一对应）──
    private readonly TextBlock srcLabel = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox chkGeoCols = new() { Content = "坐标与时窗", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid grid = TaskUi.Grid(readOnly: false, single: true);
    private readonly DataGrid inGrid = TaskUi.Grid(readOnly: true, single: true);
    private readonly TextBlock inHint = new() { Margin = new Thickness(2, 6, 2, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock opStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
    private readonly TextBlock totalStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly List<DataGridColumn> _geoCols = new();

    public SinkLedgerWindow()
    {
        Title = "去向台账 — 日常生产组织";
        TaskUi.Place(this, 1480, 700);

        // ① 标题条
        var header = TaskUi.Header("去向台账", "排土场 / 破碎站 / 煤仓 / 堆场 统一台账 — 能排到哪、还能排多少（库容口径=占容方 V实×Kr）");

        // ② 工具条：刷新 / 新增 / 删除 / 盘点 / 保存 / 关闭 + 数据来源
        var tool = new DockPanel();
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); tool.Children.Add(c); }
        var refresh = TaskUi.Btn("刷新", OnRefresh, 72); ToolTip.SetTip(refresh, "丢弃未保存的改动，重读台账"); L(refresh);
        var rebuild = TaskUi.Btn("按台账重建排土场…", OnRebuildFromLedger, 140, bold: true);
        ToolTip.SetTip(rebuild, "从【采掘单元台账】的排土位置按场名归并出排土场。\n\n"
            + "· 场名**原样取台账那一列** —— 绑定端读的是同一列，所以名字天然对齐\n"
            + "　（此前台账写「内排土场1」、去向台账叫「内排土场」，差一个字符，17 个排土面整月零入方）；\n"
            + "· 设计容量 = Σ 位置库容（占容方 V容），与排产扣库容同一本账；\n\n"
            + "⚠ 已填 / 坐标 / 通过能力 / 工作线长 / 兜底运距 / 时窗**派生不出来**，一律留空 ——\n"
            + "　台账里没有这些，编一个出来会让运距和库容告警看着正常而实际是假的。\n"
            + "　已填走「盘点修正」，坐标在「坐标与时窗」那几列补。\n\n"
            + "已存在的同名场**只补容量与台阶高，不动你填过的其它列**。");
        L(rebuild);
        var add = TaskUi.Btn("新增去向", OnAdd, 86); ToolTip.SetTip(add, "加一行空去向（默认外排土场）；改好类型/名称/容量后点「保存」才入库"); L(add);
        var del = TaskUi.Btn("删除去向", OnDelete, 86); ToolTip.SetTip(del, "删除选中去向（二次确认）；当日有入方的去向不允许删"); L(del);
        var st = TaskUi.Btn("盘点修正…", OnStocktake, 92); ToolTip.SetTip(st, "改「已填」的唯一入口：显式盘点 + 记录原因，留一条盘点流水"); L(st);
        // 坐标与时窗这 6 列合计 500px 出头，常看的列被它们挤到窗外。它们是**建档时录一次**的东西 ⇒ 默认收起。
        ToolTip.SetTip(chkGeoCols, "展开 X/Y/Z 坐标、开放起止、启用期次这 6 列。\n它们建档时录一次就不动了，日常看的是容量/充填率/可接物料 —— 常驻会把那几列挤到窗外。");
        chkGeoCols.IsCheckedChanged += (_, _) => ApplyGeoCols();
        L(chkGeoCols);
        var save = TaskUi.Btn("保存", OnSave, 72); ToolTip.SetTip(save, "把台账写回 dump_site / load_unload_point / sink_profile"); L(save);
        var close = TaskUi.Btn("关闭", OnClose, 72); close.Margin = new Thickness(0, 0, 14, 0); L(close);
        TaskUi.Theme(srcLabel, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        tool.Children.Add(srcLabel);

        // ③ 去向主表（可编辑；已填/剩余/充填率只读）
        grid.Margin = new Thickness(10, 10, 10, 6);
        grid.Columns.Add(Derived("编号", nameof(SinkRow.Id), 92));
        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("名称"), Binding = new Binding(nameof(SinkRow.Name)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        // 类型：SinkKind + .Label()，中文名唯一出处；跨族改类型要换表，保存时会明确拦下
        grid.Columns.Add(ComboCol("类型", 92, nameof(SinkRow.KindLabel), nameof(SinkRow.KindOptions), nameof(SinkRow.Kind), nameof(KindOption.Kind), nameof(KindOption.Label),
            "排弃类存 dump_site，通过型存 load_unload_point；改到另一族须删除后重建"));
        grid.Columns.Add(ComboCol("状态", 86, nameof(SinkRow.StatusLabel), nameof(SinkRow.StatusOptions), nameof(SinkRow.StatusCode), nameof(StatusOption.Code), nameof(StatusOption.Label), null));
        // 0 = 不限：破碎站/煤仓是通过型去向，卸多少走多少、不占库容
        grid.Columns.Add(NumCol("设计容量", nameof(SinkRow.CapacityWan), 96, "{0:0.##}", "万m³（占容方 V实×Kr）· 填 0 = 不限（破碎站/煤仓是通过型去向，卸多少走多少、不占库容）"));
        // 已填 / 剩余 / 充填率：实绩累计出来的账，普通编辑一律只读，要改走「盘点修正」
        grid.Columns.Add(Derived("已填", nameof(SinkRow.Filled), 76, "万m³ 占容方 · 实绩累计出来的账，要改走「盘点修正」"));
        grid.Columns.Add(Derived("剩余", nameof(SinkRow.Remaining), 76, "万m³ 占容方 = 设计容量 − 已填", bold: true));
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = TaskUi.Head("充填率"), Width = new DataGridLength(104), SortMemberPath = nameof(SinkRow.FillPct), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<SinkRow>((_, _) =>
            {
                var g = new Grid();
                g.Bind(ToolTip.TipProperty, new Binding(nameof(SinkRow.FillTip)));
                var pb = new ProgressBar { Height = 15, Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
                pb.Bind(ProgressBar.ValueProperty, new Binding(nameof(SinkRow.FillPct)) { Mode = BindingMode.OneWay });
                pb.Bind(ProgressBar.ForegroundProperty, new Binding(nameof(SinkRow.FillBrush)));
                TaskUi.Theme(pb, ProgressBar.BackgroundProperty, "Theme.Input.Background");
                TaskUi.Theme(pb, ProgressBar.BorderBrushProperty, "Theme.Surface.Border");
                var t = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                t.Bind(TextBlock.TextProperty, new Binding(nameof(SinkRow.FillText)));
                g.Children.Add(pb); g.Children.Add(t);
                return g;
            }),
        });
        grid.Columns.Add(NumCol("通过能力", nameof(SinkRow.Tph), 88, "{0:0.##}", "t/h · 填 0 = 不限"));
        grid.Columns.Add(NumCol("台阶高", nameof(SinkRow.BenchH), 72, "{0:0.##}", "m"));
        grid.Columns.Add(NumCol("排弃层", nameof(SinkRow.Level), 72, null, "当前排弃层"));
        grid.Columns.Add(NumCol("工作线长", nameof(SinkRow.LineLen), 80, "{0:0.#}", "m"));
        grid.Columns.Add(NumCol("兜底运距", nameof(SinkRow.FallbackKm), 80, "{0:0.##}", "km · 路网与坐标都对不上时的第三层兜底"));
        // 坐标（V036）：排土场存 sink_profile.x/y/z，卸载点存 load_unload_point.x/y/z；两族都可编辑，装载器按去向类型自动落表。
        var colX = StyledNum("X(m)", nameof(SinkRow.X), 86, "{0:0.##}", nameof(SinkRow.CoordTip), nameof(SinkRow.CoordBrush), nameof(SinkRow.CoordStyle), null, "去向代表点平面坐标，与路网节点同一坐标系。X、Y 均为 0 = 未录坐标");
        var colY = StyledNum("Y(m)", nameof(SinkRow.Y), 86, "{0:0.##}", nameof(SinkRow.CoordTip), nameof(SinkRow.CoordBrush), nameof(SinkRow.CoordStyle), null, "去向代表点平面坐标，与路网节点同一坐标系。X、Y 均为 0 = 未录坐标");
        var colZ = StyledNum("Z(m)", nameof(SinkRow.Z), 78, "{0:0.##}", nameof(SinkRow.CoordTip), nameof(SinkRow.CoordBrush), nameof(SinkRow.CoordStyle), null, "去向代表点标高。Z=0 是合法标高，不参与「有没有坐标」的判定");
        var colOpenFrom = NumCol("开放起", nameof(SinkRow.OpenFrom), 72, "{0:0.#}", "小时");
        var colOpenTo = NumCol("开放止", nameof(SinkRow.OpenTo), 72, "{0:0.#}", "小时");
        // 启用期次：内排土场的时机开关。开放起/止管"今天几点到几点能卸"，本列管"从哪个月起才存在"。
        var colOpenPeriod = StyledNum("启用期次", nameof(SinkRow.OpenPeriod), 92, null, nameof(SinkRow.PeriodTip), nameof(SinkRow.PeriodBrush), null, nameof(SinkRow.PeriodWeight), "内排土场须等采空区形成后才能启用，填期次标签（如 2026-09）；留空 = 已启用");
        foreach (var c in new DataGridColumn[] { colX, colY, colZ, colOpenFrom, colOpenTo, colOpenPeriod }) { c.IsVisible = false; _geoCols.Add(c); grid.Columns.Add(c); }
        // 可接物料：多选，选项源 = MaterialCatalog.All；全不选 = 不限定。用「摘要 + … 按钮开多选框」而不是把复选框塞进下拉。
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = TaskUi.Head("可接物料"), Width = new DataGridLength(1.6, DataGridLengthUnitType.Star), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<SinkRow>((_, _) =>
            {
                var dp = new DockPanel { LastChildFill = true };
                dp.Bind(ToolTip.TipProperty, new Binding(nameof(SinkRow.MaterialsTip)));
                var btn = new Button { Content = "…", Width = 26, Padding = new Thickness(0), Margin = new Thickness(4, 1, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Center };
                ToolTip.SetTip(btn, "选择本点可接的物料（多选）");
                btn.Click += (s, _) => { if (s is Control c && c.DataContext is SinkRow row) OnPickMaterials(row); };
                DockPanel.SetDock(btn, Avalonia.Controls.Dock.Right); dp.Children.Add(btn);
                var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };   // 不设 CharacterEllipsis：DataGrid 星号列里会引发布局死循环（见 datagrid-star-column-fit 记录）
                t.Bind(TextBlock.TextProperty, new Binding(nameof(SinkRow.MaterialsCaption)));
                dp.Children.Add(t);
                return dp;
            }),
        });
        // 新增未入库的行：整行加粗，让"哪几条还没落库"一眼可见
        grid.LoadingRow += (_, e) => e.Row.FontWeight = e.Row.DataContext is SinkRow r && r.IsNew ? FontWeight.Bold : FontWeight.Normal;

        // ④ 本日入方（库容预警：排后剩余为负 ⇒ 整行标红）
        inGrid.MaxHeight = 160; inGrid.Margin = new Thickness(0);
        inGrid.Columns.Add(StarText("去向", nameof(InboundRow.Sink), 1.2));
        inGrid.Columns.Add(TaskUi.TextCol("类型", nameof(InboundRow.Kind), 80));
        inGrid.Columns.Add(TaskUi.TextCol("今日入方(万m³实方)", nameof(InboundRow.InSitu), 130));
        inGrid.Columns.Add(TaskUi.TextCol("占容(万m³)", nameof(InboundRow.Dump), 95));
        inGrid.Columns.Add(TaskUi.TextCol("吨量(万t)", nameof(InboundRow.Tonnage), 90));
        inGrid.Columns.Add(TaskUi.TextCol("已排(占容万m³)", nameof(InboundRow.Done), 105));
        inGrid.Columns.Add(TaskUi.TextCol("排后剩余(万m³)", nameof(InboundRow.AfterRemaining), 115));
        inGrid.Columns.Add(StarText("结论", nameof(InboundRow.Note), 2));
        // 原 AlertCell：排后剩余为负 ⇒ 整行红字加粗
        inGrid.LoadingRow += (_, e) =>
        {
            bool over = e.Row.DataContext is InboundRow r && r.Over;
            e.Row.FontWeight = over ? FontWeight.Bold : FontWeight.Normal;
            if (over) e.Row.Foreground = DangerBrush; else e.Row.ClearValue(ForegroundProperty);
        };
        TaskUi.Theme(inHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var inPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(inGrid, 0); Grid.SetRow(inHint, 1); inPanel.Children.Add(inGrid); inPanel.Children.Add(inHint);
        var inBox = TaskUi.GroupBox("本日入方（按当日任务的去向聚合 · 排后剩余为负即库容告警）", inPanel, new Thickness(10, 0, 10, 6), 6);

        // ⑤ 状态栏：全矿今日排弃合计 / 内排率 / 加权平均运距
        var footDock = new DockPanel();
        TaskUi.Theme(opStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        DockPanel.SetDock(opStatus, Avalonia.Controls.Dock.Right); footDock.Children.Add(opStatus);
        TaskUi.Theme(totalStatus, TextBlock.ForegroundProperty, "Theme.Text.Body");
        footDock.Children.Add(totalStatus);
        var foot = TaskUi.Bar(footDock, top: false, padY: 9);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(grid, 2); Grid.SetRow(inBox, 3); Grid.SetRow(foot, 4);
        root.Children.Add(header); root.Children.Add(bar); root.Children.Add(grid); root.Children.Add(inBox); root.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = root;

        Opened += (_, _) => { ApplyGeoCols(); Reload(); };
    }

    // ── 列小件 ───────────────────────────────────────────────────────────────

    /// <summary>只读派生列（原 DerivedCell：灰显以示"这里改不了"）。</summary>
    private static DataGridTemplateColumn Derived(string header, string path, double width, string? tip = null, bool bold = false)
    {
        var col = new DataGridTemplateColumn { Header = TaskUi.Head(header, tip), Width = new DataGridLength(width), IsReadOnly = true, SortMemberPath = path };
        col.CellTemplate = new FuncDataTemplate<SinkRow>((_, _) =>
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
            tb.Bind(TextBlock.TextProperty, new Binding(path));
            TaskUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Muted");
            return tb;
        });
        return col;
    }

    /// <summary>可编辑数值列（原 DataGridTextColumn + StringFormat）。</summary>
    private static DataGridTextColumn NumCol(string header, string path, double width, string? format, string? tip)
        => new() { Header = TaskUi.Head(header, tip), Binding = new Binding(path) { Mode = BindingMode.TwoWay, StringFormat = format }, Width = new DataGridLength(width) };

    /// <summary>带条件观感的可编辑列（原 CoordCell / PeriodCell：前景/斜体/粗体/提示绑到行）。</summary>
    private static DataGridTemplateColumn StyledNum(string header, string path, double width, string? format,
        string tipPath, string brushPath, string? stylePath, string? weightPath, string headerTip)
    {
        var col = new DataGridTemplateColumn { Header = TaskUi.Head(header, headerTip), Width = new DataGridLength(width), IsReadOnly = false, SortMemberPath = path };
        col.CellTemplate = new FuncDataTemplate<SinkRow>((_, _) =>
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            tb.Bind(TextBlock.TextProperty, new Binding(path) { StringFormat = format });
            tb.Bind(TextBlock.ForegroundProperty, new Binding(brushPath));
            if (stylePath != null) tb.Bind(TextBlock.FontStyleProperty, new Binding(stylePath));
            if (weightPath != null) tb.Bind(TextBlock.FontWeightProperty, new Binding(weightPath));
            tb.Bind(ToolTip.TipProperty, new Binding(tipPath));
            return tb;
        });
        col.CellEditingTemplate = new FuncDataTemplate<SinkRow>((_, _) =>
        {
            var tx = new TextBox { VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(6, 2) };
            tx.Bind(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            return tx;
        });
        return col;
    }

    /// <summary>下拉列（原 DataGridTemplateColumn 里的 ComboBox：SelectedValuePath / DisplayMemberPath）。</summary>
    private static DataGridTemplateColumn ComboCol(string header, double width, string sortPath, string itemsPath, string valuePath, string valueMember, string displayMember, string? tip)
    {
        var col = new DataGridTemplateColumn { Header = TaskUi.Head(header), Width = new DataGridLength(width), SortMemberPath = sortPath, IsReadOnly = true };
        col.CellTemplate = new FuncDataTemplate<SinkRow>((_, _) =>
        {
            var cb = new ComboBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0, Padding = new Thickness(6, 2) };
            cb.Bind(ItemsControl.ItemsSourceProperty, new Binding(itemsPath));
            cb.SelectedValueBinding = new Binding(valueMember);
            cb.DisplayMemberBinding = new Binding(displayMember);
            cb.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedValueProperty, new Binding(valuePath) { Mode = BindingMode.TwoWay });
            if (tip != null) ToolTip.SetTip(cb, tip);
            return cb;
        });
        return col;
    }

    private static DataGridTextColumn StarText(string header, string path, double star)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path), Width = new DataGridLength(star, DataGridLengthUnitType.Star) };

    // ── 装载 ─────────────────────────────────────────────────────────────────

    public void Reload()
    {
        // 编辑副本：登记簿是全局的（别的窗口也在用），台账上的改动在保存前不许外泄。
        _work = SafeRegistry().Clone();
        _dirty = false;

        srcLabel.Text = "数据来源：" + SafeSourceLabel();
        _inbound = TodayInbound();

        FillSinkGrid(_work);
        FillInboundGrid(_work, _inbound);
        FillTotals(_work, _inbound);
    }

    private static SinkRegistry SafeRegistry()
    {
        try
        {
            var reg = SinkRegistryLoader.Current;
            if (reg != null && reg.All.Count > 0) return reg;
        }
        catch { /* DB/服务未就绪 → 落样例 */ }
        return SinkRegistry.Sample();
    }

    private static string SafeSourceLabel()
    {
        try { return SinkRegistryLoader.LastSourceLabel; }
        catch (Exception ex) { return $"去向装载器不可用（{Short(ex)}），已用内置样例去向"; }
    }

    private void FillSinkGrid(SinkRegistry reg)
    {
        // 排弃类在前（现场最关心库容），其次通过型；同类按充填率降序——快满的先跳出来。
        _rows = reg.All
            .OrderByDescending(s => s.IsDumping)
            .ThenByDescending(s => s.FillRate)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .Select(s => new SinkRow(s, isNew: false, OnRowEdited))
            .ToList();
        grid.ItemsSource = null;
        grid.ItemsSource = _rows;
    }

    private void OnRowEdited()
    {
        _dirty = true;
        opStatus.Text = "有未保存的改动——点「保存」写回台账";
        opStatus.Foreground = WarnBrush;
    }

    private static string StatusText(SinkNode s)
        => s.IsActive ? "在用"
         : string.Equals(s.Status, "full", StringComparison.OrdinalIgnoreCase) ? "已排满"
         : string.Equals(s.Status, "closed", StringComparison.OrdinalIgnoreCase) ? "已关闭"
         : s.Status;

    /// <summary>状态归一化：中文/英文都收，认不出按在用（写库前装载器还会再校一次）。</summary>
    private static string NormalizeStatusCode(string? raw)
    {
        string v = (raw ?? "").Trim();
        if (v.Equals("full", StringComparison.OrdinalIgnoreCase) || v == "已排满") return "full";
        if (v.Equals("closed", StringComparison.OrdinalIgnoreCase) || v == "已关闭") return "closed";
        return "active";
    }

    private static IBrush FillColor(double pct)
        => pct > DangerFillPct ? DangerBrush : pct >= WarnFillPct ? WarnBrush : OkBrush;

    // ── 本日入方 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 从当日任务聚合每个去向的入方。
    /// 【只取采装侧 Load】：排土面的目标本身就是入方推导来的，两侧都算会把同一批料记两遍。
    /// </summary>
    private static List<Inbound> TodayInbound()
    {
        var map = new Dictionary<string, Inbound>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in SampleTaskBoard.Day())
            {
                if (t.Process != ProcessType.Load) continue;
                if (t.TargetVolumeM3 <= 1e-6 && t.ActualVolumeM3 <= 1e-6) continue;

                string key = string.IsNullOrWhiteSpace(t.DestinationId)
                    ? (string.IsNullOrWhiteSpace(t.DestinationName) ? "" : t.DestinationName)
                    : t.DestinationId;

                if (!map.TryGetValue(key, out var acc))
                {
                    acc = new Inbound
                    {
                        SinkId = key,
                        SinkName = string.IsNullOrWhiteSpace(t.DestinationName) ? key : t.DestinationName,
                        Kind = t.DestinationKind,
                    };
                    map[key] = acc;
                }

                // 计划侧按物料流六元组拆（混采面一条任务可拆出煤/岩两条流），口径全走 MaterialCatalog。
                foreach (var f in t.ToFlows(SampleTaskBoard.DateLabel))
                {
                    acc.PlanInSituM3 += f.InSituM3;
                    acc.PlanDumpM3 += f.DumpM3;
                    acc.PlanTonnageT += f.TonnageT;
                }
                // 已排侧与 SampleTaskBoard 的实绩回灌同一公式，保证「已填」不被重复扣。
                if (t.ActualVolumeM3 > 1e-6) acc.DoneDumpM3 += t.ResolvedMix.ToDumpM3(t.ActualVolumeM3);
            }
        }
        catch { /* 任务台账不可用 → 入方表留空，主表照常显示 */ }

        return map.Values.OrderByDescending(x => x.PlanDumpM3).ToList();
    }

    private void FillInboundGrid(SinkRegistry reg, List<Inbound> inbound)
    {
        var rows = new List<InboundRow>();
        int overCount = 0, noDest = 0;

        foreach (var b in inbound)
        {
            if (b.SinkId.Length == 0) { noDest++; continue; }

            var node = reg.Find(b.SinkId)
                    ?? reg.All.FirstOrDefault(x => string.Equals(x.Name, b.SinkName, StringComparison.OrdinalIgnoreCase));
            bool dumping = node?.IsDumping ?? b.Kind.IsDumping();
            bool limited = node?.IsCapacityLimited ?? false;

            // 排后剩余 = 当前剩余 − 今日【尚未排弃】的占容方。
            // 已排部分已经由实绩回灌计进 FilledM3，再扣一次就是重复扣。
            double after = limited ? node!.RemainingM3 - b.PendingDumpM3 : double.PositiveInfinity;
            bool over = limited && after < 0;
            if (over) overCount++;

            rows.Add(new InboundRow
            {
                Sink = node?.Name ?? b.SinkName,
                Kind = (node?.Kind ?? b.Kind).Label(),
                InSitu = $"{b.PlanInSituM3 / 1e4:0.####}",
                Dump = dumping ? $"{b.PlanDumpM3 / 1e4:0.####}" : "—",
                Tonnage = $"{b.PlanTonnageT / 1e4:0.####}",
                Done = dumping ? $"{b.DoneDumpM3 / 1e4:0.####}" : "—",
                AfterRemaining = limited ? $"{after / 1e4:0.##}" : "不限",
                Note = InboundNote(node, b, dumping, limited, after),
                Over = over,
            });
        }

        inGrid.ItemsSource = null;
        inGrid.ItemsSource = rows;

        var hints = new List<string>
        {
            "口径：入方按采装侧任务聚合（排土面目标由入方推导，两侧同算会重复）；" +
            "「已填」已含今日实绩回灌，故排后剩余只扣今日尚未排弃的占容方。",
        };
        if (rows.Count == 0) hints.Add("当日无任何入方——各采装面尚未指定卸点，或当日无采装任务。");
        if (noDest > 0) hints.Add($"⚠ 有 {noDest} 组任务未指定卸点，其产出未计入任何去向，运距与编组无法核算。");
        if (overCount > 0) hints.Add($"⚠ {overCount} 个去向按今日计划排完即超容，须调减剥离量或启用/扩容其它排土场。");
        inHint.Text = string.Join("　", hints);
        if (overCount > 0 || noDest > 0) inHint.Foreground = DangerBrush; else TaskUi.Theme(inHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
    }

    private static string InboundNote(SinkNode? node, Inbound b, bool dumping, bool limited, double after)
    {
        if (node == null) return "该去向不在登记簿内（请在「排土场管理」/本「去向台账」补录）";
        if (!node.IsActive) return $"去向状态 {node.Status}，本日不应再排";
        if (!dumping) return $"通过型去向，不占库容；受卸点通过能力约束（{(node.AcceptTph > 0 ? $"{node.AcceptTph:0} t/h" : "不限")}）";
        if (!limited) return "容量未录（无法校核库容，建议在排土场台账补设计容量）";

        if (after < 0) return $"库容不足：缺 {-after / 1e4:0.##} 万m³占容";
        double pctAfter = node.DesignCapacityM3 <= 0 ? 0 : (node.DesignCapacityM3 - after) / node.DesignCapacityM3 * 100;
        string adv = node.WorkLineLengthM > 1e-6 && node.BenchHeightM > 1e-6
            ? $"，排土推进 {node.AdvanceMetersFor(b.PendingDumpM3):0.##} m"
            : "";
        return pctAfter > DangerFillPct
            ? $"排后充填 {pctAfter:0.#}%，已进危险区，须准备接续排土场{adv}"
            : pctAfter >= WarnFillPct
                ? $"排后充填 {pctAfter:0.#}%，警示{adv}"
                : $"排后充填 {pctAfter:0.#}%，正常{adv}";
    }

    // ── 状态栏合计 ───────────────────────────────────────────────────────────

    private void FillTotals(SinkRegistry reg, List<Inbound> inbound)
    {
        double dumpAll = inbound.Where(b => b.Kind.IsDumping()).Sum(b => b.PlanDumpM3);
        double dumpIn = inbound.Where(b => b.Kind == SinkKind.InternalDump).Sum(b => b.PlanDumpM3);
        double internalPct = dumpAll <= 1e-6 ? 0 : dumpIn / dumpAll * 100;

        // 加权平均运距：吨量加权（运输功 ÷ 总吨量），与 PeriodBalance 同口径。
        double work = 0, tonn = 0;
        try
        {
            foreach (var t in SampleTaskBoard.Day().Where(t => t.Process == ProcessType.Load))
            {
                work += t.TransportWorkTKm;
                tonn += t.TargetTonnageT;
            }
        }
        catch { }
        double avgKm = tonn <= 1e-6 ? 0 : work / tonn;

        int limited = reg.All.Count(s => s.IsCapacityLimited);
        int danger = reg.All.Count(s => s.IsCapacityLimited && s.FillRate * 100 > DangerFillPct);

        // 无坐标的去向：运距降级到兜底值，这对"运距准不准"是决定性的，得让人看见有几个。
        int noCoord = reg.All.Count(s => Math.Abs(s.X) < 1e-6 && Math.Abs(s.Y) < 1e-6);

        totalStatus.Text =
            $"全矿今日排弃占容合计 {dumpAll / 1e4:0.##} 万m³　|　内排率 {internalPct:0.#}%　|　" +
            $"吨量加权平均运距 {avgKm:0.##} km　|　去向 {reg.All.Count} 个（受库容约束 {limited} 个" +
            (danger > 0 ? $"，其中 {danger} 个充填率 >{DangerFillPct:0}%" : "") + "）" +
            (noCoord > 0
                ? $"　|　⚠ {noCoord} 个去向未录坐标（X/Y 列琥珀色那几行）：路网按编号匹配不上就只能用兜底运距，"
                  + "补上坐标才能按最近节点吸附出真实运距"
                : "");
    }

    // ── 按钮 ─────────────────────────────────────────────────────────────────

    private async void OnRefresh()
    {
        if (_dirty && !await Confirm("有未保存的改动，刷新会丢弃它们。确定重读台账？", "刷新")) return;

        try
        {
            SinkRegistryLoader.Invalidate();
            var reg = SinkRegistryLoader.Load();

            // 重读台账会丢掉「今日实绩已排」的内存增量，这里按同一公式补回，
            // 让「已填」在刷新前后是同一口径。走 SinkRegistry.AddFilled（只改内存），
            // 不走 SinkRegistryLoader.AddFilled——那条路会再回写一次台账，造成重复计量。
            foreach (var b in TodayInbound())
                if (b.DoneDumpM3 > 1e-6 && b.Kind.IsDumping() && b.SinkId.Length > 0)
                    reg.AddFilled(b.SinkId, b.DoneDumpM3);

            Reload();
            opStatus.Text = "已重读去向台账";
            MutedStatus();
        }
        catch (Exception ex)
        {
            opStatus.Text = $"刷新失败：{Short(ex)}";
            opStatus.Foreground = DangerBrush;
        }
    }

    /// <summary>
    /// 新增去向：先在表里加一行（默认外排土场），改好类型/名称/容量后点「保存」才入库。
    /// 类型决定落哪张表——排弃类进 dump_site，通过型进 load_unload_point，装载器按 Kind 选表。
    /// </summary>
    private void OnAdd()
    {
        try
        {
            var node = SinkRegistryLoader.CreateNew(SinkKind.ExternalDump);
            _work.Put(node);

            var row = new SinkRow(node, isNew: true, OnRowEdited);
            _rows.Insert(0, row);
            grid.ItemsSource = null;
            grid.ItemsSource = _rows;
            grid.SelectedItem = row;
            grid.ScrollIntoView(row, null);

            _dirty = true;
            opStatus.Text = $"已加一行「{node.Name}」（编号 {node.Id}）——改好类型/名称/容量后点「保存」才入库";
            opStatus.Foreground = WarnBrush;
        }
        catch (Exception ex)
        {
            opStatus.Text = $"新增失败：{Short(ex)}";
            opStatus.Foreground = DangerBrush;
        }
    }

    /// <summary>
    /// 删除去向：二次确认；**当日有入方的去向不许删** —— 那批料已经安排出去了，
    /// 删掉去向等于让今天的运量没有落点，库容校核与内排率当场失真。
    /// </summary>
    private async void OnDelete()
    {
        if (grid.SelectedItem is not SinkRow row)
        {
            opStatus.Text = "请先在表里选中要删除的去向";
            opStatus.Foreground = WarnBrush;
            return;
        }

        var node = row.Node;
        string name = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;

        // ① 当日有入方 → 先提示，不删
        var hit = _inbound.FirstOrDefault(b =>
            (b.SinkId.Length > 0 && string.Equals(b.SinkId, node.Id, StringComparison.OrdinalIgnoreCase))
            || string.Equals(b.SinkName, node.Name, StringComparison.OrdinalIgnoreCase));
        if (hit != null && (hit.PlanDumpM3 > 1e-6 || hit.DoneDumpM3 > 1e-6 || hit.PlanInSituM3 > 1e-6))
        {
            await TaskUi.Info(this, "删除去向",
                $"「{name}」当日有入方（计划占容 {hit.PlanDumpM3 / 1e4:0.##} 万m³、已排 {hit.DoneDumpM3 / 1e4:0.##} 万m³），不允许删除。\n\n" +
                "请先把投向它的作业面改到别的去向（「作业面台账」），当日入方清零后再删。");
            opStatus.Text = $"未删除：「{name}」当日有入方";
            opStatus.Foreground = WarnBrush;
            return;
        }

        // ② 二次确认
        if (!await Confirm($"确定删除去向「{name}」（编号 {node.Id}）？\n\n" +
                     (row.IsNew ? "这条还没入库，删除只是把它从表里去掉。"
                                : "台账里的本体行与扩展档案会一起删除，且不可撤销。"),
                     "删除去向")) return;

        // ③ 没入库的新行：本地删掉即可，不必惊动数据库
        if (row.IsNew)
        {
            RemoveRow(row);
            opStatus.Text = $"已移除未入库的「{name}」";
            MutedStatus();
            return;
        }

        var res = SinkRegistryLoader.Delete(node);
        if (res.Deleted > 0)
        {
            RemoveRow(row);
            SinkRegistryLoader.Invalidate();     // 让别的窗口下次取到新台账
            opStatus.Text = $"已删除「{name}」　{res.Caption}";
            MutedStatus();
        }
        else
        {
            await ReportFailure(res, "删除去向");
        }
    }

    /// <summary>
    /// 「按台账重建排土场」—— 真实排土场清单的唯一正当来源是<b>采掘单元台账</b>。
    ///
    /// <para><b>为什么名字会天然对齐</b>：场名原样取台账的「采场/排土场」列，
    /// 而绑定端（`ProcessZoneFaceSource`）读的也是这一列 —— 同一个源就不会差字符。
    /// 此前台账写「内排土场1」、去向台账叫「内排土场」，绑定按名字精确配，
    /// 差一个字符 ⇒ <b>17 个排土面整月零入方</b>，而甘特上只是少几行、不报错。</para>
    ///
    /// <para><b>已存在的同名场只补容量与台阶高</b>，不动你填过的坐标/时窗/兜底运距 ——
    /// 那些是人录的事实，重建不许抹掉。</para>
    /// </summary>
    private async void OnRebuildFromLedger()
    {
        const string NL = "\n";
        List<UnitLedger.MiningUnitLedger.Row> rows;
        string from;
        try
        {
            var store = new UnitLedger.MonthlyUnitLedgerStore();
            if (!store.TryLoadBase(out rows, out var issues))
            {
                await TaskUi.Info(this, "按台账重建",
                    "读不到采掘单元台账的基表 —— 排土场清单派生不出来。\n\n"
                  + string.Join(NL, issues ?? new List<string>()));
                return;
            }
            from = "基表_采掘单元.csv";
        }
        catch (Exception ex)
        {
            await TaskUi.Info(this, "按台账重建", "台账读取出错：" + ex.Message);
            return;
        }

        var built = SinkFromLedgerBuilder.Build(rows);
        string msg = built.Headline + NL + NL + "来源：" + from
                   + (built.Notes.Count > 0 ? NL + NL + "· " + string.Join(NL + "· ", built.Notes) : "");
        if (!built.Ok)
        { await TaskUi.Info(this, "按台账重建", msg); return; }

        if (!await Confirm(msg + NL + NL + "写入去向台账？（同名场只补容量与台阶高，不动你填过的其它列）", "按台账重建")) return;

        int created = 0, updated = 0; var errs = new List<string>();
        foreach (var sk in built.Sinks)
        {
            try
            {
                var have = EquipmentDataContext.DumpSites.All(activeOnly: false)
                           .FirstOrDefault(d => d != null &&
                               string.Equals((d.Name ?? "").Trim(), sk.Name, StringComparison.Ordinal));
                if (have != null)
                {
                    // ★ 只改设计容量那一列 —— 整行 Upsert 会把带外键的列一起重写，
                    //   原本就悬空的外键会让一次无关修改以 FOREIGN KEY constraint failed 收场。
                    EquipmentDataContext.DumpSites.UpdateDesignCapacity(
                        have.DumpId, Math.Round(sk.CapacityM3 / 1e4, 3));
                    updated++;
                    continue;
                }

                EquipmentDataContext.DumpSites.Upsert(new Data.Entities.DumpSite
                {
                    DumpId = SinkFromLedgerBuilder.IdOf(sk.Name),
                    Name = sk.Name,
                    // 名字里带「内排」就按内排，否则外排 —— 认不出时**按外排**（保守：外排不吃采空区约束）
                    DumpType = sk.Name.Contains("内排") ? "internal" : "external",
                    DesignCapacityWanM3 = Math.Round(sk.CapacityM3 / 1e4, 3),
                    // ★ 已填留 0 = **还没录**，不是"空的"。要录走「盘点修正」，那条路会留流水。
                    CurrentFilledWanM3 = 0,
                    BenchHeightM = sk.BenchHeightM > 0 ? sk.BenchHeightM : null,
                    Status = "active",
                    Notes = $"按采掘单元台账派生（{sk.SlotCount} 个排土位置）—— "
                          + "坐标/通过能力/工作线长/兜底运距/时窗均未录，需人工补",
                });
                created++;
            }
            catch (Exception ex) { if (errs.Count < 3) errs.Add($"{sk.Name}：{ex.Message}"); }
        }

        opStatus.Text = $"按台账重建：新建 {created} 个 · 更新容量 {updated} 个"
                     + (errs.Count > 0 ? "　◆ " + string.Join("；", errs) : "");
        Reload();
    }

    /// <summary>
    /// 盘点修正：改「已填」的唯一入口。必填原因，写 sink_stocktake 流水。
    /// 分开做而不是让它混在普通编辑里 —— 改账是需要凭据的事，不该和改个名字一样随手。
    /// </summary>
    private async void OnStocktake()
    {
        if (grid.SelectedItem is not SinkRow row)
        {
            opStatus.Text = "请先在表里选中要盘点的去向";
            opStatus.Foreground = WarnBrush;
            return;
        }

        var node = row.Node;
        string name = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;

        if (!node.IsDumping)
        {
            await TaskUi.Info(this, "盘点修正", $"「{name}」是通过型去向（{node.Kind.Label()}），卸多少走多少、不占排土库容，无需盘点。");
            return;
        }
        if (row.IsNew)
        {
            await TaskUi.Info(this, "盘点修正", $"「{name}」还没入库，请先点「保存」建档，再做盘点。");
            return;
        }

        var ask = await TryAskStocktake(this, node);
        if (ask == null) return;
        var (newFilledWan, reason) = ask.Value;

        var res = SinkRegistryLoader.Stocktake(node, newFilledWan * 1e4, reason);   // 万m³ → m³
        if (res.Ok)
        {
            row.RefreshDerived();
            FillInboundGrid(_work, _inbound);      // 排后剩余跟着变
            FillTotals(_work, _inbound);
            SinkRegistryLoader.Invalidate();
            opStatus.Text = $"「{name}」已盘点：已填改为 {newFilledWan:0.##} 万m³　{res.Caption}";
            MutedStatus();
        }
        else await ReportFailure(res, "盘点修正");
    }

    /// <summary>
    /// 收/放【坐标与时窗】那 6 列。
    /// <para><b>为什么默认收起</b>：主表 18 个固定宽列合计约 1770px，而窗口 1400px ——
    /// 星号列（名称、可接物料）被挤到 0 宽。这 6 列是<b>建档时录一次</b>的东西，让位给容量/充填率/可接物料。</para>
    /// <para><b>收起不是删掉</b>：列还在、数据还在、保存照样带上它们 —— 只是不占宽度。</para>
    /// </summary>
    private void ApplyGeoCols()
    {
        bool vis = chkGeoCols.IsChecked == true;
        foreach (var c in _geoCols) c.IsVisible = vis;
    }

    /// <summary>保存：把整张台账写回 dump_site / load_unload_point / sink_profile，逐条报结果。</summary>
    private async void OnSave()
    {
        // 正在编辑的单元格先落值，否则最后改的那一格会存不进去。
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var nodes = _rows.Select(r => r.Node).ToList();
        var res = SinkRegistryLoader.Save(nodes);

        if (res.Aborted)
        {
            opStatus.Text = res.Caption;
            opStatus.Foreground = DangerBrush;
            await TaskUi.Info(this, "保存去向台账",
                $"台账未保存：{res.AbortReason}\n\n改动还在窗口里，接通数据库后再点一次「保存」即可。");
            return;
        }

        // 有一条成了就得让别的窗口重读——半成功也是改动。
        if (res.Saved > 0) SinkRegistryLoader.Invalidate();

        opStatus.Text = res.Caption;
        opStatus.Foreground = res.Ok ? OkBrush : DangerBrush;

        if (res.Ok)
        {
            _dirty = false;
            Reload();      // 重读：新分配的卸载点编号、被表校正过的值，都以台账为准
        }
        else
        {
            // 失败的行留在窗口里让用户改——这时候刷新等于把人家的活儿扔了。
            await TaskUi.Info(this, "保存去向台账",
                $"保存完成，但有 {res.Failed} 条失败（成功 {res.Saved} 条）：\n\n"
                + string.Join("\n", res.Errors.Select(x => "· " + x))
                + (res.Notes.Count > 0 ? "\n\n提示：\n" + string.Join("\n", res.Notes.Select(x => "· " + x)) : "")
                + "\n\n失败的行仍在窗口里，改好后可再次保存。");
        }
    }

    private async void OnClose()
    {
        if (_dirty && !await Confirm("有未保存的改动，关闭会丢弃它们。确定关闭？", "关闭")) return;
        Close();
    }

    /// <summary>可接物料多选：选项源 = MaterialCatalog.All，勾选写回本点白名单。</summary>
    private async void OnPickMaterials(SinkRow row)
    {
        if (!await TryPickMaterials(this, row)) return;

        opStatus.Text = $"「{(string.IsNullOrWhiteSpace(row.Name) ? row.Id : row.Name)}」可接物料已改为：{row.MaterialsCaption}——点「保存」写回台账";
        opStatus.Foreground = WarnBrush;
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private void RemoveRow(SinkRow row)
    {
        _rows.Remove(row);
        // SinkRegistry 契约里没有 Remove，整表重灌是唯一的移除手段（行数是几十的量级，无所谓）。
        _work.Load(_work.All.Where(s => !ReferenceEquals(s, row.Node)).ToList());
        grid.ItemsSource = null;
        grid.ItemsSource = _rows;
        FillTotals(_work, _inbound);
    }

    private async Task ReportFailure(SinkRegistryLoader.SinkSaveResult res, string title)
    {
        opStatus.Text = res.Caption;
        opStatus.Foreground = DangerBrush;
        string body = res.Aborted
            ? res.AbortReason
            : string.Join("\n", res.Errors.Select(x => "· " + x));
        await TaskUi.Info(this, title, string.IsNullOrWhiteSpace(body) ? res.Caption : body);
    }

    private Task<bool> Confirm(string msg, string title) => TaskUi.Confirm(this, title, msg);

    /// <summary>次要文字色：回到主题的 Muted（原 SecondaryBrush()）。</summary>
    private void MutedStatus() => TaskUi.Theme(opStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");

    /// <summary>
    /// 可接物料多选框（就地构造）：列出 MaterialCatalog.All，勾中的即本点白名单。
    /// 全不勾 = 不限定，按物料自身的允许去向类型判定（表土只能进表土堆场那条合规约束在目录里）。
    /// </summary>
    private static async Task<bool> TryPickMaterials(Window owner, SinkRow row)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{row.Node.Caption}　可接物料",
            FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "勾选 = 只收勾中的物料；全不勾 = 不限定（按物料自身允许的去向类型判定）。\n"
                 + "勾选只能把口子收得更严：物料目录里不允许进本类去向的料，勾了也进不来。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Opacity = 0.75,
        });

        // 快照：取消时原样还原，不留半截改动
        var before = row.MaterialPicks.ToDictionary(p => p.Code, p => p.Checked, StringComparer.OrdinalIgnoreCase);
        var boxes = new List<CheckBox>();
        foreach (var pick in row.MaterialPicks)
        {
            var cb = new CheckBox
            {
                Content = $"{pick.Name}（{pick.Code}）",
                IsChecked = pick.Checked,
                Margin = new Thickness(0, 3, 0, 3),
                Tag = pick,
            };
            boxes.Add(cb);
            panel.Children.Add(cb);
        }

        var dlg = new Window
        {
            Title = "可接物料",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
        };
        TaskUi.Theme(dlg, BackgroundProperty, "Theme.Window.Background");
        var ok = new Button { Content = "确定", MinWidth = 72, Margin = new Thickness(0, 12, 8, 0), IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "取消", MinWidth = 72, Margin = new Thickness(0, 12, 0, 0), IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 640 };
        ok.Click += (_, _) => dlg.Close(true);
        cancel.Click += (_, _) => dlg.Close(false);

        if (await dlg.ShowDialog<bool?>(owner) != true) return false;

        bool changed = false;
        foreach (var cb in boxes)
        {
            if (cb.Tag is not MaterialPick pick) continue;
            bool want = cb.IsChecked == true;
            if (before.TryGetValue(pick.Code, out bool was) && was == want) continue;
            pick.Checked = want;       // setter 会写回 Node.AcceptedMaterials 并置脏
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// 盘点录入小窗（就地构造，不另开窗口文件）：新的已填量 + 修正原因，两项都要。
    /// 原因是必填的——没有凭据的改账等于把账做糊涂了。
    /// </summary>
    private static async Task<(double newFilledWan, string reason)?> TryAskStocktake(Window owner, SinkNode node)
    {
        var box = new TextBox { Text = $"{node.FilledM3 / 1e4:0.##}", Margin = new Thickness(0, 2, 0, 10) };
        var why = new TextBox
        {
            Margin = new Thickness(0, 2, 0, 10), MinHeight = 56,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top,
        };
        var err = new TextBlock { Foreground = DangerBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{node.Caption}\n设计容量 {node.DesignCapacityM3 / 1e4:0.##} 万m³　当前已填 {node.FilledM3 / 1e4:0.##} 万m³（充填 {node.FillRate * 100:0.#}%）",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontWeight = FontWeight.Bold,
        });
        panel.Children.Add(new TextBlock { Text = "盘点后已填（万m³，占容方口径 V实×Kr）：" });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "修正原因（必填，将记入盘点流水）：" });
        panel.Children.Add(why);
        panel.Children.Add(err);

        var ok = new Button { Content = "确定盘点", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = "库容盘点修正",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            Content = panel,
        };
        TaskUi.Theme(dlg, BackgroundProperty, "Theme.Window.Background");

        double parsed = 0;
        string reasonText = "";
        ok.Click += (_, _) =>
        {
            if (!double.TryParse(box.Text?.Trim(), out parsed) || parsed < 0)
            { err.Text = "盘点后已填必须是 ≥0 的数字（单位：万m³）。"; return; }
            reasonText = (why.Text ?? "").Trim();
            if (reasonText.Length == 0)
            { err.Text = "必须填写修正原因——改账要留凭据。"; return; }
            dlg.Close(true);
        };
        cancel.Click += (_, _) => dlg.Close(false);
        dlg.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        if (await dlg.ShowDialog<bool?>(owner) != true) return null;
        return (parsed, reasonText);
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
