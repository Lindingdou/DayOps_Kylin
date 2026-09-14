using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 编辑命令拖拽期的幽灵预览 —— 对照原 xllAcEd 的 PreviewProxy.h / AcDbEntity::buildPreviewProxy。
/// 判据写错不报错也不留日志，只会静默退化成"全是真几何"(拖起来卡回原样)或"什么都不画"(幽灵没了)，
/// 两头都不出异常 —— 所以阈值和段数上限只能靠这里钉住。
/// </summary>
public class DragPreviewTests
{
    private static PolylineEntity Line(int n)
    {
        var pl = new PolylineEntity();
        for (int i = 0; i < n; i++) pl.Points.Add((i, i % 7));
        return pl;
    }

    /// <summary>格网三角网：cols×rows 顶点，每格两个三角。</summary>
    private static MeshEntity Grid(int cols, int rows)
    {
        var m = new MeshEntity();
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
                m.Verts.Add((i, j, (i + j) % 5));
        for (int j = 0; j + 1 < rows; j++)
            for (int i = 0; i + 1 < cols; i++)
            {
                int a = j * cols + i;
                m.Tris.Add((a, a + 1, a + cols));
                m.Tris.Add((a + 1, a + cols + 1, a + cols));
            }
        m.Invalidate();
        return m;
    }

    // ── 降级判据(两道闸) ──

    [Fact]
    public void Single_heavy_entity_is_demoted_regardless_of_budget()
    {
        int budget = DragPreview.TotalBudget;
        Assert.False(DragPreview.UsesRealGeometry(DragPreview.HeavyEntityCost + 1, ref budget));
        Assert.Equal(DragPreview.TotalBudget, budget);   // 降级的不占预算
    }

    [Fact]
    public void Medium_entities_are_demoted_once_the_total_budget_runs_out()
    {
        // 每个都不超"单实体重"那条闸, 但加起来会把帧率拖死 —— 超预算的部分照样降级
        int budget = DragPreview.TotalBudget;
        int taken = 0;
        while (DragPreview.UsesRealGeometry(50000, ref budget)) taken++;
        Assert.Equal(4, taken);                          // 200000 / 50000
        Assert.True(budget < 50000);
    }

    [Fact]
    public void Proxy_segment_budget_is_shared_and_never_below_the_floor()
    {
        Assert.Equal(0, DragPreview.ProxySegmentBudget(0));
        Assert.Equal(DragPreview.ProxyTotalSegments, DragPreview.ProxySegmentBudget(1));
        Assert.Equal(DragPreview.ProxyTotalSegments / 10, DragPreview.ProxySegmentBudget(10));
        // 选集里重实体很多时宁可总量略超, 也不能稀到看不出是什么
        Assert.Equal(DragPreview.ProxyMinSegments, DragPreview.ProxySegmentBudget(1000));
    }

    // ── 各实体的代价与替身 ──

    [Fact]
    public void Preview_cost_is_o1_and_matches_the_tessellated_vertex_count()
    {
        Assert.Equal(2000, Line(1000).PreviewCost());
        var m = Grid(4, 4);
        Assert.Equal(m.Tris.Count * 6, m.PreviewCost());
        Assert.False(m.HasEdgeCache);                    // 算代价不许去建边表
        Assert.Equal(500, new PointCloudEntity("c", Enumerable.Range(0, 500).Select(i => ((double)i, 0.0, 0.0))).PreviewCost());
    }

    [Fact]
    public void Polyline_proxy_keeps_the_shape_within_budget_and_never_loses_the_tail()
    {
        var pl = Line(50000);
        var o = new List<(double x, double y, double z)>();
        pl.BuildPreviewProxy(o, 100);
        Assert.InRange(o.Count / 2, 1, 101);             // 首尾必留, 至多多出一段
        Assert.Equal(0.0, o[0].x);                       // 起点
        Assert.Equal(49999.0, o[^1].x);                  // 尾点 —— 丢了它长境界线拖起来会"短一截"
    }

    [Fact]
    public void Closed_polyline_proxy_returns_to_the_start()
    {
        var pl = Line(500);
        pl.Closed = true;
        var o = new List<(double x, double y, double z)>();
        pl.BuildPreviewProxy(o, 20);
        Assert.Equal(0.0, o[^1].x);                      // 最后一段回到起点
    }

