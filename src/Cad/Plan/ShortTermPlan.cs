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

// ─────────────────────────────────────────────────────────────────────────────
//  短期生产计划编制 —— 核心数据契约（月度计划）
//
//  定位（见 docs/短期生产计划_设计.md）：
//   · 计划三层级中的「短期」（1 周~数月，本系统取**月度**粒度）——把中长远进度计划某一年的
//     年采剥目标，按 工作历(有效作业日) × 设备能力 × 季节降效 × 作业组织 摊到 12 个月，
//     落到可采区域(三量保有) + 作业面接续，产出逐月生产计划表 + 完成率 + 均衡/设备指标。
//   · 它比中长远更「薄」——不另起优化器，只做**年→月的均衡细化 + 现场约束校核**。
//   · 多方案 = 单套 / 多套（作业组织 × 工作历方案 正交派生 → 联合对比）。
//   · 比选即比较多个 ShortTermPlan（对标 LongTermPlan）。
//
//  本文件只是数据 + 样例 + Clone；排产在 ShortTermScheduler，对比在 ShortTermComparer，图在 ShortTermCharts。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>作业组织策略（短期派生的「作业重心」轴）。</summary>
public enum DispatchStrategy
{
    Balanced,      // 均衡型：各月/各面平稳，月产变异最小
    Concentrated,  // 集中强采型：主作业面集中产能，峰值高、效率高、均衡差
    MultiFace      // 多面展开型：多作业面并行，设备利用高、衔接稳
}

/// <summary>工作历方案（短期派生的「有效作业日」轴）。</summary>
public enum CalendarScenario
{
    Standard,      // 标准：常规作业日
    Push,          // 抢产：增加作业日 / 压缩检修
    Conservative   // 保守：扣减雨雪/恶劣天气日
}

/// <summary>月度均衡目标权重（归一）：月产量 / 剥采比 / 设备负荷均衡。</summary>
public sealed class StBalanceWeights
{
    public double OutputSmooth { get; set; } = 0.45;  // 月产量均衡
    public double RatioSmooth { get; set; } = 0.30;   // 剥采比均衡（月间削峰）
    public double EquipSmooth { get; set; } = 0.25;   // 设备负荷均衡

    public double Sum => OutputSmooth + RatioSmooth + EquipSmooth;
    public static StBalanceWeights CreateDefault() => new();
    public StBalanceWeights Copy() => new() { OutputSmooth = OutputSmooth, RatioSmooth = RatioSmooth, EquipSmooth = EquipSmooth };
}

/// <summary>月度计划比选权重（归一，联合对比加权评分用）。完成率/均衡/设备利用/推进达标=高优，削峰=峰值低好。</summary>
public sealed class StDecisionWeights
{
    public double Completion { get; set; } = 0.28;    // 年目标完成率
    public double OutputBalance { get; set; } = 0.22; // 月产均衡
    public double EquipUtil { get; set; } = 0.18;     // 设备利用率
    public double PeakShaving { get; set; } = 0.16;   // 剥采比削峰
    public double AdvanceAttain { get; set; } = 0.16; // 推进达标

    public double Sum => Completion + OutputBalance + EquipUtil + PeakShaving + AdvanceAttain;
    public static StDecisionWeights CreateDefault() => new();
    public StDecisionWeights Copy() => new()
    {
        Completion = Completion, OutputBalance = OutputBalance, EquipUtil = EquipUtil,
        PeakShaving = PeakShaving, AdvanceAttain = AdvanceAttain
    };
}

/// <summary>
/// 一个作业面（台阶/分条）——开采程序内的最小调度单元。
///
/// 除几何/份额外，还带**物料维**（采什么）与**去向维**（送/排到哪）：月计划定的就是"从哪采剥、
/// 排弃到哪"，把去向留到日计划再猜，日计划只能靠等分或就近，成本口径必然失真。
/// 字段名是与下游作业计划（TaskLib 侧按名反射软读）的契约，**不要改名**，改名会静默断链。
/// </summary>
public sealed class WorkingFace
{
    public string Name { get; set; } = "作业面1";
    public double BenchElevationM { get; set; } = 0;       // 台阶标高 (m)
    public double SharePct { get; set; } = 100;            // 本面承担的产能份额 (%)
    public double AvailableReserveWanT { get; set; } = 0;  // 本面备采储量 (万t)
    public double AdvanceAzimuthDeg { get; set; } = 90;    // 推进方位 (°)
    public int Order { get; set; } = 1;                    // 接续次序（1=先采）
    public string Note { get; set; } = "";

    // ── 物料维 ──
    /// <summary>物料码（topsoil/weathered/rock/interburden/coal/lowgrade），参数一律经 PlanMaterialCatalog 取。</summary>
    public string MaterialCode { get; set; } = PlanMaterialCatalog.Coal;
    /// <summary>混采构成文本（如 "煤7∶岩3"，可空）。填了就按它拆流，不填按 MaterialCode + 默认剥离岩性构成。</summary>
    public string MaterialMixText { get; set; } = "";

    // ── 去向维 ──
    /// <summary>去向 Id（对 dump_site.dump_id / 装卸点）。空=排产时按运输功最小自动分配。</summary>
    public string DestinationId { get; set; } = "";
    /// <summary>去向显示名。</summary>
    public string DestinationName { get; set; } = "";
    /// <summary>去向类别文案（内排土场/外排土场/破碎站/原煤仓/储煤场/表土堆场）。</summary>
    public string DestinationKindText { get; set; } = "";
    /// <summary>实际运距 km。</summary>
    public double HaulDistanceKm { get; set; }
    /// <summary>等效运距 km（含坡度/路况折算，可空；0=按去向类型折算）。</summary>
    public double EquivHaulKm { get; set; }

    // ── 空间身份 ──
    /// <summary>
    /// 关联的空间对象：mineable_region.id 或工程位置 groupKey。
    /// 上游采区划分的 MiningPanel 带 BoundaryHandle，到短期这层被截断了，这个字段把空间身份接回来。
    /// </summary>
    public string SourceRefId { get; set; } = "";

