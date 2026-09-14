using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad.Transport;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 约束条件设置（§三三九）：派生量公式、方案容器、校验判据、卡车预设。
///
/// 判据分两类：**公式对不对**（路面宽/通过能力/年运力/展线长/最小平曲线半径三取一），
/// 和**"没有的参数绝不编"**（在册车型只带设备库确有的字段，其余留 null）。
/// </summary>
public class TransportConstraintTests
{
    // ── 派生量公式 ──────────────────────────────────────────
    [Fact]
    public void 公式_路面宽度按车道车宽间隙安全带算()
    {
        var s = new TransportConstraintSettings
        { LaneCount = 2, TruckWidth = 6.0, LaneClearance = 1.0, SafetyStrip = 1.0 };
        // B = 2×6 + 3×1 + 2×1 = 17
        Assert.Equal(17.0, s.RoadWidthPreview(), 6);
    }

    [Fact]
    public void 公式_安全车挡高是轮胎直径的三分之二()
    {
        var s = new TransportConstraintSettings { TireDiameter = 3.0 };
        Assert.Equal(2.0, s.BermHeightPreview(), 6);
    }

    [Fact]
    public void 公式_通过能力与年运力()
    {
        var s = new TransportConstraintSettings
        { HeadwaySec = 30, LaneCount = 2, UtilizationPct = 80, WorkHoursPerYear = 5000, TruckPayload = 90 };
        Assert.Equal(120, s.LaneCapacityPreview(), 6);              // 3600/30
        Assert.Equal(120 * 2 * 0.8, s.RoadCapacityPreview(), 6);     // ×车道×利用率
        Assert.Equal(192 * 5000 * 90, s.AnnualCapacityTonsPreview(), 3);
    }

    [Fact]
    public void 公式_车头时距为零时通过能力算零而不是无穷()
    {
        var s = new TransportConstraintSettings { HeadwaySec = 0 };
        Assert.Equal(0, s.LaneCapacityPreview(), 6);
        Assert.Equal(0, s.AnnualCapacityTonsPreview(), 6);
    }

    [Fact]
    public void 公式_展线长是台阶高除以坡度()
    {
        var s = new TransportConstraintSettings { RefBenchHeight = 15, MaxGradePct = 8 };
        Assert.Equal(15 / 0.08, s.DevelopmentLengthPreview(), 6);
        Assert.Equal(0, new TransportConstraintSettings { MaxGradePct = 0 }.DevelopmentLengthPreview(), 6);
    }

    [Fact]
    public void 公式_最小平曲线半径按车速反算()
    {
        var s = new TransportConstraintSettings { DesignSpeedKmh = 25, MaxSuperelevationPct = 6 };
        // R = v²/(127(μ+e)) = 625/(127×0.21)
        Assert.Equal(625.0 / (127.0 * 0.21), s.MinCurveRadiusBySpeed(), 6);
    }

    [Fact]
    public void 公式_实际最小平曲线半径是三者取大()
    {
        // 车速反算 / 车辆转弯 / 设定下限，物理+设备双下限
        var s = new TransportConstraintSettings
        { DesignSpeedKmh = 25, MaxSuperelevationPct = 6, TruckTurnRadius = 10, MinCurveRadiusM = 15 };
        double eff = s.EffectiveMinCurveRadiusM();
        Assert.Equal(Math.Max(Math.Max(s.MinCurveRadiusBySpeed(), 10), 15), eff, 6);

        // 把设定下限抬到最高 → 由它控制
        s.MinCurveRadiusM = 9999;
        Assert.Equal(9999, s.EffectiveMinCurveRadiusM(), 6);
    }

    [Theory]
    [InlineData(25.0, 6.0, 10.0, 15.0, "车速反算控制")]
    [InlineData(5.0, 6.0, 300.0, 15.0, "车辆转弯控制")]
    [InlineData(5.0, 6.0, 10.0, 9999.0, "设定下限控制")]
    public void 文案_点名是哪一项在控制最小平曲线半径(double v, double e, double turn, double floor, string who)
    {
        // 不点名的话，用户改了"设定下限"看不到任何变化，会以为参数没生效
        var s = new TransportConstraintSettings
        { DesignSpeedKmh = v, MaxSuperelevationPct = e, TruckTurnRadius = turn, MinCurveRadiusM = floor };
        Assert.Contains(who, TransportConstraintCheck.DescribeEffectiveCurveRadius(s));
    }

