using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>绘制工具基类 —— 点击状态机：喂点，凑齐后返回一个实体（否则 null，继续等点）。</summary>
public abstract class DrawTool
{
    /// <summary>当前提示（命令行/状态栏显示）。</summary>
    public abstract string Prompt { get; }

    /// <summary>喂一个点。凑齐则返回新实体并复位（可连续绘制）；否则 null。</summary>
    public abstract SceneEntity? AddPoint(double x, double y);

    public virtual void Reset() { }

    /// <summary>多点工具（如多段线）需显式结束。</summary>
    public virtual bool IsMultiPoint => false;

    /// <summary>结束多点绘制，返回实体（不足则 null）。</summary>
    public virtual SceneEntity? Finish() => null;

    // ── 命令行选项关键字（AutoCAD 的 “指定下一点或 [闭合(C)/放弃(U)]”）─────────────
    /// <summary>一个选项：命令行键入的字母、中文名。</summary>
    public readonly record struct Option(string Key, string Name);

    /// <summary>执行一个选项后的结果。</summary>
    /// <param name="Handled">该关键字是否被本工具认领（false = 不是选项，按坐标/命令处理）。</param>
    /// <param name="Entity">选项产出的实体（如“闭合”出一条闭合多段线）。</param>
    /// <param name="EndsCommand">该选项是否结束本次绘制。</param>
    /// <param name="SwitchTo">改用另一个绘制命令（如 圆 的 3P/2P/T），由外层重新激活。</param>
    /// <param name="Message">状态栏回显。</param>
    public readonly record struct OptionResult(
        bool Handled, SceneEntity? Entity = null, bool EndsCommand = false, string? SwitchTo = null, string? Message = null);

    /// <summary>当前可用的选项（随已点点数变化，如“闭合”要够 2 点才出现）。空 = 无选项。</summary>
    public virtual IReadOnlyList<Option> Options => System.Array.Empty<Option>();

    /// <summary>执行选项；不认识的关键字返回 Handled=false。</summary>
    public virtual OptionResult Invoke(string key) => new(false);

    /// <summary>提示里的选项串：<c>[闭合(C)/放弃(U)]</c>；无选项返回空串。</summary>
    public string OptionHint()
    {
        var ops = Options;
        if (ops.Count == 0) return "";
        var parts = new string[ops.Count];
        for (int i = 0; i < ops.Count; i++) parts[i] = $"{ops[i].Name}({ops[i].Key})";
        return "或 [" + string.Join("/", parts) + "]";      // 接在“指定下一点”后面, 同 AutoCAD 的中文提示行
    }

    /// <summary>关键字匹配：不分大小写的全词命中（AutoCAD 的选项字母）。</summary>
    protected bool IsKey(string typed, string key) => string.Equals(typed?.Trim(), key, StringComparison.OrdinalIgnoreCase);

    /// <summary>追加进行中的预览（橡皮筋）：已点的点 + 当前光标。cursor 为 null 时只画已确定部分。</summary>
    public virtual void AppendPreview(List<float> o, (double x, double y)? cursor) { }

    /// <summary>拖拽即时信息（长度/角度/半径/宽高…）。未落基点或工具无适配 → null（此时状态栏仅显 X/Y）。</summary>
    public virtual string? DragHint(double x, double y) => null;

    /// <summary>长度 + 角度（度，从 +X 逆时针 0–360）—— 直线/多段线段等通用格式。</summary>
    protected static string LenAng(double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (ang < 0) ang += 360.0;
        return $"长 {Math.Sqrt(dx * dx + dy * dy):0.##}  角 {ang:0.#}°";
    }

    /// <summary>两点距离。</summary>
    protected static double Dist(double x0, double y0, double x1, double y1)
        => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    /// <summary>预览色（灰蓝）。</summary>
    protected const float PR = 0.55f, PG = 0.62f, PB = 0.70f;

    protected static SceneEntity Tint(SceneEntity e) { e.Cr = PR; e.Cg = PG; e.Cb = PB; return e; }
}

/// <summary>直线：起点 → 终点。</summary>
public sealed class LineTool : DrawTool
{
    private (double x, double y)? _p0;
    public override string Prompt => _p0 == null ? "直线：指定起点" : "直线：指定终点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p0 == null) { _p0 = (x, y); return null; }
        var p = _p0.Value; _p0 = null;
        return new LineEntity { X0 = p.x, Y0 = p.y, X1 = x, Y1 = y };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_p0 != null && cursor != null)
            Tint(new LineEntity { X0 = _p0.Value.x, Y0 = _p0.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
    }
    public override string? DragHint(double x, double y) => _p0 == null ? null : LenAng(_p0.Value.x, _p0.Value.y, x, y);
    public override void Reset() => _p0 = null;
}

