using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 煤层柱状采样器（原 GeoDataBase.Domain.VirtualDrill.SeamColumnSampler 在本模型里用到的两个口）：
/// 竖直向下穿各煤层顶/底板，算 [zLow, zHigh] 区间内的煤厚（可按层名分账）；取指定层底板高程。
/// 排土场路径不用它（sampler = null 只归级配对，不判煤岩）。
/// </summary>
public interface ISeamColumnSampler
{
    double CoalThicknessIn(double x, double y, double zLow, double zHigh, IDictionary<string, double>? perSeam = null);
    bool TryFloorZ(string seamName, double x, double y, out double z);
}

/// <summary>
/// 「采矿模型·按标准水平」纯算法：把一堆台阶线归成标准水平（平盘标高级）→ 相邻级夹成「幅」
/// （一幅 = 一个台阶坡面 = 上级坡顶线 + 下级坡底线）→ 逐幅判煤台阶 / 岩台阶。
///
/// 和「按区域」构建的差别在配对口径：按区域是「Δz 窗 + 最近质心」自由配，配不上的线成为黑账
/// （"建出 N 体 / 跳过 M 条"对不上台阶级数）；这里先归级、再【只在相邻两级之间】配对，
/// 幅数与级数强相关（每对相邻级至少一幅），漏了哪一级一眼能看出来。
///
/// 煤/岩判定口径（要点，改之前先想清楚）：
///   · 一幅的高度区间 = [下级标高, 上级标高]，即该台阶从坡底到坡顶吃掉的这一段柱高；
///   · 沿【坡顶线】等弧长取 N 个采样点，每点向库里的各煤层顶/底板面竖直求交
///     （<see cref="ISeamColumnSampler"/> ← virtual_drill_surface 表），
///     算各见煤层与该幅区间的【重叠厚度】之和 = 该点煤厚；煤层跨出台阶的部分不计；
///   · 煤厚占比 = 沿线平均煤厚 ÷ 台阶高。分母用【全部采样点】而非"采到煤的点"——
///     真尖灭处煤厚就是 0，本就该把均值拉下来；只有【整幅一个点都没落在地质模型范围内】时
///     才不作数（记 Warning，判为岩台阶，不冒充煤）。
///   · 占比 ≥ CoalRatioThreshold(默认 0.5) = 煤台阶；> MixedRatioFloor(默认 0.05) = 混合台阶；否则岩台阶。
///
/// 一级台阶可能同时压着多层煤（4-1 / 4-2 叠在一个 12m 台阶里），故按煤层名分别累计厚度，
/// 不并成一个数 —— 后续按煤种配矿要用。
///
/// 纯托管、无副作用：取线 / 建体 / 展示由 <c>MiningModelDialog</c> 负责。
/// </summary>
public static class StandardLevelModel
{
    /// <summary>台阶性质。</summary>
    public enum BenchKind
    {
        /// <summary>岩台阶：区间内基本无煤。</summary>
        Rock = 0,
        /// <summary>煤台阶：区间内煤厚占比达阈值。</summary>
        Coal = 1,
        /// <summary>混合台阶：见煤但不足阈值（煤岩同采，需单列）。</summary>
        Mixed = 2,
    }

    /// <summary>该幅涉及的一层煤及其沿线平均厚度。</summary>
    public sealed class SeamShare
    {
        public string Name = "";
        /// <summary>该煤层落在本幅区间内的沿线平均厚度（m）。</summary>
        public double MeanThickM;
    }

    /// <summary>一「幅」= 相邻两级夹出的一个台阶（上级坡顶线 + 下级坡底线）。</summary>
    public sealed class BenchPair
    {
        /// <summary>序号，1 = 最上一幅。</summary>
        public int Index;

        /// <summary>坡顶级 / 坡底级代表标高（m）。</summary>
        public double CrestZ, ToeZ;

        /// <summary>台阶高（m）= 坡顶标高 − 坡底标高。</summary>
        public double BenchHeightM => CrestZ - ToeZ;

        /// <summary>配对上的两条线。</summary>
        public ulong CrestHandle, ToeHandle;

        /// <summary>坡顶线平面长度（m），作幅的规模参考。</summary>
        public double CrestLengthM;

        /// <summary>
        /// 配对时实测的坡面水平投影（m）= 坡顶线到坡底线的中位水平间距。
        /// 与台阶高一起可反推坡面角 atan(H / FaceRun)，用来核对这一对是不是真的同一坡面。
        /// </summary>
        public double FaceRunM;

        /// <summary>
        /// 实测煤层倾角（度，沿推进方向即往高墙里为正，抬升为正 —— 与内核
        /// <c>CarveStripInput.layerDipDeg</c> 同号）。煤/混合幅才有意义；岩幅与无地质模型时为 0。
        /// 煤层的采矿模型走【倾斜分层】就是拿这个角，而不是全局填一个数。
        /// </summary>
        public double SeamDipDeg;

        /// <summary>本幅应当采用的分层倾角：煤/混合幅 = 实测煤层倾角；岩幅 = 0（顶底水平）。</summary>
        public double EffectiveDipDeg => Kind == BenchKind.Rock ? 0.0 : SeamDipDeg;

        public BenchKind Kind = BenchKind.Rock;

