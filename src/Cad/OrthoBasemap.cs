using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 正射影像底图（移植原 <c>TaskLib.Adjust.OrthophotoBasemap</c> + <c>PointCloudLib.Shading.OrthophotoConfig</c>）。
///
/// **原版做的是什么**：把当期航拍影像整张传成内核纹理（t2），再把着色模式切到「正射」（<c>ShadingMode=5</c>），
/// 于是**地表本身**按世界 XY 采影像上色 —— 目的是让演示"具备可核对性"：光看素色三角网，
/// 没人判断得出这块推进带压在哪条路、哪个平盘上。
///
/// **Kylin 侧怎么落**：地表在这里就是场景里的三角网与点云，而两者本来就各有
/// 「<c>RgbColors</c>=真实色 / <c>VertColors</c>(点云为 <c>Colors</c>)=当前显示色」这对字段。
/// 所以正射底图 = **按每个顶点/点的世界 XY 采影像，写进"当前显示色"**，
/// <see cref="Clear"/> 时再从真实色恢复。不需要新的 GL 通道。
///
/// **登记的差异**：原版是逐**片元**采纹理，本移植是逐**顶点/点**采样 —— 分辨率因此取决于
/// 三角网的疏密（稀网上影像会显得糊）。要逐片元就得给材质通道加一套 UV 顶点流（§三一九 那条
/// 材质通道是 P3_C3_N3，没有 UV），属另一件事，登记。
///
/// **一条纪律照搬原版**：装载完必须**当场核覆盖范围**。影像四至与场景都是绝对世界坐标，
/// 对象落在影像之外时画面上就是一片灰白，而人只会以为是"渲染坏了" —— 所以把落在框外的对象点名说出来。
/// </summary>
public static class OrthoBasemap
{
    /// <summary>影像外的兜底灰（同 Kylin 既有「正射着色」命令的口径）。</summary>
    public const float OutsideGray = 0.5f;

