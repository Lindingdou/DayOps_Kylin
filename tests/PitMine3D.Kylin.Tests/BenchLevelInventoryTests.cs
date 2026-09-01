using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using SL = PitMine3D.Kylin.Cad.BenchLevelInventory.SourceLine;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 平盘标高清单 回归 —— 忠实移植原 BenchLevelInventory 的已知值验证:
/// 标高一维聚类 / 链式吞并防护(≤2×容差) / 斜线·碎线·无效剔除 / 平面长度(仅XY) / CSV 喂料。
/// </summary>
public class BenchLevelInventoryTests
{
    // 水平线: 标高 z, 从 (x0,y0) 到 (x1,y1)。
    static SL H(ulong h, double z, double x0, double y0, double x1, double y1, string layer = "")
        => new() { Handle = h, Layer = layer, Xyz = new[] { x0, y0, z, x1, y1, z } };

    [Fact]
    public void Three_distinct_levels_high_to_low()
    {
        var r = BenchLevelInventory.Build(new List<SL>
        {
            H(0, 100, 0, 0, 10, 0),
            H(1, 110, 0, 0, 10, 0),
            H(2, 120, 0, 0, 10, 0),
        });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, r.LevelCount);
        Assert.Equal(120, r.Levels[0].Elevation, 3);   // 高→低
        Assert.Equal(110, r.Levels[1].Elevation, 3);
        Assert.Equal(100, r.Levels[2].Elevation, 3);
        Assert.Equal(1, r.Levels[0].Index);
        Assert.Equal(120, r.TopZ, 3);
        Assert.Equal(100, r.BottomZ, 3);
        Assert.Equal(20, r.RangeM, 3);
        Assert.Equal(10, r.MedianDropM, 3);            // 级间距中位
        Assert.Equal(0, r.DropSpreadM, 3);             // 齐整
        Assert.Equal(10, r.Levels[0].DropToNextM, 3);
        Assert.Equal(0, r.Levels[2].DropToNextM, 3);   // 最低级到下一级=0
    }

    [Fact]
    public void Crest_and_floor_at_same_elevation_merge_into_one_level()
    {
        // 坡顶 100.0 与下一台阶坡底 100.2 (差 0.2 < 0.5 容差) → 同一级; 另有 110 一级。
        var r = BenchLevelInventory.Build(new List<SL>
        {
            H(0, 100.0, 0, 0, 10, 0),
            H(1, 100.2, 0, 0, 10, 0),
            H(2, 110.0, 0, 0, 10, 0),
        });
        Assert.Equal(2, r.LevelCount);
        var low = r.Levels[1];                          // 低级 = 100 附近
        Assert.Equal(2, low.LineCount);
        Assert.Equal(100.1, low.Elevation, 3);          // 中位数(100.0,100.2)
        Assert.Equal(3, r.UsedLineCount);
    }

    [Fact]
    public void One_bench_broken_into_many_lines_counts_as_one_level()
    {
        // 同一级平盘被打断成 3 段 + 另一级 → 2 级(按标高归, 不按线数虚高)。
        var r = BenchLevelInventory.Build(new List<SL>
        {
            H(0, 100, 0, 0, 5, 0),
            H(1, 100, 5, 0, 10, 0),
            H(2, 100, 10, 0, 15, 0),
            H(3, 112, 0, 0, 10, 0),
        });
        Assert.Equal(2, r.LevelCount);
        Assert.Equal(3, r.Levels[1].LineCount);         // 低级 3 段
        Assert.Equal(1, r.Levels[0].LineCount);
    }

    [Fact]
    public void Chain_guard_limits_cluster_span_to_twice_tolerance()
    {
        // Z = 100.0/100.4/100.8/101.2, 逐间隙 0.4(<0.5 不断), 但跨度限 2×0.5=1.0:
        // 101.2 使跨度 1.2>1.0 → 断出新级。故 2 级: {101.2} 与 {100.0,100.4,100.8}。
        var r = BenchLevelInventory.Build(new List<SL>
        {
            H(0, 100.0, 0, 0, 10, 0),
            H(1, 100.4, 0, 0, 10, 0),
            H(2, 100.8, 0, 0, 10, 0),
            H(3, 101.2, 0, 0, 10, 0),
        });
        Assert.Equal(2, r.LevelCount);
        Assert.Equal(101.2, r.Levels[0].Elevation, 3);
        Assert.Equal(3, r.Levels[1].LineCount);
        Assert.Equal(100.4, r.Levels[1].Elevation, 3);  // 中位(100.0,100.4,100.8)
        Assert.Equal(0.8, r.Levels[1].SpanM, 3);
    }

    [Fact]
    public void Tilted_line_is_skipped()
    {
        // 一条起伏 5m 的斜线(坡面/出入沟) 不定平盘标高 → 被剔。
        var lines = new List<SL>
        {
            H(0, 100, 0, 0, 10, 0),
            H(1, 110, 0, 0, 10, 0),
            new SL { Handle = 2, Xyz = new double[] { 0, 0, 100, 10, 0, 105 } },   // 斜线
        };
        var r = BenchLevelInventory.Build(lines);
        Assert.Equal(1, r.SkippedTilted);
        Assert.Equal(2, r.UsedLineCount);
        Assert.Equal(2, r.LevelCount);
        Assert.Contains(r.Warnings, w => w.Contains("坡面线"));
    }

    [Fact]
    public void Short_and_invalid_lines_are_skipped()
    {
        var r = BenchLevelInventory.Build(new List<SL>
        {
            H(0, 100, 0, 0, 20, 0),                                   // 长 20
            H(1, 110, 0, 0, 2, 0),                                    // 长 2 (< MinLength 10)
            new SL { Handle = 2, Xyz = new double[] { 0, 0, 120 } },  // 单点 → 无效
        }, new BenchLevelInventory.Options { MinLengthM = 10 });
        Assert.Equal(1, r.SkippedShort);
        Assert.Equal(1, r.SkippedInvalid);
        Assert.Equal(1, r.UsedLineCount);
        Assert.Equal(1, r.LevelCount);
    }

    [Fact]
    public void Plane_length_ignores_elevation()
    {
        // (0,0,100)->(30,40,100): XY 平面长 = 50。
        var r = BenchLevelInventory.Build(new List<SL> { H(0, 100, 0, 0, 30, 40) });
        Assert.Equal(50, r.Levels[0].TotalLengthM, 3);
    }

    [Fact]
    public void Empty_and_all_tilted_return_not_ok()
    {
        Assert.False(BenchLevelInventory.Build(null).Ok);
        Assert.False(BenchLevelInventory.Build(new List<SL>()).Ok);
        var allTilted = BenchLevelInventory.Build(new List<SL>
        {
            new SL { Handle = 0, Xyz = new double[] { 0, 0, 100, 10, 0, 108 } },
        });
        Assert.False(allTilted.Ok);
        Assert.Contains("斜线", allTilted.Message);
    }

    [Fact]
    public void BuildReport_has_count_and_table_header()
    {
        var r = BenchLevelInventory.Build(new List<SL> { H(0, 100, 0, 0, 10, 0), H(1, 110, 0, 0, 10, 0) });
        var csv = BenchLevelInventory.BuildReport(r, "测试取线");
        Assert.Contains("平盘标高个数,2", csv);
        Assert.Contains("序号,标高(m),线条数", csv);
        Assert.Contains("取线范围,测试取线", csv);
    }

    [Fact]
    public void ParseCsv_groups_rows_by_line_id()
    {
        string csv = "lineId,x,y,z,layer\n"
                   + "A,0,0,100,坡顶\n"
                   + "A,10,0,100,坡顶\n"
                   + "# 注释行\n"
                   + "B,0,0,110,坡底\n"
                   + "B,10,0,110,坡底\n";
        var lines = BenchLevelInventory.ParseCsv(csv);
        Assert.Equal(2, lines.Count);
        Assert.Equal(6, lines[0].Xyz.Length);          // A: 2 点 × 3
        Assert.Equal("坡顶", lines[0].Layer);
        var r = BenchLevelInventory.Build(lines);
        Assert.Equal(2, r.LevelCount);
        Assert.Equal(110, r.Levels[0].Elevation, 3);
    }
}