/// <summary>圆：圆心 → 半径点。</summary>
public sealed class CircleTool : DrawTool
{
    private (double x, double y)? _c;
    public override string Prompt => _c == null ? $"圆：指定圆心{OptionHint()}" : "圆：指定半径";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_c == null) { _c = (x, y); return null; }
        var c = _c.Value; _c = null;
        double r = Math.Sqrt((x - c.x) * (x - c.x) + (y - c.y) * (y - c.y));
        return new CircleEntity { Cx = c.x, Cy = c.y, Radius = r };
    }

    // 同 AutoCAD CIRCLE 的 [三点(3P)/两点(2P)/相切、相切、半径(T)]：本系统各是一条独立命令, 选项即切过去。
    // 只在还没定圆心时给 —— 定了圆心再切等于丢掉已点的点。
    public override IReadOnlyList<Option> Options => _c != null ? System.Array.Empty<Option>()
        : new[] { new Option("3P", "三点"), new Option("2P", "两点"), new Option("T", "相切、相切、半径") };

    public override OptionResult Invoke(string key)
    {
        if (_c != null) return new(false);   // 圆心已定：不再认这几个字, 免得把已点的点悄悄丢了
        if (IsKey(key, "3P")) return new(true, SwitchTo: "CIRCLE3P");
        if (IsKey(key, "2P")) return new(true, SwitchTo: "CIRCLE2P");
        if (IsKey(key, "T") || IsKey(key, "TTR")) return new(true, SwitchTo: "TTR");
        return new(false);
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_c != null && cursor != null)
        {
            double dx = cursor.Value.x - _c.Value.x, dy = cursor.Value.y - _c.Value.y;
            Tint(new CircleEntity { Cx = _c.Value.x, Cy = _c.Value.y, Radius = Math.Sqrt(dx * dx + dy * dy) }).Tessellate(o);
        }
    }
    public override string? DragHint(double x, double y) => _c == null ? null : $"半径 {Dist(_c.Value.x, _c.Value.y, x, y):0.##}";
    public override void Reset() => _c = null;
}

/// <summary>圆(2点)：两点为直径端点。</summary>
public sealed class Circle2PTool : DrawTool
{
    private (double x, double y)? _p0;
    public override string Prompt => _p0 == null ? "圆(2点)：指定直径第一端点" : "圆(2点)：指定直径第二端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p0 == null) { _p0 = (x, y); return null; }
        var a = _p0.Value; _p0 = null;
        return Make(a.x, a.y, x, y);
    }
    private static CircleEntity Make(double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return new CircleEntity { Cx = (x0 + x1) / 2, Cy = (y0 + y1) / 2, Radius = Math.Sqrt(dx * dx + dy * dy) / 2 };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_p0 != null && cursor != null)
            Tint(Make(_p0.Value.x, _p0.Value.y, cursor.Value.x, cursor.Value.y)).Tessellate(o);
    }
    public override string? DragHint(double x, double y) => _p0 == null ? null : $"直径 {Dist(_p0.Value.x, _p0.Value.y, x, y):0.##}";
    public override void Reset() => _p0 = null;
}

/// <summary>圆(3点)：过三点（三点外接圆）。</summary>
public sealed class Circle3PTool : DrawTool
{
    private (double x, double y)? _p1, _p2;
    public override string Prompt =>
        _p1 == null ? "圆(3点)：第一点" : _p2 == null ? "圆(3点)：第二点" : "圆(3点)：第三点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p1 == null) { _p1 = (x, y); return null; }
        if (_p2 == null) { _p2 = (x, y); return null; }
        var a = _p1.Value; var b = _p2.Value; _p1 = null; _p2 = null;
        var cc = ArcMath.Circumcircle(a.x, a.y, b.x, b.y, x, y);
        return cc == null ? null : new CircleEntity { Cx = cc.Value.cx, Cy = cc.Value.cy, Radius = cc.Value.r };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _p1 == null) return;
        if (_p2 == null)
            Tint(new LineEntity { X0 = _p1.Value.x, Y0 = _p1.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
        else
        {
            var cc = ArcMath.Circumcircle(_p1.Value.x, _p1.Value.y, _p2.Value.x, _p2.Value.y, cursor.Value.x, cursor.Value.y);
            if (cc != null) Tint(new CircleEntity { Cx = cc.Value.cx, Cy = cc.Value.cy, Radius = cc.Value.r }).Tessellate(o);
        }
    }
    public override void Reset() { _p1 = null; _p2 = null; }
}

