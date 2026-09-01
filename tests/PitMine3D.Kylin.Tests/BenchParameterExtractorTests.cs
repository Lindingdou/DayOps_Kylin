using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using BPE = PitMine3D.Kylin.Cad.BenchParameterExtractor;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 现状台阶参数提取 回归 —— 忠实移植原 PlanLib.ShortTerm.ParameterExtractor 的已知值验证。
/// 合成两级台阶(H=10, α=45°, W=5): 逐顶点最近邻反推 H/α/W/β/采深/台阶数。
/// </summary>
public class BenchParameterExtractorTests
{
    // 一条沿 Y∈[0,10] 的台阶线(常 X,Z), 2 顶点。
    static BPE.Line L(bool crest, double x, double z)
        => BPE.Line.From(new[] { x, 0.0, z, x, 10.0, z }, crest);

    // 两级台阶(开采向 +X, 逐级降 Z):
    //   crest1 X=0 Z=20 → toe1 X=10 Z=10 (H=10,run=10,α=45) ; 平盘 W=5 → crest2 X=15 Z=10 → toe2 X=25 Z=0
    static List<BPE.Line> TwoBench() => new()
    {
        L(true, 0, 20),   // crest1
        L(false, 10, 10), // toe1
        L(true, 15, 10),  // crest2
        L(false, 25, 0),  // toe2
    };

    [Fact]
    public void Two_bench_staircase_known_values()
    {
        var r = BPE.Extract(TwoBench());
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.BenchCount);
        Assert.Equal(10, r.BenchHeight, 3);
        Assert.Equal(45, r.FaceAngleDeg, 2);
        Assert.Equal(5, r.BermWidth, 3);
        // β = atan(H/(H/tanα + W)) = atan(10/(10+5)) = atan(0.6667) ≈ 33.69°
        Assert.Equal(33.69, r.OverallSlopeAngleDeg, 1);
        // 实量 β: 最高坡顶(0,0,20) → 最低坡底(25,0,0): atan(20/25) ≈ 38.66°
        Assert.Equal(38.66, r.OverallSlopeAngleMeasuredDeg, 1);
        Assert.Equal(20, r.DepthM, 3);
        Assert.Equal(0, r.HeightSpreadM, 3);   // 齐整
        Assert.Equal(0, r.BermSpreadM, 3);
        Assert.Equal(4, r.Benches.Count);      // 4 个坡顶顶点各一坡面采样
    }

    [Fact]
    public void Single_bench_warns_and_has_no_berm()
    {
        var r = BPE.Extract(new List<BPE.Line> { L(true, 0, 20), L(false, 10, 10) });
        Assert.True(r.Ok);
        Assert.Equal(1, r.BenchCount);
        Assert.Equal(10, r.BenchHeight, 3);
        Assert.Equal(0, r.BermWidth, 3);       // 找不到同标高坡顶 → 无平盘
        Assert.Contains(r.Warnings, w => w.Contains("仅识别到 1 级"));
    }

    [Fact]
    public void Uneven_bench_height_warns()
    {
        // 三级, H = 10 / 10 / 20 → IQR/中位 超 0.25 阈 → 报不齐。
        var lines = new List<BPE.Line>
        {
            L(true, 0, 40), L(false, 10, 30),   // H=10
            L(true, 15, 30), L(false, 25, 20),  // H=10
            L(true, 30, 20), L(false, 40, 0),   // H=20
        };
        var r = BPE.Extract(lines);
        Assert.True(r.Ok);
        Assert.Contains(r.Warnings, w => w.Contains("台阶高不齐"));
    }

    [Fact]
    public void Missing_crest_or_toe_fails_gracefully()
    {
        var onlyCrest = BPE.Extract(new List<BPE.Line> { L(true, 0, 20), L(true, 15, 10) });
        Assert.False(onlyCrest.Ok);
        Assert.Contains("坡顶线和坡底线", onlyCrest.Message);
        Assert.False(BPE.Extract(null).Ok);
        Assert.False(BPE.Extract(new List<BPE.Line>()).Ok);
    }

    [Fact]
    public void Face_pairing_respects_drop_window()
    {
        // Δz 窗口 [2.5,60]: 坡顶下方仅 0.5m 处的坡底不配对 → 找不到坡面 → 降级。
        var r = BPE.Extract(new List<BPE.Line> { L(true, 0, 10), L(false, 5, 9.5) });
        Assert.False(r.Ok);
        Assert.Contains("找不到下方相邻坡底", r.Message);
    }

    [Fact]
    public void Self_pairing_guard_keeps_berm_nonzero()
    {
        // 退化输入: 一条坡顶与坡底(10,*,10)坐标完全重合。平盘宽不能因重合点变成 0——
        // 守卫须跳过重合坡顶, 选真坡顶(15,*,10) → W=5。
        var lines = new List<BPE.Line>
        {
            L(true, 0, 20),    // crest1: 提供坡面 H=10 (下方 toe1)
            L(false, 10, 10),  // toe1
            L(true, 10, 10),   // 与 toe1 完全重合的坡顶(退化) —— 守卫须跳过
            L(true, 15, 10),   // 真坡顶: 距 toe1 为 5 → 平盘宽 W
        };
        var r = BPE.Extract(lines);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(5, r.BermWidth, 3);   // 守卫跳过重合点, 取真坡顶 → W=5(非 0)
    }

    [Fact]
    public void ParseCsv_by_role_and_lineid()
    {
        string csv = "role,lineId,x,y,z\n"
                   + "C,1,0,0,20\nC,1,0,10,20\n"      // 坡顶线 1
                   + "T,1,10,0,10\nT,1,10,10,10\n"    // 坡底线 1
                   + "坡顶,2,15,0,10\n坡顶,2,15,10,10\n"
                   + "坡底,2,25,0,0\n坡底,2,25,10,0\n";
        var lines = BPE.ParseCsv(csv);
        Assert.Equal(4, lines.Count);
        Assert.Equal(2, lines.Count(l => l.IsCrest));
        Assert.Equal(2, lines.Count(l => !l.IsCrest));
        var r = BPE.Extract(lines);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.BenchCount);
        Assert.Equal(10, r.BenchHeight, 3);
        Assert.Equal(5, r.BermWidth, 3);
    }

    [Fact]
    public void BuildReport_has_headline_metrics()
    {
        var r = BPE.Extract(TwoBench());
        var csv = BPE.BuildReport(r, "测试");
        Assert.Contains("台阶级数,2", csv);
        Assert.Contains("台阶高中位(m),10", csv);
        Assert.Contains("坡面采样,坡顶标高", csv);
    }
}
