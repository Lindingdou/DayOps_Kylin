// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ActivityAnchorTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;
using Xunit.Abstractions;
using Pt = PitMine3D.Kylin.TaskLib.Simulation.ActivityAnchor.Pt;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 作业标记锚点校正的判据（N 组）。
///
/// <para>这一摊是**截图**逼出来的，不是判据逼出来的：65 条判据全绿的那一版，
/// 图上 6 个作业标记浮在采场外几公里的空地上。所以这里的判据分两半 ——
/// 「该动的要动」和「不该动的一定不许动」。后者更重要：一个乱搬位置的校正比不校正糟得多。</para>
/// </summary>
public sealed class ActivityAnchorTests
{
    private readonly ITestOutputHelper _out;
    public ActivityAnchorTests(ITestOutputHelper o) => _out = o;

    /// <summary>本矿实测：采掘单元台账 X 619975…626105、Y 4379132…4382975（2312 个单元）。</summary>
    private static List<Pt> RealSite()
    {
        var pts = new List<Pt>();
        for (int i = 0; i <= 20; i++)
            for (int j = 0; j <= 20; j++)
                pts.Add(new Pt(619975 + 6130.0 * i / 20, 4379132 + 3843.0 * j / 20, 1200 + 4.0 * j));
        return pts;
    }

    /// <summary>样例盘子实测：作业面 X 619200…620350、Y 4375570…4378700。</summary>
    private static List<Pt> SampleFaces() => new()
    {
        new Pt(620320, 4376140, 72), new Pt(620300, 4376040, 60), new Pt(619920, 4375570, 48),
        new Pt(620350, 4375940, 66), new Pt(619960, 4376520, 96), new Pt(619200, 4376120, 68),
        new Pt(620300, 4378700, 110),
    };

    // ── 该动的要动 ────────────────────────────────────────────────────────────

    [Fact]
    public void N1_真实算例必须触发()
    {
        var r = ActivityAnchor.Compute(SampleFaces(), RealSite());
        _out.WriteLine($"间距 {r.SeparationM:0} m　闸门 {r.ThresholdM:0} m　Δ=({r.Dx:0}, {r.Dy:0}, {r.Dz:0})");
        Assert.True(r.Fired, "真实算例（样例盘子 vs 本矿台账）必须判为不同原点 —— 这正是截图上看到的那一版。");
        Assert.True(r.SeparationM > 400, $"间距应有几百米以上，实得 {r.SeparationM:0}");
    }

    /// <summary>
    /// 这条是**闸门写法本身**的判据。第一版闸门量的是"到中心距离 / 包围盒半径 &gt; 3"，
    /// 在这个狭长矿区上只有 1.80×，一次都没触发 —— 判据全绿、图上全错。
    /// 换成"到盒的间距"之后才拦得住。这条钉的就是"别再退回中心距那种写法"。
    /// </summary>
    [Fact]
    public void N2_狭长矿区不能把间距稀释掉()
    {
        var site = RealSite();
        double cx = (site.Min(p => p.X) + site.Max(p => p.X)) / 2;
        double cy = (site.Min(p => p.Y) + site.Max(p => p.Y)) / 2;
        double radius = Math.Max(site.Max(p => p.X) - site.Min(p => p.X),
                                 site.Max(p => p.Y) - site.Min(p => p.Y)) / 2;

        var acts = SampleFaces();
        double ratio = Math.Sqrt(Math.Pow(acts.Average(p => p.X) - cx, 2)
                               + Math.Pow(acts.Average(p => p.Y) - cy, 2)) / radius;
        _out.WriteLine($"若按旧写法：质心距 / 半径 = {ratio:0.00}×（旧闸门 3× ⇒ 不触发）");
        Assert.True(ratio < 3.0, "算例前提：旧写法在这组数上确实拦不住（否则这条判据是空的）");
        Assert.True(ActivityAnchor.Compute(acts, site).Fired, "新写法必须拦得住");
    }

