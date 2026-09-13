// 忠实移植自原 PitMine3D Modules/TaskLib/Gantt/GanttRenderer.cs（逐行对应；WPF Canvas/Shapes → Avalonia Canvas/Shapes）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Gantt;

namespace PitMine3D.Kylin.Views.TaskLib;

// ─────────────────────────────────────────────────────────────────────────────
//  按日·三班 设备作业甘特 —— 纯 Canvas 画（不引第三方，自包含；仿 ShortTermCharts 范式）。
//  配色严格对齐设计稿：穿孔紫 / 爆破红 / 采装蓝 / 运输青 / 排土琥珀 / 检修空闲灰斜纹。
// ─────────────────────────────────────────────────────────────────────────────
internal static class GanttRenderer
{
    // 布局常量
    private const double LabelW = 116;   // 左侧设备名列宽
    /// <summary>
    /// 顶部标签区高度 —— <b>两行</b>：第一行班次标签，第二行「现在」游标标签。
    /// <para>
    /// 原来是一行 22：两个标签都画在 y=3，而「现在」的 x 跟着时刻走，
    /// 凌晨两点就正好压在「早班 00:00–08:00」上，读出来是「02:12现在00–08:00」。
    /// 靠挪 x 躲不开（现在线可以停在任何时刻），所以给它**自己的一行**。
    /// </para>
    /// </summary>
    private const double HeaderH = 36;    // 顶部标签区：班次标签行 + 现在标签行
    private const double NowLabelY = 19;  // 「现在」标签的基线（第二行）
    private const double RowH = 30;       // 行高
    private const double BarH = 18;       // 条高
    private const double RightPad = 12;
    private const double LeftPad = 10;

    // ── 工序配色（fill / border / text），与设计稿一致 ──
    private static (Color fill, Color border, Color text) ProcColors(ProcessType p) => p switch
    {
        ProcessType.Drill => (C(0xCE, 0xCB, 0xF6), C(0x53, 0x4A, 0xB7), C(0x26, 0x21, 0x5C)), // 紫
        ProcessType.Load => (C(0xB5, 0xD4, 0xF4), C(0x18, 0x5F, 0xA5), C(0x04, 0x2C, 0x53)),  // 蓝
        ProcessType.Haul => (C(0x9F, 0xE1, 0xCB), C(0x0F, 0x6E, 0x56), C(0x04, 0x34, 0x2C)),  // 青
        ProcessType.Dump => (C(0xFA, 0xC7, 0x75), C(0x85, 0x4F, 0x0B), C(0x41, 0x24, 0x02)),  // 琥珀
        ProcessType.Blast => (C(0xF7, 0xC1, 0xC1), C(0xA3, 0x2D, 0x2D), C(0x50, 0x13, 0x13)), // 红
        _ => (C(0xF1, 0xEF, 0xE8), C(0x5F, 0x5E, 0x5A), C(0x2C, 0x2C, 0x2A)),                  // 检修/空闲 灰
    };

    private static Color CatColor(string cat) => cat switch
    {
        "电铲" => C(0x18, 0x5F, 0xA5),
        "卡车" => C(0x0F, 0x6E, 0x56),
        "钻机" => C(0x53, 0x4A, 0xB7),
        "推土机" => C(0x85, 0x4F, 0x0B),
        "爆破" => C(0xA3, 0x2D, 0x2D),          // 与爆破停产带同色系：一眼看出这一行是炮
        _ => C(0x88, 0x87, 0x80),
    };

