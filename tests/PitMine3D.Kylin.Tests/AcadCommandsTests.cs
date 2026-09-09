using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>AutoCAD 命令名对照表：解析、上下文分流、空格提交规则。</summary>
public class AcadCommandsTests
{
    [Theory]
    [InlineData("L", "LINE")]
    [InlineData("l", "LINE")]
    [InlineData("PL", "PLINE")]
    [InlineData("REC", "RECTANG")]
    [InlineData("E", "ERASE")]
    [InlineData("CO", "COPY")]
    [InlineData("TR", "TRIM")]
    [InlineData("EX", "EXTEND")]
    [InlineData("Z", "ZOOM")]
    [InlineData("RE", "REGEN")]
    [InlineData("LA", "LAYER")]
    [InlineData("DLI", "DIMLINEAR")]
    [InlineData("DAL", "DIMALIGNED")]
    [InlineData("DRA", "DIMRADIUS")]
    [InlineData("MT", "多行文字")]
    [InlineData("TOP", "俯视")]
    [InlineData("SWISO", "西南等轴测")]
    [InlineData("UNI", "布尔-并集")]
    public void Acad_alias_maps_to_existing_command(string typed, string expected)
        => Assert.Equal(expected, AcadCommands.Resolve(typed));

    [Fact]
    public void Unknown_and_chinese_pass_through_unchanged()
    {
        Assert.Equal("距离", AcadCommands.Resolve("距离"));
        Assert.Equal("平盘宽度识别", AcadCommands.Resolve("平盘宽度识别"));
        Assert.Equal("ZZZNOTACOMMAND", AcadCommands.Resolve("ZZZNOTACOMMAND"));
    }

    [Fact]
    public void Arguments_after_the_command_word_are_preserved()
    {
        Assert.Equal("图案填充 45 2", AcadCommands.Resolve("H 45 2"));
        Assert.Equal("POLYGON 6", AcadCommands.Resolve("POL 6"));
        Assert.Equal("圆柱 5 12", AcadCommands.Resolve("CYL 5 12"));
    }

    /// <summary>
    /// L / P / CP 在 AutoCAD 里一词两义：命令提示符下是命令名(直线/平移/复制)，
    /// 「选择对象」提示下是选择选项(上次画的/上次选择集/交叉多边形)。
    /// </summary>
    [Fact]
    public void Selection_stage_reinterprets_autocad_selection_keywords()
    {
        Assert.Equal("LINE", AcadCommands.Resolve("L", selectingObjects: false));
        Assert.Equal("LAST", AcadCommands.Resolve("L", selectingObjects: true));

        Assert.Equal("平移", AcadCommands.Resolve("P", selectingObjects: false));
        Assert.Equal("PREVIOUS", AcadCommands.Resolve("P", selectingObjects: true));

        Assert.Equal("COPY", AcadCommands.Resolve("CP", selectingObjects: false));
        Assert.Equal("CP", AcadCommands.Resolve("CP", selectingObjects: true));

        Assert.Equal("WP", AcadCommands.Resolve("WP", selectingObjects: true));
        Assert.Equal("ALL", AcadCommands.Resolve("ALL", selectingObjects: true));
    }

    [Fact]
    public void Table_has_no_duplicate_command_names()
    {
        var dup = AcadCommands.Table
            .GroupBy(e => e.Acad, System.StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, "重复的 AutoCAD 命令名: " + string.Join(", ", dup));
    }

    /// <summary>
    /// 解析是单趟的：Target 本身不能再被改写，否则 "A→B→C" 里的 C 永远到不了。
    /// 这条把「以后往表里加链式别名」的隐患挡在测试里。
    /// </summary>
    [Fact]
    public void Resolution_is_a_single_pass_fixed_point()
    {
        foreach (var en in AcadCommands.Table)
        {
            string once = AcadCommands.Resolve(en.Acad);
            Assert.Equal(once, AcadCommands.Resolve(once));
        }
    }

    [Fact]
    public void Every_entry_is_reported_as_known()
    {
        foreach (var en in AcadCommands.Table) Assert.True(AcadCommands.IsAcadCommand(en.Acad), en.Acad);
        Assert.False(AcadCommands.IsAcadCommand("ARRAY"));      // 系统里没有的 AutoCAD 命令不该进表
        Assert.False(AcadCommands.IsAcadCommand("FILLET"));
        Assert.False(AcadCommands.IsAcadCommand(""));
    }

    /// <summary>空格=回车；但中文(输入法)与"参数只能写同一行"的命令不能被空格劫持。</summary>
    [Theory]
    [InlineData("", true)]          // 空行 + 空格 = 重复上次命令
    [InlineData("L", true)]
    [InlineData("REC", true)]
    [InlineData("DIMLINEAR", true)]
    [InlineData("H", true)]         // 图案填充改为逐项问参数 → 空格照常执行(同 AutoCAD)
    [InlineData("POL", true)]       // 正多边形先问侧面数
    [InlineData("SOR", true)]       // SOR 去噪先问 k / σ
    [InlineData("QSELECT", false)]  // 条件表达式只能写同一行, 空格留给它
    [InlineData("图案填充", false)] // 中文：空格留给输入法
    [InlineData("L 5", false)]      // 行内已有空格
    public void Space_submits_only_when_safe(string typed, bool expected)
        => Assert.Equal(expected, AcadCommands.SpaceSubmits(typed));

    [Fact]
    public void Names_cover_the_classic_pgp_shortcuts()
    {
        var names = new HashSet<string>(AcadCommands.Names, System.StringComparer.OrdinalIgnoreCase);
        foreach (var s in new[] { "L", "PL", "C", "A", "REC", "PO", "E", "M", "CO", "MI", "O",
                                  "RO", "SC", "TR", "EX", "BR", "X", "Z", "P", "RE", "LA",
                                  "DLI", "DAL", "DRA", "DI", "AA", "PR", "OP", "H", "G" })
            Assert.True(names.Contains(s), "缺 acad.pgp 缩写: " + s);
    }
}
