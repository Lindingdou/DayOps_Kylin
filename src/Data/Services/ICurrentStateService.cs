// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ICurrentStateService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 现状写实:现状高程点(现状地表/台阶),按「批次」(系统唯一时间标签)组织。
/// 「补勘钻孔写实」的姊妹功能,数据独立;供建现状三维面、更新地质模型。
/// </summary>
public interface ICurrentStateService
{
    // ─── 批次(系统唯一时间标签) ───
    IReadOnlyList<CurrentStateBatch> AllBatches();
    CurrentStateBatch? GetBatch(long id);
    /// <summary>新建批次,生成系统唯一时间标签,返回 batchId。</summary>
    long CreateBatch(string source, string? name = null);
    void RenameBatch(long id, string name);
    /// <summary>删除批次:连带删除该批全部现状点,返回删除的点数。</summary>
    int DeleteBatch(long id);

    // ─── 现状点 ───
    IReadOnlyList<CurrentStatePoint> PointsByBatch(long batchId);
    /// <summary>全部现状点(跨批次;供按煤层+顶/底筛选作观测数据)。</summary>
    IReadOnlyList<CurrentStatePoint> AllPoints();
    /// <summary>整体替换某批次的点(先删旧点再批量插)。</summary>
    void ReplacePoints(long batchId, IEnumerable<CurrentStatePoint> points);
}
