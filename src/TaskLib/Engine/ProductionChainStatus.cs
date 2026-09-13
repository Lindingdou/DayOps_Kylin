// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ProductionChainStatus.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using ChainState = PitMine3D.Kylin.TaskLib.Engine.ProductionPlanContext.ChainState;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  日常生产组织的「链路体检」—— 这一盘计划，哪一段是真数据、哪一段还在吃样例。
//
//  ══ 为什么需要它 ══
//  装配一盘当日计划要串十三段（期次/班次/爆破时窗/穿孔计划/作业面/月计划/去向/运距/
//  编组/检修/设备可用性/采掘单元/编制锚点），每一段各自「台账优先、兜底其次」。
//  于是出现这样一种状态：**十三段里有九段在吃兜底，而界面上一切正常** ——
//  有面、有量、有编组、有甘特、有达成度，每个数都自洽。
//  那串来源文案确实把每一段都写出来了，可它是十三段拼成的一长句，
//  「用样例盘子」夹在第五段里，字号与其余十二段一模一样。人不会去读它。
//
//  ══ 两条纪律 ══
//  ① **状态由装配现场直接记下来**（<see cref="ProductionPlanContext.ChainStates"/>），
//     不从来源文案里正则抠。文案一改就静默抠空，而抠空之后「样例」会被当成「真实」——
//     体检整条失去意义，而且是**朝着让人放心的方向**失效的。
//  ② **没有独立来源标志的那几段如实标「未知」**，不按文案猜。
//     猜出来的绿比红更贵：红了人会去查，绿了没人会。
//
//  ══ 每一段都要给「补法」══
//  只说「作业面：样例」是没用的 —— 看的人下一秒就要问"那我该干什么"。
//  说不出补法的诊断等于没诊断（[[judgment-discipline]] 里"指向会烂"的一种）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>链路上的一段。</summary>
public sealed class ChainStage
{
    public string Name = "";
    public ChainState State = ChainState.Unknown;
    /// <summary>这一段当前的来源文案（装配时逐段记的原话）。</summary>
    public string Label = "";
    /// <summary>怎么把它变成真数据。<b>每一段都要有</b>。</summary>
    public string Fix = "";

    public string StateZh => State switch
    {
        ChainState.Real => "真实",
        ChainState.Partial => "部分真实",
        ChainState.Sample => "样例",
        ChainState.Missing => "缺",
        _ => "未知",
    };

    /// <summary>这一段在吃样例（体检报告里最要紧的那一类）。</summary>
    public bool IsSample => State == ChainState.Sample;

    /// <summary>
    /// 一行（或两行）：<c>状态｜段名：原话</c>，不是真实数据时**另起一行**给补法。
    /// <para>补法跟在原话后面同一行是不行的：原话动辄七八十字，补法会被推到横向滚动条外面 ——
    /// 而它恰恰是这份报告里最该被看见的那半句。</para>
    /// </summary>
    public string Line => $"{StateZh,-4}｜{Name}：{Label}"
                        + (State is ChainState.Real ? "" : $"\n        → {Fix}");
}

/// <summary>一次链路体检的结果。</summary>
public sealed class ChainReport
{
    public List<ChainStage> Stages = new();
    public string Headline = "";

    public int RealCount => Stages.Count(s => s.State == ChainState.Real);
    public int SampleCount => Stages.Count(s => s.IsSample);
    public int MissingCount => Stages.Count(s => s.State == ChainState.Missing);
    public int UnknownCount => Stages.Count(s => s.State == ChainState.Unknown);

    /// <summary>
    /// 这盘计划**可以拿去用**：作业面是真的，且没有任何一段在吃样例。
    /// <para>作业面单独拎出来判，是因为它一旦是样例，后面每一段接的都是示例露天矿的面 ——
    /// 其余段就算各自都从库里读到了东西，读出来的也对不上任何一个真实工程位置。</para>
    /// </summary>
    public bool Usable => SampleCount == 0
        && Stages.Any(s => s.Name == "作业面" && s.State == ChainState.Real);

    public string Text => Headline + "\n" + string.Join("\n", Stages.Select(s => "  " + s.Line));
}

/// <summary>日常生产组织的链路体检。永不抛。</summary>
public static class ProductionChainStatus
{
    /// <summary>
    /// 逐段的补法。<b>与段名一一对应</b> —— 加一段却忘了写补法，
    /// <see cref="Inspect"/> 会当场把它列成"缺补法"，而不是给一句空话。
    /// </summary>
    private static readonly Dictionary<string, string> Fixes = new(StringComparer.Ordinal)
    {
        ["期次"] = "在主界面接一次工程上下文（打开工程即可）——没接线时期次走的是样例日期。",
        ["班次"] = "在「班次日历」里排一次本月的班（早/中/夜与作业日）。",
        ["爆破时窗"] = "在「爆破记录」里录本日的炮次；没有炮次时停产时窗走的是样例那一炮。",
        ["穿孔计划"] = "在「钻爆计划」里排本日的钻机时窗（drill_plan）——空着就是本日没有穿孔，不是 0 延米。",
        ["检修档期"] = "在「检修计划」里排本日的定修时窗；空着时有效时窗不扣检修，铲会被排成整天满负荷。",
        ["作业面"] = "两条任选：① 到「采掘单元清单」排一期 → 「作业区划分」生成工序作业区并入库，"
                   + "面就从本期计划派生出来；② 直接在「作业面台账」里建档并保存一次。",
        ["去向登记簿"] = "在「排土场管理」/「去向台账」录入排土场与卸载点（破碎站可在「破碎站位置设置」定点）。",
        ["月计划"] = "在「短期生产计划 · 确定月度计划」里定一版本月方案 —— 没定时日目标走的是样例值。",
        ["运距"] = "把路网接通（「开拓运输系统」建网 + 装卸点定位）—— 走兜底运距的腿会一路把"
                 + "循环时间、配车数、编组班产带偏，而每一步都不报错。",
        ["编组"] = "在「设备编组」里录 dispatch_rule —— 表空时走的是内置缺省规则，"
                 + "装满铲数/推荐车数/循环时间刻意留 0。",
        ["设备可用性"] = "把设备台账录起来 —— 读不到时这一段整条不校核，"
                       + "状态为「检修」甚至「报废」的电铲照样被排满三个班。",
        ["采掘单元对号"] = "在「作业面台账」给每个采装面填「单元号」—— 没绑上时任务点不回图上的体，"
                       + "备采核销与期次覆盖率都算不了。",
    };

