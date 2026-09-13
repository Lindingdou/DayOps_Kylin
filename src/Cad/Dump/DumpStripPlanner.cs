using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 【潜在排土位置】把排土台阶壳子按「分割长度 × 排土条带宽度」切成网格，枚举所有能排的位置。
///
/// 纯算法，不碰 GUI、不碰图纸、不碰内核 —— 台架上整条能跑。
///
/// 【为什么排土场不走露头带那条路】
/// 露头带是煤专用的：坡顶 = 顶板 ∩ 现状面、坡底 = 底板 ∩ 现状面，两条线天然同属一层煤、
/// 天然一一对应，所以采场那边靠它绕开了"猜哪两条线是一对"。排土场没有煤层，也没有露头，
/// 但它有采场没有的东西 —— **规整**：台阶按标准水平分级、同一级标高不变、坡面角是设计给定的。
/// 所以排土场回到「坡顶线 + 坡底线」这条正路，配对交给 <see cref="StandardLevelModel"/>
/// （先归标准水平、再只在相邻两级之间配），它的判据是中位水平间距，与采/排极性无关。
///
/// 【规则】
///   D1 一幅 = 一级台阶壳子（上级坡顶线 + 下级坡底线）。本类不猜配对，配好的幅从外面喂进来。
///   D2 分割长度 L：沿走向把壳子切成若干幅。等分而非"切满 L 再余一小截" —— 现场按段组织作业，
///      末尾留个十几米的零头没法派活。所以幅数 = ⌈总长/L⌉，各幅等长且 ≤ L。
///   D3 排土条带宽度 W：沿推进方向把幅切成若干带。推进方向指【坡脚/外】—— 排土是往外往上堆，
///      与采场往高墙里挖正好镜像（内核 CarveStripInput.isDump 管的就是这件事）。
///   D4 同一级台阶各带【标高不变】。这是"排土比采场规整"的本义：一级排土台阶沿排土线往外推进，
///      推第几带都还是那一级，坡顶标高、坡底标高、台阶高都不动。采场那边可没这条。
///   D5 推进到排土场区域环为止。判据用【坡脚】—— 坡脚是体的最外缘，它出了环，这一带就压线了。
///      越界即停并记账，不静默截断。
///   D6 一带的容量 = 走向长 × W × 台阶高，口径是【占容方 V容】（排土场里实际占掉的空间）。
///      它能承接多少采场剥离实方 = V容 / Kr（Kr = 残余膨胀系数），换算不在本类做 ——
///      Kr 随物料走（MaterialSpec），本类只出几何量，免得把物料口径焊死在几何里。
///
/// 【坡顶/坡底怎么对应上】这是唯一的几何坑，见 <see cref="AlignToeToCrest"/>。
/// 内核按【归一化弧长】把两条线配起来（CarveStrip.cpp 的 lerpAt），它不做方向/起点对齐 ——
/// 一条顺时针一条逆时针、或闭合环起点错开，loft 出来就是拧成麻花的体。对齐责任在调用方，即这里。
/// </summary>
public static class DumpStripPlanner
{
    /// <summary>一级排土台阶壳子 = 已配好的一幅（上级坡顶线 + 下级坡底线）。</summary>
    public sealed class BenchInput
    {
        /// <summary>台阶级序，1 = 最上一级（与 <see cref="StandardLevelModel.BenchPair.Index"/> 同源）。</summary>
        public int LevelIndex;

        /// <summary>坡顶 / 坡底代表标高（m）。</summary>
        public double CrestZ, ToeZ;

        /// <summary>坡顶线 / 坡底线折点，扁平世界坐标 [x,y,z,...]。</summary>
        public double[] CrestXyz = Array.Empty<double>();
        public double[] ToeXyz = Array.Empty<double>();

        /// <summary>
        /// 线是否闭合。图上多段线自己带这个标志（<c>TryGetPolylineWorldVertices</c> 的 out closed），
        /// 它是权威 —— 有就传进来，别让几何去猜。null = 按几何自动判（见 <see cref="IsClosed"/>）。
        /// </summary>
        public bool? CrestClosed, ToeClosed;

        /// <summary>台阶高（m）。</summary>
        public double BenchHeightM => CrestZ - ToeZ;
    }

    public sealed class Options
    {
        /// <summary>分割长度 L（m，沿走向）。≤0 = 不分幅，一级台阶一整条。</summary>
        public double PanelLengthM = 100.0;

        /// <summary>排土条带宽度 W（m，推进方向水平量）。</summary>
        public double StripWidthM = 40.0;

        /// <summary>
        /// 排土场区域环（扁平 [x0,y0,x1,y1,...]，隐式闭合）。推进到此为止。
        /// null = 不设边界，每幅只出第 1 带（当前排土线那一带）—— 没有边界就没有"潜在"可言，
        /// 无限外推是造数，不是算数。
        /// </summary>
        public double[]? BoundaryRingXy;

        /// <summary>
        /// 【排土工作帮范围环】（扁平 [x,y,...]）排土场区域**之内**再收一层 —— 本期实际排弃的那一片。
        ///
        /// <para>与 <see cref="BoundaryRingXy"/> 分工不同，两者都要：区域环是<b>推进的止点</b>
        /// （后界轨不许越出排土场），工作帮环是<b>本期干哪儿</b>（这一格算不算在内）。
        /// 所以它不参与推带、不改变几何，只在最后按<b>格质心</b>过滤 ——
        /// 质心正是台账那一行的位置，图上看到的就是它。</para>
        ///
        /// <para><b>null / 顶点不足 = 不限制</b>（旧口径：整个排土场切格）。
        /// 调用方应先过 <see cref="WorkSlopeRange.Diagnose"/>：圈错地方的表现是"一个位置都没出来"，
        /// 与"图上没配出一幅台阶"长得一模一样。</para>
        /// </summary>
        public double[]? WorkSlopeRingXy;

        /// <summary>
        /// 推进带数上限（兜底）。区域环再大也不该出几千带 —— 真出了那么多，
        /// 多半是 W 填错了量级（填了 4 而不是 40），上限把它挡在这里而不是挡在显存里。
        ///
        /// 【它是兜底，不是工作值】填小了就成了人为截断：真实数据上 50 会把一个幅截在半路
        /// （少 17 个位置 / 55 万 m³）。放到 80 之后"触顶"这条丢弃完全消失，
        /// 而 120 与 80 结果一模一样 —— 几何自己停住了，兜底没再挡人。
        /// 判断兜底闸门够不够宽，看的就是这个："再放宽还有没有变化"。
        /// </summary>
        public int MaxSteps = 80;

        /// <summary>
        /// 断点探测距离 = 本值 × W。
        ///
        /// 【它判的是"这里有没有第 1 带"，所以只该探一个 W】
        /// 断点逻辑把"在探测距离处会折回"的段【整段排除】——【排除的地方一个位置都不出，
        /// 而且不进任何丢弃计数】。探 5W 就是拿"第 5 带会不会折回"去否决第 1 带：
        /// 那一段台阶明明推得动一带，却被凭空挖掉。现场看到的就是【虚线】。
        ///
        /// 【为什么以前是 5】早先一处折回会让【整幅】停在那一带，所以提前断开确实划算
        /// （当时实测 1W 1296 个 → 5W 1665 个）。现在折回已经改成【逐带一维裁剪】
        /// （foldTrim：只让出折回的那一段，其余照出全宽），前瞻就成了纯损失。
        ///
        /// 真实数据在 8 带下逐档重量了一遍（位置数 / 库容 / **台阶线走向覆盖率**）：
        ///   1W  2264 个 · 9297 万 · **80.3%**   ← 现在的默认
        ///   2W  2111 个 · 8559 万 · 66.4%
        ///   3W  2076 个 · 8399 万 · 62.6%
        ///   5W  1995 个 · 7997 万 · 57.3%   ← 原来的默认，把 43% 的台阶线挖没了
        /// 三项全部单调，1W 是全胜。拓扑/水密/方量(0.999~1.001)/重叠(0.41%)/带号连续照旧全绿。
        ///
        /// 【教训，两条】
        /// · 修好一个东西之后要回头看它当初逼出来的补丁还需不需要 —— 5W 是"整幅停"时代的补丁，
        ///   逐带裁剪落地之后就该退役，可它带着一张漂亮的实测表活了下来。
        ///   **表是真的，前提没了。** 见 [[iterate-by-rules-not-patches]]。
        /// · 找它花了很久，因为**被挖掉的段不进丢弃计数**——丢弃清单看着完全正常。
        ///   最后是"沿台阶线逐 5m 问这里有没有位置"这张一维覆盖图把它照出来的。
        /// </summary>
        public int SplitProbeSteps = 1;

        /// <summary>
        /// 带序推进的范围：false = 逐【台阶壳子】（默认），true = 逐【级】。
        ///
        /// 占位栅格是按【级】共享的，而推进循环按壳子跑 —— 先跑的壳子把地占完，
        /// 后跑的可能一个位置都出不来。提到级这一层就是让同级所有壳子【并肩】逐带往外，
        /// 谁也不会先冲出去几百米把邻居的第 1 带占掉。
        ///
        /// 【这一项翻过两次，别只看一次实测就下结论】
        /// 早先（15 条台阶线、坡面角闸门 15°）实测逐级【每项都更差】，所以留在壳子上：
        ///   逐壳子 1703 个 · 7932 万 · 净覆盖 5.32M m² · 重叠 0.45%
        ///   逐级   1536 个 · 6430 万 · 净覆盖 4.31M m² · 重叠 0.59%
        /// 后来现场把闸门降到 3°，台阶线从 15 条变成 **41 条**、总长 18,980→51,385 m，
        /// 彼此挨得很近 —— 推 8 带时同级壳子互相抢地，走向覆盖从 88.8% 塌到 **57.9%**。
        /// **台阶线密度变了，前提就变了**，所以这一项做成可调，见 §5.0 的实测表。
        /// </summary>
        public bool BandMajorPerLevel = false;

        /// <summary>
        /// 急弯断开的保护：坏段占比超过本值就【整条不断】。
        /// 一条均匀急弯的线每一段都会被判折回，那时断开会把整条切没 —— 那种线该靠夹紧，不该断。
        /// 断开只给"其余部分推得动、只有几处推不动"的情形用。
        ///
        /// 【扫过，不是限制项】0.4/0.5/0.6/0.75/0.9 逐档量：0.6 之后完全饱和
        /// （0.75 与 0.9 结果一模一样），0.5→0.6 只多 11 个位置（0.7%）。
        /// 而调高它在"均匀急弯"那类线上有已知的翻车风险（台架里那条用例红过）。
        /// 0.7% 的收益换一个已知风险，不划算 —— 保持 0.5。
        /// </summary>
        public double SplitBadSegLimit = 0.5;

        /// <summary>
        /// 【推进距离 ≤ 源台阶线自身走向长 × 本值】。0 = 不限。
        ///
        /// 「条带」的前提是**沿着一条线往外推**。推进量一旦超过线自身的长度，
        /// 出来那片东西的形状就不再由台阶线决定、而是由拐角处的圆弧决定 ——
        /// 一条 200m 带尖拐的碎段推 2km，等距偏移的正解是一段半径 2km 的巨扇。
        /// 几何没算错，错在「把 200m 的线往外推 2km」这件事本身：
        /// 那片地方的排土形态，现有台阶线根本没有信息支撑。
        ///
        /// 【为什么不能靠区域边界兜住】边界只管"别出界"，管不了"别散开"。
        /// 实测真实数据里这类扇面撑起了**一半的库容**（39194 → 19320 万 m³），
        /// 而逐个位置的水密/算量/带号连续全绿 —— 是平面图看出来的，数值验收抓不到。
        ///
        /// 长的嵌套等高线（源线数公里）不受影响：各幅并肩推进、互相填满，那是真的排土推进。
        /// </summary>
        public double MaxReachRatio = 2.0;

        /// <summary>
        /// 一带压到【同级别的幅已经占了的地】超过这个比例就停这一幅。0 = 不查。
        ///
        /// 同一级各幅是并肩往外推的，推着推着会撞上 —— 撞上就该停，
        /// 再往外那块地已经是别人的位置了。不拦的话同一方土进两个位置的账：
        /// 实测真实数据上位置之间平面重叠 10.8%（L23 到 26.2%），
        /// 而水密、绕向、逐格真体积/清单全绿 —— 逐格判据看不见跨格的重叠。
        ///
        /// 【为什么必须配合带序推进】一幅推到底再推下一幅的话，先来的幅会把远处的地先占了，
        /// 后面那幅连第 1 带（真实排土工作面！）都进不去。现场是同级各幅并肩逐带向外，
        /// 算法的循环顺序得跟它一致。
        ///
        /// 相邻幅本来就共边，阈值不能太低 —— 5m 栅格下共边约占一带的 8%。
        /// </summary>
        public double MaxClaimedOverlap = 0.18;

        /// <summary>
        /// 凹弯把推进夹窄之后，是接着往外推还是就此收手。
        ///
        /// 收手的话，凹弯处的幅停在第 2~3 带，而左右邻幅推了 10~20 带 ——
        /// 中间留一个四周都是位置、自己没被派到的兜（实测 L16 一个 224×286m 的洞）。
        ///
        /// 【实测这条路是死的，别再试】打开之后 **一个位置都没多出来**：
        /// 57 个"夹窄后停止"原样变成 63 个"连最小有效宽都推不动"，位置数、库容分毫不差。
        /// 凹弯的**曲率中心是硬天花板** —— 一条鞋带自己往外偏移，过了曲率中心必自交，
        /// 再怎么小步挪也过不去。那个兜只能靠【整条线一起推、自交处裁掉】的推进前锋模型覆盖，
        /// 那是另一套口径（幅号不再跨带守恒），不是这里补一刀能解决的。
        /// 留着这个开关是为了钉住"试过、不行"。
        /// </summary>
        public bool ContinueAfterClamp;

        /// <summary>短于此的幅丢弃（m）。0 = 全要。</summary>
        public double MinStrikeLenM = 0.0;

        /// <summary>
        /// 坡面水平投影上限（m）。0 = 自动取 4×台阶高。
        ///
        /// 【为什么要这道兜底】坡面投影 ≥ 2W 时第 1 带的容量被钳到 0 —— 一个排不下东西的"位置"
        /// 本来就不该出。而投影动辄几百上千米的，是坡顶/坡底根本没配上（两条轨隔着几公里），
        /// 那种 loft 出来是横跨全图的三角扇面。逐点投影已经从构造上堵住了大部分，
        /// 这道闸门管剩下的：带本身就跨了尖灭区/断崖，投影再怎么取也不成对。
        /// </summary>
        public double MaxFaceRunM = 0.0;

        /// <summary>
        /// 轨的采样间距（m）：每幅按此间距重采样坡顶/坡底轨。太密则轨点爆炸，太疏则弯道走直线。
        ///
        /// 【扫过，5 就是峰值】真实数据逐档量（位置数 / 库容 / 净覆盖）：
        ///   2m  1558 · 6824 万 · 4.58M      3m 1626 · 7614 万 · 5.11M
        ///   4m  1660 · 7782 万 · 5.22M      **5m 1665 · 7815 万 · 5.24M ← 峰值**
        ///   6m  1654 · 7689 万 · 5.16M      10m 1651 · 7620 万 · 5.11M      20m 1609 · 7262 万 · 4.87M
        ///
        /// 【反直觉的一点：更细反而更差】2m 比 5m 掉 14%。折回是【逐段】判的 ——
        /// 采样越细，段越短、被判坏的段越多，"坏段超过一半就整条不断"那道保护触发得也越频繁，
        /// 于是该断开的急弯反而断不开。密度不是越高越准。
        /// </summary>
        public double RailSampleStepM = 5.0;

        /// <summary>
        /// 凹弯处夹紧后的【最小有效推进宽度】，按 W 的比例给。低于它就认定"真的没有空间"、整幅丢弃。
        ///
        /// 这条直接决定"做全"能做到多全：定得高，急弯处的幅整片没有位置（排土场上留洞）；
        /// 定得低，会出很窄的条带。0.15 = W 的 15%（W=40 时 6m）——
        /// 6m 宽的条带虽窄，但它是【真实存在的可排空间】，有库容、能被排产选中；
        /// 而一个洞是永远排不到的。宁可出窄带并标注，也不留洞。
        ///
        /// 【曾经扫过说"几乎不起作用"，那个结论已经过期】当时 <see cref="SplitProbeSteps"/>=5W，
        /// 尖角在分幅前就被提前断开（代价是挖掉 43% 的台阶线），夹紧自然很少走到。
        /// 探测距离收回 1W 之后，**被夹紧的位置从 18 个回到 33 个**（8 带实测），
        /// 有效宽 6.9~39.4 m、占总库容 1.0% —— 这条重新是活的，它接住的正是原先被挖掉的那些急弯。
        /// </summary>
        public double MinClampRatio = 0.15;

        /// <summary>
        /// 走向长的【几何下限】按 W 的比例给（与业务闸门 <see cref="MinStrikeLenM"/> 取大）。
        ///
        /// 走向比推进宽度短太多时，"条带"这个模型本身就不成立 —— 端盖的面积超过侧面，
        /// 扫出来的体是个墙比肉多的残块。
        ///
        /// 【为什么从 0.5 降到 0.35】0.5 当初是给【扇区面积公式】兜底的：那个公式对短带
        /// 算不准（走向 4m 的实测比值到 12），只能靠下限把它们挡在外面。
        /// 平面面积改成鞋带公式直接量之后，短带也算得对了 —— 下限就不必再替公式挡枪。
        /// 同一份真实数据逐档量过：
        ///   0.50 → 1811 个位置，比值 max 1.002
        ///   0.35 → 2014 个位置，比值 max 1.002　← 默认取这一档（多 203 个位置，精度没掉）
        ///   0.25 → 2060 个位置，比值 max 1.008（只多 46 个，精度开始爬）
        ///
        /// 【0.35 正卡在悬崖边上 —— 别再往下调】后来现场追"做全"，把它当成覆盖率旋钮又扫了一遍，
        /// 这次是把 2000+ 个位置【逐个喂进内核建体】看的（L=50 · W=40 · 8 带）：
        ///   0.35W(14m)  2386 个 · 走向覆盖 85.4% · 质心偏出 **0** 个 · 最差 1.04 m　← 干净
        ///   0.25W(10m)  2479 个 · 走向覆盖 87.3% · 质心偏出 3 个 · **最差 97.997 m** · 方量翻倍 4 个
        ///   0.15W( 6m)  2794 个 · 走向覆盖 92.4% · 质心偏出 15 个 · 最差 97.997 m · 方量翻倍 18 个
        ///
        /// 覆盖率一路涨得很好看（81.9%→92.4%），而 0.35 一跨过去就有位置**不在清单说的地方** ——
        /// 偏 98 m，而运距吃的正是质心。**覆盖率是个只会涨的数，不能拿它当收工信号**；
        /// 上面那三行里能证伪的只有"质心偏出"这一列。见 [[green-audit-blind-spots]]。
        /// </summary>
        public double MinStrikeRatio = 0.35;
    }

