// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/BuiltInTemplates.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 内置报表模板（预设）—— 让「一键生成」开箱即用。用户克隆后即可定制成本矿样式。
/// 前几张是接管旧"产量统计/达成度"等写死窗口的模板化替身；
/// 后两张（采排物流日报 / 采—排流向矩阵）回答的是露天矿每天必答的那个问题：
/// **从哪采剥、排弃到哪、拉多远、还排得下吗**。
/// </summary>
public static class BuiltInTemplates
{
    public static List<ReportDefinition> All() => new()
    {
        ComprehensiveDaily(),
        DailyByPanel(),
        OutputByEquipment(),
        StripByProcess(),
        HaulLogisticsDaily(),
        FlowMatrix(),
        NarrativeDaily(),
    };

    private static ReportColumn Dim(string header, double w = 120) =>
        new() { Header = header, Width = w, Align = CellAlign.Left, Bind = ColBind.Dimension };
    private static ReportColumn Ind(string header, string id, double w = 90, bool total = true) =>
        new() { Header = header, Width = w, Align = CellAlign.Right, Bind = ColBind.Indicator, IndicatorId = id, Total = total };

    /// <summary>综合生产日报（全指标）—— 展示扩充后的取数：吨位/台班产量/单耗/运距/剥采比。</summary>
    public static ReportDefinition ComprehensiveDaily() => new()
    {
        Id = "builtin_comprehensive_daily",
        Name = "综合生产日报（全指标）",
        Level = "矿",
        Title = "露天矿综合生产日报",
        BuiltIn = true,
        DetailGroup = GroupDim.Panel,
        KpiIndicators = { "actual_t", "coal_t", "strip_ratio_t", "attain", "util", "avg_haul_dist" },
        Columns =
        {
            Dim("作业面"),
            Ind("实绩m³", "actual_vol"),
            Ind("实绩(吨)", "actual_t"),
            Ind("达成%", "attain", 66),
            Ind("台班产量", "shift_output", 84),
            Ind("单位油耗", "fuel_per_m3", 80),
            Ind("运距km", "avg_haul_dist", 66),
        },
        ShowTotalRow = true,
        Charts = { new ChartSpec { Title = "各作业面 计划 vs 实绩(吨)", IndicatorId = "plan_t", IndicatorId2 = "actual_t", By = GroupDim.Panel } },
        ShowConclusion = true,
    };

    /// <summary>生产日报（按作业面）—— 接管旧"任务报表"的日报口径。</summary>
    public static ReportDefinition DailyByPanel() => new()
    {
        Id = "builtin_daily_panel",
        Name = "生产日报（按作业面）",
        Level = "矿",
        Title = "露天矿生产日报",
        BuiltIn = true,
        DetailGroup = GroupDim.Panel,
        Columns =
        {
            Dim("作业面"),
            Ind("计划m³", "plan_vol"),
            Ind("实绩m³", "actual_vol"),
            Ind("达成%", "attain", 70, total: true),
            Ind("欠产m³", "shortfall", 80),
            Ind("工时h", "plan_hours", 64),
        },
        ShowTotalRow = true,
        KpiIndicators = { "actual_vol", "attain", "strip_ratio", "shortfall" },
        Charts = { new ChartSpec { Title = "各作业面 计划 vs 实绩", IndicatorId = "plan_vol", IndicatorId2 = "actual_vol", By = GroupDim.Panel } },
        ShowConclusion = true,
    };

    /// <summary>设备产量报表（按设备）—— 接管旧"产量统计"设备维。</summary>
    public static ReportDefinition OutputByEquipment() => new()
    {
        Id = "builtin_output_equip",
        Name = "设备产量报表（按设备）",
        Level = "矿",
        Title = "设备产量与效率报表",
        BuiltIn = true,
        DetailGroup = GroupDim.Equipment,
        Columns =
        {
            Dim("设备", 90),
            Ind("计划m³", "plan_vol"),
            Ind("实绩m³", "actual_vol"),
            Ind("达成%", "attain", 70),
            Ind("工时利用率%", "util", 96),
            Ind("工时h", "plan_hours", 64),
        },
        ShowTotalRow = true,
        KpiIndicators = { "actual_vol", "attain", "util", "plan_hours" },
        Charts = { new ChartSpec { Title = "各设备 计划 vs 实绩", IndicatorId = "plan_vol", IndicatorId2 = "actual_vol", By = GroupDim.Equipment } },
        ShowConclusion = true,
    };

