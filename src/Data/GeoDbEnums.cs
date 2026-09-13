// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Enums.cs（逐行对应；仅命名空间适配）
namespace PitMine3D.Kylin.Data;

/// <summary>设备类别。</summary>
public enum EquipmentCategory
{
    Shovel,       // 电铲
    Truck,        // 卡车
    Drill,        // 钻机
    Loader,       // 装载机
    Dozer,        // 推土机
    Grader,       // 平路机
    WaterTruck,   // 洒水车
    Other
}

/// <summary>设备状态。</summary>
public enum EquipmentStatus
{
    InUse,         // 在用
    Maintenance,   // 检修
    Idle,          // 闲置
    Scrapped       // 报废
}

/// <summary>班次。</summary>
public enum Shift
{
    A,    // 早班 07:30~15:30
    B,    // 中班 15:30~23:30
    C     // 夜班 23:30~07:30
}

/// <summary>故障类型。</summary>
public enum FaultType
{
    Electrical,        // 电气
    Mechanical,        // 机械
    Hydraulic,         // 液压
    Tire,              // 轮胎
    Control,           // 电控
    BlastAvoidance,    // 避炮
    RainAvoidance,     // 避雨
    Maintenance,       // 计划维修
    Other              // 其他
}

/// <summary>所属队组。</summary>
public enum TeamCode
{
    Team1,        // 生产一队
    Team2,        // 生产二队
    Outsource     // 外委队
}

/// <summary>长周期指标的数据源。</summary>
public enum MetricSource
{
    Production,   // 产量(production_history)
    Pit,          // 矿坑(pit_history)
    Energy        // 能耗(energy_consumption)
}
