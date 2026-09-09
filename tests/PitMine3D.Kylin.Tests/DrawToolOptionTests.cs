using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>绘制中的命令行选项关键字（AutoCAD 的 “指定下一点或 [闭合(C)/放弃(U)]”）。</summary>
[Collection("TextGeometry")]
public class DrawToolOptionTests
{
    private static PolylineTool Poly(params (double x, double y)[] pts)
    {
        var t = new PolylineTool();
        foreach (var p in pts) t.AddPoint(p.x, p.y);
        return t;
    }

    // ── 多段线 闭合(C) ────────────────────────────────────────────────────
    [Fact]
    public void Pline_close_makes_a_closed_polyline_and_ends_the_command()
    {
        var t = Poly((0, 0), (10, 0), (10, 10));
        var r = t.Invoke("C");

        Assert.True(r.Handled);
        Assert.True(r.EndsCommand);
        var e = Assert.IsType<PolylineEntity>(r.Entity);
        Assert.True(e.Closed);
        Assert.Equal(3, e.Points.Count);
        Assert.Null(t.Finish());               // 已收笔, 工具里不该还留着点
    }

    [Theory]
    [InlineData("C")]
    [InlineData("c")]
    [InlineData("闭合")]
    public void Pline_close_accepts_letter_and_chinese(string typed)
    {
        var r = Poly((0, 0), (1, 0), (1, 1)).Invoke(typed);
        Assert.True(r.Handled);
        Assert.True(Assert.IsType<PolylineEntity>(r.Entity).Closed);
    }

    /// <summary>2 点闭合会退化成一条来回线，AutoCAD 也要 3 点起。给提示而不是画出退化图元。</summary>
    [Fact]
    public void Pline_close_needs_three_points()
    {
        var r = Poly((0, 0), (10, 0)).Invoke("C");
        Assert.True(r.Handled);
        Assert.Null(r.Entity);
        Assert.False(r.EndsCommand);
        Assert.Contains("3 点", r.Message);
    }

    // ── 多段线 放弃(U) ────────────────────────────────────────────────────
    [Fact]
    public void Pline_undo_drops_the_last_point_and_keeps_drawing()
    {
        var t = Poly((0, 0), (10, 0), (99, 99));
        var r = t.Invoke("U");
        Assert.True(r.Handled);
        Assert.False(r.EndsCommand);

        t.AddPoint(10, 10);
        var e = Assert.IsType<PolylineEntity>(t.Finish());
        Assert.Equal(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) }, e.Points);   // 99,99 已被撤掉
    }

    [Fact]
    public void Pline_undo_with_no_points_just_says_so()
    {
        var r = new PolylineTool().Invoke("U");
        Assert.True(r.Handled);
        Assert.Null(r.Entity);
        Assert.Contains("没有可放弃", r.Message);
    }

    // ── 提示里的选项串 ────────────────────────────────────────────────────
    [Fact]
    public void Pline_prompt_lists_the_options_as_they_become_available()
    {
        var t = new PolylineTool();
        Assert.DoesNotContain("[", t.Prompt);                 // 还没起点: 无选项

        t.AddPoint(0, 0);
        Assert.Contains("放弃(U)", t.Prompt);
        Assert.DoesNotContain("闭合(C)", t.Prompt);           // 1 点不给闭合

        t.AddPoint(10, 0);
        Assert.Contains("闭合(C)", t.Prompt);
        Assert.Contains("放弃(U)", t.Prompt);
    }

    [Fact]
    public void Unknown_keyword_is_not_claimed_so_it_stays_a_command()
    {
        Assert.False(Poly((0, 0), (1, 1)).Invoke("ZOOM").Handled);
        Assert.False(Poly((0, 0), (1, 1)).Invoke("3,4").Handled);
    }

    // ── 圆的 3P / 2P / T ──────────────────────────────────────────────────
    [Theory]
    [InlineData("3P", "CIRCLE3P")]
    [InlineData("2p", "CIRCLE2P")]
    [InlineData("T", "TTR")]
    public void Circle_options_switch_to_the_matching_command(string typed, string expected)
    {
        var r = new CircleTool().Invoke(typed);
        Assert.True(r.Handled);
        Assert.Equal(expected, r.SwitchTo);
    }

    /// <summary>圆心已定再切模式等于丢掉已点的点，AutoCAD 那时也不再给这些选项。</summary>
    [Fact]
    public void Circle_options_disappear_once_the_centre_is_picked()
    {
        var t = new CircleTool();
        Assert.Contains("三点(3P)", t.Prompt);
        t.AddPoint(0, 0);
        Assert.DoesNotContain("[", t.Prompt);
        Assert.False(t.Invoke("3P").Handled);
    }

    [Fact]
    public void Tools_without_options_claim_nothing()
    {
        Assert.Empty(new LineTool().Options);
        Assert.False(new LineTool().Invoke("C").Handled);
        Assert.Equal("", new LineTool().OptionHint());
    }
}
