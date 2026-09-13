using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// Gizmo —— 选中对象上的三轴变换手柄(同 AutoCAD 3DMOVE gizmo)的画法与命中/拖拽几何，纯函数、可单测。
///
/// 手柄锚在选择集的三维包围盒中心：+X 红 / +Y 绿 / +Z 蓝三根带锥形箭头的轴，屏幕上恒定像素长度
/// (世界长度 = 像素 × 该点处"每像素世界长度")；光标压上某根轴变黄，按住沿该轴拖动 = 选中对象沿轴平移。
/// 2D 俯视只画 X/Y(Z 轴垂直屏幕, 投影成一个点, 既看不见也拖不动)。
///
/// 几何一律输出成 <see cref="PolylineEntity"/>(逐顶点 Zs)再由调用方 Tessellate：镶嵌走实体自己那条路，
/// 渲染局部原点(RenderOrigin)之类的坐标口径由实体统一处理，这里只管世界坐标。
/// </summary>
public static class GizmoGlyph
{
    /// <summary>轴长(屏幕像素, DIP)。</summary>
    public const double AxisPx = 90;
    /// <summary>箭头锥长(像素)。</summary>
    public const double HeadPx = 16;
    /// <summary>箭头锥底半径(像素)。</summary>
    public const double HeadRadiusPx = 5.5;
    /// <summary>轴命中容差(像素): 光标到轴投影线段的距离小于它算压在轴上。</summary>
    public const double HitTolPx = 7;
    /// <summary>中心死区(像素): 三轴交汇处分不清点的是哪根，一律不算命中(让给夹点/点选)。</summary>
    public const double DeadZonePx = 8;
    /// <summary>锥底圆分段数。</summary>
    public const int HeadSegments = 12;
    /// <summary>拖拽时沿轴画的约束辅助线长度(轴长倍数, 两侧各此倍)。</summary>
    public const double GuideLenFactor = 4;

    public static readonly (float r, float g, float b)[] AxisColors =
    {
        (0.95f, 0.25f, 0.20f),   // X 红
        (0.25f, 0.85f, 0.25f),   // Y 绿
        (0.30f, 0.55f, 1.00f),   // Z 蓝
    };
    /// <summary>悬停/拖拽中的轴：金黄(AutoCAD 惯例)。</summary>
    public static readonly (float r, float g, float b) HoverColor = (1.0f, 0.85f, 0.10f);

    public static readonly (double x, double y, double z)[] AxisDirs = { (1, 0, 0), (0, 1, 0), (0, 0, 1) };
    public static readonly string[] AxisNames = { "X", "Y", "Z" };

    /// <summary>
    /// 世界点处"每像素世界长度"：用世界→屏幕投影在该点的雅可比(三根世界轴各探一小步)取最小奇异值 ——
    /// 针孔透视下它恰等于 焦距/深度(纯横向尺度; 最大奇异值还混着离轴点"沿深度动也会在屏幕上跑"的那一份, 偏大),
    /// 与相机朝向无关；2D 正交下两个奇异值相等, 退化为常数。
    /// 探针步长自适应到约 10 像素, 免得近处大步长把透视非线性探进来。投影不到(相机后方)返回 0。
    /// </summary>
    public static double WorldPerPixel(Func<double, double, double, (double sx, double sy)?> proj, double cx, double cy, double cz)
    {
        var p0 = proj(cx, cy, cz);
        if (p0 == null) return 0;
        double eps = 1.0, sigma = 0;
        for (int iter = 0; iter < 2; iter++)
        {
            double m00 = 0, m01 = 0, m11 = 0;
            foreach (var a in AxisDirs)
            {
                var pk = proj(cx + eps * a.x, cy + eps * a.y, cz + eps * a.z);
                if (pk == null) return 0;
                double jx = (pk.Value.sx - p0.Value.sx) / eps, jy = (pk.Value.sy - p0.Value.sy) / eps;
                m00 += jx * jx; m01 += jx * jy; m11 += jy * jy;
            }
            double half = (m00 - m11) / 2;
            double lmin = (m00 + m11) / 2 - Math.Sqrt(half * half + m01 * m01);
            sigma = Math.Sqrt(Math.Max(lmin, 0));
            if (sigma <= 1e-12 || !double.IsFinite(sigma)) return 0;
            double px = sigma * eps;
            if (px > 0.5 && px < 200) break;     // 探针 0.5~200 px 之间够线性
            eps = 10.0 / sigma;                   // 否则按 10 px 重探一次
        }
        return 1.0 / sigma;
    }

