// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/EquipIconBuffer.cs（逐行对应；WPF DrawingImage/PathGeometry → Avalonia 同名类型，Freeze 无对应故省去）
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  设备形态的**2D 图标**写入端 —— 第三种介质，与实体层/线框层共用同一套形状定义
//
//  为什么不是画一套图标：设备图标要出现在配置树、清单、图例上，而同一台钻机在三维模拟里
//  已经有形态与颜色了。另画一套的话，改了三维那边图标不动 —— 现象是
//  **同一台设备在配置界面和三维里长得不一样、颜色也不一样**，两边各自都自洽、都不报错。
//  这个仓库的「两套色表」正是这么来的（预览窗自定义三个颜色，注释写着"与入图同源"而三个值没一个相同）。
//
//  所以：形态仍只在 EquipSymbolLibrary 写一次，颜色仍只在 EquipPalette 定一次，
//  投影仍只用 SimPanelCamera 那一个 —— 本类只回答「一个盒子落成哪 6 个面、怎么排前后」。
//
//  ── 与另两种介质的对应 ──
//    Box    → 转成 Hex（同一套 8 个角点），6 个面
//    Hex    → 同上，用调用方给的 8 个角点
//    PrismX → sides 个侧面 + 2 个端盖；★ 角点公式与 EquipMeshBuffer/EquipWireBuffer 逐字相同
//  ⇒ 三种介质的角点集合逐点相同，只是「连成边 / 连成三角 / 连成填充面」不同。形态不可能漂。
//
//  ── 画法：painter's algorithm ──
//  与 SimPanelOverlay 同一条路子（WPF 没有深度缓冲）：面按深度排序后依次画。
//  面级排序对这种十几个体的符号足够 —— 它们是实心凸块，不像线框那样要看穿。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 设备符号的 2D 图标缓冲。写进来的是<b>局部</b>坐标，<see cref="BeginPose"/> 之后自己变换到世界系
/// —— 与另两个 sink 同一套位姿口径（尺度 → 绕 Z 旋 → 平移）。
/// </summary>
public sealed class EquipIconBuffer : IEquipShapeSink
{
    /// <summary>一个面：4 个世界坐标点 + 颜色。</summary>
    private readonly List<(double[] P, uint Rgb)> _faces = new();

    private double _ox, _oy, _oz, _cos = 1, _sin, _s = 1;

    /// <summary>面数。</summary>
    public int FaceCount => _faces.Count;

    /// <summary>「顶点数」= 面数 × 4。<b>只为满足 <see cref="IEquipShapeSink"/> 的对账口径</b>，
    /// 与另两种介质的顶点数不可比（连法不同），别拿它们互相校验。</summary>
    public int VertexCount => _faces.Count * 4;

    public bool IsEmpty => _faces.Count == 0;

    public void Clear() => _faces.Clear();

    public void BeginPose(double ox, double oy, double oz, double headingRad, double scale)
    {
        _ox = ox; _oy = oy; _oz = oz;
        _cos = Math.Cos(headingRad); _sin = Math.Sin(headingRad);
        _s = scale;
    }

    public void EndPose() { }

    public void Box(double x0, double x1, double y0, double y1, double z0, double z1, uint rgb)
        => Hex(x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0,
               x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1, rgb);

    public void Hex(double x0, double y0, double z0, double x1, double y1, double z1,
                    double x2, double y2, double z2, double x3, double y3, double z3,
                    double x4, double y4, double z4, double x5, double y5, double z5,
                    double x6, double y6, double z6, double x7, double y7, double z7,
                    uint rgb)
    {
        var p = new[]
        {
            x0, y0, z0, x1, y1, z1, x2, y2, z2, x3, y3, z3,
            x4, y4, z4, x5, y5, z5, x6, y6, z6, x7, y7, z7,
        };

        Quad(p, 0, 1, 2, 3, rgb);   // 底
        Quad(p, 4, 5, 6, 7, rgb);   // 顶
        Quad(p, 0, 1, 5, 4, rgb);   // 侧 ×4
        Quad(p, 1, 2, 6, 5, rgb);
        Quad(p, 2, 3, 7, 6, rgb);
        Quad(p, 3, 0, 4, 7, rgb);
    }

