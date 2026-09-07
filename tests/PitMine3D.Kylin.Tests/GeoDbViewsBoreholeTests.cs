using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>「钻孔管理」组(GeoDbViews.Borehole)查询/导入/柱状/层位/虚拟钻孔逻辑回归(对 SQLite 种子库 + 合成数据)。</summary>
public class GeoDbViewsBoreholeTests
{
    // ─────────────────────────── 钻孔表 ───────────────────────────

    [Fact]
    public void LoadBoreholes_seed_has_hundreds_sorted_by_hole_id()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDbViews.LoadBoreholes(db.Connection);
        Assert.True(rows.Count > 100, $"种子钻孔应有上百个, 实得 {rows.Count}");
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.HoleId)));
        for (int i = 1; i < rows.Count; i++)
            Assert.True(StringComparer.OrdinalIgnoreCase.Compare(rows[i - 1].HoleId, rows[i].HoleId) <= 0, "按孔号不区分大小写升序");
        Assert.Contains(rows, r => r.ZCollar is not null && r.DepthTotal is > 0);   // 有可展绘孔
        var first = rows[0];
        Assert.Equal(first.X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), first.XStr);   // 数值列文本包装
    }

    [Fact]
    public void Insert_update_delete_borehole_round_trip()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        Assert.Null(GeoDbViews.GetBoreholeByHoleId(c, "T-NEW-1"));
        var b = new GeoDbViews.BoreholeRow { HoleId = "T-NEW-1", X = 100, Y = 200, ZCollar = 1200.5, DepthTotal = 88, Category = "生产" };
        long id = GeoDbViews.InsertBorehole(c, b);
        Assert.True(id > 0);
        Assert.Equal(id, b.Id);
        var back = GeoDbViews.GetBoreholeByHoleId(c, "T-NEW-1")!;
        Assert.Equal(1200.5, back.ZCollar);
        Assert.Equal("原始", back.CoordFilled);

        back.ZCollarStr = "1300";           // 文本包装编辑 → 数值
        back.DepthTotalStr = "";            // 可空列留空 = 置空
        back.Remark = "改过";
        Assert.Equal(1, GeoDbViews.UpdateBorehole(c, back));
        var again = GeoDbViews.GetBoreholeByHoleId(c, "T-NEW-1")!;
        Assert.Equal(1300, again.ZCollar);
        Assert.Null(again.DepthTotal);
        Assert.Equal("改过", again.Remark);

        Assert.Equal(1, GeoDbViews.DeleteBorehole(c, id));
        Assert.Null(GeoDbViews.GetBoreholeByHoleId(c, "T-NEW-1"));
    }

    // ─────────────────────────── CSV 导入 / 模板 ───────────────────────────

    [Fact]
    public void Header_canon_aliases_and_required_check()
    {
        Assert.Equal("hole", GeoDbViews.BoreholeCanon("﻿钻孔编号"));
        Assert.Equal("x", GeoDbViews.BoreholeCanon("经距X"));
        Assert.Equal("y", GeoDbViews.BoreholeCanon(" y "));
        Assert.Equal("z", GeoDbViews.BoreholeCanon("Z_COLLAR"));
        Assert.Equal("depth", GeoDbViews.BoreholeCanon("孔深"));
        Assert.Null(GeoDbViews.BoreholeCanon("未知列"));
        var map = GeoDbViews.BoreholeBuildHeaderMap(new List<string> { "备注", "孔号", "经距", "纬距", "孔号" });
        Assert.Equal(1, map["hole"]);   // 重复表头取首列
        Assert.True(GeoDbViews.BoreholeHasRequired(map));
        Assert.False(GeoDbViews.BoreholeHasRequired(GeoDbViews.BoreholeBuildHeaderMap(new List<string> { "孔号", "经距" })));
        Assert.Equal("a", GeoDbViews.BoreholeField(new List<string> { " a " }, map, "remark"));   // 备注在第 0 列, 去空白
        Assert.Equal("", GeoDbViews.BoreholeField(new List<string> { "a" }, map, "y"));           // 越界空串
        Assert.Equal(1.5, GeoDbViews.BoreholeParseNum("1.5"));
        Assert.Null(GeoDbViews.BoreholeParseNum("abc"));
    }

    [Fact]
    public void Csv_records_respect_quotes_and_template_matches_headers()
    {
        var recs = GeoDbViews.BoreholeReadCsvRecords("孔号,经距X,纬距Y,备注\r\nZK1,1,2,\"a,b\"\"c\"\n\n,,,\nZK2,3,4,x");
        Assert.Equal(3, recs.Count);          // 全空行剔除
        Assert.Equal("a,b\"c", recs[1][3]);   // 引号内逗号与转义引号
        Assert.Equal("ZK2", recs[2][0]);

        var tpl = GeoDbViews.BoreholeReadCsvRecords(GeoDbViews.BoreholeCsvTemplate());
        Assert.Equal(2, tpl.Count);
        Assert.Equal(GeoDbViews.BoreholeImportHeaders, tpl[0].ToArray());
        Assert.Equal("ZK001", tpl[1][0]);
        Assert.Equal("示例行，可删除", tpl[1][9]);
    }

    [Fact]
    public void Preview_import_flags_invalid_duplicate_new_and_existing()
    {
        using var db = GeoDatabase.OpenSeeded();
        var existing = GeoDbViews.LoadBoreholes(db.Connection)[0].HoleId;
        var text = "孔号,经距X,纬距Y,孔口高程\n" +
                   ",1,2,3\n" +           // 缺孔号
                   "A1,x,2,3\n" +         // 经距非数字
                   "A2,1,y,3\n" +         // 纬距非数字
                   "A3,1,2,3\n" +         // 新增
                   "A3,1,2,3\n" +         // 文件内重复
                   $"{existing},5,6,7\n"; // 已存在 → 更新
        var (rows, msg) = GeoDbViews.BoreholePreviewImport(db.Connection, GeoDbViews.BoreholeReadCsvRecords(text));
        Assert.Equal(6, rows.Count);
        Assert.Equal("✗ 缺孔号", rows[0].Status);
        Assert.Equal("✗ 经距非数字", rows[1].Status);
        Assert.Equal("✗ 纬距非数字", rows[2].Status);
        Assert.Equal("＋ 将新增", rows[3].Status);
        Assert.Equal("✗ 文件内孔号重复", rows[4].Status);
        Assert.Equal("↻ 将更新(已存在)", rows[5].Status);
        Assert.Equal(2, rows.Count(r => r.Valid));
        Assert.Equal("解析 6 行，可导入 2 行；点「导入入库」执行", msg);

        var (none, m2) = GeoDbViews.BoreholePreviewImport(db.Connection, GeoDbViews.BoreholeReadCsvRecords("孔号,经距X\nA,1"));
        Assert.Empty(none);
        Assert.StartsWith("缺少必要列", m2);
    }

    [Fact]
    public void Apply_import_inserts_updates_and_respects_overwrite_switch()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var existing = GeoDbViews.LoadBoreholes(c)[0];
        string text = $"孔号,经距X,纬距Y,孔口高程,类别\nNEW-A,10,20,30,补勘\n{existing.HoleId},{existing.X},{existing.Y},999.5,改类别\n";
        var (rows, _) = GeoDbViews.BoreholePreviewImport(c, GeoDbViews.BoreholeReadCsvRecords(text));

        var o1 = GeoDbViews.BoreholeApplyImport(c, rows, overwrite: false);
        Assert.Equal(1, o1.Inserted); Assert.Equal(0, o1.Updated); Assert.Equal(1, o1.Skipped); Assert.Equal(0, o1.Failed);
        Assert.NotEqual(999.5, GeoDbViews.GetBoreholeByHoleId(c, existing.HoleId)!.ZCollar);

        var (rows2, _) = GeoDbViews.BoreholePreviewImport(c, GeoDbViews.BoreholeReadCsvRecords(text));
        Assert.Equal("↻ 将更新(已存在)", rows2[0].Status);   // 刚新增的孔第二次已存在
        var o2 = GeoDbViews.BoreholeApplyImport(c, rows2, overwrite: true);
        Assert.Equal(0, o2.Inserted); Assert.Equal(2, o2.Updated);
        Assert.Equal(999.5, GeoDbViews.GetBoreholeByHoleId(c, existing.HoleId)!.ZCollar);
        Assert.Equal("改类别", GeoDbViews.GetBoreholeByHoleId(c, existing.HoleId)!.Category);
    }

    [Fact]
    public void Direct_import_skips_invalid_and_reports_header_problem()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        int before = GeoDbViews.LoadBoreholes(c).Count;
        var recs = GeoDbViews.BoreholeReadCsvRecords("孔号,经距X,纬距Y\nD1,1,2\nD1,3,4\n,5,6\nD2,x,7\nD3,8,9\n");
        var o = GeoDbViews.BoreholeImportDirect(c, recs, overwrite: true, out string msg)!;
        Assert.Equal(2, o.Inserted);   // D1, D3
        Assert.Equal(3, o.Skipped);    // 重复 D1 / 缺孔号 / 非数字
        Assert.Equal(before + 2, GeoDbViews.LoadBoreholes(c).Count);
        Assert.Null(GeoDbViews.BoreholeImportDirect(c, GeoDbViews.BoreholeReadCsvRecords("孔号,经距X\nA,1"), true, out msg));
        Assert.StartsWith("缺少必要列", msg);
        Assert.Null(GeoDbViews.BoreholeImportDirect(c, GeoDbViews.BoreholeReadCsvRecords("孔号,经距X,纬距Y"), true, out msg));
        Assert.Equal("文件为空或只有表头", msg);
    }

    [Fact]
    public void Export_csv_has_12_columns_and_one_line_per_hole()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDbViews.LoadBoreholes(db.Connection);
        var csv = GeoDbViews.BoreholeExportCsv(rows);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(rows.Count + 1, lines.Length);
        Assert.Equal("孔号,经距X,纬距Y,孔口高程,总孔深,终孔层位,施工时间,施工单位,钻探评级,综合评级,类别,备注", lines[0].TrimEnd('\r'));
        Assert.StartsWith(rows[0].HoleId + ",", lines[1]);
    }

    // ─────────────────────────── 见煤成果 / 字典 ───────────────────────────

    [Fact]
    public void Seam_results_and_seam_defs_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var normal = GeoDbViews.LoadSeamResultsByStatus(c, "正常");
        Assert.True(normal.Count > 300, $"种子 '正常' 见煤成果应有数百条, 实得 {normal.Count}");
        Assert.All(normal, s => Assert.Equal("正常", s.Status));
        var byHole = GeoDbViews.LoadSeamResultsByBorehole(c, normal[0].BoreholeId);
        Assert.NotEmpty(byHole);
        Assert.All(byHole, s => Assert.Equal(normal[0].BoreholeId, s.BoreholeId));

        var defs = GeoDbViews.LoadSeamDefs(c);
        Assert.Equal(7, defs.Count);
        Assert.Equal("4", defs[0].Code);   // 按 sort_order
        Assert.Equal(((byte)0xD4, (byte)0xA0, (byte)0x17), GeoDbViews.SeamPaletteColor(defs, "4"));
        Assert.Equal(((byte)0x55, (byte)0x55, (byte)0x55), GeoDbViews.SeamPaletteColor(defs, "没有的层"));
        Assert.Equal("4-1 号煤层", GeoDbViews.SeamDisplayName(defs, "4-1"));
        Assert.Equal("Z9 号煤层", GeoDbViews.SeamDisplayName(defs, "Z9"));
        Assert.True(GeoDbViews.TryParseHexColor("2C5F8D", out var rgb) && rgb.b == 0x8D);
        Assert.False(GeoDbViews.TryParseHexColor("#12", out _));
    }

    // ─────────────────────────── 展绘钻孔 ───────────────────────────

    [Fact]
    public void Borehole_columns_from_seed_produce_columns_and_labels()
    {
        using var db = GeoDatabase.OpenSeeded();
        var holes = GeoDbViews.LoadBoreholes(db.Connection);
        // 展绘钻孔的数据来自随包 SQLite 种子库(迁移 V013, 真实平朔地质数据), 不是造的:
        Assert.Equal(241, holes.Count);                                        // borehole 表 241 孔
        Assert.True(GeoDbViews.LoadSeamResultsByStatus(db.Connection, "正常").Count > 100);   // 见煤成果("正常")
        var r = GeoDbViews.BuildBoreholeColumns(db.Connection);
        int drawable = holes.Count(h => h.ZCollar is not null && h.DepthTotal is > 0);
        Assert.Equal(drawable, r.HoleCount);
        Assert.Equal(holes.Count - drawable, r.SkippedHoles);
        Assert.True(r.SeamCount > 0);
        Assert.True(r.MeshGroups >= 2);   // 岩色 + 至少一种煤层名
        Assert.NotNull(r.Bounds);
        Assert.Equal(r.HoleCount, r.Entities.OfType<TextEntity>().Count(t => t.Height == 34.0));   // 每孔一个孔号
        Assert.Equal(r.SeamCount, r.Entities.OfType<TextEntity>().Count(t => t.Height == 19.0));   // 每煤层段一个煤层名
        // 真三维柱: 每种颜色一张合并三角网(岩色 + 各煤层编码), 柱径 = 2×半径
        var meshes = r.Entities.OfType<MeshEntity>().ToList();
        Assert.Equal(r.MeshGroups, meshes.Count);
        Assert.Contains(meshes, m => m.Name == "钻孔柱-岩层");
        Assert.All(meshes, m => Assert.True(m.TriangleCount > 0 && m.VertexCount > 0));

        var subset = GeoDbViews.BuildBoreholeColumns(db.Connection, holes.Where(h => h.ZCollar != null && h.DepthTotal > 0).Take(3).ToList());
        Assert.Equal(3, subset.HoleCount);
    }

    [Fact]
    public void Borehole_column_geometry_rock_coal_rock_top_down()
    {
        var hole = new GeoDbViews.BoreholeRow { Id = 1, HoleId = "H1", X = 1000, Y = 2000, ZCollar = 500, DepthTotal = 100 };
        var seams = new Dictionary<long, List<GeoDbViews.BoreholeSeamRow>>
        {
            [1] = new()
            {
                new GeoDbViews.BoreholeSeamRow { BoreholeId = 1, SeamCode = "4", FloorElevation = 450, AdoptedThickness = 5 },
                new GeoDbViews.BoreholeSeamRow { BoreholeId = 1, SeamCode = "9", FloorElevation = 300, AdoptedThickness = 3 },   // 低于孔底 400 → 跳过
                new GeoDbViews.BoreholeSeamRow { BoreholeId = 1, SeamCode = "11", FloorElevation = 420, AdoptedThickness = null, OverallThickness = 0 },   // 无厚度 → 跳过
            }
        };
        var r = GeoDbViews.BuildBoreholeColumns(new[] { hole }, seams);
        Assert.Equal(1, r.HoleCount); Assert.Equal(1, r.SeamCount); Assert.Equal(2, r.SkippedSeams);
        // 真三维柱: 岩段(400..450) + 煤段(450..455) + 上覆岩层(455..500), 按颜色合并成 2 张三角网
        var meshes = r.Entities.OfType<MeshEntity>().ToDictionary(m => m.Name);
        Assert.Equal(2, meshes.Count);
        var rock = meshes["钻孔柱-岩层"]; var coal = meshes["钻孔柱-4煤"];
        Assert.Equal(0x3C / 255f, coal.Cr, 3);   // 煤色
        Assert.Equal(0x5F / 255f, rock.Cr, 3);   // 岩色
        // 28 棱: 每段 56 顶点/56 三角; 岩=2 段 + 底盖 + 顶盖(各 +1 顶点/28 三角), 煤=中间段不封盖
        Assert.Equal(56, coal.VertexCount); Assert.Equal(56, coal.TriangleCount);
        Assert.Equal(114, rock.VertexCount); Assert.Equal(168, rock.TriangleCount);
        var cb = coal.Bounds; var rb = rock.Bounds;
        Assert.Equal(450, cb.minZ, 6); Assert.Equal(455, cb.maxZ, 6);            // 煤层段落在真实高程
        Assert.Equal(400, rb.minZ, 6); Assert.Equal(500, rb.maxZ, 6);            // 孔底 → 孔口
        Assert.Equal(1000 - 7, cb.minX, 6); Assert.Equal(1000 + 7, cb.maxX, 6);  // 柱径 = 2×7m
        // 注记始终朝屏幕(原版 screenFacing), 放在真实高程上
        var seamTxt = Assert.Single(r.Entities.OfType<TextEntity>().Where(t => t.Text == "4煤"));
        Assert.True(seamTxt.ScreenFacing); Assert.Equal(452.5, seamTxt.Elevation, 6);
        var holeTxt = Assert.Single(r.Entities.OfType<TextEntity>().Where(t => t.Text == "H1"));
        Assert.True(holeTxt.ScreenFacing); Assert.Equal(1, holeTxt.HAlign);
        Assert.Equal(500 + 34 * 0.6, holeTxt.Elevation, 6);
        // 引线: 柱面 → 标注锚点, 三维虚线
        var leader = Assert.Single(r.Entities.OfType<PolylineEntity>().Where(p => p.Dash != null));
        Assert.True(leader.Has3D);
        Assert.Equal(1000 + 7, leader.Points[0].x, 6);
        Assert.Equal(452.5, leader.Zs![0], 6);
    }

    // ─────────────────────────── 原始钻孔柱状图 ───────────────────────────

    [Fact]
    public void Original_segments_fill_hole_with_rock_between_coal()
    {
        var defs = new List<GeoDbViews.SeamDefRow> { new("4", "4 号煤层", 10, "#D4A017") };
        var seams = new List<GeoDbViews.BoreholeSeamRow>
        {
            new() { SeamCode = "4", LogEndDepth = 50, AdoptedThickness = 5, RoofLithology = "泥岩", FloorLithology = "砂岩" },
            new() { SeamCode = "9", FloorElevation = 920, OverallThickness = 2, Status = "正常" },   // 无测井止煤深 → 孔口 1000 − 底板 920 = 80
            new() { SeamCode = "11", Status = "未达" },
            new() { SeamCode = "7-1", LogEndDepth = 60 },   // 无厚度 → 跳过
        };
        var segs = GeoDbViews.BuildOriginalSegments(1000, 100, seams, defs, out int skipped);
        Assert.Equal(2, skipped);
        Assert.Equal(new[] { "上覆岩层", "4 号煤层", "层间岩层", "9 号煤层", "底部岩层" }, segs.Select(s => s.LayerName).ToArray());
        Assert.Equal(45, segs[1].DepthTop, 6); Assert.Equal(50, segs[1].DepthBot, 6);
        Assert.Equal(955, segs[1].ZTop, 6); Assert.Equal(950, segs[1].ZBot, 6);
        Assert.Equal("泥岩", segs[0].RockLith);          // 上覆岩层: 下邻煤层顶板岩性
        Assert.Equal("砂岩", segs[2].RockLith);          // 层间: 上邻煤层底板岩性(下邻无顶板)
        Assert.Equal(78, segs[3].DepthTop, 6); Assert.Equal(80, segs[3].DepthBot, 6);
        Assert.Equal(GeoDbViews.OcRockBase, segs[4].Fill);
        Assert.Equal(100, segs[4].DepthBot, 6);
        Assert.Equal(((byte)0xD4, (byte)0xA0, (byte)0x17), segs[1].Fill);

        var none = GeoDbViews.BuildOriginalSegments(1000, 30, Array.Empty<GeoDbViews.BoreholeSeamRow>(), defs, out _);
        Assert.Single(none);
        Assert.Equal("岩层", none[0].LayerName);
    }

    [Fact]
    public void Original_segments_on_seed_hole_have_coal()
    {
        using var db = GeoDatabase.OpenSeeded();
        var defs = GeoDbViews.LoadSeamDefs(db.Connection);
        var hole = GeoDbViews.LoadBoreholes(db.Connection).First(h => h.ZCollar != null && h.DepthTotal > 0
            && GeoDbViews.LoadSeamResultsByBorehole(db.Connection, h.Id).Any(s => s.Status == "正常"));
        var segs = GeoDbViews.BuildOriginalSegments(hole.ZCollar!.Value, hole.DepthTotal!.Value,
            GeoDbViews.LoadSeamResultsByBorehole(db.Connection, hole.Id), defs, out _);
        Assert.Contains(segs, s => s.IsCoal);
        Assert.All(segs, s => Assert.True(s.DepthBot >= s.DepthTop - 1e-9 && s.DepthTop >= 0 && s.DepthBot <= hole.DepthTotal.Value + 1e-9));
    }

    // ─────────────────────────── 展绘层位数据 ───────────────────────────

    [Fact]
    public void Horizon_points_available_seams_ordered_and_layers_built()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var seams = GeoDbViews.HorizonAvailableSeams(c);
        Assert.NotEmpty(seams);
        var order = new[] { "4", "4-1", "4-2", "4（4-1）", "7-1", "9", "11" };
        var known = seams.Where(order.Contains).Select(s => Array.IndexOf(order, s)).ToList();
        Assert.Equal(known.OrderBy(i => i), known);   // 地质顺序浅→深

        var all = GeoDbViews.BuildHorizonPoints(c);
        Assert.True(all.FloorPoints > 100);
        Assert.True(all.RoofPoints > 0 && all.RoofPoints <= all.FloorPoints);
        Assert.Equal(all.Layers.Count, all.SeamLayers);
        Assert.True(all.SeamLayers <= 2 * seams.Count);
        Assert.All(all.Layers, l => Assert.StartsWith("层位_", l.Name));

        var floorOnly = GeoDbViews.BuildHorizonPoints(c, new HashSet<string> { seams[0] }, includeRoof: false, includeFloor: true);
        Assert.Equal(0, floorOnly.RoofPoints);
        Assert.Single(floorOnly.Layers);
        Assert.Equal($"层位_{seams[0]}_底板", floorOnly.Layers[0].Name);
        Assert.True(floorOnly.FloorPoints < all.FloorPoints);

        var ents = GeoDbViews.HorizonLayerEntities(floorOnly.Layers[0]);
        Assert.Equal(floorOnly.FloorPoints, ents.Count);
        Assert.All(ents, e => Assert.IsType<PointEntity>(e));
        Assert.Equal(floorOnly.Layers[0].Pts[0].z, ents[0].Elevation);
        Assert.Equal(((byte)0xD4, (byte)0xA0, (byte)0x17), GeoDbViews.HorizonSeamColor("4"));
        Assert.Equal(((byte)0x88, (byte)0x88, (byte)0x88), GeoDbViews.HorizonSeamColor("??"));
    }

    // ─────────────────────────── 虚拟钻孔: 几何打包 / 持久化 ───────────────────────────

    [Fact]
    public void Vd_pack_unpack_round_trip_and_reject_corrupt()
    {
        var verts = new double[] { 0, 0, 10, 1, 0, 11, 0, 1, 12, 1, 1, 13 };
        var tris = new[] { 0, 1, 2, 1, 3, 2 };
        string b64 = GeoDbViews.VdPack(verts, tris);
        Assert.NotEmpty(b64);
        Assert.True(GeoDbViews.VdTryUnpack(b64, out var v2, out var t2));
        Assert.Equal(verts, v2); Assert.Equal(tris, t2);
        Assert.Equal("", GeoDbViews.VdPack(new double[] { 0, 0, 0 }, tris));   // 顶点不足
        Assert.False(GeoDbViews.VdTryUnpack("not base64!", out _, out _));
        Assert.False(GeoDbViews.VdTryUnpack(Convert.ToBase64String(new byte[20]), out _, out _));   // 坏 magic
        Assert.False(GeoDbViews.VdTryUnpack(GeoDbViews.VdPack(verts, new[] { 0, 1, 9 }), out _, out _));   // 索引越界
        GeoDbViews.VdComputeBounds(verts, out var minX, out _, out var minZ, out var maxX, out _, out var maxZ);
        Assert.Equal(0, minX); Assert.Equal(1, maxX); Assert.Equal(10, minZ); Assert.Equal(13, maxZ);
    }

    [Fact]
    public void Vd_surfaces_upsert_meta_and_clear()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        Assert.False(GeoDbViews.VdHasAnySurface(c));
        var verts = new double[] { 0, 0, 100, 10, 0, 100, 0, 10, 100, 10, 10, 100 };
        var tris = new[] { 0, 1, 2, 1, 3, 2 };
        long id1 = GeoDbViews.VdSaveSurface(c, "roof", "4", 0, "#112233", "L顶", verts, tris);
        long id2 = GeoDbViews.VdSaveSurface(c, "roof", "4", 0, "#445566", "L顶2", verts, tris);   // 同键覆盖
        Assert.True(id1 > 0); Assert.Equal(id1, id2);
        Assert.Equal(0, GeoDbViews.VdSaveSurface(c, "floor", "4", 0, "#112233", "", new double[3], tris));   // 非法几何不写
        GeoDbViews.VdSaveSurface(c, "floor", "4", 0, "#445566", "L底", verts, tris);
        GeoDbViews.VdSaveSurface(c, "surface", "", 0, "#8B7355", "地表", verts, tris);
        Assert.True(GeoDbViews.VdHasAnySurface(c));
        var all = GeoDbViews.VdAllSurfaces(c);
        Assert.Equal(3, all.Count);
        var roof = GeoDbViews.VdGetByKey(c, "roof", "4")!;
        Assert.Equal("#445566", roof.ColorHex); Assert.Equal("L顶2", roof.SourceLayer);
        Assert.Equal(4, roof.VertexCount); Assert.Equal(2, roof.TriangleCount);
        Assert.Equal(10, roof.MaxX); Assert.Equal(100, roof.MinZ);
        Assert.True(GeoDbViews.VdTryGetGeometry(c, roof.Id, out var v, out var t));
        Assert.Equal(verts, v); Assert.Equal(tris, t);
        Assert.False(GeoDbViews.VdTryGetGeometry(c, 99999, out _, out _));

        Assert.Equal(2, GeoDbViews.VdUpdateSeamMeta(c, "4", 3, "#ABCDEF"));   // 顶+底同步
        Assert.All(GeoDbViews.VdAllSurfaces(c).Where(s => s.SeamName == "4"), s => { Assert.Equal(3, s.SeamOrder); Assert.Equal("#ABCDEF", s.ColorHex); });
        Assert.Equal(3, GeoDbViews.VdClearAll(c));
        Assert.False(GeoDbViews.VdHasAnySurface(c));
    }

    // ─────────────────────────── 虚拟钻孔: 求交 / 柱状 ───────────────────────────

    private static (double[] verts, int[] tris) Plane(double z)
        => (new double[] { 0, 0, z, 100, 0, z, 0, 100, z, 100, 100, z }, new[] { 0, 1, 2, 1, 3, 2 });

    [Fact]
    public void Vd_drill_intersects_surface_and_seams()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var (sv, st) = Plane(100);
        GeoDbViews.VdSaveSurface(c, "surface", "", 0, "", "", sv, st);
        var (r4, t4) = Plane(80); GeoDbViews.VdSaveSurface(c, "roof", "4", 0, "#D4A017", "", r4, t4);
        var (f4, tf4) = Plane(75); GeoDbViews.VdSaveSurface(c, "floor", "4", 0, "#D4A017", "", f4, tf4);
        var (r9, t9) = Plane(60); GeoDbViews.VdSaveSurface(c, "roof", "9", 1, "#4A7C2E", "", r9, t9);
        var (f9, tf9) = Plane(58); GeoDbViews.VdSaveSurface(c, "floor", "9", 1, "#4A7C2E", "", f9, tf9);
        GeoDbViews.VdSaveSurface(c, "roof", "11", 2, "bad", "", r9, t9);   // 只有顶板 → 缺失

        var model = GeoDbViews.VdBuildModel(c);
        Assert.True(model.HasSurface);
        Assert.Equal(3, model.SeamCount);
        Assert.Equal(6, model.SamplerCount);
        Assert.Equal(new[] { "4", "9", "11" }, model.Seams.Select(s => s.Name).ToArray());
        Assert.Equal((0xD4, 0xA0, 0x17), (model.Seams[0].R, model.Seams[0].G, model.Seams[0].B));
        Assert.Equal((0x3C, 0x3C, 0x3C), (model.Seams[2].R, model.Seams[2].G, model.Seams[2].B));   // 坏 hex 回退

        var r = GeoDbViews.VdDrill(model, 50, 50);
        Assert.Equal(100, r.SurfaceZ!.Value, 6);
        Assert.Equal(2, r.SeamsPresent);
        Assert.True(r.Seams[0].Present); Assert.Equal(80, r.Seams[0].RoofZ, 6); Assert.Equal(5, r.Seams[0].Thickness, 6);
        Assert.Equal(15, r.Seams[0].Interval!.Value, 6);   // 75 − 60
        Assert.True(r.Seams[1].Present); Assert.Null(r.Seams[1].Interval);
        Assert.False(r.Seams[2].Present);
        Assert.Equal(7, r.TotalCoal, 6);
        Assert.Equal(100, r.ColumnTopZ); Assert.Equal(58, r.ColumnBottomZ);
        Assert.Equal(35, r.TotalRock, 6);                    // 42 − 7
        Assert.Equal(5, r.StripRatio!.Value, 6);

        var outside = GeoDbViews.VdDrill(model, 500, 500);
        Assert.Equal(0, outside.SeamsPresent);
        Assert.Null(outside.SurfaceZ); Assert.Null(outside.ColumnTopZ); Assert.Null(outside.StripRatio);

        var layers = GeoDbViews.VdBuildColumnLayers(r);
        Assert.Equal(new[] { "9", "夹层", "4", "上覆岩层" }, layers.Select(l => l.Name).ToArray());
        Assert.Equal(15, layers[1].Thickness, 6);
        Assert.Equal(20, layers[3].Thickness, 6);
        Assert.Empty(GeoDbViews.VdBuildColumnLayers(outside));

        var ents = GeoDbViews.VdBuildColumnEntities(r, out var bounds);
        Assert.NotNull(bounds);
        Assert.Equal(4, ents.OfType<RectEntity>().Count());
        Assert.Contains(ents.OfType<TextEntity>(), t => t.Text == "4煤 厚5m");
        Assert.Contains(ents.OfType<TextEntity>(), t => t.Text == "上覆岩层 厚20m" && t.HAlign == 2);
        Assert.Contains(ents.OfType<TextEntity>(), t => t.Text == "地表 100");
        Assert.Empty(GeoDbViews.VdBuildColumnEntities(outside, out var nb));
        Assert.Null(nb);
    }

    [Fact]
    public void Vd_auto_detect_pairs_roof_floor_by_layer_name()
    {
        var m = GeoDbViews.VdAutoDetect(new[] { "道路", "9煤底板", "现状地表", "4-1煤顶板", "4-1煤底板", "9煤顶板", "孤儿顶板" });
        Assert.Equal("现状地表", m.SurfaceLayer);
        Assert.Equal(2, m.Seams.Count);
        Assert.Equal("4-1", m.Seams[0].Name); Assert.Equal("4-1煤顶板", m.Seams[0].RoofLayer); Assert.Equal("4-1煤底板", m.Seams[0].FloorLayer);
        Assert.Equal("9", m.Seams[1].Name); Assert.Equal(1, m.Seams[1].Order);
        Assert.Empty(GeoDbViews.VdAutoDetect(new[] { "a", "b" }).Seams);
    }

    [Fact]
    public void Vd_mesh_sources_from_vertices_and_off()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 1), (10, 0, 2), (0, 10, 3), (10, 10, 4), (10, 10, 4) };   // 末点重复
        Assert.True(GeoDbViews.VdMeshFromVertices(pts, out var v, out var t));
        Assert.Equal(12, v.Length);   // 去重后 4 点
        Assert.Equal(6, t.Length);    // 2 三角
        Assert.False(GeoDbViews.VdMeshFromVertices(pts.Take(2).ToList(), out _, out _));

        var off = MeshWeld.ToOff(new[] { (0.0, 0.0, 5.0), (1.0, 0.0, 5.0), (0.0, 1.0, 5.0) }, new[] { (0, 1, 2) });
        Assert.True(GeoDbViews.VdMeshFromOff(off, out var ov, out var ot));
        Assert.Equal(9, ov.Length); Assert.Equal(new[] { 0, 1, 2 }, ot);
        Assert.False(GeoDbViews.VdMeshFromOff("junk", out _, out _));

        var sampler = GeoDbViews.VdTinSampler.TryBuild(ov, ot)!;
        Assert.True(sampler.TrySampleZ(0.2, 0.2, out double z) && Math.Abs(z - 5) < 1e-9);
        Assert.False(sampler.TrySampleZ(5, 5, out _));
        Assert.Null(GeoDbViews.VdTinSampler.TryBuild(new double[3], ot));
    }
}
