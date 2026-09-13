// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ProcessZoneFaceSource.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;                  // MiningUnitLedger / MonthlyUnitLedgerStore
using PitMine3D.Kylin.Data;                    // EquipmentDataContext
using PitMine3D.Kylin.Data.Entities;           // ProcessZone
using PitMine3D.Kylin.TaskLib.Domain;                        // MaterialCatalog / ProcessType
using PitMine3D.Kylin.TaskLib.Zoning;                        // ProcessZoneStore

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  工序作业区 → 作业面清单。整条真实数据链上**此前缺的那一环**。
//
//  ══ 这一环之前是断的 ══
//  链路本来是：月度计划 → 剥采清单（采掘单元台账）→ 块段模型 → 作业区划分 → 任务编制。
//  可 `ProductionPlanContext.Config()` 的作业面只有一个来源：`working_face_routing` 台账。
//  那张表空着（新工程、或还没在「作业面台账」里保存过一次）时，整盘面**整体落到样例**——
//  于是月计划排得再细，任务编制里排的仍是「主采面·东」这种示例露天矿的面。
//  而界面上只有一行小字说了这件事。
//
//  ══ 派生的口径 ══
//  与作业区划分同一条（Z 组现场口径）：**一块作业区域 = 一个作业面（工程位置）**。
//  所以本期的**采装工序区**就是本期的采装作业面清单，**排土卸载区**就是排土作业面清单 ——
//  同一条口径正着读一遍而已，不是第二套定义。
//
//  ══ 五条口径 ══
//  **F1 只认本期、只认 active 的工序区**。工序区是按期的，上个月那批不是本月的作业面。
//  **F2 补齐，不顶替**：`working_face_routing` 里已有的面**原样保留**（人在那儿填过去向、
//      运距、设备、煤质，全是手工资产）；只把台账里没有、而本期确实有工序区的面补进来，
//      并逐个标明是派生的。反过来做会把人填的东西冲掉，而且不报错。
//  **F3 UnitId 取队首**（`Seq` 最小且未采完的那个），不是"随便挑一个"也不是"全塞进去"。
//      一个面一个月要顺着好几个单元推过去，`FaceInput.UnitId` 是单数 ——
//      它表达的是**此刻在哪个单元上作业**；而 `AvailableReserveM3` 是这个面脚下**全部**剩余量。
//      两者混成一个数，「这个面还能采几天」就永远只算得出一个单元的天数。
//  **F4 量口径按工序取，绝不混列**：采装取原位实方；排土面不给日目标，
//      走 `DerivedFromInbound`（由入方物料流推导，采排守恒）。
//      把排弃占容当成日目标填进采装那一列，谁一 SUM 就得到一个不对应任何真实量的数。
//  **F5 物料从台账类型 + 层名判**，判不出就说判不出（回落硬岩是 `MaterialCatalog` 的既定保守口径，
//      但**要报出来有几个是回落的**）—— 物料决定需不需要爆破、允许进哪些去向，
//      静默回落会让表土被排进内排场而没有任何东西报错。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>本期工序作业区 → 作业面清单。永不抛，失败以 null + 文案表达。</summary>
public static class ProcessZoneFaceSource
{
    /// <summary>最近一次 <see cref="Derive"/> 的来源文案。</summary>
    public static string LastSourceLabel { get; private set; } = "";

    /// <summary>最近一次派生里要人知道的事（逐条，界面直接显示）。</summary>
    public static List<string> LastNotes { get; } = new();

    /// <summary>
    /// 派生本期的作业面。
    /// </summary>
    /// <param name="period">期次 yyyy-MM；空 = 按当前工作日期所在月。</param>
    /// <param name="workdays">月作业日（摊日目标用）；&lt;=0 = 从 <see cref="WorkCalendar"/> 取。</param>
    /// <param name="ledgerRoot">台账根目录；null = 默认。判据用它指到临时目录。</param>
    /// <returns>派生不出来返回 null（原因在 <see cref="LastSourceLabel"/>）。</returns>
    public static List<FaceInput>? Derive(string? period = null, double workdays = 0, string? ledgerRoot = null)
    {
        LastNotes.Clear();
        string month = (period ?? "").Trim();
        if (month.Length == 0)
        {
            try { month = ProjectScope.WorkDate.ToString("yyyy-MM", CultureInfo.InvariantCulture); }
            catch { month = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture); }
        }

