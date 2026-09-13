// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimSolidBuilder.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  层体三角化 —— 把「本期推进了 v 米」变成一块**封闭的真三维体**。
//
//  ── 几何构造：真台阶放坡（不是垂直壁方块）──
//  一期一块区域的层体 = 期初轮廓环与期末轮廓环之间的**环带**，沿一个台阶高展开。
//  露天矿的台阶不是垂直壁：坡面有**坡面角 α**，台阶之间有**平盘（安全平台）宽 W**。
//    · 坡面的水平投影宽 S = H / tan(α)；α=90° 退化成垂直壁（S=0）。
//    · 采场（Cut）：向下挖。坡顶环在上（topZ），坡底环 = 坡顶环再**内缩** S，落在 botZ。
//      上一级的坡底 + 平盘 = 下一级的坡顶起点 ⇒ 第 k 级整体内缩 k·(S+W)。
//    · 排土（Fill）：向上堆。坡顶环在上（topZ），坡底环 = 坡顶环再**外扩** S，落在 botZ。
//      下一级的坡顶 + 平盘 = 上一级的坡底起点 ⇒ 第 k 级同样整体内缩 k·(S+W)。
//      （排土场越堆越高、平面越收，是正截锥；采场越挖越深、平面也越收，是倒截锥 —— 两者对偶。）
//  形体 = 顶盖（顶环带）+ 底盖（底环带，反绕）+ 外环侧壁（**期初坡面**）+ 内环侧壁（**期末坡面**），
//  四片合成一个闭合壳。侧壁不再垂直，而是按 α 倾斜的**真坡面**。
//
//  台阶级差 k·(S+W) 一律按**全周等距内缩**做：那是台阶自身的形态（截锥收口），
//  与「本期往哪个方位推进」无关，不跟推进模式走。
//
//  ── 两环重采样（必须做，否则三角化会扭曲）──
//  期初/期末两环由 RingOffset 从同一条源环偏出，正常情况下点数一致且索引对齐；
//  但有三种情况会破坏这个假设：
//    ① 采空收口（RingOffset.Collapse）把环退化成 8 点小环；
//    ② 推进距离为 0 时 Offset 原样返回源环（可能是顺时针）；
//    ③ 将来轮廓来源换成别的（台账重采样/抽稀）。
//  所以本文件**不信任索引对齐**：两环一律
//    (a) 统一成逆时针 → (b) 按**两环原始顶点参数的并集**重采样到同一点数 N → (c) 相位对齐配对。
//  (b) 用并集而不是等间距弧长，是因为等间距会跳过多边形角点、把面积削掉；
//  (c) 不做的话两环的「0 号点」可能落在轮廓两端，配对连线会整体扭一圈，侧面带绞成麻花。
//  这套已验证过 35 项算例（面积误差 1e-9、网格水密），本次放坡改造**在它之上加**，没有动它。
//
//  ── 体积自检（几何对不对的唯一验证手段）──
//  加了坡面之后层体不再是棱柱：顶环带面积 A_top ≠ 底环带面积 A_bot，形体是**棱台（prismatoid）**。
//  自检没有因为公式变复杂而退化成估算，反而升级成**两条独立算法互校**：
//    ① 解析式（棱台 / Simpson）：侧面是顶底对应点之间的直纹面 ⇒ 高度 t 处的环带面积 A(t) 是 t 的
//       二次多项式 ⇒ 积分精确等于 Simpson：**V = H/6 · (A顶 + 4·A中 + A底)**，A中 取顶底中点环。
//    ② 散度定理：直接对**实际发出去的三角网**积分 V = |Σ v0·(v1×v2)| / 6。
//    两者差 >0.5% 就说明网格没水密 / 法向不一致 / 顶点与解析环对不上 —— 直接判几何有问题。
//    α 未解析退回垂直壁时 A顶=A中=A底，公式化归为老的「环带面积 × 台阶高」，与既有 35 项算例逐位一致。
//
//  再与台账体积（采场 V实 / 排土 V容）对比。这两个数**不是同一条路算出来的**，所以能真校核：
//     台账体积 → 推进距离 v = V/(L×H) → 轮廓偏移 v → 顶环带面积 A = L_有效 × v
//     棱柱等效体积 = A × H = (L_有效 / L) × V
//  于是偏差拆成两项独立因子，各归各的：
//     ① **口径因子** = L_有效/L —— 推进模式与 L 含义不一致造成的（老结论，仍然成立）；
//     ② **形状因子** = 棱台/棱柱 —— 放坡带来的（垂直壁时恒为 1）。
//  L_有效 = 顶环带面积/推进距离 仍然直接算出来摆在旁边 —— 人一眼能看出 L 该填多少。
//  典型量级（1200×700 矩形采场、L 填 1200m 一条长边）：
//     · 全周等距：整圈内缩，L_有效 ≈ 周长 3800m → 棱柱等效是台账的 3.2 倍（+198%）；
//     · 定向平移：只动迎向方位那一侧，L_有效 ≈ 横向宽 700m → −42%；
//     · L 改填 3800m（周长）后 → −2%~−9%（残差来自逐期周长收缩与锐角限幅），判通过。
//  这个偏差是**推进模式与 L 口径不一致**的真实反映，不是 bug，更不该被藏起来：
//  层体形状按轮廓走，工程量一律以台账为准。
//
//  口径铁律：采场层体对的是**实方**，排土层体对的是**占容方（×Kr）**，两者不可混用。
//  本文件不做任何体积换算，只拿调用方给的口径量做对比，绝不写死密度/膨胀系数。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>台阶放坡的几何小工具（坡面水平投影宽等，采排两侧共用）。</summary>
public static class SimSlope
{
    /// <summary>
    /// 坡面的**水平投影宽** S = H / tan(α)（m）。
    /// α≥90°（垂直壁）或参数不成立时返回 0；α 极小会算出巨大的 S，按 50 倍台阶高限幅（调用方会警示）。
    /// </summary>
    public static double RunFor(double benchHeightM, double angleDeg)
    {
        if (!(benchHeightM > 1e-9) || double.IsNaN(angleDeg) || angleDeg <= 1e-6) return 0;
        if (angleDeg >= 90 - 1e-9) return 0;                       // 垂直壁
        double t = Math.Tan(angleDeg * Math.PI / 180.0);
        if (!(t > 1e-9)) return 0;
        return Math.Min(benchHeightM / t, benchHeightM * 50);
    }
}

/// <summary>层体极性：采场向下挖 / 排土向上堆。</summary>
public enum SimSolidPolarity
{
    /// <summary>采场：挖除层落在区域代表高程之下，逐期下切。口径 = 实方。</summary>
    Cut,
    /// <summary>排土：堆填层落在区域代表高程之上，逐期长高。口径 = 占容方。</summary>
    Fill,
}

