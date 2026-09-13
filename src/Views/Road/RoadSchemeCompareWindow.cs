using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.RoadLayout;

namespace PitMine3D.Kylin.Views.Road;

/// <summary>
/// 「运量驱动布线」的方案比选窗（忠实原 <c>MineAssLib.Views.RoadSchemeCompareWindow</c>）：
/// 紧凑/均衡/单线 三套方案 + 评分 + 可行/问题；切优化目标即重算重排（推荐置顶）；
/// 「采用此方案」→ 以各线起坡点为种子跑自动布线出真中线、写落地缓存（由调用方的 adopt 回调做）。
/// </summary>
internal sealed class RoadSchemeCompareWindow : Window
{
    internal sealed class SchemeRow
    {
        public RoadLayoutScheme Scheme { get; init; } = null!;
        public string Name => Scheme.Name;
        public int Lines => Scheme.Lines.Count;
        public string Lanes => string.Join("+", Scheme.Lines.Select(l => l.LaneCount));
        public string CapexKm => $"{Scheme.TotalCapexProxy / 1000.0:0.00}";
        public string Cost => Scheme.TotalHaulCost > 0 ? $"{Scheme.TotalHaulCost / 10000.0:0.0}万" : "—";
        public string Score => $"{Scheme.TotalScore:0.0}";
        public string Feasible => Scheme.Feasible ? "✓" : "✗";
        public string Issue
        {
            get
            {
                var v = Scheme.Violations;
                if (v == null || v.Count == 0) return "—";
                return v.Count == 1 ? v[0] : $"{v[0]}（等 {v.Count} 项）";
            }
        }
    }

    private readonly Func<string, RoadLayoutResult> _resolve;
    private readonly Action<RoadLayoutScheme> _adopt;
    private readonly ObservableCollection<SchemeRow> _rows = new();
    private readonly ComboBox _objBox;
    private readonly DataGrid _grid;
    private readonly TextBox _detail;

    public RoadSchemeCompareWindow(RoadLayoutResult initial, string objective, Func<string, RoadLayoutResult> resolve, Action<RoadLayoutScheme> adopt)
    {
        _resolve = resolve; _adopt = adopt;
        Title = "运量驱动布线 — 方案比选";
        RoadUi.Place(this, 900, 560);

        _objBox = RoadUi.Combo(RoadLayoutSolver.Objectives, 160);
        _objBox.SelectedItem = RoadLayoutSolver.NormalizeObjective(objective);
        _objBox.SelectionChanged += (_, _) => Resolve();
        var top = RoadUi.Row(RoadUi.Lbl("优化目标："), _objBox, RoadUi.Hint("（切换即重算并重排，推荐方案置顶）"));
        top.Margin = new Thickness(12, 12, 12, 6);

        _grid = RoadUi.Table(new (string, string, double)[]
        {
            ("方案", nameof(SchemeRow.Name), 180), ("线数", nameof(SchemeRow.Lines), 50), ("车道", nameof(SchemeRow.Lanes), 90),
            ("展线km", nameof(SchemeRow.CapexKm), 80), ("运营成本", nameof(SchemeRow.Cost), 90), ("评分", nameof(SchemeRow.Score), 60),
            ("可行", nameof(SchemeRow.Feasible), 50), ("问题（不可行/告警原因）", nameof(SchemeRow.Issue), 300),
        }, multi: false);
        _grid.ItemsSource = _rows; _grid.Height = 150; _grid.Margin = new Thickness(12, 0, 12, 6);
        _grid.SelectionChanged += (_, _) => ShowDetail();

        _detail = RoadUi.Mono2("", 12); _detail.Margin = new Thickness(12, 0, 12, 6); _detail.IsReadOnly = true; _detail.AcceptsReturn = true; _detail.TextWrapping = TextWrapping.NoWrap;

        var adoptBtn = RoadUi.Btn("采用此方案（出真中线 → 可直接【坑线落地】）", Adopt, 260, primary: true);
        ToolTip.SetTip(adoptBtn, "以方案各线起坡点为种子跑一遍自动布线出真中线，画进「运输坑线_预览」并写入落地缓存；\n命令行会逐线回显是否贯通、展线长与落地缓存段数。");
        var foot = RoadUi.Foot(adoptBtn, RoadUi.Btn("关闭", Close, 80));
        Content = RoadUi.Rows(top, new Grid { Children = { _grid }, Height = 160 }, _detail, foot);
        Populate(initial);
    }

