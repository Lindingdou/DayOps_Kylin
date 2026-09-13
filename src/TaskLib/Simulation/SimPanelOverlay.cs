// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimPanelOverlay.cs（相机/分组/分层/取景逐行对应；
// WPF DrawingVisual 双层画布 → Avalonia：RequestRender 时把两层各自投影、排序成"已投影图元表"（底图层按相机指纹缓存，
// 内容层逐帧重建），宿主 Control 的 Render 只按表落笔 —— 计数 DrawnSegments 等仍在 RequestRender 返回时即成立。）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  把动态舞台画到【窗体自己的面板】上 —— 与画到主视图是同一个接口的两种落地
//
//  四个舞台（车流 / 动态设备 / 路线标注 / 块体搬运）都只认 <see cref="ISimDynamicOverlay"/>，
//  它们交出去的是**世界坐标的线段 / 点 / 文字**，从不关心谁来画。
//     · <see cref="SimDynamicOverlay"/>      → 内核批量 overlay 组（主视图）
//     · <see cref="SimPanelOverlay"/>（本类）→ 窗体里的面板
//
//  ── 相机：轴测投影（不是透视）── tilt=90° 退化成正射俯视，tilt=0° 是正立面。
//  ── 画法：painter's algorithm —— 按深度排序后依次画。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>轴测相机（世界 → 面板像素）。</summary>
public sealed class SimPanelCamera
{
    /// <summary>方位角（度）：绕 Z 转多少。0 = 正北朝屏幕上方。</summary>
    public double AzimuthDeg { get; set; } = 30;
    /// <summary>俯仰角（度）：90 = 正射俯视，0 = 正立面。</summary>
    public double TiltDeg { get; set; } = 90;   // 默认俯视（右键拖即可转成轴测）
    /// <summary>缩放：一个世界米占多少像素。</summary>
    public double Scale { get; set; } = 0.05;
    /// <summary>视点中心（世界）。</summary>
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double Cz { get; set; }
    /// <summary>Z 向夸张倍数。<b>默认 1 = 真实比例</b>（现场口径：取消夸张）。</summary>
    public double ZExaggeration { get; set; } = 1.0;

    /// <summary>透视强度 0..1：0 = 纯轴测（平行投影），1 = 明显的透视收缩。缺省 0.35。</summary>
    public double Perspective { get; set; } = 0.35;

    /// <summary>相机到视点中心的距离（世界米）。透视强度按它与深度的比值算。</summary>
    public double EyeDistanceM { get; set; } = 6000;

    /// <summary>面板尺寸（像素）。</summary>
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 600;

    /// <summary>世界 → 屏幕。<paramref name="depth"/> 越大越靠近观察者（画得越晚）。</summary>
    public void Project(double x, double y, double z, out double sx, out double sy, out double depth)
    {
        double a = AzimuthDeg * Math.PI / 180.0;
        double ca = Math.Cos(a), sa = Math.Sin(a);
        double dx = x - Cx, dy = y - Cy, dz = (z - Cz) * ZExaggeration;

        double rx = dx * ca + dy * sa;          // 屏幕右（世界东向绕 Z 转 az 之后）
        double ry = -dx * sa + dy * ca;         // 与屏幕右垂直的那个水平方向
        double t = TiltDeg * Math.PI / 180.0;
        double S = Math.Sin(t), C = Math.Cos(t);

        // 屏幕上方 = 世界向量 up = (−sa·S, ca·S, C)：t=90° 平面完整铺开、Z 不出现；t=0° 只剩高差。
        double up = ry * S + dz * C;

        // 透视：越靠近观察者放得越大。Perspective=0 时 k≡1 退化成轴测。
        double depthTo = -ry * C + dz * S;          // 朝观察者为正
        double k = 1.0;
        if (Perspective > 1e-6 && EyeDistanceM > 1e-6)
            k = 1.0 / Math.Max(0.2, 1.0 - Perspective * (depthTo / EyeDistanceM));

        sx = Width * 0.5 + rx * Scale * k;
        sy = Height * 0.5 - up * Scale * k;
        depth = depthTo;
    }

    /// <summary>屏幕右方向（世界单位向量）。</summary>
    private (double X, double Y, double Z) Right()
    {
        double a = AzimuthDeg * Math.PI / 180.0;
        return (Math.Cos(a), Math.Sin(a), 0);
    }

    /// <summary>屏幕上方向（世界单位向量）。t=90° 时退化成水平、t=0° 时是 +Z。</summary>
    private (double X, double Y, double Z) Up()
    {
        double a = AzimuthDeg * Math.PI / 180.0, t = TiltDeg * Math.PI / 180.0;
        double S = Math.Sin(t), C = Math.Cos(t);
        return (-Math.Sin(a) * S, Math.Cos(a) * S, C);
    }