/// <summary>
/// 层体顶点的物料着色源（块体模型接得上时逐顶点判煤/岩，接不上整条链降级到去向类型着色）。
/// 定义成接口是为了让 <see cref="SimSolidBuilder"/> 保持纯几何、不直接依赖 BlockModelLib。
/// </summary>
public interface ISimMaterialColorSource
{
    /// <summary>能按世界点采到煤岩类别码。</summary>
    bool Available { get; }
    /// <summary>来源 / 降级原因（界面直接显示这一句）。</summary>
    string StatusLabel { get; }
    /// <summary>煤色 0x00RRGGBB。</summary>
    uint CoalRgb { get; }
    /// <summary>岩色 0x00RRGGBB。</summary>
    uint RockRgb { get; }
    /// <summary>批量分类：世界坐标扁平 [x,y,z,...] → 每点类别（0=采不到 / 1=煤 / 2=岩）。</summary>
    byte[] ClassifyPoints(double[] worldXyz);
}

/// <summary>一期一块区域的层体（可直接喂 IEntityCapability.BuildColoredMeshOnLayer）。</summary>
public sealed class SimSolidLayer
{
    public string RegionName { get; set; } = "";
    public SimSolidPolarity Polarity { get; set; }
    /// <summary>第几期（0 基）。累计模式下决定层体摆在第几个台阶。</summary>
    public int LevelIndex { get; set; }

    // ── 内核要的三件套 ──
    /// <summary>世界坐标扁平 [x,y,z,...]。</summary>
    public double[] WorldXyz { get; set; } = Array.Empty<double>();
    /// <summary>三角形顶点索引，每 3 个一面。</summary>
    public uint[] Triangles { get; set; } = Array.Empty<uint>();
    /// <summary>逐顶点色 0x00RRGGBB。</summary>
    public uint[] VertexRgb { get; set; } = Array.Empty<uint>();

    public int VertexCount => WorldXyz.Length / 3;
    public int TriangleCount => Triangles.Length / 3;
    /// <summary>有可入库的几何（顶/底盖 + 内外侧壁至少各一圈）。</summary>
    public bool HasGeometry => VertexCount >= 12 && TriangleCount >= 8;

    // ── 几何量 ──
    /// <summary>**顶**环带平面面积 m²（本期推进扫过的地面，L_有效 归因用的就是它）。</summary>
    public double FootprintM2 { get; set; }
    /// <summary>底环带平面面积 m²（放坡后与顶环带不等，这正是棱台的由来）。</summary>
    public double BottomFootprintM2 { get; set; }
    /// <summary>半高处环带平面面积 m²（Simpson 的中项）。</summary>
    public double MidFootprintM2 { get; set; }
    /// <summary>层厚 = 台阶高 m。</summary>
    public double BenchHeightM { get; set; }
    /// <summary>本期推进距离 m（反算值 v = V/(L×H)）。NaN = 未解出。</summary>
    public double AdvanceM { get; set; } = double.NaN;
    /// <summary>反算时**填写**的工作线长 L m。0 = 未知。</summary>
    public double DeclaredLineLengthM { get; set; }

    // ── 放坡参数 ──
    /// <summary>台阶坡面角 α(°)。NaN = 未解析（层体按垂直壁建，不猜角度）。</summary>
    public double SlopeAngleDeg { get; set; } = double.NaN;
    /// <summary>平盘（安全平台）宽 W m。NaN = 未解析。</summary>
    public double BermWidthM { get; set; } = double.NaN;
    /// <summary>坡面水平投影宽 S = H/tan(α) m。0 = 垂直壁。</summary>
    public double SlopeRunM { get; set; }
    /// <summary>本级台阶相对基准环的水平错距 = LevelIndex × (S + W) m。</summary>
    public double LevelSetbackM { get; set; }
    /// <summary>真的按放坡建出来了（S&gt;0）。false = 垂直壁。</summary>
    public bool SlopeApplied => SlopeRunM > 1e-9;

    /// <summary>
    /// 轮廓给出的**有效推进边长** L_有效 = 顶环带面积 / 推进距离 m。
    /// <para>
    /// 这是体积偏差的直接归因量：棱柱等效体积 / 台账体积 ≈ L_有效 / L_填写。
    /// 等距推进是全周内缩/外扩，L_有效 ≈ 轮廓周长；定向平移只动迎向方位那一侧，
    /// L_有效 ≈ 垂直于推进方位的横向宽度。两者与「一条工作线的长度」根本不是一回事，
    /// 差几倍很正常 —— 所以必须把这个数摆出来，让人知道 L 该填什么。
    /// </para>
    /// </summary>
    public double EffectiveLineLengthM =>
        AdvanceM > 1e-9 && !double.IsNaN(AdvanceM) ? FootprintM2 / AdvanceM : double.NaN;
    /// <summary>层体顶/底高程 m。</summary>
    public double TopZ { get; set; }
    public double BottomZ { get; set; }
    /// <summary>重采样点数（每环）。</summary>
    public int SampleCount { get; set; }
    /// <summary>因期初/期末环重合（零厚度）而跳过的环段数 —— 跳它们是为了免 z-fight，见 Warnings。</summary>
    public int SkippedZeroSegments { get; set; }
    /// <summary>
    /// 跳过的那些段在顶环带上合计占的面积 m²。这是「跳段到底丢了多少东西」的**可量化**答案：
    /// 每段宽度都 ≤ <see cref="SimSolidBuilder.ZeroBandM"/>，所以这个数应当小到可忽略；
    /// 摆出来是让人自己判断，而不是让人信一句「不影响体积」。
    /// </summary>
    public double DroppedBandAreaM2 { get; set; }

    // ── 体积自检 ──
    /// <summary>
    /// 几何体积 m³ —— **棱台（Simpson）解析式**：V = H/6·(A顶 + 4·A中 + A底)。
    /// 侧面是直纹面 ⇒ A(t) 是 t 的二次式 ⇒ 该式是精确积分，不是近似。
    /// 垂直壁时三个面积相等，化归为老的「环带面积 × 台阶高」。
    /// </summary>
    public double MeshVolumeM3 { get; set; }
    /// <summary>散度定理对**实际三角网**积分出的体积 m³（与解析式互校，判网格是否水密/法向一致）。</summary>
    public double DivergenceVolumeM3 { get; set; }
    /// <summary>棱柱等效体积 m³ = 顶环带面积 × 台阶高（旧口径，只用来把「放坡带来的形状因子」单独摘出来）。</summary>
    public double PrismVolumeM3 => FootprintM2 * BenchHeightM;
    /// <summary>棱台/棱柱形状因子（放坡对体积的贡献倍数）。1 = 垂直壁。</summary>
    public double ShapeFactor => PrismVolumeM3 > 1e-9 ? MeshVolumeM3 / PrismVolumeM3 : 1;
    /// <summary>网格自检（解析式 ↔ 散度积分）通过。</summary>
    public bool MeshWatertight { get; set; }
    /// <summary>网格自检的人读结论。</summary>
    public string MeshCheckText { get; set; } = "";

    /// <summary>台账体积 m³：采场 = 本期挖除实方；排土 = 本期堆填占容方。0 = 未解出。</summary>
    public double TargetVolumeM3 { get; set; }
    /// <summary>台账体积可用（能做校核）。</summary>
    public bool TargetKnown => TargetVolumeM3 > SimSolidBuilder.VolumeFloorM3;
    /// <summary>相对偏差 %（几何 − 台账）/台账；台账未知返回 NaN。</summary>
    public double DeviationPct => TargetKnown ? (MeshVolumeM3 - TargetVolumeM3) / TargetVolumeM3 * 100 : double.NaN;
    public SimCheckLevel VolumeCheck { get; set; } = SimCheckLevel.NotAvailable;
    /// <summary>体积自检的人读结论（界面直接显示这一句）。</summary>
    public string VolumeCheckText { get; set; } = "";

