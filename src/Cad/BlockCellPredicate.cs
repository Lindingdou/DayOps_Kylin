using System;
using System.Collections.Generic;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 逐块谓词表达式（删除单元「按表达式」）—— 原 ExpressionEngine 只有算术，其对话框例子「z &gt; 200」却写了比较运算；
/// 本类在 <see cref="BlockAttrExpression"/> 之上补一层 比较(&gt; &lt; &gt;= &lt;= == != =) 与 逻辑(AND/OR/NOT, &amp;&amp; || !)，
/// 算术子式仍交 BlockAttrExpression 编译（同一套变量/函数）。无比较时结果 &gt; 0 为真（原语义）。
/// 纯逻辑、可单测。
/// </summary>
public sealed class BlockCellPredicate
{
    private readonly Node _root;
    public IReadOnlyCollection<string> ReferencedVariables { get; }

    private BlockCellPredicate(Node root, HashSet<string> vars) { _root = root; ReferencedVariables = vars; }

    public static BlockCellPredicate Compile(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) throw new BlockExprException("表达式为空");
        var toks = Tokenize(expr);
        int pos = 0;
        var vars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = ParseOr(toks, ref pos, vars);
        if (pos != toks.Count) throw new BlockExprException($"表达式末尾多余内容: '{toks[pos].Text}'");
        return new BlockCellPredicate(root, vars);
    }

    /// <summary>true = 命中（结果 &gt; 0 且非 NaN）。</summary>
    public bool Evaluate(IBlockExprContext ctx)
    {
        double r = _root.Eval(ctx);
        return r > 0 && !double.IsNaN(r);
    }

    private enum K { Arith, LParen, RParen, Cmp, And, Or, Not }
    private readonly record struct Tok(K Kind, string Text);

    private static List<Tok> Tokenize(string s)
    {
        var toks = new List<Tok>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { toks.Add(new Tok(K.LParen, "(")); i++; continue; }
            if (c == ')') { toks.Add(new Tok(K.RParen, ")")); i++; continue; }
            if (i + 1 < n)
            {
                string two = s.Substring(i, 2);
                if (two is ">=" or "<=" or "==" or "!=") { toks.Add(new Tok(K.Cmp, two)); i += 2; continue; }
                if (two == "&&") { toks.Add(new Tok(K.And, two)); i += 2; continue; }
                if (two == "||") { toks.Add(new Tok(K.Or, two)); i += 2; continue; }
            }
            if (c is '>' or '<' or '=') { toks.Add(new Tok(K.Cmp, c.ToString())); i++; continue; }
            if (c == '!') { toks.Add(new Tok(K.Not, "!")); i++; continue; }
            if (char.IsLetter(c) || c == '_' || c > 127)
            {
                int j = i; while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] > 127)) j++;
                string w = s.Substring(i, j - i);
                if (w.Equals("and", StringComparison.OrdinalIgnoreCase)) toks.Add(new Tok(K.And, w));
                else if (w.Equals("or", StringComparison.OrdinalIgnoreCase)) toks.Add(new Tok(K.Or, w));
                else if (w.Equals("not", StringComparison.OrdinalIgnoreCase)) toks.Add(new Tok(K.Not, w));
                else toks.Add(new Tok(K.Arith, w));
                i = j; continue;
            }
            if (char.IsDigit(c) || c == '.')
            {
                int j = i; while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '.' || ((s[j] == '+' || s[j] == '-') && (s[j - 1] == 'e' || s[j - 1] == 'E')))) j++;
                toks.Add(new Tok(K.Arith, s.Substring(i, j - i))); i = j; continue;
            }
            toks.Add(new Tok(K.Arith, c.ToString())); i++;   // + - * / % , 等交算术层
        }
        return toks;
    }

    private static Node ParseOr(List<Tok> t, ref int p, HashSet<string> vars)
    {
        var left = ParseAnd(t, ref p, vars);
        while (p < t.Count && t[p].Kind == K.Or) { p++; var r = ParseAnd(t, ref p, vars); left = new Bin(left, r, (a, b) => (a > 0 || b > 0) ? 1 : 0); }
        return left;
    }
    private static Node ParseAnd(List<Tok> t, ref int p, HashSet<string> vars)
    {
        var left = ParseNot(t, ref p, vars);
        while (p < t.Count && t[p].Kind == K.And) { p++; var r = ParseNot(t, ref p, vars); left = new Bin(left, r, (a, b) => (a > 0 && b > 0) ? 1 : 0); }
        return left;
    }
    private static Node ParseNot(List<Tok> t, ref int p, HashSet<string> vars)
    {
        if (p < t.Count && t[p].Kind == K.Not) { p++; var r = ParseNot(t, ref p, vars); return new Bin(r, r, (a, _) => a > 0 ? 0 : 1); }
        return ParseCompare(t, ref p, vars);
    }
    private static Node ParseCompare(List<Tok> t, ref int p, HashSet<string> vars)
    {
        var left = ParsePrimary(t, ref p, vars);
        if (p < t.Count && t[p].Kind == K.Cmp)
        {
            string op = t[p++].Text; var r = ParsePrimary(t, ref p, vars);
            Func<double, double, double> f = op switch
            {
                ">" => (a, b) => a > b ? 1 : 0, "<" => (a, b) => a < b ? 1 : 0, ">=" => (a, b) => a >= b ? 1 : 0,
                "<=" => (a, b) => a <= b ? 1 : 0, "!=" => (a, b) => a != b ? 1 : 0, _ => (a, b) => a == b ? 1 : 0,
            };
            return new Bin(left, r, f);
        }
        return left;
    }

    /// <summary>括号组：内部（同层）含比较/逻辑 → 逻辑分组；否则整段（含其后算术）交算术层。</summary>
    private static Node ParsePrimary(List<Tok> t, ref int p, HashSet<string> vars)
    {
        if (p >= t.Count) throw new BlockExprException("表达式意外结束");
        if (t[p].Kind is K.RParen or K.Cmp or K.And or K.Or or K.Not) throw new BlockExprException($"意外的 '{t[p].Text}'");
        if (t[p].Kind == K.LParen && GroupIsLogical(t, p))
        {
            p++; var e = ParseOr(t, ref p, vars);
            if (p >= t.Count || t[p].Kind != K.RParen) throw new BlockExprException("缺少右括号 )");
            p++; return e;
        }
        // 算术片段：同层直到 比较/逻辑/右括号
        var sb = new StringBuilder();
        int depth = 0;
        while (p < t.Count)
        {
            var k = t[p].Kind;
            if (depth == 0 && k is K.Cmp or K.And or K.Or or K.Not or K.RParen) break;
            if (k == K.LParen) depth++;
            if (k == K.RParen) depth--;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t[p].Text); p++;
        }
        if (sb.Length == 0) throw new BlockExprException("缺少表达式");
        var arith = BlockAttrExpression.Compile(sb.ToString());
        foreach (var v in arith.ReferencedVariables) vars.Add(v);
        return new Leaf(arith);
    }

    private static bool GroupIsLogical(List<Tok> t, int open)
    {
        int depth = 0;
        for (int i = open; i < t.Count; i++)
        {
            if (t[i].Kind == K.LParen) depth++;
            else if (t[i].Kind == K.RParen) { depth--; if (depth == 0) return false; }
            else if (depth == 1 && t[i].Kind is K.Cmp or K.And or K.Or or K.Not) return true;
        }
        return false;
    }

    private abstract class Node { public abstract double Eval(IBlockExprContext c); }
    private sealed class Leaf : Node { private readonly BlockAttrExpression _e; public Leaf(BlockAttrExpression e) { _e = e; } public override double Eval(IBlockExprContext c) => _e.Evaluate(c); }
    private sealed class Bin : Node
    {
        private readonly Node _l, _r; private readonly Func<double, double, double> _f;
        public Bin(Node l, Node r, Func<double, double, double> f) { _l = l; _r = r; _f = f; }
        public override double Eval(IBlockExprContext c) => _f(_l.Eval(c), _r.Eval(c));
    }
}