        /// <summary>
        /// 这一幅要不要出煤体（沿底板开采）。判据是【平均煤厚 ≥ 最小可采厚】，不是占比 ——
        /// 煤体多厚由顶底板定，与台阶高无关。混合台阶同样出煤体，只是标签不同。
        /// </summary>
        public bool IsMineableCoal;

        /// <summary>本幅区间内煤厚占比（0..1）。</summary>
        public double CoalRatio;

        /// <summary>沿线平均煤厚（m）。</summary>
        public double MeanCoalThickM;

        /// <summary>涉及煤层（按平均厚度降序）。</summary>
        public readonly List<SeamShare> Seams = new();

        /// <summary>采样点数 / 其中采到地质模型（至少一层见煤）的点数。</summary>
        public int SampleCount, SampleHit;

        /// <summary>煤/岩标签的中文名。</summary>
        public string KindLabel => Kind switch
        {
            BenchKind.Coal  => "煤台阶",
            BenchKind.Mixed => "混合台阶",
            _               => "岩台阶",
        };

        /// <summary>涉及煤层摘要，如 "4-1(2.3m) / 4-2(0.8m)"；无煤返回空串。</summary>
        public string SeamSummary =>
            Seams.Count == 0 ? "" : string.Join(" / ", Seams.Select(s => $"{s.Name}({s.MeanThickM:0.##}m)"));
    }

    public sealed class Options
    {
        /// <summary>归级容差（m），透传 <see cref="BenchLevelInventory.Options.MergeTolM"/>。</summary>
        public double MergeTolM = 0.5;

        /// <summary>视为水平线的起伏上限（m）；超过判为坡面线 / 道路，不定标高。</summary>
        public double FlatTolM = 0.5;

        /// <summary>平面长度小于此值的碎线丢弃（m）；0 = 不丢。</summary>
        public double MinLengthM = 0;

        /// <summary>每幅沿坡顶线的采样点数（等弧长）。太少判不准起伏煤层，太多白烧时间。</summary>
        public int SamplesPerBench = 12;

        /// <summary>煤厚占比 ≥ 此值 = 煤台阶。</summary>
        /// <summary>
        /// 「以煤为主」的占比线 —— 只决定**标注**（煤台阶 / 混合台阶），不再决定要不要出煤体。
        ///
        /// 原先拿它当生成开关是错的：4 号煤才 1.29m，套进 15m 标准台阶占比只有 9%，
        /// 于是薄煤层【永远】判不出煤台阶。可煤体该多厚由顶底板定，跟台阶高本来就没关系 ——
        /// 把"这幅以煤为主还是以岩为主"和"这里该不该出煤体"混成一件事，是判据本身的毛病。
        /// 现在：占比 &gt; <see cref="MinMineableCoalThickM"/> 折出的下限即出煤体（沿底板开采），
        /// 占比只用来给这一幅贴标签。
        /// </summary>
        public double CoalRatioThreshold = 0.5;

        /// <summary>
        /// 最小可采煤厚（m）：幅内平均煤厚低于此值不出煤体（薄到不值得单独采）。
        /// 0 = 只要含煤就出。默认 0.8m —— 露天矿常用下限，按你的口径改。
        /// </summary>
        public double MinMineableCoalThickM = 0.8;

        /// <summary>煤厚占比 &gt; 此值（但未达煤台阶阈值）= 混合台阶；≤ 则为岩台阶。</summary>
        public double MixedRatioFloor = 0.05;

        /// <summary>
        /// 坡面水平投影上限（m）：坡顶线与坡底线的中位水平间距超过此值就不配成一幅。
        /// ≤0 = 自动取 max(30, 2.5×台阶高)。12m 台阶 70° 坡面才 4.4m，几十米的"间距"必是错配。
        /// 这是拦住"两条不相干的线被 loft 成横穿全图的张合条带"的唯一闸门，别调得太大。
        /// </summary>
        public double MaxFaceRunM = 0;

        /// <summary>
        /// 采掘带宽度 W（m），只用于按同一基线实测煤层倾角（在推进方向上前探 W 采一次底板）。
        /// 应与生成时填的 W 一致，倾斜分层的抬降量才与内核算的对得上。
        /// </summary>
        public double StripWidthM = 20.0;

        /// <summary>
        /// 同一级间隙内的相对闸门倍数：只接受间距 ≤ max(本间隙最小间距 × 此倍数, <see cref="MinAbsFaceRunM"/>) 的对。
        ///
        /// 为什么要相对闸门：绝对上限定不准 —— 坡面投影是设计给定的（12m/70° = 4.4m），
        /// 同一套设计里各幅基本一致，而"边角料"错配（剩下的外环 ↔ 剩下的内环 = 平盘宽 + 两个坡面投影）
        /// 往往只有几十米，照样能钻过一个宽松的绝对上限，多配出一幅错的。
        /// 拿本间隙【最小】间距当基准最稳：那一定是真坡面投影，别的真工作面也在同一量级。
        /// </summary>
        public double FaceRunRelaxFactor = 2.5;

        /// <summary>相对闸门的下限（m）：坡面很陡时最小间距接近 0，别把阈值收得连自己都进不去。</summary>
        public double MinAbsFaceRunM = 8.0;

