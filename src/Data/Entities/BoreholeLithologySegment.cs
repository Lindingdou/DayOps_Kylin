// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/BoreholeLithologySegment.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>钻孔岩性分层段（柱状图核心数据，可由附表2 推断或人工录入）。</summary>
[Table("borehole_lithology_segment")]
[ColumnDescription("钻孔岩性分层段")]
public class BoreholeLithologySegment
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("borehole_id")]
    [ColumnDescription("FK borehole.id")]
    public long BoreholeId { get; set; }

    [Column("depth_from")]
    [ColumnDescription("段顶深度 (m)")]
    public double DepthFrom { get; set; }

    [Column("depth_to")]
    [ColumnDescription("段底深度 (m)")]
    public double DepthTo { get; set; }

    [Column("lithology_code")]
    [ColumnDescription("岩性代号 (sand/silt/mud/coal/limestone/...)")]
    public string LithologyCode { get; set; } = "";

    [Column("lithology_name")]
    [ColumnDescription("岩性名 (粗砂岩/细砂岩/泥岩/煤/...)")]
    public string? LithologyName { get; set; }

    [Column("color_hex")]
    [ColumnDescription("绘图颜色 #RRGGBB")]
    public string? ColorHex { get; set; }

    [Column("pattern")]
    [ColumnDescription("SVG pattern 标识 (用于柱状图填充)")]
    public string? Pattern { get; set; }

    [Column("description")]
    [ColumnDescription("分层描述")]
    public string? Description { get; set; }

    [Column("source")]
    [ColumnDescription("来源 (附表2推断/外部录入/人工)")]
    public string Source { get; set; } = "推断";

    [Column("sort_order")]
    [ColumnDescription("同孔段内排序 (按深度递增)")]
    public int SortOrder { get; set; }
}
