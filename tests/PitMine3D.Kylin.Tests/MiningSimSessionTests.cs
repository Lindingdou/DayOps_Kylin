using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 采剥演示接到 Kylin 块体模型上（§三二五）：判煤口径、可见性只走 <c>SimHidden</c> 不碰
/// <c>DeletedIds</c>、装配的前置条件、收起演示要还原。
/// </summary>
public class MiningSimSessionTests
{
    /// <summary>造一个块体模型：20×6×6 格，带一个分类属性「矿岩类型」(0=岩 1=3-1煤)。</summary>
    private static BlockModelMeta MakeModel(string name, double x0 = 0, double y0 = 0, bool withCoal = true)
    {
        var m = new BlockModelMeta { Name = name, Sx = 10, Sy = 10, Sz = 10 };
        var codes = new List<double>();
        for (int ix = 0; ix < 20; ix++)
            for (int iy = 0; iy < 6; iy++)
                for (int iz = 0; iz < 6; iz++)
                {
                    m.Blocks.Add(new BlockModel.Block { X = x0 + ix * 10 + 5, Y = y0 + iy * 10 + 5, Z = iz * 10 + 5, Size = 10, Grade = 0 });
                    codes.Add(iz < 2 ? 1 : 0);   // 下两层是煤
                }
        if (withCoal)
        {
            m.PropertySchema.Add(new BlockPropertyColumn
            {
                Name = "矿岩类型", IsCategorical = true,
                CategoryLabels = new List<string> { "岩石", "3-1煤" },
            });
            m.Attrs["矿岩类型"] = codes.ToArray();
        }
        return m;
    }

    // ── 判煤 ──────────────────────────────────────────────────
    [Fact]
    public void 判煤_类别名含煤的码算煤()
    {
        var m = MakeModel("采场");
        var codes = MiningSimSession.CoalCodes(m, out string? attr);
        Assert.Equal("矿岩类型", attr);
        Assert.Contains(1, codes);      // 「3-1煤」
        Assert.DoesNotContain(0, codes); // 「岩石」
    }