/// <summary>矩形：角点 → 对角点。</summary>
public sealed class RectTool : DrawTool
{
    private (double x, double y)? _p0;
    public override string Prompt => _p0 == null ? "矩形：指定角点" : "矩形：指定对角点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p0 == null) { _p0 = (x, y); return null; }
        var p = _p0.Value; _p0 = null;
        return new RectEntity { X0 = p.x, Y0 = p.y, X1 = x, Y1 = y };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_p0 != null && cursor != null)
            Tint(new RectEntity { X0 = _p0.Value.x, Y0 = _p0.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
    }
    public override string? DragHint(double x, double y)
        => _p0 == null ? null : $"宽 {Math.Abs(x - _p0.Value.x):0.##}  高 {Math.Abs(y - _p0.Value.y):0.##}";
    public override void Reset() => _p0 = null;
}

/// <summary>点：单击放置（连续）。</summary>
public sealed class PointTool : DrawTool
{
    public override string Prompt => "点：指定位置";
    public override SceneEntity? AddPoint(double x, double y) => new PointEntity { X = x, Y = y };
}

/// <summary>圆弧：三点（起点 → 圆弧上一点 → 端点）。</summary>
public sealed class ArcTool : DrawTool
{
    private (double x, double y)? _p1, _p2;
    public override string Prompt =>
        _p1 == null ? "圆弧：指定起点" : _p2 == null ? "圆弧：指定圆弧上一点" : "圆弧：指定端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p1 == null) { _p1 = (x, y); return null; }
        if (_p2 == null) { _p2 = (x, y); return null; }
        var a = _p1.Value; var b = _p2.Value; _p1 = null; _p2 = null;
        return new ArcEntity { X1 = a.x, Y1 = a.y, X2 = b.x, Y2 = b.y, X3 = x, Y3 = y };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _p1 == null) return;
        if (_p2 == null)   // 只 1 点：起点→光标 直线预览
            Tint(new LineEntity { X0 = _p1.Value.x, Y0 = _p1.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
        else               // 2 点：三点圆弧预览
            Tint(new ArcEntity { X1 = _p1.Value.x, Y1 = _p1.Value.y, X2 = _p2.Value.x, Y2 = _p2.Value.y, X3 = cursor.Value.x, Y3 = cursor.Value.y }).Tessellate(o);
    }
    public override string? DragHint(double x, double y)
        => _p1 == null ? null : _p2 == null ? LenAng(_p1.Value.x, _p1.Value.y, x, y) : LenAng(_p2.Value.x, _p2.Value.y, x, y);
    public override void Reset() { _p1 = null; _p2 = null; }
}

/// <summary>圆弧：起点 → 圆心 → 端点（逆时针）。</summary>
public sealed class ArcSceTool : DrawTool
{
    private (double x, double y)? _s, _c;
    public override string Prompt => _s == null ? "圆弧(起点圆心端点)：起点" : _c == null ? "圆弧：圆心" : "圆弧：端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_s == null) { _s = (x, y); return null; }
        if (_c == null) { _c = (x, y); return null; }
        var s = _s.Value; var c = _c.Value; _s = null; _c = null;
        return MakeSce(s.x, s.y, c.x, c.y, x, y);
    }
    internal static SceneEntity? MakeSce(double sx, double sy, double cx, double cy, double ex, double ey)
    {
        var t = ArcMath.FromStartCenterEnd(sx, sy, cx, cy, ex, ey);
        return t == null ? null : new ArcEntity { X1 = t.Value.x1, Y1 = t.Value.y1, X2 = t.Value.x2, Y2 = t.Value.y2, X3 = t.Value.x3, Y3 = t.Value.y3 };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _s == null) return;
        if (_c == null) { Tint(new LineEntity { X0 = _s.Value.x, Y0 = _s.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o); return; }
        var e = MakeSce(_s.Value.x, _s.Value.y, _c.Value.x, _c.Value.y, cursor.Value.x, cursor.Value.y);
        if (e != null) Tint(e).Tessellate(o);
    }
    public override string? DragHint(double x, double y)
        => _s == null ? null : _c == null ? LenAng(_s.Value.x, _s.Value.y, x, y) : $"半径 {Dist(_c.Value.x, _c.Value.y, _s.Value.x, _s.Value.y):0.##}";
    public override void Reset() { _s = null; _c = null; }
}

