using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Dump;

// 原 BlockModelLib.Domain.StandardLevelSource.cs 的台账条目类型。StandardLevelSource.FromDatabase（读 mine_location/working_face 的那段）
// 依赖原库服务，「套设计台账」归级路径待 Kylin 数据层适配后再移；排土场走「按图上台阶线聚类」不用它。

/// <summary>库里登记的一个标准水平（平盘）+ 它的设计台阶几何。</summary>
public sealed class StandardLevelSpec
{
    /// <summary>平盘编码（<c>mine_location.location_code</c>，通常就是标高，如 "1195"）。</summary>
    public string Code = "";
    public string Name = "";

    /// <summary>标准标高（m）。</summary>
    public double ElevationM;

    /// <summary>该平盘设计台阶高（m）。取不到时由 <see cref="StandardLevelSource.Result.DefaultBenchHeightM"/> 兜。</summary>
    public double? BenchHeightM;

    /// <summary>该平盘设计坡面角（°）。决定坡面水平投影 H/tanα —— 配对闸门的设计依据。</summary>
    public double? FaceAngleDeg;

    /// <summary>安全平盘宽（m），仅作展示/体检参考。</summary>
    public double? SafetyBermM;

    /// <summary>这一级的台阶几何是从哪来的（逐平盘工作面 / 全矿中位 / 参数定义默认），供报表溯源。</summary>
    public string GeometrySource = "";

    public override string ToString() => $"{Code}({ElevationM:0.##}m)";
}
