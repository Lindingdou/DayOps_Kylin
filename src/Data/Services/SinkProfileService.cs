// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/SinkProfileService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// <see cref="ISinkProfileService"/> 实现:通用仓储 CRUD。
/// 读路径吞异常(老库没跑过 V035、表被人删了,台账仍要能打开);写路径不吞(保存失败必须报出去)。
/// </summary>
internal sealed class SinkProfileService : ISinkProfileService
{
    private readonly ISqlService _sql;
    private readonly IRepository<SinkProfile> _repo;
    private readonly IRepository<SinkStocktake> _takes;

    public SinkProfileService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<SinkProfile>();
        _takes = sql.Repository<SinkStocktake>();
    }

    public IReadOnlyList<SinkProfile> All()
    {
        try { return _repo.All(); }
        catch { return Array.Empty<SinkProfile>(); }
    }

    public SinkProfile? Get(string sinkId)
    {
        if (string.IsNullOrWhiteSpace(sinkId)) return null;
        try { return _repo.GetByKey(sinkId); }
        catch { return null; }
    }

    public void Upsert(SinkProfile entity)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.SinkId))
            throw new ArgumentException("去向档案缺少 sink_id,无法保存", nameof(entity));
        _repo.Upsert(entity);
    }

    public void Delete(string sinkId)
    {
        if (string.IsNullOrWhiteSpace(sinkId)) return;
        _repo.Delete(sinkId);
    }

    public long AddStocktake(SinkStocktake entity)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.SinkId))
            throw new ArgumentException("盘点流水缺少 sink_id,无法记账", nameof(entity));
        return _takes.Insert(entity);
    }

    public IReadOnlyList<SinkStocktake> StocktakesOf(string sinkId, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(sinkId)) return Array.Empty<SinkStocktake>();
        try
        {
            var rows = _sql.Query<SinkStocktake>(
                "SELECT * FROM sink_stocktake WHERE sink_id = @s ORDER BY id DESC LIMIT @n",
                new { s = sinkId, n = limit <= 0 ? 50 : limit });
            return new List<SinkStocktake>(rows);
        }
        catch { return Array.Empty<SinkStocktake>(); }
    }
}
