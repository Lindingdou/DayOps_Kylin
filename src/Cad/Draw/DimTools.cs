using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 标注样式（DIM 变量：文字高/小数位/端刻度·箭头比例）——忠实原"标注样式"对话框的文档级设置。
/// 影响新建标注(线性/半径)。纯数据，可单测。
/// </summary>
public sealed class DimStyle
{
    /// <summary>固定文字高；0 = 用调用方传入的缩放相关高度(DIMTXT=0 自动)。</summary>
    public double TextHeight = 0;
    /// <summary>距离/半径文字小数位(DIMDEC)。</summary>
    public int DecimalPlaces = 2;
    /// <summary>端刻度长 = 高 × 比(DIMTXT 相对)。</summary>
    public double TickRatio = 0.4;
    /// <summary>箭头长 = 高 × 比(DIMASZ 相对)。</summary>
    public double ArrowRatio = 0.5;
    public double ArrowWidthRatio = 0.25;
    /// <summary>文字偏移 = 高 × 比(DIMGAP 相对)。</summary>
    public double TextOffsetRatio = 0.6;
    /// <summary>延伸线起点偏移 = 高 × 比(DIMEXO 相对): 测点与延伸线起点的间隙。</summary>
    public double ExtLineOffsetRatio = 0.2;
    /// <summary>延伸线超出量 = 高 × 比(DIMEXE 相对): 延伸线越过尺寸线的长度。</summary>
    public double ExtLineExtensionRatio = 0.4;

    public static readonly DimStyle Default = new();

    /// <summary>按小数位生成格式串：0 位→"0"(整数)，n 位→"0.###…"(末尾零裁剪，同原 0.## 风格)。</summary>
    public string NumberFormat => DecimalPlaces <= 0 ? "0" : "0." + new string('#', DecimalPlaces);
}