    /// <summary>逐段的来源文案取哪儿。</summary>
    private static Dictionary<string, Func<string>> Labels => new(StringComparer.Ordinal)
    {
        ["期次"] = () => ProductionPlanContext.PeriodSourceLabel,
        ["班次"] = () => ProductionPlanContext.ShiftSourceLabel,
        ["爆破时窗"] = () => ProductionPlanContext.BlastSourceLabel,
        ["穿孔计划"] = () => ProductionPlanContext.DrillSourceLabel,
        ["检修档期"] = () => ProductionPlanContext.MaintenanceSourceLabel,
        ["作业面"] = () => ProductionPlanContext.FaceSourceLabel,
        ["去向登记簿"] = () => ProductionPlanContext.SinkSourceLabel,
        ["月计划"] = () => ProductionPlanContext.PlanSourceLabel,
        ["运距"] = () => ProductionPlanContext.HaulSourceLabel,
        ["编组"] = () => ProductionPlanContext.FleetSourceLabel,
        ["设备可用性"] = () => ProductionPlanContext.EquipSourceLabel,
        ["采掘单元对号"] = () => ProductionPlanContext.UnitSourceLabel,
    };

    /// <summary>
    /// 体检。<b>会先装配一次盘子</b>（逐段状态就是那一次记下来的）。
    /// </summary>
    public static ChainReport Inspect()
    {
        var rep = new ChainReport();
        try { ProductionPlanContext.Config(); }
        catch (Exception ex)
        {
            rep.Headline = $"◆ 盘子装配失败（{ex.GetType().Name}：{ex.Message}）—— 体检做不下去。";
            return rep;
        }

        var states = ProductionPlanContext.ChainStates;
        var labels = Labels;

        // 顺序 = 现场的先后，不是字典序：人是顺着链路读的。
        foreach (var name in new[]
                 {
                     "期次", "班次", "作业面", "月计划", "去向登记簿", "运距", "编组",
                     "穿孔计划", "爆破时窗", "检修档期", "设备可用性", "采掘单元对号",
                 })
        {
            var st = states.TryGetValue(name, out var v) ? v : ChainState.Unknown;
            string label = labels.TryGetValue(name, out var f) ? Safe(f) : "";
            rep.Stages.Add(new ChainStage
            {
                Name = name,
                State = st,
                Label = label.Length > 0 ? label : "（这一段没有留下来源文案）",
                // 补法缺了就明说缺了 —— 给一句"请检查配置"等于没给
                Fix = Fixes.TryGetValue(name, out var fix) ? fix
                    : "◆ 这一段还没有写补法（开发漏了），请把这条报给开发。",
            });
        }

        rep.Headline = Compose(rep);
        return rep;
    }

    private static string Compose(ChainReport rep)
    {
        if (rep.SampleCount > 0)
        {
            var names = rep.Stages.Where(s => s.IsSample).Select(s => s.Name).ToList();
            bool faceIsSample = names.Contains("作业面");
            return $"◆◆ 这盘计划里有 {names.Count} 段在吃**样例数据**（{string.Join("、", names)}）"
                 + (faceIsSample
                     ? " —— **作业面就是其中之一**，所以甘特、任务书、达成度里的每一个数都出自「示例露天矿」，"
                     + "不是这个矿的。"
                     : " —— 作业面是真的，但上面这几段仍是示例露天矿的数据，"
                     + "它们会以「看着合理」的方式混进班产、时窗与去向。")
                 + $"　另有 {rep.MissingCount} 段是空的、{rep.UnknownCount} 段没有独立的来源标志。";
        }
        if (!rep.Usable)
            return $"◆ 没有任何一段在吃样例，但**作业面不是真实数据**（"
                 + rep.Stages.First(s => s.Name == "作业面").StateZh
                 + "）—— 后面每一段接的都不是真实工程位置。";
        return $"√ 十二段里 {rep.RealCount} 段是真实数据，没有一段在吃样例"
             + (rep.MissingCount > 0 ? $"；{rep.MissingCount} 段是空的（本日确实没有，下游按「没有」处理）" : "")
             + (rep.UnknownCount > 0 ? $"；{rep.UnknownCount} 段没有独立的来源标志，请看它们的原话" : "")
             + "。";
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? ""; }
        catch { return ""; }
    }
}
