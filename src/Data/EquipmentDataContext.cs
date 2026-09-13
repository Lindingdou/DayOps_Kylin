// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/EquipmentDataContext.cs（逐行对应；仅命名空间/依赖适配）
using System;
using PitMine3D.Kylin.Data.Services;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 静态门面:GeoDataBase 内部 7 个窗口通过本类访问 IGeoDataContext,
/// 避免每个窗口都要拿 IPluginContext。
/// </summary>
public static class EquipmentDataContext
{
    private static IGeoDataContext? _ctx;

    /// <summary>由 GeoDataBasePlugin.Initialize 调用。</summary>
    public static void Initialize(IGeoDataContext ctx) => _ctx = ctx;

    /// <summary>获取当前上下文。未初始化时抛异常。</summary>
    public static IGeoDataContext Current => _ctx
        ?? throw new InvalidOperationException(
            "EquipmentDataContext 未初始化。请确认 GeoDataBasePlugin 已 Initialize。");

    /// <summary>便捷别名。</summary>
    public static IEquipmentService Equipment => Current.Equipment;
    public static IEquipmentModelService Models => Current.Models;
    public static IShiftCalendarService ShiftCalendar => Current.ShiftCalendar;
    public static IMaintenanceWindowService MaintenanceWindows => Current.MaintenanceWindows;
    public static IDrillPlanService DrillPlans => Current.DrillPlans;
    public static IMineLocationService Locations => Current.Locations;
    public static IProductionService Production => Current.Production;
    public static IFaultService Fault => Current.Fault;
    public static IKpiService Kpi => Current.Kpi;
    public static IBlastService Blast => Current.Blast;
    public static IDailyMineService DailyMine => Current.DailyMine;
    public static IPlanService Plan => Current.Plan;
    public static IWorkforceService Workforce => Current.Workforce;
    public static ILongTermService LongTerm => Current.LongTerm;
    public static IDispatchRuleService Dispatch => Current.Dispatch;
    public static IWorkingFaceService WorkingFaces => Current.WorkingFaces;
    public static IDumpSiteService DumpSites => Current.DumpSites;
    public static ISlopeDesignService Slopes => Current.Slopes;
    public static IHaulRoadService HaulRoads => Current.HaulRoads;

    // V005 地质模块
    public static IBoreholeService Borehole => Current.Borehole;
    public static ICoalSeamService CoalSeam => Current.CoalSeam;
    public static ICoalQualityService CoalQuality => Current.CoalQuality;
    public static ICoalReferenceService CoalRef => Current.CoalRef;
    public static ISupplementaryBoreholeService SupplementaryBorehole => Current.SupplementaryBorehole;
    public static ICurrentStateService CurrentState => Current.CurrentState;

    // V030 虚拟钻孔地质模型(地表 + 煤层顶底板三角网)
    public static IVirtualDrillService VirtualDrill => Current.VirtualDrill;
    public static ISeamBenchParamService SeamBenchParam => Current.SeamBenchParam;

    // V006 工艺架构
    public static IProcessArchitectureService ProcessArchitecture => Current.ProcessArchitecture;

    // V008/V009 模板库 + 平盘绑定
    public static IProcessTemplateService ProcessTemplate => Current.ProcessTemplate;
    public static IPhaseLocationBindingService PhaseLocationBinding => Current.PhaseLocationBinding;

    // V011 现场验收
    public static IParameterAcceptanceService ParameterAcceptance => Current.ParameterAcceptance;

    // V015 可采区域边界
    public static IMineableRegionService MineableRegion => Current.MineableRegion;

    // V047 工序作业区(按期次;不参与推演推进极性)
    public static IProcessZoneService ProcessZones => Current.ProcessZones;

    // V039 潜在排土位置(排土条带网格)
    public static IDumpStripService DumpStrips => Current.DumpStrips;

    // V017 装卸点(运输源/汇)
    public static ILoadUnloadPointService LoadUnloadPoints => Current.LoadUnloadPoints;

    // V019 路网存档(某时期道路网络图)
    public static IRoadNetworkService RoadNetworks => Current.RoadNetworks;

    // V045 中心线存档(建网的进料:中线几何原样)
    public static IRoadCenterlineSetService RoadCenterlineSets => Current.RoadCenterlineSets;

    // V035 去向扩展档案 + 库容盘点(TaskLib「去向台账」写回)
    public static ISinkProfileService SinkProfiles => Current.SinkProfiles;

    // V035 作业面去向档案(TaskLib「作业面台账」写回)
    public static IWorkingFaceRoutingService WorkingFaceRoutings => Current.WorkingFaceRoutings;

    // V005 煤质空间插值
    public static IInterpolationServiceHook Interpolation => Current.Interpolation;
}
