// 忠实移植自原 PitMine3D Modules/TaskLib/Features/WeekPlanWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;
using EquipmentDataContext = PitMine3D.Kylin.Data.EquipmentDataContext;
using WeekPlanTarget = PitMine3D.Kylin.Data.Entities.WeekPlanTarget;

/// <summary>
/// 周计划编制：月→周裂解，本周 7 天逐日采剥 + 有效班。
///
/// <para>
/// 口径全在 <see cref="WeekPlanLink"/>（W1–W8），本窗口只负责显示与翻页。每一格都有出处，判不了就写"—"，不拿常数顶。
/// </para>
/// <para>
/// <b>没有「一键周裂解」这个按钮了</b>：周计划本来就不是一个"生成动作"——它是月计划 ÷ 作业日在这七天上的投影，
/// 打开就已经算好了。真正要落盘的是**日**计划，那件事在「生产任务编制」里做。
/// </para>
/// </summary>
public sealed class WeekPlanWindow : Window
{
    /// <summary>一行（public 供绑定反射）。</summary>
    public sealed class Row
    {
        public string Day { get; set; } = "";
        public FontWeight DayWeight { get; set; } = FontWeight.Normal;
        public string Shifts { get; set; } = "";
        public string PlanLoad { get; set; } = "";
        public string PlanDump { get; set; } = "";
        public string ActualLoad { get; set; } = "";
        public string Attainment { get; set; } = "";
        public string Status { get; set; } = "";
        public string Basis { get; set; } = "";
        public IBrush StatusBrush { get; set; } = Brushes.Gray;
        public IBrush BasisBrush { get; set; } = TaskUi.Muted;
    }

    private static readonly IBrush OkBrush = Frozen(0x16, 0xA3, 0x4A);
    private static readonly IBrush WarnBrush = Frozen(0xD9, 0x77, 0x06);
    private static readonly IBrush BadBrush = Frozen(0xDC, 0x26, 0x26);
    private static readonly IBrush DimBrush = Frozen(0x8A, 0x8A, 0x8A);
    private static readonly IBrush RunBrush = Frozen(0x0C, 0x44, 0x7C);

