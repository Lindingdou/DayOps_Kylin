// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimProcessReadoutTests.cs（逐行对应；仅命名空间/依赖适配）
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
/// 工序读数的判据（PS8）。
///
/// <para><b>这一组守的是一条只能靠判据守的口径</b>：三个方量口径绝不许并成一列。
/// 它没有任何自然现象会暴露 —— 加错了照样出一个数，照样自洽，照样没人报错。
/// 第一版这段逻辑写在窗口的私有方法里，等于这条口径根本没有守卫（同 <c>SimPeriodKey</c> 那次）。</para>
/// </summary>
public class SimProcessReadoutTests
{
    private const string M = "2026-08";

    private static ProcessZone Zone(string name, string process, double area, double? vol, double z = 1200)
    {
        var ring = new List<ZonePoint>
        {
            new(0, 0, z), new(100, 0, z), new(100, 100, z), new(0, 100, z),
        };
        return new ProcessZone
        {
            Period = M, Process = process, Name = name, GroupKey = name,
            PointsJson = ZoneStore.SerializeRing(ring),
            ZSource = "算例", CellM = 1, AreaM2 = area, VolumeM3 = vol, Active = 1,
        };
    }

    private static SimProcessRegionSet Set(params ProcessZone[] rows)
        => SimProcessRegions.LoadFrom(M, rows, faceLevel: null);

    private static SimProcRow Row(IReadOnlyList<SimProcRow> rows, string name)
        => rows.Single(r => r.Name == name);

    // ══════════════════════════════════════════════════════════════
    //  PS8 三本账不许合并
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PS8：三行量各自带**自己的口径文字**，而且三种口径文字互不相同。
    /// <para>口径写在量的同一格里，就是为了让"把这三个数加起来"这件事在视觉上先别扭起来。</para>
    /// </summary>
    [Fact]
    public void PS8_ThreeVolumeBases_AreLabelledAndDistinct()
    {
        var rows = SimProcessReadout.Build(Set(
            Zone("面1", ProcessZone.ProcDrill, 10000, 30000),
            Zone("面1", ProcessZone.ProcLoad, 50000, 120000),
            Zone("排1", ProcessZone.ProcDumpTip, 20000, 90000)), frame: null);

        Assert.Contains("控制方量", Row(rows, "穿孔").Volume);
        Assert.Contains("原位实方", Row(rows, "采装").Volume);
        Assert.Contains("排弃占容", Row(rows, "排土·卸载").Volume);

        // 三种口径文字必须互不相同 —— 同名就等于默许它们是一列
        var bases = new[] { "控制方量", "原位实方", "排弃占容" };
        Assert.Equal(bases.Length, bases.Distinct().Count());
    }

    /// <summary>
    /// PS8：**爆破的量位是「—」，不是 0**（M9）。
    /// <para>0 会被读成"这个月一炮没放"；而真相是它根本没有自己的方量口径 ——
    /// 它爆的就是穿孔那一笔控制方量，再记一遍就是把同一方岩算两次。</para>
    /// </summary>
    [Fact]
    public void PS8_Blast_HasNoVolumeOfItsOwn_AndSaysSo()
    {
        var rows = SimProcessReadout.Build(Set(
            Zone("炮1", ProcessZone.ProcBlastGuard, 4_000_000, null)), frame: null);

        var blast = Row(rows, "爆破");
        Assert.False(blast.HasVolume);
        Assert.StartsWith(SimProcRow.Dash, blast.Volume);
        Assert.DoesNotContain("0 万m³", blast.Volume);
        Assert.Contains("控制方量", blast.Volume);      // 说清"那笔量在谁那儿"

        // 但**地**要有：警戒范围是它唯一占的那块地
        Assert.NotEqual(SimProcRow.Dash, blast.Land);
        Assert.Contains("禁入区", blast.Land);
    }

    /// <summary>PS8：推排不另计量 —— 卸下来的那一方就是要推的那一方。</summary>
    [Fact]
    public void PS8_Doze_IsNotCountedTwice()
    {
        var rows = SimProcessReadout.Build(Set(
            Zone("排1", ProcessZone.ProcDumpTip, 20000, 90000),
            Zone("排1", ProcessZone.ProcDumpDoze, 13000, 90000)), frame: null);

        var doze = Row(rows, "排土·推排");
        Assert.False(doze.HasVolume);
        Assert.NotEqual(SimProcRow.Dash, doze.Land);   // 地照样要有：推土机站在那儿
    }

    /// <summary>
    /// PS8：运输**没有面状作业区**（P9）—— 地那一列写清"线状"，不是留空、更不是圈一块地出来。
    /// </summary>
    [Fact]
    public void PS8_Haul_HasNoAreaZone_ButSaysWhy()
    {
        var rows = SimProcessReadout.Build(Set(Zone("面1", ProcessZone.ProcLoad, 50000, 120000)), frame: null);
        var haul = Row(rows, "运输");
        Assert.StartsWith(SimProcRow.Dash, haul.Land);
        Assert.Contains("线状", haul.Land);
        Assert.Contains("装车点", haul.Land);
    }

