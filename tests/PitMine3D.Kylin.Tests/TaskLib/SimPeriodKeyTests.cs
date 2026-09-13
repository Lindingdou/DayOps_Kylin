// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimPeriodKeyTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 期次键解析的判据。
///
/// <para><b>这组判据是被一次真实故障逼出来的</b>：界面上「建单元体」建出 0 个体，
/// 状态栏原话是「没有给期次序列（PeriodKeys），定不了每个单元落在第几帧 → 不建体」。
/// 根因是解析器假设 <c>yyyy-MM</c> 在字符串开头，而真实帧标签是「<c>推算 2026-08 月</c>」。
/// 所以 P1 直接钉住那条真标签 —— <b>判据里的字符串必须是现场原样，不许是我顺手编的干净样本</b>。</para>
/// </summary>
public class SimPeriodKeyTests
{
    // ── P1：现场原样的标签 ─────────────────────────────────────────────────
    [Theory]
    [InlineData("推算 2026-08 月", "2026-08")]   // ★ 出事的那一条，一字不改
    [InlineData("2026-08 月", "2026-08")]
    [InlineData("2026-08", "2026-08")]
    [InlineData("推算 2026-8 月", "2026-08")]    // 个位月要补零，否则台账键对不上
    [InlineData("2026年8月", "2026-08")]
    [InlineData("2026/8", "2026-08")]
    [InlineData("计划 2027-12 月（外推）", "2027-12")]
    public void P1_真实标签能认出期次(string label, string want)
        => Assert.Equal(want, SimPeriodKey.Parse(label));

    // ── P2：认不出来必须返回空，不许猜 ───────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("早班")]          // 班粒度，本来就没有月
    [InlineData("夜班")]
    [InlineData("第 3 期")]
    [InlineData("2026-13")]       // 13 月不是月份
    [InlineData("2026-00")]
    [InlineData("1899-05")]       // 世纪前缀不认
    public void P2_认不出的一律返回空串(string? text)
        => Assert.Equal("", SimPeriodKey.Parse(text));

    // ── P3：Period 是权威，Label 只是退路 ────────────────────────────────
    [Fact]
    public void P3_优先读结构化Period而不是显示Label()
    {
        // 两者不一致时必须听 Period —— Label 是给人看的，可能被随便改
        var f = new SimFrame { Period = "2026-08", Label = "推算 2030-01 月" };
        Assert.Equal("2026-08", SimPeriodKey.Of(f));
    }

    [Fact]
    public void P3b_Period认不出时才退到Label()
    {
        var f = new SimFrame { Period = "", Label = "推算 2026-08 月" };
        Assert.Equal("2026-08", SimPeriodKey.Of(f));
    }

    // ── P4：时间轴逐帧对位 —— 认不出的必须留空占位，绝不能把后面的往前挪 ──
    [Fact]
    public void P4_认不出的帧留空占位不许错位()
    {
        var tl = new SimTimeline();
        tl.Frames.Add(new SimFrame { Label = "第 1 期" });            // 认不出
        tl.Frames.Add(new SimFrame { Period = "2026-09" });
        tl.Frames.Add(new SimFrame { Label = "推算 2026-10 月" });

        var keys = SimPeriodKey.Of(tl);

        Assert.Equal(3, keys.Length);          // 长度 = 帧数，不是"认出来的个数"
        Assert.Equal("", keys[0]);             // 空着，不是被 2026-09 填掉
        Assert.Equal("2026-09", keys[1]);      // 还在第 1 位（错位就会挪到第 0 位）
        Assert.Equal("2026-10", keys[2]);
    }

    // ── P5：舞台的前置条件 —— 12 帧月时间轴必须给得出 12 个非空键 ────────
    [Fact]
    public void P5_月时间轴每帧都要有键否则舞台建不出体()
    {
        var tl = new SimTimeline();
        for (int i = 0; i < 12; i++)
            tl.Frames.Add(new SimFrame { Period = $"2026-{i + 1:00}", Label = $"推算 2026-{i + 1:00} 月" });

        var keys = SimPeriodKey.Of(tl);

        // 这一条空过的唯一方式是解析器全对 —— 出故障那版在这里会拿到 0
        Assert.Equal(12, keys.Count(k => k.Length > 0));
        Assert.Equal(12, keys.Distinct().Count());   // 键还必须互不相同，否则多帧塌成一帧
    }

    // ── P6：空时间轴不炸 ─────────────────────────────────────────────────
    [Fact]
    public void P6_空时间轴返回空数组()
    {
        Assert.Empty(SimPeriodKey.Of(new SimTimeline()));
        Assert.Empty(SimPeriodKey.Of((SimTimeline?)null));
        Assert.Equal("", SimPeriodKey.Of((SimFrame?)null));
    }
}