        // ── ① 本期工序区（F1）──
        List<ProcessZone> zones;
        try { zones = ProcessZoneStore.LoadPeriod(month); }
        catch (Exception ex)
        {
            LastSourceLabel = $"作业面·派生：工序作业区台账读不到（{Short(ex)}）";
            return null;
        }

        // ★ 工作日期那个月没有工序区时，**退到台账里最新的一期**（2026-08-19 补）。
        //
        //   实测：工程工作日期停在 2026-06-17，而排产排的是 2026-08、工序区也入库在 2026-08。
        //   原来这里按工作日期取月份、取不到就返回 null ⇒ 盘子的面数是 0、面来源 None ⇒
        //   逐日逐班切不出来、甘特空、达成度算不了 —— **而入库明明成功了 75 块**。
        //   界面上的提示还写着「入库后面就会派生出来」，于是那句话成了一句假承诺。
        //
        //   ⚠ 退的时候必须说出来（与 `ZonePlanSource.DefaultMonth` 同一条纪律）：
        //     不说的话，人以为在看本月，其实在看两个月前那一期，而面上每个数都正常。
        if (zones.Count == 0 && string.IsNullOrWhiteSpace(period))
        {
            string latest = LatestPeriodWithZones(month);
            if (latest.Length > 0)
            {
                try { zones = ProcessZoneStore.LoadPeriod(latest); } catch { zones = new List<ProcessZone>(); }
                if (zones.Count > 0)
                {
                    LastNotes.Add($"◆ 工程工作日期在 {month}，但那一期**没有工序作业区** —— "
                                + $"已退到台账里最新的一期「{latest}」（{zones.Count} 块）。"
                                + "两者不一致时看到的是另一期的面，而面上每个数都正常。"
                                + "对不上就去「工程设置」把工作日期改到本期，或给本期生成一次工序区。");
                    month = latest;
                }
            }
        }
        // ── ② 本期台账（量 / 物料 / 方位角 / 排产序都在这儿）──
        var rowById = new Dictionary<string, MiningUnitLedger.Row>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var store = new MonthlyUnitLedgerStore(ledgerRoot);
            if (store.Exists(month))
            {
                store.TryLoad(month, out var rows, out _);
                foreach (var r in rows ?? new List<MiningUnitLedger.Row>())
                    if (r != null && !string.IsNullOrWhiteSpace(r.UnitId)) rowById[r.UnitId.Trim()] = r;
            }
        }
        catch { /* 台账读不到：量与物料退到工序区自己带的那份，DeriveFrom 里会报出来 */ }

        return DeriveFrom(month, zones, rowById, workdays,
                          crewLedger: LoadFaceCrews());
    }

    /// <summary>
    /// 作业面台账里的一条「台阶 → 主电铲」。
    /// <para><b>主电铲派生不出来</b>：工序作业区只给位置与量，配哪台铲是人定的，
    /// 存在 <c>working_face.equipment_id</c>。这个类型只是把那一列搬过来，不做任何推断。</para>
    /// </summary>
    public sealed class FaceCrew
    {
        public string FaceCode = "";
        /// <summary>平盘标高（m）。<b>NaN = 台账的平盘编码不是标高</b>，那样这一行认不了。</summary>
        public double BenchElevM = double.NaN;
        public string MainEquipment = "";
        /// <summary>物料码（rh / coal …）。空 = 不限。</summary>
        public string Material = "";
    }

    /// <summary>标高认领的容差（m）。与归属口径 FA3 同一个数量级。</summary>
    public const double CrewElevTolM = 8.0;

    /// <summary>
    /// 读作业面台账里的「台阶 → 主电铲」。
    /// <para><b>平盘编码按标高解</b>（<c>working_face.location_code</c>，实测值形如 1195/1210/1240）——
    /// 这是这张表的约定。解不出数的行**不认领**，并在派生说明里点名，不猜。</para>
    /// </summary>
    private static List<FaceCrew> LoadFaceCrews()
    {
        var list = new List<FaceCrew>();
        try
        {
            foreach (var f in EquipmentDataContext.Current.WorkingFaces.ActiveAll())
            {
                if (f == null || string.IsNullOrWhiteSpace(f.EquipmentId)) continue;
                double z = double.NaN;
                if (double.TryParse((f.LocationCode ?? "").Trim(),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) z = v;
                list.Add(new FaceCrew
                {
                    FaceCode = f.FaceCode ?? "",
                    BenchElevM = z,
                    MainEquipment = f.EquipmentId!.Trim(),
                    Material = f.Material ?? "",
                });
            }
        }
        catch { /* 台账读不到：DeriveFrom 里会按"一条都没有"报出来 */ }
        return list;
    }

    /// <summary>
    /// 台账里的哪一条管这个标高（同物料 + 标高最近且在容差内）。
    /// <para><b>并列时不认领</b>：同标高同物料摆了两条，只有现场知道哪台铲在哪个面 —— 与归属口径 FA5 同一条纪律。</para>
    /// </summary>
    internal static FaceCrew? PickCrew(IReadOnlyList<FaceCrew>? crews, double benchZ, bool isCoal,
                                       double tolM = CrewElevTolM)
    {
        if (crews == null || crews.Count == 0 || double.IsNaN(benchZ)) return null;
        var cand = crews
            .Where(c => c != null && !double.IsNaN(c.BenchElevM)
                     && Math.Abs(c.BenchElevM - benchZ) <= Math.Max(0, tolM)
                     && MatOk(c.Material, isCoal))
            .OrderBy(c => Math.Abs(c.BenchElevM - benchZ))
            .ToList();
        if (cand.Count == 0) return null;
        // FA5 同纪律：并列就不认领
        if (cand.Count > 1 &&
            Math.Abs(Math.Abs(cand[0].BenchElevM - benchZ) - Math.Abs(cand[1].BenchElevM - benchZ)) < 1e-6)
            return null;
        return cand[0];
    }

    /// <summary>物料码是不是煤（与 MatOk 同一份口径，别各判各的）。</summary>
    private static bool IsCoalMat(string? mat)
    {
        string m = (mat ?? "").Trim();
        return m.Equals("coal", StringComparison.OrdinalIgnoreCase)
            || m.Equals("lowgrade", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>物料相容：空 = 不限；coal 只配煤面，其余只配岩面。</summary>
    private static bool MatOk(string? mat, bool isCoal)
    {
        string m = (mat ?? "").Trim();
        if (m.Length == 0) return true;
        bool crewIsCoal = m.Equals("coal", StringComparison.OrdinalIgnoreCase)
                       || m.Equals("lowgrade", StringComparison.OrdinalIgnoreCase);
        return crewIsCoal == isCoal;
    }

    /// <summary>
    /// 台账里**有工序区**的最新一期（排除已经试过的那一期）。
    /// <para>只看有没有工序区，不看期次名排序之外的东西 —— 期次名是 <c>yyyy-MM</c>，字典序即时间序。</para>
    /// </summary>
    private static string LatestPeriodWithZones(string tried)
    {
        try
        {
            return ProcessZoneStore.ListPeriods()
                   .Where(p => !string.IsNullOrWhiteSpace(p)
                            && !string.Equals(p, tried, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(p => p, StringComparer.Ordinal)
                   .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 派生的算法本体（工序区与台账都已装好时用这一版）。
    /// <para><b>离线判据喂合成算例走它</b> —— 裸台架里所有台账路径都静默走兜底，
    /// 拿 <see cref="Derive"/> 判等于什么都没判。</para>
    /// </summary>
    /// <param name="sinkOverride">去向登记簿；null = 走 <see cref="SinkRegistryLoader"/>。
    /// <b>判据用它喂算例</b> —— 裸台架里那个装载器一律回落样例去向，绑出来的汇不是算例里的。</param>
    /// <param name="qualityPick">按 (煤层号, x, y) 取煤质；null = 走 <see cref="SeamQualitySampler"/> 读库。
    /// <b>判据用它喂算例</b>。</param>
    public static List<FaceInput>? DeriveFrom(string month, IReadOnlyList<ProcessZone>? allZones,
                                              IReadOnlyDictionary<string, MiningUnitLedger.Row>? ledger,
                                              double workdays = 0, SinkRegistry? sinkOverride = null,
                                              Func<string, double, double, SeamQualityPick>? qualityPick = null,
                                              IReadOnlyList<FaceCrew>? crewLedger = null)
    {
        LastNotes.Clear();
        var zones = (allZones ?? Array.Empty<ProcessZone>()).Where(z => z != null).ToList();
        var rowById = ledger ?? new Dictionary<string, MiningUnitLedger.Row>(StringComparer.OrdinalIgnoreCase);

        // F1：只认 active，且只认**采装 / 排土卸载**两类。
        //     穿孔区是同一个面的另一段地 —— 算成第二个面的话，同一块料会被排两遍产；
        //     爆破警戒区是禁入区，语义与作业面正相反。
        var usable = zones
            .Where(z => z.Active != 0
                     && (Eq(z.Process, ProcessZone.ProcLoad) || Eq(z.Process, ProcessZone.ProcDumpTip)))
            .ToList();
        if (usable.Count == 0)
        {
            LastSourceLabel = zones.Count == 0
                ? $"作业面·派生：{month} 还没有工序作业区 —— 到「作业区划分」生成并入库一次即可"
                : $"作业面·派生：{month} 有 {zones.Count} 块工序区，但没有一块是**采装或排土卸载**且已纳入本期"
                  + "（穿孔区与爆破警戒区不构成作业面：前者是同一个面的另一段地，后者是禁入区）";
            return null;
        }

        // ★ 派生的面**一律没有主电铲**（2026-08-19 补说明）。
        //   工序作业区给的是位置与量；配哪台铲是 `working_face.equipment_id` 那一列，人定的。
        //   下游 `ShiftPlanAssembler.LoadMachines` 是**按面取设备**的，
        //   于是台账空着时它报「一台可派设备都没有」—— 读起来像设备库空了，
        //   而设备库里可能有几百台。这句话必须在派生这一步就说清楚。
        if (rowById.Count == 0)
            LastNotes.Add($"◆ {month} 的**月度台账读不到**（工序区还在，台账没了或换了目录）—— "
                        + "备采量只能用工序区入库当时记下的那个数，物料一律按硬岩回落。"
                        + "这两样都会往下影响需不需要爆破、允许进哪些去向。");

        double wd = workdays > 0 ? workdays : ResolveWorkdays(month);

        // ── ③ 逐块工序区 → 一个面 ──
        // 去向登记簿：排土面按台账的排土场名绑汇（**不猜**：对不上就点名，见下）
        SinkRegistry? sinks = sinkOverride;
        if (sinks == null) { try { sinks = SinkRegistryLoader.Current; } catch { } }
        var unboundDump = new List<string>();

        var faces = new List<FaceInput>();
        int fallbackMat = 0, noUnit = 0, multiUnit = 0, degenerateCentroid = 0;
        int crewHit = 0; var crewMiss = new List<string>();
        int qualityOk = 0;
        var qualityMiss = new List<string>();

        foreach (var z in usable)
        {
            bool isDump = Eq(z.Process, ProcessZone.ProcDumpTip);
            var ring = ProcessZoneStore.ParseRing(z);
            var ids = SplitIds(z.UnitIds);
            var rows = ids.Select(id => rowById.TryGetValue(id, out var r) ? r : null)
                          .Where(r => r != null).Select(r => r!).ToList();

            if (ids.Count == 0) noUnit++;
            if (ids.Count > 1) multiUnit++;

            // F3：队首 = Seq 最小且未采完的那个（Seq 为 0 排队尾）
            var head = rows.Where(r => r.Done < 1 - 1e-9)
                           .OrderBy(r => r.Seq == 0 ? int.MaxValue : r.Seq)
                           .ThenBy(r => r.UnitId, StringComparer.Ordinal)
                           .FirstOrDefault()
                    ?? rows.OrderBy(r => r.Seq == 0 ? int.MaxValue : r.Seq).FirstOrDefault();

            // F4：备采 = 本面脚下**全部**剩余量；台账读不到时退到工序区记的那个数
            double reserve = rows.Count > 0
                ? rows.Sum(r => Remain(r))
                : Math.Max(0, z.VolumeM3 ?? 0);

            string mat = MaterialOf(head, isDump, out bool guessed);
            if (guessed) fallbackMat++;

            var f = new FaceInput
            {
                Zone = z.Name,
                Process = isDump ? ProcessType.Dump : ProcessType.Load,
                UnitId = head?.UnitId?.Trim() ?? (ids.Count > 0 ? ids[0] : ""),
                BenchElevationM = AvgZ(ring),
                AvailableReserveM3 = reserve,
                MaterialCode = mat,
                Material = MaterialCatalog.Resolve(mat).Name,
                AdvanceAzimuthDeg = head?.AzimuthDeg,
                MiningWidthM = head != null && head.WidthM > 1e-6 ? head.WidthM : null,
            };

            // ★ 面积质心，不是顶点均值。走 HaulResolver 的公用件（同一块地在运距里和在这里
            //   必须是同一个点）。环是栅格描边 + DP 抽稀出来的 —— 弯的那一侧留的点更多，
            //   顶点均值会系统性地偏向弯的那一头，而偏出来的点看着还挺像个中心。
            var (cx, cy) = HaulResolver.AreaCentroid(
                ring.Select(p => (p.X, p.Y)).ToList(), out bool degenerate);
            f.SourceX = cx; f.SourceY = cy; f.SourceZ = f.BenchElevationM;
            // ★ 这个点不是人录的，也不是按 mineable_region 推的 —— 单列一档说清楚。
            //   记成「录入坐标」等于给一个几何推导出来的点冒充实测精度。
            f.SourceOrigin = HaulResolver.OriginDerived;
            if (degenerate) degenerateCentroid++;

            // ★ 主电铲从**作业面台账**认领（2026-08-19）。
            //
            //   工序作业区只给位置与量；配哪台铲是 `working_face.equipment_id` 那一列。
            //   下游 `ShiftPlanAssembler` 是**按面取设备**的 —— 面上没有主设备就一台都取不到，
            //   报出来是「一台可派设备都没有」，读起来像设备库空了（实测：库里 518 台、
            //   电铲 30 台可派且解出台效）。**同一个设备库，排产那条路配得到、这条路配不到。**
            //
            //   认领口径与归属 FA3/FA5 同一条：同物料 + 标高最近且在容差内，**并列就不认领**。
            //   认不到就是认不到 —— 不给兜底设备（兜底出来的班表在现场派不下去，
            //   而报表上量是对的、达成度是对的）。
            var crew = PickCrew(crewLedger, f.BenchElevationM, !isDump && IsCoalMat(mat));
            if (crew != null) { f.Group.MainEquipment = crew.MainEquipment; crewHit++; }
            else crewMiss.Add($"{z.Name}（标高 {f.BenchElevationM:0.#}m）");

            if (isDump)
            {
                // ★ 排土面必须绑上汇，否则它整月零入方 —— 而且是静默的。
                //   FlowAssigner.MatchesFace 的兜底是「汇名与面名**精确相等**」，
                //   而派生的面名来自工序区的分组键：兜底时是「排土场·台阶」（外排土场·L1），
                //   汇叫「外排土场」—— 对不上 ⇒ 零入方 ⇒ DerivedFromInbound 给出日目标 0
                //   ⇒ 那个面整月不动，甘特上连一条都排不出来，而没有任何东西报错。
                //   台账里有权威答案：排土单元那行的 Region 就是排土场名，按它绑。
                BindDumpSink(f, head, sinks, unboundDump);
                // F4：排土面**不给日目标** —— 由入方物料流推导（Σ入方占容方），保证采排守恒。
                //     手填一个"排弃占容 / 工日"会与采装侧算出来的入方对不上，而两边各自都合理。
                f.DerivedFromInbound = true;
                f.DayTargetM3 = 0;
            }
            else
            {
                f.DayTargetM3 = wd > 0 ? reserve / wd : 0;

                // ★ 煤面按**位置**取煤质：钻孔在图上、面也在图上，中间缺的只是一次按距离的取值。
                //   此前煤质只有一条来源 —— 人在「作业面台账」上手填。没人填 ⇒ 该面无煤质目标
                //   ⇒ **配煤约束整条不跑**，而计划照出、报表照有，「按综合灰分重分配采出量」
                //   那一步静默变成空操作。
                if (head != null && head.Kind == LedgerKind.Coal)
                {
                    var pick = (qualityPick ?? ((sc, px, py) => SeamQualitySampler.Pick(sc, px, py)))
                               (head.Seam ?? "", f.SourceX, f.SourceY);
                    if (pick.Ok)
                    {
                        f.Quality = pick.Quality;
                        qualityOk++;
                    }
                    else qualityMiss.Add($"{f.Zone}（{head.Seam}）：{pick.Why}");
                }
            }

            faces.Add(f);
        }

        if (faces.Count == 0)
        {
            LastSourceLabel = $"作业面·派生：{month} 的 {usable.Count} 块工序区一个面都没派生出来";
            return null;
        }

        int load = faces.Count(f => f.Process == ProcessType.Load);
        LastSourceLabel = $"作业面·派生自{month}工序作业区：{faces.Count} 个面"
                        + $"（采装 {load} · 排土 {faces.Count - load}，日目标按 {wd:0.#} 个作业日摊）";

        if (noUnit > 0)
            LastNotes.Add($"◆ {noUnit} 块工序区**没有单元号** —— 派生出来的面没有身份证："
                        + "备采核销、期次覆盖率、图上定位这三件事都做不了。"
                        + "多半是那批区域是手工圈的，不是从月计划生成的。");
        if (multiUnit > 0)
            LastNotes.Add($"· {multiUnit} 个面盖着多个采掘单元 —— `UnitId` 取的是**队首**"
                        + "（排产序最小且未采完的那个，= 此刻在哪个单元上作业）；"
                        + "备采量是这个面脚下**全部**单元的剩余量之和。两者不是一个数。");
        if (qualityOk > 0)
            LastNotes.Add($"· {qualityOk} 个煤面的**煤质按位置取自钻孔样**（同层、反距离加权、原煤口径）—— "
                        + "配煤约束因此有目标可用。逐面的取样依据见各面的说明。");
        if (qualityMiss.Count > 0)
            LastNotes.Add($"◆ {qualityMiss.Count} 个煤面**取不到煤质**："
                        + string.Join("；", qualityMiss.Take(3))
                        + (qualityMiss.Count > 3 ? " 等" : "")
                        + "　—— 这些面**没有煤质目标**，配煤约束在它们身上不起作用（不报错、也不生效）。"
                        + "要么在这一带补孔补样，要么在「作业面台账」上手填四项（缺一不可）。");
        // ── 主电铲认领的账，逐条报 ────────────────────────────────
        if (crewHit > 0)
            LastNotes.Add($"· {crewHit} 个面从**作业面台账**认领到主电铲"
                        + $"（按平盘标高 ±{CrewElevTolM:0.#}m + 同物料配的，并列就不认领）。");
        if (crewMiss.Count > 0)
            LastNotes.Add($"· {crewMiss.Count} 个面**在作业面台账里认不到主电铲**（"
                        + string.Join("、", crewMiss.Take(6))
                        + (crewMiss.Count > 6 ? " 等" : "") + "）—— "
                        + "工序作业区只给位置与量，配哪台铲本来是「作业面台账（working_face）」里的"
                        + "`equipment_id` 那一列。这些面转由**自动指派**兜住（见下一条）；"
                        + "要精细指派就在「作业面台账」按这些标高建档并填主电铲，填了就以台账为准。");

        // ── 主设备自动指派（AS 组）──
        //  台账里只有 5 条作业面档案，而派生出来的面有几十个 ——
        //  认不到主机的面在逐日逐班里**一条任务都排不出来**（下游按面取设备），
        //  报出来却是「一台可派设备都没有」，读起来像设备库空了。
        //  这里按「在用 + 类别对 + 一机一面 + 大机配大面」补上，并标成自动指派。
        var auto = FaceEquipmentAutoAssigner.Assign(faces);
        if (auto.Label.Length > 0) LastSourceLabel += "　·　" + auto.Label;
        foreach (var n in auto.Notes) LastNotes.Add(n);

        if (degenerateCentroid > 0)
            LastNotes.Add($"◆ {degenerateCentroid} 个面的区域环**面积退化**（顶点共线 / 自交 / 重复点）—— "
                        + "源端坐标退回了顶点均值。那个点不代表装车位置，路网求出来的运距会偏。");
        if (unboundDump.Count > 0)
            LastNotes.Add($"◆ {unboundDump.Count} 个排土面**没绑上去向**"
                        + $"（{string.Join("、", unboundDump.Take(6))}{(unboundDump.Count > 6 ? " 等" : "")}）—— "
                        + "这些面会**整月零入方**：日目标由入方推导，入方为 0 就是一条任务都排不出来，"
                        + "而甘特上只是少了几行，没有任何东西报错。"
                        + "补法：让「去向台账」里的排土场名与采掘单元台账的「采场/排土场」列对齐，"
                        + "或在「作业面台账」上给这些面直接指定去向。");
        if (fallbackMat > 0)
            LastNotes.Add($"◆ {fallbackMat} 个面的**物料判不出，已回落硬岩**（保守：按剥离、需爆破处理）。"
                        + "物料决定需不需要爆破、允许进哪些去向 —— 表土被回落成硬岩就会被排进内排场，"
                        + "而没有任何东西会报错。补法：在「作业面台账」上填物料码。");
        return faces;
    }

    // ── 小件 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 排土面 → 汇。<b>按台账的排土场名绑，绑不上就不绑</b>（拒绝猜）。
    ///
    /// <para>为什么不按面名绑：派生的面名是工序区的分组键，兜底时带着台阶
    /// （「外排土场·L1」），而汇叫「外排土场」。按面名去配，要么配不上，
    /// 要么得做模糊匹配 —— 后者会让「外排土场」配上「外排土场2」，那比配不上更糟。</para>
    ///
    /// <para>为什么不随便挑一个排土场顶上：挑错的那一个不会报错，
    /// 只是这个面的料一整月都排到了别的排土场，而两边的量各自都平。</para>
    /// </summary>
    private static void BindDumpSink(FaceInput f, MiningUnitLedger.Row? head,
                                     SinkRegistry? sinks, List<string> unbound)
    {
        string region = (head?.Region ?? "").Trim();
        if (sinks == null || region.Length == 0) { unbound.Add(f.Zone + (region.Length == 0 ? "（台账没有排土场名）" : "")); return; }

        var hit = sinks.All.FirstOrDefault(
                      s => s.IsDumping && string.Equals(s.Name?.Trim(), region, StringComparison.OrdinalIgnoreCase))
               ?? sinks.All.FirstOrDefault(
                      s => s.IsDumping && string.Equals(s.Id?.Trim(), region, StringComparison.OrdinalIgnoreCase));
        if (hit == null) { unbound.Add($"{f.Zone}（台账写的是「{region}」，去向台账里没有同名的排土场）"); return; }

        f.DestinationId = hit.Id;
        f.DestinationName = hit.Name;
        f.DestinationKind = hit.Kind;
    }


    /// <summary>F5：台账类型 + 层名 → 物料码。判不出置 <paramref name="guessed"/>。</summary>
    private static string MaterialOf(MiningUnitLedger.Row? r, bool isDump, out bool guessed)
    {
        guessed = false;
        if (r == null) { guessed = !isDump; return MaterialCatalog.Rock; }

        string seam = (r.Seam ?? "").Trim();
        if (seam.Contains("表土", StringComparison.Ordinal)) return MaterialCatalog.Topsoil;
        if (seam.Contains("风化", StringComparison.Ordinal)) return MaterialCatalog.Weathered;
        if (seam.Contains("夹矸", StringComparison.Ordinal)) return MaterialCatalog.Interburden;

        return r.Kind switch
        {
            LedgerKind.Coal => MaterialCatalog.Coal,
            LedgerKind.Rock => MaterialCatalog.Rock,
            _ => Guess(out guessed),                    // 排土行本身没有物料 —— 它装的是别处运来的料
        };

        static string Guess(out bool g) { g = false; return MaterialCatalog.Rock; }
    }

    private static double Remain(MiningUnitLedger.Row r)
    {
        double v = r.Kind switch
        {
            LedgerKind.Coal => r.CoalM3 ?? 0,
            LedgerKind.Rock => r.NetRockM3 ?? 0,
            _ => r.DumpCapM3 ?? 0,
        };
        return Math.Max(0, v * (1 - Math.Clamp(r.Done, 0, 1)));
    }

    private static List<string> SplitIds(string? s)
        => (s ?? "").Split(new[] { '、', ',', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private static double AvgZ(List<ZonePoint> ring)
        => ring.Count == 0 ? 0 : ring.Average(p => p.Z);

    private static double ResolveWorkdays(string month)
    {
        try
        {
            if (DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d))
            {
                var info = WorkCalendar.MonthWorkdays(d);
                if (info.Workdays > 0) return info.Workdays;
            }
        }
        catch { }
        return WorkCalendar.FallbackMonthWorkdays;
    }

    private static bool Eq(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
