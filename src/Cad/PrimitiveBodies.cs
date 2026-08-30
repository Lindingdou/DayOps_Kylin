using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 基本几何体程序化生成（忠实移植原 <c>MeshEditLib.Tools.PrimitiveBodies</c> 的 Box/Sphere/Cylinder 生成器）——
/// 立方体 / UV 球 / 带封盖圆柱, 均拓扑闭合、共享索引(非 soup)、外法线朝外, 可直接喂布尔/体积。
/// 纯几何、可单测。返回 (verts, tris) 元组表(接入 OFF 生态)。原入库 ImportToEngine 走内核 PMBI, 不在此。
/// </summary>
public static class PrimitiveBodies
{
    /// <summary>立方体：8 顶点 / 12 三角, 外法线朝外, 拓扑闭合。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) Box(
        double cx, double cy, double cz, double sx, double sy, double sz)
    {
        double hx = sx * 0.5, hy = sy * 0.5, hz = sz * 0.5;
        var v = new List<(double, double, double)>
        {
            (cx - hx, cy - hy, cz - hz), (cx + hx, cy - hy, cz - hz),
            (cx + hx, cy + hy, cz - hz), (cx - hx, cy + hy, cz - hz),
            (cx - hx, cy - hy, cz + hz), (cx + hx, cy - hy, cz + hz),
            (cx + hx, cy + hy, cz + hz), (cx - hx, cy + hy, cz + hz),
        };
        var t = new List<(int, int, int)>
        {
            (0, 3, 2), (0, 2, 1),   // -Z
            (4, 5, 6), (4, 6, 7),   // +Z
            (0, 1, 5), (0, 5, 4),   // -Y
            (1, 2, 6), (1, 6, 5),   // +X
            (3, 7, 6), (3, 6, 2),   // +Y
            (0, 4, 7), (0, 7, 3),   // -X
        };
        return (v, t);
    }

    /// <summary>UV 球：stacks 维(含两极) × slices 经向, 拓扑闭合。stacks≥3, slices≥3。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) Sphere(
        double cx, double cy, double cz, double r, int stacks, int slices)
    {
        if (stacks < 3) stacks = 3;
        if (slices < 3) slices = 3;
        var verts = new List<(double, double, double)>();
        var tris = new List<(int, int, int)>();

        int topIdx = 0;
        verts.Add((cx, cy, cz + r));                       // 北极
        for (int lat = 1; lat < stacks; lat++)
        {
            double theta = lat * Math.PI / stacks;
            double sinT = Math.Sin(theta), cosT = Math.Cos(theta);
            for (int lon = 0; lon < slices; lon++)
            {
                double phi = lon * 2.0 * Math.PI / slices;
                verts.Add((cx + r * sinT * Math.Cos(phi), cy + r * sinT * Math.Sin(phi), cz + r * cosT));
            }
        }
        int bottomIdx = verts.Count;
        verts.Add((cx, cy, cz - r));                       // 南极

        for (int lon = 0; lon < slices; lon++)             // 顶扇
        {
            int next = (lon + 1) % slices;
            tris.Add((topIdx, 1 + lon, 1 + next));
        }
        for (int lat = 0; lat < stacks - 2; lat++)         // 中段
        {
            int ringA = 1 + lat * slices;
            int ringB = 1 + (lat + 1) * slices;
            for (int lon = 0; lon < slices; lon++)
            {
                int nextLon = (lon + 1) % slices;
                int a = ringA + lon, b = ringA + nextLon;
                int c = ringB + lon, d = ringB + nextLon;
                tris.Add((a, c, d)); tris.Add((a, d, b));
            }
        }
        int lastRing = 1 + (stacks - 2) * slices;          // 底扇
        for (int lon = 0; lon < slices; lon++)
        {
            int next = (lon + 1) % slices;
            tris.Add((bottomIdx, lastRing + next, lastRing + lon));
        }
        return (verts, tris);
    }

    /// <summary>圆柱：底/顶圆心 + 两圈 segments 顶点 + 上下封盖, 拓扑闭合。segments≥3。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) Cylinder(
        double cx, double cy, double cz, double r, double h, int segments)
    {
        if (segments < 3) segments = 3;
        double half = h * 0.5;
        var verts = new List<(double, double, double)>();
        var tris = new List<(int, int, int)>();

        verts.Add((cx, cy, cz - half));                    // 0 底心
        verts.Add((cx, cy, cz + half));                    // 1 顶心
        int bRing = 2;
        for (int i = 0; i < segments; i++)
        {
            double phi = i * 2.0 * Math.PI / segments;
            verts.Add((cx + r * Math.Cos(phi), cy + r * Math.Sin(phi), cz - half));
        }
        int tRing = 2 + segments;
        for (int i = 0; i < segments; i++)
        {
            double phi = i * 2.0 * Math.PI / segments;
            verts.Add((cx + r * Math.Cos(phi), cy + r * Math.Sin(phi), cz + half));
        }
        for (int i = 0; i < segments; i++)                 // 底封盖
        {
            int next = (i + 1) % segments;
            tris.Add((0, bRing + next, bRing + i));
        }
        for (int i = 0; i < segments; i++)                 // 顶封盖
        {
            int next = (i + 1) % segments;
            tris.Add((1, tRing + i, tRing + next));
        }
        for (int i = 0; i < segments; i++)                 // 侧壁
        {
            int next = (i + 1) % segments;
            int b = bRing + i, bn = bRing + next, t = tRing + i, tn = tRing + next;
            tris.Add((b, bn, tn)); tris.Add((b, tn, t));
        }
        return (verts, tris);
    }
}
