using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.SeamOutcrop;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 露头交线重剖分（§三四二）：沿「现状面 ∩ 顶/底板」的交线把现状网切开，再<b>逐面</b>着色。
///
/// 这一层存在的理由就是 §三四一 那条按节点着色的两个毛病：边界锯齿 ≈ 一个三角边长，
/// 以及**窄露头带整片漏染**。故本文件的头号判据是：
/// <b>同一个输入，按节点着色一点色都上不去，重剖分之后能切出露头面来。</b>
/// </summary>
public class SeamOutcropRefinerTests
{
    private static (double[] v, int[] t) Plane(double z, double x0 = 0, double y0 = 0, double x1 = 100, double y1 = 100)
        => (new[] { x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z }, new[] { 0, 1, 2, 0, 2, 3 });

    private static SeamOutcropRunner.Layer Seam(uint rgb, double roofZ, double floorZ, string name = "2煤")
    {
        var (rv, rt) = Plane(roofZ);
        var (fv, ft) = Plane(floorZ);
        return new SeamOutcropRunner.Layer
        { Name = name, PackedRgb = rgb, RoofVerts = rv, RoofTris = rt, FloorVerts = fv, FloorTris = ft };
    }

    /// <summary>一张沿 X 从 z0 线性升到 z1 的网，X 方向切 n 段（三角够多才切得出东西）。</summary>
    private static (double[] v, int[] t) Ramp(double z0, double z1, int n = 4, double len = 100, double wid = 100)
    {
        var v = new List<double>();
        var t = new List<int>();
        for (int i = 0; i <= n; i++)
        {
            double x = len * i / n, z = z0 + (z1 - z0) * i / n;
            v.AddRange(new[] { x, 0.0, z });
            v.AddRange(new[] { x, wid, z });
        }
        for (int i = 0; i < n; i++)
        {
            int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
            t.AddRange(new[] { a, c, d, a, d, b });
        }
        return (v.ToArray(), t.ToArray());
    }

    // ── 这一层的存在理由 ────────────────────────────────────
    [Fact]
    public void 补漏_按节点染不上的窄露头带_重剖分切得出来()
    {
        // 一个大三角：三个顶点 z=0/0/105 都在煤层 30~40 之外，但带子从三角内部横穿。
        // 按节点着色 ⇒ 0 个顶点被染；重剖分 ⇒ 切出真正落在带内的面。
        var v = new[] { 0.0, 0, 0, 90.0, 0, 0, 45.0, 90, 105.0 };
        var t = new[] { 0, 1, 2 };
        var layers = new[] { Seam(0xFF0000, 40, 30) };

        var byNode = SeamOutcropRunner.Run(v, t, layers);
        Assert.Equal(0, byNode.TotalMarked);
        Assert.True(byNode.TotalUncovered > 0);          // 它自己就报了"这儿该重剖分"

        var refined = SeamOutcropRunner.Refine(v, t, layers);
        Assert.True(refined.CoalTris > 0, "重剖分应当切出露头面");
        Assert.True(refined.TotalTris > 1, "现状网应当被切开");
        Assert.True(refined.CutVerts > 0, "交线上应当插出新点");
    }

