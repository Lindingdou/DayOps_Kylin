// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IGeoDataContext.cs（逐行对应；仅命名空间/依赖适配）
namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 地质与设备数据访问总入口。
/// 任何其他模块通过 IPluginContext.Services.GetService&lt;IGeoDataContext&gt;() 获取。
/// </summary>
public interface IGeoDataContext
{
    IEquipmentService Equipment { get; }
    IEquipmentModelService Models { get; }
    IShiftCalendarService ShiftCalendar { get; }

    /// <summary>检修档期(V042)：装箱的有效时窗要扣它。</summary>
    IMaintenanceWindowService MaintenanceWindows { get; }

    /// <summary>穿孔作业计划(V044)：工序链「穿孔 → 爆破 → 采装」的第一环。</summary>
    IDrillPlanService DrillPlans { get; }

    IMineLocationService Locations { get; }
    IProductionService Production { get; }
    IFaultService Fault { get; }
    IKpiService Kpi { get; }
    IBlastService Blast { get; }
    IDailyMineService DailyMine { get; }
    IPlanService Plan { get; }
    IWorkforceService Workforce { get; }
    ILongTermService LongTerm { get; }
    IDispatchRuleService Dispatch { get; }

    // ─── 工艺几何 / 设计参数(V003 加入)────────────────────────
    IWorkingFaceService WorkingFaces { get; }
    IDumpSiteService DumpSites { get; }
    ISlopeDesignService Slopes { get; }
    IHaulRoadService HaulRoads { get; }

    // ─── 地质 / 钻孔 / 煤质(V005 加入)──────────────────────────
    IBoreholeService Borehole { get; }
    ICoalSeamService CoalSeam { get; }
    ICoalQualityService CoalQuality { get; }
    ICoalReferenceService CoalRef { get; }

    // ─── 补勘钻孔写实(V028 加入,「模型更新」组)──────────────────
    ISupplementaryBoreholeService SupplementaryBorehole { get; }

    // ─── 现状写实(V032 加入,「更新地质模型」组·现状面/点) ────────
    ICurrentStateService CurrentState { get; }

    // ─── 虚拟钻孔地质模型(V030 加入,「钻孔管理·虚拟钻孔」)──────────
    IVirtualDrillService VirtualDrill { get; }

    /// <summary>逐煤层台阶参数(煤的采矿模型·倾斜分层;覆盖 parameter_definition 的煤台阶默认)。</summary>
    ISeamBenchParamService SeamBenchParam { get; }

    // ─── 工艺架构(V006 加入)────────────────────────────────────
    IProcessArchitectureService ProcessArchitecture { get; }

    // ─── 模板库 + 平盘绑定(V008/V009 加入)──────────────────────
    IProcessTemplateService ProcessTemplate { get; }
    IPhaseLocationBindingService PhaseLocationBinding { get; }

    // ─── 现场验收(V011 加入)──────────────────────────────────
    IParameterAcceptanceService ParameterAcceptance { get; }

    // ─── 可采区域边界(V015 加入,短期「采场/排土场圈定」圈画) ──────
    IMineableRegionService MineableRegion { get; }

    /// <summary>V047 工序作业区：某一期某道工序在哪儿干。<b>与 mineable_region 是两张表</b>——
    /// 那张是长期存在的地（推演与路网裁剪读它），本张按期次、只给任务编制与派工。</summary>
    IProcessZoneService ProcessZones { get; }

    /// <summary>V039 潜在排土位置（排土条带网格）。</summary>
    IDumpStripService DumpStrips { get; }

    // ─── 装卸点(运输源/汇)(V017 加入,RoadLib「装卸点设置」) ──────
    ILoadUnloadPointService LoadUnloadPoints { get; }

    // ─── 路网存档(某时期道路网络图)(V019 加入,RoadLib「保存路网」/「路网存档」) ──────
    IRoadNetworkService RoadNetworks { get; }

    // ─── 中心线存档(某一版道路中心线几何)(V045 加入,RoadLib「中心线管理」) ──────────
    //  与上面成对:这条存**进料**(中线折线原样),上面存**成品**(建完网的节点+边图)。
    //  建网单向,从成品反推不回中线,所以两张都要留。
    IRoadCenterlineSetService RoadCenterlineSets { get; }

    // ─── 去向扩展档案 + 库容盘点(V035 加入,TaskLib「去向台账」写回) ──────────────
    //  dump_site / load_unload_point 放不下的去向属性(可接物料、工作线长、排弃层、
    //  兜底运距、开放时窗)与「已填」的盘点修正流水。
    ISinkProfileService SinkProfiles { get; }

    // ─── 作业面去向档案(V035 加入,TaskLib「作业面台账」写回) ──────────────────
    //  working_face 放不下的「当日怎么干」:去向、运距、日目标、混采分项。
    IWorkingFaceRoutingService WorkingFaceRoutings { get; }

    // ─── 煤质空间插值挂点 (V005 加入，默认 IDW 兜底，等系统级 OK/Co-Kriging 接入)
    IInterpolationServiceHook Interpolation { get; }
}
