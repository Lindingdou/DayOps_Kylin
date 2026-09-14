using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using MeshZSampler = PitMine3D.Kylin.Cad.SeamOutcrop.MeshZSampler;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>创建工程位置 ⑥⑦（§三六八）：沿交线裁线（排土档）+ 沿衔接链拼链放样台阶面。</summary>
public class BenchFaceBuilderTests
{
    private static EpPolyline Line(ulong h, string layer, double z, params (double x, double y)[] xy)
    {
        var xyz = new double[xy.Length * 3];
        for (int i = 0; i < xy.Length; i++) { xyz[i * 3] = xy[i].x; xyz[i * 3 + 1] = xy[i].y; xyz[i * 3 + 2] = z; }
        return new EpPolyline(h, layer, xyz, false);
    }

    /// <summary>斜面：z = 1200 + 0.5·x，覆盖 x∈[0,100]、y∈[-50,50]。</summary>
    private static MeshZSampler Slope()
    {
        var v = new double[] { 0, -50, 1200, 100, -50, 1250, 100, 50, 1250, 0, 50, 1200 };
        var t = new[] { 0, 1, 2, 0, 2, 3 };
        return new MeshZSampler(v, t);
    }

    [Fact]
    public void 沿交线裁线_只留面之上的段_零点处切开()
    {
        // 线在 z=1220：面在 x=40 处也是 1220 ⇒ x<40 线在面上（留），x>40 线被埋（丢）
        var xyz = new double[] { 0, 0, 1220, 100, 0, 1220 };
        var segs = LineAboveMeshClipper.Clip(xyz, false, Slope(), keepUncovered: true);
        var seg = Assert.Single(segs);
        Assert.Equal(6, seg.Length);
        Assert.Equal(0, seg[0], 6); Assert.Equal(40, seg[3], 3);
        // 反向：整条都在面下 ⇒ 空表；整条在面上 ⇒ 原样
        Assert.Empty(LineAboveMeshClipper.Clip(new double[] { 50, 0, 1000, 100, 0, 1000 }, false, Slope(), true));
        Assert.Single(LineAboveMeshClipper.Clip(new double[] { 0, 0, 1300, 100, 0, 1300 }, false, Slope(), true));
    }

    [Fact]
    public void 沿交线裁线_面外的点按keepUncovered定_无面原样不删()
    {
        var xyz = new double[] { 150, 0, 1000, 200, 0, 1000 };           // 面没盖到
        Assert.Single(LineAboveMeshClipper.Clip(xyz, false, Slope(), keepUncovered: true));
        Assert.Empty(LineAboveMeshClipper.Clip(xyz, false, Slope(), keepUncovered: false));
        Assert.Single(LineAboveMeshClipper.Clip(xyz, false, null, false));
        // 中间一段被埋：两头各留一段
        var mid = new double[] { 0, 0, 1230, 50, 0, 1210, 100, 0, 1260 };
        var segs = LineAboveMeshClipper.Clip(mid, false, Slope(), true);
        Assert.Equal(2, segs.Count);
    }