    /// <summary>当前显示的那一周里的锚点日（翻页时 ±7 天）。</summary>
    private DateTime _anchor;

    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock headerText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBox coalBox = new() { Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox stripBox = new() { Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock weekTargetText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DataGrid grid = TaskUi.Grid(single: true);
    private readonly TextBlock sumText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock noteText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    public WeekPlanWindow()
    {
        Title = "周计划编制 — 日常生产组织";
        TaskUi.Place(this, 1180, 640);
        _anchor = ProjectScope.WorkDate;

        var header = TaskUi.Header("周计划编制", "月→周裂解：本周 7 天逐日采剥 + 有效班（除数与装箱同一份作业日口径）");

        // 工具条：周翻页
        var tool = new DockPanel { LastChildFill = true };
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); tool.Children.Add(c); }
        var prev = TaskUi.Btn("‹ 上一周", () => { _anchor = _anchor.AddDays(-7); Fill(); }, 72); prev.Margin = new Thickness(0, 0, 4, 0); L(prev);
        var cur = TaskUi.Btn("本周", () => { _anchor = ProjectScope.WorkDate; Fill(); }, 56); cur.Margin = new Thickness(0, 0, 4, 0); ToolTip.SetTip(cur, "回到当前作业日所在的那一周"); L(cur);
        var next = TaskUi.Btn("下一周 ›", () => { _anchor = _anchor.AddDays(+7); Fill(); }, 72); next.Margin = new Thickness(0, 0, 14, 0); L(next);
        var refresh = TaskUi.Btn("刷新", OnRefresh, 56); refresh.Margin = new Thickness(0, 0, 14, 0); ToolTip.SetTip(refresh, "重读班次日历 / 月计划 / 实绩，并重算当日盘子"); L(refresh);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        toolStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = toolStatus });
        tool.Children.Add(toolStatus);

        // 抬头：本周区间 + 月计划 + 作业日口径
        TaskUi.Theme(headerText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        var headBar = TaskUi.Bar(headerText, top: true, padY: 8);

        // 周目标下达（V049 week_plan_target）：月→周是人给的，本行就是那个输入口
        var target = new StackPanel();
        var tRow = new StackPanel { Orientation = Orientation.Horizontal };
        var tl = TaskUi.Text("本周目标", 13, false, true); tl.VerticalAlignment = VerticalAlignment.Center; tl.Margin = new Thickness(0, 0, 12, 0); tRow.Children.Add(tl);
        tRow.Children.Add(Lbl("采出", 0, 4));
        ToolTip.SetTip(coalBox, "本周计划采出，万吨。与月计划同一口径同一单位，四周之和可与月计划对账"); tRow.Children.Add(coalBox);
        tRow.Children.Add(Lbl("万t", 4, 16));
        tRow.Children.Add(Lbl("剥离", 0, 4));
        ToolTip.SetTip(stripBox, "本周计划剥离，万立方米（原位实方）"); tRow.Children.Add(stripBox);
        tRow.Children.Add(Lbl("万m³", 4, 16));
        var save = TaskUi.Btn("下达本周目标", OnSaveWeekTarget, 104); save.Margin = new Thickness(0, 0, 6, 0);
        ToolTip.SetTip(save, "存进周计划目标台账。存完之后当日目标改由「周剩余量 × 今天的能力权重」拆出来，不再走月量÷作业日"); tRow.Children.Add(save);
        var fill = TaskUi.Btn("按月摊算填入", OnFillFromMonth, 96); fill.Margin = new Thickness(0, 0, 6, 0);
        ToolTip.SetTip(fill, "用月计划 ÷ 当月作业日 × 本周作业日算一个建议值填进上面两个框（只是填，不下达）"); tRow.Children.Add(fill);
        var clear = TaskUi.Btn("撤销本周目标", OnClearWeekTarget, 96); clear.Margin = new Thickness(0);
        ToolTip.SetTip(clear, "撤掉之后当日目标退回「月量 ÷ 作业日」那条老路"); tRow.Children.Add(clear);
        target.Children.Add(tRow);
        TaskUi.Theme(weekTargetText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        target.Children.Add(weekTargetText);
        var targetBar = TaskUi.Bar(target, top: true, padY: 8);

        grid.Columns.Add(TaskUi.StyledCol<Row>("日期", nameof(Row.Day), 110, boldPath: nameof(Row.DayWeight), editable: false));
        grid.Columns.Add(TaskUi.TextCol("有效班", nameof(Row.Shifts), 64));
        grid.Columns.Add(Star("计划采装(m³)", nameof(Row.PlanLoad), 1));
        grid.Columns.Add(Star("计划排土(m³)", nameof(Row.PlanDump), 1));
        grid.Columns.Add(Star("实绩采装(m³)", nameof(Row.ActualLoad), 1));
        grid.Columns.Add(TaskUi.TextCol("达成度", nameof(Row.Attainment), 76));
        grid.Columns.Add(TaskUi.StyledCol<Row>("状态", nameof(Row.Status), 104, brushPath: nameof(Row.StatusBrush), editable: false));
        var basis = TaskUi.StyledCol<Row>("计划量来源", nameof(Row.Basis), 200, tipPath: nameof(Row.Basis), brushPath: nameof(Row.BasisBrush), editable: false);
        basis.Width = new DataGridLength(1.6, DataGridLengthUnitType.Star);
        grid.Columns.Add(basis);

        // 本周合计
        TaskUi.Theme(sumText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        var sumBox = new Border { Margin = new Thickness(10, 0, 10, 8), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = sumText };
        TaskUi.Theme(sumBox, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(sumBox, Border.BorderBrushProperty, "Theme.Surface.Border");

        // 口径提示
        TaskUi.Theme(noteText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var noteBar = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = noteText };
        TaskUi.Theme(noteBar, Border.BorderBrushProperty, "Theme.Surface.Border");

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(headBar, 2); Grid.SetRow(targetBar, 3); Grid.SetRow(grid, 4); Grid.SetRow(sumBox, 5); Grid.SetRow(noteBar, 6);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(headBar); g.Children.Add(targetBar); g.Children.Add(grid); g.Children.Add(sumBox); g.Children.Add(noteBar);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        Opened += (_, _) => Fill();
    }

    private static TextBlock Lbl(string t, double left, double right)
    {
        var l = TaskUi.Lbl(t); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(left, 0, right, 0);
        return l;
    }

    private static DataGridTextColumn Star(string header, string path, double star)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path), Width = new DataGridLength(star, DataGridLengthUnitType.Star) };

    private void OnRefresh()
    {
        ProductionPlanContext.Invalidate();   // 班次日历/月计划可能刚被别的窗口改过
        Fill();
    }

    private void Fill()
    {
        WeekPlanResult res;
        try { res = WeekPlanLink.Build(_anchor); }
        catch (Exception ex)
        {
            grid.ItemsSource = null;
            headerText.Text = "";
            sumText.Text = "";
            noteText.Text = "";
            toolStatus.Text = $"周裂解失败：{Short(ex)}";
            return;
        }

        grid.ItemsSource = res.Days.Select(ToRow).ToList();
        headerText.Text = res.Header;

        // 合计：判不了的那几天不计入，且必须说出来（合计看着小不是产量低，是没数）
        string att = res.PlanLoadSumM3 > 1e-6 && res.ActualLoadSumM3 > 1e-6
            ? $"　·　周达成度 {res.ActualLoadSumM3 / res.PlanLoadSumM3 * 100:0.#}%"
            : "";
        sumText.Text =
            $"本周合计：计划采装 {res.PlanLoadSumM3 / 1e4:0.00} 万m³　·　计划排土 {res.PlanDumpSumM3 / 1e4:0.00} 万m³"
            + $"　·　实绩采装 {(res.ActualLoadSumM3 > 1e-6 ? $"{res.ActualLoadSumM3 / 1e4:0.00} 万m³" : "尚无录入")}{att}"
            + $"　|　排班 {res.ScheduledDays}/7 天"
            + (res.MonthSharePct is { } p ? $"　|　占本月计划 {p:0.#}%" : "")
            + (res.UnknownDays > 0 ? $"　|　⚠ {res.UnknownDays} 天判不出计划量，未计入合计" : "");

        noteText.Text = res.Notes.Count > 0
            ? "口径：" + string.Join("　", res.Notes)
            : "口径：日计划 = 月计划 ÷ 本月作业日（与装箱同一个除数）；当日那一行取当日盘子实数；实绩只读 actuals/ 里录过的，没录写「—」不写 0。";

        toolStatus.Text = $"{res.Monday:yyyy-MM-dd} — {res.Sunday:MM-dd}"
                        + (WeekPlanLink.MondayOf(ProjectScope.WorkDate) == res.Monday ? "（当前作业周）" : "")
                        + $"　|　作业日 {ProjectScope.DateLabel}";

        FillWeekTarget(res);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  周目标下达（V049）—— 月→周是人给的，这里是那个输入口
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>当前显示这一周的周一（周目标的键）。</summary>
    private DateTime Monday => WeekPlanLink.MondayOf(_anchor);

    private void FillWeekTarget(WeekPlanResult res)
    {
        WeekPlanTarget? t = null;
        string err = "";
        try { t = EquipmentDataContext.Plan.GetWeek(WeekTargetLink.KeyOf(Monday)); }
        catch (Exception ex) { err = Short(ex); }

        coalBox.Text = t != null ? t.TargetCoalWanT.ToString("0.##") : "";
        stripBox.Text = t != null ? t.TargetStripWanM3.ToString("0.##") : "";

        if (err.Length > 0)
        {
            weekTargetText.Text = $"周目标台账未接通（{err}）—— 现在填了也存不进去。";
            return;
        }

        if (t == null || !t.HasTarget)
        {
            weekTargetText.Text = "本周尚未下达周目标 —— 当日目标走「月计划 ÷ 当月作业日」那条老路。"
                                + "填上两个数并「下达本周目标」，日任务就改按周剩余量拆（已过去的作业日按实绩核销）。";
            return;
        }

        // 下过之后：把「周目标 vs 已按周排出去的量」摆在一起，差多少一眼可见。以周为准（WK7），所以这里不拦月差额，只报出来。
        double coalM3 = t.TargetCoalWanT * 1e4 / CoalDensity();
        double stripM3 = t.TargetStripWanM3 * 1e4;
        double doneLoad = res.ActualLoadSumM3;
        weekTargetText.Text =
            $"已下达（{t.Source}）：采出 {t.TargetCoalWanT:0.##} 万t ≈ {coalM3 / 1e4:0.00} 万m³实方"
            + $"　·　剥离 {t.TargetStripWanM3:0.##} 万m³"
            + $"　|　本周已录实绩采装 {doneLoad / 1e4:0.00} 万m³"
            + (coalM3 > 1e-6 ? $"（占周目标 {doneLoad / coalM3 * 100:0.#}%）" : "")
            + (res.MonthSharePct is { } mp ? $"　|　按月均这一周本应占本月 {mp:0.#}%" : "")
            + "　|　当日目标已改按周剩余量拆分。";
    }

    /// <summary>煤密度：与月→日裂解**同一个数**（ShortTermLink.Density）。</summary>
    private static double CoalDensity() => ShortTermLink.Density();

    private void OnSaveWeekTarget()
    {
        if (!TryNum(coalBox.Text, out double coal) || !TryNum(stripBox.Text, out double strip))
        {
            weekTargetText.Text = "采出/剥离要填数字（空着按 0）。没填就是没下达，不会按 0 去排。";
            return;
        }
        if (coal <= 1e-9 && strip <= 1e-9)
        {
            weekTargetText.Text = "两条腿都是 0 —— 这等于「本周不干活」。"
                                + "若本意是撤销周目标、退回月路，请点「撤销本周目标」。";
            return;
        }

        try
        {
            EquipmentDataContext.Plan.UpsertWeek(new WeekPlanTarget
            {
                Monday = WeekTargetLink.KeyOf(Monday),
                TargetCoalWanT = coal,
                TargetStripWanM3 = strip,
                Source = "人工下达",
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            ProductionPlanContext.Invalidate();   // 当日盘子要按新的周目标重装
            Fill();
        }
        catch (Exception ex) { weekTargetText.Text = $"存不进去（{Short(ex)}）—— 周目标没有落库，当日目标仍走老路。"; }
    }

    private void OnClearWeekTarget()
    {
        try
        {
            EquipmentDataContext.Plan.DeleteWeek(WeekTargetLink.KeyOf(Monday));
            ProductionPlanContext.Invalidate();
            Fill();
        }
        catch (Exception ex) { weekTargetText.Text = $"撤不掉（{Short(ex)}）。"; }
    }

    /// <summary>
    /// 按月摊算一个建议值填进输入框（<b>只填不下达</b>）。
    /// <para>算法：月计划 ÷ 当月作业日 × 本周作业日 —— 与老路同一个除数，所以"照着填"等价于不改变现状，人再在这个基础上加减。</para>
    /// </summary>
    private void OnFillFromMonth()
    {
        WeekPlanResult res;
        try { res = WeekPlanLink.Build(_anchor); }
        catch (Exception ex) { weekTargetText.Text = $"算不出建议值（{Short(ex)}）。"; return; }

        double coalM3 = res.PlanLoadSumM3, stripM3 = res.PlanDumpSumM3;
        if (coalM3 <= 1e-6 && stripM3 <= 1e-6)
        {
            weekTargetText.Text = "这一周按月均摊出来是 0 —— 多半是没有确定的月计划，或本周一天班都没排。"
                                + "此时没有建议值可填，请直接按调度意图填。";
            return;
        }

        coalBox.Text = (coalM3 * CoalDensity() / 1e4).ToString("0.##");   // m³实方 → t → 万t
        stripBox.Text = (stripM3 / 1e4).ToString("0.##");
        weekTargetText.Text = "已按「月计划 ÷ 当月作业日 × 本周作业日」填入建议值 —— **还没有下达**，"
                            + "改成本周真正要干的数以后再点「下达本周目标」。";
    }

    private static bool TryNum(string? s, out double v)
    {
        v = 0;
        s = (s ?? "").Trim();
        if (s.Length == 0) return true;                  // 空 = 0
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out v) && v >= 0;
    }

    private static Row ToRow(WeekDayRow d)
    {
        return new Row
        {
            Day = d.DayLabel + (d.IsAnchor ? " ●" : ""),
            DayWeight = d.IsAnchor ? FontWeight.Bold : FontWeight.Normal,
            Shifts = d.Shifts?.ToString() ?? "—",
            PlanLoad = Num(d.PlanLoadM3),
            PlanDump = Num(d.PlanDumpM3),
            ActualLoad = Num(d.ActualLoadM3),
            Attainment = d.AttainmentPct is { } a ? $"{a:0}%" : "—",
            Status = d.Status,
            Basis = d.Basis,
            StatusBrush = d.Status switch
            {
                "已完成" => OkBrush,
                "部分完成" => WarnBrush,
                "无实绩录入" => BadBrush,
                "执行中" or "已录实绩" or "已录实绩(日期未到)" => RunBrush,
                _ => DimBrush,
            },
        };
    }

    /// <summary>null = 判不了/没录 → "—"；0 是真 0（未排班那天）。</summary>
    private static string Num(double? v) => v.HasValue ? $"{v.Value:N0}" : "—";

    private static IBrush Frozen(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
