// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/ParameterAcceptanceService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class ParameterAcceptanceService : IParameterAcceptanceService
{
    private readonly ISqlService _sql;
    private readonly IRepository<ParameterAcceptance> _repo;

    public ParameterAcceptanceService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<ParameterAcceptance>();
    }

    public ParameterAcceptance? Get(long id) => _repo.GetByKey(id);

    public IReadOnlyList<ParameterAcceptance> ByLocationPhase(string locationCode, long phaseId)
        => _repo.Where("location_code = @c AND phase_id = @p ORDER BY measure_date DESC, id DESC",
            new { c = locationCode, p = phaseId });

    public ParameterAcceptance? LatestForParam(string locationCode, long phaseId, long paramId)
        => _repo.Where(
            "location_code = @c AND phase_id = @p AND param_id = @pid ORDER BY measure_date DESC, id DESC LIMIT 1",
            new { c = locationCode, p = phaseId, pid = paramId }).FirstOrDefault();

    public IReadOnlyDictionary<long, ParameterAcceptance> LatestSnapshotByLocation(string locationCode)
    {
        // 用 SQL 拉每个 param 的最新一条(GROUP BY param_id + 最大 measure_date)
        var sql = @"SELECT a.* FROM parameter_acceptance a
                    INNER JOIN (
                        SELECT param_id, MAX(measure_date) AS md, MAX(id) AS mid
                        FROM parameter_acceptance
                        WHERE location_code = @c
                        GROUP BY param_id, phase_id
                    ) latest ON a.param_id = latest.param_id AND a.measure_date = latest.md AND a.id = latest.mid
                    WHERE a.location_code = @c";
        var rows = _sql.Query<ParameterAcceptance>(sql, new { c = locationCode });
        return rows.ToDictionary(r => r.ParamId);
    }

    public IReadOnlyList<ParameterAcceptance> RecentByStatus(string status, int days = 30)
        => _repo.Where(
            "status = @s AND measure_date >= date('now', @d) ORDER BY measure_date DESC",
            new { s = status, d = $"-{days} days" });

    public IReadOnlyList<ParameterAcceptance> SeriesByParam(string locationCode, long phaseId, long paramId, DateTime startDate, DateTime endDate)
        => _repo.Where(
            "location_code = @c AND phase_id = @p AND param_id = @pid AND measure_date BETWEEN @s AND @e ORDER BY measure_date",
            new { c = locationCode, p = phaseId, pid = paramId, s = BusinessDate.P(startDate), e = BusinessDate.P(endDate) });

    public long Insert(ParameterAcceptance entity) => _repo.Insert(entity);
    public void Update(ParameterAcceptance entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);

    public (double? deviationPct, string status) ComputeStatus(ParameterDefinition def, double? templateValue, double? measuredValue)
    {
        if (!measuredValue.HasValue) return (null, "pending");

        double? deviation = null;
        if (templateValue.HasValue && Math.Abs(templateValue.Value) > 1e-6)
            deviation = (measuredValue.Value - templateValue.Value) / templateValue.Value * 100;

        // 状态判定:优先看报警阈值,其次看标准范围
        var v = measuredValue.Value;
        bool fail =
            (def.AlarmLow.HasValue && v < def.AlarmLow.Value) ||
            (def.AlarmHigh.HasValue && v > def.AlarmHigh.Value);
        if (fail) return (deviation, "fail");

        bool warn = false;
        if (def.StandardMin.HasValue && v < def.StandardMin.Value) warn = true;
        if (def.StandardMax.HasValue && v > def.StandardMax.Value) warn = true;
        if (deviation.HasValue && Math.Abs(deviation.Value) > 15) warn = true;   // 偏差超 15%

        return (deviation, warn ? "warning" : "pass");
    }
}
