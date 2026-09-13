// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ExploderConfig.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  TaskExploder 的输入契约（"盘子"）+ 校核输出。
//  引擎 = 月/日目标 ÷ 编组班产 → 按班次装箱（备采用尽则空闲、超能力回摊保守恒）+ 约束校核。
//  现由 SampleTaskBoard 提供样例 Config；真实接 ShortTerm 月计划 + GeoDataBase 台账时只换 Config。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个班次时段。</summary>
public sealed class ShiftWindow
{
    public string Name { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public ShiftWindow() { }
    public ShiftWindow(string name, double start, double end) { Name = name; Start = start; End = end; }
}

/// <summary>
/// 一个采装/排土作业面（区）的当日输入：源 + 物料 + 去向 + 目标量 + 编组 + 质量。
/// 「源—物料—汇」三者齐备才算得出循环时间 T_c → 最优配车数 n* → 编组班产，
/// 编组班产才是装箱的 bin 大小。缺去向 ⇒ 编组只能拍脑袋。
/// </summary>
public sealed class FaceInput
{
    // 从哪采（源）
    public string Zone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public string EngineeringPositionId { get; set; } = "";

    /// <summary>
    /// 采掘单元号（V041）= 采矿模型导出表里的 <c>MiningUnitLedger.Row.UnitId</c>（层-B带号-P幅号）。
    /// <para>
    /// <b>它是这个面在图上的身份证</b>。没有它，一条日任务只有 <see cref="Zone"/> 这个自由文本面名：
    /// 点一行定位不到图上任何一个体 → 备采量核销不了、期次覆盖率算不出、影像底图上没有边界可画。
    /// 空 = 该面还没绑到单元（合法状态，装箱照跑，只是上面那几件事做不了）。
    /// </para>
    /// </summary>
    public string UnitId { get; set; } = "";

    /// <summary>本面绑到了具体采掘单元。</summary>
    public bool HasUnit => !string.IsNullOrWhiteSpace(UnitId);

    /// <summary>
    /// 备采储量（m³ 原位实方，V041）。<b>0 = 未录，不是"采空"</b>。
    /// <para>
    /// 装箱里那句「备采用尽 → 该班空闲(OreShortage)」判的是<b>当日目标</b>用尽；
    /// 而"这个面还能采几天""采准断不断档"（三量）要的是这个量。两者不是一回事：
    /// 当日目标是今天要采多少，备采储量是脚下总共还有多少。
    /// </para>
    /// </summary>
    public double AvailableReserveM3 { get; set; }

    /// <summary>推进方位 °（正北起顺时针，V041）。null = 未录。作业区布置画箭头、动画走位用。</summary>
    public double? AdvanceAzimuthDeg { get; set; }

    /// <summary>本面当前推进宽 m（V041）。null = 未录。与推进方位配对：一个给方向一个给步长。</summary>
    public double? MiningWidthM { get; set; }

    /// <summary>
    /// 按当日目标算，备采储量还够采几天。备采未录（≤0）或当日目标为 0 时返回 null——
    /// <b>不拿 0 冒充"采空"</b>，那会让每个没录储量的面天天报断档，真断档的那个反而淹了。
    /// </summary>
    public double? PreparedDays => AvailableReserveM3 > 1e-6 && DayTargetM3 > 1e-6
        ? AvailableReserveM3 / DayTargetM3
        : null;

    /// <summary>
    /// 源代表点坐标（铲位/装载点）。路网求运距要求源汇两端都能定位到 RoadNode——
    /// 汇端有 SinkNode.X/Y/Z，源端缺了它就只能靠 Zone/工程位置名去匹配节点，
    /// 对不上就整条链落到兜底运距，配车数与编组班产跟着一起偏。
    /// X、Y 均为 0 视为未录（与 HaulResolver 的判据一致，标高 0 是合法值）。
    /// </summary>
    public double SourceX { get; set; }
    public double SourceY { get; set; }
    public double SourceZ { get; set; }

    /// <summary>源端是否有可用坐标（只看 XY，Z=0 合法）。</summary>
    public bool HasSourcePosition => Math.Abs(SourceX) > 1e-9 || Math.Abs(SourceY) > 1e-9;

