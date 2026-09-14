using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 编制人工锚点（§三五三）。原版要修的毛病是"几处各一套数、谁也不喂引擎" ——
/// 界面改了什么都不会发生。这里的判据全都盯着同一件事：**改了要真的落到盘子上，没改的不许被顶掉**。
///
/// 三条头号判据（都不会报错）：
///   ① <b>null ≠ 0</b>：没设过就保持引擎缺省；填 0 是"人工确认过不降效/不扣交接"。
///      把缺省值也写进锚点，日后引擎调缺省时旧值会把新口径顶回来。
///   ② <b>只设全盘降效时，分环节那条路要与老口径逐位相同</b>（早退分支）。
///   ③ <b>Clone 必须深拷字典</b>：浅拷会让"改了不保存"和"重置"顺手改掉原件。
/// </summary>
public class CompileOverridesTests
{
    private static ExploderConfig Cfg() => new();

    // ── null ≠ 0 ────────────────────────────────────────────
    [Fact]
    public void 锚点_没设过就不动引擎缺省()
    {
        var cfg = Cfg();
        Assert.Equal("", CompileOverrides.ApplyTo(cfg, new CompileAnchors()));
        Assert.Equal(0.5, cfg.HandoverRampH, 6);
        Assert.Equal(0, cfg.WeatherDeratePct, 6);
        Assert.Equal(20, cfg.EffHours, 6);
        Assert.Equal(0, cfg.MinPreparedDays, 6);
        Assert.Null(cfg.Blend);                          // 没设配煤就不造 BlendStandard（造了等于打开配煤约束）
    }

    [Fact]
    public void 锚点_填零与没填是两回事()
    {
        // ★ 0 = 人工确认过今天不降效 / 本矿不扣交接；null = 没设过
        var cfg = Cfg();
        string s = CompileOverrides.ApplyTo(cfg, new CompileAnchors { WeatherDeratePct = 0, HandoverRampH = 0 });
        Assert.Equal(0, cfg.HandoverRampH, 6);
        Assert.Contains("天气不降效（人工确认）", s);
        Assert.Contains("不扣交接班（人工确认）", s);
    }

    [Fact]
    public void 锚点_备采下限填零是人工确认不校核()
    {
        var cfg = Cfg();
        Assert.Contains("不校核备采保有（人工确认）", CompileOverrides.ApplyTo(cfg, new CompileAnchors { MinPreparedDays = 0 }));
    }

    [Fact]
    public void 锚点_配煤三项只盖设过的那几项()
    {
        var cfg = Cfg();
        CompileOverrides.ApplyTo(cfg, new CompileAnchors { MaxAshPct = 11.5 });
        Assert.Equal(11.5, cfg.Blend!.MaxAshPct, 6);
        Assert.Equal(21.5, cfg.Blend.MinCalorificMJkg, 6);   // 没设 ⇒ 引擎缺省
        Assert.Equal(0.7, cfg.Blend.MaxSulfurPct, 6);
    }

    [Fact]
    public void 锚点_有效工时盖上去且来源文案说得出来()
    {
        var cfg = Cfg();
        string s = CompileOverrides.ApplyTo(cfg, new CompileAnchors { EffHoursPerDay = 18 });
        Assert.Equal(18, cfg.EffHours, 6);
        Assert.Contains("面日产能工时 18h", s);
    }

    [Fact]
    public void 锚点_有效工时读取口回落到配煤上那个兼容视图()
    {
        // 原版把它从 BlendStandard 上摘下来；老盘子仍可能在那儿设值
        var cfg = new ExploderConfig { Blend = new BlendStandard { EffHoursPerDay = 16 } };
        Assert.Equal(16, cfg.EffHours, 6);
        cfg.EffHoursPerDay = 21;
        Assert.Equal(21, cfg.EffHours, 6);                  // 本级优先
    }

    [Fact]
    public void 锚点_分环节留空时不做填充()
    {
        // ★ 填了就分不出「跟随全盘」与「人工确认与全盘同值」—— 后者在日后调全盘值时不该跟着走
        var cfg = Cfg();
        CompileOverrides.ApplyTo(cfg, new CompileAnchors { WeatherDeratePct = 20 });
        Assert.Null(cfg.LoadDeratePct);
        Assert.Null(cfg.HaulDeratePct);
        Assert.Null(cfg.DumpDeratePct);
    }

    [Fact]
    public void 锚点_空锚点返回空串调用方据此不显示这一段()
        => Assert.Equal("", CompileOverrides.ApplyTo(Cfg(), new CompileAnchors()));

    [Fact]
    public void 锚点_没有盘子时不抛()
        => Assert.Equal("", CompileOverrides.ApplyTo(null, new CompileAnchors { WeatherDeratePct = 10 }));

    // ── Clone / IsEmpty ─────────────────────────────────────
    [Fact]
    public void 克隆_字典是深拷不共用()
    {
        var a = new CompileAnchors();
        a.SinkBlend["北排土场"] = new SinkBlendAnchor { MaxAshPct = 12 };
        var b = a.Clone();
        b.SinkBlend["北排土场"].MaxAshPct = 9;
        b.SinkBlend["新加的"] = new SinkBlendAnchor { MaxAshPct = 8 };
        Assert.Equal(12, a.SinkBlend["北排土场"].MaxAshPct);
        Assert.Single(a.SinkBlend);
    }

