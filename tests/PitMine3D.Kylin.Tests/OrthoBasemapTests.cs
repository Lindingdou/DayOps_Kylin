using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 正射影像底图（§三三一）：按世界 XY 采影像给地表上色。
/// 最要紧的一条是**覆盖范围要点名** —— 对象落在影像外时画面上就是一片灰，
/// 不说出来的话人只会以为是"渲染坏了"（原版那条纪律）。
/// </summary>
[Collection("MeshRenderMode")]
public class OrthoBasemapTests
{
    /// <summary>合成影像：在 [0,100]×[0,100] 内按 x 给红、y 给绿；框外返 null。</summary>
    private sealed class FakeSampler : OrthoBasemap.ISampler
    {
        private readonly double _x0, _y0, _x1, _y1;
        public FakeSampler(double x0 = 0, double y0 = 0, double x1 = 100, double y1 = 100)
        { _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1; }
        public (double minX, double minY, double maxX, double maxY) Extent => (_x0, _y0, _x1, _y1);
        public (byte r, byte g, byte b)? SampleRgb(double x, double y)
        {
            if (x < _x0 || x > _x1 || y < _y0 || y > _y1) return null;
            return ((byte)Math.Clamp((x - _x0) / (_x1 - _x0) * 255, 0, 255),
                    (byte)Math.Clamp((y - _y0) / (_y1 - _y0) * 255, 0, 255),
                    (byte)0);
        }
    }

