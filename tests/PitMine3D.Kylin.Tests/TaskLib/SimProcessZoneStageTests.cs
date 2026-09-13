// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimProcessZoneStageTests.cs（逐行对应；仅命名空间/依赖适配）
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
/// 工序作业区图层的判据（PS 组）。
///
/// <para><b>这一层修的是「工艺工序的模拟不清晰」里的第一条</b>：三维模拟里工序只有一个彩色点，
/// 同一个作业面的五道工序全摆在同一个坐标上。地本来就有（<c>process_zone</c> / V047），
/// 只是一直没接进主推演窗。</para>
///
/// <para><b>为什么判「推下去的线段」而不是判截图</b>：形态问题只有出图看得出来（已出过两张），
/// 但出图证不了「禁入区没被填充」「斜纹没横穿出边界」这类**会静默退化**的事 ——
/// 缩略图上它们只表现为"这块地画大了一点"。两样都要，各管各的那一半。</para>
/// </summary>
public class SimProcessZoneStageTests
{
    private const string M = "2026-08";

    // ══════════════════════════════════════════════════════════════
    //  夹具
    // ══════════════════════════════════════════════════════════════

    private static ProcessZone Zone(string name, string process, string group,
                                    double x0, double y0, double w, double h, double z = 1200)
    {
        var ring = new List<ZonePoint>
        {
            new(x0, y0, z), new(x0 + w, y0, z), new(x0 + w, y0 + h, z), new(x0, y0 + h, z),
        };
        return new ProcessZone
        {
            Period = M, Process = process, Name = name, GroupKey = group,
            PointsJson = ZoneStore.SerializeRing(ring),
            ZSource = "算例", CellM = 1, AreaM2 = w * h, Active = 1,
        };
    }

    /// <summary>凹多边形（L 形）—— 扫描线配对错位只在凹的上面才暴露得出来。</summary>
    private static ProcessZone LShape(string name, string process, double z = 1200)
    {
        var ring = new List<ZonePoint>
        {
            new(0, 0, z), new(300, 0, z), new(300, 100, z),
            new(100, 100, z), new(100, 300, z), new(0, 300, z),
        };
        return new ProcessZone
        {
            Period = M, Process = process, Name = name, GroupKey = name,
            PointsJson = ZoneStore.SerializeRing(ring),
            ZSource = "算例", CellM = 1, AreaM2 = 300 * 100 + 100 * 200, Active = 1,
        };
    }

    private static SimProcessRegionSet Set(params ProcessZone[] rows)
        => SimProcessRegions.LoadFrom(M, rows, faceLevel: null);

    private static (RecordingDynamicOverlay Rec, SimProcessZoneStage Stage) Draw(
        SimProcessRegionSet set, string? only = null, bool hatch = true)
    {
        var rec = new RecordingDynamicOverlay();
        var stage = new SimProcessZoneStage(rec);
        stage.Apply(set, only, labelHeightM: 20f, hatch: hatch);
        return (rec, stage);
    }

    private static bool Inside(IReadOnlyList<SimPoint> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Y > y) != (ring[j].Y > y) &&
                x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    private static IEnumerable<(double X, double Y, uint Argb)> Midpoints(RecordingDynamicOverlay rec, string group)
    {
        var p = rec.StateOf(group);
        if (p == null) yield break;
        for (int i = 0; i < p.Count; i++)
            yield return ((p.Xyz[i * 6] + p.Xyz[i * 6 + 3]) / 2,
                          (p.Xyz[i * 6 + 1] + p.Xyz[i * 6 + 4]) / 2, p.Argb[i]);
    }