    /// <summary>
    /// 库表身份：<c>working_face.face_code</c>（如 WF-1195-A）。空 = 这个面还没在「工作面管理」建档。
    ///
    /// <para><b>为什么必须有这一列</b>：本类与 <c>GeoDataBase.Public.Entities.WorkingFace</c> <b>同名不同物</b> ——
    /// 本类管份额/物料/去向/设备配置，库表管台阶几何（台阶高 / 坡面角 / 采宽 / 面长 / 月推进度）和主电铲。
    /// 在这一列出现之前，两者之间<b>一个关联字段都没有</b>，几何与设备台账在计划侧完全取不到。</para>
    ///
    /// <para><b>几何字段刻意不往这边复制</b>：复制就是第二份，两份迟早不一致而且都不报错
    /// （本仓库的两套色表 / 两套去向码都是这么来的）。要几何就按 face_code 去库里取。</para>
    /// </summary>
    public string FaceCode { get; set; } = "";

    // ── 设备维（型号级配置）──
    // 这里钉的是【类型/型号约束】，不是机号：机号是排产解出来的（MineAssLib.EquipmentAssigner），
    // 和「#5 钉面级去向约束、#6 解单元级对位」完全同构。在这儿排具体机号 = 第二套指派实现。
    // 留空 = 不约束，排产时全矿在册设备里挑。

    /// <summary>穿孔钻机型号（<c>equipment_model.model</c>，类别 Drill）。空 = 不约束。</summary>
    public string DrillModel { get; set; } = "";
    /// <summary>采装设备型号（电铲 / 前装机）。空 = 不约束。</summary>
    public string LoaderModel { get; set; } = "";
    /// <summary>运输卡车型号。空 = 不约束。</summary>
    public string TruckModel { get; set; } = "";
    /// <summary>排土推土机型号。空 = 不约束。</summary>
    public string DozerModel { get; set; } = "";

    /// <summary>
    /// 本面一台采装设备配几台车。<b>0 = 按现场编组规则</b>（<c>dispatch_rule.recommended_truck_count</c>）。
    /// <para>填了非 0 就是<b>面级覆盖</b>规则台账 —— 覆盖会写进指派结果的溯源里，别指望它悄悄生效。</para>
    /// </summary>
    public int TrucksPerLoader { get; set; } = 0;

    // ── 工艺维（穿爆采运排五工序链）──
    /// <summary>
    /// 本面的<b>工艺流程与工序参数</b>：走不走穿爆、孔网/单耗/超前期、采装方式、是否经破碎站、排土方式。
    /// <para><b>与上面四个 *Model 列是两件事</b>：型号说"用哪台机器"，工艺说"这道工序干不干、按什么参数干"。
    /// 同一台 WK-10 在表土面上不穿爆、在硬岩面上要穿爆 —— 型号列表达不了这件事。</para>
    /// <para>月度粒度：它推出的是<b>月工序量</b>（穿孔延米 / 炸药量 / 爆破次数），
    /// 不是某一炮的孔位。更细的粒度在下游作业计划。</para>
    /// </summary>
    public FaceProcessChain Process { get; set; } = new();

    // ── 展示派生 ──
    /// <summary>四工序设备配置的一行文案。全空时给「（未配）」——别显示成空白，空白读起来像没这一列。</summary>
    public string EquipConfigCaption
    {
        get
        {
            var seg = new List<string>(4);
            if (!string.IsNullOrWhiteSpace(DrillModel)) seg.Add("穿 " + DrillModel.Trim());
            if (!string.IsNullOrWhiteSpace(LoaderModel)) seg.Add("采 " + LoaderModel.Trim());
            if (!string.IsNullOrWhiteSpace(TruckModel))
                seg.Add("运 " + TruckModel.Trim() + (TrucksPerLoader > 0 ? $"×{TrucksPerLoader}" : ""));
            if (!string.IsNullOrWhiteSpace(DozerModel)) seg.Add("排 " + DozerModel.Trim());
            return seg.Count == 0 ? "（未配）" : string.Join(" · ", seg);
        }
    }

    /// <summary>工艺流程的一行文案（表格里显示用）。<c>Caption</c> 要物料码，绑定不方便，故在这里包一层。</summary>
    public string ProcessCaption => (Process ?? new FaceProcessChain()).Caption(MaterialCode);

    /// <summary>配齐了几个工序的设备型号（0~4）—— 窗口底栏统计用。</summary>
    public int EquipConfiguredCount
        => (string.IsNullOrWhiteSpace(DrillModel) ? 0 : 1) + (string.IsNullOrWhiteSpace(LoaderModel) ? 0 : 1)
         + (string.IsNullOrWhiteSpace(TruckModel) ? 0 : 1) + (string.IsNullOrWhiteSpace(DozerModel) ? 0 : 1);

    public string MaterialName => PlanMaterialCatalog.NameOf(MaterialCode);
    /// <summary>物料显示文案：有混采构成显示构成，否则显示单一物料名。</summary>
    public string MaterialText => string.IsNullOrWhiteSpace(MaterialMixText) ? MaterialName : MaterialMixText.Trim();
    public bool HasDestination => !string.IsNullOrWhiteSpace(DestinationId) || !string.IsNullOrWhiteSpace(DestinationName);
    public string DestinationCaption => HasDestination
        ? (string.IsNullOrWhiteSpace(DestinationKindText) ? DestinationName : $"{DestinationName}（{DestinationKindText}）")
        : "（自动分配）";

