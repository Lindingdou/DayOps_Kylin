using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「修复拓扑关系」参数面板 6 个开关的回归：每个开关都必须真的管用（关掉就不做、打开才做），
/// 免得像原版那样面板上摆着、底下走的还是默认值。
/// </summary>
public class MeshRepairOptionsTests
{
    // 单位立方体缺顶面：4 条开放边
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) OpenBox()
    {
        var v = new List<(double, double, double)>
        { (0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1) };
        var t = new List<(int, int, int)>
        {
            (0,1,2),(0,2,3), (0,1,5),(0,5,4), (2,3,7),(2,7,6), (0,3,7),(0,7,4), (1,2,6),(1,6,5),
        };
        return (v, t);
    }

    [Fact]
    public void 补洞开关_打开才补上顶面()
    {
        var (v, t) = OpenBox();
        Assert.Equal(4, MeshDiagnose.Analyze(v, t).BoundaryEdges);

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options { FillHoles = false });
        Assert.Equal(0, off.FilledHoles);
        Assert.Equal(4, off.BoundaryAfter);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options { FillHoles = true });
        Assert.True(on.FilledHoles >= 1);
        Assert.Equal(0, on.BoundaryAfter);
    }

    [Fact]
    public void 退化面开关_打开才删掉零面积三角()
    {
        var (v, t) = OpenBox();
        v.Add((0.5, 0, 0));                       // 落在边 (0,0,0)-(1,0,0) 上
        t.Add((0, 1, v.Count - 1));               // 三点共线 → 零面积

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveDegenerate = false, WeldVertices = false, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.Equal(0, off.RemovedDegenerate);
        Assert.Equal(t.Count, off.Tris.Count);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveDegenerate = true, WeldVertices = false, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.Equal(1, on.RemovedDegenerate);
        Assert.Equal(t.Count - 1, on.Tris.Count);
    }

    [Fact]
    public void 焊接开关_打开才合并重合顶点()
    {
        var (v, t) = OpenBox();
        int dup = v.Count; v.Add(v[0]);           // 与 0 号点完全重合
        t.Add((dup, 1, 2));                       // 用重复点再画一个面

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { WeldVertices = false, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.Equal(0, off.WeldedVertices);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { WeldVertices = true, FillHoles = false, RemoveIsolated = false, SplitNonManifold = false });
        Assert.True(on.WeldedVertices >= 1);
    }

    [Fact]
    public void 孤立点开关_打开才移除未被引用的顶点()
    {
        var (v, t) = OpenBox();
        v.Add((99, 99, 99));                      // 谁都不用它

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveIsolated = false, WeldVertices = false, FillHoles = false, SplitNonManifold = false });
        Assert.Equal(0, off.RemovedIsolated);
        Assert.Equal(v.Count, off.Verts.Count);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { RemoveIsolated = true, WeldVertices = false, FillHoles = false, SplitNonManifold = false });
        Assert.Equal(1, on.RemovedIsolated);
        Assert.Equal(v.Count - 1, on.Verts.Count);
    }

    [Fact]
    public void 非流形开关_打开才把三片共边拆开()
    {
        // 一条边 (0-1) 上挂 3 个三角 → 非流形边
        var v = new List<(double x, double y, double z)>
        { (0,0,0),(1,0,0),(0,1,0),(0,-1,0),(0,0,1) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 1, 3), (0, 1, 4) };
        Assert.Equal(1, MeshDiagnose.Analyze(v, t).NonManifoldEdges);

        var off = MeshRepair.Repair(v, t, new MeshRepair.Options
        { SplitNonManifold = false, WeldVertices = false, FillHoles = false, RemoveIsolated = false });
        Assert.Equal(0, off.SplitNonManifoldEdges);
        Assert.Equal(1, MeshDiagnose.Analyze(off.Verts, off.Tris).NonManifoldEdges);

        var on = MeshRepair.Repair(v, t, new MeshRepair.Options
        { SplitNonManifold = true, WeldVertices = false, FillHoles = false, RemoveIsolated = false });
        Assert.Equal(1, on.SplitNonManifoldEdges);
        Assert.Equal(0, MeshDiagnose.Analyze(on.Verts, on.Tris).NonManifoldEdges);
        Assert.Equal(3, on.Tris.Count);           // 面不减, 只是把顶点复制开
    }

    [Fact]
    public void 全关_什么都不动()
    {
        var (v, t) = OpenBox();
        var r = MeshRepair.Repair(v, t, new MeshRepair.Options
        {
            RemoveDegenerate = false, WeldVertices = false, FillHoles = false,
            RemoveIsolated = false, SplitNonManifold = false, FlipInverted = false,
        });
        Assert.Equal(0, r.TotalChanges);
        Assert.Equal(t.Count, r.Tris.Count);
        Assert.Equal(v.Count, r.Verts.Count);
    }
}