    public void PrismX(double x0, double x1, double zc0, double zc1, double radiusY, int sides, uint rgb)
    {
        if (sides < 3) sides = 3;
        // ★ 角点公式与另两个 sink 逐字相同（连 rz<=0 也不额外兜底）——
        //   兜底一加，退化情形下三种介质的角点就不一样了，而那正是共用形状库的理由。
        double zc = (zc0 + zc1) * 0.5, rz = (zc1 - zc0) * 0.5;
        double Py(int i) => radiusY * Math.Cos(2 * Math.PI * i / sides);
        double Pz(int i) => zc + rz * Math.Sin(2 * Math.PI * i / sides);

        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            AddFace(new[]
            {
                x0, Py(i), Pz(i), x1, Py(i), Pz(i),
                x1, Py(j), Pz(j), x0, Py(j), Pz(j),
            }, rgb);
        }
        // 端盖：用扇形三角（退化成 4 点面，最后一点重复）——只为遮住内部，形态不靠它
        for (int i = 1; i + 1 < sides; i++)
        {
            AddFace(new[] { x0, Py(0), Pz(0), x0, Py(i), Pz(i), x0, Py(i + 1), Pz(i + 1), x0, Py(0), Pz(0) }, rgb);
            AddFace(new[] { x1, Py(0), Pz(0), x1, Py(i + 1), Pz(i + 1), x1, Py(i), Pz(i), x1, Py(0), Pz(0) }, rgb);
        }
    }

    private void Quad(double[] p, int a, int b, int c, int d, uint rgb)
        => AddFace(new[]
        {
            p[a * 3], p[a * 3 + 1], p[a * 3 + 2],
            p[b * 3], p[b * 3 + 1], p[b * 3 + 2],
            p[c * 3], p[c * 3 + 1], p[c * 3 + 2],
            p[d * 3], p[d * 3 + 1], p[d * 3 + 2],
        }, rgb);

    /// <summary>写一个面（局部 → 世界）。</summary>
    private void AddFace(double[] local, uint rgb)
    {
        var w = new double[12];
        for (int i = 0; i < 4; i++)
        {
            Xf(local[i * 3], local[i * 3 + 1], local[i * 3 + 2],
               out w[i * 3], out w[i * 3 + 1], out w[i * 3 + 2]);
        }
        _faces.Add((w, rgb));
    }

    /// <summary>局部 → 世界：尺度 → 绕 Z 旋 → 平移（与另两个 sink 同一口径）。</summary>
    private void Xf(double x, double y, double z, out double wx, out double wy, out double wz)
    {
        double sx = x * _s, sy = y * _s, sz = z * _s;
        wx = _ox + sx * _cos - sy * _sin;
        wy = _oy + sx * _sin + sy * _cos;
        wz = _oz + sz;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  出图
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>光源方向（世界系，指向光源）。固定值 —— 图标之间明暗要可比。</summary>
    private static readonly (double X, double Y, double Z) Light = Norm(-0.4, -0.55, 0.75);

    /// <summary>环境光占比。0.55 是让背光面仍能看清轮廓的下限（再低就糊成黑块）。</summary>
    private const double Ambient = 0.55;

    /// <summary>
    /// 投影 + 朗伯明暗 + 深度排序，出一张 <see cref="DrawingImage"/>。
    ///
    /// <para><b>投影用调用方给的相机</b>，不在本类里另写一份 —— 面板、三维、图标必须是同一套轴测，
    /// 否则同一台设备在两处的姿态不一样，人对不上号。</para>
    /// </summary>
    /// <param name="cam">轴测相机。<b>只用它的角度</b>；缩放与中心由本方法按内容自适应覆盖。</param>
    /// <param name="sizePx">图标边长（DIP）。</param>
    /// <param name="padPx">四周留白（DIP）。</param>
    public DrawingImage ToIcon(SimPanelCamera cam, double sizePx = 20, double padPx = 1.0)
    {
        var g = new DrawingGroup();
        if (_faces.Count == 0 || sizePx <= 0) return Freeze(g, sizePx);

        // ① 先按相机角度投影一遍，量出内容包围盒 → 自适应缩放与居中。
        //    图标是给人认形状的，不是量尺寸的，所以这里可以自适应；
        //    但角度不许动 —— 角度一变就不是"三维里那台"了。
        var fit = new SimPanelCamera
        {
            AzimuthDeg = cam.AzimuthDeg, TiltDeg = cam.TiltDeg,
            ZExaggeration = cam.ZExaggeration,
            Scale = 1, Cx = 0, Cy = 0, Cz = 0,
            // 图标只看形状：透视关掉、面板尺寸归零，Project 才退化成纯轴测的 (rx, up)（原版相机无透视项）
            Perspective = 0, Width = 0, Height = 0,
        };
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var (p, _) in _faces)
            for (int i = 0; i < 4; i++)
            {
                fit.Project(p[i * 3], p[i * 3 + 1], p[i * 3 + 2], out double sx, out double sy, out _);
                sy = -sy;   // Kylin 相机已把 up 翻成屏幕向下；这里要的是原版口径的 up 向上
                if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
                if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
            }
        double w = Math.Max(1e-9, maxX - minX), h = Math.Max(1e-9, maxY - minY);
        double inner = Math.Max(1e-6, sizePx - 2 * padPx);
        double k = inner / Math.Max(w, h);
        double offX = padPx + (inner - w * k) * 0.5 - minX * k;
        double offY = padPx + (inner - h * k) * 0.5 - minY * k;

        // ② 逐面：投影 → 深度 → 朗伯明暗
        var drawn = new List<(double Depth, Geometry Geo, IBrush Fill)>(_faces.Count);
        foreach (var (p, rgb) in _faces)
        {
            var pts = new Point[4];
            double depth = 0;
            for (int i = 0; i < 4; i++)
            {
                fit.Project(p[i * 3], p[i * 3 + 1], p[i * 3 + 2], out double sx, out double sy, out double d);
                sy = -sy;
                // 屏幕 Y 向下 —— 投影出来的 up 是向上的，所以取负
                pts[i] = new Point(sx * k + offX, sizePx - (sy * k + offY));
                depth += d;
            }
            depth *= 0.25;

            double lam = Lambert(p);
            var c = Tint(rgb, lam);

            var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
            fig.Segments!.Add(new PolyLineSegment(new[] { pts[1], pts[2], pts[3] }));
            var geo = new PathGeometry();
            geo.Figures!.Add(fig);
            drawn.Add((depth, geo, c));
        }

        // ③ 远 → 近（painter's）。深度大的先画。
        drawn.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        foreach (var (_, geo, fill) in drawn)
            g.Children.Add(new GeometryDrawing { Geometry = geo, Brush = fill });

        return Freeze(g, sizePx);
    }

    private static DrawingImage Freeze(DrawingGroup g, double sizePx)
    {
        // 明确给一个方形边界：不给的话 DrawingImage 的尺寸随内容走，
        // 树上每个图标大小都不一样，看起来像做坏了。
        // Avalonia 的 DrawingImage 尺寸按子图并集算、ClipGeometry 不计入 —— 垫一张透明方块把边界钉住。
        double s = Math.Max(1, sizePx);
        g.Children.Insert(0, new GeometryDrawing { Geometry = new RectangleGeometry(new Rect(0, 0, s, s)), Brush = Brushes.Transparent });
        g.ClipGeometry = new RectangleGeometry(new Rect(0, 0, s, s));
        return new DrawingImage { Drawing = g };
    }

    /// <summary>面法线 · 光向 → 明暗系数（含环境光）。退化面按全亮处理。</summary>
    private static double Lambert(double[] p)
    {
        double ux = p[3] - p[0], uy = p[4] - p[1], uz = p[5] - p[2];
        double vx = p[6] - p[0], vy = p[7] - p[1], vz = p[8] - p[2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(len > 1e-12)) return 1.0;
        nx /= len; ny /= len; nz /= len;
        double d = Math.Abs(nx * Light.X + ny * Light.Y + nz * Light.Z);   // 双面：背面也照亮，免得内壁全黑
        return Ambient + (1 - Ambient) * d;
    }

    private static SolidColorBrush Tint(uint rgb, double lam)
    {
        byte R = (byte)Math.Clamp(((rgb >> 16) & 0xFF) * lam, 0, 255);
        byte G = (byte)Math.Clamp(((rgb >> 8) & 0xFF) * lam, 0, 255);
        byte B = (byte)Math.Clamp((rgb & 0xFF) * lam, 0, 255);
        return new SolidColorBrush(Color.FromRgb(R, G, B));
    }

    private static (double, double, double) Norm(double x, double y, double z)
    {
        double l = Math.Sqrt(x * x + y * y + z * z);
        return l > 1e-12 ? (x / l, y / l, z / l) : (0, 0, 1);
    }
}

