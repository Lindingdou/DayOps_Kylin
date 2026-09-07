using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PitMine3D.Kylin.Controls.Charts;

/// <summary>竖直柱段（钻孔煤层段/化验段/插值体素）：以 (X,Y) 为中心, 高程 Z0..Z1, 底面 W×D。</summary>
public sealed class Box3D
{
    public double X, Y, Z0, Z1, W = 1, D = 1;
    public Color Color = Color.Parse("#78909C");
    public double Opacity = 1;
    public string? Label;      // 顶面上方标注
    public string? Tip;        // 悬停提示
    public object? Tag;
    public bool Highlight;
}

public sealed class Point3D
{
    public double X, Y, Z, Size = 4;
    public Color Color = Color.Parse("#1976D2");
    public string? Label;
    public string? Tip;
    public object? Tag;
}

public sealed class Line3D
{
    public double X0, Y0, Z0, X1, Y1, Z1;
    public Color Color = Color.Parse("#9E9E9E");
    public double Thickness = 1;
}

/// <summary>
/// 托管三维视图（替代原 HelixViewport3D 用于 煤质空间分布 / 钻孔煤质柱状图 3D）：
/// 正交投影 + 轨道旋转(左键拖) + 平移(右/中键拖) + 缩放(滚轮)，画家算法按深度排序绘制柱段/点/线、底图网格、坐标轴、视图立方提示。
/// </summary>
public sealed class Scene3DView : Control
{
    public List<Box3D> Boxes { get; } = new();
    public List<Point3D> Points { get; } = new();
    public List<Line3D> Lines { get; } = new();
    public bool ShowGrid { get; set; } = true;
    public bool ShowAxes { get; set; } = true;
    /// <summary>高程放大倍数（钻孔柱通常 Z 远小于平面跨度）。</summary>
    public double ZExaggeration { get; set; } = 1;
    public Color Background { get; set; } = Color.Parse("#F5F8FB");
    public double FontSize { get; set; } = 11;
    public string EmptyText { get; set; } = "暂无三维数据";

    /// <summary>点击命中某柱段/点（Tag）。</summary>
    public event Action<object?>? ItemClicked;

    // 相机
    private double _yaw = -0.6, _pitch = 0.55, _scale = 1, _panX, _panY;
    private (double x, double y, double z) _center;
    private bool _fitPending = true;
    private Point _last; private bool _rotating, _panning;
    private Point? _hover;
    private readonly List<(Point p, double r, object? tag, string? tip)> _hits = new();

    public Scene3DView() { ClipToBounds = true; MinHeight = 80; Focusable = true; }

