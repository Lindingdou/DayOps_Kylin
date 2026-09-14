using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.SeamOutcrop;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 煤层露头着色（§三四一）：露头 = 现状面高程夹在【底板 ≤ 现状 ≤ 顶板】的区域。
///
/// 盯三处：**Z 采样器的重心插值与盖不到时的 false**、**逐顶点分类的层序优先**、
/// 以及 **"判不了"不冒充"不是露头"**（nodata 单独计数，不当成"不在露头里"混过去）。
/// </summary>
public class SeamOutcropTests
{
    /// <summary>造一张水平面：[x0,x1]×[y0,y1] 的两个三角形，全在标高 z。</summary>
    private static (double[] v, int[] t) Plane(double z, double x0 = 0, double y0 = 0, double x1 = 100, double y1 = 100)
        => (new[] { x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z },
            new[] { 0, 1, 2, 0, 2, 3 });

    /// <summary>造一张沿 X 线性倾斜的面：x=x0 处 zAtX0，x=x1 处 zAtX1。</summary>
    private static (double[] v, int[] t) Ramp(double zAtX0, double zAtX1, double x0 = 0, double y0 = 0, double x1 = 100, double y1 = 100)
        => (new[] { x0, y0, zAtX0, x1, y0, zAtX1, x1, y1, zAtX1, x0, y1, zAtX0 },
            new[] { 0, 1, 2, 0, 2, 3 });

    // ── Z 采样器 ────────────────────────────────────────────
    [Fact]
    public void 采样_水平面上任意点都返回该标高()
    {
        var (v, t) = Plane(37.5);
        var s = new MeshZSampler(v, t);
        Assert.False(s.IsEmpty);
        foreach (var (x, y) in new[] { (1.0, 1.0), (50.0, 50.0), (99.0, 2.0), (0.0, 0.0) })
        {
            Assert.True(s.TrySampleZ(x, y, out double z), $"({x},{y}) 应该采得到");
            Assert.Equal(37.5, z, 9);
        }
    }

    [Fact]
    public void 采样_斜面按重心插值()
    {
        var (v, t) = Ramp(0, 100);          // 每米 X 抬高 1 m
        var s = new MeshZSampler(v, t);
        Assert.True(s.TrySampleZ(25, 50, out double z));
        Assert.Equal(25.0, z, 6);
        Assert.True(s.TrySampleZ(80, 10, out double z2));
        Assert.Equal(80.0, z2, 6);
    }

    [Fact]
    public void 采样_盖不到的地方返回假而不是零()
    {
        // ★ 关键：返回 0 会被上层当成"标高 0 的真实地面"
        var (v, t) = Plane(50, 0, 0, 10, 10);
        var s = new MeshZSampler(v, t);
        Assert.False(s.TrySampleZ(500, 500, out _));
        Assert.False(s.TrySampleZ(-5, 5, out _));
    }

    [Fact]
    public void 采样_空网标记为空且采不出东西()
    {
        Assert.True(new MeshZSampler(Array.Empty<double>(), Array.Empty<int>()).IsEmpty);
        Assert.True(new MeshZSampler(new double[] { 0, 0, 0 }, Array.Empty<int>()).IsEmpty);
        Assert.False(new MeshZSampler(Array.Empty<double>(), Array.Empty<int>()).TrySampleZ(0, 0, out _));
    }

    [Fact]
    public void 采样_包围盒对得上()
    {
        var (v, t) = Plane(0, -10, -20, 30, 40);
        var s = new MeshZSampler(v, t);
        Assert.Equal(-10, s.MinX, 9);
        Assert.Equal(-20, s.MinY, 9);
        Assert.Equal(30, s.MaxX, 9);
        Assert.Equal(40, s.MaxY, 9);
    }

