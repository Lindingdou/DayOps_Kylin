// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ProductionChainStatusTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「链路体检」的判据（H 组）。
///
/// <para>装配一盘当日计划要串十二段，每一段各自「台账优先、兜底其次」。于是有这样一种状态：
/// <b>一半的段在吃兜底，而界面上一切正常</b> —— 有面、有量、有编组、有甘特、有达成度，
/// 每个数都自洽。这一组判的就是那件事说没说出来、说得够不够响、以及**状态是不是真的**。</para>
///
/// <para><b>最要紧的一条是 H4</b>：状态必须由装配现场记下来，不能从来源文案里正则抠。
/// 文案一改就静默抠空，而抠空之后「样例」会被当成「真实」—— 体检整条失效，
/// 而且是<b>朝着让人放心的方向</b>失效的。</para>
/// </summary>
[Collection("PlanContext")]
public class ProductionChainStatusTests
{
    /// <summary>
    /// H1 <b>每一段都要给得出补法</b>。
    /// <para>只说"作业面：样例"是没用的 —— 看的人下一秒就要问"那我该干什么"。
    /// 加一段却忘了写补法，这一条必红（体检会把它列成「开发漏了」）。</para>
    /// </summary>
    [Fact]
    public void H1_每一段都给得出补法()
    {
        var rep = ProductionChainStatus.Inspect();
        Assert.NotEmpty(rep.Stages);
        Assert.All(rep.Stages, st =>
        {
            Assert.False(string.IsNullOrWhiteSpace(st.Fix), $"「{st.Name}」没有补法");
            Assert.DoesNotContain("开发漏了", st.Fix);
        });
    }

    /// <summary>
    /// H2 每一段都留下了**来源文案**（原话），不是只留一个状态码。
    /// <para>状态码回答"是不是真的"，原话回答"到底是什么"。少了后者，
    /// 「样例」与「读库失败所以退样例」在报告里长得一模一样，而补法完全不同。</para>
    /// </summary>
    [Fact]
    public void H2_每一段都留下来源原话()
    {
        var rep = ProductionChainStatus.Inspect();
        Assert.All(rep.Stages, st => Assert.False(string.IsNullOrWhiteSpace(st.Label)));
    }

    /// <summary>
    /// H3 有段在吃样例时，结论要**顶格响亮**，而且要**点名是哪几段**。
    /// <para>"部分数据来自示例"这种说法等于没说 —— 人不知道该去补哪一张表。</para>
    /// </summary>
    [Fact]
    public void H3_有样例时结论响亮且点名()
    {
        var rep = ProductionChainStatus.Inspect();
        if (rep.SampleCount > 0)
        {
            Assert.StartsWith("◆◆", rep.Headline);
            foreach (var st in rep.Stages.Where(x => x.IsSample))
                Assert.Contains(st.Name, rep.Headline);
        }
        else
        {
            // 没有样例时也不许含糊：要么明说全真，要么明说作业面不真
            Assert.True(rep.Headline.StartsWith("√") || rep.Headline.StartsWith("◆"),
                        "结论既不是「全真」也不是「作业面不真」—— 这是一句含糊话");
        }
    }

    /// <summary>
    /// H4 <b>不变式</b>：「作业面」这一段的状态与 <see cref="ProductionPlanContext.FaceOrigin"/>
    /// 严格一致 —— 状态是装配现场记的，不是从文案抠的。
    /// <para>把 Mark 那一行删掉、改成按文案里有没有"样例"两个字来判，这一条会在
    /// 文案措辞变化的那一天静默变绿 —— 所以这里判的是两个**独立记录**之间的一致性。</para>
    /// </summary>
    [Fact]
    public void H4_作业面状态与来源层严格一致()
    {
        var rep = ProductionChainStatus.Inspect();
        var face = rep.Stages.Single(s => s.Name == "作业面");

        Assert.Equal(ProductionPlanContext.FacesAreReal,
                     face.State == ProductionPlanContext.ChainState.Real);
        // ★ 样例不再兜底：没有面时这一段是「缺」，不是「样例」——
        //   标成样例会让体检报告说一件不存在的事。
        Assert.Equal(ProductionPlanContext.NoFaces,
                     face.State == ProductionPlanContext.ChainState.Missing);
    }

