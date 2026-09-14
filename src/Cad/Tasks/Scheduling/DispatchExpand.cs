using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>派车指令状态机：计划 → 已下达 → 装车 → 重车运行 → 已卸（或取消）。</summary>
public enum DispatchOrderStatus { Planned, Issued, Loading, Hauling, Dumped, Cancelled }

/// <summary>一条派车指令（车次级）。</summary>
public sealed class DispatchOrder
{
    public string OrderId { get; set; } = "";
    public string TruckId { get; set; } = "";
    /// <summary>这辆车谁开（来自班组派工）。空 = 没派工，<b>不编名字</b>。</summary>
    public string Driver { get; set; } = "";
    public string ShovelId { get; set; } = "";
    public string TaskId { get; set; } = "";
    /// <summary>对应任务的稳定键（实绩对账、单据追溯按它）。</summary>
    public string StableKey { get; set; } = "";
    public int TripNo { get; set; }
    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";
    public string WorkZone { get; set; } = "";
    public string MaterialCode { get; set; } = "";
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";

    public double PlannedLoadHour { get; set; }
    public double PlannedDumpHour { get; set; }
    public double CycleMin { get; set; }

    /// <summary>单车载重 t —— <b>守恒量</b>，三口径都由它折。</summary>
    public double PayloadT { get; set; }
    /// <summary>本趟折的原位实方 m³（回记作业量、扣备采量用）。</summary>
    public double PayloadInSituM3 { get; set; }
    /// <summary>本趟折的松方 m³（车厢容积校核用 —— 卡车拉的是爆破后的松散料）。</summary>
    public double PayloadLooseM3 { get; set; }
    public double HaulKm { get; set; }

    public DispatchOrderStatus Status { get; set; } = DispatchOrderStatus.Planned;

    /// <summary>本趟的运输功 t·km。</summary>
    public double WorkTKm => PayloadT * HaulKm;
}

/// <summary>一条任务没能展开车次的原因。</summary>
public sealed class TripSkip
{
    public string TaskId { get; set; } = "";
    public string WorkZone { get; set; } = "";
    public string Reason { get; set; } = "";
    public int RecommendedTrucks { get; set; }
}

/// <summary>车次展开的产物。</summary>
public sealed class DispatchPlan
{
    public List<DispatchOrder> Orders { get; set; } = new();
    /// <summary>人读的展开说明（每条任务一行；<b>解不出的会说清为什么</b>）。</summary>
    public List<string> Notes { get; set; } = new();
    public List<TripSkip> Skips { get; set; } = new();

    public int TripCount => Orders.Count;
    public double TotalPayloadT => Orders.Sum(o => o.PayloadT);
    public double TotalWorkTKm => Orders.Sum(o => o.WorkTKm);
    /// <summary>加权平均运距 km = 总运输功 ÷ 总载重。没有车次时判不了。</summary>
    public double? AvgHaulKm => TotalPayloadT > 1e-6 ? TotalWorkTKm / TotalPayloadT : null;

    public string Caption => Orders.Count == 0
        ? "本班没有排出车次。"
        : $"车次 {TripCount} 趟　·　载重合计 {TotalPayloadT:N0} t"
          + $"　·　运输功 {TotalWorkTKm / 1e4:0.00} 万t·km"
          + (AvgHaulKm is { } k ? $"　·　加权平均运距 {k:0.00} km" : "　·　加权平均运距 —");
}

