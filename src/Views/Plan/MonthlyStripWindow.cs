using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.Views.Road;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「量驱动月度采剥接续」窗口（移植原 <c>PlanLib.ShortTerm.MonthlyStripWindow</c>）—— <b>整条链唯一的界面入口</b>。
///
/// <para><b>这里只做三件事</b>：把界面上的值装成 <see cref="MonthlyStripSessionInput"/>、
/// 调 <see cref="MonthlyStripSession.Run"/>、把 <c>Log</c> 原样显示出来。
/// <b>装配逻辑、缺省值、失败该说什么话，全在会话层</b>（那一层脱 GUI 可验收，S 组判据钉着）——
/// 后台代码里不许再有第二套口径，否则界面和台架就会各说各的。</para>
/// </summary>
internal sealed class MonthlyStripWindow : Window
{
    private MonthlyStripSessionResult? _last;
    private MonthlyStripDeriveResult? _derive;
    private double[] _coal = Array.Empty<double>();

    private static readonly IBrush BadBrush = Brushes.OrangeRed;

    private readonly TextBox TxtProfile = new() { Width = 440, Height = 26, VerticalContentAlignment = VerticalAlignment.Center },
                             TxtMonths = Ro("—", 42), TxtCoal = Ro("—", 150), TxtN = Ed("3", 34),
                             TxtElapsed = Ed("4", 40), TxtActCoal = Ed("", 70), TxtActRock = Ed("", 70);
    private readonly TextBlock TxtProfileInfo = RoadUi.Hint("", 12), TxtSlotInfo = RoadUi.Hint("尚未取到 —— 先在「排土条带」里生成位置清单", 12),
                               TxtTargetInfo = RoadUi.Hint("逐月配置表：尚未取", 12), TxtDeriveInfo = RoadUi.Hint("", 12),
                               TxtReplanInfo = new() { TextWrapping = TextWrapping.Wrap, FontFamily = RoadUi.Mono, FontSize = 12 },
                               TxtStatus = new() { Text = "填好上面两组，点「排产」。", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox CmbPace, CmbStrategy;
    private readonly CheckBox ChkSteady = new() { Content = new TextBlock { Text = "期初已达稳态" }, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid GridMonths, GridFlows, GridCoal, GridSchemes;
    private readonly ListBox ListLog = new() { FontFamily = RoadUi.Mono, FontSize = 12 };
    private readonly Button BtnRun, BtnReport, BtnExport, BtnConfirm, BtnReplan, BtnRunScheme;
    private readonly TabControl _tabs;

    private static TextBox Ro(string t, double w) => new() { Text = t, Width = w, Height = 26, IsReadOnly = true, Focusable = false, IsTabStop = false, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
    private static TextBox Ed(string t, double w) => new() { Text = t, Width = w, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
    private static TextBlock L(string t, double w = double.NaN) { var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center }; if (!double.IsNaN(w)) tb.Width = w; return tb; }
    private static StackPanel H(Thickness margin, params Control[] kids) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin }; foreach (var k in kids) sp.Children.Add(k); return sp; }
    private static DataGridTextColumn Col(string header, string path, double width, string? fmt = null)
    {
        var b = new Binding(path); if (fmt != null) b.StringFormat = fmt;
        return new DataGridTextColumn { Header = header, Binding = b, Width = new DataGridLength(width), IsReadOnly = true };
    }
    private static DataGrid Grid(params DataGridColumn[] cols)
    {
        var dg = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12, SelectionMode = DataGridSelectionMode.Single };
        foreach (var c in cols) dg.Columns.Add(c);
        return PlanUi.FitHeaders(dg);
    }

    public MonthlyStripWindow()
    {
        Title = "量驱动月度采剥接续 — 给定月煤量，算该剥多少岩、剥在哪、排到哪";
        PlanUi.Place(this, 1280, 720);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        // 标题（天蓝渐变 —— 原 #38BDF8 → #0369A1）
        var header = PlanUi.Header("量驱动月度采剥接续", "压覆关系塌成标量不等式 · 超前剥离 = 约束前推 N 月 · 走廊内拉绳求剥采比均衡",
            Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x03, 0x69, 0xA1));

        // ── 备料 + 规则
        var prep = new StackPanel();
        ToolTip.SetTip(TxtProfile, "「生成采区台阶面」落的中间文件（只存扫一遍块体才能得到的东西，几百 KB）");
        var browse = RoadUi.Btn("浏览…", () => _ = OnBrowseProfileAsync(), 64); browse.Margin = new Thickness(6, 0, 0, 0);
        var latest = RoadUi.Btn("用最近一次", OnUseLatest, 86); latest.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(latest, "用 %TEMP%\\pitmine_benchdump 下最近一次生成的剖面");
        TxtProfileInfo.Margin = new Thickness(12, 0, 0, 0); TxtProfileInfo.VerticalAlignment = VerticalAlignment.Center;
        var lb1 = L("① 备料", 56); lb1.FontWeight = FontWeight.Bold;
        prep.Children.Add(H(new Thickness(0, 0, 0, 8), lb1, L("岩量剖面"), Sp(6), TxtProfile, browse, latest, TxtProfileInfo));
        TxtSlotInfo.Width = 440; TxtSlotInfo.VerticalAlignment = VerticalAlignment.Center;
        prep.Children.Add(H(new Thickness(0, 0, 0, 8), L("", 56), L("排土位置"), Sp(6), TxtSlotInfo, RoadUi.Btn("从「排土条带」取", RefreshSlotInfo, 120)));

