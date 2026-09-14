using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 去向台账装载器 —— 把 <c>dump_site</c> / <c>load_unload_point</c> 两张"死表"接成活的
/// <see cref="SinkRegistry"/>（移植原 <c>TaskLib.Engine.SinkRegistryLoader</c> 的**读取路径**）。
///
/// 在此之前 Kylin 的这三张表（V003 排土场 / V017 装卸点 / V035+V036 去向档案）**零消费者**：
/// 录了排土场也没人用，编组与流向分配只能吃 <c>SinkRegistry.Sample()</c> 的假数据。本类是那条接线。
///
/// ── 三张表的分工 ──
///   <c>dump_site</c>          排土场本体：容量 / 已堆 / 台阶高 / 台阶坡角 / 状态 / 内外排
///   <c>load_unload_point</c>  卸载点本体：名称 / 通过能力 / 坐标 / 卸载子类
///   <c>sink_profile</c>       两张表都放不下的：可接物料白名单 / 工作线长 / 当前排弃层 /
///                             兜底运距 / 开放时窗 / 启用期次 / 细分类型 / 卸载点状态 / **排土场坐标**
///
/// ── 坐标的权威归属（最容易搞反的一处）──
///   · 卸载点 —— <c>load_unload_point.x/y/z</c> 为准（那张表自带坐标列）；
///   · 排土场 —— <c>sink_profile.x/y/z</c> 为准（<c>dump_site</c> 根本没有坐标列，档案是它唯一的家）。
///   读回时严格按这条挑，**绝不让档案里的坐标盖掉卸载点本体的坐标**。
///
/// ── 单位陷阱（本文件最容易出错的地方）──
///   <c>dump_site</c> 的容量列是【万 m³】，<see cref="SinkNode"/> 契约里是【m³】—— 换算系数 1e4。
///   做反了会差 4 个数量级。<c>sink_profile</c> 一律用工程原单位（m / km / t·h⁻¹ / 小时）。
///
/// ── 容错原则 ──
///   数据库没接通、表是空的，都不许崩：读一律回落 <see cref="SinkRegistry.Sample"/>，
///   并通过 <see cref="LastSourceLabel"/> 把"数据是哪来的"告诉 UI。
/// </summary>
public static partial class SinkRegistryLoader
{
    public const double DefaultBenchHeightM = 20;
    public const double DefaultBenchSlopeDeg = 35;

    /// <summary>
    /// 数据来源文案（供 UI 显示"这盘数据可不可信"）。形如
    /// 「排土场台账（DB，5 个去向 · …）」或「样例去向（DB 未接通：…）」。
    /// <para>注："样例去向"是原版沿用的叫法，而 <see cref="SinkRegistry.Sample"/> 两边都是**空登记簿** ——
    /// 假去向比没有去向坏得多，回落时宁可一条都不给。文案照原样保留以便与原版对照。</para>
    /// </summary>
    public static string LastSourceLabel { get; private set; } = "样例去向（尚未装载）";

    /// <summary>
    /// 当前这份去向登记簿是不是从库里来的（false = 回落的空登记簿）。
    /// <para><b>不许从 <see cref="LastSourceLabel"/> 里正则抠</b>：文案一改就静默抠空，
    /// 而抠空之后"样例"会被当成"真实"—— 链路体检整条失去意义。</para>
    /// </summary>
    public static bool FromDatabase { get; private set; }