        /// <summary>
        /// 允许的【最缓】坡面角（°）—— 坡面投影的上限，run ≤ H/tan(此角)。≤0 = 不启用（默认）。
        ///
        /// 【默认关闭，因为这条关系只对"按设计角度切出来的坡面"成立】
        /// 岩台阶是爆破成型的，坡顶↔坡底水平间距 = H/tan(65~75°)，很小，这条卡得住。
        /// 但沿底板开采的【煤台阶】上界是顶板、下界是底板，坡面就是煤层本身的产状 ——
        /// 间距由【煤层倾角】决定，不由设计坡面角决定。现场煤层倾角约 3°，5.87m 垂高
        /// 对应水平距 5.87/tan(3°) ≈ 112m。拿 45° 去卡它，会把所有真正沿煤层走的幅全拒掉。
        ///
        /// 所以这条只适合【明确知道该级是岩台阶】时单独启用；配对阶段还没判煤岩，不能一刀切。
        /// 留着接口是因为岩台阶那条路确实需要它，但默认值必须是"不启用"。
        /// </summary>
        public double MinFaceAngleDeg = 0.0;

        /// <summary>
        /// 参数系统读来的标准水平台账（<see cref="StandardLevelSource.FromDatabase"/>）。
        /// 非 null 且非空 → 走【吸附】：线按标高套到台账的级上，不再盲聚类。
        /// null → 退回按线聚类（老路径）。
        ///
        /// 为什么优先用台账：标准水平是设计台账，本就登记在 <c>mine_location</c> 里；盲聚类要求图上有
        /// 严格水平的设计线，实测台阶线顶点起伏一超容差就被整条剔成"坡面线"，于是一级都归不出来 ——
        /// 而这跟设计里到底有几个标准水平根本没关系。
        /// </summary>
        public IReadOnlyList<StandardLevelSpec>? StandardLevels;

        /// <summary>
        /// 吸附容差（m）：线的代表标高与标准水平差在此内即归入该级。
        /// ≤0 = 自动取 min(台阶高/3, 5m)，下限 1m —— 取台阶高的 1/3 是为了两个相邻标准水平的吸附域绝不重叠。
        /// </summary>
        public double SnapTolM = 0.0;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";

        /// <summary>归级原始结果（级数 / 各级标高 / 级间距中位数 = 台阶高）。</summary>
        public BenchLevelInventory.Result? Levels;

        /// <summary>配出的幅（自上而下）。</summary>
        public readonly List<BenchPair> Pairs = new();

        public int CoalBenches, MixedBenches, RockBenches;

        /// <summary>标准水平个数（= 归出的平盘标高级数）。</summary>
        public int LevelCount => Levels?.LevelCount ?? 0;

        /// <summary>是否接上了地质模型（没接则全判岩台阶）。</summary>
        public bool HasSeamModel;

        public readonly List<string> Warnings = new();

        /// <summary>标准水平从哪来的："参数系统台账" / "图上台阶线聚类"。</summary>
        public string LevelSource = "";

        /// <summary>台账里有、但图上没套上任何线的标准水平（标高列表，高→低）。这些级出不了幅。</summary>
        public readonly List<double> LevelsWithoutLines = new();

        /// <summary>图上有、但落不到任何标准水平吸附域内的线数（可能是等高线 / 未登记的平盘 / 坐标系不符）。</summary>
        public int UnsnappedLines;
    }