/// <summary>
/// 按设备类别出图标 —— <b>唯一入口</b>，带缓存。
/// <para>树/清单/图例一律走这里，别各处自己 new 一个 buffer：
/// 那样迟早有人给某一处换个角度或换个尺寸，于是同一台设备在两个列表里又长得不一样了。</para>
/// </summary>
public static class EquipIconLibrary
{
    /// <summary>
    /// 图标的轴测角度。缺省<b>与面板轴测同一套</b>（方位 30°、俯仰 55°）——
    /// 55° 是面板注释里写的"兼顾平面关系与高差"的那个缺省，不是随手取的。
    /// <para>可改（<see cref="Configure"/>）。改了<b>所有</b>用图标的地方一起变 ——
    /// 这正是它是个设置而不是各处常量的理由：同一台设备在两个列表里姿态不一样，人对不上号。</para>
    /// </summary>
    public static double AzimuthDeg { get; private set; } = 30;
    public static double TiltDeg { get; private set; } = 55;

    /// <summary>缺省图标边长（DIP）。<b>16 认不出类别</b>（电铲/前装机都是一小块蓝），所以缺省 20。</summary>
    public static int DefaultSizePx { get; private set; } = 20;

    /// <summary>当前角度的相机（每次取新实例 —— 相机是可变对象，共享一个会被调用方改脏）。</summary>
    public static SimPanelCamera IconCamera
        => new() { AzimuthDeg = AzimuthDeg, TiltDeg = TiltDeg, ZExaggeration = 1.0 };

