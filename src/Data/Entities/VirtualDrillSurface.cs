// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/VirtualDrillSurface.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 虚拟钻孔地质模型的一张已捕获三角网面(地表 / 某煤层顶板 / 某煤层底板)。
///
/// 首次配置时由场景中选定的三角网实体读出:节点(顶点 xyz)与连接顺序(三角索引)打包成
/// 二进制后 base64 存 <see cref="GeometryB64"/>(opaque)。虚拟钻孔求交时从这里取几何。
/// 顶/底板按 <see cref="SeamName"/> 配对成一层煤;<see cref="Role"/> 区分角色。
/// </summary>
[Table("virtual_drill_surface")]
[ColumnDescription("虚拟钻孔地质模型面(地表/煤层顶底板三角网 + 节点连接)")]
public class VirtualDrillSurface
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    /// <summary>角色:'surface' 地表 | 'roof' 煤层顶板 | 'floor' 煤层底板。</summary>
    [Column("role")]
    [ColumnDescription("面角色:surface/roof/floor")]
    public string Role { get; set; } = "roof";

    /// <summary>煤层编号(地表为空;顶/底板填煤层号,如 4 / 7-1 / 9 / 11)。顶+底同名 = 一层煤。</summary>
    [Column("seam_name")]
    [ColumnDescription("煤层编号(地表为空)")]
    public string SeamName { get; set; } = "";

    /// <summary>地质排序(自上而下递增,0 在最上)。决定柱状排列与层间距。</summary>
    [Column("seam_order")]
    [ColumnDescription("地质排序(自上而下递增)")]
    public int SeamOrder { get; set; }

    /// <summary>层位显示色 #RRGGBB(可配置)。顶/底同煤层共用一色。</summary>
    [Column("color_hex")]
    [ColumnDescription("层位显示色 #RRGGBB")]
    public string ColorHex { get; set; } = "#3C3C3C";

    /// <summary>来源:原场景三角网图层名(溯源用)。</summary>
    [Column("source_layer")]
    [ColumnDescription("来源图层名")]
    public string SourceLayer { get; set; } = "";

    [Column("vertex_count")]
    [ColumnDescription("顶点数")]
    public int VertexCount { get; set; }

    [Column("triangle_count")]
    [ColumnDescription("三角面数")]
    public int TriangleCount { get; set; }

    [Column("min_x")] public double MinX { get; set; }
    [Column("min_y")] public double MinY { get; set; }
    [Column("min_z")] public double MinZ { get; set; }
    [Column("max_x")] public double MaxX { get; set; }
    [Column("max_y")] public double MaxY { get; set; }
    [Column("max_z")] public double MaxZ { get; set; }

    /// <summary>打包的顶点 + 三角索引(base64,opaque)。见 <c>VirtualDrillGeometry</c>。</summary>
    [Column("geometry_b64")]
    [ColumnDescription("三角网几何(顶点+索引,base64)")]
    public string GeometryB64 { get; set; } = "";

    [Column("created_at")]
    public string? CreatedAt { get; set; }

    [Column("updated_at")]
    public string? UpdatedAt { get; set; }
}
