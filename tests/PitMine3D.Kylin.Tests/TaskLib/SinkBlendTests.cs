// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SinkBlendTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 逐受矿点配煤的判据。
///
/// <para><b>口径</b>：配煤是按受矿点成立的。两个破碎站各有各的合同指标，把两边的煤混在一起
/// 算一个"全矿综合灰分"没有物理含义 —— 那堆煤永远不会真的混到一起。
/// 掺配动作更要命：把量从"送 A 站的高灰面"移到"送 B 站的低灰面"，A 站的灰分一点没改善，
/// 却凭空改了两个站的到货量。<b>S2 钉的就是"不许跨点移量"</b>，它是这次改动的全部意义所在。</para>
///
/// <para><b>S1 是兼容闸</b>：一个去向都没填时必须与老口径逐位相同 —— 现场的去向登记簿
/// 现在还是空表，绝大多数盘子走的就是这一条路。</para>
/// </summary>
public class SinkBlendTests
{
    private static ExploderConfig Plate()
        => new()
        {
            DateLabel = "2026-08-20 周四", IdPrefix = "T", EnforceMassBalance = false,
            Blend = new BlendStandard { MaxAshPct = 12.0 },
            Shifts =
            {
                new ShiftWindow("早班", 0, 8), new ShiftWindow("中班", 8, 16), new ShiftWindow("夜班", 16, 24),
            },
        };

    private static FaceInput Coal(string zone, double ash, double target, string sinkId = "")
        => new()
        {
            Zone = zone, Process = ProcessType.Load, Material = "煤", DayTargetM3 = target,
            DestinationId = sinkId, DestinationKind = SinkKind.Crusher,
            Quality = new CoalQuality { AshPct = ash, CalorificMJkg = 22, SulfurPct = 0.5 },
            Group = new EquipmentGroup { MainEquipment = "WK-" + zone, GroupCapacityM3PerH = 500 },
        };

    private static void PutSink(ExploderConfig cfg, string id, string name, double? maxAsh = null)
        => cfg.Sinks.Put(new SinkNode { Id = id, Name = name, Kind = SinkKind.Crusher, MaxAshPct = maxAsh });

    // ── S1：一个去向都没填 ⇒ 只分出一组，与老口径逐位相同 ──────────────────────
    [Fact]
    public void S1_无去向时与老口径逐位相同()
    {
        var cfg = Plate();
        cfg.Faces.Add(Coal("高灰", 16, 1000));
        cfg.Faces.Add(Coal("低灰", 8, 1000));

        var r = TaskExploder.Explode(cfg);

        // 综合 12.0 = 上限，掺配后达标；只该报**一条**配煤结论（分组分出两组就会报两条）
        int blendMsgs = r.Violations.Count(v => v.Code is "配煤达标" or "配煤调整" or "配煤不达标");
        Assert.Equal(1, blendMsgs);

        // 单组时文案里不带【受矿点】前缀（带了说明把一组也当多组渲染了）
        var only = r.Violations.First(v => v.Code is "配煤达标" or "配煤调整" or "配煤不达标");
        Assert.DoesNotContain("【", only.Message);
    }

    // ── S2：不许跨受矿点移量 —— 本组的核心 ────────────────────────────────────
    //
    //  A 站：只有一个 16% 的高灰面（组内无低灰面可掺）⇒ 应报「不达标」。
    //  B 站：只有一个 8% 的低灰面 ⇒ 达标，且**量一克不许动**。
    //  若按老口径全矿混一堆：综合 (16+8)/2 = 12.0 ≤ 12 直接判达标，两个站的问题全被抹平。
    [Fact]
    public void S2_不许把量从一个站移到另一个站()
    {
        var cfg = Plate();
        PutSink(cfg, "CR-A", "1号破碎站");
        PutSink(cfg, "CR-B", "2号破碎站");
        var hi = Coal("北帮高灰", 16, 1000, "CR-A");
        var lo = Coal("南帮低灰", 8, 1000, "CR-B");
        cfg.Faces.Add(hi);
        cfg.Faces.Add(lo);

        var r = TaskExploder.Explode(cfg);

        Assert.Equal(1000, hi.DayTargetM3, 0);   // 两个站各自的到货量都没被动过
        Assert.Equal(1000, lo.DayTargetM3, 0);

        Assert.Contains(r.Violations, v => v.Code == "配煤不达标" && v.Message.Contains("1号破碎站"));
        Assert.Contains(r.Violations, v => v.Code == "配煤达标" && v.Message.Contains("2号破碎站"));

        // 反例组：两个面都送 A 站 ⇒ 组内可掺，量必须真的动起来。
        // 灰分取 18/8（综合 13.0 > 上限 12.0）——上面主算例的 16/8 综合正好压在 12.0 上，
        // 那是"达标所以不动"，拿它当反例证明不了"分组没把该在一组的面拆开"。
        var same = Plate();
        PutSink(same, "CR-A", "1号破碎站");
        var hi2 = Coal("北帮高灰", 18, 1000, "CR-A");
        var lo2 = Coal("南帮低灰", 8, 1000, "CR-A");
        same.Faces.Add(hi2);
        same.Faces.Add(lo2);
        TaskExploder.Explode(same);
        Assert.True(hi2.DayTargetM3 < 1000,
            "同一个站内部本该掺得动 —— 一点没动说明分组把该在一组的面拆开了");
    }

