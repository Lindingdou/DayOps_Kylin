// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/ActivityAnchor.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>
/// 作业面坐标与本矿场景的**锚点校正**。
///
/// <para><b>这是截图逼出来的</b>：日班档下 6 个作业标记全部浮在采场外的空地上。
/// 65 条判据一条没红 —— 因为标记确实画在了作业面档案给的坐标上，摆放逻辑没有错。
/// 错的是那批坐标的**原点**：作业面台账为空时任务盘子回落样例盘子，
/// 而样例盘子的基准点写死在代码里 (620000, 4376000)，与本矿采掘单元台账的实测坐标无关。
/// 这正是 <c>[数值验收全绿也可能是盲区]</c> 那条：形态问题只有出图才看得见。</para>
///
/// <para><b>为什么是整体平移</b>：样例盘子里各作业面**彼此之间**的方位与距离
/// （主采面·东、辅采面·南、表土剥离面·北…）是这份样例真正的内容；它的绝对原点是任意的。
/// 所以用一个刚性 Δ 把整簇搬到台账中心：相对格局一点不动，只把"活在几公里外的空地上"
/// 这句错误的绝对声明去掉。缩放、旋转、逐点吸附一律不做 —— 那才是编造位置。</para>
/// </summary>
internal static class ActivityAnchor
{
    /// <summary>离参照盒最小的判定间距：比这还近的，当作"就在场边上"，不动。</summary>
    private const double MinSeparationM = 500;

    /// <summary>间距还要超过参照系**较短边**的这个比例，才算不同原点。</summary>
    private const double SpanFraction = 0.20;

    /// <summary>高程差超过这个值才一并平移；否则只动平面。</summary>
    private const double ZTriggerM = 50;

    /// <summary>盒内点占比达到这个比例就判为"同一个坐标系"，一律不动。</summary>
    private const double InsideShare = 1.0 / 3.0;

