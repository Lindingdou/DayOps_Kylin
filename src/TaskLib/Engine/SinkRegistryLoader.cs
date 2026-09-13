// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/SinkRegistryLoader.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;     // DumpSite / LoadUnloadPoint / SinkProfile
using PitMine3D.Kylin.Data.Services;     // IDumpSiteService / ILoadUnloadPointService / ISinkProfileService
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  去向台账装载器 —— 把 dump_site / load_unload_point 两张「死表」接成活的 SinkRegistry，
//  并把台账窗口上的改动写回去（读—改—存闭环，不是只读视图）。
//
//  在此之前 DumpSite / LoadUnloadPoint 两个实体零消费者：录了排土场也没人用，
//  编组与流向分配只能吃 SinkRegistry.Sample() 的假数据。本类是那条接线。
//
//  ── 三张表的分工（V035 / V036 之后）──
//    dump_site            排土场本体：容量 / 已堆 / 台阶高 / 台阶坡角 / 状态 / 内外排
//    load_unload_point    卸载点本体：名称 / 通过能力 / 坐标 / 卸载子类
//    sink_profile         两张表都放不下的：可接物料白名单 / 工作线长 / 当前排弃层 /
//                         兜底运距 / 开放时窗 / 启用期次 / 细分类型（表土堆场）/ 卸载点状态 /
//                         **排土场坐标**（V036）
//
//  坐标的权威归属（V036，最容易搞反的一处）：
//    · 卸载点 —— load_unload_point.x/y/z 为准（那张表自带坐标列，「装卸点设置」也在改它）；
//    · 排土场 —— sink_profile.x/y/z 为准（dump_site 根本没有坐标列，档案是它唯一的家）。
//    写回时两侧都往档案里落一份（与 accept_tph / status 同做法：写全、读按权威挑），
//    读回时严格按上面这条规则挑，绝不让档案里的坐标盖掉卸载点本体的坐标。
//
//  单位陷阱（本文件最容易出错的地方）：
//    dump_site 表里的容量列是【万 m³】，SinkNode 契约里是【m³】——换算系数 1e4，
//    两个方向（读台账 ×1e4、回灌台账 ÷1e4）都必须做，做反了会差 4 个数量级。
//    sink_profile 一律用工程原单位（m / km / t·h⁻¹ / 小时），不掺万 m³。
//
//  「已填」的写回纪律（<see cref="Save"/> 与 <see cref="Stocktake"/> 的分界）：
//    普通保存**绝不**回写 current_filled_wan_m3 —— 内存里的 FilledM3 常常叠着当日实绩
//    的界面增量（见 SinkLedgerWindow.OnRefresh），照写会把当日排弃量重复计一遍。
//    要改「已填」只能走盘点：Stocktake() 显式改账并往 sink_stocktake 留一条流水。
//
//  容错原则：数据库没接通、服务没注册、表是空的，都不许让 TaskLib 崩——
//  读一律回落 SinkRegistry.Sample()，并通过 LastSourceLabel 把「数据是哪来的」告诉 UI；
//  写一律返回 <see cref="SinkSaveResult"/>（成功几条 / 失败几条 / 为什么失败），不抛、不静默。
// ─────────────────────────────────────────────────────────────────────────────

public static class SinkRegistryLoader
{
    /// <summary>吸附/兜底常量：台账没有的字段用这些工程缺省值。</summary>
    private const double DefaultBenchHeightM = 20;
    private const double DefaultBenchSlopeDeg = 35;

    /// <summary>
    /// 数据来源文案（供 UI 显示「这盘数据可不可信」）。
    /// 形如「排土场台账（DB，5 个去向）」或「样例去向（DB 未接通：…）」。
    /// </summary>
    public static string LastSourceLabel { get; private set; } = "样例去向（尚未装载）";

    /// <summary>最近一次 Load() 得到的登记簿——AddFilled 的内存回灌对象。</summary>
    private static SinkRegistry? _current;

    /// <summary>是否真的读到了 DB（决定 AddFilled 要不要回写台账）。</summary>
    private static bool _fromDb;

    /// <summary>
    /// 当前这份去向登记簿是不是从库里来的（false = 样例去向）。
    /// <para><b>不许从 <see cref="LastSourceLabel"/> 里正则抠</b>：文案一改就静默抠空，
    /// 而抠空之后"样例"会被当成"真实"—— 链路体检整条失去意义。</para>
    /// </summary>
    public static bool FromDatabase => _fromDb;

    /// <summary>最近一次装载出来的登记簿；从未装载过则现装一次。</summary>
    public static SinkRegistry Current => _current ??= Load();

