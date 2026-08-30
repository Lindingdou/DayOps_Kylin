using System;
using System.Collections.Generic;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// DXF 导入 —— 用 ACadSharp（纯托管、跨平台）读 .dxf，把实体几何提取成线段（交错 P3_C3），
/// 供 <c>GlRenderer.Upload</c> 直接上屏。对应 Windows 版 DwgDxfImportService 的托管读取路径。
///
/// 覆盖：Line / LwPolyline / Polyline2D / Polyline3D / Circle / Arc（圆、弧按分段折线近似）。
/// 说明：最终架构里几何应交 C++ 内核（AcDb）渲染；此托管提取用于骨架阶段先"能打开图纸看"，
/// 待内核接入后由内核渲染路径取代。颜色暂用统一色，按图层/ACI 上色为后续项。
/// </summary>
public static class DxfImportService
{
    /// <summary>圆/整弧的分段数（折线近似）。</summary>
    private const int CircleSegments = 64;

    /// <summary>导入几何统一色（浅蓝灰）。按图层颜色上色留作后续。</summary>
    private static readonly (float r, float g, float b) LineColor = (0.80f, 0.86f, 0.93f);

    public sealed class ImportResult
    {
        public bool Success { get; set; }
        public int EntityCount { get; set; }
        public int SegmentCount { get; set; }
        /// <summary>交错 P3_C3（位置3+颜色3），GL_LINES 用，每 2 顶点一段。</summary>
        public float[] LineVertices { get; set; } = Array.Empty<float>();
        /// <summary>[minX, minY, maxX, maxY]，供范围缩放。</summary>
        public double[] Bounds { get; set; } = { 0, 0, 0, 0 };
        /// <summary>按图元类型（中文名）计数，供对象管理器。</summary>
        public Dictionary<string, int> TypeCounts { get; } = new();
        /// <summary>各图层几何（图层名 → 交错 P3_C3），供图层管理器按层显隐。</summary>
        public Dictionary<string, float[]> LayerGeometry { get; } = new();
        /// <summary>图层出现顺序（稳定，供面板列出）。</summary>
        public List<string> LayerOrder { get; } = new();
        /// <summary>按图层的实体计数。</summary>
        public Dictionary<string, int> LayerCounts { get; } = new();
        /// <summary>各图元类型几何（类型中文名 → 交错 P3_C3），供对象树选择高亮。</summary>
        public Dictionary<string, float[]> TypeGeometry { get; } = new();
        public List<string> Warnings { get; } = new();
        public string? Error { get; set; }
    }

