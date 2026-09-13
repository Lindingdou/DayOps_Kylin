// 忠实移植自原 PitMine3D Modules/TaskLib/Features/BlastPlanWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data.Entities;     // DrillPlan
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;
using EquipmentDataContext = PitMine3D.Kylin.Data.EquipmentDataContext;   // 穿孔计划读写
using BlastPlanLink = PitMine3D.Kylin.TaskLib.Engine.BlastPlanLink;       // 与 Kylin 旧切片 Data.BlastPlanLink 消歧

/// <summary>
/// 钻爆计划衔接：逐炮排程 → 停产清场窗口 → 衔接采装面。钻爆设计在专业模块。
///
/// <para>
/// 口径全在 <see cref="BlastPlanLink"/>（B1–B7），本窗口只负责显示与翻页。读的是 blast_event 真表，
/// 火工品四个量（孔数/延米/装药/方量）逐炮列出并合计——那正是任务编制要交给火工品计划的数。
/// </para>
/// <para>
/// <b>本窗口最要紧的一列是「进装箱时窗」</b>：引擎的停产窗口是一对标量，只吃最早一炮，
/// 第二炮起在计划里根本不存在（那些时段计划仍在满负荷作业）。这件事以前没人看得见。
/// </para>
/// </summary>
public sealed class BlastPlanWindow : Window
{
    /// <summary>一行（public 供绑定反射）。</summary>
    public sealed class Row
    {
        public string Seq { get; set; } = "";
        public string Time { get; set; } = "";
        public string Location { get; set; } = "";
        public string Material { get; set; } = "";
        /// <summary>台账记的钻机（blast_event.drill_id）——事后填的事实。</summary>
        public string DrillId { get; set; } = "";
        /// <summary>本区当日的穿孔计划（drill_plan）——事前排的计划。两者不是一回事，不许合成一列。</summary>
        public string DrillPlanText { get; set; } = "";
        public IBrush DrillBrush { get; set; } = Brushes.Gray;
        /// <summary>孔数 / 延米 合成一格（横向预算让给右侧三列）。</summary>
        public string HolesMeters { get; set; } = "";
        /// <summary>装药 kg（单耗）合成一格。</summary>
        public string ExplosiveUnit { get; set; } = "";
        public string Volume { get; set; } = "";
        public string Stop { get; set; } = "";
        public FontWeight StopWeight { get; set; } = FontWeight.Normal;
        public string ShiftText { get; set; } = "";
        public string Face { get; set; } = "";
        public string Status { get; set; } = "";
        /// <summary>格子里显示的短文案。</summary>
        public string NoteShort { get; set; } = "";
        /// <summary>整句（ToolTip）。</summary>
        public string Note { get; set; } = "";
        public IBrush FaceBrush { get; set; } = Brushes.Gray;
        public IBrush StatusBrush { get; set; } = Brushes.Gray;
        public IBrush NoteBrush { get; set; } = Brushes.Gray;
        public string Drill { get; set; } = "";
    }

    private static readonly IBrush OkBrush = Frozen(0x16, 0xA3, 0x4A);
    private static readonly IBrush WarnBrush = Frozen(0xD9, 0x77, 0x06);
    private static readonly IBrush BadBrush = Frozen(0xDC, 0x26, 0x26);
    private static readonly IBrush DimBrush = Frozen(0x8A, 0x8A, 0x8A);
    private static readonly IBrush TextBrush = Frozen(0x30, 0x30, 0x30);

    private DateTime _date;

