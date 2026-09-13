// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/MonthlyShiftDecomposer.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;                 // MiningUnitLedger / UnitRailFile / TaskZoneSplitter
using PitMine3D.Kylin.TaskLib.Domain;                        // ProcessType / MaterialCatalog

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  月度 → 班组计划。**设备 × 物料量 × 空间位置** 三样一起解，逐班推进。
//
//  ══ 它替掉的是什么 ══
//  此前「月 → 日」是一次**标量摊派**：月量 ÷ 作业日 = 日目标，再按编组班产装箱。
//  那条路上**没有位置**：任务落在哪块地、这台铲今天推到哪儿，全靠人事后对着图猜；
//  也**没有推进**：同一个面每天的目标一样多，而现场是一个单元采完换下一个。
//  这里改成**逐班推进**：每一班给每台设备找它此刻该干的那一段，干完就往前推，
//  推到哪儿是算出来的 —— 任务区域就是推过的那一段地。
//
//  ══ 三样是怎么进来的 ══
//  · **空间** ← 采掘单元的真轨（`UnitRailFile`）+ `TaskZoneSplitter` 按量切地；
//  · **物料量** ← 月度台账逐单元的煤/岩量与层号（决定派什么设备、料去哪儿）；
//  · **设备** ← 台效（能力）× 本班有效小时（已扣爆破清场）= 这一班干得完多少。
//
//  ══ 八条口径（M 组）══
//  **M1 同一作业面、同一班，只有一道工序。** 现场规矩：钻机和电铲不能同时在一个面上。
//      并行只发生在**面与面之间**。不管这条，排出来的班表在图上是两台设备摞在一块地上。
//  **M2 一台设备同一班只在一处。** 与 M1 是两条独立约束（一个管地，一个管机）。
//  **M3 穿孔必须超前采装。** 同一个单元，采装的最早开工班 = 穿孔完成班 + 超前期。
//      不挡的话会排出「还没打孔就开挖」的班表，而每一行看着都正常。
//  **M4 班量 ≤ 本班能力**（台效 × 有效小时 ÷ 标准班时）。爆破班扣清场 —— 那一班真干不了那么多。
//  **M5 四个量口径分开记**（原位实方 / 控制方量 / 排弃占容 / 承运量），**不给总量**。
//  **M6 任务区域按量切，且按面积反求宽度**（走 `TaskZoneSplitter`）——
//      弯轨上量与宽度不成正比，线性分会让每块的面积对不上它该有的量，而图上看不出来。
//  **M7 排不下的量单列成缺口**，不悄悄少排。月底对不上账全从这儿来。
//  **M10 穿孔与爆破只排白班**（时窗与 6:00–18:00 重叠过半的那一班）。夜里没有视线、警戒与装药条件。
//      按**时窗**判不按班名判：换一套班制（四班三运转/交接不在整点）时按名字判会静默失效，
//      而失效的样子是"穿孔爆破排到夜里"，图上看不出异常。
//  **M8 物料决定设备与去向。** 煤面不派剥离铲、表土不进内排场 ——
//      物料判错时四个数一样正常，只有到了现场才发现料送错了地方。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条班组任务（逐班一行）。</summary>
public sealed class ShiftTaskRow
{
    public string Date = "";
    public string Shift = "";
    public double StartHour, EndHour;
    /// <summary>本班有效小时（已扣爆破清场）。</summary>
    public double EffectiveHours;
    public bool IsBlastShift;

    public string MachineId = "";
    public string MachineModel = "";
    /// <summary>班组（没排班组时为空 —— <b>空就是没排，不编一个</b>）。</summary>
    public string CrewId = "";

    public ProcessType Process;
    /// <summary>作业面名（空间分组键；M1 按它判同面并行）。</summary>
    public string FaceName = "";
    public string UnitId = "";

    public string MaterialCode = "";
    public double VolumeM3;
    /// <summary>量口径。<b>四本账不许并成一列</b>。</summary>
    public string Basis = "";

    /// <summary>本班在这个单元上推过的那一段（推进方向上的宽度区间，m）。</summary>
    public double W0, W1;
    /// <summary>任务区域的平面环（扁平 [x,y,…]）。空 = 切不出地（无真轨）。</summary>
    public double[] ZoneRingXy = Array.Empty<double>();
    public double ZoneAreaM2;

    public bool CountsAsOutput => Process == ProcessType.Load;

    public string Caption =>
        $"{Date} {Shift}班　{MachineId} @ {FaceName}/{UnitId}　"
      + $"{ProcessZh}　{VolumeM3:N0} m³（{Basis}）"
      + (ZoneAreaM2 > 0 ? $"　占地 {ZoneAreaM2 / 1e4:0.##} 万m²" : "　无地");

