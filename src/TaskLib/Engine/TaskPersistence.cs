// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/TaskPersistence.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  本地存储层 —— 执行期的一切「事实」都要落盘，否则关窗即失。
//
//  原先这里只有 ExploderResult 的一份 JSON 快照，且唯一调用方（报表窗）没有 Ribbon 入口，
//  等于用户点不到。本轮扩成完整的六类落盘，任务下达 / 派车单 / 回执 / 实绩 / 故障 / 派工
//  各有其目录，全部走 %LOCALAPPDATA%/PitMine/ 下的 JSON —— 不引 SQLite，保持自包含。
//
//  目录结构（根 = %LOCALAPPDATA%/PitMine/）：
//    task_snapshots/    任务计划_{日期}.json          计划快照（原有，签名兼容保留）
//    task_instances/    {日期}_{班次}.json            任务实例（下达单据 + 版本）
//    dispatch_orders/   {日期}_{班次}.json            派车单（车次级）
//    dispatch_receipts/ {日期}.json                   回执流水（追加式，只增不改）
//    actuals/           {日期}_{班次}.json            班末实绩
//    fault_events/      {日期}.json                   故障记录
//    crew/roster.json                                 人员花名册（首次运行生成样例）
//    crew/{日期}_{班次}.json                          班组派工
//
//  容错三原则（现场断网/换机/文件被记事本改坏都不许让派工中断）：
//    ① 目录不存在自动建；② 文件损坏/半截 JSON 一律返回空集合而不抛；③ 写盘失败向上报 false 而不抛。
//
//  合并语义：Save 覆盖整份，Append 按业务主键 upsert（实例按 StableKey、指令按 OrderId、
//  实绩按 StableKey、故障按 EventId、派工按主设备），回执永远纯追加（单据流水不许被改写）。
// ─────────────────────────────────────────────────────────────────────────────
public static class TaskPersistence
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>最近一次读写的结果文案（供 UI 显示"到底存到哪了/为什么没存上"）。</summary>
    public static string LastIoLabel { get; private set; } = "";

    // ═════════════════════════════════════════════════════════════════════════
    //  路径
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 存储根覆盖。<b>只给判据台架用</b> —— 链闭合台架要写快照/单据/实绩，
    /// 而那些是用户本人的生产数据；写进 <c>%LOCALAPPDATA%\PitMine</c> 就等于
    /// 拿造出来的数污染真台账，而且下一次开软件时它们看着和真的一模一样。
    /// <para>null = 走 <c>%LOCALAPPDATA%\PitMine</c>（生产路径）。</para>
    /// </summary>
    public static string? RootOverride { get; set; }

    /// <summary>存储根目录 %LOCALAPPDATA%/PitMine（不存在则创建）。</summary>
    public static string RootDir() => EnsureDir(
        !string.IsNullOrWhiteSpace(RootOverride)
            ? RootOverride!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitMine"));

    /// <summary>快照根目录（不存在则创建）。</summary>
    public static string SnapshotDir() => EnsureDir(Path.Combine(RootDir(), "task_snapshots"));

    public static string InstanceDir() => EnsureDir(Path.Combine(RootDir(), "task_instances"));
    public static string OrderDir() => EnsureDir(Path.Combine(RootDir(), "dispatch_orders"));
    public static string ReceiptDir() => EnsureDir(Path.Combine(RootDir(), "dispatch_receipts"));
    public static string ActualDir() => EnsureDir(Path.Combine(RootDir(), "actuals"));
    public static string FaultDir() => EnsureDir(Path.Combine(RootDir(), "fault_events"));
    public static string CrewDir() => EnsureDir(Path.Combine(RootDir(), "crew"));

    /// <summary>
    /// 按日期标签生成默认快照路径，如「任务计划_2026-06-17 周二.json」。
    /// ★ 命名保持原样（空格不替换）：<c>DailyGanttWindow.LoadSnapshot</c> 取第一个下划线之后的整串
    /// 当作日期标签再解析，把空格换成下划线会让它解不出日期，翻页看图直接失效。
    /// 新增的六类单据另走 <see cref="Safe"/>，不共用这条命名规则。
    /// </summary>
    public static string DefaultPath(string dateLabel)
    {
        var safe = string.Join("_", (dateLabel ?? "plan").Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(SnapshotDir(), $"任务计划_{safe}.json");
    }

    /// <summary>文件名安全化（新增单据用）：非法字符与空格一律换成下划线。</summary>
    public static string Safe(string? s)
    {
        string v = (s ?? "").Trim();
        if (v.Length == 0) return "unnamed";
        foreach (char c in Path.GetInvalidFileNameChars()) v = v.Replace(c, '_');
        return v.Replace(' ', '_');
    }

    /// <summary>日期键：取日期标签的首个片段（与 <see cref="TaskKey.DateTag"/> 同口径）。</summary>
    public static string DateKey(string? dateLabel) => TaskKey.DateTag(dateLabel);

    /// <summary>{日期}_{班次} 文件名（班次为空时用 "全天"）。</summary>
    private static string ShiftFile(string? date, string? shift)
        => $"{DateKey(date)}_{Safe(string.IsNullOrWhiteSpace(shift) ? "全天" : shift)}.json";

    // ═════════════════════════════════════════════════════════════════════════
    //  ① 计划快照（原有 API，签名兼容）
    // ═════════════════════════════════════════════════════════════════════════

    public static void Save(string path, ExploderResult result)
        => File.WriteAllText(path, JsonSerializer.Serialize(result, Opt));

    public static ExploderResult? Load(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<ExploderResult>(File.ReadAllText(path), Opt) : null;

    /// <summary>快照目录下已存的计划文件（全路径，按修改时间倒序）。</summary>
    public static List<string> ListSnapshots() => ListFiles(SnapshotDir());

    // ═════════════════════════════════════════════════════════════════════════
    //  ② 任务实例
    // ═════════════════════════════════════════════════════════════════════════

    public static bool SaveInstances(string date, string shift, IEnumerable<TaskInstance> items)
        => WriteJson(Path.Combine(InstanceDir(), ShiftFile(date, shift)), items.ToList());

    public static List<TaskInstance> LoadInstances(string date, string shift)
        => ReadJson<List<TaskInstance>>(Path.Combine(InstanceDir(), ShiftFile(date, shift))) ?? new();

    /// <summary>
    /// 追加/更新任务实例：按 <see cref="TaskInstance.StableKey"/> upsert。
    /// 键已存在时新记录覆盖旧记录（版本号由调用方在 upsert 前 +1，本层不擅自改业务字段）。
    /// </summary>
    public static bool AppendInstances(string date, string shift, IEnumerable<TaskInstance> items)
    {
        var cur = LoadInstances(date, shift);
        foreach (var it in items)
        {
            int i = cur.FindIndex(x => string.Equals(x.StableKey, it.StableKey, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) cur[i] = it; else cur.Add(it);
        }
        return SaveInstances(date, shift, cur);
    }

    /// <summary>已有实例文件的 (日期,班次) 清单。</summary>
    public static List<(string Date, string Shift)> ListInstances() => ListShiftKeys(InstanceDir());

    /// <summary>某日全部班次的实例（下达状态汇总用）。</summary>
    public static List<TaskInstance> LoadInstancesOfDay(string date)
    {
        var all = new List<TaskInstance>();
        foreach (var (d, s) in ListInstances())
            if (string.Equals(d, DateKey(date), StringComparison.OrdinalIgnoreCase))
                all.AddRange(LoadInstances(date, s));
        return all;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ③ 派车单
    // ═════════════════════════════════════════════════════════════════════════

    public static bool SaveOrders(string date, string shift, IEnumerable<DispatchOrder> items)
        => WriteJson(Path.Combine(OrderDir(), ShiftFile(date, shift)), items.ToList());

    public static List<DispatchOrder> LoadOrders(string date, string shift)
        => ReadJson<List<DispatchOrder>>(Path.Combine(OrderDir(), ShiftFile(date, shift))) ?? new();

    /// <summary>按 OrderId upsert（车次状态回填走这条路，不会把整份派车单冲掉）。</summary>
    public static bool AppendOrders(string date, string shift, IEnumerable<DispatchOrder> items)
    {
        var cur = LoadOrders(date, shift);
        foreach (var it in items)
        {
            int i = cur.FindIndex(x => string.Equals(x.OrderId, it.OrderId, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) cur[i] = it; else cur.Add(it);
        }
        return SaveOrders(date, shift, cur);
    }

    public static List<(string Date, string Shift)> ListOrders() => ListShiftKeys(OrderDir());

    /// <summary>某日全部班次的派车单（看板的车次执行进度用）。</summary>
    public static List<DispatchOrder> LoadOrdersOfDay(string date)
    {
        var all = new List<DispatchOrder>();
        foreach (var (d, s) in ListOrders())
            if (string.Equals(d, DateKey(date), StringComparison.OrdinalIgnoreCase))
                all.AddRange(LoadOrders(date, s));
        return all;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ④ 回执（追加式流水，只增不改）
    // ═════════════════════════════════════════════════════════════════════════

    private static string ReceiptPath(string date) => Path.Combine(ReceiptDir(), $"{DateKey(date)}.json");

    public static bool SaveReceipts(string date, IEnumerable<DispatchReceipt> items)
        => WriteJson(ReceiptPath(date), items.ToList());

    public static List<DispatchReceipt> LoadReceipts(string date)
        => ReadJson<List<DispatchReceipt>>(ReceiptPath(date)) ?? new();

    /// <summary>追加回执（纯追加：单据流水不许被改写，这正是"下达了没有"可查的根据）。</summary>
    public static bool AppendReceipts(string date, IEnumerable<DispatchReceipt> items)
    {
        var cur = LoadReceipts(date);
        cur.AddRange(items);
        return SaveReceipts(date, cur);
    }

    public static bool AppendReceipt(string date, DispatchReceipt item)
        => AppendReceipts(date, new[] { item });

    /// <summary>已有回执文件的日期清单。</summary>
    public static List<string> ListReceipts() => ListDateKeys(ReceiptDir());

    // ═════════════════════════════════════════════════════════════════════════
    //  ⑤ 实绩
    // ═════════════════════════════════════════════════════════════════════════

    public static bool SaveActuals(string date, string shift, IEnumerable<ActualRecord> items)
        => WriteJson(Path.Combine(ActualDir(), ShiftFile(date, shift)), items.ToList());

    public static List<ActualRecord> LoadActuals(string date, string shift)
        => ReadJson<List<ActualRecord>>(Path.Combine(ActualDir(), ShiftFile(date, shift))) ?? new();

    /// <summary>按稳定键 upsert —— 同一条任务反复录入只留最后一次，不会累计成假账。</summary>
    public static bool AppendActuals(string date, string shift, IEnumerable<ActualRecord> items)
    {
        var cur = LoadActuals(date, shift);
        foreach (var it in items)
        {
            int i = cur.FindIndex(x => string.Equals(x.StableKey, it.StableKey, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) cur[i] = it; else cur.Add(it);
        }
        return SaveActuals(date, shift, cur);
    }

    public static List<(string Date, string Shift)> ListActuals() => ListShiftKeys(ActualDir());

    public static List<ActualRecord> LoadActualsOfDay(string date)
    {
        var all = new List<ActualRecord>();
        foreach (var (d, s) in ListActuals())
            if (string.Equals(d, DateKey(date), StringComparison.OrdinalIgnoreCase))
                all.AddRange(LoadActuals(date, s));
        return all;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ⑥ 故障记录
    // ═════════════════════════════════════════════════════════════════════════

    private static string FaultPath(string date) => Path.Combine(FaultDir(), $"{DateKey(date)}.json");

    public static bool SaveFaults(string date, IEnumerable<FaultEvent> items)
        => WriteJson(FaultPath(date), items.ToList());

    public static List<FaultEvent> LoadFaults(string date)
        => ReadJson<List<FaultEvent>>(FaultPath(date)) ?? new();

    /// <summary>按 EventId upsert（复机是改同一条记录的状态，不是新开一条）。</summary>
    public static bool AppendFaults(string date, IEnumerable<FaultEvent> items)
    {
        var cur = LoadFaults(date);
        foreach (var it in items)
        {
            int i = cur.FindIndex(x => string.Equals(x.EventId, it.EventId, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) cur[i] = it; else cur.Add(it);
        }
        return SaveFaults(date, cur);
    }

    public static bool AppendFault(string date, FaultEvent item) => AppendFaults(date, new[] { item });

    public static List<string> ListFaults() => ListDateKeys(FaultDir());

    // ═════════════════════════════════════════════════════════════════════════
    //  ⑦ 班组花名册与派工
    // ═════════════════════════════════════════════════════════════════════════

    private static string RosterPath() => Path.Combine(CrewDir(), "roster.json");

    /// <summary>
    /// 读人员花名册。首次运行（文件不存在）自动生成一份样例花名册并写盘——
    /// 让"派工"这件事一开箱就有人可派，而不是永远显示"（待派）"。
    /// 接入 GeoDataBase 人员台账后，把 <see cref="SaveRoster"/> 换成台账灌入即可。
    /// </summary>
    public static List<CrewMember> LoadRoster()
    {
        var list = ReadJson<List<CrewMember>>(RosterPath());
        if (list is { Count: > 0 }) return list;

        var sample = SampleRoster();
        SaveRoster(sample);
        LastIoLabel = $"首次运行：已生成样例花名册 {sample.Count} 人 → {RosterPath()}";
        return sample;
    }

    public static bool SaveRoster(IEnumerable<CrewMember> items)
        => WriteJson(RosterPath(), items.ToList());

    /// <summary>内置样例花名册（操作手按设备类别持证，卡车司机若干，含一名休班者演示考勤校核）。</summary>
    private static List<CrewMember> SampleRoster() => new()
    {
        new CrewMember { PersonId = "P-001", Name = "张建国", Job = "操作手", CertFor = "电铲", Phone = "13800000001" },
        new CrewMember { PersonId = "P-002", Name = "李卫东", Job = "操作手", CertFor = "电铲", Phone = "13800000002" },
        new CrewMember { PersonId = "P-003", Name = "王振华", Job = "操作手", CertFor = "电铲", Phone = "13800000003" },
        new CrewMember { PersonId = "P-004", Name = "赵国强", Job = "操作手", CertFor = "液压铲", Phone = "13800000004" },
        new CrewMember { PersonId = "P-005", Name = "刘志刚", Job = "操作手", CertFor = "钻机", Phone = "13800000005" },
        new CrewMember { PersonId = "P-006", Name = "陈立新", Job = "操作手", CertFor = "推土机", Phone = "13800000006" },
        new CrewMember { PersonId = "P-007", Name = "杨永年", Job = "操作手", CertFor = "推土机", Phone = "13800000007" },
        new CrewMember { PersonId = "P-008", Name = "周海涛", Job = "操作手", CertFor = "电铲", OnDuty = false, Phone = "13800000008" },
        new CrewMember { PersonId = "P-101", Name = "孙宝山", Job = "司机", CertFor = "矿卡", Phone = "13900000001" },
        new CrewMember { PersonId = "P-102", Name = "吴建军", Job = "司机", CertFor = "矿卡", Phone = "13900000002" },
        new CrewMember { PersonId = "P-103", Name = "郑德福", Job = "司机", CertFor = "矿卡", Phone = "13900000003" },
        new CrewMember { PersonId = "P-104", Name = "冯少华", Job = "司机", CertFor = "矿卡", Phone = "13900000004" },
        new CrewMember { PersonId = "P-105", Name = "蒋文斌", Job = "司机", CertFor = "矿卡", Phone = "13900000005" },
        new CrewMember { PersonId = "P-106", Name = "许长胜", Job = "司机", CertFor = "矿卡", Phone = "13900000006" },
        new CrewMember { PersonId = "P-107", Name = "何玉良", Job = "司机", CertFor = "矿卡", Phone = "13900000007" },
        new CrewMember { PersonId = "P-108", Name = "邓春生", Job = "司机", CertFor = "矿卡", Phone = "13900000008" },
        new CrewMember { PersonId = "P-201", Name = "曹红兵", Job = "辅助", CertFor = "洒水车", Phone = "13700000001" },
    };

    public static bool SaveCrew(string date, string shift, IEnumerable<CrewAssignment> items)
        => WriteJson(Path.Combine(CrewDir(), ShiftFile(date, shift)), items.ToList());

    public static List<CrewAssignment> LoadCrew(string date, string shift)
        => ReadJson<List<CrewAssignment>>(Path.Combine(CrewDir(), ShiftFile(date, shift))) ?? new();

    /// <summary>按主设备 upsert。</summary>
    public static bool AppendCrew(string date, string shift, IEnumerable<CrewAssignment> items)
    {
        var cur = LoadCrew(date, shift);
        foreach (var it in items)
        {
            int i = cur.FindIndex(x => string.Equals(x.MainEquipment, it.MainEquipment, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) cur[i] = it; else cur.Add(it);
        }
        return SaveCrew(date, shift, cur);
    }

    /// <summary>已有派工文件的 (日期,班次) 清单（roster.json 不计入）。</summary>
    public static List<(string Date, string Shift)> ListCrew()
        => ListShiftKeys(CrewDir()).Where(k => !string.Equals(k.Date, "roster", StringComparison.OrdinalIgnoreCase)).ToList();

    // ═════════════════════════════════════════════════════════════════════════
    //  容错 IO
    // ═════════════════════════════════════════════════════════════════════════

    private static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* 只读盘/权限不足 → 由写盘处报错 */ }
        return dir;
    }

    /// <summary>写 JSON。失败只记文案返回 false，绝不抛——派工不能因为写盘失败就中断。</summary>
    private static bool WriteJson<T>(string path, T value)
    {
        try
        {
            EnsureDir(Path.GetDirectoryName(path) ?? RootDir());
            File.WriteAllText(path, JsonSerializer.Serialize(value, Opt));
            LastIoLabel = $"已写入 {path}";
            return true;
        }
        catch (Exception ex)
        {
            LastIoLabel = $"写盘失败（{Short(ex)}）：{path}";
            return false;
        }
    }

    /// <summary>读 JSON。文件不存在/损坏/半截一律返回 default 而不抛。</summary>
    private static T? ReadJson<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return default;
            return JsonSerializer.Deserialize<T>(text, Opt);
        }
        catch (Exception ex)
        {
            LastIoLabel = $"读盘失败（{Short(ex)}）：{path}";
            return default;
        }
    }

    private static List<string> ListFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.json")
                            .OrderByDescending(File.GetLastWriteTime)
                            .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>目录下 {日期}_{班次}.json 的键清单。</summary>
    private static List<(string Date, string Shift)> ListShiftKeys(string dir)
    {
        var res = new List<(string Date, string Shift)>();
        foreach (var f in ListFiles(dir))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            int i = name.LastIndexOf('_');
            if (i <= 0) { res.Add((name, "")); continue; }
            res.Add((name[..i], name[(i + 1)..]));
        }
        return res;
    }

    /// <summary>目录下 {日期}.json 的日期清单。</summary>
    private static List<string> ListDateKeys(string dir)
        => ListFiles(dir).Select(Path.GetFileNameWithoutExtension).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