    /// <summary>
    /// 第二个错口径的判据。改成"两个包围盒之间的间距"之后仍然拦不住 ——
    /// 样例盘子里那个北排土场（OY+2700）把作业簇的盒一直顶到台账盒下沿 432 m 处，
    /// 盒间距被这**一个离群点**稀释到闸门以下。盒对盒只取决于最近的那一个点，不代表整簇在哪。
    /// 这条钉的是"别再退回盒间距那种写法"。
    /// </summary>
    [Fact]
    public void N2b_单个离群点不能把间距稀释掉()
    {
        var site = RealSite();
        var acts = SampleFaces();

        double ax1 = acts.Max(p => p.Y), ry0 = site.Min(p => p.Y);
        double boxSep = Math.Max(0, ry0 - ax1);          // 旧写法：盒对盒
        var r = ActivityAnchor.Compute(acts, site);      // 新写法：质心对盒
        _out.WriteLine($"盒对盒 = {boxSep:0} m（旧写法）　质心对盒 = {r.SeparationM:0} m（新写法）　闸门 {r.ThresholdM:0} m");

        Assert.True(boxSep < r.ThresholdM,
            "算例前提：旧写法在这组数上确实被离群点稀释到闸门以下（否则这条判据是空的）");
        Assert.True(r.Fired, "新写法必须拦得住");
        Assert.True(r.SeparationM > 5 * boxSep, "质心口径要明显不受那个离群点影响");
    }

    [Fact]
    public void N3_位移是刚性的_两两距离一点不变()
    {
        var acts = SampleFaces();
        var r = ActivityAnchor.Compute(acts, RealSite());
        Assert.True(r.Fired);

        var moved = acts.Select(p => new Pt(p.X + r.Dx, p.Y + r.Dy, p.Z + r.Dz)).ToList();
        double worst = 0;
        for (int i = 0; i < acts.Count; i++)
            for (int j = i + 1; j < acts.Count; j++)
            {
                double a = Dist(acts[i], acts[j]), b = Dist(moved[i], moved[j]);
                worst = Math.Max(worst, Math.Abs(a - b));
            }
        _out.WriteLine($"两两距离最大变化 {worst:0.000000} m");
        Assert.True(worst < 1e-6, $"必须是刚性平移，实测最大变形 {worst} m —— 一旦有缩放/旋转就是在编造位置");
    }

    [Fact]
    public void N4_搬完之后质心落在参照盒里()
    {
        var site = RealSite();
        var r = ActivityAnchor.Compute(SampleFaces(), site);
        double mx = SampleFaces().Average(p => p.X) + r.Dx;
        double my = SampleFaces().Average(p => p.Y) + r.Dy;
        _out.WriteLine($"搬后质心 ({mx:0}, {my:0})");
        Assert.InRange(mx, site.Min(p => p.X), site.Max(p => p.X));
        Assert.InRange(my, site.Min(p => p.Y), site.Max(p => p.Y));
    }

    // ── 不该动的一定不许动 ────────────────────────────────────────────────────

    [Fact]
    public void N5_同一原点的作业一律不动()
    {
        var site = RealSite();
        var acts = new List<Pt>
        {
            new(621000, 4380000, 1210), new(623500, 4381500, 1230), new(625000, 4380500, 1220),
        };
        var r = ActivityAnchor.Compute(acts, site);
        _out.WriteLine($"间距 {r.SeparationM:0} m　闸门 {r.ThresholdM:0} m　触发 {r.Fired}");
        Assert.False(r.Fired, "作业就在台账范围内，绝不该被搬");
        Assert.Equal(0, r.Dx); Assert.Equal(0, r.Dy); Assert.Equal(0, r.Dz);
    }

    /// <summary>场边上真有个排土场 / 真有个面在坑外 —— 这是常态，不许误伤。</summary>
    [Fact]
    public void N6_场边上几百米的作业不许误伤()
    {
        var site = RealSite();
        double ry1 = site.Max(p => p.Y);
        foreach (double outM in new[] { 100.0, 300.0, 600.0 })
        {
            var acts = new List<Pt>
            {
                new(622000, ry1 + outM, 1215), new(623000, ry1 + outM + 120, 1218),
            };
            var r = ActivityAnchor.Compute(acts, site);
            _out.WriteLine($"出界 {outM:0} m ⇒ 间距 {r.SeparationM:0}　闸门 {r.ThresholdM:0}　触发 {r.Fired}");
            Assert.False(r.Fired, $"整簇只出界 {outM} m，是正常的场外作业，不该判为不同原点");
        }
    }