    /// <summary>按屏幕位移移动视点中心（精确反解，任何俯仰角下都成立）。</summary>
    public void MoveByScreen(double du, double dv)
    {
        double s = Math.Max(1e-9, Scale);
        var r = Right(); var u = Up();
        double a = du / s, b = dv / s;
        Cx += a * r.X + b * u.X;
        Cy += a * r.Y + b * u.Y;
        Cz += (a * r.Z + b * u.Z) / Math.Max(1e-9, ZExaggeration);   // Z 投影时被夸张过，反解要除回去
    }

    /// <summary>以屏幕点为锚缩放：那一点下的世界位置保持不动。</summary>
    public void ZoomAt(double sx, double sy, double factor)
    {
        double s0 = Math.Max(1e-9, Scale);
        double s1 = Math.Clamp(s0 * factor, 1e-5, 200);
        if (Math.Abs(s1 - s0) < 1e-12) return;

        double u = (sx - Width * 0.5) / s0;
        double v = -(sy - Height * 0.5) / s0;
        double k = s0 / s1;

        // ★ 先换 Scale，再让 MoveByScreen 按**新**尺度把像素换回世界 —— 所以这里要乘 s1 不是 s0
        Scale = s1;
        MoveByScreen((1 - k) * u * s1, (1 - k) * v * s1);
    }

    /// <summary>把一个世界包围盒装进面板（留 <paramref name="marginFrac"/> 边距）。</summary>
    public void FitTo(double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
                      double marginFrac = 0.12)
    {
        if (!(maxX > minX) || !(maxY > minY)) return;
        Cx = (minX + maxX) * 0.5; Cy = (minY + maxY) * 0.5; Cz = (minZ + maxZ) * 0.5;

        // 取包围盒 8 个角投影后的屏幕范围反推 Scale（先按 Scale=1 投一遍）
        double keep = Scale; Scale = 1.0;
        double lo_x = double.MaxValue, hi_x = double.MinValue, lo_y = double.MaxValue, hi_y = double.MinValue;
        foreach (var (x, y, z) in Corners(minX, minY, minZ, maxX, maxY, maxZ))
        {
            Project(x, y, z, out double px, out double py, out _);
            px -= Width * 0.5; py -= Height * 0.5;
            lo_x = Math.Min(lo_x, px); hi_x = Math.Max(hi_x, px);
            lo_y = Math.Min(lo_y, py); hi_y = Math.Max(hi_y, py);
        }
        Scale = keep;

        double spanX = Math.Max(1e-9, hi_x - lo_x), spanY = Math.Max(1e-9, hi_y - lo_y);
        double s = Math.Min(Width * (1 - marginFrac) / spanX, Height * (1 - marginFrac) / spanY);
        if (s > 0 && !double.IsInfinity(s)) Scale = s;
    }

    private static IEnumerable<(double, double, double)> Corners(double x0, double y0, double z0,
                                                                 double x1, double y1, double z1)
    {
        foreach (var x in new[] { x0, x1 })
            foreach (var y in new[] { y0, y1 })
                foreach (var z in new[] { z0, z1 })
                    yield return (x, y, z);
    }
}

/// <summary>
/// 组画在哪一层。<b>这不是 z 序的细分，是"要不要逐帧重画"的分界</b>。
/// </summary>
public enum SimPanelLayer
{
    /// <summary>逐帧重画（缺省）：块体、卡车、设备符号、铭牌、高亮路径。</summary>
    Content = 0,
    /// <summary>底图：路网底图、区域边界、正射影像。只在自己变或相机变时重画。</summary>
    Base = 1,
}

/// <summary>
/// 面板落地端。<b>只收不画</b> —— <see cref="RequestRender"/> 才真算一帧
/// （与内核那条一样的纪律：Set* 攒、帧末推一次）。
/// </summary>
public sealed class SimPanelOverlay : ISimDynamicOverlay, ISimFaceSink
{
    private sealed class LineGroup { public double[] Xyz = Array.Empty<double>(); public uint[] Argb = Array.Empty<uint>(); public int N; public double W = 1.4; }
    private sealed class MarkGroup { public double[] Xyz = Array.Empty<double>(); public uint[] Argb = Array.Empty<uint>(); public float[]? Px; public byte[]? Style; public int N; }
    private sealed class TextGroup { public double[] Xyz = Array.Empty<double>(); public List<string> Text = new(); public uint[] Argb = Array.Empty<uint>(); public float[]? H; public int N; }
    private sealed class FaceGroup { public double[] Xyz = Array.Empty<double>(); public uint[] Argb = Array.Empty<uint>(); public int N; }

    private readonly Dictionary<string, FaceGroup> _faces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LineGroup> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkGroup> _marks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextGroup> _texts = new(StringComparer.Ordinal);

    private sealed class RasterGroup
    {
        public Bitmap? Img;
        public double X0, Y0, X1, Y1, Z;
        public double Opacity = 1.0;
    }
    private readonly Dictionary<string, RasterGroup> _rasters = new(StringComparer.Ordinal);