    [Fact]
    public void 判煤_也认SEAM和COAL()
    {
        var m = new BlockModelMeta { Name = "x" };
        m.PropertySchema.Add(new BlockPropertyColumn
        {
            Name = "lith", IsCategorical = true,
            CategoryLabels = new List<string> { "waste", "Coal Seam A", "OVERBURDEN", "seam-B" },
        });
        var codes = MiningSimSession.CoalCodes(m, out _);
        Assert.Equal(new[] { 1, 3 }, codes.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 判煤_没有分类列时全按岩()
    {
        var m = MakeModel("无分类", withCoal: false);
        var codes = MiningSimSession.CoalCodes(m, out string? attr);
        Assert.Null(attr);
        Assert.Empty(codes);

        // 全按岩 → 采完时"采煤"是 0、"剥离"是全量, 比瞎猜成煤诚实
        var s = new MiningSimSession();
        Assert.True(s.Setup(m, null, null, out _));
        Assert.Equal(0, s.CoalTotal);
        Assert.True(s.WasteTotal > 0);
    }

    // ── 装配 ──────────────────────────────────────────────────
    [Fact]
    public void 装配_没给采场就报原因而不是崩()
    {
        var s = new MiningSimSession();
        Assert.False(s.Setup(null, null, null, out string err));
        Assert.Contains("采场", err);
        Assert.False(s.Ready);
    }

    [Fact]
    public void 装配_采场全被删时报原因()
    {
        var m = MakeModel("空采场");
        for (int i = 0; i < m.Blocks.Count; i++) m.DeletedIds.Add(i);
        var s = new MiningSimSession();
        Assert.False(s.Setup(m, null, null, out string err));
        Assert.Contains("没有可用的块", err);
    }

    [Fact]
    public void 装配_内外排可缺()
    {
        var s = new MiningSimSession();
        Assert.True(s.Setup(MakeModel("采场"), null, null, out _));
        Assert.False(s.HasExternal);
        Assert.False(s.HasInternal);
        Assert.Contains("外排未选", s.StatusLabel);
        Assert.Contains("内排未选", s.StatusLabel);
    }

    [Fact]
    public void 装配_煤岩方量与块体对得上()
    {
        var pit = MakeModel("采场");
        var s = new MiningSimSession();
        Assert.True(s.Setup(pit, null, null, out _));
        double cell = 10.0 * 10 * 10;
        int coalCells = pit.Blocks.Count / 3;        // 6 层里下 2 层是煤
        Assert.Equal(coalCells * cell, s.CoalTotal, 6);
        Assert.Equal((pit.Blocks.Count - coalCells) * cell, s.WasteTotal, 6);
    }

    [Fact]
    public void 装配_已删的块不参与演示()
    {
        var pit = MakeModel("采场");
        int before = pit.Blocks.Count;
        for (int i = 0; i < 100; i++) pit.DeletedIds.Add(i);
        var s = new MiningSimSession();
        Assert.True(s.Setup(pit, null, null, out _));
        double cell = 10.0 * 10 * 10;
        Assert.Equal((before - 100) * cell, s.CoalTotal + s.WasteTotal, 6);
    }

    // ── 驱动可见性 ────────────────────────────────────────────
    [Fact]
    public void 推进只改SimHidden不碰DeletedIds()
    {
        var pit = MakeModel("采场");
        var s = new MiningSimSession();
        Assert.True(s.Setup(pit, null, null, out _));

        s.Step(0.5);
        Assert.NotEmpty(pit.SimHidden);          // 采掉的块藏起来了
        Assert.Empty(pit.DeletedIds);            // 但绝不能写进"删除块体"
        Assert.All(pit.SimHidden, i => Assert.False(pit.IsCellVisible(i)));
    }

    [Fact]
    public void 进度0全可见_进度1全藏起()
    {
        var pit = MakeModel("采场");
        var s = new MiningSimSession();
        Assert.True(s.Setup(pit, null, null, out _));

        s.Step(0);
        Assert.Empty(pit.SimHidden);
        Assert.Equal(pit.Blocks.Count, Enumerable.Range(0, pit.Blocks.Count).Count(pit.IsCellVisible));

        s.Step(1);
        Assert.Equal(pit.Blocks.Count, pit.SimHidden.Count);
        Assert.Equal(0, Enumerable.Range(0, pit.Blocks.Count).Count(pit.IsCellVisible));
    }

    [Fact]
    public void 排土反过来_起步全藏末了露出来()
    {
        var pit = MakeModel("采场");
        var inner = MakeModel("内排", withCoal: false);   // 与采场同址 = 采空区回填
        var s = new MiningSimSession();
        Assert.True(s.Setup(pit, null, inner, out _));

        s.Step(0.0);
        Assert.Equal(inner.Blocks.Count, inner.SimHidden.Count);   // 还没采空, 内排一块不露

        s.Step(1.0);
        Assert.True(inner.SimHidden.Count < inner.Blocks.Count, "跑到头内排还是一块没露");
    }

    [Fact]
    public void 收起演示要把模型还原()
    {
        var pit = MakeModel("采场");
        var inner = MakeModel("内排", withCoal: false);
        var s = new MiningSimSession();
        Assert.True(s.Setup(pit, null, inner, out _));
        s.Step(0.5);
        Assert.NotEmpty(pit.SimHidden);

        s.Reset();
        Assert.Empty(pit.SimHidden);
        Assert.Empty(inner.SimHidden);
        Assert.False(s.Ready);
        Assert.Equal(pit.Blocks.Count, Enumerable.Range(0, pit.Blocks.Count).Count(pit.IsCellVisible));
    }

    [Fact]
    public void 重新装配前先还原上一次()
    {
        var a = MakeModel("采场A");
        var b = MakeModel("采场B", x0: 1000);
        var s = new MiningSimSession();
        s.Setup(a, null, null, out _);
        s.Step(0.6);
        Assert.NotEmpty(a.SimHidden);

        s.Setup(b, null, null, out _);   // 换一个采场
        Assert.Empty(a.SimHidden);       // 上一个不该留着半采的样子
    }

    [Fact]
    public void 推进方向描述带方位角和方向词()
    {
        var pit = MakeModel("采场");
        var s = new MiningSimSession();
        Assert.True(s.Setup(pit, null, null, out _));
        Assert.Contains("方位角", s.AdvanceLabel);
        Assert.Contains("采掘带", s.AdvanceLabel);
        Assert.Matches("向(北|东北|东|东南|南|西南|西|西北)", s.AdvanceLabel);
    }
}
