// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/MinePlanExport.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>逐月一行（下游月计划表直接吃）。单位与 PlanLib 对齐：煤 <b>万t</b>、岩 <b>万m³实方</b>。</summary>
public sealed class ExportMonth
{
    public int    Month { get; set; }
    public double CoalWanT { get; set; }
    public double StripWanM3 { get; set; }
    /// <summary>生产剥采比 m³/t（岩实方 ÷ 煤吨）。</summary>
    public double Ratio { get; set; }
    public double CumCoalWanT { get; set; }
    public double CumStripWanM3 { get; set; }
    /// <summary>本月推进量（m）—— 各标高格推进量的加权平均，喂 Q=L·v·H·ρ 那套换算。</summary>
    public double AdvanceM { get; set; }
    /// <summary>备采煤量（万t，已露未采）与保有月数。</summary>
    public double PreparedWanT { get; set; }
    public double PreparedMonths { get; set; }
    /// <summary>超前剥离储备（万m³实方）= 累计剥离 − 累计必须剥。</summary>
    public double LeadStockWanM3 { get; set; }
    /// <summary>内排率（%，按实方）。没跑配对时为 −1（<b>不是 0</b>：0 是"全外排"这个真实结论）。</summary>
    /// <summary>内排率 %。<b>−1 = 本月没跑采排配对</b>（不是 0 —— 0 是"全外排"这个真实结论）。</summary>
    public double InternalRatePct { get; set; } = -1;

    /// <summary>
    /// 内排率的<b>显示文本</b>：没跑配对给「—」，不给 <c>−1.0</c>。
    /// <para>界面直接绑数值的话，用户会在百分比列里看到 <b>−1.0%</b> —— 那是把哨兵值当数据显示。
    /// 契约层"缺什么不拿 0 冒充"的纪律，到了显示层同样成立。</para>
    /// </summary>
    public string InternalRateText => InternalRatePct < 0 ? "—" : InternalRatePct.ToString("0.0");

    /// <summary>运输功的显示文本 —— 没跑配对时同样给「—」，不给 0.0（0 是真值不是"没算"）。</summary>
    public string TransportWorkText => InternalRatePct < 0 ? "—" : TransportWorkWanTKm.ToString("0.0");
    public double TransportWorkWanTKm { get; set; }
    /// <summary>排不下的实方（万m³）。&gt;0 就是这个月的计划落不了地。</summary>
    public double UnplacedWanM3 { get; set; }
    /// <summary>露煤紧迫月（拉绳触底）/ 能力吃紧月（触顶）。</summary>
    public bool   IsExposureCritical { get; set; }
    public bool   IsCapacityCritical { get; set; }

    /// <summary>
    /// 紧迫标记的<b>显示文本</b>：这个月是被什么顶住的。
    ///
    /// <para><b>为什么要显示</b>：触底/触顶标的是<b>本月的约束在哪一侧咬住了</b> ——
    /// 触底 = 再少剥就露不出下个月的煤；触顶 = 剥离能力已经用满。
    /// 它回答的是"这张表为什么长这样、想改善该松哪一条"，
    /// 而不是又一个数。内核自己的文字报表一直印着 <c>◄触底/◄触顶</c>，
    /// 到了界面的月度表却掉了 —— 同一个信号，两处报法不该两样。</para>
    ///
    /// <para>两侧可能<b>同时</b>咬住（同一期里既有触底转折又有触顶转折）——
    /// 那说明这个月被夹死了，最该显示，所以不做二选一。</para>
    /// </summary>
    public string CriticalText
        => IsExposureCritical && IsCapacityCritical ? "触底+触顶(夹死)"
         : IsExposureCritical ? "触底(露煤紧迫)"
         : IsCapacityCritical ? "触顶(能力吃紧)"
         : "—";
}

/// <summary>物料流六元组 —— 与 PlanLib 的 <c>PlanFlow</c> 同构，适配层一一赋值即可。</summary>
public sealed class ExportFlow
{
    public int    Month { get; set; }
    /// <summary>源：层间标签名（覆岩 / 层间n / 层m夹矸 / 底板下）。</summary>
    public string SourceName { get; set; } = "";
    public int    SourceGap { get; set; }
    /// <summary>物料码（<c>topsoil/weathered/rock/interburden</c>…）。空 = 下游按名字匹配。</summary>
    public string MaterialCode { get; set; } = "";
    public string MaterialName { get; set; } = "";
    /// <summary><b>权威量</b>：原位实方（万m³）。下游一切换算从它出发。</summary>
    public double InSituWanM3 { get; set; }
    /// <summary>汇：去向名 + 台阶级（0 = 最先承接的最下一级）。</summary>
    public string DestinationName { get; set; } = "";
    public int    DestinationLevel { get; set; }
    public bool   IsInternalDump { get; set; }
    public double HaulKm { get; set; }
    /// <summary>本引擎<b>实际用的</b> ρ / Kr —— 下游拿它和自己的物料目录对账。</summary>
    public double DensityUsed { get; set; }
    public double KrUsed { get; set; }
    /// <summary>按上面那两个系数算出来的派生量，供对账（下游应当自己按目录重算并比对）。</summary>
    public double TonnageWanT { get; set; }
    public double DumpWanM3 { get; set; }
}

