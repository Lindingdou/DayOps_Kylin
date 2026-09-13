// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ISupplementaryBoreholeService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 补勘钻孔写实:补勘孔 + 逐煤层顶/底板高程的 CRUD。
/// 数据独立于原始 borehole / borehole_seam_result,供「补勘钻孔写实」录入与自动重建煤层三维面。
/// </summary>
public interface ISupplementaryBoreholeService
{
    // ─── 批次(系统唯一时间标签) ───
    /// <summary>全部批次(按标签倒序,含 HoleCount)。</summary>
    IReadOnlyList<SupplementaryBatch> AllBatches();
    SupplementaryBatch? GetBatch(long id);
    /// <summary>新建批次,生成系统唯一时间标签,返回 batchId。</summary>
    long CreateBatch(string source, string? name = null);
    void RenameBatch(long id, string name);
    /// <summary>删除批次:连带删除该批全部孔+层位,返回删除的孔数。</summary>
    int DeleteBatch(long id);

    // ─── 补勘孔 ───
    /// <summary>列出补勘孔(batchId=null 则全部;否则仅该批次)。</summary>
    IReadOnlyList<SupplementaryBorehole> AllHoles(long? batchId = null);
    SupplementaryBorehole? GetHole(long id);
    SupplementaryBorehole? GetHoleByHoleId(string holeId);
    long InsertHole(SupplementaryBorehole hole);
    void UpdateHole(SupplementaryBorehole hole);
    /// <summary>删除补勘孔(连带删除该孔全部层位)。</summary>
    void DeleteHole(long id);

    // ─── 煤层顶/底板层位 ───
    IReadOnlyList<SupplementarySeamHorizon> HorizonsByHole(long supBoreholeId);

    /// <summary>整体替换某孔的层位(先删旧层再批量插新层),保证一孔层位一致。</summary>
    void ReplaceHorizons(long supBoreholeId, IEnumerable<SupplementarySeamHorizon> horizons);

    /// <summary>全部层位(用于自动重建时按煤层聚合)。</summary>
    IReadOnlyList<SupplementarySeamHorizon> AllHorizons();
}
