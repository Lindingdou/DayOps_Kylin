// 忠实移植自原 PitMine3D Modules/TaskLib/Adjust/OrthophotoBasemap.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Shading;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.TaskLib.Adjust;

// ─────────────────────────────────────────────────────────────────────────────
//  正射影像（GeoTIFF）底图 —— 让「每个区域的作业演示」踩在真实航拍地表上。
//
//  ── 为什么演示需要它 ──
//  推演层体本身只有形状与体积是真的；摆在素色三角网上，看图的人无从判断
//  「这块推进带到底压在哪条路、哪个平盘、哪个坡面上」。贴上当期航拍影像之后，
//  区域轮廓与实际地物对得上，演示才具备可核对性。
//
//  ── 全流程复用 PointCloudLib 已有的两块，不另写一套解析 ──
//    · GeoTiffInfo.Read       ：只读文件头（IFD + 几个 double），上百 MB 也是毫秒级，
//                               优先 GeoTIFF 标签（33922 tiepoint + 33550 pixelScale），退 .tfw 世界文件；
//    · OrthophotoLoader.Load  ：WPF 解码 → BGRA8，只按 GPU 纹理边长硬上限降采样。
//  再经 IViewCapability.SetOrthophoto 上传 + SetShadingMode(5) 切到正射着色。
//  影像是**全局唯一一张、内存态**（内核 t2 纹理），所以本类是单例状态而不是逐区域一张。
//
//  ── 一条纪律：覆盖范围要当场核 ──
//  影像四至与区域轮廓都是绝对世界坐标。区域落在影像之外时画面上就是一片空白，
//  而人会以为是「渲染坏了」。所以装载完就逐区域判一次包含关系，把落在框外的区域点名说出来。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>底图装载结果（成功与否都带一句给人看的话）。</summary>
public sealed class BasemapResult
{
    public bool Ok { get; set; }
    /// <summary>界面直接显示的一行状态。</summary>
    public string Message { get; set; } = "";
    /// <summary>降级 / 覆盖范围提示，一条都不吞。</summary>
    public List<string> Notes { get; set; } = new();
    public OrthophotoGeoRef? Geo { get; set; }
    public string Path { get; set; } = "";
}

/// <summary>
/// 正射影像底图的装载/清除。全局单张（对齐内核 t2 纹理的语义），线程亲和 UI。
/// 所有方法永不抛。
/// </summary>
public static class OrthophotoBasemap
{
    /// <summary>正射影像着色模式（与内核 <c>AcGi::ShadingMode</c> 对齐：5 = Orthophoto）。</summary>
    public const int ShadingModeOrthophoto = 5;
    /// <summary>素色平滑着色（清底图后退回这一档）。</summary>
    public const int ShadingModeSmooth = 1;

    /// <summary>当前已装载的影像路径（空 = 没装）。</summary>
    public static string CurrentPath { get; private set; } = "";
    /// <summary>当前影像的地理配准（null = 没装）。</summary>
    public static OrthophotoGeoRef? CurrentGeo { get; private set; }

    public static bool IsLoaded => CurrentGeo != null && CurrentPath.Length > 0;

    /// <summary>选文件对话框用的过滤串。</summary>
    public const string FileFilter =
        "正射影像 (*.tif;*.tiff)|*.tif;*.tiff|所有图像 (*.tif;*.tiff;*.png;*.jpg)|*.tif;*.tiff;*.png;*.jpg|所有文件 (*.*)|*.*";

    /// <summary>
    /// 装载一张正射影像并切到正射着色。
    /// <paramref name="regions"/> 传入当前区域集时，会逐区域核一遍覆盖范围并把落在框外的点名。
    /// </summary>
    public static BasemapResult Load(string path, SimRegionSet? regions = null)
    {
        var res = new BasemapResult { Path = path ?? "" };

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            res.Message = "影像文件不存在。";
            return res;
        }

