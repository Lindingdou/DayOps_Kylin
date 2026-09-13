// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/DumpStrip.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 潜在排土位置 — 排土台阶壳子按「分割长度 × 排土条带宽度」切出的一格。
///
/// 一个位置 = 一级排土台阶 × 沿走向一幅 × 沿推进方向一带。
/// 它是排产/寻径的最小可调度单元：有质心（算运距）、有库容（够不够排）、有台阶级与带号（排土次序）。
///
/// 【库容口径】<see cref="CapacityM3"/> 是【占容方 V容】= 走向长 × 条带宽 × 台阶高，
/// 即这个位置在排土场里实际占掉的空间。它能承接多少采场剥离【实方】= V容 / Kr
/// （Kr = 残余膨胀系数，随物料走）。表里只存几何量，换算留给读的人 ——
/// 把物料口径焊进几何表，换个物料就得改表。
/// </summary>
[Table("dump_strip")]
[ColumnDescription("潜在排土位置(排土条带网格)")]
public class DumpStrip
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("region_id")]
    [ColumnDescription("所属排土场 → mineable_region.id")]
    public long RegionId { get; set; }

    [Column("region_name")]
    [ColumnDescription("排土场名称(冗余,清单直接可读)")]
    public string RegionName { get; set; } = "";

    [Column("category")]
    [ColumnDescription("external_dump 外排 / internal_dump 内排")]
    public string Category { get; set; } = "external_dump";

    [Column("code")]
    [ColumnDescription("位置编号 外排1-L3-P02-S05")]
    public string Code { get; set; } = "";

    [Column("level_index")]
    [ColumnDescription("台阶级序(1 = 最上一级)")]
    public long LevelIndex { get; set; }

    [Column("panel_index")]
    [ColumnDescription("沿走向第几幅(1 起)")]
    public long PanelIndex { get; set; }

    [Column("panel_count")]
    [ColumnDescription("本级共几幅")]
    public long PanelCount { get; set; } = 1;

    [Column("step_index")]
    [ColumnDescription("沿推进方向第几带(1 = 当前排土线那一带)")]
    public long StepIndex { get; set; }

    [Column("sub_index")]
    [ColumnDescription("带内子号(1 起;0 = 该带没切)")]
    public long SubIndex { get; set; }

    [Column("sub_count")]
    [ColumnDescription("本带共切成几个位置")]
    public long SubCount { get; set; } = 1;

    [Column("crest_z")]
    [ColumnDescription("坡顶标高(m)")]
    public double CrestZ { get; set; }

    [Column("toe_z")]
    [ColumnDescription("坡底标高(m)")]
    public double ToeZ { get; set; }

    [Column("bench_height_m")]
    [ColumnDescription("台阶高(m)")]
    public double BenchHeightM { get; set; }

    [Column("strike_len_m")]
    [ColumnDescription("走向长(m,水平)")]
    public double StrikeLenM { get; set; }

    [Column("strip_width_m")]
    [ColumnDescription("排土条带宽度 W(m,推进方向)")]
    public double StripWidthM { get; set; }

    [Column("capacity_m3")]
    [ColumnDescription("库容(m³,占容方)= 走向长 × W × 台阶高")]
    public double CapacityM3 { get; set; }

    [Column("centroid_x")]
    [ColumnDescription("质心 X(寻径算运距用)")]
    public double CentroidX { get; set; }

    [Column("centroid_y")]
    [ColumnDescription("质心 Y")]
    public double CentroidY { get; set; }

    [Column("centroid_z")]
    [ColumnDescription("质心 Z")]
    public double CentroidZ { get; set; }

    [Column("crest_json")]
    [ColumnDescription("前脸坡顶轨(扁平 xyz JSON)")]
    public string CrestJson { get; set; } = "[]";

    [Column("toe_json")]
    [ColumnDescription("前脸坡底轨(扁平 xyz JSON)")]
    public string ToeJson { get; set; } = "[]";

    [Column("entity_handle")]
    [ColumnDescription("图上壳子体 handle(0 = 还没建体)")]
    public long EntityHandle { get; set; }

    [Column("design_version")]
    [ColumnDescription("方案版本(多方案比选)")]
    public string? DesignVersion { get; set; }

    [Column("notes")]
    [ColumnDescription("备注")]
    public string? Notes { get; set; }

    // created_at / updated_at 不映射:交给 DB 的 DEFAULT + AFTER UPDATE 触发器,
    // 避免显式传 NULL 撞 NOT NULL(与 MineableRegion 同口径)。
}
