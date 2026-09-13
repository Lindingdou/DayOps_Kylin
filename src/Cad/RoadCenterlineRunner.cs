using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>道路中心线提取过程回显的严重度（对应原版 CommandLineSeverity）。</summary>
public enum RoadCenterlineSeverity { Info, Success, Warn, Error }

/// <summary>「提取道路中心线」一次运行的参数 —— 对话框收上来的那几项（原 RoadCenterlineDialog 的 Options + 连通/裁剪开关）。</summary>
public sealed class RoadCenterlineRunOptions
{
    /// <summary>骨架路由法参数（栅格精度/坡度上限/最小路宽/撒点数/面平滑/挡墙）。</summary>
    public RoadSkeletonOptions Skeleton = new();

    /// <summary>连通增强（焊接近失端点 + 桥接断头 + T 形打断）。</summary>
    public bool EnableConnect = true;

    /// <summary>自动断头连接距离上限 (m)。</summary>
    public double ConnectDist = 25.0;

    /// <summary>作业区域限制：非空则台阶线预筛 + 输出精裁到这些区域并集内；null/空 = 全图生成。</summary>
    public IReadOnlyList<RoadClipRegion>? ClipRegions;
}

/// <summary>一次运行的结果：中心线（含手动补充、已连通）+ 过程回显。</summary>
public sealed class RoadCenterlineRunResult
{
    public bool Ok;
    public List<double[]> Lines = new();
    /// <summary>过程回显（按原版 Echo 顺序），上层逐条写命令行/信息栏。</summary>
    public List<(RoadCenterlineSeverity Level, string Text)> Notes = new();
    /// <summary>用到了哪种插值面（TIN / 台阶线块状面），写进最终回显。</summary>
    public string FaceSource = "";
    /// <summary>并入的手动补充线条数。</summary>
    public int ManualCount;

    internal void Note(RoadCenterlineSeverity lv, string t) => Notes.Add((lv, t));
}

/// <summary>
/// 道路中心线提取（骨架路由法）命令逻辑 —— 忠实移植原 PointCloudLib.RoadCenterline.RoadCenterlineRunner.RunAsync
/// 对话框之后的那段（收台阶线 → 高程守卫 → 区域预筛 → 骨架路由提取 → 区域精裁 → 连通增强/并入补充线）。
/// 场景读写（选集 / 图层 / TIN 实体 / 入库）留在 MainWindow 侧，这里只吃扁平折线，纯逻辑、可单测。
/// </summary>
public static class RoadCenterlineRunner
{
    public const string OutputLayer = "点云_道路中心线";

    /// <param name="benchLines">台阶线（扁平 [x,y,z,...]，须带真实高程）。</param>
    /// <param name="manualLines">运行前选中、作"手动补充线路"并入并连通的多段线（不作台阶线）。</param>
    /// <param name="meshVerts">场景三角网 TIN 顶点 [x,y,z,...]；null = 无 TIN，退回台阶线块状面。</param>
    /// <param name="meshTris">TIN 三角索引 [i0,i1,i2,...]。</param>
    public static RoadCenterlineRunResult Run(IReadOnlyList<double[]> benchLines, IReadOnlyList<double[]> manualLines,
                                              double[]? meshVerts, int[]? meshTris, RoadCenterlineRunOptions opt)
    {
        var res = new RoadCenterlineRunResult();
        manualLines ??= Array.Empty<double[]>();
        var skopt = opt.Skeleton;
        var bench = new List<double[]>(benchLines);

        if (bench.Count < 2)
        {
            res.Note(RoadCenterlineSeverity.Warn,
                "图纸中未找到足够多段线作为台阶线。请确认已导入/绘制台阶线（坡顶/坡底/台阶边线），或先框选台阶线再运行。");
            return res;
        }

        // 高程退化守卫：台阶线若是 2D（无 Z），无法区分平盘/立面。
        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var ln in bench)
            for (int i = 2; i < ln.Length; i += 3) { if (ln[i] < zMin) zMin = ln[i]; if (ln[i] > zMax) zMax = ln[i]; }
        if (zMax - zMin < 1.0)
        {
            res.Note(RoadCenterlineSeverity.Warn,
                $"台阶线高程范围仅 {zMax - zMin:F2} m（疑似 2D 无高程）。需要真实 Z 值区分平盘与立面；请确认台阶线带高程（3D 多段线），或先赋节点高程。");
            return res;
        }

