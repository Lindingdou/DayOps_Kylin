// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IWorkingFaceService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>工作面 / 台阶几何参数管理。</summary>
public interface IWorkingFaceService
{
    WorkingFace? Get(long id);
    WorkingFace? GetByCode(string faceCode);

    /// <summary>当前活跃工作面(status=active)。</summary>
    IReadOnlyList<WorkingFace> ActiveAll();

    /// <summary>某平盘上的全部工作面。</summary>
    IReadOnlyList<WorkingFace> ByLocation(string locationCode);

    /// <summary>某电铲负责的工作面。</summary>
    IReadOnlyList<WorkingFace> ByEquipment(string equipmentId);

    IReadOnlyList<WorkingFace> All();

    long Insert(WorkingFace entity);
    void Update(WorkingFace entity);
    void Delete(long id);

    /// <summary>关闭工作面(标记为 closed,不物理删除)。</summary>
    void Close(long id, string reason);
}