/// <summary>
/// 修复拓扑在地形面上的护栏（用户实测「很慢、执行后直接卡死」）：
/// 外轮廓是个大"洞"，不设面积上限就用扇面把它封死 —— 几千个横贯整张图的巨三角，随后的自交检测逐个落格直接卡死。
/// 现在只补 XY 面积 ≤ MaxHoleArea(原版 1e6) 的小洞、修复前后诊断不做自交检测、巨三角在自交网格里单独处理。
/// </summary>
public class MeshRepairTerrainTests
{
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Terrain(int n, double size, params (int i, int j)[] holes)
    {
        var v = new List<(double x, double y, double z)>(); var t = new List<(int a, int b, int c)>();
        double step = size / n;
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) v.Add((500000 + i * step, 4000000 + j * step, 1200 + 10 * System.Math.Sin(i * 0.2) * System.Math.Cos(j * 0.15)));
        var skip = new HashSet<(int, int)>(holes);
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            if (skip.Contains((i, j))) continue;   // 挖掉一格 → 一个 4 边小洞
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            t.Add((a, b, d)); t.Add((a, d, c));
        }
        return (v, t);
    }

    [Fact]
    public void 地形面_只补小洞_外轮廓不封_秒级完成()
    {
        var (v, t) = Terrain(600, 3000, (100, 100), (300, 250), (450, 500));   // 72 万三角, 3 个小洞
        int outer = 4 * 600;   // 外轮廓开放边
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = MeshRepair.Repair(v, t, new MeshRepair.Options());
        sw.Stop();
        Assert.Equal(3, r.FilledHoles);
        Assert.Equal(3 * 4, r.FilledFaces);                 // 每洞 4 条边 → 4 个扇面
        Assert.Equal(outer + 12, r.BoundaryBefore);
        Assert.Equal(outer, r.BoundaryAfter);               // 外轮廓照旧开放
        Assert.Equal(0, r.FlippedFaces);                    // 翻转开关默认关: 一根不翻
        Assert.Equal(t.Take(5).ToList(), r.Tris.Take(5).ToList());   // 原三角绕向原样
        Assert.True(sw.ElapsedMilliseconds < 20000, $"72 万三角修复用了 {sw.ElapsedMilliseconds} ms（实测约 2 s）");
    }

    [Fact]
    public void 诊断_巨三角不拖死自交检测()
    {
        var (v, t) = Terrain(150, 1000);   // 4.5 万三角(在自交检测上限内)
        // 追加几百个"扇面巨三角": 从外轮廓顶点到中心, 横贯整张图
        int c = v.Count; v.Add((500500, 4000500, 1200));
        for (int i = 0; i < 150; i += 1) t.Add((i, i + 1, c));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = MeshDiagnose.Analyze(v, t);
        sw.Stop();
        Assert.True(d.SelfIntersectTriangles > 0);          // 扇面横切地形, 必有自交
        Assert.True(sw.ElapsedMilliseconds < 30000, $"带巨三角的自交检测用了 {sw.ElapsedMilliseconds} ms");
    }
}
