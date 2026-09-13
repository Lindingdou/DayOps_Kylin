using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 会话级共享：短期(月度)生产计划的「基础约束」(Base，单一) + 候选「月度计划方案」集(Schemes)。
/// 六个入口共用同一实例（编制=编 Base；现场参数/可采区域/开采程序=编 Base 子项；
/// 月度计划编制/派生/对比 用 Schemes）。
///
/// 模型（见 docs/短期生产计划_设计.md）：Base = 不随方案变的给定约束；方案一律由「派生计划方案」
/// 在 Base 上变决策变量（作业组织×工作历）生成。首次访问以默认 Base + 默认派生一批演示。
/// </summary>
public static class ShortTermSchemeStore
{
    private static ShortTermBase? _base;
    private static ObservableCollection<ShortTermPlan>? _schemes;

    /// <summary>
    /// 基础约束（单一基准/盘子）。「短期生产计划编制」窗口编辑它。
    /// <para><b>首次创建时铺【主体案例】</b>（<see cref="PlanCase"/>）—— 全链缺省只有那一处来源。
    /// 只铺一次：人改过之后这里绝不回头覆盖（`??=` 已经保证了这一点）。</para>
    /// </summary>
    public static ShortTermBase Base => _base ??= PlanCase.NewBase();

    /// <summary>候选月度计划方案集（由派生产生；月度计划编制/对比/出图共用）。首次给一批默认派生。</summary>
    public static ObservableCollection<ShortTermPlan> Schemes => _schemes ??= CreateDefault();

    /// <summary>「确定月度计划」选定的主方案（供下游/出图默认选中；可为空）。</summary>
    public static ShortTermPlan? Confirmed { get; set; }

    private static ObservableCollection<ShortTermPlan> CreateDefault()
    {
        var list = ShortTermScheduler.Generate(Base,
            new[]
            {
                new ShortTermScheduler.DispatchSpec("均衡型", DispatchStrategy.Balanced),
                new ShortTermScheduler.DispatchSpec("多面展开", DispatchStrategy.MultiFace),
            },
            new[]
            {
                new ShortTermScheduler.CalendarSpec("标准", CalendarScenario.Standard),
            },
            schedule: true);
        return new ObservableCollection<ShortTermPlan>(list);
    }
}
