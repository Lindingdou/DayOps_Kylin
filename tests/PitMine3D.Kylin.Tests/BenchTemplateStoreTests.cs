using System;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 台阶模板写入（§三四四）。它与 <see cref="BenchTemplateResolver"/> 是同一件事的两头：
/// 这里写、那里读。故本文件的头号判据是 **写进去的读得回来**（端到端往返），
/// 尤其是【排土场模板】那条 —— 两边标记字面量对不上，就是"排土场永不读模板"那个坑重现。
/// </summary>
public class BenchTemplateStoreTests
{
    private static void Clear(DbConnection c)
    {
        using var a = c.CreateCommand();
        a.CommandText = "DELETE FROM template_param_value";
        a.ExecuteNonQuery();
        using var b = c.CreateCommand();
        b.CommandText = "DELETE FROM process_template";
        b.ExecuteNonQuery();
    }

    // ── 端到端往返（存 → 解析器读得回来）────────────────────
    [Fact]
    public void 往返_排土模板存进去解析器读得回来()
    {
        // ★ 头号判据：编辑器写的标记 = 解析器筛的标记
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec
        { Name = "外排土场 v1", IsDump = true, BenchHeight = 22, FaceAngleDeg = 33, BermWidth = 9 });
        Assert.True(r.Ok, r.Error);
        Assert.True(r.Inserted);