    /// <summary>
    /// 源端坐标<b>是哪来的</b>。空 = 台账人工录入（历史行为，绝大多数面走这一档）。
    /// 取值见 <c>HaulResolver.OriginEntered / OriginRegion / OriginDerived</c>。
    ///
    /// <para><b>为什么要这一列</b>：坐标本身分不出「人在台账上录的铲位」与
    /// 「按区域几何推出来的质心」——前者是实测的装车点，后者只是一块地的中心，
    /// 两者的运距可以差出几百米。运距一变，循环时间、配车数、编组班产跟着一起变。
    /// 报表上写「录入 N」而其实没人录过，是在给一个数字冒充精度。</para>
    /// </summary>
    public string SourceOrigin { get; set; } = "";

    // 采什么（物料）
    public string Material { get; set; } = "";       // 显示文本（历史自由文本，兼容保留）
    public string MaterialCode { get; set; } = "";   // 结构化物料码（MaterialCatalog）
    public MaterialMix? Mix { get; set; }            // 物料构成（煤岩混采按份额拆）

    // 排到哪（汇）—— 混采时以下五项是【主去向】，完整分项见 Splits
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public SinkKind DestinationKind { get; set; } = SinkKind.ExternalDump;
    public double HaulDistanceKm { get; set; }       // 运距 km（三层兜底：路网 → 手填 → 汇的 FallbackHaulKm）
    public double EquivHaulKm { get; set; }          // 等效运距 km（含坡度折算）

    /// <summary>
    /// 按物料分项的去向。一台铲在同一时窗挖混采料，煤去破碎站、岩去排土场——
    /// 是一条任务多个去向，不能拆面（同一主设备会撞成"设备双占"）。空 = 单一去向。
    /// </summary>
    public List<MaterialDestination> Splits { get; set; } = new();
    public bool HasSplits => Splits.Count > 0;

    // 干多少
    public double DayTargetM3 { get; set; }          // 当日备采/目标量（m³ 实方）
    public CoalQuality? Quality { get; set; }

    // 谁来干
    public EquipmentGroup Group { get; set; } = new();
    public string ShovelModelPref { get; set; } = "";  // 主铲型号偏好（编组求解用）
    public ProcessType Process { get; set; } = ProcessType.Load;   // Load / Dump

    /// <summary>
    /// 排土面专用：日目标由入方物料流自动推导（Σ入方占容方），而非手工填。
    /// 置 true 后引擎会覆盖 DayTargetM3，保证采排守恒。
    /// </summary>
    public bool DerivedFromInbound { get; set; }

    /// <summary>
    /// 主设备是<b>自动指派</b>的（<see cref="FaceEquipmentAutoAssigner"/>），不是作业面台账里的档案。
    /// <para>
    /// 自动指派只保证"这个面有一台在用的、类别对的、独占的主机"，
    /// <b>没算</b>位置远近与车队匹配。派工单/甘特要能把它与人填的区分开 ——
    /// 混成一样的，现场就会以为这台机是调度定的。人在台账保存一次即固化，之后自动指派不再插手。
    /// </para>
    /// </summary>
    public bool MainEquipmentAuto { get; set; }

    /// <summary>物料构成：Mix 优先，其次 MaterialCode，最后解析历史文本。</summary>
    public MaterialMix ResolvedMix =>
        Mix ?? (MaterialCatalog.Exists(MaterialCode) ? MaterialMix.Single(MaterialCode) : MaterialMix.Parse(Material));

    public double TargetTonnageT => ResolvedMix.ToTonnage(DayTargetM3);
    /// <summary>本面当日产出的排弃占容方（仅非矿部分需排弃）。</summary>
    public double WasteDumpM3 => ResolvedMix.Split(DayTargetM3).Where(x => !x.Spec.IsOre).Sum(x => x.Spec.ToDumpM3(x.InSituM3));
    public double EffectiveHaulKm => EquivHaulKm > 1e-6 ? EquivHaulKm : HaulDistanceKm;
    public bool HasDestination => !string.IsNullOrWhiteSpace(DestinationId) || !string.IsNullOrWhiteSpace(DestinationName);

    /// <summary>该物料的去向：优先分项，无分项回落主去向。</summary>
    public MaterialDestination DestinationFor(string materialCode)
    {
        foreach (var s in Splits)
            if (string.Equals(s.MaterialCode, materialCode, System.StringComparison.OrdinalIgnoreCase))
                return s;
        return new MaterialDestination
        {
            MaterialCode = materialCode, Fraction = ResolvedMix.FractionOf(materialCode),
            DestinationId = DestinationId, DestinationName = DestinationName, DestinationKind = DestinationKind,
            HaulKm = HaulDistanceKm, EquivHaulKm = EquivHaulKm,
        };
    }

