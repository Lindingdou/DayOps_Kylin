// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/DayCapacityBudget.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  当日能力预算 —— 「这盘计划到底排不排得下」，在按下裂解之前就答出来。
//
//  编制配置窗里那几个旋钮（天气降效 / 交接班损失 / 有效工时 / 作业组织 / 配煤）单看都是抽象的
//  百分数和小时数，人改完不知道改了多少方量。本类把它们统一翻译成**方量**：
//      当日能力 = Σ_面 [ 编组班产 × 天气系数 × 该设备当日有效工时 ]
//      当日欠产 = Σ_面 max(0, 日目标 − 该面当日能力)
//      当日闲置 = Σ_面 max(0, 该面当日能力 − 日目标)
//
//  ★ 为什么必须逐面算、不能拿全矿总能力去减全矿总目标：
//    能力不是可以在面之间流动的东西 —— 每个面绑着自己那台铲。A 面闲 3000、B 面欠 3000，
//    全矿总账是"刚好排满"，实际是一台铲在晒太阳、另一台干不完。总账会把这件事整个抹掉，
//    而它恰恰是**作业组织策略**要解决的问题（策略做的就是把量从欠的面挪到闲的面）。
//    所以本类同时给「欠」和「闲」两个数：两个都不为零 = 这盘子该考虑换组织策略。
//
//  ★ 与装箱是同一笔账，不是另算一遍：
//    逐面能力这一步与 TaskExploder.ExplodeFace 的 slot 计算逐字对齐
//    （同一个 WorkWindowCalc、同一个 avail≥0.5 门槛、同一个 Math.Max(1e-6, ...)），
//    于是 Shortfall 必然逐面等于装箱报出来的「当日欠产」。判据 DayCapacityBudgetTests
//    把这条等式钉死：两边写同一个期望值，任一侧改了口径立刻红。
//
//  ★ 采装与排土**分开列，永不相加**：
//    采装面 DayTargetM3 是原位实方，排土面是排弃占容方（V容 = V实 × Kr）。
//    两个口径的方量加在一起得到的数没有任何物理含义（见 DeriveDumpTargets 的口径差注释）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一道工序（采装 / 排土）的当日能力预算。方量口径见类注释，两道工序不许相加。</summary>
public sealed class CapacityLine
{
    public ProcessType Process { get; init; }

    /// <summary>参与预算的作业面数。</summary>
    public int FaceCount { get; set; }

    /// <summary>当日能力合计 m³ = Σ 班产×天气系数×当日有效工时。</summary>
    public double CapacityM3 { get; set; }

    /// <summary>当日目标合计 m³（盘子里各面 DayTargetM3 之和）。</summary>
    public double TargetM3 { get; set; }

    /// <summary>逐面欠产合计 m³ = Σ max(0, 目标−能力)。与装箱报的「当日欠产」同一笔账。</summary>
    public double ShortM3 { get; set; }

    /// <summary>逐面闲置合计 m³ = Σ max(0, 能力−目标)。有欠又有闲 ⇒ 该考虑换作业组织策略。</summary>
    public double SlackM3 { get; set; }

    /// <summary>干不完的面数。</summary>
    public int ShortFaces { get; set; }

    /// <summary>今天没量、但有能力的面数（"空闲"条会挂在这些面上）。</summary>
    public int IdleFaces { get; set; }

    /// <summary>时窗被检修/爆破吃光、当日一小时也开不了工的面数。</summary>
    public int NoWindowFaces { get; set; }

    /// <summary>天气降效吃掉的方量 m³（分环节时 = 三项合计的实际损失，不降效时为 0）。</summary>
    public double WeatherCostM3 { get; set; }

    /// <summary>其中：把运输环节单独还原成不降效时，能多干出来的方量 m³。</summary>
    public double HaulDerateCostM3 { get; set; }

    /// <summary>其中：把采装环节单独还原成不降效时，能多干出来的方量 m³。</summary>
    public double LoadDerateCostM3 { get; set; }

    /// <summary>分环节降效下，因缺 τ_L/T_c 而只能按全盘值降的面数。</summary>
    public int NoCycleFaces { get; set; }

    /// <summary>交接班损失吃掉的方量 m³（HandoverRampH=0 时为 0）。</summary>
    public double HandoverCostM3 { get; set; }

    /// <summary>检修 + 爆破清场吃掉的方量 m³（相对于"三班满开"）。</summary>
    public double DowntimeCostM3 { get; set; }

    /// <summary>目标/能力，能力为 0 时返回 0。&gt;1 = 排不下。</summary>
    public double LoadRatio => CapacityM3 > 1e-6 ? TargetM3 / CapacityM3 : 0;

    public bool HasAny => FaceCount > 0;
}

