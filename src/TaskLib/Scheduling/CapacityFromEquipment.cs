// 忠实移植自原 PitMine3D Modules/TaskLib/Scheduling/CapacityFromEquipment.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Scheduling;

/// <summary>一个面推出来的日能力，附上它是怎么推出来的。</summary>
public sealed class FaceCapacity
{
    public string Zone { get; init; } = "";
    /// <summary>日能力上限（m³ 实方/日）。0 = 推不出来。</summary>
    public double DailyM3 { get; init; }
    /// <summary>编组班产（m³/h）＝ min(铲装能力, 车队运力)，由 FleetMatcher 解出。</summary>
    public double GroupM3PerH { get; init; }
    /// <summary>本面一天的有效作业小时（各班时窗之和 × 时间利用率）。</summary>
    public double DailyHours { get; init; }
    /// <summary>推导依据（给人读的，不是调试文本）。</summary>
    public string Basis { get; init; } = "";
}

/// <summary>全矿一天的工序能力（穿孔 / 爆破 / 排土），从在籍设备推。</summary>
public sealed class SiteCapacity
{
    /// <summary>钻机日能力（孔米/日）。</summary>
    public double DrillMetersPerDay { get; init; }
    /// <summary>推土机日承接能力（m³ 占容/日）。</summary>
    public double DumpM3PerDay { get; init; }
    /// <summary>在籍钻机 / 推土机台数。</summary>
    public int Drills { get; init; }
    public int Dozers { get; init; }
    public List<string> Basis { get; } = new();
}

/// <summary>
/// 从**月度任务量 + 设备信息**推出分解所需的一切约束。
///
/// <para><b>原则：不问人要第二套数</b>。日能力上限、穿孔能力、排土承接能力、开月爆堆，
/// 全部由"这个矿有哪些设备、每台什么参数、排了几个班"推出来。
/// 人要填的只有月度任务量本身 —— 那是计划，不是参数。</para>
///
/// <para><b>口径复用而不是另起一套</b>：铲装能力与车队运力的解算走
/// <see cref="FleetMatcher"/>（它读 dispatch_rule + equipment_model 拿斗容/载重/效率），
/// 这里只做"小时能力 → 日能力"的换算。另写一套小时能力必然与排产口径打架，
/// 到时候两个数都对不上、谁也说不清哪个是真的。</para>
/// </summary>
public static class CapacityFromEquipment
{
    /// <summary>
    /// 时间利用率：一个班的时窗里真正能干活的比例。
    /// <para>交接班、加油、班中餐、等爆破警戒、路面洒水都要占时间。0.85 是露天矿常见口径。
    /// 这个数直接乘在日能力上，改它等于改整个月的排班密度，所以摆在明面上。</para>
    /// </summary>
    public const double TimeUtilisation = 0.85;

    /// <summary>钻机纯钻速（孔米/小时）。潜孔钻在中硬岩的常见值。</summary>
    public const double DrillMetersPerHour = 22.0;

    /// <summary>推土机排弃能力（m³ 占容/小时）：推平 + 压实。</summary>
    public const double DozerM3PerHour = 260.0;