/// <summary>圆弧：圆心 → 起点 → 端点（逆时针）。</summary>
public sealed class ArcCseTool : DrawTool
{
    private (double x, double y)? _c, _s;
    public override string Prompt => _c == null ? "圆弧(圆心起点端点)：圆心" : _s == null ? "圆弧：起点" : "圆弧：端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_c == null) { _c = (x, y); return null; }
        if (_s == null) { _s = (x, y); return null; }
        var c = _c.Value; var s = _s.Value; _c = null; _s = null;
        return ArcSceTool.MakeSce(s.x, s.y, c.x, c.y, x, y);
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _c == null) return;
        if (_s == null) { Tint(new LineEntity { X0 = _c.Value.x, Y0 = _c.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o); return; }
        var e = ArcSceTool.MakeSce(_s.Value.x, _s.Value.y, _c.Value.x, _c.Value.y, cursor.Value.x, cursor.Value.y);
        if (e != null) Tint(e).Tessellate(o);
    }
    public override string? DragHint(double x, double y)
        => _c == null ? null : _s == null ? $"半径 {Dist(_c.Value.x, _c.Value.y, x, y):0.##}" : LenAng(_s.Value.x, _s.Value.y, x, y);
    public override void Reset() { _c = null; _s = null; }
}

/// <summary>多段线：连续点，双击结束。</summary>
public sealed class PolylineTool : DrawTool
{
    private readonly List<(double x, double y)> _pts = new();
    public override bool IsMultiPoint => true;
    public override string Prompt => _pts.Count == 0
        ? "多段线：指定起点"
        : $"多段线：指定下一点{OptionHint()}（已 {_pts.Count} 点，回车/双击结束）";
    public override SceneEntity? AddPoint(double x, double y) { _pts.Add((x, y)); return null; }
    public override SceneEntity? Finish()
    {
        if (_pts.Count < 2) { _pts.Clear(); return null; }
        var e = new PolylineEntity { Points = new List<(double, double)>(_pts) };
        _pts.Clear();
        return e;
    }

    // 选项同 AutoCAD PLINE：闭合(C) 要够 2 点才给（1 点闭合无意义）；放弃(U) 有点就能撤。
    public override IReadOnlyList<Option> Options => _pts.Count >= 2
        ? new[] { new Option("C", "闭合"), new Option("U", "放弃") }
        : _pts.Count == 1 ? new[] { new Option("U", "放弃") } : System.Array.Empty<Option>();

    public override OptionResult Invoke(string key)
    {
        if (IsKey(key, "C") || IsKey(key, "闭合"))
        {
            if (_pts.Count < 3) return new(true, Message: "多段线：至少 3 点才能闭合");
            var e = new PolylineEntity { Points = new List<(double, double)>(_pts), Closed = true };
            int n = _pts.Count; _pts.Clear();
            return new(true, e, EndsCommand: true, Message: $"多段线已闭合（{n} 点）");
        }
        if (IsKey(key, "U") || IsKey(key, "放弃"))
        {
            if (_pts.Count == 0) return new(true, Message: "多段线：没有可放弃的点");
            _pts.RemoveAt(_pts.Count - 1);
            return new(true, Message: _pts.Count == 0 ? "多段线：已退回到起点之前" : $"多段线：已放弃上一点（剩 {_pts.Count} 点）");
        }
        return new(false);
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        for (int i = 0; i + 1 < _pts.Count; i++)
            Seg(o, _pts[i], _pts[i + 1], 0.86f, 0.9f, 0.6f);
        if (cursor != null && _pts.Count > 0)
            Seg(o, _pts[^1], cursor.Value, PR, PG, PB);       // 橡皮筋段
    }
    public override string? DragHint(double x, double y) => _pts.Count == 0 ? null : LenAng(_pts[^1].x, _pts[^1].y, x, y);
    private static void Seg(List<float> o, (double x, double y) a, (double x, double y) b, float r, float g, float bl)
    {
        double ox = RenderOrigin.X, oy = RenderOrigin.Y;   // 先减原点再转 float, 见 RenderOrigin
        o.Add((float)(a.x - ox)); o.Add((float)(a.y - oy)); o.Add(0); o.Add(r); o.Add(g); o.Add(bl);
        o.Add((float)(b.x - ox)); o.Add((float)(b.y - oy)); o.Add(0); o.Add(r); o.Add(g); o.Add(bl);
    }
    public override void Reset() => _pts.Clear();
}

