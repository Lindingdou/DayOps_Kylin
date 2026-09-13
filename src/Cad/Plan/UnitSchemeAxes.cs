// 忠实移植自原 PitMine3D Modules/PlanLib/Views/UnitSchemeCompareWindow.cs 头部三个纯类型（UnitSchemeText / UnitSchemeAxes / UnitSchemeApplyOutcome，逐行对应；仅命名空间适配）。
// 比选窗口本体（UnitSchemeCompareWindow）在原版 2026-08-18 现场令后已从「采掘单元清单」撤掉入口（原文注释：现在从本窗口够不着了），故未移植。
using System;
using PitMine3D.Kylin.Cad.Units;

namespace PitMine3D.Kylin.Cad.Plan;

public static class UnitSchemeText
{
    public static string Dump(DumpOrder d) => d switch
    {
        DumpOrder.StepThenPanel => "逐带推进",
        DumpOrder.PanelThenStep => "逐幅到底",
        DumpOrder.NearestToSource => "就近优先",
        DumpOrder.MostRoom => "摊平(库容均)",
        _ => "?" + (int)d,
    };

    public static string Face(FacePriority f) => f switch
    {
        FacePriority.LongestFirst => "长带优先",
        FacePriority.LowestRatioFirst => "剥采比低",
        FacePriority.NearestFirst => "近路优先",
        _ => "?" + (int)f,
    };

    public static string Strike(StrikeAdvance s) => s switch
    {
        StrikeAdvance.OneWay => "单向",
        StrikeAdvance.BothEnds => "两头对采",
        StrikeAdvance.FromMiddle => "中间开切",
        _ => "?" + (int)s,
    };

    public static string Pair(PairingStrategy p) => p switch
    {
        PairingStrategy.MinHaul => "运输功最小",
        PairingStrategy.InternalFirst => "内排优先",
        PairingStrategy.LevelCapacity => "库容均衡",
        _ => "?" + (int)p,
    };
}

/// <summary>
/// 一套方案 = 四条派生轴各取一个值。<b>回调传的是轴，不是算好的结果</b> ——
/// 调用方必须拿它<b>重跑</b>一遍再写回表，否则"表里写着 A、实际执行 B"。
/// </summary>
public sealed class UnitSchemeAxes
{
    public DumpOrder DumpOrder;
    public FacePriority FacePriority;
    public StrikeAdvance StrikeAdvance;
    public PairingStrategy Strategy;

    public void ApplyTo(UnitPlanInput inp)
    {
        if (inp == null) return;
        inp.DumpOrder = DumpOrder;
        inp.FacePriority = FacePriority;
        inp.StrikeAdvance = StrikeAdvance;
        inp.Strategy = Strategy;
    }

    public static UnitSchemeAxes From(UnitPlanInput inp) => new()
    {
        DumpOrder = inp.DumpOrder,
        FacePriority = inp.FacePriority,
        StrikeAdvance = inp.StrikeAdvance,
        Strategy = inp.Strategy,
    };

    public string Caption =>
        $"排弃顺序={UnitSchemeText.Dump(DumpOrder)} · 作业面={UnitSchemeText.Face(FacePriority)}"
      + $" · 走向推进={UnitSchemeText.Strike(StrikeAdvance)} · 配对={UnitSchemeText.Pair(Strategy)}";
}

/// <summary>
/// 调用方「用这套重跑并写回表」的回执。
/// <para><b>Result 必须是重跑出来的那一份</b>（不是比选时算的）—— 比选窗口拿它和
/// 比选时的签名对一遍，不一致就当场报出来。不对这一遍的话，
/// "引擎不是纯函数"或"两次输入不同"这两种错都会静默通过。</para>
/// </summary>
public sealed class UnitSchemeApplyOutcome
{
    /// <summary><b>重跑</b>出来的结果。null = 没跑成（Message 说为什么）。</summary>
    public UnitPlanResult? Result;
    /// <summary>给人看的一段话（调用方状态栏也用这一份）。</summary>
    public string Message = "";
    /// <summary>有没有真的写回表。自洽没过时应为 false。</summary>
    public bool WrittenBack;
}