    /// <summary>
    /// 分析。<paramref name="lines"/> 同 <see cref="BenchLevelInventory"/> 的取线口径；
    /// <paramref name="sampler"/> = null 时跳过煤岩判定（全部按岩台阶出，并给出提示）。
    /// 任何异常都降级为 Ok=false，不抛。
    /// </summary>
    public static Result Analyze(IReadOnlyList<BenchLevelInventory.SourceLine>? lines,
                                 ISeamColumnSampler? sampler,
                                 Options? opt = null)
    {
        opt ??= new Options();
        var res = new Result { HasSeamModel = sampler != null };

        BenchLevelInventory.Result inv;
        if (opt.StandardLevels is { Count: > 0 })
        {
            inv = SnapToStandardLevels(lines, opt, res);
            res.LevelSource = "参数系统台账";
        }
        else
        {
            inv = BenchLevelInventory.Build(lines, new BenchLevelInventory.Options
            {
                MergeTolM = opt.MergeTolM,
                FlatTolM = opt.FlatTolM,
                SkipTilted = true,      // 标准水平只由水平线定，坡面线 / 出入沟不参与
                MinLengthM = opt.MinLengthM,
            });
            res.LevelSource = "图上台阶线聚类";
        }
        res.Levels = inv;
        if (!inv.Ok) { res.Message = inv.Message; return res; }
        res.Warnings.AddRange(inv.Warnings);

        if (inv.Levels.Count < 2)
        {
            res.Message = $"只归出 {inv.Levels.Count} 个有线的标准水平（来源：{res.LevelSource}），"
                        + "凑不出一幅台阶（一幅需要相邻两级：上级坡顶 + 下级坡底）。";
            return res;
        }

        // handle → 几何，供配对求环大小与沿线采样
        var geom = new Dictionary<ulong, double[]>();
        foreach (var l in lines!)
            if (l != null && l.Xyz is { Length: >= 6 }) geom[l.Handle] = l.Xyz;

        // ── 相邻级配对成幅 ────────────────────────────────────────────
        // 判别量 = 两条线之间的【实际水平间距】(逐点到对方折线，取中位数)，不是质心距、也不是环大小。
        //
        // 为什么不是质心距：闭合坑的台阶线是一圈套一圈的同心环，质心几乎重合，"最近质心"在那里是
        // 退化的，等于瞎猜（「按区域」构建至今是这么配的）。
        // 为什么不是环大小：对开口工作线，"平均半径"退化成半个线长 —— 变成按线长挑线，同样是瞎猜。
        //
        // 间距则在两种图上都是对的物理量：一对真坡顶/坡底之间隔的就是坡面水平投影 H/tanα
        //（12m 台阶 70° 才 4.4m），而不相干的线隔着几十上百米。于是：
        //   · 同心环坑：内环(下一台阶坡顶) 与 下级外环(本台阶坡底) 间距 = 坡面投影，比任何错配都小；
        //   · 开口工作帮：坡顶线与其正下方的坡底线本就贴着走，错配的线离得远。
        // 而且这条判据【与采/排极性无关】—— 镜像的是几何，最近的一对始终是同一坡面的那对。
        //
        // 超过 MaxFaceRunM 的一律不配（宁可不出这一幅，也不要把两条不相干的线 loft 成横穿全图的
        // 张合条带 —— 那正是错配在图上的样子）。
        int idx = 1;
        int gatedOut = 0;

        for (int k = 0; k + 1 < inv.Levels.Count; k++)
        {
            var upper = inv.Levels[k];      // 坡顶级
            var lower = inv.Levels[k + 1];  // 坡底级
            double benchH = upper.Elevation - lower.Elevation;
            if (benchH <= 1e-6) continue;

            double gate = opt.MaxFaceRunM > 0 ? opt.MaxFaceRunM : Math.Max(30.0, 2.5 * benchH);

            // 物理硬上限：坡面投影 = H/tanα，α 不可能缓过 MinFaceAngleDeg。
            // 这一条与相对闸门是【与】的关系 —— 相对闸门管"同一级里谁更像一对"，
            // 它管"这对在物理上到底可不可能是台阶坡面"。缺了它，一级里全错时最小的那个照样过关。
            double physGate = double.MaxValue;
            if (opt.MinFaceAngleDeg > 0 && opt.MinFaceAngleDeg < 90)
                physGate = benchH / Math.Tan(opt.MinFaceAngleDeg * Math.PI / 180.0);
            gate = Math.Min(gate, physGate);

            // 全部候选对按间距升序，贪心一对一消费 —— 同一级间隙有多个工作面时能各配各的
            var cands = new List<(double Dist, ulong Crest, ulong Toe)>();
            foreach (ulong ch in upper.Handles)
            {
                if (!geom.TryGetValue(ch, out var cxyz)) continue;
                foreach (ulong th in lower.Handles)
                {
                    if (!geom.TryGetValue(th, out var txyz)) continue;
                    if (BboxGapXY(cxyz, txyz) > gate) { gatedOut++; continue; }   // 便宜的粗筛
                    double d = MedianDistanceXY(cxyz, txyz, opt.SamplesPerBench);
                    if (d > gate) { gatedOut++; continue; }
                    cands.Add((d, ch, th));
                }
            }
            cands.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            if (cands.Count == 0) continue;

            // 相对闸门：本间隙最小间距 = 真坡面投影，据此收紧，拦掉"剩下的外环 ↔ 剩下的内环"这类边角料错配
            // MinAbsFaceRunM 是"别把阈值收得太死"的下限，但它绝不能把【物理上限】顶开 ——
            // 5.87m 的台阶物理上限才 5.9m，若被 8m 的下限抬上去，24m 那种错配又会漏进来。
            double relGate = Math.Min(
                Math.Max(cands[0].Dist * Math.Max(1.0, opt.FaceRunRelaxFactor),
                         Math.Max(0.0, opt.MinAbsFaceRunM)),
                gate);

            var usedCrest = new HashSet<ulong>();
            var usedToe = new HashSet<ulong>();
            foreach (var (dist, ch, th) in cands)
            {
                if (dist > relGate) { gatedOut++; continue; }
                if (!usedCrest.Add(ch)) continue;
                if (!usedToe.Add(th)) { usedCrest.Remove(ch); continue; }

                var cxyz = geom[ch];
                var pair = new BenchPair
                {
                    Index = idx++,
                    CrestZ = upper.Elevation,
                    ToeZ = lower.Elevation,
                    CrestHandle = ch,
                    ToeHandle = th,
                    CrestLengthM = PlanLength(cxyz),
                    FaceRunM = dist,
                };
                ClassifyBench(pair, cxyz, geom[th], sampler, opt);
                res.Pairs.Add(pair);
            }
        }

        if (gatedOut > 0)
            res.Warnings.Add($"有 {gatedOut} 对候选线因水平间距超过坡面投影上限被拒配 —— "
                           + "正常，相邻级里本就有大量互不相干的线。若某段台阶该出幅却没出，"
                           + "多半是坡面很缓(间距大)，调大「坡面投影上限」再试。");

        if (res.Pairs.Count == 0)
        {
            res.Message = $"归出 {inv.LevelCount} 个标准水平，但相邻级之间一幅都没配出来（检查各级是否真有成对的坡顶/坡底线）。";
            return res;
        }

        res.CoalBenches  = res.Pairs.Count(p => p.Kind == BenchKind.Coal);
        res.MixedBenches = res.Pairs.Count(p => p.Kind == BenchKind.Mixed);
        res.RockBenches  = res.Pairs.Count(p => p.Kind == BenchKind.Rock);

        if (sampler == null)
            res.Warnings.Add("库里没有煤层顶底板面（virtual_drill_surface 空）——全部按岩台阶出。"
                           + "请先在「虚拟钻孔·配置地质模型」里捕获各煤层顶/底板，再回来分煤岩。");
        else
        {
            int blind = res.Pairs.Count(p => p.SampleHit == 0);
            if (blind > 0)
                res.Warnings.Add($"{blind} 幅的采样点全部落在地质模型范围外（或该处各层均尖灭），按岩台阶计 —— "
                               + "若这些幅本该见煤，多半是台阶线与顶底板面不在同一坐标系/无重叠。");
        }

        res.Ok = true;
        res.Message = $"{inv.LevelCount} 个标准水平（{inv.BottomZ:0.##} ~ {inv.TopZ:0.##}m，台阶高中位 {inv.MedianDropM:0.##}m）"
                    + $"；配出 {res.Pairs.Count} 幅：煤 {res.CoalBenches} / 混合 {res.MixedBenches} / 岩 {res.RockBenches}。";
        return res;
    }

