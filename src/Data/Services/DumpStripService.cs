// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/DumpStripService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary><see cref="IDumpStripService"/> 实现：通用仓储 CRUD + 按区整体替换。</summary>
internal sealed class DumpStripService : IDumpStripService
{
    private readonly IRepository<DumpStrip> _repo;

    public DumpStripService(ISqlService sql)
    {
        _repo = sql.Repository<DumpStrip>();
    }

    public IReadOnlyList<DumpStrip> All()
        => _repo.Where("1=1 ORDER BY region_id, level_index, panel_index, step_index, sub_index");

    public IReadOnlyList<DumpStrip> ByRegion(long regionId, string? designVersion = null)
        => string.IsNullOrWhiteSpace(designVersion)
            ? _repo.Where($"region_id = {regionId} ORDER BY level_index, panel_index, step_index, sub_index")
            : _repo.Where($"region_id = {regionId} AND IFNULL(design_version,'') = '{Esc(designVersion!)}' "
                        + "ORDER BY level_index, panel_index, step_index, sub_index");

    public DumpStrip? Get(long id) => _repo.GetByKey(id);

    public long Insert(DumpStrip entity) => _repo.Insert(entity);

    public void Update(DumpStrip entity) => _repo.Update(entity);

    public void Delete(long id) => _repo.Delete(id);

    public DumpStripSaveReport ReplaceForRegion(long regionId, string? designVersion,
                                                IReadOnlyList<DumpStrip> rows)
    {
        // 先清后写：切分参数一改，编号/幅数/带数全变，旧行留着就是两套网格叠在一张表里
        foreach (var old in ByRegion(regionId, designVersion))
            try { _repo.Delete(old.Id); } catch { }

        int n = 0, bad = 0;
        string? first = null;
        foreach (var r in rows ?? new List<DumpStrip>())
        {
            if (r == null) continue;
            r.RegionId = regionId;
            r.DesignVersion = designVersion;
            try { _repo.Insert(r); n++; }
            catch (System.Exception ex)
            {
                // 【不能再静默吞了】唯一索引挡掉的行原样消失，清单和图上却还在。
                // 位置编号撞号、带内子格没有子号，两次都是这么丢的 —— 见 DumpStripSaveReport。
                bad++;
                first ??= $"{r.Code}: {ex.Message}";
            }
        }
        return new DumpStripSaveReport(n, bad, first);
    }

    /// <summary>单引号转义 —— design_version 是用户可填的自由文本，直接拼进 WHERE 会撞语法。</summary>
    private static string Esc(string s) => s.Replace("'", "''");
}
