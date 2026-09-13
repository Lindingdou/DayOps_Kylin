// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IProcessZoneService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 工序作业区(V047)服务：某一期、某一道工序在哪儿干。
///
/// <para><b>与 <see cref="IMineableRegionService"/> 的分工</b>：那张表是长期存在的地
/// （采场/排土场/工作帮，推演与路网裁剪读它）；本表是按期次的工序作业位置，
/// 下游是任务编制 / 派工 / 清场，<b>不参与推演的推进极性</b>。两张表的行不许互相顶替。</para>
///
/// <para><b>upsert 键是三者</b>：期次 + 工序 + 名字。少了工序，同一个作业面的
/// 穿孔区与采装区会互相覆盖 —— 而 upsert 不报错。</para>
/// </summary>
public interface IProcessZoneService
{
    /// <summary>全部（按 期次 / 工序 / 名字 排）。</summary>
    IReadOnlyList<ProcessZone> All();

    /// <summary>一期的全部工序区。</summary>
    IReadOnlyList<ProcessZone> ByPeriod(string period);

    /// <summary>一期里某一道工序的区域。</summary>
    IReadOnlyList<ProcessZone> ByProcess(string period, string process);

    ProcessZone? Get(long id);

    /// <summary>按 (期次, 工序, 名字) 取一条；没有返回 null。</summary>
    ProcessZone? Find(string period, string process, string name);

    long Insert(ProcessZone entity);

    void Update(ProcessZone entity);

    /// <summary>
    /// 按 (期次, 工序, 名字) upsert：有就换几何、没有就新增，返回主键。
    /// <para><b>只换几何与记账列</b>，<see cref="ProcessZone.Active"/> /
    /// <see cref="ProcessZone.Visible"/> / id 一律保住 —— 换几何时顺手换掉这些，
    /// 等于把这块区域从别处的引用里悄悄摘走。</para>
    /// </summary>
    long Upsert(ProcessZone entity);

    void Delete(long id);

    /// <summary>删掉一整期（重新生成一期时先清后写）。返回删掉几条。</summary>
    int DeletePeriod(string period);
}