/// <summary>
/// 逐月逐层<b>采出煤</b>。
/// <para><b>只给源侧（层 + 量），不给汇</b>：煤去哪个破碎站/煤仓由下游的去向台账说了算，
/// 本引擎不知道也不该猜。下游拿这几行去建自己的煤流（`PlanFlow` 的 `IsOre` 那一半）。</para>
/// <para>没有它，下游按流派生的「采出量」会是 <b>0</b> —— 只导岩流是半份账。</para>
/// </summary>
public sealed class ExportCoal
{
    public int    Month { get; set; }
    public int    SeamIndex { get; set; }
    public string SeamName { get; set; } = "";
    /// <summary>原位实方（万m³）—— 权威量。</summary>
    public double InSituWanM3 { get; set; }
    /// <summary>本引擎用的容重 t/m³。下游拿它和自己的物料目录对账。</summary>
    public double DensityUsed { get; set; }
    /// <summary>吨量（万t）= 实方 × 容重。</summary>
    public double TonnageWanT { get; set; }
    /// <summary>
    /// <b>这一行的分层量是引擎摊的还是用户给的</b>（规则 R36）。
    /// true 时下游必须在界面上标出来 —— 摊过的数看上去和用户填的一模一样。
    /// </summary>
    public bool AllocatedByEngine { get; set; }

    /// <summary>
    /// 分层量来源的<b>显示文本</b>。R36 那句"下游必须在界面上标出来"要落到一列上，
    /// 光有个 bool 没人看得见。
    ///
    /// <para><b>不要用勾选框</b>：勾/不勾都是个记号，读的人得先知道"勾上=引擎摊的"才看得懂；
    /// 而这一列存在的理由恰恰是 <b>摊过的数看上去和用户填的一模一样</b> ——
    /// 那就得把话写全，不能再要求读者事先知道约定。</para>
    /// </summary>
    public string AllocationText => AllocatedByEngine ? "引擎按规则摊的" : "用户给的";
}

/// <summary>逐月逐台阶位置 —— 喂三维动态模拟建层体。</summary>
public sealed class ExportBench
{
    public int    Month { get; set; }
    /// <summary><c>pit</c> = 采场（自上而下压覆）；<c>dump</c> = 排土（自下而上承接）。</summary>
    public string Kind { get; set; } = "pit";
    public int    Level { get; set; }
    /// <summary>该级代表标高（m）。采场 = 标高格中位；排土 = 该级位置质心的平均 Z。</summary>
    public double ElevZ { get; set; }
    /// <summary>月末位置：采场 = 推进轴 u（m）；排土 = 该级<b>累计已占容量占比</b>（0~1）。</summary>
    public double Position { get; set; }
    /// <summary>本月推进量（m，采场）。</summary>
    public double AdvanceM { get; set; }
    /// <summary>本月该级的量：采场 = 万m³<b>实方</b>；排土 = 万m³<b>占容</b>（两侧口径不同，别相加）。</summary>
    public double VolumeWanM3 { get; set; }
    /// <summary>去向名（排土侧）。采场侧为空。</summary>
    public string DumpName { get; set; } = "";
    /// <summary>该级质心（排土侧，世界坐标）—— 层体定位用。</summary>
    public double Cx { get; set; }
    public double Cy { get; set; }
}

/// <summary>
/// 建层体要用的几何参数。<b>不给的话下游只能退回垂直壁</b>
/// （`采运排一体化` §十 已知边界 1：坡面角与平盘宽只能界面手填）。
/// </summary>
public sealed class ExportGeometry
{
    /// <summary>岩台阶高（m）。</summary>
    public double RockBenchHeightM { get; set; }
    /// <summary>岩/煤台阶坡面角（°）。</summary>
    public double RockFaceDeg { get; set; }
    public double CoalFaceDeg { get; set; }
    /// <summary>最小工作平盘宽（m）。</summary>
    public double MinBermM { get; set; }
    /// <summary>工作帮坡角 α（°）。</summary>
    public double WorkingSlopeDeg { get; set; }
    /// <summary>排土台阶高 / 坡面角（m / °）。0 = 没有台账来源，下游要自己兜底并标出来。</summary>
    public double DumpBenchHeightM { get; set; }
    public double DumpFaceDeg { get; set; }
    /// <summary>各项的来源说明（设计参数 / 台账 / 兜底默认值）。</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// 浅拷一份（全是 double/string，浅拷即够）。
    /// <para><b>为什么要提供它</b>：调用方要"在原有几何上补两项"时，很自然会写成
    /// <c>new ExportGeometry { A = g.A, B = g.B, … }</c> —— 那是一份**手抄清单**，
    /// 本类将来加字段就会被悄悄丢掉（本仓在手写 clone 上栽过四次，见判据 G18j）。
    /// 有了这个，补字段的写法是"克隆 + 改那两项"，加字段自动跟着走。</para>
    /// </summary>
    public ExportGeometry Clone() => (ExportGeometry)MemberwiseClone();
}

/// <summary>
/// 「逐月采剥排计划」的<b>导出契约</b>。
///
/// <para><b>为什么要有这一层</b>：下游有两种消费方式 ——
/// <list type="bullet">
///   <item><b>PlanLib</b> 直接引用 MineAssLib（`PlanLib.csproj` 里有），
///     所以它<b>不需要反射</b>，拿这个 POCO 直接填自己的 <c>MonthPeriod</c> / <c>PlanFlow</c>。</item>
///   <item><b>TaskLib</b> 不引用 MineAssLib，走它已有的 <c>ShortTermLink</c> 读 PlanLib 的月计划 ——
///     只要 PlanLib 填好了，TaskLib 那条桥<b>一行不用改</b>。</item>
///   <item><b>持久化 / 排障 / 三维模拟</b> 走 <see cref="ToJson"/>：带
///     <see cref="SchemaVersion"/>，格式变了下游<b>解析当场失败</b>，而不是静默读到半份数据。</item>
/// </list></para>
///
/// <para><b>每个数都带来源</b>（<see cref="Provenance"/> / <see cref="Notes"/>）：剖面指纹、期初工作帮姿态、
/// 外循环收敛情况、尾部外推。填错姿态时每项硬校核都还是"✓"，只有这些标注看得出来。</para>
///
/// <para><b>单位</b>：煤 <b>万t</b>、岩 <b>万m³ 原位实方</b>、运距 <b>km</b>、运输功 <b>万t·km</b> ——
/// 与 PlanLib 现有口径一致，适配层不做单位换算。</para>
/// </summary>
public sealed class MinePlanExport
{
    /// <summary>契约版本。字段增删必须升它；下游遇到不认识的版本要拒收而不是猜。</summary>
    public const int CurrentSchema = 1;
    public int SchemaVersion { get; set; } = CurrentSchema;

