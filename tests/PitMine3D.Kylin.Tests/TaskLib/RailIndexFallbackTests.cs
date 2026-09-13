// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/RailIndexFallbackTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;
using PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// <b>RF 组 · 三维取真轨的两个来源</b>（会话内交接件 + 落盘伴生文件）。
///
/// <para>原来的闸是 <c>if (coal + rock + dump == 0)</c> —— <b>会话内一条都没有</b>才去读盘。
/// 可这两个交接件是各自独立的：开一次软件只跑了「排土条带」，dump&gt;0 ⇒ 闸关上 ⇒
/// 盘上明明有煤/岩的真轨，那 933 个单元照样全退盒子，<b>而且不报错</b>
/// （屏幕上只是"形状不对"）。"会话内有一条"证明不了"会话内那一类也有"。</para>
///
/// <para>所以口径是<b>逐 UnitId 补空缺</b>：会话内的优先（刚生成），会话内没有的从盘上补，
/// 并把"补了多少、最早是什么时候生成的"说出来 —— 补齐与新鲜是两回事。</para>
/// </summary>
public class RailIndexFallbackTests
{
    private readonly ITestOutputHelper _out;
    public RailIndexFallbackTests(ITestOutputHelper o) => _out = o;

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "pitmine_railidx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static UnitRailFile.RailRow Rail(string id, bool dump = false)
        => new()
        {
            UnitId = id,
            Crest = new[] { 621199.123, 4381136.456, 1292.75, 621299.5, 4381200.25, 1291.5, 621399.0, 4381260.125, 1290.25 },
            Toe = new[] { 621190.0, 4381130.0, 1277.5, 621290.0, 4381195.0, 1276.5, 621390.0, 4381255.0, 1275.0 },
            WidthM = 40.0,
            CrestZ = dump ? 1292.75 : null,
            ToeZ = dump ? 1277.5 : null,
        };

    private static MiningUnitLedger.Row Unit(string id) => new() { UnitId = id };

    /// <summary>判据用的一次「排土条带」结果 —— 会话内只有这一类。</summary>
    private static DumpStripPlanner.Result DumpOnly(string code)
    {
        var r = new DumpStripPlanner.Result { Ok = true, Message = "判据造的" };
        r.Cells.Add(new DumpStripPlanner.Cell
        {
            Code = code,
            LevelIndex = 1, PanelIndex = 1, StepIndex = 1,
            CrestZ = 1479.761, ToeZ = 1474.948,
            StripWidthM = 40,
            CrestXyz = new[] { 623305.519, 4380093.137, 1479.761, 623309.41, 4380090.037, 1479.761 },
            ToeXyz = new[] { 623306.0, 4380093.0, 1474.948, 623309.53, 4380090.187, 1474.948 },
        });
        return r;
    }

    [Fact(DisplayName = "RF1 会话内只有排土条带时，煤/岩从落盘文件补齐（不许静默退盒子）")]
    public void RF1_DiskFillsTheKindsSessionLacks()
    {
        string dir = TempDir();
        try
        {
            // 盘上有上一次落下的三类真轨
            UnitRailFile.Write(UnitRailFile.PathIn(dir), new[]
            {
                Rail("9-B2-P6"), Rail("1010-B1-P1"), Rail("内排土场1-L9-P09-S01", dump: true),
            }, out string w);
            _out.WriteLine(w);

            // 这一次会话只跑过「排土条带」
            MiningModelStore.Clear(); DumpStripStore.Clear();
            DumpStripStore.Put(DumpOnly("内排土场1-L1-P01-S01"), "判据造的一次排土条带");

            var idx = UnitSolidStage.RailIndex.Snapshot(dir);
            _out.WriteLine(idx.Caption);

            Assert.NotNull(idx.Find(Unit("9-B2-P6")));          // ← 修之前这里是 null，整批退盒子
            Assert.NotNull(idx.Find(Unit("1010-B1-P1")));
            Assert.NotNull(idx.Find(Unit("内排土场1-L9-P09-S01")));
            Assert.NotNull(idx.Find(Unit("内排土场1-L1-P01-S01")));   // 本次会话生成的那个

            // 两个来源同时有的时候，Caption 必须把【各来了多少】说清楚
            Assert.Contains("会话内", idx.Caption);
            Assert.Contains("落盘文件补", idx.Caption);
        }
        finally
        {
            MiningModelStore.Clear(); DumpStripStore.Clear();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact(DisplayName = "RF2 同一个 UnitId 两边都有时，用会话内刚生成的那条")]
    public void RF2_SessionWinsOverDisk()
    {
        string dir = TempDir();
        try
        {
            // 盘上那条：宽 40、三点
            UnitRailFile.Write(UnitRailFile.PathIn(dir), new[] { Rail("内排土场1-L1-P01-S01", dump: true) }, out _);

            MiningModelStore.Clear(); DumpStripStore.Clear();
            DumpStripStore.Put(DumpOnly("内排土场1-L1-P01-S01"), "判据造的一次排土条带");   // 同名，两点

            var idx = UnitSolidStage.RailIndex.Snapshot(dir);
            _out.WriteLine(idx.Caption);

            var got = idx.Find(Unit("内排土场1-L1-P01-S01"));
            Assert.NotNull(got);
            // 会话内那条是 2 点、顶 1479.761；盘上那条是 3 点、顶 1292.75
            Assert.Equal(2, got!.Value.Crest.Length / 3);
            Assert.Equal(1479.761, got.Value.ConstTopZ!.Value, 3);
            Assert.DoesNotContain("落盘文件补", idx.Caption);      // 一条都不用补
        }
        finally
        {
            MiningModelStore.Clear(); DumpStripStore.Clear();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact(DisplayName = "RF3 会话内空、盘上也没有 ⇒ 说清楚是全退盒子，不假装有")]
    public void RF3_NothingAnywhereSaysSo()
    {
        string dir = TempDir();
        try
        {
            MiningModelStore.Clear(); DumpStripStore.Clear();
            var idx = UnitSolidStage.RailIndex.Snapshot(dir);
            _out.WriteLine(idx.Caption);
            Assert.Null(idx.Find(Unit("9-B2-P6")));
            Assert.Contains("全部退盒子", idx.Caption);
        }
        finally
        {
            MiningModelStore.Clear(); DumpStripStore.Clear();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
