using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 去向台账的**写回路径**（移植原 <c>TaskLib.Engine.SinkRegistryLoader</c> 的后半段）。
/// 读—改—存闭环的后半程：排土场进 <c>dump_site</c>，破碎站/煤仓/堆场进 <c>load_unload_point</c>，
/// 两张表放不下的字段进 <c>sink_profile</c>。
///
/// ── 「已填」的写回纪律（<see cref="Save(DbConnection, IEnumerable{SinkNode})"/> 与 <see cref="Stocktake"/> 的分界）──
///   普通保存**绝不**回写 <c>current_filled_wan_m3</c> —— 内存里的 <c>FilledM3</c> 常常叠着当日实绩的
///   界面增量，照写会把当日排弃量重复计一遍。要改「已填」只能走盘点：<see cref="Stocktake"/>
///   显式改账并往 <c>sink_stocktake</c> 留一条流水。新建的去向例外 —— 那是开账初值，必须写一次。
///
/// ── Kylin 侧的实现差异（登记）──
///   ① 原版走 <c>EquipmentDataContext</c> 的三个仓储服务，Kylin 没有服务层，这里直接对表发 SQL；
///      "服务未注册"这类中止原因相应改为"没有数据库连接"。
///   ② 新建卸载点取自增主键：原版靠仓储的 <c>Insert</c> 返回值，这里用
///      <c>INSERT</c> 后 <c>SELECT MAX(id)</c> —— SQLite 的 <c>last_insert_rowid()</c> 与 PG 的
///      <c>RETURNING</c> 各家不通用，而本台账是单人桌面录入，不存在并发插入。
/// </summary>
public static partial class SinkRegistryLoader
{
    /// <summary>
    /// 台账写回结果。UI 要能据此说清三件事：成了几条、败了几条、每一条为什么败。
    /// 「一条都没跑起来」（DB 不可用）与「跑了但某几条失败」是两回事，故单列 <see cref="Aborted"/>。
    /// </summary>
    public sealed class SinkSaveResult
    {
        /// <summary>新建的去向数（本体表里原本没有的行）。</summary>
        public int Inserted { get; internal set; }
        public int Updated { get; internal set; }
        public int Deleted { get; internal set; }
        /// <summary>失败的去向数（含"本体写进去了但扩展档案没写成"的半成功）。</summary>
        public int Failed { get; internal set; }

        /// <summary>整体没跑起来（数据库不可用 / 无输入），此时各计数均为 0。</summary>
        public bool Aborted { get; internal set; }
        public string AbortReason { get; internal set; } = "";

        /// <summary>逐条失败原因，形如「北排土场：状态取值非法」。</summary>
        public List<string> Errors { get; } = new();
        /// <summary>需要让用户知道、但不算失败的事（新分配的编号等）。</summary>
        public List<string> Notes { get; } = new();

        public bool Ok => !Aborted && Failed == 0;
        public int Saved => Inserted + Updated;

        internal void Abort(string reason) { Aborted = true; AbortReason = reason; }
        internal void Fail(string who, string why) { Failed++; Errors.Add($"{who}：{why}"); }