    /// <summary>本面各物料是否都已定去向（混采面须逐项都有）。</summary>
    public bool AllMaterialsRouted
        => ResolvedMix.Shares.Count > 0
        && ResolvedMix.Shares.All(s => s.Fraction <= 1e-6 || DestinationFor(s.MaterialCode).HasDestination);
}

/// <summary>穿孔输入（待爆区）。</summary>
public sealed class DrillInput
{
    public string EquipId { get; set; } = "";
    public string Zone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public double Start { get; set; }
    public double End { get; set; }

    // ── 作业量（来自 drill_plan 台账；未录为 null，不拿 0 冒充"打 0 米"）──
    //  穿孔没有 m³ 口径，它的量是孔数与延米。不带下来的话穿孔任务只有时窗、没有量，
    //  「工序进度跟踪」就只能按"完成/未完成"判，而那个状态没有任何入口能置位。
    public int? HoleCount { get; set; }
    public double? HoleLengthM { get; set; }
}

/// <summary>
/// 一段爆破停产时窗（爆破时刻 → 清场解除）。
///
/// <para>
/// <b>为什么必须是列表而不是一对标量</b>：一天两三炮是常态，而
/// <see cref="ExploderConfig.BlastStart"/>/<see cref="ExploderConfig.BlastEnd"/> 只装得下一炮
/// （装配层取 <c>hours.Min()</c>）。后面那些炮在装箱里<b>根本不存在</b>——
/// 界面上排得整整齐齐，计划里那几个时段仍在满负荷作业。2026-08-11 补齐。
/// </para>
/// </summary>
public sealed class BlastWindow
{
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>说明（"第 3 炮 · EP-08"），仅供显示。</summary>
    public string Label { get; set; } = "";

    public BlastWindow() { }
    public BlastWindow(double start, double end, string label = "")
    { Start = start; End = end; Label = label; }

    public double Hours => Math.Max(0, End - Start);
    public bool Valid => End > Start + 1e-9;

    /// <summary>重叠的窗口并成一段，按起点排。两炮挨着放会切出一段 0.001h 的碎片，那种段没有意义。</summary>
    public static List<BlastWindow> Merge(IEnumerable<BlastWindow>? windows)
    {
        var src = (windows ?? Enumerable.Empty<BlastWindow>())
            .Where(w => w != null && w.Valid)
            .OrderBy(w => w.Start)
            .ToList();

        var res = new List<BlastWindow>();
        foreach (var w in src)
        {
            var last = res.Count > 0 ? res[^1] : null;
            if (last != null && w.Start <= last.End + 1e-9)
            {
                if (w.End > last.End) last.End = w.End;
                if (w.Label.Length > 0) last.Label = last.Label.Length > 0 ? last.Label + " / " + w.Label : w.Label;
                continue;
            }
            res.Add(new BlastWindow(w.Start, w.End, w.Label));
        }
        return res;
    }

    /// <summary>
    /// 从 [from,to) 里挖掉全部停产时窗，返回剩下的可作业段（按起点排；长度 ≤1e-9 的碎片丢弃）。
    /// </summary>
    public static List<(double Start, double End)> Subtract(double from, double to, IEnumerable<BlastWindow>? windows)
    {
        var res = new List<(double Start, double End)>();
        if (to <= from + 1e-9) return res;

        double cur = from;
        foreach (var w in Merge(windows))
        {
            if (w.End <= cur + 1e-9) continue;      // 整段在左边
            if (w.Start >= to - 1e-9) break;        // 整段在右边（已按起点排，后面的更右）
            if (w.Start > cur + 1e-9) res.Add((cur, Math.Min(w.Start, to)));
            cur = Math.Max(cur, w.End);
            if (cur >= to - 1e-9) return res;
        }
        if (to > cur + 1e-9) res.Add((cur, to));
        return res;
    }
}

/// <summary>检修/计划停机窗口。</summary>
public sealed class MaintenanceWindow
{
    public string EquipId { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Label { get; set; } = "检修";
}

public sealed class ExploderConfig
{
    public string DateLabel { get; set; } = "";
    public string IdPrefix { get; set; } = "D";
    public double NowHour { get; set; }
    public double FromHour { get; set; }               // 仅排此刻之后（滚动重排用；0=全天）

