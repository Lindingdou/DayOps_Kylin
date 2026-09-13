// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ILoadUnloadPointService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 装卸点(运输源/汇)持久化服务:CRUD + 整组替换。
///
/// ⚠ <see cref="ReplaceAll"/> 原是给 RoadLib「装卸点设置」一次提交全表用的，
/// 而那个窗口 2026-08-10 已窄化成「破碎站位置设置」——现在<b>没有任何窗口拥有全表</b>：
/// 破碎站归 RoadLib，排土场/储矿场归 TaskLib「去向台账」。
/// 谁再用 ReplaceAll 提交自己那一部分，就会把别人的记录整片抹掉，
/// 还会重排主键、打断去向台账的 <c>LUP-{id}</c> 引用。
/// 只管一个子集时请照 <c>RoadLib.Transport.CrusherStore.SaveScoped</c> 的做法走增量 CRUD。
/// </summary>
public interface ILoadUnloadPointService
{
    /// <summary>全部装卸点(按 id 升序)。</summary>
    IReadOnlyList<LoadUnloadPoint> All();

    LoadUnloadPoint? Get(long id);

    /// <summary>插入,返回新主键。</summary>
    long Insert(LoadUnloadPoint entity);

    void Update(LoadUnloadPoint entity);

    void Delete(long id);

    /// <summary>整组替换:清空现有 + 批量插入(窗口「确定」一次性提交全表)。</summary>
    void ReplaceAll(IEnumerable<LoadUnloadPoint> entities);
}
