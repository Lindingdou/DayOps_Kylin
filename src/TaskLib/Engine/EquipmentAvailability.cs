// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/EquipmentAvailability.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data;              // EquipmentStatus
using PitMine3D.Kylin.Data.Entities;     // Equipment
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  「今天这台设备能不能上岗」—— 设计文档 §5 里 IEquipmentCapabilityService.AvailableEquipment
//  那一条，最小的、诚实的落地。
//
//  为什么必须有：装配盘子时，每个作业面的主设备来自 working_face_routing.main_equipment，
//  而**从来没有人拿它跟设备台账对过一次**。于是一台状态是「检修」甚至「报废」的电铲，
//  照样被排满三个班的活、进任务书、进派车单——现场拿到单子才发现车根本动不了。
//  （反倒是「补车/顶替取车池」EquipmentPool 一直在按状态过滤：同一件事，一半做了一半没做。）
//
//  分级，不搞"一刀切拦下"：
//    · 报废(Scrapped)      → Error。报废设备不可能上岗，这是**数据错**，不是调度问题；
//                            下达闸门(DispatchEngine.ValidateForIssue)会挡住。
//    · 检修(Maintenance)   → Warn。检修有长有短，调度员可能知道今天下午就出来；
//                            拦死会逼人去改台账状态来骗过校核，那比不校核更糟。
//    · 台账里查无此设备     → Warn。临时借调/台账未录都可能，如实说，不替人判定。
//    · 闲置(Idle)/在用(InUse)/状态为空 → 不报。状态没填是常态，不能当异常。
//
//  ★ 台账读不出来（未接通/异常）时**一条都不报**，而不是把所有面报成"查无此设备"——
//    整屏假告警会让人从此忽略这一类校核，那等于把这个闸门废掉。读不出来只说一句来源。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>主设备可用性校核。</summary>
public static class EquipmentAvailability
{
    /// <summary>最近一次校核的来源文案（UI 显示"这一条到底查没查"）。</summary>
    public static string LastSourceLabel { get; private set; } = "";

    /// <summary>
    /// 逐面校核主设备状态，把问题写进 <paramref name="violations"/>，返回来源文案。
    /// </summary>

    /// <summary>
    /// 上一次装配用的是不是真数据（false = 走了兜底）。
    /// <para><b>链路体检读它，不读来源文案</b> —— 文案一改就静默抠空，
    /// 而抠空之后「兜底」会被当成「真实」，体检朝着让人放心的方向失效。</para>
    /// </summary>
    public static bool LastFromLedger { get; private set; }

    public static string Check(ExploderConfig cfg, List<PlanViolation>? violations = null)
    {
        if (cfg == null || cfg.Faces.Count == 0) return LastSourceLabel = "";

        Dictionary<string, Equipment> byId;
        try
        {
            byId = EquipmentDataContext.Equipment.All()
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.EquipmentId))
                .GroupBy(e => e.EquipmentId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // 读不出台账就一条都不判——宁可不查，也不整屏假告警
            { LastFromLedger = false; return LastSourceLabel = $"设备可用性：台账未接通（{Short(ex)}），本次不校核"; }
        }

        if (byId.Count == 0)
            { LastFromLedger = false; return LastSourceLabel = "设备可用性：设备台账为空，本次不校核"; }

        LastFromLedger = true;                 // 台账读到了，下面才谈得上校核
        int scrapped = 0, maintenance = 0, unknown = 0, checkedCount = 0;

        foreach (var f in cfg.Faces)
        {
            string id = (f.Group?.MainEquipment ?? "").Trim();
            if (id.Length == 0) continue;   // 无主设备是另一条校核（下达闸门管），不在此重复
            checkedCount++;

            if (!byId.TryGetValue(id, out var e))
            {
                unknown++;
                violations?.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Warn, Code = ViolationCodes.EquipUnavailable,
                    Message = $"{f.Zone} 主设备 {id} 不在设备台账里（临时借调或台账未录）——"
                            + "型号/斗容取不到，编组只能按兜底参数解",
                });
                continue;
            }

            string st = (e.Status ?? "").Trim();

            if (string.Equals(st, nameof(EquipmentStatus.Scrapped), StringComparison.OrdinalIgnoreCase))
            {
                scrapped++;
                violations?.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Error, Code = ViolationCodes.EquipUnavailable,
                    Message = $"{f.Zone} 主设备 {id} 台账状态为「报废」，不能排产——"
                            + "请换设备或先改正台账状态",
                });
            }
            else if (string.Equals(st, nameof(EquipmentStatus.Maintenance), StringComparison.OrdinalIgnoreCase))
            {
                maintenance++;
                violations?.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Warn, Code = ViolationCodes.EquipUnavailable,
                    Message = $"{f.Zone} 主设备 {id} 台账状态为「检修」——"
                            + "本面全天的活仍按计划排出，出不来就得改派或顺延（检修档期无台账，引擎排不出具体时窗）",
                });
            }
        }

        int bad = scrapped + maintenance + unknown;
        LastSourceLabel = bad == 0
            ? $"设备可用性：{checkedCount} 个面的主设备全部在册且可用"
            : $"设备可用性：{checkedCount} 面已核 —— 报废 {scrapped} · 检修 {maintenance} · 不在台账 {unknown}";
        return LastSourceLabel;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
