// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/DumpSite.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>排土场台账。</summary>
[Table("dump_site")]
[ColumnDescription("排土场")]
public class DumpSite
{
    [Column("dump_id"), PrimaryKey]
    [ColumnDescription("排土场编号")]
    public string DumpId { get; set; } = "";

    [Column("name")]
    [ColumnDescription("名称")]
    public string Name { get; set; } = "";

    [Column("dump_type")]
    [ColumnDescription("内排 internal / 外排 external")]
    public string DumpType { get; set; } = "external";

    [Column("design_capacity_wan_m3")]
    [ColumnDescription("设计容量(万 m³)")]
    public double DesignCapacityWanM3 { get; set; }

    [Column("current_filled_wan_m3")]
    [ColumnDescription("已堆容量(万 m³)")]
    public double CurrentFilledWanM3 { get; set; }

    [Column("max_height_m")]
    [ColumnDescription("最大堆高(m)")]
    public double? MaxHeightM { get; set; }

    [Column("bench_height_m")]
    [ColumnDescription("单层堆高(m)")]
    public double? BenchHeightM { get; set; }

    [Column("overall_slope_angle_deg")]
    [ColumnDescription("整体坡角(°)")]
    public double? OverallSlopeAngleDeg { get; set; }

    [Column("bench_slope_angle_deg")]
    [ColumnDescription("台阶坡角(°)")]
    public double? BenchSlopeAngleDeg { get; set; }

    [Column("service_years_remaining")]
    [ColumnDescription("剩余服务年限(年)")]
    public double? ServiceYearsRemaining { get; set; }

    [Column("start_date")]
    [ColumnDescription("启用日期")]
    public DateTime? StartDate { get; set; }

    [Column("close_date")]
    [ColumnDescription("关闭日期(预计)")]
    public DateTime? CloseDate { get; set; }

    /// <summary>
    /// 对应的作业区域(<c>mineable_region.id</c>);<b>NULL = 还没挂上</b>(V046)。
    ///
    /// <para>排土场在库里有两个身份:几何侧的 <c>mineable_region</c>(圈定落的,带 points_json,
    /// 【排土条带】用它)和台账侧的本表(容量/堆高/坡角,【逐月排土配对】用它)。这一列是它们之间
    /// 唯一的桥 —— 不挂上,"条带算出的几何库容"和"台账里的设计容量"连是不是同一个场都判不了。</para>
    ///
    /// <para><b>没挂上时不许猜</b>(比如按名字模糊匹配):猜错一个场,对账的两个数分别属于两个
    /// 排土场,而算出来的差额看着完全正常。没挂上就如实报没挂上。</para>
    /// </summary>
    [Column("region_id")]
    [ColumnDescription("对应作业区域 mineable_region.id(NULL=未挂)")]
    public long? RegionId { get; set; }

    [Column("responsible_dozer_id")]
    [ColumnDescription("主推土机编号")]
    public string? ResponsibleDozerId { get; set; }

    [Column("status")]
    [ColumnDescription("状态 active/full/closed")]
    public string Status { get; set; } = "active";

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>充填率(派生)。</summary>
    [NotMapped]
    public double FillRate => DesignCapacityWanM3 <= 0 ? 0 : CurrentFilledWanM3 / DesignCapacityWanM3;
}
