using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Data;
using Xunit;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Tests;

/// <summary>「三维地质建模 → 更新地质模型」组(补勘钻孔写实 / 现状写实 / 更新煤层面)的库表往返、CSV IO、煤层结构、引擎与标记几何回归。</summary>
public class ModelingModelUpdateTests
{
    // ═════════════════ 补勘写实: 批次 / 孔 / 层位 往返 ═════════════════

    [Fact]
    public void Sup_batch_create_rename_delete_round_trip()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        int before = SupAllBatches(c).Count;
        long id = SupCreateBatch(c, "手工录入", now: new DateTime(2026, 9, 7, 10, 30, 15));
        long id2 = SupCreateBatch(c, "手工录入", now: new DateTime(2026, 9, 7, 10, 30, 15));   // 同秒 → -02 后缀
        var b = SupGetBatch(c, id)!;
        Assert.Equal("SBW-20260907-103015", b.Label);
        Assert.Equal("手工录入 2026-09-07 10:30", b.Name);
        Assert.Equal("SBW-20260907-103015-02", SupGetBatch(c, id2)!.Label);
        Assert.Equal(before + 2, SupAllBatches(c).Count);
        Assert.Equal(id2, SupAllBatches(c)[0].Id);   // 标签倒序 = 最新在前

        SupRenameBatch(c, id, "补勘一期");
        Assert.Equal("补勘一期", SupGetBatch(c, id)!.Name);

        var h = new SupHoleRow { HoleId = "BK-T1", X = 100, Y = 200, ZCollar = 1250, BatchId = id };
        SupInsertHole(c, h);
        SupReplaceHorizons(c, h.Id, new[] { new SupHorizonRow { SeamCode = "4", RoofElevation = 1200, FloorElevation = 1195, SortOrder = 0 } });
        Assert.Equal(1, SupAllBatches(c).First(x => x.Id == id).HoleCount);

