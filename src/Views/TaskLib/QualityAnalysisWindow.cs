// 忠实移植自原 PitMine3D Modules/TaskLib/Features/QualityAnalysisWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 质量·配煤分析：各面实测 vs 目标(灰/热/硫) 达标 + 综合配煤入仓。取引擎任务的质量回灌。
///
/// <para>
/// <b>按班组织</b>：配煤是按班入仓的——一个班掺得好、另一个班掺砸了，全天平均下来可能刚好达标，
/// 而实际入仓的是两批不同的煤。默认只看当前班。
/// </para>
/// </summary>
public sealed class QualityAnalysisWindow : Window
{
    public sealed class Row
    {
        public string Zone { get; set; } = "";
        public string Shift { get; set; } = "";
        public string AshT { get; set; } = "";
        public string AshA { get; set; } = "";
        public string CvT { get; set; } = "";
        public string CvA { get; set; } = "";
        public string ST { get; set; } = "";
        public string SA { get; set; } = "";
        public string Ok { get; set; } = "";
        /// <summary>单据状态。计划侧不再被下达闸挡掉之后，这一列是必须的。</summary>
        public string Issue { get; set; } = "";
    }

    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid grid = TaskUi.Grid();
    private readonly TextBlock blendNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private bool _loaded;

    public QualityAnalysisWindow()
    {
        Title = "质量·配煤分析 — 日常生产组织";
        TaskUi.Place(this, 1060, 600);

        var tool = new DockPanel { LastChildFill = true };
        var lbl = TaskUi.Lbl("班次"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left);
        // 取值域按当日班制、默认落当前班；「全部」留给日终看全天配煤
        DockPanel.SetDock(shiftCombo, Avalonia.Controls.Dock.Left);
        var refresh = TaskUi.Btn("刷新", Build, 66); refresh.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Left);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        toolStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = toolStatus });
        tool.Children.Add(lbl); tool.Children.Add(shiftCombo); tool.Children.Add(refresh); tool.Children.Add(toolStatus);

        grid.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("作业面"), Binding = new Binding(nameof(Row.Zone)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180 });
        grid.Columns.Add(TaskUi.TextCol("班次", nameof(Row.Shift), 56));
        grid.Columns.Add(TaskUi.TextCol("灰目标%", nameof(Row.AshT), 76));
        grid.Columns.Add(TaskUi.TextCol("灰实测%", nameof(Row.AshA), 76));
        grid.Columns.Add(TaskUi.TextCol("热目标", nameof(Row.CvT), 70));
        grid.Columns.Add(TaskUi.TextCol("热实测", nameof(Row.CvA), 70));
        grid.Columns.Add(TaskUi.TextCol("硫目标%", nameof(Row.ST), 72));
        grid.Columns.Add(TaskUi.TextCol("硫实测%", nameof(Row.SA), 72));
        grid.Columns.Add(TaskUi.TextCol("达标", nameof(Row.Ok), 64));
        // 单据列（EV9）：计划侧不再被下达闸挡掉，逐行看得出这条签没签发。
        // 未下达的行只有目标列有数、实测列一律「—」，且不进达标率分母。
        grid.Columns.Add(TaskUi.TextCol("单据", nameof(Row.Issue), 76));

        TaskUi.Theme(blendNote, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(blendNote, top: false, padY: 8);

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var header = TaskUi.Header("质量·配煤分析", "各面实测 vs 目标(灰/热/硫) 达标 + 综合配煤入仓");
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(grid, 2); Grid.SetRow(foot, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(grid); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) Build(); };
        Opened += (_, _) =>
        {
            ShiftSelector.Bind(shiftCombo, includeAll: true);
            _loaded = true;
            Build();
        };
    }

    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);

    private void Build()
    {
        string shift = ShiftFilter;
        string scope = shift.Length == 0 ? "全天" : ShiftScope.Caption(shift);

        // ★ 计划侧口径（EV9）：未下达的算，已撤回的不算。
        //   配煤目标（灰/热/硫）是排的时候就定下的，与签发与否无关 ——
        //   原先整窗套分母口径，一条没下达时连"这几个面本班要掺成什么样"都看不到了。
        var loads = SampleTaskBoard.Day()
            .Where(DispatchStateLink.CountsForPlan)
            .Where(t => t.Process == ProcessType.Load)
            .Where(t => shift.Length == 0 || t.Shift == shift)
            .ToList();

        // ★ 只列**产煤的**采装面。纯剥离面按定义就没有灰/热/硫，
        //   把它们铺进来只是多出一屏「—」—— 而"没有煤质数据"与"这个面本来就不出煤"
        //   是两件事，混在一列里就分不出来了。判据走物料本体（OreVolumeM3），
        //   不按作业面名字猜（"内排土场1·L76" 这种名字什么也证明不了）。
        var inScope = loads.Where(t => t.OreVolumeM3 > 1e-6 || t.QualityTarget != null).ToList();
        int nonCoal = loads.Count - inScope.Count;

        // ★ 实绩侧（达标率的分母）仍**只算下达过的**（用户 2026-08-20 定）。
        //   口径判据收在 DispatchStateLink 一处，别在各窗写各的 Where。
        var issued = inScope.Where(DispatchStateLink.CountsForEvaluation).ToList();
        int notIssued = inScope.Count - issued.Count;

        var tasks = issued
            .Where(t => t.QualityActual != null && t.QualityTarget != null)
            .OrderBy(t => t.WorkZone).ThenBy(t => t.StartHour)
            .ToList();

        // ★ 表按**计划侧**铺（EV9）：每个采装面都列一行，目标列照出；
        //   实测列只有"已下达且有回灌"的行才有数。原先表只铺有回灌的那几行，
        //   于是没下达 / 没录质量的面在界面上根本不存在 —— 而"哪几个面还没录"
        //   恰恰是这个窗最该回答的问题。
        var rows = inScope
            .OrderBy(t => t.WorkZone).ThenBy(t => t.StartHour)
            .Select(t =>
            {
                bool counted = DispatchStateLink.CountsForEvaluation(t);
                bool hasBoth = counted && t.QualityActual != null && t.QualityTarget != null;
                var tg = t.QualityTarget;
                var ac = hasBoth ? t.QualityActual : null;
                return new Row
                {
                    Zone = t.WorkZone,
                    Shift = t.Shift,
                    AshT = tg != null ? $"{tg.AshPct:0.#}" : "—",
                    AshA = ac != null ? $"{ac.AshPct:0.#}" : "—",
                    CvT = tg != null ? $"{tg.CalorificMJkg:0.#}" : "—",
                    CvA = ac != null ? $"{ac.CalorificMJkg:0.#}" : "—",
                    ST = tg != null ? $"{tg.SulfurPct:0.##}" : "—",
                    SA = ac != null ? $"{ac.SulfurPct:0.##}" : "—",
                    // 「未判」与「不达标」必须分开：没有回灌不是超标。
                    Ok = hasBoth ? (ac!.MeetsTarget(tg!) ? "✓达标" : "✗超标")
                       : counted ? "未录" : "未下达",
                    Issue = t.IsWithdrawn ? "已撤回" : t.IsIssued ? "已下达" : "未下达",
                };
            }).ToList();
        grid.ItemsSource = rows;

        // 达标率的分子分母只数**判得了的那些**（已下达 + 有回灌），与表的行数不是一回事。
        int ok = tasks.Count(t => t.QualityActual!.MeetsTarget(t.QualityTarget!));
        int noQuality = issued.Count - tasks.Count;

        // ★ 空态（2026-08-18 补）：样例不再兜底之后，"没有数据"是常态。
        //   原来这一句在 rows.Count==0 时会写成「达标 0/0（0%）」——
        //   **「0% 达标」与「没有可判的数据」是两件事**，而它们在这一行里长得一样。
        //   而且两种空还要再分开：没有采装任务（计划没排）vs 有面但一条回灌都没有（实绩没录）。
        //   ★ 还有第三种（2026-08-20 补）：排了、但一条都没下达 —— 见 EmptyScopeReason。
        string issueNote = (notIssued > 0
                              ? $"　|　◆ **{notIssued} 个面尚未下达**（目标列照出，不进达标率分母）"
                              : "")
                         + (nonCoal > 0 ? $"　·　另有 {nonCoal} 个纯剥离面不出煤，本窗不列" : "");

        // 没有目标列的煤面 = 煤质目标没接上（钻孔取不到 / 台账没填），
        // 这与"实测没录"是两件事，补法也不同 —— 必须分开说。
        int noTarget = inScope.Count(t => t.QualityTarget == null);

        if (loads.Count == 0)
            // 计划侧也空才是真的没排 —— 这里不会再把"没签发"说成"没排计划"了（EV9）。
            toolStatus.Text = $"{scope}：**本日没有采装任务** —— 不是配煤不达标，是计划还没排出来。"
                            + ProductionPlanContext.SourceLabel;
        else if (inScope.Count == 0)
            toolStatus.Text = $"{scope}：{loads.Count} 个采装面**全是纯剥离面，一个不出煤** —— "
                            + "没有可配的煤，不是配煤不达标。"
                            + "若这不符合现场，去查作业面的物料构成（煤面被判成了岩面）。";
        else if (noTarget == inScope.Count)
            toolStatus.Text = $"{scope}：{inScope.Count} 个产煤面**一个煤质目标都没有** —— "
                            + "**不是超标，是没有目标可比**。煤质目标按位置从钻孔取"
                            + "（SeamQualitySampler：煤层号 + 面的坐标 → 最近钻孔）；"
                            + "取不到通常是煤层号对不上或该面没有源坐标。"
                            + $"　·　配煤标准（灰/热/硫上下限）仍在下方按入仓实绩判。{issueNote}"
                            + "　|　" + SampleTaskBoard.DispatchSourceLabel;
        else if (tasks.Count == 0)
            // ★「0% 达标」与「没有可判的数据」是两件事，而它们在同一行里长得一样。
            //   两种空还要再分：一条都没下达 vs 下达了但一条回灌都没有 —— 补法完全不同。
            toolStatus.Text = $"{scope}：{inScope.Count} 个采装面里**一条可判的质量数据都没有** —— "
                            + "**不是达标率 0%，是没有可判的数据**。"
                            + (issued.Count == 0
                                ? $"原因：{inScope.Count} 条**一条都没下达**，达标率分母只算下达过的。"
                                  + "补法：到「任务下达」签发本班任务，再到「实绩录入」回灌灰/热/硫。"
                                : $"原因：已下达的 {issued.Count} 条一条回灌都没有。"
                                  + "补法：到「实绩录入」把这几个面的灰/热/硫回灌一次。")
                            + issueNote;
        else
            toolStatus.Text = $"{scope}：{tasks.Count} 条质量回灌 · 达标 {ok}/{tasks.Count}"
                            + $"（{100.0 * ok / tasks.Count:0}%）"
                            + (noQuality > 0 ? $" · 另有 {noQuality} 个已下达的采装面没有质量回灌，未参与判定" : "")
                            + issueNote
                            + "　|　" + SampleTaskBoard.DispatchSourceLabel;

        // 综合配煤是**入仓实绩**的加权，故走实绩侧口径（已下达）而不是计划侧 ——
        // 未下达的面没有入仓量，掺进来会把加权质量算偏。
        BuildBlendNote(issued, scope);
    }

    /// <summary>
    /// 综合配煤（产量加权入仓）。
    /// <para>
    /// ★ 配煤上限取<b>当日盘子里的配煤标准</b>（<c>cfg.Blend</c>，来自「质量标准 / 编制配置」），
    /// 不是写死的 12.8。原先这里硬编码了那个数：质量标准窗一改，这里照旧按 12.8 判达标，
    /// 而界面上一点异常都没有。
    /// </para>
    /// </summary>
    private void BuildBlendNote(List<ProductionTask> loads, string scope)
    {
        var wq = loads.Where(t => t.QualityActual != null && t.ActualVolumeM3 > 0).ToList();
        double tot = wq.Sum(t => t.ActualVolumeM3);
        if (tot <= 1e-6)
        {
            blendNote.Text = $"综合配煤（{scope}）：本班尚无实绩量，算不出入仓加权质量 —— 先在「实绩录入」录本班产量与煤质。";
            return;
        }

        double ash = wq.Sum(t => t.ActualVolumeM3 * t.QualityActual!.AshPct) / tot;
        double cv = wq.Sum(t => t.ActualVolumeM3 * t.QualityActual!.CalorificMJkg) / tot;
        double su = wq.Sum(t => t.ActualVolumeM3 * t.QualityActual!.SulfurPct) / tot;

        BlendStandard std;
        string src;
        try { std = SampleTaskBoard.Config().Blend ?? new BlendStandard(); src = "配煤标准取自当日盘子"; }
        catch { std = new BlendStandard(); src = "⚠ 盘子不可用，配煤标准按引擎缺省"; }

        bool okAsh = ash <= std.MaxAshPct + 1e-9;
        bool okCv = cv >= std.MinCalorificMJkg - 1e-9;
        bool okS = su <= std.MaxSulfurPct + 1e-9;

        blendNote.Text =
            $"综合配煤（{scope} · 产量加权入仓 {tot / 1e4:0.00} 万m³实方）："
          + $"灰 {ash:0.0}% / 标 ≤{std.MaxAshPct:0.#}% {(okAsh ? "✓" : "⚠")}　"
          + $"热 {cv:0.0} / 标 ≥{std.MinCalorificMJkg:0.#} MJ/kg {(okCv ? "✓" : "⚠")}　"
          + $"硫 {su:0.##}% / 标 ≤{std.MaxSulfurPct:0.##}% {(okS ? "✓" : "⚠")}"
          + $"。{(okAsh && okCv && okS ? "入仓达标。" : "有项越限，需掺配调整。")}（{src}）";
    }
}