    public string Caption => $"{Name} · {BenchElevationM:0}m · {SharePct:0}% · {MaterialText} → {DestinationCaption}";
    public WorkingFace Copy() => new()
    {
        Name = Name, BenchElevationM = BenchElevationM, SharePct = SharePct,
        AvailableReserveWanT = AvailableReserveWanT, AdvanceAzimuthDeg = AdvanceAzimuthDeg,
        Order = Order, Note = Note,
        MaterialCode = MaterialCode, MaterialMixText = MaterialMixText,
        DestinationId = DestinationId, DestinationName = DestinationName, DestinationKindText = DestinationKindText,
        HaulDistanceKm = HaulDistanceKm, EquivHaulKm = EquivHaulKm, SourceRefId = SourceRefId,
        FaceCode = FaceCode,
        DrillModel = DrillModel, LoaderModel = LoaderModel, TruckModel = TruckModel, DozerModel = DozerModel,
        TrucksPerLoader = TrucksPerLoader,
        Process = Process.Copy(),        // ★ 深拷贝：派生方案共享同一个工艺对象的话，改一套等于改全部
    };
}

/// <summary>
/// 现场参数（「现场参数提取」编辑）：工作历(有效作业日 + 季节/检修降效) + 设备台账 + 年初已完成进度。
/// 决定每月的「有效作业能力」——月度分配的物理基础。
/// </summary>
public sealed class FieldParams
{
    public double StandardWorkdays { get; set; } = 25;     // 月标准作业日（无降效月）
    public int ShiftsPerDay { get; set; } = 3;             // 每日班次
    public int EquipmentCount { get; set; } = 4;           // 主采设备台数（电铲/挖机）
    public double EquipmentAvailabilityPct { get; set; } = 82; // 设备完好率 (%)
    public double EquipMonthlyCapacityWanM3 { get; set; } = 28; // 单台·满月作业能力 (万m³，含采+剥)

    public string WinterMonthsCsv { get; set; } = "12,1,2"; // 冬季/雨季降效月（逗号分隔）
    public double WinterDeratePct { get; set; } = 20;       // 季节降效幅度 (%)
    public int MaintenanceMonth { get; set; } = 7;          // 集中检修月（0=无）
    public double MaintenanceDeratePct { get; set; } = 30;  // 检修月降效 (%)

    public double YtdActualCoalWanT { get; set; } = 0;      // 年初至计划起点已采出 (万t)
    public double YtdActualStripWanM3 { get; set; } = 0;    // 年初至计划起点已剥离 (万m³)

    /// <summary>季节降效月集合。</summary>
    public HashSet<int> WinterMonths()
    {
        var set = new HashSet<int>();
        foreach (var t in (WinterMonthsCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12) set.Add(m);
        return set;
    }

    /// <summary>某月有效作业日（季节/检修/工作历方案叠加）。</summary>
    public double WorkdaysFor(int month, CalendarScenario scenario)
    {
        double wd = StandardWorkdays;
        if (WinterMonths().Contains(month)) wd *= 1 - WinterDeratePct / 100.0;
        if (MaintenanceMonth == month) wd *= 1 - MaintenanceDeratePct / 100.0;
        wd *= scenario switch
        {
            CalendarScenario.Push => 1.10,         // 抢产：加点/压缩检修
            CalendarScenario.Conservative => 0.92, // 保守：留天气余量
            _ => 1.0
        };
        return Math.Max(1, Math.Round(wd, 1));
    }

    public FieldParams Copy() => (FieldParams)MemberwiseClone();
}

/// <summary>
/// 可采区域（「采场/排土场圈定」编辑）：三量保有（开拓/准备/备采）+ 工程边界/矿权/安全限制说明 + 可采面积。
/// 约束本期可调度的储量上限与边界（短期版的「分期境界截面」）。
/// </summary>
public sealed class MineableArea
{
    /// <summary>
    /// 备采储量 (万t)。<b>由几何算出</b> —— 走「量驱动采剥接续」那条链
    /// （<c>MonthlyMineSchedule</c> 逐月算 → <c>MinePlanExport</c> 契约 →
    /// <c>MinePlanImporter.ApplyPreparedReserve</c> 写进来）。默认 0 = 还没算过。
    /// </summary>
    public double PreparedReserveWanT { get; set; }

    // ── 已删：开拓储量 / 准备储量 / 可采面积 / 已揭露台阶数 / 安全限制 / 矿权限制 ──
    //  这六个字段各自只有【声明那一行】被引用，零读零写，从加进来到删掉一次都没用过。
    //  留着的代价不是内存，是误导：
    //    · 「短期生产计划编制」的提示里写着"三量保有 →「采场/排土场圈定」"，
    //    · 「确定开采程序」拿不到备采时报错也指向那个窗口，
    //  而那个窗口（现改名「采场/排土场圈定」）按 DEM 残差判凹凸，全文不碰煤层/储量/矿权/煤柱。
    //  声明摆在这儿，会让下一个人以为"只是还没填"，于是再去接一遍一个本来就不存在的来源。
    //  真要做三量统计时，它需要块体模型/煤层面作输入 —— 那是一块新开发，不是补几个字段。

    /// <summary>备采保有月数（备采储量 ÷ 月均采出）——三量保有校核用。</summary>
    public double PreparedMonths(double monthlyCoalWanT)
        => monthlyCoalWanT > 1e-6 ? PreparedReserveWanT / monthlyCoalWanT : 0;

    public MineableArea Copy() => (MineableArea)MemberwiseClone();
}

/// <summary>
/// 进度计划「一月」——排产产物（逐月计划行）。
///
/// 本行的事实来源是 <see cref="Flows"/>（本月的一条条源—汇物料流）：采出量、剥离量、剥采比、
/// 内/外排、排弃占容、运输功、加权运距全部由它聚合出来。
/// 两个标量 + 一个内/外排二值答不了「排弃到哪、排不排得下」，所以标量降级为**由流派生的兼容视图**：
/// Flows 非空时读派生值，为空时退回历史标量（老方案/外部直接赋值仍然可用）。
/// </summary>
public sealed class MonthPeriod
{
    public string Label { get; set; } = "";    // 月标签（如 2027-01）
    public int Month { get; set; }              // 月份 1..12
    public double Workdays { get; set; }        // 有效作业日