        Assert.Equal(1, SupDeleteBatch(c, id));
        Assert.Null(SupGetBatch(c, id));
        Assert.Null(SupGetHoleByHoleId(c, "BK-T1"));
        Assert.Empty(SupHorizonsByHole(c, h.Id));
        SupDeleteBatch(c, id2);
        Assert.Equal(before, SupAllBatches(c).Count);
    }

    [Fact]
    public void Sup_hole_and_horizons_round_trip_and_observations()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        long bid = SupCreateBatch(c, "手工录入");
        var h = new SupHoleRow { HoleId = "BK-T2", X = 10, Y = 20, ZCollar = null, BatchId = bid };
        long hid = SupInsertHole(c, h);
        Assert.True(hid > 0);
        Assert.Equal(hid, SupGetHoleByHoleId(c, "BK-T2")!.Id);

        h.X = 11; h.ZCollar = 1300.5;
        Assert.Equal(1, SupUpdateHole(c, h));
        Assert.Equal(1300.5, SupGetHole(c, hid)!.ZCollar);

        SupReplaceHorizons(c, hid, new[]
        {
            new SupHorizonRow { SeamCode = "4", RoofElevation = 1200, FloorElevation = 1194, SortOrder = 0 },
            new SupHorizonRow { SeamCode = "9", RoofElevation = null, FloorElevation = 1150, SortOrder = 1 },
        });
        var hz = SupHorizonsByHole(c, hid);
        Assert.Equal(2, hz.Count);
        Assert.Equal("4", hz[0].SeamCode); Assert.Equal("9", hz[1].SeamCode);   // 按层序
        SupReplaceHorizons(c, hid, new[] { new SupHorizonRow { SeamCode = "4", RoofElevation = 1201, FloorElevation = 1195, SortOrder = 0 } });
        Assert.Single(SupHorizonsByHole(c, hid));   // 整体替换
        Assert.Single(SupAllHoles(c, bid));
        Assert.Contains(SupAllHoles(c), x => x.Id == hid);

        var obsRoof = SupObservations(c, "4", "顶板", bid);
        Assert.Single(obsRoof);
        Assert.Equal((11.0, 20.0, 1201.0), (obsRoof[0].x, obsRoof[0].y, obsRoof[0].z));
        Assert.Contains("手工录入", obsRoof[0].batch);
        Assert.Empty(SupObservations(c, "9", "底板", bid));
        Assert.Single(SupObservations(c, "4", "底板", null));

        SupDeleteHole(c, hid);
        Assert.Null(SupGetHole(c, hid));
        Assert.Empty(SupHorizonsByHole(c, hid));
    }

    // ═════════════════ 现状写实: 批次 / 点 往返 ═════════════════

    [Fact]
    public void Cs_batch_points_replace_delete_and_observations()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        long bid = CsCreateBatch(c, "拾取录入", now: new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal("CSR-20260102-030405", CsGetBatch(c, bid)!.Label);
        CsReplacePoints(c, bid, new[]
        {
            new CsPointRow { X = 1, Y = 2, Z = 1100, SeamCode = "4", Horizon = "顶板", Remark = "a" },
            new CsPointRow { X = 3, Y = 4, Z = 1090, SeamCode = "4", Horizon = "底板" },
            new CsPointRow { X = 5, Y = 6, Z = 1080, SeamCode = "9", Horizon = "顶板" },
        });
        Assert.Equal(3, CsPointsByBatch(c, bid).Count);
        Assert.Equal(3, CsAllBatches(c).First(b => b.Id == bid).PointCount);
        Assert.Equal("a", CsPointsByBatch(c, bid)[0].Remark);

        var obs = CsObservations(c, "4", "顶板", bid);
        Assert.Single(obs);
        Assert.Equal(1100, obs[0].z);
        Assert.Single(CsObservations(c, "4", "底板", null));

        CsReplacePoints(c, bid, new[] { new CsPointRow { X = 9, Y = 9, Z = 1, SeamCode = "4", Horizon = "顶板" } });
        Assert.Single(CsPointsByBatch(c, bid));   // 整体替换

        CsRenameBatch(c, bid, "现状 A");
        Assert.Equal("现状 A", CsGetBatch(c, bid)!.Name);
        Assert.Equal(1, CsDeleteBatch(c, bid));
        Assert.Null(CsGetBatch(c, bid));
        Assert.Empty(CsPointsByBatch(c, bid));
    }

    // ═════════════════ 煤层结构 / 设置 ═════════════════

    [Fact]
    public void SeamStructure_default_from_dict_and_settings_persist()
    {
        using var db = GeoDatabase.OpenSeeded();
        string tmp = Path.Combine(Path.GetTempPath(), "kylin_mu_" + Guid.NewGuid().ToString("N") + ".json");
        var old = MuSettingsPath;
        MuSettingsPath = tmp;
        try
        {
            var def = SeamStructureConfig.Default(db.Connection);
            Assert.True(def.Seams.Count >= 4, $"种子煤层字典应有多层, 实得 {def.Seams.Count}");
            Assert.Equal("4", def.Seams[0].Code);   // 浅→深, 4 号煤在最上
            Assert.Equal(def.Seams.Count, SeamStructureConfig.LoadOrDefault(db.Connection).Seams.Count);   // 未记住 → 默认

            var cfg = new SeamStructureConfig();
            cfg.Seams.Add(new SeamStructureConfig.SeamItem { Code = "6", Name = "6 号煤层" });
            cfg.Seams.Add(new SeamStructureConfig.SeamItem { Code = "4", Name = "4 号煤层" });
            cfg.Save();
            var back = SeamStructureConfig.LoadOrDefault(db.Connection);
            Assert.Equal(new[] { "6", "4" }, back.Seams.Select(s => s.Code).ToArray());
            Assert.Equal("6 号煤层", back.NameFor("6"));
            Assert.Equal("zz", back.NameFor("zz"));
            Assert.Equal(2, back.AsPairs().Count);

            MuSettingsSet("geo.realistic.currentstate.mark", new { Show = false, Size = 7.5 });
            Assert.Equal(new[] { "6", "4" }, SeamStructureConfig.LoadOrDefault(db.Connection).Seams.Select(s => s.Code).ToArray());   // 其它键不干扰
        }
        finally { MuSettingsPath = old; try { File.Delete(tmp); } catch { } }
    }

    // ═════════════════ CSV 模板 / 解析 / 导入 ═════════════════

    [Fact]
    public void Sup_csv_template_parse_import_export_round_trip()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var seams = new List<(string code, string name)> { ("4", "4 号煤层"), ("9", "9 号煤层") };
        string tpl = SupCsvTemplate(seams);
        var lines = tpl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("孔号,经距X,纬距Y,孔口高程,4 号煤层顶板高程,4 号煤层底板高程,9 号煤层顶板高程,9 号煤层底板高程", lines[0].Trim());

        var parsed = SupParseCsv(tpl, seams, out var status);   // 示例行本身可解析
        Assert.Single(parsed);
        Assert.Equal("BK001", parsed[0].HoleId);
        Assert.Equal(1285.6, parsed[0].ZCollar);
        Assert.Equal((1200.0, 1195.0), (parsed[0].Horizons["4"].roof, parsed[0].Horizons["4"].floor));
        Assert.Equal((1170.0, 1165.0), (parsed[0].Horizons["9"].roof, parsed[0].Horizons["9"].floor));
        Assert.Contains("解析 1 孔", status);

        string csv = "孔号,经距X,纬距Y,孔口高程,4顶板,4底板,9 号煤层顶板高程\nH1,1,2,3,10,5,\nH1,1,2,3,,,\nH2,4,5,,,,20\n,9,9,9,,,\n";
        var p2 = SupParseCsv(csv, seams, out status);
        Assert.Equal(2, p2.Count);
        Assert.Contains("跳过 1 个重复孔号", status);
        Assert.Equal((10.0, 5.0), (p2[0].Horizons["4"].roof, p2[0].Horizons["4"].floor));   // 别名列头
        Assert.False(p2[0].Horizons.ContainsKey("9"));
        Assert.Null(p2[1].ZCollar);
        Assert.Equal(20.0, p2[1].Horizons["9"].roof);

        var cfg = new SeamStructureConfig();
        foreach (var (code, name) in seams) cfg.Seams.Add(new SeamStructureConfig.SeamItem { Code = code, Name = name });
        var (bid, ins, upd, fail) = SupImportParsed(c, p2, cfg);
        Assert.NotNull(bid); Assert.Equal(2, ins); Assert.Equal(0, upd); Assert.Equal(0, fail);
        Assert.Equal("CSV导入", SupGetBatch(c, bid!.Value)!.Source);
        var h1 = SupGetHoleByHoleId(c, "H1")!;
        Assert.Equal(bid, h1.BatchId);
        Assert.Single(SupHorizonsByHole(c, h1.Id));

        var (bid2, ins2, upd2, _) = SupImportParsed(c, p2, cfg);   // 再导 → 整孔覆盖并归入新批次
        Assert.Equal(0, ins2); Assert.Equal(2, upd2);
        Assert.Equal(bid2, SupGetHoleByHoleId(c, "H1")!.BatchId);
        Assert.Equal(0, SupAllBatches(c).First(b => b.Id == bid).HoleCount);

        string exp = SupExportCsv(c, bid2, seams);
        var back = SupParseCsv(exp, seams, out _);
        Assert.Equal(2, back.Count);
        Assert.Equal(5.0, back.First(x => x.HoleId == "H1").Horizons["4"].floor);

        Assert.Empty(SupParseCsv("经距X,纬距Y\n1,2\n", seams, out status));
        Assert.Equal("缺少必要列：孔号", status);
        Assert.Null(SupImportParsed(c, new List<SupParsedHole>(), cfg).batchId);   // 一孔没成 → 空批次删掉
    }

    [Fact]
    public void Cs_csv_template_parse_and_export()
    {
        string tpl = CsCsvTemplate();
        var pts = CsParseCsv(tpl, out var status);
        Assert.Single(pts);
        Assert.Equal(1180.5, pts[0].Z);
        Assert.Equal("4", pts[0].SeamCode); Assert.Equal("顶板", pts[0].Horizon);

        var p2 = CsParseCsv("x,y,z,备注,层位\n1,2,3,,底板\n4,abc,6,,\n,,,\n7,8,9,r,xx\n", out status);
        Assert.Equal(2, p2.Count);
        Assert.Equal("底板", p2[0].Horizon); Assert.Null(p2[0].SeamCode);
        Assert.Equal("顶板", p2[1].Horizon); Assert.Equal("r", p2[1].Remark);
        Assert.Contains("跳过 1 行", status);

        Assert.Empty(CsParseCsv("a,b\n1,2\n", out status));
        Assert.StartsWith("缺少必要列", status);

        string exp = CsExportCsv(new[] { new CsPointRow { X = 1.5, Y = 2.5, Z = 3.5, SeamCode = "9", Horizon = "顶板", Remark = "含,逗号" } });
        var back = CsParseCsv(exp, out _);
        Assert.Single(back);
        Assert.Equal("含,逗号", back[0].Remark); Assert.Equal("9", back[0].SeamCode);
    }

    // ═════════════════ 展绘 / 标记 / 现状面 几何 ═════════════════

    [Fact]
    public void SupBuildHoleEntities_point_text_column_and_horizon_points()
    {
        var defs = new List<SeamDefRow> { new("4", "4 号煤层", 10, "#D4A017") };
        var holes = new List<SupHoleRow> { new() { Id = 1, HoleId = "A", X = 0, Y = 0, ZCollar = 1300 }, new() { Id = 2, HoleId = "B", X = 50, Y = 0, ZCollar = null } };
        var hz = new Dictionary<long, List<SupHorizonRow>>
        {
            [1] = new() { new() { SeamCode = "4", RoofElevation = 1200, FloorElevation = 1195 } },
            [2] = new() { new() { SeamCode = "4", RoofElevation = null, FloorElevation = 1190 } },
        };
        var ents = SupBuildHoleEntities(holes, hz, defs, 5);
        Assert.All(ents, e => Assert.Equal(SupDrawLayer, e.LayerName));
        Assert.Equal(2, ents.OfType<TextEntity>().Count());
        var col = ents.OfType<PolylineEntity>().Single();   // 孔 B 只有一个高程 → 无孔柱
        Assert.True(col.Has3D);
        Assert.Equal((1300.0, 1195.0), (col.ZAt(0), col.ZAt(1)));
        var pts = ents.OfType<PointEntity>().ToList();
        Assert.Equal(5, pts.Count);   // 2 孔位 + A 顶/底 + B 底
        var roof = pts.Single(p => p.Style == 2 && p.Elevation == 1200);
        Assert.InRange(roof.Cr, 0.82f, 0.84f);   // #D4A017
        Assert.Contains(pts, p => p.Style == 3 && p.Elevation == 1190 && p.X == 50);
        Assert.Contains(pts, p => (p.Style & 32) != 0 && p.Elevation == 1190 && p.X == 50);   // B 无孔口高程 → 孔位点取最高
    }

    [Fact]
    public void CurrentStatePointMarker_meshes_and_entities()
    {
        var items = new List<CurrentStatePointMarker.Item>
        {
            new() { X = 0, Y = 0, Z = 100, SeamName = "4 号煤层", Horizon = "顶板" },
            new() { X = 10, Y = 0, Z = 100, SeamName = "4 号煤层", Horizon = "底板" },
        };
        var m = CurrentStatePointMarker.BuildMeshes(items, 10);
        // 每点: 垫底 2 条+2 片=4 quad; 亮层 2 条+2 片=4 quad; quad=4 顶点 6 索引
        Assert.Equal(2 * 4 * 12, m.HaloV.Count); Assert.Equal(2 * 4 * 6, m.HaloT.Count);
        Assert.Equal(4 * 12, m.RoofV.Count); Assert.Equal(4 * 12, m.FloorV.Count);
        Assert.Equal(7.5, m.Arm); Assert.Equal(16, m.Pole);
        Assert.Equal(100 + 16, m.RoofV.Skip(2).Where((_, i) => i % 3 == 0).Max());   // 竖杆顶 = z+pole

        var ents = CurrentStatePointMarker.Build(items, 10);
        Assert.All(ents, e => Assert.Equal(CurrentStatePointMarker.Layer, e.LayerName));
        Assert.Equal(3, ents.OfType<MeshEntity>().Count());
        Assert.Equal(2, ents.OfType<PointEntity>().Count());
        Assert.Equal(2, ents.OfType<PolylineEntity>().Count());   // 引线
        var texts = ents.OfType<TextEntity>().ToList();
        Assert.Equal(4, texts.Count);
        Assert.Contains(texts, t => t.Text == "4 号煤层·底板");
        Assert.Contains(texts, t => t.Text == "Z=100.00" && t.Height == 10);
        Assert.Empty(CurrentStatePointMarker.Build(new List<CurrentStatePointMarker.Item>(), 10));
    }

    [Fact]
    public void CurrentStateSurfaceBuilder_delaunay_and_degenerate()
    {
        var (ok, msg, mesh) = CurrentStateSurfaceBuilder.Build(new[] { (0.0, 0.0, 1.0), (10.0, 0.0, 2.0), (0.0, 10.0, 3.0), (10.0, 10.0, 4.0) }, "现状面");
        Assert.True(ok, msg);
        Assert.NotNull(mesh);
        Assert.Equal(4, mesh!.VertexCount); Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal("现状面", mesh.LayerName);
        Assert.False(CurrentStateSurfaceBuilder.Build(new[] { (0.0, 0.0, 1.0), (1.0, 1.0, 1.0) }, "l").ok);
        Assert.False(CurrentStateSurfaceBuilder.Build(new[] { (0.0, 0.0, 1.0), (1.0, 1.0, 1.0), (2.0, 2.0, 1.0) }, "l").ok);   // 共线
    }

    // ═════════════════ SurfaceUpdateEngine ═════════════════

    private static (double[] v, int[] t) FlatGrid(int n, double spacing, double z)
    {
        var v = new List<double>(); var t = new List<int>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) { v.Add(i * spacing); v.Add(j * spacing); v.Add(z); }
        int w = n + 1;
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * w + i, b = a + 1, c = a + w, d = c + 1;
            t.Add(a); t.Add(b); t.Add(d); t.Add(a); t.Add(d); t.Add(c);
        }
        return (v.ToArray(), t.ToArray());
    }

    [Fact]
    public void Engine_idw_matches_default_path_and_clusters_area_volume()
    {
        var (v, t) = FlatGrid(40, 10, 1000);   // 400×400 平面, z=1000
        var obs = new List<(double x, double y, double z)> { (100, 100, 1010), (110, 100, 1010), (350, 350, 990) };
        var opt = new SurfaceUpdateEngine.Options { Algorithm = "IDW", InfluenceRadius = 60 };
        var res = SurfaceUpdateEngine.Evaluate(v, t, obs, opt);
        var basic = SurfaceUpdate.Evaluate(v, t, obs, new SurfaceUpdate.Options { Algorithm = "IDW", InfluenceRadius = 60 });
        Assert.True(res.Any);
        Assert.Equal(basic.AffectedVertices, res.AffectedVertices);
        for (int i = 0; i < res.NewZ.Length; i++) Assert.Equal(basic.NewZ[i], res.NewZ[i], 9);
        Assert.Equal(basic.NetVolume, res.NetVolume, 6);
        Assert.Equal(2, res.Clusters.Count);   // 两片: (100,100)+(110,100) 搭接, (350,350) 独立
        Assert.Equal(res.AffectedArea, res.Clusters.Sum(c => c.Area), 6);
        Assert.Equal(res.NetVolume, res.Clusters.Sum(c => c.NetVol), 6);
        Assert.Equal(2, res.Clusters[0].ObsCount);
        Assert.True(res.Clusters[0].NetVol > 0 && res.Clusters[1].NetVol < 0);
        Assert.True(res.MaxDisp > 9 && res.MinDisp < -9);   // 观测点上顶点位移 ≈ ±10
        Assert.Equal(400.0 * 400.0, res.TotalArea, 6);
        Assert.True(res.HasBounds && res.HasAffBounds);
        Assert.True(res.AffMinX >= res.MinX && res.AffMaxX <= res.MaxX);
        Assert.Contains("受影响", res.Message);
        Assert.False(SurfaceUpdateEngine.Evaluate(v, t, new List<(double, double, double)>(), opt).Any);
    }

    [Theory]
    [InlineData("NN")] [InlineData("MA")] [InlineData("OK")] [InlineData("SK")] [InlineData("UK")]
    public void Engine_all_algorithms_pull_surface_toward_observations(string algo)
    {
        var (v, t) = FlatGrid(20, 10, 500);
        var obs = new List<(double x, double y, double z)> { (90, 90, 520), (110, 90, 520), (90, 110, 520), (110, 110, 520), (100, 100, 520) };
        var res = SurfaceUpdateEngine.Evaluate(v, t, obs, new SurfaceUpdateEngine.Options { Algorithm = algo, InfluenceRadius = 50 });
        Assert.True(res.Any, algo);
        Assert.True(res.MaxDisp > 15 && res.MaxDisp <= 20.5, $"{algo}: 观测点处应抬升近 20, 实得 {res.MaxDisp}");
        Assert.True(res.MinDisp >= -1e-6, algo);
        Assert.Single(res.Clusters);
        // 半径外顶点不动
        int far = 0 * 21 + 0; Assert.Equal(500, res.NewZ[far * 3 / 3]);
        Assert.Equal(res.AffectedVertices, res.Affected.Count(a => a));
    }

    [Fact]
    public void Engine_single_observation_degrades_to_nn_and_feathers()
    {
        var (v, t) = FlatGrid(10, 10, 0);
        var res = SurfaceUpdateEngine.Evaluate(v, t, new List<(double x, double y, double z)> { (50, 50, 10) }, new SurfaceUpdateEngine.Options { Algorithm = "OK", InfluenceRadius = 25 });
        int center = 5 * 11 + 5;
        Assert.Equal(10, res.NewZ[center], 9);           // 落在观测点上 → 完全拟合
        int next = 5 * 11 + 6;                            // 10m 外: t=0.6, smoothstep=0.648
        Assert.Equal(6.48, res.NewZ[next], 6);
        Assert.Equal(0, res.NewZ[5 * 11 + 8]);            // 30m 外 → 不动
    }

    [Fact]
    public void Engine_sample_grid_and_estimates()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 10), (10, 0, 20), (0, 10, 30), (100, 100, 99) };
        var g = new SurfaceUpdateEngine.SampleGrid2D(pts);
        var hits = g.FindNearest(1, 0, 15, 5);
        Assert.Equal(3, hits.Count);
        Assert.Equal(0, hits[0].index); Assert.Equal(1, hits[1].index); Assert.Equal(2, hits[2].index);
        Assert.Equal(1, hits[0].dist, 9);
        Assert.Equal(2, g.FindNearest(1, 0, 15, 2).Count);
        Assert.Empty(g.FindNearest(50, 50, 5, 5));

        var ctx = SurfaceUpdateEngine.BuildCtx(pts, new SurfaceUpdateEngine.Options { Algorithm = "IDW", InfluenceRadius = 15 });
        Assert.Equal(10, SurfaceUpdateEngine.ComputeEstimate(ctx, g.FindNearest(0, 0, 15, 5), 0, 0));   // 命中样本
        double idw = SurfaceUpdateEngine.ComputeEstimate(ctx, hits, 1, 0);
        Assert.InRange(idw, 10, 20);
        ctx.Algorithm = "NN"; Assert.Equal(10, SurfaceUpdateEngine.ComputeEstimate(ctx, hits, 1, 0));
        ctx.Algorithm = "MA"; Assert.Equal(20, SurfaceUpdateEngine.ComputeEstimate(ctx, hits, 1, 0), 9);
        ctx.MinSamples = 4; Assert.Equal(SurfaceUpdateEngine.NoData, SurfaceUpdateEngine.ComputeEstimate(ctx, hits, 1, 0));
        var kctx = SurfaceUpdateEngine.BuildCtx(pts, new SurfaceUpdateEngine.Options { Algorithm = "ok", InfluenceRadius = 15 });
        Assert.Equal("OK", kctx.Algorithm); Assert.NotNull(kctx.Cps); Assert.NotNull(kctx.Vg);
        Assert.Equal("NN", SurfaceUpdateEngine.BuildCtx(pts.Take(1).ToList(), new SurfaceUpdateEngine.Options { Algorithm = "UK" }).Algorithm);
    }

    [Fact]
    public void MeshSampler2D_interpolates_and_rejects_outside()
    {
        var (v, t) = FlatGrid(4, 10, 0);
        for (int i = 0; i < v.Length; i += 3) v[i + 2] = v[i] + 2 * v[i + 1];   // z = x + 2y 平面
        var s = SurfaceUpdateEngine.MeshSampler2D.Build(v, t)!;
        Assert.NotNull(s);
        Assert.True(s.TrySample(12.5, 7.5, out double z));
        Assert.Equal(12.5 + 15, z, 9);
        Assert.False(s.TrySample(-1, 5, out _));
        Assert.False(s.TrySample(45, 45, out _));
        Assert.Null(SurfaceUpdateEngine.MeshSampler2D.Build(new double[6], new int[3]));
        var me = new MeshEntity("m", new[] { (0.0, 0.0, 5.0), (10.0, 0.0, 5.0), (0.0, 10.0, 5.0) }, new[] { (0, 1, 2) });
        Assert.True(SurfaceUpdateEngine.MeshSampler2D.Build(me)!.TrySample(2, 2, out z)); Assert.Equal(5, z, 9);
    }

    [Fact]
    public void Engine_bands_nice_and_overlay_cells()
    {
        Assert.Equal(1, SurfaceUpdateEngine.Nice125(1.2)); Assert.Equal(2, SurfaceUpdateEngine.Nice125(2.9));
        Assert.Equal(5, SurfaceUpdateEngine.Nice125(6)); Assert.Equal(10, SurfaceUpdateEngine.Nice125(8)); Assert.Equal(0.05, SurfaceUpdateEngine.Nice125(0));
        Assert.Equal(0.1, SurfaceUpdateEngine.NiceStep(0)); Assert.Equal(2, SurfaceUpdateEngine.NiceStep(8));
        Assert.Equal(0, SurfaceUpdateEngine.BandLow(0, 1)); Assert.Equal(1, SurfaceUpdateEngine.BandLow(1, 1)); Assert.Equal(8, SurfaceUpdateEngine.BandLow(-4, 1));
        Assert.Equal(0, SurfaceUpdateEngine.BandOf(0.5, 1)); Assert.Equal(1, SurfaceUpdateEngine.BandOf(1.5, 1));
        Assert.Equal(-2, SurfaceUpdateEngine.BandOf(-2.5, 1)); Assert.Equal(3, SurfaceUpdateEngine.BandOf(5, 1)); Assert.Equal(-4, SurfaceUpdateEngine.BandOf(-9, 1));
        Assert.Equal(((byte)0xA5, (byte)0x0F, (byte)0x15), SurfaceUpdateEngine.BandRgb(4));
        Assert.Equal(((byte)0x08, (byte)0x51, (byte)0x9C), SurfaceUpdateEngine.BandRgb(-4));
        Assert.Equal(((byte)0xDD, (byte)0xDD, (byte)0xDD), SurfaceUpdateEngine.BandRgb(0));

        var (v, t) = FlatGrid(30, 10, 1000);
        var obs = new List<(double x, double y, double z)> { (150, 150, 1012), (160, 150, 1012) };
        var opt = new SurfaceUpdateEngine.Options { Algorithm = "IDW", InfluenceRadius = 60 };
        var res = SurfaceUpdateEngine.Evaluate(v, t, obs, opt);
        var sampler = SurfaceUpdateEngine.MeshSampler2D.Build(v, t)!;
        var ob = SurfaceUpdateEngine.BuildOverlay(sampler, obs, opt, res, new[] { (155.0, 150.0, "① 最大12m") }, maxCells: 8000);
        Assert.True(ob.Any, ob.Message);
        Assert.True(ob.Cells <= 8000 + 4 * 200);
        Assert.Equal(ob.Cells, ob.CellList.Count);
        Assert.Equal(ob.Cells, ob.BandCounts.Sum());
        Assert.Equal(0, ob.BandCounts[4]);   // 0 级不出格
        Assert.All(ob.CellList, c => Assert.True(c.Band > 0 && c.Disp > 0));   // 只抬升
        Assert.True(ob.MaxDispVal > 11 && ob.MinDispVal >= 0);
        Assert.Equal(10, ob.Step);   // 中位 |Δz|=12 → Nice125 = 10
        Assert.Single(ob.Labels);
        Assert.Equal(1000, ob.Labels[0].z, 9);
        Assert.All(ob.CellList, c => Assert.True(c.X0 >= res.AffMinX - 2 * ob.CellSize && c.X1 <= res.AffMaxX + 2 * ob.CellSize));

        var ents = SurfaceUpdateEngine.OverlayEntities(ob, "更新影响预览", 5);
        Assert.Equal(ob.Cells, ents.OfType<RectEntity>().Count());
        Assert.Single(ents.OfType<TextEntity>());
        Assert.All(ents, e => Assert.Equal("更新影响预览", e.LayerName));
        var top = ents.OfType<RectEntity>().First(r => ob.CellList.First(c => c.X0 == r.X0 && c.Y0 == r.Y0).Band == ob.CellList.Max(c => c.Band));
        Assert.True(top.Elevation > 1000);

        Assert.False(SurfaceUpdateEngine.BuildOverlay(sampler, obs, opt, new SurfaceUpdateEngine.Result(), Array.Empty<(double, double, string)>()).Any);
    }

    [Fact]
    public void Engine_build_updated_mesh_keeps_topology_and_applies_new_z()
    {
        var (v, t) = FlatGrid(3, 5, 1);
        var newZ = new double[v.Length / 3];
        for (int i = 0; i < newZ.Length; i++) newZ[i] = i;
        var m = SurfaceUpdateEngine.BuildUpdatedMesh(v, newZ, t, "更新");
        Assert.Equal("更新", m.Name);
        Assert.Equal(16, m.VertexCount); Assert.Equal(18, m.TriangleCount);
        Assert.Equal(7, m.Verts[7].z);
        Assert.Equal((t[3], t[4], t[5]), m.Tris[1]);
        Assert.InRange(m.Cr, 0.54f, 0.56f);   // #8C9AA8
    }
}
