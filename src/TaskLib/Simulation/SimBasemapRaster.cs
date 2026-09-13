// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimBasemapRaster.cs（逐行对应；WPF BitmapSource/CroppedBitmap → Avalonia WriteableBitmap（裁剪直接按像素缓冲切））
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PitMine3D.Kylin.Shading;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>
/// 正射影像（GeoTIFF）→ 模拟面板的世界坐标底图。
///
/// <para><b>为什么不复用主视图那条路</b>：<c>IViewCapability.SetOrthophoto</c> 把影像贴到
/// **主三维视图**的地形上；而本窗口的原则是"全部画在本面板内"（不推主视图、不建实体、不占 Undo 栈）。
/// 所以这里走面板自己的栅格层 <see cref="SimPanelOverlay.SetRaster"/>：
/// 读像素 + 读地理配准，按世界坐标铺在矢量层底下。</para>
///
/// <para><b>影像只是底图</b>：它不参与任何量的计算，也不定位任何东西。</para>
/// </summary>
public sealed class SimBasemapRaster
{
    /// <summary>面板里的组名。</summary>
    public const string Group = "sim.basemap";

    /// <summary>降采样上限：面板本身就几百像素宽，铺一张 20000² 的图纯属浪费内存。</summary>
    public const int MaxDimension = 4096;

    public string Path { get; private set; } = "";
    public OrthophotoGeoRef? Geo { get; private set; }
    public Bitmap? Image { get; private set; }
    public string Message { get; private set; } = "";
    public List<string> Notes { get; } = new();

    public bool IsLoaded => Image != null && Geo != null;

    private double _x0, _y0, _x1, _y1;

    /// <summary>影像**实际有内容**那一块的覆盖范围（世界米）。未装载时全 NaN。四周补白已裁掉，见 <see cref="ContentBox"/>。</summary>
    public (double X0, double Y0, double X1, double Y1) Extent
        => Geo == null ? (double.NaN, double.NaN, double.NaN, double.NaN)
                       : (_x0, _y0, _x1, _y1);

    /// <summary>
    /// 内容紧包围盒（像素）。从四边往里扫，遇到第一条"有实际内容"的行/列就停。
    /// <para>判"有内容"的口径：该行/列里非补白像素占比 &gt; <c>Hit</c>。补白同时认纯白与纯黑，且要求 R≈G≈B。</para>
    /// </summary>
    internal static (int X, int Y, int W, int H, bool Trimmed) ContentBox(byte[] bgra, int w, int h)
    {
        const double Hit = 0.02;          // 一行/列里 2% 的像素有内容就算内容行
        const int Near = 12;              // 与 255/0 的容差
        const int Gray = 10;              // R/G/B 互差容差（超了就是彩色 ⇒ 不算补白）

        bool Blank(int i)
        {
            int o = i * 4;
            byte b = bgra[o], g = bgra[o + 1], r = bgra[o + 2], a = bgra[o + 3];
            if (a < 8) return true;                                   // 透明 = 补白
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx - mn > Gray) return false;                         // 彩色 ⇒ 有内容
            return mn >= 255 - Near || mx <= Near;                    // 近白 / 近黑
        }

        bool RowHas(int y)
        {
            int n = 0, need = (int)(w * Hit) + 1;
            for (int x = 0; x < w; x++) if (!Blank(y * w + x) && ++n >= need) return true;
            return false;
        }
        bool ColHas(int x)
        {
            int n = 0, need = (int)(h * Hit) + 1;
            for (int y = 0; y < h; y++) if (!Blank(y * w + x) && ++n >= need) return true;
            return false;
        }

        int top = 0, bottom = h - 1, left = 0, right = w - 1;
        while (top < bottom && !RowHas(top)) top++;
        while (bottom > top && !RowHas(bottom)) bottom--;
        while (left < right && !ColHas(left)) left++;
        while (right > left && !ColHas(right)) right--;