    // ── 来源与可信度 ────────────────────────────────────────────────────────
    /// <summary>剖面指纹（<see cref="ProfileProvenance.Text"/>）—— 这份计划是从哪份几何算出来的。</summary>
    public string Provenance { get; set; } = "";
    /// <summary>期初工作帮姿态（显式给 / 稳态推 / 裸起始帮）。</summary>
    public string InitialPosture { get; set; } = "";
    /// <summary>基建剥离（万m³实方）—— <b>不在</b>逐月计划里，单列。</summary>
    public double BoxCutWanM3 { get; set; }
    /// <summary>外循环收敛说明。空 = 没跑外循环（内排/运距按静态值）。</summary>
    public string ConvergenceNote { get; set; } = "";
    /// <summary>需要下游知道的其它口径说明（尾部外推、弱哈希、兜底运距…）。</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary><b>逐层煤量是不是引擎摊的</b>（规则 R36）。true 时下游必须在界面上标出来。</summary>
    public bool CoalAllocatedByEngine { get; set; }
    /// <summary>摊法说明。</summary>
    public string CoalAllocationMethod { get; set; } = "";

    // ── 校核 ────────────────────────────────────────────────────────────────
    /// <summary>硬约束逐条（`✓/✗ H1 名称：实测`）。</summary>
    public List<string> Checks { get; set; } = new();
    /// <summary>硬约束是否全过。<b>false 时下游不得当成可执行计划</b>。</summary>
    public bool Feasible { get; set; }
    /// <summary>是否有排不下的方量。</summary>
    public bool AllPlaced { get; set; } = true;

    // ── 正文 ────────────────────────────────────────────────────────────────
    public List<ExportMonth> Months { get; set; } = new();
    /// <summary>采出侧（源+量，汇由下游台账定）。</summary>
    public List<ExportCoal>  Coal   { get; set; } = new();
    /// <summary>剥离侧（完整六元组，汇已定）。</summary>
    public List<ExportFlow>  Flows  { get; set; } = new();
    public List<ExportBench> Benches { get; set; } = new();
    /// <summary>煤层名（与 <see cref="ExportCoal.SeamIndex"/> 同序）。</summary>
    public List<string> SeamNames { get; set; } = new();
    /// <summary>建层体要用的几何参数。缺了下游只能退回垂直壁。</summary>
    public ExportGeometry Geometry { get; set; } = new();

    // ── 汇总 ────────────────────────────────────────────────────────────────
    public double TotalCoalWanT { get; set; }
    public double TotalStripWanM3 { get; set; }
    public double OverallRatio { get; set; }
    public double RatioCv { get; set; }
    public double TotalTransportWorkWanTKm { get; set; }
    public double OverallInternalRatePct { get; set; } = -1;