    public string ProcessZh => Process switch
    {
        ProcessType.Drill => "穿孔",
        ProcessType.Blast => "爆破",
        ProcessType.Load => "采装",
        ProcessType.Haul => "运输",
        ProcessType.Dump => "排土",
        _ => "待命",
    };
}

/// <summary>排不下的一笔。</summary>
public sealed record ShiftShortfall(string UnitId, string Basis, double M3, string Why);

/// <summary>一次月→班分解的结果。</summary>
public sealed class ShiftPlanResult
{
    public string Period = "";
    public List<ShiftTaskRow> Rows = new();
    public List<ShiftShortfall> Shortfalls = new();
    public List<string> Notes = new();
    public string Headline = "";

    public bool Ok => Rows.Count > 0;

    /// <summary>按量口径分开求和 —— <b>不给总量</b>，那个数不对应任何真实量。</summary>
    public IEnumerable<(string Basis, double M3)> ByBasis
        => Rows.GroupBy(r => r.Basis).Select(g => (g.Key, g.Sum(x => x.VolumeM3)));

    public double ShortfallOf(string basis)
        => Shortfalls.Where(x => x.Basis == basis).Sum(x => x.M3);
}

// ── 输入模型（全部可注入，离线判据喂算例走它）─────────────────────────

/// <summary>参与本期的一台设备。</summary>
public sealed record PlanMachine(
    string MachineId, string Model, ProcessType Process,
    double RatePerDay,
    /// <summary>能干哪些物料码；空 = 不挑料。</summary>
    IReadOnlyCollection<string>? Materials = null)
{
    public bool CanTake(string materialCode)
        => Materials == null || Materials.Count == 0 || Materials.Contains(materialCode);
}

/// <summary>一个班。</summary>
public sealed record PlanShift(string Name, double StartHour, double EndHour, bool IsBlast)
{
    public double Hours => Math.Max(0, EndHour - StartHour);
    public double EffectiveHours => Math.Max(0, Hours - (IsBlast ? BlastClearHours : 0));
    /// <summary>爆破清场占本班的小时数。清场是真占时间的。</summary>
    public const double BlastClearHours = 1.5;
}

/// <summary>一个待采单元（月度台账的一行 + 真轨）。</summary>
public sealed class PlanUnit
{
    public string UnitId = "";
    public string FaceName = "";
    public string MaterialCode = "";
    public int Seq;
    /// <summary>剩余原位实方（采装口径）。</summary>
    public double RemainM3;
    /// <summary>需不需要先穿孔爆破。</summary>
    public bool NeedsBlasting;
    /// <summary>穿孔的控制方量（免爆时为 0）。</summary>
    public double DrillM3;

    /// <summary>真轨；null = 切不出地（任务照排，但没有区域）。</summary>
    public UnitRailFile.RailRow? Rail;
    public bool TowardCrest = true;

    // ── 推进游标（算法内部推着走）──
    internal double DoneM3;
    internal double DoneDrillM3;
    /// <summary>穿孔完成在第几个作业日（-1 = 还没完）。M3 靠它挡采装。</summary>
    internal int DrillDoneDay = -1;

    /// <summary>爆破排在第几个作业日（-1 = 还没排）。同一个单元只爆一次。</summary>
    internal int BlastDay = -1;
}

/// <summary>月度 → 班组计划。纯计算，不碰数据库、不碰 GUI。</summary>
public static class MonthlyShiftDecomposer
{
    /// <summary>
    /// 台效折成"这一班能干多少"的分母 —— <b>24，因为 <see cref="PlanMachine.RatePerDay"/> 是**日**台效</b>。
    ///
    /// <para>
    /// ⚠ 这里原来写的是 8（"标准班时"）。两边的口径对不上：
    /// 采装侧 <c>RatePerDay = 编组班产(m³/h) × 24</c>（<see cref="ShiftPlanAssembler.DefaultWorkHoursPerDay"/>），
    /// 钻机侧是台效台账里的 <c>M3PerDay</c> —— <b>两个都是一整天的量</b>，
    /// 再乘 <c>班有效工时 / 8</c> 就等于**每个班都排了一整天的活**，一天三班 ⇒ <b>三倍</b>。
    /// </para>
    /// <para>
    /// 实测（2026-08 真库）：一台电铲单班排到 <b>40,736 m³</b>（8h 折 5,092 m³/h），
    /// 于是全月 381 万m³ 的采装在 <b>11 个作业日</b> 里"干完"，08-23 之后排了班却一条任务都没有；
    /// 08-20 只剩最后一台电铲的 3 条 —— 甘特上就是一行电铲三根条。
    /// 穿孔同理：整月的孔在前 8 天打完，之后钻机全空。
    /// </para>
    /// <para>
    /// 除以 24 之后，三班（3×8h，无损失工时）的能力之和正好等于日台效；
    /// 两班制的矿一天就只有 16/24 的量 —— 这才是"这台设备今天真能干多少"。
    /// </para>
    /// </summary>
    private const double RateBasisHoursPerDay = 24.0;