    /// <summary>本月物料流子表（源—汇 O-D 明细）——月计划物料维/去向维的载体。</summary>
    public List<PlanFlow> Flows { get; set; } = new();

    private double _coalWanT, _stripWanM3, _ratio;
    private DumpMode _dump = DumpMode.External;

    /// <summary>本行是否已有物料流（有则各标量走派生）。</summary>
    public bool HasFlows => Flows.Count > 0;

    /// <summary>采出量 (万t) —— 派生：各煤流吨量之和（吨量是三个体积口径间唯一守恒的量）。</summary>
    public double CoalWanT
    {
        get => HasFlows ? Math.Round(Flows.Where(z => z.IsOre).Sum(z => z.TonnageWanT), 1) : _coalWanT;
        set => _coalWanT = value;
    }

    /// <summary>剥离量 (万m³ 原位实方) —— 派生：各岩流实方之和。</summary>
    public double StripWanM3
    {
        get => HasFlows ? Math.Round(Flows.Where(z => !z.IsOre).Sum(z => z.InSituWanM3), 0) : _stripWanM3;
        set => _stripWanM3 = value;
    }

    /// <summary>生产剥采比 (m³实方/t) —— 派生：剥离实方 ÷ 采出吨量。</summary>
    public double Ratio
    {
        get => HasFlows ? (CoalWanT > 1e-6 ? Math.Round(StripWanM3 / CoalWanT, 2) : 0) : _ratio;
        set => _ratio = value;
    }

    /// <summary>内/外排二值（兼容下游图表）—— 派生：内排占容 ≥ 半数即记内排。</summary>
    public DumpMode Dump
    {
        get => HasFlows ? (InternalDumpPct >= 50 ? DumpMode.Internal : DumpMode.External) : _dump;
        set => _dump = value;
    }

    public double CumCoal { get; set; }         // 累计采出 (万t)
    public double CumStrip { get; set; }        // 累计剥离 (万m³)
    public double AdvanceM { get; set; }        // 本月推进 (m)
    public double EquipUtilPct { get; set; }    // 设备利用率 (%)
    public double CompletionPct { get; set; }   // 累计完成率（对年目标，%）
    public string ActiveFace { get; set; } = "";// 当月主作业面
    public bool IsMaintenance { get; set; }     // 检修月
    public bool IsPeak { get; set; }            // 峰值月

    /// <summary>本月排弃/库容警示（排不下、超容、去向不兼容）。空=无警示，绝不静默。</summary>
    public string Warning { get; set; } = "";

    // ── 物料流派生指标 ──

    /// <summary>本月总吨量 (万t，含煤与岩)。</summary>
    public double TonnageWanT => Math.Round(Flows.Sum(z => z.TonnageWanT), 1);

    /// <summary>本月排弃占容合计 (万m³ 占容方 = Σ V实×Kr)——排土场剩余库容按它扣。</summary>
    public double DumpedWanM3 => Math.Round(Flows.Where(z => z.IsDumping).Sum(z => z.DumpWanM3), 0);

    /// <summary>内排率 (%) = 内排占容 ÷ 全部排弃占容。露天矿降本的核心指标。</summary>
    public double InternalDumpPct
    {
        get
        {
            double all = Flows.Where(z => z.IsDumping).Sum(z => z.DumpWanM3);
            if (all <= 1e-9) return 0;
            double inner = Flows.Where(z => z.IsInternalDump).Sum(z => z.DumpWanM3);
            return Math.Round(inner / all * 100, 1);
        }
    }

    /// <summary>本月运输功 (万t·km) = Σ 吨量×等效运距。配车/成本的直接驱动量。</summary>
    public double TransportWorkWanTKm => Math.Round(Flows.Sum(z => z.TransportWorkWanTKm), 0);

    /// <summary>吨量加权平均运距 (km)——不是各流运距的简单平均（那会把小流量的远距离放大）。</summary>
    public double WeightedAvgHaulKm
    {
        get
        {
            double t = Flows.Sum(z => z.TonnageWanT);
            return t <= 1e-9 ? 0 : Math.Round(Flows.Sum(z => z.TonnageWanT * z.EffectiveHaulKm) / t, 2);
        }
    }

    public string DumpText => Dump == DumpMode.Internal ? "内排" : "外排";
    public string FlagText => IsMaintenance ? "检修" : (IsPeak ? "◆峰值" : "");
    /// <summary>量的来源（原 XAML 用 HasFlows 触发器切文案）：流派生 = 采出/剥离/剥采比/内排率由逐笔物料流聚合；份额摊分 = 由年目标按作业组织×工作历摊出来的标量。</summary>
    public string FlowSourceText => HasFlows ? "流派生" : "份额摊分";
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
}

/// <summary>短期(月度)计划求解结果（系统级指标，方向感知）。</summary>
/// <summary>三量保有校核的三态。<b>「判不了」必须与「判了但没过」分开</b>。</summary>
public enum PrepCheckState
{
    /// <summary>备采储量没有数据 —— <b>根本没校核</b>，不构成"不通过"。</summary>
    NotEntered = 0,
    /// <summary>有数据，且保有月数 ≥ 下限。</summary>
    Pass = 1,
    /// <summary>有数据，但保有月数 &lt; 下限 —— <b>这才是真的不达标</b>。</summary>
    Fail = 2,
}

public sealed class ShortTermResult
{
    public double TotalCoalWanT { get; set; }       // 全期采出合计 (万t)
    public double TotalStripWanM3 { get; set; }     // 全期剥离合计 (万m³)
    public double CompletionRatePct { get; set; }   // 年目标采出完成率 (%)，接近100好
    public double AvgRatio { get; set; }            // 平均生产剥采比 (m³/t)
    public double PeakMonthCoalWanT { get; set; }   // 峰值月采出 (万t)，越低越均衡
    public string PeakMonthLabel { get; set; } = "—";
    public double OutputCv { get; set; }            // 月产量变异系数，越低越均衡
    public double RatioCv { get; set; }             // 剥采比变异系数，越低越均衡
    public double AvgEquipUtilPct { get; set; }     // 平均设备利用率 (%)，越高越好（不超100）
    public double AdvanceTotalM { get; set; }       // 全期推进合计 (m)
    public double PreparedMonths { get; set; }      // 备采保有月数，越高越稳
    public double BalanceCoef { get; set; }         // 月产均衡系数 (0..1)，越高越均衡
    public double CompositeScore { get; set; }      // 综合得分 (0..100)
    public bool Ok { get; set; }                    // 硬约束（完成率±容差 + 月产/剥离≤上限 + 剥采比≤上限 + 备采保有≥下限）