    /// <summary>已投影的一层：Render 时按序落笔（底图层缓存、内容层逐帧重建）。</summary>
    private sealed class Layer
    {
        public readonly List<(Matrix M, Bitmap Img, double W, double H, double Opacity)> Rasters = new();
        public readonly List<(IBrush Fill, IPen Stroke, StreamGeometry Geo)> Tris = new();
        public readonly List<(IPen Pen, StreamGeometry Geo)> Polylines = new();
        public readonly List<(IPen Pen, Point A, Point B)> Segs = new();
        public readonly List<(IBrush Brush, Point C, double R)> Marks = new();
        public readonly List<(FormattedText Ft, Point P)> Texts = new();
        public int NSeg, NMark, NLabel, NFace, NRaster;
        public void Clear() { Rasters.Clear(); Tris.Clear(); Polylines.Clear(); Segs.Clear(); Marks.Clear(); Texts.Clear(); NSeg = NMark = NLabel = NFace = NRaster = 0; }
    }
    private readonly Layer _base = new(), _content = new();

    private readonly Dictionary<uint, IBrush> _brushCache = new();

    public SimPanelCamera Camera { get; } = new();

    /// <summary>面板落地端永远可用 —— 它不依赖内核。</summary>
    public bool Available => true;
    public string StatusLabel => "画在窗体面板上（不依赖三维内核）";

    /// <summary>宿主（Render 时回调落笔；RequestRender 后让它失效重画）。</summary>
    internal Control? Host { get; set; }

    /// <summary>本帧画了多少段/点/字（状态栏用）。</summary>
    public int DrawnSegments { get; private set; }
    public int DrawnMarkers { get; private set; }
    public int DrawnLabels { get; private set; }
    /// <summary>本帧画出的栅格底图张数（界面自检：影像到底铺上没有）。</summary>
    public int DrawnRasters { get; private set; }
    public int DrawnFaces { get; private set; }

    /// <summary>线宽（像素）。线框密时调细。</summary>
    public double LineThickness { get; set; } = 1.4;

    public bool SetLines(string group, double[]? xyzFlat, uint[]? argbPerSeg, int segCount)
    {
        if (string.IsNullOrEmpty(group)) return false;
        Touch(group);
        if (xyzFlat == null || segCount <= 0) { _lines.Remove(group); return true; }
        int cap = xyzFlat.Length / 6;
        if (argbPerSeg != null && argbPerSeg.Length < cap) cap = argbPerSeg.Length;
        segCount = Math.Min(segCount, cap);
        if (segCount <= 0) { _lines.Remove(group); return true; }
        double keepW = _widthOf.TryGetValue(group, out double w0) ? w0 : LineThickness;
        _lines[group] = new LineGroup { Xyz = xyzFlat, Argb = argbPerSeg ?? Array.Empty<uint>(), N = segCount, W = keepW };
        return true;
    }

    public bool SetMarkers(string group, double[]? xyzFlat, uint[]? argbPerPt,
                           float[]? pixelSizePerPt, byte[]? stylePerPt, int count)
    {
        if (string.IsNullOrEmpty(group)) return false;
        Touch(group);
        if (xyzFlat == null || count <= 0) { _marks.Remove(group); return true; }
        int cap = xyzFlat.Length / 3;
        count = Math.Min(count, cap);
        if (count <= 0) { _marks.Remove(group); return true; }
        _marks[group] = new MarkGroup
        { Xyz = xyzFlat, Argb = argbPerPt ?? Array.Empty<uint>(), Px = pixelSizePerPt, Style = stylePerPt, N = count };
        return true;
    }

    /// <summary>
    /// 铺一张**按世界坐标定位**的栅格（正射影像 tif 底图）。传 null 即清掉这一组。
    /// 影像画在所有矢量之前；取三个角投出来的屏幕点算一个仿射矩阵去贴图（俯视下精确，轴测下近似）。
    /// </summary>
    public bool SetRaster(string group, Bitmap? img,
                          double minX, double minY, double maxX, double maxY,
                          double z = 0, double opacity = 1.0)
    {
        if (string.IsNullOrEmpty(group)) return false;
        _baseDirty = true;   // 栅格影像按定义就在底图层
        if (img == null || maxX - minX < 1e-9 || maxY - minY < 1e-9) { _rasters.Remove(group); return true; }
        _rasters[group] = new RasterGroup
        { Img = img, X0 = minX, Y0 = minY, X1 = maxX, Y1 = maxY, Z = z, Opacity = Math.Clamp(opacity, 0, 1) };
        return true;
    }

    /// <summary>当前铺着的栅格组名（界面自检用）。</summary>
    public IReadOnlyList<string> RasterGroups => _rasters.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    public bool SetLabels(string group, double[]? xyzFlat, IReadOnlyList<string>? texts,
                          uint[]? argbPerPt, float[]? heightMPerPt,
                          byte[]? hAlignPerPt, byte[]? vAlignPerPt, int count)
    {
        if (string.IsNullOrEmpty(group)) return false;
        Touch(group);
        if (xyzFlat == null || texts == null || count <= 0) { _texts.Remove(group); return true; }
        count = Math.Min(count, Math.Min(xyzFlat.Length / 3, texts.Count));
        if (count <= 0) { _texts.Remove(group); return true; }
        _texts[group] = new TextGroup
        { Xyz = xyzFlat, Text = texts.ToList(), Argb = argbPerPt ?? Array.Empty<uint>(), H = heightMPerPt, N = count };
        return true;
    }

