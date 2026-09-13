// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/CoalQualityService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;
using CoalSample = PitMine3D.Kylin.Data.Entities.CoalSample;   // 与外层 Data.CoalAnalytics 的同名 record 消歧（须放在命名空间内才盖得过外层）

/// <summary>
/// 煤芯煤样化验 + 衍生统计。本 Service 是煤质模块最核心的实现。
/// 注意:
///   - GB/T 5751 反推等"纯逻辑"委托给 ICoalReferenceService
///   - 重算 summary 是写库操作，请在调用前确保已加载基础数据
/// </summary>
internal sealed class CoalQualityService : ICoalQualityService
{
    private readonly ISqlService _sql;
    private readonly ICoalReferenceService _ref;
    private readonly IRepository<CoalSample> _sampleRepo;
    private readonly IRepository<CoalSampleSummary> _summaryRepo;

    public CoalQualityService(ISqlService sql, ICoalReferenceService refSvc)
    {
        _sql = sql;
        _ref = refSvc;
        _sampleRepo = sql.Repository<CoalSample>();
        _summaryRepo = sql.Repository<CoalSampleSummary>();
    }

    // ─── coal_sample CRUD ───
    public CoalSample? GetSample(long id) => _sampleRepo.GetByKey(id);

    public IReadOnlyList<CoalSample> SamplesByBorehole(long boreholeId)
        => _sampleRepo.Where("borehole_id = @id ORDER BY depth_from", new { id = boreholeId });

    public IReadOnlyList<CoalSample> SamplesBySeam(string seamCode)
        => _sampleRepo.Where("seam_code = @c ORDER BY borehole_id, depth_from", new { c = seamCode });

    public IReadOnlyList<CoalSample> SamplesByCoalType(string coalType)
        => _sampleRepo.Where("coal_type = @t", new { t = coalType });

    public IReadOnlyList<CoalSample> All() => _sampleRepo.All();

    public long InsertSample(CoalSample entity) => _sampleRepo.Insert(entity);
    public void UpdateSample(CoalSample entity) => _sampleRepo.Update(entity);
    public void DeleteSample(long id) => _sampleRepo.Delete(id);

    public int BulkInsertSamples(IEnumerable<CoalSample> rows)
    {
        var list = rows as IList<CoalSample> ?? rows.ToList();
        _sql.BulkInsert("coal_sample", list);
        return list.Count;
    }

    // ─── 3D 视图 ───
    public IReadOnlyList<CoalSample3DRow> Get3DPoints(string? seamCode = null)
    {
        var where = seamCode is null ? "z_sample IS NOT NULL" : "seam_code = @c AND z_sample IS NOT NULL";
        var sql = $@"
            SELECT id                AS Id,
                   borehole_id       AS BoreholeId,
                   hole_id           AS HoleId,
                   seam_code         AS SeamCode,
                   x                 AS X,
                   y                 AS Y,
                   z_sample          AS ZSample,
                   depth_from        AS DepthFrom,
                   depth_to          AS DepthTo,
                   sample_thickness  AS SampleThickness,
                   ad_raw            AS AdRaw,
                   ad_clean          AS AdClean,
                   vdaf_raw          AS VdafRaw,
                   vdaf_clean        AS VdafClean,
                   std_raw           AS StdRaw,
                   std_clean         AS StdClean,
                   qgr_d             AS QgrD,
                   qnet_ad           AS QnetAd,
                   caking_g          AS CakingG,
                   plastic_y_mm      AS PlasticYMm,
                   coal_type         AS CoalType,
                   coal_type_name    AS CoalTypeName
              FROM v_coal_sample_3d
             WHERE {where}";
        return _sql.Query<CoalSample3DRow>(sql, seamCode is null ? null : new { c = seamCode }).ToList();
    }

