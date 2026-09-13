// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IPhaseLocationBindingService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>工艺环节 ↔ 平盘 ↔ 模板 三方绑定服务。</summary>
public interface IPhaseLocationBindingService
{
    PhaseLocationBinding? Get(long id);

    /// <summary>某平盘当前激活的全部绑定(用于平盘工艺地图主页)。</summary>
    IReadOnlyList<PhaseLocationBinding> ActiveByLocation(string locationCode);

    /// <summary>某环节当前激活的全部绑定。</summary>
    IReadOnlyList<PhaseLocationBinding> ActiveByPhase(long phaseId);

    /// <summary>某平盘+环节当前激活的绑定(应只有 1 条)。</summary>
    PhaseLocationBinding? ActiveBy(string locationCode, long phaseId);

    /// <summary>所有平盘的所有绑定(管理用)。</summary>
    IReadOnlyList<PhaseLocationBinding> All(bool activeOnly = true);

    long Insert(PhaseLocationBinding entity);
    void Update(PhaseLocationBinding entity);
    void Delete(long id);

    /// <summary>切换某平盘+环节使用的模板(自动 end 旧绑定 + new 新绑定)。</summary>
    void RebindTemplate(string locationCode, long phaseId, long? newTemplateId);
}