    public readonly struct Pt
    {
        public readonly double X, Y, Z;
        public Pt(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    public readonly struct Result
    {
        public readonly double Dx, Dy, Dz;
        public readonly bool Fired;
        /// <summary>到参照盒的间距（m）；未触发时也有值，供诊断。</summary>
        public readonly double SeparationM;
        /// <summary>本次用的间距闸门（m）。</summary>
        public readonly double ThresholdM;
        public Result(double dx, double dy, double dz, bool fired, double sep, double thr)
        { Dx = dx; Dy = dy; Dz = dz; Fired = fired; SeparationM = sep; ThresholdM = thr; }

        public static readonly Result None = new(0, 0, 0, false, 0, 0);
    }

    /// <summary>
    /// 算出把 <paramref name="acts"/> 整簇搬到 <paramref name="reference"/> 需要的刚性位移。
    ///
    /// <para><b>判定分两步，两步都是被实测数据逼出来的</b>：</para>
    ///
    /// <para>① <b>只要有任何一项落在参照盒里，就一律不动</b>。有一项在矿里，说明这批坐标
    /// 用的就是本矿的坐标系，只是分布散 —— 此时搬整簇会把那项本来正确的坐标也搬错。</para>
    ///
    /// <para>② 否则量**质心到参照盒的距离**。这里换过两次口径，两次都是判据抓出来的：</para>
    /// <list type="bullet">
    ///   <item>第一版量"质心到中心的距离 / 包围盒半径 &gt; 3×"。本矿台账东西跨 6.1 km、
    ///   南北跨 3.8 km，半径 3065 m，而作业簇在盒南 2.7 km —— 图上一眼就是错的，
    ///   按这个口径却只有 1.80×，闸门根本拦不住。狭长矿区把中心距这个度量稀释掉了。</item>
    ///   <item>第二版改量"两个包围盒之间的间距"。结果被**一个离群点**破坏：样例盘子里那个
    ///   北排土场在 OY+2700 处，把作业簇的盒一直顶到台账盒下沿 432 m 处，间距就被稀释成 432 m。
    ///   盒对盒的间距只取决于最靠近的那一个点，不代表整簇在哪。</item>
    /// </list>
    /// <para>质心到盒的距离两个毛病都没有：不受参照系形状影响（量的是盒不是圆），
    /// 也不被单个离群点带偏（质心是全体的）。</para>
    ///
    /// <para><b>不会误伤</b>：场边上真有个排土场、真有个面在坑外时，质心离盒只有几百米，
    /// 低于 <see cref="MinSeparationM"/> 与参照系较短边 <see cref="SpanFraction"/> 的闸门，一律不动。</para>
    /// </summary>
    public static Result Compute(IReadOnlyList<Pt> acts, IReadOnlyList<Pt> reference)
    {
        if (acts == null || acts.Count == 0) return Result.None;
        if (reference == null || reference.Count == 0) return Result.None;

        double rx0 = reference.Min(p => p.X), rx1 = reference.Max(p => p.X);
        double ry0 = reference.Min(p => p.Y), ry1 = reference.Max(p => p.Y);

        double shorter = Math.Min(rx1 - rx0, ry1 - ry0);
        double thr = Math.Max(MinSeparationM, SpanFraction * shorter);

        // ① 盒内点**占多数（≥ InsideShare）** ⇒ 同一个坐标系，不动。
        //
        //  第一版写的是"只要有一项在盒内就不动"。参照系是采掘单元台账（6.1×3.8 km）时没问题，
        //  换成正射影像（8.2×5.8 km）就被一个离群点挟持了：样例盘子里那个北排土场在 OY+2700，
        //  恰好落进影像范围，于是另外 6 个面明明在影像外 2.7 km 也不许搬 —— 图上是"影像在一边、
        //  标记在另一边"。这个离群点已经第三次坏事了（前两次坏的是闸门口径本身）。
        //  改判多数：一两个点在里面不足以说明整簇属于这个坐标系。
        int inside = acts.Count(p => p.X >= rx0 && p.X <= rx1 && p.Y >= ry0 && p.Y <= ry1);
        if (inside >= Math.Max(1, (int)Math.Ceiling(acts.Count * InsideShare)))
            return new Result(0, 0, 0, false, 0, thr);

        // ② 质心到盒的距离
        double mx = acts.Average(p => p.X), my = acts.Average(p => p.Y);
        double sepX = Math.Max(0, Math.Max(rx0 - mx, mx - rx1));
        double sepY = Math.Max(0, Math.Max(ry0 - my, my - ry1));
        double sep = Math.Sqrt(sepX * sepX + sepY * sepY);
        if (sep <= thr) return new Result(0, 0, 0, false, sep, thr);

        double dx = (rx0 + rx1) / 2 - acts.Average(p => p.X);
        double dy = (ry0 + ry1) / 2 - acts.Average(p => p.Y);

        // 高程同理：平面对上了立面还飘着，标记照样不落在台阶上。
        double dz = 0;
        var rz = reference.Where(p => !double.IsNaN(p.Z) && Math.Abs(p.Z) > 1e-6).ToList();
        var az = acts.Where(p => !double.IsNaN(p.Z) && Math.Abs(p.Z) > 1e-6).ToList();
        if (rz.Count > 0 && az.Count > 0)
        {
            double d = rz.Average(p => p.Z) - az.Average(p => p.Z);
            if (Math.Abs(d) > ZTriggerM) dz = d;
        }

        return new Result(dx, dy, dz, true, sep, thr);
    }

    /// <summary>给界面用的说明。只在触发时有内容。</summary>
    public static string Explain(Result r, int placedCount, int refCount)
    {
        if (!r.Fired) return "";
        return $"⚠ 作业面坐标与本矿**不是同一个原点**：{placedCount} 项作业的坐标整簇落在采掘单元台账"
             + $"（{refCount} 个单元的实测坐标）之外 {r.SeparationM / 1000:0.##} km —— "
             + $"每一项都在台账范围外，不是「场边上有个面」（判定闸门 {r.ThresholdM:0} m）。"
             + $"根因是作业面台账没填坐标时任务盘子回落**样例盘子**，样例的基准点写死在代码里，与本矿无关。"
             + $"已按刚性 Δ=({r.Dx:+0;-0} m, {r.Dy:+0;-0} m"
             + (Math.Abs(r.Dz) > 1e-6 ? $", {r.Dz:+0;-0} m" : "") + $") 把整簇搬到台账中心："
             + $"各作业面**相互**的方位与距离原样保留（那是样例真正的内容），"
             + $"但**绝对位置是样例的、不是实测的** —— 别拿它量距离。"
             + $"要让标记落到真位置，在「作业面台账」把各面的 source_x/y/z 填上即可，命名怎么写都不影响。";
    }
}