    // ══════════════════════════════════════════════════════════════
    //  PS1 每道工序各占一块地
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS1：<c>Patches</c> 收<b>台账原样</b>，含 <see cref="SimProcessRegionSet.Find"/> 永远不返回的
    /// 警戒区与推排带 —— 而它们恰恰是「五道工序各在哪」这张图上最该看见的两块。
    /// </summary>
    [Fact]
    public void PS1_Patches_KeepsGuardAndDozeThatFindNeverReturns()
    {
        var set = Set(
            Zone("面1", ProcessZone.ProcDrill, "面1", 0, 0, 100, 100),
            Zone("面1", ProcessZone.ProcLoad, "面1", 0, 0, 100, 100),
            Zone("炮1", ProcessZone.ProcBlastGuard, "炮1", -50, -50, 400, 400),
            Zone("排1", ProcessZone.ProcDumpTip, "排1", 500, 0, 60, 200),
            Zone("排1", ProcessZone.ProcDumpDoze, "排1", 560, 0, 60, 200));

        Assert.Equal(5, set.Patches.Count);
        Assert.Contains(set.Patches, p => p.Process == ProcessZone.ProcBlastGuard);
        Assert.Contains(set.Patches, p => p.Process == ProcessZone.ProcDumpDoze);

        // Find 那条路上，爆破退回采装那块地、推排带根本没有入口 —— 两条路不能合并
        Assert.Equal(ProcessZone.ProcLoad, SimProcessRegionSet.CodeOf(ProcessType.Blast));
        Assert.Equal(ProcessZone.ProcDumpTip, SimProcessRegionSet.CodeOf(ProcessType.Dump));
    }

    /// <summary>PS1：五类地都要画出来，一类都不许静默丢。</summary>
    [Fact]
    public void PS1_AllFiveKindsAreDrawn()
    {
        var set = Set(
            Zone("面1", ProcessZone.ProcDrill, "面1", 0, 0, 100, 100),
            Zone("面1", ProcessZone.ProcLoad, "面1", 0, 0, 100, 100),
            Zone("炮1", ProcessZone.ProcBlastGuard, "炮1", -50, -50, 400, 400),
            Zone("排1", ProcessZone.ProcDumpTip, "排1", 500, 0, 60, 200),
            Zone("排1", ProcessZone.ProcDumpDoze, "排1", 560, 0, 60, 200));

        var (_, stage) = Draw(set);
        Assert.Equal(5, stage.DrawnByProcess.Count);
        Assert.All(stage.DrawnByProcess.Values, n => Assert.Equal(1, n));
        Assert.Equal("", stage.DropNote);
    }

    // ══════════════════════════════════════════════════════════════
    //  PS4 警戒区是禁入区，不是作业区
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS4：警戒区**不进斜纹组**。
    /// <para>实测本矿一块警戒区 447 万 m²，是全部穿孔区的 32 倍 —— 填上就把整张图盖住，
    /// 而"工序分不出来"正是这一层要治的病。</para>
    /// </summary>
    [Fact]
    public void PS4_KeepOutZone_IsNeverHatched()
    {
        var set = Set(Zone("炮1", ProcessZone.ProcBlastGuard, "炮1", 0, 0, 400, 400));
        var (rec, stage) = Draw(set);

        Assert.Equal(1, stage.DrawnByProcess[ProcessZone.ProcBlastGuard]);
        var hatch = rec.StateOf(SimProcessZoneStage.HatchGroup);
        Assert.True(hatch == null || hatch.Count == 0,
            "警戒区被填充了 —— 它是禁入区不是作业区，且面积可以比全场还大。");

        // 边界仍要画，而且是**虚线**（一条边切成多段），否则与作业区只靠颜色分不开
        var edge = rec.StateOf(SimProcessZoneStage.EdgeGroup);
        Assert.NotNull(edge);
        Assert.True(edge!.Count > 4, $"警戒区边界只有 {edge.Count} 段，不是虚线（4 条边应切成更多段）。");
    }

    /// <summary>PS4 对照组：作业区必须**有**斜纹 —— 否则上一条判据「没有斜纹」会永远成立。</summary>
    [Fact]
    public void PS4b_WorkArea_IsHatched()
    {
        var set = Set(Zone("面1", ProcessZone.ProcLoad, "面1", 0, 0, 400, 400));
        var (rec, _) = Draw(set);
        var hatch = rec.StateOf(SimProcessZoneStage.HatchGroup);
        Assert.NotNull(hatch);
        Assert.True(hatch!.Count > 0, "作业区一条斜纹都没有，这块地在图上是空的。");
    }