/// <summary>正多边形：中心 → 半径点(顶点方向)。边数默认 6，可设 Sides。</summary>
public sealed class PolygonTool : DrawTool
{
    private (double x, double y)? _c;
    public int Sides = 6;
    public override string Prompt => _c == null ? $"正多边形（{Sides} 边）：指定中心" : $"正多边形（{Sides} 边）：指定顶点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_c == null) { _c = (x, y); return null; }
        var c = _c.Value; _c = null;
        return Make(c.x, c.y, x, y, Sides);
    }
    private static PolygonEntity Make(double cx, double cy, double px, double py, int sides)
    {
        double dx = px - cx, dy = py - cy;
        return new PolygonEntity { Cx = cx, Cy = cy, Radius = Math.Sqrt(dx * dx + dy * dy), Sides = sides, Rotation = Math.Atan2(dy, dx) };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_c != null && cursor != null)
            Tint(Make(_c.Value.x, _c.Value.y, cursor.Value.x, cursor.Value.y, Sides)).Tessellate(o);
    }
    public override string? DragHint(double x, double y) => _c == null ? null : $"半径 {Dist(_c.Value.x, _c.Value.y, x, y):0.##}  边 {Sides}";
    public override void Reset() => _c = null;
}

/// <summary>
/// 文字：忠实原版 TextJigAdapter 的四步 —— 指定起点 → 字高 &lt;2.5&gt; → 旋转角 &lt;0&gt; → 输入文字。
/// 起点用点击/键入坐标；字高与角度既可在命令行键入数值，也可像原版那样点第二点(取到起点的距离/方向)；
/// 回车取默认。落点 = 文字左端基线(原版 AcDbText(pos))，不再是"视口中心"。
/// 预览与原版一致：内容未输入前先用占位串 "TEXT" 按当前起点/字高/角度描一份轮廓跟着光标走。
/// </summary>
public sealed class TextTool : DrawTool
{
    public enum Step { Start, Height, Rotation, Content }

    public const double DefaultHeight = 2.5;      // 原版 "指定文字高度 <2.5000>:"
    private const string Placeholder = "TEXT";    // 原版 createPreview 未得内容时的占位串

    public Step Stage { get; private set; } = Step.Start;
    /// <summary>多行文字：内容里的 '|' 作换行。</summary>
    public bool MultiLine;

    private (double x, double y)? _p;
    private double _height = DefaultHeight;
    private double _rot;                          // 弧度

    public (double x, double y)? Start => _p;
    public double Height => _height;
    public double RotationDeg => _rot * 180.0 / Math.PI;

    private string Name => MultiLine ? "多行文字" : "文字";

    public override string Prompt => Stage switch
    {
        Step.Start => $"{Name}：指定文字的起点",
        Step.Height => $"{Name}：指定文字高度 <{DefaultHeight:0.####}>（键入数值或点第二点）",
        Step.Rotation => $"{Name}：指定文字的旋转角度 <0>（键入度数或点方向点）",
        _ => MultiLine ? $"{Name}：输入文字（| 分行）并回车" : $"{Name}：输入文字并回车"
    };

    /// <summary>正在等内容：命令行这一行是文字, 不是命令/选项/坐标(空格也是文字的一部分)。</summary>
    public bool AwaitingText => Stage == Step.Content;

    public override SceneEntity? AddPoint(double x, double y)
    {
        switch (Stage)
        {
            case Step.Start:
                _p = (x, y); Stage = Step.Height; return null;
            case Step.Height:
            {
                // 同原版 InputType::Distance 的点击分支：取到上一个点的距离
                double d = Dist(_p!.Value.x, _p.Value.y, x, y);
                if (d > 1e-9) _height = d;
                Stage = Step.Rotation; return null;
            }
            case Step.Rotation:
            {
                double dx = x - _p!.Value.x, dy = y - _p.Value.y;
                if (dx * dx + dy * dy > 1e-18) _rot = Math.Atan2(dy, dx);
                Stage = Step.Content; return null;
            }
            default:
                return null;   // 等内容时视口点击不作数(内容只能从命令行来)
        }
    }