    // ─── 衍生统计 ───
    public IReadOnlyList<QualityStats> StatsBySeam(string? indicator = null)
    {
        // 当前默认按 4 个核心指标分别算
        var indicators = indicator is null
            ? new[] { "ad_raw", "vdaf_raw", "std_raw", "qnet_ad" }
            : new[] { indicator };

        var results = new List<QualityStats>();
        foreach (var ind in indicators)
        {
            // SQLite 自带统计有限，用 Query 收集后程序里算 percentile
            var sql = $@"SELECT seam_code, {ind} AS v FROM coal_sample WHERE {ind} IS NOT NULL";
            var rows = _sql.Query<(string SeamCode, double V)>(sql).ToList();
            foreach (var grp in rows.GroupBy(r => r.SeamCode))
            {
                var values = grp.Select(g => g.V).OrderBy(v => v).ToList();
                if (values.Count == 0) continue;
                double mean = values.Average();
                double std = values.Count > 1
                    ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1))
                    : 0;
                results.Add(new QualityStats(
                    SeamCode: grp.Key,
                    Indicator: ind,
                    Count: values.Count,
                    Mean: mean,
                    Std: std,
                    Min: values[0],
                    Max: values[^1],
                    P25: Percentile(values, 0.25),
                    P50: Percentile(values, 0.50),
                    P75: Percentile(values, 0.75)));
            }
        }
        return results.OrderBy(r => r.Indicator).ThenBy(r => r.SeamCode).ToList();
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double pos = (sorted.Count - 1) * p;
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        double frac = pos - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }

    // ─── 重算 summary ───
    public int RebuildSummary()
    {
        // 一次性清表 + 重灌（数据量小，~88 行）
        _sql.Execute("DELETE FROM coal_sample_summary");

        const string sql = @"
            SELECT borehole_id, seam_code,
                   sample_thickness,
                   mad_raw, mad_clean, ad_raw, ad_clean,
                   vdaf_raw, vdaf_clean, fcd_raw, fcd_clean,
                   std_raw, std_clean, qgr_d, qnet_ad,
                   caking_g, plastic_y_mm, clean_coal_yield, coal_type
              FROM coal_sample";

        var rows = _sql.Query<SampleAggRow>(sql).ToList();
        var groups = rows.GroupBy(r => (r.borehole_id, r.seam_code));

        var summaries = new List<CoalSampleSummary>();
        foreach (var g in groups)
        {
            var items = g.ToList();
            string? dom = items
                .Where(i => !string.IsNullOrEmpty(i.coal_type))
                .GroupBy(i => i.coal_type!)
                .OrderByDescending(grp => grp.Count())
                .FirstOrDefault()?.Key;

            summaries.Add(new CoalSampleSummary
            {
                BoreholeId = g.Key.borehole_id,
                SeamCode = g.Key.seam_code,
                SampleCount = items.Count,
                AvgThickness = Avg(items.Select(i => i.sample_thickness)),
                AvgMadRaw = Avg(items.Select(i => i.mad_raw)),
                AvgMadClean = Avg(items.Select(i => i.mad_clean)),
                AvgAdRaw = Avg(items.Select(i => i.ad_raw)),
                AvgAdClean = Avg(items.Select(i => i.ad_clean)),
                AvgVdafRaw = Avg(items.Select(i => i.vdaf_raw)),
                AvgVdafClean = Avg(items.Select(i => i.vdaf_clean)),
                AvgFcdRaw = Avg(items.Select(i => i.fcd_raw)),
                AvgFcdClean = Avg(items.Select(i => i.fcd_clean)),
                AvgStdRaw = Avg(items.Select(i => i.std_raw)),
                AvgStdClean = Avg(items.Select(i => i.std_clean)),
                AvgQgrD = Avg(items.Select(i => i.qgr_d)),
                AvgQnetAd = Avg(items.Select(i => i.qnet_ad)),
                AvgCakingG = Avg(items.Select(i => i.caking_g)),
                AvgPlasticY = Avg(items.Select(i => i.plastic_y_mm)),
                AvgCleanYield = Avg(items.Select(i => i.clean_coal_yield)),
                DominantCoalType = dom,
                IsFromSource = 0,
                LastBuiltAt = DateTime.Now,
            });
        }

        _sql.BulkInsert("coal_sample_summary", summaries);
        return summaries.Count;
    }

    private static double? Avg(IEnumerable<double?> source)
    {
        var arr = source.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return arr.Count == 0 ? null : arr.Average();
    }

    public IReadOnlyList<CoalSampleSummary> AllSummary()
        => _summaryRepo.Where("1=1 ORDER BY seam_code, borehole_id");

    // ─── 衍生计算 ───
    public string? ClassifyByGB5751(double? vdaf, double? gIndex, double? plasticY)
        => _ref.ResolveCoalType(vdaf, gIndex, plasticY);

    public string GradeAsh(double? ad) => GradeOf("ash", ad);
    public string GradeSulfur(double? std) => GradeOf("sulfur", std);
    public string GradeQnet(double? qnet) => GradeOf("qnet", qnet);

    private string GradeOf(string ruleType, double? value)
    {
        if (value is null) return "—";
        var lvl = _ref.FindLevel(ruleType, value.Value);
        return lvl?.LevelName ?? "—";
    }

    // 局部 record 用于 SqlLib Query 反序列化
    private record SampleAggRow(
        long borehole_id, string seam_code,
        double? sample_thickness,
        double? mad_raw, double? mad_clean, double? ad_raw, double? ad_clean,
        double? vdaf_raw, double? vdaf_clean, double? fcd_raw, double? fcd_clean,
        double? std_raw, double? std_clean, double? qgr_d, double? qnet_ad,
        double? caking_g, double? plastic_y_mm, double? clean_coal_yield, string? coal_type);
}