        var view = SimHost.View;
        if (view == null)
        {
            res.Message = "宿主未注入视图能力（IViewCapability），无法上传底图。";
            res.Notes.Add("这一条是接线问题不是数据问题：TaskLibPlugin.Initialize 里已调 SimHost.Inject(context.Capabilities)，"
                        + "若仍取不到说明宿主没提供 IViewCapability。甘特与环节明细不受影响，只是三维少了底图。");
            return res;
        }

        // ── ① 地理配准（只读文件头）──
        int pw = 0, ph = 0;
        try { OrthophotoLoader.TryReadPixelSize(path, out pw, out ph); } catch { }

        OrthophotoGeoRef? geo;
        string geoErr = "";
        try { geo = GeoTiffInfo.Read(path, pw, ph, out geoErr); }
        catch (Exception ex) { geo = null; geoErr = ex.Message; }

        if (geo == null || !(geo.PixelSizeX > 0) || !(geo.MaxX > geo.MinX) || !(geo.MaxY > geo.MinY))
        {
            res.Message = $"影像没有可用的地理配准：{(geoErr.Length > 0 ? geoErr : "四至解析失败")}";
            res.Notes.Add("正射底图必须知道自己贴在世界坐标的哪一块 —— 没有 GeoTIFF 标签就得有同名 .tfw 世界文件。"
                        + "**不会**拿区域包围盒去凑一个四至：那样贴出来的影像与地物错位，比不贴更误导。");
            res.Notes.Add("常见成因：① 导出时没勾「写入 GeoTIFF 标签」；② BigTIFF（>4GB）需先转经典 TIFF；"
                        + "③ 影像带旋转（ModelTransformation 含旋转项），需先在 GIS 里重采样为正北。");
            return res;
        }

        // ── ② 解码像素（只按 GPU 硬上限降采样）──
        int maxDim;
        try { maxDim = view.GetMaxOrthophotoDimension(); }
        catch { maxDim = OrthophotoLoader.FallbackMaxDimension; }

        OrthophotoPixels? px;
        string decErr = "";
        try { px = OrthophotoLoader.Load(path, maxDim, out decErr); }
        catch (Exception ex) { px = null; decErr = ex.Message; }

        if (px == null)
        {
            res.Message = $"影像解码失败：{decErr}";
            return res;
        }

        // ── ③ 上传 + 切正射着色 ──
        bool ok;
        try { ok = view.SetOrthophoto(px.Bgra, px.Width, px.Height, geo.MinX, geo.MinY, geo.MaxX, geo.MaxY); }
        catch (Exception ex) { ok = false; decErr = ex.Message; }

        if (!ok)
        {
            res.Message = "影像上传失败（像素为空 / 四至退化 / 引擎未就绪）"
                        + (decErr.Length > 0 ? $"：{decErr}" : "。");
            return res;
        }

        try { view.SetShadingMode(ShadingModeOrthophoto); view.RequestRender(); } catch { }

        CurrentPath = path;
        CurrentGeo = geo;

        // 装成功即记进工程配置：这样"配置影像"不必是一次专门的操作 ——
        // 在哪个窗口选的都算配上了，下一个需要底图的功能直接就有（见 OrthophotoConfig）。
        try { OrthophotoConfig.Remember(path); } catch { }

        res.Ok = true;
        res.Geo = geo;
        double km2 = (geo.MaxX - geo.MinX) * (geo.MaxY - geo.MinY) / 1e6;
        res.Message = $"底图已贴：{Path.GetFileName(path)}　{px.Width}×{px.Height}"
                    + (px.Downsampled ? $"（原 {px.SourceWidth}×{px.SourceHeight}，已按 GPU 上限 {maxDim} 降采样）" : "")
                    + $"　{geo.PixelSizeX:0.###} m/像素　覆盖 {km2:0.##} km²";

