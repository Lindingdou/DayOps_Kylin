// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/BlastZoneTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 爆破演在哪块地上的判据。
///
/// <para><b>口径（2026-08-20 现场口径确认）</b>：<b>爆破干的就是那块将要采剥的地</b> ——
/// 它是采剥的<b>前序工序</b>，不是另一块地。所以爆破取 <c>load</c>（采剥区），
/// 与运输退回采装是同一条路（S2/S4）。</para>
///
/// <para><b>被换掉的那一版</b>：原先爆破指向 <see cref="ProcessZone.ProcBlastGuard"/> ——
/// 那是<b>爆破警戒范围</b>，安全清场的边界（<c>ProcessZone</c> 自己的标签就写着「爆破警戒」，
/// 而且它<b>不派设备</b>：派的是清场，人不是机）。拿警戒范围当作业区有两种坏法，
/// <b>两种都不报错</b>：台账里有警戒区时，爆破演在一个比作业面大一圈的范围上；
/// 台账里没有时（绝大多数情况）一路退到面级轮廓，位置更不对。</para>
/// </summary>
public class BlastZoneTests
{
    private const string Period = "2026-08";
    private const string Face = "采场1（pit）·岩1248";

    // ── BZ1：工序 → 工序区码。爆破必须落到采剥区 ──────────────────────────────
    [Fact]
    public void BZ1_爆破取采剥区而不是警戒区()
    {
        Assert.Equal(ProcessZone.ProcLoad, SimProcessRegionSet.CodeOf(ProcessType.Blast));

        // 与它同路的两条：运输也退采装（S2）；采装自己当然是采装
        Assert.Equal(ProcessZone.ProcLoad, SimProcessRegionSet.CodeOf(ProcessType.Haul));
        Assert.Equal(ProcessZone.ProcLoad, SimProcessRegionSet.CodeOf(ProcessType.Load));

        // 反例组：穿孔/排土**不许**被一起并到采剥区上 —— 它们真有自己的地
        //（穿孔超前、排土在另一头；并过去就等于把"五道工序错开"这件事整个抹平）
        Assert.Equal(ProcessZone.ProcDrill, SimProcessRegionSet.CodeOf(ProcessType.Drill));
        Assert.Equal(ProcessZone.ProcDumpTip, SimProcessRegionSet.CodeOf(ProcessType.Dump));
    }

    // ── BZ2：台账里同时有采剥区和警戒区时，爆破取的是**采剥区** ────────────────
    //
    //  这一条是本组的核心：只判 CodeOf 的话，把映射改回警戒区、同时把警戒区也塞进台账，
    //  BZ1 会红但"演出来是哪块地"仍无人过问。这里用两块**面积不同**的地区分。
    [Fact]
    public void BZ2_有警戒区也仍然演在采剥区上()
    {
        var rows = new List<ProcessZone>
        {
            Zone(ProcessZone.ProcLoad, Face, Square(0, 0, 100)),          // 采剥区：100×100
            Zone(ProcessZone.ProcBlastGuard, Face, Square(-200, -200, 500)), // 警戒区：500×500，明显更大
        };

        var set = SimProcessRegions.LoadFrom(Period, rows, faceLevel: null);

        var blast = set.Find(ProcessType.Blast, Face);
        Assert.NotNull(blast);
        Assert.Equal(100.0 * 100.0, blast!.AreaM2, 0);      // 取到的是采剥区，不是警戒区

        // 采装取的当然也是同一块 —— 爆破是它的前序，同一块地
        var load = set.Find(ProcessType.Load, Face);
        Assert.NotNull(load);
        Assert.Equal(blast.AreaM2, load!.AreaM2, 0);
    }

    // ── BZ3：台账里**只有**警戒区时，爆破不许拿它当作业区 ──────────────────────
    //
    //  警戒范围是清场边界，不是作业区。宁可退回面级轮廓（并如实标注"面级"），
    //  也不能把一个大一圈的范围画成"爆破在这块地上干"。
    [Fact]
    public void BZ3_只有警戒区时不拿它当作业区()
    {
        var rows = new List<ProcessZone>
        {
            Zone(ProcessZone.ProcBlastGuard, Face, Square(-200, -200, 500)),
        };

        var set = SimProcessRegions.LoadFrom(Period, rows, faceLevel: null);
        Assert.Null(set.Find(ProcessType.Blast, Face));

        // 反例组：把同一块地按采剥区入库，就必须取得到（否则上面那条靠"永远取不到"也能过）
        var ok = SimProcessRegions.LoadFrom(Period,
            new List<ProcessZone> { Zone(ProcessZone.ProcLoad, Face, Square(-200, -200, 500)) },
            faceLevel: null);
        Assert.NotNull(ok.Find(ProcessType.Blast, Face));
    }

    // ── BZ4：穿孔仍然演在自己的孔位带上（爆破的改动不许殃及它）──────────────────
    [Fact]
    public void BZ4_穿孔不受影响()
    {
        var rows = new List<ProcessZone>
        {
            Zone(ProcessZone.ProcLoad, Face, Square(0, 0, 100)),
            Zone(ProcessZone.ProcDrill, Face, Square(0, 120, 60)),   // 孔位带超前在推进方向前方
        };

        var set = SimProcessRegions.LoadFrom(Period, rows, faceLevel: null);

        var drill = set.Find(ProcessType.Drill, Face);
        Assert.NotNull(drill);
        Assert.Equal(60.0 * 60.0, drill!.AreaM2, 0);              // 取的是孔位带，不是采剥区

        var blast = set.Find(ProcessType.Blast, Face);
        Assert.Equal(100.0 * 100.0, blast!.AreaM2, 0);            // 爆破仍在采剥区
    }

    // ── 夹具 ─────────────────────────────────────────────────────────────────

    private static ProcessZone Zone(string process, string name, double[] ringXyz) => new()
    {
        Period = Period,
        Process = process,
        Name = name,
        GroupKey = name,
        Active = 1,
        PointsJson = "[" + string.Join(",", ringXyz.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) + "]",
    };

    /// <summary>以 (x0,y0) 为左下角、边长 side 的正方形环（xyz 扁平，z 取 1200）。</summary>
    private static double[] Square(double x0, double y0, double side)
        => new[]
        {
            x0,        y0,        1200.0,
            x0 + side, y0,        1200.0,
            x0 + side, y0 + side, 1200.0,
            x0,        y0 + side, 1200.0,
        };
}
