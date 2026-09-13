// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CoalClassification.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>GB/T 5751 煤类字典（焦煤/气煤/长焰煤等）。</summary>
[Table("coal_classification")]
[ColumnDescription("GB/T 5751 煤类字典")]
public class CoalClassification
{
    [Column("code"), PrimaryKey]
    [ColumnDescription("煤类代号 (JM/QM/CY/WY1/...)")]
    public string Code { get; set; } = "";

    [Column("name_cn")]
    [ColumnDescription("中文名 (焦煤/气煤/长焰煤/...)")]
    public string NameCn { get; set; } = "";

    [Column("name_short")]
    [ColumnDescription("短名")]
    public string? NameShort { get; set; }

    [Column("vdaf_min")]
    [ColumnDescription("V_daf 下限 %")]
    public double? VdafMin { get; set; }

    [Column("vdaf_max")]
    [ColumnDescription("V_daf 上限 %")]
    public double? VdafMax { get; set; }

    [Column("g_min")]
    [ColumnDescription("粘结指数 G 下限")]
    public double? GMin { get; set; }

    [Column("g_max")]
    [ColumnDescription("粘结指数 G 上限")]
    public double? GMax { get; set; }

    [Column("y_min")]
    [ColumnDescription("胶质层 Y 下限 mm")]
    public double? YMin { get; set; }

    [Column("y_max")]
    [ColumnDescription("胶质层 Y 上限 mm")]
    public double? YMax { get; set; }

    [Column("sort_order")]
    [ColumnDescription("排序号")]
    public int SortOrder { get; set; }

    [Column("description")]
    [ColumnDescription("说明")]
    public string? Description { get; set; }
}