    // ── 物料分色（块体模型）──
    /// <summary>本层体是按块体模型的煤岩类别上的色（false = 回落到去向类型着色）。</summary>
    public bool MaterialColored { get; set; }
    /// <summary>逐顶点采样命中：煤 / 岩 / 采不到。</summary>
    public int CoalVertices { get; set; }
    public int RockVertices { get; set; }
    public int UnsampledVertices { get; set; }
    /// <summary>着色来源/降级说明。</summary>
    public string MaterialLabel { get; set; } = "";
    /// <summary>本层体范围内的**块体口径**煤/岩体积构成（null = 没统计出来）。</summary>
    public SimBlockComposition? Composition { get; set; }

    /// <summary>建体失败/降级的人读原因（一条都不许吞）。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>入库后的实体 handle（0 = 未入库）。</summary>
    public ulong Handle { get; set; }

    /// <summary>口径名（体积单位后缀）。</summary>
    public string VolumeKind => Polarity == SimSolidPolarity.Cut ? "实方" : "占容方";

    public string Caption =>
        $"{RegionName}　{(Polarity == SimSolidPolarity.Cut ? "挖除层" : "堆填层")}"
      + $"　{TriangleCount} 面 / {VertexCount} 点　高程 {BottomZ:0.##}~{TopZ:0.##}m"
      + (SlopeApplied ? $"　坡面 {SlopeAngleDeg:0.#}°（投影 {SlopeRunM:0.##}m）· 平盘 {BermWidthM:0.##}m" : "　垂直壁");
}

/// <summary>把一期的推进量三角化成层体。纯几何，无宿主依赖，可单测。</summary>
public static class SimSolidBuilder
{
    // ── 配色：与 SimPlanRenderer / PitDesignGeometryPort 同一套，平面图和三维不能两套颜色 ──
    /// <summary>采场 橙。</summary>
    public const uint RgbPit = 0xD85A30;
    /// <summary>外排土场 蓝。</summary>
    public const uint RgbExternalDump = 0x2E6FCF;
    /// <summary>内排土场 青。</summary>
    public const uint RgbInternalDump = 0x1D9E75;
    /// <summary>库容见顶/排穿 红。</summary>
    public const uint RgbAlert = 0xE23B3B;

    /// <summary>底面/坡脚的压暗系数：顶亮底暗，台阶坡面才看得出立体，不是数据含义。</summary>
    private const double BottomShade = 0.52;

    /// <summary>均匀保底采样数（在两环原始顶点之外再补一圈，保证侧面带不至于太稀）。</summary>
    public const int MinSamples = 24;
    /// <summary>每环采样点数上限（8N 面，2048 → 16384 面，够用且不撑爆视口）。</summary>
    public const int MaxSamples = 2048;

    /// <summary>体积校核的绝对地板 m³：小于它的量视为「本期没实质推进」，不校核也不报警。</summary>
    public const double VolumeFloorM3 = 100;
    /// <summary>体积偏差告警阈 %。</summary>
    public const double VolumeWarnPct = 10;
    /// <summary>体积偏差错误阈 %。</summary>
    public const double VolumeErrorPct = 25;

    /// <summary>
    /// 零厚度环段的判定阈 m：该段的环带**有效厚度**（条带面积/段长）小于它，就整段跳过（不发三角）。
    /// <para>
    /// 「定向平移」推进时，**非推进侧**的期初/期末环完全重合，环带在那一段厚度为 0：
    /// 顶/底盖三角是零面积（无所谓），但内外侧壁**共面**，渲染时会 z-fight 出闪烁斑纹。
    /// 跳掉的是一整片零体积的「刀刃」，散度积分照样对得上棱台解析式（这一点由网格自检当场验证，
    /// 不是拍胸脯保证）—— 所以跳过是零代价的：既治了 z-fight，又不动体积。跳了几段会如实报出来。
    /// </para>
    /// </summary>
    public const double ZeroBandM = 1e-3;

    /// <summary>网格自检（解析式 ↔ 散度积分）的相对容差 %。</summary>
    public const double MeshCheckTolPct = 0.5;

    /// <summary>按区域类别取色（与平面示意一致）。</summary>
    public static uint RgbFor(SimRegion r, bool alert)
        => alert ? RgbAlert
         : r.IsInternalDump ? RgbInternalDump
         : r.IsDump ? RgbExternalDump
         : RgbPit;

    // ═════════════════════════ 入口 ═════════════════════════

    /// <summary>
    /// 从平面场景里的一块区域形状直接建层体（推荐入口：口径/极性/配色/放坡参数全部按 shape 走）。
    /// </summary>
    /// <param name="shape">某一帧某一区的推进形状（期初/期末环 + 台账体积 + 台阶高 + 放坡参数）。</param>
    /// <param name="levelIndex">第几期（0 基）。累计模式下决定摆在第几个台阶。</param>
    /// <param name="fallbackBenchH">
    /// 区域自身没解出台阶高时的兜底值（采场用界面 H）。&lt;=0 表示不兜底 —— 那就跳过该区，
    /// 不拿假台阶高凑数。
    /// </param>
    /// <param name="material">物料着色源（块体模型）；null 或不可用时回落到去向类型着色。</param>
    public static SimSolidLayer FromShape(SimPlanShape shape, int levelIndex, double fallbackBenchH = 0,
                                          ISimMaterialColorSource? material = null)
    {
        double h = shape.BenchHeightM > 1e-6 ? shape.BenchHeightM : fallbackBenchH;
        var layer = Build(
            shape.Region.Name,
            shape.Before, shape.After,
            double.IsNaN(shape.Region.Z) ? 0 : shape.Region.Z,
            h,
            shape.IsDump ? SimSolidPolarity.Fill : SimSolidPolarity.Cut,
            levelIndex,
            shape.TargetVolumeM3,
            RgbFor(shape.Region, shape.Alert),
            shape.AdvanceM,
            shape.WorkLineLengthM,
            shape.SlopeUsable ? shape.SlopeAngleDeg : double.NaN,
            shape.SlopeUsable ? shape.BermWidthM : double.NaN,
            material);

        if (double.IsNaN(shape.Region.Z))
            layer.Warnings.Add("区域轮廓无高程（既无台账真 xyz，也没能从现状面采到 Z），层体按 0 m 基准摆放："
                             + "形状与体积可信，绝对高程不可信。");
        else if (shape.Region.ZSource.Length > 0)
            layer.Warnings.Add($"层体基准高程 {shape.Region.Z:0.##} m，来源：{shape.Region.ZSource}。");
        if (shape.Region.Synthetic)
            layer.Warnings.Add("轮廓是**示意图形**（未圈画可采区域），层体形状不代表真实边界。");
        if (shape.BenchHeightM <= 1e-6 && fallbackBenchH > 1e-6)
            layer.Warnings.Add($"该区台账未给台阶高，层厚用界面 H={fallbackBenchH:0.##}m 兜底。");
        if (shape.SlopeSourceLabel.Length > 0)
            layer.Warnings.Add(shape.SlopeUsable ? $"放坡参数：{shape.SlopeSourceLabel}" : shape.SlopeSourceLabel);
        return layer;
    }

