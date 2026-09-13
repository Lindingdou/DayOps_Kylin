// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportViewBuilder.cs（逐行对应；WPF 控件树 → Avalonia 控件树）
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Reporting;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 共享渲染器 —— 把渲染树 <see cref="ReportDocument"/> 画成"实际报表"控件树（纸张预览）。
/// 版式：报表头 → KPI 指标卡行 → 报告叙述 → 明细表(斑马纹) → 图表(计划vs实绩双色柱) → 结论 → 签批。
/// 报表中心与报表存档页共用，保证屏幕呈现一致。
/// </summary>
public static class ReportViewBuilder
{
    public static void Render(Panel host, ReportDocument doc)
    {
        host.Children.Clear();

        // 报表头
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = doc.Title, FontSize = 23, FontWeight = FontWeight.Bold, Foreground = Hex("#1E3A5F"), HorizontalAlignment = HorizontalAlignment.Center });
        if (!string.IsNullOrEmpty(doc.Subtitle))
            head.Children.Add(new TextBlock { Text = doc.Subtitle, FontSize = 13, Foreground = Hex("#6C757D"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var m in doc.MetaLines)
            meta.Children.Add(new TextBlock { Text = m, FontSize = 12, Foreground = Hex("#6C757D"), Margin = new Thickness(12, 0, 12, 0) });
        head.Children.Add(meta);
        host.Children.Add(new Border { Child = head, BorderBrush = Hex("#00BFFE"), BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(0, 0, 0, 8) });

        // KPI 指标卡行
        if (doc.KpiCards.Count > 0) host.Children.Add(BuildKpiCards(doc));

        // 报告叙述章节
        foreach (var nb in doc.Narrative)
        {
            var block = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            if (!string.IsNullOrEmpty(nb.Heading))
                block.Children.Add(new TextBlock { Text = nb.Heading, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Hex("#1E3A5F"), Margin = new Thickness(0, 0, 0, 4) });
            block.Children.Add(new TextBlock { Text = nb.Body, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Hex("#1F2937"), LineHeight = 22 });
            host.Children.Add(block);
        }

        // 明细表
        if (doc.Columns.Count > 0)
        {
            host.Children.Add(SectionLabel("明细", doc.Rows.Count));
            host.Children.Add(Scrollable(BuildTable(doc)));
        }

        // 交叉表（采—排流向矩阵）
        if (doc.Matrix is { Rows.Count: > 0 } mx)
        {
            host.Children.Add(SectionLabel(mx.Title, mx.Rows.Count));
            if (!string.IsNullOrEmpty(mx.Legend))
                host.Children.Add(new TextBlock { Text = mx.Legend, FontSize = 12, Foreground = Hex("#6C757D"), Margin = new Thickness(0, 2, 0, 0) });
            host.Children.Add(Scrollable(BuildMatrix(mx)));
        }

        // 图表
        foreach (var ch in doc.Charts.Where(c => c.Bars.Count > 0))
            host.Children.Add(BuildChart(ch));

        // 结论
        if (!string.IsNullOrEmpty(doc.Conclusion))
        {
            var concl = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Hex("#1F2937") };
            concl.Inlines!.Add(new Run("分析结论：") { FontWeight = FontWeight.Bold });
            concl.Inlines.Add(new Run(doc.Conclusion));
            host.Children.Add(new Border { Child = concl, Background = Hex("#F5F8FC"), Padding = new Thickness(12), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 16, 0, 0) });
        }

        // 口径脚注（指标口径 / 估算假设 / 数据缺口）
        if (doc.Footnotes.Count > 0)
        {
            var notes = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            notes.Children.Add(new TextBlock { Text = "口径说明", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Hex("#6C757D"), Margin = new Thickness(0, 0, 0, 3) });
            foreach (var n in doc.Footnotes)
                notes.Children.Add(new TextBlock { Text = "· " + n, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Hex("#6C757D"), LineHeight = 18 });
            host.Children.Add(notes);
        }

        // 签批栏
        if (!string.IsNullOrEmpty(doc.Signatures))
            host.Children.Add(new TextBlock { Text = doc.Signatures, FontSize = 13, Foreground = Hex("#1F2937"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 28, 0, 8) });
    }

    /// <summary>给一张<b>可能比纸张宽</b>的表包一层自己的横向滚动条 —— 「纸张不被撑宽」的关键。</summary>
    private static Control Scrollable(Control inner) => new ScrollViewer
    {
        Content = inner,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Transparent,
        Padding = new Thickness(0),
    };

    private static Control SectionLabel(string text, int count)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 2) };
        sp.Children.Add(new Border { Width = 4, Height = 15, Background = Hex("#00BFFE"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Hex("#1E3A5F") });
        if (count > 0) sp.Children.Add(new TextBlock { Text = $"　共 {count} 行", FontSize = 11, Foreground = Hex("#9AA5B1"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
        return sp;
    }

    private static Control BuildKpiCards(ReportDocument doc)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 2) };
        foreach (var k in doc.KpiCards)
        {
            var sp = new StackPanel { Margin = new Thickness(13, 9, 18, 9) };
            sp.Children.Add(new TextBlock { Text = k.Label, FontSize = 12, Foreground = Hex("#6C757D") });
            var valLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            valLine.Children.Add(new TextBlock { Text = k.ValueText, FontSize = 23, FontWeight = FontWeight.Bold, Foreground = StatusBrush(k.Status) });
            if (!string.IsNullOrEmpty(k.Unit))
                valLine.Children.Add(new TextBlock { Text = " " + k.Unit, FontSize = 12, Foreground = Hex("#6C757D"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 3) });
            sp.Children.Add(valLine);
            if (!string.IsNullOrEmpty(k.SubText))
                sp.Children.Add(new TextBlock { Text = k.SubText, FontSize = 11, Foreground = Hex("#9AA5B1"), Margin = new Thickness(0, 1, 0, 0) });

            var accent = new Border { Width = 4, Background = StatusAccent(k.Status), CornerRadius = new CornerRadius(6, 0, 0, 6) };
            var dock = new DockPanel();
            DockPanel.SetDock(accent, Avalonia.Controls.Dock.Left);
            dock.Children.Add(accent);
            dock.Children.Add(sp);
            wrap.Children.Add(new Border
            {
                Child = dock,
                Background = Hex("#F8FAFC"),
                BorderBrush = Hex("#E2E8F0"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 134,
            });
        }
        return wrap;
    }

    /// <summary>明细表。<b>列宽按内容自适应</b>（Auto + 模板宽度当下限），末尾再挂一根 <c>*</c> 空白列把表铺满纸张。</summary>
    private static Grid BuildTable(ReportDocument doc)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in doc.Columns)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = Math.Max(48, c.Width) });
        // 铺满用的空白列（不放数据，只承接底色）
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        int filler = doc.Columns.Count;

        int rowCount = 1 + doc.Rows.Count + (doc.TotalRow != null ? 1 : 0);
        for (int i = 0; i < rowCount; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 表头
        for (int c = 0; c < doc.Columns.Count; c++)
            AddCell(g, 0, c, doc.Columns[c].Header, doc.Columns[c].Align, Hex("#334155"), true,
                Hex("#E9EEF4"), new Thickness(0, 0, 0, 1), Hex("#9DB2C6"));
        AddCell(g, 0, filler, "", CellAlign.Left, Hex("#334155"), false,
            Hex("#E9EEF4"), new Thickness(0, 0, 0, 1), Hex("#9DB2C6"));

        // 明细（斑马纹）
        for (int r = 0; r < doc.Rows.Count; r++)
        {
            var row = doc.Rows[r];
            IBrush? rowBg = (r % 2 == 1) ? Hex("#F6F9FC") : null;
            for (int c = 0; c < row.Cells.Count && c < doc.Columns.Count; c++)
            {
                var cell = row.Cells[c];
                AddCell(g, r + 1, c, cell.Text, cell.Align, StatusBrush(cell.Status), cell.Bold,
                    rowBg, new Thickness(0, 0, 0, 1), Hex("#EDEFF2"));
            }
            AddCell(g, r + 1, filler, "", CellAlign.Left, Hex("#1F2937"), false,
                rowBg, new Thickness(0, 0, 0, 1), Hex("#EDEFF2"));
        }

        // 合计
        if (doc.TotalRow != null)
        {
            int rr = doc.Rows.Count + 1;
            for (int c = 0; c < doc.TotalRow.Cells.Count && c < doc.Columns.Count; c++)
            {
                var cell = doc.TotalRow.Cells[c];
                AddCell(g, rr, c, cell.Text, cell.Align, StatusBrush(cell.Status), true,
                    Hex("#EAF0F6"), new Thickness(0, 1, 0, 0), Hex("#9DB2C6"));
            }
            AddCell(g, rr, filler, "", CellAlign.Left, Hex("#1F2937"), false,
                Hex("#EAF0F6"), new Thickness(0, 1, 0, 0), Hex("#9DB2C6"));
        }
        return g;
    }

    /// <summary>交叉表：左上角 = 行维＼列维，首列 = 行键，其余 = 各 O-D 单元格。列宽按内容自适应；宽表由 Scrollable 自己横向滚。</summary>
    private static Grid BuildMatrix(MatrixTableView m)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 140 });
        foreach (var _ in m.ColumnKeys)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 112 });
        for (int i = 0; i <= m.Rows.Count; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddCell(g, 0, 0, m.RowHeader, CellAlign.Left, Hex("#334155"), true, Hex("#E9EEF4"), new Thickness(0, 0, 0, 1), Hex("#9DB2C6"));
        for (int c = 0; c < m.ColumnKeys.Count; c++)
            AddCell(g, 0, c + 1, m.ColumnKeys[c], CellAlign.Right, Hex("#334155"), true, Hex("#E9EEF4"), new Thickness(0, 0, 0, 1), Hex("#9DB2C6"));

        for (int r = 0; r < m.Rows.Count; r++)
        {
            var row = m.Rows[r];
            IBrush? bg = row.Emphasize ? Hex("#EAF0F6") : (r % 2 == 1 ? Hex("#F6F9FC") : null);
            var border = row.Emphasize ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);
            var borderBrush = row.Emphasize ? Hex("#9DB2C6") : Hex("#EDEFF2");
            AddCell(g, r + 1, 0, row.Key, CellAlign.Left, Hex("#1F2937"), row.Emphasize, bg, border, borderBrush);
            for (int c = 0; c < row.Cells.Count && c < m.ColumnKeys.Count; c++)
            {
                var cell = row.Cells[c];
                AddCell(g, r + 1, c + 1, cell.Text, cell.Align, StatusBrush(cell.Status), cell.Bold || row.Emphasize, bg, border, borderBrush);
            }
        }
        return g;
    }

    private static void AddCell(Grid g, int row, int col, string text, CellAlign align, IBrush fg, bool bold,
        IBrush? bg, Thickness border, IBrush borderBrush)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = align == CellAlign.Right ? HorizontalAlignment.Right : align == CellAlign.Center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            Margin = new Thickness(9, 6, 9, 6),
            FontSize = 13,
        };
        var b = new Border { Child = tb, BorderThickness = border, BorderBrush = borderBrush };
        if (bg != null) b.Background = bg;
        Grid.SetRow(b, row);
        Grid.SetColumn(b, col);
        g.Children.Add(b);
    }

    private static Border BuildChart(ChartView ch)
    {
        bool grouped = !string.IsNullOrEmpty(ch.SeriesB);
        double max = Math.Max(1e-6, ch.Bars.Max(b => grouped ? Math.Max(b.Value, double.IsNaN(b.Value2) ? 0 : b.Value2) : b.Value));
        const double track = 320;

        var panel = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(new TextBlock { Text = $"{ch.Title}（{ch.Unit}）", FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Hex("#1E3A5F") });
        if (grouped)
        {
            title.Children.Add(Legend("#B5D4F4", ch.SeriesA));
            title.Children.Add(Legend("#1D9E75", ch.SeriesB));
        }
        panel.Children.Add(title);

        foreach (var b in ch.Bars)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            rowPanel.Children.Add(new TextBlock { Text = b.Label, Width = 110, FontSize = 12, Foreground = Hex("#1F2937"), VerticalAlignment = VerticalAlignment.Center });
            if (grouped)
            {
                var bars2 = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                bars2.Children.Add(MakeBar(b.Value, max, track, "#B5D4F4", 9));
                bars2.Children.Add(MakeBar(double.IsNaN(b.Value2) ? 0 : b.Value2, max, track, "#1D9E75", 9));
                rowPanel.Children.Add(bars2);
                var vals = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                vals.Children.Add(new TextBlock { Text = b.Value.ToString("N0"), FontSize = 11, Foreground = Hex("#6C757D") });
                vals.Children.Add(new TextBlock { Text = (double.IsNaN(b.Value2) ? 0 : b.Value2).ToString("N0"), FontSize = 11, Foreground = Hex("#1D9E75") });
                rowPanel.Children.Add(vals);
            }
            else
            {
                var bar = MakeBar(b.Value, max, track, "#1D9E75", 14);
                bar.VerticalAlignment = VerticalAlignment.Center;
                rowPanel.Children.Add(bar);
                rowPanel.Children.Add(new TextBlock { Text = b.Value.ToString("N0"), Width = 84, TextAlignment = TextAlignment.Right, FontSize = 12, Foreground = Hex("#6C757D"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            }
            panel.Children.Add(rowPanel);
        }
        return new Border { Child = panel, Margin = new Thickness(0, 18, 0, 0) };
    }

    private static StackPanel Legend(string color, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        sp.Children.Add(new Border { Width = 11, Height = 11, Background = Hex(color), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = " " + text, FontSize = 12, Foreground = Hex("#6C757D"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static Border MakeBar(double v, double max, double track, string color, double height)
    {
        double w = Math.Max(1, v / max * track);
        var fill = new Border { Width = w, Height = height, Background = Hex(color), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
        return new Border { Width = track, Height = height, Background = Hex("#EEF2F6"), CornerRadius = new CornerRadius(2), Child = fill, Margin = new Thickness(0, 1, 0, 1) };
    }

    public static IBrush StatusBrush(ReportStatus s) => s switch
    {
        ReportStatus.Ok => Hex("#0F6E56"),
        ReportStatus.Warn => Hex("#B7791F"),
        ReportStatus.Bad => Hex("#C0392B"),
        _ => Hex("#1F2937"),
    };

    private static IBrush StatusAccent(ReportStatus s) => s switch
    {
        ReportStatus.Ok => Hex("#1D9E75"),
        ReportStatus.Warn => Hex("#C98A18"),
        ReportStatus.Bad => Hex("#C0392B"),
        _ => Hex("#00BFFE"),
    };

    public static IBrush Hex(string s) => new SolidColorBrush(Color.Parse(s));
}