    // ══════════════════════════════════════════════════════════════
    //  PS-H 斜纹：扫描线，不是三角化
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS-H：斜纹**必须落在自己那块地里**。
    /// <para>扫描线配对错位（顶点正好落在扫描线上被两条边各记一笔 ⇒ 交点数成奇数）的现象是
    /// 纹路横穿到多边形外面，而缩略图上它只表现为"这块地画大了一点"。凹多边形才暴露得出来。</para>
    /// </summary>
    [Fact]
    public void PSH_Hatch_StaysInsideTheConcavePolygon()
    {
        var set = Set(LShape("凹面", ProcessZone.ProcLoad));
        var ring = set.Patches.Single().Region.Ring;
        var (rec, _) = Draw(set);

        var mids = Midpoints(rec, SimProcessZoneStage.HatchGroup).ToList();
        Assert.True(mids.Count > 0, "凹多边形一条斜纹都没画出来。");
        var outside = mids.Where(m => !Inside(ring, m.X, m.Y)).ToList();
        Assert.True(outside.Count == 0,
            $"{outside.Count}/{mids.Count} 段斜纹落在多边形之外 —— 扫描线配对错位。");
    }

    /// <summary>
    /// PS-H：纹路条数**按块自适应**，大块小块都得有纹。
    /// <para>写死间距时，大块生出上万条、小块一条都没有；后者的现象是「这块地看着是空的」。</para>
    /// </summary>
    [Fact]
    public void PSH_Hatch_ScalesWithPatchSize()
    {
        var big = Draw(Set(Zone("大", ProcessZone.ProcLoad, "大", 0, 0, 4000, 4000))).Rec
                  .StateOf(SimProcessZoneStage.HatchGroup);
        var small = Draw(Set(Zone("小", ProcessZone.ProcLoad, "小", 0, 0, 20, 20))).Rec
                    .StateOf(SimProcessZoneStage.HatchGroup);

        Assert.NotNull(big); Assert.NotNull(small);
        Assert.True(small!.Count > 0, "20m 见方的小块一条纹都没有 —— 图上它是空的。");
        Assert.True(big!.Count < 500, $"4km 见方的大块生出 {big.Count} 条纹，会把面板糊死。");
    }

    /// <summary>
    /// PS-H：**重合的两块地靠纹路角度分开**。
    /// <para>这是本轮最要紧的一条：月尺度上穿孔与采装本来就是同一块地
    /// （实测 11 块里 10 块环逐点相同），边界完全重合 ⇒ 只有纹路角度分得出它们。</para>
    /// </summary>
    [Fact]
    public void PSH_CoincidentZones_AreDistinguishedByHatchAngle()
    {
        var set = Set(
            Zone("面1", ProcessZone.ProcDrill, "面1", 0, 0, 400, 400),
            Zone("面1", ProcessZone.ProcLoad, "面1", 0, 0, 400, 400));   // 同一块地
        var (rec, _) = Draw(set);

        var p = rec.StateOf(SimProcessZoneStage.HatchGroup);
        Assert.NotNull(p);

        // 逐段方向：两道工序的纹路必须不是同一个方向
        var dirs = new HashSet<int>();
        for (int i = 0; i < p!.Count; i++)
        {
            double dx = p.Xyz[i * 6 + 3] - p.Xyz[i * 6];
            double dy = p.Xyz[i * 6 + 4] - p.Xyz[i * 6 + 1];
            dirs.Add((int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI / 5) * 5);
        }
        Assert.True(dirs.Count >= 2,
            "两块完全重合的地画出了同一个方向的纹路 —— 图上它们无法分辨。");

        // 而且两种颜色都在（不是只画了后来的那一块）
        var colors = Enumerable.Range(0, p.Count).Select(i => p.Argb[i] & 0x00FFFFFFu).Distinct().ToList();
        Assert.Contains(SimProcessPalette.RgbDrill, colors);
        Assert.Contains(SimProcessPalette.RgbLoad, colors);
    }

