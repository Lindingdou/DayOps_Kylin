// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/FaceOriginTierTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 作业面三层来源的判据（T 组）：<c>作业面台账 → 本期工序作业区派生 → 样例</c>。
///
/// <para><b>这一组补的是一个"没有判据能碰到"的缺口</b>：裸台架里数据库不可用 ⇒
/// <c>ProcessZoneFaceSource.Derive()</c> 永远返回 null ⇒ <c>Config()</c> 里那条
/// 「用派生的面顶掉样例」的分支<b>一次都走不到</b>。而那条分支正是"不再用示例数据"
/// 这件事的落点 —— 整条链上最该被判住的一段，反倒是唯一没有判据能碰到的一段。
/// 所以给它开了一个注入口（<c>ProductionPlanContext.FaceDeriveHook</c>）。</para>
///
/// <para><b>每条都必须收好摊子</b>：注入口是进程级静态，留着不清会污染同一进程里
/// 后面所有判据的装配 —— 而那种污染不报错，只是别的判据莫名其妙地变绿或变红。</para>
/// </summary>
[Collection("PlanContext")]
public class FaceOriginTierTests : IDisposable
{
    public FaceOriginTierTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        ProductionPlanContext.FaceDeriveHook = null;
        ProductionPlanContext.Invalidate();
    }

    private static FaceInput Face(string zone, string unitId = "", double target = 500)
        => new()
        {
            Zone = zone,
            UnitId = unitId,
            Process = ProcessType.Load,
            MaterialCode = MaterialCatalog.Rock,
            DayTargetM3 = target,
            AvailableReserveM3 = target * 25,
            SourceX = 1000, SourceY = 2000, SourceZ = 1180,
            SourceOrigin = HaulResolver.OriginDerived,
        };

    // ══════════════════════════════════════════════════════════════
    //  T1 派生的面顶掉样例
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// T1 <b>台账为空、但本期有工序区时，作业面走派生 —— 不再落到样例</b>。
    ///
    /// <para>这一条是整个"不再用示例数据"的落点。它之前没有任何判据碰得到：
    /// 裸台架里派生源读不到库，永远返回 null，于是每次都走样例分支。</para>
    /// </summary>
    [Fact]
    public void T1_有派生面时不再落到样例()
    {
        ProductionPlanContext.FaceDeriveHook = () => new List<FaceInput> { Face("派生面A", "4-B1-P1") };
        var cfg = ProductionPlanContext.Config();

        Assert.Equal(ProductionPlanContext.FaceOriginKind.Derived, ProductionPlanContext.FaceOrigin);
        Assert.True(ProductionPlanContext.FacesAreReal);
        Assert.Contains(cfg.Faces, f => f.Zone == "派生面A");
        // 样例那批面（示例露天矿的「主采面·东」等）一个都不许混进来
        Assert.DoesNotContain(cfg.Faces, f => f.Zone.Contains("主采面", StringComparison.Ordinal));
    }

    /// <summary>
    /// T1b 走派生时，来源文案<b>不带</b>「样例数据·非本矿」的记号。
    /// <para>与 G1 的不变式同一条，但这里是从**真实数据那一侧**验的 ——
    /// G1 在裸台架里只验得到"没有真数据 ⇒ 有记号"这半边。</para>
    /// </summary>
    [Fact]
    public void T1b_走派生时不带样例记号()
    {
        ProductionPlanContext.FaceDeriveHook = () => new List<FaceInput> { Face("派生面A") };
        ProductionPlanContext.Config();

        Assert.DoesNotContain("【", ProductionPlanContext.SourceLabel);
        Assert.DoesNotContain(ProductionPlanContext.AssemblyNotes, n => n.Contains("一个作业面都没有"));
    }

    /// <summary>
    /// T1c 走派生时要<b>说清楚这些面还没人填过去向/运距/设备/煤质</b>。
    /// <para>派生出来的是"位置对了"，不是"档案齐了"。不说的话，
    /// 人会以为作业面台账已经建好了。</para>
    /// </summary>
    [Fact]
    public void T1c_走派生时说清楚档案还没填()
    {
        ProductionPlanContext.FaceDeriveHook = () => new List<FaceInput> { Face("派生面A") };
        ProductionPlanContext.Config();

        Assert.Contains(ProductionPlanContext.AssemblyNotes,
                        n => n.Contains("从本期工序作业区派生") && n.Contains("作业面台账"));
    }

    // ══════════════════════════════════════════════════════════════
    //  T2 链路体检跟着一起变
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// T2 走派生时链路体检里「作业面」那一段是<b>真实</b>，不再列进"吃样例"。
    /// <para>体检的状态是装配现场记的（H4）—— 这一条从真实那一侧验它确实跟着变，
    /// 而不是恒为「样例」。</para>
    /// </summary>
    [Fact]
    public void T2_体检里作业面变成真实()
    {
        ProductionPlanContext.FaceDeriveHook = () => new List<FaceInput> { Face("派生面A") };
        var rep = ProductionChainStatus.Inspect();

        var face = rep.Stages.Single(s => s.Name == "作业面");
        Assert.Equal(ProductionPlanContext.ChainState.Real, face.State);
        Assert.False(face.IsSample);

        // 结论里不再把它**列进吃样例的那几段**。
        // ★ 不能写成 DoesNotContain("作业面", Headline)：走这一支时结论里本来就有
        //   「作业面是真的，但上面这几段仍是示例露天矿的数据」—— 那三个字是**该出现**的。
        //   判成"标题里不许出现作业面"会把一句正确的结论判成错。
        Assert.DoesNotContain(rep.Stages.Where(x => x.IsSample).Select(x => x.Name), n => n == "作业面");
        if (rep.SampleCount > 0) Assert.Contains("作业面是真的", rep.Headline);
    }

    // ══════════════════════════════════════════════════════════════
    //  T3 补齐不顶替（这一条 F2 只单测了合并函数，这里验的是 Config 真的这么干）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// T3 派生返回空 / null 时，行为与这一层出现之前<b>逐字一致</b>（落样例）。
    /// <para>F0 纪律：关掉这个来源必须真的什么都不做。</para>
    /// </summary>
    [Fact]
    public void T3_派生为空时与从前逐字一致()
    {
        ProductionPlanContext.FaceDeriveHook = () => new List<FaceInput>();
        ProductionPlanContext.Config();
        Assert.Equal(ProductionPlanContext.FaceOriginKind.None, ProductionPlanContext.FaceOrigin);

        Reset();
        ProductionPlanContext.FaceDeriveHook = () => null;
        ProductionPlanContext.Config();
        Assert.Equal(ProductionPlanContext.FaceOriginKind.None, ProductionPlanContext.FaceOrigin);
        Assert.False(ProductionPlanContext.FacesAreReal);
    }

    /// <summary>
    /// T3b 派生源<b>抛异常</b>时不拖垮整盘装配，落样例并把原因记下来。
    /// <para>派生只是一个来源 —— 它炸了不该让当日计划整个出不来。</para>
    /// </summary>
    [Fact]
    public void T3b_派生源抛异常时不拖垮装配()
    {
        ProductionPlanContext.FaceDeriveHook = () => throw new InvalidOperationException("算例故意炸");
        var cfg = ProductionPlanContext.Config();

        Assert.NotNull(cfg);
        Assert.Empty(cfg.Faces);                            // 派生炸了 ⇒ 没有面（不再补样例）
        Assert.Equal(ProductionPlanContext.FaceOriginKind.None, ProductionPlanContext.FaceOrigin);
        Assert.Contains(ProductionPlanContext.AssemblyNotes, n => n.Contains("派生作业面失败"));
    }

    // ══════════════════════════════════════════════════════════════
    //  T4 派生的面真的往下游走通了
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// T4 派生的面带的<b>源端坐标真的被运距那一段用上了</b>，且记的是「工序区质心」这一档。
    /// <para>只判 Config 里有这个面是不够的：面进了盘子而坐标没被采纳，
    /// 运距会整条落到兜底，而每一步都不报错。</para>
    /// </summary>
    [Fact]
    public void T4_派生面的源坐标被运距采纳且不冒充录入()
    {
        ProductionPlanContext.FaceDeriveHook = () => new List<FaceInput> { Face("派生面A", "4-B1-P1") };
        ProductionPlanContext.Config();

        Assert.Contains("工序区质心", ProductionPlanContext.HaulSourceLabel);
        Assert.DoesNotContain("录入 1 ", ProductionPlanContext.HaulSourceLabel);
    }

    /// <summary>
    /// T5 注入口用完必须清干净 —— 否则同一进程里后面的判据会莫名其妙地变绿。
    /// <para>这一条判的是 <see cref="Dispose"/> 真的在收摊：把它注释掉，
    /// 后跑的 G2「裸台架落到样例」会随机红，而红的原因看起来毫不相干。</para>
    /// </summary>
    [Fact]
    public void T5_注入口用完清干净()
    {
        ProductionPlanContext.FaceDeriveHook = () => new List<FaceInput> { Face("派生面A") };
        ProductionPlanContext.Config();
        Assert.True(ProductionPlanContext.FacesAreReal);

        Reset();
        ProductionPlanContext.Config();
        Assert.False(ProductionPlanContext.FacesAreReal);   // 清干净之后回到样例
    }
}
