// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/RoadConditionSymbologyTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 「路况显示」逐段纵坡分档（R-L1 / R-L2 / R-L5）的判据。
///
/// 判据设计遵守两条既有纪律：
///   · <b>关掉规则闸必须真的红</b> —— L6 用 A/B 对照组证明「平滑」这条规则确实在起作用，
///     不是碰巧全绿（关掉平滑必须碎成一串档，开着必须收敛）。
///   · <b>同色不同义要有闸挡着</b> —— L9 钉死 9 种语义色两两不撞，
///     旧版向标黄与 StatusMaintenance RGB 完全相同那种事不许再发生。
/// </summary>
public class RoadConditionSymbologyTests
{
    /// <summary>造一条水平点距 dx、恒定纵坡 gradePct 的直线中线（可叠交替 Z 噪声模拟抽线抖动）。</summary>
    private static List<Point3d> Ramp(int ptCount, double dx, double gradePct, double noiseZ = 0)
    {
        var pts = new List<Point3d>(ptCount);
        for (int i = 0; i < ptCount; i++)
        {
            double x = i * dx;
            double z = x * gradePct / 100.0 + (i % 2 == 0 ? noiseZ : -noiseZ);
            pts.Add(new Point3d(x, 0, z));
        }
        return pts;
    }

    // ── L1~L3 三档三向的基本口径 ────────────────────────────────────────────

    [Fact]
    public void L1_水平中线_归平段且不分上下坡()
    {
        var runs = RoadConditionSymbology.Classify(Ramp(20, 10, 0.0));

        Assert.Single(runs);
        Assert.Equal(GradeBand.Level, runs[0].Band);
        Assert.Equal(GradeSense.Level, runs[0].Sense);
        Assert.Equal(0.0, runs[0].AvgGradePct, 6);
    }

    [Theory]
    [InlineData(1.5, GradeBand.Level, GradeSense.Level)]    // ≤3
    [InlineData(4.5, GradeBand.Mild, GradeSense.Up)]        // 3~6
    [InlineData(-4.5, GradeBand.Mild, GradeSense.Down)]
    [InlineData(8.0, GradeBand.Steep, GradeSense.Up)]       // 6~10
    [InlineData(-8.0, GradeBand.Steep, GradeSense.Down)]
    [InlineData(14.0, GradeBand.Over, GradeSense.Up)]       // >10
    [InlineData(-14.0, GradeBand.Over, GradeSense.Down)]
    public void L2_恒定纵坡_档位与坡向按_3_6_10_分(double gradePct, GradeBand band, GradeSense sense)
    {
        var runs = RoadConditionSymbology.Classify(Ramp(30, 10, gradePct));

        Assert.Single(runs);
        Assert.Equal(band, runs[0].Band);
        Assert.Equal(sense, runs[0].Sense);
        Assert.Equal(gradePct, runs[0].AvgGradePct, 6);
    }

    [Theory]
    [InlineData(3.0, GradeBand.Level)]     // 边界值归下档（闭区间在下）
    [InlineData(6.0, GradeBand.Mild)]
    [InlineData(10.0, GradeBand.Steep)]
    public void L3_档位边界值归下档(double gradePct, GradeBand band)
    {
        Assert.Equal(band, RoadConditionSymbology.ClassOf(gradePct).Band);
        Assert.Equal(band, RoadConditionSymbology.ClassOf(-gradePct).Band);
    }

    // ── L4~L5 切段的结构完整性 ─────────────────────────────────────────────

    /// <summary>
    /// 这条同时是 <b>R-L6 的闸门</b>：没有「过渡带不成路段」那步时，平滑窗口会在两个交界处
    /// 各造出一截 ~7m 的「缓坡↑」，实测切成 <b>5</b> 段而不是 3 段。把 R-L6 拿掉这条必红。
    /// </summary>
    [Fact]
    public void L4_平段接陡坡接平段_切三段且相邻段共享顶点()
    {
        // 0~200m 平 → 200~400m 爬 8% → 400~600m 平。点距 10m。
        var pts = new List<Point3d>();
        double z = 0;
        for (int i = 0; i <= 20; i++) pts.Add(new Point3d(i * 10, 0, z));
        for (int i = 1; i <= 20; i++) { z += 0.8; pts.Add(new Point3d(200 + i * 10, 0, z)); }
        for (int i = 1; i <= 20; i++) pts.Add(new Point3d(400 + i * 10, 0, z));

        var runs = RoadConditionSymbology.Classify(pts);

        Assert.Equal(3, runs.Count);
        Assert.Equal((GradeBand.Level, GradeSense.Level), (runs[0].Band, runs[0].Sense));
        Assert.Equal((GradeBand.Steep, GradeSense.Up), (runs[1].Band, runs[1].Sense));
        Assert.Equal((GradeBand.Level, GradeSense.Level), (runs[2].Band, runs[2].Sense));

        // 相邻 run 共享交界顶点 —— 逐条画多段线时交界处不留缝。
        for (int i = 1; i < runs.Count; i++)
            Assert.Equal(runs[i - 1].EndIndex, runs[i].StartIndex);
    }