/// <summary>
/// 车次展开（移植原 <c>TaskLib.Engine.DispatchEngine</c> 的固定配车展开 A 段）。
///
/// <para>
/// 任务说的是"这个班采 2450 m³"，司机要的是"我第 3 趟 09:12 到 WK-10 装车、09:26 卸到破碎站"。
/// 中间那一步就是车次展开：
/// </para>
/// <code>
///   τ_L  装车节拍 min/车        T_c  循环时间 min
///   ★ 第 i 台车（i 从 1 起）第 k 趟的装车时刻：s(i,k) = 起始 + (i−1)·τ_L + (k−1)·T_c
/// </code>
/// <para>
/// <b>(i−1)·τ_L 这个错峰项不能省</b>：n 台车同时压到铲下就得排队，现场是按装车节拍依次进场的；
/// 错峰后展开出来的车次数才与匹配系数 <c>MF = n·τ_L/T_c</c> 的物理含义自洽。
/// </para>
/// <para>
/// 车次数取两条路的<b>小者</b>（两者都是上界，谁先见底听谁的）：
/// 时间法（一趟算数的条件是<b>能把料卸掉</b>：装完 + 重车行驶 ≤ 班末；空车返程可以压过班末）、
/// 量法 <c>N_vol = ⌈目标吨量 ÷ 单车载重⌉</c>。
/// <b>派车指令是承诺，半趟不是一车料</b>；备采量只够 7 趟就不能签发 8 趟。
/// </para>
/// <para>
/// <b>载重的口径</b>（最容易搞错的地方）：单车载重 W_t 是<b>吨</b> ——
/// 吨量是实方/松方/占容方三口径间<b>唯一的守恒量</b>，故一律以吨为主：
/// 实方 = W_t ÷ ρ实、松方 = 实方 × Ks、占容 = 实方 × Kr。
/// ρ实 / Ks / Kr 一律经 <see cref="MaterialCatalog"/>，<b>本类不出现任何密度常量</b>。
/// </para>
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item><b>混采多线未移</b>（原版 <c>TruckTripPlanner.AllocateTripLegs</c>）：一台铲同一时窗挖
///     「煤7∶岩3」、煤去破碎站岩去内排场时，车队跑的是两条运距不同的线，车次要按份额交错分摊。
///     Kylin 的作业面台账目前一个面一个主去向，故本类只走<b>单线</b>；
///     等混采拆线接进来时，分摊函数必须与甘特<b>共用同一个</b> —— 两边各写一套，
///     立刻会出现"图上第 3 趟去破碎站、单子上去排土场"。</item>
///   <item><b>动态派车（最小铲饱和度 / 最早可装车）未移</b>，本类只有固定配车这一种。</item>
/// </list>
/// </summary>
public static class DispatchExpand
{
    /// <summary>单台车一个班最多排几趟（防呆上界）。</summary>
    public const int MaxTripsPerTruck = 40;
    /// <summary>一次展开的车次总上限（防呆：整天全班次展开会出上万条）。</summary>
    public const int MaxTripsTotal = 4000;

    /// <summary>
    /// 把一批任务展开成派车单。<paramref name="shift"/> 为空则全天。
    /// <paramref name="driverOf"/>：(班次, 车号) → 司机名；没派工留空，<b>不编名字</b>。
    /// </summary>
    public static DispatchPlan Expand(IEnumerable<ShiftTask>? tasks, string planDate, string? shift = null,
                                      Func<string, string, string>? driverOf = null)
    {
        var plan = new DispatchPlan();
        var list = (tasks ?? Enumerable.Empty<ShiftTask>())
            .Where(t => t != null && t.Process == ProcessType.Load && t.TargetVolumeM3 > 1)
            .Where(t => string.IsNullOrWhiteSpace(shift) || string.Equals(t.Shift, shift, StringComparison.Ordinal))
            .OrderBy(t => t.StartHour).ThenBy(t => t.Group.MainEquipment, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var t in list)
        {
            if (plan.Orders.Count >= MaxTripsTotal)
            {
                plan.Notes.Add($"车次总数已达上限 {MaxTripsTotal}，其余任务未展开（请缩小班次范围）。");
                break;
            }
            var orders = ExpandOne(t, planDate, driverOf, out string note, out TripSkip? skip);
            plan.Orders.AddRange(orders);
            plan.Notes.Add(note);
            if (skip != null) plan.Skips.Add(skip);
        }

        if (plan.Orders.Count == 0 && plan.Notes.Count == 0)
            plan.Notes.Add("本班无采装任务可展开车次。");
        return plan;
    }

