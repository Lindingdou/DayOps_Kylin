using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>智能助手引擎（菜单树引导）回归。</summary>
public class AssistantEngineTests
{
    [Fact]
    public void Welcome_shows_main_menu()
    {
        var a = new AssistantEngine();
        var r = a.Current();
        Assert.Contains("智能助手", r.Content);
        Assert.Equal(5, r.Options.Count);                          // 绘制/编辑/标注/视图/帮助
        Assert.Contains(r.Options, o => o.Label == "绘制图形");
        Assert.All(r.Options, o => Assert.Null(o.Command));         // 主菜单项无直接命令
    }

    [Fact]
    public void Draw_menu_options_carry_commands()
    {
        var a = new AssistantEngine();
        var draw = a.Select(a.Current().Options.First(o => o.Label == "绘制图形"));
        Assert.Equal("draw_menu", a.CurrentNode);
        Assert.Equal("CIRCLE", draw.Options.First(o => o.Label == "圆").Command);
        Assert.Equal("PLINE", draw.Options.First(o => o.Label == "多段线").Command);
        Assert.Contains(draw.Options, o => o.Label == "返回主菜单" && o.Next == "main_menu");
    }

    [Fact]
    public void Edit_menu_has_eight_commands()
    {
        var a = new AssistantEngine();
        var edit = a.Select(a.Current().Options.First(o => o.Label == "编辑修改"));
        var cmds = edit.Options.Where(o => o.Command != null).Select(o => o.Command).ToList();
        Assert.Equal(8, cmds.Count);
        Assert.Contains("MIRROR", cmds);
        Assert.Contains("TRIM", cmds);
    }

    [Fact]
    public void Back_returns_to_main()
    {
        var a = new AssistantEngine();
        a.Select(a.Current().Options.First(o => o.Label == "视图操作"));
        var main = a.Select(new AssistantEngine.Option("返回主菜单", null, "main_menu"));
        Assert.Equal("main_menu", a.CurrentNode);
        Assert.Equal(5, main.Options.Count);
    }

    [Fact]
    public void Help_shows_guide_and_back()
    {
        var a = new AssistantEngine();
        var help = a.Select(a.Current().Options.First(o => o.Label == "查看使用帮助"));
        Assert.Contains("使用指南", help.Content);
        Assert.Single(help.Options);
        Assert.Equal("main_menu", help.Options[0].Next);
    }

    [Fact]
    public void Free_text_command_executes()
    {
        var a = new AssistantEngine();
        var r = a.HandleText("CIRCLE", cmd => cmd == "CIRCLE");
        Assert.Contains("已为您执行", r.Content);
        Assert.Equal("main_menu", a.CurrentNode);
    }

    [Fact]
    public void Free_text_unknown_falls_back()
    {
        var a = new AssistantEngine();
        var r = a.HandleText("你好呀", _ => false);
        Assert.Contains("抱歉", r.Content);
        Assert.Equal(5, r.Options.Count);                          // 兜底给主菜单
    }

    [Fact]
    public void Reset_returns_to_welcome()
    {
        var a = new AssistantEngine();
        a.Select(a.Current().Options.First(o => o.Label == "编辑修改"));
        a.Reset();
        Assert.Equal("welcome", a.CurrentNode);
    }
}
