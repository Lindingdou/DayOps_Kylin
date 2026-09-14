using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>填充是一个整体对象（HatchEntity）：整块选中/变换/改参数/存档/分解 的回归。</summary>
[Collection("MeshRenderMode")]
public class HatchEntityTests
{
    private static HatchEntity Square(string pattern = "ANSI31", double side = 10, double scale = 4)
    {
        var h = new HatchEntity { PatternName = pattern, Scale = scale };
        h.Boundary.AddRange(new[] { (0.0, 0.0), (side, 0.0), (side, side), (0.0, side) });
        return h;
    }

    [Fact]
    public void 一个填充是一个实体_图案线是算出来的()
    {
        var h = Square();
        Assert.NotEmpty(h.Lines());
        var o = new List<float>();
        h.Tessellate(o);
        Assert.Equal(h.Lines().Count * 12, o.Count);         // 每段 2 顶点 × 6 float
        Assert.Equal("填充", EntityTypeName.Of(h));
    }

    [Fact]
    public void 改图案改比例就地重算_不用重画()
    {
        var h = Square("ANSI31", scale: 4);
        int before = h.Lines().Count;
        h.PatternName = "NET"; h.Invalidate();
        int after = h.Lines().Count;
        Assert.NotEqual(before, after);                       // 换成方格网, 线数变了
        h.Scale = 16; h.Invalidate();
        Assert.True(h.Lines().Count < after, "比例调大应更疏");
    }

    [Fact]
    public void 缓存命中_参数没变不重算()
    {
        var h = Square();
        var a = h.Lines();
        var b = h.Lines();
        Assert.Same(a, b);                                    // 同一份缓存
        h.Angle += 30; h.Invalidate();
        Assert.NotSame(a, h.Lines());
    }

    [Fact]
    public void 比例给0走自动_按边界大小折算()
    {
        var auto = Square("ANSI31", side: 100, scale: 0);
        var small = Square("ANSI31", side: 10, scale: 0);
        // 自动比例按边界折算 → 大小两块的线数应当量级接近(而不是大的那块密上百倍)
        Assert.InRange(auto.Lines().Count, small.Lines().Count / 2, small.Lines().Count * 2 + 8);
    }

    [Fact]
    public void 整体移动_边界跟着走图案不变形()
    {
        var h = Square();
        int n = h.Lines().Count;
        var moved = (HatchEntity)h.Apply(Affine2.Translate(100, 50));
        Assert.Equal(n, moved.Lines().Count);                 // 平移不改疏密
        Assert.Equal(100, moved.Boundary[0].x, 6);
        Assert.Equal(50, moved.Boundary[0].y, 6);
        Assert.Equal(h.PatternName, moved.PatternName);
        Assert.Equal(h.Scale, moved.Scale, 6);
    }

    [Fact]
    public void 整体缩放_图案跟着放大而不是变密()
    {
        var h = Square();
        int n = h.Lines().Count;
        var big = (HatchEntity)h.Apply(Affine2.Scale(3, 0, 0));
        Assert.Equal(h.Scale * 3, big.Scale, 6);
        Assert.InRange(big.Lines().Count, n - 2, n + 2);      // 疏密不变(线数基本一致)
    }

    [Fact]
    public void 整体旋转_图案角度跟着转()
    {
        var h = Square();
        var rot = (HatchEntity)h.Apply(Affine2.Rotate(Math.PI / 2, 0, 0));
        Assert.Equal(90, rot.Angle, 4);
    }

    [Fact]
    public void 拖边界夹点_图案重新铺满()
    {
        var h = Square();
        Assert.Equal(4, h.Grips().Count);
        var moved = (HatchEntity)h.MoveGrip(2, 30, 30)!;
        Assert.Equal((30.0, 30.0), moved.Boundary[2]);
        // 图案随新边界重铺: 面积变大 → 图案线总长变长
        // (条数不一定变 —— ANSI31 是 45° 线, 而这一拖正好沿 45° 方向拉长, 只加长不加条)
        Assert.True(TotalLen(moved) > TotalLen(h) * 1.2, $"图案没跟着边界重铺: {TotalLen(moved):0.#} vs {TotalLen(h):0.#}");
    }

    private static double TotalLen(HatchEntity h)
        => h.Lines().Sum(l => Math.Sqrt((l.x2 - l.x1) * (l.x2 - l.x1) + (l.y2 - l.y1) * (l.y2 - l.y1)));

