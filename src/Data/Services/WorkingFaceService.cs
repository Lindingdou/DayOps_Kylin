// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/WorkingFaceService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class WorkingFaceService : IWorkingFaceService
{
    private readonly ISqlService _sql;
    private readonly IRepository<WorkingFace> _repo;

    public WorkingFaceService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<WorkingFace>();
    }

    public WorkingFace? Get(long id) => _repo.GetByKey(id);

    public WorkingFace? GetByCode(string faceCode)
        => _sql.QueryFirstOrDefault<WorkingFace>(
            "SELECT * FROM working_face WHERE face_code = @c", new { c = faceCode });

    public IReadOnlyList<WorkingFace> ActiveAll()
        => _repo.Where("status = 'active' ORDER BY location_code, face_code");

    public IReadOnlyList<WorkingFace> ByLocation(string locationCode)
        => _repo.Where("location_code = @c", new { c = locationCode });

    public IReadOnlyList<WorkingFace> ByEquipment(string equipmentId)
        => _repo.Where("equipment_id = @id", new { id = equipmentId });

    public IReadOnlyList<WorkingFace> All() => _repo.All();

    public long Insert(WorkingFace entity) => _repo.Insert(entity);
    public void Update(WorkingFace entity) => _repo.Update(entity);
    public void Delete(long id) => _repo.Delete(id);

    public void Close(long id, string reason)
    {
        var e = _repo.GetByKey(id);
        if (e == null) return;
        e.Status = "closed";
        e.EffectiveTo = System.DateTime.Today;
        if (!string.IsNullOrEmpty(reason))
            e.Notes = string.IsNullOrEmpty(e.Notes) ? reason : $"{e.Notes}; 关闭:{reason}";
        _repo.Update(e);
    }
}