    /// <summary>
    /// 每个采装面的日能力上限。
    /// <para><paramref name="cfg"/> 里的班次时窗决定"一天有几个小时可用"；
    /// <c>Group.GroupCapacityM3PerH</c> 决定"每小时能出多少方"。两者相乘再乘时间利用率。</para>
    /// </summary>
    public static List<FaceCapacity> ForFaces(ExploderConfig cfg)
    {
        var list = new List<FaceCapacity>();
        if (cfg == null) return list;

        double shiftHours = ShiftHours(cfg, out string shiftBasis);
        double effective = shiftHours * TimeUtilisation;

        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load))
        {
            double q = f.Group?.GroupCapacityM3PerH ?? 0;
            if (q <= 1e-9)
            {
                list.Add(new FaceCapacity
                {
                    Zone = f.Zone, DailyM3 = 0, GroupM3PerH = 0, DailyHours = effective,
                    Basis = $"「{f.Zone}」编组班产没解出来（FleetMatcher 没给 GroupCapacityM3PerH）"
                          + " ⇒ 推不出日能力上限。**不猜** —— 分解那边会退回纯均摊并注明未校核。",
                });
                continue;
            }

            list.Add(new FaceCapacity
            {
                Zone = f.Zone,
                DailyM3 = Math.Round(q * effective),
                GroupM3PerH = q,
                DailyHours = effective,
                Basis = $"编组班产 {q:0} m³/h × 日有效工时 {effective:0.#} h "
                      + $"（{shiftBasis}，时间利用率 {TimeUtilisation:P0}）= {q * effective:0} m³/日",
            });
        }
        return list;
    }

    /// <summary>全矿的穿孔与排土日能力，从在籍设备台数推。</summary>
    public static SiteCapacity ForSite(ExploderConfig cfg, IReadOnlyList<RosterEntry>? roster)
    {
        double shiftHours = ShiftHours(cfg, out string shiftBasis);
        double effective = shiftHours * TimeUtilisation;

        var rs = roster ?? new List<RosterEntry>();
        int drills = rs.Count(r => Is(r, "钻机", "潜孔钻", "牙轮"));
        int dozers = rs.Count(r => Is(r, "推土机", "推土"));

        var cap = new SiteCapacity
        {
            Drills = drills, Dozers = dozers,
            DrillMetersPerDay = Math.Round(drills * DrillMetersPerHour * effective),
            DumpM3PerDay = Math.Round(dozers * DozerM3PerHour * effective),
        };
        cap.Basis.Add($"日有效工时 {effective:0.#} h（{shiftBasis}，时间利用率 {TimeUtilisation:P0}）");
        cap.Basis.Add(drills > 0
            ? $"钻机 {drills} 台 × {DrillMetersPerHour:0} 孔米/h ⇒ {cap.DrillMetersPerDay:0} 孔米/日"
            : "在籍设备里**没有钻机** ⇒ 穿孔能力为 0，本月的爆破备孔无从安排。");
        cap.Basis.Add(dozers > 0
            ? $"推土机 {dozers} 台 × {DozerM3PerHour:0} m³/h ⇒ {cap.DumpM3PerDay:0} m³占容/日"
            : "在籍设备里**没有推土机** ⇒ 排土承接能力为 0。");
        return cap;
    }

    /// <summary>
    /// 开月爆堆存量的**推定值**。
    ///
    /// <para><b>为什么可以推而不是问</b>：月度计划本身就隐含"上月末把料给你备好了"这个前提 ——
    /// 否则计划第一天就执行不了，那样的计划不会被确认下发。所以推定
    /// <b>开月爆堆 = 爆后等待期内的采装量</b>，即刚好够撑到本月第一炮可采为止。</para>
    ///
    /// <para><b>为什么必须标成推定</b>：它是假设不是实测。真实爆堆存量要么来自现场盘点、
    /// 要么来自上月末的爆破/采装台账差额。分解结果里会写明这是推定值 ——
    /// 一旦有实测就该覆盖它，而不是让这个假设一直当真值用。</para>
    /// </summary>
    public static double AssumeOpeningMuck(double monthM3, int workdays, ProcessParams p)
    {
        if (monthM3 <= 1e-9 || workdays <= 0) return 0;
        int cover = Math.Max(1, p.BlastLeadDays);
        return Math.Round(monthM3 / workdays * cover);
    }

    /// <summary>一天的班次时窗合计（小时）。没有班次表就按三班八小时。</summary>
    private static double ShiftHours(ExploderConfig? cfg, out string basis)
    {
        var s = cfg?.Shifts;
        if (s != null && s.Count > 0)
        {
            double h = s.Sum(x => Math.Max(0, x.End - x.Start));
            if (h > 1e-6)
            {
                basis = $"{s.Count} 个班共 {h:0.#} h";
                return h;
            }
        }
        basis = "无班次表，按三班 × 8 h";
        return 24.0;
    }

    /// <summary>按类别 + 显示名认设备种类（台账里两处都可能写着"钻机"）。</summary>
    private static bool Is(RosterEntry r, params string[] kinds)
    {
        string s = (r.Category ?? "") + " " + (r.Display ?? "") + " " + (r.EquipId ?? "");
        return kinds.Any(k => s.Contains(k, StringComparison.Ordinal));
    }
}