    private void Resolve()
    {
        if (_objBox.SelectedItem is not string obj) return;
        try { Populate(_resolve(obj)); }
        catch (Exception ex) { _detail.Text = "重算失败：" + ex.Message; }
    }

    private void Populate(RoadLayoutResult result)
    {
        _rows.Clear();
        if (result == null || !result.Success) { _detail.Text = "✗ 求解失败：" + (result?.Error ?? "未知"); return; }
        foreach (var s in result.Schemes) _rows.Add(new SchemeRow { Scheme = s });
        _grid.SelectedItem = _rows.FirstOrDefault();
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (_grid.SelectedItem is not SchemeRow row) { _detail.Text = ""; return; }
        var s = row.Scheme;
        var sb = new StringBuilder();
        string obj = _objBox.SelectedItem as string ?? RoadLayoutSolver.Objectives[0];
        sb.AppendLine($"【{s.Name}】{(s.Feasible ? "✓ 可行" : "✗ 需调整")}   评分 {s.TotalScore:0.0}（目标：{obj}）");
        sb.AppendLine($"线路 {s.Lines.Count} 条 · 展线合计 {s.TotalCapexProxy / 1000.0:0.00} km · 运营成本 {(s.TotalHaulCost > 0 ? $"{s.TotalHaulCost / 10000.0:0.0} 万元/期" : "—(未填运价)")}");
        sb.AppendLine("口径：运营成本只算吨公里（总运量 × 链运距 × 单价），各方案共用同一条链几何与同一总运量 →")
          .AppendLine("      数值必然相同，不参与排序；排序看【基建工程量(展线合计＝线数×单链长) · 车道利用率 · 并行线分流冗余】；")
          .AppendLine("      压矿属块体侧尚未接入（恒 0，不计分）。")
          .AppendLine();
        foreach (var l in s.Lines)
            sb.AppendLine($"  {l.Id}: {l.LaneCount} 车道 · 年运力 {l.CapacityTons / 10000.0:0} 万t · 利用率 {l.Utilization * 100:0}% · 展线 {l.LengthM:0} m");
        if (s.Lines.Count > 0)
        {
            sb.AppendLine().AppendLine($"坑线逐段（{s.Lines[0].Id}）：" + (s.Lines.Count > 1 ? "（其余并行线共用同一条链几何，只差车道数）" : ""));
            foreach (var sg in s.Lines[0].Segments)
            {
                var p = sg.Centerline.Count > 0 ? sg.Centerline[0] : (X: 0.0, Y: 0.0, Z: 0.0);
                sb.AppendLine($"  {sg.FromLevel:0}→{sg.ToLevel:0}m  {RampRouteGenerator.FormLabel(sg.Form)}  纵坡{sg.GradePct:0.#}%  起坡({p.X:0.#},{p.Y:0.#},{p.Z:0.#})  中线{sg.Centerline.Count}点{(sg.Centerline.Count >= 2 ? "" : "（仅起坡点）")}");
            }
        }
        foreach (var v in s.Violations) sb.AppendLine($"  ⚠ {v}");
        _detail.Text = sb.ToString();
    }

    private async void Adopt()
    {
        if (_grid.SelectedItem is not SchemeRow row) { await RoadUi.Info(this, "方案比选", "请先在表中选中一套方案。"); return; }
        _adopt(row.Scheme);
    }
}
