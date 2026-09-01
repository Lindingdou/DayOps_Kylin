using System;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>块体属性表达式求值（忠实原 ExpressionEngine 值模式）回归。</summary>
public class BlockAttrExpressionTests
{
    private static double Ev(string expr, params (string, double)[] vars)
    {
        var ctx = new MutableBlockExprContext();
        foreach (var (n, v) in vars) ctx.Set(n, v);
        return BlockAttrExpression.Compile(expr).Evaluate(ctx);
    }

    [Fact]
    public void Arithmetic_precedence_and_parens()
    {
        Assert.Equal(14.0, Ev("2 + 3 * 4"), 9);
        Assert.Equal(20.0, Ev("(2 + 3) * 4"), 9);
        Assert.Equal(1.0, Ev("7 % 3"), 9);
        Assert.Equal(-5.0, Ev("-(2 + 3)"), 9);
        Assert.Equal(2.5, Ev("5 / 2"), 9);
    }

    [Fact]
    public void Variables_and_attribute_formula()
    {
        // metal = grade * 体积 * 密度; grade=4, size=5 → 4 * 125 * 2.7
        Assert.Equal(4.0 * 125 * 2.7, Ev("grade * size*size*size * 2.7", ("grade", 4), ("size", 5)), 6);
        Assert.Equal(900.0, Ev("1000 - z", ("z", 100)), 9);   // 埋深
        Assert.Equal(0.0, Ev("undefined_var"), 9);            // 未定义 → 0
    }

    [Fact]
    public void Builtin_functions_known_values()
    {
        Assert.Equal(3.0, Ev("min(3, 7)"), 9);
        Assert.Equal(7.0, Ev("max(3, 7)"), 9);
        Assert.Equal(5.0, Ev("abs(0 - 5)"), 9);
        Assert.Equal(4.0, Ev("sqrt(16)"), 9);
        Assert.Equal(2.0, Ev("floor(2.9)"), 9);
        Assert.Equal(3.0, Ev("ceil(2.1)"), 9);
        Assert.Equal(2.0, Ev("round(2.5)"), 9);   // Math.Round 默认银行家舍入 ToEven → 2
        Assert.Equal(3.0, Ev("round(2.6)"), 9);
        Assert.Equal(1.0, Ev("exp(0)"), 9);
        Assert.Equal(0.0, Ev("log(1)"), 9);
    }

    [Fact]
    public void Clamp_and_if_conditional()
    {
        Assert.Equal(5.0, Ev("clamp(10, 0, 5)"), 9);   // 上夹
        Assert.Equal(0.0, Ev("clamp(-3, 0, 5)"), 9);   // 下夹
        Assert.Equal(3.0, Ev("clamp(3, 0, 5)"), 9);
        Assert.Equal(1.0, Ev("if(grade - 5, 1, 0)", ("grade", 8)), 9);   // 8-5>0 → 1
        Assert.Equal(0.0, Ev("if(grade - 5, 1, 0)", ("grade", 2)), 9);   // 2-5<0 → 0
    }

    [Fact]
    public void Div_and_mod_by_zero_are_nan_not_throw()
    {
        Assert.True(double.IsNaN(Ev("1 / 0")));
        Assert.True(double.IsNaN(Ev("1 % 0")));
    }

    [Fact]
    public void Syntax_errors_throw()
    {
        Assert.Throws<BlockExprException>(() => BlockAttrExpression.Compile("2 +"));
        Assert.Throws<BlockExprException>(() => BlockAttrExpression.Compile("(2 + 3"));
        Assert.Throws<BlockExprException>(() => BlockAttrExpression.Compile("2 3"));
        Assert.Throws<BlockExprException>(() => BlockAttrExpression.Compile("@bad"));
    }
}