    /// <summary>
    /// 建手柄几何。<paramref name="viewDir"/> 给视线方向(单位向量)时轴杆按屏幕垂直方向铺 5 条线加粗(GL 线宽只有 1px)；
    /// null 则单线。<paramref name="hover"/>/<paramref name="dragging"/> 为轴序号(0/1/2, -1 无)，命中的轴金黄；
    /// 拖拽中另画贯穿中心的长约束线。
    /// </summary>
    public static List<PolylineEntity> Build((double x, double y, double z) c, double wpp,
        (double x, double y, double z)? viewDir, bool only2D, int hover, int dragging)
    {
        var list = new List<PolylineEntity>();
        if (wpp <= 0 || !double.IsFinite(wpp)) return list;
        double len = AxisPx * wpp, head = HeadPx * wpp, hr = HeadRadiusPx * wpp;
        for (int k = 0; k < 3; k++)
        {
            if (only2D && k == 2) continue;
            var a = AxisDirs[k];
            bool lit = k == hover || k == dragging;
            var col = lit ? HoverColor : AxisColors[k];
            var tip = Add(c, a, len);
            var baseC = Add(c, a, len - head);

            // 轴杆: 屏幕上垂直于轴的方向(轴 × 视线)铺几条平行线, 视觉上成 2~3 px 粗
            var n = viewDir != null ? Cross(a, viewDir.Value) : (0, 0, 0);
            double nl = Len(n);
            double[] offsets = nl > 0.05 ? new[] { -1.0, -0.5, 0, 0.5, 1.0 } : new[] { 0.0 };
            if (nl > 0.05) n = (n.x / nl, n.y / nl, n.z / nl);
            foreach (double off in offsets)
            {
                var o0 = Add(c, n, off * wpp);
                var o1 = Add(baseC, n, off * wpp);
                list.Add(Line(o0, o1, col));
            }

            if (only2D)
            {
                // 2D 俯视: 平面实心三角箭头(锥体从正上方看是两个叠着的三角, 反而花)。底边沿 XY 面内垂直轴的方向
                var pd = k == 0 ? (0.0, 1.0, 0.0) : (1.0, 0.0, 0.0);
                const int fan = 8;
                for (int i = 0; i <= fan; i++)
                {
                    double f = -1 + 2.0 * i / fan;
                    list.Add(Line(tip, Add(baseC, pd, f * hr), col));
                }
                list.Add(Line(Add(baseC, pd, -hr), Add(baseC, pd, hr), col));
            }
            else
            {
                // 3D: 线框锥 —— 锥尖到锥底圆各辐条 + 底圆环
                var (u, w) = Basis(a);
                var ring = new PolylineEntity { Closed = true, Cr = col.r, Cg = col.g, Cb = col.b, Zs = new List<double>() };
                for (int i = 0; i < HeadSegments; i++)
                {
                    double th = 2 * Math.PI * i / HeadSegments;
                    var b = (baseC.x + hr * (Math.Cos(th) * u.x + Math.Sin(th) * w.x),
                             baseC.y + hr * (Math.Cos(th) * u.y + Math.Sin(th) * w.y),
                             baseC.z + hr * (Math.Cos(th) * u.z + Math.Sin(th) * w.z));
                    ring.Points.Add((b.Item1, b.Item2)); ring.Zs!.Add(b.Item3);
                    list.Add(Line(tip, b, col));
                }
                list.Add(ring);
            }

            // 拖拽中: 贯穿中心的约束线(轴色减淡), 说明"只沿这根轴走"
            if (k == dragging)
            {
                var g0 = Add(c, a, -GuideLenFactor * len);
                var g1 = Add(c, a, GuideLenFactor * len);
                var dim = (col.r * 0.6f, col.g * 0.6f, col.b * 0.6f);
                list.Add(Line(g0, g1, dim));
            }
        }
        return list;
    }