    [Fact]
    public void 补漏_切完之后每个三角要么整片在带内要么整片在带外()
    {
        // 判据：逐面归属 FaceSeam 与 Tris 同序且长度一致 —— 每个面有且只有一个归属，没有"半个面"
        var (v, t) = Ramp(0, 100, n: 4);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(0xFF0000, 40, 30) });
        Assert.Equal(r.Tris.Length / 3, r.FaceSeam.Length);
        Assert.All(r.FaceSeam, s => Assert.InRange(s, -1, 0));
        Assert.Equal(r.CoalTris, r.FaceSeam.Count(s => s == 0));
    }

    // ── 不该切的时候别切 ────────────────────────────────────
    [Fact]
    public void 不切_现状面整片在带外时不产生新点()
    {
        var (v, t) = Plane(500);                       // 远高于煤层
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 40, 30) });
        Assert.Equal(0, r.CutVerts);
        Assert.Equal(0, r.CoalTris);
        Assert.Equal(2, r.TotalTris);                  // 原样两个三角
    }

    [Fact]
    public void 不切_现状面整片在带内时全判露头且不切()
    {
        var (v, t) = Plane(35);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 40, 30) });
        Assert.Equal(0, r.CutVerts);
        Assert.Equal(2, r.TotalTris);
        Assert.Equal(2, r.CoalTris);
        Assert.All(r.FaceSeam, s => Assert.Equal(0, s));
    }

    // ── 面积 ────────────────────────────────────────────────
    [Fact]
    public void 面积_水平露头面的三维面积等于投影面积()
    {
        // 水平面上两者必须相等；不等就说明有一处把 z 算进/漏出了
        var (v, t) = Plane(35, 0, 0, 100, 100);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 40, 30) });
        Assert.Single(r.SeamArea3D);
        Assert.Equal(10000.0, r.SeamAreaXY[0], 3);      // 100×100
        Assert.Equal(r.SeamAreaXY[0], r.SeamArea3D[0], 3);
    }

    [Fact]
    public void 面积_陡坡处三维面积大于水平投影()
    {
        // ★ 两个都给、别替对方：按投影面积估储量会低估陡坡上的露头
        var (v, t) = Ramp(0, 100, n: 8);               // 45° 斜面
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 100, 0) });   // 整片都在带内
        Assert.True(r.SeamArea3D[0] > r.SeamAreaXY[0] * 1.3,
            $"三维 {r.SeamArea3D[0]:0.#} 应明显大于投影 {r.SeamAreaXY[0]:0.#}");
        // 45° 斜面：三维 = 投影 × √2
        Assert.Equal(Math.Sqrt(2.0), r.SeamArea3D[0] / r.SeamAreaXY[0], 3);
    }

    [Fact]
    public void 面积_没有露头时是零而不是没有这一项()
    {
        var (v, t) = Plane(500);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 40, 30) });
        Assert.Single(r.SeamArea3D);                   // 项还在
        Assert.Equal(0.0, r.SeamArea3D[0], 9);
        Assert.Equal(0.0, r.SeamAreaXY[0], 9);
    }

    // ── 多层 ────────────────────────────────────────────────
    [Fact]
    public void 多层_各层面积与三角数按入参同序对上()
    {
        // 斜面 z: 0→100；2煤 30~40、3煤 70~80 ⇒ 两层各切出一段
        var (v, t) = Ramp(0, 100, n: 20);
        var r = SeamOutcropRunner.Refine(v, t, new[]
        {
            Seam(0xAA0000, 40, 30, "2煤"),
            Seam(0x0000BB, 80, 70, "3煤"),
        });
        Assert.Equal(2, r.SeamTris.Length);
        Assert.Equal(2, r.SeamArea3D.Length);
        Assert.True(r.SeamTris[0] > 0, "2煤应有露头面");
        Assert.True(r.SeamTris[1] > 0, "3煤应有露头面");
        Assert.Equal(r.CoalTris, r.SeamTris.Sum());
        Assert.Contains(0, r.FaceSeam);
        Assert.Contains(1, r.FaceSeam);
    }

    [Fact]
    public void 多层_缺顶底板的层也占一个位子不让后面的数错位()
    {
        // ★ 入参 seams 的序号就是结果里 SeamTris/SeamArea 的序号 —— 跳过不加会让整排数错位
        var (v, t) = Plane(35);
        var broken = new SeamOutcropRunner.Layer
        {
            Name = "缺底板", PackedRgb = 0xFF0000,
            RoofVerts = Plane(40).v, RoofTris = Plane(40).t,
            FloorVerts = Array.Empty<double>(), FloorTris = Array.Empty<int>(),
        };
        var r = SeamOutcropRunner.Refine(v, t, new[] { broken, Seam(0x00FF00, 40, 30, "2煤") });
        Assert.Equal(2, r.SeamTris.Length);
        Assert.Equal(0, r.SeamTris[0]);                // 缺面的那层判不出露头
        Assert.True(r.SeamTris[1] > 0);                // 但第二层的数没有挪到第一格去
    }

    // ── 范围裁剪 ────────────────────────────────────────────
    [Fact]
    public void 范围_裁剪之后露头面积变小()
    {
        var (v, t) = Plane(35, 0, 0, 100, 100);
        var full = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 40, 30) });
        var half = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 40, 30) },
            new[] { new[] { -1.0, -1.0, 50.0, -1.0, 50.0, 101.0, -1.0, 101.0 } });
        Assert.True(half.SeamAreaXY[0] < full.SeamAreaXY[0]);
    }

    // ── 结构完整性 ──────────────────────────────────────────
    [Fact]
    public void 结构_索引不越界且逐顶点色与顶点数一致()
    {
        var (v, t) = Ramp(0, 100, n: 6);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(0xFF0000, 40, 30) });

        int nv = r.Verts.Length / 3;
        Assert.True(nv > 0);
        Assert.Equal(nv, r.Colors.Length);
        Assert.Equal(0, r.Tris.Length % 3);
        Assert.All(r.Tris, i => Assert.True(i < (uint)nv, $"索引 {i} 越界(顶点 {nv})"));
    }

    [Fact]
    public void 结构_非露头面用哨兵色表示走源面底色()
    {
        // 哨兵不是"白色" —— 交给渲染时要换成源面自身的生效色, 直接当颜色用会把非露头区染成白的
        var (v, t) = Ramp(0, 100, n: 6);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(0xFF0000, 40, 30) });
        Assert.Contains(SeamOutcropRefiner.BaseColorSentinel, r.Colors);
        Assert.Contains(0xFF0000u, r.Colors);
    }

    [Fact]
    public void 结构_每个三角三顶点同色_硬边界无渐变()
    {
        // 只在颜色分界处劈开顶点, 使每个三角三顶点同色; 否则边界会插值成一条渐变带
        var (v, t) = Ramp(0, 100, n: 6);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(0xFF0000, 40, 30) });
        for (int f = 0; f < r.Tris.Length / 3; f++)
        {
            uint c0 = r.Colors[r.Tris[f * 3]];
            Assert.Equal(c0, r.Colors[r.Tris[f * 3 + 1]]);
            Assert.Equal(c0, r.Colors[r.Tris[f * 3 + 2]]);
        }
    }

    [Fact]
    public void 结构_切开之后三角只多不少()
    {
        var (v, t) = Ramp(0, 100, n: 4);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(0xFF0000, 40, 30) });
        Assert.True(r.TotalTris >= t.Length / 3);
        Assert.True(r.SplitTris > 0, "斜面穿过顶底板, 应当被切");
    }

    // ── 兜底 ────────────────────────────────────────────────
    [Fact]
    public void 兜底_空输入不抛只回空结果()
    {
        foreach (var r in new[]
                 {
                     SeamOutcropRunner.Refine(null, null, new[] { Seam(1, 40, 30) }),
                     SeamOutcropRunner.Refine(Plane(0).v, Plane(0).t, null),
                     SeamOutcropRunner.Refine(Plane(0).v, Plane(0).t, Array.Empty<SeamOutcropRunner.Layer>()),
                     SeamOutcropRunner.Refine(Array.Empty<double>(), Array.Empty<int>(), new[] { Seam(1, 40, 30) }),
                 })
        {
            Assert.Empty(r.Tris);
            Assert.Equal(0, r.CoalTris);
        }
    }

    [Fact]
    public void 兜底_容差为负也不炸()
    {
        var (v, t) = Plane(35);
        var r = SeamOutcropRunner.Refine(v, t, new[] { Seam(1, 40, 30) }, null, snapEps: -5);
        Assert.True(r.TotalTris > 0);
    }
}
