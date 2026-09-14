using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「编制配置」—— 裂解装箱用的切分规则（当日能力 / 面间分配 / 校核阈值）。
/// 盘子里的**事实**（作业面·去向·设备·爆破）在各自台账里改，此处只放**人工锚点**。
///
/// <para>
/// 每一格<b>留空 = 不设</b>（由引擎缺省接管），不是 0。这条在两处尤其要紧：
/// 「天气降效 0」= 人工确认过今天不降效，「交接班损失 0」= 本矿不扣交接 ——
/// 与"没设过"是两回事，把缺省写进锚点，日后引擎调缺省时旧值会把新口径顶回来。
/// </para>
///
/// ── 与原版的范围差异（登记）──
/// <list type="number">
///   <item>原窗上半部是大段<b>回显</b>：当日能力预算条形图、班制/检修爆破/盘子来源回显、
///     铲—车编组联动表、链路体检。它们读的是排产装配层装出来的当日盘子，Kylin 尚无那一层，
///     <b>未移</b> —— 回显一个假盘子比不回显更坏。</item>
///   <item><b>作业组织策略（均衡/多面展开/集中强采）未移</b>：它要按策略重分配全盘目标，
///     Kylin 的引擎没有那段逻辑。界面上也不放这三个单选 —— 摆上去却不生效，
///     正是原版当初要修的那个毛病（"界面改了什么都不会发生"）。</item>
///   <item>逐受矿点入仓标准原版是可编辑表格；这里照 §三三三 的做法：上方选点填值、下方只读表看。</item>
/// </list>
/// </summary>
internal sealed class CompileConfigWindow : Window
{
    internal sealed class SinkRow
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Ash { get; set; } = "";
        public string Cv { get; set; } = "";
        public string Sulfur { get; set; } = "";
        public string From { get; set; } = "";
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    // ① 当日能力
    private readonly TextBox _handover = new() { Width = 84, Watermark = "缺省 0.5" };
    private readonly TextBox _weather = new() { Width = 84, Watermark = "缺省 0" };
    private readonly TextBox _dLoad = new() { Width = 66, Watermark = "跟随" };
    private readonly TextBox _dHaul = new() { Width = 66, Watermark = "跟随" };
    private readonly TextBox _dDump = new() { Width = 66, Watermark = "跟随" };
    // ② 面间分配
    private readonly TextBox _effHours = new() { Width = 84, Watermark = "缺省 20" };
    private readonly TextBox _ash = new() { Width = 84, Watermark = "缺省 12.8" };
    private readonly TextBox _cv = new() { Width = 84, Watermark = "缺省 21.5" };
    private readonly TextBox _sulfur = new() { Width = 84, Watermark = "缺省 0.7" };
    // ③ 校核阈值
    private readonly TextBox _minPrepared = new() { Width = 84, Watermark = "0=不校核" };

