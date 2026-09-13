// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/CenterlineJunctionTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 「捕捉交点」（手动标定线路时把点吸到路口上）的口径。
///
/// <b>本组判的是一件事</b>：能吸上的点，必须正好是<b>建网会打出节点</b>的那些位置
/// （<see cref="RoadGraphBuilder"/> noding 的 X 十字 / T 丁字 / 半腰焊 / 接缝四条规则），
/// 一条不多一条不少 —— 否则用户吸得挺准、建网却不在那里连通，"为什么这条路还是不通"就永远说不清。
///
/// 每条闸门都配一个对照组（把闸门放开必须真的翻过来），免得判据空过（同 [[judgment-discipline]]）：
/// S2/S4/S5 是容差与立交闸的对照，S7/S13 是"不该出的别出"。
/// </summary>
public class CenterlineJunctionTests
{
    private static double[] L(params double[] xyz) => xyz;

    private static CenterlineJunctionSet Build(params double[][] lines)
        => CenterlineJunctions.Build(new List<double[]>(lines));

    // ── ① X 十字 ────────────────────────────────────────────────────────────

    [Fact]
    public void S1_十字相交_出一个X形交点且位置精确()
    {
        var set = Build(
            L(-50, 0, 100, 50, 0, 100),      // 东西向
            L(0, -50, 100, 0, 50, 100));     // 南北向

        var j = Assert.Single(set.All);
        Assert.Equal(JunctionKind.Cross, j.Kind);
        Assert.Equal(0.0, j.X, 6);
        Assert.Equal(0.0, j.Y, 6);
        Assert.False(j.GradeSeparated);
        Assert.Equal(1, set.CrossCount);
    }

    [Fact]
    public void S2_立交进表但打标_对照组同处平交不打标()
    {
        // 平面同一处交叉，只有标高不同：20m 高差 = 上下台阶，建网刻意不在此打断。
        var over = Build(
            L(-50, 0, 100, 50, 0, 100),
            L(0, -50, 120, 0, 50, 120));
        var jo = Assert.Single(over.All);
        Assert.True(jo.GradeSeparated);              // 打标：捕捉得到，但明说"建网不在此连通"
        Assert.Equal(1, over.GradeSeparatedCount);
        Assert.Equal(20.0, jo.DzM, 6);

        // 对照组：同一几何、高差 1m（闸门内）→ 必须翻过来判成平交
        var flat = Build(
            L(-50, 0, 100, 50, 0, 100),
            L(0, -50, 101, 0, 50, 101));
        Assert.False(Assert.Single(flat.All).GradeSeparated);
        Assert.Equal(0, flat.GradeSeparatedCount);
    }

    [Fact]
    public void S13_一条线自己打结_不记成交点()
    {
        // 自交是画废的线，不是路口；建网的 noding 也只判 i<j 两条不同的线。
        var set = Build(L(0, 0, 100, 40, 0, 100, 40, 40, 100, 20, -20, 100));
        Assert.Empty(set.All);
    }

    // ── ② T 丁字 ────────────────────────────────────────────────────────────

    [Fact]
    public void S3_端点贴在另一条身上_交点取垂足而不是那个端点()
    {
        // 支线端点 (20, 3) 离干线 3m —— 建网是把干线在垂足 (20,0) 打断，捕捉就得吸到 (20,0)。
        var set = Build(
            L(0, 0, 100, 100, 0, 100),
            L(20, 3, 100, 20, 60, 100));

        var j = Assert.Single(set.All);
        Assert.Equal(JunctionKind.Tee, j.Kind);
        Assert.Equal(20.0, j.X, 6);
        Assert.Equal(0.0, j.Y, 6);                   // 垂足，不是端点的 y=3
        Assert.Equal(3.0, j.GapM, 6);
    }

    [Fact]
    public void S4_端点离得超容差_不出交点_放开容差才出()
    {
        var lines = new List<double[]>
        {
            L(0, 0, 100, 100, 0, 100),
            L(20, 8, 100, 20, 60, 100),              // 缝宽 8m > 默认 5m
        };
        Assert.Empty(CenterlineJunctions.Build(lines).All);
        Assert.Single(CenterlineJunctions.Build(lines, contactTolM: 10.0).All);   // 对照组：闸门放开真的翻过来
    }