    /// <summary>
    /// 从台账装载全部去向：dump_site（排土场）+ load_unload_point 中 kind=='unloading' 的
    /// 破碎站 / 煤仓 / 堆场。任何一步失败都回落 SinkRegistry.Sample()，不抛异常。
    /// </summary>
    public static SinkRegistry Load()
    {
        var sinks = new List<SinkNode>();
        string? dumpErr = null, lupErr = null;
        int dumpCount = 0, lupCount = 0;

        // ── ① 排土场台账 ──
        try
        {
            // activeOnly:false —— full/closed 的排土场也要进登记簿：库容校核要看得见「已排满」，
            // 是否可用交给 SinkNode.IsActive 判定，而不是在这里就把它们过滤掉。
            var rows = EquipmentDataContext.DumpSites.All(activeOnly: false);
            foreach (var d in rows)
            {
                if (d == null || string.IsNullOrWhiteSpace(d.DumpId)) continue;
                sinks.Add(FromDumpSite(d));
                dumpCount++;
            }
        }
        catch (Exception ex) { dumpErr = Short(ex); }

        // ── ② 装卸点里的卸载点（破碎站 / 煤仓 / 堆场；kind=='unloading'）──
        try
        {
            var rows = EquipmentDataContext.LoadUnloadPoints.All();
            foreach (var p in rows)
            {
                if (p == null) continue;
                if (!string.Equals(p.Kind?.Trim(), "unloading", StringComparison.OrdinalIgnoreCase)) continue;
                sinks.Add(FromLoadUnloadPoint(p));
                lupCount++;
            }
        }
        catch (Exception ex) { lupErr = Short(ex); }

        // ── ③ 一个都没读到 → 回落样例，把原因带出去 ──
        if (sinks.Count == 0)
        {
            string why = dumpErr ?? lupErr ?? "dump_site 与 load_unload_point 均为空表（请先在「排土场管理」/本「去向台账」录入；破碎站也可在「破碎站位置设置」定点）";
            LastSourceLabel = $"样例去向（DB 未接通：{why}）";
            _fromDb = false;
            _current = SinkRegistry.Sample();
            return _current;
        }

        // ── ④ 覆盖扩展档案（V035 sink_profile）：两张本体表放不下的字段在这里补回来。
        //     没有档案行的去向保持工程缺省值——即「从没在台账里编辑过」，不是「被清零」。
        int profCount = 0;
        string? profErr = null;
        try
        {
            var byId = new Dictionary<string, SinkProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in EquipmentDataContext.SinkProfiles.All())
                if (p != null && !string.IsNullOrWhiteSpace(p.SinkId)) byId[p.SinkId] = p;

            foreach (var s in sinks)
                if (byId.TryGetValue(s.Id, out var p)) { ApplyProfile(s, p); profCount++; }
        }
        catch (Exception ex) { profErr = Short(ex); }

        var reg = new SinkRegistry();
        reg.Load(sinks);
        _fromDb = true;
        _current = reg;

