// 忠实移植自原 PitMine3D Modules/TaskLib/Adjust/StageGantt.cs 里的 StageGanttRenderer（逐行对应；WPF Canvas 图元 → Avalonia Canvas 图元）
using System;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Adjust;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>跨天环节甘特的画法（区域 × 工序 × 日）。每个可点的格子把 <see cref="StageGanttCell"/> 挂在 <c>Tag</c> 上。</summary>
internal static class StageGanttRenderer
{
    public const double LabelW = 232;   // 左侧「区域 / 工序」列（要放得下「25 天 · 计划 X · 实绩 Y」）
    public const double HeaderH = 34;   // 顶部两行：日 + 星期
    public const double RowH = 24;
    public const double GroupRowH = 26;
    public const double DayW = 26;      // 一天一列
    public const double MinBarH = 5;    // 量再小也要留一根点得中的条
    public const double MinCapH = 3.5;  // 顺延帽子的最小可见高度（真实比例常不到 1px）
    private const double CellPad = 2.5;

    // 工序配色与 GanttRenderer 逐色对齐 —— 同一道工序跨窗口必须是同一个颜色
    private static (Color fill, Color border) ProcColors(ProcessType p) => p switch
    {
        ProcessType.Drill => (C(0xCE, 0xCB, 0xF6), C(0x53, 0x4A, 0xB7)),
        ProcessType.Load => (C(0xB5, 0xD4, 0xF4), C(0x18, 0x5F, 0xA5)),
        ProcessType.Haul => (C(0x9F, 0xE1, 0xCB), C(0x0F, 0x6E, 0x56)),
        ProcessType.Dump => (C(0xFA, 0xC7, 0x75), C(0x85, 0x4F, 0x0B)),
        ProcessType.Blast => (C(0xF7, 0xC1, 0xC1), C(0xA3, 0x2D, 0x2D)),
        _ => (C(0xF1, 0xEF, 0xE8), C(0x5F, 0x5E, 0x5A)),
    };