    private static readonly Dictionary<(EquipKind, EquipState, int), DrawingImage> _cache = new();

    /// <summary>
    /// 调图标口径。<b>只在这一处改，且必须清缓存</b> ——
    /// 不清的话已经取过的图标还是旧角度，界面上新旧混着，而且只在部分行上出现。
    /// </summary>
    /// <param name="azimuthDeg">方位角。null = 不改。</param>
    /// <param name="tiltDeg">俯仰角（90 = 正射俯视，0 = 正立面）。null = 不改。</param>
    /// <param name="defaultSizePx">缺省边长。null = 不改。</param>
    public static void Configure(double? azimuthDeg = null, double? tiltDeg = null, int? defaultSizePx = null)
    {
        if (azimuthDeg is { } a) AzimuthDeg = a;
        if (tiltDeg is { } t) TiltDeg = Math.Clamp(t, 0, 90);
        if (defaultSizePx is { } s && s > 0) DefaultSizePx = s;
        _cache.Clear();
    }

    /// <summary>取一类设备的图标。<paramref name="state"/> 决定明度（与三维同一套 StateShade）。</summary>
    /// <param name="sizePx">边长；&lt;=0 用 <see cref="DefaultSizePx"/>。</param>
    public static DrawingImage Get(EquipKind kind, EquipState state = EquipState.Working, int sizePx = 0)
    {
        if (sizePx <= 0) sizePx = DefaultSizePx;
        var key = (kind, state, sizePx);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var buf = new EquipIconBuffer();
        // 落位在原点、朝 +X、符号长 1 —— 图标只看形状，位姿由 ToIcon 自适应。
        // markerScale: 0 = 不画状态标记（那根杆在小尺寸下会把设备本体挤小）。
        EquipSymbolLibrary.Emit(buf, kind, state, 0, 0, 0, 0, lengthM: 1.0, markerScale: 0);
        var img = buf.ToIcon(IconCamera, sizePx);
        _cache[key] = img;
        return img;
    }
}
