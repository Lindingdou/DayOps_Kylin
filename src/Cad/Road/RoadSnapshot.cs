// 忠实移植自原 PitMine3D Modules/RoadLib/Network/RoadSnapshot.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 时段快照：一期路网状态的「命名 + 冻结拷贝」。
/// 快照体系与会话路网解耦——快照只持有一份图的深拷贝和一个用户可编辑的名称，
/// 不随会话图后续编辑而改变；需要时可「转为当前路网」把这份拷贝回灌成会话图。
/// </summary>
public sealed class RoadSnapshot
{
    /// <summary>用户可编辑的名称（开采期号 / 时间标签等）。空则回退占位名。</summary>
    public string Name { get; set; }

    /// <summary>这一期被冻结的路网图（深拷贝，独立于会话图）。</summary>
    public RoadGraph Graph { get; }

    public RoadSnapshot(string name, RoadGraph graph)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Name = string.IsNullOrWhiteSpace(name) ? "未命名快照" : name.Trim();
    }
}
