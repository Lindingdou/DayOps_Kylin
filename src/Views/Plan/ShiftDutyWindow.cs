using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  「班组作业推演」—— 排产结果 → 每台设备每一班干什么（移植原 PlanLib.Views.ShiftDutyWindow）。
//
//  ══ 这个窗必须守住的三条口径 ══
//  ① **量口径不许并成一列**：原位实方 / 控制方量 / 排弃占容 / 承运量是四种东西。底部按口径分行显示，**不给"合计"**。
//  ② **削峰欠量单列**：ByBasis 是"排下去了多少"，Shortfalls 是"没排下去多少"。
//  ③ **CapM3 是 NaN 时说「排产没给台效」，不是 0**。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>排产结果 → 班组作业的查看窗（只读 + 导出）。</summary>
internal sealed class ShiftDutyWindow : Window
{
    private readonly ShiftInferenceResult _res;
    private readonly string _period;
    private readonly TextBlock _msg = new()
    {
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x53, 0x5A, 0x63)), FontSize = 12,
    };

    /// <summary>展示用的一行。</summary>
    public sealed class Row
    {
        public string 日期 { get; set; } = "";
        public string 班次 { get; set; } = "";
        public string 时段 { get; set; } = "";
        public string 工时 { get; set; } = "";
        public string 设备 { get; set; } = "";
        public string 角色 { get; set; } = "";
        public string 作业对象 { get; set; } = "";
        public string 量 { get; set; } = "";
        public string 口径 { get; set; } = "";
        public string 能力核对 { get; set; } = "";
    }

    public ShiftDutyWindow(ShiftInferenceResult res, string period)
    {
        _res = res ?? new ShiftInferenceResult();
        _period = (period ?? "").Trim();

        Title = $"班组作业推演　{_period}";
        PlanUi.Place(this, 1180, 720);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        var root = new DockPanel { Margin = new Thickness(12) };

        // ── 顶：一句话结论 ──
        var head = new TextBlock
        {
            Text = _res.Headline.Length > 0 ? _res.Headline : "（推演没有给出结论）",
            FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(head, Avalonia.Controls.Dock.Top);
        root.Children.Add(head);

        // ── 底：按口径分行的汇总 + 欠量 + 导出 ──
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        bottom.Children.Add(BuildTotals());
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(RoadUi.Btn("导出CSV…", () => _ = ExportCsvAsync(), 96));
        bottom.Children.Add(bar);
        bottom.Children.Add(_msg);
        root.Children.Add(bottom);

        // ── 中：明细（原版 AutoGenerateColumns；Avalonia 手列，列名同属性名）──
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5,
        };
        foreach (var (h, w) in new[] { ("日期", 90.0), ("班次", 90.0), ("时段", 96.0), ("工时", 130.0), ("设备", 80.0), ("角色", 56.0), ("作业对象", 130.0), ("量", 80.0), ("口径", 90.0), ("能力核对", 0.0) })
            grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(h), Width = w <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w), IsReadOnly = true });
        PlanUi.FitHeaders(grid);
        grid.ItemsSource = BuildRows(_res);
        root.Children.Add(grid);

        Content = root;
    }

    /// <summary>结果 → 表格行。<b>纯函数</b>，判据直接喂算例。</summary>
    public static List<Row> BuildRows(ShiftInferenceResult? res)
    {
        var list = new List<Row>();
        foreach (var d in (res?.Duties ?? new List<ShiftDuty>())
                 .OrderBy(x => x.Date, StringComparer.Ordinal)
                 .ThenBy(x => x.StartTime, StringComparer.Ordinal)
                 .ThenBy(x => x.MachineId, StringComparer.Ordinal))
        {
            list.Add(new Row
            {
                日期 = d.Date,
                班次 = d.Shift + (d.IsBlastShift ? "（爆破班）" : ""),
                时段 = $"{d.StartTime}-{d.EndTime}",
                工时 = d.EffectiveHours.ToString("0.##") + " h" + (d.IsBlastShift ? $"（已扣清场 {ShiftInference.BlastClearHours:0.#} h）" : ""),
                设备 = d.MachineId,
                角色 = EquipmentAssignResult.RoleName(d.Role),
                作业对象 = d.UnitId,
                量 = d.VolumeM3.ToString("N0"),
                口径 = d.Basis,
                能力核对 = CapText(d),
            });
        }
        return list;
    }

    /// <summary>能力核对这一列。<b>NaN 与 0 必须说成两句话</b>。</summary>
    public static string CapText(ShiftDuty d)
    {
        if (double.IsNaN(d.CapM3)) return "◆ 排产没给台效 ⇒ 无从校核";
        if (d.WasCapped) return $"⚠ 已削峰 −{d.CappedM3:N0}（上限 {d.CapM3:N0}）";
        return $"✓ 上限 {d.CapM3:N0}";
    }

    private Control BuildTotals()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "按量口径分列（**不给合计** —— 原位实方 / 控制方量 / 排弃占容 / 承运量是四种东西，并成一个数就没有任何真实含义）",
            FontWeight = FontWeight.Bold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });

        var byBasis = _res.ByBasis.OrderBy(x => x.Basis, StringComparer.Ordinal).ToList();
        if (byBasis.Count == 0) sp.Children.Add(Line("（没有推演出任何班级作业）", 0x99, 0x3C, 0x1D));

        foreach (var (basis, m3) in byBasis)
        {
            // 排下去了多少 / 没排下去多少 —— 两个数并排摆，谁也别想被吃掉
            double sf = _res.ShortfallOf(basis);
            string s = $"· {basis}：已排 {m3:N0} m³";
            if (sf > 1e-9) s += $"　⚠ 削峰排不下 {sf:N0} m³（**没排下去的，不是已排的一部分**）";
            sp.Children.Add(sf > 1e-9 ? Line(s, 0x99, 0x3C, 0x1D) : Line(s, 0x33, 0x3A, 0x40));
        }

        // 有欠量、但对应口径一条都没排上时，上面的循环碰不到它 —— 单独补一遍
        foreach (var g in _res.Shortfalls.GroupBy(x => x.Basis))
            if (!byBasis.Any(b => b.Basis == g.Key))
                sp.Children.Add(Line($"· {g.Key}：已排 0　⚠ 削峰排不下 {g.Sum(x => x.M3):N0} m³　（{string.Join("；", g.Select(x => x.Why).Distinct())}）", 0x99, 0x3C, 0x1D));

        if (_res.Skipped > 0)
            sp.Children.Add(Line($"◆ 有 {_res.Skipped} 笔排产结果没能落成班级作业 —— 见下方说明，这些量**不在**上面任何一行里。", 0x99, 0x3C, 0x1D));

        foreach (var n in _res.Notes) sp.Children.Add(Line("· " + n, 0x53, 0x5A, 0x63));
        return sp;
    }

    private static TextBlock Line(string t, byte r, byte g, byte b) => new()
    {
        Text = t, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
    };

    /// <summary>结果 → CSV。<b>纯函数</b>。表头带「推演」两个字 —— 这份表不是实绩，不得回写台账。</summary>
    public static string ToCsv(ShiftInferenceResult? res)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("日期,班次,起,止,有效工时h,设备,角色,作业对象,量m3(推演),量口径,能力核对");
        foreach (var d in (res?.Duties ?? new List<ShiftDuty>())
                 .OrderBy(x => x.Date, StringComparer.Ordinal)
                 .ThenBy(x => x.StartTime, StringComparer.Ordinal))
            sb.AppendLine(string.Join(",", new[]
            {
                d.Date, d.Shift + (d.IsBlastShift ? "(爆破班)" : ""), d.StartTime, d.EndTime,
                d.EffectiveHours.ToString("0.##"), d.MachineId,
                EquipmentAssignResult.RoleName(d.Role), d.UnitId,
                d.VolumeM3.ToString("0.##"), d.Basis, CapText(d),
            }.Select(Q)));

        // 欠量单独成段 —— 混进明细里会被求和吃掉
        var sf = (res?.Shortfalls ?? new List<(string Basis, double M3, string Why)>()).ToList();
        if (sf.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【削峰排不下的量 —— 不是上表的一部分】");
            sb.AppendLine("量口径,排不下m3,原因");
            foreach (var (basis, m3, why) in sf)
                sb.AppendLine(string.Join(",", new[] { basis, m3.ToString("0.##"), why }.Select(Q)));
        }
        return sb.ToString();
    }

    private static string Q(string s)
    {
        s ??= "";
        bool need = s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0;
        return need ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private async Task ExportCsvAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出班组作业（推演）", SuggestedFileName = (_period.Length > 0 ? _period : "班组作业") + "_班组作业推演.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;
        try
        {
            File.WriteAllText(file.Path.LocalPath, ToCsv(_res), new System.Text.UTF8Encoding(true));
            _msg.Text = "已导出：" + file.Path.LocalPath + "\n（表头带「推演」两个字 —— 这是照排产结果推出来的班表，**不是实绩**，不得回写 production_record / equipment_kpi_monthly。）";
        }
        catch (Exception ex) { _msg.Text = "导出失败：" + ex.Message; }
    }
}