        /// <summary>一句话结论（直接贴状态栏）。</summary>
        public string Caption
        {
            get
            {
                if (Aborted) return $"未保存：{AbortReason}";
                var parts = new List<string>();
                if (Inserted > 0) parts.Add($"新增 {Inserted} 条");
                if (Updated > 0) parts.Add($"更新 {Updated} 条");
                if (Deleted > 0) parts.Add($"删除 {Deleted} 条");
                if (parts.Count == 0 && Failed == 0) parts.Add("无改动");
                string head = string.Join("，", parts);
                if (Failed > 0)
                    head += $"；失败 {Failed} 条 —— {string.Join("；", Errors.Take(3))}"
                          + (Errors.Count > 3 ? $" 等 {Errors.Count} 条" : "");
                if (Notes.Count > 0) head += "　（" + string.Join("；", Notes.Take(3)) + "）";
                return head;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  保存
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>把登记簿整体写回台账。返回逐条结果，不抛异常。</summary>
    public static SinkSaveResult Save(DbConnection? conn, SinkRegistry? registry)
        => Save(conn, registry?.All);

    /// <summary>写回指定的若干去向。</summary>
    public static SinkSaveResult Save(DbConnection? conn, IEnumerable<SinkNode>? sinks)
    {
        var r = new SinkSaveResult();
        var list = sinks?.Where(s => s != null).ToList() ?? new List<SinkNode>();
        if (list.Count == 0) { r.Abort("没有可保存的去向"); return r; }
        if (conn == null) { r.Abort("没有数据库连接"); return r; }

        foreach (var s in list) SaveOne(conn, s, r);
        return r;
    }

    private static void SaveOne(DbConnection conn, SinkNode s, SinkSaveResult r)
    {
        string id = (s.Id ?? "").Trim();
        string name = string.IsNullOrWhiteSpace(s.Name) ? id : s.Name.Trim();
        if (id.Length == 0)
        { r.Fail(string.IsNullOrWhiteSpace(name) ? "（无名去向）" : name, "去向编号为空，无法定位台账行"); return; }

        string? status = NormalizeStatus(s.Status);
        if (status == null) { r.Fail(name, $"状态取值非法（{s.Status}）—— 只能是 在用/已排满/已关闭"); return; }

        if (s.DesignCapacityM3 < 0 || double.IsNaN(s.DesignCapacityM3)) { r.Fail(name, "设计容量必须 ≥0"); return; }
        if (s.AcceptTph < 0 || double.IsNaN(s.AcceptTph)) { r.Fail(name, "通过能力必须 ≥0"); return; }

        // 本体行在哪张表：RefEntityId 是权威（读进来时写好的）；空 = 新建，按类型选表
        string reference = string.IsNullOrWhiteSpace(s.RefEntityId) ? "" : s.RefEntityId.Trim();
        bool rowIsLup = reference.Length > 0 ? IsLupRef(reference) : (!s.Kind.IsDumping() || IsLupRef(id));
        bool wantsLup = !s.Kind.IsDumping();

        // 跨族改类型 = 换表存储，不能靠 UPDATE 完成（会留下孤儿行 + 主键换命名空间）。
        // 明说而不是偷偷改：让用户删了重建，账面才不会出现"同一个去向两处存在"。
        if (reference.Length > 0 && rowIsLup != wantsLup)
        {
            r.Fail(name, rowIsLup
                ? "由通过型改成排弃类需换表存储（load_unload_point→dump_site）：请删除本条后重新新增"
                : "由排弃类改成通过型需换表存储（dump_site→load_unload_point）：请删除本条后重新新增");
            return;
        }

        bool bodyOk;
        bool isNew;
        try
        {
            bodyOk = rowIsLup
                ? SaveLoadUnloadPoint(conn, s, name, r, out isNew)
                : SaveDumpSite(conn, s, name, status, out isNew);
        }
        catch (Exception ex) { r.Fail(name, $"写本体失败：{Short(ex)}"); return; }

        if (!bodyOk) return;

        // 扩展档案：写不进去要算失败 —— 否则用户改的"可接物料/时窗"会在下次重读时悄悄回到旧值，
        // 而界面刚刚提示过"保存成功"。半成功如实报，不许四舍五入成成功。
        try { UpsertProfile(conn, s, status); }
        catch (Exception ex)
        {
            r.Fail(name, $"本体已写入，但扩展档案（可接物料/工作线长/时窗）失败：{Short(ex)}");
            return;
        }

        if (isNew) r.Inserted++; else r.Updated++;
    }

    /// <summary>SinkNode → dump_site。★ 单位换算：契约【m³】→ 台账【万 m³】（÷1e4）。</summary>
    private static bool SaveDumpSite(DbConnection conn, SinkNode s, string name, string status, out bool isNew)
    {
        string dumpId = string.IsNullOrWhiteSpace(s.RefEntityId) ? s.Id.Trim() : s.RefEntityId.Trim();
        isNew = !RowExists(conn, "dump_site", "dump_id", dumpId);

        // dump_type 只有 internal/external 两个合法值（表上有 CHECK）；表土堆场落 external，
        // 「表土堆场」这层细分由 sink_profile.sink_kind 保存，读回时再细化。
        string type = s.Kind == SinkKind.InternalDump ? "internal" : "external";
        double capWan = Math.Max(0, s.DesignCapacityM3) / 1e4;

        if (isNew)
        {
            // 开账初值：只有新建时才写「已填」，之后一律交给实绩回灌与盘点
            Exec(conn,
                "INSERT INTO dump_site (dump_id, name, dump_type, design_capacity_wan_m3, current_filled_wan_m3, "
              + "bench_height_m, bench_slope_angle_deg, status, start_date) VALUES ("
              + Lit(dumpId) + ", " + Lit(name) + ", " + Lit(type) + ", " + Num(capWan) + ", "
              + Num(Math.Max(0, s.FilledM3) / 1e4) + ", "
              + (s.BenchHeightM > 0 ? Num(s.BenchHeightM) : "NULL") + ", "
              + (s.BenchSlopeAngleDeg > 0 ? Num(s.BenchSlopeAngleDeg) : "NULL") + ", "
              + Lit(status) + ", " + Lit(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + ")");
        }
        else
        {
            // ★ 不写 current_filled_wan_m3 —— 见本文件抬头的「已填」纪律
            var sets = new List<string>
            {
                "name = " + Lit(name),
                "dump_type = " + Lit(type),
                "design_capacity_wan_m3 = " + Num(capWan),
                "status = " + Lit(status),
            };
            // 0 = "界面没填"，不是"改成 0"：照写会把台账里录好的台阶参数抹掉
            if (s.BenchHeightM > 0) sets.Add("bench_height_m = " + Num(s.BenchHeightM));
            if (s.BenchSlopeAngleDeg > 0) sets.Add("bench_slope_angle_deg = " + Num(s.BenchSlopeAngleDeg));
            Exec(conn, "UPDATE dump_site SET " + string.Join(", ", sets) + " WHERE dump_id = " + Lit(dumpId));
        }

        s.RefEntityId = dumpId;
        return true;
    }

    /// <summary>SinkNode → load_unload_point（通过型去向：名称 / 通过能力 / 坐标 / 卸载子类）。</summary>
    private static bool SaveLoadUnloadPoint(DbConnection conn, SinkNode s, string name, SinkSaveResult r, out bool isNew)
    {
        bool hasId = TryLupId(string.IsNullOrWhiteSpace(s.RefEntityId) ? s.Id : s.RefEntityId, out long id)
                  && id > 0 && RowExists(conn, "load_unload_point", "id", id.ToString(CultureInfo.InvariantCulture), quote: false);
        isNew = !hasId;

        if (isNew)
        {
            // 新建：主键是自增整数，只有插进去才知道编号，故插完必须把 Id/RefEntityId 回填，
            // 否则这一条会在下次保存时被当成"又一个新点"再插一遍。
            Exec(conn,
                "INSERT INTO load_unload_point (name, kind, unload_sub, x, y, z, throughput_tph, visible) VALUES ("
              + Lit(name) + ", 'unloading', " + Lit(SubCodeOf(s.Kind)) + ", "
              + Num(Sane(s.X)) + ", " + Num(Sane(s.Y)) + ", " + Num(Sane(s.Z)) + ", "
              + Num(Math.Max(0, s.AcceptTph)) + ", 1)");

            long newId = ScalarLong(conn, "SELECT MAX(id) FROM load_unload_point");
            if (newId <= 0) { r.Fail(name, "卸载点插入未返回编号"); return false; }

            s.Id = $"LUP-{newId}";
            s.RefEntityId = s.Id;
            r.Notes.Add($"{name} 已建档，编号 {s.Id}");
            return true;
        }

        Exec(conn, "UPDATE load_unload_point SET name = " + Lit(name) + ", kind = 'unloading', "
                 + "unload_sub = " + Lit(SubCodeOf(s.Kind)) + ", "
                 + "x = " + Num(Sane(s.X)) + ", y = " + Num(Sane(s.Y)) + ", z = " + Num(Sane(s.Z)) + ", "
                 + "throughput_tph = " + Num(Math.Max(0, s.AcceptTph))
                 + " WHERE id = " + id.ToString(CultureInfo.InvariantCulture));
        s.RefEntityId = $"LUP-{id}";
        return true;
    }

    /// <summary>SinkNode → sink_profile（两张本体表都没有列的那些字段）。先删后插，各家语法通用。</summary>
    private static void UpsertProfile(DbConnection conn, SinkNode s, string status)
    {
        string sinkId = s.Id.Trim();
        Exec(conn, "DELETE FROM sink_profile WHERE sink_id = " + Lit(sinkId));
        Exec(conn,
            "INSERT INTO sink_profile (sink_id, sink_kind, status, accept_tph, accepted_materials, "
          + "work_line_length_m, active_bench_level, fallback_haul_km, open_from_hour, open_to_hour, "
          + "open_from_period, x, y, z) VALUES ("
          + Lit(sinkId) + ", " + Lit(s.Kind.ToString()) + ", " + Lit(status) + ", "
          + Num(Math.Max(0, s.AcceptTph)) + ", "
          + Lit(string.Join(",", s.AcceptedMaterials.Where(MaterialCatalog.Exists))) + ", "
          + Num(Math.Max(0, s.WorkLineLengthM)) + ", " + Math.Max(1, s.ActiveBenchLevel) + ", "
          + Num(Math.Max(0, s.FallbackHaulKm)) + ", "
          + Num(Clamp24(s.OpenFromHour, 0)) + ", " + Num(Clamp24(s.OpenToHour, 24)) + ", "
          + Lit(string.IsNullOrWhiteSpace(s.OpenFromPeriod) ? null : s.OpenFromPeriod!.Trim()) + ", "
          // 坐标两族都写一份。读回时只有排土场采信本表（见 ApplyProfile），卸载点的权威在
          // load_unload_point —— 但档案里同样存一份：一来不至于让卸载点的档案行留一串 0
          // 看着像数据丢了，二来两边对不上时有据可查。
          + Num(Sane(s.X)) + ", " + Num(Sane(s.Y)) + ", " + Num(Sane(s.Z)) + ")");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  删除
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 删除一个去向：本体行（<c>dump_site</c> / <c>load_unload_point</c>）+ 扩展档案一起删。
    /// <b>调用方须先自行拦截</b>：当日有入方的去向不许删 —— 那样删掉，当日运量就没有落点了。
    /// </summary>
    public static SinkSaveResult Delete(DbConnection? conn, SinkNode? sink)
    {
        var r = new SinkSaveResult();
        if (sink == null || string.IsNullOrWhiteSpace(sink.Id)) { r.Abort("未指定要删除的去向"); return r; }
        if (conn == null) { r.Abort("没有数据库连接"); return r; }

        string name = string.IsNullOrWhiteSpace(sink.Name) ? sink.Id : sink.Name;
        string reference = string.IsNullOrWhiteSpace(sink.RefEntityId) ? sink.Id : sink.RefEntityId;

        try
        {
            if (IsLupRef(reference))
            {
                if (TryLupId(reference, out long id) && id > 0)
                    Exec(conn, "DELETE FROM load_unload_point WHERE id = " + id.ToString(CultureInfo.InvariantCulture));
                else { r.Fail(name, $"卸载点编号解析不出（{reference}），未删除"); return r; }
            }
            else Exec(conn, "DELETE FROM dump_site WHERE dump_id = " + Lit(reference));

            r.Deleted++;
        }
        catch (Exception ex) { r.Fail(name, $"删除本体失败：{Short(ex)}"); return r; }

        // 档案删不掉不算失败：本体已经没了，残留档案行不会被任何人读到（读取按本体表驱动）
        try { Exec(conn, "DELETE FROM sink_profile WHERE sink_id = " + Lit(sink.Id)); }
        catch (Exception ex) { r.Notes.Add($"{name} 的扩展档案未能清除：{Short(ex)}"); }

        return r;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  盘点（唯一允许改「已填」的入口）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 库容盘点修正：显式改「已填」，并往 <c>sink_stocktake</c> 留一条流水（改前/改后/差额/原因）。
    /// 这是唯一允许改 <c>FilledM3</c> 的入口 —— 普通编辑改不了，因为那是实绩累计出来的账。
    /// 成功后同步内存节点，UI 无需重读即可看到新充填率。
    /// </summary>
    public static SinkSaveResult Stocktake(DbConnection? conn, SinkNode? sink, double newFilledM3,
                                           string? reason, string? op = null)
    {
        var r = new SinkSaveResult();
        if (sink == null || string.IsNullOrWhiteSpace(sink.Id)) { r.Abort("未指定要盘点的去向"); return r; }
        if (string.IsNullOrWhiteSpace(reason)) { r.Abort("盘点必须填写修正原因（无原因不许改账）"); return r; }
        if (double.IsNaN(newFilledM3) || double.IsInfinity(newFilledM3) || newFilledM3 < 0)
        { r.Abort("盘点后的已填量必须是 ≥0 的数"); return r; }
        if (!sink.IsDumping) { r.Abort("通过型去向（破碎站/煤仓/堆场）不占排土库容，无需盘点"); return r; }
        if (conn == null) { r.Abort("没有数据库连接"); return r; }

        string name = string.IsNullOrWhiteSpace(sink.Name) ? sink.Id : sink.Name;
        string dumpId = string.IsNullOrWhiteSpace(sink.RefEntityId) ? sink.Id : sink.RefEntityId;
        if (IsLupRef(dumpId)) { r.Abort("该去向存在 load_unload_point，表里没有库容列，无法盘点"); return r; }

        double before = sink.FilledM3;
        try
        {
            if (!RowExists(conn, "dump_site", "dump_id", dumpId))
            { r.Fail(name, $"台账里找不到排土场 {dumpId}（请先保存台账建档）"); return r; }
            // ★ 单位换算：契约【m³】→ 台账【万 m³】
            Exec(conn, "UPDATE dump_site SET current_filled_wan_m3 = " + Num(newFilledM3 / 1e4)
                     + " WHERE dump_id = " + Lit(dumpId));
            r.Updated++;
        }
        catch (Exception ex) { r.Fail(name, $"改账失败：{Short(ex)}"); return r; }

        // 流水写不进去要报出来：账改了却没留痕，比不改更糟 —— 用户得知道这次修正没有凭据
        try
        {
            Exec(conn,
                "INSERT INTO sink_stocktake (sink_id, sink_name, before_filled_m3, after_filled_m3, delta_m3, "
              + "reason, operator) VALUES (" + Lit(sink.Id) + ", " + Lit(name) + ", " + Num(before) + ", "
              + Num(newFilledM3) + ", " + Num(newFilledM3 - before) + ", " + Lit(reason!.Trim()) + ", "
              + Lit((string.IsNullOrWhiteSpace(op) ? Environment.UserName : op!).Trim()) + ")");
        }
        catch (Exception ex) { r.Notes.Add($"账已改，但盘点流水未能记录：{Short(ex)}"); }

        sink.FilledM3 = newFilledM3;      // 内存同步，UI 立刻看到新充填率
        return r;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  实绩回灌 / 新建
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 实绩回灌：把当期排弃的【占容方 m³】累加进去向。
    /// 内存登记簿一定更新；只有来自 <c>dump_site</c> 的去向才回写台账（÷1e4 折回万 m³）。
    /// DB 不可用时只更新内存、不抛异常 —— 日常派工不能因为写库失败就中断。
    /// 返回该去向回灌后的充填率（0..1）；去向不存在返回 0。
    /// </summary>
    public static double AddFilled(DbConnection? conn, SinkRegistry? registry, string sinkId, double dumpM3)
    {
        var sink = registry?.Find(sinkId);
        if (sink == null || registry == null) return 0;

        // ① 内存永远先更新（计划推演与 UI 立即看到效果）
        double rate = registry.AddFilled(sinkId, dumpM3);

        // ② 回写台账：破碎站/煤仓（LUP- 前缀）不占库容，无需回写
        if (conn != null && FromDatabase && sink.IsDumping && !IsLupRef(sink.RefEntityId))
        {
            try
            {
                // ★ 单位换算：契约【m³】→ 台账【万 m³】
                string dumpId = string.IsNullOrWhiteSpace(sink.RefEntityId) ? sink.Id : sink.RefEntityId;
                Exec(conn, "UPDATE dump_site SET current_filled_wan_m3 = " + Num(sink.FilledM3 / 1e4)
                         + " WHERE dump_id = " + Lit(dumpId));
            }
            catch { /* 写库失败不影响内存口径, 下次 Load() 会以台账为准重新对齐 */ }
        }
        return rate;
    }

    /// <summary>
    /// 造一个还没入库的新去向（「新增去向」按钮用）。排土类给 <c>dump_site</c> 风格的编号，
    /// 通过型给占位编号 —— 真编号是 <c>load_unload_point</c> 的自增主键，只有 INSERT 之后才知道。
    /// </summary>
    public static SinkNode CreateNew(SinkKind kind, string? name = null)
    {
        _newSeq++;
        bool dumping = kind.IsDumping();
        string id = dumping ? $"D-{DateTime.Now:yyMMddHHmm}{_newSeq:00}" : $"LUP-新{_newSeq}";

        return new SinkNode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"新建{kind.Label()}{_newSeq}" : name!.Trim(),
            Kind = kind,
            DesignCapacityM3 = dumping ? 100e4 : 0,      // 通过型不占库容 ⇒ 0 = 不限
            FilledM3 = 0,
            AcceptTph = 0,
            BenchHeightM = dumping ? DefaultBenchHeightM : 0,
            BenchSlopeAngleDeg = DefaultBenchSlopeDeg,
            ActiveBenchLevel = 1,
            Status = "active",
            FallbackHaulKm = FallbackKmOf(kind),
            RefEntityId = "",                             // 空 = 还没有本体行，保存时按 Kind 选表建档
        };
    }

    private static int _newSeq;

    /// <summary>丢弃"数据是哪来的"标记（台账改动后调用，逼下次 Load 重读）。</summary>
    public static void Invalidate()
    {
        FromDatabase = false;
        LastSourceLabel = "样例去向（尚未装载）";
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────

    /// <summary>状态归一化：中文/英文都收，非法值返回 null（<c>dump_site.status</c> 上有 CHECK 约束，不能乱写）。</summary>
    internal static string? NormalizeStatus(string? raw)
    {
        string v = (raw ?? "").Trim();
        if (v.Length == 0) return "active";
        if (v.Equals("active", StringComparison.OrdinalIgnoreCase) || v == "在用" || v == "启用") return "active";
        if (v.Equals("full", StringComparison.OrdinalIgnoreCase) || v == "已排满" || v == "排满") return "full";
        if (v.Equals("closed", StringComparison.OrdinalIgnoreCase) || v == "已关闭" || v == "关闭") return "closed";
        return null;
    }

    /// <summary>
    /// SinkKind → <c>load_unload_point.unload_sub</c>。词表沿用「装卸点设置」的
    /// crusher/stockpile/dump，外加 silo；未知一律 dump。
    /// </summary>
    internal static string SubCodeOf(SinkKind k) => k switch
    {
        SinkKind.Crusher => "crusher",
        SinkKind.Stockpile => "stockpile",
        SinkKind.Silo => "silo",
        _ => "dump",
    };

    /// <summary>坐标兜底：NaN/无穷一律记 0（= 未录坐标），别让脏值进库再被当成真实位置吸附路网。</summary>
    internal static double Sane(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    internal static double Clamp24(double v, double fallback)
        => double.IsNaN(v) || v < 0 || v > 24 ? fallback : v;

    internal static bool TryLupId(string? s, out long id)
    {
        id = 0;
        if (!IsLupRef(s)) return false;
        return long.TryParse(s!.Substring(4).Trim(), out id);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool RowExists(DbConnection conn, string table, string key, string value, bool quote = true)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {key} = " + (quote ? Lit(value) : value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static long ScalarLong(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    private static string Lit(string? s)
        => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";

    /// <summary>数值字面量一律走不变文化：中文 Windows 下 <c>ToString()</c> 会按区域出小数点分隔符。</summary>
    private static string Num(double v)
        => (double.IsNaN(v) || double.IsInfinity(v) ? 0 : v).ToString("R", CultureInfo.InvariantCulture);
}
