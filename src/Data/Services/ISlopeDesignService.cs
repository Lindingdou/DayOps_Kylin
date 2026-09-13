// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ISlopeDesignService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>边坡设计管理。</summary>
public interface ISlopeDesignService
{
    SlopeDesign? Get(long id);
    IReadOnlyList<SlopeDesign> All();

    /// <summary>当前现行版本(EffectiveTo IS NULL 或 > today)。</summary>
    IReadOnlyList<SlopeDesign> CurrentDesigns();

    IReadOnlyList<SlopeDesign> BySide(string sideName);

    long Insert(SlopeDesign entity);
    void Update(SlopeDesign entity);
    void Delete(long id);
}