        int cw = right - left + 1, chh = bottom - top + 1;
        // 整幅都被判成补白（全黑/全白的图）⇒ 不裁，原样铺；裁到 0 会更糟
        if (cw < 8 || chh < 8) return (0, 0, w, h, false);
        return (left, top, cw, chh, left > 0 || top > 0 || cw < w || chh < h);
    }

    /// <summary>读一张 GeoTIFF。失败不抛 —— 底图缺了整个推演照常跑，只是没那张图。</summary>
    public bool Load(string path)
    {
        Notes.Clear();
        Path = ""; Geo = null; Image = null; Message = "";

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        { Message = $"影像文件不存在：{path}"; return false; }

        if (!OrthophotoLoader.TryReadPixelSize(path, out int sw, out int sh) || sw < 1 || sh < 1)
        { Message = $"读不出影像尺寸：{System.IO.Path.GetFileName(path)}"; return false; }

        var geo = GeoTiffInfo.Read(path, sw, sh, out string gerr);
        if (geo == null)
        {
            // 没有地理配准就**不铺** —— 按像素范围硬铺等于凭空给影像编一个位置
            Message = $"影像没有地理配准（{(gerr.Length > 0 ? gerr : "缺 ModelTiepoint/PixelScale")}），不铺底图。"
                    + "没有配准就按像素硬铺的话，图上看着严丝合缝、实际位置是编的。";
            return false;
        }

        var px = OrthophotoLoader.Load(path, MaxDimension, out string perr);
        if (px == null || px.Width < 1 || px.Height < 1)
        { Message = $"影像像素读不出来：{(perr.Length > 0 ? perr : "未知原因")}"; return false; }

        // ── 裁掉四周的补白 ── 航摄范围通常是斜的，成图时补成轴对齐矩形，四周是大片纯白（或纯黑）。
        //  按"从外往里扫，遇到第一行/列有实际内容就停"裁出内容的紧包围盒，世界四至同步按比例收。
        var (cx0, cy0, cw, ch, trimmed) = ContentBox(px.Bgra, px.Width, px.Height);
        double sx = (geo.MaxX - geo.MinX) / px.Width, sy = (geo.MaxY - geo.MinY) / px.Height;
        _x0 = geo.MinX + cx0 * sx;
        _x1 = geo.MinX + (cx0 + cw) * sx;
        _y1 = geo.MaxY - cy0 * sy;
        _y0 = geo.MaxY - (cy0 + ch) * sy;

        var bmp = ToBitmap(px.Bgra, px.Width, px.Height, cx0, cy0, cw, ch);

        Path = path; Geo = geo; Image = bmp;
        Message = $"影像底图：{System.IO.Path.GetFileName(path)}　"
                + $"{_x1 - _x0:0} × {_y1 - _y0:0} m　"
                + $"{bmp.PixelSize.Width}×{bmp.PixelSize.Height} px"
                + (px.Downsampled ? $"（原图 {px.SourceWidth}×{px.SourceHeight}，已降采样）" : "")
                + (trimmed ? $"；已裁掉四周补白（原栅格 {geo.MaxX - geo.MinX:0} × {geo.MaxY - geo.MinY:0} m）" : "");
        if (geo.CrsName is { Length: > 0 }) Notes.Add($"影像坐标系：{geo.CrsName}");
        return true;
    }

    /// <summary>BGRA 缓冲（可带裁剪窗）→ Avalonia 位图。</summary>
    private static unsafe Bitmap ToBitmap(byte[] bgra, int w, int h, int cx, int cy, int cw, int ch)
    {
        var wb = new WriteableBitmap(new PixelSize(cw, ch), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        fixed (byte* src = bgra)
        {
            for (int y = 0; y < ch; y++)
            {
                byte* s = src + ((long)(cy + y) * w + cx) * 4;
                byte* d = (byte*)fb.Address + (long)y * fb.RowBytes;
                Buffer.MemoryCopy(s, d, fb.RowBytes, (long)cw * 4);
            }
        }
        return wb;
    }

    public void Clear()
    {
        Path = ""; Geo = null; Image = null; Message = ""; Notes.Clear();
    }

    /// <summary>铺到面板上（<paramref name="show"/>=false 即清掉这一组）。</summary>
    public void PushTo(SimPanelOverlay panel, bool show, double z = 0, double opacity = 1.0)
    {
        if (panel == null) return;
        if (!show || !IsLoaded) { panel.SetRaster(Group, null, 0, 0, 0, 0); return; }
        // 裁过补白 ⇒ 按内容四至铺（与原版一致：CroppedBitmap 对应的就是 _x0.._x1 那一块）
        panel.SetRaster(Group, Image, _x0, _y0, _x1, _y1, z, opacity);
    }

    /// <summary>影像盖没盖住这些点。**必须报** —— 影像范围与矿区错开时，看着像渲染坏了，其实是两个范围不重合。</summary>
    public string CoverageOf(IReadOnlyList<(double X, double Y)> pts)
    {
        if (!IsLoaded) return "";
        if (pts == null || pts.Count == 0) return "";
        int inside = pts.Count(p => p.X >= Geo!.MinX && p.X <= Geo.MaxX
                                 && p.Y >= Geo.MinY && p.Y <= Geo.MaxY);
        if (inside == pts.Count) return $"影像盖住全部 {pts.Count} 个参照点。";
        return inside == 0
            ? $"⚠ 影像与本矿**完全不重合**：{pts.Count} 个参照点一个都不在影像范围内"
              + $"（影像 X {Geo!.MinX:0}…{Geo.MaxX:0}、Y {Geo.MinY:0}…{Geo.MaxY:0}）。"
              + "多半是影像的坐标系或投影带与台账不一致 —— 底图铺了也对不上，别拿它判位置。"
            : $"⚠ 影像只盖住 {inside}/{pts.Count} 个参照点，其余在影像范围外。";
    }
}
