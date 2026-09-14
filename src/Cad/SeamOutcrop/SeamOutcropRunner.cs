using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.SeamOutcrop;

/// <summary>
/// 逐层跑 <see cref="SeamOutcropEngine"/> 的驱动：现状面 + N 层顶/底板 → 逐顶点色 + 逐层统计。
///
/// <b>层序即优先级</b>：先来的层先认领顶点（引擎里 <c>assigned[v]</c> 一旦为真后面的层就跳过）。
/// 煤层在空间上本不该重叠，真重叠了说明顶/底板选错或建面有问题 ——
/// 这里**不去合并、不去平均**，按层序定，并把每层实际认领数如实报出来，让人自己看出哪层被吃掉了。
///
/// 原版把这段循环写在对话框的确定回调里；切出来是为了能脱 GUI 验收
/// （与 §三三九/§三四〇 同一条：判据与流程只此一份）。
/// </summary>
public static class SeamOutcropRunner
{
    /// <summary>一层煤的输入：顶/底板三角网 + 颜色。</summary>
    public sealed class Layer
    {
        public string Name = "";
        public uint PackedRgb;
        public double[] RoofVerts = Array.Empty<double>();
        public int[] RoofTris = Array.Empty<int>();
        public double[] FloorVerts = Array.Empty<double>();
        public int[] FloorTris = Array.Empty<int>();
    }

    /// <summary>
    /// 交线重剖分：沿「现状面 ∩ 顶/底板」的交线切开现状网再<b>逐面</b>着色。
    /// 切完每个三角要么整片在带内、要么整片在带外，边界就是交线本身 ——
    /// <see cref="Run"/> 那条按节点着色的路会漏掉的窄露头带，这里补得回来。
    /// 顺带给出各层露头的三维面积与水平投影面积（判据与着色同一处产生，
    /// 杜绝"图上一个数、报表另一个数"）。
    /// </summary>
    internal static SeamOutcropRefiner.Result Refine(double[]? terrainVerts, int[]? terrainTris,
                                                   IReadOnlyList<Layer>? layers,
                                                   IReadOnlyList<double[]>? regionRings = null,
                                                   double snapEps = 0.05)
    {
        if (terrainVerts == null || terrainTris == null || layers == null || layers.Count == 0)
            return new SeamOutcropRefiner.Result();

        var fields = new List<SeamOutcropRefiner.SeamField>(layers.Count);
        foreach (var L in layers)
        {
            var roof = new MeshZSampler(L.RoofVerts, L.RoofTris);
            var floor = new MeshZSampler(L.FloorVerts, L.FloorTris);
            // 与 Run 同一条纪律：顶或底板缺一张就整层跳过。这里不能直接不加 ——
            // 入参 seams 的**序号**就是结果里 SeamTris/SeamArea 的序号，少一项会让整排数错位。
            // 故照加，靠空采样器采不到 Z 使该层判不出任何露头。
            fields.Add(new SeamOutcropRefiner.SeamField(roof, floor, L.PackedRgb));
        }

        return SeamOutcropRefiner.Build(terrainVerts, terrainTris, fields, snapEps,
                                        RegionMask.Build(regionRings));
    }

    /// <summary>
    /// 跑一遍（按节点着色）。<paramref name="regionRings"/> 为可采范围环
    /// （扁平 [x0,y0,x1,y1,…]，隐式闭合），null/空 = 不裁。
    /// </summary>
    public static SeamOutcropReport Run(double[]? terrainVerts, int[]? terrainTris,
                                        IReadOnlyList<Layer>? layers,
                                        IReadOnlyList<double[]>? regionRings = null,
                                        double snapEps = 0.05)
    {
        var rep = new SeamOutcropReport();
        int nv = (terrainVerts?.Length ?? 0) / 3;
        if (terrainVerts == null || nv < 1) { rep.Why = "现状面没有顶点"; return rep; }
        if (layers == null || layers.Count == 0) { rep.Why = "没有配置煤层"; return rep; }

        rep.Color = new uint[nv];
        rep.Assigned = new bool[nv];
        var region = RegionMask.Build(regionRings);

        foreach (var L in layers)
        {
            var stat = new SeamOutcropLayerStat { Name = L.Name, PackedRgb = L.PackedRgb };
            var roof = new MeshZSampler(L.RoofVerts, L.RoofTris);
            var floor = new MeshZSampler(L.FloorVerts, L.FloorTris);

            // 顶或底板缺一张就整层跳过：只有一张面判不出"夹在中间"，
            // 拿另一张凑合会把半个矿都染成这层煤。
            if (roof.IsEmpty || floor.IsEmpty)
            {
                stat.Marked = 0;
                rep.Layers.Add(stat);
                continue;
            }

            stat.Marked = SeamOutcropEngine.MarkOutcropVertices(
                terrainVerts, roof, floor, snapEps, region, L.PackedRgb,
                rep.Assigned, rep.Color, out int noData);
            stat.NoData = noData;

            if (terrainTris != null && terrainTris.Length >= 3)
                stat.UncoveredTriangles = SeamOutcropEngine.CountUncoveredTriangles(
                    terrainVerts, terrainTris, rep.Assigned, roof, floor, snapEps, region);

            rep.Layers.Add(stat);
        }

        if (rep.TotalMarked == 0)
            rep.Why = "没有顶点落在任何一层的顶底板之间 —— 检查顶/底板选得对不对、"
                    + "现状面与它们是不是同一个坐标系，以及容差是不是太小";
        return rep;
    }
}