    // ── 煤/岩判定 ────────────────────────────────────────────────────

    /// <summary>沿坡顶线等弧长采样，算该幅区间内的煤厚占比并打标；煤/混合幅再实测煤层倾角。sampler=null → 直接判岩。</summary>
    private static void ClassifyBench(BenchPair pair, double[] crestXyz, double[] toeXyz,
                                      ISeamColumnSampler? sampler, Options opt)
    {
        if (sampler == null) { pair.Kind = BenchKind.Rock; return; }

        int n = Math.Max(2, opt.SamplesPerBench);
        var pts = SampleAlongXY(crestXyz, n);
        if (pts.Count == 0) { pair.Kind = BenchKind.Rock; return; }

        double zLow = pair.ToeZ, zHigh = pair.CrestZ;
        double sumThick = 0;
        var perSeamSum = new Dictionary<string, double>(StringComparer.Ordinal);
        int hit = 0;

        foreach (var (x, y) in pts)
        {
            var perSeam = new Dictionary<string, double>(StringComparer.Ordinal);
            double t = sampler.CoalThicknessIn(x, y, zLow, zHigh, perSeam);
            sumThick += t;
            if (t > 0) hit++;
            foreach (var kv in perSeam)
                perSeamSum[kv.Key] = (perSeamSum.TryGetValue(kv.Key, out double had) ? had : 0.0) + kv.Value;
        }

        pair.SampleCount = pts.Count;
        pair.SampleHit = hit;
        pair.MeanCoalThickM = sumThick / pts.Count;              // 分母含尖灭点：真无煤就该拉低
        double h = pair.BenchHeightM;
        pair.CoalRatio = h > 1e-6 ? Math.Min(1.0, pair.MeanCoalThickM / h) : 0.0;

        foreach (var kv in perSeamSum.OrderByDescending(kv => kv.Value))
            pair.Seams.Add(new SeamShare { Name = kv.Key, MeanThickM = kv.Value / pts.Count });

        // 出不出煤体，看【平均煤厚】够不够可采 —— 与台阶高无关。
        // 薄煤层套进 15m 标准台阶占比只有个位数，但它照样是要采的煤。
        pair.IsMineableCoal = pair.MeanCoalThickM >= opt.MinMineableCoalThickM && pair.Seams.Count >= 0
                              && pair.MeanCoalThickM > 1e-6;

        // 占比只用来贴标签：以煤为主 = 煤台阶，含煤但以岩为主 = 混合台阶。
        pair.Kind = !pair.IsMineableCoal                        ? BenchKind.Rock
                  : pair.CoalRatio >= opt.CoalRatioThreshold    ? BenchKind.Coal
                  :                                              BenchKind.Mixed;

        if (pair.Kind != BenchKind.Rock && pair.Seams.Count > 0)
            pair.SeamDipDeg = MeasureSeamDip(pts, toeXyz, pair.Seams[0].Name, sampler, opt.StripWidthM);
    }

