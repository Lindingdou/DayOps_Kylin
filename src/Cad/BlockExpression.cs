using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 块体表达式筛选/删除 —— 忠实原 BlockModelLib.ExpressionEngine 的「删除单元·表达式范围」用途:
/// 把 "Grade > 5 AND Z &lt; 100" 编译成块谓词。属性(不分大小写): X/Y/Z/Grade/Size(及别名 品位=Grade/高程=Z)。
/// 统一值语法(比较/布尔产 0/1, 同 C, 括号无歧义): OR &lt; AND &lt; NOT &lt; 比较 &lt; +− &lt; ×÷ &lt; 一元− &lt; 原子。
/// 纯逻辑、可单测。(原多属性 CellData 公式赋值仍受数据模型限, 记录。)
/// </summary>
public static class BlockExpression
{
    /// <summary>编译为块谓词; 语法错误抛 FormatException。</summary>
    public static Func<BlockModel.Block, bool> Compile(string expr)
    {
        var toks = Tokenize(expr);
        if (toks.Count == 0) throw new FormatException("表达式为空");
        int pos = 0;
        var fn = ParseOr(toks, ref pos);
        if (pos != toks.Count) throw new FormatException($"表达式末尾多余内容: '{toks[pos]}'");
        return b => fn(b) != 0;
    }

    // ── 词法 ──
    private static List<string> Tokenize(string s)
    {
        var toks = new List<string>();
        if (s == null) return toks;
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                int j = i; while (j < n && (char.IsDigit(s[j]) || s[j] == '.')) j++;
                toks.Add(s.Substring(i, j - i)); i = j; continue;
            }
            if (char.IsLetter(c) || c == '_' || c > 127)   // 标识符(含中文别名)
            {
                int j = i; while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] > 127)) j++;
                toks.Add(s.Substring(i, j - i)); i = j; continue;
            }
            // 双字符运算符
            if (i + 1 < n)
            {
                string two = s.Substring(i, 2);
                if (two == ">=" || two == "<=" || two == "==" || two == "!=" || two == "&&" || two == "||")
                { toks.Add(two); i += 2; continue; }
            }
            if ("><=+-*/()!".IndexOf(c) >= 0) { toks.Add(c.ToString()); i++; continue; }
            throw new FormatException($"非法字符: '{c}'(位置 {i})");
        }
        return toks;
    }

    private static bool IsKw(string t, string kw) => string.Equals(t, kw, StringComparison.OrdinalIgnoreCase);

    // ── 递归下降(每层返回 Func<Block,double>) ──
    private static Func<BlockModel.Block, double> ParseOr(List<string> t, ref int p)
    {
        var left = ParseAnd(t, ref p);
        while (p < t.Count && (IsKw(t[p], "OR") || t[p] == "||"))
        { p++; var r = ParseAnd(t, ref p); var l = left; left = b => (l(b) != 0 || r(b) != 0) ? 1.0 : 0.0; }
        return left;
    }

    private static Func<BlockModel.Block, double> ParseAnd(List<string> t, ref int p)
    {
        var left = ParseNot(t, ref p);
        while (p < t.Count && (IsKw(t[p], "AND") || t[p] == "&&"))
        { p++; var r = ParseNot(t, ref p); var l = left; left = b => (l(b) != 0 && r(b) != 0) ? 1.0 : 0.0; }
        return left;
    }

    private static Func<BlockModel.Block, double> ParseNot(List<string> t, ref int p)
    {
        if (p < t.Count && (IsKw(t[p], "NOT") || t[p] == "!"))
        { p++; var r = ParseNot(t, ref p); return b => r(b) == 0 ? 1.0 : 0.0; }
        return ParseCompare(t, ref p);
    }

    private static Func<BlockModel.Block, double> ParseCompare(List<string> t, ref int p)
    {
        var left = ParseAdd(t, ref p);
        if (p < t.Count && (t[p] == ">" || t[p] == "<" || t[p] == ">=" || t[p] == "<=" || t[p] == "==" || t[p] == "!=" || t[p] == "="))
        {
            string op = t[p++]; var r = ParseAdd(t, ref p); var l = left;
            return op switch
            {
                ">" => b => l(b) > r(b) ? 1.0 : 0.0,
                "<" => b => l(b) < r(b) ? 1.0 : 0.0,
                ">=" => b => l(b) >= r(b) ? 1.0 : 0.0,
                "<=" => b => l(b) <= r(b) ? 1.0 : 0.0,
                "!=" => b => l(b) != r(b) ? 1.0 : 0.0,
                _ => b => l(b) == r(b) ? 1.0 : 0.0,   // == 或 =
            };
        }
        return left;
    }

    private static Func<BlockModel.Block, double> ParseAdd(List<string> t, ref int p)
    {
        var left = ParseMul(t, ref p);
        while (p < t.Count && (t[p] == "+" || t[p] == "-"))
        { string op = t[p++]; var r = ParseMul(t, ref p); var l = left; left = op == "+" ? b => l(b) + r(b) : b => l(b) - r(b); }
        return left;
    }

    private static Func<BlockModel.Block, double> ParseMul(List<string> t, ref int p)
    {
        var left = ParseUnary(t, ref p);
        while (p < t.Count && (t[p] == "*" || t[p] == "/"))
        { string op = t[p++]; var r = ParseUnary(t, ref p); var l = left; left = op == "*" ? b => l(b) * r(b) : b => l(b) / r(b); }
        return left;
    }

    private static Func<BlockModel.Block, double> ParseUnary(List<string> t, ref int p)
    {
        if (p < t.Count && t[p] == "-") { p++; var r = ParseUnary(t, ref p); return b => -r(b); }
        if (p < t.Count && t[p] == "+") { p++; return ParseUnary(t, ref p); }
        return ParsePrimary(t, ref p);
    }

    private static Func<BlockModel.Block, double> ParsePrimary(List<string> t, ref int p)
    {
        if (p >= t.Count) throw new FormatException("表达式意外结束");
        string tok = t[p];
        if (tok == "(")
        {
            p++; var e = ParseOr(t, ref p);
            if (p >= t.Count || t[p] != ")") throw new FormatException("缺少右括号 )");
            p++; return e;
        }
        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
        { p++; return _ => num; }
        // 属性名
        p++;
        return tok.ToLowerInvariant() switch
        {
            "x" => b => b.X,
            "y" => b => b.Y,
            "z" or "高程" or "标高" => b => b.Z,
            "grade" or "品位" or "值" => b => b.Grade,
            "size" or "尺寸" or "边长" => b => b.Size,
            _ => throw new FormatException($"未知属性: '{tok}'(可用 X/Y/Z/Grade/Size)"),
        };
    }
}
