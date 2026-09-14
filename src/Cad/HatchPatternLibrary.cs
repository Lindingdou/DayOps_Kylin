using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 命名填充图案库 —— 原版「填充」的图案下拉来自 C++ 引擎的图案库(PitMine_GetHatchPatternNames, 名字如
/// ANSI31 / SOLID), 那份内核无源; 这里按 AutoCAD 图案定义(acad.pat)的**公开语义**在托管侧重算同名图案:
///
///   每个图案由若干条「线族」组成, 一条线族 = 角度 + 基点(x,y) + delta-x + delta-y + 短划序列:
///     · 角度      线的方向
///     · 基点      该族第 0 条线经过的点
///     · delta-y   相邻两条线的**垂距**(决定疏密)
///     · delta-x   每加一条线, 沿线方向的**错位**(砖缝/蜂窝这类交错图案靠它)
///     · 短划      沿线方向的 画/空 长度序列(正=画, 负=空, 0=点); 无则整条实线
///
/// 生成结果是一批线段(裁到边界内), 与本渲染器的线框管线一致。纯逻辑、可单测。
/// </summary>
public static class HatchPatternLibrary
{
    /// <summary>一条线族(语义同 .pat 的一行)。</summary>
    public readonly struct PatLine
    {
        public readonly double Angle, X, Y, DeltaX, DeltaY;
        public readonly double[]? Dash;
        public PatLine(double angle, double x, double y, double deltaX, double deltaY, double[]? dash = null)
        { Angle = angle; X = x; Y = y; DeltaX = deltaX; DeltaY = deltaY; Dash = dash; }
    }

    /// <summary>一个命名图案。<see cref="IsSolid"/> = 实心填充(不是线族, 由调用方铺面)。</summary>
    public sealed class Pattern
    {
        public string Name = "";          // 图案名(同 AutoCAD, 如 ANSI31)
        public string Cn = "";            // 中文说明(下拉里显示)
        public PatLine[] Lines = Array.Empty<PatLine>();
        public bool IsSolid;
        public bool IsUserDefined;        // 用户定义: 用界面上的角度/间距, 不用表里的线族
        public string Display => Cn.Length == 0 ? Name : $"{Name} {Cn}";
    }

    private static PatLine L(double a, double x, double y, double dx, double dy, params double[] dash)
        => new(a, x, y, dx, dy, dash.Length == 0 ? null : dash);

    /// <summary>
    /// 图案表(定义值取 AutoCAD 标准图案 acad.pat)。顺序即下拉顺序: 实心/用户定义在前, 其后按用途分组。
    /// </summary>
    public static readonly Pattern[] All =
    {
        new() { Name = "SOLID", Cn = "实心", IsSolid = true },
        new() { Name = "USER",  Cn = "用户定义(按角度/间距)", IsUserDefined = true },

        new() { Name = "ANSI31", Cn = "斜线(铁/砖/石)", Lines = new[] { L(45, 0, 0, 0, .125) } },
        new() { Name = "ANSI32", Cn = "双斜线(钢)", Lines = new[]
        {
            L(45, 0, 0, 0, .375),
            L(45, .176776695, 0, 0, .375),
        } },
        new() { Name = "ANSI33", Cn = "斜线+短划(铜)", Lines = new[]
        {
            L(45, 0, 0, 0, .25),
            L(45, .176776695, 0, 0, .25, .125, -.0625),
        } },
        new() { Name = "ANSI34", Cn = "疏斜线(塑料/橡胶)", Lines = new[]
        {
            L(45, 0, 0, 0, .75),
            L(45, .176776695, 0, 0, .75),
            L(45, .353553391, 0, 0, .75),
            L(45, .530330086, 0, 0, .75),
        } },
        new() { Name = "ANSI35", Cn = "耐火砖", Lines = new[]
        {
            L(45, 0, 0, 0, .25),
            L(45, .176776695, 0, 0, .25, .3125, -.0625, 0, -.0625),
        } },
        new() { Name = "ANSI36", Cn = "大理石/板岩", Lines = new[]
        {
            L(45, 0, 0, .21875, .125, .3125, -.0625, 0, -.0625),
        } },
        new() { Name = "ANSI37", Cn = "十字交叉(铅/锌)", Lines = new[]
        {
            L(45, 0, 0, 0, .125),
            L(135, 0, 0, 0, .125),
        } },
        new() { Name = "ANSI38", Cn = "铝", Lines = new[]
        {
            L(45, 0, 0, 0, .125),
            L(135, 0, 0, .25, .125, .3125, -.1875),
        } },

        new() { Name = "LINE", Cn = "水平线", Lines = new[] { L(0, 0, 0, 0, .125) } },
        new() { Name = "NET",  Cn = "方格网", Lines = new[]
        {
            L(0, 0, 0, 0, .125),
            L(90, 0, 0, 0, .125),
        } },
        new() { Name = "NET3", Cn = "三向网", Lines = new[]
        {
            L(0, 0, 0, 0, .125),
            L(60, 0, 0, 0, .125),
            L(120, 0, 0, 0, .125),
        } },
        new() { Name = "DOTS", Cn = "点", Lines = new[]
        {
            L(0, 0, 0, 0, .03125, 0, -.0625),
        } },
        new() { Name = "SQUARE", Cn = "方块", Lines = new[]
        {
            L(0, 0, 0, 0, .125, .125, -.125),
            L(90, 0, 0, 0, .125, .125, -.125),
        } },
        new() { Name = "ANGLE", Cn = "角钢", Lines = new[]
        {
            L(0, 0, 0, 0, .275, .2, -.075),
            L(90, 0, 0, 0, .275, .2, -.075),
        } },
        new() { Name = "CROSS", Cn = "十字", Lines = new[]
        {
            L(0, 0, 0, .25, .25, .125, -.375),
            L(90, .0625, -.0625, .25, .25, .125, -.375),
        } },
        new() { Name = "ZIGZAG", Cn = "阶梯", Lines = new[]
        {
            L(0, 0, 0, .125, .125, .125, -.125),
            L(90, .125, 0, .125, .125, .125, -.125),
        } },
        new() { Name = "BRICK", Cn = "砖墙", Lines = new[]
        {
            L(0, 0, 0, 0, .25),
            L(90, 0, 0, .25, .25, .25, -.25),
        } },
        new() { Name = "HONEY", Cn = "蜂窝", Lines = new[]
        {
            L(0, 0, 0, .1082531754, .1875, .125, -.25),
            L(120, 0, 0, .1082531754, .1875, .125, -.25),
            L(60, .0625, .108253175, .1082531754, .1875, .125, -.25),
        } },
        new() { Name = "TRIANG", Cn = "三角", Lines = new[]
        {
            L(60, 0, 0, .1875, .324759526, .1875, -.1875),
            L(120, 0, 0, .1875, .324759526, .1875, -.1875),
            L(0, -.09375, .162379763, .1875, .324759526, .1875, -.1875),
        } },
        new() { Name = "STARS", Cn = "六芒星", Lines = new[]
        {
            L(0, 0, 0, .21650635, .375, .375, -.375),
            L(60, 0, 0, .21650635, .375, .375, -.375),
            L(120, .1875, .108253175, .21650635, .375, .375, -.375),
        } },
        new() { Name = "EARTH", Cn = "土壤", Lines = new[]
        {
            L(0, 0, 0, .21875, .21875, .21875, -.109375),
            L(0, 0, .09375, .21875, .21875, .21875, -.109375),
            L(90, .109375, .109375, .21875, .21875, .21875, -.109375),
            L(90, .203125, .109375, .21875, .21875, .21875, -.109375),
        } },
        new() { Name = "GRASS", Cn = "草地", Lines = new[]
        {
            L(90, 0, 0, .707106781, .707106781, .1875, -.4375),
            L(45, 0, 0, 0, 1, .1875, -.8125),
            L(135, .125, .125, 0, 1, .1875, -.8125),
        } },
    };

    /// <summary>按名取图案(大小写不敏感); 找不到返回 null。</summary>
    public static Pattern? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var p in All)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>图案的基准疏密(线族里最小的垂距) —— 自动比例按"边界大小 ÷ 基准"折算。</summary>
    public static double BaseSpacing(Pattern p)
    {
        double min = double.MaxValue;
        foreach (var l in p.Lines)
        {
            double d = Math.Abs(l.DeltaY);
            if (d > 1e-9 && d < min) min = d;
        }
        return min == double.MaxValue ? 0.125 : min;
    }

    /// <summary>一次生成最多这么多线段: 比例给小了(图案密到看不清)也不能把界面卡死。</summary>
    public const int MaxSegments = 200_000;

    /// <summary>
    /// 在闭合边界内生成图案线段。scale = 图案缩放(越大越疏), angleDeg = 图案整体旋转角(叠加在图案自带角度上)。
    /// 实心图案(SOLID)不走这里(返回空), 由调用方铺面。超过 <see cref="MaxSegments"/> 即停止并返回已生成的。
    /// </summary>
    public static List<(double x1, double y1, double x2, double y2)> Generate(
        Pattern pattern, IReadOnlyList<(double x, double y)> boundary, double scale, double angleDeg)
    {
        var result = new List<(double, double, double, double)>();
        if (pattern == null || pattern.IsSolid || boundary == null || boundary.Count < 3 || scale <= 1e-12) return result;
        foreach (var line in pattern.Lines)
        {
            if (result.Count >= MaxSegments) break;
            EmitFamily(boundary, line, scale, angleDeg, result);
        }
        return result;
    }

    // 一条线族: 把边界旋到"线水平"的坐标系里逐条扫, 交点配对得到内部区间, 再按短划切段。
    private static void EmitFamily(IReadOnlyList<(double x, double y)> boundary, PatLine line,
                                   double scale, double angleDeg,
                                   List<(double, double, double, double)> result)
    {
        double deltaY = Math.Abs(line.DeltaY) * scale;
        if (deltaY <= 1e-12) return;

        double a = (line.Angle + angleDeg) * Math.PI / 180.0;
        double ca = Math.Cos(a), sa = Math.Sin(a);

        // 基点也要跟着整体旋转(图案是一个整体, 不能只转线不转基点, 否则交错关系错位)
        double ga = angleDeg * Math.PI / 180.0;
        double gc = Math.Cos(ga), gs = Math.Sin(ga);
        double bx0 = line.X * scale, by0 = line.Y * scale;
        double bx = bx0 * gc - by0 * gs, by = bx0 * gs + by0 * gc;
        double uBase = bx * ca + by * sa, vBase = -bx * sa + by * ca;

        int n = boundary.Count;
        var uv = new (double u, double v)[n];
        double vmin = double.MaxValue, vmax = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double x = boundary[i].x, y = boundary[i].y;
            double u = x * ca + y * sa, v = -x * sa + y * ca;
            uv[i] = (u, v);
            if (v < vmin) vmin = v; if (v > vmax) vmax = v;
        }
        if (vmax - vmin < 1e-12) return;

        long k0 = (long)Math.Ceiling((vmin - vBase) / deltaY);
        long k1 = (long)Math.Floor((vmax - vBase) / deltaY);
        if (k1 < k0) return;
        // 条数上限: 比例极小的时候 (vmax-vmin)/deltaY 可能上亿, 先掐掉再算
        if (k1 - k0 > MaxSegments) k1 = k0 + MaxSegments;

        double deltaX = line.DeltaX * scale;
        var xs = new List<double>();
        for (long k = k0; k <= k1; k++)
        {
            if (result.Count >= MaxSegments) return;
            double v = vBase + k * deltaY;
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                var (u1, v1) = uv[i];
                var (u2, v2) = uv[(i + 1) % n];
                bool between = (v1 <= v && v2 > v) || (v2 <= v && v1 > v);   // 半开区间: 顶点不重复计数
                if (!between) continue;
                double t = (v - v1) / (v2 - v1);
                xs.Add(u1 + t * (u2 - u1));
            }
            if (xs.Count < 2) continue;
            xs.Sort();

            double uOrigin = uBase + k * deltaX;   // 该条线自身的短划起点(错位靠它)
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                double ua = xs[i], ub = xs[i + 1];
                if (ub - ua < 1e-12) continue;
                if (line.Dash == null) Emit(ua, ub);
                else EmitDashed(ua, ub, uOrigin, line.Dash, scale, deltaY, Emit);
            }

            void Emit(double ua, double ub)
            {
                if (result.Count >= MaxSegments) return;
                double ax = ua * ca - v * sa, ay = ua * sa + v * ca;   // 逆旋回原坐标
                double bx2 = ub * ca - v * sa, by2 = ub * sa + v * ca;
                result.Add((ax, ay, bx2, by2));
            }
        }
    }

    // 短划: 沿线方向按 画/空 序列切段。序列从该条线的原点算起并周期重复; 0 长度 = 点(给一小段以便看得见)。
    private static void EmitDashed(double ua, double ub, double uOrigin, double[] dash, double scale,
                                   double deltaY, Action<double, double> emit)
    {
        double period = 0;
        foreach (var d in dash) period += Math.Abs(d) * scale;
        if (period <= 1e-12) { emit(ua, ub); return; }

        double dotLen = Math.Max(deltaY * 0.02, 1e-9);   // 点在纯线框里画成极短的一段
        double s = ua - uOrigin, e = ub - uOrigin;
        double cycle = Math.Floor(s / period) * period;
        for (double c = cycle; c < e; c += period)
        {
            double at = c;
            foreach (var d in dash)
            {
                double len = Math.Abs(d) * scale;
                bool draw = d > 0;
                bool dot = Math.Abs(d) < 1e-12;
                if (dot) { len = dotLen; draw = true; }
                double x0 = at, x1 = at + len;
                at = x1;
                if (!draw) continue;
                double lo = Math.Max(x0, s), hi = Math.Min(x1, e);
                if (hi - lo > 1e-12) emit(uOrigin + lo, uOrigin + hi);
            }
        }
    }
}
