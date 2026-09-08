using System;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// CSV 导入表格规范：模板、类型识别、依赖顺序。
/// 规格此前散在 Ribbon 提示串、模板表、各 Import* 函数三处，必然走样
/// （实测爆破记录/设备型号两种压根没有模板）。这里钉住"以 ImportSpec 为唯一来源"。
/// </summary>
public class ImportSpecTests
{
    [Fact]
    public void 每种导入都有模板且必填列都给了示例()
    {
        Assert.NotEmpty(ImportSpec.All);
        foreach (var e in ImportSpec.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Table), $"{e.Key} 没写目标表");
            Assert.False(string.IsNullOrWhiteSpace(e.UpsertKey), $"{e.Key} 没写去重键");
            Assert.NotEmpty(e.Required_);
            foreach (var c in e.Required_)
                Assert.False(string.IsNullOrWhiteSpace(c.Example),
                    $"{e.Key}.{c.Name} 是必填列, 模板里必须给示例值, 否则模板填了也导不进");
        }
    }

    [Fact]
    public void 模板能被自己的CSV解析器读回且含全部必填列()
    {
        foreach (var e in ImportSpec.All)
        {
            var rows = GeoDataQueries.ParseCsv(e.Template());
            Assert.True(rows.Count == 1, $"{e.Key} 模板应恰好一行示例, 实际 {rows.Count} 行");
            foreach (var c in e.Required_)
                Assert.True(rows[0].ContainsKey(c.Name), $"{e.Key} 模板缺必填列 {c.Name}");
        }
    }

    [Fact]
    public void 补上了此前缺模板的两种()
    {
        // 这两种 Ribbon 上有导入入口, 但旧的模板表里没有, 用户拿不到模板
        Assert.NotNull(ImportSpec.Find("爆破记录"));
        Assert.NotNull(ImportSpec.Find("设备型号"));
    }

    [Fact]
    public void 列名不重复()
    {
        foreach (var e in ImportSpec.All)
        {
            var dup = e.Cols.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dup.Count == 0, $"{e.Key} 有重复列: {string.Join(",", dup)}");
        }
    }

    [Theory]
    [InlineData("煤质化验_2025.csv", "煤质化验")]
    [InlineData("设备台账.csv", "设备台账")]
    [InlineData("2025年月度KPI汇总.csv", "月度KPI")]
    [InlineData("导出_爆破记录_1月.txt", "爆破记录")]
    [InlineData("边坡设计 (副本).csv", "边坡设计")]
    public void 按文件名认类型(string file, string expect)
        => Assert.Equal(expect, ImportSpec.FromFileName(file)?.Key);

    [Fact]
    public void 认不出类型时返回空而不是乱猜()
    {
        Assert.Null(ImportSpec.FromFileName("data.csv"));
        Assert.Null(ImportSpec.FromFileName("导出结果.csv"));
        Assert.Null(ImportSpec.FromFileName(""));
    }

    [Fact]
    public void 长类型名优先_月度计划不会被计划类的短名抢走()
    {
        // 「月度计划」与「月度产能」「月度KPI」前缀相同, 认错就会导进另一张表
        Assert.Equal("月度计划", ImportSpec.FromFileName("月度计划.csv")?.Key);
        Assert.Equal("月度产能", ImportSpec.FromFileName("月度产能.csv")?.Key);
        Assert.Equal("月度KPI", ImportSpec.FromFileName("月度KPI.csv")?.Key);
    }

    [Fact]
    public void 依赖顺序_设备型号先于台账_台账先于按设备统计的几种()
    {
        int Pos(string k) => Array.FindIndex(ImportSpec.All, e => e.Key == k);
        int model = Pos("设备型号"), ledger = Pos("设备台账");
        Assert.True(model < ledger, "设备台账要引用型号, 型号必须先导");
        foreach (var k in new[] { "生产记录", "月度产能", "月度KPI", "故障记录" })
            Assert.True(ledger < Pos(k), $"{k} 按 equipment_id 落到设备上, 台账必须先导");
    }

    [Fact]
    public void 规范文本把必填标出来且列全了每一种()
    {
        string doc = ImportSpec.Describe();
        foreach (var e in ImportSpec.All)
        {
            Assert.Contains(e.Key, doc);
            Assert.Contains(e.Table, doc);
            Assert.Contains(e.UpsertKey, doc);
        }
        Assert.Contains("去重键", doc);
        Assert.Contains("必填", doc);
    }
}