    // ══════════════════════════════════════════════════════════════
    //  PS10 重合要说出来
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS10：两块地环逐点相同时必须**明说**。
    /// <para>不说的话，看图的人只有两种解释（画错了 / 工序根本没错开），
    /// 而真相是第三种：它们错开在时间轴上。我自己就先去查了一遍并不存在的几何 bug。</para>
    /// </summary>
    [Fact]
    public void PS10_IdenticalRings_AreCalledOut()
    {
        var set = Set(
            Zone("面1", ProcessZone.ProcDrill, "面1", 0, 0, 400, 400),
            Zone("面1", ProcessZone.ProcLoad, "面1", 0, 0, 400, 400));

        string note = SimProcessZoneStage.CoincidenceNote(set);
        Assert.Contains("同一块地", note);
        Assert.Contains("穿孔", note);
        Assert.Contains("采装", note);
        Assert.Contains("时间轴", note);
    }

    /// <summary>PS10 对照组：真错开时**不许**报重合（否则这句话到处都在，等于没说）。</summary>
    [Fact]
    public void PS10b_SeparatedZones_ProduceNoNote()
    {
        var set = Set(
            Zone("面1", ProcessZone.ProcDrill, "面1", 0, 0, 100, 400),
            Zone("面1", ProcessZone.ProcLoad, "面1", 300, 0, 100, 400));
        Assert.Equal("", SimProcessZoneStage.CoincidenceNote(set));
    }

    // ══════════════════════════════════════════════════════════════
    //  图例 / 过滤 / 空集
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS9：图例只列**这一帧真画了的**工序。
    /// <para>列上没画的那几道，等于告诉人"图上应该有"，而它一块地都没有。</para>
    /// </summary>
    [Fact]
    public void PS9_Legend_ListsOnlyWhatWasActuallyDrawn()
    {
        var set = Set(Zone("面1", ProcessZone.ProcLoad, "面1", 0, 0, 100, 100));
        var (_, stage) = Draw(set);
        Assert.Contains("采装", stage.Legend);
        Assert.DoesNotContain("穿孔", stage.Legend);
        Assert.DoesNotContain("排土", stage.Legend);
    }

    /// <summary>「只看某一道工序」只挡显示：挡住的那几道一段线都不该推下去。</summary>
    [Fact]
    public void PS3_OnlyOneProcess_HidesTheRest()
    {
        var set = Set(
            Zone("面1", ProcessZone.ProcDrill, "面1", 0, 0, 100, 100),
            Zone("面1", ProcessZone.ProcLoad, "面1", 200, 0, 100, 100));

        var (rec, stage) = Draw(set, only: ProcessZone.ProcDrill);
        Assert.Single(stage.DrawnByProcess);
        Assert.True(stage.DrawnByProcess.ContainsKey(ProcessZone.ProcDrill));

        var colors = Midpoints(rec, SimProcessZoneStage.EdgeGroup)
                     .Select(m => m.Argb & 0x00FFFFFFu).Distinct().ToList();
        Assert.DoesNotContain(SimProcessPalette.RgbLoad, colors);
    }

    /// <summary>
    /// 空集要说清楚，而且要说到「所以你在图上会看到什么」——
    /// 只写「没有工序区」的话，人不知道那意味着五道工序会叠在一起。
    /// </summary>
    [Fact]
    public void PS_EmptySet_SaysWhatTheMapWillLookLike()
    {
        var stage = new SimProcessZoneStage(new RecordingDynamicOverlay());
        string s = stage.StatusOf(SimProcessRegions.LoadFrom(M, Array.Empty<ProcessZone>(), null));
        Assert.Contains("一块地都没有", s);
        Assert.Contains("作业区划分", s);      // 指路
    }

