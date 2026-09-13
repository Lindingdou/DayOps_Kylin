using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Mining;

internal sealed class DumpAdvanceHost
{
    public Func<System.Data.Common.DbConnection?> Db = () => null;
    /// <summary>在图上画本期形态：(环 [x,y,z,...] 列表, 每环颜色 0xRRGGBB)。</summary>
    public Action<IReadOnlyList<double[]>, IReadOnlyList<uint>> ShowOverlay = (_, _) => { };
    public Action ClearOverlay = () => { };
    public Action<string, bool> Echo = (_, _) => { };
}

/// <summary>
/// 「排土场按量推进」（忠实原 BlockModelLib <c>DumpAdvanceDialog</c>）：给排弃量 → 沿「排土条带」的带序逐带吃下去，
/// 给出推进到第几级第几带、逐级推进距离、剩余库容、排不下的余量，并把本期形态画到图上。
/// 与采场侧「刀量切割」对称 —— 那边给量沿工作线切进去，这边给量沿排土线堆出来。
/// ★ 吃形态、不造形态：排土场得先用「排土条带」切过。只算不写，不动库容台账。算法 <see cref="DumpAdvanceByVolume"/>（原版逐行移植）。
/// </summary>
internal sealed class DumpAdvanceWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly DumpAdvanceHost _host;
    private readonly ComboBox _site = new() { MinWidth = 420 };
    private readonly TextBox _volume = RoadUi.NumBox(500000, 120);
    private readonly ComboBox _volumeUnit = new() { MinWidth = 220 };
    private readonly TextBox _kr = RoadUi.NumBox(1.25, 60);
    private readonly ComboBox _order = new() { MinWidth = 420 };
    private readonly TextBox _filled = RoadUi.NumBox(0, 120);
    private readonly TextBlock _siteInfo = RoadUi.Hint("");
    private readonly TextBlock _status = RoadUi.Text("", 12);
    private readonly DataGrid _detail = RoadUi.Table(new (string, string, double)[]
    {
        ("位置编号", "Code", 200), ("级", "Level", 40), ("幅", "Panel", 40), ("带", "Step", 40),
        ("本带库容 m³", "CapacityText", 100), ("填入 m³", "FillText", 100), ("填充率", "FractionText", 90), ("台阶高 m", "BenchText", 70), ("推进宽 m", "WidthText", 76),
    });
    private readonly Button _run, _show;
    private DumpAdvanceByVolume.DumpAdvanceResult? _last;

    private sealed class SiteItem
    {
        public long RegionId; public string Name = ""; public string Category = ""; public List<DumpStrip> Strips = new();
        public double TotalCapacityM3 => Strips.Sum(s => s.CapacityM3);
        public string CategoryZh => Category == "internal_dump" ? "内排" : "外排";
        public override string ToString() => $"{Name}（{CategoryZh} · {Strips.Count} 个位置 · 总库容 {TotalCapacityM3:N0} m³）";
    }
    internal sealed class FillRow
    {
        public string Code { get; set; } = ""; public long Level { get; set; } public long Panel { get; set; } public long Step { get; set; }
        public string CapacityText { get; set; } = ""; public string FillText { get; set; } = ""; public string FractionText { get; set; } = "";
        public string BenchText { get; set; } = ""; public string WidthText { get; set; } = "";
    }

    public DumpAdvanceWindow(DumpAdvanceHost host)
    {
        _host = host;
        Title = "排土场按量推进 · 量 → 形态";
        RoadUi.Place(this, 900, 640);

        _volumeUnit.ItemsSource = new[] { "实方 V实（采场挖出来的）", "占容方 V容（排土场里占掉的）" }; _volumeUnit.SelectedIndex = 0;
        _order.ItemsSource = new[] { "并肩推进（整条排土线一带一带往外长 · 现场口径）", "逐级排满（一级推到边界再上一级）" }; _order.SelectedIndex = 0;

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(12, 10, 12, 4) };
        int r = 0;
        void Add(string label, Control c)
        {
            form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (label.Length > 0) { var tb = RoadUi.Lbl(label); tb.VerticalAlignment = VerticalAlignment.Center; tb.Margin = new Thickness(0, 4, 8, 4); Grid.SetRow(tb, r); Grid.SetColumn(tb, 0); form.Children.Add(tb); }
            c.Margin = new Thickness(0, 3, 0, 3); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(c); r++;
        }
        Add("排土场:", _site);
        Add("", _siteInfo);
        Add("排弃量:", RoadUi.Row(_volume, _volumeUnit, RoadUi.Lbl("　Kr"), _kr));
        Add("推进次序:", _order);
        Add("起始已填(m³占容):", _filled);
        Add("", RoadUi.Hint("口径：表里存的是【占容方】= 走向长 × 条带宽 × 台阶高。给实方时按 V容 = V实 × Kr 换算，只换这一次。\n最后一带按比例部分填，不整带进位；带吃完还有余量会明说「排不下」，不静默截断。\n本窗口只算不写：不改排土场已填量、不改条带台账。"));
        _run = RoadUi.Btn("按量推进", OnRun, 110, primary: true);
        _show = RoadUi.Btn("在图上显示形态", OnShow, 130); _show.IsEnabled = false;
        var clear = RoadUi.Btn("清除显示", () => { _host.ClearOverlay(); _status.Text = "已清除图上显示。"; }, 90);
        var close = RoadUi.Btn("关闭", Close, 80);
        var btns = RoadUi.Row(_run, _show, clear, close); btns.Margin = new Thickness(12, 4, 12, 4);
        _status.TextWrapping = TextWrapping.Wrap;
        var statusScroll = new ScrollViewer { Content = _status, MaxHeight = 170, Margin = new Thickness(12, 0, 12, 8) };
        var top = new StackPanel(); top.Children.Add(form); top.Children.Add(btns);
        Content = RoadUi.Rows(top, _detail, statusScroll, new Panel());

        _site.SelectionChanged += (_, _) => ShowSiteInfo();
        ReloadSites();
    }

    public void Reload() => ReloadSites();

    private void ReloadSites()
    {
        var items = new List<SiteItem>();
        List<DumpStrip> all;
        try { all = DumpStripRepo.All(_host.Db()); }
        catch (Exception ex) { _status.Text = "读潜在排土位置失败：" + ex.Message; _run.IsEnabled = false; return; }
        foreach (var g in all.GroupBy(s => s.RegionId))
        {
            var first = g.First();
            items.Add(new SiteItem { RegionId = g.Key, Name = string.IsNullOrWhiteSpace(first.RegionName) ? $"排土场#{g.Key}" : first.RegionName, Category = first.Category, Strips = g.ToList() });
        }
        var keep = (_site.SelectedItem as SiteItem)?.RegionId;
        _site.ItemsSource = items;
        if (items.Count == 0)
        {
            _status.Text = "库里没有潜在排土位置 —— 先用同组的「排土条带」把排土场切一遍。按量推进是【吃】条带的：形态由条带定，本窗口只回答\"这些量推到第几带\"。";
            _run.IsEnabled = false; return;
        }
        _run.IsEnabled = true;
        _site.SelectedIndex = Math.Max(0, items.FindIndex(i => i.RegionId == keep));
    }

    private void ShowSiteInfo()
    {
        if (_site.SelectedItem is not SiteItem s) { _siteInfo.Text = ""; return; }
        int levels = s.Strips.Select(x => x.LevelIndex).Distinct().Count();
        long steps = s.Strips.Count == 0 ? 0 : s.Strips.Max(x => x.StepIndex);
        double h = s.Strips.Where(x => x.BenchHeightM > 0).Select(x => x.BenchHeightM).DefaultIfEmpty(0).Average();
        double w = s.Strips.Where(x => x.StripWidthM > 0).Select(x => x.StripWidthM).DefaultIfEmpty(0).Average();
        _siteInfo.Text = $"形态：{levels} 级台阶 × 最多 {steps} 带 · 平均台阶高 {h:0.#} m · 平均推进宽 {w:0.#} m · 总库容 {s.TotalCapacityM3:N0} m³（吃「排土条带」的位置清单）";
        try
        {
            var conn = _host.Db();
            if (conn != null)
            {
                var site = GeoDataQueries.GetDumpSites(conn).FirstOrDefault(d => string.Equals(d.Name, s.Name, StringComparison.OrdinalIgnoreCase));
                if (site != null) _filled.Text = (site.CurrentFilledWanM3 * 10000).ToString("0", Inv);
            }
        }
        catch { /* 台账没接上就保持用户填的 */ }
    }

    private void OnRun()
    {
        if (_site.SelectedItem is not SiteItem s) { _status.Text = "请先选一个排土场。"; return; }
        double vol = Num(_volume.Text);
        if (vol <= 0) { _status.Text = "排弃量要大于 0。"; return; }
        bool solid = _volumeUnit.SelectedIndex == 0;
        double kr = solid ? Math.Max(1.0, Num(_kr.Text)) : 1.0;
        double filled = Math.Max(0, Num(_filled.Text));
        var order = _order.SelectedIndex == 1 ? DumpAdvanceByVolume.Order.LevelByLevel : DumpAdvanceByVolume.Order.StepAbreast;
        _last = DumpAdvanceByVolume.Advance(s.Strips, vol, kr, order, filled);
        var slice = _last.Slices.FirstOrDefault();
        if (!_last.Ok || slice == null) { _status.Text = _last.Message; _detail.ItemsSource = null; _show.IsEnabled = false; return; }
        _detail.ItemsSource = slice.Fills.Select(f => new FillRow
        {
            Code = f.Strip.Code, Level = f.Strip.LevelIndex, Panel = f.Strip.PanelIndex, Step = f.Strip.StepIndex,
            CapacityText = f.Strip.CapacityM3.ToString("N0", Inv), FillText = f.FillM3.ToString("N0", Inv),
            FractionText = f.IsPartial ? $"{f.Fraction * 100:0.#}%（部分）" : "100%",
            BenchText = f.Strip.BenchHeightM.ToString("0.#", Inv), WidthText = (f.Strip.StripWidthM * f.Fraction).ToString("0.#", Inv),
        }).ToList();
        string adv = slice.AdvanceMByLevel.Count == 0 ? "—" : string.Join(" · ", slice.AdvanceMByLevel.OrderBy(kv => kv.Key).Select(kv =>
        {
            slice.LeadAdvanceMByLevel.TryGetValue(kv.Key, out double lead);
            return $"L{kv.Key} 全级平均 {kv.Value:0.#} m / 领先幅 {lead:0.#} m";
        }));
        string krText = solid ? $"实方 {vol:N0} × Kr {kr:0.##} = 占容方 {slice.RequestedM3:N0} m³" : $"占容方 {slice.RequestedM3:N0} m³";
        _status.Text = $"{krText}\n排入 {slice.PlacedM3:N0} m³，占 {slice.TouchedCells} 个位置，推进到 第{slice.TopLevel}级·第{slice.TopStep}带\n"
                     + $"逐级推进：{adv}\n　　（全场对照 V容/(L排中位×h排中位) = {slice.EquivalentAdvanceM:0.#} m —— 只在一级一幅等宽时才与逐级值相符）\n"
                     + $"本场：总库容 {_last.TotalCapacityM3:N0} m³ · 累计已填 {_last.PlacedM3:N0} m³ · 剩余 {_last.RemainingM3:N0} m³\n"
                     + (slice.SiteFull ? $"⚠ 排满：还有 {slice.OverflowM3:N0} m³ 排不下 —— 需另找去向或扩场" : _last.Message)
                     + (_last.Notes.Count > 0 ? "\n" + string.Join("\n", _last.Notes) : "");
        _show.IsEnabled = slice.Fills.Count > 0;
        _host.Echo($"排土场按量推进：{s.Name} 排入 {slice.PlacedM3:N0} m³ → 推进到 第{slice.TopLevel}级·第{slice.TopStep}带" + (slice.SiteFull ? $"，排不下 {slice.OverflowM3:N0} m³" : ""), slice.SiteFull);
    }

    private void OnShow()
    {
        var slice = _last?.Slices.FirstOrDefault();
        if (slice == null) { _status.Text = "没有可显示的结果。"; return; }
        var rings = new List<double[]>(); var colors = new List<uint>(); int skipped = 0;
        foreach (var f in slice.Fills)
        {
            var ring = CellRing(f.Strip);
            if (ring.Length < 9) { skipped++; continue; }
            rings.Add(ring); colors.Add(f.IsPartial ? 0xFFA000u : 0x00A0FFu);   // 部分填=橙，满填=蓝
        }
        if (rings.Count == 0) { _status.Text = "这些带的坡顶/坡底轨没存进台账，画不出来（重切一次条带即可带上轨）。"; return; }
        try { _host.ShowOverlay(rings, colors); }
        catch (Exception ex) { _status.Text = "显示失败：" + ex.Message; return; }
        _status.Text += $"\n已在图上画出 {rings.Count} 个位置的轮廓（橙=部分填）" + (skipped > 0 ? $"；{skipped} 个位置没有轨数据，未画出" : "");
    }

    private static double[] CellRing(DumpStrip s)
    {
        var crest = Flat(s.CrestJson); var toe = Flat(s.ToeJson);
        if (crest.Length < 6 || toe.Length < 6) return Array.Empty<double>();
        var ring = new List<double>(crest.Length + toe.Length + 3);
        ring.AddRange(crest);
        for (int i = toe.Length - 3; i >= 0; i -= 3) { ring.Add(toe[i]); ring.Add(toe[i + 1]); ring.Add(toe[i + 2]); }
        ring.Add(crest[0]); ring.Add(crest[1]); ring.Add(crest[2]);
        return ring.ToArray();
    }
    private static double[] Flat(string? json)
    {
        try { var v = System.Text.Json.JsonSerializer.Deserialize<double[]>(json ?? "[]"); return v is { Length: >= 6 } && v.Length % 3 == 0 ? v : Array.Empty<double>(); }
        catch { return Array.Empty<double>(); }
    }
    private static double Num(string? t) => double.TryParse(t, NumberStyles.Any, Inv, out var v) ? v : 0;
}