    /// <summary>一个潜在排土位置 = 一级台阶 × 一幅 × 一带。</summary>
    public sealed class Cell
    {
        /// <summary>台阶级序（1 = 最上一级）。</summary>
        public int LevelIndex;
        /// <summary>沿走向第几幅（1 起）。</summary>
        public int PanelIndex;
        /// <summary>本级共几幅。</summary>
        public int PanelCount;
        /// <summary>沿推进方向第几带（1 = 当前排土线那一带）。</summary>
        public int StepIndex;

        /// <summary>
        /// 带内再切分的序号（1 起；0 = 该带没切）。外凸拐角推出去后带会变长，
        /// 超过分割长度 L 就在带内再切 —— 一个位置沿走向始终 ≤ L。见 <see cref="SubCount"/>。
        /// </summary>
        public int SubIndex;
        /// <summary>本带共切成几个位置（1 = 没切）。</summary>
        public int SubCount = 1;

        /// <summary>坡顶 / 坡底标高（m）—— 同一级各带不变（D4）。</summary>
        public double CrestZ, ToeZ;
        public double BenchHeightM => CrestZ - ToeZ;

        /// <summary>本带的前脸轨：坡顶 / 坡底，扁平 [x,y,z,...]，逐点对应、等长。喂内核建体用。</summary>
        public double[] CrestXyz = Array.Empty<double>();
        public double[] ToeXyz = Array.Empty<double>();

        /// <summary>走向长度（m，水平）—— 本带【前脸轨】的长度。</summary>
        public double StrikeLenM;

        /// <summary>
        /// 本带的平面面积（m²）= W × (前脸轨长 + 后界轨长)/2。
        /// 弯道上前后两条轨不等长，条带是个环形扇区，拿 L前 × W 会算少（凹角）或算多（凸角）。
        /// 容量 = 本值 × 台阶高（再按第 1 带的坡面楔子折一下）。
        /// </summary>
        public double PlanAreaM2;
        /// <summary>推进宽度（m）= W。</summary>
        public double StripWidthM;

        /// <summary>
        /// 容量（m³，占容方）= 走向长 × W × 台阶高。
        /// 截面是平行四边形（顶/底水平，前/后是同一坡面平移 W），面积 = W × H，不是梯形 ——
        /// 相邻带首尾相接正好填满整级台阶，这才是"条带"该有的账。
        /// </summary>
        public double CapacityM3;

        /// <summary>质心（世界坐标）—— 寻径算运距吃的就是它。</summary>
        public double Cx, Cy, Cz;

        /// <summary>本带前脸的水平投影（m）。第 1 带 = 真坡面的 run；其后各带 = ε（近竖直的格子界）。</summary>
        public double FaceRunM;

        /// <summary>
        /// 是不是当前排土工作面那一带（第 1 带）。只有它的前脸是图上真实存在的坡面，
        /// 其余各带的前脸是格子界，不是脸 —— 报表和着色要分得开。
        /// </summary>
        public bool IsWorkingFace;

        /// <summary>
        /// 本带的推进宽度被【凹弯夹窄】过（<see cref="StripWidthM"/> &lt; 名义 W）。
        /// 急弯处再往外推必自交，夹窄是几何允许的极限 —— 窄的体是真的，自交的体是假的。
        /// 报表要把它标出来：那不是设计宽度，是地形逼出来的。
        /// </summary>
        public bool IsClamped;

        /// <summary>位置编号，如 "外排1-L3-P02-S05"。人看的定位，也是落库的自然键。</summary>
        public string Code = "";
    }

    /// <summary>被丢弃 / 被截断的记录 —— 少了多少、为什么少，必须说得清。</summary>
    public sealed class Drop
    {
        public string Reason = "";
        public int Count;
        public double LostStrikeLenM;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public List<Cell> Cells = new();
        public List<Drop> Drops = new();
        public List<string> Notes = new();

        public int LevelCount => Cells.Select(c => c.LevelIndex).Distinct().Count();
        public double TotalCapacityM3 => Cells.Sum(c => c.CapacityM3);

        /// <summary>按台阶级汇总：位置数 / 容量。</summary>
        public IEnumerable<(int Level, int Cells, double CapacityM3)> PerLevel()
            => Cells.GroupBy(c => c.LevelIndex)
                    .Select(g => (g.Key, g.Count(), g.Sum(c => c.CapacityM3)))
                    .OrderBy(t => t.Item1);
    }