    /// <summary>
    /// H5 <b>每一段都有独立的来源标志</b>，没有一段是「未知」。
    ///
    /// <para>这一条是 2026-08-18 那次体检直接换来的：当时月计划/运距/编组/设备可用性/采掘单元
    /// 这五段只有文案、没有标志，只能如实标「未知」—— 而报告一出来就看见，
    /// 那五段里至少三段<b>正在吃兜底</b>（月计划「用样例日目标」、编组「内置缺省编组规则」、
    /// 设备可用性「本次不校核」）。"未知"不是中立的：它把三段兜底藏在了一个看着无害的词后面。</para>
    ///
    /// <para>新加一段而忘了给它标志，这一条必红 —— 这正是它存在的理由。</para>
    /// </summary>
    [Fact]
    public void H5_每一段都有独立的来源标志()
    {
        var rep = ProductionChainStatus.Inspect();
        var blind = rep.Stages
            .Where(s => s.State == ProductionPlanContext.ChainState.Unknown)
            .Select(s => s.Name).ToList();
        Assert.True(blind.Count == 0,
            "这几段还没有独立的来源标志，状态只能靠猜：" + string.Join("、", blind));
    }

    /// <summary>
    /// H5b 状态<b>不许</b>从来源文案里正则抠。
    /// <para>反证：把「月计划」的文案换掉一个字，状态不应该跟着变 —— 这里判的是
    /// 状态与文案是两个独立记录（文案里有"样例"二字而状态是 Real、或反过来，都合法）。
    /// 具体地：`ShortTermLink.LastFromLedger` 与那一段的状态严格一致。</para>
    /// </summary>
    [Fact]
    public void H5b_月计划状态取自标志而不是文案()
    {
        var rep = ProductionChainStatus.Inspect();
        var st = rep.Stages.Single(s => s.Name == "月计划");
        Assert.Equal(ShortTermLink.LastFromLedger,
                     st.State == ProductionPlanContext.ChainState.Real);
    }

    /// <summary>
    /// H6 <b>可用</b>的判定要同时看两件事：一段样例都没有，<b>且</b>作业面是真的。
    /// <para>只判"没有样例"是不够的：作业面缺失（不是样例、是空）时其余段就算全从库里读到，
    /// 读出来的也对不上任何一个真实工程位置。</para>
    /// </summary>
    [Fact]
    public void H6_可用要同时满足没有样例且作业面是真的()
    {
        var rep = ProductionChainStatus.Inspect();
        bool faceReal = rep.Stages.Single(s => s.Name == "作业面").State
                        == ProductionPlanContext.ChainState.Real;
        Assert.Equal(rep.SampleCount == 0 && faceReal, rep.Usable);
    }

    /// <summary>
    /// H7 裸台架里必然不可用，而且作业面必然是样例。
    /// <para>第一条 Assert 是故意的：它红了不是这一层坏了，而是判据工程连上了真库副本 ——
    /// 那件事本身就值得当场知道。</para>
    /// </summary>
    [Fact]
    public void H7_裸台架里必然不可用()
    {
        var rep = ProductionChainStatus.Inspect();
        Assert.False(rep.Usable,
            "裸台架里不该出现「可用」—— 这一条红了说明判据工程连上了真库副本，见 offline-real-db-harness");
        // 作业面这一段是「缺」而不是「样例」（样例已停用）
        Assert.Contains(rep.Stages, s => s.Name == "作业面"
                        && s.State == ProductionPlanContext.ChainState.Missing);
    }

    /// <summary>H8 十二段一段不少，且顺序是现场的先后而不是字典序。</summary>
    [Fact]
    public void H8_十二段齐全且按现场顺序()
    {
        var rep = ProductionChainStatus.Inspect();
        var want = new[]
        {
            "期次", "班次", "作业面", "月计划", "去向登记簿", "运距", "编组",
            "穿孔计划", "爆破时窗", "检修档期", "设备可用性", "采掘单元对号",
        };
        Assert.Equal(want, rep.Stages.Select(s => s.Name).ToArray());
    }

    /// <summary>H9 整份报告能一次性摊成文本（界面/日志直接贴）。</summary>
    [Fact]
    public void H9_报告能摊成文本()
    {
        var text = ProductionChainStatus.Inspect().Text;
        Assert.Contains("作业面", text);
        Assert.Contains("→", text);          // 不是真实的那些段后面跟着补法
    }
}
