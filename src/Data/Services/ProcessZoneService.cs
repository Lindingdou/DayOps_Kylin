// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/ProcessZoneService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary><see cref="IProcessZoneService"/> 实现：通用仓储 CRUD + 按三键 upsert。</summary>
internal sealed class ProcessZoneService : IProcessZoneService
{
    private readonly IRepository<ProcessZone> _repo;

    public ProcessZoneService(ISqlService sql) => _repo = sql.Repository<ProcessZone>();

    public IReadOnlyList<ProcessZone> All()
        => _repo.Where("1=1 ORDER BY period DESC, process, name");

    public IReadOnlyList<ProcessZone> ByPeriod(string period)
        => string.IsNullOrWhiteSpace(period)
            ? Array.Empty<ProcessZone>()
            : _repo.Where("1=1 ORDER BY process, name").Where(r => Eq(r.Period, period)).ToList();

    public IReadOnlyList<ProcessZone> ByProcess(string period, string process)
        => ByPeriod(period).Where(r => Eq(r.Process, process)).ToList();

    public ProcessZone? Get(long id) => _repo.GetByKey(id);

    public ProcessZone? Find(string period, string process, string name)
        => _repo.Where("1=1").FirstOrDefault(
               r => Eq(r.Period, period) && Eq(r.Process, process) && Eq(r.Name, name));

    public long Insert(ProcessZone entity) => _repo.Insert(entity);

    public void Update(ProcessZone entity) => _repo.Update(entity);

    /// <summary>
    /// 三键 upsert。<b>id / active / visible 从既有行沿用</b> ——
    /// 换几何时顺手把"这块算不算数"改回默认，等于悄悄改了本期作业范围。
    /// </summary>
    public long Upsert(ProcessZone entity)
    {
        if (entity == null) return 0;
        var old = Find(entity.Period, entity.Process, entity.Name);
        if (old == null) return _repo.Insert(entity);

        entity.Id = old.Id;
        entity.Active = old.Active;
        entity.Visible = old.Visible;
        _repo.Update(entity);
        return old.Id;
    }

    public void Delete(long id) => _repo.Delete(id);

    public int DeletePeriod(string period)
    {
        if (string.IsNullOrWhiteSpace(period)) return 0;
        int n = 0;
        foreach (var r in ByPeriod(period)) { _repo.Delete(r.Id); n++; }
        return n;
    }

    private static bool Eq(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