    /// <summary>
    /// 实测煤层倾角（度）：在每个采样点沿【推进方向】前探 W，比较主煤层底板两处高程，
    /// dip = atan(Δz / W)。推进方向 d = 该点由坡底线指向坡顶线的水平单位向量（= 往高墙里，
    /// 与内核 CarveStrip 自求的坡向同向），所以正值 = 往里抬升，与 <c>layerDipDeg</c> 同号。
    ///
    /// 取【中位数】而不是均值：个别点落在煤层尖灭处或面外会给出离谱斜率，中位数不被它们带偏。
    /// 两处都采到底板的点才计；一个都没有则返回 0（退回顶底水平，不瞎倾）。
    /// </summary>
    private static double MeasureSeamDip(List<(double X, double Y)> pts, double[] toeXyz,
                                         string seamName, ISeamColumnSampler sampler, double stripWidth)
    {
        double w = stripWidth > 1e-6 ? stripWidth : 20.0;
        var dips = new List<double>(pts.Count);

        foreach (var (x, y) in pts)
        {
            // 推进方向：由坡底线上最近点指向本点（坡顶）——即垂直走向、指向高墙
            var (tx, ty) = NearestPointXY(toeXyz, x, y);
            double dx = x - tx, dy = y - ty;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;
            dx /= len; dy /= len;

            if (!TryFloorZ(sampler, seamName, x, y, out double z0)) continue;
            if (!TryFloorZ(sampler, seamName, x + dx * w, y + dy * w, out double z1)) continue;
            dips.Add(Math.Atan2(z1 - z0, w) * 180.0 / Math.PI);
        }

        if (dips.Count == 0) return 0.0;
        dips.Sort();
        int m = dips.Count / 2;
        return dips.Count % 2 == 1 ? dips[m] : (dips[m - 1] + dips[m]) * 0.5;
    }

    /// <summary>取指定煤层在 (x,y) 处的底板高程；该处该层尖灭/缺失返回 false。</summary>
    private static bool TryFloorZ(ISeamColumnSampler sampler, string seamName, double x, double y, out double z)
    {
        return sampler.TryFloorZ(seamName, x, y, out z);
    }

    // ── 几何辅助 ────────────────────────────────────────────────────

