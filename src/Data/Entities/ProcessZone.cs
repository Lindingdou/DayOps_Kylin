// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ProcessZone.cs（逐行对应；仅命名空间适配）
using System;

using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 工序作业区(V047)：<b>某一期、某一道工序，在哪儿干</b>。
///
/// <para><b>与 <see cref="MineableRegion"/> 的分工（这是本表存在的全部理由）</b>：
/// mineable_region 装的是<b>长期存在的地</b>（采场 / 排土场 / 两个工作帮）——
/// 一个采场一条，推进了改边界、不按期次新建，三维推演的推进轮廓、路网中心线裁剪、
/// 单元归属都读它。本表装的是<b>某一期某道工序的作业位置</b>，按期次新建、过期即历史，
/// 下游是任务编制 / 派工 / 清场，<b>不参与推演的推进极性</b>。</para>
///
/// <para><b>为什么不能塞进 mineable_region</b>：那张表的类别判定是白名单而兜底是采场
/// （<c>IsPit =&gt; !IsDump &amp;&amp; !IsWorkingSlope</c>，TaskLib 的 ZoneStore 与 SimRegions 各一份）。
/// 新增一个 category 会让这些行<b>静默当成采场</b>进推演轮廓与路网裁剪 ——
/// 一个字的红字都不会有，只是推演里凭空多出几块朝推进方向动的地。</para>
///
/// <para><b>五道工序的地不在同一个位置</b>：穿孔超前、爆破在其后、采装再后、排土在另一头。
/// 同一个作业面同一期，五块地沿推进方向错开。所以唯一键是
/// <c>期次 + 工序 + 名字</c>三者 —— 少了 <see cref="Process"/>，后生成的会 upsert 掉前一块而不报错。</para>
/// </summary>
[Table("process_zone")]
[ColumnDescription("工序作业区 / 某期某工序的作业位置")]
public class ProcessZone
{
    // ── 工序码的值域（**只在这里定义一次**）──────────────────────────────
    //
    // 与 mineable_region.category 同一条纪律：值域散在各模块里各写一遍字面量的话，
    // 改一个字母就静默筛不出东西。运输不在其列 —— 它是线状的（装车点 → 路径 → 卸载点），
    // 硬圈成面只会得到一块没人用的地。

    /// <summary>穿孔。位置 = 采装区沿推进方向<b>前推</b>超前期那一段；免爆的面没有这一块。</summary>
    public const string ProcDrill = "drill";

    /// <summary>
    /// 爆破警戒区。= 穿孔区外扩警戒半径。
    /// <para><b>它是禁入区不是作业区</b>：语义与其余四类相反（其余是"谁去这儿干活"，
    /// 它是"这段时间谁都不许进"）。也因此它<b>按炮次分组，不按作业面</b> ——
    /// 它天然会盖住相邻的面，而这正是它的用处。</para>
    /// </summary>
    public const string ProcBlastGuard = "blast_guard";

    /// <summary>采装。位置 = 本期采掘单元的占地并集（与三维单元体同源）。</summary>
    public const string ProcLoad = "load";

    /// <summary>排土·卸载带。= 排土幅的<b>前缘带</b>（靠坡顶线一侧，宽 = 卸载带宽 + 车挡）。设备是卡车。</summary>
    public const string ProcDumpTip = "dump_tip";

    /// <summary>排土·推排带。= 排土幅<b>减去</b>卸载带剩下的后方带。设备是推土机。</summary>
    public const string ProcDumpDoze = "dump_doze";

    /// <summary>全部工序码，<b>按现场先后次序</b>（下游按序号排甘特行，别重排）。</summary>
    public static readonly string[] AllProcesses =
        { ProcDrill, ProcBlastGuard, ProcLoad, ProcDumpTip, ProcDumpDoze };

    /// <summary>工序码 → 中文名。<b>加工序时只改这里</b>（认不出就把原码显示出来，不冒充成某一类）。</summary>
    public static string DisplayName(string? process) => (process ?? "").Trim() switch
    {
        ProcDrill => "穿孔",
        ProcBlastGuard => "爆破警戒",
        ProcLoad => "采装",
        ProcDumpTip => "排土·卸载",
        ProcDumpDoze => "排土·推排",
        "" => "未指定工序",
        var other => other,
    };

    /// <summary>这道工序该由谁去干（派工时按它筛设备）。</summary>
    public static string EquipRole(string? process) => (process ?? "").Trim() switch
    {
        ProcDrill => "钻机",
        ProcLoad => "电铲",
        ProcDumpTip => "卡车",
        ProcDumpDoze => "推土机",
        ProcBlastGuard => "",          // 警戒区不派设备 —— 它派的是清场，人不是机
        _ => "",
    };

    /// <summary>
    /// 这块地的量是<b>哪个口径</b>的。
    /// <para><b>三个口径绝不许并成一列</b>：原位实方(采装) / 控制方量(穿孔) / 排弃占容 V实×Kr(排土)。
    /// 混进同一列，谁一 SUM 就得到一个不对应任何真实量的数，而且不会有任何东西报错。</para>
    /// </summary>
    public static string VolumeBasis(string? process) => (process ?? "").Trim() switch
    {
        ProcDrill => "控制方量",
        ProcLoad => "原位实方",
        ProcDumpTip or ProcDumpDoze => "排弃占容",
        _ => "",
    };