    /// <summary>
    /// 最早一炮的停产窗口（<b>兼容视图</b>）。真源是 <see cref="Blasts"/>——
    /// 一律用 <see cref="BlastWindows"/> 取全部时窗，别只读这一对：一天两三炮时，
    /// 只看这一对就等于把后面的炮当作不存在。
    /// <see cref="Blasts"/> 为空时（样例种子盘子 / 只给了一炮的老路径）它就是唯一那一段。
    /// </summary>
    public double BlastStart { get; set; }
    public double BlastEnd { get; set; }

    /// <summary>
    /// 本日全部爆破停产时窗。空 = 走 <see cref="BlastStart"/>/<see cref="BlastEnd"/> 的兼容视图。
    /// 写入请走 <see cref="SetBlasts"/>，它顺手把兼容视图同步成最早那一炮（两处不许各说各的）。
    /// </summary>
    public List<BlastWindow> Blasts { get; set; } = new();

    /// <summary>
    /// 交接班损失（非首班每班扣的坡道时长，h）。<b>人工锚点</b>，在「编制配置」里改，见 <see cref="CompileOverrides"/>。
    /// <para>
    /// 它没有台账真源——是这个矿的作业惯例（点名/交接/进场），故按工程缺省 0.5h 起步，由人锚定。
    /// 落在<b>时窗</b>上而不是能力上（与天气降效正好相反）：交接是"这段时间不能开工"，
    /// 每班少半小时，三班一天就少 1.5 小时的能力 —— 它是"当日排不排得下"最容易被忽略的那一项。
    /// 唯一的实现在 <see cref="WorkWindowCalc"/>，装箱与逐日能力日历共用。
    /// </para>
    /// </summary>
    public double HandoverRampH { get; set; } = 0.5;

    /// <summary>
    /// 天气/路况降效 %（0 = 不降效）。人工锚点，在「编制配置」里改，见 <see cref="CompileOverrides"/>。
    /// <para>
    /// 落在**能力**上而不是时窗上：雨雪影响的是路面车速与能见度（每小时干得少），
    /// 而检修/爆破清场/交接班影响的是能开工的**时段**。两者混在一处，
    /// 「今天雨大」就会被记成「今天少上了两小时班」，甘特上的条形位置全错。
    /// 降效后当日干不完，装箱那一步会照常报「当日欠产」——这正是设计文档要的"全盘降效回摊"。
    /// </para>
    /// </summary>
    public double WeatherDeratePct { get; set; }

    /// <summary>降效后的能力系数（1 = 不降效）。上限截到 95%：全停产是不排班，不是降效 100%。</summary>
    public double WeatherFactor => 1 - Math.Clamp(WeatherDeratePct, 0, 95) / 100.0;

    // ── 按环节降效（采装 / 运输 / 排土）────────────────────────────────────────
    //
    //  全盘一个降效乘子答不了现场最常见的那句话：「今天路面湿滑，运输慢，采装照常」。
    //  雨雪打的是路面车速与卸点排队（T_c 变长），大风停的是电铲与爆破（τ_L 变长），
    //  低温打的是排土场推土作业 —— 三件事量级不同，揉成一个数就只能一起降。
    //
    //  三项都是**可空的分环节覆盖**：null = 跟随 WeatherDeratePct（全盘值）。
    //  于是「只设全盘」时三者相等，WeatherFactorFor 会走早退分支，结果与老口径**逐位相同**——
    //  这条兼容性由判据钉死，不是靠肉眼看公式。

    /// <summary>采装环节降效 %（电铲装车）。null = 跟随 <see cref="WeatherDeratePct"/>。</summary>
    public double? LoadDeratePct { get; set; }

    /// <summary>运输环节降效 %（重车/空车行驶、卸点排队）。null = 跟随 <see cref="WeatherDeratePct"/>。</summary>
    public double? HaulDeratePct { get; set; }

    /// <summary>排土环节降效 %（推土机平整）。null = 跟随 <see cref="WeatherDeratePct"/>。</summary>
    public double? DumpDeratePct { get; set; }

