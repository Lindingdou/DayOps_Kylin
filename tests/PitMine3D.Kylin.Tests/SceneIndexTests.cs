using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点选/框选的空间索引 —— 关键是"只是变快，选中的东西必须一个不多一个不少"。</summary>
[Collection("TextGeometry")]
public class SceneIndexTests
{
    /// <summary>在 [0,span]² 里铺 n 条短线段。</summary>
    private static Scene Grid(int n, double span, double len = 1.0)
    {
        var s = new Scene();
        int side = (int)Math.Ceiling(Math.Sqrt(n));
        var rnd = new Random(11);
        for (int i = 0; i < n; i++)
        {
            double x = (i % side) / (double)side * span, y = (i / side) / (double)side * span;
            s.Add(new LineEntity { X0 = x, Y0 = y, X1 = x + len, Y1 = y + len * rnd.NextDouble() });
        }
        return s;
    }

    /// <summary>索引版点选必须与全场景扫描逐次同解 —— 快而不准等于没用。</summary>
    [Fact]
    public void Indexed_pick_matches_brute_force_everywhere()
    {
        var scene = Grid(4000, 1000);
        var idx = new SceneIndex(scene.Entities);
        var rnd = new Random(3);
        int hit = 0;
        for (int k = 0; k < 300; k++)
        {
            double x = rnd.NextDouble() * 1000, y = rnd.NextDouble() * 1000;
            var a = scene.Pick(x, y, 2.0);
            var b = scene.Pick(x, y, 2.0, null, idx);
            Assert.Same(a, b);
            if (a != null) hit++;
        }
        Assert.True(hit >= 10, $"用例本身要能选中点东西(否则一路 null 也算'一致'), 实际只命中 {hit}");
    }

    /// <summary>框选的候选集必须覆盖全场景扫描选出的每一个图元。</summary>
    [Theory]
    [InlineData(true)]      // 交叉选
    [InlineData(false)]     // 窗口选
    public void Indexed_box_select_matches_brute_force(bool crossing)
    {
        var scene = Grid(3000, 500);
        var idx = new SceneIndex(scene.Entities);

        foreach (var (x0, y0, x1, y1) in new[] { (10.0, 10.0, 90.0, 60.0), (0.0, 0.0, 500.0, 500.0), (233.0, 111.0, 240.0, 118.0) })
        {
            var brute = scene.Entities.Where(e => e.Visible && SelectionBox.Match(e, x0, y0, x1, y1, crossing)).ToHashSet();
            var viaIdx = idx.Query(x0, y0, x1, y1).Where(e => e.Visible && SelectionBox.Match(e, x0, y0, x1, y1, crossing)).ToHashSet();
            Assert.True(brute.SetEquals(viaIdx),
                $"框 [{x0},{y0}]-[{x1},{y1}] crossing={crossing}: 全扫 {brute.Count} 个, 索引 {viaIdx.Count} 个");
        }
    }

    /// <summary>查询结果不能重复（同一图元跨多个格子时容易重复吐出来）。</summary>
    [Fact]
    public void Query_does_not_return_duplicates()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 100, Y1 = 100 });   // 一条长线, 必然跨很多格
        for (int i = 0; i < 500; i++) s.Add(new PointEntity { X = i % 25, Y = i / 25 });
        var idx = new SceneIndex(s.Entities);
        var got = idx.Query(-10, -10, 110, 110).ToList();
        Assert.Equal(got.Count, got.Distinct().Count());
    }

    /// <summary>跨格过多的大图元走 oversize 兜底，仍要能被查到。</summary>
    [Fact]
    public void Very_large_entities_are_still_found()
    {
        var s = Grid(2000, 1000);
        var huge = new LineEntity { X0 = -5000, Y0 = -5000, X1 = 5000, Y1 = 5000 };
        s.Add(huge);
        var idx = new SceneIndex(s.Entities);
        Assert.Contains(huge, idx.Query(0, 0, 1, 1));
        Assert.Contains(huge, idx.Query(-4000, -4000, -3999, -3999));
    }

    [Fact]
    public void Empty_and_degenerate_scenes_do_not_throw()
    {
        var empty = new SceneIndex(new List<SceneEntity>());
        Assert.Empty(empty.Query(-1, -1, 1, 1));

        var one = new Scene();
        one.Add(new PointEntity { X = 5, Y = 5 });
        var idx = new SceneIndex(one.Entities);
        Assert.Single(idx.Query(4, 4, 6, 6));
        Assert.Empty(idx.Query(100, 100, 101, 101));
    }

    /// <summary>查询框给反了（x0&gt;x1）也要照样查得到 —— 交叉框选就是右→左拉出来的。</summary>
    [Fact]
    public void Reversed_query_box_is_normalised()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 10, Y0 = 10, X1 = 20, Y1 = 20 });
        var idx = new SceneIndex(s.Entities);
        Assert.Single(idx.Query(30, 30, 5, 5));
    }
}
