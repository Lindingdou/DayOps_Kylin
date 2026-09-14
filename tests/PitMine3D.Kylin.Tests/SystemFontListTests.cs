using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Views;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>「字体」下拉的条目表（首项原始 / 常用中文置顶 / 其余排序）回归。</summary>
public class SystemFontListTests
{
    [Fact]
    public void 首项恒为原始且字体名为空()
    {
        var items = SystemFontList.Build(new[] { "Arial" });
        Assert.Equal(SystemFontList.OriginalLabel, items[0].Display);
        Assert.Equal("", items[0].Family);
    }

    [Fact]
    public void 没装的字体不出现在下拉里()
    {
        var items = SystemFontList.Build(new[] { "Arial", "SimSun" });
        Assert.Contains(items, x => x.Family == "SimSun");
        Assert.DoesNotContain(items, x => x.Family == "SimHei");     // 没装 → 不列
        Assert.DoesNotContain(items, x => x.Family == "KaiTi");
    }

    [Fact]
    public void 常用中文字体置顶并显示中文名()
    {
        var items = SystemFontList.Build(new[] { "Arial", "Consolas", "SimHei", "SimSun", "Noto Sans CJK SC" });
        var names = items.Select(x => x.Family).ToList();
        // 中文字体都排在普通西文字体之前
        int lastCjk = new[] { "Noto Sans CJK SC", "SimSun", "SimHei" }.Max(f => names.IndexOf(f));
        int firstLatin = new[] { "Arial", "Consolas" }.Min(f => names.IndexOf(f));
        Assert.True(lastCjk < firstLatin, $"中文字体没置顶: {string.Join(",", names)}");
        Assert.Contains(items, x => x.Display == "宋体 SimSun");
        Assert.Contains(items, x => x.Display == "黑体 SimHei");
        Assert.Contains(items, x => x.Display == "思源黑体 Noto Sans CJK SC");
    }

    [Fact]
    public void 置顶顺序按表不按字母序()
    {
        var items = SystemFontList.Build(new[] { "SimSun", "SimHei", "Microsoft YaHei" });
        var fams = items.Select(x => x.Family).ToList();
        Assert.True(fams.IndexOf("SimSun") < fams.IndexOf("SimHei"));            // 表里 宋体 在 黑体 前
        Assert.True(fams.IndexOf("SimHei") < fams.IndexOf("Microsoft YaHei"));
    }

    [Fact]
    public void 其余字体去重且按名排序()
    {
        var items = SystemFontList.Build(new[] { "Verdana", "Arial", "arial", " Consolas " });
        var rest = items.Skip(1).Select(x => x.Family).ToList();
        Assert.Equal(3, rest.Count);                                  // Arial/arial 去重(大小写不敏感), 空白已裁
        Assert.Equal(rest.OrderBy(x => x, System.StringComparer.CurrentCulture).ToList(), rest);
        Assert.Contains("Consolas", rest);
    }

    [Fact]
    public void 空清单只给首项_不抛()
    {
        Assert.Single(SystemFontList.Build(null));
        Assert.Single(SystemFontList.Build(new List<string>()));
        Assert.Single(SystemFontList.Build(new[] { "", "   " }));
    }
}