    /// <summary>采剥汇总（按工序）—— 剥采比一览。</summary>
    public static ReportDefinition StripByProcess() => new()
    {
        Id = "builtin_strip_process",
        Name = "采剥汇总（按工序）",
        Level = "矿",
        Title = "采剥量与剥采比汇总",
        BuiltIn = true,
        DetailGroup = GroupDim.Process,
        Columns =
        {
            Dim("工序", 90),
            Ind("计划m³", "plan_vol"),
            Ind("实绩m³", "actual_vol"),
            Ind("达成%", "attain", 70),
            Ind("任务数", "task_count", 64),
        },
        ShowTotalRow = true,
        KpiIndicators = { "actual_vol", "coal_vol", "waste_vol", "strip_ratio" },
        Charts = { new ChartSpec { Title = "各工序 计划 vs 实绩", IndicatorId = "plan_vol", IndicatorId2 = "actual_vol", By = GroupDim.Process } },
        ShowConclusion = true,
    };

    /// <summary>
    /// 采排物流日报（按去向）—— 露天矿的"另一半日报"：产量日报回答「采了多少」，
    /// 这张回答「排到哪、排得下吗、拉了多远」。明细带按**去向**展开：
    /// 各去向今日入方（占容方 / 吨量 / 车次估算）+ 平均运距 + 运输功 + 库容消耗率。
    /// </summary>
    public static ReportDefinition HaulLogisticsDaily() => new()
    {
        Id = "builtin_haul_logistics_daily",
        Name = "采排物流日报（按去向）",
        Level = "矿",
        Title = "露天矿采排物流日报",
        BuiltIn = true,
        DetailGroup = GroupDim.Destination,
        // 决策四问：今天干了多少运输功、平均拉多远、内排比例、有几个场子要满了
        KpiIndicators = { "transport_work", "avg_haul_weighted", "internal_dump_pct", "dump_volume", "sink_alert_count", "haul_coverage_pct" },
        Columns =
        {
            Dim("去向", 130),
            Ind("入方(万m³占容)", "dump_volume", 110),
            Ind("吨量(t)", "actual_t", 90),
            Ind("车次(估)", "truck_trips", 78),
            Ind("平均运距km", "avg_haul_dist", 88, total: true),
            Ind("运输功(万t·km)", "transport_work", 104),
            Ind("库容消耗率%", "dump_capacity_used_pct", 96, total: true),
        },
        ShowTotalRow = true,
        Charts =
        {
            new ChartSpec { Title = "各去向 排弃占容量", IndicatorId = "dump_volume", By = GroupDim.Destination },
            new ChartSpec { Title = "各作业面 运输功", IndicatorId = "transport_work", By = GroupDim.Panel },
        },
        ShowConclusion = true,
        Footnotes =
        {
            "入方按【占容方 V容 = 实方 × Kr(残余膨胀)】计——排土库容吃的是沉降稳定后的体积，按实方扣会少扣 10~20%。",
            "排弃量以采装侧（源侧）为准；采装面全部未接去向时才回落排土侧受排实绩，两侧不叠加。",
            "库容消耗率 = 本期排弃占容 ÷ 该去向剩余库容（去向台账当前值），除一下即知还能撑几期。",
            "平均运距为吨量加权，不是简单平均；只统计任务上有真运距的量，其余不做估算填充。",
        },
    };

