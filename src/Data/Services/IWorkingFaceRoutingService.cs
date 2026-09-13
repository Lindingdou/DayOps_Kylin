// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IWorkingFaceRoutingService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 作业面去向档案(V035)持久化服务:working_face 装不下的「当日怎么干」(去向/运距/日目标/混采分项)。
///
/// 容错约定同 <see cref="ISinkProfileService"/>:读吞异常返回空,写不吞异常 ——
/// 保存失败必须能报出原因。
/// </summary>
public interface IWorkingFaceRoutingService
{
    /// <summary>全部作业面去向档案(读失败返回空表)。</summary>
    IReadOnlyList<WorkingFaceRouting> All();

    /// <summary>按作业面编号取档案;无档案或读失败返回 null。</summary>
    WorkingFaceRouting? Get(string faceCode);

    /// <summary>存在即更新,否则插入。失败抛异常(调用方须报给用户)。</summary>
    void Upsert(WorkingFaceRouting entity);

    void Delete(string faceCode);
}
