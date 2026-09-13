// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/EquipmentService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;
// GeoDataBase.Equipment 是老目录命名空间,与 Public.Entities.Equipment 类型同名,
// 当前命名空间在 GeoDataBase.* 下时子命名空间隐式可见,会让 alias 失效。
// 解决:全部使用类型完全限定(PitMine3D.Kylin.Data.Entities.Equipment)。
using DbEq = PitMine3D.Kylin.Data.Entities.Equipment;
using DbEm = PitMine3D.Kylin.Data.Entities.EquipmentModel;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class EquipmentService : IEquipmentService
{
    private readonly ISqlService _sql;
    private readonly IRepository<DbEq> _repo;

    public EquipmentService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<DbEq>();
    }

    public DbEq? GetById(string equipmentId) => _repo.GetByKey(equipmentId);
    public IReadOnlyList<DbEq> All() => _repo.All();

    public IReadOnlyList<DbEq> ByCategory(EquipmentCategory category)
        => _repo.Where("category = @cat", new { cat = category.ToString() });

    public IReadOnlyList<DbEq> ByStatus(EquipmentStatus status)
        => _repo.Where("status = @s", new { s = status.ToString() });

    public IReadOnlyList<DbEq> ActiveOnly()
        => _repo.Where("status = @s", new { s = EquipmentStatus.InUse.ToString() });

    public void Insert(DbEq entity) => _repo.Insert(entity);
    public void Update(DbEq entity) => _repo.Update(entity);
    public void Upsert(DbEq entity) => _repo.Upsert(entity);
    public void Delete(string equipmentId) => _repo.Delete(equipmentId);

    public void SetStatus(string equipmentId, EquipmentStatus status, string? reason = null)
    {
        var e = _repo.GetByKey(equipmentId);
        if (e == null) return;
        e.Status = status.ToString();
        if (!string.IsNullOrEmpty(reason))
            e.Notes = string.IsNullOrEmpty(e.Notes) ? reason : $"{e.Notes};{reason}";
        _repo.Update(e);
    }

    public double CalculateCumulativeHours(string equipmentId)
    {
        var baseHours = _sql.ExecuteScalar<double?>(
            "SELECT cumulative_hours FROM equipment WHERE equipment_id = @id",
            new { id = equipmentId }) ?? 0;
        var addedHours = _sql.ExecuteScalar<double?>(
            "SELECT COALESCE(SUM(work_hours), 0) FROM production_record WHERE equipment_id = @id",
            new { id = equipmentId }) ?? 0;
        return baseHours + addedHours;
    }

    public string? GetCurrentArea(string equipmentId)
        => _sql.ExecuteScalar<string?>(
            "SELECT operating_area FROM equipment WHERE equipment_id = @id",
            new { id = equipmentId });
}

internal sealed class EquipmentModelService : IEquipmentModelService
{
    private readonly ISqlService _sql;
    private readonly IRepository<DbEm> _repo;

    public EquipmentModelService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<DbEm>();
    }

    public DbEm? Get(string model) => _repo.GetByKey(model);
    public IReadOnlyList<DbEm> All() => _repo.All();
    public IReadOnlyList<DbEm> ByCategory(EquipmentCategory category)
        => _repo.Where("category = @c", new { c = category.ToString() });
    public void Upsert(DbEm entity) => _repo.Upsert(entity);
    public void Delete(string model) => _repo.Delete(model);
}