    /// <summary>
    /// 从台账装载全部去向：<c>dump_site</c>（排土场）+ <c>load_unload_point</c> 中
    /// <c>kind='unloading'</c> 的（卸载点），再用 <c>sink_profile</c> 细化。
    /// 任何一步失败都回落 <see cref="SinkRegistry.Sample"/>，不抛异常。
    /// </summary>
    public static SinkRegistry Load(DbConnection? conn)
    {
        if (conn == null)
        {
            FromDatabase = false;
            LastSourceLabel = "样例去向（DB 未接通：未打开数据库连接）";
            return SinkRegistry.Sample();
        }

        var sinks = new List<SinkNode>();
        string? dumpErr = null, lupErr = null;
        int dumpCount = 0, lupCount = 0;

        // ① 排土场台账 —— full/closed 的也要进登记簿：库容校核要看得见"已排满"，
        //    是否可用交给 SinkNode.IsActive 判定，而不是在这里就把它们过滤掉。
        try { foreach (var s in ReadDumpSites(conn)) { sinks.Add(s); dumpCount++; } }
        catch (Exception ex) { dumpErr = Short(ex); }

        // ② 装卸点里的卸载点（破碎站 / 煤仓 / 堆场；kind='unloading'）
        try { foreach (var s in ReadUnloadPoints(conn)) { sinks.Add(s); lupCount++; } }
        catch (Exception ex) { lupErr = Short(ex); }

        // ③ 一个都没读到 → 回落，把原因带出去
        if (sinks.Count == 0)
        {
            string why = dumpErr ?? lupErr
                ?? "dump_site 与 load_unload_point 均为空表（请先在「排土场管理」/本「去向台账」录入）";
            FromDatabase = false;
            LastSourceLabel = $"样例去向（DB 未接通：{why}）";
            return SinkRegistry.Sample();
        }

        // ④ 覆盖扩展档案（sink_profile）：两张本体表放不下的字段在这里补回来。
        //    没有档案行的去向保持工程缺省值 —— 即"从没在台账里编辑过"，不是"被清零"。
        int profCount = 0;
        string? profErr = null;
        var reg = new SinkRegistry();
        reg.Load(sinks);
        try { profCount = ApplyProfiles(conn, reg); }
        catch (Exception ex) { profErr = Short(ex); }

        FromDatabase = true;

        // 部分成功也要说清楚：读到了排土场但装卸点表炸了，运维得知道。
        string detail = $"排土场 {dumpCount} + 卸载点 {lupCount}";
        if (profCount > 0) detail += $" · 扩展档案 {profCount}";
        if (dumpErr != null) detail += $"；排土场读取失败：{dumpErr}";
        if (lupErr != null) detail += $"；装卸点读取失败：{lupErr}";
        if (profErr != null) detail += $"；扩展档案读取失败：{profErr}（可接物料/工作线长/时窗按缺省值）";
        LastSourceLabel = $"排土场台账（DB，{sinks.Count} 个去向 · {detail}）";
        return reg;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        int nl = m.IndexOfAny(new[] { '\r', '\n' });
        if (nl > 0) m = m[..nl];
        return m.Length <= 60 ? m : m[..60] + "…";
    }