    /// <summary>沿折线按【等弧长】取 n 个 XY 采样点（含首末）。总长为 0 时退化成首点一个。</summary>
    private static List<(double X, double Y)> SampleAlongXY(double[] xyz, int n)
    {
        var outPts = new List<(double, double)>(n);
        int m = xyz.Length / 3;
        if (m == 0) return outPts;
        if (m == 1) { outPts.Add((xyz[0], xyz[1])); return outPts; }

        // 逐段累计弧长
        var acc = new double[m];
        for (int i = 1; i < m; i++)
        {
            double dx = xyz[i * 3] - xyz[(i - 1) * 3];
            double dy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1];
            acc[i] = acc[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = acc[m - 1];
        if (total <= 1e-9) { outPts.Add((xyz[0], xyz[1])); return outPts; }

        int seg = 1;
        for (int k = 0; k < n; k++)
        {
            double target = total * k / (n - 1);
            while (seg < m - 1 && acc[seg] < target) seg++;
            double segLen = acc[seg] - acc[seg - 1];
            double f = segLen > 1e-12 ? (target - acc[seg - 1]) / segLen : 0.0;
            double x = xyz[(seg - 1) * 3]     + f * (xyz[seg * 3]     - xyz[(seg - 1) * 3]);
            double y = xyz[(seg - 1) * 3 + 1] + f * (xyz[seg * 3 + 1] - xyz[(seg - 1) * 3 + 1]);
            outPts.Add((x, y));
        }
        return outPts;
    }

    /// <summary>
    /// 把线【吸附】到参数系统给的标准水平上，产出与盲聚类同形的 <see cref="BenchLevelInventory.Result"/>，
    /// 下游配对逻辑一字不改。
    ///
    /// 与盲聚类的两处关键差别：
    ///   ① 级的标高来自台账，不是线的中位数 —— 图上线再歪，标准水平也不动；
    ///   ② 起伏超限的线【不再整条剔掉】。盲聚类必须剔斜线（否则坡面线会定出假标高），
    ///      但这里标高已由台账定死，斜线只是"套不进任何吸附域"而已，让它自己落选即可。
    ///      这正是原先一级都归不出来的根因：实测台阶线顶点起伏一超 FlatTolM，整条被当坡面线扔了。
    /// </summary>
    private static BenchLevelInventory.Result SnapToStandardLevels(
        IReadOnlyList<BenchLevelInventory.SourceLine>? lines, Options opt, Result res)
    {
        var specs = opt.StandardLevels!;
        var outRes = new BenchLevelInventory.Result();

        // 吸附容差：默认 min(台阶高/3, 5m)，下限 1m。取 1/3 保证相邻两级吸附域不重叠。
        double AutoTol(double? benchH)
        {
            double h = benchH is > 0 ? benchH.Value : 0;
            double t = h > 0 ? Math.Min(h / 3.0, 5.0) : 5.0;
            return Math.Max(1.0, t);
        }

        var buckets = new Dictionary<int, List<(double Z, double Len, ulong Handle, string Layer)>>();
        int invalid = 0, tooShort = 0, unsnapped = 0;

        foreach (var l in lines ?? Array.Empty<BenchLevelInventory.SourceLine>())
        {
            var xyz = l?.Xyz;
            int n = xyz == null ? 0 : xyz.Length / 3;
            if (l == null || n < 2) { invalid++; continue; }

            // 代表标高用中位数（个别歪顶点不带偏），与 BenchLevelInventory 同口径
            var zs = new List<double>(n);
            double len = 0;
            for (int i = 0; i < n; i++)
            {
                zs.Add(xyz![i * 3 + 2]);
                if (i > 0)
                {
                    double dx = xyz[i * 3] - xyz[(i - 1) * 3];
                    double dy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1];
                    len += Math.Sqrt(dx * dx + dy * dy);
                }
            }
            if (opt.MinLengthM > 0 && len < opt.MinLengthM) { tooShort++; continue; }

            zs.Sort();
            double repZ = zs.Count % 2 == 1 ? zs[zs.Count / 2] : (zs[zs.Count / 2 - 1] + zs[zs.Count / 2]) * 0.5;

            // 找最近的标准水平
            int best = -1; double bestDz = double.MaxValue;
            for (int i = 0; i < specs.Count; i++)
            {
                double dz = Math.Abs(repZ - specs[i].ElevationM);
                if (dz < bestDz) { bestDz = dz; best = i; }
            }
            if (best < 0) { unsnapped++; continue; }

            double tol = opt.SnapTolM > 0 ? opt.SnapTolM : AutoTol(specs[best].BenchHeightM);
            if (bestDz > tol) { unsnapped++; continue; }

            if (!buckets.TryGetValue(best, out var bucket))
                buckets[best] = bucket = new List<(double, double, ulong, string)>();
            bucket.Add((repZ, len, l.Handle, l.Layer ?? ""));
        }

        res.UnsnappedLines = unsnapped;
        outRes.SkippedInvalid = invalid;
        outRes.SkippedShort = tooShort;

        // 落成级：只有【套上了线】的标准水平才能出幅；空级单独记账，不进 Levels（否则配对会跨空级乱配）
        for (int i = 0; i < specs.Count; i++)
        {
            if (!buckets.TryGetValue(i, out var bucket) || bucket.Count == 0)
            {
                res.LevelsWithoutLines.Add(specs[i].ElevationM);
                continue;
            }
            var lv = new BenchLevelInventory.Level
            {
                Index = outRes.Levels.Count + 1,
                Elevation = specs[i].ElevationM,          // ← 台账标高，不是线的中位数
                LineCount = bucket.Count,
                TotalLengthM = bucket.Sum(t => t.Len),
                SpanM = bucket.Max(t => t.Z) - bucket.Min(t => t.Z),
            };
            foreach (var t in bucket)
            {
                lv.Handles.Add(t.Handle);
                if (t.Layer.Length > 0 && !lv.Layers.Contains(t.Layer)) lv.Layers.Add(t.Layer);
            }
            outRes.Levels.Add(lv);
            outRes.UsedLineCount += bucket.Count;
        }

        if (outRes.Levels.Count == 0)
        {
            outRes.Message = $"台账里有 {specs.Count} 个标准水平，但图上 {unsnapped} 条线一条都没套上"
                           + $"（标高对不上，最近的也差出容差）。请确认取线范围是台阶线所在图层、"
                           + "且图纸与台账同一高程基准。";
            return outRes;
        }

        var drops = new List<double>();
        for (int i = 0; i < outRes.Levels.Count - 1; i++)
        {
            double d = outRes.Levels[i].Elevation - outRes.Levels[i + 1].Elevation;
            outRes.Levels[i].DropToNextM = d;
            drops.Add(d);
        }
        outRes.TopZ = outRes.Levels[0].Elevation;
        outRes.BottomZ = outRes.Levels[^1].Elevation;
        outRes.MedianDropM = drops.Count > 0 ? MedianOf(drops) : 0;

        if (res.LevelsWithoutLines.Count > 0)
            outRes.Warnings.Add($"台账 {specs.Count} 级里有 {res.LevelsWithoutLines.Count} 级图上没线"
                              + $"（{string.Join(" / ", res.LevelsWithoutLines.Select(z => z.ToString("0.##")))}m），这些级出不了幅。");
        if (unsnapped > 0)
            outRes.Warnings.Add($"另有 {unsnapped} 条线落不进任何标准水平的吸附域，已忽略"
                              + "（多半是地形等高线，也可能是台账里没登记的平盘）。");

        outRes.Ok = true;
        outRes.Message = $"按参数系统台账套上 {outRes.Levels.Count}/{specs.Count} 级："
                       + $"{outRes.BottomZ:0.##} ~ {outRes.TopZ:0.##}m，用了 {outRes.UsedLineCount} 条线。";
        return outRes;
    }

    private static double MedianOf(List<double> xs)
    {
        var s = new List<double>(xs);
        s.Sort();
        int m = s.Count / 2;
        return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) * 0.5;
    }

    /// <summary>
    /// 两条折线的【中位】水平间距：沿 a 等弧长取 n 点，各求到 b 的最近距离，取中位数。
    /// 用中位数而非最小值：两条不相干的线常在某处擦身而过，最小值会把它们判成一对；
    /// 中位数要求"整条都贴着走"，正是同一坡面的坡顶/坡底该有的样子。
    /// </summary>
    private static double MedianDistanceXY(double[] a, double[] b, int n)
    {
        var pts = SampleAlongXY(a, Math.Max(2, n));
        if (pts.Count == 0) return double.MaxValue;
        var ds = new List<double>(pts.Count);
        foreach (var (x, y) in pts) ds.Add(PointToPolylineDistXY(b, x, y));
        ds.Sort();
        int m = ds.Count / 2;
        return ds.Count % 2 == 1 ? ds[m] : (ds[m - 1] + ds[m]) * 0.5;
    }