    [Fact]
    public void 文案_自动计算区六行都在()
    {
        var lines = TransportConstraintCheck.DerivedLines(new TransportConstraintSettings());
        Assert.Equal(6, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("路面宽度 B"));
        Assert.Contains(lines, l => l.StartsWith("安全车挡高"));
        Assert.Contains(lines, l => l.StartsWith("通过能力"));
        Assert.Contains(lines, l => l.StartsWith("单条路年运力"));
        Assert.Contains(lines, l => l.StartsWith("展线长"));
        Assert.Contains(lines, l => l.StartsWith("实际最小平曲线半径"));
    }

    // ── 校验判据 ────────────────────────────────────────────
    [Fact]
    public void 硬错_默认配置放行()
        => Assert.Empty(TransportConstraintCheck.CollectHardErrors(new TransportConstraintSettings()));

    [Fact]
    public void 硬错_弯道纵坡不能比限制坡度还陡()
    {
        var s = new TransportConstraintSettings { MaxGradePct = 8, CurveMaxGradePct = 10 };
        Assert.Contains(TransportConstraintCheck.CollectHardErrors(s), e => e.Contains("弯道只能更缓"));
    }

    [Fact]
    public void 硬错_缓坡段不能比限制坡度还陡()
    {
        var s = new TransportConstraintSettings { MaxGradePct = 3, EaseGradePct = 5 };
        Assert.Contains(TransportConstraintCheck.CollectHardErrors(s), e => e.Contains("缓坡段只能更缓"));
    }

    [Fact]
    public void 硬错_超高吃满合成坡度时纵坡没余量()
    {
        var s = new TransportConstraintSettings { MaxSuperelevationPct = 8, MaxResultantGradePct = 8 };
        Assert.Contains(TransportConstraintCheck.CollectHardErrors(s), e => e.Contains("纵坡没有余量"));
    }

    [Fact]
    public void 硬错_缓坡段最小长度要满足最小坡长()
    {
        var s = new TransportConstraintSettings { EaseMinLengthM = 20, MinGradeSectionLengthM = 50 };
        Assert.Contains(TransportConstraintCheck.CollectHardErrors(s), e => e.Contains("不满足最小坡长"));
    }

    [Fact]
    public void 硬错_不设缓坡段时不因长度报错()
    {
        // EaseMinLengthM = 0 表示"不设"，不该被最小坡长卡住
        var s = new TransportConstraintSettings { EaseMinLengthM = 0, MinGradeSectionLengthM = 50 };
        Assert.DoesNotContain(TransportConstraintCheck.CollectHardErrors(s), e => e.Contains("最小坡长"));
    }

    [Theory]
    [InlineData(0.0, 2, 8.0, "车宽必须 > 0")]
    [InlineData(6.0, 0, 8.0, "车道数至少为 1")]
    [InlineData(6.0, 2, 0.0, "限制坡度必须 > 0")]
    public void 硬错_三个基本量(double w, int lanes, double grade, string msg)
    {
        var s = new TransportConstraintSettings { TruckWidth = w, LaneCount = lanes, MaxGradePct = grade };
        Assert.Contains(msg, TransportConstraintCheck.CollectHardErrors(s));
    }

    [Fact]
    public void 软警_路面宽超过最小工作平盘宽()
    {
        var s = new TransportConstraintSettings { LaneCount = 6, TruckWidth = 9, MinWorkingBenchWidth = 20 };
        Assert.Contains(TransportConstraintCheck.CollectSoftWarnings(s), w => w.Contains("平盘塞不下"));
    }

