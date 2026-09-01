using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 网格/曲面空间约束(4 模式) —— 忠实移植原 BlockModelLib.MeshContainmentTester 的 MeshConstraintMode。
/// 判点 (x,y,z) 相对一张网格是否保留:
///   KeepAboveSurface / KeepBelowSurface —— 相对**开放曲面(TIN)**: 取该面在 (x,y) 处的高程, 点在其上/下则保留;
///   KeepInsideClosed / KeepOutsideClosed —— 相对**闭合网格**: 广义缠绕数(GWN)判内/外。
///
/// 复用已移基元: 面高程 = <see cref="MineableAreaIdentifier.SampleMeshZ"/>(点落三角内重心插值 Z; 面外 null);
/// 闭合内外 = <see cref="WindingNumberTester.IsInsideClosed"/>。区别于 Kylin 既有「约束块体」(仅 2D 闭合多段线内),
/// 本类做**3D 曲面/网格约束**(如: 保留地表以下且煤层底板以上 = 可采带)。纯几何、可单测。
/// </summary>
public enum MeshConstraintMode { KeepAboveSurface, KeepBelowSurface, KeepInsideClosed, KeepOutsideClosed }

public static class MeshContainment
{
    /// <summary>点是否满足约束。closed 仅内/外模式需要(可预建复用)。面外(取不到面高程)对上/下模式一律不保留。</summary>
    public static bool Keep(double[] verts, int[] tris, WindingNumberTester? closed, double x, double y, double z, MeshConstraintMode mode)
    {
        switch (mode)
        {
            case MeshConstraintMode.KeepAboveSurface:
            {
                var s = MineableAreaIdentifier.SampleMeshZ(verts, tris, x, y);
                return s.HasValue && z >= s.Value;
            }
            case MeshConstraintMode.KeepBelowSurface:
            {
                var s = MineableAreaIdentifier.SampleMeshZ(verts, tris, x, y);
                return s.HasValue && z <= s.Value;
            }
            case MeshConstraintMode.KeepInsideClosed:
                return closed != null && closed.IsInsideClosed(x, y, z);
            case MeshConstraintMode.KeepOutsideClosed:
                return closed == null || !closed.IsInsideClosed(x, y, z);
            default:
                return false;
        }
    }

    /// <summary>过滤点表(扁平顶点 verts=[x,y,z,…], tris=[a,b,c,…]的网格), 返回保留点的下标。</summary>
    public static List<int> KeepIndices(double[] verts, int[] tris, IReadOnlyList<(double x, double y, double z)> pts, MeshConstraintMode mode)
    {
        var keep = new List<int>();
        if (pts == null || verts == null || tris == null) return keep;
        WindingNumberTester? closed = (mode is MeshConstraintMode.KeepInsideClosed or MeshConstraintMode.KeepOutsideClosed)
            ? new WindingNumberTester(verts, tris) : null;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (Keep(verts, tris, closed, p.x, p.y, p.z, mode)) keep.Add(i);
        }
        return keep;
    }

    /// <summary>把三角网 (顶点列 + 索引三元组) 摊平成 WindingNumberTester/SampleMeshZ 所需的扁平数组。</summary>
    public static (double[] verts, int[] tris) Flatten(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var fv = new double[v.Count * 3];
        for (int i = 0; i < v.Count; i++) { fv[i * 3] = v[i].x; fv[i * 3 + 1] = v[i].y; fv[i * 3 + 2] = v[i].z; }
        var ft = new int[t.Count * 3];
        for (int i = 0; i < t.Count; i++) { ft[i * 3] = t[i].a; ft[i * 3 + 1] = t[i].b; ft[i * 3 + 2] = t[i].c; }
        return (fv, ft);
    }
}