    // 逐受矿点
    private readonly ComboBox _sinkPick = new() { Width = 200 };
    private readonly TextBox _sAsh = new() { Width = 74, Watermark = "灰%" };
    private readonly TextBox _sCv = new() { Width = 74, Watermark = "热MJ" };
    private readonly TextBox _sSulfur = new() { Width = 74, Watermark = "硫%" };
    private readonly DataGrid _sinkGrid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };
    private readonly ObservableCollection<SinkRow> _sinkRows = new();

    private readonly TextBlock _effect = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private CompileAnchors _anchors = new();
    private SinkRegistry? _sinks;

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal CompileConfigWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "编制配置";
        Width = 900; Height = 600;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        BuildSinkColumns();
        _sinkGrid.ItemsSource = _sinkRows;
        Content = BuildLayout();
        Reload();
    }

    private void BuildSinkColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };
        _sinkGrid.Columns.Add(C("受矿点", nameof(SinkRow.Name), 190));
        _sinkGrid.Columns.Add(C("种类", nameof(SinkRow.Kind), 110));
        _sinkGrid.Columns.Add(C("灰分上限 %", nameof(SinkRow.Ash), 120));
        _sinkGrid.Columns.Add(C("热值下限 MJ/kg", nameof(SinkRow.Cv), 150));
        _sinkGrid.Columns.Add(C("硫分上限 %", nameof(SinkRow.Sulfur), 120));
        _sinkGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("来源"), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(SinkRow.From)),
        });
    }

    private static Button B(string t, Action a, bool bold = false, string? tip = null)
    {
        var b = new Button { Content = t, Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
        if (bold) b.FontWeight = FontWeight.SemiBold;
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => a();
        return b;
    }

    private static TextBlock Lab(string t, double w = 130, string? tip = null)
    {
        var tb = new TextBlock
        {
            Text = t, Width = w, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap, FontSize = 12,
        };
        if (tip != null) ToolTip.SetTip(tb, tip);
        return tb;
    }

    private static TextBlock Hint(string t) => new()
    { Text = t, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#777"), Margin = new Thickness(0, 2, 0, 8) };

    private static Border Group(string header, params Control[] children)
    {
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(new TextBlock
        { Text = header, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            BorderBrush = Brush.Parse("#DDD"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 10), Child = sp,
        };
    }

    private static WrapPanel Field(params Control[] cs)
    {
        var wp = new WrapPanel();
        foreach (var c in cs) wp.Children.Add(c);
        return wp;
    }

    private Control BuildLayout()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(14, 12, 14, 8),
            Children =
            {
                Group("切分规则 ① 当日能力（决定这盘排不排得下）",
                    Field(Lab("交接班损失 h/班", 130, "非首班每班扣的坡道时长。落在时窗上：每班少半小时，三班一天就少 1.5 小时能力"), _handover),
                    Hint("留空 = 引擎缺省 0.5h；填 0 = 人工确认本矿不扣交接（如连续接班）—— 两者不是一回事。"),
                    Field(Lab("天气降效 %", 130, "落在能力上，不是时窗上"), _weather),
                    Hint("雨雪影响的是每小时干得少（能力），检修/爆破清场/交接影响的是能开工的时段。"
                       + "两者混在一处，「今天雨大」就会被记成「今天少上了两小时班」，甘特上条形位置全错。"
                       + "上限截到 95%：全停产是不排班，不是降效 100%。"),
                    Field(Lab("　└ 分环节 %", 130), new TextBlock { Text = "采装", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) }, _dLoad,
                          new TextBlock { Text = "运输", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(12, 0, 4, 0) }, _dHaul,
                          new TextBlock { Text = "排土", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(12, 0, 4, 0) }, _dDump),
                    Hint("留空 = 跟随上面那个全盘值。三项相等时与只设全盘值逐位相同。"
                       + "运输降效对采装瓶颈的面自然不生效、对运力瓶颈的面全额生效 —— 那是按编组周期分解算的物理结论；"
                       + "编组没经周期求解（拿不到 τ_L/T_c/MF）时退回全盘值，不凭空劈。")),

                Group("切分规则 ② 面间分配（决定同样的量摊到哪几个面上）",
                    Field(Lab("面日产能工时 h/日", 130, "面日产能上限 = 编组班产 × 本值 × 降效系数"), _effHours),
                    Hint("≈ 三班合计扣掉检修/爆破清场/交接班之后还剩的工时，工程缺省 20h。"
                       + "它是配煤重分配与装箱共用的同一个闸 —— 两处取值不同，就会出现"
                       + "「配煤说移得动、装箱那边排不下」这种自相矛盾。"),
                    new TextBlock { Text = "综合配煤标准（全矿级 · 按各面采出量加权）", FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 4) },
                    Field(Lab("灰分上限 %"), _ash),
                    Field(Lab("热值下限 MJ/kg"), _cv),
                    Field(Lab("硫分上限 %"), _sulfur),
                    Hint("三项全留空 = 不设配煤锚点，按引擎缺省（灰 12.8 / 热 21.5 / 硫 0.7）。"),
                    new TextBlock { Text = "逐受矿点入仓标准（留空 = 按上面的全矿级）", FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 4) },
                    Field(_sinkPick, new TextBlock { Text = " ", Width = 6 }, _sAsh, new TextBlock { Text = " ", Width = 6 }, _sCv,
                          new TextBlock { Text = " ", Width = 6 }, _sSulfur, new TextBlock { Text = " ", Width = 8 },
                          B("设定", SetSink, bold: true), B("清除", ClearSink)),
                    new Border { Height = 130, Margin = new Thickness(0, 4, 0, 0), Child = _sinkGrid },
                    Hint("台账自带入仓标准的点不被锚点覆盖 —— 锚点是给「台账还没有这几列」用的补位。"
                       + "去向台账改过名或换过 id 之后，对不上的锚点会静默失效，那时这里会点名报出来。")),

                Group("切分规则 ③ 校核阈值（决定报不报警，不改计划）",
                    Field(Lab("备采保有下限 天", 130, "只对录了备采储量的面生效"), _minPrepared),
                    Hint("某面按当日强度采下去、剩余备采不足这个天数就预警。等真采空那天再报，"
                       + "采准（穿孔/爆破）根本来不及跟上，那个面就得停。填 0 = 人工确认不校核。")),
            },
        };

        var effectBar = new Border
        {
            Background = Brush.Parse("#F5F7FA"), Padding = new Thickness(14, 6),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _effect,
        };
        var statusBar = new Border { Padding = new Thickness(14, 2), Child = _status };
        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(14, 6, 14, 10),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children =
                {
                    B("全部清空", ResetAll, tip: "把所有锚点清成「没设过」，一律回引擎缺省"),
                    B("重新载入", Reload),
                    B("保存", SaveAll, bold: true),
                    B("关闭", Close),
                },
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(effectBar, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(effectBar);
        // 内容包 ScrollViewer + 页脚 Dock 到底：缩放≠1 时窗口长高也不会把「保存」顶出屏幕
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        return root;
    }

    // ═══════════════════ 载入 / 保存 ═══════════════════

    private static string T(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";

    internal void Reload()
    {
        CompileOverrides.Invalidate();
        _anchors = CompileOverrides.Current.Clone();

        _handover.Text = T(_anchors.HandoverRampH);
        _weather.Text = T(_anchors.WeatherDeratePct);
        _dLoad.Text = T(_anchors.LoadDeratePct);
        _dHaul.Text = T(_anchors.HaulDeratePct);
        _dDump.Text = T(_anchors.DumpDeratePct);
        _effHours.Text = T(_anchors.EffHoursPerDay);
        _ash.Text = T(_anchors.MaxAshPct);
        _cv.Text = T(_anchors.MinCalorificMJkg);
        _sulfur.Text = T(_anchors.MaxSulfurPct);
        _minPrepared.Text = T(_anchors.MinPreparedDays);

        LoadSinks();
        RefreshEffect();
        _status.Text = CompileOverrides.LastIoLabel;
    }

    private void LoadSinks()
    {
        _sinks = null;
        var conn = _conn();
        if (conn != null)
        {
            try { _sinks = SinkRegistryLoader.Load(conn); } catch { _sinks = null; }
        }

        var names = _sinks?.All.Select(s => s.Name).Where(n => n.Length > 0).OrderBy(n => n, StringComparer.Ordinal).ToList()
                    ?? new List<string>();
        _sinkPick.ItemsSource = names;
        if (names.Count > 0 && _sinkPick.SelectedIndex < 0) _sinkPick.SelectedIndex = 0;
        RefreshSinkGrid();
    }

    private void RefreshSinkGrid()
    {
        _sinkRows.Clear();
        if (_sinks == null)
        {
            // 台账读不出来时**不拿锚点当全表**：那样会让人以为库里就这几个受矿点
            foreach (var (key, lim) in _anchors.SinkBlend.OrderBy(k => k.Key, StringComparer.Ordinal))
                _sinkRows.Add(new SinkRow
                {
                    Name = key, Kind = "—",
                    Ash = T(lim.MaxAshPct) is { Length: > 0 } a ? a : "—",
                    Cv = T(lim.MinCalorificMJkg) is { Length: > 0 } c ? c : "—",
                    Sulfur = T(lim.MaxSulfurPct) is { Length: > 0 } s ? s : "—",
                    From = "锚点（去向台账没读出来，对不对得上未知）",
                });
            return;
        }

        foreach (var s in _sinks.All.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            bool hasLedger = s.HasBlendLimits;
            _anchors.SinkBlend.TryGetValue(s.Name, out var lim);
            lim ??= _anchors.SinkBlend.TryGetValue(s.Id, out var byId) ? byId : null;

            string from = hasLedger ? "台账（锚点不覆盖）" : lim is { IsEmpty: false } ? "锚点" : "按全矿级";
            _sinkRows.Add(new SinkRow
            {
                Name = s.Name, Kind = s.Kind.Label(),   // 中文名只此一份（MaterialEnumLabels），别在窗里再写一遍
                Ash = Pick(hasLedger ? s.MaxAshPct : lim?.MaxAshPct),
                Cv = Pick(hasLedger ? s.MinCalorificMJkg : lim?.MinCalorificMJkg),
                Sulfur = Pick(hasLedger ? s.MaxSulfurPct : lim?.MaxSulfurPct),
                From = from,
            });
        }

        // 对不上的锚点键要点名：台账改过名/换过 id 之后它们会静默失效，而配煤照样报「达标」
        var known = new HashSet<string>(_sinks.All.SelectMany(s => new[] { s.Name, s.Id }), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, lim) in _anchors.SinkBlend.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (known.Contains(key) || lim == null || lim.IsEmpty) continue;
            _sinkRows.Add(new SinkRow
            {
                Name = key, Kind = "—",
                Ash = Pick(lim.MaxAshPct), Cv = Pick(lim.MinCalorificMJkg), Sulfur = Pick(lim.MaxSulfurPct),
                From = "⚠ 去向台账里找不到这个键 —— 这条现在不生效",
            });
        }
    }

    private static string Pick(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "—";

    /// <summary>把界面读成一份锚点。任何一格不合法就返回原因，<b>整份不保存</b>（不写半份进配置）。</summary>
    private string? Collect(CompileAnchors into)
    {
        string? e;
        if ((e = CompileOverrides.ParseAnchor(_handover.Text, 0, 4, "交接班损失", out var hr)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_weather.Text, 0, 95, "天气降效", out var wd)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_dLoad.Text, 0, 95, "采装降效", out var dl)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_dHaul.Text, 0, 95, "运输降效", out var dh)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_dDump.Text, 0, 95, "排土降效", out var dd)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_effHours.Text, 0.5, 24, "面日产能工时", out var eh)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_ash.Text, 0, 60, "灰分上限", out var ash)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_cv.Text, 1, 40, "热值下限", out var cv)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_sulfur.Text, 0, 10, "硫分上限", out var su)) != null) return e;
        if ((e = CompileOverrides.ParseAnchor(_minPrepared.Text, 0, 365, "备采保有下限", out var mp)) != null) return e;

        into.HandoverRampH = hr; into.WeatherDeratePct = wd;
        into.LoadDeratePct = dl; into.HaulDeratePct = dh; into.DumpDeratePct = dd;
        into.EffHoursPerDay = eh;
        into.MaxAshPct = ash; into.MinCalorificMJkg = cv; into.MaxSulfurPct = su;
        into.MinPreparedDays = mp;
        return null;
    }

    private void SaveAll()
    {
        var a = _anchors.Clone();
        string? bad = Collect(a);
        if (bad != null) { _status.Text = "没保存：" + bad; return; }

        _anchors = a;
        _status.Text = CompileOverrides.Save(_anchors) ? CompileOverrides.LastIoLabel : CompileOverrides.LastIoLabel;
        RefreshSinkGrid();
        RefreshEffect();
        _echo("编制配置已保存：" + (_effect.Text ?? ""));
    }

    private void ResetAll()
    {
        _anchors = new CompileAnchors();
        _handover.Text = _weather.Text = _dLoad.Text = _dHaul.Text = _dDump.Text = "";
        _effHours.Text = _ash.Text = _cv.Text = _sulfur.Text = _minPrepared.Text = "";
        CompileOverrides.Save(_anchors);
        RefreshSinkGrid();
        RefreshEffect();
        _status.Text = "已清空全部锚点 —— 一律回引擎缺省。";
    }

    private void SetSink()
    {
        if (_sinkPick.SelectedItem is not string name || name.Length == 0)
        { _status.Text = "先选一个受矿点（去向台账没读出来时这里是空的）。"; return; }

        string? e;
        if ((e = CompileOverrides.ParseAnchor(_sAsh.Text, 0, 60, "灰分上限", out var ash)) != null) { _status.Text = e; return; }
        if ((e = CompileOverrides.ParseAnchor(_sCv.Text, 1, 40, "热值下限", out var cv)) != null) { _status.Text = e; return; }
        if ((e = CompileOverrides.ParseAnchor(_sSulfur.Text, 0, 10, "硫分上限", out var su)) != null) { _status.Text = e; return; }

        var lim = new SinkBlendAnchor { MaxAshPct = ash, MinCalorificMJkg = cv, MaxSulfurPct = su };
        if (lim.IsEmpty) { _anchors.SinkBlend.Remove(name); _status.Text = $"{name}：三项都留空 = 取消该点的锚点，按全矿级判。"; }
        else { _anchors.SinkBlend[name] = lim; _status.Text = $"{name} 已设（点「保存」才落盘）。"; }
        RefreshSinkGrid();
    }

    private void ClearSink()
    {
        if (_sinkGrid.SelectedItem is SinkRow row && _anchors.SinkBlend.Remove(row.Name))
        { _status.Text = $"{row.Name} 的锚点已清（点「保存」才落盘）。"; RefreshSinkGrid(); return; }
        _status.Text = "先在下表里选中一行（只有「来源=锚点」的那些清得掉）。";
    }

    /// <summary>把当前这份锚点套到一份空盘子上，回显"现在生效的到底是什么"。</summary>
    private void RefreshEffect()
    {
        var probe = _anchors.Clone();
        Collect(probe);                                  // 不合法的格子在这里被忽略，报错留给保存那一步
        string s = CompileOverrides.ApplyTo(new ExploderConfig(), probe);
        _effect.Text = s.Length > 0 ? s : "尚未设任何锚点 —— 全部按引擎缺省（交接 0.5h/班 · 不降效 · 面日产能工时 20h · 不校核备采）。";
    }

    // ── 自检钩子用 ──
    internal string EffectText => _effect.Text ?? "";
    internal string StatusText => _status.Text ?? "";
    internal int SinkRowCount => _sinkRows.Count;
}
