using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 设备智能编组(忠实原 EquipmentDispatchWindow): 基于斗容/载重/循环时间的铲车匹配矩阵 +
/// 月计划目标分解 → FleetOptimizer 整数车队选型 → 推荐编组卡片(M/M/c 排队论指标 + 瓶颈研判)。
/// </summary>
public partial class EquipmentDispatchWindow : Window
{
    private readonly GeoDbContext _ctx;
    private List<EqDispatchRule> _rawRules = new();

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentDispatchWindow() { _ctx = null!; InitializeComponent(); }

    public EquipmentDispatchWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadData();
    }

    private void LoadData()
    {
        try { _rawRules = EqLoadDispatchRules(_ctx.Conn); }
        catch (Exception ex) { statusText.Text = "编组规则不可用: " + ex.Message; return; }
        Recompute();
        statusText.Text = $"加载 {_rawRules.Count} 条编组规则";
    }

    private void OnRecalcClick(object? sender, RoutedEventArgs e) => Recompute();

    private void Recompute()
    {
        if (_rawRules.Count == 0) return;
        matchingGrid.ItemsSource = _rawRules.OrderByDescending(x => x.EfficiencyScore).Select(r => new EqDispatchRuleVm(r)).ToList();
        BuildRecommendations(ParsePlan());
    }

    private (double StrippingWanM3, double DistanceKm, int Days) ParsePlan()
    {
        double.TryParse(planStrippingInput.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var stripping);
        double.TryParse(planDistanceInput.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var distance);
        int.TryParse(planDaysInput.Text, out var days);
        if (days <= 0) days = 30;
        return (stripping, distance, days);
    }

    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    private void BuildRecommendations((double StrippingWanM3, double DistanceKm, int Days) plan)
    {
        recommendationPanel.Children.Clear();

        var header = new Border { Background = B("#E3F2FD"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 12) };
        var hs = new StackPanel();
        hs.Children.Add(new TextBlock { Text = "📋 月度生产计划", FontWeight = FontWeight.Bold, FontSize = 13, Foreground = B("#1976D2") });
        hs.Children.Add(new TextBlock { Text = $"目标剥离:{plan.StrippingWanM3:F0} 万 m³  ·  平均运距:{plan.DistanceKm:F2} km  ·  工作日:{plan.Days} 天", FontSize = 11, Foreground = B("#666666"), Margin = new Thickness(0, 4, 0, 0) });
        hs.Children.Add(new TextBlock { Text = $"日均产能需求:{plan.StrippingWanM3 / plan.Days:F2} 万 m³/天", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = B("#333333"), Margin = new Thickness(0, 2, 0, 0) });
        header.Child = hs;
        recommendationPanel.Children.Add(header);

        double dailyTargetM3 = plan.Days > 0 ? plan.StrippingWanM3 * 1e4 / plan.Days : 0;
        if (_rawRules.Count == 0 || dailyTargetM3 <= 0)
        {
            recommendationPanel.Children.Add(NoteBlock("请填写月度剥离量与工作日，并确保已加载编组规则。"));
            return;
        }

        // 整数车队选型优化(FleetOptimizer)
        var opt = FleetOptimizer.Optimize(new FleetOptInput
        {
            DailyTargetM3 = dailyTargetM3,
            HaulDistanceKm = plan.DistanceKm,
            Efficiency = EqAverageEfficiency(EqLoadKpi(_ctx.Conn)),
            Rules = _rawRules.Select(r => r.ToFleetRule()).ToList(),
            ShovelInventory = EqInventoryByModel(_ctx.Conn),
        });
        var payloadByShovel = _rawRules.GroupBy(r => r.ShovelModel).ToDictionary(g => g.Key, g => g.First().ShovelPayloadT);

        recommendationPanel.Children.Add(BuildSummaryCard(opt));
        foreach (var g in opt.Groups) recommendationPanel.Children.Add(BuildGroupCard(g, payloadByShovel.TryGetValue(g.Rule.ShovelModel, out var p) ? p : 0));
        foreach (var n in opt.Notes) recommendationPanel.Children.Add(NoteBlock(n));
    }

    private static Border BuildSummaryCard(FleetOptResult opt)
    {
        bool met = opt.TargetMet;
        var card = new Border { Background = B(met ? "#E8F5E9" : "#FDECEA"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 12) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = met ? "✓ 优化编组方案（满足日产能）" : "⚠ 优化编组方案（在籍不足，未满足）", FontWeight = FontWeight.Bold, FontSize = 13, Foreground = B(met ? "#2E7D32" : "#C62828") });
        sp.Children.Add(new TextBlock
        {
            Text = $"电铲合计 {opt.TotalShovels} 台 · 卡车合计 {opt.TotalTrucks} 辆 · 可达 {opt.TotalDailyM3 / 1e4:0.00} 万m³/天（目标 {opt.TargetM3 / 1e4:0.00}，达成 {opt.FillRatio * 100:0}%）",
            FontSize = 12, Foreground = B("#333333"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        card.Child = sp;
        return card;
    }

    private static Border BuildGroupCard(FleetGroup g, double shovelPayloadT)
    {
        var rule = g.Rule;
        var card = new Border { Background = Brushes.White, BorderBrush = B("#E0E0E0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 10) };
        var stack = new StackPanel();

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleText = new TextBlock { Text = $"⛏️ {rule.ShovelModel} × {g.ShovelCount} 台", FontWeight = FontWeight.Bold, FontSize = 14, Foreground = B("#263238") };
        var scoreBadge = new Border { Background = B(EqScoreColor(rule.EfficiencyScore)), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2), Child = new TextBlock { Text = $"评分 {rule.EfficiencyScore}", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeight.Bold } };
        Grid.SetColumn(scoreBadge, 1);
        titleRow.Children.Add(titleText); titleRow.Children.Add(scoreBadge);
        stack.Children.Add(titleRow);

        stack.Children.Add(new TextBlock
        {
            Text = $"斗容 {rule.ShovelBucketM3} m³ · 单斗负载 {shovelPayloadT} t · 单组日产能 {g.GroupDailyM3 / 1e4:0.00} 万m³/台",
            FontSize = 11, Foreground = B("#666666"), Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap,
        });

        var recRow = new Border { Background = B("#F5F5F5"), CornerRadius = new CornerRadius(3), Padding = new Thickness(8) };
        var rec = new StackPanel();
        rec.Children.Add(new TextBlock { Text = "▶ 优化配置", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = B("#388E3C") });
        rec.Children.Add(Bullet($"{g.ShovelCount} 台 {rule.ShovelModel}，每台配 {g.TrucksPerShovel} 辆 {rule.TruckModel}（装满需 {rule.BucketLoadsPerTruck:F1} 斗）"));
        rec.Children.Add(Bullet($"卡车小计：{g.TotalTrucks} 辆 · 本型日产能 {g.TotalDailyM3 / 1e4:0.00} 万m³"));
        rec.Children.Add(new TextBlock
        {
            Text = $"• 有效循环 {g.EffectiveCycleMin:F0} 分钟 · 小时趟次 ≈ {60 / Math.Max(1, g.EffectiveCycleMin):F1} · 装车节拍 {g.LoadTimeMin:0.0} 分/车" + (g.Inventory >= 0 ? $" · 在籍 {g.Inventory} 台" : ""),
            FontSize = 11, Foreground = B("#666666"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        rec.Children.Add(new TextBlock
        {
            Text = $"• 排队论 M/M/c：匹配系数 MF={g.MatchFactor:0.00} · 铲利用率 {g.ShovelUtil * 100:0}% · 车利用率 {g.TruckUtil * 100:0}% · 排队概率 {g.WaitProbability * 100:0}%",
            FontSize = 11, Foreground = B("#666666"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        string bneckColor = g.Bottleneck.Contains("均衡") ? "#388E3C" : g.Bottleneck.Contains("铲能力") ? "#C62828" : "#F57C00";
        rec.Children.Add(new Border
        {
            Background = B(bneckColor), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0),
            Child = new TextBlock { Text = $"瓶颈研判：{g.Bottleneck}", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeight.Bold },
        });
        if (g.InventoryShort)
            rec.Children.Add(new TextBlock { Text = $"⚠ 需 {g.ShovelCount} 台 > 在籍 {g.Inventory} 台", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = B("#C62828"), Margin = new Thickness(0, 3, 0, 0) });
        recRow.Child = rec;
        stack.Children.Add(recRow);
        card.Child = stack;
        return card;
    }

    private static TextBlock Bullet(string text) => new() { Text = "• " + text, FontSize = 12, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private static TextBlock NoteBlock(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = B("#888888"), Margin = new Thickness(0, 2, 0, 0) };
}

/// <summary>匹配矩阵行(原 DispatchRuleVm; EfficiencyBrush 供进度条着色)。</summary>
public sealed class EqDispatchRuleVm
{
    public string ShovelModel { get; }
    public double ShovelBucketM3 { get; }
    public double ShovelPayloadT { get; }
    public string TruckModel { get; }
    public double TruckPayloadT { get; }
    public double BucketLoadsPerTruck { get; }
    public int RecommendedTruckCount { get; }
    public double CycleTimeMin { get; }
    public int EfficiencyScore { get; }
    public IBrush EfficiencyBrush => new SolidColorBrush(Color.Parse(EqScoreColor(EfficiencyScore)));

    public EqDispatchRuleVm(EqDispatchRule r)
    {
        ShovelModel = r.ShovelModel; ShovelBucketM3 = r.ShovelBucketM3; ShovelPayloadT = r.ShovelPayloadT; TruckModel = r.TruckModel;
        TruckPayloadT = r.TruckPayloadT; BucketLoadsPerTruck = r.BucketLoadsPerTruck; RecommendedTruckCount = r.RecommendedTruckCount;
        CycleTimeMin = r.CycleTimeMin; EfficiencyScore = r.EfficiencyScore;
    }
}