    /// <summary>
    /// 三量保有这一项的状态。
    /// <para><b>「没录数据」和「不达标」是两件事</b>，此前被当成同一件：
    /// <c>Base.Mineable.PreparedReserveWanT</c> 全仓无人写 ⇒ 恒 0 ⇒ <c>prepOk</c> 恒 false ⇒
    /// <c>Ok</c> 恒 false。露天矿里这两种情况的处置完全相反 ——
    /// 前者是"生产技术科还没核定，先别下结论"，后者是"准备工程滞后，必须加快剥离"。</para>
    /// <para>更实的害处：因为它恒红，这个指示灯不携带任何信息，用久了必然被无视 ——
    /// 而真的备采不足恰恰要靠它报出来。</para>
    /// </summary>
    public PrepCheckState PrepState { get; set; } = PrepCheckState.NotEntered;

    /// <summary>
    /// 硬约束总判定的显示文案。
    /// <para><b>三个字各有各的意思，不许混</b>：
    /// 「通过」= 校核过且都满足；「未通过」= 校核过但有项不满足；
    /// 「未校核」= 缺数据，根本判不了。此前 false 一律显示"待校核"，
    /// 于是"剥采比超上限"和"三量没录"在界面上是同一个字。</para>
    /// </summary>
    public string OkText => PrepState == PrepCheckState.NotEntered && Ok ? "通过（三量未校核）"
                          : Ok ? "通过"
                          : "未通过";

    // ── 物料/去向维（由逐月 Flows 聚合）──
    public double TotalDumpedWanM3 { get; set; }    // 全期排弃占容 (万m³，Kr 口径)
    public double InternalDumpPct { get; set; }     // 内排率 (%)，越高越省
    public double TransportWorkWanTKm { get; set; } // 全期运输功 (万t·km)，越低越省
    public double WeightedAvgHaulKm { get; set; }   // 吨量加权平均运距 (km)
    /// <summary>去向台账来源（GeoDataBase 台账 / 内置样例）——口径可追溯。</summary>
    public string DumpSourceText { get; set; } = "";
    /// <summary>
    /// 去向/库容提示（排不下、超容、物料不兼容、内排未起转…）。不并入 <see cref="Ok"/>（那是产量类硬约束，
    /// 并入会连锁改变既有比选口径），但必须显式呈现 —— 绝不静默。带「◆」前缀的是硬警示（排不下/超容）。
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    public bool DumpOk => Warnings.Count == 0;
    /// <summary>是否存在硬警示（排不下 / 超容）。</summary>
    public bool HasHardWarning => Warnings.Any(w => w.StartsWith("◆", StringComparison.Ordinal));
    public string DumpOkText => DumpOk ? "去向·库容 通过"
        : (HasHardWarning ? $"◆去向·库容 硬警示（共 {Warnings.Count} 条）" : $"去向·库容 提示 {Warnings.Count} 条");
    public string WarningText => Warnings.Count == 0 ? "" : string.Join("\n", Warnings);
}

/// <summary>
/// 「短期(月度)生产计划方案」—— 一组完整的月度排产输入（来源年度目标 + 时间骨架 + 现场参数 +
/// 可采区域 + 作业面接续 + 均衡/约束/比选权重 + 作业组织/工作历）。配置窗口编辑它，月度计划编制求解、派生、对比。
/// </summary>
public sealed class ShortTermPlan
{
    public const double DefaultCoalDensity = MiningProgramPlan.DefaultCoalDensity; // t/m³

    public string Name { get; set; } = "月度计划1";
    public string Note { get; set; } = "";
    public bool Participate { get; set; } = true;

    // ① 来源（承接中长远进度计划某一年的年度采剥目标）
    public string SourceLongTermName { get; set; } = "";
    public LongTermPlan? SourceLongTerm { get; set; }
    public int PlanYear { get; set; } = 2027;
    public double AnnualCoalTargetWanT { get; set; } = 1000;   // 年采出目标 (万t)
    public double AnnualStripTargetWanM3 { get; set; } = 6500; // 年剥离目标 (万m³)
    public double BaseRatio => AnnualCoalTargetWanT > 1e-6 ? AnnualStripTargetWanM3 / AnnualCoalTargetWanT : 0;

    // ② 时间骨架（月度）
    public int StartMonth { get; set; } = 1;   // 起始月
    public int MonthCount { get; set; } = 12;  // 计划月数

    // ③ 现场参数 / ④ 可采区域 / ⑤ 作业面接续
    public FieldParams Field { get; set; } = new();
    public MineableArea Mineable { get; set; } = new();
    public ObservableCollection<WorkingFace> Faces { get; set; } = CreateDefaultFaces();

    // ⑥ 均衡权重
    public StBalanceWeights Balance { get; set; } = StBalanceWeights.CreateDefault();

    // ⑦ 约束（硬约束）
    public double MonthlyCoalCeilingWanT { get; set; } = 120;   // 月采出上限 (万t，0=不限)
    public double MonthlyStripCeilingWanM3 { get; set; } = 800; // 月剥离上限 (万m³，0=不限)
    public double RatioCeiling { get; set; } = 12;              // 月生产剥采比上限 (m³/t)
    public double CompletionTolerancePct { get; set; } = 3;     // 年目标完成率允许偏差 (±%)
    public double MinPreparedMonths { get; set; } = 2;          // 备采保有月数下限