    // ── S3：各点判各点的标准，缺项落回全矿级 ──────────────────────────────────
    [Fact]
    public void S3_各点判各点的标准()
    {
        // A 站严（10%）、B 站没给（落回全矿级 12%）。两组各有一对 13%/9% 的面：
        //   综合 11.0 → A 站超标要掺、B 站达标不动。
        var cfg = Plate();
        PutSink(cfg, "CR-A", "严标站", maxAsh: 10.0);
        PutSink(cfg, "CR-B", "常规站");

        var a1 = Coal("A高", 13, 1000, "CR-A");
        var a2 = Coal("A低", 9, 1000, "CR-A");
        var b1 = Coal("B高", 13, 1000, "CR-B");
        var b2 = Coal("B低", 9, 1000, "CR-B");
        foreach (var f in new[] { a1, a2, b1, b2 }) cfg.Faces.Add(f);

        var r = TaskExploder.Explode(cfg);

        Assert.True(a1.DayTargetM3 < 1000, "严标站超标（11.0 > 10.0）本该掺配");
        Assert.Equal(1000, b1.DayTargetM3, 0);   // 常规站 11.0 ≤ 12.0，不该动
        Assert.Equal(1000, b2.DayTargetM3, 0);

        Assert.Contains(r.Violations, v => v.Message.Contains("严标站") && v.Message.Contains("本点标准"));
        Assert.Contains(r.Violations, v => v.Message.Contains("常规站") && v.Message.Contains("全矿级缺省"));
    }

    // ── S4：组内孤面不许静默 —— "没动"有两种原因，必须分得出来 ─────────────────
    [Fact]
    public void S4_孤面要如实报无从掺配()
    {
        var cfg = Plate();
        PutSink(cfg, "CR-A", "1号破碎站");
        PutSink(cfg, "CR-B", "2号破碎站");
        cfg.Faces.Add(Coal("A面", 16, 1000, "CR-A"));
        cfg.Faces.Add(Coal("B面", 9, 1000, "CR-B"));

        var r = TaskExploder.Explode(cfg);
        Assert.Contains(r.Violations, v => v.Message.Contains("无从掺配") && v.Message.Contains("1号破碎站"));
    }

    // ── S5：锚点套用要在去向载入之后，且对不上的键必须报出来 ──────────────────
    //
    //  静默失效是这条链上最像正常的一种坏：台账改过名/换过 id，锚点一条都没套上，
    //  配煤照样按全矿级判并报「达标」，从头到尾不报错。
    [Fact]
    public void S5_逐点锚点套用与失配报账()
    {
        try
        {
            var cfg = Plate();
            PutSink(cfg, "CR-A", "1号破碎站");

            CompileOverrides.SetForTest(new CompileAnchors
            {
                SinkBlend =
                {
                    ["CR-A"] = new SinkBlendAnchor { MaxAshPct = 9.5 },
                    ["CR-XXX"] = new SinkBlendAnchor { MaxAshPct = 8.0 },   // 登记簿里没有这个键
                },
            });

            string label = CompileOverrides.ApplySinkBlend(cfg);

            Assert.Equal(9.5, cfg.Sinks.Find("CR-A")!.MaxAshPct);
            Assert.Contains("1 个受矿点已套用", label);
            Assert.Contains("找不到", label);          // 失配的那条必须说出来

            // 台账自带标准的点不被锚点覆盖（台账才是真源）
            var cfg2 = Plate();
            PutSink(cfg2, "CR-A", "1号破碎站", maxAsh: 11.0);
            CompileOverrides.ApplySinkBlend(cfg2);
            Assert.Equal(11.0, cfg2.Sinks.Find("CR-A")!.MaxAshPct);
        }
        finally { CompileOverrides.SetForTest(null); }
    }

    // ── S6：锚点进 IsEmpty / Clone，且 Clone 是深拷 ───────────────────────────
    [Fact]
    public void S6_逐点锚点的空判与深拷()
    {
        var a = new CompileAnchors();
        Assert.True(a.IsEmpty);
        a.SinkBlend["CR-A"] = new SinkBlendAnchor { MaxAshPct = 9.5 };
        Assert.False(a.IsEmpty);

        var cp = a.Clone();
        cp.SinkBlend["CR-A"].MaxAshPct = 1.0;
        cp.SinkBlend["CR-B"] = new SinkBlendAnchor { MaxAshPct = 2.0 };

        // 浅拷的话原件会跟着一起变 —— 「改了不保存」和「重置」都会顺手毁掉原件
        Assert.Equal(9.5, a.SinkBlend["CR-A"].MaxAshPct);
        Assert.False(a.SinkBlend.ContainsKey("CR-B"));
    }

    // ── S7：去向登记簿是空表时，不许假装"逐去向"已经在管着了 ──────────────────
    [Fact]
    public void S7_空登记簿时全部落回全矿级()
    {
        var cfg = Plate();                        // 一个 sink 都没 Put
        cfg.Faces.Add(Coal("高灰", 20, 1000));
        cfg.Faces.Add(Coal("低灰", 6, 1000));

        var r = TaskExploder.Explode(cfg);

        // 综合 13.0 > 12.0 ⇒ 按全矿级掺配，且只有一组
        Assert.Single(r.Violations.Where(v => v.Code is "配煤达标" or "配煤调整" or "配煤不达标"));
        Assert.Contains(r.Violations, v => v.Code == "配煤调整");
    }
}
