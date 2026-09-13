// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimProcessRegionTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Simulation;
using PitMine3D.Kylin.TaskLib.Zoning;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「工序作业区 → 推演轮廓」的判据（S 组）。
///
/// <para><b>这一层修的是一个看不出来的错</b>：推演此前按<b>面名</b>配轮廓，
/// 于是同一个作业面的五道工序共用同一条轮廓 —— 穿孔那一格演在电铲脚下、
/// 爆破警戒演在采装带上、排土卸载演在整幅排土幅上。画面每一帧都规整，
/// 量与班次也都是真的，<b>只有位置是错的</b>，而位置恰恰是演示要说明的那件事。</para>
///
/// <para>所以这一组判的全是**位置与身份**，不判面积：几何换了而类别跟着换掉，
/// 排土轮廓会朝反方向推进，推演一声不响。</para>
/// </summary>
public class SimProcessRegionTests
{
    private const string M = "2026-08";

    // ══════════════════════════════════════════════════════════════
    //  夹具
    // ══════════════════════════════════════════════════════════════

    private static ProcessZone Zone(string name, string process, string group,
                                    double x0, double y0, double w, double h,
                                    double z = 1200, long active = 1)
    {
        var ring = new List<ZonePoint>
        {
            new(x0, y0, z), new(x0 + w, y0, z), new(x0 + w, y0 + h, z), new(x0, y0 + h, z),
        };
        return new ProcessZone
        {
            Period = M, Process = process, Name = name, GroupKey = group,
            PointsJson = ZoneStore.SerializeRing(ring),
            ZSource = "算例", CellM = 1, AreaM2 = w * h, Active = active,
        };
    }

    private static SimRegionSet FaceLevel(params (string Name, string Category)[] regs)
    {
        var set = new SimRegionSet();
        foreach (var (n, c) in regs)
            set.Regions.Add(new SimRegion
            {
                Name = n, Category = c, SinkId = "SINK-" + n, SlopeAngleDeg = 37,
                Ring = new List<SimPoint> { new(0, 0), new(500, 0), new(500, 500), new(0, 500) },
                Z = 1100,
            });
        return set;
    }