    /// <summary>
    /// 逐班推进，把本期的量分解成班组任务。
    /// </summary>
    /// <param name="units">本期待采单元，<b>按推进序</b>（Seq）给。</param>
    /// <param name="machines">参与本期的设备。</param>
    /// <param name="days">作业日清单（来自班次日历）。</param>
    /// <param name="shiftsOf">某一天的班。</param>
    /// <param name="blastLeadDays">穿爆超前（作业日）。</param>
    /// <param name="loadQuotaOf">
    /// 某个作业日的<b>采装配额</b>（m³ 原位实方）。null = 自己按能力权重均衡到全期（见 <see cref="BuildDefaultQuota"/>）。
    /// <para>周目标下达过时，由 <see cref="ShiftPlanAssembler"/> 把那一周的配额换成周目标拆出来的量。</para>
    /// </param>
    public static ShiftPlanResult Decompose(
        string period,
        IReadOnlyList<PlanUnit>? units,
        IReadOnlyList<PlanMachine>? machines,
        IReadOnlyList<DateTime>? days,
        Func<DateTime, IReadOnlyList<PlanShift>> shiftsOf,
        double blastLeadDays = 3,
        Func<DateTime, double>? loadQuotaOf = null)
    {
        var res = new ShiftPlanResult { Period = period ?? "" };
        var us = (units ?? Array.Empty<PlanUnit>()).Where(u => u != null && u.RemainM3 > 1e-6)
                 .OrderBy(u => u.Seq == 0 ? int.MaxValue : u.Seq)
                 .ThenBy(u => u.UnitId, StringComparer.Ordinal).ToList();
        var ms = (machines ?? Array.Empty<PlanMachine>()).Where(m => m != null).ToList();
        var ds = (days ?? Array.Empty<DateTime>()).ToList();

        if (us.Count == 0) { res.Headline = "本期没有待采单元 —— 分解不出班组计划。"; return res; }
        if (ms.Count == 0) { res.Headline = "没有可派设备 —— **拒绝分解**（编一台出来等于凭空造产能）。"; return res; }
        if (ds.Count == 0) { res.Headline = "作业日清单是空的 —— **拒绝分解**（不拿自然日顺延顶替）。"; return res; }

        foreach (var u in us) ResetCursor(u);

        // ── 逐日配额（2026-08-20 补）─────────────────────────────────────────
        //
        //  ★ 在它之前这里是 **ASAP 排产**：每台设备每班顶格干，直到单元采完。
        //    实测后果 —— 全月 367 万m³ 的采装被压进前十几天，逐日条数单调衰减到 1 台，
        //    月末排了班却一条任务都没有。它**不报错**：量守恒、时窗合法、每一行都对，
        //    只是"这个月的活什么时候干"这件事没人管过。
        //
        //  配额把"今天最多排多少"变成一条硬约束：v = min(设备能力, 单元剩余, 当日剩余配额)。
        //  缺省配额按**当天能力**摊（不是等分）：定修多的日子、少上一个班的日子自动少排 ——
        //  与 ShortTermLink/DayCapacityCalendar 的日目标同一个思路。
        //  下达过周目标时，那一周的配额由调用方换成周目标拆出来的量（见 loadQuotaOf）。
        //
        //  ⚠ 配额只封顶、不保底：能力不够就排不满，缺口照旧记在 Shortfalls 里。
        //    反过来做（配额当目标硬塞）就是拿计划量冒充能力。
        var quota = loadQuotaOf ?? BuildDefaultQuota(us, ms, ds, shiftsOf);

        //  穿孔配额 = **lead 天之后那天的采装配额**（超前的语义就是这个：
        //  今天打的孔，是 lead 天后要挖的那批量）。期末最后 lead 天不再打孔 ——
        //  那几天打的孔属于下一期，本期排出来只会占着钻机而没有下游。
        var drillQuota = BuildDrillQuota(ds, quota, blastLeadDays);

        // 配额的消耗按 (日, 班) 记 —— 只按日记的话早班会把一天的量吃光（见下面那段）
        var usedLoad = new Dictionary<(DateTime, string), double>();
        var usedDrill = new Dictionary<(DateTime, string), double>();

        for (int di = 0; di < ds.Count; di++)
        {
            var date = ds[di];
            string dk = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var shifts = (shiftsOf(date) ?? Array.Empty<PlanShift>()).OrderBy(s => s.StartHour).ToList();
            if (shifts.Count == 0) continue;
            double dayHours = shifts.Sum(s => s.EffectiveHours);   // 配额按班分份用它当分母
            // ★ 折算分母要按**这台设备当天真正能上的班**算（2026-08-20，随 M10 一起补）：
            //   钻机只上白班，日台效却按 24h 折 ⇒ 白班只给 1/3 的量，等于把台账里的日台效砍掉 2/3。
            //   台账的 M3PerDay 是"这台钻机一天打多少"，它本来就只在白班干 —— 分母就该是白班的工时。
            //   实测：不改的话整月控制方量从 381 万m³ 掉到 141 万m³，而下游的穿爆超前照单全收，
            //   采装被挡掉一大片，看着像"设备不够"。
            double dayHoursDayShift = shifts.Where(IsDayShift).Sum(s => s.EffectiveHours);

            foreach (var sh in shifts)
            {
                // M1：这一班，每个作业面只许一道工序在动
                var faceBusy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // M2：一台设备同一班只在一处
                var machBusy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // 这个面上"前一道工序几点干完" —— 衔接关系就靠它（穿孔→爆破→采装 顺着往后推）
                var faceReady = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                // ── 爆破（M9，2026-08-20 补）───────────────────────────────
                //
                //  ★ 爆破此前**在计划里根本不存在**：`blast_event` 是事后填的事实表
                //    （炮次/装药/单耗），设备台账里也没有爆破队这个类别，
                //    于是工序链「穿孔→爆破→采装」中间那一环在甘特上是空的 ——
                //    而 M3 明明拿"穿爆超前"挡着采装，挡它的那件事却画不出来。
                //
                //  排法：孔打完的单元，**在下一个作业日的爆破班**放一炮（一个单元只爆一次）。
                //  执行人留空 —— 爆破队台账还没有，编一个队号出来是假的（口径由用户定：不指人）。
                //  这一炮**占住这个面这一班**（M1）：爆破当班这个面不装车，现场就是这样。
                foreach (var u in us)
                {
                    if (u.BlastDay >= 0) continue;                    // 爆过了
                    if (!u.NeedsBlasting) continue;                   // 免爆的面不排爆破
                    if (u.DrillDoneDay < 0 || u.DrillDoneDay >= di) continue;   // 孔没打完 / 就是今天打完的
                    if (faceBusy.Contains(u.FaceName)) continue;      // 这个面这一班已经有活
                    if (!IsBlastShift(sh, shifts)) continue;          // 只在爆破班放炮

                    // 爆破占**清场时长**，不占整班；这个面在清场结束之前不能装车（衔接关系）
                    double bStart = Math.Max(sh.StartHour, faceReady.TryGetValue(u.FaceName, out double fr0) ? fr0 : sh.StartHour);
                    double bEnd = Math.Min(sh.EndHour, bStart + PlanShift.BlastClearHours);
                    faceReady[u.FaceName] = bEnd;

                    res.Rows.Add(new ShiftTaskRow
                    {
                        Date = dk, Shift = sh.Name,
                        StartHour = bStart, EndHour = bEnd,
                        EffectiveHours = sh.EffectiveHours, IsBlastShift = true,
                        MachineId = "", MachineModel = "",           // 执行人留空：没有爆破队台账
                        Process = ProcessType.Blast,
                        FaceName = u.FaceName, UnitId = u.UnitId,
                        MaterialCode = u.MaterialCode,
                        VolumeM3 = 0,                                 // 爆破不单记量：它的量就是穿孔的控制方量
                        Basis = BasisOf(ProcessType.Blast),
                    });
                    u.BlastDay = di;
                    faceBusy.Add(u.FaceName);
                }

                // 设备按台效从大到小派 —— 大机先咬住大单元，小机去收尾，
                // 反过来会让大机整班在一个小单元上吃不饱（而班表看着满满当当）
                foreach (var m in ms.OrderBy(x => ChainOrder(x.Process))
                                    .ThenByDescending(x => x.RatePerDay)
                                    .ThenBy(x => x.MachineId, StringComparer.Ordinal))
                {
                    if (machBusy.Contains(m.MachineId)) continue;

                    // M10：**穿孔只排白班**（与爆破同一条规矩）。
                    //   夜里排穿孔，班表看着满满当当，而现场那几台钻机根本不出勤 ——
                    //   下游的"穿爆超前"于是按一份不存在的进度在放行采装。
                    if (m.Process == ProcessType.Drill && !IsDayShift(sh)) continue;

                    var pick = PickUnit(us, m, faceBusy, di, blastLeadDays);
                    if (pick == null) continue;

                    // ★ 两个来源的 RatePerDay **口径不一样**，分母不能一刀切：
                    //   · 采装 = 编组班产(m³/h) × 24h —— 它是**小时口径折出来的一天**，
                    //     分母必须是 24：两班制的矿一天就只有 16/24 的量（少上一个班就少一个班的活）。
                    //   · 钻机 = 台效台账的 M3PerDay —— 它是**实测的日台效**，
                    //     这台钻机本来就只在白班干，分母就该是白班的工时；
                    //     按 24 折等于把台账里的日台效砍掉 2/3。
                    //   同一个字段两种口径，判分母的地方必须把这件事写出来，否则下一个人只会看到一个魔数。
                    double basis = m.Process == ProcessType.Drill ? dayHoursDayShift : RateBasisHoursPerDay;
                    if (basis <= 1e-9) basis = RateBasisHoursPerDay;
                    double cap = m.RatePerDay > 1e-9
                        ? m.RatePerDay * sh.EffectiveHours / basis
                        : 0;
                    if (!(cap > 1e-9)) continue;                 // 没台效 = 不可派，不是"能力无限"

                    double left = m.Process == ProcessType.Drill
                        ? pick.DrillM3 - pick.DoneDrillM3
                        : pick.RemainM3 - pick.DoneM3;

                    // 本班剩余配额（第三条约束，见上面那段注释）
                    //
                    // ★ 配额要**按班再分一次**，不能只按天：只按天的话早班会把一天的量吃光，
                    //   中夜班无事可做 —— 实测 6 台钻机全挤在早班，甘特上就是"钻机只上早班"。
                    //   一班一份，份额按本班有效工时占全天的比例（爆破班扣了清场，自然少一点）。
                    var usedMap = m.Process == ProcessType.Drill ? usedDrill : usedLoad;
                    double dayCap = m.Process == ProcessType.Drill ? drillQuota(date) : quota(date);
                    double roomLeft;
                    if (double.IsPositiveInfinity(dayCap))
                    {
                        roomLeft = double.PositiveInfinity;
                    }
                    else
                    {
                        // 配额按班分份也用同一个分母：钻机的日配额全落在白班里（它只有白班能干）
                        double qBasis = m.Process == ProcessType.Drill ? dayHoursDayShift : dayHours;
                        double shiftCap = qBasis > 1e-9 ? dayCap * sh.EffectiveHours / qBasis : 0;
                        var key = (date, sh.Name);
                        roomLeft = shiftCap - (usedMap.TryGetValue(key, out double u0) ? u0 : 0);
                    }
                    if (!(roomLeft > 1e-6)) continue;          // 这一班的量排满了，这台设备本班就到这儿

                    double v = Math.Min(Math.Min(cap, left), roomLeft);
                    if (!(v > 1e-6)) continue;

                    var k2 = (date, sh.Name);
                    usedMap[k2] = (usedMap.TryGetValue(k2, out double u1) ? u1 : 0) + v;

                    // ── 实际时长与衔接（2026-08-20）──────────────────────────
                    //  ★ 条的起止原来写死成**班的起止** ⇒ 每根条都铺满 8 小时，
                    //    而"这笔活要干多久"其实一直算得出来（量 ÷ 每小时能力）。
                    //    于是图上看不到任何衔接与等待：穿孔打完等超前、爆破清场、采装才开始 ——
                    //    这些全被抹平成"三班都满"。
                    //  · 起点 = max(班起点, 这个面上一道工序在本班的结束时刻)  ← 衔接
                    //  · 长度 = 本笔量 ÷ 每小时能力，封顶到班末              ← 实际时长
                    //  · 空出来的时间就是等待，本来就该看得见
                    double perHour = basis > 1e-9 ? m.RatePerDay / basis : 0;
                    double start = Math.Max(sh.StartHour, faceReady.TryGetValue(pick.FaceName, out double fr) ? fr : sh.StartHour);
                    double dur = perHour > 1e-9 ? v / perHour : sh.EffectiveHours;
                    double end = Math.Min(sh.EndHour, start + dur);
                    if (end <= start + 1e-6) continue;          // 这一班已经排满，这笔挪到后面的班
                    faceReady[pick.FaceName] = end;

                    var row = new ShiftTaskRow
                    {
                        Date = dk, Shift = sh.Name,
                        StartHour = start, EndHour = end,
                        EffectiveHours = sh.EffectiveHours, IsBlastShift = sh.IsBlast,
                        MachineId = m.MachineId, MachineModel = m.Model,
                        Process = m.Process,
                        FaceName = pick.FaceName, UnitId = pick.UnitId,
                        MaterialCode = pick.MaterialCode,
                        VolumeM3 = v,
                        Basis = BasisOf(m.Process),
                    };

                    // M6：任务区域 = 这一班在这个单元上推过的那一段（按量切、按面积反求宽度）
                    if (m.Process == ProcessType.Load) CutZone(pick, v, row, res);

                    res.Rows.Add(row);
                    faceBusy.Add(pick.FaceName);
                    machBusy.Add(m.MachineId);

                    if (m.Process == ProcessType.Drill)
                    {
                        pick.DoneDrillM3 += v;
                        if (pick.DoneDrillM3 >= pick.DrillM3 - 1e-6 && pick.DrillDoneDay < 0)
                            pick.DrillDoneDay = di;
                    }
                    else if (m.Process == ProcessType.Load) pick.DoneM3 += v;
                }
            }
        }

        Summarize(us, res, blastLeadDays);
        return res;
    }