    public void Refresh() => InvalidateVisual();
    public void ZoomExtents() { _fitPending = true; _panX = _panY = 0; InvalidateVisual(); }
    public void SetView(double yawDeg, double pitchDeg) { _yaw = yawDeg * Math.PI / 180; _pitch = pitchDeg * Math.PI / 180; InvalidateVisual(); }
    public void TopView() => SetView(0, 89);
    public void FrontView() => SetView(0, 0);
    public void SideView() => SetView(90, 0);
    public void IsoView() => SetView(-35, 32);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pp = e.GetCurrentPoint(this);
        _last = pp.Position;
        if (pp.Properties.IsLeftButtonPressed)
        {
            _rotating = true;
            var hit = HitAt(pp.Position);
            if (hit != null) ItemClicked?.Invoke(hit.Value.tag);
        }
        else _panning = true;
        e.Pointer.Capture(this);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _rotating = _panning = false;
        e.Pointer.Capture(null);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_rotating)
        {
            _yaw += (p.X - _last.X) * 0.01;
            _pitch = Math.Clamp(_pitch + (p.Y - _last.Y) * 0.01, -1.55, 1.55);
        }
        else if (_panning) { _panX += p.X - _last.X; _panY += p.Y - _last.Y; }
        _last = p; _hover = p;
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = null; InvalidateVisual(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double f = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        var p = e.GetPosition(this);
        double cx = Bounds.Width / 2 + _panX, cy = Bounds.Height / 2 + _panY;
        _panX += (cx - p.X) * (f - 1); _panY += (cy - p.Y) * (f - 1);
        _scale *= f;
        InvalidateVisual();
    }

    private (object? tag, string? tip)? HitAt(Point p)
    {
        double best = 12 * 12; (object? tag, string? tip)? res = null;
        foreach (var h in _hits)
        {
            double d = (h.p.X - p.X) * (h.p.X - p.X) + (h.p.Y - p.Y) * (h.p.Y - p.Y);
            double lim = Math.Max(best, h.r * h.r);
            if (d < lim && d < best + h.r * h.r) { best = d; res = (h.tag, h.tip); }
        }
        return res;
    }

    // 世界 → 视图坐标(相机空间: vx 右, vy 上, vz 深)
    private (double vx, double vy, double vz) View(double x, double y, double z)
    {
        double dx = x - _center.x, dy = y - _center.y, dz = (z - _center.z) * ZExaggeration;
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw), cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        double rx = dx * cy - dy * sy, ry = dx * sy + dy * cy;          // 绕 Z 旋转
        double vy = ry * sp + dz * cp;                                  // 俯仰: 前后倾
        double vz = -ry * cp + dz * sp;                                 // 深度(越大越近)
        return (rx, vy, vz);
    }
    private Point Screen((double vx, double vy, double vz) v) => new(Bounds.Width / 2 + _panX + v.vx * _scale, Bounds.Height / 2 + _panY - v.vy * _scale);

    private void Fit()
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        void Acc(double x, double y, double z) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z); }
        foreach (var b in Boxes) { Acc(b.X - b.W / 2, b.Y - b.D / 2, b.Z0); Acc(b.X + b.W / 2, b.Y + b.D / 2, b.Z1); }
        foreach (var p in Points) Acc(p.X, p.Y, p.Z);
        foreach (var l in Lines) { Acc(l.X0, l.Y0, l.Z0); Acc(l.X1, l.Y1, l.Z1); }
        if (minX == double.MaxValue) { _center = (0, 0, 0); _scale = 1; return; }
        _center = ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        double ext = Math.Max(Math.Max(maxX - minX, maxY - minY), (maxZ - minZ) * ZExaggeration);
        if (ext < 1e-9) ext = 1;
        _scale = Math.Min(Bounds.Width, Bounds.Height) * 0.8 / (ext * 1.25);
        _fitPending = false;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        _hits.Clear();
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(new SolidColorBrush(Background), new Rect(0, 0, W, H));
        if (W < 10 || H < 10) return;
        if (Boxes.Count == 0 && Points.Count == 0 && Lines.Count == 0)
        {
            var ft = Text(EmptyText, 13, Color.Parse("#6B7280"));
            ctx.DrawText(ft, new Point((W - ft.Width) / 2, (H - ft.Height) / 2));
            return;
        }
        if (_fitPending) Fit();

        // 底图网格(XY 平面, 最低高程)
        double minZ = Boxes.Select(b => b.Z0).Concat(Points.Select(p => p.Z)).Concat(Lines.Select(l => Math.Min(l.Z0, l.Z1))).DefaultIfEmpty(0).Min();
        double minX = Boxes.Select(b => b.X - b.W / 2).Concat(Points.Select(p => p.X)).Concat(Lines.Select(l => Math.Min(l.X0, l.X1))).DefaultIfEmpty(0).Min();
        double maxX = Boxes.Select(b => b.X + b.W / 2).Concat(Points.Select(p => p.X)).Concat(Lines.Select(l => Math.Max(l.X0, l.X1))).DefaultIfEmpty(1).Max();
        double minY = Boxes.Select(b => b.Y - b.D / 2).Concat(Points.Select(p => p.Y)).Concat(Lines.Select(l => Math.Min(l.Y0, l.Y1))).DefaultIfEmpty(0).Min();
        double maxY = Boxes.Select(b => b.Y + b.D / 2).Concat(Points.Select(p => p.Y)).Concat(Lines.Select(l => Math.Max(l.Y0, l.Y1))).DefaultIfEmpty(1).Max();
        if (ShowGrid)
        {
            var gp = new Pen(new SolidColorBrush(Color.Parse("#D0D7DE")), 1);
            double span = Math.Max(maxX - minX, maxY - minY); if (span < 1e-9) span = 1;
            double step = NiceStep(span / 8);
            double gx0 = Math.Floor(minX / step) * step - step, gx1 = Math.Ceiling(maxX / step) * step + step;
            double gy0 = Math.Floor(minY / step) * step - step, gy1 = Math.Ceiling(maxY / step) * step + step;
            for (double x = gx0; x <= gx1 + 1e-9; x += step) ctx.DrawLine(gp, Screen(View(x, gy0, minZ)), Screen(View(x, gy1, minZ)));
            for (double y = gy0; y <= gy1 + 1e-9; y += step) ctx.DrawLine(gp, Screen(View(gx0, y, minZ)), Screen(View(gx1, y, minZ)));
        }

        // 画家排序
        var items = new List<(double depth, Action draw)>();
        double lightYaw = _yaw + 0.8;
        foreach (var b in Boxes)
        {
            var bb = b;
            var c = View(bb.X, bb.Y, (bb.Z0 + bb.Z1) / 2);
            items.Add((c.vz, () => DrawBox(ctx, bb)));
        }
        foreach (var p in Points)
        {
            var pp = p;
            var v = View(pp.X, pp.Y, pp.Z);
            items.Add((v.vz, () =>
            {
                var s = Screen(v);
                double r = Math.Max(1.5, pp.Size / 2);
                ctx.DrawEllipse(new SolidColorBrush(pp.Color), null, s, r, r);
                if (!string.IsNullOrEmpty(pp.Label)) ctx.DrawText(Text(pp.Label!, FontSize - 1, Color.Parse("#1F2937")), new Point(s.X + r + 2, s.Y - FontSize / 2));
                _hits.Add((s, r + 3, pp.Tag, pp.Tip ?? pp.Label));
            }));
        }
        foreach (var l in Lines)
        {
            var ll = l;
            var a = View(ll.X0, ll.Y0, ll.Z0); var b2 = View(ll.X1, ll.Y1, ll.Z1);
            items.Add(((a.vz + b2.vz) / 2, () => ctx.DrawLine(new Pen(new SolidColorBrush(ll.Color), ll.Thickness), Screen(a), Screen(b2))));
        }
        foreach (var it in items.OrderBy(t => t.depth)) it.draw();

        // 坐标轴指示(左下角)
        if (ShowAxes)
        {
            double ox = 40, oy = H - 34, len = 26;
            void Axis(double x, double y, double z, string name, Color col)
            {
                var v = View(_center.x + x, _center.y + y, _center.z + z / Math.Max(1e-9, ZExaggeration));
                var v0 = View(_center.x, _center.y, _center.z);
                double dx = v.vx - v0.vx, dy = v.vy - v0.vy; double n = Math.Sqrt(dx * dx + dy * dy); if (n < 1e-9) return;
                var e = new Point(ox + dx / n * len, oy - dy / n * len);
                ctx.DrawLine(new Pen(new SolidColorBrush(col), 2), new Point(ox, oy), e);
                ctx.DrawText(Text(name, 10, col), new Point(e.X + 2, e.Y - 6));
            }
            Axis(1, 0, 0, "X", Color.Parse("#C62828"));
            Axis(0, 1, 0, "Y", Color.Parse("#2E7D32"));
            Axis(0, 0, 1, "Z", Color.Parse("#1565C0"));
        }
        var hint = Text("左拖旋转 · 右拖平移 · 滚轮缩放", 10, Color.Parse("#9CA3AF"));
        ctx.DrawText(hint, new Point(W - hint.Width - 8, H - hint.Height - 4));

        // 悬停提示
        if (_hover != null)
        {
            var h = HitAt(_hover.Value);
            if (h != null && !string.IsNullOrEmpty(h.Value.tip))
            {
                var ft = Text(h.Value.tip!, FontSize, Colors.White);
                double x = Math.Min(W - ft.Width - 12, _hover.Value.X + 12), y = Math.Max(0, _hover.Value.Y - ft.Height - 14);
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#DD1F2937")), null, new Rect(x, y, ft.Width + 10, ft.Height + 6), 3, 3);
                ctx.DrawText(ft, new Point(x + 5, y + 3));
            }
        }
    }

    private void DrawBox(DrawingContext ctx, Box3D b)
    {
        double hw = b.W / 2, hd = b.D / 2;
        // 8 顶点
        var c = new (double x, double y, double z)[]
        {
            (b.X - hw, b.Y - hd, b.Z0), (b.X + hw, b.Y - hd, b.Z0), (b.X + hw, b.Y + hd, b.Z0), (b.X - hw, b.Y + hd, b.Z0),
            (b.X - hw, b.Y - hd, b.Z1), (b.X + hw, b.Y - hd, b.Z1), (b.X + hw, b.Y + hd, b.Z1), (b.X - hw, b.Y + hd, b.Z1),
        };
        var v = c.Select(p => View(p.x, p.y, p.z)).ToArray();
        var s = v.Select(Screen).ToArray();
        // 面: 顶(4567) 底(0123) 前(0154) 右(1265) 后(2376) 左(3047); 法线在视图空间的 z 分量 >0 即朝向相机
        int[][] faces = { new[] { 4, 5, 6, 7 }, new[] { 0, 3, 2, 1 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } };
        double[] shade = { 1.0, 0.55, 0.85, 0.7, 0.85, 0.7 };
        var col = b.Highlight ? Color.Parse("#FFC107") : b.Color;
        var order = new List<(double depth, int f)>();
        for (int f = 0; f < faces.Length; f++)
        {
            var idx = faces[f];
            // 视图空间法线
            var a = v[idx[0]]; var p1 = v[idx[1]]; var p2 = v[idx[3]];
            double ux = p1.vx - a.vx, uy = p1.vy - a.vy, uz = p1.vz - a.vz, wx = p2.vx - a.vx, wy = p2.vy - a.vy, wz = p2.vz - a.vz;
            double nz = ux * wy - uy * wx;   // 面向相机分量
            if (nz <= 0) continue;
            order.Add((idx.Average(i => v[i].vz), f));
        }
        foreach (var (_, f) in order.OrderBy(t => t.depth))
        {
            var idx = faces[f];
            var poly = new PolylineGeometry(idx.Select(i => s[i]).ToList(), true);
            var cc = Color.FromArgb((byte)(255 * Math.Clamp(b.Opacity, 0, 1)), (byte)(col.R * shade[f]), (byte)(col.G * shade[f]), (byte)(col.B * shade[f]));
            ctx.DrawGeometry(new SolidColorBrush(cc), b.Opacity >= 0.99 ? new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 0.6) : null, poly);
        }
        var topC = Screen(View(b.X, b.Y, b.Z1));
        var midC = Screen(View(b.X, b.Y, (b.Z0 + b.Z1) / 2));
        double rad = Math.Max(4, Math.Max(Math.Abs(s[1].X - s[0].X), Math.Abs(s[4].Y - s[0].Y)) / 2);
        _hits.Add((midC, rad, b.Tag, b.Tip ?? b.Label));
        if (!string.IsNullOrEmpty(b.Label))
        {
            var ft = Text(b.Label!, FontSize - 1, Color.Parse("#1F2937"));
            ctx.DrawText(ft, new Point(topC.X - ft.Width / 2, topC.Y - ft.Height - 2));
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * mag;
    }
    private static FormattedText Text(string s, double size, Color c)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, new SolidColorBrush(c));
}