    // ══════════════════════════════════════════════════════════════
    //  PS3 配色一处定义
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS3：任务侧枚举与工序码两套键必须给出**同一个颜色**。
    /// <para>配色以前写死在窗口的一个 switch 里，甘特另写一份 —— 改一处就悄悄分叉，
    /// 现象是「同一道工序在两个窗里是两个颜色」，没有任何东西会报错。</para>
    /// </summary>
    [Fact]
    public void PS3_Palette_TwoKeysAgree()
    {
        Assert.Equal(SimProcessPalette.RgbOf(ProcessType.Drill), SimProcessPalette.RgbOfCode(ProcessZone.ProcDrill));
        Assert.Equal(SimProcessPalette.RgbOf(ProcessType.Blast), SimProcessPalette.RgbOfCode(ProcessZone.ProcBlastGuard));
        Assert.Equal(SimProcessPalette.RgbOf(ProcessType.Load), SimProcessPalette.RgbOfCode(ProcessZone.ProcLoad));
        Assert.Equal(SimProcessPalette.RgbOf(ProcessType.Dump), SimProcessPalette.RgbOfCode(ProcessZone.ProcDumpTip));

        // 排土两带同族不同调：合成一个颜色就等于承认它们是一块地（PS5 反面）
        Assert.NotEqual(SimProcessPalette.RgbOfCode(ProcessZone.ProcDumpTip),
                        SimProcessPalette.RgbOfCode(ProcessZone.ProcDumpDoze));

        // 认不出的工序码给灰，不冒充成某一类
        Assert.Equal(SimProcessPalette.RgbIdle, SimProcessPalette.RgbOfCode("no_such_process"));
    }

    /// <summary>PS3：图例、读数表、工序链带三处共用同一个序 —— 各排各的就会自相矛盾。</summary>
    [Fact]
    public void PS3b_Order_IsMonotonicAlongTheChain()
    {
        Assert.True(SimProcessPalette.OrderOfCode(ProcessZone.ProcDrill)
                  < SimProcessPalette.OrderOfCode(ProcessZone.ProcBlastGuard));
        Assert.True(SimProcessPalette.OrderOfCode(ProcessZone.ProcBlastGuard)
                  < SimProcessPalette.OrderOfCode(ProcessZone.ProcLoad));
        Assert.True(SimProcessPalette.OrderOfCode(ProcessZone.ProcLoad)
                  < SimProcessPalette.OrderOfCode(ProcessZone.ProcDumpTip));
        Assert.True(SimProcessPalette.OrderOfCode(ProcessZone.ProcDumpTip)
                  < SimProcessPalette.OrderOfCode(ProcessZone.ProcDumpDoze));

        // 运输落在采装与排土之间（它没有面状区域，但在链上有位置）
        Assert.True(SimProcessPalette.OrderOf(ProcessType.Load) < SimProcessPalette.OrderOf(ProcessType.Haul));
        Assert.True(SimProcessPalette.OrderOf(ProcessType.Haul) < SimProcessPalette.OrderOf(ProcessType.Dump));
    }

    // ══════════════════════════════════════════════════════════════
    //  PS-Z 高程
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS-Z：每块地用**它自己的高程**。摊平到 0m 的话，排土场(+1300m)与采场底(+1100m)
    /// 会画在一起，而画面上看着完全正常。
    /// </summary>
    [Fact]
    public void PSZ_EachPatchKeepsItsOwnElevation()
    {
        var set = Set(
            Zone("低", ProcessZone.ProcLoad, "低", 0, 0, 100, 100, z: 1128),
            Zone("高", ProcessZone.ProcDumpTip, "高", 500, 0, 100, 100, z: 1320));

        var (rec, _) = Draw(set);
        var zs = Midpoints(rec, SimProcessZoneStage.EdgeGroup).ToList();
        var p = rec.StateOf(SimProcessZoneStage.EdgeGroup)!;
        var heights = Enumerable.Range(0, p.Count).Select(i => Math.Round(p.Xyz[i * 6 + 2])).Distinct().ToList();

        Assert.Contains(1129d, heights);   // 1128 + 1（边界抬 1m 免得与地面 z-fighting）
        Assert.Contains(1321d, heights);
    }
}