    [Fact]
    public void 软警_设定下限明显低于车速反算时形同虚设()
    {
        var s = new TransportConstraintSettings { DesignSpeedKmh = 60, MinCurveRadiusM = 5 };
        Assert.Contains(TransportConstraintCheck.CollectSoftWarnings(s), w => w.Contains("形同虚设"));
    }

    [Fact]
    public void 软警_不拦确认_默认配置也可能有软警但硬错为空()
    {
        var s = new TransportConstraintSettings { DesignSpeedKmh = 60, MinCurveRadiusM = 5 };
        Assert.NotEmpty(TransportConstraintCheck.CollectSoftWarnings(s));
        Assert.Empty(TransportConstraintCheck.CollectHardErrors(s));   // 软警只提示、不拦
    }

    [Fact]
    public void 告警_限制坡度超过设备爬坡度()
    {
        var s = new TransportConstraintSettings { MaxGradePct = 12, TruckClimbPct = 10 };
        Assert.True(s.GradeExceedsClimb());
        Assert.Contains("请下调", TransportConstraintCheck.GradeWarning(s));
        Assert.Equal("", TransportConstraintCheck.GradeWarning(new TransportConstraintSettings()));
    }

    // ── 方案容器 ────────────────────────────────────────────
    [Fact]
    public void 方案_空容器用老单份配置迁成默认方案()
    {
        // 升级迁移：用户原来那份配置必须原样保住
        var old = new TransportConstraintSettings { MaxGradePct = 6.5 };
        var box = new TransportConstraintProfiles();
        var cur = box.EnsureUsable(old);
        Assert.Same(old, cur);
        Assert.Equal(TransportConstraintProfiles.DefaultName, box.CurrentName);
        Assert.Equal(6.5, box.Profiles[TransportConstraintProfiles.DefaultName].MaxGradePct, 6);
    }

    [Fact]
    public void 方案_当前指向不存在时回落首个()
    {
        var box = new TransportConstraintProfiles { CurrentName = "早没了" };
        box.Put("乙", new TransportConstraintSettings());
        box.Put("甲", new TransportConstraintSettings());
        box.EnsureUsable(new TransportConstraintSettings());
        Assert.Equal("乙", box.CurrentName);   // SortedNames 序数排序：乙(U+4E59) < 甲(U+7532)
    }

