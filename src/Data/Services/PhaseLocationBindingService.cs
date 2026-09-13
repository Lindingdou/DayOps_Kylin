// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/PhaseLocationBindingService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class PhaseLocationBindingService : IPhaseLocationBindingService
{
    private readonly ISqlService _sql;
    private readonly IRepository<PhaseLocationBinding> _repo;

    public PhaseLocationBindingService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<PhaseLocationBinding>();
    }

    public PhaseLocationBinding? Get(long id) => _repo.GetByKey(id);

    public IReadOnlyList<PhaseLocationBinding> ActiveByLocation(string locationCode)
        => _repo.Where("location_code = @c AND is_active = 1 AND (ended_at IS NULL OR ended_at >= date('now')) ORDER BY phase_id",
            new { c = locationCode });

    public IReadOnlyList<PhaseLocationBinding> ActiveByPhase(long phaseId)
        => _repo.Where("phase_id = @p AND is_active = 1 AND (ended_at IS NULL OR ended_at >= date('now'))",
            new { p = phaseId });

    public PhaseLocationBinding? ActiveBy(string locationCode, long phaseId)
        => _repo.Where("location_code = @c AND phase_id = @p AND is_active = 1 AND (ended_at IS NULL OR ended_at >= date('now'))",
            new { c = locationCode, p = phaseId }).FirstOrDefault();

    public IReadOnlyList<PhaseLocationBinding> All(bool activeOnly = true)
        => activeOnly
            ? _repo.Where("is_active = 1")
            : _repo.All();

    public long Insert(PhaseLocationBinding entity) => _repo.Insert(entity);
    public void Update(PhaseLocationBinding entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);

    public void RebindTemplate(string locationCode, long phaseId, long? newTemplateId)
    {
        using var tx = _sql.BeginTransaction();
        // 1) 关闭旧绑定
        var olds = _repo.Where("location_code = @c AND phase_id = @p AND is_active = 1",
            new { c = locationCode, p = phaseId });
        foreach (var old in olds)
        {
            old.IsActive = false;
            old.EndedAt = DateTime.Today;
            _repo.Update(old);
        }
        // 2) 新增
        _repo.Insert(new PhaseLocationBinding
        {
            PhaseId = phaseId,
            LocationCode = locationCode,
            BoundTemplateId = newTemplateId,
            StartedAt = DateTime.Today,
            IsActive = true
        });
        tx.Commit();
    }
}
