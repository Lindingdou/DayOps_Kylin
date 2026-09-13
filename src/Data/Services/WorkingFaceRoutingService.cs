// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/WorkingFaceRoutingService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// <see cref="IWorkingFaceRoutingService"/> 实现:通用仓储 CRUD。
/// 读路径吞异常(老库没跑过 V035 时作业面台账仍要能打开),写路径不吞(保存失败必须报出去)。
/// </summary>
internal sealed class WorkingFaceRoutingService : IWorkingFaceRoutingService
{
    private readonly IRepository<WorkingFaceRouting> _repo;

    public WorkingFaceRoutingService(ISqlService sql)
    {
        _repo = sql.Repository<WorkingFaceRouting>();
    }

    public IReadOnlyList<WorkingFaceRouting> All()
    {
        try { return _repo.All(); }
        catch { return Array.Empty<WorkingFaceRouting>(); }
    }

    public WorkingFaceRouting? Get(string faceCode)
    {
        if (string.IsNullOrWhiteSpace(faceCode)) return null;
        try { return _repo.GetByKey(faceCode); }
        catch { return null; }
    }

    public void Upsert(WorkingFaceRouting entity)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.FaceCode))
            throw new ArgumentException("作业面去向档案缺少 face_code,无法保存", nameof(entity));
        _repo.Upsert(entity);
    }

    public void Delete(string faceCode)
    {
        if (string.IsNullOrWhiteSpace(faceCode)) return;
        _repo.Delete(faceCode);
    }
}