/// <summary>
/// 当日能力预算：把编制配置里的旋钮翻译成方量，回答「这盘计划排不排得下」。
/// </summary>
public static class DayCapacityBudget
{
    /// <summary>采装 + 排土两道工序各自的预算。cfg 为 null 时返回空预算，不抛。</summary>
    public static (CapacityLine Load, CapacityLine Dump) Of(ExploderConfig? cfg)
    {
        var load = new CapacityLine { Process = ProcessType.Load };
        var dump = new CapacityLine { Process = ProcessType.Dump };
        if (cfg == null) return (load, dump);

        var blasts = cfg.BlastWindows();

        foreach (var f in cfg.Faces)
        {
            var line = f.Process == ProcessType.Load ? load
                     : f.Process == ProcessType.Dump ? dump
                     : null;
            if (line == null) continue;   // 穿孔/检修不经装箱切分，不进能力预算

            // ★ 与 TaskExploder.ExplodeFace 逐字对齐：cap 先吃天气系数并截到 1e-6，
            //   工时走同一个 WorkWindowCalc（同一个 avail≥0.5 门槛）。
            double capH = Math.Max(1e-6, f.Group.GroupCapacityM3PerH * cfg.WeatherFactorFor(f));
            double hours = WorkWindowCalc.DayHours(cfg.Shifts, f.Group.MainEquipment,
                                                   cfg.Maintenance, blasts, cfg.HandoverRampH);
            double cap = capH * hours;
            double target = Math.Max(0, f.DayTargetM3);

            line.FaceCount++;
            line.CapacityM3 += cap;
            line.TargetM3 += target;

            if (target - cap > 1) { line.ShortM3 += target - cap; line.ShortFaces++; }
            else if (cap - target > 1) line.SlackM3 += cap - target;

            if (hours < 0.5) line.NoWindowFaces++;
            else if (target <= 1) line.IdleFaces++;

            // ── 三个旋钮各自吃掉多少方量（都以"其余条件不变"为口径，故不可相加成总账）──
            double capHFull = Math.Max(1e-6, f.Group.GroupCapacityM3PerH);
            line.WeatherCostM3 += (capHFull - capH) * hours;

            // 分环节时再拆一层：把**某一个环节**单独还原成不降效，能多干出来多少。
            // 两项**不可相加**成 WeatherCostM3 —— 各自都是"只松这一个"的边际量，
            // 而两个环节是串联的（松开运输之后采装才可能成为新瓶颈）。
            if (cfg.HasLinkDerate)
            {
                if (!f.Group.HasCycleBreakdown && f.Process == ProcessType.Load) line.NoCycleFaces++;
                line.HaulDerateCostM3 += (Math.Max(1e-6, f.Group.GroupCapacityM3PerH * FactorWith(cfg, f, haulFree: true)) - capH) * hours;
                line.LoadDerateCostM3 += (Math.Max(1e-6, f.Group.GroupCapacityM3PerH * FactorWith(cfg, f, loadFree: true)) - capH) * hours;
            }

            double hoursNoHandover = WorkWindowCalc.DayHours(cfg.Shifts, f.Group.MainEquipment,
                                                             cfg.Maintenance, blasts, 0);
            line.HandoverCostM3 += capH * Math.Max(0, hoursNoHandover - hours);

            // "三班满开" = 无检修无爆破无交接；与之相比检修+爆破吃掉的部分
            double hoursIdeal = WorkWindowCalc.DayHours(cfg.Shifts, f.Group.MainEquipment, null, null, 0);
            line.DowntimeCostM3 += capH * Math.Max(0, hoursIdeal - hoursNoHandover);
        }

        return (load, dump);
    }

    /// <summary>
    /// 把某一个环节单独调成"不降效"之后的面系数。用 <see cref="ExploderConfig"/> 的浅拷贝算，
    /// 绝不改原盘子 —— 预算是只读的，它一旦改到 cfg 上，后面的装箱就按被预算改过的口径跑了。
    /// </summary>
    private static double FactorWith(ExploderConfig cfg, FaceInput f, bool haulFree = false, bool loadFree = false)
    {
        var probe = new ExploderConfig
        {
            WeatherDeratePct = cfg.WeatherDeratePct,
            LoadDeratePct = loadFree ? 0 : cfg.LoadDeratePct,
            HaulDeratePct = haulFree ? 0 : cfg.HaulDeratePct,
            DumpDeratePct = cfg.DumpDeratePct,
        };
        // 排土面只受排土降效，松采装/运输对它没有意义，直接给回原系数免得报出一个假的收益
        if (f.Process == ProcessType.Dump) return cfg.WeatherFactorFor(f);
        return probe.WeatherFactorFor(f);
    }

    /// <summary>
    /// 一句话结论（给编制配置窗的抬头）。<paramref name="reshuffled"/>=true 时说明装箱前还会重分配，
    /// 本预算只算"按上游给的分配"那一版。
    /// </summary>
    public static string Headline(CapacityLine load, bool reshuffled)
    {
        if (!load.HasAny) return "盘子里没有采装面，能力预算无从算起";

        string core = load.ShortM3 > 1
            ? $"◆ 这盘计划排不下：{load.ShortFaces} 个采装面干不完，合计欠 {load.ShortM3:N0} m³"
              + (load.SlackM3 > 1 ? $"；同时另有 {load.SlackM3:N0} m³ 能力闲着" : "")
            : $"◇ 这盘计划排得下：采装当日目标 {load.TargetM3:N0} / 能力 {load.CapacityM3:N0} m³"
              + (load.SlackM3 > 1 ? $"（余 {load.SlackM3:N0} m³）" : "");

        if (load.ShortM3 > 1 && load.SlackM3 > 1)
            core += " —— 有面干不完、有面闲着，正是作业组织策略要解决的情形";

        if (reshuffled)
            core += "。（当前策略非均衡型，装箱前还会按策略把量在面间重灌一遍，最终以装箱结果为准）";

        return core;
    }
}
