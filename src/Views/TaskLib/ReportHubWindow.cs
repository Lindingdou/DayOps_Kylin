// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportHubWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using Avalonia;
using Avalonia.Controls;
using PitMine3D.Kylin.TaskLib.Reporting;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 生产报告 —— 报表功能的**唯一入口**。四个页签＝一条链：
///   指标/计算规则（定义怎么算） → 报表模板（定义长什么样） → 报表中心（一键生成 日/周/月） → 存档（回溯·两期对比）。
///
/// <para>此前这四块是四个各自独立的窗，收成页签后跨窗刷新由本窗一处转接（见下面四个 Wire）。</para>
/// <b>报表内容一行没动</b>：模板、指标、取数（<see cref="FactSource"/>）、渲染（<see cref="ReportEngine"/> / <see cref="ReportViewBuilder"/>）全部原样。
/// </summary>
public sealed class ReportHubWindow : Window
{
    /// <summary>页签副标题：抬头跟着当前页走。索引与 TabItem 的顺序一一对应。</summary>
    private static readonly string[] Subtitles =
    {
        "一键生成 · 日/周/月 × 模板 × 范围 → 纸张预览 · 导出 PDF/Word · 打印",
        "定制样式 · 绑定指标 · 存为模板 → 供报表中心一键生成",
        "内置只读 · 自定义(结构化/公式)可增删改 · 供报表模板绑定",
        "回溯历史 · 按当前数据重生成 · 选两期对比(环比/趋势)",
    };

    private readonly TextBlock subtitleText;
    private readonly TabControl tabs = new() { Margin = new Thickness(10, 8, 10, 10), BorderThickness = new Thickness(0), Background = Avalonia.Media.Brushes.Transparent };
    private readonly ReportCenterView viewCenter = new();
    private readonly ReportTemplateView viewTemplate = new();
    private readonly IndicatorLibraryView viewIndicator = new();
    private readonly ReportArchiveView viewArchive = new();

    public ReportHubWindow()
    {
        Title = "生产报告 — 日常生产组织";
        TaskUi.Place(this, 1280, 820);
        MinWidth = 1120; MinHeight = 620;

        var header = TaskUi.Header("生产报告", Subtitles[0]);
        subtitleText = (TextBlock)((StackPanel)header.Child!).Children[1];

        // 四页签＝一条链：定指标 → 定模板 → 出报表 → 归档回溯
        tabs.Items.Add(new TabItem { Header = "报表中心", Content = viewCenter });
        tabs.Items.Add(new TabItem { Header = "报表模板", Content = viewTemplate });
        tabs.Items.Add(new TabItem { Header = "指标 / 计算规则", Content = viewIndicator });
        tabs.Items.Add(new TabItem { Header = "存档", Content = viewArchive });
        tabs.SelectionChanged += (_, e) =>
        {
            // TabControl 的 SelectionChanged 会被页内的 ListBox/ComboBox 冒泡上来，不挡住的话点一下模板列表就会去改抬头
            if (!ReferenceEquals(e.Source, tabs)) return;
            int i = tabs.SelectedIndex;
            subtitleText.Text = i >= 0 && i < Subtitles.Length ? Subtitles[i] : "";
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0); Grid.SetRow(tabs, 1);
        root.Children.Add(header); root.Children.Add(tabs);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = root;

        // 模板设计器「预览」：切到报表中心页直接预览这份未保存的模板。
        viewTemplate.PreviewRequested += def =>
        {
            viewCenter.ShowPreview(def);
            Select(0);
        };
        // 模板存盘 ⇒ 报表中心的模板下拉当场刷新（原来必须关窗重开）。
        viewTemplate.TemplatesChanged += () => viewCenter.ReloadTemplates();
        // 「指标库…」：原来是再开一个窗，现在就是隔壁那一页。
        viewTemplate.IndicatorLibraryRequested += () => Select(2);

        // 指标增删改 ⇒ 模板设计器的指标下拉刷新。
        viewIndicator.LibraryChanged += () => viewTemplate.RefreshIndicators();

        // 归档 ⇒ 存档页当场重读。
        viewCenter.Archived += () => viewArchive.Reload();

        subtitleText.Text = Subtitles[0];
    }

    /// <summary>直接落到某一页打开（功能区目前只用默认的报表中心页）。</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < tabs.Items.Count) tabs.SelectedIndex = index;
    }
}
