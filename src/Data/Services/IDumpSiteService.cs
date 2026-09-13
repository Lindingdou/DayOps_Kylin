// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IDumpSiteService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>排土场管理。</summary>
public interface IDumpSiteService
{
    DumpSite? Get(string dumpId);
    IReadOnlyList<DumpSite> All(bool activeOnly = true);
    IReadOnlyList<DumpSite> Internal();   // 内排
    IReadOnlyList<DumpSite> External();   // 外排

    void Upsert(DumpSite entity);
    void Delete(string dumpId);

    /// <summary>累加排土量(自动更新 current_filled,返回新充填率)。</summary>
    double AddFilledVolume(string dumpId, double wanM3);

    /// <summary>
    /// 只改设计容量一列(万 m³)。
    ///
    /// <para><b>为什么不用 <see cref="Upsert"/></b>:整行 upsert 会把该行【所有列】一起重写,
    /// 包括 responsible_dozer_id 这类带外键的列 —— 只要那一行原本就有一个悬空外键,
    /// 改任何一个无关字段都会以 <c>FOREIGN KEY constraint failed</c> 收场。实测踩过。
    /// 改一列就只 UPDATE 一列。</para>
    /// </summary>
    /// <returns>false = 没有这一行(或库不可用)。</returns>
    bool UpdateDesignCapacity(string dumpId, double wanM3);

    /// <summary>只改边坡三项(单层堆高 / 台阶坡角 / 整体坡角)。理由同 <see cref="UpdateDesignCapacity"/>。</summary>
    bool UpdateSlopeParams(string dumpId, double benchHeightM, double benchSlopeDeg, double overallSlopeDeg);
}