    [Fact]
    public void 采样_大网也采得准()
    {
        // 顺带压一下 CSR 索引：格网分块之后仍要逐点采对
        const int n = 60;
        var verts = new List<double>();
        var tris = new List<int>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            { verts.Add(i); verts.Add(j); verts.Add(i * 0.5); }   // z = x/2
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                tris.AddRange(new[] { a, b, d, a, d, c });
            }
        var s = new MeshZSampler(verts.ToArray(), tris.ToArray());
        foreach (var (x, y) in new[] { (0.5, 0.5), (17.25, 43.75), (59.9, 0.1), (30.0, 30.0) })
        {
            Assert.True(s.TrySampleZ(x, y, out double z), $"({x},{y})");
            Assert.Equal(x * 0.5, z, 6);
        }
    }

    // ── 露头判定 ────────────────────────────────────────────
    private static SeamOutcropRunner.Layer Seam(string name, uint rgb, double roofZ, double floorZ)
    {
        var (rv, rt) = Plane(roofZ);
        var (fv, ft) = Plane(floorZ);
        return new SeamOutcropRunner.Layer
        { Name = name, PackedRgb = rgb, RoofVerts = rv, RoofTris = rt, FloorVerts = fv, FloorTris = ft };
    }

    [Fact]
    public void 露头_现状夹在顶底板之间才算()
    {
        // 煤层 30~40；现状面分别在 35(夹在中间) / 50(高于顶板) / 10(低于底板)
        var inside = SeamOutcropRunner.Run(Plane(35).v, Plane(35).t, new[] { Seam("2煤", 0xFF0000, 40, 30) });
        Assert.Equal(4, inside.TotalMarked);
        Assert.All(inside.Color, c => Assert.Equal(0xFF0000u, c));

        var above = SeamOutcropRunner.Run(Plane(50).v, Plane(50).t, new[] { Seam("2煤", 0xFF0000, 40, 30) });
        Assert.Equal(0, above.TotalMarked);

        var below = SeamOutcropRunner.Run(Plane(10).v, Plane(10).t, new[] { Seam("2煤", 0xFF0000, 40, 30) });
        Assert.Equal(0, below.TotalMarked);
    }

    [Fact]
    public void 露头_容差把边界上的点收进来()
    {
        // 现状面正好压在顶板上：eps=0 时算露头(≤ 含等号)，比顶板高 0.03 时要靠 eps 才收得进来
        var justAbove = Plane(40.03);
        var tight = SeamOutcropRunner.Run(justAbove.v, justAbove.t, new[] { Seam("2煤", 1, 40, 30) }, null, snapEps: 0.0);
        Assert.Equal(0, tight.TotalMarked);

        var loose = SeamOutcropRunner.Run(justAbove.v, justAbove.t, new[] { Seam("2煤", 1, 40, 30) }, null, snapEps: 0.05);
        Assert.Equal(4, loose.TotalMarked);
    }

    [Fact]
    public void 露头_斜坡现状面只染穿过煤层的那一段()
    {
        // 现状面 z 从 0 线性升到 100；煤层 30~40 ⇒ 只有 x∈[30,40] 那段的顶点在带内。
        // 顶点在 x=0/100 两端，都不在带内 ⇒ 一个都染不上，但漏染估算应当报出来（重心 x=50/66 也不在带内…）
        // 故这里用三点直接落在带内的网：x=0(z=0) / x=35(z=35) / x=100(z=100)
        var v = new[] { 0.0, 0, 0, 35.0, 0, 35, 100.0, 0, 100, 0.0, 50, 0, 35.0, 50, 35, 100.0, 50, 100 };
        var t = new[] { 0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4 };
        var rep = SeamOutcropRunner.Run(v, t, new[] { Seam("2煤", 0x00FF00, 40, 30) });
        Assert.Equal(2, rep.TotalMarked);                 // 只有 x=35 那两个点
        Assert.True(rep.Assigned[1]);
        Assert.True(rep.Assigned[4]);
        Assert.False(rep.Assigned[0]);
        Assert.False(rep.Assigned[2]);
    }

    // ── 层序优先 ────────────────────────────────────────────
    [Fact]
    public void 层序_先来的层先认领重叠的顶点()
    {
        // 两层煤都盖住 z=35：先配的 2煤 认领，后配的 3煤 一个都拿不到
        var (v, t) = Plane(35);
        var rep = SeamOutcropRunner.Run(v, t, new[]
        {
            Seam("2煤", 0xAA0000, 40, 30),
            Seam("3煤", 0x0000BB, 45, 25),
        });
        Assert.Equal(4, rep.Layers[0].Marked);
        Assert.Equal(0, rep.Layers[1].Marked);            // 被吃掉了 —— 如实报出来，不合并不平均
        Assert.All(rep.Color, c => Assert.Equal(0xAA0000u, c));
    }

    [Fact]
    public void 层序_不重叠时各染各的()
    {
        // 现状面一半 z=35、一半 z=15；2煤 30~40、3煤 10~20
        var v = new[] { 0.0, 0, 35, 40.0, 0, 35, 40.0, 100, 35, 0.0, 100, 35,
                        60.0, 0, 15, 100.0, 0, 15, 100.0, 100, 15, 60.0, 100, 15 };
        var t = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        var rep = SeamOutcropRunner.Run(v, t, new[]
        {
            Seam("2煤", 0xAA0000, 40, 30),
            Seam("3煤", 0x0000BB, 20, 10),
        });
        Assert.Equal(4, rep.Layers[0].Marked);
        Assert.Equal(4, rep.Layers[1].Marked);
        Assert.Equal(0xAA0000u, rep.Color[0]);
        Assert.Equal(0x0000BBu, rep.Color[4]);
    }

    // ── 缺面 / nodata ───────────────────────────────────────
    [Fact]
    public void 缺面_顶或底板缺一张就整层跳过()
    {
        // 只有一张面判不出"夹在中间"，拿另一张凑合会把半个矿都染成这层煤
        var (v, t) = Plane(35);
        var half = new SeamOutcropRunner.Layer
        {
            Name = "缺底板", PackedRgb = 0xFF0000,
            RoofVerts = Plane(40).v, RoofTris = Plane(40).t,
            FloorVerts = Array.Empty<double>(), FloorTris = Array.Empty<int>(),
        };
        var rep = SeamOutcropRunner.Run(v, t, new[] { half });
        Assert.Equal(0, rep.TotalMarked);
        Assert.Single(rep.Layers);
        Assert.Equal(0, rep.Layers[0].Marked);
    }

    [Fact]
    public void 判不了_顶底板盖不到的顶点单独计入nodata()
    {
        // ★ "判不了"不能冒充"不是露头"：现状面比顶/底板大一圈，外圈那些点要计进 NoData
        var (rv, rt) = Plane(40, 40, 40, 60, 60);      // 顶底板只覆盖中间一小块
        var (fv, ft) = Plane(30, 40, 40, 60, 60);
        var terrain = Plane(35, 0, 0, 100, 100);       // 现状面四个角全在小块之外

        var rep = SeamOutcropRunner.Run(terrain.v, terrain.t, new[]
        {
            new SeamOutcropRunner.Layer
            { Name = "2煤", PackedRgb = 1, RoofVerts = rv, RoofTris = rt, FloorVerts = fv, FloorTris = ft },
        });
        Assert.Equal(0, rep.TotalMarked);
        Assert.Equal(4, rep.Layers[0].NoData);         // 四个角都判不了
    }

    // ── 可采范围裁剪 ────────────────────────────────────────
    [Fact]
    public void 范围_裁到可采范围之内()
    {
        var (v, t) = Plane(35);                        // 四个角 (0,0)(100,0)(100,100)(0,100)
        var ring = new[] { -1.0, -1.0, 50.0, -1.0, 50.0, 50.0, -1.0, 50.0 };   // 只框住左下角那个点

        var all = SeamOutcropRunner.Run(v, t, new[] { Seam("2煤", 1, 40, 30) });
        Assert.Equal(4, all.TotalMarked);

        var clipped = SeamOutcropRunner.Run(v, t, new[] { Seam("2煤", 1, 40, 30) }, new[] { ring });
        Assert.Equal(1, clipped.TotalMarked);
        Assert.True(clipped.Assigned[0]);
    }

    [Fact]
    public void 范围_空环等于不裁()
    {
        var (v, t) = Plane(35);
        foreach (IReadOnlyList<double[]>? rings in new IReadOnlyList<double[]>?[]
                 { null, Array.Empty<double[]>(), new[] { new double[] { 1, 2 } } })   // 第三个：顶点不足的环
            Assert.Equal(4, SeamOutcropRunner.Run(v, t, new[] { Seam("2煤", 1, 40, 30) }, rings).TotalMarked);
    }

    [Fact]
    public void 范围_多个环取并集()
    {
        var (v, t) = Plane(35);
        var lowerLeft = new[] { -1.0, -1.0, 50.0, -1.0, 50.0, 50.0, -1.0, 50.0 };
        var upperRight = new[] { 50.0, 50.0, 101.0, 50.0, 101.0, 101.0, 50.0, 101.0 };
        var rep = SeamOutcropRunner.Run(v, t, new[] { Seam("2煤", 1, 40, 30) }, new[] { lowerLeft, upperRight });
        Assert.Equal(2, rep.TotalMarked);
    }

    // ── 漏染估算 ────────────────────────────────────────────
    [Fact]
    public void 漏染_窄露头带整片漏掉时要报出来()
    {
        // 一个大三角，三个顶点都远在带外，但重心恰好落在煤层带内 ⇒ 按节点着色一点色都上不去。
        // 这正是"该改走交线重剖分"的信号。
        var v = new[] { 0.0, 0, 0, 90.0, 0, 0, 45.0, 90, 105.0 };   // 重心 z = 35
        var t = new[] { 0, 1, 2 };
        var rep = SeamOutcropRunner.Run(v, t, new[] { Seam("2煤", 1, 40, 30) });
        Assert.Equal(0, rep.TotalMarked);
        Assert.Equal(1, rep.TotalUncovered);
    }

    [Fact]
    public void 漏染_已有顶点染上色的三角不算漏()
    {
        var (v, t) = Plane(35);
        var rep = SeamOutcropRunner.Run(v, t, new[] { Seam("2煤", 1, 40, 30) });
        Assert.Equal(4, rep.TotalMarked);
        Assert.Equal(0, rep.TotalUncovered);
    }

    // ── 兜底 ────────────────────────────────────────────────
    [Fact]
    public void 兜底_没有现状面或没有煤层时给出原因()
    {
        Assert.Contains("没有顶点", SeamOutcropRunner.Run(null, null, new[] { Seam("2煤", 1, 40, 30) }).Why);
        Assert.Contains("没有配置煤层", SeamOutcropRunner.Run(Plane(0).v, Plane(0).t, null).Why);
        Assert.Contains("没有配置煤层",
            SeamOutcropRunner.Run(Plane(0).v, Plane(0).t, Array.Empty<SeamOutcropRunner.Layer>()).Why);
    }

    [Fact]
    public void 兜底_一个都没染上时说清可能的原因()
    {
        var rep = SeamOutcropRunner.Run(Plane(500).v, Plane(500).t, new[] { Seam("2煤", 1, 40, 30) });
        Assert.Equal(0, rep.TotalMarked);
        Assert.Contains("坐标系", rep.Why);      // 现场最常见的就是这个
    }

    // ── 颜色打包 ────────────────────────────────────────────
    [Theory]
    [InlineData("#FF8000", 0xFF8000u)]
    [InlineData("ff8000", 0xFF8000u)]
    [InlineData("#000000", 0x000000u)]
    public void 颜色_十六进制解析(string hex, uint expect)
        => Assert.Equal(expect, SeamSpec.ParseHex(hex));

    [Theory]
    [InlineData("#FF80")]        // 位数不够
    [InlineData("#GG0000")]      // 非十六进制
    [InlineData("")]
    [InlineData(null)]
    public void 颜色_解析不了就返回空而不是猜一个(string? hex)
        => Assert.Null(SeamSpec.ParseHex(hex));

    [Fact]
    public void 颜色_打包与拆包往返()
    {
        uint p = SeamSpec.Pack(0x12, 0x34, 0x56);
        Assert.Equal(0x123456u, p);
        Assert.Equal((0x12, 0x34, 0x56), SeamSpec.Unpack(p));
        Assert.Equal("#123456", new SeamSpec { PackedRgb = p }.Hex);
    }
}
