// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/MineableRegion.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 可采区域边界 — 短期「采场/排土场圈定」逐点圈画出的一块采场可采区域多边形。
/// 边界顶点以 JSON（扁平 [x0,y0,z0,x1,y1,z1,...]）存在 <see cref="PointsJson"/>。
/// </summary>
[Table("mineable_region")]
[ColumnDescription("可采区域边界")]
public class MineableRegion
{
    // ── category 的值域（**只在这里定义一次**）────────────────────────────
    //
    // 这些字符串跨三个模块用：PlanLib「采场/排土场圈定」写它，BlockModelLib 的
    // 「采矿模型」「排土条带」按它筛，TaskLib / MineAssLib 的下游按它分类。
    // 而 BlockModelLib 引不到 PlanLib（反向依赖），所以值域只能落在实体这一层 ——
    // 各处各写一遍字面量的话，改一个字母就静默筛不出东西。

    /// <summary>
    /// <b>未分类</b> —— 手动圈画时还没判类别的默认值（列值域的历史名是 <c>mineable</c>）。
    ///
    /// <para><b>显示名不叫"可采区域"</b>（2026-08-17 改）：那个词在界面上有两种身份 ——
    /// 既是这一个类别，又是<b>整个区域库的统称</b>（"限定可采区域""可采区域库"）。
    /// 一词两义，而这张表现在装的是采场 / 排土场 / 两个工作帮 / 未分类六类 ——
    /// 采场和排土场都不是"可采区域"，拿它当类别名只会让人以为自己选错了。</para>
    ///
    /// <para><b>⚠ 值不能改、也不能从值域里删</b>：露头带 / 斜面模板那条链把它
    /// <b>与 <see cref="CatPit"/> 一并当"可采范围"用</b>（<c>MineAssLibPlugin</c> 的
    /// 「自动·可采范围」= <c>mineable</c> + <c>pit</c> 的全部环）。改值会让那条链筛不出东西，
    /// 而它不会报错，只会少裁一片。</para>
    /// </summary>
    public const string CatMineable = "mineable";
    /// <summary>采场（凹·逐级降深）。<b>只有它参与"这个单元属于哪个采场"的归属判定。</b></summary>
    public const string CatPit = "pit";
    /// <summary>外排土场（凸·境界外）。</summary>
    public const string CatExternalDump = "external_dump";
    /// <summary>内排土场（凸·坑内回填）。</summary>
    public const string CatInternalDump = "internal_dump";

    /// <summary>
    /// 剥采工作帮 —— 采场范围<b>之内</b>再收一层，「采矿模型」的范围闸门（2026-08-17 加）。
    /// <para><b>不是"一块地"，不参与归属</b>：台账那一行写哪个采场仍由 <see cref="CatPit"/> 决定。
    /// 一个采场一条，工作帮往前推了就改它的边界，不按期次新建。</para>
    /// </summary>
    public const string CatPitWorkingSlope = "pit_working_slope";

    /// <summary>排土工作帮 —— 排土场范围之内再收一层，「排土条带」的范围闸门。</summary>
    public const string CatDumpWorkingSlope = "dump_working_slope";