    [Fact]
    public void Mesh_proxy_is_a_decimated_net_of_real_vertices_within_budget()
    {
        var m = Grid(120, 120);                          // 1.4 万顶点 / 2.8 万三角
        Assert.True(m.PreviewCost() > DragPreview.HeavyEntityCost);
        var o = new List<(double x, double y, double z)>();
        m.BuildPreviewProxy(o, 800);
        Assert.True(o.Count > 0);
        Assert.True(o.Count / 2 <= 800, $"替身 {o.Count / 2} 段超预算");
        // 代表点取的是【真实顶点】而不是格心 —— 替身的每个点都真的在这张网上
        var verts = new HashSet<(double, double, double)>(m.Verts.Select(v => (v.x, v.y, v.z)));
        Assert.All(o, p => Assert.Contains((p.x, p.y, p.z), verts));
    }

    [Fact]
    public void Degenerate_mesh_falls_back_to_the_bounding_box()
    {
        // 顶点全挤在一格(纯竖直的一线墙): 连不出边 → 退包围盒的 12 条棱, 不交白卷
        var m = new MeshEntity();
        m.Verts.AddRange(new[] { (0.0, 0.0, 0.0), (0.0, 0.0, 10.0), (0.0, 0.0, 20.0) });
        m.Tris.Add((0, 1, 2));
        m.Invalidate();
        var o = new List<(double x, double y, double z)>();
        m.BuildPreviewProxy(o, 600);
        Assert.Equal(24, o.Count);                       // 12 条棱
    }

    [Fact]
    public void Point_cloud_draws_a_box_instead_of_nothing()
    {
        // 点云的 Tessellate 是空的(点走 GL_POINTS 通道), 不特判就等于"拖着点云走, 屏幕上什么都没有"
        var pc = new PointCloudEntity("c", new[] { (0.0, 0.0, 0.0), (10.0, 20.0, 30.0) });
        var plain = new List<float>();
        pc.Tessellate(plain);
        Assert.Empty(plain);
        var ghost = new List<float>();
        pc.TessellatePreview(ghost);
        Assert.Equal(12, ghost.Count / 12);              // 包围盒 12 条棱
        var o = new List<(double x, double y, double z)>();
        pc.BuildPreviewProxy(o, 600);
        Assert.Equal(24, o.Count);
    }

    // ── 备料 + 逐帧搬点 ──

    [Fact]
    public void Light_selection_ghost_follows_the_transform()
    {
        var pl = new PolylineEntity();
        pl.Points.AddRange(new[] { (0.0, 0.0), (10.0, 0.0) });
        var dp = DragPreview.Build(new SceneEntity[] { pl });
        Assert.False(dp.Decimated);
        Assert.Equal(0, dp.ProxySegments);

        var o = new List<float>();
        dp.Append(o, Affine2.Translate(100, 200), 0.5f, 0.6f, 0.7f);
        Assert.Equal(1, o.Count / 12);
        Assert.Equal(100f, o[0], 3);                     // 起点被平移
        Assert.Equal(200f, o[1], 3);
        Assert.Equal(110f, o[6], 3);
        Assert.Equal(0.5f, o[3], 3);                     // 统一改成预览色
    }

    [Fact]
    public void Heavy_selection_is_decimated_and_still_follows_the_transform()
    {
        var m = Grid(150, 150);
        var dp = DragPreview.Build(new SceneEntity[] { m });
        Assert.True(dp.Decimated, "两万多三角的网必须降级, 否则每帧重镶嵌拖不动");
        Assert.Equal(0, dp.LightVertices);
        Assert.True(dp.ProxySegments <= DragPreview.ProxyTotalSegments);

        var a = new List<float>();
        dp.Append(a, Affine2.Translate(0, 0), 0.5f, 0.6f, 0.7f);
        var b = new List<float>();
        dp.Append(b, Affine2.Translate(1000, 0), 0.5f, 0.6f, 0.7f);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a[0] + 1000f, b[0], 2);
    }

    [Fact]
    public void Mixed_selection_splits_into_real_geometry_and_proxies()
    {
        var light = new PolylineEntity();
        light.Points.AddRange(new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 0.0) });
        var dp = DragPreview.Build(new SceneEntity[] { light, Grid(150, 150) });
        Assert.Equal(4, dp.LightVertices);               // 轻的那条照画真几何(2 段)
        Assert.True(dp.ProxySegments > 0);               // 重的那张走替身
        Assert.True(dp.Decimated);                       // → 提示语要跟着出"预览已抽稀"
    }
}