    public static ImportResult Load(string filePath)
    {
        var result = new ImportResult();

        CadDocument doc;
        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".dxf")
            {
                result.Error = $"暂只支持 .dxf（DWG 后续接入）：{ext}";
                return result;
            }
            doc = DxfReader.Read(filePath);
        }
        catch (Exception ex)
        {
            result.Error = $"读取失败：{ex.Message}";
            return result;
        }

        var verts = new List<float>(4096);
        var byLayer = new Dictionary<string, List<float>>();
        var byType = new Dictionary<string, List<float>>();
        List<float> cur = new();                                      // 当前实体所属图层的几何缓冲
        List<float> curType = new();                                  // 当前实体所属类型的几何缓冲
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        float cr = LineColor.r, cg = LineColor.g, cb = LineColor.b;   // 逐实体更新
        Func<(double x, double y), (double x, double y)>? xform = null;   // 块引用展开时的累积变换
        int insertDepth = 0;

        void Seg(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            if (xform != null) { (x0, y0) = xform((x0, y0)); (x1, y1) = xform((x1, y1)); }
            verts.Add((float)x0); verts.Add((float)y0); verts.Add((float)z0); verts.Add(cr); verts.Add(cg); verts.Add(cb);
            verts.Add((float)x1); verts.Add((float)y1); verts.Add((float)z1); verts.Add(cr); verts.Add(cg); verts.Add(cb);
            cur.Add((float)x0); cur.Add((float)y0); cur.Add((float)z0); cur.Add(cr); cur.Add(cg); cur.Add(cb);
            cur.Add((float)x1); cur.Add((float)y1); cur.Add((float)z1); cur.Add(cr); cur.Add(cg); cur.Add(cb);
            curType.Add((float)x0); curType.Add((float)y0); curType.Add((float)z0); curType.Add(cr); curType.Add(cg); curType.Add(cb);
            curType.Add((float)x1); curType.Add((float)y1); curType.Add((float)z1); curType.Add(cr); curType.Add(cg); curType.Add(cb);
            if (x0 < minX) minX = x0; if (y0 < minY) minY = y0; if (x0 > maxX) maxX = x0; if (y0 > maxY) maxY = y0;
            if (x1 < minX) minX = x1; if (y1 < minY) minY = y1; if (x1 > maxX) maxX = x1; if (y1 > maxY) maxY = y1;
        }

        // 圆/弧折线近似：从 a0 到 a1（弧度）分段
        void ArcSegs(double cx, double cy, double cz, double radius, double a0, double a1)
        {
            int n = Math.Max(8, (int)(CircleSegments * Math.Abs(a1 - a0) / (Math.PI * 2)));
            double prevX = cx + radius * Math.Cos(a0), prevY = cy + radius * Math.Sin(a0);
            for (int i = 1; i <= n; i++)
            {
                double t = a0 + (a1 - a0) * i / n;
                double x = cx + radius * Math.Cos(t), y = cy + radius * Math.Sin(t);
                Seg(prevX, prevY, cz, x, y, cz);
                prevX = x; prevY = y;
            }
        }

        // 椭圆折线近似：主轴向量 + 半径比 + 起止参数
        void EllipseSegs(Ellipse ell)
        {
            double ecx = ell.Center.X, ecy = ell.Center.Y, ecz = ell.Center.Z;
            double majX = ell.MajorAxisEndPoint.X, majY = ell.MajorAxisEndPoint.Y;
            double majLen = Math.Sqrt(majX * majX + majY * majY);
            if (majLen < 1e-9) return;
            double rot = Math.Atan2(majY, majX);
            double minLen = majLen * ell.RadiusRatio;
            double t0 = ell.StartParameter, t1 = ell.EndParameter;
            if (t1 <= t0) t1 += Math.PI * 2;
            int n = Math.Max(16, (int)(CircleSegments * Math.Abs(t1 - t0) / (Math.PI * 2)));
            double cosR = Math.Cos(rot), sinR = Math.Sin(rot);
            double px = 0, py = 0;
            for (int i = 0; i <= n; i++)
            {
                double t = t0 + (t1 - t0) * i / n;
                double lx = majLen * Math.Cos(t), ly = minLen * Math.Sin(t);
                double wx = ecx + lx * cosR - ly * sinR;
                double wy = ecy + lx * sinR + ly * cosR;
                if (i > 0) Seg(px, py, ecz, wx, wy, ecz);
                px = wx; py = wy;
            }
        }

        // 样条：De Boor 采样为折线；无有效节点则退回拟合点 / 控制多边形
        void SplineSegs(Spline sp)
        {
            var cps = sp.ControlPoints;
            int deg = sp.Degree;
            var knots = sp.Knots;
            if (cps == null || cps.Count < 2)
            {
                var fps = sp.FitPoints;
                if (fps != null)
                    for (int i = 0; i + 1 < fps.Count; i++)
                        Seg(fps[i].X, fps[i].Y, fps[i].Z, fps[i + 1].X, fps[i + 1].Y, fps[i + 1].Z);
                return;
            }
            if (knots == null || deg < 1 || knots.Count < cps.Count + deg + 1)
            {
                for (int i = 0; i + 1 < cps.Count; i++)
                    Seg(cps[i].X, cps[i].Y, cps[i].Z, cps[i + 1].X, cps[i + 1].Y, cps[i + 1].Z);
                return;
            }
            var sx = new double[cps.Count]; var sy = new double[cps.Count]; var sz = new double[cps.Count];
            for (int i = 0; i < cps.Count; i++) { sx[i] = cps[i].X; sy[i] = cps[i].Y; sz[i] = cps[i].Z; }
            int n = cps.Count - 1;
            double u0 = knots[deg], u1 = knots[n + 1];
            int samples = Math.Max(CircleSegments, cps.Count * 8);
            double prevx = 0, prevy = 0, prevz = 0;
            for (int s = 0; s <= samples; s++)
            {
                double u = s == samples ? u1 : u0 + (u1 - u0) * s / samples;
                var pt = EvalBSpline(sx, sy, sz, knots, deg, u);
                if (s > 0) Seg(prevx, prevy, prevz, pt.x, pt.y, pt.z);
                prevx = pt.x; prevy = pt.y; prevz = pt.z;
            }
        }

        // 单个实体 → 线段（Insert 递归展开）
        void Emit(Entity ent)
        {
            switch (ent)
            {
                case Line ln:
                    Seg(ln.StartPoint.X, ln.StartPoint.Y, ln.StartPoint.Z, ln.EndPoint.X, ln.EndPoint.Y, ln.EndPoint.Z);
                    break;

                case LwPolyline lp:
                {
                    var vs = lp.Vertices;
                    for (int i = 0; i + 1 < vs.Count; i++)
                        Seg(vs[i].Location.X, vs[i].Location.Y, lp.Elevation, vs[i + 1].Location.X, vs[i + 1].Location.Y, lp.Elevation);
                    if (lp.IsClosed && vs.Count > 1)
                        Seg(vs[vs.Count - 1].Location.X, vs[vs.Count - 1].Location.Y, lp.Elevation, vs[0].Location.X, vs[0].Location.Y, lp.Elevation);
                    break;
                }

                case Polyline2D p2:
                {
                    var vs = p2.Vertices;
                    for (int i = 0; i + 1 < vs.Count; i++)
                        Seg(vs[i].Location.X, vs[i].Location.Y, vs[i].Location.Z, vs[i + 1].Location.X, vs[i + 1].Location.Y, vs[i + 1].Location.Z);
                    if (p2.IsClosed && vs.Count > 1)
                        Seg(vs[vs.Count - 1].Location.X, vs[vs.Count - 1].Location.Y, vs[vs.Count - 1].Location.Z, vs[0].Location.X, vs[0].Location.Y, vs[0].Location.Z);
                    break;
                }

                case Arc ar:
                {
                    double a0 = ar.StartAngle, a1 = ar.EndAngle;
                    if (a1 <= a0) a1 += Math.PI * 2;
                    ArcSegs(ar.Center.X, ar.Center.Y, ar.Center.Z, ar.Radius, a0, a1);
                    break;
                }

                case Circle ci:
                    ArcSegs(ci.Center.X, ci.Center.Y, ci.Center.Z, ci.Radius, 0, Math.PI * 2);
                    break;

                case Point pt:
                {
                    const double s = 0.5;   // 点标记十字半长
                    Seg(pt.Location.X - s, pt.Location.Y, pt.Location.Z, pt.Location.X + s, pt.Location.Y, pt.Location.Z);
                    Seg(pt.Location.X, pt.Location.Y - s, pt.Location.Z, pt.Location.X, pt.Location.Y + s, pt.Location.Z);
                    break;
                }

                case Ellipse ell:
                    EllipseSegs(ell);
                    break;

                case Insert ins:
                    ExpandInsert(ins);
                    break;

                case Spline sp:
                    SplineSegs(sp);
                    break;

                default:
                    result.Warnings.Add($"跳过未支持实体：{ent.GetType().Name}");
                    break;
            }
        }

        // 块引用展开：块内实体按插入变换(平移/旋转/缩放)发出；嵌套块递归(深度上限 8)
        void ExpandInsert(Insert ins)
        {
            if (insertDepth >= 8) return;
            var block = ins.Block;
            if (block == null) return;
            var outer = xform;
            double ipx = ins.InsertPoint.X, ipy = ins.InsertPoint.Y;
            double sx = ins.XScale == 0 ? 1 : ins.XScale;
            double sy = ins.YScale == 0 ? 1 : ins.YScale;
            double rot = ins.Rotation;
            xform = p =>
            {
                var t = ApplyInsert(p.x, p.y, ipx, ipy, sx, sy, rot);
                return outer != null ? outer(t) : t;
            };
            insertDepth++;
            foreach (var be in block.Entities)
            {
                (cr, cg, cb) = ColorOf(be);
                Emit(be);
            }
            insertDepth--;
            xform = outer;
        }

        int entCount = 0;
        try
        {
            var model = doc.BlockRecords["*Model_Space"];
            foreach (var e in model.Entities)
            {
                entCount++;
                (cr, cg, cb) = ColorOf(e);
                var cn = CnTypeName(e);
                if (cn != null) result.TypeCounts[cn] = result.TypeCounts.GetValueOrDefault(cn) + 1;

                // 路由到当前实体所属图层的几何缓冲
                string layerName = SafeLayerName(e);
                if (byLayer.TryGetValue(layerName, out var existing))
                {
                    cur = existing;
                }
                else
                {
                    cur = new List<float>();
                    byLayer[layerName] = cur;
                    result.LayerOrder.Add(layerName);
                }
                result.LayerCounts[layerName] = result.LayerCounts.GetValueOrDefault(layerName) + 1;

                // 路由到当前实体所属类型的几何缓冲（供对象树选择高亮）
                string typeName = cn ?? "其他";
                if (byType.TryGetValue(typeName, out var exType)) curType = exType;
                else { curType = new List<float>(); byType[typeName] = curType; }

                Emit(e);
            }
        }
        catch (Exception ex)
        {
            result.Error = $"提取几何失败：{ex.Message}";
            return result;
        }

        result.Success = true;
        result.EntityCount = entCount;
        result.SegmentCount = verts.Count / 12;   // 每段 2 顶点 × 6 float
        result.LineVertices = verts.ToArray();
        foreach (var kv in byLayer) result.LayerGeometry[kv.Key] = kv.Value.ToArray();
        foreach (var kv in byType) result.TypeGeometry[kv.Key] = kv.Value.ToArray();
        result.Bounds = verts.Count == 0 ? new double[] { 0, 0, 0, 0 } : new[] { minX, minY, maxX, maxY };
        return result;
    }

    /// <summary>实体颜色：ByLayer 取图层色；真彩色直接用 RGB；否则按 ACI 索引映射。失败回落统一色。</summary>
    private static (float r, float g, float b) ColorOf(Entity e)
    {
        try
        {
            var color = e.Color;
            if (color.IsByLayer && e.Layer != null)
                color = e.Layer.Color;
            if (color.IsTrueColor)
                return (color.R / 255f, color.G / 255f, color.B / 255f);
            return AciToRgb(color.Index);
        }
        catch
        {
            return LineColor;
        }
    }

    /// <summary>AutoCAD 颜色索引(ACI) → RGB。1-9 标准色（深底上做了适配），其余回落浅蓝灰。</summary>
    private static (float r, float g, float b) AciToRgb(int index) => index switch
    {
        1 => (0.90f, 0.32f, 0.32f),   // 红
        2 => (0.90f, 0.85f, 0.35f),   // 黄
        3 => (0.38f, 0.85f, 0.42f),   // 绿
        4 => (0.36f, 0.85f, 0.90f),   // 青
        5 => (0.42f, 0.56f, 0.96f),   // 蓝
        6 => (0.90f, 0.46f, 0.86f),   // 品红
        7 => (0.88f, 0.90f, 0.94f),   // 白/黑 → 深底上用浅色
        8 => (0.55f, 0.55f, 0.55f),   // 深灰
        9 => (0.75f, 0.75f, 0.78f),   // 浅灰
        _ => LineColor                // 其余/未知 → 默认
    };

    /// <summary>实体图层名；空/异常回落 "0"（AutoCAD 默认层）。</summary>
    private static string SafeLayerName(Entity e)
    {
        try { return string.IsNullOrEmpty(e.Layer?.Name) ? "0" : e.Layer!.Name; }
        catch { return "0"; }
    }

    /// <summary>图元 → 中文类型名（对象管理器用）；不支持的返回 null。</summary>
    private static string? CnTypeName(Entity e) => e switch
    {
        Line => "直线",
        LwPolyline => "多段线",
        Polyline2D => "多段线",   // 含 Polyline3D
        Arc => "圆弧",            // 必须在 Circle 前（Arc : Circle）
        Circle => "圆",
        Point => "点",
        Ellipse => "椭圆",
        Insert => "块引用",
        Spline => "样条",
        _ => null
    };

    /// <summary>块引用变换：块内坐标 → 缩放 → 绕原点旋转 → 平移到插入点。</summary>
    internal static (double x, double y) ApplyInsert(double x, double y, double ipx, double ipy, double sx, double sy, double rot)
    {
        double xs = x * sx, ys = y * sy;
        double cos = Math.Cos(rot), sin = Math.Sin(rot);
        return (xs * cos - ys * sin + ipx, xs * sin + ys * cos + ipy);
    }

    /// <summary>非有理 B 样条 De Boor 求值（Piegl &amp; Tiller）。px/py/pz 控制点分量，knots 节点，degree 阶，u 参数。</summary>
    internal static (double x, double y, double z) EvalBSpline(
        IReadOnlyList<double> px, IReadOnlyList<double> py, IReadOnlyList<double> pz,
        IReadOnlyList<double> knots, int degree, double u)
    {
        int n = px.Count - 1;
        int k = FindSpan(n, degree, u, knots);
        var dx = new double[degree + 1];
        var dy = new double[degree + 1];
        var dz = new double[degree + 1];
        for (int j = 0; j <= degree; j++) { int idx = k - degree + j; dx[j] = px[idx]; dy[j] = py[idx]; dz[j] = pz[idx]; }
        for (int r = 1; r <= degree; r++)
            for (int j = degree; j >= r; j--)
            {
                int i = k - degree + j;
                double denom = knots[i + degree - r + 1] - knots[i];
                double a = denom > 1e-12 ? (u - knots[i]) / denom : 0.0;
                dx[j] = (1 - a) * dx[j - 1] + a * dx[j];
                dy[j] = (1 - a) * dy[j - 1] + a * dy[j];
                dz[j] = (1 - a) * dz[j - 1] + a * dz[j];
            }
        return (dx[degree], dy[degree], dz[degree]);
    }

    private static int FindSpan(int n, int degree, double u, IReadOnlyList<double> knots)
    {
        if (u >= knots[n + 1]) return n;
        if (u <= knots[degree]) return degree;
        int lo = degree, hi = n + 1, mid = (lo + hi) / 2;
        while (u < knots[mid] || u >= knots[mid + 1])
        {
            if (u < knots[mid]) hi = mid; else lo = mid;
            mid = (lo + hi) / 2;
        }
        return mid;
    }
}
