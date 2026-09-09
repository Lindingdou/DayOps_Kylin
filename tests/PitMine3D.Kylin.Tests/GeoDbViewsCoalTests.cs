using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>「煤质管理」组(GeoDbViews.Coal): 煤样 CRUD / CSV 闭环 / 层平均重算 / 统计 / 插值 / 结论 —— 对 SQLite 种子库。</summary>
public class GeoDbViewsCoalTests
{
    // ─── 字典 + 煤样读取 ───
    [Fact]
    public void Seed_dictionaries_and_samples_load()
    {
        using var db = TestDb.Open();
        var seams = GeoDbViews.CoalSeamDefs(db.Connection);
        Assert.Equal(7, seams.Count);                                          // V005 种子 7 个煤层
        Assert.Equal("4", seams[0].Code);                                      // sort_order 10 首位
        Assert.Equal("#D4A017", seams[0].ColorHex);
        Assert.Equal(16, GeoDbViews.CoalClassRefs(db.Connection).Count);      // GB/T 5751 节选 16 种
        var ash = GeoDbViews.CoalGradeRules(db.Connection, "ash");
        Assert.Equal(5, ash.Count);
        Assert.Equal("特低灰", GeoDbViews.CoalFindLevel(ash, 5)!.LevelName);   // [-∞,10)
        Assert.Equal("中灰", GeoDbViews.CoalFindLevel(ash, 20)!.LevelName);    // 20 落 [20,30) 半开
        Assert.Null(GeoDbViews.CoalFindLevel(ash, null));

        var rows = GeoDbViews.CoalLoadSamples(db.Connection);
        Assert.True(rows.Count >= 200, $"种子煤样应为数百段, 实际 {rows.Count}");
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.HoleId)));
        Assert.True(rows.Count(r => r.AdRaw.HasValue) > rows.Count * 0.9);   // 灰分覆盖率高
        Assert.True(rows.Count(r => r.MadRaw.HasValue) > 0);                   // 全列映射(原 GetCoalSamples 无 Mad)
        // 排序: 孔号 → 煤层 → 起深
        var ordered = rows.OrderBy(r => r.HoleId, StringComparer.Ordinal).ThenBy(r => r.SeamCode, StringComparer.Ordinal).ThenBy(r => r.DepthFrom).Select(r => r.Id).ToList();
        Assert.Equal(ordered, rows.Select(r => r.Id).ToList());
    }

    [Fact]
    public void Seam_color_from_dictionary_with_fallback()
    {
        using var db = TestDb.Open();
        var seams = GeoDbViews.CoalSeamDefs(db.Connection);
        Assert.Equal(((byte)0x4A, (byte)0x7C, (byte)0x2E), GeoDbViews.CoalSeamColor(seams, "9"));
        Assert.Equal(((byte)0x55, (byte)0x55, (byte)0x55), GeoDbViews.CoalSeamColor(seams, "不存在"));
        Assert.Equal(((byte)0xC6, (byte)0x28, (byte)0x28), GeoDbViews.CoalHexToRgb("#C62828", (0, 0, 0)));
        Assert.Equal(((byte)1, (byte)2, (byte)3), GeoDbViews.CoalHexToRgb("zz", (1, 2, 3)));
    }

    // ─── CRUD 真写库 ───
    [Fact]
    public void Insert_update_delete_roundtrip_writes_db()
    {
        using var db = TestDb.Open();
        var bh = GeoDbViews.CoalBoreholes(db.Connection).First();
        int before = GeoDbViews.CoalLoadSamples(db.Connection).Count;
        var row = new GeoDbViews.CoalSampleRow
        {
            BoreholeId = bh.Id, SeamCode = "9", DepthFrom = 999.5, DepthTo = 1001.0, SampleThickness = 1.5,
            AdRaw = 12.5, StdRaw = 0.8, VdafRaw = 38, CakingG = 40, CoalType = "QM", CharResidueRaw = 4, Remark = "单测",
        };
        long id = GeoDbViews.CoalInsertSample(db.Connection, row);
        Assert.True(id > 0);
        Assert.Equal(before + 1, GeoDbViews.CoalLoadSamples(db.Connection).Count);

        var got = GeoDbViews.CoalGetSample(db.Connection, id)!;
        Assert.Equal(bh.HoleId, got.HoleId);
        Assert.Equal(12.5, got.AdRaw!.Value, 6);
        Assert.Equal(4, got.CharResidueRaw);
        Assert.Equal("QM", got.CoalType);

        got.AdRaw = 20.25; got.CoalType = "不是字典码";
        Assert.Equal(1, GeoDbViews.CoalUpdateSample(db.Connection, got));
        var again = GeoDbViews.CoalGetSample(db.Connection, id)!;
        Assert.Equal(20.25, again.AdRaw!.Value, 6);
        Assert.Null(again.CoalType);                                           // 非字典煤类降级 NULL(免外键失败)

        Assert.Equal(1, GeoDbViews.CoalDeleteSample(db.Connection, id));
        Assert.Null(GeoDbViews.CoalGetSample(db.Connection, id));
        Assert.Equal(before, GeoDbViews.CoalLoadSamples(db.Connection).Count);
    }

    [Fact]
    public void Rebuild_summary_writes_one_row_per_hole_seam()
    {
        using var db = TestDb.Open();
        var rows = GeoDbViews.CoalLoadSamples(db.Connection);
        int groups = rows.Select(r => (r.BoreholeId, r.SeamCode)).Distinct().Count();
        int n = GeoDbViews.CoalRebuildSummary(db.Connection);
        Assert.Equal(groups, n);
        var sums = GeoDataQueries.GetCoalSampleSummaries(db.Connection);
        Assert.Equal(groups, sums.Count);
        Assert.Equal(rows.Count, sums.Sum(s => s.SampleCount));
        // 抽一组核平均
        var g = rows.GroupBy(r => (r.BoreholeId, r.SeamCode)).First(x => x.Count(r => r.AdRaw.HasValue) >= 2);
        var s = sums.First(x => x.BoreholeId == g.Key.BoreholeId && x.SeamCode == g.Key.SeamCode);
        Assert.Equal(g.Where(r => r.AdRaw.HasValue).Average(r => r.AdRaw!.Value), s.AvgAdRawPct!.Value, 6);
        Assert.Equal(n, GeoDbViews.CoalRebuildSummary(db.Connection));       // 重算幂等
    }

    // ─── CSV 闭环 ───
    [Fact]
    public void Csv_template_and_export_import_roundtrip()
    {
        Assert.Equal(30, GeoDbViews.CoalCsvHeaders.Length);
        Assert.Equal(30, GeoDbViews.CoalCsvExample.Length);
        string tpl = GeoDbViews.CoalTemplateCsv();
        Assert.StartsWith("孔号,煤层,采样起深", tpl);
        Assert.Contains("示例行", tpl);

        using var db = TestDb.Open();
        var all = GeoDbViews.CoalLoadSamples(db.Connection);
        var two = all.Take(2).ToList();
        string csv = GeoDbViews.CoalCsvText(two.Select(GeoDbViews.CoalRowCells));
        Assert.Equal(3, csv.TrimEnd('\n', '\r').Split('\n').Length);

        // 同键覆盖: 2 覆盖 0 新增; 跳过策略: 2 跳过
        var rep = GeoDbViews.CoalImportCsv(db.Connection, csv, GeoDbViews.CoalConflict.Overwrite);
        Assert.Equal(2, rep.Overwritten); Assert.Equal(0, rep.Inserted);
        Assert.True(rep.SummaryRows > 0);
        Assert.Equal(all.Count, GeoDbViews.CoalLoadSamples(db.Connection).Count);
        var rep2 = GeoDbViews.CoalImportCsv(db.Connection, csv, GeoDbViews.CoalConflict.Skip);
        Assert.Equal(2, rep2.Skipped); Assert.Equal(0, rep2.Inserted);
        Assert.Throws<InvalidOperationException>(() => GeoDbViews.CoalImportCsv(db.Connection, csv, GeoDbViews.CoalConflict.Abort));

        // 新段 + 未知孔号 + 4-x 归一
        string hole = two[0].HoleId;
        string add = "孔号,煤层,采样起深,采样止深,Ad_原,煤类\n" +
                     $"{hole},9,5000,5002.5,33.3,JM\n" +
                     "NOHOLE,9,1,2,10,\n" +
                     $"{hole},4-9,6000,6001,11,\n";
        var rep3 = GeoDbViews.CoalImportCsv(db.Connection, add, GeoDbViews.CoalConflict.Skip);
        Assert.Equal(2, rep3.Inserted); Assert.Equal(1, rep3.Skipped);
        Assert.Contains("NOHOLE", rep3.UnknownHoles);
        Assert.Contains("NOHOLE", rep3.ToMessage());
        var after = GeoDbViews.CoalLoadSamples(db.Connection);
        var ins = after.First(r => r.DepthFrom == 5000);
        Assert.Equal(33.3, ins.AdRaw!.Value, 6); Assert.Equal("JM", ins.CoalType);
        Assert.Equal("4", after.First(r => r.DepthFrom == 6000).SeamCode);   // 字典无 4-9 → 并入 4

        Assert.Throws<InvalidOperationException>(() => GeoDbViews.CoalImportCsv(db.Connection, "孔号,煤层\nA,B\n", GeoDbViews.CoalConflict.Skip)); // 缺必填列
    }

    // ─── 统计 ───
    [Fact]
    public void Stats_by_seam_on_seed()
    {
        using var db = TestDb.Open();
        var rows = GeoDbViews.CoalLoadSamples(db.Connection);
        var st = GeoDbViews.CoalStatsBySeam(rows, "ad_raw");
        Assert.True(st.Count >= 3);
        Assert.Equal(rows.Count(r => r.AdRaw.HasValue), st.Sum(s => s.Count));
        Assert.Equal(st.Select(s => s.SeamCode).OrderBy(s => s, StringComparer.Ordinal).ToList(), st.Select(s => s.SeamCode).ToList());
        foreach (var s in st)
        {
            Assert.True(s.Min <= s.P25 && s.P25 <= s.P50 && s.P50 <= s.P75 && s.P75 <= s.Max);
            Assert.True(s.Std >= 0);
            Assert.Contains(s.SampleSizeLevel, new[] { "🟢 充分", "🟡 紧张", "🔴 不足" });
        }
        Assert.Equal("ad_clean", GeoDbViews.CoalMapColumn("ad_raw", true));
        Assert.Equal("caking_g", GeoDbViews.CoalMapColumn("caking_g", true));
        string csv = GeoDbViews.CoalStatsCsv(st);
        Assert.Equal(st.Count + 1, csv.TrimEnd('\n', '\r').Split('\n').Length);
    }

    [Fact]
    public void Histogram_kpi_percentile_correlation()
    {
        var vals = Enumerable.Range(0, 80).Select(i => (double)i).ToList();
        var (labels, bins) = GeoDbViews.CoalHistogram(vals);
        Assert.Equal(10, bins.Length);                                         // 80/8 = 10 ∈ [5,15]
        Assert.Equal(80, bins.Sum());
        Assert.Equal("0.0", labels[0]);
        Assert.Empty(GeoDbViews.CoalHistogram(new List<double>()).bins);

        var kpi = GeoDbViews.CoalKpiOf(new[] { 2.0, 4.0, 6.0 })!;
        Assert.Equal(4, kpi.Mean, 6); Assert.Equal(2, kpi.Std, 6); Assert.Equal(50, kpi.CvPct, 6);
        Assert.Null(GeoDbViews.CoalKpiOf(Array.Empty<double>()));
        Assert.Equal(25, GeoDbViews.CoalPctl(new[] { 10.0, 20, 30, 40 }, 50), 6);

        Assert.Equal(1.0, GeoDbViews.CoalPearson(new[] { 1.0, 2, 3 }, new[] { 2.0, 4, 6 })!.Value, 6);
        Assert.Null(GeoDbViews.CoalPearson(new[] { 1.0, 2 }, new[] { 1.0, 2 }));

        using var db = TestDb.Open();
        var rows = GeoDbViews.CoalLoadSamples(db.Connection);
        var m = GeoDbViews.CoalCorrelationMatrix(rows, false);
        int n = GeoDbViews.CoalCorrNames.Length;
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(1.0, m[i, i]);
            for (int j = 0; j < n; j++)
            {
                Assert.Equal(m[i, j], m[j, i]);
                if (m[i, j].HasValue) Assert.InRange(m[i, j]!.Value, -1, 1);
            }
        }
        Assert.True(m[0, 1].HasValue);                                         // Ad-Vdaf 成对样本充足
    }

    [Fact]
    public void Outliers_grade_counts_type_counts()
    {
        using var db = TestDb.Open();
        var rows = GeoDbViews.CoalLoadSamples(db.Connection);
        var od = GeoDbViews.CoalDetectOutliers(rows, "ad_raw", null);
        Assert.Equal(rows.Count(r => r.AdRaw.HasValue), od.N);
        Assert.True(od.Upper > od.Lower);
        Assert.All(od.Outliers, o => Assert.True(o.Value < od.Lower || o.Value > od.Upper));
        var g = GeoDbViews.CoalDetectOutliers(rows, "caking_g", "全部");          // 原窗支持 G 指标
        Assert.Equal(rows.Count(r => r.CakingG.HasValue), g.N);
        Assert.Empty(GeoDbViews.CoalDetectOutliers(rows.Take(3).ToList(), "ad_raw", null).Outliers);

        var ash = GeoDbViews.CoalGradeRules(db.Connection, "ash");
        var gc = GeoDbViews.CoalGradeCounts(ash, rows.Where(r => r.AdRaw.HasValue).Select(r => r.AdRaw!.Value));
        Assert.Equal(rows.Count(r => r.AdRaw.HasValue), gc.Sum(x => x.count));
        Assert.All(gc, x => Assert.False(string.IsNullOrEmpty(x.colorHex)));

        var seams = rows.Select(r => r.SeamCode).Distinct().OrderBy(s => s).ToList();
        var dist = GeoDbViews.CoalGradeDistribution(rows, seams, ash, s => s.AdRaw);
        Assert.Equal(rows.Count(r => r.AdRaw.HasValue), dist.Sum(d => d.counts.Sum()));
        Assert.All(dist, d => Assert.Equal(seams.Count, d.counts.Length));

        var tc = GeoDbViews.CoalTypeCounts(rows);
        Assert.Equal(rows.Count, tc.Sum(t => t.count));
        Assert.Equal("(未标注)", tc.Last().code);
        Assert.Equal("强粘结", GeoDbViews.CoalCakingWord(70)); Assert.Equal("弱粘结", GeoDbViews.CoalCakingWord(10));
    }

    // ─── 空间分布 ───
    private static List<OrdinaryKriging.ControlPoint> Grid()
    {
        var pts = new List<OrdinaryKriging.ControlPoint>();
        for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++)
            pts.Add(new OrdinaryKriging.ControlPoint(i * 100, j * 100, 1000, 10 + i * 2 + j * 0.1));   // 沿 X 递增
        return pts;
    }

    [Fact]
    public void Interpolate_all_methods_and_exact_on_control()
    {
        var pts = Grid();
        foreach (var m in GeoDbViews.CoalInterpMethods)
        {
            var vox = GeoDbViews.CoalInterpolate(pts, m, 50, 2, 5);
            Assert.True(vox.Count > 0, m);
            var at = vox.First(v => Math.Abs(v.X - 200) < 1e-6 && Math.Abs(v.Y - 200) < 1e-6);
            Assert.Equal(pts.First(p => p.X == 200 && p.Y == 200).V, at.Value, 6);       // 落控制点上精确
            Assert.All(vox, v => Assert.InRange(v.Value, 9.9, 18.5));                    // 不越样本值域
            if (m.Contains("OK")) Assert.Contains(vox, v => v.Variance.HasValue);
            else Assert.All(vox, v => Assert.Null(v.Variance));
        }
        Assert.Throws<InvalidOperationException>(() => GeoDbViews.CoalInterpolate(pts, "IDW", 1, 2, 5, maxVoxels: 100));
        Assert.Empty(GeoDbViews.CoalInterpolate(new List<OrdinaryKriging.ControlPoint>(), "IDW", 50));
    }

    [Fact]
    public void Spatial_conclusion_coverage_colormap_confidence()
    {
        var pts = Grid();
        string c = GeoDbViews.CoalSpatialConclusion(pts, "ad_raw");
        Assert.Contains("Ad 灰分", c);
        Assert.Contains("平面自西向东递增", c);                                  // 值随 X 增
        Assert.Contains("垂向无明显趋势", c);                                    // Z 常数
        Assert.Contains("不足以判空间趋势", GeoDbViews.CoalSpatialConclusion(pts.Take(3).ToList(), "ad_raw"));

        Assert.Equal(((byte)0x31, (byte)0x6D, (byte)0xC2), GeoDbViews.CoalColormap("warm", 0));
        Assert.Equal(((byte)0xD8, (byte)0x3A, (byte)0x2E), GeoDbViews.CoalColormap("warm", 1));
        Assert.Equal(((byte)0xFD, (byte)0xE7, (byte)0x25), GeoDbViews.CoalColormap("spectral", 5));   // 越界钳到 1
        Assert.Equal(1.0, GeoDbViews.CoalConfidenceKeep(null, 0, 0, 100), 6);
        Assert.Equal(0.5, GeoDbViews.CoalConfidenceKeep(5, 10, 0, 100), 6);
        Assert.Equal(0.12, GeoDbViews.CoalConfidenceKeep(null, 0, 10000, 100), 6);

        using var db = TestDb.Open();
        var rows = GeoDbViews.CoalLoadSamples(db.Connection).Where(r => r.ZSample.HasValue).ToList();
        var cov = GeoDbViews.CoalCoverageOf(rows)!;
        Assert.Equal(rows.Count, cov.N);
        Assert.True(cov.AreaKm2 > 0 && cov.Holes > 1 && cov.AvgDistM > 0);
        Assert.Equal("🟢 充分", cov.Rating);                                    // ≥100 控制点
        Assert.Contains("控制点", cov.Text);
        Assert.Null(GeoDbViews.CoalCoverageOf(new List<GeoDbViews.CoalSampleRow>()));
    }

    // ─── 钻孔柱状图 ───
    [Fact]
    public void Boreholes_with_samples_seam_results_hole_summary()
    {
        using var db = TestDb.Open();
        var holes = GeoDbViews.CoalBoreholesWithSamples(db.Connection);
        var rows = GeoDbViews.CoalLoadSamples(db.Connection);
        Assert.Equal(rows.Select(r => r.BoreholeId).Distinct().Count(), holes.Count);
        Assert.True(holes.Count < GeoDbViews.CoalBoreholes(db.Connection).Count);
        var srs = GeoDbViews.CoalSeamResults(db.Connection);
        Assert.True(srs.Count > 500);
        Assert.Contains(srs, s => s.Status == "正常" && s.FloorElevation.HasValue);
        Assert.Equal(1300 - 50, GeoDbViews.CoalSeamSegmentFloor(new GeoDbViews.CoalSeamResultRow(1, "9", 50, 2, null, "正常"), 1300));
        Assert.Equal(1250.5, GeoDbViews.CoalSeamSegmentFloor(new GeoDbViews.CoalSeamResultRow(1, "9", 50, 2, 1250.5, "正常"), 1300));

        var ash = GeoDbViews.CoalGradeRules(db.Connection, "ash");
        var sul = GeoDbViews.CoalGradeRules(db.Connection, "sulfur");
        var h = holes.First();
        var hs = GeoDbViews.CoalSamplesByBorehole(db.Connection, h.Id);
        Assert.NotEmpty(hs);
        string sum = GeoDbViews.CoalHoleSummary(hs, ash, sul);
        Assert.Contains("见煤", sum); Assert.Contains("主采", sum);
        Assert.Equal("该孔无化验段。", GeoDbViews.CoalHoleSummary(new List<GeoDbViews.CoalSampleRow>(), ash, sul));
    }

    [Fact]
    public void Analytics_bridge_from_full_rows()
    {
        using var db = TestDb.Open();
        var rows = GeoDbViews.CoalLoadSamples(db.Connection);
        var an = rows.Select(r => r.ToAnalytics()).ToList();
        Assert.Equal(rows.Count, an.Count);
        var conc = CoalAnalytics.OverallConclusions(an);
        Assert.Contains(conc, c => c.Category == "煤质表征");
        Assert.NotEmpty(CoalAnalytics.WashingBySeam(an));
        Assert.Equal(GeoDataQueries.GetCoalSamples(db.Connection).Count, an.Count);
    }
}