    /// <summary>这个类别是"工作帮"（范围闸门）而不是一块地。</summary>
    public static bool IsWorkingSlope(string? category)
        => string.Equals(category, CatPitWorkingSlope, System.StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, CatDumpWorkingSlope, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个类别是采场（<b>白名单，只有 <see cref="CatPit"/> 算</b>）。
    ///
    /// <para><b>为什么必须是白名单</b>：「采矿模型」原先用黑名单「名字里不含 dump 就算采场」，
    /// 于是 ① 手动圈画默认的 <see cref="CatMineable"/> 被当成采场；
    /// ② 后加的 <see cref="CatPitWorkingSlope"/> 会被当成一个独立采场<b>再建一遍，量算两遍</b>，
    /// 而 <see cref="CatDumpWorkingSlope"/> 因为含 dump 字样反倒被正确排除 ——
    /// 两个新类别一个中招一个不中，这种半对半错最难发现。
    /// <b>以后再加类别，默认就该被排除</b>，要算采场必须显式改这里。</para>
    /// </summary>
    public static bool IsPit(string? category)
        => string.Equals(category, CatPit, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>这个类别是排土场（内排 / 外排）。工作帮那两类<b>不算</b>。</summary>
    public static bool IsDumpSite(string? category)
        => string.Equals(category, CatExternalDump, System.StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, CatInternalDump, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这两个类别是<b>「工作帮 ↔ 它自己的母范围」</b>那一对 —— 剥采工作帮 ↔ 采场、
    /// 排土工作帮 ↔ 排土场（内排 / 外排）。<b>只有这一对之间的重叠是正常的</b>：
    /// 工作帮按定义就落在母范围之内，圈定窗口对这一对豁免"空间唯一"的扣除运算。
    ///
    /// <para><b>其余组合一律照常扣除</b>（2026-08-18 现场令）：<b>剥采工作帮压到排土场上、
    /// 排土工作帮压到采场上，都是圈错了地方，必须裁开</b>。由此派生的几种也照常扣除 ——
    /// 工作帮 ↔ 未分类、剥采工作帮 ↔ 排土工作帮、以及两条同类工作帮之间
    /// （一个采场一条、一个排土场一条，两条同类的压在一起本身就说明圈重了）。</para>
    ///
    /// <para>参数<b>无序</b>：两个方向都判，调用方不必关心谁是 self。</para>
    /// </summary>
    public static bool IsGateOverItsParent(string? a, string? b)
        => (IsPitGate(a) && IsPit(b)) || (IsPit(a) && IsPitGate(b))
        || (IsDumpGate(a) && IsDumpSite(b)) || (IsDumpSite(a) && IsDumpGate(b));

    /// <summary>剥采工作帮（采场的那个闸门）。</summary>
    public static bool IsPitGate(string? category)
        => string.Equals(category, CatPitWorkingSlope, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>排土工作帮（排土场的那个闸门）。</summary>
    public static bool IsDumpGate(string? category)
        => string.Equals(category, CatDumpWorkingSlope, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 类别 → 中文名。<b>加类别时只改这里</b>。
    ///
    /// <para><b>为什么收到这一层</b>：这份映射此前在界面侧有 <b>5 份重复</b>
    /// （标注台阶标高 · 平盘标高统计 · 路中线 · 坡底线 · 区域圈定），每份都带
    /// <c>_ =&gt;</c> 兜底。于是新增一个类别只改了圈定窗口那一份，其余四处
    /// 一声不响地把它显示成"可采区域"或原始英文码 —— 看表的人会以为类别丢了。</para>
    ///
    /// <para><c>wide_bench</c>（达标平盘）是<b>旧版遗留</b>，库里已由
    /// <c>MineableRegionService.All()</c> 统一清除，这里仍认它 —— 老库/老导出文件里还有。</para>
    /// </summary>
    public static string DisplayName(string? category) => (category ?? "").Trim() switch
    {
        CatPit => "采场",
        CatExternalDump => "外排土场",
        CatInternalDump => "内排土场",
        CatPitWorkingSlope => "剥采工作帮",
        CatDumpWorkingSlope => "排土工作帮",
        // ★ 不叫"可采区域"：那个词是整库的统称（"限定可采区域"），拿来当类别名一词两义。
        //   它的实义就是"圈了但还没判类别"。⚠ 只改显示名，category 的值仍是 mineable ——
        //   露头带那条链按值筛（mineable + pit = 自动·可采范围），改值会让它静默少裁一片。
        CatMineable => "未分类",
        "wide_bench" => "达标平盘",
        "" => "未分类",
        var other => other,          // 认不出就把原码显示出来，别冒充成某一类
    };

    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("name")]
    [ColumnDescription("区域名称")]
    public string Name { get; set; } = "";

    [Column("category")]
    [ColumnDescription("类别 mineable/pit/external_dump/internal_dump/pit_working_slope/dump_working_slope")]
    public string Category { get; set; } = "mineable";

    [Column("points_json")]
    [ColumnDescription("边界顶点(扁平 xyz JSON 数组)")]
    public string PointsJson { get; set; } = "[]";

    [Column("visible")]
    [ColumnDescription("是否在视口显示(0/1)")]
    public long Visible { get; set; } = 1;

    /// <summary>
    /// 本期是否纳入作业范围(1=选定)。**与 <see cref="Visible"/> 是两件事**:
    /// Visible 管"画不画它",本列管"算不算数" —— 三维推演的推进轮廓与路网中心线提取的
    /// 裁剪范围只认本列。混用一列会让"为了看图清爽隐掉一块"变成"推演里静默少一块"。
    /// 见 V043。
    /// </summary>
    [Column("active")]
    [ColumnDescription("本期是否纳入作业范围(0/1);推演轮廓与路网裁剪只认它")]
    public long Active { get; set; } = 1;

    [Column("note")]
    [ColumnDescription("备注")]
    public string? Note { get; set; }

    [Column("color")]
    [ColumnDescription("自定义 overlay 颜色(6 位十六进制 RRGGBB);NULL=按类别默认色")]
    public string? Color { get; set; }

    // created_at / updated_at 不映射:故意从 INSERT/UPDATE 省略,交给 DB 的
    // DEFAULT CURRENT_TIMESTAMP + AFTER UPDATE 触发器,避免显式传 NULL 撞 NOT NULL。
}