    // ══════════════════════════════════════════════════════════════
    //  「—」不是 0
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 台账整个缺失时，六行的地与量**全是「—」**，一个 0 都不许出现。
    /// <para>0 说的是"量到了、是零"；「—」说的是"这个数没有出处"。两者的处置完全不同。</para>
    /// </summary>
    [Fact]
    public void PS8_NoLedger_ProducesDashesNotZeros()
    {
        var rows = SimProcessReadout.Build(SimProcessRegions.LoadFrom(M, Array.Empty<ProcessZone>(), null),
                                           frame: null);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.False(r.HasVolume));
        Assert.All(rows, r => Assert.DoesNotContain("0 万m³", r.Volume));
        Assert.All(rows, r => Assert.DoesNotContain("0 块", r.Land));
    }

    /// <summary>
    /// 台账里那一块**量为 null**（没填）时，量位仍是「—」——
    /// 不许因为 <c>Sum()</c> 把 null 当 0 而打出「0 万m³（控制方量）」。
    /// </summary>
    [Fact]
    public void PS8_NullVolumeInLedger_StaysDash()
    {
        var rows = SimProcessReadout.Build(Set(Zone("面1", ProcessZone.ProcDrill, 10000, vol: null)), frame: null);
        var drill = Row(rows, "穿孔");
        Assert.NotEqual(SimProcRow.Dash, drill.Land);   // 地有
        Assert.False(drill.HasVolume);                  // 量没有 —— 两件事分开
    }

    // ══════════════════════════════════════════════════════════════
    //  帧优先于台账
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 有帧时采装量取**帧里真正排下去的源**，不取台账的静态量。
    /// <para>台账那个数是"这块地上一共有多少"，帧里那个是"本期排了多少"——不是一回事。</para>
    /// </summary>
    [Fact]
    public void PS8_FrameSources_WinOverLedgerVolume()
    {
        var set = Set(Zone("面1", ProcessZone.ProcLoad, 50000, 1_200_000));
        var frame = new SimFrame { Period = M, Label = M };
        frame.Sources.Add(new SimSourceStep { InSituM3 = 300_000 });

        var withFrame = Row(SimProcessReadout.Build(set, frame), "采装").Volume;
        var without = Row(SimProcessReadout.Build(set, null), "采装").Volume;

        Assert.Contains("30 万m³", withFrame);      // 帧：30 万
        Assert.Contains("120 万m³", without);       // 台账：120 万
        Assert.NotEqual(withFrame, without);
    }

    /// <summary>
    /// 「人机／本期 N 笔」只有帧里带作业清单才有。
    /// <para>月度帧不带（<c>SimBuilder</c> 只在日班档填 Activities）—— 那一列必须是「—」，
    /// 不许拿台账的块数冒充笔数。</para>
    /// </summary>
    [Fact]
    public void PS8_WorkColumn_ComesOnlyFromActivities()
    {
        var set = Set(Zone("面1", ProcessZone.ProcLoad, 50000, 120000));

        Assert.Equal(SimProcRow.Dash, Row(SimProcessReadout.Build(set, null), "采装").Work);

        var frame = new SimFrame { Period = M, Label = M };
        frame.Activities.Add(new SimActivity
        {
            Process = ProcessType.Load, Zone = "面1", Equipment = "WK-10",
            StartHour = 8, EndHour = 16, IsVolumetric = true,
        });
        var work = Row(SimProcessReadout.Build(set, frame), "采装").Work;
        Assert.Contains("1 笔", work);
        Assert.Contains("8 h", work);
        Assert.Contains("1 台", work);
    }

    // ══════════════════════════════════════════════════════════════
    //  行的完整性
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 六行**恒定存在**、且按工序链序排。
    /// <para>"这道工序这期没有量"与"这道工序不在读数表里"是两件事：
    /// 后者会让人以为这个矿没有这道工序。</para>
    /// </summary>
    [Fact]
    public void PS8_AllSixRows_AlwaysPresent_InChainOrder()
    {
        var rows = SimProcessReadout.Build(null, null);
        Assert.Equal(new[] { "穿孔", "爆破", "采装", "运输", "排土·卸载", "排土·推排" },
                     rows.Select(r => r.Name).ToArray());

        // 配色与图层同源（PS3）：读数表与图上不能是两套颜色
        Assert.Equal(SimProcessPalette.RgbDrill, Row(rows, "穿孔").Rgb);
        Assert.Equal(SimProcessPalette.RgbDumpDoze, Row(rows, "排土·推排").Rgb);
    }

    /// <summary>口径提示**永远在**，不是出了问题才出现 —— 它守的是一条平时最容易被顺手违反的规则。</summary>
    [Fact]
    public void PS8_BasisNote_IsAlwaysAvailable()
    {
        Assert.Contains("不许并成一列", SimProcessReadout.BasisNote);
        Assert.Contains("控制方量", SimProcessReadout.BasisNote);
        Assert.Contains("原位实方", SimProcessReadout.BasisNote);
        Assert.Contains("排弃占容", SimProcessReadout.BasisNote);
    }

    /// <summary>台账缺失的指路要说到「所以图上会看到什么」，不是只说"没有数据"。</summary>
    [Fact]
    public void PS8_NoLedgerNote_SaysWhatTheMapWillLookLike()
    {
        Assert.Contains("作业区划分", SimProcessReadout.NoLedgerNote);
        Assert.Contains("共用作业面", SimProcessReadout.NoLedgerNote);
    }
}