    /// <summary>两条折线 XY 包围盒之间的间隙（相交返回 0）。粗筛用，比逐点求距便宜几个量级。</summary>
    private static double BboxGapXY(double[] a, double[] b)
    {
        (double x0, double y0, double x1, double y1) Box(double[] p)
        {
            double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                if (p[i] < mnx) mnx = p[i];
                if (p[i] > mxx) mxx = p[i];
                if (p[i + 1] < mny) mny = p[i + 1];
                if (p[i + 1] > mxy) mxy = p[i + 1];
            }
            return (mnx, mny, mxx, mxy);
        }
        var (ax0, ay0, ax1, ay1) = Box(a);
        var (bx0, by0, bx1, by1) = Box(b);
        double dx = Math.Max(0, Math.Max(bx0 - ax1, ax0 - bx1));
        double dy = Math.Max(0, Math.Max(by0 - ay1, ay0 - by1));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>点到折线的最近 XY 距离（逐段点-线段距）。</summary>
    private static double PointToPolylineDistXY(double[] poly, double px, double py)
    {
        int n = poly.Length / 3;
        if (n == 0) return double.MaxValue;
        if (n == 1) return Math.Sqrt((px - poly[0]) * (px - poly[0]) + (py - poly[1]) * (py - poly[1]));
        double best = double.MaxValue;
        for (int i = 1; i < n; i++)
        {
            double d = PointSegDistXY(px, py, poly[(i - 1) * 3], poly[(i - 1) * 3 + 1], poly[i * 3], poly[i * 3 + 1]);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>折线上距 (px,py) 最近的点（XY）。求推进方向用。</summary>
    private static (double X, double Y) NearestPointXY(double[] poly, double px, double py)
    {
        int n = poly.Length / 3;
        if (n == 0) return (px, py);
        if (n == 1) return (poly[0], poly[1]);
        double best = double.MaxValue, bx = poly[0], by = poly[1];
        for (int i = 1; i < n; i++)
        {
            double ax = poly[(i - 1) * 3], ay = poly[(i - 1) * 3 + 1];
            double cx = poly[i * 3],       cy = poly[i * 3 + 1];
            double vx = cx - ax, vy = cy - ay;
            double len2 = vx * vx + vy * vy;
            double t = len2 > 1e-18 ? ((px - ax) * vx + (py - ay) * vy) / len2 : 0.0;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = ax + t * vx, qy = ay + t * vy;
            double d = (px - qx) * (px - qx) + (py - qy) * (py - qy);
            if (d < best) { best = d; bx = qx; by = qy; }
        }
        return (bx, by);
    }

    private static double PointSegDistXY(double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double len2 = vx * vx + vy * vy;
        double t = len2 > 1e-18 ? ((px - ax) * vx + (py - ay) * vy) / len2 : 0.0;
        t = t < 0 ? 0 : (t > 1 ? 1 : t);
        double qx = ax + t * vx, qy = ay + t * vy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }

    /// <summary>平面(XY)折线长度 —— 与 <see cref="BenchLevelInventory"/> 同口径，不含坡面爬升。</summary>
    private static double PlanLength(double[] xyz)
    {
        double len = 0;
        int n = xyz.Length / 3;
        for (int i = 1; i < n; i++)
        {
            double dx = xyz[i * 3] - xyz[(i - 1) * 3];
            double dy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1];
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }

    /// <summary>清单报表（CSV）。</summary>
    public static string BuildReport(Result r, string sourceNote = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("采矿模型·标准水平清单");
        if (!string.IsNullOrWhiteSpace(sourceNote)) sb.AppendLine("取线范围," + sourceNote.Replace(',', '，'));
        sb.AppendLine($"标准水平个数,{r.LevelCount}");
        sb.AppendLine($"配出幅数,{r.Pairs.Count}");
        sb.AppendLine($"煤台阶,{r.CoalBenches}");
        sb.AppendLine($"混合台阶,{r.MixedBenches}");
        sb.AppendLine($"岩台阶,{r.RockBenches}");
        sb.AppendLine($"接入地质模型,{(r.HasSeamModel ? "是" : "否（全判岩）")}");
        sb.AppendLine();
        sb.AppendLine("序号,坡顶标高(m),坡底标高(m),台阶高(m),坡面投影(m),坡顶线长(m),性质,煤厚占比,平均煤厚(m),分层倾角(°),涉及煤层,采样命中");
        foreach (var p in r.Pairs)
            sb.AppendLine($"{p.Index},{p.CrestZ:0.##},{p.ToeZ:0.##},{p.BenchHeightM:0.##},{p.FaceRunM:0.#},{p.CrestLengthM:0.#},"
                        + $"{p.KindLabel},{p.CoalRatio:P1},{p.MeanCoalThickM:0.##},{p.EffectiveDipDeg:0.##},"
                        + $"{p.SeamSummary.Replace(',', '，')},{p.SampleHit}/{p.SampleCount}");
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("提示");
            foreach (var w in r.Warnings) sb.AppendLine(w.Replace(',', '，'));
        }
        return sb.ToString();
    }
}
