using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.PointCloud;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「提取道路中心线」（原 RoadLib「路网构建」组首钮 → PointCloudLib.RoadCenterline.RoadCenterlineRunner）。
///
/// 原版走的是<b>骨架路由法</b>（<see cref="RoadSkeletonExtractor"/>：台阶线/TIN 建可行驶面 → 坡度掩膜 → 细化骨架
/// → 入口撒点路由），不是"坡线配对取中线"（<see cref="RoadCenterlineExtractor"/> 在原版里没有任何按钮调用）。
/// 本文件把原 Runner 的场景侧照搬过来：运行前选中的多段线 = 手动补充线（对话框可勾）/ 框选台阶线（旧语义）；
/// 否则读全图多段线（排除「点云_道路中心线」「等高线」层）→ 高程守卫 → 读作业区域台账 →
/// 场景里顶点最多的三角网作 TIN 插值面 → 骨架路由提取 → 区域精裁 → 连通增强 → 覆盖写「点云_道路中心线」（琥珀 #F2A517）。
/// 纯算法部分在 <see cref="RoadCenterlineRunner"/>（可单测）。
/// </summary>
public partial class MainWindow
{
    private void ExtractCenterline() => _ = ExtractCenterlineAsync();

    /// <summary>多段线 → 扁平 [x,y,z,...]（三维多段线逐点 Z，平面多段线取标高；闭合线补回闭合段，挡墙才封得住）。</summary>
    private static double[] PolylineToFlatXyz(PolylineEntity pl)
    {
        int n = pl.Points.Count;
        bool closeBack = pl.Closed && n >= 3;
        var flat = new double[(n + (closeBack ? 1 : 0)) * 3];
        for (int i = 0; i < n; i++)
        { flat[3 * i] = pl.Points[i].x; flat[3 * i + 1] = pl.Points[i].y; flat[3 * i + 2] = pl.ZAt(i); }
        if (closeBack)
        { flat[3 * n] = pl.Points[0].x; flat[3 * n + 1] = pl.Points[0].y; flat[3 * n + 2] = pl.ZAt(0); }
        return flat;
    }

    /// <summary>
    /// 读项目级「采场/排土场圈定」的 mineable_region，转成中心线提取的裁剪多边形（需求07）。
    /// 忠实原 RoadLibPlugin.LoadClipRegions：<b>整表都给出去，不按 active 过滤</b>（active 只决定默认勾不勾）；
    /// 几何不成立的行要计数；台账为空 / 读不出来 / 有但没选定 三种口径分开说（scopeLabel）。
    /// 工程库未连接时**不**在这里拉起连库流程 —— 区域只是可选的裁剪范围，缺了就全图提取，不该把命令吞掉。
    /// </summary>
    private IReadOnlyList<RoadClipRegion>? LoadRoadClipRegions(out string scopeLabel)
    {
        scopeLabel = "";
        try
        {
            if (_geoDb == null)
            {
                scopeLabel = "裁剪范围：作业区域台账读不出来（工程库未连接）—— 本次全图提取。"
                           + "这不是「没圈定」，重新圈也没用；请先连接工程库（任一数据库命令会拉起连接）。";
                return null;
            }
            var list = new List<RoadClipRegion>();
            int rows = 0, badGeom = 0;
            foreach (var r in Data.MineableRegions.List(_geoDb.Connection))
            {
                rows++;
                if (!r.RingUsable) { badGeom++; continue; }   // 需 ≥3 顶点(每点 xyz)
                int n = r.PointCount;
                var ring = new double[n * 2];
                for (int i = 0; i < n; i++) { ring[2 * i] = r.Points[3 * i]; ring[2 * i + 1] = r.Points[3 * i + 1]; }
                list.Add(new RoadClipRegion
                {
                    Name = r.Name,
                    Category = r.Category,
                    RingXy = ring,
                    Active = Data.MineableRegions.IsActive(r),
                });
            }

            int on = list.Count(z => z.Active);
            string bad = badGeom > 0 ? $"；另有 {badGeom} 块几何不成立（顶点<3 或 points_json 解不开），本次用不了" : "";
            scopeLabel =
                list.Count == 0
                    ? (rows > 0
                        ? $"裁剪范围：台账 {rows} 块区域全部几何不成立（顶点<3 或 points_json 解不开）—— 本次全图提取。"
                        : "裁剪范围：作业区域台账为空 —— 本次全图提取（去「采场/排土场圈定」圈一块即可限制）。")
                : on == 0
                    ? $"裁剪范围：台账里有 {list.Count} 块作业区域，但本期一块都没选定 —— 默认全图提取。"
                      + "要限制范围：对话框里直接勾要用的区域（只影响本次），或去「作业区划分」勾上「选定」再重跑" + bad + "。"
                : on == list.Count
                    ? $"裁剪范围：本期选定的 {on} 块作业区域（已默认勾上）" + bad + "。"
                    : $"裁剪范围：本期选定的 {on}/{list.Count} 块作业区域已默认勾上；"
                      + $"其余 {list.Count - on} 块未选定，需要时可在对话框里补勾" + bad + "。";
            return list.Count > 0 ? list : null;
        }
        catch (Exception ex)
        {
            scopeLabel = $"裁剪范围：作业区域台账读不出来（{ex.Message}）—— 本次全图提取。"
                       + "这不是「没圈定」，重新圈也没用；请确认工程库已加载。";
            return null;
        }
    }