    private readonly TextBlock dateText = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold, MinWidth = 120, TextAlignment = TextAlignment.Center };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock headerText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DataGrid grid = TaskUi.Grid(single: true);
    private readonly TextBlock drillStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly DataGrid drillGrid = TaskUi.Grid(readOnly: false, single: true);
    private readonly TextBlock noteText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    public BlastPlanWindow()
    {
        Title = "钻爆计划衔接 — 日常生产组织";
        TaskUi.Place(this, 1280, 700);
        _date = ProjectScope.WorkDate;

        var header = TaskUi.Header("钻爆计划衔接", "逐炮排程（blast_event）→ 停产清场窗口 → 衔接采装面（钻爆设计在专业模块）");

        var tool = new DockPanel { LastChildFill = true };
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); tool.Children.Add(c); }
        var prev = TaskUi.Btn("‹", () => { _date = _date.AddDays(-1); Fill(); }, 30); prev.Margin = new Thickness(0, 0, 4, 0); L(prev);
        TaskUi.Theme(dateText, TextBlock.ForegroundProperty, "Theme.Text.Body"); L(dateText);
        var next = TaskUi.Btn("›", () => { _date = _date.AddDays(+1); Fill(); }, 30); next.Margin = new Thickness(4, 0, 10, 0); L(next);
        var today = TaskUi.Btn("作业日", () => { _date = ProjectScope.WorkDate; Fill(); }, 62); today.Margin = new Thickness(0, 0, 14, 0); ToolTip.SetTip(today, "回到当前作业日"); L(today);
        var refresh = TaskUi.Btn("刷新", OnRefresh, 56); refresh.Margin = new Thickness(0, 0, 14, 0); L(refresh);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        toolStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = toolStatus });
        tool.Children.Add(toolStatus);

        TaskUi.Theme(headerText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        var headBar = TaskUi.Bar(headerText, top: true, padY: 8);

        // 逐炮表：火工品五个量压成两列（孔数/延米、装药+单耗）
        var c = grid.Columns;
        c.Add(TaskUi.TextCol("炮次", nameof(Row.Seq), 46));
        c.Add(TaskUi.TextCol("爆破时刻", nameof(Row.Time), 70));
        c.Add(TaskUi.TextCol("平盘/爆区", nameof(Row.Location), 100));
        c.Add(TaskUi.TextCol("物料", nameof(Row.Material), 80));
        c.Add(TaskUi.TextCol("钻机", nameof(Row.DrillId), 62));
        c.Add(TaskUi.TextCol("孔数/延米", nameof(Row.HolesMeters), 88));
        c.Add(TaskUi.TextCol("装药kg(单耗)", nameof(Row.ExplosiveUnit), 104));
        c.Add(TaskUi.TextCol("方量(m³)", nameof(Row.Volume), 80));
        c.Add(TaskUi.StyledCol<Row>("穿孔计划", nameof(Row.DrillPlanText), 120, tipPath: nameof(Row.Drill), brushPath: nameof(Row.DrillBrush), editable: false));
        c.Add(TaskUi.StyledCol<Row>("停产清场", nameof(Row.Stop), 94, tipPath: nameof(Row.ShiftText), boldPath: nameof(Row.StopWeight), editable: false));
        var face = TaskUi.StyledCol<Row>("衔接采装面", nameof(Row.Face), 140, tipPath: nameof(Row.Face), brushPath: nameof(Row.FaceBrush), editable: false); face.Width = new DataGridLength(1, DataGridLengthUnitType.Star); face.MinWidth = 140; c.Add(face);
        c.Add(TaskUi.StyledCol<Row>("状态", nameof(Row.Status), 82, tipPath: nameof(Row.ShiftText), brushPath: nameof(Row.StatusBrush), editable: false));
        var note = TaskUi.StyledCol<Row>("进装箱时窗", nameof(Row.NoteShort), 140, tipPath: nameof(Row.Note), brushPath: nameof(Row.NoteBrush), editable: false); note.Width = new DataGridLength(1, DataGridLengthUnitType.Star); note.MinWidth = 140; c.Add(note);
        grid.Margin = new Thickness(10, 10, 10, 4);

        // 穿孔作业计划（V044 drill_plan）：表建在这儿而不是另开一个窗 —— 工序链的两端本来就要对着看
        var drillBar = new DockPanel();
        void D(Control x) { DockPanel.SetDock(x, Avalonia.Controls.Dock.Left); drillBar.Children.Add(x); }
        var dl = TaskUi.Text("穿孔作业计划", 13, false, true); dl.VerticalAlignment = VerticalAlignment.Center; dl.Margin = new Thickness(0, 0, 10, 0); D(dl);
        var addD = TaskUi.Btn("新增一条", OnAddDrill, 76); addD.Margin = new Thickness(0, 0, 6, 0); ToolTip.SetTip(addD, "按本日 + 当前班次起点新建一条，改完点「保存穿孔计划」写库"); D(addD);
        var delD = TaskUi.Btn("删除选中", OnDeleteDrill, 76); delD.Margin = new Thickness(0, 0, 6, 0); D(delD);
        var saveD = TaskUi.Btn("保存穿孔计划", OnSaveDrills, 100, bold: true); saveD.Margin = new Thickness(0); D(saveD);
        var genD = TaskUi.Btn("按本期计划生成", OnGenerateDrills, 112); genD.Margin = new Thickness(10, 0, 0, 0);
        ToolTip.SetTip(genD, "从本期【班组计划】里的穿孔笔生成 —— 谁/何时/多少由分解器算，\n在哪儿/什么标高取自【穿孔工序区】。\n⚠ 孔数与延米一律留空（NULL）：孔网参数不在本模块可达范围，\n而「免爆」与「算不出」的延米都是 0，写 0 等于把缺口藏起来。\n先清掉上一版由计划生成的，再写新的（手工录的那些不动）。");
        D(genD);
        TaskUi.Theme(drillStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        drillStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = drillStatus });
        drillBar.Children.Add(drillStatus);
        var drillHead = new Border { Margin = new Thickness(10, 4, 10, 0), Padding = new Thickness(10, 6), BorderThickness = new Thickness(1, 1, 1, 0), CornerRadius = new CornerRadius(6, 6, 0, 0), Child = drillBar };
        TaskUi.Theme(drillHead, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(drillHead, Border.BorderBrushProperty, "Theme.Surface.Border");

        var dc = drillGrid.Columns;
        dc.Add(Edit("钻机", nameof(DrillRow.EquipId), 90));
        dc.Add(Edit("待爆区/平盘", nameof(DrillRow.Zone), 150));
        dc.Add(Edit("起 HH:mm", nameof(DrillRow.Start), 82));
        dc.Add(Edit("止 HH:mm", nameof(DrillRow.End), 82));
        // 班次只读：排的时候看不见班次很容易把一条活排在交接线上
        dc.Add(TaskUi.TextCol("班次", nameof(DrillRow.Shift), 64));
        dc.Add(Edit("台阶(m)", nameof(DrillRow.Bench), 78));
        dc.Add(Edit("孔数", nameof(DrillRow.Holes), 62));
        dc.Add(Edit("延米(m)", nameof(DrillRow.Meters), 78));
        var st = new DataGridTemplateColumn { Header = TaskUi.Head("状态"), Width = new DataGridLength(86) };
        st.CellTemplate = new FuncDataTemplate<DrillRow>((_, _) =>
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            TaskUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Body");
            tb.Bind(TextBlock.TextProperty, new Binding(nameof(DrillRow.Status)));
            return tb;
        });
        st.CellEditingTemplate = new FuncDataTemplate<DrillRow>((_, _) =>
        {
            var cb = new ComboBox { ItemsSource = new[] { "计划", "进行中", "完成", "取消" }, MinWidth = 80 };
            cb.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(DrillRow.Status)) { Mode = BindingMode.TwoWay });
            return cb;
        });
        dc.Add(st);
        var noteCol = Edit("备注", nameof(DrillRow.Note), 120); noteCol.Width = new DataGridLength(1, DataGridLengthUnitType.Star); noteCol.MinWidth = 120; dc.Add(noteCol);
        drillGrid.Margin = new Thickness(10, 0, 10, 10);
        drillGrid.ItemsSource = _drills;

        var mid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,170") };
        mid.RowDefinitions[0].MinHeight = 120; mid.RowDefinitions[2].MinHeight = 90;
        Grid.SetRow(grid, 0); Grid.SetRow(drillHead, 1); Grid.SetRow(drillGrid, 2);
        mid.Children.Add(grid); mid.Children.Add(drillHead); mid.Children.Add(drillGrid);

        TaskUi.Theme(noteText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = noteText };
        TaskUi.Theme(foot, Border.BorderBrushProperty, "Theme.Surface.Border");

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(headBar, 2); Grid.SetRow(mid, 3); Grid.SetRow(foot, 4);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(headBar); g.Children.Add(mid); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        Opened += (_, _) => Fill();
    }

    private static DataGridTextColumn Edit(string header, string path, double width)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(width) };

    private void OnRefresh()
    {
        ProductionPlanContext.Invalidate();   // 爆破台账可能刚被改过，停产窗口跟着变
        Fill();
    }

    private void Fill()
    {
        dateText.Text = _date.ToString("yyyy-MM-dd");

        BlastPlanResult res;
        try { res = BlastPlanLink.Build(_date); }
        catch (Exception ex)
        {
            grid.ItemsSource = null;
            headerText.Text = "";
            noteText.Text = "";
            toolStatus.Text = $"钻爆排程失败：{Short(ex)}";
            return;
        }

        grid.ItemsSource = res.Shots.Select(ToRow).ToList();
        headerText.Text = res.Header;
        noteText.Text = res.Notes.Count > 0
            ? string.Join("　", res.Notes)
            : "采装/排土作业止于爆破前，清场解除后恢复；本表的停产窗口与装箱扣掉的是同一个常数（"
              + $"清场 {BlastPlanLink.ClearanceH * 60:0} 分钟）。";

        toolStatus.Text = (res.FromLedger
                            ? $"爆破台账 {res.Shots.Count} 炮"
                            : "爆破台账本日无记录（表内为引擎兜底窗口）")
                        + (_date == ProjectScope.WorkDate ? "　|　当前作业日" : $"　|　作业日为 {ProjectScope.DateLabel}")
                        + (res.Unlinked > 0 ? $"　|　{res.Unlinked} 炮未对上作业面" : "");

        FillDrills();
    }

    private static Row ToRow(BlastShotRow s)
    {
        bool notInWindow = s.Hour.HasValue && !s.DrivesEngine;
        return new Row
        {
            Seq = s.Seq?.ToString() ?? "—",
            Time = s.TimeText,
            Location = s.Location.Length > 0 ? s.Location : "—",
            Material = s.MaterialText.Length > 0 ? s.MaterialText : "—",
            DrillId = s.DrillId.Length > 0 ? s.DrillId : "—",
            DrillPlanText = s.DrillText,
            Drill = s.DrillText,
            DrillBrush = s.HasDrill ? TextBrush : WarnBrush,
            HolesMeters = Pair(Num(s.HoleCount), Num(s.HoleMeters, "N0")),
            ExplosiveUnit = s.UnitKgM3 is > 1e-9
                ? $"{Num(s.ExplosiveKg, "N0")}（{s.UnitKgM3.Value:0.###}）"
                : Num(s.ExplosiveKg, "N0"),
            Volume = Num(s.VolumeM3, "N0"),
            Stop = s.StopWindow,
            StopWeight = s.DrivesEngine ? FontWeight.Bold : FontWeight.Normal,
            ShiftText = s.Shift.Length > 0 ? $"落在 {s.Shift}" : "判不出落在哪个班",
            Face = s.LinkedFace,
            Status = s.Status,
            NoteShort = s.DrivesEngine ? "✓ 已进（按它扣停产）"
                      : notInWindow ? "⚠ 未进（只扣最早一炮）"
                      : s.Note.Length > 0 ? "⚠ 排不进时窗" : "—",
            Note = s.DrivesEngine ? "装箱按这一炮扣停产：采装/排土作业止于爆破前，清场解除后恢复。"
                 : s.Note.Length > 0 ? s.Note : "—",
            FaceBrush = s.Linked ? TextBrush : WarnBrush,
            StatusBrush = s.Status.StartsWith("待爆", StringComparison.Ordinal) ? WarnBrush
                        : s.Status == "清场中" ? BadBrush
                        : s.Status == "已爆" ? OkBrush
                        : DimBrush,
            NoteBrush = s.DrivesEngine ? OkBrush : notInWindow ? BadBrush : DimBrush,
        };
    }

    // ═══════════════════ 穿孔作业计划（V044 drill_plan）═══════════════════
    //  录入一律走文本：起止是 HH:mm，台阶/孔数/延米**留空 = 未录**，不拿 0 冒充——0 是合法标高，也是合法孔数。

    /// <summary>穿孔计划一行（全部可编辑，故用可写属性）。</summary>
    public sealed class DrillRow
    {
        public string EquipId { get; set; } = "";
        public string Zone { get; set; } = "";
        public string Start { get; set; } = "";
        public string End { get; set; } = "";

        /// <summary>这条钻孔排在哪个班（由起始时刻按当日班制推，只读）。</summary>
        public string Shift => ShiftOfText(Start);
        public string Bench { get; set; } = "";
        public string Holes { get; set; } = "";
        public string Meters { get; set; } = "";
        public string Status { get; set; } = "计划";
        public string Note { get; set; } = "";

        /// <summary>库里那一行的主键（改了钻机/起始时刻要先删旧键再写新键）。null = 本行还没入库。</summary>
        public string? KeyEquip;
        public string? KeyStart;

        /// <summary>"HH:mm" → 班次名；解析不出返回空串（不猜一个班）。</summary>
        private static string ShiftOfText(string? hhmm)
        {
            string s = (hhmm ?? "").Trim();
            int i = s.IndexOf(':');
            if (i <= 0 || !int.TryParse(s[..i], out int h)) return "";
            int m = 0;
            if (i + 1 < s.Length) int.TryParse(s[(i + 1)..], out m);
            return ShiftScope.ShiftOf(h + m / 60.0);
        }
    }

    private readonly ObservableCollection<DrillRow> _drills = new();

    private void FillDrills()
    {
        _drills.Clear();

        List<DrillPlan> rows;
        try { rows = EquipmentDataContext.DrillPlans.ByDate(_date).Where(r => r != null).ToList(); }
        catch (Exception ex)
        {
            drillStatus.Text = $"穿孔计划表读不到（{Short(ex)}）—— GeoDataBase 未就绪时排了也存不进去";
            return;
        }

        foreach (var r in rows)
            _drills.Add(new DrillRow
            {
                EquipId = (r.EquipmentId ?? "").Trim(),
                Zone = (r.Zone ?? "").Trim(),
                Start = (r.StartTime ?? "").Trim(),
                End = (r.EndTime ?? "").Trim(),
                Bench = r.BenchElevationM?.ToString("0.##") ?? "",
                Holes = r.HoleCount?.ToString() ?? "",
                Meters = r.HoleLengthM?.ToString("0.##") ?? "",
                Status = string.IsNullOrWhiteSpace(r.Status) ? "计划" : r.Status.Trim(),
                Note = r.Note ?? "",
                KeyEquip = (r.EquipmentId ?? "").Trim(),
                KeyStart = (r.StartTime ?? "").Trim(),
            });

        drillStatus.Text = _drills.Count == 0
            ? $"{_date:MM-dd} 未排穿孔 —— 排一条，钻机才会进甘特与工序进度（起止 HH:mm；台阶/孔数留空=未录）"
            : $"{_date:MM-dd} 共 {_drills.Count} 条穿孔计划";
    }

    private void OnAddDrill()
    {
        // 起点默认落在**当前班**的班首：多数穿孔就是整班作业，改起止比从空白填起省事。
        var cur = ShiftScope.Window(ShiftScope.Current) ?? ShiftScope.Windows.FirstOrDefault();
        double start = cur?.Start ?? 0;
        double end = cur?.End ?? 8;

        _drills.Add(new DrillRow
        {
            EquipId = "", Zone = "", Start = Hm(start), End = Hm(end), Status = "计划",
        });
        drillGrid.SelectedIndex = _drills.Count - 1;
        drillStatus.Text = "已加一行：填钻机编号与待爆区（待爆区要与爆破台账的平盘编码对得上，否则衔接不上），再点「保存穿孔计划」";
    }

    private void OnDeleteDrill()
    {
        if (drillGrid.SelectedItem is not DrillRow row) { drillStatus.Text = "先选中要删的那一行"; return; }

        if (row.KeyEquip != null && row.KeyStart != null)
        {
            try { EquipmentDataContext.DrillPlans.Delete(row.KeyEquip, _date, row.KeyStart); }
            catch (Exception ex) { drillStatus.Text = $"删除失败（{Short(ex)}）"; return; }
        }
        _drills.Remove(row);
        drillStatus.Text = $"已删除 {row.EquipId} {row.Start}–{row.End}";
        ProductionPlanContext.Invalidate();
        Fill();
    }

    /// <summary>
    /// 按本期班组计划生成穿孔计划。<b>只有这一条写入路径</b>：分解器是当日盘子的主路径，由它写。
    /// </summary>
    private async void OnGenerateDrills()
    {
        string period;
        try { period = ProjectScope.WorkDate.ToString("yyyy-MM", CultureInfo.InvariantCulture); }
        catch { period = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture); }

        ShiftPlanAssembler.Assembled asm;
        var old = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try { asm = ShiftPlanAssembler.Build(period, ProductionPlanContext.Config()); }
        catch (Exception ex)
        {
            Cursor = old;
            await TaskUi.Info(this, "按本期计划生成穿孔计划", $"分解本期班组计划失败（{ex.GetType().Name}：{ex.Message}）");
            return;
        }
        finally { Cursor = old; }

        // 先试算给人看一眼，再问要不要写 —— 这张表下游是钻爆衔接与工序进度，一键覆盖不是可接受的默认行为。
        var dry = DrillPlanSync.Sync(period, asm.Plan, dryRun: true);
        if (!dry.Ok)
        {
            await TaskUi.Info(this, "按本期计划生成穿孔计划", dry.Headline + "\n\n" + string.Join("\n", dry.Notes));
            return;
        }

        if (!await TaskUi.Confirm(this, "按本期计划生成穿孔计划",
                dry.Headline + "\n\n" + string.Join("\n\n", dry.Notes)
              + "\n\n先清掉上一版【由班组计划生成】的那些，再写新的；**手工录的不动**。要写吗？")) return;

        var done = DrillPlanSync.Sync(period, asm.Plan);
        await TaskUi.Info(this, "按本期计划生成穿孔计划", done.Headline + "\n\n" + string.Join("\n\n", done.Notes));
        Fill();
    }

    private void OnSaveDrills()
    {
        drillGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        drillGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var bad = new List<string>();
        int saved = 0;

        foreach (var row in _drills.ToList())
        {
            string equip = (row.EquipId ?? "").Trim();
            if (equip.Length == 0) { bad.Add("有一行没填钻机编号"); continue; }

            double? s = ParseHm(row.Start), en = ParseHm(row.End);
            if (s == null) { bad.Add($"{equip} 起始时刻「{row.Start}」认不出（写 HH:mm）"); continue; }
            if (en == null) { bad.Add($"{equip} 结束时刻「{row.End}」认不出（写 HH:mm）"); continue; }
            if (en <= s) { bad.Add($"{equip} 结束不晚于开始（跨零点请拆成两条）"); continue; }

            var ent = new DrillPlan
            {
                EquipmentId = equip,
                PlanDate = _date.ToString("yyyy-MM-dd"),
                StartTime = Hm(s.Value),
                EndTime = Hm(en.Value),
                Zone = (row.Zone ?? "").Trim(),
                BenchElevationM = ParseOpt(row.Bench),
                HoleCount = ParseOpt(row.Holes) is { } hc ? (int)Math.Round(hc) : null,
                HoleLengthM = ParseOpt(row.Meters),
                Status = string.IsNullOrWhiteSpace(row.Status) ? "计划" : row.Status.Trim(),
                Note = string.IsNullOrWhiteSpace(row.Note) ? null : row.Note.Trim(),
            };

            try
            {
                // 主键是 (钻机, 日期, 起始时刻)：这两样改过就要先删旧行，否则库里会留一条孤儿
                if (row.KeyEquip != null && row.KeyStart != null
                    && (!string.Equals(row.KeyEquip, ent.EquipmentId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(row.KeyStart, ent.StartTime, StringComparison.Ordinal)))
                    EquipmentDataContext.DrillPlans.Delete(row.KeyEquip, _date, row.KeyStart);

                EquipmentDataContext.DrillPlans.Upsert(ent);
                row.KeyEquip = ent.EquipmentId;
                row.KeyStart = ent.StartTime;
                saved++;
            }
            catch (Exception ex) { bad.Add($"{equip} 写库失败（{Short(ex)}）"); }
        }

        drillStatus.Text = bad.Count > 0
            ? $"已保存 {saved} 条；{bad.Count} 条没存：{string.Join("；", bad.Take(2))}"
            : $"已保存 {saved} 条穿孔计划 —— 盘子已作废重算，钻机任务立刻进甘特与工序进度";

        ProductionPlanContext.Invalidate();   // 穿孔进的是当日盘子，存完必须重算
        Fill();
    }

    /// <summary>"07:30" / "7.5" → 小时数；空或认不出返回 null。</summary>
    private static double? ParseHm(string? s)
    {
        string v = (s ?? "").Trim();
        if (v.Length == 0) return null;
        if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var ts) && ts.TotalHours is >= 0 and <= 24)
            return Math.Round(ts.TotalHours, 3);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h is >= 0 and <= 24)
            return h;
        return null;
    }

    /// <summary>留空 = 未录（null），不拿 0 冒充。</summary>
    private static double? ParseOpt(string? s)
        => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static string Hm(double hour)
    {
        int h = (int)Math.Floor(hour);
        int m = (int)Math.Round((hour - h) * 60);
        if (m >= 60) { h++; m -= 60; }
        if (h >= 24) { h = 23; m = 59; }
        return $"{h:00}:{m:00}";
    }

    private static string Num(int? v) => v.HasValue ? v.Value.ToString("N0") : "—";
    private static string Num(double? v, string fmt) => v is > 1e-9 ? v.Value.ToString(fmt) : "—";

    /// <summary>"42 / 520"；两侧都没有就一个破折号（不写 "— / —"）。</summary>
    private static string Pair(string a, string b) => a == "—" && b == "—" ? "—" : $"{a} / {b}";

    private static IBrush Frozen(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