        var got = BenchTemplateResolver.Resolve(db.Connection, isDump: true);
        Assert.Equal(r.TemplateId, got.TemplateId);
        Assert.Equal(22, got.BenchHeight, 6);
        Assert.Equal(33, got.FaceAngleDeg, 6);
        Assert.Equal(9, got.BermWidth, 6);
        Assert.Contains("排土模板", got.Provenance);
    }

    [Fact]
    public void 往返_采场模板不会被排土场读走()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec
        { Name = "硬岩标准 v1", IsDump = false, BenchHeight = 15, FaceAngleDeg = 70, BermWidth = 8 });

        Assert.Null(BenchTemplateResolver.Resolve(db.Connection, isDump: true).TemplateId);
        Assert.NotNull(BenchTemplateResolver.Resolve(db.Connection, isDump: false).TemplateId);
    }

    [Fact]
    public void 往返_采场模板的煤台阶也存得下读得回()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec
        {
            Name = "煤岩分层 v1", IsDump = false,
            BenchHeight = 15, FaceAngleDeg = 70, BermWidth = 8,
            CoalBenchHeight = 8, CoalFaceAngleDeg = 62, CoalBermWidth = 5,
        });
        var got = BenchTemplateResolver.Resolve(db.Connection, false);
        Assert.Equal(8, got.CoalBenchHeight, 6);
        Assert.Equal(62, got.CoalFaceAngleDeg, 6);
        Assert.Equal(5, got.CoalBermWidth, 6);
    }

    [Fact]
    public void 往返_排土模板不写煤台阶()
    {
        // 排土场没有煤岩分层这回事 —— 写进去只会给后面读的人一个假参数
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "排土 v1", IsDump = true });
        Assert.True(r.Ok, r.Error);
        var back = BenchTemplateStore.Load(db.Connection, "排土 v1")!;
        // 读回来的是规范默认（因为库里这条模板压根没写煤台阶那三项）
        Assert.Equal(BenchTemplateResolver.DefaultCoalH, back.CoalBenchHeight, 6);
    }

    [Fact]
    public void 往返_最小工作平盘存得下_工作帮坡角才算得对()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec
        { Name = "半连续 v1", IsDump = false, BenchHeight = 15, FaceAngleDeg = 70, BermWidth = 8, MinWorkingBerm = 80 });

        var got = BenchTemplateResolver.Resolve(db.Connection, false);
        Assert.Equal(80, got.MinWorkingBermWidth, 6);
        Assert.True(got.WorkingSlopeAngleDeg < 15, $"80m 工作平盘应当很缓, 实得 {got.WorkingSlopeAngleDeg:0.#}°");
        Assert.True(got.OverallSlopeAngleDeg > got.WorkingSlopeAngleDeg);
    }

    // ── 存 / 改 / 删 ────────────────────────────────────────
    [Fact]
    public void 保存_同名是更新不是新建()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var a = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲", BenchHeight = 12 });
        var b = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲", BenchHeight = 18 });
        Assert.True(a.Inserted);
        Assert.False(b.Inserted);
        Assert.Equal(a.TemplateId, b.TemplateId);              // 主键不变
        Assert.Single(BenchTemplateStore.List(db.Connection));
        Assert.Equal(18, BenchTemplateStore.Load(db.Connection, "甲")!.BenchHeight, 6);
    }

    [Fact]
    public void 保存_改类型时标记跟着换()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲", IsDump = false });
        Assert.False(BenchTemplateStore.List(db.Connection).Single().IsDump);

        BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲", IsDump = true });
        Assert.True(BenchTemplateStore.List(db.Connection).Single().IsDump);
        Assert.NotNull(BenchTemplateResolver.Resolve(db.Connection, isDump: true).TemplateId);
        Assert.Null(BenchTemplateResolver.Resolve(db.Connection, isDump: false).TemplateId);
    }

    [Fact]
    public void 保存_写了哪几项数得出来()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        // 采场：H/α/W/工作平盘 + 煤台阶三项 = 7；不给采宽
        var r = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲", IsDump = false });
        Assert.Equal(7, r.ParamsWritten);
        Assert.Empty(r.MissingCodes);

        // 排土：H/α/W/工作平盘 = 4（不写煤台阶）
        Clear(db.Connection);
        var d = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "乙", IsDump = true });
        Assert.Equal(4, d.ParamsWritten);
    }

    [Fact]
    public void 保存_给了采宽才写采宽()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var with = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲", MiningWidth = 40 });
        Assert.Equal(8, with.ParamsWritten);
        Assert.Equal(40, BenchTemplateResolver.Resolve(db.Connection, false).MiningWidth);

        Clear(db.Connection);
        var without = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲", MiningWidth = 0 });
        Assert.Equal(7, without.ParamsWritten);
        Assert.Null(BenchTemplateResolver.Resolve(db.Connection, false).MiningWidth);
    }

    [Fact]
    public void 删除_连参数值一起删()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "甲" });
        Assert.Equal("", BenchTemplateStore.Delete(db.Connection, "甲"));
        Assert.Empty(BenchTemplateStore.List(db.Connection));
        Assert.Null(BenchTemplateResolver.Resolve(db.Connection, false).TemplateId);

        using var q = db.Connection.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM template_param_value";
        Assert.Equal(0L, Convert.ToInt64(q.ExecuteScalar()));
    }

    [Fact]
    public void 删除_库里没有时说清楚而不是假装删了()
        => Assert.Contains("没有模板", BenchTemplateStore.Delete(TestDb.Open().Connection, "根本没有这个"));

    // ── 校验 ────────────────────────────────────────────────
    [Theory]
    [InlineData(0.0, 70.0, "台阶高必须 > 0")]
    [InlineData(-1.0, 70.0, "台阶高必须 > 0")]
    [InlineData(12.0, 0.0, "坡面角")]
    [InlineData(12.0, 90.0, "坡面角")]
    [InlineData(12.0, 120.0, "坡面角")]
    public void 校验_参数不合法就别落库(double h, double a, string msg)
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec
        { Name = "甲", BenchHeight = h, FaceAngleDeg = a });
        Assert.False(r.Ok);
        Assert.Contains(msg, r.Error);
        Assert.Empty(BenchTemplateStore.List(db.Connection));   // 一条都没建
    }

    [Fact]
    public void 校验_平盘宽不能为负()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Contains("平盘宽", BenchTemplateStore.Save(db.Connection,
            new BenchTemplateSpec { Name = "甲", BermWidth = -1 }).Error);
    }

    [Fact]
    public void 校验_空名与没有连接分别报()
    {
        using var db = TestDb.Open();
        Assert.Contains("模板名", BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "  " }).Error);
        Assert.Contains("数据库连接", BenchTemplateStore.Save(null, new BenchTemplateSpec { Name = "甲" }).Error);
        Assert.Contains("模板名", BenchTemplateStore.Save(db.Connection, null).Error);
    }

    [Fact]
    public void 兜底_名字里的单引号不炸SQL()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var r = BenchTemplateStore.Save(db.Connection, new BenchTemplateSpec { Name = "O'Brien 标准" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("O'Brien 标准", BenchTemplateStore.List(db.Connection).Single().Name);
        Assert.NotNull(BenchTemplateStore.Load(db.Connection, "O'Brien 标准"));
    }

    [Fact]
    public void 兜底_读一个不存在的模板返回空()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Null(BenchTemplateStore.Load(db.Connection, "没有这个"));
        Assert.Null(BenchTemplateStore.Load(null, "甲"));
        Assert.Empty(BenchTemplateStore.List(null));
    }

    [Fact]
    public void 兜底_读回来的缺项落规范默认而不是零()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        // 直接建一条只有 H 的模板（绕过 Save，模拟别处/旧版写进来的半截数据）
        using (var c = db.Connection.CreateCommand())
        {
            c.CommandText = "INSERT INTO process_template (code, name, description, is_current, status) "
                          + "VALUES ('半截','半截','" + BenchTemplateResolver.PitTemplateMark + "',1,'active')";
            c.ExecuteNonQuery();
        }
        var back = BenchTemplateStore.Load(db.Connection, "半截")!;
        Assert.True(back.FaceAngleDeg > 0, "坡面角不能落成 0");
        Assert.True(back.MinWorkingBerm > 0, "工作平盘不能落成 0");
    }
}