    private static List<DispatchOrder> ExpandOne(ShiftTask t, string planDate,
                                                 Func<string, string, string>? driverOf,
                                                 out string note, out TripSkip? skip)
    {
        var result = new List<DispatchOrder>();
        skip = null;

        var trucks = t.Group.Trucks.Where(s => !string.IsNullOrWhiteSpace(s))
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (trucks.Count == 0)
        {
            note = $"{t.Id} {t.WorkZone}：尚未配车（建议 {t.Group.RecommendedTrucks} 台），无法展开车次 —— "
                 + "请先在「作业面台账 / 班组派工」配车。";
            skip = new TripSkip { TaskId = t.Id, WorkZone = t.WorkZone, Reason = note, RecommendedTrucks = t.Group.RecommendedTrucks };
            return result;
        }

        if (!t.Group.HasCycleBreakdown)
        {
            note = $"{t.Id} {t.WorkZone}：编组没有周期分解（τ_L / T_c 解不出来），排不出车次时刻 —— "
                 + "多半是主设备机型不在「设备编组规则」里。**这里不按经验值编一个节拍**："
                 + "编出来的时刻表看着精确，司机照着跑必然对不上。";
            skip = new TripSkip { TaskId = t.Id, WorkZone = t.WorkZone, Reason = note };
            return result;
        }

        double takt = Math.Max(0.05, t.Group.LoadTaktMin);
        double tc = Math.Max(takt, t.Group.CycleTimeMin);
        double loadedMin = Math.Max(0, (tc - takt) / 2);      // 重车行驶 ≈ 半个非装车时间
        double winMin = Math.Max(0, (t.EndHour - t.StartHour) * 60.0);

        // ── 时间法：逐车算能跑几趟（一趟算数的条件是能把料卸掉）──
        var slots = new List<(string Truck, double LoadMin)>();
        for (int i = 0; i < trucks.Count; i++)
        {
            double startMin = i * takt;                        // (i−1)·τ_L 错峰进场
            for (int k = 1; k <= MaxTripsPerTruck; k++)
            {
                if (startMin + takt + loadedMin > winMin + 1e-6) break;
                slots.Add((trucks[i], startMin));
                startMin += tc;                                // s(i,k+1) = s(i,k) + T_c
            }
        }
        if (slots.Count == 0)
        {
            note = $"{t.Id} {t.WorkZone}：时窗 {Hm(t.StartHour)}–{Hm(t.EndHour)}（{winMin:0}min）"
                 + $"不足一个循环 T_c {tc:0.0}min，本班无完整车次。";
            skip = new TripSkip { TaskId = t.Id, WorkZone = t.WorkZone, Reason = note };
            return result;
        }

        var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText(t.Material));
        double rho = Math.Max(0.1, spec.InSituDensityTPerM3);
        double targetT = t.TargetVolumeM3 * rho;

        // ── 单车载重 W_t ──
        double payloadT = t.Group.TruckPayloadT;
        string payloadSrc = "编组规则台账";
        if (payloadT <= 1e-6)
        {
            // 台账没解出载重 ⇒ 按「目标吨量 ÷ 时间法车次数」反推，两条路自洽（此时量法不再另设上界）
            payloadT = targetT / Math.Max(1, slots.Count);
            payloadSrc = "按目标吨量反推";
        }
        payloadT = Math.Max(0.1, payloadT);

        // ── 量法上界，与时间法取小 ──
        int nVol = payloadSrc == "编组规则台账"
            ? (int)Math.Ceiling(targetT / payloadT - 1e-6)
            : slots.Count;
        int n = Math.Max(0, Math.Min(slots.Count, Math.Max(1, nVol)));

        // 削哪些：按装车时刻排序后砍最晚的几趟（现场也是班末不再发车）
        var taken = slots.OrderBy(x => x.LoadMin)
                         .ThenBy(x => x.Truck, StringComparer.OrdinalIgnoreCase)
                         .Take(n).ToList();

        string key = TaskKey.Of(t, planDate);
        string dateTag = TaskKey.DateTag(planDate);
        string shiftTag = TaskKey.ShiftTag(t.Shift);

