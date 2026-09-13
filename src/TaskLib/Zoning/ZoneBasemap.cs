// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneBasemap.cs（逐行对应；WPF BitmapSource → Avalonia WriteableBitmap）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PitMine3D.Kylin.Shading;

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  正射影像(GeoTIFF)的**二维底图** —— 划作业区就该对着航拍图划。
//
//  与 TaskLib.Adjust.OrthophotoBasemap 的分工：那个把影像贴在三维地表上（看演示用）；
//  本类只解码成一张位图，画在本窗口的平面画布上（划区用）。两者共用同一套解析（GeoTiffInfo + OrthophotoLoader）。
//
//  分辨率上限 4096：平面画布只在屏幕上显示，降的是显示分辨率，四至与配准没有变。
//  不做坐标系转换：影像必须与工程同一套投影，装载完必须逐区域核一遍覆盖关系并点名。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>装载好的二维正射底图。</summary>
public sealed class ZoneBasemap
{
    /// <summary>平面画布用的解码位图。</summary>
    public Bitmap Image { get; init; } = null!;
    /// <summary>地理配准（四至为绝对世界坐标，米）。</summary>
    public OrthophotoGeoRef Geo { get; init; } = null!;
    public string Path { get; init; } = "";
    /// <summary>界面直接显示的一行状态。</summary>
    public string Message { get; init; } = "";
    /// <summary>降级 / 配准 / 覆盖范围提示，一条都不吞。</summary>
    public List<string> Notes { get; init; } = new();

    public double MinX => Geo.MinX;
    public double MinY => Geo.MinY;
    public double MaxX => Geo.MaxX;
    public double MaxY => Geo.MaxY;

    /// <summary>一个世界点是否落在影像覆盖内。</summary>
    public bool Covers(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    /// <summary>某条环被影像覆盖的顶点比例（0..1）。</summary>
    public double CoverageOf(IReadOnlyList<ZonePoint> ring)
        => ring.Count == 0 ? 0 : (double)ring.Count(p => Covers(p.X, p.Y)) / ring.Count;
}

/// <summary>底图装载失败时的结果（成功时 <see cref="Map"/> 非空）。</summary>
public sealed class ZoneBasemapResult
{
    public ZoneBasemap? Map { get; init; }
    public bool Ok => Map != null;
    public string Message { get; init; } = "";
    public List<string> Notes { get; init; } = new();
}

public static class ZoneBasemapLoader
{
    /// <summary>平面画布的解码边长上限（屏幕显示用，不是 GPU 纹理上限）。</summary>
    public const int MaxDimension = 4096;

    /// <summary>选文件对话框的过滤串（与三维底图同一套）。</summary>
    public const string FileFilter =
        "正射影像 (*.tif;*.tiff)|*.tif;*.tiff|所有图像 (*.tif;*.tiff;*.png;*.jpg)|*.tif;*.tiff;*.png;*.jpg|所有文件 (*.*)|*.*";

    /// <summary>装载一张带地理配准的影像。永不抛。</summary>
    public static ZoneBasemapResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new ZoneBasemapResult { Message = "影像文件不存在。" };

        // ① 地理配准（只读文件头，上百 MB 也是毫秒级）
        int pw = 0, ph = 0;
        try { OrthophotoLoader.TryReadPixelSize(path, out pw, out ph); } catch { }

        OrthophotoGeoRef? geo;
        string geoErr = "";
        try { geo = GeoTiffInfo.Read(path, pw, ph, out geoErr); }
        catch (Exception ex) { geo = null; geoErr = ex.Message; }

        if (geo == null || !(geo.PixelSizeX > 0) || !(geo.MaxX > geo.MinX) || !(geo.MaxY > geo.MinY))
            return new ZoneBasemapResult
            {
                Message = $"影像没有可用的地理配准：{(geoErr.Length > 0 ? geoErr : "四至解析失败")}",
                Notes =
                {
                    "划作业区要把画布上的点换算成**世界坐标**入库，所以影像必须知道自己贴在哪一块 —— "
                    + "没有 GeoTIFF 标签就得有同名 .tfw 世界文件。不会拿区域包围盒去凑一个四至：那样画出来的区域坐标是错的。",
                    "常见成因：① 导出时没勾「写入 GeoTIFF 标签」；② BigTIFF（>4GB）需先转经典 TIFF；"
                    + "③ 影像带旋转（ModelTransformation 含旋转项），需先在 GIS 里重采样为正北。",
                },
            };

