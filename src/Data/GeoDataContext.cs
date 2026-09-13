// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/GeoDataContext.cs（逐行对应；仅命名空间/依赖适配）
using PitMine3D.Kylin.Data.Services;

using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// IGeoDataContext 实现:聚合 21 个领域服务,共享同一 ISqlService。
/// V005 加入: Borehole / CoalSeam / CoalQuality / CoalRef
/// </summary>
internal sealed class GeoDataContext : IGeoDataContext
{
    public IEquipmentService Equipment { get; }
    public IEquipmentModelService Models { get; }
    public IShiftCalendarService ShiftCalendar { get; }
    public IMaintenanceWindowService MaintenanceWindows { get; }
    public IDrillPlanService DrillPlans { get; }
    public IMineLocationService Locations { get; }
    public IProductionService Production { get; }
    public IFaultService Fault { get; }
    public IKpiService Kpi { get; }
    public IBlastService Blast { get; }
    public IDailyMineService DailyMine { get; }
    public IPlanService Plan { get; }
    public IWorkforceService Workforce { get; }
    public ILongTermService LongTerm { get; }
    public IDispatchRuleService Dispatch { get; }
    public IWorkingFaceService WorkingFaces { get; }
    public IDumpSiteService DumpSites { get; }
    public ISlopeDesignService Slopes { get; }
    public IHaulRoadService HaulRoads { get; }

    // V005 地质模块
    public IBoreholeService Borehole { get; }
    public ICoalSeamService CoalSeam { get; }
    public ICoalQualityService CoalQuality { get; }
    public ICoalReferenceService CoalRef { get; }

    // V028 补勘钻孔写实(「模型更新」组)
    public ISupplementaryBoreholeService SupplementaryBorehole { get; }

    // V032 现状写实(「更新地质模型」组)
    public ICurrentStateService CurrentState { get; }

    // V030 虚拟钻孔地质模型(「钻孔管理·虚拟钻孔」)
    public IVirtualDrillService VirtualDrill { get; }
    public ISeamBenchParamService SeamBenchParam { get; }

    // V006 工艺架构
    public IProcessArchitectureService ProcessArchitecture { get; }

    // V008/V009 模板库 + 平盘绑定
    public IProcessTemplateService ProcessTemplate { get; }
    public IPhaseLocationBindingService PhaseLocationBinding { get; }

    // V011 现场验收
    public IParameterAcceptanceService ParameterAcceptance { get; }

    // V015 可采区域边界
    public IMineableRegionService MineableRegion { get; }
    public IProcessZoneService ProcessZones { get; }

    // V039 潜在排土位置(排土条带网格)
    public IDumpStripService DumpStrips { get; }

    // V017 装卸点(运输源/汇)
    public ILoadUnloadPointService LoadUnloadPoints { get; }

    // V019 路网存档(某时期道路网络图)
    public IRoadNetworkService RoadNetworks { get; }

    // V045 中心线存档(某一版道路中心线几何)
    public IRoadCenterlineSetService RoadCenterlineSets { get; }

    // V035 去向扩展档案 + 库容盘点(TaskLib「去向台账」写回)
    public ISinkProfileService SinkProfiles { get; }

    // V035 作业面去向档案(TaskLib「作业面台账」写回)
    public IWorkingFaceRoutingService WorkingFaceRoutings { get; }

    // V005 煤质空间插值挂点
    public IInterpolationServiceHook Interpolation { get; }

    public GeoDataContext(ISqlService sql)
    {
        Equipment     = new EquipmentService(sql);
        Models        = new EquipmentModelService(sql);
        ShiftCalendar = new ShiftCalendarService(sql);
        MaintenanceWindows = new MaintenanceWindowService(sql);
        DrillPlans    = new DrillPlanService(sql);
        Locations     = new MineLocationService(sql);
        Production    = new ProductionService(sql);
        Fault         = new FaultService(sql);
        Kpi           = new KpiService(sql);
        Blast         = new BlastService(sql);
        DailyMine     = new DailyMineService(sql);
        Plan          = new PlanService(sql);
        Workforce     = new WorkforceService(sql);
        LongTerm      = new LongTermService(sql);
        Dispatch      = new DispatchRuleService(sql);
        WorkingFaces  = new WorkingFaceService(sql);
        DumpSites     = new DumpSiteService(sql);
        Slopes        = new SlopeDesignService(sql);
        HaulRoads     = new HaulRoadService(sql);

        // V005 地质模块 (注意 CoalQuality 依赖 CoalRef，需先构造)
        CoalRef       = new CoalReferenceService(sql);
        Borehole      = new BoreholeService(sql);
        CoalSeam      = new CoalSeamService(sql);
        CoalQuality   = new CoalQualityService(sql, CoalRef);

        // V028 补勘钻孔写实(「模型更新」组)
        SupplementaryBorehole = new SupplementaryBoreholeService(sql);

        // V032 现状写实(「更新地质模型」组)
        CurrentState = new CurrentStateService(sql);

        // V030 虚拟钻孔地质模型(地表 + 煤层顶底板三角网持久化)
        VirtualDrill = new VirtualDrillService(sql);
        SeamBenchParam = new SeamBenchParamService(sql);

        // V006 工艺架构
        ProcessArchitecture = new ProcessArchitectureService(sql);

        // V008/V009 模板库 + 平盘绑定
        ProcessTemplate = new ProcessTemplateService(sql);
        PhaseLocationBinding = new PhaseLocationBindingService(sql);

        // V011 现场验收
        ParameterAcceptance = new ParameterAcceptanceService(sql);

        // V015 可采区域边界
        MineableRegion = new MineableRegionService(sql);
        ProcessZones = new ProcessZoneService(sql);

        // V039 潜在排土位置(排土条带网格)
        DumpStrips = new DumpStripService(sql);

        // V017 装卸点(运输源/汇)
        LoadUnloadPoints = new LoadUnloadPointService(sql);

        // V019 路网存档(某时期道路网络图)
        RoadNetworks = new RoadNetworkService(sql);

        // V045 中心线存档(建网的进料:中线几何原样)
        RoadCenterlineSets = new RoadCenterlineSetService(sql);

        // V035 去向扩展档案 + 库容盘点 / 作业面去向档案(TaskLib 两个台账的写回落点)
        SinkProfiles = new SinkProfileService(sql);
        WorkingFaceRoutings = new WorkingFaceRoutingService(sql);

        // V005 煤质空间插值挂点（默认 IDW 兜底）
        Interpolation = new CoalQualityEstimator();
    }
}