    [Fact]
    public void 方案_改名带着当前指针一起走()
    {
        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings());
        box.CurrentName = "甲";
        Assert.True(box.Rename("甲", "乙"));
        Assert.Equal("乙", box.CurrentName);
        Assert.False(box.Profiles.ContainsKey("甲"));
    }

    [Theory]
    [InlineData("甲", "甲")]      // 同名
    [InlineData("甲", "")]        // 新名为空
    [InlineData("没有这个", "丙")] // 老名不存在
    public void 方案_改名的三种拒绝(string oldName, string newName)
    {
        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings());
        box.Put("乙", new TransportConstraintSettings());
        Assert.False(box.Rename(oldName, newName));
    }

    [Fact]
    public void 方案_改名不许撞已有的()
    {
        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings());
        box.Put("乙", new TransportConstraintSettings());
        Assert.False(box.Rename("甲", "乙"));
        Assert.True(box.Profiles.ContainsKey("甲"));   // 没被吞掉
    }

    [Fact]
    public void 方案_只剩一个时拒绝删()
    {
        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings());
        Assert.False(box.Remove("甲"));
        Assert.Single(box.Profiles);      // 容器不能空
    }

    [Fact]
    public void 方案_删掉当前的会切到首个()
    {
        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings());
        box.Put("乙", new TransportConstraintSettings());
        box.CurrentName = "甲";
        Assert.True(box.Remove("甲"));
        Assert.Equal("乙", box.CurrentName);
    }

    [Fact]
    public void 方案_唯一名字加括号序号()
    {
        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings());
        Assert.Equal("乙", box.UniqueName("乙"));
        Assert.Equal("甲(2)", box.UniqueName("甲"));
        box.Put("甲(2)", new TransportConstraintSettings());
        Assert.Equal("甲(3)", box.UniqueName("甲"));
    }

    [Fact]
    public void 方案_合并导入时重名加后缀绝不覆盖()
    {
        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings { MaxGradePct = 1 });
        var other = new TransportConstraintProfiles();
        other.Put("甲", new TransportConstraintSettings { MaxGradePct = 2 });

        var added = box.MergeFrom(other);
        Assert.Equal(new[] { "甲(2)" }, added);
        Assert.Equal(1, box.Profiles["甲"].MaxGradePct, 6);       // 原有的没被动
        Assert.Equal(2, box.Profiles["甲(2)"].MaxGradePct, 6);
    }

    [Fact]
    public void 方案_合并空容器不出事()
        => Assert.Empty(new TransportConstraintProfiles().MergeFrom(null));

    // ── 持久化与导入导出 ────────────────────────────────────
    [Fact]
    public void 持久化_存读往返且镜像键等于当前方案()
    {
        // 镜像不变量：消费侧只读老的单份配置键，不必知道"方案"的存在
        string dir = Path.Combine(Path.GetTempPath(), "pm_tc_" + Guid.NewGuid().ToString("N"));
        using var st = new PitMine3D.Kylin.UserSettings(dir);

        var box = new TransportConstraintProfiles();
        box.Put("甲", new TransportConstraintSettings { MaxGradePct = 5 });
        box.Put("乙", new TransportConstraintSettings { MaxGradePct = 9 });
        box.CurrentName = "乙";
        Assert.True(TransportConstraintProfileStore.TrySave(st, box, out string? err), err);

        var back = TransportConstraintProfileStore.Load(st);
        Assert.NotNull(back);
        Assert.Equal(2, back!.Profiles.Count);
        Assert.Equal("乙", back.CurrentName);

        var mirror = TransportConstraintProfileStore.LoadMirrorOrDefault(st);
        Assert.Equal(9, mirror.MaxGradePct, 6);      // ★ 镜像 = 当前方案

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void 持久化_没存过时读回空并由调用方seed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_tc_" + Guid.NewGuid().ToString("N"));
        using var st = new PitMine3D.Kylin.UserSettings(dir);
        Assert.Null(TransportConstraintProfileStore.Load(st));
        Assert.Equal(new TransportConstraintSettings().MaxGradePct,
                     TransportConstraintProfileStore.LoadMirrorOrDefault(st).MaxGradePct, 6);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void 导入导出_容器往返()
    {
        string f = Path.Combine(Path.GetTempPath(), "pm_tc_" + Guid.NewGuid().ToString("N") + ".json");
        var box = new TransportConstraintProfiles();
        box.Put("甲方案", new TransportConstraintSettings { MaxGradePct = 7 });
        Assert.True(TransportConstraintProfileStore.TryExport(f, box, out string? e1), e1);

        var back = TransportConstraintProfileStore.TryImport(f, out string? e2);
        Assert.NotNull(back);
        Assert.Equal(7, back!.Profiles["甲方案"].MaxGradePct, 6);
        Assert.Null(e2);
        try { File.Delete(f); } catch { }
    }

    [Fact]
    public void 导入_裸的单份配置按文件名收成一个方案()
    {
        string f = Path.Combine(Path.GetTempPath(), "老口径_" + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(f, "{\"TruckClass\":\"220t级\",\"MaxGradePct\":6.5}");
        var box = TransportConstraintProfileStore.TryImport(f, out string? err);
        Assert.NotNull(box);
        Assert.Null(err);
        Assert.Single(box!.Profiles);
        Assert.Equal(6.5, box.Profiles[box.CurrentName].MaxGradePct, 6);
        try { File.Delete(f); } catch { }
    }

    [Fact]
    public void 导入_不相干的JSON不能反成一份全默认配置()
    {
        // System.Text.Json 忽略未知字段, 任何 JSON 都能"成功"反出一份全默认值 —— 必须先认字段
        string f = Path.Combine(Path.GetTempPath(), "pm_tc_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(f, "{\"完全不相干\":123}");
        Assert.Null(TransportConstraintProfileStore.TryImport(f, out string? err));
        Assert.Contains("没有可识别的约束配置", err);
        try { File.Delete(f); } catch { }
    }

    [Fact]
    public void 导入_坏文件只回原因不抛()
    {
        string f = Path.Combine(Path.GetTempPath(), "pm_tc_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(f, "{不是 JSON");
        Assert.Null(TransportConstraintProfileStore.TryImport(f, out string? err));
        Assert.False(string.IsNullOrEmpty(err));
        try { File.Delete(f); } catch { }
    }

    [Fact]
    public void 导入_文件不存在只回原因不抛()
        => Assert.Null(TransportConstraintProfileStore.TryImport(
            Path.Combine(Path.GetTempPath(), "根本没有这个文件.json"), out _));

    // ── 卡车预设 ────────────────────────────────────────────
    [Fact]
    public void 预设_四个吨级经验档的轴距一律留空()
    {
        // 没有可靠出处就不编：假轴距会让弯道加宽 ε=车道·L²/(2R) 算出假结果
        Assert.Equal(4, TruckPresets.Empirical.Count);
        Assert.All(TruckPresets.Empirical, p => Assert.Null(p.WheelbaseM));
        Assert.All(TruckPresets.Empirical, p => Assert.Contains("车辆轴距", p.MissingFieldNames()));
    }

    [Fact]
    public void 预设_经验档带全其余六项()
    {
        var p = TruckPresets.Empirical.Single(x => x.Name == "100t级");
        Assert.Equal(new[] { "车辆轴距" }, p.MissingFieldNames());
        Assert.Equal(6.0, p.WidthM);
        Assert.Equal(90.0, p.PayloadT);
    }

    [Fact]
    public void 预设_在册车型显示名带在册后缀()
    {
        Assert.Equal("930E · 在册", new TruckPreset("930E", TruckPresetSource.Registry).DisplayName);
        Assert.Equal("100t级", new TruckPreset("100t级", TruckPresetSource.Empirical).DisplayName);
    }

    [Fact]
    public void 预设_什么都没有的档把七项全列进未提供()
        => Assert.Equal(7, new TruckPreset("自定义", TruckPresetSource.Empirical).MissingFieldNames().Count);

    [Theory]
    [InlineData("11.0 × 7.4 × 6.4", 7.4)]      // 长 × 【宽】 × 高
    [InlineData("11.0*7.4*6.4", 7.4)]
    [InlineData("11.0 × 7.4", null)]            // 只有两个数 ⇒ 判为拿不到
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("没有数字", null)]
    public void 预设_车宽只从长宽高串里取中间那个数(string? dims, double? expect)
        => Assert.Equal(expect, TruckPresets.ParseWidthM(dims));

    [Fact]
    public void 预设_没有连接时只剩自定义加经验档()
    {
        var all = TruckPresets.AllFor(null);
        Assert.Equal(1 + TruckPresets.Empirical.Count, all.Count);
        Assert.Equal(TruckPresets.CustomName, all[0].Name);
        Assert.DoesNotContain(all, p => p.Source == TruckPresetSource.Registry);
    }

    [Fact]
    public void 预设_从设备库读在册车型只带确有的字段()
    {
        using var db = TestDb.Open();
        Exec(db.Connection, "DELETE FROM equipment_constraint");
        Exec(db.Connection, "DELETE FROM equipment_model WHERE model IN ('TT-930E','TT-730E')");
        Exec(db.Connection, "INSERT INTO equipment_model (model, category, load_t, dimensions_lwh, tire_spec) "
                          + "VALUES ('TT-930E','卡车',290,'15.6 × 8.7 × 7.4','53/80R63')");

        var list = TruckPresets.FromEquipmentLibrary(db.Connection);
        var p = list.Single(x => x.Name == "TT-930E");
        Assert.Equal(TruckPresetSource.Registry, p.Source);
        Assert.Equal(290, p.PayloadT);
        Assert.Equal(8.7, p.WidthM);          // 从长宽高串解析出来
        // 设备库里根本没有的三项一律留 null —— 绝不用经验值冒充在册车型的实测参数
        Assert.Null(p.TurnRadiusM);
        Assert.Null(p.WheelbaseM);
        Assert.Null(p.TireDiameterM);         // tire_spec 是规格串不是直径, 不做经验换算
        Assert.Null(p.MaxGradePct);           // 限制坡度是设计取值, 不等于设备爬坡能力
        Assert.Contains("最小转弯半径", p.MissingFieldNames());
    }

    [Fact]
    public void 预设_爬坡度从设备约束表按参数码读()
    {
        using var db = TestDb.Open();
        Exec(db.Connection, "DELETE FROM equipment_constraint");
        Exec(db.Connection, "DELETE FROM equipment_model WHERE model = 'TT-930E'");
        Exec(db.Connection, "INSERT INTO equipment_model (model, category, load_t) VALUES ('TT-930E','卡车',290)");

        long pid = ParamId(db.Connection, "road_max_slope_pct");
        Exec(db.Connection, $"INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value) "
                          + $"VALUES ({pid}, 'TT-930E', 'max', 9.0)");

        var p = TruckPresets.FromEquipmentLibrary(db.Connection).Single(x => x.Name == "TT-930E");
        Assert.Equal(9.0, p.ClimbPct);
        Assert.DoesNotContain("爬坡度", p.MissingFieldNames());
    }

    [Fact]
    public void 预设_约束表里没录的型号爬坡度留空而不牵连其余()
    {
        using var db = TestDb.Open();
        Exec(db.Connection, "DELETE FROM equipment_constraint");
        Exec(db.Connection, "DELETE FROM equipment_model WHERE model = 'TT-无约束'");
        Exec(db.Connection, "INSERT INTO equipment_model (model, category, load_t) VALUES ('TT-无约束','卡车',100)");

        var p = TruckPresets.FromEquipmentLibrary(db.Connection).Single(x => x.Name == "TT-无约束");
        Assert.Null(p.ClimbPct);
        Assert.Equal(100, p.PayloadT);        // 载重照旧带得出来
    }

    [Fact]
    public void 预设_不是卡车的型号不进下拉()
    {
        using var db = TestDb.Open();
        Exec(db.Connection, "DELETE FROM equipment_model WHERE model = 'EX-挖机'");
        Exec(db.Connection, "INSERT INTO equipment_model (model, category, bucket_m3) VALUES ('EX-挖机','电铲',35)");
        Assert.DoesNotContain(TruckPresets.FromEquipmentLibrary(db.Connection), p => p.Name == "EX-挖机");
    }

    [Fact]
    public void 文案_设备库有无在册车型的两种说法()
    {
        Assert.Contains("仅吨级经验档", TransportConstraintCheck.TruckSourceNote(0));
        Assert.Contains("在册 3 种", TransportConstraintCheck.TruckSourceNote(3));
    }

    [Fact]
    public void 文案_切车型缺参数要当场点名()
    {
        // 静默沿用旧值就是错算的来源
        var p = new TruckPreset("930E", TruckPresetSource.Registry, PayloadT: 290);
        string note = TransportConstraintCheck.TruckGapNote(p);
        Assert.Contains("930E · 在册", note);
        Assert.Contains("车辆轴距", note);
        Assert.Contains("未随车型更新", note);
        // 全都提供了就不啰嗦
        Assert.Equal("", TransportConstraintCheck.TruckGapNote(
            new TruckPreset("全", TruckPresetSource.Empirical, 1, 2, 3, 4, 5, 6, 7)));
        Assert.Equal("", TransportConstraintCheck.TruckGapNote(null));
    }

    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>取参数码对应的 param_id。V007 已把 road_max_slope_pct 播进去（3002）——
    /// 这里按 code 查而不是写死 3002，正是被测代码守的那条规矩。</summary>
    private static long ParamId(DbConnection c, string code)
    {
        using var q = c.CreateCommand();
        q.CommandText = $"SELECT param_id FROM parameter_definition WHERE code = '{code}'";
        var v = q.ExecuteScalar();
        Assert.False(v == null || v is DBNull, $"种子里应有参数码 {code}");
        return Convert.ToInt64(v);
    }
}
