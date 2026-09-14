using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 编辑命令(移动/复制/旋转/缩放/镜像)拖拽期的幽灵预览备料 ——
/// 忠实原版 Kernel/xllAcEd 的 PreviewProxy.h + EditCommandState::EnsurePreviewClones。
///
/// 幽灵预览每次光标移动都要重画一遍。照直画真几何的话，一张百万三角的 TIN 一帧就是几百万顶点，
/// 拖起来是幻灯片 —— 这正是原版注释里"大模型一拖就卡"的出处。于是拖拽期间分两级：
///   轻实体 → 定基点时镶嵌一次，拖拽期只按变换搬点；
///   重实体 → 定基点时烘焙一份【抽稀替身】线段(网格还是网格、多段线还是那条线，只是稀了)，
///            拖拽期每帧只重画这几千条线，与实体有多重【无关】。
/// 松手落地(ApplyEditTransform)之后由场景常规渲染路径给出准确图形。
///
/// 原版是"轻实体每帧重跑 worldDraw"；托管侧线段通道没有模型矩阵，每帧重镶嵌等于每帧重建
/// 三角网边表，所以这里改成备料一次、逐帧搬点 —— 画出来的东西一样，每帧只剩一遍纯加乘。
/// </summary>
public sealed class DragPreview
{
    // ── 降级阈值(单位: 一次镶嵌吐出的顶点数, 见 SceneEntity.PreviewCost)。忠实原版取值 ──
    // 6 万 ≈ 一万个三角的网, 或三万点的多段线 —— 这个量级每帧重画一遍还跟得上手。
    public const int HeavyEntityCost = 60000;
    public const int TotalBudget = 200000;
    // 替身的段数预算: 6000 段 = 12000 顶点/帧, 与"轻实体"那条闸同量级。
    public const int ProxyTotalSegments = 6000;
    public const int ProxyMinSegments = 60;

    /// <summary>
    /// 降级判据。true = 画真几何(并就地扣预算); false = 降级成抽稀替身。两道闸:
    /// ① 单个实体太重直接降级(一个百万三角的 TIN 就是这条);
    /// ② 累计超预算的部分也降级 —— 否则"选二百条上万点的等高线"每条都不超①, 加起来照样把帧率拖死。
    /// </summary>
    public static bool UsesRealGeometry(int cost, ref int budget)
    {
        if (cost > HeavyEntityCost) return false;
        if (cost > budget) return false;
        budget -= cost;
        return true;
    }

    /// <summary>heavyCount 个重实体平摊段数预算，返回【每个】实体的段数上限(平摊而不是各给各的)。</summary>
    public static int ProxySegmentBudget(int heavyCount)
    {
        if (heavyCount <= 0) return 0;
        int share = ProxyTotalSegments / heavyCount;
        return share > ProxyMinSegments ? share : ProxyMinSegments;
    }

    // 轻实体镶嵌好的模板(交错 P3_C3, 局部坐标 = 世界减烘焙时的渲染原点)
    private float[] _light = Array.Empty<float>();
    private double _bakeOx, _bakeOy;
    // 重实体的抽稀替身: 线段端点, 每 2 个一段, 世界坐标
    private readonly List<(double x, double y, double z)> _proxy = new();

    /// <summary>是否有实体被抽稀 —— 供提示文案(不明说的话, 用户看到稀疏网格会以为几何被改坏了)。</summary>
    public bool Decimated => _proxy.Count > 0;
    /// <summary>替身段数(自检/单测用)。</summary>
    public int ProxySegments => _proxy.Count / 2;
    /// <summary>轻实体模板顶点数(自检/单测用)。</summary>
    public int LightVertices => _light.Length / 6;

    /// <summary>
    /// 定基点时一次性备料。分两趟: 段数预算要在【重实体总数已知】之后才能平摊 ——
    /// 选十张地形网格时每张都给满预算就等于总量涨十倍, 降级也救不回来。
    /// </summary>
    public static DragPreview Build(IEnumerable<SceneEntity> selection)
    {
        var dp = new DragPreview { _bakeOx = RenderOrigin.X, _bakeOy = RenderOrigin.Y };
        var heavy = new List<SceneEntity>();
        var tmp = new List<float>();
        int budget = TotalBudget;
        foreach (var e in selection)
        {
            if (e == null) continue;
            if (UsesRealGeometry(e.PreviewCost(), ref budget)) e.TessellatePreview(tmp);
            else heavy.Add(e);
        }
        dp._light = tmp.ToArray();
        int per = ProxySegmentBudget(heavy.Count);
        foreach (var e in heavy) e.BuildPreviewProxy(dp._proxy, per);
        return dp;
    }

    /// <summary>把幽灵按变换 m 追加到预览几何 o(统一改成预览色)。</summary>
    public void Append(List<float> o, Affine2 m, float cr, float cg, float cb)
    {
        double ox = RenderOrigin.X, oy = RenderOrigin.Y;
        for (int i = 0; i + 5 < _light.Length; i += 6)
        {
            // 模板存的是烘焙时的局部坐标: 先加回烘焙原点还原世界坐标, 变换后再减当前原点
            // (多标签各有各的原点, 拖拽中切标签也不会整体跑飞)
            var p = m.Map(_light[i] + _bakeOx, _light[i + 1] + _bakeOy);
            o.Add((float)(p.x - ox)); o.Add((float)(p.y - oy)); o.Add(_light[i + 2]);
            o.Add(cr); o.Add(cg); o.Add(cb);
        }
        for (int i = 0; i + 1 < _proxy.Count; i += 2)
        {
            var a = m.Map(_proxy[i].x, _proxy[i].y);
            var b = m.Map(_proxy[i + 1].x, _proxy[i + 1].y);
            o.Add((float)(a.x - ox)); o.Add((float)(a.y - oy)); o.Add((float)_proxy[i].z);
            o.Add(cr); o.Add(cg); o.Add(cb);
            o.Add((float)(b.x - ox)); o.Add((float)(b.y - oy)); o.Add((float)_proxy[i + 1].z);
            o.Add(cr); o.Add(cg); o.Add(cb);
        }
    }
}
