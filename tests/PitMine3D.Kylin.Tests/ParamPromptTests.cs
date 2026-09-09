using PitMine3D.Kylin.Views.Modeling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>命令行参数问答的纯逻辑：提示行渲染 + 键入值归一化。</summary>
public class ParamPromptTests
{
    private static PromptDialog.Field Num(string def = "10", string? unit = null)
        => new("k", "最大间距", def, unit);

    private static PromptDialog.Field Choice(params string[] choices)
        => new("side", "保留侧", choices[0], null, null, false, choices);

    private static PromptDialog.Field Flag(string def = "否")
        => new("hi", "高容错", def, null, null, false, null, true);

    // ── 提示行 ────────────────────────────────────────────────────────────
    [Fact]
    public void Numeric_prompt_shows_unit_and_default()
        => Assert.Equal("最大间距(m) <10>", ParamPrompt.PromptText(Num("10", "m")));

    [Fact]
    public void Choice_prompt_lists_options()
        => Assert.Equal("保留侧 [圈内/圈外] <圈内>", ParamPrompt.PromptText(Choice("圈内", "圈外")));

    [Fact]
    public void Bool_prompt_is_yes_no()
        => Assert.Equal("高容错 [是/否] <否>", ParamPrompt.PromptText(Flag()));

    // ── 归一化 ────────────────────────────────────────────────────────────
    [Fact]
    public void Empty_input_takes_the_default()
    {
        Assert.True(ParamPrompt.TryNormalize(Num("10"), "", out string v, out _));
        Assert.Equal("10", v);
        Assert.True(ParamPrompt.TryNormalize(Num("10"), "   ", out v, out _));
        Assert.Equal("10", v);
    }

    [Fact]
    public void Numeric_rejects_non_numbers_with_a_readable_reason()
    {
        Assert.False(ParamPrompt.TryNormalize(Num(), "abc", out _, out string err));
        Assert.Contains("最大间距", err);
        Assert.True(ParamPrompt.TryNormalize(Num(), "2.5", out string v, out _));
        Assert.Equal("2.5", v);
        Assert.True(ParamPrompt.TryNormalize(Num(), "-3", out v, out _));   // 负值合法(如 圆弧SER 半径取另一侧)
        Assert.Equal("-3", v);
    }

    /// <summary>选项可以打全名、序号、唯一前缀 —— 中文选项敲序号最快。</summary>
    [Theory]
    [InlineData("圈外", "圈外")]
    [InlineData("圈内", "圈内")]
    [InlineData("1", "圈内")]
    [InlineData("2", "圈外")]
    public void Choice_accepts_full_name_and_index(string typed, string expected)
    {
        Assert.True(ParamPrompt.TryNormalize(Choice("圈内", "圈外"), typed, out string v, out _));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Choice_accepts_a_unique_prefix_and_rejects_an_ambiguous_one()
    {
        var f = Choice("刀下方", "刀上方");
        Assert.True(ParamPrompt.TryNormalize(f, "刀下", out string v, out _));
        Assert.Equal("刀下方", v);

        Assert.False(ParamPrompt.TryNormalize(f, "刀", out _, out string err));
        Assert.Contains("不唯一", err);
    }

    [Fact]
    public void Choice_out_of_range_index_is_rejected()
    {
        Assert.False(ParamPrompt.TryNormalize(Choice("圈内", "圈外"), "3", out _, out string err));
        Assert.Contains("序号 1~2", err);
    }

    [Theory]
    [InlineData("y", "是")]
    [InlineData("Y", "是")]
    [InlineData("是", "是")]
    [InlineData("1", "是")]
    [InlineData("true", "是")]
    [InlineData("n", "否")]
    [InlineData("否", "否")]
    [InlineData("0", "否")]
    public void Bool_accepts_yes_no_in_several_spellings(string typed, string expected)
    {
        Assert.True(ParamPrompt.TryNormalize(Flag(), typed, out string v, out _));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Bool_rejects_nonsense()
    {
        Assert.False(ParamPrompt.TryNormalize(Flag(), "maybe", out _, out string err));
        Assert.Contains("是/否", err);
    }

    /// <summary>对话框里布尔默认写法五花八门(true/1/是)，命令行上统一显示成 是/否。</summary>
    [Theory]
    [InlineData("true", "是")]
    [InlineData("1", "是")]
    [InlineData("是", "是")]
    [InlineData("否", "否")]
    [InlineData("", "否")]
    public void Bool_default_is_normalised(string def, string expected)
        => Assert.Equal(expected, ParamPrompt.DefaultOf(Flag(def)));

    [Fact]
    public void Free_text_field_takes_anything()
    {
        var f = new PromptDialog.Field("name", "名称", "面1", null, null, false);
        Assert.True(ParamPrompt.TryNormalize(f, "台阶面 A", out string v, out _));
        Assert.Equal("台阶面 A", v);
    }

    [Fact]
    public void Inline_args_split_on_space_and_comma()
    {
        Assert.Equal(new[] { "45", "2" }, ParamPrompt.SplitArgs(" 45 2"));
        Assert.Equal(new[] { "45", "2" }, ParamPrompt.SplitArgs("45,2"));
        Assert.Empty(ParamPrompt.SplitArgs(""));
    }

    /// <summary>取值对象与来源无关：对话框 / 命令行 / 自检默认，拿到的都是同一种。</summary>
    [Fact]
    public void PromptValues_reads_typed_values()
    {
        var v = PromptValues.From(new System.Collections.Generic.Dictionary<string, string>
        { ["d"] = "2.5", ["n"] = "7", ["s"] = "圈内", ["b"] = "是" });
        Assert.Equal(2.5, v.D("d"), 6);
        Assert.Equal(7, v.I("n"));
        Assert.Equal("圈内", v.S("s"));
        Assert.True(v.B("b"));
        Assert.Equal(3.0, v.D("missing", 3.0), 6);
    }
}