    private static MeshEntity Quad(string name, double x0, double y0, double size)
    {
        var m = new MeshEntity(name,
            new List<(double x, double y, double z)>
            {
                (x0, y0, 0), (x0 + size, y0, 0), (x0 + size, y0 + size, 0), (x0, y0 + size, 0),
            },
            new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) });
        return m;
    }

    private static PointCloudEntity Cloud(string name, params (double x, double y)[] pts)
    {
        var p = new PointCloudEntity { Name = name };
        foreach (var (x, y) in pts) p.Pts.Add((x, y, 0));
        return p;
    }

    // ── 目标收集 ────────────────────────────────────────────
    [Fact]
    public void 只认三角网与点云()
    {
        var ents = new List<SceneEntity>
        {
            Quad("网", 0, 0, 10),
            Cloud("云", (1, 1)),
            new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1 },     // 线没有"地表"可言
            new TextEntity { X = 0, Y = 0, Text = "字", Height = 1 },
        };
        var t = OrthoBasemap.TargetsOf(ents);
        Assert.Equal(2, t.Count);
        Assert.Contains(t, x => x.Name == "网");
        Assert.Contains(t, x => x.Name == "云");
    }

    [Fact]
    public void 空几何的对象不进目标()
    {
        var empty = new MeshEntity("空网", new List<(double, double, double)>(), new List<(int, int, int)>());
        Assert.Empty(OrthoBasemap.TargetsOf(new SceneEntity[] { empty, new PointCloudEntity { Name = "空云" } }));
    }

    // ── 上色 ────────────────────────────────────────────────
    [Fact]
    public void 按世界XY采色_落到逐顶点色上()
    {
        var m = Quad("网", 0, 0, 100);
        var r = OrthoBasemap.Apply(new FakeSampler(), OrthoBasemap.TargetsOf(new[] { m }));
        Assert.True(r.Ok, r.Message);
        Assert.NotNull(m.VertColors);
        Assert.Equal(4, m.VertColors!.Count);
        Assert.Equal(0f, m.VertColors[0].r, 2);          // (0,0) → 红 0
        Assert.Equal(1f, m.VertColors[1].r, 2);          // (100,0) → 红 满
        Assert.Equal(1f, m.VertColors[2].g, 2);          // (100,100) → 绿 满
        Assert.Equal(4, r.Colored);
        Assert.Equal(0, r.Outside);
    }

    [Fact]
    public void 点云同样能贴()
    {
        var c = Cloud("云", (10, 10), (50, 50), (90, 90));
        var r = OrthoBasemap.Apply(new FakeSampler(), OrthoBasemap.TargetsOf(new[] { c }));
        Assert.True(r.Ok, r.Message);
        Assert.NotNull(c.Colors);
        Assert.Equal(3, c.Colors!.Count);
        Assert.Equal(3, r.Colored);
    }

    // ── 覆盖范围（本组最要紧的一条）──────────────────────────
    [Fact]
    public void 整个落在影像外的对象被点名且不着色()
    {
        var far = Quad("跑偏的网", 5000, 5000, 10);
        var r = OrthoBasemap.Apply(new FakeSampler(), OrthoBasemap.TargetsOf(new[] { far }));
        Assert.False(r.Ok);                               // 一个都没贴上
        Assert.Null(far.VertColors);                      // 没有被涂成一片灰
        Assert.Contains(r.Notes, n => n.Contains("跑偏的网") && n.Contains("影像范围之外"));
        Assert.Contains("坐标系", r.Message);              // 提示最可能的原因
    }

    [Fact]
    public void 部分落在影像外_按灰着色并提示占比()
    {
        // 一半在影像内、一半在外
        var half = Quad("半边网", 80, 80, 60);            // [80,140] 越过 100 边界
        var r = OrthoBasemap.Apply(new FakeSampler(), OrthoBasemap.TargetsOf(new[] { half }));
        Assert.True(r.Ok);
        Assert.True(r.Outside > 0, "越界的点应计入 Outside");
        Assert.Contains(half.VertColors!, c => Math.Abs(c.r - OrthoBasemap.OutsideGray) < 1e-6
                                            && Math.Abs(c.g - OrthoBasemap.OutsideGray) < 1e-6);
        Assert.Contains(r.Notes, n => n.Contains("半边网") && n.Contains("%"));
    }

    [Fact]
    public void 命中率算得对()
    {
        var inside = Quad("内", 0, 0, 50);
        var outside = Quad("外", 900, 900, 10);
        var r = OrthoBasemap.Apply(new FakeSampler(), OrthoBasemap.TargetsOf(new SceneEntity[] { inside, outside }));
        Assert.Equal(8, r.Vertices);        // 两张网各 4 顶点, 越界那张也计入总数
        Assert.Equal(4, r.Colored);
        Assert.Equal(4, r.Outside);
        Assert.Equal(0.5, r.HitRatio, 6);
    }

    [Fact]
    public void 框交叠判定含边界相接()
    {
        Assert.True(OrthoBasemap.Overlaps((0, 0, 10, 10), (10, 10, 20, 20)));   // 只碰一个角也算
        Assert.True(OrthoBasemap.Overlaps((0, 0, 10, 10), (5, 5, 6, 6)));       // 包含
        Assert.False(OrthoBasemap.Overlaps((0, 0, 10, 10), (10.1, 0, 20, 10)));
    }

    // ── 清除 ────────────────────────────────────────────────
    [Fact]
    public void 清底图_有真实色的还原真实色()
    {
        var m = Quad("网", 0, 0, 100);
        // 真实色取蓝：假采样器的 b 恒为 0，取红会和 (100,0) 那个顶点采出来的 (1,0,0) 撞上，
        // 断言"已被覆盖"就假失败了（第一版正是这么写错的）
        m.RgbColors = new List<(float r, float g, float b)> { (0, 0, 1), (0, 0, 1), (0, 0, 1), (0, 0, 1) };
        var targets = OrthoBasemap.TargetsOf(new[] { m });
        OrthoBasemap.Apply(new FakeSampler(), targets);
        Assert.All(m.VertColors!, c => Assert.NotEqual((0f, 0f, 1f), c));   // 已被影像色覆盖

        OrthoBasemap.Clear(targets);
        Assert.NotNull(m.VertColors);
        Assert.All(m.VertColors!, c => Assert.Equal((0f, 0f, 1f), c));
    }

    [Fact]
    public void 清底图_没有真实色的撤回逐顶点色()
    {
        var m = Quad("网", 0, 0, 100);
        var targets = OrthoBasemap.TargetsOf(new[] { m });
        OrthoBasemap.Apply(new FakeSampler(), targets);
        Assert.NotNull(m.VertColors);

        OrthoBasemap.Clear(targets);
        Assert.Null(m.VertColors);        // 退回实体基色, 而不是留一份被影像涂过的
    }

    [Fact]
    public void 清底图_点云同样还原()
    {
        var c = Cloud("云", (10, 10), (20, 20));
        c.RgbColors = new List<(float r, float g, float b)> { (0, 0, 1), (0, 0, 1) };
        var targets = OrthoBasemap.TargetsOf(new[] { c });
        OrthoBasemap.Apply(new FakeSampler(), targets);
        OrthoBasemap.Clear(targets);
        Assert.All(c.Colors!, x => Assert.Equal((0f, 0f, 1f), x));
    }

    // ── 边界 ────────────────────────────────────────────────
    [Fact]
    public void 没有目标或没有采样器时给原因而不是崩()
    {
        var r1 = OrthoBasemap.Apply(new FakeSampler(), new List<OrthoBasemap.ITarget>());
        Assert.False(r1.Ok);
        Assert.Contains("没有可贴底图", r1.Message);

        var r2 = OrthoBasemap.Apply(null!, OrthoBasemap.TargetsOf(new[] { Quad("网", 0, 0, 10) }));
        Assert.False(r2.Ok);
        Assert.NotEmpty(r2.Message);
    }
}