    [Fact]
    public void S3b_T形也过立交闸_上下台阶不算路口只是打标()
    {
        var set = Build(
            L(0, 0, 100, 100, 0, 100),
            L(20, 3, 130, 20, 60, 130));            // 平面贴上，高差 30m
        var j = Assert.Single(set.All);
        Assert.Equal(JunctionKind.Tee, j.Kind);
        Assert.True(j.GradeSeparated);
    }

    // ── ③ 半腰焊 ────────────────────────────────────────────────────────────

    [Fact]
    public void S5_两条擦身而过的线_半腰贴上出交点_离远了不出()
    {
        // 都不相交、最近处落在**两条各自的中间折点**上：这正是建网 R-N3 补的那条
        // （现场 226 处，最狠一处擦身 4.4m、图上要绕 6594m）。X 规则要真相交、T 规则只投端点，
        // 两条都看不见它 —— 所以捕捉这边也必须单列一条，否则那种地方永远吸不上。
        double[] a = L(0, 0, 100, 50, 0, 100, 100, 0, 100);
        double[] b = L(0, 30, 100, 50, 3, 100, 100, 30, 100);   // 中间折点探到 3m 处又拐走
        var near = Build(a, b);
        var j = Assert.Single(near.All);
        Assert.Equal(JunctionKind.MidWeld, j.Kind);
        Assert.Equal(3.0, j.GapM, 6);
        Assert.Equal(50.0, j.X, 6);

        var far = Build(a, L(0, 30, 100, 50, 8, 100, 100, 30, 100));   // 对照组：只探到 8m > 容差
        Assert.Empty(far.All);
    }

    [Fact]
    public void S5b_平行叠着跑的两条线_出两个T形接点_不是一个()
    {
        // 一条从另一条半腰起步、平行错开 3m 跑出去：两处端点各自贴在对方身上，
        // 建网也是在这两处各打一断 —— 捕捉给两个点是对的，别为了"看着干净"合成一个。
        var set = Build(
            L(0, 0, 100, 100, 0, 100),
            L(50, 3, 100, 150, 3, 100));

        Assert.Equal(2, set.All.Count);
        Assert.All(set.All, x => Assert.Equal(JunctionKind.Tee, x.Kind));
        Assert.Contains(set.All, x => System.Math.Abs(x.X - 50) < 1e-6 && System.Math.Abs(x.Y) < 1e-6);
        Assert.Contains(set.All, x => System.Math.Abs(x.X - 100) < 1e-6 && System.Math.Abs(x.Y - 3) < 1e-6);
    }

    // ── ④ 接缝（端点碰端点）────────────────────────────────────────────────

    [Fact]
    public void S6_两条线端点碰在一起_出接缝且落在中点()
    {
        var set = Build(
            L(0, 0, 100, 100, 0, 100),
            L(102, 0, 100, 200, 0, 100));            // 缝宽 2m

        var j = Assert.Single(set.All);
        Assert.Equal(JunctionKind.Seam, j.Kind);
        Assert.Equal(101.0, j.X, 6);
        Assert.Equal(2.0, j.GapM, 6);
        Assert.Equal(1, set.SeamCount);
    }

    [Fact]
    public void S7_端点处相交的两条线_记成接缝而不是X形()
    {
        // 建网把"端点相交"让给端点吸附（SegCross2D 刻意排掉端点参数），捕捉必须同口径 —— 否则
        // 同一处会既算 X 又算接缝，交点表里凭空多出一半。
        var set = Build(
            L(0, 0, 100, 50, 0, 100),
            L(50, 0, 100, 50, 50, 100));

        var j = Assert.Single(set.All);
        Assert.Equal(JunctionKind.Seam, j.Kind);
        Assert.Equal(50.0, j.X, 6);
        Assert.Equal(0.0, j.Y, 6);
    }

    // ── ⑤ 合并 / 定序 / 半径 ────────────────────────────────────────────────

    [Fact]
    public void S8_四条支路汇于一点_合并成一个交点而不是六个()
    {
        // 四条线两两端点相碰 = C(4,2) 中共 6 对贴上；不合并的话一个路口会摆出六个几乎同位的捕捉点，
        // 吸到哪一个全看运气。
        var set = Build(
            L(0, 0, 100, -50, 0, 100),
            L(0, 0, 100, 50, 0, 100),
            L(0, 0, 100, 0, -50, 100),
            L(0, 0, 100, 0, 50, 100));

        var j = Assert.Single(set.All);
        Assert.Equal(0.0, j.X, 6);
        Assert.Equal(0.0, j.Y, 6);
        Assert.True(set.MergedCount > 0);            // 合并掉的要记账，不能悄悄消失
    }