    /// <summary>三个环节是否给了各不相同的值（给了才谈得上"分环节"）。</summary>
    public bool HasLinkDerate =>
        Math.Abs(DerateOf(LoadDeratePct) - DerateOf(HaulDeratePct)) > 1e-9
     || Math.Abs(DerateOf(LoadDeratePct) - DerateOf(DumpDeratePct)) > 1e-9;

    private double DerateOf(double? link) => Math.Clamp(link ?? WeatherDeratePct, 0, 95);

    /// <summary>
    /// <b>某个作业面</b>的能力系数（1 = 不降效）。按环节降效的唯一实现，装箱/配煤/作业组织/能力预算共用。
    ///
    /// <para><b>排土面</b>：推土机平整不含运输循环，直接吃排土环节的降效。</para>
    ///
    /// <para><b>采装面</b>：班产是 <c>min(铲装能力 A, 车队运力 B)</c> 压出来的一个标量，
    /// 单看它无从分环节。用编组带下来的周期分解还原两侧再分别降：
    /// <code>
    ///   A ∝ 1/τ_L          → A' = A·(1−d采装)
    ///   B ∝ n/T_c          → T_c' = τ_L/(1−d采装) + (T_c−τ_L)/(1−d运输)，B' = B·T_c/T_c'
    ///   系数 = min(A',B') / min(A,B)
    /// </code>
    /// τ_L 以外的循环时间（重车、卸载、空车、排队）整段算作"路上"，吃运输降效。
    /// 于是<b>运输降效对采装瓶颈的面自然不生效</b>（MF&gt;1 时 B 侧还有富余），
    /// 对运力瓶颈的面全额生效 —— 这是物理结论，不是按运距拍的加权。</para>
    ///
    /// <para><b>没有周期分解时不许凭空劈</b>：编组未经 FleetMatcher 求解（τ_L/T_c 为 0，
    /// 兜底盘子/历史快照会这样）就退回全盘值，并由调用方报一条「降效未分环节」。
    /// 编个份额出来分，比不分更坏 —— 那是给一个不存在的精度。</para>
    /// </summary>
    public double WeatherFactorFor(FaceInput? face)
    {
        if (face == null) return WeatherFactor;

        if (face.Process == ProcessType.Dump) return 1 - DerateOf(DumpDeratePct) / 100.0;

        double dLoad = DerateOf(LoadDeratePct), dHaul = DerateOf(HaulDeratePct);
        double fLoad = 1 - dLoad / 100.0;
        if (Math.Abs(dLoad - dHaul) < 1e-9) return fLoad;      // 两环节同降 ⇒ 与全盘老口径逐位相同

        var g = face.Group;
        if (!g.HasCycleBreakdown) return WeatherFactor;        // 分不了就不分，别编

        double fHaul = 1 - dHaul / 100.0;
        double takt = g.LoadTaktMin, tc = g.CycleTimeMin, mf = g.MatchFactor;

        // 归一化到 A=1：则 B=MF，班产 = min(1, MF)
        double baseCap = Math.Min(1.0, mf);
        double tcD = takt / fLoad + (tc - takt) / fHaul;
        double capD = Math.Min(1.0 * fLoad, mf * (tc / tcD));
        return baseCap > 1e-9 ? Math.Clamp(capD / baseCap, 0.05, 1.0) : fLoad;
    }

    /// <summary>
    /// 备采保有下限（天）。<b>0 = 不校核</b>。人工锚点，在「编制配置」里改。
    /// <para>
    /// 采准三量保有原则落到日计划上的形式：某面按当日强度采下去，剩余备采不足这个天数就该预警了——
    /// 等到真采空那天再报，采准（穿孔/爆破）根本来不及跟上，那个面就得停。
    /// 只对**录了备采储量**的面生效（<see cref="FaceInput.AvailableReserveM3"/> &gt; 0）。
    /// </para>
    /// </summary>
    public double MinPreparedDays { get; set; }

    /// <summary>
    /// 面日产能的有效工时（h/日）。<b>人工锚点</b>，在「编制配置」里改，见 <see cref="CompileOverrides"/>。
    /// <para>
    /// <b>面日产能上限 = 编组班产 × 本值 × 天气系数</b>，它是<b>作业组织重分配与配煤重分配共用的同一个闸</b>
    /// （<see cref="TaskExploder"/> 两处必须取同一个值，否则"策略灌得进、配煤移不动"这种自相矛盾无从解释）。
    /// ≈ 三班合计扣掉检修/爆破清场/交接班之后还剩的工时，工程缺省 20h。
    /// </para>
    /// <para>
    /// <b>刻意不挂在 <see cref="BlendStandard"/> 上</b>：它先前是 <c>BlendStandard.EffHoursPerDay</c>，
    /// 于是"只想调有效工时"必须先造一份配煤标准 —— 而 <see cref="Blend"/> 为 null 的语义是
    /// 「不管配煤，纯量矿」，一造就把配煤约束整个打开了。两件事的开关不许绑在一起。
    /// </para>
    /// </summary>
    public double EffHoursPerDay { get; set; } = 20;

