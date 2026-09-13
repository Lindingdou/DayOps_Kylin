using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 装卸点(开拓运输系统的源/汇)跨模块共享数据。
///
/// 单一数据源(见 docs/开拓运输系统_窗体设计.md 决策4):W1「装卸点设置」(RoadLib)写、
/// W3「运量驱动布线」(MineAssLib)读，经 <see cref="PitMine.Platform.IUserSettings"/>
/// 全局持久化(key <see cref="SettingsKey"/>)跨越模块边界——RoadLib 与 MineAssLib 互不引用，
/// 故源/汇定义放 Platform 这一两侧都引用的程序集。
/// 本轮只承载图算法/选线所需的最小字段；卸载子类/产量/接收能力等求解器富字段随波4补。
/// </summary>
public sealed class LoadUnloadPoint
{
    public string Id { get; set; } = "";
    /// <summary>true=采剥点(源·Loading)，false=卸载点(汇·Unloading)。</summary>
    public bool IsLoading { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    /// <summary>吞吐能力 t/h。</summary>
    public double ThroughputTph { get; set; }
}

/// <summary>装卸点集合(持久化容器)。</summary>
public sealed class LoadUnloadPointSet
{
    /// <summary>IUserSettings 持久化键。</summary>
    public const string SettingsKey = "transport.loadunload";

    public List<LoadUnloadPoint> Points { get; set; } = new();
}