        // ── 作业区域限制（需求07）：构建裁剪环 → 输入预筛台阶线（输出再精裁）──
        List<double[]>? clipRings = null;
        if (opt.ClipRegions is { Count: > 0 })
        {
            clipRings = new List<double[]>();
            foreach (var r in opt.ClipRegions)
                if (r.RingXy.Length >= 6) clipRings.Add(r.RingXy);   // ≥3 顶点
        }
        if (clipRings is { Count: > 0 })
        {
            var bboxes = RegionClip.ExpandedBBoxes(clipRings, 120.0);   // 外扩 120m（≥配对可达，安全）
            int before = bench.Count;
            bench = RegionClip.FilterBenchLinesByBBox(bench, bboxes);
            // 本次到底按哪几块裁的，逐块报出来 —— 只报块数的话，"某条路没提出来"事后分不清
            // 是范围被缩了还是算法没识别（下面那条 <2 的警告同理）。
            var names = new List<string>();
            int offLedger = 0;
            foreach (var r in opt.ClipRegions!)
            {
                if (r.RingXy.Length < 6) continue;
                names.Add(r.Name.Length > 0 ? r.Name : "(未命名)");
                if (!r.Active) offLedger++;
            }
            res.Note(RoadCenterlineSeverity.Info,
                $"作业区域限制：按 {names.Count} 块区域裁剪（{string.Join("、", names)}）"
                + (offLedger > 0 ? $"，其中 {offLedger} 块是本期未选定的（本次临时用，台账未改）" : "")
                + $"；台阶线 {before} → {bench.Count} 条（区域内/邻接）。");
            if (bench.Count < 2)
            {
                res.Note(RoadCenterlineSeverity.Warn, "所选作业区域内的台阶线不足（<2 条）。请确认区域范围覆盖到台阶线，或在对话框取消区域限制。");
                return res;
            }
        }

        // 读场景三角网(TIN)做插值面（推荐先建三角网）；无 TIN 退回台阶线块状面。
        bool useTin = meshVerts != null && meshTris != null && meshVerts.Length >= 9 && meshTris.Length >= 3;
        if (!useTin) { meshVerts = null; meshTris = null; }
        res.FaceSource = useTin ? $"TIN插值面({meshVerts!.Length / 3}点)" : "台阶线块状面(建议先建三角网更佳)";
        res.Note(RoadCenterlineSeverity.Info, $"道路中心线提取(骨架路由法)：计算中（台阶线 {bench.Count} 条 / {res.FaceSource}）…");
        var result = RoadSkeletonExtractor.Extract(bench, meshVerts, meshTris, skopt);

        if (!result.Ok || result.Centerlines.Count == 0)
        {
            res.Note(RoadCenterlineSeverity.Warn, $"道路中心线提取：未得到中心线。{(result.Ok ? result.Summary : result.Error)}");
            return res;
        }
        res.Note(RoadCenterlineSeverity.Info, result.Summary);   // DEM 规模/可行驶格/骨架格：调参时看这一行

        // ── 作业区域精裁（需求07）：把中心线裁到区域并集内，剔碎段 ──
        if (clipRings is { Count: > 0 })
        {
            double minKeep = Math.Max(5.0, skopt.MinRoadWidth);
            var clipped = new List<double[]>();
            foreach (var cl in result.Centerlines)
                clipped.AddRange(RegionClip.ClipPolyline(cl, clipRings, minKeep));
            int before = result.Centerlines.Count;
            result.Centerlines = clipped;
            if (result.Centerlines.Count == 0)
            {
                res.Note(RoadCenterlineSeverity.Warn, "作业区域裁剪后无中心线落在区域内（请检查区域范围是否覆盖道路）。");
                return res;
            }
            res.Note(RoadCenterlineSeverity.Info, $"作业区域裁剪：中心线 {before} → {result.Centerlines.Count} 条（仅保留区域内）。");
        }

        // ── 连通增强 + 并入手动补充线路（让本该连通的真连通、把用户画的补充线接进路网）──
        List<double[]> outLines;
        if (opt.EnableConnect)
        {
            var connOpt = new RoadConnectOptions
            {
                SnapTol = 4.0,
                ConnectDist = opt.ConnectDist,
                ManualConnectDist = Math.Max(40.0, opt.ConnectDist * 2.5),
                MaxBridgeSlopeDeg = skopt.MaxDriveSlopeDeg,   // 与可行驶坡度一致，防接穿台阶面
                DrapeSpacing = Math.Max(2.0, skopt.CellSize), // 补充线按 TIN 铺贴成平稳过渡
            };
            // 地形采样器：有 TIN 用 TIN、否则用台阶线插值；把手动补充线按现状地形起伏铺贴。
            var sampler = RoadTerrainSampler.BuildSampler(meshVerts!, meshTris!, bench);
            var cr = RoadNetworkConnector.Connect(result.Centerlines, manualLines, connOpt, sampler);
            outLines = cr.Lines;
            res.Note(RoadCenterlineSeverity.Info, cr.Summary + (manualLines.Count > 0
                ? (sampler != null ? "（补充线已按现状地形铺贴）" : "（无 TIN/台阶线，补充线未铺贴）") : ""));
        }
        else
        {
            outLines = new List<double[]>(result.Centerlines);
            if (manualLines.Count > 0) outLines.AddRange(manualLines);   // 仅并入不连通
        }

        res.Lines = outLines;
        res.ManualCount = manualLines.Count;
        res.Ok = outLines.Count > 0;
        return res;
    }
}