    /// <summary>
    /// 作业组织策略（人工锚点，在「编制配置」里选）。缺省 <see cref="WorkOrganization.Balanced"/> = 不动月计划给的分配。
    /// </summary>
    public WorkOrganization Organization { get; set; } = WorkOrganization.Balanced;

    public List<ShiftWindow> Shifts { get; set; } = new();
    public List<FaceInput> Faces { get; set; } = new();
    public List<DrillInput> Drills { get; set; } = new();
    public List<MaintenanceWindow> Maintenance { get; set; } = new();
    public BlendStandard? Blend { get; set; }          // 综合配煤标准（null=不管配煤，纯量矿）

    /// <summary>去向登记簿（排土场/破碎站/煤仓/堆场）。未接台账时为 SinkRegistry.Sample()。</summary>
    public SinkRegistry Sinks { get; set; } = new();

    /// <summary>是否校核采排守恒与排土场库容/卸点通过能力。</summary>
    public bool EnforceMassBalance { get; set; } = true;

    /// <summary>
    /// 本日全部停产时窗（合并重叠、按起点排）。<b>装箱、重排、甘特爆破带、钻爆衔接一律走这里。</b>
    /// <see cref="Blasts"/> 为空时回落到 <see cref="BlastStart"/>/<see cref="BlastEnd"/> 那一对。
    /// </summary>
    public IReadOnlyList<BlastWindow> BlastWindows()
        => Blasts.Count > 0
            ? BlastWindow.Merge(Blasts)
            : BlastEnd > BlastStart + 1e-9
                ? new List<BlastWindow> { new(BlastStart, BlastEnd) }
                : new List<BlastWindow>();

    /// <summary>
    /// 写入本日停产时窗，并把兼容视图同步成<b>最早</b>那一段。
    /// 一个入口写两处，是为了不出现"列表说三炮、标量说一炮"的分叉。
    /// </summary>
    public void SetBlasts(IEnumerable<BlastWindow>? windows)
    {
        Blasts = BlastWindow.Merge(windows);
        if (Blasts.Count > 0) { BlastStart = Blasts[0].Start; BlastEnd = Blasts[0].End; }
        else { BlastStart = 0; BlastEnd = 0; }
    }

    /// <summary>期标签（物料流聚合用；默认取 DateLabel）。</summary>
    public string Period => string.IsNullOrWhiteSpace(DateLabel) ? IdPrefix : DateLabel;

    /// <summary>本盘子的物料流六元组（按面拆物料）。</summary>
    public IEnumerable<MaterialFlow> Flows()
    {
        foreach (var f in Faces.Where(f => f.Process == ProcessType.Load))
            foreach (var (spec, m3) in f.ResolvedMix.Split(f.DayTargetM3))
            {
                if (m3 <= 1e-6) continue;
                var d = f.DestinationFor(spec.Code);   // 混采面按分项取各自去向
                yield return new MaterialFlow
                {
                    Period = Period,
                    SourceId = string.IsNullOrWhiteSpace(f.EngineeringPositionId) ? f.Zone : f.EngineeringPositionId,
                    SourceName = f.Zone, SourceBenchElevationM = f.BenchElevationM,
                    MaterialCode = spec.Code, InSituM3 = m3,
                    SinkId = d.DestinationId, SinkName = d.DestinationName, SinkKind = d.DestinationKind,
                    HaulKm = d.HaulKm, EquivHaulKm = d.EquivHaulKm,
                };
            }
    }

