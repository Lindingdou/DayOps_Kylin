// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CoalGradeRule.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>灰分/硫分/发热量分级规则（GB/T 15224 系列）。</summary>
[Table("coal_grade_rule")]
[ColumnDescription("煤质分级规则")]
public class CoalGradeRule
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("rule_type")]
    [ColumnDescription("规则类别: ash/sulfur/qnet/volatile")]
    public string RuleType { get; set; } = "";

    [Column("level_code")]
    [ColumnDescription("等级码: extra_low/low/medium/high/extra_high")]
    public string LevelCode { get; set; } = "";

    [Column("level_name")]
    [ColumnDescription("显示名 (\"特低灰\"/\"低硫\"...)")]
    public string LevelName { get; set; } = "";

    [Column("value_min")]
    [ColumnDescription("区间下限 (含)")]
    public double? ValueMin { get; set; }

    [Column("value_max")]
    [ColumnDescription("区间上限 (不含)")]
    public double? ValueMax { get; set; }

    [Column("color_hex")]
    [ColumnDescription("着色 #RRGGBB")]
    public string? ColorHex { get; set; }

    [Column("sort_order")]
    [ColumnDescription("排序号")]
    public int SortOrder { get; set; }
}