    [Fact]
    public void 边界拉出图案范围时条数也增加()
    {
        var h = Square();
        var taller = (HatchEntity)h.MoveGrip(2, 10, 40)!;      // 沿 45° 的垂向拉高 → 扫描线变多
        Assert.True(taller.Lines().Count > h.Lines().Count, "垂向变大后应铺更多条线");
    }

    [Fact]
    public void 分解才变成一根根线()
    {
        var h = Square();
        var parts = h.Explode()!;
        Assert.Equal(h.Lines().Count, parts.Count);
        Assert.All(parts, p => Assert.IsType<LineEntity>(p));
        Assert.All(parts, p => Assert.Equal(h.LayerName, p.LayerName));
    }

    [Fact]
    public void 实心填充走面通道_点内部即命中()
    {
        var h = Square("SOLID");
        Assert.True(h.IsSolid);
        Assert.Empty(h.Lines());                              // 没有线
        var faces = new List<float>();
        h.TessellateFaces(faces);
        Assert.True(faces.Count >= 2 * 3 * 6, "实心应铺出三角面");
        Assert.Equal(0, h.DistanceTo(5, 5), 6);               // 内部 → 命中
        Assert.True(h.DistanceTo(20, 5) > 9);                 // 外面 → 不命中
    }

    [Fact]
    public void 有图案的按线拾取_不吞掉上面的图元()
    {
        var h = Square();
        Assert.True(h.DistanceTo(5, 5) < 1.0);                // 图案线附近 → 近
        Assert.True(h.DistanceTo(1000, 1000) > 100);          // 远处 → 远
    }

    [Fact]
    public void 存档往返_图案参数与边界都在()
    {
        var scene = new Scene();
        var h = Square("BRICK", scale: 3);
        h.Angle = 15; h.Cross = true; h.Elevation = 42;
        h.Cr = 0.9f; h.Cg = 0.2f; h.Cb = 0.1f; h.LayerName = "填充层";
        scene.Add(h);

        var doc = SceneIO.LoadDoc(SceneIO.SaveDoc(scene, new List<Layer>(), "0"));
        var back = Assert.IsType<HatchEntity>(Assert.Single(doc.Scene.Entities));
        Assert.Equal("BRICK", back.PatternName);
        Assert.Equal(3, back.Scale, 6);
        Assert.Equal(15, back.Angle, 6);
        Assert.True(back.Cross);
        Assert.Equal(42, back.Elevation, 6);
        Assert.Equal("填充层", back.LayerName);
        Assert.Equal(4, back.Boundary.Count);
        Assert.Equal(h.Lines().Count, back.Lines().Count);    // 读回来重算得到同样的图案
    }

    [Fact]
    public void 特性面板可改图案比例角度()
    {
        var h = Square();
        var labels = EntityProperties.EditableLabels(h);
        Assert.Contains("图案", labels);
        Assert.Contains("比例", labels);
        Assert.Contains("角度", labels);

        var edited = (HatchEntity)EntityProperties.WithEdited(h, "图案", "HONEY")!;
        Assert.Equal("HONEY", edited.PatternName);
        Assert.Equal(h.Boundary.Count, edited.Boundary.Count);

        var scaled = (HatchEntity)EntityProperties.WithEdited(h, "比例", "12")!;
        Assert.Equal(12, scaled.Scale, 6);
        var auto = (HatchEntity)EntityProperties.WithEdited(h, "比例", "自动")!;
        Assert.Equal(0, auto.Scale, 9);

        var turned = (HatchEntity)EntityProperties.WithEdited(h, "角度", "30")!;
        Assert.Equal(30, turned.Angle, 6);

        Assert.Null(EntityProperties.WithEdited(h, "图案", "没有这个图案"));   // 未知图案拒绝
    }

    [Fact]
    public void 改颜色改图层不丢图案参数()
    {
        var h = Square("EARTH", scale: 5);
        h.Angle = 22;
        var recolored = (HatchEntity)EntityProperties.WithEdited(h, "颜色", "#FF0000")!;
        Assert.Equal(1f, recolored.Cr, 3);
        Assert.Equal("EARTH", recolored.PatternName);
        Assert.Equal(5, recolored.Scale, 6);
        Assert.Equal(22, recolored.Angle, 6);
    }

    [Fact]
    public void 拖拽替身用边界环_代价与图案线数无关()
    {
        var h = Square("NET", scale: 0.5);                    // 很密
        var proxy = new List<(double x, double y, double z)>();
        h.BuildPreviewProxy(proxy, 64);
        Assert.Equal(8, proxy.Count);                         // 4 条边 × 2 端点
    }
}
