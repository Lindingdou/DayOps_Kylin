using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 花名册与派工记录的落盘（对应原版 <c>%LOCALAPPDATA%/PitMine/crew/</c> 那两类文件）。
///
/// <para>
/// <b>★ 不生成样例花名册</b>（与原版的差异，登记）：原版首次运行会写一份样例 roster.json。
/// Kylin 不编 —— 编出来的是<b>人名</b>，会被当成真人派工、写进单据、发到班组。
/// 花名册为空就是空，界面如实说"先录人"并给补法（同 §三五四 那条"样例不兜底"，
/// 而这一条比作业面还硬：假的面只是数字不对，假的人是发给不存在的人干活）。
/// </para>
///
/// 派工按 <c>日期|班次|主设备</c> 覆盖（一台设备一个班派给谁，只有一个答案）。
/// </summary>
public static class CrewStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>最近一次读写出的问题（空 = 没问题）。<b>读坏与读空是两回事</b>。</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>落盘目录（测试可改指向临时目录）。</summary>
    public static string? DirOverride { get; set; }

    private static string Dir => DirOverride
        ?? Path.GetDirectoryName(PitMine3D.Kylin.CrashLog.Path)
        ?? Path.GetTempPath();

    private static string RosterFile => Path.Combine(Dir, "crew_roster.json");
    private static string AssignFile => Path.Combine(Dir, "crew_assignments.json");

    // ── 花名册 ──────────────────────────────────────────────

    public static List<CrewMember> LoadRoster()
    {
        LastError = "";
        try
        {
            if (!File.Exists(RosterFile)) return new List<CrewMember>();
            return JsonSerializer.Deserialize<List<CrewMember>>(File.ReadAllText(RosterFile), Json)
                ?? new List<CrewMember>();
        }
        catch (Exception ex) { LastError = "花名册读不出（" + Short(ex) + "）"; return new List<CrewMember>(); }
    }

    public static string SaveRoster(IEnumerable<CrewMember>? members)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var list = (members ?? Enumerable.Empty<CrewMember>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Name))
                .OrderBy(m => m.Job, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ToList();
            File.WriteAllText(RosterFile, JsonSerializer.Serialize(list, Json));
            LastError = "";
            return "";
        }
        catch (Exception ex) { LastError = "花名册写盘失败（" + Short(ex) + "）"; return LastError; }
    }

    // ── 派工 ────────────────────────────────────────────────

    /// <summary>读全部派工（键 = 日期|班次|主设备）。</summary>
    public static Dictionary<string, CrewAssignment> LoadAssignments()
    {
        LastError = "";
        var map = new Dictionary<string, CrewAssignment>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(AssignFile)) return map;
            var list = JsonSerializer.Deserialize<List<CrewAssignment>>(File.ReadAllText(AssignFile), Json);
            foreach (var a in list ?? new List<CrewAssignment>())
                if (a != null && a.MainEquipment.Length > 0) map[a.Key] = a;
        }
        catch (Exception ex) { LastError = "派工记录读不出（" + Short(ex) + "）"; }
        return map;
    }

    /// <summary>
    /// 写入若干条派工（按键覆盖，其余原样保留）。
    /// <b>先读后合并</b>：整表覆盖会把别的日期/班次的派工一起抹掉。
    /// 读坏时<b>拒绝写</b> —— 那样写下去会把读不出来的那一段永久顶掉。
    /// </summary>
    public static string SaveAssignments(IEnumerable<CrewAssignment>? more)
    {
        var add = (more ?? Enumerable.Empty<CrewAssignment>())
            .Where(a => a != null && a.MainEquipment.Length > 0).ToList();
        if (add.Count == 0) return "";

        var all = LoadAssignments();
        if (LastError.Length > 0)
            return LastError + " —— 为免把读不出来的那一段顶掉，本次不写。请先处理该文件再重试。";
        foreach (var a in add) all[a.Key] = a;

        try
        {
            Directory.CreateDirectory(Dir);
            var list = all.Values
                .OrderBy(a => a.PlanDate, StringComparer.Ordinal)
                .ThenBy(a => a.Shift, StringComparer.Ordinal)
                .ThenBy(a => a.MainEquipment, StringComparer.Ordinal)
                .ToList();
            File.WriteAllText(AssignFile, JsonSerializer.Serialize(list, Json));
            return "";
        }
        catch (Exception ex) { LastError = "派工写盘失败（" + Short(ex) + "）"; return LastError; }
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
