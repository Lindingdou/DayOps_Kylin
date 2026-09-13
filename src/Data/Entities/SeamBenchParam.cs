// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/SeamBenchParam.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 逐煤层台阶参数（煤的采矿模型·倾斜分层）。
///
/// 这是【覆盖表】，不是另一套指标：取值区间 / 报警上下限 / 全矿默认值仍归
/// <c>parameter_definition</c> 的 V034 三项（<c>coal_bench_height</c> 2007 /
/// <c>coal_bench_slope_angle</c> 2008 / <c>coal_platform_width</c> 2009）。
/// 本表只记「某层煤要偏离全矿默认」的那几项，字段为 null = 不覆盖。
/// 读取顺序：本表 → parameter_definition 默认 → 代码兜底。
///
/// 为什么要逐煤层：各层厚度、顶底板岩性、可采下限都不同，4 号煤和 11 号煤没道理共用一个台阶高；
/// 而 V034 那套是挂在 phase_id=201 上的全矿值，表达不了层间差异。
///
/// <see cref="SeamCode"/> → <c>coal_seam_def.code</c>，与 <c>virtual_drill_surface.seam_name</c>
/// 同一套取值（4 / 9 / 11），所以参数分组能直接对上地质模型里的顶底板面。
/// </summary>
[Table("seam_bench_param")]
[ColumnDescription("逐煤层台阶参数(覆盖全矿默认)")]
public class SeamBenchParam
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>煤层编号 → <c>coal_seam_def.code</c>（4 / 9 / 11 ...）。</summary>
    [Column("seam_code")]
    [ColumnDescription("煤层编号")]
    public string SeamCode { get; set; } = "";

    // ── 覆盖 parameter_definition 的煤台阶三项（null = 回落全矿默认）──

    /// <summary>台阶高（m）。null = 用 <c>coal_bench_height</c> 默认（V034: 15，区间 5~15）。</summary>
    [Column("bench_height_m")]
    [ColumnDescription("台阶高(m,null=全矿默认)")]
    public double? BenchHeightM { get; set; }

    /// <summary>坡面角（°）。null = 用 <c>coal_bench_slope_angle</c> 默认（V034: 65，区间 60~70）。</summary>
    [Column("bench_slope_angle_deg")]
    [ColumnDescription("坡面角(°,null=全矿默认)")]
    public double? BenchSlopeAngleDeg { get; set; }

    /// <summary>平盘宽（m）。null = 用 <c>coal_platform_width</c> 默认（V034: 6，区间 6~12）。</summary>
    [Column("berm_width_m")]
    [ColumnDescription("平盘宽(m,null=全矿默认)")]
    public double? BermWidthM { get; set; }

    // ── 煤的采矿模型专有（parameter_definition 里没有对应项）──

    /// <summary>采掘带宽度 W（m）= 往高墙里推进的水平深度。</summary>
    [Column("strip_width_m")]
    [ColumnDescription("采掘带宽度W(m)")]
    public double? StripWidthM { get; set; }

    /// <summary>分层方式：<c>inclined</c> 倾斜分层（跟煤层倾向）/ <c>horizontal</c> 顶底水平。</summary>
    [Column("layering_mode")]
    [ColumnDescription("分层方式 inclined/horizontal")]
    public string LayeringMode { get; set; } = "inclined";

    /// <summary>最小可采厚（m）：该层煤薄于此的地方不出煤体。</summary>
    [Column("min_mineable_thick_m")]
    [ColumnDescription("最小可采厚(m)")]
    public double? MinMineableThickM { get; set; }

    /// <summary>
    /// 分层基准：<c>floor</c> 从底板起算往上切（默认，煤采到底板为止，从底往上分才贴实际）/
    /// <c>roof</c> 从顶板起算往下切。
    /// </summary>
    [Column("datum")]
    [ColumnDescription("分层基准 floor/roof")]
    public string Datum { get; set; } = "floor";

    /// <summary>方案版本；同一层煤可存多套做比选，空 = 默认方案。</summary>
    [Column("design_version")]
    [ColumnDescription("方案版本")]
    public string? DesignVersion { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>是否参与建模（同一层煤多方案时只有一套为 1）。</summary>
    [Column("is_active")]
    [ColumnDescription("是否启用")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public string? CreatedAt { get; set; }

    [Column("updated_at")]
    public string? UpdatedAt { get; set; }

    /// <summary>是否从底板起算分层。</summary>
    public bool IsFloorDatum => !string.Equals(Datum, "roof", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>是否倾斜分层（跟煤层倾向）。</summary>
    public bool IsInclined => !string.Equals(LayeringMode, "horizontal", System.StringComparison.OrdinalIgnoreCase);

    public override string ToString()
        => $"{SeamCode} 煤：H={BenchHeightM?.ToString("0.##") ?? "默认"}m "
         + $"α={BenchSlopeAngleDeg?.ToString("0.#") ?? "默认"}° "
         + $"W={StripWidthM?.ToString("0.#") ?? "-"}m {(IsInclined ? "倾斜分层" : "顶底水平")}";
}