    /// <summary>
    /// 屏幕点压在哪根轴上：到各轴投影线段的像素距离最小且 ≤ 容差的那根；中心死区内或都不够近返回 -1。
    /// </summary>
    public static int HitAxis(Func<double, double, double, (double sx, double sy)?> proj,
        (double x, double y, double z) c, double wpp, bool only2D, double sx, double sy)
    {
        var p0 = proj(c.x, c.y, c.z);
        if (p0 == null || wpp <= 0) return -1;
        if (Dist(sx, sy, p0.Value.sx, p0.Value.sy) < DeadZonePx) return -1;
        double len = AxisPx * wpp;
        int best = -1; double bestD = HitTolPx;
        for (int k = 0; k < 3; k++)
        {
            if (only2D && k == 2) continue;
            var t = Add(c, AxisDirs[k], len);
            var pt = proj(t.x, t.y, t.z);
            if (pt == null) continue;
            double d = SegDistPx(sx, sy, p0.Value.sx, p0.Value.sy, pt.Value.sx, pt.Value.sy);
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    /// <summary>
    /// 沿轴拖拽的量：光标射线与轴线的最近点在轴上的参数(世界单位, 从中心起, 正向为轴正方向)。
    /// 射线与轴几乎平行(对着轴看)时无解返回 null —— 此时拖多少都没意义。
    /// </summary>
    public static double? AxisParam((double ox, double oy, double oz, double dx, double dy, double dz) ray,
        (double x, double y, double z) c, int axis)
    {
        if (axis < 0 || axis > 2) return null;
        var a = AxisDirs[axis];
        double dl = Math.Sqrt(ray.dx * ray.dx + ray.dy * ray.dy + ray.dz * ray.dz);
        if (dl < 1e-12) return null;
        double dx = ray.dx / dl, dy = ray.dy / dl, dz = ray.dz / dl;
        double wx = c.x - ray.ox, wy = c.y - ray.oy, wz = c.z - ray.oz;
        double b = a.x * dx + a.y * dy + a.z * dz;          // 轴·射线
        double denom = 1 - b * b;
        if (denom < 1e-6) return null;
        double d1 = a.x * wx + a.y * wy + a.z * wz;         // 轴·(中心-射线起点)
        double e1 = dx * wx + dy * wy + dz * wz;            // 射线·(中心-射线起点)
        return (b * e1 - d1) / denom;
    }

    /// <summary>点到线段的像素距离。</summary>
    public static double SegDistPx(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        double t = len2 < 1e-12 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        return Dist(px, py, ax + t * dx, ay + t * dy);
    }

    /// <summary>三维包围盒 12 条棱, 输出为线段实体(供拖拽幽灵: 大网/点云不逐面画, 画个框说明在哪)。</summary>
    public static List<PolylineEntity> BoxEdges(double x0, double y0, double z0, double x1, double y1, double z1,
        (float r, float g, float b) col)
    {
        var list = new List<PolylineEntity>();
        foreach (double z in new[] { z0, z1 })
        {
            list.Add(Line((x0, y0, z), (x1, y0, z), col)); list.Add(Line((x1, y0, z), (x1, y1, z), col));
            list.Add(Line((x1, y1, z), (x0, y1, z), col)); list.Add(Line((x0, y1, z), (x0, y0, z), col));
        }
        list.Add(Line((x0, y0, z0), (x0, y0, z1), col)); list.Add(Line((x1, y0, z0), (x1, y0, z1), col));
        list.Add(Line((x1, y1, z0), (x1, y1, z1), col)); list.Add(Line((x0, y1, z0), (x0, y1, z1), col));
        return list;
    }

    private static PolylineEntity Line((double x, double y, double z) p, (double x, double y, double z) q, (float r, float g, float b) col)
        => new()
        {
            Points = new List<(double x, double y)> { (p.x, p.y), (q.x, q.y) },
            Zs = new List<double> { p.z, q.z },
            Cr = col.r, Cg = col.g, Cb = col.b,
        };

    private static (double x, double y, double z) Add((double x, double y, double z) p, (double x, double y, double z) d, double s)
        => (p.x + d.x * s, p.y + d.y * s, p.z + d.z * s);

    private static (double x, double y, double z) Cross((double x, double y, double z) a, (double x, double y, double z) b)
        => (a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

    private static double Len((double x, double y, double z) v) => Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>与轴 a 垂直的一对单位向量(锥底圆用)。</summary>
    private static ((double x, double y, double z) u, (double x, double y, double z) w) Basis((double x, double y, double z) a)
    {
        var u = Math.Abs(a.z) < 0.9 ? Cross(a, (0, 0, 1)) : Cross(a, (1, 0, 0));
        double ul = Len(u); u = (u.x / ul, u.y / ul, u.z / ul);
        var w = Cross(a, u);
        return (u, w);
    }
}