    private static string RoadRegionCategoryText(string cat) => cat switch
    {
        "pit" => "（采场）",
        "external_dump" => "（外排土场）",
        "internal_dump" => "（内排土场）",
        "pit_working_slope" => "（剥采工作帮）",
        "dump_working_slope" => "（排土工作帮）",
        "mineable" => "（未分类）",
        _ => "",
    };

    private const string RoadScopeRegion = "仅在所选作业区域内生成";
    private const string RoadScopeFull = "全图生成（不限制区域）";

    /// <summary>
    /// 参数对话框（忠实原 RoadCenterlineDialog.xaml 的组织：说明 → 生成范围(区域清单) → 五个参数 + 挡墙 →
    /// 连通增强/手动补充 → 确定/取消）。自检下取默认值直通。
    /// </summary>
    private PcForm BuildRoadCenterlineForm(IReadOnlyList<RoadClipRegion>? regions, int selectedCount, string ledgerNote)
    {
        var form = new PcForm
        {
            Title = "道路中心线提取（骨架路由法）", OkText = "确定", Width = 560,
            CliDescription = "从台阶线建可行驶面 → 坡度掩膜 → 细化骨架 → 从入口撒点(FPS)路由探索，输出连通的道路网（落「点云_道路中心线」）。",
        };
        form.Text("从台阶线建可行驶面 → 坡度掩膜 → 细化骨架 → 从入口撒点(FPS)路由探索，输出连通的道路网"
                + "（落「点云_道路中心线」并入路网）。优先用场景三角网(TIN)做插值面（先建三角网更佳）。");

        // ── 需求07：生成范围——全图生成 或 仅在所选作业区域内生成 ──
        var scopeRows = new List<PcRow>();
        if (regions == null || regions.Count == 0)
        {
            // 台账里确实没有可用区域（或读不出来）：只能全图生成。原因用上层的原话，别一律说成"未定义"。
            form.Group("生成范围（需求07：可限制在作业区域内，也可全图直接生成）",
                PcRow.Radios("scope", "生成范围", new[] { RoadScopeFull }, RoadScopeFull));
            form.Small(ledgerNote.Length > 0 ? ledgerNote
                : "作业区域台账里没有可用区域（去『采场/排土场圈定』圈一块后可限制）；本次全图生成。");
        }
        else
        {
            int on = regions.Count(r => r.Active);
            // 默认按区域限制（有本期选定的）；一块都没选定则默认全图 —— 不擅自替人缩小范围，但限制这条路必须留着。
            scopeRows.Add(PcRow.Radios("scope", "生成范围", new[] { RoadScopeRegion, RoadScopeFull }, on > 0 ? RoadScopeRegion : RoadScopeFull));
            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                string label = (r.Name.Length > 0 ? r.Name : "(未命名)") + RoadRegionCategoryText(r.Category) + (r.Active ? "" : " · 本期未选定");
                scopeRows.Add(PcRow.Check("rg" + i, label, r.Active));
            }
            form.Group("生成范围（需求07：可限制在作业区域内，也可全图直接生成）", scopeRows.ToArray());
            if (on == 0)
                form.Small($"台账里有 {regions.Count} 块作业区域，但本期一块都没选定（「作业区划分」的『选定』列一个都没勾）—— 所以默认全图生成。"
                         + "要限制范围：选「仅在所选作业区域内生成」，再勾要用的区域（只影响本次提取，不改台账）；或去「作业区划分」勾上『选定』后重跑。");
            else if (on < regions.Count)
                form.Small($"台账 {regions.Count} 块：本期选定的 {on} 块已勾上，其余 {regions.Count - on} 块本期未选定，需要时可直接补勾（只影响本次提取，不改台账）。");
        }

