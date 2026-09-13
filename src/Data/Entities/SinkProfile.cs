// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/SinkProfile.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 去向扩展档案(V035)。dump_site / load_unload_point 两张表都放不下的去向属性存这里,
/// 主键 <see cref="SinkId"/> 与调度侧的 SinkNode.Id 同值:
/// 排土场为 <c>dump_site.dump_id</c>,卸载点为 <c>LUP-{load_unload_point.id}</c>。
///
/// 权威边界(读回时按此合并,避免双主口径):
///  · 容量 / 已堆 / 台阶高 / 台阶坡角 / 状态 / 内外排 —— dump_site 为准;
///  · 名称 / 通过能力 / 坐标 / 卸载子类           —— load_unload_point 为准;
///  · 本表只对「原表表达不了的部分」有话语权(可接物料、工作线长、排弃层、兜底运距、
///    开放时窗、表土堆场这类细分类型、卸载点的状态)。
///
/// 单位一律工程原单位(m³ / m / km / t·h⁻¹ / 小时),不用万 m³ —— 万 m³ 只是 dump_site 的
/// 历史口径,换算只发生在 dump_site 边界上。
/// </summary>
[Table("sink_profile")]
[ColumnDescription("去向扩展档案")]
public class SinkProfile
{
    [Column("sink_id"), PrimaryKey]
    [ColumnDescription("去向编号(dump_site.dump_id 或 LUP-{id})")]
    public string SinkId { get; set; } = "";

    [Column("sink_kind")]
    [ColumnDescription("去向类型(SinkKind 枚举名;细化 dump_type 表达不了的表土堆场等)")]
    public string SinkKind { get; set; } = "";

    [Column("status")]
    [ColumnDescription("状态 active/full/closed(仅卸载点侧权威)")]
    public string Status { get; set; } = "active";

    [Column("accept_tph")]
    [ColumnDescription("通过能力 t/h,0=不限(仅排土场侧权威)")]
    public double AcceptTph { get; set; }

    [Column("accepted_materials")]
    [ColumnDescription("可接物料码白名单,逗号分隔;空=按物料自身允许去向判定")]
    public string AcceptedMaterials { get; set; } = "";

    [Column("work_line_length_m")]
    [ColumnDescription("排土工作线长(m)")]
    public double WorkLineLengthM { get; set; }

    [Column("active_bench_level")]
    [ColumnDescription("当前可排台阶层(自下而上,1 起)")]
    public long ActiveBenchLevel { get; set; } = 1;

    [Column("fallback_haul_km")]
    [ColumnDescription("兜底运距(km)")]
    public double FallbackHaulKm { get; set; }

    [Column("open_from_hour")]
    [ColumnDescription("当日开放时窗起(0..24)")]
    public double OpenFromHour { get; set; }

    [Column("open_to_hour")]
    [ColumnDescription("当日开放时窗止(0..24);0/24=全天")]
    public double OpenToHour { get; set; } = 24;

    [Column("open_from_period")]
    [ColumnDescription("启用期次(内排土场须等采空区形成;空=已启用)")]
    public string? OpenFromPeriod { get; set; }

    // ── 去向代表点坐标(V036)────────────────────────────────────────────────────
    // 权威归属:【卸载点】以 load_unload_point.x/y/z 为准(那张表自带坐标列);
    // 本三列只对【排土场】生效 —— dump_site 没有坐标列,这里是排土场坐标唯一的家。
    // 用途:HaulResolver 先按 RefEntityId/Id 去路网精确匹配节点,匹配不上才按坐标吸附;
    //       没有坐标的排土场两条路都走不通,运距只能落到第三层兜底值。
    // 口径:与 load_unload_point.x/y/z、road_network 节点同一坐标系,单位 m。
    //       X、Y 均为 0 = 未录坐标(不是原点);Z=0 是合法标高,不参与该判定。

    [Column("x")]
    [ColumnDescription("去向代表点 X(m);x、y 均为 0=未录坐标。仅排土场侧权威")]
    public double X { get; set; }

    [Column("y")]
    [ColumnDescription("去向代表点 Y(m);仅排土场侧权威")]
    public double Y { get; set; }

    [Column("z")]
    [ColumnDescription("去向代表点 Z 标高(m);仅排土场侧权威")]
    public double Z { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    // created_at / updated_at 不映射:交给 DB DEFAULT CURRENT_TIMESTAMP + AFTER UPDATE 触发器
    // (与 LoadUnloadPoint 同做法);映射成可空 DateTime 反而会在 Update 时把它写成 NULL。
}