    /// <summary>工序链序：穿孔 → 爆破 → 采装 → 运输 → 排土。按它排设备，衔接才推得下去。</summary>
    private static int ChainOrder(ProcessType p) => p switch
    {
        ProcessType.Drill => 0,
        ProcessType.Blast => 1,
        ProcessType.Load => 2,
        ProcessType.Haul => 3,
        ProcessType.Dump => 4,
        _ => 9,
    };

    /// <summary>
    /// 哪一班放炮：台账标了爆破班就用它；一个都没标时用**含缺省爆破时刻（12:00）的那一班**。
    /// <para>不随便挑一班：炮在哪一班放决定了哪一班这个面不装车，挑错了班表就与现场对不上。</para>
    /// </summary>
    private static bool IsBlastShift(PlanShift sh, List<PlanShift> all)
    {
        // M10：爆破只在**白班**（见 IsDayShift）。台账标了爆破班的，也要落在白班里才算数 ——
        // 标错班的那一条不该把炮排到夜里去。
        if (!IsDayShift(sh)) return false;
        if (all.Any(x => x.IsBlast && IsDayShift(x))) return sh.IsBlast;
        const double DefaultBlastHour = 12.0;
        return sh.StartHour <= DefaultBlastHour && DefaultBlastHour < sh.EndHour;
    }

