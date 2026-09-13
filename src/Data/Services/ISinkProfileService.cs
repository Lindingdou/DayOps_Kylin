// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ISinkProfileService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 去向扩展档案(V035)+ 库容盘点流水 的持久化服务。
///
/// 容错约定(与本模块其它服务不同,这里是刻意的):
///  · **读**(<see cref="All"/> / <see cref="Get"/> / <see cref="StocktakesOf"/>)吞异常返回空 ——
///    扩展档案是「锦上添花」的补充信息,老库没跑过 V035 时台账仍须能打开;
///  · **写**(<see cref="Upsert"/> / <see cref="Delete"/> / <see cref="AddStocktake"/>)不吞异常 ——
///    保存失败必须让调用方拿到原因去告诉用户,静默成功比报错危险得多。
/// </summary>
public interface ISinkProfileService
{
    /// <summary>全部去向档案(读失败返回空表)。</summary>
    IReadOnlyList<SinkProfile> All();

    /// <summary>按去向编号取档案;无档案或读失败返回 null。</summary>
    SinkProfile? Get(string sinkId);

    /// <summary>存在即更新,否则插入。失败抛异常(调用方须报给用户)。</summary>
    void Upsert(SinkProfile entity);

    /// <summary>删除档案(去向本体的删除由 dump_site / load_unload_point 各自负责)。</summary>
    void Delete(string sinkId);

    /// <summary>追加一条库容盘点流水,返回新主键。流水只增不改。</summary>
    long AddStocktake(SinkStocktake entity);

    /// <summary>某去向的盘点流水(按时间倒序,读失败返回空表)。</summary>
    IReadOnlyList<SinkStocktake> StocktakesOf(string sinkId, int limit = 50);
}
