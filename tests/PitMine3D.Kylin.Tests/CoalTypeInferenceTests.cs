using System.Collections.Generic;
using PitMine3D.Kylin.Data;
using Xunit;
using CR = PitMine3D.Kylin.Data.CoalTypeInference.ClassRange;
using GR = PitMine3D.Kylin.Data.CoalTypeInference.GradeRule;

namespace PitMine3D.Kylin.Tests;

/// <summary>GB/T 5751 煤类反推 + 分级区间匹配回归（忠实原 CoalReferenceService.ResolveCoalType/FindLevel 纯逻辑）。</summary>
public class CoalTypeInferenceTests
{
    // 简化的 GB/T 5751 式区间(仅测算法, 非真国标表): Vdaf 主分 + G 副分 + 一条 Y 门。
    private static List<CR> Ranges() => new()
    {
        new("WY", 0, 10, null, null, null, null),      // 无烟煤: Vdaf<10
        new("PM", 10, 20, null, 5, null, null),        // 贫煤:   Vdaf 10-20, G<5
        new("SM", 10, 20, 20, 65, null, null),         // 瘦煤:   Vdaf 10-20, G 20-65
        new("JM", 20, 28, 65, null, 7, null),          // 焦煤:   Vdaf 20-28, G≥65, Y≥7
        new("QM", 28, 37, null, null, null, null),     // 气煤:   Vdaf 28-37
        new("CY", 37, null, null, null, null, null),   // 长焰煤: Vdaf≥37
    };

    [Fact]
    public void ResolveCoalType_vdaf_only_and_boundaries()
    {
        var rg = Ranges();
        Assert.Equal("WY", CoalTypeInference.ResolveCoalType(5, null, null, rg));     // Vdaf 5 → 无烟煤
        Assert.Equal("QM", CoalTypeInference.ResolveCoalType(32, null, null, rg));    // Vdaf 32 → 气煤
        Assert.Equal("CY", CoalTypeInference.ResolveCoalType(45, null, null, rg));    // Vdaf 45 → 长焰煤
        // 半开 [min,max): Vdaf=10 出 WY(max10 排他), 入 PM(min10 含); G 未提供跳过 → 首个 Vdaf∈[10,20) 命中 = PM。
        Assert.Equal("PM", CoalTypeInference.ResolveCoalType(10, null, null, rg));
        // Vdaf=37 出 QM(max37 排他)入 CY(min37 含)。
        Assert.Equal("CY", CoalTypeInference.ResolveCoalType(37, null, null, rg));
    }

    [Fact]
    public void ResolveCoalType_g_and_y_dimensions()
    {
        var rg = Ranges();
        Assert.Equal("PM", CoalTypeInference.ResolveCoalType(15, 3, null, rg));       // Vdaf15 G3 → 贫煤(G<5)
        Assert.Equal("SM", CoalTypeInference.ResolveCoalType(15, 40, null, rg));      // Vdaf15 G40 → 瘦煤(G 20-65)
        Assert.Equal("JM", CoalTypeInference.ResolveCoalType(25, 70, 10, rg));        // Vdaf25 G70 Y10 → 焦煤
        // G=10 落在 PM(G<5)与 SM(20-65)之间空档 → Vdaf 10-20 无区间接纳 → null。
        Assert.Null(CoalTypeInference.ResolveCoalType(15, 10, null, rg));
        // Vdaf25 G70 但 Y=3 < 焦煤 Y 门(≥7) → 焦煤不命中, 无其它 Vdaf20-28 区间 → null。
        Assert.Null(CoalTypeInference.ResolveCoalType(25, 70, 3, rg));
    }

    [Fact]
    public void ResolveCoalType_null_vdaf_or_empty_ranges()
    {
        Assert.Null(CoalTypeInference.ResolveCoalType(null, 50, 10, Ranges()));       // 缺 Vdaf → null
        Assert.Null(CoalTypeInference.ResolveCoalType(15, 3, null, new List<CR>()));  // 空区间表 → null
    }

    [Fact]
    public void ResolveCoalType_first_match_wins()
    {
        // 两个重叠区间, 前者应胜(忠实原 foreach 首命中)。
        var rg = new List<CR> { new("A", 0, 20, null, null, null, null), new("B", 10, 30, null, null, null, null) };
        Assert.Equal("A", CoalTypeInference.ResolveCoalType(15, null, null, rg));
    }

    [Fact]
    public void FindGradeLevel_half_open_intervals()
    {
        var rules = new List<GR>
        {
            new("特低灰", null, 10), new("低灰", 10, 16), new("中灰", 16, 29), new("富灰", 29, 40), new("高灰", 40, null),
        };
        Assert.Equal("特低灰", CoalTypeInference.FindGradeLevel(8, rules));
        Assert.Equal("低灰", CoalTypeInference.FindGradeLevel(10, rules));    // 边界 10 → 低灰([10,16))
        Assert.Equal("中灰", CoalTypeInference.FindGradeLevel(16, rules));
        Assert.Equal("高灰", CoalTypeInference.FindGradeLevel(55, rules));    // ≥40 无上界
        Assert.Null(CoalTypeInference.FindGradeLevel(null, rules));
    }

    [Fact]
    public void InferConsistency_rate_and_inconclusive()
    {
        var rg = Ranges();
        CoalSample S(long id, double? vdaf, double? g, double? y, string? labeled) =>
            new(id, "H" + id, "4-1", id, id, 100, null, null, null, null, null, null, vdaf, null,
                null, null, null, g, y, labeled);
        var samples = new List<CoalSample>
        {
            S(1, 5, null, null, "WY"),      // 反推 WY == 标注 WY → 一致
            S(2, 15, 3, null, "PM"),        // 反推 PM == 标注 PM → 一致
            S(3, 15, 40, null, "PM"),       // 反推 SM ≠ 标注 PM → 不一致
            S(4, 32, null, null, null),     // 反推 QM 但无标注 → 无法判定
            S(5, null, null, null, "WY"),   // 缺 Vdaf → 反推 null → 无法判定
        };
        var r = CoalTypeInference.InferConsistency(samples, rg);
        Assert.Equal(3, r.Total);                       // 1,2,3 有反推且有标注
        Assert.Equal(2, r.Consistent);                  // 1,2 一致
        Assert.Equal(2, r.Inconclusive);                // 4,5 无法判定
        Assert.Equal(200.0 / 3, r.RatePct, 4);          // 2/3 = 66.67%
        Assert.Equal("SM", System.Linq.Enumerable.First(r.Rows, x => x.Id == 3).Inferred);
        Assert.False(System.Linq.Enumerable.First(r.Rows, x => x.Id == 3).Match);
    }

    [Fact]
    public void InferConsistency_uses_clean_vdaf_when_requested()
    {
        var rg = Ranges();
        // VdafRaw=5(WY) 而 VdafClean=32(QM): useClean 切换应改变反推。
        var s = new CoalSample(1, "H1", "4-1", 0, 0, 100, null, null, null, null, null, null,
            /*VdafRaw*/5, /*VdafClean*/32, null, null, null, null, null, "QM");
        var raw = CoalTypeInference.InferConsistency(new[] { s }, rg, useClean: false);
        var cln = CoalTypeInference.InferConsistency(new[] { s }, rg, useClean: true);
        Assert.Equal("WY", System.Linq.Enumerable.First(raw.Rows).Inferred);
        Assert.Equal("QM", System.Linq.Enumerable.First(cln.Rows).Inferred);
        Assert.Equal(100.0, cln.RatePct, 4);            // 浮煤反推 QM == 标注 QM
    }
}