    /// <summary>
    /// 白班判定（M10）：该班时窗与白天 <c>[6:00, 18:00]</c> 的重叠**占本班一半以上**。
    ///
    /// <para>
    /// <b>为什么不写死"中班"</b>：班名与班制是矿定的（这个矿是 早 00–08 / 中 08–16 / 夜 16–24，
    /// 别的矿可能四班三运转、或交接时刻不在整点）。按名字判，换一套班制就静默失效 ——
    /// 而失效的样子是"穿孔爆破排到夜里"，图上看不出异常。按时窗判，换班制自己就跟着走。
    /// </para>
    /// <para>
    /// 现场规矩：穿孔与爆破**只在白班**（视线、警戒、装药与起爆的安全条件都只有白天具备）。
    /// </para>
    /// </summary>
    internal static bool IsDayShift(PlanShift sh)
    {
        const double DayFrom = 6.0, DayTo = 18.0;
        double hours = Math.Max(0, sh.EndHour - sh.StartHour);
        if (hours <= 1e-6) return false;
        double overlap = Math.Max(0, Math.Min(sh.EndHour, DayTo) - Math.Max(sh.StartHour, DayFrom));
        return overlap > hours / 2.0;
    }

    // ── 逐日配额 ──────────────────────────────────────────────────────

