// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/TemplateParamValue.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>模板下的具体参数值。</summary>
[Table("template_param_value")]
[ColumnDescription("模板参数值")]
public class TemplateParamValue
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("template_id")]
    public long TemplateId { get; set; }

    [Column("param_id")]
    public long ParamId { get; set; }

    [Column("recommended_value")]
    [ColumnDescription("本模板推荐值")]
    public double? RecommendedValue { get; set; }

    [Column("min_value")]
    public double? MinValue { get; set; }

    [Column("max_value")]
    public double? MaxValue { get; set; }

    [Column("text_value")]
    public string? TextValue { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }
}
