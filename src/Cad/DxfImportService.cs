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
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var (cr, cg, cb) = LineColor;

        void Seg(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            verts.Add((float)x0); verts.Add((float)y0); verts.Add((float)z0); verts.Add(cr); verts.Add(cg); verts.Add(cb);
            verts.Add((float)x1); verts.Add((float)y1); verts.Add((float)z1); verts.Add(cr); verts.Add(cg); verts.Add(cb);
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

        int entCount = 0;
        try
        {
            var model = doc.BlockRecords["*Model_Space"];
            foreach (var e in model.Entities)
            {
                entCount++;
                switch (e)
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

                    // Polyline2D 同时接住 Polyline3D（ACadSharp 中后者派生自前者）
                    case Polyline2D p2:
                    {
                        var vs = p2.Vertices;
                        for (int i = 0; i + 1 < vs.Count; i++)
                            Seg(vs[i].Location.X, vs[i].Location.Y, vs[i].Location.Z, vs[i + 1].Location.X, vs[i + 1].Location.Y, vs[i + 1].Location.Z);
                        if (p2.IsClosed && vs.Count > 1)
                            Seg(vs[vs.Count - 1].Location.X, vs[vs.Count - 1].Location.Y, vs[vs.Count - 1].Location.Z, vs[0].Location.X, vs[0].Location.Y, vs[0].Location.Z);
                        break;
                    }

                    // Arc 必须在 Circle 之前（ACadSharp 中 Arc 派生自 Circle）
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

                    default:
                        result.Warnings.Add($"跳过未支持实体：{e.GetType().Name}");
                        break;
                }
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
        result.Bounds = verts.Count == 0 ? new double[] { 0, 0, 0, 0 } : new[] { minX, minY, maxX, maxY };
        return result;
    }
}
