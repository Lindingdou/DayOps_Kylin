using System;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 渲染局部原点（XY，世界单位）—— 实体镶嵌成 GPU 顶点时先减掉它，再转 float。
///
/// 为什么非有不可：顶点缓冲与整条矩阵链都是 float32，而矿区坐标动辄 X≈62 万 / Y≈438 万。
/// float32 在 4,194,304 以上的最小间隔正好是 <b>0.5 m</b>——块体模型的层厚也是 0.5 m，于是
/// 煤层带断断续续、顶面发花（原版记作「大坐标 renderOrigin 未 rebase 的 float32 精度丢失 →
/// 条纹/穿模」，它是在 native 侧 rebase 修的）。视口本来就有局部原点，但它是在
/// <c>(float)</c> 之后才减——量化早已发生，减了也补不回来。这里把减法提到转 float <b>之前</b>：
/// 坐标先落到 ±3 km 量级，float32 的间隔降到 ~0.0002 m，绰绰有余。
///
/// 约定：
/// · 值由视口拥有（<c>CadGlViewport.SetRenderOrigin</c> / <c>SyncRenderOrigin</c> 一并写这里），
///   每篇文档定一次就不再变，所以已上传的缓冲不会和它错位；换文档/换标签时随该视口的原点重设。
/// · 只有<b>送 GPU</b> 的镶嵌走这套。凡是拿镶嵌结果做<b>世界坐标</b>运算的（拾取距离、空间索引、
///   框选、裁剪求交、导入包围盒），一律用 <see cref="Suspend"/> 临时归零 —— 见各调用点。
/// · Z 不减：高程只有千米量级，float32 间隔 ~0.0001 m，本来就够。
///
/// <b>逐线程</b>（<see cref="ThreadStaticAttribute"/>）：送 GPU 的镶嵌一概在 UI 线程上做
/// （RefreshScene / 预览 / 高亮），后台线程上跑的都是"要世界坐标"的活（DXF 导入算包围盒、
/// 空间索引），默认 0 正好就是它们要的世界系。顺带也免得并发单测互相污染这份静态态。
/// </summary>
public static class RenderOrigin
{
    [ThreadStatic] private static double _x;
    [ThreadStatic] private static double _y;

    public static double X => _x;
    public static double Y => _y;

    /// <summary>设为给定原点（视口专用；其余地方只读）。</summary>
    public static void Set(double x, double y) { _x = x; _y = y; }

    /// <summary>
    /// 临时归零：作用域内镶嵌出的是<b>世界坐标</b>。给"拿镶嵌结果算几何"的调用方用
    /// （<c>using var _ = RenderOrigin.Suspend();</c>）。
    /// </summary>
    public static Scope Suspend() => new Scope(_x, _y);

    public readonly struct Scope : IDisposable
    {
        private readonly double _sx, _sy;
        internal Scope(double x, double y) { _sx = x; _sy = y; _x = 0; _y = 0; }
        public void Dispose() { _x = _sx; _y = _sy; }
    }
}
