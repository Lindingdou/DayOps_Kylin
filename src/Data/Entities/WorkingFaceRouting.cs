// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/WorkingFaceRouting.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 作业面去向档案(V035)。<see cref="WorkingFace"/> 存的是「台阶几何」(台阶高/采宽/面长/推进度),
/// 本表存的是「当日怎么干」(去向、运距、日目标、混采分项)——调度侧 FaceInput 上那批
/// working_face 没有列可落的字段。主键 <see cref="FaceCode"/> 对齐 <c>working_face.face_code</c>。
///
/// 两处口径陷阱(写错就污染别的消费者):
///  · <see cref="BenchElevationM"/> 是台阶【标高】,不是 working_face.bench_height_m 的台阶
///    【高度】——量纲同为 m,含义完全不同,严禁互写。
///  · <see cref="EngineeringPositionId"/> 是工程位置(EP-xx),不是 working_face.location_code
///    的平盘编码。
/// </summary>
[Table("working_face_routing")]
[ColumnDescription("作业面去向档案")]
public class WorkingFaceRouting
{
    [Column("face_code"), PrimaryKey]
    [ColumnDescription("作业面编号(= working_face.face_code)")]
    public string FaceCode { get; set; } = "";

    [Column("process")]
    [ColumnDescription("工序 Load(采装)/Dump(排土)")]
    public string Process { get; set; } = "Load";

    [Column("engineering_position_id")]
    [ColumnDescription("工程位置(EP-xx);≠ 平盘编码")]
    public string EngineeringPositionId { get; set; } = "";

    [Column("bench_elevation_m")]
    [ColumnDescription("台阶标高(m);不是台阶高度")]
    public double BenchElevationM { get; set; }

    [Column("material_code")]
    [ColumnDescription("物料码")]
    public string MaterialCode { get; set; } = "";

    [Column("material_mix")]
    [ColumnDescription("混采构成原文(如 煤6∶岩4);空=单一物料")]
    public string MaterialMix { get; set; } = "";

    [Column("destination_id")]
    [ColumnDescription("主去向编号")]
    public string DestinationId { get; set; } = "";

    [Column("destination_name")]
    [ColumnDescription("主去向名称")]
    public string DestinationName { get; set; } = "";

    [Column("destination_kind")]
    [ColumnDescription("主去向类型(SinkKind 枚举名)")]
    public string DestinationKind { get; set; } = "";

    [Column("haul_distance_km")]
    [ColumnDescription("运距(km)")]
    public double HaulDistanceKm { get; set; }

    [Column("equiv_haul_km")]
    [ColumnDescription("等效运距(km,含坡度折算)")]
    public double EquivHaulKm { get; set; }

    [Column("day_target_m3")]
    [ColumnDescription("当日目标(m³ 实方)")]
    public double DayTargetM3 { get; set; }

    [Column("derived_from_inbound")]
    [ColumnDescription("1=排土面,日目标由入方推导")]
    public long DerivedFromInbound { get; set; }

    [Column("shovel_model_pref")]
    [ColumnDescription("主铲型号偏好(编组求解用)")]
    public string ShovelModelPref { get; set; } = "";

    [Column("main_equipment")]
    [ColumnDescription("主设备(同时写回 working_face.equipment_id)")]
    public string MainEquipment { get; set; } = "";

    [Column("splits_json")]
    [ColumnDescription("混采分项去向 JSON(MaterialDestination[]),opaque")]
    public string SplitsJson { get; set; } = "[]";

    // ── 煤质目标(V036;配煤约束的输入)──────────────────────────────────────────
    // ★ 四项一律可空,且各自独立可空:
    //   · NULL = 该项无目标。纯量矿场景整面没有煤质目标,四项全 NULL——
    //     用 0 冒充会让配煤约束把每个面都判成「灰分 0、硫 0,达标」,恒真的约束比没有更危险。
    //   · 各自可空是为了让「只填了灰分、热值还没定」这种录入中间态如实存下来;
    //     是否构成一个【有效的煤质目标】由调用方判(TaskLib 侧:四项齐全才组装 CoalQuality)。
    // 单位:灰/硫/水为百分数 %(填 12.5 不是 0.125);热值为 MJ/kg(不是 kcal/kg)。

    [Column("quality_ash_pct")]
    [ColumnDescription("灰分目标 %(NULL=无目标)")]
    public double? QualityAshPct { get; set; }

    [Column("quality_cv_mjkg")]
    [ColumnDescription("热值目标 MJ/kg(NULL=无目标)")]
    public double? QualityCvMjKg { get; set; }

    [Column("quality_sulfur_pct")]
    [ColumnDescription("硫分目标 %(NULL=无目标)")]
    public double? QualitySulfurPct { get; set; }

    [Column("quality_moisture_pct")]
    [ColumnDescription("水分目标 %(NULL=无目标)")]
    public double? QualityMoisturePct { get; set; }

