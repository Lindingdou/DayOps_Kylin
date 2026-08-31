using System;
using System.Collections.Generic;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using PitMine3D.Kylin.Cad.Draw;
using DrawText = PitMine3D.Kylin.Cad.Draw.TextEntity;   // 与 ACadSharp.Entities.TextEntity 消歧
using AcHatch = ACadSharp.Entities.Hatch;               // 与本项目静态类 Cad.Hatch 消歧

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
            doc = ext switch
            {
                ".dxf" => DxfReader.Read(filePath),
                ".dwg" => DwgReader.Read(filePath),
                _ => throw new NotSupportedException($"不支持的 CAD 格式：{ext}（支持 .dxf/.dwg）")
            };
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
                    int n = vs.Count, last = lp.IsClosed ? n : n - 1;
                    for (int i = 0; i < last && n > 1; i++)
                    {
                        var a = vs[i]; var b = vs[(i + 1) % n];
                        if (Math.Abs(a.Bulge) > 1e-9)                    // 弧段 → 细分
                        {
                            double px = a.Location.X, py = a.Location.Y;
                            foreach (var ap in BulgeArc.Interior(a.Location.X, a.Location.Y, b.Location.X, b.Location.Y, a.Bulge))
                            { Seg(px, py, lp.Elevation, ap.x, ap.y, lp.Elevation); px = ap.x; py = ap.y; }
                            Seg(px, py, lp.Elevation, b.Location.X, b.Location.Y, lp.Elevation);
                        }
                        else Seg(a.Location.X, a.Location.Y, lp.Elevation, b.Location.X, b.Location.Y, lp.Elevation);
                    }
                    break;
                }

                case Polyline2D p2:
                {
                    var vs = p2.Vertices;
                    int n = vs.Count, last = p2.IsClosed ? n : n - 1;
                    for (int i = 0; i < last && n > 1; i++)
                    {
                        var a = vs[i]; var b = vs[(i + 1) % n];
                        if (Math.Abs(a.Bulge) > 1e-9)
                        {
                            double px = a.Location.X, py = a.Location.Y;
                            foreach (var ap in BulgeArc.Interior(a.Location.X, a.Location.Y, b.Location.X, b.Location.Y, a.Bulge))
                            { Seg(px, py, a.Location.Z, ap.x, ap.y, a.Location.Z); px = ap.x; py = ap.y; }
                            Seg(px, py, a.Location.Z, b.Location.X, b.Location.Y, b.Location.Z);
                        }
                        else Seg(a.Location.X, a.Location.Y, a.Location.Z, b.Location.X, b.Location.Y, b.Location.Z);
                    }
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

                case Leader ld:   // 引线：顶点折线
                {
                    var vs = ld.Vertices; var lst = new List<CSMath.XYZ>(vs);
                    for (int i = 0; i + 1 < lst.Count; i++) Seg(lst[i].X, lst[i].Y, lst[i].Z, lst[i + 1].X, lst[i + 1].Y, lst[i + 1].Z);
                    break;
                }
                case MLine ml:   // 多线：中心线顶点折线
                {
                    var vs = ml.Vertices; int mc = vs.Count;
                    for (int i = 0; i + 1 < mc; i++) Seg(vs[i].Position.X, vs[i].Position.Y, vs[i].Position.Z, vs[i + 1].Position.X, vs[i + 1].Position.Y, vs[i + 1].Position.Z);
                    break;
                }
                case MultiLeader mld when mld.ContextData?.LeaderRoots != null:   // 多重引线：各引线线段
                {
                    foreach (var root in mld.ContextData.LeaderRoots)
                        foreach (var line in root.Lines)
                        {
                            var ps = line.Points;
                            for (int i = 0; i + 1 < ps.Count; i++) Seg(ps[i].X, ps[i].Y, ps[i].Z, ps[i + 1].X, ps[i + 1].Y, ps[i + 1].Z);
                        }
                    break;
                }

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

    /// <summary>可编辑导入结果：DXF/DWG → 绘制场景实体（可选中/编辑/删除/按层管理）。</summary>
    public sealed class EntityImportResult
    {
        public bool Success => Error == null;
        public string? Error { get; set; }
        public List<SceneEntity> Entities { get; } = new();
        public double[] Bounds { get; set; } = { 0, 0, 0, 0 };
        /// <summary>图层名 → 代表色（供图层面板色块）。</summary>
        public Dictionary<string, (float r, float g, float b)> LayerColors { get; } = new();
        /// <summary>图层名 → 状态(开/冻结/锁定)，来自 DXF 图层表；供导入后恢复图层开关（round-trip 保真）。</summary>
        public Dictionary<string, (bool on, bool frozen, bool locked)> LayerStates { get; } = new();
        public List<string> LayerOrder { get; } = new();
        public Dictionary<string, int> TypeCounts { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>读 DXF/DWG 为可编辑场景实体（Line/Circle/Arc/Polyline/Point/Ellipse/Spline/Insert 展开）。</summary>
    public static EntityImportResult LoadEntities(string filePath)
    {
        var result = new EntityImportResult();
        CadDocument doc;
        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            doc = ext switch
            {
                ".dxf" => DxfReader.Read(filePath),
                ".dwg" => DwgReader.Read(filePath),
                _ => throw new NotSupportedException($"不支持的 CAD 格式：{ext}")
            };
        }
        catch (Exception ex) { result.Error = $"读取失败：{ex.Message}"; return result; }
        return MapDocument(doc, result);
    }

    /// <summary>把 CadDocument 映射为场景实体（可单测：测试直接传入内存 doc）。</summary>
    public static EntityImportResult MapDocument(CadDocument doc, EntityImportResult? into = null)
    {
        var result = into ?? new EntityImportResult();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double[]? emitDash = null;   // 当前实体线型(Emit 顶置, Finalize 读)
        short emitLW = -1;           // 当前实体线宽(同上; 存实体自身值, round-trip 保真, 不解析 ByLayer)

        void Finalize(SceneEntity se, Affine2? xf, (float r, float g, float b) col, string layer)
        {
            se.Cr = col.r; se.Cg = col.g; se.Cb = col.b; se.Dash = emitDash; se.LineWeight = emitLW;
            if (xf != null) se = se.Apply(xf.Value);   // 变换保留颜色(Colored)，但不拷层名
            se.LayerName = layer;
            result.Entities.Add(se);
            var o = new List<float>(); se.Tessellate(o);
            for (int i = 0; i + 1 < o.Count; i += 6)
            {
                float x = o[i], y = o[i + 1];
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
        }

        PolylineEntity? EllipsePoly(Ellipse ell)
        {
            double majX = ell.MajorAxisEndPoint.X, majY = ell.MajorAxisEndPoint.Y;
            double majLen = Math.Sqrt(majX * majX + majY * majY);
            if (majLen < 1e-9) return null;
            double rot = Math.Atan2(majY, majX), minLen = majLen * ell.RadiusRatio;
            double t0 = ell.StartParameter, t1 = ell.EndParameter;
            if (t1 <= t0) t1 += Math.PI * 2;
            bool full = Math.Abs((t1 - t0) - Math.PI * 2) < 1e-6;
            int n = Math.Max(16, (int)(CircleSegments * Math.Abs(t1 - t0) / (Math.PI * 2)));
            double cosR = Math.Cos(rot), sinR = Math.Sin(rot);
            var pl = new PolylineEntity { Closed = full };
            for (int i = 0; i <= n; i++)
            {
                double t = t0 + (t1 - t0) * i / n;
                double lx = majLen * Math.Cos(t), ly = minLen * Math.Sin(t);
                pl.Points.Add((ell.Center.X + lx * cosR - ly * sinR, ell.Center.Y + lx * sinR + ly * cosR));
            }
            return pl.Points.Count >= 2 ? pl : null;
        }

        PolylineEntity? SplinePoly(Spline sp)
        {
            var cps = sp.ControlPoints; int deg = sp.Degree; var knots = sp.Knots;
            var pl = new PolylineEntity();
            if (cps == null || cps.Count < 2)
            {
                var fps = sp.FitPoints;
                if (fps != null) foreach (var p in fps) pl.Points.Add((p.X, p.Y));
                return pl.Points.Count >= 2 ? pl : null;
            }
            if (knots == null || deg < 1 || knots.Count < cps.Count + deg + 1)
            {
                foreach (var p in cps) pl.Points.Add((p.X, p.Y));
                return pl.Points.Count >= 2 ? pl : null;
            }
            var sx = new double[cps.Count]; var sy = new double[cps.Count]; var sz = new double[cps.Count];
            for (int i = 0; i < cps.Count; i++) { sx[i] = cps[i].X; sy[i] = cps[i].Y; sz[i] = cps[i].Z; }
            int nn = cps.Count - 1; double u0 = knots[deg], u1 = knots[nn + 1];
            int samples = Math.Max(CircleSegments, cps.Count * 8);
            for (int s = 0; s <= samples; s++)
            {
                double u = s == samples ? u1 : u0 + (u1 - u0) * s / samples;
                var pt = EvalBSpline(sx, sy, sz, knots, deg, u);
                pl.Points.Add((pt.x, pt.y));
            }
            return pl.Points.Count >= 2 ? pl : null;
        }

        void Emit(Entity ent, Affine2? xf, (float r, float g, float b) col, string layer, int depth)
        {
            emitDash = ResolveDash(ent.LineType?.Name, ent.Layer?.LineType?.Name);   // 线型(ByLayer 解析)
            emitLW = (short)ent.LineWeight;                                          // 线宽(存实体自身值, round-trip 保真)
            switch (ent)
            {
                case Line ln:
                    Finalize(new LineEntity { X0 = ln.StartPoint.X, Y0 = ln.StartPoint.Y, X1 = ln.EndPoint.X, Y1 = ln.EndPoint.Y }, xf, col, layer);
                    break;
                case LwPolyline lp:
                {
                    var pl = new PolylineEntity { Closed = lp.IsClosed };
                    var lvs = lp.Vertices;
                    for (int i = 0; i < lvs.Count; i++)
                    {
                        var v = lvs[i];
                        pl.Points.Add((v.Location.X, v.Location.Y));
                        int ni = i + 1;
                        if (ni >= lvs.Count) { if (!lp.IsClosed) break; ni = 0; }
                        if (Math.Abs(v.Bulge) > 1e-9)                     // 该段为弧 → 插入弧点
                            foreach (var ap in BulgeArc.Interior(v.Location.X, v.Location.Y, lvs[ni].Location.X, lvs[ni].Location.Y, v.Bulge))
                                pl.Points.Add(ap);
                    }
                    if (pl.Points.Count >= 2) Finalize(pl, xf, col, layer);
                    break;
                }
                case Polyline2D p2:
                {
                    var pl = new PolylineEntity { Closed = p2.IsClosed };
                    var pvs = new List<Vertex2D>(p2.Vertices);
                    for (int i = 0; i < pvs.Count; i++)
                    {
                        var v = pvs[i];
                        pl.Points.Add((v.Location.X, v.Location.Y));
                        int ni = i + 1;
                        if (ni >= pvs.Count) { if (!p2.IsClosed) break; ni = 0; }
                        if (Math.Abs(v.Bulge) > 1e-9)
                            foreach (var ap in BulgeArc.Interior(v.Location.X, v.Location.Y, pvs[ni].Location.X, pvs[ni].Location.Y, v.Bulge))
                                pl.Points.Add(ap);
                    }
                    if (pl.Points.Count >= 2) Finalize(pl, xf, col, layer);
                    break;
                }
                case Polyline3D p3:
                {
                    var pl = new PolylineEntity { Closed = p3.IsClosed };
                    foreach (var v in p3.Vertices) pl.Points.Add((v.Location.X, v.Location.Y));
                    if (pl.Points.Count >= 2) Finalize(pl, xf, col, layer);
                    break;
                }
                case XLine xl:   // 构造线(无限)→ 过点双向长线段近似
                {
                    var s = xl.FirstPoint; var d = xl.Direction;
                    Finalize(new LineEntity { X0 = s.X - d.X * 10000, Y0 = s.Y - d.Y * 10000, X1 = s.X + d.X * 10000, Y1 = s.Y + d.Y * 10000 }, xf, col, layer);
                    break;
                }
                case Ray ry:     // 射线(半无限)→ 起点朝方向长线段近似
                {
                    var s = ry.StartPoint; var d = ry.Direction;
                    Finalize(new LineEntity { X0 = s.X, Y0 = s.Y, X1 = s.X + d.X * 10000, Y1 = s.Y + d.Y * 10000 }, xf, col, layer);
                    break;
                }
                case Arc ar:   // 须在 Circle 之前（Arc : Circle）
                {
                    double a0 = ar.StartAngle, a1 = ar.EndAngle;
                    if (a1 <= a0) a1 += Math.PI * 2;
                    double am = (a0 + a1) / 2;
                    Finalize(new ArcEntity
                    {
                        X1 = ar.Center.X + ar.Radius * Math.Cos(a0), Y1 = ar.Center.Y + ar.Radius * Math.Sin(a0),
                        X2 = ar.Center.X + ar.Radius * Math.Cos(am), Y2 = ar.Center.Y + ar.Radius * Math.Sin(am),
                        X3 = ar.Center.X + ar.Radius * Math.Cos(a1), Y3 = ar.Center.Y + ar.Radius * Math.Sin(a1)
                    }, xf, col, layer);
                    break;
                }
                case Circle ci:
                    Finalize(new CircleEntity { Cx = ci.Center.X, Cy = ci.Center.Y, Radius = ci.Radius }, xf, col, layer);
                    break;
                case Point pt:
                    Finalize(new PointEntity { X = pt.Location.X, Y = pt.Location.Y }, xf, col, layer);
                    break;
                case Ellipse ell:
                {
                    var pl = EllipsePoly(ell);
                    if (pl != null) Finalize(pl, xf, col, layer);
                    break;
                }
                case Spline sp:
                {
                    var pl = SplinePoly(sp);
                    if (pl != null) Finalize(pl, xf, col, layer);
                    break;
                }
                case ACadSharp.Entities.TextEntity te:
                {
                    // 对齐(用枚举名, 稳健)：非左/基线时锚点取 AlignmentPoint
                    int ha = te.HorizontalAlignment.ToString() switch { "Center" or "Middle" or "Aligned" or "Fit" => 1, "Right" => 2, _ => 0 };
                    int va = te.VerticalAlignment.ToString() switch { "Middle" => 1, "Top" => 2, _ => 0 };
                    var ap = te.AlignmentPoint;
                    bool useAlign = (ha != 0 || va != 0) && (ap.X != 0 || ap.Y != 0);
                    double tx = useAlign ? ap.X : te.InsertPoint.X, ty = useAlign ? ap.Y : te.InsertPoint.Y;
                    Finalize(new DrawText { X = tx, Y = ty, Height = te.Height > 0 ? te.Height : 1, Rotation = te.Rotation, HAlign = ha, VAlign = va, WidthFactor = te.WidthFactor > 0 ? te.WidthFactor : 1, ObliqueAngle = te.ObliqueAngle * System.Math.PI / 180.0, Text = te.Value ?? "" }, xf, col, layer);
                    break;
                }
                case MText mt:
                {
                    var lines = MTextLines(mt.Value ?? "");
                    double mh = mt.Height > 0 ? mt.Height : 1;
                    double step = mh * 1.4;                    // 行距
                    double mc = System.Math.Cos(mt.Rotation), ms = System.Math.Sin(mt.Rotation);
                    double dnx = ms, dny = -mc;                // 行向下(垂直文字方向)
                    for (int li = 0; li < lines.Length; li++)
                    {
                        if (lines[li].Length == 0) continue;
                        Finalize(new DrawText
                        {
                            X = mt.InsertPoint.X + li * step * dnx, Y = mt.InsertPoint.Y + li * step * dny,
                            Height = mh, Rotation = mt.Rotation, Text = lines[li]
                        }, xf, col, layer);
                    }
                    break;
                }
                case Solid so:
                {
                    var pl = new PolylineEntity { Closed = true };   // 2D 实心：角点序 1,2,4,3 成四边形轮廓
                    pl.Points.Add((so.FirstCorner.X, so.FirstCorner.Y));
                    pl.Points.Add((so.SecondCorner.X, so.SecondCorner.Y));
                    pl.Points.Add((so.FourthCorner.X, so.FourthCorner.Y));
                    pl.Points.Add((so.ThirdCorner.X, so.ThirdCorner.Y));
                    Finalize(pl, xf, col, layer);
                    break;
                }
                case Face3D f3:
                {
                    var pl = new PolylineEntity { Closed = true };
                    pl.Points.Add((f3.FirstCorner.X, f3.FirstCorner.Y));
                    pl.Points.Add((f3.SecondCorner.X, f3.SecondCorner.Y));
                    pl.Points.Add((f3.ThirdCorner.X, f3.ThirdCorner.Y));
                    pl.Points.Add((f3.FourthCorner.X, f3.FourthCorner.Y));
                    Finalize(pl, xf, col, layer);
                    break;
                }
                case Dimension dim:   // 标注(对齐/线性/半径/角度…)：爆炸其渲染块还原尺寸线/箭头/文字
                {
                    if (depth >= 8 || dim.Block == null) break;
                    foreach (var be in dim.Block.Entities)
                        Emit(be, xf, ColorOf(be), layer, depth + 1);
                    break;
                }
                case AcHatch ha:   // 填充：取边界环为闭合多段线轮廓(不填充=Skia)；边界含 Line/Arc/Polyline 边
                {
                    foreach (var loop in ExtractHatchBoundaries(ha))
                    {
                        var pl = new PolylineEntity { Closed = true };
                        foreach (var p in loop) pl.Points.Add(p);
                        Finalize(pl, xf, col, layer);
                    }
                    break;
                }
                case Insert ins:
                {
                    if (depth >= 8 || ins.Block == null) break;
                    double c = Math.Cos(ins.Rotation), s = Math.Sin(ins.Rotation);
                    double sx = ins.XScale == 0 ? 1 : ins.XScale, sy = ins.YScale == 0 ? 1 : ins.YScale;
                    var insM = new Affine2(sx * c, sx * s, -sy * s, sy * c, ins.InsertPoint.X, ins.InsertPoint.Y);
                    var childXf = xf == null ? insM : Affine2.Multiply(xf.Value, insM);
                    foreach (var be in ins.Block.Entities)
                        Emit(be, childXf, ColorOf(be), layer, depth + 1);   // 块内实体归插入所在层
                    break;
                }
                case Leader ld:   // 引线：顶点折线（箭头/注释块另随文档实体, 此还原引线本体）
                {
                    var pl = new PolylineEntity();
                    foreach (var v in ld.Vertices) pl.Points.Add((v.X, v.Y));
                    if (pl.Points.Count >= 2) Finalize(pl, xf, col, layer);
                    break;
                }
                case MLine ml:   // 多线：以中心线顶点折线还原（平行偏移线族需 MLineStyle, 记录）
                {
                    var pl = new PolylineEntity();
                    foreach (var v in ml.Vertices) pl.Points.Add((v.Position.X, v.Position.Y));
                    if (pl.Points.Count >= 2) Finalize(pl, xf, col, layer);
                    break;
                }
                case MultiLeader mld when mld.ContextData?.LeaderRoots != null:   // 多重引线：取各引线线段折线（文字/块内容随文档另出）
                {
                    foreach (var root in mld.ContextData.LeaderRoots)
                        foreach (var line in root.Lines)
                        {
                            var pl = new PolylineEntity();
                            foreach (var p in line.Points) pl.Points.Add((p.X, p.Y));
                            if (pl.Points.Count >= 2) Finalize(pl, xf, col, layer);
                        }
                    break;
                }
                case Mesh mesh:   // 网格：逐面还原为闭合折线线框(与 Face3D 一致的 2D 投影)
                {
                    var mv = mesh.Vertices;
                    foreach (var face in mesh.Faces)
                    {
                        if (face == null || face.Length < 3) continue;
                        int start = (face.Length >= 4 && face[0] == face.Length - 1) ? 1 : 0;   // 首元素为顶点数 → 跳过
                        var pl = new PolylineEntity { Closed = true };
                        for (int i = start; i < face.Length; i++)
                        {
                            int idx = face[i];
                            if (idx >= 0 && idx < mv.Count) pl.Points.Add((mv[idx].X, mv[idx].Y));
                        }
                        if (pl.Points.Count >= 3) Finalize(pl, xf, col, layer);
                    }
                    break;
                }
                case PolyfaceMesh pfm:   // 多面网格：面记录 1-based(负=隐藏边取绝对值, 0=缺=三角)
                {
                    var pv = pfm.Vertices;
                    foreach (var face in pfm.Faces)
                    {
                        short[] idxs = { face.Index1, face.Index2, face.Index3, face.Index4 };
                        var pl = new PolylineEntity { Closed = true };
                        foreach (short raw in idxs)
                        {
                            if (raw == 0) continue;
                            int idx = Math.Abs(raw) - 1;
                            if (idx >= 0 && idx < pv.Count) pl.Points.Add((pv[idx].Location.X, pv[idx].Location.Y));
                        }
                        if (pl.Points.Count >= 3) Finalize(pl, xf, col, layer);
                    }
                    break;
                }
                default:
                    result.Warnings.Add($"跳过未支持实体：{ent.GetType().Name}");
                    break;
            }
        }

        try
        {
            var model = doc.BlockRecords["*Model_Space"];
            foreach (var e in model.Entities)
            {
                string layer = SafeLayerName(e);
                if (!result.LayerColors.ContainsKey(layer)) { result.LayerColors[layer] = ColorOf(e); result.LayerOrder.Add(layer); }
                var cn = CnTypeName(e);
                if (cn != null) result.TypeCounts[cn] = result.TypeCounts.GetValueOrDefault(cn) + 1;
                Emit(e, null, ColorOf(e), layer, 0);
            }
            foreach (var ly in doc.Layers)   // 图层表状态(开/冻结/锁定) → round-trip 保真
            {
                bool frozen = (ly.Flags & ACadSharp.Tables.LayerFlags.Frozen) != 0;
                bool locked = (ly.Flags & ACadSharp.Tables.LayerFlags.Locked) != 0;
                result.LayerStates[ly.Name] = (ly.IsOn, frozen, locked);
            }
        }
        catch (Exception ex) { result.Error = $"映射实体失败：{ex.Message}"; return result; }

        result.Bounds = result.Entities.Count == 0 ? new double[] { 0, 0, 0, 0 } : new[] { minX, minY, maxX, maxY };
        return result;
    }

    // Hatch 边界环 → 闭合点环列表（移植自原 DwgDxfImportService.ExtractHatchBoundaries：
    // Line/Arc/Polyline 边；剥首尾重合点；丢顶点 <3 的环）。填充本体需 Skia，此处仅取轮廓。
    private static List<List<(double x, double y)>> ExtractHatchBoundaries(AcHatch hatch)
    {
        var boundaries = new List<List<(double x, double y)>>();
        foreach (var path in hatch.Paths)
        {
            var loop = new List<(double x, double y)>();
            foreach (var edge in path.Edges)
            {
                switch (edge)
                {
                    case AcHatch.BoundaryPath.Line le:
                        AddHatchPt(loop, le.Start.X, le.Start.Y);
                        AddHatchPt(loop, le.End.X, le.End.Y);
                        break;
                    case AcHatch.BoundaryPath.Arc ae:
                    {
                        double sweep = ae.EndAngle - ae.StartAngle;
                        if (sweep < 0) sweep += 2 * Math.PI;
                        for (int i = 0; i <= 16; i++)
                        {
                            double a = ae.StartAngle + sweep * i / 16.0;
                            AddHatchPt(loop, ae.Center.X + ae.Radius * Math.Cos(a), ae.Center.Y + ae.Radius * Math.Sin(a));
                        }
                        break;
                    }
                    case AcHatch.BoundaryPath.Polyline pe:
                        foreach (var v in pe.Vertices) AddHatchPt(loop, v.X, v.Y);
                        break;
                }
            }
            if (loop.Count >= 2 && Math.Abs(loop[0].x - loop[^1].x) < 1e-9 && Math.Abs(loop[0].y - loop[^1].y) < 1e-9)
                loop.RemoveAt(loop.Count - 1);
            if (loop.Count >= 3) boundaries.Add(loop);
        }
        return boundaries;
    }

    private static void AddHatchPt(List<(double x, double y)> loop, double x, double y)
    {
        if (loop.Count > 0 && Math.Abs(loop[^1].x - x) < 1e-9 && Math.Abs(loop[^1].y - y) < 1e-9) return;
        loop.Add((x, y));
    }

    // MText 格式控制码剥离（移植自原 DwgDxfImportService.StripMTextFormatting）——
    // 去 \H字高 \W字宽 \F字体 \C颜色 \A对齐 等带参(到 ';')码、成对开关码、{} 分组括号，只留可见文字。
    internal static string StripMTextFormatting(string input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        var sb = new System.Text.StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch == '{' || ch == '}') continue;            // 分组括号：去掉留内容
            if (ch == '\\' && i + 1 < input.Length)
            {
                char code = input[i + 1];
                switch (code)
                {
                    // 带参、以 ';' 结束的控制码（仅当真有 ';' 才剥离，保护 "C:\Files" 这类合法反斜杠）
                    case 'f': case 'F': case 'H': case 'W': case 'C': case 'c':
                    case 'T': case 'Q': case 'A': case 'p': case 'S':
                    {
                        int semi = input.IndexOf(';', i + 2);
                        if (semi < 0) sb.Append('\\');       // 无 ';' → 字面反斜杠
                        else i = semi;                        // 跳到 ';'
                        break;
                    }
                    // 无参开关码（下/上划线、删除线，成对）：去掉 '\'+letter
                    case 'L': case 'l': case 'O': case 'o': case 'K': case 'k':
                        i++;
                        break;
                    case 'P': case 'X': sb.Append(' '); i++; break;   // 段落换行 → 空格
                    case '~': sb.Append(' '); i++; break;             // 不间断空格
                    case '\\': sb.Append('\\'); i++; break;           // 转义反斜杠
                    case '{': sb.Append('{'); i++; break;
                    case '}': sb.Append('}'); i++; break;
                    default: sb.Append('\\'); break;                  // 未知码：留 '\'
                }
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>解析实体线型 → 虚线样式：ByLayer(或空)时取图层线型名, 否则取实体线型名, 映射到 <see cref="Draw.DashPattern"/>。</summary>
    internal static double[]? ResolveDash(string? entLineType, string? layerLineType)
    {
        bool byLayer = string.IsNullOrEmpty(entLineType) || entLineType == "ByLayer" || entLineType == "BYLAYER" || entLineType == "Bylayer";
        string name = byLayer ? (layerLineType ?? "") : entLineType!;
        return PitMine3D.Kylin.Cad.Draw.DashPattern.ByName(name);
    }

    /// <summary>MText 按段落换行 \P 拆成多行, 各行剥格式码。空/无 \P → 单行。供多行文字导入逐行渲染。</summary>
    internal static string[] MTextLines(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return System.Array.Empty<string>();
        var parts = raw.Split(new[] { "\\P", "\\p" }, System.StringSplitOptions.None);
        var res = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++) res[i] = StripMTextFormatting(parts[i]);
        return res;
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
        Leader => "引线",
        MLine => "多线",
        MultiLeader => "多重引线",
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