        form.Rows(
            PcRow.Num("cell", "栅格精度", "3", "m", "（越小越细越慢）", null, 120, 64),
            PcRow.Num("slope", "可行驶坡度上限", "14", "°", "（≤为可行驶；越小越不会斜穿小台阶）", null, 120, 64),
            PcRow.Num("wmin", "最小路面宽度", "12", "m", "（窄于此剔除；越小纳入越窄的台阶、缺口越少）", null, 120, 64),
            PcRow.Num("dests", "路网密度（撒点数）", "400", null, "（FPS 每坑撒点数，越多覆盖越全越密）", null, 120, 64),
            PcRow.Num("smooth", "面平滑", "1.2", "格", "（越小越保留小台阶、坡度越噪）", null, 120, 64),
            PcRow.Check("barrier", "用台阶线挡墙（杜绝斜穿小台阶，几何最精确）", true));

        // ── 连通增强 + 手动补充线路 ──
        var sup = selectedCount > 0
            ? PcRow.Check("supplement", $"把运行前选中的 {selectedCount} 条多段线作为补充线路并入并连通（不作台阶线）", true)
            : PcRow.Check("supplement", "把运行前选中的多段线作为补充线路并入（运行前未选中任何多段线）", false);
        sup.Enabled = selectedCount > 0;
        form.Group("连通增强 / 手动补充",
            PcRow.Check("connect", "连接邻近线路（焊接近失端点 + 把断头按距离连到最近线路，T形处打断成节点）", true),
            PcRow.Num("connectDist", "断头连接距离上限", "25", "m", "（≤此的悬空端点才补连接段；补充线放宽至 2.5×）", null, 120, 64),
            sup,
            PcRow.Check("overwrite", "覆盖已有「点云_道路中心线」（清掉上次提取再写，避免重复）", true));
        form.Small("提示：先在视口画/选中要补充的多段线，再运行即并入并连通（选中的线不当台阶线）；补充线按现状地形(三角网或台阶线)铺贴成平稳过渡。"
                 + "只加一条联络道可改用「手动标定线路」更轻量。");