        // 月数/月煤量【只读回显】—— 唯一来源是「逐月配置表」
        ToolTip.SetTip(TxtMonths, "只读回显 —— 月数由「逐月配置表」定，要改去「短期生产计划编制 → 逐月配置表」");
        ToolTip.SetTip(TxtCoal, "只读回显 —— 逐月煤量由「逐月配置表」定（人工覆盖也在那里填）");
        TxtCoal.Margin = new Thickness(4, 0, 2, 0);
        var pull = RoadUi.Btn("取配置表", OnPullTargets, 72); pull.Margin = new Thickness(0, 0, 12, 0);
        ToolTip.SetTip(pull, "重新从「逐月配置表」取：逐月煤量 / 作业日 / 剥离能力 / 起始月");
        ToolTip.SetTip(TxtN, "R35：备采保有月数，与「回采煤量 ≈ 2–3 月产量」同义");
        CmbPace = PlanUi.Combo(new[] { "贴底（最省）", "拉平（最均衡）", "前重（抗风险）" }, 1, 130); CmbPace.Margin = new Thickness(4, 0, 12, 0);
        CmbStrategy = PlanUi.Combo(new[] { "运输功最小", "内排优先", "库容均衡" }, 0, 118); CmbStrategy.Margin = new Thickness(4, 0, 12, 0);
        ToolTip.SetTip(ChkSteady, "R40：生产接续勾上。不勾 = 裸起始工作帮，基建剥离会全压到第 1 个月（只有新建矿才对）");
        var lb2 = L("② 规则", 56); lb2.FontWeight = FontWeight.Bold;
        prep.Children.Add(H(default, lb2, L("月数"), TxtMonths, L("月煤量(万t)"), TxtCoal, pull, L("N(备采月)"), TxtN, L("剥离节奏"), CmbPace, L("配对策略"), CmbStrategy, ChkSteady));
        // 从表里取到了什么：取到什么就显示什么 —— 缺的那几项如实说"没给"
        TxtTargetInfo.TextWrapping = TextWrapping.Wrap; TxtTargetInfo.VerticalAlignment = VerticalAlignment.Center;
        var tgtRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var pad = L("", 56); DockPanel.SetDock(pad, Avalonia.Controls.Dock.Left); tgtRow.Children.Add(pad); tgtRow.Children.Add(TxtTargetInfo);
        prep.Children.Add(tgtRow);
        var prepB = new Border { Padding = new Thickness(14, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = prep };
        RoadUi.Theme(prepB, Border.BorderBrushProperty, "Theme.Panel.Border");

        // ── 结果页签
        GridMonths = Grid(Col("月", "Month", 46), Col("采出(万t)", "CoalWanT", 82, "{0:F2}"), Col("剥离(万m³)", "StripWanM3", 88, "{0:F2}"), Col("剥采比", "Ratio", 62, "{0:F2}"),
            Col("推进(m)", "AdvanceM", 72, "{0:F2}"), Col("备采(万t)", "PreparedWanT", 76, "{0:F1}"), Col("保有(月)", "PreparedMonths", 66, "{0:F2}"), Col("超前储备(万m³)", "LeadStockWanM3", 104, "{0:F1}"),
            // 绑 Text 不绑数值：没跑配对时是 −1（哨兵），直接绑会在百分比列里显示 −1.0
            Col("内排率(%)", "InternalRateText", 76), Col("运输功(万t·km)", "TransportWorkText", 106), Col("排不下(万m³)", "UnplacedWanM3", 96, "{0:F2}"),
            // 这个月被哪一侧顶住：回答"想改善该松哪一条"
            Col("紧迫", "CriticalText", 118));

        ListLog.ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontFamily = RoadUi.Mono, FontSize = 12 });

        GridSchemes = Grid(Col("方案", "AxisText", 220), Col("得分", "Score", 56, "{0:F1}"),
            new DataGridCheckBoxColumn { Header = "可行", Binding = new Binding("Feasible"), Width = new DataGridLength(46), IsReadOnly = true },
            Col("剥采比变异", "RatioCv", 82, "{0:F3}"), Col("峰值剥离(m³)", "PeakStripM3", 106, "{0:N0}"), Col("超前储备(m³)", "MeanLeadStockM3", 106, "{0:N0}"),
            Col("最低保有(月)", "MinPreparedMonths", 94, "{0:F2}"), Col("总剥离(m³)", "TotalRockM3", 102, "{0:N0}"),
            Col("运输功(万t·km)", "TransportWorkText", 102), Col("内排率(%)", "InternalRateText", 76));
        var deriveTab = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var dHint = RoadUi.Hint("方案 = 规则取值的组合，不是解的枚举 —— 拉绳在给定规则下唯一最优，所以差异只能来自规则本身，也因此可归因到具体哪条规则。选中一行点「按选中方案排产」会【重跑】完整链路（派生阶段没跑外循环）。", 12);
        dHint.Margin = new Thickness(4, 4, 4, 6); dHint.TextWrapping = TextWrapping.Wrap;
        var dBtns = H(new Thickness(4, 6, 4, 4));
        var derive = RoadUi.Btn("派生比选", OnDerive, 96); ToolTip.SetTip(derive, "按 N × 剥离节奏 × 采煤节奏 穷举规则取值，各自求最优后打分排序 + 归因");
        BtnRunScheme = RoadUi.Btn("按选中方案排产", OnRunScheme, 132); BtnRunScheme.IsEnabled = false;
        TxtDeriveInfo.Margin = new Thickness(14, 0, 0, 0); TxtDeriveInfo.VerticalAlignment = VerticalAlignment.Center;
        dBtns.Children.Add(derive); dBtns.Children.Add(BtnRunScheme); dBtns.Children.Add(TxtDeriveInfo);
        Avalonia.Controls.Grid.SetRow(dHint, 0); Avalonia.Controls.Grid.SetRow(GridSchemes, 1); Avalonia.Controls.Grid.SetRow(dBtns, 2);
        deriveTab.Children.Add(dHint); deriveTab.Children.Add(GridSchemes); deriveTab.Children.Add(dBtns);

        var replanTab = new StackPanel { Margin = new Thickness(6, 8, 6, 6) };
        var rHint = RoadUi.Hint("跑了几个月之后，拿实绩重排剩下的月份。欠剥比欠采危险 —— 欠采是这个月少卖点煤，欠剥是下个月没煤可采；重排会把欠账压进后续月份，压不下去时硬校核会红，那正是要看的。", 12);
        rHint.TextWrapping = TextWrapping.Wrap; rHint.Margin = new Thickness(0, 0, 0, 10); replanTab.Children.Add(rHint);
        ToolTip.SetTip(TxtActCoal, "留空 = 按计划量（等于假设照计划走）"); ToolTip.SetTip(TxtActRock, "留空 = 按计划量");
        TxtElapsed.Margin = new Thickness(6, 0, 16, 0); TxtActCoal.Margin = new Thickness(6, 0, 16, 0); TxtActRock.Margin = new Thickness(6, 0, 16, 0);
        BtnReplan = RoadUi.Btn("按实绩重排", OnReplan, 106); BtnReplan.IsEnabled = false;
        replanTab.Children.Add(H(new Thickness(0, 0, 0, 8), L("已过月数"), TxtElapsed, L("累计实采(万t)"), TxtActCoal, L("累计实剥(万m³)"), TxtActRock, BtnReplan));
        replanTab.Children.Add(TxtReplanInfo);
        var rHint2 = RoadUi.Hint("⚠ 这里只填量、不填台阶位置，所以期初姿态只能按稳态推算 —— 那等于假设前几个月完全照计划走，位置上的偏差会被吞掉。要真纠偏，得从现状面反算真实台阶位置。", 12);
        rHint2.TextWrapping = TextWrapping.Wrap; rHint2.Margin = new Thickness(0, 10, 0, 0); replanTab.Children.Add(rHint2);

        // 岩流 + 煤流 放同一页：只看岩流是**半份账**
        GridFlows = Grid(Col("月", "Month", 46), Col("源", "SourceName", 120), Col("物料", "MaterialName", 96), Col("物料码", "MaterialCode", 86), Col("实方(万m³)", "InSituWanM3", 92, "{0:F2}"),
            Col("去向", "DestinationName", 120), new DataGridCheckBoxColumn { Header = "内排", Binding = new Binding("IsInternalDump"), Width = new DataGridLength(50), IsReadOnly = true },
            Col("运距(km)", "HaulKm", 76, "{0:F2}"), Col("ρ", "DensityUsed", 46, "{0:F2}"), Col("Kr", "KrUsed", 46, "{0:F2}"), Col("吨量(万t)", "TonnageWanT", 86, "{0:F2}"), Col("占容(万m³)", "DumpWanM3", 92, "{0:F2}"));
        // R36：摊过的数看上去和用户填的一模一样，所以必须在界面上标出来（「分层量来源」列）
        GridCoal = Grid(Col("月", "Month", 46), Col("层", "SeamName", 120), Col("实方(万m³)", "InSituWanM3", 92, "{0:F2}"), Col("ρ", "DensityUsed", 46, "{0:F2}"), Col("吨量(万t)", "TonnageWanT", 86, "{0:F2}"), Col("分层量来源", "AllocationText", 150));
        var flowTab = new Grid { RowDefinitions = new RowDefinitions("*,Auto,1.1*") };
        var fHint = RoadUi.Hint("采出煤（逐月逐层）—— 只给源侧（层+量），煤去哪个破碎站/煤仓由下游的去向台账定。「引擎摊的」那一列若为真，这一行的分层量是引擎按规则摊出来的，不是填的。", 12);
        fHint.Margin = new Thickness(4, 8, 4, 4); fHint.TextWrapping = TextWrapping.Wrap;
        Avalonia.Controls.Grid.SetRow(GridFlows, 0); Avalonia.Controls.Grid.SetRow(fHint, 1); Avalonia.Controls.Grid.SetRow(GridCoal, 2);
        flowTab.Children.Add(GridFlows); flowTab.Children.Add(fHint); flowTab.Children.Add(GridCoal);

        _tabs = new TabControl { Margin = new Thickness(10, 8, 10, 0) };
        _tabs.Items.Add(Tab("逐月计划", GridMonths));
        _tabs.Items.Add(Tab("过程与结论", ListLog));
        _tabs.Items.Add(Tab("派生比选", deriveTab));
        _tabs.Items.Add(Tab("实绩与重排", replanTab));
        _tabs.Items.Add(Tab("物料流", flowTab));

        // ── 底栏
        var foot = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        BtnRun = RoadUi.Btn("排产", OnRun, 92, bold: true); BtnRun.Height = 30;
        BtnReport = RoadUi.Btn("导出报表…", () => _ = OnExportReportAsync(), 98); BtnReport.IsEnabled = false; BtnReport.Height = 30;
        ToolTip.SetTip(BtnReport, "给人看的那一份：逐月表（带触底/触顶标记）+ 源×去向配对矩阵 + 方案比选 + 过程与结论。契约 JSON 是给下游软件的，不是给会上传阅的");
        BtnExport = RoadUi.Btn("导出契约 JSON…", () => _ = OnExportJsonAsync(), 132); BtnExport.IsEnabled = false; BtnExport.Height = 30;
        var import = RoadUi.Btn("装回契约 JSON…", () => _ = OnImportJsonAsync(), 132); import.Height = 30;
        ToolTip.SetTip(import, "本子系统的状态全在会话内，关掉就没了 —— 契约 JSON 是唯一能带走的东西。装回来可以接着确定入库，但没有剖面/外循环，重排用不了");
        BtnConfirm = RoadUi.Btn("确定为月度方案", OnConfirm, 132); BtnConfirm.IsEnabled = false; BtnConfirm.Height = 30;
        ToolTip.SetTip(BtnConfirm, "写入短期方案确定簿 —— 作业计划 / 三维动态模拟 / 采运排一体化都从这里取");
        var close = RoadUi.Btn("关闭", Close, 72); close.Height = 30; close.Margin = new Thickness(0);
        foreach (var b in new[] { BtnRun, BtnReport, BtnExport, import, BtnConfirm, close }) btns.Children.Add(b);
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Right); foot.Children.Add(btns); foot.Children.Add(TxtStatus);
        var footB = new Border { Padding = new Thickness(12, 8), Child = foot };

        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Avalonia.Controls.Grid.SetRow(header, 0); Avalonia.Controls.Grid.SetRow(prepB, 1); Avalonia.Controls.Grid.SetRow(_tabs, 2); Avalonia.Controls.Grid.SetRow(footB, 3);
        shell.Children.Add(header); shell.Children.Add(prepB); shell.Children.Add(_tabs); shell.Children.Add(footB);
        Content = shell;

        TxtProfile.Text = DefaultProfilePath();
        RefreshProfileInfo();
        RefreshSlotInfo();
        RefreshTargetInfo();
    }

    private static Control Sp(double w) => new Border { Width = w };
    private static TabItem Tab(string header, Control content) => new() { Header = new TextBlock { Text = header, FontSize = 13 }, Content = content };

    // ── 备料 ────────────────────────────────────────────────────────────────

    /// <summary>「生成采区台阶面」默认落盘的位置（与 <c>InclineCaseFile</c> 同一个约定）。</summary>
    private static string DefaultProfilePath()
        => Path.Combine(Path.GetTempPath(), "pitmine_benchdump", "REAL_latest.case");

    private async Task OnBrowseProfileAsync()
    {
        var opt = new FilePickerOpenOptions
        {
            Title = "选岩量剖面中间文件", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("剖面/用例") { Patterns = new[] { "*.case", "*.mprof" } }, new FilePickerFileType("所有文件") { Patterns = new[] { "*" } } },
        };
        try
        {
            string? dir = Path.GetDirectoryName(TxtProfile.Text ?? "");
            if (dir != null && Directory.Exists(dir)) opt.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);
        }
        catch { /* 路径不合法就用系统缺省目录 */ }
        var files = await StorageProvider.OpenFilePickerAsync(opt);
        if (files.Count > 0) { TxtProfile.Text = files[0].Path.LocalPath; RefreshProfileInfo(); }
    }

    private void OnUseLatest()
    {
        TxtProfile.Text = DefaultProfilePath();
        RefreshProfileInfo();
    }

    /// <summary>
    /// 刷新剖面文件状态。<b>不止报"文件在不在"，还要真打开看看里面有没有岩剖面</b>。
    /// <para>只在**选文件/点按钮**时调，不挂 TextChanged —— 读 22MB 不能跟着每次敲键盘走。</para>
    /// </summary>
    private void RefreshProfileInfo()
    {
        string p = TxtProfile.Text?.Trim() ?? "";
        if (p.Length == 0) { TxtProfileInfo.Text = "未选"; return; }
        try
        {
            var fi = new FileInfo(p);
            if (!fi.Exists)
            {
                Paint("◆ 文件不存在 —— 先跑一次「生成采区台阶面」", bad: true);
                return;
            }
            string size = $"{fi.Length / 1048576.0:0.0} MB · {fi.LastWriteTime:MM-dd HH:mm}";

            // 真打开看一眼（两种容器都收）。大文件读一次几百毫秒，值。
            Cursor = new Cursor(StandardCursorType.Wait);
            CoalProfile? cp;
            string err;
            try { cp = MonthlyStripSession.LoadProfileFile(p, null, out err, out _); }
            finally { Cursor = Cursor.Default; }

            // 判什么、按什么顺序判，都在 DescribeProfile 里（判据 S19 钉着）。这里只管把文件大小拼上去、上个色。
            var (msg, isBad) = MonthlyStripSession.DescribeProfile(cp, err);
            Paint($"{size}　{msg}", bad: isBad);
        }
        catch (Exception ex) { Paint("◆ 读不到：" + ex.Message, bad: true); }

        void Paint(string s, bool bad = false)
        {
            TxtProfileInfo.Text = s;
            if (bad) TxtProfileInfo.Foreground = BadBrush; else RoadUi.Theme(TxtProfileInfo, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        }
    }

    private void RefreshSlotInfo() => TxtSlotInfo.Text = DumpStripStore.Caption;

    // ── 规则 ────────────────────────────────────────────────────────────────

    /// <summary>「取配置表」—— 重新从逐月配置表取一遍并回显。</summary>
    private void OnPullTargets()
    {
        var t = Targets(out string err);
        RefreshTargetInfo();
        if (t == null) { Status(err, bad: true); return; }
        Status("已取逐月配置表：" + TxtTargetInfo.Text);
    }

    /// <summary>取<b>那一张</b>逐月配置表（<see cref="MonthlyTargetStore.Current"/>）。本窗口不再有第二个月煤量来源。</summary>
    private MonthlyTargetTable? Targets(out string err)
    {
        err = "";
        try
        {
            var t = MonthlyTargetStore.Current;   // 首次访问会按当前基础约束自动派生一份
            if (t.Rows.Count == 0)
            { err = "逐月配置表是空的 —— 去「短期生产计划编制」窗口点「按当前参数派生」"; return null; }
            return t;
        }
        catch (Exception ex) { err = "取不到逐月配置表：" + ex.Message; return null; }
    }

    /// <summary>把表里取到的量回显到界面。<b>取到什么显示什么</b>。</summary>
    private void RefreshTargetInfo()
    {
        var t = Targets(out string err);
        if (t == null)
        {
            _coal = Array.Empty<double>();
            TxtMonths.Text = "—"; TxtCoal.Text = "—";
            Paint("◆ " + err, bad: true);
            return;
        }

        _coal = t.CoalTargetWt();
        var wd = t.WorkdaysArray();
        t.StripCapM3Array(out bool capAllMissing);
        int capGiven = t.Rows.Count(r => r.StripCapWanM3.HasValue);
        int wdGiven = wd.Count(v => v > 0);

        TxtMonths.Text = _coal.Length.ToString();
        TxtCoal.Text = _coal.Length <= 6
            ? string.Join(",", _coal.Select(v => v.ToString("0.##")))
            : string.Join(",", _coal.Take(5).Select(v => v.ToString("0.##"))) + $",…({_coal.Length}个)";

        Paint($"逐月配置表：{t.Rows.Count} 个月（{(t.Rows.Count > 0 ? t.Rows[0].PeriodKey + "…" + t.Rows[^1].PeriodKey : "")}）"
            + $"　煤合计 {_coal.Sum():0.#} 万t　起始月 {t.StartMonth}"
            + $"　作业日 {(wdGiven > 0 ? $"{wdGiven}/{wd.Length} 个月有" : "**一个月都没有** → 下游按缺省 25 天折算(I9)")}"
            + $"　剥离能力 {(capAllMissing ? "整列留空 = 不限" : $"{capGiven}/{t.Rows.Count} 个月给了")}"
            + $"　人工覆盖 {t.ManualRowCount} 行",
              bad: false);

        void Paint(string s, bool bad)
        {
            TxtTargetInfo.Text = s;
            if (bad) TxtTargetInfo.Foreground = BadBrush; else RoadUi.Theme(TxtTargetInfo, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        }
    }

    private StripPace Pace() => CmbPace.SelectedIndex switch { 0 => StripPace.Hug, 2 => StripPace.FrontLoad, _ => StripPace.Level };
    private PairingStrategy Strategy() => CmbStrategy.SelectedIndex switch { 1 => PairingStrategy.InternalFirst, 2 => PairingStrategy.LevelCapacity, _ => PairingStrategy.MinHaul };

    // ── 排产 ────────────────────────────────────────────────────────────────

    private void OnRun()
    {
        var inp = BuildInput(out string err);
        if (inp == null) { Status(err, bad: true); return; }

        BtnRun.IsEnabled = false;
        Cursor = new Cursor(StandardCursorType.Wait);
        MonthlyStripSessionResult r;
        try { r = MonthlyStripSession.Run(inp); }
        finally { Cursor = Cursor.Default; BtnRun.IsEnabled = true; }
        ShowRun(r);
    }

    /// <summary>
    /// 把界面上的值装成会话输入。<b>单套排产与派生比选共用这一份</b>。
    /// <para><b>逐月煤量 / 作业日 / 剥离能力 / 起始月这四项来自逐月配置表</b>，界面上填不了。</para>
    /// </summary>
    private MonthlyStripSessionInput? BuildInput(out string err)
    {
        var t = Targets(out err);
        RefreshTargetInfo();          // 装配这一刻表里是什么样，界面上就显示什么样
        if (t == null) return null;

        var q = t.CoalTargetWt();
        if (q.Length == 0 || q.Sum() <= 1e-9)
        { err = "逐月配置表里的月采出全是 0 —— 这是整条链唯一的驱动量，去表里填（本窗口不接受手填）"; return null; }
        if (q.Any(v => v < 0))
        { err = "逐月配置表里有负的月采出 —— 先去表里改（这里不替你夹回 0）"; return null; }
        if (q.Length > 120)
        { err = $"逐月配置表有 {q.Length} 个月 —— 逐月接续是短期计划，超过 120 个月请用中长远"; return null; }

        if (!int.TryParse(TxtN.Text?.Trim(), out int n) || n < 0) { err = "N 要是非负整数"; return null; }

        var cells = DumpStripStore.Last?.Cells;
        if (cells == null || cells.Count == 0)
        { err = "没有排土位置 —— 先在「排土条带」里生成位置清单，再回来点「从「排土条带」取」"; return null; }

        // 作业日：会话层的口径是「空 = 没给（记 I9，按缺省 25 天折算）」。表里一个月都没派生出作业日时传空数组，不传一串 0。
        var wd = t.WorkdaysArray();
        bool wdOk = wd.Length == q.Length && wd.Any(v => v > 0);

        // 剥离能力：未给 → −1（下游口径里 <0 = 不限），这一次转换在 StripCapM3Array 里做。整列都没给就干脆传空数组。
        var cap = t.StripCapM3Array(out bool capAllMissing);

        return new MonthlyStripSessionInput
        {
            ProfilePath = TxtProfile.Text?.Trim() ?? "",
            DumpCells = cells,
            DumpName = DumpStripStore.SourceNote,
            CoalTargetWt = q,
            Workdays = wdOk ? wd : Array.Empty<double>(),
            StripCapM3 = capAllMissing ? Array.Empty<double>() : cap,
            StartMonth = t.StartMonth,
            LookaheadMonths = n,
            Pace = Pace(),
            Strategy = Strategy(),
            StartInSteadyState = ChkSteady.IsChecked == true,
            PlanName = $"量驱动月度采剥接续 {DateTime.Now:MM-dd HH:mm}",
        };
    }

    /// <summary>把一次排产的结果铺到界面上（单套排产与"按选中方案排产"共用）。</summary>
    private void ShowRun(MonthlyStripSessionResult r)
    {
        _last = r;
        ListLog.ItemsSource = r.Log.ToList();
        GridMonths.ItemsSource = r.Export?.Months;
        GridFlows.ItemsSource = r.Export?.Flows;
        GridCoal.ItemsSource = r.Export?.Coal;      // 采出煤那一半 —— 只看岩流是半份账
        BtnExport.IsEnabled = r.Export != null;
        BtnReport.IsEnabled = true;                 // 报表失败时也能导 —— 失败的那次最需要把过程带走给人看
        BtnConfirm.IsEnabled = r.CanConfirm;
        BtnReplan.IsEnabled = r.Success && r.Schedule != null;

        if (!r.Success) Status("✗ " + r.Error + "　（详见「过程与结论」）", bad: true);
        else if (!r.CanConfirm)
            Status($"跑通了，但有 {r.Blocking.Count} 条阻断性问题，不能确定为月度方案 —— {r}", bad: true);
        else Status("✔ " + r);
    }

    // ── 派生比选 ────────────────────────────────────────────────────────────

    private void OnDerive()
    {
        var inp = BuildInput(out string err);
        if (inp == null) { Status(err, bad: true); return; }

        // 三条轴的取值就是「短期计划里要设计的规则」。配对策略那条轴不默认打开 —— 四轴全开是 81 套。
        var axes = new ScheduleAxes
        {
            Lookahead = new[] { Math.Max(0, ReadN() - 1), ReadN(), ReadN() + 1 }.Distinct().ToArray(),
            StripPaces = new[] { StripPace.Hug, StripPace.Level, StripPace.FrontLoad },
            // 缺省三值，不是用户挑的 —— 所以不置 CoalPacesExplicit：有真工作历时该让会话层把它收成「均衡」
            CoalPaces = new[] { CoalPace.Balanced, CoalPace.Rush, CoalPace.Conservative },
        };

        Cursor = new Cursor(StandardCursorType.Wait);
        MonthlyStripDeriveResult d;
        try { d = MonthlyStripSession.Derive(inp, axes); }
        finally { Cursor = Cursor.Default; }

        _derive = d;
        ListLog.ItemsSource = d.Log.ToList();
        GridSchemes.ItemsSource = d.Schemes.OrderByDescending(s => s.Feasible).ThenByDescending(s => s.Score).ToList();
        GridSchemes.SelectedItem = d.Recommended;
        BtnRunScheme.IsEnabled = d.Schemes.Count > 0;
        TxtDeriveInfo.Text = d.Success && d.Recommended != null
            ? $"推荐「{d.Recommended.AxisText}」{d.Recommended.Score:0.0} 分（可行优先，再比分）"
            : "";
        _tabs.SelectedIndex = 2;
        Status(d.Success ? "✔ " + d : "✗ " + d.Error, bad: !d.Success);
    }

    private void OnRunScheme()
    {
        var pick = GridSchemes.SelectedItem as ScheduleScheme;
        if (pick == null) { Status("先在比选表里选中一行", bad: true); return; }

        Cursor = new Cursor(StandardCursorType.Wait);
        MonthlyStripSessionResult r;
        try { r = MonthlyStripSession.RunScheme(_derive, pick); }
        finally { Cursor = Cursor.Default; }
        ShowRun(r);
    }

    private int ReadN() => int.TryParse(TxtN.Text?.Trim(), out int n) && n >= 0 ? n : 3;

    // ── 滚动重排 ────────────────────────────────────────────────────────────

    private void OnReplan()
    {
        if (_last?.Success != true) { Status("先跑一次排产，才有可重排的上期计划", bad: true); return; }
        if (!int.TryParse(TxtElapsed.Text?.Trim(), out int k) || k <= 0)
        { Status("已过月数要是正整数", bad: true); return; }

        // 留空 = 按计划量（= 假设照计划走）。这不是"没填"，是一个真实的假设，Log 里会说。
        var planned = _last.Schedule!.Months;
        if (k > planned.Count) { Status($"已过 {k} 个月，但上期计划只有 {planned.Count} 个月", bad: true); return; }
        var at = planned[k - 1];

        var act = new ActualToDate
        {
            MonthsElapsed = k,
            CoalWt = ReadOr(TxtActCoal, at.CoalCumWt),
            RockM3 = ReadOr(TxtActRock, at.RockCumM3 / 1e4) * 1e4,
            // 位置留空 —— 会话层会说清"这等于假设照计划走"，不在这儿悄悄补一个
        };

        Cursor = new Cursor(StandardCursorType.Wait);
        MonthlyStripSessionResult r;
        try { r = MonthlyStripSession.Replan(_last, act); }
        finally { Cursor = Cursor.Default; }

        TxtReplanInfo.Text = r.Replan != null
            ? r.Replan.Summary() + "\n" + string.Join("\n", r.Replan.Notes)
            : (r.Success ? "" : r.Error);
        ShowRun(r);
    }

    private static double ReadOr(TextBox tb, double fallback)
        => double.TryParse(tb.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0
         ? v : fallback;

    // ── 产物 ────────────────────────────────────────────────────────────────

    /// <summary>导出<b>人读报表</b>（.txt）。内容全部来自内核已有的三份报表，这里不新算。</summary>
    private async Task OnExportReportAsync()
    {
        if (_last == null) { Status("还没有可导出的排产结果", bad: true); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出月度采剥报表", SuggestedFileName = $"月度采剥接续_报表_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            FileTypeChoices = new[] { new FilePickerFileType("文本") { Patterns = new[] { "*.txt" } } },
        });
        if (file == null) return;
        try
        {
            // UTF-8 **带 BOM**：这份是给人用 Excel/记事本打开的，没 BOM 的话中文会乱码。（契约 JSON 那条相反，给程序读，必须不带 BOM。）
            File.WriteAllText(file.Path.LocalPath, MonthlyStripSession.BuildReport(_last, _derive), new System.Text.UTF8Encoding(true));
            Status($"已导出报表 → {file.Path.LocalPath}");
        }
        catch (Exception ex) { Status("导出失败：" + ex.Message, bad: true); }
    }

    private async Task OnExportJsonAsync()
    {
        if (_last?.Export == null) { Status("还没有可导出的契约", bad: true); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出月度采剥契约", SuggestedFileName = $"月度采剥接续_{DateTime.Now:yyyyMMdd_HHmm}.json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file == null) return;
        try
        {
            File.WriteAllText(file.Path.LocalPath, _last.Export.ToJson(), new System.Text.UTF8Encoding(false));
            Status($"已导出 → {file.Path.LocalPath}");
        }
        catch (Exception ex) { Status("导出失败：" + ex.Message, bad: true); }
    }

    private async Task OnImportJsonAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "装回月度采剥契约", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }, new FilePickerFileType("所有文件") { Patterns = new[] { "*" } } },
        });
        if (files.Count == 0) return;

        string json;
        try { json = File.ReadAllText(files[0].Path.LocalPath); }
        catch (Exception ex) { Status("读文件失败：" + ex.Message, bad: true); return; }
        ImportJson(json);
    }

    private void ImportJson(string json)
    {
        var r = MonthlyStripSession.FromContract(json);
        // 装回来的没有排产结果 ⇒ 「按实绩重排」必须保持关闭（ShowRun 已按 Schedule 判）
        ShowRun(r);
        if (r.Success) Status($"✔ 已装回：{r.Export!.Months.Count} 个月 · "
                            + $"煤 {r.Export.TotalCoalWanT:0.0}万t · 岩 {r.Export.TotalStripWanM3:0.0}万m³ · "
                            + (r.CanConfirm ? "可确定入库" : $"◆{r.Blocking.Count} 条阻断"));
    }

    private void OnConfirm()
    {
        if (!MonthlyStripSession.Confirm(_last, out string err)) { Status("✗ " + err, bad: true); return; }
        ListLog.ItemsSource = _last!.Log.ToList();
        BtnConfirm.IsEnabled = false;
        Status($"✔ 已确定为月度方案「{_last.Plan!.Name}」—— 作业计划 / 三维动态模拟 / 采运排一体化即刻可用");
    }

    private void Status(string s, bool bad = false)
    {
        TxtStatus.Text = s;
        if (bad) TxtStatus.Foreground = BadBrush; else RoadUi.Theme(TxtStatus, TextBlock.ForegroundProperty, "Theme.Text.Primary");
    }

    // ── 自检直通 ────────────────────────────────────────────────────────────
    internal string SelftestStatus => TxtStatus.Text ?? "";
    internal string SelftestProfileInfo => TxtProfileInfo.Text ?? "";
    internal string SelftestSlotInfo => TxtSlotInfo.Text ?? "";
    internal string SelftestTargetInfo => TxtTargetInfo.Text ?? "";
    internal int SelftestMonthRows => GridMonths.ItemsSource is IList<ExportMonth> l ? l.Count : 0;
    internal int SelftestLogLines => ListLog.ItemsSource is IList<string> l ? l.Count : 0;
    internal void SelftestSetProfile(string path) { TxtProfile.Text = path; RefreshProfileInfo(); }
    internal void SelftestRun() => OnRun();
    internal void SelftestDerive() => OnDerive();
    internal void SelftestImportJson(string json) => ImportJson(json);
    /// <summary>自检：用调用方装好的会话输入跑整条链（跳过界面备料 —— 自检机上没有剖面文件与排土条带）并铺到界面。</summary>
    internal void SelftestRunWith(MonthlyStripSessionInput inp) { ShowRun(MonthlyStripSession.Run(inp)); }
    internal void SelftestDeriveWith(MonthlyStripSessionInput inp)
    {
        var axes = new ScheduleAxes { Lookahead = new[] { 2, 3, 4 }, StripPaces = new[] { StripPace.Hug, StripPace.Level, StripPace.FrontLoad }, CoalPaces = new[] { CoalPace.Balanced, CoalPace.Rush, CoalPace.Conservative } };
        var d = MonthlyStripSession.Derive(inp, axes); _derive = d;
        GridSchemes.ItemsSource = d.Schemes.OrderByDescending(s => s.Feasible).ThenByDescending(s => s.Score).ToList();
        GridSchemes.SelectedItem = d.Recommended; BtnRunScheme.IsEnabled = d.Schemes.Count > 0;
        TxtDeriveInfo.Text = d.Success && d.Recommended != null ? $"推荐「{d.Recommended.AxisText}」{d.Recommended.Score:0.0} 分（可行优先，再比分）" : "";
        _tabs.SelectedIndex = 2; Status(d.Success ? "✔ " + d : "✗ " + d.Error, bad: !d.Success);
    }
    internal int SelftestSchemeRows => GridSchemes.ItemsSource is System.Collections.ICollection c ? c.Count : 0;
    internal void SelftestTab(int i) => _tabs.SelectedIndex = i;
}
