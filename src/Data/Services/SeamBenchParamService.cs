// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/SeamBenchParamService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// <see cref="ISeamBenchParamService"/> 实现。
///
/// 全矿默认值从 <c>parameter_definition</c> 现取（V034 的 coal_* 三项），不硬编码 ——
/// 那三项的区间和取值依据（初设5-4）是校准过的，抄一份到代码里迟早两边不一致。
/// 只有连 parameter_definition 都读不到时才用兜底常数，且会在 Sources 里标出来。
/// </summary>
internal sealed class SeamBenchParamService : ISeamBenchParamService
{
    // parameter_definition 里煤台阶三项的 code（V034）
    private const string CodeBenchH = "coal_bench_height";
    private const string CodeSlopeA = "coal_bench_slope_angle";
    private const string CodeBermW  = "coal_platform_width";

    // 兜底：仅当 parameter_definition 也读不到时用（与 V034 的 standard_default 对齐）
    private const double FbBenchH = 15.0, FbSlopeA = 65.0, FbBermW = 6.0;
    private const double FbStripW = 20.0, FbMinThick = 0.8;

    private readonly IRepository<SeamBenchParam> _repo;
    private readonly IRepository<CoalSeamDef> _seams;
    private readonly IRepository<ParameterDefinition> _params;

    public SeamBenchParamService(ISqlService sql)
    {
        _repo   = sql.Repository<SeamBenchParam>();
        _seams  = sql.Repository<CoalSeamDef>();
        _params = sql.Repository<ParameterDefinition>();
    }

    public IReadOnlyList<SeamBenchParam> All(bool activeOnly = true)
    {
        var rows = activeOnly ? _repo.Where("is_active = 1") : _repo.Where("1=1");
        // 按煤层字典的 sort_order 排（浅 → 深），字典里没有的排最后
        var order = SeamOrder();
        return rows.OrderBy(r => order.TryGetValue(r.SeamCode, out int o) ? o : int.MaxValue)
                   .ThenBy(r => r.SeamCode, StringComparer.Ordinal)
                   .ToList();
    }

    public SeamBenchParam? Get(string seamCode, string? designVersion = null)
        => _repo.Where("seam_code = @c AND IFNULL(design_version,'') = @v",
                       new { c = seamCode ?? "", v = designVersion ?? "" })
                .FirstOrDefault();

    public long Upsert(SeamBenchParam entity)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.SeamCode)) return 0;
        var existing = Get(entity.SeamCode, entity.DesignVersion);
        if (existing == null) { _repo.Insert(entity); return entity.Id; }

        entity.Id = existing.Id;
        _repo.Update(entity);
        return entity.Id;
    }

    public void Delete(long id) => _repo.Delete(id);

    public int EnsureRowsForAllSeams()
    {
        List<CoalSeamDef> defs;
        try { defs = _seams.Where("1=1").ToList(); } catch { return 0; }

        var have = new HashSet<string>(
            _repo.Where("IFNULL(design_version,'') = ''").Select(r => r.SeamCode), StringComparer.Ordinal);

        int n = 0;
        foreach (var d in defs)
        {
            if (string.IsNullOrWhiteSpace(d.Code) || have.Contains(d.Code)) continue;
            _repo.Insert(new SeamBenchParam
            {
                SeamCode = d.Code,
                StripWidthM = FbStripW,
                LayeringMode = "inclined",
                MinMineableThickM = FbMinThick,
                Datum = "floor",
                IsActive = true,
                Notes = "自动建行：参数回落全矿默认，按需逐项覆盖",
            });
            n++;
        }
        return n;
    }

    public EffectiveSeamBench Effective(string seamCode, string? designVersion = null)
    {
        var row = Get(seamCode, designVersion);
        var eff = new EffectiveSeamBench { SeamCode = seamCode ?? "" };

        double Pick(double? over, string code, double fallback, string field)
        {
            if (over is double v) { eff.Sources[field] = "覆盖"; return v; }
            double? std = StandardDefault(code);
            if (std is double s) { eff.Sources[field] = "全矿默认"; return s; }
            eff.Sources[field] = "兜底";
            return fallback;
        }

        eff.BenchHeightM       = Pick(row?.BenchHeightM,       CodeBenchH, FbBenchH, nameof(eff.BenchHeightM));
        eff.BenchSlopeAngleDeg = Pick(row?.BenchSlopeAngleDeg, CodeSlopeA, FbSlopeA, nameof(eff.BenchSlopeAngleDeg));
        eff.BermWidthM         = Pick(row?.BermWidthM,         CodeBermW,  FbBermW,  nameof(eff.BermWidthM));

        // 这两项 parameter_definition 里没有对口指标，只有「覆盖 / 兜底」两级
        eff.StripWidthM = row?.StripWidthM ?? FbStripW;
        eff.Sources[nameof(eff.StripWidthM)] = row?.StripWidthM.HasValue == true ? "覆盖" : "兜底";
        eff.MinMineableThickM = row?.MinMineableThickM ?? FbMinThick;
        eff.Sources[nameof(eff.MinMineableThickM)] = row?.MinMineableThickM.HasValue == true ? "覆盖" : "兜底";

        eff.Inclined   = row?.IsInclined   ?? true;
        eff.FloorDatum = row?.IsFloorDatum ?? true;
        return eff;
    }

    /// <summary>读 parameter_definition 的 standard_default；缺行 / 读失败返回 null。</summary>
    private double? StandardDefault(string code)
    {
        try
        {
            return _params.Where("code = @c AND is_active = 1", new { c = code })
                          .FirstOrDefault()?.StandardDefault;
        }
        catch { return null; }
    }

    private Dictionary<string, int> SeamOrder()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var s in _seams.Where("1=1"))
                if (!string.IsNullOrWhiteSpace(s.Code)) d[s.Code] = s.SortOrder;
        }
        catch { }
        return d;
    }
}
