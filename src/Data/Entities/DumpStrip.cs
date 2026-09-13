
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
public class DumpStrip
{
    // column: id
    public long Id { get; set; }

    // column: region_id
    public long RegionId { get; set; }

    // column: region_name
    public string RegionName { get; set; } = "";

    // column: category
    public string Category { get; set; } = "external_dump";

    // column: code
    public string Code { get; set; } = "";

    // column: level_index
    public long LevelIndex { get; set; }

    // column: panel_index
    public long PanelIndex { get; set; }

    // column: panel_count
    public long PanelCount { get; set; } = 1;

    // column: step_index
    public long StepIndex { get; set; }

    // column: sub_index
    public long SubIndex { get; set; }

    // column: sub_count
    public long SubCount { get; set; } = 1;

    // column: crest_z
    public double CrestZ { get; set; }

    // column: toe_z
    public double ToeZ { get; set; }

    // column: bench_height_m
    public double BenchHeightM { get; set; }

    // column: strike_len_m
    public double StrikeLenM { get; set; }

    // column: strip_width_m
    public double StripWidthM { get; set; }

    // column: capacity_m3
    public double CapacityM3 { get; set; }

    // column: centroid_x
    public double CentroidX { get; set; }

    // column: centroid_y
    public double CentroidY { get; set; }

    // column: centroid_z
    public double CentroidZ { get; set; }

    // column: crest_json
    public string CrestJson { get; set; } = "[]";

    // column: toe_json
    public string ToeJson { get; set; } = "[]";

    // column: entity_handle
    public long EntityHandle { get; set; }

    // column: design_version
    public string? DesignVersion { get; set; }

    // column: notes
    public string? Notes { get; set; }

    // created_at / updated_at 不映射:交给 DB 的 DEFAULT + AFTER UPDATE 触发器,
    // 避免显式传 NULL 撞 NOT NULL(与 MineableRegion 同口径)。
}