    // ── 组装 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从内核结果组装导出契约。<paramref name="dump"/> 为 null 时只出采剥不出排（流向/内排率留空、标注说明）。
    /// </summary>
    /// <param name="geom">
    /// 建层体的几何参数（坡面角/平盘宽/排土台阶高）。不给则只填能从剖面推出来的那几项，
    /// 并在 <see cref="Notes"/> 里如实说明下游会退回垂直壁。
    /// </param>
    public static MinePlanExport Build(MonthlyScheduleResult sched, RockProfile? rock,
                                       DumpAllocationResult? dump = null,
                                       CoupledPlanResult? coupled = null,
                                       ExportGeometry? geom = null)
    {
        var e = new MinePlanExport();
        if (sched == null || !sched.Success)
        {
            e.Feasible = false;
            e.Notes.Add("采剥接续未解出：" + (sched?.Error ?? "无结果"));
            return e;
        }

        e.Provenance = rock?.Provenance?.Text() ?? "（无剖面指纹 —— 这份计划的几何来源不可追溯）";
        e.InitialPosture = sched.InitialPosture;
        e.BoxCutWanM3 = sched.BoxCutM3 / 1e4;
        e.ConvergenceNote = coupled?.ConvergenceNote ?? "";
        if (coupled is { Converged: false })
            e.Notes.Add("⚠ 外循环未收敛 —— " + coupled.ConvergenceNote);
        if (sched.TailExtrapolated)
            e.Notes.Add("尾部 N 个月的露煤前瞻用【末月强度外推】的煤计划；接上中长远下一年目标后可去掉这条外推。");
        e.CoalAllocatedByEngine = sched.CoalAllocatedByEngine;
        e.CoalAllocationMethod = sched.CoalAllocationMethod;
        if (sched.CoalAllocatedByEngine)
            e.Notes.Add("★ 逐层煤量是【引擎摊的】，不是用户填的（R36）：" + sched.CoalAllocationMethod);
        if (dump == null)
            e.Notes.Add("未跑采排配对 —— 逐月内排率/运输功/流向为空（不是 0）。");

        // ★ 把**内核自己留的条**原样带下去。
        //   上面那几条是按 `sched` 的**标志位**重新编的（TailExtrapolated / CoalAllocatedByEngine…），
        //   而 `sched.Notes` 这条通道以前**根本没人读** —— 内核往里写什么，下游一个字都收不到。
        //   实测因此漏掉两条要紧的：「工作帮推到剖面数据尽头」（此后剥离量是假的）与
        //   「未给 n经，H5 没参与判定」。二者都只到会话日志为止，契约里查不到。
        //   与 G18j 那批手写 clone 同一个病：**靠人记得同步的通道，迟早漏**。
        foreach (var n in sched.Notes)
            if (!e.Notes.Contains(n)) e.Notes.Add(n);

        foreach (var c in sched.Checks) e.Checks.Add(c.ToString());
        // ★ H5 挂在 `if (RatioCeiling > 0)` 里，没给上限就整条不加 ——
        //   而 `Feasible = AllChecksOk` 是对**跑过的那几条**求与。于是契约上写着
        //   「Feasible: true」，读的人不会知道**经济剥采比这一条压根没参与判定**。
        //   本仓一贯的纪律是「缺什么不拿 0/沉默冒充」（内排率没算给 −1、显示「—」）——
        //   这里同理：在 Checks 里留一行明写"未参与判定"。用「—」起头，
        //   与 ✓/✗ 区分开，也**不动 Feasible**（它没通过、也没失败，是没跑）。
        if (!sched.Checks.Any(c => c.Code == "H5"))
            e.Checks.Add($"— H5 生产剥采比 ≤ n经：**未给 n经，本条未参与判定**"
                       + $"（本期实测最大 {(sched.Months.Count > 0 ? sched.Months.Max(m => m.Ratio) : 0):0.00} m³/t，可拿它对照）");
        e.Feasible = sched.AllChecksOk && (dump == null || dump.AllPlaced);
        e.AllPlaced = dump?.AllPlaced ?? true;

        if (rock != null) e.SeamNames.AddRange(rock.SeamNames);

        // 几何参数：给了就用给的；没给就只填剖面推得出来的那两项，并如实说明后果。
        e.Geometry = geom ?? new ExportGeometry
        {
            RockBenchHeightM = rock?.BenchHeight ?? 0,
            Source = "仅剖面可推项（台阶高）—— 坡面角/平盘宽/排土台阶高未给",
        };
        if (e.Geometry.RockBenchHeightM <= 0 && rock != null) e.Geometry.RockBenchHeightM = rock.BenchHeight;
        if (e.Geometry.RockFaceDeg <= 0 || e.Geometry.MinBermM <= 0)
            e.Notes.Add("⚠ 坡面角/平盘宽未给 —— 下游建三维层体只能退回垂直壁（`采运排一体化` §十 边界 1）。");
        if (e.Geometry.DumpBenchHeightM <= 0)
            // ⚠ 这句原来写的是「`dump_site` 表无此列」—— **过期了**：该表带着
            //   `bench_height_m` / `bench_slope_angle_deg` / `overall_slope_angle_deg`，且有真数据。
            //   内核不认识数据库（不能依赖 GeoDataBase），所以由会话层
            //   `MonthlyStripSession.FillDumpBenchFromLedger` 查了填进来；填不上才走到这里。
            e.Notes.Add("⚠ 排土台阶高/坡面角这次没拿到 —— 排土侧层体需下游兜底并标出来。"
                      + "台账 `dump_site` 里有 `bench_height_m`/`bench_slope_angle_deg` 两列，"
                      + "把本次用到的排土场那几行填上即可自动带出来。");

        // 逐月
        double h = rock?.BenchHeight ?? 0;
        for (int i = 0; i < sched.Months.Count; i++)
        {
            var m = sched.Months[i];

            // 采出侧逐层分解：按【逐层前界】做差（用户给了逐层目标就是各层各的，否则各层同一前界）。
            // 没有它，下游按流派生的「采出量」会是 0 —— 只导岩流是半份账。
            if (rock != null && sched.SeamFrontU.Length > m.Month && sched.SeamFrontU[m.Month] != null)
            {
                var f1 = sched.SeamFrontU[m.Month];
                var f0 = sched.SeamFrontU[m.Month - 1];
                for (int j = 0; j < rock.CoalVolBins.Length && j < f1.Length; j++)
                {
                    double wt = rock.CoalWtUpTo(j, f1[j]) - rock.CoalWtUpTo(j, j < f0.Length ? f0[j] : f1[j]);
                    if (wt <= 1e-9) continue;
                    double dens = j < rock.SeamDensity.Length && rock.SeamDensity[j] > 0 ? rock.SeamDensity[j] : 1.35;
                    e.Coal.Add(new ExportCoal
                    {
                        Month = m.Month, SeamIndex = j,
                        SeamName = j < rock.SeamNames.Length ? rock.SeamNames[j] : $"层{j}",
                        InSituWanM3 = wt / dens, DensityUsed = dens, TonnageWanT = wt,
                        AllocatedByEngine = sched.CoalAllocatedByEngine,      // R36
                    });
                }
            }
            var dm = dump?.Months.FirstOrDefault(x => x.Month == m.Month);
            // 上月位置：第 1 个月取【期初那一排】，不是拿自己减自己（那样第 1 个月恒为 0）。
            double[] prevX = PrevBenchX(sched, i);
            double adv = 0;
            if (m.BenchX.Length > 0 && m.BenchX.Length == prevX.Length)
                adv = m.BenchX.Select((v, k) => v - prevX[k]).Average();
            e.Months.Add(new ExportMonth
            {
                Month = m.Month,
                CoalWanT = m.CoalWt, StripWanM3 = m.RockM3 / 1e4, Ratio = m.Ratio,
                CumCoalWanT = m.CoalCumWt, CumStripWanM3 = m.RockCumM3 / 1e4,
                AdvanceM = adv,
                PreparedWanT = m.PreparedWt, PreparedMonths = m.PreparedMonths,
                LeadStockWanM3 = m.LeadStockM3 / 1e4,
                InternalRatePct = dm?.InternalRatePct ?? -1,
                TransportWorkWanTKm = (dm?.TransportWorkTKm ?? 0) / 1e4,
                UnplacedWanM3 = (dm?.UnplacedM3 ?? 0) / 1e4,
                IsExposureCritical = m.OnLowerBound, IsCapacityCritical = m.OnUpperBound,
            });

            // 采场台阶位置
            for (int k = 0; k < m.BenchX.Length && k < sched.Levels.Length; k++)
            {
                double prev = k < prevX.Length ? prevX[k] : m.BenchX[k];
                e.Benches.Add(new ExportBench
                {
                    Month = m.Month, Kind = "pit", Level = sched.Levels[k],
                    ElevZ = (sched.Levels[k] + 0.5) * h,
                    Position = m.BenchX[k], AdvanceM = m.BenchX[k] - prev,
                    VolumeWanM3 = rock != null
                        ? (rock.VolUpTo(sched.Levels[k], -1, m.BenchX[k]) - rock.VolUpTo(sched.Levels[k], -1, prev)) / 1e4
                        : 0,
                });
            }
        }

        // 物料流 + 排土台阶
        if (dump != null)
        {
            foreach (var dm in dump.Months)
                foreach (var f in dm.Flows)
                    e.Flows.Add(new ExportFlow
                    {
                        Month = f.Month, SourceGap = f.Gap,
                        SourceName = rock != null && f.Gap < rock.GapNames.Length ? rock.GapNames[f.Gap] : $"标签{f.Gap}",
                        MaterialCode = f.MaterialCode, MaterialName = f.MaterialName,
                        InSituWanM3 = f.InSituM3 / 1e4,
                        DestinationName = f.DumpName, DestinationLevel = f.Level,
                        IsInternalDump = f.IsInternal, HaulKm = f.HaulKm,
                        DensityUsed = f.Density, KrUsed = f.Kr,
                        TonnageWanT = f.TonnageT / 1e4, DumpWanM3 = f.DumpM3 / 1e4,
                    });
            // 排土侧：逐月逐去向逐级 —— 标高与质心从位置质心加权求，Position = 该级累计已占容占比。
            // 层体按它长：知道级、标高、质心、已填到几成，就能画出这个月长成什么样。
            var cumByLevel = new Dictionary<(string, int), double>();
            var capByLevel = new Dictionary<(string, int), double>();
            foreach (var s in dump.Months.SelectMany(x => x.Flows))
            {
                var key = (s.DumpName, s.Level);
                capByLevel.TryGetValue(key, out double c);
                capByLevel[key] = c + s.DumpM3;                       // 全期该级总占容 = 分母
            }
            foreach (var dm in dump.Months)
                foreach (var g in dm.Flows.GroupBy(f => (f.DumpName, f.Level)))
                {
                    double vol = g.Sum(f => f.DumpM3);
                    cumByLevel.TryGetValue(g.Key, out double cum);
                    cumByLevel[g.Key] = cum + vol;
                    double denom = capByLevel.TryGetValue(g.Key, out double cap) && cap > 1e-9 ? cap : 1;
                    double w = g.Sum(f => f.DumpM3);
                    e.Benches.Add(new ExportBench
                    {
                        Month = dm.Month, Kind = "dump", Level = g.Key.Level, DumpName = g.Key.DumpName,
                        ElevZ = w > 1e-9 ? g.Sum(f => f.Dz * f.DumpM3) / w : 0,
                        Cx = w > 1e-9 ? g.Sum(f => f.Dx * f.DumpM3) / w : 0,
                        Cy = w > 1e-9 ? g.Sum(f => f.Dy * f.DumpM3) / w : 0,
                        Position = cumByLevel[g.Key] / denom,          // 该级已填到几成（0~1）
                        AdvanceM = 0,
                        VolumeWanM3 = vol / 1e4,                       // 排土侧记【占容】，与采场的实方不同口径
                    });
                }
            e.TotalTransportWorkWanTKm = dump.TotalTransportWorkTKm / 1e4;
            e.OverallInternalRatePct = dump.OverallInternalRatePct;
        }

        e.TotalCoalWanT = sched.TotalCoalWt;
        e.TotalStripWanM3 = sched.TotalRockM3 / 1e4;
        e.OverallRatio = sched.OverallRatio;
        e.RatioCv = sched.RatioCv;
        return e;
    }