    /// <summary>
    /// 按设备产能限产：作业日 × 台数 × 单台月能力 × 完好率 算出的月能力，当成月产上限夹一道。
    ///
    /// <para><b>默认关</b>。打开之后「工作历」轴（抢产/保守）才真的改变采剥量 ——
    /// 关着的时候它只是 <c>w[i]/wsum</c> 里一个自我抵消的常数因子，一吨煤都改不动
    /// （实测：三套工作历的逐月采出<b>逐位相同</b>，只有设备利用率读数不同）。</para>
    ///
    /// <para><b>为什么默认关而不是默认开</b>：缺省样例是年 1000 万t + 6500 万m³（需 603 万m³/月），
    /// 而缺省机队是 4 台 × 28 万m³ × 82% = 92 万m³/月 —— <b>两个缺省值本来就差 6.5 倍</b>。
    /// 默认开会把每套默认计划都砍到六分之一，那是把一个参数问题伪装成算法行为。
    /// 关着时改判成<b>只报不改</b>：警示里会说清"最紧那个月是能力的几倍"。</para>
    ///
    /// <para>卡住的量<b>不回摊</b> —— 回摊等于"这个月干不完就让别的月替它干"，
    /// 那正好又把工作历的影响抵消回去了。</para>
    /// </summary>
    public bool EnforceCapacityCeiling { get; set; }
    public double BenchHeightM { get; set; } = 12;              // 台阶高 H (m)
    public double WorkLineLenM { get; set; } = 1100;            // 工作线长 L (m，推进换算)

    // ── 放坡参数（三维层体用）──────────────────────────────────────────────
    //
    // 【为什么加在这里】TaskLib 的 `SimModel.LoadSlope` 用**反射按属性名**从本对象上软读
    // α / W / β（候选名依次是 BenchFaceAngleDeg… / BermWidthM… / OverallSlopeAngleDeg…）。
    // 读不到就明确降级成「层体按**垂直壁**建」并写清原因 —— 那正是
    // `采运排一体化` §十 已知边界 1 说的「**PlanLib 侧加上字段后会自动接通**」。
    // 所以这三个字段一加，三维动态模拟的真台阶放坡就通了，**TaskLib 一行不用改**。
    //
    // 0 = 未录（TaskLib 侧照旧降级为垂直壁，不猜角度）。由「逐月采剥接续」的导出契约
    // （`MinePlanExport.Geometry`）经 `MinePlanImporter` 填入，也可界面手填。

    /// <summary>台阶坡面角 α (°)。0 = 未录 → 三维层体按垂直壁建。</summary>
    public double BenchFaceAngleDeg { get; set; }
    /// <summary>安全平盘宽 W (m)。0/未录时 TaskLib 会尝试用 <c>W=H/tanβ−H/tanα</c> 由 β 反算。</summary>
    public double BermWidthM { get; set; }
    /// <summary>工作帮整体边坡角 β (°)。α 有、W 没有时靠它反算 W。</summary>
    public double OverallSlopeAngleDeg { get; set; }

    /// <summary>
    /// 放坡几何是否齐全（三维能不能建真台阶）。
    /// <para>W 缺时 TaskLib 会由 β 反算，所以 α + β 也算齐 —— 口径与
    /// <see cref="ShortTermBase.HasSlopeGeometry"/> 略有不同，这里是<b>方案侧</b>的实际可用性。</para>
    /// </summary>
    public bool HasSlopeGeometry
        => BenchFaceAngleDeg > 0 && (BermWidthM > 0 || OverallSlopeAngleDeg > 0);

    // ⑧ 比选权重
    public StDecisionWeights Decision { get; set; } = StDecisionWeights.CreateDefault();

    // 派生决策变量
    public DispatchStrategy Dispatch { get; set; } = DispatchStrategy.Balanced;
    public CalendarScenario Calendar { get; set; } = CalendarScenario.Standard;

    // 求解产物
    public ObservableCollection<MonthPeriod> Months { get; set; } = new();
    public ShortTermResult? Result { get; set; }

    // ── 展示用 ──
    public string DispatchText => Dispatch switch
    {
        DispatchStrategy.Balanced => "均衡型",
        DispatchStrategy.Concentrated => "集中强采型",
        DispatchStrategy.MultiFace => "多面展开型",
        _ => "—"
    };
    public string CalendarText => Calendar switch
    {
        CalendarScenario.Push => "抢产",
        CalendarScenario.Conservative => "保守",
        _ => "标准"
    };
    public string Caption => $"{PlanYear}年 · {DispatchText} · 工作历{CalendarText}";
    public string SolvedText => Result == null ? "未求解" : "已求解";
    public string SourceText => string.IsNullOrEmpty(SourceLongTermName) ? "（样例年度目标）" : $"{SourceLongTermName}·{PlanYear}年";

    private static ObservableCollection<WorkingFace> CreateDefaultFaces() => new()
    {
        // 采煤面：煤定去向（破碎站/原煤仓），伴生的剥离量按默认岩性构成拆流后由排产按运输功最小自动配去向。
        new WorkingFace { Name = "主采面·东", BenchElevationM = 60, SharePct = 60, AvailableReserveWanT = 600, AdvanceAzimuthDeg = 90, Order = 1,
                          MaterialCode = PlanMaterialCatalog.Coal, DestinationId = "CR-1", DestinationName = "1号破碎站", DestinationKindText = "破碎站", HaulDistanceKm = 2.6 },
        new WorkingFace { Name = "辅采面·南", BenchElevationM = 48, SharePct = 40, AvailableReserveWanT = 360, AdvanceAzimuthDeg = 180, Order = 2,
                          MaterialCode = PlanMaterialCatalog.Coal, DestinationId = "SL-1", DestinationName = "原煤仓", DestinationKindText = "原煤仓", HaulDistanceKm = 3.8 },
    };

