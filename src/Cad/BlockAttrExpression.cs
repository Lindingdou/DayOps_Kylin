using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
//  块体属性表达式求值 —— 忠实移植原 BlockModelLib.ExpressionEngine 的「属性赋值 公式模式」:
//  把 "grade * size*size*size * 2.7" / "if(z < 100, 1, 0)" 编译成 AST, 逐 cell 按 IExprContext 求值。
//  与 BlockExpression(仅谓词, 供筛选/删除)互补: 此产 double 值(供新属性赋值/派生列)。
//  文法: expr=term((+|-)term)*  term=unary((*|/|%)unary)*  unary=(+|-)unary|primary
//        primary=number | ident | ident'('args')' | '('expr')'
//  内置函数: min/max/abs/sqrt/exp/log/sin/cos/tan/floor/ceil/round(1~3 参) + clamp(v,lo,hi) + if(c,a,b)(c>0取a)。
//  除零/模零 → NaN(不抛); 未定义变量 → 0(容错, 同原 v1)。纯逻辑、可单测。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>表达式求值上下文：按变量名取 double。</summary>
public interface IBlockExprContext { double GetVariable(string name); }

/// <summary>可改写上下文(每 cell 求值前 Set 变量; 未定义取 0)。</summary>
public sealed class MutableBlockExprContext : IBlockExprContext
{
    private readonly Dictionary<string, double> _vars = new(StringComparer.OrdinalIgnoreCase);
    public void Set(string name, double value) => _vars[name] = value;
    public void Clear() => _vars.Clear();
    public double GetVariable(string name) => _vars.TryGetValue(name, out var v) ? v : 0.0;
}

public sealed class BlockExprException : Exception
{
    public BlockExprException(string message) : base(message) { }
}

public sealed class BlockAttrExpression
{
    private readonly Node _root;
    public IReadOnlyCollection<string> ReferencedVariables { get; }

    private BlockAttrExpression(Node root, HashSet<string> vars) { _root = root; ReferencedVariables = vars; }

    /// <summary>编译表达式; 语法错误抛 BlockExprException。</summary>
    public static BlockAttrExpression Compile(string expr)
    {
        var toks = Tokenize(expr);
        var p = new Parser(toks);
        var root = p.ParseExpr();
        if (!p.AtEnd) throw new BlockExprException($"表达式末尾多余内容(位置 {p.PeekPos})");
        return new BlockAttrExpression(root, p.UsedVars);
    }

    public double Evaluate(IBlockExprContext ctx) => _root.Eval(ctx);

    // ── 词法 ──
    private enum K { Num, Ident, Plus, Minus, Star, Slash, Percent, LParen, RParen, Comma, End }
    private readonly record struct Tok(K Kind, string Text, double Number, int Pos);