    // ── 序列化 ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不转义，人能读
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opt);

    /// <summary>
    /// 解析。版本不认识就<b>拒收</b>并说明，不猜、不读半份。
    /// </summary>
    public static MinePlanExport? FromJson(string json, out string err)
    {
        err = "";
        try
        {
            var e = JsonSerializer.Deserialize<MinePlanExport>(json, Opt);
            if (e == null) { err = "解析出空对象"; return null; }
            if (e.SchemaVersion != CurrentSchema)
            { err = $"契约版本不匹配：文件 v{e.SchemaVersion}，本程序读 v{CurrentSchema}"; return null; }
            return e;
        }
        catch (Exception ex) { err = "JSON 解析失败：" + ex.Message; return null; }
    }

    /// <summary>
    /// 自洽校核（导出前后都可跑）：流向合计要对得上逐月剥离量、汇总要对得上逐月合计。
    /// <b>对不上就是有一侧在自说自话</b> —— A5 的教训：两个数谁也解释不了谁。
    /// </summary>
    public List<string> Validate(double tolPct = 0.5)
    {
        var bad = new List<string>();
        if (SchemaVersion != CurrentSchema) bad.Add($"契约版本 {SchemaVersion} ≠ {CurrentSchema}");

        // ±∞ / NaN 不是工程量，而且 `System.Text.Json` 遇到它**直接抛异常** ⇒
        // 契约一旦带上，`ToJson()` 这条唯一的持久化路径当场断掉，而前面每一项校核都还是"✓"。
        // 真出过：排土位置投不到推进轴上时 `SlotU = −∞`，|u−(−∞)| 让运输功变成 ∞（G24g）。
        foreach (var (name, v) in NonFiniteFields())
            bad.Add($"{name} = {v} —— ±∞/NaN 不是工程量（JSON 也写不出去）");

        double sumCoal = Months.Sum(m => m.CoalWanT), sumStrip = Months.Sum(m => m.StripWanM3);
        if (Rel(sumCoal, TotalCoalWanT) > tolPct) bad.Add($"逐月煤合计 {sumCoal:0.00} ≠ 汇总 {TotalCoalWanT:0.00} 万t");
        if (Rel(sumStrip, TotalStripWanM3) > tolPct) bad.Add($"逐月岩合计 {sumStrip:0.00} ≠ 汇总 {TotalStripWanM3:0.00} 万m³");

        if (Flows.Count > 0)
            foreach (var m in Months)
            {
                double flow = Flows.Where(f => f.Month == m.Month).Sum(f => f.InSituWanM3);
                double want = m.StripWanM3 - m.UnplacedWanM3;
                if (Rel(flow, want) > tolPct)
                    bad.Add($"第{m.Month}月流向合计 {flow:0.00} ≠ 剥离−排不下 {want:0.00} 万m³");
            }

        // 采出侧：逐层吨量之和必须等于该月采出量。下游的「采出量」是按煤流派生的，
        // 这一条不成立，它那边就会得到一个和本表对不上的产量。
        if (Coal.Count > 0)
            foreach (var m in Months)
            {
                double sum = Coal.Where(c => c.Month == m.Month).Sum(c => c.TonnageWanT);
                if (Rel(sum, m.CoalWanT) > tolPct)
                    bad.Add($"第{m.Month}月逐层煤合计 {sum:0.00} ≠ 月采出 {m.CoalWanT:0.00} 万t");
            }

        foreach (var f in Flows.Where(f => f.KrUsed > 0))
        {
            if (Rel(f.InSituWanM3 * f.KrUsed, f.DumpWanM3) > tolPct)
            { bad.Add($"第{f.Month}月 {f.MaterialName} 占容量与 Kr 对不上"); break; }
        }

        // ── 完备性：**该有的是不是都在** ──
        //
        // 前四类都只看"在场的那些行"对不对，看不出**少了一行**：
        // 月份 1,2,4,5 缺了第 3 月 —— 求和照样自洽（汇总跟着少）、序号照样严格递增、
        // 范围引用也都没问题。而下游按 `Months` 逐帧推演，那一期就这么没了。
        if (Months.Count > 0)
        {
            int lo = Months[0].Month, hi = Months[^1].Month;
            if (hi - lo + 1 != Months.Count)
            {
                var have = Months.Select(m => m.Month).ToHashSet();
                var miss = Enumerable.Range(lo, hi - lo + 1).Where(x => !have.Contains(x)).Take(5).ToList();
                bad.Add($"月份不连续：{lo}..{hi} 应有 {hi - lo + 1} 个月，实际 {Months.Count} 个"
                      + (miss.Count > 0 ? $"，缺第 {string.Join("/", miss)} 月" : ""));
            }

            // 孤儿行：明细表里的月份在逐月表里不存在 ⇒ 按 Months 迭代的下游会**静默丢掉**它们
            var months = Months.Select(m => m.Month).ToHashSet();
            foreach (var (what, orphan) in new (string, int)[]
            {
                ("物料流",   Flows.Where(f => !months.Contains(f.Month)).Select(f => f.Month).FirstOrDefault(-1)),
                ("逐层煤",   Coal.Where(c => !months.Contains(c.Month)).Select(c => c.Month).FirstOrDefault(-1)),
                ("台阶层体", Benches.Where(b => !months.Contains(b.Month)).Select(b => b.Month).FirstOrDefault(-1)),
            })
                if (orphan >= 0)
                    bad.Add($"{what}里有第{orphan}月的行，但逐月表里没有这个月 —— 按逐月表迭代的下游会丢掉它");

            // 采场台阶：各月的标高格集合要一致（某月少一级 ⇒ 那一级那个月的量凭空消失）
            var pitByMonth = Benches.Where(b => b.Kind == "pit")
                                    .GroupBy(b => b.Month)
                                    .ToDictionary(g => g.Key, g => g.Select(b => b.Level).ToHashSet());
            if (pitByMonth.Count > 1)
            {
                var union = pitByMonth.Values.SelectMany(x => x).ToHashSet();
                foreach (var kv in pitByMonth.OrderBy(k => k.Key))
                    if (kv.Value.Count != union.Count)
                    {
                        var miss = union.Except(kv.Value).OrderBy(x => x).Take(5);
                        bad.Add($"第{kv.Key}月的采场台阶少了标高格 {string.Join("/", miss)}（其它月份有）");
                        break;
                    }
            }
        }

        // ── 范围与引用完整性：**数在不在合法域里、指的东西存不存在** ──
        //
        // 为什么现在才要紧：`FromContract` 允许把**外面给的 JSON** 装回来（换机器复现、
        // 别人给的契约、手改过的）。自产的契约由构造保证一致，装回来的**什么都可能**。
        // 求和与单调都只看数之间的关系，看不出"这个去向根本不存在"。
        {
            if (Months.Any(m => m.Month < 1))
                bad.Add("有月序号 < 1（月份从 1 起算）");
            // 内排率是「内排实方 ÷ 总实方」这种和之比，**全内排时会浮点溢出一点点**
            //   （实测真排土条带位置跑出来 100.000000000000014%）。
            //   不留容差的话：一个**完全合法**的结果（全部内排）会被判成不自洽，
            //   而且报出来的还是 `100.0% 越界` —— 数字本身看着就在域内，没人看得懂。
            //   ⇒ 上下界各留 1e-6，并且**报到能看出问题的位数**。
            const double PCT_EPS = 1e-6;
            foreach (var m in Months.Where(m => m.InternalRatePct is not (-1)
                                             && (m.InternalRatePct < -PCT_EPS || m.InternalRatePct > 100 + PCT_EPS)))
            { bad.Add($"第{m.Month}月内排率 {m.InternalRatePct:0.############}% 越界（合法域 [0,100]，−1 = 没跑配对）"); break; }
            if (OverallInternalRatePct is not (-1)
             && (OverallInternalRatePct < -PCT_EPS || OverallInternalRatePct > 100 + PCT_EPS))
                bad.Add($"总内排率 {OverallInternalRatePct:0.############}% 越界（合法域 [0,100]）");

            foreach (var f in Flows)
            {
                // Kr < 1 意味着"排土占的空间比挖出来的坑还小" —— 岩石破碎后只会膨胀，物理上不可能
                if (f.KrUsed is > 0 and < 1)
                { bad.Add($"第{f.Month}月 {f.MaterialName} 的 Kr={f.KrUsed:0.###} < 1 —— 岩石破碎后只会膨胀"); break; }
                if (f.DensityUsed < 0)
                { bad.Add($"第{f.Month}月 {f.MaterialName} 容重为负"); break; }
                if (f.InSituWanM3 < -1e-9)
                { bad.Add($"第{f.Month}月 {f.MaterialName} 实方为负"); break; }
            }

            // 引用完整性：流指向的去向，排土层体里得真有
            var dumpNames = Benches.Where(b => b.Kind == "dump")
                                   .Select(b => b.DumpName).Where(n => n.Length > 0)
                                   .ToHashSet(StringComparer.Ordinal);
            if (dumpNames.Count > 0)
                foreach (var f in Flows.Where(f => f.DestinationName.Length > 0 && !dumpNames.Contains(f.DestinationName)))
                { bad.Add($"第{f.Month}月的流指向去向「{f.DestinationName}」，但排土层体里没有这个去向"); break; }

            // 煤层下标越界（逐层煤表是按 SeamNames 对齐的）
            if (SeamNames.Count > 0)
                foreach (var c in Coal.Where(c => c.SeamIndex < 0 || c.SeamIndex >= SeamNames.Count))
                { bad.Add($"第{c.Month}月逐层煤的层下标 {c.SeamIndex} 越界（共 {SeamNames.Count} 层）"); break; }
        }

        // ── 单调性与前缀和：**累计量与位置是有方向的**，方向错了下游会静默算歪 ──
        //
        // 为什么必须校：`SimRegions` 是按【累计推进】把区域环整体外移的
        // （`RingOffset.Offset(ring, cumAdvance)`）。累计量一回退、或台阶位置一后退，
        // 环就往回缩 —— 图上看着像"采空区又长回去了"，而每一项**求和**校核都还是"✓"。
        for (int i = 0; i < Months.Count; i++)
        {
            var m = Months[i];
            if (i > 0)
            {
                var p = Months[i - 1];
                if (m.Month <= p.Month)
                    bad.Add($"月序号没递增：第{p.Month}月之后是第{m.Month}月");
                if (m.CumCoalWanT < p.CumCoalWanT - 1e-6)
                    bad.Add($"第{m.Month}月累计采出回退 {p.CumCoalWanT:0.00} → {m.CumCoalWanT:0.00} 万t");
                if (m.CumStripWanM3 < p.CumStripWanM3 - 1e-6)
                    bad.Add($"第{m.Month}月累计剥离回退 {p.CumStripWanM3:0.00} → {m.CumStripWanM3:0.00} 万m³");
            }
            // 累计 = 逐月前缀和（两个数各记各的就会漂，漂了报表对不上而谁也不知道以哪个为准）
            double preCoal = Months.Take(i + 1).Sum(x => x.CoalWanT);
            double preRock = Months.Take(i + 1).Sum(x => x.StripWanM3);
            if (Rel(preCoal, m.CumCoalWanT) > tolPct)
                bad.Add($"第{m.Month}月累计采出 {m.CumCoalWanT:0.00} ≠ 逐月前缀和 {preCoal:0.00} 万t");
            if (Rel(preRock, m.CumStripWanM3) > tolPct)
                bad.Add($"第{m.Month}月累计剥离 {m.CumStripWanM3:0.00} ≠ 逐月前缀和 {preRock:0.00} 万m³");
        }

        // 台阶位置逐月不后退（采场 = 推进位置 C1；排土 = 已填占比，排土只增不减）
        foreach (var g in Benches.GroupBy(b => (b.Kind, b.Level, b.DumpName)))
        {
            var seq = g.OrderBy(b => b.Month).ToList();
            for (int i = 1; i < seq.Count; i++)
                if (seq[i].Position < seq[i - 1].Position - 1e-6)
                {
                    bad.Add($"{(g.Key.Kind == "dump" ? g.Key.DumpName : "采场")} 格{g.Key.Level} 位置回退："
                          + $"第{seq[i - 1].Month}月 {seq[i - 1].Position:0.###} → 第{seq[i].Month}月 {seq[i].Position:0.###}");
                    break;      // 同一条链报一次就够，别把同一个问题刷成几十条
                }
        }

        // 逐台阶体积之和 = 该月剥离量。**第 1 个月最容易破** —— 拿 Months[0] 自己减自己当
        // "上月位置"时，逐台阶全是 0 而月总量照样对，契约在最多人看的那个月自相矛盾。
        // 这条同时兜住"期初那一排没报出来"（`MonthlyScheduleResult.InitialBenchX`）。
        if (Benches.Count > 0)
            foreach (var m in Months)
            {
                double sum = Benches.Where(b => b.Month == m.Month && b.Kind == "pit").Sum(b => b.VolumeWanM3);
                if (Rel(sum, m.StripWanM3) > tolPct)
                    bad.Add($"第{m.Month}月逐台阶体积合计 {sum:0.00} ≠ 月剥离 {m.StripWanM3:0.00} 万m³"
                          + (m.Month == Months[0].Month ? "（第1月：期初台阶位置没报出来？）" : ""));
            }
        return bad;
    }

    /// <summary>
    /// 第 <paramref name="i"/> 个月的<b>上月台阶位置</b>。第 1 个月取
    /// <see cref="MonthlyScheduleResult.InitialBenchX"/>（期初那一排），**不是拿本月减本月**。
    /// <para>拿本月减本月的后果：第 1 个月逐台阶推进与逐台阶体积恒为 0，而月总剥离量却是对的 ——
    /// 三维层体第 1 帧空着、契约自相矛盾，而且每一项既有校核都还是"✓"。真出过。</para>
    /// <para>期初那一排长度对不上（老结果 / 标高格数变了）时退回本月位置，
    /// 让 <see cref="Validate"/> 的逐台阶合计那条把它报出来 —— 不静默。</para>
    /// </summary>
    private static double[] PrevBenchX(MonthlyScheduleResult sched, int i)
    {
        var cur = sched.Months[i].BenchX;
        var prev = i > 0 ? sched.Months[i - 1].BenchX : sched.InitialBenchX;
        return prev.Length == cur.Length ? prev : cur;
    }

    /// <summary>
    /// 扫出契约里所有非有限的数（<c>±∞ / NaN</c>），最多报 8 条。
    /// <para>手写而不用反射：这几张表是固定的，反射一遍的成本和被漏掉的风险都不值得。
    /// 加了新字段要记得加进来 —— 判据 G24g 会在合成算例上兜一层。</para>
    /// </summary>
    private IEnumerable<(string Name, double V)> NonFiniteFields()
    {
        static bool Bad(double d) => double.IsNaN(d) || double.IsInfinity(d);
        int n = 0;
        foreach (var t in Scan())
        {
            if (!Bad(t.V)) continue;
            yield return t;
            if (++n >= 8) yield break;
        }

        IEnumerable<(string Name, double V)> Scan()
        {
            yield return (nameof(TotalCoalWanT), TotalCoalWanT);
            yield return (nameof(TotalStripWanM3), TotalStripWanM3);
            yield return (nameof(OverallRatio), OverallRatio);
            yield return (nameof(OverallInternalRatePct), OverallInternalRatePct);
            yield return (nameof(TotalTransportWorkWanTKm), TotalTransportWorkWanTKm);
            foreach (var m in Months)
            {
                yield return ($"第{m.Month}月.采出", m.CoalWanT);
                yield return ($"第{m.Month}月.剥离", m.StripWanM3);
                yield return ($"第{m.Month}月.剥采比", m.Ratio);
                yield return ($"第{m.Month}月.推进", m.AdvanceM);
                yield return ($"第{m.Month}月.备采", m.PreparedWanT);
                yield return ($"第{m.Month}月.保有月数", m.PreparedMonths);
                yield return ($"第{m.Month}月.超前储备", m.LeadStockWanM3);
                yield return ($"第{m.Month}月.内排率", m.InternalRatePct);
                yield return ($"第{m.Month}月.运输功", m.TransportWorkWanTKm);
                yield return ($"第{m.Month}月.排不下", m.UnplacedWanM3);
            }
            foreach (var f in Flows)
            {
                yield return ($"第{f.Month}月流.实方", f.InSituWanM3);
                yield return ($"第{f.Month}月流.运距", f.HaulKm);
                yield return ($"第{f.Month}月流.吨量", f.TonnageWanT);
                yield return ($"第{f.Month}月流.占容", f.DumpWanM3);
            }
            foreach (var b in Benches)
            {
                yield return ($"第{b.Month}月台阶{b.Level}.位置", b.Position);
                yield return ($"第{b.Month}月台阶{b.Level}.推进", b.AdvanceM);
                yield return ($"第{b.Month}月台阶{b.Level}.体积", b.VolumeWanM3);
            }
        }
    }

    private static double Rel(double a, double b)
        => Math.Abs(a) < 1e-9 && Math.Abs(b) < 1e-9 ? 0
         : Math.Abs(a - b) / Math.Max(1e-9, Math.Max(Math.Abs(a), Math.Abs(b))) * 100;
}