        // 部分成功也要说清楚：读到了排土场但装卸点表炸了，运维得知道。
        string detail = $"排土场 {dumpCount} + 卸载点 {lupCount}";
        if (profCount > 0) detail += $" · 扩展档案 {profCount}";
        if (dumpErr != null) detail += $"；排土场读取失败：{dumpErr}";
        if (lupErr != null) detail += $"；装卸点读取失败：{lupErr}";
        if (profErr != null) detail += $"；扩展档案读取失败：{profErr}（可接物料/工作线长/时窗按缺省值）";
        LastSourceLabel = $"排土场台账（DB，{sinks.Count} 个去向 · {detail}）";
        return reg;
    }

    // ── sink_profile → SinkNode（只补两张本体表表达不了的部分）────────────────
    private static void ApplyProfile(SinkNode s, SinkProfile p)
    {
        bool isLup = IsLupRef(s.RefEntityId) || IsLupRef(s.Id);

        // 类型：只作为**细化**采信——同族才覆盖（排弃类↔排弃类、通过型↔通过型）。
        // 理由：行躺在哪张表是物理事实，dump_site 的行不可能是破碎站。档案能表达的是
        // dump_type 表达不了的「表土堆场」、以及 unload_sub 表达不了的「原煤仓」。
        if (Enum.TryParse<SinkKind>(p.SinkKind, ignoreCase: true, out var kind)
            && kind.IsDumping() == s.Kind.IsDumping())
            s.Kind = kind;

        // 状态：排土场以 dump_site.status 为准（那张表自带 status，且「排土场管理」也在改它）；
        // 卸载点表没有 status 列，档案是它唯一的家。
        if (isLup && !string.IsNullOrWhiteSpace(p.Status)) s.Status = p.Status.Trim();

        // 通过能力：卸载点以 load_unload_point.throughput_tph 为准；排土场表没有这一列。
        if (!isLup && p.AcceptTph > 0) s.AcceptTph = p.AcceptTph;

        s.AcceptedMaterials = ParseMaterials(p.AcceptedMaterials);
        if (p.WorkLineLengthM > 0) s.WorkLineLengthM = p.WorkLineLengthM;
        if (p.ActiveBenchLevel > 0) s.ActiveBenchLevel = (int)Math.Min(int.MaxValue, p.ActiveBenchLevel);
        if (p.FallbackHaulKm > 0) s.FallbackHaulKm = p.FallbackHaulKm;

        // 时窗：0/24 就是「全天」，属于有效取值，不能用 >0 过滤，否则改回全天存不下来。
        if (p.OpenToHour > 0 && p.OpenToHour <= 24 && p.OpenFromHour >= 0 && p.OpenFromHour < p.OpenToHour)
        {
            s.OpenFromHour = p.OpenFromHour;
            s.OpenToHour = p.OpenToHour;
        }
        s.OpenFromPeriod = string.IsNullOrWhiteSpace(p.OpenFromPeriod) ? null : p.OpenFromPeriod.Trim();

        // 坐标（V036）：★ 只对排土场生效。
        // 卸载点的坐标以 load_unload_point 为准 —— FromLoadUnloadPoint 已经填过了，
        // 这里要是照抄档案，就会用一份可能过期的副本盖掉本体表的权威值。
        // 排土场则相反：dump_site 没有坐标列，FromDumpSite 留下的是 0，档案是它唯一的来源。
        if (!isLup) { s.X = p.X; s.Y = p.Y; s.Z = p.Z; }
    }

    /// <summary>可接物料白名单：逗号分隔的物料码 → 集合；目录里不存在的码直接丢弃（别让脏码变成"什么都不收"）。</summary>
    private static HashSet<string> ParseMaterials(string? csv)
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

    // ── dump_site → SinkNode ─────────────────────────────────────────────────
    private static SinkNode FromDumpSite(DumpSite d)
    {
        var kind = DumpKindOf(d);
        return new SinkNode
        {
            Id = d.DumpId,
            Name = string.IsNullOrWhiteSpace(d.Name) ? d.DumpId : d.Name,
            Kind = kind,

            // ★ 单位换算：台账【万 m³】→ 契约【m³】。
            DesignCapacityM3 = d.DesignCapacityWanM3 * 1e4,
            FilledM3 = d.CurrentFilledWanM3 * 1e4,

            BenchHeightM = d.BenchHeightM ?? DefaultBenchHeightM,
            BenchSlopeAngleDeg = d.BenchSlopeAngleDeg ?? DefaultBenchSlopeDeg,
            Status = string.IsNullOrWhiteSpace(d.Status) ? "active" : d.Status.Trim(),

            // dump_site 表无坐标列 —— 这里先留 0，坐标由 sink_profile.x/y/z 在 ApplyProfile 里补
            // （V036；「去向台账」的 X/Y/Z 三列就是录它的入口）。档案里也没录 ⇒ 保持 0，
            // HaulResolver 据此判定「无坐标」，否则会把 (0,0,0) 当成真实位置去 NearestNode，
            // 吸附到离原点最近的路网节点上，算出一个看着像真的假运距。
            // 也无工作线长列（WorkLineLengthM=0 ⇒ AdvanceMetersFor 返回 0，三维推进反算暂不可用）。
            FallbackHaulKm = FallbackKmOf(kind),
            RefEntityId = d.DumpId,
        };
    }

    /// <summary>内排 / 外排 / 表土堆场判定：先看 dump_type，名字里含「表土」的单独归为表土堆场。</summary>
    private static SinkKind DumpKindOf(DumpSite d)
    {
        string name = d.Name ?? "";
        // 表土必须单独堆存供复垦，不得混入岩石排土场——名字是目前唯一的判据。
        if (name.Contains("表土") || name.Contains("腐殖")) return SinkKind.TopsoilYard;
        return string.Equals(d.DumpType?.Trim(), "internal", StringComparison.OrdinalIgnoreCase)
            ? SinkKind.InternalDump
            : SinkKind.ExternalDump;
    }

    // ── load_unload_point(kind='unloading') → SinkNode ───────────────────────
    private static SinkNode FromLoadUnloadPoint(LoadUnloadPoint p)
    {
        var kind = UnloadKindOf(p);
        return new SinkNode
        {
            // 前缀隔离命名空间：装卸点主键是自增 long，直接用会和 dump_id 撞车。
            Id = $"LUP-{p.Id}",
            Name = string.IsNullOrWhiteSpace(p.Name) ? $"卸载点{p.Id}" : p.Name,
            Kind = kind,

            // load_unload_point 表没有容量列：破碎站 / 煤仓是「通过型」去向，
            // 卸多少走多少、不占库容，DesignCapacityM3=0 即 RemainingM3=+∞。
            // 堆场/排土类卸载点同样无容量数据，只能按不限处理——真要卡库容，
            // 得把它同时登记进 dump_site。
            DesignCapacityM3 = 0,
            FilledM3 = 0,

            AcceptTph = p.ThroughputTph,
            X = p.X, Y = p.Y, Z = p.Z,       // 装卸点有真实坐标 ⇒ HaulResolver 可按坐标吸附路网节点
            Status = "active",
            FallbackHaulKm = FallbackKmOf(kind),
            RefEntityId = $"LUP-{p.Id}",
        };
    }

    /// <summary>卸载子类映射：名字优先（更贴近现场叫法），其次 unload_sub 列。</summary>
    private static SinkKind UnloadKindOf(LoadUnloadPoint p)
    {
        string name = p.Name ?? "";
        string sub = (p.UnloadSub ?? "").Trim();

        // ① 煤仓：名字里含「煤仓」/「silo」的，不管子类填的什么都按原煤仓算。
        if (name.Contains("煤仓") || name.Contains("silo", StringComparison.OrdinalIgnoreCase))
            return SinkKind.Silo;

        // ② 破碎站
        if (string.Equals(sub, "crusher", StringComparison.OrdinalIgnoreCase) || name.Contains("破碎"))
            return SinkKind.Crusher;

        // ③ 堆场（配矿缓冲 / 低品位暂存）
        if (string.Equals(sub, "stockpile", StringComparison.OrdinalIgnoreCase)
            || name.Contains("堆场") || name.Contains("储煤"))
            return SinkKind.Stockpile;

        // ④ sub=='dump' 或未填 —— 按名字判内外排 / 表土，默认外排土场。
        if (name.Contains("表土") || name.Contains("腐殖")) return SinkKind.TopsoilYard;
        if (name.Contains("内排")) return SinkKind.InternalDump;
        return SinkKind.ExternalDump;
    }

    /// <summary>
    /// 三层兜底最后一层用的缺省运距 km（仅当路网不可解且面上没手填时才生效）。
    /// 按去向类型给量级差异：内排在采空区里，比外排近得多——这正是内排降本的由来。
    /// 台账里录了真值可直接改 SinkNode.FallbackHaulKm 覆盖。
    /// </summary>
    private static double FallbackKmOf(SinkKind kind) => kind switch
    {
        SinkKind.InternalDump => 1.5,
        SinkKind.TopsoilYard => 2.0,
        SinkKind.Crusher => 2.5,
        SinkKind.Stockpile => 2.5,
        SinkKind.Silo => 3.0,
        _ => 3.0,   // 外排土场
    };

    /// <summary>
    /// 实绩回灌：把当期排弃的【占容方 m³】累加进去向。
    /// 内存登记簿一定更新；只有来自 dump_site 的去向才回写台账（÷1e4 折回万 m³）。
    /// DB 不可用时只更新内存、不抛异常——日常派工不能因为写库失败就中断。
    /// 返回该去向回灌后的充填率（0..1）；去向不存在返回 0。
    /// </summary>
    public static double AddFilled(string sinkId, double dumpM3)
    {
        var reg = Current;
        var sink = reg.Find(sinkId);
        if (sink == null) return 0;

        // ① 内存永远先更新（计划推演与 UI 立即看到效果）
        double rate = reg.AddFilled(sinkId, dumpM3);

        // ② 回写台账：破碎站/煤仓（LUP- 前缀）不占库容，无需回写；样例数据也不回写。
        if (_fromDb && sink.IsDumping && !sink.RefEntityId.StartsWith("LUP-", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // ★ 单位换算：契约【m³】→ 台账【万 m³】。
                double wanM3 = dumpM3 / 1e4;
                string dumpId = string.IsNullOrWhiteSpace(sink.RefEntityId) ? sink.Id : sink.RefEntityId;
                rate = EquipmentDataContext.DumpSites.AddFilledVolume(dumpId, wanM3);
            }
            catch
            {
                // 写库失败不影响内存口径，下次 Load() 会以台账为准重新对齐。
            }
        }
        return rate;
    }

    /// <summary>丢弃缓存，下次 Current / Load 重新读台账（台账改动后调用）。</summary>
    public static void Invalidate()
    {
        _current = null;
        _fromDb = false;
        LastSourceLabel = "样例去向（尚未装载）";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  写回台账
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 台账写回结果。UI 要能据此说清三件事：成了几条、败了几条、每一条为什么败。
    /// 「一条都没跑起来」（DB 不可用）与「跑了但某几条失败」是两回事，故单列 <see cref="Aborted"/>。
    /// </summary>
    public sealed class SinkSaveResult
    {
        /// <summary>新建的去向数（dump_site / load_unload_point 里原本没有的行）。</summary>
        public int Inserted { get; internal set; }
        /// <summary>更新的去向数。</summary>
        public int Updated { get; internal set; }
        /// <summary>删除的去向数。</summary>
        public int Deleted { get; internal set; }
        /// <summary>失败的去向数（含"主表写进去了但扩展档案没写成"的半成功）。</summary>
        public int Failed { get; internal set; }

        /// <summary>整体没跑起来（数据库不可用 / 无输入），此时各计数均为 0。</summary>
        public bool Aborted { get; internal set; }
        /// <summary>中止原因（仅 <see cref="Aborted"/> 时有值）。</summary>
        public string AbortReason { get; internal set; } = "";

        /// <summary>逐条失败原因，形如「北排土场：状态取值非法」。</summary>
        public List<string> Errors { get; } = new();
        /// <summary>需要让用户知道、但不算失败的事（新分配的编号、样例数据首次入库等）。</summary>
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

    /// <summary>
    /// 把登记簿整体写回台账：排土场进 dump_site，破碎站/煤仓/堆场进 load_unload_point，
    /// 两张表放不下的字段进 sink_profile。返回逐条结果，不抛异常。
    ///
    /// 【不写「已填」】普通保存永远不动 current_filled_wan_m3——内存里的 FilledM3 叠着当日
    /// 实绩的界面增量，照写会重复计量。改「已填」请走 <see cref="Stocktake"/>。
    /// 新建的去向例外：那是开账初值，必须写一次。
    /// </summary>
    public static SinkSaveResult Save(SinkRegistry? registry)
        => Save(registry?.All);

    /// <summary>写回指定的若干去向（同 <see cref="Save(SinkRegistry)"/>）。</summary>
    public static SinkSaveResult Save(IEnumerable<SinkNode>? sinks)
    {
        var r = new SinkSaveResult();
        var list = sinks?.Where(s => s != null).ToList() ?? new List<SinkNode>();
        if (list.Count == 0) { r.Abort("没有可保存的去向"); return r; }

        if (!TryServices(out var dumps, out var lups, out var profiles, out string why))
        {
            r.Abort(why);
            return r;
        }

        foreach (var s in list) SaveOne(s, dumps!, lups!, profiles!, r);
        return r;
    }

    /// <summary>
    /// 删除一个去向：本体行（dump_site / load_unload_point）+ 扩展档案一起删。
    /// 【调用方须先自行拦截】当日有入方的去向不许删——那样删掉，当日运量就没有落点了。
    /// </summary>
    public static SinkSaveResult Delete(SinkNode? sink)
    {
        var r = new SinkSaveResult();
        if (sink == null || string.IsNullOrWhiteSpace(sink.Id)) { r.Abort("未指定要删除的去向"); return r; }
        if (!TryServices(out var dumps, out var lups, out var profiles, out string why)) { r.Abort(why); return r; }

        string name = string.IsNullOrWhiteSpace(sink.Name) ? sink.Id : sink.Name;
        string reference = string.IsNullOrWhiteSpace(sink.RefEntityId) ? sink.Id : sink.RefEntityId;

        try
        {
            if (IsLupRef(reference))
            {
                if (TryLupId(reference, out long id) && id > 0) lups!.Delete(id);
                else { r.Fail(name, $"卸载点编号解析不出（{reference}），未删除"); return r; }
            }
            else dumps!.Delete(reference);

            r.Deleted++;
        }
        catch (Exception ex) { r.Fail(name, $"删除本体失败：{Short(ex)}"); return r; }

        // 档案删不掉不算失败：本体已经没了，残留档案行不会被任何人读到（读取按本体表驱动）。
        try { profiles!.Delete(sink.Id); }
        catch (Exception ex) { r.Notes.Add($"{name} 的扩展档案未能清除：{Short(ex)}"); }

        return r;
    }

    /// <summary>
    /// 库容盘点修正：显式改「已填」，并往 sink_stocktake 留一条流水（改前/改后/差额/原因）。
    /// 这是唯一允许改 FilledM3 的入口——普通编辑改不了，因为那是实绩累计出来的账。
    /// 成功后同步内存节点，UI 无需重读即可看到新充填率。
    /// </summary>
    public static SinkSaveResult Stocktake(SinkNode? sink, double newFilledM3, string? reason, string? op = null)
    {
        var r = new SinkSaveResult();
        if (sink == null || string.IsNullOrWhiteSpace(sink.Id)) { r.Abort("未指定要盘点的去向"); return r; }
        if (string.IsNullOrWhiteSpace(reason)) { r.Abort("盘点必须填写修正原因（无原因不许改账）"); return r; }
        if (double.IsNaN(newFilledM3) || double.IsInfinity(newFilledM3) || newFilledM3 < 0)
        { r.Abort("盘点后的已填量必须是 ≥0 的数"); return r; }
        if (!sink.IsDumping) { r.Abort("通过型去向（破碎站/煤仓/堆场）不占排土库容，无需盘点"); return r; }

        if (!TryServices(out var dumps, out _, out var profiles, out string why)) { r.Abort(why); return r; }

        string name = string.IsNullOrWhiteSpace(sink.Name) ? sink.Id : sink.Name;
        string dumpId = string.IsNullOrWhiteSpace(sink.RefEntityId) ? sink.Id : sink.RefEntityId;
        if (IsLupRef(dumpId)) { r.Abort("该去向存在 load_unload_point，表里没有库容列，无法盘点"); return r; }

        double before = sink.FilledM3;
        try
        {
            var e = dumps!.Get(dumpId);
            if (e == null) { r.Fail(name, $"台账里找不到排土场 {dumpId}（请先保存台账建档）"); return r; }
            // ★ 单位换算：契约【m³】→ 台账【万 m³】。
            e.CurrentFilledWanM3 = newFilledM3 / 1e4;
            Stamp(e);
            dumps.Upsert(e);
            r.Updated++;
        }
        catch (Exception ex) { r.Fail(name, $"改账失败：{Short(ex)}"); return r; }

        // 流水写不进去要报出来：账改了却没留痕，比不改更糟——用户得知道这次修正没有凭据。
        try
        {
            profiles!.AddStocktake(new SinkStocktake
            {
                SinkId = sink.Id,
                SinkName = name,
                BeforeFilledM3 = before,
                AfterFilledM3 = newFilledM3,
                DeltaM3 = newFilledM3 - before,
                Reason = reason!.Trim(),
                Operator = (string.IsNullOrWhiteSpace(op) ? Environment.UserName : op!).Trim(),
            });
        }
        catch (Exception ex) { r.Notes.Add($"账已改，但盘点流水未能记录：{Short(ex)}"); }

        sink.FilledM3 = newFilledM3;      // 内存同步，UI 立刻看到新充填率
        return r;
    }

    /// <summary>
    /// 造一个还没入库的新去向（「新增去向」按钮用）。排土类给 dump_site 风格的编号，
    /// 通过型给占位编号——真编号是 load_unload_point 的自增主键，只有 INSERT 之后才知道。
    /// </summary>
    public static SinkNode CreateNew(SinkKind kind, string? name = null)
    {
        _newSeq++;
        bool dumping = kind.IsDumping();
        string id = dumping
            ? $"D-{DateTime.Now:yyMMddHHmm}{_newSeq:00}"
            : $"LUP-新{_newSeq}";

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

    // ── 写回内部实现 ─────────────────────────────────────────────────────────

    /// <summary>一次性探活三个服务：要么全都拿得到，要么整体中止，绝不做一半。</summary>
    private static bool TryServices(
        out IDumpSiteService? dumps, out ILoadUnloadPointService? lups,
        out ISinkProfileService? profiles, out string why)
    {
        dumps = null; lups = null; profiles = null; why = "";
        try
        {
            var ctx = EquipmentDataContext.Current;
            dumps = ctx.DumpSites;
            lups = ctx.LoadUnloadPoints;
            profiles = ctx.SinkProfiles;
            if (dumps == null || lups == null || profiles == null)
            {
                why = "数据库服务未注册（GeoDataBase 未就绪）";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            why = $"数据库未接通（{Short(ex)}）";
            return false;
        }
    }

    private static void SaveOne(
        SinkNode s, IDumpSiteService dumps, ILoadUnloadPointService lups,
        ISinkProfileService profiles, SinkSaveResult r)
    {
        string id = (s.Id ?? "").Trim();
        string name = string.IsNullOrWhiteSpace(s.Name) ? id : s.Name.Trim();
        if (id.Length == 0) { r.Fail(string.IsNullOrWhiteSpace(name) ? "（无名去向）" : name, "去向编号为空，无法定位台账行"); return; }

        string? status = NormalizeStatus(s.Status);
        if (status == null) { r.Fail(name, $"状态取值非法（{s.Status}）——只能是 在用/已排满/已关闭"); return; }

        if (s.DesignCapacityM3 < 0 || double.IsNaN(s.DesignCapacityM3)) { r.Fail(name, "设计容量必须 ≥0"); return; }
        if (s.AcceptTph < 0 || double.IsNaN(s.AcceptTph)) { r.Fail(name, "通过能力必须 ≥0"); return; }

        // 本体行在哪张表：RefEntityId 是权威（读进来时写好的）；空 = 新建，按类型选表。
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
                ? SaveLoadUnloadPoint(s, name, lups, r, out isNew)
                : SaveDumpSite(s, name, status, dumps, out isNew);
        }
        catch (Exception ex) { r.Fail(name, $"写本体失败：{Short(ex)}"); return; }

        if (!bodyOk) return;

        // 扩展档案：两张本体表放不下的部分。写不进去要算失败——否则用户改的"可接物料/时窗"
        // 会在下次重读时悄悄回到旧值，而界面刚刚提示过"保存成功"。
        // 计数放在这之后：本体成了、档案没成，这条只能算失败（半成功如实报，不许四舍五入成功）。
        try { profiles.Upsert(ToProfile(s, status)); }
        catch (Exception ex)
        {
            r.Fail(name, $"本体已写入，但扩展档案（可接物料/工作线长/时窗）失败：{Short(ex)}");
            return;
        }

        if (isNew) r.Inserted++; else r.Updated++;
    }

    /// <summary>SinkNode → dump_site。★ 单位换算：契约【m³】→ 台账【万 m³】（÷1e4）。</summary>
    private static bool SaveDumpSite(SinkNode s, string name, string status, IDumpSiteService dumps, out bool isNew)
    {
        string dumpId = string.IsNullOrWhiteSpace(s.RefEntityId) ? s.Id.Trim() : s.RefEntityId.Trim();
        var e = dumps.Get(dumpId);
        isNew = e == null;

        if (e == null)
        {
            e = new DumpSite
            {
                DumpId = dumpId,
                StartDate = DateTime.Today,
                // 开账初值：只有新建时才写「已填」，之后一律交给实绩回灌与盘点。
                CurrentFilledWanM3 = Math.Max(0, s.FilledM3) / 1e4,
                CreatedAt = DateTime.Now,
            };
        }

        e.Name = name;
        // dump_type 只有 internal/external 两个合法值（表上有 CHECK）；表土堆场落 external，
        // 「表土堆场」这层细分由 sink_profile.sink_kind 保存，读回时再细化。
        e.DumpType = s.Kind == SinkKind.InternalDump ? "internal" : "external";
        e.DesignCapacityWanM3 = Math.Max(0, s.DesignCapacityM3) / 1e4;
        if (s.BenchHeightM > 0) e.BenchHeightM = s.BenchHeightM;
        if (s.BenchSlopeAngleDeg > 0) e.BenchSlopeAngleDeg = s.BenchSlopeAngleDeg;
        e.Status = status;
        Stamp(e);

        dumps.Upsert(e);
        s.RefEntityId = dumpId;
        return true;
    }

    /// <summary>SinkNode → load_unload_point（通过型去向：名称 / 通过能力 / 坐标 / 卸载子类）。</summary>
    private static bool SaveLoadUnloadPoint(
        SinkNode s, string name, ILoadUnloadPointService lups, SinkSaveResult r, out bool isNew)
    {
        bool hasId = TryLupId(string.IsNullOrWhiteSpace(s.RefEntityId) ? s.Id : s.RefEntityId, out long id) && id > 0;
        var e = hasId ? lups.Get(id) : null;
        isNew = e == null;

        if (e == null)
        {
            // 新建：主键是自增 long，只有插进去才知道编号，故插完必须把 Id/RefEntityId 回填，
            // 否则这一条会在下次保存时被当成"又一个新点"再插一遍。
            var fresh = new LoadUnloadPoint
            {
                Name = name,
                Kind = "unloading",
                UnloadSub = SubCodeOf(s.Kind),
                X = s.X, Y = s.Y, Z = s.Z,
                ThroughputTph = Math.Max(0, s.AcceptTph),
                Visible = 1,
            };
            long newId = lups.Insert(fresh);
            if (newId <= 0) { r.Fail(name, "卸载点插入未返回编号"); return false; }

            s.Id = $"LUP-{newId}";
            s.RefEntityId = s.Id;
            r.Notes.Add($"{name} 已建档，编号 {s.Id}");
            return true;
        }

        e.Name = name;
        e.Kind = "unloading";
        e.UnloadSub = SubCodeOf(s.Kind);
        e.X = s.X; e.Y = s.Y; e.Z = s.Z;
        e.ThroughputTph = Math.Max(0, s.AcceptTph);
        lups.Update(e);
        s.RefEntityId = $"LUP-{id}";
        return true;
    }

    /// <summary>SinkNode → sink_profile（两张本体表都没有列的那些字段）。</summary>
    private static SinkProfile ToProfile(SinkNode s, string status) => new()
    {
        SinkId = s.Id.Trim(),
        SinkKind = s.Kind.ToString(),
        Status = status,
        AcceptTph = Math.Max(0, s.AcceptTph),
        AcceptedMaterials = string.Join(",", s.AcceptedMaterials.Where(MaterialCatalog.Exists)),
        WorkLineLengthM = Math.Max(0, s.WorkLineLengthM),
        ActiveBenchLevel = Math.Max(1, s.ActiveBenchLevel),
        FallbackHaulKm = Math.Max(0, s.FallbackHaulKm),
        OpenFromHour = Clamp24(s.OpenFromHour, 0),
        OpenToHour = Clamp24(s.OpenToHour, 24),
        OpenFromPeriod = string.IsNullOrWhiteSpace(s.OpenFromPeriod) ? null : s.OpenFromPeriod!.Trim(),

        // 坐标（V036）：两族都写一份。读回时只有排土场采信本表（见 ApplyProfile），
        // 卸载点的权威在 load_unload_point —— 但档案里同样存一份，
        // 一来不至于让卸载点的档案行留一串 0 看着像数据丢了，二来两边对不上时有据可查。
        X = Sane(s.X), Y = Sane(s.Y), Z = Sane(s.Z),
    };

    /// <summary>坐标兜底：NaN/无穷一律记 0（= 未录坐标），别让脏值进库再被当成真实位置吸附路网。</summary>
    private static double Sane(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    private static double Clamp24(double v, double fallback)
        => double.IsNaN(v) || v < 0 || v > 24 ? fallback : v;

    /// <summary>状态归一化：中文/英文都收，非法值返回 null（dump_site.status 上有 CHECK 约束，不能乱写）。</summary>
    private static string? NormalizeStatus(string? raw)
    {
        string v = (raw ?? "").Trim();
        if (v.Length == 0) return "active";
        if (v.Equals("active", StringComparison.OrdinalIgnoreCase) || v == "在用" || v == "启用") return "active";
        if (v.Equals("full", StringComparison.OrdinalIgnoreCase) || v == "已排满" || v == "排满") return "full";
        if (v.Equals("closed", StringComparison.OrdinalIgnoreCase) || v == "已关闭" || v == "关闭") return "closed";
        return null;
    }

    /// <summary>
    /// SinkKind → load_unload_point.unload_sub。词表沿用 RoadLib「装卸点设置」的
    /// crusher/stockpile/dump，外加 PlanLib 已识别的 silo；未知一律 dump。
    /// </summary>
    private static string SubCodeOf(SinkKind k) => k switch
    {
        SinkKind.Crusher => "crusher",
        SinkKind.Stockpile => "stockpile",
        SinkKind.Silo => "silo",
        _ => "dump",
    };

    private static void Stamp(DumpSite e)
    {
        e.CreatedAt ??= DateTime.Now;     // created_at 是 NOT NULL，插入时不能给 NULL
        e.UpdatedAt = DateTime.Now;       // 表上还有 AFTER UPDATE 触发器兜底
    }

    private static bool IsLupRef(string? s)
        => !string.IsNullOrWhiteSpace(s) && s!.StartsWith("LUP-", StringComparison.OrdinalIgnoreCase);

    private static bool TryLupId(string? s, out long id)
    {
        id = 0;
        if (!IsLupRef(s)) return false;
        return long.TryParse(s!.Substring(4).Trim(), out id);
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