    private static readonly Color NowColor = C(0xE2, 0x4B, 0x4A);
    private static readonly Color BlastFill = C(0xF7, 0xC1, 0xC1);
    private static readonly Color BlastBorder = C(0xA3, 0x2D, 0x2D);
    private static IBrush Divider => new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));
    private static IBrush RowSep => new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88));
    private static IBrush Hint => new SolidColorBrush(Color.FromArgb(0xAA, 0x88, 0x88, 0x88));

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color ParseHex(string s) => Color.Parse(s);

    /// <summary>把一天的甘特画到 canvas 上（随宽度自适应；高度按行数定）。</summary>
    public static void Render(Canvas c, DailyGanttModel m)
    {
        c.Children.Clear();
        double w = c.Bounds.Width > 60 ? c.Bounds.Width : 1080;
        int n = m.Rows.Count;
        double bodyTop = HeaderH;
        double bodyBottom = HeaderH + n * RowH;
        c.Height = bodyBottom + 2;

        double tx0 = LabelW, tx1 = w - RightPad, tw = Math.Max(60, tx1 - tx0);
        double X(double hour) => tx0 + Math.Clamp(hour, 0, 24) / 24.0 * tw;

        // ── 顶部班次标签 ──
        //  ★ 按**当日真实班制**画（m.Shifts），不再写死"早班 00–08 / 中班 08–16 / 夜班 16–24"：
        //    日历改成四班、或交接时刻从 8:00 挪到 7:30 时，写死的表头就在说谎，
        //    而条的位置是按真实时窗排的 —— 图上两者对不上却毫无提示。
        //    模型没带班制（脱离盘子的离线台架）才退回三等分兜底。
        var shifts = m.Shifts is { Count: > 0 } ? m.Shifts : null;
        if (shifts == null)
        {
            double seg = tw / 3.0;
            AddText(c, tx0, 3, "早班 00–08", Hint, 10.5, seg, center: true);
            AddText(c, tx0 + seg, 3, "中班 08–16", Hint, 10.5, seg, center: true);
            AddText(c, tx0 + 2 * seg, 3, "夜班 16–24", Hint, 10.5, seg, center: true);
        }
        else
        {
            foreach (var s in shifts)
            {
                // 跨零点的班（22→6）在 0–24 的画布上是两段，各画一段标签才不会跑到画布外
                foreach (var (a, b) in SpanOn24(s.Start, s.End))
                {
                    double x = X(a), wSeg = Math.Max(24, X(b) - x);
                    AddText(c, x, 3, $"{s.Name} {Hm(a)}–{Hm(b)}", Hint, 10.5, wSeg, center: true);
                }
            }
        }

        // ── 爆破停产带（先画，垫在条下面）；无爆破窗口（如按日快照）就不画，免得在 0 点糊一条 ──
        //  ★ 逐炮画，不是只画最早一炮：装箱自 2026-08-11 起按全部时窗扣，图上少画一段
        //    就会出现"这个时段明明在清场、条却排得满满的"——图与计划必须说同一件事。
        var bands = m.Blasts is { Count: > 0 }
            ? m.Blasts
            : m.BlastEnd > m.BlastStart + 1e-6
                ? new List<BlastWindow> { new(m.BlastStart, m.BlastEnd) }
                : new List<BlastWindow>();

        foreach (var b in bands)
        {
            double bx0 = X(b.Start), bx1 = X(b.End);
            AddRect(c, bx0, bodyTop, Math.Max(2, bx1 - bx0), bodyBottom - bodyTop,
                new SolidColorBrush(Color.FromArgb(0x99, BlastFill.R, BlastFill.G, BlastFill.B)), hitTest: false);
            AddLine(c, bx0, bodyTop, bx0, bodyBottom, new SolidColorBrush(BlastBorder), 1);
            AddLine(c, bx1, bodyTop, bx1, bodyBottom, new SolidColorBrush(BlastBorder), 1);
            AddText(c, bx0 - 12, bodyBottom - 14, "爆破", new SolidColorBrush(BlastBorder), 9);
        }

        // ── 班次分隔线：画在每个班的交接时刻上（原先写死 8h / 16h）──
        if (shifts == null)
        {
            AddLine(c, X(8), bodyTop, X(8), bodyBottom, Divider, 0.8);
            AddLine(c, X(16), bodyTop, X(16), bodyBottom, Divider, 0.8);
        }
        else
        {
            foreach (double h in shifts.Select(s => Wrap24(s.Start)).Distinct().Where(h => h > 0.01 && h < 23.99))
                AddLine(c, X(h), bodyTop, X(h), bodyBottom, Divider, 0.8);
        }
        AddLine(c, tx0, bodyTop, tx0, bodyBottom, Divider, 0.8); // 标签列分隔

        // ── 逐行 ──
        for (int i = 0; i < n; i++)
        {
            var row = m.Rows[i];
            double rowTop = bodyTop + i * RowH;

            // 分组头（作业区 / 设备类型 / 去向）：整行淡色带 + 左侧色条 + 粗体组名 + 右侧补充说明
            if (row.IsZoneHeader)
            {
                Color zc = ParseHex(string.IsNullOrEmpty(row.ZoneColorHex) ? "#FF888888" : row.ZoneColorHex);
                byte bandA = (byte)(row.NoteAlert ? 0x3A : 0x22);
                AddRect(c, LeftPad, rowTop + 2, tx1 - LeftPad, RowH - 3, new SolidColorBrush(Color.FromArgb(bandA, zc.R, zc.G, zc.B)));
                AddRect(c, LeftPad, rowTop + 5, 3, RowH - 9, new SolidColorBrush(zc));
                // 组名：**右侧有统计文字时夹在左列里**（超出截断成省略号）。
                bool hasNote = !string.IsNullOrEmpty(row.Note);
                AddText(c, LeftPad + 9, rowTop + RowH / 2 - 8, "▸ " + row.Name, new SolidColorBrush(zc), 12,
                    width: hasNote ? Math.Max(40, LabelW - LeftPad - 13) : (double?)null,
                    weight: FontWeight.Bold, trim: hasNote, tip: hasNote ? row.Name : null);

                // 按去向视图：今日入方占容 / 剩余库容 —— 写在时间轴区起点，跟组名同行
                if (!string.IsNullOrEmpty(row.Note))
                {
                    var noteBrush = new SolidColorBrush(row.NoteAlert ? zc : Color.FromArgb(0xCC, 0x55, 0x55, 0x55));
                    AddText(c, tx0 + 6, rowTop + RowH / 2 - 7, row.Note, noteBrush, 10.5,
                        width: Math.Max(40, tx1 - tx0 - 12),
                        weight: row.NoteAlert ? FontWeight.Bold : null);
                }
                AddLine(c, LeftPad, rowTop + RowH, tx1, rowTop + RowH, RowSep, 0.6);
                continue;
            }

            // 行分隔
            AddLine(c, LeftPad, rowTop + RowH, tx1, rowTop + RowH, RowSep, 0.6);

            // 行首色点 + 设备名
            double dotX = LeftPad + (row.IsSub ? 14 : 2);
            double dotY = rowTop + RowH / 2 - 3.5;
            var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(CatColor(row.Category)) };
            Canvas.SetLeft(dot, dotX); Canvas.SetTop(dot, dotY); c.Children.Add(dot);
            AddText(c, dotX + 11, rowTop + RowH / 2 - 8, row.Name,
                new SolidColorBrush(row.IsSub ? Color.FromArgb(0xCC, 0x66, 0x66, 0x66) : Color.FromArgb(0xFF, 0x33, 0x33, 0x33)),
                11);

            // 条
            double barY = rowTop + (RowH - BarH) / 2;
            foreach (var bar in row.Bars)
            {
                double x = X(bar.StartHour), bw = X(bar.EndHour) - x;
                // 车次条首尾相接，留 0.8px 缝，否则一串车次糊成一根长条，看不出趟数
                if (bar.TripIndex > 0) bw = Math.Max(1.5, bw - 0.8);

                var (fill, border, text) = ProcColors(bar.Process);
                IBrush fillBrush = bar.Hatch ? HatchBrush() : new SolidColorBrush(fill);
                // 影子条（编组未解出的回落形态）用半透明填充，与真车次条区分
                if (bar.IsShadow) fillBrush = new SolidColorBrush(Color.FromArgb(0x77, fill.R, fill.G, fill.B));

                // 车次条很窄（T_c 25min ≈ 16px）：用小字号 + 更低的显字阈值，
                // 长运距（T_c 大）时车次号就能直接写在条上，短的仍靠悬停看。
                AddBar(c, x, barY, bw, BarH, fillBrush, new SolidColorBrush(border),
                       bar.Label, new SolidColorBrush(text), bar, bar.Tooltip, compact: bar.TripIndex > 0);

                // 去向色标：同一去向的采装条与排土条在条左端戴同色小帽 → 采排配对看得见
                if (!string.IsNullOrEmpty(bar.TintHex) && bw >= 4)
                {
                    var tint = ParseHex(bar.TintHex);
                    // 不吃鼠标：点在色帽上要照样能选中它下面的条
                    AddRect(c, x + 0.8, barY + 2, 2.6, BarH - 4, new SolidColorBrush(tint), hitTest: false);
                }
            }
        }

        // ── 现在线 ──
        double nx = X(m.NowHour);
        AddLine(c, nx, bodyTop, nx, bodyBottom, new SolidColorBrush(NowColor), 1.5);
        // 标签画在第二行（见 HeaderH）；贴近右边界时翻到线的左侧，免得跑出画布
        string nowText = $"{m.NowText} 现在";
        double nowTextW = nowText.Length * 7.0 + 4;          // 10px 字号的粗估宽，够用来判越界
        double nowLabelX = nx + 3 + nowTextW > tx1 ? Math.Max(tx0, nx - 3 - nowTextW) : nx + 3;
        AddText(c, nowLabelX, NowLabelY, nowText, new SolidColorBrush(NowColor), 10);
    }

    /// <summary>底部「当日工作量」进度条（计划 vs 实绩 + 现在游标）。</summary>
    public static void RenderProgress(Canvas c, DailyGanttModel m)
    {
        c.Children.Clear();
        double w = c.Bounds.Width > 20 ? c.Bounds.Width : 600;
        double h = c.Bounds.Height > 4 ? c.Bounds.Height : 14;
        double r = h / 2;

        // 轨道
        var track = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)) };
        Canvas.SetLeft(track, 0); Canvas.SetTop(track, 0); c.Children.Add(track);

        // 实绩填充
        double fillFrac = m.DayPlanWanM3 > 1e-6 ? Math.Clamp(m.DayActualWanM3 / m.DayPlanWanM3, 0, 1) : 0;
        if (fillFrac > 0)
        {
            var fill = new Rectangle { Width = w * fillFrac, Height = h, RadiusX = r, RadiusY = r, Fill = new SolidColorBrush(C(0x1D, 0x9E, 0x75)) };
            Canvas.SetLeft(fill, 0); Canvas.SetTop(fill, 0); c.Children.Add(fill);
        }

        // 现在游标（= 至「现在」应完成进度位置）
        double nf = m.DayPlanWanM3 > 1e-6
            ? Math.Clamp(m.PlanToNowWanM3 / m.DayPlanWanM3, 0, 1)
            : Math.Clamp(m.NowHour / 24.0, 0, 1);
        AddLine(c, w * nf, -2, w * nf, h + 2, new SolidColorBrush(Color.FromArgb(0xCC, 0x66, 0x66, 0x66)), 1.5);
    }

    // ── 画图小工具（仿 ShortTermCharts）──
    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, IBrush b, double th = 1)
    {
        var l = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th, IsHitTestVisible = false };
        c.Children.Add(l);
    }

    private static void AddRect(Canvas c, double x, double y, double w, double h, IBrush b, bool hitTest = true)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = b, IsHitTestVisible = hitTest };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
    }

    private static void AddBar(Canvas c, double x, double y, double w, double h, IBrush fill, IBrush border,
                               string text, IBrush textBrush, object? tag = null, string? tooltip = null,
                               bool compact = false)
    {
        if (w <= 0) return;
        var r = new Rectangle
        {
            Width = w, Height = h, Fill = fill, Stroke = border, StrokeThickness = 0.8,
            RadiusX = compact ? 1.5 : 3, RadiusY = compact ? 1.5 : 3,
            Tag = tag, Cursor = tag != null ? new Cursor(StandardCursorType.Hand) : null,
        };
        if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(r, tooltip);
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);

        double pad = compact ? 4.2 : 4;      // compact 条左端有去向色帽，文字从帽后起排
        double minW = compact ? 18 : 24;
        if (!string.IsNullOrEmpty(text) && w > minW)
        {
            var tb = new TextBlock
            {
                Text = text, Foreground = textBrush, FontSize = compact ? 9 : 10.5, Tag = tag,
                Width = Math.Max(0, w - pad - 2), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            };
            if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(tb, tooltip);
            Canvas.SetLeft(tb, x + pad); Canvas.SetTop(tb, y + (h - (compact ? 13 : 15)) / 2); c.Children.Add(tb);
        }
    }

    /// <param name="trim">超出 <paramref name="width"/> 时截断成省略号（默认溢出画到旁边的东西上）。</param>
    /// <param name="tip">悬浮提示（截断后原文靠它才看得到）。</param>
    private static void AddText(Canvas c, double x, double y, string t, IBrush b, double size = 11, double? width = null, bool center = false, FontWeight? weight = null, bool trim = false, string? tip = null)
    {
        var tb = new TextBlock { Text = t, Foreground = b, FontSize = size, IsHitTestVisible = false };
        if (width != null) { tb.Width = width.Value; tb.TextAlignment = center ? TextAlignment.Center : TextAlignment.Left; }
        if (trim) { tb.TextTrimming = TextTrimming.CharacterEllipsis; tb.TextWrapping = TextWrapping.NoWrap; }
        if (!string.IsNullOrEmpty(tip)) { ToolTip.SetTip(tb, tip); tb.IsHitTestVisible = true; }
        if (weight != null) tb.FontWeight = weight.Value;
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); c.Children.Add(tb);
    }

    /// <summary>45° 灰斜纹刷（检修/空闲）。</summary>
    private static IBrush HatchBrush()
    {
        var grp = new DrawingGroup();
        grp.Children.Add(new GeometryDrawing
        {
            Brush = new SolidColorBrush(C(0xF1, 0xEF, 0xE8)),
            Geometry = new RectangleGeometry(new Rect(0, 0, 6, 6)),
        });
        grp.Children.Add(new GeometryDrawing
        {
            Pen = new Pen(new SolidColorBrush(C(0xD3, 0xD1, 0xC7)), 2.4),
            Geometry = new LineGeometry(new Point(0, 6), new Point(6, 0)),
        });
        return new DrawingBrush(grp)
        {
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, 6, 6, RelativeUnit.Absolute),
            SourceRect = new RelativeRect(0, 0, 6, 6, RelativeUnit.Absolute),
        };
    }

    private static double Wrap24(double h)
    {
        if (double.IsNaN(h) || double.IsInfinity(h)) return 0;
        h %= 24;
        return h < 0 ? h + 24 : h;
    }

    private static string Hm(double h)
    {
        double v = Wrap24(h);
        int hh = (int)v, mm = (int)Math.Round((v - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }

    /// <summary>
    /// 把一个班的时窗投到 0–24 的画布上。跨零点的班（22→6）拆成 [22,24) 与 [0,6) 两段——
    /// 不拆的话标签会画到画布外，或者一条横跨整幅图的错误色带。
    /// </summary>
    private static IEnumerable<(double A, double B)> SpanOn24(double start, double end)
    {
        double s = Wrap24(start), e = Wrap24(end);
        if (Math.Abs(end - 24) < 1e-9 && s < 24) { yield return (s, 24); yield break; }
        if (s < e) { yield return (s, e); yield break; }
        if (s > e)
        {
            yield return (s, 24);
            if (e > 0.01) yield return (0, e);
        }
    }
}