    /// <summary>
    /// 采—排流向矩阵（O-D 交叉表）—— 行=源(作业面)、列=汇(去向)、格=该 O-D 的吨量（括号内运距）。
    /// 这是露天矿调度最经典的一张表，直接对应"从哪采剥、排弃到哪"这个问题本身：
    /// 一眼看出哪个面在往哪个场排、哪条 O-D 拉得最远（运输功的大头就在那里）。
    /// </summary>
    public static ReportDefinition FlowMatrix() => new()
    {
        Id = "builtin_flow_matrix",
        Name = "采—排流向矩阵（源×汇）",
        Level = "矿",
        Title = "露天矿采—排流向矩阵",
        BuiltIn = true,
        Kind = ReportKind.Matrix,
        DetailGroup = GroupDim.Panel,
        KpiIndicators = { "actual_t", "transport_work", "avg_haul_weighted", "internal_dump_pct" },
        Matrix = new MatrixSpec
        {
            Title = "采—排流向矩阵（行=作业面，列=去向）",
            RowDim = GroupDim.Panel,
            ColDim = GroupDim.Destination,
            IndicatorId = "actual_t",        // 单元格主值：该 O-D 的实绩吨量
            IndicatorId2 = "avg_haul_dist",  // 括号内：该 O-D 的加权运距
            ShowTotals = true,
            HideEmpty = true,
            SourceSideOnly = true,           // 源必须是采装面，排土面是汇侧作业，计进来会重复
        },
        // 矩阵是主体，明细带只留一条"按作业面的量与运输功"作为侧证
        Columns =
        {
            Dim("作业面"),
            Ind("实绩(t)", "actual_t"),
            Ind("平均运距km", "avg_haul_dist", 88, total: true),
            Ind("运输功(万t·km)", "transport_work", 104),
        },
        ShowTotalRow = true,
        ShowConclusion = true,
        Footnotes =
        {
            "单元格 = 该「作业面 → 去向」当期实绩吨量，括号内为该 O-D 的吨量加权运距(km)；无流量的 O-D 留空，不写 0。",
            "只统计采装侧事实：排土面是同一批料的汇侧作业，作为源计进来会让同一方岩在矩阵里出现两次。",
            "运输功 = 吨量 × 等效运距（等效运距含坡度折算，内排多为下排故等效运距低于实距）——它是流向方案比选的核心成本代理。",
        },
    };

    /// <summary>生产日报分析报告（叙述版 / 报告）—— 占位符自动成文 + KPI 附表 + 签批栏。</summary>
    public static ReportDefinition NarrativeDaily() => new()
    {
        Id = "builtin_narrative_daily",
        Name = "生产日报分析报告（叙述）",
        Level = "矿",
        Title = "露天矿生产日报分析报告",
        BuiltIn = true,
        Kind = ReportKind.Narrative,
        KpiIndicators = { "actual_vol", "attain", "strip_ratio", "shortfall" },
        DetailGroup = GroupDim.Panel,
        ShowConclusion = false,
        ShowTotalRow = true,
        NarrativeSections =
        {
            new NarrativeSection
            {
                Heading = "一、生产完成情况",
                Body = "本报告期（{@periodLabel}），{@mine}{@scope}范围共安排生产任务 {task_count} 项，全工序实绩工作量 {actual_vol} m³（计划 {plan_vol} m³，计划工时 {plan_hours} h）。采装侧采出 {coal_vol} m³ 实方、折 {coal_t} t，剥离 {waste_vol} m³ 实方，其中表土 {topsoil_volume} 万m³；综合剥采比 {strip_ratio_t} m³/t（剥离实方÷采出吨量），采出占比 {ore_ratio}%。"
            },
            new NarrativeSection
            {
                Heading = "二、计划达成分析",
                Body = "本期采剥总量达成率 {attain}%，欠产 {shortfall} m³，设备工时利用率 {util}%。总体完成情况见下附表。"
            },
            new NarrativeSection
            {
                Heading = "三、采排物流",
                Body = "本期排弃占容 {dump_volume} 万m³，内排率 {internal_dump_pct}%，库容消耗率 {dump_capacity_used_pct}%，库容预警 {sink_alert_count} 个。完成运输功 {transport_work} 万t·km，吨量加权平均运距 {avg_haul_weighted} km，运距覆盖率 {haul_coverage_pct}%。运输功是全矿运输成本最稳的代理量，加大内排、缩短等效运距是降本的第一杠杆。"
            },
            new NarrativeSection
            {
                Heading = "四、存在问题与原因",
                Body = "达成率偏低的作业面需重点关注设备保障、运力配置与工序接续；库容临近上限的排土场须提前安排接续或抬升台阶；具体分布见附表与采排物流日报。"
            },
            new NarrativeSection
            {
                Heading = "五、下一步建议",
                Body = "对达成率偏低的作业面加强调度，将欠量滚动回摊至次日；对运距偏长的流向重新比选去向，确保月度采剥计划与运输功指标均衡完成。"
            },
        },
        Columns =
        {
            Dim("作业面"),
            Ind("计划m³", "plan_vol"),
            Ind("实绩m³", "actual_vol"),
            Ind("达成%", "attain", 70),
        },
        SignatureLine = "编制：＿＿＿＿＿　　审核：＿＿＿＿＿　　批准：＿＿＿＿＿",
    };
}
