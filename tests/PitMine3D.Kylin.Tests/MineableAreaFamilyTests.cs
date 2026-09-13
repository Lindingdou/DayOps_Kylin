using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Data.Entities;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「采场/排土场圈定」家族（原 PlanLib.ShortTerm.RegionGeometry + 宿主 RegionBrushSession + IMineableRegionService）：
/// 区域重叠判定与求差 · 选区笔刷涂改（并入/移出/擦空/切碎留大块）· mineable_region 表读写。纯托管 + 内存 SQLite。
/// </summary>
public class MineableAreaFamilyTests
{
    private static double[] Rect(double x0, double y0, double x1, double y1, double z = 100)
        => new[] { x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z };

    private static double AreaXy(double[] r)
    {
        int n = r.Length / 3; double s = 0;
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; s += r[i * 3] * r[j * 3 + 1] - r[j * 3] * r[i * 3 + 1]; }
        return Math.Abs(s) * 0.5;
    }

    [Fact]
    public void 重叠判定_成片重叠算_仅边界相邻不算_包围盒不交不算()
    {
        var a = Rect(0, 0, 100, 100);
        Assert.True(RegionGeometry.Overlaps(a, Rect(50, 50, 150, 150)));       // 1/4 重叠
        Assert.False(RegionGeometry.Overlaps(a, Rect(100, 0, 200, 100)));      // 共边相邻
        Assert.False(RegionGeometry.Overlaps(a, Rect(300, 300, 400, 400)));    // 不相交
        Assert.False(RegionGeometry.Overlaps(a, Rect(99.5, 0, 200, 100)));     // 0.5% 薄条 < 2%
        Assert.False(RegionGeometry.Overlaps(a, Array.Empty<double>()));
    }

    [Fact]
    public void 求差_保留最大块_完全覆盖返回空_Z按主体平面()
    {
        var subject = Rect(0, 0, 100, 100, 250);
        var res = RegionGeometry.SubtractKeepLargest(subject, Rect(50, -10, 110, 110));   // 切掉东半
        Assert.True(res.Length >= 9);
        Assert.InRange(AreaXy(res), 4200, 5300);                                      // ≈ 50×100
        Assert.True(res.Where((_, i) => i % 3 == 0).Max() <= 51.5);                    // 东边界 ≤ 50 附近
        Assert.All(Enumerable.Range(0, res.Length / 3), i => Assert.Equal(250, res[i * 3 + 2], 3));   // Z 沿主体平面
        Assert.Empty(RegionGeometry.SubtractKeepLargest(subject, Rect(-10, -10, 110, 110)));
        // 切成两块（中间挖一条竖槽）→ 只留最大块
        var split = RegionGeometry.SubtractKeepLargest(subject, Rect(30, -10, 40, 110));
        Assert.InRange(AreaXy(split), 5400, 6300);                                     // 东块 60×100
        Assert.Equal(subject, RegionGeometry.SubtractKeepLargest(subject, Array.Empty<double>()));
    }

    [Fact]
    public void 笔刷_普通涂移出_Alt涂并入_擦空_减法切碎只留大块()
    {
        var target = Rect(0, 0, 100, 100, 80);
        var s = new RegionBrushSession(target, 0xD85A30u, new[] { Rect(200, 0, 300, 100) }, new[] { 0x2E6FCFu }, 1.0);
        Assert.Equal(80, s.RepZ, 6);
        Assert.Single(s.Context);
        double a0 = AreaXy(s.Ring);
        Assert.InRange(a0, 9500, 10500);

        // 普通涂（移出）：在东北角挖掉半径 20 的一笔
        s.StrokeBegin(add: false); s.StrokeMove(100, 100, 20); s.StrokeEnd();
        double a1 = AreaXy(s.Ring);
        Assert.True(a1 < a0 - 200, $"{a0} → {a1}");
        Assert.True(s.HasArea());

        // Alt 涂（并入）：贴着西边外侧拖一笔（与选区连通才算进环；孤立的一笔留在掩膜里但不进外轮廓，与原版同）
        s.StrokeBegin(add: true); s.StrokeMove(-10, 50, 15); s.StrokeMove(-10, 80, 15); s.StrokeEnd();
        double a2 = AreaXy(s.Ring);
        Assert.True(a2 > a1 + 300, $"{a1} → {a2}");
        Assert.True(s.Ring.Where((_, i) => i % 3 == 0).Min() < -20);   // 环西扩到 x<−20

        // 减法切碎：竖着一刀从 x=50 上下穿透 → 只留大块（西块含新并入的部分更大）
        s.StrokeBegin(add: false); s.StrokeMove(52, -30, 6); s.StrokeMove(52, 130, 6); s.StrokeEnd();
        double a3 = AreaXy(s.Ring);
        Assert.InRange(a3, 4000, 7500);
        Assert.True(s.Ring.Where((_, i) => i % 3 == 0).Max() < 50);     // 留的是西块

        // 擦空
        s.StrokeBegin(add: false);
        for (double y = -40; y <= 140; y += 20) s.StrokeMove(30, y, 120);
        s.StrokeEnd();
        Assert.False(s.HasArea());
        Assert.Empty(s.Ring);
    }

    private static SqliteConnection Db()
    {
        var c = new SqliteConnection("Data Source=:memory:"); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE mineable_region (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL DEFAULT '', points_json TEXT NOT NULL DEFAULT '[]', visible INTEGER NOT NULL DEFAULT 1, note TEXT, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, category TEXT NOT NULL DEFAULT 'mineable', color TEXT, active INTEGER NOT NULL DEFAULT 1)";
        cmd.ExecuteNonQuery();
        return c;
    }

    [Fact]
    public void 区域表_增改删查_颜色去井号_无连接不抛()
    {
        using var c = Db();
        var e = new MineableRegion { Name = "采场1", Category = MineableRegion.CatPit, PointsJson = "[0,0,100,10,0,100,10,10,100]", Visible = 1, Color = "#D85A30" };
        long id = MineableRegionRepo.Insert(c, e, out string err);
        Assert.Equal("", err); Assert.True(id > 0); Assert.Equal(id, e.Id);
        var all = MineableRegionRepo.All(c);
        Assert.Single(all);
        Assert.Equal("采场1", all[0].Name); Assert.Equal("pit", all[0].Category); Assert.Equal("D85A30", all[0].Color); Assert.Equal(1, all[0].Visible);

        all[0].Name = "东采场"; all[0].Visible = 0; all[0].Color = null; all[0].Category = MineableRegion.CatInternalDump;
        Assert.Equal("", MineableRegionRepo.Update(c, all[0]));
        var again = MineableRegionRepo.All(c)[0];
        Assert.Equal("东采场", again.Name); Assert.Equal(0, again.Visible); Assert.Null(again.Color); Assert.Equal("internal_dump", again.Category);

        Assert.Equal("", MineableRegionRepo.Delete(c, id));
        Assert.Empty(MineableRegionRepo.All(c));

        Assert.Empty(MineableRegionRepo.All(null));
        Assert.Equal(0, MineableRegionRepo.Insert(null, e, out var e2)); Assert.Contains("连接", e2);
        Assert.Contains("连接", MineableRegionRepo.Update(null, e));
        Assert.Contains("连接", MineableRegionRepo.Delete(null, 1));
    }

    [Fact]
    public void 类别语义_工作帮对母范围豁免_跨门类照常()
    {
        Assert.True(MineableRegion.IsGateOverItsParent(MineableRegion.CatPitWorkingSlope, MineableRegion.CatPit));
        Assert.True(MineableRegion.IsGateOverItsParent(MineableRegion.CatExternalDump, MineableRegion.CatDumpWorkingSlope));
        Assert.False(MineableRegion.IsGateOverItsParent(MineableRegion.CatPitWorkingSlope, MineableRegion.CatExternalDump));
        Assert.False(MineableRegion.IsGateOverItsParent(MineableRegion.CatDumpWorkingSlope, MineableRegion.CatPit));
        Assert.True(MineableRegion.IsWorkingSlope(MineableRegion.CatDumpWorkingSlope));
        Assert.Equal("未分类", MineableRegion.DisplayName(MineableRegion.CatMineable));
        Assert.Equal("剥采工作帮", MineableRegion.DisplayName(MineableRegion.CatPitWorkingSlope));
    }
}