    /// <summary>
    /// 缺省的采装日配额：<b>本期采装总量 × 当天能力 ÷ 全期能力</b>。
    ///
    /// <para>
    /// 当天能力 = Σ<sub>采装设备</sub> 日台效 × (当天各班有效工时之和 ÷ 24)。
    /// 所以定修/爆破清场多的日子、少上一个班的日子自动少排 —— 与
    /// <see cref="DayCapacityCalendar"/> 给 <see cref="ShortTermLink"/> 的日目标是同一个思路，
    /// 只是这里在分解器内部算（分解器不碰库，拿不到检修档期，只能用班表这一层的信息）。
    /// </para>
    /// <para>
    /// 全期能力为 0（没有采装设备/没有班）时返回 <c>+∞</c> —— <b>不封顶</b>，
    /// 让原来的能力约束继续起作用。给一个 0 配额会让整期一条任务都排不出来，
    /// 而"排不出来"的原因会被记成"配额用完了"，那是假的。
    /// </para>
    /// </summary>
    private static Func<DateTime, double> BuildDefaultQuota(
        List<PlanUnit> us, List<PlanMachine> ms, List<DateTime> ds,
        Func<DateTime, IReadOnlyList<PlanShift>> shiftsOf)
    {
        double totalLoad = us.Sum(u => Math.Max(0, u.RemainM3));
        double rateSum = ms.Where(m => m.Process == ProcessType.Load).Sum(m => Math.Max(0, m.RatePerDay));

        var w = new Dictionary<DateTime, double>();
        double wSum = 0;
        foreach (var d in ds)
        {
            // 采装侧全天可上，分母是全天工时（钻机的配额另按白班折，见上面 qBasis）
            double hours = (shiftsOf(d) ?? Array.Empty<PlanShift>()).Sum(s => s.EffectiveHours);
            double wd = rateSum * hours / RateBasisHoursPerDay;
            w[d] = wd;
            wSum += wd;
        }

        if (!(totalLoad > 1e-6) || !(wSum > 1e-6))
            return _ => double.PositiveInfinity;

        return d => w.TryGetValue(d, out double x) ? totalLoad * x / wSum : 0;
    }