/// <summary>
/// 线性标注（尺寸）—— 两点 → 尺寸线 + 端点刻度 + 距离文字（复用单笔画字体）。纯逻辑、可单测。
/// </summary>
public static class DimTools
{
    public static List<SceneEntity> Build(double x1, double y1, double x2, double y2, double h, DimStyle? style = null)
    {
        style ??= DimStyle.Default;
        double H = style.TextHeight > 0 ? style.TextHeight : h;
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        list.Add(L(x1, y1, x2, y2));                       // 尺寸线
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return list;
        double nx = -dy / len, ny = dx / len;              // 单位法线
        double tk = H * style.TickRatio;
        list.Add(L(x1 - nx * tk, y1 - ny * tk, x1 + nx * tk, y1 + ny * tk));   // 端刻度
        list.Add(L(x2 - nx * tk, y2 - ny * tk, x2 + nx * tk, y2 + ny * tk));

        string s = len.ToString(style.NumberFormat, CultureInfo.InvariantCulture);
        double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        double ox = nx * H * style.TextOffsetRatio, oy = ny * H * style.TextOffsetRatio;   // 文字偏到尺寸线上方
        double tw = s.Length * H * 0.8;                     // 粗略文字宽 → 居中
        list.Add(new TextEntity { X = mx + ox - tw / 2, Y = my + oy, Height = H, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }

    /// <summary>
    /// 线性标注(标准 DIMLINEAR, 3 点)：测两点 (x1,y1)-(x2,y2), 尺寸线偏移过点 (offX,offY)。
    /// 画 延伸线(从测点经 DIMEXO 间隙到尺寸线+DIMEXE 超出) + 偏移尺寸线 + 两端箭头 + 距离文字。忠实原版偏移标注。纯逻辑可单测。
    /// </summary>
    public static List<SceneEntity> BuildLinear(double x1, double y1, double x2, double y2, double offX, double offY, double h, DimStyle? style = null)
    {
        style ??= DimStyle.Default;
        double H = style.TextHeight > 0 ? style.TextHeight : h;
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) { list.Add(L(x1, y1, x2, y2)); return list; }
        double ux = dx / len, uy = dy / len;                    // 测量方向
        double nx = -uy, ny = ux;                               // 单位法线
        double proj = (offX - x1) * nx + (offY - y1) * ny;      // 偏移点到测量线的带符号垂距 = 尺寸线偏移
        double sgn = proj >= 0 ? 1 : -1;
        double gap = H * style.ExtLineOffsetRatio, ext = H * style.ExtLineExtensionRatio;

        double d1x = x1 + nx * proj, d1y = y1 + ny * proj;      // 尺寸线端点(测点投到偏移处)
        double d2x = x2 + nx * proj, d2y = y2 + ny * proj;
        // 延伸线: 从测点+间隙 到 尺寸线+超出
        list.Add(L(x1 + nx * sgn * gap, y1 + ny * sgn * gap, d1x + nx * sgn * ext, d1y + ny * sgn * ext));
        list.Add(L(x2 + nx * sgn * gap, y2 + ny * sgn * gap, d2x + nx * sgn * ext, d2y + ny * sgn * ext));
        list.Add(L(d1x, d1y, d2x, d2y));                        // 偏移尺寸线

        double ah = H * style.ArrowRatio, aw = H * style.ArrowWidthRatio;   // 两端箭头(指向外)
        list.Add(L(d1x, d1y, d1x + ux * ah + nx * aw, d1y + uy * ah + ny * aw));
        list.Add(L(d1x, d1y, d1x + ux * ah - nx * aw, d1y + uy * ah - ny * aw));
        list.Add(L(d2x, d2y, d2x - ux * ah + nx * aw, d2y - uy * ah + ny * aw));
        list.Add(L(d2x, d2y, d2x - ux * ah - nx * aw, d2y - uy * ah - ny * aw));

        string s = len.ToString(style.NumberFormat, CultureInfo.InvariantCulture);
        double mx = (d1x + d2x) / 2, my = (d1y + d2y) / 2;
        double tox = nx * sgn * H * style.TextOffsetRatio, toy = ny * sgn * H * style.TextOffsetRatio;
        double tw = s.Length * H * 0.8;
        list.Add(new TextEntity { X = mx + tox - tw / 2, Y = my + toy, Height = H, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }

    /// <summary>半径标注(DIMRADIAL)：圆心→(dirx,diry) 方向的圆周点，径向线 + 箭头 + "R值"文字。</summary>
    public static List<SceneEntity> BuildRadial(double cx, double cy, double radius, double dirx, double diry, double h, DimStyle? style = null)
    {
        style ??= DimStyle.Default;
        double H = style.TextHeight > 0 ? style.TextHeight : h;
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        double dl = Math.Sqrt(dirx * dirx + diry * diry);
        double ux = dl < 1e-9 ? 1 : dirx / dl, uy = dl < 1e-9 ? 0 : diry / dl;   // 单位方向
        double ex = cx + ux * radius, ey = cy + uy * radius;                       // 圆周端点
        list.Add(L(cx, cy, ex, ey));                                              // 径向线

        // 圆周端箭头：沿方向回缩，左右各张开
        double ah = H * style.ArrowRatio, aw = H * style.ArrowWidthRatio;
        double bx = ex - ux * ah, by = ey - uy * ah;   // 箭底
        double px = -uy, py = ux;                       // 垂向
        list.Add(L(ex, ey, bx + px * aw, by + py * aw));
        list.Add(L(ex, ey, bx - px * aw, by - py * aw));

        string s = "R" + radius.ToString(style.NumberFormat, CultureInfo.InvariantCulture);
        list.Add(new TextEntity { X = ex + ux * H * 0.3, Y = ey + uy * H * 0.3, Height = H, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }

    /// <summary>直径标注(DIMDIAMETER)：过圆心的直径线(两端圆周点)+ 两端箭头 + "Ø值"(值=2·半径)文字。</summary>
    public static List<SceneEntity> BuildDiameter(double cx, double cy, double radius, double dirx, double diry, double h, DimStyle? style = null)
    {
        style ??= DimStyle.Default;
        double H = style.TextHeight > 0 ? style.TextHeight : h;
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        double dl = Math.Sqrt(dirx * dirx + diry * diry);
        double ux = dl < 1e-9 ? 1 : dirx / dl, uy = dl < 1e-9 ? 0 : diry / dl;
        double e1x = cx + ux * radius, e1y = cy + uy * radius;   // 一端圆周点
        double e2x = cx - ux * radius, e2y = cy - uy * radius;   // 对端(过圆心)
        list.Add(L(e1x, e1y, e2x, e2y));                         // 直径线

        double ah = H * style.ArrowRatio, aw = H * style.ArrowWidthRatio;
        double px = -uy, py = ux;
        double b1x = e1x - ux * ah, b1y = e1y - uy * ah;          // 端1箭头(指外 +u)
        list.Add(L(e1x, e1y, b1x + px * aw, b1y + py * aw)); list.Add(L(e1x, e1y, b1x - px * aw, b1y - py * aw));
        double b2x = e2x + ux * ah, b2y = e2y + uy * ah;          // 端2箭头(指外 -u)
        list.Add(L(e2x, e2y, b2x + px * aw, b2y + py * aw)); list.Add(L(e2x, e2y, b2x - px * aw, b2y - py * aw));

        string s = "Ø" + (2 * radius).ToString(style.NumberFormat, CultureInfo.InvariantCulture);
        list.Add(new TextEntity { X = e1x + ux * H * 0.3, Y = e1y + uy * H * 0.3, Height = H, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }

    /// <summary>
    /// 角度标注(DIMANGULAR)：顶点 V + 两射线到 P1/P2 → 劣弧(≤180°) + 度数"n°"文字 + 两延长线。
    /// arcR≤0 用 h×3。纯逻辑、可单测。
    /// </summary>
    public static List<SceneEntity> BuildAngular(double vx, double vy, double p1x, double p1y, double p2x, double p2y,
        double arcR, double h, DimStyle? style = null)
    {
        style ??= DimStyle.Default;
        double H = style.TextHeight > 0 ? style.TextHeight : h;
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        double a1 = Math.Atan2(p1y - vy, p1x - vx);
        double a2 = Math.Atan2(p2y - vy, p2x - vx);
        double diff = a2 - a1;
        while (diff <= -Math.PI) diff += 2 * Math.PI;
        while (diff > Math.PI) diff -= 2 * Math.PI;               // (-π,π] 取劣弧
        double deg = Math.Abs(diff) * 180.0 / Math.PI;
        if (arcR <= 1e-9) arcR = H * 3;

        double ext = arcR * 1.1;                                  // 两延长线沿射线
        list.Add(L(vx, vy, vx + Math.Cos(a1) * ext, vy + Math.Sin(a1) * ext));
        list.Add(L(vx, vy, vx + Math.Cos(a2) * ext, vy + Math.Sin(a2) * ext));

        int steps = Math.Max(2, (int)(Math.Abs(diff) / (Math.PI / 18)));   // ~10°/段
        double ppx = vx + Math.Cos(a1) * arcR, ppy = vy + Math.Sin(a1) * arcR;
        for (int i = 1; i <= steps; i++)
        {
            double ang = a1 + diff * i / steps;
            double npx = vx + Math.Cos(ang) * arcR, npy = vy + Math.Sin(ang) * arcR;
            list.Add(L(ppx, ppy, npx, npy));                     // 弧折线段
            ppx = npx; ppy = npy;
        }

        double am = a1 + diff / 2;                                // 中角外侧放度数
        double tx = vx + Math.Cos(am) * (arcR + H * 0.5), ty = vy + Math.Sin(am) * (arcR + H * 0.5);
        string s = deg.ToString(style.NumberFormat, CultureInfo.InvariantCulture) + "°";
        list.Add(new TextEntity { X = tx, Y = ty, Height = H, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }

    /// <summary>
    /// 坐标标注(COORD)：点 → 小十字标记 + 引线 + "X=… Y=…"(可含 Z) 文字。
    /// 忠实原 CAD 工具栏「坐标标注」意图(测量/矿业标准注记)。leaderDx/Dy = 引线到文字锚点的相对位移。纯逻辑、可单测。
    /// </summary>
    public static List<SceneEntity> BuildCoordLabel(double px, double py, double leaderDx, double leaderDy,
        double h, DimStyle? style = null, double? z = null)
    {
        style ??= DimStyle.Default;
        double H = style.TextHeight > 0 ? style.TextHeight : h;
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        double m = H * 0.4;                                 // 点标记(小十字)
        list.Add(L(px - m, py, px + m, py));
        list.Add(L(px, py - m, px, py + m));
        double ax = px + leaderDx, ay = py + leaderDy;      // 文字锚点
        list.Add(L(px, py, ax, ay));                        // 引线

        var inv = CultureInfo.InvariantCulture;
        string fmt = style.NumberFormat;
        string s = z.HasValue
            ? $"X={px.ToString(fmt, inv)} Y={py.ToString(fmt, inv)} Z={z.Value.ToString(fmt, inv)}"
            : $"X={px.ToString(fmt, inv)} Y={py.ToString(fmt, inv)}";
        double tw = s.Length * H * 0.8;                     // 粗略文字宽
        double textX = leaderDx >= 0 ? ax + H * 0.3 : ax - H * 0.3 - tw;   // 引线朝左则文字右对齐
        list.Add(new TextEntity { X = textX, Y = ay, Height = H, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }
}