    // ── dump_site → SinkNode ────────────────────────────────────────────────
    internal static List<SinkNode> ReadDumpSites(DbConnection conn)
    {
        var list = new List<SinkNode>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dump_id, name, dump_type, design_capacity_wan_m3, current_filled_wan_m3, "
                        + "bench_height_m, bench_slope_angle_deg, status FROM dump_site";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string id = S(rd, 0), name = S(rd, 1);
            if (string.IsNullOrWhiteSpace(id)) continue;   // 无主键的行进不了登记簿（Put 会静默丢弃）
            var kind = DumpKindOf(name, S(rd, 2));
            list.Add(new SinkNode
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                Kind = kind,
                // ★ 单位换算：台账【万 m³】→ 契约【m³】
                DesignCapacityM3 = D(rd, 3) * 1e4,
                FilledM3 = D(rd, 4) * 1e4,
                BenchHeightM = rd.IsDBNull(5) ? DefaultBenchHeightM : D(rd, 5),
                BenchSlopeAngleDeg = rd.IsDBNull(6) ? DefaultBenchSlopeDeg : D(rd, 6),
                Status = string.IsNullOrWhiteSpace(S(rd, 7)) ? "active" : S(rd, 7).Trim(),
                // dump_site 无坐标列 —— 留 0，由 sink_profile.x/y/z 在 ApplyProfile 里补。
                // 档案里也没录就保持 0：调用方据此判"无坐标"，否则会把 (0,0,0) 当真实位置，
                // 吸附到离原点最近的路网节点上，算出一个看着像真的假运距。
                FallbackHaulKm = FallbackKmOf(kind),
                RefEntityId = id,
            });
        }
        return list;
    }

    /// <summary>内排 / 外排 / 表土堆场判定：先看 dump_type，**名字里含「表土」的单独归为表土堆场**。</summary>
    internal static SinkKind DumpKindOf(string? name, string? dumpType)
    {
        string n = name ?? "";
        // 表土必须单独堆存供复垦，不得混入岩石排土场 —— 名字是目前唯一的判据
        if (n.Contains("表土") || n.Contains("腐殖")) return SinkKind.TopsoilYard;
        return string.Equals((dumpType ?? "").Trim(), "internal", StringComparison.OrdinalIgnoreCase)
            ? SinkKind.InternalDump
            : SinkKind.ExternalDump;
    }

    // ── load_unload_point(kind='unloading') → SinkNode ──────────────────────
    internal static List<SinkNode> ReadUnloadPoints(DbConnection conn)
    {
        var list = new List<SinkNode>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, unload_sub, x, y, z, throughput_tph FROM load_unload_point "
                        + "WHERE kind = 'unloading'";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string id = S(rd, 0), name = S(rd, 1);
            var kind = UnloadKindOf(name, S(rd, 2));
            list.Add(new SinkNode
            {
                // 前缀隔离命名空间：装卸点主键是自增整数，直接用会和 dump_id 撞车
                Id = $"LUP-{id}",
                Name = string.IsNullOrWhiteSpace(name) ? $"卸载点{id}" : name,
                Kind = kind,
                // 该表没有容量列：破碎站/煤仓是「通过型」去向，卸多少走多少、不占库容，
                // DesignCapacityM3=0 即 RemainingM3=+∞。
                DesignCapacityM3 = 0,
                FilledM3 = 0,
                AcceptTph = D(rd, 6),
                X = D(rd, 3), Y = D(rd, 4), Z = D(rd, 5),   // 装卸点有真实坐标
                Status = "active",
                FallbackHaulKm = FallbackKmOf(kind),
                RefEntityId = $"LUP-{id}",
            });
        }
        return list;
    }

    /// <summary>卸载子类映射：**名字优先**（更贴近现场叫法），其次 <c>unload_sub</c> 列。</summary>
    internal static SinkKind UnloadKindOf(string? name, string? unloadSub)
    {
        string n = name ?? "";
        string sub = (unloadSub ?? "").Trim();

        // ① 煤仓：名字里含「煤仓」/「silo」的，不管子类填什么都按原煤仓算
        if (n.Contains("煤仓") || n.Contains("silo", StringComparison.OrdinalIgnoreCase)) return SinkKind.Silo;
        // ② 破碎站
        if (string.Equals(sub, "crusher", StringComparison.OrdinalIgnoreCase) || n.Contains("破碎")) return SinkKind.Crusher;
        // ③ 堆场（配矿缓冲 / 低品位暂存）
        if (string.Equals(sub, "stockpile", StringComparison.OrdinalIgnoreCase)
            || n.Contains("堆场") || n.Contains("储煤")) return SinkKind.Stockpile;
        // ④ sub=='dump' 或未填 —— 按名字判内外排 / 表土，默认外排土场
        if (n.Contains("表土") || n.Contains("腐殖")) return SinkKind.TopsoilYard;
        if (n.Contains("内排")) return SinkKind.InternalDump;
        return SinkKind.ExternalDump;
    }

    /// <summary>
    /// 三层兜底最后一层用的缺省运距 km（仅当路网不可解且面上没手填时才生效）。
    /// 按去向类型给量级差异：**内排在采空区里，比外排近得多** —— 这正是内排降本的由来。
    /// </summary>
    internal static double FallbackKmOf(SinkKind kind) => kind switch
    {
        SinkKind.InternalDump => 1.5,
        SinkKind.TopsoilYard => 2.0,
        SinkKind.Crusher => 2.5,
        SinkKind.Stockpile => 2.5,
        SinkKind.Silo => 3.0,
        _ => 3.0,   // 外排土场
    };

    // ── sink_profile 细化 ───────────────────────────────────────────────────
    private static int ApplyProfiles(DbConnection conn, SinkRegistry reg)
    {
        int n = 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sink_id, sink_kind, status, accept_tph, accepted_materials, work_line_length_m, "
                        + "active_bench_level, fallback_haul_km, open_from_hour, open_to_hour, open_from_period, x, y, z "
                        + "FROM sink_profile";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var s = reg.Find(S(rd, 0));
            if (s == null) continue;      // 档案里有、本体表里没有 ⇒ 孤儿行，忽略
            ApplyProfile(s,
                sinkKind: S(rd, 1), status: S(rd, 2), acceptTph: D(rd, 3), materials: S(rd, 4),
                workLineM: D(rd, 5), benchLevel: (int)D(rd, 6), fallbackKm: D(rd, 7),
                openFrom: D(rd, 8), openTo: D(rd, 9), openFromPeriod: S(rd, 10),
                x: D(rd, 11), y: D(rd, 12), z: D(rd, 13));
            n++;
        }
        return n;
    }

    /// <summary>把一条档案套到去向上（拆成参数是为了能单测，不必造 DbDataReader）。</summary>
    internal static void ApplyProfile(SinkNode s, string? sinkKind, string? status, double acceptTph,
                                      string? materials, double workLineM, int benchLevel, double fallbackKm,
                                      double openFrom, double openTo, string? openFromPeriod,
                                      double x, double y, double z)
    {
        bool isLup = IsLupRef(s.RefEntityId) || IsLupRef(s.Id);

        // 类型：只作为**细化**采信 —— 同族才覆盖（排弃类↔排弃类、通过型↔通过型）。
        // 行躺在哪张表是物理事实，dump_site 的行不可能是破碎站；档案能表达的是
        // dump_type 表达不了的「表土堆场」、以及 unload_sub 表达不了的「原煤仓」。
        if (Enum.TryParse<SinkKind>(sinkKind, ignoreCase: true, out var kind) && kind.IsDumping() == s.Kind.IsDumping())
            s.Kind = kind;

        // 状态：排土场以 dump_site.status 为准；卸载点表没有 status 列，档案是它唯一的家
        if (isLup && !string.IsNullOrWhiteSpace(status)) s.Status = status!.Trim();

        // 通过能力：卸载点以 load_unload_point.throughput_tph 为准；排土场表没有这一列
        if (!isLup && acceptTph > 0) s.AcceptTph = acceptTph;

        s.AcceptedMaterials = ParseMaterials(materials);
        if (workLineM > 0) s.WorkLineLengthM = workLineM;
        if (benchLevel > 0) s.ActiveBenchLevel = benchLevel;
        if (fallbackKm > 0) s.FallbackHaulKm = fallbackKm;

        // 时窗：0/24 就是「全天」，属于有效取值，不能用 >0 过滤，否则改回全天存不下来
        if (openTo > 0 && openTo <= 24 && openFrom >= 0 && openFrom < openTo)
        {
            s.OpenFromHour = openFrom;
            s.OpenToHour = openTo;
        }
        s.OpenFromPeriod = string.IsNullOrWhiteSpace(openFromPeriod) ? null : openFromPeriod!.Trim();

        // 坐标：★ **只对排土场生效**。卸载点的坐标以本体表为准 —— 这里若照抄档案，
        // 就会用一份可能过期的副本盖掉本体表的权威值。
        if (!isLup) { s.X = x; s.Y = y; s.Z = z; }
    }

    /// <summary>可接物料白名单：分隔符切开 → 集合；**目录里不存在的码直接丢弃**（别让脏码变成"什么都不收"）。</summary>
    internal static HashSet<string> ParseMaterials(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var raw in csv.Split(new[] { ',', '，', ';', '；', '/', '、' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string code = raw.Trim();
            if (code.Length == 0) continue;
            if (!MaterialCatalog.Exists(code)) code = MaterialCatalog.CodeFromText(code);   // 容忍中文名
            if (MaterialCatalog.Exists(code)) set.Add(code);
        }
        return set;
    }

    internal static bool IsLupRef(string? id) => (id ?? "").StartsWith("LUP-", StringComparison.OrdinalIgnoreCase);

    private static string S(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    private static double D(DbDataReader rd, int i)
        => rd.IsDBNull(i) ? 0 : Convert.ToDouble(rd.GetValue(i), CultureInfo.InvariantCulture);
}