    /// <summary>给某个线组单独定线宽（像素）。<b>粗细是主次的主要手段</b>。设过之后 SetLines 不会把它冲掉。</summary>
    public void SetGroupWidth(string group, double px)
    {
        if (string.IsNullOrEmpty(group)) return;
        Touch(group);
        if (_lines.TryGetValue(group, out var g)) g.W = px;
        else _lines[group] = new LineGroup { N = 0, W = px };
        _widthOf[group] = px;
    }

    private readonly Dictionary<string, double> _widthOf = new(StringComparer.Ordinal);

    public bool SetFaces(string group, double[]? xyzFlat, uint[]? argbPerTri, int triCount)
    {
        if (string.IsNullOrEmpty(group)) return false;
        Touch(group);
        if (xyzFlat == null || triCount <= 0) { _faces.Remove(group); return true; }
        int cap = xyzFlat.Length / 9;
        if (argbPerTri != null && argbPerTri.Length < cap) cap = argbPerTri.Length;
        triCount = Math.Min(triCount, cap);
        if (triCount <= 0) { _faces.Remove(group); return true; }
        _faces[group] = new FaceGroup { Xyz = xyzFlat, Argb = argbPerTri ?? Array.Empty<uint>(), N = triCount };
        return true;
    }

    public void Clear(string group)
    {
        if (string.IsNullOrEmpty(group)) return;
        Touch(group);
        _faces.Remove(group); _lines.Remove(group); _marks.Remove(group); _texts.Remove(group);
    }

    /// <summary>撤掉全部组。</summary>
    public void ClearAll() { _faces.Clear(); _lines.Clear(); _marks.Clear(); _texts.Clear(); _baseDirty = true; }

    /// <summary>已 attach 的组名（界面自检用）。</summary>
    public IReadOnlyList<string> Groups
        => _faces.Keys.Concat(_lines.Keys).Concat(_marks.Keys).Concat(_texts.Keys).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    // ── 取景用的「背景组」标记 —— 与分层是**两件事**：分层问"要不要逐帧重画"，取景问"相机该框住谁" ──

    private readonly HashSet<string> _backdrop = new(StringComparer.Ordinal);

    /// <summary>把一组标成<b>背景</b>：<see cref="TryGetFramingBounds"/> 不算它。</summary>
    public void SetGroupBackdrop(string group, bool backdrop)
    {
        if (string.IsNullOrEmpty(group)) return;
        if (backdrop) _backdrop.Add(group); else _backdrop.Remove(group);
    }

    /// <summary>这一组是背景吗（判据用）。</summary>
    public bool IsBackdrop(string group) => _backdrop.Contains(group ?? "");

