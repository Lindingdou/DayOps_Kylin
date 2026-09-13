// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/CrewLookup.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  派工结果的**唯一读出口**。
//
//  在它出现之前「班组派工」是一条断链：CrewAssignment 落了盘，而 LoadCrew 除派工窗自己
//  之外零调用方 —— 任务书上没有操作手、派车单组头只有车号没有司机、看板设备卡不知道当班是谁。
//  字段级的证据是 CertNote / AttendNote / EquipCategory / GroupCaption 四个**写了零读**。
//
//  这里把"谁在这台设备上、谁开这辆车"收成一处，三个消费方读同一份，不各查各的。
//
//  两条口径：
//   ① 车号→司机优先读 CrewAssignment.TruckDrivers（派工时定下的显式配对）。
//      旧档案没有那一段时，按 Drivers 与编组配车顺序**逐位配对**兜底 —— 这是推出来的，
//      故 FromExplicitPairing=false，界面据此可以标注"推定"，不假装是派工时定的。
//   ② 读不到派工就返回空壳（IsEmpty=true），调用方显示"未派工"，
//      **不要回落成"（待派）"之类的文案冒充已派** —— 没派就是没派。
// ─────────────────────────────────────────────────────────────────────────────
public static class CrewLookup
{
    private static readonly Dictionary<string, CrewShift> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>某日某班的派工。结果带缓存；派工窗保存后调 <see cref="Invalidate"/>。</summary>
    public static CrewShift Of(string? date, string? shift)
    {
        string key = $"{TaskPersistence.DateKey(date)}|{(shift ?? "").Trim()}";
        if (_cache.TryGetValue(key, out var hit)) return hit;

        List<CrewAssignment> rows;
        try { rows = TaskPersistence.LoadCrew(date ?? "", shift ?? ""); }
        catch { rows = new List<CrewAssignment>(); }

        var cs = new CrewShift(rows);
        _cache[key] = cs;
        return cs;
    }

    /// <summary>丢弃缓存（派工保存 / 换作业日后调）。</summary>
    public static void Invalidate() => _cache.Clear();
}

/// <summary>一个班的派工视图：主设备→操作手、车号→司机。</summary>
public sealed class CrewShift
{
    private readonly Dictionary<string, CrewAssignment> _byEquip = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _driverByTruck = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本班有没有派过工。false ⇒ 调用方显示"未派工"，不要编一个名字出来。</summary>
    public bool IsEmpty => _byEquip.Count == 0;

    /// <summary>车号→司机是不是派工时定下的显式配对（false = 按配车顺序推定的）。</summary>
    public bool FromExplicitPairing { get; }

    public CrewShift(IEnumerable<CrewAssignment> rows)
    {
        bool explicitPairs = true;
        foreach (var a in rows)
        {
            if (string.IsNullOrWhiteSpace(a.MainEquipment)) continue;
            _byEquip[a.MainEquipment] = a;

            if (a.TruckDrivers is { Count: > 0 })
            {
                foreach (var td in a.TruckDrivers)
                    if (!string.IsNullOrWhiteSpace(td.TruckId) && !string.IsNullOrWhiteSpace(td.Name))
                        _driverByTruck[td.TruckId] = td.Name;
            }
            else if (a.Drivers is { Count: > 0 })
            {
                explicitPairs = false;   // 旧档案：只有名字列表，车号得靠外部按顺序补
            }
        }
        FromExplicitPairing = explicitPairs;
    }

    /// <summary>这台主设备的操作手；没派到返回空串。</summary>
    public string OperatorOf(string? mainEquip)
        => mainEquip != null && _byEquip.TryGetValue(mainEquip, out var a) ? a.Operator ?? "" : "";

    /// <summary>这台主设备配的司机名单（顺序即配车顺序）。</summary>
    public IReadOnlyList<string> DriversOf(string? mainEquip)
        => mainEquip != null && _byEquip.TryGetValue(mainEquip, out var a)
            ? (a.TruckDrivers is { Count: > 0 } ? a.TruckDrivers.Select(t => t.Name).ToList() : a.Drivers)
            : Array.Empty<string>();

    /// <summary>持证/出勤校核结论（派工时算好的，别在消费侧重算一遍——重算就会与派工窗打架）。</summary>
    public string CertNoteOf(string? mainEquip)
        => mainEquip != null && _byEquip.TryGetValue(mainEquip, out var a) ? a.CertNote ?? "" : "";

    public string AttendNoteOf(string? mainEquip)
        => mainEquip != null && _byEquip.TryGetValue(mainEquip, out var a) ? a.AttendNote ?? "" : "";

    /// <summary>
    /// 这辆车谁开。优先显式配对；没有就按 <paramref name="groupTrucks"/> 的顺序在名单里取同一位
    /// （旧档案的兜底，调用方可用 <see cref="FromExplicitPairing"/> 标注"推定"）。
    /// 认不出返回空串。
    /// </summary>
    public string DriverOf(string? truckId, string? mainEquip = null, IReadOnlyList<string>? groupTrucks = null)
    {
        if (string.IsNullOrWhiteSpace(truckId)) return "";
        if (_driverByTruck.TryGetValue(truckId, out var name)) return name;

        if (groupTrucks == null || mainEquip == null) return "";
        int i = -1;
        for (int k = 0; k < groupTrucks.Count; k++)
            if (string.Equals(groupTrucks[k], truckId, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        if (i < 0) return "";

        var list = DriversOf(mainEquip);
        return i < list.Count ? list[i] : "";
    }

    /// <summary>"张建国 ＋司机 孙宝山/吴建军" —— 单据上的一句话；没派工返回空串。</summary>
    public string CaptionOf(string? mainEquip)
    {
        string op = OperatorOf(mainEquip);
        var dr = DriversOf(mainEquip).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (op.Length == 0 && dr.Count == 0) return "";
        return (op.Length > 0 ? op : "操作手待派")
             + (dr.Count > 0 ? $"　司机 {string.Join("/", dr)}" : "");
    }
}