    [Fact]
    public void S10_输入顺序颠倒_留下的代表点不变()
    {
        // 合并是"先到先得"，没有全序的话线序一变留下来的点就变 —— 那样同一张图两次打开会吸到不同位置。
        var forward = Build(
            L(0, 0, 100, -50, 0, 100),
            L(0, 0, 100, 50, 0, 100),
            L(0, 0, 100, 0, 50, 100));
        var backward = Build(
            L(0, 0, 100, 0, 50, 100),
            L(0, 0, 100, 50, 0, 100),
            L(0, 0, 100, -50, 0, 100));

        Assert.Equal(forward.Count, backward.Count);
        Assert.Equal(forward.All[0].X, backward.All[0].X, 6);
        Assert.Equal(forward.All[0].Y, backward.All[0].Y, 6);
    }

    [Fact]
    public void S9_捕捉半径外一律不吸_恰在半径上算命中()
    {
        var set = Build(
            L(-50, 0, 100, 50, 0, 100),
            L(0, -50, 100, 0, 50, 100));

        Assert.Null(set.Nearest(30, 0, 12.0, out _));                 // 30m 外：点在空处不能顺手吸到路口
        var hit = set.Nearest(8, 0, 12.0, out double d);
        Assert.NotNull(hit);
        Assert.Equal(8.0, d, 6);
        Assert.NotNull(set.Nearest(12, 0, 12.0, out _));              // 恰好落在半径上 = 命中
        Assert.Null(set.Nearest(12.0001, 0, 12.0, out _));
    }

    [Fact]
    public void S9b_半径按平面判_取点Z是0也照样吸得上()
    {
        // 视口取点在纯线框视图下 Z≈0，而路面在 1100m 以上：判距掺 Z 就等于永远吸不上（见 road-snap-to-surface-not-node）。
        var set = Build(
            L(-50, 0, 1128, 50, 0, 1128),
            L(0, -50, 1128, 0, 50, 1128));
        Assert.NotNull(set.Nearest(3, 0, 12.0, out _));
    }

    // ── ⑥ 交点给出的标高 ────────────────────────────────────────────────────

    [Fact]
    public void S11_平交取两线中值_立交取离点击近的那层()
    {
        var flat = Build(
            L(-50, 0, 100, 50, 0, 100),
            L(0, -50, 102, 0, 50, 102));
        Assert.Equal(101.0, Assert.Single(flat.All).ZAt(0.0), 6);      // 平交：中值

        var over = Build(
            L(-50, 0, 1100, 50, 0, 1100),
            L(0, -50, 1130, 0, 50, 1130));
        var j = Assert.Single(over.All);
        Assert.Equal(1130.0, j.ZAt(1128.0), 6);                        // 点击落在上层附近 → 取上层
        Assert.Equal(1100.0, j.ZAt(0.0), 6);                           // Z 不可信(=0) → 恒取低层，故调用方必须回显取的是哪层
    }

    // ── ⑥b 路口 vs 接缝（真实网上唯一分得开这两种的量）────────────────────────

    [Fact]
    public void S15_三条中线端点碰头是路口_两条只是接缝()
    {
        // 现场那张网 1080 条中线里 X 形只有 1 处，路口全长成"几条线的端点碰在一起"的样子；
        // 而"一条直路被打断"留下的接头长得一模一样，只差在碰头的**条数**上（度 2 vs ≥3）。
        var seam = Build(
            L(0, 0, 100, -50, 0, 100),
            L(0, 0, 100, 50, 0, 100));
        var s0 = Assert.Single(seam.All);
        Assert.Equal(2, s0.LineCount);
        Assert.False(s0.IsRealJunction);           // 度 2 = 接缝，不是路口
        Assert.Equal(1, seam.SeamCount);
        Assert.Equal(0, seam.JunctionCount);

        var junc = Build(
            L(0, 0, 100, -50, 0, 100),
            L(0, 0, 100, 50, 0, 100),
            L(0, 0, 100, 0, 50, 100));           // 多出来的第三条把它变成真路口
        var j0 = Assert.Single(junc.All);
        Assert.Equal(3, j0.LineCount);
        Assert.True(j0.IsRealJunction);
        Assert.Equal(1, junc.ConfluenceCount);
        Assert.Equal(0, junc.SeamCount);
        Assert.Contains("3路汇合口", j0.KindLabel);
    }