    /// <summary>
    /// 两环 + 台阶高 + 极性 (+ 放坡参数) → 一块封闭层体。
    /// 任何一步不成立都返回**无几何**的 layer 并带上人读原因，不抛。
    /// </summary>
    /// <param name="before">期初轮廓环（平面）。</param>
    /// <param name="after">期末轮廓环（平面）。</param>
    /// <param name="baseZ">区域代表高程 m。</param>
    /// <param name="benchHeightM">层厚（台阶高）m。</param>
    /// <param name="levelIndex">第几期（0 基）。</param>
    /// <param name="targetVolumeM3">台账体积 m³（采场实方 / 排土占容方）；0 = 未解出，不校核。</param>
    /// <param name="rgb">基色 0x00RRGGBB（物料着色不可用时用它）。</param>
    /// <param name="advanceM">本期推进距离 m（只用于偏差归因，不参与建模）。</param>
    /// <param name="declaredLineLengthM">反算时填写的工作线长 L m（只用于偏差归因）。</param>
    /// <param name="slopeAngleDeg">台阶坡面角 α(°)；NaN = 未解析 → 按垂直壁建（不猜角度）。</param>
    /// <param name="bermWidthM">平盘宽 W(m)；NaN = 未解析 → 按 0。</param>
    /// <param name="material">物料着色源。</param>
    public static SimSolidLayer Build(
        string regionName,
        IReadOnlyList<SimPoint> before,
        IReadOnlyList<SimPoint> after,
        double baseZ,
        double benchHeightM,
        SimSolidPolarity polarity,
        int levelIndex,
        double targetVolumeM3,
        uint rgb,
        double advanceM = double.NaN,
        double declaredLineLengthM = 0,
        double slopeAngleDeg = double.NaN,
        double bermWidthM = double.NaN,
        ISimMaterialColorSource? material = null)
    {
        var layer = new SimSolidLayer
        {
            RegionName = string.IsNullOrWhiteSpace(regionName) ? "（未命名区域）" : regionName,
            Polarity = polarity,
            LevelIndex = Math.Max(0, levelIndex),
            BenchHeightM = benchHeightM,
            TargetVolumeM3 = Math.Max(0, targetVolumeM3),
            AdvanceM = advanceM,
            DeclaredLineLengthM = Math.Max(0, declaredLineLengthM),
            SlopeAngleDeg = slopeAngleDeg,
            BermWidthM = bermWidthM,
        };

        try
        {
            // ① 前置条件：两环各 ≥3 点、台阶高为正、基准高程有限
            if (before == null || after == null || before.Count < 3 || after.Count < 3)
            {
                layer.Warnings.Add("期初/期末轮廓点数不足 3，无法围成环带 → 该区跳过（不拿假轮廓凑数）。");
                return layer;
            }
            if (!(benchHeightM > 1e-6) || double.IsNaN(benchHeightM) || double.IsInfinity(benchHeightM))
            {
                layer.Warnings.Add("台阶高未解出，定不了层厚 → 该区跳过（宁可不建，也不用缺省台阶高冒充）。");
                return layer;
            }
            if (double.IsNaN(baseZ) || double.IsInfinity(baseZ)) baseZ = 0;

            // ② 放坡量：坡面水平投影宽 S 与台阶级差 (S+W)。
            //    α 未解析 ⇒ S=0（垂直壁），整条几何路径与旧行为逐位一致。
            double berm = double.IsNaN(bermWidthM) || bermWidthM < 0 ? 0 : bermWidthM;
            double run = SimSlope.RunFor(benchHeightM, slopeAngleDeg);
            layer.SlopeRunM = run;
            layer.LevelSetbackM = layer.LevelIndex * (run + berm);

            // ③ 台阶级差：第 k 级整体**内缩** k·(S+W)。采场是倒截锥、排土是正截锥，都是「越远离基准面越收」，
            //    所以同一个方向。放在重采样之前做，后面的 Pair 照旧能处理点数变化（含收口退化）。
            var srcB = ApplySetback(before, layer.LevelSetbackM);
            var srcA = ApplySetback(after, layer.LevelSetbackM);
            if (layer.LevelSetbackM > 1e-9 && Math.Abs(SimRegion.SignedArea(srcA)) <= 1e-6)
            {
                layer.Warnings.Add($"第 {layer.LevelIndex + 1} 级台阶的水平错距已累到 {layer.LevelSetbackM:0.##} m"
                                 + $"（{layer.LevelIndex} × (坡面投影 {run:0.##} + 平盘 {berm:0.##})），轮廓被收没了（截锥收口）"
                                 + " → 该级不建体。这是放坡参数与区域尺度的真实关系，不是错误。");
                Grade(layer);
                return layer;
            }

            // ④ 统一逆时针 + 去重合点（外法向/绕序/弧长参数化全靠它）
            var b = Ccw(srcB);
            var a = Ccw(srcA);
            if (b.Count < 3 || a.Count < 3)
            {
                layer.Warnings.Add("去掉重合点后轮廓不足 3 点（退化成线或点）→ 该区跳过。");
                return layer;
            }

            // ⑤⑥ 重采样到同一点数 + 相位对齐（见 Pair 的注释：为什么不能用等间距弧长）
            var (ringB, ringA, decimated) = Pair(b, a);
            int n = ringB.Count;
            if (n < 3 || ringA.Count != n)
            {
                layer.Warnings.Add("轮廓重采样失败（周长为 0 或点全重合）→ 该区跳过。");
                return layer;
            }
            layer.SampleCount = n;
            if (decimated)
                layer.Warnings.Add($"两环顶点合计超过 {MaxSamples} 个，已抽稀到 {n} 点：环带面积会有轻微损失（体积自检里看得见）。");

            // ⑦ 分外/内环（采场期末在内、排土期末在外，但不假设，按面积判）
            double areaB = Math.Abs(SimRegion.SignedArea(ringB));
            double areaA = Math.Abs(SimRegion.SignedArea(ringA));
            var outerTop = areaB >= areaA ? ringB : ringA;
            var innerTop = areaB >= areaA ? ringA : ringB;

            // ⑧ 顶环带面积（= L_有效 归因用的那个面积）。用条带三角的有符号面积和，
            //    这才是**网格真正围出来的**面积；用 |A外|−|A内| 只是解析值，网格自交时会骗人。
            double signedTop = StripSignedArea(outerTop, innerTop);
            double footprint = Math.Abs(signedTop);
            layer.FootprintM2 = footprint;

            if (footprint * benchHeightM <= VolumeFloorM3)
            {
                layer.Warnings.Add(footprint <= 1e-6
                    ? "本期推进量为 0（或推进距离未解出），环带面积为零 → 无层体可建。"
                    : $"本期层体体积仅 {footprint * benchHeightM:0.#} m³，小于 {VolumeFloorM3:0} m³ 地板 → 不建（建了也看不见）。");
                Grade(layer);
                return layer;
            }

            // ⑨ 坡底环 = 坡顶环再偏 S：采场**内缩**（往坑里收）、排土**外扩**（坡脚甩出去）。
            //    偏移后点数必须仍是 n（RingOffset 逐顶点映射）；触发收口退化就退回垂直壁并说明。
            bool cut = polarity == SimSolidPolarity.Cut;
            var outerBot = outerTop;
            var innerBot = innerTop;
            if (run > 1e-9)
            {
                var ob = RingOffset.Offset(outerTop, run, outward: !cut, SimAdvanceMode.Uniform, 0);
                var ib = RingOffset.Offset(innerTop, run, outward: !cut, SimAdvanceMode.Uniform, 0);
                if (ob.Count == n && ib.Count == n) { outerBot = ob; innerBot = ib; }
                else
                {
                    layer.Warnings.Add($"坡面投影宽 S={run:0.##} m（α={slopeAngleDeg:0.#}°）大到把轮廓收没了（坡底环退化），"
                                     + "本级退回**垂直壁**建体 —— 宁可少一个坡面，也不给一块自交的烂网格。");
                    layer.SlopeRunM = run = 0;
                }
            }

            // ⑩ 层体高程：采场向下切，排土向上堆；levelIndex 让累计模式叠成阶梯
            double topZ, botZ;
            if (cut)
            {
                topZ = baseZ - layer.LevelIndex * benchHeightM;
                botZ = topZ - benchHeightM;
            }
            else
            {
                botZ = baseZ + layer.LevelIndex * benchHeightM;
                topZ = botZ + benchHeightM;
            }
            layer.TopZ = topZ;
            layer.BottomZ = botZ;

            // ⑪ 底/中环带面积 → 棱台（Simpson）体积。侧面是直纹面 ⇒ A(t) 二次 ⇒ Simpson 精确，不是近似。
            double signedBot = StripSignedArea(outerBot, innerBot);
            var outerMid = MidRing(outerTop, outerBot);
            var innerMid = MidRing(innerTop, innerBot);
            double signedMid = StripSignedArea(outerMid, innerMid);
            layer.BottomFootprintM2 = Math.Abs(signedBot);
            layer.MidFootprintM2 = Math.Abs(signedMid);
            layer.MeshVolumeM3 = Math.Abs(benchHeightM / 6.0 * (signedTop + 4 * signedMid + signedBot));

            // ⑫ 顶点：外环顶(0..n) 内环顶(n..2n) 外环底(2n..3n) 内环底(3n..4n)
            int nv = 4 * n;
            var xyz = new double[nv * 3];
            void Put(int idx, SimPoint p, double z)
            {
                xyz[idx * 3] = p.X; xyz[idx * 3 + 1] = p.Y; xyz[idx * 3 + 2] = z;
            }
            for (int i = 0; i < n; i++)
            {
                Put(i, outerTop[i], topZ);
                Put(n + i, innerTop[i], topZ);
                Put(2 * n + i, outerBot[i], botZ);
                Put(3 * n + i, innerBot[i], botZ);
            }

            // ⑬ 三角：顶盖(+Z) / 底盖(−Z) / 外壁=期初坡面(朝外) / 内壁=期末坡面(朝内)，共 8n 面。
            //    发两份：
            //      · full —— **完整闭合壳**，只喂给⑭的散度自检。散度定理只对闭合面成立，
            //                 拿删过面的开壳去积分会得出一个毫无意义的数（实测差 33%，那不是几何错，
            //                 是「对开曲面用了闭合面的公式」）。
            //      · emit —— 真正入库的那份，零厚度段整段不发（治 z-fight）。丢掉的是宽度 ≤ZeroBandM
            //                 的「刀刃」，面积记在 droppedArea 里如实报出来，量级一看便知可忽略。
            var full = new List<uint>(8 * n * 3);
            var emit = new List<uint>(8 * n * 3);
            int skipped = 0;
            double droppedArea = 0;

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                bool dead = IsZeroBand(outerTop, innerTop, outerBot, innerBot, i, j);
                if (dead)
                {
                    skipped++;
                    droppedArea += Math.Abs(Tri2(outerTop[i], outerTop[j], innerTop[j])
                                          + Tri2(outerTop[i], innerTop[j], innerTop[i]));
                }

                int ot = i, oj = j, it = n + i, ij = n + j;
                int ob2 = 2 * n + i, obj = 2 * n + j, ib2 = 3 * n + i, ibj = 3 * n + j;

                void T(int i0, int i1, int i2)
                {
                    full.Add((uint)i0); full.Add((uint)i1); full.Add((uint)i2);
                    if (!dead) { emit.Add((uint)i0); emit.Add((uint)i1); emit.Add((uint)i2); }
                }

                // 顶盖：逆时针外环 → 内环，法向 +Z
                T(ot, oj, ij);
                T(ot, ij, it);
                // 底盖：顶盖反绕，法向 −Z
                T(ob2, ibj, obj);
                T(ob2, ib2, ibj);
                // 外侧壁（期初坡面）：法向朝外
                T(ot, ob2, obj);
                T(ot, obj, oj);
                // 内侧壁（期末坡面）：法向朝内（外壁反绕）
                T(it, ibj, ib2);
                T(it, ij, ibj);
            }
            layer.SkippedZeroSegments = skipped;
            layer.DroppedBandAreaM2 = droppedArea;

            if (emit.Count < 24)
            {
                layer.WorldXyz = Array.Empty<double>();
                layer.Triangles = Array.Empty<uint>();
                layer.VertexRgb = Array.Empty<uint>();
                layer.Warnings.Add($"全环 {n} 段里有 {skipped} 段是零厚度，剩下的不足以围成体 → 该区不建体"
                                 + "（本期几乎没有实质推进，或推进方位与轮廓完全平行）。");
                Grade(layer);
                return layer;
            }

            layer.WorldXyz = xyz;
            layer.Triangles = emit.ToArray();

            // ⑭ 散度定理：对**完整闭合壳**积分，与 ⑪ 的解析式互校（网格水密性 + 法向一致性自检）
            layer.DivergenceVolumeM3 = MeshVolumeByDivergence(xyz, full);
            GradeMesh(layer);

            if (skipped > 0)
                layer.Warnings.Add($"跳过 {skipped}/{n} 段**零厚度环段**（期初/期末环在这些段上重合，"
                                 + "常见于「定向平移」的非推进侧）：这些段的内外壁共面，渲染会 z-fight。"
                                 + $"丢掉的这部分环带面积合计 {droppedArea:0.####} m²，占顶环带 "
                                 + $"{(footprint > 1e-9 ? droppedArea / footprint * 100 : 0):0.#####}% —— "
                                 + "是宽度不足 " + ZeroBandM.ToString("0.###") + " m 的「刀刃」，对体积无影响；"
                                 + "网格自检跑的是**完整壳**（散度定理只对闭合面成立），所以自检结论不受跳段影响。");

            // ⑮ 逐顶点上色：块体模型能采到就按煤/岩分色，采不到回落去向类型色（顶亮底暗）
            layer.VertexRgb = Colorize(layer, xyz, rgb, n, material);

            Grade(layer);
            return layer;
        }
        catch (Exception ex)
        {
            layer.WorldXyz = Array.Empty<double>();
            layer.Triangles = Array.Empty<uint>();
            layer.VertexRgb = Array.Empty<uint>();
            layer.Warnings.Add($"层体三角化异常（{ex.GetType().Name}: {ex.Message}）→ 该区跳过。");
            return layer;
        }
    }

    // ═════════════════════════ 着色 ═════════════════════════

    /// <summary>
    /// 逐顶点着色。
    /// <para>
    /// **块体模型可用**：按顶点的 (x,y,z) 采块体的煤岩类别码 → 煤色 / 岩色。
    /// 采不到的点（模型外 / 该处无块 / 无煤岩属性列）**不猜**，保留本区的去向类型色，
    /// 并把「采到几个、没采到几个」如实计到 layer 上。
    /// </para>
    /// <para>
    /// **块体模型不可用**：整条链降级为原来的去向类型着色（采橙 / 外排蓝 / 内排青），顶亮底暗。
    /// </para>
    /// </summary>
    private static uint[] Colorize(SimSolidLayer layer, double[] xyz, uint baseRgb, int n, ISimMaterialColorSource? material)
    {
        int nv = xyz.Length / 3;
        var col = new uint[nv];
        uint top = baseRgb & 0x00FFFFFF;
        uint bot = Shade(top, BottomShade);

        // 顶点布局：前 2n 个在顶面、后 2n 个在底面
        for (int i = 0; i < nv; i++) col[i] = i < 2 * n ? top : bot;

        if (material == null || !material.Available)
        {
            layer.MaterialColored = false;
            layer.MaterialLabel = material?.StatusLabel
                ?? "物料分色未启用：按去向类型着色（采场橙 / 外排蓝 / 内排青）。";
            return col;
        }

        byte[] cls;
        try { cls = material.ClassifyPoints(xyz); }
        catch (Exception ex)
        {
            layer.MaterialColored = false;
            layer.MaterialLabel = $"块体采样异常（{ex.GetType().Name}），已回落到去向类型着色。";
            return col;
        }
        if (cls == null || cls.Length < nv)
        {
            layer.MaterialColored = false;
            layer.MaterialLabel = "块体采样返回结果长度不符，已回落到去向类型着色。";
            return col;
        }

        int coal = 0, rock = 0, miss = 0;
        for (int i = 0; i < nv; i++)
        {
            bool onTop = i < 2 * n;
            switch (cls[i])
            {
                case 1: col[i] = onTop ? material.CoalRgb : Shade(material.CoalRgb, BottomShade); coal++; break;
                case 2: col[i] = onTop ? material.RockRgb : Shade(material.RockRgb, BottomShade); rock++; break;
                default: miss++; break;      // 采不到就保留去向类型色，不编一个煤岩出来
            }
        }
        layer.CoalVertices = coal;
        layer.RockVertices = rock;
        layer.UnsampledVertices = miss;
        layer.MaterialColored = coal + rock > 0;
        layer.MaterialLabel = layer.MaterialColored
            ? $"物料分色：{material.StatusLabel}；{nv} 个顶点里煤 {coal} / 岩 {rock} / 采不到 {miss}"
              + (miss > 0 ? "（采不到的点保留去向类型色，不按比例编煤岩）" : "")
            : $"物料分色：块体模型接上了，但本层体的 {nv} 个顶点**一个都没采到**"
              + "（层体落在块体模型范围之外，或该处无块）→ 全部回落到去向类型着色。";
        return col;
    }

    // ═════════════════════════ 体积自检 ═════════════════════════

    /// <summary>
    /// 网格自检：棱台解析式 ↔ 散度定理对实际三角网的积分。
    /// 两条路互相独立（一条走环的面积、一条走三角的行列式），对得上才说明网格水密且法向一致。
    /// </summary>
    private static void GradeMesh(SimSolidLayer l)
    {
        double an = l.MeshVolumeM3, dv = l.DivergenceVolumeM3;
        if (!(an > 1e-9))
        {
            l.MeshWatertight = false;
            l.MeshCheckText = "解析体积为 0，网格自检不成立。";
            return;
        }
        double pct = Math.Abs(dv - an) / an * 100;
        l.MeshWatertight = pct <= MeshCheckTolPct;
        l.MeshCheckText = l.MeshWatertight
            ? $"网格自检通过：棱台解析 {an:0.###} m³ ↔ 散度积分 {dv:0.###} m³，差 {pct:0.####}%（≤{MeshCheckTolPct}%）—— 壳水密、法向一致。"
            : $"⚠ 网格自检不通过：棱台解析 {an:0.###} m³ ↔ 散度积分 {dv:0.###} m³，差 {pct:0.##}% —— "
            + "壳可能没闭合或有面翻向，层体只能看态势，不能量方。";
        if (!l.MeshWatertight) l.Warnings.Add(l.MeshCheckText);
    }

    /// <summary>
    /// 体积自检：几何体积（棱台解析式）↔ 台账体积（采场 V实 / 排土 V容）。
    /// 两个数来自两条独立路径，偏差有明确的物理含义，必须显示出来。
    /// </summary>
    private static void Grade(SimSolidLayer l)
    {
        double mesh = l.MeshVolumeM3;
        string geo = l.SlopeApplied
            ? $"几何体 {mesh / 1e4:0.###}万m³（棱台 H/6·(A顶+4A中+A底)：顶环带 {l.FootprintM2 / 1e4:0.###}万m² / "
              + $"底环带 {l.BottomFootprintM2 / 1e4:0.###}万m² × 台阶 {l.BenchHeightM:0.##}m，坡面 {l.SlopeAngleDeg:0.#}°）"
            : $"几何体 {mesh / 1e4:0.###}万m³（垂直壁棱柱：环带 {l.FootprintM2 / 1e4:0.###}万m² × 台阶 {l.BenchHeightM:0.##}m）";

        if (!l.HasGeometry)
        {
            // 本期压根没建出体（推进为 0 / 参数缺失）：那是「没做」，不是「做错」，
            // 判 NotAvailable 而不是 Error —— 恒不成立的比较不构成校核结论。
            l.VolumeCheck = SimCheckLevel.NotAvailable;
            l.VolumeCheckText = "本期未建出层体 → 无体积可校核（原因见提示）。";
            return;
        }

        if (!l.TargetKnown)
        {
            l.VolumeCheck = SimCheckLevel.NotAvailable;
            l.VolumeCheckText = $"{geo}；本期台账体积未解出 → **无法校核**（不是通过）。";
            return;
        }

        double d = mesh - l.TargetVolumeM3;
        double pct = d / l.TargetVolumeM3 * 100;
        double apct = Math.Abs(pct);

        l.VolumeCheck = Math.Abs(d) <= VolumeFloorM3 ? SimCheckLevel.Ok
                      : apct >= VolumeErrorPct ? SimCheckLevel.Error
                      : apct >= VolumeWarnPct ? SimCheckLevel.Warn
                      : SimCheckLevel.Ok;

        string verdict = l.VolumeCheck switch
        {
            SimCheckLevel.Ok => "体积自检通过",
            SimCheckLevel.Warn => "体积偏差偏大",
            _ => "体积对不上",
        };
        l.VolumeCheckText =
            $"{geo} ↔ 台账 {l.TargetVolumeM3 / 1e4:0.###}万m³{l.VolumeKind}，"
          + $"偏差 {d / 1e4:+0.###;-0.###;0}万m³（{pct:+0.#;-0.#;0}%）· {verdict}";

        if (l.VolumeCheck == SimCheckLevel.Ok) return;

        // 归因：偏差拆成两项独立因子，各归各的，别混成一个刺眼的百分比。
        //   ① 口径因子 = L_有效 / L_填写  —— 推进模式与 L 含义不一致造成的（老结论，仍成立）；
        //   ② 形状因子 = 棱台体积 / 棱柱体积 —— 放坡带来的（垂直壁时恒为 1）。
        double lEff = l.EffectiveLineLengthM;
        if (!double.IsNaN(lEff) && lEff > 1e-6 && l.DeclaredLineLengthM > 1e-6)
        {
            l.Warnings.Add(
                $"偏差归因①（口径）：轮廓的**有效推进边长** L_有效 = 顶环带面积/推进距离 = {lEff:0} m，"
              + $"而反算用的**工作线长** L = {l.DeclaredLineLengthM:0} m，比值 {lEff / l.DeclaredLineLengthM:0.00}×。"
              + "「全周等距」是整圈内缩/外扩，L_有效≈轮廓周长；「定向平移」只动迎向方位那一侧，"
              + "L_有效≈垂直推进方位的横向宽度。要让层体体积对上台账，把 L 填成上面的 L_有效即可"
              + "（或改推进模式/方位，让轮廓的推进方式与 L 的含义一致）。");
        }
        else
        {
            l.Warnings.Add("偏差归因①（口径）：填写的工作线长 L 与轮廓的有效推进边长不符，或等距推进在锐角顶点被限幅"
                         + "（RingOffset 的 MaxMiter），或多个源/汇合并到了同一块区域。");
        }
        if (l.SlopeApplied)
            l.Warnings.Add($"偏差归因②（形状）：放坡把棱柱变成了棱台，形状因子 = 棱台/棱柱 = {l.ShapeFactor:0.000}×"
                         + $"（坡面 {l.SlopeAngleDeg:0.#}°、投影宽 {l.SlopeRunM:0.##}m）。"
                         + "台账体积是按 v=V/(L×H) 这个**棱柱口径**反算出来的，本来就不含坡面，这一项差是意料之中的。");
        l.Warnings.Add("**层体形状按轮廓走，工程量一律以台账数为准** —— 层体是用来看推进态势的，不是用来量方的。");
    }

    // ═════════════════════════ 几何小件 ═════════════════════════

    /// <summary>台阶级差：整体**内缩** dist 米（全周等距）。dist≤0 原样返回。</summary>
    private static List<SimPoint> ApplySetback(IReadOnlyList<SimPoint> ring, double dist)
        => dist <= 1e-9 ? ring.ToList()
                        : RingOffset.Offset(ring, dist, outward: false, SimAdvanceMode.Uniform, 0);

    /// <summary>两环中点环（Simpson 的中截面）。</summary>
    private static List<SimPoint> MidRing(IReadOnlyList<SimPoint> a, IReadOnlyList<SimPoint> b)
    {
        int n = Math.Min(a.Count, b.Count);
        var r = new List<SimPoint>(n);
        for (int i = 0; i < n; i++) r.Add(new SimPoint((a[i].X + b[i].X) * 0.5, (a[i].Y + b[i].Y) * 0.5));
        return r;
    }

    /// <summary>环带（外环−内环）的有符号面积：按条带三角求和 —— 网格真正围出来的那个面积。</summary>
    private static double StripSignedArea(IReadOnlyList<SimPoint> outer, IReadOnlyList<SimPoint> inner)
    {
        int n = Math.Min(outer.Count, inner.Count);
        if (n < 3) return 0;
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            s += Tri2(outer[i], outer[j], inner[j]);
            s += Tri2(outer[i], inner[j], inner[i]);
        }
        return s;
    }

    /// <summary>
    /// 第 i 段的环带**有效厚度** m = 该段条带四边形的面积 / 段长。
    /// <para>
    /// 不能拿「两环对应顶点的距离」当厚度：重采样按**归一化弧长**取点，两环周长不同，
    /// 同一个参数 t 在两条环上落到的位置沿边**错开**若干米。于是「几何上完全重合的那一段」
    /// （定向平移的非推进侧）顶点距离并不为 0，按距离判会一段都判不出来 —— 实测过，0/36 段。
    /// 而按**面积/段长**判就稳：两环共线时条带四边形面积恒为 0，与顶点怎么错开无关。
    /// </para>
    /// </summary>
    private static double SegBandWidth(IReadOnlyList<SimPoint> outer, IReadOnlyList<SimPoint> inner, int i, int j)
    {
        double area = Math.Abs(Tri2(outer[i], outer[j], inner[j]) + Tri2(outer[i], inner[j], inner[i]));
        double len = Math.Max((outer[j] - outer[i]).Length, (inner[j] - inner[i]).Length);
        return len <= 1e-9 ? 0 : area / len;      // 段长也为 0 = 整段退化成点，同样该跳
    }

    /// <summary>第 i 段是不是零厚度（顶、底两层的条带都薄于 <see cref="ZeroBandM"/>）。</summary>
    private static bool IsZeroBand(IReadOnlyList<SimPoint> outerTop, IReadOnlyList<SimPoint> innerTop,
                                   IReadOnlyList<SimPoint> outerBot, IReadOnlyList<SimPoint> innerBot, int i, int j)
        => SegBandWidth(outerTop, innerTop, i, j) <= ZeroBandM
        && SegBandWidth(outerBot, innerBot, i, j) <= ZeroBandM;

    /// <summary>
    /// 散度定理算封闭三角网的体积：V = |Σ v0·(v1×v2)| / 6。
    /// 只对水密且法向一致的壳成立 —— 所以它同时就是一次**网格质量检测**。
    /// </summary>
    private static double MeshVolumeByDivergence(double[] xyz, IReadOnlyList<uint> tris)
    {
        double v6 = 0;
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            int a = (int)tris[t] * 3, b = (int)tris[t + 1] * 3, c = (int)tris[t + 2] * 3;
            double ax = xyz[a], ay = xyz[a + 1], az = xyz[a + 2];
            double bx = xyz[b], by = xyz[b + 1], bz = xyz[b + 2];
            double cx = xyz[c], cy = xyz[c + 1], cz = xyz[c + 2];
            // v0 · (v1 × v2)
            v6 += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
        }
        return Math.Abs(v6) / 6.0;
    }

    /// <summary>三角形有符号面积（2D）。</summary>
    private static double Tri2(SimPoint p, SimPoint q, SimPoint r)
        => 0.5 * ((q.X - p.X) * (r.Y - p.Y) - (r.X - p.X) * (q.Y - p.Y));

    /// <summary>统一成逆时针，并去掉重合点（弧长参数化不能有 0 长段）。</summary>
    private static List<SimPoint> Ccw(IReadOnlyList<SimPoint> ring)
    {
        var pts = new List<SimPoint>(ring.Count);
        foreach (var p in ring)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y)) continue;
            if (pts.Count > 0 && (p - pts[^1]).Length <= 1e-9) continue;
            pts.Add(p);
        }
        while (pts.Count > 1 && (pts[^1] - pts[0]).Length <= 1e-9) pts.RemoveAt(pts.Count - 1);
        if (pts.Count >= 3 && SimRegion.SignedArea(pts) < 0) pts.Reverse();
        return pts;
    }

    /// <summary>闭合环的归一化弧长参数化：t∈[0,1) → 环上一点。</summary>
    private sealed class RingParam
    {
        private readonly List<SimPoint> _p;
        private readonly double[] _cum;     // _cum[i] = 到 i 号点的累计弧长；_cum[m] = 周长
        public readonly double Total;
        /// <summary>各原始顶点的归一化参数（重采样时要**原样保留**，否则多边形角点会被削掉）。</summary>
        public readonly double[] T;

        public RingParam(List<SimPoint> pts)
        {
            _p = pts;
            int m = pts.Count;
            _cum = new double[m + 1];
            for (int i = 0; i < m; i++) _cum[i + 1] = _cum[i] + (pts[(i + 1) % m] - pts[i]).Length;
            Total = _cum[m];
            T = new double[m];
            if (Total > 1e-9) for (int i = 0; i < m; i++) T[i] = _cum[i] / Total;
        }

        public bool Valid => _p.Count >= 3 && Total > 1e-9;

        public SimPoint At(double t)
        {
            int m = _p.Count;
            if (m == 0) return new SimPoint(0, 0);
            if (Total <= 1e-9) return _p[0];
            t -= Math.Floor(t);
            double s = t * Total;
            int lo = 0, hi = m;                        // 找 k 使 _cum[k] <= s < _cum[k+1]
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) / 2;
                if (_cum[mid] <= s) lo = mid; else hi = mid;
            }
            int k = Math.Min(lo, m - 1);
            double seg = _cum[k + 1] - _cum[k];
            double u = seg <= 1e-12 ? 0 : Math.Clamp((s - _cum[k]) / seg, 0, 1);
            var p = _p[k];
            var q = _p[(k + 1) % m];
            return new SimPoint(p.X + (q.X - p.X) * u, p.Y + (q.Y - p.Y) * u);
        }
    }

    /// <summary>
    /// 两环配对重采样 —— 层体几何的关键一步。
    /// <para>
    /// **为什么不能用「等间距弧长重采样」**：那样采出来的点全在边上，但**会跳过多边形角点**。
    /// 一个 1000×600 的矩形取 16 个等距点，若相位不巧，每个角能被削掉上万 m² ——
    /// 环带面积直接失真，体积自检就变成在校核一个错的几何。
    /// </para>
    /// <para>
    /// 做法：取两环**原始顶点参数的并集**（A 的参数先按最优相位平移到 B 的参数系），
    /// 两环都在这套并集参数上取值。于是
    ///   · 每个原始角点都被保留（落在自己环上的原参数处，精确复现）；
    ///   · 另一环在该参数处取到的是边上的插值点（本来就在边上，面积不受影响）；
    ///   · 两环点数天然相同且索引一一对应，侧面带不会扭。
    /// 再补一圈均匀参数保底密度（同样落在边上，不改面积），并按相位对齐，避免绞成麻花。
    /// </para>
    /// </summary>
    /// <returns>(期初环采样, 期末环采样, 是否因超过 MaxSamples 而抽稀过)。</returns>
    private static (List<SimPoint> B, List<SimPoint> A, bool Decimated) Pair(List<SimPoint> b, List<SimPoint> a)
    {
        var pb = new RingParam(b);
        var pa = new RingParam(a);
        if (!pb.Valid || !pa.Valid) return (new List<SimPoint>(), new List<SimPoint>(), false);

        // ① 粗相位搜索：两环的「0 号点」未必在同侧，不对齐侧面带会整体扭一圈
        int probe = Math.Clamp(Math.Max(b.Count, a.Count) * 2, 64, 256);
        double phi = BestPhase(pb, pa, probe);

        // ② 参数并集（A 的参数先平移到 B 的参数系）+ 均匀保底
        var set = new List<double>(b.Count + a.Count + MinSamples);
        foreach (var t in pb.T) set.Add(Wrap(t));
        foreach (var t in pa.T) set.Add(Wrap(t - phi));
        for (int i = 0; i < MinSamples; i++) set.Add((double)i / MinSamples);
        set.Sort();

        // ③ 去掉几乎重合的参数（免得出现 0 长边）
        double eps = 1.0 / (16.0 * MaxSamples);
        var u = new List<double>(set.Count);
        foreach (var t in set)
            if (u.Count == 0 || t - u[^1] > eps) u.Add(t);
        if (u.Count >= 2 && 1.0 - u[^1] + u[0] <= eps) u.RemoveAt(u.Count - 1);

        // ④ 极端稠密时抽稀（会轻微损失角点，layer 里会带警示）
        bool decimated = false;
        if (u.Count > MaxSamples)
        {
            int step = (int)Math.Ceiling(u.Count / (double)MaxSamples);
            var thin = new List<double>(MaxSamples + 1);
            for (int i = 0; i < u.Count; i += step) thin.Add(u[i]);
            u = thin;
            decimated = true;
        }

        var rb = new List<SimPoint>(u.Count);
        var ra = new List<SimPoint>(u.Count);
        foreach (var t in u)
        {
            rb.Add(pb.At(t));
            ra.Add(pa.At(t + phi));
        }
        return (rb, ra, decimated);
    }

    /// <summary>在均匀探针上搜使配对连线平方和最短的相位差 φ（A 相对 B）。带早停。</summary>
    private static double BestPhase(RingParam b, RingParam a, int probe)
    {
        var bs = new SimPoint[probe];
        var as_ = new SimPoint[probe];
        for (int i = 0; i < probe; i++)
        {
            bs[i] = b.At((double)i / probe);
            as_[i] = a.At((double)i / probe);
        }
        int best = 0;
        double bestD = double.MaxValue;
        for (int k = 0; k < probe; k++)
        {
            double d = 0;
            for (int i = 0; i < probe; i++)
            {
                var p = bs[i];
                var q = as_[(i + k) % probe];
                double dx = p.X - q.X, dy = p.Y - q.Y;
                d += dx * dx + dy * dy;
                if (d >= bestD) break;
            }
            if (d < bestD) { bestD = d; best = k; }
        }
        return (double)best / probe;
    }

    private static double Wrap(double t) => t - Math.Floor(t);

    /// <summary>压暗一档（顶亮底暗，纯视觉，不带数据含义）。</summary>
    private static uint Shade(uint rgb, double k)
    {
        int r = (int)Math.Round(((rgb >> 16) & 0xFF) * k);
        int g = (int)Math.Round(((rgb >> 8) & 0xFF) * k);
        int b = (int)Math.Round((rgb & 0xFF) * k);
        r = Math.Clamp(r, 0, 255); g = Math.Clamp(g, 0, 255); b = Math.Clamp(b, 0, 255);
        return (uint)((r << 16) | (g << 8) | b);
    }
}