    [Fact]
    public void 克隆_十项标量一个不落()
    {
        var a = new CompileAnchors
        {
            MaxAshPct = 1, MinCalorificMJkg = 2, MaxSulfurPct = 3, WeatherDeratePct = 4, MinPreparedDays = 5,
            EffHoursPerDay = 6, HandoverRampH = 7, LoadDeratePct = 8, HaulDeratePct = 9, DumpDeratePct = 10,
        };
        var b = a.Clone();
        Assert.Equal(new double?[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            new[] { b.MaxAshPct, b.MinCalorificMJkg, b.MaxSulfurPct, b.WeatherDeratePct, b.MinPreparedDays,
                    b.EffHoursPerDay, b.HandoverRampH, b.LoadDeratePct, b.HaulDeratePct, b.DumpDeratePct });
    }

    [Fact]
    public void 空判_任一项设过就不算空()
    {
        Assert.True(new CompileAnchors().IsEmpty);
        Assert.False(new CompileAnchors { WeatherDeratePct = 0 }.IsEmpty);   // 0 也算设过
        var withSink = new CompileAnchors();
        withSink.SinkBlend["甲"] = new SinkBlendAnchor { MaxAshPct = 12 };
        Assert.False(withSink.IsEmpty);
    }

    // ── 量程校验 ────────────────────────────────────────────
    [Fact]
    public void 校验_留空是不设不是零()
    {
        Assert.Null(CompileOverrides.ParseAnchor("  ", 0, 95, "天气降效", out var v));
        Assert.Null(v);
    }

    [Fact]
    public void 校验_认不出与越界分别报()
    {
        Assert.Contains("认不出", CompileOverrides.ParseAnchor("下大雨", 0, 95, "天气降效", out _)!);
        Assert.Contains("0~95", CompileOverrides.ParseAnchor("120", 0, 95, "天气降效", out _)!);
        Assert.Null(CompileOverrides.ParseAnchor("95", 0, 95, "天气降效", out var ok));
        Assert.Equal(95, ok);
    }

    // ── 逐受矿点 ────────────────────────────────────────────
    private static SinkRegistry Sinks(bool ledgerHasLimits = false)
    {
        var r = new SinkRegistry();
        r.Put(new SinkNode
        {
            Id = "D1", Name = "北排土场", Kind = SinkKind.ExternalDump, DesignCapacityM3 = 1e7,
            MaxAshPct = ledgerHasLimits ? 10 : null,
        });
        r.Put(new SinkNode { Id = "C1", Name = "破碎站一", Kind = SinkKind.Crusher, DesignCapacityM3 = 1e7 });
        return r;
    }

    [Fact]
    public void 逐去向_按名字对上并盖三项()
    {
        var a = new CompileAnchors();
        a.SinkBlend["破碎站一"] = new SinkBlendAnchor { MaxAshPct = 11, MinCalorificMJkg = 22, MaxSulfurPct = 0.5 };
        var sinks = Sinks();
        string s = CompileOverrides.ApplySinkBlend(sinks, a);
        var node = sinks.Find("C1")!;
        Assert.Equal(11, node.MaxAshPct);
        Assert.Equal(22, node.MinCalorificMJkg);
        Assert.Equal(0.5, node.MaxSulfurPct);
        Assert.Contains("1 个受矿点已套用", s);
    }

    [Fact]
    public void 逐去向_按Id也对得上()
    {
        var a = new CompileAnchors();
        a.SinkBlend["C1"] = new SinkBlendAnchor { MaxAshPct = 11 };
        var sinks = Sinks();
        CompileOverrides.ApplySinkBlend(sinks, a);
        Assert.Equal(11, sinks.Find("C1")!.MaxAshPct);
    }

    [Fact]
    public void 逐去向_台账自带标准的点不被锚点覆盖()
    {
        // 锚点是给"台账还没有这几列"用的补位，台账真有了就以台账为准
        var a = new CompileAnchors();
        a.SinkBlend["北排土场"] = new SinkBlendAnchor { MaxAshPct = 11 };
        var sinks = Sinks(ledgerHasLimits: true);
        CompileOverrides.ApplySinkBlend(sinks, a);
        Assert.Equal(10, sinks.Find("D1")!.MaxAshPct);
    }

    [Fact]
    public void 逐去向_对不上的键要点名说这条不生效()
    {
        // ★ 台账改过名/换过 id 后锚点静默失效，而配煤照样按全矿级判并报「达标」—— 最像正常的一种坏
        var a = new CompileAnchors();
        a.SinkBlend["南排土场"] = new SinkBlendAnchor { MaxAshPct = 11 };
        string s = CompileOverrides.ApplySinkBlend(Sinks(), a);
        Assert.Contains("找不到", s);
        Assert.Contains("不生效", s);
    }

    [Fact]
    public void 逐去向_没有锚点或没有登记簿时返回空串()
    {
        Assert.Equal("", CompileOverrides.ApplySinkBlend(Sinks(), new CompileAnchors()));
        Assert.Equal("", CompileOverrides.ApplySinkBlend(null, new CompileAnchors()));
    }

    [Fact]
    public void 逐去向_三项都空的那条当没设()
    {
        var a = new CompileAnchors();
        a.SinkBlend["破碎站一"] = new SinkBlendAnchor();
        Assert.Equal("", CompileOverrides.ApplySinkBlend(Sinks(), a));
    }
}