    private static readonly Color NowColor = C(0xE2, 0x4B, 0x4A);
    private static IBrush Divider => new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));
    private static IBrush RowSep => new SolidColorBrush(Color.FromArgb(0x1E, 0x88, 0x88, 0x88));
    private static IBrush Hint => new SolidColorBrush(Color.FromArgb(0xAA, 0x88, 0x88, 0x88));
    private static IBrush Weekend => new SolidColorBrush(Color.FromArgb(0x14, 0x88, 0x88, 0x88));
    private static IBrush OffDay => new SolidColorBrush(Color.FromArgb(0x30, 0x88, 0x88, 0x88));

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>整幅画布宽度（供 ScrollViewer 定内容宽）。</summary>
    public static double WidthOf(StageGanttModel m) => LabelW + Math.Max(1, m.Days.Count) * DayW + 8;

    /// <summary>整幅画布高度。</summary>
    public static double HeightOf(StageGanttModel m)
        => HeaderH + m.Groups.Count * GroupRowH + m.Groups.Sum(g => g.Lanes.Count) * RowH + 6;

    /// <summary>画。<paramref name="selected"/> 为当前选中的格（画高亮框）。</summary>
    public static void Render(Canvas c, StageGanttModel m, StageGanttCell? selected)
    {
        c.Children.Clear();
        if (m.IsEmpty)
        {
            c.Width = 400; c.Height = 60;
            AddText(c, 12, 20, "没有可画的环节 —— 见下方来源说明。", Hint, 12);
            return;
        }

        int nd = m.Days.Count;
        double w = WidthOf(m), h = HeightOf(m);
        c.Width = w; c.Height = h;
        double bodyTop = HeaderH, bodyBottom = h - 6;

        double X(int dayIndex) => LabelW + dayIndex * DayW;

        // ── 列底纹：周末淡、非作业日更明显（先画，垫在最底下）──
        for (int i = 0; i < nd; i++)
        {
            var d = m.Days[i];
            if (!d.IsWorkday) AddRect(c, X(i), bodyTop, DayW, bodyBottom - bodyTop, OffDay, hitTest: false);
            else if (d.IsWeekend) AddRect(c, X(i), bodyTop, DayW, bodyBottom - bodyTop, Weekend, hitTest: false);
        }

        // ── 表头：日 + 星期；月首/周一加竖分隔；作业日那一列红底白字 ──
        for (int i = 0; i < nd; i++)
        {
            var d = m.Days[i];
            bool off = !d.IsWorkday;
            bool isNow = i == m.ActualDayIndex;

            if (isNow)
                AddRect(c, X(i) + 1, 1, DayW - 2, HeaderH - 3, new SolidColorBrush(NowColor), hitTest: false);

            var dayBrush = isNow ? Brushes.White
                         : off ? Hint
                         : new SolidColorBrush(Color.FromArgb(0xDD, 0x66, 0x66, 0x66));
            AddText(c, X(i), 3, d.Date.Day.ToString(), dayBrush, 10.5, DayW, center: true,
                    weight: (isNow || d.Date.Day == 1) ? FontWeight.Bold : null);
            AddText(c, X(i), 17, d.WeekLabel.Substring(1),
                    isNow ? Brushes.White : off ? Hint : Weekday(d), 9, DayW, center: true);
            if (d.Date.DayOfWeek == DayOfWeek.Monday || d.Date.Day == 1)
                AddLine(c, X(i), bodyTop, X(i), bodyBottom, Divider, 0.7);
        }
        AddLine(c, LabelW, 0, LabelW, bodyBottom, Divider, 0.9);
        AddLine(c, 0, bodyTop, w, bodyTop, Divider, 0.9);

        // ── 作业日竖线（那一天是真数据，其余都是推算）──
        if (m.ActualDayIndex >= 0 && m.ActualDayIndex < nd)
        {
            double nx = X(m.ActualDayIndex);
            AddRect(c, nx, bodyTop, DayW, bodyBottom - bodyTop,
                    new SolidColorBrush(Color.FromArgb(0x18, NowColor.R, NowColor.G, NowColor.B)), hitTest: false);
            AddLine(c, nx, 0, nx, bodyBottom, new SolidColorBrush(NowColor), 1.4);
            AddLine(c, nx + DayW, 0, nx + DayW, bodyBottom, new SolidColorBrush(NowColor), 1.4);
        }

        // ── 逐组逐线 ──
        double y = bodyTop;
        foreach (var g in m.Groups)
        {
            // 组头：区域名 + 全期合计
            var gc = g.HasAbnormal ? C(0xA3, 0x2D, 0x2D) : C(0x33, 0x41, 0x55);
            AddRect(c, 0, y + 1, w, GroupRowH - 2, new SolidColorBrush(Color.FromArgb(0x20, gc.R, gc.G, gc.B)), hitTest: false);
            AddRect(c, 4, y + 4, 3, GroupRowH - 8, new SolidColorBrush(gc), hitTest: false);
            AddText(c, 12, y + 5, "▸ " + g.Region, new SolidColorBrush(gc), 11.5, LabelW - 16, weight: FontWeight.Bold);
            if (g.TotalCaption.Length > 0)
                AddText(c, LabelW + 6, y + 6, g.TotalCaption, Hint, 9.5);
            y += GroupRowH;

            foreach (var lane in g.Lanes)
            {
                var (fill, border) = ProcColors(lane.Process);
                // 行首：工序名 + 该线的天数/合计
                AddRect(c, 14, y + RowH / 2 - 4, 8, 8, new SolidColorBrush(border), hitTest: false);
                AddText(c, 26, y + RowH / 2 - 8, lane.Process.Label(), new SolidColorBrush(border), 10.5,
                        weight: FontWeight.SemiBold);
                string tail = lane.Process is ProcessType.Load or ProcessType.Dump
                    ? (lane.TotalM3 > 1e-6
                        ? $"{lane.WorkDayCount} 天 · 计划 {lane.TotalM3 / 1e4:0.##} 万m³"
                          + (lane.ActualTotalM3 > 1e-6 ? $" · 实绩 {lane.ActualTotalM3 / 1e4:0.##}" : "")
                        : $"{lane.WorkDayCount} 天 · 计划外 实绩 {lane.ActualTotalM3 / 1e4:0.##} 万m³")
                    : $"{lane.WorkDayCount} 天";
                // 必须给宽度：不设的话 TextBlock 会一路铺进图表区，盖住右边的格子
                AddText(c, 74, y + RowH / 2 - 7, tail, Hint, 9, width: LabelW - 78);
                AddLine(c, 0, y, w, y, RowSep, 0.6);

                double peak = Math.Max(1e-6, lane.PeakM3);
                foreach (var cell in lane.Cells)
                {
                    if (!cell.HasWork || !cell.IsWorkday) continue;

                    double cx = X(cell.DayIndex) + CellPad;
                    double cw = DayW - CellPad * 2;
                    double avail = RowH - CellPad * 2;
                    // 量型工序按占峰值比例定条高（下限 MinBarH，保证点得中）；非量型铺满
                    double bh = cell.IsVolumeProcess
                        ? Math.Max(MinBarH, avail * Math.Clamp(cell.DisplayVolumeM3 / peak, 0, 1))
                        : avail;
                    double by = y + RowH - CellPad - bh;

                    // 推算格淡一档 + 虚边；真实格实心实边 —— 这一眼的区别不能省
                    byte alpha = cell.Projected ? (byte)0x9E : (byte)0xFF;
                    var fb = new SolidColorBrush(Color.FromArgb(alpha, fill.R, fill.G, fill.B));
                    var bb = new SolidColorBrush(Color.FromArgb(cell.Projected ? (byte)0xAA : (byte)0xFF,
                                                                border.R, border.G, border.B));

                    var rect = new Rectangle
                    {
                        Width = cw, Height = bh, Fill = fb, Stroke = bb,
                        StrokeThickness = cell.Projected ? 0.7 : 1.0,
                        StrokeDashArray = cell.Projected ? new AvaloniaList<double> { 2, 1.6 } : null,
                        RadiusX = 1.5, RadiusY = 1.5,
                        Tag = cell, Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    ToolTip.SetTip(rect, cell.Tooltip);
                    Canvas.SetLeft(rect, cx); Canvas.SetTop(rect, by); c.Children.Add(rect);

                    // 顺延补量：在条顶画一段更深的帽子，高度按顺延量占顺延后计划的比例（最小 MinCapH 保证看得见）
                    if (cell.RolledInM3 > 1e-6 && cell.PlannedTotalM3 > 1e-6)
                    {
                        double rh = Math.Max(MinCapH, bh * Math.Clamp(cell.RolledInM3 / cell.DisplayVolumeM3, 0, 1));
                        rh = Math.Min(rh, bh);
                        AddRect(c, cx, by, cw, rh,
                                new SolidColorBrush(Color.FromArgb(0xEE, border.R, border.G, border.B)),
                                hitTest: false);
                        // 帽子底边一条红线：与实绩条（画在格底）方向相反，两者同时出现也分得开
                        AddLine(c, cx, by + rh, cx + cw, by + rh, new SolidColorBrush(NowColor), 1.2);
                    }

                    // 实绩条：贴在计划条底部，高度按实绩占 max(计划,实绩) 的比例（分母用 DisplayVolumeM3）
                    if (cell.IsVolumeProcess && cell.ActualVolumeM3 > 1e-6)
                    {
                        double ratio = Math.Clamp(cell.ActualVolumeM3 / Math.Max(1e-6, cell.DisplayVolumeM3), 0, 1);
                        double ah = Math.Max(1.5, bh * ratio);
                        AddRect(c, cx + 1, y + RowH - CellPad - ah, Math.Max(1, cw - 2), ah,
                                new SolidColorBrush(Color.FromArgb(0x88, border.R, border.G, border.B)), hitTest: false);
                    }

                    // 异常角标：右上角一个小红三角（比整条染红克制，不盖掉工序色）
                    if (cell.Abnormal)
                    {
                        var tri = new Polygon
                        {
                            Points = new AvaloniaList<Point> { new Point(cx + cw, by), new Point(cx + cw - 5, by), new Point(cx + cw, by + 5) },
                            Fill = new SolidColorBrush(NowColor), IsHitTestVisible = false,
                        };
                        c.Children.Add(tri);
                    }

                    // 选中框
                    if (selected != null && ReferenceEquals(selected, cell))
                    {
                        var sel = new Rectangle
                        {
                            Width = DayW, Height = RowH, Stroke = new SolidColorBrush(NowColor),
                            StrokeThickness = 1.8, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                        };
                        Canvas.SetLeft(sel, X(cell.DayIndex)); Canvas.SetTop(sel, y); c.Children.Add(sel);
                    }
                }
                y += RowH;
            }
        }
    }

    private static IBrush Weekday(DayPlan d)
        => d.IsWeekend ? new SolidColorBrush(Color.FromArgb(0xBB, 0xA3, 0x2D, 0x2D))
                       : new SolidColorBrush(Color.FromArgb(0x99, 0x88, 0x88, 0x88));

    private static void AddRect(Canvas c, double x, double y, double w, double h, IBrush b, bool hitTest = true)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = b, IsHitTestVisible = hitTest };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
    }

    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, IBrush b, double th)
    {
        var l = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th, IsHitTestVisible = false };
        c.Children.Add(l);
    }

    private static void AddText(Canvas c, double x, double y, string t, IBrush b, double size = 11,
                                double? width = null, bool center = false, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = t, Foreground = b, FontSize = size, IsHitTestVisible = false };
        if (width != null)
        {
            tb.Width = width.Value;
            tb.TextAlignment = center ? TextAlignment.Center : TextAlignment.Left;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        if (weight != null) tb.FontWeight = weight.Value;
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); c.Children.Add(tb);
    }
}