    // ══════════════════════════════════════════════════════════════
    //  S1 只换几何，不换身份
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// S1 类别 / 汇绑定 / 边坡角<b>沿用面级区域</b>，不用工序区自己的。
    /// <para>类别决定推进极性。工序区自己不带内外排 —— 拿它去判，
    /// <b>内排土场会被当成外排，轮廓朝反方向动，而推演不报错</b>。</para>
    /// </summary>
    [Fact]
    public void S1_类别与汇绑定沿用面级区域()
    {
        // ★ 名字**故意不带「内排」**：名字兜底会把它判成 external_dump，
        //   而面级区域里它是 internal_dump —— 两条路结果必须不同，这条断言才证得了「沿用」。
        //   （第一版给的算例叫「内排场1」，兜底恰好也算出 internal_dump，
        //     于是把 Category 改成不沿用面级，这条判据照样绿。证伪跑出来才发现。）
        var face = FaceLevel(("北排土场", "internal_dump"));
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("北排土场", ProcessZone.ProcDumpTip, "北排土场", 0, 0, 300, 25) }, face);

        var r = set.Find(ProcessType.Dump, "北排土场");
        Assert.NotNull(r);
        Assert.Equal("internal_dump", r!.Category);          // 名字兜底会给 external_dump
        Assert.Equal("SINK-北排土场", r.SinkId);
        Assert.Equal(37, r.SlopeAngleDeg);
    }

    /// <summary>
    /// S1b 几何<b>确实换成了工序区那一块</b>（不是把面级那块原样传回来）。
    /// <para>只判类别沿用是不够的：实现里"取不到工序区就退面级"写反成"永远退面级"，
    /// 上面那条照样绿，而这一层等于没做。</para>
    /// </summary>
    [Fact]
    public void S1b_几何换成了工序区那一块()
    {
        var face = FaceLevel(("面A", "pit"));                  // 面级是 0..500 见方
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "面A", 100, 100, 60, 40) }, face);

        var r = set.Find(ProcessType.Load, "面A")!;
        Assert.InRange(r.AreaM2, 60 * 40 * 0.99, 60 * 40 * 1.01);
        Assert.InRange(r.Centroid.X, 129, 131);
        Assert.InRange(r.Centroid.Y, 119, 121);
        Assert.InRange(r.Z, 1199, 1201);                       // Z 取自工序区的环，不是面级的 1100
    }

    /// <summary>S1c 面级配不上时类别按 Z8 兜底（名字含内排 → 内排），且不炸。</summary>
    [Fact]
    public void S1c_面级配不上时类别按名字兜底()
    {
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("内排场9", ProcessZone.ProcDumpTip, "内排场9", 0, 0, 100, 20) }, null);
        Assert.Equal("internal_dump", set.Find(ProcessType.Dump, "内排场9")!.Category);

        var set2 = SimProcessRegions.LoadFrom(M,
            new[] { Zone("北排土场", ProcessZone.ProcDumpTip, "北排土场", 0, 0, 100, 20) }, null);
        Assert.Equal("external_dump", set2.Find(ProcessType.Dump, "北排土场")!.Category);
    }

    // ══════════════════════════════════════════════════════════════
    //  S2 工序 → 区域码
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// S2 五道工序各取各的区，<b>运输与爆破都退回采装</b>。
    /// <para>运输没有面状区域（P9），但车是从铲下装的 —— 演在采装那块地上比演在「没有」上有意义。</para>
    /// <para><b>2026-08-20 口径修正</b>：爆破由 <c>blast_guard</c> 改为 <c>load</c>。
    /// <b>爆破干的就是那块将要采剥的地</b>，它是采剥的前序工序，不是另一块地；
    /// 而 <c>blast_guard</c> 是<b>爆破警戒范围</b>（安全清场边界，本表自己的标签就是「爆破警戒」，
    /// 且它不派设备——派的是清场，人不是机）。拿警戒范围当作业区，
    /// 有警戒区时爆破演在大一圈的范围上、没有时一路退到面级轮廓，两种都不报错。
    /// 详判见 <c>BlastZoneTests</c>（BZ1–BZ4）。</para>
    /// </summary>
    [Fact]
    public void S2_工序各取各的区且运输退回采装()
    {
        Assert.Equal(ProcessZone.ProcDrill, SimProcessRegionSet.CodeOf(ProcessType.Drill));
        Assert.Equal(ProcessZone.ProcLoad, SimProcessRegionSet.CodeOf(ProcessType.Blast));
        Assert.Equal(ProcessZone.ProcLoad, SimProcessRegionSet.CodeOf(ProcessType.Load));
        Assert.Equal(ProcessZone.ProcLoad, SimProcessRegionSet.CodeOf(ProcessType.Haul));
        Assert.Equal(ProcessZone.ProcDumpTip, SimProcessRegionSet.CodeOf(ProcessType.Dump));
        Assert.Equal("", SimProcessRegionSet.CodeOf(ProcessType.Idle));
    }

    /// <summary>
    /// S2b <b>同一个面的穿孔与采装是两块不同的地</b> —— 这就是这一整层存在的理由。
    /// <para>键里漏掉工序的话，后装的那块会覆盖前一块，两道工序又回到同一条轮廓上，
    /// 而画面照样规整。</para>
    /// </summary>
    [Fact]
    public void S2b_同名不同工序是两块不同的地()
    {
        var set = SimProcessRegions.LoadFrom(M, new[]
        {
            Zone("面A", ProcessZone.ProcLoad,  "面A",   0, 0, 100, 40),
            Zone("面A", ProcessZone.ProcDrill, "面A", 200, 0, 100, 40),   // 沿推进方向错开 200m
        }, FaceLevel(("面A", "pit")));

        var load = set.Find(ProcessType.Load, "面A")!;
        var drill = set.Find(ProcessType.Drill, "面A")!;
        Assert.True(Math.Abs(drill.Centroid.X - load.Centroid.X) > 150,
                    "穿孔区与采装区落在了同一处 —— 键里大概漏了工序");
    }

    /// <summary>S2c 排土的卸载带与推排带也是两块不同的地。</summary>
    [Fact]
    public void S2c_卸载带与推排带是两块地()
    {
        var rows = new[]
        {
            Zone("排土1", ProcessZone.ProcDumpTip,  "排土1", 0,  0, 300, 25),
            Zone("排土1", ProcessZone.ProcDumpDoze, "排土1", 0, 25, 300, 75),
        };
        var set = SimProcessRegions.LoadFrom(M, rows, FaceLevel(("排土1", "external_dump")));

        // Dump 环节取的是卸载带（卡车去的地方），不是推排带
        var tip = set.Find(ProcessType.Dump, "排土1")!;
        Assert.InRange(tip.Centroid.Y, 11, 14);
        Assert.Equal(2, set.Count);
    }

    // ══════════════════════════════════════════════════════════════
    //  S3 取不到就退，且退得干净
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// S3 这道工序没有区时返回 null（调用方退面级轮廓），<b>不许拿别的工序那块顶上</b>。
    /// </summary>
    [Fact]
    public void S3_没有这道工序的区时返回null()
    {
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "面A", 0, 0, 100, 40) },
            FaceLevel(("面A", "pit")));

        Assert.NotNull(set.Find(ProcessType.Load, "面A"));
        Assert.Null(set.Find(ProcessType.Drill, "面A"));       // 不许拿采装那块顶上
        Assert.Null(set.Find(ProcessType.Dump, "面A"));
    }

    /// <summary>S3b 一块工序区都没有时整体为空，且说清楚全部退面级 + 给补法。</summary>
    [Fact]
    public void S3b_一块都没有时说清楚全部退面级()
    {
        var set = SimProcessRegions.LoadFrom(M, Array.Empty<ProcessZone>(), null);
        Assert.True(set.IsEmpty);
        Assert.Contains("全部退面级轮廓", set.SourceLabel);
        Assert.Contains("作业区划分", set.SourceLabel);
    }

    /// <summary>S3c 未纳入本期（active=0）的工序区不进推演。</summary>
    [Fact]
    public void S3c_未纳入本期的不进推演()
    {
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "面A", 0, 0, 100, 40, active: 0) }, null);
        Assert.Null(set.Find(ProcessType.Load, "面A"));
    }

    // ══════════════════════════════════════════════════════════════
    //  S4 名字匹配
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// S4 分块时（面名-2）主名对不上，取<b>本组最大的那一块</b>。
    /// <para>不取的话，一个被切成两块的面在推演里整期没有轮廓。</para>
    /// </summary>
    [Fact]
    public void S4_分块时取本组最大的那一块()
    {
        var set = SimProcessRegions.LoadFrom(M, new[]
        {
            Zone("面A-2", ProcessZone.ProcLoad, "面A",   0, 0,  40, 40),
            Zone("面A-3", ProcessZone.ProcLoad, "面A", 200, 0, 100, 40),   // 更大
        }, FaceLevel(("面A", "pit")));

        var r = set.Find(ProcessType.Load, "面A");
        Assert.NotNull(r);
        Assert.InRange(r!.AreaM2, 100 * 40 * 0.99, 100 * 40 * 1.01);
    }

    /// <summary>
    /// S4b <b>「采场1」不许配上「采场10」</b>。
    /// <para>推演在别处用的是"互相包含"的模糊匹配（NameHit）—— 那一条在这里必须收紧，
    /// 否则一块地会被配给一个根本不相干的面，而两边名字看着都挺像。</para>
    /// </summary>
    [Fact]
    public void S4b_采场1不许配上采场10()
    {
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("采场10", ProcessZone.ProcLoad, "采场10", 0, 0, 100, 40) }, null);

        Assert.Null(set.Find(ProcessType.Load, "采场1"));
        Assert.NotNull(set.Find(ProcessType.Load, "采场10"));
    }

    /// <summary>S4c 名字大小写与首尾空白不影响匹配。</summary>
    [Fact]
    public void S4c_大小写与空白不影响匹配()
    {
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("FaceA", ProcessZone.ProcLoad, "FaceA", 0, 0, 100, 40) }, null);
        Assert.NotNull(set.Find(ProcessType.Load, "  facea "));
    }

    // ══════════════════════════════════════════════════════════════
    //  S5 高程
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// S5 环的高程为 0 要<b>点名报出来</b>。
    /// <para>0 会被下游当成实测高程，层体整体摆到 0 米而画面正常 —— 这是这条链上的老账。</para>
    /// </summary>
    [Fact]
    public void S5_高程为零要点名()
    {
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "面A", 0, 0, 100, 40, z: 0) }, null);
        Assert.Contains("0m", set.SourceLabel);
    }

    /// <summary>S5b 正常高程时不报那句警告（不许每次都报 —— 每跑必报的警告等于一条死路）。</summary>
    [Fact]
    public void S5b_高程正常时不报警告()
    {
        var set = SimProcessRegions.LoadFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "面A", 0, 0, 100, 40, z: 1180) }, null);
        Assert.DoesNotContain("环高程为 0", set.SourceLabel);
        Assert.InRange(set.Find(ProcessType.Load, "面A")!.Z, 1179, 1181);
    }

    /// <summary>S6 点数不足 3 的环不进推演（围不成面），且不炸。</summary>
    [Fact]
    public void S6_围不成面的环不进推演()
    {
        var bad = Zone("面A", ProcessZone.ProcLoad, "面A", 0, 0, 100, 40);
        bad.PointsJson = "[0,0,1200,10,0,1200]";              // 只有 2 个点
        var set = SimProcessRegions.LoadFrom(M, new[] { bad }, null);
        Assert.True(set.IsEmpty);
    }
}
