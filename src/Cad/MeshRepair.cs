using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网拓扑修复流水线（忠实原 MeshEditLib「修复拓扑关系」的常见修复部分）——按**正确次序**串联既有算子：
/// ① 焊接(合并重合顶点 + 去重三角，得共享拓扑) → ② 朝向一致(<see cref="MeshOrient"/>，需焊后拓扑传播) →
/// ③ 补边界洞(<see cref="MeshHoleFill"/>，需焊后拓扑取边界环)。返回修复网格 + 前后诊断。
/// 次序关键：焊接须最先(否则重合但未共享的顶点使朝向/补洞失效)。纯逻辑、可单测。
/// (非流形边拆分/自交去除属内核级修复，不含；此为常见拓扑修复。)
/// </summary>
public static partial class MeshRepair
{
    public readonly record struct Result(
        List<(double x, double y, double z)> Verts, List<(int a, int b, int c)> Tris,
        int VertsBefore, int VertsAfter, int FilledHoles, int BoundaryBefore, int BoundaryAfter);

    public static Result Repair(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        int vBefore = verts?.Count ?? 0;
        int bBefore = MeshDiagnose.Analyze(verts, tris, selfIntersect: false).BoundaryEdges;   // 只要开放边数
        if (verts == null || tris == null || verts.Count < 3 || tris.Count < 1)
            return new Result(new List<(double, double, double)>(verts ?? new List<(double, double, double)>()),
                new List<(int, int, int)>(tris ?? new List<(int, int, int)>()), vBefore, vBefore, 0, bBefore, bBefore);

        // ① 焊接：按包围盒对角尺度容差合并重合顶点 + 去重三角
        var m = MeshMetrics.Compute(verts, tris);
        double diag = Math.Sqrt((m.MaxX - m.MinX) * (m.MaxX - m.MinX) +
                                (m.MaxY - m.MinY) * (m.MaxY - m.MinY) +
                                (m.MaxZ - m.MinZ) * (m.MaxZ - m.MinZ));
        var w = MeshWeld.Weld(verts, tris, diag > 0 ? diag * 1e-4 : 1e-6, dropDuplicateTris: true);

        // ② 朝向一致(焊后)
        var oriented = MeshOrient.MakeConsistent(w.Verts, w.Tris);

        // ③ 补边界洞(焊后拓扑)：只补 XY 投影面积 ≤ 1e6 的洞(原版 maxHoleArea 默认)——地形外轮廓那种大"洞"不封,
        //    否则几千个横贯整张图的扇面巨三角把后续检测/显示全拖死(见 MeshRepairOptions 类注释)
        var (fv, ft, holes) = MeshHoleFill.Fill(w.Verts, oriented, 1e6);

        int bAfter = MeshDiagnose.Analyze(fv, ft, selfIntersect: false).BoundaryEdges;
        return new Result(fv, ft, vBefore, fv.Count, holes, bBefore, bAfter);
    }
}