    [Fact]
    public void L5_所有段无缝覆盖整条中线_里程守恒()
    {
        var pts = Ramp(60, 8, 0.0);
        for (int i = 20; i < 40; i++) pts[i] = new Point3d(pts[i].X, 0, pts[i].Z + (i - 19) * 0.9);   // 中段掺个坡
        for (int i = 40; i < 60; i++) pts[i] = new Point3d(pts[i].X, 0, pts[39].Z);

        var runs = RoadConditionSymbology.Classify(pts);

        Assert.NotEmpty(runs);
        Assert.Equal(0, runs[0].StartIndex);
        Assert.Equal(pts.Count - 1, runs[^1].EndIndex);
        for (int i = 1; i < runs.Count; i++)
            Assert.Equal(runs[i - 1].EndIndex, runs[i].StartIndex);

        double total = 0;
        for (int i = 1; i < pts.Count; i++) total += pts[i].DistanceTo(pts[i - 1]);
        Assert.Equal(total, runs.Sum(r => r.LengthM), 6);   // 分档不许吞里程
    }

    // ── L6 R-L5 的 A/B 对照组：关掉平滑必须真的碎 ──────────────────────────

    [Fact]
    public void L6_Z噪声下_关掉平滑碎成一串档_开着收敛()
    {
        // 恒定 5% 缓坡，水平点距 5m，顶点 Z 交替 ±0.1m（抽线抖动量级）。
        // 逐段原值：(0.25±0.2)/5 → 9% 与 1% 交替，即「陡坡↑ / 平段」来回跳。
        var pts = Ramp(40, 5, 5.0, noiseZ: 0.1);

        var raw = RoadConditionSymbology.Classify(pts, windowM: 0, hysteresisPct: 0);      // 闸门关
        var smooth = RoadConditionSymbology.Classify(pts);                                  // 闸门开

        Assert.True(raw.Count >= 10, $"关掉平滑应碎成一串档，实得 {raw.Count} 段——闸门形同虚设");
        Assert.True(smooth.Count <= 3, $"开着平滑应收敛，实得 {smooth.Count} 段");
        Assert.All(smooth, r => Assert.Equal(GradeBand.Mild, r.Band));                      // 真值 5% 落缓坡档
        Assert.All(smooth, r => Assert.Equal(GradeSense.Up, r.Sense));
    }

    [Fact]
    public void L7_滞回_边界附近小幅穿越不切档_大幅穿越要切()
    {
        // 在 6%（缓/陡边界）上下小幅摆动 ±0.2%（< 0.3 滞回带）——不许切档。
        var small = BandCrossingLine(5.85, 6.15);
        Assert.Single(RoadConditionSymbology.Classify(small, windowM: 0));

        // 摆动 ±1.5%（远超滞回带）——必须切档，否则滞回就成了盖住真实变化的橡皮擦。
        var big = BandCrossingLine(4.5, 7.5);
        Assert.True(RoadConditionSymbology.Classify(big, windowM: 0).Count > 1);
    }

    /// <summary>前半段 gradeA、后半段 gradeB 的两段折线（每段 200m，点距 10m）。</summary>
    private static List<Point3d> BandCrossingLine(double gradeA, double gradeB)
    {
        var pts = new List<Point3d> { new(0, 0, 0) };
        double z = 0;
        for (int i = 1; i <= 20; i++) { z += 10 * gradeA / 100.0; pts.Add(new Point3d(i * 10, 0, z)); }
        for (int i = 1; i <= 20; i++) { z += 10 * gradeB / 100.0; pts.Add(new Point3d(200 + i * 10, 0, z)); }
        return pts;
    }