    /// <summary>取景包围盒：**跳过背景组**。一个非背景组都没有内容时返回 false。</summary>
    public bool TryGetFramingBounds(out double minX, out double minY, out double minZ,
                                    out double maxX, out double maxY, out double maxZ)
        => BoundsCore(g => !_backdrop.Contains(g), out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

    /// <summary>把当前所有组的世界包围盒算出来（相机自适应用）。空返回 false。</summary>
    public bool TryGetWorldBounds(out double minX, out double minY, out double minZ,
                                  out double maxX, out double maxY, out double maxZ)
        => BoundsCore(null, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

    private bool BoundsCore(Func<string, bool>? keep,
                            out double minX, out double minY, out double minZ,
                            out double maxX, out double maxY, out double maxZ)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        bool any = false;

        void Acc(double x, double y, double z)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) return;
            if (double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z)) return;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            any = true;
        }

        foreach (var kv in _lines)
        {
            if (keep != null && !keep(kv.Key)) continue;
            var g = kv.Value;
            for (int i = 0; i < g.N; i++)
            { int o = i * 6; Acc(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2]); Acc(g.Xyz[o + 3], g.Xyz[o + 4], g.Xyz[o + 5]); }
        }
        foreach (var kv in _faces)
        {
            if (keep != null && !keep(kv.Key)) continue;
            var g = kv.Value;
            for (int i = 0; i < g.N * 3; i++)
            { int o = i * 3; Acc(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2]); }
        }
        foreach (var kv in _marks)
        {
            if (keep != null && !keep(kv.Key)) continue;
            var g = kv.Value;
            for (int i = 0; i < g.N; i++)
            { int o = i * 3; Acc(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2]); }
        }
        foreach (var kv in _texts)
        {
            if (keep != null && !keep(kv.Key)) continue;
            var g = kv.Value;
            for (int i = 0; i < g.N; i++)
            { int o = i * 3; Acc(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2]); }
        }

        minX = x0; minY = y0; minZ = z0;
        maxX = x1; maxY = y1; maxZ = z1;
        return any;
    }

    // ── 两层：**底图层**只在变的时候重投影，**内容层**逐帧重投影（路网底图 9 万段不该按 100ms 一次重排）──

    private readonly Dictionary<string, SimPanelLayer> _layerOf = new(StringComparer.Ordinal);
    private bool _baseDirty = true;
    private string _baseCamSig = "";

    /// <summary>把一组划到**底图层**（不随帧变，只在自己变或相机变时重画）。宁可少划，别把逐帧组划进来。</summary>
    public void SetGroupLayer(string group, SimPanelLayer layer)
    {
        if (string.IsNullOrEmpty(group)) return;
        if (_layerOf.TryGetValue(group, out var cur) && cur == layer) return;
        _layerOf[group] = layer;
        _baseDirty = true;     // 组换层：两层都得重排一次
    }

    /// <summary>这一组在哪一层（判据用）。</summary>
    public SimPanelLayer LayerOf(string group)
        => _layerOf.TryGetValue(group ?? "", out var l) ? l : SimPanelLayer.Content;

    private readonly Dictionary<string, int> _orderOf = new(StringComparer.Ordinal);

    /// <summary>底图层内部的叠放序（小的先画 = 在下）。缺省 0，同序再按组名序数排。</summary>
    public void SetGroupOrder(string group, int order)
    {
        if (string.IsNullOrEmpty(group)) return;
        if (_orderOf.TryGetValue(group, out int cur) && cur == order) return;
        _orderOf[group] = order;
        Touch(group);
    }

    /// <summary>这一组的层内叠放序（判据用）。</summary>
    public int OrderOf(string group) => _orderOf.TryGetValue(group ?? "", out int o) ? o : 0;

    private bool IsBase(string group) => LayerOf(group) == SimPanelLayer.Base;

    /// <summary>某个组变了 —— 它在底图层的话，下一帧底图要重画。</summary>
    private void Touch(string group) { if (IsBase(group)) _baseDirty = true; }

    /// <summary>底图层自开窗以来重画了几次。<b>判据用</b>：逐帧不重画底图 ⇒ 播十秒它也不该涨。</summary>
    public int BaseRedrawCount { get; private set; }

    /// <summary>相机指纹：任何一项变了都要重画底图。</summary>
    private string CamSig()
    {
        var c = Camera;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{c.AzimuthDeg:R}|{c.TiltDeg:R}|{c.Scale:R}|{c.Cx:R}|{c.Cy:R}|{c.Cz:R}|{c.ZExaggeration:R}|{c.Perspective:R}|{c.EyeDistanceM:R}|{c.Width:R}|{c.Height:R}");
    }

    /// <summary>真算一帧（帧末调一次）。底图层按需重投影，内容层每次都投影；然后让宿主重画。</summary>
    public void RequestRender()
    {
        string sig = CamSig();
        if (_baseDirty || sig != _baseCamSig)
        {
            BuildLayer(_base, baseLayer: true);
            _baseCamSig = sig;
            _baseDirty = false;
            BaseRedrawCount++;
        }

        BuildLayer(_content, baseLayer: false);
        DrawnSegments = _base.NSeg + _content.NSeg;
        DrawnMarkers = _base.NMark + _content.NMark;
        DrawnLabels = _base.NLabel + _content.NLabel;
        DrawnFaces = _base.NFace + _content.NFace;
        DrawnRasters = _base.NRaster + _content.NRaster;
        Host?.InvalidateVisual();
    }

    /// <summary>宿主 Render 回调：两层依次落笔（底图在下）。</summary>
    internal void Draw(DrawingContext dc)
    {
        // 裁剪到面板，免得远处的段拖出巨大的几何
        using (dc.PushClip(new Rect(0, 0, Math.Max(1, Camera.Width), Math.Max(1, Camera.Height))))
        {
            DrawLayer(dc, _base);
            DrawLayer(dc, _content);
        }
    }

    private static void DrawLayer(DrawingContext dc, Layer l)
    {
        foreach (var r in l.Rasters)
        {
            using (dc.PushTransform(r.M))
            {
                if (r.Opacity < 1.0)
                    using (dc.PushOpacity(r.Opacity)) dc.DrawImage(r.Img, new Rect(0, 0, r.W, r.H));
                else dc.DrawImage(r.Img, new Rect(0, 0, r.W, r.H));
            }
        }
        foreach (var t in l.Tris) dc.DrawGeometry(t.Fill, t.Stroke, t.Geo);
        foreach (var p in l.Polylines) dc.DrawGeometry(null, p.Pen, p.Geo);
        foreach (var s in l.Segs) dc.DrawLine(s.Pen, s.A, s.B);
        foreach (var m in l.Marks) dc.DrawEllipse(m.Brush, null, m.C, m.R, m.R);
        foreach (var t in l.Texts) dc.DrawText(t.Ft, t.P);
    }

    /// <summary>把某一层的组投影、排序成图元表。</summary>
    private void BuildLayer(Layer L, bool baseLayer)
    {
        L.Clear();

        // ── 栅格底图（正射影像）：画在所有矢量**之前**，且只属于底图层 ──
        if (baseLayer)
            foreach (var g in _rasters.Values)
            {
                if (g.Img == null) continue;
                double w = g.Img.PixelSize.Width, h = g.Img.PixelSize.Height;
                if (w < 1 || h < 1) continue;

                // 影像像素系：(0,0)=左上、(w,0)=右上、(0,h)=左下；世界：左上=(X0,Y1)、右上=(X1,Y1)、左下=(X0,Y0)
                Camera.Project(g.X0, g.Y1, g.Z, out double ltx, out double lty, out _);
                Camera.Project(g.X1, g.Y1, g.Z, out double rtx, out double rty, out _);
                Camera.Project(g.X0, g.Y0, g.Z, out double lbx, out double lby, out _);
                if (double.IsNaN(ltx) || double.IsNaN(rtx) || double.IsNaN(lbx)) continue;

                var m = new Matrix((rtx - ltx) / w, (rty - lty) / w,
                                   (lbx - ltx) / h, (lby - lty) / h, ltx, lty);
                if (Math.Abs(m.GetDeterminant()) < 1e-12) continue;   // 退化（视线与影像面共面）⇒ 不画

                L.Rasters.Add((m, g.Img, w, h, g.Opacity));
                L.NRaster++;
            }

        // ── 面与线一起按深度排序（painter's algorithm）——同一个排序队列 ──
        var tris = new List<(double Depth, Point A, Point B, Point C, uint Argb)>();
        foreach (var kv in _faces)
        {
            if (IsBase(kv.Key) != baseLayer) continue;
            var g = kv.Value;
            for (int i = 0; i < g.N; i++)
            {
                int o = i * 9;
                Camera.Project(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2], out double ax, out double ay, out double da);
                Camera.Project(g.Xyz[o + 3], g.Xyz[o + 4], g.Xyz[o + 5], out double bx, out double by, out double db);
                Camera.Project(g.Xyz[o + 6], g.Xyz[o + 7], g.Xyz[o + 8], out double cx, out double cy, out double dcz);
                if (double.IsNaN(ax) || double.IsNaN(bx) || double.IsNaN(cx)) continue;

                uint baseArgb = i < g.Argb.Length ? g.Argb[i] : 0xFFFFFFFFu;
                tris.Add(((da + db + dcz) / 3.0, new Point(ax, ay), new Point(bx, by), new Point(cx, cy),
                          Shade(baseArgb, g.Xyz, o)));
            }
        }
        tris.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        foreach (var t in tris)
        {
            var geo = new StreamGeometry();
            using (var gctx = geo.Open())
            {
                gctx.BeginFigure(t.A, true);
                gctx.LineTo(t.B);
                gctx.LineTo(t.C);
                gctx.EndFigure(true);
            }
            // 面自带一圈同色描边：相邻三角之间的接缝（反走样会露白）就补上了
            L.Tris.Add((BrushOf(t.Argb), PenOf(t.Argb, 0.6), geo));
            L.NFace++;
        }

        // ── 线 ──
        if (baseLayer)
        {
            // 底图层：**同色同宽的连续段并成一条折线**，一堆一次 DrawGeometry（路网中线是连续折线，段间前后关系没有信息量）
            L.NSeg += BuildBaseLines(L);
        }
        else
        {
            var segs = new List<(double Depth, double X0, double Y0, double X1, double Y1, uint Argb, double W)>();
            foreach (var kv in _lines)
            {
                if (IsBase(kv.Key)) continue;
                var g = kv.Value;
                for (int i = 0; i < g.N; i++)
                {
                    int o = i * 6;
                    Camera.Project(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2], out double x0, out double y0, out double d0);
                    Camera.Project(g.Xyz[o + 3], g.Xyz[o + 4], g.Xyz[o + 5], out double x1, out double y1, out double d1);
                    if (double.IsNaN(x0) || double.IsNaN(x1)) continue;
                    uint c = i < g.Argb.Length ? g.Argb[i] : 0xFFFFFFFFu;
                    segs.Add(((d0 + d1) * 0.5, x0, y0, x1, y1, c, g.W));
                }
            }
            // 深度相同则**细线先画**：粗的（当前在搬的那条）压在上面
            segs.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.W.CompareTo(b.W));
            foreach (var s in segs)
            {
                L.Segs.Add((PenOf(s.Argb, s.W), new Point(s.X0, s.Y0), new Point(s.X1, s.Y1)));
                L.NSeg++;
            }
        }

        // ── 点：屏幕固定像素（缩放不变大）──
        foreach (var kv in _marks)
        {
            if (IsBase(kv.Key) != baseLayer) continue;
            var g = kv.Value;
            for (int i = 0; i < g.N; i++)
            {
                int o = i * 3;
                Camera.Project(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2], out double x, out double y, out _);
                if (double.IsNaN(x)) continue;
                double r = g.Px != null && i < g.Px.Length ? g.Px[i] : 5.0;
                uint c = i < g.Argb.Length ? g.Argb[i] : 0xFFFFFFFFu;
                L.Marks.Add((BrushOf(c), new Point(x, y), r * 0.5));
                L.NMark++;
            }
        }

        // ── 字：世界米高 → 像素；太小就省绘（画了也看不清，只是拖慢）──
        var face = new Typeface("Microsoft YaHei UI");
        foreach (var kv in _texts)
        {
            if (IsBase(kv.Key) != baseLayer) continue;
            var g = kv.Value;
            for (int i = 0; i < g.N; i++)
            {
                if (i >= g.Text.Count || string.IsNullOrEmpty(g.Text[i])) continue;
                int o = i * 3;
                Camera.Project(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2], out double x, out double y, out _);
                if (double.IsNaN(x)) continue;
                double hM = g.H != null && i < g.H.Length ? g.H[i] : 12.0;
                double px = hM * Camera.Scale;
                if (px < 6) continue;
                px = Math.Min(px, 42);
                uint c = i < g.Argb.Length ? g.Argb[i] : 0xFFFFFFFFu;
                var ft = new FormattedText(g.Text[i], System.Globalization.CultureInfo.CurrentCulture,
                                           FlowDirection.LeftToRight, face, px, BrushOf(c));
                L.Texts.Add((ft, new Point(x + 4, y - ft.Height * 0.5)));
                L.NLabel++;
            }
        }
    }

    /// <summary>底图层的线：按 (颜色, 线宽) 归堆，堆内把**首尾相接的段**接成折线。返回段数。</summary>
    private sealed class PenBucket { public StreamGeometry Geo = new(); public StreamGeometryContext Ctx = null!; public bool FigureOpen; }

    private int BuildBaseLines(Layer L)
    {
        var byPen = new Dictionary<(uint Argb, double W), PenBucket>();
        var order = new List<(uint Argb, double W)>();
        int n = 0;

        // ★ 按层内叠放序遍历（见 SetGroupOrder）：底图层不做深度排序，谁压谁只由这个序决定。
        foreach (var kv in _lines.OrderBy(k => OrderOf(k.Key)).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!IsBase(kv.Key)) continue;
            var g = kv.Value;
            double prevX1 = double.NaN, prevY1 = double.NaN;
            (uint, double) prevKey = default;
            bool open = false;

            for (int i = 0; i < g.N; i++)
            {
                int o = i * 6;
                Camera.Project(g.Xyz[o], g.Xyz[o + 1], g.Xyz[o + 2], out double x0, out double y0, out _);
                Camera.Project(g.Xyz[o + 3], g.Xyz[o + 4], g.Xyz[o + 5], out double x1, out double y1, out _);
                if (double.IsNaN(x0) || double.IsNaN(x1)) { open = false; continue; }

                uint c = i < g.Argb.Length ? g.Argb[i] : 0xFFFFFFFFu;
                var key = (c, g.W);

                if (!byPen.TryGetValue(key, out var e))
                {
                    e = new PenBucket();
                    e.Ctx = e.Geo.Open();
                    byPen[key] = e;
                    order.Add(key);
                }

                // 接得上（上一段的终点 == 本段的起点，同色同宽）就续折线，否则另起一条
                bool cont = open && key.Equals(prevKey)
                            && Math.Abs(x0 - prevX1) < 1e-6 && Math.Abs(y0 - prevY1) < 1e-6;
                if (!cont)
                {
                    if (e.FigureOpen) e.Ctx.EndFigure(false);
                    e.Ctx.BeginFigure(new Point(x0, y0), false);
                    e.FigureOpen = true;
                }
                e.Ctx.LineTo(new Point(x1, y1));

                prevX1 = x1; prevY1 = y1; prevKey = key; open = true;
                n++;
            }
        }

        foreach (var key in order)
        {
            var e = byPen[key];
            if (e.FigureOpen) e.Ctx.EndFigure(false);
            e.Ctx.Dispose();
            L.Polylines.Add((PenOf(key.Argb, key.W), e.Geo));
        }
        return n;
    }

    /// <summary>朗伯明暗：按**世界法线**与固定光向的夹角调亮度（光向固定在世界系，不跟相机转）。底光 0.42。</summary>
    private static uint Shade(uint argb, double[] xyz, int o)
    {
        double ux = xyz[o + 3] - xyz[o], uy = xyz[o + 4] - xyz[o + 1], uz = xyz[o + 5] - xyz[o + 2];
        double vx = xyz[o + 6] - xyz[o], vy = xyz[o + 7] - xyz[o + 1], vz = xyz[o + 8] - xyz[o + 2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12) return argb;
        nx /= len; ny /= len; nz /= len;

        const double lx = -0.40, ly = -0.45, lz = 0.80;
        double d = Math.Abs(nx * lx + ny * ly + nz * lz);      // 取绝对值：双面都受光，免得背面全黑
        double k = 0.42 + 0.58 * Math.Clamp(d, 0, 1);

        byte a = (byte)(argb >> 24);
        int r = (int)Math.Round(((argb >> 16) & 0xFF) * k);
        int g = (int)Math.Round(((argb >> 8) & 0xFF) * k);
        int b = (int)Math.Round((argb & 0xFF) * k);
        return ((uint)a << 24) | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF);
    }

    private readonly Dictionary<(uint, int), IPen> _penCache2 = new();

    private IPen PenOf(uint argb, double thickness)
    {
        var key = (argb, (int)Math.Round(thickness * 100));
        if (_penCache2.TryGetValue(key, out var p)) return p;
        p = new ImmutablePen(new ImmutableSolidColorBrush(ColorOf(argb)), thickness);
        _penCache2[key] = p;
        return p;
    }

    private static Color ColorOf(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private IBrush BrushOf(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out var b)) return b;
        b = new ImmutableSolidColorBrush(ColorOf(argb));
        _brushCache[argb] = b;
        return b;
    }
}