        foreach (var g in taken.GroupBy(x => x.Truck, StringComparer.OrdinalIgnoreCase))
        {
            int trip = 0;
            foreach (var slot in g.OrderBy(x => x.LoadMin))
            {
                trip++;
                double loadHour = t.StartHour + slot.LoadMin / 60.0;
                double inSitu = payloadT / rho;
                result.Add(new DispatchOrder
                {
                    OrderId = $"DO-{dateTag}-{shiftTag}-{g.Key}-{trip:000}",
                    TruckId = g.Key,
                    Driver = driverOf?.Invoke(t.Shift, g.Key) ?? "",
                    ShovelId = t.Group.MainEquipment,
                    TaskId = t.Id,
                    StableKey = key,
                    TripNo = trip,
                    PlanDate = planDate,
                    Shift = t.Shift,
                    WorkZone = t.WorkZone,
                    MaterialCode = spec.Code,
                    SinkId = t.DestinationId,
                    SinkName = t.DestinationName,
                    PlannedLoadHour = Math.Round(loadHour, 3),
                    PlannedDumpHour = Math.Round(loadHour + (takt + loadedMin) / 60.0, 3),
                    CycleMin = Math.Round(tc, 1),
                    PayloadT = Math.Round(payloadT, 2),
                    PayloadInSituM3 = Math.Round(inSitu, 2),
                    PayloadLooseM3 = Math.Round(spec.ToLooseM3(inSitu), 2),
                    HaulKm = Math.Round(t.EquivHaulKm, 3),
                    Status = DispatchOrderStatus.Planned,
                });
            }
        }

        string cut = n < slots.Count ? $"（时间法可跑 {slots.Count} 趟，按目标量 {nVol} 趟取小）" : "";
        string route = t.DestinationName.Length > 0 ? t.DestinationName
                     : t.DestinationId.Length > 0 ? t.DestinationId : "（未定卸点）";
        note = $"{t.Id} {t.WorkZone} → {route}：{result.Count} 趟{cut}"
             + $"　τ_L {takt:0.0}min / T_c {tc:0.0}min / 载重 {payloadT:0.#}t（{payloadSrc}）"
             + (t.EquivHaulKm > 1e-6 ? $" / 运距 {t.EquivHaulKm:0.##}km" : " / 运距未录，运输功偏小");
        return result;
    }

    /// <summary>小时数 → <c>HH:mm</c>；跨日回绕并加 "+1"。</summary>
    public static string Hm(double hh)
    {
        bool next = hh >= 24;
        double v = next ? hh - 24 : hh;
        int h = (int)v;
        int m = (int)Math.Round((v - h) * 60);
        if (m >= 60) { m -= 60; h += 1; }
        return $"{h:00}:{m:00}" + (next ? "+1" : "");
    }

    /// <summary>导出 CSV。</summary>
    public static string ToCsv(DispatchPlan plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# ").Append(plan.Caption.Replace('　', ' ')).Append('\n');
        sb.Append("指令号,车号,司机,电铲,趟次,作业地点,物料,卸点,装车,卸车,循环min,载重t,实方m3,松方m3,运距km,运输功t·km\n");
        foreach (var o in plan.Orders.OrderBy(o => o.PlannedLoadHour).ThenBy(o => o.TruckId, StringComparer.Ordinal))
            sb.Append(string.Join(",", new[]
            {
                Q(o.OrderId), Q(o.TruckId), Q(o.Driver.Length > 0 ? o.Driver : "—"), Q(o.ShovelId),
                o.TripNo.ToString(CultureInfo.InvariantCulture), Q(o.WorkZone), Q(o.MaterialCode),
                Q(o.SinkName.Length > 0 ? o.SinkName : o.SinkId),
                Hm(o.PlannedLoadHour), Hm(o.PlannedDumpHour),
                o.CycleMin.ToString("0.#", CultureInfo.InvariantCulture),
                o.PayloadT.ToString("0.##", CultureInfo.InvariantCulture),
                o.PayloadInSituM3.ToString("0.##", CultureInfo.InvariantCulture),
                o.PayloadLooseM3.ToString("0.##", CultureInfo.InvariantCulture),
                o.HaulKm.ToString("0.###", CultureInfo.InvariantCulture),
                o.WorkTKm.ToString("0.#", CultureInfo.InvariantCulture),
            })).Append('\n');
        return sb.ToString();
    }

    private static string Q(string? s)
    {
        string v = s ?? "";
        return v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