    public PeriodBalance Balance() => new() { Period = Period, Flows = Flows().ToList() };
}

/// <summary>校核码常量——各引擎/UI 共用，避免字符串散落。</summary>
public static class ViolationCodes
{
    public const string TruckShortage = "运力不足";
    public const string DayShortfall = "当日欠产";
    public const string EquipDoubleBooked = "设备双占";
    public const string ProcessChain = "工序接续";
    /// <summary>一个作业面都没有 —— 本盘不排任何任务（口径见 ProductionPlanContext.BuildAndApply）。</summary>
    public const string NoWorkFace = "没有作业面";
    public const string BlendOk = "配煤达标";
    public const string BlendAdjusted = "配煤调整";
    public const string BlendFailed = "配煤不达标";
    // ── 采—运—排 ──
    public const string NoDestination = "去向未定";
    public const string MaterialRejected = "物料不兼容";
    public const string DumpCapacity = "排土库容不足";
    public const string SinkThroughput = "卸点能力不足";
    public const string MassImbalance = "采排不守恒";
    public const string SinkClosed = "去向不可用";
    public const string HaulMissing = "运距缺失";
    public const string HaulAbnormal = "运距反常";
    public const string FleetMismatch = "编组失配";
    public const string FlowSplit = "去向拆分";        // 混采面的物料被分到多个汇，面上只记主去向
    public const string TopsoilRoute = "表土去向";      // 表土须单独堆存供复垦
    public const string StripAllocation = "剥离分摊";   // 日剥离量在各排土面间的分摊依据
    public const string DispatchIssued = "任务下达";    // 下达/签发/回执
    public const string SimulationNote = "推演提示";    // 三维/时序推演的口径说明
    public const string WeatherDerate = "天气降效";     // 全盘按 WeatherFactor 降能力
    public const string BlastClearance = "爆破清场";    // 爆破停产时窗把班切成多段
    public const string WorkdayBasis = "作业日口径";    // 日历口径 vs 月计划口径不一致
    public const string PreparedReserve = "备采家底";   // 采准三量：备采储量 / 保有天数
    public const string EquipUnavailable = "设备不可用"; // 主设备检修/报废/不在台账
    public const string UnitLink = "采掘单元";          // 作业面 ↔ 采掘单元台账的对号
    public const string WorkOrg = "作业组织";           // 集中强采 / 多面展开 的面间重分配
}

/// <summary>
/// 作业组织策略（BR-P4）—— 同样的日总量，摊在几个面上。
///
/// <para>
/// <b>三种策略只重分配、不改总量</b>：日采出总量是月计划定的，策略动不了它；
/// 能动的是"这些量摊在哪几个面上"。这与配煤重分配是同一类操作，
/// 故排在配煤**之前**：策略是偏好，配煤是约束，约束必须能推翻偏好。
/// </para>
/// <list type="bullet">
/// <item><b>均衡型</b>：不动。月计划（或单元剩余量）给的分配就是结果。</item>
/// <item><b>集中强采型</b>：按面日产能从大到小灌满，能少开面就少开——
///   设备集中、辅助工程少，代价是备采消耗快、单点故障影响大。</item>
/// <item><b>多面展开型</b>：按面日产能等比例摊，让每个面都在干——
///   抗单点故障、采准压力分散，代价是设备分散、辅助工程多。</item>
/// </list>
/// <para>
/// 两种非均衡策略都受**面日产能**与**备采储量**双重上限：
/// 灌到一个面上的量不能超过它一天干得完的（班产×有效工时），也不能超过它脚下有的
/// （备采储量，未录时不设这一限——未录不等于 0）。
/// </para>
/// </summary>
public enum WorkOrganization
{
    /// <summary>均衡型：不动上游给的分配。</summary>
    Balanced,
    /// <summary>集中强采型：按产能从大到小灌满，少开面。</summary>
    Concentrated,
    /// <summary>多面展开型：按产能等比例摊，多开面。</summary>
    MultiFace,
}

/// <summary>综合配煤入仓标准（各面采出按量加权后的混煤指标须达标）。</summary>
public sealed class BlendStandard
{
    public double MaxAshPct { get; set; } = 12.8;       // 综合灰分上限
    public double MinCalorificMJkg { get; set; } = 21.5; // 综合热值下限
    public double MaxSulfurPct { get; set; } = 0.7;      // 综合硫上限
}

public enum ViolationSeverity { Info, Warn, Error }

/// <summary>计划校核条目（约束/冲突）。</summary>
public sealed class PlanViolation
{
    public ViolationSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ExploderResult
{
    public List<ProductionTask> Tasks { get; set; } = new();
    public List<PlanViolation> Violations { get; set; } = new();
}
