// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneView.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  平面画布的世界 ↔ 屏幕变换。
//
//  ── 为什么单独一个类 ──
//  划区这件事的**全部风险**都压在这条换算上：画布上点一下，换出来的世界坐标要直接入
//  mineable_region，而推演、路网裁剪都拿它当真边界。换错了没有任何一处会报错 ——
//  区域照样入库、照样显示、照样进推演，只是位置整个是偏的。
//  埋在窗口的 private 字段里就只能靠肉眼看图验，抽出来才能拿真影像四至做往返自检。
//
//  约定：世界 Y 向上，屏幕 Y 向下（所以 Sy 用 MaxY − wy）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>画布视图变换（世界米 ↔ 屏幕 DIP）。</summary>
public sealed class ZoneView
{
    /// <summary>屏幕左边界对应的世界 X。</summary>
    public double MinX { get; private set; }
    /// <summary>屏幕上边界对应的世界 Y。</summary>
    public double MaxY { get; private set; }
    /// <summary>每米多少屏幕像素。</summary>
    public double Scale { get; private set; } = 1;
    /// <summary>视口尺寸（DIP）。</summary>
    public double ViewW { get; private set; }
    public double ViewH { get; private set; }
    /// <summary>已经 Fit 过一次、可以开始换算了。</summary>
    public bool Ready { get; private set; }

    /// <summary>
    /// 底图最长边的世界跨度 m（0 = 没有底图）。缩放上限按它算：位图铺到画布上是一个 WPF
    /// 元素，边长过万后布局/渲染明显发卡，所以钳到「影像最长边不超过 <see cref="MaxImagePx"/> 像素」。
    /// </summary>
    public double ImageSpanM { get; set; }

    /// <summary>底图元素允许的最大边长（像素）。</summary>
    public const double MaxImagePx = 30000;
    /// <summary>缩放绝对上限：200 px/m = 5 mm 一像素，够任何细部。</summary>
    public const double MaxScale = 200;
    public const double MinScale = 1e-4;

    /// <summary>视口留边比例（Fit 时四周各留一点，免得轮廓贴边）。</summary>
    private const double FitFill = 0.94;

    public void SetViewport(double w, double h)
    {
        ViewW = w;
        ViewH = h;
    }

    /// <summary>视口够大到可以布局（太小时 Fit 无意义）。</summary>
    public bool ViewportUsable => ViewW >= 20 && ViewH >= 20;

    // ── 换算 ────────────────────────────────────────────────────────────────

    public double Sx(double wx) => (wx - MinX) * Scale;
    public double Sy(double wy) => (MaxY - wy) * Scale;
    public double Wx(double sx) => MinX + sx / Scale;
    public double Wy(double sy) => MaxY - sy / Scale;

    /// <summary>当前分辨率（米/像素），状态栏读数用。</summary>
    public double MetersPerPixel => Scale > 0 ? 1 / Scale : double.NaN;

    // ── 视野 ────────────────────────────────────────────────────────────────

    /// <summary>把给定世界矩形装进视口（居中 + 留边）。视口太小时不动。</summary>
    public bool Fit(double minX, double minY, double maxX, double maxY)
    {
        if (!ViewportUsable) return false;
        double dx = Math.Max(1e-6, maxX - minX), dy = Math.Max(1e-6, maxY - minY);
        Scale = Math.Min(ViewW / dx, ViewH / dy) * FitFill;
        ClampScale();
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        MinX = cx - ViewW / Scale / 2;
        MaxY = cy + ViewH / Scale / 2;
        Ready = true;
        return true;
    }

    /// <summary>以光标处为不动点缩放。返回是否真的变了。</summary>
    public bool ZoomAt(double screenX, double screenY, double factor)
    {
        if (!Ready) return false;
        double wx = Wx(screenX), wy = Wy(screenY);
        double before = Scale;
        Scale *= factor;
        ClampScale();
        if (Math.Abs(Scale - before) < 1e-12) return false;
        MinX = wx - screenX / Scale;
        MaxY = wy + screenY / Scale;
        return true;
    }

    /// <summary>从一次拖拽的起点状态平移（拖了多少屏幕像素）。</summary>
    public void PanFrom(double anchorMinX, double anchorMaxY, double dxPx, double dyPx)
    {
        MinX = anchorMinX - dxPx / Scale;
        MaxY = anchorMaxY + dyPx / Scale;
    }

    private void ClampScale()
    {
        if (double.IsNaN(Scale) || double.IsInfinity(Scale) || Scale <= 0) Scale = 1;
        if (ImageSpanM > 1e-6) Scale = Math.Min(Scale, MaxImagePx / ImageSpanM);
        Scale = Math.Max(MinScale, Math.Min(Scale, MaxScale));
    }

    // ── 框定 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 一次矩形拖框 → 四个角的世界坐标（逆时针，从左下开始）。
    /// <para>起止点任意方向拖都归一化成同一个矩形；退化（宽或高不足 <paramref name="minSideM"/> 米）返回空表 ——
    /// 手一抖点一下就入库一个零面积区域，比不入库更难查。</para>
    /// </summary>
    public List<(double X, double Y)> RectFromDrag(double sx0, double sy0, double sx1, double sy1,
                                                   double minSideM = 1.0)
    {
        var pts = new List<(double X, double Y)>();
        if (!Ready) return pts;

        double ax = Wx(sx0), ay = Wy(sy0), bx = Wx(sx1), by = Wy(sy1);
        double minX = Math.Min(ax, bx), maxX = Math.Max(ax, bx);
        double minY = Math.Min(ay, by), maxY = Math.Max(ay, by);
        if (maxX - minX < minSideM || maxY - minY < minSideM) return pts;

        pts.Add((minX, minY));
        pts.Add((maxX, minY));
        pts.Add((maxX, maxY));
        pts.Add((minX, maxY));
        return pts;
    }
}
