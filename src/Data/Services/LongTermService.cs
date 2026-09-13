// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/LongTermService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class LongTermService : ILongTermService
{
    private readonly ISqlService _sql;
    private readonly IRepository<LongTermMetric> _repo;

    public LongTermService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<LongTermMetric>();
    }

    public IReadOnlyList<LongTermMetric> Query(MetricSource source, string item, int? year = null)
    {
        var where = "source = @s AND item = @i";
        if (year.HasValue) where += " AND year = @y";
        return _repo.Where(where + " ORDER BY year, month",
            new { s = source.ToString().ToLowerInvariant(), i = item, y = year ?? 0 });
    }

    public IReadOnlyList<LongTermMetric> AnnualSeries(MetricSource source, string item)
        => _repo.Where("source = @s AND item = @i AND month IS NULL ORDER BY year",
            new { s = source.ToString().ToLowerInvariant(), i = item });

    public IReadOnlyList<LongTermMetric> AllBySource(MetricSource source)
        => _repo.Where("source = @s ORDER BY year, month",
            new { s = source.ToString().ToLowerInvariant() });

    public IReadOnlyList<string> ListItems(MetricSource source)
        => new List<string>(_sql.Query<string>(
            "SELECT DISTINCT item FROM long_term_metric WHERE source = @s ORDER BY item",
            new { s = source.ToString().ToLowerInvariant() }));

    public void Insert(LongTermMetric entity) => _repo.Insert(entity);
}