    /// <summary>从上游中长远进度计划继承指定年度的年采剥目标 + 经济/约束基线。</summary>
    public void InheritFrom(LongTermPlan lt, int? year = null)
    {
        SourceLongTermName = lt.Name;
        SourceLongTerm = lt;
        // 选定年度：优先给定年；否则取首个达产年(设计计算年)；再否则取首个生产年。
        MonthPeriodSourceYear(lt, year);
        BenchHeightM = lt.BenchHeightM > 0 ? lt.BenchHeightM : BenchHeightM;
        WorkLineLenM = lt.WorkLine.WorkLineLenM > 0 ? lt.WorkLine.WorkLineLenM : WorkLineLenM;
        RatioCeiling = lt.EconomicStripRatioMax > 0 ? lt.EconomicStripRatioMax : RatioCeiling;
        if (Faces.Count > 0)
        {
            Faces[0].AdvanceAzimuthDeg = lt.WorkLine.AdvanceAzimuthDeg;
            AllocateFaceReserves();
        }
    }

    private void MonthPeriodSourceYear(LongTermPlan lt, int? year)
    {
        PlanPeriod? src = null;
        if (year is { } y)
            src = lt.Periods.FirstOrDefault(p => int.TryParse(p.Label, out var yy) && yy == y);
        src ??= lt.Periods.FirstOrDefault(p => p.IsDesignCalcYear)
             ?? lt.Periods.FirstOrDefault(p => p.CoalWanT > 0);
        if (src != null)
        {
            if (int.TryParse(src.Label, out var yy)) PlanYear = yy;
            AnnualCoalTargetWanT = Math.Round(src.CoalWanT, 0);
            AnnualStripTargetWanM3 = Math.Round(src.StripWanM3, 0);
        }
    }

    /// <summary>按作业面份额把备采储量分摊到各面（无来源时给样例）。</summary>
    public void AllocateFaceReserves()
    {
        double total = Mineable.PreparedReserveWanT;
        double shareSum = Faces.Sum(f => f.SharePct);
        if (shareSum <= 0 || total <= 0) return;
        foreach (var f in Faces) f.AvailableReserveWanT = Math.Round(total * f.SharePct / shareSum, 0);
    }

    public ShortTermPlan Clone()
    {
        var c = (ShortTermPlan)MemberwiseClone();
        c.Name = Name + "·副本";
        c.Field = Field.Copy();
        c.Mineable = Mineable.Copy();
        c.Faces = new ObservableCollection<WorkingFace>(Faces.Select(f => f.Copy()));
        c.Balance = Balance.Copy();
        c.Decision = Decision.Copy();
        c.SourceLongTerm = SourceLongTerm;
        c.Months = new ObservableCollection<MonthPeriod>();
        c.Result = null;
        return c;
    }

    /// <summary>骨架演示样例：3 套（不同作业组织 × 工作历）。求解链回填 Months/Result。</summary>
    public static ObservableCollection<ShortTermPlan> CreateSamples()
    {
        var a = new ShortTermPlan
        {
            Name = "月度计划A·均衡", PlanYear = 2027,
            AnnualCoalTargetWanT = 1000, AnnualStripTargetWanM3 = 6500,
            Dispatch = DispatchStrategy.Balanced, Calendar = CalendarScenario.Standard,
        };
        var b = a.Clone();
        b.Name = "月度计划B·多面展开"; b.Note = "推荐";
        b.Dispatch = DispatchStrategy.MultiFace; b.Calendar = CalendarScenario.Standard;
        var c = a.Clone();
        c.Name = "月度计划C·集中强采抢产";
        c.Dispatch = DispatchStrategy.Concentrated; c.Calendar = CalendarScenario.Push;

        var list = new ObservableCollection<ShortTermPlan> { a, b, c };
        foreach (var p in list) ShortTermScheduler.Schedule(p);
        return list;
    }
}

/// <summary>
/// 短期(月度)计划的「基础约束」（基准/盘子）—— 不随候选方案变的给定条件：来源年度目标、时间骨架、
/// 现场参数、可采区域、作业面、均衡/约束/比选权重。「短期生产计划编制」窗口只编它一套；候选方案一律由
/// 「派生计划方案」在它之上变决策变量（作业组织 × 工作历）生成。见 docs/短期生产计划_设计.md §3。
/// </summary>
public sealed class ShortTermBase
{
    public string SourceLongTermName { get; set; } = "";
    public LongTermPlan? SourceLongTerm { get; set; }
    public int PlanYear { get; set; } = 2027;
    public double AnnualCoalTargetWanT { get; set; } = 1000;
    public double AnnualStripTargetWanM3 { get; set; } = 6500;

    public int StartMonth { get; set; } = 1;
    public int MonthCount { get; set; } = 12;

    public FieldParams Field { get; set; } = new();
    public MineableArea Mineable { get; set; } = new();
    public ObservableCollection<WorkingFace> Faces { get; set; } = new()
    {
        new WorkingFace { Name = "主采面·东", BenchElevationM = 60, SharePct = 60, AvailableReserveWanT = 600, AdvanceAzimuthDeg = 90, Order = 1,
                          MaterialCode = PlanMaterialCatalog.Coal, DestinationId = "CR-1", DestinationName = "1号破碎站", DestinationKindText = "破碎站", HaulDistanceKm = 2.6 },
        new WorkingFace { Name = "辅采面·南", BenchElevationM = 48, SharePct = 40, AvailableReserveWanT = 360, AdvanceAzimuthDeg = 180, Order = 2,
                          MaterialCode = PlanMaterialCatalog.Coal, DestinationId = "SL-1", DestinationName = "原煤仓", DestinationKindText = "原煤仓", HaulDistanceKm = 3.8 },
    };

