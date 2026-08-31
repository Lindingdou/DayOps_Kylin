using System;
using PitMine3D.Kylin.Cad;
using Xunit;
using B = PitMine3D.Kylin.Cad.BlockModel.Block;

namespace PitMine3D.Kylin.Tests;

/// <summary>块体表达式筛选(BlockExpression)回归 —— 忠实原 ExpressionEngine「表达式删单元」用途。</summary>
public class BlockExpressionTests
{
    private static B Blk(double x, double y, double z, double grade, double size = 1)
        => new B { X = x, Y = y, Z = z, Grade = grade, Size = size };

    [Fact]
    public void Simple_comparison()
    {
        var f = BlockExpression.Compile("Grade > 5");
        Assert.True(f(Blk(0, 0, 0, 8)));
        Assert.False(f(Blk(0, 0, 0, 3)));
        Assert.False(f(Blk(0, 0, 0, 5)));   // 严格 >
    }

    [Fact]
    public void And_combines_conditions()
    {
        var f = BlockExpression.Compile("Grade > 5 AND Z < 100");
        Assert.True(f(Blk(0, 0, 50, 8)));
        Assert.False(f(Blk(0, 0, 150, 8)));   // Z 不满足
        Assert.False(f(Blk(0, 0, 50, 3)));    // Grade 不满足
    }

    [Fact]
    public void Or_and_precedence()
    {
        // AND 高于 OR: "a OR b AND c" = a OR (b AND c)
        var f = BlockExpression.Compile("Grade > 100 OR Z < 10 AND X > 0");
        Assert.True(f(Blk(5, 0, 5, 1)));      // 右: Z<10 且 X>0
        Assert.False(f(Blk(-5, 0, 5, 1)));    // 右 X 不满足, 左 Grade 不满足
        Assert.True(f(Blk(0, 0, 0, 200)));    // 左满足
    }

    [Fact]
    public void Not_and_parens()
    {
        var f = BlockExpression.Compile("NOT (Grade > 5)");
        Assert.True(f(Blk(0, 0, 0, 3)));
        Assert.False(f(Blk(0, 0, 0, 8)));
        // 括号改变结合: "(a OR b) AND c"
        var g = BlockExpression.Compile("(Grade > 5 OR Z < 0) AND X > 10");
        Assert.True(g(Blk(20, 0, 0, 8)));
        Assert.False(g(Blk(5, 0, 0, 8)));     // X 不满足
    }

    [Fact]
    public void Arithmetic_in_conditions()
    {
        // 品位×尺寸 (粗略金属量代理) > 阈
        var f = BlockExpression.Compile("Grade * Size > 10");
        Assert.True(f(Blk(0, 0, 0, 4, 3)));   // 12 > 10
        Assert.False(f(Blk(0, 0, 0, 4, 2)));  // 8
        var g = BlockExpression.Compile("Z >= 100 - 20");
        Assert.True(g(Blk(0, 0, 80, 0)));     // 80 >= 80
        Assert.False(g(Blk(0, 0, 79, 0)));
    }

    [Fact]
    public void Comparison_operators_and_equals()
    {
        Assert.True(BlockExpression.Compile("Z <= 10")(Blk(0, 0, 10, 0)));
        Assert.True(BlockExpression.Compile("Z >= 10")(Blk(0, 0, 10, 0)));
        Assert.True(BlockExpression.Compile("Grade == 5")(Blk(0, 0, 0, 5)));
        Assert.True(BlockExpression.Compile("Grade = 5")(Blk(0, 0, 0, 5)));   // = 同 ==
        Assert.True(BlockExpression.Compile("Grade != 5")(Blk(0, 0, 0, 6)));
    }

    [Fact]
    public void Attribute_aliases_case_insensitive()
    {
        Assert.True(BlockExpression.Compile("品位 > 5")(Blk(0, 0, 0, 8)));       // 品位=Grade
        Assert.True(BlockExpression.Compile("高程 < 100")(Blk(0, 0, 50, 0)));    // 高程=Z
        Assert.True(BlockExpression.Compile("grade > 5 and z < 100")(Blk(0, 0, 50, 8)));   // 小写关键字
    }

    [Fact]
    public void Syntax_errors_throw()
    {
        Assert.Throws<FormatException>(() => BlockExpression.Compile(""));
        Assert.Throws<FormatException>(() => BlockExpression.Compile("Grade >"));         // 右缺操作数
        Assert.Throws<FormatException>(() => BlockExpression.Compile("(Grade > 5"));       // 缺右括号
        Assert.Throws<FormatException>(() => BlockExpression.Compile("Foo > 5"));          // 未知属性
        Assert.Throws<FormatException>(() => BlockExpression.Compile("Grade > 5 5"));      // 末尾多余
    }
}
