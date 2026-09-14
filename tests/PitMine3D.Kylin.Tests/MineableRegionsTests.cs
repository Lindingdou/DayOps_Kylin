using System;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 作业区域台账（§三五一）。这张表 V015/V016/V018 建好之后一直零消费者，本轮接上。
///
/// 头号判据是原版守的那条：**Z 没有出处就拒绝入库** ——
/// 写 0 会被推演当成台账实测高程，层体整体摆在 0 米，而且**一路不会报错**。
/// 其次是关联诊断的五条：选定 / 极性 / 几何 / 高程 / 绑定 —— 它们也全都不会报错，故必须逐条判出来。
/// </summary>
public class MineableRegionsTests
{
    private static void Clear(DbConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM mineable_region";
        cmd.ExecuteNonQuery();
    }

    private static RegionRecord Ring(string name, string cat = MineableRegions.CatMineable,
                                     double z = 1200, bool active = true, int n = 4)
    {
        var r = new RegionRecord
        {
            Name = name, Category = cat,
            Note = (active ? MineableRegions.ActiveTag : "") + MineableRegions.ZTag + "现状面采样;",
        };
        // 一个 100×100 的方环（n=4）或退化环
        var pts = new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0) };
        foreach (var (x, y) in pts.Take(n)) { r.Points.Add(x); r.Points.Add(y); r.Points.Add(z); }
        return r;
    }

    // ── 头号判据：Z 没有出处就拒绝入库 ──────────────────────
    [Fact]
    public void 入库_Z没有出处时拒绝()
    {
        // ★ 写 0 会被推演当成台账实测高程，层体整体摆在 0 米，而且一路不报错
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = Ring("北一采");
        r.Note = MineableRegions.ActiveTag;          // 没有 Z: 出处
        string err = MineableRegions.Save(db.Connection, r, out long id);
        Assert.Contains("Z 没有出处", err);
        Assert.Contains("0 米", err);
        Assert.Equal(0, id);
        Assert.Empty(MineableRegions.List(db.Connection));
    }

    [Fact]
    public void 入库_有出处就存得下()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Equal("", MineableRegions.Save(db.Connection, Ring("北一采"), out long id));
        Assert.True(id > 0);
        var back = MineableRegions.List(db.Connection).Single();
        Assert.Equal("北一采", back.Name);
        Assert.Equal(4, back.PointCount);
        Assert.Equal("现状面采样", back.ZProvenance);
    }

    [Fact]
    public void 入库_顶点不足三个时拒绝()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        string err = MineableRegions.Save(db.Connection, Ring("退化", n: 2), out _);
        Assert.Contains("需 ≥3", err);
        Assert.Empty(MineableRegions.List(db.Connection));
    }

    [Fact]
    public void 入库_空名与没有连接分别报()
    {
        using var db = TestDb.Open();
        Assert.Contains("区域名", MineableRegions.Save(db.Connection, Ring("  "), out _));
        Assert.Contains("数据库连接", MineableRegions.Save(null, Ring("甲"), out _));
        Assert.Contains("没有要保存", MineableRegions.Save(db.Connection, null, out _));
    }

    // ── 存读改删 ────────────────────────────────────────────
    [Fact]
    public void 存读_往返一致含类别颜色可见()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = Ring("北排土场", MineableRegions.CatExternalDump, z: 1150);
        r.Color = "AABBCC";
        r.Visible = false;
        MineableRegions.Save(db.Connection, r, out _);

        var back = MineableRegions.List(db.Connection).Single();
        Assert.Equal(MineableRegions.CatExternalDump, back.Category);
        Assert.Equal("AABBCC", back.Color);
        Assert.False(back.Visible);
        Assert.Equal(1150, back.AvgZ, 6);
    }

    [Fact]
    public void 存读_更新走同一条不新增()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        MineableRegions.Save(db.Connection, Ring("甲"), out long id);
        var r = MineableRegions.List(db.Connection).Single();
        r.Name = "甲改";
        MineableRegions.Save(db.Connection, r, out long id2);
        Assert.Equal(id, id2);
        Assert.Single(MineableRegions.List(db.Connection));
        Assert.Equal("甲改", MineableRegions.List(db.Connection).Single().Name);
    }

    [Fact]
    public void 删除_删掉之后列表里没有()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        MineableRegions.Save(db.Connection, Ring("甲"), out long id);
        Assert.Equal("", MineableRegions.Delete(db.Connection, id));
        Assert.Empty(MineableRegions.List(db.Connection));
    }

    [Fact]
    public void 兜底_名字里的单引号不炸SQL()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        MineableRegions.Save(db.Connection, Ring("O'Brien 采区"), out _);
        Assert.Equal("O'Brien 采区", MineableRegions.List(db.Connection).Single().Name);
    }

    [Fact]
    public void 兜底_没有连接时列表为空不抛()
        => Assert.Empty(MineableRegions.List(null));

    // ── 几何 ────────────────────────────────────────────────
    [Fact]
    public void 几何_面积按鞋带公式且与绕向无关()
    {
        var cw = Ring("顺");
        var ccw = new RegionRecord { Name = "逆", Note = cw.Note };
        for (int i = cw.PointCount - 1; i >= 0; i--)
        { ccw.Points.Add(cw.Points[i * 3]); ccw.Points.Add(cw.Points[i * 3 + 1]); ccw.Points.Add(cw.Points[i * 3 + 2]); }
        Assert.Equal(10000, cw.AreaM2, 6);
        Assert.Equal(cw.AreaM2, ccw.AreaM2, 6);
    }

    [Fact]
    public void 几何_没有顶点时平均Z是判不了而不是零()
    {
        // ★ 0 是个合法标高，用它表示"没有"就分不开了
        Assert.True(double.IsNaN(new RegionRecord().AvgZ));
        Assert.Equal(0, new RegionRecord().AreaM2, 9);
    }

    [Fact]
    public void 几何_只取XY环给范围裁剪用()
    {
        var r = Ring("甲");
        var xy = MineableRegions.RingXy(r);
        Assert.Equal(8, xy.Length);
        Assert.Equal(new[] { 0.0, 0.0, 100.0, 0.0, 100.0, 100.0, 0.0, 100.0 }, xy);
    }

    [Theory]
    [InlineData("[1,2,3,4,5,6,7,8,9]", 9)]
    [InlineData("[1,2,3,4]", 3)]        // 半个点截掉
    [InlineData("[]", 0)]
    [InlineData("不是 JSON", 0)]
    [InlineData(null, 0)]
    public void 几何_顶点解析不了就当空不抛(string? json, int expect)
        => Assert.Equal(expect, MineableRegions.ParseXyz(json).Count);

    // ── 类别与配色 ──────────────────────────────────────────
    [Theory]
    [InlineData(MineableRegions.CatPit, "采场", false)]
    [InlineData(MineableRegions.CatExternalDump, "外排土场", true)]
    [InlineData(MineableRegions.CatInternalDump, "内排土场", true)]
    [InlineData(MineableRegions.CatMineable, "可采区域", false)]
    [InlineData("乱写", "可采区域", false)]
    public void 类别_中文名与排土判定(string cat, string zh, bool isDump)
    {
        Assert.Equal(zh, MineableRegions.CategoryZh(cat));
        Assert.Equal(isDump, new RegionRecord { Category = cat }.IsDump);
    }

    [Fact]
    public void 类别_四种默认色互不相同()
    {
        var cats = new[] { MineableRegions.CatPit, MineableRegions.CatExternalDump,
                           MineableRegions.CatInternalDump, MineableRegions.CatMineable };
        var colors = cats.Select(MineableRegions.DefaultColor).ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    // ── 关联诊断（五条全都不会报错，故必须逐条判）────────────
    private static SinkRegistry Sinks(params string[] names)
    {
        var r = new SinkRegistry();
        int i = 1;
        foreach (var n in names)
            r.Put(new SinkNode { Id = "D" + i++, Name = n, Kind = SinkKind.ExternalDump, DesignCapacityM3 = 1e7 });
        return r;
    }

    [Fact]
    public void 诊断_五条都过时判为可进推演()
    {
        var d = MineableRegions.Diagnose(Ring("北排土场", MineableRegions.CatExternalDump), Sinks("北排土场"));
        Assert.True(d.Usable);
        Assert.Equal(5, d.Lines.Count);
    }

    [Fact]
    public void 诊断_没选定时排在最前且判不可用()
    {
        var d = MineableRegions.Diagnose(Ring("甲", active: false), null);
        Assert.StartsWith("⓪ 本期未选定", d.Lines[0]);
        Assert.False(d.Usable);
    }

    [Fact]
    public void 诊断_极性按类别给且点明填反不会报错()
    {
        var pit = MineableRegions.Diagnose(Ring("采场", MineableRegions.CatPit), null);
        var dump = MineableRegions.Diagnose(Ring("外排", MineableRegions.CatExternalDump), Sinks("外排"));
        Assert.Contains("内缩", pit.Lines[1]);
        Assert.Contains("外扩", dump.Lines[1]);
        Assert.Contains("不会报错", pit.Lines[1]);
    }

    [Fact]
    public void 诊断_顶点不足时说明推演会静默跳过()
    {
        var d = MineableRegions.Diagnose(Ring("退化", n: 2), null);
        Assert.Contains("静默跳过", d.Lines[2]);
        Assert.False(d.Usable);
    }

    [Fact]
    public void 诊断_全环Z为零且无出处时点明会被当成实测高程()
    {
        var r = Ring("甲", z: 0);
        r.Note = MineableRegions.ActiveTag;          // 去掉 Z 出处
        var d = MineableRegions.Diagnose(r, null);
        Assert.Contains("Z=0", d.Lines[3]);
        Assert.Contains("实测高程", d.Lines[3]);
        Assert.False(d.Usable);
    }

    [Fact]
    public void 诊断_排土类对不上去向时点名说收不到量()
    {
        var d = MineableRegions.Diagnose(Ring("南排土场", MineableRegions.CatExternalDump), Sinks("北排土场"));
        Assert.Contains("收不到量", d.Lines[4]);
        Assert.False(d.Usable);
    }

    [Fact]
    public void 诊断_采场类不需要对上去向()
    {
        var d = MineableRegions.Diagnose(Ring("北一采", MineableRegions.CatPit), null);
        Assert.Contains("不需对上去向", d.Lines[4]);
        Assert.True(d.Usable);
    }

    [Fact]
    public void 诊断_去向台账读不出来时是判不了而不是通过()
    {
        // ★ 判不了不能当成通过 —— 那样会让人以为这块区域进得了推演
        var d = MineableRegions.Diagnose(Ring("北排土场", MineableRegions.CatExternalDump), null);
        Assert.Contains("判不了", d.Lines[4]);
        Assert.False(d.Usable);
    }

    [Fact]
    public void 诊断_名字包含也算对得上()
    {
        var d = MineableRegions.Diagnose(Ring("北排", MineableRegions.CatExternalDump), Sinks("北排土场"));
        Assert.Contains("对上去向", d.Lines[4]);
        Assert.True(d.Usable);
    }
}