    /// <summary>
    /// 采掘单元 → 作业面的<b>手工归属覆盖</b>（单元号 → 作业面名），对应 <see cref="FaceUnitResolver"/> 的 FA1。
    ///
    /// <para><b>为什么必须有它</b>：FA5 判歧义时不配（同标高同物料摆了两个面，只有现场知道哪个是哪个），
    /// 并在提示里写着"要定就手工指定"。在这个字段出现之前，那句话<b>指向一个不存在的入口</b> ——
    /// 判据告诉人去做一件他做不了的事，比不给提示更糟。</para>
    ///
    /// <para>只存<b>人明确定过的</b>那几条，不做全量快照：全量存下来的话，采矿模型重算、
    /// 单元被重划之后，这张表会把一批已经不存在的单元号一直带着，而且没人会发现。</para>
    /// </summary>
    public Dictionary<string, string> FaceAttribution { get; set; } = new(StringComparer.Ordinal);

    public StBalanceWeights Balance { get; set; } = StBalanceWeights.CreateDefault();

    public double MonthlyCoalCeilingWanT { get; set; } = 120;
    public double MonthlyStripCeilingWanM3 { get; set; } = 800;
    public double RatioCeiling { get; set; } = 12;
    public double CompletionTolerancePct { get; set; } = 3;
    public double MinPreparedMonths { get; set; } = 2;
    /// <summary>按设备产能限产。默认关；见 <see cref="ShortTermPlan.EnforceCapacityCeiling"/>。</summary>
    public bool EnforceCapacityCeiling { get; set; }
    public double BenchHeightM { get; set; } = 12;
    public double WorkLineLenM { get; set; } = 1100;

    // ── 放坡三件套：由「采场参数识别」实测写入 ───────────────────────────────
    //  ⚠ 缺省一律 0 = 【没测过】，不给假默认值。
    //  三维那边（TaskLib SimModel.LoadSlope）读不到就退成**垂直壁**并说明；
    //  这里若给个 60° 之类的"像样"缺省，三维会一本正经地按一个没人量过的角度放坡，
    //  而画面、方量、每个数看上去都正常 —— 那比垂直壁难发现得多。
    //  此前 ShortTermBase 上【根本没有这三个字段】，所以采场参数识别量出来的 α/W/β
    //  无论怎么点都到不了三维，只有走「量驱动采剥接续」导出契约那条路的方案才有真放坡。

    /// <summary>台阶坡面角 α（度）。0 = 没测过。</summary>
    public double BenchFaceAngleDeg { get; set; }
    /// <summary>平盘宽 W（m）。0 = 没测过。</summary>
    public double BermWidthM { get; set; }
    /// <summary>工作帮整体坡角 β（度）。0 = 没测过。</summary>
    public double OverallSlopeAngleDeg { get; set; }

    /// <summary>放坡三件套是否齐全（三维能不能建真台阶）。</summary>
    public bool HasSlopeGeometry => BenchFaceAngleDeg > 0 && BermWidthM > 0;

    public StDecisionWeights Decision { get; set; } = StDecisionWeights.CreateDefault();

    public double BaseRatio => AnnualCoalTargetWanT > 1e-6 ? AnnualStripTargetWanM3 / AnnualCoalTargetWanT : 0;
    public string SourceText => string.IsNullOrEmpty(SourceLongTermName) ? "（无来源：用样例年度目标）" : $"{SourceLongTermName}·{PlanYear}年";

    /// <summary>从上游中长远进度计划继承指定年度的年采剥目标 + 约束基线。</summary>
    public void InheritFrom(LongTermPlan lt, int? year = null)
    {
        SourceLongTermName = lt.Name;
        SourceLongTerm = lt;
        PlanPeriod? src = null;
        if (year is { } y) src = lt.Periods.FirstOrDefault(p => int.TryParse(p.Label, out var yy) && yy == y);
        src ??= lt.Periods.FirstOrDefault(p => p.IsDesignCalcYear) ?? lt.Periods.FirstOrDefault(p => p.CoalWanT > 0);
        if (src != null)
        {
            if (int.TryParse(src.Label, out var yy)) PlanYear = yy;
            AnnualCoalTargetWanT = Math.Round(src.CoalWanT, 0);
            AnnualStripTargetWanM3 = Math.Round(src.StripWanM3, 0);
        }
        if (lt.BenchHeightM > 0) BenchHeightM = lt.BenchHeightM;
        if (lt.WorkLine.WorkLineLenM > 0) WorkLineLenM = lt.WorkLine.WorkLineLenM;
        if (lt.EconomicStripRatioMax > 0) RatioCeiling = lt.EconomicStripRatioMax;
    }

    /// <summary>在本基础约束之上 + 作业组织 + 工作历，造一个候选月度计划方案。</summary>
    public ShortTermPlan NewCandidate(DispatchStrategy dispatch, CalendarScenario calendar, string name)
        => new()
        {
            Name = name,
            SourceLongTermName = SourceLongTermName, SourceLongTerm = SourceLongTerm, PlanYear = PlanYear,
            AnnualCoalTargetWanT = AnnualCoalTargetWanT, AnnualStripTargetWanM3 = AnnualStripTargetWanM3,
            StartMonth = StartMonth, MonthCount = MonthCount,
            Field = Field.Copy(), Mineable = Mineable.Copy(),
            Faces = new ObservableCollection<WorkingFace>(Faces.Select(f => f.Copy())),
            Balance = Balance.Copy(),
            MonthlyCoalCeilingWanT = MonthlyCoalCeilingWanT, MonthlyStripCeilingWanM3 = MonthlyStripCeilingWanM3,
            RatioCeiling = RatioCeiling, CompletionTolerancePct = CompletionTolerancePct,
            MinPreparedMonths = MinPreparedMonths, EnforceCapacityCeiling = EnforceCapacityCeiling,
            BenchHeightM = BenchHeightM, WorkLineLenM = WorkLineLenM,
            // ★ 放坡三件套要跟着传，否则派生出来的每一套方案都是"没测过"，三维只能建垂直壁
            BenchFaceAngleDeg = BenchFaceAngleDeg, BermWidthM = BermWidthM,
            OverallSlopeAngleDeg = OverallSlopeAngleDeg,
            Decision = Decision.Copy(),
            Dispatch = dispatch, Calendar = calendar,
        };
}