    /// <summary>
    /// 枚举潜在排土位置。<paramref name="sitePrefix"/> 进位置编号（通常是排土场名）。
    /// 任何一级出问题只影响该级，其余照出；异常一律降级为记账，不抛。
    /// </summary>
    public static Result Plan(IReadOnlyList<BenchInput>? benches, Options? opt = null, string sitePrefix = "排土")
    {
        opt ??= new Options();
        var res = new Result();

        if (benches == null || benches.Count == 0)
        { res.Message = "没有可切分的排土台阶 —— 图上这个排土场没配出一幅（上级坡顶线 + 下级坡底线）。"; return res; }

        double W = opt.StripWidthM > 1e-6 ? opt.StripWidthM : 40.0;
        int maxSteps = Math.Max(1, opt.MaxSteps);
        int dropShort = 0, dropDegenerate = 0; double lostShort = 0;
        int dropWideFace = 0; double lostWideFace = 0;
        int collidedStop = 0, collidedTrim = 0, foldTrim = 0, boundaryTrim = 0, dropFlatFace = 0, dropFanDominated = 0, dropPinchedFace = 0;
        var claimed = new Dictionary<int, HashSet<long>>();
        var panelSeqByLevel = new Dictionary<int, int>();
        var allPanels = new List<PanelState>();   // 整级的幅收齐了再一起推，见下面的带序循环      // 幅号按级连续，见下   // 逐级的占位栅格，防同一方土进两本账
        int truncatedByBoundary = 0, truncatedByReach = 0, truncatedByCap = 0, foldedStop = 0, clampedStop = 0, splitRuns = 0;
        int dropSelfOverlap = 0;
        int splitCut = 0; double lostSplit = 0;   // 断点挖掉的台阶线（唯一无声丢地处，必须报）
        int pinchedTrim = 0; double lostPinched = 0;   // 前脸退化：裁到好的那一段，丢多少要报
        int probeStepsUsed = Math.Max(1, Math.Min(opt.SplitProbeSteps, maxSteps));

        if (opt.BoundaryRingXy is not { Length: >= 6 })
            res.Notes.Add("没给排土场区域边界 —— 每幅只出第 1 带（当前排土线那一带）。"
                        + "「所有潜在位置」要有边界才谈得上，无边界外推是造数。");

        foreach (var b in benches)
        {
            if (b == null) continue;
            // 【幅号按「级」连续，不是按台阶壳子】一级台阶常常有好几条壳子
            //（现状面切出来的带被区域边界或坡面角闸门打断），先前每条壳子都从 1 重新数，
            // 于是同一级里出现两个 P01 —— 位置编号撞号，实测 1018 个位置里有 60 个
            // 无法唯一标识，跨 11 个级。编号是位置的身份：落库靠它做唯一索引、
            // 排产按它取用、图上按它挂属性，撞号的两个位置在下游就是同一个东西。
            if (!panelSeqByLevel.TryGetValue(b.LevelIndex, out int panelSeq)) panelSeq = 0;
            int nc = b.CrestXyz.Length / 3, nt = b.ToeXyz.Length / 3;
            if (nc < 2 || nt < 2) { dropDegenerate++; continue; }
            if (b.BenchHeightM <= 1e-6) { dropDegenerate++; continue; }

            // 坡底轨对齐到坡顶轨（方向 + 闭合环起点），否则 loft 拧麻花
            double[] crestUse = b.CrestXyz;
            double[] toeAligned = AlignToeToCrest(crestUse, b.ToeXyz, b.CrestClosed, b.ToeClosed);

            // 闭合环补上收尾段：末点→首点那一截不在折点表里，不补就整圈漏掉一段
            // （72 点的环漏 1/72，点越少漏越多，而排土台阶线常是十几个折点的粗环）。
            crestUse = CloseIfRing(crestUse, b.CrestClosed);
            toeAligned = CloseIfRing(toeAligned, b.ToeClosed);

            double crestLen = PlanLength(crestUse);
            if (crestLen <= 1e-6) { dropDegenerate++; continue; }

            // 【急弯处先断开，再分幅】
            //
            // 一个幅里往往只有【一处】尖内角，其余部分完全推得动。整幅丢掉的话，
            // 排土场上那一片就永远没有位置 —— 实测 44 个幅是这么整片丢掉的。
            // 现场对这种台阶线的理解本来也是"有个尖内角，那是两个工作面"。
            // 所以在分幅之前先把折回点找出来当强制断点，两侧各自成幅。
            // 【探测距离 = 一带，不跟推进带数走】断点逻辑把"在探测距离处会折回"的段
            // 【整段排除】—— 排除的地方一个位置都不出，而且不进任何丢弃计数。
            // 它回答的是"这一段有没有第 1 带"，所以只能拿【第 1 带真会走的那个距离】去问；
            // 拿第 5 带的折回去否决第 1 带，就是凭空在台阶线上挖掉一截截 ——
            // 现场截图上那条"虚线"就是它。第 2 带起的折回由 foldTrim 逐带一维裁剪处理，
            // 那是【让出折回的那一段】而不是【当这截台阶不存在】。
            // 实测（8 带）：探 5W 走向覆盖 57.3%，探 1W 80.3%，位置 1995→2264。
            // 仍留 min(…, maxSteps)：设定值大于实际带数时没有前瞻的意义。
            int probeSteps = probeStepsUsed;
            // 【切向窗口不跟"带数上限"走 —— 它是【本次探测】的尺度，不是"将来最多推多远"】
            //
            // 原来传的是 maxSteps，而 SplitAtFoldPoints 内部又拿它乘一遍
            //（far = stripWidthM × maxSteps，而 stripWidthM 本身已经是 W×probeSteps）——
            // 于是"最多推几带"这个**纯上限**参数，反过来改变了【第 1 带】的分幅结果。
            //
            // 实测（3° 那份 41 条台阶线）：光把上限从 3 提到 8，第 1 带的幅数
            // **1050 → 745**、前脸总长 43,156 → 29,729 m。第 1 带在坡顶线上，
            // 与后面推几带毫无关系，它变了就是上游被污染了。
            // 机理：窗口从 120m 撑到 320m，把密而弯的台阶线的曲率抹平，
            // 逐边法向的**正负号**跟着变 → 折回判据标坏的段变了 → 分幅结果变了。
            //（早先"窗口宽窄构造性惰性"的实测是在 15 条稀疏直线上做的，那时抹不平什么。）
            var runs = SplitAtFoldPoints(crestUse, toeAligned, W * probeSteps, probeSteps,
                                         Math.Max(0.1, Math.Min(0.95, opt.SplitBadSegLimit)), probeSteps);

            // 【落在急弯上的那些段，报个数就行，不能记成"丢了"】
            //
            // 它们现在照常成幅（Stub），死在哪一步就由哪一步的丢弃理由记账 ——
            // 实测绝大多数死在"走向短于 20m"：急弯段是逐条折线段来的，平均只有 11 m。
            // 这里再记一次就是【重复计账】：同一截既算"急弯挖掉"又算"走向太短"。
            // 只统计条数与长度，供现场判断"有多少走向长只能靠夹窄"。
            {
                double stub = crestLen * runs.Where(r => r.Blocked).Sum(r => Math.Max(0.0, r.T1 - r.T0));
                if (stub > 1e-6)
                { splitCut += runs.Count(r => r.Blocked); lostSplit += stub; }
            }

            foreach (var (rt0, rt1, rtBlocked) in runs)
            {
            double runLen = crestLen * (rt1 - rt0);
            if (runLen <= 1e-6) continue;

            // D2 等分分幅：幅数 = ⌈段长/L⌉，各幅等长且 ≤ L
            int panelCount = opt.PanelLengthM > 1e-6
                ? Math.Max(1, (int)Math.Ceiling(runLen / opt.PanelLengthM - 1e-9))
                : 1;
            double panelLen = runLen / panelCount;

            // 丢的是【本段】，不是整条台阶线 —— 原来加的是 crestLen，
            // 于是 19,000 m 的台阶线报出 229,789 m 的损失（一条线被断成 N 段就重复计 N 次）。
            if (opt.MinStrikeLenM > 0 && panelLen < opt.MinStrikeLenM)
            { dropShort += panelCount; lostShort += runLen; continue; }

            int samplesPerPanel = Math.Max(4, (int)Math.Ceiling(panelLen / Math.Max(0.5, opt.RailSampleStepM)) + 1);

            for (int p = 0; p < panelCount; p++)
            {
                // 幅的归一化弧长区间落在【本段】之内，不是整条轨
                double t0 = rt0 + (rt1 - rt0) * p / panelCount;
                double t1 = rt0 + (rt1 - rt0) * (p + 1) / panelCount;
                panelSeq++;

                var crest0 = ResampleByArcRange(crestUse, t0, t1, samplesPerPanel);
                if (crest0.Length < 6) { dropDegenerate++; continue; }

                // 【坡底幅逐点投影取，不按弧长切】
                //
                // 早先两条轨各按【同一段归一化弧长】切，看着"天然对应"，在同心环上也确实对。
                // 但从现状面切出来的带不是同心环：坡顶线与坡底线绕着起伏地形走，形状和长度差很多，
                // 于是"第 3 幅的坡顶"和"第 3 幅的坡底"根本不在一处。
                // 实测：走向长只有 46m 的位置，XY 跨度却是 2886 × 3456m —— 两条轨隔着 3 公里，
                // loft 出来是横跨全图的三角扇面。这是"拧麻花"的同一类错误降到了【幅】这一层。
                //
                // 逐点投影到坡底折线取最近点，局部成对是构造性保证的，与两条线的总长无关。
                var toe0 = ProjectOnto(crest0, toeAligned);
                if (toe0.Length < 6) { dropDegenerate++; continue; }

                // D4 标高按【级】定死，不用折点自带的 Z —— 实测线歪几十厘米，
                // 让它带进来就变成"同一级各带高矮不一"，那正是排土规整该排除的噪声。
                SetZ(crest0, b.CrestZ);
                SetZ(toe0, b.ToeZ);

                double strikeLen = PlanLength(crest0);
                if (strikeLen <= 1e-6) { dropDegenerate++; continue; }

                // 【几何下限：走向长必须撑得起一个条带】
                //
                // 用户填的「最短幅长」是业务闸门（多短的位置不值得派活）；这里还要一道几何的：
                // 走向 4m 却要推 40m 宽的"条带"不是条带，是个墙比肉多的残块 ——
                // 实测这种幅出的体绕向不一致、体积为 0，却照样往总库容里塞了几千方【幽灵容量】。
                // 下限取 W/2：比它还短，端盖的面积就超过侧面，"条带"这个模型本身不成立了。
                double strikeFloor = Math.Max(opt.MinStrikeLenM,
                                              W * Math.Max(0.05, Math.Min(2.0, opt.MinStrikeRatio)));
                if (strikeLen < strikeFloor)
                { dropShort++; lostShort += strikeLen; continue; }

                // D3 推进方向：逐点垂直走向，指坡脚/外。
                // 切向窗口取【最远推进距离】那个尺度 —— 偏置多远，急弯就得在多远的尺度上摊平，
                // 否则最外那几带必折回。只按 W 取的话，推 5 带（200m）时窗口还是不够。
                // 【同上：窗口按【一带】取，不按带数上限】否则"最多推几带"这个纯上限
                // 会反过来改变第 1 带的几何。见上面 SplitAtFoldPoints 那段的实测。
                var dir = AdvanceDirs(crest0, toe0, Math.Max(W, 2.0));

                // 坡面水平投影（run）：第 1 带的前脸是真坡面，容量要扣掉坡前那个楔子
                double faceRun = MeanGapXY(crest0, toe0);
                double H = b.BenchHeightM;

                // 兜底：投影离谱 = 这一幅的坡顶/坡底压根没配上，出体只会是横跨全图的扇面
                double runCap = opt.MaxFaceRunM > 1e-6 ? opt.MaxFaceRunM : 4.0 * H;
                if (faceRun > runCap) { dropWideFace++; lostWideFace += strikeLen; continue; }

                // 第 2 带起前脸留的 ε 缝，见 EpsGapM 说明
                double eps = EpsGapM(W);

                allPanels.Add(new PanelState
                {
                    Bench = b,
                    Crest0 = crest0, Toe0 = toe0, Dir = dir, Eps = eps, H = H,
                    FaceRun = faceRun, StrikeFloor = strikeFloor, CrestLen = crestLen,
                    PanelSeq = panelSeq, PanelCount = panelCount,
                    IsStub = rtBlocked,
                });
            }
            }   // ← 急弯断开出的各段
            if (runs.Count > 1) splitRuns += runs.Count - 1;

            panelSeqByLevel[b.LevelIndex] = panelSeq;   // 下一条同级壳子接着往下数
        }

        // 【同级各幅并肩逐带向外，不是一幅推到底再推下一幅】
        //
        // 现场的排土推进就是这样：整条排土线一带一带往外长，不是先把一幅推到边界
        // 再回头推邻幅。循环顺序跟它一致，才谈得上"撞上了就停"——
        // 一幅推到底的话，先来的幅会把远处的地先占了，后面那幅连第 1 带
        //（真实排土工作面！）都进不去。
        //
        // 【范围是「台阶壳子」，不是「级」—— 试过提到级，实测更差，别再改】
        //
        // 动机是公平：占位栅格逐级共享，而循环逐壳子跑，同一级若有两条独立壳子，
        // 第一条会把地占完，第二条一个位置都出不来（台架喂两条面对面的直线：A 推 360m / B 出 0）。
        //
        // 可把范围提到「级」之后真实数据上**每项都更差**，而且【两次都是】：
        //   逐壳子                    1703 个 · 7932 万 · 净覆盖 5.32M m² · 重叠 0.45%
        //   逐级（撞车=整幅停那版）    1496 个 · 6302 万 · 净覆盖 4.23M m² · 重叠 0.57%
        //   逐级（撞车已改成裁带之后） 1536 个 · 6430 万 · 净覆盖 4.31M m² · 重叠 0.59%
        //
        // 第一次我判的根因是"撞上就整幅停这条规则太狠 —— 齐步走就双双卡住"。
        // 后来把撞车处置改成"只让出撞上的那一段"（见下面 Band 里那段），**重测结论没变**：
        // 裁带只挽回一点点，19% 的覆盖差还在。所以那个根因判断也是错的 ——
        // 差异纯粹来自 Band() 的调用顺序，不是停止规则的宽严。真正的机理还没查清。
        //
        // 在查清之前按数据走：范围留在壳子上。
        //
        // 【但同级壳子之间的先后要按线长排，不能按图上顺序】
        //
        // 「真实数据上偏袒问题不咬」这句话是错的，实测打脸：L5 有 4 条壳子，
        // 图上顺序把 **182m 的短头** 排在 **1613m 的主线** 前面，两者只隔 340m ——
        // 短头先把地占了，主线单独跑能出 108 个位置，混在一起整个 L5 只出 107 个。
        // 那条主线的走向覆盖从单跑的 71% 掉到 45%。
        //
        // 排土场上先推的是【主工作面】，不是一截短头。按线长降序 = 主推进面优先，
        // 是个有原理支撑的顺序（不像"幅序正反"那种只值 0.4% 的过拟合）。
        var shellGroups = allPanels.GroupBy(p => p.Bench!)
                                   .OrderBy(g => g.Key.LevelIndex)
                                   .ThenByDescending(g => PlanLength(g.Key.CrestXyz))
                                   .ToList();
        // 【范围：逐壳子 or 逐级】—— 见 Options.BandMajorPerLevel，随台阶线密度而变
        var groups = opt.BandMajorPerLevel
            ? shellGroups.GroupBy(g => g.Key.LevelIndex)
                         .OrderBy(g => g.Key)
                         .Select(g => (IEnumerable<PanelState>)g.SelectMany(x => x).ToList())
                         .ToList()
            : shellGroups.Select(g => (IEnumerable<PanelState>)g).ToList();

        foreach (var lv in groups)
        {
            // 【主推进面先占地，急弯上的窄带（Stub）最后补】
            // 顺序在这里定一次就够 —— live 每带原序遍历，所以每一带 Stub 都排在后面。
            var live = lv.OrderBy(p => p.IsStub ? 1 : 0).ThenBy(p => p.PanelSeq).ToList();
            for (int k = 1; k <= maxSteps && live.Count > 0; k++)
            {
                var next = new List<PanelState>(live.Count);
                // 【幅序只值 0.4%，别在这上面做文章】占位栅格先到先得，同一带内谁先推谁先占地，
                // 顺序理论上会影响谁被拦。实测正序 1665 个 / 7815 万，反序 1670 个 / 7845 万 ——
                // 位置 +0.3%、库容 +0.4%、重叠 0.41→0.36%、兜 1.18→1.13%，五项都略好但都在噪声量级。
                // 而"反序"本身没有原理支撑：它在这份数据上碰巧好一点，换一份可能反过来。
                // 为 0.4% 挑一个没道理的顺序是过拟合 —— 按幅号正序走。
                foreach (var pn in live)
                    if (Band(pn.Bench!, pn, k)) next.Add(pn);
                live = next;
            }
        }



        // ── 一幅的一带：出体、记账，返回"这一幅还能不能继续往外推" ──────────
        bool Band(BenchInput b, PanelState pn, int k)
        {
            double[] crest0 = pn.Crest0, toe0 = pn.Toe0;
            var dir = pn.Dir;
            double eps = pn.Eps, H = pn.H, faceRun = pn.FaceRun,
                   strikeFloor = pn.StrikeFloor, crestLen = pn.CrestLen;
            int panelSeq = pn.PanelSeq, panelCount = pn.PanelCount;

                // 前界接着上一带的实际后界走 —— 上一带被凹弯夹窄过的话，
                // 本带就该从那个窄位置起，而不是从名义的 k·W 起（那会跳过一条缝）
                double front = pn.Reached > 0 ? pn.Reached : (k - 1) * W;
                double back = front + W;         // 本带后界（内核把后脸放在前脸轨 + W 处）

                // 推进不能远超源台阶线自身的长度 —— 再往外，形状就由拐角圆弧说了算了
                if (opt.MaxReachRatio > 1e-6 && back > crestLen * opt.MaxReachRatio)
                { if (k > 1) truncatedByReach++; return false; }

                // 后界轨：既用于边界判断，也用于算平面面积（见下面的容量口径）
                //
                // 【折回必须查在后界轨上】先前只查前脸轨，而第 1 带的前脸偏移量是 0、
                // 永远不会折 —— 真正会折的是外扩了 W 的后界。凹角曲率半径小于 W 时后界翻到
                // 另一侧，体被压塌：实测真体积只有清单库容的 0.2%（132 vs 65478 m³），
                // 而拓扑自检和边界判断都放它过去了。
                var backLine = OffsetRail(crest0, dir, back, out int backFold);
                bool clampedHere = false;
                if (backFold > 0)
                {
                    // 【折回不丢整幅，先把推进夹到曲率半径之内】
                    //
                    // 内核对同一情形的处置就是夹紧而不是放弃：「急弯处的体变窄，
                    // 但窄的体是真的，自交的体是假的」。先前这里直接 break，
                    // 实测约 129/228 个幅（57%）在第 1 带就折回、一个位置都不出 ——
                    // 排土场上那么大一片空着没有位置，排产就排不到那儿去。
                    //
                    // 二分找【不折回的最大推进距离】：那是这一处几何允许的极限，
                    // 出一条窄带把空间填上，然后停（再往外必自交）。
                    double lo = front, hi = back;
                    for (int it = 0; it < 14 && hi - lo > 0.25; it++)
                    {
                        double mid = (lo + hi) * 0.5;
                        OffsetRail(crest0, dir, mid, out int f);
                        if (f > 0) hi = mid; else lo = mid;
                    }
                    // 【先试"只在没折回的那一段上出全宽"，不行再退回夹窄】
                    //
                    // 夹窄是把【整条】按最窄处的宽度出一条 —— 折回只发生在局部时太亏。
                    // 撞车那边已经验过同一个思路：沿走向找"哪一段不行"是【一维划分】，
                    // 逐段问折回判据就行（+38 个位置 / +117 万 m³）。这里照做。
                    //
                    // 取最长的一段连续"不折"，三条轨按下标裁到它，按全宽 W 出体，然后停。
                    // 比较的是"整条 × 窄宽" vs "一段 × 全宽"，谁大取谁 —— 由下面的面积判。
                    {
                        // 【逐段折回让 OffsetRail 回填，别自己拿下标去减】
                        // 原先这里也是 foldedAtW[i+1] − foldedAtW[i] —— 与 SplitAtFoldPoints
                        // 同一个错：圆角接头插点后偏移轨比原轨长，foldedAtW[i] 不是原轨第 i 点。
                        // 那两句 Math.Min 限幅看着像在防越界，实际上插了点之后【根本不会触发】，
                        // 只是安安静静地读错点。这一处比那一处更要紧：它逐带都跑，
                        // 真实数据上一轮就命中 150 次（"凹弯折回：只在没折回的那一段上出全宽"）。
                        int nSeg = crest0.Length / 3 - 1;
                        var badAtW = new bool[Math.Max(1, nSeg)];
                        OffsetRail(crest0, dir, back, out _, badAtW);
                        var okSeg = new bool[Math.Max(1, nSeg)];
                        for (int i = 0; i < nSeg; i++) okSeg[i] = !badAtW[i];
                        int fi = -1, fl = 0, fs = -1;
                        for (int i = 0; i <= nSeg; i++)
                        {
                            bool ok = i < nSeg && okSeg[i];
                            if (ok) { if (fs < 0) fs = i; }
                            else if (fs >= 0) { if (i - fs > fl) { fl = i - fs; fi = fs; } fs = -1; }
                        }
                        if (fi >= 0 && fl >= 1)
                        {
                            var cF = SliceByIndex(crest0, fi, fi + fl);
                            if (cF.Length >= 6 && PlanLength(cF) >= strikeFloor)
                            {
                                // "一段 × 全宽" 比 "整条 × 夹窄宽" 大才换
                                double narrow = Math.Max(0.0, lo - front);
                                if (PlanLength(cF) * W > PlanLength(crest0) * narrow)
                                {
                                    var tF = SliceByIndex(toe0, fi, fi + fl);
                                    if (tF.Length >= 6)
                                    {
                                        crest0 = cF; toe0 = tF;
                                        // 窗口按一带取 —— 与分幅处同口径，否则带数上限会污染第 1 带
                                        dir = AdvanceDirs(crest0, toe0, Math.Max(W, 2.0));
                                        backLine = OffsetRail(crest0, dir, back);
                                        foldTrim++;
                                        goto foldHandled;
                                    }
                                }
                            }
                        }
                    }

                    // 连最小有效宽都推不动 —— 那是真的没有空间，如实丢弃
                    double minClamp = W * Math.Max(0.02, Math.Min(0.9, opt.MinClampRatio));
                    if (lo - front < minClamp) { foldedStop++; return false; }
                    back = lo;
                    backLine = OffsetRail(crest0, dir, back);
                    clampedHere = true;
                foldHandled: ;
                }
                double wEff = back - front;      // 本带的【有效推进宽度】，正常时 = W

                // 无边界只出第 1 带（边界裁切挪到 planArea 之后，见下面）
                if (opt.BoundaryRingXy is not { Length: >= 6 } && k > 1) return false;

                double[] crestK, toeK;
                if (k == 1)
                {
                    // 第 1 带 = 当前排土工作面那一带：前脸是【真坡面】(坡顶线 ↔ 坡底线)，
                    // 这就是台阶模型的脸，也是图上唯一一张真实存在的坡面。
                    crestK = crest0; toeK = toe0;
                }
                else
                {
                    // 第 2 带起前脸取【竖直断面】—— 它不是真实的脸，只是格子的界。
                    //
                    // 【为什么必须竖直】内核的后脸本来就是竖直的（CarveStrip.cpp 里 cb/tb 共用同一 XY，
                    // 防折回那一遍还会再强制一次）。若每带都拿真坡面当前脸，相邻两带之间会漏掉
                    // 一个楔子（面积 ½·run·H），本算例是每带少 10% —— 那不是误差，是"所有潜在位置"
                    // 根本没盖住整级台阶，全场累计成一笔说不清的库容。前脸也竖直，两带才拼得严。
                    crestK = OffsetRail(crest0, dir, front);
                    toeK = OffsetRail(crest0, dir, front + eps);
                }
                SetZ(crestK = (double[])crestK.Clone(), b.CrestZ);
                SetZ(toeK = (double[])toeK.Clone(), b.ToeZ);

                // 【平面面积用鞋带公式直接量，不做任何形状假设】
                //
                // 这一项前后换过三次口径，每次都是被真实数据打回来的：
                //   ① L前 × W        —— 弯道上前后两条轨不等长，拐角处差到 7.5 倍
                //   ② W × (L前+L后)/2 —— 环形扇区的【精确解】，正常弯道对到 0.1% 以内，
                //      但它假设"等半径扇区"。短幅撞上尖凸角时 miter 把后界轨从 20m 撑到 94m，
                //      假设不成立，实测比值掉到 0.131。
                //   ③ 鞋带公式 —— 前脸轨 + 反向后界轨围成的多边形，直接量。不假设任何形状，
                //      弯的直的尖的都对；局部自重叠时正负相消，给出的正是【净覆盖面积】。
                // 容量再按"有效宽里要扣掉的那点"折一下（第 1 带扣坡面楔子，其后扣 ε 缝）。
                int emitted = 0;   // 本带出了几个格子；一个都没出就得停（见下面的带号连续不变量）
                bool collidedStopAfterTrim = false;
                double strikeK = PlanLength(crestK);
                double planArea = PolygonAreaXY(crestK, backLine);

                // 【净面积远小于名义面积 = 多边形自重叠】鞋带在重叠处正负相消，
                // 算出来接近 0 —— 那说明这一带的前后脸缠在一起，本来就不是个能装东西的位置。
                // 折回判据是逐段看的，跨多段的缓慢缠绕它抓不到；这一条从【面积】这个整体量上兜住。
                // 实测这种带的实体体积能到清单的 50 倍（清单≈0），留着只会污染账。
                //
                // 【三成→五成】三成时真实数据上还漏过一个：净面积只有名义的 0.356，
                // 它的真体积是清单的 1.582 倍（鞋带把自重叠正负相消了，内核建出来的体没有）。
                // 逐个位置量过一遍，这条比值的低端是 **0.356 一个，然后直接跳到 0.804** ——
                // 中间是空的，闸门放在 0.4~0.7 任何一处都只丢那一个，代价 0.01% 库容。
                if (planArea < strikeK * wEff * 0.5)
                { dropSelfOverlap++; return false; }
                // D5 后界压出区域环即停 —— 后界是本带的最外缘
                if (opt.BoundaryRingXy is { Length: >= 6 })
                {
                    if (!RailInRing(backLine, opt.BoundaryRingXy))
                    {
                        // 【只让出出界的那一段，别把整幅都停了】
                        //
                        // 原先是后界轨任一点出环就整幅停 —— 环是不规则的，一幅的后界常常
                        // 只有一头探出去，剩下大半还在里面，跟着一起没了。
                        // 撞车、折回两处已经验过同一个思路（合计 +77 个位置 / +294 万 m³）：
                        // 沿走向找"哪一段不行"是【一维划分】，逐点问就行。
                        int nb = backLine.Length / 3;
                        var ins = new bool[nb];
                        for (int i = 0; i < nb; i++)
                            ins[i] = PointInRingXY(backLine[i * 3], backLine[i * 3 + 1],
                                                   opt.BoundaryRingXy);
                        int gi = LongestRun(ins, out int gl);
                        if (gi < 0 || gl < 2) { if (k > 1) truncatedByBoundary++; return false; }

                        int hi2 = Math.Min(crestK.Length / 3 - 1, gi + gl - 1);
                        var cIn = SliceByIndex(crestK, gi, hi2);
                        var tIn = SliceByIndex(toeK, gi, hi2);
                        if (cIn.Length < 6 || tIn.Length < 6 || PlanLength(cIn) < strikeFloor)
                        { if (k > 1) truncatedByBoundary++; return false; }

                        // 后界按裁过的前脸重算 —— 用整带偏移的切片会 splay，
                        // 内核建出来的体对不上（撞车那处栽过，4 个位置掉到清单的 78%）
                        crestK = cIn; toeK = tIn;
                        backLine = OffsetRail(crestK, AdvanceDirs(crestK, toeK, Math.Max(W, 2.0)), wEff);
                        if (backLine.Length < 6) { truncatedByBoundary++; return false; }
                        strikeK = PlanLength(crestK);
                        planArea = PolygonAreaXY(crestK, backLine);
                        if (planArea <= 1e-6) { truncatedByBoundary++; return false; }
                        // 【裁完要把自重叠闸门再过一遍】裁切会重算 planArea，
                        // 而原来的闸门在裁之前就判过了 —— 裁成自重叠的带就这么漏过去。
                        // 实测漏过一个净面积只有名义 0.37 的，真体积是清单的 3.46 倍。
                        if (planArea < strikeK * wEff * 0.5) { dropSelfOverlap++; return false; }
                        boundaryTrim++;

                        // 【裁完不停，把幅本身缩到环内那一段接着推】
                        //
                        // 裁下来的那段本身没问题 —— 它还在环内，凭什么出完一带就停？
                        // 把幅的两条源轨换成裁过的，下一带从这段继续外推，
                        // 直到它自己也压出环（那时再裁一次，或者短到撑不起条带为止）。
                        // 走向下限那道闸门保证它不会一直缩下去。
                        pn.Crest0 = crestK;
                        pn.Toe0 = toeK;
                        // 窗口按一带取 —— 同上，带数上限不该改变第 1 带的几何
                        pn.Dir = AdvanceDirs(crestK, toeK, Math.Max(W, 2.0));

                        // 【推进闸门的分母只能变小，不能变大】
                        //
                        // 这里一度写的是 pn.CrestLen = strikeK —— 而 strikeK 是【本带】的轨长，
                        // 外凸拐角处越往外推越长。闸门是"推进 ≤ 源线长 × 2"，
                        // 分母跟着涨的话就成了**越扇越放行**，自己把自己废了：
                        // 实测东侧 55m/47m/44m 的碎段一路推到第 67 带（2680m），
                        // 铺出占总库容 49% 的一大片 —— 而那片是自然地形。
                        //
                        // 裁过之后幅只会更短，取小的那个才对。
                        pn.CrestLen = Math.Min(pn.CrestLen, strikeK);
                    }
                }


                // 【撞上别的幅就停】同一级各幅并肩往外推，推到别人已经占了的地就该停 ——
                // 再往外那块地已经是别人的位置了，出下去就是同一方土进两个位置的账。
                if (opt.MaxClaimedOverlap > 1e-6)
                {
                    if (!claimed.TryGetValue(b.LevelIndex, out var grid))
                        claimed[b.LevelIndex] = grid = new HashSet<long>();
                    // 【步长必须跟着 W 缩放，不能有绝对下限】相邻两带天然共一条边，
                    // 共边在栅格上占的比例 = 1 / (W/步长)。先前写的是 max(1.0, W/8)：
                    // W=40 时是 W/8（共边占 12.5%，勉强在阈值下），W=4 时那个 1 米下限
                    // 把带压成 4 格宽，共边一下占到 25% —— 每一带都被自己的前一带判成"撞车"，
                    // 整幅只出得来一带。判据跟参数量级挂钩，那就不是判据。
                    // 取 W/16：共边占 6.25%，离 0.18 有一倍余量，与 W 无关。
                    var ks = PlanCellKeys(crestK, backLine, Math.Max(0.05, W / 16.0));
                    if (ks.Count > 0)
                    {
                        int hit = 0;
                        foreach (var key in ks) if (grid.Contains(key)) hit++;
                        if ((double)hit / ks.Count > opt.MaxClaimedOverlap)
                        {
                            // 【只让出撞上的那一段，别把整带都扔了】
                            //
                            // 原先是撞上就整幅停 —— 这一带里没被占的部分跟着一起没了。
                            // 我一度以为"裁带"要多边形布尔所以放弃，那个判断是错的：
                            // 沿走向找"哪一段被占了"是【一维划分】，逐点问占位栅格就行。
                            //
                            // 逐点看"本点到后界的中点"落在哪个格子：占了就是堵，没占就是通。
                            // 取最长的一段连续"通"，把三条轨都按下标裁到它，照常出体，然后停。
                            int nPt = crestK.Length / 3;
                            var free = new bool[nPt];
                            double gs = Math.Max(0.05, W / 16.0);
                            // 【前脸第 i 点对应后界第几点，要问 OffsetRail】圆角插点后不再一一对应；
                            // 原先写的 Math.Min(i, 后界点数−1) 看着像限幅，插了点之后根本不触发，
                            // 只是拿错点去算中点 —— 于是问的是别处的格子占没占。
                            var vmap = new int[nPt];
                            OffsetRail(crestK, AdvanceDirs(crestK, toeK, Math.Max(W, 2.0)), wEff,
                                       out _, null, vmap);
                            for (int i = 0; i < nPt; i++)
                            {
                                int j = Math.Min(vmap[i], backLine.Length / 3 - 1);
                                double mx = (crestK[i * 3] + backLine[j * 3]) * 0.5;
                                double my = (crestK[i * 3 + 1] + backLine[j * 3 + 1]) * 0.5;
                                long key = ((long)(int)Math.Floor(mx / gs) << 32)
                                         ^ (uint)(int)Math.Floor(my / gs);
                                free[i] = !grid.Contains(key);
                            }
                            int bi = -1, bl2 = 0, s2 = -1;
                            for (int i = 0; i <= nPt; i++)
                            {
                                bool ok = i < nPt && free[i];
                                if (ok) { if (s2 < 0) s2 = i; }
                                else if (s2 >= 0)
                                { if (i - s2 > bl2) { bl2 = i - s2; bi = s2; } s2 = -1; }
                            }
                            if (bi < 0 || bl2 < 2) { collidedStop++; return false; }

                            var cFree = SliceByIndex(crestK, bi, bi + bl2 - 1);
                            var tFree = SliceByIndex(toeK, bi, bi + bl2 - 1);
                            var bFree = SliceByIndex(backLine, bi,
                                                     Math.Min(backLine.Length / 3 - 1, bi + bl2 - 1));
                            if (cFree.Length < 6 || tFree.Length < 6 || bFree.Length < 6 ||
                                PlanLength(cFree) < strikeFloor)
                            { collidedStop++; return false; }

                            // 【后界必须按【裁过的】前脸重算，不能用整带偏移的切片】
                            // 内核只拿到本格自己的两条轨 + W，扫出来的后界就是这条轨自己外扩 W；
                            // 而整带的后界带着相邻段的上下文，两端会 splay。
                            // 实测直接用切片：4 个位置的内核体积掉到清单的 78%。
                            // 这跟带内再切那处是同一类错，见 CentroidExact 上面那段。
                            crestK = cFree; toeK = tFree;
                            var dFree = AdvanceDirs(crestK, toeK, Math.Max(W, 2.0));
                            backLine = OffsetRail(crestK, dFree, wEff);
                            if (backLine.Length < 6) { collidedStop++; return false; }
                            strikeK = PlanLength(crestK);
                            planArea = PolygonAreaXY(crestK, backLine);
                            if (planArea <= 1e-6) { collidedStop++; return false; }
                            // 裁完再过一遍自重叠闸门（同边界裁切那处，原因见那里）
                            if (planArea < strikeK * wEff * 0.5) { dropSelfOverlap++; return false; }
                            ks = PlanCellKeys(crestK, backLine, gs);
                            collidedTrim++;
                            foreach (var key in ks) grid.Add(key);
                            // 【撞车裁完就停，不接着推】与边界那处不同：
                            // 边界是【外部约束】—— 裁掉出界那段，剩下的完全没问题；
                            // 撞车和折回是【内部几何冲突】—— 接着推容易再撞、再折，越推越碎。
                            // 实测三处全接着推：2103 个 / 13834 万；只有边界接着推：2160 个 / 14062 万。
                            collidedStopAfterTrim = true;
                        }
                        else foreach (var key in ks) grid.Add(key);
                    }
                }

                // 【楔子按前脸的真实平面投影面积扣，不按中位坡面投影】
                //
                // 体是"平面面积 × 台阶高"减掉坡面前那个楔子。楔子的平面足迹就是
                // 坡顶轨与坡底轨之间那条带，截面是底 run、高 H 的三角形 —— 所以
                //     楔子体积 = ∫ run(s)·H/2 ds = (H/2) × 【坡顶轨与坡底轨围成的平面面积】
                // 直接量那块面积就行，不必先取一个 run 的代表值。
                //
                // 【为什么中位数不行】先前折减因子是 (W − faceRun/2)/W，faceRun 取的是
                // 逐点间距的【中位数】。坡面投影沿走向均匀时两者恒等；可第 1 带的前脸是
                // 从起伏地形上切出来的【真坡面】，间距沿走向能差好几倍，中位数代表不了。
                // 真实数据全量过内核抓到：1279 个位置里 **40 个**（3.1%）内核体积与清单差 >5%，
                // 最差只有清单的 **0.641**。拓扑全是干净的 —— 错的只有账。
                // 改成量面积之后是【构造上精确】：均匀间距时与旧式恒等，不均匀时才分家。
                double faceArea = PolygonAreaXY(crestK, toeK);
                double cap = Math.Max(0.0, planArea - 0.5 * faceArea) * H;

                // 【D2 判在带上，不只判在源线上】
                //
                // 分割长度 L 定的是【一个位置沿走向多长】。先前只拿它切源台阶线，
                // 可推进之后带会变长：源线上一处外凸拐角，推到第 k 带时那儿多出一段
                // 半径 k·W 的圆弧。实测最长的一带前脸轨是本幅源长的 28 倍 ——
                // 一个 2800m 长的"位置"，既派不出活，也不是用户填的 L=100m。
                //
                // 【为什么是切开而不是丢掉】那片地方是真能排的，扇形展开本来就是外凸鼻子
                // 往外推的正常形态。按扇张比截断会丢掉 42% 的库容 —— 丢的是真库容。
                // 切成 ⌈带长/L⌉ 个位置，库容一点不少，每个位置都回到 L 的尺度上。
                int nsub = opt.PanelLengthM > 1e-6
                    ? Math.Max(1, (int)Math.Ceiling(strikeK / opt.PanelLengthM - 1e-9))
                    : 1;
                // 【按下标切，不按弧长比例切】三条轨都是同一条 crest0 偏移出来的，
                // 圆弧点数只跟张角有关、与偏移量无关 —— 所以点数一致、逐点同序，
                // 同一个下标就是同一条推进射线。切点由前脸轨的累计长度定（每段 ≤ L）。
                //
                // 按【弧长比例】切是错的：拐角处前脸的弧半径是 front、后界是 back，
                // 同一个拐角在两条轨上占的弧长比例根本不一样 ——
                // front=40/back=80 的直角，弧占前脸 24%、占后界 39%。
                // 实测按比例切 26/400 个位置真体积与清单差 >5%，改逐点投影仍剩 8 个；
                // 按下标切是【构造上精确】，一个不剩。
                bool byIndex = nsub > 1
                               && crestK.Length == backLine.Length && crestK.Length == toeK.Length;
                int[] cut = byIndex ? ArcCutIndices(crestK, nsub) : System.Array.Empty<int>();
                if (byIndex) nsub = cut.Length - 1;

                for (int s = 0; s < nsub; s++)
                {
                    double[] cSub, tSub, bSub;
                    if (nsub <= 1) { cSub = crestK; tSub = toeK; bSub = backLine; }
                    else if (byIndex)
                    {
                        cSub = SliceByIndex(crestK, cut[s], cut[s + 1]);
                        tSub = SliceByIndex(toeK, cut[s], cut[s + 1]);
                        bSub = SliceByIndex(backLine, cut[s], cut[s + 1]);
                        if (cSub.Length < 6 || tSub.Length < 6 || bSub.Length < 6) continue;
                    }
                    else
                    {
                        // 兜底：点数万一对不上（不该发生），退回弧长切 + 逐点投影配后界
                        double s0 = (double)s / nsub, s1 = (double)(s + 1) / nsub;
                        int ns = Math.Max(4, crestK.Length / 3 / nsub + 2);
                        cSub = ResampleByArcRange(crestK, s0, s1, ns);
                        tSub = ResampleByArcRange(toeK, s0, s1, ns);
                        if (cSub.Length < 6 || tSub.Length < 6) continue;
                        bSub = ProjectOnto(cSub, backLine);
                        if (bSub.Length < 6) continue;
                    }
                    double strikeS = PlanLength(cSub);

                    // 【库容为 0 的位置不该出】坡面投影吃掉整个平面面积时（run 接近 2W，坡太缓），
                    // 楔子把体扣成零厚的鳍 —— 它排不进任何东西，却占着编号、进清单、进库、进排产，
                    // 还会在图上留一片看不见的面。默认的竖直断面轨碰不到（run≈0.5m）；
                    // 【图上带边线】那条来源实测出过 6 个。
                    double faceS0 = PolygonAreaXY(cSub, tSub);

                    // 【面积以内核实际会建出来的那个体为准】
                    //
                    // 内核 CarveDumpStripsByRails 只拿到【本格自己】的两条轨 + W，扫出来的
                    // 后界就是本格前脸按自己的方向外扩 W。而整带的后界轨 backLine 带着
                    // 相邻段的上下文：一条两端各接一个拐角的【直】段，真实后界是张开的。
                    // 实测 L9-P04-S04-02 只有 2 个点、前脸 66.3m 直段，整带口径给 3064 m²、
                    // 内核只能建出 2652 m² —— 差 15.5%，13/400 个位置因此报红。
                    //
                    // 谁对？两个都对，问的不是同一件事：整带口径答"这一段占了多少地方"，
                    // 内核答"这个位置的体有多大"。**清单是给排产按位置取用的，
                    // 只能以体为准** —— 否则同一方土在图上没有、在账上有。
                    // 切片边界那点张开的楔子不属于任何位置，如实不计。
                    double[] bUse = bSub;
                    if (nsub > 1)
                    {
                        var dSub = AdvanceDirs(cSub, tSub, Math.Max(W, 2.0));
                        bUse = OffsetRail(cSub, dSub, wEff);
                    }
                    double areaS = nsub == 1 ? planArea : PolygonAreaXY(cSub, bUse);
                    if (strikeS < strikeFloor || areaS <= 1e-6) continue;
                    if (areaS - 0.5 * faceS0 <= 1e-6) { dropFlatFace++; continue; }   // 见上：库容为 0 的不出

                    // 【自重叠闸门不能逐格再判一遍 —— 试过，误伤向内收的环】
                    //
                    // 想法是"带那层判过之后还会裁切、还会带内再切，判在出口才保险"。
                    // 可【向内】推进的弧，面积本来就该缩：半径 R 的环往内推 W，
                    // 面积比 = 1 − W/(2R)，R=40m 时正好 0.5 —— 合成算例上小半径那几级
                    // 整级被判死（第 4 级 30 幅只剩 1 幅），台架当场红。
                    // "缩得多"和"自重叠"在面积比上分不开，得靠别的量分。
                    // 裁切那两处已经各自复检过，够了。

                    // 【还要一条反向的：净面积不能远【大】于名义】
                    //
                    // 自重叠闸门管的是"面积太小"，可另一头同样有病：一段很短的轨上带个大拐角，
                    // 拐角处的圆弧把面积撑起来 —— 实测走向 23.9m、W 40m、116° 拐角的一格，
                    // 净面积是名义的 **3.79 倍**。那一格几乎全是扇形，不是条带；
                    // 内核从这么短的轨上建不出那个扇，方量只有清单的 0.708，质心也偏 34.6m。
                    //
                    // 【必须同时要求"轨还短"，不能只看面积比】
                    // 半径 R 的【环】外扩 W，面积比 = 1 + W/(2R) —— R=20m 时正好 2.0。
                    // 只看比值的话小半径的环整级被判死：合成算例上第 4 级 30 幅只剩 1 幅，台架当场红。
                    // 而环的走向长是 2πR ≥ 6R，永远远大于 W；真正有病的是那种
                    // "走向比 W 还短、却顶着个大拐角"的短桩（实测 23.9m / W 40m / 116°，比值 3.79）。
                    // 两条一起判：面积比 > 2 且走向 < 2W。
                    if (areaS > strikeS * wEff * 2.0 && strikeS < wEff * 2.0)
                    { dropFanDominated++; continue; }

                    // 【前脸不能在某一点上退化】竖直断面轨的坡顶/坡底本该处处隔着 ε。
                    // 真实数据上出现过某点两者重合（间距 0.00）—— 那一点的推进方向无定义，
                    // 偏移、质心、内核 loft 全跟着乱：实测那一格内核体积只有清单的 0.79、
                    // 质心偏 34.6m，而运距吃的就是质心。逐点量最小间距，退化的点不能进。
                    //
                    // 【但退化只在【某几个点】上，不该整格丢掉 —— 沿走向裁到好的那一段】
                    //
                    // 与撞车/折回/出界是同一类问题："沿走向哪一段不行"是【一维划分】，
                    // 逐点问判据、取最长的一段连续"好"、按下标裁 —— 见 [[one-dim-not-boolean]]。
                    // 原来是一个坏点判死一整幅：闭合台阶环（外排放坡造出来的就是环）在拐角与接缝处
                    // 方向估不出来，合成算例实测 16 个幅里判掉 5 个、**丢掉三成走向长**，
                    // 而丢弃清单上只写"前脸在某点退化 x 5"，看不出代价有多大。
                    double gate = EpsGapM(W) * 0.2;
                    int nQ = cSub.Length / 3;
                    var gapOk = new bool[nQ];
                    int nBadQ = 0;
                    for (int q = 0; q < nQ; q++)
                    {
                        gapOk[q] = PointToPolylineDistXY(tSub, cSub[q * 3], cSub[q * 3 + 1]) >= gate;
                        if (!gapOk[q]) nBadQ++;
                    }
                    if (nBadQ > 0)
                    {
                        int gi = LongestRun(gapOk, out int gl);
                        if (gi < 0 || gl < 2) { dropPinchedFace++; lostPinched += strikeS; continue; }

                        var cKeep = SliceByIndex(cSub, gi, gi + gl - 1);
                        var tKeep = SliceByIndex(tSub, gi, gi + gl - 1);
                        double strikeKeep = PlanLength(cKeep);
                        if (cKeep.Length < 6 || tKeep.Length < 6 || strikeKeep < strikeFloor)
                        { dropPinchedFace++; lostPinched += strikeS; continue; }

                        // 后界按裁过的前脸重算 —— 用整带偏移的切片会 splay（撞车那处栽过，
                        // 4 个位置的内核体积掉到清单的 78%）。见 [[prism-strip-centered-bug]]。
                        var dKeep = AdvanceDirs(cKeep, tKeep, Math.Max(W, 2.0));
                        var bKeep = OffsetRail(cKeep, dKeep, wEff);
                        double areaKeep = PolygonAreaXY(cKeep, bKeep);
                        if (bKeep.Length < 6 || areaKeep <= 1e-6 || areaKeep < strikeKeep * wEff * 0.5)
                        { dropPinchedFace++; lostPinched += strikeS; continue; }

                        pinchedTrim++;
                        lostPinched += Math.Max(0.0, strikeS - strikeKeep);
                        cSub = cKeep; tSub = tKeep; bUse = bKeep;
                        strikeS = strikeKeep; areaS = areaKeep;
                        faceS0 = PolygonAreaXY(cSub, tSub);
                    }

                    var cell = new Cell
                    {
                        LevelIndex = b.LevelIndex,
                        PanelIndex = panelSeq,     // 跨"急弯断开"的各段连续编号
                        PanelCount = panelCount,
                        StepIndex = k,
                        SubIndex = nsub > 1 ? s + 1 : 0,
                        SubCount = nsub,
                        CrestZ = b.CrestZ,
                        ToeZ = b.ToeZ,
                        CrestXyz = cSub,
                        ToeXyz = tSub,
                        StrikeLenM = strikeS,      // 前脸轨长
                        PlanAreaM2 = areaS,
                        StripWidthM = wEff,        // 夹窄的带如实报窄宽度，别拿名义 W 冒充
                        // D6 占容方 = 平面面积 × 台阶高 − 坡前楔子（楔子按前脸的真实平面投影量）
                        CapacityM3 = Math.Max(0.0, areaS - 0.5 * PolygonAreaXY(cSub, tSub)) * H,
                        FaceRunM = k == 1 ? faceRun : eps,
                        IsWorkingFace = k == 1,
                        IsClamped = clampedHere,
                        Code = $"{sitePrefix}-L{b.LevelIndex}-P{panelSeq:00}-S{k:00}"
                             + (nsub > 1 ? $"-{s + 1:00}" : ""),
                    };
                        // 质心用平面多边形的真形心 —— 运距吃它，粗近似在弯带上能差出一个带宽
                        (cell.Cx, cell.Cy, cell.Cz) = CentroidExact(cSub, bUse, b.CrestZ, b.ToeZ);
                    res.Cells.Add(cell);
                    emitted++;
                }

                // 【这一带一个格子都没出 → 这一幅到此为止】
                //
                // 带内那几道闸门（走向下限 / 库容为 0 / 扇形主导 / 前脸退化）跳过的是【子格】。
                // 若某一带的子格全被跳过、而下一带又照常出来，带号就断了 —— 实测出过 3 个幅有缺口。
                // "第 k+1 带"的含义是"从第 k 带的后界再往外一带"：第 k 带都不存在，
                // 第 k+1 带就没有立足点，排产也排不出先后。
                if (emitted == 0) return false;

                // 撞车裁过的带：这一段出完就停（见上面那段口径说明）

                if (collidedStopAfterTrim) { collidedStop++; return false; }


                // 夹窄了就说明再往外必自交 —— 出完这一条就停
                if (clampedHere && !opt.ContinueAfterClamp) { clampedStop++; return false; }
                if (clampedHere) pn.Reached = back;      // 下一带从夹紧处接着往外，不从 k·W 起
                if (k == maxSteps) truncatedByCap++;
            return true;
        }

        void Rec(string why, int c, double lost)
        { if (c > 0) res.Drops.Add(new Drop { Reason = why, Count = c, LostStrikeLenM = lost }); }
        double geoFloor = W * Math.Max(0.05, Math.Min(2.0, opt.MinStrikeRatio));
        Rec($"走向短于 {Math.Max(opt.MinStrikeLenM, geoFloor):0.#}m（业务闸门与几何下限取大）",
            dropShort, lostShort);

        // 【是"你设的闸门"在挡，还是"几何撑不住"在挡？——这两件事该分开说】
        //
        // 走向下限取 max(业务闸门, 几何下限)。业务闸门更大时，挡掉的那些【几何上是能出的】——
        // 那是一条经营决定（"多短的位置不值得派活"），不是算法的极限。
        // 现场追"做全"时该知道这一刀花了多少，以及松到哪儿为止就不能再松了。
        //
        // 实测这份数据（L=50 · W=40 · 8 带）：
        //   闸门 20m（几何下限 14m 被盖住）→ 2272 个位置 · 走向覆盖 81.9%
        //   闸门 ≤14m（几何下限接管）      → 2386 个位置 · 走向覆盖 85.4%   内核全绿
        // 【再往下就不行了】把几何下限本身从 0.35W 调到 0.25W：内核里 3 个位置的质心偏出 98m、
        // 4 个方量翻倍；0.15W 则是 15 个和 18 个。0.35W 正卡在悬崖边上，别动它。
        if (opt.MinStrikeLenM > geoFloor + 1e-6 && lostShort > 1e-6)
            res.Notes.Add($"「最短幅长」{opt.MinStrikeLenM:0.#}m 高于几何下限 {geoFloor:0.#}m"
                        + $"（=0.35×W）—— 挡掉的 {lostShort:N0} m 走向里，有一部分几何上本来出得来，"
                        + "是按\"多短的位置不值得派一次活\"挡掉的。要更全就把它降到几何下限；"
                        + "**几何下限本身不要再往下调**，那以下的位置内核建出来会偏位、方量翻倍。");
        Rec("台阶壳子退化（点数不足 / 台阶高为 0 / 线长为 0）", dropDegenerate, 0);
        // 【这一条不是"丢弃"，是"只能靠夹窄"】—— 它们照常成幅，只是排在主推进面后面推。
        // 真死在哪一步由那一步记账（实测绝大多数死在"走向短于 XXm"：急弯段平均只有 11 m）。
        if (splitCut > 0)
            res.Notes.Add($"有 {splitCut} 段台阶线落在急弯上、合计 {lostSplit:N0} m"
                        + $"（按一个条带宽推过去会自交，探测距离 {probeStepsUsed}×W）—— "
                        + "这些段不再整段丢掉，而是排在主推进面【之后】靠夹窄出窄带；"
                        + "推不动的会各自出现在下面的丢弃理由里，不在这儿重复计账。");
        Rec("坡面投影超上限（坡顶/坡底没配上，出体会是横跨全图的扇面）", dropWideFace, lostWideFace);
        Rec("推进出区域环：让出出界那一段、只出环内的部分", boundaryTrim, 0);
        Rec("推进到排土场边界，后续带不出", truncatedByBoundary, 0);
        Rec("撞上同级别的幅已占的地：让出那一段、只出没被占的部分", collidedTrim, 0);
        Rec("推进撞上同级别的幅已占的地，后续带不出（同一方土不进两本账）", collidedStop, 0);
        Rec($"推进已达源台阶线自身长度的 {opt.MaxReachRatio:0.##} 倍，再往外形状由拐角圆弧决定", truncatedByReach, 0);
        Rec("凹弯折回：只在没折回的那一段上出全宽（比整条夹窄更划算时）", foldTrim, 0);
        Rec("凹弯太急，连最小有效宽都推不动（真的没有空间）", foldedStop, 0);
        Rec("前脸在某点退化（坡顶坡底重合，推进方向无定义）", dropPinchedFace, lostPinched);
        Rec("前脸局部退化：让出退化的那一段、只出好的部分", pinchedTrim, 0);
        Rec("扇形主导：净面积超过名义两倍（短轨上一个大拐角，不是条带）", dropFanDominated, 0);
        Rec("前后脸缠在一起（净面积不足名义的三成），后续带不出", dropSelfOverlap, 0);
        if (splitRuns > 0)
            res.Notes.Add($"台阶线上有 {splitRuns} 处急弯，已在那里断开分幅 —— "
                        + "两侧各自成幅继续推，而不是整条丢掉（现场对这种线的理解也是"
                        + "\"有个尖内角，那是两个工作面\"）。");
        // 【幅与幅之间留没留兜，得报给人看】
        //
        // "带号 1..N 连续"只保证【一幅之内】不缺，保证不了【幅与幅之间】不留兜 ——
        // 四周都是位置、自己没被派到的那种地。它是逐幅偏移的结构性残留（相邻幅推进速度不同、
        // 凹弯处推不动），算法治不干净，但**不能让它只活在离线台架里**：
        // 界面上一个字都没有的话，现场既不知道有兜、也不知道换个 L/W 能不能少一点。
        //
        // 占位栅格里本来就有这个信息：从格网外缘漫水，漫不到又没被认领的格子就是兜。
        {
            // 【要用真正出了体的格子重画一张网，不能借推进时那张占位栅格】
            // 占位栅格是按【整带】认领的，而带内被走向下限过滤掉的碎片并没有出体 ——
            // 借它算的话那些地方算"已覆盖"，兜就少报了。实测两者差 3 倍
            //（借占位栅格 10 处 / 1.8 万 m²，按出体格子 223 处 / 6.2 万 m²）。
            // 【这张网的步长不跟着 W 缩 —— 它是诊断，不是判据】
            //
            // 撞车用的那张栅格必须是 W/16（正确性：相邻带共边占比得与 W 无关，
            // 早先写死 1.0m 时 W=4 直接把每带都判成撞车）。但留兜报告只是报个面积，
            // 2m 分辨率足够。跟着 W 缩的话代价是 O(1/W³)：实测 W=40 用 1.9 秒、
            // W=20 要 19.7 秒、W=10 要 **115.8 秒** —— 界面僵近两分钟，
            // 而多出来的时间全花在把一个诊断算得更精确上。
            // 【推进已经结束，占位栅格可以放掉了】
            //
            // 它是千万级 key 的 HashSet：实测整进程峰值 W=40 203MB / W=20 481MB / W=10 651MB，
            // 大头就是它。**放掉它并不降低峰值** —— 峰值出在推进过程中，那时它正满着；
            // 实测清理前后峰值一样（207MB / 675MB）。
            // 真正的收益在【之后】：Plan() 返回后调用方要建上千个体，
            // 那时再攥着 400MB 已经没人读的格子纯属占地方。
            claimed.Clear();

            double step = Math.Max(2.0, W / 16.0);
            double cellA = step * step;
            double holeA = 0; int holeN = 0;
            var covered = new Dictionary<int, HashSet<long>>();
            foreach (var c in res.Cells)
            {
                if (c.CrestXyz.Length < 6 || c.ToeXyz.Length < 6) continue;
                var dSub = AdvanceDirs(c.CrestXyz, c.ToeXyz, Math.Max(c.StripWidthM, 2.0));
                var back = OffsetRail(c.CrestXyz, dSub, c.StripWidthM);
                if (!covered.TryGetValue(c.LevelIndex, out var g))
                    covered[c.LevelIndex] = g = new HashSet<long>();
                foreach (var key in PlanCellKeys(c.CrestXyz, back, step)) g.Add(key);
            }
            foreach (var kv in covered)
            {
                var (a, n) = CountEnclosedGaps(kv.Value);
                holeA += a * cellA; holeN += n;
            }
            if (holeN > 0)
                res.Notes.Add($"幅与幅之间留了 {holeN} 处兜、合计 {holeA:N0} m²"
                            + $"（约占已覆盖面积的 {(holeA / Math.Max(1.0, holeA + res.Cells.Sum(c => c.PlanAreaM2)) * 100):0.0#}%）"
                            + " —— 四周都是位置、自己没被派到的地。相邻幅推进快慢不同、凹弯处推不动都会留兜，"
                            + "换小一点的条带宽度 W 通常能少一些。");
        }

        // 【第 1 带的坡前楔子，图上不会被扣掉 —— 得说清楚】
        //
        // 第 1 带的前脸按设计是【真坡面】(坡顶↔坡底)，清单里扣了楔子 ½·run·H；
        // 而软件建体走棱柱造法（usePrism），棱柱的前脸是**竖直**的，建出来是整个盒子。
        //
        // 从现状面切带那条默认路径不受影响：轨是"坡顶线处竖直断面"，坡顶坡底只隔 ε=0.5m，
        // 楔子可忽略（真实数据 1665 个位置方量差 >5% 的是 0 个）。
        // 但换成【图上已有的排土台阶线】那条来源，run 就是真实坡面投影 ——
        // 合成算例实测 run=21.4m 时第 1 带的内核体积比清单大 37%。
        //
        // 账面不会错（对话框会把内核实测体积回填进清单），但第 1 带的库容会偏大一个楔子。
        // 这里按实际数据算出偏差量报出来，让人自己判断要不要换来源。
        {
            double a1 = 0, wedge1 = 0;
            foreach (var c in res.Cells)
            {
                if (!c.IsWorkingFace) continue;
                a1 += c.PlanAreaM2;
                wedge1 += 0.5 * PolygonAreaXY(c.CrestXyz, c.ToeXyz);
            }
            if (a1 > 1e-6 && wedge1 / a1 > 0.02)
                res.Notes.Add($"第 1 带的坡前楔子占其平面面积的 {(wedge1 / a1 * 100):0.#}% —— "
                            + "清单里扣掉了，但图上建体走的是竖直前脸的棱柱造法，**不会扣**。"
                            + "落库以内核实测体积为准时，第 1 带库容会偏大这一块。"
                            + "从现状面切带（默认来源）不会有这个问题：那条路的前脸本来就是竖直断面。");
        }

        Rec("坡太缓：坡面投影吃掉整个平面面积，库容为 0（排不进东西的位置不出）", dropFlatFace, 0);

        Rec("凹弯处推进被夹窄后停止（已出一条窄带，不是丢弃）", clampedStop, 0);
        // 【"触顶"该不该报警，看它是【工作值】还是【兜底】】
        //
        // 「每级最多推进带数」是用户填的工作值 —— 填 1 就是"只要当前工作面那一带"，
        // 推到 1 带触顶是**正常完成**，不是异常。原先这条一律附上
        // "检查条带宽度 W 是不是填小了一个量级"，用户填 1 时 189 个幅全被这么数落一遍。
        // 只有填得很大却仍然触顶，才真的是 W 填错了量级。
        Rec(maxSteps <= 12
            ? $"推进到你设的带数上限（{maxSteps} 带 = {maxSteps * W:0} m）—— 按参数正常收尾，不是异常"
            : $"推进带数触顶（上限 {maxSteps}）—— 检查条带宽度 W 是不是填小了一个量级",
            truncatedByCap, 0);

        // ── 工作帮范围：本期实际排弃的那一片。按【格质心】过滤 ──────────────
        //
        // 放在最后而不是推带过程中，是因为它**不该改变几何**：推带的止点是排土场区域环，
        // 工作帮只回答"这一格算不算本期的"。混进推带循环会让"能推到哪儿"跟着本期范围变，
        // 下个月换一片工作帮，同一个位置的库容就变了 —— 那是几何跟着计划漂。
        //
        // ★ 编号与 PanelCount **一律不重排**：UnitId 是位置的身份（落库唯一索引、排产取用、
        //   图上挂属性都靠它）。按本期范围重编号的话，同一个位置在不同工作帮下就是两个东西。
        //   所以 P 号有断档是**对的**，`PanelCount` 仍是这一级几何上的总幅数。
        int outsideWorkSlope = 0;
        if (opt.WorkSlopeRingXy is { Length: >= 6 })
        {
            double lostOutside = 0;
            var kept = new List<Cell>(res.Cells.Count);
            foreach (var c in res.Cells)
            {
                if (c != null && WorkSlopeRange.Contains(opt.WorkSlopeRingXy, c.Cx, c.Cy)) { kept.Add(c); continue; }
                outsideWorkSlope++;
                lostOutside += c?.StrikeLenM ?? 0;
            }
            res.Cells.Clear();
            res.Cells.AddRange(kept);
            if (outsideWorkSlope > 0)
                res.Drops.Add(new Drop
                {
                    Reason = "在工作帮范围外（本期不排这一片；编号与幅数未重排，P 号断档是对的）",
                    Count = outsideWorkSlope,
                    LostStrikeLenM = lostOutside,
                });
            else
                res.Notes.Add("◆ 给了工作帮范围，但一个位置都没挡下 —— 它没起到收窄作用"
                            + "（多半圈得比排土场还大）。");
        }

        if (res.Cells.Count == 0)
        {
            // ★ 给了工作帮范围时必须换一句话：圈错地方的表现与"台阶壳子本身就空"完全相同，
            //   而下面那句原话会把人引去查台阶线。
            res.Message = outsideWorkSlope > 0
                ? $"工作帮范围内一个潜在排土位置都没有 —— 范围外有 {outsideWorkSlope} 个被挡下，"
                  + "**先核对工作帮范围圈在哪儿**：查台阶线没有用，位置是切出来了的。"
                : "切完一个潜在排土位置都没出来。"
                  + (res.Drops.Count > 0
                      ? "原因：" + string.Join("；", res.Drops.Select(d => $"{d.Reason} {d.Count} 处"))
                      : "多半是台阶壳子本身就空。");
            return res;
        }

        res.Ok = true;
        var per = res.PerLevel().ToList();
        res.Message = $"{res.Cells.Count} 个潜在排土位置，覆盖 {res.LevelCount} 级排土台阶，"
                    + $"合计库容 {res.TotalCapacityM3:N0} m³（占容方）。\n"
                    + $"（长 ≤{opt.PanelLengthM:0} m × 宽 {W:0} m × 高 台阶高）\n"
                    + string.Join("\n", per.Select(x =>
                        $"  第 {x.Level} 级 {x.Cells} 个位置，{x.CapacityM3:N0} m³"));
        if (res.Drops.Count > 0)
            res.Message += "\n" + string.Join("；", res.Drops.Select(d =>
                $"{d.Reason} {d.Count} 处" + (d.LostStrikeLenM > 0 ? $"（{d.LostStrikeLenM:N0} m）" : "")));
        return res;
    }