    /// <summary>
    /// 穿孔日配额 = <b>lead 个作业日之后那天的采装配额</b>。
    ///
    /// <para>
    /// 「穿爆超前」的语义就是这个：今天打的孔，是 lead 天后要挖的那批量。
    /// 期末最后 lead 天配额为 0 —— 那几天打的孔属于下一期，本期排出来只会占着钻机而没有下游。
    /// </para>
    /// <para>
    /// 不这么做的话钻机会一次把一个单元的孔全打完（它的台效大到永远碰不到能力上限）：
    /// 实测全月的孔在前 8 天打完，之后钻机整月空着，而当日甘特里一条穿孔条都没有。
    /// </para>
    /// </summary>
    private static Func<DateTime, double> BuildDrillQuota(
        List<DateTime> ds, Func<DateTime, double> loadQuota, double leadDays)
    {
        int lead = Math.Max(0, (int)Math.Ceiling(leadDays));
        var map = new Dictionary<DateTime, double>();
        for (int i = 0; i < ds.Count; i++)
        {
            // 期末最后 lead 天没有"本期的下游"可对 —— 但**不能因此归 0**：
            // 那几天打的孔服务的是**下一期**，现场本来就在超前打。归 0 会让每个月末
            // 钻机集体停工三天，而下个月初的采装又因为没孔可挖被穿爆超前挡住 ——
            // 两处都不报错，只是每个月接不上茬。按期末那天的配额继续打。
            map[ds[i]] = i + lead < ds.Count ? loadQuota(ds[i + lead]) : loadQuota(ds[^1]);
        }
        return d => map.TryGetValue(d, out double x) ? x : 0;
    }

    // ── 选单元 ────────────────────────────────────────────────────────

    /// <summary>
    /// 给这台设备找它此刻该干的单元。找不到返回 null（这一班它闲着 —— <b>闲着就是闲着</b>）。
    /// </summary>
    private static PlanUnit? PickUnit(List<PlanUnit> us, PlanMachine m,
                                      HashSet<string> faceBusy, int dayIndex, double lead)
    {
        foreach (var u in us)
        {
            if (faceBusy.Contains(u.FaceName)) continue;                 // M1
            if (!m.CanTake(u.MaterialCode)) continue;                    // M8

            if (m.Process == ProcessType.Drill)
            {
                if (!u.NeedsBlasting) continue;                          // 免爆的面不派钻机
                if (u.DoneDrillM3 >= u.DrillM3 - 1e-6) continue;
                return u;
            }
            if (m.Process == ProcessType.Load)
            {
                if (u.DoneM3 >= u.RemainM3 - 1e-6) continue;
                // M3：穿孔必须超前采装 —— 没打完孔、或还没过超前期，这个单元开不了挖
                if (u.NeedsBlasting)
                {
                    if (u.DrillDoneDay < 0) continue;
                    if (dayIndex < u.DrillDoneDay + Math.Max(0, (int)Math.Ceiling(lead))) continue;
                }
                return u;
            }
        }
        return null;
    }

    // ── 切地 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 把这一班的量切成一块任务区域。<b>无真轨就不切</b>（任务照排，但没有地）——
    /// 拿质心盒子编一块出来，派工按它去现场会找不到地方。
    /// </summary>
    private static void CutZone(PlanUnit u, double v, ShiftTaskRow row, ShiftPlanResult res)
    {
        if (u.Rail == null || !u.Rail.IsValid) return;
        var parts = new List<(string, double)>();
        if (u.DoneM3 > 1e-9) parts.Add(("已采", u.DoneM3));
        parts.Add(("本班", v));
        double rest = u.RemainM3 - u.DoneM3 - v;
        if (rest > 1e-9) parts.Add(("待采", rest));

        var split = TaskZoneSplitter.Split(u.Rail, u.TowardCrest, parts);
        var mine = split.Slices.FirstOrDefault(s => s.Key == "本班");
        if (mine == null || !mine.Ok) return;

        row.W0 = mine.W0; row.W1 = mine.W1;
        row.ZoneRingXy = mine.RingXy;
        row.ZoneAreaM2 = mine.AreaM2;
    }