/// <summary>
/// 承载 <see cref="SimPanelOverlay"/> 的宿主控件：负责尺寸同步与鼠标交互
/// （右键拖 = 转方位/俯仰，中键拖 = 平移，滚轮 = 缩放）。
/// </summary>
public sealed class SimPanelHost : Control
{
    /// <summary>旋转灵敏度（度/像素）。界面上没暴露 —— 要调改这里。</summary>
    public double RotateSensitivity { get; set; } = 0.55;

    private readonly SimPanelOverlay _ov;
    private Point _last;
    private bool _rotating, _panning;

    public SimPanelHost(SimPanelOverlay ov)
    {
        _ov = ov ?? throw new ArgumentNullException(nameof(ov));
        _ov.Host = this;
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        // ★ 透明背景当命中区：没有它鼠标就是死的（自绘控件没有可命中的面积）
        Background = Brushes.Transparent;
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<SimPanelHost>();
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        dc.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));
        _ov.Draw(dc);
    }

    /// <summary>让本控件在布局里占满可用空间。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(w, h);
    }

    /// <summary>人有没有自己动过相机（转 / 平移 / 缩放）。取景自适应只在**没动过**的时候才允许再来一次。</summary>
    public bool UserAdjusted { get; private set; }

    /// <summary>把取景权交回给自动取景（人显式点了「充满」之后调）。</summary>
    public void ResetUserAdjusted() => UserAdjusted = false;

    /// <summary>视口尺寸变了（窗口最大化 / 拖边框 / 拖分隔条）。调用方据此决定要不要重新取景。</summary>
    public event EventHandler? ViewportResized;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _ov.Camera.Width = Bounds.Width;
        _ov.Camera.Height = Bounds.Height;
        // ★ 只改宽高不重新取景 ⇒ 尺度还是按**旧视口**算出来的那一个；调用方按 ViewportResized 决定要不要重新取景
        ViewportResized?.Invoke(this, EventArgs.Empty);
        _ov.RequestRender();
    }

    // ── 鼠标口径：与主视图 / 常见 CAD 一致 ── 右键拖 = 转视角　中键拖 = 平移　滚轮 = 缩放；左键留给将来的拾取

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (p.Properties.IsRightButtonPressed) { _rotating = true; _last = p.Position; e.Pointer.Capture(this); Focus(); e.Handled = true; }
        else if (p.Properties.IsMiddleButtonPressed) { _panning = true; _last = p.Position; e.Pointer.Capture(this); Focus(); e.Handled = true; }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Right) { _rotating = false; e.Pointer.Capture(null); }
        if (e.InitialPressMouseButton == MouseButton.Middle) { _panning = false; e.Pointer.Capture(null); }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_rotating && !_panning) return;
        var p = e.GetPosition(this);
        double dx = p.X - _last.X, dy = p.Y - _last.Y;
        _last = p;
        var c = _ov.Camera;
        UserAdjusted = true;

        if (_rotating)
        {
            // 灵敏度 0.55°/像素：一屏宽 ~1000px ⇒ 拖满屏转 550°，一次拖动足够绕过去看背面
            c.AzimuthDeg += dx * RotateSensitivity;
            c.TiltDeg = Math.Clamp(c.TiltDeg - dy * RotateSensitivity, 2, 90);
        }
        else
        {
            // ★ 平移：按**投影的精确反解**移相机中心，不做任何近似（tilt 的任何取值下都成立）
            c.MoveByScreen(-dx, dy);
        }
        _ov.RequestRender();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // ★ 缩放锚在**鼠标所在的那个点**上，不是画面中心
        var p = e.GetPosition(this);
        UserAdjusted = true;
        _ov.Camera.ZoomAt(p.X, p.Y, e.Delta.Y > 0 ? 1.18 : 1 / 1.18);
        _ov.RequestRender();
        e.Handled = true;
    }
}