    // ── 几何 ────────────────────────────────────────────────────────

    /// <summary>
    /// 【急弯断点】把一条台阶轨按"推得动 / 推不动"切成若干段，返回各段的归一化弧长区间。
    ///
    /// 做法：按最远推进距离把整条轨外扩一遍，逐段查折回；折回的那些段（连同左右各一段的余量）
    /// 挖掉，剩下的连续区间就是"能推"的段。
    ///
    /// 【为什么值得做】一个幅里往往只有【一处】尖内角，其余部分完全推得动。
    /// 整幅丢掉的话，排土场上那一片就永远没有位置 —— 实测 44 个幅是这么整片丢掉的。
    /// 现场对这种台阶线的理解本来也是"有个尖内角，那是两个工作面"。
    ///
    /// 全程推得动就返回一个 [0,1]，不改变原行为。
    /// </summary>
    public static List<(double T0, double T1, bool Blocked)> SplitAtFoldPoints(
        double[] crest, double[] toe, double stripWidthM, int maxSteps, double badLimit = 0.5, int probeStepsHint = 1)
    {
        var outRuns = new List<(double, double, bool)>();
        int n = crest.Length / 3;
        if (n < 3) { outRuns.Add((0.0, 1.0, false)); return outRuns; }

        double far = stripWidthM * Math.Max(1, maxSteps);
        var dirs = AdvanceDirs(crest, toe, far);

        // 按【一个 W】外扩查折回：那是每一带都要跨过的最小步长，这一步都折回的地方推不动。
        //
        // 【逐段的折回标记必须让 OffsetRail 回填，不能自己拿下标去减】
        // 原先这里写的是 `moved[(i+1)*3] - moved[i*3]` —— 假定偏移轨与原轨点数一一对应。
        // 可圆角接头会在拐角处插点（弧），偏移轨比原轨长，`moved[i]` 根本不是原轨第 i 个顶点。
        // 于是标坏的段与真正折回的段【错位】，挖掉的是别处的台阶线。
        // 拐角越多错得越远，而这正是"哪里该断开"唯一的依据。
        int m = n - 1;
        var badSeg = new bool[m];
        OffsetRail(crest, dirs, stripWidthM, out int nbad, badSeg);
        if (nbad == 0) { outRuns.Add((0.0, 1.0, false)); return outRuns; }

        // 【只在局部尖内角处断开，整条都紧的不断】
        //
        // 一条【均匀急弯】的台阶线（比如半径 50m 的凹弧、推 40m）每一段都会被判折回 ——
        // 那时断开会把整条切没，一个位置都不出，比夹紧还差。
        // 而夹紧对这种线恰恰有效：整条按同一个更小的宽度推出去就是了。
        // 断开是给"其余部分推得动、只有一处推不动"的情形准备的。
        if (nbad > m * badLimit) { outRuns.Add((0.0, 1.0, false)); return outRuns; }

        // 坏段左右各扩一段余量 —— 折回是渐变的，紧贴坏段的邻段多半也已经很挤
        var block = new bool[m];
        // 【余量只在探测距离 > 一带时才加】
        //
        // 原先无条件把坏段左右各扩一段（"折回是渐变的，紧贴坏段的邻段多半也已经很挤"）。
        // 探得比推得远时这条成立；可探测距离已经收成 min(设定, 实际带数) 之后，
        // 探的就是【本带真会走到的那个距离】—— 邻段挤不挤，判据自己就答了，不必再加保险。
        // 而余量的代价是实打实的：每处急弯多挖掉两段台阶线，图上就是一截截空档。
        //
        // 【为什么不干脆让坏段也成幅】试过，更糟：坏段出的窄带会【占地】，
        // 同级的好幅推进时撞上它就停，在同心环上层层连锁 ——
        // 合成算例第 4 级从 39 幅掉到 2 幅，台架当场红。
        int margin = probeStepsHint > 1 ? 1 : 0;
        for (int i = 0; i < m; i++)
            if (badSeg[i])
                for (int j = Math.Max(0, i - margin); j <= Math.Min(m - 1, i + margin); j++) block[j] = true;

        // 段索引 → 归一化弧长
        var acc = new double[n];
        for (int i = 1; i < n; i++)
        {
            double dx = crest[i * 3] - crest[(i - 1) * 3], dy = crest[i * 3 + 1] - crest[(i - 1) * 3 + 1];
            acc[i] = acc[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = acc[n - 1];
        if (total <= 1e-9) { outRuns.Add((0.0, 1.0, false)); return outRuns; }

        // 【好段坏段都返回，坏段带着 Blocked 标记】
        //
        // 原先只返回能推的段，坏段连同它下面那截台阶线一起没了、且不进任何丢弃计数 ——
        // 这是本函数唯一会【无声丢地】的地方，真实数据上一轮 12 处、共 2039 m。
        //
        // 【为什么现在敢让坏段也成幅了 —— 前提变了】
        // 早先试过一次，更糟：坏段出的窄带会【占地】，同级的好幅推进时撞上它就停，
        // 在同心环上层层连锁，合成算例第 4 级从 39 幅掉到 2 幅。当时的两个前提如今都不成立：
        //   · 那时探测距离是 5W —— "坏"意味着"第 5 带会折回"，好端端的段被判成坏段，
        //     于是大量假坏段出窄带去占地。现在探 1W，坏 = 连第 1 带都推不动，数量少一个量级。
        //   · 那时没有先后：坏段的幅与主推进面**平起平坐**地抢地。现在坏段标成 Stub，
        //     带序循环里**永远排在主推进面后面**（与同级壳子按线长降序是同一条道理）。
        // 前提变了就该重测 —— 这一条正是 [[iterate-by-rules-not-patches]] 说的"回头看补丁"。
        int s = 0;
        while (s < m)
        {
            int e = s;
            bool blk = block[s];
            while (e < m && block[e] == blk) e++;
            double t0 = acc[s] / total, t1 = acc[e] / total;
            if (t1 - t0 > 1e-6) outRuns.Add((t0, t1, blk));
            s = e;
        }
        return outRuns;
    }

    /// <summary>
    /// 把坡底轨对齐到坡顶轨：方向一致 + （闭合环）起点对应。
    ///
    /// 【为什么必须在这里做】内核按归一化弧长把两条线配起来（CarveStrip.cpp 的 lerpAt），
    /// 它不管方向也不管起点。一条顺时针一条逆时针 → 坡顶的头配上坡底的尾，loft 出来是麻花；
    /// 闭合环起点错开半圈 → 体横穿整个排土场。两种都不是"几何退化"，是喂进去的对应关系就错了。
    ///
    /// 判据分闭合 / 开口两套：
    ///   · 闭合环：比有向面积的符号（XY 叉积和）。同号 = 同向。这对同心环最稳 ——
    ///     排土台阶线正是一圈套一圈的同心环。再把坡底环旋转到"离坡顶首点最近"的那一点起头。
    ///   · 开口线：比 |c0−t0|+|cN−tN| 与 |c0−tN|+|cN−t0|，后者小则坡底反向。
    ///     首末两端一起比，比只比一端稳 —— 一端擦身而过的情况骗不过两端。
    /// </summary>
    public static double[] AlignToeToCrest(double[] crest, double[] toe,
                                           bool? crestClosedHint = null, bool? toeClosedHint = null)
    {
        int nc = crest.Length / 3, nt = toe.Length / 3;
        if (nc < 2 || nt < 2) return toe;

        bool crestClosed = crestClosedHint ?? IsClosed(crest);
        bool toeClosed = toeClosedHint ?? IsClosed(toe);
        var t = (double[])toe.Clone();

        if (crestClosed && toeClosed)
        {
            if (Math.Sign(SignedAreaXY(crest)) != Math.Sign(SignedAreaXY(t)) && Math.Abs(SignedAreaXY(t)) > 1e-9)
                t = Reverse(t);

            // 旋转到离坡顶首点最近的那一点起头
            double cx0 = crest[0], cy0 = crest[1];
            int best = 0; double bestD = double.MaxValue;
            int n = t.Length / 3;
            for (int i = 0; i < n; i++)
            {
                double dx = t[i * 3] - cx0, dy = t[i * 3 + 1] - cy0;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best > 0) t = Rotate(t, best);
        }
        else
        {
            double d00 = Dist2XY(crest, 0, t, 0) + Dist2XY(crest, nc - 1, t, nt - 1);
            double d0N = Dist2XY(crest, 0, t, nt - 1) + Dist2XY(crest, nc - 1, t, 0);
            if (d0N < d00) t = Reverse(t);
        }
        return t;
    }

    /// <summary>
    /// 逐点推进方向：垂直走向的水平单位向量，指【坡脚 / 外】。
    /// 与内核 CarveStrip 同口径 —— 那边先统一指坡顶，isDump 再翻一次；这里一步到位指坡脚。
    /// </summary>
    /// <param name="advanceWindowM">
    /// 求走向切向的【窗口宽度】（m）。0 = 用相邻两点（±1）。
    ///
    /// 【为什么不能用相邻两点】实测台阶线上全是小尺度抖动。拿相邻两点算切向，凹处的推进方向
    /// 发散剧烈，偏移一个 W 后脸就翻过来 —— 真实数据上 228 个幅里 154 个推不到 2 带就被
    /// 折回拦下，位置数只出到 399（上限 1140）。
    ///
    /// 正解是【偏置距离多大，切向就在多大的尺度上看】：窗口取 ~W 米，急弯被摊平。
    /// 内核 <c>BuildMiningRailSegments</c> 的 advanceWindowM 就是干这个的，
    /// 而本方法先前把那份平滑丢掉、又自己按 ±1 重算了一遍，等于把问题放了回来。
    /// </param>
    // 【窗口宽窄在本功能里其实不影响结果 —— 扫过】0.2W ~ 80W 逐档量，位置数/库容/覆盖/重叠/留兜
    // 五项**一个数都不动**。原因在下游：OffsetRail 只拿这个方向定【法向的正负号】
    //（sgn = dot(边法向, dir) < 0 ? -1 : 1），真正的偏移用的是逐边法向 en[i]。
    // 只要窗口不把方向指反，宽窄就没有下文。SplitAtFoldPoints 里也是同样的用法。
    // 记在这里省得以后有人像我一样，因为"maxSteps 从 60 提到 80 把窗口从 2400m 撑到 3200m"
    // 而去担心它 —— 那个担心是多余的。
    public static (double X, double Y)[] AdvanceDirs(double[] crest, double[] toe, double advanceWindowM = 40.0)
    {
        int n = crest.Length / 3;
        var dirs = new (double, double)[n];

        // 窗口宽度换算成点数：量本轨的中位点距
        int half = 1;
        if (advanceWindowM > 1e-6 && n >= 3)
        {
            var steps = new List<double>(n - 1);
            for (int i = 1; i < n; i++)
            {
                double px = crest[i * 3] - crest[(i - 1) * 3], py = crest[i * 3 + 1] - crest[(i - 1) * 3 + 1];
                steps.Add(Math.Sqrt(px * px + py * py));
            }
            steps.Sort();
            double med = steps[steps.Count / 2];
            if (med > 1e-6) half = Math.Max(1, Math.Min(Math.Max(1, n / 2), (int)Math.Round(advanceWindowM / med)));
        }

        for (int i = 0; i < n; i++)
        {
            // 走向切向在【宽窗口】上取（端点自动收窄），不是相邻两点的连线
            int a = Math.Max(0, i - half), b = Math.Min(n - 1, i + half);
            if (a == b) { a = Math.Max(0, i - 1); b = Math.Min(n - 1, i + 1); }
            double sx = crest[b * 3] - crest[a * 3], sy = crest[b * 3 + 1] - crest[a * 3 + 1];
            double sl = Math.Sqrt(sx * sx + sy * sy);
            double dx, dy;
            if (sl > 1e-9) { dx = -sy / sl; dy = sx / sl; }     // 垂直走向
            else { dx = 1; dy = 0; }

            // 定正负号：指向坡脚（坡底线那一侧）
            int j = Math.Min(i, toe.Length / 3 - 1);
            double vx = toe[j * 3] - crest[i * 3], vy = toe[j * 3 + 1] - crest[i * 3 + 1];
            if (dx * vx + dy * vy < 0) { dx = -dx; dy = -dy; }
            dirs[i] = (dx, dy);
        }
        return dirs;
    }

    /// <summary>
    /// 第 2 带起前脸留的那条极窄的缝（m）。
    ///
    /// 【为什么不让前脸严格竖直】内核靠「坡顶线在坡底线的哪一侧」定推进方向的正负号
    /// （CarveStrip.cpp 里 <c>d·(crest − tp) &lt; 0</c> 那句）。两条轨 XY 完全重合时这个点积恒为 0，
    /// 符号退化成只由坡顶线的【绕向】决定 —— 顺时针画的环和逆时针画的环会往相反方向推，
    /// 而绕向是画图的人随手定的，不该决定排土往哪边堆。留一条 ε 缝，判据就有确定答案。
    ///
    /// ε 取 W 的 2%、上限 0.5m：坡面角 88° 以上，扣掉的楔子不到 1%，而且在容量里如实扣，
    /// 不是抹掉。
    /// </summary>
    public static double EpsGapM(double stripWidthM) => Math.Min(0.5, Math.Max(0.02, stripWidthM * 0.02));

    /// <summary>
    /// 坡面水平投影 run（m）= 坡顶轨各点到【坡底折线】的垂距中位数。
    ///
    /// 【不能用"同弧长对应点"的距离】两条轨是按归一化弧长配起来的，而同心环的内外周长不同 ——
    /// 直边上对得齐，拐角处对应点会飘开。合成算例上实测：真实垂距 21.4m（15m/tan35°），
    /// 按对应点距量出来 34.8m，虚高 63%。这个数直接进第 1 带的容量
    /// （<c>W − run/2</c>），量错就是库容错。
    ///
    /// 取【中位数】而不是均值：个别点落在拐角外侧会给出离谱值，中位数不被带偏 ——
    /// 与 <see cref="StandardLevelModel"/> 判配对用的 <c>MedianDistanceXY</c> 同一口径。
    /// </summary>
    public static double MeanGapXY(double[] crest, double[] toe)
    {
        int n = crest.Length / 3;
        if (n == 0 || toe.Length < 6) return 0;
        var ds = new List<double>(n);
        for (int i = 0; i < n; i++)
            ds.Add(PointToPolylineDistXY(toe, crest[i * 3], crest[i * 3 + 1]));
        if (ds.Count == 0) return 0;
        ds.Sort();
        int m = ds.Count / 2;
        return ds.Count % 2 == 1 ? ds[m] : (ds[m - 1] + ds[m]) * 0.5;
    }

    /// <summary>
    /// 把一条轨的每个点【投影】到另一条折线上取最近点（XY 最近，Z 取该处折线的插值）。
    ///
    /// 这是"两条轨怎么对应"的正解：局部成对由构造保证，跟两条线的总长、点数、形状差多少都无关。
    /// 按弧长比例对应只在两条线【近似平行且等分】时才成立 —— 同心环成立，
    /// 从起伏地形上切出来的带不成立。
    /// </summary>
    public static double[] ProjectOnto(double[] src, double[] target)
    {
        int n = src.Length / 3, m = target.Length / 3;
        if (n == 0 || m < 2) return Array.Empty<double>();
        var o = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            double px = src[i * 3], py = src[i * 3 + 1];
            double best = double.MaxValue, bx = target[0], by = target[1], bz = target[2];
            for (int k = 1; k < m; k++)
            {
                double ax = target[(k - 1) * 3], ay = target[(k - 1) * 3 + 1], az = target[(k - 1) * 3 + 2];
                double cx = target[k * 3], cy = target[k * 3 + 1], cz = target[k * 3 + 2];
                double vx = cx - ax, vy = cy - ay;
                double l2 = vx * vx + vy * vy;
                double t = l2 > 1e-18 ? ((px - ax) * vx + (py - ay) * vy) / l2 : 0.0;
                t = t < 0 ? 0 : (t > 1 ? 1 : t);
                double qx = ax + t * vx, qy = ay + t * vy;
                double d = (px - qx) * (px - qx) + (py - qy) * (py - qy);
                if (d < best) { best = d; bx = qx; by = qy; bz = az + t * (cz - az); }
            }
            o[i * 3] = bx; o[i * 3 + 1] = by; o[i * 3 + 2] = bz;
        }
        return o;
    }

    /// <summary>
    /// 台架用：量"偏移点离原线多远"。圆角接头那条判据要它 ——
    /// 真等距时每个偏移点离原线恒为 off，限幅 miter 会在拐角处甩出去。
    /// </summary>
    public static double PointToPolylineDistXYForTest(double[] poly, double px, double py)
        => PointToPolylineDistXY(poly, px, py);

    /// <summary>点到折线的最近 XY 距离（逐段点-线段距）。</summary>
    private static double PointToPolylineDistXY(double[] poly, double px, double py)
    {
        int n = poly.Length / 3;
        if (n == 0) return 0;
        if (n == 1) return Math.Sqrt((px - poly[0]) * (px - poly[0]) + (py - poly[1]) * (py - poly[1]));
        double best = double.MaxValue;
        for (int i = 1; i < n; i++)
        {
            double ax = poly[(i - 1) * 3], ay = poly[(i - 1) * 3 + 1];
            double bx = poly[i * 3], by = poly[i * 3 + 1];
            double vx = bx - ax, vy = by - ay;
            double l2 = vx * vx + vy * vy;
            double t = l2 > 1e-18 ? ((px - ax) * vx + (py - ay) * vy) / l2 : 0.0;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = ax + t * vx, qy = ay + t * vy;
            double d = (px - qx) * (px - qx) + (py - qy) * (py - qy);
            if (d < best) best = d;
        }
        return Math.Sqrt(best);
    }

    /// <summary>尖角处偏移放大的限幅：1/cos(θ/2) 在锐角处会爆炸，必须钳（与 RingOffset 同口径）。</summary>
    private const double MaxMiter = 2.5;

    /// <summary>
    /// 把台阶线【等距外扩】<paramref name="off"/> 米（Z 不动 —— D4）。这就是"按台阶线向外发展"。
    ///
    /// 【为什么不能逐点沿各自法向平移】直线上两者一样，但台阶线是带拐角的环：
    /// 拐角处两条边各自的偏移线交在【角平分线】上、距原顶点 <c>off / cos(θ/2)</c> 处，
    /// 而不是距顶点 off。照法向平移，拐角会越推越"瘪"——推一两带看不出来，
    /// 推二十带（800m）出来的线已经完全不平行于原台阶线了，现场看就是"没顺着台阶线发展"。
    ///
    /// <paramref name="folded"/> 回报折回的段数：凹弯的曲率半径小于推进距离时，
    /// 偏移线会翻到另一侧（自交）。折回的位置不该再往外出条带 —— 那儿的台阶已经推没了。
    ///
    /// <paramref name="badSeg"/>（可选，长度须为 <c>点数−1</c>）逐段回填"这一段折回了没有"。
    /// <paramref name="vertexAt"/>（可选，长度须为 <c>点数</c>）逐点回填"原轨第 i 个顶点
    /// 对应返回轨的第几个点"。
    ///
    /// **凡是要把返回轨与原轨【按位对齐】的调用方，都必须用这两个出参**，不能自己拿下标去减：
    /// 圆角接头会在拐角处插点，返回的轨比原轨长，<c>r[i]</c> 早就不是原轨第 i 个顶点了。
    /// 一条 150° 拐角的轨实测 13 点插成 24 点 —— 自己减下标时真折回 0 段却标出 8/12 段。
    ///
    /// 这个错在本文件里【出现过三次】，三处都不报错、不崩，只是安静地读错点：
    ///   · `SplitAtFoldPoints` 的断点标记 —— 挖掉的是别处的台阶线
    ///   · 凹弯折回的一维裁剪 —— 留下的是别处的一段（逐带都跑，真实数据一轮命中 150 次）
    ///   · 撞车扫描的中点取样 —— 问的是别处的格子占没占（一轮命中 192 次）
    /// 判据只在这里留一份，别再抄第四份。
    /// </summary>
    public static double[] OffsetRail(double[] rail, (double X, double Y)[] dirs, double off,
                                      out int folded, bool[]? badSeg = null, int[]? vertexAt = null)
    {
        folded = 0;
        var r = (double[])rail.Clone();
        int n = r.Length / 3;
        if (vertexAt is not null)
            for (int i = 0; i < vertexAt.Length; i++) vertexAt[i] = Math.Min(i, Math.Max(0, n - 1));
        if (Math.Abs(off) < 1e-12 || n < 2) return r;

        // ① 逐【边】外法向：先取左法向，再按该处推进方向定正负号（统一指外）。
        //
        // 【为什么坚持用原始边的法向，而不是平滑方向场的平均】前者是【真等距】——
        // 拐角处两条边的偏移线交在角平分线上、距原顶点 off/cos(θ/2)。
        // 换成平滑方向场后 90° 直角处等距性掉 24%（100m 量成 76.5m），
        // 而"宽度齐不齐"正是这条链最要紧的性质之一。
        //
        // 代价是尖折点处 miter 会把邻近几米的点"反超"，被折回判据抓到 —— 那是真的自交，
        // 该抓。真实数据上 91/1238 个位置因此被夹窄（不是丢弃），拓扑与算量全绿。
        int m = n - 1;
        var en = new (double X, double Y)[Math.Max(1, m)];
        var sgn = new int[Math.Max(1, m)];                        // +1 = 用的左法向，-1 = 翻过号
        for (int i = 0; i < m; i++)
        {
            double ex = rail[(i + 1) * 3] - rail[i * 3], ey = rail[(i + 1) * 3 + 1] - rail[i * 3 + 1];
            double el = Math.Sqrt(ex * ex + ey * ey);
            if (el < 1e-9) { en[i] = dirs[Math.Min(i, dirs.Length - 1)]; sgn[i] = 0; continue; }
            double nx = -ey / el, ny = ex / el;
            var d = dirs[Math.Min(i, dirs.Length - 1)];
            sgn[i] = (nx * d.X + ny * d.Y < 0) ? -1 : 1;
            if (sgn[i] < 0) { nx = -nx; ny = -ny; }
            en[i] = (nx, ny);
        }

        // ② 逐【顶点】外扩。拐角分两种，处置完全不同：
        //
        //   · 朝偏移这一侧【张开】的拐角（外角）—— 两条边的偏移线之间是一道【缺口】，
        //     等距偏移在这里本来就是一段【圆弧】（圆心是原顶点、半径 off），不是一个尖。
        //     拿角平分线上那个交点去补，点距顶点 off/cos(θ/2)：θ 越尖甩得越远。
        //     限幅 2.5 只是把爆炸调慢，没有消掉它 —— 真实数据上推到第 50 带时 off=2000m，
        //     一个尖角就把点甩到 5000m 外，图上是横跨全区的【扇面】，
        //     而逐个位置的水密/算量/带号连续【全是绿的】，数值验收根本抓不到。
        //     改出圆弧后每个偏移点离原线【恒为 off】，尖角爆炸从构造上就不存在了。
        //
        //   · 朝偏移这一侧【收拢】的拐角（内角）—— 两条偏移线相互穿插，那是折回，
        //     由下面 ③ 的判据抓、由推进夹紧处置。这里照旧走角平分线。
        //
        // 张开/收拢怎么判：cross(a,b) 就是两条边的转向（旋转 90° 不改叉积、翻号平方掉），
        // 与偏移侧 sgn 反号即为张开。两段法向翻号不一致时（推进方向近乎沿边）判据不可靠，
        // 退回限幅 miter。
        //
        // 【弧点数只跟张角有关、与 off 无关】—— 前脸的两条轨（off = front 与 front+eps）
        // 必须点数一致、逐点成对，内核 loft 才对得上。挂上 off 就会一带一个样。
        var pts = new List<double>(n * 3 + 96);
        var segStart = new int[Math.Max(1, m)];    // 原段 i 起点对应的偏移点序号
        var segEnd = new int[Math.Max(1, m)];      // 原段 i 终点对应的偏移点序号
        for (int i = 0; i < n; i++)
        {
            int ia = Math.Max(0, Math.Min(i - 1, m - 1)), ib = Math.Max(0, Math.Min(i, m - 1));
            var a = en[ia]; var b = en[ib];
            double px = rail[i * 3], py = rail[i * 3 + 1], pz = rail[i * 3 + 2];
            int first = pts.Count / 3;

            double bx = a.X + b.X, by = a.Y + b.Y;
            double bl = Math.Sqrt(bx * bx + by * by);
            double cosHalf = bl > 1e-9 ? (bx / bl) * b.X + (by / bl) * b.Y : 0.0;
            bool open = i > 0 && i < n - 1 && bl > 1e-9
                        && cosHalf < 1.0 / MaxMiter
                        && sgn[ia] != 0 && sgn[ia] == sgn[ib]
                        && (a.X * b.Y - a.Y * b.X) * sgn[ib] < 0;

            if (open && off > 0)
            {
                double th0 = Math.Atan2(a.Y, a.X), th1 = Math.Atan2(b.Y, b.X);
                double dth = th1 - th0;
                while (dth > Math.PI) dth -= 2 * Math.PI;
                while (dth < -Math.PI) dth += 2 * Math.PI;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 12.0)));  // ≤15°/段
                for (int t = 0; t <= steps; t++)
                {
                    double th = th0 + dth * t / steps;
                    pts.Add(px + Math.Cos(th) * off); pts.Add(py + Math.Sin(th) * off); pts.Add(pz);
                }
            }
            else
            {
                if (bl < 1e-9) { bx = b.X; by = b.Y; bl = 1.0; }  // 180° 折返，退回单边法向
                double ux = bx / bl, uy = by / bl;
                double scale = 1.0 / Math.Max(cosHalf, 1.0 / MaxMiter);
                pts.Add(px + ux * off * scale); pts.Add(py + uy * off * scale); pts.Add(pz);
            }