    [Fact]
    public void S16_半径内先挑路口_接缝再近也不夺_没有路口时照吸接缝()
    {
        // 接缝在真实网上有二百多个、彼此才隔十几米；纯"取最近"等于每次都吸到一条缝上。
        var set = Build(
            L(0, 0, 100, -50, 0, 100),           // ↘ 路口在 (0,0)：三条线碰头
            L(0, 0, 100, 50, 0, 100),
            L(0, 0, 100, 0, 50, 100),
            L(0, -30, 100, 0, -80, 100),         // ↘ 接缝在 (0,-30)：只有两条线碰头
            L(0, -30, 100, 60, -30, 100));

        var hit = set.Nearest(0, -20, 40.0, out double d);   // 点在两者之间：接缝 10m、路口 20m
        Assert.NotNull(hit);
        Assert.True(hit!.IsRealJunction);                    // 接缝更近也不夺 —— 仍然吸路口
        Assert.Equal(20.0, d, 6);

        var near = set.Nearest(0, -28, 12.0, out _);         // 对照组：半径内根本没有路口
        Assert.NotNull(near);
        Assert.False(near!.IsRealJunction);                  // 该退给接缝时真的退（并由调用方回显"这是接缝"）
    }

    // ── ⑦ 与建网对表（本组的地基）───────────────────────────────────────────

    [Fact]
    public void S14_每个可捕捉交点处_建网真有一个节点_立交处真没有()
    {
        // 这条才是"捕捉交点"敢承诺的东西：吸上去 = 建网会在那儿打节点。
        // 一旦捕捉这边和 RoadGraphBuilder 的四条 noding 规则漂开，用户吸得挺准、路网照样不连通，
        // 而两边都不会报错 —— 只能靠这条对表钉住。容差两边必须用同一个（插件传的是 DefaultSnapToleranceM=10）。
        const double Tol = 10.0;
        var lines = new List<double[]>
        {
            L(-100, 0, 100, 100, 0, 100),                 // 干线（东西）
            L(0, -100, 100, 0, 100, 100),                 // X 十字（同高）
            L(60, 4, 100, 60, 80, 100),                   // T 丁字：端点离干线 4m
            L(-60, -100, 130, -60, 100, 130),             // 立交：平面穿过干线，高差 30m
        };

        var set = CenterlineJunctions.Build(lines, contactTolM: Tol, gradeSeparationM: 4.0);
        Assert.Contains(set.All, j => j.Kind == JunctionKind.Cross && !j.GradeSeparated);
        Assert.Contains(set.All, j => j.Kind == JunctionKind.Tee);
        Assert.Contains(set.All, j => j.GradeSeparated);

        var graph = RoadGraphBuilder.FromPolylines(lines.Select(ToPoints), snapToleranceM: Tol);

        foreach (var j in set.All)
        {
            bool hasNode = graph.Nodes.Any(n => Math.Abs(n.Position.X - j.X) <= Tol && Math.Abs(n.Position.Y - j.Y) <= Tol);
            if (j.GradeSeparated)
                Assert.False(hasNode, $"立交处不该有节点：({j.X:F1},{j.Y:F1})");   // 对照组：闸门真的挡住了
            else
                Assert.True(hasNode, $"{j.KindLabel} 处建网没打节点：({j.X:F1},{j.Y:F1})");
        }
    }

    private static IReadOnlyList<Point3d> ToPoints(double[] flat)
    {
        var pts = new Point3d[flat.Length / 3];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Point3d(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]);
        return pts;
    }

    // ── ⑧ 退化输入 ──────────────────────────────────────────────────────────

    [Fact]
    public void S12_空输入与退化线_不炸也不出交点()
    {
        Assert.Empty(CenterlineJunctions.Build(null).All);
        Assert.Empty(CenterlineJunctions.Build(new List<double[]>()).All);
        Assert.Empty(CenterlineJunctions.Build(new List<double[]>
        {
            new double[] { 0, 0, 0 },                 // 单点，成不了段
            new double[0],
            L(0, 0, 100, 0, 0, 100),                  // 零长段
        }).All);
    }
}