    /// <summary>
    /// 实配车号(V036),逗号分隔如 <c>T-01,T-02</c>;空 = 未配车。
    /// 现场实际配的车,与 FleetMatcher 求解出的建议车数之差正是「运力不足」的判据
    /// (配 &lt; 荐 ⇒ 铲将待车)。建议车数与编组班产是求解派生值,故意不入库。
    /// </summary>
    [Column("assigned_trucks")]
    [ColumnDescription("实配车号,逗号分隔(如 T-01,T-02);空=未配车")]
    public string AssignedTrucks { get; set; } = "";

    // ── 源端坐标(V037;路网求运距的另一半)──────────────────────────────────────
    //  V036 补齐了【汇】端坐标,本组补齐【源】端。路网寻径要求源汇两端都能定位到
    //  RoadNode:源端没坐标只能拿作业面名/工程位置号去做名称匹配,匹配不上整条链就落
    //  「兜底运距」,而运距是循环时间 T_c 的输入、T_c 定最优配车数 n*、n* 定编组班产 ——
    //  编组班产正是编制裂解装箱的 bin,源端缺坐标下游一串数全跟着偏。
    //
    //  ★ 判据:X、Y 均为 0 = 未录坐标,**不是原点**(与 <c>FaceInput.HasSourcePosition</c>、
    //    HaulResolver 的坐标吸附闸门完全一致)。把 (0,0) 当真实位置去吸附路网节点,
    //    会吸到离原点最近的那个节点上,算出一个「看着很正常」的假运距 —— 宁可如实落兜底。
    //  ★ 这三列只存【人工录入】的坐标。没录时由 TaskLib 侧从 mineable_region 的区域质心
    //    自动推导,那是系统推的、**不入库**(库里仍是 0):区域边界改了推导值要跟着改,
    //    不能被一份陈旧的猜测钉死,更不能让人分不清哪些是自己填的、哪些是系统猜的。
    //  单位:m,与 sink_profile.x/y/z、load_unload_point.x/y/z、road_network 节点同一坐标系。

    [Column("source_x")]
    [ColumnDescription("源代表点 X(m);x、y 均为 0 = 未录坐标")]
    public double SourceX { get; set; }

    [Column("source_y")]
    [ColumnDescription("源代表点 Y(m)")]
    public double SourceY { get; set; }

    [Column("source_z")]
    [ColumnDescription("源代表点 Z 标高(m);Z=0 是合法标高,不参与'有没有坐标'的判定")]
    public double SourceZ { get; set; }

    // ── 空间身份与备采家底(V041)───────────────────────────────────────────────
    //  这一组补的是「这个面是图上哪个体、还剩多少可采、往哪推」。
    //  缺它们时:一条日任务只有 zone(自由文本面名),点一行定位不到图上任何东西;
    //  "备采用尽"判的是当日目标用尽而不是储量见底,采准断不断档(三量)无从判起。

    /// <summary>
    /// 采掘单元号(V041),= 采矿模型导出表里的 <c>UnitId</c>(层-B带号-P幅号,如 <c>3煤-B12-P03</c>)。
    /// 空 = 该面还没绑到具体单元。绑上之后:任务能对回图上的体、备采量能核销、
    /// 期次覆盖率能算、影像底图上有边界可画。
    /// </summary>
    [Column("unit_id")]
    [ColumnDescription("采掘单元号(= 采矿模型 UnitId);空=未绑")]
    public string UnitId { get; set; } = "";

    /// <summary>
    /// 本面备采储量(m³ 原位实方,V041)。
    /// <b>0 = 未录,不是"采空"</b> —— 判据一律先看 &gt;0 再用。拿 0 当采空会让每个没录储量的面
    /// 天天报"采准断档",真正断档的那个反而淹了。
    /// </summary>
    [Column("available_reserve_m3")]
    [ColumnDescription("备采储量(m³ 原位实方);0=未录,非采空")]
    public double AvailableReserveM3 { get; set; }

    /// <summary>推进方位(°,正北起顺时针,V041)。NULL = 未录。作业区布置画推进箭头、动画走位用。</summary>
    [Column("advance_azimuth_deg")]
    [ColumnDescription("推进方位(°,正北起顺时针);NULL=未录")]
    public double? AdvanceAzimuthDeg { get; set; }

    /// <summary>
    /// 本面当前推进宽(m,V041)。NULL = 未录。
    /// <b>与 <c>working_face.mining_width_m</c> 同名不同义</b>:那边是设计采宽(按月变的设计口径),
    /// 这边是本面当日实际推进宽。严禁互写。
    /// </summary>
    [Column("mining_width_m")]
    [ColumnDescription("本面当前推进宽(m);≠ working_face 的设计采宽;NULL=未录")]
    public double? MiningWidthM { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    // created_at / updated_at 不映射:交给 DB DEFAULT + AFTER UPDATE 触发器。
}