    // ── 记账 ──────────────────────────────────────────────────────────

    private static void Summarize(List<PlanUnit> us, ShiftPlanResult res, double lead)
    {
        // M7：排不下的量单列 —— 月底对不上账全从这儿来
        foreach (var u in us)
        {
            double leftLoad = u.RemainM3 - u.DoneM3;
            if (leftLoad > Math.Max(1.0, u.RemainM3 * 1e-6))
                res.Shortfalls.Add(new ShiftShortfall(u.UnitId, "原位实方", leftLoad,
                    u.NeedsBlasting && u.DrillDoneDay < 0
                        ? "穿孔没打完 —— 这个单元本期开不了挖（M3：穿爆超前是硬约束）"
                        : "本期的班数 × 设备能力吃不下它"));

            double leftDrill = u.DrillM3 - u.DoneDrillM3;
            if (u.NeedsBlasting && leftDrill > Math.Max(1.0, u.DrillM3 * 1e-6))
                res.Shortfalls.Add(new ShiftShortfall(u.UnitId, "控制方量", leftDrill,
                    "钻机能力吃不下 —— 穿孔排不完，下游的采装跟着排不上"));
        }

        var byBasis = res.ByBasis.Select(x => $"{x.Basis} {x.M3 / 1e4:0.##} 万m³").ToList();
        res.Headline = $"分解出 {res.Rows.Count} 条班组任务"
                     + $"（{res.Rows.Select(r => r.Date).Distinct().Count()} 个作业日 · "
                     + $"{res.Rows.Select(r => r.MachineId).Distinct().Count()} 台设备 · "
                     + $"{res.Rows.Select(r => r.FaceName).Distinct().Count()} 个作业面）"
                     + (byBasis.Count > 0 ? "：" + string.Join(" · ", byBasis) : "");

        if (res.Shortfalls.Count > 0)
        {
            var s = res.Shortfalls.GroupBy(x => x.Basis)
                       .Select(g => $"{g.Key} {g.Sum(y => y.M3) / 1e4:0.##} 万m³").ToList();
            res.Notes.Add($"◆ **本期排不下**：{string.Join(" · ", s)}（{res.Shortfalls.Count} 笔）。"
                        + "这些量没有落到任何一个班上 —— **排不下不等于不用干**，"
                        + "月底会对不上账。要么加设备/加班制，要么把这部分挪到下期并在月计划上认掉。");
            foreach (var x in res.Shortfalls.Take(5))
                res.Notes.Add($"　· {x.UnitId}：{x.M3:N0} m³（{x.Basis}）—— {x.Why}");
            if (res.Shortfalls.Count > 5) res.Notes.Add($"　· …等 {res.Shortfalls.Count} 笔");
        }

        int noZone = res.Rows.Count(r => r.Process == ProcessType.Load && r.ZoneRingXy.Length < 6);
        if (noZone > 0)
            res.Notes.Add($"◆ {noZone} 条采装任务**切不出任务区域**（那些单元没有真轨）—— "
                        + "任务本身成立，但派工点开它定位不到图上，"
                        + "「这一班推到哪儿」也说不出来。补法：跑一次采矿模型，再从模型取一次台账。");

        res.Notes.Add($"· 口径：同一作业面同一班**只有一道工序**（钻机与电铲不同时在一个面上），"
                    + $"并行只发生在面与面之间；穿爆超前 {lead:0.#} 个作业日是硬约束，挡住的量记在缺口里。");
        res.Notes.Add("· **四个量口径分开列，不给总量**：原位实方 / 控制方量 / 排弃占容 / 承运量。"
                    + "加起来那个数不对应任何真实量。");
    }

    private static void ResetCursor(PlanUnit u)
    { u.DoneM3 = 0; u.DoneDrillM3 = 0; u.DrillDoneDay = u.NeedsBlasting ? -1 : 0; u.BlastDay = -1; }

    private static string BasisOf(ProcessType p) => p switch
    {
        ProcessType.Drill => "控制方量",
        // 爆破不单记方量：它爆的就是穿孔那一笔控制方量，再记一遍就成了两份账
        ProcessType.Blast => "炮次",
        ProcessType.Dump => "排弃占容",
        ProcessType.Haul => "承运量",
        _ => "原位实方",
    };
}