        // 选了"按区域限制"却一块没勾：原版曾静默退成全图，出来的路网多出一片场外的线。这里挡住，不猜他要哪种。
        form.Validate = v =>
        {
            if (regions == null || v.S("scope") != RoadScopeRegion) return null;
            for (int i = 0; i < regions.Count; i++) if (v.B("rg" + i)) return null;
            return "选了「仅在所选作业区域内生成」，但一块区域都没勾 —— 请至少勾一块，或改选「全图生成（不限制区域）」。（不替你默认成全图：那会静静改掉生成范围。）";
        };
        return form;
    }

    /// <summary>场景里顶点最多的三角网（忠实原 RoadTinReader.TryReadBestTin）→ 扁平 verts/tris；没有返回 false。</summary>
    private bool TryReadBestRoadTin(out double[]? verts, out int[]? tris)
    {
        verts = null; tris = null;
        MeshEntity? best = null;
        foreach (var e in _scene.Entities)
            if (e is MeshEntity m && m.Verts.Count >= 3 && m.Tris.Count >= 1 && (best == null || m.Verts.Count > best.Verts.Count))
                best = m;
        if (best == null) return false;
        verts = new double[best.Verts.Count * 3];
        for (int i = 0; i < best.Verts.Count; i++)
        { var v = best.Verts[i]; verts[3 * i] = v.x; verts[3 * i + 1] = v.y; verts[3 * i + 2] = v.z; }
        tris = new int[best.Tris.Count * 3];
        for (int i = 0; i < best.Tris.Count; i++)
        { var t = best.Tris[i]; tris[3 * i] = t.a; tris[3 * i + 1] = t.b; tris[3 * i + 2] = t.c; }
        return true;
    }

    private static EchoLevel RoadEchoLevel(RoadCenterlineSeverity s) => s switch
    {
        RoadCenterlineSeverity.Success => EchoLevel.Success,
        RoadCenterlineSeverity.Warn => EchoLevel.Warn,
        RoadCenterlineSeverity.Error => EchoLevel.Error,
        _ => EchoLevel.Info,
    };

    private async Task ExtractCenterlineAsync()
    {
        try
        {
            // 运行前选中的多段线 → 作"手动补充线路"候选（用户选中即并入并连通，区别于台阶线）。
            var selSet = new HashSet<SceneEntity>();
            var selPolys = new List<double[]>();
            foreach (var e in _selected)
                if (e is PolylineEntity pl && pl.Points.Count >= 2)
                { selPolys.Add(PolylineToFlatXyz(pl)); selSet.Add(pl); }

            // 裁剪范围不静默：提取前先把"这次在哪几块区域内找"说清楚，
            // 否则"某条路没提出来"到底是范围被缩了还是算法没识别，事后分不开。
            var regions = LoadRoadClipRegions(out string ledgerNote);
            if (ledgerNote.Length > 0) EditEcho(ledgerNote, EchoLevel.Info);

            var form = BuildRoadCenterlineForm(regions, selPolys.Count, ledgerNote);
            var v = await form.AskAsync(this);
            if (v == null) { EditEcho("道路中心线提取：已取消", EchoLevel.Info); return; }

            static double Pos(double d, double fallback) => d > 0 ? d : fallback;
            var opt = new RoadCenterlineRunOptions
            {
                Skeleton = new RoadSkeletonOptions
                {
                    CellSize = Pos(v.D("cell"), 3.0),
                    MaxDriveSlopeDeg = Pos(v.D("slope"), 14.0),
                    MinRoadWidth = Pos(v.D("wmin"), 12.0),
                    MaxDestinations = v.I("dests") > 0 ? v.I("dests") : 400,
                    DemSmoothSigma = Pos(v.D("smooth"), 1.2),
                    UseBenchBarrier = v.B("barrier", true),
                },
                EnableConnect = v.B("connect", true),
                ConnectDist = Pos(v.D("connectDist"), 25.0),
            };
            bool overwrite = v.B("overwrite", true);
            bool useSelAsSupplement = selPolys.Count > 0 && v.B("supplement");
            var manualLines = useSelAsSupplement ? selPolys : new List<double[]>();
            if (regions != null && v.S("scope") == RoadScopeRegion)
            {
                var picked = new List<RoadClipRegion>();
                for (int i = 0; i < regions.Count; i++) if (v.B("rg" + i)) picked.Add(regions[i]);
                if (picked.Count > 0) opt.ClipRegions = picked;
            }

            // 收集台阶线：选中作补充时 → 读全图(排除补充选集/输出层/等高线)；
            //             否则保留旧语义(选≥2 条=框选台阶线；否则读全图)。
            List<double[]> benchLines;
            if (!useSelAsSupplement && selPolys.Count >= 2)
            {
                benchLines = new List<double[]>(selPolys);   // 旧语义：框选台阶线再运行
            }
            else
            {
                benchLines = new List<double[]>();
                foreach (var e in _scene.Entities)
                {
                    if (e is not PolylineEntity pl || pl.Points.Count < 2) continue;
                    if (pl.LayerName == RoadCenterlineRunner.OutputLayer || pl.LayerName == "等高线") continue;
                    if (useSelAsSupplement && selSet.Contains(pl)) continue;   // 补充线不当台阶线
                    benchLines.Add(PolylineToFlatXyz(pl));
                }
            }

            // 读场景三角网(TIN)做插值面（推荐先建三角网）；无 TIN 退回台阶线块状面。取顶点最多的 mesh。
            TryReadBestRoadTin(out var meshVerts, out var meshTris);

            var res = await Task.Run(() => RoadCenterlineRunner.Run(benchLines, manualLines, meshVerts, meshTris, opt));
            foreach (var (lv, text) in res.Notes) EditEcho(text, RoadEchoLevel(lv));
            if (!res.Ok) return;

            BeginChange();
            // 覆盖：清掉上次「点云_道路中心线」再写（幂等，避免多次提取重复堆叠），可在对话框关闭。
            if (overwrite)
            {
                int n = _scene.Entities.RemoveAll(e => e.LayerName == RoadCenterlineRunner.OutputLayer);
                if (n > 0) _selected.RemoveAll(e => e.LayerName == RoadCenterlineRunner.OutputLayer);
            }

            // 入库为多段线（图层 点云_道路中心线，琥珀色 #F2A517，线宽 0.5mm）。
            var layer = _layers.Get(RoadCenterlineRunner.OutputLayer)
                        ?? _layers.EnsureImported(RoadCenterlineRunner.OutputLayer, 242 / 255f, 165 / 255f, 23 / 255f);
            if (layer.LineWeight < 0) layer.LineWeight = 50;
            int written = 0, degenerate = 0;
            foreach (var cl in res.Lines)
            {
                int n = cl.Length / 3;
                if (n < 2) continue;
                // 焊接容差(4m)大于栅格(3m)时，骨架上一格长的小段两端会被吸到同一节点、退化成零长 ——
                // 原版把它们原样写进引擎；这里零长折线画不出也选不中，只会把对象数虚高，略过并报数。
                double lenXY = 0;
                for (int i = 1; i < n; i++)
                    lenXY += Math.Sqrt((cl[3 * i] - cl[3 * i - 3]) * (cl[3 * i] - cl[3 * i - 3]) + (cl[3 * i + 1] - cl[3 * i - 2]) * (cl[3 * i + 1] - cl[3 * i - 2]));
                if (lenXY < 1e-6) { degenerate++; continue; }
                var poly = new PolylineEntity { LayerName = layer.Name, Cr = 242 / 255f, Cg = 165 / 255f, Cb = 23 / 255f, Elevation = 0, Zs = new List<double>(n) };
                for (int i = 0; i < n; i++) { poly.Points.Add((cl[3 * i], cl[3 * i + 1])); poly.Zs.Add(cl[3 * i + 2]); }
                _scene.Add(poly);
                written++;
            }
            RefreshScene();

            string supTxt = res.ManualCount > 0 ? $"（含手动补充 {res.ManualCount} 条）" : "";
            string degTxt = degenerate > 0 ? $"（另 {degenerate} 条焊接后零长已略）" : "";
            EditEcho($"道路中心线已入库 {written} 条{supTxt}{degTxt}（图层 {RoadCenterlineRunner.OutputLayer}）；可被「基础道路网络构建」纳入路网。",
                EchoLevel.Success);
        }
        catch (Exception ex)
        {
            EditEcho($"道路中心线提取异常：{ex.Message}", EchoLevel.Error);
        }
    }
}
