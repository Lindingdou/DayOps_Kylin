using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 机群总览 · 领导驾驶舱(忠实原 EquipmentFleetCockpitWindow): 一屏给出全机群健康度(红绿灯)、
/// 产能瓶颈、可解锁产能、需关注设备清单(双击下钻打开设备能力分析)。数据: equipment × 月度 KPI × 产能。
/// </summary>
public partial class EquipmentFleetCockpitWindow : Window
{
    private readonly GeoDbContext _ctx;

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentFleetCockpitWindow() { _ctx = null!; InitializeComponent(); }

    public EquipmentFleetCockpitWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadData();
    }

    private void LoadData()
    {
        GeoDbViews.EqFleetCockpitResult r;
        try { r = GeoDbViews.EqComputeFleetCockpit(_ctx.Conn); }
        catch (Exception ex) { statusText.Text = "设备台账不可用: " + ex.Message; return; }
        if (!r.HasData) { statusText.Text = r.StatusText; return; }

        watchList.ItemsSource = r.Watch;
        int total = r.Total;
        tileGreen.Text = r.Green.ToString(); tileGreenPct.Text = $"占 {100.0 * r.Green / total:F0}%";
        tileYellow.Text = r.Yellow.ToString(); tileYellowPct.Text = $"占 {100.0 * r.Yellow / total:F0}%";
        tileRed.Text = r.Red.ToString(); tileRedPct.Text = $"占 {100.0 * r.Red / total:F0}%";
        tileOee.Text = $"{r.AvgOee * 100:F0}%"; tileOeeSub.Text = $"可用率达标率 {r.AvailPass:F0}%（≥90%）";
        tileUnlock.Text = $"{r.UnlockWan / 1e4:F1}";
        tileUnlockSub.Text = r.FleetCapWan > 0 ? $"≈当前产能 {100 * r.UnlockWan / r.FleetCapWan:F0}% · 补齐各机最短板(理论上限)" : "补齐各机最短板(理论上限)";

        verdictBanner.Background = new SolidColorBrush(Color.Parse(r.VerdictColor));
        verdictIcon.Text = r.VerdictIcon; verdictHeadline.Text = r.Headline; verdictAction.Text = r.Action;

        categoryChart.Categories = r.CategoryPass.Select(c => c.Category).ToList();
        categoryChart.Series = new List<ChartSeries>
        {
            new() { Name = "可用率达标率 %", Kind = SeriesKind.Bar, Color = Color.Parse("#42A5F5"), Values = r.CategoryPass.Select(c => (double?)c.PassPct).ToList(), ShowValueLabels = true, ValueFormat = "0" },
        };
        categoryChart.Refresh();

        asOfText.Text = r.AsOfText;
        statusText.Text = r.StatusText;
    }

    /// <summary>双击下钻: 原窗口 MouseDoubleClick → new EquipmentCapabilityWindow().Show()。</summary>
    private void OnWatchDoubleTapped(object? sender, TappedEventArgs e)
        => GeoDbWindows.Show(_ctx, () => new EquipmentCapabilityWindow(_ctx));
}