            int last = pts.Count / 3 - 1;
            if (i > 0) segEnd[i - 1] = first;    // 原段 i-1 的终点 = 本顶点吐出的【第一个】点
            if (i < m) segStart[i] = last;       // 原段 i   的起点 = 本顶点吐出的【最后一个】点
            if (vertexAt is not null && i < vertexAt.Length) vertexAt[i] = last;
        }
        r = pts.ToArray();

        // ③ 折回检测：两条判据，缺一不可
        //   · 反号 —— 偏移后某段与原段方向相反 = 这一段翻到了曲率中心另一侧（自交）
        //   · 压缩 —— 还没反号，但偏移段被压到原段的三成以下。凹弯把后界压到曲率中心附近时，
        //     前后脸已经贴上并穿插，体被压塌但方向还没翻。内核当年也是先只看反号，
        //     真·自相交检测抓到 9 对而三个代理判据全说没问题，才补上压缩比这一条。
        //     实测：只查反号时真实数据仍留下 79 个位置真体积远小于清单（最低到 0.2%）。
        for (int i = 0; i < m; i++)
        {
            double ox = rail[(i + 1) * 3] - rail[i * 3], oy = rail[(i + 1) * 3 + 1] - rail[i * 3 + 1];
            // 圆弧接头后偏移点与原顶点不再一一对应，按 segStart/segEnd 找本段两端
            double qx = r[segEnd[i] * 3] - r[segStart[i] * 3],
                   qy = r[segEnd[i] * 3 + 1] - r[segStart[i] * 3 + 1];
            bool bad;
            if (ox * qx + oy * qy < 0) bad = true;
            else
            {
                double la = Math.Sqrt(ox * ox + oy * oy), lb = Math.Sqrt(qx * qx + qy * qy);
                bad = la > 1e-9 && lb < la * 0.30;
            }
            if (bad) { folded++; if (badSeg is not null) badSeg[i] = true; }
        }
        return r;
    }

    /// <summary>
    /// 把一条轨按累计平面长度切成 <paramref name="nsub"/> 段，返回 nsub+1 个【下标】切点。
    /// 段长尽量匀，但切点必须落在原有顶点上 —— 这样同一下标在几条同源轨上指的是同一条推进射线。
    /// </summary>
    private static int[] ArcCutIndices(double[] rail, int nsub)
    {
        int n = rail.Length / 3;
        if (n < 2 || nsub < 2) return new[] { 0, Math.Max(1, n - 1) };
        var acc = new double[n];
        for (int i = 1; i < n; i++)
        {
            double dx = rail[i * 3] - rail[(i - 1) * 3], dy = rail[i * 3 + 1] - rail[(i - 1) * 3 + 1];
            acc[i] = acc[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = acc[n - 1];
        var cuts = new List<int> { 0 };
        for (int s = 1; s < nsub; s++)
        {
            double target = total * s / nsub;
            int j = cuts[^1] + 1;
            while (j < n - 1 && acc[j] < target) j++;
            if (j > cuts[^1] && j < n - 1) cuts.Add(j);      // 顶点太少时自然会少切几段
        }
        cuts.Add(n - 1);
        return cuts.ToArray();
    }

    /// <summary>取 [i0, i1] 闭区间的点（含两端），扁平 xyz。</summary>
    private static double[] SliceByIndex(double[] xyz, int i0, int i1)
    {
        int cnt = i1 - i0 + 1;
        if (cnt < 2) return System.Array.Empty<double>();
        var r = new double[cnt * 3];
        System.Array.Copy(xyz, i0 * 3, r, 0, cnt * 3);
        return r;
    }

    /// <summary>
    /// 一幅备好的推进状态。抽出来是为了让循环能【按带】走 —— 同一级各幅并肩往外，
    /// 而不是一幅推到底再推下一幅。见 <see cref="Options.MaxClaimedOverlap"/>。
    /// </summary>
    private sealed class PanelState
    {
        public double[] Crest0 = System.Array.Empty<double>();
        public double[] Toe0 = System.Array.Empty<double>();
        public (double X, double Y)[] Dir = System.Array.Empty<(double, double)>();
        public BenchInput? Bench;
        public double Eps, H, FaceRun, StrikeFloor, CrestLen;
        /// <summary>本幅已推到的实际距离（被凹弯夹窄过就不是 k·W 的整数倍）。0 = 还没推。</summary>
        public double Reached;
        public int PanelSeq, PanelCount;
        /// <summary>
        /// 这一幅落在急弯段上（按一个 W 推会自交）—— 只能靠夹窄出窄带。
        ///
        /// 带序循环里 Stub 永远排在主推进面【后面】。早先让坏段成幅失败过一次，
        /// 失败的机理正是"窄带先占了地、主推进面撞上就停"；先后一分开，机理就不成立了。
        /// </summary>
        public bool IsStub;
    }

    /// <summary>
    /// 把一带的平面轮廓（前脸轨 + 反向后界轨）打散成 <paramref name="step"/> 米的栅格键。
    ///
    /// 用栅格而不是多边形布尔运算：算法层没有几何库，而这里要回答的问题
    ///（"这块地被别的幅占过多少"）本来就只需要面积量级的答案，不需要精确边界。
    /// 扫描线填充，键把 (ix,iy) 打包进一个 long。
    /// </summary>
    private static List<long> PlanCellKeys(double[] front, double[] back, double step)
    {
        var keys = new List<long>();
        int nf = front.Length / 3, nb = back.Length / 3;
        if (nf < 2 || nb < 2 || step <= 1e-6) return keys;

        int n = nf + nb;
        var px = new double[n]; var py = new double[n];
        for (int i = 0; i < nf; i++) { px[i] = front[i * 3]; py[i] = front[i * 3 + 1]; }
        for (int i = 0; i < nb; i++)
        { px[nf + i] = back[(nb - 1 - i) * 3]; py[nf + i] = back[(nb - 1 - i) * 3 + 1]; }

        double ymin = double.MaxValue, ymax = double.MinValue;
        for (int i = 0; i < n; i++) { if (py[i] < ymin) ymin = py[i]; if (py[i] > ymax) ymax = py[i]; }
        int j0 = (int)Math.Floor(ymin / step), j1 = (int)Math.Floor(ymax / step);
        if (j1 - j0 > 20000) return keys;                    // 病态输入的兜底，别把内存吃光

        var xs = new List<double>(8);
        for (int j = j0; j <= j1; j++)
        {
            double yc = (j + 0.5) * step;
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                int i2 = (i + 1) % n;
                double ya = py[i], yb = py[i2];
                if ((ya <= yc) == (yb <= yc)) continue;       // 不跨这条扫描线
                double t = (yc - ya) / (yb - ya);
                xs.Add(px[i] + t * (px[i2] - px[i]));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (int s = 0; s + 1 < xs.Count; s += 2)
            {
                int a = (int)Math.Floor(xs[s] / step), bIdx = (int)Math.Floor(xs[s + 1] / step);
                if (bIdx - a > 20000) continue;
                for (int ix = a; ix <= bIdx; ix++) keys.Add(((long)ix << 32) ^ (uint)j);
            }
        }
        return keys;
    }

    /// <summary>
    /// 占位栅格里【被围住的空格】：从格网包围盒外缘漫水，漫不到又没被认领的格子就是兜。
    /// 返回（格子数, 连通块数）。
    ///
    /// 用漫水而不是"找环的内洞"，是因为算法层没有几何库；而这个问题在栅格上本来就是
    /// 连通性问题，不必回到多边形。包围盒各边各留一圈空位当水源，免得贴边的空格被误判成兜。
    /// </summary>
    private static (long Cells, int Groups) CountEnclosedGaps(HashSet<long> claimed)
    {
        if (claimed.Count == 0) return (0, 0);
        int xLo = int.MaxValue, xHi = int.MinValue, yLo = int.MaxValue, yHi = int.MinValue;
        foreach (var k in claimed)
        {
            int ix = (int)(k >> 32), iy = (int)(uint)k;
            if (ix < xLo) xLo = ix; if (ix > xHi) xHi = ix;
            if (iy < yLo) yLo = iy; if (iy > yHi) yHi = iy;
        }
        xLo--; xHi++; yLo--; yHi++;                       // 留一圈水源
        long w = (long)xHi - xLo + 1, h = (long)yHi - yLo + 1;
        if (w <= 2 || h <= 2 || w * h > 40_000_000L) return (0, 0);   // 病态尺寸不算，别把内存吃光

        var outside = new bool[w * h];
        var stack = new Stack<long>();
        long Idx(int ix, int iy) => (long)(iy - yLo) * w + (ix - xLo);
        long Key(int ix, int iy) => ((long)ix << 32) ^ (uint)iy;

        for (int ix = xLo; ix <= xHi; ix++)
        { stack.Push(Idx(ix, yLo)); stack.Push(Idx(ix, yHi)); }
        for (int iy = yLo; iy <= yHi; iy++)
        { stack.Push(Idx(xLo, iy)); stack.Push(Idx(xHi, iy)); }

        while (stack.Count > 0)
        {
            long id = stack.Pop();
            if (id < 0 || id >= outside.Length || outside[id]) continue;
            int ix = (int)(id % w) + xLo, iy = (int)(id / w) + yLo;
            if (claimed.Contains(Key(ix, iy))) continue;
            outside[id] = true;
            if (ix > xLo) stack.Push(Idx(ix - 1, iy));
            if (ix < xHi) stack.Push(Idx(ix + 1, iy));
            if (iy > yLo) stack.Push(Idx(ix, iy - 1));
            if (iy < yHi) stack.Push(Idx(ix, iy + 1));
        }

        // 剩下的空格就是被围住的；再数一遍连通块，好报"几处"
        long cells = 0; int groups = 0;
        var seen = new bool[w * h];
        for (int iy = yLo; iy <= yHi; iy++)
            for (int ix = xLo; ix <= xHi; ix++)
            {
                long id = Idx(ix, iy);
                if (outside[id] || seen[id] || claimed.Contains(Key(ix, iy))) continue;
                groups++;
                stack.Push(id);
                long grp = 0;
                while (stack.Count > 0)
                {
                    long q = stack.Pop();
                    if (q < 0 || q >= outside.Length || seen[q] || outside[q]) continue;
                    int qx = (int)(q % w) + xLo, qy = (int)(q / w) + yLo;
                    if (claimed.Contains(Key(qx, qy))) continue;
                    seen[q] = true; grp++;
                    if (qx > xLo) stack.Push(Idx(qx - 1, qy));
                    if (qx < xHi) stack.Push(Idx(qx + 1, qy));
                    if (qy > yLo) stack.Push(Idx(qx, qy - 1));
                    if (qy < yHi) stack.Push(Idx(qx, qy + 1));
                }
                if (grp < 4) { groups--; continue; }      // 一两个格子的缝不算兜
                cells += grp;
            }
        return (cells, groups);
    }

    /// <summary>不关心折回数时的简写。</summary>
    public static double[] OffsetRail(double[] rail, (double X, double Y)[] dirs, double off)
        => OffsetRail(rail, dirs, off, out _);

    /// <summary>
    /// 本带质心：坡顶轨与坡底轨的中点各取一半，再沿推进方向挪半个 W。
    /// 截面是平行四边形，形心就在两条轨中线的中点再往推进方向偏 W/2。运距用这个精度够。
    /// </summary>
    private static (double X, double Y, double Z) Centroid(double[] crest, double[] toe, double W,
                                                           (double X, double Y)[] dirs)
    {
        var (cx, cy, cz) = MeanXyz(crest);
        var (tx, ty, tz) = MeanXyz(toe);
        double mx = (cx + tx) * 0.5, my = (cy + ty) * 0.5, mz = (cz + tz) * 0.5;
        double dx = 0, dy = 0;
        foreach (var d in dirs) { dx += d.X; dy += d.Y; }
        double dl = Math.Sqrt(dx * dx + dy * dy);
        if (dl > 1e-9) { mx += dx / dl * W * 0.5; my += dy / dl * W * 0.5; }
        return (mx, my, mz);
    }

    /// <summary>
    /// 本带质心的【真解】：前脸轨与反向后界轨围成的平面多边形的形心，Z 取台阶中高。
    ///
    /// 【为什么不能用两条轨的点平均再挪半个 W】那是把带当成直的平行四边形算的。
    /// 弯带上"点的平均值"根本不是面的形心，而且"沿推进方向挪 W/2"只用了一个平均方向 ——
    /// 拐弯的带上这个方向哪一头都不对。真实数据上把内核建出来的体的体积质心拿来比：
    /// **183/1279（14%）偏出 W/4 以外，最差 45.96m —— 比带宽还大**。
    ///
    /// 【为什么要紧】质心是【算运距】用的，寻径和排产按它排先后。
    /// 而方量、水密、拓扑判据对"体挪了位置"一概无感 —— 棱柱造法那个整体错位半个 W 的 bug
    /// 就是这么藏了很久的。位置得单独验（见 test_carve_strip_real_cells.cpp 的质心那一条）。
    ///
    /// 大坐标必须先平移再算：形心公式里有 x·y 的乘积，622000×4380000 直接吃掉有效位。
    /// </summary>
    private static (double X, double Y, double Z) CentroidExact(double[] front, double[] back,
                                                                double crestZ, double toeZ)
    {
        int nf = front.Length / 3, nb = back.Length / 3;
        if (nf < 2 || nb < 2) return (0, 0, (crestZ + toeZ) * 0.5);

        int n = nf + nb;
        var px = new double[n]; var py = new double[n];
        double ox = front[0], oy = front[1];
        for (int i = 0; i < nf; i++) { px[i] = front[i * 3] - ox; py[i] = front[i * 3 + 1] - oy; }
        for (int i = 0; i < nb; i++)
        { px[nf + i] = back[(nb - 1 - i) * 3] - ox; py[nf + i] = back[(nb - 1 - i) * 3 + 1] - oy; }

        double a2 = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double cr = px[i] * py[j] - px[j] * py[i];
            a2 += cr;
            sx += (px[i] + px[j]) * cr;
            sy += (py[i] + py[j]) * cr;
        }
        if (Math.Abs(a2) < 1e-9)      // 退化多边形（面积≈0）：退回顶点平均，至少不是 NaN
        {
            double mx0 = 0, my0 = 0;
            for (int i = 0; i < n; i++) { mx0 += px[i]; my0 += py[i]; }
            return (ox + mx0 / n, oy + my0 / n, (crestZ + toeZ) * 0.5);
        }
        return (ox + sx / (3.0 * a2), oy + sy / (3.0 * a2), (crestZ + toeZ) * 0.5);
    }

    /// <summary>
    /// 沿折线按【归一化弧长区间】[t0,t1] 等距重采样 <paramref name="n"/> 个点（含两端）。
    /// 两条轨用同一区间、同一点数取样 → 天然逐点对应、等长，正是内核 loft 要的输入。
    /// </summary>
    public static double[] ResampleByArcRange(double[] xyz, double t0, double t1, int n)
    {
        int m = xyz.Length / 3;
        if (m < 2 || n < 2) return Array.Empty<double>();

        var acc = new double[m];
        for (int i = 1; i < m; i++)
        {
            double dx = xyz[i * 3] - xyz[(i - 1) * 3];
            double dy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1];
            acc[i] = acc[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = acc[m - 1];
        if (total <= 1e-9) return Array.Empty<double>();

        var outXyz = new double[n * 3];
        int seg = 1;
        for (int k = 0; k < n; k++)
        {
            double t = t0 + (t1 - t0) * k / (n - 1);
            double target = Math.Max(0, Math.Min(total, total * t));
            while (seg < m - 1 && acc[seg] < target) seg++;
            while (seg > 1 && acc[seg - 1] > target) seg--;
            double segLen = acc[seg] - acc[seg - 1];
            double f = segLen > 1e-12 ? (target - acc[seg - 1]) / segLen : 0.0;
            for (int c = 0; c < 3; c++)
                outXyz[k * 3 + c] = xyz[(seg - 1) * 3 + c] + f * (xyz[seg * 3 + c] - xyz[(seg - 1) * 3 + c]);
        }
        return outXyz;
    }

    private static void SetZ(double[] xyz, double z)
    { for (int i = 2; i < xyz.Length; i += 3) xyz[i] = z; }

    /// <summary>
    /// 【按区域裁线】把一条台阶线裁到排土场区域环内，只留落在区域里的那些段。
    ///
    /// 【为什么不能拿质心做整条取舍】一条线横跨采场和排土场时，质心落哪边就整条归哪边 ——
    /// 归错了是整条错。而台阶线在图上本来就常常连着画过界（一条线绕过整个采区）。
    /// 区域要约束的是台阶线的【范围】，不是"要不要这条线"，所以正确做法是裁开：
    /// 落在区域内的段留下各自成线，出界的段丢掉。采场那条链早就是这么干的
    /// （<c>SeamOutcropBandExtractor.Options.ClipRingXy</c>）。
    ///
    /// 裁完的段是【开口线】—— 一条闭合环被区域切掉一截，剩下的就不再是环了，
    /// 闭合标志必须跟着改，否则后面补收尾段会凭空接一条横跨区域的弦。
    /// 整条都在区域内（一个交点都没有）时原样返回，闭合标志保留。
    /// </summary>
    public static List<(double[] Xyz, bool Closed)> ClipToRing(double[] xyz, bool closed, double[]? ringXy)
    {
        var outList = new List<(double[], bool)>();
        int n0 = xyz.Length / 3;
        if (n0 < 2) return outList;
        if (ringXy is not { Length: >= 6 }) { outList.Add((xyz, closed)); return outList; }

        // 闭合环把收尾段也纳入裁剪，否则那一段不受区域约束
        double[] work = closed ? CloseIfRing(xyz, true) : xyz;
        int n = work.Length / 3;

        var pieces = new List<List<double>>();
        List<double>? cur = null;
        // 【判"整条都在里面"不能数交点】环恰好在顶点上跨界时，交点参数正好是 0 或 1，
        // 被 t∈(0,1) 的过滤剔掉 —— 于是"零交点"却已经出界一半。如实记有没有出过界才准。
        bool anyOutside = false;

        for (int i = 0; i + 1 < n; i++)
        {
            double ax = work[i * 3], ay = work[i * 3 + 1], az = work[i * 3 + 2];
            double bx = work[(i + 1) * 3], by = work[(i + 1) * 3 + 1], bz = work[(i + 1) * 3 + 2];

            // 本段与环的全部交点参数 t，升序
            var ts = new List<double>();
            int rn = ringXy.Length / 2;
            for (int e = 0, f = rn - 1; e < rn; f = e++)
            {
                if (TrySegInt(ax, ay, bx, by, ringXy[f * 2], ringXy[f * 2 + 1], ringXy[e * 2], ringXy[e * 2 + 1],
                              out double t) && t > 1e-9 && t < 1 - 1e-9)
                    ts.Add(t);
            }
            ts.Sort();

            double prevT = 0;
            for (int s = 0; s <= ts.Count; s++)
            {
                double curT = s < ts.Count ? ts[s] : 1.0;
                double mt = (prevT + curT) * 0.5;
                bool inside = PointInRingXY(ax + (bx - ax) * mt, ay + (by - ay) * mt, ringXy);

                if (inside)
                {
                    cur ??= new List<double>();
                    if (cur.Count == 0)
                        cur.AddRange(new[] { ax + (bx - ax) * prevT, ay + (by - ay) * prevT, az + (bz - az) * prevT });
                    cur.AddRange(new[] { ax + (bx - ax) * curT, ay + (by - ay) * curT, az + (bz - az) * curT });
                }
                else
                {
                    anyOutside = true;
                    if (cur is { Count: >= 6 }) pieces.Add(cur);
                    cur = null;
                }

                prevT = curT;
            }
        }
        if (cur is { Count: >= 6 }) pieces.Add(cur);

        // 一步都没出过界 → 原样返回（闭合标志保住）
        if (!anyOutside) { outList.Add((xyz, closed)); return outList; }

        foreach (var p in pieces)
            if (p.Count >= 6) outList.Add((p.ToArray(), false));   // 裁过的段不再闭合
        return outList;
    }

    /// <summary>线段 AB 与线段 CD 求交，返回 AB 上的参数 t。平行/不相交返回 false。</summary>
    private static bool TrySegInt(double ax, double ay, double bx, double by,
                                  double cx, double cy, double dx, double dy, out double t)
    {
        t = 0;
        double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return false;
        double qpx = cx - ax, qpy = cy - ay;
        t = (qpx * sy - qpy * sx) / den;
        double u = (qpx * ry - qpy * rx) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    /// <summary>轨的中点是否落在环内。用中点而非全部点：边界处总有一两个点压线，全判会把整带误杀。</summary>
    public static bool MidPointInRing(double[] rail, double[] ringXy)
    {
        int n = rail.Length / 3;
        if (n == 0) return false;
        int m = n / 2;
        return PointInRingXY(rail[m * 3], rail[m * 3 + 1], ringXy);
    }

    /// <summary>bool 数组里最长的一段连续 true：返回起点下标，长度经 <paramref name="len"/> 带出；没有返回 -1。</summary>

    private static int LongestRun(bool[] f, out int len)

    {

        int bi = -1, bl = 0, s = -1;

        for (int i = 0; i <= f.Length; i++)

        {

            bool ok = i < f.Length && f[i];

            if (ok) { if (s < 0) s = i; }

            else if (s >= 0) { if (i - s > bl) { bl = i - s; bi = s; } s = -1; }

        }

        len = bl;

        return bi;

    }


    /// <summary>
    /// 后界轨【整条】在不在区域环内。D5 说的是"推进到区域环为止"，
    /// 那就得整条都在里面才算数。
    ///
    /// 【为什么不能只判中点】先前判的是中点：一条向两侧张开的后界轨，中段在环内、
    /// 两翼捅出环外 1 公里，中点判据照样放行。图上是横跨全区的扇面，
    /// 而逐个位置的水密/算量/带号连续全绿 —— 数值验收抓不到，是平面图看出来的。
    /// 环外的"潜在排土位置"根本不是位置。
    /// </summary>
    public static bool RailInRing(double[] rail, double[] ringXy)
    {
        int n = rail.Length / 3;
        if (n == 0) return false;
        for (int i = 0; i < n; i++)
            if (!PointInRingXY(rail[i * 3], rail[i * 3 + 1], ringXy)) return false;
        return true;
    }

    /// <summary>
    /// 一条带的平面面积：前脸轨 + 反向的后界轨围成的多边形，鞋带公式直接量。
    ///
    /// 不假设任何形状 —— 直的、弯的、尖角的都对。局部自重叠时正负相消，
    /// 给出的正是【净覆盖面积】，而那恰恰是这个位置真正能装东西的地方。
    /// </summary>
    public static double PolygonAreaXY(double[] front, double[] back)
    {
        int nf = front.Length / 3, nb = back.Length / 3;
        if (nf < 2 || nb < 2) return 0.0;

        // 前脸正向 + 后界反向 = 闭合环
        int n = nf + nb;
        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < nf; i++) { xs[i] = front[i * 3]; ys[i] = front[i * 3 + 1]; }
        for (int i = 0; i < nb; i++)
        { xs[nf + i] = back[(nb - 1 - i) * 3]; ys[nf + i] = back[(nb - 1 - i) * 3 + 1]; }

        // 平移到质心再算 —— 矿区坐标 1e6 量级，直接套鞋带公式有效位会被吃掉
        double ox = 0, oy = 0;
        for (int i = 0; i < n; i++) { ox += xs[i]; oy += ys[i]; }
        ox /= n; oy /= n;

        double a = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
            a += (xs[j] - ox) * (ys[i] - oy) - (xs[i] - ox) * (ys[j] - oy);
        return Math.Abs(a) * 0.5;
    }

    /// <summary>点在多边形环内（射线法）。ring = 扁平 [x0,y0,x1,y1,...]，隐式闭合。</summary>
    public static bool PointInRingXY(double px, double py, double[] ring)
    {
        int n = ring.Length / 2;
        if (n < 3) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1];
            double xj = ring[2 * j], yj = ring[2 * j + 1];
            bool cross = ((yi > py) != (yj > py)) &&
                         (px < (xj - xi) * (py - yi) / (yj - yi + 1e-12) + xi);
            if (cross) inside = !inside;
        }
        return inside;
    }

    /// <summary>是闭合环且首末点没重合 → 末尾补一个首点，让收尾段进入弧长表。非环原样返回。</summary>
    public static double[] CloseIfRing(double[] xyz, bool? closedHint = null)
    {
        int n = xyz.Length / 3;
        if (n < 4 || !(closedHint ?? IsClosed(xyz))) return xyz;
        double dx = xyz[0] - xyz[(n - 1) * 3], dy = xyz[1] - xyz[(n - 1) * 3 + 1];
        if (dx * dx + dy * dy < 1e-12) return xyz;          // 已经首末重合
        var r = new double[xyz.Length + 3];
        Array.Copy(xyz, r, xyz.Length);
        r[xyz.Length] = xyz[0]; r[xyz.Length + 1] = xyz[1]; r[xyz.Length + 2] = xyz[2];
        return r;
    }

    /// <summary>
    /// 几何自动判闭合（<see cref="BenchInput.CrestClosed"/> 没给时的退路）。
    ///
    /// 判据 = 首末间距跟【折点间距】比，不跟总长比。
    /// 【百分比阈值是错的】n 点环的首末间距本来就是总长的 1/(n−1)：8 点粗环占 14%，
    /// 拿"小于总长 5%"去判，粗环一律被判成开口线 —— 而排土台阶线恰恰常是十几个折点的粗环。
    /// 首末间距 ≈ 一个折点间距 = 这是一圈只是没重复首点；真开口线的首末间距是整条线的跨度，
    /// 比折点间距大一两个量级，两者分得很开。
    /// </summary>
    private static bool IsClosed(double[] xyz)
    {
        int n = xyz.Length / 3;
        if (n < 4) return false;
        double dx = xyz[0] - xyz[(n - 1) * 3], dy = xyz[1] - xyz[(n - 1) * 3 + 1];
        double gap = Math.Sqrt(dx * dx + dy * dy);
        if (gap < 1e-9) return true;                       // 首末点重合 = 显式闭合

        var segs = new List<double>(n - 1);
        for (int i = 1; i < n; i++)
        {
            double sx = xyz[i * 3] - xyz[(i - 1) * 3], sy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1];
            segs.Add(Math.Sqrt(sx * sx + sy * sy));
        }
        segs.Sort();
        double med = segs[segs.Count / 2];
        return med > 1e-9 && gap <= Math.Max(2.0 * med, 1.0);
    }

    private static double SignedAreaXY(double[] xyz)
    {
        int n = xyz.Length / 3;
        double a = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
            a += xyz[j * 3] * xyz[i * 3 + 1] - xyz[i * 3] * xyz[j * 3 + 1];
        return a * 0.5;
    }

    private static double[] Reverse(double[] xyz)
    {
        int n = xyz.Length / 3;
        var r = new double[xyz.Length];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 3; c++) r[i * 3 + c] = xyz[(n - 1 - i) * 3 + c];
        return r;
    }

    private static double[] Rotate(double[] xyz, int start)
    {
        int n = xyz.Length / 3;
        var r = new double[xyz.Length];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 3; c++) r[i * 3 + c] = xyz[((i + start) % n) * 3 + c];
        return r;
    }

    private static double Dist2XY(double[] a, int ia, double[] b, int ib)
    {
        double dx = a[ia * 3] - b[ib * 3], dy = a[ia * 3 + 1] - b[ib * 3 + 1];
        return dx * dx + dy * dy;
    }

    private static (double X, double Y, double Z) MeanXyz(double[] xyz)
    {
        int n = xyz.Length / 3;
        if (n == 0) return (0, 0, 0);
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < n; i++) { x += xyz[i * 3]; y += xyz[i * 3 + 1]; z += xyz[i * 3 + 2]; }
        return (x / n, y / n, z / n);
    }

    /// <summary>平面(XY)折线长度 —— 与 <see cref="StandardLevelModel"/> 同口径，不含坡面爬升。</summary>
    public static double PlanLength(double[] xyz)
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

    /// <summary>清单报表（CSV）—— 潜在排土位置一行一个。</summary>
    public static string BuildReport(Result r, string sourceNote = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("潜在排土位置清单");
        if (!string.IsNullOrWhiteSpace(sourceNote)) sb.AppendLine("来源," + sourceNote.Replace(',', '，'));
        sb.AppendLine($"位置数,{r.Cells.Count}");
        sb.AppendLine($"排土台阶级数,{r.LevelCount}");
        sb.AppendLine($"合计库容(m³占容方),{r.TotalCapacityM3:0}");
        sb.AppendLine();
        // 【子号必须单列一列】外凸拐角把带撑长之后，一带会切成几个位置，
        // 它们的 (台阶级,幅号,带号) 三列完全一样、只有编号带后缀。少了这一列，
        // 拿这张表做透视/排序的人会把它们并成一个位置 —— 库容对不上账。
        // 同一个坑在 dump_strip 的唯一索引、位置编号、排产位次上各栽过一次。
        sb.AppendLine("编号,台阶级,幅号,幅数,带号,带内子号,本带子格数,是否工作面带,坡顶标高(m),坡底标高(m),台阶高(m),"
                    + "走向长(m),有效条带宽(m),是否被凹弯夹窄,平面面积(m²),前脸投影(m),库容(m³),质心X,质心Y,质心Z");
        foreach (var c in r.Cells)
            sb.AppendLine($"{c.Code},{c.LevelIndex},{c.PanelIndex},{c.PanelCount},{c.StepIndex},"
                        + $"{c.SubIndex},{c.SubCount},"
                        + $"{(c.IsWorkingFace ? "是(真坡面)" : "否(格子界)")},"
                        + $"{c.CrestZ:0.##},{c.ToeZ:0.##},{c.BenchHeightM:0.##},{c.StrikeLenM:0.#},{c.StripWidthM:0.##},"
                        + $"{(c.IsClamped ? "是(地形逼窄)" : "否")},{c.PlanAreaM2:0},"
                        + $"{c.FaceRunM:0.##},{c.CapacityM3:0},{c.Cx:0.###},{c.Cy:0.###},{c.Cz:0.###}");
        if (r.Drops.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("丢弃/截断");
            foreach (var d in r.Drops) sb.AppendLine($"{d.Reason.Replace(',', '，')},{d.Count}");
        }
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("提示");
            foreach (var n in r.Notes) sb.AppendLine(n.Replace(',', '，'));
        }
        return sb.ToString();
    }
}