    /// <summary>一次装载的结果（成功与否都带一句给人看的话）。</summary>
    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        /// <summary>降级 / 覆盖范围提示，一条都不吞。</summary>
        public List<string> Notes = new();
        public int Entities, Vertices, Colored, Outside;
        /// <summary>命中率（采到影像内的顶点占比）。</summary>
        public double HitRatio => Vertices > 0 ? (double)Colored / Vertices : 0;
    }

    /// <summary>被着色对象的最小接口：给出世界点、收下颜色。三角网与点云各实现一份。</summary>
    public interface ITarget
    {
        string Name { get; }
        int Count { get; }
        (double x, double y) PointAt(int i);
        /// <summary>把当前显示色整份换掉。</summary>
        void SetColors(List<(float r, float g, float b)> colors);
        /// <summary>恢复到真实色/基色（清底图用）。</summary>
        void Restore();
        /// <summary>世界 XY 包围盒（覆盖范围核对用）。</summary>
        (double minX, double minY, double maxX, double maxY) Bounds();
    }

    private sealed class MeshTarget : ITarget
    {
        private readonly MeshEntity _m;
        public MeshTarget(MeshEntity m) => _m = m;
        public string Name => _m.Name;
        public int Count => _m.Verts.Count;
        public (double x, double y) PointAt(int i) => (_m.Verts[i].x, _m.Verts[i].y);
        public void SetColors(List<(float r, float g, float b)> c) { _m.VertColors = c; _m.Invalidate(); }
        // 有真实色就还原真实色; 没有就把逐顶点色撤掉、退回实体基色(同 MeshEntity 的既有语义)
        public void Restore() { _m.VertColors = _m.HasRgbColors ? new List<(float, float, float)>(_m.RgbColors!) : null; _m.Invalidate(); }
        public (double, double, double, double) Bounds()
        {
            var b = _m.Bounds;
            return (b.minX, b.minY, b.maxX, b.maxY);
        }
    }

    private sealed class CloudTarget : ITarget
    {
        private readonly PointCloudEntity _p;
        public CloudTarget(PointCloudEntity p) => _p = p;
        public string Name => _p.Name;
        public int Count => _p.Pts.Count;
        public (double x, double y) PointAt(int i) => (_p.Pts[i].x, _p.Pts[i].y);
        public void SetColors(List<(float r, float g, float b)> c) => _p.Colors = c;
        public void Restore() => _p.Colors = _p.HasRgb ? new List<(float, float, float)>(_p.RgbColors!) : null;
        public (double, double, double, double) Bounds()
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (x, y, _) in _p.Pts)
            {
                if (x < minX) minX = x; if (y < minY) minY = y;
                if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
            return minX > maxX ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
        }
    }

    /// <summary>把场景实体包成着色目标（只认三角网与点云 —— 线/文字这些没有"地表"可言）。</summary>
    public static List<ITarget> TargetsOf(IEnumerable<SceneEntity> entities)
    {
        var list = new List<ITarget>();
        foreach (var e in entities)
        {
            if (e is MeshEntity m && m.Verts.Count > 0) list.Add(new MeshTarget(m));
            else if (e is PointCloudEntity p && p.Pts.Count > 0) list.Add(new CloudTarget(p));
        }
        return list;
    }

    /// <summary>
    /// 采样器抽象 —— 只要"给世界 XY 回 RGB"与"四至"两件事。
    /// 抽出来是为了单测能喂一张合成影像，不必真去造 GeoTIFF 文件。
    /// </summary>
    public interface ISampler
    {
        (byte r, byte g, byte b)? SampleRgb(double x, double y);
        (double minX, double minY, double maxX, double maxY) Extent { get; }
    }

    /// <summary><see cref="GeoTiffSampler"/> 的适配。</summary>
    public sealed class GeoTiffAdapter : ISampler
    {
        private readonly GeoTiffSampler _s;
        public GeoTiffAdapter(GeoTiffSampler s) => _s = s;
        public (byte r, byte g, byte b)? SampleRgb(double x, double y) => _s.SampleRgb(x, y);
        public (double, double, double, double) Extent => (_s.MinX, _s.MinY, _s.MaxX, _s.MaxY);
    }

    /// <summary>两个 XY 框有没有交叠（含边界相接算交叠）。</summary>
    public static bool Overlaps((double minX, double minY, double maxX, double maxY) a,
                               (double minX, double minY, double maxX, double maxY) b)
        => a.minX <= b.maxX && a.maxX >= b.minX && a.minY <= b.maxY && a.maxY >= b.minY;

    /// <summary>
    /// 给一批目标贴底图。影像外的点着 <see cref="OutsideGray"/> 灰。
    /// 返回统计与提示；**完全落在影像外的对象逐个点名**（原版那条纪律）。
    /// </summary>
    public static Result Apply(ISampler sampler, IReadOnlyList<ITarget> targets)
    {
        var r = new Result();
        if (sampler == null) { r.Message = "没有可用的影像。"; return r; }
        if (targets == null || targets.Count == 0)
        {
            r.Message = "场景里没有可贴底图的三角网或点云 —— 先加载地表数据再来。";
            return r;
        }

        var ext = sampler.Extent;
        foreach (var t in targets)
        {
            var b = t.Bounds();
            if (!Overlaps(b, ext))
            {
                // 点名而不是默默灰掉: 一片灰白会被当成"渲染坏了"
                r.Notes.Add($"「{t.Name}」整个落在影像范围之外，未着色");
                r.Outside += t.Count;
                r.Vertices += t.Count;
                continue;
            }

            var colors = new List<(float r, float g, float b)>(t.Count);
            int hit = 0;
            for (int i = 0; i < t.Count; i++)
            {
                var (x, y) = t.PointAt(i);
                var c = sampler.SampleRgb(x, y);
                if (c is { } v) { colors.Add((v.r / 255f, v.g / 255f, v.b / 255f)); hit++; }
                else colors.Add((OutsideGray, OutsideGray, OutsideGray));
            }
            t.SetColors(colors);
            r.Entities++;
            r.Vertices += t.Count;
            r.Colored += hit;
            r.Outside += t.Count - hit;

            double ratio = t.Count > 0 ? (double)hit / t.Count : 0;
            if (ratio > 0 && ratio < 0.5)
                r.Notes.Add($"「{t.Name}」只有 {ratio * 100:0}% 落在影像内，其余按灰显示");
        }

        r.Ok = r.Entities > 0;
        r.Message = r.Ok
            ? $"影像底图：{r.Entities} 个对象 · {r.Colored:N0}/{r.Vertices:N0} 个点采到影像色（{r.HitRatio * 100:0.#}%）"
            : "影像底图：所有对象都落在影像范围之外 —— 请确认影像与图纸用的是同一套坐标系。";
        return r;
    }

    /// <summary>清底图：各对象恢复真实色/基色。</summary>
    public static int Clear(IReadOnlyList<ITarget> targets)
    {
        int n = 0;
        foreach (var t in targets) { t.Restore(); n++; }
        return n;
    }
}