        // ② 解码像素
        OrthophotoPixels? px;
        string decErr = "";
        try { px = OrthophotoLoader.Load(path, MaxDimension, out decErr); }
        catch (Exception ex) { px = null; decErr = ex.Message; }

        if (px == null || px.Width <= 0 || px.Height <= 0)
            return new ZoneBasemapResult { Message = $"影像解码失败：{(decErr.Length > 0 ? decErr : "解码结果为空")}" };

        Bitmap bmp;
        try { bmp = ToBitmap(px.Bgra, px.Width, px.Height); }
        catch (Exception ex) { return new ZoneBasemapResult { Message = $"位图构建失败：{ex.Message}" }; }

        double km2 = (geo.MaxX - geo.MinX) * (geo.MaxY - geo.MinY) / 1e6;
        var notes = new List<string>
        {
            $"配准来源：{geo.Source}"
                + (string.IsNullOrEmpty(geo.CrsName) ? "（影像未写坐标系名）" : $"　坐标系：{geo.CrsName}")
                + " —— 本程序**不做坐标系转换**，影像必须与工程同一套投影，否则画出来的区域整体平移。",
            $"影像四至 X[{geo.MinX:0.#} ~ {geo.MaxX:0.#}] Y[{geo.MinY:0.#} ~ {geo.MaxY:0.#}]（绝对世界坐标 m）。",
        };
        if (px.Downsampled)
            notes.Add($"已按画布上限降采样到 {px.Width}×{px.Height}（原 {px.SourceWidth}×{px.SourceHeight}）："
                    + "降的是显示分辨率，四至与配准没有变；判读细部请以原图为准。");

        var map = new ZoneBasemap
        {
            Image = bmp,
            Geo = geo,
            Path = path,
            Message = $"底图：{System.IO.Path.GetFileName(path)}　{px.Width}×{px.Height}"
                    + $"　{geo.PixelSizeX:0.###} m/像素　覆盖 {km2:0.##} km²",
            Notes = notes,
        };

        // 装成功即记进工程配置：在哪个窗口选的都算配上了，下一个需要底图的功能直接就有
        try { OrthophotoConfig.Remember(path); } catch { }

        return new ZoneBasemapResult { Map = map, Message = map.Message, Notes = notes };
    }

    private static unsafe Bitmap ToBitmap(byte[] bgra, int w, int h)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        fixed (byte* src = bgra)
        {
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * w * 4, (byte*)fb.Address + (long)y * fb.RowBytes, fb.RowBytes, (long)w * 4);
        }
        return wb;
    }

    /// <summary>逐区域核覆盖范围，把全落框外 / 部分出框的点名。返回的每一条都直接显示给人看。</summary>
    public static List<string> Coverage(ZoneBasemap map, IReadOnlyList<ZoneRecord> zones)
    {
        var lines = new List<string>();
        if (zones.Count == 0) return lines;

        var outside = new List<string>();
        var partial = new List<string>();
        int full = 0;

        foreach (var z in zones)
        {
            if (z.Ring.Count == 0) continue;
            int hit = z.Ring.Count(p => map.Covers(p.X, p.Y));
            if (hit == 0) outside.Add(z.Name);
            else if (hit < z.Ring.Count) partial.Add($"{z.Name}（{hit}/{z.Ring.Count} 点在框内）");
            else full++;
        }

        if (full > 0) lines.Add($"覆盖核对：{full} 块区域完整落在影像范围内。");
        if (partial.Count > 0) lines.Add($"⚠ 部分出框：{string.Join("、", partial)}。");
        if (outside.Count > 0)
            lines.Add($"⚠ **完全落在影像之外**：{string.Join("、", outside)} —— "
                    + "多半是影像与工程不在同一套坐标系（或影像只拍了场地的一部分），请先核对配准再划区。");
        return lines;
    }
}