    /// <summary>这道工序是禁入区（而不是作业区）。</summary>
    public static bool IsKeepOut(string? process)
        => string.Equals(process, ProcBlastGuard, StringComparison.OrdinalIgnoreCase);

    /// <summary>工序默认色 0xRRGGBB —— 与甘特的工序色对齐（穿孔紫/爆破红/采装蓝/排土琥珀）。</summary>
    public static uint Color(string? process) => (process ?? "").Trim() switch
    {
        ProcDrill => 0x8E5FD0u,        // 穿孔 紫
        ProcBlastGuard => 0xD8342Bu,   // 爆破警戒 红
        ProcLoad => 0x2E6FCFu,         // 采装 蓝
        ProcDumpTip => 0xE8A33Du,      // 排土·卸载 琥珀
        ProcDumpDoze => 0xB07A22u,     // 排土·推排 深琥珀
        _ => 0x808080u,
    };

    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("period")]
    [ColumnDescription("期次 yyyy-MM(工序区是按期的)")]
    public string Period { get; set; } = "";

    [Column("process")]
    [ColumnDescription("工序码 drill/blast_guard/load/dump_tip/dump_doze")]
    public string Process { get; set; } = "";

    [Column("name")]
    [ColumnDescription("区域名(作业面名/炮次号;分块带 -2 -3 后缀)")]
    public string Name { get; set; } = "";

    [Column("group_key")]
    [ColumnDescription("分组键(作业面名;警戒区按炮次)")]
    public string GroupKey { get; set; } = "";

    [Column("piece_index")]
    [ColumnDescription("本组第几块(不相连时分块出)")]
    public long PieceIndex { get; set; } = 1;

    [Column("piece_count")]
    [ColumnDescription("本组共几块")]
    public long PieceCount { get; set; } = 1;

    [Column("points_json")]
    [ColumnDescription("边界顶点(扁平 xyz JSON 数组);与 mineable_region 同口径")]
    public string PointsJson { get; set; } = "[]";

    /// <summary>
    /// 高程出处。<b>空 = 不许入库</b>。
    /// <para>同 mineable_region 的 z 陷阱：points_json 里写 0 会被下游当成实测高程
    /// （只有零顶点才返回 NaN），层体整体摆到 0m 而界面正常。</para>
    /// </summary>
    [Column("z_source")]
    [ColumnDescription("高程出处(空=不许入库)")]
    public string ZSource { get; set; } = "";

    /// <summary>栅格格距 m —— <b>轮廓精度就是它</b>，不存的话人会把它当实测边界。</summary>
    [Column("cell_m")]
    [ColumnDescription("栅格格距 m = 轮廓精度")]
    public double CellM { get; set; }

    [Column("area_m2")]
    [ColumnDescription("栅格面积 m2(真实占地,不受描边失真影响)")]
    public double AreaM2 { get; set; }

    [Column("ring_area_m2")]
    [ColumnDescription("描边环面积 m2(与栅格面积差得多=描边不可信)")]
    public double RingAreaM2 { get; set; }

    /// <summary>
    /// 膨胀顶到了格网边界 = 本区被截断（仅爆破警戒区会有）。
    /// <para>截断后的轮廓仍是一条规规矩矩的闭合环，图上看不出来 —— 所以必须在库里留证据。</para>
    /// </summary>
    [Column("clipped_at_border")]
    [ColumnDescription("1=膨胀顶到格网边界,本区被截断")]
    public long ClippedAtBorder { get; set; }

    [Column("unit_ids")]
    [ColumnDescription("覆盖的采掘单元号,顿号分隔")]
    public string UnitIds { get; set; } = "";

    [Column("unit_count")]
    [ColumnDescription("覆盖的采掘单元数")]
    public long UnitCount { get; set; }

    [Column("volume_m3")]
    [ColumnDescription("本区的量 m3(NULL=不适用,如警戒区)")]
    public double? VolumeM3 { get; set; }

    [Column("volume_basis")]
    [ColumnDescription("量口径:原位实方/控制方量/排弃占容(三者绝不许并成一列)")]
    public string Basis { get; set; } = "";

    [Column("equip_role")]
    [ColumnDescription("该区的设备角色:钻机/电铲/卡车/推土机")]
    public string EquipRoleName { get; set; } = "";

    [Column("lead_days")]
    [ColumnDescription("相对采装的超前(+)/滞后(-)工日;NULL=不适用")]
    public double? LeadDays { get; set; }

    [Column("guard_radius_m")]
    [ColumnDescription("警戒半径 m(仅 blast_guard)")]
    public double? GuardRadiusM { get; set; }

    [Column("band_width_m")]
    [ColumnDescription("带宽 m(仅 dump_tip/dump_doze)")]
    public double? BandWidthM { get; set; }

    /// <summary>本期算不算数。<b>与 <see cref="Visible"/> 是两件事</b>：Visible 管画不画它。</summary>
    [Column("active")]
    [ColumnDescription("本期是否纳入(0/1)")]
    public long Active { get; set; } = 1;

    [Column("visible")]
    [ColumnDescription("是否在视口显示(0/1)")]
    public long Visible { get; set; } = 1;

    [Column("note")]
    [ColumnDescription("记账:期次+分组+单元清单+格距+Z出处")]
    public string Note { get; set; } = "";

    // created_at / updated_at 不映射:交给 DB 的 DEFAULT + 触发器,避免显式传 NULL 撞 NOT NULL。
}