    /// <summary>
    /// R-L6 的<b>反向闸</b>：它只该吃掉「档位夹在前后之间」的窗口伪影，
    /// 不许把两侧都比它缓的<b>真实短陡段</b>一起吃掉——那种段恰恰是路况显示最该报的东西。
    /// </summary>
    [Fact]
    public void L12_两侧都是平段的短超限坡_不许被过渡带合并吃掉()
    {
        // 平 300m → 12% 超限 40m → 平 300m，点距 5m。
        var pts = new List<Point3d> { new(0, 0, 0) };
        double x = 0, z = 0;
        for (int i = 0; i < 60; i++) { x += 5; pts.Add(new Point3d(x, 0, z)); }
        for (int i = 0; i < 8; i++) { x += 5; z += 5 * 0.12; pts.Add(new Point3d(x, 0, z)); }
        for (int i = 0; i < 60; i++) { x += 5; pts.Add(new Point3d(x, 0, z)); }

        var runs = RoadConditionSymbology.Classify(pts);

        Assert.Contains(runs, r => r.Band == GradeBand.Over && r.Sense == GradeSense.Up);
        Assert.Equal(3, runs.Count);   // 两头平 + 中间超限，过渡带并进平段，超限段活着
    }

    // ── L8~L9 分类映射与配色的自检闸 ───────────────────────────────────────

    [Fact]
    public void L8_ClassIndex与ClassAt互逆_七类稠密不重号()
    {
        var seen = new HashSet<int>();
        foreach (var band in Enum.GetValues<GradeBand>())
            foreach (var sense in Enum.GetValues<GradeSense>())
            {
                // 只有 (Level,Level) 与 (非Level, Up/Down) 是合法组合，共 7 种。
                bool legal = (band == GradeBand.Level) == (sense == GradeSense.Level);
                if (!legal) continue;
                int idx = RoadConditionSymbology.ClassIndex(band, sense);
                Assert.True(seen.Add(idx), $"({band},{sense}) 的下标 {idx} 与别人撞号");
                Assert.Equal((band, sense), RoadConditionSymbology.ClassAt(idx));
            }
        Assert.Equal(RoadConditionSymbology.ClassCount, seen.Count);
    }

    [Fact]
    public void L9_九种语义色两两不撞_黄已让给检修()
    {
        var palette = new Dictionary<string, Rgb>();
        for (int i = 0; i < RoadConditionSymbology.ClassCount; i++)
        {
            var (band, sense) = RoadConditionSymbology.ClassAt(i);
            palette[$"{band}/{sense}"] = RoadConditionSymbology.ColorOf(band, sense);
        }
        palette["检修"] = RoadSymbology.StatusMaintenance;
        palette["封闭"] = RoadSymbology.StatusClosed;
        palette["向标"] = RoadSymbology.ConditionChevron;

        // 同一张图上并存的 10 种语义（7 档 + 检修 + 封闭 + 向标）不许有两个撞 RGB。
        var byColor = palette.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).ToList();
        Assert.True(byColor.Count == 0,
            "同色不同义：" + string.Join(" / ", byColor.Select(g => string.Join("==", g.Select(kv => kv.Key)))));

        // R-L4 的专项闸：向标绝不能再等于检修黄。
        Assert.NotEqual(RoadSymbology.StatusMaintenance, RoadSymbology.ConditionChevron);
    }

    // ── L10 退化输入 ───────────────────────────────────────────────────────

    [Fact]
    public void L10_退化中线_返回空表不抛()
    {
        Assert.Empty(RoadConditionSymbology.Classify(null!));
        Assert.Empty(RoadConditionSymbology.Classify(new List<Point3d>()));
        Assert.Empty(RoadConditionSymbology.Classify(new List<Point3d> { new(0, 0, 0) }));
        // 全部重合：水平投影退化，谈不上纵坡。
        Assert.Empty(RoadConditionSymbology.Classify(
            new List<Point3d> { new(5, 5, 0), new(5, 5, 3), new(5, 5, 9) }));
    }

    [Fact]
    public void L11_竖直退化段不产生天文数字坡度()
    {
        // 中间掺一段近乎竖直的退化段（水平 1mm、垂直 12m）。
        var pts = new List<Point3d>
        {
            new(0, 0, 0), new(50, 0, 0), new(50.001, 0, 12), new(100, 0, 12), new(150, 0, 12),
        };
        var runs = RoadConditionSymbology.Classify(pts);

        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.True(
            Math.Abs(r.AvgGradePct) <= RoadConditionSymbology.MaxAbsGradePct + 1e-9,
            $"坡度 {r.AvgGradePct}% 越过钳位上限"));
    }
}