        res.Notes.Add($"配准来源：{geo.Source}"
                    + (string.IsNullOrEmpty(geo.CrsName) ? "（影像未写坐标系名）" : $"　坐标系：{geo.CrsName}")
                    + " —— 本程序**不做坐标系转换**，影像必须与工程同一套投影，否则会整体平移。");
        res.Notes.Add($"影像四至 X[{geo.MinX:0.#} ~ {geo.MaxX:0.#}] Y[{geo.MinY:0.#} ~ {geo.MaxY:0.#}]（绝对世界坐标 m）。");
        if (px.Downsampled)
            res.Notes.Add($"⚠ 已降采样到 {px.Width}×{px.Height}：判读细部（车辙、小型设备）请以原图为准，"
                        + "这里降的是显示分辨率，四至与配准没有变。");
        res.Notes.Add($"显存占用约 {px.Bytes / 1024.0 / 1024.0:0} MB（BGRA8 + mip）。");
        res.Notes.Add("着色已切到「正射影像」(mode 5)：这是**全局**模式，场景内所有 2.5D 三角网一起变。"
                    + "没有三角网时只贴不出东西 —— 底图是贴在面上的，不是贴在空气里。");

        // ── ④ 覆盖范围核对：区域落在影像外，图上就是空白，必须点名 ──
        AppendCoverage(res, regions, geo);
        return res;
    }

    /// <summary>清除底图并退回素色平滑着色。</summary>
    public static string Clear()
    {
        var view = SimHost.View;
        if (view == null) { CurrentPath = ""; CurrentGeo = null; return "宿主未注入视图能力，无需清除。"; }
        try
        {
            view.ClearOrthophoto();
            view.SetShadingMode(ShadingModeSmooth);
            view.RequestRender();
        }
        catch { }
        CurrentPath = ""; CurrentGeo = null;
        return "底图已清除，着色退回素色平滑。";
    }

    /// <summary>一个世界点是否落在影像覆盖内。没装底图时恒 false。</summary>
    public static bool Covers(double x, double y)
    {
        var g = CurrentGeo;
        return g != null && x >= g.MinX && x <= g.MaxX && y >= g.MinY && y <= g.MaxY;
    }

    /// <summary>某块区域被影像覆盖的顶点比例（0..1）。没装底图返回 0。</summary>
    public static double CoverageOf(SimRegion r)
    {
        var g = CurrentGeo;
        if (g == null || r == null || r.Ring.Count == 0) return 0;
        int hit = r.Ring.Count(p => p.X >= g.MinX && p.X <= g.MaxX && p.Y >= g.MinY && p.Y <= g.MaxY);
        return (double)hit / r.Ring.Count;
    }

    /// <summary>逐区域核覆盖范围，把全落框外/部分出框的区域点名写进 Notes。</summary>
    private static void AppendCoverage(BasemapResult res, SimRegionSet? regions, OrthophotoGeoRef geo)
    {
        if (regions == null || regions.IsEmpty) return;

        var outside = new List<string>();
        var partial = new List<string>();
        int full = 0;

        foreach (var r in regions.Regions)
        {
            if (r.Ring.Count == 0) continue;
            int hit = r.Ring.Count(p => p.X >= geo.MinX && p.X <= geo.MaxX && p.Y >= geo.MinY && p.Y <= geo.MaxY);
            if (hit == 0) outside.Add(r.Name);
            else if (hit < r.Ring.Count) partial.Add($"{r.Name}（{hit}/{r.Ring.Count} 点在框内）");
            else full++;
        }

        if (full > 0) res.Notes.Add($"覆盖核对：{full} 块区域完整落在影像范围内。");
        if (partial.Count > 0)
            res.Notes.Add($"⚠ 部分出框：{string.Join("、", partial)} —— 出框那一侧的演示画面下没有底图。");
        if (outside.Count > 0)
            res.Notes.Add($"⚠ **完全落在影像之外**：{string.Join("、", outside)} —— 这些区域的演示看不到底图。"
                        + "多半是影像与工程不在同一套坐标系（或影像只拍了场地的一部分），请先核对配准。");

        if (regions.Regions.Count > 0 && regions.AllSynthetic)
            res.Notes.Add("⚠ 当前区域轮廓是**示意图形**（未圈画可采区域），其坐标是本包生成的，"
                        + "与影像的位置关系没有意义 —— 上面这条覆盖核对对示意图形不作数。");
    }
}