    /// <summary>
    /// 命令行键入：字高/角度步接受数值；内容步接受任意串并出实体。
    /// 坐标样式的输入(x,y / @… / d&lt;a)不认领 —— 交给外层按坐标喂点(点第二点的键盘等价)。
    /// </summary>
    public override OptionResult Invoke(string key)
    {
        string s = (key ?? "").Trim();
        switch (Stage)
        {
            case Step.Start:
                return new(false);
            case Step.Height:
                if (LooksLikeCoord(s)) return new(false);
                if (!TryNum(s, out double h)) return new(true, Message: "需要数值字高或第二点");
                if (h <= 0) return new(true, Message: "字高必须大于 0");
                _height = h; Stage = Step.Rotation;
                return new(true, Message: $"字高 = {h:0.####}");
            case Step.Rotation:
                if (LooksLikeCoord(s)) return new(false);
                if (!TryNum(s, out double deg)) return new(true, Message: "需要角度(度)或方向点");
                _rot = deg * Math.PI / 180.0; Stage = Step.Content;
                return new(true, Message: $"旋转角 = {deg:0.##}°");
            default:
                if (s.Length == 0) return new(true, EndsCommand: true, Message: $"{Name}：内容为空，已取消");
                var ent = Build(MultiLine ? s.Replace("|", "\n") : s);
                Reset();
                return new(true, Entity: ent, EndsCommand: true, Message: $"已放置{Name}「{s}」（字高 {ent.Height:0.##}）");
        }
    }

    /// <summary>空回车：字高/角度步取默认值；内容步 = 空内容, 结束命令(同原版/AutoCAD 空串不出实体)。</summary>
    public OptionResult AcceptDefault() => Stage switch
    {
        Step.Height => Advance(Step.Rotation, $"字高 = {DefaultHeight:0.####}（默认）"),
        Step.Rotation => Advance(Step.Content, "旋转角 = 0°（默认）"),
        Step.Content => new(true, EndsCommand: true, Message: $"{Name}：内容为空，已取消"),
        _ => new(false)
    };

    private OptionResult Advance(Step next, string msg) { Stage = next; return new(true, Message: msg); }

    private TextEntity Build(string text) => new()
    {
        X = _p!.Value.x, Y = _p.Value.y, Height = _height, Rotation = _rot, HAlign = 0, VAlign = 0, Text = text
    };

    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        // 同原版 updatePreview：把光标推导成当前步的输入, 再按 createPreview 出占位文字
        double px, py, h = _height, r = _rot;
        if (Stage == Step.Start)
        {
            if (cursor == null) return;
            px = cursor.Value.x; py = cursor.Value.y; h = DefaultHeight; r = 0;
        }
        else
        {
            px = _p!.Value.x; py = _p.Value.y;
            if (cursor != null)
            {
                double dx = cursor.Value.x - px, dy = cursor.Value.y - py;
                if (Stage == Step.Height) { double d = Math.Sqrt(dx * dx + dy * dy); if (d > 1e-9) h = d; r = 0; }
                else if (Stage == Step.Rotation && dx * dx + dy * dy > 1e-18) r = Math.Atan2(dy, dx);
            }
        }
        Tint(new TextEntity { X = px, Y = py, Height = h, Rotation = r, Text = Placeholder }).TessellatePick(o);   // 轮廓(真字体也描边)
    }

    public override string? DragHint(double x, double y)
    {
        if (_p == null) return null;
        if (Stage == Step.Height) return $"字高 {Dist(_p.Value.x, _p.Value.y, x, y):0.##}";
        if (Stage == Step.Rotation)
        {
            double ang = Math.Atan2(y - _p.Value.y, x - _p.Value.x) * 180.0 / Math.PI;
            if (ang < 0) ang += 360.0;
            return $"角 {ang:0.#}°";
        }
        return null;
    }

    public override void Reset() { Stage = Step.Start; _p = null; _height = DefaultHeight; _rot = 0; }

    private static bool TryNum(string s, out double v)
        => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

    /// <summary>x,y / @dx,dy / d&lt;ang —— 交给坐标解析而不是当数值。</summary>
    private static bool LooksLikeCoord(string s) => s.StartsWith("@") || s.Contains(',') || s.Contains('<');
}