    /// <summary>
    /// 盒内点占多数（≥1/3）⇒ 同一个坐标系，整簇不许搬。
    ///
    /// <para>这条原来写的是"只要有一项在盒内就不搬"。参照系是采掘单元台账时够用，
    /// 换成正射影像（8.2×5.8 km，比台账大一圈）立刻被一个离群点挟持：
    /// 样例盘子那个北排土场恰好落进影像范围，于是另外 6 个面明明在影像外 2.7 km 也不许搬，
    /// 图上是"影像在一边、标记在另一边"。一两个点在里面不足以说明整簇属于这个坐标系。</para>
    /// </summary>
    [Fact]
    public void N7_盒内点占多数才算同一坐标系()
    {
        var site = RealSite();

        // ① 少数在内（1/8）⇒ 仍判不同原点，整簇要搬
        var few = SampleFaces().ToList();
        few.Add(new Pt(622000, 4380500, 1215));
        var r1 = ActivityAnchor.Compute(few, site);
        _out.WriteLine($"1/{few.Count} 在内 ⇒ 触发 {r1.Fired}");
        Assert.True(r1.Fired, "一个离群点不该挟持整簇");

        // ② 多数在内（4/7）⇒ 同一坐标系，不许搬
        var many = SampleFaces().Take(3).ToList();
        many.AddRange(new[]
        {
            new Pt(621000, 4380000, 1210), new Pt(622500, 4380800, 1215),
            new Pt(624000, 4381500, 1220), new Pt(625000, 4382000, 1225),
        });
        var r2 = ActivityAnchor.Compute(many, site);
        _out.WriteLine($"4/{many.Count} 在内 ⇒ 触发 {r2.Fired}");
        Assert.False(r2.Fired, "多数点在参照范围内 ⇒ 就是这个坐标系，搬了反而把真坐标搬错");
    }

    [Fact]
    public void N8_参照系为空时什么都不做()
    {
        Assert.False(ActivityAnchor.Compute(SampleFaces(), new List<Pt>()).Fired);
        Assert.False(ActivityAnchor.Compute(new List<Pt>(), RealSite()).Fired);
        Assert.False(ActivityAnchor.Compute(SampleFaces(), null!).Fired);
        Assert.False(ActivityAnchor.Compute(null!, RealSite()).Fired);
    }

    // ── 高程 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void N9_高程差大时一并平移_差小时不动()
    {
        var site = RealSite();                       // Z ≈ 1200…1280
        var far = ActivityAnchor.Compute(SampleFaces(), site);      // 样例 Z 48…110，差 ~1130 m
        _out.WriteLine($"高程差大：Δz = {far.Dz:0} m");
        Assert.True(Math.Abs(far.Dz) > 500, "高程差上千米时必须一并平移，否则标记悬在地形上方");

        // 同样的平面错位，但高程本来就对得上 ⇒ 只动平面
        var near = SampleFaces().Select(p => new Pt(p.X, p.Y, 1240)).ToList();
        var r = ActivityAnchor.Compute(near, site);
        _out.WriteLine($"高程差小：Δz = {r.Dz:0} m");
        Assert.True(r.Fired, "平面仍然错位，仍须触发");
        Assert.Equal(0, r.Dz);
    }

    /// <summary>说明文只在触发时有内容，且必须把"这是样例位置"说清楚 —— 不能让人拿它量距离。</summary>
    [Fact]
    public void N10_说明文该有的话必须有()
    {
        Assert.Equal("", ActivityAnchor.Explain(ActivityAnchor.Result.None, 7, 2312));

        var r = ActivityAnchor.Compute(SampleFaces(), RealSite());
        string s = ActivityAnchor.Explain(r, 7, 2312);
        _out.WriteLine(s);
        Assert.Contains("不是同一个原点", s);
        Assert.Contains("样例", s);
        Assert.Contains("不是实测", s);
        Assert.Contains("source_x", s);
    }

    private static double Dist(Pt a, Pt b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