    [Fact]
    public void 拼链_原线沿连线接上衔接段与下一条线()
    {
        // 模板 (110,0)-(190,0) z=1200；老线 (215,-30)-(215,-100)；同级角部相交 ⇒ 交点 (215,0)
        var tpl = new List<EpPolyline> { Line(1, "台阶_1200", 1200, (110, 0), (190, 0)) };
        var pit = new List<EpPolyline> { Line(2, "0", 1200, (215, -30), (215, -100)) };
        var s = EngineeringPositionBuilder.Build(tpl, pit, null, "", allEndpointNodes: true);
        Assert.True(s.Success, s.Error);
        var t = s.Nodes.First(n => n.Kind == EpNodeKind.Template && n.X > 150);
        var w = s.Nodes.First(n => n.Kind == EpNodeKind.EndWall && n.Y > -50);
        EngineeringPositionBuilder.AddLink(s, t, w);
        var cons = EpConnectorBuilder.Build(s, 8);
        Assert.Single(cons); Assert.True(cons[0].ByIntersection);
        var byHandle = tpl.Concat(pit).ToDictionary(p => p.Handle, p => p);

        var chain = BenchFaceBuilder.StitchChain(tpl[0], s, cons, byHandle);
        Assert.Equal((110, 0), (Math.Round(chain[0].X, 6), Math.Round(chain[0].Y, 6)));
        Assert.Equal((215, -100), (Math.Round(chain[^1].X, 6), Math.Round(chain[^1].Y, 6)));   // 走到老线的另一头
        Assert.Contains(chain, p => Math.Abs(p.X - 215) < 1e-6 && Math.Abs(p.Y) < 1e-6);          // 经过折点
        // 从老线出发拼：往"首端"方向长出去的那一截攒完再倒过来接在前面 ⇒ 整条链仍是 模板远端 → 老线远端
        var chain2 = BenchFaceBuilder.StitchChain(pit[0], s, cons, byHandle);
        Assert.Equal((110, 0), (Math.Round(chain2[0].X, 6), Math.Round(chain2[0].Y, 6)));
        Assert.Equal((215, -100), (Math.Round(chain2[^1].X, 6), Math.Round(chain2[^1].Y, 6)));
        Assert.Equal(chain.Count, chain2.Count);
    }

    [Fact]
    public void 拼链_两线已交叉时不插几何而裁掉尾巴()
    {
        var tpl = new List<EpPolyline> { Line(1, "台阶_1200", 1200, (110, 0), (230, 0)) };
        var pit = new List<EpPolyline> { Line(2, "0", 1200, (220, 20), (220, -100)) };
        var s = EngineeringPositionBuilder.Build(tpl, pit, null, "", allEndpointNodes: true);
        var t = s.Nodes.First(n => n.Kind == EpNodeKind.Template && n.X > 150);
        var w = s.Nodes.First(n => n.Kind == EpNodeKind.EndWall && n.Y > 0);
        EngineeringPositionBuilder.AddLink(s, t, w);
        var cons = EpConnectorBuilder.Build(s, 8);
        Assert.True(cons[0].Overlapped);
        var byHandle = tpl.Concat(pit).ToDictionary(p => p.Handle, p => p);
        var chain = BenchFaceBuilder.StitchChain(tpl[0], s, cons, byHandle);
        // 模板尾巴 10m 裁掉 ⇒ 折点 (220,0)；老线头 20m 裁掉 ⇒ 接着从 (220,0) 往 -y
        Assert.DoesNotContain(chain, p => p.X > 220 + 1e-6);
        Assert.DoesNotContain(chain, p => p.Y > 1e-6);
        Assert.Equal((220, -100), (Math.Round(chain[^1].X, 6), Math.Round(chain[^1].Y, 6)));
    }

    [Fact]
    public void 放样_按弧长逐站配对_方向自动对齐_面积对得上()
    {
        var crest = new List<(double X, double Y, double Z)> { (0, 10, 1210), (100, 10, 1210) };
        var toe = new List<(double X, double Y, double Z)> { (100, 0, 1200), (0, 0, 1200) };     // 反向给
        var verts = new List<double>(); var tris = new List<int>();
        double area = BenchFaceBuilder.Ribbon(crest, toe, verts, tris);
        double expect = 100 * Math.Sqrt(200);                                                    // 宽 100 × 斜距 √(10²+10²)
        Assert.Equal(expect, area, 3);
        Assert.Equal(0, tris.Count % 3);
        Assert.True(tris.All(i => i >= 0 && i < verts.Count / 3));
        // 首站：坡顶 (0,10) 配坡底 (0,0)（方向已对齐，不是麻花）
        Assert.Equal(0, verts[0], 6); Assert.Equal(0, verts[3], 6); Assert.Equal(0, verts[4], 6);
        Assert.Equal(0, BenchFaceBuilder.Ribbon(crest, new List<(double, double, double)> { (0, 0, 0) }, verts, tris), 9);
    }
}