    private static List<Tok> Tokenize(string s)
    {
        var toks = new List<Tok>();
        s ??= "";
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                int j = i; while (j < n && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == 'e' || s[j] == 'E'
                    || ((s[j] == '+' || s[j] == '-') && j > i && (s[j - 1] == 'e' || s[j - 1] == 'E')))) j++;
                if (!double.TryParse(s.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    throw new BlockExprException($"数字非法(位置 {i})");
                toks.Add(new Tok(K.Num, s.Substring(i, j - i), num, i)); i = j; continue;
            }
            if (char.IsLetter(c) || c == '_' || c > 127)
            {
                int j = i; while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] > 127)) j++;
                toks.Add(new Tok(K.Ident, s.Substring(i, j - i), 0, i)); i = j; continue;
            }
            K k = c switch
            {
                '+' => K.Plus, '-' => K.Minus, '*' => K.Star, '/' => K.Slash, '%' => K.Percent,
                '(' => K.LParen, ')' => K.RParen, ',' => K.Comma, _ => K.End,
            };
            if (k == K.End) throw new BlockExprException($"非法字符 '{c}'(位置 {i})");
            toks.Add(new Tok(k, c.ToString(), 0, i)); i++;
        }
        toks.Add(new Tok(K.End, "", 0, n));
        return toks;
    }

    // ── 语法(递归下降) ──
    private sealed class Parser
    {
        private readonly List<Tok> _t; private int _p;
        public HashSet<string> UsedVars { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Parser(List<Tok> t) { _t = t; }
        public bool AtEnd => _t[_p].Kind == K.End;
        public int PeekPos => _t[_p].Pos;
        private Tok Peek() => _t[_p];
        private Tok Eat() => _t[_p++];
        private void Expect(K k) { if (_t[_p].Kind != k) throw new BlockExprException($"期望 {k}, 实际 '{_t[_p].Text}'(位置 {_t[_p].Pos})"); _p++; }

        public Node ParseExpr() => ParseAddSub();

        private Node ParseAddSub()
        {
            var left = ParseMulDiv();
            while (Peek().Kind is K.Plus or K.Minus)
            { bool add = Eat().Kind == K.Plus; var r = ParseMulDiv(); left = new BinNode(add ? '+' : '-', left, r); }
            return left;
        }
        private Node ParseMulDiv()
        {
            var left = ParseUnary();
            while (Peek().Kind is K.Star or K.Slash or K.Percent)
            { var op = Eat().Kind; var r = ParseUnary(); left = new BinNode(op == K.Star ? '*' : op == K.Slash ? '/' : '%', left, r); }
            return left;
        }
        private Node ParseUnary()
        {
            if (Peek().Kind == K.Plus) { Eat(); return ParseUnary(); }
            if (Peek().Kind == K.Minus) { Eat(); return new NegNode(ParseUnary()); }
            return ParsePrimary();
        }
        private Node ParsePrimary()
        {
            var t = Peek();
            if (t.Kind == K.Num) { Eat(); return new NumNode(t.Number); }
            if (t.Kind == K.LParen) { Eat(); var inner = ParseExpr(); Expect(K.RParen); return inner; }
            if (t.Kind == K.Ident)
            {
                Eat();
                if (Peek().Kind == K.LParen)
                {
                    Eat();
                    var args = new List<Node>();
                    if (Peek().Kind != K.RParen) { args.Add(ParseExpr()); while (Peek().Kind == K.Comma) { Eat(); args.Add(ParseExpr()); } }
                    Expect(K.RParen);
                    return new FuncNode(t.Text, args.ToArray());
                }
                UsedVars.Add(t.Text);
                return new VarNode(t.Text);
            }
            throw new BlockExprException($"期望 数字/变量/'(' , 实际 '{t.Text}'(位置 {t.Pos})");
        }
    }

    // ── AST ──
    private abstract class Node { public abstract double Eval(IBlockExprContext c); }
    private sealed class NumNode : Node { private readonly double _v; public NumNode(double v) { _v = v; } public override double Eval(IBlockExprContext c) => _v; }
    private sealed class VarNode : Node { private readonly string _n; public VarNode(string n) { _n = n; } public override double Eval(IBlockExprContext c) => c.GetVariable(_n); }
    private sealed class NegNode : Node { private readonly Node _x; public NegNode(Node x) { _x = x; } public override double Eval(IBlockExprContext c) => -_x.Eval(c); }
    private sealed class BinNode : Node
    {
        private readonly char _op; private readonly Node _l, _r;
        public BinNode(char op, Node l, Node r) { _op = op; _l = l; _r = r; }
        public override double Eval(IBlockExprContext c)
        {
            double a = _l.Eval(c), b = _r.Eval(c);
            return _op switch { '+' => a + b, '-' => a - b, '*' => a * b,
                '/' => b == 0 ? double.NaN : a / b, '%' => b == 0 ? double.NaN : a % b, _ => double.NaN };
        }
    }
    private sealed class FuncNode : Node
    {
        private readonly string _name; private readonly Node[] _args;
        public FuncNode(string n, Node[] a) { _name = n.ToLowerInvariant(); _args = a; }
        public override double Eval(IBlockExprContext c)
        {
            double a0 = _args.Length > 0 ? _args[0].Eval(c) : double.NaN;
            double a1 = _args.Length > 1 ? _args[1].Eval(c) : double.NaN;
            double a2 = _args.Length > 2 ? _args[2].Eval(c) : double.NaN;
            return _name switch
            {
                "min" => _args.Length == 2 ? Math.Min(a0, a1) : double.NaN,
                "max" => _args.Length == 2 ? Math.Max(a0, a1) : double.NaN,
                "abs" => _args.Length == 1 ? Math.Abs(a0) : double.NaN,
                "sqrt" => _args.Length == 1 ? Math.Sqrt(a0) : double.NaN,
                "exp" => _args.Length == 1 ? Math.Exp(a0) : double.NaN,
                "log" => _args.Length == 1 ? Math.Log(a0) : double.NaN,
                "sin" => _args.Length == 1 ? Math.Sin(a0) : double.NaN,
                "cos" => _args.Length == 1 ? Math.Cos(a0) : double.NaN,
                "tan" => _args.Length == 1 ? Math.Tan(a0) : double.NaN,
                "floor" => _args.Length == 1 ? Math.Floor(a0) : double.NaN,
                "ceil" => _args.Length == 1 ? Math.Ceiling(a0) : double.NaN,
                "round" => _args.Length == 1 ? Math.Round(a0) : double.NaN,
                "clamp" => _args.Length == 3 ? Math.Clamp(a0, Math.Min(a1, a2), Math.Max(a1, a2)) : double.NaN,
                "if" => _args.Length == 3 ? (a0 > 0 ? a1 : a2) : double.NaN,
                _ => throw new BlockExprException($"未知函数 '{_name}'"),
            };
        }
    }
}
